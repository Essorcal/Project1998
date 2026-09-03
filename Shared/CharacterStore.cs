using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Shared;

public enum CharacterLoadStatus
{
    Ok,
    NotFound,
    Unreadable,
}

/// <summary>The three possible outcomes of reading a character row. An unreadable row is never absence.</summary>
public sealed record CharacterLoadResult(
    CharacterLoadStatus Status,
    Character? Character = null,
    string? Reason = null);

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
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
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

    public CharacterLoadResult Load(string name)
    {
        string user = Key(name);
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT json FROM characters WHERE username=$u LIMIT 1;";
            cmd.Parameters.AddWithValue("$u", user);
            if (cmd.ExecuteScalar() is not string s)
                return new CharacterLoadResult(CharacterLoadStatus.NotFound);

            var character = Deserialize(s);
            return new CharacterLoadResult(CharacterLoadStatus.Ok, character);
        }
        catch (Exception e)
        {
            string reason = e.Message;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [db] !! Load('{user}') failed: {reason}");
            return new CharacterLoadResult(CharacterLoadStatus.Unreadable, Reason: reason);
        }
    }

    /// <summary>
    /// The character graph as the bytes that would be stored — a SNAPSHOT, taken separately from the write
    /// that ships it.
    ///
    /// <para>Splitting these two is what lets a caller hold its own lock across the part that has to be
    /// consistent and drop it across the part that touches a disk. <c>Session.FlushNow</c> serializes here
    /// under the session's state monitor, so no handler or tick can land a half-applied change in the middle
    /// of the graph, and then writes the resulting string with the monitor released — a synchronous SQLite
    /// write under it would put the world tick behind the autosave thread's disk I/O (#29).</para>
    /// </summary>
    public static string Serialize(Character c) => JsonSerializer.Serialize(c, Json);

    /// <summary>Upgrade raw JSON to the current schema, then bind it with strict member handling.</summary>
    public static Character Deserialize(string json)
    {
        var root = JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonNode>(json, Json);
        var upgraded = CharacterUpgrader.Upgrade(root);
        return upgraded.Deserialize<Character>(Json)
            ?? throw new JsonException("Character JSON deserialized to null.");
    }

    /// <summary>Whole-graph upsert. Returns true on success, false if the write failed. A caller that cares
    /// about durability (the dirty-flag autosave path) uses the return value to keep retrying instead of
    /// silently dropping the mutation; everyone else can ignore it exactly as before.
    ///
    /// <para>Serializing and writing in one call is only safe when the caller can guarantee nothing mutates
    /// <paramref name="c"/> for the duration. A caller that cannot takes the snapshot itself
    /// (<see cref="Serialize"/>) and writes it with <see cref="SaveJson"/>.</para></summary>
    public bool Save(Character c) => SaveJson(Key(c.Name), Serialize(c));

    /// <summary>Write an already-serialized character snapshot (see <see cref="Serialize"/>) against the
    /// normalized <paramref name="user"/> key.</summary>
    public bool SaveJson(string user, string json)
    {
        try
        {
            using var cn = Db.Open();
            using var tx = cn.BeginTransaction(deferred: false);
            if (!CanOverwrite(cn, tx, user, out string? reason))
            {
                tx.Rollback();
                LogRefusedOverwrite(user, reason!);
                return false;
            }
            using var cmd = cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO characters(username, json, updated_utc) VALUES($u, $j, $t)
                                ON CONFLICT(username) DO UPDATE SET json=$j, updated_utc=$t;";
            cmd.Parameters.AddWithValue("$u", user);
            cmd.Parameters.AddWithValue("$j", json);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
            tx.Commit();
            return true;
        }
        catch (Exception e)
        {
            // Best effort; persistence must never crash a session — but a swallowed write failure is
            // otherwise invisible, so surface it.
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [db] !! Save('{user}') failed: {e.Message}");
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
        var rows = new List<(string User, string Json)>(chars.Count);
        foreach (var c in chars) rows.Add((Key(c.Name), Serialize(c)));
        return SaveManyJson(rows);
    }

    /// <summary>The already-snapshotted form of <see cref="SaveMany"/>, for the same reason
    /// <see cref="SaveJson"/> exists: <c>Session.FlushPair</c> takes both characters' snapshots under both
    /// sessions' state monitors and commits them here with the monitors released.</summary>
    public bool SaveManyJson(IReadOnlyList<(string User, string Json)> rows)
    {
        if (rows.Count == 0) return true;
        try
        {
            using var cn = Db.Open();
            using var tx = cn.BeginTransaction(deferred: false);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var (user, json) in rows)
            {
                if (!CanOverwrite(cn, tx, user, out string? reason))
                    throw new InvalidOperationException(
                        $"Refusing to overwrite unreadable character row '{user}': {reason}");

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
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [db] !! SaveMany({rows.Count}) failed: {e.Message}");
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
            using var tx = cn.BeginTransaction(deferred: false);

            string user = Key(c.Name);
            if (!CanOverwrite(cn, tx, user, out string? reason))
            {
                tx.Rollback();
                LogRefusedOverwrite(user, reason!);
                return false;
            }

            if (!work(cn, tx)) { tx.Rollback(); return false; }

            using var cmd = cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO characters(username, json, updated_utc) VALUES($u, $j, $t)
                                ON CONFLICT(username) DO UPDATE SET json=$j, updated_utc=$t;";
            cmd.Parameters.AddWithValue("$u", user);
            cmd.Parameters.AddWithValue("$j", Serialize(c));
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
                try { c = Deserialize(File.ReadAllText(f)); }
                catch { continue; }   // skip corrupt/legacy files
                if (c is null || string.IsNullOrEmpty(c.Name)) continue;

                using var cn = Db.Open();
                using var cmd = cn.CreateCommand();
                cmd.CommandText = @"INSERT OR IGNORE INTO characters(username, json, updated_utc)
                                    VALUES($u, $j, $t);";
                cmd.Parameters.AddWithValue("$u", Key(c.Name));
                cmd.Parameters.AddWithValue("$j", Serialize(c));
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                if (cmd.ExecuteNonQuery() > 0) migrated++;
            }
            if (migrated > 0)
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [db] migrated {migrated} character(s) " +
                                  $"from {_jsonDir} into {Db.Path} (JSON files kept as backup)");
        }
        catch { /* migration is best-effort; a fresh DB still works */ }
    }

    /// <summary>
    /// An unreadable existing row is irreplaceable evidence, not an empty slot. Validate it inside the same
    /// transaction as the write so no save path — including a stale duplicate session — can erase it.
    /// </summary>
    private static bool CanOverwrite(
        SqliteConnection cn,
        SqliteTransaction tx,
        string user,
        out string? reason)
    {
        using var read = cn.CreateCommand();
        read.Transaction = tx;
        read.CommandText = "SELECT json FROM characters WHERE username=$u LIMIT 1;";
        read.Parameters.AddWithValue("$u", user);
        if (read.ExecuteScalar() is not string existing)
        {
            reason = null;
            return true;
        }

        try
        {
            Deserialize(existing);
            reason = null;
            return true;
        }
        catch (Exception e)
        {
            reason = e.Message;
            return false;
        }
    }

    private static void LogRefusedOverwrite(string user, string reason) =>
        Console.WriteLine(
            $"[{DateTime.Now:HH:mm:ss}] [db] !! Refused to overwrite unreadable character row '{user}': {reason}");
}
