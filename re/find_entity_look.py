"""Find the LOOK (sprite) field inside the client's entity object.

Why: entity presence comes from scanning the client's own entity pool (uid@+0xF8, x@+0xFC,
y@+0x100, type@+0xF4), and that path is what makes ghosts impossible. But pool-discovered
entities carry NO look -- look is only known for mobs whose 0x07 SPAWN packet we happened to
see. That makes a look-based kill whitelist refuse almost everything, which is exactly how the
bot ended up standing still being beaten by a mob it would not target.

Method (differential, no guessing): the 0x07 wire packet gives us eid -> look for the subset of
mobs that spawned in view. So for those entities we already KNOW the answer. Read every u16 in
the object and keep the offsets whose value equals the known look; intersect across many
entities and only one offset should survive.

SAFETY: reads a single ~0x20c window per entity via native reads -- tiny. Does NOT byte-walk
large regions from JS (that freezes the live client -- see nexustk-live-frida-scan-safety).

Usage:
    python find_entity_look.py [seconds]
"""
import sys, time, collections

sys.path.insert(0, ".")
import nexus_bot as NB
import nexus_agent as NA

JS = r"""
rpc.exports.entwords = function(vt, lo, hi, span){
  // For every entity object in [lo,hi), return [uid, x, y, type, u16[0..span/2]].
  const out = [];
  try{
    const pat = [vt&0xff,(vt>>>8)&0xff,(vt>>>16)&0xff,(vt>>>24)&0xff]
      .map(x=>('0'+x.toString(16)).slice(-2)).join(' ');
    const ms = Memory.scanSync(ptr(lo), hi - lo, pat);
    for (const m of ms){
      const a = m.address;
      if (a.and(3).toInt32() !== 0) continue;
      try{
        const uid = a.add(0xF8).readU32();
        const x = a.add(0xFC).readU32(), y = a.add(0x100).readU32();
        const ty = a.add(0xF4).readU32();
        if (!(uid > 1000 && x > 0 && y > 0 && x < 1000 && y < 1000)) continue;
        const words = [];
        for (let o = 0; o < span; o += 2) words.push(a.add(o).readU16());
        out.push([uid, x, y, ty, words]);
      }catch(e){}
    }
  }catch(e){}
  return out;
};
"""


def main():
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 25.0
    wins = NB.find_windows()
    if not wins:
        raise SystemExit("no live NexusTK window")
    agent = NA.Agent()
    world = NB.World(agent)
    s, sc = NB.attach(NB.build_pump(world, agent), pid=wins[0][2])
    world.mem_ex = sc.exports_sync
    sc2 = s.create_script(JS)
    sc2.load()
    ex = sc2.exports_sync

    print(f"collecting 0x07 spawn looks for {secs:.0f}s (walk around / let mobs respawn)...")
    t0 = time.time()
    while time.time() - t0 < secs:
        if world.pool is None:
            world.bootstrap_pool()
        world.refresh_from_pool()
        time.sleep(0.4)

    with world.lock:
        known = {eid: e["look"] for eid, e in world.ent.items() if e.get("look") is not None}
    print(f"entities with a wire-known look: {len(known)}")
    if known:
        print("  ", dict(collections.Counter(known.values())))
    expect = None
    for a in sys.argv[1:]:
        if a.startswith("--expect="):
            expect = int(a.split("=", 1)[1])
    if not known and expect is None:
        # FALLBACK: no spawn was witnessed (standing still => nothing new spawns). We still know
        # what lives here, so match the value directly instead of waiting for the wire.
        print("no wire looks. Re-run with --expect=<look> (e.g. 25 = squirrel family) to match"
              " the value directly, or walk to fresh ground so mobs spawn in view.")
        s.detach(); return

    if world.pool is None:
        print("entity pool not located"); s.detach(); return
    rows = ex.entwords(NB.ENT_VTABLE, world.pool[0], world.pool[1], 0x20C)
    print(f"entity objects read from the pool: {len(rows)}")

    # offsets whose u16 equals that entity's known look, intersected over entities
    hits = None
    checked = 0
    for uid, x, y, ty, words in rows:
        if ty != 3:
            continue                      # creatures only; ground items have no sprite of interest
        look = known.get(uid, expect)     # wire truth when we have it, else the expected value
        if look is None:
            continue
        checked += 1
        # the wire masks the look with &0x7fff, so accept the field with or without flag bits
        matching = {i * 2 for i, w in enumerate(words) if w == look or (w & 0x7FFF) == look}
        hits = matching if hits is None else (hits & matching)
    print(f"entities cross-checked: {checked}")
    if not hits:
        print("NO consistent offset holds the look as a u16.")
        print("Next: the look may be a u8, or stored with flag bits (the wire masks &0x7fff),")
        print("or it lives off a pointer rather than inline.")
    else:
        print(f"CANDIDATE look offsets: {sorted(hex(h) for h in hits)}")
        for h in sorted(hits):
            vals = {uid: words[h // 2] for uid, _, _, _, words in rows}
            print(f"   +{h:#05x}: sample {list(vals.items())[:6]}")
    s.detach()


if __name__ == "__main__":
    main()
