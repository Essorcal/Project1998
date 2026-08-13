"""Extract every shop NPC's SELL list — the items it will BUY FROM the player — from the rtklua NPC scripts
into game-data/ShopBuysFrom.csv. The mirror image of re/extract_shops.py (which does the buy side, i.e. what
the NPC sells TO you); the two contexts are deliberately kept apart because they are different lists in RTK:
the butcher SELLS six things but BUYS twenty-two, and before this the server let every shop buy anything at
all with a sell price.

Approach mirrors extract_shops.py: for each NPCs/*.lua, find the NpcXxx table name(s) and collect every quoted
string in a SELL context — `sell… = {…}` tables, inline `sellExtend(…, {…})` tables, `sellExtend(prompt, var)`
resolved back to `local var = {…}`, `table.insert(sell…, "x")`, and `sellItems = function … end` bodies. Then
keep only real item identifiers (Items.csv), which drops prompts and labels. An NPC whose sell context can't
be resolved statically simply gets no row, and the server falls back to accepting anything sellable.

Two things extract_shops.py doesn't have to deal with, both discovered by eyeballing the first run's output:

* Most shops write `sellItems = function() local sellItems = XxxNpc.buyItems() … end` — the sell list is the
  BUY list plus extras. Scanning the sell context alone lost the whole base list (the inn stopped buying back
  its own wine), so a first pass indexes every file's `buyItems` body by table name and the sell pass unions
  it in when the body calls it. It is cross-file: clan_npc.lua calls InnNpc.buyItems().
* Extras are often wrapped in `if (Config.someFlag) then … end`, and RTK ships several of those flags OFF
  (config.lua) — e.g. `bossDropSalesEnabled = false`, which is what made the librarian buy dungeon keys. Those
  blocks are stripped before scanning, so a disabled feature's items don't leak into the list.

Output columns: NpcIdentifier,ItemKeys(|-separated)
"""
import re
from pathlib import Path

# Shop NPCs of OURS that RTK has no script for, mapped to the RTK shop they are a copy of. Without a row an
# NPC buys anything sellable (see Content.ShopBuysFrom), so a shop RTK never wrote is the one case the
# extraction can't cover on its own. Kept here rather than hand-edited into the CSV, which is regenerated.
#   TaimyrNpc — the Arctic Village butcher (game-data/NPCs.csv #396, map 3814 "Taimyr Butcher"), same look
#   and trade as the four RTK butchers, so she takes the same meats and pelts (user, 2026-08-12).
ALIASES = {"TaimyrNpc": "ButcherNpc"}

ROOT = Path(__file__).parent
NPC_DIR = ROOT.parent / "RTK-Server" / "rtklua" / "Accepted" / "NPCs"
if not NPC_DIR.exists():
    NPC_DIR = ROOT / "RTK-Server" / "rtklua" / "Accepted" / "NPCs"
CONFIG = NPC_DIR.parent / "config.lua"
DATA = ROOT.parent / "game-data"
ITEMS = DATA / "Items.csv"
OUT = DATA / "ShopBuysFrom.csv"

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

def disabled_config_flags() -> set[str]:
    """Config.lua booleans that are OFF — the features whose `if (Config.x) then … end` blocks never run."""
    if not CONFIG.exists():
        return set()
    return set(re.findall(r'(\w+)\s*=\s*false', CONFIG.read_text(encoding="utf-8", errors="replace")))

def strip_disabled(text: str, flags: set[str]) -> str:
    """Cut every `if (Config.<off-flag>) then … end` block out of the source, nesting-aware."""
    for flag in flags:
        while True:
            m = re.search(r'if\s*\(?\s*Config\.' + re.escape(flag) + r'\s*\)?\s*then\b', text)
            if not m:
                break
            # walk forward counting block openers until this if's own `end`
            depth, i = 1, m.end()
            for tok in re.finditer(r'\b(if|for|while|function|do|end)\b', text[m.end():]):
                if tok.group(1) == "do":            # `for … do` / `while … do` already counted by their keyword
                    continue
                depth += -1 if tok.group(1) == "end" else 1
                if depth == 0:
                    i = m.end() + tok.end()
                    break
            text = text[: m.start()] + text[i:]
    return text

def buy_item_bodies(text: str) -> list[str]:
    """Strings inside a `buyItems = function … end` body — what `local sellItems = XxxNpc.buyItems()` pulls in."""
    out: list[str] = []
    for m in re.finditer(r'buyItems\s*=\s*function', text):
        body = text[m.end():]
        endm = re.search(r'\n\tend[,]?\s*(\n|$)', body)
        body = body[: endm.start()] if endm else body
        for k in re.findall(r'Item\(\s*"([a-z0-9_]+)"', body) + STR.findall(body):
            if k not in out:
                out.append(k)
    return out

def has_sell_handler(text: str) -> bool:
    """Does this NPC offer a Sell option at all? Separates 'buys nothing' from 'we couldn't read the list'."""
    return "sellExtend" in text or re.search(r'sellItems\s*=\s*function', text) is not None

