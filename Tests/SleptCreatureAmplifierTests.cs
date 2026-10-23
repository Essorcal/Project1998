using System.Reflection;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// #363: a slept or dozed creature's amplifier goes inside the hit's one floor. #78 put every hit on one rule
/// (multiply the terms, round down once, at least 1, then double a hit from behind), and a creature's own
/// sleep-family amplifier was the last term outside it: <c>World.TryDamage</c> received the attacker's figure already
/// floored (and doubled from behind) and multiplied it by the amplifier, rounding half to even, so the first hit on a
/// slept (1.5x) or dozed (1.3x) creature landed a point or two off the rule, more from behind. RTK multiplies
/// by <c>target.sleep</c> before its one floor (<c>swingDamage.lua:92</c>, floor <c>:108</c>, the rear x2
/// <c>:147-150</c>). Now every creature damage site hands <c>TryDamage</c> its figure unfloored, and <c>TryDamage</c>
/// multiplies the amplifier in, floors once and doubles from behind, under the lock that consumes the amplifier.
///
/// <para>The amplifier is armed with <c>World.ArmDamageAmp</c>, the call <c>Session.LuaAmplify</c> makes for a creature
/// once a hold lands (<c>arch_debuff</c>'s last step), at the data's figures: 1.5 for the Sleep family
/// (<c>AMP_BY_SPELL</c>) and 1.3 for the Doze family (<c>HOLD.sleep.amp</c>), <c>game-data/spell_verbs.lua</c>. Swings
/// are real 0x13 frames and casts real 0x0F frames through <c>Session.Receive</c>; the area and sacrifice sites, which
/// a real cast reaches only through randomness or era gates, are called directly with a whole amount. Every figure
/// below is red on 2874006, the master this branch starts from.</para>
///
/// <para>Map 62630 is content-free (no Maps.csv row: PvP off, nothing spawns) and no other class uses it.</para>
/// </summary>
[Collection("world")]
public sealed class SleptCreatureAmplifierTests
{
    private const ushort Field = 62630;
    private const ushort CX = 10, CY = 10;   // the attacker stands here, facing east at the creature on (CX + 1, CY)
    private const int CreatureHp = 10_000_000;
    private const uint PlayerHp = 100_000, Mana = 50_000;
    private const int Swings = 400;

    /// <summary>The Sleep family's amplifier (sleep_mage and its three twins).</summary>
    private const double Slept = 1.5;
    /// <summary>The Doze family's amplifier (the `sleep` hold's default).</summary>
    private const double Dozed = 1.3;

