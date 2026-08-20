# game-data — the content registry

Everything the game *is*, as opposed to how it works: 66 CSVs, 4 Lua scripts, 1,840 `.map` terrain files
and `SObj.tbl`. Read-only to a running server, hot-reloadable with the `@reload` GM command, and replaced
wholesale by a deploy.

**Editing a file here does not need a rebuild.** See [`../docs/common/Modding.md`](../docs/common/Modding.md)
for the full map of which file holds what, and
[`../docs/common/Architecture.md`](../docs/common/Architecture.md) §3 for why content, live state and code
live in three directories that cannot see each other.

> This was its own repository (`Project1998-data`, a submodule) until 2026-08. It is a plain directory now:
> a change to a CSV and a change to the `Content.cs` loader that parses it are one change, and as two repos
> they could never be one commit.

## Where it came from

Most of it is distilled from **[github.com/unkmc/RTK-Server](https://github.com/unkmc/RTK-Server)** — a
Mithia/7.x NexusTK server whose production MySQL dump (`database/2020-09-02-21-55-01_RTK.sql.bak`, 2 MB,
54 tables) is the best available source of monster/item/map/NPC **names + stats + placement**. Names and
stats are *not* in the 4.95 client (see [`../docs/4.x/Protocol.md`](../docs/4.x/Protocol.md) §11a — the
client-data audit), so this is the canonical content source.

The RTK creature-spawn packet is **byte-identical to our 4.95 `0x07`** (`look = 0x8000|monsterId`, then a
`look_color` palette byte), which independently confirms our reverse-engineered recolor model (§11a.1).

**`Sources.csv` is the provenance registry.** Every source carries a `Tier` and a `Weight`, and content
rows cite a `SourceId`; when two disagree, the higher weight wins and the loser goes in the notes. RTK is
**weight 0** — the most-used source here and the least authoritative. Read
[`../docs/research/README.md`](../docs/research/README.md) before adding a value.

## Files

Regenerable via the `re/*.py` extractors — `re/rtk_extract.py`, `re/build_map_index.py`,
`re/extract_mob_drops.py`, `re/extract_shops.py`, `re/extract_lua_spawns.py`,
`re/extract_minor_quests.py`, `re/extract_spell_formulas.py` — each of which writes here. The C# loader
in `Server/Content.cs` names the script that generated each table in its doc comment.

| File | Rows | Key columns |
|---|---|---|
| `mobs.csv` | 716 | `MobLook`, `MobLookColor`, `Vita`(HP), `Exp`, `Level`, might/grace/will, min/max dmg |
| `Maps.csv` | 9850 | `MapId` (↔ our `0x15` mapId & `Maps\TK<MapId>.map`), `MapName`, BGM, indoor, light, PvP, warpout |
| `Warps.csv` | 4476 | `SourceMapId/X/Y → DestinationMapId/X/Y` (portals) |
| `Spawns0.csv` | 1175 | `SpnMobId`, `SpnMapId`, `SpnX/Y` (where mobs spawn) |
| `NPCs0.csv` | 385 | `NpcDescription`, map/x/y, `NpcLook`, `NpcLookColor` |
| `Items.csv` | 2545 | `ItmDescription`, `ItmType`, `ItmLook`, damage/armor/stats, buy/sell price |
| `Spells.csv` | 906 | spell/skill definitions |
| `Paths.csv` | 23 | class names + rank titles |

## Version caveats (RTK is 7.x, our client is 4.95)

- **Look-ids 0–326 overlap** and are validated against our EPF shape-matching (rat=91, mouse=120, bull=27,
  rabbit=21, fox=22, wolf=23, bear=24, squirrel=25).
- **Maps:** 1387 of RTK's `MapId`s have a matching `TK<N>.map` in our client; the rest are 7.x-added.
- **Colours ≤19** map to our 20 `Monster.pal` blocks; **>19 are 7.x-only** and must be re-picked via `!crecol`.
- **Item look/icon ids** reference 7.x `Item.epf` — names reliable, sprite ids need checking.
- **Stats** are 7.x-balanced (structurally correct, numerically a design choice).

The `Description` field is the display name; `Identifier` is the internal snake_case key.
