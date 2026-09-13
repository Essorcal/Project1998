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
/// <para>The log is captured with the <c>ConsoleTap</c> pattern from <c>Tests/MobAiTickTests.cs</c> (the
/// same <c>Console.SetOut</c> swap restored in a <c>finally</c>, and the same <c>WaitFor</c>, because
/// <c>Log</c> is a queue drained by its own writer thread). No new logging seam: that is separately queued
/// work.</para>
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
    private const ushort PhaseMap = 60050, QuietMap = 60051;

    /// <summary>Enough wandering creatures that one beat costs whole milliseconds on any machine, so the
    /// phase line has something to name. They are spread over a 64-tile square with the watcher in the
    /// middle: most are out of its viewport, which is the shape a real hunting map has (the load run's stall
    /// was 305 mobs and 400 players on one map).</summary>
    private const int Creatures = 4000;

    private const string Head = "SLOW TICK PHASES:";

    // =====================================================================================================

    /// <summary>A beat the watchdog reports prints the counts line and then the phases, and the phases add
    /// up to the work the counts line claims.</summary>
    [Fact]
    public void ASlowBeatNamesItsPhasesAndThePartsSumToTheWhole()
    {
        var (watcher, outbound) = _fx.Player("PhaseWatcher", PhaseMap, x: 32, y: 32);
        try
        {
            Seed(PhaseMap, Creatures);
            outbound.Clear();

            string log = Captured(() => _fx.World.TickOnceWatchedForTest(slowMs: 1), Head);
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

            // And the parts are the whole. `work` is whole milliseconds off Stopwatch, measured over a
            // window a hair wider than the phase clock's (it also covers the GC counter read either side of
            // the beat), so one millisecond of truncation each way is expected and 3 is slack on top.
            long work = long.Parse(Regex.Match(lines[counts], @"work (\d+)ms").Groups[1].Value);
            long sum = Parts(lines[phases]).Sum();
            Assert.True(Math.Abs(work - sum) <= 3,
                $"phases sum to {sum}ms, work says {work}ms — the breakdown has to account for the beat:\n{lines[phases]}");
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
        var tap = new ConsoleTap();
        var prior = Console.Out;
        Console.SetOut(tap);
        try
        {
            body();
            return tap.WaitFor(needle, TimeSpan.FromSeconds(10));
        }
        finally { Console.SetOut(prior); }
    }

    private static string[] Lines(string log) =>
        log.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Every millisecond figure on the phase line, `other` included.</summary>
    private static IEnumerable<long> Parts(string line) =>
        Regex.Matches(line, @"(\d+)ms").Select(m => long.Parse(m.Groups[1].Value));

    /// <summary>A <c>Console.Out</c> stand-in that can be read back safely while <c>Log</c>'s writer thread
    /// is still appending to it — the same one <c>MobAiTickTests</c> uses.</summary>
    private sealed class ConsoleTap : TextWriter
    {
        private readonly StringBuilder _text = new();

        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { lock (_text) _text.Append(value); }
        public override void Write(string? value) { lock (_text) _text.Append(value); }
        public override void Write(char[] buffer, int index, int count) { lock (_text) _text.Append(buffer, index, count); }

        private string Snapshot() { lock (_text) return _text.ToString(); }

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
