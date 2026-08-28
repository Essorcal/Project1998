# NexusTK 5.x (client 5.33) — server support notes

This folder documents everything specific to serving the **NexusTK 5.33 client**
(`C:\Program Files (x86)\Nexon\NextAeon5`, build **5.3.3.384**, exe dated Jan 2003) from the
local C# `Project1998`. The 4.95 protocol is documented separately in
[`../4.x/Protocol.md`](../4.x/Protocol.md); this folder only covers where 5.x
**differs**.

> **Status (2026-08-13): terrain rendering SOLVED; 5.33 support paused here.** The 5.33 client logs in,
> enters the world, and renders 4.x terrain correctly — verified offline at 1,719,261 / 1,719,261 drawn
> cells across all 1,750 shipped maps. Door cell patches were fixed to the correct 3-short shape.
>
> **Object collision: SOLVED 2026-08-20 by patching the client.** 5.33 re-authored the `SObj.tbl`
> collision flags and collides client-side, so 17,841 cells across 397 maps that the 4.x maps intend as
> walkable were impassable on 5.33 — Arctic Village 35,32/36,32 is the reference case. This is a
> *structural* divergence, not a cosmetic one: a 4.95 and a 5.33 player standing in the same room had
> different walkable geometry. The server cannot fix it (graphic and collision are the same wire value —
> every server-side scope buys walkability by deleting artwork, and only `all` reaches parity).
>
> The fix is [`re/patches/patch_533_sobj_flags.py`](../../re/patches/patch_533_sobj_flags.py): rewrite the
> 362 differing flag bytes in the client's own `SOBJ.TBL` to their 4.x values. Same length, in place, no
> repack. **Verified against all 1,840 maps: cells reachable on 4.x but not 5.33 goes 17,841 → 0, with
> zero artwork loss.** It also fixes the other direction — 3,296 cells where 5.33 started a step the
> server then refused, producing rubber-band snap-backs. A patched client should run
> `P1998_OBJ_FIX_533=off`; the server-side workaround is redundant. See
> [`Terrain-Streaming.md`](Terrain-Streaming.md) → "Object collision".
>
> With rendering and collision both settled, **object flags were the only structural divergence left** —
> ground passability cannot diverge by construction (the `.map` pass field only ever holds 0 or 3, which
> is exactly the sheet-2 selector, and 5.33 reads bit 0 of it).

## The one thing to understand about 5.x

**5.x terrain is streamed from the server, not loaded from local files.** This is the single biggest
difference from 4.95. The 4.95 client draws its map from a local `Maps\TK<id>.map` it ships with; the
5.33 client instead **asks the server** for the tiles in its viewport (opcode `0x05`/`0x06`) and the
server streams them back (opcode `0x06`). Until the server answers, the map is a black void — which is
exactly the symptom that dominated this investigation.

See [`Terrain-Streaming.md`](Terrain-Streaming.md) for the packet spec.

## Documents in this folder

| File | What it covers |
|------|----------------|
| [`Client-Setup.md`](Client-Setup.md) | Redirecting the 5.33 client to the local server, the unified dual-client (4.95 + 5.33) server design, and how to run/test. |
| [`Terrain-Streaming.md`](Terrain-Streaming.md) | The `0x05`/`0x06` map-data request/response protocol — the core 5.x rendering path. Definitive packet layouts. |
| [`Wire-Divergences.md`](Wire-Divergences.md) | **Where the 5.x wire differs from 4.95** — the four-dispatcher model, the zero-probe method, every confirmed packet delta, the confirmed NON-deltas, and the open questions. Read this before touching a 5.x packet. |
| [`Reverse-Engineering.md`](Reverse-Engineering.md) | `NexusTK.exe` RE reference: function addresses, the `.cmp` map format, the opcode dispatcher, the cipher, `Tile.dat` layout, and the Frida/diagnostic tooling. |

## Quick start (run both clients against one server)

