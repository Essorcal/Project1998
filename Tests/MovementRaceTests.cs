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
/// test.</para>
/// </summary>
[Collection("world")]
public class MovementRaceTests
{
    private readonly SessionFixture _fx;

    public MovementRaceTests(SessionFixture fx) => _fx = fx;

    /// <summary>Map ids with no content behind them, one per test. See the class doc for why they are not
    /// shared.</summary>
    private const ushort RaceMap = 60000, LivingMap = 60001, GhostMap = 60002, DeadMap = 60003, ReasonMap = 60004;

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
