namespace Server;

/// <summary>
/// Shared melee-damage math used by both directions of combat (<see cref="World"/>'s mob AI swinging at a
/// player, and <see cref="Session.HandleAttack"/> swinging at a mob) — ported from RTK's real Lua combat
/// engine (<c>Accepted/Scripts/hitCritChance.lua</c> + <c>swingDamage.lua</c>), NOT the commented-out/dead
/// damage math in the C engine's <c>mob.c</c>. Kept as pure, stateless functions so both call sites can share
/// one verified implementation instead of drifting into two subtly different formulas.
/// </summary>
public static class Combat
{
    /// <summary>RTK <c>hitCritChance.lua</c>: rolls a swing's <c>critChance</c> classification — 0 (outside
    /// the hit-chance window entirely), 1 (a normal hit), or 2 (a genuine critical). Two distinct hitchance
    /// formulas, matching the Lua's <c>block.blType</c> branch:
    /// <list type="bullet">
    /// <item>Player attacker: hitchance = 55 + (grace+level)*0.75 + hit - (targetGrace+targetLevel)*0.5;
    /// crit chance (once inside the hitchance window) is a flat 3.</item>
    /// <item>Mob attacker: hitchance = 95 - (targetGrace/10)*2 - (targetLevel-level); crit chance is
    /// hit/5.</item>
    /// </list>
    /// Both hitchances are clamped to [5,100]. IMPORTANT: the caller decides whether "2" actually multiplies
    /// damage — RTK's <c>swingDamage.lua</c> only ever applies the x3 crit multiplier for a PLAYER attacker
    /// (baked into <c>_getPlayerSwingDamage</c>); a MOB's own crit roll exists (it plays extra hit sounds
    /// via hitCritChance.lua) but <c>_getMobSwingDamage</c> never reads it — a mob's damage is the same
    /// whether or not it "crit". Don't scale mob→player damage by this return value; do use it for the
    /// wire-visual crit byte on either side. NOT ported: RTK's <c>targetSpeed</c> multiplier (a player
    /// haste/slow stat we don't model) and the overflow "critChanceIncrease" bonus (only applies when the
    /// raw hitchance exceeds 100 before clamping — a minor edge case). Also NOT ported: the equipment "Miss"
    /// stat's flat early-miss roll — our item data has no Miss column to source it from, so every swing here
    /// always lands (for at least 1 damage), same as RTK's common case (Miss is a rare cursed-item stat, not
    /// a baseline miss chance).</summary>
    /// <remarks>The <c>attackerIsMob:false</c> (player) branch is SUPERSEDED by <see cref="RollPlayerSwingRtk"/>
    /// (Astrael's board regression, which models real misses) and no longer called — only the mob-attacker
    /// branch is live. Kept intact rather than deleted so the mob path is unchanged.</remarks>
    public static int RollCritChance(bool attackerIsMob, double atkGrace, double atkLevel, double atkHit, double tgtGrace, double tgtLevel)
    {
        double hitchance = attackerIsMob
            ? 95 - (tgtGrace / 10.0) * 2 - (tgtLevel - atkLevel)
            : 55 + (atkGrace + atkLevel) * 0.75 + atkHit - (tgtGrace + tgtLevel) * 0.5;
        hitchance = Math.Clamp(hitchance, 5, 100);

        if (Random.Shared.Next(100) >= hitchance) return 0;   // outside the roll window

        double critChance = attackerIsMob ? atkHit / 5.0 : 3;
        critChance = Math.Clamp(critChance, 1, 30);
        return Random.Shared.Next(100) < critChance ? 2 : 1;
    }

    /// <summary>The outcome of a player weapon swing: a genuine <see cref="Miss"/> (0 damage), a normal
    /// <see cref="Hit"/>, or a <see cref="Crit"/> (×3, applied by the caller).</summary>
    public enum SwingOutcome { Miss, Hit, Crit }

    // DELETED 2026-08-04: RollPlayerSwing - Warrior Tutor Astrael's AC-based regression
    //   (pathBase + 0.4448*defAc - 0.2260*defGrace + 0.3499*might + 0.8858*hit), with per-path bases
    //   67.7781 Warrior / 84.8479 Rogue. Removed outright rather than left uncalled because it is not
    //   merely unused, it is DISPROVEN - leaving it invited a future reader to restore it. Two independent
    //   failures: (1) it keys hit chance off the DEFENDER's AC, which is calibrated for the negative player
    //   AC of PvP and blows up to 88-100% against positive mob AC, where the 7.x tap measures ~47-63%;
    //   (2) its intercept is a REGRESSION ARTIFACT fitted near AC -76 and predicts 131.7% against an AC-90
    //   green squirrel whose real rate is ~63-75%. See nexustk-published-formulas-are-endgame-fits.
    //   Session.HitBase (its per-path table) was deleted with it. RollPlayerSwingRtk below is the
    //   live-validated replacement.

