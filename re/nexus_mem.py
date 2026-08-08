#!/usr/bin/env python
"""
nexus_mem.py -- LOCATE the live client's own game-state structures in memory, so the
bot can READ ground truth (every entity's id/x/y/sprite, self position, the map's
collision grid) instead of reconstructing a lossy world model from the packet stream.

Strategy = known-value scan, bootstrapped by the wire we already decode:
  1. Hook the decrypt routine (same as nexus_agent) to learn, for free, the CURRENT
     live entities: eid -> (x, y, look). This is our ground truth.
  2. Memory.scan the process's writable heap for a live eid (as u32 LE). The raw packet
     buffer stores ids BIG-endian, so an LE scan is already biased toward the client's
     native in-memory structs and away from transient packet copies.
  3. For each hit, read a window around it and find the byte offsets where the entity's
     known x and y appear (u16 LE). Offsets that repeat across MULTIPLE entities = the
     per-entity struct layout.
  4. CONFIRM: watch those bytes update when the mob walks (wire 0x0c changes x,y and the
     struct must follow). That distinguishes the real entity record from any stale copy.

Run:  python re/nexus_mem.py --discover [--seconds 8]
"""
import os, sys, time, json, threading, collections
import frida
import nexus_agent as NA

MOD, DEC_RVA = NA.MOD, NA.DEC_RVA
be = NA.be

JS = r"""
'use strict';
const MAIN = (Process.findModuleByName && Process.findModuleByName('__MOD__')) || Process.enumerateModules()[0];
const BASE = MAIN.base;
send({t:'mod', name:MAIN.name, base:BASE.toString(), size:MAIN.size});

// decrypt hook: forward just the world-state opcodes so Python keeps live ground truth
Interceptor.attach(BASE.add(__RVA__), {
  onEnter(args){ this.out = args[2]; },
  onLeave(ret){
    try{
      let n = ret.toInt32(); if(n<=0) return; if(n>2048) n=2048;
      const b = new Uint8Array(this.out.readByteArray(n));
      const op = b[0];
      if(op===0x07||op===0x0c||op===0x0e||op===0x0b||op===0x04){
        send({t:'pkt', ts:Date.now(), op:op,
              hex:Array.from(b).map(x=>('0'+x.toString(16)).slice(-2)).join(' ')});
      }
    }catch(e){}
  }
});

// SEND side: forward outgoing STEP(0x06)/TURN(0x11) so Python can count how many tiles
// we actually moved (self-walk isn't echoed by the server, so this is the only signal).
function scanOut(ptr, n){
  try{ let o=0;
    while(o+4<=n){
      if(ptr.add(o).readU8()===0xAA){
        const len=(ptr.add(o+1).readU8()<<8)|ptr.add(o+2).readU8();
        if(len<4||len>4096){o++;continue;}
        const op=ptr.add(o+3).readU8();
        if(op===0x06||op===0x11) send({t:'out',ts:Date.now(),op:op});
        o+=1+len;
      } else o++;
    }
  }catch(e){}
}
(function(){
  function hk(mod,name){ let a=null; try{const m=Process.findModuleByName(mod); if(m)a=m.findExportByName(name);}catch(e){}
    if(!a){try{a=Module.findExportByName(mod,name);}catch(e){}} if(!a)return;
    if(name==='WSASend'){ Interceptor.attach(a,{onEnter(args){try{const bufs=args[1],cnt=args[2].toInt32();
      for(let i=0;i<cnt;i++){const wb=bufs.add(i*8); scanOut(wb.add(4).readPointer(), wb.readU32());}}catch(e){}}});}
    else Interceptor.attach(a,{onEnter(args){scanOut(args[1],args[2].toInt32());}});
  }
  hk('ws2_32.dll','WSASend'); hk('ws2_32.dll','send');
})();

function u32le(v){ v=v>>>0; return [v&0xff,(v>>>8)&0xff,(v>>>16)&0xff,(v>>>24)&0xff]; }
function patOf(bytes){ return bytes.map(x=>('0'+x.toString(16)).slice(-2)).join(' '); }

// --- snapshot/diff of the heap to find a value that changed by a known delta ---
var SNAP = [];   // [{base:NativePointer, size, bytes:ArrayBuffer}]
rpc.exports_extra = null;

rpc.exports = {
  // scan writable memory for a u32 LE value; return match addresses (string), capped
  scanu32: function(v, cap){
    const p = patOf(u32le(v));
    const out = [];
    let ranges;
    try{ ranges = Process.enumerateRanges('rw-'); }catch(e){ return out; }
    for (const r of ranges){
      let ms;
      try{ ms = Memory.scanSync(r.base, r.size, p); }catch(e){ continue; }
      for (const m of ms){ out.push(m.address.toString()); if(out.length>=cap) return out; }
    }
    return out;
  },
  readhex: function(addr, n){
    try{ const b=new Uint8Array(ptr(addr).readByteArray(n));
         return Array.from(b).map(x=>('0'+x.toString(16)).slice(-2)).join(' '); }
    catch(e){ return ''; }
  },
  ru32: function(addr){ try{ return ptr(addr).readU32(); }catch(e){ return null; } },
  ru16: function(addr){ try{ return ptr(addr).readU16(); }catch(e){ return null; } },
  ru8: function(addr){ try{ return ptr(addr).readU8(); }catch(e){ return null; } },

  // enclosing memory range {base,size,prot} of an address -- big allocations flag a map/tile array
  rangeof: function(addr){
    try{ const r = Process.findRangeByAddress(ptr(addr));
         return r ? {base:r.base.toString(), size:r.size, prot:r.protection} : null; }
    catch(e){ return null; }
  },
  // all rw ranges at least minKB in size -- the map/tile array is a big contiguous allocation
  bigranges: function(minKB){
    const out = []; let rs; try{ rs = Process.enumerateRanges('rw-'); }catch(e){ return out; }
    for (const r of rs){ if (r.size >= minKB*1024) out.push({base:r.base.toString(), size:r.size}); }
    return out;
  },
  rangesbetween: function(minKB, maxKB){
    const out = []; let rs; try{ rs = Process.enumerateRanges('rw-'); }catch(e){ return out; }
    for (const r of rs){ if (r.size >= minKB*1024 && r.size <= maxKB*1024)
      out.push({base:r.base.toString(), size:r.size}); }
    return out;
  },
  // raw bytes of a region as hex (for offline correlation); capped for transfer sanity
  readregion: function(base, size){
    try{ if (size > 2*1024*1024) size = 2*1024*1024;
      const b = new Uint8Array(ptr(base).readByteArray(size));
      return Array.from(b).map(x=>('0'+x.toString(16)).slice(-2)).join('');
    }catch(e){ return ''; }
  },
  // read a strided byte column: value at base + (i*stride) for i in [0,count) -- for probing
  // whether a candidate array separates known-walkable from known-blocked tiles
  readstride: function(base, stride, count, width){
    const out = [];
    try{ for (let i=0;i<count;i++){ out.push(ptr(base).add(i*stride).readU8()); } }catch(e){}
    return out;
  },

  // capture private, committed, writable heap ranges (bounded) for a before/after diff
  snap: function(loB, hiB, capMB){
    SNAP = [];
    let total = 0; const cap = capMB * 1024 * 1024;
    let ranges;
    try{ ranges = Process.enumerateRanges('rw-'); }catch(e){ return 0; }
    for (const r of ranges){
      const b = parseInt(r.base.toString(), 16);
      if (b < loB || b > hiB) continue;
      if (r.size > 16*1024*1024) continue;                // skip huge maps -> stay light
      if (total + r.size > cap) break;
      try{ SNAP.push({base:r.base, size:r.size, bytes:r.base.readByteArray(r.size)}); total += r.size; }
      catch(e){}
    }
    return total;
  },
  // find u32 y-fields that changed by dy AND whose x-field (y_addr+4) changed by dx
  diff: function(dx, dy){
    const out = [];
    for (const s of SNAP){
      let cur;
      try{ cur = new Uint8Array(s.base.readByteArray(s.size)); }catch(e){ continue; }
      const old = new Uint8Array(s.bytes);
      const n = Math.min(cur.length, old.length) - 8;
      for (let o = 0; o + 8 <= n; o += 4){
        const oy = old[o]|(old[o+1]<<8)|(old[o+2]<<16)|(old[o+3]<<24);
        const cy = cur[o]|(cur[o+1]<<8)|(cur[o+2]<<16)|(cur[o+3]<<24);
        if ((cy - oy) !== dy) continue;
        if (oy < 0 || oy > 4096 || cy < 0 || cy > 4096) continue;   // tile coords only
        const ox = old[o+4]|(old[o+5]<<8)|(old[o+6]<<16)|(old[o+7]<<24);
        const cx = cur[o+4]|(cur[o+5]<<8)|(cur[o+6]<<16)|(cur[o+7]<<24);
        if ((cx - ox) !== dx) continue;
        if (ox < 0 || ox > 4096) continue;
        out.push({addr: s.base.add(o).toString(), oy:oy, cy:cy, ox:ox, cx:cx});
        if (out.length > 200) return out;
      }
    }
    return out;
  },
  // assumption-light: every address whose u16 changed by exactly `delta`, both values in
  // tile range [0,4096]. Catches u16 fields and the low half of u32 fields alike.
  diffval: function(delta){
    const out = [];
    for (const s of SNAP){
      let cur; try{ cur = new Uint8Array(s.base.readByteArray(s.size)); }catch(e){ continue; }
      const old = new Uint8Array(s.bytes);
      const n = Math.min(cur.length, old.length) - 2;
      for (let o = 0; o + 2 <= n; o++){
        const ov = old[o]|(old[o+1]<<8);
        const cv = cur[o]|(cur[o+1]<<8);
        if ((cv - ov) === delta && ov <= 4096 && cv <= 4096 && cv >= 0){
          out.push(s.base.add(o).toString());
          if (out.length > 4000) return out;
        }
      }
    }
    return out;
  }
};
""".replace("__MOD__", MOD).replace("__RVA__", hex(DEC_RVA))


