#!/usr/bin/env python3
"""Extract the hardcoded spell-DATA dictionaries out of Server/Content.cs into flat CSVs (Phase 1 of the
data-driven-server plan). Runs ONCE to generate the CSVs with guaranteed fidelity (parses the C# literals
directly — no hand transcription), after which Content.Load() reads the CSVs and the literals are deleted.

Extracts (all keyed by spell identifier, pure balance/param data a modder tunes):
  SpellLevelOverrides -> SpellLevels.csv   (key,level)                    real level gate for Type-5 skills
  MorphSpells + MorphDispatchSpells -> Morphs.csv (key,look,lookFemale,mana,durationMs,answers)
  PetSpells -> Pets.csv                    (key,mobKey,level,mana,cooldownMs)
  TrapSpells -> Traps.csv                  (key,kind,level,mana)          spell-side cast cost (kind=enum name)
  RageAmount + EnchantSpells -> SpellMods.csv (key,rage,enchantAmt,enchantMana)

Deliberately NOT extracted (classification welded to a C# mechanic, not tunable balance numbers): the family
membership HashSets (Stealth/ManaSteal/ManaGift/Cleanse/Revive/Leap/GroundLoot/Ambush/SpotTraps/Divination/
Bladestorm), SacrificeAliases (enum family), TrapKeyForAnswer/TrapWireKind (fixed enum<->string plumbing),
and World.cs trap damage/durations (welded to the TriggerTrapLocked switch).
"""
import re, csv, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "Server" / "Content.cs"
OUT = ROOT / "data" / "game-data"
text = SRC.read_text(encoding="utf-8")

def block(name):
    """Return the text of a `= new(...) { ... };` initializer for the field named `name`."""
    m = re.search(r"\b" + re.escape(name) + r"\b\s*=\s*\n?\s*new\b[^{]*\{", text)
    if not m:
        # single-line `new(...) {`
        m = re.search(r"\b" + re.escape(name) + r"\b[^\n]*new\b[^{]*\{", text)
    if not m:
        sys.exit(f"could not find block {name}")
    i = m.end()  # just past the opening brace
    depth = 1
    while i < len(text) and depth:
        if text[i] == "{": depth += 1
        elif text[i] == "}": depth -= 1
        i += 1
    return text[m.end():i-1]

def write_csv(path, header, rows):
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f, lineterminator="\n")
        w.writerow(header)
        w.writerows(rows)
    print(f"  {path.name}: {len(rows)} rows")

# ---- SpellLevels ----
lvl = re.findall(r'\["([^"]+)"\]\s*=\s*(\d+)', block("SpellLevelOverrides"))
write_csv(OUT / "SpellLevels.csv", ["key", "level"], [(k, v) for k, v in lvl])

# ---- Morphs (fixed + dispatch) ----
morph_rows = []
for k, l, lf, mn, d in re.findall(r'\["([^"]+)"\]\s*=\s*\((\d+),\s*(\d+),\s*(\d+),\s*(\d+)\)', block("MorphSpells")):
    morph_rows.append((k, l, lf, mn, d, ""))
for m in re.finditer(r'\["([^"]+)"\]\s*=\s*\(new\([^)]*\)\s*\{([^}]*)\},\s*(\d+),\s*(\d+)\)', block("MorphDispatchSpells")):
    key, inner, mn, d = m.group(1), m.group(2), m.group(3), m.group(4)
    answers = ";".join(f"{a}:{v}" for a, v in re.findall(r'\["([^"]+)"\]\s*=\s*(\d+)', inner))
    morph_rows.append((key, "", "", mn, d, answers))
write_csv(OUT / "Morphs.csv", ["key", "look", "lookFemale", "mana", "durationMs", "answers"], morph_rows)

# ---- Pets ----
pets = re.findall(r'\["([^"]+)"\]\s*=\s*\("([^"]+)",\s*(\d+),\s*(\d+),\s*(\d+)\)', block("PetSpells"))
write_csv(OUT / "Pets.csv", ["key", "mobKey", "level", "mana", "cooldownMs"], pets)

# ---- Traps (spell-side cast cost; kind = TrapKind enum name) ----
traps = re.findall(r'\["([^"]+)"\]\s*=\s*\(TrapKind\.(\w+),\s*(\d+),\s*(\d+)\)', block("TrapSpells"))
write_csv(OUT / "Traps.csv", ["key", "kind", "level", "mana"], traps)

# ---- SpellMods (rage + enchant, one row per key) ----
mods = {}
for k, v in re.findall(r'\["([^"]+)"\]\s*=\s*(\d+)\s*,', block("RageAmount")):
    mods.setdefault(k, {})["rage"] = v
for k, a, mn in re.findall(r'\["([^"]+)"\]\s*=\s*\(([\d.]+),\s*(\d+)\)', block("EnchantSpells")):
    mods.setdefault(k, {})["ea"] = a; mods[k]["em"] = mn
mod_rows = [(k, d.get("rage", ""), d.get("ea", ""), d.get("em", "")) for k, d in mods.items()]
write_csv(OUT / "SpellMods.csv", ["key", "rage", "enchantAmt", "enchantMana"], mod_rows)

print("done.")
