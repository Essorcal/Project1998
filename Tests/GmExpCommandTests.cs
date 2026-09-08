using System.IO;
using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// <c>@exp &lt;n&gt; [kill]</c> — the two forms and what separates them (#155).
///
/// <para>The command exists to exercise the REAL leveling path, so its <c>kill</c> form has to be a real
/// kill's payout and not merely a flag on a quest-style grant. It used to call <c>AwardExp(n, killExp:
/// true)</c> straight, which bought the totem window and nothing else: a grouped caller took the whole grant
/// and the party standing next to them took nothing, so every group-experience rule — the per-head share, the
/// standing scale, the range filter, the group-wide totem window — was unreachable from the command that
/// advertises itself as "as if from a kill". The test client's party layer found it
/// (Essorcal/project1998-testclient#40, 2026-09-07).</para>
///
/// <para>What is pinned here is the SPLIT, not the arithmetic: the numbers below (reward 10 -&gt; 8 each for
/// two level-1 mark-0 members) belong to <see cref="Session.AwardKillExp"/> and are pinned against that
/// method directly in <see cref="GroupKillExpMonitorTests"/>. These cases pin that the command reaches it —
/// and that the three things that must NOT change did not: a solo caller, the bare form, and the range
/// filter.</para>
///
/// <para>Entered as a framed 0x0E chat packet, the way <see cref="CommandTableTests"/> enters: a GM command
/// that skipped the dispatcher would also skip the tier gate and the state monitor around
/// <c>Session.Handle</c>, and the monitor is exactly what a cross-session payout needs.</para>
/// </summary>
[Collection("world")]
public sealed class GmExpCommandTests
{
    private readonly SessionFixture _fx;

    public GmExpCommandTests(SessionFixture fx)
    {
        _fx = fx;
        EnsureGmRoster();
    }

    /// <summary>The bug. Two grouped level-1 mark-0 characters standing on the same tile: <c>@exp 10 kill</c>
    /// on one of them pays BOTH their share, the way a mob kill in that spot would.
    /// <para>Red on the base — the caller took 10 and the member 0.</para></summary>
    [Fact]
    public void GroupedKillGrantSplitsAcrossTheGroup()
    {
        var (caller, outbound, callerCharacter) = GmPlayer();
        var (member, _, memberCharacter) = _fx.PlayerWith("ExpSplitMember", c =>
        {
            c.Level = 1;
            c.Mark = 0;
            c.Totem = 4;
            c.Grouped = true;
        });
        FormParty(caller, member);
        outbound.Clear();

        Run(caller, "@exp 10 kill");

        // One assertion over the pair, not two in sequence: the failure message then carries BOTH halves of
        // the bug (the caller keeping the whole grant AND the member drawing nothing) instead of stopping at
        // the first.
        Assert.Equal(((uint)8, (uint)8), (callerCharacter.Exp, memberCharacter.Exp));
    }

    /// <summary>A GM grant is not the death of any creature, so no quest tally moves for anyone it pays —
    /// <c>ExpCmd</c> passes no <c>mobKey</c>. The whole <c>Kills</c> map is asserted rather than one key,
    /// because a key invented for the grant would be just as wrong as the mob's own.</summary>
    [Fact]
    public void KillGrantGivesNobodyQuestCredit()
    {
        var (caller, outbound, callerCharacter) = GmPlayer();
        var (member, _, memberCharacter) = _fx.PlayerWith("ExpCreditMember", c =>
        {
            c.Level = 1;
            c.Mark = 0;
            c.Totem = 4;
            c.Grouped = true;
        });
        FormParty(caller, member);
        outbound.Clear();

        Run(caller, "@exp 10 kill");

        Assert.Empty(callerCharacter.Kills);
        Assert.Empty(memberCharacter.Kills);
    }

    /// <summary>Negative control: ungrouped, the kill form still pays the caller the whole grant.</summary>
    [Fact]
    public void SoloKillGrantStillPaysTheWholeAmount()
    {
        var (caller, _, callerCharacter) = GmPlayer();

        Run(caller, "@exp 10 kill");

        Assert.Equal((uint)10, callerCharacter.Exp);
    }

