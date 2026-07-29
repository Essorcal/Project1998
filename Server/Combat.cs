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

    /// <summary>RTK <c>swingDamage.lua</c>'s base, UNCONDITIONAL positional bonus — every swing gets this
    /// check regardless of class/stance (unlike <see cref="IsBackstabAngle"/>/<see cref="IsFlankAngle"/>
    /// below, which only apply while the Warrior-only Backstab/Flank stance is active): when attacker and
    /// target face the SAME direction and the attacker is positioned on the target's blind side (i.e.
    /// genuinely sneaking up from behind a target moving/facing the same way), the swing doubles. Dir
    /// 0=N/1=E/2=S/3=W matches this codebase's existing convention throughout (Session/Mob.Dir), which
    /// already lines up with RTK's own "side" numbering — no translation needed.</summary>
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

    /// <summary>RTK <c>swingDamage.lua</c>'s <c>block.backstab</c> table — active only while the caster has
    /// the Warrior "Backstab" skill's stance up (RTK Spells.csv: SplPthId=1, no other class learns it; a
    /// timed self-buff, NOT a passive or weapon effect — see <c>Session.CastStance</c>). Unlike
    /// <see cref="IsBehindTarget"/>, this checks OPPOSITE facings (attacker's dir is target's dir+2 mod 4)
    /// with the attacker positioned on the target's back side — literally "the target doesn't see you
    /// coming from their blind side while walking toward you".</summary>
    public static bool IsBackstabAngle(byte attackerDir, byte targetDir, int atkX, int atkY, int tgtX, int tgtY) =>
        (attackerDir, targetDir) switch
        {
            (0, 2) => atkY < tgtY,
            (1, 3) => atkX > tgtX,
            (2, 0) => atkY > tgtY,
            (3, 1) => atkX < tgtX,
            _ => false,
        };

    /// <summary>RTK <c>swingDamage.lua</c>'s <c>block.flank</c> table — active only while the caster has the
    /// Warrior "Flank" skill's stance up (same real-skill caveat as <see cref="IsBackstabAngle"/>). Checks
    /// PERPENDICULAR facings (a 90-degree angle between attacker and target dir) with a specific
    /// positional condition per angle pair — RTK's literal table, not derived from a simpler rule.</summary>
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
    /// <c>minimumArmor</c>) — armor can reduce a hit but never fully negate it.</summary>
    public static int ApplyArmor(int rawDamage, int armor, int floor)
    {
        double deduction = 1.0 + Math.Max(armor, floor) / 100.0;
        return Math.Max(1, (int)Math.Floor(rawDamage * deduction));
    }
}
