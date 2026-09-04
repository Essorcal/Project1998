using System.Collections.Generic;
using System.Linq;
using Server;
using Xunit;

namespace Tests;

/// <summary>
/// Where a batch spawn group is allowed to put a creature (<see cref="World.SpawnDirector.PlacementBox"/>,
/// <see cref="World.SpawnDirector.Placeable"/>, <see cref="World.SpawnDirector.OpenTiles"/>) — and, through
/// them, the guarantee that a group's cap is a cap rather than an average.
///
/// <para><b>The bug these were written for.</b> RTK rolls a random tile at a time and gives up after
/// <c>maxMobs[z] * 4</c> failures (mobSpawnHandler.lua:3003). Ported literally, that is four rolls for a
/// cap-1 creature — and every <c>handleSpawn</c> row extracts to a ZERO box, so the roll is uniform over the
/// whole map. Sute's Nest (map 442, 30x30) is 52% walkable ground, so <b>Sute</b> — cap 1, the one creature
/// in the room that is a quest object rather than a population — was absent from a measured 6.9% of refills,
/// for the full 300s until the group's clock next came round. Yachi (cap 15, 60 rolls) and Seki (cap 10, 40
/// rolls) never came up short, so the room looked fully repopulated with the boss simply missing.</para>
///
/// <para>The fix is <see cref="World.SpawnDirector.OpenTiles"/>: the roll stays as the cheap common path, and
/// a member the rolls leave short falls back to enumerating the box. So the interesting case here is the LAST
/// free tile — the one a random roll finds with probability 1/900 and this has to find every time.</para>
///
/// <para>Real content, not a fixture: these run against the shipped <c>game-data</c> and the client's own
/// <c>TK442.map</c>, because a fixture would prove the fixture walkable.</para>
/// </summary>
public class SpawnPlacementTests
{
    private static readonly object _gate = new();
    private static bool _loaded;

