namespace Shared;

/// <summary>
/// Resolves paths anchored to the repository root, NOT the current working directory. Both server
/// processes (login + game) ship under the same repo root but launch with different
/// AppContext.BaseDirectory values; anchoring to the repo root keeps them pointed at the same
/// data/ directory (character store, SQLite db) no matter where each process is started from.
/// </summary>
public static class RepoPaths
{
    /// <summary>Repo root — the directory holding the .sln (or the Server + Shared project folders).
    /// Falls back to the current working directory if no marker is found.</summary>
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

    /// <summary>&lt;repo&gt;/data — the shared data directory both processes read/write.</summary>
    public static string DataDir() => Path.Combine(Root(), "data");

    /// <summary>&lt;repo&gt;/data/chars — the per-account character store directory.</summary>
    public static string CharsDir() => Path.Combine(DataDir(), "chars");
}
