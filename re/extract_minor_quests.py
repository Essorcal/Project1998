"""Extract the minor/major/epic quest tables from rtklua Accepted/Quests/MinorQuest.lua into a flat CSV
the server loads (Content.MinorQuestDefs). Keeps the Lua as the source of truth: re-run after editing it.

Each Lua entry looks like:
    ["squirrel"] = {
        displayName = "Squirrel",
        mobs = {"squirrel", "white_rabbit"},
        minLevel = 0, maxLevel = 10, minStat = 0, maxStat = _maxStat, minMark = 0, maxMark = _maxMark
    },
grouped under local tables _minorQuests / _majorQuests / _epicQuests. Constants: _maxLevel=100,
_maxStat=1000000000, _maxMark=5.

Output columns: Tier,Key,DisplayName,Mobs(|-separated),MinLevel,MaxLevel,MinStat,MaxStat,MinMark,MaxMark
"""
import re
from pathlib import Path

LUA = Path(__file__).parent / "RTK-Server" / "rtklua" / "Accepted" / "Quests" / "MinorQuest.lua"
if not LUA.exists():
    LUA = Path(__file__).parents[1] / "RTK-Server" / "rtklua" / "Accepted" / "Quests" / "MinorQuest.lua"
OUT = Path(__file__).parent.parent / "data" / "game-data" / "MinorQuests.csv"

CONSTS = {"_maxLevel": 100, "_maxStat": 1000000000, "_maxMark": 5}

def val(tok: str) -> int:
    tok = tok.strip()
    return CONSTS[tok] if tok in CONSTS else int(tok)

def extract_table(text: str, name: str):
    """Return list of entry dicts for the `local <name> = { ... }` table."""
    # find the table's body by brace matching from `local <name> = {`
    m = re.search(r"local\s+" + re.escape(name) + r"\s*=\s*\{", text)
    if not m:
        return []
    i = m.end() - 1  # at the opening brace
    depth, start = 0, i
    while i < len(text):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                break
        i += 1
    body = text[start + 1 : i]

    entries = []
    # each entry: ["key"] = { ... },  — brace-match the value block (it contains a nested mobs = {...}).
    for em in re.finditer(r'\["([^"]+)"\]\s*=\s*\{', body):
        key = em.group(1)
        j, depth = em.end() - 1, 0
        while j < len(body):
            if body[j] == "{":
                depth += 1
            elif body[j] == "}":
                depth -= 1
                if depth == 0:
                    break
            j += 1
        inner = body[em.end() : j]
        dn = re.search(r'displayName\s*=\s*"([^"]*)"', inner)
        mobs = re.findall(r'"([^"]+)"', re.search(r"mobs\s*=\s*\{(.*?)\}", inner, re.DOTALL).group(1))
        def field(f):
            fm = re.search(f + r"\s*=\s*([%\w]+)", inner)
            return val(fm.group(1))
        entries.append({
            "key": key,
            "name": dn.group(1) if dn else key,
            "mobs": mobs,
            "minLevel": field("minLevel"), "maxLevel": field("maxLevel"),
            "minStat": field("minStat"),   "maxStat": field("maxStat"),
            "minMark": field("minMark"),   "maxMark": field("maxMark"),
        })
    return entries

def main():
    text = LUA.read_text(encoding="utf-8")
    # strip block comments so commented-out @TODO entries don't leak in
    text = re.sub(r"--\[\[.*?\]\]", "", text, flags=re.DOTALL)
    # strip line comments (keep it simple: drop from -- to EOL; safe here, no -- inside strings in tables)
    text = "\n".join(re.sub(r"--.*$", "", ln) for ln in text.splitlines())

    tiers = [("Minor", "_minorQuests"), ("Major", "_majorQuests"), ("Epic", "_epicQuests")]
    rows = []
    for tier, tname in tiers:
        for e in extract_table(text, tname):
            rows.append([tier, e["key"], e["name"], "|".join(e["mobs"]),
                         e["minLevel"], e["maxLevel"], e["minStat"], e["maxStat"],
                         e["minMark"], e["maxMark"]])

    OUT.parent.mkdir(parents=True, exist_ok=True)
    with OUT.open("w", newline="", encoding="utf-8") as f:
        f.write("Tier,Key,DisplayName,Mobs,MinLevel,MaxLevel,MinStat,MaxStat,MinMark,MaxMark\n")
        for r in rows:
            f.write(",".join(str(c) for c in r) + "\n")
    print(f"wrote {len(rows)} quest defs -> {OUT}")
    for tier, _ in tiers:
        print(f"  {tier}: {sum(1 for r in rows if r[0] == tier)}")

if __name__ == "__main__":
    main()
