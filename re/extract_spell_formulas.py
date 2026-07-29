"""Extract RTK's Lua spell scripts into a data-driven table the C# 'magic engine' consumes.

Each RTK spell is a Lua table `NAME = { cast = function(player[,target]) ... end, ... }` whose cast body
computes a damage/heal number from player stats and calls a shared helper
(`global_zap`/`global_attack`/`global_heal`.cast(player, target, AMOUNT, MANACOST, pcalign)), or applies a
timed stat buff (`setDuration` + recast `player.<stat> = player.<stat> + N`), a debuff/status on a mob,
a cure (`removeDuras(category)`), or the mana-battery pattern (`player.magic = player.maxMagic`).

We classify each into an archetype and pull out the numeric params + the *formula string* for the amount,
so C# can evaluate the real RTK formula (a tiny expression evaluator handles the ~30 distinct expressions).

RTK is gitignored reference data. Run: python re/extract_spell_formulas.py
Writes data/game-data/spell_effects.csv (gitignored) + prints an archetype coverage report.
"""
import os, re, glob, csv
from collections import Counter

ROOT = os.path.dirname(os.path.abspath(__file__))
SPELLS = os.path.join(ROOT, '..', 'RTK-Server', 'rtklua', 'Accepted', 'Spells')
OUT = os.path.join(ROOT, '..', 'data', 'game-data'); os.makedirs(OUT, exist_ok=True)


def tables(text):
    """Split a lua file into top-level `NAME = { ... }` tables (brace-matched)."""
    out = []
    for m in re.finditer(r'(\w+)\s*=\s*\{', text):
        name = m.group(1); i = m.end() - 1; depth = 0
        while i < len(text):
            if text[i] == '{': depth += 1
            elif text[i] == '}':
                depth -= 1
                if depth == 0: break
            i += 1
        out.append((name, text[m.end():i]))
    return out


def subfn(body, fname):
    """Extract a named sub-function body: `fname = function(...) ... end` (brace/keyword unaware -> depth by
    counting function/end). Good enough for these small, flat spell functions."""
    m = re.search(r'\b' + fname + r'\s*=\s*function\s*\([^)]*\)', body)
    if not m: return ''
    i = m.end(); depth = 1
    toks = re.finditer(r'\b(function|if|for|while|do|end)\b', body[i:])
    last = i
    for t in toks:
        w = t.group(1)
        if w in ('function', 'if', 'for', 'while'): depth += 1
        elif w == 'do': continue  # `do` pairs handled loosely; spells rarely use bare do-blocks
        elif w == 'end':
            depth -= 1
            if depth == 0:
                return body[i:i + t.start()]
    return body[i:]


def split_args(s):
    """Split a call's argument list on top-level commas."""
    args, depth, cur = [], 0, ''
    for ch in s:
        if ch in '([{': depth += 1
        elif ch in ')]}': depth -= 1
        if ch == ',' and depth == 0:
            args.append(cur.strip()); cur = ''
        else:
            cur += ch
    if cur.strip(): args.append(cur.strip())
    return args


def resolve_local(body, name):
    """Return the RHS expression of `local <name> = <expr>` (first occurrence)."""
    m = re.search(r'local\s+' + re.escape(name) + r'\s*=\s*([^\n]+)', body)
    return m.group(1).strip() if m else ''


def num(pat, body, default=''):
    m = re.search(pat, body)
    return m.group(1) if m else default


def classify(body):
    b = body
    if 'player.magic = player.maxMagic' in b or 'healthCost' in b: return 'ManaBattery'
    if re.search(r'global_(zap|attack)\.cast', b) or re.search(r'\blocal damage\b', b): return 'Damage'
    if 'global_heal.cast' in b or re.search(r'addHealthExtend|player\.health\s*=\s*player\.health\s*\+', b): return 'Heal'
    if 'setDuration' in b:
        # debuff if it durations a *target*, else a self/ally buff
        return 'Debuff' if re.search(r'target\s*:\s*setDuration', b) or 'paralyzed' in b else 'Buff'
    if 'removeDuras' in b or 'removeHealthExtend' not in b and re.search(r'\bcure\b', b, re.I): return 'Cure'
    if 'removeDuras' in b: return 'Cure'
    if 'warp' in b: return 'Teleport'
    if 'addNPC' in b or 'summon' in b.lower(): return 'Summon'
    if 'dialog' in b or 'menuString' in b: return 'Dialog'
    return 'Utility'


# stat names RTK mutates in recast/uncast, mapped to our canonical set
STAT_MAP = {
    'might': 'might', 'grace': 'grace', 'will': 'will', 'health': 'hp', 'maxHealth': 'maxhp',
    'magic': 'mp', 'maxMagic': 'maxmp', 'armor': 'armor', 'hit': 'hit', 'dam': 'dam',
    'regen': 'regen', 'ac': 'armor',
}


