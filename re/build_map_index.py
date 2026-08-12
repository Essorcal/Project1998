"""Build a map index (id -> name, width, height) the server loads for warp/list commands.

CLIENT-AUTHORITATIVE: we iterate the 4.95 client's own TK<id>.map files and emit EVERY one — a map the
client ships is warpable, period. The client .map is headerless (raw 4-byte cells, no dims), so we only
need to split its cell count into (xs, ys). Any factor pair with the right product is SAFE — the client
reads exactly the file; a wrong split only skews the row-stride (map looks sheared, tiles land in the
wrong place), it never overruns or crashes. So we never drop a client map, we just have to guess well.

Dim choice per map (cells = client filesize/4), in priority order:
  1. **Wall-connectivity scoring** (primary, added 2026-07-27 after the Mythic Rabbit dungeon shipped
     with 5 of its 8 rooms sheared by a bad guess — see docs/NexusTK-4.95-Protocol.md §8.2 and
     memory/nexustk-495-mythic-rabbit-dims.md). For each candidate (w, h) factor pair of `cells`, read the
     ground-passability bit per cell and count what fraction of solid/wall cells have ZERO orthogonal
     solid neighbor ("isolated"). A correctly-strided hand-built room has almost no isolated wall pixels —
     walls are drawn as continuous lines/blobs. A wrong stride shears row N against row N+1 by the wrong
     offset, which statistically isolates a large fraction of wall cells. This is a strong, self-contained
     signal that doesn't depend on RTK's often-mismatched reference data at all.
     - Candidates are filtered to a sane aspect ratio (<=3.2:1) and reasonable size (both dims >=8) to
       avoid degenerate thin-strip wins (e.g. 280x10) that can spuriously score well on sparse outdoor
       maps just because there are few rows for a wall cell to have a vertical neighbor in.
     - Also filtered against every coordinate `Warps.csv` actually uses as a source/destination on this
       map: a real warp landing at (x=15, y=53) proves w>15 and h>53, which resolves orientation ties.
       If applying that filter leaves zero candidates (a single bad Warps.csv row can do this — see the
       Owsla warp-1953 case), the filter is dropped rather than failing the map.
     - Only trusted when there's a real signal: >=20 wall cells and >=5% wall density. Below that
       (near-empty maps), falls through to step 2.
  2. RTK's `rtkmaps/Accepted/<MapFile>` header (first 4 bytes = xs/ys BE) when it's an exact cell-count
     match — a genuine confirmation, just weaker evidence than step 1 when both are available since RTK is
     a different (7.x) client version that redesigned many rooms (different cell counts entirely).
  3. Closest-aspect-ratio guess to RTK's hint (or closest-to-square with no RTK row at all) — last resort.

Name: RTK MapName if present, else "Map <id>" (still warpable by id / by that label).

Output: game-data/map_index.csv (id,name,xs,ys) — gitignored (logic-only repo, docs §17.1).
Env overrides: RTK_MAPS_CSV, RTK_MAPS_DIR, CLIENT_MAPS, OUT, RTK_WARPS_CSV.
"""
import csv, os, struct, glob

HERE       = os.path.dirname(os.path.abspath(__file__))
DATA       = os.path.join(HERE, '..', 'data', 'game-data')
MAPS_CSV   = os.environ.get('RTK_MAPS_CSV', os.path.join(DATA, 'Maps.csv'))
WARPS_CSV  = os.environ.get('RTK_WARPS_CSV', os.path.join(DATA, 'Warps.csv'))
RTK_MAPS   = os.environ.get('RTK_MAPS_DIR', os.path.join(HERE, '..', 'RTK-Server', 'rtkmaps', 'Accepted'))
CLIENT_MAP = os.environ.get('CLIENT_MAPS', r'C:\Program Files (x86)\Nexon\NextAeon\Maps')
OUT        = os.environ.get('OUT', os.path.join(DATA, 'map_index.csv'))

WALL_MIN, DENSITY_MIN, MAX_ASPECT, MIN_DIM, IMPROVE_EPS = 20, 0.05, 3.2, 8, 0.02