    /// <summary>The player hit-rate ceiling. RAISED BACK TO RTK's 100 on 2026-08-04 — i.e. no artificial
    /// cap. It sat at 62 for a while, which meant a level-99 character in perfect gear still whiffed 38% of
    /// swings at a rabbit. That was never defensible; it came from over-generalising one lvl-13..15 rogue's
    /// automated measurements.
    ///
    /// Unclamped, the formula matches almost every anchor we own:
    ///                                        predicted   measured    instrument
    ///     deer       L5  lvl15 rogue            74.5       61.5%     7.x tap  (automated)
    ///     cat        L15 lvl15 rogue            64.5       63%       7.x tap  (automated)
    ///     fox        L30 lvl15 rogue            49.5       47%       7.x tap  (automated)
    ///     g.squirrel L5  lvl13 rogue +3 hit     72.5       63.2%     bot      (automated, n=272)
    ///     g.squirrel L5  lvl13 rogue +3 hit     72.5       75%       HAND     (n=20)
    /// Three of five land within 2.5 points with NO ceiling, and the split is not random: every miss-match
    /// is an AUTOMATED reading against a LEVEL-5 target, while cat, fox and the hand count all agree.
    ///
    /// The cause is almost certainly that LOW-LEVEL MOBS FLEE: the target steps off the tile between the
    /// swing and its resolution, so the tool records a landed swing at empty ground as a miss. Same
    /// contamination as the unfiltered `dist != 1` rows in the bot's swings.csv, and the same root cause as
    /// the positional-x2 artifact (flee-prone Mouse showed 22% doubled hits vs Bat's 6%). So the 62 was
    /// fitted to ONE over-prediction (deer) that was itself a measurement artifact.
    ///
    /// KEPT AS A NAMED CONSTANT, not deleted: a real cap may well exist, we simply have no valid evidence
    /// for where. Re-measure BY HAND, or with a bot that excludes swings where the target moved. Do not
    /// re-derive it from raw automated hit rates against weak mobs — that is exactly how 62 happened.</summary>
    public const double PlayerHitCeiling = 100;

    /// <summary>Player melee hit/crit via RTK <c>hitCritChance.lua</c> (grace/level based), clamped to
    /// <see cref="PlayerHitCeiling"/>. LIVE-VALIDATED against the 7.x combat tap: matches cat (lvl15,
    /// 63%) and fox (lvl30, 47%) within a point, where the old AC-based Astrael regression (deleted above)
    /// gave a wrong 88-100% (it keyed hit chance off the DEFENDER's AC, calibrated for negative player
    /// AC in PvP but blown to 100% by positive mob AC). Hit chance keys off the LEVEL+GRACE gap; AC is
    /// now purely a damage-reduction term (<see cref="ApplyArmor"/>). Used for ALL player melee (PvM and
    /// PvP) per the "clamped RTK everywhere" decision. Crit is RTK's flat 3% on a landed hit.
    ///
    /// DEFENDER TERM IS LEVEL-ONLY (2026-08-03). RTK's original used <c>(tgtGrace + tgtLevel) * 0.5</c>, but
    /// mobs.csv's Grace column is SYNTHETIC — 519/716 rows have Grace == Level exactly, 117 have Grace == 0,
    /// and Might is 0 on 713/716. So Grace carries no information; it only injects a factor-2 wobble in
    /// evasion between rows that are otherwise siblings. Folding it into <c>tgtLevel * 1.0</c> is IDENTICAL
    /// on the 519 Grace==Level rows (which include every live-validated point below) and merely makes the
    /// 117 zeroed rows consistent with their peers, instead of mysteriously 0.5*Level easier to hit.
    ///
    /// LIVE-VALIDATED (all four unchanged by the fold — deer/cat/fox/squirrel all have Grace == Level):
    ///   deer L5 tap 61.5% -> 62 (clamped) | cat L15 tap 63% -> 62 | fox L30 tap 47% -> 49.5
    ///   green squirrel L5, bot n=272 63.2% -> 62      (independent confirmation, 2026-08-03)
    /// TO RE-FIT LATER: these four (attacker was a lvl-15 rogue for the tap, lvl-13 for the squirrel) are the
    /// anchor set. A manual hand-swing count on green squirrel read 15/20 = 75%, CI [50.9, 91.3] — overlapping
    /// the bot's 63.2%, so PlayerHitCeiling could be ~12 points low. 62 is deliberately the CONSERVATIVE
    /// side of that ambiguity (errs toward missing). Re-measure the ceiling first; it dominates the
    /// early game because every starter mob clamps against it.</summary>
    public static SwingOutcome RollPlayerSwingRtk(double atkGrace, double atkLevel, double atkHit,
                                                  double tgtGrace, double tgtLevel)
    {
        double hitChance = 55 + (atkGrace + atkLevel) * 0.75 + atkHit - tgtLevel * 1.0;
        hitChance = Math.Clamp(hitChance, 5, PlayerHitCeiling);
        if (Random.Shared.Next(100) >= hitChance) return SwingOutcome.Miss;
        return Random.Shared.Next(100) < 3 ? SwingOutcome.Crit : SwingOutcome.Hit;
    }

