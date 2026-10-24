using System.Text;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The two mail topic fields of the <c>0x31</c> board reply carry a one-byte length: the topic in a mailbox row
/// (<c>Session.SendBoardPosts</c>, board 0) and the topic in the letter view (<c>Session.ReadMail</c>).
///
/// <para>A subject arrives through a u8 (<c>HandleNmailSend</c>), so it is at most 255 bytes, but two topics
/// are longer than the subject: the mailbox row writes an unread letter as <c>"* " + topic</c>, and the
/// keep-a-copy letter is stored as <c>"[To &lt;name&gt;] &lt;subject&gt;"</c>. Before this change both fields
/// wrote <c>(byte)length</c>, so 257 bytes went out as length 1: the client read one byte of topic and parsed
/// the rest of the row, and every row after it, out of the topic's text. Each field is now cut to 255 bytes,
/// and its length byte says exactly what was sent. The text is <c>Encoding.ASCII</c>, which writes only bytes
/// 0x00-0x7F, so the cut never splits a character.</para>
///
/// <para>Every topic that already fit is the frame it always was: the identity fact compares the real
/// builders with the pre-change builder lines, copied below as the oracle.</para>
/// </summary>
[Collection("world")]
public sealed class MailTopicCutTests
{
    private readonly SessionFixture _fx;
    public MailTopicCutTests(SessionFixture fx) => _fx = fx;

    /// <summary>A 255-byte subject sent through the native compose window (the real <c>0x3B</c> sub-6 frame)
    /// with "keep a copy" ticked. The recipient's mailbox row is <c>"* "</c> plus the first 253 bytes of the
    /// subject; the recipient's letter view keeps all 255 (it always fit). The sender's copy is topiced
    /// <c>"[To &lt;name&gt;] &lt;subject&gt;"</c>, so both its row and its letter view are cut to 255. Every
    /// body parses to its last byte. Red without the fix: the row's length byte reads 1 and the copy's letter
    /// view reads 255 + 6 + the name's length, mod 256.</summary>
    [Fact]
    public void AComposedLetterWithA255ByteSubjectListsAndReadsWellFormed()
    {
        string tag = Guid.NewGuid().ToString("N")[..6];
        var (from, fromRec, _) = _fx.PlayerWith("MtcFrom" + tag, c => c.Level = 99);
        var (to, toRec, _) = _fx.PlayerWith("MtcTo" + tag, c => c.Level = 99);
        try
        {
            string subject = Pattern(255);
            from.Receive(ComposeFrame(to.CharName, subject, "Hello", keepCopy: true));
            Assert.Equal(Ack(6, true, "Your message has been sent."), Assert.Single(fromRec.BodiesOf(0x31)));

            string copyTopic = $"[To {to.CharName}] {subject}";
            Assert.True(copyTopic.Length > 255);

            var row = Assert.Single(MailboxRows(to, toRec));
            Assert.Equal(from.CharName, row.Sender);
            Assert.Equal(Cut255("* " + subject), row.Topic);

            Assert.Equal(Cut255(subject), ReadView(to, toRec, row.Position).Topic);
            Assert.Equal(255, subject.Length);   // so the view above is the whole subject, unchanged

            var copyRow = Assert.Single(MailboxRows(from, fromRec));
            Assert.Equal(Cut255("* " + copyTopic), copyRow.Topic);
            Assert.Equal(Cut255(copyTopic), ReadView(from, fromRec, copyRow.Position).Topic);
        }
        finally
        {
            _fx.World.LeaveMap(from, SessionFixture.HomeMap);
            _fx.World.LeaveMap(to, SessionFixture.HomeMap);
        }
    }

