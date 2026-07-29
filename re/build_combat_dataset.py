#!/usr/bin/env python
"""
Turn decoded_live.jsonl (from frida_decode_live.py) into a per-swing combat dataset.

Packet semantics (established from live KRU 7.5.2.0):
  0x13  mob HP/stat:  [1:5]=entity u32BE, [5]=flags (0x40=crit, 0x80=update), [6]=current HP,
                      [10]=raw/pre-mitigation value. damage/swing = HP drop.
  0x0a  "N experience!" -> kill + exp, attributed to the entity that just hit 0 HP.
  0x07  spawn: [8:12]=entity u32BE, [12:14]=look u16BE  -> mob identity.

Output: re/combat_swings.csv  (ts, entity, look, hp_before, hp_after, damage, crit, raw10)
        plus a printed summary per mob.
"""
import json, os, csv, re, collections, argparse

D = os.path.dirname(os.path.abspath(__file__))
F = os.path.join(D, "decoded_live.jsonl")
OUT = os.path.join(D, "combat_swings.csv")

# Might/Grace/Will aren't streamed (only pushed on level-up); pass your current values so
# every swing is stamped with them until we auto-capture a level-up to map the offsets.
_ap = argparse.ArgumentParser()
_ap.add_argument("--might", default="")
_ap.add_argument("--grace", default="")
_ap.add_argument("--will", default="")
_ap.add_argument("--level", default="")
ARGS = _ap.parse_args()


def bs(r):
    return bytes(int(x, 16) for x in r["hex"].split())


def be(b, o, n):
    v = 0
    for i in range(n):
        v = (v << 8) | b[o + i]
    return v


rows = []
for _l in open(F, encoding="utf-8", errors="replace"):
    _l = _l.strip()
    if not _l:
        continue
    try:
        rows.append(json.loads(_l))   # tolerate interleaved/partial lines from concurrent writers
    except Exception:
        pass
look = {}          # entity -> look id
hp = {}            # entity -> last known HP
swings = []
last_zero = None   # most recent entity to reach 0 HP (for exp attribution)
kills = []

# live player stats, tracked from 0x08 (offsets established by known-plaintext map) and
# 0x39 (might/grace/will — offsets filled once a stats-screen capture is available).
P = {"level": ARGS.level, "might": ARGS.might, "grace": ARGS.grace, "will": ARGS.will,
     "ac": "", "dam": "", "hit": "", "hp": "", "mana": "", "tnl": "", "exp": ""}


def update_player(op, d):
    if op == 0x08:
        sub = d[1] if len(d) > 1 else 0
        if len(d) >= 6:
            P["exp"] = (d[4] << 8) | d[5]
        if sub == 0x19 and len(d) >= 29:
            P["tnl"] = (d[24] << 8) | d[25]
            P["ac"], P["dam"], P["hit"] = d[26], d[27], d[28]
        if sub == 0x38 and len(d) >= 10:
            P["hp"] = (d[4] << 8) | d[5]
            P["mana"] = (d[8] << 8) | d[9]
        if sub == 0x79 and len(d) >= 20:      # level-up push: new Might/Grace/Will
            P["might"], P["will"], P["grace"] = d[15], d[16], d[19]


for r in rows:
    d = bs(r)
    op = r["op"]
    update_player(op, d)
    if op == 0x07 and len(d) >= 14:
        ent = be(d, 8, 4)
        look[ent] = be(d, 12, 2)
    elif op == 0x13 and len(d) >= 11:
        ent = be(d, 1, 4)
        flags = d[5]
        cur = d[6]          # scaled HP-bar value (display), NOT raw damage
        dmg = d[10]         # raw10 = TRUE damage dealt this swing (= the popup number)
        prev = hp.get(ent)
        is_hit = prev is None or cur != prev     # new target or HP-bar moved => a swing
        if is_hit:
            swings.append({
                "ts": r["ts"], "entity": f"0x{ent:06x}", "look": look.get(ent, ""),
                "damage": dmg, "crit": int(bool(flags & 0x40)),
                "hpbar_before": prev if prev is not None else "",
                "hpbar_after": cur,
                "hpbar_delta": (prev - cur) if prev is not None else "",
                "p_might": P["might"], "p_grace": P["grace"], "p_will": P["will"],
                "p_ac": P["ac"], "p_dam": P["dam"], "p_hit": P["hit"],
                "p_hp": P["hp"], "p_mana": P["mana"], "p_tnl": P["tnl"], "p_exp": P["exp"],
            })
        hp[ent] = cur
        if cur == 0:
            last_zero = ent
    elif op == 0x0a:
        m = re.search(rb"(\d+) experience", d)
        if m:
            kills.append({"ts": r["ts"], "entity": f"0x{last_zero:06x}" if last_zero else "",
                          "look": look.get(last_zero, ""), "exp": int(m.group(1))})

FIELDS = ["ts", "entity", "look", "damage", "crit", "hpbar_before", "hpbar_after", "hpbar_delta",
          "p_might", "p_grace", "p_will", "p_ac", "p_dam", "p_hit", "p_hp", "p_mana", "p_tnl", "p_exp"]
with open(OUT, "w", newline="", encoding="utf-8") as f:
    w = csv.DictWriter(f, fieldnames=FIELDS)
    w.writeheader()
    w.writerows(swings)

print(f"{len(swings)} swings, {len(kills)} kills -> {OUT}\n")

# per-mob summary
by = collections.defaultdict(list)
for s in swings:
    by[(s["entity"], s["look"])].append(s)
print("per-mob (entity/look): hits  damages(raw10=popup)  crits  realHP(sum)")
for (ent, lk), lst in by.items():
    dmgs = [s["damage"] for s in lst]
    ncrit = sum(s["crit"] for s in lst)
    lkh = f"0x{lk:04x}" if isinstance(lk, int) else "?"
    print(f"  {ent} look={lkh}  hits={len(lst)}  dmg={dmgs}  crits={ncrit}  realHP~{sum(dmgs)}")

if kills:
    print("\nkills:")
    for k in kills:
        lkh = f"0x{k['look']:04x}" if isinstance(k["look"], int) else "?"
        print(f"  {k['entity']} look={lkh}  +{k['exp']} exp")
