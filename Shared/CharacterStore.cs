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
/// (and left in place afterwards as a backup). Existing data/chars/*.json are imported on first run.
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

    public bool Exists(string name)
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

    // One-time import of the legacy data/chars/*.json into the DB. Idempotent: each record is inserted
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
