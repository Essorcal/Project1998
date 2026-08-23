# Lesser alliances with the mythic animals

Twelve quests, one per zodiac cave. The animals are at war in fixed pairs, and each keeps a chamber past
its guard room where it waits behind a black gate. Say the name of its **enemy** to it and it offers you a
favour: kill three of each of that enemy's two leaders, bring back a tribute stolen from their cave, and you
are its ally for good.

Greater alliances are **not** built — see [What is not built](#what-is-not-built).

**Code**: [`Server/MythicAlliance.cs`](../../Server/MythicAlliance.cs) (the model and the conversation),
`Shared.KillTrack` in [`Shared/Character.cs`](../../Shared/Character.cs) (the eight-slot list everything
below turns on), `Session.NpcCastRebirth` / `NpcCastStormstrike` in
[`Server/Session.Spells.cs`](../../Server/Session.Spells.cs).
**Data**: [`game-data/MythicAlliances.csv`](../../game-data/MythicAlliances.csv) — hot-reloads with `@reload`.
**Tests**: [`Tests/MythicAllianceTests.cs`](../../Tests/MythicAllianceTests.cs).

---

## How one runs

1. **Get into the guard room.** Every cave but the Rat's is entered by *falling* — walk the right room and
   the floor gives way ([`game-data/FallRooms.csv`](../../game-data/FallRooms.csv)). The Rat's you simply
   walk into from its sentry room. Atlas names the fall rooms per cave; so does the tutor-board chart.
2. **Find the door.** Somewhere in the guard room is a door into the mythic's chamber. All three cave tiers
   lead to the same chamber, which is why the reward below works "on any level cave".
3. **Say the enemy's name.** Standing near the gate, say `Dog` to the Dragon, `Rooster` to the Rabbit, and
   so on. That single word is the entire interface — for the offer, for the hand-in, and for every visit
   afterwards. Clicking a mythic does nothing, exactly as clicking any other creature does.
4. **Kill three of each of the enemy's two leaders**, in one cave tier: its **key boss** (Mythic / Divine /
   Spirit) and its **item boss** (the named one; at cave 3 they are all *Avengers*). See
   [The kill track](#the-kill-track) for the part that actually makes this hard.
5. **Bring the tribute** — four of the enemy cave's item and four or eight of its key.
6. **Say the enemy's name again.**

## The twelve

Each mythic sends you into its enemy's cave, and everything it asks for is looted there.

| Alliance | Enemy | Tribute (all of it from the enemy's cave) | Favour |
|---|---|---|---|
| **Rat** | Horse | 4 × Pearl charm + 8 × Key to Thunder | Rat's favor |
| **Horse** | Rat | 4 × Ambrosia + 8 × Key to Pond | Horse's favor |
| **Dragon** | Dog | 4 × Fragile Rose + 8 × Key to Wind | Dragon's favor |
| **Dog** | Dragon | 4 × Dragon's liver + 3 × Chung ryong key | Dog's favor |
| **Rabbit** | Rooster | 4 × Tao stone + 8 × Key to Heaven | Rabbit's favor |
| **Rooster** | Rabbit | 4 × Lucky Coin + 8 × Key to Earth | Rooster's favor |
| **Snake** | Pig | 4 × Magical dust + 8 × Key to Mountain | Snake's favor |
| **Pig** | Snake | 4 × Scribe's book + 4 × Hyun moo key | Pig's favor |
| **Sheep** | Ox | 4 × Scribe's pen + 8 × Key to Water | Sheep's favor |
| **Ox** | Sheep | 4 × Lucky silver coin + 4 × Ju jak key | Ox's favor |
| **Tiger** | Monkey | 4 × Battle helm + 8 × Key to Fire | Tiger's favor |
| **Monkey** | Tiger | 4 × Purified water + 4 × Baekho key | Monkey's favor |

Eight keys where the cave has a key of its own; **four** where the cave drops one of the four **totem**
keys — the same keys the Wilderness shrines take, and the reason those four caves ask for half as many.

### The bosses, by cave

Three of **both** halves of **one** row. The pairs are the cave tiers, and the tiers are level-banded
(see [`game-data/MythicCaves.csv`](../../game-data/MythicCaves.csv)), so in practice the pair you can reach
is the pair you fight.

| Cave | Cave 1 | Cave 2 | Cave 3 |
|---|---|---|---|
| **Rat** | Mythic rat + Mighty mouse | Divine rat + Rat lord | Spirit rat + Rat avenger |
| **Horse** | Mythic horse + Horse do | Divine horse + Horse chongun | Spirit horse + Horse avenger |
| **Dragon** | Mythic dragon + Dragon mage | Divine dragon + Dragon slayer | Spirit dragon + Dragon avenger |
| **Dog** | Mythic dog + Dog assassin | Divine dog + Dog cutthroat | Spirit dog + Dog avenger |
| **Rabbit** | Mythic hare + Hare witch | Divine rabbit + Rabbit witch | Spirit rabbit + Rabbit avenger |
| **Rooster** | Mythic rooster + Rooster swordsman | Divine rooster + Rooster barbarian | Spirit rooster + Rooster avenger |
| **Snake** | Mythic snake + Snake shaman | Divine snake + Snake mage | Spirit snake + Snake avenger |
| **Pig** | Mythic boar + Boar champion | Divine pig + Pig champion | Spirit pig + Pig avenger |
| **Sheep** | Mythic sheep + Sheep veteran | Divine sheep + Sheep shepherd | Spirit sheep + Sheep avenger |
| **Ox** | Mythic ox + Ox gorer | Divine ox + Ox charger | Spirit ox + Ox avenger |
| **Tiger** | Mythic tiger + Tiger warrior | Divine tiger + Tiger slasher | Spirit tiger + Tiger avenger |
| **Monkey** | Mythic monkey + Monkey mauler | Divine monkey + Monkey basher | Spirit monkey + Monkey avenger |

## The kill track

This is the mechanic the quest is really about, and the one thing about it worth reading twice.

The game remembers only **the last eight KINDS of creature you have killed**, most recent first, each with
a count that tops out at 255. Kill a ninth kind and the least-recently-killed one drops off the end — and
its count is gone. `Shared.KillTrack` is that list; `Character.KillTrack` persists it.

Everything the period sources say about alliances falls out of those two sentences:

- **"You must avoid killing anything else"** is not a rule the NPC enforces. Two of your eight slots hold
  the bosses, so **six other kinds are free** — and the *number* you kill of each of those six does not
  matter at all. 255 rabbits cost you nothing; one squirrel too many kinds costs you a boss.
- **Accepting an alliance wipes the track.** So bosses banked before you accepted do not count, and the only
  order that works is: start every alliance you mean to run, *then* go hunting. Accepting a second one
  midway through the first costs the first its banked bosses — the alliance itself stays accepted and the
  tribute stays in your bag, but the kills have to be made again.
- **Four at a time is the ceiling**, and nobody had to code that. Two slots each, eight slots total.
- **The tutors' trick works.** Made a mistake? Kill one more of each boss *before* the next one. They move
  back to the front, so the thing that falls off next is a mistake instead of a boss — and a count survives
  being moved.
- **Group kills count for everyone**, which is also why a careless group-mate can cost you the run: the
  track is fed from the same place experience is, so every kill the group is paid for lands on it.

Lifetime kill counts (`Character.Kills`, what the armor chains read) are a **separate** tally and are never
disturbed by any of this.

## Rewards

Paid on the hand-in, all five of them:

| | |
|---|---|
| **Karma** | **3 points** |
| **Item** | that animal's **favour**, "which can be used for joining an NPC subpath" |
| **Experience** | **10,000,000** |
| **Legend** | *Lesser alliance with the &lt;Animal&gt; (date)* |
| **Forever after** | say the enemy's name at any of that cave's three tiers and the mythic casts **Rebirth** on you — a full heal, and a resurrection if you are a ghost |

And two punishments, both real:

- **Wearing the enemy's mark.** Say the word to an animal whose enemy you are already sworn to and it
  Stormstrikes you and sends you home to a tavern. The tutor board states it plainly: *"If you try to do an
  alliance with the enemy of an animal you already alli'ed with, it will zap you and send you to tavern."*
- **Refusing to its face.** *"Then die."* Same treatment. Only RTK's script witnesses this one, but it is
  the same beat as the above, so it stands.

There is **no level gate and no karma gate** beyond the ordinary scum floor. The caves gate themselves.

## Where the sources disagree

Four witnesses: Nexus Atlas's twelve alliance pages and its Alliance Tips page; Head Tutor Nussan's
walkthrough; Atlas's 5.0 per-cave drop tables; and RTK's `mythic_alliance_npc.lua`. RTK is used for
**prose only** — the mythics' lines survive nowhere else — on the standing rule that the archive outranks
it. Three decisions were close enough to write down.

### 1. The tribute item for four caves — and a bug in our own drop tables

Atlas's and Nussan's tribute tables both name the **wrong cave's item** for Horse, Tiger, Rabbit and Sheep
(they are rotated in a four-cycle). They are not independent witnesses on this point — the two tables share
a lineage — and both contradict:

- **their own rule**, stated on every alliance page: the tribute is *"items and keys stolen from the enemies
  cave"*, and the **key** half of all twelve is uncontested and always the enemy's;
- **Atlas's own 5.0 cave pages**, which are per-creature: the Rat page gives *Mighty Mouse → Ambrosia*, the
  Rooster page gives *Rooster Swordsman → Tao stone*;
- **the tutor-board drop chart** (Nussan, *Mythic cave boss drops*): *"Rat Item Drop: Ambrosia"*, *"Monkey
  Item Drop: Battle Helm"*, *"Rooster Item Drop: Tao Stone"*, *"Ox Item Drop: Scribe's Pen"*.

So the drop tables win and the tribute tables lose. **This also fixed a real bug in this repo**:
`MobDrops.csv` carried the same four-cycle scramble (it came in from RTK, which is internally consistent
but disagrees with the period sources), and it was already flagged in
[`re/global_cross_reference.md`](../../re/global_cross_reference.md) as *"mighty_mouse: missing
('ambrosia')"* and friends. Twenty-four rows across the Monkey, Rat, Rooster and Ox caves were corrected:

| Cave | was (RTK) | now (period) |
|---|---|---|
| Monkey | Ambrosia | **Battle helm** |
| Rat | Battle helm | **Ambrosia** |
| Rooster | Scribe's pen (+ Tao stone rare) | **Tao stone** (+ **Ambrosia** rare) |
| Ox | Tao stone | **Scribe's pen** |

`MythicAllianceTests.TributeIsFarmableInTheEnemysCave` pins it: every tribute must be lootable off the
bosses the quest sends you to, checked against the drop tables rather than merely against the item registry.

Still not reconciled, and left alone as out of scope: a handful of *rare* secondaries the period chart lists
that we do not carry (Rat sentries' Tao stone, Ox's Magical dust, Horse's Scribe's pen). They are tracked in
`re/global_cross_reference.md`.

### 2. Chung ryong keys: three or four

The Dog alliance is the only one where a key count is contested. Atlas and Nussan both say **3**; RTK says
4. The other three totem-key alliances are 4 in all three sources. Two period witnesses beat one fan-server,
so it is **3** — a discount on the hardest cave in the game, which is at least a coherent design.

### 3. Karma: 1, 2–4, 3, or 4

Nussan's walkthrough itemises **"3 Karma points"**; his karma post rates the quest *"1-3 points"*; Atlas
says *"A Large Increase in Karma (2-4 Points)"*; RTK pays a flat 4. **3** is the only single number an
eyewitness gives and it sits inside every stated range. It lives in the CSV, so it is one edit to change.

### Prose is RTK's, verbatim, including where it understates the mechanic

Every line a mythic speaks is ported from `mythic_alliance_npc.lua` unchanged — they survive nowhere else.
That includes the confirmation before you accept, *"Starting this quest will reset your kills of these mobs
that you may have had prior"*, which is narrower than the truth: the **whole** track goes, not just that
cave's bosses (*"it resets your Kill Track to zero"*). The behaviour follows the archive and the wording
follows RTK, which is the same split this repo uses for the armour chains. It is deliberate — do not
reconcile the line to the mechanic.

## What is not built

**Greater alliances.** Ee San, six completed lesser alliances, three caves' worth of bosses at five apiece
and nothing else killed, a cap of three per character, and a legend that replaces the lesser mark. The word
is `greater`, and today no NPC answers to it — it falls through to ordinary chat. The eight-slot track it
needs is already here, which is most of the work. See
[Deferred-Work.md](Deferred-Work.md).
