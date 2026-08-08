"""Find the CURRENT ROOM NAME in the live client in a REUSABLE way.

Last session we found the name only as a bare UTF-16LE string on the heap -- no pointer to
it, so the address died with the client. A room name we can read every tick is what makes
(a) per-room mob attribution in the CSVs and (b) WARP DETECTION possible: a warp can drop
you 1 tile from where you stood, so position deltas can NEVER detect it -- only a change of
room identity can.

We don't need to know which room we're in to find it: harvest every UTF-16LE string in the
client's heap and intersect that set with the 1751 known map names from
data/game-data/map_index.csv. Whatever matches IS the current room (plus any static name
tables, which are equally useful -- a table entry is a stable, restart-proof address).

Then climb from the string toward a module-static root so the accessor survives restarts.

Usage:  python find_roomname.py            # harvest + identify
        python find_roomname.py <addr>     # climb from a known string address
"""
import sys, os, csv, collections, frida

MODLO, MODHI = 0x400000, 0x6b3000
MAPCSV = os.path.join(os.path.dirname(__file__), "..", "data", "game-data", "map_index.csv")

JS = r"""
rpc.exports = {
  // Harvest UTF-16LE ASCII strings (len>=minLen) from writable memory in ONE pass.
  // Byte-walking in JS beats 1751 separate Memory.scanSync calls by orders of magnitude.
  utf16strings: function(minLen, cap){
    const out = [];
    let rs; try{ rs = Process.enumerateRanges('rw-'); }catch(e){ return out; }
    for (const r of rs){
      if (r.size > 0x4000000) continue;              // skip huge mappings (asset caches)
      let buf; try{ buf = new Uint8Array(r.base.readByteArray(r.size)); }catch(e){ continue; }
      let i = 0;
      while (i + 1 < buf.length){
        // a UTF-16LE ASCII char is [0x20..0x7e, 0x00]
        if (buf[i] >= 0x20 && buf[i] <= 0x7e && buf[i+1] === 0x00){
          let j = i, s = '';
          while (j + 1 < buf.length && buf[j] >= 0x20 && buf[j] <= 0x7e && buf[j+1] === 0x00){
            s += String.fromCharCode(buf[j]); j += 2;
          }
          if (s.length >= minLen){
            out.push([r.base.add(i).toString(), s]);
            if (out.length >= cap) return out;
          }
          i = j + 2;
        } else i += 2;
      }
    }
    return out;
  },
  scanu32: function(v, cap, prot){
    v = v>>>0;
    const p = [v&0xff,(v>>>8)&0xff,(v>>>16)&0xff,(v>>>24)&0xff]
      .map(x=>('0'+x.toString(16)).slice(-2)).join(' ');
    const out = []; let rs;
    try{ rs = Process.enumerateRanges(prot); }catch(e){ return out; }
    for (const r of rs){
      let ms; try{ ms = Memory.scanSync(r.base, r.size, p); }catch(e){ continue; }
      for (const m of ms){ out.push(m.address.toString()); if (out.length >= cap) return out; }
    }
    return out;
  },
  ru32: function(a){ try{ return ptr(a).readU32(); }catch(e){ return null; } },
  rutf16: function(a, n){ try{ return ptr(a).readUtf16String(n); }catch(e){ return null; } },
  readbytes: function(a, n){
    try{ const b = new Uint8Array(ptr(a).readByteArray(n));
      let s=''; for(let i=0;i<b.length;i++) s += ('0'+b[i].toString(16)).slice(-2);
      return s; }catch(e){ return ''; }
  },
};
"""


def connect():
    dev = frida.get_local_device()
    pids = [p.pid for p in dev.enumerate_processes() if p.name.lower() == "nexustk.exe"]
    if not pids:
        raise SystemExit("no live client")
    sess = dev.attach(pids[0])
    sc = sess.create_script(JS)
    sc.load()
    print(f"attached pid {pids[0]}")
    return sess, sc.exports_sync


def climb(ex, addr, label=""):
    """Report holders of `addr` and try to reach a module-static root."""
    print(f"\n--- climbing from {hex(addr)} {label} ---")
    seen, frontier = set(), [addr]
    for depth in range(1, 5):
        nxt, statics = [], []
        for a in frontier:
            if a in seen:
                continue
            seen.add(a)
            for x in ex.scanu32(a, 12, "rw-") + ex.scanu32(a, 12, "r--"):
                v = int(x, 16)
                (statics if MODLO <= v < MODHI else nxt).append(v)
        uniq = sorted(set(nxt))
        if statics:
            print(f"  depth {depth}: STATIC {[hex(v) for v in sorted(set(statics))[:10]]}")
            return sorted(set(statics))
        print(f"  depth {depth}: {len(uniq)} non-static holders "
              f"{[hex(v) for v in uniq[:6]]}")
        if not uniq:
            print("  dead end (string is likely INLINE in a struct, not pointed at)")
            return []
        frontier = uniq[:40]
    return []


def main():
    sess, ex = connect()

    if len(sys.argv) > 1:                       # climb mode
        climb(ex, int(sys.argv[1], 0), "(given)")
        sess.detach(); return

    names = {}
    with open(MAPCSV, encoding="utf-8") as f:
        for r in csv.DictReader(f):
            names.setdefault(r["name"], []).append((r["id"], r["xs"], r["ys"]))
    print(f"map dictionary: {len(names)} distinct names")

    print("harvesting UTF-16LE strings from heap (one pass)...")
    got = ex.utf16strings(4, 400000)
    print(f"  {len(got)} strings")

    hits = collections.defaultdict(list)
    for addr, s in got:
        if s in names:
            hits[s].append(int(addr, 16))
    print(f"\n=== strings matching a known map name: {len(hits)} ===")
    for s, addrs in sorted(hits.items(), key=lambda kv: -len(kv[1]))[:40]:
        meta = names[s][0]
        print(f"  {s!r:34} id={meta[0]:>5} {meta[1]}x{meta[2]}  "
              f"{len(addrs)} copy(s) {[hex(a) for a in addrs[:4]]}")

    # A name that appears EXACTLY once in the heap is the strongest "current room" signal;
    # names appearing many times are usually a static table of every map.
    singles = {s: a for s, a in hits.items() if len(a) == 1}
    print(f"\nsingle-copy candidates (likely the CURRENT room): {list(singles)}")
    for s, addrs in list(singles.items())[:3]:
        climb(ex, addrs[0], f"({s!r})")
    sess.detach()


if __name__ == "__main__":
    main()
