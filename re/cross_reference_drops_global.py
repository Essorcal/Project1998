"""Global (zone-independent) cross-reference: for every mob named in the tswolf/nexusatlas/
nexuswiki/fandom archives, check (1) whether a matching, real (non-training-dummy,
non-companion-pet) mob definition exists in mobs.csv at all, (2) whether that mob is
actually spawned anywhere in AreaSpawns.csv/Spawns0.csv, and (3) whether our MobDrops.csv
loot table for it contains every item the archives agree it drops.

This avoids the zone-name keyword-matching fragility of cross_reference_zones.py (many RTK
cave rooms are thematically named -- "Fang"/"Gnash"/"Drool" for the dog cave, "Rat Burrough"
for the rat cave -- without literally containing the family keyword, which produced false
"missing" results there). Matching a mob to its world population here is done directly by
MobId membership in the spawn tables, independent of which room it's placed in.
"""
import csv
import json
import re
from pathlib import Path
from collections import defaultdict

ROOT = Path(__file__).parent.parent
ARCHIVE = Path(r"C:\Users\brian\Desktop\scraped_nexus_data\artifacts\game_data")
DATA = ROOT / "data" / "game-data"

VORTEX_ERA_PAGES = {"events", "gogoonisland", "assassin", "bandit", "greyhand", "hillmen", "hunter",
                     "magus", "shadow", "wind", "water", "anchorite", "dread", "asmodi", "earth"}


def norm(s):
    return re.sub(r"[^a-z0-9]+", " ", s.lower()).strip()


def read_csv(name):
    with (DATA / name).open(encoding="utf-8") as f:
        return list(csv.DictReader(f))


mob_rows = read_csv("mobs.csv")
desc_to_rows = defaultdict(list)
for r in mob_rows:
    desc_to_rows[norm(r["Description"])].append(r)

area_mobids = set(r["MobId"] for r in read_csv("AreaSpawns.csv"))
trap_mobids = set(r["MobId"] for r in read_csv("AreaSpawnsTrap.csv"))  # tiger/rabbit-boss/trapdoor trap spawns
fixed_mobids = set(r["SpnMobId"] for r in read_csv("Spawns0.csv"))
all_spawned_ids = area_mobids | trap_mobids | fixed_mobids

drops = {r["MobKey"]: r for r in read_csv("MobDrops.csv")}
item_names_by_key = {r["ItmIdentifier"]: r["ItmDescription"] for r in read_csv("Items.csv")}


def pick_real_row(rows):
    """Pick the row that represents the actual huntable mob when several share a Description. Filters out
    the non-huntable twins by identifier prefix — dummy_ (mythic-boss AI placeholders), call_companion_
    (summoned pets), and museum_ (decorative 0-exp props whose LOWER id would otherwise win a naive pick;
    this is the golden_rabbit/log/marsh_ogre collision) — then prefers a candidate that's actually spawned
    so a real roaming mob beats a leftover unspawned variant. Falls back to whatever's there."""
    real = [r for r in rows if not r["Identifier"].startswith(("dummy_", "call_companion_", "museum_"))] or rows
    return next((r for r in real if r["MobId"] in all_spawned_ids), real[0])


def our_loot_items(mob_key):
    d = drops.get(mob_key)
    if not d:
        return set()
    items = set()
    for cell in (d["Loot"], d["RareLoot"]):
        if not cell:
            continue
        for tok in cell.split("|"):
            bits = tok.split(":")
            key = bits[0]
            items.add("gold" if key == "GOLD" else norm(item_names_by_key.get(key, key)))
    return items


# ---- gather archive mob -> drop-item-name set (union across all 3 archive sources) ----
archive_drops = defaultdict(set)   # norm(name) -> set of norm(item text)
archive_display = {}
archive_pages = defaultdict(set)


