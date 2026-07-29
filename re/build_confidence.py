#!/usr/bin/env python3
"""Derive a HIGH / MEDIUM / LOW confidence rating for every sourced datum in the data CSVs, from the
provenance recorded on each row (a `source`/`Sources` column) plus the per-source attributes in the
central registry data/game-data/Sources.csv.

Confidence is NEVER hand-authored -- it is computed here from the referenced sources, so adding a live
test or another anecdote automatically re-rates the datum. Tune the model by editing source `Weight`s in
the registry (or the RULES below) and re-running; nothing downstream stores a stale confidence.

Source cell grammar (in a data CSV's source column):
    A `|`-separated list of clauses. Each clause is either
        <id>                      -> applies to the whole row (field '*')
        <field>=<id>[,<id>...]    -> applies to that specific field only (per-field provenance)
    e.g.  *=rtk-db|MobLook=live-2026-07-26-wrabbit|MobArmor=?
    A bare '?' id means "explicitly flagged uncertain" (counts as unsourced, not an error).
    An empty cell inherits the file's DEFAULT source (configured per file below).

Weight scale (registry `Weight` column): 3=live/tested/client-RE/era-correct(<=2003) archival,
2=documented archival tutor post (older, ~2004-2012), 1=newer(2013+)/formula-site/generic archive,
0=RTK fallback (rtk-db/rtk-lua/rtk-c).
"""
import csv, sys
from pathlib import Path
from collections import Counter, defaultdict

ROOT = Path(__file__).resolve().parent.parent
REGISTRY = ROOT / "data/game-data/Sources.csv"

# Files to score: (path, source_column, default_source_id_for_blank_cells)
FILES = [
    ("data/game-data/SpellLearnCosts.csv", "source", None),
    ("data/game-data/mobs.csv",        "Sources", "rtk-db"),   # pilot column not added yet -> skipped if absent
    ("data/game-data/MythicCaves.csv", "Sources", None),       # zodiac cave reqs; 4 tutor-caves-* per row
    # Tier-1 location/warp geometry (mostly rtk-lua fallback -> LOW, which is honest until independently verified)
    ("data/game-data/Inns.csv",             "Sources", "rtk-lua"),
    ("data/game-data/ForageAreas.csv",      "Sources", "rtk-lua"),
    ("data/game-data/Doors.csv",            "Sources", None),
    ("data/game-data/PathHalls.csv",        "Sources", "rtk-lua"),
    ("data/game-data/GatewayGates.csv",     "Sources", "rtk-lua"),
    ("data/game-data/WorldMapDests.csv",    "Sources", "binary-re"),
    ("data/game-data/WorldMapTriggers.csv", "Sources", "rtk-lua"),
    ("data/game-data/FallRooms.csv",        "Sources", "rtk-lua"),
    ("data/game-data/ShopCatalogues.csv",   "Sources", "rtk-lua"),
    ("data/game-data/SpellParams.csv",       "Sources", "rtk-lua"),   # Lua verb/row spell params (spike)
    ("data/game-data/ItemParams.csv",        "Sources", "rtk-lua"),   # Lua verb/row item use-effect params
    # Phase-1 spell-DATA tables (extracted from Content.cs literals; all rtk-lua-sourced balance numbers).
    ("data/game-data/SpellLevels.csv",  "Sources", "rtk-lua"),
    ("data/game-data/Morphs.csv",       "Sources", "rtk-lua"),
    ("data/game-data/Pets.csv",         "Sources", "rtk-lua"),
    ("data/game-data/Traps.csv",        "Sources", "rtk-lua"),
    ("data/game-data/SpellMods.csv",    "Sources", "rtk-lua"),
    ("data/game-data/PathGrowth.csv",   "Sources", "rtk-lua"),   # per-class level-up HP/MP gain ranges
]

# ---- load the source registry ------------------------------------------------------------------------
reg = {}
with REGISTRY.open(encoding="utf-8") as f:
    for row in csv.DictReader(f):
        reg[row["SourceId"]] = {
            "weight": int(row.get("Weight") or 0),
            "tier":   row.get("Tier", ""),
            "type":   row.get("Type", ""),
        }

def classify(ids):
    """Map a list of source ids -> (confidence, note). Unknown ids are surfaced by the caller."""
    known = [reg[i] for i in ids if i in reg]
    if not known:
        return "UNSOURCED", "no known source"
    weights = [s["weight"] for s in known]
    best    = max(weights)
    strong  = sum(1 for w in weights if w >= 2)
    live_ct = sum(1 for s in known if s["tier"] == "live" or s["type"] in ("live-obs", "anecdote"))
    total   = len(known)
    if best >= 3 or strong >= 2 or live_ct >= 3:
        return "HIGH", f"best={best} strong={strong} live={live_ct}"
    if best == 2 or (best >= 1 and total >= 2):
        return "MEDIUM", f"best={best} total={total}"
    return "LOW", f"best={best} total={total}"

def parse_cell(cell, default):
    """cell -> {field: [ids]}. '*' is the row-wide field."""
    cell = (cell or "").strip()
    if not cell:
        return {"*": [default]} if default else {"*": []}
    out = defaultdict(list)
    for clause in cell.split("|"):
        clause = clause.strip()
        if not clause:
            continue
        if "=" in clause:
            field, ids = clause.split("=", 1)
            out[field.strip()].extend(i.strip() for i in ids.split(",") if i.strip())
        else:
            out["*"].append(clause)
    return out

# ---- score each file ---------------------------------------------------------------------------------
ORDER = ["HIGH", "MEDIUM", "LOW", "UNSOURCED"]
unknown_ids = Counter()
overall = Counter()

for rel, col, default in FILES:
    path = ROOT / rel
    if not path.exists():
        print(f"\n## {rel}\n   (file not found -- skipped)")
        continue
    with path.open(encoding="utf-8") as f:
        rdr = csv.DictReader(f)
        if col not in (rdr.fieldnames or []):
            print(f"\n## {rel}\n   (no '{col}' column yet -- skipped; add it to enable scoring)")
            continue
        tally = Counter()
        by_source = Counter()
        examples = defaultdict(list)
        n = 0
        for row in rdr:
            n += 1
            fields = parse_cell(row.get(col), default)
            for field, ids in fields.items():
                for i in ids:
                    if i and i != "?" and i not in reg:
                        unknown_ids[i] += 1
                    if i and i != "?":
                        by_source[i] += 1
                conf, why = classify(ids)
                tally[conf] += 1
                overall[conf] += 1
                keyname = row.get("key") or row.get("MobId") or row.get(rdr.fieldnames[0])
                if len(examples[conf]) < 3:
                    examples[conf].append(f"{keyname} [{field}] <- {','.join(ids) or '(none)'} ({why})")
    print(f"\n## {rel}   ({n} rows)")
    total_datums = sum(tally.values())
    for k in ORDER:
        if tally[k]:
            pct = 100 * tally[k] / total_datums
            print(f"   {k:9} {tally[k]:5}  ({pct:4.0f}%)   e.g. {examples[k][0] if examples[k] else ''}")
    print("   by source:", ", ".join(f"{s}={c}" for s, c in by_source.most_common()))

# ---- summary -----------------------------------------------------------------------------------------
print("\n## OVERALL")
tot = sum(overall.values()) or 1
for k in ORDER:
    if overall[k]:
        print(f"   {k:9} {overall[k]:5}  ({100*overall[k]/tot:4.0f}%)")
if unknown_ids:
    print("\n!! UNKNOWN source ids (not in registry -- add them or fix the tag):")
    for i, c in unknown_ids.most_common():
        print(f"     {i}  x{c}")
