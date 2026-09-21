using System.Buffers;
using System.Buffers.Binary;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The walk step's map snapshot, once it rents its two arrays instead of building them with LINQ
/// (<c>World.ViewPooled</c> / <c>World.ReturnView</c>, and the <c>count</c> the two sweeps now take).
///
/// <para>Why this needs guarding is the shape of the change rather than its size. A pooled buffer fails
/// SILENTLY in both directions, which is AGENTS.md rule 3 in its purest form. Fill it differently from the
/// LINQ it replaces — a filter that lets the walker see itself, an order the sweeps did not have — and
/// nothing throws; somebody's screen is just wrong. Hand it back without wiping it and nothing throws
/// either; the pool simply holds four hundred logged-out <c>Session</c>s and every <c>Mob</c> on the map
/// alive until that exact buffer is rented again, which is the leak <c>World.ReturnPeers</c> was written to
/// document. Read past the fill and nothing throws a third time: an <see cref="ArrayPool{T}"/> array is at
/// least as long as it was asked for, so the tail is the previous tenant's peers, drawn on this client.</para>
///
/// <para>So the three facts below are the three ways it can be wrong: the pooled snapshot must equal the
/// LINQ one element for element (<c>World.View</c> is kept as the oracle, and is still the production path
/// for <c>ResyncPeers</c> and <c>RedrawWorld</c>); the return must leave no reference behind; and the
/// sweeps must stop at the count they were given. The falsifications each one was checked against are in
/// <c>briefs/reports/view-snapshot-pool-opus.md</c>.</para>
///
/// <para>Geometry, as in <c>PeerSweepBatchTests</c>: a content-free map is 12x12, so <c>EdgeAwareAnchor</c>
/// centres it — from (5,10) the strict rect is x in [-2,15). x=5 is inside it; x=16 is past the drawn rect.
/// Hygiene, also as there: the fixture's <c>World</c> is shared and has no teardown, so every seated player
/// is removed in a <c>finally</c>.</para>
/// </summary>
[Collection("world")]
public class ViewSnapshotPoolTests
{
    private readonly SessionFixture _fx;

    public ViewSnapshotPoolTests(SessionFixture fx) => _fx = fx;

    // Content-free maps (no registry row, no terrain, no warps, no spawns), one per fact so nothing is shared.
    private const ushort OracleMap = 60170, WipeMap = 60171, CountMap = 60172;

    private const ushort ViewerX = 5, ViewerY = 10;
    private const ushort InStrict = 5;      // x in [-2,15)

    private Mob MobAt(string name, ushort x, ushort y) => new(_fx.World.AllocateMobId(), 1, x, y, name, 100);

    /// <summary>Every frame the recorder holds, as (opcode, entity id), the same decode
    /// <c>PeerSweepBatchTests.Wire</c> and <c>MobSweepArrayOrderTests.Wire</c> use.</summary>
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

    private static string Render(List<(byte op, uint id)> seq) =>
        seq.Count == 0 ? "(nothing)" : string.Join(", ", seq.Select(f => $"0x{f.op:X2}#{f.id}"));

    // =====================================================================================================

    /// <summary>The pooled snapshot is the LINQ snapshot: the same peers with the same ids and tiles, the
    /// same mobs, in the same order, with the viewer excluded from the peers and nobody excluded from the
    /// mobs. <c>World.View</c> is the oracle here precisely because it is the code this replaces and is
    /// still live for the two callers that cannot give a buffer back.
    ///
    /// <para>Five peers and five mobs, seated in a known order, so an order defect is a different SEQUENCE
    /// and not just a different set — a set comparison would pass on a snapshot walked backwards, and the
    /// sweeps' own order facts (<c>PeerSweepArrayOrderTests</c>, <c>MobSweepArrayOrderTests</c>) would then
    /// be pinning an order the snapshot no longer produces.</para></summary>
    [Fact]
    public void ThePooledSnapshotEqualsTheLinqSnapshotElementForElement()
    {
        var (viewer, _, _) = _fx.PlayerWith("PoolOracleV", _ => { }, OracleMap, ViewerX, ViewerY);
        var seated = new List<Session> { viewer };
        try
        {
            for (int i = 0; i < 5; i++)
            {
                var (p, _, _) = _fx.PlayerWith($"PoolOracle{i}", _ => { }, OracleMap, (ushort)(1 + i), 1);
                seated.Add(p);
            }
            for (int i = 0; i < 5; i++) _fx.World.AddMob(OracleMap, MobAt($"poolmob{i}", (ushort)(2 + i), 3));

            var (linqPeers, linqMobs) = _fx.World.View(viewer, OracleMap);
            var pooled = _fx.World.ViewPooled(viewer, OracleMap);
            try
            {
                Assert.Equal(5, linqPeers.Length);          // the fixture is the one this fact describes
                Assert.Equal(5, linqMobs.Length);
                Assert.Equal(linqPeers.Length, pooled.PeerCount);
                Assert.Equal(linqMobs.Length, pooled.MobCount);

                for (int i = 0; i < linqPeers.Length; i++)
                {
                    Assert.Equal(linqPeers[i], pooled.Peers[i]);             // session, id and tile, same slot
                    Assert.Same(linqPeers[i].Session, pooled.Peers[i].Session);
                    Assert.NotSame(viewer, pooled.Peers[i].Session);         // the p != s filter survived
                }
                for (int i = 0; i < linqMobs.Length; i++)
                    Assert.Same(linqMobs[i], pooled.Mobs[i]);                // same mob, same slot
            }
            finally { World.ReturnView(in pooled); }
        }
        finally { foreach (var s in seated) _fx.World.LeaveMap(s, OracleMap); }
    }

