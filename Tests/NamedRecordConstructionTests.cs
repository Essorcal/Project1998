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
}
