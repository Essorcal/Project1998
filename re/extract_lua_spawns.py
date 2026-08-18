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
area-spawn table our server can materialize: one row per (call, mobId) with a count and an
optional bounding box. Box (0,0,0,0) means "anywhere walkable on the map".

`timer` and the CALL GROUPING are both carried through, because they are the mechanism, not
decoration. RTK keys its spawn clock off `spawnTable[map][mobs[1]]` — ONE stamp per call — and
refills every mob in that call together, in one batch, once `now > stamp + timer`. So the twelve
Wilderness mobs share a single 300s clock; they do not each respawn on their own timer as they die.
Group is that call's ordinal within its map, so the server can reassemble the batch.

Timers reach a call one of two ways: a bare integer literal (792 of 844 calls), or an entry in
one of the file's three rate tables (52 calls) — `spawnRates["sheep"]` or `spawnRates["ox_sentry"][2]`.
We resolve against `_defaultRates`. RTK's own config.lua ships rebalancedSpawnsEnabled = true, so
production RTK ran `_rebalancedRates`, which differs from default in exactly three entries (sheep
240->120, sintu 6000->1500, golden_lobster 3600->900). Neither table is evidence of classic pacing —
both are fork tuning — so we take the pre-rebalance one and keep the choice to one constant below.

Calls whose map/mob/count args are not integer literals (loop-computed `i+1`, etc.) are
skipped and reported; those are a handful of programmatic dungeons we can add by hand later.

Rows are NOT merged across calls. RTK counts a mob's population MAP-WIDE (`getObjectsInMap`) before
topping up, so two calls that name the same mob on the same map do not stack — the second sees the
first's mobs and adds nothing, making the effective cap the MAX of the two, never the sum. Summing
them (what this tool used to do) overspawned three spots that RTK ships with copy-paste duplicates:
map 167 `{13, 13, 14}` and map 399's whole call pasted twice. Keeping one row per call and letting
the server count map-wide reproduces RTK exactly and is what keeps a room from overfilling.

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
# Which of mobSpawnHandler.lua's three rate tables to resolve spawnRates[...] against. See the
# module docstring: "_defaultRates" is the pre-rebalance one; "_rebalancedRates" is what RTK
# production actually ran; "_fastRates" is a blanket ~60s test knob, not a balance pass.
RATE_TABLE = "_defaultRates"
# Timer for a row we carry over from the committed CSV that no handleSpawn call produces (the
# hand-added tutorial rows). 300s is the baseline literal at 445 of 844 call sites.
CARRYOVER_TIMER = 300

# (map, mobId) pairs RTK spawns that we deliberately do NOT. Kept here rather than by deleting rows from
# the CSV, because the CSV is generated: a --rewrite-base would silently put them back, which is exactly
# how both of these reappeared after having been absent from the committed file for weeks.
#   (4711, 2) squirrels in Welcome, the tutorial's first room — a curated room. The rabbits/squirrels
#             split across the early tutorial maps (4712 rabbits, 4713 squirrels) is hand-authored and
#             not something the extractor gets a vote on.
#   (41, 553) thirsty_ogre on Mythic Nexus.
# Both were re-added by the 2026-08-13 extraction and pulled straight back out on the user's call.
EXCLUDED_ROWS = {(4711, 2), (41, 553)}
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


def parse_rates(src, table=RATE_TABLE):
    """Parse `local _defaultRates = { ["key"] = 90, ["other"] = {300, 240, 330} }` to a dict of
    int or list-of-int. Returns {} if the table isn't in this file (the crafting spawners have no
    rate tables at all — every timer there is a literal)."""
    m = re.search(r"local\s+" + re.escape(table) + r"\s*=\s*\{", src)
    if not m:
        return {}
    body, _ = balanced_braces(src, m.end() - 1)
    rates = {}
    for key, val in re.findall(r'\["([^"]+)"\]\s*=\s*(\{[^}]*\}|\d+)', body):
        rates[key] = int(val) if val.isdigit() else int_list(val)
    return rates


def balanced_braces(text, open_idx):
    """Given the index of '{' return the substring inside the matching '}'."""
    depth = 0
    for i in range(open_idx, len(text)):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                return text[open_idx + 1 : i], i
    return None, None


def resolve_timer(tok, rates):
    """The `timer` argument -> seconds, or None if we can't read it (reported, never guessed).
    Three shapes: `300`, `spawnRates["sheep"]`, `spawnRates["ox_sentry"][2]` (Lua is 1-based)."""
    tok = tok.strip()
    if re.fullmatch(r"\d+", tok):
        return int(tok)
    m = re.fullmatch(r'spawnRates\[\s*"([^"]+)"\s*\](?:\[\s*(\d+)\s*\])?', tok)
    if not m:
        return None
    val = rates.get(m.group(1))
    if val is None:
        return None
    if m.group(2):                       # indexed: the per-cave-level variants, {level1, level2, level3}
        idx = int(m.group(2)) - 1
        return val[idx] if isinstance(val, list) and 0 <= idx < len(val) else None
    return val if isinstance(val, int) else None


def strip_comments(src):
    """Drop Lua line comments. miningSpawnHandler.lua opens with a COMMENTED-OUT handleSpawn for squirrels,
    and reading it as live put 100 squirrels in the mining field — a scanner that ignores `--` is not a
    scanner, it is a superset."""
    return re.sub(r"--(?!\[\[).*", "", src)


