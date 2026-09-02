# AGENTS.md

Instructions for AI coding agents working in this repository. Humans should read
[CONTRIBUTING.md](CONTRIBUTING.md), which covers the same ground with more context.

Read this file **before your first edit**. Most of it is not style preference — it is the list of ways
this codebase fails silently.

---

## What this is

Project1998 is a from-scratch C#/.NET 8 server for **NexusTK 4.95**, a 2001 Korean-American MMORPG. The
original server is gone. Everything the server does was recovered by reverse-engineering the client
binary, reading a later community server's source, and mining twenty years of fan archives.

That shapes the work in a way ordinary application development does not: **most changes are archaeology
before they are engineering.** The question is rarely "what should this do?" — it is "what *did* this do
in 2001, and how do we know?"

## Orientation

```
Server/          the game process (~62 files; Session.*.cs are partials of one class)
LoginServer/     the login process
Shared/          anything both processes must agree on
Protocol.Tk495/  the wire adapter: cipher + framing for the 2001 clients
game-data/       ALL game content: CSVs, Lua, 1,750 .map terrain files
docs/            see docs/README.md -- 4.x / 5.x / common / research
re/              the reverse-engineering workbench (~155 Python scripts)
Tests/           guards on silent failures, not unit tests
```

Start with [`docs/common/Architecture.md`](docs/common/Architecture.md). It is the map.

## The loop

```bash
dotnet build Project1998.sln
```
```bash
dotnet test Tests/Tests.csproj
```
```bash
./run-server.bat
```

`run-server.bat` starts login and game in separate windows. **On Linux/macOS**, run the two projects
directly:

```bash
dotnet run --project LoginServer -- --ports 2000,2001
```
```bash
dotnet run --project Server -- --ports 2005,2006
```

> **`dotnet build` will fail with a file lock if a server is already running.** That is not a broken
> build. Stop the server, or build to a scratch output directory with `-p:BaseOutputPath=...`. Do **not**
> kill the user's running server processes without asking.

> **Warnings are build errors.** `Directory.Build.props` sets `TreatWarningsAsErrors` for every project.
> Fix the warning. If one site is a genuine false positive, a targeted `#pragma warning disable` with a
> comment saying why — never a project-wide `NoWarn`. CI builds `Project1998.Server.slnf`, which is the
> solution minus the Windows-only MapEditor.

> **`dotnet test` writes to the real database** at `state/project1998.db`, using throwaway usernames. It
> is safe, and it is deliberate — the persistence guarantees being tested are SQLite's. Do not point the
> tests at a fresh database to make them "cleaner"; that removes what they check.

## Rule 1: data describes *what*, code implements *how*

Before writing C#, check whether the change is a data change. It usually is.

| Change | Where | Rebuild? |
|---|---|---|
| A monster's HP, an item's price, a spell's mana cost | `game-data/*.csv` | No — `@reload` |
| What a spell or a consumable *does* | `game-data/spell_verbs.lua`, `item_verbs.lua` | No — `@reload` |
| An NPC conversation | `game-data/npc_dialog.lua` | No — `@reload` |
| A packet format, the combat formula, the AI tick | `Server/*.cs` | Yes |

Adding a C# field for something that belongs in a CSV is the most common wrong turn in this codebase.
[`docs/common/Modding.md`](docs/common/Modding.md) is the full map of which file holds what.

## Rule 2: cite your source, and rank it

Never write a game value without knowing where it came from. `game-data/Sources.csv` is a provenance
registry with an explicit weight per source, and content rows cite a `SourceId`. The ladder:

> **live observation** ≈ **client binary** > **period tutor post (2005)** > **later tutor post** > **fan site** > **RTK's implementation**

**RTK is weight 0.** It is a *7.x* server and we target 4.95; its balance numbers are known-wrong in
places (its AC formula is proven wrong against retail). Use it for identity, geometry and structure — not
for balance without a second source.

Read [`docs/research/README.md`](docs/research/README.md) before mining any source. It covers the five —
the RTK server, the Wayback Machine, the official tutor boards, Nexus Atlas, tswolf — and the specific
traps in each. Three worth knowing up front:

* **A capture's date is not the content's date.** Date the content, not the archive snapshot.
* **Published formulas are endgame fits.** Tutors derived them from level-99 characters; the intercept
  terms are regression artifacts and go wrong at low level.
