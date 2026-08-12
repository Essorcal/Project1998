#!/usr/bin/env python3
"""Extract every spell table's `requirements()` return values (level, items, itemAmounts, description)
from the RTK Lua spell scripts -- the real per-class/per-spell learn cost baseline. Walks
warrior/rogue/mage/poet/Subpaths/common/GM/dog/instance/baseFunc so every spell table is covered, not just
the "peasant commons" already ported by hand.

Best-effort static parse (no real Lua interpreter, hand-rolled brace-aware scanner since table literals span
multiple lines): finds each top-level `name = { ... }` table, locates its `requirements = function(player)
... end` block (or an alias `requirements = other.requirements` / `return other.requirements(player)`,
resolved in a second pass against already-parsed tables), and reads `local x = <literal>` assignments for
the level/items/itemAmounts/description variables (names vary per file). `Item("key").id` / `Item("key")`
item references are resolved to the item KEY string (what our C# LearnCost table actually keys on), not the
numeric id. Skips (flags parse_ok=False) anything else computed at runtime (baseClass branches, table.insert
loops, arithmetic) -- those need a human/manual read, not a wrong automatic answer.

Output: JSON list of {key, file, level, items: [[item_key_or_id, amount], ...], description, parse_ok}.
"""
import json, re
from pathlib import Path

ROOT = Path(r"C:\Users\brian\Desktop\Project1998\RTK-Server\rtklua\Accepted\Spells")
CLASS_DIRS = ["warrior", "rogue", "mage", "poet", "common", "Subpaths", "GM", "dog", "instance", "baseFunc"]

TABLE_RE = re.compile(r'^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*\{', re.MULTILINE)
ITEM_CALL_RE = re.compile(r'Item\(\s*"([^"]+)"\s*\)(?:\.id)?')

def find_balanced(text, start):
    """text[start] is the opening brace/paren; return index just after its matching close."""
    depth = 0
    i = start
    while i < len(text):
        if text[i] in '{(':
            depth += 1
        elif text[i] in '})':
            depth -= 1
            if depth == 0:
                return i + 1
        i += 1
    return len(text)

def extract_locals(body):
    """Scan `local NAME = <expr>` assignments in a function body, expr can span multiple lines if it
    starts with { or (. Returns {name: raw_expr_string}."""
    out = {}
    for m in re.finditer(r'local\s+(\w+)\s*=\s*', body):
        name = m.group(1)
        start = m.end()
        if start < len(body) and body[start] in '{(':
            end = find_balanced(body, start)
            expr = body[start:end]
        else:
            nl = body.find('\n', start)
            expr = body[start:nl if nl >= 0 else len(body)]
        out[name] = expr.strip().rstrip(',')
    return out

def parse_literal(expr, item_mode=False):
    expr = expr.strip().rstrip(',')
    if re.fullmatch(r'-?\d+(\.\d+)?', expr):
        return (float(expr) if '.' in expr else int(expr)), True
    if re.fullmatch(r'"[^"]*"', expr) or re.fullmatch(r"'[^']*'", expr):
        return expr[1:-1], True
    im = ITEM_CALL_RE.fullmatch(expr)
    if im:
        return im.group(1), True
    if expr.startswith('{') and expr.endswith('}'):
        inner = expr[1:-1].strip()
        if inner == '':
            return [], True
        parts, ok = [], True
        for part in split_top_level(inner):
            part = part.strip()
            if part == '':
                continue
            v, pok = parse_literal(part)
            parts.append(v)
            ok = ok and pok
        return parts, ok
    return f"<?{expr}>", False

def split_top_level(s):
    parts, depth, cur = [], 0, ''
    for ch in s:
        if ch in '{(':
            depth += 1
        elif ch in '})':
            depth -= 1
        if ch == ',' and depth == 0:
            parts.append(cur); cur = ''
        else:
            cur += ch
    if cur.strip():
        parts.append(cur)
    return parts