class Wire:
    """Minimal live world model from the wire, used only as ground truth for the scan."""
    def __init__(self):
        self.lock = threading.Lock()
        self.ent = {}          # eid -> {x,y,look,ts,moves}
        self.self_xy = None
        self.base = None
        self.steps = 0         # outgoing 0x06 STEP count (self-walk, not server-echoed)
        self.turns = 0

    def on_out(self, p):
        with self.lock:
            if p.get("op") == 0x06:
                self.steps += 1
            elif p.get("op") == 0x11:
                self.turns += 1

    def on(self, p):
        op = p["op"]
        d = bytes(int(x, 16) for x in p["hex"].split())
        ts = p["ts"]
        with self.lock:
            if op == 0x07 and len(d) >= 3:
                cnt = be(d, 1, 2)
                for i in range(cnt):
                    o = 3 + 15 * i
                    if o + 11 > len(d):
                        break
                    x, y = be(d, o, 2), be(d, o + 2, 2)
                    eid = be(d, o + 5, 4)
                    look = be(d, o + 9, 2) & 0x7fff
                    self.ent[eid] = {"x": x, "y": y, "look": look, "ts": ts,
                                     "moves": self.ent.get(eid, {}).get("moves", 0)}
            elif op == 0x0c and len(d) >= 10:
                eid = be(d, 1, 4)
                x, y = be(d, 5, 2), be(d, 7, 2)
                e = self.ent.get(eid, {"look": None, "moves": 0})
                e.update({"x": x, "y": y, "ts": ts, "moves": e.get("moves", 0) + 1})
                self.ent[eid] = e
            elif op == 0x0e and len(d) >= 5:
                self.ent.pop(be(d, 1, 4), None)
            elif op == 0x0b and len(d) >= 6 and d[1] == 0x04:
                self.self_xy = (be(d, 2, 2), be(d, 4, 2))

    def snapshot(self):
        with self.lock:
            return {k: dict(v) for k, v in self.ent.items()}, self.self_xy


def find_pos_offsets(win_hex, base_off, x, y):
    """In a hex window, byte offsets (relative to base_off) where u16 LE == x and == y."""
    b = [int(t, 16) for t in win_hex.split()]
    xs, ys = [], []
    for i in range(len(b) - 1):
        v = b[i] | (b[i + 1] << 8)
        if v == x:
            xs.append(i - base_off)
        if v == y:
            ys.append(i - base_off)
    return xs, ys


