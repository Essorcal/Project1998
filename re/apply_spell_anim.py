"""Join the nexusatlas spell->animation-GIF data to spell_effects.csv and report/apply the real
4.95 Effect.tbl animation ids.

Chain:  spell name (atlas) -> gif -> client Effect.tbl index (matched visually + by template
correlation, see match_spell_fx.py / template_match_fx.py) -> WIRE id.

WIRE ID = client Effect.tbl index + 1. The client loads Effect.tbl 1-based, so the 0x29 wire byte N
draws table entry N-1 (documented and live-proven in Server/Session.Entity.cs SendEffect). Every
spot-check agrees: heal.gif -> #4 -> 5 == heal_mage's 5; ignite.gif -> #3 -> 4 == ignite_mage's 4;
kwisinheal.gif -> #64 -> 65 == ancestors_touch_mage's 65.

IMPORTANT LIMITATION -- the atlas reuses ONE gif for a whole visual family (10 mage spells share
ignite.gif), so this resolves a spell's animation FAMILY, not necessarily its exact id. Where the
client holds several near-identical siblings (e.g. the bolts #26/#27/#29) the atlas cannot say which
one a given spell uses. So a disagreement with the CSV is only reported, never auto-applied, unless
--write is passed AND the spell's family maps to a single client effect.

Usage:
  python apply_spell_anim.py report            # join + diff, changes nothing
  python apply_spell_anim.py report --unmatched
"""
import csv
import json
import os
import re
import sys

FX = os.path.join(os.path.dirname(os.path.abspath(__file__)), "fx")
CSV = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   "data", "game-data", "spell_effects.csv")

# gif -> client Effect.tbl index (0-based). Established by template correlation at native scale and
# confirmed frame-by-frame; see fx/template_all.txt for the scores.
GIF_EFFECT = {
    "annihilation": 53, "berserk": 5, "chill": 21, "confuse": 33, "dart": 11, "deathtrap": 43,
    "desperate": 6, "endear": 38, "freeze": 51, "heal": 4, "hellfire": 7, "ignite": 3,
    "kwisinberserk": 67, "kwisinfury": 102, "kwisinheal": 64, "kwisininvoke": 94, "kwisinpara": 87,
    "kwisinpurge": 69, "kwisinremmy": 70, "kwisinret": 99, "kwisinrez": 95, "kwisinsanc": 60,
    "kwisinvex": 52, "kwisinww": 68, "ls": 8, "might": 10, "mingkenfury": 105,
    "mingkenhardenarmor": 109, "mingkenhardenbody": 71, "mingkenheal": 63, "mingkenhellfire": 103,
    "mingkeninspiration": 75, "mingkeninvoke": 89, "mingkenpara": 98, "mingkenpurge": 56,
    "mingkenremmy": 106, "mingkenret": 85, "mingkenrez": 115, "mingkensanc": 55,
    "mingkenshadowfigure": 19, "mingkenvenom": 83, "mingkenvex": 100, "mingkenww": 66,
    "mingkenzap": 29, "newmight": 116, "newshadowfigure": 20, "ohaenghardenarmor": 97,
    "ohaenghardenbody": 86, "ohaengheal": 62, "ohaenghf": 111, "ohaenginspire": 48,
    "ohaenginvoke": 77, "ohaengpurge": 107, "ohaengremmy": 57, "ohaengret": 113, "ohaengrez": 91,
    "ohaengsanc": 58, "ohaengvex": 78, "ohaengww": 59, "remmy": 9, "retribution": 50, "sanc": 1,
    "sealfate": 16, "slash": 30, "spark": 26, "spiritfury": 17, "summon": 2, "totemeffect": 15,
    "vex": 0, "whitetigerclaws": 31, "ww": 8,
    # kwisinarmor.gif was NEVER archived (empty CDX), so this one is inferred rather than seen:
    # its two siblings land on #109 (mingkenhardenarmor) and #97 (ohaenghardenarmor), and the CSV
    # already carries 111 for thicken_skin -- i.e. #110, directly adjacent to the Ming-Ken #109.
    # Adjacency + the existing value agreeing is good evidence, but it is NOT a visual match.
    "kwisinarmor": 110,
}
INFERRED = {"kwisinarmor"}
# none.gif is the atlas's explicit "no animation" marker -> the client's own empty effect slots
NO_ANIM = "none"

CLASSES = {"mage": "mage", "poet": "poet", "rogue": "rogue", "warrior": "warrior"}


# The atlas spells out names; our keys drop apostrophes and CLOSE UP hyphens
# ("Kwi-Sin Chameleon" -> kwisin_chameleon, "Infuse Life-force" -> infuse_lifeforce,
# "Dragon's Fury" -> dragons_fury). Underscoring the hyphen instead misses all of them.
def key_of(name, cls):
    s = name.lower().strip().replace("'", "").replace("’", "").replace("-", "")
    k = re.sub(r"[^a-z0-9]+", "_", s).strip("_")
    return f"{k}_{cls}" if cls else k


# Atlas name -> our key, for cases normalisation can't bridge: the atlas's own misspellings, and
# names that simply differ from the key the extractor produced.
ALIAS = {
    "avalance": "avalanche", "cresendo": "crescendo", "annoint": "anoint",
    "dispise_friend": "despise_friend", "caculating_blow": "calculating_blow",
    "darken_veil": "dark_veil", "death_trap": "set_death_trap", "set_trap": "set_trap",
    "winters_shadow": "winters_shadow", "unalign_armor": "unalign_armor",
}


