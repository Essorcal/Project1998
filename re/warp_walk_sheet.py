#!/usr/bin/env python
r"""Generate a live-walkthrough coverage sheet for maps whose warps the dumps never wired.

The point is NOT to hand-record every door — capture_warps.py reads a Wireshark pcap and emits
the (map,x,y)->(map,x,y) pairs for you. This sheet is the ROUTE and the CHECKLIST: it tells you
which maps still have an unwired exit to walk through, groups them into clusters you can sweep in
one sitting, and (for hub rooms) prints a door table you can fill by eye as a backup / as the
--pair assignments if you'd rather skip the capture for a small cluster.

Coverage closes the loop: point --covered at a capture's visited-map list (capture_warps.py
--visited, or a client Maps\ .cmp cache dir) and the sheet reprints with every visited map ticked
and a "N of M exits still unwalked" tally, so a 500-map sweep has a progress bar instead of a
vibe.

USAGE
    python re/warp_walk_sheet.py --cluster 4600-4618            # one cluster, to a file + stdout
    python re/warp_walk_sheet.py --all                          # every cluster -> re/warp_walk_sheet.md
    python re/warp_walk_sheet.py --all --covered re/warp_visited.txt   # + tick what you've walked
    python re/warp_walk_sheet.py --all --covered "C:\...\NexusTK\Maps" # .cmp cache = visited set
"""
import argparse, os, glob, re as _re, sys
import propose_warps as pw

HERE = os.path.dirname(os.path.abspath(__file__))

# The sheet is UTF-8 (arrows in the door tables, en dashes in the headings) and the FILE is written
# as UTF-8 explicitly. The preview echoed to stdout is not: on a stock Windows console that is cp1252,
# and printing the first arrow raised UnicodeEncodeError -- after the sheet had already been written,
# so the tool did its job and then died with a traceback and a non-zero exit. Ask for UTF-8 out, and
# fall back to replacing what the console cannot draw rather than failing.
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except (AttributeError, OSError):       # not a reconfigurable stream (redirected/wrapped)
    pass


def visited_from(path):
    """A set of map ids the player has already reached — from a capture_warps --visited file
    (one id per line) or straight off a client Maps\\ dir (TK######.cmp / .map filenames)."""
    ids = set()
    if not path:
        return ids
    if os.path.isdir(path):
        for f in glob.glob(os.path.join(path, "TK*.*")):
            m = _re.search(r"TK0*(\d+)\.", os.path.basename(f))
            if m:
                ids.add(int(m.group(1)))
    else:
        for line in open(path, encoding="utf-8"):
            line = line.strip().split(",")[0].split("#")[0].strip()
            if line.isdigit():
                ids.add(int(line))
    return ids


def clusters_from_scan(reg, max_run):
    """Reproduce cmd_scan's clustering: orphan maps with terrain, split on id gaps > 6, each
    cluster widened to include any wired maps in its id span (they're the likely anchors)."""
    index, names, warp_src, wired, claimed, flagged, is_door, _ = reg
    have = lambda m: os.path.exists(os.path.join(pw.MAPS_DIR, f"TK{m}.map"))
    orphans = sorted(m for m in names if m not in wired and m in index and have(m))
    groups, cur = [], []
    for m in orphans:
        if cur and m - cur[-1] > 6:
            groups.append(cur); cur = []
        cur.append(m)
    if cur:
        groups.append(cur)
    out = []
    for g in groups:
        span = sorted(set(g) | {m for m in names
                                if g[0] <= m <= g[-1] and m in wired and m in index and have(m)})
        out.append((g, span))
    return out


def exits_of(reg, mid, max_run):
    index, names, warp_src, wired, claimed, flagged, is_door, _ = reg
    geo, ex = pw.find_exits(mid, index, warp_src, claimed, is_door, max_run)
    return (ex[0] if ex else []), (ex[1] if ex else [])


