#!/usr/bin/env python3
"""Tag every RTK map with the OLDEST client that can draw all of its art.

RTK is a 7.x world (see re/render_rtk_maps.py). Its rooms are built from a tile index space that
every client since 4.x has only ever APPENDED to, so "can client C draw this map" is a containment
question, not a rendering one: every ground tile the map names must exist in C's tile sheet, and
every SObj id it names must exist in C's SObj table.

    4.x + 5.x   the 4.95 client can draw every cell (and so can everything after it)
    5.x         needs 5.33's sheet; the 4.x client is missing at least one tile or object
    modern      needs the modern split-archive client; 5.33 cannot draw it either

The tiers nest (4.x art is a subset of 5.33's, which is a subset of the modern client's), so the
tags are exactly "4.x","5.x" / "5.x" / "modern" -- asserted, not assumed, in selfcheck().

CEILINGS, read from each client's own tables (nothing here is hardcoded lore):
    4.95  TileA 9,922 + TileB 8,938 ground frames, 16,409 object frames, 7,608 SObj records
    5.33  TILE.EPF 28,551 ground frames, TILEC 29,414 object frames, 12,696 SObj records
    modern 47,912 ground frames, 53,698 object frames, 19,551 SObj records

THE 4.x GROUND CHECK IS NOT `id < 9922`. An RTK ground word is a flat index into 5.33's MERGED
sheet. 4.x had two sheets: sheet 1 landed at TILE[i+1] (so merged 1..9922 is reachable directly),
but sheet 2 was RE-PACKED into the merged sheet, scattered as far up as 24,034 -- those frames ARE
4.x art and must count as supported. game-data/Tile533Map.csv carries that lookup; its image is
unioned in here. Using a plain ceiling instead would mislabel ~5,100 perfectly drawable frames.

CAVEAT: this answers "does the client have art for it", not "does it look identical". 165 of 5.33's
12,696 shared SObj records and 183 of 4.95's 7,608 differ from RTK's -- re-packed frame lists for the
same objects. Those draw, they may just draw a different sprite.

Usage:
    python re/tag_rtk_client_support.py            # report the distribution, writes nothing
    python re/tag_rtk_client_support.py --apply    # stamp tags into both rendered map indexes
    python re/tag_rtk_client_support.py --check    # self-check
"""
import importlib.util
import json
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)

# The 4.x tier means the 4.95 client -- the era this server emulates. (The 4.83 client in
# KRU/NexusTK483 is smaller still: 8,224 + 6,807 ground frames, 6,000 SObj. Tagging against it
# would just move maps from "4.x" to "5.x" for a client nobody runs.)
CLIENT_4X = [
    r'C:\Users\brian\Desktop\NextAeon\NexusTK.dat',
    r'C:\Program Files (x86)\KRU\NexusTK\NexusTK.dat',
]


def _load(name, path):
    spec = importlib.util.spec_from_file_location(name, os.path.join(HERE, path))
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


class Support:
    """Per-client ceilings, and the tag for one map's cells."""

    def __init__(self):
        self.rrm = _load('rrm_tag', 'render_rtk_maps.py')
        rm = self.rrm.rm

        dat4 = next((p for p in CLIENT_4X if os.path.exists(p)), None)
        if not dat4:
            raise SystemExit('no 4.x client archive found; looked in:\n  ' + '\n  '.join(CLIENT_4X))
        a4 = rm.read_dat(dat4)
        self.sobj4 = len(rm.TileSet._parse_sobj(a4['SOBJ.TBL']))
        na = np.frombuffer(a4['TILEA.TBL'], np.uint32, 1)[0]        # sheet-1 frame count
        # Merged-sheet ids a 4.x client can name: sheet 1 sits at TILE[i+1]; sheet 2 is scattered.
        self.ok4 = np.zeros(1 << 16, bool)
        self.ok4[1:na + 1] = True
        for merged in rm.load_sheet2().values():
            if merged < len(self.ok4):
                self.ok4[merged] = True

        ts = rm.TileSet()                                            # 5.33
        self.ground5, self.sobj5 = ts.nground, len(ts.objs)

        meta = rm.read_dat(os.path.join(self.rrm.LATEST_CLIENT, 'Data', 'tile.dat'))
        self.groundm = len(rm.tbl_palettes(meta['TILE.TBL']))
        self.sobjm = len(rm.TileSet._parse_sobj(meta['SOBJ.TBL']))
        self.src4 = dat4

    def missing(self, cells):
        """-> (unsupported cells on 4.x, on 5.33, on modern) for one map's [ground, object] array."""
        g = cells[:, 0]
        o = cells[:, 1]
        g, o = g[g > 0], o[o > 0]
        return (int((~self.ok4[g]).sum()) + int((o >= self.sobj4).sum()),
                int((g >= self.ground5).sum()) + int((o >= self.sobj5).sum()),
                int((g >= self.groundm).sum()) + int((o >= self.sobjm).sum()))

    def tags(self, cells):
        b4, b5, _ = self.missing(cells)
        return ['4.x', '5.x'] if not b4 else (['5.x'] if not b5 else ['modern'])


