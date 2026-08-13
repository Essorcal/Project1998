using Server;
using Xunit;

namespace Tests;

/// <summary>
/// Pins the opcode <c>0x06</c> cell layout (<see cref="MapCell"/>).
///
/// <para>Both the viewport stream and the door cell patch send <c>0x06</c>, and each client's handler reads
/// a FIXED cell width with no length check — 4 bytes on 4.95 (<c>0x44fb90</c>), 6 on 5.33
/// (<c>sub_469060</c>). Emitting the wrong width does not throw and does not fail a smoke test: the client
/// consumes the next cell's bytes as its own, reads past the end of the body, and repaints the run with
/// garbage. That shipped once — doors on 5.33 drew wrong tiles until the next full refresh.</para>
/// </summary>
public class MapCellTests
{
    private const Session.ClientVersion V533 = Session.ClientVersion.V533;
    private const Session.ClientVersion V495 = Session.ClientVersion.V495;

    private static byte[] One(ushort tile, ushort pass, ushort obj, Session.ClientVersion ver)
    {
        var b = new List<byte>();
        MapCell.Write(b, tile, pass, obj, ver);
        return b.ToArray();
    }

    [Fact]
    public void FiveThreeThree_IsThreeBigEndianShorts()
    {
        Assert.Equal(3, MapCell.ShortsPerCell(V533));
        Assert.Equal(6, MapCell.BytesPerCell(V533));
        Assert.Equal(new byte[] { 0x02, 0x8C, 0x00, 0x03, 0x06, 0x06 }, One(652, 3, 1542, V533));
    }

    [Fact]
    public void FourNineFive_IsTwoBigEndianShortsWithPassInTheGroundWord()
    {
        Assert.Equal(2, MapCell.ShortsPerCell(V495));
        Assert.Equal(4, MapCell.BytesPerCell(V495));
        // pass 3 -> top two bits set: 652 | 0xC000 == 0xC28C
        Assert.Equal(new byte[] { 0xC2, 0x8C, 0x06, 0x06 }, One(652, 3, 1542, V495));
    }

    [Fact]
    public void FourNineFive_GroundWordRoundTripsExactly()
    {
        // The real stream path hands in the WHOLE ground word plus its own pass. Re-packing must reproduce
        // the word byte-for-byte, or a streamed cell stops matching the client's own .map copy.
        foreach (ushort word in new ushort[] { 1, 651, 8114, 0xC000, 0xC001, 0xDA38, 0xFFFF })
        {
            ushort pass = (ushort)((word >> 14) & 3);
            var b = One(word, pass, 0, V495);
            Assert.Equal(word, (ushort)((b[0] << 8) | b[1]));
        }
    }

    [Fact]
    public void FiveThreeThree_TileShortIsNotPollutedByPass()
    {
        // The bug this guards: packing pass into the tile short. A sheet-2-derived value >= 0xC000 reads as
        // NEGATIVE in the 5.33 handler's signed bounds check, so the cell silently draws nothing.
        var b = One(652, 3, 0, V533);
        Assert.Equal(652, (ushort)((b[0] << 8) | b[1]));
        Assert.Equal(3, (ushort)((b[2] << 8) | b[3]));
    }

    [Fact]
    public void BothCallSitesAgreeOnWidth_ForAnyCellCount()
    {
        // A run's total length must be exactly count * BytesPerCell, which is what the client assumes when
        // it walks the body. Any drift here desyncs the whole run rather than one cell.
        foreach (var ver in new[] { V495, V533 })
        {
            var b = new List<byte>();
            for (int i = 0; i < 7; i++) MapCell.Write(b, (ushort)(600 + i), 0, 0, ver);
            Assert.Equal(7 * MapCell.BytesPerCell(ver), b.Count);
        }
    }
}