def clean_piece(piece):
    piece = re.sub(r"\(.*?\)", "", piece)          # strip "(22%)"
    piece = re.sub(r"^\s*[\d,]+\s+coins?\b", "gold", piece, flags=re.I)  # "3,000 Coins" -> gold
    piece = piece.replace(",", "")                  # drop thousands separators before norm
    piece = norm(piece)
    piece = re.sub(r"^\s*\d+\s+", "", piece).strip()  # strip leading amount "2 acorns"
    return piece


def add_items(name, page, items):
    """items: an already-split list of atomic drop-name strings (no further splitting)."""
    n = norm(name)
    archive_display[n] = name
    if page:
        archive_pages[n].add(page)
    for raw in items:
        if not raw:
            continue
        piece = clean_piece(raw)
        if piece and piece not in ("none", "nothing", "gold"):
            archive_drops[n].add(piece)
        elif piece == "gold":
            archive_drops[n].add("gold")


def add_blob(name, page, sep, *texts):
    """texts: single delimited strings (nexuswiki ';'-joined, fandom ','-joined) to split first."""
    for t in texts:
        if not t:
            continue
        add_items(name, page, re.split(sep, t))


with (ARCHIVE / "nexusatlas_monsters" / "monsters.jsonl").open(encoding="utf-8") as f:
    for line in f:
        d = json.loads(line)
        add_items(d["name"], d["page"], (d.get("common_drops") or []) + (d.get("rare_drops") or []))

with (ARCHIVE / "nexuswiki_monsters" / "monsters.jsonl").open(encoding="utf-8") as f:
    for line in f:
        d = json.loads(line)
        add_blob(d["name"], None, r";", d.get("drops"), d.get("rare_drops"))

with (ARCHIVE / "fandom_monsters" / "monsters.jsonl").open(encoding="utf-8") as f:
    for line in f:
        d = json.loads(line)
        add_blob(d["name"], None, r",", d.get("drops"))

# ---- compare ----
not_in_csv = []
never_spawned = []
drop_mismatches = []

for n, item_set in archive_drops.items():
    if archive_pages.get(n) and archive_pages[n] <= VORTEX_ERA_PAGES and len(archive_pages[n]) == len(archive_pages[n] & VORTEX_ERA_PAGES) and archive_pages[n]:
        continue  # skip pure vortex-era-only mobs
    rows = desc_to_rows.get(n)
    if not rows:
        not_in_csv.append(archive_display[n])
        continue
    row = pick_real_row(rows)
    spawned = row["MobId"] in all_spawned_ids
    if not spawned:
        never_spawned.append((archive_display[n], row["Identifier"]))
        continue
    our_items = our_loot_items(row["Identifier"])
    missing_items = [x for x in item_set if x and x not in our_items and not any(x in oi or oi in x for oi in our_items)]
    if missing_items:
        drop_mismatches.append((archive_display[n], row["Identifier"], tuple(sorted(missing_items)), tuple(sorted(our_items))))

out = ["# Global mob/drop cross-reference (zone-independent)\n"]
out.append(f"Archive mobs checked: {len(archive_drops)}. Excluded vortex-era-only pages: {sorted(VORTEX_ERA_PAGES)}.\n")

out.append(f"\n## Archive mobs with NO matching Description in mobs.csv ({len(not_in_csv)})\n")
for n in sorted(set(not_in_csv)):
    out.append(f"- {n}")

out.append(f"\n## Archive-documented mobs that exist in mobs.csv but are NEVER spawned anywhere (AreaSpawns.csv + Spawns0.csv) ({len(never_spawned)})\n")
for n, key in sorted(set(never_spawned)):
    out.append(f"- {n} (key=`{key}`)")

out.append(f"\n## Spawned mobs whose real drop table is missing an archive-documented item ({len(drop_mismatches)})\n")
for n, key, missing, ours in sorted(set(drop_mismatches)):
    out.append(f"- **{n}** (`{key}`): missing {missing} -- our current loot: {ours}")

(ROOT / "re" / "global_cross_reference.md").write_text("\n".join(out), encoding="utf-8")
print(f"not_in_csv={len(not_in_csv)} never_spawned={len(set(never_spawned))} drop_mismatches={len(set(drop_mismatches))}")
