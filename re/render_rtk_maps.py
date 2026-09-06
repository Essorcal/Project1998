"""Render the RTK (7.x reference server) maps into the map viewer, ISOLATED from our own maps.

WHY SEPARATE: RTK is a later, re-tiled 7.x fork (see memory/nexustk-rtk-vs-client-maps). Its rooms
often differ from the 4.95 client's for the SAME map id — different tiles, different geometry, rooms
we do not ship at all. Mixing the two sets in one gallery would make it impossible to tell which
world you are looking at, so everything here writes to `assets/rtk/` and defines its OWN globals
(`window.RTK_MAPS`, and `window.RTK_OVERLAY` from gen_map_overlay.py --rtk). Nothing overwrites the
official set.

RTK .map FORMAT (differs from the 4.x client's headerless 4-byte cells):
    u16 xs, u16 ys              BIG-ENDIAN header
    then xs*ys * (u16 ground, u16 pass, u16 object)   all BIG-ENDIAN  -- 6 bytes per cell
The 4.x client file is 4 bytes/cell with no header and little-endian words; do not confuse them.
Confirmed against re/rtk_cavern_to_4x.py, which already parses this format.

ART: two sets are rendered and KEPT SIDE BY SIDE, because the difference between them is the point.
  assets/rtk/         5.33 Tile.dat, 24px. ~295k ground cells have no art and render black -- that
                      black is exactly the 7.x-only content our own era's client cannot draw.
  assets/rtk-modern/  the modern split-archive client (LatestTileSet), 48px. 0 unresolved cells.
All of them index the same frame space; each later client simply appends to it. --data swaps the
legacy archive (OTK 5.56 sits between the two: 29,972 ground frames, recovers ~21% of the black). That is not a compromise — RTK's
ground words index the same extended sheet 5.33 ships, and a spot render of RTK's Kugnae comes out
coherent (river, bridge, roofs, shop signs). Two known gaps, reported by --stats:
  * 5.2% of all ground cells reference a frame beyond 5.33's 28,551 -> those cells draw BLACK.
    RTK maps reach tile id 47,907, so ~19,400 frames of 7.x art are simply absent here. The gap is
    CLUSTERED, not spread: most maps are clean, but 152 of 3,250 are more than half black because
    their room is built from a handful of high-id tiles (e.g. 3712 Foxy Hole, 324/324 cells from 10
    missing ids). Use `--stats --only <id>` to see which ids a given map is missing.
  * ~7.5% of object frames RTK asks for are beyond 5.33's TILEC -> those pieces are missing.
    Only a later 7.x client's Tile.dat would fill either gap; this box has 4.x and 5.33 only.
Both are 7.x art we do not have a client for; they show up as holes, never as wrong tiles.

EMPTY MARGINS: RTK's editor allocates a map at one size and fills a smaller rectangle, so 533 maps
carry an all-zero right/bottom margin that renders as a black bar (3605 declares 23x23 for an 18x16
room). Those bars are NOT missing art — the file has no tile there. They are rendered AS-IS and the
map keeps its declared size. An earlier version trimmed them, which made the viewer report 3605 as
18x16; a map's declared size is its size, and a wrong dimension is worse than an honest black
margin. Do not re-add cropping.

OBJECTS: uses **RTK's own SObj.tbl** (`RTK-Server/rtk/SObj.tbl`, 18,954 records vs 5.33's 12,696).
Every object id RTK's maps use is in range there; 5.33's table would silently drop the top third.

CLIENT-ERA TAGS: each map in the index carries `t` = ["4.x","5.x"] / ["5.x"] / ["modern"], the
oldest client that owns every tile and object the map names, plus `b4`/`b5` = how many cells the
4.95 / 5.33 clients are missing. A map whose TERRAIN is 4.x-drawable and that only misses later
OBJECTS also gets "4.x-props" (b4 is then the prop count). Stamped here at render time; re/tag_rtk_client_support.py --apply
re-stamps an existing index without re-rendering.

Usage:
    python re/render_rtk_maps.py all                        # modern art -> assets/rtk-modern/
    python re/render_rtk_maps.py all --legacy-art           # 5.33 art   -> assets/rtk/
    python re/render_rtk_maps.py one <id> [out.png] [--client]   # --client = full 7.x art, 48px
    python re/render_rtk_maps.py --stats                    # coverage report, renders nothing
    python re/render_rtk_maps.py --stats --only 3918,3712   # why IS THAT MAP black?
    python re/render_rtk_maps.py --learn-tiles              # rebuild the fallback table
    python re/render_rtk_maps.py all --legacy-art --fill-missing   # patch gaps with guesses
    python re/render_rtk_maps.py --check        # self-check
"""
import argparse
import importlib.util
import json
import os
import re
import struct
import sys
import time

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
RTK_MAPS = os.path.join(REPO, 'RTK-Server', 'rtkmaps', 'Accepted')
RTK_SOBJ = os.path.join(REPO, 'RTK-Server', 'rtk', 'SObj.tbl')
RTK_SQL = os.path.join(REPO, 'RTK-Server', 'database', '2020-09-02-21-55-01_RTK.sql.bak')
# TWO RTK sets, kept side by side ON PURPOSE so you can see what the older client cannot draw:
#   assets/rtk/         5.33-era art, 24px  -- the era our own server targets; ~295k cells come out
#                       black, and that black IS the finding (7.x-only content)
#   assets/rtk-modern/  modern client, 48px -- complete, 0 unresolved cells
# The output dir follows the art automatically; you cannot render one over the other by forgetting
# a flag (I did exactly that once).
OUTDIR_LEGACY = os.path.join(REPO, 're', 'mapviewer', 'assets', 'rtk')
OUTDIR_MODERN = os.path.join(REPO, 're', 'mapviewer', 'assets', 'rtk-modern')
OUTDIR = OUTDIR_LEGACY



