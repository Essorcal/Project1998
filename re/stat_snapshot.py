#!/usr/bin/env python
"""
Build a clean per-level / per-change stat timeline for the character you're playing,
from decoded_live.jsonl (produced by frida_decode_live.py).

Two goals:
  (1) exact stat snapshot at every level        -> level-up 0x08/sub-0x79 events
  (2) BASE stats (gear removed) vs TOTAL (gear on)
      -> we don't guess a "base" byte; we just log a fresh snapshot every time a stat
         CHANGES. Unequip-all makes AC/DAM/HP/mana drop to base; re-equip restores them.
         The change-log itself is the base-vs-equip experiment.

Packet offsets (live KRU 7.5.2.0, from known-plaintext mapping):
  0x08 sub-0x19 : exp=BE[4:6], TNL=BE[24:26], AC=[26], DAM=[27], HIT=[28]
  0x08 sub-0x38 : maxHP=BE[4:6]? / current-vitals block (HP/mana)
  0x08 sub-0x79 : LEVEL-UP push. might=[15], will=[16], grace=[19]; also AC=[63],DAM=[64],
                  TNL=BE[61:63]. Two parallel stat records (block A @9, block B @33).
Fields we're unsure of are printed as raw hex so nothing is silently dropped.
"""
import json, os, argparse

D = os.path.dirname(os.path.abspath(__file__))
F = os.path.join(D, "decoded_live.jsonl")


def bs(r):
    return bytes(int(x, 16) for x in r["hex"].split())


def be(b, o, n):
    v = 0
    for i in range(n):
        if o + i < len(b):
            v = (v << 8) | b[o + i]
    return v


