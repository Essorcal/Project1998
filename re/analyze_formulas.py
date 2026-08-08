"""What do we actually know from the combat data -- and what is still unidentifiable?

Makes data collection SELF-DIRECTING. A coefficient can only be fit if its input VARIES;
with one character at one gear/buff configuration every predictor is constant, and no
amount of extra swings helps. So this reports, per predictor:

  * how much it varies (constant -> not identifiable, full stop)
  * whether it is collinear with another predictor (moves only in lockstep -> the two
    cannot be told apart no matter how much data is collected)

then summarises what IS measurable now: per-mob damage distributions and hit rates.

Reads auto/swings.csv (damage; landed hits only) and auto/attempts.csv (hit AND miss).
Pure stdlib.

Usage:  python analyze_formulas.py
"""
import os, csv, math, collections

D = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(D, "auto")
P_SWINGS = os.path.join(OUT, "swings.csv")
P_ATTEMPTS = os.path.join(OUT, "attempts.csv")

# our offensive inputs; a damage/hit formula can only use what moves
PREDICTORS = ["level", "might", "grace", "will", "dam", "hit", "hit_stat", "ac", "rel_dir"]


def load(path):
    if not os.path.exists(path):
        return []
    with open(path, encoding="utf-8") as f:
        return list(csv.DictReader(f))


def nums(rows, key):
    out = []
    for r in rows:
        v = r.get(key, "")
        if v not in ("", None):
            try:
                out.append(float(v))
            except ValueError:
                pass
    return out


def stats(xs):
    n = len(xs)
    if not n:
        return None
    m = sum(xs) / n
    sd = math.sqrt(sum((x - m) ** 2 for x in xs) / n) if n > 1 else 0.0
    return n, m, sd, min(xs), max(xs)


def corr(a, b):
    n = min(len(a), len(b))
    if n < 3:
        return None
    a, b = a[:n], b[:n]
    ma, mb = sum(a) / n, sum(b) / n
    va = sum((x - ma) ** 2 for x in a)
    vb = sum((x - mb) ** 2 for x in b)
    if va == 0 or vb == 0:
        return None
    cov = sum((x - ma) * (y - mb) for x, y in zip(a, b))
    return cov / math.sqrt(va * vb)


def identifiability(rows, label):
    print(f"\n=== IDENTIFIABILITY ({label}, n={len(rows)}) ===")
    if not rows:
        print("  no data")
        return []
    # In attempts.csv `hit` is the OUTCOME (did it land) and `hit_stat` is the predictor;
    # in swings.csv `hit` IS the stat. Treating the outcome as an input would report the
    # very thing we are trying to explain as an explanatory variable.
    preds = [k for k in PREDICTORS
             if not (k == "hit" and rows and "hit_stat" in rows[0])]
    varying = []
    for k in preds:
        xs = nums(rows, k)
        if not xs:
            continue
        distinct = sorted(set(xs))
        if len(distinct) <= 1:
            val = distinct[0] if distinct else "?"
            print(f"  {k:9} CONSTANT at {val:g}  -> coefficient NOT identifiable")
        else:
            varying.append(k)
            print(f"  {k:9} varies: {len(distinct)} levels "
                  f"{[f'{v:g}' for v in distinct[:6]]}  -> usable")
    if not varying:
        print("\n  NOTHING varies. More swings at this configuration cannot identify any")
        print("  coefficient -- they only sharpen one distribution. Change ONE stat and")
        print("  keep farming the SAME mobs:")
        print("    * cast / drop Might      -> isolates `might` (+3, nothing else moves)")
        print("    * Black ring on / off    -> isolates `hit`   (+3, nothing else moves)")
        print("    * Swift <-> Novice sword -> moves `dam` AND `grace` together")
        print("    * levelling              -> moves `level` (and base stats)")
        return varying
    print("\n  collinearity (|r| > 0.95 means the pair cannot be separated):")
    flagged = False
    for i, a in enumerate(varying):
        for b in varying[i + 1:]:
            r = corr(nums(rows, a), nums(rows, b))
            if r is not None and abs(r) > 0.95:
                print(f"    {a} ~ {b}: r={r:+.3f}  -> CONFOUNDED, vary one independently")
                flagged = True
    if not flagged:
        print("    none -- the varying predictors are separable")
    return varying


def damage_by_mob(rows):
    print(f"\n=== DAMAGE BY MOB (landed hits, n={len(rows)}) ===")
    by = collections.defaultdict(list)
    for r in rows:
        mob = r.get("mob") or r.get("look") or "?"
        try:
            by[(r.get("zone", ""), mob)].append(float(r["dmg"]))
        except (ValueError, KeyError, TypeError):
            pass
    if not by:
        print("  no damage rows")
        return
    print(f"  {'zone':<12}{'mob':<20}{'n':>5}{'mean':>8}{'sd':>7}{'min':>6}{'max':>6}")
    for (zone, mob), xs in sorted(by.items(), key=lambda kv: -len(kv[1])):
        n, m, sd, lo, hi = stats(xs)
        print(f"  {zone[:11]:<12}{mob[:19]:<20}{n:>5}{m:>8.1f}{sd:>7.1f}{lo:>6.0f}{hi:>6.0f}")
    print("\n  NB: a wide sd at a FIXED stat vector is the per-swing roll (+crit), not noise")
    print("  to average away -- it bounds how many samples each configuration needs.")


