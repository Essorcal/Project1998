# Contributing to Project1998

Thanks for wanting to help rebuild this. Before anything else, one thing about the shape of the work:

**This project is archaeology first and engineering second.** NexusTK 4.95 shipped on 2001-07-09. Its
server is gone, its source was never public, and nobody who wrote it is around to ask. Every number in
`game-data/` was recovered from a client binary, a later community server, or a twenty-year-old forum
post. So the hard part of most changes is not "how do I implement this" — it is **"what did the real game
do, and how do we know?"**

If you internalise one thing from this document, make it that. Everything below follows from it.

---

## Getting set up

You need [.NET 8 SDK](https://dotnet.microsoft.com/download). Nothing else is required to build.
On Windows without one, `run-server.bat` offers to fetch a private copy into `.dotnet\`.

```bash
git clone https://github.com/project1998/Project1998.git
```
```bash
cd Project1998 && dotnet build Project1998.sln
```
```bash
dotnet test Tests/Tests.csproj
```

Then run it — two processes, login and game:

```bash
dotnet run --project LoginServer -- --ports 2000,2001
```
```bash
dotnet run --project Server -- --ports 2005,2006
```

On Windows, `run-server.bat` does both in separate windows and builds first.

To actually *play*, you need a 4.95 client pointed at your machine. See
[`docs/4.x/README.md`](docs/4.x/README.md) and `Tools/` (the `Inter.dat` redirect patcher).

### Optional: the research trees

Only needed if you are doing content or RE work. Neither is vendored — both are large and belong to
other people.

```bash
git clone --depth 1 https://github.com/unkmc/RTK-Server.git
```

That goes in the repo root (it is gitignored) or a sibling directory; `re/_paths.py` finds it either way,
and `P1998_RTK` overrides. The Wayback scrape is `P1998_ARCHIVE`. See
[`docs/research/README.md`](docs/research/README.md).

---

## Working with an AI agent

Most contributors here work with an AI coding agent, and the repository is set up for it:

* **[AGENTS.md](AGENTS.md)** is the agent's briefing — the same rules as this document, compressed and
  written for a model. Point your agent at it first. Claude Code, Cursor and most other agents read it
  automatically.
* **`.claude/settings.json`** carries project-wide permissions (read-only commands pre-approved; anything
  that writes to the world still prompts). Your personal approvals go in `.claude/settings.local.json`,
  which is gitignored.
* **`docs/` is written to be read by a model**, which is why it is organised by *what would make a page
  wrong* rather than by topic.

You are still responsible for what you submit. In particular: **an agent will confidently invent a game
mechanic if you let it.** That is the single most damaging failure mode in this project, because an
invented mechanic sounds right, gets believed, propagates into content, and costs somebody a day to
un-learn. Check every game fact against a source before it lands.

---

## The three rules

### 1. Data describes *what*; code implements *how*

Most changes are not code changes.

| You want to change | Edit | Restart? |
|---|---|---|
| A monster's HP, an item's price, a spell's cost | `game-data/*.csv` | No — `@reload` in game |
| What a spell or consumable does | `game-data/spell_verbs.lua`, `item_verbs.lua` | No — `@reload` |
| An NPC's conversation | `game-data/npc_dialog.lua` | No — `@reload` |
| A packet format, the combat formula, the AI tick | `Server/*.cs` | Yes |

A monster's HP is data; the combat formula is code. A spell's mana cost is data; the packet that draws
its animation is code. [`docs/common/Modding.md`](docs/common/Modding.md) is the full map.

Adding a C# constant for something that belongs in a CSV is the most common wrong turn here — and it
matters more than tidiness, because content hot-reloads and code does not. A content-only change deploys
to the live server without disconnecting anyone.

### 2. Cite your source, and know its rank

`game-data/Sources.csv` is a provenance registry: every source has a weight, and content rows cite a
`SourceId`. When two sources disagree, the higher weight wins and the disagreement goes in the notes so
nobody re-litigates it.

> **live observation** ≈ **client binary** > **period tutor post (2005)** > **later tutor post** > **fan site** > **RTK's implementation**

**RTK sits at weight 0.** It is the most-used source in this codebase and the least authoritative one —
a 7.x server where we target 4.95. Excellent for names, geometry and structure. Not to be trusted for
balance without corroboration.

[`docs/research/README.md`](docs/research/README.md) covers all five sources and the traps in each. Read
it before mining any of them.

### 3. Assume silent failure

The characteristic bug in this codebase is not a crash. It is a wrong number nothing complains about:

* a map that renders as perfectly plausible *wrong* terrain
* a weapon that draws as a different weapon
* a karma band that silently hands out the wrong reward
* a hearing range that silently drops speech

This is why `Tests/` is not a normal unit-test suite. It is a set of guards on exactly these — wire
formats, id translations, thresholds. The bar for adding a test is **not** "is this code correct" but
**"would this fail loudly?"** If it throws, you probably do not need a test. If it is silently wrong,
you do.

Apply the same standard to your PR description. "It builds" proves very little here.

---

## Commits

The convention is **prose, not tags** — no `feat:`/`fix:` prefixes, no scope brackets. Read `git log`;
the existing messages are the specification.

**Subject** — what is now true, present tense, ≤ 80 characters, sentence case, no trailing period.
Describe the outcome, not the action:

```
Weapon sheets render the HOLD pose, not the swing arc      ← good
Fix render_weapons.py                                       ← says nothing
```

**Body** — **why**, not what. The diff already shows what. Bodies here routinely run 10–30 lines, and
that is correct. A good body:

* names the failure mode ("that is how several ItmLook values got mis-picked")
* cites the source — an RTK path and line, an archive URL with its date, the experiment you ran
* says what you rejected and why, so the next person does not re-propose it

A body that restates the subject is worse than no body.

**Scope** — one change per commit. A content change and the engine change that reads it belong *together*;
that is now possible in one commit, and is the whole reason `game-data` stopped being a submodule.

**Trailer** — if an AI agent wrote the change, credit it:
`Co-Authored-By: <model> <noreply@anthropic.com>`.

---

## Pull requests

1. **Branch off `master`.** Small and focused beats large and comprehensive.
2. **`dotnet build` and `dotnet test` both pass.** CI runs both plus a content smoke test that catches
   what the compiler cannot — a bad CSV row, a renamed Lua verb, a missing `.map`.
3. **Say how you verified it.** "Built and tested" is not verification for a game server. Did you log in
   and look? Which client? What did you expect and what did you see?
4. **Say what you did *not* verify.** A stated gap is useful; an implied guarantee is not.
5. **Sources for any game fact**, in the PR body or the commit.

CI has two lanes. A push touching only `game-data/` is hot-reloadable and deploys without restarting the
world; anything else stages a new release with warned restart. This is why keeping content changes
content-only is worth a little effort.

---

## What not to do

* **Never blind-revert.** `git checkout --`, `git reset --hard`, and recursive pathspec globs have all
  destroyed uncommitted work in this repo. Look before you discard.
* **Never move a SQLite database by copying files.** It runs in WAL mode; moving the `.db` without its
  `-wal` and `-shm` sidecars silently loses committed transactions. Use `sqlite3 .backup`.
* **Never commit a client binary or extracted game art.** `.gitignore` blocks them.
* **Never commit anything from `state/`, `logs/` or `run/`.** `state/` is live player data.
* **Do not add byproducts to `re/`.** The rule: if re-running a script recreates it, it is a byproduct
  and is gitignored. If a human decided it, it is a finding and is tracked. Findings are small.

---

## Reverse engineering

If a question can only be answered by the client, [`docs/research/Toolkit.md`](docs/research/Toolkit.md)
is the guide: static disassembly, live Frida instrumentation, PAK extraction and sprite matching, and
client patching.

The 4.95 client is PE32 x86, **ImageBase `0x400000`, no ASLR** — every address in the docs is directly
usable in a disassembler or a Frida script with no rebasing. The workflow that works is string-first:
find a user-visible string, find the code that references it, read outward.

Two rules that have each cost real time:

* **A block on one path does not prove all paths blocked.** "The client refuses to do X" has more than
  once meant "the client refuses to do X *the way we tried*". Try a second route before recording a
  negative result.
* **Verify sprites visually.** Look ids, palette indices and icon frames are three id spaces that agree
  often enough to lull you and diverge often enough to matter. Render it and look at it.

When you find something, write it up in [`docs/4.x/`](docs/4.x/) or [`docs/5.x/`](docs/5.x/) with the
address, the experiment and the date — and add a row to `game-data/Sources.csv` if it backs a content
value.

---

## Where to ask

Open an issue. If it is a game-behaviour question ("what did X do in 2001?"), say what you have already
checked — that alone saves the next person from repeating it.

## Licence

**This project does not have a licence yet**, which means default copyright applies and contributors have
no explicit grant. That is a known gap and it is being resolved. If it matters to you, say so on an issue
before investing significant work.
