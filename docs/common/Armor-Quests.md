# Star / Moon / Sun armor quests

Twelve quests — three tiers for each of the four paths — and the level-66-and-up spine of the 4.95 endgame.
This is what was built, what each step demands, and every place the surviving sources disagree about it.

Wind is deliberately out of scope. It is a different giver (the Scribe atop Scribe's Mountain in Vale), it
was never released in the era the pages were written, and every one of them says so in the same sentence.

**Code**: [`Server/ArmorQuest.cs`](../../Server/ArmorQuest.cs) (the twelve chains as data),
[`Server/ArmorQuestAbility.cs`](../../Server/ArmorQuestAbility.cs) (the step runner),
[`Server/TotemWorship.cs`](../../Server/TotemWorship.cs), [`Server/Mentorship.cs`](../../Server/Mentorship.cs).
**Data**: [`game-data/ArmorQuests.csv`](../../game-data/ArmorQuests.csv) (level + karma gates).
**Tests**: [`Tests/ArmorQuestTests.cs`](../../Tests/ArmorQuestTests.cs).

---

## How a chain works

Say **"star"**, **"moon"** or **"sun"** to your own path's guildmaster in **Kugnae or Buya**. He tells you
what he wants. Go do it, come back, say the word again. That is the entire interface — there is no menu
entry, and clicking him shows the ordinary trainer menu.

| | |
|---|---|
| **Prerequisite for Star** | *Blessed by the Stars* (below) |
| **Star gives** | a bonded Star garment + the legend `mastered_the_stars` |
| **Moon needs** | `mastered_the_stars`; consumes a **Star** garment; gives `understood_the_moon` |
| **Sun needs** | both; consumes a **Moon** garment; gives `survived_the_sun` |
| **Moon also unlocks** | that path's **Wind** quest, alongside Sun |

The garment a chain consumes **need not be your own**. Every page says so in near-identical words —
*"whether it's bonded to someone else, your bonded one, or even a nonbonded one"* — which is precisely why
unbonded Star and Moon armor had a resale market.

### Rules that apply to every step

1. **Items must be at 100% durability**, and **worn copies count and will be taken.** Implemented as
   `Session.CountReady` / `Session.TakeReady`, which look at the bag *and* the equipment slots and skip
   anything worn down. Bag stacks are spent first, so you keep what you are wearing whenever the bag can pay.
2. **Nothing is taken unless everything is present.** The period pages warn that the NPC "may take the ones
   you have and not give you credit"; we check the whole ask first, then confirm, then take. The warning is
   still spoken, because worn gear really does come off.
3. **Kill counts are deltas from the moment he asked.** Kills you banked before the ask do not pay for the
   step. This is what makes *"make SURE these are the LAST creatures you kill before returning"* a real rule
   rather than a superstition.
4. **Tier-scaled mobs are satisfied by any tier.** Warrior Star wants "18 monkeys of your Mythic Monkey
   tier"; the step accepts 18 Spry, 18 Agile *or* 18 Fast. You can only reach the cave you qualify for, so
   this is the same rule stated without a cave-tier lookup.
5. **"And nothing else" comes in two strengths**, matching the two ways the sources word it:
   - *"kill 200 Rabbits and nothing else"* → **strict**: every kill since the ask must have been a rabbit.
   - *"WITHOUT killing any other creatures in that cave"* → **scoped**: a named list of that cave's ordinary
     residents is forbidden; a kill anywhere else is fine.
   Breaking either **restarts that step's count**, and does not fail the chain. (Atlas's own phrasing is
   "you will have to start from step 1" — the gentler reading is the only one that leaves a poisoned counter
   recoverable.)

### Blessed by the Stars

Carry a **White amber** into the **Mythic Nexus** (map 41) and **drop it in the middle circle**. It is
absorbed rather than dropped, and you get the mark. **Level 60 required.**

