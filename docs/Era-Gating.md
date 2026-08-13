# Era gating — targeting a point in NexusTK's history

NexusTK ran for years against a slowly-changing client. "Which client do we target" therefore does not
answer "what content should exist": client 4.95 shipped 2001-07-09, but a player logging in the week it
shipped met a different tutorial than one logging in eleven months earlier. Quests were added, moved
between givers, and occasionally taken away.

So the server carries a **target date**, and dated content declares the window it existed in.

## Configuring it

| Where | What |
|---|---|
| `game-data/ServerTuning.csv` | `EraDate,20010709` — the date the world is pretending it is, as `yyyymmdd`. `0` switches gating off entirely. |
| `game-data/EraFeatures.csv` | One row per dated feature: `Feature,Introduced,Retired,Source,Notes`. |
| `Server/Era.cs` | `Era.Has("feature")`, plus the feature-key constants our code actually asks about. |
| `@era` | Read-only GM readout of the date and what it includes. |

Both files hot-reload with `@reload`; no restart.

The default `20010709` is the day client 4.95 shipped — the build this server targets — so out of the box
everything documented below is switched **on**. Move the date back to play an earlier world.

## The two rules that matter

**Sparse and fail-open.** A feature with no row is always present, and a missing or malformed file gates
nothing. Only add a row you can point at a source for. Absence of evidence must never remove content —
the failure direction has to be "a player sees something they shouldn't", not "content silently vanishes".

**`Retired` is exclusive.** `Retired=2000-10-06` means the feature is gone *on* that day. That's the
natural reading when one thing replaces another, because the replacement's `Introduced` is the same date
and exactly one of the pair must be live.

## The three tutorial eras

