"""Read the LIVE inventory (item -> slot letter) from client memory.

Why memory: pressing `i` opens the inventory UI but sends NO packet (verified against full
plaintext egress), so the list exists only client-side. It is a fixed-stride array --
**0x1FC bytes per record, UTF-16 name at the record start** -- and the record INDEX is the
slot letter the game shows in the mini text box (`g: Farmer armor`). Confirmed against
ground truth: index 6 == 'g', which is where Farmer armor sat before we swapped it and
where Peasant garb landed after.

That makes `w`+letter scriptable: look up an item's CURRENT letter, wear it, and verify
against the 0x39 profile. Letters shift as inventory changes, which is exactly why this
has to be read fresh rather than hard-coded.

Usage:
    python inventory.py                 # print the live inventory with slot letters
    python inventory.py "Novice sword"  # just that item's letter
"""
import sys, re, time

STRIDE = 0x1FC          # bytes between consecutive inventory records
MAXSLOTS = 26           # a..z
_QTY = re.compile(r"\s*\(\d+\)\s*$")     # 'Yellow scroll (107)' -> 'Yellow scroll'

JS = r"""
rpc.exports.scanpat = function(pat, cap){
  const out=[]; let rs;
  try{ rs=Process.enumerateRanges('rw-'); }catch(e){ return out; }
  for(const r of rs){ if (r.size > 0x4000000) continue;
    let ms; try{ ms=Memory.scanSync(r.base,r.size,pat); }catch(e){ continue; }
    for(const m of ms){ out.push(m.address.toString()); if(out.length>=cap) return out; } }
  return out;
};
rpc.exports.rutf16 = function(a, n){
  try{ return ptr(a).readUtf16String(n); }catch(e){ return null; }
};
"""


def _script(session):
    sc = session.create_script(JS)
    sc.load()
    return sc.exports_sync


def find_base(ex, anchors):
    """Locate record 0 of the PLAYER'S inventory.

    A single anchor is not enough: the client also holds a global item-name table, and
    matching one word there (e.g. "Book" inside "Book Donor for Children") yields a
    misaligned base and a listing full of truncated names like 'epter' / 'lade'. So score
    every candidate by how many DISTINCT anchors land exactly on a stride boundary from
    it, and require at least two. Alignment is implied: a wrong base puts the other
    anchors at non-multiples.
    """
    hits = {}
    for name in anchors:
        pat = " ".join(f"{b:02x}" for b in name.encode("utf-16-le"))
        try:
            hits[name] = [int(h, 16) for h in ex.scanpat(pat, 60)]
        except Exception:
            hits[name] = []
    best, best_score = None, 0
    for name, addrs in hits.items():
        for a in addrs:
            for back in range(MAXSLOTS):            # assume this hit is record `back`
                base = a - back * STRIDE
                score = 0
                for other, others in hits.items():
                    if any((o - base) >= 0 and (o - base) % STRIDE == 0
                           and (o - base) // STRIDE < MAXSLOTS for o in others):
                        score += 1
                if score > best_score:
                    best, best_score = base, score
    return best if best_score >= 2 else None


def read(ex, base=None, anchors=("Book", "Topaz", "Peasant garb", "Novice sword")):
    """-> list of (letter, raw_name, canonical_name); [] if the array can't be located."""
    if base is None:
        base = find_base(ex, anchors)
    if base is None:
        return []
    out, blanks = [], 0
    for i in range(MAXSLOTS):
        t = ex.rutf16(hex(base + i * STRIDE), 40)
        ok = t and t.strip() and any(c.isalpha() for c in t)
        if not ok:
            # An empty/unreadable slot is NOT the end of the list -- inventory slots can be
            # vacated in the middle (that is how `Novice sword` ended up past a gap). Only
            # a long unbroken run of blanks means we have walked off the array.
            blanks += 1
            if blanks >= 6:
                break
            continue
        blanks = 0
        out.append((chr(ord("a") + i), t.strip(), _QTY.sub("", t.strip())))
    return out


def letter_of(ex, item):
    """Current slot letter for `item`, or None. Always read fresh: letters shift."""
    for letter, raw, canon in read(ex):
        if canon.lower() == item.lower():
            return letter
    return None


def main():
    sys.path.insert(0, ".")
    import nexus_bot as NB
    import nexus_agent as NA
    agent = NA.Agent()
    world = NB.World(agent)
    s, sc = NB.attach(NB.build_pump(world, agent))
    time.sleep(1.2)
    ex = _script(s)
    try:
        inv = read(ex)
        if not inv:
            print("could not locate the inventory array")
            return
        if len(sys.argv) > 1:
            want = sys.argv[1]
            hit = [l for l, _, c in inv if c.lower() == want.lower()]
            print(f"{want}: slot {hit[0]}" if hit else f"{want}: not in inventory")
            return
        print(f"{len(inv)} inventory slots:\n")
        for letter, raw, canon in inv:
            qty = "" if raw == canon else f"   x{raw[len(canon):].strip().strip(chr(40)+chr(41))}"
            # item names can contain non-cp1252 glyphs; don't let the console kill the run
            line = f"   {letter}  {canon}{qty}"
            print(line.encode(sys.stdout.encoding or "utf-8", "replace")
                      .decode(sys.stdout.encoding or "utf-8", "replace"))
    finally:
        s.detach()


if __name__ == "__main__":
    main()
