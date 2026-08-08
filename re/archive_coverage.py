#!/usr/bin/env python
"""
Coverage report for the archive scrape: how much of the (class, level) space we actually
have, and how much of what we downloaded is unusable and why.

Not every page is usable. A page drops out at one of three gates:
  1. no `Vital statistics` section at all (the player hid their stats)
  2. stats present but no path mark in the Legend -> class unknown
  3. level 99 -> contaminated by permanent endgame/quest stat bonuses

Run any time mid-crawl:  python re/archive_coverage.py
"""
import os, re, csv, json, gzip, collections

D = os.path.dirname(os.path.abspath(__file__))
A = os.path.join(D, "archive")
CACHE = os.path.join(A, "cache")
CLASSES = ["Warrior", "Rogue", "Mage", "Poet"]
BANDS = [(1, 9), (10, 19), (20, 29), (30, 39), (40, 49),
         (50, 59), (60, 69), (70, 79), (80, 89), (90, 98)]


def band(lv):
    for lo, hi in BANDS:
        if lo <= lv <= hi:
            return f"{lo}-{hi}"
    return "99"


def main():
    idx = {}
    for l in open(os.path.join(A, "index.jsonl"), encoding="utf-8"):
        if l.strip():
            r = json.loads(l)
            idx[r["digest"]] = r

    # ---- gate 1: audit the raw cache for a stats section
    cached = has_stats = no_stats = listing = 0
    for f in os.listdir(CACHE):
        dg = f[:-3]
        r = idx.get(dg)
        if not r or len(r["name"]) == 1:
            listing += 1
            continue
        cached += 1
        try:
            h = gzip.open(os.path.join(CACHE, f), "rb").read().decode("utf-8", "replace")
        except Exception:
            continue
        if "Vital statistics" in h:
            has_stats += 1
        else:
            no_stats += 1

    rows = list(csv.DictReader(open(os.path.join(A, "chars.csv"), encoding="utf-8")))
    # apply the learned subpath/rank-title -> class map, same as the analyze stage
    import importlib.util
    spec = importlib.util.spec_from_file_location("ascr", os.path.join(D, "archive_scrape.py"))
    ascr = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(ascr)
    tm = ascr.load_titlemap()
    from_legend = sum(1 for r in rows if r["class"])
    recovered = 0
    for r in rows:
        if not r["class"]:
            hit = tm.get(ascr.title_prefix(r.get("name"), r.get("bare_name") or ""))
            if hit:
                r["class"] = hit["class"]
                r["class_src"] = "title"
                recovered += 1
    known = [r for r in rows if r["class"]]
    unknown = [r for r in rows if not r["class"]]

    def lv(r):
        try:
            return int(r["level"])
        except Exception:
            return -1

    usable = [r for r in known if 1 <= lv(r) <= 98]
    l99 = [r for r in known if lv(r) == 99]

    print("=" * 66)
    print("PIPELINE YIELD")
    print("=" * 66)
    print(f"  cached character pages      : {cached:,}   (+{listing:,} directory pages ignored)")
    print(f"    has 'Vital statistics'    : {has_stats:,}  ({has_stats/max(cached,1)*100:.0f}%)")
    print(f"    stats HIDDEN by player    : {no_stats:,}  ({no_stats/max(cached,1)*100:.0f}%)  <- gate 1")
    print(f"  parsed rows in chars.csv    : {len(rows):,}")
    print(f"    class known (Legend mark)  : {len(known):,}  ({len(known)/max(len(rows),1)*100:.0f}%)")
    print(f"    class UNKNOWN              : {len(unknown):,}  ({len(unknown)/max(len(rows),1)*100:.0f}%)  <- gate 2")
    print(f"  level 99 (unusable)          : {len(l99):,}  ({len(l99)/max(len(known),1)*100:.0f}% of class-known)  <- gate 3")
    print(f"  >>> USABLE (class + lv 1-98) : {len(usable):,}  "
          f"= {len(usable)/max(cached,1)*100:.1f}% of pages downloaded")

    print()
    print("=" * 66)
    print("CLASS DISTRIBUTION (level 1-98, class known)")
    print("=" * 66)
    cc = collections.Counter(r["class"] for r in usable)
    for c in CLASSES:
        n = cc.get(c, 0)
        bar = "#" * int(40 * n / max(max(cc.values()) if cc else 1, 1))
        print(f"  {c:8s} {n:5,}  {bar}")

    print()
    print("=" * 66)
    print("LEVEL DISTRIBUTION (class known, incl. 99 for contrast)")
    print("=" * 66)
    lb = collections.Counter(band(lv(r)) for r in known if lv(r) >= 1)
    order = [f"{lo}-{hi}" for lo, hi in BANDS] + ["99"]
    mx = max(lb.values()) if lb else 1
    for b in order:
        n = lb.get(b, 0)
        bar = "#" * int(44 * n / mx)
        print(f"  {b:>6s} {n:5,}  {bar}")

    print()
    print("=" * 66)
    print("CLASS x LEVEL-BAND MATRIX (usable samples)")
    print("=" * 66)
    m = collections.Counter((r["class"], band(lv(r))) for r in usable)
    hdr = "  band  " + "".join(f"{c[:7]:>9s}" for c in CLASSES) + "     total"
    print(hdr)
    for lo, hi in BANDS:
        b = f"{lo}-{hi}"
        cells = [m.get((c, b), 0) for c in CLASSES]
        print(f"  {b:>6s}" + "".join(f"{x:>9,}" for x in cells) + f"{sum(cells):>10,}")
    tot = [sum(m.get((c, f'{lo}-{hi}'), 0) for lo, hi in BANDS) for c in CLASSES]
    print("  " + "-" * 54)
    print(f"  {'total':>6s}" + "".join(f"{x:>9,}" for x in tot) + f"{sum(tot):>10,}")

    # ---- per-cell depth: how many (class, exact level) cells have enough samples
    print()
    print("=" * 66)
    print("PER-CELL DEPTH  (class x EXACT level, 4 x 98 = 392 cells)")
    print("=" * 66)
    cell = collections.Counter((r["class"], lv(r)) for r in usable)
    bare_cell = collections.Counter()
    slots = ["eq_weapon", "eq_armor", "eq_helm", "eq_left_hand", "eq_right_hand"]
    for r in usable:
        if all(not (r.get(s) or "").strip() for s in slots):
            bare_cell[(r["class"], lv(r))] += 1
    for t in (1, 2, 3, 5, 10):
        n = sum(1 for v in cell.values() if v >= t)
        print(f"  cells with >= {t:2d} samples : {n:3d} / 392  ({n/392*100:.0f}%)")
    print(f"  cells with a BARE (fully unequipped) sample : {len(bare_cell):3d} / 392"
          f"   <- these give base directly")
    print(f"  total bare samples so far                   : {sum(bare_cell.values()):,}")

    # ---- projection to tier-0 completion
    per = collections.defaultdict(list)
    for r in idx.values():
        if len(r["name"]) > 1:
            per[r["name"]].append(r)
    tier0 = len(per)
    have = len({f[:-3] for f in os.listdir(CACHE)} &
               {sorted(v, key=lambda x: x["ts"])[0]["digest"] for v in per.values()})
    if have:
        scale = tier0 / have
        print()
        print("=" * 66)
        print(f"PROJECTION at tier-0 completion ({have:,}/{tier0:,} done, x{scale:.1f})")
        print("=" * 66)
        print(f"  projected usable samples : ~{int(len(usable)*scale):,}")
        for c in CLASSES:
            print(f"    {c:8s} ~{int(cc.get(c,0)*scale):,}")
        print(f"  projected samples per (class, level) cell : "
              f"~{int(len(usable)*scale/392)}")


if __name__ == "__main__":
    main()
