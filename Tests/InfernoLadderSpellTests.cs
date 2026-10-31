using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The Inferno ladder takes the whole pool and arms its 70 s aether (owner, 2026-09-29, "1 yes").
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
/// <para>The 600-mana gate (owner, 2026-10-21, decision sheet item 2). The same RTK body refuses a cast below 600
/// mana before it sweeps anything (<c>inferno.lua:11-14</c>, "You do not have enough mana."), and the live Nexus Atlas
/// mage page gives Inferno "Mana Cost - 600". The four rows said 0, which the cast reads as 5, so a 50-mana Inferno
/// fired and started the full 70 s wait. The rows now say 600: the verb refuses below it for free, with no aether,
/// and from it the verb's 600 and then the drain leave the pool at 0, as before.</para>
///
/// <para>Driven end to end through <c>Session.Receive</c> on content-free maps, at a creature with ten million
/// hit points beside the caster so every sweep that should land does land and nothing dies.</para>
/// </summary>
[Collection("world")]
public sealed class InfernoLadderSpellTests
{
    private const ushort LandedMap = 62010, RefusedMap = 62011, EmptyMap = 62012, OtherMap = 62013, GateMap = 62600;

    private const uint Pool = 40_000;

    /// <summary>The least a cast of the ladder needs (RTK <c>inferno.lua:11</c>; the rows' mana).</summary>
    private const uint Gate = 600;

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
            var (pool, aether) = (c.Mp, SpellCastSupport.AetherLeft(session, key));
            Assert.True(pool == 0 && aether is >= 60_000 and <= 70_000,
                        $"pool {pool} (want 0), aether {aether} ms left (want 60000 to 70000)");
        }
        finally
        {
            _fx.World.DespawnMob(LandedMap, mob);
            _fx.World.LeaveMap(session, LandedMap);
        }
    }

    /// <summary>A cast below the 600-mana gate is refused and takes and arms nothing, exactly as a refused archetype
    /// cast: 599 mana is short of the row's 600, so the verb answers "You do not have enough mana.", the creature
    /// beside the caster keeps every point, and the pool and the aether are untouched. It is also the shared tail's
    /// <c>ok</c> gate's fact: a refused cast that reached the tail would take the pool and arm the aether. Red on
    /// 6de1d24: 599 clears the 5 the cast charged for a row saying 0, so it fires, empties the pool and arms the 70 s
    /// wait.</summary>
    [Theory]
    [MemberData(nameof(Ladder))]
    public void ACastBelowTheSixHundredManaGateIsRefusedForFree(string key)
    {
        var sp = Content.SpellByKey(key)!;
        var (session, outbound, c) = Caster(sp, RefusedMap, Gate - 1);
        var mob = Target(RefusedMap);
        try
        {
            session.Receive(SpellCastSupport.CastFrame(0, mob.Id));

            var lines = SpellCastSupport.MiniTexts(outbound);
            Assert.Contains("You do not have enough mana.", lines);
            Assert.DoesNotContain($"You cast {sp.Name}.", lines);
            Assert.Equal(mob.MaxHp, mob.Hp);
            var (pool, aether) = (c.Mp, SpellCastSupport.AetherLeft(session, key));
            Assert.True(pool == Gate - 1 && aether == 0,
                        $"pool {pool} (want {Gate - 1}), aether {aether} ms left (want 0)");
        }
        finally
        {
            _fx.World.DespawnMob(RefusedMap, mob);
            _fx.World.LeaveMap(session, RefusedMap);
        }
    }

    /// <summary>The gate's other side: exactly 600 mana casts, reaches the creature, ends at 0 and arms the 70 s
    /// aether, as RTK's <c>player.magic &lt; 600</c> lets 600 through. Green on 6de1d24 too; it pins the boundary, so
    /// a gate written as "at most 600" fails here.</summary>
    [Theory]
    [MemberData(nameof(Ladder))]
    public void ACastOnExactlySixHundredManaLands(string key)
    {
        var sp = Content.SpellByKey(key)!;
        var (session, outbound, c) = Caster(sp, GateMap, Gate);
        var mob = Target(GateMap);
        try
        {
            session.Receive(SpellCastSupport.CastFrame(0, mob.Id));

            Assert.Contains($"You cast {sp.Name}.", SpellCastSupport.MiniTexts(outbound));
            Assert.True(mob.Hp < mob.MaxHp, $"{sp.Name} never reached the creature beside the caster");
            var (pool, aether) = (c.Mp, SpellCastSupport.AetherLeft(session, key));
            Assert.True(pool == 0 && aether is >= 60_000 and <= 70_000,
                        $"pool {pool} (want 0), aether {aether} ms left (want 60000 to 70000)");
        }
        finally
        {
            _fx.World.DespawnMob(GateMap, mob);
            _fx.World.LeaveMap(session, GateMap);
        }
    }

    /// <summary>Aimed at nothing, the sweep centres on the caster and finds nobody, and the verb still accepts
    /// it as a cast: "You cast X." and the row's mana (5 on 9c00b98, 600 now). So it also costs the whole pool and
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
            var (pool, aether) = (c.Mp, SpellCastSupport.AetherLeft(session, key));
            Assert.True(pool == 0 && aether is >= 60_000 and <= 70_000,
                        $"pool {pool} (want 0), aether {aether} ms left (want 60000 to 70000)");
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
            var (pool, aether) = (c.Mp, SpellCastSupport.AetherLeft(session, key));
            Assert.True(pool == Pool - (uint)mana && aether == 0,
                        $"pool {pool} (want {Pool - (uint)mana}), aether {aether} ms left (want 0)");
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