    private static readonly FieldInfo NextActionTick =
        typeof(Session).GetField("_nextActionTick", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly SessionFixture _fx;
    private static int _serial;

    public SleptCreatureAmplifierTests(SessionFixture fx) => _fx = fx;

    /// <summary>A Peasant's bare-hand swing (4.5 or 5.0, three times that on a crit; see <see cref="Swinger"/>) at a
    /// slept or dozed creature, from in front (the creature faces the attacker) and from behind (both face east, the
    /// attacker to its west). Each landed hit must be one of the one rule's four figures and a non-crit one must land:
    /// <list type="bullet">
    /// <item>Slept, AC +75, in front: 4.5 x 1.75 x 1.5 = 11.8 and 5.0 x 1.75 x 1.5 = 13.1, so 11 or 13 (35 or 39 on a
    /// crit). Red on 2874006: 10 or 12; the swing floored 7.875 to 7 and TryDamage rounded 7 x 1.5 = 10.5 to the even
    /// 10, and 8 x 1.5 to 12.</item>
    /// <item>Slept, AC +75, from behind: the same floors doubled, 22 or 26 (70 or 78). Red: 21 or 24 (14 x 1.5, and
    /// 16 x 1.5).</item>
    /// <item>Dozed, AC +20, in front: 5.4 x 1.3 = 7.02 and 6.0 x 1.3 = 7.8, so 7 either way (21 or 23). Red: 6 or 8
    /// (5 x 1.3 = 6.5 to the even 6, and 6 x 1.3 = 7.8 to 8).</item>
    /// <item>Dozed, AC +20, from behind: 14 (42 or 46). Red: 13 or 16 (10 x 1.3, and 12 x 1.3 = 15.6).</item>
    /// <item>No amplifier, AC +75: 7 or 8 in front (23 or 26) and 14 or 16 from behind (46 or 52), which is what the
    /// same swing lands on 2874006: an un-slept hit is unchanged.</item>
    /// </list></summary>
    [Theory]
    [InlineData(Slept, 75, false, new[] { 11, 13 }, new[] { 11, 13, 35, 39 })]
    [InlineData(Slept, 75, true, new[] { 22, 26 }, new[] { 22, 26, 70, 78 })]
    [InlineData(Dozed, 20, false, new[] { 7 }, new[] { 7, 21, 23 })]
    [InlineData(Dozed, 20, true, new[] { 14 }, new[] { 14, 42, 46 })]
    [InlineData(1.0, 75, false, new[] { 7, 8 }, new[] { 7, 8, 23, 26 })]
    [InlineData(1.0, 75, true, new[] { 14, 16 }, new[] { 14, 16, 46, 52 })]
    public void ASwingTakesTheCreaturesAmplifierInsideItsOneFloorThenTheRearDouble(
        double amp, int ac, bool fromBehind, int[] nonCrit, int[] figures)
    {
        // Dir 1 is east, the way the attacker faces, so the attacker is behind; Dir 3 faces the attacker.
        var mob = Creature(ac, dir: fromBehind ? (byte)1 : (byte)3);
        var (attacker, _, _) = Swinger();
        try
        {
            var landed = new List<int>();
            for (int i = 0; i < Swings && !landed.Any(nonCrit.Contains); i++)
            {
                if (amp > 1.0) _fx.World.ArmDamageAmp(mob, amp, 60_000);   // a miss leaves it armed; a hit spends it
                int was = mob.Hp;
                attacker.WithState(() => NextActionTick.SetValue(attacker, 0L));
                attacker.Receive(SessionFixture.Frame(ClientOp.Attack, new byte[] { 0 }));
                if (mob.Hp != was) landed.Add(was - mob.Hp);
            }
            Assert.True(landed.Count > 0 && landed.All(figures.Contains) && landed.Any(nonCrit.Contains),
                        $"amp {amp}, AC {ac}, from behind {fromBehind}; landed: {string.Join(", ", landed)}");
        }
        finally
        {
            _fx.World.LeaveMap(attacker, Field);
            _fx.World.DespawnMob(Field, mob);
        }
    }

    /// <summary>A damage spell at a slept or dozed creature, one real cast. Spark (the Damage archetype,
    /// <c>ctx:magicDamage</c>, <c>Session.LuaMagicDamage</c>) at level 13 is 56.5, the live rabbit reading's 113 at
    /// AC +100; mage Bolt (<c>verbs.magic_damage</c>, <c>ctx:damage</c>, <c>Session.LuaDamageTarget</c>) at Will 31 is
    /// 48.5. The one rule floors the whole product; 2874006 floored the armour step and then rounded the amplified
    /// whole number half to even:
    /// <list type="bullet">
    /// <item>Spark, AC +100: 113 x 1.5 = 169.5 lands 169 (red 170); 113 x 1.3 = 146.9 lands 146 (red 147).</item>
    /// <item>Spark, AC +75: 98.875 x 1.5 = 148.3 lands 148 (red 98 x 1.5 = 147); x 1.3 = 128.5 lands 128 (red 98 x 1.3
    /// = 127.4, 127).</item>
    /// <item>Bolt, AC +75: 84.875 x 1.5 = 127.3 lands 127 (red 84 x 1.5 = 126); x 1.3 = 110.3 lands 110 (red 109).</item>
    /// </list></summary>
    [Theory]
    [InlineData("spark_mage", 13, 3, 100, Slept, 20, 169)]
    [InlineData("spark_mage", 13, 3, 100, Dozed, 20, 146)]
    [InlineData("spark_mage", 13, 3, 75, Slept, 20, 148)]
    [InlineData("spark_mage", 13, 3, 75, Dozed, 20, 128)]
    [InlineData("bolt_mage", 50, 31, 75, Slept, 4, 127)]
    [InlineData("bolt_mage", 50, 31, 75, Dozed, 4, 110)]
    public void ADamageSpellTakesTheCreaturesAmplifierInsideItsOneFloor(
        string key, int level, int will, int ac, double amp, int mana, int expected)
    {
        var sp = Spell(key);
        var mob = Creature(ac, dir: 3);
        var (caster, _, cc) = Caster(sp, level, will);
        try
        {
            _fx.World.ArmDamageAmp(mob, amp, 60_000);
            caster.Receive(SpellCastSupport.CastFrame(0));

            Assert.Equal(Mana - (uint)mana, cc.Mp);   // the cast went through its own verb
            Assert.Equal(expected, CreatureHp - mob.Hp);
        }
        finally
        {
            _fx.World.LeaveMap(caster, Field);
            _fx.World.DespawnMob(Field, mob);
        }
    }

    /// <summary>The amplifier is spent by exactly one hit, in the same hold of the world lock as the HP it changes: a
    /// slept rabbit takes a level-13 Spark for 169, and the next Spark for the plain 113, with nothing left armed.
    /// <para>Red on 2874006: the first lands 170.</para></summary>
    [Fact]
    public void TheAmplifierIsSpentByTheOneHitItAmplifies()
    {
        var sp = Spell("spark_mage");
        var mob = Creature(ac: 100, dir: 3);
        var (caster, _, cc) = Caster(sp, level: 13, will: 3);
        try
        {
            _fx.World.ArmDamageAmp(mob, Slept, 60_000);

            caster.Receive(SpellCastSupport.CastFrame(0));
            int first = CreatureHp - mob.Hp;
            double leftArmed = mob.DamageAmp;
            caster.WithState(() => NextActionTick.SetValue(caster, 0L));
            caster.Receive(SpellCastSupport.CastFrame(0));
            int second = CreatureHp - mob.Hp - first;

            Assert.Equal(Mana - 40, cc.Mp);   // both casts went through
            Assert.Equal((169, 113, 0.0), (first, second, leftArmed));
        }
        finally
        {
            _fx.World.LeaveMap(caster, Field);
            _fx.World.DespawnMob(Field, mob);
        }
    }

    /// <summary>The creature sites a real cast reaches only by a range roll, a vita formula or an era gate, called as
    /// their verbs call them, with a whole amount of 101 at AC -22 (0.78): the 4-way zap (<c>LuaAreaZap</c>, Erupt's
    /// verb), the dog 5-way (<c>LuaTargetAreaZap</c>, Fissure's, at a creature one tile away, inside its sure range)
    /// and the sacrifice strike (<c>LuaSacApply</c>, Berserk's). 78.78 x 1.5 = 118.2 lands 118 and 78.78 x 1.3 = 102.4
    /// lands 102.
    /// <para>Red on 2874006: 117 and 101 (the armour step's 78, then 78 x 1.5 and 78 x 1.3 = 101.4).</para></summary>
    [Theory]
    [InlineData("area", Slept, 118)]
    [InlineData("area", Dozed, 102)]
    [InlineData("target-area", Slept, 118)]
    [InlineData("target-area", Dozed, 102)]
    [InlineData("sacrifice", Slept, 118)]
    [InlineData("sacrifice", Dozed, 102)]
    public void TheAreaAndSacrificeSitesTakeTheCreaturesAmplifierInsideTheirOneFloor(string site, double amp, int expected)
    {
        var mob = Creature(ac: -22, dir: 3);
        var (caster, _, _) = Caster(Spell("spark_mage"), level: 13, will: 3);
        try
        {
            _fx.World.ArmDamageAmp(mob, amp, 60_000);
            caster.WithState(() =>
            {
                switch (site)
                {
                    case "area": caster.LuaAreaZap(101, Spell("erupt_mage")); break;
                    case "target-area": caster.LuaTargetAreaZap(101, Spell("fissure"), mob.Id); break;
                    default: Assert.True(caster.LuaSacFrontMob()); caster.LuaSacApply(Spell("berserk_warrior"), 101); break;
                }
            });

            Assert.Equal(expected, CreatureHp - mob.Hp);
        }
        finally
        {
            _fx.World.LeaveMap(caster, Field);
            _fx.World.DespawnMob(Field, mob);
        }
    }

    /// <summary>A whole-number blow (a trap's, a poison tick's, a pet's swing, a harvest swing: the figures that reach
    /// <c>World.TryDamage</c> with no armour term of their own) takes the amplifier inside the same floor: 333 on a
    /// slept creature is 499.5 and lands 499, on a dozed one 432.9 and lands 432, and with none armed it lands 333.
    /// <para>Red on 2874006: 500 and 433 (499.5 to the even 500, and 432.9 to the nearest, 433).</para></summary>
    [Theory]
    [InlineData(Slept, 499)]
    [InlineData(Dozed, 432)]
    [InlineData(1.0, 333)]
    public void AWholeNumberBlowTakesTheAmplifierInsideTheSameFloor(double amp, int expected)
    {
        var mob = Creature(ac: 0, dir: 3);
        try
        {
            if (amp > 1.0) _fx.World.ArmDamageAmp(mob, amp, 60_000);
            Assert.True(_fx.World.TryDamage(Field, mob, 333, out bool died) && !died);
            Assert.Equal(expected, CreatureHp - mob.Hp);
        }
        finally
        {
            _fx.World.DespawnMob(Field, mob);
        }
    }

    // ===== helpers =======================================================================================

    private static SpellDef Spell(string key)
    {
        var sp = Content.SpellByKey(key);
        Assert.NotNull(sp);
        return sp!;
    }

    /// <summary>A creature on the attacker's faced tile with <paramref name="ac"/>, facing <paramref name="dir"/>, no
    /// Will and no Protection (so no deflect), and an HP pool nothing here can empty.</summary>
    private Mob Creature(int ac, byte dir)
    {
        Assert.Null(_fx.World.MobAt(Field, CX + 1, CY));
        var mob = new Mob(_fx.World.AllocateMobId(), 1, CX + 1, CY, $"Sca creature {Interlocked.Increment(ref _serial)}",
                          CreatureHp) { Ac = ac, Dir = dir };
        _fx.World.AddMob(Field, mob);
        return mob;
    }

    /// <summary>A level-99 Peasant of might 40 and grace 100 (the hit roll at its ceiling) with no gear, facing east:
    /// no class factor and no Dam, so the bare hand's swing is S/2 + floor(40/4)/2 - 1 = 4.5 or 5.0 (S is 1 or 2), and
    /// three times that on a crit (SwingSingleFloorTests' attacker).</summary>
    private (Session session, RecordingOutbound outbound, Character c) Swinger() =>
        _fx.PlayerWith($"ScaSwinger{Interlocked.Increment(ref _serial)}", c =>
        {
            c.Level = 99; c.Grace = 100; c.Might = 40; c.Dir = 1;
            c.MaxHp = PlayerHp; c.Hp = PlayerHp;
            c.MapXs = 100; c.MapYs = 100;
        }, Field, CX, CY);

    /// <summary>A caster of <paramref name="level"/> and <paramref name="will"/>, facing east, knowing only
    /// <paramref name="sp"/> (slot 0), with mana and HP to spare.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Caster(SpellDef sp, int level, int will) =>
        _fx.PlayerWith($"ScaCaster{Interlocked.Increment(ref _serial)}", c =>
        {
            c.Level = (byte)level; c.Will = (byte)will; c.Dir = 1;
            c.MaxHp = PlayerHp; c.Hp = PlayerHp;
            c.MaxMp = Mana; c.Mp = Mana;
            c.MapXs = 100; c.MapYs = 100;
            c.Spells.Add(sp.Id);
        }, Field, CX, CY);
}
