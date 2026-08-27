// Read-only world markers for the editor overlay: cells that server content points at
// (warp sources/arrivals, world-map trigger zones, spawn points, NPCs), so a mapper sees
// what they'd break before painting over it. Everything is re-read from game-data on each
// request — the files are a few thousand rows at most, and this way a CSV edit shows up
// on the next map switch with no restart.
//
// Column semantics mirror Server/Content.cs's loaders (LoadWarps, LoadSpawns,
// LoadAreaSpawns, LoadWorldTriggers, LoadWorldDests) — that file is the authority.
using System.Text;

namespace MapEditor;

public static class Markers
{
    public static object For(int id, string gameData, Func<int, string> mapName)
    {
        string P(string f) => Path.Combine(gameData, f);

        var mobNames = new Dictionary<int, string>();
        foreach (var r in ReadCsv(P("mobs.csv")))
            if (Int(r, "MobId", out var mid))
                mobNames[mid] = Str(r, "Description") is { Length: > 0 } d ? d : Str(r, "Identifier");
        string MobName(int mob) => mobNames.GetValueOrDefault(mob, "");

        var warpsOut = new List<object>();
        var warpsIn = new List<object>();
        foreach (var r in ReadCsv(P("Warps.csv")))
        {
            if (!Int(r, "SourceMapId", out var sm) || !Int(r, "SourceX", out var sx) || !Int(r, "SourceY", out var sy)
                || !Int(r, "DestinationMapId", out var dm) || !Int(r, "DestinationX", out var dx) || !Int(r, "DestinationY", out var dy))
                continue;
            if (sm == id) warpsOut.Add(new { x = sx, y = sy, m = dm, dx, dy, name = mapName(dm) });
            if (dm == id) warpsIn.Add(new { x = dx, y = dy, m = sm, sx, sy, name = mapName(sm) });
        }

        // A trigger row is a thin band: the fixed axis in [FixedLo,FixedHi], the other axis
        // in [RangeLo,RangeHi] (stepping onto any of these cells opens the world-map screen).
        var world = new List<object>();
        foreach (var r in ReadCsv(P("WorldMapTriggers.csv")))
        {
            if (!Int(r, "Map", out var m) || m != id) continue;
            bool fixedIsX = !Str(r, "FixedAxis").Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
            if (!Int(r, "FixedLo", out var flo) || !Int(r, "FixedHi", out var fhi)
                || !Int(r, "RangeLo", out var rlo) || !Int(r, "RangeHi", out var rhi)) continue;
            for (int f = flo; f <= fhi; f++)
                for (int v = rlo; v <= rhi; v++)
                    world.Add(fixedIsX ? new { x = f, y = v } : new { x = v, y = f });
        }

        var worldArrivals = new List<object>();
        foreach (var r in ReadCsv(P("WorldMapDests.csv")))
            if (Int(r, "Map", out var m) && m == id && Int(r, "X", out var x) && Int(r, "Y", out var y))
                worldArrivals.Add(new { x, y, name = Str(r, "Name") });

        var spawns = new List<object>();
        foreach (var r in ReadCsv(P("Spawns.csv")))
            if (Int(r, "SpnMapId", out var m) && m == id
                && Int(r, "SpnMobId", out var mob) && Int(r, "SpnX", out var x) && Int(r, "SpnY", out var y))
                spawns.Add(new { x, y, mob, name = MobName(mob) });

        // All-zero box = "anywhere walkable on the map" (Server/World.cs Spawn.MinX comment).
        var areas = new List<object>();
        foreach (var file in new[] { "AreaSpawns.csv", "AreaSpawnsTrap.csv" })
            foreach (var r in ReadCsv(P(file)))
                if (Int(r, "Map", out var m) && m == id && Int(r, "MobId", out var mob) && Int(r, "Count", out var count)
                    && Int(r, "MinX", out var x0) && Int(r, "MinY", out var y0)
                    && Int(r, "MaxX", out var x1) && Int(r, "MaxY", out var y1))
                    areas.Add(new { x0, y0, x1, y1, count, mob, name = MobName(mob) });

        var npcs = new List<object>();
        foreach (var r in ReadCsv(P("NPCs.csv")))
        {
            if (!Int(r, "NpcMapId", out var m) || m != id) continue;
            if (Str(r, "NpcIsF1Npc") == "1") continue;      // virtual, has no world tile
            if (Str(r, "Enabled") == "0") continue;
            if (Int(r, "NpcX", out var x) && Int(r, "NpcY", out var y))
                npcs.Add(new { x, y, name = Str(r, "NpcDescription") });
        }

        // MapCells.csv authored overrides — the server rewrites these cells on load, so what
        // players see there differs from the shipped file the editor renders. Null = the
        // column was blank (inherits from the .map).
        var overrides = new List<object>();
        foreach (var r in ReadCsv(P("MapCells.csv")))
        {
            if (!Int(r, "Map", out var m) || m != id) continue;
            if (!Int(r, "X", out var x) || !Int(r, "Y", out var y)) continue;
            overrides.Add(new
            {
                x, y,
                tile = Int(r, "Tile", out var t) ? (int?)t : null,
                pass = Int(r, "Pass", out var pa) ? (int?)pa : null,
                obj = Int(r, "Obj", out var o) ? (int?)o : null,
                src = Str(r, "Sources"),
            });
        }

