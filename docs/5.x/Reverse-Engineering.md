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

The 4.x `TileA` **pixel region** is a verbatim prefix of `TILE.EPF`'s — but the **frame indices are
not**, and conflating the two is the trap here. `TILE.EPF`'s frame table has one extra entry at the
front: TOC entry 0 is a NULL frame (`w=0, h=0`), where `TileA.epf` frame 0 is a real 24×24 tile. The
4.x artwork therefore resumes at frame **1**.

Verified byte-for-byte against both installs (compare each frame's TOC box + pixel bytes):

| legacy sheet | 5.33 sheet | relationship |
|---|---|---|
| `TileA.epf` (9,922 frames) | `TILE.EPF` (28,551) | `TileA[i]` ≡ `TILE[i+1]`, identical for 9,921 / 9,922 |
| `TileC.epf` (16,409) | `TILEC.EPF` (29,414) | `TileC[i]` ≡ `TILEC[i+1]`, identical for 16,408 / 16,409 |

**But the client does not need a shift, because it also dropped the `-1`.** See the two blitters below.

## The two ground blitters — the definitive comparison

| | 4.x (`NextAeon\NexusTK.orig.exe`) | 5.33 (`NextAeon5\NexusTK.exe`) |
|---|---|---|
| per-cell redraw | `sub_44d0e0` | `sub_465b50` |
| cell buffer | `[this+0x3ec]`, **4 bytes/cell** | `[this+0x3d0]`, **6 bytes/cell** |
| ground blitter | `sub_431820` | `sub_4443d0` |

```c
/* 4.x  sub_431820 */                      /* 5.33  sub_4443d0 */
if (v == 0) return;                        if ((v & 0xffff) == 0) return;
if (v >= 0xC000) {                         idx = v & 0xffff;          /* no branch,      */
    sheet = sheet2;  idx = v - 0xC000;     if (idx < 0) return;       /* no subtraction, */
} else {                                   if (idx >= frameCount) return;
    sheet = sheet1;  idx = v - 1;          entry = toc + idx*24;      /* one merged sheet */
}
```

Three consequences:

1. **The top two bits are a sheet selector, not passability.** `0xC000` is not a flag pair — it is the
   `base` field in `TileB.tbl`'s header (`count u32, palCount u32, base u16`; `TileA.tbl` has `base = 1`,
   which is the other subtrahend). 30.58% of all shipped cells are in the sheet-2 range.
2. **Sheet-1 needs no shift.** 5.33 added a null frame at `TILE[0]` *and* removed the `dec`. `TILE[v]` ≡
   `TileA[v-1]` ≡ what 4.95 draws for `v`. The changes cancel exactly.
3. **5.33 ignores the second short when drawing** (`and ecx,0xffff`). That answers the long-standing
   "what does `pass` control on 5.33" question for the *render* path: nothing.

Sheet 2 is not a shift either — TileB was **re-packed** into `TILE.EPF` (232 distinct index deltas), so it
needs the lookup table in `game-data/Tile533Map.csv`.

### A global `+1` was shipped twice and was wrong both times — read this before changing it again

The `+1` was inferred from rendered tile colours (`solid:67` "drew flowers where `TileA[67]` is water").
**That evidence was an artifact of a broken renderer.** An EPF TOC entry is
`[L,T,R,B (4× i16)][pixelOffset u32][pixelEnd u32]`; the tool used `pixelEnd`, which for a 24×24 frame is
`pixelOffset + 576` — so it read 576 bytes into a 624-byte stride and returned mostly the *next* frame.
Every colour identification built on it, including the calibration tile, was off by one.

The first revert was *also* wrong, for a different reason: that build simultaneously changed
`Session.SendObjRow` to a three-short cell patch, which can desync the whole `0x06` run. Two unverified
changes, one observation, no attribution.

Three lessons, all paid for: ship one speculative change at a time; **never settle a tile-index question
from a screenshot of a map** (an off-by-one lands on a neighbouring variant of the same material, so whole
maps render coherent-but-wrong); and **validate a measuring tool against a known answer before trusting
it** — the renderer would have failed instantly on "does `TileA[i]` equal `TILE[i+1]`?"

### How the mapping is actually established

