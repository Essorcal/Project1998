using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Shared;

/// <summary>
/// Character persistence, keyed by (lowercased) username. Backed by the shared SQLite database
/// (<see cref="Db"/>): the whole <see cref="Character"/> is stored as a JSON blob in the `characters`
/// table. The object is a deep graph (inventory/equipment/bank/spells/quests/legends/portrait) that is
/// always loaded whole by key, so a JSON column keeps the exact prior semantics while gaining WAL crash-
/// safety and safe concurrent access from the two processes.
///
/// Why shared storage: the login channel (creation) and the game channel (world entry) are SEPARATE
/// processes, so the record written at creation must be visible to the game process at world entry.
///
/// The constructor still takes the legacy JSON directory — now used only as the ONE-TIME migration source
/// (and left in place afterwards as a backup). Existing state/chars/*.json are imported on first run.
/// </summary>
public sealed class CharacterStore
{
    private readonly string _jsonDir;   // legacy per-file store: migration source + on-disk backup

    // Character exposes public FIELDS (not properties); System.Text.Json ignores fields unless told.
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        IncludeFields = true,
    };

    public CharacterStore(string dir)
    {
        _jsonDir = dir;
        Db.EnsureInitialized();
        MigrateFromJsonIfNeeded();
    }

    /// <summary>The backing database file path (logged at startup so records are findable).</summary>
    public string Directory => Db.Path;

    // Normalize to a safe, case-insensitive key so "Snuggle" and "snuggle" are one account. Public so
    // World's online-session registry (duplicate-login guard) can key on the same identity.
    public static string Key(string name)
    {
        var s = new string((name ?? string.Empty).ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        return string.IsNullOrEmpty(s) ? "_" : s;
    }

    public bool Exists(string name) => CharacterExists(name);

    /// <summary>Static form of <see cref="Exists"/> — the table is keyed by the normalized username and the
    /// lookup touches no instance state, so the shared auth rule (Shared/LoginAuth) can ask "does this
    /// character exist?" without threading a store instance through. Case-insensitive (see <see cref="Key"/>).</summary>
    public static bool CharacterExists(string name)
    {
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM characters WHERE username=$u LIMIT 1;";
            cmd.Parameters.AddWithValue("$u", Key(name));
            return cmd.ExecuteScalar() is not null;
        }
        catch { return false; }
    }

    public Character? Load(string name)
    {
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT json FROM characters WHERE username=$u LIMIT 1;";
            cmd.Parameters.AddWithValue("$u", Key(name));
            if (cmd.ExecuteScalar() is not string s) return null;
            return JsonSerializer.Deserialize<Character>(s, Json);
        }
        catch { return null; }   // corrupt/legacy row -> treat as absent, caller falls back to a fresh char
    }

    /// <summary>Whole-graph upsert. Returns true on success, false if the write failed (a bad disk, or a
    /// concurrent in-memory mutation racing the JSON serialize — see Session.FlushNow). A caller that cares
    /// about durability (the dirty-flag autosave path) uses the return value to keep retrying instead of
    /// silently dropping the mutation; everyone else can ignore it exactly as before.</summary>
    public bool Save(Character c)
    {
        try
        {
            var json = JsonSerializer.Serialize(c, Json);
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = @"INSERT INTO characters(username, json, updated_utc) VALUES($u, $j, $t)
                                ON CONFLICT(username) DO UPDATE SET json=$j, updated_utc=$t;";
            cmd.Parameters.AddWithValue("$u", Key(c.Name));
            cmd.Parameters.AddWithValue("$j", json);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception e)
        {
            // Best effort; persistence must never crash a session — but a swallowed write failure is
            // otherwise invisible, so surface it.
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [db] !! Save('{c.Name}') failed: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Save several characters in ONE transaction — all of them land, or none of them do.
    ///
    /// <para>This exists for transfers BETWEEN characters. A trade calling <see cref="Save"/> twice is two
    /// independent commits, and a crash (or a single failed write) between them leaves the world with one
    /// half of the exchange applied: whichever half, that is either a duplicated stack or a destroyed one.
    /// The in-memory transfer logic can be as careful as it likes and still not fix that, because the
    /// problem is at the persistence boundary, not in the logic.</para>
    ///
    /// <para>Serialization happens BEFORE the transaction opens, so the (relatively slow) JSON work for a
    /// multi-KB character graph doesn't hold a write lock that every other session's save is queued behind.</para>
    /// </summary>
    public bool SaveMany(IReadOnlyList<Character> chars)
    {
        if (chars.Count == 0) return true;
        try
        {
            // Serialize first — outside the transaction, and eagerly, so a mid-serialize collection mutation
            // throws here (whole call returns false, caller retries) rather than half-way through a commit.
            var rows = new List<(string User, string Json)>(chars.Count);
            foreach (var c in chars) rows.Add((Key(c.Name), JsonSerializer.Serialize(c, Json)));

            using var cn = Db.Open();
            using var tx = cn.BeginTransaction();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var (user, json) in rows)
            {
                using var cmd = cn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO characters(username, json, updated_utc) VALUES($u, $j, $t)
                                    ON CONFLICT(username) DO UPDATE SET json=$j, updated_utc=$t;";
                cmd.Parameters.AddWithValue("$u", user);
                cmd.Parameters.AddWithValue("$j", json);
                cmd.Parameters.AddWithValue("$t", now);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [db] !! SaveMany({chars.Count}) failed: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Run <paramref name="work"/> and save <paramref name="c"/> in one transaction, so a database mutation
    /// and the character change that pays for it commit together. <paramref name="work"/> receives the open
    /// connection and transaction and must use them for every statement it issues.
    ///
    /// <para>The case this exists for is claiming a parcel or a piece of mail: the row is deleted from the
    /// queue and the item appears in the bag, and those must not be able to come apart. Previously the
    /// delete committed on its own and the character saved separately — a crash in between and the parcel
    /// was gone from the queue having never arrived.</para>
    ///
    /// <para>Returns false if anything threw, with the transaction rolled back. The CALLER is responsible
    /// for undoing its own in-memory change on false — this method cannot do that for it.</para>
    /// </summary>
    public bool SaveWith(Character c, Func<SqliteConnection, SqliteTransaction, bool> work)
    {
        try
        {
            using var cn = Db.Open();
            using var tx = cn.BeginTransaction();

            if (!work(cn, tx)) { tx.Rollback(); return false; }

            using var cmd = cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO characters(username, json, updated_utc) VALUES($u, $j, $t)
                                ON CONFLICT(username) DO UPDATE SET json=$j, updated_utc=$t;";
            cmd.Parameters.AddWithValue("$u", Key(c.Name));
            cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(c, Json));
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();

            tx.Commit();
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [db] !! SaveWith('{c.Name}') failed: {e.Message}");
            return false;
        }
    }

    // One-time import of the legacy state/chars/*.json into the DB. Idempotent: each record is inserted
    // only if that username is absent (INSERT OR IGNORE), so it never clobbers newer DB state and is safe
    // to run from both processes and on every startup. The JSON files are left in place as a backup.
    private void MigrateFromJsonIfNeeded()
    {
        try
        {
            if (!System.IO.Directory.Exists(_jsonDir)) return;
            var files = System.IO.Directory.GetFiles(_jsonDir, "*.json");
            if (files.Length == 0) return;

            int migrated = 0;
            foreach (var f in files)
            {
                Character? c;
                try { c = JsonSerializer.Deserialize<Character>(File.ReadAllText(f), Json); }
                catch { continue; }   // skip corrupt/legacy files
                if (c is null || string.IsNullOrEmpty(c.Name)) continue;

                using var cn = Db.Open();
                using var cmd = cn.CreateCommand();
                cmd.CommandText = @"INSERT OR IGNORE INTO characters(username, json, updated_utc)
                                    VALUES($u, $j, $t);";
                cmd.Parameters.AddWithValue("$u", Key(c.Name));
                cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(c, Json));
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                if (cmd.ExecuteNonQuery() > 0) migrated++;
            }
            if (migrated > 0)
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [db] migrated {migrated} character(s) " +
                                  $"from {_jsonDir} into {Db.Path} (JSON files kept as backup)");
        }
        catch { /* migration is best-effort; a fresh DB still works */ }
    }
}
