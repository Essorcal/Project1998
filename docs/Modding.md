# NexusServer Modding Guide

NexusServer separates the **engine** (C#, in `Server/`) from the **game data** (flat files, in the `data/`
git submodule under `game-data/`). Almost everything a modder tunes — spells, items, NPCs, monsters,
shops, maps, drop tables, level curves — lives in CSV or Lua and **hot-reloads with the `@reload` GM command,
no server restart**. This document is the map of what lives where and how to change it.

> Golden rule: **data describes *what*; code implements *how*.** A monster's HP is data; the combat formula
> is code. A spell's mana cost is data; the packet that draws its animation is code. If you're editing a
> number or a line of dialog, it should be a file edit + `@reload`, not a rebuild.

---

## The three tiers

| Tier | Lives in | Changed by | Examples |
|---|---|---|---|
| **Flat config (CSV/Lua)** | `game-data/*.csv`, `*.lua` | edit file → `@reload` | mob stats, items, spells, NPCs, shops, drops, warps, level curves |
| **Live GM/player state** | SQLite (`*.db`) | in-game actions / GM commands | inventories, mail, marriages, quest progress |
| **Engine constants** | `Server/*.cs` | edit code → rebuild | wire/packet formats, combat math, the mob AI tick, a handful of scalars |

Everything below is Tier 1 unless noted.

## Hot reload

`@reload` (GM command) re-reads **every** `game-data` file — all CSVs and the three Lua scripts — and
rebuilds the world population (added/removed/repositioned spawns and NPCs take effect immediately). A load
error keeps the previous content and reports the error, so a typo can't take the server down. There are no
compile-time content tables left that would need a restart.

---

## Recipe: add or retune a **spell**

Two layers, use whichever fits:

