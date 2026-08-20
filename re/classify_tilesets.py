#!/usr/bin/env python3
"""Rank every TK*.map by how closely its GROUND tileset matches a reference map.

Motivation: finding a map that "looks like" another one (same grass, same path,
same edge transitions) by eye means opening hundreds of maps in an editor. The
ground layer is a flat array of frame indices, so a histogram over those indices
is a cheap visual fingerprint -- two maps drawn from the same tileset share the
same ids, and in particular the same *transition* ids (the corner/edge pieces
that join path to grass), which are far more distinctive than the bulk fill.

Deliberately reads the file as len//4 cells rather than xs*ys from map_index.csv:
the histogram doesn't care about row layout, so this sidesteps the maps whose
recorded dimensions are wrong.

Usage:
    python re/classify_tilesets.py [reference_id] [--top N] [--min-cells N]
"""
import argparse
import collections
import csv
import os
import re
import sys
from _paths import CLIENT, CLIENT5

SEARCH_DIRS = [
    os.environ.get("P1998_MAPS"),
    os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "data", "maps"),
    str(CLIENT / "Maps"),
    str(CLIENT5 / "Maps"),
]

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
INDEX = os.path.join(REPO, "data", "game-data", "map_index.csv")


def map_files():
    """id -> path, union over every search dir, first dir wins (same precedence as
    MapData.SearchDirs). game-data/maps holds only a couple of overrides, so a single
    "first dir that has any TK*.map" rule would hide the full client set."""
    out = {}
    dirs = []
    for d in SEARCH_DIRS:
        if not d or not os.path.isdir(d):
            continue
        hit = False
        for fn in os.listdir(d):
            m = re.fullmatch(r"TK(\d+)\.map", fn, re.IGNORECASE)
            if m:
                hit = True
                out.setdefault(int(m.group(1)), os.path.join(d, fn))
        if hit:
            dirs.append(d)
    if not out:
        sys.exit("no TK*.map files found in any search dir")
    return out, dirs


def names():
    out = {}
    if os.path.exists(INDEX):
        with open(INDEX, newline="", encoding="utf-8", errors="replace") as f:
            for row in csv.DictReader(f):
                try:
                    out[int(row["id"])] = row["name"]
                except (ValueError, KeyError):
                    pass
    return out


def ground_hist(path):
    """Ground frame index -> count. Low 14 bits of the first u16 of each 4-byte cell."""
    d = open(path, "rb").read()
    h = collections.Counter()
    for i in range(len(d) // 4):
        h[(d[i * 4] | (d[i * 4 + 1] << 8)) & 0x3FFF] += 1
    return h


def score(ref, cand):
    """Similarity of a candidate histogram to the reference.

    Two independent halves, because either alone gives false positives:
      coverage -- what fraction of the CANDIDATE is drawn from the ref's palette
                  (a map that's 95% our tiles looks like our map; one that's 5%
                  merely contains a patch of it)
      recall   -- what fraction of the REFERENCE's tile *mass* the candidate uses
                  (weighted, so missing tile 43 costs far more than missing a
                  one-off decoration)
    Reported separately as well as combined so a "same palette, different mix"
    map is still visible in the output.
    """
    ref_total = sum(ref.values())
    cand_total = sum(cand.values()) or 1
    coverage = sum(c for t, c in cand.items() if t in ref) / cand_total
    recall = sum(w for t, w in ref.items() if t in cand) / ref_total
    # distinct transition ids shared (the strongest visual tell)
    shared = len(set(ref) & set(cand))
    return coverage, recall, shared


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("reference", nargs="?", type=int, default=4711)
    ap.add_argument("--top", type=int, default=40)
    ap.add_argument("--min-cells", type=int, default=64, help="skip tiny/stub maps")
    ap.add_argument("--csv", help="write full ranking here")
    args = ap.parse_args()

    files, dirs = map_files()
    nm = names()
    if args.reference not in files:
        sys.exit(f"reference TK{args.reference}.map not found in {dirs}")
    ref = ground_hist(files[args.reference])
    ref_total = sum(ref.values())

    print(f"map dirs: {dirs}  ({len(files)} maps)")
    print(f"reference: TK{args.reference} ({nm.get(args.reference, '?')}) "
          f"{ref_total} cells, {len(ref)} distinct ground tiles")
    print("  palette: " + ", ".join(f"{t}({c})" for t, c in ref.most_common()))
    print()

    rows = []
    for mid, path in files.items():
        if mid == args.reference:
            continue
        try:
            h = ground_hist(path)
        except OSError:
            continue
        total = sum(h.values())
        if total < args.min_cells:
            continue
        cov, rec, shared = score(ref, h)
        rows.append((cov * rec, cov, rec, shared, mid, total, nm.get(mid, "")))

    rows.sort(reverse=True)

    if args.csv:
        with open(args.csv, "w", newline="", encoding="utf-8") as f:
            w = csv.writer(f)
            w.writerow(["id", "name", "cells", "combined", "coverage", "recall", "shared_tiles"])
            for comb, cov, rec, sh, mid, total, name in rows:
                w.writerow([mid, name, total, f"{comb:.4f}", f"{cov:.4f}", f"{rec:.4f}", sh])
        print(f"full ranking -> {args.csv}\n")

    print(f"{'id':>6}  {'cells':>7}  {'comb':>6}  {'cover':>6}  {'recall':>6}  {'tiles':>5}  name")
    for comb, cov, rec, sh, mid, total, name in rows[:args.top]:
        print(f"{mid:>6}  {total:>7}  {comb:>6.3f}  {cov:>6.3f}  {rec:>6.3f}  {sh:>5}  {name}")


if __name__ == "__main__":
    main()