def discover(seconds=8):
    wire = Wire()
    modinfo = {}
    dev = frida.get_local_device()
    pids = [pr.pid for pr in dev.enumerate_processes() if pr.name.lower() == MOD.lower()]
    if not pids:
        print(f"No {MOD} process. Start the client and log in first.")
        return
    sess = dev.attach(pids[0])
    script = sess.create_script(JS)

    def on_message(msg, data):
        if msg.get("type") != "send":
            return
        p = msg["payload"]
        if p.get("t") == "mod":
            modinfo.update(p)
        elif p.get("t") == "pkt":
            wire.on(p)

    script.on("message", on_message)
    script.load()
    ex = script.exports_sync
    print(f"attached pid {pids[0]}; collecting live entities for {seconds}s ...")
    time.sleep(seconds)

    ents, self_xy = wire.snapshot()
    if modinfo:
        print(f"module {modinfo.get('name')} base={modinfo.get('base')} size={modinfo.get('size')}")
    print(f"self_xy(from wire)={self_xy}   live entities={len(ents)}")

    # pick a few well-tracked entities with distinctive ids to scan for
    cand = sorted(ents.items(), key=lambda kv: -kv[1]["moves"])
    cand = [(eid, e) for eid, e in cand if eid > 0x1000][:6]
    if not cand:
        print("no scan-worthy entities (need eids > 0x1000). Move near mobs and retry.")
        return
    print("scan targets (eid, x, y, look, moves):")
    for eid, e in cand:
        print(f"  {eid:#010x}  ({e['x']},{e['y']})  look={e['look']}  moves={e['moves']}")

    # ---- locate each entity's id in heap, record where x/y sit relative to it ----
    layouts = collections.Counter()   # (xoff, yoff) -> how many entities agree
    hits_by_eid = {}
    for eid, e in cand:
        addrs = ex.scanu32(eid, 64)
        keep = []
        for a in addrs:
            win = ex.readhex(hex_sub(a, 16), 96)   # 16 before id .. 80 after
            if not win:
                continue
            xs, ys = find_pos_offsets(win, 16, e["x"], e["y"])
            # pair every x-offset with every y-offset seen in the same record
            for xo in xs:
                for yo in ys:
                    if -8 <= xo <= 80 and -8 <= yo <= 80:
                        layouts[(xo, yo)] += 1
            if xs and ys:
                keep.append((a, xs, ys, win))
        hits_by_eid[eid] = keep
        print(f"  eid {eid:#010x}: {len(addrs)} raw id hits, {len(keep)} with x&y nearby")

    if not layouts:
        print("\nNo consistent (x,y) offset found next to any entity id.")
        print("Positions may be stored as u32, or scaled, or the id isn't u32-LE. "
              "Dumping a sample window for manual inspection:")
        for eid, keep in hits_by_eid.items():
            addrs = ex.scanu32(eid, 4)
            for a in addrs[:2]:
                print(f"  {eid:#010x} @ {a}: {ex.readhex(hex_sub(a,16),96)}")
        return

    print("\ncandidate (x_off, y_off) relative to id, by #entities agreeing:")
    for (xo, yo), c in layouts.most_common(8):
        print(f"  x@{xo:+d}  y@{yo:+d}   agree={c}")

    (xo, yo), agree = layouts.most_common(1)[0]
    print(f"\nBEST layout: id@0, x@{xo:+d}, y@{yo:+d} (agreed by {agree} entities).")

    # ---- CONFIRM by watching the struct follow a live walk ----
    print("confirming: watching a mob walk and checking the struct updates in lockstep ...")
    target = None
    for eid, keep in hits_by_eid.items():
        for a, xs, ys, win in keep:
            if xo in xs and yo in ys:
                target = (eid, a)
                break
        if target:
            break
    if not target:
        print("  (could not select a single struct address to confirm; layout still reported)")
        return
    eid, addr = target
    id_addr = addr           # scanu32 returned the address of the id field itself
    for _ in range(20):
        mem_x = ex.ru16(hex_add(id_addr, xo))
        mem_y = ex.ru16(hex_add(id_addr, yo))
        ents2, _ = wire.snapshot()
        wx = ents2.get(eid, {}).get("x")
        wy = ents2.get(eid, {}).get("y")
        match = (mem_x == wx and mem_y == wy)
        print(f"  eid {eid:#010x} @ {id_addr}: mem=({mem_x},{mem_y}) wire=({wx},{wy}) "
              f"{'OK' if match else 'DIFF'}")
        time.sleep(0.5)

    print("\nRESULT: entity struct id@0 x@%+d y@%+d. Next: map the sprite/look + hp "
          "fields in the same record, then find the entity-array base/stride." % (xo, yo))


def hex_sub(addr_str, n):
    return hex(int(addr_str, 16) - n)


def hex_add(addr_str, n):
    return hex(int(addr_str, 16) + n)


def _u32(hexwin, off):
    b = [int(t, 16) for t in hexwin.split()]
    if off + 4 > len(b):
        return None
    return b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24)


def _utf16(hexwin, off, maxch=24):
    b = [int(t, 16) for t in hexwin.split()]
    out = []
    i = off
    while i + 1 < len(b) and len(out) < maxch:
        c = b[i] | (b[i + 1] << 8)
        if c == 0:
            break
        if 32 <= c < 127:
            out.append(chr(c))
        else:
            return "".join(out)  # non-ASCII wide char -> stop (name is ASCII here)
        i += 2
    return "".join(out)


# hypothesised full-entity-object layout, relative to the eid field (id@0):
Y_OFF, X_OFF, PTR_OFF, NAME_OFF = -16, -12, -8, +4


def looks_like_struct(ex, id_addr, eid):
    """Read a window around an id-field address and test the full-object hypothesis.
    Returns dict(x,y,name,ptr) if the eid matches and x/y are plausible tile coords."""
    win = ex.readhex(hex_sub(id_addr, 24), 96)
    if not win:
        return None
    base = 24  # window starts 24 before the id
    if _u32(win, base) != eid:
        return None
    x = _u32(win, base + X_OFF)
    y = _u32(win, base + Y_OFF)
    if x is None or y is None or not (0 <= x < 4096 and 0 <= y < 4096):
        return None
    return {"x": x, "y": y,
            "name": _utf16(win, base + NAME_OFF),
            "ptr": _u32(win, base + PTR_OFF)}


