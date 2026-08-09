#!/usr/bin/env python
"""
build_base_curve.py — Recover the deterministic per-level BASE stat schedule for
each base class from the archive.org character scrape (user_pages/chars.csv).

Key findings this pipeline rests on (see MEMORY nexustk-archive-userpages-scrape):
  * Archive pages show BASE stats (no item bonuses) -> the value at (class,level)
    is a deterministic function; within-cell CV is ~0-3%.
  * The variance that exists is NOT per-character RNG spread, it is two overlays:
      - might/grace/will: a low "trailing" population (undercapped / point-loss)
        BELOW a hard ceiling, plus HIGH exp-sold outliers (>= level+ , the 100s)
        that unlock at level 90.
      - vita/mana: NO low trail (fully deterministic); contamination is purely
        HIGH (exp-sold HP, and from ~lvl96 up the il-san HP explosion).
  * Level 99 is the display cap where il-san characters hide (HP into the millions)
    -> unusable. 90..98 is a contamination GRADIENT, salvaged per-cell by pruning.

Two prune strategies, one per stat family:
  vita/mana  -> monotone-trend gate: fit local slope on a trusted band, keep only
                records within TREND_TOL of the extrapolated expectation. Value =
                median of survivors. (Physical prior: HP is smooth & monotone; a
                +2900/level jump is impossible, so the 10k-plateau is rejected.)
  m/g/w      -> ceiling/mode: drop HIGH exp-sold outliers, the surviving cluster's
                MODE is the true formula ceiling; fraction below the ceiling is the
                empirical "loss rate".

Outputs (scraped_nexus_data/artifacts/user_pages/):
  base_curve.csv        class,level,stat,value,kind,n,n_pruned,loss_rate
  base_curve_rtk_diff.csv   where our recovered ceiling disagrees with onLevel.lua
Run:  python re/build_base_curve.py
"""
import csv, collections, statistics as st, os

HERE = os.path.dirname(os.path.abspath(__file__))
# Same resolution as archive_scrape.py -- data lives in scraped_nexus_data, not here.
ARCHIVE = os.environ.get("NEXUS_ARCHIVE") or os.path.normpath(
    os.path.join(HERE, "..", "..", "scraped_nexus_data", "artifacts", "user_pages"))
CHARS = os.path.join(ARCHIVE, "chars.csv")
OUT_CURVE = os.path.join(ARCHIVE, "base_curve.csv")
OUT_DIFF = os.path.join(ARCHIVE, "base_curve_rtk_diff.csv")

CLASSES = ["Warrior", "Rogue", "Mage", "Poet"]
KEY = {"Warrior": "might", "Rogue": "grace", "Mage": "will", "Poet": "will"}
VITA_STATS = ["vita", "mana"]        # monotone-trend gate
POINT_STATS = ["might", "grace", "will"]  # ceiling/mode

LO_LEVEL = 10        # below this, archive coverage is empty
HI_LEVEL = 98        # 99 is il-san-contaminated, always dropped
TREND_TOL = 0.06     # vita/mana: keep within +/-6% of extrapolated trend
TREND_FIT = (78, 95) # levels used to establish the local slope for extrapolation


def toi(x):
    try:
        return int(x)
    except (TypeError, ValueError):
        return None


def load():
    rows = list(csv.DictReader(open(CHARS, encoding="utf-8")))
    data = collections.defaultdict(lambda: collections.defaultdict(list))  # [class][stat] -> (level,val)
    for r in rows:
        c = r["class"]
        l = toi(r["level"])
        if c not in CLASSES or l is None:
            continue
        for s in VITA_STATS + POINT_STATS:
            v = toi(r[s])
            if v and v > 0:
                data[c][s].append((l, v))
    return data


def local_slope(pairs, lo, hi):
    """Least-squares slope/intercept on per-level medians within [lo,hi]."""
    bylvl = collections.defaultdict(list)
    for l, v in pairs:
        if lo <= l <= hi:
            bylvl[l].append(v)
    pts = [(l, st.median(vs)) for l, vs in bylvl.items()]
    if len(pts) < 3:
        return None, None
    n = len(pts)
    mx = sum(l for l, _ in pts) / n
    my = sum(m for _, m in pts) / n
    sxx = sum((l - mx) ** 2 for l, _ in pts)
    sxy = sum((l - mx) * (m - my) for l, m in pts)
    if not sxx:
        return None, None
    b = sxy / sxx
    return my - b * mx, b