def load():
    out = []
    for l in open(F, encoding="utf-8", errors="replace"):
        l = l.strip()
        if not l or not l.startswith("{"):
            continue
        try:
            out.append(json.loads(l))
        except Exception:
            pass
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--raw79", action="store_true", help="dump every raw 0x79 fully")
    args = ap.parse_args()
    rows = load()

    # running stat sheet; a snapshot is emitted whenever any tracked field changes.
    S = {"level": None, "might": None, "grace": None, "will": None,
         "ac": None, "dam": None, "hit": None, "tnl": None,
         "hp": None, "mana": None, "exp": None}
    TRACK = ["might", "grace", "will", "ac", "dam", "hit", "hp", "mana", "tnl"]
    last_snap = None
    timeline = []
    levelups = []
    t0 = None

    for r in rows:
        if r["op"] != 0x08:
            continue
        d = bs(r)
        if len(d) < 2:
            continue
        sub = d[1]
        if t0 is None:
            t0 = r["ts"]
        if sub == 0x19 and len(d) >= 29:
            S["exp"] = be(d, 4, 2)
            S["tnl"] = be(d, 24, 2)
            S["ac"], S["dam"], S["hit"] = d[26], d[27], d[28]
        elif sub == 0x38 and len(d) >= 10:
            S["hp"] = be(d, 4, 2)
            S["mana"] = be(d, 8, 2)
        elif sub in (0x58, 0x59, 0x78, 0x79) and len(d) >= 20:
            # stat-block family (SAME layout): 0x78/0x79=level-up push, 0x58/0x59=equip/stat
            # refresh. These are the ONLY packets carrying Might/Grace/Will, and they reflect
            # gear bonuses (e.g. Sword of Power +1 might shows here, not in 0x19).
            S["level"] = d[6]
            S["might"], S["will"], S["grace"] = d[15], d[16], d[19]
            S["hp"], S["mana"] = be(d, 9, 2), be(d, 13, 2)   # max HP / max mana (gear-inclusive)
            if len(d) >= 63:
                S["tnl"] = be(d, 61, 2)                        # exp to NEXT level
            if sub in (0x78, 0x79):
                levelups.append((round((r["ts"] - t0) / 1000, 1), d))
        # emit a snapshot row when the tracked subset changes
        cur = tuple(S[k] for k in TRACK)
        if cur != last_snap and all(S[k] is not None for k in ("ac", "dam")):
            timeline.append((round((r["ts"] - t0) / 1000, 1), dict(S)))
            last_snap = cur

    print("=== STAT TIMELINE (new row only when AC/DAM/HIT/might/grace/will/HP/mana/TNL changes) ===")
    print("   (watch AC/DAM/HIT for gear on/off; HP/mana/might/grace/will jump on level-up)")
    print(f"{'t(s)':>7} {'lvl':>4} {'might':>5} {'grace':>5} {'will':>4} {'AC':>4} {'DAM':>4} {'HIT':>4} "
          f"{'curHP':>6} {'mana':>5} {'TNL':>6} {'exp':>7}")
    for t, s in timeline:
        def g(k):
            return s[k] if s[k] is not None else "-"
        print(f"{t:>7} {g('level'):>4} {g('might'):>5} {g('grace'):>5} {g('will'):>4} {g('ac'):>4} {g('dam'):>4} "
              f"{g('hit'):>4} {g('hp'):>6} {g('mana'):>5} {g('tnl'):>6} {g('exp'):>7}")

    # --- persist actual values to a merged per-level CSV (accumulates across captures) ---
    # NEVER overwrite the curated table when this capture has no level-up packets
    # (an equip-refresh 0x58/0x59 is NOT a ding and must not clobber char_levels.csv).
    LVL_CSV = os.path.join(D, "char_levels.csv")
    if not levelups:
        print(f"\n[no 0x78/0x79 level-up packets in this capture -> leaving {LVL_CSV} untouched]")
    else:
        import csv
        COLS = ["level", "might", "grace", "will", "vita", "mana", "tnl_next"]
        table = {}
        if os.path.exists(LVL_CSV):
            for row in csv.DictReader(open(LVL_CSV, encoding="utf-8")):
                try:
                    table[int(row["level"])] = row
                except (KeyError, ValueError):
                    pass
        for t, d in levelups:
            lvl = d[6]
            table[lvl] = {"level": lvl, "might": d[15], "grace": d[19], "will": d[16],
                          "vita": be(d, 9, 2), "mana": be(d, 13, 2), "tnl_next": be(d, 61, 2)}
        with open(LVL_CSV, "w", newline="", encoding="utf-8") as f:
            w = csv.DictWriter(f, fieldnames=COLS)
            w.writeheader()
            for lvl in sorted(table):
                w.writerow(table[lvl])
        print(f"\n[merged {len(levelups)} capture level(s) into {LVL_CSV}: now holds levels {sorted(table)}]")

    if levelups:
        print(f"\n=== PER-LEVEL TABLE (from {len(levelups)} level-up packet(s)) ===")
        print(f"{'lvl':>4} {'might':>5} {'grace':>5} {'will':>4} {'maxHP':>6} {'mana':>5} {'TNLnext':>7}")
        seen = {}
        for t, d in levelups:
            seen[d[6]] = d          # dedup by level
        prev = None
        for lvl in sorted(seen):
            d = seen[lvl]
            mgt, wil, grc = d[15], d[16], d[19]
            hp, mana, tnl = be(d, 9, 2), be(d, 13, 2), be(d, 61, 2)
            delta = ""
            if prev is not None:
                pm, pw, pg, ph, pn = prev
                delta = f"  (+{mgt-pm}m +{grc-pg}g +{wil-pw}w +{hp-ph}hp +{mana-pn}mp)"
            print(f"{lvl:>4} {mgt:>5} {grc:>5} {wil:>4} {hp:>6} {mana:>5} {tnl:>7}{delta}")
            prev = (mgt, wil, grc, hp, mana)
        if args.raw79:
            for t, d in levelups:
                print(f"\n  raw lvl{d[6]}:")
                print("   A:", " ".join(f"{x:02x}" for x in d[5:30]))
                print("   B:", " ".join(f"{x:02x}" for x in d[30:65]))
    else:
        print("\n(no level-ups in this capture)")


if __name__ == "__main__":
    main()
