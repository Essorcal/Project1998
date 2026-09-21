using System.Buffers;
using System.Buffers.Binary;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The TICK's map snapshot, once <c>World.ReconcileViews</c> rents its buffers instead of building them with
/// LINQ — the tick-side twin of <c>Tests/ViewSnapshotPoolTests.cs</c>, and it needs guarding for the same
/// reason in a worse place.
///
/// <para>A pooled buffer fails SILENTLY in all three directions, which is AGENTS.md rule 3. Fill it in a
/// different order from the LINQ it replaces and nothing throws — the sweeps' own order facts
/// (<c>PeerSweepArrayOrderTests</c>, <c>MobSweepArrayOrderTests</c>) would then be pinning an order the
/// snapshot no longer produces. Hand it back without wiping it and nothing throws either; the pool simply
/// holds every logged-out <see cref="Session"/>, every <see cref="Mob"/> and every <see cref="GroundItem"/>
/// of that map alive until the buffer is rented again. Read past the fill and nothing throws a third time:
/// an <see cref="ArrayPool{T}"/> array is at LEAST as long as it was asked for, so the tail is the previous
/// tenant's — and here the previous tenant is ANOTHER MAP on the same beat, whose peers and mobs would be
/// drawn on this client.</para>
///
/// <para>So the three facts below are the three ways it can be wrong: the snapshot must allocate nothing per
/// beat (the point of the change), the sweeps must stop at the count they were given and never read the
/// tail, and each player must receive its map's roster in the map's own order. The falsifications each was
/// checked against are in <c>briefs/reports/reconcile-snapshot-pool-opus.md</c>.</para>
///
/// <para>Geometry, as in <c>ViewSnapshotPoolTests</c>: a content-free map is 12x12, so <c>EdgeAwareAnchor</c>
/// centres it — from (5,10) the strict rect is x in [-2,15), and x=5 is inside it. Hygiene, also as there:
/// the fixture's <c>World</c> is shared and has no teardown, so every seated player is removed in a
/// <c>finally</c>.</para>
/// </summary>
[Collection("world")]
public class ReconcileSnapshotPoolTests
{
    private readonly SessionFixture _fx;

    public ReconcileSnapshotPoolTests(SessionFixture fx) => _fx = fx;

    // Content-free maps (no registry row, no terrain, no warps, no spawns), one block per fact so nothing is
    // shared. 60180-60183 are the allocation fact's four populated maps; 60184 is the tail fact's map and
    // 60185-60186 its two DECOY maps; 60187 is the order fact's.
    private const ushort AllocMap0 = 60180;
    private const ushort TailMap = 60184, Decoy0 = 60185, Decoy1 = 60186;
    private const ushort OrderMap = 60187;

    private const ushort ViewerX = 5, ViewerY = 10;
    private const ushort InStrict = 5;      // x in [-2,15)

    private Mob MobAt(string name, ushort x, ushort y) => new(_fx.World.AllocateMobId(), 1, x, y, name, 100);

    private GroundItem ItemAt(ushort x, ushort y) =>
        new() { Id = _fx.World.AllocateItemId(), ItemId = 1, X = x, Y = y, Amount = 1, Graphic = 22 };

