#!/usr/bin/env python
r"""Propose Warps.csv rows for maps the RTK/CTK dumps never wired, from map geometry.

WHY. Warps.csv is the RTK SQL dump's warp table, and whole interior clusters (the Buya palace
rooms 4603/4606/4609/4610/4615, and ~500 more maps that have real terrain) have NO rows in it —
verified missing from both the RTK dump and the CTK closing dump, so the data does not exist
anywhere we can import. The rooms themselves tell us most of the answer: every candidate doorway
is visible in the .map file as either a passable opening on the map border or a door-object run
(DoorObjects.csv ids) with passable ground on exactly ONE side. What geometry cannot tell us is
the ASSIGNMENT in hub rooms — which of the courtyard's four north doors is the Throne Room — so
this tool auto-pairs only the forced cases and takes explicit --pair for the judgment calls.
Output is a REVIEW file, not an edit: rows go into re/warp_proposals*.csv with Confidence/Note
columns; strip the last two columns and append to game-data/Warps.csv yourself, then @reload.

WHAT COUNTS AS AN EXIT (matches HandleWalk's warp-precedence semantics):
  * an edge run — consecutive passable (pass==0) tiles on the map border. Runs wider than
    --max-run (default 8) are reported but never paired: street-wide seams belong to the big
    outdoor maps, which are already wired.
  * a door run — consecutive DoorObjects.csv door tiles with passable ground on exactly one
    side (its FRONT). Passable on both sides is EITHER an interior door within one map (both
    sides open into the room — excluded) OR a VESTIBULE doorway: a dead-end pocket a tile or
    two deep behind the door, which is where these maps put the warp tile (Buya Courtyard's
    five north doors are exactly this; compare wired Buya 330 (72-74,52), the threshold row
    under the arch). A pocket that small can serve no other purpose, so the run is kept as an
    exit with the POCKET tiles as its warp sources. On the border a door is part of an edge
    run instead. Warp tiles beat collision in HandleWalk, so pass=3 under a door graphic or a
    threshold is expected and fine.
  * tiles already claimed are excluded: existing Warps.csv sources, and the scripted-tile
    systems that must NOT get a competing warp row (the warp branch runs first in HandleWalk
    and would eat the step — see docs/common/Modding.md): ArenaDoors, EventCaves, MythicCaves,
    WorldMapDests. Maps referenced by PathHalls/FallRooms are flagged in the listing instead —
    their scripted tiles live in code, not CSV, so eyeball those before trusting a run.

ROW GENERATION for a pair A<->B (both legs, matching the file's existing conventions, e.g.
rows 5621-5691): sources are the run's own tiles; the destination is the OTHER run's front
tile (one step inside, never on the return run itself, so no instant bounce); runs of unequal
width scale-map with clamping exactly like the Market Entry rows do.

USAGE
    python re/propose_warps.py --scan                        # cluster report for the whole registry
    python re/propose_warps.py --cluster 4600-4618           # list a cluster's exits (labels e0,e1..)
    python re/propose_warps.py --cluster 4600-4618 --auto    # + pair the forced cases
    python re/propose_warps.py --cluster 4600-4618 --auto \
        --pair 4604.e1=4603.e0 --pair 4604.e2=4606.e0        # + your assignments for the rest
Exit labels are stable (sorted by kind/side/coords) — read them off the listing first.
"""
import argparse, csv, os, sys
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
DATA = os.path.join(ROOT, "game-data")
MAPS_DIR = os.environ.get("P1998_MAPS", os.path.join(DATA, "maps"))

SIDES = ("N", "E", "S", "W")


# ---------------------------------------------------------------- registry loads

def rows_of(name):
    path = os.path.join(DATA, name)
    if not os.path.exists(path):
        return None, []
    with open(path, newline="", encoding="utf-8-sig") as f:
        rows = [r for r in csv.reader(f) if r and not r[0].lstrip().startswith("#")]
    return rows[0], rows[1:]


def parse_tiles(spec):
    """'13:0;14:0' -> [(13,0),(14,0)]"""
    out = []
    for part in str(spec).split(";"):
        part = part.strip()
        if ":" in part:
            x, y = part.split(":", 1)
            if x.strip().isdigit() and y.strip().isdigit():
                out.append((int(x), int(y)))
    return out


