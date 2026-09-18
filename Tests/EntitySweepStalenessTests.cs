using System.Buffers.Binary;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Tests;

/// <summary>
/// What a DEFERRED MOB or GROUND-ITEM send is allowed to be stale about, which is nothing the drawn-set state
/// has since contradicted — the twin of <see cref="PeerSweepStalenessTests"/>, which pins the same thing for
/// peers.
///
/// <para>PR #245's reviewer found the hazard on the peer sweep (finding F1, HIGH): decide for every entity
/// under one acquisition and send afterwards, and a send pass can be parked mid-list while a walk reconcile on
/// the viewer's own thread completes and decides the OPPOSITE about an entity still sitting in the buffer. The
/// parked pass then sends its older frame, and because the sets say the opposite no later sweep repairs it —
/// a peer lost from the client while the server records it as drawn, or a ghost drawn that the server does not
/// know about. The mob and item sweeps defer their sends as of this PR, so they have the same interval.</para>
///
/// <para><b>Where these park, and why it is a different point from the peer facts.</b> The peer send pass
/// blocks inside the real <c>ShowPlayer</c> -&gt; <c>Snapshot</c>, on the subject's state monitor. Nothing on
/// the mob or item send path enters another session at all — <c>ShowMob</c>, <c>ShowGroundItem</c> and
/// <c>SendDespawn</c> build a packet and hand it to the outbound channel — so there is no monitor to park on
/// and the real interval is a scheduling one. These facts make it deterministic by parking in the seam that
/// already exists for this: the session's <see cref="IOutbound"/>. A recorder that blocks on the first frame
/// the sweep thread hands it puts the pass exactly where a descheduled thread would be, between the first
/// entity's frame and the rest of the buffer, with the real sweep, the real sets and the real position seam on
/// the other side.</para>
///
/// <para>The shape they pin is the revalidation: the decide pass mutates the sets and stamps each decision,
/// and the send pass re-takes <c>_viewLock</c> immediately before each frame and drops any decision the sets
/// no longer agree with. Remove that check and all three go red; that is the falsification.</para>
///
/// <para>Geometry: everyone stands on a 100x100 map, so <c>EdgeAwareAnchor</c> takes the plain follow branch
/// and the anchor is (8,7) — the strict rect is x in [X-8, X+9) and the drawn rect (pad 1) is x in
/// [X-9, X+10). An entity at x=30 is inside both for a viewer at x=22, in the overdraw band at x=21, and
/// outside both at x=20. That is what makes a stale decision visible on the wire.</para>
///
/// <para>Hygiene: the fixture's <c>World</c> is shared and has no teardown, so every seated player is removed
/// in a <c>finally</c>, and the parked thread is joined there too.</para>
/// </summary>
[Collection("world")]
public class EntitySweepStalenessTests
{
    private readonly SessionFixture _fx;
    private readonly ITestOutputHelper _out;

    public EntitySweepStalenessTests(SessionFixture fx, ITestOutputHelper output) { _fx = fx; _out = output; }

    // Content-free maps, one per fact so nothing is shared. The character is widened to 100x100.
    private const ushort StaleMobDespawnMap = 60120, StaleMobShowMap = 60121, StaleItemMap = 60122;
    // PR #246's F1: a parked show invalidated by the viewer walking, one map per fact.
    private const ushort BandMobMap = 60123, BandPeerMap = 60124, BandItemMap = 60125;
    // PR #246's F2: a parked RE-ASSERT show, dropped, must not un-draw a draw the client really received.
    private const ushort ReassertMobMap = 60126, ReassertPeerMap = 60127;

    /// <summary>An outbound that records every frame and, once armed, blocks the ARMED THREAD inside the first
    /// frame it hands over. That is the send pass's park: the world tick's sweep stops between its first
    /// entity's frame and the rest of its buffer while the test thread — which is not armed, so its own frames
    /// go straight through — walks the viewer and reconciles.
    ///
    /// <para>Thread-safe, unlike <see cref="RecordingOutbound"/>, because two threads genuinely send through
    /// it here.</para></summary>
    private sealed class GatedRecorder : IOutbound
    {
        private readonly List<byte[]> _frames = new();
        private int _armedThread;
        private bool _fired;                       // only ever touched on the armed thread

        internal readonly ManualResetEventSlim Parked = new(false);
        internal readonly ManualResetEventSlim Release = new(false);

        public string Remote => "gated";
        public int Capacity => int.MaxValue;
        public int QueueDepth => 0;
        public void Close() { }

