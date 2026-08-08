# Crafting values — archive-validated corrections (pre-port)

Cross-reference of RTK Lua crafting values (`RTK-Server/rtklua/Accepted/crafting.lua`
+ `Crafting/*.lua`) against the scraped archive. **Decision: trust the archive for
everything.** There is no `Crafting.cs` yet — this file is the correction list to apply
when crafting is ported.

Full 22-post verification pass against the raw tutor-board HTML (not just the
aggregated `crafting.md`) done 2026-07-28: zero contradictions found in any claim below;
the gaps that pass surfaced are folded in throughout this file (asymmetric
Refining/Manufacturing exclusivity, gathering level gates, Chef's food-prep gate,
launch-scope cuts).

**Era-gating infra is built** (`Server/CraftingToggles.cs`, wired through `Content.cs`'s
existing flat-file registry pattern — same as maps/mobs/items/etc., NOT SQLite): the
config lives in `data/game-data/CraftingToggles.csv` (`Skill,Enabled` columns, env
override `NEXUS_CRAFTING_TOGGLES`) and is picked up by the existing `@reload` GM
command, no restart required. Only skills actually listed in the file override the
code-level default in `CraftingToggles.DefaultDisabled`; anything absent falls through
to that default. Jewelry, Food Preparation, and Chef default OFF (see "Launch scope"
below for why); everything else defaults ON. `@craft` is a read-only status command
(shows on/off per skill) — the toggle itself is config, not live GM state, so there's no
`@craft on/off` mutation command; edit the CSV and `@reload` instead. Farming has no
toggle entry at all — it's excluded from scope entirely, not gated. This infra is ready
now even though the actual skill mechanics (`Crafting.cs`) aren't ported yet —
`CraftingToggles.IsEnabled(skill)` is the check to call once they are.

## Source priority

tswolf / nexusatlas carry only passing mentions of crafting (news/forum chatter, no
charts) — but see "Launch scope" below, where dated `tswolf`/`all_news.md` news posts
were the deciding evidence for two scope cuts. For skill values and recipes, the only
archive source with real crafting numbers is the **boards tutor posts** — Head Tutor
Nussan's skill charts (`scraped_nexus_data/artifacts/game_data/boards_tutors/by_category/crafting.md`).
So the boards are authoritative there by default.

## Launch scope — gate Jewelry + Food Prep/Chef behind a toggle, cut Farming entirely

Era research turned up three different pictures, so three different treatments:

- **Jewelry**: real client history, but era-boundary sits mid-skill. News evidence
  (`all_news.md`, sourced from `mainpage-archive-12-2004.php`) dates its introduction to
  **2004-12-09** ("Jewelry crafting" by Conro — explicitly framed as a brand-new
  ability, donate 1000c to the Wilderness Gemmer's "Jeweler Devotion" tab). It was live
  for ~10 months in the **5.x** client before the **6.5** client shipped 2005-10-11 — so
  it predates the 6.x boundary that got Farming cut (below), just not the 4.83/4.95
  builds this project centers on. The other three Manufacturing skills
  (Tailoring/Carpentry/Smithing) are attested far earlier (2000-08-26/2000-10-24,
  `tswolf/arc7-2000.html`/`arc9-2000.html`). **Decision: implement it, gated off by
  default** via `data/game-data/CraftingToggles.csv` (`Server/CraftingToggles.cs`) rather
  than cutting it — keeps the values ready without committing to an era claim the
  research doesn't cleanly support either way.
- **Food Preparation / Chef**: dated 2001-01-13/2001-01-31 — technically in-era for 4.95
  (ships 5 months later) but after 4.83. Same treatment: **implemented, gated off by
  default**, a GM opts in per-target-era by flipping the row in
  `CraftingToggles.csv` and running `@reload`. See the dedicated section below for the
  full dating.
- **Farming**: excluded from scope entirely (no toggle) — it IS a real, documented mechanic
  (Mage tutor Nussan's dedicated `Nussan 04260155.html` "Farming info" post; RTK has a real coded
  `"farming"` skillPointLevels curve, `crafting.lua:276-290`), so it's not an RTK-only
  invention, but it's confirmed **late/6.x-era**: dated news (`mainpage-archive-11-2007.php`,
  Vini, "Farming skill is here & Other Reset changes", 2007-11-07 7:33 PM EST) puts its
  launch in November 2007 — years after both the 4.83 (2000-10-12) and 4.95
  (2001-06-29) client builds this project targets. Correctly out of scope for this
  server, not just deprioritized.
  - Mechanics for reference if this ever gets revisited for a later-era server: buy a
    `basic_sickle` (200c) from Ssal (Kugnae 111,137 or Nagnang Woodlands field), wield it,
    walk the crop field; hidden crop-mobs (`very_tall_wheat`/`tall_wheat`/`damp_wheat`/
    `bent_grain`) trigger a 4-option flavor-text prompt (swing high/right/left/low
    matching the text) — a correct swing damages the crop (65→235 dmg scaling
    Novice→Legendary), killing it is the success event (+1 skill, success-only). Yields
    raw Wheat/Rice/Peas (Wheat variants mostly drop Wheat + bonus Rice/Peas; bent_grain
    drops Rice + bonus Wheat/Peas). Wheat mills into Flour (Food Prep) →
    Flour+Salt Block+Water Jug = noodles (Chef); Rice/Peas have no further recipe use in
    RTK's Lua. Skill curve: cumulative points 0/56/430/1291/3760/8620/14008/22654/33424/
    46794/70190 (Novice→Legendary).

