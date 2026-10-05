using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The Inferno ladder takes the whole pool and arms its 70 s aether (Caleb, 2026-09-29, "1 yes").
///
/// <para>Inferno and its three alignment reskins (Death's Door, Nature's Denial, Steel Storm) are target-area
/// zaps: <c>Session.ApplyCast</c> sends them to the <c>target_area_zap</c> verb. That branch used to return
/// straight out, ahead of the pre-cast pool read, the post-cast drain (<c>Content.PostCastManaDrainFor</c>) and
/// the aether arm the archetype path shares, so the ladder charged only the verb's 5 mana and could be recast at
/// once: a free nuke scaling off a pool that never moved, with its 70 s wait never started. RTK's
/// <c>Spells/mage/inferno.lua</c> ends every one of the four casts with <c>setAether(&lt;key&gt;, 70000)</c> and
/// <c>player.magic = 0</c>; their <c>spell_effects.csv</c> rows carry the 70000 aether.</para>
///
/// <para>The branch now feeds the same tail as the archetypes, gated on the same verdict: the verb's own. A cast
/// the verb refuses (not enough mana) takes and arms nothing. A cast the verb accepts is a cast even when the
/// sweep reaches nobody, because <c>verbs.target_area_zap</c> treats an empty sweep as a cast ("0 hits is
/// fine"); RTK's script does the same, arming the aether and zeroing the pool after the sweep whatever it
/// hit. The other seven 5-way spells (the dog fire family, Volcanic Blast, the Earthquake ladder) carry no drain
/// entry and no aether, so the shared tail leaves them exactly as they were.</para>
///
/// <para>Driven end to end through <c>Session.Receive</c> on content-free maps, at a creature with ten million
/// hit points beside the caster so every sweep that should land does land and nothing dies.</para>
/// </summary>
[Collection("world")]
public sealed class InfernoLadderSpellTests
{
    private const ushort LandedMap = 62010, RefusedMap = 62011, EmptyMap = 62012, OtherMap = 62013;

    private const uint Pool = 40_000;

    private readonly SessionFixture _fx;
    private static int _serial;

    public InfernoLadderSpellTests(SessionFixture fx) => _fx = fx;

    public static TheoryData<string> Ladder => new()
    {
        "inferno_mage", "deaths_door_mage", "natures_denial_mage", "steel_storm_mage",
    };

    /// <summary>Every other <c>Content.IsTargetAreaZap</c> spell.</summary>
    public static TheoryData<string> OtherFiveWays => new()
    {
        "fissure", "lava_surge", "volcanic_blast_mage",
        "earthquake_poet", "tossing_the_bones_poet", "natures_fury_poet", "groundstrike_poet",
    };

    /// <summary>A cast that lands empties the pool and arms the row's 70 s aether. Red on 9c00b98: the pool is
    /// down only the verb's 5 and no aether is running.</summary>
    [Theory]
    [MemberData(nameof(Ladder))]
    public void ALandedCastTakesTheWholePoolAndArmsTheSeventySecondAether(string key)
    {
        var sp = Content.SpellByKey(key)!;
        Assert.Equal(70_000, Content.FxFor(sp)!.Aether);
        var (session, outbound, c) = Caster(sp, LandedMap, Pool);
        var mob = Target(LandedMap);
        try
        {
            session.Receive(SpellCastSupport.CastFrame(0, mob.Id));

            Assert.Contains($"You cast {sp.Name}.", SpellCastSupport.MiniTexts(outbound));
            Assert.True(mob.Hp < mob.MaxHp, $"{sp.Name} never reached the creature beside the caster");
            Assert.Equal(0u, c.Mp);
            Assert.InRange(SpellCastSupport.AetherLeft(session, key), 60_000, 70_000);
        }
        finally
        {
            _fx.World.DespawnMob(LandedMap, mob);
            _fx.World.LeaveMap(session, LandedMap);
        }
    }

