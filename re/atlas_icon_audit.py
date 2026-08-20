"""Full graphics audit: for every Atlas weapon whose icon .gif we have, rank our Item.epf frames
and compare the winner against the item's current ItmIcon in Items.csv.

Scoring note: the matcher is validated on known-good pairs (long_spear 52, steel_dagger 1,
steelflash 46, star_staff 64) which all land < 10. So:
   score < 12  -> confident match
   12..35      -> plausible (palette/era drift)
   > 35        -> no usable match, report as UNRESOLVED rather than guessing.
"""
import csv, json, os, re, collections, warnings
warnings.filterwarnings('ignore')
from match_item_icon import best
from _paths import DATA, RE, ROOT, RTK_LUA, ARCHIVE

FRAMES_495 = 1310
CONFIDENT, PLAUSIBLE = 12, 35

rows = list(csv.DictReader(open(DATA / 'Items.csv', encoding='utf-8')))
atlas = json.load(open('atlas_weapons.json'))

def norm(s):
    s = s.lower().replace("\\'", "'")
    return ' '.join(re.sub(r'[^a-z0-9]+', ' ', s).split())

ALIAS = {'dark simitar': 'dark scimitar', 'serpant fan': 'serpent fan'}
by_name = collections.defaultdict(list)
for r in rows:
    by_name[norm(r['ItmDescription'])].append(r)

# one match pass per Atlas entry
pairs = []
for a in atlas:
    c = by_name.get(ALIAS.get(norm(a['name']), norm(a['name'])), [])
    w = [x for x in c if x['ItmType'] == '3'] or c
    if w:
        pairs.append((a, w[0]))

# rank each distinct gif ONCE (expensive), then apply to every item using it
gifs = sorted({a['icon'].lower() for a, _ in pairs if a['icon']})
ranked = {}
for i, g in enumerate(gifs, 1):
    p = os.path.join('atlasgif', g + '.gif')
    if not os.path.exists(p):
        continue
    try:
        ranked[g] = best(p, topn=6)
    except Exception as e:
        print(f'  ERR {g}: {e}')
    if i % 20 == 0:
        print(f'  ...ranked {i}/{len(gifs)}')

out = []
for a, it in pairs:
    g = (a['icon'] or '').lower()
    r = ranked.get(g)
    if not r:
        continue
    s, frame = r[0]
    out.append(dict(name=a['name'], ident=it['ItmIdentifier'], gif=g,
                    cur=int(it['ItmIcon']), sugg=frame - 1, score=s,
                    alts=[(f - 1, round(sc, 1)) for sc, f in r[:4]]))

json.dump(out, open('icon_audit.json', 'w'), indent=1)

agree = [o for o in out if o['cur'] == o['sugg']]
conf = [o for o in out if o['cur'] != o['sugg'] and o['score'] < CONFIDENT]
plaus = [o for o in out if o['cur'] != o['sugg'] and CONFIDENT <= o['score'] < PLAUSIBLE]
weak = [o for o in out if o['cur'] != o['sugg'] and o['score'] >= PLAUSIBLE]

print(f'\naudited {len(out)} weapons with a reference gif')
print(f'  agree with our ItmIcon : {len(agree)}')
print(f'  CONFIDENT mismatch     : {len(conf)}')
print(f'  plausible mismatch     : {len(plaus)}')
print(f'  no usable match        : {len(weak)}')

def dump(title, lst):
    print(f'\n{"="*80}\n{title}\n{"="*80}')
    for o in sorted(lst, key=lambda x: x['score']):
        bad = '  [INVISIBLE on 4.95]' if o['cur'] >= FRAMES_495 else ''
        print(f"  {o['name']:28s} {o['ident']:26s} cur={o['cur']:<6} -> {o['sugg']:<5} "
              f"(score {o['score']:.1f}, {o['gif']}.gif){bad}")
        print(f"      alts: {o['alts']}")

dump('CONFIDENT ICON MISMATCHES (score < 12 — matcher validated at <10 on known pairs)', conf)
dump('PLAUSIBLE ICON MISMATCHES (12-35 — verify by eye)', plaus)
dump('NO USABLE MATCH (>35 — do NOT auto-apply)', weak)
