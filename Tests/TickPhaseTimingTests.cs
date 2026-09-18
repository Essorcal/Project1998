using System.Text;
using System.Text.RegularExpressions;
using Shared;
using Server;
using Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Tests;

/// <summary>
/// The slow-tick watchdog's phase breakdown: the second line it prints, which attributes the <c>work</c>
/// number on the first line to the labelled phases of <c>World.Tick</c> / <c>World.FlushTick</c>.
///
/// <para>The first bot load run (2026-09-12) found 26 slow ticks at 400 players on one map — work p50
/// 132ms against a 333ms period, lock-wait 0ms in every line — which said the tick BODY was too big but
/// not which part of it. These tests pin the answer's shape: the line only appears on a beat the watchdog
/// already reported, it names phases, and its parts sum to the beat rather than to whatever is convenient.
/// A number that does not add up is the failure mode worth guarding against here; an unattributed
/// remainder is meant to show as <c>other</c>, never to be quietly absorbed.</para>
///
/// <para>Driven through <c>World.TickOnceWatchedForTest</c>, which is one beat plus the watchdog with the
/// threshold passed in — <c>World.SlowTickMs</c> is a static readonly off an environment variable, fixed
/// the moment the type initialises, so a test cannot lower it. Everything past that gate is the production
/// emission path.</para>
///
/// <para>The log is captured with <c>Tests/Support/ConsoleTap.cs</c>, which is exclusive across the whole
/// test process (<c>Console.Out</c> is one global slot, and two collections swapping it at once lose each
/// other's redirect) and waits for <c>Log</c>'s writer thread to reach the console. No new logging seam:
/// that is separately queued work.</para>
///
/// <para>Hygiene, as in every class in the <c>world</c> collection: the fixture's <c>World</c> is shared
/// and has no teardown, so the creatures and the player seeded here are removed in a <c>finally</c>.</para>
/// </summary>
[Collection("world")]
public class TickPhaseTimingTests
{
    private readonly SessionFixture _fx;
    private readonly ITestOutputHelper _out;

    public TickPhaseTimingTests(SessionFixture fx, ITestOutputHelper output) { _fx = fx; _out = output; }

    // Content-free maps (no registry row, no terrain, no warps, no spawns), one per test so nothing is shared.
    private const ushort PhaseMap = 60050, QuietMap = 60051, SnapshotMap = 60052;

    /// <summary>A real beat's worth of wandering creatures, spread over a 64-tile square with the watcher in
    /// the middle: most are out of its viewport, which is the shape a real hunting map has (the load run's
    /// stall was 305 mobs and 400 players on one map).
    ///
    /// <para>They are no longer what makes the phase line have something to name — <c>PhaseProbe</c> is —
    /// and the fact below was run green with this set to 0 by hand. They are kept because a beat with
    /// nothing in it is not the beat this instrument exists to attribute: with them the line names several
    /// real phases, and the parts-sum-to-the-whole assertions are checked against a beat of tens of
    /// milliseconds rather than one of three.</para></summary>
    private const int Creatures = 4000;

    /// <summary>What <c>PhaseProbe</c> costs the beat inside <c>(4.3) status</c>. Comfortably above the 1ms
    /// floor <c>PhaseBreakdown</c> folds into <c>other</c>, and small enough beside a 4,000-creature beat
    /// that it does not distort what the line says the beat was spent on.</summary>
    private const int ProbeMs = 3;

    private const string Head = "SLOW TICK PHASES:";

    // =====================================================================================================

