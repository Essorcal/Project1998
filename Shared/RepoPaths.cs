namespace Shared;

/// <summary>
/// The ONE place that decides where anything on disk lives. Every path is anchored to the deployment
/// root, NOT the current working directory: the login and game servers are separate processes with
/// different AppContext.BaseDirectory values, and anchoring to the root is what makes them agree on a
/// single database, a single content set and a single log directory no matter where each is started.
///
/// THE SPLIT. There are two kinds of data here and they must never share a directory:
///
///   game-data/  CONTENT. The CSVs, the Lua, the .map terrain, SObj.tbl. Authored, versioned in its own
///               git repo (a submodule), identical on every deployment, and READ-ONLY at runtime. A
///               deploy replaces this wholesale.
///   state/      INSTANCE. The SQLite database, the character store, the staff rosters. Created by
///               running the server, unique to this one deployment, irreplaceable. A deploy must never
///               touch it, and it is the ONLY thing the backup has to capture.
///
/// Two more, deliberately outside BOTH of those, because they are neither authored content nor state
/// worth restoring:
///
///   logs/       Append-only stdout captures. Grows without bound, regenerable, never backed up. It used
///               to sit in the data directory, where a 67 MB rotated server.log was one careless glob
///               away from riding along in a content sync.
///   run/        Control triggers (restart_at, reload_now). A deploy writes them, the running server
///               consumes and deletes them. Meaningless the moment the process exits, so restoring one
///               from a backup would schedule a restart that nobody asked for.
///
/// They were all one `data/` directory before this. The cost showed up everywhere downstream: the deploy
/// rsync carried a hand-maintained exclude list of live-state filenames and could not use --delete
/// without eating the character store, and the content repo needed its own .gitignore purely to keep the
/// database it was sitting on top of out of its history. Separate roots delete both problems rather than
/// managing them.
///
/// Each root takes an environment override so a container (or a test) can place any of them anywhere —
/// the layout below is the default, not a requirement.
/// </summary>
public static class RepoPaths
{
    /// <summary>Deployment root — the directory holding the .sln (or the Server + Shared marker folders).
    /// Falls back to the current working directory if no marker is found.
    ///
    /// The fallback matters more than it looks. When this walk fails, content resolution has to fail the
    /// SAME way everything else does: a layout where the database was found but the content was not
    /// produced a server that started, listened, and accepted logins into a world with zero maps, zero
    /// mobs and zero NPCs, with nothing in the log that read as an error. One shared root means the
    /// process either finds its data or misses it as a whole, and the startup counts mean what they say.</summary>
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            bool isRoot = dir.GetFiles("*.sln").Length > 0
                       || (Directory.Exists(Path.Combine(dir.FullName, "Server"))
                           && Directory.Exists(Path.Combine(dir.FullName, "Shared")));
            if (isRoot) return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    private static string RootedDir(string envVar, string name)
    {
        var env = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(env) ? Path.Combine(Root(), name) : env;
    }

    /// <summary>&lt;root&gt;/game-data — authored content. Read-only at runtime. Override: NEXUS_GAME_DATA.</summary>
    public static string GameDataDir() => RootedDir("NEXUS_GAME_DATA", "game-data");

    /// <summary>&lt;root&gt;/state — live instance state, and the whole of what a backup must capture.
    /// Override: NEXUS_STATE.</summary>
    public static string StateDir() => RootedDir("NEXUS_STATE", "state");

    /// <summary>&lt;root&gt;/logs — stdout captures. Regenerable; not backed up. Override: NEXUS_LOGS.</summary>
    public static string LogsDir() => RootedDir("NEXUS_LOGS", "logs");

    /// <summary>&lt;root&gt;/run — deploy-to-server control triggers, consumed and deleted by the running
    /// process. Not state; not backed up. Override: NEXUS_RUN.</summary>
    public static string RunDir() => RootedDir("NEXUS_RUN", "run");

    /// <summary>A file under <see cref="GameDataDir"/>, with a per-file environment override that wins
    /// over both. The per-file overrides predate the directory one and are how a test or a bisect points
    /// a single table somewhere else without relocating the whole content set.</summary>
    public static string GameData(string envVar, params string[] parts) =>
        Environment.GetEnvironmentVariable(envVar) is { } e && !string.IsNullOrWhiteSpace(e)
            ? e
            : Path.Combine(new[] { GameDataDir() }.Concat(parts).ToArray());

    /// <summary>A file under <see cref="StateDir"/>.</summary>
    public static string State(params string[] parts) =>
        Path.Combine(new[] { StateDir() }.Concat(parts).ToArray());

    /// <summary>&lt;root&gt;/state/chars — the legacy per-account JSON character store. Superseded by the
    /// `characters` table in the database; kept as the one-time migration source and an on-disk backup,
    /// which is exactly why it belongs under state/ and inside the backup.</summary>
    public static string CharsDir() => State("chars");

    /// <summary>&lt;root&gt;/state/nexus.db — accounts, characters, boards, mail, parcels, moderation, the
    /// world clock. The single most important file on the host.</summary>
    public static string DbPath() => State("nexus.db");
}