    /// <summary>A real accepted 0x06 walk step gives both buffers back WIPED: after the step's
    /// <c>finally</c>, no slot the fill used still holds a <see cref="Session"/> or a <see cref="Mob"/>.
    ///
    /// <para>Checked on the arrays themselves, captured before the return rather than rented back
    /// afterwards, so the fact is deterministic — <see cref="ArrayPool{T}"/> is process-wide and a rent
    /// racing another test's would make this flaky about the one thing it is for. The step half is real: the
    /// walker's snapshot comes out of <c>ViewPooled</c> inside <c>HandleWalk</c>, so the direct call below
    /// exercises the same rent, fill and return the step does, and the step itself is driven first to prove
    /// it accepts and reaches that code at all.</para></summary>
    [Fact]
    public void ReturningAViewSnapshotLeavesNoSessionOrMobReferenceInTheBuffer()
    {
        var (walker, _, character) = _fx.PlayerWith("PoolWipeW", _ => { }, WipeMap, ViewerX, ViewerY);
        var seated = new List<Session> { walker };
        try
        {
            for (int i = 0; i < 4; i++)
            {
                var (p, _, _) = _fx.PlayerWith($"PoolWipe{i}", _ => { }, WipeMap, (ushort)(1 + i), 1);
                seated.Add(p);
            }
            for (int i = 0; i < 4; i++) _fx.World.AddMob(WipeMap, MobAt($"wipemob{i}", (ushort)(2 + i), 3));

            // The real walk packet, through Session.Receive -> Handle -> WithState(Dispatch) -> HandleWalk,
            // which is where the pooled snapshot is taken and given back. It must be ACCEPTED, or the step
            // returns before reaching it.
            ushort x0 = walker.PlayerX;
            walker.Receive(SessionFixture.Frame(ClientOp.Walk,
                new byte[] { 1, 0, (byte)(x0 >> 8), (byte)x0, (byte)(walker.PlayerY >> 8), (byte)walker.PlayerY }));
            Assert.Equal((ushort)(x0 + 1), walker.PlayerX);
            Assert.Equal((ushort)12, character.MapXs);       // the geometry the rect is read off

            var snap = _fx.World.ViewPooled(walker, WipeMap);
            var peerBuf = snap.Peers; int peerCount = snap.PeerCount;
            var mobBuf = snap.Mobs;  int mobCount = snap.MobCount;
            Assert.Equal(4, peerCount);
            Assert.Equal(4, mobCount);
            for (int i = 0; i < peerCount; i++) Assert.NotNull(peerBuf[i].Session);   // filled before the return
            for (int i = 0; i < mobCount; i++) Assert.NotNull(mobBuf[i]);

            World.ReturnView(in snap);

            for (int i = 0; i < peerCount; i++)
                Assert.True(peerBuf[i].Session is null && peerBuf[i].Id == 0,
                            $"peer slot {i} still holds {peerBuf[i].Session?.CharName ?? "id " + peerBuf[i].Id} " +
                            "after the buffer went back to the pool — a returned buffer is a live GC root");
            for (int i = 0; i < mobCount; i++)
                Assert.True(mobBuf[i] is null,
                            $"mob slot {i} still holds {mobBuf[i]?.Name} after the buffer went back to the pool");
        }
        finally { foreach (var s in seated) _fx.World.LeaveMap(s, WipeMap); }
    }

    /// <summary>The sweeps stop at the count they were given and never read the tail. A rented buffer is at
    /// LEAST as long as the fill, and whatever is past it was put there by whoever rented it last — on a
    /// live server, another map's peers and mobs. A sweep that walked <c>Length</c> would draw them.
    ///
    /// <para>The tail entries here are deliberately in the strict rect, so reading them is not a subtle
    /// mistake: it is a peer and a mob appearing on a client that should never have heard of them.</para></summary>
    [Fact]
    public void TheSweepsReadOnlyTheFilledPrefixAndNotTheBufferTail()
    {
        var (viewer, outbound, character) = _fx.PlayerWith("PoolCountV", _ => { }, CountMap, ViewerX, ViewerY);
        var (wanted, _, _) = _fx.PlayerWith("PoolCountWanted", _ => { }, CountMap, InStrict, ViewerY);
        var (tail, _, _) = _fx.PlayerWith("PoolCountTail", _ => { }, CountMap, InStrict, ViewerY);
        try
        {
            Assert.Equal((ushort)12, character.MapXs);

            var wantedMob = MobAt("poolcount-wanted", InStrict, ViewerY);
            var tailMob = MobAt("poolcount-tail", InStrict, ViewerY);

            // Both buffers are longer than their fill, with a perfectly drawable entity sitting in the tail.
            var peers = new[] { new PeerTile(wanted, InStrict, ViewerY), new PeerTile(tail, InStrict, ViewerY) };
            var mobs = new[] { wantedMob, tailMob };

            viewer.DespawnEntity(wanted.PlayerId);           // map entry drew the two peers; start from nothing
            viewer.DespawnEntity(tail.PlayerId);
            outbound.Clear();

            viewer.SyncPeers(peers, 1);
            viewer.SyncMobs(mobs, 1);

            var seq = Wire(outbound);
            var expected = new List<(byte, uint)> { (0x33, wanted.PlayerId), (0x07, wantedMob.Id) };
            Assert.True(seq.SequenceEqual(expected),
                $"a sweep given a count must draw only that many entries; expected {Render(expected)} " +
                $"but the wire carried {Render(seq)} (the tail is peer #{tail.PlayerId} / mob #{tailMob.Id})");
        }
        finally { foreach (var s in new[] { viewer, wanted, tail }) _fx.World.LeaveMap(s, CountMap); }
    }
}
