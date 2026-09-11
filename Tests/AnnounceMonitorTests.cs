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

    // ---- 2. the ban's kick -----------------------------------------------------------------------------

    /// <summary>"@ban &lt;name&gt;" writes the ban record and then, if the player is online, saves them, tells
    /// them why they are going and drops them — all from the OPERATOR's thread, and until now with nothing held.
    /// All three are one section on the target, the shape <c>@kick</c> and <c>KickForReplacement</c> already
    /// have. The notice is the <c>0x02</c> login-box channel rather than minitext (which is why the probe decodes
    /// both) and it must still go out BEFORE the connection closes; the operator's own confirmation still reports
    /// the kick.
    ///
    /// <para>The target has to exist in the character store for <c>@ban</c> to get past its first gate
    /// (<c>CharacterStore.CharacterExists</c>), so the row is written directly into the redirected test store
    /// rather than by driving a login. The ban record itself lands in the redirected test database under a name
    /// no other fact uses.</para></summary>
    [Fact]
    public void TheBanNoticeReachesTheTargetInsideTheirMonitorBeforeTheDisconnect()
    {
        var (gm, gmProbe, _) = ProbePlayer(GmRoster.Name);
        var (target, targetProbe, character) = ProbePlayer("BanTarget");
        _fx.Store.SaveJson(CharacterStore.Key(character.Name), CharacterStore.Serialize(character));
        Assert.True(CharacterStore.CharacterExists(character.Name), "@ban refuses a name with no character row");
        gmProbe.Clear(); targetProbe.Clear();

        gm.Receive(SayFrame("@ban BanTarget duping items"));

        // LoginAuth.BanMessageFor reads the record back, so this is the real banned-login wording.
        const string needle = "This account is banned";
        Assert.Equal(1, targetProbe.Count(needle));
        Assert.True(targetProbe.AllHeld(needle), targetProbe.Explain(needle));
        Assert.Equal("This account is banned: duping items", targetProbe.Only(needle));
        Assert.True(targetProbe.Closed, "the ban must still close the connection after the notice");
        // The target's notice is the target's alone. (The operator's own "Banned … [kicked]" confirmation goes out
        // on SendLog — the 0x0D self-speech channel, which this probe deliberately does not decode — so it is not
        // asserted here; what rule 2 is about is that the operator never receives the peer's line.)
        Assert.Equal(0, gmProbe.Count(needle));
        // The ban itself really landed, so the notice is not being read off a stale record.
        Assert.True(Moderation.IsBanned(character.Name, out var why, out _));
        Assert.Equal("duping items", why);
    }

    // ---- 3. "@announce" --------------------------------------------------------------------------------

    /// <summary>"@announce &lt;message&gt;" walks the whole online roster from the OPERATOR's thread and sends
    /// each player the type-5 system line. Every one of those sends is now inside that player's own monitor —
    /// taken by <c>SystemAnnounce</c> at its definition, so this loop and the restart ladder's are covered by one
    /// acquisition. The three recipients are built in <c>StateRank</c> order around the operator so the single
    /// loop crosses both directions of rule 2, and the operator's own copy (rule 3's re-entrant case) still goes
    /// out.</summary>
    [Fact]
    public void TheGmAnnouncementEntersEveryOnlineSessionsMonitor()
    {
        var (low, lowProbe, _) = ProbePlayer("AnnounceLow");
        var (gm, gmProbe, _) = ProbePlayer(GmRoster.Name);
        var (high, highProbe, _) = ProbePlayer("AnnounceHigh");
        Assert.True(low.StateRank < gm.StateRank && gm.StateRank < high.StateRank);
        lowProbe.Clear(); gmProbe.Clear(); highProbe.Clear();

        gm.Receive(SayFrame("@announce the field is clear"));

        const string line = "the field is clear";
        foreach (var probe in new[] { lowProbe, highProbe, gmProbe })
        {
            Assert.Equal(1, probe.Count(line));
            Assert.True(probe.AllHeld(line), probe.Explain(line));
            Assert.Equal(line, probe.Only(line));   // not prefixed with the GM's name — the server speaking
        }
        // The operator walks out holding exactly what they walked in with: nothing, once the handler returns.
        Assert.False(gm.StateHeld);
    }

    // ---- 4. the restart ladder's announcement, from a thread that owns no session ------------------------

    /// <summary>The restart countdown is announced by <c>RestartSchedule.Announce</c>, which on a live server
    /// runs on the <c>PeriodicTimer</c> task <c>TkListener.StartWorld</c> starts — a thread that is NOT a session
    /// handler and holds no session monitor of its own. Driven here from a plain worker thread through the public
    /// entries the GM command uses (<c>Schedule</c> and <c>Cancel</c>; <c>Announce</c> itself is private), with
    /// the announcing thread checking first that it holds none of the three monitors it is about to enter — so a
    /// held line proves <c>SystemAnnounce</c> took the monitor rather than inheriting one.
    ///
    /// <para>Both wordings are asserted: the booking line and the cancellation line, which are the two the ladder
    /// and the trigger file both reach.</para></summary>
    [Fact]
    public void TheRestartAnnouncementEntersEverySessionsMonitorFromANonHandlerThread()
    {
        var (a, aProbe, _) = ProbePlayer("RestartA");
        var (b, bProbe, _) = ProbePlayer("RestartB");
        var (c, cProbe, _) = ProbePlayer("RestartC");
        var probes = new[] { aProbe, bProbe, cProbe };
        foreach (var p in probes) p.Clear();

        Exception? failure = null;
        var ladder = new Thread(() =>
        {
            try
            {
                // This thread is nobody's read loop: it walks in holding nothing at all.
                Assert.False(a.StateHeld); Assert.False(b.StateHeld); Assert.False(c.StateHeld);
                _fx.World.Restarts.Schedule(5, "deploying");
                Assert.True(_fx.World.Restarts.Pending);
                Assert.True(_fx.World.Restarts.Cancel());
                // And out holding nothing: every guard was disposed inside the loop.
                Assert.False(a.StateHeld); Assert.False(b.StateHeld); Assert.False(c.StateHeld);
            }
            catch (Exception e) { failure = e; }
        }) { IsBackground = true, Name = "restart-ladder" };
        ladder.Start();
        Assert.True(ladder.Join(TimeSpan.FromSeconds(60)), "the restart announcement never finished");
        if (failure is not null) throw failure;

        const string booked = "The server will restart in 5 minutes. Please find a safe place to log out.";
        const string called = "The scheduled server restart has been cancelled.";
        foreach (var probe in probes)
        {
            Assert.Equal(1, probe.Count(booked));
            Assert.True(probe.AllHeld(booked), probe.Explain(booked));
            Assert.Equal(booked, probe.Only(booked));
            Assert.Equal(1, probe.Count(called));
            Assert.True(probe.AllHeld(called), probe.Explain(called));
        }
        // The reason never reaches a player — it is log-only (RestartSchedule.RestartLine).
        Assert.Equal(0, aProbe.Count("deploying"));
    }

    // ---- 5. the ladder against a GM booking and cancelling ----------------------------------------------

    /// <summary>The ordering question the ladder raises, run rather than argued. <c>RestartSchedule</c> has a
    /// private lock, and a GM's <c>@restart</c> reaches it from a handler thread that already holds that GM's own
    /// session monitor — session monitor -&gt; <c>_lock</c>. Announcing enters every recipient's monitor, so if any
    /// caller held <c>_lock</c> across <c>Announce</c> the reverse edge (<c>_lock</c> -&gt; session monitor) would
    /// exist too and <c>StateRank</c> could not break the cycle, <c>_lock</c> being outside the session ordering
    /// entirely. Every caller announces outside the lock (<c>RestartSchedule.Schedule</c>, <c>Cancel</c>,
    /// <c>TickWarnings</c>, which snapshots the line under the lock and announces after it), so the cycle should
    /// not be reachable — and here are a few hundred rounds of both sides trying, with the progress watch rather
    /// than a stopwatch deciding whether anything wedged.</summary>
    [Fact]
    public void TheLadderAndAGmBookingCannotDeadlockAgainstEachOther()
    {
        const int Rounds = 300;
        var (gm, _, _) = ProbePlayer(GmRoster.Name);
        var (listener, listenerProbe, _) = ProbePlayer("RestartRaceListener");
        listenerProbe.Clear();

        var counter = new StallWatch.RoundCounter();
        var failures = new List<Exception>();
        var start = new Barrier(2);

        Thread Worker(string name, Action<int> round) => new(() =>
        {
            start.SignalAndWait();
            for (int i = 0; i < Rounds; i++)
            {
                try { round(i); }
                catch (Exception e) { lock (failures) failures.Add(e); return; }
                counter.Bump();
            }
        }) { IsBackground = true, Name = name };

        // The ladder's thread: no session monitor of its own, booking and calling off in turn.
        var ladder = Worker("restart-ladder", i =>
        {
            if (i % 2 == 0) _fx.World.Restarts.Schedule(10, "ladder");
            else _fx.World.Restarts.Cancel();
        });
        // The GM: a real @restart frame, so the handler holds that GM's own monitor across Schedule/Cancel.
        var operatorThread = Worker("gm-handler", i =>
            gm.Receive(SayFrame(i % 2 == 0 ? "@restart 30 deploying" : "@restart cancel")));

        var threads = new[] { ladder, operatorThread };
        foreach (var t in threads) t.Start();
        StallWatch.RunUntilDoneOrStalled(threads, () => counter.Rounds, StallWatch.StallQuiet, StallWatch.StallCap,
                                         "the restart ladder against a GM booking");

        lock (failures) Assert.Empty(failures);
        Assert.Equal(2L * Rounds, counter.Rounds);
        // Both threads walked out holding exactly what they walked in with.
        Assert.False(gm.StateHeld);
        Assert.False(listener.StateHeld);
        // And every announcement the listener actually received was built inside the listener's own monitor —
        // from whichever of the two threads happened to send it.
        const string booked = "The server will restart";
        Assert.True(listenerProbe.Count(booked) > 0, "the race produced no announcement at all");
        Assert.True(listenerProbe.AllHeld(booked), listenerProbe.Explain(booked));
        Assert.True(listenerProbe.AllHeld("cancelled"), listenerProbe.Explain("cancelled"));

        _fx.World.Restarts.Cancel();   // leave no booking behind for the rest of the collection
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

        /// <summary>The failure message for <see cref="AllHeld"/>: which lines were seen and how. Capped at a
        /// handful of hits — the race fact sends the same line hundreds of times, and a failure there is about
        /// whether ANY of them was unheld, not about reading all six hundred.</summary>
        internal string Explain(string needle)
        {
            var hits = Matching(needle);
            if (hits.Count == 0) return $"{Remote}: no line carrying \"{needle}\" was sent at all";
            var unheld = hits.Where(l => !l.Held).ToList();
            var show = (unheld.Count > 0 ? unheld : hits).Take(5);
            string more = (unheld.Count > 0 ? unheld.Count : hits.Count) > 5 ? $" (+ more of the same)" : "";
            return $"{Remote}: {hits.Count} line(s) carrying \"{needle}\", {unheld.Count} of them NOT held: " +
                   string.Join(", ", show.Select(l => $"\"{l.Text}\" monitor {(l.Held ? "HELD" : "NOT held")}")) + more;
        }
    }
}
