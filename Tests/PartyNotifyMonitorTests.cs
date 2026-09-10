using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// The party path's foreign-state accesses, and the refusal order that must survive closing them (#29 rule 2,
/// <c>Server/Session.State.cs</c>; the PR #210 review's F4 and the #198 author's note).
///
/// <para><b>The notifications.</b> <c>Party.Broadcast</c> calls <c>NotifyGroup</c> on every member from the
/// thread of whichever member invited, left, kicked or disconnected — a thread that owns none of them. Rule 2
/// is a blanket rule on entering a peer's state, and that is the whole justification for the monitor here.
/// It is NOT a torn-byte repair: <c>NotifyGroup</c> is <c>SendMiniText</c>, which writes that member's
/// <c>_gameInc</c> (<c>Session.cs</c>), but the increment is a byte read once and passed BY VALUE both into
/// the encrypted body and into the frame header, so each frame decrypts with the increment it declares and a
/// lost update can only repeat a nonce — recorded as harmless at <c>Session.WorldApi.cs:314-315</c>, and
/// visible in <see cref="MonitorProbe"/> below, which decrypts with <c>pkt.Increment</c>. The accesses that
/// were genuinely unguarded are the foreign READS this slice closes with the sends: <c>p._char.Name</c> and
/// <c>p.IsIgnoring(...)</c> in <c>DoGroupChat</c>, and the invite's <c>target.IsDead</c> below.</para>
///
/// <para><b>The dead-target read.</b> The invite's <c>target.IsDead</c> refusal is <c>target._char.Hp == 0</c>
/// (<c>Session.Entity.cs</c>), read on the inviter's thread. It sat ahead of the invite's critical section so
/// that the refusal ORDER — full, then dead, then refuse — stayed put; it now sits inside that section,
/// between the cap re-check and the in-body refusals, which is the same order under the owner's monitor.</para>
///
/// <para><b>How "under the monitor" is observed.</b> <c>SendMiniText</c> has no seam of its own, so these
/// facts give the recipient a <see cref="MonitorProbe"/> outbound: <c>Session.Send</c> hands it the finished
/// frame synchronously on the sending thread, so the probe reads <c>Session.StateHeld</c>
/// (<c>Monitor.IsEntered(_state)</c>) at exactly the instant <c>_gameInc</c> was incremented to build it. The
/// dead-target read has no send of its own to observe — the refusal goes to the INVITER — so it is pinned the
/// other way, the way <c>PartyMembersRaceTests</c> pins the seat: hold the target's monitor on a third thread
/// and show the invite parks before it refuses.</para>
/// </summary>
[Collection("world")]
public sealed class PartyNotifyMonitorTests
{
    private readonly SessionFixture _fx;

    public PartyNotifyMonitorTests(SessionFixture fx) => _fx = fx;

    private const string Left = "You have left the group.";
    private const string Disbanded = "Your group has disbanded.";
    private const string Joining = "is joining the group.";
    private const string Leaving = "is leaving the group.";
    private const string FullLine = "Your group is already full.";
    private const string DeadLine = "They are unable to join this group.";
    private const string RefuseLine = "They refuse to join this group.";

    // ---- 1. a broadcast enters every other member's monitor -------------------------------------------

    /// <summary>A leave broadcasts "X is leaving the group." to the rest of the roster from the LEAVER's
    /// thread. Every one of those sends must happen inside the recipient's own monitor, not the leaver's
    /// alone. The three sessions are built in rank order on purpose so the one broadcast covers both branches
    /// of rule 2: the leaver DESCENDS into the leader and ASCENDS into the third member.</summary>
    [Fact]
    public void ABroadcastFromALeaversThreadReachesEveryOtherMemberInsideThatMembersMonitor()
    {
        var (leader, leaderProbe, _) = ProbePlayer("BroadcastMonitorLeader", groupable: true);   // lowest rank: descending
        var (leaver, leaverProbe, _) = ProbePlayer("BroadcastMonitorLeaver", groupable: true);
        var (third, thirdProbe, _)   = ProbePlayer("BroadcastMonitorThird", groupable: true);    // highest: ascending
        Assert.True(leader.StateRank < leaver.StateRank && leaver.StateRank < third.StateRank);

        SessionFixture.FormParty(leader, leaver);
        SessionFixture.FormParty(leader, third);
        leaderProbe.Clear(); leaverProbe.Clear(); thirdProbe.Clear();

        leaver.Receive(SessionFixture.GroupToggleFrame());   // the real Shift+G leave, on this thread

        // The two remaining members each heard it once, and each send was made holding THEIR monitor.
        Assert.Equal(1, leaderProbe.Count(Leaving));
        Assert.Equal(1, thirdProbe.Count(Leaving));
        Assert.True(leaderProbe.AllHeld(Leaving), leaderProbe.Explain(Leaving));
        Assert.True(thirdProbe.AllHeld(Leaving), thirdProbe.Explain(Leaving));

        // The leaver's own line is the re-entrant case (rule 3): it is sent from inside member.WithState,
        // so the monitor is held there too and the guard NotifyGroup takes releases nothing.
        Assert.Equal(1, leaverProbe.Count(Left));
        Assert.True(leaverProbe.AllHeld(Left), leaverProbe.Explain(Left));
        Assert.Equal(0, leaverProbe.Count(Leaving));   // the roster snapshot no longer holds them
    }