def prune_vita(pairs):
    """Monotone-trend gate. Returns {level: (value, n_kept, n_pruned)}."""
    a, b = local_slope(pairs, *TREND_FIT)
    bylvl = collections.defaultdict(list)
    for l, v in pairs:
        bylvl[l].append(v)
    out = {}
    for l in range(LO_LEVEL, HI_LEVEL + 1):
        raw = bylvl.get(l, [])
        if not raw:
            continue
        if a is not None and l >= 88:  # only gate near/above the contamination onset
            exp = a + b * l
            kept = [v for v in raw if exp > 0 and abs(v - exp) / exp <= TREND_TOL]
        else:
            # below 88: deterministic & clean, but still drop gross highs via MAD
            med = st.median(raw)
            mad = st.median([abs(v - med) for v in raw]) or 1
            kept = [v for v in raw if abs(v - med) <= 6 * mad]
        if kept:
            out[l] = (st.median(kept), len(kept), len(raw) - len(kept))
    return out


def prune_point(pairs, is_key):
    """Ceiling = TOP of the clean cluster (NOT the mode -- at high levels the
    undercapped 'unlucky' tail outnumbers capped chars, so the mode drifts below
    the true ceiling). Exp-selling (unlocks lvl90) pushes stats ABOVE the natural
    ceiling, so we drop high outliers first, then take the max of what remains.
    Returns {level: (ceiling, n_kept, n_pruned, loss_rate)}."""
    bylvl = collections.defaultdict(list)
    for l, v in pairs:
        bylvl[l].append(v)
    # trend for secondaries from the fully clean pre-exp-sell band (no lvl90+),
    # used to gate the contaminated 88+ cells against an external reference.
    hi_ceil = None if is_key else local_slope(pairs, 60, 89)
    if hi_ceil == (None, None):
        hi_ceil = None
    out = {}
    for l in range(LO_LEVEL, HI_LEVEL + 1):
        raw = bylvl.get(l, [])
        if not raw:
            continue
        if is_key:
            # PROVEN law: key stat's natural ceiling == level. Anything above is
            # exp-sold (the lvl90+ 100s); prune it. Ceiling is then max(<=level).
            kept = [v for v in raw if v <= l]
            if not kept:            # cell is entirely exp-sold caps (rare, high lvl)
                kept = raw
            ceiling = min(max(kept), l)
        elif l >= 88 and hi_ceil is not None:
            # Near/above the exp-sell onset the cell median is itself poisoned, so
            # we CANNOT fence against it. Gate against the smooth trend extrapolated
            # from the clean pre-90 band instead: keep the capped + undercapped
            # (v <= trend+2) and drop exp-sold highs (the 100s). Undercapped below
            # the trend are legitimate and kept (no lower bound).
            exp = hi_ceil[0] + hi_ceil[1] * l
            kept = [v for v in raw if v <= exp + 2]
            if not kept:
                continue            # cell wholly exp-sold/il-san -> unrecoverable, omit
            ceiling = max(kept)
        else:
            # clean pre-88 region: deterministic; MAD fence around the median.
            med = st.median(raw)
            mad = st.median([abs(v - med) for v in raw]) or 1
            kept = [v for v in raw if v <= med + 4 * mad]
            ceiling = max(kept)
        below = sum(1 for v in kept if v < ceiling)
        loss = below / len(kept)
        out[l] = (ceiling, len(kept), len(raw) - len(kept), loss)
    return out


# ---- RTK onLevel.lua schedule for the diff (reuse the scraper's canonical impl) ----
try:
    from archive_scrape import rtk_expected  # (might, grace, will) cumulative
except Exception:
    rtk_expected = None


