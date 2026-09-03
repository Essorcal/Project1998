using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Arrivals under the world lock (#99 part 1) — the lock-scope half, and only that half.
///
/// <para><c>Session.EnterMap</c> is the funnel for every player position change that is not a walk step: 24
/// callers, covering the SQL warp a step takes, the five scripted-tile entrances, world-map travel, the
/// Gateway, and the GM teleports. It used to clamp the requested tile to the map's bounds and assign
/// <c>_char.X/Y</c> with no lock held, and <c>@approach</c>/<c>@bring</c>/<c>@npc</c> chose their tile even
/// earlier, through a <c>World.PeerAt</c> and a <c>World.MobAt</c> that each took and released the lock. The
/// resolve and the write are now one acquisition, in <see cref="World.PlacePlayer"/>.</para>
///
/// <para><b>What these tests deliberately do NOT decide.</b> Whether an occupied arrival tile should refuse
/// the warp, step the arriver aside, or stack is #99's open source question, and nothing here answers it.
/// The default <see cref="ArrivalPolicy.Clamp"/> is the behaviour that has always shipped — clamp and take
/// the tile, occupancy untested — and <see cref="TwoWarpsThroughOneDoorBothLandWhereClampSays"/> pins that
/// by name, so a later policy change has to come past a test that says which policy it is replacing.</para>
/// </summary>
[Collection("world")]
public class WarpUnderLockTests
{
    private readonly SessionFixture _fx;

    public WarpUnderLockTests(SessionFixture fx) => _fx = fx;

    /// <summary>Country Farm's door into Mignok's Home — the same real doorway MovementRaceTests uses, and
    /// the only content this file depends on. Destination read back from Warps.csv, never hardcoded.</summary>
    private const ushort DoorMap = 4715, DoorX = 17, DoorY = 5;

    /// <summary>Content-free map ids for the policy tests: no Maps.csv row means <c>MapData.For</c> is null,
    /// so nothing is ever terrain-blocked and occupancy is the only thing the search can be reacting to.
    /// One per test, since the fixture World is shared and a session is never unregistered from a map.</summary>
    private const ushort AdjacentMap = 60010, BoxedMap = 60011, FromMap = 60012, ElsewhereMap = 60013,
                         ConcurrentMap = 60014, LiveMobMap = 60015, DeadMobMap = 60016,
                         ColdMap = 60017, ColdClampMap = 60018;

    private static byte[] WalkPacket(byte dir, int fromX, int fromY) =>
        SessionFixture.Frame(0x06, new byte[]
        {
            dir, 0,
            (byte)(fromX >> 8), (byte)fromX,
            (byte)(fromY >> 8), (byte)fromY,
        });

    // =====================================================================================================
    // #99 checkbox 1: every Session.EnterMap arrival resolves its tile and writes the position inside one
    // World._lock acquisition.
    // =====================================================================================================

    /// <summary>
    /// A real warp still lands where the warp says, and the write happened under both locks.
    ///
    /// <para><b>Two halves, and the #102 review showed why one of them is not enough.</b> The
    /// <c>Debug.Assert</c>s inside <c>Session.SetPositionUnderWorldLock</c> prove that a write which REACHES
    /// the seam held both locks. They say nothing about a write that bypasses it — the reviewer reverted
    /// <c>Session.EnterMap</c> to its old inline clamp and this test stayed green, because the landing tile
    /// is the same either way. So the seam's use is now observed directly:
    /// <c>Session.PositionWritesUnderWorldLock</c> counts writes through it, and the walk below has to move
    /// it. Bypass the seam and the count does not move, whatever tile the player ends up on.</para>
    ///
    /// <para>(The assert half is compiled out in Release, which is the standing #29-family gap the reviewers
    /// filed separately. The counter is not, so this half of the proof holds in both configurations.)</para>
    /// </summary>
    [Fact]
    public void WarpingThroughADoorWritesThePositionUnderTheWorldLock()
    {
        long writesBefore = Session.PositionWritesUnderWorldLock;
        Assert.True(Content.TryWarp(DoorMap, DoorX, DoorY, out var dest), "Country Farm's door into Mignok's Home");
        Assert.True(Content.TryMap(DoorMap, out var src));

        var (mover, _, _) = _fx.PlayerWith("LockedWarpWalker", c => { c.MapXs = src.Xs; c.MapYs = src.Ys; },
                                           DoorMap, DoorX, (ushort)(DoorY + 1));

        mover.Receive(WalkPacket(dir: 0, fromX: DoorX, fromY: DoorY + 1));   // north, onto the door

        Assert.Equal(dest.x, mover.PlayerX);
        Assert.Equal(dest.y, mover.PlayerY);
        // ...and it got there THROUGH the seam, not merely to the right tile.
        Assert.True(Session.PositionWritesUnderWorldLock > writesBefore,
            "the arrival landed on the right tile without going through SetPositionUnderWorldLock — " +
            "the lock-scope claim is what this asserts, not the destination");
    }

    /// <summary>
    /// <b>The 24 callers are otherwise unchanged: the default policy still does not test occupancy.</b> A
    /// player is standing on the destination tile and the arriver lands on it anyway, exactly as before this
    /// PR — a warp has never asked whether anyone was already there.
    ///
    /// <para>This is the test that would go red if the lock-scope refactor quietly acquired an opinion, which
    /// is the one thing part 1 must not do.</para>
    /// </summary>
    [Fact]
    public void TheDefaultPolicyStillDoesNotTestOccupancy()
    {
        long writesBefore = Session.PositionWritesUnderWorldLock;
        Assert.True(Content.TryWarp(DoorMap, DoorX, DoorY, out var dest));
        Assert.True(Content.TryMap(DoorMap, out var src));
        Assert.True(Content.TryMap(dest.m, out var dm));

        // Someone is already standing exactly where the door comes out.
        var (sitter, _, _) = _fx.PlayerWith("DoorwaySitter", c => { c.MapXs = dm.Xs; c.MapYs = dm.Ys; },
                                            dest.m, dest.x, dest.y);
        var (mover, _, _) = _fx.PlayerWith("StacksOnArrival", c => { c.MapXs = src.Xs; c.MapYs = src.Ys; },
                                           DoorMap, DoorX, (ushort)(DoorY + 1));

        mover.Receive(WalkPacket(dir: 0, fromX: DoorX, fromY: DoorY + 1));

        Assert.Equal(dest.x, mover.PlayerX);
        Assert.Equal(dest.y, mover.PlayerY);
        Assert.Equal(sitter.PlayerX, mover.PlayerX);   // stacked, as it always has
        Assert.Equal(sitter.PlayerY, mover.PlayerY);
        Assert.True(Session.PositionWritesUnderWorldLock > writesBefore, "the arrival bypassed the seam");
    }

    /// <summary>
    /// <b>Two warps through one door in the same instant.</b> Both resolve inside the lock — one at a time,
    /// since they contend for it — and both land where <see cref="ArrivalPolicy.Clamp"/> says, which is the
    /// requested tile, because Clamp does not look at who is standing there.
    ///
    /// <para><b>The policy is named on purpose.</b> If #99's source check later says an occupied arrival tile
    /// should refuse or step aside, this test is where that shows up: it has to be rewritten to name the new
    /// policy, rather than silently continuing to pass. Part 1 does not decide it.</para>
    ///
    /// <para><b>What this is NOT.</b> It is not a race test, and the #102 reviewer was right to say so: under
    /// <see cref="ArrivalPolicy.Clamp"/> there is no contested resource, so the only way it can fail is an
    /// exception out of the concurrent path. It asserts the outcome Clamp guarantees — both arrivals on the
    /// requested tile, every round — and it exists to pin the policy by name and to run the arrival path from
    /// two threads at once. The place where a real contest is won or lost under this lock is
    /// <c>MovementRaceTests.TwoSessionsRacingForOneTileExactlyOneWins</c>.</para>
    ///
    /// <para>Driven at <see cref="World.PlacePlayer"/> rather than through two concurrent
    /// <c>Session.EnterMap</c> calls, and that is a deliberate limit: a full EnterMap broadcasts into other
    /// sessions' recorders, and <c>RecordingOutbound</c> is documented as not thread-safe, so a concurrent
    /// end-to-end version would be testing the recorder. The end-to-end path is covered sequentially by the
    /// two tests above.</para>
    /// </summary>
    [Fact]
    public void TwoWarpsThroughOneDoorBothLandWhereClampSays()
    {
        const int Rounds = 200;
        const ushort Dx = 7, Dy = 7;   // the "destination tile" both doors come out on

        var world = _fx.World;
        var (a, _) = _fx.Player("DoorRacerA", ConcurrentMap, 1, 1);
        var (b, _) = _fx.Player("DoorRacerB", ConcurrentMap, 2, 2);

        int rounds = 0, bothLanded = 0;
        Exception? fault = null;

        var barrier = new Barrier(2, _ =>
        {
            try
            {
                rounds++;
                if (a.PlayerX == Dx && a.PlayerY == Dy && b.PlayerX == Dx && b.PlayerY == Dy) bothLanded++;
                // Send them back to their own corners for the next round.
                a.WithState(() => world.SetPlayerPosition(a, 1, 1));
                b.WithState(() => world.SetPlayerPosition(b, 2, 2));
            }
            catch (Exception e) { fault = e; }
        });

        void Arrive(Session mover, ushort hx, ushort hy)
        {
            for (int i = 0; i < Rounds; i++)
            {
                mover.WithState(() => world.PlacePlayer(
                    mover, ConcurrentMap, 12, 12, Dx, Dy,
                    ArrivalPolicy.Clamp, new FromTile(ConcurrentMap, hx, hy), out _, out _));
                barrier.SignalAndWait();
            }
        }

        var ta = new Thread(() => Arrive(a, 1, 1)) { IsBackground = true, Name = "door-racer-a" };
        var tb = new Thread(() => Arrive(b, 2, 2)) { IsBackground = true, Name = "door-racer-b" };
        ta.Start(); tb.Start();
        Assert.True(ta.Join(TimeSpan.FromSeconds(30)), "racer A never finished");
        Assert.True(tb.Join(TimeSpan.FromSeconds(30)), "racer B never finished");

        Assert.Null(fault);
        Assert.Equal(Rounds, rounds);
        Assert.Equal(Rounds, bothLanded);   // Clamp stacks them, every round, on every machine
    }

    // =====================================================================================================
    // #99 checkbox 2: ApproachTile's free-tile search runs inside that acquisition.
    // =====================================================================================================

    /// <summary>The search @approach/@bring/@npc use picks the first FREE cardinal neighbour, in N/E/S/W
    /// order — the order the old <c>Session.ApproachTile</c> walked. North of the anchor is taken, so east
    /// wins.</summary>
    [Fact]
    public void AdjacentPolicyTakesTheFirstFreeCardinalNeighbour()
    {
        var world = _fx.World;
        _fx.Player("AdjBlockerNorth", AdjacentMap, 5, 4);          // N of the anchor — taken
        var (mover, _) = _fx.Player("AdjMover", AdjacentMap, 11, 11);

        mover.WithState(() => world.PlacePlayer(
            mover, AdjacentMap, 12, 12, 5, 5,
            ArrivalPolicy.AdjacentFreeElseStack, new FromTile(AdjacentMap, 11, 11), out _, out _));

        Assert.Equal(6, mover.PlayerX);   // east, the next one round
        Assert.Equal(5, mover.PlayerY);
    }

    /// <summary>A LIVING mob north of the anchor takes that tile, so the search moves on to east — the mob
    /// half of the predicate, which the player cases could not distinguish. Deleting
    /// <c>AdjacentFreeLocked</c>'s mob line leaves every other test in this file green (the #102 reviewer's
    /// M8); it turns this one red.</summary>
    [Fact]
    public void AdjacentPolicySkipsATileHeldByALivingMob()
    {
        var world = _fx.World;
        world.AddMob(LiveMobMap, new Mob(world.AllocateMobId(), 1, 5, 4, "north guard", hp: 10));
        var (mover, _) = _fx.Player("LiveMobMover", LiveMobMap, 11, 11);

        mover.WithState(() => world.PlacePlayer(
            mover, LiveMobMap, 12, 12, 5, 5,
            ArrivalPolicy.AdjacentFreeElseStack, new FromTile(LiveMobMap, 11, 11), out _, out _));

        Assert.Equal(6, mover.PlayerX);
        Assert.Equal(5, mover.PlayerY);
    }

    /// <summary>...and a DEAD one does not: the predicate is <c>mo.Alive</c>, so a corpse north of the anchor
    /// leaves that tile free and the search takes it. The mirror of the case above, and what stops "skip
    /// tiles with a mob on them" from quietly becoming "skip tiles that ever had one".</summary>
    [Fact]
    public void AdjacentPolicyTakesATileHeldByADeadMob()
    {
        var world = _fx.World;
        var corpse = new Mob(world.AllocateMobId(), 1, 5, 4, "north corpse", hp: 10);
        corpse.Hp = 0;
        world.AddMob(DeadMobMap, corpse);
        var (mover, _) = _fx.Player("DeadMobMover", DeadMobMap, 11, 11);

        mover.WithState(() => world.PlacePlayer(
            mover, DeadMobMap, 12, 12, 5, 5,
            ArrivalPolicy.AdjacentFreeElseStack, new FromTile(DeadMobMap, 11, 11), out _, out _));

        Assert.Equal(5, mover.PlayerX);
        Assert.Equal(4, mover.PlayerY);
    }

    /// <summary>A WALL north of the anchor does the same as a body — the terrain half of the predicate, which
    /// the content-free maps the other cases use cannot exercise at all, since <c>MapData.For</c> is null
    /// there and nothing is ever blocked. Deleting <c>AdjacentFreeLocked</c>'s <c>BlockedMove</c> line (the
    /// reviewer's M9) turns this one red and nothing else.
    ///
    /// <para>The anchor is FOUND rather than hardcoded: the first tile on a real map whose north neighbour is
    /// blocked and whose east neighbour is not, with both free of bodies. A Warps.csv or terrain edit moves
    /// the test with it instead of breaking it.</para></summary>
    [Fact]
    public void AdjacentPolicySkipsATileBehindAWall()
    {
        var world = _fx.World;
        Assert.True(Content.TryMap(DoorMap, out var md));
        var terrain = MapData.For(DoorMap, md.Xs, md.Ys);
        Assert.NotNull(terrain);

        (int x, int y) anchor = (-1, -1);
        for (int y = 1; y < md.Ys - 1 && anchor.x < 0; y++)
            for (int x = 1; x < md.Xs - 1; x++)
                if (terrain!.BlockedMove(x, y - 1, 0) && !terrain.BlockedMove(x + 1, y, 1)
                    && world.PeerAt(DoorMap, x, y - 1) is null && world.PeerAt(DoorMap, x + 1, y) is null
                    && world.MobAt(DoorMap, x, y - 1) is null && world.MobAt(DoorMap, x + 1, y) is null)
                { anchor = (x, y); break; }
        Assert.True(anchor.x >= 0, $"no tile on map {DoorMap} has a blocked north and a free east");

        var (mover, _, _) = _fx.PlayerWith("WallMover", c => { c.MapXs = md.Xs; c.MapYs = md.Ys; },
                                           DoorMap, 1, 1);
        world.LeaveMap(mover, DoorMap);   // as Session.EnterMap does before placing

        mover.WithState(() => world.PlacePlayer(
            mover, DoorMap, md.Xs, md.Ys, anchor.x, anchor.y,
            ArrivalPolicy.AdjacentFreeElseStack, new FromTile(DoorMap, 1, 1), out _, out _));

        Assert.Equal(anchor.x + 1, mover.PlayerX);   // east: north is a wall
        Assert.Equal(anchor.y, mover.PlayerY);
    }

    /// <summary>...and stacks on the anchor's own tile when every neighbour is taken, which is what the old
    /// code's "else the target's own tile" fallback did. A GM must never be refused a rescue.</summary>
    [Fact]
    public void AdjacentPolicyStacksWhenTheAnchorIsBoxedIn()
    {
        var world = _fx.World;
        _fx.Player("BoxN", BoxedMap, 5, 4);
        _fx.Player("BoxE", BoxedMap, 6, 5);
        _fx.Player("BoxS", BoxedMap, 5, 6);
        _fx.Player("BoxW", BoxedMap, 4, 5);
        var (mover, _) = _fx.Player("BoxMover", BoxedMap, 11, 11);

        mover.WithState(() => world.PlacePlayer(
            mover, BoxedMap, 12, 12, 5, 5,
            ArrivalPolicy.AdjacentFreeElseStack, new FromTile(BoxedMap, 11, 11), out _, out _));

        Assert.Equal(5, mover.PlayerX);
        Assert.Equal(5, mover.PlayerY);
    }

    /// <summary>
    /// <b>The terrain load happens before the lock, not inside it.</b> The #102 reviewer found that
    /// <c>MapData.Prewarm</c> returns early for a map with no <c>Maps.csv</c> row, so the search's own
    /// <c>MapData.For</c> did the cold load — a disk read, a full cell decode and a SQLite query — inside
    /// <c>World._lock</c>, with every player on every map queued behind it. Reachable through
    /// <c>@approach</c>/<c>@bring</c> on an unregistered map, which is also the path every policy test here
    /// takes.
    ///
    /// <para>Asserted from the outside: the cache is cold before the call and warm after, and the load is
    /// attributable to <c>PlacePlayer</c> because nothing else in the test touches that map id.</para>
    /// </summary>
    [Fact]
    public void TheSearchWarmsTheMapCacheBeforeTakingTheLock()
    {
        Assert.False(Content.Maps.ContainsKey(ColdMap), "the point of the test is an UNregistered map");
        Assert.False(MapData.IsLoadedForTest(ColdMap), "cold to start with");

        var (mover, _) = _fx.Player("ColdSearchMover", ColdMap, 9, 9);
        mover.WithState(() => _fx.World.PlacePlayer(
            mover, ColdMap, 12, 12, 5, 5,
            ArrivalPolicy.AdjacentFreeElseStack, new FromTile(ColdMap, 9, 9), out _, out _));

        Assert.True(MapData.IsLoadedForTest(ColdMap),
            "PlacePlayer did not warm the cache — the search's MapData.For is then a cold load under _lock");
    }

    /// <summary>...and <see cref="ArrivalPolicy.Clamp"/> loads no terrain at all, because it never reads any.
    /// The old inline clamp in <c>Session.EnterMap</c> did not either, so this is the arrival path staying as
    /// cheap as it was for the twenty-one callers that use the default.</summary>
    [Fact]
    public void TheDefaultPolicyLoadsNoTerrain()
    {
        Assert.False(Content.Maps.ContainsKey(ColdClampMap));
        var (mover, _) = _fx.Player("ColdClampMover", ColdClampMap, 9, 9);

        mover.WithState(() => _fx.World.PlacePlayer(
            mover, ColdClampMap, 12, 12, 5, 5,
            ArrivalPolicy.Clamp, new FromTile(ColdClampMap, 9, 9), out _, out _));

        Assert.False(MapData.IsLoadedForTest(ColdClampMap));
    }

    /// <summary>
    /// <b>The mover still holds the tile it is leaving.</b> The one place where moving the search under the
    /// lock could have changed an answer: <c>Session.EnterMap</c> calls <c>World.LeaveMap</c> before it places
    /// anyone, so by the time the search runs the mover is in no map's player list — and a search that could
    /// not see them would happily offer them the tile they are standing on, which the old ordering never did
    /// (it ran before EnterMap, with the mover still listed). <c>PlacePlayer</c>'s <c>from</c> is what carries
    /// that across, and this is the case that would go red without it.
    ///
    /// <para>Set up exactly as production does it: the mover is registered on the map, then LEFT, and only
    /// then placed — so the map's own player list genuinely cannot account for them.</para>
    /// </summary>
    [Fact]
    public void TheMoverStillOccupiesTheTileItIsLeaving()
    {
        var world = _fx.World;
        var (mover, _) = _fx.Player("FromGuardMover", FromMap, 5, 4);   // standing N of the anchor
        world.LeaveMap(mover, FromMap);                                 // what Session.EnterMap does first

        mover.WithState(() => world.PlacePlayer(
            mover, FromMap, 12, 12, 5, 5,
            ArrivalPolicy.AdjacentFreeElseStack, new FromTile(FromMap, 5, 4), out _, out _));

        Assert.Equal(6, mover.PlayerX);   // east: north is still ours, even unlisted
        Assert.Equal(5, mover.PlayerY);
    }

    /// <summary>...and the guard is map-scoped: a mover arriving from a DIFFERENT map holds nothing here, so
    /// the north tile is free and the search takes it. Without the map test the guard would spuriously skip a
    /// tile whenever the coordinates happened to match.</summary>
    [Fact]
    public void TheFromGuardOnlyAppliesOnTheMapTheMoverIsLeaving()
    {
        var world = _fx.World;
        var (mover, _) = _fx.Player("FromElsewhereMover", ElsewhereMap, 5, 4);
        world.LeaveMap(mover, ElsewhereMap);

        mover.WithState(() => world.PlacePlayer(
            mover, ElsewhereMap, 12, 12, 5, 5,
            ArrivalPolicy.AdjacentFreeElseStack, new FromTile(60099, 5, 4), out _, out _));

        Assert.Equal(5, mover.PlayerX);   // north, free after all — the from-tile was on another map
        Assert.Equal(4, mover.PlayerY);
    }
}
