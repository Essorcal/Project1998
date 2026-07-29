# Melee swing model (auto-refreshed)

samples: 709

## Damage by mob (look id)

| look | n | min | median | max | crit n | low-cluster mean | high-cluster mean | ratio |
|---|---|---|---|---|---|---|---|---|
|  | 328 | 1 | 23 | 255 | 12 | 8.5 | 121.5 | 14.24 |
| 185 | 267 | 8 | 28 | 93 | 18 | 24.8 | 38.9 | 1.57 |
| 519 | 113 | 20 | 21 | 69 | 5 | 20.7 | 29.9 | 1.44 |
| 25 | 1 | 28 | 28 | 28 | 0 | 28.0 | 0.0 | 0.00 |

## Least-squares fit (non-crit swings)

`dmg ~= -15.693*DAM + 8.635*might + -17.03`
R^2 = 0.044  (n=674)

RTK reference: `(s/2*enchant + DAM*2.5 + might/8 + class) * rage * crit`,
then `x(1 + mobArmor/100)` and `x2` for a positional (back) hit.