_spec = importlib.util.spec_from_file_location('render_maps', os.path.join(HERE, 'render_maps.py'))
rm = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(rm)


# Tile art, richest first. OTK 5.56 is a strict SUPERSET of 5.33 -- verified append-only (402/402
# sampled shared ground frames byte-identical) -- with 29,972 ground frames against 28,551 and
# 32,585 object frames against 29,414. That extra art is 7.x-era, so it only matters here: it renders
# 21% of the ground cells RTK maps left black (63k) and recovers 50k object frames. Our own 4.95 maps
# resolve 100% on plain 5.33, so render_maps.py deliberately stays on it.
TILE_CANDIDATES = [
    'C:/Users/brian/Downloads/OTK-556.1/Tile.dat',
    rm.DEFAULT_DATA,
]


def best_tile_dat():
    """Legacy path defaults to plain 5.33 -- that is the era baseline the rtk/ set exists to show.
    Pass --data to use a richer 5.x archive instead (OTK 5.56 recovers ~21% of the black)."""
    return rm.DEFAULT_DATA


# ----------------------------------------------------------------------------- RTK data
def rtk_cells(path):
    """RTK .map -> (cells[n,2] = [ground, object], xs, ys), or None if the file is malformed."""
    d = open(path, 'rb').read()
    if len(d) < 4:
        return None
    xs, ys = struct.unpack('>HH', d[:4])
    n = xs * ys
    if n == 0 or len(d) < 4 + n * 6:
        return None
    a = np.frombuffer(d, dtype='>u2', count=n * 3, offset=4).reshape(n, 3)
    cells = np.empty((n, 2), np.uint16)
    cells[:, 0] = a[:, 0]          # ground word (flat index; RTK never sets the sheet-2 tag)
    cells[:, 1] = a[:, 2]          # object id   (a[:,1] is passability, not drawn)
    return cells, xs, ys


_TUPLE = re.compile(r"\(((?:[^()']|'(?:[^'\\]|\\.)*')*)\)")


def sql_rows(table, sql=None):
    """Pull one table's INSERT ... VALUES tuples out of RTK's mysqldump."""
    d = sql if sql is not None else open(RTK_SQL, 'rb').read().decode('utf8', 'replace')
    out = []
    for m in re.finditer(r'INSERT INTO `%s`[^\n]*?VALUES\s*(.*?);\s*\n' % table, d, re.S):
        out += _TUPLE.findall(m.group(1))
    return out


