"""Regenerate game-data/MobSpawnRules.csv from what RTK's 99 `on_spawn` hooks actually say, without undoing
what has been decided by hand in the file since.

Nearly every on_spawn in the tree is boss PLACEMENT, not behaviour: 20 are a bare `mob:warp(map, x, y)` and
the rest are `local rand = math.random(1, N); if rand == 1 then mob:warp(A) elseif ... else mob:warp(C) end`
-- "put me in one of my rooms, at random". That is a table, so it becomes one here and the 99 scripts go away.

The other two things a spawn does in RTK, folded into the same file:

* `AI/mob_on_spawn.lua` is the DEFAULT on_spawn for every creature that doesn't override it, and all it does
  is jitter max HP by +/- random((minDam + maxDam) * 2) so no two spawns are identical. That is a global
  rule, emitted as the single `*` row.
* A handful of mobs cap their own population -- `strange_thing` counts every other strange_thing across two
  maps and vanishes if one is already out. That is the MaxAlive column.

WHAT IT WRITES, AND WHAT IT DOES NOT
------------------------------------
RTK supplies five columns: MobKey, Rooms (map:x:y|...), MaxAlive, CapMaps (|-separated), HpJitter. The checked-in
file has more, and has been edited by hand since the first run, so the extractor's five columns are NOT the file:

* the columns RTK cannot supply (today FleeBelowPct, SpawnChance, DeathCooldownSec: every column of the file that
  is not one of the five), whose values were set by hand;
* every row RTK's hooks do not yield (today nine_tailed_fox, ogre_citelam, ogre_maletic), kept as the file has
  it: such a row is the file's own, and one RTK stopped yielding is removed by editing the file, not by this;
* the `#` comment lines, which sit above the row they explain;
* rooms the file deliberately holds against RTK's warp: ROOM_DEVIATIONS below. #367 moved spirit_dragon's middle
  room from RTK's 4258:7:13 (open ground walled in on all four sides on the 4.95 map) to 4258:9:13, on the project owner's
  word (2026-10-22). Written bare, from RTK alone, this script would put the boss back in the sealed niche.

So the result is RTK's five columns with everything above carried over from the file. Nothing is carried that
the file does not already say: a new hand decision is made in the file, and a new room held against RTK's is
also entered in ROOM_DEVIATIONS, with its reason, or the next run reports it as a difference.

HOW TO RUN IT (from the repo root; `-I` keeps the script's own directory off sys.path)
-------------------------------------------------------------------------------------
    python -I re/extract_mob_spawn_rules.py                 CHECK (the default). Writes nothing. Prints what was
                                                            carried over and each row that differs from the file,
                                                            then a diff. Exit 0: the file is exactly what this
                                                            would write. Exit 1: it differs.
    python -I re/extract_mob_spawn_rules.py --out NEW.csv   The same check, and the result is also written to
                                                            NEW.csv, a separate file (never the checked-in one).
    python -I re/extract_mob_spawn_rules.py --overwrite     Replace the checked-in file with the result. The
                                                            only way to change it. The differences are printed
                                                            first, and the carried-over values still apply.

    --target FILE   the file compared against and, with --overwrite, replaced (default: game-data/MobSpawnRules.csv)
    --rtk DIR       RTK-Server's root (default: $P1998_RTK, else RTK-Server in the repo root, else beside it,
                    the order re/_paths.py uses)

Exit 2: refused or could not run: RTK-Server not found, the target lacks one of the five columns, a duplicate
MobKey, a ROOM_DEVIATIONS entry that matches nothing in RTK now (RTK moved the room, or dropped the row: decide
again before anything is written), or --overwrite with --out.

Output is LF, as the file is in the repository (.gitattributes).
"""
import argparse
import csv
import difflib
import glob
import io
import os
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TARGET = ROOT / "game-data" / "MobSpawnRules.csv"

# The columns RTK's hooks supply. Every other column of the file is hand-kept and carried over untouched.
EXTRACTED = ["MobKey", "Rooms", "MaxAlive", "CapMaps", "HpJitter"]

