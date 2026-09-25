using System.Buffers.Binary;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The exception boundaries around the tick's pre-sweep phases, (1) to (1.7) (#106). Each phase that runs
/// under <c>World._lock</c> before the mob sweep has its own guard, so a phase that throws costs only the rest
/// of ITSELF: the phases after it, the mob sweep and <c>FlushTick</c> still run in the same beat. Before the
/// guards a throw there left <c>Tick</c> and reached <c>TickLoop</c>'s catch, which abandoned the whole beat
/// and every queue it would have flushed.
///
/// <para>How each fact tells "the beat went on" from "the beat was lost": <c>TickOnceForTest</c> is
/// <c>Tick</c> with no catch around it, so a throw a guard does not stop comes out of the call and the fact
/// goes red there. What must be true AFTER the call is then asserted on three separate observers: the later
/// phases were entered (<see cref="World.PreSweepProbeForTest"/> sees each one), the mob sweep ran for the
/// watched map (<see cref="World.SweepProbeForTest"/>), and something queued that beat reached the watcher's
/// recorder (so <c>FlushTick</c> ran).</para>
///
/// <para>The first fact is a real throw from a real line — a spawn point whose creature has a null key, so
/// <c>Materialize</c>'s <c>Content.MobSpawnRules.TryGetValue(d.Key, ...)</c> hands a null key to a
/// <c>Dictionary</c>. Since the spawn row guards that throw is caught one level below phase (1)'s own guard
/// and costs only its point (<see cref="SpawnRowGuardTests"/> has the group and throttle facts). No phase
/// has a throw of its own that a content-free setup can reach, so the per-phase facts throw from
/// <see cref="World.PreSweepProbeForTest"/>, which runs first inside each guard.</para>
///
/// <para>Hygiene, as in <see cref="MobAiTickTests"/>: the fixture's <c>World</c> is shared by the
/// <c>world</c> collection, so every probe is uninstalled, every seated player removed and every registered
/// creature or point undone in <c>finally</c>. The fault throttle is per world and keyed by phase and
/// exception type, so each fact throws its own type: a fact that reused another's (phase, type) inside the
/// throttle's quiet window would see its first throw counted rather than stacked.</para>
/// </summary>
[Collection("world")]
public class TickPhaseGuardTests
{
    private readonly SessionFixture _fx;

    public TickPhaseGuardTests(SessionFixture fx) => _fx = fx;

    // Content-free maps (no registry row, no terrain, no warps, no spawns). PerPhaseMap + i is the
    // per-phase fact's map for phase i, so its seven cases share nothing.
    private const ushort PointMap = 60046, ThrottleMap = 60047, PerPhaseMap = 60073;   // 60073..60079

    private const string StackPrefix = "world tick phase ";
    private const string RepeatNeedle = "world tick phase still throwing";

    private static MobDef Creature(int id, string key) =>
        new()
        {
            Id = id,
            Key = key,
            Name = "Test " + id,
            Look = 1,
            Color = 0,
            Hp = 100,
            Exp = 0,
            Level = 1,
            MoveTime = 1_000_000,
            Stationary = true,
        };

