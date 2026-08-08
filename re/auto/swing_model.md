# Melee swing model (auto-refreshed)

samples: 1119

## Damage by mob (look id)

| look | n | min | median | max | crit n | low-cluster mean | high-cluster mean | ratio |
|---|---|---|---|---|---|---|---|---|
|  | 413 | 1 | 22 | 255 | 17 | 8.7 | 111.4 | 12.86 |
| 185 | 267 | 8 | 28 | 93 | 18 | 24.8 | 38.9 | 1.57 |
| 25 | 163 | 3 | 25 | 64 | 6 | 11.5 | 36.5 | 3.18 |
| 89 | 117 | 10 | 12 | 75 | 5 | 11.0 | 30.8 | 2.80 |
| 519 | 113 | 20 | 21 | 69 | 5 | 20.7 | 29.9 | 1.44 |
| 88 | 32 | 10 | 12 | 83 | 1 | 10.9 | 30.0 | 2.75 |
| 21 | 13 | 27 | 54 | 64 | 0 | 33.3 | 61.7 | 1.85 |
| 124 | 1 | 26 | 26 | 26 | 0 | 26.0 | 0.0 | 0.00 |

## Least-squares fit (non-crit swings)

`dmg ~= 18.942*DAM + 2.082*might + 4.80`
R^2 = 0.032  (n=1067)

RTK reference: `(s/2*enchant + DAM*2.5 + might/8 + class) * rage * crit`,
then `x(1 + mobArmor/100)` and `x2` for a positional (back) hit.

## Mob HP + armor

HP is BOUNDED, not exact: the killing blow overkills, so true max HP lies in
`(total_damage - killing_blow, total_damage]`. A one-shot kill only gives an
upper bound (lower bound 0), so those rows are marked and are weak evidence.

| look | clean kills | HP lower | HP upper | best bound | multi-hit kills | swings | exp |
|---|---|---|---|---|---|---|---|
| 185 | 262 | 9 | 19 | (9, 17] | 5 | 1.0 | 75 |
| 25 | 99 | 13 | 25 | (15, 18] | 43 | 1.9 | 10 |
| 21 | 23 | 0 | 30 | (0, 2]  _(1-shot only)_ | 0 | 1.0 | 5 |
| 89 | 11 | 290 | 317 | (256, 266] | 11 | 22.3 | 50 |
| 519 | 3 | 925 | 945 | (925, 945] | 3 | 37.7 | 384 |
| 88 | 2 | 261 | 286 | (261, 273] | 2 | 16.0 | 50 |
| 451 | 2 | 380 | 391 | (380, 391] | 2 | 30.5 | 90 |
| 22 | 1 | 183 | 192 | (183, 192] | 1 | 21.0 | 320 |

### Relative armor (frontal non-crit damage ratios)

| look | frontal mean dmg | dmg vs softest | implied AC if softest = 0 |
|---|---|---|---|
| 21 | 33.3 | 1.000 | 0 |
| 124 | 26.0 | 0.781 | -22 |
| 185 | 24.8 | 0.744 | -26 |
| 519 | 20.7 | 0.622 | -38 |
| 25 | 11.4 | 0.341 | -66 |
| 88 | 10.9 | 0.328 | -67 |
| 89 | 10.6 | 0.318 | -68 |
|  | 8.9 | 0.268 | -73 |

_Caveat: this assumes the softest observed mob has AC 0 and that your stats were comparable across these fights. It is a RELATIVE ladder; anchor it with one known mob AC to make it absolute._

## Absolute mob AC (needs DAM to vary per mob AT A FIXED LEVEL)

| look | level | might | DAM values | slope | implied AC = 100*(slope/2.5 - 1) |
|---|---|---|---|---|---|
|  | 12.0 | 9 | [0, 1.0] | 32.38 | 1195 |
| 25 | 15.0 | 10 | [0, 1.0] | 21.34 | 754 |
| 89 | 15.0 | 10 | [0, 1.0] | 15.70 | 528 |
|  | 15.0 | 10 | [0, 1.0] | 48.84 | 1853 |
| 185 | 14.0 | 10 | [0, 1.0] | 1.16 | -53 |
| 185 | 13.0 | 9 | [0, 1.0] | -22.85 | -1014 |
| 88 | 15.0 | 10 | [0, 1.0] | 13.51 | 440 |
| 185 | 10.0 | 8 | [0, 1.0] | 2.27 | -9 |
|  | 13.0 | 9 | [0, 1.0] | 0.00 | -100 |
| 185 | 5.0 | 5 | [0, 1.0] | -4.14 | -265 |
| 21 | 15.0 | 10 | [0, 1.0] | -19.08 | -863 |

_Assumes RTK's DAM*2.5 term holds on the live server. If it doesn't, these
are wrong by exactly that factor -- cross-check against the incoming-damage
test below, which uses YOUR known AC and assumes nothing._

## Armor law check, using YOUR known AC

| attacker | level | your AC | hits | mean dmg | ratio vs first | predicted |
|---|---|---|---|---|---|---|
| 519 | 12 | 73 | 49 | 19.3 | 1.000 | 1.000 |
| 519 | 12 | 83 | 14 | 19.3 | 0.999 | 1.058 |

_'ratio vs first' should track 'predicted' if damage really scales as
(1 + AC/100). Agreement confirms the armor law live and lets the DAM*2.5
anchor above be trusted; disagreement means the live formula differs from RTK._
