#!/usr/bin/env python3
"""Merge RTK's per-mob `Mobs.MobSpawnTime` into game-data/mobs.csv as a `SpawnTime` column.

This is the STATIC spawn clock. RTK runs two unrelated spawn systems and this is the one in the C
engine (mob.c): every row of the `Spawns0` table is a persistent mob object that revives at its own
tile once `last_death + Mobs.MobSpawnTime` seconds have passed. Per mob TYPE, not per point, and
nothing to do with the Lua spawner NPC that drives the hunting maps (see extract_lua_spawns.py).

We had no such column, so every static spawn shared one hard-coded ~18s cadence. That happens to
match the modal RTK value, but the table spreads 12/18/24/30/60/360 (SQL default 180) -- so the
Mythic elites on 360 were coming back twenty times too fast.

MERGE, not re-extract. mobs.csv has been hand-repaired (there are .bak and .pre-vita-restore copies
next to it); regenerating the whole file to gain one column would throw those fixes away. This adds
the column and touches nothing else -- every existing row keeps its exact values and order, and a mob
the dump doesn't mention keeps a blank cell rather than a guessed one (the server falls back).

Usage:  python re/merge_mob_spawn_time.py [--write]
"""
import csv
import io
import re
import sys
from pathlib import Path

ROOT = Path(__file__).parents[1]
DUMP = ROOT / "RTK-Server" / "database" / "2020-09-02-21-55-01_RTK.sql.bak"
MOBS = ROOT / "game-data" / "mobs.csv"
COLUMN = "SpawnTime"


def dump_spawn_times(sql):
    """{MobIdentifier: MobSpawnTime} from the dump's `Mobs` INSERT."""
    create = re.search(r"CREATE TABLE `Mobs` \((.*?)\n\) ENGINE", sql, re.S)
    if not create:
        raise SystemExit("no CREATE TABLE `Mobs` in the dump")
    cols = re.findall(r"^\s*`(\w+)`", create.group(1), re.M)
    i_id, i_time = cols.index("MobIdentifier"), cols.index("MobSpawnTime")

    insert = re.search(r"INSERT INTO `Mobs` VALUES (.*?);\n", sql, re.S)
    if not insert:
        raise SystemExit("no INSERT INTO `Mobs` in the dump")

    times = {}
    for body in split_rows(insert.group(1)):
        fields = next(csv.reader(io.StringIO(body), quotechar="'", escapechar="\\", skipinitialspace=True))
        if len(fields) != len(cols):
            raise SystemExit(f"row parsed to {len(fields)} fields, expected {len(cols)}: {body[:80]}")
        try:
            times[fields[i_id]] = int(fields[i_time])
        except ValueError:
            continue
    return times


def split_rows(body):
    """`(1,'a',2),(3,'b',4)` -> ['1,\\'a\\',2', '3,\\'b\\',4'], scanned rather than matched.

    Written by hand on purpose. The obvious regex for this -- alternating "unquoted char" and "quoted
    run" inside parens -- silently glued hundreds of mob rows into two giant blobs on this dump, and
    because the merge skipped over-wide rows it looked like a clean parse of a smaller table. A scanner
    that tracks quote state has no such failure mode, and the caller now HARD FAILS on an unexpected
    field count rather than dropping the row."""
    rows, depth, quoted, cur = [], 0, False, []
    i = 0
    while i < len(body):
        c = body[i]
        if quoted:
            cur.append(c)
            if c == "\\":                      # \' and \\ inside a MySQL string literal
                i += 1
                if i < len(body):
                    cur.append(body[i])
            elif c == "'":
                quoted = False
        elif c == "'":
            quoted = True
            cur.append(c)
        elif c == "(":
            depth += 1
            if depth == 1:
                cur = []                       # start of a row; don't keep the paren itself
            else:
                cur.append(c)
        elif c == ")":
            depth -= 1
            if depth == 0:
                rows.append("".join(cur))
            else:
                cur.append(c)
        elif depth:
            cur.append(c)
        i += 1
    return rows


def main():
    times = dump_spawn_times(DUMP.read_text(encoding="utf8", errors="replace"))
    print(f"read {len(times)} MobSpawnTime values from {DUMP.name}")

    with MOBS.open(newline="", encoding="utf-8-sig") as f:
        reader = csv.DictReader(f)
        header = list(reader.fieldnames or [])
        rows = list(reader)
    if COLUMN not in header:
        header.append(COLUMN)

    hit = miss = 0
    missing = []
    for r in rows:
        t = times.get((r.get("Identifier") or "").strip())
        if t is None:
            miss += 1
            missing.append(r.get("Identifier"))
            r.setdefault(COLUMN, "")
        else:
            r[COLUMN] = str(t)
            hit += 1
    print(f"  matched {hit} mobs by Identifier, {miss} unmatched (left blank -> server default)")
    if missing:
        print(f"  unmatched sample: {[m for m in missing if m][:12]}")

    dist = {}
    for r in rows:
        dist[r.get(COLUMN) or "(blank)"] = dist.get(r.get(COLUMN) or "(blank)", 0) + 1
    print("  distribution: " + ", ".join(f"{k}s x{v}" for k, v in sorted(dist.items(), key=lambda kv: -kv[1])))

    if "--write" not in sys.argv:
        print("dry run — pass --write to update mobs.csv")
        return 0

    with MOBS.open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=header)
        w.writeheader()
        w.writerows(rows)
    print(f"wrote {len(rows)} rows -> {MOBS}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