    /// <summary>Entity ids carried by the <c>0x07</c> creature-list frames: <c>count(u16)</c>, then 12 bytes
    /// per entity with the id at +4 (<see cref="ViewportRectTests"/>' reading of the same frame).</summary>
    private static List<uint> SpawnedIds(RecordingOutbound outbound) =>
        outbound.BodiesOf(0x07)
                .SelectMany(b =>
                {
                    int n = BinaryPrimitives.ReadUInt16BigEndian(b);
                    var ids = new List<uint>(n);
                    for (int i = 0; i < n; i++) ids.Add(BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(2 + i * 12 + 4)));
                    return ids;
                })
                .ToList();

    private List<Mob> MobsOf(Session viewer, ushort map, MobDef def) =>
        _fx.World.View(viewer, map).mobs.Where(m => m.DefId == def.Id).ToList();

    /// <summary>Phase (1), the natural case: three due spawn points on one watched map, the middle one's
    /// creature carrying a null key. In ONE beat the first point materialises, the second throws, and its
    /// spawn row guard (<c>SpawnDirector.RespawnDuePoints</c>) costs only that point: the third point
    /// materialises in the same beat, the mob sweep still runs for the map, <c>FlushTick</c> draws BOTH good
    /// creatures to the watcher, and the fault is written once, with its stack, after the lock is released —
    /// as a row fault naming the creature and the map, not as a phase fault.
    ///
    /// <para>Falsified by removing the row guard in <c>RespawnDuePoints</c> (the bare <c>Materialize</c>
    /// call, the #289 shape): red, the third point is absent (#289's phase guard catches the throw and ends
    /// the phase there). Falsified again with the row guard's catch logging straight from under the lock:
    /// red on the lock probe.</para></summary>
    [Fact]
    public void APointWhoseMaterialisationThrowsCostsOnlyThatPoint()
    {
        var before = Creature(990_101, "test_guard_before");
        var broken = Creature(990_102, null!);   // Materialize's first line refuses it: ArgumentNullException
        var after = Creature(990_103, "test_guard_after");

        var (watcher, outbound) = _fx.Player("PointGuardWatcher", PointMap, x: 5, y: 10);   // before the points, so entry materialises none
        var swept = new List<ushort>();
        _fx.World.SweepProbeForTest = id => swept.Add(id);
        try
        {
            _fx.World.UnderWorldLockForTest(() =>
            {
                var spawns = _fx.World.SpawnsForTest;
                spawns.AddPointForTest(PointMap, before, 3, 5, respawnEvery: 1, dueAt: 1);
                spawns.AddPointForTest(PointMap, broken, 5, 5, respawnEvery: 1, dueAt: 1);
                spawns.AddPointForTest(PointMap, after, 7, 5, respawnEvery: 1, dueAt: 1);
            });
            outbound.Clear();

            IReadOnlyList<LogLineSink.Entry> lines;
            using (var sink = LogLineSink.Acquire(probe: () => _fx.World.HoldsWorldLock))
            {
                _fx.World.TickOnceForTest();
                lines = sink.Lines;
            }

            // Phase (1) went past the throw: both good points materialised, the bad one did not.
            var first = Assert.Single(MobsOf(watcher, PointMap, before));
            Assert.Equal(((ushort)3, (ushort)5), (first.X, first.Y));
            var third = Assert.Single(MobsOf(watcher, PointMap, after));
            Assert.Equal(((ushort)7, (ushort)5), (third.X, third.Y));
            Assert.Empty(MobsOf(watcher, PointMap, broken));

            // The sweep ran for the map, and FlushTick drew both creatures phase (1) made this beat.
            Assert.Contains(PointMap, swept);
            Assert.Contains(first.Id, SpawnedIds(outbound));
            Assert.Contains(third.Id, SpawnedIds(outbound));

            // One stack, written with the lock released, naming the row and the real exception. The phase's
            // own guard never saw it.
            var ours = lines.Where(e => e.Line.Contains(StackPrefix)).ToList();
            var stack = Assert.Single(ours);
            Assert.Contains($"world tick phase (1) respawns: a spawn point threw — '' (creature id {broken.Id}) on map {PointMap}", stack.Line);
            Assert.DoesNotContain("world tick phase (1) respawns threw", stack.Line);
            Assert.Contains(nameof(ArgumentNullException), stack.Line);
            Assert.Contains("\n      ", stack.Line);   // Log.Detail's continuation: the stack is on it
            Assert.Equal(LogLevel.Error, stack.Level);
            Assert.False(stack.Probe, $"written with World._lock held: {stack.Line}");
        }
        finally
        {
            _fx.World.SweepProbeForTest = null;
            _fx.World.LeaveMap(watcher, PointMap);
            _fx.World.UnderWorldLockForTest(() => _fx.World.SpawnsForTest.ForgetMapForTest(PointMap));
        }
    }

    public static TheoryData<int> PreSweepPhaseIndexes()
    {
        var data = new TheoryData<int>();
        for (int i = 0; i < World.PreSweepPhasesForTest.Length; i++) data.Add(i);
        return data;
    }

    /// <summary>Every pre-sweep phase, one at a time: the phase throws at the top of its guard, and in that
    /// same beat every LATER phase is still entered, the mob sweep still runs for the watched map, the
    /// chaser's step still reaches the watcher through <c>FlushTick</c>, and the phase's stack is written
    /// once, after the lock. The chaser is <see cref="MobAiTickTests"/>' isolation-test chaser: aggressive,
    /// its move turn due on its first beat, five tiles north of the watcher, so its one beat is one step
    /// south and one <c>0x0C</c>.
    ///
    /// <para>Falsified per phase, all seven, by disabling that phase's guard in <c>World.Tick</c> (its catch
    /// given the never-true filter <c>when (e.HResult == 1)</c>): red on that phase's case, the rigged
    /// <c>InvalidOperationException</c> out of <c>TickOnceForTest</c>. Also falsified by logging (1.6)'s
    /// fault straight from its catch, under the lock: red on the lock probe.</para></summary>
    [Theory]
    [MemberData(nameof(PreSweepPhaseIndexes))]
    public void APreSweepPhaseThatThrowsCostsOnlyItsOwnRest(int index)
    {
        int target = World.PreSweepPhasesForTest[index];
        string name = World.PhaseNameForTest(target);
        ushort map = (ushort)(PerPhaseMap + index);

        var (watcher, outbound) = _fx.Player($"PhaseGuardWatcher{index}", map, x: 5, y: 10);
        var chaser = new Mob(_fx.World.AllocateMobId(), 1, 5, 5, "PhaseGuardChaser", 100)
        {
            Wander = true, Aggressive = true, MoveTime = 1,
        };
        _fx.World.AddMob(map, chaser);
        var entered = new List<int>();
        var swept = new List<ushort>();
        _fx.World.PreSweepProbeForTest = ph =>
        {
            entered.Add(ph);
            if (ph == target) throw new InvalidOperationException($"rigged fault in {name}");
        };
        _fx.World.SweepProbeForTest = id => swept.Add(id);
        try
        {
            outbound.Clear();
            IReadOnlyList<LogLineSink.Entry> lines;
            using (var sink = LogLineSink.Acquire(probe: () => _fx.World.HoldsWorldLock))
            {
                _fx.World.TickOnceForTest();
                lines = sink.Lines;
            }

            Assert.Equal(World.PreSweepPhasesForTest, entered);   // every phase entered, the later ones included
            Assert.Contains(map, swept);                           // the mob sweep ran
            var moves = outbound.BodiesOf(0x0C).Where(b => BinaryPrimitives.ReadUInt32BigEndian(b) == chaser.Id).ToList();
            Assert.Single(moves);                                  // …and FlushTick sent what it queued

            var ours = lines.Where(e => e.Line.Contains(StackPrefix)).ToList();
            var stack = Assert.Single(ours);
            Assert.Contains($"world tick phase {name} threw", stack.Line);
            Assert.Contains($"rigged fault in {name}", stack.Line);
            Assert.False(stack.Probe, $"written with World._lock held: {stack.Line}");
        }
        finally
        {
            _fx.World.PreSweepProbeForTest = null;
            _fx.World.SweepProbeForTest = null;
            _fx.World.DespawnMob(map, chaser);
            _fx.World.LeaveMap(watcher, map);
        }
    }

    private const int ThrottleBeats = 5;

    /// <summary>A phase that throws on EVERY beat costs the log one stack and then one line a beat, not a
    /// stack every 333 ms: <see cref="ThrottleBeats"/> beats of <c>(1.6) clock</c> throwing give one stack
    /// and <see cref="ThrottleBeats"/> - 1 repeat lines naming the phase and the exception type, all written
    /// with the lock released. Every beat still enters all seven phases.
    ///
    /// <para>Falsified by making <c>LogPhaseFaults</c> write the stack for every fault (its
    /// <c>Admit</c> test OR'd with a condition that is always true): red, "Assert.Single() Failure: The
    /// collection contained 5 matching items". Also falsified by logging the fault straight from (1.6)'s
    /// catch, under the lock: red on the lock probe ("written with World._lock held").</para></summary>
    [Fact]
    public void APhaseThatThrowsOnEveryBeatLogsOneStackThenOneLineABeat()
    {
        int clock = World.PreSweepPhasesForTest[5];
        string name = World.PhaseNameForTest(clock);
        Assert.Equal("(1.6) clock", name);

        var (watcher, _) = _fx.Player("PhaseThrottleWatcher", ThrottleMap, x: 5, y: 10);
        int entered = 0;
        _fx.World.PreSweepProbeForTest = ph =>
        {
            entered++;
            if (ph == clock) throw new NotSupportedException("rigged clock fault");
        };
        try
        {
            IReadOnlyList<LogLineSink.Entry> lines;
            using (var sink = LogLineSink.Acquire(probe: () => _fx.World.HoldsWorldLock))
            {
                for (int beat = 0; beat < ThrottleBeats; beat++) _fx.World.TickOnceForTest();
                lines = sink.Lines;
            }

            Assert.Equal(ThrottleBeats * World.PreSweepPhasesForTest.Length, entered);

            string stackNeedle = $"world tick phase {name} threw";
            string repeatNeedle = $"{name} {nameof(NotSupportedException)}";
            var ours = lines.Where(e => e.Line.Contains(stackNeedle) || (e.Line.Contains(RepeatNeedle) && e.Line.Contains(repeatNeedle))).ToList();
            Assert.All(ours, e => Assert.False(e.Probe, $"written with World._lock held: {e.Line}"));

            var stack = Assert.Single(ours, e => e.Line.Contains(stackNeedle));
            Assert.Contains("rigged clock fault", stack.Line);
            Assert.Contains("\n      ", stack.Line);
            Assert.Equal(ThrottleBeats - 1, ours.Count(e => e.Line.Contains(RepeatNeedle)));
        }
        finally
        {
            _fx.World.PreSweepProbeForTest = null;
            _fx.World.LeaveMap(watcher, ThrottleMap);
        }
    }
}
