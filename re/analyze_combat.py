#!/usr/bin/env python
"""Mine decoded_live.jsonl for combat mechanics: exp/kills (0x02/0x0a), player
vitals/stats (0x08), and mob HP/stat readouts (0x13)."""
import json, os, collections

F = os.path.join(os.path.dirname(os.path.abspath(__file__)), "decoded_live.jsonl")
rows = [json.loads(l) for l in open(F, encoding="utf-8") if l.strip()]


def bs(r):
    return bytes(int(x, 16) for x in r["hex"].split())


def u32le(b, o):
    return b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)


print(f"total packets: {len(rows)}")
c = collections.Counter(r["op"] for r in rows)
print("opcodes:", {hex(k): v for k, v in sorted(c.items())})
t0 = rows[0]["ts"]

print("\n=== 0x0a / 0x02 text (kills, exp, messages) ===")
for r in rows:
    if r["op"] in (0x0a, 0x02):
        d = bs(r)
        txt = d[3:].split(b"\x00")[0].decode("latin1", "replace")
        if any(ch.isalpha() for ch in txt):
            print(f"  +{(r['ts']-t0)/1000:6.2f}s op=0x{r['op']:02x}  {txt!r}")

print("\n=== 0x08 vitals/stats (subtype = byte1) ===")
for r in rows:
    if r["op"] != 0x08:
        continue
    d = bs(r)
    sub = d[1]
    # dump candidate u16/u32 fields
    u16 = [f"@{o}:{d[o]|(d[o+1]<<8)}" for o in range(2, min(len(d), 22), 2)]
    print(f"  +{(r['ts']-t0)/1000:6.2f}s sub=0x{sub:02x} n={r['n']:2d}  {' '.join(f'{x:02x}' for x in d)}")

print("\n=== 0x13 mob HP/stat (grouped by entity id, subtype=byte5) ===")
byent = collections.defaultdict(list)
for r in rows:
    if r["op"] != 0x13:
        continue
    d = bs(r)
    ent = u32le(d, 1) & 0xffffff  # id at bytes1..3? try 1..4
    ent = (d[2] << 16) | (d[3] << 8) | d[4]
    byent[ent].append((r["ts"], d))
for ent, lst in byent.items():
    print(f"  entity 0x{ent:06x}:")
    for ts, d in lst:
        print(f"     +{(ts-t0)/1000:6.2f}s sub=0x{d[5]:02x} val6={d[6]}(0x{d[6]:02x})  {' '.join(f'{x:02x}' for x in d)}")
