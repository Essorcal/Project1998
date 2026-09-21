using System.Runtime;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shared;

namespace Server;

/// <summary>
/// Publishes <c>run/status.json</c> — the small document the launcher polls to show "N online" beside the
/// server name.
///
/// A FILE, NOT AN HTTP ENDPOINT, and that is the whole design. The alternative was a listening socket inside
/// the game process, which would mean a third public port, a second protocol parser on the hot path, and a
/// new denial-of-service surface in front of the world tick — all to publish one integer. Writing a file the
/// web server already in front of us can serve costs a timer and nothing else, and the blast radius of a bug
/// here is a stale number rather than a crashed world.
///
/// The first three fields are fixed by the launcher's <c>ServerStatus</c> DTO (camelCase, case-insensitive);
/// everything after them is for whoever is reading a load run, and the launcher ignores what it does not
/// know:
/// <code>
/// {
///   "online": true, "players": 12, "message": null,
///   "ticks": 41233, "slowTicks": 66,
///   "elapsedMs": 13738589, "beatMs": 1508442,
///   "phaseMs": { "(0) warm": 811, "lock-wait": 12, "(1) respawns": 2104,
///                "(3) viewports": 903117, "(6) time": 41, "other": 1992 },
///   "workingSetMb": 1141, "gcHeapMb": 214, "gcCommittedAtLastGcMb": 968,
///   "gcAvailableMb": 15776, "gcHighLoadMb": 14198,
///   "gen0": 1873, "gen1": 402, "gen2": 3, "gcMode": "server-concurrent-conserve5"
/// }
/// </code>
/// Its poll runs every 30s, so a 10s cadence here means the number is never more than one poll stale.
///
/// <para><c>elapsedMs</c>, <c>beatMs</c> and <c>phaseMs</c> are since-process-start totals, and they are
/// totals on purpose: a rate is <c>delta(phaseMs) / delta(elapsedMs)</c> between two samples, which a
/// reader can take over any span it likes, while a rate published here would have to pick the span for it.
/// <c>phaseMs</c> carries one key per phase, in the order the buckets run, named exactly as the
/// <c>SLOW TICK PHASES</c> log line names them so the log and the document are one vocabulary; its parts
/// sum to <c>beatMs</c>, <c>other</c> included. The tick thread writes those slots as the beat runs, so a
/// document can straddle a beat by up to one tick period — see <c>World.PhaseTotalsMs</c>.</para>
///
/// <para>THE MEMORY FIELDS, and why they are here rather than in the load-run driver. Every hold in the
/// 2026-09-16 to 2026-09-18 family recorded the game process's working set from <c>Get-Process</c>, and on
/// 2026-09-18 that number peaked at 1,141 MB against 366-442 MB in the three holds before it, from the same
/// 159 MB at launch, on server code that differed by one database-timeout PR
/// (<c>briefs/reports/hold-master-vs-245-opus.md</c>, "Answers" 5). The working set alone cannot say why: it
/// is committed-and-touched pages, and it moves when the GC's BUDGET moves as readily as when the program
/// retains more. <c>gcHeapMb</c> against <c>gcCommittedAtLastGcMb</c> separates those two — bytes the program
/// is keeping alive against bytes the GC had taken from the OS and not given back — and <c>gcAvailableMb</c>
/// with <c>gcHighLoadMb</c> record the machine state the GC sizes itself against, which is the variable that
/// actually differed between those holds. <c>gen0</c>/<c>gen1</c>/<c>gen2</c> close the other gap: the
/// <c>SLOW TICK</c> line prints a <c>gc</c> pause only on beats the watchdog already flagged, so a collection
/// on a healthy beat is invisible today and the log cannot count collections at all.
///
/// WHERE EACH FIELD COMES FROM, AND WHAT IT DATES TO:
/// <list type="table">
/// <item><term><c>workingSetMb</c></term><description><c>Process.WorkingSet64</c> — instantaneous</description></item>
/// <item><term><c>gcHeapMb</c></term><description><c>GC.GetTotalMemory(false)</c> — instantaneous</description></item>
/// <item><term><c>gcCommittedAtLastGcMb</c></term><description><c>GC.GetGCMemoryInfo().TotalCommittedBytes</c> — AS OF THE LAST COLLECTION</description></item>
/// <item><term><c>gcAvailableMb</c></term><description><c>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes</c> — as of the last collection; a machine constant</description></item>
/// <item><term><c>gcHighLoadMb</c></term><description><c>GC.GetGCMemoryInfo().HighMemoryLoadThresholdBytes</c> — as of the last collection; a machine constant</description></item>
/// <item><term><c>gen0</c>/<c>gen1</c>/<c>gen2</c></term><description><c>GC.CollectionCount(n)</c> — since-start counts</description></item>
/// <item><term><c>gcMode</c></term><description>the GC configuration this process started with, flavour then budget (<c>server-concurrent-conserve5</c>) — constant</description></item>
/// </list>
///
/// Read as deltas between two samples, exactly like the phase totals: <c>gen0</c>, <c>gen1</c> and
/// <c>gen2</c> are since-start counts and only rise, while the megabyte figures go both ways.
/// <c>gcHeapMb</c> is <c>GC.GetTotalMemory(false)</c> — the GC's own estimate, without forcing a collection,
/// so it is a floor that includes garbage not yet collected, not a live-set measurement.
///
/// <c>gcCommittedAtLastGcMb</c> IS NOT AN INSTANTANEOUS READING, and its name says so because a load run read
/// it as one and drew the wrong picture. <c>GC.GetGCMemoryInfo()</c> with no argument returns the record of
/// the LATEST collection, so the committed figure is what the GC had committed when that collection ran: it
/// reads <b>0</b> until the first collection of the process, and then holds one exact value until the next
/// collection. The 2026-09-18 row A hold (<c>briefs/reports/hold-row-a-opus.md</c>) shows both halves — 0 on
/// the first three samples, then exactly 188 MB for twelve consecutive samples while <c>workingSetMb</c> went
/// 240 to 1,121 MB and <c>gcHeapMb</c> went 36 to 1,054 MB. It changed only at the three samples where a
/// generation counter changed. So <c>gcHeapMb</c> can stand far ABOVE <c>gcCommittedAtLastGcMb</c> between
/// collections, and that is not a contradiction. Read the field beside <c>gen0</c>/<c>gen1</c>/<c>gen2</c>:
/// on a sample where a count stepped, the committed figure is fresh and can be set against that sample's
/// <c>workingSetMb</c>; on every other sample it is the last collection's figure repeated.
/// <c>gcAvailableMb</c> and <c>gcHighLoadMb</c> come from the same record and carry the same date, but they
/// are the machine's physical memory and its 90% threshold — constants that the runtime populates before any
/// collection has run, which is why they were already correct on the samples where the committed figure
/// was 0. There is NO instantaneous committed figure in the GC API: nothing short of forcing a collection
/// refreshes this record, and forcing one from a status timer would be worse than the imprecision.
///
/// All of it is read on the STATUS thread, none of it touches the world or any lock, and nothing here runs on
/// the tick path.
///
/// The cost is not symmetric, which is worth knowing before anyone moves these reads. Measured on this
/// machine (median of 7 runs of 100 renders, Debug): the document WITHOUT them renders in <b>7.6us</b>, the
/// five GC readings add <b>0.8us</b>, and <c>workingSetMb</c> alone adds <b>2.18ms</b> —
/// <c>Process.WorkingSet64</c> on Windows snapshots the whole system process table to answer one question.
/// At the 10s cadence that is 0.02% of the status thread and it is off the beat entirely, which is why it is
/// accepted here; it would not be acceptable anywhere near the tick.</para>
///
/// The launcher treats this as ENRICHMENT, not truth: it proves reachability by opening a socket to the login
/// port, and a missing or unreachable status document never downgrades a server it just reached. The one
/// exception is an explicit <c>"online": false</c>, which wins — that is the maintenance switch. Which is why
/// the shutdown hook below bothers to write one.
/// </summary>
public static class StatusFile
{
    /// <summary>Where to publish. "-" disables publishing entirely; an unset knob resolves blank here and
    /// takes the run directory's own default, which is why the fallback lives at this call site rather than
    /// in the declaration — it is computed from another knob.</summary>
    private static readonly string Path =
        ServerConfig.Current.StatusFile is { Length: > 0 } configured
            ? configured
            : System.IO.Path.Combine(Shared.RepoPaths.RunDir(), "status.json");

