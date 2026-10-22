using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Wind master is not castable until a source names its creature (Caleb, 2026-10-21, decision sheet item 3).
///
/// <para>The Poet's Sam San summon (<c>cotw_wind_master_poet</c>) has no Pets.csv row: RTK has no script for it,
/// <c>mobs.csv</c> has no creature for it, and the live Nexus Atlas poet page describes it as "Summons a ??? vita
/// creature" (<c>game-data/Sources.csv</c> <c>atlas-poet-spells</c>). With no pet row the cast fell through to the
/// generic <c>misc</c> verb (<c>Tests/Fixtures/spell-dispatch-routes.txt</c>), which charged the row's 10 mana,
/// answered "You cast Wind master.", played the cast pose and armed the row's 480 s aether, and summoned nothing.
/// Silent: the player paid and waited eight minutes for a creature that never came.</para>
///
/// <para>A <c>game-data/SpellParams.csv</c> row now binds it to <c>verbs.not_castable</c>, which refuses every cast
/// before anything is spent. The spell stays learnable as before: refusing the cast did not need the learn row
/// touched, so the second fact pins it unchanged.</para>
///
/// <para>Driven end to end through <c>Session.Receive</c>, on a content-free map no other class uses.</para>
/// </summary>
[Collection("world")]
public sealed class WindMasterUncastableTests
{
    private const ushort Map = 62601;
    private const string Key = "cotw_wind_master_poet";
    private const uint Mana = 10_000;

    private readonly SessionFixture _fx;

    public WindMasterUncastableTests(SessionFixture fx) => _fx = fx;

    /// <summary>A cast from the book sends nothing back and changes nothing: no line, no pose, no mana, no aether,
    /// no cast/swing slot held, and no creature on the map. Red on 6de1d24: "You cast Wind master.", the pose, 10
    /// mana and the 480 s aether.</summary>
    [Fact]
    public void ACastIsRefusedSilentlyAndForFree()
    {
        var sp = Content.SpellByKey(Key);
        Assert.NotNull(sp);
        var (session, outbound, c) = _fx.PlayerWith("WindMaster1", ch =>
        {
            ch.Level = 99;
            ch.MaxHp = 1_000; ch.Hp = 1_000;
            ch.MaxMp = Mana; ch.Mp = Mana;
            ch.Spells.Add(sp!.Id);
        }, Map, x: 5, y: 5);
        try
        {
            session.Receive(SpellCastSupport.CastFrame(0));

            string what = $"lines [{string.Join(" | ", SpellCastSupport.MiniTexts(outbound))}], " +
                          $"poses {outbound.BodiesOf(ServerOp.Action).Count}, mana {c.Mp} (want {Mana}), " +
                          $"aether {SpellCastSupport.AetherLeft(session, Key)} ms, slot {session.ActionSlotLeft} ms, " +
                          $"creatures {_fx.World.View(session, Map).mobs.Length}";
            Assert.True(SpellCastSupport.MiniTexts(outbound).Count == 0 && outbound.BodiesOf(ServerOp.Action).Count == 0
                        && c.Mp == Mana && SpellCastSupport.AetherLeft(session, Key) == 0
                        && session.ActionSlotLeft == 0 && _fx.World.View(session, Map).mobs.Length == 0, what);
        }
        finally
        {
            _fx.World.LeaveMap(session, Map);
        }
    }

    /// <summary>It is still learnable as it was: a Poet's trainer row, level 99 and 350,000 coins
    /// (<c>game-data/SpellLearnCosts.csv</c>, source <c>tswolf-2002-10-01</c>). Green on 6de1d24 too.</summary>
    [Fact]
    public void ItIsStillLearnableAsBefore()
    {
        Assert.True(Content.SpellCosts.TryGetValue(Key, out var byPath), $"{Key} has no learn row");
        Assert.True(byPath!.TryGetValue(4, out var cost), $"{Key} has no Poet learn row");
        Assert.Equal((99, 350_000, 0), (cost!.Level, cost.Gold, cost.Items.Length));
    }
}
