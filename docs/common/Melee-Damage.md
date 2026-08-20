# NexusTK 4.95 — Player Melee Swing Damage

Live-measured against the real server, 2026-08-15/16. Supersedes the RTK-derived port in
`Server/Session.Items.cs` and `Server/Combat.cs`.

**Everything here is derived from observed damage numbers.** Character-sheet readings were used only as
corroboration and are NOT load-bearing anywhere — see [Sheet staleness](#sheet-staleness).

---

## 1. The formula

```
s          = random integer in [minSDam, maxSDam] summed over all equipment
swing      = (s / 2) * enchant  +  Dam * 2.5  +  mightTerm(Might)  +  classFactor(class, level)
swing     *= rage * invisible * critical            (5x stealth, 3x crit — unchanged from RTK)
damage     = max(1, floor(swing * (1 + targetAC / 100)))
damage    *= 2                                       if attacking from behind (Combat.IsBehindTarget)
damage     = max(1, floor(damage * reach))           reach 0.5 for backstab/flank reach tiles
```

`targetAC` clamps at −95 for a mob target, −80 for a player target (RTK `minimumArmor`).

### mightTerm — QUANTIZED

```
mightTerm(Might) = floor(Might / 4) / 2 - 1.0        (equivalently (floor(Might/4) - 2) / 2)
```

Steps **+0.5 for every 4 points of Might**, aligned to multiples of 4. It does NOT slope.

| Might | 0-3 | 4-7 | 8-11 | 12-15 | 16-19 | 20-23 | 24-27 | 28-31 | 32-35 | 36-39 | 40-43 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| mightTerm | −1.0 | −0.5 | 0.0 | 0.5 | 1.0 | 1.5 | 2.0 | 2.5 | 3.0 | 3.5 | 4.0 |

Step boundaries directly observed at Might **4, 16, 28, 32, 36, 40** — six, across three classes.

The decisive evidence is the **flat stretches**, which no continuous term can produce: a level-65 rogue
reads identical damage at Might 37, 38 and 39; a level-25 warrior reads identical damage with a sword at
Might 16 and 19.

Step **size** confirmed at 0.50 (not 0.33) by a fixed-level sweep on the level-65 rogue — at Might 39
with a military fork the observed collision set was `{81, 84, 86}`, chi2 3.41 vs 10.28 for the 0.33
alternative. The tell: a damage value seen exactly once where 0.33 requires it doubled.

The **−1.0 offset is measured, not chosen** — see [classFactor](#classfactor).

### classFactor — a STAIRCASE in 0.5s, level-driven

Peasant: **flat 0** — measured directly on a level 1-3 peasant, and this is what pins mightTerm's
−1.0 offset.

**Mage: cf = 0.5 at level 16, 1.0 at level 18** — APPLIED. The step at level 8 in `MageClassFactor` is
borrowed from the warrior and is a GUESS; levels 4-15, 17 and 19+ are unmeasured.
**The mage steps at the SAME levels as the warrior** — see
[Is classFactor class-independent?](#is-classfactor-class-independent).
**Poet: untested** — we ship 0 purely because RTK does. See [the mage anomaly](#the-mage-anomaly),
which also covers a separate live bug: mages get reduced weapon S.

**Warrior** (measured at 14 levels):

| Levels | classFactor |
|---|---|
| 1 – 7 | 0.0 |
| 8 – 16 | 0.5 |
| 18 – 32 | 1.0 |
| 35 – 38 | 1.5 |

Step positions: **#1 at exactly level 8** (levels 7 and 8 both measured, adjacent).
**#2 in levels 17-18.** **#3 in levels 33-35.** Gaps are 9-10 then 15-17 — they GROW, not uniform.
Levels 36, 37 and 38 also measure 1.5, so step #4 is at 39 or later. Nothing above 38 is measured.

**THE BEST INSTRUMENT FOR classFactor: UNARMED vs a SQUIRREL.** AC 100 is qualitatively different from
every other target, not merely better:

- deduction is **exactly ×2.00**, and every swing term is a multiple of 0.5, so `damage = 2 × swing`
  with **no flooring at all** — `damage = s + 5·Dam + 2·mightTerm + 2·cf`;
- each 0.5 of cf moves the window by **exactly 2**, so it can never degenerate at any level with any
  weapon (below AC 100 the shift is `0.5 × deduction` < 1, which is why the ×1.80 cat aliases adjacent
  cf values at some levels);
- unarmed is only 2 s-values, so ~20 swings saturates the window completely;
- squirrel AC 100 is on the confirmed list, and squirrels are free and harmless.

**One-line readout:  `cf = (lowValue − 1) / 2 − mightTerm(Might)`**

**Or just run `re/classfactor.py` — it does all of this and does not need me in the loop.**

```
python re/classfactor.py plan  --cls rogue --level 9 --might 7 --weapon "Wooden saber"
python re/classfactor.py solve --cls rogue --level 7 --might 6 --weapon "Wooden saber"     --values "8 10 5 6 5 10 6 8 9 5 6 9 8 10 7 6 9 8 5 5 10 8 9 6 6 6"
python re/classfactor.py scan
```

`plan` answers "does this setup SEPARATE the hypotheses" before you farm, and prints the swings needed
for both endpoints plus swings-per-kill. `solve` returns cf, every cf the window permits, chi2, and
warns when an endpoint is unobserved (i.e. the answer may still move). It knows the mob AC table, the
item S ranges, the warrior +2, the mage S penalty, and drops rear-attack ×2 outliers automatically.
`--stale` suppresses the warrior weapon bonus so historical warrior #2 runs can be re-solved.

Validated against known answers: rogue lvl7 → 0.5 (chi2 4.46/5df), warrior lvl7 `--stale` → 0.0
(chi2 3.26/5df). `plan` independently rediscovers the level-48 cat/viperhead collision (cf 2.5 vs 3.0)
that was found by hand.

Cost is 18 vita per squirrel, so ~10 kills a run. For a big HP pool instead, **Slush ogre** (Snow
Dungeon, level 20, AC 100, 4150 vita, hits 10-60) is the same ×2.00 with ~100 swings per kill — but its
AC is unverified, and an AC error aliases *exactly* with a cf error, so calibrate it at a level where cf
is already known before trusting it.

**Cat** (AC 80, 400 vita, hits 7-11) remains the best *weapon-bearing* dummy — same AC as deer so runs
are directly comparable — but check separation every time.

**Weapon choice: prefer 6 s-values over 11.** Sword of power (S 20-25) and wooden saber (S 5-10) both
have 6; viperhead and military fork have 11. At n=40 the chance of missing a window endpoint is 0.05%
versus 2.1%.

**CONFOUND: a mightTerm step and a cf step BOTH add exactly 1 to the low value.** Always record Might.
Best practice is two readings inside one mightTerm band (36-39, 40-43, 44-47, 48-51, 52-55) — within a
band any change is pure cf.

**Rogue**: ~1.0 at level ~15 (early, low precision), **1.0 at levels 18 and 19** (mined from
`re/auto/swings.csv` — see §5.1), **6.0 at level 65**. Bands at the adjacent pair:

| Level | Band | Instruments |
|---|---|---|
| 18 | `[0.87, 1.14)` | green squirrel + novice sword (n=80) ∩ big bat + swift sword (n=86) |
| 19 | `[0.87, 1.34)` | green squirrel + novice sword (n=76) |

**Those bands do NOT establish that the rogue is flat across 15-19.** They are ~0.3 wide, and a smooth
ramp of ~0.10/level fits both (1.10 at 18, 1.21 at 19) — which is exactly the slope needed to reach the
measured 6.0 at level 65. The rogue's step size has still never been observed.

It is **level-driven, not might-driven**: holding the level-65 rogue fixed and walking Might 35 → 42
moved damage by exactly the mightTerm step and nothing more.

<a name="the-mage-anomaly"></a>
### The mage anomaly — cf is 0.5, and mages get REDUCED WEAPON S

```
lvl16 MAGE  might9   (mightTerm 0.0, Dam 0)
  UNARMED       vs squirrel       (AC 100, x2.00)  n=13+   -> {2, 3}     -> cf 0.5
  UNARMED       vs green squirrel (AC  90, x1.90)          -> {1, 2}     -> cf 0.5
  wooden saber  vs squirrel       (AC 100)  n=13           -> {3, 4}     (3 x7, 4 x6)
  wooden saber  vs green squirrel (AC  90)                 -> {2, 3}

lvl18 MAGE  might10  (mightTerm 0.0 — SAME BAND as might 9, so no confound)
  UNARMED       vs green squirrel (AC  90)                 -> {2, 3}     -> cf 1.0  <-- STEP
  wooden saber  vs green squirrel (AC  90)                 -> {3, 4}
```

**Finding 1 — Mage classFactor is 0.5 at level 16 and 1.0 at level 18, not 0.** APPLIED to the code.
From the unarmed runs, where S = 1-2 is already established: `damage = s + 2*mightTerm + 2*cf`.
Squirrel gives `1 + 2cf = 2`; green squirrel independently gives `floor((0.5+cf)*1.9) = 1`. Both solve
to **0.5**, and no other value fits either. cf 1.0 predicts {3,4} / {2,3}; cf 0.0 predicts {1,2} / {1}.
At level 18 the same rig gives {2,3} on a green squirrel, which solves to **1.0** — and because Might 9
and Might 10 sit in the same 8-11 mightTerm band, that is a PURE cf step with nothing else moving.
So the mage's step #2 is in **levels 17-18**.

<a name="is-classfactor-class-independent"></a>
#### ~~Is classFactor class-independent?~~ NO — FALSIFIED AT LEVEL 7 (2026-08-17)

**The rogue is NOT on the warrior's ladder.**

| Level 7 | Might | mightTerm | window | cf |
|---|---|---|---|---|
| Warrior | 7 | −0.5 | **4-9** (n=31) | **0.0** |
| Rogue | 6 | −0.5 | **5-10** (n=26) | **0.5** |

Same level, same mightTerm, same weapon (wooden saber), same mob (squirrel ×2.00), and **both windows
fully saturated at both endpoints** — so neither is a missing-endpoint artifact. The rogue's first step
is at level **6 or 7**; the warrior's is pinned to exactly **8**.

**ROGUE STEP #1 IS PINNED TO EXACTLY LEVEL 7** (measured 2026-08-17): level 6 = 0.0 (n=28), level 7 =
0.5 (n=26), adjacent, both windows saturated. The rogue steps exactly **one level before** the warrior,
whose step #1 is at 8. Two classes with cleanly pinned first steps, one level apart.

**PROVENANCE CAVEAT — RAISED AND NOW RESOLVED.** Every warrior reading below level 14 came from
**warrior #2**, the character that ran fifteen levels on the [stale-stat bug](#stale-stat-bug). We had
proved the staleness hit base Dam; we had NOT proved it spared classFactor. If it had not, warrior #2's
0.0 at level 7 would have been a *peasant* reading and the divergence would have been an artifact.

**Settled 2026-08-17 with a fresh, relogged warrior:**

```
lvl6 WARRIOR (CLEAN) might6 Dam2 wooden saber squirrel n=23
  18 16 14 14 14 18 18 19 19 14 14 16 16 18 18 18 17 18 18 19 14 16 15
  -> window 14-19, both endpoints. chi2 10.13/5df (lumpy but n=23; no other deduction fits —
     x1.95 would give 13-18).  cf = 0.0

lvl7 WARRIOR (CLEAN) might7 Dam2 wooden saber squirrel n=15
  19 16 16 17 14 16 16 17 19 19 17 18 17 17 16
  -> window 14-19 (no 15 seen, P(miss)=6.5% at n=15 — irrelevant, the 14 is what matters).
     cf 0.5 floors at 15 and CANNOT produce a 14.  cf = 0.0
```

Both agree with warrior #2 at the same levels. **The stale-stat bug hit base Dam ONLY; classFactor was
never affected.** That retroactively validates every warrior #2 classFactor reading — including step #1
at exactly 8 and the values at 8, 9, 14, 15, 18, 19 — and the whole warrior ladder stands as measured.

These runs also re-confirm the **+2 warrior weapon bonus** independently: with Dam 0 the level-6 window
would be 9-14, and it measured 14-19, a full 5 raw above.

**THE FALSIFICATION IS FINAL.** Rogue step #1 is at exactly 7, warrior step #1 at exactly 8 — both
pinned on adjacent-level pairs with saturated windows, and the warrior half now confirmed on a clean
character. The classes do NOT share one ladder.

| Level | Warrior | Rogue |
|---|---|---|
| 5 | 0.0 | 0.0 |
| 6 | 0.0 (clean) | 0.0 |
| **7** | **0.0** (clean) | **0.5** |
| 8 | 0.5 | 0.5 |

**Consequence: the old "rogue ≈1.0 at level 15" reading is rehabilitated.** It was filed as suspect
*because* the shared ladder said 0.5 there. If the rogue's ladder simply runs earlier, it is consistent —
which makes levels 15/16 a high-value checkpoint rather than a formality.

**What the ladders look like now:**

| Class | measured |
|---|---|
| Warrior | 0.0 ≤7 · 0.5 @8-16 · 1.0 @18-32 · 1.5 @35-38 |
| Rogue | 0.0 @5 · **0.5 @7** · 1.0 @18,19 · 6.0 @65 |
| Mage | 0.5 @16 · 1.0 @18 |
| Peasant | 0.0 @1,2,3,5 |

They still agree at 18 — all three read 1.0 — so the divergence is a narrow early offset, not a
wholesale per-class ramp. That is itself odd and unexplained.

The original agreement that motivated the hypothesis is kept below for the record.

#### [SUPERSEDED] The three classes agree everywhere they overlap

| Class | cf @ 16 | cf @ 18 | step #2 window |
|---|---|---|---|
| Warrior | 0.5 | 1.0 | 17-18 |
| Rogue | — | 1.0 | — |
| Mage | 0.5 | 1.0 | 17-18 |

**Every class measured at level 18 reads 1.0, and the mage's step sits in the same 17-18 window as the
warrior's.** Nothing measured below level 20 distinguishes the classes at all. If that holds, the three
per-class tables collapse into ONE level ladder (steps at 8, 17-18, 33-35, …) and RTK's per-class
`_classFactors` are not a low-level mechanic at all.

**What breaks it: the level-65 rogue at 6.0.** A shared ladder predicts ~1.5-2.0 there. So the classes
must diverge *somewhere* between 19 and 65 — which is a very different model from "each class has its
own ramp from level 1".

**Leading hypothesis for the HIGH-LEVEL divergence: subpath or rank driven, not class driven.** A level-65 rogue has
a subpath (Il/Ee/Sam/Sa San); a level-18 one does not. That would explain the irregular step levels, the
identical low-level behaviour, and why RTK's constants look like endgame values. No rank/level ladder
exists in `game-data` to corroborate it — checked `PathGrowth.csv`, `PathHalls.csv`, `Paths.csv`.

**[SUPERSEDED — the level-7 pair above answered the class-independence question directly.
Still worth running for the SHAPE question: where does the rogue accelerate toward 6.0?]
THE TEST: a rogue at level 25-30, unarmed vs a squirrel.** At level 28 with Might 20 (mightTerm 1.5),
`low = 4 + 2*cf`:

| reading | cf | verdict |
|---|---|---|
| **6 / 7** | 1.0 | ladder is SHARED through 28 — classFactor is not class-specific at all below 30, and the divergence is late/subpath |
| **8 / 9** | 2.0 | rogue already ahead by 28 — genuine per-class ramp |
| **10 / 11** | 3.0 | rogue diverges early and steeply |

This is now the single highest-value measurement in the whole investigation — it decides the *shape of
the model*, not just one constant. The bot already logged a rogue at level 28, so the character exists.

**NOT YET ASSUMED IN CODE:** the per-class tables stay separate until this run happens.

**Finding 2 — a mage gets far less than a weapon's listed S range.** RULE SOLVED AND APPLIED:
effective S = `[floor((minS+5)/4), floor((maxS+5)/4)]`, rolled uniformly.
The wooden saber (S 5-10) behaves as an effective **S 2-3** on both mobs:

| target | full S 5-10 would give | observed | effective S |
|---|---|---|---|
| squirrel ×2.00, lvl16 | 5-10 (six values) | **3, 4** | 2-3 |
| green squirrel ×1.90, lvl16 | 5-10 | **2, 3** | 2-3 |
| green squirrel ×1.90, lvl18 | 6-11 | **3, 4** | 2-3 |
| squirrel ×2.00, lvl18 | 7-12 | **4, 5** | 2-3 |

Two values, not six, on two deductions and two levels — and the effective range stays 2-3 as the level
and cf move, so it is not scaling with either. No item in the game has a listed S of 2-3, so this is
derived, not an item lookup.

**This is a MAGE mechanic, not a gate on the item.** The wooden saber is `ItmPthId 0`, and both a
WARRIOR and a PEASANT get its full S 5-10 from it:

| Class | same wooden saber (`ItmPthId 0`) | evidence |
|---|---|---|
| Peasant | **full S 5-10** | lvl1 window 3-8 and lvl2 window 4-9, all six values both times |
| Warrior | **full S 5-10** | the level 5-9 runs fit 5-10 exactly |
| Mage | **effective S 2-3** | two mobs, two levels |

Three classes, one item, and only the mage is reduced. No item-side gate can explain that.

The lvl18 squirrel reading is the cleanest of the four: ×2.00 involves no flooring, so
`damage = s_eff + 2*mightTerm + 2*cf` is exact, and with cf pinned to 1.0 by the unarmed run on the same
character, `{4,5}` gives `s_eff ∈ {2,3}` with no rounding ambiguity. Effective S has not moved across
two levels, two deductions and two cf values.

Candidate rules, all still open:
- **flat 2-3 for any weapon a mage holds** (weapon S ignored entirely, small fixed bonus over unarmed);
- **`floor(S/4) + 1`** — maps 5,6,7→2 and 8,9,10→3, an even 3:3 split matching the observed 7:6;
- **`ceil(S/4)`** — same 2-3 range, but a 4:2 split; only the observed ratio separates it from the above;
- **off-path penalty only** — a mage might get full S from a mage-path weapon.

`round(S/4)` is excluded: it spans 1-3, which would put 3s in the lvl18 squirrel run, and none appeared.

**VIPERHEAD RESULT (lvl18, might 10, cf 1.0, regular squirrel ×2.00, `damage = s_eff + 2`):
observed {7, 8, 9} -> effective s ∈ {5, 6, 7}.** Viperhead is S 15-25.

This KILLS two of the four candidates outright:
- **flat 2-3 / weapon S ignored** — predicted {4,5}. DEAD.
- **full S (on-path exemption irrelevant here)** — predicted {17..27}. DEAD.

**Weapon S IS read by a mage; it is scaled by roughly a quarter.** 5-10 → 2-3, 15-25 → 5-7.

Three scaling rules survive, and all three give the wooden saber's {4,5} identically. They split only
on the viperhead, where the tell is WHETHER A 6 EVER APPEARS:

| rule | viperhead damage : frequency out of 11 |
|---|---|
| `floor(S/4)+1` | **6**:1 · 7:4 · 8:4 · 9:2 |
| `ceil(S/4)` | **6**:2 · 7:4 · 8:4 · 9:1 |
| `floor((S+5)/4)` | *no 6* · 7:4 · 8:4 · 9:3 |

**n=27 RESULT (lvl18, might 10, squirrel): {7:12, 8:7, 9:8} — NO 6 EVER OBSERVED.**

| rule | viperhead-only odds | P(no 6 in 27) | verdict |
|---|---|---|---|
| `floor((S+5)/4)` | best | 1.000 | LEADER |
| `floor(S/4)+1` | 26:1 against | 0.076 | disfavoured |
| `ceil(S/4)` | 6561:1 against | 0.004 | **DEAD** |

Two independent signals drive it: the absent 6, and the 9-count (8 observed where `ceil` predicts 2.5
and `floor(S/4)+1` predicts 4.9).

**BE HONEST ABOUT THE COMBINED FIT.** The lvl16 wooden-saber split was 7:6 (near-even), and
`floor((S+5)/4)` wants 1:2 there while `floor(S/4)+1` wants 1:1 — so the saber pulls the other way.
Combining both weapons the odds drop from 26:1 to roughly **8.5:1**. Leading, NOT settled. Do not
implement on this alone.

  [superseded] n=3 stage — was consistent with all three. The lvl16 wooden-saber split (7:6, even) mildly favours
`floor(S/4)+1` (predicts 3:3, chi2 0.08) over `floor((S+5)/4)` (predicts 2:4, chi2 2.46).

**RESOLVED 2026-08-17 — STAFF OF POWER, n=23: {7:13, 8:10}. No 6, plenty of 8s.**

```
lvl18 MAGE  might10  Staff of Power (S 15-20, ItmPthId 3)  squirrel (AC 100)  n=23
  8 7 8 7 7 7 7 8 7 7 7 8 7 7 8 8 8 8 8 7 7 8 7      -> {7, 8} only -> s_eff {5, 6}
```

**THE RULE:  effective S range = [floor((minS+5)/4), floor((maxS+5)/4)], rolled uniformly.**
APPLIED — `WeaponTotals()` in `Server/Session.Items.cs`.

| weapon | listed S | measured effective S |
|---|---|---|
| Wooden saber | 5-10 | **2-3** |
| Viperhead woodsaber | 15-25 | **5-7** |
| Staff of Power | 15-20 | **5-6** |

- `ceil(S/4)` is **impossible** — it cannot produce an 8 at all, and the staff run has ten of them.
- `floor(S/4)+1` maps 15→4 where the data needs 5; beaten **8700:1** across all three weapons.
- **NO ON-PATH EXEMPTION.** The Staff of Power is `ItmPthId 3` — the mage's OWN path weapon — and is
  scaled identically. This is a flat property of the class, not an off-path penalty.

**ENDPOINT-TRANSFORM, NOT PER-ROLL.** Both variants give the same three ranges, but they differ in the
distribution inside the range. Transforming the endpoints and rolling uniformly fits better on all three
weapons (total LL −54.62 vs −55.99, ~4:1) and is the more plausible implementation:

| weapon | endpoint-then-uniform chi2 | per-roll chi2 |
|---|---|---|
| wooden 5-10 | **0.08** | 2.46 |
| viper 15-25 | 1.56 | 1.35 |
| staff 15-20 | **0.39** | 1.07 |

Only the S range is affected — the item's Dam/Might lines are untouched, and an unarmed mage keeps the
normal 1-2 (no weapon, so no weapon transform; note `floor((1+5)/4) = 1` would have made unarmed
deterministic, and it is not).

**POET IS UNTESTED and deliberately excluded from the code.** Do not extend it without measuring.

  [superseded plan] TO RESOLVE: the STAFF OF POWER (S 15-20, `ItmPthId 3`).** It is a sharper instrument than the
viperhead — the discriminating roll is 1-in-6 there instead of 1-in-11 — and all four hypotheses give
completely distinct signatures at lvl18 / might 10 / squirrel:

| rule | predicted damage |
|---|---|
| `floor((S+5)/4)` | **7, 8** only, ratio 2:1 |
| `floor(S/4)+1` | **6, 7, 8**, ratio 1:4:1 |
| `ceil(S/4)` | **6, 7** only |
| full S (on-path exemption) | **17-22** |

~25 swings separates them, AND it answers the still-open on-path question in the same run.

**All four lvl18 readings cross-check.** cf 1.0 with mightTerm 0.0 predicts, and observation gives:
unarmed green squirrel {2,3} ✓ / unarmed squirrel {3,4} ✓ / wooden saber green squirrel {3,4} ✓ /
wooden saber squirrel {4,5} ✓. Two mobs, two weapons, one cf — the mob labels are sound.

**SUPERSEDED NEXT-TEST TABLE (kept for the Staff of Power line, which is still open):** Predictions at lvl18 / might 10 / cf 1.0 vs a squirrel, where
`damage = s_eff + 2`:

| weapon | flat 2-3 | floor(S/4)+1 | full S (on-path exemption) |
|---|---|---|---|
| **Viperhead woodsaber** S 15-25, `ItmPthId 0` | 4, 5 | 6-9 | 17-27 |
| **Staff of Power** S 15-20, `ItmPthId 3` (mage) | 4, 5 | 6-8 | 17-22 |

Viperhead is the easier of the two — `ItmPthId 0`, same as the wooden saber a mage clearly can hold —
and it separates "weapon S is ignored" from "weapon S is scaled" on its own. Staff of Power then answers
whether an ON-PATH weapon is exempt. Run both if possible; they answer different questions.

**THE DECISIVE TEST: Staff of Power** (`ItmPthId 3`, mage path, level 5 requirement, **S 15-20**) on the
same level-16 mage vs a squirrel. The three rules diverge completely:

| observed | rule |
|---|---|
| **16-21** (six values) | mages get FULL S from on-path weapons — this is an off-path penalty |
| **5, 6, 7** | `floor(S/4)+1` — a universal scaling of weapon S |
| **3, 4** | flat 2-3 — weapon S is ignored outright |

**FIXED.** Before this, we gave mages the full weapon S range — a level-16 mage with a wooden saber
swung 6-11 on our server where live gives 3-4, roughly 2-3× too hard.

**POET is completely untested** on both counts — neither its classFactor nor its weapon-S behaviour.
Do not assume it matches the mage.

### Dam

```
Dam = (sum of equipment Dam)
    + (warrior && any weapon equipped ? +2 : 0)                  warrior weapon bonus
Dam = max(0, Dam)                                                floor at zero
```

Base Dam is **0 for every class, at every level.** The one warrior term is **absent from RTK's
`swingDamage.lua` entirely.**

- The **+2 on equip** applies at every level, on top of the item's own Dam line, from the moment the
  character joins the Warrior path.
- It lives on the **character**, not the item. The sword of power's tooltip is `+1 might / +1 hit /
  +10 vita` and no Dam — matching its Items.csv row — yet a warrior wearing it reads character Dam 2.
- Pinned by one character at level 20 swapping weapons: military fork item Dam +1 → character Dam 3;
  viperhead woodsaber item Dam +0 → character Dam 2. Item Dam + 2 both times.
- The **max(0, Dam) clamp** is kept as a defensive floor. RTK has `math.max(player.dam, 1)`; the floor
  is at **0**, not 1 (a level-1 peasant with a 0-dam weapon contributes 0). Nothing in the live data
  now reaches it.

#### There is NO level-based Dam flip — and why we thought there was

A `WarriorDamFlipLevel` (base −2 → 0, bracketed 20-28) lived in the code for a day. **It does not
exist.** It was an artifact of splicing two different characters at the seam between them:

| Character | Levels supplied |
|---|---|
| Warrior #1 | 15 – 35 |
| Warrior #2 | 1 – 19 (releveled later) |

Both were measured at **level 15 with the same weapon**, ten damage apart:

| Character | Level | Weapon | Target | Observed | Dam 0 predicts | Dam 2 predicts |
|---|---|---|---|---|---|---|
| Warrior #2 | 15 | sword of power | green squirrel (n=45) | **21-26** | 21-26 ✓ | 31-36 ✗ |
| Warrior #1 | 15 | sword of power | wolf | **23-27** | 16-20 ✗ | 23-27 ✓ |

No level rule can produce that. The character boundary was being read as a level boundary, which is
also why the bracket kept moving (11 → 15 → 16 → 23).

<a name="stale-stat-bug"></a>
#### The live-server stale-stat bug

Warrior #2 was not following a different rule — it was running on a **real bug in the live 4.95
server**. Base Dam and base AC are **stored character stats that only get recomputed when the character
loads.** A character who joins the Warrior path mid-session keeps the peasant-era values until they log
out and back in.

**The server swings with the stale value.** This is not merely a display fault. Warrior #2's sheet read
base −2 / equipped 0 for fifteen levels *and its damage agreed with the sheet the whole time.*

Two consequences:

1. **We do not reproduce this bug.** Our server applies the correct 0 / +2 immediately.
2. **Warrior #2's runs are still valid evidence for mightTerm and classFactor.** Dam genuinely *was* 0
   throughout them, which is exactly what those fits assumed. Only the Dam conclusion drawn from them
   was wrong. The classFactor staircase is untouched.

This also explains the level-20 run previously filed as "contaminated": the relog happened around
there, so Dam changed mid-series.

### Unarmed weapon range

**`S = 1-2`.** Bare-handed is NOT zero — a zero range is deterministic, and every unarmed sample shows
two adjacent damage values in a ~50/50 split. Endpoints pinned by cross-referencing an armed run on the
same character (military fork S 90-100 gave a window of 113-123, fixing the non-weapon term to
[9.00, 9.05), which forces the unarmed low roll to contribute 0.5).

### Armor / deduction

`damage = max(1, floor(raw * (1 + ac/100)))` — **one** floor, applied at the end. Never truncate the raw
swing to int first; double-flooring is disproven (n=178).

Confirmed on 11/11 spell readings spanning AC −22 to +100, and on every melee window since.

---

## 2. Confirmed mob AC values

Each is *uniquely* pinned — no neighbouring integer works.

| Mob | AC | Deduction |
|---|---|---|
| Squirrel | 100 | ×2.00 |
| Green squirrel | 90 | ×1.90 |
| Deer / Doe | 80 | ×1.80 |
| Cat | 80 | ×1.80 |
| Wolf | 45 | ×1.45 |
| Fox | 40 | ×1.40 |
| Dark fox | 20 | ×1.20 |

All match `game-data/mobs.csv` `MobArmor` as recorded.

## 3. Confirmed weapon S ranges

| Item | S range | Item Dam | Source |
|---|---|---|---|
| Wooden saber | 5-10 | 0 | Items.csv, confirmed by fit |
| Viperhead woodsaber | 15-25 | 0 | client tooltip "15m25" + 67 swings |
| Sword of power | 20-25 | 0 | Items.csv, confirmed by fit |
| Military fork | 90-100 | 1 | 31 swings, all 11 consecutive values |
| Steel dagger | 35-70 | 0 | client tooltip, matches Items.csv |

Note: **Items.csv S ranges have proven reliable.** An early claim that `sword_of_power` had wrong Dam
and S was WRONG — the client's "+2 dam" was the warrior weapon bonus, not the item. The only confirmed
bad row found is **Metal orb's level gate** (Items.csv says 10, live requires 50).

---

## 4. Methodology — how to measure this

### The two readings

Every run produces a **window** (min and max damage) and a **collision pattern** (which value(s) appear
roughly twice as often, because two `s` rolls floor to the same damage).

- **Window** — robust, needs ~40 swings to see both endpoints reliably. Use when candidate hypotheses
  produce different min/max.
- **Collision position** — much finer (±0.03), but needs the doubled value to pull clear of ~9 singles,
  which takes n ≈ 100-150. Use only when the window can't separate.

### Pick the mob by whether it SEPARATES, not by precision

This cost two wasted farming sessions. Deer has the tightest signature (±0.028) but at level 30 both
candidate classFactors produced the identical 28-37 window there, so the answer hid in a collision that
tied three ways across 91 swings. Fox shifted the *window* between the same two candidates and settled it
in 33 swings.

**Always check first whether the candidate hypotheses give different windows on your chosen mob.** The
discriminating mob changes with `M` — fox worked at level 30 and was useless at level 35.

### Deduction matters

- **×2.00 (squirrel)** maps `s` one-to-one — six `s` values give six consecutive damage values, no
  collisions. Only reads the window. Best when you want an exact, rounding-free read (`dmg = s + 2M`).
- **×1.80, ×1.90, ×1.45, ×1.40** collapse some rolls, producing collisions — a free second check.

### Low-level characters are better instruments

A level-1 peasant has **one** unknown (mightTerm). A level-65 rogue has three, with fitted constants that
quietly absorb errors. The peasant found the −1.0 offset bug that thousands of high-level swings had
hidden; the low-level warrior found the base-Dam mechanic.

### Sample sizes

- Window only, 6-value weapon: ~25-40 swings
- Window only, 11-value weapon: ~40 swings (98% chance of seeing both endpoints)
- Collision identification: ~100-150 swings
- A wide S range (steel dagger, 36 values) is **worse**, not better — 32% chance of missing an endpoint
  at n=40, and collisions become undetectable.

### Sheet staleness — it is a SERVER bug, not a display bug

**The live server does not refresh base AC or base Dam until you sign out and back in.** Base Dam and
base AC are stored character stats, recomputed only on character load. Join the Warrior path
mid-session and you keep the peasant-era values.

Critically, **the server swings with the stale value** — this is not merely a cosmetic UI fault. A
warrior can spend fifteen levels dealing damage as though they were still a peasant, with the sheet and
the damage agreeing with each other the whole time. That is what happened to warrior #2 and it is what
produced the phantom `WarriorDamFlipLevel`.

Practical rules:

- **Relog after any class change, and after anything that should alter a base stat**, before recording
  a single swing.
- **Note which character every run came from.** Two characters at the same level are not
  interchangeable data points — that assumption is exactly what created the phantom flip.
- Deriving from damage instead of the sheet does **not** save you here, because both are wrong
  together. Only a relog does.

---

## 5. Raw measurement log

Format: level, might, weapon, target → observed values → derived window.

### Peasant (classFactor = 0 throughout — the control)

```
lvl1  might3  wooden saber  squirrel  n=25
  6 4 6 7 8 6 8 7 7 3 4 6 8 4 4 6 6 7 3 4 6 6 3 3 3
  -> window 3-8.  dmg = s + 2M exactly, so 2M = -2, M = -1.00. PINS THE OFFSET.

lvl2  might4  wooden saber  squirrel  n=26
  5 6 6 4 9 8 7 6 9 6 7 9 8 8 5 7 4 9 10 9 4 9 6 8 6 8
  -> window 4-9, plus ONE stray 10 (rear-attack x2 of a 5). confirms step at Might 4.

REPLICATION 2026-08-17, fresh characters, same setup — model confirmed at both levels, plus the
first UNARMED readings at these levels:
  lvl1 might3 (mightTerm -1.0)  wooden saber  5 6 6 4 3 8 7                     -> window 3-8, all 6
                                UNARMED       only 1 damage
  lvl2 might4 (mightTerm -0.5)  wooden saber  4 5 7 8 6 4 9 7 4 4 9 6 8 6 5 9 5 8 5 7 8
                                              -> window 4-9, counts 4:4 5:4 6:3 7:3 8:4 9:3
                                UNARMED       only 1 damage
  The lvl2 UNARMED run is the tighter constraint: cf 0 predicts {1}, cf 0.5 predicts {1,2}. No 2s
  seen, so cf < 0.5 at level 2 independently of the saber runs. (lvl1 unarmed cannot separate 0 from
  0.5 — both floor to {1} — it only excludes cf >= 1.0.)
  The 3-8 -> 4-9 shift between the two levels is the mightTerm step at Might 4, seen again.
  A later attempt to recall lvl2 as Might 3 is WRONG and the damage says so: Might 3 (mightTerm -1.0)
  predicts 3-8, which is what LEVEL 1 gave. The only way to reach 4-9 at Might 3 is cf 0.5, and that
  dies on level 3 (Might 4, window 4-9, needs cf 0) because classFactor cannot decrease.

  lvl2 REPLICATION #2  might4  wooden saber  n=20 -> 4 4 4 6 5 6 4 4 5 4 6 7 4 9 5 7 7 8 7 9
                                                    window 4-9 again, chi2 6.41/5df
  lvl3 REPLICATION     might4  wooden saber  n=19 -> 5 3 4 8 8 7 7 6 9 6 7 4 4 5 9 5 7 9 9
                                                    window 4-9 (chi2 1.33/5df excluding the stray),
                                                    PLUS ONE STRAY 3 that this setup cannot produce.
    A 6-value weapon at x2.00 gives exactly 6 consecutive damage values; there is no roll yielding 3.
    Likely a swing at a different mob (green squirrel or deer both floor the low roll to exactly 3:
    floor(2.0*1.9) = 3) or a reach/flank tile at x0.5.
    WOODEN SABER S 5-10 CONFIRMED, not 4-10: S 4-10 would give 3-9 at this mightTerm, and the two lvl2
    runs total n=41 with no 3 at all — probability 0.0018 under S 4-10.

lvl3  might4  wooden saber  squirrel  n=24
  4 7 4 4 5 7 7 6 7 7 7 5 8 5 8 9 7 6 7 6 9 4 7 6
  -> window 4-9. same Might as lvl2, so mightTerm identical -> peasant cf stays 0 as level rises.
```

### Warrior

**TWO DIFFERENT CHARACTERS.** Levels 1-19 are warrior #2; levels 15-35 are warrior #1. They overlap at
level 15 and disagree there — see [the Dam flip section](#there-is-no-level-based-dam-flip--and-why-we-thought-there-was).
**Warrior #2 ran the whole series with stale peasant Dam (effective Dam 0 even with a weapon), because
of the [live stale-stat bug](#stale-stat-bug).** That does not invalidate its mightTerm/classFactor
readings — Dam really was 0 — but every "Dam" note below is what was *observed*, not what a correctly
loaded warrior would have.

```
=== WARRIOR #2 (levels 1-19, stale Dam throughout) ===

lvl5  might5  wooden saber  squirrel  n=29
  4 8 6 8 8 7 4 7 8 4 6 8 9 7 4 6 7 6 4 9 4 8 8 6 7 5 4 4 4
  -> window 4-9.  cf = 0.0, effective Dam 0 (stale: no weapon bonus applied)

lvl6  might6  wooden saber  squirrel  n=36
  9 9 4 4 6 9 8 6 9 8 9 8 7 7 8 8 10 7 5 8 4 8 4 8 9 7 7 5 9 7 7 4 4 9 7 9
  -> window 4-9, plus one stray 10 (rear attack).  cf = 0.0

lvl7  might7  wooden saber  squirrel  n=31
  9 9 5 8 5 4 8 7 9 9 6 7 7 8 8 8 4 4 8 4 8 9 7 6 9 7 5 5 4 5 9
  -> window 4-9, chi2 3.26/5df.  cf = 0.0   <-- LAST LEVEL AT 0.0

lvl8  might8  wooden saber  squirrel  n=24
  10 8 6 10 10 10 11 11 8 6 11 9 6 7 6 6 9 6 8 8 6 8 9 8
  -> window 6-11.  cf = 0.5   <-- STEP #1, EXACTLY LEVEL 8

lvl9  might9  wooden saber  deer  n=39
  6 9 8 8 8 5 6 9 9 6 9 9 6 9 5 5 7 9 8 7 8 9 5 6 8 7 9 6 9 8 8 5 6 7 6 6 9 5 8
  -> window 5-9, collision at 9, chi2 3.23/4df.  cf = 0.5

lvl14 might15 sword of power  green squirrel  n=32
  24 24 24 20 22 21 25 24 21 20 25 23 24 22 22 25 25 20 24 22 23 21 21 25 23 20 20 23 25 23 23 24
  -> window 20-25, chi2 1.38/5df (best fit of the series).  cf = 0.5, effective Dam 0 (stale)

lvl15 might16 sword of power  green squirrel  n=45
  21 21 26 22 24 22 21 25 23 22 23 21 23 26 22 21 26 23 22 22 24 23 24 21 21 25 22 24 24 24 22 24
  23 26 23 22 23 26 21 24 25 26 26 23 25
  -> window 21-26, chi2 2.33/5df.  cf = 0.5, effective Dam 0 (stale)
     COMPARE warrior #1 at the same level with the same weapon, below — it needs Dam 2.

lvl18 might19 sword of power  green squirrel  n=38
  24 24 26 27 25 25 25 22 27 22 22 27 25 25 24 25 23 23 26 26 24 27 24 27 26 26 24 26 24 24 22 23
  27 23 26 26 26 23
  -> window 22-27, chi2 2.74/5df.  cf = 1.0   <-- STEP #2 HAS HAPPENED BY 18. Dam still 0.

lvl19 might20 sword of power  green squirrel  n=70 (two runs merged)
  27 24 24 24 25 25 25 25 25 23 24 26 27 26 24 27 23 25 27 28 27 24 24 26 26 26 25 26 25 25
  24 27 25 28 27 23 23 28 24 27 28 25 24 26 25 23 25 25 26 26 24 25 27 26 23 23 24 23 25 24 27 28
  23 25 27 26 24 24 24 28
  -> window 23-28, chi2 7.49/5df.  cf = 1.0, effective Dam still 0 (relog happened around lvl20)


=== WARRIOR #1 (levels 15-35, correctly loaded: weapon bonus active) ===

lvl15 might15 UNARMED   squirrel 3/4,  deer 2/3,  wolf 2
lvl15 might18 UNARMED   squirrel 4/5,  deer 3/4,  fox 2/3        (Valor +3)
  -> both need Dam 0 — correct: no weapon equipped, so no weapon bonus. The +1 might between the
     unarmed (15) and armed (16) runs is the sword of power's own ItmMight 1.

lvl15 might16 sword of power   squirrel 33/36,  deer 30/34,  wolf 23/27   (ranges only)
  -> needs Dam 2. Wolf is the tightest: Dam 2 predicts EXACTLY 23-27, Dam 0 predicts 16-20.
     THIS IS THE READING THAT KILLED THE FLIP — warrior #2 at the SAME level with the SAME
     weapon reads 21-26 on a green squirrel, which needs Dam 0.

lvl16 might17 viperhead  deer  n=67 (two runs merged)
  31 32 31 27 27 33 27 28 27 26 28 29 32 34 30 32 31 26 27 27 25 33 27 27 34 30 26 34 28 33 27 31
  32 31 26 27 30
  26 31 31 29 32 34 28 30 32 29 26 34 29 29 32 30 31 33 27 33 34 34 34 29 28 32 31 29 29 30
  -> window 25-34, collision 27, chi2 7.95/9df.  cf = 0.5, Dam 2 (viperhead ItmDam 0 + weapon bonus)

lvl20 might20  CLIENT STAT READOUT, two weapons back to back — pins the +2:
  military fork        item Dam +1  ->  character Dam 3
  viperhead woodsaber  item Dam +0  ->  character Dam 2
  Items.csv agrees on both item lines. Character total = item Dam + 2 in both cases.
  (The damage runs from this level are CONTAMINATED — gear changed mid-series — but the stat
   readout above is clean and is the single best piece of evidence for the weapon bonus.)

lvl25 might25 viperhead  fox  n=60
  26 22 28 24 27 24 28 24 25 27 24 21 23 23 22 28 28 28 23 28 28 21 21 24 25 28 25 26 25 23 22 24
  26 24 23 23 23 23 21 28 23 27 26 21 21 28 22 23 21 27 27 25 25 27 28 22 23 28 25 27
  -> window 21-28, collision {23,25,28}, chi2 3.25/7df.  cf = 1.0

lvl25 might25 viperhead  wolf  n=29
  22 25 23 23 29 29 27 29 29 24 27 26 27 28 28 25 23 27 28 29 22 28 24 22 26 29 22 29 28
  -> window 22-29. agrees with the fox run.

lvl25 might28 viperhead  fox  n=31   (Valor +3)
  22 26 22 22 28 28 29 23 28 25 25 29 27 23 22 24 25 23 22 24 22 25 23 28 28 22 23 29 26 27 27
  -> window 22-29. confirms the mightTerm step at Might 28.

lvl28 might28 viperhead  deer  n=30
  37 35 35 30 34 35 36 37 36 36 31 34 32 37 36 30 29 32 34 36 36 36 28 36 31 35 30 34 29 29
  -> window 28-37, collision 36, chi2 6.67/9df.  cf = 1.0, Dam 2

lvl28 might28 military fork  deer  n=21
  101 109 107 105 105 106 108 108 108 109 108 105 103 108 102 107 105 100 103 104 101
  -> window 100-109, collision 108, chi2 4.40/9df. IDENTICAL band to the viperhead run.

lvl30 might30 viperhead + (+2 Dam gear, sheet Dam 4)  deer  n=59 (two runs merged)
  38 46 45 38 45 46 38 45 45 40 42 38 38 37 41 41 37 39 40 44 39 43 40 38 46 45 43 39
  40 39 44 41 43 46 46 41 40 39 39 38 44 39 38 38 41 37 44 43 42 43 40 40 42 38 45 45 40 42 37
  -> window 37-46. collision ambiguous (38 got 10 hits).

lvl30 might30 viperhead (Dam 2)  deer  n=32
  33 30 28 31 32 30 30 29 36 34 30 33 32 31 34 28 33 28 33 32 32 32 33 30 30 37 32 34 36 30 35 33
  -> window 28-37. collision ambiguous three ways (30:7, 32:6, 33:6).

lvl30 might30 viperhead  fox  n=33
  25 25 22 23 25 22 28 23 25 29 27 22 24 23 27 29 28 24 24 24 23 24 23 27 23 28 22 22 28 27 29 22 29
  -> window 22-29.  cf = 1.0 (the fox WINDOW separated what deer collisions could not)

lvl32 might32 viperhead  fox  n=37
  25 23 23 25 25 30 26 26 23 23 29 28 28 23 26 24 25 27 23 23 25 23 26 30 25 28 23 24 23 28 30 23
  24 25 28 23 23
  -> window 23-30.  cf = 1.0. confirms mightTerm step at Might 32.
  NOTE 23 got 13 hits where a double expects 6.7 — that is SIGNAL, it is only explicable if 23
  is doubled, which requires cf = 1.0 and rules out an earlier step.

lvl35 might35 viperhead  deer  n=47
  30 37 33 39 30 36 36 33 36 31 39 30 32 33 36 31 35 33 31 39 36 37 39 39 37 36 33 36 36 33 35 30
  36 33 33 35 39 38 30 30 34 38 35 34 36 39 39
  -> window 30-39, collision 36, chi2 13.15/9df.  cf = 1.5   <-- STEP #3 by level 35
     BOTH endpoints observed at n=47, so this one is firm: cf 2.0 needs a 31 floor, cf 1.0 caps at 38.

lvl36 might36 viperhead  CAT (AC 80, 400 vita)  n=27
  34 38 37 36 32 39 36 34 36 31 32 34 34 35 38 33 32 39 39 33 32 39 33 37 34 35 33
  -> window 31-39 observed (31:1 32:4 33:4 34:5 35:2 36:3 37:2 38:2 39:4).  cf = 1.5, NO STEP 35->36.
     Predicted 31-40 with 36 doubled; the 40 is a single-roll endpoint (s=25 only) and n=27 gives
     ~8% odds of missing any one value, so its absence is noise.
     Also pins Dam = 2 exactly: Dam 0 predicts 22-31, Dam 3 predicts 36-45. Warrior bonus confirmed
     live on a relogged character.
     HONESTY NOTE: 31-39 alone is EQUALLY consistent with cf 1.0 (window 30-39, missing the 30) —
     chi2 is identical, 9.87/9df either way. What kills cf 1.0 is the firm lvl35 = 1.5 plus
     monotonicity, not this run. cf 2.0 IS excluded outright: it cannot floor to 31.
     Cat is a better dummy than deer at this level: same AC 80, 400 vita vs 300, hits for only 7-11.

lvl37 might37 UNARMED  squirrel (AC 100)  n=22
  11 12 11 12 12 11 12 11 12 12 12 12 12 11 12 12 11 12 12 12 12 11
  -> window EXACTLY {11, 12} (11 x7, 12 x15).  cf = 1.5   <-- CLEANEST READING IN THE WHOLE LOG
     Both endpoints observed, nothing inferred. AC 100 -> x2.00 exactly, and every swing term is a
     multiple of 0.5, so damage = 2*swing with NO flooring at all: damage = s + 2*mightTerm + 2*cf.
     Independent of the lvl36 cat run in every way (verified AC, no weapon, no warrior Dam bonus).
     Also re-confirms unarmed S = 1-2: S 1-3 would produce 13s, S 0-2 would produce 10s; 22 swings
     with neither excludes both at p < 1e-4.
     The 7/15 split leans high (chi2 2.91/1df, p=0.09) — not significant, but if another unarmed run
     skews the same way, question whether s is uniform.

lvl38 might38 UNARMED  squirrel (AC 100)  n=15
  11 11 12 11 11 12 12 12 12 11 12 12 12 11 12
  -> window EXACTLY {11, 12} (11 x6, 12 x9).  cf = 1.5, NO STEP 37->38.
     PURE cf CHECK: might 38 is in the same mightTerm band (36-39) as the lvl37 run, so the two are
     directly comparable with no mightTerm confound. n=15 is ample for a 2-value window —
     P(missing a value) = 2^-15.

  S-UNIFORMITY WATCH: the two unarmed runs combined are 13 low / 24 high (chi2 3.27/1df, p=0.07).
  Leans toward the high roll but still not significant. Alternatives already excluded: S 1-3 and
  S 0-2 (would produce 13s / 10s, none seen in 37 swings); a CONTINUOUS s over [1,2] (would produce
  almost ALL lows — the opposite skew). Keep watching; three runs at p<0.05 would mean s is not
  uniform over {1,2}.
```

### Rogue — fresh character, 2026-08-17 series (shared-ladder test)

```
PEASANT BASELINE on the same character line:
lvl5 PEASANT might5 wooden saber squirrel n=21
  6 7 6 9 9 6 5 5 8 7 4 8 4 7 7 6 9 9 6 6 5   -> window 4-9, chi2 3.29/5df.  cf = 0

lvl5 ROGUE   might5 wooden saber squirrel n=24 (18 + 6 more)
  6 6 7 6 5 4 5 6 6 6 7 4 4 7 8 5 6 7  +  8 9 4 6 8 9
  -> window 4-9, BOTH ENDPOINTS OBSERVED. counts 4:4 5:3 6:8 7:4 8:3 9:2, chi2 5.5/5df.  cf = 0
  FULLY PINNED: the 4 excludes cf 0.5 (floors at 5); the 9 excludes cf -0.5 (caps at 8).
  Matches warrior cf 0.0 at levels 5-7 -> first shared-ladder point on this character.
  (At n=18 this read 4-8 and the two hypotheses were indistinguishable at chi2 10.0/5df either way.
   Six more swings produced the 9 and settled it — a concrete illustration of the sample-size note
   below: a missing endpoint is not a small error here, it is a different answer.)
```

```
lvl6 ROGUE might6 wooden saber squirrel n=28
  8 7 7 6 5 7 9 5 4 8 8 8 4 4 9 5 8 9 5 4 4 5 7 7 6 4 5 6
  -> window 4-9, both endpoints. counts 4:6 5:6 6:3 7:5 8:5 9:3, chi2 2.00/5df.  cf = 0.0
  <-- WITH lvl7 BELOW, THIS PINS ROGUE STEP #1 TO EXACTLY LEVEL 7. Adjacent levels, same Might,
      same mightTerm band, both windows saturated. Not a bracket.

lvl7 ROGUE might6 wooden saber squirrel n=26
  8 10 5 6 5 10 6 8 9 5 6 9 8 10 7 6 9 8 5 5 10 8 9 6 6 6
  -> window 5-10, both endpoints. counts 5:5 6:7 7:1 8:5 9:4 10:4, chi2 4.47/5df.  cf = 0.5
  WARRIOR AT THE SAME LEVEL AND SAME mightTerm READS 4-9 / cf 0.0 -> see the falsification above.

lvl8 ROGUE might7 wooden saber squirrel n=18
  9 10 7 8 7 5 5 8 8 9 8 7 9 8 10 6 5 10
  -> window 5-10, both endpoints. counts 5:3 6:1 7:3 8:5 9:3 10:3, chi2 2.67/5df.  cf = 0.5
  NO STEP AT 8. Might 7 is the same mightTerm band (-0.5) as the lvl7 run, so the two are directly
  comparable with no confound.
```

**THE DIVERGENCE IS ONE LEVEL WIDE.**

| Level | Warrior | Rogue |
|---|---|---|
| 5 | 0.0 | 0.0 |
| **7** | **0.0** | **0.5** ← the only disagreement anywhere |
| 8 | 0.5 | 0.5 |
| 18 | 1.0 | 1.0 |

The ladders agree at every level either class has been measured at EXCEPT level 7. The rogue's step #1
is at 6-7, the warrior's at 8 — a one-to-two level offset, and both are back in lockstep by 8.

**So the whole "classes diverge" conclusion rests on ONE data point: warrior #2's level-7 run** — from
the character that spent fifteen levels on the stale-stat bug. If that reading is tainted and a clean
warrior reads 0.5 at level 7, the two classes are identical everywhere either has ever been measured
and the shared ladder is fully restored. **The fresh-warrior-at-7 run decides it.**

**SAMPLE SIZE WARNING for levels 7 and 8.** n=18 leaves a 7.5% chance of missing an endpoint, and at
the step-detection levels an endpoint miss FLIPS THE ANSWER rather than just adding noise: cf 0 gives
4-9, and missing the 4 leaves 5-9, which reads as cf 0.5 (5-10) with a missing top. Use **n≈35** there
(endpoint-miss risk 0.3%). 18-20 is fine everywhere else.

### Rogue (level 65 throughout)

```
UNARMED, fixed level, walking Might — this is what proved classFactor is level-driven and
mightTerm is quantized:
  might 35  squirrel 19/20   deer 17/18   fox 13/14   wolf 13/14  (wolf later reported 13-15)
  might 37  squirrel 20/21   deer 18      fox 14      wolf 14/15
  might 38                                fox 14      wolf 14/15
  might 39  squirrel 20/21   deer 18      fox 14/15   wolf 14/15
  might 40                                fox 14/15   wolf 15
  might 42  squirrel 21/22   deer 18      fox 14/15   wolf 15
  -> FLAT across 37/38/39 = quantized. steps at Might 36 and 40.

might35 military fork  squirrel  n=31 (two runs merged)
  116 118 120 114 117 123 116 122 115 121 118 123 116 116 116 117 122 122 123 120
  119 119 121 113 117 119 114 114 120 118 116
  -> window 113-123, ALL 11 consecutive values, no gaps. confirms fork S 90-100 and the s/2 grid.

might35 military fork  deer  n=29
  101 103 106 108 104 110 102 108 107 103 103 104 106 110 104 101 108 108 105 105 103 108 108 105
  101 105 110 102 110
  -> window 101-110, collision 108 (6 hits). pins M(35) to [9.00, 9.05).

might35 military fork  wolf  n=12
  87 86 81 81 82 89 88 84 89 89 86 89        -> window 81-89

might35 military fork  fox  n=4
  84 84 82 81

might39 military fork  fox  n=32
  84 84 81 82 79 82 80 86 85 86 83 79 84 81 84 79 82 86 80 80 81 81 79 86 81 83 86 79 80 84 81 83
  -> window 79-86, collision {81,84,86}, chi2 3.41/7df vs 10.28 for the alternative.
     THIS IS WHAT PINNED THE STEP SIZE AT 0.50.
```

### 5.1 Mined from `re/auto/swings.csv` (bot log, 16,029 swings)

The combat bot has been logging every swing with `level, might, dam, ac, weapon, mob, zone, look, crit`
all along. Mining it produced the first adjacent-level rogue readings.

**⚠ THE FILE HOLDS TWO CHARACTERS AND HAS NO CHARACTER-ID COLUMN.** Separate them before use — this is
the same trap that produced the phantom Dam flip. The signature is clean:

| Rule | Class | Levels present |
|---|---|---|
| `grace_base` ≈ level, > `might_base` | **Rogue** | 17, 18, 19, 21, 22, 24, 25, 26, 27, 28, 65 |
| `might_base` ≈ level, > `grace_base` | **Warrior** | 16, 19, 20, 22, 23, 24, 25, 28, 29, 30, 32, 33 |

Usable groups (crit filtered out, ×2 rear-attack outliers removed):

```
ROGUE lvl18 might15 dam0 Novice sword (S 10-12)  green squirrel (AC 90)  n=80
  12 x30, 13 x23, 14 x20   +doubles 24 x3, 26 x3, 28 x1
  -> window 12-14  ->  cf in [0.868, 1.342)

ROGUE lvl19 might15 dam0 Novice sword  green squirrel  n=76
  12 x19, 13 x28, 14 x26   +doubles 26 x1, 28 x2
  -> window 12-14  ->  cf in [0.868, 1.342)

ROGUE lvl19 might12 dam0 Novice sword  green squirrel  n=41
  12 x17, 13 x8, 14 x16    -> window 12-14 (might 12 and 15 share mightTerm 0.5 — consistent)

ROGUE lvl18 might12 dam1 Swift sword (S 20-25)  big bat (AC 75)  n=86
  24 x12, 25 x16, 26 x11, 27 x16, 28 x31
  -> window 24-28  ->  cf in [0.71, 1.14)

  INTERSECTION at lvl18: cf in [0.868, 1.14)   ->   0.5-grid value 1.0
```

Three things fell out of this for free:

1. **Positional ×2 CONFIRMED from real data.** The outliers are exact doubles of the main cluster —
   green squirrel 12/13/14 → 24/26/28, never 25 or 27. Closes open question #10.
2. **Rogues get NO warrior weapon bonus.** Swift sword `ItmDam 1` → logged character `dam 1`; novice
   sword `ItmDam 0` → logged `dam 0`. The +2 is warrior-only, independently confirmed.
3. **mightTerm quantization re-confirmed** — might 12 and might 15 give an identical window.

**What is NOT usable, and why.** The rogue's levels 26-28 and the warrior's 28-33 sit in the Mythic
rabbit caves and are heavily contaminated: full ranges of 19-206 with 22 distinct values, from stealth
(×5), rage, and rear attacks, with `weapon` blank on most rows and the mob's AC unknown. ~8,000 swings
of the *exact* missing band, unreadable as logged.

**To make future grinding self-measuring, the bot needs three more columns:** mob name or look (only
7.7% / 32% populated today), weapon (67%), and **buff state — stealth and rage at minimum.** With those,
every grind session becomes classFactor data at no extra cost.

---

## 6. Open questions

### High value

1. ~~`WarriorDamFlipLevel`~~ — **RESOLVED, it does not exist.** See
   [There is NO level-based Dam flip](#there-is-no-level-based-dam-flip--and-why-we-thought-there-was).
   Open follow-up: does the **other** stale stat, base AC, behave the same way — i.e. does a warrior
   who joins the path mid-session also *take* damage as though still a peasant until relog? Same
   experiment, read from damage taken instead of damage dealt. Related to #11.

2. **THE SHAPE PROBLEM — the two classes cannot both be what we've fitted.** This is now the biggest
   open question in the whole model, and it is *structural*, not a missing decimal.

   | | measured | implies |
   |---|---|---|
   | Warrior | 0.0 ≤7 · 0.5 @8 · 1.0 @17-18 · 1.5 @33-35 | gaps 9, then 16 — **decelerating**. Extrapolates to ~2.1-2.5 at level 99 |
   | Rogue | ~1.0 @15-19 · 6.0 @65 | +5.0 across 46 levels — **must accelerate hard** |

   Two problems with that. First, the classes have opposite curvature, which is odd for one formula
   with a per-class constant. Second, the tutor board's endgame values are warrior **9** / rogue **7.5**
   — the warrior *higher* than the rogue at 99, while every measurement below 35 has the warrior far
   flatter. A decelerating warrior cannot reach 9.

   **STEP-POSITION PATTERNS TESTED 2026-08-17** against the measured brackets (warrior 8 / 17-18 /
   33-35):

   | pattern | verdict | step #4 |
   |---|---|---|
   | pure geometric in level `8*r^(n-1)` | **EXCLUDED** — step2 needs r 2.125-2.250, step3 needs 2.031-2.092, no overlap | — |
   | fixed EXPERIENCE milestone | **EXCLUDED** — see below | — |
   | spell-learn levels | hits 8 and 35, MISSES 17-18 (nearest 15 and 20). 22 learn levels in 99 makes 2/3 pure chance | — |
   | geometric in GAPS (k 1.5-2.0) | fits | **55-71** |
   | quadratic / constant 2nd difference | fits | **53-62** |
   | `L(n) = 2^(n+2) + (n-1)` -> 8, 17, 34, 67 | fits all three exactly | **67** |

   The three survivors cluster on **step #4 in levels 53-71**. Nothing separates them without a fourth
   step, which is why a warrior reading at 45-55 remains the highest-value measurement.

   NOTE: an earlier note here called the step levels "very close to doubling". That reading does NOT
   survive testing both brackets simultaneously — see row 1.

   **The EXPERIENCE-milestone idea is killed by the rogue.** `LevelExp.csv` is class-specific and the
   rogue's curve is exactly **0.8x the warrior's** at every level. A rogue therefore reaches any fixed
   exp total at a HIGHER level than a warrior, so a fixed-exp trigger predicts the rogue steps LATER.
   It steps EARLIER (7 vs 8). Backwards.

   **STILL LIVE: "rogue = the warrior's ladder shifted one level earlier."** It fits every rogue
   measurement — step #1 at 7 vs warrior 8, and 1.0 at both 18 and 19 which is consistent with step #2
   at 16-17 vs warrior 17-18. **Rogue level 16 discriminates: 1.0 = shifted ladder, 0.5 = identical to
   the warrior and the level-7 offset is a one-off.** It cannot hold to the top, though — a rogue at 65
   would be ~warrior at 66, cf ~2.0-2.5 under any surviving fit, not the measured 6.0.

   Candidate resolutions for the high-level divergence, none tested: the step size grows with level; the ladder is driven by
   subpath/rank rather than raw level (ranks arrive at irregular levels, which would explain irregular
   steps); the board constants are regression artifacts (see
   [[nexustk-published-formulas-are-endgame-fits]]); or the level-65 rogue's 6.0 is contaminated by
   something unmodelled.

   **The single most valuable measurement available is a warrior at level 45-55.** If cf is still 1.5
   the gaps really do grow and the board's 9 is wrong. If it has jumped to 2.5+, the step size grows and
   both classes are on an accelerating ladder. One clean 40-swing run settles it. A rogue at 35-45 is
   the second most valuable, for the same reason on the other curve.

3. **Rogue classFactor shape.** Now three readings (~15, 18-19, 65) but the step *size* is still
   unobserved — the level-18/19 bands are ~0.3 wide, which permits both a flat 1.0 and a ~0.10/level
   ramp. Any rogue reading in levels 25-50 collapses it.

4. **classFactor above level 35 (warrior) / 65 (rogue).** Nothing measured. The table holds at the last
   value rather than extrapolating. Subsumed by #2.

### Medium

4. **classFactor step #2 — level 17 or 18.** One level from pinned. Needs a fresh warrior at 17.

5. **L range semantics.** RTK uses the L range against **boss-flagged** mobs; the tutor board says L is
   the **crit** range. Untested. The military fork is the instrument (`S 90-100` vs `L 95-105`), and
   **gimyi** (level 20, AC 0, 12-64 dmg, 16950 vita) is the only survivable boss-flagged mob.

6. **Invisible multiplier.** We ship **5**; RTK's `swingDamage.lua` and the tutor board both say **9**.
   Two independent sources against our value. Trivially testable on a stealthed rogue.

7. **Rage vs Cunning.** The board gives warrior Rage `8/14/20/26/36/81` and rogue Cunning
   `6/7/9/10/12` — different ladders per class. We run one `EffRage` for everyone.

8. **Ingress / enchant ladder.** Board says warrior 2-10, rogue 1.5-4. We run one `EffEnchant`.

### Low

9. **Crit rate and multiplier.** Flat 3% and ×3 are ported from RTK, never measured.

### Resolved

10. ~~**Positional ×2.**~~ **CONFIRMED** from `re/auto/swings.csv`: the high outliers in every group are
    exact doubles of the main cluster — green squirrel 12/13/14 → 24/26/28, never 25 or 27. See §5.1.

11. ~~**Player base AC.**~~ **SOLVED: naked base AC = 100 − level**, decrementing by 1 every level (99 at
    level 1, 98 at level 2, …). Already what the code implements —
    `Server/Session.CharacterApi.cs:455` and `Server/Session.cs:858`, cached on
    `Shared/Character.cs` `Ac`. Class-independent; every class reaches AC 1 at level 99.
    Remaining sub-question: base AC is the *other* stat on the
    [stale-stat](#stale-stat-bug) list, so does a warrior who joins the path mid-session also *take*
    damage as though still a peasant until relog? Same experiment, read from damage taken.

---

## 7. Falsified hypotheses — do NOT retry these

| Hypothesis | Why it died |
|---|---|
| `mightTerm = might/8` continuous | Flat stretches at Might 37/38/39 are impossible for a continuous term |
| `mightTerm` 8.8125 intercept above Might 40 | Was RTK's warrior classFactor 9 leaking into a might-only fit |
| `mightTerm` offset −0.5 | Level-1 peasant pins it at −1.0; −0.5 put every Peasant/Mage/Poet one damage high |
| mightTerm step size 0.33 | chi2 10.28 vs 3.41 on the level-65 rogue fork sweep |
| classFactor flat (RTK's table as written) | Predicts deer 45-54 for a level-28 warrior who hits 28-37 (~60% high); one-shots starter mobs |
| classFactor = 0 always | True only below level 8; it steps |
| classFactor linear ramp to level 99 | Overshoots; falsified by level-25/28 |
| classFactor saturating at level 90 | Fitted to a bad level-20 reading |
| classFactor sublinear / power law | Falsified by later readings |
| classFactor uniform period 13 (steps 8/21/34) | Fitted all 11 readings then died to the level-18 run — cf is already 1.0 at 18 |
| unarmed `S = 0-0` | Deterministic; cannot produce the observed two-value spread |
| unarmed `S = 0-1` | Fork cross-reference pins the low roll to 0.5 → S 1-2 |
| `WarriorDamFlipLevel` (any value) | **The flip does not exist.** 11/15/16/23 each died in turn because the constant was fitting the seam between two characters, one of them running stale stats from a live-server bug. See §1 |
| Warrior base Dam −2 below some level | Same. Base Dam is 0 for all classes at all levels; the −2 was a stale peasant value the live server failed to recompute |
| sword_of_power carries +2 Dam | Its tooltip is +1 might / +1 hit / +10 vita, matching Items.csv `ItmDam 0`. The +2 is the warrior weapon bonus on the *character* |
| `math.max(dam, 1)` floor (RTK) | Level-1 peasant with a 0-dam weapon contributes 0. The floor is at **0** |
| Astrael's AC-based hit regression | Keys hit chance off defender AC; predicts 88-100% where live is ~50-63% |
| sword_of_power Items.csv row is wrong | It is correct — the client's "+2 dam" is the warrior weapon bonus |
| Level-20 warrior reading (cf = 0.78) | Only reading where two weapons on one character disagreed; gear changed mid-series. CONTAMINATED, excluded |

---

## 8. Code locations

| What | Where |
|---|---|
| `PlayerSwingDamage` | `Server/Session.Items.cs` |
| `MightTerm`, `ClassFactor`, `WarriorDamFlipLevel`, `HasWeaponEquipped` | `Server/Session.Items.cs` |
| `WeaponTotals` (unarmed S 1-2) | `Server/Session.Items.cs` |
| `ApplyArmor`, `IsBehindTarget`, `RollPlayerSwingRtk` | `Server/Combat.cs` |
| Mob AC (`MobArmor`) | `game-data/mobs.csv` |
| Weapon S ranges | `game-data/Items.csv` |
| RTK reference (diverges — see below) | `RTK-Server/rtklua/Accepted/Scripts/swingDamage.lua` |

### On RTK as a source

RTK's content tables are a fork of ClassicTK's 2019 dump — roughly two decades downstream of 4.95. The
pattern observed throughout: **RTK preserves the structure and the endgame constants, but the era-specific
behaviour has been smoothed away** — continuous where 4.95 quantizes, flat where 4.95 steps, floors added
or moved. Treat it as a source of *shapes and constants*, and measure anything that varies.

---

## Appendix — spell damage (landed in the same session)

Not melee, but recorded so it is not lost. `game-data/spell_effects.csv` damage rows were rewritten:

```
Spark  =  50 + floor((level + will) / 4)      8 rows
Singe  = 100 + floor((level + will) / 2)     12 rows
Ignite = 200 + (level + will)                12 rows   <-- EXTRAPOLATED, NOT MEASURED
```

Each tier doubles: constant ×2, divisor ÷2. Verified exact on three readings — mage lvl16/will16 Spark
58 and Singe 116, rogue lvl65/will25 Singe 145.

The old shape (`25 + floor(level/2) + floor((will+3)/4)`) split a single `(level+will)` term into
independent halves, which happens to look right on a mage (where will == level) and fails on every other
class.

**Ignite is a guess** — it continues the doubling pattern because leaving it on the old shape made Ignite
weaker than Singe. One cast confirms or kills it: predicts **464** on a squirrel at level 16 / will 16.

The archived "Spark = 50 + level/2, 19/19 exact" is correct but is the *mage special case* — a mage's
will equals their level, so `(L+W)/4` collapses to `L/2`.
