# Deferred work — known gaps, deliberately not built yet

Things we found, understood well enough to build, and chose not to build at the time. This is **not** a bug
list and **not** a wishlist: every entry is something whose absence we have already confirmed, with the
source that documents what the real behaviour was, so that picking it up later is implementation rather
than research.

Rules for this file:

* **Only things we actually investigated.** If we never looked, it does not belong here.
* **Cite the source.** RTK path + line, an archive URL, or a walkthrough screenshot. The point of the entry
  is that the next person does not repeat the digging.
* **Say why it was deferred**, so a stale reason is visible as stale. "Out of scope" ages badly on its own.
* **Delete entries when they land.** A done item's record is the code and the commit, not this file.

Related: `docs/Era-Gating.md` (whether a thing should exist *at our date* — a different question from whether
it is built), `docs/Crafting-Values.md`.

---

## Karma

The karma system itself is built (`Server/Karma.cs`, `Tests/KarmaTests.cs`) — fractional score, the named
ladder, `Meets` gates, `AddKarma`/`RemoveKarma`, and RTK's `Tools.checkKarma` scum floor, with Lua bindings.
What is missing is almost everything that *uses* it.

### Nothing removes karma

The only call site in our content is Chu Rua's `addKarma(1)`. Karma is therefore monotonically increasing:
the Rat and Snake bands and the `<= -3` scum floor are unreachable, and `KarmaTooLow()` is wired into the
Chu Rua speech NPCs but can never fire. RTK's drains, none of which are ported:

| Source | Amount | RTK path |
|---|---|---|
| Class trainers, failed oath | -3 (mage/rogue/warrior), -5 (poet) | `NPCs/Common/*_trainer.lua` |
| Dog linguist | -4 | `NPCs/Common/dog_linguist.lua:433` |
| Monk `forgive` | variable penalty | `Spells/Subpaths/Monk/forgive.lua:50` |
| Faerie light | -25 to -30 | `Scripts/faerieLight.lua:19` |
| Fishing (level 15+ branch) | -0.001 | `NPCs/Common/fishnpc.lua:78` |

### Star / Moon / Sun armour chains

RTK's four class trainers each run a three-tier armour questline, and **every numbered step re-checks
karma** before advancing (`"Your soul is too impure to master the stars. Improve your karma and return."`).
None of it is ported — our trainers have no armour chain at all. The gates, from
`NPCs/Common/{mage,poet,rogue,warrior}_trainer.lua`:

| Tier | Warrior | Rogue | Mage | Poet |
|---|---|---|---|---|
| Star (3 steps) | Rabbit (2) | Rabbit (2) | Rabbit (2) | Rabbit (2) |
| Moon (4–5 steps) | Dog (3) | Dog (3) | Ox (6) | Ox (6) |
| Sun (5–8 steps) | Tiger (11) | Bear (8) | Tiger (11) | Bear (8) |

The check sits *inside* each step, after the intro dialog and before the objective is given or completed, so
dropping below the tier mid-chain stalls you where you stand rather than locking the door.

### Three other karma gates

* **Exp Seller, `"kawlana"`** — Spirit (19), part of the wind-armour chain (`ExpSeller.lua:395`). Note it is
  a *hidden* gate: failing it gives `"I really have no idea what you are talking about."`, the same line as
  missing the prerequisite quests. Do not "improve" that into a helpful message; the opacity is the design.
* **Monk `forgive`** — Dragon (14) (`Monk/forgive.lua:21`). The spell that restores someone else's karma
  costs high karma to cast.
* **Faerie light** — Angel (30) (`faerieLight.lua:7`), the top of the ladder, and the biggest sink in the
  game: casting it drops you roughly Angel → Bear.

That last pair is the shape of the system — karma is a currency you spend, not only a badge.

### Karma is not displayed

The 4.95 `0x39` self-profile grammar has no karma field and the client cannot render one (see
`Session.SendSelfProfile`). Feedback is the minitext + sparkle on change, as in RTK. `Karma.LevelName()`
exists for dialog and GM inspection. If we ever surface it, note that RTK's smallest penalty (0.001) is
3,000× too weak to move a real tier yet still flips the displayed name Cat → Rat instantly — a small dead
zone around zero would be worth adding at that point, not before.

---

## Fishing

Only the under-15 branch of `NPCs/Common/fishnpc.lua` is ported (`FishAbility`, the "You're still a
youngin'!" path with a flat 25% catch). The **level 15+** branch is absent entirely: borrowing gear, the
four-pole / four-bait purchase menus (1/10/100/1000 and 0/5/50/500 gold), the 10-base-health minimum, and
the two "laziness" penalty rolls (1-in-10 lose 1 base health; else 1-in-40 lose 0.001 karma).

---

## Chu Rua / By the Sea

The quest is complete and era-accurate as of the tswolf (Jan 2001) and nexusatlas passes. Two known gaps:

* **The tiger stays visible after being scared off.** RTK warps the player to map 1117, a tiger-free copy of
  the Tiger Pass; `TK1117.map` does not exist in `game-data/maps/`, so `Session.TryGinseng` gates on the
  `chu_rua_tiger_gone` flag instead and leaves the player on 1116. Consequence: you pick the root while the
  tiger you just sent south is still standing there, and because the flag is permanent the "too dangerous"
  gate never re-arms on a later visit. Fix needs either the map or per-player mob despawn.
* **The Lost Legend mermaid song** (`chu_rua.lua:121-194`, the `"humm dee do dum do hee"` branch) is not
  ported. It belongs to the wind-armour / Lost Legend chain, not to By the Sea, and gates on
  `quest["wind_armor"]` and `quest["min_song_asked"]`, neither of which exists here.

---

## Level caps on quests

nexusatlas lists By the Sea as "level 15 and lower"; the Jan 2001 tswolf page mentions only the level 3
minimum, and RTK's `chu_rua.lua` has no level check at all. Read as a later-era addition and deliberately
not implemented. If per-quest level caps are ever wanted, they are an era-gated rule, not a Chu Rua
special case — see `docs/Era-Gating.md`.
