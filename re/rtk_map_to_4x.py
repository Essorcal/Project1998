#!/usr/bin/env python3
"""Backport an RTK (7.x) map into TK<id>.map terrain in our 4-bytes/cell map format.

    python re/rtk_map_to_4x.py 2511 2517                  # report only, writes nothing
    python re/rtk_map_to_4x.py 2511 2517 --write          # emit game-data/maps/TK<id>.map
    python re/rtk_map_to_4x.py 2511 --target 5x --write   # aim the ART at the 5.33 client
    python re/rtk_map_to_4x.py --check                    # self-check

TWO TARGETS. The FILE format is the same either way — what changes is which client's tile tables the
art is chosen from:

  --target 4x   every cell drawn with art the 4.95 client owns. Blocked cells whose art has no 4.x
                sheet-2 slot take a substitute tile, so those cells are the wrong stone.
  --target 5x   blocked cells keep RTK's EXACT art on 5.33 by RE-POINTING a free sheet-2 slot (see
                `slot`), and objects 4.95 lacks are kept rather than dropped. 4.95 still draws a
                near-miss for those cells and TileTranslation blanks the objects it has no record
                for, so a 4.95 player sees the 4x-target result and a 5.33 player sees RTK's.

THE 4.x GROUND WORD IS TAGGED, AND ITS TOP TWO BITS ARE OVERLOADED. This is the whole difficulty and
it is the thing to get right:

    v == 0              void, draw nothing
    0 < v < 0xC000      sheet 1 -- TileA[v-1], i.e. merged TILE[v]     -- and pass 0 (WALKABLE)
    v >= 0xC000         sheet 2 -- TileB[v-0xC000] via Tile533Map.csv  -- and pass 3 (SOLID)

The server reads the top 2 bits as passability (Server/MapData.cs) and the client reads them as a
sheet selector (Server/TileTranslation.cs); on 4.95 the wire word IS the .map word, so those two
readings cannot be separated (Server/MapCell.cs). **There is therefore no way to say "solid" and
"sheet-1 art" in the same cell.** Measured, not assumed:

  * Across all 1,924,445 ground cells of the 2,017 maps we ship, ZERO words fall in 0x4000..0xBFFF.
    Every cell is either < 0x4000 (sheet 1) or >= 0xC000 (sheet 2). Pass values 1 and 2 never occur.
  * On the 697 rooms that are the same room in both worlds, "RTK pass != 0" and "our word >= 0xC000"
    agree on 96.7% of 553,772 cells. The 3.3% is RTK's own re-tiling, which is the known rate.

So each cell converts as:

    walkable            word = g                      needs 1 <= g <= 9922 (TileA's frame count)
    blocked             word = 0xC000 + L             needs Tile533Map to carry L -> g
    blocked, no sheet-2 slot, but a fully-solid SObj object already covers the cell
                    ->  word = g       art exact, collision comes from the object layer
    blocked, no sheet-2 slot, nothing else blocking
                    ->  nearest sheet-2 art by pixel distance. COLLISION WINS over exact art: a
                        wall you can walk through is a worse bug than a wall of the wrong stone.
    object id past the 4.x SObj table
                    ->  dropped to 0. The 4.95 client has 7,608 records; RTK's has 18,954.

Substitutions are chosen by mean-squared pixel distance between the two decoded 24x24 frames, over
every sheet-2 frame the 4.x client owns -- the same rendered-colour matching that built
Tile533Map.csv in the first place -- and every one is printed, with its distance, so a bad match is
visible rather than silent.

WHAT THIS CANNOT DO: art the 4.x client simply does not have. Check first with
`python re/tag_rtk_client_support.py` -- a map tagged 4.x converts exactly; 5.x or modern loses
whatever the tag's b4 counts.
"""
import argparse
import importlib.util
import os
import struct
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
OUT = os.path.join(REPO, 'game-data', 'maps')
SHEET2_BASE = 0xC000
CLIENT_4X = r'C:\Users\brian\Desktop\NextAeon\NexusTK.dat'

# Hand-picked substitutes, for cases where nearest-by-pixels picks something that is wrong IN
# CONTEXT. The metric compares one tile against one tile; it cannot see that a tile has to sit
# between water and grass. Every entry needs a reason and should be checked in situ.
#   24311  Nagnang Valley's lake shore. MSE liked a flat grey-brown (merged 297, distance 47), which
#          renders as a concrete strip between the lake and the field. Legacy 5865 is 4.x's own sand
#          lakeshore -- wave edge on top, beach below -- which is what RTK draws there.
GROUND_OVERRIDE = {24311: 5865}


