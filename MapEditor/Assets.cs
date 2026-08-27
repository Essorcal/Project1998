using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MapEditor;

/// <summary>
/// The 5.33 tile assets, decoded from the client's <c>Tile.dat</c> exactly as
/// <c>re/render_maps.py</c> does (that file is the format authority; every structure note
/// below is condensed from it). Both clients' maps render with THIS tileset: the server
/// emulates the 4.x world, its players run the 5.33-based client, and the KRU retail
/// tiles are a later re-tiled set whose indices no longer line up.
///
/// Formats, in brief:
///   Tile.dat    Nexon DAT archive: u32 count, then count records of {u32 offset, char[13] name},
///               last record = EOF terminator.
///   *.PAL       "DLPalette" blocks; header length varies, so each block's 256 RGBA color
///               entries are its LAST 1024 bytes.
///   *.TBL       u32 count, then count * 2 bytes = [paletteIndex, flag] per frame.
///   *.EPF       u16 frameCount @0, u32 tocOffset @8 (add 12); TOC entries are 16 bytes:
///               i16 top,left,bot,right + u32 pixOff,stencilOff. The pixel region is a FULL
///               UNCOMPRESSED (w*h) raster of palette indices (sten - pix == w*h for every
///               frame); the stencil is a separate per-ROW RLE visibility mask: per row,
///               bytes until a 0x00 terminator, byte &gt; 0x80 = draw (byte - 0x80) pixels,
///               byte &lt;= 0x80 = skip that many. The mask gates visibility only — it never
///               repositions raster pixels. (Confirmed against a native 5.33 client
///               screenshot 2026-08-26; the previous packed-pixel model sheared any frame
///               with interior transparency.)
///   SOBJ.TBL    u32 count, then per object: u8 tileCount, tileCount*u16 TILEC frame ids,
///               FF FF FF FF 00, u8 flag. Frame [0] sits on the ANCHOR cell and the column
///               grows NORTH one 24px cell per frame.
///
/// A .map ground word is TAGGED: 0 = void (draw nothing), &lt; 0xC000 = TILE.EPF frame v
/// directly, &gt;= 0xC000 = legacy sheet-2 frame (v - 0xC000) remapped through
/// game-data/Tile533Map.csv. The same two top bits are the 4.x passability flag, so
/// "sheet 2" and "blocked" coincide by construction (Server/MapData.cs, TileTranslation.cs).
/// </summary>
public sealed class TileAssets
{
    public const int Cell = 24;
    public const ushort Sheet2Base = 0xC000;
    public const int AtlasCols = 64;

    public int GroundCount { get; private set; }
    public int TilecCount { get; private set; }
    public byte[] GroundPng { get; private set; } = [];
    public byte[] TilecPng { get; private set; } = [];
    /// <summary>SObj id -> TILEC frame column (index 0 = anchor cell, growing north).</summary>
    public List<ushort[]> Objs { get; } = [];
    /// <summary>SObj id -> the trailing flag byte (collision-ish; surfaced raw, semantics per client).</summary>
    public List<byte> ObjFlags { get; } = [];
    /// <summary>Legacy sheet-2 runs, verbatim rows of Tile533Map.csv: (startLegacy, count, start533).</summary>
    public List<(int Legacy, int Count, int S533)> Sheet2Runs { get; } = [];

