# Monster Matcher

A local tool to map our **4.95 client sprites** (look id 0–326) to **monster names + stats**,
so the server can spawn named, correctly-statted monsters.

- **Left = truth:** each card shows the *actual* `Monster.epf` sprite the client draws for that
  look id (rendered from the client PAK: EPF frames + per-monster DLPalette). Look id = the
  `Monster.tbl` index, i.e. the value you pass as `0x8000 | lookId` in the `0x07` spawn packet.
- **Right = reference:** the name box autocompletes from **Nexus Atlas** (389 monsters, scraped from
  Wayback Machine snapshots ~2005‑10, pre‑6.5). Picking an Atlas name auto‑fills exp/type.

## Run

```bash
python tools/monster-matcher/monster_matcher.py
```

Opens `http://localhost:8777`. Match sprites to names, then click **💾 Save to repo**.
It writes `data/monster_mapping.json`, which the server reads to name/stat monsters.
Work auto‑saves in your browser as you go; **Download JSON** is a manual backup.

## How the sprites were decoded (for reference)

- `Monster.epf`: header `u16 frameCount, u16 w, u16 h, u16 unk, u32 tocOffset`; pixels at `[12..toc]`;
  TOC at `toc`, 16 bytes/frame: `i16 top, i16 left, u32 pixOff, u32 stencilOff, i16 bottom, i16 right`.
  Frame `w = |right-left|`, `h = |bottom-top|`, raw 8‑bit indices at `12+pixOff`, index 0 = transparent.
- `Monster.pal`: a `DLPalette` container of **20 recolor palettes** (blocks of 1056 B, each starting with
  the ASCII tag `DLPalette`). Within a block the 256 colors are **4 bytes/entry, RGB = the first 3 bytes**,
  starting at **byte offset 38** of the block (empirically pinned by matching a known sprite's dark outline
  ramp + gold body against a Nexus Atlas reference image). Per‑monster palette = the `Palette` column in
  `Monster.tbl`. **The sprites are grayscale material‑index art; the palette is the recolor** — the same base
  sprite appears in many colors (red/blue/green/… dog) by swapping the palette, which is why each block reads
  as a themed ramp rather than a full‑spectrum palette.
- Thumbnail per monster = its walk cycle (`[Starting, Starting+Walk]`) rendered as an animated GIF, bottom‑
  centre anchored so it doesn't jump; falls back to the best single clean frame.

Data files (generated, checked in so the tool runs out of the box):
`monsters.json` (sprites as data URIs), `atlas.json` (Nexus Atlas list).