# Rooms the file holds on purpose against RTK's warp: (MobKey, RTK's room) -> the room the file holds instead.
# Each needs the reason beside its row in the CSV (a `#` comment line, which is carried over with the row).
ROOM_DEVIATIONS = {
    # #367: RTK's mythic_dragon.lua spirit_dragon on_spawn warps to 4258:7:13, which on the 4.95 map (Jeweled
    # Dragon 3, 18x36) is open ground walled in on all four sides. 4258:9:13 is the corridor two tiles east.
    # The project owner's word, 2026-10-22. Pinned by TheSpiritDragonsJeweledDragonRoomIsOnTheCorridorAPlayerWalks.
    ("spirit_dragon", "4258:7:13"): "4258:9:13",
}

DIFF_LINES = 80   # a longer diff is cut here; the per-row lines above it are complete


def rtk_root(arg):
    """--rtk, else $P1998_RTK, else RTK-Server in the repo root, else beside it: re/_paths.py's order, repeated
    here because `python -I` cannot import it (the script's directory is not on sys.path)."""
    if arg:
        return Path(arg)
    env = os.environ.get("P1998_RTK")
    if env:
        return Path(env)
    for candidate in (ROOT / "RTK-Server", ROOT.parent / "RTK-Server"):
        if candidate.exists():
            return candidate
    return ROOT / "RTK-Server"


def valid_mobs():
    with (ROOT / "game-data" / "mobs.csv").open(encoding="utf-8", errors="replace") as f:
        return {r["Identifier"] for r in csv.DictReader(f)}


def tables(accepted):
    """Every `name = { ... }` mob table body across the AI + Mobs trees."""
    for pat in ("AI/**/*.lua", "Mobs/**/*.lua"):
        for f in glob.glob(str(accepted / pat), recursive=True):
            text = Path(f).read_text(encoding="utf-8", errors="replace")
            for m in re.finditer(r"^(\w+)\s*=\s*\{", text, re.M):
                end = text.find("\n}", m.end())
                yield m.group(1), text[m.end(): end if end > 0 else len(text)]


def extract(accepted, mobs):
    """RTK's five columns, as strings: {MobKey: row}, the `*` default row first, the rest sorted by key."""
    rows = {}

    for name, body in tables(accepted):
        if name not in mobs:
            continue
        hook = re.search(r"\bon_spawn\s*=\s*function\s*\([^)]*\)(.*?)\n\tend", body, re.S)
        if not hook:
            continue
        inner = hook.group(1)

        rooms = ["%s:%s:%s" % w for w in re.findall(r"mob:warp\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)", inner)]

        # Population cap: a body that counts its own kind across some maps and vanishes past a limit.
        cap, cap_maps = 0, []
        if "vanish" in inner:
            cap_maps = re.findall(r"getObjectsInMap\(\s*(\d+)", inner)
            m = re.search(r"mobCount\s*>\s*(\d+)", inner)
            if m:
                cap = int(m.group(1))   # `> N` means N may be alive at once

        if rooms or cap:
            rows[name] = dict(MobKey=name, Rooms="|".join(rooms), MaxAlive=str(cap),
                              CapMaps="|".join(cap_maps), HpJitter="")

    # The global default (AI/mob_on_spawn.lua): every creature's max HP is jittered on spawn.
    out = {"*": dict(MobKey="*", Rooms="", MaxAlive="0", CapMaps="", HpJitter="1")}
    for k in sorted(rows):
        out[k] = rows[k]
    return out


class Refused(Exception):
    """The script cannot give a trustworthy answer: exit 2, nothing written."""


def apply_deviations(rows):
    """Put ROOM_DEVIATIONS' rooms in place of RTK's. Returns the (key, rtk room, held room) applied. An entry
    that matches nothing is stale, and refuses: RTK's warp moved, or the row went, and a person decides."""
    applied, stale = [], []
    for (key, rtk_room), held in ROOM_DEVIATIONS.items():
        row = rows.get(key)
        rooms = row["Rooms"].split("|") if row and row["Rooms"] else []
        if rtk_room not in rooms:
            stale.append(f"{key} {rtk_room} -> {held}")
            continue
        rooms[rooms.index(rtk_room)] = held
        row["Rooms"] = "|".join(rooms)
        applied.append((key, rtk_room, held))
    if stale:
        raise Refused("ROOM_DEVIATIONS matches nothing in RTK now (" + "; ".join(stale) + "): RTK moved the room "
                      "or dropped the row. Decide the room again and update the table before anything is written.")
    return applied


def read_exact(path):
    """The file's text as it is on disk: no newline translation, so a CRLF file is seen as one."""
    return path.read_bytes().decode("utf-8")


