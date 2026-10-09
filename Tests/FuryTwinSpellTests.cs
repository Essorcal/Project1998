using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The furies' alignment twins, and the two level-99 dog furies, arm their rage (#334).
///
/// <para>Most abilities exist four times, once per alignment, and a trainer teaches a character only its own
/// alignment's name for each (<c>Content.SpellsForClass</c>). So an aligned Warrior or Rogue holds Soul's Rage,
/// Filling the Soul or Strength of Ancestors in place of Wolf's, Tiger's or Dragon's Fury. Only the originals had
/// a <c>rage</c> row in <c>game-data/SpellMods.csv</c>, so the twins fell through to <c>arch_buff</c>, which took
/// the row's mana, answered "You cast Soul's Rage." and armed nothing: no stat, no slot, and a recast took the
/// mana again. Serpent's Fury and Spirit Fury, the Rogue's and the Warrior's level-99 dog spells, were the same.
/// Silent: the player paid, was told it worked, and swung at x1.</para>
///
/// <para>The values are Caleb's (2026-10-08, #334), from the era spell pages in <c>game-data/Sources.csv</c>:
/// the 2002 Nexus Atlas Warrior and Rogue pages list each twin as an alignment name of its fury, "Increases
/// Weapon Damage" x2 (Wolf's), x3 (Tiger's) and x4 (Dragon's), 625 s, with no stats of their own
/// (<c>atlas-2002-12-30-spells-warrior-dog</c>, <c>atlas-2002-12-30-spells-classes</c>); the Atlas dog page gives
/// Serpent's Fury x4, 800 mana, 375 s and a 25 s aether, and Spirit Fury x5, 1000 mana, 375 s and none. tswolf
/// (2001) agrees on the mana and the durations. RTK's scripts agree on every multiplier (weight 0).</para>
///
/// <para>Driven end to end: the real 0x0F frame through <c>Session.Receive</c>, on content-free maps no other class
/// uses.</para>
/// </summary>
[Collection("world")]
public sealed class FuryTwinSpellTests
{
    private const ushort TwinMap = 62340;
    private const ushort DogMap = 62341;

    /// <summary>Slack on a timer read straight after the cast: the cast and the read are microseconds apart, but
    /// a loaded CI runner can stall a thread for a while.</summary>
    private const long SlackMs = 5_000;

    private readonly SessionFixture _fx;

    public FuryTwinSpellTests(SessionFixture fx) => _fx = fx;

    /// <summary>The fifteen twins: the key, the fury it is a name of, its mana and the rage Caleb chose.</summary>
    public static TheoryData<string, string, int, int> Twins => new()
    {
        { "souls_rage_warrior",            "wolfs_fury_warrior",   30, 2 },
        { "spirit_of_the_forest_warrior",  "wolfs_fury_warrior",   30, 2 },
        { "augmentation_warrior",          "wolfs_fury_warrior",   30, 2 },
        { "souls_rage_rogue",              "wolfs_fury_rogue",     30, 2 },
        { "spirit_of_the_forest_rogue",    "wolfs_fury_rogue",     30, 2 },
        { "augmentation_rogue",            "wolfs_fury_rogue",     30, 2 },
        { "filling_the_soul_warrior",      "tigers_fury_warrior",  90, 3 },
        { "spirit_of_the_wild_warrior",    "tigers_fury_warrior",  90, 3 },
        { "ohaengs_grace_warrior",         "tigers_fury_warrior",  90, 3 },
        { "filling_the_soul_rogue",        "tigers_fury_rogue",    90, 3 },
        { "spirit_of_the_wild_rogue",      "tigers_fury_rogue",    90, 3 },
        { "ohaengs_grace_rogue",           "tigers_fury_rogue",    90, 3 },
        { "strength_of_ancestors_warrior", "dragons_fury_warrior", 150, 4 },
        { "spirit_of_the_dragon_warrior",  "dragons_fury_warrior", 150, 4 },
        { "ohaengs_anger_warrior",         "dragons_fury_warrior", 150, 4 },
    };

