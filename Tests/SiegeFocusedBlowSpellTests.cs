using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Siege and Focused Blow hit, cost and wait what the Nexus Atlas pages say (#328; owner, 2026-10-21, decision sheet
/// item 6).
///
/// <para>Siege (<c>siege_warrior</c>), its three alignment names (<c>souls_freedom_warrior</c>,
/// <c>lifes_end_warrior</c>, <c>winter_chill_warrior</c>) and Focused Blow (<c>focused_blow_rogue</c>) are in
/// <c>Content.SacrificeAliases</c>, so <c>Session.ApplyCast</c> sends them to <c>verbs.sacrifice</c>
/// (<c>Tests/Fixtures/spell-dispatch-routes.txt</c> keeps all five on <c>verb=sacrifice</c>). The verb had branches
/// for Lethal Strike, Desperate Attack, Berserk and Whirlwind only, and these two fell to its <c>else</c> arms: 60
/// mana, a 60 s wait, 0 damage (which the hit raises to 1) and no vita cost. Silent: a player paid and waited for a
/// level-99 strike that did 1 damage.</para>
///
/// <para>The numbers, all from the Atlas (RTK has no script for either spell):</para>
/// <list type="bullet">
/// <item><b>Focused Blow</b>, the live rogue page: "Takes 2/3 of current Vita in a Strong Attack. The attack does 2 times
/// current vitality in damage at 0 AC", Mana Cost 3,000, Aethers 38 Seconds.</item>
/// <item><b>Siege</b>, the live warrior page fetched 2026-10-21: "leaves the caster with 25% vita left. Damage to
/// target is 1.875 times current vitality plus 0.5 current mana at 0 AC", Mana Cost 8,000, Aethers 43 Seconds; its
/// Kwi-Sin, Ming-Ken and Ohaeng names are Soul's freedom, Life's end and Winter chill.</item>
/// </list>
/// <para>"Current" is the caster's vita and mana as the strike is cast, before its own mana comes out, as the spell
/// rows' formulas read them (<c>game-data/spell_effects.csv</c>). The vita left is rounded up, as the family's other
/// strikes round it, so a strike never takes the last point.</para>
///
/// <para>Driven end to end: the real 0x0F frame through <c>Session.Receive</c>, at a creature with 0 AC on the faced
/// tile, on content-free maps no other class uses.</para>
/// </summary>
[Collection("world")]
public sealed class SiegeFocusedBlowSpellTests
{
    private const ushort StrikeMap = 62602, ShortMap = 62603;
    private const ushort CX = 10, CY = 10;      // the caster, facing east at (CX + 1, CY)
    private const int Creature = 10_000_000;

    /// <summary>Slack on an aether read straight after the cast.</summary>
    private const long SlackMs = 5_000;

    private readonly SessionFixture _fx;
    private static int _serial;

    public SiegeFocusedBlowSpellTests(SessionFixture fx) => _fx = fx;

    /// <summary>The five keys and the mana and aether the Atlas gives each.</summary>
    public static TheoryData<string, int, int> Strikes => new()
    {
        { "focused_blow_rogue",    3_000, 38_000 },
        { "siege_warrior",         8_000, 43_000 },
        { "souls_freedom_warrior", 8_000, 43_000 },
        { "lifes_end_warrior",     8_000, 43_000 },
        { "winter_chill_warrior",  8_000, 43_000 },
    };

    /// <summary>A strike at a 0-AC creature on the faced tile takes the Atlas's damage off it, leaves the caster the
    /// Atlas's share of their vita, takes the Atlas's mana and arms the Atlas's aether. The caster holds 9,001 vita
    /// and 20,001 mana, so every rounding shows: Focused Blow hits for 18,002 and leaves 3,001; Siege hits for
    /// ceil(9,001 x 1.875) + ceil(20,001 x 0.5) = 16,877 + 10,001 = 26,878 and leaves ceil(9,001 / 4) = 2,251. Red on
    /// 6de1d24: 1 damage, 9,001 vita kept, 60 mana and a 60 s aether.</summary>
    [Theory]
    [MemberData(nameof(Strikes))]
    public void AStrikeHitsCostsAndWaitsWhatTheAtlasSays(string key, int mana, int aetherMs)
    {
        const uint Hp = 9_001, Mp = 20_001;
        var sp = Content.SpellByKey(key);
        Assert.NotNull(sp);
        bool siege = Content.SacrificeFamilyFor(sp!) == Content.SacrificeFamily.Siege;
        Assert.True(siege || Content.SacrificeFamilyFor(sp!) == Content.SacrificeFamily.FocusedBlow);
        long damage = siege ? (long)Math.Ceiling(Hp * 1.875) + (long)Math.Ceiling(Mp * 0.5) : Hp * 2L;
        uint hpLeft = siege ? (uint)Math.Ceiling(Hp / 4.0) : (uint)Math.Ceiling(Hp / 3.0);

        var mob = new Mob(_fx.World.AllocateMobId(), 1, CX + 1, CY, $"Siege target {Interlocked.Increment(ref _serial)}", Creature);
        Assert.Equal(0, mob.Ac);
        _fx.World.AddMob(StrikeMap, mob);
        var (session, outbound, c) = Caster(sp!, StrikeMap, Hp, Mp);
        try
        {
            session.Receive(SpellCastSupport.CastFrame(0));

            Assert.Contains($"You cast {sp!.Name}.", SpellCastSupport.MiniTexts(outbound));
            long aether = SpellCastSupport.AetherLeft(session, key);
            string what = $"{sp.Name}: creature lost {Creature - mob.Hp} (want {damage}), caster vita {c.Hp} (want " +
                          $"{hpLeft}), mana {c.Mp} (want {Mp - mana}), aether {aether} ms (want {aetherMs})";
            Assert.True(Creature - mob.Hp == damage && c.Hp == hpLeft && c.Mp == Mp - (uint)mana
                        && aether > aetherMs - SlackMs && aether <= aetherMs, what);
        }
        finally
        {
            _fx.World.LeaveMap(session, StrikeMap);
            _fx.World.DespawnMob(StrikeMap, mob);
        }
    }

    /// <summary>One mana short of the Atlas's cost, the strike is refused for free: "You do not have enough mana.",
    /// no line saying it was cast, and the creature, the caster's vita and mana, and the aether as they were. Red on
    /// 6de1d24, where the cost was 60 and the strike went off.</summary>
    [Theory]
    [MemberData(nameof(Strikes))]
    public void OneManaShortTheStrikeIsRefusedForFree(string key, int mana, int aetherMs)
    {
        const uint Hp = 9_001;
        uint mp = (uint)mana - 1;
        var sp = Content.SpellByKey(key)!;
        var mob = new Mob(_fx.World.AllocateMobId(), 1, CX + 1, CY, $"Siege target {Interlocked.Increment(ref _serial)}", Creature);
        _fx.World.AddMob(ShortMap, mob);
        var (session, outbound, c) = Caster(sp, ShortMap, Hp, mp);
        try
        {
            session.Receive(SpellCastSupport.CastFrame(0));

            var lines = SpellCastSupport.MiniTexts(outbound);
            long aether = SpellCastSupport.AetherLeft(session, key);
            string what = $"{sp.Name} on {mp} mana (its aether is {aetherMs} ms): lines [{string.Join(" | ", lines)}], " +
                          $"creature lost {Creature - mob.Hp}, vita {c.Hp}, mana {c.Mp}, aether {aether} ms";
            Assert.True(lines.Contains("You do not have enough mana.") && !lines.Contains($"You cast {sp.Name}.")
                        && mob.Hp == Creature && c.Hp == Hp && c.Mp == mp && aether == 0, what);
        }
        finally
        {
            _fx.World.LeaveMap(session, ShortMap);
            _fx.World.DespawnMob(ShortMap, mob);
        }
    }

    /// <summary>A level-99 caster at (CX, CY) facing east, on a 100x100 content-free map, with
    /// <paramref name="sp"/> in book slot 0.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Caster(SpellDef sp, ushort map, uint hp, uint mp) =>
        _fx.PlayerWith($"SiegeCaster{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 99;
            ch.MaxHp = hp; ch.Hp = hp;
            ch.MaxMp = mp; ch.Mp = mp;
            ch.Dir = 1;
            ch.MapXs = 100; ch.MapYs = 100;
            ch.Spells.Add(sp.Id);
        }, map, CX, CY);
}