def sql_split(t):
    """Split a VALUES tuple on commas that are not inside a quoted string."""
    f, cur, q, i = [], '', False, 0
    while i < len(t):
        c = t[i]
        if q:
            if c == '\\':
                cur += t[i:i + 2]
                i += 2
                continue
            if c == "'":
                q = False
            cur += c
        elif c == "'":
            q = True
            cur += c
        elif c == ',':
            f.append(cur.strip())
            cur = ''
        else:
            cur += c
        i += 1
    f.append(cur.strip())
    return [x[1:-1] if len(x) > 1 and x[0] == "'" and x[-1] == "'" else x for x in f]


def rtk_map_names():
    """RTK's own Maps table: id -> name (9,850 rows, including ids the 4.95 client has no file for)."""
    names = {}
    for t in sql_rows('Maps'):
        f = sql_split(t)
        if f and f[0].lstrip('-').isdigit():
            names[int(f[0])] = f[1]
    return names


def rtk_tileset(data=None):
    """5.33 art, but RTK's SObj table — its object id space is a third larger than 5.33's."""
    ts = rm.TileSet(data or best_tile_dat())
    ts.objs = rm.TileSet._parse_sobj(open(RTK_SOBJ, 'rb').read())
    return ts


def map_files():
    out = {}
    for fn in os.listdir(RTK_MAPS):
        m = re.fullmatch(r'TK(\d+)\.map', fn)
        if m:
            out[int(m.group(1))] = os.path.join(RTK_MAPS, fn)
    return out


FALLBACK_CSV = os.path.join(HERE, 'rtk_tile_fallback.csv')


def load_fallback():
    """RTK tile id -> 5.33 frame, for ids past the end of 5.33's TILE.EPF. See learn_fallback()."""
    out = {}
    if not os.path.exists(FALLBACK_CSV):
        return out
    with open(FALLBACK_CSV, encoding='utf-8') as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith('#'):
                continue
            a, b = line.split(',')[:2]
            out[int(a)] = int(b)
    return out


def learn_fallback(ts):
    """Learn a 5.33 frame for RTK tile ids we have no art for, from rooms present in BOTH worlds.

    For a room the 4.95 client also ships, a cell where RTK uses an out-of-range id and we use a real
    tile is direct evidence of what belongs there. Both sets are anchored at (0,0), so the overlap
    compares cleanly even when the declared sizes differ. Kept only on >=3 observations with >=60%
    agreement, so one re-tiled room cannot invent a mapping.

    Coverage is inherently limited — most 7.x-only tiles only ever appear in RTK-only rooms, where
    there is nothing to compare against. It resolves ~3.6% of all black cells, but some individual
    rooms come back completely (3605 "Do Circle", whose whole wall is the single id 31339 -> 801).
    """
    from collections import Counter, defaultdict
    spec = importlib.util.spec_from_file_location('render_maps2', os.path.join(HERE, 'render_maps.py'))
    rm2 = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(rm2)
    dims, files = rm2.load_index(), map_files()
    votes = defaultdict(Counter)
    for mid in sorted(set(dims) & set(files)):
        r = rtk_cells(files[mid])
        if not r:
            continue
        c, rxs, rys = r
        _, oxs, oys = dims[mid]
        try:
            ours = rm2.map_cells(mid)
        except Exception:                       # noqa: BLE001
            continue
        if len(ours) < oxs * oys:
            continue
        w, h = min(rxs, oxs), min(rys, oys)
        if w < 4 or h < 4:
            continue
        rg = c[:, 0].reshape(rys, rxs)[:h, :w].ravel()
        og = ours[:oxs * oys, 0].reshape(oys, oxs)[:h, :w].ravel()
        m = rg >= ts.nground
        if not m.any():
            continue
        for a, word in zip(rg[m], og[m]):
            f = ts.ground_frame(int(word))
            if 0 < f < ts.nground:
                votes[int(a)][f] += 1
    rows = []
    for a, c in sorted(votes.items()):
        best, n = c.most_common(1)[0]
        if n >= 3 and n / sum(c.values()) >= 0.6:
            rows.append((a, best, n))
    with open(FALLBACK_CSV, 'w', encoding='utf-8') as f:
        f.write('# rtk_tile_id,tile533_frame,observations\n')
        f.write('# Generated by: python re/render_rtk_maps.py --learn-tiles\n')
        f.write('# What a 7.x-only ground tile should look like, learned from the same room in the\n')
        f.write('# 4.95 world. Only ids past 5.33 TILE.EPF frame %d appear here.\n' % (ts.nground - 1))
        for a, b, n in rows:
            f.write('%d,%d,%d\n' % (a, b, n))
    print('learned %d fallback tiles -> %s' % (len(rows), FALLBACK_CSV))



