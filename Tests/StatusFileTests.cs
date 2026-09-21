using System.Text.Json;
using Server;
using Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Tests;

/// <summary>
/// What <c>run/status.json</c> publishes beside the player count: the two tick counters (<c>ticks</c>, every
/// beat the world has run, and <c>slowTicks</c>, every beat the watchdog reported), the per-phase totals, and
/// the memory instrument.
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

    // ===== the memory instrument =========================================================================

    /// <summary>The heap fields render, parse, and stand in the relation the runtime guarantees between
    /// them; the collection counts only ever rise; the mode string says something.
    ///
    /// <para>The silent failure this guards is the reason the fields exist at all. The 2026-09-18 load run
    /// recorded a 1,141 MB working set against 366-442 MB in the three holds before it
    /// (<c>briefs/reports/hold-master-vs-245-opus.md</c>) and had nothing to say about WHY, because the
    /// working set is the one memory number the server published. A heap field that is wired to the wrong
    /// counter — committed where the heap belongs, a per-call snapshot where a since-start count belongs —
    /// would publish a plausible megabyte figure that the next hold would build a conclusion on. So the
    /// assertions are about the RELATIONS the runtime fixes, not about any particular size: the committed
    /// figure cannot exceed the memory the GC believes the machine has, the high-load threshold is inside
    /// that figure, and a count of collections that have already happened cannot fall.</para>
    ///
    /// <para>WHAT THIS FACT DELIBERATELY NO LONGER ASSERTS, because neither is guaranteed and the row A hold
    /// (<c>briefs/reports/hold-row-a-opus.md</c>) shows both failing in the real document:
    /// <c>gcHeapMb ≤ gcCommittedAtLastGcMb</c> — the heap is instantaneous and the committed figure dates to
    /// the last collection, so the heap read 1,054 MB against a committed 188 MB on that run — and
    /// <c>gcCommittedAtLastGcMb &gt; 0</c>, which is false until the first collection of the process and read
    /// 0 on that run's first three samples. Both passed here only because the test host collects often; they
    /// were a latent flake asserting a relation the runtime does not fix. What IS guaranteed about the
    /// committed figure is asserted in
    /// <c>TheCommittedFigureIsTheLastCollectionsAndCoversThatCollectionsHeap</c>, which forces the collection
    /// the field dates itself to first.</para></summary>
    [Fact]
    public void TheMemoryFieldsRenderInTheRelationsTheRuntimeGuarantees()
    {
        var first = Memory();
        _fx.World.TickOnceWatchedForTest(slowMs: NeverSlow);
        var second = Memory();

        _out.WriteLine($"workingSetMb {second.WorkingSetMb}, gcHeapMb {second.HeapMb}, " +
                       $"gcCommittedAtLastGcMb {second.CommittedAtLastGcMb}, gcAvailableMb {second.AvailableMb}, " +
                       $"gcHighLoadMb {second.HighLoadMb}, gen0/1/2 {second.Gen0}/{second.Gen1}/{second.Gen2}, " +
                       $"gcMode {second.Mode}");

        // Sane on its own: a running process has a working set. `gcCommittedAtLastGcMb` gets no floor here —
        // it is legitimately 0 until this process's first collection.
        Assert.True(second.WorkingSetMb > 0, $"workingSetMb is {second.WorkingSetMb}");

        // Both of these come out of the same last-collection record, so the bound between them holds on any
        // render, including one taken before any collection has run (all three read 0 / the machine
        // constants).
        Assert.True(second.CommittedAtLastGcMb <= second.AvailableMb,
            $"gcCommittedAtLastGcMb {second.CommittedAtLastGcMb} exceeds gcAvailableMb {second.AvailableMb}");
        Assert.True(second.HighLoadMb > 0 && second.HighLoadMb <= second.AvailableMb,
            $"gcHighLoadMb {second.HighLoadMb} is not a threshold inside gcAvailableMb {second.AvailableMb}");

        // Since-start counts, read as deltas by every consumer, so the one thing they must not do is fall.
        Assert.True(second.Gen0 >= first.Gen0, $"gen0 fell {first.Gen0} -> {second.Gen0}");
        Assert.True(second.Gen1 >= first.Gen1, $"gen1 fell {first.Gen1} -> {second.Gen1}");
        Assert.True(second.Gen2 >= first.Gen2, $"gen2 fell {first.Gen2} -> {second.Gen2}");
        // gen0 >= gen1 >= gen2 by construction: a gen1 collects gen0 with it, a gen2 collects both.
        Assert.True(second.Gen0 >= second.Gen1 && second.Gen1 >= second.Gen2,
            $"collection counts are not nested: {second.Gen0}/{second.Gen1}/{second.Gen2}");

        Assert.False(string.IsNullOrWhiteSpace(second.Mode), "gcMode must name the mode this process got");

        // The launcher's three fields are still first and still mean what they meant.
        using var doc = JsonDocument.Parse(StatusFile.Render(_fx.World, online: true, players: 3));
        Assert.True(doc.RootElement.GetProperty("online").GetBoolean());
        Assert.Equal(3, doc.RootElement.GetProperty("players").GetInt32());
    }

    /// <summary>Right after a forced gen2, the published committed figure is that collection's committed
    /// bytes, and it covers the heap as that collection left it.
    ///
    /// <para>This is what remains true of the two assertions the relations fact used to make, once the field
    /// is read as what it is. <c>GC.GetGCMemoryInfo()</c> reports the LATEST collection, so both halves have
    /// to be pinned to a collection this test forces rather than to an arbitrary render: after a gen2 there
    /// has certainly been a collection (so the figure is above zero), and the heap read immediately after
    /// that collection is what that collection's committed pages were holding (so the heap does not exceed
    /// it). Between collections neither half holds — see the relations fact.</para>
    ///
    /// <para>The silent failure it guards is the one the old assertion was reaching for: a committed field
    /// wired to the wrong counter. The first assertion below is an equality against
    /// <c>TotalCommittedBytes</c> read directly, either side of the render, which is exact — the figure
    /// cannot move except at a collection, so "before or after" only allows for a collection landing inside
    /// the render, and any other value means the field is not this counter.</para></summary>
    [Fact]
    public void TheCommittedFigureIsTheLastCollectionsAndCoversThatCollectionsHeap()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        // Immediately after the collection, before anything else allocates: this is the heap as that
        // collection left it, and the committed record the document is about to publish.
        long heapBytes = GC.GetTotalMemory(forceFullCollection: false);
        long committedBefore = GC.GetGCMemoryInfo().TotalCommittedBytes;
        var reading = Memory();
        long committedAfter = GC.GetGCMemoryInfo().TotalCommittedBytes;

        _out.WriteLine($"forced gen2: heap {Mb(heapBytes)}MB, TotalCommittedBytes {Mb(committedBefore)}MB " +
                       $"-> {Mb(committedAfter)}MB, document gcCommittedAtLastGcMb " +
                       $"{reading.CommittedAtLastGcMb}MB");

        Assert.True(reading.CommittedAtLastGcMb == Mb(committedBefore) ||
                    reading.CommittedAtLastGcMb == Mb(committedAfter),
            $"the document says gcCommittedAtLastGcMb {reading.CommittedAtLastGcMb} while " +
            $"GC.GetGCMemoryInfo().TotalCommittedBytes read {Mb(committedBefore)}MB just before the render " +
            $"and {Mb(committedAfter)}MB just after — the field is not that counter");

        Assert.True(reading.CommittedAtLastGcMb > 0,
            $"a blocking gen2 has just run and the document still says gcCommittedAtLastGcMb " +
            $"{reading.CommittedAtLastGcMb} — the record is not being re-read at render");

        // Megabytes are truncated, so the two may read equal on a small process; the heap as the collection
        // left it can never be HIGHER than what that collection had committed to hold it.
        Assert.True(Mb(heapBytes) <= reading.CommittedAtLastGcMb,
            $"the heap read {Mb(heapBytes)}MB immediately after a blocking gen2 and that collection's " +
            $"committed figure is {reading.CommittedAtLastGcMb}MB — the GC cannot have held more managed " +
            $"bytes at that collection than it had committed pages for; the two fields are crossed");
    }

    /// <summary><c>gcMode</c> names the GC's memory BUDGET as well as its flavour: with
    /// <c>System.GC.ConserveMemory</c> set the string carries a <c>-conserveN</c> suffix, and with it unset
    /// the string is exactly what it was before the setting existed.
    ///
    /// <para>The silent failure: the repository pins that property in
    /// <c>Server/runtimeconfig.template.json</c>, and the one thing a hold uses the document for is telling a
    /// run that GOT the pinned budget from one whose launcher overrode it (<c>DOTNET_GCConserveMemory</c>) or
    /// whose publish dropped the template. A suffix hardcoded from the repository's own value, or one
    /// rendered whatever the property says, would read "conserve5" on both and the hold would price a setting
    /// the process never had. So the fact drives the real seam with the property set and with it absent, and
    /// asserts the document agrees with the seam.</para>
    ///
    /// <para>The test host runs workstation GC and pins nothing, so the flavour half differs from the game
    /// process and the unset case is the host's own state; what is under test is the suffix.</para></summary>
    [Fact]
    public void TheModeStringNamesTheConserveMemoryLevelThisProcessGot()
    {
        object? restore = AppContext.GetData("System.GC.ConserveMemory");
        try
        {
            AppContext.SetData("System.GC.ConserveMemory", null);
            string unset = StatusFile.DescribeMode();

            // The host hands runtimeconfig configProperties to the runtime as STRINGS, which is how the
            // pinned 5 actually arrives in the game process; the int case is what a caller in this process
            // would set.
            AppContext.SetData("System.GC.ConserveMemory", "5");
            string asString = StatusFile.DescribeMode();
            AppContext.SetData("System.GC.ConserveMemory", 5);
            string asInt = StatusFile.DescribeMode();

            // 0 is the dial's own "off" and must not be described as a pinned budget; a value that is not a
            // number at all is a misconfiguration the document must not invent a level for.
            AppContext.SetData("System.GC.ConserveMemory", "0");
            string off = StatusFile.DescribeMode();
            AppContext.SetData("System.GC.ConserveMemory", "yes");
            string junk = StatusFile.DescribeMode();

            _out.WriteLine($"unset '{unset}', \"5\" '{asString}', 5 '{asInt}', \"0\" '{off}', " +
                           $"\"yes\" '{junk}'");

            Assert.DoesNotContain("conserve", unset);
            Assert.Equal(unset + "-conserve5", asString);
            Assert.Equal(unset + "-conserve5", asInt);
            Assert.Equal(unset, off);
            Assert.Equal(unset, junk);
        }
        finally
        {
            AppContext.SetData("System.GC.ConserveMemory", restore);
        }

        // And the document publishes that function's answer rather than a second copy of the rule. The field
        // behind `gcMode` is resolved once at type init, so this holds for the process's own configuration —
        // which the finally above has just put back.
        Assert.Equal(StatusFile.DescribeMode(), Memory().Mode);
    }

    /// <summary>Bytes to whole megabytes, the document's own truncation, so a comparison against a directly
    /// read counter is a comparison of the same arithmetic.</summary>
    private static long Mb(long bytes) => bytes / (1024 * 1024);

    /// <summary>A collection that really happened moves <c>gen2</c>.
    ///
    /// <para>Stated separately from the relations above because it fails for a different reason: counts
    /// captured once and republished, or read off the wrong generation, satisfy every bound in that test and
    /// still make the next hold read "no gen2 ran" through a collection it watched happen. This is the only
    /// fact in the file that forces a collection, and it forces the one the instrument exists to date — the
    /// 270 MB fall in both sides of the 2026-09-18 pair has no <c>SLOW TICK</c> line anywhere near it,
    /// because the log prints a <c>gc</c> pause only on beats the watchdog already flagged.</para></summary>
    [Fact]
    public void AForcedGen2ShowsUpInTheDocumentsCollectionCounts()
    {
        long before = Memory().Gen2;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        long after = Memory().Gen2;

        _out.WriteLine($"gen2 {before} -> {after}");
        Assert.True(after >= before + 1,
            $"a forced blocking gen2 ran and the document still says gen2 {after} against {before} — the " +
            $"count is not being re-read at render");
    }

    // =====================================================================================================

    /// <summary>One reading of the published document's memory instrument, parsed — so these are facts about
    /// <c>run/status.json</c> rather than about a call this test could have made itself.</summary>
    private readonly record struct MemoryReading(long WorkingSetMb, long HeapMb, long CommittedAtLastGcMb,
                                                 long AvailableMb, long HighLoadMb,
                                                 long Gen0, long Gen1, long Gen2, string Mode);

    private MemoryReading Memory()
    {
        using var doc = JsonDocument.Parse(StatusFile.Render(_fx.World, online: true, players: 0));
        var r = doc.RootElement;
        long L(string n) => r.GetProperty(n).GetInt64();
        return new MemoryReading(L("workingSetMb"), L("gcHeapMb"), L("gcCommittedAtLastGcMb"), L("gcAvailableMb"),
                                 L("gcHighLoadMb"), L("gen0"), L("gen1"), L("gen2"),
                                 r.GetProperty("gcMode").GetString() ?? "");
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
