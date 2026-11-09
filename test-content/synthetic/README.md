# Synthetic content profile

A small content root, every byte of it invented, that a server can be pointed at instead of `game-data/`. It gives a
client test known terrain, walls, warps and an entity without carrying any retail content. Nothing reads it by
default: the server opens `P1998_GAME_DATA` (default `<root>/game-data`), and the release bundle ships `game-data/`
only.

`manifest.json` is the machine-readable description: the maps and the start tile, the wall band, every object with
its table flag and its server flag, the warps both ways, the NPCs with their runtime ids and the clerk's dialog, and
the environment to set. Every file here except this README is written by the generator.

## What is in it

- **Map 7400, "Synthetic Start"**, 64 x 64, floor ground word 1. The start tile is (32, 32).
  - A band of blocked ground (word 0xC001) on row 26 from x 16 to x 47, with one gap at (32, 26) that holds object 17.
  - Objects 1 to 15 carry flags 1 to 15 (UP 1, DOWN 2, RIGHT 4, LEFT 8 and every combination) in a 5 x 3 grid on the
    even tiles from (34, 36) to (42, 40). Each is open on all four sides, so every heading can be tried.
  - Object 16 is decor, flag 0, at (28, 32).
  - Object 17 is 0x0F in `SObj.tbl` and 0 on the server through `ObjectFlagOverrides.csv`: an entrance that looks
    solid and lets the player through.
  - NPC 1000, "Synthetic Sentinel", stands still at (30, 29), in view of the start tile. A fresh server gives it
    runtime id 300000. It has no script and no menu entries, so a click on it opens nothing: the server sends no
    box.
  - NPC 1001, "Synthetic Clerk" (identifier `SyntheticClerk`), stands still at (34, 29), the sentinel's tile mirrored
    across column 32, also in view of the start tile. A fresh server gives it runtime id 300001: NPCs are numbered
    from 300000 in `NPCs.csv` order. Its dialog is described below.
  - Stepping south into (32, 56) warps to map 7401 at (20, 13).
- **Map 7401, "Synthetic Annex"**, 40 x 32, floor ground word 2. Stepping north into (20, 12) warps back to map 7400
  at (32, 55).
- Both maps are indoor in `Maps.csv`, which holds the weather clear. `npc_dialog.lua` holds the clerk's dialog; the
  other three Lua files are inert stubs the loader accepts.

## The clerk's dialog

A click on the clerk opens a menu (`0x30` kind `02 02`), "Synthetic Clerk: pick a test.", with three entries:

1. **"Type a line"** opens an input box (`04 04`), "Type a line for the clerk.".
   - A submit (`0x3A` kind 4, step 2) is answered with a text box (`00 01`) reading "You typed N characters: TEXT",
     where TEXT is the text the server read and N its length in bytes. An empty submit gives
     "You typed 0 characters: ".
   - Any other reply to the input box, such as its Previous (kind 4, step 0), is answered with "Nothing was
     submitted.".
2. **"Read the notice"** is answered with a text box, "The notice is blank. It is a synthetic notice.".
3. **"Goodbye"** ends the conversation, and so does a pick outside 1-3 or any reply to the menu that is not a pick:
   the server sends nothing more.

Every text box has a next arrow, and its next ends the conversation. The entity's `dialog` block in `manifest.json`
states all of this, the submit's answer as the pattern `You typed {N} characters: {TEXT}`. The script and the
manifest are written from the same strings, and `Tests/SyntheticProfileLoadTests.cs` drives the whole conversation
through the server's own click, dialog and reply code and holds every box it sends to the manifest.

## Regenerate

```
dotnet run --project Tools -- synthetic-profile
```

The generator is `Tools/SyntheticProfile.cs`. It refuses to write a layout that breaks one of the promises above (two
NPCs on one tile, a dialog the manifest describes with no script, or a script with no description, among others).
`Tests/SyntheticProfileTests.cs` regenerates the profile into a temp folder and fails if any committed byte or file
differs, and fails if any file, data row, name, dialog sentence or map id here is also in `game-data/`.

## Point a server at it

Set these for both the login server and the game server. Use absolute paths: the server uses a path as given.

```
P1998_GAME_DATA=<repo>/test-content/synthetic
P1998_MAPS=<repo>/test-content/synthetic/maps
P1998_SOBJ=<repo>/test-content/synthetic/SObj.tbl
P1998_MAPS_INSTALL_FALLBACK=0
P1998_STATE=<a new, empty directory>
P1998_START_POINT=7400,32,32
```

`P1998_START_POINT` needs the start-point setting. A server without it ignores the variable and starts a new
character on map 4711, which this profile does not have.

Expect warnings at boot: the profile has only the tables above, and the server warns about each one it does not
find. `--selftest` fails on this content, because it checks retail rows.

A retail 4.95 client draws these ids with its own art and collides with its own `SObj.tbl`, so the manifest, not
that client, says what each object does.
