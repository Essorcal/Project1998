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
        // synchronous=NORMAL is per-connection (unlike journal_mode=WAL, which is a persistent DB-file
        // setting) — reapply it on every connection, else this connection silently runs at SQLite's
        // default FULL.
        pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
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
);

-- RTK nmail (clif.c case 9/2/3 reuse boards_showposts/boards_readpost against board id 0 — a player's own
-- mailbox is really just a board only they can see). One row per piece of mail; position is 1-based within
-- the RECIPIENT's own mailbox (mirrors board_posts.position's per-board scoping). item_id<0 = no parcel
-- attached (see Mail.cs).
CREATE TABLE IF NOT EXISTS mail_posts (
  id        INTEGER PRIMARY KEY AUTOINCREMENT,
  recipient TEXT NOT NULL COLLATE NOCASE,
  position  INTEGER NOT NULL,
  sender    TEXT,
  topic     TEXT,
  body      TEXT,
  month     INTEGER,
  day       INTEGER,
  item_id     INTEGER NOT NULL DEFAULT -1,
  item_amount INTEGER NOT NULL DEFAULT 0,
  item_dura   INTEGER NOT NULL DEFAULT 0,
  claimed     INTEGER NOT NULL DEFAULT 0,
  is_read     INTEGER NOT NULL DEFAULT 0
);

-- Parcels: item/gold sent player-to-player, collected from a MessengerNpc (RTK Parcels table +
-- messenger.lua/Parcel.lua). SEPARATE from mail — RTK keeps them apart, and a gold parcel has no letter.
-- Name-addressed like mail_posts (offline recipients resolve by CharacterStore). item_id<0 = a GOLD
-- parcel (item_amount = the coin amount); item_id>=0 = an item stack (item_amount = count). position is
-- 1-based within the recipient's own queue (FIFO claim). See Server/Parcel.cs.
CREATE TABLE IF NOT EXISTS parcels (
  id        INTEGER PRIMARY KEY AUTOINCREMENT,
  recipient TEXT NOT NULL COLLATE NOCASE,
  position  INTEGER NOT NULL,
  sender    TEXT,
  item_id     INTEGER NOT NULL DEFAULT -1,
  item_amount INTEGER NOT NULL DEFAULT 0,
  item_dura   INTEGER NOT NULL DEFAULT 0,
  engrave     TEXT,
  month     INTEGER,
  day       INTEGER
);
";
            cmd.ExecuteNonQuery();
            _initialized = true;
        }
    }
}
