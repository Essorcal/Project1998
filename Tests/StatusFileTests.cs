using System.Text.Json;
using Server;
using Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Tests;

/// <summary>
/// The two counters <c>run/status.json</c> publishes beside the player count: <c>ticks</c>, every beat the
/// world has run, and <c>slowTicks</c>, every beat the watchdog reported.
///
/// <para>Why they exist: both load reports state their headline — the share of beats that were slow — as a
/// wall-clock span divided by the 333ms period, because the server published no total. That is a division,
/// not a measurement, and it cannot be checked. These two make it <c>slowTicks / ticks</c>.</para>
///
/// <para>Which makes the silent failure worth guarding against an obvious one: a counter that does not
/// count. Nothing would throw, nothing would look wrong, and the next load run would publish a confident
/// wrong number — a 0% slow-beat rate reads like good news. So the facts below are about the ARITHMETIC
/// over a known number of beats, not about the document parsing.</para>
///
/// <para>Driven through <c>World.TickOnceWatchedForTest</c>, the same seam the phase tests use: it runs a
/// real beat and then the real watchdog with the threshold passed in. The <c>late</c> argument is what makes
/// a beat slow or healthy on purpose here — <c>work</c> on a near-empty map is a race with the machine,
/// while <c>late</c> is a number this test supplies, so "three slow beats and two healthy ones" is a fact
/// rather than a hope.</para>
///
/// <para>Deltas, never absolutes: the <c>world</c> collection's <c>World</c> is shared and every other test
/// class in it has been ticking the same counter. The collection serialises its classes, so a delta across
/// this method is exactly this method's beats.</para>
/// </summary>
[Collection("world")]
public class StatusFileTests
{
    private readonly SessionFixture _fx;
    private readonly ITestOutputHelper _out;

    public StatusFileTests(SessionFixture fx, ITestOutputHelper output) { _fx = fx; _out = output; }

    /// <summary>A threshold no beat on any machine can reach, so the beat is healthy by construction.</summary>
    private const int NeverSlow = 60_000;

    // =====================================================================================================

