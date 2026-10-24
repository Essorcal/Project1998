using System.Text;
using System.Text.RegularExpressions;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The <c>0x02</c> status frame's one-byte text length (<c>code len text 00</c>), on both channels that send
/// it: the login server's <c>LoginSession.SendStatus</c> and the game server's <c>Session.SendMessage</c>.
/// Each used to write <c>(byte)text.Length</c> unchecked, so a text over 255 bytes, such as a ban notice
/// carrying a long reason, went out with a wrapped length. Both now build the frame through
/// <see cref="LoginStatus"/>, which cuts the text to 255 bytes.
///
/// <para>The tests cannot reference the login server, so its half is two facts: the builder's behaviour,
/// and a read of <c>SendStatus</c>'s source showing it sends through that builder and writes no length of
/// its own (the <c>CreateFaceByteTests</c> shape). The game half drives the real <c>SendMessage</c> through
/// <c>LuaMessage</c> into a recording outbound.</para>
///
/// <para>The class is in <c>"db"</c> because the game facts build a <see cref="CharacterStore"/>, which opens
/// the test database.</para>
/// </summary>
[Collection("db")]
public sealed class LoginStatusFrameTests
{
    /// <summary>A ban notice in <c>LoginAuth.BanMessageFor</c>'s wording, 40 bytes of prefix and a 260-byte
    /// reason: 300 bytes, so the old length byte read 300 &amp; 0xFF = 44. The reason counts 0-9 over and over,
    /// so where the text stops is visible.</summary>
    private static readonly string LongNotice = "This account is banned (59m remaining): "
        + string.Concat(Enumerable.Repeat("0123456789", 26));

    /// <summary>A 300-byte status text goes out as ONE frame whose length byte is 255, the length of the text
    /// it carries: the first 255 bytes of the notice, then the terminator, then nothing.
    ///
    /// <para>FALSIFIED by deleting the cut in <c>LoginStatus.Body</c>: the length byte reads 44.</para></summary>
    [Fact]
    public void AThreeHundredByteStatusTextIsCutTo255AndTheFrameStaysWellFormed()
    {
        byte[] text = Encoding.ASCII.GetBytes(LongNotice);
        Assert.Equal(300, text.Length);

        byte[] body = DecodeOneFrame(LoginStatus.Build(0x03, text));

        Assert.Equal(0x03, body[0]);
        Assert.Equal(255, body[1]);
        Assert.Equal(2 + 255 + 1, body.Length);
        Assert.Equal(text[..255], body[2..257]);
        Assert.Equal((byte)'4', body[256]);   // ...ends on a whole character of the reason: 40 + 214 = 254 -> '4'
        Assert.Equal(0, body[257]);
    }

    /// <summary>The login server's half: <c>SendStatus</c> hands its ASCII text to <see cref="LoginStatus"/>
    /// and writes no length byte of its own, so the cut above is the one its frames get.
    ///
    /// <para>FALSIFIED by restoring <c>LoginServer/LoginSession.cs</c> from 0627024: its SendStatus builds the
    /// body itself with <c>(byte)t.Length</c>.</para></summary>
    [Fact]
    public void TheLoginServerSendsItsStatusThroughTheSharedBuilder()
    {
        var src = File.ReadAllText(RepoFile("LoginServer/LoginSession.cs"));
        var fn = Regex.Match(src, @"private void SendStatus\(byte code, string text\).*?\n    \}", RegexOptions.Singleline);
        Assert.True(fn.Success, "could not find LoginSession.SendStatus to check it");
        Assert.Contains("Send(LoginStatus.Build(code, Encoding.ASCII.GetBytes(text)));", fn.Value);
        Assert.DoesNotContain("(byte)", fn.Value);
        Assert.DoesNotContain("TkPacket.Build", fn.Value);
        // ...and the 0x0F message box is still SendStatus, not a second hand-rolled frame.
        Assert.Contains("private void SendMessage(string text) => SendStatus(0x0F, text);", src);
    }

    /// <summary>The game server's half, through the real <c>SendMessage</c>: a 300-byte message box goes out as
    /// one <c>0x02</c> frame of <c>0F ff</c>, 255 bytes of text and the terminator.
    ///
    /// <para>FALSIFIED by restoring <c>Server/Session.Dialog.cs</c> from 0627024: the length byte reads 44 and
    /// the body is 303 bytes.</para></summary>
    [Fact]
    public void AThreeHundredByteGameMessageIsCutTo255AndTheFrameStaysWellFormed()
    {
        var (session, rec) = NewSession();

        session.LuaMessage(LongNotice);

        byte[] frame = Assert.Single(rec.Frames);
        byte[] body = DecodeOneFrame(frame);
        Assert.Equal(0x0F, body[0]);
        Assert.Equal(255, body[1]);
        Assert.Equal(2 + 255 + 1, body.Length);
        Assert.Equal(Encoding.ASCII.GetBytes(LongNotice[..255]), body[2..257]);
        Assert.Equal(0, body[257]);
    }

