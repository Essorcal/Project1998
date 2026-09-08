using System.Buffers.Binary;
using System.Text;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The per-mob half of the tick (#36): <c>MobAiTick.Step</c> driven on one creature with no socket, and the
/// exception boundary <c>World.Tick</c> puts around it.
///
/// <para>Before the split, the only catch on the tick thread was <c>TickLoop</c>'s, and it abandoned the
/// whole beat: every packet the tick had queued for every map was dropped unsent, while the mobs that had
/// already stepped stayed stepped. The isolation test below is that failure, reproduced and then denied —
/// one rigged creature throws, and the healthy one next to it still reaches the wire.</para>
///
/// <para>The rigged creature is a real throw from a real line, not a seam: <c>Mob.Key</c> nulled with
/// <c>LastStandUntil</c> set, so the Last Stand check's <c>Content.MobBosses.TryGetValue(mob.Key, ...)</c>
/// hands a null key to a <c>Dictionary</c>, which refuses it with <c>ArgumentNullException</c>. Every branch
/// before that line either compares the key with <c>==</c> (null-safe) or never looks at it, and the mob
/// carries nothing else that would make an earlier branch fire — verified by reading <c>Step</c> top to
/// bottom, and pinned by <see cref="StepPropagatesAThrowToItsCaller"/>, which names the exception type.</para>
///
/// <para>Hygiene: the fixture's <c>World</c> is shared by every class in the <c>world</c> collection and has
/// no teardown, so each test that rigs a creature un-rigs it and each test that seats a player removes it
/// (<c>World.LeaveMap</c>) — in <c>finally</c>, so a red run cannot leave a creature that throws on every
/// beat for whatever drives the next one (the #108 review hit exactly that).</para>
/// </summary>
[Collection("world")]
public class MobAiTickTests
{
    private readonly SessionFixture _fx;

    public MobAiTickTests(SessionFixture fx) => _fx = fx;

    // Content-free maps (no registry row, no terrain, no warps, no spawns), one per test so nothing is shared.
    private const ushort StepMap = 60030, ThrowMap = 60031, TickMap = 60032, PoisonMap = 60033, BlindMap = 60034, AssertMap = 60035,
                         OrderMap = 60036, WiringMap = 60037, WanderMap = 60038;

    /// <summary>Northeast Koguryo — the one map the Ice Beast melt is wired to (<c>World.IceBeastMap</c>), so
    /// the melt case below cannot use a content-free stand-in the way every other case here does. 36x22 by
    /// the registry, with (29,13) and (29,14) open in all four directions and no spawn on either: read off
    /// <c>Content.Maps</c>, <c>MapData</c> and the map's live mob list before the test was written.</summary>
    private const ushort KoguryoMap = 3040;

    private Mob Registered(ushort map, Mob mob)
    {
        _fx.World.AddMob(map, mob);
        return mob;
    }

    /// <summary>A creature that throws on a named line of <c>Step</c> — see the class doc. Rigged AFTER it is
    /// registered, so the spawn broadcast <c>AddMob</c> sends is an ordinary one. Undo with <see cref="Unrig"/>.</summary>
    private Mob Rigged(ushort map, ushort x, ushort y)
    {
        var mob = Registered(map, new Mob(_fx.World.AllocateMobId(), 1, x, y, "Faulty", 100));
        mob.Key = null!;
        mob.LastStandUntil = long.MaxValue;
        return mob;
    }

    private static void Unrig(Mob mob)
    {
        mob.Key = "";
        mob.LastStandUntil = 0;
    }

    // =====================================================================================================
    // Step on its own.
    // =====================================================================================================

    /// <summary>One creature, no player, no socket: standing three tiles south of its home with a two-tile
    /// leash, its beat is the walk home — one step north. The step lands in the context's move queue as
    /// the SOURCE tile (the 0x0C overshoot rule), the mob is on the new tile, and the collision index has
    /// vacated the old one and taken the new. No turn: it already faced north.
    ///
    /// <para>Deterministic on purpose. Wander rolls <c>Random.Shared</c>; the walk-home step toward a target
    /// on the same column has one candidate direction and takes it. What this pins is the walk-home block's
    /// hand-off to <c>StepMobToward</c>; the queue write it lands on is <c>StepMobTo</c>'s, in World.cs, a
    /// helper the extraction did not move — the two cases after this one pin writes inside <c>Step</c>.</para></summary>
    [Fact]
    public void StepQueuesTheMoveAndKeepsTheTileIndexCurrent()
    {
        var mob = new Mob(_fx.World.AllocateMobId(), 1, 5, 8, "Homesick", 100) { Wander = true };
        mob.HomeX = 5; mob.HomeY = 5;   // the constructor homes a mob where it stands; move home three tiles north
        Registered(StepMap, mob);

        World.MobTickContext ctx = null!;
        _fx.World.UnderWorldLockForTest(() =>
        {
            ctx = _fx.World.MobTickContextForTest(StepMap);
            Assert.Contains((5, 8), ctx.MobTiles);
            World.MobAiTick.Step(ctx, mob);
        });

        Assert.True(mob.Returning, "outside its leash, the creature should have started for home");
        var move = Assert.Single(ctx.Moves);
        Assert.Equal((StepMap, mob.Id, (ushort)5, (ushort)8, (byte)0), move);   // source tile, facing north
        Assert.Equal(((ushort)5, (ushort)7), (mob.X, mob.Y));
        Assert.Contains((5, 7), ctx.MobTiles);
        Assert.DoesNotContain((5, 8), ctx.MobTiles);
        Assert.Empty(ctx.Turns);
        Assert.Empty(ctx.Hits);
        Assert.Empty(ctx.TrapDamage);
    }

    /// <summary>The WANDER step's commit, pinned byte for byte, and the negative control for the melt case
    /// below: a creature that wanders one tile north broadcasts its SOURCE tile (the 0x0C forward-slide
    /// overshoot rule), vacates the tile it left and takes the one it arrived on, and springs the trap
    /// waiting on the destination exactly once, credited to the trap's owner. Nothing about that changed
    /// when #150 routed the commit through <c>MobMovement.StepMobTo</c>, which is the point of pinning it.
    ///
    /// <para><b>How it is made deterministic.</b> The wander block has no RNG seam — it rolls
    /// <c>Random.Shared</c> for whether to reconsider its facing and for which side to face — so the test
    /// removes the CHOICE instead of the roll. Home is the mob's own tile with <c>Leash = 1</c>, which
    /// leaves four candidate tiles; three of them are filled in the context's collision index, so the only
    /// tile the validation can ever pass is (5,4). The facing is forced north before each beat, which makes
    /// a step attempt land on roughly half of them (36% straight-ahead, plus a 1-in-4 side roll on the
    /// other 64%), and the loop stops the moment the mob moves. 200 beats is not a guess about timing: it
    /// is the bound at which the mob failing to step has probability ~0.48^200, and any real regression
    /// (the commit dropped, the leash test inverted, the candidate maths wrong) makes it fail every beat,
    /// so the assertions below fire rather than the loop hanging. <c>ctx.Turns</c> collects the re-facings
    /// of the beats that did not step and is deliberately not asserted on.</para>
    ///
    /// <para>Falsified by sending the DESTINATION tile instead of the source in <c>StepMobTo</c>: red,
    /// "Assert.Equal() Failure ... Expected: (60038, ..., 5, 5, 0) Actual: (60038, ..., 5, 4, 0)".</para></summary>
    [Fact]
    public void WanderStepBroadcastsItsSourceTileAndSpringsTheTrapOnItsDestination()
    {
        var mob = Registered(WanderMap, new Mob(_fx.World.AllocateMobId(), 1, 5, 5, "Roamer", 100)
        {
            Wander = true,
            Leash = 1,      // home is where it stands, so the leash box is the four neighbours
            MoveTime = 1,   // every beat is its turn, whatever TickMs is configured to
        });
        _fx.World.PlaceTrap(WanderMap, 5, 4, "dart", ownerId: 77);

        World.MobTickContext ctx = null!;
        _fx.World.UnderWorldLockForTest(() =>
        {
            ctx = _fx.World.MobTickContextForTest(WanderMap);
            ctx.MobTiles.Add((4, 5)); ctx.MobTiles.Add((6, 5)); ctx.MobTiles.Add((5, 6));   // only (5,4) is left
            for (int beat = 0; beat < 200 && mob.Y == 5; beat++)
            {
                mob.Dir = 0;
                World.MobAiTick.Step(ctx, mob);
            }
        });

        Assert.Equal(((ushort)5, (ushort)4), (mob.X, mob.Y));
        Assert.Equal((WanderMap, mob.Id, (ushort)5, (ushort)5, (byte)0), Assert.Single(ctx.Moves));
        Assert.Contains((5, 4), ctx.MobTiles);
        Assert.DoesNotContain((5, 5), ctx.MobTiles);
        var sprung = Assert.Single(ctx.TrapDamage);
        Assert.Same(mob, sprung.mob);
        Assert.Equal(WanderMap, sprung.map);
        Assert.Equal(77u, sprung.ownerId);
        Assert.True(sprung.dmg > 0);
        Assert.Empty(_fx.World.TrapsNear(WanderMap, 5, 4, 5));   // sprung once and gone
    }

    /// <summary>#150: an Ice Beast that WANDERS onto its lava melts, the same as one that chases, retreats,
    /// hops or darts onto it. Before this the tick's wander block committed its own step and the melt lived
    /// only in <c>MobMovement.StepMobTo</c>, so the one commit path that skipped <c>StepMobTo</c> was the
    /// one that skipped the melt.
    ///
    /// <para>Not hypothetical. <c>HomeX/HomeY</c> are written in two places — the retreat step and the
    /// charm-expiry branch's "Re-home it where it actually is" — and the beast is charmable
    /// (<c>mobs.csv</c> row 48, <c>MobIsBoss = 0</c>), so letting a charm lapse a tile short of the lava
    /// re-homes a wild, wandering beast with the lava inside its leash. Reproduced on a synthetic mob by
    /// the #151 review (<c>reviews/PR151-by-opus.md</c>, F1). This test starts from that end state — a
    /// wandering beast homed at (29,14) standing on (29,13) — rather than replaying the charm, because what
    /// is under test is the commit, not how the beast got there.</para>
    ///
    /// <para>Determinism, the collision fill and the 200-beat bound are the negative control's above. The
    /// lethal half of the melt is asserted on the context's own queue; the animation half is asserted on the
    /// wire, through a watcher on the same map and <c>FlushTickForTest</c>, because <c>_deferredFx</c> is
    /// private to <c>World</c> and gets no seam for one test.</para>
    ///
    /// <para>Red before the change with <c>Assert.Single() Failure: The collection was empty</c> on
    /// <c>ctx.TrapDamage</c> — the inline wander commit carried no melt.</para></summary>
    [Fact]
    public void AWanderingIceBeastMeltsOnItsLava()
    {
        // Inside the FX box the melt broadcasts over the lava tile (World.FxHalfW/FxHalfH, 19x17 either side
        // of (29,14)) and nowhere near the four tiles the wander step chooses between.
        var (watcher, outbound) = _fx.Player("MeltWatcher", KoguryoMap, x: 25, y: 12);
        var beast = new Mob(_fx.World.AllocateMobId(), 1, 29, 13, "Ice Beast", 100)
        {
            Key = "ice_beast",   // World.IceBeastKey: the melt is keyed on this and on the map id
            Wander = true,
            Leash = 1,
            MoveTime = 1,
        };
        beast.HomeX = 29; beast.HomeY = 14;   // re-homed onto the lava row by a lapsed charm; see the doc
        try
        {
            Registered(KoguryoMap, beast);

            var q = new World.TickQueues();
            World.MobTickContext ctx = null!;
            _fx.World.UnderWorldLockForTest(() =>
            {
                ctx = _fx.World.MobTickContextForTest(KoguryoMap, q);
                ctx.MobTiles.Add((28, 13)); ctx.MobTiles.Add((30, 13));   // (29,12) is outside the leash
                for (int beat = 0; beat < 200 && beast.Y == 13; beat++)
                {
                    beast.Dir = 2;
                    World.MobAiTick.Step(ctx, beast);
                }
            });

            Assert.Equal(((ushort)29, (ushort)14), (beast.X, beast.Y));   // stepped onto the lava (x 29-30, y 14-16)
            Assert.Equal((KoguryoMap, beast.Id, (ushort)29, (ushort)13, (byte)2), Assert.Single(ctx.Moves));

            var melt = Assert.Single(ctx.TrapDamage);
            Assert.Same(beast, melt.mob);
            Assert.Equal((KoguryoMap, beast.MaxHp, 0u), (melt.map, melt.dmg, melt.ownerId));

            outbound.Clear();
            _fx.World.FlushTickForTest(q);
            var overTheBeast = outbound.BodiesOf(0x29)
                                       .Where(b => BinaryPrimitives.ReadUInt32BigEndian(b) == beast.Id)
                                       .ToList();
            Assert.Equal(5, Assert.Single(overTheBeast)[4]);   // IceBeastMeltAnim, EfxWireOffset 0
        }
        finally
        {
            beast.Hp = 0;   // out of the map's live-mob set: this is a real content map the fixture shares
            _fx.World.LeaveMap(watcher, KoguryoMap);
        }
    }

    /// <summary>A venomed creature, held still: the poison tick is the first thing <c>Step</c> queues and the
    /// paralysis check is where it returns, so the beat is exactly two lines of the moved body and nothing
    /// rolls <c>Random.Shared</c>. Pins <c>Step</c>'s own <c>trapDamage.Add</c> (the poison block), the
    /// never-lethal clamp (7 off a creature on 100 is 7), the owner credited with the tick, and the 1500 ms
    /// cadence: a second beat inside the window queues nothing.
    ///
    /// <para>Falsified by deleting the <c>trapDamage.Add</c> line in <c>World.MobAiTick.cs</c>: red.</para></summary>
    [Fact]
    public void StepQueuesThePoisonTickFromItsOwnBody()
    {
        var mob = Registered(PoisonMap, new Mob(_fx.World.AllocateMobId(), 1, 5, 5, "Venomed", 100)
        {
            PoisonUntil = long.MaxValue, PoisonNextTick = 0, PoisonTickDam = 7, PoisonOwnerId = 42,
            FrozenUntil = long.MaxValue,   // held still: Step returns at the paralysis check, before any roll
        });

        World.MobTickContext ctx = null!;
        _fx.World.UnderWorldLockForTest(() =>
        {
            ctx = _fx.World.MobTickContextForTest(PoisonMap);
            World.MobAiTick.Step(ctx, mob);
            World.MobAiTick.Step(ctx, mob);   // inside the 1500 ms window: no second tick
        });

        var tick = Assert.Single(ctx.TrapDamage);
        Assert.Equal((PoisonMap, mob, 7, 42u), tick);
        Assert.True(mob.PoisonNextTick > Environment.TickCount64 + 1000, "the next tick should be ~1500 ms out");
        Assert.Equal(100, mob.Hp);        // the damage is applied by the flush, outside the lock — not here
        Assert.Empty(ctx.Moves);
        Assert.Empty(ctx.Turns);
        Assert.Empty(ctx.Hits);
    }

    /// <summary>A blinded creature with a player standing directly south of it: it cannot see, so it does not
    /// chase and does not wander, but it lashes out at whoever is in arm's reach — turning to face them first.
    /// The turn and the swing are both <c>Step</c>'s own writes (the blind block), and the path to them
    /// rolls nothing. Pins the face-then-swing order, the timer reset on the swing, and that the sighted
    /// target is dropped (<c>TargetId</c> cleared) even though someone is adjacent.
    ///
    /// <para>Falsified by deleting the blind block's <c>hits.Add</c> line in <c>World.MobAiTick.cs</c>: red.</para></summary>
    [Fact]
    public void StepQueuesTheBlindSwingFromItsOwnBody()
    {
        var (bystander, _) = _fx.Player("BlindBystander", BlindMap, x: 5, y: 6);
        try
        {
            var mob = Registered(BlindMap, new Mob(_fx.World.AllocateMobId(), 1, 5, 5, "Blinded", 100)
            {
                BlindUntil = long.MaxValue,
                AttackTime = 1,          // swings on its first beat, whatever TickMs is configured to
                TargetId = 999_999,      // a sighted target it must forget: blind creatures drop what they were fighting
            });

            World.MobTickContext ctx = null!;
            _fx.World.UnderWorldLockForTest(() =>
            {
                ctx = _fx.World.MobTickContextForTest(BlindMap);
                World.MobAiTick.Step(ctx, mob);
            });

            Assert.Equal(0u, mob.TargetId);
            var turn = Assert.Single(ctx.Turns);
            Assert.Equal((BlindMap, mob.Id, (byte)2), turn);   // faced south, toward the bystander
            Assert.Equal(2, mob.Dir);
            var hit = Assert.Single(ctx.Hits);
            Assert.Same(mob, hit.mob);
            Assert.Same(bystander, hit.target);
            Assert.Equal(0, mob.AttackTimer);                  // the swing spent the timer
            Assert.Empty(ctx.Moves);                           // blind: no chase, no wander
            Assert.Equal(((ushort)5, (ushort)5), (mob.X, mob.Y));
        }
        finally { _fx.World.LeaveMap(bystander, BlindMap); }
    }

    /// <summary>The guard is the tick's, not <c>Step</c>'s: driven directly, a throw comes out. This is the
    /// half the isolation test leans on — if <c>Step</c> ever swallowed its own exceptions, the tick test
    /// below would pass for the wrong reason.</summary>
    [Fact]
    public void StepPropagatesAThrowToItsCaller()
    {
        var mob = Rigged(ThrowMap, 5, 5);
        try
        {
            Assert.Throws<ArgumentNullException>(() =>
                _fx.World.UnderWorldLockForTest(() => World.MobAiTick.Step(_fx.World.MobTickContextForTest(ThrowMap), mob)));
        }
        finally { Unrig(mob); }
    }

#if DEBUG
    /// <summary>The contract at the top of <c>Step</c>, and the one at the top of the context constructor:
    /// both refuse to run without <c>World._lock</c>. Both directions, in the <c>SessionActorTests</c> shape —
    /// under the lock they are silent, off it they are loud. Debug-only by construction, like every lock
    /// assert in <c>docs/common/Locking.md</c>: the assert is compiled out of Release, so the test is too,
    /// and the Release suite proves nothing about it (the test host turns a failed <c>Debug.Assert</c> into
    /// a <c>DebugAssertException</c> carrying the message; a Debug server process would fail fast).
    ///
    /// <para>The creature is a plain one that would do nothing on its beat, so if the assert ever stopped
    /// firing the unlocked call would run to completion harmlessly — and this test would go red on
    /// <c>Assert.NotNull</c>, which is the point. Falsified by deleting the assert line: red.</para></summary>
    [Fact]
    public void StepOutsideTheWorldLockAsserts()
    {
        var mob = Registered(AssertMap, new Mob(_fx.World.AllocateMobId(), 1, 5, 5, "Unlocked", 100));

        World.MobTickContext ctx = null!;
        var rightWay = Record.Exception(() => _fx.World.UnderWorldLockForTest(() =>
        {
            ctx = _fx.World.MobTickContextForTest(AssertMap);
            World.MobAiTick.Step(ctx, mob);
        }));
        Assert.Null(rightWay);

        Assert.False(_fx.World.HoldsWorldLock);
        var wrongWay = Record.Exception(() => World.MobAiTick.Step(ctx, mob));
        Assert.NotNull(wrongWay);
        Assert.Contains("nowhere else", wrongWay!.Message);

        var wrongContext = Record.Exception(() => _fx.World.MobTickContextForTest(AssertMap));
        Assert.NotNull(wrongContext);
        Assert.Contains("build it under World._lock", wrongContext!.Message);
    }
#endif

    // =====================================================================================================
    // The whole beat.
    // =====================================================================================================

    /// <summary>Two creatures on one map with a player watching; the first is rigged to throw, the second is
    /// an aggressive chaser standing five tiles north of the player. One beat through the real <c>Tick</c>:
    /// the chaser's step reaches the watcher's outbound as a 0x0C (source tile, facing south), and the log
    /// names the creature that was skipped and carries its exception.
    ///
    /// <para>Falsified two ways, and they fail differently. With BOTH guards removed from <c>World.Tick</c>
    /// (today's shape) the <c>ArgumentNullException</c> escapes <c>TickOnceForTest</c> and the test is red
    /// before its first assertion: the abandoned beat, nothing flushed. With only the per-mob try removed,
    /// the per-map guard swallows the throw instead, the WHOLE map loses its beat — the chaser never steps —
    /// and the test is red on the chaser's target (the first assertion) after the log wait runs out its
    /// ceiling, because the per-map line does not name the creature.</para>
    ///
    /// <para>The chaser's beat is deterministic: the unprovoked-aggro scan locks onto the only player, the
    /// chase step toward a target on the same column has one candidate direction, and the tile is free.
    /// It is registered SECOND so the sweep reaches it after the throw.</para></summary>
    [Fact]
    public void OneMobThrowingDoesNotCostTheOthersTheirPackets()
    {
        var (watcher, outbound) = _fx.Player("TickWatcher", TickMap, x: 5, y: 10);
        var faulty = Rigged(TickMap, 8, 5);
        try
        {
            var chaser = Registered(TickMap, new Mob(_fx.World.AllocateMobId(), 1, 5, 5, "Chaser", 100)
            {
                Wander = true, Aggressive = true,
                MoveTime = 1,   // its move turn comes on its first beat, whatever TickMs is configured to
            });
            outbound.Clear();   // both spawns are drawn already; only the beat's own traffic from here

            var tap = new ConsoleTap();
            var prior = Console.Out;
            Console.SetOut(tap);
            string log;
            try
            {
                _fx.World.TickOnceForTest();
                // Log is a queue drained by its own writer thread; give the line a moment to reach the console.
                log = tap.WaitFor($"#{faulty.Id}", TimeSpan.FromSeconds(10));
            }
            finally { Console.SetOut(prior); }

            Assert.Equal(watcher.PlayerId, chaser.TargetId);   // it locked onto the watcher
            Assert.Equal(((ushort)5, (ushort)6), (chaser.X, chaser.Y));
            var moves = outbound.BodiesOf(0x0C).Where(b => BinaryPrimitives.ReadUInt32BigEndian(b) == chaser.Id).ToList();
            var move = Assert.Single(moves);
            Assert.Equal(5, BinaryPrimitives.ReadUInt16BigEndian(move.AsSpan(4)));   // source x
            Assert.Equal(5, BinaryPrimitives.ReadUInt16BigEndian(move.AsSpan(6)));   // source y
            Assert.Equal(2, move[8]);                                                  // facing south, toward the watcher

            Assert.Contains($"#{faulty.Id} on map {TickMap}", log);
            Assert.Contains(nameof(ArgumentNullException), log);
            Assert.Equal(((ushort)8, (ushort)5), (faulty.X, faulty.Y));   // skipped where the throw found it
        }
        finally
        {
            Unrig(faulty);
            _fx.World.LeaveMap(watcher, TickMap);
        }
    }

    /// <summary>The drain ORDER of <c>World.FlushTick</c>, on the wire (#107). Phases (3), (4) and (4.5) are
    /// not three independent loops that happen to be written in that sequence — the sequence IS the
    /// behaviour, and moving the flush into its own method is only safe if it is pinned.
    ///
    /// <para><b>What this pins, exactly:</b> viewports before moves, moves before turns, turns before swings
    /// — the three boundaries below, and no more. The rest of the (3)-(6) sequence (the deferred visuals and
    /// Lua hooks, regen, the clock, the weather) is NOT covered here; it is preserved by the fact that the
    /// flush body was moved into <c>FlushTick</c> verbatim, which is a different kind of evidence. Do not
    /// read a green run here as a licence to reorder those.</para>
    ///
    /// <para>One beat, one watcher, two creatures. The STEPPER stands one row below the watcher's view rect
    /// and walks home into it: its new tile is what phase (3) reconciles, so the watcher gets its spawn
    /// (0x07) from <c>ReconcileViews</c> and then its move (0x0C) from phase (4) — and only in that order,
    /// because <c>Session.MoveMob</c> is a no-op for a mob the client has not been shown. Send the move
    /// first and the client never sees the step at all: that is the despawn/off-screen-0x0C desync the
    /// comment on phase (3) is about. The SWINGER stands adjacent to the watcher but facing away from it, so
    /// its beat queues a turn and no move: the turn (0x11) comes out of phase (4) after every move, and the
    /// swing (0x1A) out of phase (4.5) after that.</para>
    ///
    /// <para>Also the no-work-queue pin: this beat tops up no forage box (the map is content-free) and, all
    /// but always, rolls no weather period (the warm-up beat syncs it first), so nothing goes out for either
    /// — exactly one 0x07 reaches the watcher (the stepper's spawn; a forage placement would be a second one,
    /// since <c>ShowGroundItem</c> draws through the same packet) and no 0x1F. The weather half has a real
    /// window: the period turns over every ~15 real minutes, and the gap between the warm-up beat and the
    /// measured one, while milliseconds wide, is not zero. So <c>WeatherModel.PeriodNow()</c> is read either
    /// side of the measured beat and the 0x1F assertion is skipped if it moved, rather than the test being
    /// flaky a few times a year.</para>
    ///
    /// <para>What that pins is the WIRE: a beat with no forage and no weather change sends neither packet.
    /// It does not distinguish <c>null</c> from an empty list — an eager <c>new()</c> on either field would
    /// pass it just the same. The null-until-used laziness is an allocation property, visible by reading the
    /// two <c>?</c> declarations on <c>TickQueues</c>, and is not worth a production seam to test.</para>
    ///
    /// <para>Deterministic: the stepper is outside its two-tile leash on the same column as its home, so the
    /// walk-home step has one candidate direction and rolls nothing; the swinger is cardinally adjacent, so
    /// it turns to face the watcher and swings on its first beat (<c>AttackTime = 1</c>) rather than stepping.
    ///
    /// <para>Falsified three ways, each restored afterwards. Moving the <c>q.Hits</c> loop above
    /// <c>ReconcileViews()</c> in <c>FlushTick</c>: red with "the move must precede the swing (move at 6,
    /// swing at 0)". Moving the <c>q.Turns</c> loop above <c>q.Moves</c>: red with "the move must precede the
    /// turn (move at 2, turn at 1)". Moving the <c>q.Hits</c> loop above <c>q.Turns</c>: red with "the turn
    /// must precede the swing (turn at 7, swing at 2)".</para></summary>
    [Fact]
    public void FlushSendsViewportsThenMovesThenHits()
    {
        var (watcher, outbound) = _fx.Player("OrderWatcher", OrderMap, x: 5, y: 10);
        try
        {
            // Warm-up beat with the map still empty: it syncs World._lastWeatherPeriod, so the measured beat
            // below almost never rolls a weather period. Almost: the period turns over every ~15 real
            // minutes (WeatherModel.PeriodHours) and the window between the two beats, though milliseconds
            // wide, is not zero. So the period is read either side of the measured beat and the 0x1F
            // assertion is made only when it did not move — see the end of the test.
            _fx.World.TickOnceForTest();

            // One row below the watcher's 17x15 view rect (rows -1..13 for a 12x12 map), so it is not drawn
            // yet; three tiles from home with a two-tile leash, so its beat is one step north into view.
            var stepper = new Mob(_fx.World.AllocateMobId(), 1, 5, 14, "Stepper", 100) { Wander = true };
            stepper.HomeX = 5; stepper.HomeY = 11;
            Registered(OrderMap, stepper);

            var swinger = Registered(OrderMap, new Mob(_fx.World.AllocateMobId(), 1, 5, 9, "Swinger", 100)
            {
                Aggressive = true,
                AttackTime = 1,   // swings on its first beat, whatever TickMs is configured to
                Dir = 0,          // facing AWAY from the watcher, so the swing branch queues a turn first
            });
            outbound.Clear();   // the swinger's own spawn is drawn already; only the beat's traffic from here

            long periodBefore = WeatherModel.PeriodNow();
            _fx.World.TickOnceForTest();
            long periodAfter = WeatherModel.PeriodNow();

            Assert.Equal(((ushort)5, (ushort)13), (stepper.X, stepper.Y));   // stepped into view
            Assert.Equal(watcher.PlayerId, swinger.TargetId);                // locked onto the watcher
            Assert.Equal(((ushort)5, (ushort)9), (swinger.X, swinger.Y));    // adjacent: swung, did not step

            Assert.Equal(2, swinger.Dir);                                    // turned to face it before swinging

            int spawn = IndexOfFrame(outbound, 0x07, b => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(6)) == stepper.Id);
            int move  = IndexOfFrame(outbound, 0x0C, b => BinaryPrimitives.ReadUInt32BigEndian(b) == stepper.Id);
            int turn  = IndexOfFrame(outbound, 0x11, b => BinaryPrimitives.ReadUInt32BigEndian(b) == swinger.Id);
            int swing = IndexOfFrame(outbound, 0x1A, b => BinaryPrimitives.ReadUInt32BigEndian(b) == swinger.Id);

            Assert.True(spawn >= 0, "phase (3) should have spawned the stepper into the watcher's view");
            Assert.True(move  >= 0, "phase (4) should have streamed the stepper's move");
            Assert.True(turn  >= 0, "phase (4) should have streamed the swinger's turn");
            Assert.True(swing >= 0, "phase (4.5) should have sent the swinger's attack pose");
            Assert.True(spawn < move,  $"the viewport spawn must precede the move (spawn at {spawn}, move at {move})");
            Assert.True(move  < turn,  $"the move must precede the turn (move at {move}, turn at {turn})");
            Assert.True(turn  < swing, $"the turn must precede the swing (turn at {turn}, swing at {swing})");

            // The turn is the swinger's, facing south — one turn frame, so "after every move" is exact.
            var tn = Assert.Single(outbound.BodiesOf(0x11));
            Assert.Equal(swinger.Id, BinaryPrimitives.ReadUInt32BigEndian(tn));
            Assert.Equal(2, tn[4]);
            Assert.Single(outbound.BodiesOf(0x0C));

            // The move is the step's SOURCE tile, facing north — the 0x0C overshoot rule.
            var mv = outbound.BodiesOf(0x0C).Single(b => BinaryPrimitives.ReadUInt32BigEndian(b) == stepper.Id);
            Assert.Equal(5, BinaryPrimitives.ReadUInt16BigEndian(mv.AsSpan(4)));
            Assert.Equal(14, BinaryPrimitives.ReadUInt16BigEndian(mv.AsSpan(6)));
            Assert.Equal(0, mv[8]);

            // No forage placement: the map is content-free, so nothing tops up on it, and a placement would
            // show as a second 0x07 (ShowGroundItem draws through the same creature-list packet a spawn does).
            Assert.Single(outbound.BodiesOf(0x07));
            // No weather frame — asserted only if the period really did stand still across the beat.
            if (periodBefore == periodAfter) Assert.Empty(outbound.BodiesOf(0x1F));
        }
        finally { _fx.World.LeaveMap(watcher, OrderMap); }
    }

    /// <summary>The context's ten queue fields are the TICK's queues, not copies and not each other (#107).
    ///
    /// <para>This PR rewrote a thirteen-argument constructor into a four-argument one, which is exactly the
    /// edit that ships a crossed pair. Nine of the ten wirings are protected by the compiler — the tuple
    /// types differ — but <c>HealthShows</c> and <c>ExpiredPets</c> are both
    /// <c>List&lt;(ushort map, Mob mob)&gt;</c>, so swapping those two compiles and, before this test,
    /// left the whole suite green. Reference identity covers all ten in one pass and costs nothing.</para>
    ///
    /// <para>Falsified by swapping <c>HealthShows</c> and <c>ExpiredPets</c> in the <c>MobTickContext</c>
    /// constructor: red with "Assert.Same() Failure: Values are not the same instance".</para></summary>
    [Fact]
    public void TheContextsQueuesAreTheTicksOwnQueues()
    {
        var q = new World.TickQueues();
        World.MobTickContext ctx = null!;
        _fx.World.UnderWorldLockForTest(() => ctx = _fx.World.MobTickContextForTest(WiringMap, q));

        Assert.Same(q.Moves, ctx.Moves);
        Assert.Same(q.Turns, ctx.Turns);
        Assert.Same(q.Hits, ctx.Hits);
        Assert.Same(q.MobCasts, ctx.MobCasts);
        Assert.Same(q.Chatter, ctx.Chatter);
        Assert.Same(q.MobHits, ctx.MobHits);
        Assert.Same(q.TrapDamage, ctx.TrapDamage);
        Assert.Same(q.FxRepeats, ctx.FxRepeats);
        Assert.Same(q.HealthShows, ctx.HealthShows);
        Assert.Same(q.ExpiredPets, ctx.ExpiredPets);
    }

    /// <summary>The same two same-typed wirings again, this time end to end on the wire: an entry on
    /// <c>HealthShows</c> has to come out as an over-head HP bar (0x13) for that mob, and an entry on
    /// <c>ExpiredPets</c> as a despawn (0x0E) for that one. Swap the two in the constructor and each mob
    /// gets the other's packet — a bug the type system cannot see and the reference-identity test above
    /// states abstractly; this one states it as what the player would witness.
    ///
    /// <para>The pet half runs through a REAL producer: a conjured creature whose lifetime has lapsed is
    /// queued by <c>Step</c>'s ownership-expiry block, with no roll anywhere on the path. The heal half has
    /// no deterministic producer — <c>SuteAi.TryHeal</c> is the only writer of <c>HealthShows</c> in the
    /// tree and it is gated on a 1-in-12 <c>Random.Shared</c> roll, a 20 s cooldown and Sute reaching his
    /// red bar with a target in range — so that entry is placed through <c>ctx.HealthShows</c>, the context
    /// field the constructor wired, which is the thing under test. Driving <c>Step</c> and then
    /// <c>FlushTick</c> over one shared <c>TickQueues</c> is what <c>MobTickContextForTest(map, q)</c> and
    /// <c>FlushTickForTest</c> exist for.</para>
    ///
    /// <para>Falsified by swapping <c>HealthShows</c> and <c>ExpiredPets</c> in the <c>MobTickContext</c>
    /// constructor: red with "the HP bar must be drawn over the healed mob (expected #100001, got #100000)
    /// — HealthShows and ExpiredPets are crossed". The two producer assertions above it are deliberately
    /// written against the CONTEXT's fields, so they still pass under the swap and the failure lands where
    /// it belongs, on the wire.</para></summary>
    [Fact]
    public void HealthShowsAndExpiredPetsReachTheWireOnTheirOwnMobs()
    {
        var (watcher, outbound) = _fx.Player("WiringWatcher", WiringMap, x: 5, y: 10);
        try
        {
            var pet = Registered(WiringMap, new Mob(_fx.World.AllocateMobId(), 1, 5, 8, "Conjured", 100)
            {
                OwnerId = watcher.PlayerId,
                PetExpiresAt = 1,   // already lapsed: Environment.TickCount64 is never below 1
                Summoned = true,    // conjured, not mind-controlled: the despawn ending
            });
            var healed = Registered(WiringMap, new Mob(_fx.World.AllocateMobId(), 1, 5, 12, "Healed", 100));
            healed.Hp = 50;

            var q = new World.TickQueues();
            World.MobTickContext ctx = null!;
            _fx.World.UnderWorldLockForTest(() =>
            {
                ctx = _fx.World.MobTickContextForTest(WiringMap, q);
                World.MobAiTick.Step(ctx, pet);              // real producer for the ExpiredPets half
                ctx.HealthShows.Add((WiringMap, healed));    // see the doc comment for why this half is placed
            });
            // Both producers fired, asserted on the CONTEXT's fields so this holds whatever the wiring is —
            // what happens to the two entries after that is the flush's business, below.
            Assert.Same(pet, Assert.Single(ctx.ExpiredPets).mob);
            Assert.Same(healed, Assert.Single(ctx.HealthShows).mob);

            outbound.Clear();
            _fx.World.FlushTickForTest(q);

            uint barId = BinaryPrimitives.ReadUInt32BigEndian(Assert.Single(outbound.BodiesOf(0x13)));
            Assert.True(barId == healed.Id,
                $"the HP bar must be drawn over the healed mob (expected #{healed.Id}, got #{barId}) — " +
                "HealthShows and ExpiredPets are crossed");
            Assert.Equal(50, outbound.BodiesOf(0x13)[0][5]);   // 50/100 hp: a half-full bar, this mob's number

            var despawned = outbound.BodiesOf(0x0E).SelectMany(DespawnedIds).ToList();
            Assert.True(despawned.Contains(pet.Id) && !despawned.Contains(healed.Id),
                $"the despawn must be the expired pet's (#{pet.Id}) and not the healed mob's (#{healed.Id}); " +
                $"despawned: {string.Join(", ", despawned)}");
        }
        finally { _fx.World.LeaveMap(watcher, WiringMap); }
    }

    /// <summary>The ids inside one 4.95 despawn body: a count byte then that many u32BE ids.</summary>
    private static IEnumerable<uint> DespawnedIds(byte[] body)
    {
        for (int i = 0; i < body[0]; i++)
            yield return BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1 + i * 4));
    }

    /// <summary>The position of the FIRST recorded frame carrying <paramref name="opcode"/> whose decrypted
    /// body satisfies <paramref name="body"/>, or -1. Indexes across every frame rather than within one
    /// opcode, which is the only way to compare the order of two different packets.</summary>
    private static int IndexOfFrame(RecordingOutbound outbound, byte opcode, Func<byte[], bool> body)
    {
        for (int i = 0; i < outbound.Frames.Count; i++)
        {
            if (!TkPacket.TryParse(outbound.Frames[i], out var pkt, out _)) continue;
            if (pkt.Opcode != opcode) continue;
            if (body(TkCrypt.Crypt(pkt.Body, pkt.Increment, TkCrypt.LoginKey))) return i;
        }
        return -1;
    }

    /// <summary>A <c>Console.Out</c> stand-in that can be read back safely while <c>Log</c>'s writer thread is
    /// still appending to it.</summary>
    private sealed class ConsoleTap : TextWriter
    {
        private readonly StringBuilder _text = new();

        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { lock (_text) _text.Append(value); }
        public override void Write(string? value) { lock (_text) _text.Append(value); }
        public override void Write(char[] buffer, int index, int count) { lock (_text) _text.Append(buffer, index, count); }

        private string Snapshot() { lock (_text) return _text.ToString(); }

        /// <summary>Everything written so far once <paramref name="needle"/> has appeared, or whatever was
        /// written when the deadline passed — the caller's assertion then names what was missing.</summary>
        public string WaitFor(string needle, TimeSpan deadline)
        {
            var until = DateTime.UtcNow + deadline;
            while (true)
            {
                string s = Snapshot();
                if (s.Contains(needle) || DateTime.UtcNow >= until) return s;
                Thread.Sleep(20);
            }
        }
    }
}
