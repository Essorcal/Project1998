using System.Collections.Generic;
using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The live mute state on a <see cref="Session"/> (#57 finding 31), which now lives in
/// <c>Server/Session.Moderation.cs</c> next to the <c>@mute</c>/<c>@unmute</c> commands that push it rather
/// than in <c>Server/Session.Chat.cs</c>.
///
/// <para><b>Why this file exists.</b> The move is a pure relocation and the compiler proves most of it — the
/// two files are halves of one partial class, so nothing outside <c>Session</c> could even notice. What the
/// compiler does NOT prove is that the gate is still WIRED: <c>HandleChat</c> keeps the only caller
/// (<c>if (IsMuted()) { ReportMuted(); return; }</c>) and it has to keep sitting after the command table and
/// before the broadcast. There was no test on any of that before this slice — moving unowned code is exactly
/// when you want one — so the facts below drive the real <c>0x0E</c> chat frame and assert on what does and
/// does not reach the wire.</para>
///
/// <para>Expiry is the other fact worth pinning, because it is the reason the state is an absolute deadline
/// rather than a flag: a mute lifts itself with no timer anywhere, so a session that has been asleep past its
/// deadline is simply not muted the next time it is asked.</para>
/// </summary>
[Collection("world")]
public class MuteStateTests
{
    /// <summary>A content-free map in the instance band, past the highest id any other class claims — the
    /// band is hand-partitioned and the fixture World is shared. See <c>ResolveOnlinePlayerTests</c>.</summary>
    private const ushort MuteMap = 60091;

    private const string MutedPrefix = "You are muted and cannot speak";
    private const string UnmutedLine = "You are no longer muted.";

    private readonly SessionFixture _fx;

    public MuteStateTests(SessionFixture fx) => _fx = fx;

    /// <summary>The real client chat frame: <c>chatType(u8) msgLen(u8) msg[]</c>. Type 0 is say.</summary>
    private static byte[] ChatFrame(string text, byte chatType = 0)
    {
        byte[] m = Encoding.ASCII.GetBytes(text);
        var body = new List<byte> { chatType, (byte)m.Length };
        body.AddRange(m);
        return SessionFixture.Frame(ClientOp.Chat, body.ToArray());
    }

    /// <summary>Every <c>0x0D</c> line the session sent, as ASCII. Both the over-head speech bubble and
    /// <c>SendLog</c> land here, which is what lets one assertion separate "the bubble went out" from "the
    /// refusal went out" by looking for the text.</summary>
    private static List<string> Lines(RecordingOutbound o)
    {
        var lines = new List<string>();
        foreach (var body in o.BodiesOf(ServerOp.Speech)) lines.Add(Encoding.ASCII.GetString(body));
        return lines;
    }

    private static bool Any(RecordingOutbound o, string needle) => Lines(o).Exists(l => l.Contains(needle));

    [Fact]
    public void AnUnmutedPlayerSpeaksAndAMutedOneIsToldWhyNot()
    {
        var (session, outbound) = _fx.Player("MuteSpeaker", MuteMap, 5, 5);

        session.Receive(ChatFrame("hello"));
        Assert.True(Any(outbound, "MuteSpeaker: hello"));   // the over-head bubble, attributed to us
        Assert.False(Any(outbound, MutedPrefix));

        // @mute's push onto an already-online session: the placement is immediate, so it lands on the very
        // next line rather than at the next login.
        outbound.Clear();
        session.ApplyMute(Moderation.Forever, "duping items");
        Assert.True(session.IsMuted());
        Assert.True(Any(outbound, $"{MutedPrefix}: duping items"));   // permanent, so no "(… remaining)"

        outbound.Clear();
        session.Receive(ChatFrame("hello again"));
        Assert.False(Any(outbound, "MuteSpeaker: hello again"));   // no bubble reached anyone
        Assert.True(Any(outbound, MutedPrefix));                   // and the player was told why

        // @unmute, the same way round.
        outbound.Clear();
        session.ApplyMute(0, "");
        Assert.False(session.IsMuted());
        Assert.True(Any(outbound, UnmutedLine));

        outbound.Clear();
        session.Receive(ChatFrame("and now"));
        Assert.True(Any(outbound, "MuteSpeaker: and now"));
    }

    [Fact]
    public void AMuteWithAReasonSaysItAndOneWithoutDoesNotSayAnEmptyOne()
    {
        var (session, outbound) = _fx.Player("MuteReason", MuteMap, 6, 6);

        session.ApplyMute(Moderation.Forever, "");
        Assert.Equal(new[] { $"{MutedPrefix}." }, TrimmedMuteLines(outbound));

        // A null reason is normalised, not dereferenced — @unmute pushes "" but nothing stops a caller
        // handing null, and the field is declared non-nullable.
        outbound.Clear();
        session.ApplyMute(Moderation.Forever, null!);
        Assert.Equal(new[] { $"{MutedPrefix}." }, TrimmedMuteLines(outbound));

        // A timed mute carries how long is left; a permanent one deliberately does not.
        outbound.Clear();
        session.ApplyMute(Moderation.Deadline(30), "cool off");
        var timed = Assert.Single(TrimmedMuteLines(outbound));
        Assert.StartsWith($"{MutedPrefix} (", timed);
        Assert.EndsWith("): cool off", timed);
    }

    [Fact]
    public void AMuteWhoseDeadlineHasPassedIsNotAMuteAndNeedsNoTimerToLift()
    {
        var (session, outbound) = _fx.Player("MuteExpiry", MuteMap, 7, 7);

        // An absolute deadline already in the past. Nothing runs, nothing is swept: the next question simply
        // gets a different answer, which is the whole design.
        session.ApplyMute(Moderation.Now - 1, "served");
        Assert.False(session.IsMuted());
        Assert.True(Any(outbound, UnmutedLine));   // ApplyMute reports the state it actually produced

        outbound.Clear();
        session.Receive(ChatFrame("free to talk"));
        Assert.True(Any(outbound, "MuteExpiry: free to talk"));
        Assert.False(Any(outbound, MutedPrefix));
    }

    /// <summary>The mute lines only, each cut down to the sentence — the 0x0D body carries a type byte, an
    /// entity id and a length prefix ahead of the ASCII, and none of that is this file's subject.</summary>
    private static List<string> TrimmedMuteLines(RecordingOutbound o)
    {
        var found = new List<string>();
        foreach (var line in Lines(o))
        {
            int at = line.IndexOf(MutedPrefix, System.StringComparison.Ordinal);
            if (at >= 0) found.Add(line[at..].TrimEnd('\0'));
        }
        return found;
    }
}