    /// <summary>The forming invite's two join lines, from the INVITER's thread into the invitee's session —
    /// the other <c>Party.Broadcast</c> call site.</summary>
    [Fact]
    public void TheInvitesJoinLinesReachTheInviteeInsideTheInviteesMonitor()
    {
        var (inviter, inviterProbe, _) = ProbePlayer("JoinLineInviter");
        var (invitee, inviteeProbe, _) = ProbePlayer("JoinLineInvitee", groupable: true);
        inviterProbe.Clear(); inviteeProbe.Clear();

        SessionFixture.FormParty(inviter, invitee);

        // A forming group announces both founders, so each side hears two lines.
        Assert.Equal(2, inviteeProbe.Count(Joining));
        Assert.True(inviteeProbe.AllHeld(Joining), inviteeProbe.Explain(Joining));
        Assert.Equal(2, inviterProbe.Count(Joining));
        Assert.True(inviterProbe.AllHeld(Joining), inviterProbe.Explain(Joining));
    }

    // ---- 2. group chat ---------------------------------------------------------------------------------

    /// <summary>Group chat ("!!" as the whisper target) delivers to every member from the SENDER's thread,
    /// skipping a pair where either side has the other on ignore (RTK <c>clif_isignore</c>). Each delivery is
    /// now inside the recipient's monitor, and both halves of the ignore rule still hold: the recipient
    /// ignoring the sender, and the sender ignoring the recipient.</summary>
    [Fact]
    public void GroupChatReachesEachRecipientInsideTheirMonitorAndStillSkipsAnIgnoringPair()
    {
        const string sender = "GroupChatSender", ignoredBySender = "GroupChatIgnored";
        var (leader, leaderProbe, _) = ProbePlayer("GroupChatLeader", groupable: true);
        var (talker, talkerProbe, _) = ProbePlayer(sender, groupable: true);
        // Ignores the sender: RTK blocks the line when EITHER side has the other listed.
        var (blocker, blockerProbe, _) = ProbePlayer("GroupChatBlocker", groupable: true,
            configure: c => c.IgnoreList.Add(sender));
        // ...and the other direction, the sender's own list.
        var (ignored, ignoredProbe, _) = ProbePlayer(ignoredBySender, groupable: true);

        SessionFixture.FormParty(leader, talker);
        SessionFixture.FormParty(leader, blocker);
        SessionFixture.FormParty(leader, ignored);
        SetIgnoreList(talker, ignoredBySender);
        leaderProbe.Clear(); talkerProbe.Clear(); blockerProbe.Clear(); ignoredProbe.Clear();

        talker.Receive(GroupChatFrame("field is clear"));

        const string body = "field is clear";
        Assert.Equal(1, leaderProbe.Count(body));
        Assert.True(leaderProbe.AllHeld(body), leaderProbe.Explain(body));
        // The sender's own echo — you cannot ignore yourself, and the acquisition is re-entrant.
        Assert.Equal(1, talkerProbe.Count(body));
        Assert.True(talkerProbe.AllHeld(body), talkerProbe.Explain(body));
        // Both ignore directions still drop the line entirely.
        Assert.Equal(0, blockerProbe.Count(body));
        Assert.Equal(0, ignoredProbe.Count(body));
    }

    // ---- 3. the refusal order --------------------------------------------------------------------------

