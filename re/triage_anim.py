"""Triage every spell_effects.csv / nexusatlas animation disagreement into: keep-CSV, take-atlas, or
needs-a-human.

The two sources have different resolutions, so a disagreement is NOT automatically an error:
  * the CSV value comes from RTK, whose ids are proven IDENTITY with 4.95's for 342 rows
  * the atlas gif marks a whole VISUAL FAMILY (10 mage spells share ignite.gif)
So when the CSV points at a near-twin of the atlas-matched effect (the client keeps sibling variants
like the bolts #26/#27/#29), the CSV is simply the finer answer and wins. When the CSV points at
something visually unrelated, one of them is wrong and a person has to look.

Rules, in order:
  1. CSV value out of range (client has only 1..121)      -> TAKE ATLAS (csv cannot render)
  2. CSV blank                                            -> TAKE ATLAS (fills a gap)
  3. atlas says none.gif and csv has a value              -> REVIEW (atlas may just lack a capture)
  4. similarity(csv effect, atlas effect) >= SIB          -> KEEP CSV (sibling variant, finer)
  5. otherwise                                            -> REVIEW

Usage:
  python triage_anim.py                 # print the triage
  python triage_anim.py sheets          # render a comparison image per REVIEW case
"""
import csv
import os
import sys

import match_spell_fx as M
import render_effects as R
import apply_spell_anim as AP

SIB = 0.62  # similarity above which two client effects are treated as variants of one another


def effect_descriptors():
    return M.client_descriptors()


def sim(cd, a, b):
    if a not in cd or b not in cd:
        return None
    s = M.score(cd[a], cd[b])
    return s[0]


def build():
    rows = list(csv.DictReader(open(AP.CSV, encoding="utf-8-sig")))
    by_key = {r["key"]: r for r in rows}
    atlas = AP.load_atlas()
    cd = effect_descriptors()

    out, seen = [], set()
    for a in atlas:
        cls = AP.CLASSES.get(a.get("class", ""), "")
        nm = a.get("aligned_name") or a["name"]
        gif = a["gif"][:-4]
        row, how = AP.resolve(nm, cls, by_key)
        targets = [row] if row is not None else AP.resolve_multi(nm, cls, by_key)
        for row in targets:
            if (row["key"], gif) in seen:
                continue
            seen.add((row["key"], gif))
            emit(out, cd, row, nm, cls, gif)
    return out


def emit(out, cd, row, nm, cls, gif):
    """Classify one (spell, gif) pair and append a verdict."""
    cur = (row["animation"] or "").strip()
    curi = int(cur) if cur.lstrip("-").isdigit() else None

    if gif == AP.NO_ANIM:
        if cur and cur != "-1":
            out.append((row["key"], nm, cls, gif, cur, "", "REVIEW", "atlas says no animation"))
        elif not cur:
            # blank means "unknown"; the atlas positively says this spell has NO animation, so
            # record that as -1 instead of leaving it indistinguishable from a gap.
            out.append((row["key"], nm, cls, gif, cur, "-1", "TAKE-ATLAS",
                        "atlas none.gif -> confirmed no animation"))
        return
    if gif not in AP.GIF_EFFECT:
        out.append((row["key"], nm, cls, gif, cur, "", "REVIEW", "gif never archived"))
        return

    wire = AP.GIF_EFFECT[gif] + 1
    if cur == str(wire):
        return
    if curi is None or not cur:
        out.append((row["key"], nm, cls, gif, cur, wire, "TAKE-ATLAS", "csv blank"))
    elif curi < 1 or curi > 121:
        out.append((row["key"], nm, cls, gif, cur, wire, "TAKE-ATLAS",
                    f"csv {curi} outside client range 1..121"))
    else:
        s = sim(cd, curi - 1, wire - 1)
        if s is not None and s >= SIB:
            out.append((row["key"], nm, cls, gif, cur, wire, "KEEP-CSV",
                        f"#{curi - 1} vs #{wire - 1} similar {s:.2f}"))
        else:
            out.append((row["key"], nm, cls, gif, cur, wire, "REVIEW",
                        f"#{curi - 1} vs #{wire - 1} differ {s:.2f}" if s is not None
                        else "effect missing"))


def main():
    out = build()
    order = {"TAKE-ATLAS": 0, "REVIEW": 1, "KEEP-CSV": 2}
    counts = {}
    for r in out:
        counts[r[6]] = counts.get(r[6], 0) + 1
    print("triage:", counts, "\n")
    for verdict in ("TAKE-ATLAS", "REVIEW", "KEEP-CSV"):
        rs = [r for r in out if r[6] == verdict]
        if not rs:
            continue
        print(f"=== {verdict} ({len(rs)}) ===")
        for k, nm, cls, gif, cur, wire, _, why in sorted(rs):
            print(f"  {k:34s} csv={cur or '(blank)':>7s} atlas={str(wire):>4s} {gif:20s} {why}")
        print()
    with open(os.path.join(AP.FX, "triage.csv"), "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["key", "spell", "class", "gif", "csv_animation", "atlas_wire", "verdict", "why"])
        w.writerows(sorted(out, key=lambda r: (order[r[6]], r[0])))
    print("wrote", os.path.join(AP.FX, "triage.csv"))


if __name__ == "__main__":
    if "sheets" in sys.argv:
        import review_sheets
        review_sheets.main(build())
    else:
        main()
