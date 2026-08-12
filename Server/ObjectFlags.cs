namespace Server;

/// <summary>
/// Directional object-passability flags from the client's <c>SObj.tbl</c> — the collision the 4.x CLIENT
/// applies locally (why a player can't walk through a hut wall) but which the server historically ignored
/// (why mobs could: they path on the ground pass flag only). RTK's <c>map.c object_flag_init()</c> parses
/// this very table but leaves <c>objectFlags[z]=flag;</c> COMMENTED OUT, and its mob AI (<c>map_canmove</c>)
/// collides on the pass flag only — so RTK mobs clip these walls too. We enable it: read SObj.tbl into a
/// per-object flag byte and mirror RTK's <c>clif_object_canmove</c> directional test so mob (and player)
/// movement respects the same walls the client draws over otherwise-walkable ground.
///
/// Format (confirmed: the record walk both consumes the file to the exact byte AND yields exactly the
/// header's object count, then validated tile-by-tile against Buya's Jadespear-hut geometry — walls come
/// out 0x0F solid, the doorway 0x00 walkable):
///   <c>u32 count</c> | 1 lead byte | <c>count</c> records, each:
///     <c>u8 tileCount</c> | <c>tileCount * u16</c> frame ids | 5-byte separator <c>FF FF FF FF 00</c> | <c>u8 flag</c>
///   <c>flag[recordIndex]</c> (1-based) is indexed directly by the map's object-tile id; object id 0 = "no
///   object" = flag 0 = never blocks.
/// Flag bits (RTK <c>map.h</c>): UP=1 DOWN=2 RIGHT=4 LEFT=8; a solid wall piece = 0x0F (blocked on all sides).
/// </summary>
public static class ObjectFlags
{
    public const byte Up = 1, Down = 2, Right = 4, Left = 8;

    private static byte[]? _flags;
    private static readonly object _lock = new();

    /// <summary>The SObj.tbl flag byte for an object-tile id (0 if unknown / no object / table missing).</summary>
    public static byte Flag(int objId)
    {
        var f = _flags ?? Load();
        return (objId >= 0 && objId < f.Length) ? f[objId] : (byte)0;
    }

    /// <summary>Does the object at a DESTINATION cell block a move that ENTERS it while heading
    /// <paramref name="dir"/> (0=N 1=E 2=S 3=W)? Mirrors RTK <c>clif_object_canmove</c>:
    /// N→UP, E→RIGHT, S→DOWN, W→LEFT. (A 0x0F wall blocks every direction ⇒ fully solid.)</summary>
    public static bool Blocks(int objId, int dir)
    {
        byte f = Flag(objId);
        return dir switch
        {
            0 => (f & Up) != 0,
            1 => (f & Right) != 0,
            2 => (f & Down) != 0,
            3 => (f & Left) != 0,
            _ => false,
        };
    }

    private static byte[] Load()
    {
        lock (_lock)
        {
            if (_flags != null) return _flags;
            var path = Locate();
            if (path is null)
            {
                Log.Info("   !! SObj.tbl not found — object-wall collision disabled (mobs/players use pass flag only)");
                return _flags = Array.Empty<byte>();
            }
            try
            {
                var d = File.ReadAllBytes(path);
                int count = d[0] | (d[1] << 8) | (d[2] << 16) | (d[3] << 24);
                var flags = new byte[count + 1];   // [0] unused (obj id 0 = empty); records fill 1..count
                int off = 4 + 1;                    // u32 header + 1 lead byte, then the records
                for (int z = 1; z <= count && off < d.Length; z++)
                {
                    int tc = d[off++];              // tile-frame count
                    off += tc * 2;                  // skip the u16 frame ids
                    off += 5;                        // skip the FF FF FF FF 00 separator
                    if (off >= d.Length) break;
                    flags[z] = d[off++];             // the directional flag for object id z
                }
                Log.Info($"   -> loaded SObj.tbl ({count} objects) from {path}");
                return _flags = flags;
            }
            catch (Exception e)
            {
                Log.Info($"   !! SObj.tbl parse failed ({e.Message}) — object-wall collision disabled");
                return _flags = Array.Empty<byte>();
            }
        }
    }

    // Same search strategy as MapData.Locate: env override, then content. Prefer game-data/SObj.tbl — that is
    // the CLIENT's own table (extract with
    // `python re/pak_extract.py <install>/NexusTK.dat SObj.tbl game-data/SObj.tbl`), whose object-id space the
    // .map files actually index, so server collision matches what the client draws. The RTK-Server copy is a
    // superset that agrees on every in-range id, so it is a safe fallback if the client extract is absent.
    private static string? Locate()
    {
        var candidates = new List<string>();
        var env = Environment.GetEnvironmentVariable("P1998_SOBJ");
        if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env);

        candidates.Add(Path.Combine(Shared.RepoPaths.GameDataDir(), "SObj.tbl"));
        candidates.Add(Path.Combine(Shared.RepoPaths.Root(), "RTK-Server", "rtk", "SObj.tbl"));

        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }
}
