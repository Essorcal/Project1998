using System.Buffers.Binary;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The peer's entity id now rides in <see cref="PeerTile"/>, taken under <c>World._lock</c> with the tile —
/// the three snapshot sites that build one, and what they put in it.
///
/// <para><b>Why this needs tests by the "would this fail loudly?" rule.</b> Every failure mode of the change
/// is silent. A snapshot site that fills <c>Id</c> from the wrong field hands the sweep a peer under an id
/// that is not theirs: the show still draws correctly (0x33 carries <c>Snapshot().Id</c>, off the subject),
/// so the screen looks right, and the damage is in the tracking sets — <c>_shownPeers</c> gains an id nobody
/// owns, the peer is never despawned when they leave, and a despawn goes out for an id the client is not
/// drawing. Nothing throws and nothing logs.</para>
///
/// <para>The two facts are: <c>World.View</c> and <c>World.EnterMap</c> hand out tiles carrying the peer's
/// own id; and the tick's own snapshot, whose array never leaves <c>World</c>, does the same.</para>
///
/// <para>What the sweep then DOES with that id is <c>PeerSweepCaptureTests</c>.</para>
///
/// <para>Geometry: the beat fact needs a peer that can stand genuinely outside the viewer's drawn rect, so
/// its map is 100x100, where <c>EdgeAwareAnchor</c> is <c>clamp(x-8, 0, xs-17)</c> — from (5,10) the origin
/// is (0,3), the strict rect is x in [0,17) and the drawn rect (pad 1) x in [-1,18). So x=5 is in view and
/// x=30 is past both.</para>
///
/// <para>Hygiene, as in <c>ViewportRectTests</c>: the fixture's <c>World</c> is shared and has no teardown,
/// so every seated player is removed in a <c>finally</c>.</para>
/// </summary>
[Collection("world")]
public class PeerTileIdTests
{
    private readonly SessionFixture _fx;

    public PeerTileIdTests(SessionFixture fx) => _fx = fx;

    // Content-free maps (no registry row, no terrain, no warps, no spawns), one per fact so nothing is shared.
    private const ushort SnapshotMap = 60130, WideBeatMap = 60131;

    private const ushort ViewerX = 5, ViewerY = 10;
    private const ushort InStrict = 5;      // x in [0,17) from the (0,3) origin

    /// <summary>The character hook that widens a content-free map to 100x100, so a peer has somewhere to
    /// stand that is genuinely outside the viewer's drawn rect.</summary>
    private static void Wide(Character c) { c.MapXs = 100; c.MapYs = 100; }

