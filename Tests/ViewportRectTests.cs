using System.Buffers.Binary;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The geometry of the tick's viewport reconcile — phase <c>(3) viewports</c>, the phase that led 65 of the
/// 66 slow beats in the 400-player load hold (<c>briefs/reports/load-run-2.md</c>) and the one the rect
/// hoist in <c>Session.WorldApi.cs</c> cut.
///
/// <para>What these pin is not the hoist itself — a pure cost cut has no behaviour to pin — but the
/// arithmetic the hoist now carries in ONE place instead of recomputing per entity: the strict 17x15 rect a
/// <c>0x07</c>/<c>0x33</c> draw is accepted in, the wider 19x17 rect past which a drawn entity is really
/// despawned, and the overdraw band between them where a drawn entity is kept. Before the hoist those
/// bounds lived inside <c>InView</c> and were rebuilt for every entity at every pad; now they live in
/// <c>ViewRect</c> and are built once per sweep. Get the origin, the width, the height or the pad wrong in
/// that one place and every player's screen is wrong — silently, which is the bar <c>AGENTS.md</c> rule 3
/// sets for a test here.</para>
///
/// <para>All three facts drive the REAL tick (<c>World.TickOnceForTest</c> -> <c>FlushTick</c> ->
/// <c>ReconcileViews</c>) and assert on frames the recorder caught, not on internal state.</para>
///
/// <para>Hygiene, as in <c>MobAiTickTests</c>: the fixture's <c>World</c> is shared and has no teardown, so
/// every seated player is removed in a <c>finally</c>.</para>
/// </summary>
[Collection("world")]
public class ViewportRectTests
{
    private readonly SessionFixture _fx;

    public ViewportRectTests(SessionFixture fx) => _fx = fx;

    // Content-free maps (no registry row, no terrain, no warps, no spawns), one per test so nothing is shared.
    private const ushort RectMap = 60050, BandMap = 60051, PeerMap = 60052;

    // The viewer stands here in every case below. A content-free map measures 12x12, which is narrower and
    // shorter than the 17x15 viewport, so EdgeAwareAnchor CENTRES it: vx = x + (17-12)/2 = x+2 and
    // vy = y + (15-12)/2 = y+1. The rect origin is (X - vx, Y - vy) = (-2, -1), so from (5,10) the strict
    // rect is x in [-2,15) and y in [-1,14), and the drawn rect (pad 1) is x in [-3,16) and y in [-2,15).
    // Every coordinate below is read off those two rects; MapXs/MapYs are asserted in each test so a change
    // to the content-free map size fails loudly here rather than quietly moving the boundaries.
    private const ushort ViewerX = 5, ViewerY = 10;

    private Mob Parked(ushort map, ushort x, ushort y, string name)
    {
        // Parked FAR outside both rects first, so World.AddMob's own spawn broadcast cannot draw it and the
        // only thing that can put it on the wire is the tick's reconcile. Home follows it to wherever the
        // test moves it (see Move), so it never has a reason to walk.
        var mob = new Mob(_fx.World.AllocateMobId(), 1, x, y, name, 100);
        _fx.World.AddMob(map, mob);
        return mob;
    }

    /// <summary>Put a parked creature on a tile without giving it a reason to leave it: home moves with it,
    /// so the walk-home block has nothing to do and the beat is a pure reconcile.
    ///
    /// <para>UNDER THE WORLD LOCK, WITH THE MAP'S VIEW GENERATION BUMPED, because that is what a real mob
    /// step does: <c>MobAiTick.StepMobTo</c> is the world's only mob commit and it bumps
    /// <c>World.MapState.ViewGen</c> under <c>_lock</c> beside the two stores, which is how the tick's sweep
    /// skip knows the map changed. A helper that writes the tile directly bypasses both, and the tick then
    /// correctly decides nothing on this map moved and skips the sweep the fact is asserting on. Teleporting
    /// a creature by assignment was never a thing the world does; this makes the helper do what the world
    /// does instead of making the production path defend against it.</para></summary>
    private void Move(ushort map, Mob mob, ushort x, ushort y) =>
        _fx.World.UnderWorldLockForTest(() =>
        {
            mob.X = x; mob.Y = y;
            mob.HomeX = x; mob.HomeY = y;
            _fx.World.BumpViewGenUnderWorldLock(map);
        });