    private static readonly int IntervalMs = ServerConfig.Current.StatusMs;

    /// <summary>Optional operator note shown beside the count. Null leaves the launcher's own wording.</summary>
    private static readonly string? Message =
        ServerConfig.Current.StatusMessage is { Length: > 0 } note ? note : null;

    private static bool Disabled => Path == "-";

    /// <summary>The launcher's three fields, then the tick counters, then the phase instrument, then the
    /// memory instrument. Order matters only for readability — the launcher's DTO is case-insensitive and ignores what it does not
    /// know — so every addition goes on the end, where a reader of an old document and a reader of a new one
    /// see the same first three.</summary>
    private sealed record Doc(
        [property: JsonPropertyName("online")]     bool Online,
        [property: JsonPropertyName("players")]    int Players,
        [property: JsonPropertyName("message")]    string? Message,
        [property: JsonPropertyName("ticks")]      long Ticks,
        [property: JsonPropertyName("slowTicks")]  long SlowTicks,
        [property: JsonPropertyName("elapsedMs")]  long ElapsedMs,
        [property: JsonPropertyName("beatMs")]     long BeatMs,
        [property: JsonPropertyName("phaseMs")]    IReadOnlyDictionary<string, long> PhaseMs,
        [property: JsonPropertyName("workingSetMb")]  long WorkingSetMb,
        [property: JsonPropertyName("gcHeapMb")]      long GcHeapMb,
        [property: JsonPropertyName("gcCommittedAtLastGcMb")] long GcCommittedAtLastGcMb,
        [property: JsonPropertyName("gcAvailableMb")] long GcAvailableMb,
        [property: JsonPropertyName("gcHighLoadMb")]  long GcHighLoadMb,
        [property: JsonPropertyName("gen0")]          long Gen0,
        [property: JsonPropertyName("gen1")]          long Gen1,
        [property: JsonPropertyName("gen2")]          long Gen2,
        [property: JsonPropertyName("gcMode")]        string GcMode);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>The GC configuration this process was BUILT with, resolved once. It cannot change while the
    /// process runs, and it is in the document because the alternative — reading it off
    /// <c>Server.csproj</c> — says what the repository configures rather than what this process got: the
    /// mode is overridable at launch without a rebuild (<c>DOTNET_gcServer=0</c>, <c>DOTNET_gcConcurrent=0</c>),
    /// which is exactly the knob a hold that wants to test the GC's part in the working set would turn.
    ///
    /// <para>Concurrency comes from the <c>System.GC.Concurrent</c> AppContext switch rather than from
    /// <c>GCSettings.LatencyMode</c>: the latency mode is a property the PROGRAM can set at any moment and
    /// would report whatever was last assigned, while the switch is the configuration the runtime actually
    /// started with, which is the thing a hold's matrix row varies.</para>
    ///
    /// <para>The string is the GC FLAVOUR then its BUDGET: <c>server-concurrent-conserve5</c> on a process
    /// that got the repository's pinned <c>System.GC.ConserveMemory</c>, and <c>server-concurrent</c> with no
    /// third part on one that did not. The budget half is here for the same reason as the first: the setting
    /// is pinned in <c>Server/runtimeconfig.template.json</c> but overridable at launch without a rebuild
    /// (<c>DOTNET_GCConserveMemory</c>), and without it in the document a hold cannot tell a run that got the
    /// pinned budget from one whose launcher turned it off. It is the working set's other variable —
    /// <c>gcAvailableMb</c> says what the machine offered, this says how hard the GC was told to hold
    /// back.</para></summary>
    private static readonly string Mode = DescribeMode();

