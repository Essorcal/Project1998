#!/usr/bin/env python
"""Convert RTK/TKR (7.x) Buya Library Caverns maps into 4.95-client TK<id>.map terrain.

RTK rtkmaps/Accepted/TK00<id>.map format (verified 2026-08):
  [u16 xs BE][u16 ys BE] then xs*ys cells of 6 bytes = [ground u16 BE][passable u16 BE][object u16 BE]

4.95 client TK<id>.map format (headerless, see Server/MapData.cs Load):
  xs*ys cells of 4 bytes = [ground16 LE][object16 LE], where ground16 = (pass<<14)|(tile & 0x3FFF)
  pass: 0 = walkable, 3 = blocked.

Findings that drive the remap (see conversation): the OBJECT layer (walls/structure) is 100% inside
the 4.x shared tile range and passes through verbatim. ~25% of GROUND (floor) cells use 7.x-only tiles
(>16383, unrepresentable in the 4.x 14-bit ground field); those are replaced with the map's own dominant
IN-RANGE ground tile so the floor stays coherent. RTK passable {0,1} -> 4.x {0,3}.

Emits TK<id>.map into game-data/maps and prints (id,name,xs,ys) rows for map_index.csv.
"""
import os, glob, struct, collections

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
SRC  = os.path.join(REPO, "RTK-Server", "rtkmaps", "Accepted")
OUT  = os.path.join(REPO, "game-data", "maps")
MAXTILE = 0x3FFF  # 16383 – 4.x ground field ceiling

# Width-crop overrides (none currently — see VOID_FILL). Kept as a hook.
CROP_WIDTH = {}

# Void reconstruction: a few RTK rooms ship PARTIAL — cells with neither ground nor object, rendering
# as black holes. Originally filled from the room's dominant floor tile; superseded for every room that
# has a NATIVE_SOURCE below. Kept for any remaining RTK-only partial room.
VOID_FILL = {mid + off for off in (1, 6) for mid in (6502, 6522, 6542, 6562, 6582)}

# TRUE 4.x sources: the caverns' first 10 room layouts exist NATIVELY in the 4.x dump as maps 410-419,
# mislabeled with desert names (Sand Glen, Sting, Venom Den, ...). Verified by object-layer fingerprint
# (97.7-100% agreement; objects are the strong signal — pass/tile diffs are 7.x-era RTK edits). These are
# copied verbatim, which also heals the RTK void holes in 6503/6504/6508 with era-correct content.
# Rooms +10 (Sonhi guard), +11..+16 and +17 (Gloth) have no native source and stay RTK-converted.
NATIVE_SOURCE = {}
for _base in (6502, 6522, 6542, 6562, 6582):
    for _off, _nat in ((0, 416), (1, 414), (2, 413), (3, 418), (4, 417),
                       (5, 411), (6, 415), (7, 412), (8, 410), (9, 419)):
        NATIVE_SOURCE[_base + _off] = _nat
NATIVE_DIMS = {416: (30, 30), 414: (30, 28), 413: (24, 36), 418: (30, 28), 417: (24, 30),
               411: (30, 30), 415: (30, 30), 412: (30, 30), 410: (30, 30), 419: (28, 28)}

# Explicit 7.x->4.x ground overrides for tiles the Rosetta gets wrong because their local neighbourhood is
# too noisy for band inference (confirmed against the confirmed-correct 17540 edge-wall band + user reports).
GROUND_OVERRIDE = {
    19386: 1846,   # edge wall: was 1220 (re-tile noise); neighbours 19385->1845, 19405->1865 fix delta 17540
}

