#!/usr/bin/env python3
"""Merge the archive-extracted spell level/cost data (re/archive_{warrior,mage,rogue,poet}_spells.md,
NUMERIC-LEVEL base-spell tables only -- the Il/Ee/Sam/Sa-san alignment-tier and Dog-trainer spells are a
separate, not-yet-implemented rank-progression system and are deliberately excluded here, not silently
guessed) against Spells.csv (for key/pathId) and the Lua-extracted baseline (re/spell_requirements_lua.json,
used as a fallback for any spell with no archive coverage). The 9 within-archive conflicts already
identified were resolved directly with the user (2026-07-27) and are hardcoded as overrides below.

Output: re/spell_costs_final.csv -- one row per Spells.csv key that got a level+cost assignment, columns:
key,pathId,level,gold,item1,amt1,item2,amt2,item3,amt3,item4,amt4,source
"""
import csv, json, re
from pathlib import Path

ROOT = Path(r"C:\Users\brian\Desktop\NexusServer")

# ---- Spells.csv: key -> (name, pathId) -----------------------------------------------------------------
spells_by_name = {}   # normalized name -> [(key, pathId), ...] (a name can appear for multiple pathIds -- reskins)
spells_by_key = {}
with open(ROOT / "game-data/Spells.csv", encoding="utf-8") as f:
    r = csv.DictReader(f)
    for row in r:
        key = row["SplIdentifier"].strip()
        name = row["SplDescription"].strip().replace("\\'", "'")   # CSV stores escaped apostrophes literally
        if not key or key.startswith("==") or not name:
            continue
        try:
            pth = int(row["SplPthId"])
        except ValueError:
            continue
        spells_by_key[key] = (name, pth)
        spells_by_name.setdefault(name.lower(), []).append((key, pth))

# ---- Items.csv: valid item keys ------------------------------------------------------------------------
item_keys = set()
with open(ROOT / "game-data/Items.csv", encoding="utf-8") as f:
    r = csv.DictReader(f)
    for row in r:
        item_keys.add(row["ItmIdentifier"].strip())

def slugify(name):
    s = name.lower().strip()
    s = s.replace("'s ", "s_").replace("'s", "s").replace("'", "")
    s = re.sub(r"[^a-z0-9]+", "_", s).strip("_")
    return s

def item_key_for(name):
    s = slugify(name)
    if s in item_keys:
        return s
    if s.endswith("s") and s[:-1] in item_keys:   # crude de-pluralize
        return s[:-1]
    if (s + "s") in item_keys:
        return s + "s"
    return None   # unresolvable -- caller should flag, not guess

def parse_cost(cost_text):
    """'10 Acorns, 10 Rabbit meat, 50 Coins' -> (gold:int, items:[(key,amt),...], unresolved:[str,...])"""
    gold = 0
    items = []
    unresolved = []
    if not cost_text or cost_text.strip().lower() in ("none", "free", "-", ""):
        return gold, items, unresolved
    for part in re.split(r",\s*(?:\+\s*)?|\s+\+\s+", cost_text):
        part = part.strip().rstrip(".")
        if not part:
            continue
        m = re.match(r"^(\d[\d,]*)\s+(.+)$", part)
        if not m:
            unresolved.append(part)
            continue
        amt = int(m.group(1).replace(",", ""))
        item_name = m.group(2).strip()
        if item_name.lower() in ("coins", "coin"):
            gold += amt
            continue
        key = item_key_for(item_name)
        if key is None:
            unresolved.append(part)
        else:
            items.append((key, amt))
    return gold, items, unresolved

# Archive display names that don't match Spells.csv's real name -- confirmed manually, not guessed. Empty
# now: blockade_human_poet's CSV SplDescription was changed to "Human Barrier" (2026-07-27, user's call) to
# match tswolf.com's contemporaneous-2001 name over RTK-Server's own (a ~20-years-later fan-run private
# server, "RetroTK" per its own dialogue text -- plausible the Lua's "Blockade Human" is a later rename, not
# the original). Kept as an extension point for any future name mismatches like this one.
NAME_ALIASES = {}