def extract_from_file(path):
    text = path.read_text(encoding='utf-8', errors='replace')
    results = []
    table_starts = [(m.group(1), m.start(), m.end() - 1) for m in TABLE_RE.finditer(text)]
    for idx, (key, start, brace_open) in enumerate(table_starts):
        next_start = table_starts[idx + 1][1] if idx + 1 < len(table_starts) else len(text)
        table_end = find_balanced(text, brace_open)
        block = text[start:min(table_end, next_start)]

        rm = re.search(r'requirements\s*=\s*function\s*\(\s*player\s*\)', block)
        alias = None
        req_body = None
        if rm:
            fn_start = rm.end()
            fn_end_end = re.search(r'\n\s*end\b', block[fn_start:])
            req_body = block[fn_start: fn_start + fn_end_end.start()] if fn_end_end else block[fn_start:]
        else:
            am = re.search(r'requirements\s*=\s*([A-Za-z_]\w*)\.requirements\b', block)
            if am:
                alias = am.group(1)

        if req_body is None and alias is None:
            continue  # no requirements at all on this table (e.g. a helper table, not a real spell)

        entry = {"key": key, "file": str(path.relative_to(ROOT.parent.parent.parent)),
                 "level": None, "items": [], "description": None, "parse_ok": False, "alias_of": alias}

        if req_body is not None:
            locals_ = extract_locals(req_body)
            returns = re.findall(r'\breturn\s+([^\n]+)', req_body)
            ret_line = returns[-1] if returns else None
            # `requirements = function(player) return OTHER.requirements(player) end` -- a full-function
            # delegate (as opposed to the field-alias `requirements = OTHER.requirements` caught above).
            dm = re.fullmatch(r'(\w+)\.requirements\(\s*player\s*\)', ret_line.strip()) if ret_line else None
            if dm:
                entry['alias_of'] = dm.group(1)
                results.append(entry)
                continue
            parse_ok = ret_line is not None
            if ret_line:
                ret_vars = [v.strip() for v in split_top_level(ret_line)]
                def resolve(v):
                    if v in locals_:
                        return parse_literal(locals_[v])
                    return parse_literal(v)
                level = items = amounts = desc = None
                if len(ret_vars) >= 1: level, ok = resolve(ret_vars[0]); parse_ok &= ok
                if len(ret_vars) >= 2: items, ok = resolve(ret_vars[1]); parse_ok &= ok
                if len(ret_vars) >= 3: amounts, ok = resolve(ret_vars[2]); parse_ok &= ok
                if len(ret_vars) >= 4: desc, _ = resolve(ret_vars[3])

                combined = []
                if isinstance(items, list):
                    for i, it in enumerate(items):
                        amt = amounts[i] if isinstance(amounts, list) and i < len(amounts) else None
                        combined.append([it, amt])
                entry.update(level=level, items=combined, description=desc, parse_ok=parse_ok)
            if 'table.insert' in req_body or 'baseClass' in req_body:
                entry['parse_ok'] = False

        results.append(entry)
    return results

def main():
    all_results = []
    for cdir in CLASS_DIRS:
        d = ROOT / cdir
        if not d.is_dir():
            continue
        for f in sorted(d.glob('*.lua')):
            all_results.extend(extract_from_file(f))

    by_key = {r['key']: r for r in all_results}
    for r in all_results:
        seen = set()
        cur = r
        while cur.get('alias_of') and cur['alias_of'] in by_key and cur['alias_of'] not in seen:
            seen.add(cur['alias_of'])
            target = by_key[cur['alias_of']]
            r['level'] = target['level']; r['items'] = target['items']
            r['description'] = target['description']; r['parse_ok'] = target['parse_ok']
            r['resolved_via_alias'] = cur['alias_of']
            cur = target

    out_path = Path(r"C:\Users\brian\Desktop\Project1998\re\spell_requirements_lua.json")
    out_path.write_text(json.dumps(all_results, indent=1), encoding='utf-8')
    ok = sum(1 for r in all_results if r['parse_ok'])
    print(f"{len(all_results)} spell tables found, {ok} cleanly parsed, {len(all_results)-ok} need manual review")
    print(f"-> {out_path}")

if __name__ == '__main__':
    main()