    /// <summary>A beat the watchdog reports prints the counts line and then the phases, and the phases add
    /// up to the work the counts line claims.
    ///
    /// <para><b>The named phase is one this fact pays for, not one it hopes the machine is slow enough to
    /// produce.</b> This assertion — that the line names a labelled bucket at all — went red once on a
    /// shared CI runner (PR #245) and was green on rerun and green locally. The cause is the 1ms floor
    /// <c>PhaseBreakdown</c> folds into <c>other</c>: the fact's premise was "4,000 wandering creatures cost
    /// whole milliseconds on any machine", which is a claim about the machine, and on a fast enough one
    /// every bucket truncates to zero and the line carries only <c>other</c>. A wider tolerance cannot fix
    /// that, because the claim under test IS the threshold behaviour. So <c>PhaseProbe</c> spends a known
    /// <c>ProbeMs</c> inside <c>(4.3) status</c> and the fact asserts that bucket by name: the outcome no
    /// longer depends on how fast the runner is.</para></summary>
    [Fact]
    public void ASlowBeatNamesItsPhasesAndThePartsSumToTheWhole()
    {
        var (watcher, outbound) = _fx.Player("PhaseWatcher", PhaseMap, x: 32, y: 32);
        try
        {
            Seed(PhaseMap, Creatures);
            outbound.Clear();

            string log;
            using (PhaseProbe.CostingAtLeast(ProbeMs))
                log = Captured(() => _fx.World.TickOnceWatchedForTest(slowMs: 1), Head);
            string[] lines = Lines(log);

            int counts = Array.FindIndex(lines, l => l.Contains("SLOW TICK: work "));
            Assert.True(counts >= 0, $"no SLOW TICK line in:\n{log}");
            int phases = counts + 1;
            Assert.True(phases < lines.Length && lines[phases].Contains(Head),
                $"the phase line should follow the counts line immediately; got:\n{log}");
            _out.WriteLine(lines[counts]);
            _out.WriteLine(lines[phases]);

            // It names phases, not just a remainder: at least one labelled bucket, and `other` is a bucket
            // and not a phase name.
            Assert.Matches(@"\(\d[\d.]*\) [a-z/]+ \d+ms", lines[phases]);

            // And the bucket it names is the one this fact paid for, carrying what it paid. This is the
            // assertion that makes the one above independent of the runner: `(4.3) status` cannot come in
            // under ProbeMs, so it cannot truncate into `other`, on any machine.
            long probed = Phase(lines[phases], @"\(4\.3\) status");
            Assert.True(probed >= ProbeMs,
                $"the probe spent {ProbeMs}ms inside `(4.3) status` and the line says {probed}ms — either " +
                $"the hook is not inside that bucket or the bucket is not measuring it:\n{lines[phases]}");

            // And the parts are the whole. That is two separate claims, and they are worth asserting
            // separately because they fail for different reasons.
            long work = long.Parse(Regex.Match(lines[counts], @"work (\d+)ms").Groups[1].Value);
            long sum = Parts(lines[phases]).Sum();

            // The parts may never exceed the whole. This is structural, not a timing coincidence: the mark
            // chain partitions the beat (each mark closes its bucket where the previous one ended), every
            // printed figure truncates down, and `other` is *defined* as floor(the beat) minus everything
            // named — so the line sums to exactly floor(_phaseTotal). `work`'s window strictly contains the
            // phase clock's (it opens before Tick calls BeginPhases and is read after EndPhases), so
            // floor(work) can only be the larger. Parts over the whole would mean the instrument
            // double-counts somewhere, which is the one thing a breakdown must never do — no tolerance.
            Assert.True(sum <= work,
                $"phases sum to {sum}ms but work is only {work}ms — the parts cannot exceed the beat:\n{lines[phases]}");

            // And it may not lose much of the beat either. This direction needs a tolerance, because the two
            // figures are not measuring quite the same window: `work`'s clock opens before Tick calls
            // BeginPhases and is read after EndPhases, so it carries a prologue (the GC counter read, the try
            // entry) that the phase clock cannot see, and the two then truncate to whole milliseconds
            // independently. That gap is measurement noise, not misattribution, and it is not zero —
            // measured here over 30 consecutive runs on a loaded machine, `work - sum` was 1ms on 28 of them
            // and 2ms on the other two, across beats of 25-47ms. A fixed 1ms would be flaky.
            //
            // So the slack is an eighth of the beat, floored at the 2ms the noise actually costs. Both halves
            // are measured, not picked: the floor is the worst gap seen in 72 runs, and an eighth keeps a
            // millisecond of headroom over that worst case at the beat it occurred on (2ms against 25ms).
            // The floor is the honest part — no black-box check of these two printed numbers can be tighter
            // than the instrument's own noise. The proportional part is the fix: what the old
            // `Math.Abs(...) <= 3` got wrong was not its size but that it stayed constant as the beat shrank,
            // so against a faster machine's beat it would wave through most of it. Scaled, the guard says the
            // same thing at any speed — the phases have to account for essentially all of the beat.
            long slack = Math.Max(2, work / 8);
            Assert.True(work - sum <= slack,
                $"phases sum to {sum}ms, work says {work}ms — {work - sum}ms of the beat is unattributed " +
                $"(slack {slack}ms), and an unattributed remainder is supposed to show as `other`:\n{lines[phases]}");
        }
        finally
        {
            _fx.World.ClearMap(PhaseMap);
            _fx.World.LeaveMap(watcher, PhaseMap);
        }
    }

