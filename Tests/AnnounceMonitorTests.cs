using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The last four cross-session status-text sends: the 1:1 whisper, the ban's kick, "@announce" and the restart
/// ladder's announcement. Each of them composes a line on ONE thread and hands it to ANOTHER player's session,
/// and each did so with no monitor at all.
///
/// <para><b>What the monitor is for here.</b> #29 rule 2 (<c>Server/Session.State.cs:25-33</c>) is a blanket
/// rule: entering another session's state happens under that session's monitor. It is not a torn-byte repair —
/// the <c>_gameInc</c> these sends write is read once and passed BY VALUE into both the encrypted body and the
/// frame header, so a lost update can only repeat a nonce (harmless, <c>Server/Session.WorldApi.cs:314-315</c>).
/// The accesses that were genuinely unguarded are the foreign READS on the same path: for the whisper, the
/// target's name (twice) and their ignore list; for the ban's kick, the target's name inside <c>Disconnect</c>'s
/// log line and the character graph <c>FlushNow</c> serialises. The two announcements carry no foreign read
/// beyond the send itself, and are here because rule 2 is a blanket rule.</para>
///
/// <para><b>The restart ladder is not a handler thread.</b> <c>RestartSchedule.Announce</c> runs on the
/// <c>PeriodicTimer</c> task <c>TkListener.StartWorld</c> starts, on the trigger-file poll, and on a GM's
/// <c>@restart</c> handler thread — three different threads, only one of which owns a session. So two facts
/// below drive it from a thread that is NOT a session handler, and one races that thread against a GM booking
/// and cancelling restarts, because <c>RestartSchedule</c> has a private lock of its own and the question rule 2
/// asks about a new ordering can only be answered by both sides running at once.</para>
///
/// <para><b>How "under the monitor" is observed.</b> The send helpers have no seam of their own, so each fact
/// gives the recipient a <see cref="MonitorProbe"/> outbound: <c>Session.Send</c> hands it the finished frame
/// synchronously on the sending thread, so the probe can read <c>Session.StateHeld</c>
/// (<c>Monitor.IsEntered(_state)</c>) at exactly the instant the frame was built. The probe records both
/// channels these sites use — <c>0x0A</c> minitext (the whisper and both announcements) and the ban's
/// <c>0x02</c> login-box message. The shape is <c>Tests/PeerMiniTextMonitorTests.cs</c>'s, copied rather than
/// shared: that file is PR #225's and is not edited here.</para>
/// </summary>
[Collection("world")]
public sealed class AnnounceMonitorTests
{
    private readonly SessionFixture _fx;

    public AnnounceMonitorTests(SessionFixture fx)
    {
        _fx = fx;
        GmRoster.Ensure();
    }

    // ---- 1. the 1:1 whisper ----------------------------------------------------------------------------

    /// <summary>A whisper reads the target's name and both sides' ignore lists on the SENDER's thread, delivers
    /// on the target's session, and reads their name once more for the sender's echo. Those reads and the
    /// delivery are now one critical section on the target. Both halves of the ignore rule (RTK
    /// <c>clif_isignore</c>: either side blocking drops the line) must still refuse with the same wording, the
    /// echo must still carry the target's own proper-cased name, and the delivery must be held for a target
    /// both BELOW and ABOVE the sender in <c>StateRank</c> so one fact crosses both branches of rule 2.</summary>
    [Fact]
    public void TheWhisperReachesTheTargetInsideTheirMonitorAndStillRefusesBothIgnoreDirections()
    {
        const string sender = "WhisperSender";
        var (low, lowProbe, _) = ProbePlayer("WhisperLow");
        var (self, selfProbe, _) = ProbePlayer(sender);
        var (high, highProbe, _) = ProbePlayer("WhisperHigh");
        // One ignores the sender; the sender ignores the other. Both directions must refuse.
        var (blocker, blockerProbe, _) = ProbePlayer("WhisperBlocker", c => c.IgnoreList.Add(sender));
        var (ignored, ignoredProbe, _) = ProbePlayer("WhisperIgnored");
        Assert.True(low.StateRank < self.StateRank && self.StateRank < high.StateRank);

        SetIgnoreList(self, ignored.CharName);
        foreach (var p in new[] { lowProbe, selfProbe, highProbe, blockerProbe, ignoredProbe }) p.Clear();

        // The real 0x19 whisper frame, on this thread: Receive -> Handle -> WithState(Dispatch) -> DoWhisper,
        // exactly the shape the read loop produces.
        self.Receive(WhisperFrame(low.CharName, "field is clear"));
        self.Receive(WhisperFrame(high.CharName, "field is clear"));

        const string delivered = "WhisperSender\" field is clear";
        foreach (var probe in new[] { lowProbe, highProbe })
        {
            Assert.Equal(1, probe.Count(delivered));
            Assert.True(probe.AllHeld(delivered), probe.Explain(delivered));
            Assert.Equal(delivered, probe.Only(delivered));
        }

        // The sender's echo carries the TARGET's name, read inside that same section, and is unchanged.
        Assert.Equal($"{low.CharName}> field is clear", selfProbe.Only($"{low.CharName}>"));
        Assert.Equal($"{high.CharName}> field is clear", selfProbe.Only($"{high.CharName}>"));
        Assert.Equal(0, selfProbe.Count(delivered));   // the target's copy is the target's alone

        // Both ignore directions still refuse, with RTK's wording, and neither target hears anything.
        selfProbe.Clear();
        self.Receive(WhisperFrame(blocker.CharName, "field is clear"));
        self.Receive(WhisperFrame(ignored.CharName, "field is clear"));
        const string refusal = "They cannot hear you right now.";
        Assert.Equal(2, selfProbe.Count(refusal));
        Assert.Equal(0, blockerProbe.Count(delivered));
        Assert.Equal(0, ignoredProbe.Count(delivered));
        Assert.Equal(0, selfProbe.Count($"{blocker.CharName}>"));   // no echo for a refused whisper
        Assert.Equal(0, selfProbe.Count($"{ignored.CharName}>"));
    }

