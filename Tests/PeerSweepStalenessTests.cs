using System.Buffers.Binary;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Tests;

/// <summary>
/// What a DEFERRED send is allowed to be stale about, which is nothing the drawn-set state has since
/// contradicted.
///
/// <para>The batched sweep (<c>SyncPeers</c>, <c>Session.WorldApi.cs</c>) decides for every peer under one
/// acquisition and sends afterwards. That put a new interval on the board: peer B's decision is made, and
/// then the send pass can BLOCK on peer A's <c>Snapshot()</c> — the subject's state monitor, held by some
/// other thread — while B's decision sits in the buffer waiting to go out. The per-peer shape never had that
/// interval, because it blocked on A before B was ever decided. PR #245's reviewer reproduced the
/// consequence (finding F1, HIGH): a walk reconcile completes during the block, redraws B correctly, and the
/// parked pass then sends B's older despawn without revalidation. The client loses a peer the server still
/// records as drawn, and because the sets say "drawn" no later sweep repairs it.</para>
///
/// <para>These two facts are that schedule and its mirror. Each parks a real sweep at the real blocking
/// point — another thread holding the first peer's state monitor, the sweep waiting inside the real
/// <c>ShowPlayer</c> -&gt; <c>Snapshot</c> — completes the viewer's movement and its reconciles while it
/// waits, and then lets the stale pass in. The first is the reviewer's own probe
/// (<c>reviews/PR245-evidence/Pr245ReviewTests.cs</c>), kept as the regression; the second is the mirror it
/// names, a stale SHOW arriving after a completed reconcile despawned that peer, which leaves a ghost the
/// server does not know it drew.</para>
///
/// <para>The shape they pin is the revalidation: the decide pass mutates the sets and stamps each decision,
/// and the send pass re-takes <c>_viewLock</c> immediately before each frame and drops any decision the sets
/// no longer agree with. Remove that check and both go red; that is the falsification.</para>
///
/// <para>Geometry: everyone stands on a 100x100 map, so <c>EdgeAwareAnchor</c> takes the plain follow branch
/// and the anchor is (8,7) — the strict rect is x in [X-8, X+9) and the drawn rect (pad 1) is x in
/// [X-9, X+10). A peer at x=30 is inside both for a viewer at x=22, in the overdraw band at x=21, and
/// outside both at x=20. That is what makes a stale decision visible on the wire.</para>
///
/// <para>Lock order, and why these tests are not themselves the bug: the blocking peer is seated BEFORE the
/// viewer, so its <c>StateRank</c> is lower and the test thread's "hold the blocker, then enter the viewer"
/// is the ascending order rule 2 requires (<c>Session.State.cs</c>). Each fact asserts that ranking rather
/// than assuming it.</para>
///
/// <para>Hygiene, as in <c>ViewportRectStalenessTests</c>: the fixture's <c>World</c> is shared and has no
/// teardown, so every seated player is removed in a <c>finally</c>.</para>
/// </summary>
[Collection("world")]
public class PeerSweepStalenessTests
{
    private readonly SessionFixture _fx;
    private readonly ITestOutputHelper _out;

    public PeerSweepStalenessTests(SessionFixture fx, ITestOutputHelper output) { _fx = fx; _out = output; }

    // Content-free maps, one per fact so nothing is shared. The character hook widens them to 100x100.
    private const ushort StaleDespawnMap = 60101, StaleShowMap = 60102, RedrawnMap = 60103;

    private static void Wide(Character c) { c.MapXs = 100; c.MapYs = 100; }