# Explicit overrides for maps confirmed by the STRONGEST evidence tier: wall-connectivity scoring AND
# live in-game verification after a !reload (2026-07-27, Mythic Rabbit dungeon investigation -- see
# docs/NexusTK-4.95-Protocol.md §8.2 and memory/nexustk-495-mythic-rabbit-dims.md). Two of these (Rabbit
# Leap, Hare Depression) only clear the statistical bar by a small margin on their own, too close to the
# general IMPROVE_EPS threshold to trust sight-unseen elsewhere in the file set -- but these five are
# not "sight unseen": a human confirmed the fix live in the actual client. Covers all 3 depth tiers.
DIM_OVERRIDES = {}
for base, (xs, ys) in {
    204: (20, 60),   # Rabbit Leap
    205: (30, 39),   # Foraged Fields
    206: (30, 30),   # Hare Depression
    207: (20, 20),   # Mythic Owsla
    208: (20, 40),   # Hare Summit
}.items():
    for tier_offset in (0, 3000, 4000):
        DIM_OVERRIDES[base + tier_offset] = (xs, ys)

# Same evidence tier as above, but these don't follow the dungeon's +3000/+4000 tiering -- each id listed
# explicitly instead (some are duplicate-content map ids sharing one physical room, not depth tiers).
for ids, dims in {
    (472, 3472, 4472): (10, 30),   # Woolen Squeeze -- 30x10 (the shape/content match) was live-confirmed
        # right but oriented wrong ("horizontal when it should be vertical"); transposed to 10x30.
        # 12x25 was independently confirmed wrong too, so this is now the only untested candidate left of
        # the four that originally tied at isolation_fraction 0.000.
    (6302, 6382): (30, 30),        # Desert -- restored: round-2's revert-on-ambiguity swept this back to
        # the wrong 45x20 even though its connectivity margin is decisive (0.007 vs next-best ~0.02, same
        # signature as sibling Desert map 6309 which IS live-confirmed correct at 30x30).
    (6315, 6355, 6435, 6475): (10, 100), # Desert -- LIVE-CONFIRMED CORRECT (2026-07-27), the hardest case
        # in this whole sweep: 25x40/20x50/50x20/100x10/5x200/200x5 were all tried and ruled out first.
        # The winner FAILS the row-uniformity heuristic (header spills into row 1) that correctly predicted
        # every other fix in this file -- proof that heuristic is necessary-looking but not sufficient, at
        # least on maps with strongly periodic content (this one repeats every 10 tiles, so most candidate
        # widths "looked" structurally plausible without being right). Root lesson: for a map this
        # ambiguous, live human verification is the only real ground truth -- don't trust any single
        # automated signal past a certain point. See docs/NexusTK-4.95-Protocol.md §17.4 and
        # memory/nexustk-495-mythic-rabbit-dims.md.
}.items():
    for mid in ids:
        DIM_OVERRIDES[mid] = dims

# Maps where wall-connectivity scoring was live-tested via !warp and confirmed WRONG despite passing
# every statistical gate (2026-07-27) -- forced back to the RTK/aspect-guess fallback instead. Each is a
# distinct failure mode, not one bug: 533/1214 had a real, non-tied scoring margin but the metric itself is
# a poor fit for organic garden/scattered-tree terrain, which legitimately has isolated single-tile
# decorations (the metric's "walls form continuous lines" assumption is architecture-shaped, not
# garden-shaped). Don't let wall_connectivity_dims re-decide these; a future .map file change would need
# this list revisited by hand.
FORCE_PRIOR_IDS = {533, 3533, 4533, 1214}

def rtk_dims(mapfile):
    p = os.path.join(RTK_MAPS, mapfile or '')
    if not mapfile or not os.path.isfile(p):
        return None
    with open(p, 'rb') as f:
        head = f.read(4)
    return struct.unpack('>HH', head) if len(head) >= 4 else None