def probe(seconds=6):
    """Stage 2: confirm the entity-object layout + the registry array, live."""
    wire = Wire()
    dev = frida.get_local_device()
    pids = [pr.pid for pr in dev.enumerate_processes() if pr.name.lower() == MOD.lower()]
    if not pids:
        print(f"No {MOD} process. Start the client and log in first.")
        return
    sess = dev.attach(pids[0])
    script = sess.create_script(JS)
    script.on("message", lambda m, d: wire.on(m["payload"])
              if m.get("type") == "send" and m["payload"].get("t") == "pkt" else None)
    script.load()
    ex = script.exports_sync
    print(f"attached pid {pids[0]}; collecting {seconds}s ...")
    time.sleep(seconds)
    ents, _ = wire.snapshot()
    cand = [(eid, e) for eid, e in ents.items() if eid > 0x1000]
    if not cand:
        print("no entities to probe; move near mobs.")
        return

    # ---- A. confirm the full-object layout; test ALL hits, keep position-matching ----
    print("\n== A. full entity objects (scan eid -> id-12=x, id-16=y, id+4=name) ==")
    confirmed = []
    for eid, e in cand[:8]:
        found = None
        for a in ex.scanu32(eid, 160):
            s = looks_like_struct(ex, a, eid)
            if s and abs(s["x"] - e["x"]) <= 2 and abs(s["y"] - e["y"]) <= 2:
                found = (a, s)
                break
        if found:
            a, s = found
            raw = ex.readhex(hex_sub(a, 24), 56)
            print(f"  eid {eid:#08x} @ {a}: mem=({s['x']},{s['y']}) wire=({e['x']},{e['y']}) "
                  f"look={e['look']} name={s['name']!r}")
            print(f"        raw[id-24..id+32]: {raw}")
            confirmed.append((eid, a, s, e))
        else:
            print(f"  eid {eid:#08x} wire=({e['x']},{e['y']}) look={e['look']}: "
                  f"no position-matching object found")
    print(f"  -> {len(confirmed)}/{len(cand[:8])} entities confirmed. "
          f"mob NAME in memory {'WORKS' if any(c[2]['name'] for c in confirmed) else 'not seen'}.")

    # ---- B. find + walk the registry array: eid-sorted, stride 12, eid@rec+4 ----
    print("\n== B. registry array (eid-sorted, stride 12, eid@rec+4) ==")
    arr_rec = find_registry(ex, [e for e, _ in cand])
    if arr_rec is None:
        print("  registry array not located this run.")
    else:
        eid_here = ex.ru32(hex(arr_rec + 4))
        print(f"  found: record @ {hex(arr_rec)} holds eid {eid_here:#08x}")
        _dump_array(ex, arr_rec, wire)

    print("\nDone. If A shows OK rows, entity perception is a solved read: "
          "for each eid, x=*(id-12), y=*(id-16), name=id+4 (UTF-16).")


def find_registry(ex, eids, stride=12):
    """Locate the entity registry array by its structural invariant: records are
    stride-12, eid at record+4, and the array is sorted by eid ASCENDING with small
    gaps. For a hit at address a (holding a known eid), a is the eid field iff the
    eids at a-12, a, a+12, a+24 are strictly ascending with plausible gaps."""
    def rd(addr):
        v = ex.ru32(hex(addr))
        return v if v else 0

    def score(a):
        """How array-like is the stride-12 window centred on eid-field address a?
        Count records (rec = a + k*stride - 4) where rec+0 is a heap ptr and the eid
        at rec+4 is plausible and strictly ascending vs the previous kept eid."""
        good, last = 0, 0
        for k in range(-10, 11):
            e = rd(a + k * stride)
            p = rd(a + k * stride - 4)
            if 0x1000 < e < 0x40000 and 0x10000 < p < 0x7fffffff and e > last:
                good += 1
                last = e
            elif e and e <= last:
                last = e  # tolerate an out-of-order blip, keep scanning
        return good

    best = (0, None)
    for eid in eids:
        for a_s in ex.scanu32(eid, 256):
            a = int(a_s, 16)
            sc = score(a)
            if sc > best[0]:
                best = (sc, a)
                if sc >= 18:
                    return a - 4        # unmistakably the array
    if best[1] is not None and best[0] >= 8:
        print(f"  [diag] best array score={best[0]}/21 @ {hex(best[1])}")
        return best[1] - 4
    if best[1] is not None:
        print(f"  [diag] weak best score={best[0]}/21 @ {hex(best[1])}")
    return None


def _struct_at_ptr(ex, p, eid):
    win = ex.readhex(hex(p), 64)
    if not win:
        return False
    b = [int(t, 16) for t in win.split()]
    tgt = [eid & 0xff, (eid >> 8) & 0xff, (eid >> 16) & 0xff, (eid >> 24) & 0xff]
    for i in range(len(b) - 3):
        if b[i:i + 4] == tgt:
            return True
    return False


def _dump_array(ex, rec_addr, wire, stride=12, span=10):
    ents, _ = wire.snapshot()
    print("  walking the array (ptr, eid, u16, u16serial) and following ptr->object:")
    for k in range(-2, span):
        a = rec_addr + k * stride
        win = ex.readhex(hex(a), stride)
        if not win:
            continue
        ptr = _u32(win, 0)
        eid = _u32(win, 4)
        f8 = _u32(win, 8)  # (u16,u16) packed
        if not eid or eid < 0x1000 or eid > 0x7fffffff:
            continue
        w = ents.get(eid)
        print(f"    [{k:+d}] {hex(a)}: ptr={ptr:#010x} eid={eid:#08x} f8={f8:#010x}"
              f"{'  wire=(%d,%d)' % (w['x'], w['y']) if w else ''}")
        if ptr and 0x10000 < ptr < 0x7fffffff:
            raw = ex.readhex(hex(ptr), 48)
            print(f"          obj@{ptr:#010x}: {raw}")


def _read_obj(ex, ptr, eid):
    """Given a pointer that should reference a full entity object, find the eid inside
    it and decode x/y/name relative to that eid field."""
    win = ex.readhex(hex(ptr), 80)
    if not win:
        return None
    b = [int(t, 16) for t in win.split()]
    tgt = [eid & 0xff, (eid >> 8) & 0xff, (eid >> 16) & 0xff, (eid >> 24) & 0xff]
    for i in range(24, len(b) - 3):
        if b[i:i + 4] == tgt:
            id_addr = ptr + i
            return looks_like_struct(ex, hex(id_addr), eid)
    return None


