using Server;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Movement under the world lock (#30).
///
/// <para>What used to be true, and is what these tests check has stopped being true: <c>HandleWalk</c> asked
/// <c>World.MobAt</c>, then <c>World.PlayerAt</c> or <c>World.PvpGhostAt</c> — three acquisitions of
/// <c>World._lock</c>, each taken and released — and then, about a hundred lines of warp handling later,
/// committed <c>_char.X/Y</c> under no lock at all. Two sessions stepping onto one empty tile in the same
/// instant both passed the check and both committed onto it, and the world tick's <c>occupied</c> snapshot
/// was already stale for any walk that committed mid-beat. <see cref="World.TryMovePlayer"/> makes the check
/// and the write one critical section.</para>
///
/// <para>These tests use their own map ids, one per test, rather than the fixture's <c>HomeMap</c>. The World
/// is shared across the collection and a session is never unregistered from a map once entered, so a test that
/// asserts "this tile is empty" cannot share a map with anything else. An id with no <c>Maps.csv</c> row is
/// also terrain-free (<c>MapData.For</c> returns null, so nothing is ever pass-blocked), warp-free and
/// spawn-free, which leaves occupancy as the only thing that can refuse a step — exactly what is under
/// test. The one exception is <see cref="WarpTileBeatsOccupancy"/>, which is about a warp and therefore has
/// to stand on a map that has one.</para>
/// </summary>
[Collection("world")]
public class MovementRaceTests
{
    private readonly SessionFixture _fx;

    public MovementRaceTests(SessionFixture fx) => _fx = fx;

    /// <summary>Map ids with no content behind them, one per test. See the class doc for why they are not
    /// shared.</summary>
    private const ushort RaceMap = 60000, LivingMap = 60001, GhostMap = 60002, DeadMap = 60003, ReasonMap = 60004,
                         TornMap = 60005;

    /// <summary>Enough rounds that a lost race shows. The window this closes is a few instructions wide, so
    /// one round proves nothing; four hundred barrier-released rounds is where the unlocked version fails.
    /// </summary>
    private const int RaceRounds = 400;

    /// <summary>The client's walk packet (0x06): direction, a step counter we don't read, then the tile the
    /// client believes it is standing on as two big-endian u16s.</summary>
    private static byte[] WalkPacket(byte dir, int fromX, int fromY) =>
        SessionFixture.Frame(0x06, new byte[]
        {
            dir, 0,
            (byte)(fromX >> 8), (byte)fromX,
            (byte)(fromY >> 8), (byte)fromY,
        });

    // =====================================================================================================
    // The acceptance test the ticket asks for by name.
    // =====================================================================================================

    /// <summary>
    /// <b>"Two sessions racing for one tile — exactly one succeeds."</b>
    ///
    /// <para>Two players stand either side of one empty tile and both step onto it, released together by a
    /// <see cref="Barrier"/> so the two <c>TryMovePlayer</c> calls overlap rather than queue. Exactly one may
    /// return true, and afterwards the two must be on different tiles — the second assertion is the one that
    /// matters, because "both returned true" and "both are standing here" are the same bug seen from two
    /// sides, and only the second is what a player would report.</para>
    ///
    /// <para>The barrier's post-phase action does the checking and the reset: it runs on one participant
    /// thread with the other still parked, so it is the one point in the loop where reading both sessions'
    /// positions is not itself a race.</para>
    ///
    /// <para><b>Shaped so it actually fails without the fix.</b> Verified by reverting the body of
    /// <c>TryMovePlayer</c> to the pre-#30 shape — the two scans in one <c>lock</c>, the lock released, then
    /// the write in a second one — with nothing else changed and no sleep widening the window: 57 of the 400
    /// rounds ended with both movers on the tile. With the real body, 0.</para>
    /// </summary>
    [Fact]
    public void TwoSessionsRacingForOneTileExactlyOneWins()
    {
        var world = _fx.World;
        var (west, _) = _fx.Player("RaceWest", RaceMap, 4, 5);
        var (east, _) = _fx.Player("RaceEast", RaceMap, 6, 5);

        var results = new bool[2];
        int rounds = 0, winners = 0, doubleWins = 0, sharedTile = 0;
        Exception? fault = null;

        var barrier = new Barrier(2, _ =>
        {
            try
            {
                rounds++;
                if (results[0] && results[1]) doubleWins++;
                if (results[0] || results[1]) winners++;
                if (west.PlayerX == east.PlayerX && west.PlayerY == east.PlayerY) sharedTile++;

                // Put them back for the next round. Through the same World setter the production snap-back
                // uses, so the write is under the same lock the round's reads were.
                west.WithState(() => world.SetPlayerPosition(west, 4, 5));
                east.WithState(() => world.SetPlayerPosition(east, 6, 5));
                results[0] = results[1] = false;
            }
            catch (Exception e) { fault = e; }
        });

        void Race(int slot, Session mover)
        {
            for (int i = 0; i < RaceRounds; i++)
            {
                // Exactly what HandleWalk does for a step onto (5,5) with no terrain in the way: the mover's
                // own state monitor first, then the world lock inside TryMovePlayer (#29's order).
                results[slot] = mover.WithState(() => world.TryMovePlayer(
                    mover, RaceMap, 5, 5,
                    ghostMover: false, enforceOccupancy: true, otherwiseBlocked: false, out _));
                barrier.SignalAndWait();
            }
        }

        var a = new Thread(() => Race(0, west)) { IsBackground = true, Name = "race-west" };
        var b = new Thread(() => Race(1, east)) { IsBackground = true, Name = "race-east" };
        a.Start(); b.Start();
        Assert.True(a.Join(TimeSpan.FromSeconds(30)), "race thread west did not finish");
        Assert.True(b.Join(TimeSpan.FromSeconds(30)), "race thread east did not finish");

        Assert.Null(fault);
        Assert.Equal(RaceRounds, rounds);
        Assert.Equal(0, doubleWins);                 // both passed the check and both committed — the bug
        Assert.Equal(0, sharedTile);                 // ...as a player would see it
        Assert.Equal(RaceRounds, winners);           // and the step is not merely refused to both
    }

    // =====================================================================================================
    // HandleWalk behaviour, driven through the packet the client actually sends.
    // =====================================================================================================

    /// <summary>
    /// <b>The viewport reconcile never sees a torn (X, Y).</b> The #97 review's one real finding: the tick
    /// snapshotted the player LIST under <c>_lock</c> and then let <c>ReconcilePeer</c> read
    /// <c>other.PlayerX</c> and <c>other.PlayerY</c> back off each session out here, with no lock held. Those
    /// are two separate <c>ushort</c> reads of a character whose every writer holds <c>_lock</c>, so the
    /// viewport gate could test one tile's X against the previous tile's Y — and its two <c>InView</c> calls
    /// could each catch a different pair.
    ///
    /// <para>The mover shuttles between two tiles that differ in BOTH axes, so any mixture of them is a tile
    /// it was never on and a tear is unambiguous. Two watchers run against it. The first asks the world for
    /// its view the way the tick does, and every <see cref="PeerTile"/> that comes back must be one of the
    /// two real tiles — that is the assertion. The second is the OLD read, <c>PlayerX</c> then
    /// <c>PlayerY</c> with no lock, and it is counted but never asserted on: whether an unlocked read tears
    /// on a given run is exactly the machine-dependent question this suite has been getting rid of.</para>
    ///
    /// <para><b>The control is what makes this more than a tautology,</b> and it took a correction to get
    /// right. The first version ran the unlocked read inside the same loop as the snapshot — which calls
    /// <c>World.View</c>, and so takes <c>_lock</c> every iteration, serialising the reader against the
    /// writer. It reported zero tears and proved nothing. Given its own thread and no lock, the same read
    /// tears reliably. Three runs of this test as it stands: 2 915, 3 635 and 7 207 torn pairs out of ~7.5M
    /// unlocked reads, against 0 out of ~117 000 snapshot observations in the same runs. The finding,
    /// reproduced; the fix, holding.</para>
    /// </summary>
    [Fact]
    public void ViewportReconcileNeverSeesATornPeerTile()
    {
        var world = _fx.World;
        var (mover, _) = _fx.Player("TornMover", TornMap, 5, 5);
        var (observer, _) = _fx.Player("TornObserver", TornMap, 12, 12);   // out of the way; View excludes self

        const int Steps = 40_000;
        var done = new ManualResetEventSlim();
        Exception? walkerFault = null, watcherFault = null;
        int snapshotTears = 0, observations = 0;
        int unlockedTears = 0, unlockedReads = 0;

        static bool RealTile(int x, int y) => (x == 5 && y == 5) || (x == 6 && y == 6);

        var walker = new Thread(() =>
        {
            try
            {
                for (int i = 0; i < Steps; i++)
                {
                    var (nx, ny) = (i & 1) == 0 ? (6, 6) : (5, 5);
                    mover.WithState(() => world.TryMovePlayer(
                        mover, TornMap, nx, ny,
                        ghostMover: false, enforceOccupancy: true, otherwiseBlocked: false, out _));
                }
            }
            catch (Exception e) { walkerFault = e; }
            finally { done.Set(); }
        }) { IsBackground = true, Name = "torn-walker" };

        // What the tick hands the reconcile since the fix: coordinates taken with the player list, in one
        // acquisition of the world lock.
        var watcher = new Thread(() =>
        {
            try
            {
                while (!done.IsSet)
                    foreach (var peer in world.View(observer, TornMap).peers)
                    {
                        if (!ReferenceEquals(peer.Session, mover)) continue;
                        observations++;
                        if (!RealTile(peer.X, peer.Y)) snapshotTears++;
                    }
            }
            catch (Exception e) { watcherFault = e; }
        }) { IsBackground = true, Name = "torn-watcher" };

        // What it used to do. Its own thread and no lock, or it does not reproduce anything — see the doc.
        var control = new Thread(() =>
        {
            while (!done.IsSet)
            {
                int x = mover.PlayerX, y = mover.PlayerY;
                unlockedReads++;
                if (!RealTile(x, y)) unlockedTears++;
            }
        }) { IsBackground = true, Name = "torn-control" };

        walker.Start(); watcher.Start(); control.Start();
        Assert.True(walker.Join(TimeSpan.FromSeconds(60)), "the walker never finished");
        Assert.True(watcher.Join(TimeSpan.FromSeconds(60)), "the watcher never finished");
        Assert.True(control.Join(TimeSpan.FromSeconds(60)), "the control reader never finished");

        Assert.Null(walkerFault);
        Assert.Null(watcherFault);
        Assert.True(observations > 0, "the watcher never saw the mover — the probe proved nothing");
        Assert.Equal(0, snapshotTears);
    }

    /// <summary>A step onto a living player is refused: the mover does not move, and it is re-anchored with
    /// the 0x04 snap-back that cancels the client's own prediction of the step.</summary>
    [Fact]
    public void StepOntoLivingPlayerIsRefusedAndTheMoverStaysPut()
    {
        var (blocker, _) = _fx.Player("WalkBlocker", LivingMap, 5, 4);
        var (mover, outbound) = _fx.Player("WalkMover", LivingMap, 5, 5);
        Assert.False(blocker.IsDead);

        mover.Receive(WalkPacket(dir: 0, fromX: 5, fromY: 5));   // north, onto the blocker's tile

        Assert.Equal(5, mover.PlayerX);
        Assert.Equal(5, mover.PlayerY);
        Assert.NotEmpty(outbound.BodiesOf(0x04));
    }

    /// <summary>...and the reason it was refused is the PLAYER one, which is what puts " player" in the walk
    /// log. Asserted at the seam rather than through the log, because the log has no test sink — this is the
    /// value the log line formats.</summary>
    [Fact]
    public void RefusalReasonForALivingPlayerIsPlayer()
    {
        var world = _fx.World;
        _fx.Player("ReasonBlocker", ReasonMap, 5, 4);
        var (mover, _) = _fx.Player("ReasonMover", ReasonMap, 5, 5);

        BlockReason why = BlockReason.None;
        bool moved = mover.WithState(() =>
        {
            bool ok = world.TryMovePlayer(mover, ReasonMap, 5, 4,
                                          ghostMover: false, enforceOccupancy: true, otherwiseBlocked: false, out var w);
            why = w;
            return ok;
        });

        Assert.False(moved);
        Assert.Equal(BlockReason.Player, why);
        Assert.Equal(5, mover.PlayerY);   // refused means it stayed where it was
    }

    /// <summary>A step onto a DEAD player is allowed — a corpse is a ghost, and you can walk over one to
    /// reach it. The mirror of the test above, and the reason the occupancy predicate cannot simply be "is
    /// anyone standing here".</summary>
    [Fact]
    public void StepOntoDeadPlayerIsAllowed()
    {
        var (corpse, _, _) = _fx.PlayerWith("WalkCorpse", c => c.Hp = 0, DeadMap, 5, 4);
        var (mover, _) = _fx.Player("WalkOverCorpse", DeadMap, 5, 5);
        Assert.True(corpse.IsDead);

        mover.Receive(WalkPacket(dir: 0, fromX: 5, fromY: 5));   // north, onto the corpse

        Assert.Equal(5, mover.PlayerX);
        Assert.Equal(4, mover.PlayerY);
    }

    /// <summary>
    /// <b>Warps still beat occupancy.</b> The one precedence this refactor could plausibly have broken: the
    /// occupancy check moved DOWN, from above the warp block to below it, so a warp tile with someone standing
    /// on it is the case that would show it if the order had slipped. Stepping onto an occupied warp tile must
    /// still carry the mover through — a doorway is not blocked by the person who just walked through it.
    ///
    /// <para>Unlike the tests above this one needs REAL content, so it uses a real doorway: Country Farm
    /// (4715) tile (17,5), the door into Mignok's Home (4716). Only the source map and tile are hardcoded; the
    /// destination is read back from <c>Content.Warps</c>, so a Warps.csv edit moves the assertion with it
    /// rather than failing. The step is proven to be onto an occupied tile first, through the same seam
    /// HandleWalk uses — so a pass here is precedence, not an empty tile.</para>
    /// </summary>
    [Fact]
    public void WarpTileBeatsOccupancy()
    {
        const ushort srcMap = 4715;
        const ushort doorX = 17, doorY = 5;   // the warp source; the mover stands one tile south of it
        Assert.True(Content.TryWarp(srcMap, doorX, doorY, out var dest), "Country Farm's door into Mignok's Home");
        Assert.True(Content.TryMap(srcMap, out var src));

        var world = _fx.World;
        _fx.PlayerWith("DoorBlocker", c => { c.MapXs = src.Xs; c.MapYs = src.Ys; }, srcMap, doorX, doorY);
        var (mover, _, _) = _fx.PlayerWith("DoorWalker", c => { c.MapXs = src.Xs; c.MapYs = src.Ys; },
                                           srcMap, doorX, doorY + 1);

        // The door tile really is occupied: bare occupancy refuses it, with the player reason.
        BlockReason why = BlockReason.None;
        bool wouldMove = mover.WithState(() =>
        {
            bool ok = world.TryMovePlayer(mover, srcMap, doorX, doorY,
                                          ghostMover: false, enforceOccupancy: true, otherwiseBlocked: false, out var w);
            why = w;
            return ok;
        });
        Assert.False(wouldMove);
        Assert.Equal(BlockReason.Player, why);

        // ...and the walk goes through it anyway, because the warp branch returns before occupancy is asked.
        mover.Receive(WalkPacket(dir: 0, fromX: doorX, fromY: doorY + 1));

        Assert.Equal(dest.x, mover.PlayerX);
        Assert.Equal(dest.y, mover.PlayerY);
    }

    /// <summary>A PvP ghost is blocked only by another GHOST, and no-clips through the living — the arena
    /// rule, carried into the lock with the rest of the occupancy predicate. Driven at the seam rather than
    /// through <c>HandleWalk</c> because reaching the ghost branch there needs a real PvP map, and the branch
    /// itself is the thing under test.</summary>
    [Fact]
    public void GhostMoverIsBlockedByGhostsAndNotByTheLiving()
    {
        var world = _fx.World;
        var (living, _) = _fx.Player("GhostLiving", GhostMap, 5, 4);
        var (ghost, _, _) = _fx.PlayerWith("GhostPeer", c => c.Hp = 0, GhostMap, 5, 6);
        var (mover, _, _) = _fx.PlayerWith("GhostMover", c => c.Hp = 0, GhostMap, 5, 5);
        Assert.False(living.IsDead);
        Assert.True(ghost.IsDead);

        // Onto the LIVING player's tile: allowed, and it commits.
        bool ontoLiving = mover.WithState(() => world.TryMovePlayer(
            mover, GhostMap, 5, 4,
            ghostMover: true, enforceOccupancy: true, otherwiseBlocked: false, out _));
        Assert.True(ontoLiving);
        Assert.Equal(4, mover.PlayerY);

        // Onto the other GHOST's tile: refused, with the ghost reason.
        BlockReason why = BlockReason.None;
        bool ontoGhost = mover.WithState(() =>
        {
            bool ok = world.TryMovePlayer(mover, GhostMap, 5, 6,
                                          ghostMover: true, enforceOccupancy: true, otherwiseBlocked: false, out var w);
            why = w;
            return ok;
        });
        Assert.False(ontoGhost);
        Assert.Equal(BlockReason.Ghost, why);
        Assert.Equal(4, mover.PlayerY);   // and it did not move
    }
}
