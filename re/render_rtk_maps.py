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

ART: rendered with the SAME 5.33 Tile.dat we use for our own maps. That is not a compromise — RTK's
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

EMPTY MARGINS: RTK's editor allocates a map at one size and fills a smaller rectangle, so 554 of the
3,248 maps carry an all-zero right/bottom margin that renders as a hard black bar (map 3605 declares
23x23 for an 18x16 room; 9097 declares 250x250 for the same 18x16). Those bars are NOT missing art —
the file has no tile there. They are trimmed by default, right and bottom only so every cell keeps
its (x, y) and the overlay still lines up, and never past a warp or NPC. `--no-crop` keeps them, and
maps.json records the declared size in `decl` whenever it differs.

OBJECTS: uses **RTK's own SObj.tbl** (`RTK-Server/rtk/SObj.tbl`, 18,954 records vs 5.33's 12,696).
Every object id RTK's maps use is in range there; 5.33's table would silently drop the top third.

Usage:
    python re/render_rtk_maps.py all [outdir] [--thumb 400] [--maxfull 2560] [--only a,b]
    python re/render_rtk_maps.py one <id> [out.png]
    python re/render_rtk_maps.py --stats                    # coverage report, renders nothing
    python re/render_rtk_maps.py --stats --only 3918,3712   # why IS THAT MAP black?
    python re/render_rtk_maps.py all --no-crop              # keep RTK's padded dims
    python re/render_rtk_maps.py --learn-tiles              # rebuild the fallback table
    python re/render_rtk_maps.py all --no-fill              # show missing art as black
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
OUTDIR = os.path.join(REPO, 're', 'mapviewer', 'assets', 'rtk')

_spec = importlib.util.spec_from_file_location('render_maps', os.path.join(HERE, 'render_maps.py'))
rm = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(rm)


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


def content_extent(cells, xs, ys):
    """Right/bottom extent of real content: (w, h) such that everything past it is empty.

    RTK's editor allocates a map at one size and fills a smaller rectangle, leaving the right and
    bottom margins as ground word 0. Rendered literally that is a hard black bar, which reads as
    missing art but is not — the file simply has no tile there. 554 of the 3,248 RTK maps have such
    a margin, 339 of them wasting more than a quarter of the image (map 9097 declares 250x250 for an
    18x16 room).

    Trimmed from the RIGHT and BOTTOM only, never the left or top, so every cell keeps its (x, y)
    and the warp/NPC overlay still lines up. Objects count as content too: an object anchored in the
    margin can draw north into the visible area.
    """
    g = cells[:, 0].reshape(ys, xs)
    ob = cells[:, 1].reshape(ys, xs)
    nz = np.argwhere((g != 0) | (ob != 0))
    if not len(nz):
        return xs, ys
    return int(nz[:, 1].max()) + 1, int(nz[:, 0].max()) + 1


def crop_cells(cells, xs, ys, w, h):
    if (w, h) == (xs, ys):
        return cells, xs, ys
    grid = cells.reshape(ys, xs, 2)[:h, :w]
    return grid.reshape(-1, 2).copy(), w, h


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


def rtk_tileset(data=rm.DEFAULT_DATA):
    """5.33 art, but RTK's SObj table — its object id space is a third larger than 5.33's."""
    ts = rm.TileSet(data)
    ts.objs = rm.TileSet._parse_sobj(open(RTK_SOBJ, 'rb').read())
    return ts


def map_files():
    out = {}
    for fn in os.listdir(RTK_MAPS):
        m = re.fullmatch(r'TK(\d+)\.map', fn)
        if m:
            out[int(m.group(1))] = os.path.join(RTK_MAPS, fn)
    return out


