#!/usr/bin/env python3
"""
classFactor solver for NexusTK 4.95 melee swings.

Kills the send-numbers-and-wait loop. Three modes:

  plan   BEFORE you farm: does this setup actually SEPARATE the hypotheses, and
         how many swings do you need? (The absence of this check cost two
         farming sessions.)
  solve  AFTER you farm: paste the damage values, get cf + band + chi2 + a
         saturation warning if the window could still be something else.
  scan   Mine re/auto/swings.csv for anything already measurable.

Model (docs/common/Melee-Damage.md):
  swing  = s/2 + Dam*2.5 + mightTerm(Might) + classFactor
  damage = max(1, floor(swing * (1 + mobAC/100)))
  mightTerm(m) = floor(m/4)/2 - 1
Class rules: warrior +2 Dam with any weapon; mage weapon S -> floor((x+5)/4);
unarmed S = 1-2.

Examples:
  python re/classfactor.py plan  --cls rogue --level 9 --might 7 --weapon "Wooden saber"
  python re/classfactor.py solve --cls rogue --level 7 --might 6 --weapon "Wooden saber" \
      --values "8 10 5 6 5 10 6 8 9 5 6 9 8 10 7 6 9 8 5 5 10 8 9 6 6 6"
  python re/classfactor.py scan
"""
import argparse
import csv
import math
import os
import sys
from collections import Counter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GD = os.path.join(ROOT, "game-data")
PATH_IDS = {"peasant": 0, "warrior": 1, "rogue": 2, "mage": 3, "poet": 4}
BARE = ("", "none", "unarmed", "bare", "barehanded")


def might_term(m):
    return math.floor(m / 4) / 2 - 1.0


def apply_ac(swing, ac):
    return max(1, math.floor(swing * (1 + ac / 100)))


def _load(fn, key, want):
    out = {}
    with open(os.path.join(GD, fn), newline="", encoding="utf-8", errors="replace") as f:
        for r in csv.DictReader(f):
            k = (r.get(key) or "").strip().lower()
            if k and k not in out:
                out[k] = {w: r.get(w, "") for w in want}
    return out


def weapon_range(name, path_id):
    """Effective (minS, maxS, itemDam) after class rules. Empty name = unarmed."""
    if (name or "").strip().lower() in BARE:
        return 1, 2, 0
    items = _load("Items.csv", "ItmDescription",
                  ["ItmMinimumSDamage", "ItmMaximumSDamage", "ItmDam"])
    row = items.get(name.strip().lower())
    if row is None:
        sys.exit("weapon not found in Items.csv: %r" % name)
    lo = int(row["ItmMinimumSDamage"] or 0)
    hi = int(row["ItmMaximumSDamage"] or 0)
    dam = int(row["ItmDam"] or 0)
    if path_id == 3:                      # mage weapon penalty, endpoint transform
        lo, hi = (lo + 5) // 4, (hi + 5) // 4
    return lo, hi, dam


def mob_ac(name):
    mobs = _load("mobs.csv", "Description", ["MobArmor", "Vita"])
    row = mobs.get(name.strip().lower())
    if row is None:
        sys.exit("mob not found in mobs.csv: %r" % name)
    return int(row["MobArmor"] or 0), int(row["Vita"] or 0)


def predict(lo, hi, dam, mt, cf, ac):
    """Damage multiset for one cf: {damage: number of s-rolls producing it}."""
    return Counter(apply_ac(s / 2 + dam * 2.5 + mt + cf, ac) for s in range(lo, hi + 1))


def setup(cls, level, might, weapon, mob, extra_dam=0, stale=False):
    pid = PATH_IDS[cls.lower()]
    lo, hi, idam = weapon_range(weapon, pid)
    dam = idam + extra_dam
    if pid == 1 and not stale and (weapon or "").strip().lower() not in BARE:
        dam += 2                          # warrior weapon bonus (suppressed by --stale)
    dam = max(0, dam)
    ac, vita = mob_ac(mob)
    return pid, lo, hi, dam, might_term(might), ac, vita