The tutor (Jadespear #49 / Ironheart #20) is present in **all three**. What changes is how much he still
has to teach — content moved *into* the newbie area, and later new quests were appended to his chain.

| | before 2000-10-06 | 2000-10-06 → 2001-03-17 | 2001-03-18 → |
|---|---|---|---|
| Newbie area (4711-4718) | — | yes | yes |
| Tutor teaches saber/rabbits/squirrels/Soothe | yes | — (moved to the area) | — |
| Novice sword comes from | tutor, stage 8 | Woodland Guard's law quiz | Woodland Guard's law quiz |
| Rest of the tutor chain | yes | yes | yes |
| Du Mountain (stage 11) + student cap (stage 13) | — | — | yes |

The seam is explicit in the area's own last line — the Woodland Angel closes with *"I now leave you in
the hands of the tutor from the town you have picked as home"* and warps you to him. The area never
replaced the tutor; it fed him.

The novice sword is the one reward that crosses the seam without belonging to the moved chain: it hangs
off the tutor's **stage 8** (the deer hunt, the first task that needs a real weapon), not off
`NoviceQuest`, so `tutor_novice_chain` does not cover it. It is gated separately, on
`newbie_tutorial_area` — the era that has the area is the era where the Woodland Guard hands the same
sword out for passing his law quiz, and the tutor must not hand out a second one.

### What is *not* gated

Du Mountain the place, and Haguru standing on it, are **older than the quest** — the release post calls
him "the old guy named Haguru that was stranded on the mountain in early 4.0". With
`tutor_du_mountain_quest` off he is still there and still answers; he just has no wolves to send you
after. Same principle everywhere: gate the quest, keep the world.

## Sources

- `tswolf-2001-03-18` — TSWolf/NexNet "Du Mountain opened", dating both 2001 quests to the day.
- `tswolf-newbie-guide` — the three-page newbie quest guide the area's dialog is transcribed from.

Both are in `game-data/Sources.csv` with the exact quotes and the surviving/missing screenshot list.

## Adding a gate

1. Add a row to `EraFeatures.csv` with a `Source`.
2. Add a `const` to `Server/Era.cs` (bare strings at call sites mean a typo becomes a silently always-on
   gate — the fail-open default makes a misspelling invisible at runtime) and list it in `KnownFeatures`
   so `@era` reports it.
3. Call `Era.Has(...)` at the point that decides. From Lua, `ctx:eraHas("feature")`.

Gate the *behaviour*, not the placement: a gated quest usually still has its NPC standing there, so the
script is what needs to know, not `NPCs.csv`.

## New-character placement

`CharacterFactory.StartFor` picks the spawn from the calendar: **Welcome (4711) at (3,5)** once
`newbie_tutorial_area` is live, the nation's tutor home before that. `HomeCityFor` is deliberately left
alone — it is also the Silver Thread revive point, and a defeated veteran must not wake up in the newbie
area.

This is the reason `EraCalendar` lives in `Shared` rather than under `Server`. Placement runs in the
**login server**, a separate process that never loads world content; if it couldn't read the calendar it
would place every character by the pre-2000 rule regardless of what the game server believed, and nothing
would report the disagreement. The calendar is read lazily so the login server still starts instantly.

> **The login server caches the calendar for its process lifetime.** It has no `@reload`. After changing
> `EraDate`, restart it — otherwise it keeps placing characters by the old date.

Welcome's way onward is the four-tile doorway along its bottom edge, `(8..11, 15)` → Open Field (4712)
at `(8..11, 2)`, so the player keeps walking south into the next room.

> **Historical note.** Welcome originally shipped with its only exits at `(9,16)` and `(10,16)` on a
> **16×16** map — rows 0–15 — so both source tiles were out of bounds and the room was a sealed box.
> (Two of those three rows even share a source tile with conflicting destinations.) They are left in
> `Warps.csv`: they are unreachable, and they route `4711 → 4714`, which is the *later* room order, so
> they may be wanted by a future post-4.x era config. `ContentSmokeTests.NewbieAreaFirstDoorwayIsReachable`
> now asserts the real doorway so this cannot regress silently.

### Still needed for the area to be traversable end to end

| From | To | Why |
|---|---|---|
| `4712 → 4713` | Forest Path | after the rabbits |
| `4713 → 4714` | Deep Forest | after the squirrels |
| `4717 → 4718` | Angel's Blessing | the "door north" after the quiz; only the reverse exists |

`4711 → 4712`, `4714 → 4715`, `4715 ↔ 4716` and `4715 ↔ 4717` exist. The final exit to the city needs no
warp — the Woodland Angel warps the player home in script when they say "Finish".

Until those three land, a new character can reach Open Field and no further. Running at
**`EraDate=20001005`** (the day before the area) keeps new characters at the tutor instead, with him
teaching the first-steps beats himself, at the cost of the two 2001-03-18 quests.

### Dying inside the area

The area is sealed, and that has to hold for death too. Every Shaman is out in the world, so taking
Silver Thread passage would eject a player from the chain permanently — there is no warp back in — and
the passage never checked whether you were even dead, so a *living* player could leave the same way.

- **Silver Thread is refused on any tutorial map** and answers with what the ability is for instead.
- **While dead, clicking ANY NPC in the area** runs the Shaman's own revival dialog. Deliberately every
  NPC: a beginner shouldn't have to work out which one to click.

Both key off `Content.IsTutorialMap` — a *where you physically are* test, not an era or quest-progress
one. That's on purpose: it holds for a character who was GM-warped in, who abandoned the chain half way,
or whose progress flag disagrees with the map. The ghost branch reuses `ReviveAbility.Resurrect` rather
than copying its strings, so the wording can only drift in one place.

### Mob spawns

Welcome carries **rabbits only** — it is the practice room before the first quest, and the armorer's
squirrels are not introduced until Forest Path. Open Field is rabbits (quest 1), Forest Path squirrels
(quest 2); Deep Forest and Country Farm carry both, which is what lets a player collect the 5 acorns and
5 rabbit meats Mignok asks for.
