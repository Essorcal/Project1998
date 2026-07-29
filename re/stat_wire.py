#!/usr/bin/env python
"""Read your stats straight off the wire — no memory scanning.

The server pushes a stat-block packet (0x08 sub-0x58/0x59/0x78/0x79) on EVERY
change: login, level-up (0x78/0x79), and each equip/unequip that alters a stat
(0x58 off / 0x59 on). Each packet carries the CURRENT EQUIPPED total for
might/grace/will/maxHP/mana. So:
  * base stat        = the reading with an item OFF (0x58), or a naked ding
  * item's bonus     = (0x59 on) - (0x58 off) across a toggle
  * equipped total   = the reading with gear ON (0x59)
This prints every stat-block packet in order with deltas, so equip diffs are
explicit. Feed it decoded_live.jsonl from frida_decode_live.py.

Offsets (live KRU 7.5.2.0): level=d[6], maxHP=BE[9:11], mana=BE[13:15],
might=d[15], will=d[16], grace=d[19].
"""
import json, os
F=os.path.join(os.path.dirname(os.path.abspath(__file__)),"decoded_live.jsonl")
def bs(r): return bytes(int(x,16) for x in r["hex"].split())
def be(b,o,n):
    v=0
    for i in range(n):
        if o+i<len(b): v=(v<<8)|b[o+i]
    return v
rows=[]
for l in open(F,encoding="utf-8",errors="replace"):
    l=l.strip()
    if l.startswith("{"):
        try: rows.append(json.loads(l))
        except: pass
print("Every stat-block packet (0x58/59/78/79) = server pushing your totals on login/ding/equip-change:")
print(f"{'t':>7} {'sub':>4} {'lvl':>3} {'mgt':>3} {'grc':>3} {'wil':>3} {'maxHP':>5} {'mana':>4}  kind")
t0=None; prev=None
KIND={0x58:'equip/refresh',0x59:'equip/refresh',0x78:'LEVEL-UP',0x79:'LEVEL-UP'}
for r in rows:
    if r["op"]!=0x08: continue
    d=bs(r)
    if len(d)<20 or d[1] not in (0x58,0x59,0x78,0x79): continue
    if t0 is None: t0=r["ts"]
    lvl,mgt,wil,grc=d[6],d[15],d[16],d[19]
    hp,mana=be(d,9,2),be(d,13,2)
    cur=(mgt,grc,wil,hp,mana)
    delta=""
    if prev and cur!=prev:
        dm,dg,dw,dh,dn=mgt-prev[0],grc-prev[1],wil-prev[2],hp-prev[3],mana-prev[4]
        parts=[]
        if dm: parts.append(f"{dm:+d}mgt")
        if dg: parts.append(f"{dg:+d}grc")
        if dw: parts.append(f"{dw:+d}wil")
        if dh: parts.append(f"{dh:+d}hp")
        if dn: parts.append(f"{dn:+d}mp")
        delta="  <-- "+" ".join(parts) if parts else ""
    print(f"{(r['ts']-t0)/1000:7.1f} {d[1]:#04x} {lvl:>3} {mgt:>3} {grc:>3} {wil:>3} {hp:>5} {mana:>4}  {KIND[d[1]]}{delta}")
    prev=cur
