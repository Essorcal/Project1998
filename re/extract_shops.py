"""Extract every shop NPC's BUY list from the rtklua NPC scripts into data/game-data/ShopStock.csv, so any
shop-flagged NPC has stock without hand-authoring each catalogue. The server loads this as a fallback behind
the curated Shops.cs catalogues (which stay authoritative where present).

Approach: for each NPCs/*.lua, find the NpcXxx table name(s) and collect every quoted string that appears in a
BUY context — `buy… = {…}` tables (incl. nested category tables), inline `buyExtend(…, {…})` tables, and
`table.insert(buy…, "x")`. Then keep only strings that are real item identifiers (Items.csv), which drops
prompts, region names, registry keys, and category labels. Sell-only contexts are deliberately not scanned, so
items an NPC only buys from the player don't become purchasable. Dynamic lists built via an intermediate
non-"buy" variable (a few shops) may be missed — those simply stay empty, as before.

Output columns: NpcIdentifier,ItemKeys(|-separated)
"""
import re
from pathlib import Path

ROOT = Path(__file__).parent
NPC_DIR = ROOT / "RTK-Server" / "rtklua" / "Accepted" / "NPCs"
if not NPC_DIR.exists():
    NPC_DIR = ROOT.parent / "RTK-Server" / "rtklua" / "Accepted" / "NPCs"
DATA = ROOT.parent / "data" / "game-data"
ITEMS = DATA / "Items.csv"
OUT = DATA / "ShopStock.csv"

def valid_item_keys() -> set[str]:
    keys = set()
    with ITEMS.open(encoding="utf-8", errors="replace") as f:
        header = f.readline().rstrip("\n").split(",")
        idx = header.index("ItmIdentifier")
        for line in f:
            cols = line.rstrip("\n").split(",")
            if len(cols) > idx and cols[idx]:
                keys.add(cols[idx])
    return keys

def balanced(text: str, open_idx: int, opener: str, closer: str) -> str:
    """Return text[open_idx : matching-close], inclusive, brace/paren balanced."""
    depth, i = 0, open_idx
    while i < len(text):
        if text[i] == opener:
            depth += 1
        elif text[i] == closer:
            depth -= 1
            if depth == 0:
                return text[open_idx : i + 1]
        i += 1
    return text[open_idx:]

STR = re.compile(r'"([a-z0-9_]+)"')

def buy_strings(text: str) -> set[str]:
    found = set()
    # `buy… = { … }`  (locals, reassignments, nested category tables)
    for m in re.finditer(r'\bbuy\w*\s*=\s*\{', text):
        block = balanced(text, m.end() - 1, "{", "}")
        found |= set(STR.findall(block))
    # inline tables passed to buyExtend( prompt, { … } )
    for m in re.finditer(r'buyExtend\s*\(', text):
        call = balanced(text, m.end() - 1, "(", ")")
        for tm in re.finditer(r'\{', call):
            found |= set(STR.findall(balanced(call, tm.start(), "{", "}")))
    # bare category variables passed to buyExtend(prompt, someVar) -> resolve `local someVar = { … }`
    # (covers categorized shops like the smith: buyExtend(str, pclothes), buyExtend(str, mhelms), …)
    for m in re.finditer(r'buyExtend\s*\((?:[^()]*?),\s*(\w+)\s*\)', text, re.DOTALL):
        var = m.group(1)
        for vm in re.finditer(r'\blocal\s+' + re.escape(var) + r'\s*=\s*\{', text):
            found |= set(STR.findall(balanced(text, vm.end() - 1, "{", "}")))
    # table.insert(buy…, "item")
    for m in re.finditer(r'table\.insert\(\s*buy\w*\s*,\s*"([a-z0-9_]+)"', text):
        found.add(m.group(1))
    # `buyItems = function … end` bodies that build the list dynamically (Item("k").id, local strings={…}):
    # grab every Item("k") ref and bare "k" literal in the body (the item-key filter drops non-items).
    for m in re.finditer(r'buyItems\s*=\s*function', text):
        body = text[m.end():]
        endm = re.search(r'\n\tend[,]?\s*(\n|$)', body)   # function closes at one-tab indent (table field)
        body = body[: endm.start()] if endm else body
        found |= set(re.findall(r'Item\(\s*"([a-z0-9_]+)"', body))
        found |= set(STR.findall(body))
    return found

def main():
    items = valid_item_keys()
    rows = {}   # identifier -> ordered list of keys
    for lua in sorted(NPC_DIR.rglob("*.lua")):
        text = lua.read_text(encoding="utf-8", errors="replace")
        names = re.findall(r'^(\w+Npc)\s*=\s*\{', text, re.M)
        if not names:
            continue
        keys = [k for k in sorted(buy_strings(text)) if k in items]
        if not keys:
            continue
        for name in names:
            rows.setdefault(name, [])
            for k in keys:
                if k not in rows[name]:
                    rows[name].append(k)

    OUT.parent.mkdir(parents=True, exist_ok=True)
    with OUT.open("w", newline="", encoding="utf-8") as f:
        f.write("NpcIdentifier,ItemKeys\n")
        for name in sorted(rows):
            f.write(f"{name},{'|'.join(rows[name])}\n")
    print(f"wrote {len(rows)} shop stock lists -> {OUT}")
    for name in sorted(rows):
        print(f"  {name}: {len(rows[name])} items")

if __name__ == "__main__":
    main()
