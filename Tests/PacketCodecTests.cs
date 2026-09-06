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
}