    /// <summary>The cut counts encoded bytes, and every byte is a whole character on both paths. Text that is
    /// not ASCII (Hangul here, two bytes a character in the Korean code page) reaches the wire as one
    /// <c>?</c> per character from both encoders, so 300 of them are cut to 255 <c>?</c> and no byte has the
    /// high bit a lead byte would carry. Punctuation the game path spells out in ASCII (an ellipsis is three
    /// dots) is counted after it is spelled out.</summary>
    [Fact]
    public void TheCutCountsEncodedBytesAndEveryByteIsAWholeCharacter()
    {
        string hangul = new('한', 300);   // U+D55C, a Hangul syllable

        byte[] login = DecodeOneFrame(LoginStatus.Build(0x0F, Encoding.ASCII.GetBytes(hangul)));
        Assert.Equal(255, login[1]);
        Assert.All(login[2..257], b => Assert.Equal((byte)'?', b));

        var (session, rec) = NewSession();
        session.LuaMessage(hangul);
        session.LuaMessage(new string('…', 100));   // 100 ellipses = 300 dots on the wire
        var bodies = rec.Frames.Select(DecodeOneFrame).ToList();
        Assert.Equal(2, bodies.Count);
        Assert.Equal(255, bodies[0][1]);
        Assert.All(bodies[0][2..257], b => Assert.Equal((byte)'?', b));
        Assert.Equal(255, bodies[1][1]);
        Assert.All(bodies[1][2..257], b => Assert.Equal((byte)'.', b));
        Assert.Equal(258, bodies[1].Length);
    }

    /// <summary>Every text that already fits is sent exactly as before, on the login path: for each length
    /// from 0 to 255 and each code the login server sends, the frame equals the one the old
    /// <c>SendStatus</c> body built (its lines at 0627024 are copied below as the oracle).</summary>
    [Fact]
    public void AStatusTextThatFitsIsTheFrameItAlwaysWas()
    {
        foreach (byte code in new byte[] { 0x00, 0x03, 0x05, 0x0F })
            for (int n = 0; n <= 255; n++)
            {
                byte[] text = Printable(n, seed: code);
                Assert.Equal(OldFrame(code, text), LoginStatus.Build(code, text));
            }
    }

    /// <summary>The same on the game path, through the real <c>SendMessage</c>: for each length from 0 to 255
    /// the recorded frame equals the one the old body built, including a text whose typographic punctuation
    /// the game path spells out in ASCII before it measures anything.</summary>
    [Fact]
    public void AGameMessageThatFitsIsTheFrameItAlwaysWas()
    {
        var (session, rec) = NewSession();
        for (int n = 0; n <= 255; n++)
        {
            byte[] text = Printable(n, seed: 7);
            rec.Clear();
            session.LuaMessage(Encoding.ASCII.GetString(text));
            Assert.Equal(OldFrame(0x0F, text), Assert.Single(rec.Frames));
        }
        rec.Clear();
        session.LuaMessage("You cast Sanctuary — “done”…");
        Assert.Equal(OldFrame(0x0F, Encoding.ASCII.GetBytes("You cast Sanctuary - \"done\"...")), Assert.Single(rec.Frames));
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    /// <summary>The frame both senders built before this change, line for line (LoginServer/LoginSession.cs
    /// and Server/Session.Dialog.cs at 0627024): the unchecked length byte included.</summary>
    private static byte[] OldFrame(byte code, byte[] t)
    {
        var body = new List<byte> { code, (byte)t.Length };
        body.AddRange(t);
        body.Add(0);
        var enc = TkCrypt.Crypt(body.ToArray(), 0x02, TkCrypt.LoginKey);
        return TkPacket.Build(0x02, 0x02, enc);
    }

    /// <summary>n printable ASCII bytes (space to '~'), varied by <paramref name="seed"/>.</summary>
    private static byte[] Printable(int n, int seed) =>
        Enumerable.Range(0, n).Select(i => (byte)(0x20 + (i * 7 + seed) % 95)).ToArray();

    /// <summary>One whole frame and nothing after it, opcode 0x02 under increment 0x02: its decrypted body.</summary>
    private static byte[] DecodeOneFrame(byte[] frame)
    {
        Assert.True(TkPacket.TryParse(frame, out var pkt, out int consumed));
        Assert.Equal(frame.Length, consumed);
        Assert.Equal(ServerOp.Message, pkt.Opcode);
        Assert.Equal(0x02, pkt.Increment);
        return TkCrypt.Crypt(pkt.Body, pkt.Increment, TkCrypt.LoginKey);
    }

    private static (Session, RecordingOutbound) NewSession()
    {
        var rec = new RecordingOutbound();
        return (new Session(rec, 2005, new CharacterStore(RepoPaths.CharsDir()), new World()), rec);
    }

    /// <summary>Walk up to the repo root so the source check reads the real file however tests are hosted.</summary>
    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Project1998.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}