    /// <summary>A cast the verb refuses takes nothing and arms nothing, exactly as a refused archetype cast:
    /// 3 mana is short of the 5 the verb charges, so it answers "You do not have enough mana." and the pool and
    /// the aether are untouched. Green on 9c00b98 too (it returned before both); it is here so the shared tail's
    /// <c>ok</c> gate has a fact that fails without it.</summary>
    [Theory]
    [MemberData(nameof(Ladder))]
    public void ACastTheVerbRefusesTakesNothingAndArmsNothing(string key)
    {
        var sp = Content.SpellByKey(key)!;
        var (session, outbound, c) = Caster(sp, RefusedMap, 3);
        var mob = Target(RefusedMap);
        try
        {
            session.Receive(SpellCastSupport.CastFrame(0, mob.Id));

            var lines = SpellCastSupport.MiniTexts(outbound);
            Assert.Contains("You do not have enough mana.", lines);
            Assert.DoesNotContain($"You cast {sp.Name}.", lines);
            Assert.Equal(mob.MaxHp, mob.Hp);
            Assert.Equal(3u, c.Mp);
            Assert.Equal(0L, SpellCastSupport.AetherLeft(session, key));
        }
        finally
        {
            _fx.World.DespawnMob(RefusedMap, mob);
            _fx.World.LeaveMap(session, RefusedMap);
        }
    }

    /// <summary>Aimed at nothing, the sweep centres on the caster and finds nobody, and the verb still accepts
    /// it as a cast: "You cast X." and the verb's 5 mana, as on 9c00b98. So it now also costs the whole pool and
    /// arms the aether, as in RTK's <c>inferno.lua</c>, whose aether and <c>player.magic = 0</c> follow the
    /// sweep whatever it hit. Red on 9c00b98: the pool is down 5 and no aether is running.</summary>
    [Theory]
    [MemberData(nameof(Ladder))]
    public void ACastThatReachesNobodyIsStillACast(string key)
    {
        var sp = Content.SpellByKey(key)!;
        var (session, outbound, c) = Caster(sp, EmptyMap, Pool);
        try
        {
            session.Receive(SpellCastSupport.CastFrame(0));

            Assert.Contains($"You cast {sp.Name}.", SpellCastSupport.MiniTexts(outbound));
            Assert.Equal(0u, c.Mp);
            Assert.InRange(SpellCastSupport.AetherLeft(session, key), 60_000, 70_000);
        }
        finally
        {
            _fx.World.LeaveMap(session, EmptyMap);
        }
    }

    /// <summary>The rest of the 5-way family is unchanged: a landed cast costs the row's flat mana and nothing
    /// more, and arms no aether. Green on 9c00b98 and after.</summary>
    [Theory]
    [MemberData(nameof(OtherFiveWays))]
    public void TheOtherFiveWaysCostTheirFlatManaAndArmNoAether(string key)
    {
        var sp = Content.SpellByKey(key)!;
        Assert.True(Content.IsTargetAreaZap(sp));
        int mana = Content.FxFor(sp)!.Mana;
        Assert.True(mana > 0, $"{key} has no flat cost in its row");
        var (session, outbound, c) = Caster(sp, OtherMap, Pool);
        var mob = Target(OtherMap);
        try
        {
            session.Receive(SpellCastSupport.CastFrame(0, mob.Id));

            Assert.Contains($"You cast {sp.Name}.", SpellCastSupport.MiniTexts(outbound));
            Assert.True(mob.Hp < mob.MaxHp, $"{sp.Name} never reached the creature beside the caster");
            Assert.Equal(Pool - (uint)mana, c.Mp);
            Assert.Equal(0L, SpellCastSupport.AetherLeft(session, key));
        }
        finally
        {
            _fx.World.DespawnMob(OtherMap, mob);
            _fx.World.LeaveMap(session, OtherMap);
        }
    }

    /// <summary>A level-99 caster at (5,5) with <paramref name="sp"/> in book slot 0.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Caster(SpellDef sp, ushort map, uint mp)
    {
        int n = Interlocked.Increment(ref _serial);
        return _fx.PlayerWith($"Inferno{n}", ch =>
        {
            ch.Level = 99;
            ch.MaxHp = 1_000; ch.Hp = 1_000;
            ch.MaxMp = Pool; ch.Mp = mp;
            ch.Spells.Add(sp.Id);
        }, map, x: 5, y: 5);
    }

    /// <summary>A creature on the tile east of the caster, inside every sweep's reach and too big to kill.</summary>
    private Mob Target(ushort map)
    {
        var mob = new Mob(_fx.World.AllocateMobId(), 1, 6, 5, "Inferno target", 10_000_000);
        _fx.World.AddMob(map, mob);
        return mob;
    }
}
