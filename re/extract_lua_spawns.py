#!/usr/bin/env python3
"""Extract RTK dynamic mob spawns from rtklua/Accepted/NPCs/mobSpawnHandler.lua.

RTK spawns town/dungeon mobs two ways:
  * a small static `Spawns0` SQL table (already exported to game-data/Spawns0.csv) —
    covers only ~19 maps (Kugnae, Buya, a few specials);
  * a big Lua "spawner NPC" that calls, per hunting map:
        handleSpawn(npc, map, {mobIds...}, {counts...}, timer [, minX, minY, maxX, maxY])
    This is where EVERY cave/dungeon (the Mythic zodiac caves, wilderness, etc.) gets its
    mobs. None of it is in Spawns0, so our server placed nothing there.

This tool parses those handleSpawn(...) calls (including multi-line ones) into an
area-spawn table our server can materialize: one row per (map, mobId) with a count and an
optional bounding box. Box (0,0,0,0) means "anywhere walkable on the map". `timer` is
ignored — the server uses its own respawn cadence.

Calls whose map/mob/count args are not integer literals (loop-computed `i+1`, etc.) are
skipped and reported; those are a handful of programmatic dungeons we can add by hand later.

RTK has THREE spawner NPCs with this identical call shape, not one — the other two place the crafting
nodes, which are ordinary mobs to the engine (see game-data/HarvestNodes.csv):
  * miningSpawnHandler.lua      — ore veins
  * woodcuttingSpawnHandler.lua — ginko trees
Reading only mobSpawnHandler.lua is why mining and woodcutting were switched on in CraftingToggles with
nothing in the world to gather from. Those two go to their own AreaSpawnsCrafting.csv, concatenated at load
like AreaSpawnsTrap.csv, so a re-run of the main extractor can't drop them and the counts stay easy to tune.
"""
import csv
import re
import sys
from pathlib import Path

NPCS = Path(__file__).parents[1] / "RTK-Server" / "rtklua" / "Accepted" / "NPCs"
if not NPCS.exists():
    NPCS = Path(__file__).parent / "RTK-Server" / "rtklua" / "Accepted" / "NPCs"
DATA = Path(__file__).parents[1] / "game-data"

LUA = NPCS / "mobSpawnHandler.lua"
OUT = DATA / "AreaSpawns.csv"
# The crafting-node spawners, written separately (see the module docstring).
CRAFT_LUA = [NPCS / "miningSpawnHandler.lua", NPCS / "woodcuttingSpawnHandler.lua"]
CRAFT_OUT = DATA / "AreaSpawnsCrafting.csv"


def balanced_args(text, open_idx):
    """Given index of '(' return the substring inside the matching ')'."""
    depth = 0
    for i in range(open_idx, len(text)):
        c = text[i]
        if c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
            if depth == 0:
                return text[open_idx + 1 : i], i
    return None, None


def split_top(args):
    """Split a Lua arg list on top-level commas (ignoring commas inside {})."""
    out, depth, cur = [], 0, ""
    for c in args:
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
        if c == "," and depth == 0:
            out.append(cur.strip())
            cur = ""
        else:
            cur += c
    if cur.strip():
        out.append(cur.strip())
    return out


def int_list(tok):
    """Parse a `{1, 2, 3}` token to a list of ints, or None if any element isn't an int."""
    inner = tok.strip().lstrip("{").rstrip("}")
    vals = []
    for p in inner.split(","):
        p = p.strip()
        if not p:
            continue
        if not re.fullmatch(r"\d+", p):
            return None
        vals.append(int(p))
    return vals


def strip_comments(src):
    """Drop Lua line comments. miningSpawnHandler.lua opens with a COMMENTED-OUT handleSpawn for squirrels,
    and reading it as live put 100 squirrels in the mining field — a scanner that ignores `--` is not a
    scanner, it is a superset."""
    return re.sub(r"--(?!\[\[).*", "", src)


def scan(src):
    """Every literal handleSpawn(...) call in one spawner script -> (rows, skipped, skipped_maps)."""
    src = strip_comments(src)
    rows = []
    skipped = 0
    skipped_maps = set()
    for m in re.finditer(r"handleSpawn\s*\(", src):
        args, _ = balanced_args(src, m.end() - 1)
        if args is None:
            continue
        parts = split_top(args)
        # parts[0] == 'npc'; [1]=map [2]={mobs} [3]={counts} [4]=timer [5..8]=box
        if len(parts) < 4:
            continue
        if not re.fullmatch(r"\d+", parts[1]):
            skipped += 1
            skipped_maps.add(parts[1])
            continue
        mp = int(parts[1])
        mobs = int_list(parts[2])
        counts = int_list(parts[3])
        if mobs is None or counts is None or len(mobs) != len(counts):
            skipped += 1
            skipped_maps.add(str(mp))
            continue
        box = (0, 0, 0, 0)
        if len(parts) >= 9 and all(re.fullmatch(r"\d+", parts[i]) for i in range(5, 9)):
            box = tuple(int(parts[i]) for i in range(5, 9))
        for mob, cnt in zip(mobs, counts):
            if cnt <= 0:
                continue
            rows.append((mp, mob, cnt, *box))
    return rows, skipped, skipped_maps


def write(rows, out):
    """Merge duplicate (map, mob, box) rows by summing counts, then write the CSV."""
    merged = {}
    for mp, mob, cnt, x0, y0, x1, y1 in rows:
        k = (mp, mob, x0, y0, x1, y1)
        merged[k] = merged.get(k, 0) + cnt
    out_rows = sorted((mp, mob, cnt, x0, y0, x1, y1) for (mp, mob, x0, y0, x1, y1), cnt in merged.items())

    out.parent.mkdir(parents=True, exist_ok=True)
    with out.open("w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["Map", "MobId", "Count", "MinX", "MinY", "MaxX", "MaxY"])
        w.writerows(out_rows)
    return out_rows


def main():
    # AreaSpawns.csv is NOT rewritten by default. The committed file has diverged from a clean extraction --
    # it carries hand-added newbie-area rows (maps 4712/4713) that no handleSpawn call produces -- so a
    # regeneration silently DELETES tutorial spawns. Pass --rewrite-base only when you mean to, and diff it.
    rows, skipped, skipped_maps = scan(LUA.read_text(encoding="latin1"))
    if "--rewrite-base" in sys.argv:
        out_rows = write(rows, OUT)
        print(f"wrote {len(out_rows)} area-spawn rows -> {OUT}")
        print(f"  {len(sorted({r[0] for r in out_rows}))} maps, {sum(r[2] for r in out_rows)} total mobs")
        print(f"  skipped {skipped} calls with non-literal args (computed maps: {sorted(skipped_maps)[:20]})")
    else:
        print(f"parsed {len(rows)} base area-spawn rows (not written — pass --rewrite-base; see the note in main)")

    craft_rows = []
    for lua in CRAFT_LUA:
        if not lua.exists():
            print(f"  !! missing {lua.name} — crafting nodes not extracted")
            continue
        r, _, _ = scan(lua.read_text(encoding="latin1"))
        craft_rows += r
    craft_out = write(craft_rows, CRAFT_OUT)
    print(f"wrote {len(craft_out)} crafting-node rows -> {CRAFT_OUT}")
    print(f"  {len(sorted({r[0] for r in craft_out}))} maps, {sum(r[2] for r in craft_out)} total nodes")


if __name__ == "__main__":
    sys.exit(main())
