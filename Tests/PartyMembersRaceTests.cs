using System;
using System.Collections.Generic;
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
/// #167. A party's membership is written by whichever session leaves, kicks or invites, and READ on the
/// killer's thread on every group kill — plus group chat, the roster text and <c>Party.Broadcast</c>. While
/// <c>Party._members</c> was a plain <c>List&lt;Session&gt;</c> a kick or a disconnect landing inside the
/// killer's eligibility <c>foreach</c> threw <c>InvalidOperationException</c> out of it; <c>Session.Handle</c>
/// logs and drops the packet, so that kill paid nobody.
/// </summary>
[Collection("world")]
public sealed class PartyMembersRaceTests
{
    private readonly SessionFixture _fx;

    public PartyMembersRaceTests(SessionFixture fx) => _fx = fx;

    /// <summary>How many kills the killer's thread resolves while the membership churns underneath it.
    /// The window is the handful of instructions between two <c>MoveNext</c> calls on a two-or-three element
    /// list, so the count buys reproduction probability and nothing else.</summary>
    private const int Kills = 4000;

    [Fact]
    public void AGroupKillNeverThrowsWhileAMemberLeavesAndRejoinsOnAnotherThread()
    {
        const string mobKey = "party_race_mob";
        var (killer, _, killerCharacter) = _fx.PlayerWith("RaceKiller", c => { c.Totem = 4; });
        var (mate, _, _)  = _fx.PlayerWith("RaceMate",  c => { c.Totem = 4; c.Grouped = true; });
        var (churn, _, _) = _fx.PlayerWith("RaceChurn", c => { c.Totem = 4; c.Grouped = true; });
        SessionFixture.FormParty(killer, mate);
        SessionFixture.FormParty(killer, churn);

        // The mutator drives the two REAL write paths on its own thread, each under its own session's
        // monitor and neither under the killer's: Shift+G leaves (RemoveFromParty -> Party.Remove), Shift+G
        // again re-arms "Join a group", and a NON-leader's 0x2E re-invite adds (Party.Add). Nothing here
        // touches the killer's session, which is the whole point — the two threads share only the list.
        int stop = 0, cycles = 0;
        Exception? mutatorError = null;
        var mutator = new Thread(() =>
        {
            try
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    churn.Receive(SessionFixture.GroupToggleFrame());              // leave
                    churn.Receive(SessionFixture.GroupToggleFrame());              // "Join a group" back ON
                    SessionFixture.FormParty(mate, churn);                         // rejoin, invited by a peer
                    cycles++;
                    Thread.Yield();
                }
            }
            catch (Exception e) { mutatorError = e; }
        }) { IsBackground = true, Name = "party-mutator" };
        mutator.Start();

        // reward 0 so this loop sends NOTHING (RecordingOutbound is deliberately not thread-safe, and the
        // mutator's broadcasts already write the killer's recorder). The eligibility scan and the tally loop
        // — the enumeration this issue is about — run exactly as they do for a paying kill.
        Exception? killerError = null;
        int paid = 0;
        try
        {
            for (int i = 0; i < Kills; i++)
            {
                killer.WithState(() => killer.AwardKillExp(0, SessionFixture.HomeMap, 5, 10, mobKey));
                paid++;
            }
        }
        catch (Exception e) { killerError = e; }

        Volatile.Write(ref stop, 1);
        mutator.Join();

        Assert.Null(killerError);
        Assert.Null(mutatorError);
        Assert.True(cycles > 0, $"the mutator never completed a leave/rejoin cycle ({cycles})");
        // Every kill counted for the killer: the tally loop runs off the eligibility snapshot, so a scan that
        // threw is a kill that credited nobody.
        Assert.Equal(Kills, paid);
        Assert.Equal(Kills, killer.KillCount(mobKey));

        // ...and a kill resolved on the quiet side of the churn still pays.
        uint before = killerCharacter.Exp;
        killer.WithState(() => killer.AwardKillExp(10, SessionFixture.HomeMap, 5, 10, mobKey));
        Assert.True(killerCharacter.Exp > before, $"the killer was not paid ({before} -> {killerCharacter.Exp})");
    }

    // ---- the straggler comes back from the removal, not from a re-read (#167) -------------------------

    private const string Disbanded = "Your group has disbanded.";

    [Fact]
    public void TheLastMemberLeftStandingIsToldTheGroupDisbandedExactlyOnce()
    {
        var (leader, leaderRec, leaderCharacter) = _fx.PlayerWith("StragglerLeader", c => { });
        var (first, firstRec)  = _fx.Player("StragglerFirst");
        var (second, secondRec) = _fx.Player("StragglerSecond");
        MakeGroupable(first);
        MakeGroupable(second);
        SessionFixture.FormParty(leader, first);
        SessionFixture.FormParty(leader, second);
        leaderRec.Clear(); firstRec.Clear(); secondRec.Clear();

        first.Receive(SessionFixture.GroupToggleFrame());    // three down to two: nobody disbands
        Assert.Equal(0, MiniTexts(leaderRec, Disbanded));

        second.Receive(SessionFixture.GroupToggleFrame());   // two down to one: the leader is the straggler

        Assert.Equal(1, MiniTexts(leaderRec, Disbanded));
        Assert.Equal(0, MiniTexts(firstRec, Disbanded));
        Assert.Equal(0, MiniTexts(secondRec, Disbanded));
        Assert.Equal(1, MiniTexts(firstRec, "You have left the group."));
        Assert.Equal(1, MiniTexts(secondRec, "You have left the group."));
        Assert.False(leaderCharacter.Grouped);               // the disband flipped the last member's status OFF
    }

    /// <summary>The two-thread version, on <see cref="Party.Remove"/> itself. Asserting on the notification
    /// TEXT under two threads would be asserting on <see cref="RecordingOutbound"/>, which is deliberately
    /// not thread-safe (both leavers broadcast into the straggler's recorder), so the assertion is on the
    /// value <c>RemoveFromParty</c> now acts on: exactly one of two simultaneous removals is handed the
    /// straggler, and it is the right session.</summary>
    [Fact]
    public void TwoSimultaneousRemovalsHandTheStragglerToExactlyOneOfThem()
    {
        var (leader, _) = _fx.Player("ConcurrentStragglerLeader");
        var (first, _)  = _fx.Player("ConcurrentStragglerFirst");
        var (second, _) = _fx.Player("ConcurrentStragglerSecond");

        for (int round = 0; round < 200; round++)
        {
            var party = new Party(leader, first);
            party.Add(leader, second);

            Session? fromFirst = null, fromSecond = null;
            using var gun = new Barrier(2);
            var a = new Thread(() => { gun.SignalAndWait(); fromFirst = party.Remove(first).Straggler; });
            var b = new Thread(() => { gun.SignalAndWait(); fromSecond = party.Remove(second).Straggler; });
            a.Start(); b.Start();
            a.Join(); b.Join();

            int handed = (fromFirst is null ? 0 : 1) + (fromSecond is null ? 0 : 1);
            Assert.Equal(1, handed);
            Assert.Same(leader, fromFirst ?? fromSecond);
            Assert.Single(party.Members);
            Assert.Same(leader, party.Members[0]);
        }
    }

    // ---- the invite writes the TARGET's field under the TARGET's monitor (#167) -----------------------

    private const string Joining = "is joining the group.";
    private const string Refused = "They refuse to join this group.";

    [Fact]
    public void TheInviteCannotSeatTheTargetWhileTheTargetsOwnMonitorIsHeld()
    {
        var (target, targetRec) = _fx.Player("HeldInviteTarget");
        var (leader, _) = _fx.Player("HeldInviteLeader");
        MakeGroupable(target);
        targetRec.Clear();

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = new Thread(() => target.WithState(() => { entered.Set(); release.Wait(); }))
            { IsBackground = true, Name = "monitor-holder" };
        holder.Start();
        Assert.True(entered.Wait(5000), "the third thread never took the target's monitor");

        var inviter = new Thread(() => SessionFixture.FormParty(leader, target))
            { IsBackground = true, Name = "inviter" };
        inviter.Start();

        // Nothing of the invite may land while its critical section on the target is occupied. Reading the
        // recorder here is safe precisely because the inviter is parked before its first send.
        Assert.False(inviter.Join(500), "the invite completed while the target's monitor was held");
        Assert.Equal(0, MiniTexts(targetRec, Joining));

        release.Set();
        Assert.True(inviter.Join(5000), "the invite never completed after the monitor was released");
        holder.Join();

        // A forming group announces both founders, so the target hears two lines.
        Assert.Equal(2, MiniTexts(targetRec, Joining));
    }

    [Fact]
    public void TwoInvitersRacingForOneTargetLandItInExactlyOneParty()
    {
        // The target is built FIRST so it outranks neither inviter: both nested acquisitions are DESCENDING
        // (Session.State.cs rule 2), which is the branch that drops the inviter's own monitor while it waits.
        var (target, targetRec) = _fx.Player("ContestedTarget");
        var (one, oneRec) = _fx.Player("ContestedInviterOne");
        var (two, twoRec) = _fx.Player("ContestedInviterTwo");
        Assert.True(target.StateRank < one.StateRank && target.StateRank < two.StateRank);

        for (int round = 0; round < 200; round++)
        {
            MakeGroupable(target);
            targetRec.Clear(); oneRec.Clear(); twoRec.Clear();

            using var gun = new Barrier(2);
            var a = new Thread(() => { gun.SignalAndWait(); SessionFixture.FormParty(one, target); });
            var b = new Thread(() => { gun.SignalAndWait(); SessionFixture.FormParty(two, target); });
            a.Start(); b.Start();
            a.Join(); b.Join();

            // Exactly one group formed around the target (two founder lines, not four), and exactly one
            // inviter was told no. Each recorder here is written by one thread only.
            Assert.Equal(2, MiniTexts(targetRec, Joining));
            Assert.Equal(1, MiniTexts(oneRec, Refused) + MiniTexts(twoRec, Refused));

            // Put everyone back: the target leaves, which drops the winner's party to one and disbands it.
            target.Receive(SessionFixture.GroupToggleFrame());
        }
    }

    // ---- the seat and the disband are ONE decision under the gate (#167, review round 1) --------------
    // A removal takes only the leaf gate, so it needs neither the leaver's nor the inviter's monitor: it can
    // land between an invite body's `var party = _party` and its `party.Add(...)`. When the disband decided
    // at that swap was then acted on unconditionally, the invitee was seated into an array that still held
    // the straggler while the straggler was told the group had disbanded and had its own field nulled.

    /// <summary>How many rounds each race shape runs. The window is the handful of instructions between the
    /// invite body's party read and its seat, so the count buys reproduction probability and nothing else —
    /// the kick shape stranded ~1 round in 5 before the fix, the leave shape ~4 in 5.</summary>
    private const int RaceRounds = 1000;

    [Fact]
    public void AnInviteRacingAKickStrandsNobodyInADisbandedParty()
    {
        var (t, _, tc) = ConcurrentPlayer("StrandKickTarget");
        var (l, _, lc) = ConcurrentPlayer("StrandKickLeader");
        var (a, _, ac) = ConcurrentPlayer("StrandKickMember");

        var stranded = new List<string>();
        for (int round = 0; round < RaceRounds; round++)
        {
            SetParty(t, null); SetParty(l, null); SetParty(a, null);
            tc.Grouped = true; lc.Grouped = true; ac.Grouped = true;
            SessionFixture.FormParty(l, a);
            var old = PartyOf(l)!;

            // A invites T while the LEADER re-invites A, which is the kick gesture.
            using var gun = new Barrier(2);
            var invite = new Thread(() => { gun.SignalAndWait(); SessionFixture.FormParty(a, t); });
            var kick   = new Thread(() => { gun.SignalAndWait(); SessionFixture.FormParty(l, a); });
            invite.Start(); kick.Start();
            Assert.True(invite.Join(5000) && kick.Join(5000), $"round {round}: the race hung");

            var bad = Inconsistency(round, old, l, a, t);
            if (bad is not null) stranded.Add(bad);
        }
        Assert.True(stranded.Count == 0,
            $"{stranded.Count} / {RaceRounds} rounds ended stranded: {string.Join(" | ", stranded.Take(3))}");
    }

    [Fact]
    public void AnInviteRacingAMembersLeaveStrandsNobodyInADisbandedParty()
    {
        var (t, _, tc) = ConcurrentPlayer("StrandLeaveTarget");
        var (l, _, lc) = ConcurrentPlayer("StrandLeaveLeader");
        var (a, _, ac) = ConcurrentPlayer("StrandLeaveMember");

        var stranded = new List<string>();
        for (int round = 0; round < RaceRounds; round++)
        {
            SetParty(t, null); SetParty(l, null); SetParty(a, null);
            tc.Grouped = true; lc.Grouped = true; ac.Grouped = true;
            SessionFixture.FormParty(l, a);
            var old = PartyOf(l)!;

            // The other member leaves through the real Shift+G frame while the survivor invites a third.
            using var gun = new Barrier(2);
            var invite = new Thread(() => { gun.SignalAndWait(); SessionFixture.FormParty(l, t); });
            var leave  = new Thread(() => { gun.SignalAndWait(); a.Receive(SessionFixture.GroupToggleFrame()); });
            invite.Start(); leave.Start();
            Assert.True(invite.Join(5000) && leave.Join(5000), $"round {round}: the race hung");

            var bad = Inconsistency(round, old, l, a, t);
            if (bad is not null) stranded.Add(bad);
        }
        Assert.True(stranded.Count == 0,
            $"{stranded.Count} / {RaceRounds} rounds ended stranded: {string.Join(" | ", stranded.Take(3))}");
    }

    /// <summary>A leader's kick reads <c>member._party</c> on its own thread and then parks in
    /// <c>member.Snapshot()</c>, so it can reach <c>Party.Remove</c> after the member's own leave has already
    /// removed them. That second removal changes nothing, so it may not speak: one "disbanded" to the
    /// straggler and one "left" to the leaver, not two of each.</summary>
    [Fact]
    public void AKickLandingAfterTheMembersOwnLeaveSaysNothingASecondTime()
    {
        var (l, lRec, _) = ConcurrentPlayer("DoubleRemovalLeader");    // built first, so the kick's thread
        var (m, mRec, _) = ConcurrentPlayer("DoubleRemovalMember");    // keeps its own monitor while it waits
        SessionFixture.FormParty(l, m);
        lRec.Clear(); mRec.Clear();

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        // The member's own leave, run while the member's monitor is held — the shape of its real Shift+G
        // handler, and of the disconnect teardown whose FlushNow holds that monitor for milliseconds.
        var leave = new Thread(() => m.WithState(() =>
        {
            entered.Set();
            release.Wait();
            m.Receive(SessionFixture.GroupToggleFrame());
        })) { IsBackground = true, Name = "member-leave" };
        leave.Start();
        Assert.True(entered.Wait(5000), "the leave thread never took the member's monitor");

        var kick = new Thread(() => SessionFixture.FormParty(l, m)) { IsBackground = true, Name = "leader-kick" };
        kick.Start();
        Assert.False(kick.Join(300), "the kick did not park on the member's monitor");
        release.Set();
        Assert.True(leave.Join(5000), "the member's own leave hung");
        Assert.True(kick.Join(5000), "the kick hung");

        Assert.Equal(1, lRec.MiniTexts(Disbanded));
        Assert.Equal(1, mRec.MiniTexts(Left));
        Assert.Null(PartyOf(l));
        Assert.Null(PartyOf(m));
    }

    // ---- a member the kick has swapped out is still holding the party (#167, review round 2) ----------
    // The kick is the one removal that runs on a thread that is NOT the member's: Party.Remove swaps them out
    // at the gate, then member.WithState waits for their monitor. While that wait lasts the roster no longer
    // holds them but their _party still names it — and in the DESCENDING case the wait has dropped the
    // leader's monitor, so another member's leave can reach TryDisband and RETIRE the party inside the gap.
    // Everything the swapped-out member's own packet then reads has to survive the empty array.

    /// <summary>How long the kicked-member window is hunted for, and how many rounds it takes. The window is
    /// the gap between the kick's <c>Snapshot()</c> releasing the member's monitor and its <c>WithState</c>
    /// re-taking it, so the counts buy reproduction probability and nothing else — it landed on the first
    /// round in the review's own run.</summary>
    private const int WindowRounds = 20000;
    private const int WindowBudgetMs = 60000;

    /// <summary>A leader kicks B while B's own handler is mid-packet, and the third member's leave retires the
    /// party inside that window: B's <c>_party</c> still names a party whose roster is now EMPTY. B's own
    /// 0x2D profile request is answered from there — <c>Party.Leader</c> answers null for a retired party
    /// instead of indexing <c>Members[0]</c>, which threw <c>IndexOutOfRangeException</c> out of the handler
    /// and dropped the reply (#167 review, F3) — and the kick's <c>WithState</c> clears B's field when it
    /// finally lands.</summary>
    [Fact]
    public void AKickedMembersOwnPacketSurvivesThePartyBeingRetiredUnderIt()
    {
        var (b, bRec, bc) = ConcurrentPlayer("RetiredReadMember");   // lowest rank of the three
        var (a, _, ac) = ConcurrentPlayer("RetiredReadLeaver");
        var (l, lRec, lc) = ConcurrentPlayer("RetiredReadLeader");   // highest: the kick DESCENDS into B, so
        Assert.True(l.StateRank > b.StateRank && l.StateRank > a.StateRank);   // rule 2 drops L's own monitor
                                                                               // for the wait and the leave gets in
        Exception? leaderRead = null;
        Party? namedInWindow = null;
        int rosterInWindow = -1, profiles = -1;
        bool landed = false;
        int round = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (; round < WindowRounds && !landed && clock.ElapsedMilliseconds < WindowBudgetMs; round++)
        {
            SetParty(l, null); SetParty(a, null); SetParty(b, null);
            lc.Grouped = true; ac.Grouped = true; bc.Grouped = true;
            SessionFixture.FormParty(l, a);
            SessionFixture.FormParty(l, b);
            var old = PartyOf(l)!;
            Assert.Equal(3, old.Members.Count);
            lRec.Clear(); bRec.Clear();

            using var inWindow = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            using var kickDone = new ManualResetEventSlim(false);
            bool caught = false;
            // B's own handler thread: it re-takes B's monitor until it holds it at the moment the kick has
            // swapped B out of the roster but has not yet written B._party — that write is under this very
            // monitor — and then stays inside its packet while the party is retired around it.
            var handler = new Thread(() =>
            {
                while (!caught && !kickDone.IsSet)
                {
                    b.WithState(() =>
                    {
                        if (!ReferenceEquals(PartyOf(b), old) || old.Members.Any(m => ReferenceEquals(m, b))) return;
                        caught = true;
                        inWindow.Set();
                        release.Wait();                     // the leave retires the party while we hold B's monitor
                        namedInWindow = PartyOf(b);
                        rosterInWindow = old.Members.Count;
                        try { _ = old.Leader; } catch (Exception e) { leaderRead = e; }
                        int before = bRec.CountOf(ServerOp.SelfProfile);
                        b.Receive(SessionFixture.Frame(ClientOp.ProfileRequest, new byte[] { 0 }));
                        profiles = bRec.CountOf(ServerOp.SelfProfile) - before;
                    });
                    if (!caught) Thread.Yield();
                }
            }) { IsBackground = true, Name = "kicked-member-handler" };
            handler.Start();
            var kick = new Thread(() => { SessionFixture.FormParty(l, b); kickDone.Set(); })
                { IsBackground = true, Name = "leader-kick" };
            kick.Start();
            WaitHandle.WaitAny(new[] { inWindow.WaitHandle, kickDone.WaitHandle }, 15000);
            if (!inWindow.IsSet)
            {
                // The kick got through B's monitor before the handler caught the gap: try again.
                Assert.True(kick.Join(5000) && handler.Join(5000), $"round {round}: the race hung");
                continue;
            }
            landed = true;
            Assert.False(kick.Join(200), "the kick did not park on the kicked member's monitor");

            // The third member leaves through the real Shift+G frame: [L] and a straggler, and the leader's
            // monitor is free (the kick's descending wait dropped it), so TryDisband fires and retires it.
            var leave = new Thread(() => a.Receive(SessionFixture.GroupToggleFrame()))
                { IsBackground = true, Name = "member-leave" };
            leave.Start();
            Assert.True(leave.Join(5000), "the leave hung: the waiting kick never dropped the leader's monitor");
            Assert.Empty(old.Members);
            Assert.Null(PartyOf(l));
            Assert.Equal(1, lRec.MiniTexts(Disbanded));

            release.Set();
            Assert.True(handler.Join(5000) && kick.Join(5000), "the window never closed");

            Assert.Same(old, namedInWindow);    // B's field named the party the roster had already retired
            Assert.Equal(0, rosterInWindow);
            Assert.Null(leaderRead);            // Leader answered instead of throwing...
            Assert.Equal(1, profiles);          // ...so B's own profile request was replied to, not dropped
            Assert.Null(PartyOf(b));            // and the kick's WithState cleared the field once it landed
            Assert.Equal(1, bRec.MiniTexts(Left));
        }
        Assert.True(landed,
            $"the kicked member never caught the window in {round} rounds / {clock.ElapsedMilliseconds} ms");
    }

    /// <summary>The other half of the one-decision rule, pinned on its own (#167 review, F4): an inviter the
    /// roster no longer holds does not get to grow it. <c>Party.Add</c> refuses — the array is down to one (a
    /// disband is on its way) or no longer holds them — and the invite forms a NEW party instead, exactly as
    /// an inviter with no party does. The end state of the racing fact above cannot tell that apart from an
    /// invite that simply got in before the kick, so the precondition is made here instead: the swap
    /// <c>Party.Remove</c> performs on the leader's thread, before the kick's <c>WithState</c> clears the
    /// member's field, which is the window the fact above reaches through the real handlers.</summary>
    [Fact]
    public void AnInviterTheRosterNoLongerHoldsFormsANewPartyInsteadOfGrowingTheOldOne()
    {
        var (l, _, _) = ConcurrentPlayer("RefusedAddLeader");
        var (a, _, _) = ConcurrentPlayer("RefusedAddMember");
        var (m, _, _) = ConcurrentPlayer("RefusedAddThird");
        var (t, _, tc) = ConcurrentPlayer("RefusedAddTarget");   // built last: the invite's nested acquisition
        Assert.True(t.StateRank > a.StateRank);                  // ASCENDS, so A keeps its own monitor

        // Down to ONE member: the disband a removal has decided is on its way, and the array no longer holds A.
        SessionFixture.FormParty(l, a);
        var pending = PartyOf(l)!;
        Assert.True(pending.Remove(a).Removed);
        Assert.Same(pending, PartyOf(a));                        // A still names it, as during a kick's wait
        SessionFixture.FormParty(a, t);
        Assert.Single(pending.Members);
        Assert.Same(l, pending.Members[0]);                      // the leader's roster did not grow
        var formed = PartyOf(a);
        Assert.NotNull(formed);
        Assert.NotSame(pending, formed);
        Assert.Same(formed, PartyOf(t));
        Assert.Equal(new[] { a, t }, formed!.Members);

        // ...and with two members still in it, so no disband pending, purely on "the array no longer holds
        // the inviter": the same refusal, the same new party.
        SetParty(a, null); SetParty(t, null); tc.Grouped = true;
        SessionFixture.FormParty(l, a);
        SessionFixture.FormParty(l, m);
        var live = PartyOf(l)!;
        Assert.Equal(3, live.Members.Count);
        Assert.True(live.Remove(a).Removed);
        Assert.Same(live, PartyOf(a));
        SessionFixture.FormParty(a, t);
        Assert.Equal(new[] { l, m }, live.Members);               // still just the leader and the third member
        var second = PartyOf(a);
        Assert.NotNull(second);
        Assert.NotSame(live, second);
        Assert.Same(second, PartyOf(t));
        Assert.Equal(new[] { a, t }, second!.Members);
    }

    /// <summary>The review's definition of a stranded outcome, read from both ends: a session the roster
    /// still holds whose own <c>_party</c> is null or names another party, and a session whose
    /// <c>_party</c> names a party whose roster does not hold it.</summary>
    private static string? Inconsistency(int round, Party old, params Session[] sessions)
    {
        var parties = new List<Party> { old };
        foreach (var s in sessions)
        {
            var p = PartyOf(s);
            if (p is not null && !parties.Any(q => ReferenceEquals(q, p))) parties.Add(p);
        }
        foreach (var p in parties)
            foreach (var m in p.Members)
                if (!ReferenceEquals(PartyOf(m), p))
                    return $"round {round}: the roster {Roster(p)} holds {m.CharName}, whose _party is "
                         + (PartyOf(m) is null ? "null" : "another party");
        foreach (var s in sessions)
        {
            var p = PartyOf(s);
            if (p is not null && !p.Members.Any(m => ReferenceEquals(m, s)))
                return $"round {round}: {s.CharName}._party is a party whose roster {Roster(p)} does not hold it";
        }
        return null;
    }

    private static string Roster(Party p) => "[" + string.Join(",", p.Members.Select(m => m.CharName)) + "]";

    /// <summary><c>Session._party</c>. Read by reflection because the roster and that field agreeing IS the
    /// invariant these three facts are about, and nothing public exposes it.</summary>
    private static readonly FieldInfo PartyField =
        typeof(Session).GetField("_party", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static Party? PartyOf(Session s) => (Party?)PartyField.GetValue(s);
    private static void SetParty(Session s, Party? p) => PartyField.SetValue(s, p);

    private const string Left = "You have left the group.";

    /// <summary>A session on the fixture's world whose recorder IS thread-safe. <see cref="RecordingOutbound"/>
    /// deliberately is not (#188) and these facts run two real handler threads that both broadcast into the
    /// same recorders. Grouped starts ON, which is what an invite's <c>WantsGroup</c> gate reads.</summary>
    private (Session session, ConcurrentRecorder recorder, Character character) ConcurrentPlayer(string name)
    {
        var character = new Character
        {
            SchemaVersion = Character.CurrentSchemaVersion,
            Id = _fx.World.AllocatePlayerId(),
            Name = name,
            Map = SessionFixture.HomeMap,
            X = 5,
            Y = 10,
            Grouped = true,
        };
        var recorder = new ConcurrentRecorder($"recorder:{name}");
        var session = new Session(recorder, 2005, _fx.Store, _fx.World, character);
        _fx.World.EnterMap(session, SessionFixture.HomeMap);
        recorder.Clear();
        return (session, recorder, character);
    }

    /// <summary>The recording outbound again, with a lock around the list — see above for why.</summary>
    private sealed class ConcurrentRecorder : IOutbound
    {
        private readonly List<byte[]> _frames = new();
        private readonly object _lock = new();

        public ConcurrentRecorder(string remote) => Remote = remote;

        public string Remote { get; }
        public int Capacity => int.MaxValue;
        public int QueueDepth => 0;
        public bool Closed { get; private set; }

        public bool Send(byte[] frame) { lock (_lock) _frames.Add(frame); return true; }
        public void Close() => Closed = true;
        public void Clear() { lock (_lock) _frames.Clear(); }

        /// <summary>How many frames of <paramref name="opcode"/> this session was sent — a packet whose
        /// handler threw is a packet whose reply never went out at all.</summary>
        public int CountOf(byte opcode)
        {
            byte[][] frames;
            lock (_lock) frames = _frames.ToArray();
            return frames.Count(f => TkPacket.TryParse(f, out var pkt, out _) && pkt.Opcode == opcode);
        }

        /// <summary>Every <c>0x0A</c> minitext body carrying <paramref name="needle"/>, decrypted the way
        /// <see cref="RecordingOutbound.BodiesOf"/> does it.</summary>
        public int MiniTexts(string needle)
        {
            byte[][] frames;
            lock (_lock) frames = _frames.ToArray();
            int n = 0;
            foreach (var frame in frames)
            {
                if (!TkPacket.TryParse(frame, out var pkt, out _)) continue;
                if (pkt.Opcode != ServerOp.MiniText) continue;
                var body = TkCrypt.Crypt(pkt.Body, pkt.Increment, TkCrypt.LoginKey);
                if (body.Length < 3) continue;
                if (Encoding.ASCII.GetString(body, 3, body.Length - 3).Contains(needle)) n++;
            }
            return n;
        }
    }

    /// <summary>The invite gate reads <c>WantsGroup</c>, which is the persisted "Join a group" flag; a
    /// fixture character starts with it off, and leaving a party turns it off again.</summary>
    private static void MakeGroupable(Session s)
    {
        if (!s.WantsGroup) s.Receive(SessionFixture.GroupToggleFrame());
    }

    /// <summary>Every <c>0x0A</c> minitext body carrying <paramref name="needle"/>: type(u8) len(u16 BE)
    /// text[len] (Session.SendMiniText).</summary>
    private static int MiniTexts(RecordingOutbound rec, string needle)
    {
        int n = 0;
        foreach (var body in rec.BodiesOf(ServerOp.MiniText))
        {
            if (body.Length < 3) continue;
            if (Encoding.ASCII.GetString(body, 3, body.Length - 3).Contains(needle)) n++;
        }
        return n;
    }
}
