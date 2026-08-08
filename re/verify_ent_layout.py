"""Verify the mob-struct layout robustly: base=uid(==wire eid), X@base+4, Y@base+8. For each
live wire entity, scan memory for its eid and check X/Y at +4/+8 match the wire position.
Also probe which field is HP (compare against wire mob-hp) and dump the struct for typing."""
import sys, time, collections
sys.path.insert(0, ".")
import nexus_bot as NB
import nexus_agent as NA

agent = NA.Agent(); world = NB.World(agent)
s, sc = NB.attach(NB.build_pump(world, agent)); ex = sc.exports_sync; world.mem_ex = ex
print("collecting wire entities 5s...")
time.sleep(5)
ents = world.fresh_entities(within_ms=8000)
print(f"{len(ents)} wire entities; resolving each in memory (uid==eid, X@+4, Y@+8):")

resolved = []
for eid, e in ents:
    x, y = e.get("x"), e.get("y")
    if x is None:
        continue
    hit = None
    for a in ex.scanu32(eid, 60):
        ap = int(a, 16)
        xv, yv = ex.ru32(hex(ap + 4)), ex.ru32(hex(ap + 8))
        # accept on PLAUSIBLE tile coords (memory pos is LIVE and may differ from the stale
        # wire pos -- that difference is exactly why memory beats the wire)
        if xv is not None and 1 <= xv <= 600 and yv is not None and 1 <= yv <= 600:
            hit = ap
            break
    ok = hit is not None
    if ok:
        mx, my = ex.ru32(hex(hit + 4)), ex.ru32(hex(hit + 8))
        drift = abs(mx - x) + abs(my - y)
        print(f"  eid={eid:#010x} wire=({x},{y}) MEM=({mx},{my}) drift={drift} @ {hex(hit)} OK")
    else:
        print(f"  eid={eid:#010x} wire=({x},{y}): eid not found w/ valid coords")
    if ok:
        resolved.append((eid, hit, e))

print(f"\n{len(resolved)}/{len(ents)} entities resolved from memory by eid.")
if not resolved:
    sys.exit(0)

# dump one struct fully to identify hp/type fields
eid, base, e = resolved[0]
print(f"\nstruct dump for eid={eid:#x} (base=uid): offset: value")
for off in range(-0x8, 0x60, 4):
    print(f"  base{off:+#06x}: {ex.ru32(hex(base+off))}")

# HP probe: compare struct fields to the wire mob-hp bar (agent.mobhp) if we have it
print("\nwire mob-hp readings (from 0x13):", dict(list(agent.mobhp.items())[:10]))

# stride/manager check: are the resolved bases at any regular spacing?
bases = sorted(b for _, b, _ in resolved)
print("\nresolved bases (heap spread):")
for b in bases:
    print(f"  {hex(b)}")
if len(bases) >= 2:
    diffs = [bases[i+1]-bases[i] for i in range(len(bases)-1)]
    print("gaps between bases:", [hex(d) for d in diffs])

# static pointer / manager: look for pointers to a few bases
print("\npointers to entity objects (manager table?):")
for eid, base, e in resolved[:4]:
    holders = ex.scanu32(base, 20)
    print(f"  eid={eid:#x} base={hex(base)}: {len(holders)} ptr(s) -> {[h for h in holders[:8]]}")