def render_cluster(reg, span_pair, max_run, visited):
    index, names, warp_src, wired, claimed, flagged, is_door, _ = reg
    orphans, span = span_pair
    lo, hi = span[0], span[-1]
    lines = [f"## Cluster {lo}–{hi}  ({len(orphans)} unwired, {len(span)} in span)\n"]

    total_exits = walked_maps = 0
    map_rows, hub_tables = [], []
    for m in span:
        e_list, notes = exits_of(reg, m, max_run)
        if m not in wired and not e_list:
            continue                                    # orphan with no exit to walk — nothing to do
        total_exits += len(e_list)
        done = m in visited
        walked_maps += 1 if (done and m not in wired) else 0
        tick = "x" if done else " "
        tags = []
        if m in wired: tags.append("wired-anchor")
        if m in flagged: tags.append("SCRIPTED — verify by hand")
        tag = f"  _{', '.join(tags)}_" if tags else ""
        map_rows.append(f"- [{tick}] **{m}** {names[m]} — {len(e_list)} exit(s){tag}")
        for e in e_list:
            a, b = e.tiles[0], e.tiles[-1]
            span_s = f"({a[0]},{a[1]})" if a == b else f"({a[0]},{a[1]})–({b[0]},{b[1]})"
            map_rows.append(f"    - `{e.label}` {e.kind}/{e.side} {span_s} w{len(e.tiles)}")
        for n in notes:
            map_rows.append(f"    - _note: {n}_")
        if len(e_list) >= 2:                            # a hub: worth a fill-by-eye table
            hub_tables.append((m, names[m], e_list))

    lines += map_rows
    for m, nm, e_list in hub_tables:
        lines.append(f"\n### {m} {nm} — door assignments (fill by walking, or let the capture do it)")
        lines.append("| exit | tile | side | → room you land in | → its map id |")
        lines.append("|---|---|---|---|---|")
        for e in e_list:
            a = e.tiles[0]
            lines.append(f"| `{e.label}` | ({a[0]},{a[1]}) | {e.side} | | |")
        labels = " ".join(f"--pair {m}.{e.label}=<destMap>.<destExit>" for e in e_list)
        lines.append(f"\n`python re/propose_warps.py --cluster {reg_span_arg(span)} {labels}`")

    pct = f"{walked_maps}/{len([m for m in orphans if exits_of(reg, m, max_run)[0]])} unwired maps walked"
    lines.insert(1, f"_{pct}, {total_exits} candidate exits_\n")
    return "\n".join(lines) + "\n"


def reg_span_arg(span):
    return f"{span[0]}-{span[-1]}"


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    g = ap.add_mutually_exclusive_group(required=True)
    g.add_argument("--cluster", help="one cluster id span, e.g. 4600-4618")
    g.add_argument("--all", action="store_true", help="every cluster in the registry")
    ap.add_argument("--covered", help="file of visited map ids, or a client Maps\\ .cmp/.map dir")
    ap.add_argument("--max-run", type=int, default=8)
    ap.add_argument("--out", help="output markdown path")
    args = ap.parse_args()

    reg = pw.load_registry()
    visited = visited_from(args.covered)

    if args.cluster:
        ids = pw.parse_cluster(args.cluster)
        index, names = reg[0], reg[1]
        span = sorted(m for m in ids if m in names)
        orphans = [m for m in span if m not in reg[3]]
        body = render_cluster(reg, (orphans, span), args.max_run, visited)
        out = args.out or os.path.join(HERE, f"warp_walk_{args.cluster}.md")
        head = f"# Warp walk sheet — cluster {args.cluster}\n\n"
    else:
        cls = clusters_from_scan(reg, args.max_run)
        # only clusters that actually have something to walk
        blocks = []
        grand_exits = grand_maps = grand_walked = 0
        for pair in cls:
            has = any(exits_of(reg, m, args.max_run)[0] for m in pair[1])
            if not has:
                continue
            blocks.append(render_cluster(reg, pair, args.max_run, visited))
            for m in pair[0]:
                el = exits_of(reg, m, args.max_run)[0]
                if el:
                    grand_maps += 1
                    grand_exits += len(el)
                    grand_walked += 1 if m in visited else 0
        head = ("# Warp walk sheet — all clusters\n\n"
                f"**{grand_walked}/{grand_maps} unwired maps walked, "
                f"{grand_exits} candidate exits across {len(blocks)} clusters.**\n\n"
                "Run Wireshark while you walk, then `python re/capture_warps.py cap.pcapng`. "
                "This sheet just says where to go and what's left.\n\n")
        body = "\n".join(blocks)
        out = args.out or os.path.join(HERE, "warp_walk_sheet.md")

    with open(out, "w", encoding="utf-8") as f:
        f.write(head + body)
    print(head + body[:2000] + ("\n... (full sheet in %s)" % out if len(body) > 2000 else ""))
    print(f"\n-> {out}")


if __name__ == "__main__":
    main()
