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

    private static void FormParty(Session leader, Session member)
    {
        byte[] name = Encoding.ASCII.GetBytes(member.CharName);
        byte[] body = new byte[name.Length + 1];
        body[0] = (byte)name.Length;
        name.CopyTo(body, 1);
        leader.Receive(SessionFixture.Frame(0x2E, body));
    }
}