    /// <summary>The mode string, computed from this process's runtime configuration. A method rather than an
    /// expression in the field initialiser because the configuration it reads is settable
    /// (<c>AppContext.SetData</c>), so this is the seam a test drives: the field above is one call to it, and
    /// a test that sets the property and calls this is testing what the document renders rather than a copy
    /// of the rule.
    ///
    /// <para>The conserve suffix names <c>System.GC.ConserveMemory</c>, which the repository pins to 5 in
    /// <c>Server/runtimeconfig.template.json</c>. The host passes runtimeconfig <c>configProperties</c> to the
    /// runtime as strings, so the value arrives here as <c>"5"</c>; an <c>int</c> is accepted too because that
    /// is what <c>AppContext.SetData</c> in a test would hand it. Anything else — absent, unparseable, or an
    /// explicit 0, which is the dial's own "off" — renders exactly the string this field rendered before the
    /// setting existed, so an unpinned process is not silently described as a pinned one.</para></summary>
    internal static string DescribeMode()
    {
        string mode =
            (GCSettings.IsServerGC ? "server" : "workstation") +
            (AppContext.TryGetSwitch("System.GC.Concurrent", out bool concurrent) && !concurrent
                ? "-blocking" : "-concurrent");

        int conserve = AppContext.GetData("System.GC.ConserveMemory") switch
        {
            int i => i,
            string s when int.TryParse(s, out int parsed) => parsed,
            _ => 0,
        };

        return conserve == 0 ? mode : $"{mode}-conserve{conserve}";
    }

