#!/usr/bin/env python
"""
Scrape the archive.org cache of users.nexustk.com character pages and build a
per-(class, level) stat dataset.

Each page carries:
    Vital statistics : Level / Vita / Mana / Might / Grace / Will
    Equipment list   : Weapon / Armor / Helm / Left hand / Right hand
    Legend           : contains the path mark ("Ohaeng Mage since ...") -> CLASS

The listed stats EXCLUDE worn equipment -- established without assuming any formula:
  * a naked and a fully-geared character at the same class+level read IDENTICALLY
    (Mage lv72: 0 slots -> 28/39/72, 4-5 slots -> 28/39/72)
  * a fixed-effects fit within (class, level) cells finds NO positive relationship
    between number of equipped slots and any stat (might slope -1.20, grace -0.86)
So a page shows the character's BASE stats. They still include PERMANENT bonuses
(endgame/quest stat awards), which is why a level-99 Mage can read Will 138 against a
level-99 base of 99 -- that is award accumulation, not gear. Levels 1-98 only, and
low/mid levels are cleanest since awards pile up late.

Stages (each resumable, run in order):
    python re/archive_scrape.py index     # CDX -> archive/index.jsonl
    python re/archive_scrape.py fetch     # download snapshots -> archive/cache/*.gz
    python re/archive_scrape.py parse      # cache -> archive/chars.csv
    python re/archive_scrape.py analyze    # chars.csv -> per class/level tables

Politeness: single-digit requests/sec, exponential backoff on 429/5xx, and every
snapshot cached on disk so re-runs never refetch. Tune with --workers / --delay.
"""
import os, sys, re, csv, json, gzip, time, random, threading, argparse, statistics
import urllib.request, urllib.error, collections

D = os.path.dirname(os.path.abspath(__file__))
A = os.path.join(D, "archive")
CACHE = os.path.join(A, "cache")
os.makedirs(CACHE, exist_ok=True)
P_INDEX = os.path.join(A, "index.jsonl")
P_CHARS = os.path.join(A, "chars.csv")
P_REPORT = os.path.join(A, "base_stats.md")
P_FAIL = os.path.join(A, "failed.txt")

UA = "Mozilla/5.0 (compatible; NexusTK-archival-stat-research)"
CDX = ("https://web.archive.org/cdx/search/cdx?url=users.nexustk.com/userfiles/"
       "&matchType=prefix")


class Throttle:
    """Adaptive global pacing.

    archive.org signals overload by REFUSING the TCP connection rather than returning
    429, and it escalates if you keep pushing. So we treat a refusal as a rate-limit
    signal: widen the delay multiplicatively, and only creep back down after sustained
    success. This keeps the crawl inside whatever budget the archive is willing to give.
    """
    def __init__(self, base=2.0, lo=1.0, hi=90.0):
        self.d = base
        self.lo, self.hi = lo, hi
        self.lock = threading.Lock()
        self.ok = self.refused = 0

    def wait(self):
        with self.lock:
            d = self.d
        time.sleep(d * (0.6 + 0.8 * random.random()))

    def hit(self, refused):
        with self.lock:
            if refused:
                self.refused += 1
                self.d = min(self.hi, self.d * 1.7)
            else:
                self.ok += 1
                self.d = max(self.lo, self.d * 0.93)

    def stats(self):
        with self.lock:
            return self.ok, self.refused, self.d


def is_refusal(e):
    s = str(e).lower()
    return "refused" in s or "10061" in s or "timed out" in s or "reset" in s


def get(url, timeout=120, tries=5):
    """Fetch with backoff. Returns bytes or raises."""
    delay = 2.0
    for a in range(tries):
        try:
            r = urllib.request.Request(url, headers={"User-Agent": UA,
                                                     "Accept-Encoding": "gzip"})
            with urllib.request.urlopen(r, timeout=timeout) as f:
                d = f.read()
                if f.headers.get("Content-Encoding") == "gzip":
                    try:
                        d = gzip.decompress(d)
                    except Exception:
                        pass
                return d
        except urllib.error.HTTPError as e:
            if e.code in (429, 503, 502, 504, 500) and a < tries - 1:
                time.sleep(delay + random.random()); delay *= 2; continue
            raise
        except Exception:
            if a < tries - 1:
                time.sleep(delay + random.random()); delay *= 2; continue
            raise


