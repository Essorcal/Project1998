using Microsoft.Data.Sqlite;

namespace Shared;

/// <summary>
/// The single SQLite database shared by the login and game processes (accounts, characters, handoff
/// tokens, board posts). One file at &lt;repo&gt;/data/nexus.db in WAL mode so both processes can access it
/// concurrently: WAL allows many readers plus one writer across processes, and a per-connection
/// busy_timeout absorbs the brief lock waits when both write at once.
///
/// Content (items/mobs/warps/…) stays in flat files — this DB is only for MUTABLE state that must be
/// crash-safe and shared between the two processes.
/// </summary>
public static class Db
{
    private static readonly object InitGate = new();
    private static bool _initialized;
    private static string? _path;

    /// <summary>Absolute path of the database file (&lt;repo&gt;/data/nexus.db).</summary>
    public static string Path => _path ??= System.IO.Path.Combine(RepoPaths.DataDir(), "nexus.db");

    /// <summary>Open a ready-to-use connection (schema guaranteed to exist, busy_timeout set).</summary>
    public static SqliteConnection Open()
    {
        EnsureInitialized();
        var cn = new SqliteConnection($"Data Source={Path}");
        cn.Open();
        using var pragma = cn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return cn;
    }

    /// <summary>Create the data directory + schema once per process. Idempotent and thread-safe; safe to
    /// run from both processes (CREATE TABLE IF NOT EXISTS). WAL is a persistent DB setting, so setting it
    /// here once is enough for every later connection from either process.</summary>
    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (InitGate)
        {
            if (_initialized) return;
            System.IO.Directory.CreateDirectory(RepoPaths.DataDir());
            using var cn = new SqliteConnection($"Data Source={Path}");
            cn.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = @"
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;

CREATE TABLE IF NOT EXISTS accounts (
  username       TEXT PRIMARY KEY COLLATE NOCASE,
  pass_hash      TEXT,
  created_utc    INTEGER,
  last_login_utc INTEGER
);

CREATE TABLE IF NOT EXISTS characters (
  username    TEXT PRIMARY KEY COLLATE NOCASE,
  json        TEXT NOT NULL,
  updated_utc INTEGER
);

CREATE TABLE IF NOT EXISTS handoff_tokens (
  nonce_hash  TEXT PRIMARY KEY,
  username    TEXT NOT NULL,
  expires_utc INTEGER NOT NULL,
  consumed    INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS board_posts (
  id       INTEGER PRIMARY KEY AUTOINCREMENT,   -- internal rowid
  board_id INTEGER NOT NULL,
  position INTEGER NOT NULL,                     -- BrdPosition: 1-based within its own board (on the wire)
  author   TEXT,
  topic    TEXT,
  body     TEXT,
  month    INTEGER,
  day      INTEGER
);";
            cmd.ExecuteNonQuery();
            _initialized = true;
        }
    }
}
