using System.Reflection;
using System.Text;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Two one-byte fields of the profile frames that could pass 255: the buff box string and the legend count.
///
/// <para><b>The buff box.</b> Every profile string goes through <c>Session.AddLenStr</c> (<c>[len u8][ASCII
/// bytes]</c>). The buff box (<c>BuffBoxText</c>, one <c>"&lt;name&gt; &lt;N&gt;s"</c> line per active effect) has
/// no cap of its own, so enough long buffs at once passed 255 and the length byte wrapped: the client read
/// <c>length mod 256</c> bytes of box and parsed the exchange flag, the legend count and the legends out of
/// the rest of the text. <c>AddLenStr</c> now cuts at 255 bytes. <c>Encoding.ASCII</c> writes one byte per
/// UTF-16 unit, never a lead or trail byte, so the cut never leaves half a character on the wire.</para>
///
/// <para><b>The legend count.</b> One byte on both clients, in the self profile (<c>0x39</c>) and the click
/// profile (<c>0x34</c>). It was clamped with <c>Math.Min(count, 255)</c> while the loop wrote every legend,
/// so past 255 the count and the list disagreed and the client stopped reading 255 records in. The list now
/// stops at the first 255, and the count names exactly the legends written.</para>
///
/// <para>Everything within the limits is the frame it always was: the identity facts compare with the
/// pre-change lines, copied below as the oracle.</para>
/// </summary>
[Collection("world")]
public sealed class ProfileListCutTests
{
    private readonly SessionFixture _fx;
    public ProfileListCutTests(SessionFixture fx) => _fx = fx;

    private static readonly MethodInfo AddLenStrMethod =
        typeof(Session).GetMethod("AddLenStr", BindingFlags.NonPublic | BindingFlags.Static)!;

    public enum Card { Self, Click }

    public static TheoryData<Card, int> EveryLegendCard => new()
    {
        { Card.Self, 2005 }, { Card.Self, 2006 }, { Card.Click, 2005 }, { Card.Click, 2006 },
    };

    // ===== the buff box ==============================================================================

    /// <summary>Twenty buffs with 24-letter names make a 599-byte box. The self profile sends its first 255
    /// bytes under a length byte of 255, and the frame parses to its last byte: the exchange flag and an
    /// empty legend list after the box, as on a short box. Seconds are masked in the comparison (they tick).
    /// Red without the fix: the length byte reads 599 mod 256 = 87.</summary>
    [Theory]
    [InlineData(2005)]
    [InlineData(2006)]
    public void ALongBuffBoxIsCutTo255AndTheSelfProfileParsesWhole(int port)
    {
        var (s, rec, c) = _fx.PlayerWith("PlcBuff" + Guid.NewGuid().ToString("N")[..8],
            ch => ch.Legends.Clear(), port: port);
        try
        {
            var names = Enumerable.Range(0, 20).Select(i => $"Buff number {i:00} of twenty").ToList();
            Assert.All(names, n => Assert.Equal(24, n.Length));
            for (int i = 0; i < names.Count; i++)
                s.ReceiveTimedBuff(new[] { "hit" }, new[] { "1" }, 600_000, $"plc_buff_{i}", names[i], "");

            string full = Session.BuffBoxJoin(names.Select(n => $"{n} 600s"));
            Assert.Equal(599, full.Length);

            bool v533 = port == 2006;
            byte[] body = CardBody(s, rec, Card.Self);
            Assert.Equal(255, body[BuffBoxAt(body, v533)]);
            var p = SelfProfile(body, v533);
            Assert.Equal(255, p.BuffBox.Length);
            Assert.Equal(MaskDigits(full[..255]), MaskDigits(Encoding.ASCII.GetString(p.BuffBox)));
            Assert.Equal(c.Exchange ? 1 : 0, p.Exchange);
            Assert.Empty(p.Legends);
        }
        finally { _fx.World.LeaveMap(s, SessionFixture.HomeMap); }
    }

    /// <summary>Every text of up to 255 bytes is written as it always was, <c>[len][Encoding.ASCII bytes]</c>:
    /// each length 0-255, plus texts with characters ASCII cannot write (each becomes <c>?</c>).</summary>
    [Fact]
    public void AddLenStrWritesEveryTextThatFitsAsItAlwaysDid()
    {
        var texts = Enumerable.Range(0, 256).Select(Pattern).ToList();
        texts.Add(null!);
        texts.Add("Caf\u00e9 \u2014 \u201cquoted\u201d \u2026");
        texts.Add(new string('\u00e9', 255));
        foreach (var t in texts)
            Assert.Equal(OldLenStr(t), LenStr(t));
    }

