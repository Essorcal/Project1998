using Server;
using Xunit;

namespace Tests;

/// <summary>
/// RTK's SAMEAREA box (<see cref="World.ShiftBox"/>), the range that decides who hears a sound and who sees a
/// speech bubble.
///
/// Worth pinning because it is a silent failure in both directions and neither shows up in a build: too wide
/// and a swing on one side of a map is audible on the other (the bug this was written for — every sfx used to
/// go out map-wide), too narrow and someone standing next to you goes quiet. The edge SHIFT is the part that
/// invites a "simplify this" rewrite into a plain clamp, which would silently cut the range in half for anyone
/// stood against a wall — so the near-edge cases below are the whole point.
///
/// Ground truth is <c>rtk/src/map/map.c</c>'s map_foreachinarea, which computes x0/y0/x1/y1 once and hands them
/// to the SAMEAREA case. Bounds are inclusive at both ends (map_foreachinblockva tests <c>&gt;= x0 &amp;&amp;
/// &lt;= x1</c>).
/// </summary>
public class AreaBoxTests
{
    // The sound/say box, on a map big enough that no edge is in play.
    private const int Xs = 200, Ys = 200;

    [Fact]
    public void CentredWhenClearOfEveryEdge()
    {
        var (x0, y0, x1, y1) = World.ShiftBox(100, 100, World.SoundHalfW, World.SoundHalfH, Xs, Ys);
        Assert.Equal((91, 92, 109, 108), (x0, y0, x1, y1));   // x±9, y±8
    }

    [Fact]
    public void BoxKeepsItsFullSizeAgainstTheWestWall()
    {
        // RTK does NOT clip here — x0 is pushed to 0 and x1 gains what x0 lost, so the box stays 19 wide and
        // you hear twice as far east as you otherwise would. y is untouched: the shift is per axis.
        var (x0, y0, x1, y1) = World.ShiftBox(0, 100, World.SoundHalfW, World.SoundHalfH, Xs, Ys);
        Assert.Equal((0, 92, 18, 108), (x0, y0, x1, y1));
        Assert.Equal(19, x1 - x0 + 1);
    }

    [Fact]
    public void BoxKeepsItsFullSizeAgainstTheEastWall()
    {
        var (x0, y0, x1, y1) = World.ShiftBox(Xs - 1, 100, World.SoundHalfW, World.SoundHalfH, Xs, Ys);
        Assert.Equal((Xs - 19, 92, Xs - 1, 108), (x0, y0, x1, y1));
        Assert.Equal(19, x1 - x0 + 1);
    }

    [Fact]
    public void BothAxesShiftIndependentlyInACorner()
    {
        var (x0, y0, x1, y1) = World.ShiftBox(0, 0, World.SoundHalfW, World.SoundHalfH, Xs, Ys);
        Assert.Equal((0, 0, 18, 16), (x0, y0, x1, y1));   // 19 wide x 17 tall, both anchored at the corner
    }

    [Fact]
    public void AMapSmallerThanTheBoxCollapsesToTheWholeMap()
    {
        // Both shifts fire and cancel, exactly as RTK's do. A 10x10 closet has no "out of earshot".
        var (x0, y0, x1, y1) = World.ShiftBox(5, 5, World.SoundHalfW, World.SoundHalfH, 10, 10);
        Assert.Equal((0, 0, 9, 9), (x0, y0, x1, y1));
    }

    [Theory]
    // Straddling the boundary at map centre: ±9 / ±8 inclusive, one tile further is out.
    [InlineData(109, 100, true)]
    [InlineData(110, 100, false)]
    [InlineData(91, 100, true)]
    [InlineData(90, 100, false)]
    [InlineData(100, 108, true)]
    [InlineData(100, 109, false)]
    public void InclusiveAtBothEnds(int px, int py, bool inside)
    {
        var (x0, y0, x1, y1) = World.ShiftBox(100, 100, World.SoundHalfW, World.SoundHalfH, Xs, Ys);
        Assert.Equal(inside, px >= x0 && px <= x1 && py >= y0 && py <= y1);
    }

    [Fact]
    public void TheFxBoxIsTheLooserOne()
    {
        // 0x29 graphics ride RTK's AREA (AREAX_SIZE+1 / AREAY_SIZE+1) rather than SAMEAREA, and it has to stay
        // wider than the client's 19x17 drawn rect or an effect could be cut from someone who can see it.
        Assert.True(World.FxHalfW > World.SoundHalfW && World.FxHalfH > World.SoundHalfH);
        Assert.Equal((19, 17), (World.FxHalfW, World.FxHalfH));
    }
}
