using System.Buffers.Binary;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// What the peer sweep puts on the wire when it decides for every peer under ONE viewport lock and sends
/// afterwards, instead of taking the lock, deciding and sending once per peer.
///
/// <para>The cut is a cost cut — at 400 players on one map the old shape took 160,000 <c>EnterView</c>
/// acquire/release pairs a beat for, in the steady state, no frames at all — so what has to be pinned is the
/// thing a cost cut can silently break: the frames. Deferring every send to after the release changes WHEN a
/// frame is built relative to the other peers' decisions, and the claim this PR makes is that it changes
/// nothing else — same frames, same ids, same order, same count.</para>
///
/// <para>Both facts are green on the shape this replaces as well, and that is deliberate: they are the
/// regression guard on a refactor, not a description of new behaviour. What takes them red is breaking the
/// new shape — see the falsifications in briefs/reports/viewport-sweep-opus.md.</para>
///
/// <para>Geometry: a content-free map is 12x12, narrower and shorter than the 17x15 viewport, so
/// <c>EdgeAwareAnchor</c> centres it — vx = x + (17-12)/2 = x+2, vy = y + (15-12)/2 = y+1. From (5,10) the
/// rect origin is (-2,-1), the strict rect is x in [-2,15) and the drawn rect (pad 1) is x in [-3,16). So on
/// row 10: x=5 is inside the strict rect, x=15 is in the overdraw band, and x=16 is past the drawn rect.
/// Peer tiles come from <see cref="PeerTile"/>, which the caller supplies, so they are not limited to tiles
/// the 12x12 map actually has — exactly as <c>ViewportRectTests</c> parks its creatures.</para>
///
/// <para>Hygiene, as in <c>ViewportRectTests</c>: the fixture's <c>World</c> is shared and has no teardown,
/// so every seated player is removed in a <c>finally</c>.</para>
/// </summary>
[Collection("world")]
public class PeerSweepBatchTests
{
    private readonly SessionFixture _fx;

    public PeerSweepBatchTests(SessionFixture fx) => _fx = fx;

    // Content-free maps (no registry row, no terrain, no warps, no spawns), one per fact so nothing is shared.
    private const ushort BatchMap = 60070, SteadyMap = 60071;

    private const ushort ViewerX = 5, ViewerY = 10;
    private const ushort InStrict = 5;      // x in [-2,15)
    private const ushort InBand = 15;       // outside the strict rect, inside the drawn 19x17
    private const ushort PastDrawn = 16;    // outside both

    /// <summary>Every frame the recorder holds, as (opcode, entity id) in the order it was handed over. A
    /// <c>0x33</c> look carries its id at body offset 5 (x u16, y u16, dir u8, then the id u32BE); a
    /// <c>0x0E</c> despawn carries a count byte and then its ids. Everything else is reported with id 0 so an
    /// unexpected frame shows up in the failure message rather than being filtered out of it.</summary>
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