    /// <summary>Every frame the recorder holds, as (opcode, entity id) in the order it was handed over — the
    /// same decode <c>PeerSweepBatchTests.Wire</c> uses. A <c>0x33</c> look carries its id at body offset 5
    /// (x u16, y u16, dir u8, then the id u32BE); a <c>0x0E</c> despawn carries a count byte and then its
    /// ids. Everything else is reported with id 0 so an unexpected frame shows up in the failure message.
    /// </summary>
    private static List<(byte op, uint id)> Wire(RecordingOutbound outbound)
    {
        var seq = new List<(byte, uint)>();
        foreach (var frame in outbound.Frames)
        {
            byte op = frame[3];
            byte[] body = TkCrypt.Crypt(frame[5..], frame[4], TkCrypt.LoginKey);
            if (op == 0x33) seq.Add((op, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(5))));
            else if (op == 0x0E)
                for (int i = 0; i < body[0]; i++) seq.Add((op, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1 + i * 4))));
            else seq.Add((op, 0u));
        }
        return seq;
    }

    private static string Render(List<(byte op, uint id)> seq) =>
        seq.Count == 0 ? "(nothing)" : string.Join(", ", seq.Select(f => $"0x{f.op:X2}#{f.id}"));

    /// <summary>(a) Every <see cref="PeerTile"/> the three snapshot sites produce carries the peer's own
    /// <c>PlayerId</c>, for every peer, through the real <c>World.EnterMap</c> and <c>World.View</c> on a map
    /// with several real sessions — and through the real <c>ReconcileViews</c>, whose array is private, by
    /// its only observable: the sweep's despawn frame carries the id the snapshot put in the tile.
    ///
    /// <para>Falsification: fill <c>Id</c> from <c>p.PlayerX</c> at the <c>EnterMap</c>, <c>View</c> and
    /// <c>ReconcileViews</c> sites. Run, confirm red, restore. Recorded in
    /// <c>briefs/reports/peertile-id-opus.md</c>.</para></summary>
    [Fact]
    public void EverySnapshotSiteCarriesThePeersOwnId()
    {
        var (a, _, _) = _fx.PlayerWith("TileIdA", _ => { }, SnapshotMap, 1, 1);
        var (b, _, _) = _fx.PlayerWith("TileIdB", _ => { }, SnapshotMap, 2, 1);
        var (c, _, _) = _fx.PlayerWith("TileIdC", _ => { }, SnapshotMap, 3, 1);
        try
        {
            // World.View: the read-only snapshot, every peer but the asker.
            var viewed = _fx.World.View(a, SnapshotMap);
            Assert.Equal(2, viewed.peers.Length);
            foreach (var t in viewed.peers)
                Assert.True(t.Id == t.Session.PlayerId,
                    $"World.View handed out a PeerTile for '{t.Session.CharName}' carrying id {t.Id} "
                  + $"where that session's PlayerId is {t.Session.PlayerId}");

            // World.EnterMap: the same snapshot on the join path. c is already on the map, so re-entering is
            // the cheapest way to get the returned array without seating a fourth session.
            var entered = _fx.World.EnterMap(c, SnapshotMap);
            Assert.Equal(2, entered.peers.Length);
            foreach (var t in entered.peers)
                Assert.True(t.Id == t.Session.PlayerId,
                    $"World.EnterMap handed out a PeerTile for '{t.Session.CharName}' carrying id {t.Id} "
                  + $"where that session's PlayerId is {t.Session.PlayerId}");

            Assert.Equal(new[] { a.PlayerId, b.PlayerId }, entered.peers.Select(t => t.Id).OrderBy(i => i).ToArray());
        }
        finally
        {
            _fx.World.LeaveMap(a, SnapshotMap);
            _fx.World.LeaveMap(b, SnapshotMap);
            _fx.World.LeaveMap(c, SnapshotMap);
        }
    }

    /// <summary>(a, continued) The <c>ReconcileViews</c> snapshot, through the real beat. Its
    /// <see cref="PeerTile"/> array never leaves <c>World</c>, so the id it carried is read back off the wire:
    /// a peer that walks out of the drawn rect is despawned by the sweep under the id the SNAPSHOT put in the
    /// tile, so a wrongly-filled <c>Id</c> sends the client a despawn for an entity it is not drawing and
    /// leaves the real one on screen forever.
    ///
    /// <para>The positions are written through <c>World.SetPlayerPosition</c> inside the mover's state
    /// monitor — the real walk-handler seam and lock order, and the one seam the snapshot reads.</para>
    ///
    /// <para>Falsification: as above. Run, confirm red, restore.</para></summary>
    [Fact]
    public void TheTickSnapshotDespawnsUnderThePeersOwnId()
    {
        var (viewer, outbound, _) = _fx.PlayerWith("TileIdBeatViewer", Wide, WideBeatMap, ViewerX, ViewerY);
        var (peer, _, _) = _fx.PlayerWith("TileIdBeatPeer", Wide, WideBeatMap, InStrict, ViewerY);
        try
        {
            // Entering drew the peer for the viewer, so it is tracked. One beat with both in view sends
            // nothing, which is the steady state this starts from.
            _fx.World.FlushTickForTest(new World.TickQueues());
            outbound.Clear();

            using (peer.EnterState())
                _fx.World.SetPlayerPosition(peer, 30, ViewerY);     // past the drawn rect x in [-1,18)

            _fx.World.FlushTickForTest(new World.TickQueues());

            // Only the viewport frames: a real beat also carries the tick's own unrelated chatter (a 0x0A
            // status refresh), which is not what this fact is about.
            var seq = Wire(outbound).Where(f => f.op is 0x33 or 0x0E).ToList();
            var expected = new List<(byte, uint)> { (0x0E, peer.PlayerId) };
            Assert.True(seq.SequenceEqual(expected),
                $"the tick's own snapshot must despawn the departing peer under its own id; expected "
              + $"{Render(expected)} but the viewport frames were {Render(seq)}");
        }
        finally
        {
            _fx.World.LeaveMap(viewer, WideBeatMap);
            _fx.World.LeaveMap(peer, WideBeatMap);
        }
    }

}
