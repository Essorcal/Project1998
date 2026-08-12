"""Apply the reviewed animation-id decisions to game-data/spell_effects.csv.

Every conflict between the CSV (RTK-derived) and nexusatlas was rendered side by side and settled by
eye -- see re/fx/review/*.png. DECISIONS records that verdict per (gif, csv value, atlas value)
group, so the reasoning is auditable and re-runnable rather than a one-off edit.

The atlas won nearly every conflict, but NOT all of them, which is why this is a table and not a
blanket overwrite:
  * spark 28 vs 27 -- the atlas illustrates Thunder Bolt, Spark and Singe with ONE Thunder Bolt
    capture. It matches #26/wire 27 exactly, and thunder_bolt is already 27. Spark/Singe use a
    genuinely different sibling bolt, so the CSV's 28 is the finer answer.
  * fissure / lava_surge -- the atlas tags both with confuse.gif (a tiny figure). The CSV's 41/42
    are lava eruption effects, which is obviously right for spells named Fissure and Lava Surge.
    An atlas mislabel, not a CSV error.

`-1` means "confirmed to have no animation" (the atlas's none.gif marker), as distinct from an empty
cell, which still means "unknown". Four rows already used -1 this way.

Usage:
  python write_spell_anim.py            # dry run, prints every edit
  python write_spell_anim.py --write    # apply, after writing a .bak
"""
import csv
import os
import shutil
import sys

import apply_spell_anim as AP
import triage_anim as T

# (gif, csv value, atlas value) -> "atlas" | "csv"
DECISIONS = {
    ("summon", "12", "3"): "atlas",
    ("ohaenghardenarmor", "59", "98"): "atlas",
    ("ohaenghardenarmor", "70", "98"): "atlas",
    ("ohaengsanc", "98", "59"): "atlas",
    ("newmight", "11", "117"): "atlas",
    ("mingkenvenom", "5", "84"): "atlas",
    ("berserk", "9", "6"): "atlas",
    ("desperate", "6", "7"): "atlas",
    ("vex", "5", "1"): "atlas",
    ("vex", "2", "1"): "atlas",
    ("dart", "33", "12"): "atlas",
    ("confuse", "39", "34"): "atlas",
    ("freeze", "120", "52"): "atlas",
    ("kwisinremmy", "77", "71"): "atlas",
    ("mingkenhardenbody", "74", "72"): "atlas",
    ("ohaengremmy", "108", "58"): "atlas",
    ("spark", "28", "27"): "csv",
    ("confuse", "41", "34"): "csv",
    ("confuse", "42", "34"): "csv",
    # These three the similarity heuristic auto-kept as "sibling variants"; rendering them side by
    # side showed the atlas effect matching frame-for-frame and the CSV one merely resembling it.
    # A reminder that the heuristic shortlists, it does not decide.
    ("ww", "7", "9"): "atlas",
    ("vex", "75", "1"): "atlas",
    ("spiritfury", "11", "18"): "atlas",
    ("deathtrap", "12", "44"): "atlas",
}
# Per-key overrides beat the group rule. "Set Trap" and "Death Trap" head the SAME atlas table, so
# both pick up the deathtrap.gif row; only Death Trap actually uses that animation.
#
# The trap LADDER comes from tswolf, not nexusatlas: nexusatlas collapses the whole ladder into two
# rungs, but tswolf's 2001 rogue page has a "Graphic / Trap / Level / Mana Cost" table listing every
# rung with its own image, each `<img>` immediately followed by its own label. tswolf serves the SAME
# image files (all seven md5s are byte-identical to the nexusatlas copies), so the existing
# gif -> effect mapping carries straight over.
#   https://web.archive.org/web/20010724080903/http://www.tswolf.com/spells/rogue.shtml
TRAPS = {
    "set_dart_trap": "12",            # dart.gif       lvl 26
    "set_flash_trap": "11",           # might.gif      lvl 35  (a radial flash -- fits the name)
    "set_repeating_dart_trap": "12",  # dart.gif       lvl 44
    "set_snare_trap": "1",            # vex.gif        lvl 63
    "set_spear_trap": "6",            # berserk.gif    lvl 70
    "set_poison_dart_trap": "1",      # vex.gif        lvl 77
    "set_death_trap": "44",           # deathtrap.gif  lvl 88
    "set_sleep_trap": "2",            # sanc.gif       lvl 98
    "set_bladestorm_trap": "9",       # ls.gif -- user: same animation as Lethal Strike
}
# Spells.csv ids 2710-2713 make these the ALIGNED variants of Bladestorm trap (rogue, Mark 2,
# level 99, 200k gold). The whole family renders as Lethal Strike -- user-confirmed: "all variants of
# bladestorm look the same". Note this DOES break the pattern the other two slash families follow
# (lethal_strike/whirlwind both go 9 / 69 / 67 / 60 across alignments); the traps do not vary, so the
# pattern is not a safe generalisation.
TRAPS.update({
    "set_swords_dance_trap": "9",     # Kwi-Sin
    "set_tigers_ambush_trap": "9",    # Ming-Ken
    "set_cutting_edge_trap": "9",     # Ohaeng
})

