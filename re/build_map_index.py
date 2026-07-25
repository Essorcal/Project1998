"""Build a map index (id -> name, width, height) the server loads for warp/list commands.

CLIENT-AUTHORITATIVE: we iterate the 4.95 client's own TK<id>.map files and emit EVERY one — a map the
client ships is warpable, period. The client .map is headerless (raw 4-byte cells, no dims), so we only
need to split its cell count into (xs, ys). RTK's data merely *informs* that split; it never gates
existence:

  - RTK's Maps.csv          -> MapId, MapName, MapFile     (display name + id<->file mapping)
  - RTK's rtkmaps/Accepted/<MapFile>, first 4 bytes = xs(u16 BE), ys(u16 BE)   (exact dims / aspect hint)

Dim choice per map (cells = client filesize/4):
  1. If RTK dims exist and xs*ys == cells -> use them (exact, verified against the client geometry).
  2. Else pick the factor pair (w, h) of `cells` whose aspect ratio is closest to RTK's (7.x may have
     resized the map but the orientation is a good hint); with no RTK row, closest-to-square wins.
Any factor pair with the right product is SAFE — the client reads exactly the file; a wrong split only
skews the row-stride (map looks sheared), it never overruns or crashes. So we never drop a client map.

Name: RTK MapName if present, else "Map <id>" (still warpable by id / by that label).

Output: re/rtk-data/map_index.csv (id,name,xs,ys) — gitignored (logic-only repo, docs §17.1).
Env overrides: RTK_MAPS_CSV, RTK_MAPS_DIR, CLIENT_MAPS, OUT.
"""
import csv, os, struct, glob, math

HERE       = os.path.dirname(os.path.abspath(__file__))
MAPS_CSV   = os.environ.get('RTK_MAPS_CSV', os.path.join(HERE, 'rtk-data', 'Maps.csv'))
RTK_MAPS   = os.environ.get('RTK_MAPS_DIR', os.path.join(HERE, '..', 'RTK-Server', 'rtkmaps', 'Accepted'))
CLIENT_MAP = os.environ.get('CLIENT_MAPS', r'C:\Program Files (x86)\Nexon\NextAeon\Maps')
OUT        = os.environ.get('OUT', os.path.join(HERE, 'rtk-data', 'map_index.csv'))

def rtk_dims(mapfile):
    p = os.path.join(RTK_MAPS, mapfile or '')
    if not mapfile or not os.path.isfile(p):
        return None
    with open(p, 'rb') as f:
        head = f.read(4)
    return struct.unpack('>HH', head) if len(head) >= 4 else None

def factor_pairs(n):
    """All (w, h) with w*h == n and w <= h (so orientation is applied afterward)."""
    out = []
    i = 1
    while i * i <= n:
        if n % i == 0:
            out.append((i, n // i))
        i += 1
    return out

def choose_dims(cells, rtk):
    if cells <= 0:
        return None
    if rtk and rtk[0] * rtk[1] == cells:
        return rtk                                   # exact, verified
    pairs = factor_pairs(cells)                       # each is (small, large)
    target = (rtk[0] / rtk[1]) if rtk and rtk[1] else 1.0   # width/height hint (else square)
    # try both orientations of every pair; pick the closest aspect to the hint
    best, bestd = None, None
    for a, b in pairs:
        for w, h in ((a, b), (b, a)):
            d = abs((w / h) - target)
            if bestd is None or d < bestd:
                best, bestd = (w, h), d
    return best

# RTK id -> (name, mapfile), for names + dim hints
rtk_row = {}
with open(MAPS_CSV, encoding='utf-8') as f:
    for r in csv.DictReader(f):
        mid = r.get('MapId', '')
        if mid.isdigit():
            rtk_row[int(mid)] = ((r.get('MapName') or '').strip(), (r.get('MapFile') or '').strip())

rows, exact, guessed, unnamed = [], 0, 0, 0
for p in glob.glob(os.path.join(CLIENT_MAP, 'TK*.map')):
    num = os.path.basename(p)[2:-4]
    if not num.isdigit():
        continue
    mid = int(num)
    cells = os.path.getsize(p) // 4
    name, mapfile = rtk_row.get(mid, ('', ''))
    d = rtk_dims(mapfile)
    dims = choose_dims(cells, d)
    if dims is None:
        continue
    if d and d[0] * d[1] == cells:
        exact += 1
    else:
        guessed += 1
    if not name:
        name = f"Map {mid}"
        unnamed += 1
    rows.append((mid, name, dims[0], dims[1]))

rows.sort()
os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, 'w', newline='', encoding='utf-8') as f:
    w = csv.writer(f)
    w.writerow(['id', 'name', 'xs', 'ys'])
    w.writerows(rows)

print(f"wrote {len(rows)} client maps -> {OUT}")
print(f"  dims: {exact} exact (RTK-verified), {guessed} best-guess (aspect/square)   |   {unnamed} without an RTK name")
named = [r for r in rows if not r[1].startswith('Map ')]
print(f"  named: {len(named)}   e.g. " + ", ".join(f"{r[0]}={r[1]}" for r in named[:6]))
