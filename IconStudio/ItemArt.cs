using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace IconStudio;

/// <summary>
/// One client's item-icon set (Item.epf / Item.pal / Item.tbl) plus the archive it came from.
/// Everything here mirrors <c>re/icon_downscale.py</c>, which is the format authority and
/// round-trips every shipped file byte-for-byte; keep the two in step.
///
/// Formats:
///   DAT   u32 count, count * {u32 offset, char[13] name}; an entry's data runs to the next offset.
///         Name fields carry leftover bytes after the NUL, so they are kept RAW for a faithful rewrite.
///   EPF   +0 u16 frameCount, +8 u32 tocRel (TOC at 12 + tocRel), pixels from +12. TOC entry j =
///         {i16 top,left,bottom,right; u32 pixOff,stenOff} (offsets relative to +12). Frame j IS client
///         item id j. Pixels: w*h raw palette indices, then the STENCIL: per row, bytes until 0x00,
///         byte &gt; 0x80 = draw (byte-0x80) px, byte &lt;= 0x80 = skip. The stencil is the alpha.
///   PAL   u32 count, then "DLPalette" blocks (header length varies); colours = the block's last 1024 B.
///   TBL   4.95 = text "NumItems N\nID i, Palette p, Alpha a, Light l\n"; 5.33 and retail = the
///         TKViewer XOR encoding (27-byte key, 8 cipher bytes per u32) over 20-byte records
///         {u32 id, u32 palette, f32 alpha, i32 light, u32 flag}.
/// </summary>
public sealed class ItemArt
{
    public required string Name { get; init; }
    public required string DatPath { get; init; }
    public required List<(byte[] RawName, byte[] Data)> Entries { get; init; }
    public required byte[] Epf { get; init; }
    public required byte[] Pal { get; init; }
    public required byte[] Tbl { get; init; }
    public required List<Frame?> Frames { get; init; }
    public required List<byte[]> Blocks { get; init; }
    public required bool TblText { get; init; }
    public required List<TblRec> Recs { get; init; }

    public int Count => Frames.Count;

    public static ItemArt Load(string name, string datPath)
    {
        var entries = Codec.ReadDat(datPath);
        var epf = Codec.DatGet(entries, "ITEM.EPF");
        var pal = Codec.DatGet(entries, "ITEM.PAL");
        var tbl = Codec.DatGet(entries, "ITEM.TBL");
        var (text, recs) = Codec.TblParse(tbl);
        var frames = Codec.EpfFrames(epf);
        if (recs.Count != frames.Count)
            throw new InvalidDataException($"{name}: Item.tbl has {recs.Count} records but Item.epf {frames.Count} frames");
        return new ItemArt
        {
            Name = name, DatPath = datPath, Entries = entries, Epf = epf, Pal = pal, Tbl = tbl,
            Frames = frames, Blocks = Codec.PalBlocks(pal), TblText = text, Recs = recs,
        };
    }

    public int PaletteIndex(int id) => (int)(Recs[id].Palette % (uint)Blocks.Count);
    public byte[] PaletteRgb(int id) => Codec.PalRgb(Blocks[PaletteIndex(id)]);
}

/// <summary>A decoded EPF frame. <c>Idx</c> is the w*h raster of palette indices, <c>Alpha</c> the
/// stencil mask; <c>StenRaw</c> keeps the shipped stencil bytes so untouched frames re-encode verbatim.</summary>
public sealed record Frame(short Top, short Left, short Bottom, short Right, byte[] Idx, bool[] Alpha, byte[]? StenRaw)
{
    public int W => Right - Left;
    public int H => Bottom - Top;

    public static Frame Centered(int w, int h, byte[] idx, bool[] alpha)
    {
        short left = (short)-(w / 2), top = (short)-(h / 2);
        return new Frame(top, left, (short)(top + h), (short)(left + w), idx, alpha, null);
    }

    public static Frame Blank() => new(0, 0, 1, 1, new byte[1], new bool[1], null);
}

public record struct TblRec(uint Id, uint Palette, float Alpha, int Light, uint Flag);