* **Atlas is 5.x-era art.** Shape evidence, never colour evidence.

## Rule 3: assume silent failure

This server's characteristic bug is not a crash. It is a wrong number that nothing complains about: a
map that renders as plausible-but-wrong terrain, a weapon that draws as a different weapon, a karma band
that hands out the wrong reward, a hearing range that quietly drops speech.

That is why `Tests/` exists and why it looks the way it does. The bar for a new test is **not** "is this
code correct" — it is **"would this fail loudly?"** If it would throw, you probably do not need a test.
If it would be silently wrong, you do.

The same rule applies to your own verification. "It builds" proves almost nothing here. Say what you
actually checked, and if you could not check something, say that instead of implying you did.

## Rule 4: do not fabricate game facts

If you do not know what the 2001 game did, **say so**. An honest "unknown" is a normal, useful state in
this project — [`docs/common/Deferred-Work.md`](docs/common/Deferred-Work.md) is an entire file of them.

A plausible-sounding invented mechanic is worse than a gap, because it will be believed, propagate into
content, and cost somebody a day to un-learn. This has already happened more than once.

## Commits

The convention here is **prose, not tags**. No `feat:`/`fix:` prefixes, no scope brackets.

* **Subject**: what is now true, in the present tense, ≤ 80 characters, sentence case, no trailing period.
  Describe the outcome, not the action — `Weapon sheets render the HOLD pose, not the swing arc`, not
  `Fix render_weapons.py`.
* **Body**: **why**, not what. The diff shows what. Bodies here routinely run 10–30 lines and that is
  correct: name the failure mode, cite the RTK path or archive URL or address the fact came from, and say
  what you rejected and why. A body that only restates the subject is worse than no body.
* **Scope**: one change per commit. A content change and the engine change that reads it belong together
  — that is now possible and is why `game-data` was merged in.
* **Trailer**: end with `Co-Authored-By: <model> <noreply@anthropic.com>` when an agent wrote the change.

Read `git log` before your first commit. The existing messages are the specification.

## Things that will bite

* **Never blind-revert.** `git checkout --`, `git reset --hard`, and recursive pathspec globs have all
  destroyed uncommitted work here. Look at what you are about to discard first.
* **Never move a SQLite database by file copy.** It is in WAL mode; moving the `.db` without its `-wal`
  and `-shm` sidecars silently loses committed transactions. Use `sqlite3 .backup`.
* **Never commit a client binary.** `.gitignore` blocks `re/**/*.exe` and `re/patches/backups/`.
* **`state/`, `logs/`, `run/` are not yours.** `state/` is live player data and the only irreplaceable
  thing on the host. Never commit it, never delete it, never "clean" it.
* **`re/` byproducts are gitignored by design.** The rule: if re-running a script recreates it, it is a
  byproduct. If a human decided it, it is a finding. Findings are small — that is not a coincidence.
* **Client quirks that look like server bugs** — the `s` profile key's hard 5-second client-side
  cooldown, viewport-gated draws being lost rather than queued, a login password of `1` crashing the
  client. [`docs/4.x/README.md`](docs/4.x/README.md) lists them. Check there before debugging the server.
* **A byte-walk in Frida JS freezes the game.** Use native `Memory.scanSync`.

## Reverse engineering

If the answer is only in the client, [`docs/research/Toolkit.md`](docs/research/Toolkit.md) covers it:
static disassembly (`re/disx.py`, `re/exestr.py`), live Frida instrumentation (`re/frida_*.py`), PAK
extraction and sprite matching, and client patching.

The 4.95 binary is **ImageBase `0x400000`, no ASLR**, so every address in the docs is directly usable.
The workflow that works is string-first: find a user-visible string, find the code that references it,
read outward.

No script hardcodes an install path — `re/_paths.py` resolves everything, with `P1998_CLIENT`,
`P1998_RTK`, `P1998_ARCHIVE` and friends as overrides. **Keep it that way.**

## When you are unsure

Ask. Specifically: ask when the change would encode a *game fact* you cannot source, when it would move
a boundary in [`docs/common/Architecture.md`](docs/common/Architecture.md), or when the same behaviour
could plausibly be data or code. These are cheap questions and expensive guesses.
