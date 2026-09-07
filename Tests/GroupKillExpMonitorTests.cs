using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

[Collection("world")]
public sealed class GroupKillExpMonitorTests
{
    private readonly SessionFixture _fx;

    public GroupKillExpMonitorTests(SessionFixture fx) => _fx = fx;

    [Fact]
    public void GroupKillPaysEachMemberUnderTheirOwnStateMonitor()
    {
        var (killer, _, killerCharacter) = _fx.PlayerWith("GroupExpKiller", c =>
        {
            c.Level = 1;
            c.Mark = 0;
            c.Totem = 4;
        });
        var (member, _, memberCharacter) = _fx.PlayerWith("GroupExpMember", c =>
        {
            c.Level = 1;
            c.Mark = 0;
            c.Totem = 4;
            c.Grouped = true;
        });
        FormParty(killer, member);

        var error = Record.Exception(() =>
            killer.WithState(() => killer.AwardKillExp(10, SessionFixture.HomeMap, 5, 10, "monitor_test_mob")));

        Assert.Null(error);
        Assert.Equal((uint)8, killerCharacter.Exp);
        Assert.Equal((uint)8, memberCharacter.Exp);
        Assert.Equal(1, killer.KillCount("monitor_test_mob"));
        Assert.Equal(1, member.KillCount("monitor_test_mob"));
    }

    [Fact]
    public void SoloKillStillPaysTheFullReward()
    {
        var (killer, _, character) = _fx.PlayerWith("SoloExpKiller", c => c.Totem = 4);

        killer.WithState(() =>
            killer.AwardKillExp(10, SessionFixture.HomeMap, 5, 10, "solo_monitor_test_mob"));

        Assert.Equal((uint)10, character.Exp);
        Assert.Equal(1, killer.KillCount("solo_monitor_test_mob"));
    }

    [Fact]
    public void OutOfRangeMemberGetsNeitherExperienceNorKillCredit()
    {
        var (killer, _, killerCharacter) = _fx.PlayerWith("RangeExpKiller", c => c.Totem = 4);
        var (member, _, memberCharacter) = _fx.PlayerWith("RangeExpMember", c =>
        {
            c.Totem = 4;
            c.Grouped = true;
        }, x: 20);
        FormParty(killer, member);

        killer.WithState(() =>
            killer.AwardKillExp(10, SessionFixture.HomeMap, 5, 10, "range_monitor_test_mob"));

        Assert.Equal((uint)10, killerCharacter.Exp);
        Assert.Equal(1, killer.KillCount("range_monitor_test_mob"));
        Assert.Equal((uint)0, memberCharacter.Exp);
        Assert.Equal(0, member.KillCount("range_monitor_test_mob"));
    }

#if DEBUG
    [Fact]
    public void BareMemberAwardUnderAnotherSessionMonitorTripsTheGuard()
    {
        var (caller, _) = _fx.Player("BareAwardCaller");
        var (member, _) = _fx.Player("BareAwardMember");

        var error = Record.Exception(() =>
            caller.WithState(() => member.AwardExp(1, killExp: true, totemTime: false)));

        Assert.NotNull(error);
        Assert.Contains("_char mutated session state outside the state monitor", error!.Message);
    }
#endif

    private static void FormParty(Session leader, Session member)
    {
        byte[] name = Encoding.ASCII.GetBytes(member.CharName);
        byte[] body = new byte[name.Length + 1];
        body[0] = (byte)name.Length;
        name.CopyTo(body, 1);
        leader.Receive(SessionFixture.Frame(0x2E, body));
    }
}
