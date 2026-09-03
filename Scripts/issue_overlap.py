#!/usr/bin/env python
"""
issue_overlap.py - which open Project1998 issues can run in parallel without touching the same files.

The parallel-sprint rule is "pick issues that share zero files". Every issue body from the refactor
analysis names its files, either as permalinks (.../blob/<sha>/Server/World.cs#L123) or as backticked
paths, so the pairing can be a lookup instead of a per-sprint derivation. This script pulls the open
issues once (cached), extracts the file set of each, resolves bare names like `World.cs` against the
checkout's `git ls-files`, and prints either the overlap matrix or, with --with N, every other issue
sorted into disjoint / overlapping relative to N.

    python Scripts/issue_overlap.py                 # matrix: every overlapping pair
    python Scripts/issue_overlap.py --with 30       # pairing candidates for #30
    python Scripts/issue_overlap.py --with 30 --with 36   # is {30,36} pairwise disjoint, and what else fits
    python Scripts/issue_overlap.py --files 30      # the extracted file set (check it when a body is odd)
    python Scripts/issue_overlap.py --refresh       # refetch instead of using the cache

Caveats the reader must keep in mind:
  * Bodies are pinned to the analysis tree (e4e0b9a). A later file move (e.g. #34 splitting Content.cs)
    is not reflected until someone edits the body. Treat "disjoint" as necessary, not sufficient, and
    still confirm the regions inside any file that appears in both, as sprint 2 did for World.cs.
  * "Depends on #N" / "Relates to #N" lines are shown because two disjoint issues can still be
    sequential by design.
  * Issues with an assignee are marked TAKEN; Board-Claim.ps1 is the authority on that, not this list.
"""
import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
import time
from collections import defaultdict

REPO = "project1998/Project1998"
CACHE = os.path.join(tempfile.gettempdir(), "p1998_open_issues.json")
CACHE_TTL = 6 * 3600
HERE = os.path.dirname(os.path.abspath(__file__))
CHECKOUT = os.path.dirname(HERE)

EXT = r"(?:cs|csv|lua|md|json|props|csproj|slnf|sln|ps1|py|bat|yml|yaml|html|js|css|txt|targets|config)"
PERMALINK = re.compile(r"/blob/[0-9a-f]{6,40}/([^\s)#\"']+)")
BACKTICK = re.compile(r"`([A-Za-z0-9_./\\-]+\." + EXT + r")(?:[:#][^`]*)?`")
PLAIN = re.compile(r"(?<![\w/.-])([A-Za-z][A-Za-z0-9_-]*(?:[/\\][A-Za-z0-9_.-]+)*\." + EXT + r")\b")
DIR_ONLY = re.compile(r"`((?:Server|Tests|Tools|MapEditor|IconStudio|LoginServer|game-data|re|Scripts|docs)/[A-Za-z0-9_./-]*)/?`")
RELATION = re.compile(r"(Depends on|Blocked by|Relates to|Related to|Child of|Parent of|Supersedes|See)\s+((?:#\d+(?:,?\s*(?:and\s+)?)?)+)", re.I)
NOISE = {"README.md", "AGENTS.md", "CONTRIBUTING.md"}  # mentioned everywhere, edited rarely