    /// <summary>One sweep, one peer in each of the four states the reconcile can be in — show, keep, despawn,
    /// re-assert from the overdraw band — and the frames come out in peer-list order with nothing extra.
    ///
    /// <para>This is the fact the batching had to not break. Under the old shape each peer's frame was built
    /// between its own decision and the next peer's; under the new one every decision is made first and the
    /// frames are built afterwards from the recorded decisions. The order is the list's either way, and a
    /// send loop that walked the buffer backwards, or that dropped the entries with nothing to send, would
    /// come out here rather than in a player's screen.</para></summary>
    [Fact]
    public void OneSweepShowsKeepsDespawnsAndReassertsInPeerOrder()
    {
        var (viewer, outbound, character) = _fx.PlayerWith("BatchViewer", _ => { }, BatchMap, ViewerX, ViewerY);
        var (toShow, _, _) = _fx.PlayerWith("BatchShow", _ => { }, BatchMap, 1, 1);
        var (toKeep, _, _) = _fx.PlayerWith("BatchKeep", _ => { }, BatchMap, 2, 1);
        var (toDespawn, _, _) = _fx.PlayerWith("BatchGone", _ => { }, BatchMap, 3, 1);
        var (toReassert, _, _) = _fx.PlayerWith("BatchBand", _ => { }, BatchMap, 4, 1);
        try
        {
            Assert.Equal((12, 12), (character.MapXs, character.MapYs));   // the geometry this is read off

            // Entering the map drew all four for the viewer, so all four are tracked. Arrange the two states
            // that are not "tracked and in view": forget one entirely, and park one in the overdraw band so
            // the sweep flags it as suspect (_edgePeers) without sending anything.
            viewer.DespawnEntity(toShow.PlayerId);
            viewer.SyncPeers(new[]
            {
                new PeerTile(toKeep, InStrict, ViewerY),
                new PeerTile(toDespawn, InStrict, ViewerY),
                new PeerTile(toReassert, InBand, ViewerY),
            });

            outbound.Clear();
            viewer.SyncPeers(new[]
            {
                new PeerTile(toShow, InStrict, ViewerY),       // untracked, in the strict rect -> 0x33
                new PeerTile(toKeep, InStrict, ViewerY),       // tracked, in the strict rect   -> nothing
                new PeerTile(toDespawn, PastDrawn, ViewerY),   // tracked, past the drawn rect  -> 0x0E
                new PeerTile(toReassert, InStrict, ViewerY),   // tracked, back from the band   -> 0x33
            });

            var seq = Wire(outbound);
            var expected = new List<(byte, uint)>
            {
                (0x33, toShow.PlayerId),
                (0x0E, toDespawn.PlayerId),
                (0x33, toReassert.PlayerId),
            };
            Assert.True(seq.SequenceEqual(expected),
                $"one sweep must send exactly a show, a despawn and a re-assert, in peer-list order; " +
                $"expected {Render(expected)} but the wire carried {Render(seq)}");
        }
        finally
        {
            foreach (var s in new[] { viewer, toShow, toKeep, toDespawn, toReassert })
                _fx.World.LeaveMap(s, BatchMap);
        }
    }

    /// <summary>The state the load runs actually measure: every peer already drawn, nobody moved, so the
    /// sweep decides "nothing" for all of them and the wire stays silent.
    ///
    /// <para>This is the case the cut is for — 399 peers a beat that produce no frames and, before this
    /// change, 399 viewport-lock acquisitions each. If the batching ever started re-asserting a peer it had
    /// already drawn, the hold would look faster and every client would be getting a redundant 0x33 per peer
    /// per beat, which is the silent failure AGENTS.md rule 3 is about.</para></summary>
    [Fact]
    public void ASweepOverPeersThatHaveNotMovedSendsNothing()
    {
        var (viewer, outbound, character) = _fx.PlayerWith("SteadyViewer", _ => { }, SteadyMap, ViewerX, ViewerY);
        var (a, _, _) = _fx.PlayerWith("SteadyA", _ => { }, SteadyMap, 1, 1);
        var (b, _, _) = _fx.PlayerWith("SteadyB", _ => { }, SteadyMap, 2, 1);
        try
        {
            Assert.Equal((12, 12), (character.MapXs, character.MapYs));

            var tiles = new[] { new PeerTile(a, InStrict, ViewerY), new PeerTile(b, InStrict, ViewerY) };
            viewer.SyncPeers(tiles);            // settle: both drawn and tracked

            outbound.Clear();
            viewer.SyncPeers(tiles);            // and again, with nothing changed
            viewer.SyncPeers(tiles);

            var seq = Wire(outbound);
            Assert.True(seq.Count == 0,
                $"a sweep over peers that have not moved must send nothing; the wire carried {Render(seq)}");
        }
        finally
        {
            foreach (var s in new[] { viewer, a, b }) _fx.World.LeaveMap(s, SteadyMap);
        }
    }
}