def monotone_envelope(cells):
    """Base stat is physically non-decreasing in level; a downward dip is a
    thin-sample miss (a level where no capped char was observed). Walk up from
    the lowest covered level and raise each value to the running max. Only ever
    RAISES a value, never lowers -- so a genuine plateau is preserved, a dip is
    lifted to the prior true value. Returns {level: repaired_value}."""
    out = {}
    run = None
    for l in sorted(cells):
        v = cells[l]
        run = v if run is None else max(run, v)
        out[l] = run
    return out


def main():
    data = load()
    rows_out = []
    for c in CLASSES:
        for s in VITA_STATS:
            pr = prune_vita(data[c][s])
            env = monotone_envelope({l: v for l, (v, _, _) in pr.items()})
            for l in sorted(pr):
                val, nk, npd = pr[l]
                rows_out.append([c, l, s, round(env[l]), "trend", nk, npd, ""])
        for s in POINT_STATS:
            is_key = (KEY[c] == s)
            pr = prune_point(data[c][s], is_key)
            env = monotone_envelope({l: ceil for l, (ceil, _, _, _) in pr.items()})
            # key stat can never exceed level even after enveloping
            for l in sorted(pr):
                ceil, nk, npd, loss = pr[l]
                v = min(env[l], l) if is_key else env[l]
                rows_out.append([c, l, s, v, "ceiling", nk, npd, f"{loss:.2f}"])

    with open(OUT_CURVE, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["class", "level", "stat", "value", "kind", "n", "n_pruned", "loss_rate"])
        w.writerows(rows_out)

    # ---- RTK diff: recovered ceiling vs onLevel.lua, for might/grace/will ----
    if rtk_expected is not None:
        recov = {(c, int(l), s): int(v) for c, l, s, v, *_ in rows_out}
        diff_rows = []
        idx = {"might": 0, "grace": 1, "will": 2}
        for c in CLASSES:
            for l in range(LO_LEVEL, HI_LEVEL + 1):
                rtk = rtk_expected(c, l)
                for s in POINT_STATS:
                    live = recov.get((c, l, s))
                    if live is None:
                        continue
                    exp = rtk[idx[s]]
                    if live != exp:
                        diff_rows.append([c, l, s, live, exp, live - exp])
        with open(OUT_DIFF, "w", newline="", encoding="utf-8") as f:
            w = csv.writer(f)
            w.writerow(["class", "level", "stat", "recovered", "rtk", "delta"])
            w.writerows(diff_rows)
        # divergence summary: mean delta per class/stat
        agg = collections.defaultdict(list)
        for c, l, s, live, exp, d in diff_rows:
            agg[(c, s)].append(d)
        print(f"\nwrote {OUT_DIFF}: {len(diff_rows)} disagreeing cells")
        print("mean (recovered - RTK) where they differ:")
        for c in CLASSES:
            parts = []
            for s in POINT_STATS:
                ds = agg[(c, s)]
                parts.append(f"{s}:{'+' if ds and st.mean(ds)>=0 else ''}{st.mean(ds):.1f}(n{len(ds)})" if ds else f"{s}:=")
            print(f"  {c:8} " + "  ".join(parts))

    # summary to stdout
    print(f"\nwrote {OUT_CURVE}: {len(rows_out)} rows")
    cov = collections.defaultdict(set)
    for c, l, s, *_ in rows_out:
        cov[(c, s)].add(l)
    print("\ncoverage (distinct levels, LO..98) per class/stat:")
    for c in CLASSES:
        parts = []
        for s in POINT_STATS + VITA_STATS:
            lv = cov[(c, s)]
            parts.append(f"{s}:{len(lv)}({min(lv) if lv else '-'}..{max(lv) if lv else '-'})")
        print(f"  {c:8} " + "  ".join(parts))

    # how far up does each stat stay usable after prune?
    print("\nhighest level retained per class/stat (post-prune):")
    for c in CLASSES:
        hi = {s: (max(cov[(c, s)]) if cov[(c, s)] else None) for s in POINT_STATS + VITA_STATS}
        print(f"  {c:8} " + "  ".join(f"{s}:{hi[s]}" for s in POINT_STATS + VITA_STATS))


if __name__ == "__main__":
    main()
