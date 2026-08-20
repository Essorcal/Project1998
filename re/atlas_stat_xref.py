"""Final stat/requirement comparison, with the two false-positive classes removed:

 1. PATH: the Atlas prints the BASE class (Warrior) where we store the SUBPATH id (6 = Chung ryong).
    Compare through Paths.csv PthType, not the raw id.
 2. LEVEL: Atlas "Level 1" and our 0 both mean "no level requirement" - not a difference.
"""
import csv, json, re, collections
from _paths import DATA, RE, ROOT, RTK_LUA, ARCHIVE

ITEMS = DATA / 'Items.csv'
PATHS = DATA / 'Paths.csv'

_paths = list(csv.DictReader(open(PATHS, encoding='utf-8')))
BASE = {int(r['PthId']): int(r['PthType']) for r in _paths}
# PthId -> its own display name, so "Chung ryong scale = Chung ryong" is not reported as a mismatch
# against our stored subpath id 6 (whose BASE is Warrior).
OWN = {int(r['PthId']): r['PthMark0'].strip() for r in _paths}
NAME = {0: 'Peasant', 1: 'Warrior', 2: 'Rogue', 3: 'Mage', 4: 'Poet', 5: 'Dreamweaver'}

def path_ok(atlas_cls, pid):
    """The Atlas may name either the base class or the subpath; accept both."""
    a = atlas_cls.lower()
    return a in (NAME.get(BASE.get(pid, pid), '').lower(), OWN.get(pid, '').lower())

rows = list(csv.DictReader(open(ITEMS, encoding='utf-8')))
atlas = json.load(open('atlas_weapons.json'))

def norm(s):
    return ' '.join(re.sub(r'[^a-z0-9]+', ' ', s.lower().replace("\\'", "'")).split())

ALIAS = {'dark simitar': 'dark scimitar', 'serpant fan': 'serpent fan'}
by_name = collections.defaultdict(list)
for r in rows:
    by_name[norm(r['ItmDescription'])].append(r)

pairs = []
for a in atlas:
    c = by_name.get(ALIAS.get(norm(a['name']), norm(a['name'])), [])
    w = [x for x in c if x['ItmType'] == '3'] or c
    if w:
        pairs.append((a, w[0]))

FIELDS = [('sMin', 'ItmMinimumSDamage', 'small-dmg min'), ('sMax', 'ItmMaximumSDamage', 'small-dmg max'),
          ('lMin', 'ItmMinimumLDamage', 'large-dmg min'), ('lMax', 'ItmMaximumLDamage', 'large-dmg max'),
          ('ac', 'ItmArmor', 'AC'), ('hit', 'ItmHit', 'Hit'), ('dam', 'ItmDam', 'Dam'),
          ('vita', 'ItmVita', 'Vita'), ('mana', 'ItmMana', 'Mana'),
          ('might', 'ItmMight', 'Might'), ('will', 'ItmWill', 'Will'), ('grace', 'ItmGrace', 'Grace'),
          ('prot', 'ItmProtection', 'Protection'), ('heal', 'ItmHealing', 'Healing'),
          ('mightReq', 'ItmMightRequired', 'Might-to-wield'), ('dura', 'ItmDurability', 'Durability')]

d = collections.defaultdict(list)
for a, it in pairs:
    for ak, ik, lbl in FIELDS:
        if a[ak] is None:
            continue
        if a[ak] != int(it[ik]):
            d[lbl].append((a['name'], it['ItmIdentifier'], a[ak], int(it[ik])))
    pid = int(it['ItmPthId'])
    ourbase = BASE.get(pid, pid)
    for acls, alvl in a['classes']:
        if not path_ok(acls, pid):
            d['CLASS'].append((a['name'], it['ItmIdentifier'], acls, NAME.get(ourbase, pid)))
        if alvl != int(it['ItmLevel']) and not (alvl == 1 and int(it['ItmLevel']) == 0):
            d['LEVEL'].append((a['name'], it['ItmIdentifier'], alvl, int(it['ItmLevel'])))
        break
    for acls, amark in a['marks']:
        if not path_ok(acls, pid):
            d['CLASS'].append((a['name'], it['ItmIdentifier'], acls, NAME.get(ourbase, pid)))
        if amark != int(it['ItmMark']):
            d['MARK'].append((a['name'], it['ItmIdentifier'], amark, int(it['ItmMark'])))
        break

print(f'compared {len(pairs)} weapons\n')
tot = 0
for k in ['CLASS', 'LEVEL', 'MARK'] + [f[2] for f in FIELDS]:
    if d.get(k):
        tot += len(d[k])
        print(f'--- {k}  ({len(d[k])}) ---')
        for nm, ident, av, iv in sorted(d[k]):
            print(f'    {nm:28s} {ident:27s} atlas={str(av):<9} ours={iv}')
        print()
print(f'total differences: {tot}')
json.dump({k: v for k, v in d.items()}, open('stat_diffs.json', 'w'), indent=1)
