# 5.33 `NexusTK.exe` — reverse-engineering reference

RE findings for the 5.33 client (`C:\Program Files (x86)\Nexon\NextAeon5\NexusTK.exe`, build
5.3.3.384). **Image base `0x400000`, non-ASLR** — the VAs below are stable, so Frida can hook them as
`base + (VA - 0x400000)`. Disassembly was done with Python `capstone` + `pefile` against the on-disk exe.

## Function map

| VA | What it is |
|----|-----------|
| `0x463320` | **Opcode dispatcher.** `al = body[0]` (opcode); `idx = byteMap[opcode-3]`; `jmp ptrTable[idx]`. `byteMap` @ `0x463d44`, `ptrTable` @ `0x463c8c` (46 entries, `0x2d` = default). |
| `0x468bb0` | `0x15` map-info parser. Reads mapId@1, width@3, height@5 (all BE u16 via `sub_4a1210`), name, flags. |
| `0x464d60` | Enter-map handler. Stores width→`[field+0x3cc]`, height→`[field+0x3ce]`, mapId→`[field+0x3ca]`; calls the `.cmp` loader. |
| `0x4609a0` | **`.cmp` file loader** (see format below). Inflates into `[field+0x3d0]`. |
| `0x460c40` | `.cmp` reader/teardown helper. |
| `0x469060` | **`0x06` map-data handler** (server→client terrain stream). Reads x0@1, y0@3 (BE u16), w@5, h@6 (u8), then cells BE into `[field+0x3d0]`. |
| `0x4655e0` | Cell getter: returns the 3 shorts `{s0@0, s1@2, s2@4}` at buffer offset `(y*width + x)*6`. |
| `0x465b50` | Per-cell redraw (repaints one cell; fires continuously in the render loop). |
| `0x466b50` | Object-layer draw: keys on `s2` (the object short) → sprite entities via `sub_47edd0`. |
| `0x4e8860` | zlib inflate wrapper (calls `inflateInit`/`inflate`/`inflateEnd` @ `0x4ea170`/`0x4ea190`/`0x4ea010`; zlib 1.1.4, statically linked, **not** exported). |
| `0x4a1210` | read **BE** u16: `b[0]<<8 \| b[1]`. |
| `0x4a1200` | read u8. |
| `0x4a3e50` | read **BE** u16 from a stream, pointer-advancing (used to pull cells). |

### Field/map object offsets (the `esi`/`ebx` "this" in the above)

| offset | field |
|--------|-------|
| `+0x3ca` | mapId (u16) |
| `+0x3cc` | width (u16) |
| `+0x3ce` | height (u16) |
| `+0x3d0` | pointer to the cell buffer (`width*height*6` bytes, 3× u16 per cell) |

### Dispatcher calibration (how opcode → handler was confirmed)

`idx = byteMap[opcode-3]`, then `jmp dword[ptrTable + idx*4]`. Known anchor: `0x15` (map-info) →
`byteMap[0x15-3=18] = 0x0b` → `ptrTable[11]` → wrapper → `sub_468bb0` ✓. Terrain: `byteMap[0x06-3=3]
= 0x02` → `ptrTable[2]` → wrapper → `sub_469060`. So **`0x06` is the terrain opcode**.

## Cipher

Same as 4.95: **NexonInc static 3-stage XOR** (`Protocol.Tk495.TkCrypt.Crypt`, key `"NexonInc."`),
used on both login and game channels for every opcode exercised so far (`0x02/0x05/0x15/0x33/0x08/0x06`).
It handles arbitrary body lengths (verified with the ~1.9 KB terrain packet). The name-keyed/table
cipher (`crypt2`) and the `SvKey1` static-vs-table opcode split are **7.x artifacts** and are NOT used
by 5.33 — the map-info (`0x15`) and terrain (`0x06`) packets both decode correctly with the static key.

## `.cmp` local map format (reversed but NOT needed for rendering)