def find_spell(name, class_hint_pathid):
    """Best-effort: exact name match, preferring the row whose PathId matches class_hint_pathid (or its
    subpath range) when a name is ambiguous across classes."""
    name = NAME_ALIASES.get(name.lower().strip(), name)
    cands = spells_by_name.get(name.lower().strip())
    if not cands:
        return None
    if len(cands) == 1:
        return cands[0]
    for key, pth in cands:
        if pth == class_hint_pathid:
            return (key, pth)
    return cands[0]

CLASS_PATHID = {"warrior": 1, "rogue": 2, "mage": 3, "poet": 4}

# ---- Conflict resolutions (user, 2026-07-27) ------------------------------------------------------------
# Warrior: "trials are something else... go with tswolf.com or nexusatlas (if early 2000s)" -- neither
# covers these Warrior high-tier spells, so fall back to the tutor board's "main spell-list" post (NOT the
# "trial requirement" post) as the closest thing to a real per-spell trainer-cost sheet.
WARRIOR_OVERRIDES = {
    # name -> (level_or_None, cost_text)
    "ho-che": (None, "1 Spike, 1 Mountain ginseng, 25000 Coins"),          # Il san tier, no numeric level
    "hoche": (None, "1 Spike, 1 Mountain ginseng, 25000 Coins"),
    "dragon's flame": (None, "1 Ee san spike, 4 Electra, 4 Dragon's liver, 60000 Coins"),
    "spirit salvation": (None, "1 Mountain ginseng, 50000 Coins"),
    "health's return": (None, "1 Mountain ginseng, 50000 Coins"),
    "greater blessing": (60, "None"),
    "spirit fury": (99, "1 Ambrosia, 10000 Coins"),
}
# Mage: Soothe -> tutor (paid) value; Fleshspeak/Ignite/Electrocute -> tswolf value.
MAGE_OVERRIDES = {
    "soothe": (6, "10 Acorns, 10 Rabbit meat, 50 Coins"),
    "fleshspeak": (11, "10 Acorns, 1 Book, 10 Rabbit meat, 100 Coins"),
    "ignite": (19, "1 Book, 1 Ink, 200 Coins"),
    "electrocute": (64, "70 Acorns, 1 Amber, 500 Coins"),
}

