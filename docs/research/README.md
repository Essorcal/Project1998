# Where the knowledge comes from

NexusTK launched in 1999 and the era we target — **2001-07-09** — has no surviving server, no design docs,
and no official record. Everything in `game-data/` was recovered from five sources, and this page is about
how much to trust each one.

**Read this before adding a number to a CSV.** Most of the expensive mistakes in this project's history
were not bad reverse-engineering; they were good reverse-engineering of the *wrong source*.

---

## The evidence ladder

Sources are not equal, and the project encodes the ranking rather than leaving it to memory:
`game-data/Sources.csv` is a provenance registry — every row is a source with a `Tier` and a `Weight`,
and content rows cite a `SourceId`. When two sources disagree, **the higher weight wins**, and the loser
is recorded in the `Notes` so the disagreement is not re-litigated.

| Weight | Tier | What it is |
|---|---|---|
| **3** | `live` / `client-re` | Observed on a live server, or read out of the client binary |
| **2** | `documented` | A period tutor-board post, a dated news item, a fan guide |
| **1** | `documented` | A later or lower-confidence write-up of the same thing |
| **0** | `fallback` / `design` | RTK's implementation; or an explicit owner decision where no evidence exists |

Note where RTK sits: **weight 0**. It is the default provenance for bulk-imported rows and the *last*
thing to believe about a number.

---

## 1. The RTK reference server — `github.com/unkmc/RTK-Server`

A community NexusTK server (Mithia lineage, client 7.x), open source, with a production MySQL dump. It is
the **single most-cited source in this codebase** and the origin of most of `game-data/`: monster names
and stats, item tables, map and warp geometry, NPC placement, spell definitions, shop stock, drop tables.

Clone it into the repo root (it is gitignored) or a sibling directory; `re/_paths.py` finds it either way,
and `P1998_RTK` overrides:

```bash
git clone --depth 1 https://github.com/unkmc/RTK-Server.git
```

Three parts, and they are not equally useful:

| Path | What it is | Trust |
|---|---|---|
| `database/` | MySQL dump — names, stats, placement | **Good** for identity and geometry |
| `rtklua/Accepted/` | Lua game scripts — spells, mobs, quests, combat | Good for *behaviour*, poor for *numbers* |
| `rtk/src/` | The C engine — `clif.c`, `pc.c`, `mob.c` | Good for wire formats; much combat math is dead code |

**RTK is 7.x and we target 4.95.** That gap is the whole problem with it:

* **Look ids 0–326 overlap** with our client and are validated by sprite matching. Above 326 is 7.x-added
  art our client does not have — 22 of 258 NPCs render nothing because of exactly this.
* **1,387 of RTK's map ids** have a matching `TK<N>.map` in our client. The rest are 7.x additions.
* **Balance numbers diverge.** RTK's `onLevel.lua` AC formula is *proven wrong* against retail. Mob AC and
  HP are known to differ. Its content tables fork ClassicTK's 2019 dump, which is two decades of drift
  from where we are aiming.
* **Some of its combat math is commented out** in favour of Lua that then does something else. Read both
  before concluding anything.

**Use RTK for identity, geometry and structure. Do not use it for balance without a second source.**

## 2. The Wayback Machine — `web.archive.org`

The way into everything below. tswolf.com and nexusatlas.com are gone or changed beyond recognition;
their 2001–2005 captures are not.

* `curl` and `urllib` both work. It **rate-limits** — back off rather than parallelising.
* The CDX API (`web.archive.org/cdx/search/cdx`) lists every capture of a URL with its timestamp. Use it
  to find a capture *near your target date* instead of taking whatever `/web/2020*/` gives you.
* **A capture's date is not the content's date.** A 2013 capture of a page written in 2005 is 2005
  evidence. Date the *content*.

> **Check the local scrape first.** `re/archive_scrape.py` and its siblings have already pulled ~55,000
> pages; the result is a directory of tswolf, nexusatlas, official-boards and user-page artifacts. Point
> `P1998_ARCHIVE` at it. Re-scraping is slow, rate-limited, and usually unnecessary.

### Dating a news post

Site news is the best evidence for *when* content existed, which is what
[`../common/Era-Gating.md`](../common/Era-Gating.md) runs on. One heuristic that has held up: posts by
"Rachel" are site maintenance, not game changes. Real game changes show up as Dream Weaver copies and
server-reset lists.

## 3. The official boards — `boards.nexustk.com`

Where the **tutor posts** live, and the strongest non-live evidence this project has for game mechanics.
Class tutors were players with deep system knowledge who wrote long, careful formula breakdowns. Sixteen
of the 29 rows in `Sources.csv` are tutor posts.

Board URLs are stable and directly citable, e.g.
`boards.nexustk.com/Warriors/Yttribium 04210049.html`. Cite the **URL and the post date**, not just the
author.

The catch, and it is a real one:

* **Tutor posts span 2005–2019 and the game was rebalanced repeatedly.** A 2014 post and a 2005 post can
  both be honest and still disagree. The 2005 posts are closer to our era; prefer them, and record the
  conflict rather than silently picking one.
* **Published formulas are endgame fits.** Tutors derived their numbers from level-99 characters. Their
  intercept terms are regression artifacts of that fit, not real constants, and they go wrong at low
  level. Treat a published formula as *a curve through the endgame*, not as the mechanism.

## 4. Nexus Atlas — `nexusatlas.com`

A fan database: item icons, spell animation GIFs, monster art, quest walkthroughs. Its value is that it
is **pictures** — the one source that shows what something actually looked like.

* `re/match_item_icon.py` scores an Atlas `.gif` against every frame of the client's `Item.epf` and
  returns the best-matching icon id. `re/match_npc_look.py` does the same for NPC sprites against a
  period screenshot. Both are how a "what look id is this?" question gets answered.
* **Atlas is 5.x-era.** Use it as **shape** evidence, never **colour** evidence. The palettes moved
  between clients; a match on silhouette is strong, a match on hue means nothing.
* Validate the matcher on known-good pairs *in the same run* before trusting a new answer. The scripts
  print scores for exactly this reason: `< 12` is confident, `12–35` is plausible, `> 35` is no match —
  report it unresolved rather than guessing.

## 5. tswolf.com

A period fan site (TSWolf / NexNet) with news archives and walkthrough guides. Smaller than the others but
**closer to our era than anything except the client itself**, which makes its news archive unusually good
for era gating. Its newbie-quest guide captured each dialog page as a GIF — which is how several NPC look
ids were recovered by image matching.

---

## Two rules that have cost real time

**A block on one path does not prove all paths blocked.** More than once, a "the client refuses to do X"
finding turned out to be "the client refuses to do X *via the route we tried*". Before recording a
negative result, try a second route to the same behaviour.

**Verify sprites visually, always.** Look ids, palette indices and icon frames are three different id
spaces that agree often enough to lull you and diverge often enough to matter. Render it and look at it.

---

## Adding a source

Add a row to `game-data/Sources.csv` — `SourceId, Type, Tier, Weight, Title, Url, Author, Date, Retrieved,
Notes` — and cite the `SourceId` from the content row it backs. Put the *disagreement* in `Notes`: what
the other sources said, and why this one wins. That note is the only thing standing between a future
contributor and re-doing your work.
