# Project1998

**A working server for NexusTK 4.95, rebuilt from nothing.**

NexusTK is a Korean-American 2D MMORPG that launched in 1999. Client build **4.95** shipped on
**2001-07-09**. Its server was never open, its source was never released, and the world that ran on it is
gone.

This is that server, rebuilt in C# on .NET 8 — reverse-engineered from the 2001 client binary, a later
community server's source, and twenty years of fan archives. The real 4.95 client connects to it, enters
the world, and plays.

```
        1,840 maps          716 creatures        2,545 items
          927 spells        368 NPCs           4,621 warps
```

---

## Status

**Playable.** Login, character creation, world entry, movement, combat, spells, items, shops, NPC
dialog, quests, parties, trade, mail, bulletin boards, marriage, pets, harvesting, weather, and a live
world clock all work against the real client.

Currently running on a live host. `master` is the deployed branch.

| | |
|---|---|
| **Primary target** | 4.95 client (2001) — full support |
| **Secondary** | 5.33 client (2003) — logs in, enters the world, renders terrain. Parked; see [`docs/5.x/README.md`](docs/5.x/README.md) |
| **Era** | The world is pretending it is **2001-07-09**. Content is date-gated — see [`docs/common/Era-Gating.md`](docs/common/Era-Gating.md) |
| **Scale** | ~35,000 lines of C#, 66 content CSVs, 4 Lua scripts, 183 tests |
| **Licence** | **None yet** — see [below](#licence) |

---

## Quick start

```bash
git clone https://github.com/project1998/Project1998.git && cd Project1998
```

**Windows** — no prerequisites:

```bash
run-server.bat
```

It finds a .NET 8 SDK or offers to fetch a private one into `.dotnet\` beside the source -- no admin,
no PATH change, delete the folder to undo -- then builds the solution and opens the login and game
servers in their own windows.

**Dev launcher** (Windows, optional) -- when one machine has several clones and several people or
agents sharing the same two ports, `Scripts\Serve.ps1` performs the same launch with the checkout and
commit stamped on it:

```powershell
Scripts\Serve.ps1 -Checkout C:\Repo\Project1998 -Testers botone -Gms botone   # build, then start the pair
Scripts\Serve.ps1 -Status                                                    # what is on 2000/2005, and whose
Scripts\Serve.ps1 -Checkout C:\Repo\Project1998 -Stop                        # close exactly that pair
Scripts\Serve.ps1 -Checkout C:\Repo\Project1998-b -PortBase 3000             # a second pair, from a second clone
```

It needs `git` on PATH: the commit and branch are what it stamps on the consoles and records, and it
refuses to start when they cannot be resolved. It refuses to start while either port is held and names
the holder (PID, command line, and that pair's checkout and commit if it was started the same way); it
never stops anything by itself. The two consoles are titled `LOGIN 2000/2001 - <clone> @ <commit>` and
`GAME 2005/2006 - <clone> @ <commit>`, where the commit is `git rev-parse HEAD` at the moment the script
built the tree and a `+` means tracked files were modified (untracked files are not counted), so the
window says which build it is. `-Testers` and
`-Gms` reach only the launched processes, as `P1998_TESTERS` / `P1998_GMS`, unioned with the clone's
`state/*_accounts.txt` as usual. What it started is recorded in the clone's `run/session.json`
(`pid_login`, `pid_game`, `checkout`, `commit`, `branch`, `ports`, `testers`, `gms`, `started`, plus each
slot's executable path, creation time and console PID); `-Status` reads it back. `-Stop` acts only on a
session file written in the checkout it is given, and only on a PID that is still the recorded
executable, created at the recorded time and holding its ports: it sends those two processes Ctrl+C,
closes the two console windows it opened, waits for the ports to free, and deletes the file. Anything
else on the ports is reported and left alone. `-PortBase 3000` binds login 3000/3001 and game 3005/3006
(the same base-rule `run-server.bat 3000` uses), so two pairs from two clones run at once, each with its
own `state/`, `run/` and `logs/`; one pair per clone, since the session file, the `bin/` and the database
all live in it. The four ports are recorded in `session.json`, and `-Status` and `-Stop` read them from
there, so the base is only given at start. `run-server.bat` stays the plain path; the script does not
replace it.

**Test-Branch.ps1** (Windows) -- turns "this branch didn't change X behaviour" into a table instead of a
claim: it starts a pair from a checkout with `Serve.ps1` (default port base 3000, so it never collides
with a developer's own pair on 2000), waits for the game server's status probe to answer, runs every
script in [`project1998-testclient`](https://github.com/Essorcal/project1998-testclient)`\scripts\`
against it with `--json`, stops the pair, and prints one line per script -- exit code, expects passed,
expects failed, wall clock -- exiting 0 only if every script exited 0.

```powershell
Scripts\Test-Branch.ps1 -Checkout C:\Repo\NexusTK-sonnet
```

`-Scripts` points it at other scripts (a glob or a list) instead of the full suite, `-KeepRunning` skips
the stop for a developer who wants to poke at the pair afterwards, and `-Json <path>` writes the same
results as JSON for a reviewer to attach to a PR. It refuses the same way `Serve.ps1` does when the ports
are already held (exit 2, and says who); a readiness timeout or any script exiting nonzero is exit 1.

**Linux/macOS** — install the [.NET 8 SDK](https://dotnet.microsoft.com/download), build, then start
the **two processes**:

```bash
dotnet build Project1998.sln
```
```bash
dotnet run --project LoginServer -- --ports 2000,2001
```
```bash
dotnet run --project Server -- --ports 2005,2006
```

To play, point a 4.95 client at your machine. `Tools/` rewrites the Nexon server IPs baked into the
client's `Inter.dat`, writing `Inter.dat.patched` and keeping a backup:

```bash
dotnet run --project Tools -- /path/to/Inter.dat --target 127.100.10.1
```

The target must be exactly 12 characters, because it is an in-place string replacement over 12-character
Nexon IP tokens. `127.100.10.1` is the default and is a valid loopback the client's resolver accepts.

The other clients redirect differently — 4.83 and 5.33 read their address from a plaintext PAK entry
inside `NexusTK.dat` rather than from `Inter.dat`. `re/patches/` handles those.

Full client setup, including the 5.33 client: [`docs/4.x/README.md`](docs/4.x/README.md),
[`docs/5.x/Client-Setup.md`](docs/5.x/Client-Setup.md).

---

## How it is put together

```
Server/          the game process -- world, movement, combat, items, NPCs
LoginServer/     the login process -- accounts, auth, handoff
Shared/          what both must agree on -- db, opcodes, tokens, paths
Protocol.Tk495/  the wire adapter -- cipher + framing for the 2001 clients
Tools/           the client-redirect patcher
Tests/           183 guards on the failures that are SILENT
game-data/       all game content: 66 CSVs, 4 Lua scripts, 1,840 .map files
docs/            everything we know -- see docs/README.md
re/              the reverse-engineering workbench (~155 Python scripts)
```

**Two processes, not one.** Login is the internet-facing front door; the game process holds the world.
The game can be restarted to ship a change while players stay connected to login and reconnect. Login
never proxies game traffic — it mints a single-use handoff token bound to the username *and* the client's
IP, and the game server consumes it once.

**Three tiers, in directories that cannot see each other.** This is the load-bearing idea:

| Tier | Lives in | Changed by | Reload |
|---|---|---|---|
| **Content** | `game-data/` | Editing a CSV or a Lua file | `@reload` — **no restart** |
| **Live state** | `state/project1998.db` | Playing the game | it *is* the live truth |
| **Engine** | `Server/*.cs` | Editing code | rebuild + restart |

> **Golden rule: data describes *what*; code implements *how*.** A monster's HP is data; the combat
> formula is code. A spell's mana cost is data; the packet that draws its animation is code.

Most of the game is therefore editable without touching C#. Spells, item effects, NPC dialog and creature
AI are **Lua** (MoonSharp, pure managed) driven by CSV rows — a new spell is usually a row plus a
function, hot-reloaded with a GM command while players are online.

Full detail: [`docs/common/Architecture.md`](docs/common/Architecture.md).

---

## Where the game came from

There is no authoritative record of NexusTK in 2001. Every value in `game-data/` was recovered, and the
project tracks **how** — `game-data/Sources.csv` is a provenance registry where each source carries a
weight, and content rows cite it. When sources disagree, the higher weight wins and the conflict is
written down so nobody re-litigates it.

| Source | What it gave us | Trust |
|---|---|---|
| **The 4.95 client binary** | The entire protocol: opcodes, wire formats, the cipher, world entry, terrain, sprites | Highest — it cannot lie |
| **[RTK-Server](https://github.com/unkmc/RTK-Server)** | Names, stats, map geometry, NPC placement, drop tables — most of `game-data/` | Structure yes, **balance no** (it is 7.x) |
| **[boards.nexustk.com](http://boards.nexustk.com)** | Class-tutor formula breakdowns — the best mechanics evidence outside live play | High, but dated: the game was rebalanced repeatedly |
| **[nexusatlas.com](https://nexusatlas.com)** | Item icons, spell animations, monster art, walkthroughs | Shape evidence only; it is 5.x-era art |
| **tswolf.com** | Period news archive and guides — closest to our era | High for *when* content existed |
| **[The Wayback Machine](https://web.archive.org)** | How we reach the three above; most no longer exist as captured | The way in |

The traps in each are documented in [`docs/research/README.md`](docs/research/README.md) — read it before
mining any of them. Two that catch everyone:

* **A capture's date is not the content's date.** A 2013 snapshot of a 2005 post is 2005 evidence.
* **Published formulas are endgame fits.** Tutors derived them from level-99 characters, so their
  intercept terms are regression artifacts that go wrong at low level.

---

## Reverse engineering

`re/` holds ~155 Python scripts: static disassembly, live Frida instrumentation, PAK extraction, sprite
matching, and client patchers. The 4.95 binary is PE32 x86, **ImageBase `0x400000`, no ASLR**, so every
address in the docs is directly usable with no rebasing.

[`docs/research/Toolkit.md`](docs/research/Toolkit.md) is the guide.
[`docs/4.x/Protocol.md`](docs/4.x/Protocol.md) is the 5,400-line result — a self-contained reference
complete enough to implement a 4.95 server from scratch.

---

## Documentation

[`docs/`](docs/) is organised by **what would make a page wrong**, not by topic:

| | |
|---|---|
| [`docs/common/`](docs/common/) | The game and this codebase — architecture, mechanics, content, era gating. Stale when *we* change something. |
| [`docs/4.x/`](docs/4.x/) | Facts about the 4.95 client. Never stale — the binary is frozen. |
| [`docs/5.x/`](docs/5.x/) | Facts about the 5.33 client, where it differs. Same. |
| [`docs/research/`](docs/research/) | Where knowledge comes from, and how to get more. Stale when a source goes offline. |

Start at [`docs/README.md`](docs/README.md), then
[`docs/common/Architecture.md`](docs/common/Architecture.md).

---

## Contributing

Yes, please. [CONTRIBUTING.md](CONTRIBUTING.md) is the guide; [AGENTS.md](AGENTS.md) is the same rules
written for an AI coding agent, which is how most work here happens.

The one thing worth knowing before you start: **this is archaeology before it is engineering.** The hard
part of most changes is not implementing the behaviour — it is establishing what the behaviour *was* in
2001, and being able to show how you know. An honest "we don't know" is a normal and valuable state here;
[`docs/common/Deferred-Work.md`](docs/common/Deferred-Work.md) is an entire file of them.

---

## Related repositories

| Repo | What |
|---|---|
| [Project1998-infra](https://github.com/project1998/Project1998-infra) | Everything host-shaped: the HAProxy edge, systemd units, deploy and backup scripts. Kept separate so this repo runs on your PC with `dotnet run` and carries no trace of our VPS. |
| [dist](https://github.com/project1998/dist) | The launcher's update manifest. |

---

## Licence

**Not yet chosen.** Default copyright therefore applies: there is no grant to use, modify or redistribute
this code, and contributors have no explicit terms. This is a known gap and it is being resolved — if it
affects you, raise an issue before investing significant work.

Game content under `game-data/` is derived from the retail NexusTK client and from
[RTK-Server](https://github.com/unkmc/RTK-Server). No client binary, executable, or Nexon-distributed
archive is redistributed here.
