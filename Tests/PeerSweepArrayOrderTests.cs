using System.Buffers.Binary;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The order <c>Session.SyncPeers</c> decides in, now that it decides straight off the snapshot array.
///
/// <para>The sweep used to take an <c>IReadOnlyList&lt;PeerTile&gt;</c> and copy every peer into a rented
/// scratch array before taking <c>_viewLock</c>, because enumerating an interface is arbitrary code and
/// arbitrary code must not run under a view lock. It now takes a concrete <c>PeerTile[]</c> and indexes it
/// inside the one acquisition. That is a cost cut — the copy pass measured about 4.1 µs per viewer per beat
/// in Debug on the viewport profile's 400-viewer fixture — and the claim it makes is that nothing else
/// changed: the same peers are decided with the same ids and tiles in the same order.</para>
///
/// <para>Order is the half of that claim a loop rewrite can break silently, and it is not cosmetic. Two
/// peers can occupy the same tile, a show and a despawn for the same client are ordered events, and
/// <c>PeerSweepBatchTests</c> already pins that the SENDS come out in the order the decisions were taken.
/// What this file pins is the other end: that the decision order is the ARRAY's order — not the ids'
/// ascending order, and not the order the peers joined the map, both of which a wrong loop would
/// accidentally agree with if the fixture were built in array order. So the array here is deliberately the
/// reverse of both.</para>
///
/// <para>Geometry, as in <c>PeerSweepBatchTests</c>: a content-free map is 12x12, narrower and shorter than
/// the 17x15 viewport, so <c>EdgeAwareAnchor</c> centres it — vx = x+2, vy = y+1. From (5,10) the rect
/// origin is (-2,-1), the strict rect is x in [-2,15) and the drawn rect (pad 1) is x in [-3,16). On row 10,
/// x=5 is in the strict rect and x=16 is past the drawn rect.</para>
///
/// <para>Hygiene, as in <c>ViewportRectTests</c>: the fixture's <c>World</c> is shared and has no teardown,
/// so every seated player is removed in a <c>finally</c>.</para>
/// </summary>
[Collection("world")]
public class PeerSweepArrayOrderTests
{
    private readonly SessionFixture _fx;

    public PeerSweepArrayOrderTests(SessionFixture fx) => _fx = fx;

    private const ushort OrderMap = 60134;

    private const ushort ViewerX = 5, ViewerY = 10;
    private const ushort InStrict = 5;      // x in [-2,15)
    private const ushort PastDrawn = 16;    // outside both rects

    /// <summary>Every frame the recorder holds, as (opcode, entity id) in the order it was handed over — the
    /// same decode <c>PeerSweepBatchTests.Wire</c> uses. A <c>0x33</c> look carries its id at body offset 5;
    /// a <c>0x0E</c> despawn carries a count byte then that many u32BE ids.</summary>
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

    /// <summary>Four peers, handed to the sweep in the reverse of the order they joined the map (and so in
    /// descending id order), with one of them walked past the drawn rect: the show, the despawn and the
    /// second show come out in the ARRAY's order.
    ///
    /// <para>Three of the four produce a frame and the fourth produces none, so the fact also pins that a
    /// peer with nothing to send does not disturb the order of the ones around it.</para>
    ///
    /// <para><b>Falsified</b> by walking the array backwards in <c>SyncPeers</c>
    /// (<c>for (int i = peers.Length - 1; i &gt;= 0; i--)</c>): red with the three frames in ascending id
    /// order instead. That is the mistake this exists to catch, and it is invisible to every other peer
    /// sweep fact, because the fixtures they build happen to be in array order already.</para></summary>
    [Fact]
    public void TheSweepDecidesInArrayOrderNotInIdOrder()
    {
        var (viewer, outbound, character) = _fx.PlayerWith("OrderViewer", _ => { }, OrderMap, ViewerX, ViewerY);
        var (first, _, _) = _fx.PlayerWith("OrderFirst", _ => { }, OrderMap, 1, 1);
        var (second, _, _) = _fx.PlayerWith("OrderSecond", _ => { }, OrderMap, 2, 1);
        var (third, _, _) = _fx.PlayerWith("OrderThird", _ => { }, OrderMap, 3, 1);
        var (fourth, _, _) = _fx.PlayerWith("OrderFourth", _ => { }, OrderMap, 4, 1);
        try
        {
            Assert.Equal((12, 12), (character.MapXs, character.MapYs));   // the geometry this is read off

            // Ids ascend in join order, so "array order" and "id order" are opposites below.
            Assert.True(first.PlayerId < second.PlayerId && second.PlayerId < third.PlayerId
                        && third.PlayerId < fourth.PlayerId,
                        "the fixture must hand out ascending ids for the reversal to mean anything");

            // Entering the map drew all four, so all four are tracked. Forget two of them so they need a
            // show, and leave the other two tracked: one will walk out of view, one will do nothing.
            viewer.DespawnEntity(first.PlayerId);
            viewer.DespawnEntity(third.PlayerId);
            outbound.Clear();

            viewer.SyncPeers(new[]
            {
                new PeerTile(fourth, InStrict, ViewerY),    // tracked, in view      -> nothing
                new PeerTile(third, InStrict, ViewerY),     // untracked, in view    -> 0x33
                new PeerTile(second, PastDrawn, ViewerY),   // tracked, out of view  -> 0x0E
                new PeerTile(first, InStrict, ViewerY),     // untracked, in view    -> 0x33
            });

            var seq = Wire(outbound);
            var expected = new List<(byte, uint)>
            {
                (0x33, third.PlayerId),
                (0x0E, second.PlayerId),
                (0x33, first.PlayerId),
            };
            Assert.True(seq.SequenceEqual(expected),
                $"the sweep must decide in the snapshot array's order, not the ids' " +
                $"(array order is #{fourth.PlayerId}, #{third.PlayerId}, #{second.PlayerId}, #{first.PlayerId}); " +
                $"expected {Render(expected)} but the wire carried {Render(seq)}");
        }
        finally
        {
            foreach (var s in new[] { viewer, first, second, third, fourth })
                _fx.World.LeaveMap(s, OrderMap);
        }
    }
}