def sh(args, cwd=None):
    return subprocess.run(args, cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace")


def fetch_issues(refresh):
    if not refresh and os.path.exists(CACHE) and time.time() - os.path.getmtime(CACHE) < CACHE_TTL:
        with open(CACHE, encoding="utf-8") as f:
            return json.load(f)
    r = sh(["gh", "issue", "list", "-R", REPO, "--state", "open", "--limit", "300",
            "--json", "number,title,body,labels,assignees,url"])
    if r.returncode != 0:
        sys.exit("gh issue list failed: " + r.stderr.strip())
    issues = json.loads(r.stdout)
    with open(CACHE, "w", encoding="utf-8") as f:
        json.dump(issues, f)
    return issues


def tracked_files():
    r = sh(["git", "ls-files"], cwd=CHECKOUT)
    files = r.stdout.split("\n") if r.returncode == 0 else []
    by_base = defaultdict(list)
    for p in files:
        if p:
            by_base[os.path.basename(p)].append(p)
    return set(files), by_base


def extract(body, tracked, by_base):
    found, dirs = set(), set()
    for m in PERMALINK.finditer(body):
        found.add(m.group(1))
    for m in BACKTICK.finditer(body):
        found.add(m.group(1).replace("\\", "/"))
    for m in PLAIN.finditer(body):
        found.add(m.group(1).replace("\\", "/"))
    for m in DIR_ONLY.finditer(body):
        dirs.add(m.group(1).rstrip("/"))
    resolved = set()
    for p in found:
        base = os.path.basename(p)
        if base in NOISE:
            continue
        if p in tracked:
            resolved.add(p)
        elif "/" not in p and len(by_base.get(base, [])) == 1:
            resolved.add(by_base[base][0])
        elif "/" not in p and len(by_base.get(base, [])) > 1:
            resolved.add(base + " (ambiguous: " + ", ".join(sorted(by_base[base])[:3]) + ")")
        else:
            resolved.add(p + " (not in tree)")
    for d in dirs:
        resolved.add(d + "/ (whole dir)")
    return resolved


def touches(a, b):
    """Shared files, treating a whole-dir mention as covering every path under it."""
    shared = set(a & b)
    for x in a:
        if x.endswith("/ (whole dir)"):
            d = x[: -len("/ (whole dir)")] + "/"
            shared |= {y for y in b if y.startswith(d)}
    for y in b:
        if y.endswith("/ (whole dir)"):
            d = y[: -len("/ (whole dir)")] + "/"
            shared |= {x for x in a if x.startswith(d)}
    return shared


def relations(body):
    out = []
    for m in RELATION.finditer(body):
        nums = re.findall(r"#(\d+)", m.group(2))
        out.append((m.group(1).lower(), [int(n) for n in nums]))
    return out


def label_str(issue):
    return " ".join(l["name"] for l in issue.get("labels", []))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--with", dest="with_", type=int, action="append", default=[], help="issue(s) already chosen")
    ap.add_argument("--files", type=int, action="append", default=[], help="print the extracted file set of issue N")
    ap.add_argument("--refresh", action="store_true")
    ap.add_argument("--all", action="store_true", help="matrix: include issues with no files (epics, docs)")
    a = ap.parse_args()

    issues = {i["number"]: i for i in fetch_issues(a.refresh)}
    tracked, by_base = tracked_files()
    files = {n: extract(i.get("body") or "", tracked, by_base) for n, i in issues.items()}
    taken = {n for n, i in issues.items() if i.get("assignees")}

    def tag(n):
        i = issues[n]
        t = "TAKEN " if n in taken else ""
        return "#%-3d %s%-58s [%s]" % (n, t, i["title"][:58], label_str(i))

    for n in a.files:
        if n not in issues:
            print("#%d is not an open issue" % n)
            continue
        print(tag(n))
        for p in sorted(files[n]):
            print("    " + p)
        for kind, nums in relations(issues[n].get("body") or ""):
            print("    %s %s" % (kind, ", ".join("#%d" % x for x in nums)))
        print()
    if a.files and not a.with_:
        return

    if a.with_:
        chosen = [n for n in a.with_ if n in issues]
        for n in a.with_:
            if n not in issues:
                print("#%d is not an open issue (closed, or not on %s)" % (n, REPO))
        if not chosen:
            return
        print("Chosen:")
        for n in chosen:
            print("  " + tag(n) + "  (%d files)" % len(files[n]))
        for i, x in enumerate(chosen):
            for y in chosen[i + 1:]:
                s = touches(files[x], files[y])
                print("  #%d x #%d: %s" % (x, y, ("OVERLAP " + ", ".join(sorted(s))) if s else "disjoint"))
        print("\nCandidates to run alongside (disjoint from every chosen issue):")
        rest = sorted(n for n in issues if n not in chosen)
        disjoint, overlap = [], []
        for n in rest:
            shared = set()
            for c in chosen:
                shared |= touches(files[c], files[n])
            (overlap if shared else disjoint).append((n, shared))
        for n, _ in disjoint:
            rel = [(k, v) for k, v in relations(issues[n].get("body") or "") if any(c in v for c in chosen)]
            extra = ("  " + "; ".join("%s %s" % (k, ",".join("#%d" % x for x in v)) for k, v in rel)) if rel else ""
            note = "  (no files named)" if not files[n] else ""
            print("  " + tag(n) + note + extra)
        print("\nOverlapping (do these sequentially, or split by region with a 'your region only' clause):")
        for n, shared in overlap:
            print("  " + tag(n))
            print("      shares: " + ", ".join(sorted(shared)))
        return

    print("Open issues on %s: %d (%d taken). File-overlap pairs:\n" % (REPO, len(issues), len(taken)))
    nums = sorted(n for n in issues if a.all or files[n])
    pairs = 0
    for i, x in enumerate(nums):
        for y in nums[i + 1:]:
            s = touches(files[x], files[y])
            if s:
                pairs += 1
                print("#%d x #%d: %s" % (x, y, ", ".join(sorted(s))))
    print("\n%d overlapping pairs among %d issues with file lists." % (pairs, len(nums)))
    print("Issues naming no files (epics/docs; pair by judgement): " +
          ", ".join("#%d" % n for n in sorted(issues) if not files[n]))


if __name__ == "__main__":
    main()
