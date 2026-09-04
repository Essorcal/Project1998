using Server;
using Xunit;

namespace Tests;

public sealed class NamedRecordConstructionTests
{
    [Fact]
    public void MapMetaInfoCarriesEveryRequiredMember()
    {
        var value = new Content.MapMetaInfo
        {
            Region = 1,
            WarpOut = true,
            Pvp = false,
            CanTalk = true,
            CanCast = false,
            ReqLvl = 2,
            ReqPath = 3,
            ReqMark = 4,
            ReqVita = 5,
            ReqMana = 6,
            LvlMax = 7,
            VitaMax = 8,
            ManaMax = 9,
            RejectMsg = "sentinel",
            Indoor = true,
        };

        Assert.Equal(1, value.Region);
        Assert.True(value.WarpOut);
        Assert.False(value.Pvp);
        Assert.True(value.CanTalk);
        Assert.False(value.CanCast);
        Assert.Equal(2, value.ReqLvl);
        Assert.Equal(3, value.ReqPath);
        Assert.Equal(4, value.ReqMark);
        Assert.Equal(5, value.ReqVita);
        Assert.Equal(6, value.ReqMana);
        Assert.Equal(7, value.LvlMax);
        Assert.Equal(8, value.VitaMax);
        Assert.Equal(9, value.ManaMax);
        Assert.Equal("sentinel", value.RejectMsg);
        Assert.True(value.Indoor);
    }

    [Fact]
    public void NpcDefCarriesItsDefaults()
    {
        var value = new NpcDef
        {
            Id = 1,
            Key = "key",
            Name = "name",
            Map = 2,
            X = 3,
            Y = 4,
            Dir = 5,
            Look = 6,
            Color = 7,
            IsChar = true,
            Shop = false,
            Repair = true,
            Bank = false,
            MoveTime = 8,
            ReturnDistance = 9,
        };

        Assert.True(value.Enabled);
        Assert.Equal("", value.EraFeature);
    }

    [Fact]
    public void MobDefCarriesItsDefaults()
    {
        var value = new MobDef
        {
            Id = 1,
            Key = "key",
            Name = "name",
            Look = 2,
            Color = 3,
            Hp = 4,
            Exp = 5,
            Level = 6,
            MoveTime = 7,
        };

        Assert.Equal(0, value.Will);
        Assert.False(value.Aggressive);
        Assert.Equal(1, value.MinDam);
        Assert.Equal(1, value.MaxDam);
        Assert.False(value.IsBoss);
        Assert.Equal(0, value.Protection);
        Assert.Equal(0, value.Hit);
        Assert.Equal(0, value.Ac);
        Assert.Equal(0, value.Grace);
        Assert.False(value.Flees);
        Assert.False(value.Stationary);
        Assert.Equal(Content.DefaultSpawnTimeSec, value.SpawnTime);
    }

    [Fact]
    public void ItemDefCarriesItsDefaults()
    {
        var value = new ItemDef
        {
            Id = 1,
            Key = "key",
            Name = "name",
            Type = 2,
            Icon = 3,
            IconColor = 4,
            Look = 5,
            LookColor = 6,
            Sex = 7,
            Level = 8,
            Durability = 9,
            StackAmount = 10,
            MaxAmount = 11,
            Armor = 12,
            Hit = 13,
            Dam = 14,
            Vita = 15,
            Mana = 16,
            Might = 17,
            Will = 18,
            Grace = 19,
            NoDrop = true,
            Thrown = false,
            BuyPrice = 20,
            SellPrice = 21,
        };

        Assert.Equal(0, value.MightReq);
        Assert.Equal(0, value.Sound);
        Assert.False(value.Indestructible);
        Assert.Equal(0, value.MinSDam);
        Assert.Equal(0, value.MaxSDam);
        Assert.Equal(0, value.MinLDam);
        Assert.Equal(0, value.MaxLDam);
        Assert.Equal(0, value.Protection);
        Assert.Equal(0, value.Healing);
        Assert.Equal(0, value.Wisdom);
        Assert.Equal("", value.Text);
        Assert.Equal("", value.BuyText);
        Assert.Equal(0, value.PathId);
        Assert.Equal(0, value.Mark);
        Assert.False(value.BreakOnDeath);
        Assert.False(value.Protected);
        Assert.True(value.Repairable);
        Assert.False(value.NoTrade);
        Assert.False(value.NoDeposit);
        Assert.Equal(value.Icon, value.ClientIcon);

        var resolved = value with { ClientIcon = 22 };
        Assert.Equal(22, resolved.ClientIcon);
        Assert.Equal(value.Icon, resolved.Icon);
    }
}