    /// <summary>Bytes to whole megabytes. Truncating, not rounding: every consumer of these fields reads
    /// them as a delta over minutes against figures in the hundreds, and a megabyte of truncation is far
    /// below the sampling noise of a 10s cadence.</summary>
    private static long Mb(long bytes) => bytes / (1024 * 1024);

    /// <summary>The document, as text, without touching the disk — the seam the counter test drives, and the
    /// one place the world's two counters are read, so a test of this method is a test of what the timer
    /// publishes rather than of a copy of it.</summary>
    internal static string Render(World world, bool online, int players)
    {
        // One walk of the world's totals, ordered: a plain Dictionary keeps insertion order for a set of
        // keys that is only ever added to in one pass, and the serialiser writes it in that order, so
        // `phaseMs` reads down the beat the way the buckets run rather than alphabetically.
        var totals = world.PhaseTotalsMs(out long beatMs);
        var phases = new Dictionary<string, long>(totals.Length);
        foreach (var (name, ms) in totals) phases[name] = ms;

        // Every line below reads THIS thread's view of the process and the GC. None of it touches `world`
        // beyond the call above, none of it takes a lock, and none of it runs on the tick thread — which is
        // the whole reason the memory instrument is here and not in the beat.
        //
        // `Process.GetCurrentProcess()` is a fresh, disposable handle each time on purpose: a cached Process
        // memoises its counters and would republish the working set it read at startup forever, which is a
        // silently wrong number of exactly the kind this document exists to avoid.
        long workingSet;
        using (var self = System.Diagnostics.Process.GetCurrentProcess()) workingSet = self.WorkingSet64;

        // ONE call, like PhaseTotalsMs's `out`: GetGCMemoryInfo snapshots the last collection's numbers
        // together, so committed, available and the high-load threshold in one document are one reading
        // rather than three that could straddle a collection.
        //
        // "the last collection's numbers" is literal, and it is why the committed field is NAMED for it:
        // TotalCommittedBytes is what the GC had committed when that collection ran, not now. It is 0 before
        // the first collection and steps only at collections. See the class doc.
        var gc = GC.GetGCMemoryInfo();

        return JsonSerializer.Serialize(
            new Doc(online, players, Message, world.Ticks, world.SlowTicks, world.ElapsedMs, beatMs, phases,
                    Mb(workingSet),
                    Mb(GC.GetTotalMemory(forceFullCollection: false)),
                    Mb(gc.TotalCommittedBytes), Mb(gc.TotalAvailableMemoryBytes),
                    Mb(gc.HighMemoryLoadThresholdBytes),
                    GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), Mode),
            Json);
    }

    public static async Task Loop(World world)
    {
        if (Disabled) { Log.Info($"status: publishing disabled (P1998_STATUS_FILE=-)"); return; }

        Log.Info($"status: publishing {Path} every {IntervalMs}ms");

        // Mark the server down on the way out. A stale file claiming online:true is harmless on the normal
        // path (the socket probe is what decides), but a launcher configured WITHOUT a proxy mapping has no
        // probe to fall back on and would show a dead server as up until someone noticed.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Write(world, false, 0);

        while (true)
        {
            try { Write(world, true, world.Online.Count); }
            catch (Exception ex) { Log.Warn("status file write failed — retrying next interval", ex); }
            // EXPECTED: cancellation at process exit is the only thing that lands here, and stopping is the
            // correct response. A write that FAILS is a different matter and is logged above.
            try { await Task.Delay(IntervalMs); } catch { return; }
        }
    }

    /// <summary>
    /// Write via a temp file plus an atomic rename. A reader polling on its own schedule will otherwise
    /// eventually catch a half-written file and parse-fail, which surfaces as the status pill flickering to
    /// "unreachable" for no reason anybody can reproduce.
    /// </summary>
    private static void Write(World world, bool online, int players)
    {
        if (Disabled) return;
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, Render(world, online, players));
        File.Move(tmp, Path, overwrite: true);
    }
}
