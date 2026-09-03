using Server;
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
                         ConcurrentMap = 60014;

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
    /// <para>The lock-scope claim is not asserted by inspection here — it is enforced by the two
    /// <c>Debug.Assert</c>s inside <c>Session.SetPositionUnderWorldLock</c>, which is now the only way an
    /// arrival's position can be written. Either would throw out of this walk if <c>World._lock</c> or the
    /// mover's own state monitor were missing, so a green run in a Debug build IS the proof. (The pair is
    /// compiled out in Release, which is the standing #29-family gap the reviewers filed separately.)</para>
    /// </summary>
    [Fact]
    public void WarpingThroughADoorWritesThePositionUnderTheWorldLock()
    {
        Assert.True(Content.TryWarp(DoorMap, DoorX, DoorY, out var dest), "Country Farm's door into Mignok's Home");
        Assert.True(Content.TryMap(DoorMap, out var src));

        var (mover, _, _) = _fx.PlayerWith("LockedWarpWalker", c => { c.MapXs = src.Xs; c.MapYs = src.Ys; },
                                           DoorMap, DoorX, (ushort)(DoorY + 1));

        mover.Receive(WalkPacket(dir: 0, fromX: DoorX, fromY: DoorY + 1));   // north, onto the door

        Assert.Equal(dest.x, mover.PlayerX);
        Assert.Equal(dest.y, mover.PlayerY);
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
