using Shared;

namespace Server;

/// <summary>
/// Server-side terrain for a single map, loaded from a 4.x headerless <c>.map</c> file
/// (4 bytes/cell: <c>[ground u16 LE][object u16 LE]</c>, ground's top 2 bits = passability flag).
///
/// Needed for the 5.33 client, whose terrain is NOT drawn from a local file but STREAMED from the
/// server: after 0x15 map-info the client sends opcode 0x05 (initial) / 0x06 (refresh) requesting a
/// view rectangle, and the server replies with an opcode-0x06 cell block. (Confirmed by reversing
/// NexusTK.exe handler 0x469060 AND by the Mithia 7.x reference `clif_sendmapdata`.) The 4.95 client
/// loads its own local maps and never uses this.
/// </summary>
public sealed class MapData
{
    public ushort Xs { get; }
    public ushort Ys { get; }
    private readonly ushort[] _tile;
    private readonly ushort[] _pass;
    private readonly ushort[] _obj;

    private MapData(ushort xs, ushort ys, ushort[] tile, ushort[] pass, ushort[] obj)
    {
        Xs = xs; Ys = ys; _tile = tile; _pass = pass; _obj = obj;
    }

    public ushort Tile(int x, int y) => _tile[y * Xs + x];
    public ushort Pass(int x, int y) => _pass[y * Xs + x];
    public ushort Obj(int x, int y)  => _obj[y * Xs + x];

    /// <summary>The raw ground <c>u16</c> for a cell — tile in the low 14 bits, passability flag in the top 2.
    /// This is the wire form the client's 0x06 cell-patch packet carries as the first word of each cell.</summary>
    public ushort GroundWord(int x, int y) => (ushort)((_tile[y * Xs + x] & 0x3FFF) | (_pass[y * Xs + x] << 14));

    /// <summary>Change the object tile at (x,y) — the door toggle ('o'/0x20) uses this. The cache is
    /// process-wide, so a toggled door is shared world state: everyone already on the map is told to redraw via
    /// the 0x06 patch, and reading it back lets the next 'o' toggle the door closed again. No-op if out of range.
    /// Purely cosmetic — collision is the ground pass flag only (see <see cref="Solid"/>), which this leaves alone.</summary>
    public void SetObj(int x, int y, ushort obj)
    {
        if (x < 0 || y < 0 || x >= Xs || y >= Ys) return;
        _obj[y * Xs + x] = (ushort)(obj & 0x3FFF);
    }

    /// <summary>Is (x,y) impassable? Only the ground-passability flag blocks (water/cliff/out-of-bounds and
    /// the ground baked under walls) — the object layer is VISUAL, not collision (matches the RTK reference's
    /// map_canmove, which collides on pass only and leaves its object check commented out). This is the same
    /// test the player's walk uses (see Session.Blocked), so mob AI and player collision agree. Out-of-range
    /// coords count as solid.</summary>
    public bool Solid(int x, int y) =>
        x < 0 || y < 0 || x >= Xs || y >= Ys || _pass[y * Xs + x] != 0;

    private static readonly Dictionary<ushort, MapData?> Cache = new();

    /// <summary>Load (and cache) map <paramref name="id"/> at the given dims, or null if the file is missing/short.</summary>
    public static MapData? For(ushort id, ushort xs, ushort ys)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(id, out var cached)) return cached;
            var md = Load(id, xs, ys);
            Cache[id] = md;
            return md;
        }
    }

    private static MapData? Load(ushort id, ushort xs, ushort ys)
    {
        var path = Locate(id);
        if (path is null) { Log.Info($"   !! map TK{id}.map not found (searched NEXUS_MAPS, repo data/maps, client installs)"); return null; }

        var d = File.ReadAllBytes(path);
        int cells = xs * ys;
        if (d.Length < cells * 4)
        {
            Log.Info($"   !! map {path} is {d.Length}B, expected >= {cells * 4} for {xs}x{ys}");
            return null;
        }

        var tile = new ushort[cells];
        var pass = new ushort[cells];
        var obj  = new ushort[cells];
        for (int i = 0; i < cells; i++)
        {
            ushort g = (ushort)(d[i * 4] | (d[i * 4 + 1] << 8));
            ushort o = (ushort)(d[i * 4 + 2] | (d[i * 4 + 3] << 8));
            tile[i] = (ushort)(g & 0x3FFF);           // ground frame index (low 14 bits)
            pass[i] = (ushort)((g >> 14) & 0x3);      // passability flag (top 2 bits)
            obj[i]  = (ushort)(o & 0x3FFF);           // object frame index
        }
        Log.Info($"   -> loaded map TK{id}.map ({xs}x{ys}, {cells} cells) from {path}");
        return new MapData(xs, ys, tile, pass, obj);
    }

    private static string? Locate(ushort id)
    {
        string file = $"TK{id}.map";
        foreach (var dir in SearchDirs())
        {
            var p = Path.Combine(dir, file);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static IEnumerable<string> SearchDirs()
    {
        var env = Environment.GetEnvironmentVariable("NEXUS_MAPS");
        if (!string.IsNullOrWhiteSpace(env)) yield return env;

        // repo data/maps (self-contained, committed)
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            bool isRoot = dir.GetFiles("*.sln").Length > 0
                       || (Directory.Exists(Path.Combine(dir.FullName, "Server"))
                           && Directory.Exists(Path.Combine(dir.FullName, "Shared")));
            if (isRoot) { yield return Path.Combine(dir.FullName, "data", "maps"); break; }
            dir = dir.Parent;
        }

        // fall back to the client installs, which ship the full 4.x map set
        yield return @"C:\Program Files (x86)\Nexon\NextAeon\Maps";
        yield return @"C:\Program Files (x86)\Nexon\NextAeon5\Maps";
    }
}
