"""Phase 1 hunt (read-only): find the client's OWN entity collection so perception can
enumerate real entities instead of reconstructing them from packets (ghost-proof).

Steps:
 1. Layout check: self struct has x@+0xFC,y@+0x100; mob eid-scan hits have eid@S,x@S+4,y@S+8.
    If same class layout, S = objbase+0xF8 and our own eid sits at selfroot+0xF8.
 2. Find mob object bases (S-0xF8), read vtable@base+0 -- same value across mobs (+ self?)
    proves one class => one container.
 3. Scan rw memory for pointers to those bases -> holder addresses; look for structure
    (contiguous array? linked nodes? stride?).
 4. Scan the module's static data for pointers into the holder structure -> static root.
"""
import sys, time, collections
sys.path.insert(0, ".")
import nexus_bot as NB
import nexus_agent as NA

agent = NA.Agent(); world = NB.World(agent)
s, sc = NB.attach(NB.build_pump(world, agent)); ex = sc.exports_sync
world.mem_ex = ex
time.sleep(4)

MODLO, MODHI = 0x400000, 0x6b3000
ru32 = lambda a: ex.ru32(hex(a))

# ---- 1. self layout check ----
selfroot = NB.read_self_root(ex)
print(f"selfroot = {hex(selfroot) if selfroot else None}")
if selfroot:
    self_uid = ru32(selfroot + 0xF8)
    self_vt  = ru32(selfroot + 0)
    print(f"  selfroot+0xF8 (uid?) = {self_uid} ({hex(self_uid) if self_uid else 0})")
    print(f"  selfroot+0x00 (vtable?) = {hex(self_vt) if self_vt else None} "
          f"{'IN-MODULE' if self_vt and MODLO <= self_vt < MODHI else ''}")
    print(f"  x,y = ({ru32(selfroot+0xFC)},{ru32(selfroot+0x100)})")

# ---- 2. mob object bases via eid scan ----
ents = world.fresh_entities(within_ms=8000)
print(f"\nwire roster: {len(ents)} fresh entities")
bases = {}
for eid, e in ents[:10]:
    for hs in ex.scanu32(eid, 40):
        S = int(hs, 16)
        x, y = ru32(S + 4), ru32(S + 8)
        if x is None or y is None:
            continue
        if abs((x or 0) - e["x"]) + abs((y or 0) - e["y"]) > 4:
            continue
        if not (1 <= x < 4096 and 1 <= y < 4096):
            continue
        base = S - 0xF8
        vt = ru32(base)
        bases[eid] = (base, vt, x, y)
        break
print(f"resolved {len(bases)} mob object bases:")
vtc = collections.Counter()
for eid, (base, vt, x, y) in bases.items():
    inmod = vt and MODLO <= vt < MODHI
    vtc[vt] += 1
    print(f"  eid={eid} base={hex(base)} vtable={hex(vt) if vt else None}"
          f"{' IN-MODULE' if inmod else ''} pos=({x},{y})")
print(f"vtable spread: { {hex(k) if k else None: v for k, v in vtc.items()} }")

# ---- 3. who points AT these objects? ----
targets = [b for b, _, _, _ in bases.values()]
if selfroot:
    targets.append(selfroot)
holders = collections.defaultdict(list)     # target -> holder addrs
for t in targets:
    for hs in ex.scanu32(t, 60):
        h = int(hs, 16)
        holders[t].append(h)
print("\nholders (who points at each object):")
allh = []
for t, hs in holders.items():
    tag = "SELF" if t == selfroot else "mob"
    print(f"  {tag} {hex(t)}: {len(hs)} holders -> {[hex(h) for h in hs[:8]]}")
    allh.extend(hs)

# ---- 4. structure analysis: are holders clustered / strided? ----
allh.sort()
print("\nholder clustering (gaps <= 0x100):")
runs = []
run = [allh[0]] if allh else []
for a, b in zip(allh, allh[1:]):
    if b - a <= 0x100:
        run.append(b)
    else:
        if len(run) > 1:
            runs.append(run)
        run = [b]
if len(run) > 1:
    runs.append(run)
for r in runs[:10]:
    gaps = sorted({b - a for a, b in zip(r, r[1:])})
    print(f"  run of {len(r)} @ {hex(r[0])}..{hex(r[-1])} gaps={gaps[:6]}")

# ---- 5. static pointers into holder neighborhoods ----
print("\nstatic (in-module) pointers into holder pages:")
pages = sorted({h & ~0xFFF for h in allh})
hits = 0
for pg in pages:
    for off in range(0, 0x1000, 4):
        pass  # too slow from python; instead scan for each holder-run head value
# cheaper: scan for pointers to each run head and to each holder itself, keep in-module hits
seen = set()
cands = [r[0] for r in runs] + allh[:40]
for v in cands:
    if v in seen:
        continue
    seen.add(v)
    for hs in ex.scanu32(v, 30):
        h = int(hs, 16)
        if MODLO <= h < MODHI:
            print(f"  STATIC {hex(h)} -> {hex(v)}")
            hits += 1
print(f"(static hits: {hits})")
s.detach()