1. **Data-only retune (most spells).** A spell's numbers live in CSVs joined by the spell **identifier**
   (`SplIdentifier`, e.g. `bolt_mage`):
   - `Spells.csv` — the roster (name, class, type, level, question prompt).
   - `spell_effects.csv` — the archetype + formula the C# engine evaluates. Archetypes: `Damage`, `Heal`,
     `Buff` (self), `TargetBuff` (a beneficial `might`/`armor` buff cast on a target — another player, self, or a
     mob/NPC such as your pet; `buffStat`+`buffAmt`+`durationMs` columns), `Debuff`, `Cure`, `ManaBattery`,
     `Summon`, `Teleport`, `Utility`.
   - `SpellLearnCosts.csv` — per-class level + item/gold cost to learn it.
   - `SpellLevels.csv` — real level gate for Type-5 skills (overrides `Spells.csv`).
   - `Morphs.csv` / `Pets.csv` / `Traps.csv` / `SpellMods.csv` — params for morph / pet-summon / trap /
     rage+enchant spells.
   Edit the number, `@reload`. Done.

   **Archetype verbs (`arch_*`).** A whole archetype's *behaviour* is also scriptable: `spell_verbs.lua` may
   define `arch_damage` (and, as they're migrated, `arch_heal`/`arch_buff`/…). When present, every spell of
   that archetype runs the verb instead of the C# handler — the engine pre-evaluates the spell's formula and
   mana cost and hands them in as `ctx.amount` / `ctx.mana`, so the verb stays pure logic
   (`return ctx:magicDamage(ctx.amount, ctx.mana)`). Delete or rename the verb and the archetype falls straight
   back to its built-in C# handler, so this is safe to experiment with live. A per-spell `SpellParams` row
   (below) still wins over the archetype verb for that one spell.

2. **New behavior (verb/row Lua).** For a spell whose logic isn't just a formula, use the **verb/row** model:
   - Add a row to `SpellParams.csv` keyed by the spell identifier, with a `verb` column naming a Lua verb and
     whatever numeric params that verb reads.
   - Define (or reuse) the verb in `spell_verbs.lua`: `function verbs.myverb(ctx, row) ... end`. The `ctx`
     facade exposes safe primitives (`ctx:spendMana`, `ctx:damage`, `ctx:heal`, `ctx:buff`, `ctx:say`, …) and
     read-only caster stats (`ctx.will`, `ctx.level`, …); `row` is your CSV row (numbers pre-parsed).
   - `@reload`. When a spell has a `SpellParams` row naming a loaded verb, the Lua path runs; otherwise it
     falls through to the C# archetype handler unchanged. **Strictly additive — you can migrate one spell at a
     time, and a broken verb just falls back.**

   **Composed / multi-effect spells.** A spell that is several things at once (a multiplier *and* a
   damage-reduction *and* a stance, possibly with per-caster state) is **not** a new archetype — it's one verb
   that composes primitives. The reference example is Baekho's Cunning (`verbs.baekhos_cunning`): a tier-1→6
   state machine built from `ctx:rage(mult, ms)` (whole-swing multiplier), `ctx:deduction(mult, ms)`
   (incoming-damage reduction), `ctx:stance("backstab"/"flank", on, ms)`, plus state via `ctx:reg/setReg`
   (transient per-caster ints), `ctx:hasDuration/setDuration`, and `ctx:onCooldown/setCooldown`. Rule of thumb:
   single effect → ride the archetype default; many effects → **compose primitives in one verb**. Never invent
   combined archetypes (`DamageBuff`, `StanceControl`, …) — that path explodes combinatorially.

## Recipe: add or retune an **item effect**

Item use-effects are fully verb/row (the old C# table is gone):
- `Items.csv` — the item roster (name, icon, equip stats, price, durability…).
- `ItemParams.csv` — the use-effect: a `verb` column + params (`amount`, `hpcost`, `statuskey`, `duration`,
  `activemsg`, …).
- `item_verbs.lua` — the verbs (`heal` / `drink` / `ward` / `hardenbody` / `cure` / `warphome`). A verb may
  `return false` to REFUSE the use (a gate, e.g. a ward already active) so the item isn't consumed.
Add a row, pick/author a verb, `@reload`.

## Recipe: add or edit an **NPC**

- **Placement**: `NPCs.csv` — id, identifier, map, tile, look, colour, `Enabled` (0 disables it). Edit +
  `@reload` and the NPC moves/appears/vanishes live.
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
- **Prey creatures**: `MobFlees.csv` (`Identifier,Flees`). A listed mob never attacks and never holds a target;
  it backs away from any player within 2 tiles at **double** its normal move rate, and for 4 s after being
  *swung at* (hit **or** miss, or damaged by anything) it keeps running and notices you from 4 tiles.
  **Ceiling:** the world steps a mob at most once per 600 ms tick, so a creature whose `MobMoveTime` is already
  ≤1200 ms — the blue rooster is 500 — is *already* at max speed and its flee shows as direction, not pace.
  Everything else about it — HP, drops, exp — is unchanged, so it's
  still killable, just harder to corner. Nothing in RTK marks these creatures (its engine only has
  `MobBehavior` 0/1/2+, and `mob_ai_basic.lua` gives a rabbit a wolf's chase-and-swing routine), so **which**
  mobs flee is ours to pick; the movement itself is ported from the one `RunAway()` in the RTK tree
  (`Mobs/mob.lua`, used by `Instances/mysterious_merchant.lua`). Ships with `rabbit` and `blue_rooster`; add a
  row + `@reload` for any other critter. Kept out of `mobs.csv` so re-running the mob extractor can't drop it.
- Shops: `ShopStock.csv` (flat stock) or `ShopCatalogues.csv` (sub-category menus).
- Drops: `MobDrops.csv`.
- Maps/warps: `map_index.csv`, `Maps.csv`, `Warps.csv`; location/warp geometry in `Inns.csv`,
  `PathHalls.csv`, `GatewayGates.csv`, `WorldMapDests.csv`, `MythicCaves.csv`, `FallRooms.csv`,
  `ArenaDoors.csv`, `Doors.csv` (lock/key), `DoorObjects.csv` (the 'o'-key open/close graphic swap: `map` rows
  are exact faced-object swaps, `delta` rows are `[lo,hi]` ranges whose open/closed ids differ by a fixed delta).
  `defaultOpen=1` on a `map` row means that faced id is a **closed** door that should start **open** — the swap
  is applied per cell as the `.map` is read, so a multi-tile door needs the flag on every piece of the run
  (the city gates, `5-8` closed ↔ `15-18` open, are the ones that need it). A door is only a wall if its
  object id is flagged solid in `SObj.tbl` — check there before assuming which id is the open one.
- **Level-banded PvP arena doors**: `ArenaDoors.csv`
  (`Map,Tiles,DestMap,DestX,DestY,MinLevel,MaxLevel,MaxVita,MaxMana,Unmarked,Label`). These are *scripted
  tiles*, not warps — in RTK they live in `onScriptedTilesArena.lua`, and only each arena's way **back** is in
  `Warps.csv`, so a door with no row here is simply dead. `Tiles` is a `;`-list of `x:y` (a door is normally
  the 2 tiles its sprite covers); `DestX` may be a `lo-hi` band that the landing column is rolled from.
  `MaxLevel`/`MaxVita`/`MaxMana` of `0` mean *no cap*, and the vital caps are **OR**'d (over either one keeps
  you out) — unlike the engine-level `Maps.csv` requirement columns, which AND them. Under-levelled gets
  *"Nightmarish visions of your own death repel you."*; over-qualified gets *"Your honor forbids you from
  entering."* Don't add the PvP entry warning here — every arena map is `MapPvP=1`, so `EnterMap` sends it.
- Progression: `LevelExp.csv` (exp curve), `PathGrowth.csv` (per-class HP/MP gain per level).
- Background music: `MusicTracks.csv` (`Track,Name,Type` — the id↔song-name table the `@music <name>` command
  reads; the stock client has 12 midis and 11 of them are named) and `MapBgm.csv` (`Zone,Track,Maps,Names` —
  `Maps` a `;`-list of ids and `lo-hi` ranges, `Names` a `;`-list of map-name globs like `Buya *`).
  Resolution order for a map is **explicit id/range → name glob → warp-graph spill → nothing**:
  - a **zone** is a row that casts a wide net — `Buya,tiger,330,Buya *`;
  - a **single-map override** is just a row with one id and no globs — `Kugnae Donjon,sorrow,24,` — and
    because ids beat globs it wins over any zone that would otherwise claim that map by name, wherever it
    sits in the file. `Track,0` on such a row means *silence here*;
  - every **unlisted** map then inherits its *nearest* listed map's track through `Warps.csv` (multi-source
    BFS at load). So Buya's shops, taverns and caves play Tiger without being listed, and the boundary with
    Kugnae falls wherever the two areas actually meet. `@music` reports the hop count (`Buya +2`).
  - a map with no warp path to any zone at all (arenas, gateways, scripted-tile dungeons — ~850 of 1750, all
    of them warp-less in `Warps.csv`) keeps whatever is already playing, and falls to the `Default` row only
    if you *log in* there with nothing playing yet.

  Because assignment is by position rather than by history, the music is the same whichever way you arrive —
  walking in, warping in, or logging in.
- Server scalars: `ServerTuning.csv` (`key,value`) — `MailMinLevel`, `SpeechRange` (NPC hearing radius),
  `BankMax` (coin cap), `SplitTrapSpells` (0/1, default 0 — the 8 individual `set_X_trap` rogue spells are a
  2003-07-01 addition, out of era for 4.95, so only the `Set Trap` typed prompt is learnable/castable; set 1
  to restore them). A missing key falls back to its historical default, so the file is optional.

## Provenance & confidence

Sourced facts carry a `Sources` column referencing `game-data/Sources.csv`; `re/build_confidence.py`
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
- **The family classification sets** (which spells are "stealth"/"sacrifice"/…) and the align-fx ladder are
  code that *reads* data, not tunable content.

## Build & verify (for engine changes)

```bash
"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" build Server/Server.csproj -clp:ErrorsOnly -v:m -p:UseAppHost=false -o "$TEMP/nexus_buildcheck"
```

Run the offline registry self-test (loads every CSV/Lua and checks the registries) with `Server.dll
--selftest` from a binary **inside the repo tree** (path resolution walks up from the binary to find
`data/`). Data-only edits need no build — just `@reload`.
