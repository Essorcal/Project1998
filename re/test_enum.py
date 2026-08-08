"""Validate enument (client entity-table enumeration) read-only:
 - bootstrap: find one mob object via eid scan -> rangeof -> pool region
 - enument the region; compare against the wire roster (with the FIXED 0x0c parsing)
 - measure enument latency (must be fast enough for a per-tick call)
 - watch for 15s: entities that despawn should vanish from enumeration (ghost-proofing)
"""
import sys, time
sys.path.insert(0, ".")
import nexus_bot as NB
import nexus_agent as NA

VT = 0x622f58
agent = NA.Agent(); world = NB.World(agent)
s, sc = NB.attach(NB.build_pump(world, agent)); ex = sc.exports_sync
world.mem_ex = ex
time.sleep(4)

# bootstrap pool region from one live eid
ents = world.fresh_entities(within_ms=8000)
print(f"wire roster: {len(ents)}")
region = None
for eid, e in ents:
    for hs in ex.scanu32(eid, 40):
        S = int(hs, 16)
        x, y = ex.ru32(hex(S + 4)), ex.ru32(hex(S + 8))
        if x is None or abs((x or 0) - e["x"]) + abs((y or 0) - e["y"]) > 4:
            continue
        vt = ex.ru32(hex(S - 0xF8))
        if vt == VT:
            region = ex.rangeof(hex(S - 0xF8))
            break
    if region:
        break
print("pool region:", region and (hex(int(region[0], 16)), hex(region[1]), region[2]))
if not region:
    print("no region found (no mobs in range?)"); s.detach(); sys.exit()
lo = int(region[0], 16); hi = lo + region[1]

for i in range(6):
    t0 = time.time()
    mem = ex.enument(VT, lo, hi)
    ms = (time.time() - t0) * 1000
    wire = dict(world.fresh_entities(within_ms=6000))
    memd = {u: (x, y) for u, x, y in mem}
    both = set(memd) & set(wire)
    agree = sum(1 for u in both if (wire[u]["x"], wire[u]["y"]) == memd[u])
    off1 = sum(1 for u in both
               if abs(wire[u]["x"] - memd[u][0]) + abs(wire[u]["y"] - memd[u][1]) == 1)
    print(f"[{i}] enument={len(mem)} in {ms:.1f}ms | wire={len(wire)} | overlap={len(both)} "
          f"exact={agree} off-by-1={off1} | mem-only={len(memd)-len(both)} "
          f"wire-only={len(wire)-len(both)}")
    wire_only = [u for u in wire if u not in memd]
    if wire_only:
        print(f"    ghosts (wire-only, would have been chased!): {wire_only[:6]}")
    time.sleep(2.5)
s.detach()
