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
| `tile` | the **whole** ground `u16` (`MapData.GroundWord`), through `TileTranslation.Ground` | Do **not** mask it first. `v >= 0xC000` selects the second legacy sheet at index `v-0xC000` (30.58% of all cells) and needs the `game-data/Tile533Map.csv` lookup; `v < 0xC000` is sheet 1 and passes through **unchanged**. See `Reverse-Engineering.md` for the two blitters side by side. |
| `pass` | `(g16 >> 14) & 3` | **Does not affect rendering on 5.33** — `sub_4443d0` does `and ecx,0xffff` and never looks at this short. Note the bits it is derived from are really the *sheet selector*, so this is currently "sheet 2 ⇒ pass 3", which is a coincidence of encoding rather than authored passability. Left as-is deliberately (it is what the collision path has always used); revisit separately. |
| `obj`  | object index = `o16 & 0x3FFF` | An `SObj.tbl` entry id, **not** a TILEC frame — passed through unshifted, because both clients share that id space. `0` = none (a sentinel; never offset it). Objects (e.g. `1542`) stream and render. |

### Checksum

Mithia computes `nexCRCC` over the cells and **skips the send** if it equals the client's checksum
(no-change optimization). Our server currently **always sends** — correct, just not bandwidth-optimal.
The `0x05` pull always sends because the client forces its checksum to `0`.

## Server implementation

- Handler: `Session.HandleMapRequest` (routed from `0x05`/`0x06` for V533 sessions only).
- Tile source: `Server/MapData.cs` loads the 4.x `TK<id>.map` (searches `P1998_MAPS`, repo `game-data/maps/`,
  then the client installs) and exposes `Tile/Pass/Obj`. Map 32 is committed at `game-data/maps/TK32.map`;
  any other map loads on demand from the installed 4.x map set.
- Translation: `Server/TileTranslation.cs` — ground `P1998_TILE_OFF_533` defaults to **`1`**, object
  `P1998_OBJ_OFF_533` to **`0`** (the SObj id space is shared). The old single `P1998_TILE_OFF` still
  works but now moves the **ground only** — it used to move the object index too, which meant using it
  to straighten the ground silently shifted every door, wall and tree by one at the same time.