def write_exact(path, text):
    path.write_bytes(text.encode("utf-8"))


def skippable(line):
    """The loader's own test (Shared.Core/Csv.cs IsSkippable): blank, or the first field starts with '#'."""
    if not line.strip():
        return True
    first = next(csv.reader([line.lstrip()]), [""])
    return (first[0] if first else "").startswith("#")


class Target:
    """The checked-in file: its header, its rows in order, and the skippable lines (comments, blanks) kept with the
    row below them. `before[key]` is the lines directly above that row; `preamble` those above the header and
    `trailing` those below the last row."""

    def __init__(self, text):
        self.preamble, self.rows, self.before, self.trailing = [], [], {}, []
        self.header = None
        pending = []
        lines = text.split("\n")
        if lines and lines[-1] == "":
            lines.pop()
        for raw in lines:
            line = raw[:-1] if raw.endswith("\r") else raw
            if skippable(line):
                pending.append(line)
                continue
            fields = next(csv.reader([line]))
            if self.header is None:
                missing = [c for c in EXTRACTED if c not in fields]
                if missing:
                    raise Refused(f"the target lacks the column(s) {', '.join(missing)}: it is not a file this script reproduces")
                self.header = fields
                self.preamble, pending = pending, []
                continue
            if len(fields) > len(self.header):
                raise Refused(f"row '{fields[0]}' has {len(fields)} fields, the header {len(self.header)}")
            row = dict(zip(self.header, fields + [""] * (len(self.header) - len(fields))))
            key = row["MobKey"]
            if key in self.before:
                raise Refused(f"MobKey '{key}' appears twice")
            self.before[key] = pending
            pending = []
            self.rows.append(row)
        self.trailing = pending
        if self.header is None:
            raise Refused("the target has no header line")


def build(extracted, target):
    """The result: RTK's five columns, plus everything carried over from the target. Returns
    (header, lines, carried) where lines is the file's text lines and carried names what came from the target."""
    header = target.header if target else list(EXTRACTED)
    kept = [c for c in header if c not in EXTRACTED]
    by_key = {r["MobKey"]: r for r in target.rows} if target else {}
    carried = {"columns": {c: [] for c in kept}, "rows": [],
               "comments": sum(1 for c in (target.preamble + target.trailing if target else []) if c.strip())}

    out_rows = []   # (key, row, comment lines above it)
    for key, rtk in extracted.items():
        row = {c: "" for c in header}
        row.update(rtk)
        old = by_key.get(key)
        if old:
            for c in kept:
                row[c] = old[c]
                if old[c] != "":
                    carried["columns"][c].append(key)
        out_rows.append((key, row, target.before[key] if old else []))

    # Every row RTK's hooks do not yield is the file's own, whatever it holds: a row made by hand with only a
    # room is as much a decision as one with a hand-kept column, and dropping it would lose it silently. A row
    # RTK stopped yielding is removed by editing the file, never by this script.
    if target:
        for old in target.rows:
            key = old["MobKey"]
            if key in extracted:
                continue
            carried["rows"].append(key)
            for c in kept:
                if old[c] != "":
                    carried["columns"][c].append(key)
            out_rows.append((key, dict(old), target.before[key]))

    lines = list(target.preamble) if target else []
    buf = io.StringIO()
    w = csv.writer(buf, lineterminator="\n")
    w.writerow(header)
    for key, row, comments in out_rows:
        carried["comments"] += sum(1 for c in comments if c.strip())
        buf.write("\n".join(comments) + "\n" if comments else "")
        w.writerow([row[c] for c in header])
    text = ("\n".join(lines) + "\n" if lines else "") + buf.getvalue()
    if target and target.trailing:
        text += "\n".join(target.trailing) + "\n"
    return header, text, out_rows, carried


def differences(header, target, out_rows):
    """Each row that differs between the target and the result: ('added'|'changed', key, detail). A row of the
    target is never dropped (build carries every row RTK does not yield), so there is no third kind."""
    if target is None:
        return [("added", key, "") for key, _, _ in out_rows]
    old = {r["MobKey"]: r for r in target.rows}
    new = {key: row for key, row, _ in out_rows}
    diffs = []
    for key in new:
        if key not in old:
            diffs.append(("added", key, "the file has no such row; this would write: " + ",".join(new[key][c] for c in header)))
            continue
        cells = [f"{c}: file '{old[key][c]}', would write '{new[key][c]}'" for c in header if old[key][c] != new[key][c]]
        if cells:
            diffs.append(("changed", key, "; ".join(cells)))
    return diffs