    /// <summary>Negative control: WITHOUT <c>kill</c> the grant is quest-style — the caller's own, never
    /// split, even inside a group. Byte-for-byte the old behaviour of both forms.</summary>
    [Fact]
    public void BareGrantPaysOnlyTheCallerEvenInAGroup()
    {
        var (caller, outbound, callerCharacter) = GmPlayer();
        var (member, _, memberCharacter) = _fx.PlayerWith("ExpBareMember", c =>
        {
            c.Level = 1;
            c.Mark = 0;
            c.Totem = 4;
            c.Grouped = true;
        });
        FormParty(caller, member);
        outbound.Clear();

        Run(caller, "@exp 10");

        Assert.Equal((uint)10, callerCharacter.Exp);
        Assert.Equal((uint)0, memberCharacter.Exp);
    }

    /// <summary>Negative control: the corpse the command reports is the CALLER's tile, so the range filter
    /// measures from there. A member 15 tiles away is outside <c>GroupExpRange</c> (12) and draws nothing,
    /// which also leaves the caller solo and therefore paid in full.</summary>
    [Fact]
    public void OutOfRangeMemberDrawsNothingFromAKillGrant()
    {
        var (caller, outbound, callerCharacter) = GmPlayer();
        var (member, _, memberCharacter) = _fx.PlayerWith("ExpRangeMember", c =>
        {
            c.Level = 1;
            c.Mark = 0;
            c.Totem = 4;
            c.Grouped = true;
        }, x: 20);
        FormParty(caller, member);
        outbound.Clear();

        Run(caller, "@exp 10 kill");

        Assert.Equal((uint)10, callerCharacter.Exp);
        Assert.Equal((uint)0, memberCharacter.Exp);
    }

    // ---- fixture -------------------------------------------------------------------------------------

    /// <summary>The GM the commands run as. Deliberately the SAME name <see cref="CommandTableTests"/>
    /// uses: both classes write the roster file, and writing identical content makes the order they run in
    /// irrelevant. A second name would be lost the first time the other class rewrote the file.</summary>
    private const string GmName = "cmdgm";

    private static bool _rosterWritten;

    private static void EnsureGmRoster()
    {
        lock (TestProcessState.Gate)
        {
            if (_rosterWritten) return;
            Directory.CreateDirectory(TestProcessState.StateDirectory);
            File.WriteAllText(Path.Combine(TestProcessState.StateDirectory, "gm_accounts.txt"), GmName + "\n");
            StaffAccounts.Load();
            _rosterWritten = true;
        }
    }

    /// <summary>A fresh level-1, mark-0, totem-less GM with the world-entry chatter dropped. Level and mark
    /// are pinned because the share is scaled by standing against the group's highest, and the totem because
    /// a live window would add 5% to whatever the case expects.</summary>
    private (Session session, RecordingOutbound outbound, Character character) GmPlayer()
    {
        var (session, outbound, character) = _fx.PlayerWith(GmName, c =>
        {
            c.Level = 1;
            c.Mark = 0;
            c.Totem = 4;
        });
        outbound.Clear();
        return (session, outbound, character);
    }

    /// <summary>The real 0x2E group-join frame, sent by the leader and naming the member.</summary>
    private static void FormParty(Session leader, Session member)
    {
        byte[] name = Encoding.ASCII.GetBytes(member.CharName);
        byte[] body = new byte[name.Length + 1];
        body[0] = (byte)name.Length;
        name.CopyTo(body, 1);
        leader.Receive(SessionFixture.Frame(0x2E, body));
    }

    /// <summary>A command the way the read loop delivers one: a framed 0x0E chat packet, type 0 (ordinary
    /// speech). Same entry point as <c>CommandTableTests.Run</c>, and for the same reason — the prefix
    /// split, the tier gate and the state monitor around <c>Session.Handle</c> are all part of what is
    /// under test.</summary>
    private static void Run(Session session, string command)
    {
        var text = Encoding.ASCII.GetBytes(command);
        var body = new byte[2 + text.Length];
        body[0] = 0;
        body[1] = (byte)text.Length;
        text.CopyTo(body, 2);
        session.Receive(SessionFixture.Frame(0x0E, body));
    }
}