    /// <summary>The three refusals, in the order the PR #210 reviewer probed on master and this branch:
    /// FULL beats DEAD, DEAD beats the shared "refuse" line, and an alive target with the toggle off still
    /// gets "refuse". Moving the dead read into the invite's critical section must not reorder any of them —
    /// the order is the contract, not an accident of where the check sits.</summary>
    [Fact]
    public void TheInviteRefusesInTheOrderFullThenDeadThenRefuse()
    {
        var (leader, leaderProbe, _) = ProbePlayer("RefusalOrderLeader");
        var fillers = Enumerable.Range(0, Party.MaxMembers - 1)
            .Select(i => ProbePlayer($"RefusalOrderFill{i}", groupable: true).session).ToArray();
        foreach (var f in fillers) SessionFixture.FormParty(leader, f);
        Assert.True(PartyOf(leader)!.IsFull);

        // FULL beats DEAD: a full group aimed at a dead player is told the group is full.
        var (deadOne, _, _) = ProbePlayer("RefusalOrderDeadOne", groupable: true, configure: c => c.Hp = 0);
        Assert.True(deadOne.IsDead);
        leaderProbe.Clear();
        SessionFixture.FormParty(leader, deadOne);
        Assert.Equal(1, leaderProbe.Count(FullLine));
        Assert.Equal(0, leaderProbe.Count(DeadLine));

        // DEAD beats REFUSE, with room in the group: an inviter with no party at all.
        var (roomy, roomyProbe, _) = ProbePlayer("RefusalOrderRoomy");
        var (deadTwo, _, _) = ProbePlayer("RefusalOrderDeadTwo", groupable: true, configure: c => c.Hp = 0);
        roomyProbe.Clear();
        SessionFixture.FormParty(roomy, deadTwo);
        Assert.Equal(1, roomyProbe.Count(DeadLine));
        Assert.Equal(0, roomyProbe.Count(FullLine) + roomyProbe.Count(RefuseLine));

        // DEAD beats REFUSE when BOTH would fire: dead AND "Join a group" off.
        var (deadAndClosed, _, _) = ProbePlayer("RefusalOrderDeadClosed", configure: c => c.Hp = 0);
        Assert.False(deadAndClosed.WantsGroup);
        roomyProbe.Clear();
        SessionFixture.FormParty(roomy, deadAndClosed);
        Assert.Equal(1, roomyProbe.Count(DeadLine));
        Assert.Equal(0, roomyProbe.Count(RefuseLine));

        // ...and the third rung on its own, so the fact above is not just "everything says dead": alive,
        // toggle off, gets the shared refusal.
        var (aliveClosed, _, _) = ProbePlayer("RefusalOrderAliveClosed");
        Assert.False(aliveClosed.IsDead);
        roomyProbe.Clear();
        SessionFixture.FormParty(roomy, aliveClosed);
        Assert.Equal(1, roomyProbe.Count(RefuseLine));
        Assert.Equal(0, roomyProbe.Count(DeadLine));
    }

    // ---- 4. the dead read happens under the TARGET's monitor -------------------------------------------

    /// <summary>Nothing of the invite — the dead-target refusal included — may be decided while the target's
    /// own critical section is occupied. Same shape as
    /// <c>PartyMembersRaceTests.TheInviteCannotSeatTheTargetWhileTheTargetsOwnMonitorIsHeld</c>, aimed at the
    /// refusal instead of the seat, because the refusal has no send of its own on the target to observe: the
    /// line goes to the INVITER. A read left outside the section answers immediately and the invite finishes
    /// while the monitor is held.</summary>
    [Fact]
    public void TheDeadTargetRefusalIsDecidedInsideTheTargetsOwnMonitor()
    {
        var (target, _, _) = ProbePlayer("HeldDeadTarget", groupable: true, configure: c => c.Hp = 0);
        var (inviter, inviterProbe, _) = ProbePlayer("HeldDeadInviter");
        Assert.True(target.IsDead);
        inviterProbe.Clear();

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = new Thread(() => target.WithState(() => { entered.Set(); release.Wait(); }))
            { IsBackground = true, Name = "dead-target-monitor-holder" };
        holder.Start();
        Assert.True(entered.Wait(5000), "the third thread never took the target's monitor");

        var invite = new Thread(() => SessionFixture.FormParty(inviter, target))
            { IsBackground = true, Name = "inviter" };
        invite.Start();

        // Reading the inviter's probe here is safe precisely because the invite is parked before it speaks.
        Assert.False(invite.Join(500), "the invite refused a dead target while that target's monitor was held");
        Assert.Equal(0, inviterProbe.Count(DeadLine));

        release.Set();
        Assert.True(invite.Join(5000), "the invite never completed after the monitor was released");
        holder.Join();

        Assert.Equal(1, inviterProbe.Count(DeadLine));
        Assert.Null(PartyOf(inviter));
        Assert.Null(PartyOf(target));
    }

