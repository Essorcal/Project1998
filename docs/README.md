# Project1998 documentation

Everything we know about NexusTK and everything we decided about this server. Split four ways, by the
question "**what would make this wrong?**":

| Directory | Holds | Goes stale when |
|---|---|---|
| [`common/`](common/) | The game and this codebase: architecture, mechanics, content, era gating | *we* change something |
| [`4.x/`](4.x/) | Facts about the **4.95 client** — its protocol, its packet formats, its quirks | never; the 2001 binary is frozen |
| [`5.x/`](5.x/) | Facts about the **5.33 client**, and only where it *differs* from 4.x | never; same reason |
| [`research/`](research/) | Where knowledge comes from, and how to get more of it | a source goes offline |

The client-versioned directories are the important part of that split. A statement like "the stats packet
carries fast-move in body byte 46" is a fact about one specific binary and cannot be argued with. A
statement like "a peasant walls at level 5" is a decision we made and can revisit. Filing them together
was how the old flat `docs/` ended up with no way to tell which was which.

---

## Start here

New to the project? Read in this order:

1. [`common/Architecture.md`](common/Architecture.md) — what the processes are, what each project does,
   and the three-tier split between code, content, and live state. **Read this before touching anything.**
2. [`common/Modding.md`](common/Modding.md) — how to change the game without rebuilding it. Most changes
   are a CSV edit and `@reload`.
3. [`4.x/Protocol.md`](4.x/Protocol.md) — the 5,400-line protocol reference. Not front-to-back; it has a
   table of contents and you want one section of it.

---

## `common/` — the game and the codebase

| Doc | What it answers |
|---|---|
| [Architecture.md](common/Architecture.md) | How the server is put together, and why it is two processes |
| [Modding.md](common/Modding.md) | Which file do I edit to change *X*, and does it need a restart |
| [Locking.md](common/Locking.md) | Every lock in the game process, and the one order they may be taken in |
| [Era-Gating.md](common/Era-Gating.md) | Should this content exist at our target date (2001-07-09) |
| [Melee-Damage.md](common/Melee-Damage.md) | The swing-damage formula, live-measured against the real server |
| [Armor-Quests.md](common/Armor-Quests.md) | The twelve Star/Moon/Sun chains: every step, and where the sources disagree |
| [Mythic-Alliances.md](common/Mythic-Alliances.md) | The twelve lesser alliances, and the eight-slot kill track they turn on |
| [Crafting-Values.md](common/Crafting-Values.md) | Archive-validated crafting numbers, for when crafting is ported |
| [Deferred-Work.md](common/Deferred-Work.md) | Things we researched, understood, and chose not to build yet |
| [Spell-Sound-Audit.txt](common/Spell-Sound-Audit.txt) | Generated: every spell's sound id, grouped by archetype |

## `4.x/` — the 4.95 client

The primary target. See [4.x/README.md](4.x/README.md).

| Doc | What it answers |
|---|---|
| [Protocol.md](4.x/Protocol.md) | Every opcode, wire format, and packet the 4.95 client sends or accepts |
| [Fast-Move.md](4.x/Fast-Move.md) | How the client's fast-move flag actually works (solved) |

## `5.x/` — the 5.33 client

Secondary, and currently parked. See [5.x/README.md](5.x/README.md) for exactly where it was parked and
what the one open item is.

| Doc | What it answers |
|---|---|
| [Client-Setup.md](5.x/Client-Setup.md) | Pointing 5.33 at a local server; serving both clients from one process |
| [Reverse-Engineering.md](5.x/Reverse-Engineering.md) | The 5.33 binary: function map, dispatcher, addresses |
| [Terrain-Streaming.md](5.x/Terrain-Streaming.md) | The `0x05`/`0x06` map-data protocol — 5.x streams terrain, 4.x does not |

## `research/` — where the knowledge comes from

| Doc | What it answers |
|---|---|
| [README.md](research/README.md) | The five sources, what each is good for, and how much to trust it |
| [Toolkit.md](research/Toolkit.md) | Frida, disassembly, PAK extraction — the tools in `re/`, and how not to hang the client |

---

## Writing docs here

**Cite the source.** An RTK path and line, an archive URL with its capture date, a screenshot, or the
experiment you ran. A claim with no source cannot be re-checked, and this project has already had to
un-learn several confidently-stated wrong things.

**Say how you know.** These are not equally strong, and the difference matters more than it looks:

> *observed live* > *read out of the client binary* > *read out of RTK's Lua* > *read off a fan site* > *inferred*

Where a fact came from a controlled experiment, say so, and say what the experiment was.

**Mark what you do not know.** An explicit "unknown" is worth more than silence — silence reads as
"nobody has looked", and the next person re-does the digging you already did.

**Delete what lands.** [`Deferred-Work.md`](common/Deferred-Work.md) is a record of things *not built*.
When one gets built, its record becomes the code and the commit; delete the entry.
