"""Build a consolidated per-zone mob roster + drop-table report from our live data
(AreaSpawns.csv + Spawns.csv + mobs.csv + MobDrops.csv + Items.csv + Maps.csv),
so it can be cross-referenced against the tswolf/nexusatlas/nexuswiki/fandom archives.

Output: re/zone_drop_report.md — one section per map (zone) that has any spawns,
listing each mob present (source: area-spawn table vs fixed Spawns0 row), its
level/exp, and its resolved drop table (item names + rates), plus a JSON sibling
for programmatic cross-referencing.
"""
import csv
import json
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).parent.parent
DATA = ROOT / "data" / "game-data"


def read_csv(name):
    with (DATA / name).open(encoding="utf-8") as f:
        return list(csv.DictReader(f))


maps = {r["MapId"]: r["MapName"] for r in read_csv("Maps.csv")}
mobs = {r["MobId"]: r for r in read_csv("mobs.csv")}
items = {r["ItmId"]: r["ItmIdentifier"] for r in read_csv("Items.csv")}
item_names = {r["ItmIdentifier"]: r["ItmDescription"] for r in read_csv("Items.csv")}
drops = {r["MobKey"]: r for r in read_csv("MobDrops.csv")}

area_spawns = read_csv("AreaSpawns.csv")
fixed_spawns = read_csv("Spawns.csv")

zone_mobs = defaultdict(lambda: defaultdict(lambda: {"area_count": 0, "fixed_count": 0}))

for r in area_spawns:
    zone_mobs[r["Map"]][r["MobId"]]["area_count"] += int(r["Count"])

for r in fixed_spawns:
    zone_mobs[r["SpnMapId"]][r["SpnMobId"]]["fixed_count"] += 1


def fmt_loot(cell):
    if not cell:
        return None
    parts = []
    for tok in cell.split("|"):
        bits = tok.split(":")
        if len(bits) == 3:
            key, amt, rate = bits
        else:
            key, rate = bits
            amt = None
        name = "Gold" if key == "GOLD" else item_names.get(key, key)
        parts.append(f"{name}" + (f" x{amt}" if amt and amt != "1" else "") + f" ({rate}%)")
    return parts


report = {}
for map_id, mob_map in zone_mobs.items():
    zone_name = maps.get(map_id, f"<unknown map {map_id}>")
    entry = report.setdefault(f"{map_id}|{zone_name}", [])
    for mob_id, counts in mob_map.items():
        m = mobs.get(mob_id)
        if not m:
            entry.append({"mob_id": mob_id, "key": "<unknown>", "name": "<unknown>", **counts})
            continue
        key = m["Identifier"]
        d = drops.get(key)
        entry.append({
            "mob_id": mob_id,
            "key": key,
            "name": m["Description"],
            "level": m["Level"],
            "exp": m["Exp"],
            "vita": m["Vita"],
            **counts,
            "loot": fmt_loot(d["Loot"]) if d else None,
            "rare_loot": fmt_loot(d["RareLoot"]) if d else None,
        })

# sort zones by numeric map id
def sortkey(k):
    mid = k.split("|", 1)[0]
    return int(mid) if mid.isdigit() else 999999

out_md = ["# Zone -> Mob Roster + Drop Table Report (generated from live server data)\n"]
out_json = {}
for zk in sorted(report, key=sortkey):
    map_id, zone_name = zk.split("|", 1)
    mob_list = sorted(report[zk], key=lambda e: (-int(e.get("area_count", 0)) - int(e.get("fixed_count", 0))))
    out_json[zk] = mob_list
    out_md.append(f"\n## [{map_id}] {zone_name}\n")
    for e in mob_list:
        src = []
        if e.get("area_count"):
            src.append(f"area x{e['area_count']}")
        if e.get("fixed_count"):
            src.append(f"fixed x{e['fixed_count']}")
        out_md.append(f"- **{e['name']}** (`{e['key']}`) lvl {e.get('level','?')} exp {e.get('exp','?')} [{', '.join(src)}]")
        if e.get("loot"):
            out_md.append(f"  - loot: {', '.join(e['loot'])}")
        if e.get("rare_loot"):
            out_md.append(f"  - rare: {', '.join(e['rare_loot'])}")

(ROOT / "re" / "zone_drop_report.md").write_text("\n".join(out_md), encoding="utf-8")
(ROOT / "re" / "zone_drop_report.json").write_text(json.dumps(out_json, indent=1), encoding="utf-8")
print(f"wrote {len(report)} zones -> re/zone_drop_report.md / .json")
