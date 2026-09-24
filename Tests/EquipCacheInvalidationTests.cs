using System.Linq;
using System.Reflection;
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

    /// <summary>Round 1 of the PR #286 review (F1): the fix must stay on the two NPC paths. Swapping one weapon
    /// for another while a weapon enchant is active drops the enchant mid-swap, and that drop pushes stats, which
    /// clamps current HP. With the cache still counting the old weapon at that moment the clamp is a no-op; an
    /// invalidation inside <c>EquipRemove</c> made it clamp against a cap counting neither weapon (200/200 read
    /// 100/250 after the swap). Red if the invalidation moves back into <c>EquipRemove</c>.</summary>
    [Fact]
    public void EnchantedWeaponSwapKeepsCurrentHp()
    {
        var shortsword = Content.Items.First(i => i.Key == "rusty_shortsword");
        var spear = Content.Items.First(i => i.Key == "might_spear");
        Assert.Equal((100, 150), (shortsword.Vita, spear.Vita));
        Assert.Equal(shortsword.EquipSlot, spear.EquipSlot);
        var (session, _, character) = _fx.PlayerWith("eqcache_swap", c =>
        {
            c.Level = 99;
            c.MaxHp = 100;
            c.Hp = 200;
            c.Might = 255;
            if (spear.Sex < 2) c.Sex = spear.Sex;
            c.Equipment.Add(new InvItem { Slot = shortsword.EquipSlot, ItemId = shortsword.Id, Dura = shortsword.Durability });
            c.Inventory.Add(new InvItem { Slot = 0, ItemId = spear.Id, Dura = spear.Durability });
        });
        Assert.Equal((uint)200, session.LuaMaxHp);   // primes the gear sum with the shortsword counted

        var equipFromSlot = typeof(Session).GetMethod("EquipFromSlot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        session.WithState(() =>
        {
            session.LuaSetEnchant(2.0);
            equipFromSlot.Invoke(session, new object[] { 0 });
        });

        Assert.Equal(spear.Id, character.Equipment.Single().ItemId);
        Assert.Equal(((uint)200, (uint)250), (character.Hp, session.LuaMaxHp));
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