# ---------------------------------------------------------------- index
def stage_index(args):
    """Page through the CDX API; keep 200/text-html /userfiles/<name>.html captures.

    Distinct digests are all KEPT (not collapsed): the same character captured years
    apart is a different level, which is exactly the signal we want.
    """
    npages = int(get(CDX + "&showNumPages=true").decode().strip())
    print(f"[index] {npages} CDX pages")
    seen = set()
    if os.path.exists(P_INDEX) and not args.force:
        for l in open(P_INDEX, encoding="utf-8"):
            try:
                seen.add(json.loads(l)["digest"])
            except Exception:
                pass
        print(f"[index] resuming, {len(seen)} digests already indexed")
    out = open(P_INDEX, "a", encoding="utf-8", buffering=1)
    kept = 0
    rx = re.compile(r"/userfiles/([^/]+)\.html?$", re.I)
    for pg in range(npages):
        try:
            txt = get(f"{CDX}&page={pg}").decode("utf-8", "replace")
        except Exception as e:
            print(f"[index] page {pg} failed: {e}")
            continue
        for line in txt.splitlines():
            f = line.split()
            if len(f) < 7:
                continue
            _key, ts, orig, mime, status, digest = f[0], f[1], f[2], f[3], f[4], f[5]
            if status != "200" or "html" not in mime.lower():
                continue
            m = rx.search(orig.split("?")[0])
            if not m:
                continue
            if digest in seen:
                continue
            seen.add(digest)
            out.write(json.dumps({"name": m.group(1).lower(), "ts": ts,
                                  "url": orig, "digest": digest}) + "\n")
            kept += 1
        print(f"[index] page {pg+1}/{npages}  kept so far: {kept}")
    out.close()
    print(f"[index] done -> {P_INDEX} ({kept} new unique-content captures)")


# ---------------------------------------------------------------- fetch
def cache_path(digest):
    return os.path.join(CACHE, digest + ".gz")


def stage_fetch(args):
    rows = [json.loads(l) for l in open(P_INDEX, encoding="utf-8") if l.strip()]
    if not args.include_listings:
        # a.html..z.html are alphabetical directory pages, not characters
        rows = [r for r in rows if len(r["name"]) > 1]
    # PRIORITY ORDER: earliest snapshot of each character first, then 2nd-earliest, ...
    # An archived page shows the character's level at capture time, so a character's
    # earliest capture is its LOWEST level -- and levels 1-98 are the whole point
    # (level 99 pages carry endgame stat bonuses and are unusable). Ordering this way
    # also gives maximum character breadth per hour spent.
    per = collections.defaultdict(list)
    for r in rows:
        per[r["name"]].append(r)
    # ERA FIRST, then rank. Measured on the first 333 class-known samples: captures from
    # 2003-2009 are ~33% below level 99, while 2010+ captures are ~2% -- the game's
    # population aged into the level cap. Since only sub-99 levels are usable, an
    # early-era page is worth an order of magnitude more than a late one.
    ranked = []
    for name, v in per.items():
        for rank, r in enumerate(sorted(v, key=lambda x: x["ts"])):
            late = 0 if r["ts"][:4] <= "2009" else 1
            ranked.append((late, rank, name, r))
    ranked.sort(key=lambda x: (x[0], x[1], x[2]))
    rows = [r for _, _, _, r in ranked]
    todo = [r for r in rows if not os.path.exists(cache_path(r["digest"]))]
    print(f"[fetch] {len(rows)} indexed, {len(todo)} to download "
          f"({len(rows)-len(todo)} already cached)")
    if args.limit:
        todo = todo[:args.limit]
        print(f"[fetch] limited to {len(todo)} this run")
    lock = threading.Lock()
    done = [0]
    saved = [0]
    failed = []
    thr = Throttle(base=args.delay)
    t_start = time.time()

    def worker(chunk):
        for r in chunk:
            thr.wait()
            # the id_ modifier returns the ORIGINAL page with no wayback toolbar
            u = f"https://web.archive.org/web/{r['ts']}id_/{r['url']}"
            try:
                d = get(u, timeout=90, tries=2)
                # never cache a rate-limit/error body as if it were a real page
                head = d[:4000].decode("utf-8", "replace")
                if "Nexus TK Character page" not in head and "userfiles" not in head:
                    raise RuntimeError("not a character page")
                with gzip.open(cache_path(r["digest"]), "wb") as f:
                    f.write(d)
                thr.hit(False)
                with lock:
                    saved[0] += 1
            except Exception as e:
                thr.hit(is_refusal(e))
                with lock:
                    failed.append(f"{r['digest']} {u} {e}")
            with lock:
                done[0] += 1
                if done[0] % 50 == 0:
                    ok, ref, d_ = thr.stats()
                    el = time.time() - t_start
                    print(f"[fetch] {done[0]}/{len(todo)} saved={saved[0]} "
                          f"refused={ref} delay={d_:.1f}s "
                          f"rate={saved[0]/max(el,1)*3600:.0f}/hr", flush=True)

    n = max(1, args.workers)
    chunks = [todo[i::n] for i in range(n)]
    ts = [threading.Thread(target=worker, args=(c,), daemon=True) for c in chunks]
    for t in ts:
        t.start()
    for t in ts:
        t.join()
    if failed:
        open(P_FAIL, "w", encoding="utf-8").write("\n".join(failed))
    print(f"[fetch] done: {done[0]} attempted, {len(failed)} failed "
          f"({'see '+P_FAIL if failed else 'none'})")


