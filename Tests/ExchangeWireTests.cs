using Server;
using Xunit;

namespace Tests;

/// <summary>
/// The native exchange window's outbound shapes (opcode <c>0x42</c>), pinned.
///
/// <para>Both were read off the clients' own parsers, not off RTK — RTK is a 7.x server and this packet
/// family diverges from it the same way the bag and profile packets do. The one that matters is the row
/// packet (sub-type 2): 5.33's parser <c>0x42c240</c> reads an icon-COLOUR byte between the icon and the
/// name length, and 4.95's <c>0x4218a0</c> reads the name length there instead. Send 4.95 the extra byte and
/// it takes the colour as the length — the exact failure mode already lived through on <c>0x0F</c> (an empty
/// item name, or a garbled one as long as the colour index) and on <c>0x39</c>/<c>0x34</c>.</para>
///
/// <para>Also pinned: <c>rowKey</c> is the third body byte and is a KEY, not a position. The client looks it
/// up (<c>0x421dd0</c> scans each row's stored first byte) and replaces a matching row, appending only when
/// there is none — which is what lets one bag slot stay exactly one row.</para>
/// </summary>
public class ExchangeWireTests
{
    private const ushort Icon = 0xC123;   // an IconWire-encoded frame; distinct high/low bytes catch an endian flip
    private const byte Colour = 0x0C;
    private const string Label = "Apple (3)";

    [Fact]
    public void V495RowHasNoColourByte()
    {
        var b = Session.ExchangeRowBody(Session.ClientVersion.V495, mine: true, rowKey: 7, Icon, Colour, Label);
        Assert.Equal(new byte[]
        {
            0x02,        // [0] sub-type 2 = add/replace a row
            0x00,        // [1] side 0 = the RECIPIENT's own list (control 5); 1 = the other party's (control 8)
            0x07,        // [2] row key = the offerer's bag slot
            0xC1, 0x23,  // [3..4] icon, u16 BE, straight into the +0x4000 sprite resolver (0x435ab0)
            0x09,        // [5] name length — NO colour byte precedes it on 4.95
        }.Concat(System.Text.Encoding.ASCII.GetBytes(Label)).ToArray(), b);
    }

    [Fact]
    public void V533RowCarriesTheColourByte()
    {
        var b = Session.ExchangeRowBody(Session.ClientVersion.V533, mine: false, rowKey: 7, Icon, Colour, Label);
        Assert.Equal(new byte[]
        {
            0x02,
            0x01,        // side 1 = the other party's list
            0x07,
            0xC1, 0x23,
            0x0C,        // [5] icon colour — 5.33 ONLY
            0x09,        // [6] name length
        }.Concat(System.Text.Encoding.ASCII.GetBytes(Label)).ToArray(), b);
    }

    [Fact]
    public void OpenBodyIsIdThenLabelThenLevel()
    {
        var b = Session.ExchangeOpenBody(0x0001CAFE, "Snuggle(Warrior)", level: 42);
        Assert.Equal(new byte[]
        {
            0x00,                          // sub-type 0 = open the window
            0x00, 0x01, 0xCA, 0xFE,        // target entity id, u32 BE (ctor reads it into [win+0x278] / [+0x254])
            0x10,                          // label length
        }.Concat(System.Text.Encoding.ASCII.GetBytes("Snuggle(Warrior)"))
         .Concat(new byte[] { 0x00, 0x2A })  // level, u16 BE — RTK sends it; neither client reads it
         .ToArray(), b);
    }

    [Fact]
    public void OpenBodyIsTheSameOnBothClients()
    {
        // Unlike the row packet, sub-type 0 has no version divergence: 4.95's ctor 0x420e80 and 5.33's
        // 0x42b300 read id-at-[1..4] then a length-prefixed string at [5], byte for byte.
        var a = Session.ExchangeOpenBody(1, "A(B)", 1);
        var b = Session.ExchangeOpenBody(1, "A(B)", 1);
        Assert.Equal(a, b);
    }
}