def buff_mods(body):
    """From a buff's recast function, collect (stat, delta) stat modifications."""
    recast = subfn(body, 'recast')
    mods = []
    for m in re.finditer(r'player\.(\w+)\s*=\s*player\.\1\s*([+\-])\s*([0-9.]+)', recast):
        stat, sign, amt = m.group(1), m.group(2), m.group(3)
        canon = STAT_MAP.get(stat)
        if not canon: continue
        val = float(amt) if '.' in amt else int(amt)
        mods.append((canon, val if sign == '+' else -val))
    return mods


rows = []
cats = Counter()
for path in glob.glob(os.path.join(SPELLS, '*', '*.lua')):
    cls = os.path.basename(os.path.dirname(path))
    if cls in ('NPCs', 'common'): continue
    text = open(path, encoding='latin1').read()
    for name, body in tables(text):
        cast = subfn(body, 'cast')
        if not cast: continue
        cat = classify(cast)
        cats[cat] += 1

        amount_expr, mana, pcalign = '', '', ''
        gm = re.search(r'global_(?:zap|attack|heal)\.cast\(([^;]*?)\)', cast)
        if gm:
            a = split_args(gm.group(1))
            # (player, target, AMOUNT, MANACOST, pcalign)
            if len(a) >= 5:
                amt_tok, mana, pcalign = a[2], a[3], a[4]
                amount_expr = resolve_local(cast, amt_tok) or amt_tok

        # mana cost fallbacks for non-global spells
        if mana == '' or not re.match(r'^-?\d+$', mana.strip()):
            mana = (num(r'local\s+magicCost\s*=\s*(\d+)', cast)
                    or num(r'local\s+magic\s*=\s*(\d+)', cast)
                    or num(r'minMagic\s*=\s*(\d+)', cast)
                    or (mana if re.match(r'^-?\d+$', str(mana).strip()) else ''))

        buff_stat, buff_amt = '', ''
        duration = num(r'setDuration\([^,]+,\s*(\d+)', cast)
        if cat == 'Buff':
            mods = buff_mods(body)
            if mods:
                buff_stat = '|'.join(s for s, _ in mods)
                buff_amt = '|'.join(str(v) for _, v in mods)

        debuff_kind = ''
        if cat == 'Debuff':
            if 'paralyzed' in cast: debuff_kind = 'paralyze'
            elif re.search(r'\bblind', cast, re.I): debuff_kind = 'blind'
            elif re.search(r'\bsleep|doze', cast, re.I): debuff_kind = 'sleep'
            else: debuff_kind = 'slow'

        cure_cat = num(r'removeDuras\((\w+)\)', cast)
        chance = ''
        cm = re.search(r'local\s+chance\s*=\s*([^\n]+)', cast)
        if cm: chance = cm.group(1).strip()
        health_cost = num(r'local\s+healthCost\s*=\s*([^\n]+)', cast).strip()

        rows.append({
            'key': name, 'class': cls, 'archetype': cat,
            'mana': str(mana).strip(),
            'amountExpr': amount_expr.replace('\t', ' ').strip(),
            'pcalign': str(pcalign).strip(),
            'buffStat': buff_stat, 'buffAmt': buff_amt, 'durationMs': duration,
            'debuff': debuff_kind, 'chance': chance, 'cureCat': cure_cat,
            'healthCost': health_cost,
            'animation': num(r'sendAnimation\((\d+)', cast),
            'sound': num(r'playSound\((\d+)\)', cast),
            'action': num(r'sendAction\((\d+)', cast),
            'aether': num(r'setAether\([^,]+,\s*(\d+)\)', cast) or num(r'local aether\s*=\s*(\d+)', cast),
        })

FIELDS = ['key', 'class', 'archetype', 'mana', 'amountExpr', 'pcalign', 'buffStat', 'buffAmt',
          'durationMs', 'debuff', 'chance', 'cureCat', 'healthCost', 'animation', 'sound', 'action', 'aether']
with open(os.path.join(OUT, 'spell_effects.csv'), 'w', newline='', encoding='utf-8') as f:
    w = csv.DictWriter(f, fieldnames=FIELDS)
    w.writeheader(); w.writerows(rows)

print(f"{len(rows)} spell tables -> data/game-data/spell_effects.csv\n")
total = sum(cats.values())
for c, n in cats.most_common():
    print(f"  {c:12} {n:5}  ({100*n/total:4.1f}%)")
core = ['Damage', 'Heal', 'Buff', 'Debuff', 'ManaBattery', 'Cure']
print(f"\n  core gameplay archetypes: {sum(cats[k] for k in core)}/{total} "
      f"({100*sum(cats[k] for k in core)/total:.0f}%)")

# sanity: how many Damage/Heal have a usable amount expression + mana?
dmg = [r for r in rows if r['archetype'] in ('Damage', 'Heal')]
have = [r for r in dmg if r['amountExpr'] and re.match(r'^-?\d+$', r['mana'] or '')]
print(f"  Damage/Heal with amountExpr + numeric mana: {len(have)}/{len(dmg)}")