def _load(name, path):
    spec = importlib.util.spec_from_file_location(name, os.path.join(HERE, path))
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


def sobj_flags(raw):
    """Object id -> SObj flag byte. The flag PRECEDES its object's frame list; see
    TileSet._parse_sobj, which learned this the hard way."""
    count = struct.unpack_from('<I', raw, 0)[0]
    off, flags = 4, []
    for _ in range(count):
        if off >= len(raw):
            break
        flags.append(raw[off])                 # this object's flag
        off += 1
        tc = raw[off]
        off += 1 + tc * 2 + 5                  # frames + FF FF FF FF 00
    return flags


class Backporter:
    def __init__(self):
        self.rrm = _load('rrm_bp', 'render_rtk_maps.py')
        self.rm = self.rrm.rm
        self.ts = self.rm.TileSet()                       # 5.33 art == the merged frame space
        self.sheet2 = self.rm.load_sheet2()               # legacy L -> merged g
        self.inv = {}
        for legacy, merged in sorted(self.sheet2.items()):
            self.inv.setdefault(merged, legacy)           # merged g -> some legacy that reaches it

        a4 = self.rm.read_dat(CLIENT_4X)
        self.na = int(np.frombuffer(a4['TILEA.TBL'], np.uint32, 1)[0])     # 9922 sheet-1 frames
        self.nb = int(np.frombuffer(a4['TILEB.TBL'], np.uint32, 1)[0])     # 8938 sheet-2 frames
        self.nsobj = len(self.rm.TileSet._parse_sobj(a4['SOBJ.TBL']))      # 7608 objects
        self.nsobj5 = len(self.ts.objs)                                    # 12696 on 5.33
        self.flags = sobj_flags(open(os.path.join(REPO, 'game-data', 'SObj.tbl'), 'rb').read())

        # Candidate sheet-2 words, and the art each one draws, for the nearest-art search.
        cand = [(L, g) for L, g in self.sheet2.items() if 0 < g < self.ts.nground]
        self.cand_legacy = np.array([L for L, _ in cand], np.int32)
        # float32, NOT int16: a squared channel delta reaches 65025 and silently wraps in int16,
        # which both poisons the distance and can pick the wrong tile.
        self.cand_art = self.ts.ground[np.array([g for _, g in cand], np.int32)].astype(np.float32)
        self._near = {}

    def solid_object(self, oid):
        return 0 < oid < len(self.flags) and self.flags[oid] == 0x0F

    # ------------------------------------------------------------------ 5.x target: re-pointed slots
    def _free_slots(self):
        """Legacy sheet-2 indices the 4.95 client can draw that NO map of ours references.

        `Tile533Map.csv` says what 5.33 draws for each legacy sheet-2 index, and it is ours to edit.
        So a slot nothing points at is a free channel: give it RTK's merged frame as its 5.33 target
        and a blocked cell can carry art 4.x never had, with no format change and no server change.
        Only slots unreferenced by every shipped map qualify — re-pointing a slot some other room
        stands on would re-tile that room on 5.33."""
        used = set()
        for mid in self.rm.load_index():
            try:
                w = self.rm.map_cells(mid)[:, 0].astype(int)
            except Exception:                        # noqa: BLE001  (missing/short file)
                continue
            used |= set((w[w >= SHEET2_BASE] - SHEET2_BASE).tolist())
        # MapCells.csv authors cells too, and a pass-3 row rebuilds a sheet-2 word in MapData.GroundWord.
        p = os.path.join(REPO, 'game-data', 'MapCells.csv')
        for line in open(p, encoding='utf-8'):
            f = line.split(',')
            if len(f) > 5 and f[0].strip().isdigit() and f[4].strip() == '3':
                used.add(int(f[3]))
        free = [L for L in range(self.nb) if L not in used and L in self.sheet2]
        self.free_legacy = np.array(free, np.int32)
        self.free_art = self.ts.ground[np.array([self.sheet2[L] for L in free], np.int32)].astype(np.float32)
        self.free_taken = np.zeros(len(free), bool)
        self.repoint = {}                            # legacy L -> merged frame to point it at

    def slot(self, merged):
        """-> (word, legacy, distance). The legacy index whose 5.33 target we re-point at `merged`.

        Picked as the NEAREST 4.x art among the free slots, so the cell a 4.95 client draws is the
        same near-miss the 4x target would have chosen, while 5.33 gets RTK's frame exactly."""
        if merged in self.inv:                       # already reachable (or re-pointed on an earlier map)
            return SHEET2_BASE + self.inv[merged], self.inv[merged], 0.0
        want = self.ts.ground[merged].astype(np.float32) if 0 < merged < self.ts.nground else None
        if want is None:
            k = int(np.argmax(~self.free_taken))
            dist = -1.0
        else:
            d = ((self.free_art - want) ** 2).mean(axis=(1, 2, 3))
            d[self.free_taken] = np.inf              # one merged frame per slot
            k = int(d.argmin())
            dist = float(np.sqrt(d[k]))
        self.free_taken[k] = True
        L = int(self.free_legacy[k])
        self.repoint[L] = merged
        self.inv[merged] = L
        self.sheet2[L] = merged
        self.ts.sheet2[L] = merged           # so verify() and the renderer read the new mapping
        return SHEET2_BASE + L, L, dist

    def nearest_sheet2(self, merged):
        """-> (word, chosen legacy, chosen merged, mean per-channel distance)."""
        if merged in self._near:
            return self._near[merged]
        if merged in GROUND_OVERRIDE:
            L = GROUND_OVERRIDE[merged]
            out = (SHEET2_BASE + L, L, int(self.sheet2.get(L, -1)), 0.0)
            self._near[merged] = out
            return out
        if not (0 < merged < self.ts.nground):
            # No art at all to match against; fall back to the most common wall-ish sheet-2 frame.
            out = (SHEET2_BASE + int(self.cand_legacy[0]), int(self.cand_legacy[0]), -1, -1.0)
            self._near[merged] = out
            return out
        want = self.ts.ground[merged].astype(np.float32)
        d = ((self.cand_art - want) ** 2).mean(axis=(1, 2, 3))
        k = int(d.argmin())
        L = int(self.cand_legacy[k])
        out = (SHEET2_BASE + L, L, int(self.sheet2[L]), float(np.sqrt(d[k])))
        self._near[merged] = out
        return out

    def convert(self, mid, target='4x'):
        """-> (bytes, xs, ys, report dict)."""
        path = self.rrm.map_files()[mid]
        raw = open(path, 'rb').read()
        xs, ys = struct.unpack('>HH', raw[:4])
        a = np.frombuffer(raw, dtype='>u2', count=xs * ys * 3, offset=4).reshape(-1, 3)
        ground, passable, obj = (a[:, 0].astype(int), a[:, 1].astype(int), a[:, 2].astype(int))

        # The object ceiling is the target client's SObj table. On 5.x we keep everything 5.33 has;
        # TileTranslation.Object blanks the ones 4.95 has no record for, per client, at send time.
        keep_obj = self.nsobj5 if target == '5x' else self.nsobj

        out = bytearray()
        rep = {'exact': 0, 'void': 0, 'obj_blocks': 0, 'subst': {}, 'dropped_obj': {},
               'walk_noart': {}, 'unwalled': 0, 'repoint': {}, 'xs': xs, 'ys': ys}
        for i in range(xs * ys):
            g, p, o = ground[i], passable[i], obj[i]

            dropped = o >= keep_obj                      # the target client has no such object
            if dropped:
                rep['dropped_obj'][o] = rep['dropped_obj'].get(o, 0) + 1
                o = 0

            if g == 0:
                word = 0
                rep['void'] += 1
            elif not p:                                  # walkable -> sheet 1
                if 1 <= g <= self.na:
                    word = g
                    rep['exact'] += 1
                else:                                    # walkable art 4.x lacks; keep it walkable
                    word = 0
                    rep['walk_noart'][g] = rep['walk_noart'].get(g, 0) + 1
            else:                                        # blocked -> needs sheet 2
                if g in self.inv:
                    word = SHEET2_BASE + self.inv[g]
                    rep['exact'] += 1
                elif self.solid_object(o):
                    word = g if 1 <= g <= self.na else 0     # object already blocks this cell
                    rep['obj_blocks'] += 1
                elif dropped:
                    # The wall here WAS the object, and we just removed it. Keeping the block would
                    # leave an invisible wall in the middle of an empty floor, so it goes with the
                    # object; the floor underneath is exact sheet-1 art.
                    word = g if 1 <= g <= self.na else 0
                    rep['unwalled'] += 1
                elif target == '5x':
                    word, L, dist = self.slot(g)
                    e = rep['repoint'].setdefault(g, [0, L, dist])
                    e[0] += 1
                else:
                    word, L, mg, dist = self.nearest_sheet2(g)
                    e = rep['subst'].setdefault(g, [0, L, mg, dist])
                    e[0] += 1
            out += struct.pack('<HH', word, o)
        return bytes(out), xs, ys, rep