def factor_pairs(n):
    """All (w, h) with w*h == n, both dims >= MIN_DIM, aspect ratio within MAX_ASPECT."""
    out = []
    i = 1
    while i * i <= n:
        if n % i == 0:
            w, h = i, n // i
            if w >= MIN_DIM and h >= MIN_DIM and max(w, h) / min(w, h) <= MAX_ASPECT:
                out.append((w, h))
                if w != h:
                    out.append((h, w))
        i += 1
    return out

def isolation_fraction(cells_ground, w, h):
    """Fraction of solid (pass!=0) cells with no orthogonal solid neighbor. Lower = more coherent."""
    if len(cells_ground) < w * h:
        return None, 0
    wall = isolated = 0
    for y in range(h):
        base = y * w
        for x in range(w):
            v = cells_ground[base + x]
            if v == 0:
                continue
            wall += 1
            ok = ((x + 1 < w and cells_ground[base + x + 1]) or
                  (x - 1 >= 0 and cells_ground[base + x - 1]) or
                  (y + 1 < h and cells_ground[base + w + x]) or
                  (y - 1 >= 0 and cells_ground[base - w + x]))
            if not ok:
                isolated += 1
    return (isolated / wall if wall else None), wall

def load_pass_bits(path, cells):
    with open(path, 'rb') as f:
        d = f.read()
    return [((d[i*4] | (d[i*4+1] << 8)) >> 14) & 3 for i in range(cells)]

def load_warp_bounds(path):
    bounds = {}
    if not os.path.isfile(path):
        return bounds
    with open(path, encoding='utf-8-sig') as f:
        r = csv.reader(f)
        next(r, None)
        for row in r:
            try:
                sm, sx, sy, dm, dx, dy = (int(row[1]), int(row[2]), int(row[3]),
                                           int(row[4]), int(row[5]), int(row[6]))
            except (IndexError, ValueError):
                continue
            for m, x, y in ((sm, sx, sy), (dm, dx, dy)):
                c = bounds.setdefault(m, [0, 0])
                c[0], c[1] = max(c[0], x), max(c[1], y)
    return bounds

def wall_connectivity_dims(path, cells, maxx, maxy, prior):
    """Best (w, h) by wall-connectivity scoring, vs `prior` (the RTK/aspect-guess dims that would
    otherwise be used). Returns None unless a candidate both clears an absolute confidence bar (enough
    wall cells + density for the signal to mean anything) AND clearly beats the prior's own score by
    IMPROVE_EPS -- this is what keeps the metric from overriding a perfectly fine map just because a
    sparse/low-wall-density candidate happens to score marginally lower by noise (confirmed on real data:
    an ungated version of this flipped 440/1750 maps, many of them obviously wrong -- see
    memory/nexustk-495-mythic-rabbit-dims.md for the false-positive writeup)."""
    ground = load_pass_bits(path, cells)
    cands = factor_pairs(cells)
    bounded = [(w, h) for (w, h) in cands if w > maxx and h > maxy]
    if not bounded:
        bounded = cands   # a single bad warp coordinate shouldn't veto every candidate
    scored = []
    for (w, h) in bounded:
        frac, wallcnt = isolation_fraction(ground, w, h)
        if frac is None:
            continue
        density = wallcnt / (w * h)
        scored.append((frac, wallcnt, density, w, h))
    if not scored:
        return None
    scored.sort(key=lambda t: (t[0], -t[1]))
    frac, wallcnt, density, w, h = scored[0]
    if wallcnt < WALL_MIN or density < DENSITY_MIN:
        return None
    if (w, h) == tuple(prior):
        return None

    # Reject a genuine tie: if a candidate with a MEANINGFULLY DIFFERENT SHAPE (not just the same
    # rectangle transposed -- that's an orientation question, not a shape disagreement) scores within
    # IMPROVE_EPS of the winner, the metric has no real opinion here and picking one is a coin flip.
    # Found live 2026-07-27: sparse/open outdoor maps (deserts, groves) can have 3-4 wildly different
    # aspect ratios (e.g. 10x30, 25x12, 20x15) all scoring ~0.000 -- the algorithm confidently "won" by
    # picking one arbitrarily, and it was wrong. See memory/nexustk-495-mythic-rabbit-dims.md.
    aspect = round(max(w, h) / min(w, h), 3)
    for f2, w2, h2, wc2, d2 in ((s[0], s[3], s[4], s[1], s[2]) for s in scored[1:]):
        if f2 - frac >= IMPROVE_EPS:
            break   # scored is sorted by frac -- nothing further can tie either
        if round(max(w2, h2) / min(w2, h2), 3) != aspect:
            return None   # a distinctly-shaped candidate is within the margin -- ambiguous, don't guess

    prior_frac, _ = isolation_fraction(ground, prior[0], prior[1]) if prior[0] * prior[1] == cells else (None, 0)
    if prior_frac is not None and (prior_frac - frac) < IMPROVE_EPS:
        return None
    return (w, h)

