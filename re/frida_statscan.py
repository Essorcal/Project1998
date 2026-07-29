#!/usr/bin/env python
"""
Differential memory scanner for the live client — finds the address of a DISPLAYED stat
(base + equipped item bonuses, already summed), which never appears on the wire.

Method (Cheat-Engine style): scan for a value, change it in-game, narrow to survivors.
Heap addresses stay valid for the process lifetime, so this works across separate calls
while you toggle gear between steps.

  python re/frida_statscan.py scan 5          # first pass: all writable mem == 5
  # equip Swift Sword -> Grace 6
  python re/frida_statscan.py narrow 6         # keep addrs that are now 6
  # unequip -> Grace 5
  python re/frida_statscan.py narrow 5         # keep addrs back to 5   (repeat until few)
  python re/frida_statscan.py show             # print current value at each survivor
  python re/frida_statscan.py struct <addr>    # dump the stat struct around an address

--w 4|2|1 sets scan width (default 4 = int32; stats are usually int32).
"""
import sys, os, json, frida

MOD = "NexusTK.exe"
CAND = os.path.join(os.path.dirname(os.path.abspath(__file__)), "statscan_candidates.json")

JS = r"""
'use strict';
function readVal(p, w){ return w===1?p.readU8():w===2?p.readU16():p.readU32(); }
rpc.exports = {
  scan(valStr, w){
    const val = parseInt(valStr,10);
    const bytes=[]; let v=val; for(let i=0;i<w;i++){ bytes.push(('0'+(v&0xff).toString(16)).slice(-2)); v=v>>>8; }
    const pat = bytes.join(' ');
    const ranges = Process.enumerateRanges('rw-');
    const hits=[];
    for(const r of ranges){
      try{ const found = Memory.scanSync(r.base, r.size, pat);
           for(const f of found) hits.push(f.address.toString()); }catch(e){}
      if(hits.length>500000) break;
    }
    return hits;
  },
  read(addrs, w){
    const out=[];
    for(const a of addrs){ try{ out.push([a, readVal(ptr(a), w)]); }catch(e){ out.push([a,-1]); } }
    return out;
  },
  dump(addr, n){
    try{ const b=new Uint8Array(ptr(addr).readByteArray(n));
         return Array.from(b).map(x=>('0'+x.toString(16)).slice(-2)).join(' '); }catch(e){ return 'ERR '+e; }
  },
  trio(v1, v2, v3, w, span){
    function pat(v){ const b=[]; let x=v; for(let i=0;i<w;i++){ b.push(('0'+(x&0xff).toString(16)).slice(-2)); x=x>>>8; } return b.join(' '); }
    function readV(p){ return w===1?p.readU8():w===2?p.readU16():p.readU32(); }
    const p1 = pat(v1);
    const ranges = Process.enumerateRanges('rw-');
    const out=[];
    for(const r of ranges){
      let found;
      try{ found = Memory.scanSync(r.base, r.size, p1); }catch(e){ continue; }
      for(const f of found){
        const a=f.address; let h2=false,h3=false;
        for(let off=-span; off<=span; off+=1){
          try{ const v=readV(a.add(off)); if(v===v2)h2=true; if(v===v3)h3=true; }catch(e){}
        }
        if(h2&&h3) out.push(a.toString());
      }
      if(out.length>3000) break;
    }
    return out;
  }
};
"""


def attach():
    dev = frida.get_local_device()
    procs = [p for p in dev.enumerate_processes() if p.name.lower() == MOD.lower()]
    if not procs:
        print("no running", MOD); sys.exit(1)
    s = dev.attach(procs[0].pid)
    sc = s.create_script(JS); sc.load()
    return sc


def load():
    return json.load(open(CAND)) if os.path.exists(CAND) else {"w": 4, "addrs": []}


def save(d):
    json.dump(d, open(CAND, "w"))


def main():
    if len(sys.argv) < 2:
        print(__doc__); return
    cmd = sys.argv[1]
    w = 4
    if "--w" in sys.argv:
        w = int(sys.argv[sys.argv.index("--w") + 1])
    sc = attach()

    if cmd == "scan":
        val = sys.argv[2]
        hits = sc.exports_sync.scan(val, w)
        save({"w": w, "addrs": hits})
        print(f"scan {val} (w{w}): {len(hits)} candidates -> {CAND}")
    elif cmd == "narrow":
        val = int(sys.argv[2])
        d = load()
        cur = sc.exports_sync.read(d["addrs"], d["w"])
        keep = [a for a, v in cur if v == val]
        d["addrs"] = keep
        save(d)
        print(f"narrow to {val}: {len(keep)} survivors")
        if len(keep) <= 12:
            for a in keep:
                print("   ", a)
    elif cmd == "show":
        d = load()
        cur = sc.exports_sync.read(d["addrs"], d["w"])
        for a, v in cur[:40]:
            print(f"   {a} = {v}")
    elif cmd == "struct":
        addr = sys.argv[2]
        # dump 64 bytes before and 96 after, as int32 grid
        base = int(addr, 16) - 64
        raw = sc.exports_sync.dump(hex(base), 160)
        b = [int(x, 16) for x in raw.split()]
        print(f"struct around {addr} (each row = offset:int32le):")
        for o in range(0, len(b) - 3, 4):
            v = b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24
            mark = "  <<<" if (base + o) == int(addr, 16) else ""
            print(f"   {hex(base + o)} (+{o - 64:4d}): {v}{mark}")
    elif cmd == "trio":
        v1, v2, v3 = int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4])
        span = 32
        if "--span" in sys.argv:
            span = int(sys.argv[sys.argv.index("--span") + 1])
        hits = sc.exports_sync.trio(v1, v2, v3, w, span)
        save({"w": w, "addrs": hits})
        print(f"trio {v1}/{v2}/{v3} (w{w}, span±{span}): {len(hits)} co-locations")
        for a in hits[:20]:
            print("   ", a)
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