    // ---- 5. the races ----------------------------------------------------------------------------------

    private const int RaceRounds = 300;
    private static readonly TimeSpan StallQuiet = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StallCap = TimeSpan.FromSeconds(120);
    private const int StallPollMs = 25;

    /// <summary>A member's own leave against a leader's kick: two removals on two threads, each broadcasting
    /// into the other's session and into the third member's. Broadcast now enters a monitor per member, so
    /// this is the shape that would cycle if it did so under <c>Party._gate</c> or out of rank order — and
    /// the class doc's "never told twice" has to survive it: each departing member hears one "left" line and
    /// the survivor hears one "disbanded".</summary>
    [Fact]
    public void ALeaveRacingAKickNeitherDeadlocksNorTellsAnybodyTwice()
    {
        var (leader, leaderProbe, leaderChar) = ProbePlayer("RaceKickLeader", groupable: true);
        var (leaver, leaverProbe, leaverChar) = ProbePlayer("RaceKickLeaver", groupable: true);
        var (kicked, kickedProbe, kickedChar) = ProbePlayer("RaceKickKicked", groupable: true);

        var wrong = new List<string>();
        long rounds = 0;
        var driver = new Thread(() =>
        {
            for (int round = 0; round < RaceRounds; round++)
            {
                SetParty(leader, null); SetParty(leaver, null); SetParty(kicked, null);
                leaderChar.Grouped = true; leaverChar.Grouped = true; kickedChar.Grouped = true;
                SessionFixture.FormParty(leader, leaver);
                SessionFixture.FormParty(leader, kicked);
                leaderProbe.Clear(); leaverProbe.Clear(); kickedProbe.Clear();

                using var gun = new Barrier(2);
                var leave = new Thread(() => { gun.SignalAndWait(); leaver.Receive(SessionFixture.GroupToggleFrame()); })
                    { IsBackground = true, Name = "member-leave" };
                var kick = new Thread(() => { gun.SignalAndWait(); SessionFixture.FormParty(leader, kicked); })
                    { IsBackground = true, Name = "leader-kick" };
                leave.Start(); kick.Start();
                leave.Join(); kick.Join();   // a hang here stops the counter below, which is what the watch sees

                int leaverLeft = leaverProbe.Count(Left), kickedLeft = kickedProbe.Count(Left);
                int disbanded = leaderProbe.Count(Disbanded);
                if (leaverLeft != 1 || kickedLeft != 1 || disbanded != 1)
                    wrong.Add($"round {round}: leaver told left {leaverLeft}x, kicked told left {kickedLeft}x, "
                            + $"survivor told disbanded {disbanded}x");
                Interlocked.Increment(ref rounds);
            }
        }) { IsBackground = true, Name = "leave-vs-kick-driver" };
        driver.Start();
        RunUntilDoneOrStalled(new[] { driver }, () => Interlocked.Read(ref rounds), StallQuiet, StallCap,
            "the leave-against-kick race");

        Assert.Equal(RaceRounds, (int)Interlocked.Read(ref rounds));
        Assert.True(wrong.Count == 0,
            $"{wrong.Count} / {RaceRounds} rounds told somebody the wrong number of times: "
            + string.Join(" | ", wrong.Take(3)));
    }

