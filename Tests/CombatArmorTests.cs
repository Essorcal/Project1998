using Server;
using Xunit;

namespace Tests;

/// <summary>
/// <see cref="Combat.ApplyArmor"/> — <c>damage × (1 + ac/100)</c>, floored, minimum 1.
///
/// This is the most heavily measured formula in the project and, as of 2026-08-21, the most widely reached:
/// it was already on every melee swing and mob hit, and now nets every DAMAGE SPELL as well (see
/// <c>Session.SpellNet</c> and <c>ReceiveSpellDamage</c>). It had no test coverage at all, which is a bad
/// combination — a regression here changes every damage number in the game and throws nothing.
///
/// The readings below come from the live Spark probe: <c>Spark = floor((50 + level/2) × (1 + ac/100))</c>,
/// solved across 19 readings with zero residual — 9 mobs spanning AC 40–100 plus 10 self-casts with the
/// caster's own AC varied by swapping gear. Level 11 gives a base of 55.5 and level 13 gives 56.5; those
/// halves are the reason the method takes a <c>double</c> and must not be pre-truncated by callers.
/// </summary>
public class CombatArmorTests
{
    /// <summary>The probe's own readings, reproduced exactly. Positive AC AMPLIFIES — the counterintuitive
    /// half of the model, and the one a "fix" is most likely to invert: a rabbit at +100 takes double, while
    /// a marsh ogre at −65 takes 35%.</summary>
    [Theory]
    [InlineData(55.5, 100, 111)]   // level 11 vs a rabbit (+100): exactly double
    [InlineData(55.5,  40,  77)]   // level 11 vs AC 40  -> 77.7
    [InlineData(56.5,  80, 101)]   // level 13 vs a deer/doe/cat (+80) -> 101.7
    [InlineData(56.5, 100, 113)]   // level 13 vs a rabbit
    [InlineData(100.0, -65,  35)]  // marsh ogre resists: 35%
    [InlineData(100.0, -10,  90)]  // log ogre
    [InlineData(100.0,   0, 100)]  // AC 0 is the identity — "to zero AC" in every archive formula
    public void ReproducesTheLiveSparkReadings(double raw, int ac, int expected) =>
        Assert.Equal(expected, Combat.ApplyArmor(raw, ac, floor: -95));

    /// <summary>FLOOR, not round — established at 5/5 against the level-13 set where rounding scored 3/5.
    /// 55.5 × 1.4 = 77.7 is the discriminating case: flooring gives 77, rounding would give 78.</summary>
    [Fact]
    public void TruncatesRatherThanRounding()
    {
        Assert.Equal(77, Combat.ApplyArmor(55.5, 40, floor: -95));    // 77.7
        Assert.Equal(101, Combat.ApplyArmor(56.5, 80, floor: -95));   // 101.7
    }

    /// <summary>The two floors are different on purpose: −95 for mobs (RTK's minimumArmor) and −80 for
    /// humans, so armor can never fully negate a hit — a maximally armored player still takes 20%.
    ///
    /// <para>The −80 case is also a floating-point regression guard. Forming the deduction as
    /// <c>1.0 + (-80/100.0)</c> yields 0.19999999999999996, giving <b>19</b> here rather than 20 — a full
    /// point of error at precisely the value the clamp makes most common. <c>ApplyArmor</c> therefore does
    /// the integer add first, <c>(100 + armor) / 100.0</c>. Rewriting it the "obvious" way fails this.</para></summary>
    [Fact]
    public void ClampsAtTheFloorAndTheFloorsDiffer()
    {
        Assert.Equal(5,  Combat.ApplyArmor(100, -500, floor: -95));   // clamped to -95 -> 5%
        Assert.Equal(20, Combat.ApplyArmor(100, -500, floor: -80));   // clamped to -80 -> 20%, NOT 19
        Assert.Equal(20, Combat.ApplyArmor(100,  -80, floor: -80));   // and at -80 reached directly
        Assert.Equal(5,  Combat.ApplyArmor(100,  -95, floor: -95));   // at the mob floor exactly
    }

    /// <summary>A hit always lands for at least 1, however armored the target — otherwise a heavily armored
    /// target would be immune to weak attacks rather than merely resistant to them.</summary>
    [Fact]
    public void NeverFallsBelowOne()
    {
        Assert.Equal(1, Combat.ApplyArmor(1, -95, floor: -95));       // 0.05 -> 1
        Assert.Equal(1, Combat.ApplyArmor(0, -95, floor: -95));
    }

    /// <summary>Callers must hand over the RAW double. Truncating first and letting the method floor again
    /// double-floors: the melee data (n=178) reproduces {12,13,14} only when the halves survive into here.
    /// This pins the difference so a caller "tidying" its own int conversion breaks the build.</summary>
    [Fact]
    public void PreTruncatingTheInputChangesTheAnswer()
    {
        Assert.Equal(77, Combat.ApplyArmor(55.5, 40, floor: -95));    // as measured
        Assert.Equal(77, Combat.ApplyArmor(55,   40, floor: -95));    // 55 -> 77.0, same here...
        Assert.Equal(111, Combat.ApplyArmor(55.5, 100, floor: -95));  // ...but 111 vs
        Assert.Equal(110, Combat.ApplyArmor(55,   100, floor: -95));  // 110 once the half is lost
    }
}
