using System.Text.Json;
using System.Text.Json.Serialization;

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
/// The shape is fixed by the launcher's <c>ServerStatus</c> DTO (camelCase, case-insensitive):
/// <code>{ "online": true, "players": 12, "message": null }</code>
/// Its poll runs every 30s, so a 10s cadence here means the number is never more than one poll stale.
///
/// The launcher treats this as ENRICHMENT, not truth: it proves reachability by opening a socket to the login
/// port, and a missing or unreachable status document never downgrades a server it just reached. The one
/// exception is an explicit <c>"online": false</c>, which wins — that is the maintenance switch. Which is why
/// the shutdown hook below bothers to write one.
/// </summary>
public static class StatusFile
{
    /// <summary>Where to publish. Empty or "-" disables publishing entirely.</summary>
    private static readonly string Path =
        Environment.GetEnvironmentVariable("P1998_STATUS_FILE") is { } p && p.Trim().Length > 0
            ? p.Trim()
            : System.IO.Path.Combine(Shared.RepoPaths.RunDir(), "status.json");

    private static readonly int IntervalMs =
        int.TryParse(Environment.GetEnvironmentVariable("P1998_STATUS_MS"), out var ms) && ms >= 1000
            ? ms : 10_000;

    /// <summary>Optional operator note shown beside the count. Null leaves the launcher's own wording.</summary>
    private static readonly string? Message =
        Environment.GetEnvironmentVariable("P1998_STATUS_MESSAGE") is { } m && m.Trim().Length > 0
            ? m.Trim() : null;

    private static bool Disabled => Path == "-";

    private sealed record Doc(
        [property: JsonPropertyName("online")]  bool Online,
        [property: JsonPropertyName("players")] int Players,
        [property: JsonPropertyName("message")] string? Message);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static async Task Loop(World world)
    {
        if (Disabled) { Log.Info($"status: publishing disabled (P1998_STATUS_FILE=-)"); return; }

        Log.Info($"status: publishing {Path} every {IntervalMs}ms");

        // Mark the server down on the way out. A stale file claiming online:true is harmless on the normal
        // path (the socket probe is what decides), but a launcher configured WITHOUT a proxy mapping has no
        // probe to fall back on and would show a dead server as up until someone noticed.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Write(false, 0);

        while (true)
        {
            try { Write(true, world.OnlinePlayerCount()); }
            catch (Exception ex) { Log.Info($"status: write failed: {ex.Message}"); }
            try { await Task.Delay(IntervalMs); } catch { return; }
        }
    }

    /// <summary>
    /// Write via a temp file plus an atomic rename. A reader polling on its own schedule will otherwise
    /// eventually catch a half-written file and parse-fail, which surfaces as the status pill flickering to
    /// "unreachable" for no reason anybody can reproduce.
    /// </summary>
    private static void Write(bool online, int players)
    {
        if (Disabled) return;
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new Doc(online, players, Message), Json));
        File.Move(tmp, Path, overwrite: true);
    }
}