def choose_dims_by_aspect(cells, rtk):
    if cells <= 0:
        return None
    pairs = []
    i = 1
    while i * i <= cells:
        if cells % i == 0:
            pairs.append((i, cells // i))
        i += 1
    target = (rtk[0] / rtk[1]) if rtk and rtk[1] else 1.0
    best, bestd = None, None
    for a, b in pairs:
        for w, h in ((a, b), (b, a)):
            d = abs((w / h) - target)
            if bestd is None or d < bestd:
                best, bestd = (w, h), d
    return best

# RTK id -> (name, mapfile), for names + dim hints
rtk_row = {}
with open(MAPS_CSV, encoding='utf-8') as f:
    for r in csv.DictReader(f):
        mid = r.get('MapId', '')
        if mid.isdigit():
            rtk_row[int(mid)] = ((r.get('MapName') or '').strip(), (r.get('MapFile') or '').strip())

warp_bounds = load_warp_bounds(WARPS_CSV)

rows, by_connectivity, by_rtk_exact, guessed, unnamed = [], 0, 0, 0, 0
for p in glob.glob(os.path.join(CLIENT_MAP, 'TK*.map')):
    num = os.path.basename(p)[2:-4]
    if not num.isdigit():
        continue
    mid = int(num)
    cells = os.path.getsize(p) // 4
    if cells <= 0:
        continue
    name, mapfile = rtk_row.get(mid, ('', ''))
    d = rtk_dims(mapfile)
    maxx, maxy = warp_bounds.get(mid, (0, 0))

    if d and d[0] * d[1] == cells:
        prior, prior_is_exact = d, True
    else:
        prior, prior_is_exact = choose_dims_by_aspect(cells, d), False
    if prior is None:
        continue

    if mid in DIM_OVERRIDES:
        dims = DIM_OVERRIDES[mid]
        by_connectivity += 1
    elif mid in FORCE_PRIOR_IDS:
        dims = prior
        if prior_is_exact:
            by_rtk_exact += 1
        else:
            guessed += 1
    else:
        dims = wall_connectivity_dims(p, cells, maxx, maxy, prior)
        if dims is not None:
            by_connectivity += 1
        else:
            dims = prior
            if prior_is_exact:
                by_rtk_exact += 1
            else:
                guessed += 1

    if not name:
        name = f"Map {mid}"
        unnamed += 1
    rows.append((mid, name, dims[0], dims[1]))

rows.sort()
os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, 'w', newline='', encoding='utf-8') as f:
    w = csv.writer(f)
    w.writerow(['id', 'name', 'xs', 'ys'])
    w.writerows(rows)

print(f"wrote {len(rows)} client maps -> {OUT}")
print(f"  dims: {by_connectivity} by wall-connectivity, {by_rtk_exact} RTK-exact, {guessed} best-guess (aspect/square)   |   {unnamed} without an RTK name")
named = [r for r in rows if not r[1].startswith('Map ')]
print(f"  named: {len(named)}   e.g. " + ", ".join(f"{r[0]}={r[1]}" for r in named[:6]))