## Food Preparation / Chef — implement, gated off by default, historical note

Dated news evidence (`tswolf/arc0-2001.html`) pins Food Preparation's release to
**2001-01-13** (Biospark, "Yummy.. Roasted Beef": *"a new addition to Nexus is the skill
'Food Preparation'"*) and Chef to **2001-01-31** (SSaturn, "Only for Advanced Cooks..").
That's ~3 months after the 4.83 client build (2000-10-12) but **~5 months before** the
4.95 client build (2001-06-29) — so both skills were live in-game well before 4.95
shipped, and neither depends on client-protocol features newer than 4.95 (NPC-dialog
driven, like every other craft). Verdict: implement both, but off by default via
`CraftingToggles` — a GM enables them per-target-era by editing
`data/game-data/CraftingToggles.csv` and running `@reload`, rather than the server
assuming one era.

## Skill-gain mechanic — use SUCCESS-ONLY everywhere

Archive rule: *"skills are based on successes, not on materials used."* → **success = +1,
failure = +0.** No graded weighting (a "masterful" success is still just +1).

RTK gets this right only for **food preparation** and **chef** (increment inside the
success branch). For **smelting, gemcutting, weaving, potion making, scribing, fishing**
RTK calls `skillChanceIncrease` *before* the roll, so failures count the same as
successes. When porting: gate the increment on success for ALL crafts.

Keep RTK's bonus rule: +1 if `crafting_bonus` buff active; 1/25 chance of +1 on totem time.

## Skill-progression thresholds

RTK matches the archive **Novice→Master almost byte-for-byte**, then diverges at
**Master→Legendary** (RTK ~2–6× easier). Corrections, favoring the archive:

- **All crafts:** restore the archive's Master→Grand Master / Champion / Legendary
  intervals (RTK's high end is not authentic). Where the archive stops at Master, leave
  GM+ as unknown/TODO rather than inventing.
- **Mining** 🔴: RTK discarded the real values and reused the woodcutting curve. The
  authentic numbers survive commented as *"NTK numbers"* on `crafting.lua:201`
  (`{0,632,4700,11511,22836,48151,75466,110636,…}`) and reproduce the archive chart
  exactly. Use those.
- **Gemcutting** 🔴: RTK diverges at every rank (16/60/310 vs archive 100/300/1440).
  Use archive: cumulative 100 / 400 / 1840 / 3990 / 7340 / 14820 / 22980 / (+GM 38390 /
  Champ 57580 / Leg 99590).
- **Tailoring**: Apprentice→Accomplished is 1440 in the archive → cumulative Accomplished
  = 1770; RTK has 1730. Use 1770.
- **Jewelry** 🟡 (implemented but gated OFF by default via `CraftingToggles`, see "Launch
  scope" above): RTK ratio vs. archive is NOT a flat ≈4×, it trends down per rank (4.0, 3.33, 2.67,
  2.375, 2.53, 1.61, 1.37, 1.93, 2.45, 2.97 across ranks) — archive is "crafted ambers
  *used*" and a ring eats several ambers, likely a units difference, not an error. Two
  independent tutor posts (Mage Nussan's Manufacturing chart + Rogue xFiReStOrMx's
  "Jeweling Chart", credited to a third contributor "tigertiger") give byte-identical
  numbers, so the archive figures themselves are solid — verify ambers-per-craft before
  changing anything.

## Gathering skill level gates

Two tutor sources conflict on Woodcutting's starting level:
- Mage tutor Nussan (`Nussan 09230096.html`, "Gathering skills info"): Woodcutting = 12,
  Mining = 8, Fishing = 8.
- Rogue tutor Melalye (`tutor 03220051.html`): "Level 8 to begin - Mining and Woodcutting"
  (groups Woodcutting with Mining at 8).

No third independent source in the archive states a starting level for any of the three.
**Use Nussan's numbers (Woodcutting=12, Mining=8, Fishing=8).** Reasoning: Melalye's
crafting write-up is itself signed "-Gull, Buyan Mage tutor. [Edited by Melalye]" — a
secondhand digest of another Mage tutor's work reposted onto the Rogue board, not
independent Rogue-tutor knowledge — and a Poet tutor post (`MoonWater 12080214.html`)
explicitly points readers to Nussan's board as the community's recognized canonical
crafting reference. The Melalye line reads as a compression artifact (three numbers
collapsed into one shared "Level 8").

### Smelting / fishing — units resolved (see mechanic note above)

Under success-only, in SUCCESS units:

- **Smelting** = cumulative (Metal + Fine Metal) from the archive:
  `0 / 16 / 108 / 496 / 1290 / 3661 / 7782 / 13674` (Novice→Master; GM+ unknown).
  (Derivation: archive Nov→App 74 ore → 13M+3F = 16 successes = 21.6%, matches
  smelting.lua's poor/med/high 12/37/62% formula.)
- **Fishing** = archive "total fish" counts (≈1 fish per successful cast):
  cumulative `~80 / 967 / 2315 / 8730 / 18534 / 32417 / 49101 / 90114`. Discard RTK's
  fishing curve — it is unrelated to the archive.

## Recipes

Materials in RTK match the archive exactly for every potion, scroll, chef noodles, and
food prep. Corrections (favoring archive):

- **Dart poison**: gate at **Adept** (RTK exposes it at Novice as a base option).
- **Black potion**: gate at **Talented** (RTK requires Master).
- **Food preparation**: add **Lean beef** as a valid input (archive lists it; RTK only
  handles plain beef). Maps to the same grilled-beef success output, 1:1.

Validated as-is (no change): purple/lime potions; all four scrolls
(Protection/Invocation/Defense/Immortality) incl. skill gates; chef noodles
(1 flour + 1 water jug + 1 salt block, Accomplished food-prep); **Chef itself gates on
Accomplished food-prep** to even start (archive: "you need to be Accomplished Food
preparer to start Chef"; RTK `chef.lua:20-24` checks this in code).

## Class effects — none except caster-only mental skills

There is **no class-based material discount** (e.g. "warriors use less ore") in RTK or the
archive. Material costs are fixed per recipe for everyone; skill gain is a flat +1 with no
class multiplier. The only class reference in all of crafting is the caster gate
(`crafting.lua:36`, `baseClass ~= 3 and ~= 4`) restricting **scribing / potion making to
Mage/Poet** — confirmed by the archive ("Only mages and poets can do these skills"). The
Crafting/*.lua subfiles have zero class/path/subpath references.

The tutor line *"depends on your path, subpath, totem time and luck"* is about **success-rate
variance**, not material cost — and RTK implements no per-class/subpath success rates anyway
(smelting success = `base% + skill×2`). The only real modifiers are totem time (1/25 bonus
point) and the premium `crafting_stone` buff (+1/attempt, 2h) — neither class-based. Any
class/subpath flavor would be a NEW design choice, not a restore.

## Structure & costs — validated

Manufacturing/mental start level **50**; refining specialization level **25**; devotion
cost **1000c**; one-manufacturing / one-mental / one-refining-specialization exclusivity;
lose-all-on-abandon. RTK charges **500c** for refining specialization (archive silent —
no conflict). **TODO:** verify the archive's "level 25 to start food-preparing" gate is
enforced somewhere (not present in `foodpreparation.lua`).

## Exclusivity is ASYMMETRIC between Refining and Manufacturing — must implement both ways

This was missing from earlier drafts of this doc and is the single most consequential
structural rule for a faithful port:

- **Refining** (Weaving / Smelting / Gemcutting) — **soft cap**. Specializing in one lets
  you keep practicing the other two, but only up to **Accomplished**; past that you must
  specialize (archive: *"In the other two, you can reach accomplished"*; RTK
  `crafting.lua:21-25` blocks only progress *past* Accomplished with "you must
  specialize").
- **Manufacturing** (Tailoring / Carpentry / Smithing / (Jewelry, post-launch)) — **hard
  lock**. Specializing in one blocks you from the other three entirely, no Accomplished
  allowance (archive: *"You cannot do the other three at all"*; RTK `crafting.lua:28-34`
  — "You do not posses knowledge in X" rejects any attempt without the legend mark).

Both archive and RTK code agree on this distinction. Implementation note: RTK's
`player.level<25` gate lives specifically inside `addSpecialization` (the *specialize*
action itself) — the base Weave/Smelt/Gem practice functions have no level check at all.
The archive's phrasing ("level 25 to begin all of the Refining skills") reads as gating
basic practice too, not just the specialize action — worth deciding explicitly when
porting rather than silently inheriting RTK's narrower gate.

## Noted but out of scope for this doc (not values, no numbers to correct)

- **Totem-time crafting-machine tables**: `Nussan 09240108.html`/`11260129.html`,
  `Living 05140076.html`, `Sadiq 10120135.html` give a full per-totem-animal/per-hour
  machine-option lookup table (~1500 lines across the four posts). Sadiq's repost
  (credited "correction" from CastleberrY) differs from Nussan/Living for Baekho hours
  21-23 — trust Sadiq's version if this table gets ported.
- **Guk-su NPC** (`Kenzi 11230196.html`): a progress-check merchant at Dae Shore
  (60,21), 1000c fee, reports a 7-tier % breakdown of progress to next skill level; also
  sells Cook Bowls (200c) for Broth. Real supporting mechanic, not ported yet.
- **Woodcutting tree-age yield math** (`Nussan 09240107.html`): tree age (New/Young/Old/
  Ancient) sets base wood yield (1 / 1-2 / 1-3 / 1-4) with a skill-based doubling chance.
  RTK's `woodworking.lua` has no tree-age logic at all — if this fidelity matters, it has
  to come from the archive, not ported from Lua.
