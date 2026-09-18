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
///                "(3) viewports": 903117, "(6) time": 41, "other": 1992 }
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

    /// <summary>The launcher's three fields, then the tick counters, then the phase instrument. Order
    /// matters only for readability — the launcher's DTO is case-insensitive and ignores what it does not
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
        [property: JsonPropertyName("phaseMs")]    IReadOnlyDictionary<string, long> PhaseMs);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

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

        return JsonSerializer.Serialize(
            new Doc(online, players, Message, world.Ticks, world.SlowTicks, world.ElapsedMs, beatMs, phases),
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