    /// <summary>RTK <c>swingDamage.lua</c>'s base, UNCONDITIONAL positional bonus — every swing gets this
    /// check regardless of class/stance (unlike <see cref="IsBackstabAngle"/>/<see cref="IsFlankAngle"/>
    /// below, which only apply while the Warrior-only Backstab/Flank stance is active): when attacker and
    /// target face the SAME direction and the attacker is positioned on the target's blind side (i.e.
    /// genuinely sneaking up from behind a target moving/facing the same way), the swing doubles. Dir
    /// 0=N/1=E/2=S/3=W matches this codebase's existing convention throughout (Session/Mob.Dir), which
    /// already lines up with RTK's own "side" numbering — no translation needed.</summary>
    /// <summary>The direction an attack TRAVELS, i.e. from the attacker toward the tile being struck.
    /// On an ordinary swing this equals the attacker's facing, but the Flank stance resolves a swing
    /// against a SIDE tile (see <c>Session.SwingTile</c>) — and every positional rule here is about the
    /// geometry of the blow, not about where the attacker's sprite happens to point. Feeding facing
    /// instead of this would silently disable the rear x2 on every flank hit. Dir 0=N/1=E/2=S/3=W.</summary>
    public static byte AttackDir(int atkX, int atkY, int tgtX, int tgtY) =>
        tgtY < atkY ? (byte)0 : tgtX > atkX ? (byte)1 : tgtY > atkY ? (byte)2 : (byte)3;

    public static bool IsBehindTarget(byte attackerDir, byte targetDir, int atkX, int atkY, int tgtX, int tgtY)
    {
        if (attackerDir != targetDir) return false;
        return attackerDir switch
        {
            0 => atkY > tgtY,   // both facing north; attacker is south (behind)
            1 => atkX < tgtX,   // both facing east; attacker is west (behind)
            2 => atkY < tgtY,   // both facing south; attacker is north (behind)
            3 => atkX > tgtX,   // both facing west; attacker is east (behind)
            _ => false,
        };
    }

    /// <summary>RTK <c>swingDamage.lua</c>'s <c>block.backstab</c> table — checks OPPOSITE facings
    /// (attacker's dir is target's dir+2 mod 4) with the attacker on the target's back side.
    ///
    /// RETIRED, NOT CALLED, and do NOT re-wire it (2026-08-04). Two reasons:
    /// (1) It could never fire. A swing resolves against the tile you face, which fixes the geometry, and
    /// this table's positional test is inverted for all four directions — e.g. attackerDir 0 puts the target
    /// at atkY-1 so atkY &gt; tgtY, but entry (0,2) requires atkY &lt; tgtY. False for every real swing.
    /// (2) It was the wrong model anyway. Backstab is a TARGETING spell — RTK's own <c>backstab.lua</c>
    /// describes it as "Strikes an enemy behind you", and <c>swing.lua</c> gates extra target SETS on
    /// player.backstab. RTK's swingDamage x2 tables mis-ported a reach feature into a damage one. The real
    /// behaviour now lives in <c>Session.SwingTargets</c> (rear tile, reduced damage, ADDITIVE to the faced tile).
    /// Kept only as a record of RTK's table. Same story for <see cref="IsFlankAngle"/>.</summary>
    public static bool IsBackstabAngle(byte attackerDir, byte targetDir, int atkX, int atkY, int tgtX, int tgtY) =>
        (attackerDir, targetDir) switch
        {
            (0, 2) => atkY < tgtY,
            (1, 3) => atkX > tgtX,
            (2, 0) => atkY > tgtY,
            (3, 1) => atkX < tgtX,
            _ => false,
        };

