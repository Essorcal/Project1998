"""Extract mob floor-loot drop tables from rtklua Accepted/Mobs/MobDrops.lua into game-data/MobDrops.csv,
so the server's drop rolls match RTK's real per-mob tables instead of a hand-guessed placeholder.

The Lua's `_mobDropsTable` has one entry per mob key, each with an optional `loot` block (independently-rolled
items, each with its own amount range and percent chance) and an optional `rareLoot` block (a few items rolled
in order, only the first hit drops — see `_handleLoot`/`_handleRareLoot` in the same file). A numeric literal
in `items` (instead of a quoted string) means a gold drop rather than an item; we emit that as the sentinel
key GOLD.

Known, deliberate gaps:
  - `_main`/`_wild`-suffixed Lua keys (e.g. "horse_defender_main", "golden_hare_wild") are aliased to the base
    mob identifier ("horse_defender", "golden_hare") since that's what's in mobs.csv.
  - The five "nagnag_armory_*" entries are a map-id-gated table (mob:m falls in one of 5 ranges) used by
    Nagnang Armory mercenary NPCs, none of which appear in Spawns0.csv/AreaSpawns.csv — i.e. that instance
    isn't populated anywhere in this server's world data. Skipped as dead data rather than wiring up an
    unused map-range special case.
  - A small number of item keys referenced by rareLoot (bright_staff, crystal_staff) don't exist in
    Items.csv at all (not in our export) — those individual rare-loot lines are dropped, the mob's other
    entries are kept. winter_sceptre is a spelling variant of our winter_scepter and is aliased, not skipped.

Output columns: MobKey,Loot,RareLoot
  Loot:     pipe-separated "item:maxAmount:ratePercent" (item is a key from Items.csv, or GOLD)
  RareLoot: pipe-separated "item:ratePercent", in Lua-source order (first hit wins at roll time)
"""
import csv
import re
from pathlib import Path

ROOT = Path(__file__).parent
LUA = ROOT / "RTK-Server" / "rtklua" / "Accepted" / "Mobs" / "MobDrops.lua"
if not LUA.exists():
    LUA = ROOT.parent / "RTK-Server" / "rtklua" / "Accepted" / "Mobs" / "MobDrops.lua"
DATA = ROOT.parent / "data" / "game-data"
MOBS_CSV = DATA / "mobs.csv"
ITEMS_CSV = DATA / "Items.csv"
OUT = DATA / "MobDrops.csv"