        internal void ArmForThisThread() => Volatile.Write(ref _armedThread, Environment.CurrentManagedThreadId);

        internal byte[][] Snapshot() { lock (_frames) return _frames.ToArray(); }
        internal void Clear() { lock (_frames) _frames.Clear(); }

        public bool Send(byte[] frame)
        {
            lock (_frames) _frames.Add(frame);
            if (Volatile.Read(ref _armedThread) == Environment.CurrentManagedThreadId && !_fired)
            {
                _fired = true;
                Parked.Set();
                Release.Wait(5000);
            }
            return true;
        }
    }

    /// <summary>A session on a 100x100 content-free map whose outbound is the gate above. Mirrors
    /// <c>SessionFixture.PlayerWith</c>, which builds its own recorder and so cannot be handed one.</summary>
    private (Session session, GatedRecorder outbound, Character character) GatedPlayer(string name, ushort map, ushort x, ushort y)
    {
        var character = new Character
        {
            SchemaVersion = Character.CurrentSchemaVersion,
            Id = _fx.World.AllocatePlayerId(),
            Name = name,
            Map = map,
            X = x,
            Y = y,
            MapXs = 100,
            MapYs = 100,
        };
        var outbound = new GatedRecorder();
        var session = new Session(outbound, 2005, _fx.Store, _fx.World, character);
        _fx.World.EnterMap(session, map);
        outbound.Clear();
        return (session, outbound, character);
    }

