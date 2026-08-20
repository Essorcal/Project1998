# The RE toolkit

Everything in `re/` — how to find out what a NexusTK client does. Nothing here is needed to *run* the
server or to change content; this is for the questions that only the binary can answer.

The tools split three ways: **read the binary** (static), **watch it run** (Frida), and **unpack its
data** (PAK). Most real answers come from combining two of them.

> **Paths.** No script hardcodes an install location any more. `re/_paths.py` resolves everything, with
> environment overrides for anything not inside the repo:
>
> | Variable | Default | What |
> |---|---|---|
> | `P1998_CLIENT` | `…\Nexon\NextAeon` | the 4.95 client |
> | `P1998_CLIENT5` | `…\Nexon\NextAeon5` | the 5.33 client |
> | `P1998_CLIENT_LIVE` | `…\KRU\NexusTK` | the live retail 7.x client |
> | `P1998_CLIENT483` | `…\KRU\NexusTK483` | the 4.83 client |
> | `P1998_RTK` | `<repo>/RTK-Server` | the RTK reference server clone |
> | `P1998_ARCHIVE` | `<repo>/../scraped_nexus_data` | the local Wayback scrape |

---

## 1. Static: reading the binary

The 4.95 client is **PE32 x86, ImageBase `0x400000`, no ASLR**. It always loads at `0x400000`, so a file
address and a runtime address are the same number. This is the single most useful property of the target
and it is why every address in [`../4.x/Protocol.md`](../4.x/Protocol.md) is quotable as-is.

| Tool | Use |
|---|---|
| `re/disx.py <start> [end]` | Linear disassembler over the 4.95 exe. `python re/disx.py 0x44a780` disassembles until `RET`. Built on `capstone` + `pefile`. |
| `re/exestr.py <exe> str <substr>` | The parameterized version — finds strings and constants in *any* NexusTK exe, not just 4.95. Start here when you have a message and want the code that prints it. |

