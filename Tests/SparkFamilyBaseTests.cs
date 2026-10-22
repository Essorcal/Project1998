using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// #200: the Spark family lands the live probe's base, <c>50 + level/2</c>, with the half carried to the armour
/// step. The probe (docs/4.x/Protocol.md, "DAMAGE SPELLS TAKE THE TARGET'S AC TOO") solves
/// <c>floor((50 + level/2) x (1 + ac/100))</c> across 19 live readings with zero residual: 9 creatures at AC 40-100
/// and 10 self-casts with the caster's own AC moved by gear. "A +1 will item changed nothing."
///
/// <para>Seven of the eight rows the decision sheet names cast through the Damage archetype (<c>arch_damage</c>,
/// <c>ctx:magicDamage</c>) and take this base: Spark, Glimpse of the Void and Nature's Ire for mage, and all four for
/// poet. Mage Bolt does not: <c>game-data/SpellParams.csv</c> binds <c>bolt_mage</c> to <c>verbs.magic_damage</c>
/// (2 + 1.5 x Will, 4 mana), which is not the Spark row at all, so moving it is a different decision, held for Caleb.
/// The Bolt facts at the end pin that its formula and cost were left as they are, and that its odd-Will half now
/// reaches the armour step like every other path's (#78).</para>
///
/// <para>Before this, the rows read <c>50 + floor((level + will)/4)</c>, a whole number, so an odd level could never
/// show its half: the level-11 rabbit reading (111) came out 110. Removing the floor in the data alone would not have
/// fixed it either: <c>Session.CastArch</c> rounded the amount, half to even, and <c>ctx:magicDamage</c>,
/// <c>LuaMagicDamage</c>, <c>SpellNet</c> and the player intake each took an int, so 57.5 became 58 and the level-15
/// self-cast at AC 85 landed 107 where the probe read 106.</para>
///
/// <para>Every cast is the real 0x0F frame through <c>Session.Receive</c>, by a fresh caster at a fresh creature (or, on
/// the arena, at nobody, which lands a Damage spell on its caster there). The creature has no Will and no Protection,
/// so the deflect roll is 0%. Maps: a content-free town for the creature casts and Village Arena (MapPvP 1) for the
/// self-casts; no other class uses either.</para>
/// </summary>
[Collection("world")]
public sealed class SparkFamilyBaseTests
{
    /// <summary>Content-free: no Maps.csv row, so PvP is off and nothing spawns.</summary>
    private const ushort Town = 62620;
    /// <summary>Village Arena (Maps.csv MapPvP 1, MapSpells 1).</summary>
    private const ushort Arena = 2592;

    private const ushort CX = 10, CY = 10;   // the caster stands here, facing east at (CX + 1, CY)
    private const int CreatureHp = 10_000_000;
    private const uint Mana = 50_000, CasterHp = 100_000;

    /// <summary>The seven rows that cast through the Damage archetype.</summary>
    public static readonly string[] SparkRows =
    {
        "spark_mage", "glimpse_of_the_void_mage", "natures_ire_mage",
        "spark_poet", "glimpse_of_the_void_poet", "bolt_poet", "natures_ire_poet",
    };

    private readonly SessionFixture _fx;
    private static int _serial;

    public SparkFamilyBaseTests(SessionFixture fx) => _fx = fx;

    /// <summary>The live readings against creatures, with Will set well away from level so the old
    /// <c>(level + will)/4</c> cannot pass by accident. Level 11 and 13 give a base of 55.5 and 56.5, and only the
    /// carried half reaches 111, 101 and 113.
    /// <para>Red on 6de1d24: 106, 97, 108 and 70 (the floored row at Will 3, and at Will 30 for level 50).</para></summary>
    public static TheoryData<string, int, int, int, int> CreatureReadings
    {
        get
        {
            var data = new TheoryData<string, int, int, int, int>();
            foreach (var key in SparkRows)
            {
                data.Add(key, 11, 3, 100, 111);   // live: level 11 vs a rabbit (+100)
                data.Add(key, 13, 3, 80, 101);    // live: level 13 vs a deer, doe or cat (+80)
                data.Add(key, 13, 3, 100, 113);   // live: level 13 vs a rabbit
                data.Add(key, 50, 30, 0, 75);     // the issue's case: level 50, Will 30, AC 0 (was 70)
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(CreatureReadings))]
    public void ASparkFamilyCastLandsTheLiveBaseOnACreature(string key, int level, int will, int ac, int expected)
    {
        var sp = Spell(key);
        var mob = Creature(Town, ac);
        var (caster, _, cc) = Caster(Town, level, will, ownAc: 0, sp);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));

