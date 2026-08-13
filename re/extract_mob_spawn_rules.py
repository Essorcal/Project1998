"""Generate game-data/MobSpawnRules.csv — what RTK's 99 `on_spawn` hooks actually say.

Nearly every on_spawn in the tree is boss PLACEMENT, not behaviour: 20 are a bare `mob:warp(map, x, y)` and
the rest are `local rand = math.random(1, N); if rand == 1 then mob:warp(A) elseif ... else mob:warp(C) end`
— "put me in one of my rooms, at random". That is a table, so it becomes one here and the 99 scripts go away.

The other two things a spawn does in RTK, folded into the same file:

* `AI/mob_on_spawn.lua` is the DEFAULT on_spawn for every creature that doesn't override it, and all it does
  is jitter max HP by +/- random((minDam + maxDam) * 2) so no two spawns are identical. That is a global
  rule, emitted as the single `*` row.
* A handful of mobs cap their own population — `strange_thing` counts every other strange_thing across two
  maps and vanishes if one is already out. That is the MaxAlive column.

Output columns: MobKey,Rooms(map:x:y|...),MaxAlive,CapMaps(|-separated),HpJitter
"""
import csv, re, glob
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "game-data" / "MobSpawnRules.csv"
ACCEPTED = ROOT / "RTK-Server" / "rtklua" / "Accepted"

def valid_mobs() -> set[str]:
    with (ROOT / "game-data" / "mobs.csv").open(encoding="utf-8", errors="replace") as f:
        return {r["Identifier"] for r in csv.DictReader(f)}

def tables():
    """Every `name = { ... }` mob table body across the AI + Mobs trees."""
    for pat in ("AI/**/*.lua", "Mobs/**/*.lua"):
        for f in glob.glob(str(ACCEPTED / pat), recursive=True):
            text = Path(f).read_text(encoding="utf-8", errors="replace")
            for m in re.finditer(r"^(\w+)\s*=\s*\{", text, re.M):
                end = text.find("\n}", m.end())
                yield m.group(1), text[m.end(): end if end > 0 else len(text)]

def main():
    mobs = valid_mobs()
    rows = {}

    for name, body in tables():
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
            rows[name] = dict(MobKey=name, Rooms="|".join(rooms), MaxAlive=cap,
                              CapMaps="|".join(cap_maps), HpJitter="")

    # The global default (AI/mob_on_spawn.lua): every creature's max HP is jittered on spawn.
    out = [dict(MobKey="*", Rooms="", MaxAlive=0, CapMaps="", HpJitter="1")]
    out += [rows[k] for k in sorted(rows)]

    with OUT.open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=["MobKey", "Rooms", "MaxAlive", "CapMaps", "HpJitter"])
        w.writeheader()
        w.writerows(out)
    placed = sum(1 for r in out if r["Rooms"])
    capped = sum(1 for r in out if r["MaxAlive"])
    print(f"wrote {len(out)} rows -> {OUT}  ({placed} with rooms, {capped} with a population cap)")

if __name__ == "__main__":
    main()