# ---------------------------------------------------------------- parse
STAT_RX = {
    "level": re.compile(r"Level\s*:?\s*</?[^>]*>?\s*(\d+)", re.I),
    "vita": re.compile(r"Vita\s*:?\s*</?[^>]*>?\s*(\d+)", re.I),
    "mana": re.compile(r"Mana\s*:?\s*</?[^>]*>?\s*(\d+)", re.I),
    "might": re.compile(r"Might\s*:?\s*</?[^>]*>?\s*(\d+)", re.I),
    "grace": re.compile(r"Grace\s*:?\s*</?[^>]*>?\s*(\d+)", re.I),
    "will": re.compile(r"Will\s*:?\s*</?[^>]*>?\s*(\d+)", re.I),
}
SLOTS = ["Weapon", "Armor", "Helm", "Left hand", "Right hand"]
CLASS_WORDS = ["Warrior", "Rogue", "Mage", "Poet"]


def to_lines(html):
    body = re.sub(r"(?s)<!--\s*BEGIN WAYBACK TOOLBAR INSERT.*?"
                  r"END WAYBACK TOOLBAR INSERT\s*-->", "", html)
    txt = re.sub(r"(?s)<script.*?</script>|<style.*?</style>", " ", body)
    txt = re.sub(r"(?s)<[^>]+>", "\n", txt).replace("&nbsp;", " ").replace("&amp;", "&")
    return [l.strip() for l in txt.split("\n") if l.strip()]


