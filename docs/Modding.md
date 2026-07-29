# NexusServer Modding Guide

NexusServer separates the **engine** (C#, in `Server/`) from the **game data** (flat files, in the `data/`
git submodule under `data/game-data/`). Almost everything a modder tunes — spells, items, NPCs, monsters,
shops, maps, drop tables, level curves — lives in CSV or Lua and **hot-reloads with the `!reload` GM command,
no server restart**. This document is the map of what lives where and how to change it.

> Golden rule: **data describes *what*; code implements *how*.** A monster's HP is data; the combat formula
> is code. A spell's mana cost is data; the packet that draws its animation is code. If you're editing a
> number or a line of dialog, it should be a file edit + `!reload`, not a rebuild.

---

## The three tiers

| Tier | Lives in | Changed by | Examples |
|---|---|---|---|
| **Flat config (CSV/Lua)** | `data/game-data/*.csv`, `*.lua` | edit file → `!reload` | mob stats, items, spells, NPCs, shops, drops, warps, level curves |
| **Live GM/player state** | SQLite (`*.db`) | in-game actions / GM commands | inventories, mail, marriages, quest progress |
| **Engine constants** | `Server/*.cs` | edit code → rebuild | wire/packet formats, combat math, the mob AI tick, a handful of scalars |

Everything below is Tier 1 unless noted.

## Hot reload

`!reload` (GM command) re-reads **every** `data/game-data` file — all CSVs and the three Lua scripts — and
rebuilds the world population (added/removed/repositioned spawns and NPCs take effect immediately). A load
error keeps the previous content and reports the error, so a typo can't take the server down. There are no
compile-time content tables left that would need a restart.

---

## Recipe: add or retune a **spell**

Two layers, use whichever fits:

1. **Data-only retune (most spells).** A spell's numbers live in CSVs joined by the spell **identifier**
   (`SplIdentifier`, e.g. `bolt_mage`):
   - `Spells.csv` — the roster (name, class, type, level, question prompt).
   - `spell_effects.csv` — the archetype + formula the C# engine evaluates (damage/heal/buff/…).
   - `SpellLearnCosts.csv` — per-class level + item/gold cost to learn it.
   - `SpellLevels.csv` — real level gate for Type-5 skills (overrides `Spells.csv`).
   - `Morphs.csv` / `Pets.csv` / `Traps.csv` / `SpellMods.csv` — params for morph / pet-summon / trap /
     rage+enchant spells.
   Edit the number, `!reload`. Done.

2. **New behavior (verb/row Lua).** For a spell whose logic isn't just a formula, use the **verb/row** model:
   - Add a row to `SpellParams.csv` keyed by the spell identifier, with a `verb` column naming a Lua verb and
     whatever numeric params that verb reads.
   - Define (or reuse) the verb in `spell_verbs.lua`: `function verbs.myverb(ctx, row) ... end`. The `ctx`
     facade exposes safe primitives (`ctx:spendMana`, `ctx:damage`, `ctx:heal`, `ctx:buff`, `ctx:say`, …) and
     read-only caster stats (`ctx.will`, `ctx.level`, …); `row` is your CSV row (numbers pre-parsed).
   - `!reload`. When a spell has a `SpellParams` row naming a loaded verb, the Lua path runs; otherwise it
     falls through to the C# archetype handler unchanged. **Strictly additive — you can migrate one spell at a
     time, and a broken verb just falls back.**

## Recipe: add or retune an **item effect**

