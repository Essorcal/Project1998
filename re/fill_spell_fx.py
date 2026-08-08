"""Fill spell_effects.csv's `animation`/`sound` columns for the Damage/Heal archetypes.

Every other archetype (Buff/Cure/Debuff/TargetBuff/ManaBattery/...) already carries an explicit
animation+sound in these columns -- only Damage and Heal punt to a hardcoded pcalign ladder in C#
(Content.ZapEffect/HealEffect). This resolves that ladder ONCE into the columns so the C# side can
collapse to a plain column read, and `pcalign` leaves the runtime path entirely.

RESOLVING ALIGNMENT -- there are three sources and ALL THREE have errors:

  (a) `pcalign`, the 5th arg the spell passes to global_zap/global_attack/global_heal. Encodes
      (visual family + alignment) in one int. BUGS: poet/vital_spark.lua passes 0 for all four members;
      mage/call_lightning.lua passes 0 for mingken+ohaeng; rogue/lethal_strike.lua passes a kwisin
      constant for its unaligned member.
  (b) `SplAlignment` in Spells.csv (the game's own export). BUGS: the recover_rogue family is tagged
      0,1,1,2 -- a duplicate kwisin and no ohaeng at all.
  (c) POSITION within the Lua file. RTK groups a family as N tables in one file, ordered
      unaligned, kwisin, mingken, ohaeng. This has held on every family inspected, and it is the only
      source that is structural rather than a hand-typed value -- so it is the primary here.

We take position-in-file as the alignment, the visual FAMILY from whichever band the file's members
mostly agree on, and report every row where (a) or (b) disagrees so the exceptions stay visible rather
than being silently resolved.

Run:  python re/fill_spell_fx.py          # dry run, prints the full disagreement report
      python re/fill_spell_fx.py --write  # rewrite data/game-data/spell_effects.csv in place
"""
import csv, re, sys, shutil, collections
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SPELLS = ROOT / "data/game-data/Spells.csv"
FX = ROOT / "data/game-data/spell_effects.csv"
LUA = ROOT / "RTK-Server/rtklua/Accepted/Spells"

ALIGN_NAME = {0: "unaligned", 1: "kwisin", 2: "mingken", 3: "ohaeng"}

# --- the ladders, transcribed from common/global_{zap,attack,heal}.lua ----------------------------------
# Re-keyed by (visual family, alignment) instead of RTK's flattened int; the alignment each code means is
# taken from RTK's own inline comments. ZAP_SFX replaces RTK's sound 55 -- that is an id in a LATER
# client's sound space; the real 4.95 sound is 701.wav, calibrated by ear 2026-08-04 off Thunder Bolt/Spark.
ZAP_SFX = 701

FAMILIES = {
    "zap":          {0: (4, 56), 1: (17, 59), 2: (30, 57), 3: (4, ZAP_SFX)},
    "zap_override": {0: (4, 56), 1: (17, 59), 2: (30, 57), 3: (4, ZAP_SFX)},   # 250-253, bypasses the class shift
    "heal":         {0: (5, 4),  1: (65, 98), 2: (64, 63), 3: (63, 4)},
    "mage_fire":    {0: (8, 88), 1: (54, 88), 2: (104, 88), 3: (112, 88)},     # hellfire/inferno/doom
    "poet_retrib":  {0: (51, 88), 1: (100, 88), 2: (86, 88), 3: (114, 88)},
    "warr_assault": {0: (7, 88), 1: (67, 14), 2: (7, 87), 3: (60, 87)},        # berserk/ww/assault/siege
    "rogue_da":     {0: (9, 88), 1: (67, 102), 2: (32, 88), 3: (68, 88)},      # DA/LS/FB
    "da_berserk":   {0: (9, 14), 1: (68, 94), 2: (32, 87), 3: (7, 87)},        # 119 / 123 / 122 / 121
    "ls_ww":        {0: (7, 88), 1: (69, 88), 2: (67, 88), 3: (60, 102)},      # 124 / 127 / 126 / 125
}

