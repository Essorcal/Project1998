using Microsoft.Data.Sqlite;
using Shared;

namespace Server;

/// <summary>
/// Persistence for RUNTIME map state — cells a player changed (doors opened, GM edits, event scripts) and
/// locked doors somebody has opened. Backed by <c>map_cells</c> / <c>map_unlocks</c> in the shared SQLite DB.
///
/// <para>What this deliberately does NOT store: the <c>.map</c> files (never written by anything in the
/// server) and authored corrections (<c>MapCells.csv</c>). Rows here are the diff against the AUTHORED
/// baseline — see <see cref="MapData"/>. Persisting against the file instead would bake corrections into the
/// database, and from then on editing the CSV would silently do nothing.</para>
///
/// <para>Writes are write-through rather than batched: doors change rarely (a handful per player per
/// session), so there is nothing to amortise, and a crash between a door opening and a periodic flush would
/// be exactly the inconsistency this exists to prevent.</para>
/// </summary>
public static class MapStore
{
    /// <summary>Every persisted runtime cell for a map, applied by <see cref="MapData"/> after the authored
    /// baseline. Empty (never null) if the map has no saved state or the DB is unreachable.</summary>
    public static IReadOnlyList<(ushort X, ushort Y, ushort Tile, ushort Pass, ushort Obj)> Cells(ushort map)
    {
        var list = new List<(ushort, ushort, ushort, ushort, ushort)>();
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT x, y, tile, pass, obj FROM map_cells WHERE map=$m";
            cmd.Parameters.AddWithValue("$m", (int)map);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(((ushort)r.GetInt32(0), (ushort)r.GetInt32(1),
                          (ushort)r.GetInt32(2), (ushort)r.GetInt32(3), (ushort)r.GetInt32(4)));
        }
        catch (Exception ex) { Log.Info($"   !! map_cells read failed for map {map}: {ex.Message}"); }
        return list;
    }

    /// <summary>Save or clear one cell to match its CURRENT state in <paramref name="md"/>. A cell that is
    /// back on its authored baseline has its row DELETED rather than stored, so the table only ever holds
    /// what is genuinely changed right now — a door toggled shut again leaves nothing behind.</summary>
    public static void Persist(MapData md, int x, int y)
    {
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            if (md.IsRuntimeChanged(x, y))
            {
                var c = md.At(x, y);
                cmd.CommandText = @"INSERT INTO map_cells (map,x,y,tile,pass,obj) VALUES ($m,$x,$y,$t,$p,$o)
                                    ON CONFLICT(map,x,y) DO UPDATE SET tile=$t, pass=$p, obj=$o";
                cmd.Parameters.AddWithValue("$t", (int)c.Tile);
                cmd.Parameters.AddWithValue("$p", (int)c.Pass);
                cmd.Parameters.AddWithValue("$o", (int)c.Obj);
            }
            else
            {
                cmd.CommandText = "DELETE FROM map_cells WHERE map=$m AND x=$x AND y=$y";
            }
            cmd.Parameters.AddWithValue("$m", (int)md.Id);
            cmd.Parameters.AddWithValue("$x", x);
            cmd.Parameters.AddWithValue("$y", y);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Log.Info($"   !! map_cells write failed for ({x},{y}) on map {md.Id}: {ex.Message}"); }
    }

    /// <summary>Persist a whole run of cells at once (a door swing is 1-4 tiles wide).</summary>
    public static void PersistRun(MapData md, int startX, int y, int width)
    {
        for (int i = 0; i < width; i++) Persist(md, startX + i, y);
    }

    /// <summary>Every locked tile that has been opened, across all maps — loaded once into
    /// <see cref="Doors"/> at startup and on <c>!reload</c>.</summary>
    public static IReadOnlyList<(ushort Map, ushort X, ushort Y)> Unlocks()
    {
        var list = new List<(ushort, ushort, ushort)>();
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT map, x, y FROM map_unlocks";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(((ushort)r.GetInt32(0), (ushort)r.GetInt32(1), (ushort)r.GetInt32(2)));
        }
        catch (Exception ex) { Log.Info($"   !! map_unlocks read failed: {ex.Message}"); }
        return list;
    }

    /// <summary>Record (or clear) a tile's unlocked state. Called the moment a key is spent, so the key and
    /// the unlock commit together — before this existed, a ConsumeKey door ate the key and relocked on
    /// restart.</summary>
    public static void SetUnlocked(ushort map, ushort x, ushort y, bool unlocked)
    {
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = unlocked
                ? @"INSERT INTO map_unlocks (map,x,y,unlocked_utc) VALUES ($m,$x,$y,$t)
                    ON CONFLICT(map,x,y) DO UPDATE SET unlocked_utc=$t"
                : "DELETE FROM map_unlocks WHERE map=$m AND x=$x AND y=$y";
            cmd.Parameters.AddWithValue("$m", (int)map);
            cmd.Parameters.AddWithValue("$x", (int)x);
            cmd.Parameters.AddWithValue("$y", (int)y);
            if (unlocked) cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Log.Info($"   !! map_unlocks write failed for ({x},{y}) on map {map}: {ex.Message}"); }
    }

    /// <summary>Wipe all persisted runtime state for a map — the reset an event needs when it ends (every
    /// door it opened swings shut and relocks). Returns how many rows went.</summary>
    public static int ClearMap(ushort map)
    {
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "DELETE FROM map_cells WHERE map=$m; DELETE FROM map_unlocks WHERE map=$m;";
            cmd.Parameters.AddWithValue("$m", (int)map);
            return cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Log.Info($"   !! map state clear failed for map {map}: {ex.Message}"); return 0; }
    }
}