def find_self(seconds=5):
    """Identify the LOCAL player's entity object and prove we can read our own position
    from memory: find named entity objects, inject a few steps, and see which object's
    (x,y) moves in lockstep with our input. That object is SELF; its address gives exact
    self-position every frame (no more dead reckoning)."""
    from bot_input_test import find_windows, post_key, VK
    wins = find_windows()
    if not wins:
        print("client window not found (need it focused for injected steps to register).")
        return
    hwnd = wins[0][0]

    wire = Wire()
    dev = frida.get_local_device()
    pids = [pr.pid for pr in dev.enumerate_processes() if pr.name.lower() == MOD.lower()]
    sess = dev.attach(pids[0])
    script = sess.create_script(JS)
    script.on("message", lambda m, d: wire.on(m["payload"])
              if m.get("type") == "send" and m["payload"].get("t") == "pkt" else None)
    script.load()
    ex = script.exports_sync
    print(f"attached pid {pids[0]}; collecting {seconds}s ...")
    time.sleep(seconds)
    ents, _ = wire.snapshot()

    # collect candidate NAMED player objects (template hits with a non-empty ASCII name)
    named = {}   # id_addr -> (eid, name, x, y)
    for eid, e in list(ents.items()):
        if eid <= 0x1000:
            continue
        for a in ex.scanu32(eid, 96):
            s = looks_like_struct(ex, a, eid)
            if s and s["name"] and (abs(s["x"] - e["x"]) <= 2 and abs(s["y"] - e["y"]) <= 2):
                named[a] = (eid, s["name"], s["x"], s["y"])
                break
    if not named:
        print("no named+position-matching objects; is a player (you) on screen?")
        return
    print("named player-object candidates:")
    for a, (eid, nm, x, y) in named.items():
        print(f"  @ {a} eid={eid:#08x} name={nm!r} pos=({x},{y})")

    def read_xy(a):
        return (ex.ru32(hex_add(a, X_OFF)), ex.ru32(hex_add(a, Y_OFF)))

    before = {a: read_xy(a) for a in named}
    print("\ninjecting steps (down x2, right x2) and watching which object follows ...")
    for name in ("down", "down", "right", "right"):
        post_key(hwnd, VK[name], 0.09)
        time.sleep(0.35)
    time.sleep(0.4)
    after = {a: read_xy(a) for a in named}

    print("object position deltas after injected movement:")
    me = None
    for a in named:
        bx, by = before[a]
        ax, ay = after[a]
        d = (ax - bx if ax and bx else 0, ay - by if ay and by else 0)
        moved = d != (0, 0)
        print(f"  @ {a} name={named[a][1]!r}: {before[a]} -> {after[a]}  delta={d}"
              f"{'  <== MOVED WITH INPUT (SELF)' if moved else ''}")
        if moved:
            me = a
    if me:
        eid = named[me][0]
        print(f"\nSELF FOUND: object @ {me} eid={eid:#08x} name={named[me][1]!r}.")
        print("Now searching for a STABLE global pointer to it (survives across frames) ...")
        find_self_pointer(ex, me, eid)
    else:
        print("\nno object moved with input — try running with the client FOREGROUNDED, or "
              "self-position may be held only in a separate camera/player struct.")


def find_self_pointer(ex, id_addr, eid, obj_base_guess=16):
    """Find a global/heap slot that holds a pointer to the self object, so the bot can
    deref it every frame instead of re-scanning. Try several plausible object-base
    offsets (the object likely starts a few fields before the id)."""
    obj = int(id_addr, 16)
    for base_off in (0, 4, 8, 12, 16, 20, 24):
        target = obj - base_off
        hits = ex.scanu32(target, 32)
        if hits:
            print(f"  pointer(s) to obj_base=id-{base_off} ({hex(target)}): "
                  f"{hits[:8]}{' ...' if len(hits) > 8 else ''}")
            # a pointer inside the low static data segment (< 0x00a00000) is the stable global
            statics = [h for h in hits if int(h, 16) < 0x00a00000]
            if statics:
                print(f"    -> STABLE candidate global slot(s): {statics}")
    print("  (deref any stable slot -> object; read x@-12,y@-16 each frame.)")


