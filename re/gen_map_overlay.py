#!/usr/bin/env python3
"""Build re/mapviewer/assets/overlay.js from the game-data CSVs.

Emits warp + NPC markers per source map, in CELL coordinates, for the overlay
layer in re/mapviewer/index.html. Adjacent warp cells that share a destination
map are merged into one run, so a four-tile doorway gets one label instead of
four. Re-run after editing Warps.csv / NPCs.csv / Maps.csv:

    python re/gen_map_overlay.py            # regenerate
    python re/gen_map_overlay.py --check    # self-check, writes nothing

Output shape:
    window.OVERLAY = {
      "<mapId>": {
        "w": [{x, y, c:[[x,y],...], l:"to Kugnae (12,40)", d:<destMapId>, r:0|1}],
        "n": [{x, y, l:"Wand"}]
      }
    }
`r` is 1 when the destination map has a rendered PNG (so the viewer can follow
the warp), 0 when it does not.
"""
import csv, json, os, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GD = os.path.join(ROOT, 'game-data')
VIEWER = os.path.join(ROOT, 're', 'mapviewer', 'assets')
OUT = os.path.join(VIEWER, 'overlay.js')


def rows(name):
    with open(os.path.join(GD, name), newline='', encoding='utf-8-sig') as f:
        return list(csv.DictReader(f))


def map_names():
    """Maps.csv is authoritative; maps.json fills in maps rendered without a row."""
    names = {}
    with open(os.path.join(VIEWER, 'maps.json'), encoding='utf-8') as f:
        rendered = json.load(f)
    for m in rendered:
        names[m['id']] = m['name']
    for r in rows('Maps.csv'):
        if r['MapId'].strip() and r['MapName'].strip():
            names[int(r['MapId'])] = r['MapName']
    return names, {m['id'] for m in rendered}


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


def build():
    names, rendered = map_names()
    ov = {}

    def bucket(mid):
        return ov.setdefault(str(mid), {'w': [], 'n': []})

    # warps: bucket by (source map, destination map), then split into adjacent runs
    grouped = {}
    for r in rows('Warps.csv'):
        try:
            src, sx, sy = int(r['SourceMapId']), int(r['SourceX']), int(r['SourceY'])
            dst, dx, dy = int(r['DestinationMapId']), int(r['DestinationX']), int(r['DestinationY'])
        except (ValueError, KeyError, TypeError):
            continue
        grouped.setdefault((src, dst), {})[(sx, sy)] = (dx, dy)

    for (src, dst), cells in sorted(grouped.items()):
        for blob in runs(cells.keys()):
            blob.sort(key=lambda c: (c[1], c[0]))
            ax, ay = blob[0]                       # label anchor = top-left cell of the run
            dx, dy = cells[(ax, ay)]
            label = 'to %s (%d,%d)' % (names.get(dst, '#%d' % dst), dx, dy)
            bucket(src)['w'].append({'x': ax, 'y': ay, 'c': blob, 'l': label,
                                     'd': dst, 'r': 1 if dst in rendered else 0})

    for r in rows('NPCs.csv'):
        if (r.get('Enabled') or '1').strip() == '0':
            continue
        try:
            mid, x, y = int(r['NpcMapId']), int(r['NpcX']), int(r['NpcY'])
        except (ValueError, KeyError, TypeError):
            continue
        name = (r.get('NpcDescription') or r.get('NpcIdentifier') or '?').strip()
        bucket(mid)['n'].append({'x': x, 'y': y, 'l': name})

    return ov


def selfcheck():
    assert len(runs([(0, 0), (1, 0), (2, 0), (5, 5)])) == 2, 'non-adjacent cells must split'
    assert len(runs([(0, 0), (0, 1), (1, 1)])) == 1, 'L-shaped run is one blob'
    ov = build()
    assert ov, 'no overlay data produced'
    w = ov['4711']['w']
    assert sum(len(r['c']) for r in w) > len(w), 'expected multi-cell doorways to collapse'
    assert all(r['l'].startswith('to ') and '(' in r['l'] for v in ov.values() for r in v['w'])
    print('selfcheck ok: %d maps, %d warp runs, %d npcs' % (
        len(ov), sum(len(v['w']) for v in ov.values()), sum(len(v['n']) for v in ov.values())))


if __name__ == '__main__':
    if '--check' in sys.argv:
        selfcheck()
        raise SystemExit
    ov = build()
    with open(OUT, 'w', encoding='utf-8') as f:
        f.write('window.OVERLAY=' + json.dumps(ov, separators=(',', ':')) + ';\n')
    print('wrote %s\n  %d maps, %d warp runs (%d cells), %d npcs, %.0f KB' % (
        OUT, len(ov),
        sum(len(v['w']) for v in ov.values()),
        sum(len(r['c']) for v in ov.values() for r in v['w']),
        sum(len(v['n']) for v in ov.values()),
        os.path.getsize(OUT) / 1024))
