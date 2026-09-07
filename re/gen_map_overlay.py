#!/usr/bin/env python3
"""Build the map viewer's warp/NPC overlay data.

Two datasets, kept ISOLATED from each other (see re/render_rtk_maps.py for why):

  default   our official 4.95 world.  Warps.csv + NPCs.csv + Maps.csv
            -> re/mapviewer/assets/overlay.js       window.OVERLAY
  --rtk     the RTK 7.x reference server.  RTK's own mysqldump Warps + Maps tables.
            Writes BOTH RTK sets, which are rendered from different client art:
            -> assets/rtk/overlay.js         window.RTK_OVERLAY   (5.33 art, 24px)
            -> assets/rtk-modern/overlay.js  window.RTKM_OVERLAY  (modern client, 48px)

Adjacent warp cells sharing a destination map are merged into one labelled run, so a four-tile
doorway gets one label instead of four. Re-run after editing the CSVs:

    python re/gen_map_overlay.py            # official
    python re/gen_map_overlay.py --rtk      # RTK reference set
    python re/gen_map_overlay.py --check    # self-check both, writes nothing

Output shape (both):
    { "<mapId>": { "w": [{x, y, c:[[x,y],...], l:"to Kugnae (12,40)", d:<dest>, r:0|1}],
                   "n": [{x, y, l:"Wand"}] } }
`r` is 1 when the destination map has a rendered PNG, so the viewer can follow the warp.

NOTE on RTK NPCs: RTK's mysqldump carries no NPC *placement* table (its Mobs table has no map/x/y,
and placement lives outside this dump), so the RTK overlay reuses our NPCs.csv positions, which are
themselves RTK-derived. A handful have since been moved to suit the 4.95 client. The viewer labels
this layer accordingly — treat RTK NPC pins as indicative, not as RTK ground truth.
"""
import csv, importlib.util, json, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
GD = os.path.join(ROOT, 'game-data')
VIEWER = os.path.join(ROOT, 're', 'mapviewer', 'assets')


def rows(name):
    with open(os.path.join(GD, name), newline='', encoding='utf-8-sig') as f:
        return list(csv.DictReader(f))


def rendered(assets):
    """ids that actually have a PNG, plus each map's name from its rendered index."""
    with open(os.path.join(assets, 'maps.json'), encoding='utf-8') as f:
        idx = json.load(f)
    return {m['id'] for m in idx}, {m['id']: m['name'] for m in idx}


def runs(cells):
    """Split (x,y) cells into 4-adjacent connected groups."""
    todo, out = set(cells), []
    while todo:
        seed = todo.pop()
        blob, stack = [seed], [seed]
        while stack:
            x, y = stack.pop()
            for n in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
                if n in todo:
                    todo.remove(n)
                    blob.append(n)
                    stack.append(n)
        out.append(blob)
    return out


def assemble(warps, npcs, names, have_png):
    """warps: (src,sx,sy,dst,dx,dy) tuples. npcs: (map,x,y,label). -> overlay dict."""
    ov = {}

    def bucket(mid):
        return ov.setdefault(str(mid), {'w': [], 'n': []})

    grouped = {}
    for src, sx, sy, dst, dx, dy in warps:
        grouped.setdefault((src, dst), {})[(sx, sy)] = (dx, dy)

    for (src, dst), cells in sorted(grouped.items()):
        for blob in runs(cells.keys()):
            blob.sort(key=lambda c: (c[1], c[0]))
            ax, ay = blob[0]                       # label anchor = top-left cell of the run
            dx, dy = cells[(ax, ay)]
            label = 'to %s (%d,%d)' % (names.get(dst, '#%d' % dst), dx, dy)
            bucket(src)['w'].append({'x': ax, 'y': ay, 'c': blob, 'l': label,
                                     'd': dst, 'r': 1 if dst in have_png else 0})

    for mid, x, y, label in npcs:
        bucket(mid)['n'].append({'x': x, 'y': y, 'l': label})
    return ov