    /// <summary>An invite's join broadcast against a member's own leave: the broadcast walks a roster the
    /// leave is removing itself from, on two threads at once, each holding its own monitor and reaching for
    /// the other's. Nobody hears a join line twice, and the leaver hears "left" exactly once.</summary>
    [Fact]
    public void ABroadcastRacingAMembersOwnLeaveNeitherDeadlocksNorTellsAnybodyTwice()
    {
        var (leader, leaderProbe, leaderChar) = ProbePlayer("RaceJoinLeader", groupable: true);
        var (leaver, leaverProbe, leaverChar) = ProbePlayer("RaceJoinLeaver", groupable: true);
        var (joiner, joinerProbe, joinerChar) = ProbePlayer("RaceJoinJoiner", groupable: true);

        var wrong = new List<string>();
        long rounds = 0;
        var driver = new Thread(() =>
        {
            for (int round = 0; round < RaceRounds; round++)
            {
                SetParty(leader, null); SetParty(leaver, null); SetParty(joiner, null);
                leaderChar.Grouped = true; leaverChar.Grouped = true; joinerChar.Grouped = true;
                SessionFixture.FormParty(leader, leaver);
                leaderProbe.Clear(); leaverProbe.Clear(); joinerProbe.Clear();

                using var gun = new Barrier(2);
                var leave = new Thread(() => { gun.SignalAndWait(); leaver.Receive(SessionFixture.GroupToggleFrame()); })
                    { IsBackground = true, Name = "member-leave" };
                var invite = new Thread(() => { gun.SignalAndWait(); SessionFixture.FormParty(leader, joiner); })
                    { IsBackground = true, Name = "leader-invite" };
                leave.Start(); invite.Start();
                leave.Join(); invite.Join();

                // The joiner's OWN announcement lands exactly once whichever way the race falls — seated into
                // the surviving party, or into a new one the invite forms when the leave retired the old one
                // first. The total is 1 or 2 because a FORMING group announces both of its founders, and the
                // joiner hears the inviter's line too; more than that would be a member told twice.
                int mine = joinerProbe.Count($"{joiner.CharName} {Joining}");
                int joinLines = joinerProbe.Count(Joining), leaverLeft = leaverProbe.Count(Left);
                if (mine != 1 || joinLines > 2 || leaverLeft != 1)
                    wrong.Add($"round {round}: the joiner heard its own line {mine}x out of {joinLines} join "
                            + $"lines, the leaver was told left {leaverLeft}x");
                Interlocked.Increment(ref rounds);
            }
        }) { IsBackground = true, Name = "invite-vs-leave-driver" };
        driver.Start();
        RunUntilDoneOrStalled(new[] { driver }, () => Interlocked.Read(ref rounds), StallQuiet, StallCap,
            "the invite-against-leave race");

        Assert.Equal(RaceRounds, (int)Interlocked.Read(ref rounds));
        Assert.True(wrong.Count == 0,
            $"{wrong.Count} / {RaceRounds} rounds announced somebody the wrong number of times: "
            + string.Join(" | ", wrong.Take(3)));
    }

    // ---- machinery -------------------------------------------------------------------------------------

    /// <summary>A copy of <c>SessionActorTests.RunUntilDoneOrStalled</c>, private to this file: a deadlock is
    /// a counter that stops dead, where a busy machine only slows it down. Copied rather than shared because
    /// that file is owned by another change in flight.</summary>
    private static void RunUntilDoneOrStalled(Thread[] threads, Func<long> progress, TimeSpan quiet,
                                              TimeSpan cap, string what)
    {
        var elapsed = Stopwatch.StartNew();
        long last = progress();
        var lastMoved = TimeSpan.Zero;

        while (true)
        {
            if (threads.All(t => t.Join(0))) return;

            long now = progress();
            if (now != last) { last = now; lastMoved = elapsed.Elapsed; }
            else if (elapsed.Elapsed - lastMoved >= quiet)
                Assert.Fail($"{what}: no round completed for {quiet.TotalSeconds:0} s, stopped at {last} rounds "
                          + $"{elapsed.Elapsed.TotalSeconds:0} s in — the threads are stuck, not slow");

            if (elapsed.Elapsed >= cap)
                Assert.Fail($"{what}: still running after the {cap.TotalSeconds:0} s cap at {last} rounds — "
                          + "still moving, so not this cycle, but far past anything this machine should need");

            Thread.Sleep(StallPollMs);
        }
    }

    /// <summary>A session on the fixture's world wearing a <see cref="MonitorProbe"/> instead of the plain
    /// recorder: thread-safe, and it records whether the recipient's own monitor was held at the instant the
    /// frame was built. <paramref name="groupable"/> presets the persisted "Join a group" flag an invite's
    /// <c>WantsGroup</c> gate reads, without going through the toggle packet.</summary>
    private (Session session, MonitorProbe probe, Character character) ProbePlayer(
        string name, bool groupable = false, Action<Character>? configure = null)
    {
        var character = new Character
        {
            SchemaVersion = Character.CurrentSchemaVersion,
            Id = _fx.World.AllocatePlayerId(),
            Name = name,
            Map = SessionFixture.HomeMap,
            X = 5,
            Y = 10,
            Grouped = groupable,
        };
        configure?.Invoke(character);

        var probe = new MonitorProbe($"probe:{name}");
        var session = new Session(probe, 2005, _fx.Store, _fx.World, character);
        probe.Owner = session;                    // set before anything can send
        _fx.World.EnterMap(session, SessionFixture.HomeMap);
        probe.Clear();
        return (session, probe, character);
    }

