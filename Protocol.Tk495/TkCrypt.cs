using System.Security.Cryptography;
using System.Text;

namespace Protocol.Tk495;

/// <summary>
/// NexusTK ciphers. Two schemes, both verified/ported from the RTK reference:
///  - Login channel + static-key game packets: the simple self-inverse 3-stage XOR (Crypt).
///  - Game channel (world data): a per-session cipher keyed by the CHARACTER NAME
///    (populate_table -> generate_key2 -> crypt2). Most opcodes use this.
/// </summary>
public static class TkCrypt
{
    /// <summary>Key baked into the 4.95 client; used for the login channel.</summary>
    public static readonly byte[] LoginKey = "NexonInc."u8.ToArray();

    /// <summary>Static game key for the whitelisted opcodes (svkey1packets). 7.x uses Urk#nI7ni.</summary>
    public static byte[] MapKey = "Urk#nI7ni"u8.ToArray();

    /// <summary>Kept for CLI compatibility; the table scheme always appends index bytes.</summary>
    public static bool MapUseIndex = true;

    /// <summary>set_packet_indexes() output with fixed rnd()=0x1337.</summary>
    public static readonly byte[] GameIndex = { 0x13, 0xF7, 0x60 };

    /// <summary>Server-&gt;client opcodes that use the STATIC key; everything else uses the table key.</summary>
    public static readonly byte[] SvKey1 = { 2, 3, 10, 64, 68, 94, 96, 98, 102, 111 };

    // ---- simple 3-stage XOR (login) ----
    public static byte[] Crypt(ReadOnlySpan<byte> data, byte inc, byte[] key)
    {
        var o = data.ToArray();
        for (int i = 0; i < o.Length; i++)
        {
            o[i] ^= key[i % 9];
            o[i] ^= (byte)(i / 9);
            if ((i / 9) != inc) o[i] ^= inc;
        }
        return o;
    }

    // ---- game channel: per-session name-keyed cipher ----

    /// <summary>Build the ~1056-byte key table from the character name (MD5 hash chain).</summary>
    public static byte[] PopulateTable(string name)
    {
        string t = Md5Hex(name);
        t = Md5Hex(t);
        for (int i = 0; i < 32; i++) t += Md5Hex(t);
        return Encoding.ASCII.GetBytes(t);

        static string Md5Hex(string s) =>
            Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(s))).ToLowerInvariant();
    }

    /// <summary>Derive the 9-byte per-packet key from the table (fixed index bytes 13 F7 60).</summary>
    public static byte[] GenerateKey2(byte[] table, bool fromClient)
    {
        uint k1 = 0xF7, k2 = 0x6013;
        if (fromClient) { k1 ^= 0x25; k2 ^= 0x2361; } else { k1 ^= 0x21; k2 ^= 0x7424; }
        k1 *= k1;
        var key = new byte[9];
        for (int i = 0; i < 9; i++) { key[i] = table[(k1 * (uint)i + k2) & 0x3FF]; k1 += 3; }
        return key;
    }

    public static bool UsesTableKey(byte opcode) => Array.IndexOf(SvKey1, opcode) < 0;

    /// <summary>crypt2: encrypt the packet body in place (offset 5, len-5 bytes) with a 9-byte key.</summary>
    public static void Crypt2InPlace(byte[] packet, byte[] key)
    {
        int len = (packet[1] << 8) | packet[2];
        int plen = len - 5;
        byte inc = packet[4];
        int group = 0, gc = 0;
        for (int i = 0; i < plen; i++)
        {
            int p = 5 + i;
            packet[p] ^= key[i % 9];
            byte kv = (byte)(group % 256);
            if (kv != inc) packet[p] ^= kv;
            packet[p] ^= inc;
            if (++gc == 9) { group++; gc = 0; }
        }
    }
}
