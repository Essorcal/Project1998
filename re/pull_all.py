#!/usr/bin/env python
"""
pull_all.py -- extract EVERYTHING about the character from the captured wire stream:
core stats, worn gear (per slot), and per-item stats. Parsers here are imported by
nexus_bot for LIVE capture; run standalone to backfill from auto/raw_packets.jsonl.

Sources (all decoded from real live packets):
  0x39 self-profile -> class + worn equipment names, slot order, per-item metadata
  0x0f item-info    -> item name + type + icon + durability + the stat line text
  0x08 statblock    -> full core stats (via nexus_agent.Agent's existing decode +
                       its equip/unequip delta decomposition = each item's REAL stat bonus)

Outputs (re/auto/):
  equipment.csv   ts, label, slot, item          (latest worn loadout, one row per slot)
  item_info.csv   item, type, icon, durability, stat_text, raw_hex   (dedup'd item DB)
Run:  python re/pull_all.py
"""
import os, sys, json, csv, re, collections
import nexus_agent as NA

_QTY = re.compile(r"\s*\((\d+)\)\s*$")   # trailing " (6)" = stack quantity, not the name

OUT = NA.OUT
P_EQUIP = os.path.join(OUT, "equipment.csv")
P_ITEMS = os.path.join(OUT, "item_info.csv")


def _bytes(hexstr):
    return [int(x, 16) for x in hexstr.split()]


def _strings(a, start=1, maxlen=40):
    """All length-prefixed printable-ASCII strings in a, as (offset, str)."""
    out, i = [], start
    while i < len(a):
        L = a[i]
        if 0 < L < maxlen and i + 1 + L <= len(a) and all(32 <= a[i + 1 + k] < 127 for k in range(L)):
            out.append((i, "".join(chr(c) for c in a[i + 1:i + 1 + L])))
            i += 1 + L
        else:
            i += 1
    return out


def parse_profile(a):
    """0x39 -> (label, [worn item names in slot order]). Worn equipment arrives as
    name/desc PAIRS (the name repeated); the legend section that follows is single,
    unpaired sentence strings. So we take the label (first string), then consume paired
    strings as gear and STOP at the first unpaired one (start of legends)."""
    ss = _strings(a)
    if not ss:
        return None, []
    label = ss[0][1]
    items, j = [], 1
    while j + 1 < len(ss) and ss[j][1] == ss[j + 1][1]:   # a real worn item (name==desc)
        items.append(ss[j][1])
        j += 2
    return label, items


def parse_item(a):
    """0x0f -> dict(name, type, icon, durability, stat_text). Layout (from live decode):
    [op][type][icon be16][00][len name][name][len desc][desc][meta... durability be16 ...
    optional trailing ASCII stat line]."""
    ss = _strings(a, start=4)
    if not ss:
        return None
    raw_name = ss[0][1]
    name = _QTY.sub("", raw_name)          # drop stack quantity -> canonical item name
    qm = _QTY.search(raw_name)
    qty = int(qm.group(1)) if qm else None
    # a later string that isn't the repeated desc is the stat/flavor line (e.g. ' 10')
    stat_text = ""
    for off, s in ss[1:]:
        if _QTY.sub("", s) != name:
            stat_text = s.strip()
            break
    return {"name": name, "qty": qty, "type": a[1] if len(a) > 1 else 0,
            "icon": (a[2] << 8) | a[3] if len(a) > 3 else 0,
            "stat_text": stat_text,
            "raw_hex": " ".join("%02x" % x for x in a)}


def backfill(path=None):
    path = path or NA.P_RAW
    agent = NA.Agent()
    agent.gear = dict(NA.Z)
    latest_profile = None
    items = {}
    n = 0
    for line in open(path, encoding="utf-8", errors="replace"):
        line = line.strip()
        if not line.startswith("{"):
            continue
        try:
            p = json.loads(line)
        except ValueError:
            continue
        if "hex" not in p:
            continue
        op = p.get("op")
        a = _bytes(p["hex"])
        n += 1
        try:
            if op == 0x39 and len(a) > 14:
                lbl, its = parse_profile(a)
                if its:
                    latest_profile = (p["ts"], lbl, its)
            elif op == 0x0f and len(a) > 6:
                it = parse_item(a)
                if it and it["name"]:
                    items[it["name"]] = it
            else:
                agent.on_packet(p)     # feeds stats + gear-delta decomposition
        except Exception:
            pass

    # ---- write equipment ----
    if latest_profile:
        ts, lbl, its = latest_profile
        with open(P_EQUIP, "w", newline="", encoding="utf-8") as f:
            w = csv.writer(f)
            w.writerow(["ts", "label", "slot", "item"])
            for i, it in enumerate(its):
                w.writerow([ts, lbl, i, it])

    # ---- write item DB ----
    if items:
        with open(P_ITEMS, "w", newline="", encoding="utf-8") as f:
            w = csv.writer(f)
            w.writerow(["item", "type", "icon", "stat_text", "raw_hex"])
            for it in sorted(items.values(), key=lambda x: x["name"]):
                w.writerow([it["name"], it["type"], it["icon"],
                            it["stat_text"], it["raw_hex"]])

    # ---- summary ----
    print(f"[pull_all] replayed {n} packets")
    base = agent.base_of(agent.cur) if agent.cur else None
    print("\n== CORE STATS ==")
    if agent.cur:
        print(f"  level={agent.level}  exp={agent.exp}  tnl={agent.tnl}")
        print(f"  might={agent.cur['might']} grace={agent.cur['grace']} will={agent.cur['will']}"
              f"  maxhp={agent.cur['maxhp']} maxmana={agent.cur['maxmana']}")
        print(f"  ac={agent.ac} dam={agent.dam} hit={agent.hit}")
        if base:
            print(f"  BASE (gear removed): {base}")
        if any(agent.gear.values()):
            print(f"  gear bonus learned from equip/unequip deltas: "
                  f"{ {k: v for k, v in agent.gear.items() if v} }")
    else:
        print("  (no statblock seen in capture)")
    if latest_profile:
        ts, lbl, its = latest_profile
        print(f"\n== WORN GEAR ({lbl}) ==")
        for i, it in enumerate(its):
            print(f"  slot {i}: {it}")
    print(f"\n== ITEM DB: {len(items)} distinct items ==")
    for it in sorted(items.values(), key=lambda x: x["name"]):
        extra = f"  stat_text={it['stat_text']!r}" if it["stat_text"] else ""
        print(f"  {it['name']}{extra}")
    print(f"\nwrote {P_EQUIP} and {P_ITEMS}")


if __name__ == "__main__":
    src = sys.argv[1] if len(sys.argv) > 1 else None
    backfill(src)