# ----------------------------------------------------------------------------- latest client (7.x)
# The modern client (e.g. C:/Users/brian/Desktop/NexusTK) stores tiles completely differently from
# the 4.x/5.x line, and it is the ONLY asset set that covers RTK's maps in full:
#   Data/tile.dat            metadata only -- SOBJ.TBL, TILE.PAL/TBL, TILEC.PAL/TBL (no pixels)
#   Data/tile0..23.dat       one TILE<n>.EPF each, 2000 frames per file (last 1912) = 47,912 ground
#   Data/tilec0..26.dat      one TILEC<n>.EPF each, 2000 per file            = 53,698 object frames
# so a global ground id maps to (tile<id//2000>.dat, frame id%2000). Tiles are 48x48, not 24x24, so
# these render at DOUBLE the resolution of the 5.x set.
#
# Why it fits RTK exactly: RTK's ground ids top out at 47,907 (< 47,912) and its SObj asks for TILEC
# frames up to 51,032 (< 53,698). RTK's own SObj.tbl is this client's, truncated -- 18,953 of the
# 18,954 shared records are byte-identical. Nothing is missing.
LATEST_CLIENT = 'C:/Users/brian/Desktop/NexusTK'
LATEST_CELL = 48


class LatestTileSet:
    """Tile art from the modern split-archive client. Frames are decoded lazily and cached."""

    def __init__(self, root=LATEST_CLIENT, sobj=None):
        self.data = os.path.join(root, 'Data')
        meta = rm.read_dat(os.path.join(self.data, 'tile.dat'))
        self.gpal = rm.load_palettes(meta['TILE.PAL'])
        self.gpi = rm.tbl_palettes(meta['TILE.TBL'])
        self.cpal = rm.load_palettes(meta['TILEC.PAL'])
        self.cpi = rm.tbl_palettes(meta['TILEC.TBL'])
        self.nground = len(self.gpi)
        self.ncobj = len(self.cpi)
        # RTK's own SObj by default: it is this client's table truncated, and it is RTK's data.
        self.objs = rm.TileSet._parse_sobj(open(sobj or RTK_SOBJ, 'rb').read())
        self._arch, self._g, self._c = {}, {}, {}

    def _epf(self, kind, n):
        key = (kind, n)
        if key not in self._arch:
            fn = '%s%d.dat' % (kind, n)
            arc = rm.read_dat(os.path.join(self.data, fn))
            name = '%s%d.EPF' % (kind.upper(), n)
            self._arch[key] = (arc[name],) + (rm.epf_entries(arc[name])[1],)
        return self._arch[key]

    def _decode(self, kind, gid, pals, pidx, cache):
        if gid in cache:
            return cache[gid]
        out = None
        if 0 < gid < len(pidx):
            epf, ents = self._epf(kind, gid // 2000)
            fi = gid % 2000
            if fi < len(ents):
                pal = pals[pidx[gid]] if pidx[gid] < len(pals) else (pals[0] if pals else None)
                if pal is not None:
                    out = rm.decode_frame(epf, ents, fi, pal)
        cache[gid] = out
        return out

    def ground_tile(self, gid):
        """Ground frame baked into a LATEST_CELL square (transparent left black)."""
        dec = self._decode('tile', gid, self.gpal, self.gpi, self._g)
        if dec is None:
            return None
        if isinstance(dec, np.ndarray):
            return dec
        left, top, w, h, rgb, a = dec
        img = np.zeros((LATEST_CELL, LATEST_CELL, 3), np.uint8)
        y1, x1 = min(top + h, LATEST_CELL), min(left + w, LATEST_CELL)
        if top < LATEST_CELL and left < LATEST_CELL and y1 > top and x1 > left:
            m = a[:y1 - top, :x1 - left]
            img[top:y1, left:x1][m] = rgb[:y1 - top, :x1 - left][m]
        self._g[gid] = img
        return img

    def cframe(self, fid):
        return self._decode('tilec', fid, self.cpal, self.cpi, self._c)


def render_latest(ts, cells, xs, ys):
    """Same painter as render_maps.render, at 48px cells and with lazily-decoded frames."""
    C = LATEST_CELL
    canvas = np.zeros((ys * C, xs * C, 3), np.uint8)
    n = min(len(cells), xs * ys)
    for i in range(n):
        g = int(cells[i, 0])
        if g <= 0:
            continue
        t = ts.ground_tile(g)
        if t is not None:
            cy, cx = divmod(i, xs)
            canvas[cy * C:cy * C + C, cx * C:cx * C + C] = t
    for i in range(n):
        z = int(cells[i, 1])
        if z == 0 or z >= len(ts.objs):
            continue
        cy, cx = divmod(i, xs)
        for k, fid in enumerate(ts.objs[z]):
            dec = ts.cframe(fid)
            if not dec:
                continue
            left, top, w, h, rgb, a = dec
            ry = cy - k                        # first frame on the anchor, column grows north
            y0, x0 = ry * C + top, cx * C + left
            sy0, sx0 = max(0, -y0), max(0, -x0)
            ey, ex = min(h, canvas.shape[0] - y0), min(w, canvas.shape[1] - x0)
            if ey <= sy0 or ex <= sx0:
                continue
            dst = canvas[y0 + sy0:y0 + ey, x0 + sx0:x0 + ex]
            m = a[sy0:ey, sx0:ex]
            dst[m] = rgb[sy0:ey, sx0:ex][m]
    return Image.fromarray(canvas)

# ----------------------------------------------------------------------------- client support
def client_support():
    """Tagger for "which client era can draw this map" -- see re/tag_rtk_client_support.py.
    Returns None if the 4.x client archive is not on this box; rendering still works, the maps just
    come out untagged (run tag_rtk_client_support.py --apply later to stamp them in)."""
    try:
        spec = importlib.util.spec_from_file_location(
            'tag_rtk', os.path.join(HERE, 'tag_rtk_client_support.py'))
        m = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(m)
        return m.Support()
    except Exception as e:                          # noqa: BLE001 - tags are a nicety, not the job
        print('  (no client-support tags: %s)' % e, flush=True)
        return None


# ----------------------------------------------------------------------------- reporting
def why_black(ts, ids):
    """Per-map breakdown of why cells render black: no data (id 0) vs no art (id past TILE.EPF)."""
    files, names = map_files(), rtk_map_names()
    print('%-6s %-22s %-9s %7s %7s %7s  %s' % (
        'id', 'name', 'dims', 'cells', 'void', 'no-art', 'missing tile ids'))
    for mid in ids:
        r = rtk_cells(files[mid]) if mid in files else None
        if not r:
            print('%-6d (no RTK map file)' % mid)
            continue
        cells, xs, ys = r
        g = cells[:, 0]
        oor = g[g >= ts.nground]
        u = sorted({int(v) for v in oor})
        print('%-6d %-22.22s %-9s %7d %7d %7d  %s%s' % (
            mid, names.get(mid, '?'), '%dx%d' % (xs, ys), len(g),
            int((g == 0).sum()), len(oor),
            ','.join(str(v) for v in u[:6]), ' ...' if len(u) > 6 else ''))
    print('')
    print('void   = ground word 0, the map genuinely has no tile there')
    print('no-art = a real tile id we have no frame for; this tileset stops at %d' % (ts.nground - 1))


def stats(ts, sample=60):
    import random
    files = sorted(map_files().items())
    random.seed(1)
    pick = random.sample(files, min(sample, len(files)))
    g_tot = g_bad = f_tot = f_bad = 0
    for _, p in pick:
        r = rtk_cells(p)
        if not r:
            continue
        cells = r[0]
        g = cells[:, 0]
        g_tot += int((g > 0).sum())
        g_bad += int((g >= ts.nground).sum())
        for z in cells[:, 1][cells[:, 1] > 0]:
            z = int(z)
            if z >= len(ts.objs):
                continue
            for fr in ts.objs[z]:
                if fr:
                    f_tot += 1
                    f_bad += fr >= len(ts.cents)
    print('sampled %d RTK maps' % len(pick))
    print('  ground cells drawn      %7d   beyond 5.33 TILE  %6d  (%.2f%%)' % (
        g_tot, g_bad, 100 * g_bad / max(g_tot, 1)))
    print('  object frames requested %7d   beyond 5.33 TILEC %6d  (%.2f%%)' % (
        f_tot, f_bad, 100 * f_bad / max(f_tot, 1)))


def selfcheck():
    files = map_files()
    assert len(files) > 3000, 'expected the full RTK map set, got %d' % len(files)
    # header dims must exactly account for the file at 6 bytes/cell
    bad = []
    for mid, p in sorted(files.items())[:200]:
        xs, ys = struct.unpack('>HH', open(p, 'rb').read(4))
        if os.path.getsize(p) != 4 + xs * ys * 6:
            bad.append(mid)
    assert not bad, 'these RTK maps are not 4+xs*ys*6 bytes: %s' % bad[:5]
    # a known room: RTK's Kugnae is 220x220, same as the 4.95 client's
    c, xs, ys = rtk_cells(files[0])
    assert (xs, ys) == (220, 220), 'RTK map 0 should be 220x220, got %dx%d' % (xs, ys)
    assert c[:, 0].max() > 1000, 'ground words look empty'
    names = rtk_map_names()
    assert names.get(0) == 'Kugnae', 'RTK Maps table did not parse (id 0 = %r)' % names.get(0)
    assert len(names) > 9000, 'expected ~9850 RTK map names, got %d' % len(names)
    objs = rm.TileSet._parse_sobj(open(RTK_SOBJ, 'rb').read())
    assert len(objs) > 18000, 'RTK SObj.tbl parsed to only %d records' % len(objs)
    print('selfcheck ok: %d map files, %d names, %d SObj records' % (len(files), len(names), len(objs)))


# ----------------------------------------------------------------------------- cli
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('cmd', nargs='?', default='all', choices=['all', 'one'])
    ap.add_argument('id', nargs='?', type=int)
    ap.add_argument('out', nargs='?')
    ap.add_argument('--data', default=None,
                    help='Tile.dat to render with (default: richest available)')
    ap.add_argument('--thumb', type=int, default=400)
    ap.add_argument('--maxfull', type=int, default=2560)
    ap.add_argument('--only', default='')
    ap.add_argument('--fill-missing', action='store_true',
                    help='substitute learned tiles from re/rtk_tile_fallback.csv for missing 7.x '
                         'art. OFF by default: the legacy set exists to SHOW the gaps, and the '
                         'modern client renders them for real.')
    ap.add_argument('--learn-tiles', action='store_true',
                    help='rebuild re/rtk_tile_fallback.csv, then exit')
    ap.add_argument('--client', nargs='?', const=LATEST_CLIENT, default=None,
                    help='modern split-archive client to render with (48px tiles, full 7.x art). '
                         'Used AUTOMATICALLY when present; pass a path to point elsewhere.')
    ap.add_argument('--legacy-art', action='store_true',
                    help='force the old 5.x Tile.dat path (24px, leaves 7.x-only tiles black)')
    ap.add_argument('--stats', action='store_true')
    ap.add_argument('--check', action='store_true')
    args = ap.parse_args()
    # The modern client is the only asset set that covers RTK in full (0 unresolved ground cells of
    # 3.7M, vs 295k black on 5.33). Use it whenever it is on the box unless explicitly refused.
    if not args.legacy_art and not args.client and os.path.exists(
            os.path.join(LATEST_CLIENT, 'Data', 'tile.dat')):
        args.client = LATEST_CLIENT
    global OUTDIR
    OUTDIR = OUTDIR_MODERN if args.client else OUTDIR_LEGACY

    if args.check:
        selfcheck()
        return

    if args.client:
        print('tile art: %s (modern split archives, 48px)' % args.client, flush=True)
        ts = LatestTileSet(args.client)
        print('  %d ground frames, %d object frames, %d SObj records' % (
            ts.nground, ts.ncobj, len(ts.objs)), flush=True)
    else:
        src = args.data or best_tile_dat()
        print('tile art: %s' % src, flush=True)
        ts = rtk_tileset(src)
    if args.learn_tiles:
        learn_fallback(ts)
        return
    if not args.client:
        print('  %d ground frames, %d object frames, %d RTK SObj records' % (
            ts.nground, len(ts.cents), len(ts.objs)), flush=True)

    if args.stats:
        ids = [int(x) for x in args.only.split(',') if x.strip()]
        why_black(ts, ids) if ids else stats(ts)
        return

    files = map_files()
    names = rtk_map_names()

    if args.cmd == 'one':
        r = rtk_cells(files[args.id])
        cells, xs, ys = r
        img = render_latest(ts, cells, xs, ys) if args.client else rm.render(ts, cells, xs, ys)
        out = args.out or 'rtk%d.png' % args.id
        img.save(out)
        print('RTK TK%d %r %dx%d -> %s' % (args.id, names.get(args.id, '?'), xs, ys, out))
        return

    full = os.path.join(OUTDIR, 'full')
    thumb = os.path.join(OUTDIR, 'thumb')
    os.makedirs(full, exist_ok=True)
    if args.thumb:
        os.makedirs(thumb, exist_ok=True)

    ids = [int(x) for x in args.only.split(',') if x.strip()] or sorted(files)
    fb = load_fallback() if (args.fill_missing and not args.client) else {}
    if fb:
        print('  %d fallback tiles for art 5.33 does not have' % len(fb), flush=True)
    filled = 0
    sup = client_support()
    meta, t0 = [], time.time()
    for k, mid in enumerate(ids):
        r = rtk_cells(files[mid]) if mid in files else None
        if not r:
            continue
        cells, xs, ys = r
        if fb:
            g = cells[:, 0]
            oor = np.flatnonzero(g >= ts.nground)
            if len(oor):
                sub = np.array([fb.get(int(g[i]), 0) for i in oor], dtype=np.uint16)
                filled += int((sub > 0).sum())
                g[oor] = np.where(sub > 0, sub, g[oor])
        try:
            img = render_latest(ts, cells, xs, ys) if args.client else rm.render(ts, cells, xs, ys)
        except Exception as e:                      # noqa: BLE001 - keep the batch going
            print('  !! TK%d: %s' % (mid, e))
            continue
        native = (img.width, img.height)
        if args.maxfull and max(native) > args.maxfull:
            img = img.copy()
            img.thumbnail((args.maxfull, args.maxfull), Image.LANCZOS)
        img.save(os.path.join(full, 'TK%d.png' % mid))
        if args.thumb:
            th = img.copy()
            th.thumbnail((args.thumb, args.thumb), Image.LANCZOS)
            th.save(os.path.join(thumb, 'TK%d.png' % mid))
        row = {'id': mid, 'name': names.get(mid, 'Map %d' % mid),
               'xs': xs, 'ys': ys, 'w': native[0], 'h': native[1]}
        if sup:
            miss = sup.missing(cells)
            row['t'] = sup.tags(miss)
            row['b4'], row['b5'] = miss['b4'], miss['b5']
        meta.append(row)
        if (k + 1) % 250 == 0:
            print('  %d/%d  (%.0fs)' % (k + 1, len(ids), time.time() - t0), flush=True)

    # --only renders a SUBSET; merge into the existing index instead of replacing it, or the
    # viewer loses every map not named on this run (it did, once).
    index_path = os.path.join(OUTDIR, 'maps.json')
    if args.only and os.path.exists(index_path):
        with open(index_path, encoding='utf-8') as fh:
            prev = {m['id']: m for m in json.load(fh)}
        prev.update({m['id']: m for m in meta})
        meta = [prev[k] for k in sorted(prev)]
    json.dump(meta, open(index_path, 'w'), separators=(',', ':'))
    # Distinct global per set, or the two maps.js files clobber each other in the viewer.
    gname = 'RTKM_MAPS' if OUTDIR == OUTDIR_MODERN else 'RTK_MAPS'
    open(os.path.join(OUTDIR, 'maps.js'), 'w').write(
        'window.%s=' % gname + json.dumps(meta, separators=(',', ':')) + ';')
    print('done: %d RTK maps in %.0fs (%d cells filled from the 4.95 world) -> %s' % (
        len(meta), time.time() - t0, filled, OUTDIR))


if __name__ == '__main__':
    main()