# pcalign -> visual family, for deciding which band a FILE belongs to (the alignment comes from position).
CODE_FAMILY = {
    **{c: "zap" for c in (0, 1, 2, 3)},
    **{c: "mage_fire" for c in (30, 31, 32, 33)},
    **{c: "poet_retrib" for c in (40, 41, 42, 43)},
    **{c: "warr_assault" for c in (100, 101, 102, 103)},
    **{c: "rogue_da" for c in (200, 201, 202, 203)},
    **{c: "da_berserk" for c in (119, 121, 122, 123)},
    **{c: "ls_ww" for c in (124, 125, 126, 127)},
    **{c: "zap_override" for c in (250, 251, 252, 253)},
    # 10/11/12 are the "distinct animation ONLY while unaligned" specials -- still the zap family.
    10: "zap", 11: "zap", 12: "zap",
}

# Codes with no alignment axis at all: one fixed look, used verbatim regardless of position.
SINGLETON = {
    10: (27, ZAP_SFX), 11: (28, ZAP_SFX), 12: (29, ZAP_SFX),   # thunder bolt / spark / singe, unaligned only
    13: (-1, 58),                                              # taunt: sound, no graphic
    34: (41, 88), 35: (42, 88), 36: (43, 88),                   # fissure / lava surge / volcanic blast
    99: (6, 88), 104: (31, 30),                                 # unaligned LS/WW, warrior slash
    120: (6, 88),                                               # the second distinct "unaligned berserk" look
    400: (12, -1), 401: (44, -1),                               # dart / death trap
}

# The extractor stored the literal token `spellFX` where a spell assigns a local first (grep "local spellFX").
SPELLFX = {
    "berserk_warrior": 119, "feral_berserk_warrior": 200, "whirlwind_warrior": 125,
    "desperate_attack_rogue": 120, "lethal_strike_rogue": 201,
}

TABLE_RE = re.compile(r"^([A-Za-z_0-9]+)\s*=\s*\{", re.M)

# Spells the extractor never emitted a row for because they live in common/ rather than a class folder, so
# they fell through to Session.ApplyCastGeneric's keyword classifier (an invented formula, not RTK's).
# Transcribed straight from their Lua. Added only if the key is absent, so re-running is idempotent.
EXTRA_ROWS = [
    # common/soothe.lua: addHealthExtend(50), magic - 3, playSound(708), sendAnimation(5). The 708 here is
    # RTK's OWN id and it matches the 4.95 sound calibrated by ear 2026-08-04 -- they happen to agree.
    dict(key="soothe", archetype="Heal", mana="3", amountExpr="50", animation="5", sound="708"),
]

# Cells the extractor left blank because the Lua passes a LOCAL VARIABLE rather than a literal (the same miss
# that produced the `spellFX` token). Applied only where the cell is currently empty. Resolved from source.
#
# NOT patched, and deliberately so: drain/drink_of_souls/parasite/absorb (rogue/drain.lua) carry an explicit
# sendAnimation but the file contains ZERO playSound calls -- those four really are silent in RTK, so a blank
# sound is faithful, not a gap.
PATCH_CELLS = {
    "slash_warrior": {"sound": "60"},   # warrior/slash.lua: `local sound = 60` -> playSound(sound). Note this
                                        # is NOT the ladder's 30 for pcalign 104 -- Slash never calls global_zap,
                                        # it does its own sendAnimation(31) + playSound(60).
}


def lua_families():
    """file -> ordered table names. RTK's order IS the alignment order (unaligned, kwisin, mingken, ohaeng).

    `test_*.lua` is skipped: rogue/test_shuriken_toss.lua is a scratch COPY of rogue/singe.lua that redefines
    the same four tables verbatim, which is where spell_effects.csv's duplicate rows came from."""
    fams = {}
    for f in sorted(LUA.rglob("*.lua")):
        if f.parent.name == "common" or f.name.startswith("test_"):
            continue
        names = TABLE_RE.findall(f.read_text(encoding="utf-8", errors="replace"))
        if names:
            fams[f.relative_to(LUA).as_posix()] = names
    return fams