def parse_page(html):
    lines = to_lines(html)
    idx = {l: i for i, l in enumerate(lines)}
    rec = {}

    def after(label):
        """the value line following a 'Label :' line"""
        for i, l in enumerate(lines):
            if l.rstrip(" :").lower() == label.lower() and i + 1 < len(lines):
                return lines[i + 1]
        return None

    for k, lab in [("level", "Level"), ("vita", "Vita"), ("mana", "Mana"),
                   ("might", "Might"), ("grace", "Grace"), ("will", "Will")]:
        v = after(lab)
        if v is None or not v.replace(",", "").isdigit():
            return None
        rec[k] = int(v.replace(",", ""))
    # CRITICAL: blank equipment fields are ambiguous -- the player may have HIDDEN the
    # equipment list rather than being unequipped. Only "section present AND all slots
    # blank" means genuinely naked, and only that gives base stats directly.
    rec["eq_present"] = int(any(l.strip().lower().startswith("equipment list")
                                for l in lines))
    rec["name"] = (after("Character name") or "").strip()
    rec["title"] = (after("Character title") or "").strip()
    rec["nation"] = (after("Character Nation") or "").strip()
    for s in SLOTS:
        rec["eq_" + s.replace(" ", "_").lower()] = (after(s) or "").strip()

    # ---- class from the Legend section (path mark), e.g. "Ohaeng Mage since ..."
    cls, why = "", ""
    li = None
    for i, l in enumerate(lines):
        if l.strip().lower() == "legend":
            li = i
            break
    if li is not None:
        legend_lines = lines[li + 1:]
        rec["legend_n"] = len(legend_lines)
        # AUTHORITATIVE: the subpath declaration line spells out the base class, e.g.
        # "Ohaeng Rogue since Yuri 56, Winter" / "Kwi-Sin Mage since ..." / "Ming-Ken
        # Poet since ...". The subpath (Ohaeng/Kwi-Sin/Ming-Ken) is orthogonal to class,
        # so it is the CLASS WORD before "since" that identifies the path.
        prim = collections.Counter()
        for l in legend_lines:
            m = re.search(rf"\b({'|'.join(CLASS_WORDS)})\s+since\b", l, re.I)
            if m:
                prim[m.group(1).capitalize()] += 1
        if prim:
            top = prim.most_common()
            if len(top) == 1 or top[0][1] > top[1][1]:
                cls, why = top[0][0], "legend-path"
        if not cls:
            # WEAK fallbacks: guild/trial marks that appear to be class-gated. Kept
            # separate because plain class words are unreliable -- "Poetry Revel" is an
            # open event and names like "MePoet" contain a class word by coincidence.
            weak = collections.Counter()
            for l in legend_lines:
                if re.search(r"Family to the Nangen Mages", l, re.I):
                    weak["Mage"] += 1
                if re.search(r"Completed Nangen Warrior Trial", l, re.I):
                    weak["Warrior"] += 1
            if weak:
                top = weak.most_common()
                if len(top) == 1 or top[0][1] > top[1][1]:
                    cls, why = top[0][0], "legend-weak"
    rec["class"] = cls
    rec["class_src"] = why
    return rec


def stage_parse(args):
    rows = [json.loads(l) for l in open(P_INDEX, encoding="utf-8") if l.strip()]
    cols = (["name", "bare_name", "ts", "digest", "class", "class_src", "level", "might",
             "grace", "will", "vita", "mana", "title", "nation", "legend_n", "eq_present"]
            + ["eq_" + s.replace(" ", "_").lower() for s in SLOTS])
    n = ok = 0
    with open(P_CHARS, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=cols, extrasaction="ignore")
        w.writeheader()
        for r in rows:
            p = cache_path(r["digest"])
            if not os.path.exists(p):
                continue
            n += 1
            try:
                html = gzip.open(p, "rb").read().decode("utf-8", "replace")
                rec = parse_page(html)
            except Exception:
                rec = None
            if not rec:
                continue
            rec["ts"] = r["ts"]
            rec["digest"] = r["digest"]
            rec["bare_name"] = r["name"]          # from the URL; used for title extraction
            if not rec.get("name"):
                rec["name"] = r["name"]
            w.writerow(rec)
            ok += 1
    print(f"[parse] {n} cached pages read, {ok} parsed -> {P_CHARS}")


# ---------------------------------------------------------------- analyze
def rtk_expected(cls, level):
    """RTK onLevel.lua base stats: Peasant 1->5 then the chosen path 5->level."""
    mg = gr = wl = 3
    for n in range(2, min(level, 5) + 1):
        if n % 2 == 0:
            mg += 1
        elif n % 3 == 0 or n % 5 == 0:
            gr += 1; wl += 1
    for n in range(6, level + 1):
        sec = 1 if n % 2 == 0 else 0
        ter = 1 if n % 3 == 0 else 0
        if cls == "Warrior":
            mg += 1; gr += sec; wl += ter
        elif cls == "Rogue":
            mg += sec; gr += 1; wl += ter
        elif cls == "Mage":
            mg += ter; gr += sec; wl += 1
        elif cls == "Poet":
            mg += ter; gr += ter; wl += 1
    return mg, gr, wl


P_TITLEMAP = os.path.join(A, "title_class_map.json")

# ---- AUTHORITATIVE path titles for characters at or below level 99 (in-game path
# descriptions). Exactly 3 subpaths per base class; a character who has not chosen a
# subpath yet just carries the plain class name as its title.
BASE_SUBPATHS = {
    "barbarian": "Warrior", "chongun": "Warrior", "do": "Warrior",
    "diviner": "Mage", "geomancer": "Mage", "shaman": "Mage",
    "merchant": "Rogue", "ranger": "Rogue", "spy": "Rogue",
    "druid": "Poet", "monk": "Poet", "muse": "Poet",
}
PLAIN_TITLES = {"warrior": "Warrior", "rogue": "Rogue", "mage": "Mage", "poet": "Poet"}
SUB99 = {**BASE_SUBPATHS, **PLAIN_TITLES}

