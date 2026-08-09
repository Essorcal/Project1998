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

-- Moderation state, keyed by the same normalized username as accounts/characters. SEPARATE from `accounts`
-- so that table stays purely about authentication — and so adding this needed no migration of an existing
-- deployment's schema.
--
-- `*_until` is unix SECONDS: 0 = not banned/muted, otherwise the moment it lapses. A permanent action stores
-- Moderation.Forever (year 9999) rather than a -1 sentinel, so every check is the same `until > now`
-- comparison with no special case to forget.
CREATE TABLE IF NOT EXISTS moderation (
  username    TEXT PRIMARY KEY COLLATE NOCASE,
  ban_until   INTEGER NOT NULL DEFAULT 0,
  ban_reason  TEXT,
  ban_by      TEXT,
  ban_at      INTEGER,
  mute_until  INTEGER NOT NULL DEFAULT 0,
  mute_reason TEXT,
  mute_by     TEXT,
  mute_at     INTEGER
);

-- Account bans are trivially evaded by making a new character, IP bans catch a shared household. RTK keeps
-- both axes (ChaBanned + a BannedIP table) and so do we; a GM picks which one fits.
CREATE TABLE IF NOT EXISTS banned_ips (
  ip        TEXT PRIMARY KEY,
  until     INTEGER NOT NULL DEFAULT 0,
  reason    TEXT,
  banned_by TEXT,
  banned_at INTEGER
);

-- Append-only record of every moderation action, including the ones that UNDO something. A ban with no
-- record of who placed it and why is unreviewable, and who LIFTED a ban is the question that actually gets
-- asked. Never updated, never deleted.
CREATE TABLE IF NOT EXISTS mod_log (
  id     INTEGER PRIMARY KEY AUTOINCREMENT,
  at_utc INTEGER NOT NULL,
  actor  TEXT NOT NULL,
  action TEXT NOT NULL,
  target TEXT,
  detail TEXT
);

-- Small key/value store for state that belongs to the WORLD rather than to any character — currently the
-- in-game clock (RTK keeps the same thing in its `Time` table). Without this the calendar resets to its
-- compiled-in start on every restart, which stopped being harmless the moment deploys began restarting the
-- server on a schedule.
CREATE TABLE IF NOT EXISTS world_state (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

-- Login->game handoff nonces. `ip` is the address the login connection came from; the game arrival must
-- come from the same one. That binding is what carries the security when the nonce itself is short: the
-- client only echoes back the bytes its fixed-size handoff field has room for after the username, so a
-- long name leaves as little as one significant byte (see Shared/HandoffTokens).
CREATE TABLE IF NOT EXISTS handoff_tokens (
  nonce_hash  TEXT PRIMARY KEY,
  username    TEXT NOT NULL,
  expires_utc INTEGER NOT NULL,
  consumed    INTEGER NOT NULL DEFAULT 0,
  ip          TEXT NOT NULL DEFAULT ''
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

-- Map cells a PLAYER changed: doors opened, GM edits, event scripts. The .map files on disk are never
-- written and authored corrections live in data/game-data/MapCells.csv, so this table holds only the
-- diff against that authored baseline (MapData.RuntimeCells) — NOT the diff against the file. Baking
-- authored corrections in here would make the DB outrank the CSV, and editing the CSV would silently
-- stop working. One row per changed cell; the row is DELETED when a cell returns to its baseline (a
-- door toggled shut again), so the table stays proportional to what is actually open right now.
CREATE TABLE IF NOT EXISTS map_cells (
  map  INTEGER NOT NULL,
  x    INTEGER NOT NULL,
  y    INTEGER NOT NULL,
  tile INTEGER NOT NULL,
  pass INTEGER NOT NULL,
  obj  INTEGER NOT NULL,
  PRIMARY KEY (map, x, y)
);

-- Locked doors a player has opened (Doors.csv Locked/Key). Separate from map_cells because unlocking is
-- a separate axis from the open/closed GRAPHIC: a door can be unlocked but shut. Without this a key with
-- ConsumeKey=1 was spent and the door relocked on restart — the key gone and the door shut again.
CREATE TABLE IF NOT EXISTS map_unlocks (
  map          INTEGER NOT NULL,
  x            INTEGER NOT NULL,
  y            INTEGER NOT NULL,
  unlocked_utc INTEGER,
  PRIMARY KEY (map, x, y)
);
";
            cmd.ExecuteNonQuery();

            // Migrations for databases created before a column existed. CREATE TABLE IF NOT EXISTS above is
            // a no-op on an existing table, so a new column has to be added explicitly; ALTER TABLE throws
            // "duplicate column name" once it is already there, which is the success case on every later run.
            foreach (var alter in new[] { "ALTER TABLE handoff_tokens ADD COLUMN ip TEXT NOT NULL DEFAULT '';" })
            {
                try { using var mig = cn.CreateCommand(); mig.CommandText = alter; mig.ExecuteNonQuery(); }
                catch (SqliteException) { /* column already present */ }
            }
            _initialized = true;
        }
    }
}