    /// <summary>A text over 255 bytes is cut to its first 255. The cut counts encoded bytes: 300 characters
    /// ASCII cannot write give 255 <c>?</c>, and a character beyond U+FFFF (two UTF-16 units, two <c>?</c>)
    /// that straddles the cut keeps one of them. Red without the fix: the length byte wraps.</summary>
    [Theory]
    [InlineData(256)]
    [InlineData(300)]
    [InlineData(599)]
    public void AddLenStrCutsATextOver255BytesToItsFirst255(int length)
    {
        string text = Pattern(length);
        Assert.Equal(Concat(255, Encoding.ASCII.GetBytes(text[..255])), LenStr(text));

        Assert.Equal(Concat(255, Enumerable.Repeat((byte)'?', 255).ToArray()), LenStr(new string('\u00e9', length)));

        string astral = new string('a', 254) + "\U0001F600" + "tail";   // the pair sits at 254-255
        Assert.Equal(Concat(255, Encoding.ASCII.GetBytes(new string('a', 254) + "?")), LenStr(astral));
    }

    // ===== the legend count ==========================================================================

    /// <summary>A character with 300 legends. Each card sends a count of 255 and then exactly the first 255
    /// legends, in order, and ends where it always ends (on 5.33's click card, the guardian byte). Everything
    /// before the count is the card the same character gets with no legends. Red without the fix: the count
    /// says 255 and 300 records follow.</summary>
    [Theory]
    [MemberData(nameof(EveryLegendCard))]
    public void A300LegendCardSendsCount255AndExactlyTheFirst255(Card card, int port)
    {
        var (s, rec, c) = _fx.PlayerWith("PlcLeg" + Guid.NewGuid().ToString("N")[..8],
            ch => ch.Legends.Clear(), port: port);
        try
        {
            byte[] quiet = CardBody(s, rec, card);
            int at = LegendCountAt(quiet, card, port);
            byte[] tail = quiet[(at + 1)..];

            var legends = Legends(300);
            s.WithState(() => c.Legends.AddRange(legends));
            byte[] body = CardBody(s, rec, card);

            Assert.Equal(quiet[..at], body[..at]);
            Assert.Equal(255, body[at]);
            int i = at + 1;
            for (int k = 0; k < 255; k++)
            {
                byte[] text = Encoding.ASCII.GetBytes(legends[k].Text);
                Assert.Equal(legends[k].Icon, body[i]);
                Assert.Equal(legends[k].Color, body[i + 1]);
                Assert.Equal(text.Length, body[i + 2]);
                Assert.Equal(text, body[(i + 3)..(i + 3 + text.Length)]);
                i += 3 + text.Length;
            }
            Assert.Equal(tail, body[i..]);
        }
        finally { _fx.World.LeaveMap(s, SessionFixture.HomeMap); }
    }

    /// <summary>Every legend list of 0-255 entries is the section it always was. The list holds an empty text,
    /// a 300-character text (cut to 255 by the loop, as before) and a text ASCII cannot write, and each count
    /// n sends the first n of it; the whole card is compared with the no-legend card plus the pre-change loop's
    /// bytes.</summary>
    [Theory]
    [MemberData(nameof(EveryLegendCard))]
    public void ALegendListThatFitsIsTheSectionItAlwaysWas(Card card, int port)
    {
        var (s, rec, c) = _fx.PlayerWith("PlcSame" + Guid.NewGuid().ToString("N")[..8],
            ch => ch.Legends.Clear(), port: port);
        try
        {
            byte[] quiet = CardBody(s, rec, card);
            int at = LegendCountAt(quiet, card, port);
            byte[] head = quiet[..at], tail = quiet[(at + 1)..];

            var all = Legends(255);
            all[3] = new Legend(1, 2, "");
            all[7] = new Legend(3, 4, new string('L', 300));
            all[11] = new Legend(5, 6, "Married to Caf\u00e9 \u2014 (Moon 1)");

            for (int n = 0; n <= 255; n++)
            {
                var some = all.Take(n).ToList();
                s.WithState(() => { c.Legends.Clear(); c.Legends.AddRange(some); });
                Assert.Equal(Concat(head, OldLegends(some), tail), CardBody(s, rec, card));
            }
        }
        finally { _fx.World.LeaveMap(s, SessionFixture.HomeMap); }
    }

    // ===== the pre-change lines (the oracle) =========================================================

    /// <summary><c>AddLenStr</c> as it was before this change.</summary>
    private static byte[] OldLenStr(string? s)
    {
        var d = new List<byte>();
        var b = Encoding.ASCII.GetBytes(s ?? "");
        d.Add((byte)b.Length);
        d.AddRange(b);
        return d.ToArray();
    }