def scan(src):
    """Every literal handleSpawn(...) call in one spawner script -> (rows, skipped, skipped_maps).

    A row is (map, mob, count, minX, minY, maxX, maxY, timer, group); `group` is the call's ordinal
    within its map, so every mob the call names shares one batch clock (see the module docstring)."""
    src = strip_comments(src)
    rates = parse_rates(src)
    groups = {}          # map -> next unused group ordinal on that map
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
        # RTK's top-up loop runs `for z = 1, #mobs`, so a counts list LONGER than the mobs list is
        # harmless there -- the surplus is never indexed. Three calls ship that way (maps 172/3172/4172,
        # `{20}` with `{13, 13}`); rejecting them on length left those maps empty of the 13 mobs RTK
        # does spawn. A SHORTER counts list is a real error (Lua would index nil), so it still skips.
        if counts is not None and mobs is not None and len(counts) > len(mobs):
            counts = counts[: len(mobs)]
        if mobs is None or counts is None or len(mobs) != len(counts):
            skipped += 1
            skipped_maps.add(str(mp))
            continue
        timer = resolve_timer(parts[4], rates) if len(parts) >= 5 else None
        if timer is None or timer <= 0:
            # An unreadable timer is the whole clock for this call — never guess one.
            skipped += 1
            skipped_maps.add(str(mp))
            continue
        box = (0, 0, 0, 0)
        if len(parts) >= 9 and all(re.fullmatch(r"\d+", parts[i]) for i in range(5, 9)):
            box = tuple(int(parts[i]) for i in range(5, 9))
        group = groups.get(mp, 0)
        groups[mp] = group + 1
        for mob, cnt in zip(mobs, counts):
            if cnt <= 0 or (mp, mob) in EXCLUDED_ROWS:
                continue
            rows.append((mp, mob, cnt, *box, timer, group))
    return rows, skipped, skipped_maps


HEADER = ["Map", "MobId", "Count", "MinX", "MinY", "MaxX", "MaxY", "Timer", "Group"]


def carry_over(rows, out):
    """Rows in the committed CSV that this scan does NOT produce, re-emitted as their own group.

    The committed AreaSpawns.csv has diverged from a clean extraction — it carries hand-added newbie
    rows (maps 4712/4713) that no handleSpawn call produces — so a regeneration used to silently DELETE
    tutorial spawns. Rather than making that a hazard the caller has to remember, keep them: any
    (map, mob) the scan didn't emit is preserved verbatim, given the next free group ordinal on its
    map and, if the old file had no Timer column, CARRYOVER_TIMER. Returns (rows, carried)."""
    if not out.exists():
        return rows, []
    have = {(r[0], r[1]) for r in rows}
    next_group = {}
    for r in rows:
        next_group[r[0]] = max(next_group.get(r[0], 0), r[8] + 1)
    carried = []
    with out.open(newline="", encoding="utf-8") as f:
        for old in csv.DictReader(f):
            try:
                mp, mob, cnt = int(old["Map"]), int(old["MobId"]), int(old["Count"])
            except (KeyError, TypeError, ValueError):
                continue
            # `have` alone isn't enough to skip an excluded row: the scan deliberately didn't emit it, so a
            # copy sitting in the current CSV would come straight back through the carry-over path.
            if (mp, mob) in have or (mp, mob) in EXCLUDED_ROWS:
                continue
            box = tuple(int(old.get(c) or 0) for c in ("MinX", "MinY", "MaxX", "MaxY"))
            timer = int(old.get("Timer") or CARRYOVER_TIMER)
            group = next_group.get(mp, 0)
            next_group[mp] = group + 1
            carried.append((mp, mob, cnt, *box, timer, group))
    return rows + carried, carried


def write(rows, out):
    """Write the CSV, deduping only WITHIN a call.

    Rows are deliberately not merged across calls — see the module docstring: RTK counts map-wide, so
    two calls naming one mob cap at the max rather than the sum, and the server reproduces that by
    counting map-wide too. The only merge left is a mob listed twice in the SAME call (RTK map 167's
    `{13, 13, 14}`), where RTK's second loop pass finds the cap already met and adds nothing — max,
    not sum, again."""
    merged = {}
    for mp, mob, cnt, x0, y0, x1, y1, timer, group in rows:
        k = (mp, group, mob, x0, y0, x1, y1, timer)
        merged[k] = max(merged.get(k, 0), cnt)
    out_rows = sorted(
        (mp, mob, cnt, x0, y0, x1, y1, timer, group)
        for (mp, group, mob, x0, y0, x1, y1, timer), cnt in merged.items()
    )

    out.parent.mkdir(parents=True, exist_ok=True)
    with out.open("w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(HEADER)
        w.writerows(out_rows)
    return out_rows


def main():
    # AreaSpawns.csv is still gated behind --rewrite-base so a regeneration is always a deliberate,
    # diffed act -- but it is no longer destructive: carry_over re-emits any hand-added row the scan
    # doesn't produce (the 4712/4713 tutorial spawns) instead of dropping it.
    rows, skipped, skipped_maps = scan(LUA.read_text(encoding="latin1"))
    if "--rewrite-base" in sys.argv:
        rows, carried = carry_over(rows, OUT)
        out_rows = write(rows, OUT)
        print(f"wrote {len(out_rows)} area-spawn rows -> {OUT}")
        print(f"  {len(sorted({r[0] for r in out_rows}))} maps, {sum(r[2] for r in out_rows)} total mobs")
        print(f"  {len({(r[0], r[8]) for r in out_rows})} spawn groups (one batch clock each)")
        print(f"  timers {min(r[7] for r in out_rows)}..{max(r[7] for r in out_rows)}s, "
              f"resolved against {RATE_TABLE}")
        print(f"  carried over {len(carried)} hand-added row(s) no handleSpawn produces: "
              f"{sorted({r[0] for r in carried})}")
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