def robust_fit(counts, lo, hi, dam, mt, ac, eps=0.15):
    """Max-likelihood cf with an outlier allowance.

    A single contaminated swing destroys a min/max window estimate — the bot log is full of
    them (wrong mob, bogus killing-blow values). So each observation is scored against a
    mixture: (1-eps) of the model plus eps spread uniformly over the whole observed span.
    Outliers cost likelihood instead of moving the answer.

    Returns (best_cf, share_of_swings_inside_the_window, ranked_list).
    """
    n = sum(counts.values())
    span = max(counts) - min(counts) + 1
    out = []
    for i in range(-2, 30):
        cf = i * 0.5
        p = predict(lo, hi, dam, mt, cf, ac)
        tot = sum(p.values())
        ll = 0.0
        inside = 0
        for d, k in counts.items():
            pm = p.get(d, 0) / tot
            if pm:
                inside += k
            ll += k * math.log((1 - eps) * pm + eps / span)
        out.append((ll, cf, inside / n))
    out.sort(reverse=True)
    return out[0][1], out[0][2], out


def cmd_plan(a):
    pid, lo, hi, dam, mt, ac, vita = setup(a.cls, a.level, a.might, a.weapon, a.mob, a.dam, a.stale)
    nvals = hi - lo + 1
    print("%s lvl%d might%d vs %s (AC %d, x%.2f, %d vita)"
          % (a.cls, a.level, a.might, a.mob, ac, 1 + ac / 100, vita))
    print("weapon %s: effective S %d-%d (%d rolls), total Dam %d%s"
          % (a.weapon or "UNARMED", lo, hi, nvals, dam,
             "   [mage penalty applied]" if pid == 3 and a.weapon else ""))
    print("mightTerm(%d) = %+.1f\n" % (a.might, mt))

    seen, rows = {}, []
    for i in range(0, 25):
        cf = i * 0.5
        p = predict(lo, hi, dam, mt, cf, ac)
        w = (min(p), max(p))
        rows.append((cf, w, dict(sorted(p.items()))))
        seen.setdefault(w, []).append(cf)

    for cf, w, p in rows[:9]:
        dup = seen[w]
        flag = ""
        if len(dup) > 1:
            others = ", ".join("%g" % c for c in dup if c != cf)
            flag = "  <== COLLIDES with cf " + others
        print("  cf %4.1f  window %3d-%-3d %s%s" % (cf, w[0], w[1], p, flag))

    bad = [v for v in seen.values() if len(v) > 1]
    print()
    if bad:
        pairs = "; ".join("cf " + "/".join("%g" % c for c in v) for v in bad[:6])
        print("!! THIS SETUP DOES NOT SEPARATE ALL cf VALUES. Colliding: " + pairs)
        print("   Use a mob with AC 100 (x2.00 never collides) or a different weapon.")
    else:
        print("OK: every cf value gives a distinct window here.")
    if nvals > 1:
        for conf in (0.95, 0.99):
            n = math.ceil(math.log(1 - conf ** 0.5) / math.log(1 - 1.0 / nvals))
            print("   swings for %d%% chance of seeing BOTH endpoints: ~%d" % (conf * 100, n))
    if vita:
        mid = max(1, sum(rows[3][1]) // 2)
        print("   ~%d swings per kill (%d vita at ~%d dmg)" % (max(1, vita // mid), vita, mid))


def cmd_solve(a):
    pid, lo, hi, dam, mt, ac, vita = setup(a.cls, a.level, a.might, a.weapon, a.mob, a.dam, a.stale)
    vals = [int(x) for x in a.values.replace(",", " ").split()]
    n0 = len(vals)

    # rear attacks land at exactly 2x a normal roll: flag and drop
    base = Counter(vals)
    med = sorted(vals)[len(vals) // 2]
    doubles = [v for v in vals if v % 2 == 0 and (v // 2) in base and v > 1.5 * med]
    if doubles and not a.keep_doubles:
        for d in doubles:
            vals.remove(d)
        print("dropped %d suspected rear-attack x2 value(s): %s  (--keep-doubles to keep)"
              % (len(doubles), sorted(set(doubles))))

    c = Counter(vals)
    n = len(vals)
    obs_w = (min(vals), max(vals))
    print("\n%s lvl%d might%d %s vs %s (AC %d) | effective S %d-%d, Dam %d, mightTerm %+.1f"
          % (a.cls, a.level, a.might, a.weapon or "UNARMED", a.mob, ac, lo, hi, dam, mt))
    print("n=%d (of %d)  observed window %d-%d  counts %s\n"
          % (n, n0, obs_w[0], obs_w[1], dict(sorted(c.items()))))

    fits = []
    for i in range(-2, 30):
        cf = i * 0.5
        p = predict(lo, hi, dam, mt, cf, ac)
        if (min(p), max(p)) != obs_w:
            continue
        tot = sum(p.values())
        exp = {d: n * k / tot for d, k in p.items()}
        chi = sum((c.get(d, 0) - e) ** 2 / e for d, e in exp.items())
        fits.append((cf, chi, len(exp) - 1, {d: round(e, 1) for d, e in sorted(exp.items())}))

    if not fits:
        print("NO cf REPRODUCES THIS WINDOW. Check Might (relog first), the mob, the weapon,")
        print("or whether rage/stealth/a buff was active.")
        for i in range(0, 12):
            cf = i * 0.5
            p = predict(lo, hi, dam, mt, cf, ac)
            print("   cf %4.1f would give %d-%d" % (cf, min(p), max(p)))
        return

    for cf, chi, df, exp in fits:
        print("  cf = %.1f   chi2 %.2f/%ddf   expected %s" % (cf, chi, df, exp))
    if len(fits) > 1:
        print("\n!! AMBIGUOUS: %d cf values give this window. This setup cannot separate them."
              % len(fits))
    else:
        print("\ncf = %.1f" % fits[0][0])

    p = predict(lo, hi, dam, mt, fits[0][0], ac)
    tot = sum(p.values())
    for end, label in ((min(p), "min"), (max(p), "max")):
        if c.get(end, 0) == 0:
            pm = (1 - p[end] / tot) ** n
            print("!! %s endpoint %d NOT OBSERVED - P(miss)=%.3f. Window may extend further and an "
                  "adjacent cf could also fit. Swing more." % (label, end, pm))


def cmd_scan(a):
    path = os.path.join(ROOT, "re", "auto", "swings.csv")
    with open(path, newline="", encoding="utf-8", errors="replace") as f:
        rows = [r for r in csv.DictReader(f)
                if r.get("crit") == "0" and r.get("mob") and r.get("level") and r.get("might")]
    groups = {}
    for r in rows:
        try:
            mb, gb = int(r["might_base"] or 0), int(r["grace_base"] or 0)
        except ValueError:
            continue
        who = "rogue" if gb > mb else "warrior"
        k = (who, int(r["level"]), int(r["might"]), r.get("weapon") or "", r["mob"])
        groups.setdefault(k, []).append(int(r["dmg"]))
    print("groups with n>=20 in re/auto/swings.csv (characters split by grace vs might):\n")
    for k in sorted(groups, key=lambda k: -len(groups[k])):
        v = groups[k]
        if len(v) < 20:
            continue
        print("  %-7s lvl%-3d might%-3d %-16s %-20s n=%-4d window %d-%d"
              % (k[0], k[1], k[2], k[4], k[3] or "UNARMED", len(v), min(v), max(v)))
    print("\nrun `solve` on any of these to get cf.")



def cmd_sweep(a):
    """Solve cf at EVERY level found in the bot's swing log.

    This is the mode that matters: the bot logs level/might/weapon/dam on every swing, so
    grinding IS measuring. No hand-recording, and no level can be missed on the way past —
    which is the failure that lost rogue level 6.

    The log's `mob` column is only ~8% populated (the name packet carries no eid), so you
    assert what you were hunting with --mob. Everything else comes from the log.
    """
    path = os.path.join(ROOT, "re", "auto", "swings.csv")
    with open(path, newline="", encoding="utf-8", errors="replace") as f:
        rows = list(csv.DictReader(f))
    ac, vita = mob_ac(a.mob)
    pid = PATH_IDS[a.cls.lower()]

    groups = {}
    for r in rows:
        if r.get("crit") != "0" or not r.get("level") or not r.get("might"):
            continue
        if a.since and (r.get("ts") or "0") < a.since:
            continue
        if a.zone and (r.get("zone") or "") != a.zone:
            continue
        if r.get("mob") and r["mob"].strip().lower() != a.mob.strip().lower():
            continue                      # a named mob that is not our target
        try:
            mb, gb = int(r["might_base"] or 0), int(r["grace_base"] or 0)
        except ValueError:
            continue
        who = "rogue" if gb > mb else "warrior"
        if a.split and who != a.cls.lower():
            continue
        wep = (r.get("weapon") or a.weapon or "").strip()
        try:
            lvl, mgt = int(r["level"]), int(r["might"])
            dmg = int(r["dmg"])
        except ValueError:
            continue
        dam = r.get("dam")
        dam = int(dam) if (dam or "").strip().lstrip("-").isdigit() else None
        groups.setdefault((lvl, mgt, wep, dam), []).append(dmg)

    print("sweep: %s vs %s (AC %d)  from %s" % (a.cls, a.mob, ac, path))
    print()
    print("%-5s %-6s %-20s %-5s %-9s %s" % ("lvl", "might", "weapon", "n", "window", "cf"))
    print("-" * 78)
    any_row = False
    for (lvl, mgt, wep, dam) in sorted(
            groups, key=lambda k: (k[0], k[1], k[2], -99 if k[3] is None else k[3])):
        v = groups[(lvl, mgt, wep, dam)]
        if len(v) < a.min:
            continue
        any_row = True
        base = Counter(v)
        med = sorted(v)[len(v) // 2]
        v = [x for x in v if not (x % 2 == 0 and (x // 2) in base and x > 1.5 * med)]
        lo, hi, idam = weapon_range(wep, pid)
        # prefer the client-reported total Dam; fall back to the class rule
        if dam is None:
            dam = idam + (2 if pid == 1 and wep.strip().lower() not in BARE else 0)
        dam = max(0, dam)
        mt = might_term(mgt)
        cnt = Counter(v)
        cf, share, ranked = robust_fit(cnt, lo, hi, dam, mt, ac)
        p = predict(lo, hi, dam, mt, cf, ac)
        win = "%d-%d" % (min(p), max(p))
        note = "%.1f" % cf
        margin = ranked[0][0] - ranked[1][0]
        if margin < 2.3:                  # ~10:1 likelihood
            note += "  (weak: %.1f close behind)" % ranked[1][1]
        if share < 0.80:
            note += "  [only %d%% of swings inside - CONTAMINATED, check mob/buffs]" % (share * 100)
        print("%-5d %-6d %-20s %-5d %-9s %s"
              % (lvl, mgt, wep or "UNARMED", len(v), win, note))
    if not any_row:
        print("(no groups with n >= %d - lower --min, or check --mob/--zone/--since)" % a.min)


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="mode", required=True)

    def common(p):
        p.add_argument("--cls", required=True, choices=list(PATH_IDS))
        p.add_argument("--level", type=int, required=True)
        p.add_argument("--might", type=int, required=True)
        p.add_argument("--weapon", default="", help="item name; omit for unarmed")
        p.add_argument("--mob", default="Squirrel")
        p.add_argument("--dam", type=int, default=0, help="extra Dam from non-weapon gear")
        p.add_argument("--stale", action="store_true",
                       help="warrior only: suppress the +2 weapon bonus, modelling a character that "
                            "joined the path mid-session and has not relogged (the stale-stat bug). "
                            "Needed to re-solve historical warrior #2 data.")

    p1 = sub.add_parser("plan")
    common(p1)
    p1.set_defaults(f=cmd_plan)

    p2 = sub.add_parser("solve")
    common(p2)
    p2.add_argument("--values", required=True, help="damage numbers, space or comma separated")
    p2.add_argument("--keep-doubles", action="store_true")
    p2.set_defaults(f=cmd_solve)

    p3 = sub.add_parser("scan")
    p3.set_defaults(f=cmd_scan)

    p4 = sub.add_parser("sweep", help="solve cf at every level in re/auto/swings.csv")
    p4.add_argument("--cls", required=True, choices=list(PATH_IDS))
    p4.add_argument("--mob", required=True, help="what you were hunting (log rarely records it)")
    p4.add_argument("--weapon", default="", help="fallback when the log has no weapon")
    p4.add_argument("--min", type=int, default=20, help="minimum swings per level (default 20)")
    p4.add_argument("--since", default="", help="only rows with ts >= this epoch-ms string")
    p4.add_argument("--zone", default="", help="only rows from this zone")
    p4.add_argument("--split", action="store_true",
                    help="drop rows whose stat signature does not match --cls (multi-character log)")
    p4.set_defaults(f=cmd_sweep)

    a = ap.parse_args()
    a.f(a)


if __name__ == "__main__":
    main()