    /// <summary>The same beat under a threshold it cannot reach prints neither line. The phase breakdown is
    /// an addition to the watchdog, not a second watchdog: a healthy beat stays silent.</summary>
    [Fact]
    public void AHealthyBeatPrintsNeitherLine()
    {
        var (watcher, outbound) = _fx.Player("QuietWatcher", QuietMap, x: 32, y: 32);
        try
        {
            Seed(QuietMap, 200);
            outbound.Clear();

            // The marker is enqueued on this thread AFTER the beat, into the same FIFO the watchdog would
            // have used, so seeing it means anything the beat logged has already been written.
            const string marker = "PHASE-TEST-MARKER quiet beat";
            string log = Captured(() =>
            {
                _fx.World.TickOnceWatchedForTest(slowMs: 60_000);
                Log.Warn(marker);
            }, marker);

            Assert.DoesNotContain("SLOW TICK", log);
        }
        finally
        {
            _fx.World.ClearMap(QuietMap);
            _fx.World.LeaveMap(watcher, QuietMap);
        }
    }

    /// <summary>The viewport phase is two different things and the line now says which is which: the
    /// <c>lock (_lock)</c> snapshot at the top of <c>ReconcileViews</c> is <c>(3.0) view snapshot</c>, and
    /// the lockless per-player sweep after it is <c>(3) viewports</c>.
    ///
    /// <para>Why this matters: load-run-2 measured <c>(3) viewports</c> leading 37 of 38 slow beats at 400
    /// players on one map, p50 111ms, while the whole-sweep bench models ~11ms of it. The snapshot contends
    /// with 400 session read-loop threads for <c>_lock</c> and is invisible to the <c>lock-wait</c> figure,
    /// which measures only <c>Tick</c>'s own acquisition — so until the bucket was split nobody could say
    /// which half the missing 100ms was in. A mark on the wrong side of the block would make the next load
    /// run answer that question wrongly and confidently, which is the silent failure this test exists for.</para>
    ///
    /// <para>The fact: with another thread holding <c>_lock</c> for a known interval, the snapshot bucket
    /// carries at least half of it and the sweep bucket does not. The beat is driven through
    /// <c>FlushTickWatchedForTest</c> rather than the whole tick, because <c>Tick</c> releases <c>_lock</c> a
    /// few instructions before <c>ReconcileViews</c> re-takes it and a thread re-entering a monitor it just
    /// released beats a blocked waiter essentially every time — through the whole beat the hold would land in
    /// <c>lock-wait</c> and never in the bucket under test.</para>
    ///
    /// <para><b>Not exposed to the 1ms floor the fact above had to be rescued from</b>, and deliberately
    /// not given the probe. It also asserts on two named buckets at <c>slowMs: 1</c>, but what it pays for
    /// them with is a 200ms hold this test takes itself, not work it hopes the machine is slow enough to
    /// make expensive: <c>(3.0) view snapshot</c> cannot come in under the floor while another thread is
    /// holding <c>_lock</c> across it, on a runner of any speed. The other direction — <c>(3) viewports</c>
    /// staying under 100ms — is the only machine-dependent claim here, and it is a claim that the sweep of
    /// 200 mobs for one viewer is not itself a tenth of a second, which is two orders of magnitude of
    /// headroom and would be a finding rather than a flake if it failed.</para></summary>
    [Fact]
    public void TheViewSnapshotBucketCarriesTheWorldLockAndTheSweepDoesNot()
    {
        var (watcher, outbound) = _fx.Player("SnapshotWatcher", SnapshotMap, x: 32, y: 32);
        try
        {
            Seed(SnapshotMap, 200);   // the map has to qualify for the snapshot, and the sweep has to be real
            outbound.Clear();

            // Long enough that the milliseconds between the holder signalling and the tick thread reaching
            // ReconcileViews cannot account for the assertion. The assertion is on half of it for the same
            // reason: what is being pinned is which side of the block the mark is on, not a stopwatch.
            const int HoldMs = 200;

            using var holding = new ManualResetEventSlim(false);
            var holder = new Thread(() => _fx.World.UnderWorldLockForTest(() =>
            {
                holding.Set();
                Thread.Sleep(HoldMs);
            })) { IsBackground = true, Name = "world-lock holder" };
            holder.Start();
            Assert.True(holding.Wait(TimeSpan.FromSeconds(10)), "the holder thread never took the world lock");

            string log = Captured(() => _fx.World.FlushTickWatchedForTest(slowMs: 1), Head);
            holder.Join();

            string[] lines = Lines(log);
            int phases = Array.FindIndex(lines, l => l.Contains(Head));
            Assert.True(phases >= 0, $"no phase line in:\n{log}");
            _out.WriteLine(lines[phases]);

            long snapshot = Phase(lines[phases], @"\(3\.0\) view snapshot");
            long sweep = Phase(lines[phases], @"\(3\) viewports");

            Assert.True(snapshot >= HoldMs / 2,
                $"`_lock` was held by another thread for {HoldMs}ms across the snapshot, so " +
                $"`(3.0) view snapshot` should carry it; it says {snapshot}ms:\n{lines[phases]}");
            Assert.True(sweep < HoldMs / 2,
                $"the sweep runs with no lock held and must not be inflated by the hold; " +
                $"`(3) viewports` says {sweep}ms:\n{lines[phases]}");
            Assert.True(snapshot > sweep,
                $"the wait belongs to the snapshot, not the sweep:\n{lines[phases]}");
        }
        finally
        {
            _fx.World.ClearMap(SnapshotMap);
            _fx.World.LeaveMap(watcher, SnapshotMap);
        }
    }