def self2():
    """Find the LOCAL player object with NO known eid/anchor: snapshot heap, inject a
    known number of steps (counted from the outgoing 0x06 stream), and diff for the u32
    (y) that changed by exactly that many tiles with its x-neighbour behaving correctly.
    Confirm with a perpendicular move. Result = a stable read of self (x,y)."""
    from bot_input_test import find_windows, post_key, VK
    wins = find_windows()
    if not wins:
        print("client window not found.")
        return
    hwnd = wins[0][0]
    wire = Wire()
    dev = frida.get_local_device()
    pids = [pr.pid for pr in dev.enumerate_processes() if pr.name.lower() == MOD.lower()]
    sess = dev.attach(pids[0])
    script = sess.create_script(JS)

    def onmsg(m, d):
        if m.get("type") != "send":
            return
        p = m["payload"]
        if p.get("t") == "pkt":
            wire.on(p)
        elif p.get("t") == "out":
            wire.on_out(p)
    script.on("message", onmsg)
    script.load()
    ex = script.exports_sync

    def push(direction, presses):
        with wire.lock:
            wire.steps = 0
        for _ in range(presses):
            post_key(hwnd, VK[direction], 0.09)
            time.sleep(0.16)
        time.sleep(0.3)
        with wire.lock:
            return wire.steps

    LO, HI, CAP = 0x00100000, 0x40000000, 96
    SIGN = {"down": +1, "up": -1, "right": +1, "left": -1}

    def track_axis(plan):
        """Intersect the 'changed-by-exactly-step-count' address sets across moves that
        go OPPOSITE ways. A real coordinate matches +s then -s; a counter cannot."""
        inter = None
        for d, presses in plan:
            ex.snap(LO, HI, CAP)
            s = push(d, presses)
            if s == 0:
                print(f"    {d}: blocked (0 steps)")
                continue
            hits = set(ex.diffval(s * SIGN[d]))
            inter = hits if inter is None else (inter & hits)
            print(f"    {d} {s} steps (delta {s*SIGN[d]:+d}) -> {len(hits)} changed; "
                  f"intersection now {len(inter)}")
        return inter or set()

    print("locating X field (right then left):")
    xset = track_axis([("right", 12), ("left", 7), ("right", 5)])
    print("locating Y field (down then up):")
    yset = track_axis([("down", 12), ("up", 7), ("down", 5)])

    xaddrs = sorted(int(a, 16) for a in xset)
    yaddrs = sorted(int(a, 16) for a in yset)
    print(f"\nX-field survivors: {[hex(a) for a in xaddrs]}")
    print(f"Y-field survivors: {[hex(a) for a in yaddrs]}")

    # in every struct we've seen, y sits 4 bytes after x. Confirm that pairing live.
    print("\nself-position struct candidates (x@A, y@A+4):")
    selfs = []
    for xa in xaddrs:
        x = ex.ru32(hex(xa))
        y = ex.ru32(hex(xa + 4))
        y_tracked = (xa + 4) in set(yaddrs)
        ctx = ex.readhex(hex(xa - 16), 48)
        print(f"  @ {hex(xa)}: x={x} y={y} y_field_tracked={y_tracked}  ctx: {ctx}")
        if 0 <= x < 4096 and 0 <= y < 4096:
            selfs.append(xa)

    if not selfs:
        print("no clean (x,y) struct; dumping Y contexts too:")
        for ya in yaddrs:
            print(f"  Y {hex(ya)}: {ex.readhex(hex(ya-16), 48)}")
        return

    # final live confirmation: read all candidates, take a step, read again
    print("\nfinal check — reading candidates, stepping right 2, re-reading:")
    before = {a: (ex.ru32(hex(a)), ex.ru32(hex(a + 4))) for a in selfs}
    push("right", 4)
    for a in selfs:
        bx, by = before[a]
        ax, ay = ex.ru32(hex(a)), ex.ru32(hex(a + 4))
        print(f"  @ {hex(a)}: ({bx},{by}) -> ({ax},{ay})  "
              f"{'X TRACKS' if ax != bx else 'static'}")
    print(f"\nSELF POSITION SOLVED. Read u32@A = x, u32@A+4 = y. Canonical addr(s): "
          f"{[hex(a) for a in selfs]}. Re-locate at each map change (heap addrs move).")

    # ---- hunt a STABLE global pointer to this struct (module static data, no ASLR) ----
    print("\nsearching for a stable pointer chain to the self struct ...")
    xa = selfs[0]
    for base_off in range(0, 65, 4):
        struct_base = xa - base_off
        holders = ex.scanu32(struct_base, 40)
        if not holders:
            continue
        statics = [h for h in holders if int(h, 16) < 0x00a00000]
        if statics:
            print(f"  x@struct+{base_off}: pointer(s) to {hex(struct_base)} in STATIC seg: "
                  f"{statics}  (x = *[{statics[0]}] + {base_off}; y = +{base_off+4})")
        heaps = [h for h in holders if int(h, 16) >= 0x00a00000]
        if heaps and not statics:
            # one hop out: is a static global pointing at THIS heap holder?
            for h in heaps[:6]:
                st2 = [s for s in ex.scanu32(int(h, 16), 20) if int(s, 16) < 0x00a00000]
                if st2:
                    print(f"  2-hop: static {st2[0]} -> {h} -> struct+{base_off} ({hex(struct_base)})")
                    break
    print("  (if a STATIC slot printed, that's a permanent anchor — no wiggle needed.)")


def xset_set(xset):
    if not hasattr(xset_set, "_c") or xset_set._src is not xset:
        xset_set._c = set(int(a, 16) for a in xset)
        xset_set._src = xset
    return xset_set._c


def _attach_wire():
    wire = Wire()
    dev = frida.get_local_device()
    pids = [pr.pid for pr in dev.enumerate_processes() if pr.name.lower() == MOD.lower()]
    if not pids:
        return None, None, None
    sess = dev.attach(pids[0])
    script = sess.create_script(JS)

    def onmsg(m, d):
        if m.get("type") != "send":
            return
        p = m["payload"]
        if p.get("t") == "pkt":
            wire.on(p)
        elif p.get("t") == "out":
            wire.on_out(p)
    script.on("message", onmsg)
    script.load()
    return wire, script, script.exports_sync


def _locate_self(ex, wire, hwnd):
    """Locate the self struct (x@A, y@A+4) via opposite-move correlation. Returns A or None."""
    from bot_input_test import post_key, VK
    LO, HI, CAP = 0x00100000, 0x40000000, 96
    SIGN = {"down": +1, "up": -1, "right": +1, "left": -1}

    def push(d, presses):
        with wire.lock:
            wire.steps = 0
        for _ in range(presses):
            post_key(hwnd, VK[d], 0.09)
            time.sleep(0.16)
        time.sleep(0.30)
        with wire.lock:
            return wire.steps

    def track(pos, neg):
        inter, moves = None, 0
        for d, pr in ((pos, 8), (neg, 5), (pos, 4)):
            ex.snap(LO, HI, CAP)
            s = push(d, pr)
            if s == 0:
                continue
            h = set(ex.diffval(s * SIGN[d]))
            inter = h if inter is None else (inter & h)
            moves += 1
        return (inter or set()) if moves >= 2 else set()

    xset = set(int(a, 16) for a in track("right", "left"))
    yset = set(int(a, 16) for a in track("down", "up"))
    for xa in sorted(xset):
        x, y = ex.ru32(hex(xa)), ex.ru32(hex(xa + 4))
        if x is not None and y is not None and 0 <= x < 4096 and 0 <= y < 4096:
            return xa
    for ya in sorted(yset):
        x, y = ex.ru32(hex(ya - 4)), ex.ru32(hex(ya))
        if x is not None and y is not None and 0 <= x < 4096 and 0 <= y < 4096:
            return ya - 4
    return None


