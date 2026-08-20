"""Extract reusable game-content tables (Maps/Warps/Spawns/NPCs/Items/Spells/Paths/Mobs) from the RTK
reference server's MySQL dump into CSVs, and report how much overlaps our 4.95 client.

RTK is content data, NOT part of this (logic-only) repo. Clone it and point SQL/OUT at it:
    git clone https://github.com/unkmc/RTK-Server
    python re/rtk_extract.py            # edit SQL/OUT below, or set RTK_SQL / RTK_OUT env vars
The output CSVs are gitignored on purpose (keep game data outside the repo). See docs §17.1.
"""
import re, csv, os, glob
from _paths import CLIENT

SQL = os.environ.get('RTK_SQL', 'RTK-Server/database/2020-09-02-21-55-01_RTK.sql.bak')
sql = open(SQL, encoding='latin1').read()
OUT = os.environ.get('RTK_OUT', os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'data', 'game-data'))
os.makedirs(OUT, exist_ok=True)

def schema(tbl):
    m = re.search(r"CREATE TABLE `" + re.escape(tbl) + r"` \((.*?)\n\)\s*ENGINE", sql, re.S)
    return re.findall(r"^\s*`(\w+)`", m.group(1), re.M) if m else []

def split_vals(r):
    out, cur, q, i = [], '', False, 0
    while i < len(r):
        c = r[i]
        if c == "'" and (i == 0 or r[i-1] != '\\'):
            q = not q; cur += c
        elif c == ',' and not q:
            out.append(cur); cur = ''
        else:
            cur += c
        i += 1
    out.append(cur)
    return [x.strip().strip("'").replace('\\r', '').replace('\\n', ' ') for x in out]

def rows(tbl):
    out = []
    for m in re.finditer(r"INSERT INTO `" + re.escape(tbl) + r"` (?:\([^)]*\)\s*)?VALUES (.*?);\s*\n", sql, re.S):
        for r in re.findall(r"\(((?:[^()']|'(?:[^'\\]|\\.)*')*)\)", m.group(1)):
            out.append(split_vals(r))
    return out

def dump(tbl):
    cols = schema(tbl)
    rs = rows(tbl)
    path = os.path.join(OUT, tbl + '.csv')
    with open(path, 'w', newline='', encoding='utf-8') as f:
        w = csv.writer(f); w.writerow(cols)
        for r in rs:
            w.writerow(r[:len(cols)])
    return cols, rs

# our client's available map ids
client_maps = set()
for p in glob.glob(str(CLIENT / "Maps" / "TK*.map")):
    m = re.match(r'TK(\d+)\.map', os.path.basename(p))
    if m:
        client_maps.add(int(m.group(1)))

for tbl in ['Maps', 'Warps', 'Spawns0', 'NPCs0', 'Items', 'Spells', 'Paths', 'MobEquipment', 'NPCEquipment0']:
    cols, rs = dump(tbl)
    print(f"{tbl}: {len(rs)} rows -> {tbl}.csv")

# cross-reference overlap with our client map files
mcols, mrows = schema('Maps'), rows('Maps')
mid_i = mcols.index('MapId')
rtk_map_ids = set(int(r[mid_i]) for r in mrows if r[mid_i].isdigit())
overlap = rtk_map_ids & client_maps
print(f"\n=== MAP OVERLAP ===")
print(f"RTK maps: {len(rtk_map_ids)} | client .map files: {len(client_maps)} | BOTH: {len(overlap)}")

# warps whose BOTH source and dest maps exist in our client
wcols, wrows = schema('Warps'), rows('Warps')
si, di = wcols.index('SourceMapId'), wcols.index('DestinationMapId')
valid_warps = [r for r in wrows if r[si].isdigit() and r[di].isdigit()
               and int(r[si]) in client_maps and int(r[di]) in client_maps]
print(f"Warps usable (both endpoints have a client .map): {len(valid_warps)} / {len(wrows)}")

# spawns on maps our client has
scols, srows = schema('Spawns0'), rows('Spawns0')
smi = scols.index('SpnMapId')
valid_spawns = [r for r in srows if r[smi].isdigit() and int(r[smi]) in client_maps]
print(f"Spawns usable (map exists in client): {len(valid_spawns)} / {len(srows)}")

# npcs on maps our client has
ncols, nrows = schema('NPCs0'), rows('NPCs0')
nmi = ncols.index('NpcMapId')
valid_npcs = [r for r in nrows if r[nmi].isdigit() and int(r[nmi]) in client_maps]
print(f"NPCs usable (map exists in client): {len(valid_npcs)} / {len(nrows)}")

# named maps sample that overlap
print("\n=== sample overlapping named maps ===")
nm_i = mcols.index('MapName')
shown = 0
for r in mrows:
    if r[mid_i].isdigit() and int(r[mid_i]) in client_maps and r[nm_i] and not r[nm_i].startswith('TK'):
        print(f"  map {r[mid_i]:>5} = {r[nm_i]}")
        shown += 1
        if shown >= 18:
            break
