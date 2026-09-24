using System.IO;
using System.Linq;
using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// <c>@clearinv</c> takes the worn gear's bonuses with it (#206).
///
/// <para>The gear sum is cached (<c>Session._equipTotals</c>) and every equipment mutation site has to drop the
/// cache. Equip, unequip and break did; the bulk clear behind <c>@clearinv</c> (<c>EquipClear</c>, whose only
/// caller is the command) did not, so a character stripped of Cimmerian steel kept its +1000 vita and +8 might
/// until something else happened to invalidate the cache. The test client's hunt suite hit it as HP 1006/1006
/// with an empty equipment list (Essorcal/project1998-testclient#55).</para>
///
/// <para>Entered as a framed 0x0E chat packet, the way <see cref="GmExpCommandTests"/> enters, so the tier gate
/// and the state monitor around <c>Session.Handle</c> are part of what runs.</para>
/// </summary>
[Collection("world")]
public sealed class ClearInventoryTests
{
    private readonly SessionFixture _fx;

    public ClearInventoryTests(SessionFixture fx)
    {
        _fx = fx;
        EnsureGmRoster();
    }

    /// <summary>The bug. With the gear sum primed, <c>@clearinv</c> leaves the effective max HP and might at the
    /// bare character's. Red on the base: (1006, 11), the Cimmerian steel's +1000 vita and +8 might still
    /// counted with nothing worn.</summary>
    [Fact]
    public void ClearingGearDropsItsBonusesAtOnce()
    {
        var (gm, _, character) = SteelWearingGm();

        Run(gm, "@clearinv");

        Assert.Empty(character.Equipment);
        Assert.Equal(((uint)6, 3), (gm.LuaMaxHp, gm.LuaMight));
    }

    /// <summary>Current HP comes down with the cap straight away, the way an ordinary unequip brings it down:
    /// the command pushes stats, and <c>SendStats</c> clamps HP to the new cap. Red without that push: HP
    /// stays at 1006 until the next stats update.</summary>
    [Fact]
    public void ClearingGearCapsCurrentHpAtOnce()
    {
        var (gm, _, character) = SteelWearingGm();

        Run(gm, "@clearinv");

        Assert.Equal((uint)6, character.Hp);
    }

    /// <summary>The issue's own repro: <c>@clearinv</c> then <c>@hp 6</c> reports 6/6. <c>@hp</c> refills to
    /// the effective cap, so on the base this refilled to 1006.</summary>
    [Fact]
    public void SettingMaxHpAfterAClearUsesTheBareCap()
    {
        var (gm, _, character) = SteelWearingGm();

        Run(gm, "@clearinv");
        Run(gm, "@hp 6");

        Assert.Equal(((uint)6, (uint)6), (character.Hp, gm.LuaMaxHp));
    }

    // ---- fixture -------------------------------------------------------------------------------------

    /// <summary>The GM name <see cref="GmExpCommandTests"/> and <see cref="CommandTableTests"/> use: all three
    /// write the same roster file, and identical content makes the order they run in irrelevant.</summary>
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

    /// <summary>A level-1 GM on base max HP 6 and might 3, wearing Cimmerian steel (+1000 vita, +8 might), with
    /// the gear sum PRIMED: the precondition reads the effective stats once, which fills the cache, and pins
    /// that the steel's bonuses were really counted before the clear.</summary>
    private (Session session, RecordingOutbound outbound, Character character) SteelWearingGm()
    {
        var steel = Content.Items.First(i => i.Key == "cimmerian_steel");
        Assert.Equal((1000, 8), (steel.Vita, steel.Might));
        var (session, outbound, character) = _fx.PlayerWith(GmName, c =>
        {
            c.Level = 1;
            c.Mark = 0;
            c.MaxHp = 6;
            c.Hp = 1006;
            c.Might = 3;
            c.Equipment.Add(new InvItem { Slot = 0, ItemId = steel.Id, Dura = steel.Durability });
        });
        Assert.Equal(((uint)1006, 11), (session.LuaMaxHp, session.LuaMight));
        outbound.Clear();
        return (session, outbound, character);
    }

    /// <summary>A command the way the read loop delivers one: a framed 0x0E chat packet, type 0.</summary>
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
