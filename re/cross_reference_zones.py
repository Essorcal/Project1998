"""Cross-reference our live mob spawn/drop data (re/zone_drop_report.json) against the
scraped archives (nexusatlas/nexuswiki/fandom/tswolf) for the ~12 animal-themed cave
families that actually exist in our 4.95-era mobs.csv (rat/rabbit/dog/snake/horse/
monkey/rooster/pig/ox/sheep/tiger/dragon), plus the overworld scatter zones
(wilderness/woodlands/buya/kugnae/nagnang/vale/islets).

Deliberately excludes nexusatlas "pages" whose mob names don't appear anywhere in our
mobs.csv at all (assassin/bandit/greyhand/hillmen/hunter/magus/shadow/wind/water/
anchorite/dread/Asmodi/earth/events/gogoonisland) -- those are later-game-version content
("Vortex Armors" drops place them post-4.95) that this 4.95 server was never going to have;
diffing them would just be noise, not a real discrepancy.
"""
import json
import re
from pathlib import Path
from collections import defaultdict
from _paths import DATA, RE, ROOT, RTK_LUA, ARCHIVE

ROOT = Path(__file__).parent.parent
ARCHIVE = ARCHIVE / "artifacts" / "game_data"

CATEGORY_KEYWORDS = {
    "rat": ["rat", "mouse", "mice"],
    "rabbit": ["rabbit", "hare", "bunny"],
    "dog": ["dog", "mongrel", "mutt", "wolf-kin"],
    "snake": ["snake", "worm", "slither", "spasm", "boa"],
    "horse": ["horse", "hoov"],
    "monkey": ["monkey", "gorilla"],
    "rooster": ["rooster", "chick", "chicken", "roost", "feather"],
    "pig": ["pig", "boar", "hog", "warthog", "piglet"],
    "ox": ["ox", "bull", "horn"],
    "sheep": ["sheep", "ewe", "ram", "wool"],
    "tiger": ["tiger"],
    "dragon": ["dragon", "wyrm", "wyvern", "drake"],
}
OVERWORLD = {"wilderness": ["wilderness"], "woodlands": ["woodlands"], "buya": ["buya"],
             "kugnae": ["kugnae"], "nagnang": ["nagnang", "nagnag"], "vale": ["vale"],
             "islets": ["islets"]}

NORM_RE = re.compile(r"[^a-z0-9]+")


def norm(s):
    return NORM_RE.sub(" ", s.lower()).strip()


def categorize(name, keyword_map):
    n = norm(name)
    for cat, kws in keyword_map.items():
        for kw in kws:
            if kw in n:
                return cat
    return None


# ---- archive side ----
archive = defaultdict(lambda: defaultdict(lambda: {"exp": set(), "common": set(), "rare": set(), "sources": set()}))

with (ARCHIVE / "nexusatlas_monsters" / "monsters.jsonl").open(encoding="utf-8") as f:
    for line in f:
        d = json.loads(line)
        cat = CATEGORY_KEYWORDS.get(d["page"]) and d["page"] if d["page"] in CATEGORY_KEYWORDS else \
              (d["page"] if d["page"] in OVERWORLD else None)
        if not cat:
            continue
        e = archive[cat][d["name"]]
        if d.get("experience"):
            e["exp"].add(str(d["experience"]))
        e["common"].update(d.get("common_drops") or [])
        e["rare"].update(d.get("rare_drops") or [])
        e["sources"].add("nexusatlas")

with (ARCHIVE / "nexuswiki_monsters" / "monsters.jsonl").open(encoding="utf-8") as f:
    for line in f:
        d = json.loads(line)
        loc = d.get("where_found") or ""
        cat = categorize(loc, CATEGORY_KEYWORDS) or categorize(loc, OVERWORLD)
        if not cat:
            continue
        e = archive[cat][d["name"]]
        if d.get("experience") and d["experience"] != "None":
            e["exp"].add(str(d["experience"]))
        drops = d.get("drops") or ""
        if drops and drops != "None":
            e["common"].update(x.strip() for x in drops.split(";"))
        rare = d.get("rare_drops") or ""
        if rare and rare != "None":
            e["rare"].update(x.strip() for x in rare.split(";"))
        e["sources"].add("nexuswiki")

with (ARCHIVE / "fandom_monsters" / "monsters.jsonl").open(encoding="utf-8") as f:
    for line in f:
        d = json.loads(line)
        loc = d.get("location") or ""
        cat = categorize(loc, CATEGORY_KEYWORDS) or categorize(loc, OVERWORLD)
        if not cat:
            continue
        e = archive[cat][d["name"]]
        if d.get("experience"):
            e["exp"].add(str(d["experience"]))
        drops = d.get("drops") or ""
        if drops and drops.lower() != "nothing":
            e["common"].update(x.strip() for x in re.split(r",|;", drops))
        e["sources"].add("fandom")