        // Doors.csv rows for this map, for the player-view render (mirrors MapData.Load's
        // authored layers): DefaultClosed runs stamp their ClosedObj ids from X+StartDx
        // rightward; ForceOpen tiles are authored walkable with no object.
        var defaultClosed = new List<object>();
        var forceOpen = new List<object>();
        foreach (var r in ReadCsv(P("Doors.csv")))
        {
            if (!Int(r, "Map", out var m) || m != id) continue;
            if (!Int(r, "X", out var x) || !Int(r, "Y", out var y)) continue;
            if (Str(r, "ForceOpen").Trim() == "1") forceOpen.Add(new { x, y });
            if (Str(r, "DefaultClosed").Trim() == "1")
            {
                var objs = Str(r, "ClosedObj").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => int.TryParse(s, out var v) ? v : 0).ToArray();
                if (objs.Length == 0) continue;
                Int(r, "StartDx", out var dx);
                defaultClosed.Add(new { x = x + dx, y, objs });
            }
        }

        return new { warpsOut, warpsIn, world, worldArrivals, spawns, areas, npcs, overrides, defaultClosed, forceOpen };
    }

    /// <summary>MobId + display name for the spawn-placement picker.</summary>
    public static List<object> Mobs(string gameData)
    {
        var mobs = new List<object>();
        foreach (var r in ReadCsv(Path.Combine(gameData, "mobs.csv")))
            if (Int(r, "MobId", out var id))
                mobs.Add(new { id, name = Str(r, "Description") is { Length: > 0 } d ? d : Str(r, "Identifier") });
        return mobs;
    }

    /// <summary>Closed→open object-id map for doors that START open (DoorObjects.csv `map`
    /// rows with defaultOpen=1) — mirrors Content.LoadDoorObjects: this piece's own open
    /// counterpart sits at -startDx in the result run, keeping the swap single-cell.</summary>
    public static Dictionary<int, int> DoorDefaultOpen(string gameData)
    {
        var open = new Dictionary<int, int>();
        foreach (var r in ReadCsv(Path.Combine(gameData, "DoorObjects.csv")))
        {
            if (Str(r, "kind").Trim() != "map" || Str(r, "defaultOpen").Trim() != "1") continue;
            if (!Int(r, "lo", out var lo)) continue;
            Int(r, "startDx", out var dx);
            var ids = Str(r, "result").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var v) ? v : 0).ToArray();
            if (-dx >= 0 && -dx < ids.Length) open[lo] = ids[-dx];
        }
        return open;
    }

    /// <summary>Every MapId in the FULL RTK Maps.csv — ids that are taken even when the map
    /// isn't served here (its meta row would silently apply to a custom map on that id).</summary>
    public static HashSet<int> ReservedMapIds(string gameData)
    {
        var ids = new HashSet<int>();
        foreach (var r in ReadCsv(Path.Combine(gameData, "Maps.csv")))
            if (Int(r, "MapId", out var id)) ids.Add(id);
        return ids;
    }

    /// <summary>Highest WarpId currently in Warps.csv, so exported rows number after it.</summary>
    public static int MaxWarpId(string gameData)
    {
        int max = 0;
        foreach (var r in ReadCsv(Path.Combine(gameData, "Warps.csv")))
            if (Int(r, "WarpId", out var wid) && wid > max) max = wid;
        return max;
    }

    /// <summary>Template picker for NPC placement: every NPCs.csv row's identity + look.</summary>
    public static List<object> NpcTemplates(string gameData)
    {
        var npcs = new List<object>();
        foreach (var r in ReadCsv(Path.Combine(gameData, "NPCs.csv")))
            if (Int(r, "NpcId", out var id))
                npcs.Add(new
                {
                    id,
                    ident = Str(r, "NpcIdentifier"),
                    name = Str(r, "NpcDescription"),
                    map = Int(r, "NpcMapId", out var m) ? m : 0,
                    look = Int(r, "NpcLook", out var lk) ? lk : 0,
                });
        return npcs;
    }

    /// <summary>The header row of a CSV (for emitting new rows in the file's own column order).</summary>
    public static string[] CsvHeader(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            var cols = SplitCsv(line);
            if (cols.Length > 0 && !cols[0].StartsWith('#')) return cols;
        }
        return Array.Empty<string>();
    }

    /// <summary>All NPCs.csv rows keyed by NpcId, plus the highest id (for numbering new rows).</summary>
    public static (Dictionary<int, Dictionary<string, string>> ById, int MaxId) NpcRows(string gameData)
    {
        var byId = new Dictionary<int, Dictionary<string, string>>();
        int max = 0;
        foreach (var r in ReadCsv(Path.Combine(gameData, "NPCs.csv")))
            if (Int(r, "NpcId", out var id))
            {
                byId[id] = r;
                if (id > max) max = id;
            }
        return (byId, max);
    }

    /// <summary>Highest SpnId currently in Spawns.csv, so exported rows number after it.</summary>
    public static int MaxSpawnId(string gameData)
    {
        int max = 0;
        foreach (var r in ReadCsv(Path.Combine(gameData, "Spawns.csv")))
            if (Int(r, "SpnId", out var sid) && sid > max) max = sid;
        return max;
    }

    static string Str(Dictionary<string, string> r, string key) => r.GetValueOrDefault(key, "");
    static bool Int(Dictionary<string, string> r, string key, out int v) => int.TryParse(r.GetValueOrDefault(key), out v);

    // Header-keyed rows; quote-aware enough for these files (quoted fields, "" escapes,
    // full-line # comments — quoted or not). No multi-line fields exist in game-data.
    static IEnumerable<Dictionary<string, string>> ReadCsv(string path)
    {
        if (!File.Exists(path)) yield break;
        string[]? hdr = null;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            var cols = SplitCsv(line);
            if (cols.Length == 0 || cols[0].StartsWith('#')) continue;
            if (hdr is null) { hdr = cols; continue; }
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < hdr.Length && i < cols.Length; i++) row[hdr[i]] = cols[i];
            yield return row;
        }
    }

    static string[] SplitCsv(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        cells.Add(sb.ToString());
        return cells.ToArray();
    }
}