    /// <summary>A cast of the twin from the book arms its fury's rage for 625 s and takes its fury's mana; then the
    /// fury itself, second in the book, is refused at no cost, as any fury is while one runs. Red on 45dafed: the
    /// twin leaves the rage at 1 and the fury that follows casts.</summary>
    [Theory]
    [MemberData(nameof(Twins))]
    public void ATwinArmsItsFurysRageAndBlocksTheFury(string twinKey, string furyKey, int mana, int rage)
    {
        var twin = Content.SpellByKey(twinKey)!;
        var fury = Content.SpellByKey(furyKey)!;
        Assert.Equal(rage, Content.RageAmountFor(fury));                   // the original's row, unchanged

        var (session, outbound, c) = _fx.PlayerWith($"Twin{twin.Id}", ch => Shape(ch, twin, fury), TwinMap, x: 5, y: 5);
        try
        {
            session.Receive(SpellCastSupport.CastFrame(0));

            var (armed, left) = SpellCastSupport.Rage(session);
            Assert.Equal((rage, 10_000u - (uint)mana), (armed, c.Mp));
            Assert.InRange(left, 625_000 - SlackMs, 625_000);
            Assert.Contains($"You cast {twin.Name}.", SpellCastSupport.MiniTexts(outbound));

            outbound.Clear();
            uint afterTwin = c.Mp;
            session.Receive(SpellCastSupport.CastFrame(1));

            var lines = SpellCastSupport.MiniTexts(outbound);
            Assert.Equal((afterTwin, rage), (c.Mp, SpellCastSupport.Rage(session).Amount));
            Assert.Contains("You are already benefiting from a fury.", lines);
            Assert.DoesNotContain($"You cast {fury.Name}.", lines);
        }
        finally
        {
            _fx.World.LeaveMap(session, TwinMap);
        }
    }

    /// <summary>Serpent's Fury arms x4 for 375 s, takes 800 mana and starts its 25 s aether, and a recast inside the
    /// aether is refused at no cost. The aether is the point of the C# change: the rage path returned before the
    /// archetype tail that arms a row's aether, so Serpent's Fury would have lost it. Red on 45dafed: the rage
    /// stays at 1.</summary>
    [Fact]
    public void SerpentsFuryArmsRageFourAndKeepsItsAether()
    {
        var sp = Content.SpellByKey("serpents_fury")!;
        var (session, outbound, c) = _fx.PlayerWith("SerpentFury1", ch => Shape(ch, sp), DogMap, x: 5, y: 5);
        try
        {
            session.Receive(SpellCastSupport.CastFrame(0));

            var (armed, left) = SpellCastSupport.Rage(session);
            Assert.Equal((4, 9_200u), (armed, c.Mp));
            Assert.InRange(left, 375_000 - SlackMs, 375_000);
            Assert.InRange(SpellCastSupport.AetherLeft(session, sp.Key), 25_000 - SlackMs, 25_000);
            Assert.Contains("You cast Serpent's Fury.", SpellCastSupport.MiniTexts(outbound));

            outbound.Clear();
            session.Receive(SpellCastSupport.CastFrame(0));

            var lines = SpellCastSupport.MiniTexts(outbound);
            Assert.Equal(9_200u, c.Mp);
            Assert.Contains(lines, l => l.StartsWith("Serpent's Fury isn't ready yet (", StringComparison.Ordinal));
            Assert.DoesNotContain("You cast Serpent's Fury.", lines);
        }
        finally
        {
            _fx.World.LeaveMap(session, DogMap);
        }
    }

    /// <summary>Spirit Fury arms x5 for 375 s and takes 1000 mana; its row has no aether and none is armed. Red on
    /// 45dafed: the rage stays at 1 (the row's <c>furies</c> slot gave it a buff-box line and nothing else).</summary>
    [Fact]
    public void SpiritFuryArmsRageFive()
    {
        var sp = Content.SpellByKey("spirit_fury")!;
        var (session, outbound, c) = _fx.PlayerWith("SpiritFury1", ch => Shape(ch, sp), DogMap, x: 7, y: 5);
        try
        {
            session.Receive(SpellCastSupport.CastFrame(0));

            var (armed, left) = SpellCastSupport.Rage(session);
            Assert.Equal((5, 9_000u), (armed, c.Mp));
            Assert.InRange(left, 375_000 - SlackMs, 375_000);
            Assert.Equal(0, SpellCastSupport.AetherLeft(session, sp.Key));
            Assert.Contains("You cast Spirit Fury.", SpellCastSupport.MiniTexts(outbound));
        }
        finally
        {
            _fx.World.LeaveMap(session, DogMap);
        }
    }

    /// <summary>A level-99 caster with the given spells in book slots 0, 1, … and 10000 mana.</summary>
    private static void Shape(Character c, params SpellDef[] book)
    {
        c.Level = 99;
        c.MaxHp = 1_000; c.Hp = 1_000;
        c.MaxMp = 10_000; c.Mp = 10_000;
        foreach (var sp in book) c.Spells.Add(sp.Id);
    }
}
