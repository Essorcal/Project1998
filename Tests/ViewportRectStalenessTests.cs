using System.Collections;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// What the once-per-sweep view rect (<c>Session.WorldApi.cs</c>, <c>ViewRect</c> / <c>CurrentView</c>) is
/// allowed to be stale about, which is nothing a decision depends on.
///
/// <para>Taking the rect once a sweep instead of once per entity test is a cost cut worth real beats at 400
/// players, but it introduced a lifetime the per-entity shape never had: a sweep that read the rect and then
/// waited on <c>_viewLock</c> could decide on a tile the viewer had already left. PR #240's reviewer
/// reproduced the consequence — a delayed mob sweep despawning a mob that a completed walk reconcile had
/// just correctly drawn, then redrawing it on the next beat, a one-beat flicker of every entity near the
/// hide band whenever the tick sweep contends with a walk (finding F1, HIGH). These facts are that reviewer's
/// probes, kept as the regression: each parks a real sweep at a real blocking point, completes the viewer's
/// movement and its reconcile while it waits, and then lets the stale sweep in.</para>
///
/// <para>The shape they pin is the generation handshake: <c>Session.SetPositionUnderWorldLock</c> bumps a
/// per-session counter after writing the tile, the rect carries the counter's value from before it read the
/// tile, and every decision re-compares it under the <c>_viewLock</c> it is made under — keeping the rect
/// when the viewer has not moved (the common case, one anchor computation a sweep) and rebuilding it when it
/// has. Drop the compare and all three facts below go red; that is the falsification.</para>
///
/// <para>Geometry: both viewers stand on a 100x100 map, which is wider and taller than the 17x15 viewport, so
/// <c>EdgeAwareAnchor</c> takes the plain follow branch and the anchor is (8,7). The rect origin is therefore
/// (X-8, Y-7): from x=20 the strict rect is x in [12,29) and the drawn rect (pad 1) is x in [11,30); from
/// x=22 they are [14,31) and [13,32). An entity at x=30 is outside BOTH rects for a viewer at 20 and inside
/// both for a viewer at 22, which is what makes a stale despawn visible.</para>
///
/// <para>Hygiene, as in <c>ViewportRectTests</c>: the fixture's <c>World</c> is shared and has no teardown, so
/// every seated player is removed in a <c>finally</c>.</para>
/// </summary>
[Collection("world")]
public class ViewportRectStalenessTests
{
    private readonly SessionFixture _fx;

    public ViewportRectStalenessTests(SessionFixture fx) => _fx = fx;

    // Content-free maps, one per test so nothing is shared. The character hook widens them to 100x100.
    private const ushort MobMap = 60060, PeerMap = 60061, MidSweepMap = 60062;

    private static void Wide(Character c) { c.MapXs = 100; c.MapYs = 100; }

    /// <summary>The ids inside one 4.95 despawn body: a count byte then that many u32BE ids.</summary>
    private static int DespawnCount(RecordingOutbound outbound) => outbound.BodiesOf(0x0E).Count;