# ----------------------------------------------------------------------------- official 4.95
def build_official():
    have_png, png_names = rendered(VIEWER)
    names = dict(png_names)                        # rendered maps without a Maps.csv row
    for r in rows('Maps.csv'):                     # Maps.csv is authoritative where it has a row
        if r['MapId'].strip() and r['MapName'].strip():
            names[int(r['MapId'])] = r['MapName']

    warps = []
    for r in rows('Warps.csv'):
        try:
            warps.append((int(r['SourceMapId']), int(r['SourceX']), int(r['SourceY']),
                          int(r['DestinationMapId']), int(r['DestinationX']), int(r['DestinationY'])))
        except (ValueError, KeyError, TypeError):
            continue
    return assemble(warps, npc_rows(), names, have_png)


def npc_rows():
    out = []
    for r in rows('NPCs.csv'):
        if (r.get('Enabled') or '1').strip() == '0':
            continue
        try:
            mid, x, y = int(r['NpcMapId']), int(r['NpcX']), int(r['NpcY'])
        except (ValueError, KeyError, TypeError):
            continue
        out.append((mid, x, y, (r.get('NpcDescription') or r.get('NpcIdentifier') or '?').strip()))
    return out


# ----------------------------------------------------------------------------- RTK reference
def _rtk():
    spec = importlib.util.spec_from_file_location('rrm', os.path.join(HERE, 'render_rtk_maps.py'))
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


def build_rtk(subdir='rtk'):
    rtk = _rtk()
    assets = os.path.join(VIEWER, subdir)
    have_png, _ = rendered(assets)
    names = rtk.rtk_map_names()                    # RTK's own Maps table, 9,850 rows

    warps = []
    for t in rtk.sql_rows('Warps'):
        f = rtk.sql_split(t)
        try:
            warps.append((int(f[1]), int(f[2]), int(f[3]), int(f[4]), int(f[5]), int(f[6])))
        except (ValueError, IndexError):
            continue
    return assemble(warps, npc_rows(), names, have_png)


# ----------------------------------------------------------------------------- io
def write(ov, path, global_name):
    with open(path, 'w', encoding='utf-8') as f:
        f.write('window.%s=%s;\n' % (global_name, json.dumps(ov, separators=(',', ':'))))
    print('wrote %s\n  %d maps, %d warp runs (%d cells), %d npcs, %.0f KB' % (
        path, len(ov),
        sum(len(v['w']) for v in ov.values()),
        sum(len(r['c']) for v in ov.values() for r in v['w']),
        sum(len(v['n']) for v in ov.values()),
        os.path.getsize(path) / 1024))


def selfcheck():
    assert len(runs([(0, 0), (1, 0), (2, 0), (5, 5)])) == 2, 'non-adjacent cells must split'
    assert len(runs([(0, 0), (0, 1), (1, 1)])) == 1, 'L-shaped run is one blob'
    ov = build_official()
    assert ov, 'no official overlay data'
    w = ov['4711']['w']
    assert sum(len(r['c']) for r in w) > len(w), 'expected multi-cell doorways to collapse'
    assert all(r['l'].startswith('to ') and '(' in r['l'] for v in ov.values() for r in v['w'])
    print('official ok: %d maps, %d warp runs, %d npcs' % (
        len(ov), sum(len(v['w']) for v in ov.values()), sum(len(v['n']) for v in ov.values())))
    if os.path.exists(os.path.join(VIEWER, 'rtk', 'maps.json')):
        r = build_rtk('rtk')
        assert r, 'no RTK overlay data'
        print('rtk ok:      %d maps, %d warp runs, %d npcs' % (
            len(r), sum(len(v['w']) for v in r.values()), sum(len(v['n']) for v in r.values())))
    else:
        print('rtk skipped: render it first with re/render_rtk_maps.py')


if __name__ == '__main__':
    if '--check' in sys.argv:
        selfcheck()
    elif '--rtk' in sys.argv:
        # Same warps and NPCs for both RTK sets -- only the rendered-map list differs, so each
        # directory gets its own file with its own global.
        for sub, glob_name in (('rtk', 'RTK_OVERLAY'), ('rtk-modern', 'RTKM_OVERLAY')):
            if os.path.exists(os.path.join(VIEWER, sub, 'maps.json')):
                write(build_rtk(sub), os.path.join(VIEWER, sub, 'overlay.js'), glob_name)
    else:
        write(build_official(), os.path.join(VIEWER, 'overlay.js'), 'OVERLAY')