Not by eye, and not from the client. Every legacy frame is rendered to RGB *through its own palette* and
matched against every frame of `TILE.EPF` rendered the same way; then every cell of all 1,750 maps is
rendered under both pipelines and compared. Current result: **1,719,261 / 1,719,261 drawn cells identical**.
Corroborating checks that all agree: `TileA[i]` ≡ `TILE[i+1]` for 9,907 of 9,908 uniquely-matched frames;
TileA's 23 palette-run boundaries land at exactly `+1` in `TILE.TBL` (23/23, zero at any other offset);
and legacy palette indices fit `palCount` exactly (23/23/40 against 24/24/41) once the TBL header is read
as 10 bytes.

The object short needs no shift, for a different reason: it indexes `SObj.tbl`, and 5.33's table is the
4.x table with entries appended — 7,583 of the first 7,608 records are byte-identical.

## Asset file formats (both eras)

Getting either of these wrong silently produces plausible-but-shifted results rather than an error, and
both did exactly that during this investigation.

### `.epf` sprite sheet

```
u16 frameCount   u16 width   u16 height   u16 unknown   u32 tocRel
<pixel data>                                  starts at file offset 12
<TOC>                                         starts at 12 + tocRel, 16 bytes per frame
```

TOC entry: `i16 left, i16 top, i16 right, i16 bottom, u32 pixelOffset, u32 pixelEnd`.

- **Use `pixelOffset` (entry+8), NOT `pixelEnd` (entry+12).** For a 24×24 frame `pixelEnd == pixelOffset+576`,
  and the frame stride is 624 (576 pixels + a 48-byte stencil) — so reading at `pixelEnd` lands 576 bytes
  into the stride and returns mostly the *next* frame. This single mistake produced two wrong `+1` shifts.
- Pixel bytes are palette indices; the frame is `(right-left) * (bottom-top)` bytes at `12 + pixelOffset`.
- `12 + tocRel + 16*frameCount` should equal the file size exactly — a cheap parse check.

### `.tbl` frame table

| | header | entry |
|---|---|---|
| 4.x (`TileA/B/C.tbl`) | **10 bytes**: `u32 count, u32 palCount, u16 base` | 2 bytes `[flag, palette]` — palette is the **high** byte |
| 5.33 (`TILE/TILEC.TBL`) | **4 bytes**: `u32 count` | 2 bytes little-endian `palette | (flag << 15)` — palette is the **low** byte |

- The 4.x header's third field, **`base`, is the constant the client subtracts** from a map's ground word
  for that sheet: `0x0001` in `TileA.tbl`, `0xC000` in `TileB.tbl`, `0x0001` in `TileC.tbl`. It matches the
  `dec eax` / `sub eax,0xC000` immediates in `sub_431820` exactly.
- Reading the 4.x header as 8 bytes shifts every palette by one frame and pushes palette indices past
  `palCount` (max 192 against 24 palettes). Read as 10, they fit exactly: 23/23/40 against 24/24/41.

### `.pal` palette

`u32 count`, then one block per palette, each containing the magic `"DLPalette"`. Block headers are
**variable length**, so anchor the 256-entry × 4-byte colour table to the **end** of each block, not to a
fixed offset after the magic.

### `SObj.tbl` static-object table

```
u32 count
u8  flag[0]
per object z = 0 .. count-1:
    u8 tileCount
    tileCount * u16     frame ids
    FF FF FF FF 00      separator
    u8 flag[z+1]        the NEXT object's flag
```

The flag **precedes** its object's frame list, so a naive walk yields frames one record ahead of the flags.
Attribute `realFrames[z] = rawFrames[z+1]`; the flags land correctly as read. With the phase corrected,
7,582 of the 7,608 shared ids have identical frames across the two eras — the check that confirms the parse.
Flag bits (RTK `map.h`): `UP=1 DOWN=2 RIGHT=4 LEFT=8`; `0x0F` = solid on all sides.

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
  ground top-2-bits = passability) and exposes `Tile/Pass/Obj`. Search order: `P1998_MAPS` env → repo
  `game-data/maps/` → 4.x client install → 5.x client install.
- **Diagnostics**: `P1998_MAP_DIAG=sweep` (ramp ground index 0..28550 across the rect) /
  `solid:N` (fill one index), plus `P1998_TILE_OFF` (± shift, ended up `0`). Launchers:
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
