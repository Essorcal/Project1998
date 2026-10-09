using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Barrier and Human Barrier close the four tiles beside the Poet and hold what stands on them (#334).
///
/// <para>The eight rows reached <c>arch_buff</c> with no stat and no slot. Barrier's rows said 0 mana, which the
/// cast reads as "no cost given" and charges 5; Human Barrier charged its 300. Both answered "You cast X.", held
/// nothing, started no run, and took the mana again on every recast.</para>
///
/// <para>What they do now is the Nexus Atlas Poet page's (2002-12-30, Sources.csv
/// <c>atlas-2002-12-30-spells-classes</c>): "4 Way Invisible Blockade surrounds the caster, disabling any animals
/// from walking next to them. If an animal is in one of those spaces, they are paralyzed until the barrier wears
/// off." Human Barrier reads the same with "players". 300 mana and 22 s each, and Human Barrier an 86 s aether,
/// as tswolf (<c>tswolf-2001-spells-classes</c>) says too. Caleb's reading (2026-10-08): what stands on the four
/// tiles at the cast is held, nothing else may step onto them for the 22 s, the tiles stay where they were cast,
/// the caster is never held, and Human Barrier holds any player, in towns too.</para>
///
/// <para>Driven through the real 0x0F cast frame and 0x06 walk frame on <c>Session.Receive</c>. A creature's side
/// is driven through the tick's own per-beat context (<c>MobTickContextForTest</c>), as <c>MobAiTickTests</c> and
/// <c>MobMovementTests</c> do. "The barrier wears off" is reached by moving its deadlines into the past
/// (<c>World.EndBarriersForTest</c>, <c>SpellCastSupport.EndBuff</c>), which is what the clock does; each fact that
/// pins a deadline reads it from the cast. Content-free maps no other class uses, and Kugnae for the town
/// case.</para>
/// </summary>
[Collection("world")]
public sealed class BarrierSpellTests
{
    private const ushort CastMap = 62343, HoldMap = 62344, StepMap = 62345, HumanMap = 62346, WalkMap = 62347,
                         RaceMap = 62348, FallbackMap = 62349;
    private const ushort Kugnae = 0;

    private const long SlackMs = 5_000;
    private const byte North = 0, East = 1, South = 2, West = 3;

    /// <summary>The staff name and roster every GM class in the suite writes (see <c>ApproachSummonTests</c>), so
    /// the order the classes run in cannot demote anyone.</summary>
    private const string GmName = "cmdgm";

    private readonly SessionFixture _fx;
    private static int _serial;

    public BarrierSpellTests(SessionFixture fx)
    {
        _fx = fx;
        lock (TestProcessState.Gate)
        {
            Directory.CreateDirectory(TestProcessState.StateDirectory);
            File.WriteAllText(Path.Combine(TestProcessState.StateDirectory, "gm_accounts.txt"), GmName + "\n");
            StaffAccounts.Load();
        }
    }

    public static TheoryData<string> Barriers => new()
        { "barrier_poet", "spirit_barrier_poet", "life_barrier_poet", "balance_barrier_poet" };

    public static TheoryData<string> HumanBarriers => new()
        { "blockade_human_poet", "block_entry_poet", "distance_self_poet", "protect_sides_poet" };

    // ===== Barrier ===================================================================================

    /// <summary>A cast takes 300 mana through its own row, starts no aether, holds the <c>barriers</c> slot for
    /// 22 s, and raises a creature barrier where the Poet stands; a recast while it runs is refused at no cost.
    /// Red on 45dafed: 5 mana, no run and no barrier, and a recast takes 5 more.</summary>
    [Theory]
    [MemberData(nameof(Barriers))]
    public void BarrierTakes300ManaAndRuns22Seconds(string key)
    {
        var sp = Content.SpellByKey(key)!;
        var (poet, outbound, c) = Poet(sp, CastMap, 10, 10);
        try
        {
            poet.Receive(SpellCastSupport.CastFrame(0));

            Assert.Equal(700u, c.Mp);
            Assert.Equal(0, SpellCastSupport.AetherLeft(poet, key));
            var run = SpellCastSupport.BuffEntry(poet, key);
            Assert.NotNull(run);
            Assert.Equal("barriers", run!.Value.Category);
            Assert.InRange(run.Value.LeftMs, 22_000 - SlackMs, 22_000);
            var zone = Assert.Single(_fx.World.BarriersForTest(CastMap));
            Assert.Equal(((ushort)10, (ushort)10, false, poet.PlayerId), (zone.X, zone.Y, zone.Players, zone.CasterId));
            Assert.InRange(zone.Until - Environment.TickCount64, 22_000 - SlackMs, 22_000);
            Assert.Contains($"You cast {sp.Name}.", SpellCastSupport.MiniTexts(outbound));

            outbound.Clear();
            poet.Receive(SpellCastSupport.CastFrame(0));

            var lines = SpellCastSupport.MiniTexts(outbound);
            Assert.Equal(700u, c.Mp);
            Assert.Contains("You already cast that spell.", lines);
            Assert.DoesNotContain($"You cast {sp.Name}.", lines);
            Assert.Single(_fx.World.BarriersForTest(CastMap));
        }
        finally
        {
            _fx.World.EndBarriersForTest(CastMap);
            _fx.World.LeaveMap(poet, CastMap);
        }
    }

    /// <summary>The creatures on the four tiles at the cast are held until the barrier wears off, 22 s: frozen
    /// (<c>FrozenUntil</c>, the field every paralyze sets) to the barrier's own deadline, in the <c>snares</c> slot.
    /// One on a diagonal and one two tiles off are not held, and the caster is not held. Red on 45dafed: nothing is
    /// held.</summary>
    [Theory]
    [MemberData(nameof(Barriers))]
    public void TheCreaturesOnTheFourTilesAreHeldUntilTheBarrierWearsOff(string key)
    {
        var sp = Content.SpellByKey(key)!;
        var (poet, _, _) = Poet(sp, HoldMap, 10, 10);
        var beside = new[] { Creature(HoldMap, 10, 9), Creature(HoldMap, 11, 10), Creature(HoldMap, 10, 11), Creature(HoldMap, 9, 10) };
        var diagonal = Creature(HoldMap, 11, 11);
        var further = Creature(HoldMap, 10, 8);
        try
        {
            poet.Receive(SpellCastSupport.CastFrame(0));

            long until = Assert.Single(_fx.World.BarriersForTest(HoldMap)).Until;
            Assert.InRange(until - Environment.TickCount64, 22_000 - SlackMs, 22_000);
            foreach (var mob in beside)
            {
                Assert.InRange(mob.FrozenUntil, until - 50, until + 50);
                Assert.True(_fx.World.MobHasStatus(mob, World.BarrierHoldSlot), $"{mob.Name} is not in the snares slot");
            }
            foreach (var mob in new[] { diagonal, further })
                Assert.True(mob.FrozenUntil <= Environment.TickCount64, $"{mob.Name} is held, but it is not beside the Poet");
            Assert.False(poet.Paralyzed || poet.Asleep, "the caster is held");
        }
        finally
        {
            foreach (var mob in beside.Append(diagonal).Append(further)) _fx.World.DespawnMob(HoldMap, mob);
            _fx.World.EndBarriersForTest(HoldMap);
            _fx.World.LeaveMap(poet, HoldMap);
        }
    }

    /// <summary>A held creature cannot attack. An aggressive creature beside the Poet swings on its beat (the
    /// control, before the cast); held by the Barrier, its next beat queues no swing and no step; once the barrier
    /// and its hold have worn off it swings again. Red on 45dafed: it swings while "held".</summary>
    [Fact]
    public void AHeldCreatureCannotAttackUntilTheBarrierWearsOff()
    {
        var sp = Content.SpellByKey("barrier_poet")!;
        var (poet, _, _) = Poet(sp, HoldMap, 30, 30);
        var mob = Creature(HoldMap, 30, 31, m => { m.Aggressive = true; m.AttackTime = 1; });
        try
        {
            Assert.Single(Beat(HoldMap, mob).Hits);                     // control: it swings at the Poet

            poet.Receive(SpellCastSupport.CastFrame(0));
            var held = Beat(HoldMap, mob);
            Assert.Empty(held.Hits);
            Assert.Empty(held.Moves);
            Assert.Equal(((ushort)30, (ushort)31), (mob.X, mob.Y));

            _fx.World.EndBarriersForTest(HoldMap);
            _fx.World.UnderWorldLockForTest(() => mob.FrozenUntil = Environment.TickCount64 - 1);
            Assert.Single(Beat(HoldMap, mob).Hits);                     // worn off: it swings again
        }
        finally
        {
            _fx.World.DespawnMob(HoldMap, mob);
            _fx.World.EndBarriersForTest(HoldMap);
            _fx.World.LeaveMap(poet, HoldMap);
        }
    }

    /// <summary>No creature can step onto a barrier tile, and the tile opens when the barrier wears off. A creature
    /// two tiles north chases the Poet: the one step that closes the gap is a barrier tile, so it does not take it
    /// and reports itself walled off (<c>towardBlocked</c>). The control, the same chase at a column with no
    /// barrier, steps. After the barrier ends, a creature two tiles south steps onto the tile beside the Poet. Red
    /// on 45dafed: the chaser steps onto the tile.</summary>
    [Fact]
    public void NoCreatureCanStepOntoABarrierTile()
    {
        var sp = Content.SpellByKey("barrier_poet")!;
        var (poet, _, _) = Poet(sp, StepMap, 10, 10);
        var chaser = Creature(StepMap, 10, 8);
        var control = Creature(StepMap, 30, 8);
        var later = Creature(StepMap, 10, 12);
        try
        {
            poet.Receive(SpellCastSupport.CastFrame(0));

            bool walled = false;
            _fx.World.UnderWorldLockForTest(() =>
            {
                var ctx = _fx.World.MobTickContextForTest(StepMap);
                World.MobMovement.StepTowardForTest(ctx, chaser, 10, 10, out walled);
                World.MobMovement.StepTowardForTest(ctx, control, 30, 10, out _);
            });
            Assert.True(walled, "the only step toward the Poet is a barrier tile: the chase should be walled off");
            Assert.NotEqual(((ushort)10, (ushort)9), (chaser.X, chaser.Y));
            Assert.Equal(((ushort)30, (ushort)9), (control.X, control.Y));

            _fx.World.EndBarriersForTest(StepMap);
            _fx.World.UnderWorldLockForTest(() =>
                World.MobMovement.StepTowardForTest(_fx.World.MobTickContextForTest(StepMap), later, 10, 10, out _));
            Assert.Equal(((ushort)10, (ushort)11), (later.X, later.Y));
        }
        finally
        {
            foreach (var mob in new[] { chaser, control, later }) _fx.World.DespawnMob(StepMap, mob);
            _fx.World.EndBarriersForTest(StepMap);
            _fx.World.LeaveMap(poet, StepMap);
        }
    }

    /// <summary>The caster is never held, and the tiles stay where the barrier was cast. The Poet walks away east,
    /// two steps, unheld; a creature still cannot step onto the tile north of where the Poet cast, and one can step
    /// beside where the Poet now stands. Red if the barrier followed the Poet, or held it.</summary>
    [Fact]
    public void TheCasterWalksFreeAndTheTilesStayWhereTheyWereCast()
    {
        var sp = Content.SpellByKey("barrier_poet")!;
        var (poet, _, _) = Poet(sp, StepMap, 50, 50);
        var atCast = Creature(StepMap, 50, 48);
        var atPoet = Creature(StepMap, 52, 48);
        try
        {
            poet.Receive(SpellCastSupport.CastFrame(0));
            Walk(poet, East);
            Walk(poet, East);

            Assert.Equal(((ushort)52, (ushort)50), (poet.PlayerX, poet.PlayerY));
            Assert.False(poet.Paralyzed || poet.Asleep, "the caster is held");
            var zone = Assert.Single(_fx.World.BarriersForTest(StepMap), z => z.Until > Environment.TickCount64);
            Assert.Equal(((ushort)50, (ushort)50), (zone.X, zone.Y));

            _fx.World.UnderWorldLockForTest(() =>
            {
                var ctx = _fx.World.MobTickContextForTest(StepMap);
                World.MobMovement.StepTowardForTest(ctx, atCast, 50, 50, out _);
                World.MobMovement.StepTowardForTest(ctx, atPoet, 52, 50, out _);
            });
            Assert.NotEqual(((ushort)50, (ushort)49), (atCast.X, atCast.Y));
            Assert.Equal(((ushort)52, (ushort)49), (atPoet.X, atPoet.Y));
        }
        finally
        {
            _fx.World.DespawnMob(StepMap, atCast);
            _fx.World.DespawnMob(StepMap, atPoet);
            _fx.World.EndBarriersForTest(StepMap);
            _fx.World.LeaveMap(poet, StepMap);
        }
    }

    /// <summary>A Barrier closes its tiles to creatures, not to players: another player walks onto one. The
    /// negative control for the player-side check.</summary>
    [Fact]
    public void ABarrierDoesNotStopPlayers()
    {
        var sp = Content.SpellByKey("barrier_poet")!;
        var (poet, _, _) = Poet(sp, WalkMap, 70, 70);
        var (walker, _, _) = Plain("BarWalker", WalkMap, 70, 72);
        try
        {
            poet.Receive(SpellCastSupport.CastFrame(0));
            Walk(walker, North);

            Assert.Equal(((ushort)70, (ushort)71), (walker.PlayerX, walker.PlayerY));
            Assert.False(walker.Paralyzed);
        }
        finally
        {
            _fx.World.EndBarriersForTest(WalkMap);
            _fx.World.LeaveMap(poet, WalkMap);
            _fx.World.LeaveMap(walker, WalkMap);
        }
    }

    // ===== Human Barrier ============================================================================

    /// <summary>A cast takes 300 mana, holds the <c>humanBarriers</c> slot for 22 s, starts its 86 s aether and
    /// raises a player barrier; a recast inside the aether is refused at no cost. Red on 45dafed: no run, no
    /// aether, and a recast takes 300 more.</summary>
    [Theory]
    [MemberData(nameof(HumanBarriers))]
    public void HumanBarrierTakes300ManaRuns22SecondsAndHasAn86SecondAether(string key)
    {
        var sp = Content.SpellByKey(key)!;
        var (poet, outbound, c) = Poet(sp, CastMap, 30, 30);
        try
        {
            poet.Receive(SpellCastSupport.CastFrame(0));

            Assert.Equal(700u, c.Mp);
            Assert.InRange(SpellCastSupport.AetherLeft(poet, key), 86_000 - SlackMs, 86_000);
            var run = SpellCastSupport.BuffEntry(poet, key);
            Assert.NotNull(run);
            Assert.Equal("humanBarriers", run!.Value.Category);
            Assert.InRange(run.Value.LeftMs, 22_000 - SlackMs, 22_000);
            var zone = Assert.Single(_fx.World.BarriersForTest(CastMap), z => z.Until > Environment.TickCount64);
            Assert.Equal(((ushort)30, (ushort)30, true), (zone.X, zone.Y, zone.Players));
            Assert.Contains($"You cast {sp.Name}.", SpellCastSupport.MiniTexts(outbound));

            outbound.Clear();
            poet.Receive(SpellCastSupport.CastFrame(0));

            var lines = SpellCastSupport.MiniTexts(outbound);
            Assert.Equal(700u, c.Mp);
            Assert.Contains(lines, l => l.StartsWith($"{sp.Name} isn't ready yet (", StringComparison.Ordinal));
            Assert.DoesNotContain($"You cast {sp.Name}.", lines);
        }
        finally
        {
            _fx.World.EndBarriersForTest(CastMap);
            _fx.World.LeaveMap(poet, CastMap);
        }
    }

    /// <summary>Any player on the four tiles is held, in a town too: Kugnae, which is not a PvP map. Each held
    /// player is told who did it, through the line every spell cast on a player uses. One on a diagonal is not
    /// held, and the caster is not. Red on 45dafed: nobody is held.</summary>
    [Fact]
    public void AnyPlayerOnTheFourTilesIsHeldInATown()
    {
        Assert.False(Content.IsPvpMap(Kugnae));
        var sp = Content.SpellByKey("blockade_human_poet")!;
        var dims = Content.Maps[Kugnae];
        ushort x = (ushort)(dims.Xs / 2), y = (ushort)(dims.Ys / 2);
        var (poet, _, _) = Poet(sp, Kugnae, x, y);
        var beside = new[] { Plain("TownN", Kugnae, x, (ushort)(y - 1)), Plain("TownE", Kugnae, (ushort)(x + 1), y),
                             Plain("TownS", Kugnae, x, (ushort)(y + 1)), Plain("TownW", Kugnae, (ushort)(x - 1), y) };
        var (diagonal, _, _) = Plain("TownD", Kugnae, (ushort)(x + 1), (ushort)(y + 1));
        try
        {
            poet.Receive(SpellCastSupport.CastFrame(0));

            foreach (var (s, outbound, _) in beside)
            {
                Assert.True(s.Paralyzed, $"{s.CharName} is beside the Poet and not held");
                var hold = SpellCastSupport.BuffEntry(s, "blockade_human_poet:held");
                Assert.InRange(hold!.Value.LeftMs, 22_000 - SlackMs, 22_000);
                Assert.Contains($"{poet.CharName} casts Human Barrier on you.", SpellCastSupport.MiniTexts(outbound));
            }
            Assert.False(diagonal.Paralyzed, "the player on the diagonal is held");
            Assert.False(poet.Paralyzed, "the caster is held");
        }
        finally
        {
            _fx.World.EndBarriersForTest(Kugnae);
            foreach (var (s, _, _) in beside) _fx.World.LeaveMap(s, Kugnae);
            _fx.World.LeaveMap(diagonal, Kugnae);
            _fx.World.LeaveMap(poet, Kugnae);
        }
    }

    /// <summary>A held player cannot walk or cast until the hold, 22 s, wears off; then they walk. Taking damage
    /// does not free them: the hold lasts "until the barrier wears off". Each twin holds as its base spell does.
    /// Red on 45dafed: the player walks and casts.</summary>
    [Theory]
    [MemberData(nameof(HumanBarriers))]
    public void AHeldPlayerCannotWalkOrCastUntilTheHoldWearsOff(string key)
    {
        var sp = Content.SpellByKey(key)!;
        var sight = Content.SpellByKey("second_sight_poet")!;
        var (poet, _, _) = Poet(sp, HumanMap, 10, 10);
        var (held, heldOut, hc) = Poet(sight, HumanMap, 11, 10);
        try
        {
            poet.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(held.Paralyzed);

            heldOut.Clear();
            Walk(held, East);
            held.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(held.ReceiveSpellDamage(10, poet, "Spark") > 0, "the hit did not land");   // a hit wakes a sleeper

            Assert.Equal(((ushort)11, (ushort)10), (held.PlayerX, held.PlayerY));
            Assert.Equal(1_000u, hc.Mp);
            Assert.DoesNotContain("You cast Second sight.", SpellCastSupport.MiniTexts(heldOut));
            Assert.True(held.Paralyzed, "a hit freed a paralysed player");

            SpellCastSupport.EndBuff(held, $"{key}:held");
            Walk(held, East);
            Assert.Equal(((ushort)12, (ushort)10), (held.PlayerX, held.PlayerY));
            Assert.False(held.Paralyzed);
        }
        finally
        {
            _fx.World.EndBarriersForTest(HumanMap);
            _fx.World.LeaveMap(poet, HumanMap);
            _fx.World.LeaveMap(held, HumanMap);
        }
    }

    /// <summary>No other player may walk onto the tiles while the barrier runs, and the caster may. A player two
    /// tiles south is refused the step north, and one two tiles east the step west; the Poet steps north onto its
    /// own tile. When the barrier wears off, the step west is taken. Red on 45dafed: both steps are taken.</summary>
    [Fact]
    public void OtherPlayersCannotWalkOntoTheTilesButTheCasterCan()
    {
        var sp = Content.SpellByKey("blockade_human_poet")!;
        var (poet, _, _) = Poet(sp, WalkMap, 30, 30);
        var (south, _, _) = Plain("HbSouth", WalkMap, 30, 32);
        var (east, _, _) = Plain("HbEast", WalkMap, 32, 30);
        try
        {
            poet.Receive(SpellCastSupport.CastFrame(0));
            Walk(south, North);
            Walk(east, West);
            Assert.Equal(((ushort)30, (ushort)32, (ushort)32, (ushort)30),
                         (south.PlayerX, south.PlayerY, east.PlayerX, east.PlayerY));

            Walk(poet, North);
            Assert.Equal(((ushort)30, (ushort)29), (poet.PlayerX, poet.PlayerY));
            Assert.False(poet.Paralyzed);

            _fx.World.EndBarriersForTest(WalkMap);
            Walk(east, West);
            Assert.Equal(((ushort)31, (ushort)30), (east.PlayerX, east.PlayerY));
        }
        finally
        {
            _fx.World.EndBarriersForTest(WalkMap);
            foreach (var s in new[] { poet, south, east }) _fx.World.LeaveMap(s, WalkMap);
        }
    }

    /// <summary>A Human Barrier neither holds nor stops creatures: a creature beside the Poet is not frozen, and a
    /// creature two tiles north steps onto the tile between. The negative control for the creature side.</summary>
    [Fact]
    public void AHumanBarrierDoesNotHoldOrStopCreatures()
    {
        var sp = Content.SpellByKey("blockade_human_poet")!;
        var (poet, _, _) = Poet(sp, StepMap, 70, 70);
        var beside = Creature(StepMap, 71, 70);
        var chaser = Creature(StepMap, 70, 68);
        try
        {
            poet.Receive(SpellCastSupport.CastFrame(0));
            _fx.World.UnderWorldLockForTest(() =>
                World.MobMovement.StepTowardForTest(_fx.World.MobTickContextForTest(StepMap), chaser, 70, 70, out _));

            Assert.True(beside.FrozenUntil <= Environment.TickCount64, "a Human Barrier held a creature");
            Assert.Equal(((ushort)70, (ushort)69), (chaser.X, chaser.Y));
        }
        finally
        {
            _fx.World.DespawnMob(StepMap, beside);
            _fx.World.DespawnMob(StepMap, chaser);
            _fx.World.EndBarriersForTest(StepMap);
            _fx.World.LeaveMap(poet, StepMap);
        }
    }

    /// <summary>A GM on the tiles is held like anyone. The server's player hold has no staff exemption: the sleep
    /// gates this one sits beside have none, and RTK's GM walk-through (clif.c:5043) was never ported.</summary>
    [Fact]
    public void AGmOnTheTilesIsHeldLikeAnyone()
    {
        var sp = Content.SpellByKey("blockade_human_poet")!;
        var (poet, _, _) = Poet(sp, HumanMap, 40, 40);
        var (gm, _, _) = Plain(GmName, HumanMap, 40, 41);
        try
        {
            poet.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(gm.Paralyzed, "the GM beside the Poet is not held");
        }
        finally
        {
            _fx.World.EndBarriersForTest(HumanMap);
            _fx.World.LeaveMap(poet, HumanMap);
            _fx.World.LeaveMap(gm, HumanMap);
        }
    }

    // ===== the 5-mana fallback =======================================================================

    /// <summary>Barrier's 300 comes from its own row; the fallback that charges 5 for a row saying 0 is unchanged.
    /// Invisible (a 0 row) still takes exactly 5. The Inferno ladder (a 0 row, then the whole pool) casts on
    /// exactly 5 and is refused on 4.</summary>
    [Fact]
    public void InvisibleAndTheInfernoLadderStillCostFive()
    {
        var invisible = Content.SpellByKey("invisible_rogue")!;
        var (rogue, _, rc) = Poet(invisible, FallbackMap, 10, 10);
        var inferno = Content.SpellByKey("inferno_mage")!;
        var (onFive, fiveOut, fc) = Poet(inferno, FallbackMap, 20, 10, mp: 5);
        var (onFour, fourOut, _) = Poet(inferno, FallbackMap, 30, 10, mp: 4);
        var mobFive = Creature(FallbackMap, 21, 10, hp: 10_000_000);
        var mobFour = Creature(FallbackMap, 31, 10, hp: 10_000_000);
        try
        {
            rogue.Receive(SpellCastSupport.CastFrame(0));
            onFive.Receive(SpellCastSupport.CastFrame(0, mobFive.Id));
            onFour.Receive(SpellCastSupport.CastFrame(0, mobFour.Id));

            Assert.Equal(995u, rc.Mp);
            Assert.Contains("You cast Inferno.", SpellCastSupport.MiniTexts(fiveOut));
            Assert.Equal(0u, fc.Mp);
            Assert.Contains("You do not have enough mana.", SpellCastSupport.MiniTexts(fourOut));
        }
        finally
        {
            _fx.World.DespawnMob(FallbackMap, mobFive);
            _fx.World.DespawnMob(FallbackMap, mobFour);
            foreach (var s in new[] { rogue, onFive, onFour }) _fx.World.LeaveMap(s, FallbackMap);
        }
    }

    // ===== locks =====================================================================================

    /// <summary>Two Poets side by side cast Human Barrier at each other again and again, each also trying to step
    /// onto the other's tile, while the world ticks a creature beside them. Each cast writes the other player's
    /// state (the paralysis) after releasing <c>World._lock</c>, inside that player's monitor; each step takes its
    /// own monitor and then <c>World._lock</c>, and is refused (the tile is taken), so neither Poet moves and every
    /// cast lands on the other; the tick reads the barriers under <c>World._lock</c>. Nothing may stall
    /// (<see cref="StallWatch"/>), and both Poets are held by the other on most rounds. Paralysing inside
    /// <c>World._lock</c> instead is the cycle this pins: one Poet holds the lock and waits for the other's monitor
    /// while the other, mid-step, holds its monitor and waits for the lock.</summary>
    [Fact]
    public void CrossCastingHumanBarriersWhileWalkingAndTickingNeverStalls()
    {
        const int Rounds = 300;
        var sp = Content.SpellByKey("blockade_human_poet")!;
        var (a, aOut, _) = Poet(sp, RaceMap, 10, 10, mp: 10_000_000);
        var (b, bOut, _) = Poet(sp, RaceMap, 11, 10, mp: 10_000_000);
        var mob = Creature(RaceMap, 10, 12, m => { m.Wander = true; m.MoveTime = 1; });   // wanders, never swings: a dead Poet stops blocking
        var progress = new StallWatch.RoundCounter();
        var start = new ManualResetEventSlim();
        Exception? fault = null;
        bool done = false;

        // The cast goes in below the packet handler's action budget (three a second) and its hold gate, which
        // would drop all but a few of these casts: ApplyCast under the caster's monitor, as HandleCast calls it,
        // so the Lua gate, the verb, World._lock and the other Poet's monitor are taken exactly as in play. The
        // step is HandleWalk's: World.TryMovePlayer under the walker's own monitor, onto the other Poet.
        var applyCast = typeof(Session).GetMethod("ApplyCast",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        int landedA = 0, landedB = 0;
        void Caster(Session self, Session other, Action landed)
        {
            try
            {
                start.Wait();
                for (int i = 0; i < Rounds; i++)
                {
                    // Free this Poet to cast again: its own run, aether and any hold the other put on it.
                    SpellCastSupport.EndBuff(self, sp.Key);
                    SpellCastSupport.EndBuff(self, $"{sp.Key}:held");
                    SpellCastSupport.ClearAether(self, sp.Key);
                    bool ok = false;
                    self.WithState(() => ok = (bool)applyCast.Invoke(self, new object?[] { sp, null, null })!);
                    if (ok) landed();
                    self.WithState(() => _fx.World.TryMovePlayer(self, RaceMap, other.PlayerX, other.PlayerY,
                                                                 ghostMover: false, enforceOccupancy: true,
                                                                 otherwiseBlocked: false, out _));
                    progress.Bump();
                }
            }
            catch (Exception e) { fault ??= e; }
        }

        var ta = new Thread(() => Caster(a, b, () => landedA++)) { IsBackground = true };
        var tb = new Thread(() => Caster(b, a, () => landedB++)) { IsBackground = true };
        var tick = new Thread(() =>
        {
            try
            {
                start.Wait();
                while (!Volatile.Read(ref done)) { _fx.World.TickOnceForTest(); progress.Bump(); }
            }
            catch (Exception e) { fault ??= e; }
        }) { IsBackground = true };
        try
        {
            ta.Start(); tb.Start(); tick.Start();
            start.Set();
            StallWatch.RunUntilDoneOrStalled(new[] { ta, tb }, () => progress.Rounds,
                StallWatch.StallQuiet, StallWatch.StallCap, "the two casting Poets");
            Volatile.Write(ref done, true);
            StallWatch.RunUntilDoneOrStalled(new[] { tick }, () => progress.Rounds,
                StallWatch.StallQuiet, StallWatch.StallCap, "the tick thread");

            Assert.Null(fault);
            // Every round's cast landed, on both Poets. (Counted, not read off the pool: the tick's regen beat tops
            // the pools back up while the race runs.)
            Assert.Equal((Rounds, Rounds), (landedA, landedB));
            // …and the cross-session write ran on most rounds: each Poet was told "X casts Human Barrier on you."
            // whenever the other's cast found it free of a hold. Neither moved.
            int heldA = SpellCastSupport.MiniTexts(aOut).Count(l => l.EndsWith("casts Human Barrier on you.", StringComparison.Ordinal));
            int heldB = SpellCastSupport.MiniTexts(bOut).Count(l => l.EndsWith("casts Human Barrier on you.", StringComparison.Ordinal));
            Assert.True(heldA > Rounds / 2 && heldB > Rounds / 2, $"held {heldA} and {heldB} times in {Rounds} rounds each");
            Assert.Equal(((ushort)10, (ushort)11), (a.PlayerX, b.PlayerX));
        }
        finally
        {
            Volatile.Write(ref done, true);
            // A stalled run leaves its map as it is: cleaning it up would wait on the lock the stuck threads
            // hold, and the fact would hang instead of reporting the stall.
            if (!ta.IsAlive && !tb.IsAlive && !tick.IsAlive)
            {
                _fx.World.DespawnMob(RaceMap, mob);
                _fx.World.EndBarriersForTest(RaceMap);
                _fx.World.LeaveMap(a, RaceMap);
                _fx.World.LeaveMap(b, RaceMap);
            }
        }
    }

    // ===== helpers ===================================================================================

    /// <summary>One beat of <paramref name="mob"/>'s AI on the tick's own context, under the world lock.</summary>
    private World.MobTickContext Beat(ushort map, Mob mob)
    {
        World.MobTickContext ctx = null!;
        _fx.World.UnderWorldLockForTest(() =>
        {
            ctx = _fx.World.MobTickContextForTest(map);
            World.MobAiTick.Step(ctx, mob);
        });
        return ctx;
    }

    /// <summary>A level-99 caster with <paramref name="sp"/> in book slot 0, on a 100x100 walkable map.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Poet(SpellDef sp, ushort map, ushort x, ushort y,
                                                                          uint mp = 1_000)
    {
        int n = Interlocked.Increment(ref _serial);
        return _fx.PlayerWith($"Barrier{n}", ch =>
        {
            ch.Level = 99;
            ch.MaxHp = 1_000; ch.Hp = 1_000;
            ch.MaxMp = Math.Max(mp, 1_000u); ch.Mp = mp;
            Wide(ch, map);
            ch.Spells.Add(sp.Id);
        }, map, x, y);
    }

    /// <summary>A player with no spells, on a 100x100 walkable map.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Plain(string name, ushort map, ushort x, ushort y) =>
        _fx.PlayerWith(name == GmName ? name : $"{name}{Interlocked.Increment(ref _serial)}",
                       ch => { ch.Level = 50; ch.MaxHp = 1_000; ch.Hp = 1_000; Wide(ch, map); }, map, x, y);

    /// <summary>Content-free maps have no size of their own; the walk handler needs one.</summary>
    private static void Wide(Character c, ushort map)
    {
        if (Content.Maps.TryGetValue(map, out var m)) { c.MapXs = m.Xs; c.MapYs = m.Ys; }
        else { c.MapXs = 100; c.MapYs = 100; }
    }

    private Mob Creature(ushort map, ushort x, ushort y, Action<Mob>? shape = null, int hp = 100)
    {
        var mob = new Mob(_fx.World.AllocateMobId(), 1, x, y, $"BarrierMob{Interlocked.Increment(ref _serial)}", hp);
        shape?.Invoke(mob);
        _fx.World.AddMob(map, mob);
        return mob;
    }

    /// <summary>The client's own 0x06 walk, reporting the tile the server has the walker on.</summary>
    private static void Walk(Session s, byte dir)
    {
        ushort x = s.PlayerX, y = s.PlayerY;
        s.Receive(SessionFixture.Frame(ClientOp.Walk, new byte[] { dir, 0, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y }));
    }
}
