using System.Collections.Generic;
using System.Linq;
using Server;
using Xunit;

namespace Tests;

/// <summary>
/// <c>MapData.FreeNeighbour</c> and the two walks that feed it (#57 finding 31) — the loop
/// <c>Session.DismountTile</c> and <c>World.FreeSpawnTile</c> now share.
///
/// <para><b>What is actually at risk here.</b> Not "does it find a free tile": both callers found one before.
/// The risk in folding two hand-written loops into one is that the ORDER or the FALLBACK quietly becomes the
/// other caller's. Order is observable in both: a horse appears on a particular tile beside the rider, and a
/// respawn lands on a particular tile near its spawn point, and a player watching either would see a change
/// that no "it still works" assertion catches. So every fact below pins a SEQUENCE, not a single answer — the
/// walks are asserted tile by tile in full, and the search is asserted to take the earliest survivor rather
/// than merely some survivor.</para>
///
/// <para><b>Synthetic predicates, deliberately.</b> The blocked and occupancy tests are the callers' own and
/// stay at the call sites; what moved is the walk and the bounds. Driving those with plain lambdas over a
/// small hand-drawn map pins the geometry exactly, where a real <c>.map</c> fixture would mostly be asserting
/// that the fixture is walkable. The two callers' own predicates keep their existing coverage
/// (<c>SpawnPlacementTests</c> for the spawn side).</para>
/// </summary>
public class FreeNeighbourTests
{
    // 0=N 1=E 2=S 3=W.
    private const int N = 0, E = 1, S = 2, W = 3;

    private static bool NeverBlocked(int x, int y, int side) => false;
    private static bool NeverOccupied(int x, int y) => false;

    private static List<(int x, int y, int side)> Cardinal(int x, int y, int facing) =>
        MapData.CardinalWalk(x, y, facing).ToList();

    [Fact]
    public void TheCardinalWalkIsTheFacedTileThenClockwise()
    {
        // Facing north from (5,5): north, then east, then south, then west — and each tile carries the side
        // it was reached by, which is what BlockedMove reads and what the dismount inverts for the horse's
        // facing. Pinned in full: a rotation or a reversal here moves every dismount by a tile.
        Assert.Equal(new[] { (5, 4, N), (6, 5, E), (5, 6, S), (4, 5, W) }, Cardinal(5, 5, N));

        // Facing west, the same ring starting a quarter turn later — clockwise throughout, never reversing.
        Assert.Equal(new[] { (4, 5, W), (5, 4, N), (6, 5, E), (5, 6, S) }, Cardinal(5, 5, W));

        // Four tiles, no diagonals, and the origin is never a candidate — the rider's own tile is the
        // fallback, not a step in the walk.
        foreach (int facing in new[] { N, E, S, W })
        {
            Assert.Equal(4, Cardinal(5, 5, facing).Count);
            Assert.DoesNotContain((5, 5, facing), Cardinal(5, 5, facing));
        }

        // A facing outside 0-3 masks, exactly as the inline `(_facing + i) & 3` did.
        Assert.Equal(Cardinal(5, 5, N), Cardinal(5, 5, 4));
    }

    [Fact]
    public void TheRingWalkIsTheTileItselfThenRadiusOneThenRadiusTwo()
    {
        var walk = MapData.SelfThenRingWalk(10, 10, maxRadius: 2).ToList();

        // 1 + 8 + 16: the tile, the 3x3 ring around it, the 5x5 ring around that. No tile twice.
        Assert.Equal(25, walk.Count);
        Assert.Equal(25, walk.Distinct().Count());
        Assert.Equal((10, 10, -1), walk[0]);

        // Radius 1 in full, in the column-major order the inline loops produced (dx outer, dy inner). Which
        // of eight equally-near tiles a respawn takes is observable, so the order is the fact.
        Assert.Equal(
            new[] { (9, 9, -1), (9, 10, -1), (9, 11, -1), (10, 9, -1), (10, 11, -1),
                    (11, 9, -1), (11, 10, -1), (11, 11, -1) },
            walk.Skip(1).Take(8));

        // Every remaining tile is on the radius-2 ring — the ring, not the filled square.
        foreach (var (x, y, _) in walk.Skip(9))
            Assert.Equal(2, System.Math.Max(System.Math.Abs(x - 10), System.Math.Abs(y - 10)));

        // A zero radius is the tile alone: the spawn fallback's degenerate case is "the spawn tile or nothing".
        Assert.Equal(new[] { (4, 4, -1) }, MapData.SelfThenRingWalk(4, 4, maxRadius: 0));
    }