# Poet pet-summons: user-confirmed these have NO animation -- the spell just summons the pet.
# The full ladder is 4 alignments x 8 tiers (levels 63/68/72/81/90/99, then Mark 1 and Mark 2).
PET_SUMMONS = [f"{a}_{t}_poet" for a in ("kwisin", "mingken", "ohaeng")
               for t in ("companion", "assistant", "protector", "fighter", "warrior",
                         "champion", "avatar")] + \
              [f"cotw_{p}_poet" for p in ("controller", "caterpillar", "fluffy_dog", "panda_bear",
                                          "wild_monkey", "gorilla", "wind_dancer", "wind_warrior")]

KEY_DECISIONS = {"set_trap": "12", **TRAPS, **{k: "-1" for k in PET_SUMMONS}}
# atlas none.gif + csv has a value -> clear to -1 ("confirmed none")
CLEAR_TO_NONE = {("none", "12", ""), ("none", "2", "")}


def main():
    write = "--write" in sys.argv
    rows = list(csv.DictReader(open(AP.CSV, encoding="utf-8-sig")))
    fields = list(rows[0].keys())
    by_key = {r["key"]: r for r in rows}

    edits, unresolved = [], []
    # Per-key decisions apply to EVERY row, not just the ones the atlas happened to join -- most of
    # the trap ladder has no nexusatlas row at all and would otherwise never be visited.
    for k, want in KEY_DECISIONS.items():
        row = by_key.get(k)
        if row is None:
            print(f"  !! no csv row for {k}")
        elif (row["animation"] or "").strip() != want:
            why = ("pet summon: no animation (user)" if k in PET_SUMMONS else
                   "bladestorm family = lethal strike (user)"
                   if k in ("set_swords_dance_trap", "set_tigers_ambush_trap",
                            "set_cutting_edge_trap") else "trap ladder (tswolf)")
            edits.append((k, (row["animation"] or "").strip(), want, why))
    for k, nm, cls, gif, cur, wire, verdict, why in T.build():
        row = by_key.get(k)
        if row is None:
            continue
        grp = (gif, cur, str(wire))
        if k in KEY_DECISIONS:
            want = KEY_DECISIONS[k]
            if cur != want:
                edits.append((k, cur, want, "per-key override"))
            continue
        if grp in DECISIONS:
            if DECISIONS[grp] == "atlas" and cur != str(wire):
                edits.append((k, cur, str(wire), f"reviewed: atlas ({gif})"))
            continue
        if verdict == "TAKE-ATLAS":
            edits.append((k, cur, str(wire), f"{verdict}: {why}"))
        elif verdict == "KEEP-CSV":
            continue
        elif grp in CLEAR_TO_NONE or (gif == AP.NO_ANIM and (gif, cur, "") in CLEAR_TO_NONE):
            edits.append((k, cur, "-1", "atlas: none.gif (confirmed no animation)"))
        elif grp in DECISIONS:
            if DECISIONS[grp] == "atlas":
                edits.append((k, cur, str(wire), f"reviewed: atlas ({gif})"))
        else:
            unresolved.append((k, gif, cur, wire, why))

    for k, old, new, why in edits:
        by_key[k]["animation"] = new

    print(f"{len(edits)} edits, {len(unresolved)} unresolved\n")
    for k, old, new, why in sorted(edits):
        print(f"  {k:34s} {old or '(blank)':>7s} -> {new:<5s} {why}")
    if unresolved:
        print("\n--- UNRESOLVED (no decision recorded) ---")
        for u in sorted(unresolved):
            print("  ", u)

    if write:
        shutil.copy(AP.CSV, AP.CSV + ".bak")
        with open(AP.CSV, "w", newline="", encoding="utf-8") as f:
            w = csv.DictWriter(f, fieldnames=fields)
            w.writeheader()
            w.writerows(rows)
        print(f"\nwrote {AP.CSV} (backup at {os.path.basename(AP.CSV)}.bak)")
    else:
        print("\ndry run — pass --write to apply")


if __name__ == "__main__":
    main()
