using Protocol.Tk495;
using Xunit;

namespace Tests;

public sealed class PacketCodecTests
{
    [Fact]
    public void NumericAndStringFieldsHaveExactWireBytes()
    {
        byte[] expected = Convert.FromHexString("AB123489ABCDEF034100FF0102");
        var writer = new PacketWriter().U8(0xAB).U16BE(0x1234).U32BE(0x89ABCDEF)
            .Str8(new byte[] { 0x41, 0, 0xFF }).Bytes(new byte[] { 1, 2 });
        Assert.Equal(expected, writer.ToArray());
        var reader = new PacketReader(expected);
        Assert.Equal(0xAB, reader.U8());
        Assert.Equal(0x1234, reader.U16BE());
        Assert.Equal(0x89ABCDEFu, reader.U32BE());
        Assert.Equal(new byte[] { 0x41, 0, 0xFF }, reader.Str8().ToArray());
        Assert.Equal(new byte[] { 1, 2 }, reader.Bytes(2).ToArray());
        Assert.Equal(0, reader.Remaining);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TruncatedIntegerDoesNotAdvance(int length)
    {
        var reader = new PacketReader(new byte[length]);
        try { reader.U32BE(); Assert.Fail("Truncated u32 was accepted."); }
        catch (ArgumentOutOfRangeException) { Assert.Equal(0, reader.Position); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("03")]
    [InlineData("034142")]
    public void TruncatedStringDoesNotConsumeItsPrefix(string hex)
    {
        var reader = new PacketReader(Convert.FromHexString(hex));
        try { reader.Str8(); Assert.Fail("Truncated string was accepted."); }
        catch (ArgumentOutOfRangeException) { Assert.Equal(0, reader.Position); }
    }

    [Fact]
    public void NegativeAndOverflowSizedReadsAreRejectedWithoutAdvancing()
    {
        var reader = new PacketReader(new byte[] { 1 });
        foreach (int size in new[] { -1, int.MaxValue })
        {
            try { reader.Bytes(size); Assert.Fail("Invalid size was accepted."); }
            catch (ArgumentOutOfRangeException) { Assert.Equal(0, reader.Position); }
        }
    }

    [Fact]
    public void StringLengthBoundaryDoesNotWrapOrPartiallyWrite()
    {
        var writer = new PacketWriter().U8(7);
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Str8(new byte[256]));
        Assert.Equal(new byte[] { 7 }, writer.ToArray());
        byte[] boundary = new PacketWriter().Str8(new byte[255]).ToArray();
        Assert.Equal(256, boundary.Length);
        Assert.Equal(255, boundary[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    public void GameFramingKeepsTheExistingCipherAndHasNoTrailer(byte increment)
    {
        byte[] plain = Enumerable.Range(0, 2706).Select(i => (byte)i).ToArray();
        byte[] frame = TkPacket.BuildGame(0x06, increment, plain);
        Assert.Equal(plain.Length + 5, frame.Length);
        Assert.Equal(new byte[] { 0xAA, 0x0A, 0x94, 0x06, increment }, frame[..5]);
        Assert.Equal(TkCrypt.Crypt(plain, increment, TkCrypt.LoginKey), frame[5..]);
    }

    /// <summary>A length field under <see cref="TkPacket.MinLength"/> is MALFORMED, and saying so is all the
    /// parser does about it: no throw, nothing consumed, no packet.
    ///
    /// <para>This is a silent-failure guard pointing the other way. <c>3 + len</c> with <c>len</c> 0 or 1
    /// satisfied the "have I got the bytes" check and then sliced a negative body length, so five bytes
    /// starting <c>AA 00 00</c> from any peer threw <see cref="ArgumentOutOfRangeException"/> out of the
    /// shared read loop and into each session's catch — the game's stackful Error clause, one line per
    /// connection, at whatever rate a scanner could open sockets.</para>
    ///
    /// <para>Falsified by putting the old body back (<c>int total = 3 + length;</c> straight to the slice,
    /// with the <c>length &lt; MinLength</c> line deleted): the two malformed rows go red with
    /// <c>System.ArgumentOutOfRangeException : Specified argument was out of the range of valid values.</c>
    /// out of <c>TkPacket.Parse</c>, and <c>AA 00 02 10 7F</c> and the four-byte row stay green — which is
    /// what makes the two malformed rows the fact and the other two the controls.</para></summary>
    [Theory]
    // the defect: a length field of 0 and of 1, in five bytes — enough for total = 3 + len to be "satisfied"
    [InlineData("AA00000000", (int)TkPacket.FrameStatus.Malformed, 0)]
    [InlineData("AA00010000", (int)TkPacket.FrameStatus.Malformed, 1)]
    // the floor itself: len = 2 is the shortest LEGAL frame, opcode + increment and no body
    [InlineData("AA0002107F", (int)TkPacket.FrameStatus.Frame, 2)]
    // and an ordinary partial frame still waits, rather than being called malformed
    [InlineData("AA000310", (int)TkPacket.FrameStatus.NeedMore, 3)]
    public void ALengthFieldUnderTwoIsMalformedRatherThanShort(string hex, int expected, int length)
    {
        var status = TkPacket.Parse(Convert.FromHexString(hex), out var pkt, out int consumed, out int len);

        Assert.Equal((TkPacket.FrameStatus)expected, status);
        Assert.Equal(length, len);
        if (status == TkPacket.FrameStatus.Frame)
        {
            Assert.Equal(0x10, pkt.Opcode);
            Assert.Equal(0x7F, pkt.Increment);
            Assert.Empty(pkt.Body);
            Assert.Equal(5, consumed);
        }
        else
        {
            Assert.Equal(0, consumed);
            Assert.Null(pkt.Body);
        }

        // The two-out overload every other caller uses: false for both non-frames, and still no throw.
        Assert.Equal(status == TkPacket.FrameStatus.Frame,
            TkPacket.TryParse(Convert.FromHexString(hex), out _, out _));
    }
}