# ---- Archive tables (numeric-level base spells only; Il/Ee/Sam/Sa-san + Dog-trainer spells excluded --
# see script docstring) -- re-encoded from re/archive_*_spells.md's "no conflict" rows + the overrides above.
ARCHIVE = {
    "warrior": [
        ("Gateway", 5, "10 Acorns, 10 Rabbit meat"),
        ("Taunt", 6, "20 Acorns, 5 Rabbit meat"),
        ("Wolf's Fury", 7, "25 Rabbit meat, 100 Coins"),
        ("Soothe", 8, "70 Acorns, 30 Coins"),
        ("Bless", 9, "3 Antlers, 80 Coins"),
        ("Fleshspeak", 13, "70 Acorns, 40 Coins"),
        ("Backstab", 15, "3 Antlers, 20 Snake meat, 100 Coins"),
        ("Flank", 20, "5 Antlers, 20 Snake meat, 150 Coins"),
        ("Tiger's Fury", 24, "5 Antlers, 15 Fox fur, 250 Coins"),
        ("Enchant", 28, "5 Antlers, 10 Fox fur, 1 Steel saber, 150 Coins"),
        ("Relief", 31, "80 Acorns, 10 Red fox fur, 10 Light fox fur, 200 Coins"),
        ("Potence", 35, "1 Battle helm, 10 Red fox fur, 10 Light fox fur, 500 Coins"),
        ("Watchful Eye", 38, "1 Amber, 10 Rat meat, 100 Coins"),
        ("Mentor", 40, "1 Maxcaliber, 1000 Coins"),
        ("Dragon's Fury", 45, "5 Fine snake meat, 1 Fox tail, 20 Light fox fur, 750 Coins"),
        ("Infuse", 55, "10 Antlers, 20 Red fox fur, 1 Steel saber, 1000 Coins"),
        ("Slash", 63, "5 Antlers, 5 Tiger's heart, 1 White amber, 1000 Coins"),
        ("Ingress", 70, "10 Antlers, 10 Amber, 1 Maxcaliber, 1500 Coins"),
        ("Berserk", 80, "1 Fox blade, 20 Amber, 1 Maxcaliber, 5000 Coins"),
        ("Share Wisdom", 90, "100000 Coins"),
        ("Vigor", 90, "1 Angel's tear, 40 Light fox fur, 1 Mountain ginseng, 10000 Coins"),
        ("Whirlwind", 99, "1 Angel's tear, 20 Dark amber, 1 Electra, 10000 Coins"),
    ],
    "mage": [
        ("Gateway", 5, "10 Acorns, 10 Rabbit meat"),
        ("Thunder Bolt", 5, "10 Acorns, 10 Rabbit meat, 50 Coins"),
        ("Might", 6, "5 Antlers, 1 Bear's liver, 50 Coins"),
        ("Soothe", *MAGE_OVERRIDES["soothe"]),
        ("Pestilence", 7, "15 Acorns, 1 Wooden sword, 50 Coins"),
        ("Static", 8, "20 Acorns, 1 Soup bowl, 50 Coins"),
        ("Spark", 9, "10 Acorns, 1 Book, 10 Coins"),
        ("Fleshspeak", *MAGE_OVERRIDES["fleshspeak"]),
        ("Propose", 11, "1000 Coins"),
        ("Singe", 13, "1 Book, 1 Ink, 100 Coins"),
        ("Return", 13, "30 Acorns, 50 Coins"),
        ("Harden armor", 14, "1 Ancient armor, 1 Gold acorn, 100 Coins"),
        ("Ignite", *MAGE_OVERRIDES["ignite"]),
        ("Approach", 20, "50 Acorns, 20 Snake meat"),
        ("Lay hands", 21, "1 Ginseng, 10 Light fox fur"),
        ("Erupt", 22, "50 Acorns, 2 Topaz"),
        ("Mend wounds", 23, "5 Bear's liver, 1 Ginseng"),
        ("Valor", 23, "10 Antlers, 20 Snake meat, 150 Coins"),
        ("Ion", 26, "1 Gold acorn, 1 Topaz"),
        ("Relief", 28, "1 Gold acorn, 1 Mountain ginseng, 10 Snake meat, 250 Coins"),
        ("Purge", 30, "10 Antlers, 1 Gold acorn, 1 Mountain ginseng, 150 Coins"),
        ("Summon", 30, "80 Acorns, 10 Snake meat, 50 Coins"),
        ("Ion charge", 32, "1 Gold acorn, 25 Light fox fur, 1 Moon wine, 150 Coins"),
        ("Recover", 32, "20 Antlers, 1 Ginseng, 1 Gold acorn, 200 Coins"),
        ("Cure paralysis", 36, "1 Gold acorn, 1 Mountain ginseng, 150 Coins"),
        ("Sanctuary", 38, "1 Ancient robes, 1 Battle helm, 1 Gold acorn, 250 Coins"),
        ("Invoke", 39, "1 Book, 5 Fine snake meat, 1 Ink, 250 Coins"),
        ("Gangrel", 40, "50 Acorns, 1 Fox fur, 200 Coins"),
        ("Heal", 40, "10 Bear's liver, 1 Gold acorn, 1 Mountain ginseng, 100 Coins"),
        ("Mentor", 40, "1 Death's head, 1000 Coins"),
        ("Impact", 41, "1 Amethyst, 1 Gold acorn, 250 Coins"),
        ("Explode", 42, "100 Acorns, 10 Fox fur, 100 Coins"),
        ("Remove curse", 44, "10 Fox fur, 1 Gold acorn, 200 Coins"),
        ("Beast", 50, "70 Acorns, 100 Coins"),
        ("Call Lightning", 54, "1 Gold acorn, 1 Tao stone, 500 Coins"),
        ("Vex", 55, "70 Acorns, 1 Lucky coin, 250 Coins"),
        ("Venom", 59, "3 Fine snake meat, 1 Mountain ginseng, 10 Snake meat, 500 Coins"),
        ("Blind", 61, "2 Amber, 1 Dark amber, 400 Coins"),
        ("Paralyze", 62, "1 Ambrosia, 20 Antlers, 500 Coins"),
        ("Electrocute", *MAGE_OVERRIDES["electrocute"]),
        ("Confuse", 65, "2 Amber, 1 Death's head, 600 Coins"),
        ("Sleep", 70, "10 Dark amber, 1 Fox blade"),
        ("Stormstrike", 77, "3 Dark amber, 1 Steel saber, 2500 Coins"),
        ("Doze", 82, "5 Dark amber, 1 Fox blade"),
        ("Tempest", 85, "2 Dark amber, 3 Gold acorn, 2000 Coins"),
        ("Rejuvenate", 90, "2 Mountain ginseng, 10000 Coins"),
        ("Share wisdom", 90, "100000 Coins"),
        ("Hellfire", 99, "10 Dark amber, 10 Stardrop, 1 Star-staff, 9000 Coins"),
    ],
    "rogue": [
        ("Gateway", 5, "10 Rabbit meat, 10 Acorns"),
        ("Feral", 10, "10 Rabbit meat, 50 Acorns, 100 Coins"),
        ("Fleshspeak", 11, "70 Acorns, 40 Coins"),
        ("Might", 15, "50 Acorns, 20 Snake meat, 100 Coins"),
        ("Judge", 17, "70 Acorns, 30 Snake meat, 150 Coins"),
        ("Singe", 18, "30 Acorns, 1 Topaz, 200 Coins"),
        ("Mend wounds", 20, "50 Acorns, 100 Coins"),
        ("Ignite", 24, "30 Acorns, 1 Light fox fur, 2 Topaz, 200 Coins"),
        ("Shadow figure", 24, "100 Acorns, 10 Red fox fur, 200 Coins"),
        ("Rodent", 25, "10 Rabbit meat, 80 Acorns, 100 Coins"),
        ("Set trap", 26, "2 Fine rabbit meat, 500 Coins"),
        ("Dart trap", 26, "None"),
        ("Spy", 28, "100 Acorns, 15 Red fox fur, 200 Coins"),
        ("Wolf's fury", 30, "150 Acorns, 20 Light fox fur, 300 Coins"),
        ("Flash trap", 33, "None"),
        ("Gangrel", 33, "80 Acorns, 10 Fox fur, 100 Coins"),
        ("Invisible", 34, "1 Fox tail, 1 Topaz"),
        ("Approach", 35, "100 Acorns, 10 Fox fur, 100 Coins"),
        ("Ambush", 38, "180 Acorns, 1 Fox blade, 400 Coins"),
        ("Mentor", 40, "1 Moonblade, 1000 Coins"),
        ("Beast", 40, "80 Acorns, 20 Bear's liver, 100 Coins"),
        ("Amnesia", 43, "50 Acorns, 20 Fox fur, 1 Lucky coin, 1200 Coins"),
        ("Repeating dart", 44, "None"),
        ("Return", 45, "100 Acorns, 100 Coins"),
        ("Recover", 48, "100 Acorns, 1 Mountain ginseng, 300 Coins"),
        ("Desperate attack", 50, "50 Acorns, 2 Amethyst, 1 Steel saber, 500 Coins"),
        ("Summon", 53, "100 Acorns, 500 Coins"),
        ("Snare", 55, "None"),
        ("Tiger's fury", 56, "180 Acorns, 1 Lucky coin, 1 Moonblade, 1000 Coins"),
        ("Filch", 65, "130 Acorns, 1 Steel sword, 2000 Coins"),
        ("Spear trap", 66, "None"),
        ("Poison Dart Trap", 77, "None"),
        ("Drain", 80, "190 Acorns, 10 Dark amber, 5000 Coins"),
        ("Death trap", 88, "None"),
        ("Seal wounds", 90, "25000 Coins"),
        ("Sleep trap", 99, "None"),
        ("Lethal strike", 99, "1 Death's head, 1 Whisper bracelet, 1 Titanium glove, 5000 Coins"),
    ],
    "poet": [
        ("Soothe", 6, "5 Acorns, 5 Rabbit meat"),   # no separate Poet tutor value exists -- reuse Mage's paid-trainer level per the same user override (Newbie Quest not modeled)
        ("Gateway", 5, "10 Acorns, 10 Rabbit meat"),
        ("Invoke", 7, "1 Ink, 1 Wine"),
        ("Lay hands", 7, "20 Acorns, 10 Rabbit meat"),
        ("Spark", 8, "20 Acorns, 10 Rabbit meat"),
        ("Recover", 9, "1 Aged wine, 100 Coins"),
        ("Propose", 11, "1000 Coins"),
        ("Purge", 12, "10 Snake meat, 1 Wine, 100 Coins"),
        ("Harden armor", 14, "1 Ancient armor, 20 Snake meat, 10 Coins"),
        ("Vital Spark", 15, "50 Acorns, 100 Coins"),
        ("Singe", 17, "20 Acorns, 10 Rabbit meat"),
        ("Remove curse", 18, "1 Blue ring, 10 Coins"),
        ("Valor", 21, "10 Antlers, 20 Snake meat, 100 Coins"),
        ("Heal", 22, "1 Ink, 10 Snake meat"),
        ("Ignite", 22, "20 Acorns, 10 Rabbit meat"),
        ("Anoint", 28, "1 Aged wine, 100 Coins"),
        ("Approach", 29, "1 Gold acorn, 100 Coins"),
        ("Sanctuary", 30, "1 Scroll, 1 Ink"),
        ("Remove Veil", 31, "1 Ancient robes"),
        ("Return", 32, "1 Yellow scroll"),
        ("Endear", 33, "10 Light fox fur, 10 Red fox fur, 1 Topaz"),
        ("Cure paralysis", 34, "1 Fine snake meat, 100 Coins"),
        ("Summon", 38, "70 Acorns, 100 Coins"),
        ("Mentor", 40, "1 Wicked staff, 1000 Coins"),
        ("Revitalize", 40, "1 Aged wine, 1 Amethyst"),
        ("Inspiration", 45, "1 Fragile rose, 1000 Coins"),
        ("Remedy", 45, "20 Fox fur, 1 Mountain ginseng, 100 Coins"),
        ("Atone", 48, "1 Gold acorn, 1 Scroll, 100 Coins"),
        ("Second Sight", 53, "100 Acorns, 1 Steel saber, 500 Coins"),
        ("Retribution", 56, "1 Purple ring, 1 Wicked staff, 2000 Coins"),
        ("Barrier", 60, "70 Acorns, 20 Light fox fur, 500 Coins"),
        ("Fortify", 60, "20 Light fox fur, 1 Mountain ginseng, 100 Coins"),
        ("Inspire", 60, "1 Amethyst, 1 Gold acorn, 100 Coins"),
        ("Call of the Wild", 63, "100 Acorns, 1 Pearl charm, 1000 Coins"),
        ("Scourge", 70, "80 Acorns, 2 Amber, 1000 Coins"),
        ("Human Barrier", 75, "50 Acorns, 1 Dark amber, 500 Coins"),
        ("Heaven's Kiss", 80, "80 Acorns, 1 Dark amber, 800 Coins"),
        ("Harden Body", 85, "100 Acorns, 20 Amber, 5000 Coins"),
        ("Dispell", 88, "80 Acorns, 1 Death's head, 100 Coins"),
        ("Flare", 90, "5 Gold acorn, 10000 Coins"),
        ("Share Wisdom", 90, "100000 Coins"),
        ("Water of Life", 95, "100 Acorns, 1 Dark amber, 1 Red potion, 5000 Coins"),
        ("Resurrect", 99, "199 Acorns, 5 Dark amber, 2 Red potion, 10000 Coins"),
    ],
}

