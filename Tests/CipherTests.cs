using Protocol.Tk495;
using Xunit;

namespace Tests;

/// <summary>
/// The 4.95 NexonInc cipher, checked against an independent model of the CLIENT's decrypt routine rather
/// than against itself — a self-inverse function round-trips through its own bug, so `Crypt(Crypt(x)) == x`
/// proves nothing about interop.
///
/// The regression that motivated this file: the 9-byte block counter is a BYTE on the client
/// (<c>NexusTK.exe 0x478680</c>: <c>cmp byte ptr [ebp+8], bl</c> compares <c>inc</c> against the counter's
/// LOW byte, and the table index is <c>ebx &amp; 0xff</c>), but the server compared the unwrapped
/// <c>int</c>. The two agree until a body runs past 9 * 256 = 2304 bytes, at which point block
/// <c>256 + inc</c> has the same byte value as <c>inc</c>: the client skips the stage-3 XOR over those
/// nine bytes and a server that doesn't wrap applies it. Nothing was ever long enough to notice until
/// terrain streaming — a 27x25 prime window is a 2706-byte body — and it surfaced as three garbled tiles
/// in the bottom rows of roughly one map load in six.
/// </summary>
public class CipherTests
{
    /// <summary>The client's decrypt (0x478680), stage for stage as disassembled. Table 0x4f3358 is the
    /// identity table, so a table lookup is just the index value.</summary>
    private static byte[] ClientDecrypt(byte[] body, byte inc)
    {
        var o = (byte[])body.Clone();

        // call @0x4786d5 — XOR the whole body with table[inc]
        for (int i = 0; i < o.Length; i++) o[i] ^= inc;

        // loop @0x4786f8 — per 9-byte group, XOR with table[group & 0xff] unless (byte)group == inc
        int groups = (o.Length - 1) / 9 + 1;
        for (int g = 0; g < groups; g++)
        {
            byte gb = (byte)g;                       // `bl` / `and eax, 0xff`
            if (gb == inc) continue;                 // `cmp byte ptr [ebp+8], bl` / `je`
            for (int k = 0; k < 9 && g * 9 + k < o.Length; k++) o[g * 9 + k] ^= gb;
        }

        // call @0x478743 — XOR the whole body with the "NexonInc." key at 0x50211c
        for (int i = 0; i < o.Length; i++) o[i] ^= TkCrypt.LoginKey[i % 9];
        return o;
    }

    private static byte[] Body(int length) =>
        Enumerable.Range(0, length).Select(i => (byte)(i * 37 + 11)).ToArray();

    /// <summary>Every increment, at the sizes the server actually emits. 2706 B is PrimeViewport's 27x25
    /// window (6-byte rect header + 675 cells x 4); it is the first thing we ever sent past the 2304-byte
    /// divergence point, and 1..44 are the increments that used to corrupt it.</summary>
    [Theory]
    [InlineData(106)]    // 1x25 walk strip
    [InlineData(114)]    // 27x1 walk strip
    [InlineData(1298)]   // the client's own 19x17 map request
    [InlineData(1606)]   // a 20x20 map sent whole
    [InlineData(2305)]   // the first length that reaches block 256
    [InlineData(2706)]   // PrimeViewport's 27x25 window
    [InlineData(5000)]   // a long board/mail packet, well past two wraps
    public void ClientDecryptsEveryIncrement(int length)
    {
        var plain = Body(length);
        for (int inc = 0; inc < 256; inc++)
            Assert.Equal(plain, ClientDecrypt(TkCrypt.Crypt(plain, (byte)inc, TkCrypt.LoginKey), (byte)inc));
    }

    /// <summary>The block counter must WRAP, so block 256+n behaves exactly like block n. Pinning this
    /// directly means a future "simplification" back to an int comparison fails here rather than in the
    /// game three tiles south of the player.</summary>
    [Fact]
    public void BlockCounterWrapsAtByteWidth()
    {
        const byte inc = 7;
        var plain = Body(9 * 300);
        var enc = TkCrypt.Crypt(plain, inc, TkCrypt.LoginKey);

        // Block 256+inc is the one the client leaves alone. Undo only the stages it does apply, and the
        // plaintext must come back.
        for (int k = 0; k < 9; k++)
        {
            int i = (256 + inc) * 9 + k;
            Assert.Equal(plain[i], (byte)(enc[i] ^ TkCrypt.LoginKey[i % 9] ^ inc));
        }
    }

    /// <summary>Self-inverse in both directions — the receive path runs the same function.</summary>
    [Theory]
    [InlineData(10)]
    [InlineData(2706)]
    public void RoundTrips(int length)
    {
        var plain = Body(length);
        for (int inc = 0; inc < 256; inc++)
        {
            var enc = TkCrypt.Crypt(plain, (byte)inc, TkCrypt.LoginKey);
            Assert.Equal(plain, TkCrypt.Crypt(enc, (byte)inc, TkCrypt.LoginKey));
        }
    }
}