    // ===== plumbing =====================================================================================

    /// <summary><see cref="StaffAccounts"/> is empty by default (a fresh deployment has no staff), so the GM
    /// facts here would otherwise be answered with "Unknown command". Writes the roster into the redirected test
    /// state directory (#23 points P1998_STATE at a temp dir before anything loads) under the process-wide gate.
    /// The name and the file content are byte-identical to what <c>CommandTableTests</c> and
    /// <c>PeerMiniTextMonitorTests</c> write, so whichever class writes it first the others' rewrites are no-ops
    /// and none can demote another's sessions mid-run.</summary>
    private static class GmRoster
    {
        public const string Name = "cmdgm";
        private static bool _done;

        public static void Ensure()
        {
            lock (TestProcessState.Gate)
            {
                if (_done) return;
                Directory.CreateDirectory(TestProcessState.StateDirectory);
                File.WriteAllText(Path.Combine(TestProcessState.StateDirectory, "gm_accounts.txt"), Name + "\n");
                StaffAccounts.Load();
                _done = true;
            }
        }
    }

    /// <summary>A socket-free session whose outbound is a <see cref="MonitorProbe"/>, built the way
    /// <c>SessionFixture.PlayerWith</c> builds one — same world, same store, same map, 4.95 port — but with the
    /// probe in place of the recorder so every frame carries the monitor answer with it.</summary>
    private (Session session, MonitorProbe probe, Character character) ProbePlayer(
        string name, Action<Character>? configure = null)
    {
        var character = new Character
        {
            SchemaVersion = Character.CurrentSchemaVersion,
            Id = _fx.World.AllocatePlayerId(),
            Name = name,
            Map = SessionFixture.HomeMap,
            X = 5,
            Y = 10,
        };
        configure?.Invoke(character);

        var probe = new MonitorProbe($"probe:{name}");
        var session = new Session(probe, 2005, _fx.Store, _fx.World, character);
        probe.Owner = session;                    // set before anything can send
        _fx.World.EnterMap(session, SessionFixture.HomeMap);
        probe.Clear();
        return (session, probe, character);
    }

    /// <summary>The real <c>0x19</c> whisper frame. Wire body, as <c>Session.HandleWhisperPacket</c> reads it:
    /// <c>dstLen(u8) dst[dstLen] msgLen(u8) msg[msgLen] 00</c>.</summary>
    private static byte[] WhisperFrame(string dstName, string msg)
    {
        byte[] dst = Encoding.ASCII.GetBytes(dstName);
        byte[] text = Encoding.ASCII.GetBytes(msg);
        var body = new List<byte> { (byte)dst.Length };
        body.AddRange(dst);
        body.Add((byte)text.Length);
        body.AddRange(text);
        body.Add(0);
        return SessionFixture.Frame(ClientOp.Whisper, body.ToArray());
    }