    /// <summary>RTK <c>swingDamage.lua</c>'s <c>block.flank</c> table — checks PERPENDICULAR facings (a
    /// 90-degree angle between attacker and target dir) with a specific positional condition per angle pair.
    /// RTK's literal table, not derived from a simpler rule.
    ///
    /// RETIRED AS REDUNDANT, not as wrong (2026-08-04) — see <see cref="IsBackstabAngle"/> for the full
    /// argument. Verified entry-by-entry: all 8 pairs are the same "target's back is to the incoming blow"
    /// rule specialised to a SIDE reach tile. Each positional test merely pins the target to the side tile
    /// matching its own facing, e.g. (0,1) "we face N, target faces E, target is east of us" is exactly
    /// <c>AttackDir == targetDir == E</c>, which <see cref="IsBehindTarget"/> already tests. So
    /// <see cref="AttackDir"/> + <see cref="IsBehindTarget"/> covers every case this table did.
    /// Flank's own reach ("only hits a 1 target to the left or right per swing, not both. (Random.)") lives
    /// in <c>Session.SwingTargets</c>. Do NOT re-wire — it would double-count the rear x2.</summary>
    public static bool IsFlankAngle(byte attackerDir, byte targetDir, int atkX, int atkY, int tgtX, int tgtY) =>
        (attackerDir, targetDir) switch
        {
            (0, 1) => atkX < tgtX,
            (0, 3) => atkX > tgtX,
            (1, 0) => atkY > tgtY,
            (1, 2) => atkY < tgtY,
            (2, 1) => atkX < tgtX,
            (2, 3) => atkX > tgtX,
            (3, 0) => atkY > tgtY,
            (3, 2) => atkY < tgtY,
            _ => false,
        };

    /// <summary>RTK <c>swingDamage.lua</c>'s armor deduction: <c>deduction = 1 + max(armor, floor)/100</c>,
    /// floored (not rounded) after multiplying. Signed/lower-is-better armor: a well-armored (very negative)
    /// defender takes as little as <c>1+floor/100</c> of the raw hit; an unarmored/positive-armor defender
    /// takes MORE than raw. <paramref name="floor"/> is -80 for a player target, -95 for a mob target (RTK's
    /// <c>minimumArmor</c>) — armor can reduce a hit but never fully negate it.
    ///
    /// TAKES A DOUBLE ON PURPOSE — there is exactly ONE floor in the whole chain, here at the end.
    /// The caller used to truncate the raw swing to int first, which double-floored and was DISPROVEN
    /// live (2026-08-03): a Novice sword (S 10-12, dam 0) at might 12 vs an AC-90 green squirrel produced
    /// exactly {12,13,14} over n=178, which single-flooring reproduces 3/3 (6.5/7.0/7.5 × 1.9 =
    /// 12.35/13.30/14.25) and pre-truncation does not (it yields {11,13}, losing 12 and 14 entirely).
    /// Int callers still bind here via implicit conversion and are unaffected.
    ///
    /// <para>THE DEDUCTION IS FORMED AS <c>(100 + armor) / 100.0</c>, one integer add then one division, and
    /// NOT as <c>1.0 + armor/100.0</c>. The two are equal in real arithmetic and not in IEEE754: at the human
    /// floor, <c>1.0 + (-80/100.0)</c> evaluates to 0.19999999999999996, so a maximally armored player took
    /// 19% of a hit where the model says 20% — off by a whole point at exactly the value the clamp makes most
    /// common. The integer form is exact at every hundredth and reproduces all 19 live Spark readings
    /// unchanged; <see cref="Tests"/>' CombatArmorTests pins both that and this case.</para></summary>
    public static int ApplyArmor(double rawDamage, int armor, int floor)
    {
        double deduction = (100 + Math.Max(armor, floor)) / 100.0;
        return Math.Max(1, (int)Math.Floor(rawDamage * deduction));
    }
}