# index into (might, grace, will) of the stat RTK grants +1 EVERY level for that class,
# i.e. the stat that must equal the character's level exactly.
KEY_IDX = {"Warrior": 0, "Rogue": 1, "Mage": 2, "Poet": 2}
# Every OTHER title (assault, shikari, panoply, il san, karuna, ...) belongs to the
# Il San / Ee San ladders, which only exist ABOVE level 99 -- so such a title is itself
# evidence the character is past the usable range.


def title_prefix(display, bare):
    """'Sulsa-Do Acemander' + bare 'acemander' -> 'sulsa-do' (the PATH title).

    The page's character name is '<path title> <name>'. We know the bare name from the
    URL, so whatever precedes it is the subpath/rank title.
    """
    d = (display or "").strip()
    b = (bare or "").strip().lower()
    if not d:
        return ""
    dl = d.lower()
    if b and dl.endswith(b):
        d = d[: len(d) - len(b)]
    else:                      # name may be styled differently; drop the last token
        parts = d.split()
        d = " ".join(parts[:-1]) if len(parts) > 1 else ""
    t = d.strip()
    t = re.sub(r"\((?:m|f)\)", " ", t, flags=re.I)      # '(M)'/'(F)' gender markers
    t = re.sub(r"[^A-Za-z' -]", " ", t)
    return re.sub(r"\s+", " ", t).strip().lower()


def stage_titles(args):
    """Learn subpath-title -> base-class from the rows whose Legend states the class.

    Self-validating: a title that is genuinely class-specific will come out nearly pure.
    Impurity exposes legend-detection mistakes (a cross-class legend line like
    'Completed Nangen Warrior Trial' on a non-Warrior) rather than a real ambiguity.
    """
    rows = list(csv.DictReader(open(P_CHARS, encoding="utf-8")))
    tally = collections.defaultdict(collections.Counter)
    for r in rows:
        t = title_prefix(r.get("name"), r.get("bare_name") or "")
        if t and r.get("class"):
            tally[t][r["class"]] += 1
    mapping, ambiguous = {}, []
    for t, c in sorted(tally.items(), key=lambda x: -sum(x[1].values())):
        n = sum(c.values())
        top, ntop = c.most_common(1)[0]
        purity = ntop / n
        if n >= args.title_min_n and purity >= args.title_purity:
            mapping[t] = {"class": top, "n": n, "purity": round(purity, 3)}
        else:
            ambiguous.append((t, n, dict(c)))
    # cross-check the learned rules against the authoritative sub-99 path list
    conflict, confirm = [], []
    for t, v in mapping.items():
        auth = SUB99.get(t)
        if auth:
            (confirm if auth == v["class"] else conflict).append((t, v["class"], auth))
    # authoritative entries always win, and are present even with zero observations
    for t, c in SUB99.items():
        cur = mapping.get(t)
        mapping[t] = {"class": c, "n": cur["n"] if cur else 0,
                      "purity": cur["purity"] if cur else 1.0, "src": "authoritative"}
    json.dump(mapping, open(P_TITLEMAP, "w", encoding="utf-8"), indent=1, sort_keys=True)
    print(f"[titles] learned {len(mapping)} title->class rules "
          f"(min n={args.title_min_n}, purity>={args.title_purity})")
    print(f"[titles] vs authoritative sub-99 list: {len(confirm)} confirmed, "
          f"{len(conflict)} CONFLICT")
    for t, got, auth in conflict:
        print(f"   !! {t}: learned {got} but authoritative says {auth}")
    missing = [t for t in SUB99 if not tally.get(t)]
    if missing:
        print(f"[titles] authoritative titles not yet observed: {sorted(missing)}")
    print(f"{'title':<26}{'class':<10}{'n':>5}{'purity':>8}")
    for t, v in sorted(mapping.items(), key=lambda x: -x[1]["n"])[:40]:
        print(f"  {t:<24}{v['class']:<10}{v['n']:>5}{v['purity']:>8.2f}")
    if ambiguous:
        print(f"\n[titles] {len(ambiguous)} titles left ambiguous "
              f"(too few samples or mixed):")
        for t, n, c in ambiguous[:15]:
            print(f"  {t:<24} n={n:<4} {c}")

    # TEST the sub-99 claim: an Il San / Ee San title should only appear at level 99+.
    lv_by_kind = collections.defaultdict(list)
    for r in rows:
        t = title_prefix(r.get("name"), r.get("bare_name") or "")
        try:
            lv = int(r["level"])
        except Exception:
            continue
        kind = ("plain" if t in PLAIN_TITLES else
                "subpath" if t in BASE_SUBPATHS else "post-99" if t else "none")
        lv_by_kind[kind].append(lv)
    print("\n[titles] level range by title kind (tests the 'other titles are 99+' rule)")
    print(f"{'kind':<10}{'n':>6}{'min':>6}{'median':>8}{'max':>6}{'  %lv<99':>9}")
    for k in ("plain", "subpath", "post-99", "none"):
        v = sorted(lv_by_kind.get(k, []))
        if not v:
            continue
        sub = sum(1 for x in v if x < 99)
        print(f"{k:<10}{len(v):>6}{v[0]:>6}{v[len(v)//2]:>8}{v[-1]:>6}"
              f"{sub/len(v)*100:>8.0f}%")
    odd = [x for x in lv_by_kind.get("post-99", []) if x < 99]
    if odd:
        print(f"  NOTE: {len(odd)} post-99-titled chars are below 99 "
              f"(levels {sorted(set(odd))[:10]}) -- title ladder may start under 99")
    print(f"\n[titles] -> {P_TITLEMAP}")


