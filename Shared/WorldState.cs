namespace Shared;

/// <summary>
/// The <c>world_state</c> key/value table: state that belongs to the WORLD rather than to any character.
///
/// <para>Currently just the in-game clock. RTK keeps the same thing in its <c>Time</c> table (cur_time /
/// cur_day / cur_season / cur_year, loaded at boot); we had the compiled-in starting values instead, so
/// every restart rewound the calendar. That was a harmless quirk while restarts were rare and stopped being
/// one the moment deploys began scheduling them.</para>
///
/// <para>Deliberately a key/value table rather than typed columns: what belongs here is a handful of small,
/// unrelated scalars, and the alternative is a schema migration every time one is added.</para>
/// </summary>
public static class WorldState
{
    public static string? Get(string key)
    {
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT value FROM world_state WHERE key=$k LIMIT 1;";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
        catch { return null; }
    }

    public static int GetInt(string key, int fallback)
        => int.TryParse(Get(key), out var v) ? v : fallback;

    public static void Set(string key, string value)
    {
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = @"INSERT INTO world_state(key, value) VALUES($k, $v)
                                ON CONFLICT(key) DO UPDATE SET value=$v;";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
        catch { /* best effort — a lost clock write costs at most one in-game hour */ }
    }

    /// <summary>Write several keys in one transaction. The clock's four fields are one logical value and
    /// must not be able to half-persist: committing hour without its day rollover would slide the calendar
    /// by a day every time it happened to be interrupted at midnight.</summary>
    public static void SetMany(params (string Key, string Value)[] pairs)
    {
        try
        {
            using var cn = Db.Open();
            using var tx = cn.BeginTransaction();
            foreach (var (k, v) in pairs)
            {
                using var cmd = cn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO world_state(key, value) VALUES($k, $v)
                                    ON CONFLICT(key) DO UPDATE SET value=$v;";
                cmd.Parameters.AddWithValue("$k", k);
                cmd.Parameters.AddWithValue("$v", v);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch { /* best effort, as above */ }
    }
}