# Archive corrections to RTK's own drop tables, applied as a post-pass (see apply_corrections). RTK-Server
# ("RetroTK") is a ~20-year-old fan server whose drop data has drifted from the real 2001 game; the tswolf.com
# Wayback cache (archived Oct 2000, contemporaneous with 4.95) is the era-correct reference where it overlaps.
# Only two kinds of change are made here, both cleared with the user (see the tswolf-vs-nexusatlas comparison):
#   1. A systematic RTK BUG tswolf proves: the Mythic/Divine/Spirit Snake top-tiers are missing their entire
#      base Amber/Dark/White + gold loot line (every sibling family's top tier has it; tswolf's "Mythic Snake 1"
#      page lists Amber/Dark/White/Gold Bar/Hyun Moo Key). Gold amount mirrors the same room's caster sibling
#      (snake_shaman 1200 / snake_mage 3000 / snake_avenger 8000).
#   2. Specific accessory drops where tswolf AND nexusatlas AGREE (era-correct, not later-only): Holy Ring on
#      the snake sentry/guardian/defender ladder (tswolf shows it on the tier-1 Snake Sentry; nexusatlas across
#      all three), and Tao Stone on the rooster guardian/barbarian ladder (tswolf shows both; nexusatlas across
#      the ladder). NOT applied: nexusatlas-only additions tswolf contradicts (e.g. Ambrosia on Divine Rooster),
#      nor any drop for families tswolf doesn't cover (ox/horse/dragon/etc. elite tiers = later-era itemization).
# Rates are a calibration: the archives establish item PRESENCE, not drop rate, so added accessories take a
# modest rate in line with the row's existing accessory (typically ~1%; the caster/barbarian rows that already
# carry high-rate rings use that row's ring rate).
BASE_LOOT_LINE = [("amber", 5, 100), ("dark_amber", 3, 67), ("white_amber", 1, 34)]  # the shared top-tier line
ARCHIVE_CORRECTIONS = {
    # 1. Snake top-tiers missing base loot line (gold = same-room caster sibling's amount) — tswolf-confirmed.
    "mythic_snake":  {"loot_add": [("GOLD", 1200, 100)] + BASE_LOOT_LINE},
    "divine_snake":  {"loot_add": [("GOLD", 3000, 100)] + BASE_LOOT_LINE},
    "spirit_snake":  {"loot_add": [("GOLD", 8000, 100)] + BASE_LOOT_LINE},
    # 2a. Holy Ring on the snake elite ladder — tswolf (tier-1 sentry) + nexusatlas (all tiers) agree.
    "snake_sentry":   {"rare_add": [("holy_ring", 1)]},
    "snake_guardian": {"rare_add": [("holy_ring", 1)]},
    "snake_defender": {"rare_add": [("holy_ring", 1)]},
    # 2b. Tao Stone on the rooster elite ladder — tswolf (guardian + barbarian) + nexusatlas (ladder) agree.
    "rooster_sentry":    {"rare_add": [("tao_stone", 1)]},
    "rooster_guardian":  {"rare_add": [("tao_stone", 1)]},
    "rooster_defender":  {"rare_add": [("tao_stone", 1)]},
    "rooster_barbarian": {"rare_add": [("tao_stone", 5)]},   # barbarian's ring rate is 5, match it
    "rooster_avenger":   {"rare_add": [("tao_stone", 1.67)]},
    # 3. Red rabbit has no RTK drop table at all; fandom/nexuswiki give Rabbit meat (matches the base-rabbit line).
    "red_rabbit":    {"loot_add": [("rabbit_meat", 1, 67)]},
}


ALIASES = {
    "golden_hare_wild": "golden_hare",
    "hooves_main": "hooves",
    "horse_defender_main": "horse_defender",
    "horse_guardian_main": "horse_guardian",
    "horse_guardsman_main": "horse_guardsman",
    "horse_sentry_main": "horse_sentry",
    "horse_swordsman_main": "horse_swordsman",
}
# Lua item key -> our Items.csv key, for cases that are just a spelling variant (not a missing item).
ITEM_ALIASES = {"winter_sceptre": "winter_scepter"}
SKIP_KEYS = {"nagnag_armory_0", "nagnag_armory_1", "nagnag_armory_2", "nagnag_armory_3", "nagnag_armory_4"}


def balanced(text: str, open_idx: int) -> str:
    """text[open_idx:] must start at a '{'; return the matching-close slice, inclusive."""
    depth, i = 0, open_idx
    while i < len(text):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                return text[open_idx:i + 1]
        i += 1
    return text[open_idx:]


def read_keys(csv_path: Path, col: str) -> set[str]:
    with csv_path.open(encoding="utf-8") as f:
        return {row[col] for row in csv.DictReader(f) if row.get(col)}


def parse_sub_table(block: str, name: str):
    """Return (items, amounts, rates) token lists for `name = { items={...}, amounts={...}, rates={...} }`
    inside block, or None if `name` isn't present."""
    m = re.search(re.escape(name) + r"\s*=\s*\{", block)
    if not m:
        return None
    sub = balanced(block, m.end() - 1)[1:-1]

    def arr(field):
        am = re.search(re.escape(field) + r"\s*=\s*\{([^}]*)\}", sub)
        if not am:
            return None
        return [x.strip() for x in am.group(1).split(",") if x.strip()]

    return arr("items"), arr("amounts"), arr("rates")