    // =====================================================================================================

    /// <summary>Creatures that all want to act on the very next beat (<c>MoveTime = 1</c>), so the wander
    /// sweep does its full per-mob work rather than skipping most of them on their own timers.</summary>
    private void Seed(ushort map, int count)
    {
        for (int i = 0; i < count; i++)
        {
            ushort x = (ushort)(i % 64), y = (ushort)(i / 64 % 64);
            _fx.World.AddMob(map, new Mob(_fx.World.AllocateMobId(), 1, x, y, "Phaser", 100)
            {
                Wander = true,
                MoveTime = 1,
            });
        }
    }

    /// <summary>Run <paramref name="body"/> with the console tapped, and return everything written once
    /// <paramref name="needle"/> has appeared (or the deadline passed — the caller's assertion then names
    /// what was missing).</summary>
    private static string Captured(Action body, string needle)
    {
        using var tap = ConsoleTap.Acquire();
        body();
        return tap.WaitFor(needle, TimeSpan.FromSeconds(10));
    }

    private static string[] Lines(string log) =>
        log.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>One named phase's figure off the line, or 0 when the phase is not named (under a
    /// millisecond, folded into <c>other</c>). The name is matched as an anchored pattern so
    /// <c>(3) viewports</c> cannot accidentally read <c>(3.0) view snapshot</c>'s number.</summary>
    private static long Phase(string line, string namePattern)
    {
        var m = Regex.Match(line, $@"(?<![\d.]){namePattern} (\d+)ms");
        return m.Success ? long.Parse(m.Groups[1].Value) : 0;
    }

    /// <summary>Every millisecond figure on the phase line, `other` included.</summary>
    private static IEnumerable<long> Parts(string line) =>
        Regex.Matches(line, @"(\d+)ms").Select(m => long.Parse(m.Groups[1].Value));

}