    /// <summary>A stored 254- or 255-byte topic, unread, under a newer short letter and over an older one. The
    /// long row is cut to 255 and the rows on either side of it still parse whole, so the list says three
    /// letters and holds three. Red without the fix: the long row's length byte reads 0 or 1 and the parse
    /// runs into its text.</summary>
    [Theory]
    [InlineData(254)]
    [InlineData(255)]
    public void AnUnreadTopicThatTheMarkPushesPast255IsCutInTheMailboxRow(int length)
    {
        var (s, rec, c) = _fx.PlayerWith("MtcRow" + Guid.NewGuid().ToString("N")[..8], _ => { });
        try
        {
            string topic = Pattern(length);
            Mail.Send(c.Name, "Older", "first", "Body", 1, 2);
            Mail.Send(c.Name, "Longer", topic, "Body", 3, 4);
            Mail.Send(c.Name, "Newer", "third", "Body", 5, 6);

            var rows = MailboxRows(s, rec);
            Assert.Equal(new[] { "Newer", "Longer", "Older" }, rows.Select(r => r.Sender));
            Assert.Equal(new[] { 3, 2, 1 }, rows.Select(r => r.Position));
            Assert.Equal(new[] { "* third", Cut255("* " + topic), "* first" }, rows.Select(r => r.Topic));
            Assert.Equal(255, Encoding.ASCII.GetByteCount(rows[1].Topic));
        }
        finally { _fx.World.LeaveMap(s, SessionFixture.HomeMap); }
    }

    /// <summary>Every topic that fits is the frame it always was. For each length 0-255: the letter view
    /// (all lengths fit), and the mailbox row both unread (lengths 0-253, which fit with the mark) and read
    /// (all lengths), each compared byte for byte with the pre-change builder lines below.</summary>
    [Fact]
    public void MailTopicsThatFitAreTheFramesTheyAlwaysWere()
    {
        var (s, rec, c) = _fx.PlayerWith("MtcSame" + Guid.NewGuid().ToString("N")[..8], _ => { });
        try
        {
            for (int length = 0; length <= 255; length++)
            {
                string topic = Pattern(length);
                int pos = Mail.Send(c.Name, "Sender", topic, "Body text", 7, 8).Position;

                if (length <= 253)
                    Assert.Equal(OldMailbox(Mail.InboxFor(c.Name)), MailboxBody(s, rec));

                var letter = Mail.Get(c.Name, pos)!;
                Assert.Equal(OldReadView(letter), ReadBody(s, rec, pos));

                Assert.Equal(OldMailbox(Mail.InboxFor(c.Name)), MailboxBody(s, rec));   // now read: no mark
                Assert.True(Mail.Delete(c.Name, pos));
            }
        }
        finally { _fx.World.LeaveMap(s, SessionFixture.HomeMap); }
    }

    // ===== the pre-change builders (the oracle) ======================================================

    /// <summary>The mailbox branch of <c>SendBoardPosts</c> as it was before this change, with only the
    /// <c>SendMap</c> turned into a return. Every length is a bare <c>(byte)</c> cast.</summary>
    private static byte[] OldMailbox(List<MailPost> inbox)
    {
        var d0 = new PacketWriter().U8(4).U8(3);
        d0.U16BE((ushort)0);
        var mbn = Ascii("Mailbox");
        d0.U8((byte)mbn.Length);
        d0.Bytes(mbn);
        d0.U8((byte)inbox.Count);
        foreach (var m in inbox)
        {
            d0.U8(0);
            d0.U16BE((ushort)m.Position);
            var sn = Ascii(m.Sender);
            d0.U8((byte)sn.Length);
            d0.Bytes(sn);
            d0.U8(m.Month);
            d0.U8(m.Day);
            var topic = (m.IsRead ? "" : "* ") + m.Topic;
            var tn0 = Ascii(topic);
            d0.U8((byte)tn0.Length);
            d0.Bytes(tn0);
        }
        return d0.ToArray();
    }

    /// <summary>The letter view of <c>ReadMail</c> as it was before this change, from its type byte to its
    /// body.</summary>
    private static byte[] OldReadView(MailPost mail)
    {
        var d = new PacketWriter().U8(5).U8(3).U8(1);
        d.U16BE((ushort)mail.Position);
        var sn = Ascii(mail.Sender);
        d.U8((byte)sn.Length);
        d.Bytes(sn);
        d.U8(mail.Month);
        d.U8(mail.Day);
        var tn = Ascii(mail.Topic);
        d.U8((byte)tn.Length);
        d.Bytes(tn);
        var bn = Ascii(mail.Body);
        d.U16BE((ushort)bn.Length);
        d.Bytes(bn);
        return d.ToArray();
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s ?? "");

    // ===== reading the replies =======================================================================

    private sealed record Row(int Position, string Sender, byte Month, byte Day, string Topic);
    private sealed record Letter(int Position, string Sender, string Topic, string Body);

