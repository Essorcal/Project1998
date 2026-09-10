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

        var progress = new StallWatch.RoundCounter();
        var inviter = new Thread(() => { SessionFixture.FormParty(leader, target); progress.Bump(); })
            { IsBackground = true, Name = "inviter" };
        inviter.Start();

        // Nothing of the invite may land while its critical section on the target is occupied. Reading the
        // recorder here is safe precisely because the inviter is parked before its first send.
        Assert.False(inviter.Join(500), "the invite completed while the target's monitor was held");
        Assert.Equal(0, MiniTexts(targetRec, Joining));

        release.Set();
        StallWatch.RunUntilDoneOrStalled(new[] { inviter }, () => progress.Rounds,
            StallWatch.StallQuiet, StallWatch.StallCap, "the invite after the target monitor was released");
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
        var progress = new StallWatch.RoundCounter();
        for (int round = 0; round < RaceRounds; round++)
        {
            SetParty(t, null); SetParty(l, null); SetParty(a, null);
            tc.Grouped = true; lc.Grouped = true; ac.Grouped = true;
            SessionFixture.FormParty(l, a);
            var old = PartyOf(l)!;

            // A invites T while the LEADER re-invites A, which is the kick gesture.
            using var gun = new Barrier(2);
            var invite = new Thread(() => { gun.SignalAndWait(); SessionFixture.FormParty(a, t); progress.Bump(); });
            var kick   = new Thread(() => { gun.SignalAndWait(); SessionFixture.FormParty(l, a); progress.Bump(); });
            invite.Start(); kick.Start();
            if (!invite.Join(100) || !kick.Join(100))
                StallWatch.RunUntilDoneOrStalled(new[] { invite, kick }, () => progress.Rounds,
                    StallWatch.StallQuiet, StallWatch.StallCap, $"round {round}: the invite/kick race");

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
        var progress = new StallWatch.RoundCounter();
        for (int round = 0; round < RaceRounds; round++)
        {
            SetParty(t, null); SetParty(l, null); SetParty(a, null);
            tc.Grouped = true; lc.Grouped = true; ac.Grouped = true;
            SessionFixture.FormParty(l, a);
            var old = PartyOf(l)!;

            // The other member leaves through the real Shift+G frame while the survivor invites a third.
            using var gun = new Barrier(2);
            var invite = new Thread(() => { gun.SignalAndWait(); SessionFixture.FormParty(l, t); progress.Bump(); });
            var leave  = new Thread(() => { gun.SignalAndWait(); a.Receive(SessionFixture.GroupToggleFrame()); progress.Bump(); });
            invite.Start(); leave.Start();
            if (!invite.Join(100) || !leave.Join(100))
                StallWatch.RunUntilDoneOrStalled(new[] { invite, leave }, () => progress.Rounds,
                    StallWatch.StallQuiet, StallWatch.StallCap, $"round {round}: the invite/leave race");

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
        var progress = new StallWatch.RoundCounter();
        // The member's own leave, run while the member's monitor is held — the shape of its real Shift+G
        // handler, and of the disconnect teardown whose FlushNow holds that monitor for milliseconds.
        var leave = new Thread(() => m.WithState(() =>
        {
            entered.Set();
            release.Wait();
            m.Receive(SessionFixture.GroupToggleFrame());
            progress.Bump();
        })) { IsBackground = true, Name = "member-leave" };
        leave.Start();
        Assert.True(entered.Wait(5000), "the leave thread never took the member's monitor");

        var kick = new Thread(() => { SessionFixture.FormParty(l, m); progress.Bump(); })
            { IsBackground = true, Name = "leader-kick" };
        kick.Start();
        Assert.False(kick.Join(300), "the kick did not park on the member's monitor");
        release.Set();
        StallWatch.RunUntilDoneOrStalled(new[] { leave, kick }, () => progress.Rounds,
            StallWatch.StallQuiet, StallWatch.StallCap, "the member's own leave and the kick");

        Assert.Equal(1, lRec.MiniTexts(Disbanded));
        Assert.Equal(1, mRec.MiniTexts(Left));
        Assert.Null(PartyOf(l));
        Assert.Null(PartyOf(m));
    }

    // ---- the kick acts on the party read under the MEMBER's monitor (#198) ----------------------------
    // The kick pre-check read `target._party` with no monitor and RemoveFromParty then re-read
    // `member._party` on the leader's thread. Between those two unsynchronised reads the member can leave
    // the leader's group and join a different one, and the removal then acted on THAT group: the member was
    // thrown out of a party the leader was never in, and its last member was told it had disbanded.

    /// <summary>The ordinary, un-raced kick, single-threaded: the outcome the raced facts must not change.
    /// A kick out of a three-person group tells the kicked member they left and nobody that it disbanded;
    /// the kick that takes it down to one tells the straggler once. Same wording as a leave, because RTK's
    /// kick branch is <c>clif_leavegroup(tsd)</c>.</summary>
    [Fact]
    public void ALeadersKickTellsTheKickedMemberOnceAndTheStragglerOnce()
    {
        var (leader, leaderRec, leaderCharacter) = _fx.PlayerWith("KickLeader", c => { });
        var (first, firstRec)  = _fx.Player("KickFirst");
        var (second, secondRec) = _fx.Player("KickSecond");
        MakeGroupable(first);
        MakeGroupable(second);
        SessionFixture.FormParty(leader, first);
        SessionFixture.FormParty(leader, second);
        leaderRec.Clear(); firstRec.Clear(); secondRec.Clear();

        // The leader re-"inviting" someone already in their own group IS the kick gesture.
        SessionFixture.FormParty(leader, first);            // three down to two: nobody disbands
        Assert.Equal(1, MiniTexts(firstRec, Left));
        Assert.Equal(0, MiniTexts(leaderRec, Left) + MiniTexts(secondRec, Left));
        Assert.Equal(0, MiniTexts(leaderRec, Disbanded) + MiniTexts(firstRec, Disbanded)
                      + MiniTexts(secondRec, Disbanded));
        Assert.Null(PartyOf(first));

        SessionFixture.FormParty(leader, second);           // two down to one: the leader is the straggler
        Assert.Equal(1, MiniTexts(secondRec, Left));
        Assert.Equal(1, MiniTexts(leaderRec, Disbanded));
        Assert.Equal(0, MiniTexts(firstRec, Disbanded) + MiniTexts(secondRec, Disbanded));
        Assert.Null(PartyOf(leader));
        Assert.Null(PartyOf(second));
        Assert.False(leaderCharacter.Grouped);              // the disband flipped the last member's status OFF
    }

    /// <summary><c>Session.RemoveFromParty</c>, by reflection: it is private, and which party it acts on IS
    /// the fact. The same reason <see cref="PartyField"/> is read this way.</summary>
    private static readonly MethodInfo RemoveMethod =
        typeof(Session).GetMethod("RemoveFromParty", BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>The removal acts on the party it was GIVEN and never re-reads <c>member._party</c> to find
    /// one — the race above with the timing taken out of it. The kick's decision is taken under the member's
    /// monitor and then handed over; by the time the removal runs the member may have left that party and
    /// joined another, and the removal must be a no-op rather than throwing them out of the new one.
    ///
    /// <para>On <c>8ed52ac</c> this same setup, through the one-argument <c>RemoveFromParty(member)</c> that
    /// re-read the field, removed the member from the second party, told them they had left it and told its
    /// host it had disbanded (see the report for the red).</para></summary>
    [Fact]
    public void TheRemovalActsOnThePartyItWasGivenNotOnAReReadOfTheMembersField()
    {
        var (l, _, _) = ConcurrentPlayer("GivenPartyLeader");
        var (m, mRec, _) = ConcurrentPlayer("GivenPartyMember");
        var (c, cRec, _) = ConcurrentPlayer("GivenPartyHost");

        SessionFixture.FormParty(l, m);                     // A = [L, M]: the party a kick would decide on
        var a = PartyOf(m)!;
        m.Receive(SessionFixture.GroupToggleFrame());       // ...which M then leaves, retiring it...
        MakeGroupable(m);
        SessionFixture.FormParty(c, m);                     // ...before joining B = [C, M], which L is not in
        var b = PartyOf(m)!;
        Assert.NotSame(a, b);
        mRec.Clear(); cRec.Clear();

        RemoveMethod.Invoke(null, new object[] { m, a });

        Assert.Same(b, PartyOf(m));
        Assert.Equal(new[] { c, m }, b.Members);
        Assert.Equal(0, mRec.MiniTexts(Left));
        Assert.Equal(0, cRec.MiniTexts(Disbanded));
    }

    /// <summary>How many rounds the cross-party kick is hunted for. The kick's start is delayed by a random
    /// fraction of one measured leave-and-join each round, because the member's field only reaches the
    /// SECOND party tens of microseconds into the other thread's work: a kick fired at the same instant every
    /// round always arrives long before that and never samples the interesting part of the timeline.</summary>
    private const int CrossPartyRounds = 6000;

    /// <summary>A leader's kick may never take the member out of a group the leader is not in. The member
    /// leaves the leader's group and joins another one on their own thread, under their own monitor, while
    /// the kick is in flight; whichever order the two land in, the second group keeps its member and its
    /// host is never told it disbanded.</summary>
    [Fact]
    public void AKickNeverRemovesTheMemberFromAGroupTheLeaderIsNotIn()
    {
        var (m, mRec, mc) = ConcurrentPlayer("CrossKickMember");   // lowest rank: the kick DESCENDS into M,
        var (l, _, lc) = ConcurrentPlayer("CrossKickLeader");      // so rule 2 drops the leader's own monitor
        var (c, cRec, cc) = ConcurrentPlayer("CrossKickHost");
        Assert.True(l.StateRank > m.StateRank);

        // Calibrate the sweep: one leave-and-join, alone and timed, is the span the kick is scattered over.
        SetParty(l, null); SetParty(m, null); SetParty(c, null);
        lc.Grouped = true; mc.Grouped = true; cc.Grouped = true;
        SessionFixture.FormParty(l, m);
        long at = System.Diagnostics.Stopwatch.GetTimestamp();
        m.Receive(SessionFixture.GroupToggleFrame());
        if (!m.WantsGroup) m.Receive(SessionFixture.GroupToggleFrame());
        SessionFixture.FormParty(c, m);
        long span = System.Diagnostics.Stopwatch.GetTimestamp() - at;

        var rng = new Random(198);
        var wrong = new List<string>();
        var progress = new StallWatch.RoundCounter();
        int reachedSecondGroup = 0;
        for (int round = 0; round < CrossPartyRounds; round++)
        {
            SetParty(l, null); SetParty(m, null); SetParty(c, null);
            lc.Grouped = true; mc.Grouped = true; cc.Grouped = true;
            SessionFixture.FormParty(l, m);                        // A = [L, M]
            mRec.Clear(); cRec.Clear();

            long delay = (long)(rng.NextDouble() * span * 1.5);
            using var gun = new Barrier(2);
            var kick = new Thread(() =>
            {
                gun.SignalAndWait();
                long until = System.Diagnostics.Stopwatch.GetTimestamp() + delay;
                while (System.Diagnostics.Stopwatch.GetTimestamp() < until) Thread.SpinWait(1);
                SessionFixture.FormParty(l, m);                    // the leader's re-invite: the kick gesture
                progress.Bump();
            }) { IsBackground = true, Name = "leader-kick" };
            var move = new Thread(() =>
            {
                gun.SignalAndWait();
                m.Receive(SessionFixture.GroupToggleFrame());      // leave L's group
                if (!m.WantsGroup) m.Receive(SessionFixture.GroupToggleFrame());   // "Join a group" back ON
                SessionFixture.FormParty(c, m);                    // B = [C, M], which L is not in
                progress.Bump();
            }) { IsBackground = true, Name = "member-leave-and-join" };
            kick.Start(); move.Start();
            if (!move.Join(100) || !kick.Join(100))
                StallWatch.RunUntilDoneOrStalled(new[] { move, kick }, () => progress.Rounds,
                    StallWatch.StallQuiet, StallWatch.StallCap, $"round {round}: the cross-party kick race");

            // The member leaves exactly one group this round — L's — so exactly one "left" line, and C is
            // the straggler of no removal at all, so no "disbanded" line. A kick that acted on the party the
            // member had MOVED to shows up as both: a second "left" and C's group retired under it.
            int left = mRec.MiniTexts(Left), disbanded = cRec.MiniTexts(Disbanded);
            if (left > 1 || disbanded > 0)
                    wrong.Add($"round {round} (delay {delay}/{span}): {m.CharName} was told they left {left} "
                        + $"times and "
                        + $"{c.CharName} was told their group disbanded {disbanded} times");
            var second = PartyOf(c);
            if (second is not null && second.Members.Any(x => ReferenceEquals(x, m))) reachedSecondGroup++;
        }

        Assert.True(reachedSecondGroup > 0, "the member never reached a second group in any round");
        Assert.True(wrong.Count == 0,
            $"{wrong.Count} / {CrossPartyRounds} rounds kicked the member out of a group the leader was not "
            + $"in: {string.Join(" | ", wrong.Take(3))}");
    }

    // ---- the roster swap and the member's own field are ONE critical section (#167 review round 2, #198) --
    // The kick is the one removal that runs on a thread that is NOT the member's. It used to swap them out at
    // Party's gate on the LEADER's thread and only then wait for the member's monitor to write their _party,
    // so the roster and the field disagreed for the length of that wait — and in the DESCENDING case the wait
    // had dropped the leader's monitor, so a third member's leave could reach TryDisband and RETIRE the party
    // inside the gap. #198 moved the swap inside the member's own body, which closes that window: the first
    // fact below hunts the window and finds it gone, and the second — in which nobody is kicked, the
    // retired-party state being built at Party's gate — pins the reads that had to survive it while it was
    // open.

    /// <summary>How long the swap window is hunted for. It landed on the first round on <c>8ed52ac</c>; the
    /// counts here buy confidence that it no longer opens at all, and nothing else.</summary>
    private const int WindowRounds = 3000;
    private const int WindowBudgetMs = 30000;

    /// <summary>A kicked member's own handler can never see itself out of the roster while its <c>_party</c>
    /// still names that party: <c>Party.Remove</c> and the <c>member._party = null</c> that follows it are one
    /// critical section on the member (#198), and this hunts for a round in which they are not.</summary>
    [Fact]
    public void AKickedMemberIsNeverSeenOutOfTheRosterWhileStillNamingTheParty()
    {
        var (b, _, bc) = ConcurrentPlayer("SwapWindowMember");   // lowest rank of the three
        var (a, _, ac) = ConcurrentPlayer("SwapWindowLeaver");
        var (l, _, lc) = ConcurrentPlayer("SwapWindowLeader");   // highest: the kick DESCENDS into B, so rule 2
        Assert.True(l.StateRank > b.StateRank && l.StateRank > a.StateRank);   // drops L's own monitor to wait

        int caught = -1;
        int rounds = 0;
        var progress = new StallWatch.RoundCounter();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (; rounds < WindowRounds && Volatile.Read(ref caught) < 0
               && clock.ElapsedMilliseconds < WindowBudgetMs; rounds++)
        {
            SetParty(l, null); SetParty(a, null); SetParty(b, null);
            lc.Grouped = true; ac.Grouped = true; bc.Grouped = true;
            SessionFixture.FormParty(l, a);
            SessionFixture.FormParty(l, b);                      // three, so the kick disbands nothing
            var old = PartyOf(l)!;
            Assert.Equal(3, old.Members.Count);

            int round = rounds;
            using var kickDone = new ManualResetEventSlim(false);
            // B's own handler thread: it takes and drops B's monitor as fast as it can, looking for the
            // instant the kick has swapped B out of the roster but not yet written B._party.
            var handler = new Thread(() =>
            {
                while (Volatile.Read(ref caught) < 0 && !kickDone.IsSet)
                {
                    b.WithState(() =>
                    {
                        if (!ReferenceEquals(PartyOf(b), old) || old.Members.Any(x => ReferenceEquals(x, b))) return;
                        Volatile.Write(ref caught, round);
                    });
                    Thread.Yield();
                }
                progress.Bump();
            }) { IsBackground = true, Name = "kicked-member-handler" };
            handler.Start();
            var kick = new Thread(() => { SessionFixture.FormParty(l, b); kickDone.Set(); progress.Bump(); })
                { IsBackground = true, Name = "leader-kick" };
            kick.Start();
            if (!kick.Join(100) || !handler.Join(100))
                StallWatch.RunUntilDoneOrStalled(new[] { kick, handler }, () => progress.Rounds,
                    StallWatch.StallQuiet, StallWatch.StallCap, $"round {rounds}: the kick/handler race");
        }

        Assert.True(Volatile.Read(ref caught) < 0,
            $"round {caught} of {rounds} ({clock.ElapsedMilliseconds} ms) saw the kicked member out of the "
            + "roster while their _party still named it: the swap and the field write are not one critical "
            + "section on the member");
    }

    /// <summary>What a member whose <c>_party</c> names a RETIRED party reads. <c>Party.Leader</c> answers
    /// null for the empty array instead of indexing <c>Members[0]</c>, which threw
    /// <c>IndexOutOfRangeException</c> out of the member's own 0x2D handler and dropped the reply (#167
    /// review, F3). The state is built at Party's gate rather than hunted through the real handlers, because
    /// #198 closed the kick window that used to produce it (the fact above); the reads it guards are the
    /// same ones. Nobody is kicked here, and nobody can be: the RACED reachability of this state closed with
    /// #198 — the roster swap and the member's field write are one critical section on the member — and
    /// <c>AKickedMemberIsNeverSeenOutOfTheRosterWhileStillNamingTheParty</c> is the fact that hunts that
    /// window; this one keeps the reads themselves pinned.</summary>
    [Fact]
    public void ARetiredPartysReadsSurviveUnderAMembersOwnPacket()
    {
        var (b, bRec, _) = ConcurrentPlayer("RetiredReadMember");
        var (l, _, _) = ConcurrentPlayer("RetiredReadLeader");
        SessionFixture.FormParty(l, b);
        var old = PartyOf(l)!;
        bRec.Clear();

        Assert.True(old.Remove(b).Removed);      // out of the roster...
        Assert.True(old.TryDisband(l));          // ...and the party retired under them
        SetParty(l, null);
        Assert.Empty(old.Members);
        Assert.Same(old, PartyOf(b));            // B's field still names it

        Exception? leaderRead = null;
        try { _ = old.Leader; } catch (Exception e) { leaderRead = e; }
        Assert.Null(leaderRead);                 // Leader answered instead of throwing...

        int before = bRec.CountOf(ServerOp.SelfProfile);
        b.Receive(SessionFixture.Frame(ClientOp.ProfileRequest, new byte[] { 0 }));
        Assert.Equal(1, bRec.CountOf(ServerOp.SelfProfile) - before);   // ...so the reply went out, not dropped
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