def main(argv=None):
    ap = argparse.ArgumentParser(description="Check, or regenerate, game-data/MobSpawnRules.csv from RTK's on_spawn hooks.")
    ap.add_argument("--rtk", help="RTK-Server's root (default: $P1998_RTK, else RTK-Server in the repo root, else beside it)")
    ap.add_argument("--target", type=Path, default=TARGET, help="the file compared against and, with --overwrite, replaced")
    ap.add_argument("--out", type=Path, help="also write the result to this separate file (never the target)")
    ap.add_argument("--overwrite", action="store_true", help="replace the target with the result (the only way to change it)")
    args = ap.parse_args(argv)

    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    try:
        if args.overwrite and args.out:
            raise Refused("--overwrite and --out together: pick the separate file or the target")
        if args.out and args.out.resolve() == args.target.resolve():
            raise Refused(f"--out names the target ({args.target}); that is --overwrite")

        rtk = rtk_root(args.rtk)
        accepted = rtk / "rtklua" / "Accepted"
        if not accepted.exists():
            raise Refused(f"RTK-Server not found at {rtk} (looked for {accepted})\n"
                          "  Pass --rtk DIR or set P1998_RTK to its root. See docs/research/README.md.")

        old_text = read_exact(args.target) if args.target.exists() else ""
        target = Target(old_text) if args.target.exists() else None
        extracted = extract(accepted, valid_mobs())
        placed = sum(1 for r in extracted.values() if r["Rooms"])
        capped = sum(1 for r in extracted.values() if r["MaxAlive"] != "0")
        deviations = apply_deviations(extracted)
        header, text, out_rows, carried = build(extracted, target)
        diffs = differences(header, target, out_rows)
    except Refused as e:
        print(f"refused: {e}", file=sys.stderr)
        return 2

    print(f"RTK: {accepted}")
    print(f"target: {args.target}" + ("" if target else "  (does not exist)"))
    print(f"extracted {len(extracted)} rows from RTK ({placed} with rooms, {capped} with a population cap)")
    print("carried over from the target, which RTK alone would not write:")
    for c, keys in carried["columns"].items():
        print(f"  column {c}: values on {len(keys)} row(s): {', '.join(keys) if keys else '-'}")
    print(f"  rows RTK's hooks do not yield: {len(carried['rows'])}: {', '.join(carried['rows']) if carried['rows'] else '-'}")
    print(f"  comment lines: {carried['comments']}")
    for key, rtk_room, held in deviations:
        print(f"  room held against RTK: {key} {rtk_room} -> {held} (ROOM_DEVIATIONS)")

    if text == old_text and target:
        print(f"RESULT: {args.target.name} is exactly what this would write ({len(out_rows)} rows).")
        status = 0
    else:
        print(f"RESULT: {args.target.name} DIFFERS from what this would write: {len(diffs)} row(s) differ"
              + ("" if diffs else " (every row matches: the difference is order, comments or line endings)") + ".")
        for kind, key, detail in diffs:
            print(f"  {kind} {key}" + (f": {detail}" if detail else ""))
        shown = list(difflib.unified_diff(old_text.splitlines(), text.splitlines(),
                                          fromfile=f"{args.target.name} (file)", tofile=f"{args.target.name} (would write)", lineterm="", n=0))
        for line in shown[:DIFF_LINES]:
            print("  " + line)
        if len(shown) > DIFF_LINES:
            print(f"  ... {len(shown) - DIFF_LINES} more diff lines")
        status = 1

    if args.out:
        write_exact(args.out, text)
        print(f"wrote the result to {args.out}")
    if args.overwrite:
        if status == 0:
            print(f"--overwrite: nothing to write, {args.target.name} is unchanged.")
        else:
            write_exact(args.target, text)
            print(f"--overwrite: wrote {len(out_rows)} rows to {args.target} ({len(diffs)} differing row(s) above).")
            status = 0
    elif status:
        print("nothing was written; --overwrite replaces the file, --out FILE writes the result beside it.")
    return status


if __name__ == "__main__":
    sys.exit(main())