public static class Codec
{
    // ------------------------------------------------------------------ DAT
    public static List<(byte[] RawName, byte[] Data)> ReadDat(string path)
    {
        var d = File.ReadAllBytes(path);
        int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(d);
        var recs = new (byte[] Name, int Off)[n];
        for (int i = 0; i < n; i++)
        {
            int at = 4 + 17 * i;
            recs[i] = (d[(at + 4)..(at + 17)], (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at)));
        }
        var outp = new List<(byte[], byte[])>(n);
        for (int i = 0; i < n; i++)
        {
            int end = i + 1 < n ? recs[i + 1].Off : d.Length;
            outp.Add((recs[i].Name, d[recs[i].Off..end]));
        }
        return outp;
    }

    public static byte[] WriteDat(List<(byte[] RawName, byte[] Data)> entries)
    {
        using var ms = new MemoryStream();
        Span<byte> u32 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)entries.Count);
        ms.Write(u32);
        int off = 4 + 17 * entries.Count;
        foreach (var (name, data) in entries)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)off);
            ms.Write(u32);
            ms.Write(name, 0, 13);
            off += data.Length;
        }
        foreach (var (_, data) in entries) ms.Write(data);
        return ms.ToArray();
    }

    public static string DatName(byte[] raw)
    {
        int nul = Array.IndexOf(raw, (byte)0);
        return Encoding.Latin1.GetString(raw, 0, nul < 0 ? raw.Length : nul);
    }

    public static byte[] DatGet(List<(byte[] RawName, byte[] Data)> entries, string name)
    {
        foreach (var (raw, data) in entries)
            if (DatName(raw).Equals(name, StringComparison.OrdinalIgnoreCase)) return data;
        throw new FileNotFoundException(name + " not in archive");
    }

    public static List<(byte[] RawName, byte[] Data)> DatSet(List<(byte[] RawName, byte[] Data)> entries, string name, byte[] data)
        => entries.Select(e => DatName(e.RawName).Equals(name, StringComparison.OrdinalIgnoreCase) ? (e.RawName, data) : e).ToList();

    // ------------------------------------------------------------------ EPF
    public static List<Frame?> EpfFrames(byte[] epf)
    {
        int n = BinaryPrimitives.ReadUInt16LittleEndian(epf);
        int toc = 12 + (int)BinaryPrimitives.ReadUInt32LittleEndian(epf.AsSpan(8));
        var ents = new (short T, short L, short B, short R, uint Pix, uint Sten)[n];
        for (int j = 0; j < n; j++)
        {
            var s = epf.AsSpan(toc + 16 * j);
            ents[j] = (BinaryPrimitives.ReadInt16LittleEndian(s), BinaryPrimitives.ReadInt16LittleEndian(s[2..]),
                       BinaryPrimitives.ReadInt16LittleEndian(s[4..]), BinaryPrimitives.ReadInt16LittleEndian(s[6..]),
                       BinaryPrimitives.ReadUInt32LittleEndian(s[8..]), BinaryPrimitives.ReadUInt32LittleEndian(s[12..]));
        }
        var frames = new List<Frame?>(n);
        for (int j = 0; j < n; j++)
        {
            var (t, l, b, r, pix, sten) = ents[j];
            uint next = j + 1 < n ? ents[j + 1].Pix : (uint)(toc - 12);
            int w = r - l, h = b - t;
            if (w <= 0 || h <= 0 || sten - pix != (uint)(w * h) || 12 + next > epf.Length) { frames.Add(null); continue; }
            var idx = epf[(12 + (int)pix)..(12 + (int)sten)];
            var stenRaw = epf[(12 + (int)sten)..(12 + (int)next)];
            frames.Add(new Frame(t, l, b, r, idx, StencilDecode(stenRaw, w, h), stenRaw));
        }
        return frames;
    }

    public static bool[] StencilDecode(byte[] st, int w, int h)
    {
        var m = new bool[w * h];
        int i = 0;
        for (int y = 0; y < h; y++)
        {
            int x = 0;
            while (i < st.Length)
            {
                byte c = st[i++];
                if (c == 0) break;
                if (c > 0x80)
                {
                    int run = c - 0x80;
                    for (int k = 0; k < run && x + k < w; k++) m[y * w + x + k] = true;
                    x += run;
                }
                else x += c;
            }
        }
        return m;
    }

    /// <summary>Byte-identical to the shipped 4.95 encoding: runs alternate skip/draw, a row ends right
    /// after its last drawn run (no trailing skip), a fully transparent row is just 0x00.</summary>
    public static byte[] StencilEncode(bool[] alpha, int w, int h)
    {
        var outp = new List<byte>(w * h / 4 + h);
        for (int y = 0; y < h; y++)
        {
            int end = 0;
            for (int x = w - 1; x >= 0; x--) if (alpha[y * w + x]) { end = x + 1; break; }
            int p = 0;
            while (p < end)
            {
                bool v = alpha[y * w + p];
                int n = 1;
                while (p + n < end && alpha[y * w + p + n] == v && n < 0x7F) n++;
                outp.Add((byte)(v ? 0x80 + n : n));
                p += n;
            }
            outp.Add(0);
        }
        return outp.ToArray();
    }

    public static byte[] EpfBuild(byte[] header, IReadOnlyList<Frame> frames)
    {
        using var pix = new MemoryStream();
        using var toc = new MemoryStream();
        Span<byte> e = stackalloc byte[16];
        foreach (var f in frames)
        {
            uint p = (uint)pix.Length;
            pix.Write(f.Idx);
            uint s = (uint)pix.Length;
            pix.Write(f.StenRaw ?? StencilEncode(f.Alpha, f.W, f.H));
            BinaryPrimitives.WriteInt16LittleEndian(e, f.Top);
            BinaryPrimitives.WriteInt16LittleEndian(e[2..], f.Left);
            BinaryPrimitives.WriteInt16LittleEndian(e[4..], f.Bottom);
            BinaryPrimitives.WriteInt16LittleEndian(e[6..], f.Right);
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], p);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], s);
            toc.Write(e);
        }
        var hdr = header[..12].ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(hdr, (ushort)frames.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(8), (uint)pix.Length);
        var outp = new byte[12 + pix.Length + toc.Length];
        hdr.CopyTo(outp, 0);
        pix.ToArray().CopyTo(outp, 12);
        toc.ToArray().CopyTo(outp, 12 + (int)pix.Length);
        return outp;
    }

    // ------------------------------------------------------------------ PAL
    public static List<byte[]> PalBlocks(byte[] pal)
    {
        var offs = new List<int>();
        var needle = "DLPalette"u8;
        for (int i = 0; i <= pal.Length - needle.Length; i++)
            if (pal.AsSpan(i, needle.Length).SequenceEqual(needle)) { offs.Add(i); i += needle.Length - 1; }
        var blocks = new List<byte[]>(offs.Count);
        for (int k = 0; k < offs.Count; k++)
            blocks.Add(pal[offs[k]..(k + 1 < offs.Count ? offs[k + 1] : pal.Length)]);
        return blocks;
    }

    /// <summary>768 bytes: RGB per palette entry (the block's last 1024 bytes are RGBA).</summary>
    public static byte[] PalRgb(byte[] block)
    {
        var rgb = new byte[768];
        int at = block.Length - 1024;
        for (int c = 0; c < 256; c++)
        {
            rgb[c * 3] = block[at + c * 4]; rgb[c * 3 + 1] = block[at + c * 4 + 1]; rgb[c * 3 + 2] = block[at + c * 4 + 2];
        }
        return rgb;
    }

    /// <summary>A new block carrying <paramref name="rgb"/>: the header of a no-animation template block
    /// (32 bytes, animation count 0) followed by the 256 RGBA entries.</summary>
    public static byte[] MakeBlock(byte[] template, byte[] rgb)
    {
        var block = new byte[32 + 1024];
        Array.Copy(template, block, 32);
        block[23] = 0;
        for (int c = 0; c < 256; c++)
        {
            block[32 + c * 4] = rgb[c * 3]; block[32 + c * 4 + 1] = rgb[c * 3 + 1]; block[32 + c * 4 + 2] = rgb[c * 3 + 2];
            block[32 + c * 4 + 3] = template[template.Length - 1024 + 3];
        }
        return block;
    }

    public static byte[] PalBuild(IReadOnlyList<byte[]> blocks)
    {
        using var ms = new MemoryStream();
        Span<byte> u32 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)blocks.Count);
        ms.Write(u32);
        foreach (var b in blocks) ms.Write(b);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ TBL
    static readonly byte[] Key = [75, 82, 77, 80, 74, 67, 79, 67, 16, 89, 74, 91, 70, 81, 87, 74, 67, 69, 77, 86, 74, 75, 85, 72, 75, 78, 71];
    const uint Mask = 0x55555555;

    static void Xor8(Span<byte> blk, int off)
    {
        uint idx = (uint)((-0x1234568 - off) & 0xFFFFFFFF) % 27;
        for (int k = 0; k < 8; k++)
        {
            blk[k] ^= Key[idx];
            idx = (idx + 26) % 27;
        }
    }

    public static byte[] TblDecodeWords(byte[] enc)
    {
        var outp = new byte[enc.Length / 8 * 4];
        Span<byte> blk = stackalloc byte[8];
        for (int i = 0, off = 0; i < enc.Length / 8; i++, off += 4)
        {
            enc.AsSpan(i * 8, 8).CopyTo(blk);
            Xor8(blk, off);
            uint a = BinaryPrimitives.ReadUInt32BigEndian(blk), b = BinaryPrimitives.ReadUInt32BigEndian(blk[4..]);
            BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan(i * 4), a ^ ((a ^ b) & Mask));
        }
        return outp;
    }

    public static byte[] TblEncodeWords(byte[] plain)
    {
        var outp = new byte[plain.Length / 4 * 8];
        Span<byte> blk = stackalloc byte[8];
        for (int i = 0, off = 0; i < plain.Length / 4; i++, off += 4)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(i * 4));
            BinaryPrimitives.WriteUInt32BigEndian(blk, v);      // a == b: the mask term vanishes on decode
            BinaryPrimitives.WriteUInt32BigEndian(blk[4..], v);
            Xor8(blk, off);
            blk.CopyTo(outp.AsSpan(i * 8));
        }
        return outp;
    }

    static readonly Regex TblLine = new(@"^ID (\d+), Palette (\d+), Alpha ([\d.\-]+), Light (-?\d+)", RegexOptions.Compiled);

    public static (bool Text, List<TblRec> Recs) TblParse(byte[] tbl)
    {
        var recs = new List<TblRec>();
        if (tbl.AsSpan().StartsWith("NumItems"u8))
        {
            foreach (var line in Encoding.Latin1.GetString(tbl).Split('\n'))
            {
                var m = TblLine.Match(line);
                if (m.Success)
                    recs.Add(new TblRec(uint.Parse(m.Groups[1].Value), uint.Parse(m.Groups[2].Value),
                        float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[4].Value), 0));
            }
            return (true, recs);
        }
        var d = TblDecodeWords(tbl);
        int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(d);
        for (int i = 0; i < n; i++)
        {
            var s = d.AsSpan(4 + 20 * i);
            recs.Add(new TblRec(BinaryPrimitives.ReadUInt32LittleEndian(s), BinaryPrimitives.ReadUInt32LittleEndian(s[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(s[8..]), BinaryPrimitives.ReadInt32LittleEndian(s[12..]),
                BinaryPrimitives.ReadUInt32LittleEndian(s[16..])));
        }
        return (false, recs);
    }

    public static byte[] TblBuild(bool text, IReadOnlyList<TblRec> recs)
    {
        if (text)
        {
            var sb = new StringBuilder().Append("NumItems ").Append(recs.Count).Append('\n');
            foreach (var r in recs)
                sb.Append("ID ").Append(r.Id).Append(", Palette ").Append(r.Palette)
                  .Append(", Alpha ").Append(r.Alpha.ToString("F6", CultureInfo.InvariantCulture))
                  .Append(", Light ").Append(r.Light).Append('\n');
            return Encoding.Latin1.GetBytes(sb.ToString());
        }
        var plain = new byte[4 + 20 * recs.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(plain, (uint)recs.Count);
        for (int i = 0; i < recs.Count; i++)
        {
            var s = plain.AsSpan(4 + 20 * i);
            BinaryPrimitives.WriteUInt32LittleEndian(s, recs[i].Id);
            BinaryPrimitives.WriteUInt32LittleEndian(s[4..], recs[i].Palette);
            BinaryPrimitives.WriteSingleLittleEndian(s[8..], recs[i].Alpha);
            BinaryPrimitives.WriteInt32LittleEndian(s[12..], recs[i].Light);
            BinaryPrimitives.WriteUInt32LittleEndian(s[16..], recs[i].Flag);
        }
        return TblEncodeWords(plain);
    }

    // ------------------------------------------------------------------ downscale
    /// <summary>Retail frame -> RGBA (w*h*4) through its palette, alpha from the stencil.</summary>
    public static byte[] ToRgba(Frame f, byte[] rgb)
    {
        var outp = new byte[f.W * f.H * 4];
        for (int i = 0; i < f.W * f.H; i++)
        {
            int c = f.Idx[i] * 3;
            outp[i * 4] = rgb[c]; outp[i * 4 + 1] = rgb[c + 1]; outp[i * 4 + 2] = rgb[c + 2];
            outp[i * 4 + 3] = f.Alpha[i] ? (byte)255 : (byte)0;
        }
        return outp;
    }

    /// <summary>Area-average ("box"), footprint-snapped ("snap": the box average, then the source
    /// colour in the footprint nearest to it, keeping outlines crisp) or centre-sample ("nearest")
    /// resample of an RGBA image to w2*h2. Returns float RGB and coverage alpha per output pixel.</summary>
    public static (float[] Rgb, float[] Alpha) Downscale(byte[] rgba, int w, int h, int w2, int h2, string method)
    {
        var rgb = new float[w2 * h2 * 3];
        var alpha = new float[w2 * h2];
        for (int y = 0; y < h2; y++)
        {
            int y0 = y * h / h2, y1 = Math.Max((y + 1) * h / h2, y0 + 1);
            for (int x = 0; x < w2; x++)
            {
                int x0 = x * w / w2, x1 = Math.Max((x + 1) * w / w2, x0 + 1);
                int o = y * w2 + x;
                if (method == "nearest")
                {
                    int sx = Math.Min(w - 1, (x0 + x1) / 2), sy = Math.Min(h - 1, (y0 + y1) / 2);
                    int si = (sy * w + sx) * 4;
                    rgb[o * 3] = rgba[si]; rgb[o * 3 + 1] = rgba[si + 1]; rgb[o * 3 + 2] = rgba[si + 2];
                    alpha[o] = rgba[si + 3] > 127 ? 1 : 0;
                    continue;
                }
                float r = 0, g = 0, b = 0; int n = 0, total = 0;
                for (int sy = y0; sy < y1; sy++)
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int si = (sy * w + sx) * 4;
                        total++;
                        if (rgba[si + 3] <= 127) continue;
                        r += rgba[si]; g += rgba[si + 1]; b += rgba[si + 2]; n++;
                    }
                alpha[o] = total == 0 ? 0 : (float)n / total;
                if (n == 0) continue;
                r /= n; g /= n; b /= n;
                if (method == "snap")
                {
                    float best = float.MaxValue;
                    for (int sy = y0; sy < y1; sy++)
                        for (int sx = x0; sx < x1; sx++)
                        {
                            int si = (sy * w + sx) * 4;
                            if (rgba[si + 3] <= 127) continue;
                            float d = (rgba[si] - r) * (rgba[si] - r) + (rgba[si + 1] - g) * (rgba[si + 1] - g) + (rgba[si + 2] - b) * (rgba[si + 2] - b);
                            if (d < best) { best = d; rgb[o * 3] = rgba[si]; rgb[o * 3 + 1] = rgba[si + 1]; rgb[o * 3 + 2] = rgba[si + 2]; }
                        }
                }
                else { rgb[o * 3] = r; rgb[o * 3 + 1] = g; rgb[o * 3 + 2] = b; }
            }
        }
        return (rgb, alpha);
    }

    /// <summary>Nearest palette entry (1..255; 0 stays the transparent key) for every pixel with
    /// coverage &gt;= 0.5.</summary>
    public static (byte[] Idx, bool[] Alpha) Quantize(float[] rgb, float[] alpha, byte[] pal)
    {
        var idx = new byte[alpha.Length];
        var mask = new bool[alpha.Length];
        for (int i = 0; i < alpha.Length; i++)
        {
            if (alpha[i] < 0.5f) continue;
            mask[i] = true;
            float best = float.MaxValue; int bi = 1;
            for (int c = 1; c < 256; c++)
            {
                float dr = pal[c * 3] - rgb[i * 3], dg = pal[c * 3 + 1] - rgb[i * 3 + 1], db = pal[c * 3 + 2] - rgb[i * 3 + 2];
                float d = dr * dr + dg * dg + db * db;
                if (d < best) { best = d; bi = c; if (d == 0) break; }
            }
            idx[i] = (byte)bi;
        }
        return (idx, mask);
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
        ihdr[8] = 8; ihdr[9] = 6;
        WriteChunk(ms, "IHDR", ihdr);
        var scan = new byte[(w * 4 + 1) * h];
        for (int y = 0; y < h; y++) Buffer.BlockCopy(rgba, y * w * 4, scan, y * (w * 4 + 1) + 1, w * 4);
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
