using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// <c>Session.ResolveOnlinePlayer</c> (#57 finding 31): the one lookup-or-refuse gate the six
/// "find this online player or say why not" sites now share.
///
/// <para>Two things are worth pinning and neither is the lookup itself. The first is that the refusal goes
/// out on the channel the CALLER named and carries the caller's own sentence: the whole point of the seam is
/// that it collapses the three retyped copies of the lookup WITHOUT collapsing three player-visible strings
/// or four channels into one house style, and a resolver that quietly routed a whisper failure to the chat
/// log would be a regression the compiler cannot see (the comment on <c>SendBlueMessage</c> records that this
/// exact mistake shipped once — an error line spoken aloud as the player's own words). The second is that a
/// HIT sends nothing at all, so a routed site does not gain a stray line on its success path.</para>
///
/// <para><see cref="AWhisperToANameThatIsNotOnlineStillRefusesOnTheBlueChannel"/> is the end-to-end half:
/// the real <c>0x19</c> whisper frame through <c>Session.Receive</c>, asserting the bytes the client parses
/// rather than the seam's own return value.</para>
/// </summary>
[Collection("world")]
public class ResolveOnlinePlayerTests
{
    /// <summary>A content-free map in the instance band, for the reason <c>TradeTeardownTests</c> gives: no
    /// Maps.csv row means nothing is blocked and no other test on the shared World is standing here. 60090 is
    /// picked past the highest id any existing class claims (60085): the band is hand-partitioned and two
    /// classes sharing an id share a map on the one fixture World — 60040 is <c>SpawnDirectorTests</c>'
    /// <c>PointMap</c>, and parking a session on it made its respawn fact fail in the full run.</summary>
    private const ushort ResolveMap = 60090;

    private const byte WhisperIn = 0x19;

    private readonly SessionFixture _fx;

    public ResolveOnlinePlayerTests(SessionFixture fx) => _fx = fx;

    /// <summary>The text of every <c>0x0A</c> minitext frame recorded, paired with its type byte — the
    /// channel and the sentence are asserted together, because either one alone would pass a routing bug.
    /// Layout is the RE'd one: <c>type(u8) len(u16BE) text</c>.</summary>
    private static List<(byte Type, string Text)> MiniTexts(RecordingOutbound o)
    {
        var lines = new List<(byte, string)>();
        foreach (var body in o.BodiesOf(ServerOp.MiniText))
            if (body.Length >= 3) lines.Add((body[0], Encoding.ASCII.GetString(body, 3, body.Length - 3)));
        return lines;
    }

    /// <summary>The text of every <c>0x0D</c> over-head speech frame — where <c>SendLog</c> lands. Body is
    /// <c>SendSpeech</c>'s: type(u8), entity id, then the length-prefixed line, so the assertion is a
    /// containment check on the ASCII rather than an offset this test has no reason to pin.</summary>
    private static List<string> SpeechText(RecordingOutbound o)
    {
        var lines = new List<string>();
        foreach (var body in o.BodiesOf(ServerOp.Speech)) lines.Add(Encoding.ASCII.GetString(body));
        return lines;
    }

    private static byte[] WhisperFrame(string to, string msg)
    {
        byte[] n = Encoding.ASCII.GetBytes(to), m = Encoding.ASCII.GetBytes(msg);
        var body = new List<byte> { (byte)n.Length };
        body.AddRange(n);
        body.Add((byte)m.Length);
        body.AddRange(m);
        body.Add(0);
        return SessionFixture.Frame(WhisperIn, body.ToArray());
    }

    [Fact]
    public void AMissReturnsNullAndRefusesOnTheChannelTheCallerNamed()
    {
        var (session, outbound) = _fx.Player("ResolveMiss", ResolveMap, 5, 5);

        // MiniText — the mentor/propose sites' channel, type 3 (the status pane).
        Assert.Null(session.ResolveOnlinePlayer("NoSuchSoul", "Player is not valid or not online.",
                                                Session.RefuseChannel.MiniText));
        Assert.Equal(new[] { ((byte)3, "Player is not valid or not online.") }, MiniTexts(outbound));

        // Blue — whisper's not-found line. SAME opcode as MiniText, DIFFERENT type byte, which is exactly
        // why the type is asserted and not just the text.
        outbound.Clear();
        Assert.Null(session.ResolveOnlinePlayer("NoSuchSoul", "NoSuchSoul is nowhere to be found.",
                                                Session.RefuseChannel.Blue));
        Assert.Equal(new[] { ((byte)0, "NoSuchSoul is nowhere to be found.") }, MiniTexts(outbound));

        // Log — @kick's channel. A different OPCODE (0x0D self-speech), so nothing lands on 0x0A at all.
        outbound.Clear();
        Assert.Null(session.ResolveOnlinePlayer("NoSuchSoul", "NoSuchSoul is not online.",
                                                Session.RefuseChannel.Log));
        Assert.Empty(MiniTexts(outbound));
        Assert.Contains(SpeechText(outbound), s => s.Contains("NoSuchSoul is not online."));

        // CommandReply — the GM command channel (Refuse), which resolves to the same 0x0D today. The value
        // exists so the call site keeps saying which it meant; this pins where it lands now.
        outbound.Clear();
        Assert.Null(session.ResolveOnlinePlayer("NoSuchSoul", "'NoSuchSoul' isn't online.",
                                                Session.RefuseChannel.CommandReply));
        Assert.Empty(MiniTexts(outbound));
        Assert.Contains(SpeechText(outbound), s => s.Contains("'NoSuchSoul' isn't online."));
    }

    [Fact]
    public void AHitReturnsThatSessionAndSendsNothing()
    {
        var (asker, outbound) = _fx.Player("ResolveAsker", ResolveMap, 6, 6);
        var (found, _) = _fx.Player("ResolveFound", ResolveMap, 7, 7);
        outbound.Clear();   // the peer's own map entry broadcast a 0x07 to us; this fact is about the resolver

        var got = asker.ResolveOnlinePlayer("ResolveFound", "should not be sent",
                                            Session.RefuseChannel.MiniText);
        Assert.Same(found, got);
        Assert.Empty(outbound.Frames);   // the success path adds no line of its own

        // The lookup is the registry's, so it is case-insensitive exactly as FindPlayer already was — the
        // resolver adds no matching rule of its own.
        Assert.Same(found, asker.ResolveOnlinePlayer("resolvefound", "should not be sent",
                                                     Session.RefuseChannel.MiniText));
    }

    [Fact]
    public void AWhisperToANameThatIsNotOnlineStillRefusesOnTheBlueChannel()
    {
        var (session, outbound) = _fx.Player("ResolveWhisperer", ResolveMap, 8, 8);

        session.Receive(WhisperFrame("GhostOfNobody", "are you there"));

        // RTK's literal wording on the blue wisp channel (0x0A type 0) — byte for byte what the retyped
        // copy sent before it was routed through the resolver.
        Assert.Equal(new[] { ((byte)0, "GhostOfNobody is nowhere to be found.") }, MiniTexts(outbound));
    }
}