def report(bp, mid, rep):
    names = bp.rrm.rtk_map_names()
    n = rep['xs'] * rep['ys']
    print('\nRTK %d "%s"  %dx%d = %d cells' % (mid, names.get(mid, '?'), rep['xs'], rep['ys'], n))
    print('   %d cells exact, %d void, %d blocked by their object instead of the ground flag'
          % (rep['exact'], rep['void'], rep['obj_blocks']))
    if rep['unwalled']:
        print('   %d cells un-walled: their wall WAS an object 4.x lacks, so it left with it'
              % rep['unwalled'])
    if rep['subst']:
        tot = sum(v[0] for v in rep['subst'].values())
        print('   %d cells took a substitute wall tile (collision kept, art approximated):' % tot)
        for g, (cnt, L, mg, dist) in sorted(rep['subst'].items(), key=lambda kv: -kv[1][0]):
            tag = '  [hand-picked]' if g in GROUND_OVERRIDE else ''
            print('      merged %-6d x%-4d -> sheet-2 legacy %-5d (merged %-6d)  pixel dist %.1f%s'
                  % (g, cnt, L, mg, dist, tag))
    if rep['repoint']:
        tot = sum(v[0] for v in rep['repoint'].values())
        print('   %d cells keep RTK art exactly on 5.33 via a re-pointed sheet-2 slot:' % tot)
        for g, (cnt, L, dist) in sorted(rep['repoint'].items(), key=lambda kv: -kv[1][0]):
            print('      merged %-6d x%-4d -> free legacy slot %-5d  (4.95 draws its own art, dist %.1f)'
                  % (g, cnt, L, dist))
    if rep['walk_noart']:
        tot = sum(rep['walk_noart'].values())
        print('   %d walkable cells left VOID (4.x has no art): %s'
              % (tot, ', '.join('%d x%d' % (g, c) for g, c in sorted(rep['walk_noart'].items()))))
    if rep['dropped_obj']:
        tot = sum(rep['dropped_obj'].values())
        print('   %d object cells dropped (past the 4.95 SObj table of %d): %s'
              % (tot, bp.nsobj, ', '.join(str(o) for o in sorted(rep['dropped_obj']))))