def build_ground_remap():
    """Rosetta table: 7.x ground tile index -> true 4.x index, learned from maps that exist in BOTH the 4.x
    client set and rtkmaps. Same-map is confirmed by passability agreement (format-defined, so immune to the
    tile renumber itself); for confirmed pairs, cell i's (7.x ground, 4.x ground) is a correspondence sample.
    The 7.x client shifted a band of floor tiles to high indices (>16383); this recovers the shift per-tile
    (it is NOT a single constant offset). ~45% of cells were genuinely re-tiled between versions, so we take
    the DOMINANT 4.x target per 7.x tile and keep only entries with >=2 observations."""
    import glob as _g, collections
    fourx = {int(os.path.basename(p)[2:-4]): os.path.getsize(p)//4
             for p in _g.glob(os.path.join(OUT, "TK*.map")) if os.path.basename(p)[2:-4].isdigit()}
    votes = collections.defaultdict(collections.Counter)
    for p in _g.glob(os.path.join(SRC, "TK*.map")):
        num = os.path.basename(p)[2:-4]
        if not num.isdigit():
            continue
        mid = int(num)
        if 6502 <= mid <= 6599 or mid not in fourx:   # skip the caverns themselves (circular)
            continue
        d = open(p, "rb").read()
        xs, ys = struct.unpack(">HH", d[:4]); n = xs*ys
        if xs*ys != fourx[mid] or len(d) < 4 + n*6:
            continue
        c = open(os.path.join(OUT, f"TK{mid}.map"), "rb").read()
        g7 = [0]*n; p7 = [0]*n
        g4 = [0]*n; p4 = [0]*n
        for i in range(n):
            a, b, o = struct.unpack(">HHH", d[4+i*6:10+i*6]); g7[i], p7[i] = a, b
            w = c[i*4] | (c[i*4+1] << 8); g4[i], p4[i] = w & 0x3FFF, (w >> 14) & 3
        if sum(1 for i in range(n) if (p4[i] != 0) == (p7[i] != 0)) / n < 0.95:
            continue
        for i in range(n):
            if g7[i] and g4[i]:
                votes[g7[i]][g4[i]] += 1
    # Strong entries: a dominant 4.x target seen at least twice.
    remap = {}
    for t, c in votes.items():
        tgt, cnt = c.most_common(1)[0]
        if cnt >= 2:
            remap[t] = tgt

    # Band-consistency correction (high tiles only). Floor tiles sit in atlas bands with a locally-constant
    # (7.x - 4.x) offset; the ~45% re-tile noise occasionally makes a WRONG 4.x target out-vote the
    # band-correct one (e.g. 19369 got 1216 with 5 votes over the band-correct 1829 with 4 -> wrong-color
    # edge wall). For each high tile, take the offset its well-observed neighbours agree on and, if that
    # yields a candidate the tile ITSELF was also observed mapping to, trust the band over the raw plurality.
    def band_delta(t):
        ds = collections.Counter()
        for nb in range(t-4, t+5):
            if nb == t or nb not in votes:
                continue
            obs = sum(votes[nb].values())
            if obs >= 5:
                ds[nb - votes[nb].most_common(1)[0][0]] += obs
        return ds.most_common(1)[0][0] if ds else None
    corrected = 0
    for t in list(votes):
        if t <= MAXTILE:
            continue
        d = band_delta(t)
        if d is None:
            continue
        cand = t - d
        if 0 < cand <= MAXTILE and remap.get(t) != cand and votes[t].get(cand, 0) > 0:
            remap[t] = cand
            corrected += 1
    # High tiles fall in bands with a locally-constant (7.x - 4.x) offset. Recover rare (cnt==1) and
    # cavern-only (never-aligned) high tiles by borrowing an adjacent strong tile's delta and validating.
    # Two passes so a delta can propagate across a short gap. Only touches the >16383 band.
    for _ in range(3):
        for t in range(16384, 20481):
            if t in remap:
                continue
            for nb in (t-1, t+1, t-2, t+2, t-3, t+3):
                if nb in remap:
                    delta = nb - remap[nb]
                    cand = t - delta
                    if 0 < cand <= MAXTILE:
                        # if we also have a (weak) direct observation, prefer it only when it agrees
                        if t in votes and votes[t].most_common(1)[0][0] != cand:
                            continue
                        remap[t] = cand
                    break
    remap.update(GROUND_OVERRIDE)   # hand-confirmed fixes win over everything
    return remap

REMAP = None  # lazily built once