    /// <summary>The legend section as all three cards wrote it before this change.</summary>
    private static byte[] OldLegends(List<Legend> legs)
    {
        var d = new List<byte>();
        d.Add((byte)Math.Min(legs.Count, 255));
        foreach (var lg in legs)
        {
            var t = Encoding.ASCII.GetBytes(lg.Text ?? "");
            if (t.Length > 255) t = t[..255];
            d.Add(lg.Icon);
            d.Add(lg.Color);
            d.Add((byte)t.Length);
            d.AddRange(t);
        }
        return d.ToArray();
    }

    // ===== reading the cards =========================================================================

    private sealed record SelfCard(byte[] BuffBox, byte Exchange, List<(byte Icon, byte Color, string Text)> Legends);

    private static byte[] LenStr(string? s)
    {
        var d = new List<byte>();
        AddLenStrMethod.Invoke(null, new object?[] { d, s });
        return d.ToArray();
    }

    /// <summary>One profile card: the profile key (<c>0x2D</c> sub-0, answered with <c>0x39</c>) or a click on
    /// yourself (<c>0x43</c> id 0, answered with <c>0x34</c>).</summary>
    private static byte[] CardBody(Session s, RecordingOutbound rec, Card card)
    {
        rec.Clear();
        if (card == Card.Self)
        {
            s.Receive(SessionFixture.Frame(ClientOp.ProfileRequest, new byte[] { 0 }));
            return Assert.Single(rec.BodiesOf(ServerOp.SelfProfile));
        }
        s.Receive(SessionFixture.Frame(ClientOp.ClickInfo, new byte[] { 1, 0, 0, 0, 0, 0 }));
        return Assert.Single(rec.BodiesOf(ServerOp.PeerProfile));
    }

    /// <summary>Where the legend count sits on a card with no legends: the last byte, except on 5.33's click
    /// card, which ends with the guardian byte.</summary>
    private static int LegendCountAt(byte[] quiet, Card card, int port)
    {
        int at = quiet.Length - (card == Card.Click && port == 2006 ? 2 : 1);
        Assert.Equal(0, quiet[at]);
        return at;
    }

    /// <summary>Where the buff box's length byte sits in a self profile: after AC/dam/hit, the four strings,
    /// the group flag, the experience, the class title and the equipment cells (three u16 on 4.95, five
    /// u16+u8 on 5.33).</summary>
    private static int BuffBoxAt(byte[] b, bool v533)
    {
        int at = 3;                                   // AC, dam, hit
        for (int k = 0; k < 4; k++) at += 1 + b[at];  // clan, clan title, title, party box
        at += 1 + 4;                                  // group flag, experience to next level
        at += 1 + b[at];                              // class title
        return at + (v533 ? 5 * 3 : 3 * 2);           // equipment cells
    }

    /// <summary>The self profile walked from the buff box to its last byte; every field must fit in the
    /// body.</summary>
    private static SelfCard SelfProfile(byte[] b, bool v533)
    {
        int at = BuffBoxAt(b, v533);
        Assert.True(at + 1 + b[at] + 2 <= b.Length, $"a {b[at]}-byte box leaves no room in a {b.Length}-byte body");
        byte[] box = b[(at + 1)..(at + 1 + b[at])];
        at += 1 + b[at];
        byte exchange = b[at++];
        int count = b[at++];
        var legends = new List<(byte, byte, string)>();
        for (int k = 0; k < count; k++)
        {
            Assert.True(at + 3 <= b.Length && at + 3 + b[at + 2] <= b.Length,
                $"legend {k} of {count} runs past the body");
            legends.Add((b[at], b[at + 1], Encoding.ASCII.GetString(b, at + 3, b[at + 2])));
            at += 3 + b[at + 2];
        }
        Assert.Equal(b.Length, at);
        return new SelfCard(box, exchange, legends);
    }

    // ===== data ======================================================================================

    /// <summary><paramref name="n"/> legends with distinct icons, colours and texts of varying length.</summary>
    private static List<Legend> Legends(int n) =>
        Enumerable.Range(0, n)
            .Select(i => new Legend((byte)(i % 251), (byte)(i % 7), $"Legend {i:000} " + new string('x', i % 40)))
            .ToList();

    private static string Pattern(int length) =>
        string.Concat(Enumerable.Range(0, length).Select(i => (char)('a' + i % 26)));

    private static string MaskDigits(string s) =>
        new string(s.Select(ch => char.IsAsciiDigit(ch) ? '#' : ch).ToArray());

    private static byte[] Concat(byte first, byte[] rest) => new[] { first }.Concat(rest).ToArray();

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