def verify(bp, mid, blob, xs, ys):
    """Re-read the file we just built the way the SERVER and the CLIENT each will, and say how much
    of RTK's room actually survived."""
    c = np.frombuffer(blob, np.uint16).reshape(-1, 2).astype(int)
    word, o4 = c[:, 0], c[:, 1]
    raw = open(bp.rrm.map_files()[mid], 'rb').read()
    a = np.frombuffer(raw, dtype='>u2', count=xs * ys * 3, offset=4).reshape(-1, 3)
    g, p, o = a[:, 0].astype(int), a[:, 1].astype(int), a[:, 2].astype(int)

    assert len(blob) == xs * ys * 4, 'wrong file size'
    assert not ((word >= 0x4000) & (word < SHEET2_BASE)).any(), \
        'wrote a word in the impossible 0x4000-0xBFFF band'

    drawn = np.array([bp.ts.ground_frame(int(w)) for w in word])
    same_art = int((drawn == g).sum())
    ours_solid = word >= SHEET2_BASE
    obj_solid = np.array([bp.solid_object(int(x)) for x in o4])
    blocked_now = ours_solid | obj_solid
    print('   verify: ground art matches RTK on %d/%d cells (%.1f%%); '
          'blocked cells match on %d/%d (%.1f%%); objects kept %d/%d'
          % (same_art, xs * ys, 100 * same_art / (xs * ys),
             int((blocked_now == (p > 0)).sum()), xs * ys,
             100 * (blocked_now == (p > 0)).mean(),
             int((o4 > 0).sum()), int((o > 0).sum())))