def normalize_cross_class(rows, dispname):
    """The same spell learned by two classes must look and sound the same. RTK's per-class copies drifted --
    e.g. Singe passes pcalign 11 (Spark's animation) in mage/singe.lua but 12 in rogue/singe.lua. Where a
    base spell disagrees across classes, MAGE's values win (user's call, 2026-08-05); with no mage row, the
    most common value wins. Returns the list of rows changed, for the report."""
    groups = collections.defaultdict(list)
    for r in rows:
        if r["archetype"].strip() not in ("Damage", "Heal"):
            continue
        k, c = r["key"].strip(), r["class"].strip()
        base = k[: -(len(c) + 1)] if c and k.endswith("_" + c) else k
        groups[base].append(r)

    changed = []
    for base, members in groups.items():
        seen = {(r["animation"].strip(), r["sound"].strip()) for r in members}
        if len(seen) < 2:
            continue
        mage = [r for r in members if r["class"].strip() == "mage"]
        if mage:
            want = (mage[0]["animation"].strip(), mage[0]["sound"].strip())
        else:
            want = collections.Counter(
                (r["animation"].strip(), r["sound"].strip()) for r in members).most_common(1)[0][0]
        for r in members:
            cur = (r["animation"].strip(), r["sound"].strip())
            if cur != want:
                changed.append((r["key"].strip(), dispname.get(r["key"].strip(), ""), base, cur, want))
                r["animation"], r["sound"] = want
    return changed