Two independent witnesses for the method — nexusatlas `quests/blessed.php` ("*Simply obtain a White Amber
and drop it in the middle circle area of Mythic Nexus*") and the official-board Rogue tutor guide. The level
gate comes from Atlas alone and is implemented nowhere else, RTK included. Atlas mentions an alternative
opening (say "Stars" to Ironheart in Kugnae) and immediately says of it *"The 'Stars' part can be skipped if
you wish"* — it is a signpost to the amber, not a second route, and is not wired.

---

## Warrior — Scale Mail / Mail Dress

### Star · level 66 · Rabbit karma
1. Slay **18 monkeys** of your Mythic Monkey tier (Spry / Agile / Fast).
2. **2 Titanium gloves.**
3. **1 Electra.**
4. **1 Might, 1 Karma** → bonded *Star scale mail* / *Star mail dress*.

### Moon · level 76 · Dog karma
1. Slay the **glowing Pig boss** (item boss) of the Mythic Pig Cave.
2. Slay **30 dogs** of your Mythic Dog tier (Mad dog / Crazed mongrel / Frothing mutt).
3. Slay **20 Grim ogres** and bring **20 Amber**.
4. **1 Titanium glove, 3 Electras** · **2 Might, 1 Grace.**
5. Your **Star** garment · **2 Karma** → bonded Moon garment.

### Sun · level 86 · Tiger karma
1. Win **2 Carnages** (see *Carnage*, below).
2. Slay **60 Ice ogres** and **60 Frost ogres**.
3. **20 White ambers, 2 Titanium gloves, 4 Electras, 2 Corrupted blades** — *a portion is returned*.
4. Slay **200 rabbits, and nothing else.**
5. Slay **200 squirrels, and nothing else**, and hold **14 Gold acorns**.
6. Slay **both monkey bosses** (item + key) of your Mythic Monkey tier.
7. Your **Moon** garment · **20,000 coins, 3 Might, 2 Grace, 2 Will, 3 Karma** → bonded Sun garment.

## Rogue — Waistcoat / Blouse

### Star · level 66 · Rabbit karma
1. Slay **2 Slime ogres or 2 Muck ogres**.
2. **2 Whisper bracelets.**
3. **1 Steelthorn.**
4. **1 Grace, 1 Karma.**

### Moon · level 76 · Dog karma
1. Slay the **Dog item boss** — "the dog with a rose in its mouth" (Assassin / Cutthroat / Avenger by tier).
2. **2 Whisper bracelets, 2 Steelthorns, 50 Amber, 10 Dark amber, 1 Lucky coin, 15,000 coins.**
3. Present a **White Moon Axe** — **rebonded to you**, not consumed. Any will do, including one bonded to
   somebody else.
4. Your **Star** garment · **2 Grace, 2 Karma.**

### Sun · level 86 · Bear karma *(disputed — see below)*
1. Slay **12 Ice panthers** and **Citelam**, in Sacred Grove at the end of the Northern Ogre Cave.
2. Slay **both Rat bosses**, and nothing else in that cave.
3. Slay **both Rabbit bosses**, and nothing else in that cave.
4. **8 Steelthorns, 5 Whisper bracelets, 6 Corrupted rings, 20 Gold acorns, 50,000 coins** — *a portion is
   returned*.
5. Your **Moon** garment · **2 Might, 3 Grace, 2 Will, 3 Karma.**

## Mage — Garb / Dress

### Star · level 66 · Rabbit karma
1. Slay the **Skeleton mage** and the **Skeleton warrior** (Kugnae Haunted House basement).
2. **2 Holy rings.**
3. **1 Star-staff.**
4. **1 Will, 1 Karma.**

### Moon · level 76 · Ox karma
1. Slay **the creature with the shortest name in the Nexus** — **Li**, deep in Sute's Cave.
2. Slay **the slowest creature in the Nexus** — the **White wolf**, Buya Fox cave.
3. The full **trigram key set** (Earth, Fire, Heaven, Mountain, Wind, Pond, Thunder, Water) + **Sute's key**.
4. **2 Star-staves, 1 Holy ring.**
5. Your **Star** garment · **2 Will, 2 Karma.**

### Sun · level 86 · Tiger karma
1. Slay **40 Fluffs and 40 Thumps** (Rabbit 3) *or* **60 Mad hares and 60 Giant rabbits** (Rabbit 2).
2. **Three items with "Star" in the name** (see the *Star burst* note below).
3. Slay **one creature with "Slow" in its name** — Skeleton warrior, Wild horse or Wild rooster.
4. **20 White ambers, 4 Holy rings, 5 Star-staves, 2 Corrupted staves.**
5. Slay the **Massive scorpion** (Kugnae Spider cave).
6. Slay **200 rabbits, and nothing else.**
7. Slay **200 squirrels, and nothing else**, and hold **14 Gold acorns**.
8. Your **Moon** garment · **2 Might, 2 Grace, 3 Will, 3 Karma.**

## Poet — Robes / Gown

### Star · level 66 · Rabbit karma
1. Slay **9 Nine-tailed foxes** ("once for each tail").
2. **2 Sen gloves and 1 Titanium lance.**
3. **1 Will, 1 Karma.**

### Moon · level 76 · Ox karma — *the only Moon chain with no combat at all*
1. Be **married**.
2. **50 Roses.**
3. **Mentor 3 people** (see *Mentorship*).
4. Your **Star** garment · **2 Will, 2 Karma.**

### Sun · level 86 · Bear karma
1. Slay the **Massive scorpion**.
2. Slay **Sute**.
3. **10 Crafted white ambers, 1 Purified water, 6 Sen gloves.**
4. **Worship all four totems in order** — Chung ryong, Baekho, Ju Jak, Hyun moo (see *Totem worship*).
5. **Adept or higher in Tailoring, Smithing or Carpentry** — ⚠ **currently a hard stop**, see below.
6. **2 Titanium lances.**
7. Your **Moon** garment · **5 Karma** (no stat points).

---

## The gate table

Levels are unanimous across every source. Karma is not, which is why both live in
[`ArmorQuests.csv`](../../game-data/ArmorQuests.csv) and can be changed with a `@reload`.

| Path | Star (66) | Moon (76) | Sun (86) |
|---|---|---|---|
| Warrior | Rabbit | Dog | Tiger |
| Rogue | Rabbit | Dog | **Bear** ⚠ |
| Mage | Rabbit | Ox | Tiger |
| Poet | Rabbit | Ox | Bear |

---

## Sources, and how they were weighed

Three witnesses, none of them the game.

| Source | Era | Good for |
|---|---|---|
| **tswolf.com** `/quests/armor/{warrior,mage,poet,rogue}.shtml` | Jan–Jun 2001 | **Step structure** — which asks exist, in what order |
| **nexusatlas.com** `/quests/{warrior,mage,poet,rogue}armor.php` | 5.x | **Exact values** — every item, every stat and karma point, itemised per step |
| **Official-board tutor walkthroughs** | 2000s | Eyewitness detail, but only the **Rogue** guide is independent |

**The Warrior tutor guides are not a cross-check.** Veggs's opens "Information borrowed from
www.nexusatlas.com" and SoulHunter's is near-identical to Veggs's — one source, not two. The **Rogue** guide
(Ssjxrouge, ed. Melalye) is the genuinely independent account: its own prose, and details found nowhere else
(it names Rogue Maro, and it is the second witness for the white-amber method).

**RTK-Server's Lua is not a source for requirements here.** It is a hobby fan-server reconstruction, and per
this project's standing rule the archive outranks it. Anything it asks for that neither period page mentions
was dropped. It *is* used for **prose** — the guildmasters' spoken lines survive nowhere else, and RTK's
strings are ported dialogue rather than invented text. Its signature double-letter corruption is corrected on
the way in ("anotheer" → "another", "otheer" → "other"), the same call made for the Sute tale.

### Atlas omits nothing tswolf has

Checked step by step across all four paths: **every tswolf step appears on the corresponding Atlas page.**
The conflicts are always about a *value*, never about whether an ask exists.

### The four value conflicts, and how each was resolved

| | tswolf | Atlas + RTK | Shipped | Why |
|---|---|---|---|---|
| Poet Star foxes | 1 | **9** | 9 | Two sources; and RTK's own line is "slay him once for each tail" |
| Poet Moon roses | 20 | **50** | 50 | Two sources |
| Moon karma, Mage & Poet | Dog | **Ox** | Ox | tswolf prints "Dog Karma" on **all four** of its pages, and prints an identical Wind paragraph on all four — the line is visibly boilerplate. Warrior and Rogue are Dog on every source, so only two rows move. |
| Sun karma, Mage / Poet | Bear / Tiger | **Tiger / Bear** | Tiger / Bear | Same boilerplate problem, the same two independent sources agreeing against it |

### ⚠ Genuinely unresolved: Rogue Sun karma

nexusatlas says **Bear**; the tutor-board guide says **Tiger**. The other three paths agree across sources;
this one does not, and no third witness settles it.

**Bear ships**, for two reasons. It is the *recoverable* direction — Bear is 8 karma and Tiger is 11, so a
character built for Bear can still climb to Tiger, while the reverse wastes the effort. And it keeps the
ladder symmetric with Poet. Flipping it is one row of `ArmorQuests.csv` plus one line of
`ArmorQuestTests.TheShippedKarmaSplitIsTheDocumentedOne`, which is pinned deliberately so the change is
deliberate.

### Reorderings

Same steps, different sequence. Resolved case by case:

- **Warrior Moon** — pig boss before the 30 dogs (Atlas + RTK) rather than after (tswolf).
- **Mage Sun** — the three Star items before the "Slow" creature (Atlas + RTK).
- **Poet Moon** — **marriage before the roses.** Atlas lists roses first but then cites them as "[Step 2]"
  in its own sacrifice list; tswolf and the tutor guide both put marriage first. Two internally-consistent
  accounts beat one that contradicts itself.
- **Poet Sun** — the Massive scorpion and Sute are **two separate asks** (tswolf + tutor); only Atlas folds
  them into one line of its step 1.

### Dropped: things only RTK asks for

| Dropped | Where RTK put it |
|---|---|
| **200 squirrels** on Rogue Sun's final step | neither page has it; both Warrior and Mage Sun genuinely do |
| **Splitting Poet Star's gloves and lance into two asks** | both pages have them as one ask |
| **3 Will** on Poet Sun's final step | both pages say 5 Karma and no stats |
| **`player.baseGrace = player.baseWill - 2`** on Mage Moon | a plain typo in RTK; both pages say 2 Will |

### Kept from RTK, with the reason

- **The partial return.** Both pages state that a portion of the Warrior Sun and Rogue Sun tribute comes
  back; only RTK records *how much*. Its ranges are used (`ArmorStep.KeepBack`) and nothing else in those
  steps is.
- **All spoken dialogue**, per the note above.
- **The legend icon/colour** (5 / 128). No page records a glyph index; it is cosmetic.
- **The totem worship costs and karma odds** — but see below, where the period sources corroborate them.

### Star burst

Both pages name **four** items with "Star" in the name for Mage Sun step 2, and ask for any three: Star
powder, Stardrop, Star-staff and **Star burst**. Star burst is carpenter-made and **has no row in the 4.95
item registry**, so the pool that actually exists is exactly three and the step has no slack. RTK has the
same gap — its own script lists `star_burst` and its own database has no such item, so its `removeItem` call
was always a no-op. `ArmorQuestTests.ThreeStarItemsIsSatisfiable` pins this, and will notice if a Star burst
row is ever added.

---

## Systems these chains lean on

### Carnage — Warrior Sun step 1

**In scope** (Atlas and the tutor guide both have it; only tswolf's Jan-2001 page lacks it), and carnage is
firmly in era — the news archive has players reminiscing about "the carnage's way back in Beta and 3.0".

There is no carnage system on this server, and there is a good argument that there does not need to be one
for this step to be real: carnage was a **hosted** event, so who won it was always knowledge a human had
rather than something a server derived. So the step reads a `carnage_wins` counter that a GM writes:

```
@carnage <name> [n]
```

`n` defaults to 1 and may be negative. Warrior Sun wants two.

### Totem worship — Poet Sun step 4

Built: [`Server/TotemWorship.cs`](../../Server/TotemWorship.cs), a **Worship &lt;Name&gt;** entry on the four
shrine animals (Baekho 388, Chung ryong 389, Hyun Moo 390, Ju jak 391 — *not* the four totem priests on map
4, who are a different set).

- **Offering**: that totem's key, or **five Gold acorns**. If you already worship it, reaffirming costs
  **one** Gold acorn. All three sources agree, and the third explains the second: Atlas's "*a totem key… or
  5 gold acorns*" plus tswolf's odd-sounding "*all 4 Mythic Keys, or 3 and a Gold Acorn for your totem*" —
  three keys for the three that aren't yours, one cheap acorn for the one that is.
- **Once per 21 real hours**, shared across all four shrines.
- **Karma**: a quarter point at RTK's 1-in-5 odds, with a pity counter that forces it on the fifth miss
  (an eighth for the cheap reaffirmation). Only RTK witnesses the odds; the pages say only that frequent
  worship "will doubtless improve your Karma", which this satisfies either way.
- Worshipping **changes your totem**, and with it your totem-time experience window.
- The Poet Sun counter advances only on the **next** totem in the sequence. Worshipping out of order does
  **not** reset it — no source says a mis-step costs the run, and at 21 hours a turn that would be a brutal
  invention.

Not ported from the same script: the "Totem Animals" lore menu, the totem-helmet forging quest, and the
Leviathan "Forgive" branch.

**Not implemented: the vow of silence.** The tutor guide imposed one through the worship period (no speaking
to NPCs, no carnage, no Mythic caves). Its own 2008 update reports poets saying it no longer applied, and the
current Atlas page retracts it outright — "*You may go to Carnages, hunt and talk to NPCs if you want.*" A
dated revision, not a conflict.

### Mentorship — Poet Moon step 3

Built: [`Server/Mentorship.cs`](../../Server/Mentorship.cs). The **Mentor** ability already existed in
`Spells.csv` (id 106, taught by every trainer at level 40) and did nothing; it now runs a two-cast
relationship.

- Cast near a player at **level 3–8** who has never been mentored and has no mentor → they get an
  accept/decline prompt.
- Cast near that protégé at **level 15+** → the mentorship culminates, your tally rises by one, and they take
  a permanent "Mentored by …" mark. One per life, which is what stops two friends farming the tally.
- The mentor is remembered **by name**, so the relationship survives both parties logging out. It has to —
  it spans a dozen levels of the protégé's growth.

**RTK's script is repaired, not transcribed.** Its culmination branch sits inside
`if target.level < 3 or target.level > 8` and then tests `target.level >= 15`, so the two arms of its own
conditional contradict each other and the offer path is unreachable for exactly the levels it advertises.
Its *dialogue* states the rule plainly, and per the standing rule that RTK's strings outrank RTK's logic, the
strings are what got implemented.

**No karma penalty.** Atlas warns the Poet Moon step "could bring you to dramatically low karma (Snake!) if
you are not careful", but nothing in Atlas or RTK says the cast itself docks karma. The likeliest reading is
the obvious one — shepherding a level-3 through twelve insights means hunting far below your own level, which
is where the karma goes. Inventing a debit would be guessing.

### ⚠ Crafting rank — Poet Sun step 5 is a hard stop

Both pages carry the step ("become Adept in a Manufacturing skill" / "Adept, or higher, in a Refining
Skill"), so it belongs in the chain. There is **no crafting skill system** on this server — no skill points,
no ranks, and no manufacturing recipes to earn them from. Gathering is modelled
(`Session.Harvest.cs`) but feeds none of Tailoring, Smithing or Carpentry.

**Decision (owner's call): implement the gate faithfully and leave it blocked.** It reads a real per-skill
point registry against RTK's Adept thresholds (`ArmorQuest.ManufactureSkills` — Tailoring 3910,
Smithing 2040, Carpentry 2250); nothing writes those keys yet, so **Poet Sun is walkable through step 4 and
then stops.** The day manufacturing crafting is ported, this step starts working with no change here.

The three other paths' Sun chains are unaffected and complete end to end.

---

## What is not built

- **Wind armor** — out of scope by request; a different giver and a different era.
- **Crafting ranks** — above.
- **A carnage system** — the step is satisfiable by GM record instead.
- **Re-blessing resetting the line.** The Rogue tutor guide describes Blessed by the Stars as repeatable
  "which restarts the whole line from scratch". Stripping a player's Moon and Sun legends on a re-bless is
  far too destructive to build on one line of hearsay; a second amber simply does nothing.