def load_titlemap():
    if os.path.exists(P_TITLEMAP):
        try:
            return json.load(open(P_TITLEMAP, encoding="utf-8"))
        except Exception:
            pass
    return {}


def stage_analyze(args):
    if not os.path.exists(P_CHARS):
        print("run parse first"); return
    rows = list(csv.DictReader(open(P_CHARS, encoding="utf-8")))
    # fill in class for rows with no legend path mark, using the learned title->class map
    tm = load_titlemap()
    filled = 0
    for r in rows:
        if not r.get("class") and tm:
            t = title_prefix(r.get("name"), r.get("bare_name") or "")
            hit = tm.get(t)
            if hit:
                r["class"] = hit["class"]
                r["class_src"] = "title"
                filled += 1
    print(f"[analyze] {len(rows)} character snapshots"
          + (f"  (+{filled} class recovered from subpath/rank title)" if filled else ""))
    naked_slots = ("", "none", "None")
    by = collections.defaultdict(list)
    for r in rows:
        try:
            lv = int(r["level"])
        except Exception:
            continue
        if not r["class"] or lv < 1 or lv > 98:
            continue
        try:
            rec = (int(r["might"]), int(r["grace"]), int(r["will"]),
                   int(r["vita"]), int(r["mana"]))
        except Exception:
            continue
        # genuinely naked = equipment section WAS shown and every slot in it is blank
        bare = (r.get("eq_present") == "1" and
                all((r.get("eq_" + s.replace(" ", "_").lower(), "") or "").strip()
                    in naked_slots for s in SLOTS))
        # OPT-IN filter, OFF by default on purpose. It drops rows whose every-level stat
        # sits below the character's level -- which RTK says is impossible. But that test
        # ASSUMES RTK is correct, and RTK is the very thing we are trying to check, so
        # leaving it on by default would be circular. Enable it only to ask the narrower
        # question "among RTK-consistent characters, does the rest of RTK hold?".
        if args.drop_rtk_inconsistent and rec[KEY_IDX[r["class"]]] < lv:
            continue
        by[(r["class"], lv)].append((rec, bare))

    out = ["# Base stats per class/level, from archived character pages", "",
           f"snapshots used: {sum(len(v) for v in by.values())}",
           f"class/level cells: {len(by)}", "",
           "`min` over many samples approximates BASE (someone was wearing nothing that",
           "boosts that stat). `bare` = samples with every equipment slot empty, which is",
           "base directly. RTK = prediction from onLevel.lua.", ""]
    hdr = ("| class | lvl | n | bare | might min/RTK | grace min/RTK | will min/RTK "
           "| vita min | mana min |")
    out += [hdr, "|" + "---|" * 9]
    agree = dis = 0
    for (cls, lv) in sorted(by, key=lambda x: (x[0], x[1])):
        v = by[(cls, lv)]
        if len(v) < args.min_n:
            continue
        bare = [x[0] for x in v if x[1]]
        src = bare or [x[0] for x in v]
        mn = [min(s[i] for s in src) for i in range(5)]
        emg, egr, ewl = rtk_expected(cls, lv)
        mark = lambda a, b: f"{a}/{b}" + ("" if a == b else " X")
        for a, b in ((mn[0], emg), (mn[1], egr), (mn[2], ewl)):
            if a == b:
                agree += 1
            else:
                dis += 1
        out.append(f"| {cls} | {lv} | {len(v)} | {len(bare)} | {mark(mn[0], emg)} | "
                   f"{mark(mn[1], egr)} | {mark(mn[2], ewl)} | {mn[3]} | {mn[4]} |")
    out += ["", f"**RTK agreement on min-observed primaries: {agree} match, {dis} differ**",
            "(X marks a mismatch. A min ABOVE the RTK value usually just means no sample",
            "at that level was wearing nothing in that slot; a min BELOW it means the RTK",
            "rule is wrong for that class.)"]
    open(P_REPORT, "w", encoding="utf-8").write("\n".join(out) + "\n")
    print("\n".join(out[:40]))
    print(f"\n[analyze] full table -> {P_REPORT}")


