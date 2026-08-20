#!/usr/bin/env python
"""Add Buya Library Caverns mob spawns + the missing warps to game-data CSVs.

Mobs: reproduces RTK mobSpawnHandler.lua's `for i=6502,6598,20` loop verbatim -- the same 16 handleSpawn
calls per tier (blind mice/rats/centipede/mantis, ids 198-203), whole-map bounds, 300s batch refill.

Warps: RTK's SQL already wired tiers 2-5 internally (kept as-is). It left TIER 1 (6502-6519) completely
unwired (the low-level path via eventCaveLevelPrompt) -- we mirror tier 2's warps down (map-20; tier maps
are byte-identical, verified). RTK's only outside link is 6583->486; we add a Buya Library ENTRY (the RTK
scripted tier-select tile is not ported -- static entry to tier 1) and a library EXIT for each tier's
entry room.
"""
import csv, os

HERE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(HERE, "..", "game-data")
SPAWNS = os.path.join(DATA, "AreaSpawns.csv")
WARPS  = os.path.join(DATA, "Warps.csv")

TIER_BASES = [6502, 6522, 6542, 6562, 6582]
ENTRY_ROOMS = [b + 1 for b in TIER_BASES]  # 6503, 6523, ...  (eventCaveLevelPrompt maps[])

# (offset, [mob ids], [max counts]) — exact from mobSpawnHandler.lua (i+10 guard and i+17 gloth omitted)
SPAWN_TABLE = [
    (0,  [198,199,200], [15,18,7]),
    (1,  [198,199],      [10,10]),
    (2,  [200,202],      [12,8]),
    (3,  [198,199,200], [15,18,7]),
    (4,  [198,199,200], [15,18,7]),
    (5,  [198,199,200], [15,18,7]),
    (6,  [200,202],      [12,8]),
    (7,  [198,199,200], [15,18,7]),
    (8,  [200,202],      [12,8]),
    (9,  [200,202],      [12,8]),
    (11, [200,201,203], [12,8,4]),
    (12, [201,202,203], [12,8,4]),
    (13, [201,202,203], [12,8,4]),
    (14, [201,202,203], [12,8,4]),
    (15, [201,202,203], [12,8,4]),
    (16, [201,202,203], [12,8,4]),
]
TIMER = 300

def in_cav(m):    return 6502 <= m <= 6599
def in_tier1(m):  return 6502 <= m <= 6519

def do_spawns():
    rows = []
    with open(SPAWNS, newline="", encoding="utf-8-sig") as f:
        r = csv.reader(f); header = next(r)
        for row in r:
            if row and row[0].isdigit() and in_cav(int(row[0])):
                continue   # drop any prior cavern spawns (idempotent)
            if row: rows.append(row)
    added = 0
    for base in TIER_BASES:
        for off, mobs, counts in SPAWN_TABLE:
            m = base + off
            for mob, cnt in zip(mobs, counts):
                rows.append([m, mob, cnt, 0, 0, 0, 0, TIMER, 1])
                added += 1
    with open(SPAWNS, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f); w.writerow(header); w.writerows(rows)
    print(f"AreaSpawns.csv: +{added} cavern spawn rows across {len(TIER_BASES)} tiers")

def do_warps():
    rows = []
    maxid = 0
    tier2 = []   # warps fully inside tier 2, to mirror onto tier 1
    with open(WARPS, newline="", encoding="utf-8-sig") as f:
        r = csv.reader(f); header = next(r)
        for row in r:
            if not row: continue
            try: wid, sm, sx, sy, dm, dx, dy = map(int, row[:7])
            except ValueError: rows.append(row); continue
            maxid = max(maxid, wid)
            # drop rows we (re)generate: anything touching tier 1, and 486<->cavern links (idempotent)
            if in_tier1(sm) or in_tier1(dm): continue
            if (sm == 486 and in_cav(dm)) or (dm == 486 and in_cav(sm)): continue
            rows.append(row)
            if 6522 <= sm <= 6539 and 6522 <= dm <= 6539:
                tier2.append((sx, sy, dx, dy, sm, dm))
    new = []
    wid = maxid
    # 1) mirror tier 2 internal warps down to tier 1 (map-20)
    for sx, sy, dx, dy, sm, dm in tier2:
        wid += 1
        new.append([wid, sm-20, sx, sy, dm-20, dx, dy])
    # 2) Buya Library (486) ENTRY -> tier 1 entry room 6503 at (9,1); doorway tiles (13,0) and (14,0)
    for ex in (13, 14):
        wid += 1
        new.append([wid, 486, ex, 0, 6503, 9, 1])
    # 3) EXIT from each tier's entry room back to Buya Library (9,0)->486(13,1)  (tier 5 already had one)
    for er in ENTRY_ROOMS:
        wid += 1
        new.append([wid, er, 9, 0, 486, 13, 1])
    with open(WARPS, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f); w.writerow(header); w.writerows(rows); w.writerows(new)
    print(f"Warps.csv: +{len(tier2)} tier-1 mirrored, +2 entry, +{len(ENTRY_ROOMS)} exits  ({len(new)} new rows)")

if __name__ == "__main__":
    do_spawns()
    do_warps()
