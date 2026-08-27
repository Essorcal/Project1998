# NexusTK Map Editor

A visual editor for Project1998's world maps. It renders the game's maps with the real
5.33 client tile art — verified pixel-identical to the running game — and edits the 4.x
`.map` files the server actually serves, with import/export for both client formats.

![The editor showing Kugnae](docs/img/ui-main.png)

## Installation

### The easy way (no tools required)

1. Grab the **NexusTK-Map-Editor** folder (contains `NexusTK-Map-Editor.exe` and a
   `wwwroot` folder — keep them together).
2. Put that folder anywhere **inside your Project1998 checkout** (for example
   `Project1998\dist\NexusTK-Map-Editor\`). The editor finds the maps by looking for a
   `game-data\maps` folder in the directories above it.
3. Double-click `NexusTK-Map-Editor.exe`.

A console window opens, decodes the tileset (about a second), and your browser opens the
editor automatically. **Keep the console window open** — closing it stops the editor.

The editor also needs the 5.33 client's `Tile.dat` for the artwork. It looks, in order:

| Where | Notes |
|---|---|
| `P1998_CLIENT5` environment variable | points at the 5.33 client folder |
| next to the `.exe` | drop a copy of `Tile.dat` beside the program |
| `%LOCALAPPDATA%\Project1998\game\533\` | the standard Project1998 client install |

If either the maps or `Tile.dat` can't be found, the console says exactly what's missing
and where it looked.

### From source

```
dotnet run --project MapEditor
```

To build the standalone exe yourself:

```
dotnet publish MapEditor/MapEditor.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:AssemblyName=NexusTK-Map-Editor -o dist/NexusTK-Map-Editor
```

## A quick tour

- **Top bar** — the `.map` / `.cmp` format switch (hover for which client each belongs
  to), zoom controls, Undo, Import, Export, and Save.
- **Left rail** — the tools (hover any icon for what it does): select, marquee, stamp,
  brush, flood fill, rectangle, eyedropper, eraser, passability toggle, and the test
  character.
- **Right panel** — the tile palette, layer toggles, and the map list. **Drag the
  panel's left edge** to make it wider; the palette grows more columns.
- **Bottom bar** — current map, the cell under your cursor (its ground word, pass flag
  and object id), tool hints, unsaved-change count, and zoom.

## Finding tiles

The palette has three modes:

- **All** — the entire tileset, with a page browser and an `id…` box that jumps straight
  to a tile number (decimal, or `0xC0CC`-style hex for sheet-2 words).
- **On map** — only the tiles and objects actually placed on the current map. For most
  editing this is the mode you want: a big city uses a few hundred tiles, not 28,000.
- **★ (favorites)** — your personal palette. **Right-click any swatch** to star or
  unstar it; favorites persist between sessions.

**Shift+click** a second swatch to select a rectangular block of tiles — the brush then
stamps the whole block at once, and the rectangle tool tiles it as a repeating pattern
(a plain click returns to single-tile painting). **Hover a swatch** to see it magnified — objects preview as their full sprite (a tree
shows the whole tree, not one 24px slice). The **eyedropper** works the other way: click
any cell in the map and the palette jumps to that tile, brush ready.

On the Ground tab, **Sheet 1 · walkable** and **Sheet 2 · blocking** are the game's two
ground-tile sheets. This is a real 4.x format rule, not an editor invention: a cell's
sheet choice *is* its collision. Sheet-2 tiles (walls, cliffs, water) always block;
sheet-1 tiles never do. Paint a wall tile and you've painted its collision too.

## Navigating

Zoom from **10% to 500%**: the top-bar − / + buttons, typing a percentage into the box,
`Ctrl` + mouse wheel, or the `+` / `-` keys. Zoomed out you can see a whole city at once;
at 100% and above every step is pixel-crisp.

![Whole-city overview at 10% zoom](docs/img/ui-overview.png)

Pan with **space + drag**, middle-mouse drag, the mouse wheel (`Shift` for horizontal),
or the arrow keys.

## Editing

| Tool | What it does |
|---|---|
| Select | inspect a cell — the bottom bar shows its ground word, pass flag, object |
| Marquee | drag a box; the region is **copied on release** |
| Stamp | click to paste the copied region, as many times as you like — all layers |
| Brush | paint the selected tile (click or drag); Ground/Objects/Pass tabs pick the layer |
| Fill | flood-fill the contiguous region under the cursor on the active layer |
| Rectangle | drag a box, fills it with the selected tile |
| Eyedropper | pick the tile under the cursor into the palette |
| Eraser | ground → void, object → none, pass → walkable |
| No-entry | toggle a cell's passability (this flips its ground sheet — see above) |

**Ctrl+Z** undoes stroke by stroke. The **Passability overlay** (Layers panel) tints
every blocking cell red. **Save** (or Ctrl+S) writes the `.map` file — the first save of
each map keeps a one-time `.orig` backup beside it.

## The test character

The last tool in the rail drops a walkable test character. Move with the **arrow keys**
or the on-screen pad; blocked cells refuse the step, and walking behind trees, roofs and
walls shows the same translucent ghost the real client draws. Remove the character by
clicking it, pressing `Esc`, or the ✕ on the pad.

![Test character ghosting behind the Buya north gate](docs/img/ui-walkmode.png)

## Import & export

The `.map` / `.cmp` toggle selects the file format the Export button produces:

- **`.map`** — the 4.x client format: headerless, 4 bytes per cell. This is the format
  the server serves and the editor edits natively.
- **`.cmp`** — the 5.33 client format: `CMAP` header + zlib, 6 bytes per cell.

**Import** accepts either. A `.cmp` carries its own dimensions; a headerless `.map` will
ask for them (the editor suggests the likely factor pair from the file size). Both modes
render with the 5.33 tile art — that's the correct look for both eras, since the 5.33
tileset is the 4.x art plus additions.

## Sharing a spot

The address bar carries state you can link to:

```
http://127.0.0.1:5959/?map=330&at=72,15&zoom=200
```

`map` (id), `at` (x,y to center on), `zoom` (percent), and `walk` (x,y to drop the test
character) all work.

## Troubleshooting

- **The browser didn't open** — the console prints the address (usually
  `http://127.0.0.1:5959`); open it by hand.
- **A different port** — if 5959 is taken the editor picks the next free port and says
  so in the console; the auto-opened browser tab always goes to the right one.
- **"Could not find the game data"** — the exe isn't inside a Project1998 checkout; move
  it there or set `P1998_REPO`.
- **"Tile.dat … not found"** — install the 5.33 client, or copy its `Tile.dat` next to
  the exe, or set `P1998_CLIENT5`.

---

*created by Essorcal*