P_EMP = os.path.join(A, "empirical.md")
P_EMPCSV = os.path.join(A, "empirical_stats.csv")


def stage_empirical(args):
    """Report what the DATA says, per (class, level). RTK is a reference column only.

    Estimator = MAX observed, not min. Reasoning, all measured rather than assumed:
      * worn equipment is NOT in these numbers (naked and fully-geared characters at the
        same class+level read identically), so there is no gear inflation to strip;
      * exp-bought stats are gated to level 90+, so levels 1-89 are purchase-free;
      * within a cell, characters' stat SUMS are not conserved -- they trail off below the
        best character by up to 10 points. Points are LOST, not moved between stats.
    Loss only ever subtracts, so the top of each cell's distribution is the intended
    per-level value and everything below it is an unlucky character.
    Cells also report the full value distribution so a point mass (deterministic) is
    visually distinguishable from a spread (stochastic).
    """
    rows = list(csv.DictReader(open(P_CHARS, encoding="utf-8")))
    tm = load_titlemap()
    NAMES = ("might", "grace", "will")
    cells = collections.defaultdict(list)
    for r in rows:
        c = r.get("class") or (tm.get(title_prefix(r.get("name"),
                               r.get("bare_name") or "")) or {}).get("class")
        try:
            lv = int(r["level"])
        except Exception:
            continue
        if not c or not (1 <= lv <= args.max_level):
            continue
        s = (int(r["might"]), int(r["grace"]), int(r["will"]))
        if s == (3, 3, 3) and lv > 5:
            continue                      # known bad rows (level-1 values at high level)
        cells[(c, lv)].append((s, int(r["vita"]), int(r["mana"]), r["bare_name"]))
    # drop purchase-bleed / mislabels: a stat far above the cell median
    for k in list(cells):
        v = cells[k]
        if len(v) >= 3:
            med = [statistics.median(s[i] for s, _, _, _ in v) for i in range(3)]
            cells[k] = [x for x in v if all(x[0][i] - med[i] <= 10 for i in range(3))]

    out = ["# Empirical stat curve from archived character pages", "",
           f"levels 1-{args.max_level} (purchase-free: exp-selling unlocks at 90).",
           f"cells shown with n >= {args.min_n}. **Estimator = MAX observed** (see below).",
           "",
           "Stat points are LOST by some characters, never moved between stats (measured:",
           "sums are not conserved within a cell, trailing up to -10). So the MAX in each",
           "cell is the intended value and lower values are unlucky characters. `dist`",
           "shows every observed value: a single entry means deterministic, several means",
           "stochastic. `RTK` is shown for reference ONLY -- it is not treated as truth.",
           ""]
    hdr = ("| class | lvl | n | might | grace | will | RTK m/g/w | agrees | "
           "might dist | grace dist | will dist |")
    out += [hdr, "|" + "---|" * 11]
    csvrows = []
    agree = tot = 0
    for (c, lv) in sorted(cells, key=lambda x: (x[0], x[1])):
        v = cells[(c, lv)]
        if len(v) < args.min_n:
            continue
        mx = [max(s[i] for s, _, _, _ in v) for i in range(3)]
        e = rtk_expected(c, lv)
        ok = tuple(mx) == e
        agree += ok
        tot += 1
        dists = []
        for i in range(3):
            d = collections.Counter(s[i] for s, _, _, _ in v)
            dists.append(",".join(f"{val}x{n}" if n > 1 else f"{val}"
                                  for val, n in sorted(d.items())))
        out.append(f"| {c} | {lv} | {len(v)} | {mx[0]} | {mx[1]} | {mx[2]} | "
                   f"{e[0]}/{e[1]}/{e[2]} | {'yes' if ok else 'NO'} | "
                   + " | ".join(dists) + " |")
        csvrows.append({"class": c, "level": lv, "n": len(v),
                        "might": mx[0], "grace": mx[1], "will": mx[2],
                        "vita_max": max(x[1] for x in v),
                        "mana_max": max(x[2] for x in v),
                        "rtk_might": e[0], "rtk_grace": e[1], "rtk_will": e[2],
                        "spread_might": mx[0] - min(s[0] for s, _, _, _ in v),
                        "spread_grace": mx[1] - min(s[1] for s, _, _, _ in v),
                        "spread_will": mx[2] - min(s[2] for s, _, _, _ in v)})
    out += ["", f"**max-observed vs RTK: {agree}/{tot} cells agree "
                f"({agree/max(tot,1)*100:.0f}%)**", ""]
    # determinism summary
    out += ["## Determinism per class/stat (cells with n>=3)", "",
            "| class | stat | cells | single-valued | % | median spread |",
            "|---|---|---|---|---|---|"]
    for c in ("Warrior", "Rogue", "Mage", "Poet"):
        ks = [k for k in cells if k[0] == c and len(cells[k]) >= 3]
        for i, nm in enumerate(NAMES):
            if not ks:
                continue
            sp = [max(s[i] for s, _, _, _ in cells[k]) -
                  min(s[i] for s, _, _, _ in cells[k]) for k in ks]
            ident = sum(1 for x in sp if x == 0)
            out.append(f"| {c} | {nm} | {len(ks)} | {ident} | "
                       f"{ident/len(ks)*100:.0f}% | {statistics.median(sp):.0f} |")
    open(P_EMP, "w", encoding="utf-8").write("\n".join(out) + "\n")
    if csvrows:
        with open(P_EMPCSV, "w", newline="", encoding="utf-8") as f:
            w = csv.DictWriter(f, fieldnames=list(csvrows[0].keys()))
            w.writeheader()
            w.writerows(csvrows)
    print(f"[empirical] {tot} cells (n>={args.min_n});  max-observed matches RTK in "
          f"{agree} ({agree/max(tot,1)*100:.0f}%)")
    print(f"[empirical] -> {P_EMP}\n[empirical] -> {P_EMPCSV}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("stage", choices=["index", "fetch", "parse", "titles", "analyze", "empirical"])
    ap.add_argument("--title-min-n", type=int, default=3,
                    help="min labelled samples before a title->class rule is trusted")
    ap.add_argument("--title-purity", type=float, default=0.9,
                    help="min fraction agreeing on one class")
    ap.add_argument("--drop-rtk-inconsistent", action="store_true",
                    help="drop rows whose every-level stat is below their level "
                         "(ASSUMES RTK is right -- circular if you are testing RTK)")
    ap.add_argument("--workers", type=int, default=2)
    ap.add_argument("--delay", type=float, default=2.0,
                    help="starting pace (seconds); adapts up on refusals, down on success")
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--min-n", type=int, default=1)
    ap.add_argument("--max-level", type=int, default=89,
                    help="upper level bound; 89 keeps the purchase-free zone")
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--include-listings", action="store_true",
                    help="also fetch the a.html..z.html directory pages")
    args = ap.parse_args()
    {"index": stage_index, "fetch": stage_fetch, "parse": stage_parse,
     "titles": stage_titles, "analyze": stage_analyze,
     "empirical": stage_empirical}[args.stage](args)


if __name__ == "__main__":
    main()