            Assert.Equal(Mana - 20, cc.Mp);   // the row's 20 mana: the cast went through the archetype
            Assert.Equal(expected, CreatureHp - mob.Hp);
        }
        finally
        {
            _fx.World.LeaveMap(caster, Town);
            _fx.World.DespawnMob(Town, mob);
        }
    }

    /// <summary>The probe's self-casts: the caster's own AC netted through the player intake
    /// (<c>ReceiveSpellDamage</c>, <c>DamageIntake</c>), which took an int until now. Level 15 at AC 85 is the case the
    /// decision sheet names: 57.5 x 1.85 = 106.375, read live as 106. Level 16 has no half and must not move.
    /// <para>Red on 6de1d24: 99 at both levels (the floored row at Will 3).</para></summary>
    [Theory]
    [InlineData("spark_mage", 15, 106)]
    [InlineData("spark_poet", 15, 106)]
    [InlineData("spark_mage", 16, 107)]
    public void ASparkSelfCastNetsTheCarriedHalfAgainstTheCastersOwnArmour(string key, int level, int expected)
    {
        var sp = Spell(key);
        var (caster, _, cc) = Caster(Arena, level, will: 3, ownAc: 85, sp);
        try
        {
            Assert.True(Content.IsPvpMap(Arena));
            caster.Receive(SpellCastSupport.CastFrame(0));

            Assert.Equal(Mana - 20, cc.Mp);
            Assert.Equal(expected, (int)(CasterHp - cc.Hp));
        }
        finally
        {
            _fx.World.LeaveMap(caster, Arena);
        }
    }

    /// <summary>Will does not move the base: two level-30 casters 40 Will apart land the same figure. The
    /// probe: "a +1 will item changed nothing"; the decision (Caleb, 2026-10-21, decision sheet item 13) takes
    /// level/2 alone.
    /// <para>Red on 6de1d24: 60 and 70 (the old row moved by a quarter of the Will).</para></summary>
    [Fact]
    public void WillDoesNotMoveTheBase()
    {
        int[] landed = new int[2];
        int i = 0;
        foreach (int will in new[] { 10, 50 })
        {
            var sp = Spell("spark_mage");
            var mob = Creature(Town, ac: 0);
            var (caster, _, _) = Caster(Town, level: 30, will, ownAc: 0, sp);
            try
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                landed[i++] = CreatureHp - mob.Hp;
            }
            finally
            {
                _fx.World.LeaveMap(caster, Town);
                _fx.World.DespawnMob(Town, mob);
            }
        }
        Assert.Equal(new[] { 65, 65 }, landed);
    }

    /// <summary>Mage Bolt is not on the Spark row and this change leaves it there: <c>game-data/SpellParams.csv</c>
    /// binds <c>bolt_mage</c> to <c>verbs.magic_damage</c>, 2 + 1.5 x Will for 4 mana. At level 50, Will 30 and AC 0
    /// that is 47 (the Spark row would give 75 for 20 mana). Moving Bolt onto the Spark row is held for Caleb.</summary>
    [Fact]
    public void MageBoltStillCastsItsOwnVerb()
    {
        var sp = Spell("bolt_mage");
        var mob = Creature(Town, ac: 0);
        var (caster, _, cc) = Caster(Town, level: 50, will: 30, ownAc: 0, sp);
        try
        {
            Assert.Equal("magic_damage", Content.SpellParams["bolt_mage"]["verb"]);
            caster.Receive(SpellCastSupport.CastFrame(0));

            Assert.Equal(Mana - 4, cc.Mp);
            Assert.Equal(47, CreatureHp - mob.Hp);
        }
        finally
        {
            _fx.World.LeaveMap(caster, Town);
            _fx.World.DespawnMob(Town, mob);
        }
    }

    /// <summary>Bolt keeps its own formula, but its half now reaches the armour step like every other path's (#78:
    /// one floor). At an odd Will, 2 + 1.5 x Will ends in .5: Will 31 is 48.5, which lands 97 against AC +100, and
    /// Will 33 is 51.5, which lands 51 against AC 0. <c>ctx:damage</c> used to round it first, half to even, to 48
    /// and 52, so it landed 96 and 52: up one at one Will, down one at the other.</summary>
    [Theory]
    [InlineData(31, 100, 97)]
    [InlineData(33, 0, 51)]
    public void MageBoltsOddWillHalfReachesTheArmourStep(int will, int ac, int expected)
    {
        var sp = Spell("bolt_mage");
        var mob = Creature(Town, ac);
        var (caster, _, cc) = Caster(Town, level: 50, will, ownAc: 0, sp);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));

            Assert.Equal(Mana - 4, cc.Mp);
            Assert.Equal(expected, CreatureHp - mob.Hp);
        }
        finally
        {
            _fx.World.LeaveMap(caster, Town);
            _fx.World.DespawnMob(Town, mob);
        }
    }

    // ===== helpers =======================================================================================

    private static SpellDef Spell(string key)
    {
        var sp = Content.SpellByKey(key);
        Assert.NotNull(sp);
        return sp!;
    }

    /// <summary>A creature at the caster's faced tile with <paramref name="ac"/>, no Will and no Protection, and an
    /// HP pool no cast here can empty.</summary>
    private Mob Creature(ushort map, int ac)
    {
        Assert.Null(_fx.World.MobAt(map, CX + 1, CY));
        var mob = new Mob(_fx.World.AllocateMobId(), 1, CX + 1, CY, $"Sfb creature {Interlocked.Increment(ref _serial)}",
                          CreatureHp) { Ac = ac };
        _fx.World.AddMob(map, mob);
        return mob;
    }

    /// <summary>A caster of <paramref name="level"/> and <paramref name="will"/>, with <paramref name="ownAc"/>, facing
    /// east, knowing only <paramref name="sp"/> (slot 0), with mana and HP to spare.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Caster(
        ushort map, int level, int will, int ownAc, SpellDef sp) =>
        _fx.PlayerWith($"SfbCaster{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = (byte)level;
            ch.Will = (byte)will;
            ch.Ac = (sbyte)ownAc;
            ch.MaxHp = CasterHp; ch.Hp = CasterHp;
            ch.MaxMp = Mana; ch.Mp = Mana;
            ch.Dir = 1;
            if (Content.Maps.TryGetValue(map, out var m) && m.Xs > 0) { ch.MapXs = m.Xs; ch.MapYs = m.Ys; }
            else { ch.MapXs = 100; ch.MapYs = 100; }
            ch.Spells.Add(sp.Id);
        }, map, CX, CY);
}
