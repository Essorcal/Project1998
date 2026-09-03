using System.Buffers.Binary;
using System.Text;
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
    private const ushort StepMap = 60030, ThrowMap = 60031, TickMap = 60032, PoisonMap = 60033, BlindMap = 60034, AssertMap = 60035;

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
