"""Parse Nexus Atlas weapon pages (swords/fans/bows/staves) into structured rows.

Page layout: each weapon is a PAIR of <tr>s --
  row A: <td rowspan=2> with photo/weapons/<icon>.gif and photo/weapons/hand/<equip>.gif, then <strong>Name</strong>
  row B: <td> stat block </td><td> info block </td>
We split on the rowspan cell, so each chunk = one weapon.
"""
import re, json, sys, csv
from _paths import DATA, RE, ROOT, RTK_LUA, ARCHIVE

# Class display names straight out of Paths.csv (PthMark0), longest-first so "Chung ryong" wins over "Do".
CLASS_NAMES = sorted(
    {r['PthMark0'].strip() for r in csv.DictReader(
        open(DATA / 'Paths.csv', encoding='utf-8'))
     if r['PthMark0'].strip()},
    key=len, reverse=True)
CLASS_ALT = '|'.join(re.escape(c) for c in CLASS_NAMES)
CLASS_RE = r'(' + CLASS_ALT + r')\s*level\s*(\d+)'
# Rank/mark requirement instead of a level: "Il san Rogue", "Ee san Peasant", "Il san (Peasant)".
MARKS = {'il': 1, 'ee': 2, 'sam': 3, 'sa': 4, 'oh': 5}
MARK_RE = r'\b(il|ee|sam|sa|oh)\s+san\s*\(?\s*(' + CLASS_ALT + r')\s*\)?'

def clean(h):
    """HTML fragment -> flat text with tags stripped and whitespace collapsed."""
    h = re.sub(r'<br\s*/?>', '\n', h, flags=re.I)
    h = re.sub(r'<[^>]+>', ' ', h)
    h = h.replace('&nbsp;', ' ').replace('&amp;', '&').replace('&quot;', '"').replace("&#39;", "'")
    h = re.sub(r'[ \t]+', ' ', h)
    return h

def num(pat, text, default=None, cast=int):
    m = re.search(pat, text, re.I)
    if not m:
        return default
    try:
        # the page writes large values with thousands separators ("Mana : 15,000")
        return cast(m.group(1).replace(',', ''))
    except ValueError:
        return default

def parse_page(path, page):
    raw = open(path, encoding='latin1').read()
    i = raw.find('END WAYBACK TOOLBAR INSERT')
    if i > 0:
        raw = raw[i:]

    # split into weapon chunks on the rowspan="2" icon cell
    parts = re.split(r'<td[^>]*rowspan="?2"?[^>]*>', raw, flags=re.I)
    out = []
    for chunk in parts[1:]:
        # cut the chunk at the start of the NEXT weapon's outer row so stats don't bleed
        # (the split already does this; just guard against trailing page furniture)
        imgs = re.findall(r'photo/weapons/(?:hand/)?([A-Za-z0-9_\-. ]+?)\.gif', chunk, re.I)
        icon = imgs[0] if imgs else None
        hand = imgs[1] if len(imgs) > 1 else None

        mname = re.search(r'<strong>\s*(.*?)\s*</strong>', chunk, re.S | re.I)
        if not mname:
            continue
        name = clean(mname.group(1)).strip()
        if not name or len(name) > 60:
            continue

        t = clean(chunk)
        t = re.sub(r'\n+', '\n', t)

        # Damage - S: 1m5 L: 1m5   (also tolerate "S : 1 m 5")
        dmg = re.search(r'Damage\s*-?\s*S\s*:\s*([\d,]+)\s*m\s*([\d,]+)\s*L\s*:\s*([\d,]+)\s*m\s*([\d,]+)', t, re.I)
        smin, smax, lmin, lmax = (int(dmg.group(k).replace(',', '')) for k in (1, 2, 3, 4)) if dmg else (None,) * 4

        dura = re.search(r'Durability\s*-?\s*([\d,]+)\s*/\s*([\d,]+)', t, re.I)

        # "<Class> Level <N>" — base classes AND subpath names. Explicit alternation (from Paths.csv
        # PthMark0) because the page mixes "Level" and "level", so a generic \w+ would swallow junk.
        classes = re.findall(CLASS_RE, t, re.I)
        # Mark requirements live in the same slot as the level line; keep both, scoped to the
        # requirement window (between "Healing" and "Might to Wield") so prose can't false-positive.
        win = t
        h, w = t.lower().find('healing'), t.lower().find('might to wield')
        if 0 <= h < w:
            win = t[h:w]
        marks = [(cl, MARKS[m.lower()]) for m, cl in re.findall(MARK_RE, win, re.I)]

        rec = dict(
            page=page, name=name, icon=icon, hand=hand,
            dura=int(dura.group(2).replace(',', '')) if dura else None,
            sMin=smin, sMax=smax, lMin=lmin, lMax=lmax,
            ac=num(r'\bAC\b\s*:\s*(-?[\d,]+)', t),
            hit=num(r'\bHit\b\s*:\s*(-?[\d,]+)', t),
            dam=num(r'\bDam\b\s*:\s*(-?[\d,]+)', t),
            vita=num(r'\bVita\b\s*:\s*(-?[\d,]+)', t),
            mana=num(r'\bMana\b\s*:\s*(-?[\d,]+)', t),
            might=num(r'\bMight\b\s*:\s*(-?[\d,]+)', t),
            will=num(r'\bWill\b\s*:\s*(-?[\d,]+)', t),
            grace=num(r'\bGrace\b\s*:\s*(-?[\d,]+)', t),
            prot=num(r'\bProtection\b\s*:\s*(-?[\d,]+)', t),
            heal=num(r'\bHealing\b\s*:\s*(-?[\d,]+)', t),
            mightReq=num(r'Might to Wield\s*:?\s*(-?[\d,]+)', t),
            classes=[(c.title(), int(l)) for c, l in classes],
            marks=[(c.title(), m) for c, m in marks],
            casts=(re.search(r'Casts\s*:\s*([^\n]*)', t, re.I).group(1).strip() if re.search(r'Casts\s*:', t, re.I) else None),
            npcSell=num(r'Price NPC Sells For\s*-?\s*([\d,]+)', t, cast=lambda s: int(s.replace(',', ''))),
            npcBuy=num(r'Price NPC Buys For\s*-?\s*([\d,]+)', t, cast=lambda s: int(s.replace(',', ''))),
        )
        out.append(rec)
    return out

def main():
    all_rows = []
    for page in ('swords', 'fans', 'bows', 'staves'):
        rows = parse_page(f'atlas_{page}.html', page)
        print(f'{page}: {len(rows)} entries')
        all_rows += rows
    json.dump(all_rows, open('atlas_weapons.json', 'w'), indent=1)
    print(f'total {len(all_rows)}')
    # show a few for eyeball
    for r in all_rows[:3] + all_rows[-2:]:
        print(json.dumps(r))

if __name__ == '__main__':
    main()
