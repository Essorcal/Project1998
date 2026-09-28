using System;
using System.Threading;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// A group kill's quest tally and its exp share are one critical section per member (#298 review, pre-existing
/// 3; the #168 report's follow-up candidate 2).
///
/// <para><b>The gap.</b> <c>AwardKillExp</c> entered each member's monitor twice: once for the tally
/// (<c>TallyKill</c>), once for the payout (<c>AwardExp</c>, which ends in <c>SaveChar</c>). A newer login's
/// <c>ClaimAccountSlot</c> kicks the member it replaces under that member's monitor: the kick writes the row
/// and latches <c>_replaced</c>. A kick landing between the two entries wrote the tally, and the payout's write
/// was then refused by the #168 gate in <c>CaptureAndWrite</c>. The row the new login loaded carried the kill
/// credit without the exp it was credit for.</para>
///
/// <para><b>How the kick is put in the gap.</b> <c>Session.KillExpGapProbeForTest</c> runs on the killer's
/// thread between the member's tally and its payout. It starts the new login on a thread of its own
/// (<c>ClaimAccountSlot</c>, the arrival's real register-and-kick, then the row load) and waits for it. Where
/// the payout shares the tally's critical section, the probe's thread holds the member's monitor, so the kick
/// cannot finish before the payout does: the probe waits only until the arrival is parked, and joining it there
/// would deadlock the test. Where the two are separate entries, nothing holds the member, so the arrival runs to
/// completion inside the gap. That branch decides only when the probe stops waiting; the assertion is on the row
/// the arrival loaded.</para>
/// </summary>
[Collection("world")]
public sealed class KillExpPayoutSectionTests
{
    private readonly SessionFixture _fx;

    public KillExpPayoutSectionTests(SessionFixture fx) => _fx = fx;

    /// <summary>A newer login that kicks a member between its tally and its payout loads a row that has both
    /// or neither. With one section per member it has both: the kick waits for the payout, writes the row, and
    /// the load reads kill credit 1 and exp 8 (the two-member share, ceil(10 x 0.70339)). Both rank orders:
    /// when the member outranks the killer, the killer's thread drops and retakes its own monitor on the way in
    /// (Session.State.cs rule 2).
    ///
    /// <para>Falsified (see the persist-gate report) by splitting the section back into a tally entry and a
    /// payout entry with the probe between them: red on the loaded row, "(1, 0)", the tally kept and the share
    /// refused.</para></summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AKickBetweenAMembersTallyAndPayoutLoadsBothOrNeither(bool memberOutranksKiller)
    {
        string suffix = memberOutranksKiller ? "Desc" : "Asc";
        string memberName = "GapMember" + suffix;
        string mob = "gap_mob_" + suffix.ToLowerInvariant();
        string key = CharacterStore.Key(memberName);

        Session killer, member;
        Character killerCharacter, memberCharacter;
        if (memberOutranksKiller)
        {
            (member, _, memberCharacter) = _fx.PlayerWith(memberName, Grouped);
            (killer, _, killerCharacter) = _fx.PlayerWith("GapKiller" + suffix, Solo);
            Assert.True(member.StateRank < killer.StateRank);
        }
        else
        {
            (killer, _, killerCharacter) = _fx.PlayerWith("GapKiller" + suffix, Solo);
            (member, _, memberCharacter) = _fx.PlayerWith(memberName, Grouped);
            Assert.True(member.StateRank > killer.StateRank);
        }

        var online = _fx.World.Online;
        var relogin = new Session(new RecordingOutbound($"recorder:{memberName}:relogin"), 2005, _fx.Store, _fx.World);
        Thread? arrival = null;
        Exception? arrivalError = null;
        CharacterLoadResult? loaded = null;

        try
        {
            online.Register(key, member, out _);   // the member's own arrival claimed the slot
            SessionFixture.FormParty(killer, member);

            Session.KillExpGapProbeForTest = m =>
            {
                if (!ReferenceEquals(m, member) || arrival is not null) return;
                arrival = new Thread(() => arrivalError = Record.Exception(() =>
                {
                    relogin.WithState(() => relogin.ClaimAccountSlot(memberName));
                    loaded = _fx.Store.Load(memberName);
                })) { IsBackground = true };
                arrival.Start();

                if (member.StateHeld)
                    Assert.True(SpinWait.SpinUntil(() => (arrival.ThreadState & ThreadState.WaitSleepJoin) != 0,
                                                   TimeSpan.FromSeconds(30)), "the arrival never reached the member");
                else Assert.True(arrival.Join(TimeSpan.FromSeconds(30)), "the arrival never finished inside the gap");
            };

            killer.WithState(() => killer.AwardKillExp(10, SessionFixture.HomeMap, 5, 10, mob));

            Assert.NotNull(arrival);
            Assert.True(arrival!.Join(TimeSpan.FromSeconds(30)), "the arrival never finished");
            Assert.Null(arrivalError);
            Assert.True(member.IsReplaced, "the arrival did not kick the member");

            // The row the new login loaded: the tally and the share together.
            Assert.NotNull(loaded);
            Assert.Equal(CharacterLoadStatus.Ok, loaded!.Status);
            var row = Assert.IsType<Character>(loaded.Character);
            Assert.Equal((1, 8u), (row.Kills.GetValueOrDefault(mob), row.Exp));

            // The killer is untouched by all of this: the same share and credit as any two-member kill.
            Assert.Equal(8u, killerCharacter.Exp);
            Assert.Equal(1, killer.KillCount(mob));
            Assert.Equal(8u, memberCharacter.Exp);
        }
        finally
        {
            Session.KillExpGapProbeForTest = null;
            arrival?.Join(TimeSpan.FromSeconds(30));
            online.Unregister(key, relogin);
            online.Unregister(key, member);
        }
    }

    private static void Solo(Character c)
    {
        c.Level = 1;
        c.Mark = 0;
        c.Totem = 4;
    }

    private static void Grouped(Character c)
    {
        Solo(c);
        c.Grouped = true;
    }
}