    /// <summary>Entity ids carried by the <c>0x07</c> creature-list frames in <paramref name="outbound"/>.
    /// Body is <c>count(u16)</c> then 12 bytes per entity with the id at +4, so a one-entity spawn (what
    /// <c>ShowMob</c> sends) puts its id at body offset 6.</summary>
    private static List<uint> SpawnedIds(RecordingOutbound outbound) =>
        outbound.BodiesOf(0x07)
                .SelectMany(b =>
                {
                    int n = BinaryPrimitives.ReadUInt16BigEndian(b);
                    var ids = new List<uint>(n);
                    for (int i = 0; i < n; i++) ids.Add(BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(2 + i * 12 + 4)));
                    return ids;
                })
                .ToList();

    /// <summary>The ids inside one 4.95 despawn body: a count byte then that many u32BE ids.</summary>
    private static List<uint> DespawnedIds(RecordingOutbound outbound) =>
        outbound.BodiesOf(0x0E)
                .SelectMany(b =>
                {
                    var ids = new List<uint>(b[0]);
                    for (int i = 0; i < b[0]; i++) ids.Add(BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(1 + i * 4)));
                    return ids;
                })
                .ToList();

    /// <summary>The strict rect, on all four of the boundaries a 12x12 map can express with unsigned tiles:
    /// a creature on the last column IN is drawn, the one column past it is not; the same one row down. The
    /// two that are not drawn sit in the overdraw band — inside the 19x17 the client renders — which is the
    /// case that distinguishes a wrong WIDTH or HEIGHT from a wrong PAD: a rect built with the show pad and
    /// the hide pad swapped draws them, a rect one tile too wide or tall draws one of them.
    ///
    /// <para>Falsified by narrowing the hoisted rect — <c>mx &lt; _ox + ViewW + pad</c> to
    /// <c>_ox + ViewW + pad - 1</c> in <c>ViewRect.Contains</c>: red with "the last column inside the strict
    /// rect must be drawn". Falsified again by widening it to <c>+ 1</c>: red with "the first column outside
    /// the strict rect must not be drawn".</para></summary>
    [Fact]
    public void TheSweepDrawsTheLastTileInsideTheStrictRectAndNotTheFirstOutsideIt()
    {
        var (watcher, outbound, character) = _fx.PlayerWith("RectWatcher", _ => { }, RectMap, ViewerX, ViewerY);
        try
        {
            Assert.Equal((12, 12), (character.MapXs, character.MapYs));   // the geometry this test is read off

            var lastColumnIn   = Parked(RectMap, 5, 40, "ColumnIn");
            var firstColumnOut = Parked(RectMap, 6, 40, "ColumnOut");
            var lastRowIn      = Parked(RectMap, 7, 40, "RowIn");
            var firstRowOut    = Parked(RectMap, 8, 40, "RowOut");

            Move(RectMap, lastColumnIn,   14, 10);   // x in [-2,15): 14 is the last column inside
            Move(RectMap, firstColumnOut, 15, 10);   // 15 is out of the strict rect, still inside the drawn 19x17
            Move(RectMap, lastRowIn,       5, 13);   // y in [-1,14): 13 is the last row inside
            Move(RectMap, firstRowOut,     5, 14);   // 14 is out of the strict rect, still inside the drawn 19x17

            outbound.Clear();
            _fx.World.TickOnceForTest();

            var spawned = SpawnedIds(outbound);
            Assert.True(spawned.Contains(lastColumnIn.Id),
                $"the last column inside the strict rect must be drawn (#{lastColumnIn.Id} at (14,10)); spawned: {string.Join(", ", spawned)}");
            Assert.True(spawned.Contains(lastRowIn.Id),
                $"the last row inside the strict rect must be drawn (#{lastRowIn.Id} at (5,13)); spawned: {string.Join(", ", spawned)}");
            Assert.False(spawned.Contains(firstColumnOut.Id),
                $"the first column outside the strict rect must not be drawn (#{firstColumnOut.Id} at (15,10))");
            Assert.False(spawned.Contains(firstRowOut.Id),
                $"the first row outside the strict rect must not be drawn (#{firstRowOut.Id} at (5,14))");
        }
        finally { _fx.World.LeaveMap(watcher, RectMap); }
    }

    /// <summary>The hysteresis band, which is the whole reason the rect is tested at TWO pads rather than
    /// one: a drawn creature that steps out of the strict 17x15 but is still inside the drawn 19x17 is KEPT
    /// (no 0x0E — the client is still rendering it), and only a step past the wider rect despawns it.
    ///
    /// <para>Falsified by giving the hide test the show pad — <c>view.Contains(m.X, m.Y, HidePad)</c> to
    /// <c>ShowPad</c> in <c>SyncMobs</c>: red with "a creature in the overdraw band must not be despawned".</para></summary>
    [Fact]
    public void ACreatureInTheOverdrawBandIsKeptAndOneBeyondItIsDespawned()
    {
        var (watcher, outbound, character) = _fx.PlayerWith("BandWatcher", _ => { }, BandMap, ViewerX, ViewerY);
        try
        {
            Assert.Equal((12, 12), (character.MapXs, character.MapYs));

            var loiterer = Parked(BandMap, 5, 40, "Loiterer");
            Move(BandMap, loiterer, 5, 13);                       // inside the strict rect
            outbound.Clear();
            _fx.World.TickOnceForTest();
            Assert.Contains(loiterer.Id, SpawnedIds(outbound));   // drawn, and now tracked

            Move(BandMap, loiterer, 5, 14);                       // y in [-2,15) with the hide pad: the overdraw band
            outbound.Clear();
            _fx.World.TickOnceForTest();
            Assert.False(DespawnedIds(outbound).Contains(loiterer.Id),
                $"a creature in the overdraw band must not be despawned (#{loiterer.Id} at (5,14))");

            Move(BandMap, loiterer, 5, 15);                       // past the drawn rect: really gone
            outbound.Clear();
            _fx.World.TickOnceForTest();
            Assert.True(DespawnedIds(outbound).Contains(loiterer.Id),
                $"a creature past the drawn rect must be despawned (#{loiterer.Id} at (5,15)); " +
                $"despawned: {string.Join(", ", DespawnedIds(outbound))}");
        }
        finally { _fx.World.LeaveMap(watcher, BandMap); }
    }

    /// <summary>The same rect, on the PEER half — <c>SyncPeers</c>, which is where the cost was: at 400
    /// players on one map the reconcile runs it 400 x 400 times a beat, and the hoist changed its signature
    /// (one rect built by <c>SyncPeers</c> and handed to every <c>ReconcilePeer</c>, instead of each call
    /// rebuilding the viewer's anchor twice). The rect a peer is tested against is the VIEWER's, so a hoist
    /// that handed the wrong one down would draw the wrong peers on every screen at once.
    ///
    /// <para>A peer standing on the last row inside the strict rect is drawn (0x33); one past the drawn rect
    /// is not. Both are seated far outside the view so <c>World.EnterMap</c>'s own draw cannot be what the
    /// assertion catches, then walked onto their tiles before the beat.</para>
    ///
    /// <para>Falsified by dropping the camera anchor from the hoisted rect — <c>CurrentView</c>'s
    /// <c>new ViewRect(_char.X - vx, _char.Y - vy)</c> to <c>new ViewRect(_char.X, _char.Y)</c>, which is
    /// the plausible slip when the origin is computed in one place instead of at every test: red with "a
    /// peer past the drawn rect must not be drawn (#5 at (5,15))". The same revert takes the two cases above
    /// red as well ("the first column outside the strict rect must not be drawn" and "a creature past the
    /// drawn rect must be despawned"), which is the point of building the rect once.</para></summary>
    [Fact]
    public void ThePeerSweepTestsPeersAgainstTheViewersRect()
    {
        var (watcher, outbound, character) = _fx.PlayerWith("PeerWatcher", _ => { }, PeerMap, ViewerX, ViewerY);
        var (near, _, nearChar) = _fx.PlayerWith("PeerNear", _ => { }, PeerMap, 5, 40);
        var (far, _, farChar)   = _fx.PlayerWith("PeerFar",  _ => { }, PeerMap, 6, 40);
        try
        {
            Assert.Equal((12, 12), (character.MapXs, character.MapYs));

            nearChar.X = 5; nearChar.Y = 13;   // the last row inside the watcher's strict rect
            farChar.X = 5;  farChar.Y = 15;    // past the watcher's drawn rect

            outbound.Clear();
            _fx.World.TickOnceForTest();

            var drawn = outbound.BodiesOf(0x33)
                                .Select(b => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(5)))
                                .ToList();
            Assert.True(drawn.Contains(near.PlayerId),
                $"a peer on the last row inside the strict rect must be drawn (#{near.PlayerId} at (5,13)); drawn: {string.Join(", ", drawn)}");
            Assert.False(drawn.Contains(far.PlayerId),
                $"a peer past the drawn rect must not be drawn (#{far.PlayerId} at (5,15))");
        }
        finally
        {
            _fx.World.LeaveMap(watcher, PeerMap);
            _fx.World.LeaveMap(near, PeerMap);
            _fx.World.LeaveMap(far, PeerMap);
        }
    }
}