**The workflow that actually works** is string-first, not code-first: find a user-visible string, find
the code that references it, and read outward. Chasing the opcode dispatcher from the top is a much
longer road — though the dispatchers are documented if you want them
([`../5.x/Reverse-Engineering.md`](../5.x/Reverse-Engineering.md) maps 5.33's).

For heavier work, `x64dbg` handles what a linear disassembler cannot.

## 2. Live: Frida

`pip install frida frida-tools`. Scripts attach or spawn, hook by `module base + RVA`, and stream events
to stdout and a log file.

```bash
python re/frida_probe.py            # spawn the 4.95 client under Frida
python re/frida_probe.py --attach   # attach to a running one
```

`frida_probe.py` is the reference example: it hooks the client's own decrypt/encrypt routines, the raw
socket calls, and the map loader. Hooking the client's *own* decrypt is the trick worth stealing —
it proves our encryption is correct by showing the client's plaintext view of our packets, rather than
by re-implementing the cipher and comparing it to itself.

There are ~35 more, named for what they answer: `frida_fastmove*.py`, `frida_keys.py`, `frida_sound.py`,
`frida_nametag.py`, `frida_statscan.py`, `frida_decode_live.py` (the live 7.x client's packets as
plaintext), `frida_combat_tap.py`.

`re/nexus_mem.py` is the other half: it locates the running client's own game-state structures, so a probe
can read ground truth — every entity's id/x/y/sprite, self position, the map's collision — instead of
inferring it from the wire.

### Three things that will waste your afternoon

* **A byte-walk in Frida JS freezes the game.** Iterating memory from JavaScript is slow enough to hang
  the client's message loop. Use native `Memory.scanSync` instead.
* **A client under a probe looks broken.** 5-second key delays and giant bursts of raw output are the
  probe, not a server bug. If input feels laggy, check for a stray `python re/frida_probe.py` first.
* **Attach can fail with `0x2e4` (`ERROR_ELEVATION_REQUIRED`).** That means a WinXP-compat or
  run-as-admin flag came back on the client exe. Clear the flag rather than running the probe elevated.

### Sending as the client

`re/` can drive the client rather than just watch it: call the client's **own** send function with
plaintext and let it do the framing and encryption. That sidesteps re-implementing the cipher on the
send path entirely, and it is how the bot harness (`re/nexus_bot.py`) plays.

## 3. Unpacking: the client's data files

The client ships Nexon-PAK `.dat` archives — sprites (`.epf`), palettes (`.pal`), lookup tables
(`.tbl`), sounds, music, and the server address list.

| Tool | Use |
|---|---|
| `re/pak_list.py <dat>` | List entries in a PAK archive |
| `re/pak_extract.py <dat> <entry>` | Extract one entry |
| `re/render_items.py`, `render_weapons.py`, `render_effects.py`, `render_pets.py` | Render `.epf` frames into labelled contact sheets, so art can be identified by eye |
| `re/match_item_icon.py`, `re/match_npc_look.py` | Score a reference image against every candidate frame and return the best-matching id |
| `re/monster-matcher/` | Monster sprite ↔ name/colour matching, our art against Atlas art |

**Rendering gotcha, learned the expensive way:** an art's 20-frame block is not 20 poses. Frames 0–12 are
the static hold and walk poses — what a player actually sees. Frames 13/15/17/19 are *swing arcs*: a
smeared motion trail that looks nothing like the weapon. A "pick the meatiest frame" heuristic lands on
an arc every time, which is how several `ItmLook` values got mis-identified. Render the hold pose.

Everything these produce is a **byproduct** and is gitignored — see the rule at the top of `.gitignore`.
The tracked files in `re/` are the scripts and the small findings.

## 4. Patching a client

`re/patches/` — one script per client build, on a shared engine (`patchlib.py`) that **refuses to write
unless the bytes at the target address match the recorded original**, so a stale address is rejected
rather than silently corrupting the exe. It backs up once before the first write and supports `--check`
and `--revert`.

```bash
python re/patches/patch_495_no_nametag.py --check    # report state, change nothing
python re/patches/patch_495_no_nametag.py            # apply
python re/patches/patch_495_no_nametag.py --revert   # restore from backup
```

Two mechanisms, and the second is the surprising one:

* **Exe byte patches** — overwrite code or a string at a virtual address.
* **Dat host-list patches** — *the server address is not in the exe.* The exe's address strings are stale
  defaults that the client never reads. The live address is a plaintext PAK entry (`Address` on 4.83,
  `Connaddr` on 5.33) inside `NexusTK.dat`. Redirecting a client means rewriting that entry in place.

`Tools/` has the same idea as a proper .NET CLI for `Inter.dat`:

```bash
dotnet run --project Tools -- /path/to/Inter.dat --target 127.100.10.1
```

**Never commit a client exe.** `.gitignore` blocks `re/**/*.exe` and `re/patches/backups/` for this reason.

## 5. Extracting content from RTK

The scripts that produced most of `game-data/`. They read the RTK dump (see
[`README.md`](README.md) §1) and write CSVs:

`rtk_extract.py` (the bulk tables), `extract_mob_drops.py`, `extract_shops.py`, `extract_lua_spawns.py`,
`extract_spell_formulas.py`, `extract_ambush_tables.py`, `build_map_index.py`, and about thirty more.

**These are cited by name from C# doc comments** as the provenance of the table they generated — grep for
`re/extract_` in `Server/Content.cs` and you will find forty-five of them. That is why they are tracked
and why they should keep working: the comment is a promise that the table can be rebuilt.

`build_map_index.py` is worth calling out as the pattern to copy: it is **client-authoritative**. It
iterates the 4.95 client's own `TK<id>.map` files and emits only ids that exist there, rather than
trusting RTK's list. A warp target that comes out of it is always renderable. When RTK and the client
disagree about what exists, the client wins.

---

## Before you record a finding

Two rules, both bought with real time:

**A block on one path does not prove all paths blocked.** "The client refuses to do X" has more than once
meant "the client refuses to do X *the way we tried*". Try a second route before recording a negative.

**Verify sprites visually.** Look ids, palette indices and icon frames are three id spaces that agree
often enough to lull you and diverge often enough to matter. Render it and look at it.

Then write it up in [`../4.x/`](../4.x/) or [`../5.x/`](../5.x/) with the address, the experiment, and
the date — and add a row to `game-data/Sources.csv` if it backs a content value.
