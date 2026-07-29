"""Generate data/game-data/AreaSpawnsTrap.csv — the mob populations RTK spawns through its
trap-tile AMBUSH system (RTK-Server/rtklua/Accepted/NPCs/trap/mob_spawn.lua +
tigerTrap.lua + rabbitTrap.lua), which our handleSpawn-only extractor (extract_lua_spawns.py)
never sees.

Why a separate, curated file instead of extending extract_lua_spawns.py:
  * RTK's trap model is fundamentally different from handleSpawn's fixed per-map count. A hidden
    trap NPC is scattered across the map; stepping on it spawns a BURST of mobs around the player,
    refilling the room up to a soft cap (~50 tigers / ~64 rabbits per map) as you walk. There is
    no authored "N of mob X on map Y" number to parse — the population is emergent.
  * Our server has no ambush mechanic; it models every cave as a persistent roaming population
    (AreaSpawns). The base rabbit/spider mobs of these very caves are ALREADY persistent
    (handleSpawn rows in AreaSpawns.csv). So the faithful-within-our-architecture conversion is:
    give each trap-driven map a persistent roaming population of the same mobs the trap would
    spawn there. Net player-facing result is identical (huntable tigers/hares in the right rooms).
  * The per-map COUNTS below are therefore a deliberate calibration (documented per block), not a
    parse — chosen in line with the sibling handleSpawn caves (~10-14 base mobs/room). Mobs are
    referenced by their mobs.csv IDENTIFIER and resolved to our MobId at generation time,
    because the trap Lua's own mob IDs disagree with our DB export for the tiger sentries
    (Lua 804/805/806 vs our 900/901/902) — resolving by name is the only correct mapping.

Bosses (Mythic/Divine/Spirit Tiger, Tiger Warrior/Slasher/Avenger, Mythic/Divine/Spirit Hare,
Hare/Rabbit Witch, Rabbit Avenger) are RARE surprises in RTK: a 1-in-10 roll per trap-step, gated
by a single-instance "boss already alive?" check and a ~25-30 min cooldown. We reproduce that with
a RespawnSec column: a boss row is count 1 with a long respawn (the server starts it un-spawned and
materializes it at a random time while the map is being hunted, then holds it dead for RespawnSec+jitter
after each kill — see World.PopulateSpawns/NextRespawnTick). Base/sentry rows leave RespawnSec blank
(normal ~18s cadence).

Source line references (RTK-Server/rtklua/Accepted/NPCs/):
  trap/mob_spawn.lua      — the trap-tile dispatch: map ranges -> spawn helper / inline boss rolls
  tigerTrap.lua           — TigerSpawnNpc.spawnTigerMob{1,2,3}/spawnTigerSentries mob compositions
  rabbitTrap.lua          — RabbitSpawnNpc.spawnRabbitMob{1,2,3}/spawnRabbitSentsMob{1,2,3}
"""
import csv
from pathlib import Path

ROOT = Path(__file__).parent.parent
DATA = ROOT / "data" / "game-data"
OUT = DATA / "AreaSpawnsTrap.csv"

# Boss cooldown (seconds) -> our "rare" respawn. ~25 min, matching RTK's ~1500-1800s boss timer.
BOSS_RESPAWN = 1500

# Per-room roaming population size for a tier's base mobs, summed across that tier's base types.
# (~12/room, in line with the sibling rabbit handleSpawn rooms 201-206 which run 9-21 mobs.)
TIGER_BASE_PER_TYPE = 3     # x4 types t1 / x3 types t2,t3  -> ~12/room
SENTRY_COUNT = 5            # the guardroom trap spawns 5 sentries at once

# Rooms of each tiger tier: 100-109 (base rooms; 110 is the guardroom). Tier offsets 0/+3000/+4000.
TIGER_TIERS = [
    # (offset, [base mob identifiers], sentry identifier, revenge-map boss id, dark-pen boss id)
    (0,    ["restless_tiger", "dark_tiger", "giant_tiger", "golden_tiger"], "tiger_sentry",   "mythic_tiger", "tiger_warrior"),
    (3000, ["raging_tiger", "black_tiger", "huge_tiger"],                   "tiger_guardian", "divine_tiger", "tiger_slasher"),
    (4000, ["brazen", "knap", "crazy_claw"],                               "tiger_defender", "spirit_tiger", "tiger_avenger"),
]

# Rabbit boss-tier: the base hares are ALREADY persistent (handleSpawn rows in AreaSpawns.csv). Only the
# trap's rare boss rolls are missing. RTK rolls them on maps 203/205/208 (t1), 3203/3205/3208 (t2),
# 4203/4204/4205/4208 (t3); map *205 rolls BOTH bosses of the tier, so we anchor both there (one boss room
# per tier, mirroring the tiger cave's revenge-room bosses).
RABBIT_BOSS_TIERS = [
    # (boss-room map, [boss identifiers])
    (205,  ["mythic_hare", "hare_witch"]),
    (3205, ["divine_rabbit", "rabbit_witch"]),
    (4205, ["spirit_rabbit", "rabbit_avenger"]),
]

# Trapdoor spider: trap-only mob (id 102) on the whole Kugnae spider cave (maps 90-96). The base spiders
# (giant/radiant) are handleSpawn on 90/92 already; the trapdoor is the trap layer. ~5/room, huntable.
SPIDER_MAPS = list(range(90, 97))
TRAPDOOR_COUNT = 5


def load_ids():
    ids = {}
    with (DATA / "mobs.csv").open(encoding="utf-8") as f:
        for r in csv.DictReader(f):
            ids[r["Identifier"]] = int(r["MobId"])
    return ids


def main():
    ids = load_ids()
    rows = []   # (Map, MobId, Count, MinX, MinY, MaxX, MaxY, RespawnSec)

    def add(map_id, ident, count, respawn=""):
        if ident not in ids:
            raise SystemExit(f"identifier not in mobs.csv: {ident}")
        rows.append((map_id, ids[ident], count, 0, 0, 0, 0, respawn))

    # --- Tiger caves (all 3 tiers) ---
    for offset, base, sentry, revenge_boss, darkpen_boss in TIGER_TIERS:
        for room in range(100, 110):            # rooms 100-109 get the roaming base population
            for ident in base:
                add(room + offset, ident, TIGER_BASE_PER_TYPE)
        add(110 + offset, sentry, SENTRY_COUNT)  # guardroom = sentries
        add(105 + offset, revenge_boss, 1, BOSS_RESPAWN)   # Tiger's Revenge = mythic/divine/spirit boss
        add(109 + offset, darkpen_boss, 1, BOSS_RESPAWN)   # Dark Pen = warrior/slasher/avenger boss

    # --- Rabbit boss-tier (base hares already persistent elsewhere) ---
    for boss_map, bosses in RABBIT_BOSS_TIERS:
        for ident in bosses:
            add(boss_map, ident, 1, BOSS_RESPAWN)

    # --- Kugnae spider cave: trapdoor spiders ---
    for m in SPIDER_MAPS:
        add(m, "trapdoor_spider", TRAPDOOR_COUNT)

    with OUT.open("w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["Map", "MobId", "Count", "MinX", "MinY", "MaxX", "MaxY", "RespawnSec"])
        w.writerows(rows)

    base_rows = sum(1 for r in rows if not r[7])
    boss_rows = sum(1 for r in rows if r[7])
    print(f"wrote {len(rows)} trap-spawn rows -> {OUT}")
    print(f"  {base_rows} roaming (base/sentry/trapdoor) + {boss_rows} rare-boss rows")


if __name__ == "__main__":
    main()
