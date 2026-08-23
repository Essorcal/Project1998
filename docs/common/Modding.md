# Project1998 Modding Guide

Project1998 separates the **engine** (C#, in `Server/`) from the **game data** (flat files, in
`game-data/`). Almost everything a modder tunes — spells, items, NPCs, monsters,
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

   **`Buff` and `TargetBuff` are one verb.** `arch_buff` and `arch_targetbuff` both call a single
   `apply_buff(ctx, mode, fallbackDur)`; the only differences are `mode` (`"self"` vs `"target"` — which
   `ctx:buffTarget` resolves, the same way `verbs.ward` resolves its self-cast and ally-cast halves) and the
   fallback duration. Add behaviour there, not to one of the two names, or the halves drift.

   **Buff exclusivity slots.** A `Buff`/`TargetBuff` spell can belong to an RTK `checkIfCast` slot
   (`spellTables.lua`: `mights`, `blessings`, `potency`, `shadowFigures`, …). While a slot is running, every
   spell in it is **refused**, not refreshed — so Might can't be spammed and Might + Spirit Strength can't be
   stacked. The slot comes from `spell_verbs.lua`'s `BUFF_CATEGORY` table (edit + `@reload`), falling back to
   `spell_effects.csv`'s `cureCat` column. A buff named in neither keeps the old refresh behaviour.

   **City-locked secrets.** A few spells are taught by exactly one kingdom's trainer — the rogue Remedies
   (Maro's = Kugnae, Maso's = Buya, Dagger's = Nagnang). That list is `Content.CityLockedSpells`, keyed by
   `BaseKey` so one row covers all four alignment reskins, mapped to a `Maps.csv` `MapRegion`. Other trainers
   drop the spell from Learn Secret / Divine Secret and point the player at the right city by name.

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

## Recipe: retune the **armor quests**

`ArmorQuests.csv` holds the level and karma gate for each of the twelve Star/Moon/Sun chains
(`Path,Tier,Level,Karma`). Edit + `@reload`; no rebuild. The karma column is where the period sources
disagree — the file's own header says which rows are contested and why, and
[Armor-Quests.md](Armor-Quests.md) has the full walkthrough. The steps themselves are code
(`Server/ArmorQuest.cs`), because several of them gate on systems rather than counts.

## Recipe: a **monster**, **shop**, **drop table**, **map**, **level curve**

- Monster stats/looks: `mobs.csv`. Spawns: `Spawns.csv` (fixed points) / `AreaSpawns.csv` (per-map/box counts)
  / `AreaSpawnsTrap.csv` (rare trap-ambush bosses).
- **Hidden ambush tiles**: `AmbushConfig.csv` (per map: how many tiles, the `MobCap` that governs refills, the
  message, and which burst fires) + `AmbushBursts.csv` (what each burst is made of). Step on one and the burst
  spawns *around* you — slots 0-3 land east/west/north/south, a 5th and beyond on your own tile — then the
  sprung tile relocates somewhere else on the map, but only while live mobs are under `MobCap`. Warriors'
  Watchful Eye reveals them. The five mythic trap-caves are RTK's (`NPCs/trap/mob_spawn.lua`); Buya town (330)
  carries a rat nest reconstructed from a live 7.x sighting, which is ours. **Add burst tables in
  `re/extract_ambush_tables.py`, not by hand** — that script rewrites `AmbushBursts.csv` wholesale, so a
  hand-added row is gone on the next run. `AmbushConfig.csv` *is* hand-authored, and both hot-reload.
- **Prey creatures**: `MobFlees.csv` (`Identifier,Flees`). A listed mob never attacks and never holds a target;
  it backs away from any player within 2 tiles at **double** its normal move rate, and for 4 s after being
  *swung at* (hit **or** miss, or damaged by anything) it keeps running and notices you from 4 tiles.
- An `AreaSpawns.csv` **count is a cap and a guarantee**: the refill tops up to it and never past it, and if
  the random tile rolls come up short it enumerates the box rather than giving up (`World.FillMember` →
  `World.OpenTiles`). It only places fewer when the box genuinely has no free walkable non-warp tile left.
  This matters most for the `1`s — a lone boss used to be decided by four rolls over the whole map, which is
  how Sute went missing from ~7% of visits to his own nest.
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
  `PathHalls.csv`, `GatewayGates.csv`, `WorldMapDests.csv`, `MythicCaves.csv`, `EventCaves.csv`,
  `EventCaveTiers.csv`, `FallRooms.csv`,
  `ArenaDoors.csv`, `Doors.csv` (lock/key), `DoorObjects.csv` (the 'o'-key open/close graphic swap: `map` rows
  are exact faced-object swaps, `delta` rows are `[lo,hi]` ranges whose open/closed ids differ by a fixed delta).
  `defaultOpen=1` on a `map` row means that faced id is a **closed** door that should start **open** — the swap
  is applied per cell as the `.map` is read, so a multi-tile door needs the flag on every piece of the run
  (the city gates, `5-8` closed ↔ `15-18` open, are the ones that need it). A door is only a wall if its
  object id is flagged solid in `SObj.tbl` — check there before assuming which id is the open one.
- **No-casting maps**: `Maps.csv`'s `MapSpells` column (`0` = *"That doesn't work here."*, the whole `0x0F`
  cast opcode is refused; GMs bypass it, as in RTK). This is what keeps magic out of the towns' interiors —
  taverns, kan shops, the three Gathering halls, the class trainers' buildings. **Not** `MapIndoor`, which is
  set on every cave and dungeon too. Edit the row and `@reload`. RTK's dump is inconsistent here — Nagnang's
  trainer buildings and the later-era set block casting while Kugnae's and Buya's (the same rooms one era
  earlier) don't — so the 40 Kugnae/Buya path-hall, sanctum and alignment-room rows are corrected in place.
