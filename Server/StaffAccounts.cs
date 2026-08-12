using Shared;

namespace Server;

/// <summary>What a session is allowed to reach in the '@' command table. Ordered, and compared with
/// <c>&gt;=</c> — every tier can run everything the tier below it can, so <see cref="Gm"/> is a strict
/// superset of <see cref="Tester"/>.</summary>
public enum AccessLevel
{
    /// <summary>An ordinary player: the handful of commands that are part of the game (@whisper, @party, …).</summary>
    Player = 0,
    /// <summary>Someone testing CONTENT rather than running the world. They may rebuild their own character
    /// (@lvl / @class / @mark / @stats / @align) and summon items and coin to try things with, plus the
    /// harmless self-affecting toys (@ride, @weapon, @hurt). Deliberately NOT: teleporting (@warp), spawning
    /// monsters (@summon / @spawn / @cre / @kill), the raw-protocol and sprite labs, or @reload — a tester who
    /// breaks their own character costs nothing, and everything withheld here affects the live world or can
    /// crash a client.</summary>
    Tester = 1,
    /// <summary>Full operator access: everything.</summary>
    Gm = 2,
}

/// <summary>
/// Who may run the '@' development commands, and at which <see cref="AccessLevel"/>. Before this existed
/// every one of them — @item, @coins, @lvl, @warp, @summon, @reload — was open to any player who typed it,
/// which is harmless on a loopback test box and an instant economy collapse on a hosted one.
///
/// Deployment config, not game state, so it follows the flat-file tier (see the config-persistence note):
/// one normalized username per line in <c>state/gm_accounts.txt</c> (full access) and
/// <c>state/tester_accounts.txt</c> (the character/item subset), with <c>#</c> comments and blank lines
/// ignored, plus comma-separated <c>NEXUS_GMS</c> / <c>NEXUS_TESTERS</c> environment overrides that are
/// UNIONed with their file (handy for a container that ships no writable data dir). Matching is
/// case-insensitive via the same <see cref="Auth.Key"/> normalization accounts and characters use, so
/// "Brian", "brian" and "BRIAN" are one person. A name in BOTH rosters is a GM — the tiers are a floor, not
/// a partition, so listing someone twice can only ever grant, never take away.
///
/// EMPTY BY DEFAULT: a fresh deployment has no staff at all, and the tooling is simply unreachable until an
/// operator opts in. That's the safe default for the internet-facing case — the failure mode of forgetting
/// to add yourself is "I can't use @warp", not "anyone can mint gold".
///
/// Re-read by <c>@reload</c> along with the rest of the file-backed content, so promoting/demoting doesn't
/// need a restart. (A demotion only takes effect at the next command, not mid-command.)
/// </summary>
public static class StaffAccounts
{
    private static readonly object Gate = new();
    private static HashSet<string> _gms = new();
    private static HashSet<string> _testers = new();
    private static bool _loaded;

    /// <summary>The GM roster file — &lt;root&gt;/state/gm_accounts.txt. Absent is normal (means "no GMs").</summary>
    public static string GmPath => RepoPaths.State("gm_accounts.txt");
    /// <summary>The tester roster file — &lt;root&gt;/state/tester_accounts.txt. Absent is normal.</summary>
    public static string TesterPath => RepoPaths.State("tester_accounts.txt");

    /// <summary>The tier this username sits at. GM wins over tester; unlisted is <see cref="AccessLevel.Player"/>.</summary>
    public static AccessLevel LevelFor(string username)
    {
        if (string.IsNullOrEmpty(username)) return AccessLevel.Player;
        EnsureLoaded();
        var key = Auth.Key(username);
        lock (Gate)
            return _gms.Contains(key) ? AccessLevel.Gm
                 : _testers.Contains(key) ? AccessLevel.Tester
                 : AccessLevel.Player;
    }

    /// <summary>Full-access check (the user-list colour, the death-penalty exemption, the '!' nudge). A
    /// tester is NOT a GM here on purpose: those three are world-facing privileges, not tooling.</summary>
    public static bool IsGm(string username) => LevelFor(username) == AccessLevel.Gm;

    /// <summary>Re-read both rosters (startup and @reload). Never throws: an unreadable file leaves the
    /// previous roster in place rather than silently promoting or demoting everyone.</summary>
    public static void Load()
    {
        var gms = ReadRoster(GmPath, "NEXUS_GMS");
        var testers = ReadRoster(TesterPath, "NEXUS_TESTERS");
        if (gms is null && testers is null) { lock (Gate) _loaded = true; return; }

        lock (Gate)
        {
            if (gms is not null) _gms = gms;
            if (testers is not null) _testers = testers;
            _loaded = true;
            gms = _gms; testers = _testers;
        }
        Log.Info(gms.Count == 0
            ? $"[staff] no GM accounts configured (add one per line to {GmPath}, or set NEXUS_GMS) — the GM tier is disabled for everyone"
            : $"[staff] {gms.Count} GM account(s) + {testers.Count} tester account(s) loaded");
    }

    /// <summary>One roster file UNIONed with its environment override. Null means "the read failed" — the
    /// caller keeps the previous set rather than emptying it.</summary>
    private static HashSet<string>? ReadRoster(string path, string envVar)
    {
        var keys = new HashSet<string>();

        var env = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(env))
            foreach (var n in env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                keys.Add(Auth.Key(n));

        try
        {
            if (File.Exists(path))
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    int hash = line.IndexOf('#');
                    if (hash >= 0) line = line[..hash].Trim();
                    if (line.Length > 0) keys.Add(Auth.Key(line));
                }
        }
        catch (Exception e)
        {
            Log.Info($"!! staff roster read failed ({path}): {e.Message} — keeping the previous roster");
            return null;
        }
        return keys;
    }

    private static void EnsureLoaded()
    {
        lock (Gate) { if (_loaded) return; }
        Load();
    }
}