- Diagnostics: `P1998_MAP_DIAG=sweep` ramps the ground index `0..28550` across the rect (to probe which
  indices render); `P1998_MAP_DIAG=solid:N` fills one index. Drive via `re/Run-Diag-Sweep.bat` /
  `re/Run-Diag-Solid.bat` (set env inside a `.bat` — PowerShell's `set VAR=val` is a no-op; use `$env:`).
- **`P1998_MAP_DIAG=ground:N`** — fills the rect with the 4.x ground **word** `N` put through the real
  translation, so it exercises the sheet selector. `ground:652` is sheet-1 `TileA[651]`; `ground:49152`
  (`0xC000`) is sheet-2 frame 0. This is the one to reach for when checking the tile map, because it is
  the only diag that covers the 30% of the world that lives on sheet 2.

  The superseded `calib` mode is gone: it rested on a calibration tile chosen with a renderer that read the
  wrong EPF TOC field, so both of its "expected" colours were the wrong frames. Note that a diag fill
  proves less than it looks like it does — several consecutive frames are often the same material, so a
  fill can be consistent with three different offsets at once. The authoritative check is the offline
  both-pipelines render comparison described in `Reverse-Engineering.md`, not anything on screen.

### Cell patches (doors) use the same opcode — RESOLVED

`Session.SendObjRow` emits `0x06` too, and it must use the **same per-version cell shape** as the stream.
It used to send two-short cells to both clients, which was wrong for 5.33 and produced the reported `o`
bug: opening a door repainted the strip with garbage that only corrected itself on the next full refresh.

The client leaves no room for interpretation — `sub_469060` reads **three** BE u16 per cell
unconditionally (three reads storing to `[esi]`, `[esi+2]`, `[esi+4]`, six-byte stride
`lea ecx,[eax+eax*2]` / `[edx+ecx*2]`), with no length check and no two-short path. Given 4 bytes per
cell it consumes the next cell's bytes as its own and runs off the end of the body.

Both call sites now go through one writer, `Server/MapCell.cs`, pinned by `Tests/MapCellTests.cs`.

**The middle short is a single bit.** 5.33 merges it as `new = old ^ ((old ^ read) & 1)` — it takes only
bit 0 and preserves the rest of the existing cell value. So our 4.x-derived `pass` of 3 is equivalent to
1, and this is the same 0/1 field RTK 7.x calls `mid`.

## Object collision: 5.33 re-authored `SObj.tbl`, and it collides client-side

Both clients run object collision **locally**, against their own `SObj.tbl` — that is why you cannot walk
through a hut wall that sits on `pass=0` ground. 5.33 ships a different table: **362 flag bytes differ over
the shared id range 1..7608**, 234 ids block a direction there that they did not on 4.x, and 125 go the
other way. The sprite data still lines up (7,582 of 7,608 shared ids have identical frames) — only the
collision bytes were rewritten, presumably alongside 5.33's own map set, which we do not use.

**Symptom and how to recognise it.** A tile the 4.x maps intend as walkable is impassable on 5.33, and the
server log shows **nothing** — no `walk … BLOCKED` line — because the client refuses before it sends the
request. Found via Arctic Village (3811) 35,32 / 36,32, a staircase under objects 327 and 320, both `0x00`
on 4.x and `0x0F` (solid all four sides) on 5.33. Scope: **18,025 cells, 1.05%, in 620 of 1,750 maps.**

**Why the server cannot fix it cleanly.** Object graphic and object collision are the *same* `u16` on the
wire, so the only levers are "send a different object" or "send none" — and "send none" deletes artwork.
Rendered-content matching once claimed a visually identical, usably-flagged substitute for **4 of 128**
affected objects; that claim was retracted 2026-08-26 (see the second correction below), so today every
affected object can only be blanked.

**What we do** — `game-data/Obj533Fix.csv`, applied in `TileTranslation.Object`, scoped by
`P1998_OBJ_FIX_533`:

| scope | applies | cells | cost |
|---|---|---|---|
| `off` | nothing | — | 18k cells stay unwalkable |
| `free` **(default)** | proven look-alike substitutions — **currently none** (the 4 shipped were false matches, retracted 2026-08-26) | 0 | **none — a true identity** |
| `decor` | + blank objects 4.x marks `0x00` | +1,915 | a decoration sprite disappears (the Arctic stair lip is a 24×7 strip) |
| `all` | + blank objects with a real 4.x directional block | +16,110 | **deletes visible structures** (obj 1243 alone is 1,817 cells) |

The default is `free` on purpose: every wider scope buys walkability by deleting artwork, and that trade
belongs to whoever runs the server. At `free` the Arctic Village stairs remain impassable on 5.33 — fixing
them without changing what is on screen requires the client-side patch below.

Walkability is correct at every scope: the server enforces the 4.x flags itself (`ObjectFlags` in
`HandleWalk`), so a blanked object still blocks exactly as 4.x intended — as a `0x04` snap-back rather than
a client-side refusal. The trade is cosmetic (missing art, slight rubber-banding), not behavioural.

**The lossless fix — TAKEN, 2026-08-20.** Patch the client's `SOBJ.TBL` flag bytes to the 4.x values: 362
bytes, identical length, in place (the `SOBJ.TBL` PAK entry, at offset 140 of the stock `Tile.dat`) — no
repack. [`re/patches/patch_533_sobj_flags.py`](../../re/patches/patch_533_sobj_flags.py) derives the byte
list at run time from both tables, so it cannot rot against a different build or an edited
`game-data/SObj.tbl`; `--check` / `--revert` / `--minimal` as usual.

**Verified end to end against the patched install:** `Tile.dat` size and all 8 PAK entries unchanged, 0
flag mismatches vs 4.x over ids 1..7608, the 5,088 5.33-only records untouched, and across all 1,840 maps
**cells reachable on 4.x but not on 5.33 goes 17,841 → 0** with zero artwork loss (Arctic Village: 2,437 →
2,921 reachable == exact 4.x parity). It fixes the under-blocking direction too — 3,296 cells where 5.33
let the client start a step the server then refused, i.e. rubber-band snap-backs.

Why patch rather than keep the client stock: this is a **structural** divergence, not a cosmetic one. Two
players in the same room had different walkable geometry, and no server-side scope fixes that without
deleting artwork. It is also not a step away from later eras — RTK/7.x's own `SObj.tbl` agrees with **4.x**
on 352 of these 362 ids (4.x vs 7.x differ by 21 bytes over the shared range; 5.33 vs 7.x by 367), so 5.33's
re-authoring is a one-generation outlier that the next era undid.

A patched client should run `P1998_OBJ_FIX_533=off` — the workaround below exists only to paper over this.

**Correction (2026-08-20): `TILEC` was NOT re-packed.** This page and `TileTranslation.cs` both used to say
it was, by analogy with `TileB`. Measured: 5.33's `TILEC.EPF` is 4.x's `TileC.epf` with one null frame
prepended and new frames appended — TOC entry `i` equals 5.33's entry `i+1` for **all 16,408** frames, and
the pixel region is byte-identical over the 4.x length. Same cancelling `+1` as `TILE`/`TileA`. Combined
with `SObj` frame lists being identical for every shared id our maps place (the 25 that differ are empty
tail padding in 4.x, used by 0 cells), **object artwork needs no translation at all** — only the collision
flags ever differed.

**Correction (2026-08-26): the 4 "look-alike" substitutions were false matches — retracted.** All four
paired id → id+1 (553→554, 571→572, 600→601, 694→695), which is the fingerprint of the wrong-TOC-field
renderer (it displayed mostly the *next* frame, so object N rendered as N+1's true art — 553's frames are
`[1202,1199]` and 554's are `[1203,1200]`, exactly the off-by-one; 571 is 2 frames tall where 572 is 3).
Live symptom: every guild hall's curtain run (`571..579`, three rows per hall — TK2510 "Warrior Sword"
rows 5/10/17) streamed to 5.33 with its left pillar piece 571 rewritten to curtain-rod piece 572 —
"curtains scrambled, pillars missing" — while 4.95, the map editor, and `re/render_maps.py` all drew the
file correctly, and the server's own live state was byte-identical to the file (nothing in `state/` was
involved). The rows are now `structural` suppressions like the rest of their flag class, scope `free` is
empty (`Tests/TileTranslationTests.cs` pins it at zero), and any future substitute must be proven by
matching frame lists id-for-id in `SObj.tbl` — never by a renderer.

## Open questions

- **Passability**: *render* is settled — 5.33's blitter masks the tile short with `0xffff` and never reads
  `pass`. What remains is that `pass` is derived from bits that are actually the **sheet selector**, so
  ~30% of cells report `pass=3` because they are sheet-2 tiles, not because they were authored solid.
  Where 4.x collision really comes from (object layer / `SObj.tbl` flags?) is still open, and this is the
  next thing to chase if walking restrictions look wrong.
- **Multi-map / warps**: streaming works for any loaded map, but map transitions (server-side position +
  `0x15` + new stream) aren't exercised yet.
- **Checksum**: implement `nexCRCC` to enable the no-change skip if refresh bandwidth becomes an issue.