def sell_strings(text: str, buy_bodies: dict[str, list[str]], sell_bodies: dict[str, list[str]] | None = None) -> list[str]:
    sell_bodies = sell_bodies or {}
    """Ordered (first-seen wins) so the CSV keeps the Lua's own listing order."""
    found: list[str] = []
    def add(keys):
        for k in keys:
            if k not in found:
                found.append(k)

    # `sell… = { … }` (locals, reassignments) — but NOT `sellPrice = {}` style numeric tables; the item-key
    # filter downstream drops those anyway.
    for m in re.finditer(r'\bsell\w*\s*=\s*\{', text):
        add(STR.findall(balanced(text, m.end() - 1, "{", "}")))
    # inline tables passed to sellExtend( prompt, { … } )
    for m in re.finditer(r'sellExtend\s*\(', text):
        call = balanced(text, m.end() - 1, "(", ")")
        for tm in re.finditer(r'\{', call):
            add(STR.findall(balanced(call, tm.start(), "{", "}")))
    # bare variables passed to sellExtend(prompt, someVar) -> resolve `local someVar = { … }`
    for m in re.finditer(r'sellExtend\s*\((?:[^()]*?),\s*(\w+)\s*\)', text, re.DOTALL):
        var = m.group(1)
        for vm in re.finditer(r'\blocal\s+' + re.escape(var) + r'\s*=\s*\{', text):
            add(STR.findall(balanced(text, vm.end() - 1, "{", "}")))
    # table.insert(sell…, "item") — the per-map extras (Lien's tiger cuts, etc.)
    for m in re.finditer(r'table\.insert\(\s*sell\w*\s*,\s*"([a-z0-9_]+)"', text):
        add([m.group(1)])
    # `sellItems = function … end` bodies that build the list dynamically. `local sellItems =
    # XxxNpc.buyItems()` seeds the list from that NPC's buy catalogue FIRST, so its keys go in ahead of the
    # body's own extras — which keeps the CSV in the order the shop actually lists them.
    for m in re.finditer(r'sell\w*\s*=\s*function', text):
        body = text[m.end():]
        endm = re.search(r'\n\tend[,]?\s*(\n|$)', body)   # function closes at one-tab indent (table field)
        body = body[: endm.start()] if endm else body
        for ref in re.findall(r'(\w+Npc)\.buyItems\s*\(', body):
            add(buy_bodies.get(ref, []))
        for ref in re.findall(r'(\w+Npc)\.sellItems\s*\(', body):   # blood.lua delegates wholesale to the chapel
            add(sell_bodies.get(ref, []))
        add(re.findall(r'Item\(\s*"([a-z0-9_]+)"', body))
        add(STR.findall(body))
    return found

def main():
    items = valid_item_keys()
    flags = disabled_config_flags()
    sources = {lua: strip_disabled(lua.read_text(encoding="utf-8", errors="replace"), flags)
               for lua in sorted(NPC_DIR.rglob("*.lua"))}

    # pass 1: every table's buyItems body, so a cross-file `InnNpc.buyItems()` resolves
    buy_bodies: dict[str, list[str]] = {}
    for text in sources.values():
        body = buy_item_bodies(text)
        if body:
            for name in re.findall(r'^(\w+Npc)\s*=\s*\{', text, re.M):
                buy_bodies[name] = body

    # pass 1b: each table's own resolved sell list, so a cross-file `ChapelNpc.sellItems()` resolves too
    sell_bodies: dict[str, list[str]] = {}
    for text in sources.values():
        body = [k for k in sell_strings(text, buy_bodies) if k in items]
        if body:
            for name in re.findall(r'^(\w+Npc)\s*=\s*\{', text, re.M):
                sell_bodies[name] = body

    rows: dict[str, list[str]] = {}
    for lua, text in sources.items():
        names = re.findall(r'^(\w+Npc)\s*=\s*\{', text, re.M)
        if not names:
            continue
        keys = [k for k in sell_strings(text, buy_bodies, sell_bodies) if k in items]
        # A shop with a Sell option but an empty list buys NOTHING — chapel_npc.lua returns {} outright with
        # boss-drop sales off, and blood.lua delegates to it. That has to reach the server as an explicit
        # empty row ("-"), because a MISSING row means "buys anything" and would leave those two wide open.
        if not keys:
            if not has_sell_handler(text):
                continue
            keys = ["-"]
        for name in names:
            rows.setdefault(name, [])
            for k in keys:
                if k not in rows[name]:
                    rows[name].append(k)

    for ours, rtk in ALIASES.items():
        if rtk not in rows:
            raise SystemExit(f"alias {ours} -> {rtk}: no such RTK shop (renamed table?)")
        rows[ours] = list(rows[rtk])

    OUT.parent.mkdir(parents=True, exist_ok=True)
    with OUT.open("w", newline="", encoding="utf-8") as f:
        f.write("NpcIdentifier,ItemKeys\n")
        for name in sorted(rows):
            f.write(f"{name},{'|'.join(rows[name])}\n")
    empty = [n for n in sorted(rows) if rows[n] == ["-"]]
    print(f"wrote {len(rows)} shop buys-from lists -> {OUT}")
    for name in sorted(rows):
        if rows[name] != ["-"]:
            print(f"  {name}: {len(rows[name])} items")
    if empty:
        # Worth eyeballing after every run: each of these will refuse EVERY sale. Verified 2026-08-12 —
        # chapel/blood (boss-drop sales off), hariette ("druids probably shouldn't sell meat"), cartographer
        # (empty list), and the four totem NPCs, whose one sell item is dynamic and isn't in Items.csv anyway.
        print(f"  buys NOTHING ({len(empty)}): {', '.join(empty)}")

if __name__ == "__main__":
    main()