The 5.33 client stat/opens `Maps\TK%06d.cmp`; the format is fully reversed, but **terrain visibly comes
from the server `0x06` stream, not this file**, so you don't need to ship `.cmp` files. Documented here
for completeness.

```
"CMAP"            4 bytes  magic (client memcmp against [0x55177c])
width             u16 LE
height            u16 LE
<zlib stream>     inflates to exactly width*height*6 bytes
```

- Cell = **3× u16**, buffer offset `(y*width + x)*6`; the renderer reads them native-endian.
- Loader `sub_4609a0`: reads the 4-byte magic, then two raw 2-byte reads for W/H, and **requires the
  file's W/H to equal the server's `0x15` width/height** — otherwise it silently **zero-fills** the
  buffer (→ black). Then inflates the zlib body straight into `[field+0x3d0]`.
- Because our square 33×33 map encodes identically whether W/H are two u16 LE or one `(H<<16)|W` u32 LE,
  either producer works for it.

## `Tile.dat` (graphics archive)

DAT archive at `NextAeon5\Tile.dat`, 8 entries:

| entry | size | role |
|-------|------|------|
| `SOBJ.TBL` | 179 218 | static-object table |
| `TILE.EPF` | 18.3 MB | ground tile pixels — **~28,551 frames** (needs 15 bits to address) |
| `TILE.PAL` | 160 628 | palette |
| `TILE.TBL` | 57 106 | tile frame table (≈ 2×28553) |
| `TILEC.EPF/PAL/TBL` | — | second tile set |

The 4.x `TileA` frames are a verbatim **prefix** of 5.x `TILE.EPF`, so low indices (like floor `651`)
map to the same pixels — which is why the raw 4.x ground index renders correctly on 5.33.

## Tooling

### Frida probes (`re/`)

- **`frida_probe_533.py`** — version-safe (hooks WSOCK32 `connect`/`recv`/`send` + kernel32
  `CreateFileA/W` by export name). Good for seeing the packet + file-access sequence.
- **`frida_probe_533_map.py`** — hooks the client's own terrain handlers by VA to see the **post-decrypt**
  view: `sub_469060` (0x06 → dumps `rect + first cells`), `sub_465b50` (redraw counter), `sub_468bb0`
  (0x15 dims). This is what pinned the leading-flag-byte bug. Run `python re/frida_probe_533_map.py`
  (spawns the client with the right cwd).
  - Frida-17 note: `Module.findBaseAddress` was removed — use `Process.findModuleByName('NexusTK.exe').base`.
    `this.context.esp.add(4).readPointer()` is arg0 at a `__thiscall` entry (stack arg pushed by the wrapper).

### Server-side

- **`Server/MapData.cs`** — loads a 4.x headerless `.map` (4 B/cell `[ground u16 LE][object u16 LE]`,
  ground top-2-bits = passability) and exposes `Tile/Pass/Obj`. Search order: `NEXUS_MAPS` env → repo
  `data/maps/` → 4.x client install → 5.x client install.
- **Diagnostics**: `NEXUS_MAP_DIAG=sweep` (ramp ground index 0..28550 across the rect) /
  `solid:N` (fill one index), plus `NEXUS_TILE_OFF` (± shift, ended up `0`). Launchers:
  `re/Run-Diag-Sweep.bat`, `re/Run-Diag-Solid.bat` (set env inside the `.bat`; PowerShell `set VAR=val`
  is a no-op — use `$env:` or these bats).

## Investigation timeline (why this was hard)

1. Assumed terrain came from the local `.cmp`; fully reversed and reproduced that format, but every
   `.cmp` we crafted rendered black — because the `.cmp` was never the render source.
2. Reversed the draw path and dispatcher → found the **`0x06` server→client terrain stream**;
   cross-checked against the Mithia 7.x `clif_sendmapdata` source (which matched, modulo the flag byte).
3. Implemented the server stream → still black. A client-side Frida hook on `sub_469060` showed the
   handler reading `w=0` (a one-byte field shift from a spurious leading `0x00` copied from 7.x).
   Removing that byte → terrain renders.
