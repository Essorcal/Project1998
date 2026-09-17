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

    /// <summary>The recorded frames for ONE entity, in order, as "0x07"/"0x0E". A <c>0x07</c> creature list
    /// carries <c>count(u16BE)</c> then 12-byte entries whose id is at entry offset 4; a 4.95 <c>0x0E</c>
    /// despawn carries a count byte and then its ids.</summary>
    private static string[] FramesFor(GatedRecorder outbound, uint id)
    {
        var seq = new List<string>();
        foreach (var frame in outbound.Snapshot())
        {
            byte op = frame[3];
            if (op != 0x07 && op != 0x0E) continue;
            byte[] body = TkCrypt.Crypt(frame[5..], frame[4], TkCrypt.LoginKey);
            if (op == 0x07)
            {
                int count = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(0));
                for (int i = 0; i < count; i++)
                    if (BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(2 + i * 12 + 4)) == id) seq.Add("0x07");
            }
            else
                for (int i = 0; i < body[0]; i++)
                    if (BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1 + i * 4)) == id) seq.Add("0x0E");
        }
        return seq.ToArray();
    }

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
}
