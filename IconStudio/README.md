# NexusTK Icon Studio

A curation tool for Project1998's item icons. The 4.95 client ships 1,310 icon frames and
the 5.33 client 2,304, but the item registry points at icons up to 5,752 — those items draw
blank. The live retail client carries every one of them at 2x size in the same id space, so
the missing art exists; it just needs bringing down to size, one icon at a time, by someone
looking at it. Icon Studio shows each icon's retail source next to the frames the older
clients already have, proposes downscaled candidates, lets you fix them pixel by pixel
against the frame's real palette, and keeps what you approve as **drafts** — while **never
writing a game file or a client install itself**. Export builds a patched item archive into
the tool's own `saved\` folder for you to install deliberately.

## Installation

### The easy way (no tools required)

1. Grab the **NexusTK-Icon-Studio** folder (the exe, a `wwwroot` folder, and a few runtime
   DLLs — keep the folder together).
2. Put that folder anywhere **inside your Project1998 checkout** (for example
   `Project1998\dist\NexusTK-Icon-Studio\`). The tool finds the item registry by looking for
   `game-data\Items.csv` in the directories above it.
3. Double-click `NexusTK-Icon-Studio.exe`.

It opens as a **single application window** (a WebView2 shell — no console, no browser
tab); closing the window shuts everything down, and the window size and position are
remembered. Prefer your own browser? `Icon Studio (browser).bat` runs it with `--browser`:
a tab opens instead, and the server exits itself about 15 seconds after the last tab
closes. (`--no-browser` starts the bare server for tooling; `--port <n>` picks the port,
5961 by default.) Startup output goes to `%TEMP%\nexustk-icon-studio.log`.

It reads the item art of every client it can find; any one of them is enough to start:

| Set | Looked for at | Notes |
|---|---|---|
| retail 2x | `P1998_CLIENT_LIVE\Data\misc.dat`, then `C:\Program Files (x86)\KRU\NexusTK\Data\misc.dat` | the source for every icon the older clients lack |
| 4.95 | `P1998_CLIENT\NexusTK.dat`, then `%LOCALAPPDATA%\Project1998\game\4x\` | what the 4.95 client draws today |
| 5.33 | `P1998_CLIENT5\Misc.dat`, then `%LOCALAPPDATA%\Project1998\game\533\` | what the 5.33 client draws today |

The chips in the top bar show which sets loaded and how many frames each has.

### From source

```
dotnet run --project IconStudio
```

To build the standalone exe yourself:

```
dotnet publish IconStudio/IconStudio.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:AssemblyName=NexusTK-Icon-Studio -o dist/NexusTK-Icon-Studio
```

## A quick tour

- **Left: the icon list.** Every icon id any loaded client knows, with a thumbnail of what
  the 4.95 client draws today (retail art when it has nothing). Search by id or item name.
  The scope buttons narrow it to **No 4.95 art** (icons items use that the 4.95 client
  cannot draw — the red mark), **Drafts**, or **Approved**. A dot marks drafted icons:
  amber = saved, green = approved.
- **Right: sources and candidates.** For the selected icon: the retail 2x frame, the
  existing 4.95 and 5.33 frames, and the saved draft — then three candidates downscaled
  from the retail art (**snap** keeps pixel-art edges crisp and is usually the best start;
  **box** averages; **nearest** samples). The custom size row makes a candidate at any
  size. **Click any card to load it into the editor.**
- **Centre: the pixel editor.** Pencil (`B`), eraser (`E`), eyedropper (`I`, or
  right-click), flood fill (`G`), nudge arrows, undo/redo (`Ctrl+Z` / `Ctrl+Y`). Zoom with
  the − / + buttons, `Ctrl` + wheel, or the `+` / `-` keys. The faint green cross is the
  frame's origin — the client centres the box on the bag slot. The strip at the bottom
  right previews the frame at 1x, 2x and 3x on a bag-like background.
- **Palette.** The frame's real 256-colour block — what you paint is exactly what the
  client can encode. Index 0 is the transparent key.

## Drafts — save, approve, export

Everything the tool produces lives with it under `dist\NexusTK-Icon-Studio\saved\`:

- `saved\icons\<id>.json` — the draft (**Save draft** / `Ctrl+S`): frame, palette,
  approval state, note.
- `saved\icons\<id>.png` — the same frame as a PNG, for your eyes and for **round-tripping
  through an external pixel editor**: open it in Aseprite (or anything), fix it, then
  **Import PNG** brings it back, quantized into the frame's palette. Keep the size.
- `saved\export\<495|533>\` — what **Export approved** writes: `Item.epf`, `Item.pal`,
  `Item.tbl` and the repacked archive (`NexusTK.dat` for 4.95, `Misc.dat` for 5.33) built
  from the client's shipped art plus **only the approved drafts**, with a `manifest.json`
  listing exactly which ids went in. A draft on an existing id replaces that frame; one
  past the end appends (ids in between get blank frames so the id space stays aligned).

**Approve** is the gate: a saved draft does nothing until it is approved, and an export
carries nothing else. **Discard draft** deletes it; **Revert** drops unsaved edits.

Installing an export is deliberate and manual: back up the client's archive, copy the
exported one over it. The server then needs its icon bound raised to the new frame count
(`Content.ItemIconCount`, the GM `look` clamp) so it will send those ids.
