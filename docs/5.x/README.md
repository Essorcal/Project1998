# NexusTK 5.x (client 5.33) — server support notes

This folder documents everything specific to serving the **NexusTK 5.33 client**
(`C:\Program Files (x86)\Nexon\NextAeon5`, build **5.3.3.384**, exe dated Jan 2003) from the
local C# `Project1998`. The 4.95 protocol is documented separately in
[`../NexusTK-4.95-Protocol.md`](../NexusTK-4.95-Protocol.md); this folder only covers where 5.x
**differs**.

> **Status (2026-07): world entry + terrain both working.** The 5.33 client logs in, enters the
> world, renders the character/UI, and now renders **terrain** (ground + objects) streamed from the
> server. Movement, stats-panel fidelity, and multi-map coverage are the remaining polish items.

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
| [`Client-and-Server-Setup.md`](Client-and-Server-Setup.md) | Redirecting the 5.33 client to the local server, the unified dual-client (4.95 + 5.33) server design, and how to run/test. |
| [`Terrain-Streaming.md`](Terrain-Streaming.md) | The `0x05`/`0x06` map-data request/response protocol — the core 5.x rendering path. Definitive packet layouts. |
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
- **Terrain tile indices are RAW** — the 4.x ground index (e.g. floor `651`) renders as-is on 5.33; no
  `+1`/`-1` offset. The `TILE.EPF` frames are a superset of the 4.x tiles at the same low indices.
- **`.cmp` local map files are a red herring** for rendering. The 5.33 client *does* stat/open
  `Maps\TK######.cmp`, and the format was fully reversed (see RE doc), but terrain visibly comes from
  the server stream, not the file. You do **not** need to ship `.cmp` files.
- **Server tags client version by port** — no wire sniffing; the 4.95 code path is never entered by a
  5.33 session.
