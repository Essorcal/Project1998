using System.Linq;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Gear taken off by an NPC takes its bonuses with it (F2 of the PR #281 review).
///
/// <para>The worn-gear sum is cached (<c>Session._equipTotals</c>). PR #281 made <c>EquipClear</c> drop the
/// cache, but <c>EquipAdd</c> and <c>EquipRemove</c> still left it to their callers, and two callers never did:
/// the gender-change NPC's strip (<c>StripAllEquipment</c>) and the armor-quest guildmaster's turn-in
/// (<c>TakeReady</c>, when the tribute is worn). A character stripped of Cimmerian steel either way kept its
/// +1000 vita and +8 might, with nothing worn, until the next equip change or relog.</para>
///
/// <para>Both entry points are called under the state monitor, the way the NPC dialog reaches them.</para>
/// </summary>
[Collection("world")]
public sealed class EquipCacheInvalidationTests
{
    private readonly SessionFixture _fx;

    public EquipCacheInvalidationTests(SessionFixture fx) => _fx = fx;

    /// <summary>The gender-change strip. Red on the base: (1006, 11), the steel's bonuses still counted with
    /// the steel in the bag.</summary>
    [Fact]
    public void StrippingAllGearDropsItsBonusesAtOnce()
    {
        var (session, character) = SteelWearer("eqcache_strip");

        Assert.True(session.WithState(() => session.StripAllEquipment()));

        Assert.Empty(character.Equipment);
        Assert.Equal(((uint)6, 3), (session.LuaMaxHp, session.LuaMight));
    }

    /// <summary>The armor-quest turn-in taking the tribute off the player's back. Red on the base: (1006, 11).</summary>
    [Fact]
    public void TakingWornTributeDropsItsBonusesAtOnce()
    {
        var (session, character) = SteelWearer("eqcache_take");

        Assert.True(session.WithState(() => session.TakeReady("cimmerian_steel", 1)));

        Assert.Empty(character.Equipment);
        Assert.Equal(((uint)6, 3), (session.LuaMaxHp, session.LuaMight));
    }

    /// <summary>Both paths already push stats, and <c>SendStats</c> clamps current HP to the effective cap. With
    /// the cap now correct, current HP comes down with it at once, as an ordinary unequip brings it down. Red on
    /// the base: HP stays at 1006.</summary>
    [Fact]
    public void BothPathsCapCurrentHpAtOnce()
    {
        var (strip, stripChar) = SteelWearer("eqcache_striphp");
        strip.WithState(() => strip.StripAllEquipment());

        var (take, takeChar) = SteelWearer("eqcache_takehp");
        take.WithState(() => take.TakeReady("cimmerian_steel", 1));

        Assert.Equal(((uint)6, (uint)6), (stripChar.Hp, takeChar.Hp));
    }

    // ---- fixture -------------------------------------------------------------------------------------

    /// <summary>A level-1 character on base max HP 6 and might 3, wearing Cimmerian steel (+1000 vita, +8
    /// might) at full durability, with the gear sum PRIMED: the precondition reads the effective stats once,
    /// which fills the cache, and pins that the steel's bonuses were really counted before it came off.</summary>
    private (Session session, Character character) SteelWearer(string name)
    {
        var steel = Content.Items.First(i => i.Key == "cimmerian_steel");
        Assert.Equal((1000, 8), (steel.Vita, steel.Might));
        var (session, _, character) = _fx.PlayerWith(name, c =>
        {
            c.Level = 1;
            c.Mark = 0;
            c.MaxHp = 6;
            c.Hp = 1006;
            c.Might = 3;
            c.Equipment.Add(new InvItem { Slot = 0, ItemId = steel.Id, Dura = steel.Durability });
        });
        Assert.Equal(((uint)1006, 11), (session.LuaMaxHp, session.LuaMight));
        return (session, character);
    }
}
