#!/usr/bin/env python
"""Known-plaintext field mapper: given real stat values you read off the self-info page,
find which packet (ANY opcode) and byte offset holds each one.

Usage:
  python re/map_stats.py might=5 grace=4 will=3 vita=78 mana=42
Searches every captured packet for each value as u8 / u16be / u16le and reports hits.
"""
import json, os, sys

D = os.path.dirname(os.path.abspath(__file__))
rows = [json.loads(l) for l in open(os.path.join(D, "decoded_live.jsonl"), encoding="utf-8", errors="replace")
        if l.strip() and l.strip().startswith("{")]


def bs(r):
    return bytes(int(x, 16) for x in r["hex"].split())


targets = {}
for a in sys.argv[1:]:
    if "=" in a:
        k, v = a.split("=")
        targets[k] = int(v)
if not targets:
    print("give values, e.g.:  python re/map_stats.py might=5 grace=4 will=3 vita=78 mana=42")
    sys.exit(0)

print(f"searching {len(rows)} packets for {targets}\n")
for name, val in targets.items():
    hits = []
    for r in rows:
        d = bs(r)
        op = r["op"]
        sub = d[1] if len(d) > 1 else -1
        for o in range(len(d)):
            if val < 256 and d[o] == val:
                hits.append((op, sub, o, "u8"))
            if o + 1 < len(d):
                if (d[o] << 8 | d[o + 1]) == val:
                    hits.append((op, sub, o, "u16be"))
                if (d[o + 1] << 8 | d[o]) == val:
                    hits.append((op, sub, o, "u16le"))
    uniq = sorted(set(hits))
    tag = "u16" if val >= 256 else "u8/u16"
    shown = ", ".join(f"0x{op:02x}/s{sub:02x}@{o}({e})" for op, sub, o, e in uniq[:25])
    print(f"{name}={val:<5} [{tag}] {len(uniq)} hits: {shown}")
