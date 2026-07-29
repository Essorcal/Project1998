#!/usr/bin/env python3
"""Extract the door-object graphic toggle table from Server/Session.Movement.cs's `DoorToggle` switch
into data/game-data/DoorObjects.csv -- fidelity-guaranteed (parses the C# literal, no hand transcription).

Two row kinds:
  map,<obj>,<obj>,<startDx>,<id;id;...>   an exact faced-object match -> swap the run to these ids at startDx
  delta,<lo>,<hi>,0,<signedDelta>         faced object in [lo,hi] (single tile) -> obj + delta

The loader (Content.LoadDoorObjects) rebuilds the same lookup DoorToggle used before.
"""
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "Server/Session.Movement.cs"
OUT = ROOT / "data/game-data/DoorObjects.csv"

text = SRC.read_text(encoding="utf-8")

# isolate the DoorToggle method body so we don't accidentally match other switches
m = re.search(r"private static \(int startDx, ushort\[\] objs\)\? DoorToggle\(ushort obj\)\s*\{(.*?)\n    \}\n",
              text, re.S)
if not m:
    raise SystemExit("could not locate DoorToggle method body")
body = m.group(1)

rows = []

# explicit: case N:  return (dx, new ushort[] { a, b, c });
for cm in re.finditer(r"case\s+(\d+):\s*return\s*\((-?\d+),\s*new ushort\[\]\s*\{\s*([\d,\s]+?)\}\s*\)\s*;", body):
    obj = int(cm.group(1))
    dx = int(cm.group(2))
    ids = [x.strip() for x in cm.group(3).split(",") if x.strip()]
    rows.append(("map", obj, obj, dx, ";".join(ids)))

# range deltas: >= A and <= B => o + N,   /   o - N,
for rm in re.finditer(r">=\s*(\d+)\s*and\s*<=\s*(\d+)\s*=>\s*o\s*([+-])\s*(\d+)\s*,", body):
    lo, hi = int(rm.group(1)), int(rm.group(2))
    delta = int(rm.group(4)) * (1 if rm.group(3) == "+" else -1)
    rows.append(("delta", lo, hi, 0, str(delta)))

with OUT.open("w", encoding="utf-8", newline="") as f:
    f.write("kind,lo,hi,startDx,result\n")
    for kind, lo, hi, dx, result in rows:
        f.write(f"{kind},{lo},{hi},{dx},{result}\n")

n_map = sum(1 for r in rows if r[0] == "map")
n_delta = sum(1 for r in rows if r[0] == "delta")
print(f"wrote {OUT}  ({n_map} map rows + {n_delta} delta rows = {len(rows)} total)")