1. **Point the 5.33 client at its lane** (login port 2001) — run once, as admin:
   `client-5.33-redirect\Deploy-Connaddr-2001.bat`
2. **Start the unified server** (opens all four ports): `run-server.bat`
3. Launch **4.95** (unchanged) and/or **5.33**; each is auto-detected by the port it connects on.

Ports: `2000`/`2005` = 4.95 login/game, `2001`/`2006` = 5.33 login/game.

## Key facts at a glance

- **Same wire cipher as 4.95**: NexonInc static XOR (`TkCrypt.Crypt`, key `"NexonInc."`), both channels,
  every opcode used so far. No name-keyed/table cipher.
- **Same framing**: `AA | len(u16 BE) | op | inc | body`, no trailer. `len = 2 + body.Length`.
- **A 4.x ground word selects one of TWO sheets — this is the whole ballgame.** Read out of the 4.x
  blitter (`sub_431820`): `v == 0` → draw nothing; `v < 0xC000` → `TileA[v-1]`; `v >= 0xC000` →
  `TileB[v-0xC000]`. Those constants are the `base` u16 in each legacy `.tbl` header. The top two bits
  are **not** passability, and masking them off (`tile = v & 0x3FFF`) rewrites **30.58% of all cells** —
  526,619 of 1,722,232, across 1,492 of the 1,750 maps — into unrelated low tiles. That was the
  "tiles don't make sense in particular places" bug.
- **Sheet-1 ground needs NO shift for 5.33.** 5.33 prepended a null frame (`TileA[i]` ≡ `TILE[i+1]`) *and*
  dropped the `dec eax`; the two cancel, so `TILE[v]` is exactly the frame 4.95 draws for `v`. A global
  `+1` was shipped twice and was wrong both times — see `Reverse-Engineering.md` for why.
- **Sheet-2 ground needs a LOOKUP TABLE.** TileB was re-packed into the merged sheet, not appended
  (232 distinct deltas), so no offset can express it. `game-data/Tile533Map.csv`, applied by
  `Server/TileTranslation.cs`.
- **Verified offline, end to end**: all 1,719,261 drawn cells of the 1,750 shipped maps render
  byte-identically under the 4.x and 5.33 pipelines. Re-run that check rather than eyeballing a map.
- **Object indices are NOT renumbered** — the object short indexes `SObj.tbl`, whose id space both clients
  share (5.33 appended entries; 7,582 of the first 7,608 records carry identical sprite frames). Shifting it
  along with the ground — which the superseded single `P1998_TILE_OFF` knob did — moves every door, wall and
  tree by one object.
- **…but 5.33 RE-AUTHORED the object COLLISION flags, and it collides locally.** 362 flag bytes differ over
  the shared id range; 234 ids block a direction on 5.33 that they did not on 4.x, making **18,025 cells
  (1.05%, in 620 of 1,750 maps)** unwalkable on 5.33 even though the 4.x maps intend them to be walked.
  The server never sees these — the client refuses before sending a walk request, so there is no `BLOCKED`
  line in the log. **Fixed by patching the client** (`re/patches/patch_533_sobj_flags.py`, 362 bytes in
  place). The server-side fallback for an unpatched client is `game-data/Obj533Fix.csv`
  (`P1998_OBJ_FIX_533`, default `free` — which now applies *nothing*: its four "look-alike" substitutions
  were false matches, retracted 2026-08-26 after they scrambled every guild hall's curtains on 5.33), and
  it buys walkability by deleting artwork, only reaching full parity at `all`. See `Terrain-Streaming.md`.
- **`.cmp` local map files are a red herring** for rendering. The 5.33 client *does* stat/open
  `Maps\TK######.cmp`, and the format was fully reversed (see RE doc), but terrain visibly comes from
  the server stream, not the file. You do **not** need to ship `.cmp` files.
- **Server tags client version by port** — no wire sniffing; the 4.95 code path is never entered by a
  5.33 session.
