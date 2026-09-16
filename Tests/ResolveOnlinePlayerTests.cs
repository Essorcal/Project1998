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

    /// <summary>The staff name the routed GM commands below run as. Same roster file and same content
    /// <see cref="CommandTableTests"/> writes, deliberately: <c>StaffAccounts.Load</c> replaces the roster
    /// wholesale, so a second class declaring a different name would demote the first one's sessions
    /// depending on run order.</summary>
    private const string GmName = "cmdgm";

    private readonly SessionFixture _fx;

    public ResolveOnlinePlayerTests(SessionFixture fx)
    {
        _fx = fx;
        lock (TestProcessState.Gate)
        {
            Directory.CreateDirectory(TestProcessState.StateDirectory);
            File.WriteAllText(Path.Combine(TestProcessState.StateDirectory, "gm_accounts.txt"), GmName + "\n");
            StaffAccounts.Load();
        }
    }

    /// <summary>A command the way the read loop runs one: a framed <c>0x0E</c> chat packet, so the tier gate
    /// and the table lookup are part of what is under test.</summary>
    private static void Run(Session session, string command)
    {
        var text = Encoding.ASCII.GetBytes(command);
        var body = new byte[2 + text.Length];
        body[0] = 0;
        body[1] = (byte)text.Length;
        text.CopyTo(body, 2);
        session.Receive(SessionFixture.Frame(0x0E, body));
    }

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

    /// <summary>The three GM commands routed through the resolver by #57 part 3 — <c>@where</c>,
    /// <c>@bring</c> and <c>@rez</c> — still refuse an offline name with the sentence they always sent, on
    /// the command-reply channel (<c>Refuse</c> -&gt; <c>0x0D</c>), and nothing lands on the status pane.
    ///
    /// <para>Driven through the real chat frame rather than the seam, because what the routing could break
    /// is the CALLER: a site that passed the wrong channel enum, or let the sentence drift, compiles.
    /// Falsification: change one of the three call sites to <c>RefuseChannel.MiniText</c> and its row fails
    /// on the empty-pane assertion. Run it, confirm red, restore.</para></summary>
    [Theory]
    [InlineData("@where")]
    [InlineData("@bring")]
    [InlineData("@rez")]
    public void ARoutedGmCommandRefusesAnOfflineNameOnTheCommandChannel(string command)
    {
        var (session, outbound) = _fx.Player(GmName, ResolveMap, 9, 9);
        outbound.Clear();

        Run(session, command + " GhostOfNobody");

        Assert.Empty(MiniTexts(outbound));
        Assert.Contains(SpeechText(outbound), t => t.Contains("'GhostOfNobody' isn't online."));
    }

    /// <summary>"@click &lt;name&gt;" keeps the BLUE channel for an offline name — the same wisp channel the
    /// whisper refusal uses, and a different type byte on the same opcode as the status pane, which is the
    /// pair this whole file exists to keep apart.</summary>
    [Fact]
    public void ClickProfileRefusesAnOfflineNameOnTheBlueChannel()
    {
        var (session, outbound) = _fx.Player(GmName, ResolveMap, 10, 10);
        outbound.Clear();

        Run(session, "@click GhostOfNobody");

        Assert.Equal(new[] { ((byte)0, "GhostOfNobody is nowhere to be found.") }, MiniTexts(outbound));
    }

    /// <summary>The profile window's "Group" button aimed at someone who is not online: the real
    /// <c>0x2E</c> invite frame, refused on the blue channel exactly as it was before the routing. RTK bails
    /// silently here; the feedback is ours, and the comment at the call site is the record of that choice.
    /// </summary>
    [Fact]
    public void APartyInviteToAnOfflineNameRefusesOnTheBlueChannel()
    {
        var (session, outbound) = _fx.Player("ResolveInviter", ResolveMap, 11, 11);
        outbound.Clear();

        session.Receive(SessionFixture.PartyInviteFrame("GhostOfNobody"));

        Assert.Equal(new[] { ((byte)0, "GhostOfNobody is nowhere to be found.") }, MiniTexts(outbound));
    }
}