def load_registry():
    _, idx = rows_of("map_index.csv")
    index = {int(r[0]): (r[1], int(r[2]), int(r[3])) for r in idx}

    _, maps = rows_of("Maps.csv")
    names = {int(r[0]): r[1] for r in maps}

    _, warps = rows_of("Warps.csv")
    warp_src = defaultdict(set)     # map -> {(x,y)} existing source tiles
    wired = set()
    max_id = 0
    for r in warps:
        wid, sm, sx, sy, dm = int(r[0]), int(r[1]), int(r[2]), int(r[3]), int(r[4])
        warp_src[sm].add((sx, sy))
        wired.add(sm); wired.add(dm)
        max_id = max(max_id, wid)

    # scripted tiles that must not get a competing warp row
    claimed = defaultdict(set)      # map -> {(x,y)}
    hdr, rws = rows_of("ArenaDoors.csv")
    for r in rws:
        for t in parse_tiles(r[hdr.index("Tiles")]):
            claimed[int(r[hdr.index("Map")])].add(t)
    for fname in ("EventCaves.csv", "MythicCaves.csv"):
        hdr, rws = rows_of(fname)
        for r in rws:
            for t in parse_tiles(r[hdr.index("EntranceTiles")]):
                claimed[int(r[hdr.index("EntranceMap")])].add(t)
    hdr, rws = rows_of("WorldMapDests.csv")
    for r in rws:
        claimed[int(r[hdr.index("Map")])].add((int(r[hdr.index("X")]), int(r[hdr.index("Y")])))

    # maps whose scripted tiles live in code — flag, can't exclude precisely
    flagged = set()
    hdr, rws = rows_of("PathHalls.csv")
    for r in rws:
        for c in ("HallMap", "GuildMap", "SanctumUnaligned", "SanctumKwisin", "SanctumMingken", "SanctumOhaeng"):
            if c in hdr and r[hdr.index(c)].isdigit():
                flagged.add(int(r[hdr.index(c)]))
    hdr, rws = rows_of("FallRooms.csv")
    for r in rws:
        for part in r[hdr.index("SrcMaps")].split(";"):
            if part.strip().isdigit():
                flagged.add(int(part))

    # door-object id predicate
    hdr, rws = rows_of("DoorObjects.csv")
    exact, ranges = set(), []
    for r in rws:
        if r[0] == "map":
            exact.add(int(r[1]))
        elif r[0] == "delta":
            ranges.append((int(r[1]), int(r[2])))

    def is_door(o):
        return o in exact or any(lo <= o <= hi for lo, hi in ranges)

    return index, names, warp_src, wired, claimed, flagged, is_door, max_id


# ---------------------------------------------------------------- map geometry

class Exit:
    """One candidate doorway: a run of tiles plus the FRONT direction (into this map's interior).
    tiles are sorted ascending (x for horizontal runs, y for vertical)."""
    def __init__(self, kind, side, tiles, front):
        self.kind, self.side, self.tiles, self.front = kind, side, tiles, front  # front = (dx,dy)
        self.label = None

    def __repr__(self):
        a, b = self.tiles[0], self.tiles[-1]
        span = f"({a[0]},{a[1]})" if a == b else f"({a[0]},{a[1]})-({b[0]},{b[1]})"
        return f"{self.label or '?'} {self.kind}/{self.side} {span} w={len(self.tiles)}"


def load_map(mid, index):
    if mid not in index:
        return None
    _, xs, ys = index[mid]
    p = os.path.join(MAPS_DIR, f"TK{mid}.map")
    if not os.path.exists(p):
        return None
    d = open(p, "rb").read()
    if len(d) < xs * ys * 4:
        return None
    ps = bytearray(xs * ys)
    ob = [0] * (xs * ys)
    for i in range(xs * ys):
        g = d[i * 4] | (d[i * 4 + 1] << 8)
        ps[i] = (g >> 14) & 3
        ob[i] = (d[i * 4 + 2] | (d[i * 4 + 3] << 8)) & 0x3FFF
    return xs, ys, ps, ob