def main():
    text = LUA.read_text(encoding="utf-8")
    start = text.index("local _mobDropsTable = {")
    brace = text.index("{", start)
    body = balanced(text, brace)[1:-1]

    item_keys = read_keys(ITEMS_CSV, "ItmIdentifier")
    mob_keys = read_keys(MOBS_CSV, "Identifier")

    rows = []          # (mobKey, loot_str, rareLoot_str)
    skipped_mobs = []
    skipped_items = set()

    for m in re.finditer(r'\["([a-zA-Z0-9_]+)"\]\s*=\s*\{', body):
        key = m.group(1)
        block = balanced(body, m.end() - 1)

        if key in SKIP_KEYS:
            continue
        mob_key = ALIASES.get(key, key)
        if mob_key not in mob_keys:
            skipped_mobs.append(key)
            continue

        loot_parts = []
        loot = parse_sub_table(block, "loot")
        if loot:
            items, amounts, rates = loot
            for i, raw in enumerate(items):
                is_gold = not raw.startswith('"')
                ik = "GOLD" if is_gold else ITEM_ALIASES.get(raw.strip('"'), raw.strip('"'))
                if not is_gold and ik not in item_keys:
                    skipped_items.add(ik)
                    continue
                amt = amounts[i] if amounts and i < len(amounts) else "1"
                rate = rates[i] if rates and i < len(rates) else "0"
                loot_parts.append(f"{ik}:{amt}:{rate}")

        rare_parts = []
        rare = parse_sub_table(block, "rareLoot")
        if rare:
            items, _amounts, rates = rare
            for i, raw in enumerate(items):
                is_gold = not raw.startswith('"')
                ik = "GOLD" if is_gold else ITEM_ALIASES.get(raw.strip('"'), raw.strip('"'))
                if not is_gold and ik not in item_keys:
                    skipped_items.add(ik)
                    continue
                rate = rates[i] if rates and i < len(rates) else "0"
                rare_parts.append(f"{ik}:{rate}")

        if loot_parts or rare_parts:
            rows.append((mob_key, "|".join(loot_parts), "|".join(rare_parts)))

    corrected = apply_corrections(rows, mob_keys, item_keys)

    OUT.parent.mkdir(parents=True, exist_ok=True)
    with OUT.open("w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["MobKey", "Loot", "RareLoot"])
        for r in corrected:
            w.writerow(r)

    print(f"wrote {len(corrected)} mob drop tables -> {OUT}")
    if skipped_mobs:
        print(f"skipped {len(skipped_mobs)} lua keys with no matching MobDef: {sorted(set(skipped_mobs) - set(ALIASES) - SKIP_KEYS)}")
    if skipped_items:
        print(f"skipped {len(skipped_items)} item keys with no matching ItemDef: {sorted(skipped_items)}")


def apply_corrections(rows, mob_keys, item_keys):
    """Merge ARCHIVE_CORRECTIONS into the RTK-parsed rows: prepend missing base-loot lines, append missing
    rare accessories, and add rows for mobs RTK omits entirely (red_rabbit). Dedupes by item key so re-running
    is idempotent and an item already present in RTK's table is never doubled. Returns the sorted row list."""
    by_key = {mob: [loot, rare] for mob, loot, rare in rows}
    applied = 0
    for mob, fix in ARCHIVE_CORRECTIONS.items():
        if mob not in mob_keys:
            raise SystemExit(f"correction targets unknown mob key: {mob}")
        loot, rare = by_key.get(mob, ["", ""])
        loot_items = {p.split(":")[0] for p in loot.split("|") if p}
        rare_items = {p.split(":")[0] for p in rare.split("|") if p}

        add = []
        for item, amt, rate in fix.get("loot_add", []):
            if item != "GOLD" and item not in item_keys:
                raise SystemExit(f"correction item not in Items.csv: {item}")
            if item in loot_items:
                continue                                  # already present — leave RTK's own line untouched
            add.append(f"{item}:{amt}:{rate}")
            loot_items.add(item)
        if add:
            loot = "|".join(add) + (f"|{loot}" if loot else "")   # base line goes first, in order, like RTK
        for item, rate in fix.get("rare_add", []):
            if item not in item_keys:
                raise SystemExit(f"correction item not in Items.csv: {item}")
            if item in rare_items:
                continue
            rare = (f"{rare}|" if rare else "") + f"{item}:{rate}"
            rare_items.add(item)

        by_key[mob] = [loot, rare]
        applied += 1

    print(f"applied {applied} archive drop corrections (tswolf-tiebreaker)")
    return sorted((mob, lr[0], lr[1]) for mob, lr in by_key.items())


if __name__ == "__main__":
    main()