Item use-effects are fully verb/row (the old C# table is gone):
- `Items.csv` — the item roster (name, icon, equip stats, price, durability…).
- `ItemParams.csv` — the use-effect: a `verb` column + params (`amount`, `hpcost`, `statuskey`, `duration`,
  `activemsg`, …).
- `item_verbs.lua` — the verbs (`heal` / `drink` / `ward` / `hardenbody` / `cure` / `warphome`). A verb may
  `return false` to REFUSE the use (a gate, e.g. a ward already active) so the item isn't consumed.
Add a row, pick/author a verb, `!reload`.

## Recipe: add or edit an **NPC**

- **Placement**: `NPCs.csv` — id, identifier, map, tile, look, colour, `Enabled` (0 disables it). Edit +
  `!reload` and the NPC moves/appears/vanishes live.
- **Behavior — reusable services** (shop/bank/repair/parcel/trainer/…): `NpcAbilities.csv` maps an NPC
  identifier to a pipe-list of ability names (`SmithNpc,shop|repair`). The names resolve to C# ability
  singletons via `NpcScripts.AbilityByName` — add a name there to expose a new service to the CSV.
- **Behavior — bespoke dialog** (`npc_dialog.lua`): write the NPC as a coroutine.
  - Click dialog: `function npcs.MyNpc(ctx) ... end`.
  - Spoken trigger: `function npcs_say.MyNpc(ctx, speech) ... return true end` (return `true` to consume the
    speech). `speech` is already lowercased/trimmed.
  - The `ctx` facade yields to the engine for every prompt/action: `ctx:say(...)`, `ctx:menu(prompt, {opts})`
    (returns the 1-based pick), `ctx:input(prompt)` (returns text or nil), plus immediate helpers
    (`ctx:giveItem`, `ctx:awardExp`, `ctx:stage`/`setStage`, `ctx:warp`, `ctx:hasLegend`/`addLegend`, …). To
    add a new primitive, add a `case` in `Server/NpcScript.cs`'s `Dispatch` and a matching yield stub in
    `__make_ctx`.
  - A Lua-scripted NPC takes precedence over its C# ability; anything without a script uses the C# path.

## Recipe: a **monster**, **shop**, **drop table**, **map**, **level curve**

- Monster stats/looks: `mobs.csv`. Spawns: `Spawns.csv` (fixed points) / `AreaSpawns.csv` (per-map/box counts)
  / `AreaSpawnsTrap.csv` (rare trap-ambush bosses).
- Shops: `ShopStock.csv` (flat stock) or `ShopCatalogues.csv` (sub-category menus).
- Drops: `MobDrops.csv`.
- Maps/warps: `map_index.csv`, `Maps.csv`, `Warps.csv`; location/warp geometry in `Inns.csv`,
  `PathHalls.csv`, `GatewayGates.csv`, `WorldMapDests.csv`, `MythicCaves.csv`, `FallRooms.csv`, `Doors.csv`.
- Progression: `LevelExp.csv` (exp curve), `PathGrowth.csv` (per-class HP/MP gain per level).

## Provenance & confidence

Sourced facts carry a `Sources` column referencing `data/game-data/Sources.csv`; `re/build_confidence.py`
scores each datum HIGH/MEDIUM/LOW from the source weights (live observation > era-correct tutor archive > RTK
fallback). Run it after editing sourced data.

---

## What stays in the engine (C#) — do NOT try to move to data

These are *mechanism*, not tunable content:

- **Wire/packet formats and opcodes** — the 4.95 client protocol (`Session.*`, `Protocol/`). Reverse-engineered
  client facts, not game balance.
- **Combat math** (`Server/Combat.cs`) and the **mob AI tick** (`World.Tick`) — hot-path code that runs for
  every mob every 600 ms; a Lua call per mob-step would not survive the load. (Low-rate mob *event* hooks
  could be Lua later; the per-tick baseline stays C#.)
- **Spell/trap trigger effects welded to a mechanic** — e.g. the trap trigger switch in `World`, the family
  classification sets (which spells are "stealth"/"sacrifice"/…). These are *code that reads data*, not data.
- **A few scalars** still in code: `MailMinLevel`, `SpeechRange`, `BankMax`, the door-object graphic table.
  Candidates for a future `ServerTuning.csv`; not yet extracted.

## Build & verify (for engine changes)

```bash
"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" build Server/Server.csproj -clp:ErrorsOnly -v:m -p:UseAppHost=false -o "$TEMP/nexus_buildcheck"
```

Run the offline registry self-test (loads every CSV/Lua and checks the registries) with `Server.dll
--selftest` from a binary **inside the repo tree** (path resolution walks up from the binary to find
`data/`). Data-only edits need no build — just `!reload`.
