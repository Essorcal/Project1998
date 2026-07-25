# 5.x terrain streaming — the `0x05` / `0x06` map-data protocol

This is the core of 5.x rendering. Unlike 4.95 (which draws terrain from a local `Maps\TK<id>.map`),
the **5.33 client requests the tiles for its viewport from the server** and the server streams them
back. Confirmed two independent ways: reversing `NexusTK.exe` (handler `sub_469060`) **and** the Mithia
7.x reference server source (`clif_parsemap` / `clif_sendmapdata`) — with one 5.x-vs-7.x difference
noted below.

## Where it fits in world entry

The 5.33 client is driven into the world with the same burst as 4.95:

```
server -> client (in order):
  0x02 .00      ENTER-WORLD trigger (payload byte 0x00 builds the world object)
  0x1E          ack
  0x20          time-of-day
  0x05          YOUR entity id  (binds camera/input to the self player)
  0x15          map-info (mapId, width, height, name, light)   <-- see below
  0x04          self x/y + camera scroll anchor
  0x33          self appearance
  0x08          stats
```

Immediately after `0x15`, the client:

1. stat/opens `Maps\TK<mapId-6digit>.cmp` locally (ignored for rendering — see RE doc),
2. **sends `0x05`** (initial full-map request) — usually twice,
3. as the player moves, **sends `0x06`** (incremental view-rect refresh).

If the server does not answer these, the map stays a **black void**. Answering them with `0x06`
map-data packets is what makes terrain appear.

## `0x15` map-info (server → client) — the dims contract

The client stores `width`/`height` from this packet and later **requires the streamed map data to match
those dims** in the `.cmp` path; for streaming it simply drives the viewport math. Body layout (all
u16 **big-endian**):

```
[0..1] mapId     [2..3] width     [4..5] height     [6] flag(5)     [7] realm(0)
[8]    nameLen   [9..]  name...    then light (encoding still fuzzy; 4.95 uses BE u16)
```

Parser: `sub_468bb0`; each u16 read via `sub_4a1210` (`b0<<8 | b1`, big-endian).

## Request: client → server (`0x05` initial, `0x06` refresh)

Both opcodes carry a view rectangle. **`0x05` = initial pull** (client forces the checksum to 0 so the
server always sends a full block); **`0x06` = incremental refresh** (carries the client's current
checksum). Decrypted body layout:

```
offset  field           type
0..1    x0              u16 BE   top-left tile X of the requested rect
2..3    y0              u16 BE   top-left tile Y
4       w               u8       width  (tile count, not an end coord)
5       h               u8       height (tile count)
6..7    checksum        u16 BE   client's current checksum for that rect (0 on the 0x05 pull)
```

(These are body offsets — i.e. after `AA len len op inc`. In the raw packet they sit at offsets 5,7,9,10,11.)

The server clamps the rect to the map, reads its own tiles, and replies. The client may request
off-map rects while edge-scrolling (e.g. `x0=513`); clamp `w`/`h` to `0` and reply empty — harmless.

## Response: server → client (`0x06` map-data)

```
AA | len(u16 BE) | 06 | inc | <body>

body:
  offset  field   type
  0..1    x0      u16 BE
  2..3    y0      u16 BE
  4       w       u8       (= requested w, clamped to map)
  5       h       u8       (= requested h, clamped to map)
  6..     cells   { tile:u16 BE, pass:u16 BE, obj:u16 BE } repeated w*h times, row-major
```

The client draws each cell at `(x0 + ix, y0 + iy)` for `iy in [0,h)`, `ix in [0,w)`.

### ⚠️ 5.33 has NO leading "flag" byte (differs from Mithia 7.x)

Mithia's 7.x `clif_sendmapdata` writes a `0x00` byte immediately before `x0` (`WBUFB(buf2,5)=0`).
**The 5.33 client does not expect it** — it reads `x0` immediately after `op+inc`. Emitting that extra
byte shifts every field by one, so the client reads `w=0`, draws zero cells, and the map stays black.
This one byte was the entire "void" bug. Verified with `re/frida_probe_533_map.py`: with the byte, the
client's handler read `rect(0,2048) w=0 h=19`; without it, `rect(8,0) 19x17 first=[t=651 p=0 o=0]`.

### Cell field semantics

| field | source (from the 4.x `.map`) | notes |
|-------|------------------------------|-------|
| `tile` | ground index = `g16 & 0x3FFF` | **raw**, no `+1`. Floor `651` renders correctly. `TILE.EPF` frame. |
| `pass` | passability = `(g16 >> 14) & 3` | top 2 bits of the 4.x ground word. Exact render/movement role still being nailed down; `0` works for visible floor. |
| `obj`  | object index = `o16 & 0x3FFF` | `TILE.EPF`/`SOBJ` object frame; `0` = none. Objects (e.g. `1542`) stream and render. |

### Checksum

Mithia computes `nexCRCC` over the cells and **skips the send** if it equals the client's checksum
(no-change optimization). Our server currently **always sends** — correct, just not bandwidth-optimal.
The `0x05` pull always sends because the client forces its checksum to `0`.

## Server implementation

- Handler: `Session.HandleMapRequest` (routed from `0x05`/`0x06` for V533 sessions only).
- Tile source: `Server/MapData.cs` loads the 4.x `TK<id>.map` (searches `NEXUS_MAPS`, repo `data/maps/`,
  then the client installs) and exposes `Tile/Pass/Obj`. Map 32 is committed at `data/maps/TK32.map`;
  any other map loads on demand from the installed 4.x map set.
- Diagnostics: `NEXUS_MAP_DIAG=sweep` ramps the ground index `0..28550` across the rect (to probe which
  indices render); `NEXUS_MAP_DIAG=solid:N` fills one index. Drive via `re/Run-Diag-Sweep.bat` /
  `re/Run-Diag-Solid.bat` (set env inside a `.bat` — PowerShell's `set VAR=val` is a no-op; use `$env:`).

## Open questions

- **Passability**: what exactly the `pass` short controls on 5.33 (collision only, or render). Currently
  derived from the 4.x top-2-bits; walking restrictions not yet validated.
- **Multi-map / warps**: streaming works for any loaded map, but map transitions (server-side position +
  `0x15` + new stream) aren't exercised yet.
- **Checksum**: implement `nexCRCC` to enable the no-change skip if refresh bandwidth becomes an issue.