    /// <summary>The recorded frames for ONE entity, in order, as "0x07"/"0x33"/"0x0E". A <c>0x07</c> creature
    /// list carries <c>count(u16BE)</c> then 12-byte entries whose id is at entry offset 4; a <c>0x33</c> look
    /// carries its id at body offset 5 (x u16, y u16, dir u8, then the id u32BE); a 4.95 <c>0x0E</c> despawn
    /// carries a count byte and then its ids.</summary>
    private static string[] FramesFor(GatedRecorder outbound, uint id)
    {
        var seq = new List<string>();
        foreach (var frame in outbound.Snapshot())
        {
            byte op = frame[3];
            if (op != 0x07 && op != 0x0E && op != 0x33) continue;
            byte[] body = TkCrypt.Crypt(frame[5..], frame[4], TkCrypt.LoginKey);
            if (op == 0x07)
            {
                int count = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(0));
                for (int i = 0; i < count; i++)
                    if (BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(2 + i * 12 + 4)) == id) seq.Add("0x07");
            }
            else if (op == 0x33)
            {
                if (BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(5)) == id) seq.Add("0x33");
            }
            else
                for (int i = 0; i < body[0]; i++)
                    if (BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1 + i * 4)) == id) seq.Add("0x0E");
        }
        return seq.ToArray();
    }

    /// <summary>The character hook that widens a content-free map to 100x100, for the peers this file seats
    /// through the fixture rather than through <see cref="GatedPlayer"/>.</summary>
    private static void Wide(Character c) { c.MapXs = 100; c.MapYs = 100; }

    private Mob MobAt(string name, ushort x, ushort y) => new(_fx.World.AllocateMobId(), 1, x, y, name, 100);

    private GroundItem ItemAt(ushort x, ushort y) =>
        new() { Id = _fx.World.AllocateItemId(), ItemId = -1, Graphic = 100, X = x, Y = y };

    /// <summary>A mob send pass parked on its first frame must not despawn a LATER mob that a completed walk
    /// reconcile has redrawn — PR #245's F1, on the mob sweep.
    ///
    /// <para>The schedule: the viewer has the edge mob drawn and steps back to x=20, where that mob is outside
    /// the drawn rect, so the next sweep decides a despawn for it. The blocker mob is forgotten first, so the
    /// same sweep decides a SHOW for it and the send pass parks inside the gate on that frame, with the edge
    /// mob's despawn already decided and sitting in the buffer. With the pass parked, the viewer walks 20
    /// -&gt; 21 -&gt; 22 and reconciles each step; at x=22 the edge mob is back inside the strict rect, is
    /// drawn (0x07) and is tracked again. The gate is then released and the parked pass finishes.</para>
    ///
    /// <para>Without the revalidation it sends its saved despawn anyway: the client loses a mob the server
    /// still records as drawn, and no later sweep repairs it. Red with "a completed walk's redrawn mob must
    /// not be despawned by a parked send pass".</para></summary>
    [Fact]
    public void AParkedMobSendPassDoesNotDespawnAMobACompletedWalkRedrew()
    {
        var (viewer, outbound, character) = GatedPlayer("StaleMobDespawn", StaleMobDespawnMap, 22, 20);
        var blocker = MobAt("MobBlocker", 20, 19);
        var edge = MobAt("MobEdge", 30, 20);
        Exception? failed = null;
        Thread? sweep = null;
        try
        {
            Assert.Equal((100, 100), (character.MapXs, character.MapYs));   // the geometry this is read off

            var mobs = new[] { blocker, edge };
            viewer.SyncMobs(mobs);                                              // settle: both drawn at x=22
            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 21, 20); viewer.SyncMobs(mobs); });
            viewer.DespawnEntity(blocker.Id);                                   // so the sweep's first mob sends
            viewer.WithState(() => _fx.World.SetPlayerPosition(viewer, 20, 20)); // x=20: the edge mob is past the drawn rect

            outbound.Clear();
            sweep = new Thread(() =>
            {
                try { outbound.ArmForThisThread(); viewer.SyncMobs(mobs); }
                catch (Exception ex) { failed = ex; }
            }) { IsBackground = true };
            sweep.Start();
            Assert.True(outbound.Parked.Wait(5000), "the sweep never parked on the first mob's frame");

            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 21, 20); viewer.SyncMobs(mobs); });
            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 22, 20); viewer.SyncMobs(mobs); });
            Assert.Contains("0x07", FramesFor(outbound, edge.Id));   // the completed walk really did redraw it

            outbound.Release.Set();
            Assert.True(sweep.Join(5000), "the sweep thread never finished");
            Assert.Null(failed);

            var seq = FramesFor(outbound, edge.Id);
            _out.WriteLine("edge mob frames after the completed walk: " + string.Join(",", seq));
            outbound.Clear();
            viewer.SyncMobs(mobs);
            viewer.SyncMobs(mobs);
            _out.WriteLine("repair draws in two later sweeps: " + FramesFor(outbound, edge.Id).Length);

            Assert.True(!seq.Contains("0x0E"),
                $"a completed walk's redrawn mob must not be despawned by a parked send pass " +
                $"(#{edge.Id} at (30,20), viewer back at (22,20), strict rect x in [14,31)); " +
                $"frames for that mob: {string.Join(",", seq)}");
        }
        finally
        {
            outbound.Release.Set();
            if (sweep is not null) sweep.Join(5000);
            _fx.World.LeaveMap(viewer, StaleMobDespawnMap);
        }
    }

    /// <summary>The mirror, and the same interval: a mob send pass parked on its first frame must not draw a
    /// LATER mob that a completed walk reconcile has despawned.
    ///
    /// <para>Both mobs are untracked, so the sweep at x=22 decides a SHOW for each; the pass parks on the
    /// blocker's frame with the edge mob's show still in the buffer. The viewer then walks 22 -&gt; 21 -&gt;
    /// 20 and reconciles, and at x=20 the edge mob is past the drawn rect, so it is correctly despawned
    /// (0x0E) and dropped from <c>_shownMobs</c>. The gate is then released.</para>
    ///
    /// <para>Without the revalidation the saved 0x07 goes out afterwards: the client draws a mob that is off
    /// screen and that the server no longer tracks, so nothing will ever despawn it — a ghost, and the exact
    /// mirror of F1. Red with "a completed walk's despawned mob must not be redrawn by a parked send
    /// pass".</para></summary>
    [Fact]
    public void AParkedMobSendPassDoesNotRedrawAMobACompletedWalkDespawned()
    {
        var (viewer, outbound, character) = GatedPlayer("StaleMobShow", StaleMobShowMap, 22, 20);
        var blocker = MobAt("GhostMobBlocker", 20, 19);
        var edge = MobAt("GhostMobEdge", 30, 20);
        Exception? failed = null;
        Thread? sweep = null;
        try
        {
            Assert.Equal((100, 100), (character.MapXs, character.MapYs));

            var mobs = new[] { blocker, edge };   // both untracked: the sweep decides a show for each

            outbound.Clear();
            sweep = new Thread(() =>
            {
                try { outbound.ArmForThisThread(); viewer.SyncMobs(mobs); }
                catch (Exception ex) { failed = ex; }
            }) { IsBackground = true };
            sweep.Start();
            Assert.True(outbound.Parked.Wait(5000), "the sweep never parked on the first mob's frame");

            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 21, 20); viewer.SyncMobs(mobs); });
            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 20, 20); viewer.SyncMobs(mobs); });
            Assert.Contains("0x0E", FramesFor(outbound, edge.Id));   // the completed walk really did despawn it

            outbound.Release.Set();
            Assert.True(sweep.Join(5000), "the sweep thread never finished");
            Assert.Null(failed);

            var seq = FramesFor(outbound, edge.Id);
            _out.WriteLine("edge mob frames after the completed walk: " + string.Join(",", seq));

            Assert.True(seq.LastOrDefault() != "0x07",
                $"a completed walk's despawned mob must not be redrawn by a parked send pass " +
                $"(#{edge.Id} at (30,20), viewer back at (20,20), drawn rect x in [11,30)); " +
                $"frames for that mob: {string.Join(",", seq)}");
        }
        finally
        {
            outbound.Release.Set();
            if (sweep is not null) sweep.Join(5000);
            _fx.World.LeaveMap(viewer, StaleMobShowMap);
        }
    }

    /// <summary>And the same schedule on the GROUND-ITEM sweep, with two items on the map: a parked item send
    /// pass must not despawn an item that a completed walk reconcile has redrawn.
    ///
    /// <para>This one is not hypothetical on the old shape either. <c>SyncGroundItems</c> already sent outside
    /// the lock, and it took a SECOND acquisition to drop the id from <c>_shownItems</c> after deciding, so a
    /// walk reconcile landing between the decision and the frame could have the opposite opinion with no
    /// revalidation anywhere. Deferring the sends widens that window from one item to the rest of the sweep,
    /// which is what makes the check load-bearing.</para>
    ///
    /// <para>Red with "a completed walk's redrawn item must not be despawned by a parked send pass".</para>
    /// </summary>
    [Fact]
    public void AParkedItemSendPassDoesNotDespawnAnItemACompletedWalkRedrew()
    {
        var (viewer, outbound, character) = GatedPlayer("StaleItem", StaleItemMap, 22, 20);
        var blocker = ItemAt(20, 19);
        var edge = ItemAt(30, 20);
        Exception? failed = null;
        Thread? sweep = null;
        try
        {
            Assert.Equal((100, 100), (character.MapXs, character.MapYs));

            var items = new[] { blocker, edge };
            viewer.SyncGroundItems(items);                                      // settle: both drawn at x=22
            viewer.DespawnEntity(blocker.Id);                                   // so the sweep's first item sends
            viewer.WithState(() => _fx.World.SetPlayerPosition(viewer, 20, 20)); // x=20: the edge item is past the drawn rect

            outbound.Clear();
            sweep = new Thread(() =>
            {
                try { outbound.ArmForThisThread(); viewer.SyncGroundItems(items); }
                catch (Exception ex) { failed = ex; }
            }) { IsBackground = true };
            sweep.Start();
            Assert.True(outbound.Parked.Wait(5000), "the sweep never parked on the first item's frame");

            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 21, 20); viewer.SyncGroundItems(items); });
            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 22, 20); viewer.SyncGroundItems(items); });
            Assert.Contains("0x07", FramesFor(outbound, edge.Id));   // the completed walk really did redraw it

            outbound.Release.Set();
            Assert.True(sweep.Join(5000), "the sweep thread never finished");
            Assert.Null(failed);

            var seq = FramesFor(outbound, edge.Id);
            _out.WriteLine("edge item frames after the completed walk: " + string.Join(",", seq));
            outbound.Clear();
            viewer.SyncGroundItems(items);
            viewer.SyncGroundItems(items);
            _out.WriteLine("repair draws in two later sweeps: " + FramesFor(outbound, edge.Id).Length);

            Assert.True(!seq.Contains("0x0E"),
                $"a completed walk's redrawn item must not be despawned by a parked send pass " +
                $"(#{edge.Id} at (30,20), viewer back at (22,20), strict rect x in [14,31)); " +
                $"frames for that item: {string.Join(",", seq)}");
        }
        finally
        {
            outbound.Release.Set();
            if (sweep is not null) sweep.Join(5000);
            _fx.World.LeaveMap(viewer, StaleItemMap);
        }
    }

    /// <summary>PR #246's finding F1, on mobs: a parked SHOW must not go out after the viewer has walked so
    /// that the mob is no longer inside the strict rect — and, because it does not go out, the mob must not be
    /// left marked drawn.
    ///
    /// <para>This is the hole the set test alone cannot see. A completed walk that moves a mob from the strict
    /// rect into the overdraw band takes the <c>_edgeMobs.Add</c> branch: it produces no frame, so it takes no
    /// stamp, so a show parked behind it still finds its own stamp latest and the mob still in
    /// <c>_shownMobs</c>. The old test passed it and a 0x07 went out for a tile the client's viewport gate
    /// discards, leaving <c>_shownMobs</c> saying drawn over a client that had nothing.</para>
    ///
    /// <para>The schedule: viewer at x=22, both mobs untracked, so the sweep decides a SHOW for each and the
    /// send pass parks inside the gate on the blocker's frame with the edge mob's show still in the buffer.
    /// The viewer then walks 22 -&gt; 21 and reconciles; at x=21 the edge mob at x=30 is outside the strict
    /// rect [13,30) and inside the drawn rect [12,31), so that reconcile adds it to the band and sends
    /// nothing. The gate is released, and the parked show is re-tested against the CURRENT rect, dropped, and
    /// the mob marked undrawn.</para>
    ///
    /// <para>What the assertion is, and why it has to be the whole sequence: after the drop, the viewer walks
    /// on to x=20, where the mob is past the drawn rect. A mob correctly marked undrawn produces nothing
    /// there. A mob still marked drawn produces a <b>0x0E for a mob the client never drew</b> — that is the
    /// mismatch, made visible. Walking back to x=22 must then draw it exactly once, either way.</para>
    ///
    /// <para>Falsified two ways: revert the rollback and the 0x0E appears (red on the set half); revert the
    /// rect test and the late 0x07 appears (red on the frame half).</para></summary>
    [Fact]
    public void AParkedMobShowIsDroppedAndUndrawnWhenAWalkTakesItOutOfTheStrictRect()
    {
        var (viewer, outbound, character) = GatedPlayer("BandMobViewer", BandMobMap, 22, 20);
        var blocker = MobAt("BandMobBlocker", 20, 19);
        var edge = MobAt("BandMobEdge", 30, 20);
        Exception? failed = null;
        Thread? sweep = null;
        try
        {
            Assert.Equal((100, 100), (character.MapXs, character.MapYs));   // the geometry this is read off

            var mobs = new[] { blocker, edge };   // both untracked: the sweep decides a show for each

            outbound.Clear();
            sweep = new Thread(() =>
            {
                try { outbound.ArmForThisThread(); viewer.SyncMobs(mobs); }
                catch (Exception ex) { failed = ex; }
            }) { IsBackground = true };
            sweep.Start();
            Assert.True(outbound.Parked.Wait(5000), "the sweep never parked on the first mob's frame");

            // x=21: strict [13,30) no longer holds x=30, drawn [12,31) still does — the overdraw band, which
            // the reconcile records without a frame and without a stamp.
            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 21, 20); viewer.SyncMobs(mobs); });
            Assert.Empty(FramesFor(outbound, edge.Id));   // the band transition really did send nothing

            outbound.Release.Set();
            Assert.True(sweep.Join(5000), "the sweep thread never finished");
            Assert.Null(failed);

            // x=20: past the drawn rect [11,30). A mob correctly marked undrawn sends nothing here; a mob
            // still marked drawn sends a despawn for something the client never drew.
            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 20, 20); viewer.SyncMobs(mobs); });
            // And back into view, where it must be drawn exactly once.
            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 21, 20); viewer.SyncMobs(mobs); });
            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 22, 20); viewer.SyncMobs(mobs); });

            var seq = FramesFor(outbound, edge.Id);
            _out.WriteLine("edge mob frames over the whole schedule: " + string.Join(",", seq));

            Assert.True(seq.SequenceEqual(new[] { "0x07" }),
                $"a parked show the viewer has walked out of range of must be dropped AND must leave the mob " +
                $"undrawn, so the only frame for it is the one draw when it comes back into the strict rect " +
                $"(#{edge.Id} at (30,20), viewer 22 -> 21 -> 20 -> 21 -> 22); frames for that mob: " +
                $"{string.Join(",", seq)}");
        }
        finally
        {
            outbound.Release.Set();
            if (sweep is not null) sweep.Join(5000);
            _fx.World.LeaveMap(viewer, BandMobMap);
        }
    }

    /// <summary>The same fact on the PEER sweep, because the hole is the same one and it has been on master
    /// since PR #245: <c>DecidePeerUnderViewLock</c>'s <c>_edgePeers.Add</c> branch changes the sets without a
    /// stamp in exactly the same way, and <c>ShowPlayer</c>'s 0x33 is gated by the client's viewport with the
    /// very same rect test as the 0x07.
    ///
    /// <para>Same schedule, same assertion, same two falsifications.</para></summary>
    [Fact]
    public void AParkedPeerShowIsDroppedAndUndrawnWhenAWalkTakesItOutOfTheStrictRect()
    {
        var (blocker, _, _) = _fx.PlayerWith("BandPeerBlocker", Wide, BandPeerMap, 20, 19);
        var (viewer, outbound, character) = GatedPlayer("BandPeerViewer", BandPeerMap, 22, 20);
        var (edge, _, _) = _fx.PlayerWith("BandPeerEdge", Wide, BandPeerMap, 30, 20);
        Exception? failed = null;
        Thread? sweep = null;
        try
        {
            Assert.Equal((100, 100), (character.MapXs, character.MapYs));
            // Ascending StateRank: the send pass enters the blocker's monitor through ShowPlayer -> Snapshot
            // while nothing else holds it, so this is stated rather than relied on for the schedule.
            Assert.True(blocker.StateRank < viewer.StateRank, "the blocker must be seated before the viewer");

            var peers = new[] { new PeerTile(blocker, 20, 19), new PeerTile(edge, 30, 20) };
            viewer.DespawnEntity(blocker.PlayerId);      // both untracked: the sweep decides a show for each
            viewer.DespawnEntity(edge.PlayerId);

            outbound.Clear();
            sweep = new Thread(() =>
            {
                try { outbound.ArmForThisThread(); viewer.SyncPeers(peers); }
                catch (Exception ex) { failed = ex; }
            }) { IsBackground = true };
            sweep.Start();
            Assert.True(outbound.Parked.Wait(5000), "the sweep never parked on the first peer's frame");

            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 21, 20); viewer.SyncPeers(peers); });
            Assert.Empty(FramesFor(outbound, edge.PlayerId));

            outbound.Release.Set();
            Assert.True(sweep.Join(5000), "the sweep thread never finished");
            Assert.Null(failed);

            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 20, 20); viewer.SyncPeers(peers); });
            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 21, 20); viewer.SyncPeers(peers); });
            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 22, 20); viewer.SyncPeers(peers); });

            var seq = FramesFor(outbound, edge.PlayerId);
            _out.WriteLine("edge peer frames over the whole schedule: " + string.Join(",", seq));

            Assert.True(seq.SequenceEqual(new[] { "0x33" }),
                $"a parked peer show the viewer has walked out of range of must be dropped AND must leave the " +
                $"peer undrawn, so the only frame for it is the one draw when it comes back into the strict " +
                $"rect (#{edge.PlayerId} at (30,20), viewer 22 -> 21 -> 20 -> 21 -> 22); frames for that " +
                $"peer: {string.Join(",", seq)}");
        }
        finally
        {
            outbound.Release.Set();
            if (sweep is not null) sweep.Join(5000);
            foreach (var s in new[] { viewer, blocker, edge }) _fx.World.LeaveMap(s, BandPeerMap);
        }
    }

    /// <summary>And the same schedule on ground items — which is a regression guard rather than a fix, and
    /// this comment says so rather than implying a red it does not produce.
    ///
    /// <para>Items cannot reach F1's mismatch today, because <c>ShowGroundItem</c> owns <c>_shownItems</c>: it
    /// re-tests the viewport itself and records the item as drawn only if the draw was accepted, so a late
    /// item show is discarded by the server before it reaches the wire and nothing is left saying drawn. The
    /// rect test in the send pass makes that same decision one step earlier — saving the packet build — and
    /// gives the three sweeps one shape.</para>
    ///
    /// <para>What this fact therefore guards is the pair of properties that make items safe, and it goes red
    /// if either is taken away. Reverting the rect test alone leaves it green, which is the honest result and
    /// is recorded as such; making the decide pass add to <c>_shownItems</c> — the obvious "make items uniform
    /// with mobs" change — takes it red, because the dropped draw then does leave the item marked drawn with
    /// nothing on the client. That perturbation is the recorded falsification.</para></summary>
    [Fact]
    public void AParkedItemShowIsDroppedAndUndrawnWhenAWalkTakesItOutOfTheStrictRect()
    {
        var (viewer, outbound, character) = GatedPlayer("BandItemViewer", BandItemMap, 22, 20);
        var blocker = ItemAt(20, 19);
        var edge = ItemAt(30, 20);
        Exception? failed = null;
        Thread? sweep = null;
        try
        {
            Assert.Equal((100, 100), (character.MapXs, character.MapYs));

            var items = new[] { blocker, edge };   // both untracked: the sweep decides a show for each

            outbound.Clear();
            sweep = new Thread(() =>
            {
                try { outbound.ArmForThisThread(); viewer.SyncGroundItems(items); }
                catch (Exception ex) { failed = ex; }
            }) { IsBackground = true };
            sweep.Start();
            Assert.True(outbound.Parked.Wait(5000), "the sweep never parked on the first item's frame");

            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 21, 20); viewer.SyncGroundItems(items); });
            Assert.Empty(FramesFor(outbound, edge.Id));

            outbound.Release.Set();
            Assert.True(sweep.Join(5000), "the sweep thread never finished");
            Assert.Null(failed);

            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 20, 20); viewer.SyncGroundItems(items); });
            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 21, 20); viewer.SyncGroundItems(items); });
            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 22, 20); viewer.SyncGroundItems(items); });

            var seq = FramesFor(outbound, edge.Id);
            _out.WriteLine("edge item frames over the whole schedule: " + string.Join(",", seq));

            Assert.True(seq.SequenceEqual(new[] { "0x07" }),
                $"a parked item show the viewer has walked out of range of must be dropped AND must leave the " +
                $"item undrawn, so the only frame for it is the one draw when it comes back into the strict " +
                $"rect (#{edge.Id} at (30,20), viewer 22 -> 21 -> 20 -> 21 -> 22); frames for that item: " +
                $"{string.Join(",", seq)}");
        }
        finally
        {
            outbound.Release.Set();
            if (sweep is not null) sweep.Join(5000);
            _fx.World.LeaveMap(viewer, BandItemMap);
        }
    }

    /// <summary>PR #246's finding F2, on mobs: dropping a parked RE-ASSERT show must not un-draw a mob the
    /// client really did draw — the rollback restores the sets to what they said BEFORE that decision, which
    /// is not the same as "undrawn".
    ///
    /// <para>A show has two shapes and only one of them added anything. A FIRST show found the mob untracked
    /// and did <c>_shownMobs.Add</c>; dropping it and removing the mob is exactly right, because the client
    /// never received anything. A RE-ASSERT found the mob already drawn — by a 0x07 that really went out —
    /// loitering in the overdraw band, and its only set write was <c>_edgeMobs.Remove</c>. Rolling THAT back
    /// to undrawn throws away the server's record of a draw the client received, and with it the 0x0E that
    /// would later take the mob off the screen: an untracked mob outside the strict rect produces no decision
    /// at all, so no sweep repairs it.</para>
    ///
    /// <para>The schedule is the round-2 reviewer's probe. The mob at x=30 is drawn for real at x=22 (one
    /// 0x07). The viewer steps to x=21, where the mob is in the band, and that reconcile sends nothing and
    /// stamps nothing. The viewer steps back to x=22 and a sweep runs whose FIRST entry is an untracked
    /// blocker, so the send pass parks on the blocker's frame with the mob's re-assert show in the buffer.
    /// While it is parked, a real walk takes the viewer back to x=21 and reconciles — the band again. The gate
    /// is released and the re-assert is dropped by the rect test. The viewer then steps to x=20, past the
    /// drawn rect [11,30), where a mob the server still knows it drew must be despawned.</para>
    ///
    /// <para>Expected frames for that mob over the whole schedule: the real 0x07, then the 0x0E at x=20, and
    /// nothing else. Falsified by rolling a dropped re-assert back to undrawn — the shape this fact was
    /// written for — which leaves 0x07 alone and no despawn ever.</para></summary>
    [Fact]
    public void ADroppedReassertLeavesTheMobDrawnSoItIsStillDespawnedLater()
    {
        var (viewer, outbound, character) = GatedPlayer("ReassertMobViewer", ReassertMobMap, 22, 20);
        var blocker = MobAt("ReassertMobBlocker", 20, 19);
        var edge = MobAt("ReassertMobEdge", 30, 20);
        Exception? failed = null;
        Thread? sweep = null;
        try
        {
            Assert.Equal((100, 100), (character.MapXs, character.MapYs));

            var alone = new[] { edge };                 // the blocker stays untracked for the parked sweep
            var both = new[] { blocker, edge };

            outbound.Clear();
            viewer.SyncMobs(alone);                     // the real draw: x=30 is inside the strict rect [14,31)
            Assert.True(FramesFor(outbound, edge.Id).SequenceEqual(new[] { "0x07" }),
                "the mob must be drawn for real before the schedule starts");

            // Into the band at x=21: no frame, no stamp, _edgeMobs gains the id.
            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 21, 20); viewer.SyncMobs(alone); });
            // Back to x=22, where the next sweep's decision for the mob is a RE-ASSERT.
            viewer.WithState(() => _fx.World.SetPlayerPosition(viewer, 22, 20));

            sweep = new Thread(() =>
            {
                try { outbound.ArmForThisThread(); viewer.SyncMobs(both); }
                catch (Exception ex) { failed = ex; }
            }) { IsBackground = true };
            sweep.Start();
            Assert.True(outbound.Parked.Wait(5000), "the sweep never parked on the blocker's frame");

            // Parked with the re-assert in the buffer; the viewer walks back into the band.
            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 21, 20); viewer.SyncMobs(alone); });

            outbound.Release.Set();
            Assert.True(sweep.Join(5000), "the sweep thread never finished");
            Assert.Null(failed);

            // Past the drawn rect. The mob is still drawn on the client, so this must despawn it.
            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 20, 20); viewer.SyncMobs(alone); });

            var seq = FramesFor(outbound, edge.Id);
            _out.WriteLine("edge mob frames over the whole schedule: " + string.Join(",", seq));

            Assert.True(seq.SequenceEqual(new[] { "0x07", "0x0E" }),
                $"a dropped re-assert must leave the mob drawn, because the client really did draw it, so " +
                $"walking out of the drawn rect must still despawn it (#{edge.Id} at (30,20), viewer " +
                $"22 -> 21 -> 22(parked) -> 21 -> 20); frames for that mob: {string.Join(",", seq)}");
        }
        finally
        {
            outbound.Release.Set();
            if (sweep is not null) sweep.Join(5000);
            _fx.World.LeaveMap(viewer, ReassertMobMap);
        }
    }

    /// <summary>The same fact on the PEER sweep, which shares the rollback and the band branch. Same schedule,
    /// same assertion, same falsification; the frames are 0x33 rather than 0x07.</summary>
    [Fact]
    public void ADroppedReassertLeavesThePeerDrawnSoItIsStillDespawnedLater()
    {
        var (blocker, _, _) = _fx.PlayerWith("ReassertPeerBlocker", Wide, ReassertPeerMap, 20, 19);
        var (viewer, outbound, character) = GatedPlayer("ReassertPeerViewer", ReassertPeerMap, 22, 20);
        var (edge, _, _) = _fx.PlayerWith("ReassertPeerEdge", Wide, ReassertPeerMap, 30, 20);
        Exception? failed = null;
        Thread? sweep = null;
        try
        {
            Assert.Equal((100, 100), (character.MapXs, character.MapYs));
            Assert.True(blocker.StateRank < viewer.StateRank, "the blocker must be seated before the viewer");

            var edgeTile = new PeerTile(edge, 30, 20);
            var alone = new[] { edgeTile };
            var both = new[] { new PeerTile(blocker, 20, 19), edgeTile };

            viewer.DespawnEntity(blocker.PlayerId);     // the blocker stays untracked for the parked sweep
            viewer.DespawnEntity(edge.PlayerId);

            outbound.Clear();
            viewer.SyncPeers(alone);                    // the real draw
            Assert.True(FramesFor(outbound, edge.PlayerId).SequenceEqual(new[] { "0x33" }),
                "the peer must be drawn for real before the schedule starts");

            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 21, 20); viewer.SyncPeers(alone); });
            viewer.WithState(() => _fx.World.SetPlayerPosition(viewer, 22, 20));

            sweep = new Thread(() =>
            {
                try { outbound.ArmForThisThread(); viewer.SyncPeers(both); }
                catch (Exception ex) { failed = ex; }
            }) { IsBackground = true };
            sweep.Start();
            Assert.True(outbound.Parked.Wait(5000), "the sweep never parked on the blocker's frame");

            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 21, 20); viewer.SyncPeers(alone); });

            outbound.Release.Set();
            Assert.True(sweep.Join(5000), "the sweep thread never finished");
            Assert.Null(failed);

            viewer.WithState(() => { _fx.World.SetPlayerPosition(viewer, 20, 20); viewer.SyncPeers(alone); });

            var seq = FramesFor(outbound, edge.PlayerId);
            _out.WriteLine("edge peer frames over the whole schedule: " + string.Join(",", seq));

            Assert.True(seq.SequenceEqual(new[] { "0x33", "0x0E" }),
                $"a dropped re-assert must leave the peer drawn, because the client really did draw it, so " +
                $"walking out of the drawn rect must still despawn it (#{edge.PlayerId} at (30,20), viewer " +
                $"22 -> 21 -> 22(parked) -> 21 -> 20); frames for that peer: {string.Join(",", seq)}");
        }
        finally
        {
            outbound.Release.Set();
            if (sweep is not null) sweep.Join(5000);
            foreach (var s in new[] { viewer, blocker, edge }) _fx.World.LeaveMap(s, ReassertPeerMap);
        }
    }
}
