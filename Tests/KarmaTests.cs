using Server;
using Xunit;

namespace Tests;

/// <summary>
/// The karma ladder (<see cref="Karma"/>, ported from RTK <c>player.lua</c>).
///
/// Worth pinning because karma fails silently in both directions: a wrong band hands a spell to someone who
/// should be refused, or locks one away from someone who earned it, and neither throws or logs. The bands
/// are also the kind of table that invites a "tidy-up" — these tests make an edit to the numbers a build
/// failure rather than a live-server surprise.
///
/// The zero case is the interesting one. RTK's own two functions disagree there (karmaLevel returns "Rat"
/// for exactly 0; karmaCheck treats 0 as satisfying "cat" and requires &lt; 0 for "rat"), and we follow
/// karmaCheck — so a fresh character reads as a Cat, not a Rat. See Karma.LevelName's remarks.
/// </summary>
public class KarmaTests
{
    [Theory]
    // the scum floor and the band between it and zero
    [InlineData(-9.0, "Snake")]
    [InlineData(-3.0, "Snake")]     // <= -3, boundary is inclusive
    [InlineData(-2.99, "Rat")]
    [InlineData(-0.5, "Rat")]
    // a fresh character: 0 is a Cat, NOT RTK karmaLevel's "Rat" — the documented divergence
    [InlineData(0.0, "Cat")]
    [InlineData(0.99, "Cat")]
    [InlineData(1.0, "Squirrel")]
    [InlineData(2.0, "Rabbit")]
    [InlineData(3.0, "Dog")]
    [InlineData(4.0, "Monkey")]
    [InlineData(5.9, "Monkey")]
    [InlineData(6.0, "Ox")]
    [InlineData(8.0, "Bear")]
    [InlineData(11.0, "Tiger")]
    [InlineData(14.0, "Dragon")]
    [InlineData(19.0, "Spirit")]
    [InlineData(24.0, "Angel's tear")]
    [InlineData(30.0, "Angel")]
    [InlineData(999.0, "Angel")]
    public void LevelName_bands(double karma, string expected)
        => Assert.Equal(expected, Karma.LevelName(karma));

    [Theory]
    // the gates RTK's trainers actually use, at and just below their thresholds
    [InlineData(2.0, "rabbit", true)]
    [InlineData(1.99, "rabbit", false)]
    [InlineData(6.0, "ox", true)]
    [InlineData(5.99, "ox", false)]
    [InlineData(11.0, "tiger", true)]
    [InlineData(10.99, "tiger", false)]
    [InlineData(19.0, "spirit", true)]     // ExpSeller's wind-armor branch
    [InlineData(18.99, "spirit", false)]
    // "cat" is satisfied at exactly 0 — the half of the RTK disagreement we kept
    [InlineData(0.0, "cat", true)]
    [InlineData(-0.01, "cat", false)]
    // the bottom two invert: they ask "is this player that bad", not "at least this good"
    [InlineData(-3.0, "snake", true)]
    [InlineData(-2.99, "snake", false)]
    [InlineData(50.0, "snake", false)]     // a saint is not "at least" a snake
    [InlineData(-0.5, "rat", true)]
    [InlineData(0.0, "rat", false)]
    [InlineData(50.0, "rat", false)]
    public void Meets_gates(double karma, string tier, bool expected)
        => Assert.Equal(expected, Karma.Meets(karma, tier));

    [Theory]
    [InlineData("TIGER")]
    [InlineData("Tiger")]
    [InlineData("  tiger  ")]
    public void Meets_is_case_and_space_insensitive(string tier)
        => Assert.True(Karma.Meets(11.0, tier));

    [Fact]
    public void Meets_unknown_tier_is_false()
    {
        // RTK's karmaCheck falls through to `return false` on an unrecognised name. A typo'd gate must
        // therefore REFUSE rather than wave everyone past — the safe direction for a misspelling.
        Assert.False(Karma.Meets(999.0, "archangel"));
        Assert.False(Karma.Meets(999.0, ""));
    }

    [Fact]
    public void LevelName_round_trips_through_Meets()
    {
        // Every named band must satisfy its own tier: the two functions are separate ports of the same
        // ladder, and this is what stops them drifting apart the way RTK's did at zero.
        foreach (var k in new[] { 0.0, 1.0, 2.0, 3.0, 4.0, 6.0, 8.0, 11.0, 14.0, 19.0, 24.0, 30.0 })
            Assert.True(Karma.Meets(k, Karma.LevelName(k)),
                        $"karma {k} reads as '{Karma.LevelName(k)}' but does not satisfy that tier");
    }
}