    [Fact]
    public void TheSearchTakesTheEarliestSurvivorAndTestsBoundsBlockedOccupiedInThatOrder()
    {
        // Facing north with the north tile blocked and the east tile occupied: the answer is SOUTH, the
        // third step, not the merely-free west one.
        var got = MapData.FreeNeighbour(
            MapData.CardinalWalk(5, 5, N), xs: 20, ys: 20,
            blocked: (x, y, _) => (x, y) == (5, 4),
            occupied: (x, y) => (x, y) == (6, 5));
        Assert.Equal((5, 6, S), got);

        // The side reaches the blocked predicate, so a directional object wall can reject one approach and
        // pass another — the reason the walk carries a side at all.
        var directional = MapData.FreeNeighbour(
            MapData.CardinalWalk(5, 5, N), xs: 20, ys: 20,
            blocked: (x, y, side) => side == N, occupied: NeverOccupied);
        Assert.Equal((6, 5, E), directional);

        // Order of the three tests: a tile out of bounds must never reach the predicates. Standing at (0,0)
        // facing north, the first two candidates are off the map to the north and (on a 1-wide map) to the
        // east; a predicate that throws proves neither was handed to it.
        var seen = new List<(int, int)>();
        var bounded = MapData.FreeNeighbour(
            MapData.CardinalWalk(0, 0, N), xs: 1, ys: 3,
            blocked: (x, y, _) => { seen.Add((x, y)); return false; },
            occupied: NeverOccupied);
        Assert.Equal((0, 1, S), bounded);
        Assert.Equal(new[] { (0, 1) }, seen);   // (0,-1) and (1,0) were rejected on bounds alone

        // Blocked short-circuits occupied, so a caller whose occupancy test is the expensive one (the
        // dismount's is a world-lock peer lookup) pays it only for tiles the terrain already allowed.
        var occupancyProbes = new List<(int, int)>();
        MapData.FreeNeighbour(
            MapData.CardinalWalk(5, 5, N), xs: 20, ys: 20,
            blocked: (x, y, _) => (x, y) == (5, 4),
            occupied: (x, y) => { occupancyProbes.Add((x, y)); return false; });
        Assert.DoesNotContain((5, 4), occupancyProbes);
    }

    [Fact]
    public void TheSearchReturnsNullWhenTheWalkRunsOutSoTheCallerOwnsTheFallback()
    {
        // Boxed in on all four sides: null, NOT the origin tile. Both callers fall back to the origin, but
        // for their own stated reasons and with their own return shapes (the dismount also has to say which
        // way the horse faces), so the fallback must not live in here.
        Assert.Null(MapData.FreeNeighbour(
            MapData.CardinalWalk(5, 5, N), xs: 20, ys: 20, NeverBlocked, occupied: (x, y) => true));

        // Same for the ring walk, and for a walk every tile of which is out of bounds.
        Assert.Null(MapData.FreeNeighbour(
            MapData.SelfThenRingWalk(5, 5, 2), xs: 20, ys: 20, NeverBlocked, occupied: (x, y) => true));
        Assert.Null(MapData.FreeNeighbour(
            MapData.CardinalWalk(50, 50, N), xs: 20, ys: 20, NeverBlocked, NeverOccupied));
    }

    [Fact]
    public void AnUnknownMapHasNoUpperBoundButStillHasNoNegativeTiles()
    {
        // World.FreeSpawnTile hands int.MaxValue for a map with no registry row, because its inline version
        // skipped the `tx >= xs` test in exactly that case. A tile far past any real map still passes.
        Assert.Equal((999, 999, -1), MapData.FreeNeighbour(
            MapData.SelfThenRingWalk(999, 999, 2), int.MaxValue, int.MaxValue, NeverBlocked, NeverOccupied));

        // Negative coordinates are out of bounds regardless — that half of the test was unconditional there
        // too, so a spawn at (0,0) whose tile is taken cannot be pushed off the top-left corner.
        var got = MapData.FreeNeighbour(
            MapData.SelfThenRingWalk(0, 0, 1), int.MaxValue, int.MaxValue,
            NeverBlocked, occupied: (x, y) => (x, y) == (0, 0));
        Assert.NotNull(got);
        Assert.True(got!.Value.x >= 0 && got.Value.y >= 0);
        // The first non-negative tile of the radius-1 ring in walk order: dx=-1 is entirely off the map, dx=0
        // starts at dy=-1 which is too, so (0,1) — south — is the answer, not the east tile.
        Assert.Equal((0, 1, -1), got);
    }
}