    /// <summary>A one-element peer list that runs <paramref name="before"/> at the moment the sweep starts
    /// enumerating — after it has taken its rect, before it reconciles anything. The reviewer's interleaving
    /// harness: it needs no production seam and no scheduling luck, because the sweep itself calls it.</summary>
    private sealed class Interleaved<T>(T item, Action before) : IReadOnlyList<T>
    {
        public int Count => 1;
        public T this[int i] => item;
        public IEnumerator<T> GetEnumerator() { before(); yield return item; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A delayed MOB sweep must not undo a completed walk reconcile — finding F1, the reviewer's
    /// <c>MobRectReadBeforeLockCanUndoCompletedMovement</c>, and the regression this test exists for.
    ///
    /// <para>The owning thread holds the viewport lock, so the sweep it starts on a second thread parks at
    /// <c>EnterView</c> exactly where a tick sweep parks behind a walk. While it waits, the owner walks the
    /// viewer 20 -> 21 -> 22 through the real position seam and reconciles each step; the second reconcile
    /// correctly draws the mob at x=30, now inside the strict rect. The waiting sweep is then let in. On the
    /// reviewed head it despawned that mob (its rect was the one for x=20, whose drawn rect ends at 30) and
    /// the next beat drew it again: 0x07, 0x0E, 0x07 where the per-entity shape sent one 0x07.</para>
    ///
    /// <para>Falsified by dropping the re-anchor — <c>view = Current(view)</c> in <c>SyncMobs</c>' loop, or
    /// moving <c>var view = CurrentView()</c> back above <c>EnterView()</c>: red with "a completed walk's
    /// visible mob must not be despawned by an older sweep".</para></summary>
    [Fact]
    public void ADelayedMobSweepDoesNotUndoACompletedWalkReconcile()
    {
        var (viewer, outbound, character) = _fx.PlayerWith("StaleMobViewer", Wide, MobMap, 20, 20);
        var mob = new Mob(_fx.World.AllocateMobId(), 1, 30, 20, "StaleEdge", 100);
        Exception? failed = null;
        Thread? sweep = null;
        try
        {
            Assert.Equal((100, 100), (character.MapXs, character.MapYs));   // the geometry this is read off
            outbound.Clear();

            viewer.WithState(() => viewer.UnderViewLockForTest(() =>
            {
                sweep = new Thread(() =>
                {
                    try { viewer.SyncMobs(new[] { mob }); }
                    catch (Exception ex) { failed = ex; }
                });
                sweep.Start();
                // Parked on _viewLock: where a tick sweep waits when a walk reconcile got in first.
                Assert.True(SpinWait.SpinUntil(
                    () => sweep.ThreadState.HasFlag(System.Threading.ThreadState.WaitSleepJoin), 5000),
                    "the sweep thread never reached the viewport lock");

                _fx.World.SetPlayerPosition(viewer, 21, 20);
                viewer.SyncMobs(new[] { mob });          // x=21: strict [13,30), the mob at 30 is still out
                _fx.World.SetPlayerPosition(viewer, 22, 20);
                viewer.SyncMobs(new[] { mob });          // x=22: strict [14,31), the mob is in view and drawn

                Assert.Single(outbound.BodiesOf(0x07));
            }));

            Assert.True(sweep!.Join(5000), "the sweep thread never finished");
            Assert.Null(failed);

            Assert.True(DespawnCount(outbound) == 0,
                $"a completed walk's visible mob must not be despawned by an older sweep " +
                $"(#{mob.Id} at (30,20), viewer at (22,20), drawn rect x in [13,32)); " +
                $"despawn frames: {DespawnCount(outbound)}");
        }
        finally { _fx.World.LeaveMap(viewer, MobMap); }
    }

    /// <summary>The same fact on the PEER half, where the decision is per entity under the lock rather than
    /// one loop inside it: a peer sweep parked before its peer must not despawn a peer that a completed walk
    /// reconcile drew while it waited.
    ///
    /// <para>The sweep is parked by its own peer list, which blocks on first enumeration — after
    /// <c>SyncPeers</c> has taken the sweep's rect at x=20 and before any peer is reconciled. The owner then
    /// walks 20 -> 21 -> 22 and reconciles, drawing the peer at x=30 (0x33), and releases the sweep. With the
    /// rect the sweep is holding, that peer is outside the drawn rect and would be despawned.</para>
    ///
    /// <para>Falsified by dropping <c>view = Current(view)</c> from <c>ReconcilePeer</c>: red with "a
    /// completed walk's visible peer must not be despawned by an older sweep".</para></summary>
    [Fact]
    public void ADelayedPeerSweepDoesNotUndoACompletedWalkReconcile()
    {
        var (viewer, outbound, character) = _fx.PlayerWith("StalePeerViewer", Wide, PeerMap, 20, 20);
        var (peer, _, _) = _fx.PlayerWith("StalePeerSubject", Wide, PeerMap, 30, 20);
        using var parked = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        Exception? failed = null;
        Thread? sweep = null;
        try
        {
            Assert.Equal((100, 100), (character.MapXs, character.MapYs));
            outbound.Clear();

            var gated = new Interleaved<PeerTile>(new PeerTile(peer, 30, 20), () =>
            {
                parked.Set();
                Assert.True(release.Wait(5000), "the test never released the parked sweep");
            });
            sweep = new Thread(() =>
            {
                try { viewer.SyncPeers(gated); }
                catch (Exception ex) { failed = ex; }
            });
            sweep.Start();
            Assert.True(parked.Wait(5000), "the peer sweep never reached its peer list");

            // Its rect is the one for x=20 (drawn rect x in [11,30)); the peer at 30 is outside it.
            viewer.WithState(() => _fx.World.SetPlayerPosition(viewer, 21, 20));
            viewer.SyncPeers(new[] { new PeerTile(peer, 30, 20) });    // strict [13,30): still not in view
            viewer.WithState(() => _fx.World.SetPlayerPosition(viewer, 22, 20));
            viewer.SyncPeers(new[] { new PeerTile(peer, 30, 20) });    // strict [14,31): drawn

            Assert.Single(outbound.BodiesOf(0x33));

            release.Set();
            Assert.True(sweep.Join(5000), "the peer sweep never finished");
            Assert.Null(failed);

            Assert.True(DespawnCount(outbound) == 0,
                $"a completed walk's visible peer must not be despawned by an older sweep " +
                $"(#{peer.PlayerId} at (30,20), viewer at (22,20), drawn rect x in [13,32)); " +
                $"despawn frames: {DespawnCount(outbound)}");
        }
        finally
        {
            release.Set();
            _fx.World.LeaveMap(viewer, PeerMap);
            _fx.World.LeaveMap(peer, PeerMap);
        }
    }

    /// <summary>The other half of the same root, the reviewer's F2: a peer that comes into view because WE
    /// stepped, after the sweep began, is drawn on THAT sweep and not one sweep later.
    ///
    /// <para>The viewer's step lands between <c>SyncPeers</c> taking its rect and the peer being yielded to
    /// it — the interleaving the per-entity shape handled because it re-read the anchor at the test. Viewer
    /// 20 -> 21 makes the strict rect [13,30), and the peer standing at x=29 enters it. On the reviewed head
    /// this sweep drew nothing and the peer appeared on the following sweep.</para>
    ///
    /// <para>Falsified by dropping <c>view = Current(view)</c> from <c>ReconcilePeer</c>: red with "a peer
    /// the viewer stepped into view of must be drawn by the sweep that saw the step".</para></summary>
    [Fact]
    public void APeerSteppedIntoViewMidSweepIsDrawnOnThatSweep()
    {
        var (viewer, outbound, character) = _fx.PlayerWith("MidSweepViewer", Wide, MidSweepMap, 20, 20);
        var (peer, _, _) = _fx.PlayerWith("MidSweepSubject", Wide, MidSweepMap, 29, 20);
        try
        {
            Assert.Equal((100, 100), (character.MapXs, character.MapYs));
            outbound.Clear();

            viewer.SyncPeers(new Interleaved<PeerTile>(
                new PeerTile(peer, 29, 20),
                () => viewer.WithState(() => _fx.World.SetPlayerPosition(viewer, 21, 20))));

            Assert.True(outbound.BodiesOf(0x33).Count == 1,
                $"a peer the viewer stepped into view of must be drawn by the sweep that saw the step " +
                $"(#{peer.PlayerId} at (29,20), viewer now at (21,20), strict rect x in [13,30)); " +
                $"draw frames: {outbound.BodiesOf(0x33).Count}");

            // And it is drawn once, not once per sweep: the first sweep's show is tracked like any other.
            viewer.SyncPeers(new[] { new PeerTile(peer, 29, 20) });
            Assert.Single(outbound.BodiesOf(0x33));
        }
        finally
        {
            _fx.World.LeaveMap(viewer, MidSweepMap);
            _fx.World.LeaveMap(peer, MidSweepMap);
        }
    }
}