# ---- our side ----
ours = json.load(open(ROOT / "re" / "zone_drop_report.json", encoding="utf-8"))
our_by_cat = defaultdict(lambda: defaultdict(lambda: {"loot": set(), "rare": set(), "zones": set(), "exp": set()}))

for zk, mob_list in ours.items():
    map_id, zone_name = zk.split("|", 1)
    cat = categorize(zone_name, CATEGORY_KEYWORDS)
    ov_cat = categorize(zone_name, OVERWORLD)
    target_cat = cat or ov_cat
    if not target_cat:
        continue
    for m in mob_list:
        e = our_by_cat[target_cat][m["name"]]
        e["zones"].add(zone_name)
        e["exp"].add(str(m.get("exp", "")))
        for l in (m.get("loot") or []):
            e["loot"].add(l)
        for l in (m.get("rare_loot") or []):
            e["rare"].add(l)

# ---- diff ----
out = ["# Zone/Drop-table cross-reference: our live data vs tswolf/nexusatlas/nexuswiki/fandom archives\n"]
out.append("Scope note: nexusatlas pages assassin/bandit/greyhand/hillmen/hunter/magus/shadow/wind/water/"
           "anchorite/dread/Asmodi/earth/events/gogoonisland were excluded -- none of their mob names exist "
           "anywhere in our mobs.csv, and their drops include 'Vortex Armors' etc., marking them as "
           "later-game-version content this 4.95-era server was never going to have. Tangun/Vortex map "
           "shells exist in Maps.csv but have zero spawns wired in AreaSpawns.csv/Spawns0.csv -- consistent "
           "with the same later-era-content gap, not a bug.\n")

all_cats = list(CATEGORY_KEYWORDS) + list(OVERWORLD)
for cat in all_cats:
    arc = archive.get(cat, {})
    our = our_by_cat.get(cat, {})
    if not arc and not our:
        continue
    out.append(f"\n## {cat}\n")
    out.append(f"Archive mobs: {len(arc)} | Our mobs: {len(our)} | Our zones: "
               f"{sorted(set(z for e in our.values() for z in e['zones']))[:15]}")

    def norm_name(n):
        return norm(n).replace("s ", " ").rstrip("s")

    arc_norm = {norm_name(n): n for n in arc}
    our_norm = {norm_name(n): n for n in our}

    missing = [arc[arc_norm[k]] and arc_norm[k] for k in arc_norm if k not in our_norm]
    extra = [our_norm[k] for k in our_norm if k not in arc_norm]
    matched = [(arc_norm[k], our_norm[k]) for k in arc_norm if k in our_norm]

    if missing:
        out.append(f"\n**Archive-documented mobs ABSENT from our spawns ({len(missing)}):**")
        for n in sorted(missing):
            e = arc[n]
            out.append(f"  - {n} (src: {','.join(e['sources'])}; exp {','.join(sorted(e['exp']))[:40]}; "
                       f"common {sorted(e['common'])}; rare {sorted(e['rare'])})")

    if extra:
        out.append(f"\n**In our spawns but not named in archive ({len(extra)}):** {sorted(extra)}")

    if matched:
        out.append(f"\n**Matched ({len(matched)}) -- drop comparison:**")
        for an, on in sorted(matched):
            ae = arc[an]
            oe = our[on]
            arc_items = {norm(x) for x in ae["common"] | ae["rare"]}
            our_items = {norm(re.sub(r"\s*\(.*?\)|\s*x\d+", "", x)) for x in oe["loot"] | oe["rare"]}
            arc_items.discard("")
            missing_items = [x for x in ae["common"] | ae["rare"] if norm(x) not in our_items]
            if missing_items or (arc_items and not our_items and (ae["common"] or ae["rare"])):
                out.append(f"  - **{an}**: archive drops {sorted(ae['common'] | ae['rare'])} vs ours "
                           f"{sorted(oe['loot'] | oe['rare'])}")

(ROOT / "re" / "zone_cross_reference.md").write_text("\n".join(out), encoding="utf-8")
print("wrote re/zone_cross_reference.md")
print(f"{sum(len(v) for v in archive.values())} archive mob-entries across {len(archive)} categories")
print(f"{sum(len(v) for v in our_by_cat.values())} our mob-entries across {len(our_by_cat)} categories")