def map_discover(seconds=6):
    """Find the client's MAP/collision array in memory. Locate self, collect known-walkable
    tiles (every mob-walk + our own tile), then survey big allocations + pointers hanging off
    the player/camera structs -- the tile array is a large contiguous block, often reachable
    from a struct that also holds our position."""
    from bot_input_test import find_windows
    wins = find_windows()
    if not wins:
        print("client window not found.")
        return
    hwnd = wins[0][0]
    wire, script, ex = _attach_wire()
    if wire is None:
        print(f"No {MOD} process."); return
    print("locating self struct ...")
    self_addr = _locate_self(ex, wire, hwnd)
    if not self_addr:
        print("could not locate self (character boxed in?)."); return
    X, Y = ex.ru32(hex(self_addr)), ex.ru32(hex(self_addr + 4))
    print(f"self @ {hex(self_addr)} pos=({X},{Y})")

    # collect walkable ground truth from mob walks
    print(f"collecting walkable tiles (mob walks) for {seconds}s ...")
    time.sleep(seconds)
    ents, _ = wire.snapshot()
    walk = set((e["x"], e["y"]) for e in ents.values())
    walk.add((X, Y))
    print(f"  {len(walk)} distinct walkable tiles observed")

    # every struct that holds our (x,y) pair -- player, camera, and maybe a map cursor
    holders = []
    for a in ex.scanu32(X, 400):
        aa = int(a, 16)
        if ex.ru32(hex(aa + 4)) == Y:
            holders.append(aa)
    print(f"\n{len(holders)} struct(s) hold our (x,y) pair:")
    for h in holders:
        r = ex.rangeof(hex(h))
        sz = f"{r['size']//1024}KB" if r else "?"
        print(f"  @ {hex(h)} in range base={r['base'] if r else '?'} size={sz}")

    # pointers hanging off those structs -> big regions = tile-array candidates
    print("\nbig regions (>=32KB) reachable from pointers near the (x,y) structs:")
    seen = set()
    for h in holders:
        win = ex.readhex(hex(h - 64), 192)
        b = [int(t, 16) for t in win.split()]
        for off in range(0, len(b) - 3):
            p = b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24)
            if 0x100000 < p < 0x7fffffff and p not in seen:
                seen.add(p)
                r = ex.rangeof(hex(p))
                if r and r["size"] >= 0x8000:
                    print(f"  ptr@{hex(h - 64 + off)}={hex(p)} -> base={r['base']} "
                          f"size={r['size']//1024}KB {r['prot']}  peek:{ex.readhex(hex(p), 24)}")

    # also just list ALL big rw allocations as raw candidates
    print("\nall big rw allocations (candidate tile arrays):")
    for r in sorted(ex.bigranges(64), key=lambda r: -r["size"])[:15]:
        print(f"  base={r['base']} size={r['size']//1024}KB  peek:{ex.readhex(r['base'], 24)}")
    print("\nNext: correlate the walkable set against these arrays (width x stride x cell-byte "
          "search) to pin the collision encoding.")


def _probe_labels(ex, wire, hwnd, self_addr, steps=48):
    """Actively bump-probe to label tiles: a successful step means the destination tile is
    WALKABLE, a blocked step means it's BLOCKED. Uses exact memory position for truth. Plus
    every mob-walk tile is walkable. Returns {(x,y): 1 walkable / 0 blocked}."""
    from bot_input_test import post_key, VK
    SIGN = {"up": (0, -1), "down": (0, 1), "left": (-1, 0), "right": (1, 0)}
    labels = {}

    def me():
        return (ex.ru32(hex(self_addr)), ex.ru32(hex(self_addr + 4)))

    order = ["right", "right", "down", "left", "left", "down", "right", "right", "down"]
    i = 0
    for _ in range(steps):
        d = order[i % len(order)]
        i += 1
        x0, y0 = me()
        dx, dy = SIGN[d]
        tgt = (x0 + dx, y0 + dy)
        # press up to 3x: a step confirms walkable; only 3 consecutive no-moves = real wall
        # (one no-move is often just the walk-cooldown swallowing the press, NOT a wall)
        moved = False
        for _try in range(3):
            post_key(hwnd, VK[d], 0.09)
            time.sleep(0.26)
            x1, y1 = me()
            if (x1, y1) != (x0, y0):
                moved = True
                labels[(x1, y1)] = 1             # stepped there -> definitely walkable
                break
        if not moved:
            labels[tgt] = 0                       # 3 tries, never moved -> really blocked
        if i % len(order) == 0:
            order = order[::-1]                   # reverse to cover more ground
        ents, _ = wire.snapshot()                 # fold in mob-walk tiles as we go
        for e in ents.values():
            labels.setdefault((e["x"], e["y"]), 1)
    return labels


def _sep_of(data, cfg, walk, block):
    """Separation in [0,1] of a specific cfg on a labeled set: |P(bit=1|walk) - P(bit=1|block)|.
    Returns None if any tile indexes out of the region."""
    n, W, S, cb, O, bit = len(data), cfg["W"], cfg["stride"], cfg["cell_byte"], cfg["origin"], cfg["bit"]
    def ones(tiles):
        c = 0
        for (x, y) in tiles:
            idx = O + (y * W + x) * S + cb
            if idx >= n or x < 0 or y < 0:
                return None
            c += (data[idx] >> bit) & 1
        return c / len(tiles) if tiles else 0
    w1, b1 = ones(walk), ones(block)
    if w1 is None or b1 is None:
        return None
    return abs(w1 - b1)


def correlate_map(region_hex, base, walk, block, widths=range(16, 321),
                  strides=(2, 4, 6, 8, 12), origins=(0,)):
    """Search (width, stride, cell-byte, origin, bit) for the column that best separates
    walkable from blocked tiles on the TRAINING set. Returns best (score, cfg)."""
    data = bytes.fromhex(region_hex)
    if len(walk) < 6 or len(block) < 3:
        return None
    best = None
    for stride in strides:
        for cb in range(min(stride, 8)):
            for W in widths:
                for O in origins:
                    for bit in range(8):
                        cfg = {"base": base, "W": W, "stride": stride,
                               "cell_byte": cb, "origin": O, "bit": bit}
                        sep = _sep_of(data, cfg, walk, block)
                        if sep is not None and (best is None or sep > best[0]):
                            best = (sep, cfg)
    return best