# Apply the Warrior Il/Ee/Sam-san overrides (tier spells, no numeric level -- excluded from ARCHIVE above
# since they have no character level; recorded here only for the report, not written to the final CSV).
TIER_SPELLS_NOT_IMPLEMENTED = list(WARRIOR_OVERRIDES.keys())

def main():
    rows = []
    unresolved_report = []
    unmatched_report = []

    for cls, entries in ARCHIVE.items():
        pth = CLASS_PATHID[cls]
        for name, level, cost_text in entries:
            sp = find_spell(name, pth)
            if sp is None:
                unmatched_report.append((cls, name, level, cost_text))
                continue
            key, real_pth = sp
            gold, items, unresolved = parse_cost(cost_text)
            if unresolved:
                unresolved_report.append((cls, name, cost_text, unresolved))
            # Use the CLASS loop's own pathId (pth), NOT the CSV row's real_pth: for the 6 shared "peasant
            # commons"-looking keys (gateway/soothe/mentor/return_spell/approach_spell/summon_spell),
            # Spells.csv tags them all PathId=0, but each archive entry here is a specific class's own
            # level/cost and must be keyed by THAT class (1-4) in the output, not 0, or every class's row
            # collapses onto the same dict slot. For every other (single-class) spell, pth == real_pth anyway.
            row = {"key": key, "pathId": pth, "level": level, "gold": gold, "source": f"archive:{cls}"}
            for i in range(4):
                row[f"item{i+1}"] = items[i][0] if i < len(items) else ""
                row[f"amt{i+1}"] = items[i][1] if i < len(items) else ""
            rows.append(row)

    # Lua fallback: any spell NOT already covered by archive, with a cleanly-parsed Lua entry, for the
    # 4 real classes (1-4) -- gives every remaining spell a real level+cost instead of staying free/CSV-level.
    covered_keys = {r["key"] for r in rows}
    lua_data = json.loads((ROOT / "re/spell_requirements_lua.json").read_text(encoding="utf-8"))
    lua_by_key = {d["key"]: d for d in lua_data if d.get("parse_ok")}
    lua_fallback_count = 0
    for key, (name, pth) in spells_by_key.items():
        if key in covered_keys or pth not in (1, 2, 3, 4):
            continue
        d = lua_by_key.get(key)
        if not d or d.get("level") is None:
            continue
        gold = 0
        items = []
        unresolved = []
        for it, amt in d.get("items", []):
            if isinstance(it, (int, float)) and it == 0:   # Lua convention: itemId 0 = gold slot
                gold += int(amt) if amt else 0
                continue
            if isinstance(it, str):
                ik = it if it in item_keys else item_key_for(it)
                if ik:
                    items.append((ik, int(amt) if amt else 0))
                else:
                    unresolved.append(f"{it}={amt}")
        row = {"key": key, "pathId": pth, "level": d["level"], "gold": gold, "source": "lua"}
        for i in range(4):
            row[f"item{i+1}"] = items[i][0] if i < len(items) else ""
            row[f"amt{i+1}"] = items[i][1] if i < len(items) else ""
        rows.append(row)
        lua_fallback_count += 1

    out_path = ROOT / "re/spell_costs_final.csv"
    with open(out_path, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=["key", "pathId", "level", "gold",
                                          "item1", "amt1", "item2", "amt2", "item3", "amt3", "item4", "amt4",
                                          "source"])
        w.writeheader()
        for r in rows:
            w.writerow(r)

    print(f"{len(rows)} total rows written -> {out_path}")
    print(f"  archive-sourced: {len(rows) - lua_fallback_count}")
    print(f"  lua-fallback:    {lua_fallback_count}")
    print(f"  tier spells (Il/Ee/Sam-san) excluded, not implemented: {len(TIER_SPELLS_NOT_IMPLEMENTED)} names")
    if unmatched_report:
        print(f"\n{len(unmatched_report)} archive entries could NOT be matched to a Spells.csv row:")
        for cls, name, level, cost in unmatched_report:
            print(f"  [{cls}] '{name}' (level {level})")
    if unresolved_report:
        print(f"\n{len(unresolved_report)} entries had unresolved cost parts (kept gold/other items, dropped these):")
        for cls, name, cost_text, unresolved in unresolved_report:
            print(f"  [{cls}] '{name}': {unresolved}  (full text: \"{cost_text}\")")

if __name__ == "__main__":
    main()