def selfcheck():
    bp = Backporter()
    assert bp.na == 9922, 'TileA frame count changed: %d' % bp.na
    assert bp.nsobj == 7608, '4.95 SObj record count changed: %d' % bp.nsobj
    assert bp.flags[1502] is not None
    # The encoding claim this whole script rests on, re-measured every run against real 4.x maps.
    dims = bp.rm.load_index()
    band = 0
    for m in list(dims)[:400]:
        try:
            w = bp.rm.map_cells(m)[:, 0].astype(int)
        except Exception:                      # noqa: BLE001
            continue
        band += int(((w >= 0x4000) & (w < SHEET2_BASE)).sum())
    assert band == 0, '%d real 4.x cells sit in 0x4000-0xBFFF; the tagged-word model is wrong' % band
    blob, xs, ys, rep = bp.convert(2511)
    assert len(blob) == xs * ys * 4 and (xs, ys) == (18, 15), 'Gale Chapel geometry'
    assert rep['dropped_obj'], 'Gale Chapel should drop its 13 later-era objects'
    w = np.frombuffer(blob, np.uint16).reshape(-1, 2)[:, 0].astype(int)
    assert not ((w >= 0x4000) & (w < SHEET2_BASE)).any(), 'impossible band'

    # 5.x target: every blocked cell must end up on a sheet-2 word that resolves to RTK's own frame,
    # and the slots it spends must be ones no map of ours stands on.
    bp._free_slots()
    assert len(bp.free_legacy) > 1000, 'only %d free sheet-2 slots' % len(bp.free_legacy)
    blob5, xs, ys, rep5 = bp.convert(2511, '5x')
    assert not rep5['subst'], '5x target must never substitute art'
    assert not rep5['dropped_obj'], 'Gale Chapel objects all fit 5.33 (max 8811 < %d)' % bp.nsobj5
    raw = open(bp.rrm.map_files()[2511], 'rb').read()
    a = np.frombuffer(raw, dtype='>u2', count=xs * ys * 3, offset=4).reshape(-1, 3).astype(int)
    w5 = np.frombuffer(blob5, np.uint16).reshape(-1, 2)[:, 0].astype(int)
    drawn = np.array([bp.ts.ground_frame(int(x)) for x in w5])
    assert (drawn == a[:, 0]).all(), '5x target lost art on %d cells' % int((drawn != a[:, 0]).sum())
    print('selfcheck ok: TileA %d, TileB %d, SObj %d/%d, %d sheet-2 candidates, %d free slots, '
          '5x target reproduces Gale Chapel exactly'
          % (bp.na, bp.nb, bp.nsobj, bp.nsobj5, len(bp.cand_legacy), len(bp.free_legacy)))


def write_repoints(bp, target):
    """Append the re-pointed slots to Tile533Map.csv. Later rows win in both readers (the server's
    ParseSheet2 and re/render_maps.load_sheet2 both build a dict in file order), so this needs no
    surgery on the generated run block above it."""
    p = os.path.join(REPO, 'game-data', 'Tile533Map.csv')
    body = ['%d,1,%d' % (L, m) for L, m in sorted(bp.repoint.items())]
    if not body:
        return
    with open(p, 'a', encoding='utf-8', newline='\n') as f:
        f.write('\n# --- RE-POINTED SLOTS (re/rtk_map_to_4x.py --target %s) ------------------------\n'
                '# Legacy sheet-2 indices NO map of ours stands on, aimed at a 5.33 frame the 4.x\n'
                '# client never had. A backported room can then carry RTK art exactly on 5.33 while\n'
                '# 4.95 still draws the slot\'s own near-miss tile. These rows OVERRIDE the generated\n'
                '# block above (later wins), so re-generating that block must keep them last.\n'
                % target)
        f.write('\n'.join(body) + '\n')
    print('\nTile533Map.csv: re-pointed %d slot(s) %s'
          % (len(body), ', '.join('%d->%d' % (L, m) for L, m in sorted(bp.repoint.items()))))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('ids', nargs='*', type=int)
    ap.add_argument('--target', choices=['4x', '5x'], default='4x',
                    help='which client the ART is chosen for (the file format is the same)')
    ap.add_argument('--write', action='store_true',
                    help='write game-data/maps/TK<id>.map (and, for 5x, the Tile533Map.csv rows)')
    ap.add_argument('--check', action='store_true')
    args = ap.parse_args()
    if args.check:
        selfcheck()
        return
    if not args.ids:
        ap.error('give at least one RTK map id')

    bp = Backporter()
    if args.target == '5x':
        bp._free_slots()
        print('%d free sheet-2 slots (drawable by 4.95, referenced by no map of ours)'
              % len(bp.free_legacy))
    names = bp.rrm.rtk_map_names()
    rows = []
    for mid in args.ids:
        blob, xs, ys, rep = bp.convert(mid, args.target)
        report(bp, mid, rep)
        verify(bp, mid, blob, xs, ys)
        rows.append((mid, names.get(mid, 'Map %d' % mid), xs, ys))
        if args.write:
            p = os.path.join(OUT, 'TK%d.map' % mid)
            open(p, 'wb').write(blob)
            print('   wrote %s (%d bytes)' % (p, len(blob)))
    if args.write and args.target == '5x':
        write_repoints(bp, args.target)
    print('\nmap_index.csv rows:')
    for mid, nm, xs, ys in rows:
        print('%d,%s,%d,%d' % (mid, nm.replace("'", "\\'"), xs, ys))


if __name__ == '__main__':
    main()