    /// <summary>Ask for the mailbox (<c>0x3B</c> sub-2, board 0) and return the one <c>0x31</c> body.</summary>
    private static byte[] MailboxBody(Session s, RecordingOutbound rec)
    {
        rec.Clear();
        s.Receive(SessionFixture.Frame(0x3B, new byte[] { 2, 0, 0 }));
        return Assert.Single(rec.BodiesOf(0x31));
    }

    /// <summary>Open a letter (<c>0x3B</c> sub-3, board 0) and return the one <c>0x31</c> body.</summary>
    private static byte[] ReadBody(Session s, RecordingOutbound rec, int position)
    {
        rec.Clear();
        s.Receive(SessionFixture.Frame(0x3B, new byte[] { 3, 0, 0, (byte)(position >> 8), (byte)position }));
        return Assert.Single(rec.BodiesOf(0x31));
    }

    /// <summary>The mailbox reply, walked the way the client walks it: the header, the count, then exactly
    /// that many rows, which must end on the body's last byte.</summary>
    private static List<Row> MailboxRows(Session s, RecordingOutbound rec)
    {
        byte[] b = MailboxBody(s, rec);
        var r = new Reader(b);
        Assert.Equal(4, r.U8());
        Assert.Equal(3, r.U8());
        Assert.Equal(0, r.U16());
        Assert.Equal("Mailbox", r.Str8());
        int count = r.U8();
        var rows = new List<Row>();
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(0, r.U8());
            int pos = r.U16();
            string sender = r.Str8();
            byte month = r.U8(), day = r.U8();
            rows.Add(new Row(pos, sender, month, day, r.Str8()));
        }
        Assert.Equal(b.Length, r.At);
        return rows;
    }

    /// <summary>The letter view, walked to its last byte.</summary>
    private static Letter ReadView(Session s, RecordingOutbound rec, int position)
    {
        byte[] b = ReadBody(s, rec, position);
        var r = new Reader(b);
        Assert.Equal(5, r.U8());
        Assert.Equal(3, r.U8());
        Assert.Equal(1, r.U8());
        int pos = r.U16();
        string sender = r.Str8();
        r.U8(); r.U8();
        string topic = r.Str8();
        string body = Encoding.ASCII.GetString(r.Take(r.U16()));
        Assert.Equal(b.Length, r.At);
        return new Letter(pos, sender, topic, body);
    }

    private sealed class Reader
    {
        private readonly byte[] _b;
        public int At { get; private set; }
        public Reader(byte[] b) => _b = b;
        public byte U8() => _b[At++];
        public int U16() { int v = (_b[At] << 8) | _b[At + 1]; At += 2; return v; }
        public byte[] Take(int n) { var v = _b[At..(At + n)]; At += n; return v; }
        public string Str8() => Encoding.ASCII.GetString(Take(U8()));
    }

    // ===== frames and texts ==========================================================================

    /// <summary>The native compose window's send (<c>0x3B</c> sub-6): <c>06 00 00 toLen to topicLen topic
    /// msgLen(u16BE) msg keepCopy</c>, as <c>HandleNmailSend</c> reads it.</summary>
    private static byte[] ComposeFrame(string to, string topic, string message, bool keepCopy)
    {
        byte[] t = Ascii(to), s = Ascii(topic), m = Ascii(message);
        var body = new PacketWriter().U8(6).U16BE(0)
            .U8((byte)t.Length).Bytes(t)
            .U8((byte)s.Length).Bytes(s)
            .U16BE((ushort)m.Length).Bytes(m)
            .U8((byte)(keepCopy ? 1 : 0));
        return SessionFixture.Frame(0x3B, body.ToArray());
    }

    private static byte[] Ack(byte other, bool ok, string msg)
    {
        byte[] m = Ascii(msg);
        return new PacketWriter().U8(other).U8((byte)(ok ? 1 : 0)).U8((byte)m.Length).Bytes(m).U8(7).ToArray();
    }

    /// <summary>A text of <paramref name="length"/> letters that does not repeat within 26, so a cut in the
    /// wrong place shows.</summary>
    private static string Pattern(int length) =>
        string.Concat(Enumerable.Range(0, length).Select(i => (char)('a' + i % 26)));

    private static string Cut255(string s) => s.Length > 255 ? s[..255] : s;
}