    /// <summary>Ordinary speech (<c>0x0E</c>): <c>chatType(u8) msgLen(u8) msg[]</c> — the frame the client sends
    /// for anything typed into the chat bar, GM commands included.</summary>
    private static byte[] SayFrame(string text)
    {
        byte[] t = Encoding.ASCII.GetBytes(text);
        var body = new List<byte> { 0x00, (byte)t.Length };
        body.AddRange(t);
        return SessionFixture.Frame(ClientOp.Chat, body.ToArray());
    }

    private static readonly FieldInfo CharField =
        typeof(Session).GetField("_char", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary><c>Character.IgnoreList</c> by reflection — nothing public exposes it, the same reason
    /// <c>PartyNotifyMonitorTests</c> and <c>PeerMiniTextMonitorTests</c> reach for it.</summary>
    private static void SetIgnoreList(Session s, params string[] names)
    {
        var c = (Character)CharField.GetValue(s)!;
        c.IgnoreList.Clear();
        c.IgnoreList.AddRange(names);
    }

    /// <summary>
    /// An <c>IOutbound</c> that records each line this session was sent together with the answer to
    /// <c>Session.StateHeld</c> at the moment the frame reached the transport.
    ///
    /// <para>That instant is the one that matters: <c>Session.SendMap</c> builds the packet with
    /// <c>_gameInc++</c> and calls <c>Send</c> straight through on the same thread, so a line recorded with
    /// <c>Held == false</c> is a line built outside the owning session's monitor. Both channels these sites use
    /// are decoded: <c>0x0A</c> minitext (<c>type(u8) len(u16BE) text</c>, encrypted under its own increment)
    /// and the ban's <c>0x02</c> login-box message (<c>0x0F len(u8) text 00</c>, fixed increment <c>0x02</c>).
    /// Thread-safe, unlike <c>RecordingOutbound</c> — the race fact drives two threads through it.</para>
    /// </summary>
    private sealed class MonitorProbe : IOutbound
    {
        private readonly List<(string Text, bool Held)> _lines = new();
        private readonly object _lock = new();

        /// <summary>The session this probe belongs to. Assigned right after construction — the session cannot be
        /// handed its own outbound before the outbound exists — and read only from <see cref="Send"/>.</summary>
        internal Session? Owner;

        public MonitorProbe(string remote) => Remote = remote;

        public string Remote { get; }
        public int Capacity => int.MaxValue;
        public int QueueDepth => 0;
        public bool Closed { get; private set; }

        public bool Send(byte[] frame)
        {
            bool held = Owner is not null && Owner.StateHeld;
            if (!TkPacket.TryParse(frame, out var pkt, out _)) return true;
            var body = TkCrypt.Crypt(pkt.Body, pkt.Increment, TkCrypt.LoginKey);
            string? text = null;
            if (pkt.Opcode == ServerOp.MiniText && body.Length >= 3)
                text = Encoding.ASCII.GetString(body, 3, body.Length - 3);
            else if (pkt.Opcode == 0x02 && body.Length >= 3 && body[0] == 0x0F)
                text = Encoding.ASCII.GetString(body, 2, Math.Min(body[1], body.Length - 2));
            if (text is not null) lock (_lock) _lines.Add((text, held));
            return true;
        }

        public void Close() => Closed = true;

        public void Clear() { lock (_lock) _lines.Clear(); }

        private List<(string Text, bool Held)> Matching(string needle)
        {
            lock (_lock) return _lines.Where(l => l.Text.Contains(needle)).ToList();
        }

        /// <summary>How many lines carrying <paramref name="needle"/> this session was sent.</summary>
        internal int Count(string needle) => Matching(needle).Count;

        /// <summary>The one line carrying <paramref name="needle"/>, for asserting the exact text.</summary>
        internal string Only(string needle) => Assert.Single(Matching(needle)).Text;

        /// <summary>True when there is at least one such line and EVERY one of them was built while this
        /// session's own monitor was held.</summary>
        internal bool AllHeld(string needle)
        {
            var hits = Matching(needle);
            return hits.Count > 0 && hits.All(l => l.Held);
        }

        /// <summary>The failure message for <see cref="AllHeld"/>: which lines were seen and how.</summary>
        internal string Explain(string needle)
        {
            var hits = Matching(needle);
            if (hits.Count == 0) return $"{Remote}: no line carrying \"{needle}\" was sent at all";
            return $"{Remote}: " + string.Join(", ",
                hits.Select(l => $"\"{l.Text}\" monitor {(l.Held ? "HELD" : "NOT held")}"));
        }
    }
}
