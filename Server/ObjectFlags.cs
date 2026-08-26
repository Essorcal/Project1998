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
/// <para><b>Format.</b> Each object's flag PRECEDES its frame list, so the file is:</para>
/// <code>
///   u32 count
///   u8  flag[0]                                     // object 0
///   per object z = 0 .. count-1:
///       u8  tileCount
///       tileCount * u16                             // frame ids, object z's vertical sprite stack
///       5-byte separator FF FF FF FF 00
///       u8  flag[z+1]                               // the NEXT object's flag (absent after the last)
/// </code>
/// <para>The loop below walks it one object out of phase with that — iteration <i>z</i> parses object
/// <i>z</i>-1's frames (which it skips) and then reads object <i>z</i>'s flag — which lands the right byte
/// in <c>flags[z]</c>. Object id 0 = "no object" and never blocks; its flag byte is 0x01 in the shipped
/// table, plainly unused, so the array's default 0 is left in place rather than read.</para>
///
/// <para><b>The trap</b> (walked into 2026-08-12): read a record's frames and its trailing flag as belonging
/// to the same object and the FRAMES come out one object ahead, which makes it look as though collision is
/// shifted. It isn't. The tell is doors, since RTK's <c>open.lua</c> gives independent shut/open pairings to
/// check against: pair 346/347 with 366/367 and render them, and the frames only draw as a coherent door —
/// two matched leaves, then that same doorway with the leaves swung aside — when attributed one record
/// later, while the flags at 346/347 (0x0F solid) and 366/367 (0x00 walkable) are already correct as read
/// here. Both facts are the layout above.</para>
/// <para>Flag bits (RTK <c>map.h</c>): UP=1 DOWN=2 RIGHT=4 LEFT=8; a solid wall piece = 0x0F (all four sides).
/// The walk consumes the file to the exact byte and yields exactly the header's object count.</para>
/// </summary>
public static class ObjectFlags
{
    public const byte Up = 1, Down = 2, Right = 4, Left = 8;

    private static byte[]? _flags;
    private static readonly object _lock = new();

    /// <summary>Drop the cached table so the next <see cref="Flag"/> re-reads SObj.tbl AND
    /// <c>game-data/ObjectFlagOverrides.csv</c>. Called from the hot-reload path (<c>@reload</c>) alongside
    /// <see cref="MapData.Invalidate"/>, so an added walkable-override row takes effect without a restart.</summary>
    public static void Invalidate() { lock (_lock) _flags = null; }

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
                // AUTHORED layer: per-sprite flag overrides (game-data/ObjectFlagOverrides.csv). These are
                // doorway sprites the 4.95 table flags solid even though they sit on a warp tile, which makes
                // the warp unreachable. Applied over the extract rather than edited into it so that re-running
                // re/pak_extract.py against a stock client can't silently revert the fix. The CLIENT enforces
                // its own copy of this table, so each id here must also be patched into the client's .dat
                // (re/patch_sobj_flags.py) or the client still refuses the step.
                int applied = 0;
                foreach (var (id, flag) in FlagOverrides())
                    if (id >= 0 && id < flags.Length && flags[id] != flag) { flags[id] = flag; applied++; }
                Log.Info($"   -> loaded SObj.tbl ({count} objects) from {path}" +
                         (applied > 0 ? $" — {applied} sprite flag(s) overridden by ObjectFlagOverrides.csv" : ""));
                return _flags = flags;
            }
            catch (Exception e)
            {
                Log.Info($"   !! SObj.tbl parse failed ({e.Message}) — object-wall collision disabled");
                return _flags = Array.Empty<byte>();
            }
        }
    }

    /// <summary>The (objectId, flag) pairs in <c>game-data/ObjectFlagOverrides.csv</c> — "Obj,Flag,Note",
    /// blank/comment/header rows skipped, flag written either decimal or <c>0x</c>-prefixed. Shared with
    /// <c>re/patch_sobj_flags.py</c>, which reads the same file so the client patch and the server override
    /// can never disagree. A missing or unreadable file just means no overrides — never fatal, same as every
    /// other optional content file.</summary>
    private static List<(int Id, byte Flag)> FlagOverrides()
    {
        var rows = new List<(int, byte)>();
        var path = Path.Combine(Shared.RepoPaths.GameDataDir(), "ObjectFlagOverrides.csv");
        try
        {
            if (!File.Exists(path)) return rows;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var col = line.Split(',');
                if (col.Length < 2 || !int.TryParse(col[0].Trim(), out var id)) continue;   // header skips itself
                var f = col[1].Trim();
                bool hex = f.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                if (!byte.TryParse(hex ? f.AsSpan(2) : f,
                                   hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer,
                                   null, out var flag))
                {
                    Log.Info($"   !! ObjectFlagOverrides.csv: object {id} has unparseable flag '{f}' — row skipped");
                    continue;
                }
                rows.Add((id, flag));
            }
        }
        catch (Exception e) { Log.Info($"   !! ObjectFlagOverrides.csv read failed ({e.Message}) — no flag overrides"); }
        return rows;
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