    /// <summary>Five beats, three of them slow, move <c>ticks</c> by five and <c>slowTicks</c> by three — and
    /// the numbers the status document carries are those, not a copy kept somewhere else.</summary>
    [Fact]
    public void TheStatusDocumentCountsEveryBeatAndEverySlowBeat()
    {
        long ticks0 = _fx.World.Ticks, slow0 = _fx.World.SlowTicks;

        // `late` past the threshold is the watchdog's other gate (work OR scheduling delay), so these three
        // are slow whatever the machine does with the beat itself.
        _fx.World.TickOnceWatchedForTest(slowMs: 1, late: 5_000);
        _fx.World.TickOnceWatchedForTest(slowMs: NeverSlow);
        _fx.World.TickOnceWatchedForTest(slowMs: 1, late: 5_000);
        _fx.World.TickOnceWatchedForTest(slowMs: NeverSlow);
        _fx.World.TickOnceWatchedForTest(slowMs: 1, late: 5_000);

        string json = StatusFile.Render(_fx.World, online: true, players: 7);
        _out.WriteLine(json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(5, root.GetProperty("ticks").GetInt64() - ticks0);
        Assert.Equal(3, root.GetProperty("slowTicks").GetInt64() - slow0);

        // The document the launcher already reads is unchanged: same three fields, same names, same meaning.
        // The counters are an addition to it, not a replacement for it.
        Assert.True(root.GetProperty("online").GetBoolean());
        Assert.Equal(7, root.GetProperty("players").GetInt32());
        Assert.True(root.TryGetProperty("message", out _), "the launcher's `message` field must survive");
    }

    /// <summary>A healthy beat moves <c>ticks</c> and leaves <c>slowTicks</c> alone. Stated separately from
    /// the count above because it fails for a different reason: a <c>slowTicks</c> wired to the beat instead
    /// of to the watchdog would pass an all-slow test and make every load run read 100%.</summary>
    [Fact]
    public void AHealthyBeatIsCountedButNotCountedAsSlow()
    {
        long ticks0 = _fx.World.Ticks, slow0 = _fx.World.SlowTicks;

        _fx.World.TickOnceWatchedForTest(slowMs: NeverSlow);

        using var doc = JsonDocument.Parse(StatusFile.Render(_fx.World, online: true, players: 0));
        Assert.Equal(1, doc.RootElement.GetProperty("ticks").GetInt64() - ticks0);
        Assert.Equal(0, doc.RootElement.GetProperty("slowTicks").GetInt64() - slow0);
    }

    // ===== the phase totals ==============================================================================

    /// <summary>Every phase's total only ever goes up, the beat total carries every beat — slow or healthy —
    /// and the parts sum to the whole.
    ///
    /// <para>The silent failure this guards: a total that is reset, or that is only advanced on the beats
    /// the watchdog logged, reads as a real number and makes the next load report state a phase's
    /// milliseconds per second as a fraction of the truth. That fraction is exactly what the log line alone
    /// could say, and it is the reason these fields exist.</para>
    ///
    /// <para>The beats are made to cost a KNOWN amount with <c>World.PhaseProbeForTest</c>, which runs
    /// inside <c>(4.3) status</c>, so "the beat total moved by at least the beats' cost" is a fact rather
    /// than a reading of how busy the machine was.</para></summary>
    [Fact]
    public void EveryPhaseTotalRisesWithTheBeatsAndThePartsSumToTheWhole()
    {
        const int SpinMs = 5, Beats = 3;

        var before = Sample();
        using (PhaseProbe.CostingAtLeast(SpinMs))
            for (int i = 0; i < Beats; i++) _fx.World.TickOnceWatchedForTest(slowMs: NeverSlow);
        var after = Sample();

        _out.WriteLine($"beatMs {before.BeatMs} -> {after.BeatMs}, (4.3) status " +
                       $"{before.Phase("(4.3) status")} -> {after.Phase("(4.3) status")}");

        // Monotonic, every NAMED phase: nothing here is ever cleared or recomputed.
        //
        // `other` is excluded on purpose, and it is the one figure in the document that can go DOWN. It is
        // the derived remainder — beatMs minus everything named, the log line's own definition — so it also
        // carries every named phase's truncation, and a phase crossing a millisecond boundary (0.9ms of
        // total becoming 1.1ms) moves a millisecond out of the remainder and into that phase. The dip is
        // bounded by one millisecond per phase, once, against totals that grow by thousands of milliseconds
        // a sample; the alternative — a remainder summed in raw ticks — would be monotonic but would leave
        // that residue unaccounted for, and the parts would then no longer sum to the whole.
        foreach (var (name, ms) in after.Phases)
        {
            if (name == "other") continue;
            Assert.True(ms >= before.Phase(name),
                $"`{name}` fell from {before.Phase(name)}ms to {ms}ms — a named phase total is never reset");
        }

        // Three healthy beats of at least 5ms each are in the whole AND in the phase that cost them. The
        // beats were healthy by construction, which is the point: the totals do not depend on the watchdog.
        Assert.True(after.BeatMs - before.BeatMs >= Beats * SpinMs,
            $"three beats of >= {SpinMs}ms moved beatMs by only {after.BeatMs - before.BeatMs}ms");
        Assert.True(after.Phase("(4.3) status") - before.Phase("(4.3) status") >= Beats * SpinMs,
            $"the cost was spent in `(4.3) status` and the phase total does not show it: " +
            $"{after.Phase("(4.3) status") - before.Phase("(4.3) status")}ms");

        // And the parts are the whole, exactly: `other` is defined as beatMs minus everything named, so
        // every phase's truncation lands there rather than going missing.
        Assert.Equal(after.BeatMs, after.Phases.Values.Sum());
    }

    /// <summary>A phase that costs a fraction of a millisecond on every beat shows its real total, not
    /// zero.
    ///
    /// <para>The silent failure: totals summed in MILLISECONDS truncate every beat to zero for any phase
    /// under the millisecond — which is most phases on a healthy beat — and the instrument would then
    /// report 0ms forever for precisely the phases nobody has measured yet, while looking like it worked.
    /// The totals are therefore summed in raw Stopwatch ticks and converted once, at render.</para>
    ///
    /// <para>Ten beats at 0.4ms is 4ms. The assertion is a floor of 3ms, not an equality: the probe spins
    /// for at least 0.4ms and the phase also carries the real <c>TickSleep</c>/<c>TickPoison</c> sweep, so
    /// the total can only come out higher. What it cannot do, if the arithmetic is right, is come out at
    /// 0.</para></summary>
    [Fact]
    public void APhaseCostingAFractionOfAMillisecondEveryBeatTotalsUpRatherThanTruncatingToZero()
    {
        const int Beats = 10;
        const double SpinMs = 0.4;

        long before = Sample().Phase("(4.3) status");
        using (PhaseProbe.CostingAtLeast(SpinMs))
            for (int i = 0; i < Beats; i++) _fx.World.TickOnceWatchedForTest(slowMs: NeverSlow);
        long delta = Sample().Phase("(4.3) status") - before;

        _out.WriteLine($"{Beats} beats x {SpinMs}ms in `(4.3) status` totalled {delta}ms");
        Assert.True(delta >= 3,
            $"{Beats} beats costing {SpinMs}ms each should total about {Beats * SpinMs}ms in " +
            $"`(4.3) status`; the document says {delta}ms. A per-beat conversion to milliseconds would " +
            $"floor every one of them to 0.");
    }

    // =====================================================================================================

    /// <summary>One reading of the published document's phase instrument, parsed — so every fact above is a
    /// fact about what <c>run/status.json</c> actually carries, not about a world field.</summary>
    private readonly record struct Reading(long BeatMs, Dictionary<string, long> Phases)
    {
        public long Phase(string name) => Phases.TryGetValue(name, out long ms) ? ms : -1;
    }

    private Reading Sample()
    {
        using var doc = JsonDocument.Parse(StatusFile.Render(_fx.World, online: true, players: 0));
        var root = doc.RootElement;
        var phases = new Dictionary<string, long>();
        foreach (var p in root.GetProperty("phaseMs").EnumerateObject()) phases[p.Name] = p.Value.GetInt64();
        return new Reading(root.GetProperty("beatMs").GetInt64(), phases);
    }
}