def hit_rate(rows):
    print(f"\n=== HIT RATE (attempts incl. misses, n={len(rows)}) ===")
    if not rows:
        print("  attempts.csv is empty -- P(hit) cannot be fit yet.")
        print("  (swings.csv holds LANDED hits only, so misses leave no trace there.)")
        return
    by = collections.defaultdict(lambda: [0, 0])
    for r in rows:
        key = (r.get("mob") or "?", r.get("hit_stat", ""))
        by[key][0] += 1
        if r.get("hit") == "1":
            by[key][1] += 1
    print(f"  {'mob':<20}{'hit_stat':>9}{'swings':>8}{'hits':>6}{'rate':>8}")
    for (mob, hs), (tot, h) in sorted(by.items(), key=lambda kv: -kv[1][0]):
        print(f"  {mob[:19]:<20}{str(hs):>9}{tot:>8}{h:>6}{h / tot:>8.1%}")


P_ITEMS = os.path.join(OUT, "item_stats.csv")


def weapon_coverage(rows):
    """Damage S/L is a WEAPON property, not a per-swing one, and it is the only combat
    input the character stat vector cannot carry (the char sheet never shows it). So rows
    record which weapon was equipped and join to item_stats.csv. Report the coverage."""
    print(f"\n=== WEAPON / DAMAGE RANGE ===")
    tbl = {}
    if os.path.exists(P_ITEMS):
        for r in load(P_ITEMS):
            tbl[r["item"]] = r
    used = collections.Counter(r.get("weapon", "") for r in rows)
    if not any(used):
        print("  no weapon recorded on any row yet (bot fetches it from the 0x39 profile)")
        return
    for w, n in used.most_common():
        if not w:
            print(f"  {'(unknown)':<24} {n:>6} rows  -- profile not fetched for these")
            continue
        it = tbl.get(w)
        if it and it.get("dam_s_lo"):
            print(f"  {w:<24} {n:>6} rows  damage {it['dam_s_lo']}m{it['dam_s_hi']}"
                  f"  DAM+{it.get('dam_bonus') or 0}")
        else:
            print(f"  {w:<24} {n:>6} rows  *** NOT in item_stats.csv -- right-click it and"
                  f" add its Damage S/L ***")


def paired(rows, label="damage", value_key="dmg"):
    """THE measurement that matters: for a predictor that varies, compare outcomes on the
    SAME mob at each of its levels. Holding the mob fixed removes its defence from the
    comparison, which is the only way to attribute a change to OUR stat rather than to a
    different opponent. Anything else is confounded."""
    print(f"\n=== PAIRED COMPARISON ({label}) ===")
    any_pair = False
    preds = [k for k in PREDICTORS
             if not (k == "hit" and rows and "hit_stat" in rows[0])]
    for k in preds:
        xs = set(nums(rows, k))
        if len(xs) < 2:
            continue
        by = collections.defaultdict(lambda: collections.defaultdict(list))
        for r in rows:
            mob = r.get("mob") or ""
            v, out = r.get(k, ""), r.get(value_key, "")
            if not mob or v in ("", None) or out in ("", None):
                continue
            try:
                by[mob][float(v)].append(float(out))
            except ValueError:
                pass
        for mob, levels in by.items():
            if len(levels) < 2:
                continue          # this mob only ever seen at one level -> no comparison
            any_pair = True
            parts = []
            for v in sorted(levels):
                st = stats(levels[v])
                parts.append(f"{k}={v:g}: n={st[0]} mean={st[1]:.1f}")
            lo, hi = min(levels), max(levels)
            d = (sum(levels[hi]) / len(levels[hi])) - (sum(levels[lo]) / len(levels[lo]))
            per = d / (hi - lo) if hi != lo else 0
            print(f"  {mob:<18} " + " | ".join(parts))
            print(f"  {'':<18} delta {d:+.1f} over {hi-lo:g} points -> {per:+.2f} per point")
    if not any_pair:
        print("  no mob has been fought at two different levels of any predictor yet.")
        print("  Fight the SAME mob with a stat toggled both ways -- that pair is the")
        print("  whole experiment; unpaired data cannot separate our stat from its defence.")


def main():
    sw, at = load(P_SWINGS), load(P_ATTEMPTS)
    identifiability(sw, "damage rows")
    identifiability(at, "attempt rows")
    damage_by_mob(sw)
    hit_rate(at)
    paired(sw, 'damage per swing', 'dmg')
    paired(at, 'hit rate', 'hit')
    weapon_coverage(sw + at)
    print("\n=== NEXT ===")
    if not at:
        print("  * run the bot so attempts.csv fills (required for hit chance)")
    print("  * grind for volume at the current config, then change exactly ONE stat and")
    print("    grind the SAME mobs again -- that paired comparison is what separates our")
    print("    offense from the mob's defence.")


if __name__ == "__main__":
    main()