    /// <summary>Every frame the recorder holds, as (opcode, entity id) — the same decode
    /// <c>ViewSnapshotPoolTests.Wire</c> uses. Peers arrive as 0x33; mobs and ground items both arrive as
    /// 0x07, and their id RANGES are what tells them apart (mobs from 100,000, items from 500,000).</summary>
    private static List<(byte op, uint id)> Wire(RecordingOutbound outbound)
    {
        var seq = new List<(byte, uint)>();
        foreach (var frame in outbound.Frames)
        {
            byte op = frame[3];
            byte[] body = TkCrypt.Crypt(frame[5..], frame[4], TkCrypt.LoginKey);
            if (op == 0x33) seq.Add((op, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(5))));
            else if (op == 0x07)
            {
                int count = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(0));
                for (int i = 0; i < count; i++) seq.Add((op, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(2 + i * 12 + 4))));
            }
            else if (op == 0x0E)
                for (int i = 0; i < body[0]; i++) seq.Add((op, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1 + i * 4))));
            else seq.Add((op, 0u));
        }
        return seq;
    }

    private static string Render(IEnumerable<(byte op, uint id)> seq)
    {
        var s = string.Join(", ", seq.Select(f => $"0x{f.op:X2}#{f.id}"));
        return s.Length == 0 ? "(nothing)" : s;
    }

    // =====================================================================================================

    /// <summary>A beat's view snapshot allocates NOTHING. This is the whole point of the change: the LINQ it
    /// replaces built four arrays per POPULATED MAP per beat — the outer tuple array, then peers, mobs and
    /// items — and threw all of them away 600 ms later, for ever, at the tick's own rate.
    ///
    /// <para>Measured on <c>ReconcileViews</c> alone rather than on a whole beat, because a beat's other
    /// phases allocate for reasons this slice does not touch and would drown the signal. Four populated maps,
    /// so the per-map fills and the outer buffer are both exercised; the fixture is settled first, so the
    /// steady state sends no frames and the only allocation a beat could show is the snapshot's own.</para>
    ///
    /// <para>The budget is not zero because the measurement is not hermetic — <see cref="ArrayPool{T}"/> is
    /// process-wide and shared with whatever else is resident, so a buffer evicted from the thread-local slot
    /// by another renter has to be allocated again. It is tight enough to fail on the thing it guards, and by
    /// a wide margin: measured here the snapshot allocates <b>0 B over 200 beats</b>, and putting just ONE of
    /// the four arrays back on LINQ takes it to <b>608 B a beat</b>, ten times the budget.</para>
    ///
    /// <para>Falsification: build the outer collection with <c>_maps.Values.Where(…).Select(…).ToArray()</c>
    /// again (or drop any one of the three per-map rents back to <c>.ToArray()</c>) and this goes red on the
    /// byte count. Run it, confirm red, restore.</para></summary>
    [Fact]
    public void AViewSnapshotAllocatesNothingPerBeat()
    {
        const int Maps = 4, PlayersPerMap = 3, MobsPerMap = 4, ItemsPerMap = 3;
        const int Beats = 200;
        const long BudgetPerBeat = 64;          // bytes; the base's LINQ is ~13,000 on this fixture

        var seated = new List<(Session s, ushort map)>();
        try
        {
            for (int m = 0; m < Maps; m++)
            {
                ushort map = (ushort)(AllocMap0 + m);
                for (int i = 0; i < PlayersPerMap; i++)
                {
                    var (p, _, _) = _fx.PlayerWith($"PoolAlloc{m}_{i}", _ => { }, map, (ushort)(2 + i), ViewerY);
                    seated.Add((p, map));
                }
                for (int i = 0; i < MobsPerMap; i++) _fx.World.AddMob(map, MobAt($"allocmob{m}_{i}", (ushort)(2 + i), 3));
                for (int i = 0; i < ItemsPerMap; i++) _fx.World.DropItem(map, ItemAt((ushort)(2 + i), 4));
            }

            // Settle: after a few beats every viewer has drawn everything it can see, so a further beat sends
            // no frames and allocates only what the snapshot itself does. This also warms the pools, whose
            // first rent of each bucket is a real allocation by design.
            for (int i = 0; i < 20; i++) _fx.World.ReconcileViewsForTest();

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Beats; i++) _fx.World.ReconcileViewsForTest();
            long after = GC.GetAllocatedBytesForCurrentThread();

            long total = after - before;
            Assert.True(total <= BudgetPerBeat * Beats,
                        $"the tick's view snapshot allocated {total} B over {Beats} beats " +
                        $"({total / (double)Beats:N1} B a beat) on {Maps} populated maps of {PlayersPerMap} players, " +
                        $"{MobsPerMap} mobs and {ItemsPerMap} items — the budget is {BudgetPerBeat} B a beat. " +
                        $"GC.GetAllocatedBytesForCurrentThread before={before} after={after}");
        }
        finally { foreach (var (s, map) in seated) _fx.World.LeaveMap(s, map); }
    }

    /// <summary>The sweeps stop at the count the snapshot gave them and never read the buffer tail. A rented
    /// array is at LEAST as long as the fill, and on a live server whatever is past the fill was put there by
    /// the map reconciled just before this one on the SAME beat. A sweep that walked <c>Length</c> would draw
    /// another map's peers, mobs and floor items onto this client.
    ///
    /// <para>The arrangement makes that concrete instead of theoretical: all three pools are PRIMED before the
    /// beat — a buffer of each type is rented at exactly the length the fill will ask for, every slot filled
    /// with a perfectly drawable entity that is NOT on this map, and returned unwiped, which is what
    /// <see cref="ArrayPool{T}.Return(T[], bool)"/> does by default. The pool's thread-local slot hands that
    /// same buffer straight back to the reconcile, so the tail the sweeps see is real poison and not a
    /// hypothetical. The poison entities sit at a tile inside the viewer's strict rect, so reading them is
    /// not a subtle mistake.</para>
    ///
    /// <para>The two DECOY maps hold one player each and no mobs or items, so <c>ReconcileViews</c>' own
    /// filter skips them and cannot consume the primed buffers before the map under test reaches them. The
    /// arrangement is checked, not assumed: the fact asserts the map's own entities WERE drawn, so a beat
    /// that quietly reconciled nothing would go red rather than pass vacuously.</para>
    ///
    /// <para>Falsification: pass <c>t.view.Players.Length</c> / <c>.Mobs.Length</c> / <c>.Items.Length</c>
    /// instead of the counts at the <c>Try</c> site in <c>ReconcileViews</c> (or drop the <c>count</c>
    /// parameter's use in <c>SyncGroundItems</c>) and this goes red with the poison ids on the wire. Run it,
    /// confirm red, restore.</para></summary>
    [Fact]
    public void TheSweepsNeverReadTheRentedBufferTail()
    {
        const int Peers = 2, Mobs = 2, Items = 2;

        var (viewer, outbound, _) = _fx.PlayerWith("PoolTailV", _ => { }, TailMap, ViewerX, ViewerY);
        var seated = new List<(Session, ushort)> { (viewer, TailMap) };
        try
        {
            var peerIds = new List<uint>();
            for (int i = 0; i < Peers; i++)
            {
                var (p, _, _) = _fx.PlayerWith($"PoolTail{i}", _ => { }, TailMap, InStrict, ViewerY);
                seated.Add((p, TailMap));
                peerIds.Add(p.PlayerId);
            }
            var mobs = new List<Mob>();
            for (int i = 0; i < Mobs; i++) { var mo = MobAt($"tailmob{i}", InStrict, ViewerY); _fx.World.AddMob(TailMap, mo); mobs.Add(mo); }
            var items = new List<GroundItem>();
            for (int i = 0; i < Items; i++) { var gi = ItemAt(InStrict, ViewerY); _fx.World.DropItem(TailMap, gi); items.Add(gi); }

            // The poison. Each decoy session is ALONE on its own map with no mobs and no items, so the
            // reconcile's filter skips that map entirely and the only map in the beat is TailMap.
            var (poison0, _, _) = _fx.PlayerWith("PoolTailPoison0", _ => { }, Decoy0, InStrict, ViewerY);
            var (poison1, _, _) = _fx.PlayerWith("PoolTailPoison1", _ => { }, Decoy1, InStrict, ViewerY);
            seated.Add((poison0, Decoy0));
            seated.Add((poison1, Decoy1));
            var poisonMob = MobAt("tailpoisonmob", InStrict, ViewerY);       // never added to any map
            var poisonItem = ItemAt(InStrict, ViewerY);                      // never dropped on any map

            // Rub the viewer's view of everything out, so the beat below has a real show to send for each of
            // the six entities it should draw and for each of the poisons it must not.
            foreach (var id in peerIds) viewer.DespawnEntity(id);
            foreach (var mo in mobs) viewer.DespawnEntity(mo.Id);
            foreach (var gi in items) viewer.DespawnEntity(gi.Id);
            outbound.Clear();

            // PRIME THE POOLS. Rent at exactly the length the fill will ask for (the map's roster sizes), so
            // the bucket matches; fill every slot, tail included; return unwiped.
            var peerBuf = ArrayPool<PeerTile>.Shared.Rent(Peers + 1);        // the viewer is in the roster too
            for (int i = 0; i < peerBuf.Length; i++)
                peerBuf[i] = new PeerTile(poison0, poison0.PlayerId, InStrict, ViewerY);
            ArrayPool<PeerTile>.Shared.Return(peerBuf);
            var mobBuf = ArrayPool<Mob>.Shared.Rent(Mobs);
            for (int i = 0; i < mobBuf.Length; i++) mobBuf[i] = poisonMob;
            ArrayPool<Mob>.Shared.Return(mobBuf);
            var itemBuf = ArrayPool<GroundItem>.Shared.Rent(Items);
            for (int i = 0; i < itemBuf.Length; i++) itemBuf[i] = poisonItem;
            ArrayPool<GroundItem>.Shared.Return(itemBuf);

            _fx.World.ReconcileViewsForTest();

            var seq = Wire(outbound);
            var expected = new List<(byte, uint)>();
            foreach (var id in peerIds) expected.Add(((byte)0x33, id));
            foreach (var mo in mobs) expected.Add(((byte)0x07, mo.Id));
            foreach (var gi in items) expected.Add(((byte)0x07, gi.Id));

            // The arrangement check first: a beat that reconciled nothing must not pass this fact quietly.
            Assert.True(seq.Count > 0, "the beat drew nothing at all — the arrangement is wrong, not the code");
            var poisonIds = new[] { poison0.PlayerId, poison1.PlayerId, poisonMob.Id, poisonItem.Id };
            Assert.True(seq.SequenceEqual(expected),
                $"a sweep given a count must read only that many entries; expected {Render(expected)} " +
                $"but the wire carried {Render(seq)} (the primed tails hold peer #{poison0.PlayerId}, " +
                $"mob #{poisonMob.Id} and item #{poisonItem.Id})");
            Assert.DoesNotContain(seq, f => poisonIds.Contains(f.id));
        }
        finally { foreach (var (s, map) in seated) _fx.World.LeaveMap(s, map); }
    }

    /// <summary>Each player receives its map's roster in the MAP'S OWN ORDER, and in the same three calls in
    /// the same order — peers (0x33), then mobs (0x07), then floor items (0x07). The plain fill loops that
    /// replaced the LINQ walk the same <c>List&lt;T&gt;</c>s, so the order is the roster's, and four
    /// downstream order facts already depend on that being true.
    ///
    /// <para>Three peers, three mobs and three items rather than one of each, so an order defect is a
    /// different SEQUENCE and not merely a different set: a set comparison would pass on a fill walked
    /// backwards. The expectation is hand-built from the order the entities were seated in, not read back out
    /// of the snapshot, so it is an independent statement of what the roster order is.</para>
    ///
    /// <para>Falsification: walk any one of the three fill loops backwards in <c>ReconcileViews</c> and this
    /// goes red on the sequence. Run it, confirm red, restore.</para></summary>
    [Fact]
    public void EachPlayerReceivesItsMapsRosterInTheMapsOwnOrder()
    {
        const int Peers = 3, Mobs = 3, Items = 3;

        var (viewer, outbound, _) = _fx.PlayerWith("PoolOrderV", _ => { }, OrderMap, ViewerX, ViewerY);
        var seated = new List<Session> { viewer };
        try
        {
            var peerIds = new List<uint>();
            for (int i = 0; i < Peers; i++)
            {
                var (p, _, _) = _fx.PlayerWith($"PoolOrder{i}", _ => { }, OrderMap, InStrict, ViewerY);
                seated.Add(p);
                peerIds.Add(p.PlayerId);
            }
            var mobs = new List<Mob>();
            for (int i = 0; i < Mobs; i++) { var mo = MobAt($"ordermob{i}", InStrict, ViewerY); _fx.World.AddMob(OrderMap, mo); mobs.Add(mo); }
            var items = new List<GroundItem>();
            for (int i = 0; i < Items; i++) { var gi = ItemAt(InStrict, ViewerY); _fx.World.DropItem(OrderMap, gi); items.Add(gi); }

            foreach (var id in peerIds) viewer.DespawnEntity(id);
            foreach (var mo in mobs) viewer.DespawnEntity(mo.Id);
            foreach (var gi in items) viewer.DespawnEntity(gi.Id);
            outbound.Clear();

            _fx.World.ReconcileViewsForTest();

            // Seating order IS roster order: EnterMap appends, AddMob appends, DropItem appends.
            var expected = new List<(byte, uint)>();
            foreach (var id in peerIds) expected.Add(((byte)0x33, id));
            foreach (var mo in mobs) expected.Add(((byte)0x07, mo.Id));
            foreach (var gi in items) expected.Add(((byte)0x07, gi.Id));

            var seq = Wire(outbound);
            Assert.True(seq.SequenceEqual(expected),
                $"the tick must hand each player its map's roster in the map's order, peers then mobs then " +
                $"items; expected {Render(expected)} but the wire carried {Render(seq)}");
        }
        finally { foreach (var s in seated) _fx.World.LeaveMap(s, OrderMap); }
    }
}