# One atlas row that covers SEVERAL csv rows: the atlas documents "Call of the Wild" once, but the
# extractor split it per creature, and "Companion of Kwi-sin" is our kwisin_companion_poet.
MULTI = {
    ("call of the wild", "poet"): "cotw_",
    ("companion of kwisin", "poet"): "kwisin_companion_poet",
    ("companion of mingken", "poet"): "mingken_companion_poet",
    ("companion of ohaeng", "poet"): "ohaeng_companion_poet",
}


def resolve_multi(nm, cls, by_key):
    """-> list of csv rows an atlas row applies to (may be empty)."""
    stem = re.sub(r"[^a-z ]+", "", nm.lower().replace("-", "")).strip()
    tgt = MULTI.get((stem, cls))
    if not tgt:
        return []
    if tgt.endswith("_"):
        return [r for k, r in by_key.items() if k.startswith(tgt)]
    return [by_key[tgt]] if tgt in by_key else []


def resolve(nm, cls, by_key):
    """Atlas spell name -> csv row, trying exact, alias, then a conservative fuzzy match."""
    for base in (key_of(nm, cls), key_of(nm, "")):
        if base in by_key:
            return by_key[base], "exact"
    stem = key_of(nm, "")
    if stem in ALIAS:
        for cand in (f"{ALIAS[stem]}_{cls}", ALIAS[stem]):
            if cand in by_key:
                return by_key[cand], "alias"
    import difflib
    pool = [k for k in by_key if not cls or k.endswith("_" + cls) or "_" not in k]
    m = difflib.get_close_matches(key_of(nm, cls), pool, n=1, cutoff=0.90)
    if m:
        return by_key[m[0]], "fuzzy"
    return None, None


def load_atlas():
    return json.load(open(os.path.join(FX, "atlas_spells.json")))


def main():
    rows = list(csv.DictReader(open(CSV, encoding="utf-8-sig")))
    by_key = {r["key"]: r for r in rows}
    atlas = load_atlas()

    matched, unmatched, agree, differ, nogif = [], [], [], [], []
    for a in atlas:
        cls = CLASSES.get(a.get("class", ""), "")
        nm = a.get("aligned_name") or a["name"]
        gif = a["gif"][:-4]
        row, how = resolve(nm, cls, by_key)
        if row is None:
            unmatched.append((nm, cls, gif))
            continue
        matched.append((row["key"], gif))
        if gif == NO_ANIM:
            nogif.append((row["key"], row["animation"]))
            continue
        if gif not in GIF_EFFECT:
            continue
        wire = GIF_EFFECT[gif] + 1
        cur = (row["animation"] or "").strip()
        (agree if cur == str(wire) else differ).append((row["key"], cur, wire, gif))

    print(f"atlas rows {len(atlas)}  joined to csv {len(matched)}  unjoined {len(unmatched)}")
    print(f"\nanimation id agrees with atlas : {len(agree)}")
    print(f"animation id DIFFERS           : {len(differ)}")
    print(f"atlas says none.gif            : {len(nogif)}")

    if differ:
        print("\n--- disagreements (csv vs atlas-derived wire id) ---")
        seen = set()
        for k, cur, wire, gif in sorted(differ):
            if k in seen:
                continue
            seen.add(k)
            print(f"  {k:34s} csv={cur or '(blank)':>7s}  atlas={wire:<4d} ({gif})")
    if nogif:
        print("\n--- atlas says NO animation, csv has one ---")
        seen = set()
        for k, cur in sorted(nogif):
            if cur and k not in seen:
                seen.add(k)
                print(f"  {k:34s} csv={cur}")
    if "--unmatched" in sys.argv and unmatched:
        print("\n--- atlas spells with no csv key ---")
        for nm, cls, gif in sorted(set(unmatched))[:80]:
            print(f"  {nm:32s} {cls:8s} {gif}")


def export():
    """Write the two durable tables: the gif->effect map, and the per-spell join."""
    rows = list(csv.DictReader(open(CSV, encoding="utf-8-sig")))
    by_key = {r["key"]: r for r in rows}
    atlas = load_atlas()

    users = {}
    for a in atlas:
        users.setdefault(a["gif"][:-4], []).append(a.get("aligned_name") or a["name"])

    p1 = os.path.join(FX, "animation_map.csv")
    with open(p1, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["gif", "client_effect", "wire_id", "n_spells", "example_spells"])
        for g, e in sorted(GIF_EFFECT.items(), key=lambda t: t[1]):
            ex = sorted(set(users.get(g, [])))
            w.writerow([g, e, e + 1, len(users.get(g, [])), "; ".join(ex[:6])])
        w.writerow([NO_ANIM, "", "", len(users.get(NO_ANIM, [])), "(explicitly no animation)"])

    p2 = os.path.join(FX, "spell_animation_join.csv")
    with open(p2, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["key", "spell", "class", "alignment", "gif", "atlas_wire_id",
                    "csv_animation", "status"])
        seen = set()
        for a in atlas:
            cls = CLASSES.get(a.get("class", ""), "")
            nm = a.get("aligned_name") or a["name"]
            gif = a["gif"][:-4]
            row, how = resolve(nm, cls, by_key)
            if row is None or (row["key"], gif) in seen:
                continue
            seen.add((row["key"], gif))
            cur = (row["animation"] or "").strip()
            if gif == NO_ANIM:
                st = "none-in-atlas"
                wire = ""
            elif gif not in GIF_EFFECT:
                st, wire = "gif-unmatched", ""
            else:
                wire = GIF_EFFECT[gif] + 1
                st = "agree" if cur == str(wire) else ("fills-blank" if not cur else "differs")
            w.writerow([row["key"], nm, cls, a.get("alignment", ""), gif, wire, cur, st])
    print("wrote", p1)
    print("wrote", p2)


if __name__ == "__main__":
    if "export" in sys.argv:
        export()
    else:
        main()