    /// <summary>The recorded frames for ONE entity, in order, as "0x33"/"0x0E" — a <c>0x33</c> look carries
    /// its id at body offset 5 (x u16, y u16, dir u8, then the id u32BE), a 4.95 <c>0x0E</c> despawn carries
    /// a count byte and then its ids.</summary>
    private static string[] FramesFor(RecordingOutbound outbound, uint id)
    {
        var seq = new List<string>();
        foreach (var frame in outbound.Frames)
        {
            byte op = frame[3];
            if (op != 0x33 && op != 0x0E) continue;
            byte[] body = TkCrypt.Crypt(frame[5..], frame[4], TkCrypt.LoginKey);
            if (op == 0x33)
            {
                if (BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(5)) == id) seq.Add("0x33");
            }
            else
                for (int i = 0; i < body[0]; i++)
                    if (BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1 + i * 4)) == id) seq.Add("0x0E");
        }
        return seq.ToArray();
    }

    /// <summary>A send pass parked on the FIRST peer's <c>Snapshot()</c> must not despawn a LATER peer that a
    /// completed walk reconcile has redrawn — PR #245's reviewer's F1, and the regression this file exists
    /// for.
    ///
    /// <para>The schedule: the viewer has the edge peer drawn and steps back to x=20, where that peer is
    /// outside the drawn rect, so the next sweep decides a despawn for it. The blocker peer is forgotten
    /// first, so the same sweep decides a SHOW for it and the send pass blocks inside the real
    /// <c>ShowPlayer</c> -&gt; <c>Snapshot</c> while the test thread holds the blocker's monitor. With the
    /// pass parked, the viewer walks 20 -&gt; 21 -&gt; 22 and reconciles each step; at x=22 the edge peer is
    /// back inside the strict rect, is drawn (0x33) and is tracked again. The blocker's monitor is then
    /// released and the parked pass finishes.</para>
    ///
    /// <para>On the reviewed head <c>ca89709</c> it sent its saved despawn anyway — 0x33 then 0x0E for that
    /// peer, with no repair on two later sweeps, because <c>_shownPeers</c> says it is drawn. Red with "a
    /// completed walk's redrawn peer must not be despawned by a parked send pass".</para></summary>
    [Fact]
    public void AParkedSendPassDoesNotDespawnAPeerACompletedWalkRedrew()
    {
        var (blocker, _, _) = _fx.PlayerWith("ParkedBlocker", Wide, StaleDespawnMap, 20, 19);
        var (viewer, outbound, character) = _fx.PlayerWith("ParkedViewer", Wide, StaleDespawnMap, 22, 20);
        var (edge, _, _) = _fx.PlayerWith("ParkedEdge", Wide, StaleDespawnMap, 30, 20);
        Exception? failed = null;
        Thread? sweep = null;
        try
        {
            Assert.Equal((100, 100), (character.MapXs, character.MapYs));   // the geometry this is read off
            // Ascending StateRank: holding the blocker and then entering the viewer is rule 2's order, so the
            // schedule below cannot be a lock-order violation of the test's own making.
            Assert.True(blocker.StateRank < viewer.StateRank, "the blocker must be seated before the viewer");

            var peers = new[] { new PeerTile(blocker, 20, 19), new PeerTile(edge, 30, 20) };
            viewer.SyncPeers(peers);                                         // settle: the edge peer is drawn at x=22
            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 21, 20); viewer.SyncPeers(peers); });
            viewer.DespawnEntity(blocker.PlayerId);                          // so the sweep's first peer needs a real Snapshot
            viewer.WithState(() => _fx.World.SetPlayerPosition(viewer, 20, 20));   // x=20: the edge peer is past the drawn rect

            outbound.Clear();
            blocker.WithState(() =>
            {
                sweep = new Thread(() =>
                {
                    try { viewer.SyncPeers(peers); }
                    catch (Exception ex) { failed = ex; }
                }) { IsBackground = true };
                sweep.Start();
                // Parked inside ShowPlayer -> blocker.Snapshot(), with the edge peer's despawn already decided.
                Assert.True(SpinWait.SpinUntil(
                    () => sweep.ThreadState.HasFlag(System.Threading.ThreadState.WaitSleepJoin), 5000),
                    "the sweep never blocked on the first peer's Snapshot");

                viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 21, 20); viewer.SyncPeers(peers); });
                viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 22, 20); viewer.SyncPeers(peers); });
            });

            Assert.True(sweep!.Join(5000), "the sweep thread never finished");
            Assert.Null(failed);

            var seq = FramesFor(outbound, edge.PlayerId);
            _out.WriteLine("edge frames after the completed walk: " + string.Join(",", seq));
            outbound.Clear();
            viewer.SyncPeers(peers);
            viewer.SyncPeers(peers);
            _out.WriteLine("repair draws in two later sweeps: " + FramesFor(outbound, edge.PlayerId).Length);

            Assert.True(!seq.Contains("0x0E"),
                $"a completed walk's redrawn peer must not be despawned by a parked send pass " +
                $"(#{edge.PlayerId} at (30,20), viewer back at (22,20), strict rect x in [14,31)); " +
                $"frames for that peer: {string.Join(",", seq)}");
        }
        finally
        {
            if (sweep is not null) sweep.Join(5000);
            foreach (var s in new[] { viewer, blocker, edge }) _fx.World.LeaveMap(s, StaleDespawnMap);
        }
    }

    /// <summary>The mirror, and the same interval: a send pass parked on the FIRST peer's <c>Snapshot()</c>
    /// must not draw a LATER peer that a completed walk reconcile has despawned.
    ///
    /// <para>The schedule is the first fact's, run the other way round. Both peers are forgotten first, so
    /// the sweep at x=22 decides a SHOW for each; the send pass blocks on the blocker's monitor with the edge
    /// peer's show still in the buffer. The viewer then walks 22 -&gt; 21 -&gt; 20 and reconciles, and at
    /// x=20 the edge peer is past the drawn rect, so it is correctly despawned (0x0E) and dropped from
    /// <c>_shownPeers</c>. The parked pass is then released.</para>
    ///
    /// <para>Without the revalidation it sends the saved 0x33 afterwards: the client draws a peer that is off
    /// screen and that the server no longer tracks, so nothing will ever despawn it — a ghost, and the exact
    /// mirror of F1. Red with "a completed walk's despawned peer must not be redrawn by a parked send
    /// pass".</para></summary>
    [Fact]
    public void AParkedSendPassDoesNotRedrawAPeerACompletedWalkDespawned()
    {
        var (blocker, _, _) = _fx.PlayerWith("GhostBlocker", Wide, StaleShowMap, 20, 19);
        var (viewer, outbound, character) = _fx.PlayerWith("GhostViewer", Wide, StaleShowMap, 22, 20);
        var (edge, _, _) = _fx.PlayerWith("GhostEdge", Wide, StaleShowMap, 30, 20);
        Exception? failed = null;
        Thread? sweep = null;
        try
        {
            Assert.Equal((100, 100), (character.MapXs, character.MapYs));
            Assert.True(blocker.StateRank < viewer.StateRank, "the blocker must be seated before the viewer");

            var peers = new[] { new PeerTile(blocker, 20, 19), new PeerTile(edge, 30, 20) };
            viewer.DespawnEntity(blocker.PlayerId);      // both untracked: the sweep decides a show for each
            viewer.DespawnEntity(edge.PlayerId);

            outbound.Clear();
            blocker.WithState(() =>
            {
                sweep = new Thread(() =>
                {
                    try { viewer.SyncPeers(peers); }
                    catch (Exception ex) { failed = ex; }
                }) { IsBackground = true };
                sweep.Start();
                // Parked inside ShowPlayer -> blocker.Snapshot(), with the edge peer's show already decided.
                Assert.True(SpinWait.SpinUntil(
                    () => sweep.ThreadState.HasFlag(System.Threading.ThreadState.WaitSleepJoin), 5000),
                    "the sweep never blocked on the first peer's Snapshot");

                viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 21, 20); viewer.SyncPeers(peers); });
                viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 20, 20); viewer.SyncPeers(peers); });
            });

            Assert.True(sweep!.Join(5000), "the sweep thread never finished");
            Assert.Null(failed);

            var seq = FramesFor(outbound, edge.PlayerId);
            _out.WriteLine("edge frames after the completed walk: " + string.Join(",", seq));

            Assert.True(seq.LastOrDefault() != "0x33",
                $"a completed walk's despawned peer must not be redrawn by a parked send pass " +
                $"(#{edge.PlayerId} at (30,20), viewer back at (20,20), drawn rect x in [11,30)); " +
                $"frames for that peer: {string.Join(",", seq)}");
        }
        finally
        {
            if (sweep is not null) sweep.Join(5000);
            foreach (var s in new[] { viewer, blocker, edge }) _fx.World.LeaveMap(s, StaleShowMap);
        }
    }

    /// <summary>And the case the membership test alone cannot catch, which is why each decision carries a
    /// stamp: a parked SHOW must not go out after another reconcile despawned that peer AND drew it again,
    /// because the peer is back in <c>_shownPeers</c> and the frame would be a second 0x33 for one draw.
    ///
    /// <para>Same schedule as the mirror above, with the walk continued: 22 -&gt; 21 -&gt; 20 despawns the
    /// edge peer (0x0E), then 21 -&gt; 22 draws it again (0x33) and tracks it. The parked pass is released
    /// holding a show whose id IS drawn — but it is not the decision that drew it, and its stamp is no longer
    /// the latest for that id, so it is dropped.</para>
    ///
    /// <para>Red with "one draw must put one frame on the wire" if the stamp comparison is dropped from
    /// <c>PeerSendStillCurrentUnderViewLock</c> and the membership test is left, and red on the reviewed head
    /// <c>ca89709</c>, which revalidates nothing at all.</para></summary>
    [Fact]
    public void AParkedShowDoesNotRedrawAPeerAnotherReconcileAlreadyRedrew()
    {
        var (blocker, _, _) = _fx.PlayerWith("RedrawBlocker", Wide, RedrawnMap, 20, 19);
        var (viewer, outbound, character) = _fx.PlayerWith("RedrawViewer", Wide, RedrawnMap, 22, 20);
        var (edge, _, _) = _fx.PlayerWith("RedrawEdge", Wide, RedrawnMap, 30, 20);
        Exception? failed = null;
        Thread? sweep = null;
        try
        {
            Assert.Equal((100, 100), (character.MapXs, character.MapYs));
            Assert.True(blocker.StateRank < viewer.StateRank, "the blocker must be seated before the viewer");

            var peers = new[] { new PeerTile(blocker, 20, 19), new PeerTile(edge, 30, 20) };
            viewer.DespawnEntity(blocker.PlayerId);
            viewer.DespawnEntity(edge.PlayerId);

            outbound.Clear();
            blocker.WithState(() =>
            {
                sweep = new Thread(() =>
                {
                    try { viewer.SyncPeers(peers); }
                    catch (Exception ex) { failed = ex; }
                }) { IsBackground = true };
                sweep.Start();
                Assert.True(SpinWait.SpinUntil(
                    () => sweep.ThreadState.HasFlag(System.Threading.ThreadState.WaitSleepJoin), 5000),
                    "the sweep never blocked on the first peer's Snapshot");

                // Out of the drawn rect and back into the strict one: despawned, then drawn again by a
                // reconcile that is not the one the parked pass is holding.
                viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 21, 20); viewer.SyncPeers(peers); });
                viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 20, 20); viewer.SyncPeers(peers); });
                viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 21, 20); viewer.SyncPeers(peers); });
                viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 22, 20); viewer.SyncPeers(peers); });
            });

            Assert.True(sweep!.Join(5000), "the sweep thread never finished");
            Assert.Null(failed);

            var seq = FramesFor(outbound, edge.PlayerId);
            _out.WriteLine("edge frames after the completed walk: " + string.Join(",", seq));

            Assert.True(seq.SequenceEqual(new[] { "0x0E", "0x33" }),
                $"one draw must put one frame on the wire: the parked pass is holding a show for a peer that " +
                $"another reconcile despawned and drew again (#{edge.PlayerId}), so its frame must be dropped; " +
                $"frames for that peer: {string.Join(",", seq)}");
        }
        finally
        {
            if (sweep is not null) sweep.Join(5000);
            foreach (var s in new[] { viewer, blocker, edge }) _fx.World.LeaveMap(s, RedrawnMap);
        }
    }
}