    public static TileAssets Load(string tileDat, string sheet2Csv)
    {
        var t = new TileAssets();
        var files = ReadDat(tileDat);

        foreach (var line in File.ReadLines(sheet2Csv))
        {
            var s = line.Trim();
            if (s.Length == 0 || s.StartsWith('#')) continue;
            var p = s.Split(',');
            t.Sheet2Runs.Add((int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2])));
        }

        // Ground: bake every frame into an opaque 24x24 block (transparent = black, the client's
        // void), laid out AtlasCols per row so frame i sits at (i % 64 * 24, i / 64 * 24).
        {
            var pals = LoadPalettes(files["TILE.PAL"]);
            var palIdx = TblPalettes(files["TILE.TBL"]);
            var epf = files["TILE.EPF"];
            var ents = EpfEntries(epf, out int cnt);
            t.GroundCount = cnt;
            int rows = (cnt + AtlasCols - 1) / AtlasCols;
            int aw = AtlasCols * Cell, ah = rows * Cell;
            var rgba = new byte[aw * ah * 4];
            for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
            for (int fi = 0; fi < cnt; fi++)
            {
                var pal = PalFor(pals, palIdx, fi);
                if (pal is null) continue;
                DecodeFrameInto(epf, ents, fi, pal, rgba, aw,
                    fi % AtlasCols * Cell, fi / AtlasCols * Cell);
            }
            t.GroundPng = EncodePng(aw, ah, rgba);
        }

        // Objects: TILEC frames keep their alpha; each frame is baked at its (left, top) offset
        // inside its own 24x24 slot, so the frontend blits whole slots at cell positions.
        {
            var pals = LoadPalettes(files["TILEC.PAL"]);
            var palIdx = TblPalettes(files["TILEC.TBL"]);
            var epf = files["TILEC.EPF"];
            var ents = EpfEntries(epf, out int cnt);
            t.TilecCount = cnt;
            int rows = (cnt + AtlasCols - 1) / AtlasCols;
            int aw = AtlasCols * Cell, ah = rows * Cell;
            var rgba = new byte[aw * ah * 4];
            for (int fi = 0; fi < cnt; fi++)
            {
                var pal = PalFor(pals, palIdx, fi);
                if (pal is null) continue;
                DecodeFrameInto(epf, ents, fi, pal, rgba, aw,
                    fi % AtlasCols * Cell, fi / AtlasCols * Cell);
            }
            t.TilecPng = EncodePng(aw, ah, rgba);
        }

        // SObj table. Layout per Server/ObjectFlags.cs (the trap is documented there and in
        // docs/5.x/Reverse-Engineering.md): each object's FLAG byte precedes its frame list —
        //   u32 count, u8 flag[0], then per object z = 0..count-1:
        //   u8 tileCount, tileCount*u16 frame ids (OBJECT z's column), FF FF FF FF 00, u8 flag[z+1].
        // A walk that pairs a record's frames with its trailing flag hands every object the NEXT
        // object's frames — the misassembled-gates/fences bug. Verified by the doc's door test:
        // pairs 346/347 + 366/367 only compose a coherent shut/open doorway with this attribution.
        {
            var sd = files["SOBJ.TBL"];
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(sd);
            int off = 5;                              // u32 count + object 0's lead flag byte
            var flags = new List<byte> { sd[4] };
            for (uint z = 0; z < count && off < sd.Length; z++)
            {
                int tc = sd[off++];
                var fids = new ushort[tc];
                for (int k = 0; k < tc; k++) fids[k] = BinaryPrimitives.ReadUInt16LittleEndian(sd.AsSpan(off + k * 2));
                off += tc * 2 + 5;                    // frames + FF FF FF FF 00 separator
                t.Objs.Add(fids);                     // record z = object z's column
                if (off < sd.Length) flags.Add(sd[off]);
                off += 1;                             // flag[z+1]
            }
            while (flags.Count < t.Objs.Count) flags.Add(0);
            t.ObjFlags.AddRange(flags.Take(t.Objs.Count));
        }

        return t;
    }

    static byte[]? PalFor(List<byte[]> pals, byte[] palIdx, int fi)
    {
        if (fi < palIdx.Length && palIdx[fi] < pals.Count) return pals[palIdx[fi]];
        return pals.Count > 0 ? pals[0] : null;
    }

    static Dictionary<string, byte[]> ReadDat(string path)
    {
        var d = File.ReadAllBytes(path);
        uint n = BinaryPrimitives.ReadUInt32LittleEndian(d);
        var recs = new (string Name, int Off)[n];
        int off = 4;
        for (uint i = 0; i < n; i++)
        {
            int o = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off));
            int end = Array.IndexOf(d, (byte)0, off + 4, 13);
            int len = end < 0 ? 13 : end - (off + 4);
            recs[i] = (Encoding.Latin1.GetString(d, off + 4, len).ToUpperInvariant(), o);
            off += 17;
        }
        var map = new Dictionary<string, byte[]>();
        for (int i = 0; i < n - 1; i++)
            if (recs[i].Name.Length > 0)
                map[recs[i].Name] = d[recs[i].Off..recs[i + 1].Off];
        return map;
    }

    static List<byte[]> LoadPalettes(byte[] raw)
    {
        var offs = new List<int>();
        var needle = "DLPalette"u8;
        for (int i = 0; i <= raw.Length - needle.Length; i++)
            if (raw.AsSpan(i, needle.Length).SequenceEqual(needle)) { offs.Add(i); i += needle.Length - 1; }
        var pals = new List<byte[]>();
        for (int b = 0; b < offs.Count; b++)
        {
            int end = b + 1 < offs.Count ? offs[b + 1] : raw.Length;
            var pal = new byte[256 * 3];
            if (end - 1024 >= 0)
                for (int c = 0; c < 256; c++)
                {
                    int src = end - 1024 + c * 4;
                    pal[c * 3] = raw[src]; pal[c * 3 + 1] = raw[src + 1]; pal[c * 3 + 2] = raw[src + 2];
                }
            pals.Add(pal);
        }
        return pals;
    }

    static byte[] TblPalettes(byte[] tbl)
    {
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(tbl);
        var outp = new byte[count];
        for (uint i = 0; i < count && 4 + i * 2 < tbl.Length; i++) outp[i] = tbl[4 + i * 2];
        return outp;
    }

    static (short Top, short Left, short Bot, short Right, uint Pix, uint Sten)[] EpfEntries(byte[] epf, out int count)
    {
        count = BinaryPrimitives.ReadUInt16LittleEndian(epf);
        int toc = 12 + (int)BinaryPrimitives.ReadUInt32LittleEndian(epf.AsSpan(8));
        var ents = new (short, short, short, short, uint, uint)[count];
        for (int i = 0; i < count; i++)
        {
            var s = epf.AsSpan(toc + i * 16);
            ents[i] = (BinaryPrimitives.ReadInt16LittleEndian(s), BinaryPrimitives.ReadInt16LittleEndian(s[2..]),
                       BinaryPrimitives.ReadInt16LittleEndian(s[4..]), BinaryPrimitives.ReadInt16LittleEndian(s[6..]),
                       BinaryPrimitives.ReadUInt32LittleEndian(s[8..]), BinaryPrimitives.ReadUInt32LittleEndian(s[12..]));
        }
        return ents;
    }

    /// <summary>Decode EPF frame <paramref name="fi"/> into an RGBA atlas at slot (dx, dy).
    /// Pixels are a full w*h raster; the stencil is a per-row RLE visibility mask (see the
    /// class header). Pixels outside the mask stay whatever the atlas already holds.</summary>
    static void DecodeFrameInto(byte[] epf, (short Top, short Left, short Bot, short Right, uint Pix, uint Sten)[] ents,
        int fi, byte[] pal, byte[] atlas, int atlasW, int dx, int dy)
    {
        var (top, left, bot, right, pix, sten) = ents[fi];
        int w = right - left, h = bot - top;
        if (w <= 0 || h <= 0) return;
        int rasterOff = 12 + (int)pix;
        int off = 12 + (int)sten;

        for (int y = 0; y < h; y++)
        {
            int x = 0;
            while (off < epf.Length)
            {
                byte b = epf[off++];
                if (b == 0) break;
                if (b > 0x80)
                {
                    int run = b - 0x80;
                    for (int k = 0; k < run && x + k < w; k++)
                    {
                        int fy = y + top, fx = x + k + left;
                        if (fy < 0 || fx < 0 || fy >= Cell || fx >= Cell) continue;
                        int ri = rasterOff + y * w + x + k;
                        if (ri >= epf.Length) break;
                        byte ci = epf[ri];
                        int di = ((dy + fy) * atlasW + dx + fx) * 4;
                        if (di < 0 || di + 3 >= atlas.Length) continue;
                        atlas[di] = pal[ci * 3]; atlas[di + 1] = pal[ci * 3 + 1]; atlas[di + 2] = pal[ci * 3 + 2];
                        atlas[di + 3] = 255;
                    }
                    x += run;
                }
                else x += b;
            }
        }
    }

    // ------------------------------------------------------------------ PNG (no deps)
    static readonly uint[] CrcTable = BuildCrcTable();

    static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    static uint Crc(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (byte x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    public static byte[] EncodePng(int w, int h, byte[] rgba)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], h);
        ihdr[8] = 8; ihdr[9] = 6;                     // 8-bit RGBA
        WriteChunk(ms, "IHDR", ihdr);

        var scan = new byte[(w * 4 + 1) * h];         // filter byte 0 + row
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(rgba, y * w * 4, scan, y * (w * 4 + 1) + 1, w * 4);
        using var zms = new MemoryStream();
        using (var z = new ZLibStream(zms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(scan);
        WriteChunk(ms, "IDAT", zms.ToArray());
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var tb = Encoding.ASCII.GetBytes(type);
        s.Write(tb);
        s.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(tb, data));
        s.Write(crc);
    }
}
