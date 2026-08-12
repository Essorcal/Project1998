#!/usr/bin/env python3
"""Fix a spell_effects.csv extraction bug: a batch of beneficial MIGHT/ARMOR buffs (Valor, Harden Armor, ...)
were mis-classified as `Debuff`/`slow`, so they routed to CastDebuff and hunted a mob to slow. Ground truth
is the RTK Lua (Spells/{mage,poet}/*.lua): each is a timed stat buff cast on a target.

Reclassify to archetype `TargetBuff` with the real stat/amount/duration:
  - MIGHT buffs -> buffStat=might, buffAmt=3   (RTK target.might + 3)
  - ARMOR buffs -> buffStat=armor, buffAmt=+10/+4 (RTK target.armor - 10/-4; our `armor` stat is SUBTRACTED
    from AC in Session, so a positive amount improves AC by the same magnitude), durationMs 300000/37000.
Also clears the bogus debuff=slow. Everything else (mana, animation, sound, action) was extracted correctly
and is left untouched. Per project policy spell_effects.csv IS the source of truth (extractor not re-run), so
this edits the rows in place.
"""
import csv
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CSV = ROOT / "game-data/spell_effects.csv"

MIGHT       = {"valor", "strengthen", "bless_muscles", "power_burst"}
ARMOR_LONG  = {"harden_armor", "thicken_skin", "shield_of_life", "elemental_armor"}
ARMOR_SHORT = {"bolster", "dark_armor", "life_armor", "armor_of_elements"}   # poet-only, shorter/weaker
# Sanctuary line: RTK `target.deduction -= 0.5` = a 0.5 incoming-damage MULTIPLIER (take half), 300s, PC-only.
# buffStat=deduction is fractional -> the amount is the final multiplier (0.5), NOT an int stat delta.
DEDUCTION   = {"sanctuary", "magic_shield", "protect_soul", "guard_life"}

def plan(key):
    for base in MIGHT:
        if key in (f"{base}_mage", f"{base}_poet"): return ("might", "3", 300000)
    for base in ARMOR_LONG:
        if key in (f"{base}_mage", f"{base}_poet"): return ("armor", "10", 300000)
    for base in ARMOR_SHORT:
        if key in (f"{base}_poet",): return ("armor", "4", 37000)
    for base in DEDUCTION:
        if key in (f"{base}_mage", f"{base}_poet"): return ("deduction", "0.5", 300000)
    return None

rows = list(csv.DictReader(CSV.open(encoding="utf-8")))
fields = rows[0].keys()
changed = 0
for r in rows:
    p = plan(r["key"])
    if not p: continue
    stat, amt, dur = p
    r["archetype"]  = "TargetBuff"
    r["buffStat"]   = stat
    r["buffAmt"]    = amt
    r["durationMs"] = str(dur)
    r["debuff"]     = ""
    changed += 1

with CSV.open("w", encoding="utf-8", newline="") as f:
    w = csv.DictWriter(f, fieldnames=list(fields))
    w.writeheader()
    w.writerows(rows)
print(f"reclassified {changed} rows -> TargetBuff")
