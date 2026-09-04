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
}
