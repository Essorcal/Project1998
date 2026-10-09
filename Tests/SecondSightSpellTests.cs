using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Second Sight and its three alignment twins tell the Poet who is hiding nearby (#334).
///
/// <para>The four rows reached <c>arch_buff</c> with no stat and no slot, so a cast took 240 mana, answered
/// "You cast Second sight." and did nothing, and a recast took the mana again. The era pages give the spell an
/// effect and its numbers, and Caleb chose them over RTK's (2026-10-08, #334):</para>
/// <list type="bullet">
/// <item>tswolf, Poet page (2001-03-09, <c>tswolf-2001-spells-classes</c>): "Enables Text Notices of Invisible
/// Rogues", 140 mana, "Shows Text Notices in your Information Bar about the whereabouts of Invisible People."</item>
/// <item>The Nexus Atlas, Poet page (2002-12-30, <c>atlas-2002-12-30-spells-classes</c>): "Reveals Invisible
/// People", 140 mana, 0 aether, 325 s, "Shows a text notification that says "Name is Hidden in the Area"". Its
/// twins (Hear Spirits, Improve Sight, Show Hidden) are listed as names only.</item>
/// <item>RTK (<c>rtk-lua</c>, weight 0) says 240 mana, and gives what the pages do not: the scan runs every 15 s
/// over the players within 9 tiles on both axes, on the same map.</item>
/// </list>
///
/// <para>Driven end to end: the real 0x0F frame through <c>Session.Receive</c>, then <c>Session.RegenTick</c>, the
/// per-player beat the world tick runs, on a content-free map no other class uses.</para>
/// </summary>
[Collection("world")]
public sealed class SecondSightSpellTests
{
    private const ushort Map = 62342;
    private const long SlackMs = 5_000;

    private readonly SessionFixture _fx;

    public SecondSightSpellTests(SessionFixture fx) => _fx = fx;

    public static TheoryData<string> Family => new() { "second_sight_poet", "hear_spirits_poet", "improve_sight_poet", "show_hidden_poet" };

    /// <summary>A cast takes 140 mana, starts no aether, and holds the Second Sight slot for 325 s, which the buff
    /// box shows; a recast while it runs is refused at no cost. Red on 45dafed: 240 mana, no slot, and a recast
    /// takes 240 more.</summary>
    [Theory]
    [MemberData(nameof(Family))]
    public void ACastTakes140ManaAndRuns325Seconds(string key)
    {
        var sp = Content.SpellByKey(key)!;
        var (session, outbound, c) = _fx.PlayerWith($"Sight{sp.Id}", ch => Shape(ch, sp), Map, x: 30, y: 30);
        try
        {
            session.Receive(SpellCastSupport.CastFrame(0));

            Assert.Equal(860u, c.Mp);
            Assert.Equal(0, SpellCastSupport.AetherLeft(session, sp.Key));
            var run = SpellCastSupport.BuffEntry(session, sp.Key);
            Assert.NotNull(run);
            Assert.Equal("secondSights", run!.Value.Category);                   // Session.SecondSightSlot
            Assert.InRange(run.Value.LeftMs, 325_000 - SlackMs, 325_000);
            Assert.Contains($"You cast {sp.Name}.", SpellCastSupport.MiniTexts(outbound));

            outbound.Clear();
            session.Receive(SpellCastSupport.CastFrame(0));

            var lines = SpellCastSupport.MiniTexts(outbound);
            Assert.Equal(860u, c.Mp);
            Assert.Contains("You already cast that spell.", lines);
            Assert.DoesNotContain($"You cast {sp.Name}.", lines);
        }
        finally
        {
            _fx.World.LeaveMap(session, Map);
        }
    }

    /// <summary>On the next beat the caster reads "&lt;name&gt; is Hidden in the Area" (the Atlas's wording) for
    /// each invisible player within 9 tiles on both axes, and nothing for one 10 tiles away or for a player who
    /// is not hiding; the next beat, inside the 15 s, sends nothing more. Red on 45dafed: no line at all.</summary>
    [Theory]
    [MemberData(nameof(Family))]
    public void TheCasterIsToldWhoIsHiddenNearby(string key)
    {
        var sp = Content.SpellByKey(key)!;
        string tag = sp.Id.ToString();
        var (poet, outbound, _) = _fx.PlayerWith($"Seer{tag}", ch => Shape(ch, sp), Map, x: 10, y: 10);
        var (near, _, _) = _fx.PlayerWith($"Near{tag}", _ => { }, Map, x: 19, y: 1);    // 9 and 9 away
        var (far, _, _) = _fx.PlayerWith($"Far{tag}", _ => { }, Map, x: 20, y: 10);     // 10 away
        var (seen, _, _) = _fx.PlayerWith($"Seen{tag}", _ => { }, Map, x: 11, y: 10);   // beside, not hiding
        try
        {
            Hide(near);
            Hide(far);

            poet.Receive(SpellCastSupport.CastFrame(0));
            outbound.Clear();
            poet.RegenTick(333);

            Assert.Equal(new[] { $"Near{tag} is Hidden in the Area" }, SpellCastSupport.MiniTexts(outbound));

            outbound.Clear();
            poet.RegenTick(333);
            Assert.Empty(SpellCastSupport.MiniTexts(outbound));
            Assert.InRange(SpellCastSupport.SecondSightNextScanIn(poet), 15_000 - SlackMs, 15_000);
        }
        finally
        {
            foreach (var s in new[] { poet, near, far, seen }) _fx.World.LeaveMap(s, Map);
        }
    }

    /// <summary>Once the 325 s run is over, the beat says nothing, though an invisible player still stands beside
    /// the caster. The run is ended by moving its deadline into the past (<c>SpellCastSupport.EndBuff</c>), which
    /// is what the clock would do. Red with a scan that trusts the beat's hint and does not re-read the slot.</summary>
    [Fact]
    public void NothingIsToldOnceTheRunIsOver()
    {
        var sp = Content.SpellByKey("second_sight_poet")!;
        var (poet, outbound, _) = _fx.PlayerWith("SeerEnd", ch => Shape(ch, sp), Map, x: 40, y: 40);
        var (rogue, _, _) = _fx.PlayerWith("HiderEnd", _ => { }, Map, x: 41, y: 40);
        try
        {
            Hide(rogue);
            poet.Receive(SpellCastSupport.CastFrame(0));
            SpellCastSupport.EndBuff(poet, sp.Key);
            outbound.Clear();

            poet.RegenTick(333);

            Assert.Empty(SpellCastSupport.MiniTexts(outbound));
            Assert.Null(SpellCastSupport.BuffEntry(poet, sp.Key));
        }
        finally
        {
            _fx.World.LeaveMap(poet, Map);
            _fx.World.LeaveMap(rogue, Map);
        }
    }

    /// <summary>A run cast the moment the last one ends is told on its first beat, though the last run's notice
    /// set its 15 s clock just before. Red before the clock started again with each run (PR #338 review, F5): the
    /// new run waited out the old one's 15 s, and its first beat said nothing.</summary>
    [Fact]
    public void ARunCastRightAfterTheLastOneIsToldOnItsFirstBeat()
    {
        var sp = Content.SpellByKey("second_sight_poet")!;
        var (poet, outbound, c) = _fx.PlayerWith("SeerAgain", ch => Shape(ch, sp), Map, x: 60, y: 60);
        var (rogue, _, _) = _fx.PlayerWith("HiderAgain", _ => { }, Map, x: 61, y: 60);
        try
        {
            Hide(rogue);
            poet.Receive(SpellCastSupport.CastFrame(0));
            outbound.Clear();
            poet.RegenTick(333);
            Assert.Equal(new[] { "HiderAgain is Hidden in the Area" }, SpellCastSupport.MiniTexts(outbound));

            SpellCastSupport.EndBuff(poet, sp.Key);                  // the 325 s are over
            poet.Receive(SpellCastSupport.CastFrame(0));             // and the Poet casts again at once
            Assert.Equal(720u, c.Mp);
            outbound.Clear();
            poet.RegenTick(333);

            Assert.Equal(new[] { "HiderAgain is Hidden in the Area" }, SpellCastSupport.MiniTexts(outbound));
        }
        finally
        {
            _fx.World.LeaveMap(poet, Map);
            _fx.World.LeaveMap(rogue, Map);
        }
    }

    /// <summary>Invisible for a minute, through the same primitive the Invisible verb (<c>stance_stealth</c>) arms.</summary>
    private static void Hide(Session s) => s.WithState(() => s.LuaSetStealth(60_000));

    /// <summary>A level-99 Poet's caster with the spell in book slot 0 and 1000 mana.</summary>
    private static void Shape(Character c, SpellDef sp)
    {
        c.Level = 99;
        c.MaxHp = 1_000; c.Hp = 1_000;
        c.MaxMp = 1_000; c.Mp = 1_000;
        c.Spells.Add(sp.Id);
    }
}