def tag_all(sup=None):
    """-> {map id: {'tags': [...], 'b4': n, 'b5': n}} for every RTK map file."""
    sup = sup or Support()
    out = {}
    for mid, path in sorted(sup.rrm.map_files().items()):
        r = sup.rrm.rtk_cells(path)
        if not r:
            continue
        b4, b5, bm = sup.missing(r[0])
        out[mid] = {'tags': ['4.x', '5.x'] if not b4 else (['5.x'] if not b5 else ['modern']),
                    'b4': b4, 'b5': b5, 'bm': bm}
    return out


def apply(tags):
    """Stamp tags into the rendered indexes. Both RTK sets get them: the tag describes the MAP's
    art requirements, not which client we happened to render that copy with."""
    for sub, glob in (('rtk', 'RTK_MAPS'), ('rtk-modern', 'RTKM_MAPS')):
        d = os.path.join(REPO, 're', 'mapviewer', 'assets', sub)
        p = os.path.join(d, 'maps.json')
        if not os.path.exists(p):
            print('skip %s (not rendered)' % sub)
            continue
        with open(p, encoding='utf-8') as f:
            meta = json.load(f)
        hit = 0
        for m in meta:
            t = tags.get(m['id'])
            if t:
                m['t'], m['b4'], m['b5'] = t['tags'], t['b4'], t['b5']
                hit += 1
        json.dump(meta, open(p, 'w'), separators=(',', ':'))
        open(os.path.join(d, 'maps.js'), 'w').write(
            'window.%s=%s;' % (glob, json.dumps(meta, separators=(',', ':'))))
        print('tagged %d/%d maps in assets/%s/' % (hit, len(meta), sub))


def report(tags):
    from collections import Counter
    c = Counter(' + '.join(t['tags']) for t in tags.values())
    for k in ('4.x + 5.x', '5.x', 'modern'):
        print('  %-10s %5d maps' % (k, c.get(k, 0)))
    worst = sorted(tags.items(), key=lambda kv: -kv[1]['b5'])[:5]
    print('  most 5.33-unsupported cells: ' + ', '.join(
        '%d (%d)' % (mid, t['b5']) for mid, t in worst))


def selfcheck():
    sup = Support()
    print('4.x archive: %s' % sup.src4)
    print('ceilings: 4.x sobj %d | 5.33 ground %d sobj %d | modern ground %d sobj %d' % (
        sup.sobj4, sup.ground5, sup.sobj5, sup.groundm, sup.sobjm))
    assert sup.ok4.sum() > 14000, 'the 4.x reachable set collapsed to %d ids' % sup.ok4.sum()
    assert sup.ok4[9922] and not sup.ok4[0], 'sheet-1 range is wrong'
    assert sup.ok4[24034], 'sheet-2 remap not unioned in (frame 24034 is 4.x art)'
    assert sup.ground5 < sup.groundm, 'modern must be a superset of 5.33'
    t = tag_all(sup)
    assert len(t) > 3000, 'only tagged %d maps' % len(t)
    # The whole tag scheme rests on the tiers nesting. Prove it on the real data every run.
    assert not [m for m, v in t.items() if not v['b4'] and v['b5']], '4.x-ok but 5.33-missing'
    assert not [m for m, v in t.items() if not v['b5'] and v['bm']], '5.33-ok but modern-missing'
    assert not [m for m, v in t.items() if v['bm']], 'the modern client should cover every RTK map'
    # Canary 1: Walsuk Tavern is drawable on 4.95 ONLY through the sheet-2 remap -- its ground ids
    # run past 9,922, so a plain-ceiling check would wrongly demote it to 5.x. Guards the union.
    c2 = sup.rrm.rtk_cells(sup.rrm.map_files()[2])[0]
    assert int(c2[:, 0].max()) > 9922, 'canary map 2 no longer exercises the sheet-2 remap'
    assert t[2]['tags'] == ['4.x', '5.x'], 'Walsuk Tavern should be 4.x art, got %r' % t[2]
    # Canary 2: Foxy Hole is built from 7.x-only tiles (render_rtk_maps: 324/324 cells, 10 ids).
    assert t[3712]['tags'] == ['modern'], 'Foxy Hole is 7.x-only art, got %r' % t[3712]
    report(t)
    print('selfcheck ok: %d maps tagged' % len(t))


if __name__ == '__main__':
    if '--check' in sys.argv:
        selfcheck()
    else:
        tags = tag_all()
        report(tags)
        if '--apply' in sys.argv:
            apply(tags)