- **Tiered "event cave" doorways**: `EventCaves.csv` (one row per entrance) + `EventCaveTiers.csv` (the
  ladder they all share). A doorway into a dungeon that exists as **five parallel copies**, one per depth,
  where the copy you get is read off your level and subpath rank. Scripted tiles, not warps — the Buya
  Library Caverns doorway used to be two `Warps.csv` rows straight into tier 1, which is why the four deeper
  tiers were reachable only by `@warp`. **If you add an entrance here, delete its `Warps.csv` rows**: the warp
  branch in `HandleWalk` runs first and would take the step before the scripted tile ever saw it.
  - `EventCaveTiers.csv` (`Tier,AltTier,MinLevel,MaxLevel,MinMark,MaxMark,Label`) is matched **in file
    order**, first hit wins, so the file's order is the semantics — don't sort it. Bands must be disjoint.
    `AltTier > 0` makes the band a **split**: both depths are open and the player is asked which tunnel to
    take. No band matching at all is the **refusal** case (that's level 1-14) — they still get the entry
    dialog, then the row's `DenyMsg` in the status box and a step back off the threshold.
  - `EventCaves.csv` carries the geometry and every line of text: `EntranceTiles` a `;`-list of `x:y`,
    `TierMaps` a `|`-list of map ids shallowest-first, `Pages` a `|`-list of dialog pages, then
    `Prompt`/`OptionNear`/`OptionFar` for the split menu. A tier deeper than `TierMaps` clamps to the
    deepest map listed, so growing the ladder can never warp anyone to map 0.
  - The level bands are the archive chart's (`tutor-caves-azncloudboi-event`) verbatim; the top two bands
    are **ours**, because that chart gates caves 4/5 on 10-14 million vita — a 2005+ number that is
    unreachable at `EraDate 20010709`. Sam san (mark 3) is the top cave, Il/Ee san the split below it.
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
- Background music: `MusicTracks.csv` (`Track,Name,Kind,Set` — the id↔song-name table the `@music <name>`
  command reads) and `MapBgm.csv` (`Zone,Track,Track5x,Maps,Names` — `Maps` a `;`-list of ids and `lo-hi`
  ranges, `Names` a `;`-list of map-name globs like `Buya *`).
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

  **Two soundtracks.** `Set` splits `MusicTracks.csv` into `old` (the 12 stock midis, ids 1-12, both clients
  ship them) and `new` (the 25 mp3s and 52 playlists in the 5.x client's `Mus000.dat`). They are separate id
  spaces — mp3 2 and midi 2 are different songs — so `Kind` says which file the client opens: `midi`, `mp3`,
  `list` (an ordered ten-track playlist) or `shuffle` (the same ten from a random start). `Track` on a
  `MapBgm.csv` row is the old pick and `Track5x` the new one, and **`Track5x` must name a `list`** — a single
  mp3 never advances off its one song, and a `shuffle` (the `-rand` names) stalls dead the first time the
  client's own advance re-rolls onto the entry already playing, roughly 1 track in 10. Both failures are
  silent, so the selftest and `ContentSmokeTests` assert it. Players choose with
  `@music old` / `@music new`, remembered per character. The new set is **5.x only** — the 4.95 client has the
  mp3 engine but none of the files, so `@music new` is refused there rather than accepted and silent.
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