def overlay_extents():
    """map id -> (max warp/NPC x, max y), so trimming never hides a pin."""
    import csv
    ext = {}

    def bump(mid, x, y):
        a, b = ext.get(mid, (0, 0))
        ext[mid] = (max(a, x), max(b, y))

    for t in sql_rows('Warps'):
        f = sql_split(t)
        try:
            bump(int(f[1]), int(f[2]), int(f[3]))
        except (ValueError, IndexError):
            continue
    try:
        with open(os.path.join(REPO, 'game-data', 'NPCs.csv'), newline='', encoding='utf-8-sig') as fh:
            for r in csv.DictReader(fh):
                try:
                    bump(int(r['NpcMapId']), int(r['NpcX']), int(r['NpcY']))
                except (ValueError, KeyError, TypeError):
                    continue
    except OSError:
        pass
    return ext


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
    print('no-art = a real tile id we have no frame for; 5.33 TILE.EPF stops at %d' % (ts.nground - 1))


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
    ap.add_argument('--data', default=rm.DEFAULT_DATA)
    ap.add_argument('--thumb', type=int, default=400)
    ap.add_argument('--maxfull', type=int, default=2560)
    ap.add_argument('--only', default='')
    ap.add_argument('--no-crop', action='store_true',
                    help="keep RTK's declared dims, empty margins and all")
    ap.add_argument('--no-fill', action='store_true',
                    help='do not substitute learned tiles for missing 7.x art')
    ap.add_argument('--learn-tiles', action='store_true',
                    help='rebuild re/rtk_tile_fallback.csv, then exit')
    ap.add_argument('--stats', action='store_true')
    ap.add_argument('--check', action='store_true')
    args = ap.parse_args()

    if args.check:
        selfcheck()
        return

    print('loading 5.33 tileset + RTK SObj ...', flush=True)
    ts = rtk_tileset(args.data)
    if args.learn_tiles:
        learn_fallback(ts)
        return
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
        img = rm.render(ts, cells, xs, ys)
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
    pins = {} if args.no_crop else overlay_extents()
    fb = {} if args.no_fill else load_fallback()
    if fb:
        print('  %d fallback tiles for art 5.33 does not have' % len(fb), flush=True)
    filled = 0
    meta, t0 = [], time.time()
    cropped = 0
    for k, mid in enumerate(ids):
        r = rtk_cells(files[mid]) if mid in files else None
        if not r:
            continue
        cells, xs, ys = r
        decl = (xs, ys)
        if fb:
            g = cells[:, 0]
            oor = np.flatnonzero(g >= ts.nground)
            if len(oor):
                sub = np.array([fb.get(int(g[i]), 0) for i in oor], dtype=np.uint16)
                filled += int((sub > 0).sum())
                g[oor] = np.where(sub > 0, sub, g[oor])
        if not args.no_crop:
            w, h = content_extent(cells, xs, ys)
            px, py = pins.get(mid, (0, 0))          # never crop a warp or NPC off the image
            cells, xs, ys = crop_cells(cells, xs, ys, max(w, px + 1), max(h, py + 1))
            cropped += (xs, ys) != decl
        try:
            img = rm.render(ts, cells, xs, ys)
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
        m = {'id': mid, 'name': names.get(mid, 'Map %d' % mid),
             'xs': xs, 'ys': ys, 'w': native[0], 'h': native[1]}
        if (xs, ys) != decl:
            m['decl'] = list(decl)                 # RTK's declared size, before the empty margin
        meta.append(m)
        if (k + 1) % 250 == 0:
            print('  %d/%d  (%.0fs)' % (k + 1, len(ids), time.time() - t0), flush=True)

    json.dump(meta, open(os.path.join(OUTDIR, 'maps.json'), 'w'), separators=(',', ':'))
    open(os.path.join(OUTDIR, 'maps.js'), 'w').write(
        'window.RTK_MAPS=' + json.dumps(meta, separators=(',', ':')) + ';')
    print('done: %d RTK maps in %.0fs (%d trimmed of an empty margin, %d cells filled from the '
          '4.95 world) -> %s' % (len(meta), time.time() - t0, cropped, filled, OUTDIR))


if __name__ == '__main__':
    main()
