#!/usr/bin/env python3
"""Extract the EXACT mob-burst variant tables from RTK's ambush Lua into game-data/AmbushBursts.csv.

Source of truth:
  RTK-Server/rtklua/Accepted/NPCs/rabbitTrap.lua   (spawnRabbitMob1/2/3, spawnRabbitSentsMob1/2/3)
  RTK-Server/rtklua/Accepted/NPCs/tigerTrap.lua    (spawnTigerMob1/2/3, spawnTigerBigMob1/2/3)

Each `spawnXxx = function(...)` body builds a Lua table `mobs[i][j] = <id>`, then picks a random
variant i (`math.random(1, #mobs)`) and spawns every id in that variant around the player. We parse the
`mobs[i][j] = N` lines, group by variant i, and emit one CSV row per (table, variant):

    Table,Variant,MobIds
    tiger_mob1,3,296;296;297;295

The per-MAP branch logic (which table a map uses, ambush message, sentry y-split, big-mob roll, boss rolls)
is NOT extracted here — it is irregular control flow, hand-authored in game-data/AmbushConfig.csv. This
script only pins the exact composition tables so they can't drift from the Lua by hand-transcription error.

Tiger sentries (spawnTigerSentries) take the sentry mob id as a PARAMETER (RTK ids 804/805/806), so their
"5x <param>" burst is expressed in AmbushConfig.csv, not here. The fixed-id tables below are all that vary.

Two buckets are appended after the parse, and they are NOT the same kind of claim: SYNTH is RTK behaviour
whose ids are param-driven (the tiger sentries above); OBSERVED is a table with no RTK source at all,
reconstructed from live-server eyewitness. Keep them apart — one is transcription, the other is a guess.

Run:  python re/extract_ambush_tables.py
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RABBIT = ROOT / "RTK-Server/rtklua/Accepted/NPCs/rabbitTrap.lua"
TIGER = ROOT / "RTK-Server/rtklua/Accepted/NPCs/tigerTrap.lua"
OUT = ROOT / "game-data/AmbushBursts.csv"

# Lua function name  ->  our burst-table key. Only the fixed-id burst tables (sentries are param-driven).
FUNCS = {
    "spawnRabbitMob1": "rabbit_mob1",
    "spawnRabbitMob2": "rabbit_mob2",
    "spawnRabbitMob3": "rabbit_mob3",
    "spawnRabbitSentsMob1": "rabbit_sents1",
    "spawnRabbitSentsMob2": "rabbit_sents2",
    "spawnRabbitSentsMob3": "rabbit_sents3",
    "spawnTigerMob1": "tiger_mob1",
    "spawnTigerMob2": "tiger_mob2",
    "spawnTigerMob3": "tiger_mob3",
    "spawnTigerBigMob1": "tiger_big1",
    "spawnTigerBigMob2": "tiger_big2",
    "spawnTigerBigMob3": "tiger_big3",
}

FUNC_RE = re.compile(r"(\w+)\s*=\s*function\b")
ASSIGN_RE = re.compile(r"mobs\[(\d+)\]\[(\d+)\]\s*=\s*(\d+)")


def parse_file(path: str):
    """Yield (func_name, {variant_i: {slot_j: mob_id}}) for each `name = function` block in the file."""
    text = Path(path).read_text(encoding="utf-8", errors="replace")
    # Split on function boundaries but keep the name that opens each block.
    matches = list(FUNC_RE.finditer(text))
    for idx, m in enumerate(matches):
        name = m.group(1)
        start = m.end()
        end = matches[idx + 1].start() if idx + 1 < len(matches) else len(text)
        body = text[start:end]
        variants: dict[int, dict[int, int]] = {}
        for a in ASSIGN_RE.finditer(body):
            i, j, mob = int(a.group(1)), int(a.group(2)), int(a.group(3))
            variants.setdefault(i, {})[j] = mob
        yield name, variants


def collect():
    rows = []  # (table_key, variant_index, [mob_ids in slot order])
    seen = set()
    for path in (RABBIT, TIGER):
        if not path.exists():
            sys.exit(f"missing source: {path}")
        for name, variants in parse_file(str(path)):
            if name not in FUNCS:
                continue
            key = FUNCS[name]
            seen.add(key)
            for i in sorted(variants):
                slots = variants[i]
                ids = [slots[j] for j in sorted(slots)]
                if ids:
                    rows.append((key, i, ids))
    missing = set(FUNCS.values()) - seen
    if missing:
        sys.exit(f"ERROR: never found burst tables for {sorted(missing)} — Lua structure changed?")
    return rows


# Tiger sentries are spawned by spawnTigerSentries(mobId) — a single variant of 5x the PARAMETER id, passed
# per tier from mob_spawn.lua as RTK 804/805/806. Our DB exports those creatures as 900/901/902 (the Lua ids
# disagree with our export — resolve by ROLE, never copy Lua ids blind). Emitted here so every ambush burst
# table lives in one file; the per-map config points map 110/3110/4110 at these.
SYNTH = {
    "tiger_sents1": [900, 900, 900, 900, 900],
    "tiger_sents2": [901, 901, 901, 901, 901],
    "tiger_sents3": [902, 902, 902, 902, 902],
}

# NOT from RTK — kept in its own bucket so nobody reads it as extracted. RTK has no trap tile in Buya town at
# all (mobSpawnHandler.lua flat-spawns rats in the CAVES: `handleSpawn(npc, 370, {10, 49}, {12, 4}, ...)`).
# This one is reconstructed from live 7.x eyewitness (2026-08-22): "You have disturbed a nest of rats." and
# rats standing on every side. Four rats = one per burst slot, and World.AmbushBurstTile puts slots 0-3 east /
# west / north / south, which is the "surrounded" the sighting describes. Composition is a calibration, not a
# measurement — the count came from a screenshot, not a table.
OBSERVED = {
    "rat_nest": [10, 10, 10, 10],   # 10 = rat (Rat, 25 exp) — the creature in the sighting
}


def main():
    rows = collect()
    for key, ids in list(SYNTH.items()) + list(OBSERVED.items()):
        rows.append((key, 1, ids))
    lines = ["Table,Variant,MobIds"]
    for key, variant, ids in rows:
        lines.append(f"{key},{variant}," + ";".join(str(x) for x in ids))
    # newline="\n": the checked-in CSV is LF, and text mode would rewrite the WHOLE file as CRLF on Windows.
    OUT.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    # Console summary so a human can eyeball it against the Lua.
    print(f"wrote {OUT} ({len(rows)} variant rows)")
    cur = None
    for key, variant, ids in rows:
        if key != cur:
            print(f"  {key}:")
            cur = key
        print(f"    v{variant} = {ids}")


if __name__ == "__main__":
    main()