def map_fit(seconds=5):
    """Full pipeline: locate self, label tiles by bump-probing + mob walks, then correlate
    the labels against each candidate region to pin the collision array + width + encoding."""
    from bot_input_test import find_windows
    wins = find_windows()
    if not wins:
        print("client window not found."); return
    hwnd = wins[0][0]
    wire, script, ex = _attach_wire()
    if wire is None:
        print(f"No {MOD} process."); return
    print("locating self ...")
    self_addr = _locate_self(ex, wire, hwnd)
    if not self_addr:
        print("could not locate self."); return
    print(f"self @ {hex(self_addr)} pos=({ex.ru32(hex(self_addr))},{ex.ru32(hex(self_addr+4))})")
    time.sleep(seconds)
    print("bump-probing to label walkable/blocked tiles ...")
    labels = _probe_labels(ex, wire, hwnd, self_addr)
    nw = sum(1 for v in labels.values() if v == 1)
    nb = sum(1 for v in labels.values() if v == 0)
    print(f"  labeled {nw} walkable + {nb} blocked tiles")
    if nb < 3:
        print("  too few BLOCKED tiles to correlate (open area). Need the bot to bump walls; "
              "reposition near a wall/edge and retry, or use the observational occupancy map.")
        return
    # TRAIN/TEST split so a config that only fits noise is caught: fit on train, then demand
    # the SAME (base,W,stride,cell,bit) also separates the held-out test tiles.
    tiles = list(labels.items())
    walk_all = [t for t, v in tiles if v == 1]
    block_all = [t for t, v in tiles if v == 0]
    # deterministic split (no RNG available): every 3rd sample -> test
    tr_w = [t for i, t in enumerate(walk_all) if i % 3]
    te_w = [t for i, t in enumerate(walk_all) if not i % 3]
    tr_b = [t for i, t in enumerate(block_all) if i % 3]
    te_b = [t for i, t in enumerate(block_all) if not i % 3]
    print(f"train: {len(tr_w)}w/{len(tr_b)}b   test: {len(te_w)}w/{len(te_b)}b")
    if len(tr_b) < 3 or len(te_b) < 2:
        print("too few blocked tiles for a validated fit; reposition near walls and retry.")
        return

    regions = ex.rangesbetween(16, 1536)
    print(f"correlating against {len(regions)} candidate regions (train, then validate) ...")
    winners = []
    for r in regions:
        hexdata = ex.readregion(r["base"], r["size"])
        if not hexdata:
            continue
        res = correlate_map(hexdata, r["base"], tr_w, tr_b)
        if not res or res[0] < 0.85:
            continue
        data = bytes.fromhex(hexdata)
        test_sep = _sep_of(data, res[1], te_w, te_b)
        if test_sep is not None:
            winners.append((test_sep, res[0], res[1], r["size"]))
            print(f"  {r['base']} ({r['size']//1024}KB): train={res[0]:.2f} TEST={test_sep:.2f} {res[1]}")
    winners.sort(key=lambda w: w[0], reverse=True)
    if winners and winners[0][0] >= 0.9:
        ts, tr, cfg, sz = winners[0]
        print(f"\nMAP ARRAY VALIDATED: train={tr:.2f} test={ts:.2f}  {cfg}")
        print(f"blocked when (byte[base + (y*{cfg['W']} + x)*{cfg['stride']} + {cfg['cell_byte']}] "
              f">> {cfg['bit']}) & 1 == 1  (verify block_ones/walk_ones polarity).")
    elif winners:
        print(f"\nbest holdout separation {winners[0][0]:.2f} -- train fit didn't generalize "
              f"(likely a false positive from limited labels). Collect more blocked tiles.")
    else:
        print("\nno region passed the train threshold; collision may be a bitfield/object-wall "
              "layer (SObj), not a per-tile byte. Next: 1-byte stride + origin sweep.")


# current live stats read off the game HUD/`s` profile (seed for the memory scan)
KNOWN_STATS = {"maxhp": 911, "curhp": 911, "maxmana": 449, "curmana": 179,
               "level": 16, "might": 11, "grace": 17, "will": 9, "ac": 69,
               "dam": 1, "tnl": 6398}


def stats_find(known=None):
    """Find the player STATS struct: scan for the distinctive maxhp value, then score each
    hit by HOW MANY other known stats sit within a small window -> the real struct lights up
    with most of them. Annotate every field's offset so we can read them live in the bot."""
    known = known or KNOWN_STATS
    wire, script, ex = _attach_wire()
    if wire is None:
        print(f"No {MOD} process."); return
    anchor = known["maxhp"]
    print(f"scanning for the stats struct (anchor maxhp={anchor}); known={known}")
    hp_hits = [int(a, 16) for a in ex.scanu32(anchor, 600)]
    print(f"  {len(hp_hits)} maxhp hits")

    scored = []
    for a in hp_hits:
        win = ex.readhex(hex(a - 96), 224)          # ±96 bytes around the maxhp field
        if not win:
            continue
        b = [int(t, 16) for t in win.split()]
        u16 = [(b[i] | (b[i + 1] << 8)) for i in range(len(b) - 1)]
        u32 = [(_u32(win, i) or -1) for i in range(len(b) - 3)]
        present = {}
        for name, val in known.items():
            off = None
            for i in range(len(b) - 1):
                if u16[i] == val or (i < len(u32) and u32[i] == val):
                    off = i - 96
                    break
            if off is not None:
                present[name] = off
        scored.append((len(present), a, win, present))
    scored.sort(reverse=True, key=lambda s: s[0])
    if not scored or scored[0][0] < 4:
        print("  no struct with >=4 known stats clustered. Values may be stale/gear-adjusted; "
              "double-check the numbers, or some fields may be u8/packed.")
        for n, a, win, present in scored[:3]:
            print(f"    best {n} @ {hex(a)}: {present}")
        return
    a = scored[0][1]
    print(f"\n  BEST STATS STRUCT @ maxhp={hex(a)} ({scored[0][0]}/{len(known)} present)")
    # widen the search around the winner to catch might/grace/will/ac too
    W = 512
    win = ex.readhex(hex(a - W), 2 * W)
    b = [int(t, 16) for t in win.split()]
    print("    all known-stat offsets from maxhp (u16, first match):")
    for name, val in sorted(known.items(), key=lambda kv: kv[0]):
        offs = [i - W for i in range(len(b) - 1) if (b[i] | (b[i + 1] << 8)) == val]
        near = [o for o in offs if -64 <= o <= 96]        # prefer the tight cluster
        show = near[:4] if near else offs[:4]
        print(f"      {name:8}={val:<6} at maxhp{['%+d' % o for o in show]}")
    print(f"    raw[maxhp-8..+40]: {ex.readhex(hex(a - 8), 48)}")
    print("\n-> wire the tight-cluster offsets into the bot: read u16/u32 at maxhp_addr+off "
          "each tick for curhp/maxhp/curmp/maxmp/level/exp (+ might/grace/will/ac if clustered).")


if __name__ == "__main__":
    secs = 8
    if "--seconds" in sys.argv:
        secs = int(sys.argv[sys.argv.index("--seconds") + 1])
    if "--stats" in sys.argv:
        stats_find()
    elif "--mapfit" in sys.argv:
        map_fit(secs if "--seconds" in sys.argv else 5)
    elif "--map" in sys.argv:
        map_discover(secs if "--seconds" in sys.argv else 6)
    elif "--self2" in sys.argv:
        self2()
    elif "--self" in sys.argv:
        find_self(secs if "--seconds" in sys.argv else 5)
    elif "--probe" in sys.argv:
        probe(secs if "--seconds" in sys.argv else 6)
    else:
        discover(secs)
