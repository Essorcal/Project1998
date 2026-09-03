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
/// </summary>
[Collection("world")]
public class MobAiTickTests
{
    private readonly SessionFixture _fx;

    public MobAiTickTests(SessionFixture fx) => _fx = fx;

    // Content-free maps (no registry row, no terrain, no warps, no spawns), one per test so nothing is shared.
    private const ushort StepMap = 60030, ThrowMap = 60031, TickMap = 60032;

    private Mob Registered(ushort map, Mob mob)
    {
        _fx.World.AddMob(map, mob);
        return mob;
    }

    /// <summary>A creature that throws on a named line of <c>Step</c> — see the class doc. Rigged AFTER it is
    /// registered, so the spawn broadcast <c>AddMob</c> sends is an ordinary one.</summary>
    private Mob Rigged(ushort map, ushort x, ushort y)
    {
        var mob = Registered(map, new Mob(_fx.World.AllocateMobId(), 1, x, y, "Faulty", 100));
        mob.Key = null!;
        mob.LastStandUntil = long.MaxValue;
        return mob;
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
    /// on the same column has one candidate direction and takes it.</para></summary>
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

    /// <summary>The guard is the tick's, not <c>Step</c>'s: driven directly, a throw comes out. This is the
    /// half the isolation test leans on — if <c>Step</c> ever swallowed its own exceptions, the tick test
    /// below would pass for the wrong reason.</summary>
    [Fact]
    public void StepPropagatesAThrowToItsCaller()
    {
        var mob = Rigged(ThrowMap, 5, 5);

        Assert.Throws<ArgumentNullException>(() =>
            _fx.World.UnderWorldLockForTest(() => World.MobAiTick.Step(_fx.World.MobTickContextForTest(ThrowMap), mob)));
    }

    // =====================================================================================================
    // The whole beat.
    // =====================================================================================================

    /// <summary>Two creatures on one map with a player watching; the first is rigged to throw, the second is
    /// an aggressive chaser standing five tiles north of the player. One beat through the real <c>Tick</c>:
    /// the chaser's step reaches the watcher's outbound as a 0x0C (source tile, facing south), and the log
    /// names the creature that was skipped and carries its exception.
    ///
    /// <para>Falsified by removing the per-mob try in <c>World.Tick</c>: the throw then escapes
    /// <c>TickOnceForTest</c> and the test is red before it reaches an assertion — which is today's shape,
    /// the abandoned beat with nothing flushed.</para>
    ///
    /// <para>The chaser's beat is deterministic: the unprovoked-aggro scan locks onto the only player, the
    /// chase step toward a target on the same column has one candidate direction, and the tile is free.
    /// It is registered SECOND so the sweep reaches it after the throw.</para></summary>
    [Fact]
    public void OneMobThrowingDoesNotCostTheOthersTheirPackets()
    {
        var (watcher, outbound) = _fx.Player("TickWatcher", TickMap, x: 5, y: 10);
        var faulty = Rigged(TickMap, 8, 5);
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