def convert(path, mid):
    d = open(path, "rb").read()
    xs, ys = struct.unpack(">HH", d[:4])
    src_xs = xs
    if len(d) < 4 + xs * ys * 6:
        return None
    # optional right-margin crop (RTK partial rooms): keep the first CROP_WIDTH columns of each row
    keep_w = CROP_WIDTH.get(mid, xs)
    n = keep_w * ys
    ground = [0]*n; passv = [0]*n; obj = [0]*n
    for row in range(ys):
        for col in range(keep_w):
            g, p, o = struct.unpack(">HHH", d[4 + (row*src_xs + col)*6 : 10 + (row*src_xs + col)*6])
            i = row*keep_w + col
            ground[i], passv[i], obj[i] = g, p, o
    xs = keep_w
    # dominant in-range ground tile → fill value for the out-of-range 25%
    fill_c = collections.Counter(g for g in ground if 0 < g <= MAXTILE)
    fill = fill_c.most_common(1)[0][0] if fill_c else 0
    do_void = mid in VOID_FILL
    restored = filled = voidfilled = 0
    out = bytearray()
    for i in range(n):
        g = ground[i]
        if do_void and g == 0 and obj[i] == 0:   # reconstruct black hole as walkable floor
            g = fill; passv[i] = 0; voidfilled += 1
        if g > MAXTILE:
            if REMAP and g in REMAP:             # true 4.x tile recovered from the Rosetta table
                g = REMAP[g]; restored += 1
            else:                                # no correspondence -> blend with the room's own floor
                g = fill; filled += 1
        p = 3 if passv[i] else 0                 # RTK 1=blocked -> 4.x 3; 0 -> walkable
        g16 = (p << 14) | (g & 0x3FFF)
        o16 = obj[i] & 0x3FFF
        out += struct.pack("<HH", g16, o16)
    return xs, ys, bytes(out), restored, filled, voidfilled, n

def main():
    global REMAP
    os.makedirs(OUT, exist_ok=True)
    print("building 7.x->4.x ground Rosetta from shared maps...")
    REMAP = build_ground_remap()
    print(f"  learned {len(REMAP)} tile correspondences\n")
    rows = []
    tot_restored = tot_filled = 0
    for p in sorted(glob.glob(os.path.join(SRC, "TK0065*.map"))):
        mid = int(os.path.basename(p)[2:-4])   # TK006503 -> 6503
        if mid in NATIVE_SOURCE:               # true 4.x map exists — copy it verbatim, skip conversion
            src = NATIVE_SOURCE[mid]
            data = open(os.path.join(OUT, f"TK{src}.map"), "rb").read()
            with open(os.path.join(OUT, f"TK{mid}.map"), "wb") as f:
                f.write(data)
            xs, ys = NATIVE_DIMS[src]
            assert len(data) == xs * ys * 4, (mid, src, len(data))
            rows.append((mid, xs, ys))
            print(f"  TK{mid}: {xs}x{ys}  — NATIVE copy of TK{src}")
            continue
        r = convert(p, mid)
        if not r:
            print(f"  SKIP {mid}: short file"); continue
        xs, ys, data, restored, filled, voidfilled, n = r
        with open(os.path.join(OUT, f"TK{mid}.map"), "wb") as f:
            f.write(data)
        rows.append((mid, xs, ys))
        tot_restored += restored; tot_filled += filled
        parts = []
        if restored: parts.append(f"restored {restored}")
        if filled: parts.append(f"filled {filled}")
        if voidfilled: parts.append(f"VOID-reconstructed {voidfilled}")
        print(f"  TK{mid}: {xs}x{ys}  {n} cells — {', '.join(parts) if parts else 'clean'}")
    print(f"\nwrote {len(rows)} cavern maps to {OUT}")
    tot = tot_restored + tot_filled
    if tot:
        print(f"broken floor cells: {tot_restored} restored to true 4.x tile ({100*tot_restored//tot}%), "
              f"{tot_filled} filled with room floor ({100*tot_filled//tot}%)")
    print("\n--- map_index.csv rows ---")
    for mid, xs, ys in rows:
        print(f"{mid},Buya Library Caverns,{xs},{ys}")

if __name__ == "__main__":
    main()
