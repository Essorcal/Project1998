using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
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
            party.Add(second);

            Session? fromFirst = null, fromSecond = null;
            using var gun = new Barrier(2);
            var a = new Thread(() => { gun.SignalAndWait(); fromFirst = party.Remove(first); });
            var b = new Thread(() => { gun.SignalAndWait(); fromSecond = party.Remove(second); });
            a.Start(); b.Start();
            a.Join(); b.Join();

            int handed = (fromFirst is null ? 0 : 1) + (fromSecond is null ? 0 : 1);
            Assert.Equal(1, handed);
            Assert.Same(leader, fromFirst ?? fromSecond);
            Assert.Single(party.Members);
            Assert.Same(leader, party.Members[0]);
        }
    }

    /// <summary>The invite gate reads <c>WantsGroup</c>, which is the persisted "Join a group" flag; a
    /// fixture character starts with it off.</summary>
    private static void MakeGroupable(Session s) => s.Receive(SessionFixture.GroupToggleFrame());

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