    /// <summary>The "!!" whisper — RTK's group-chat channel (<c>clif_parsewisp</c>). Wire body, live-confirmed
    /// in <c>Session.HandleWhisperPacket</c>: <c>dstLen(u8) dst[dstLen] msgLen(u8) msg[msgLen] 00</c>.</summary>
    private static byte[] GroupChatFrame(string msg)
    {
        byte[] dst = Encoding.ASCII.GetBytes("!!");
        byte[] text = Encoding.ASCII.GetBytes(msg);
        var body = new List<byte> { (byte)dst.Length };
        body.AddRange(dst);
        body.Add((byte)text.Length);
        body.AddRange(text);
        body.Add(0);
        return SessionFixture.Frame(ClientOp.Whisper, body.ToArray());
    }

    /// <summary><c>Session._party</c> and <c>Character.IgnoreList</c>, by reflection: the roster and that
    /// field agreeing IS the invariant these facts rest on and nothing public exposes either — the same
    /// reason <c>PartyMembersRaceTests</c> reaches for <c>_party</c>.</summary>
    private static readonly FieldInfo PartyField =
        typeof(Session).GetField("_party", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo CharField =
        typeof(Session).GetField("_char", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static Party? PartyOf(Session s) => (Party?)PartyField.GetValue(s);
    private static void SetParty(Session s, Party? p) => PartyField.SetValue(s, p);

    private static void SetIgnoreList(Session s, params string[] names)
    {
        var c = (Character)CharField.GetValue(s)!;
        c.IgnoreList.Clear();
        c.IgnoreList.AddRange(names);
    }

    /// <summary>
    /// An <c>IOutbound</c> that records each <c>0x0A</c> minitext line together with the answer to
    /// <c>Session.StateHeld</c> at the moment the frame reached the transport.
    ///
    /// <para>That instant is the one that matters: <c>Session.SendMap</c> builds the packet with
    /// <c>_gameInc++</c> and calls <c>Send</c> straight through on the same thread (<c>Session.WorldApi.cs</c>
    /// → <c>_out.Send</c>), so a line recorded with <c>Held == false</c> is a line whose increment was written
    /// outside the owning session's monitor. Thread-safe, unlike <c>RecordingOutbound</c>, because these facts
    /// deliberately send into one session from two threads.</para>
    /// </summary>
    private sealed class MonitorProbe : IOutbound
    {
        private readonly List<(string Text, bool Held)> _lines = new();
        private readonly object _lock = new();

        /// <summary>The session this probe belongs to. Assigned right after construction — the session cannot
        /// be handed its own outbound before the outbound exists — and read only from <see cref="Send"/>.</summary>
        internal Session? Owner;

        public MonitorProbe(string remote) => Remote = remote;

        public string Remote { get; }
        public int Capacity => int.MaxValue;
        public int QueueDepth => 0;
        public bool Closed { get; private set; }

        public bool Send(byte[] frame)
        {
            bool held = Owner is not null && Owner.StateHeld;
            if (TkPacket.TryParse(frame, out var pkt, out _) && pkt.Opcode == ServerOp.MiniText)
            {
                var body = TkCrypt.Crypt(pkt.Body, pkt.Increment, TkCrypt.LoginKey);
                if (body.Length >= 3)
                {
                    string text = Encoding.ASCII.GetString(body, 3, body.Length - 3);
                    lock (_lock) _lines.Add((text, held));
                }
            }
            return true;
        }

        public void Close() => Closed = true;

        public void Clear() { lock (_lock) _lines.Clear(); }

        private List<(string Text, bool Held)> Matching(string needle)
        {
            lock (_lock) return _lines.Where(l => l.Text.Contains(needle)).ToList();
        }

        /// <summary>How many minitext lines carrying <paramref name="needle"/> this session was sent.</summary>
        internal int Count(string needle) => Matching(needle).Count;

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