def main(write=False):
    align, dispname = {}, {}
    for r in csv.DictReader(SPELLS.open(encoding="utf-8", errors="replace")):
        k = r["SplIdentifier"].strip()
        if not k:
            continue
        try:
            align[k] = int(r["SplAlignment"])
        except ValueError:
            pass
        dispname[k] = r["SplDescription"].replace("\\'", "'")

    allrows = list(csv.DictReader(FX.open(encoding="utf-8", errors="replace")))
    fields = list(allrows[0].keys())

    # Drop duplicate keys (the test_shuriken_toss.lua copies -- see lua_families). The pairs are identical
    # apart from FX, so keeping the first is lossless; anything NOT identical is reported rather than dropped.
    rows, seen_keys, dropped, conflicting = [], {}, [], []
    for r in allrows:
        k = r["key"].strip()
        if k in seen_keys:
            prev = seen_keys[k]
            differs = [f for f in fields if f not in ("animation", "sound") and r[f].strip() != prev[f].strip()]
            (conflicting if differs else dropped).append((k, differs))
            if not differs:
                continue
        seen_keys[k] = r
        rows.append(r)
    by_key = {r["key"].strip(): r for r in rows}

    def code_of(key, row):
        raw = row["pcalign"].strip()
        if raw == "spellFX":
            return SPELLFX.get(key)
        return int(raw) if raw.lstrip("-").isdigit() else None

    filled = skipped = 0
    disagree_lua, disagree_csv, unresolved = [], [], []
    stats = collections.Counter()
    handled = set()

    for fam_file, names in lua_families().items():
        members = [(i, n) for i, n in enumerate(names) if n in by_key]
        if not members:
            continue
        # Which visual band is this file? Take the majority vote over its members' codes.
        votes = collections.Counter()
        for _, n in members:
            c = code_of(n, by_key[n])
            if c in CODE_FAMILY:
                votes[CODE_FAMILY[c]] += 1
        band = votes.most_common(1)[0][0] if votes else None

        for pos, name in members:
            row = by_key[name]
            arch = row["archetype"].strip()
            if arch not in ("Damage", "Heal"):
                continue
            handled.add(name)
            if row["animation"].strip() or row["sound"].strip():
                skipped += 1                      # already explicit -- never overwrite
                continue
            code = code_of(name, row)
            if code in SINGLETON:                 # fixed look, no alignment axis
                anim, snd = SINGLETON[code]
                row["animation"], row["sound"] = str(anim), str(snd)
                filled += 1
                stats[f"singleton {code}"] += 1
                continue
            fam = "heal" if arch == "Heal" else band
            if fam is None or len(members) > 4:
                unresolved.append((name, f"no band (file {fam_file}, {len(members)} tables)"))
                continue
            al = pos if len(members) == 4 else 0   # single-table files are the unaligned/base spell
            anim, snd = FAMILIES[fam][al]
            row["animation"], row["sound"] = str(anim), str(snd)
            filled += 1
            stats[fam] += 1

            # What alignment did the LUA itself claim? global_heal is indexed by a bare 0..3; the zap/attack
            # bands are (base + alignment) except da_berserk/ls_ww, which RTK lists DESCENDING.
            lua_al = None
            if fam == "heal":
                lua_al = code if code in (0, 1, 2, 3) else None
            elif fam == "da_berserk":
                lua_al = {119: 0, 123: 1, 122: 2, 121: 3}.get(code)
            elif fam == "ls_ww":
                lua_al = {124: 0, 127: 1, 126: 2, 125: 3}.get(code)
            elif code is not None and CODE_FAMILY.get(code) == fam:
                base = min(c for c in CODE_FAMILY if CODE_FAMILY[c] == fam)
                lua_al = code - base if 0 <= code - base <= 3 else None
            if lua_al is not None and lua_al != al:
                disagree_lua.append((name, dispname.get(name, ""), fam_file, lua_al, al))
            sa = align.get(name)
            if sa in (0, 1, 2, 3) and sa != al:
                disagree_csv.append((name, dispname.get(name, ""), fam_file, sa, al))

    for r in rows:                                 # anything the Lua sweep never reached
        if r["archetype"].strip() in ("Damage", "Heal") and r["key"].strip() not in handled \
           and not (r["animation"].strip() or r["sound"].strip()):
            unresolved.append((r["key"].strip(), "no Lua table found"))

    added = []
    for extra in EXTRA_ROWS:
        if extra["key"] in by_key:
            continue
        row = {f: extra.get(f, "") for f in fields}
        rows.append(row)
        by_key[extra["key"]] = row
        added.append(extra["key"])

    patched = []
    for key, cells in PATCH_CELLS.items():
        row = by_key.get(key)
        if row is None:
            continue
        for col, val in cells.items():
            if not row[col].strip():
                row[col] = val
                patched.append(f"{key}.{col}={val}")

    xclass = normalize_cross_class(rows, dispname)

    print(f"filled {filled}   already-populated {skipped}   unresolved {len(unresolved)}")
    print(f"dropped {len(dropped)} duplicate rows (identical apart from FX): "
          + ", ".join(k for k, _ in dropped))
    if conflicting:
        print(f"!! {len(conflicting)} duplicate keys that DIFFER on more than FX -- kept both, review:")
        for k, d in conflicting:
            print(f"     {k}  differs on {d}")
    print("by family:", dict(stats))
    if patched:
        print("patched blank cells (Lua local vars): " + ", ".join(patched))
    if added:
        print("added missing rows (from common/*.lua): " + ", ".join(added))
    print(f"\n=== {len(xclass)} rows normalized so the same spell matches across classes (mage wins) ===")
    for k, nm, base, cur, want in xclass:
        print(f"  {k:32} {nm:20} base={base:24} {cur} -> {want}")

    print(f"\n=== {len(disagree_lua)} rows where the LUA's pcalign disagrees with file position ===")
    for k, nm, f, a, b in disagree_lua:
        print(f"  {k:32} {nm:22} {f:34} lua={ALIGN_NAME[a]:9} -> pos={ALIGN_NAME[b]}")
    print(f"\n=== {len(disagree_csv)} rows where Spells.csv SplAlignment disagrees with file position ===")
    for k, nm, f, a, b in disagree_csv:
        print(f"  {k:32} {nm:22} {f:34} csv={ALIGN_NAME[a]:9} -> pos={ALIGN_NAME[b]}")
    if unresolved:
        print(f"\n=== {len(unresolved)} UNRESOLVED (left blank -> C# fallback) ===")
        for k, why in unresolved[:40]:
            print(f"  {k:32} {why}")

    if write:
        shutil.copy(FX, FX.with_suffix(".csv.bak"))
        with FX.open("w", newline="", encoding="utf-8") as f:
            w = csv.DictWriter(f, fieldnames=fields)
            w.writeheader()
            w.writerows(rows)
        print(f"\nwrote {FX}  (backup: {FX.name}.bak)")
    else:
        print("\n(dry run -- pass --write to apply)")


if __name__ == "__main__":
    main(write="--write" in sys.argv)