    private static void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            TestProcessState.LoadContent();
            _loaded = true;
        }
    }

    private const ushort Nest = 442;          // Sute's Nest
    private const int NestXs = 30, NestYs = 30;
    // Ground the client's map file says is walkable (pass flag 0), minus the one warp tile on it — the way
    // back up to Dark Pools at (2,25), which a creature must not be parked on.
    private const int NestWalkable = 468;
    private const int NestOpen = NestWalkable - 1;

    private static HashSet<(int, int)> Nothing() => new();

    [Fact]
    public void AZeroBoxMeansTheWholeMap()
    {
        EnsureLoaded();
        // Not a special case we invented: RTK's spawner takes no box at all, so every row the extractor
        // wrote carries 0,0,0,0 and the map IS the box.
        Assert.Equal((0, 0, NestXs - 1, NestYs - 1), World.SpawnDirector.PlacementBox(Nest, 0, 0, 0, 0));
    }

    [Fact]
    public void ABoxIsClampedToTheMapItIsOn()
    {
        EnsureLoaded();
        Assert.Equal((4, 4, NestXs - 1, NestYs - 1), World.SpawnDirector.PlacementBox(Nest, 4, 4, 999, 999));
        Assert.Equal((4, 4, 9, 9), World.SpawnDirector.PlacementBox(Nest, 4, 4, 9, 9));
    }

    [Fact]
    public void AMapWithNoDimensionsOffersNothingRatherThanThrowing()
    {
        EnsureLoaded();
        var (minX, minY, maxX, maxY) = World.SpawnDirector.PlacementBox(0xFFFF, 0, 0, 0, 0);
        Assert.True(maxX < minX && maxY < minY);
        Assert.Empty(World.SpawnDirector.OpenTiles(0xFFFF, Nothing(), 0, 0, 0, 0));
    }

    [Fact]
    public void TheNestOffersEveryWalkableTileExceptItsWarp()
    {
        EnsureLoaded();
        var open = World.SpawnDirector.OpenTiles(Nest, Nothing(), 0, 0, 0, 0);

        Assert.Equal(NestOpen, open.Count);
        Assert.DoesNotContain(((ushort)2, (ushort)25), open);      // the way out to Dark Pools
        Assert.Equal(open.Count, open.Distinct().Count());
        Assert.All(open, t => Assert.True(World.SpawnDirector.Placeable(Nest, Nothing(), t.X, t.Y)));
    }

    [Fact]
    public void AWallIsNotPlaceable()
    {
        EnsureLoaded();
        // (0,0) is the rock border every one of these caves is cut out of.
        Assert.False(World.SpawnDirector.Placeable(Nest, Nothing(), 0, 0));
        Assert.False(World.SpawnDirector.Placeable(Nest, Nothing(), -1, 5));         // off the west edge
        Assert.False(World.SpawnDirector.Placeable(Nest, Nothing(), NestXs, 5));     // off the east edge
    }

    [Fact]
    public void ATileSomethingIsStandingOnIsNotPlaceable()
    {
        EnsureLoaded();
        var entrance = ((int)2, (int)26);                              // where you arrive from Dark Pools
        Assert.True(World.SpawnDirector.Placeable(Nest, Nothing(), entrance.Item1, entrance.Item2));
        Assert.False(World.SpawnDirector.Placeable(Nest, new HashSet<(int, int)> { entrance }, entrance.Item1, entrance.Item2));
    }

    /// <summary>The guarantee. One tile left in the whole nest, and it is found — this is the case a
    /// four-roll budget hits with probability 1-in-900 per try and misses essentially always.</summary>
    [Fact]
    public void TheLastFreeTileIsAlwaysFound()
    {
        EnsureLoaded();
        var all = World.SpawnDirector.OpenTiles(Nest, Nothing(), 0, 0, 0, 0);
        Assert.Equal(NestOpen, all.Count);

        foreach (var last in new[] { all[0], all[all.Count / 2], all[^1] })
        {
            var taken = new HashSet<(int, int)>(all.Where(t => t != last).Select(t => ((int)t.X, (int)t.Y)));
            var open = World.SpawnDirector.OpenTiles(Nest, taken, 0, 0, 0, 0);
            Assert.Single(open);
            Assert.Equal(last, open[0]);
        }
    }

    /// <summary>…and the other end of it: the fallback is bounded by the room, so a genuinely full box
    /// places nothing rather than stacking creatures or spinning.</summary>
    [Fact]
    public void AFullRoomOffersNothing()
    {
        EnsureLoaded();
        var taken = new HashSet<(int, int)>(
            World.SpawnDirector.OpenTiles(Nest, Nothing(), 0, 0, 0, 0).Select(t => ((int)t.X, (int)t.Y)));
        Assert.Empty(World.SpawnDirector.OpenTiles(Nest, taken, 0, 0, 0, 0));
    }

    /// <summary>The row this whole file exists for, pinned so a re-run of the spawn extractor can't quietly
    /// change its shape: one Sute, no box, on the same 300s group clock as the rest of his nest.</summary>
    [Fact]
    public void SutesRowIsTheShapeThatUsedToLoseHim()
    {
        EnsureLoaded();
        var sute = Content.MobByKey("sute");
        Assert.NotNull(sute);

        var row = Content.AreaSpawns.Single(a => a.Map == Nest && a.MobId == sute!.Id);
        Assert.Equal(1, row.Count);                                   // a cap of one — 4 placement rolls
        Assert.Equal((0, 0, 0, 0),                                    // …over the whole 30x30 room
                     ((int)row.MinX, (int)row.MinY, (int)row.MaxX, (int)row.MaxY));
        Assert.Equal(300, row.Timer);                                 // …and 300s before another chance

        // He shares the room's group with the two populations that always filled, which is why the room
        // read as "everything spawned except him".
        Assert.Equal(new[] { "yachi", "seki", "sute" },
                     Content.AreaSpawns.Where(a => a.Map == Nest)
                            .Select(a => Content.MobById(a.MobId)!.Key).ToArray());
    }
}
