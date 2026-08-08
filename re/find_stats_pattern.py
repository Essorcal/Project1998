"""Locate the LIVE equipped-stat struct with NATIVE pattern scans only.

Deliberately avoids snapshot+diff: snapshotting ~512MB and byte-walking it in JS starves
the client's threads and locks the game up (learned the hard way). Memory.scanSync is
native and cheap by comparison.

Idea: stat pages store their fields together. If might/grace/will (11/18/9 here) are
adjacent, the exact byte pattern is rare enough to find them outright -- so try every
plausible encoding (u8 / u16 / u32) and every field order, then verify the winner by
checking that ac/dam/hit/maxhp/maxmana also sit nearby.
"""
import sys, time, struct, itertools
sys.path.insert(0, ".")
import nexus_bot as NB
import nexus_agent as NA

GT = {"level": 17, "might": 11, "grace": 18, "will": 9, "ac": 68, "dam": 1, "hit": 0,
      "maxhp": 971, "maxmana": 473}
TRIPLE = {"might": 11, "grace": 18, "will": 9}      # the distinctive, adjacent-ish trio
CONFIRM = [68, 971, 473, 17]                        # ac / maxhp / maxmana / level

JS = """
rpc.exports.scanpat = function(pat, cap){
  const out=[]; let rs;
  try{ rs=Process.enumerateRanges('rw-'); }catch(e){ return out; }
  for(const r of rs){
    if (r.size > 0x4000000) continue;
    let ms; try{ ms=Memory.scanSync(r.base, r.size, pat); }catch(e){ continue; }
    for(const m of ms){ out.push(m.address.toString()); if(out.length>=cap) return out; }
  }
  return out;
};
"""


def encodings(vals):
    """(label, hex-pattern) for u8 / u16le / u32le encodings of an ordered value list."""
    yield "u8 ", " ".join(f"{v:02x}" for v in vals)
    yield "u16", " ".join(f"{v & 0xff:02x} {(v >> 8) & 0xff:02x}" for v in vals)
    yield "u32", " ".join(f"{v & 0xff:02x} {(v >> 8) & 0xff:02x} 00 00" for v in vals)


def main():
    agent = NA.Agent(); world = NB.World(agent)
    s, sc = NB.attach(NB.build_pump(world, agent))
    ex = sc.exports_sync
    world.mem_ex = ex
    sc2 = s.create_script(JS); sc2.load(); ex2 = sc2.exports_sync
    time.sleep(1.5)
    R = NB.read_self_root(ex)
    print(f"self root {hex(R) if R else None}")
    print(f"searching for adjacent {TRIPLE} in any order/encoding\n")

    found = []
    for order in itertools.permutations(TRIPLE.items()):
        names = "/".join(k for k, _ in order)
        vals = [v for _, v in order]
        for enc, pat in encodings(vals):
            try:
                hits = ex2.scanpat(pat, 200)
            except Exception as e:
                print("  scan failed:", e); continue
            if hits:
                print(f"  {enc} {names:20} pat[{pat}] -> {len(hits)} hit(s)")
                for h in hits:
                    found.append((enc, names, int(h, 16)))

    print(f"\ntotal raw hits: {len(found)}; verifying against {CONFIRM} nearby...")
    winners = []
    for enc, names, A in found:
        try:
            raw = bytes.fromhex(ex.readbytes(hex(A - 0x80), 0x140))
        except Exception:
            continue
        if len(raw) < 0x140:
            continue
        u16 = {struct.unpack_from("<H", raw, o)[0] for o in range(0, 0x140, 2)}
        u32 = {struct.unpack_from("<I", raw, o)[0] for o in range(0, 0x140, 4)}
        score = sum(1 for c in CONFIRM if c in u16 or c in u32)
        if score >= 2:
            winners.append((score, enc, names, A, raw))
    winners.sort(key=lambda t: -t[0])
    print(f"candidates confirmed by >=2 other stats: {len(winners)}\n")

    inv = {}
    for k, v in GT.items():
        inv.setdefault(v, []).append(k)
    for score, enc, names, A, raw in winners[:5]:
        rel = f" (selfroot{A-R:+#x})" if R else ""
        print(f"=== {hex(A)} [{enc} {names}] confirms={score}{rel} ===")
        for o in range(0, 0x140, 2):
            v = struct.unpack_from("<H", raw, o)[0]
            if v in inv:
                print(f"    u16 {-0x80 + o:+#06x}  {v:<6} <- {'/'.join(inv[v])}")
        print()
    if not winners:
        print("no adjacent triple found -- fields are probably not contiguous;\n"
              "fall back to the tnl differential (native scan + re-read candidates only).")
    s.detach()


if __name__ == "__main__":
    main()
