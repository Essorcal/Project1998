"""Empirically locate the live entity array -- NO assumptions from TkMemory. Capture wire mob
positions P, find every memory slot encoding a known position under SEVERAL encodings
(u32-adjacent, u16-adjacent, and x/y a few offsets apart), group by memory region, and let the
densest region + its internal stride reveal the array."""
import sys, time, collections
sys.path.insert(0, ".")
import nexus_bot as NB
import nexus_agent as NA

agent = NA.Agent(); world = NB.World(agent)
s, sc = NB.attach(NB.build_pump(world, agent)); ex = sc.exports_sync; world.mem_ex = ex
print("collecting wire entities 6s (be near mobs)...")
time.sleep(6)
mobs = world.fresh_entities(within_ms=9000)
P = set((e["x"], e["y"]) for _, e in mobs if e.get("x") and e.get("y"))
xs = sorted(set(x for x, y in P)); ys = set(y for x, y in P)
print(f"{len(mobs)} wire entities, {len(P)} distinct positions: {sorted(P)}")
if len(P) < 3:
    print("need >=3"); sys.exit(0)

def region_of(a):
    return a >> 16          # 64KB block as a coarse region proxy (no RPC)

# ---- gather candidate slots under multiple encodings; record encoding + region ----
# enc: for a hit at address a where u32@a == x, does a known y sit at a+dy (u32) for some dy?
hits = collections.defaultdict(list)   # region -> [(addr, (x,y), dy)]
tested = 0
for x in xs:
    addrs = ex.scanu32(x, 40000)
    for a in addrs:
        ap = int(a, 16)
        # try y at several small offsets after x (u32)
        for dy in (4, 8, 0xC, 0x10, -4):
            yv = ex.ru32(hex(ap + dy))
            if yv in ys and (x, yv) in P:
                hits[region_of(ap)].append((ap, (x, yv), dy))
                break
        tested += 1
print(f"scanned; candidate slots by region (region: count, dominant dy):")
ranked = sorted(hits.items(), key=lambda kv: -len(kv[1]))
for reg, lst in ranked[:8]:
    dyc = collections.Counter(d for _, _, d in lst)
    npos = len(set(p for _, p, _ in lst))
    print(f"  region {reg}: {len(lst)} slots, {npos} distinct positions, dy={dyc.most_common(3)}")

if not ranked:
    print("\nNo (x,y) encoding found. Mobs may use PIXEL coords (tile*W). Testing pixel scale...")
    # try common tile pixel sizes: find x*scale for scale in a few values
    for scale in (16, 24, 32, 48, 64):
        found = 0
        for x, y in list(P)[:6]:
            if ex.scanu32(x * scale, 2000):
                found += 1
        print(f"  scale {scale}: {found}/6 mob xs found as x*{scale}")
    sys.exit(0)

# ---- densest region: sort its slots, find the internal stride empirically ----
reg, lst = ranked[0]
addrs = sorted(set(a for a, _, _ in lst))
print(f"\ndensest region {reg}: {len(addrs)} slots; pairwise diffs (empirical stride):")
diffs = collections.Counter()
for i in range(len(addrs)):
    for j in range(i + 1, len(addrs)):
        d = addrs[j] - addrs[i]
        if 0 < d <= 0x4000:
            diffs[d] += 1
# the true stride divides many diffs -> score each small diff by how many diffs it divides
cand_strides = sorted(set(d for d in diffs if 0x40 <= d <= 0x800))
scored = []
for S in cand_strides:
    covered = sum(c for d, c in diffs.items() if d % S == 0)
    scored.append((covered, S))
scored.sort(reverse=True)
print("top stride candidates (coverage, stride):", [(c, hex(S)) for c, S in scored[:6]])
if not scored:
    print("no stride; dumping raw slots:")
    for a, p, dy in sorted(lst)[:30]:
        print(f"  {hex(a)} {p} dy={dy}")
    sys.exit(0)

S = scored[0][1]
posmap = {a: p for a, p, _ in lst}
start = min(addrs)
print(f"\nreconstructing array at stride {hex(S)} from {hex(start)}:")
known = 0
for k in range(64):
    a = start + k * S
    x = ex.ru32(hex(a)); y = ex.ru32(hex(a + 4))
    if x is None: break
    p = (x, y)
    tag = " <-known-mob" if p in P else ""
    if p in P: known += 1
    if 1 <= (x or 0) <= 600 and 1 <= (y or 0) <= 600:
        print(f"  {hex(a)}: ({x},{y}){tag}")
print(f"\n{known} slots match live wire mobs (of {len(P)}). stride={hex(S)}")
