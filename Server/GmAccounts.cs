using Shared;

namespace Server;

/// <summary>
/// Who may run the "!" development/GM commands (Session.Chat's GM gate). Before this existed every one of
/// them — !item, !coins, !lvl, !warp, !summon, !reload — was open to any player who typed it, which is
/// harmless on a loopback test box and an instant economy collapse on a hosted one.
///
/// Deployment config, not game state, so it follows the flat-file tier (see the config-persistence note):
/// one normalized username per line in <c>data/gm_accounts.txt</c>, with <c>#</c> comments and blank lines
/// ignored, plus a comma-separated <c>NEXUS_GMS</c> environment override that is UNIONed with the file
/// (handy for a container that ships no writable data dir). Matching is case-insensitive via the same
/// <see cref="Auth.Key"/> normalization accounts and characters use, so "Brian", "brian" and "BRIAN" are
/// one GM.
///
/// EMPTY BY DEFAULT: a fresh deployment has no GMs at all, and the tooling is simply unreachable until an
/// operator opts in. That's the safe default for the internet-facing case — the failure mode of forgetting
/// to add yourself is "I can't use !warp", not "anyone can mint gold".
///
/// Re-read by <c>!reload</c> along with the rest of the file-backed content, so promoting/demoting a GM
/// doesn't need a restart. (A demotion only takes effect at the next command, not mid-command.)
/// </summary>
public static class GmAccounts
{
    private static readonly object Gate = new();
    private static HashSet<string> _keys = new();
    private static bool _loaded;

    /// <summary>The roster file — &lt;repo&gt;/data/gm_accounts.txt. Absent is normal (means "no GMs").</summary>
    public static string Path => System.IO.Path.Combine(RepoPaths.DataDir(), "gm_accounts.txt");

    public static bool IsGm(string username)
    {
        if (string.IsNullOrEmpty(username)) return false;
        EnsureLoaded();
        lock (Gate) return _keys.Contains(Auth.Key(username));
    }

    /// <summary>Re-read the roster (startup and !reload). Never throws: an unreadable file leaves the
    /// previous roster in place rather than silently promoting or demoting everyone.</summary>
    public static void Load()
    {
        var keys = new HashSet<string>();

        var env = Environment.GetEnvironmentVariable("NEXUS_GMS");
        if (!string.IsNullOrWhiteSpace(env))
            foreach (var n in env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                keys.Add(Auth.Key(n));

        try
        {
            if (File.Exists(Path))
                foreach (var raw in File.ReadAllLines(Path))
                {
                    var line = raw.Trim();
                    int hash = line.IndexOf('#');
                    if (hash >= 0) line = line[..hash].Trim();
                    if (line.Length > 0) keys.Add(Auth.Key(line));
                }
        }
        catch (Exception e)
        {
            Log.Info($"!! GM roster read failed ({Path}): {e.Message} — keeping the previous roster");
            lock (Gate) { _loaded = true; }
            return;
        }

        lock (Gate) { _keys = keys; _loaded = true; }
        Log.Info(keys.Count == 0
            ? $"[gm] no GM accounts configured (add one per line to {Path}, or set NEXUS_GMS) — ! commands are disabled for everyone"
            : $"[gm] {keys.Count} GM account(s) loaded from {Path}{(string.IsNullOrWhiteSpace(env) ? "" : " + NEXUS_GMS")}");
    }

    private static void EnsureLoaded()
    {
        lock (Gate) { if (_loaded) return; }
        Load();
    }
}