def group_runs(tiles):
    """Group tiles into maximal horizontal or vertical runs of adjacent cells."""
    tiles = sorted(tiles)
    runs, used = [], set()
    tset = set(tiles)
    for t in tiles:
        if t in used:
            continue
        for dx, dy in ((1, 0), (0, 1)):
            if (t[0] + dx, t[1] + dy) in tset or (t[0] - dx, t[1] - dy) not in tset:
                run = [t]
                while (run[-1][0] + dx, run[-1][1] + dy) in tset:
                    run.append((run[-1][0] + dx, run[-1][1] + dy))
                if len(run) > 1 or (dx, dy) == (0, 1):     # singles fall to the vertical pass
                    if not any(c in used for c in run):
                        runs.append(run)
                        used.update(run)
                    break
    return runs


def find_exits(mid, index, warp_src, claimed, is_door, max_run):
    m = load_map(mid, index)
    if m is None:
        return None, []
    xs, ys, ps, ob = m
    passable = lambda x, y: 0 <= x < xs and 0 <= y < ys and ps[y * xs + x] == 0
    skip = warp_src.get(mid, set()) | claimed.get(mid, set())
    notes = []

    exits = []
    # ---- edge runs -------------------------------------------------------------
    edge_tiles = set()
    for side, cells, front in (
        ("N", [(x, 0) for x in range(xs)], (0, 1)),
        ("S", [(x, ys - 1) for x in range(xs)], (0, -1)),
        ("W", [(0, y) for y in range(ys)], (1, 0)),
        ("E", [(xs - 1, y) for y in range(ys)], (-1, 0)),
    ):
        run = []
        for c in cells + [None]:
            if c is not None and passable(*c) and c not in skip:
                run.append(c)
                continue
            if run:
                if len(run) > max_run:
                    notes.append(f"{side} edge opening {len(run)} wide skipped (> --max-run)")
                else:
                    exits.append(Exit("edge", side, list(run), front))
                    edge_tiles.update(run)
                run = []

    # ---- door runs -------------------------------------------------------------
    def pocket(run, d):
        """The dead-end pocket behind a door run in direction d, or None if the region is
        open floor (reachable area grows past a vestibule's size) or spills off the map."""
        seed = [(c[0] + d[0], c[1] + d[1]) for c in run if passable(c[0] + d[0], c[1] + d[1])]
        door = set(run)
        seen, frontier = set(seed), list(seed)
        limit = 2 * len(run) + 4
        while frontier:
            if len(seen) > limit:
                return None
            x, y = frontier.pop()
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                n = (x + dx, y + dy)
                if n in seen or n in door or not passable(*n):
                    continue
                seen.add(n); frontier.append(n)
        return seed if seed else None

    doors = [(i % xs, i // xs) for i in range(xs * ys)
             if ob[i] and is_door(ob[i]) and (i % xs, i // xs) not in skip]
    for run in group_runs(doors):
        if any(c in edge_tiles for c in run):
            continue                                     # border door: already an edge run
        horiz = len(run) == 1 or run[0][1] == run[1][1]
        f1, f2 = ((0, 1), (0, -1)) if horiz else ((1, 0), (-1, 0))
        open1 = all(passable(c[0] + f1[0], c[1] + f1[1]) for c in run)
        open2 = all(passable(c[0] + f2[0], c[1] + f2[1]) for c in run)
        if open1 == open2:
            if not open1:
                continue                                 # decor: no way to face it
            p1, p2 = pocket(run, f1), pocket(run, f2)    # passable both sides: vestibule or interior?
            if (p1 is None) == (p2 is None):
                notes.append(f"interior door run at {run[0]} (passable both sides) — not an exit")
                continue
            pock, front = (p1, f2) if p1 is not None else (p2, f1)  # sources in pocket, front = open side
            if any(c in skip for c in pock):
                continue                                 # pocket already carries a warp/scripted tile
            side = {(0, 1): "N", (0, -1): "S", (1, 0): "W", (-1, 0): "E"}[front]
            exits.append(Exit("vestibule", side, sorted(pock), front))
            continue
        front = f1 if open1 else f2
        side = {(0, 1): "N", (0, -1): "S", (1, 0): "W", (-1, 0): "E"}[front]
        exits.append(Exit("door", side, run, front))

    exits.sort(key=lambda e: (e.kind, SIDES.index(e.side), e.tiles[0][1], e.tiles[0][0]))
    for i, e in enumerate(exits):
        e.label = f"e{i}"
    return (xs, ys, ps), (exits, notes)


def front_tiles(ex, geo, depth=1):
    """The arrival tiles one step (or more) inside the map from an exit run."""
    xs, ys, ps = geo
    out = []
    for x, y in ex.tiles:
        fx, fy = x + ex.front[0] * depth, y + ex.front[1] * depth
        out.append((fx, fy, 0 <= fx < xs and 0 <= fy < ys and ps[fy * xs + fx] == 0))
    return out


# ---------------------------------------------------------------- pairing + rows

def scaled(i, n, m):
    """Index into m destination tiles for source i of n, clamped/scaled like rows 5682-5691."""
    if n <= 1 or m <= 1:
        return min(i, m - 1)
    return min(m - 1, round(i * (m - 1) / (n - 1)))


def make_rows(a, ea, geo_a, b, eb, geo_b, next_id, confidence, note):
    rows = []
    for (src_map, src_ex, geo_src, dst_map, dst_ex, geo_dst) in (
            (a, ea, geo_a, b, eb, geo_b), (b, eb, geo_b, a, ea, geo_a)):
        arrivals, depth = None, 1
        while depth <= 3:
            cand = front_tiles(dst_ex, geo_dst, depth)
            if all(ok for _, _, ok in cand):
                arrivals = [(x, y) for x, y, _ in cand]
                break
            depth += 1
        warn = ""
        if arrivals is None:
            arrivals = [(x, y) for x, y, _ in front_tiles(dst_ex, geo_dst, 1)]
            warn = " !! arrival tile not passable — check by hand"
        n, m = len(src_ex.tiles), len(arrivals)
        for i, (sx, sy) in enumerate(src_ex.tiles):
            dx, dy = arrivals[scaled(i, n, m)]
            rows.append([next_id, src_map, sx, sy, dst_map, dx, dy, confidence, note + warn])
            next_id += 1
    return rows, next_id


def auto_pairs(cluster_exits):
    """The forced case only: exactly two maps in play, one unwired exit each."""
    open_exits = [(m, e) for m, (e_list, _) in cluster_exits.items() for e in e_list]
    per_map = defaultdict(list)
    for m, e in open_exits:
        per_map[m].append(e)
    if len(per_map) == 2 and all(len(v) == 1 for v in per_map.values()):
        (ma, ea), (mb, eb) = [(m, v[0]) for m, v in per_map.items()]
        return [(ma, ea, mb, eb, "auto", f"only unwired exit of {ma} <-> only unwired exit of {mb}")]
    return []


# ---------------------------------------------------------------- modes

def parse_cluster(spec):
    ids = []
    for part in spec.split(","):
        part = part.strip()
        if "-" in part:
            lo, hi = part.split("-", 1)
            ids.extend(range(int(lo), int(hi) + 1))
        elif part:
            ids.append(int(part))
    return ids


def cmd_cluster(args, reg):
    index, names, warp_src, wired, claimed, flagged, is_door, max_id = reg
    ids = [m for m in parse_cluster(args.cluster) if m in names]
    geo, exits = {}, {}
    for mid in ids:
        g, ex = find_exits(mid, index, warp_src, claimed, is_door, args.max_run)
        if g is None:
            continue
        geo[mid] = g
        exits[mid] = ex

    print(f"cluster {args.cluster}: {len(exits)} map(s) with terrain "
          f"({len(ids) - len(exits)} skipped — no .map file / not in map_index)")
    for mid in sorted(exits):
        e_list, notes = exits[mid]
        tag = " [WIRED]" if mid in wired else ""
        tag += " [SCRIPTED-TILE MAP — verify]" if mid in flagged else ""
        print(f"\n  {mid} {names[mid]}{tag}")
        for e in e_list:
            print(f"    {e!r}")
        for n in notes:
            print(f"    ({n})")
        if not e_list and not notes:
            print("    no unwired exits")

    pairs = []
    if args.auto:
        pairs += auto_pairs(exits)
    for spec in args.pair or []:
        try:
            lhs, rhs = spec.split("=")
            ma, la = lhs.split("."); mb, lb = rhs.split(".")
            ma, mb = int(ma), int(mb)
            ea = next(e for e in exits[ma][0] if e.label == la)
            eb = next(e for e in exits[mb][0] if e.label == lb)
        except (ValueError, KeyError, StopIteration):
            sys.exit(f"--pair {spec}: use <map>.<label>=<map>.<label> with labels from the listing above")
        pairs.append((ma, ea, mb, eb, "assigned", f"--pair {spec}"))

    if not pairs:
        if args.auto:
            print("\nno forced pairing (more than two open exits) — assign with --pair <map>.<eN>=<map>.<eN>")
        return

    rows, next_id = [], max_id + 1
    for ma, ea, mb, eb, conf, note in pairs:
        r, next_id = make_rows(ma, ea, geo[ma], mb, eb, geo[mb], next_id, conf, note)
        rows += r

    out = args.out or os.path.join(HERE, f"warp_proposals_{args.cluster.replace(',', '_')}.csv")
    with open(out, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["WarpId", "SourceMapId", "SourceX", "SourceY",
                    "DestinationMapId", "DestinationX", "DestinationY", "Confidence", "Note"])
        w.writerows(rows)
    print(f"\n{len(pairs)} pair(s) -> {len(rows)} proposed rows (ids {max_id + 1}..{next_id - 1}) -> {out}")
    print("review, drop the last two columns, append to game-data/Warps.csv, then @reload")
    for r in rows:
        print("   " + ",".join(str(v) for v in r[:7]) + f"   # {r[7]}: {r[8]}")


def cmd_scan(args, reg):
    index, names, warp_src, wired, claimed, flagged, is_door, max_id = reg
    have_file = lambda m: os.path.exists(os.path.join(MAPS_DIR, f"TK{m}.map"))
    orphans = sorted(m for m in names if m not in wired and m in index and have_file(m))
    print(f"{len(orphans)} maps have terrain but no Warps.csv row at all")

    # id-contiguous clusters (gap <= 6), plus wired maps inside each span
    clusters, cur = [], []
    for m in orphans:
        if cur and m - cur[-1] > 6:
            clusters.append(cur); cur = []
        cur.append(m)
    if cur:
        clusters.append(cur)

    lines = []
    for cl in clusters:
        span = sorted(set(cl) | {m for m in names if cl[0] <= m <= cl[-1] and m in wired and m in index and have_file(m)})
        entry = [f"\n== {cl[0]}-{cl[-1]}  ({len(cl)} unwired / {len(span)} in span)"]
        n_exits = 0
        for m in span:
            _, ex = find_exits(m, index, warp_src, claimed, is_door, args.max_run)
            e_list, notes = ex if ex else ([], [])
            n_exits += len(e_list)
            tag = "wired" if m in wired else "ORPHAN"
            tag += ", scripted" if m in flagged else ""
            entry.append(f"  {m:>5} {names[m][:34]:<34} [{tag}] "
                         f"unwired exits: {len(e_list)}" + (f" ({'; '.join(map(repr, e_list))})" if e_list else ""))
        entry.append(f"  -> {n_exits} open exits in cluster"
                     + ("  [two-map forced pair candidate]" if len(cl) <= 2 and n_exits == 2 else ""))
        lines += entry

    report = os.path.join(HERE, "warp_scan_report.txt")
    with open(report, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print(f"{len(clusters)} clusters -> full report in {report}")
    print("\n".join(lines[:60]))
    if len(lines) > 60:
        print(f"... ({len(lines) - 60} more lines in the report file)")


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--scan", action="store_true", help="cluster report over the whole registry")
    ap.add_argument("--cluster", help="map ids to work on: '4600-4618' or '4603,4604,4606'")
    ap.add_argument("--auto", action="store_true", help="pair the forced (two-map) case automatically")
    ap.add_argument("--pair", action="append", metavar="A.eN=B.eN", help="explicit exit assignment (repeatable)")
    ap.add_argument("--max-run", type=int, default=8, help="ignore edge openings wider than this (default 8)")
    ap.add_argument("--out", help="proposals csv path (default re/warp_proposals_<cluster>.csv)")
    args = ap.parse_args()

    reg = load_registry()
    if args.cluster:
        cmd_cluster(args, reg)
    elif args.scan:
        cmd_scan(args, reg)
    else:
        ap.error("pick --scan or --cluster")


if __name__ == "__main__":
    main()
