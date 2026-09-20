using System.Buffers.Binary;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The order <c>Session.SyncMobs</c> decides in, now that it takes a concrete <c>Mob[]</c> and walks it with
/// an index loop.
///
/// <para>The sweep used to take an <c>IReadOnlyList&lt;Mob&gt;</c> and <c>foreach</c> it UNDER
/// <c>_viewLock</c>, so the interface enumerator — arbitrary code by type — ran inside the viewer's viewport
/// lock, kept harmless only by the convention that every caller passed an array. The parameter makes that a
/// guarantee instead. Unlike the peer sweep's equivalent change (PR #256) there was no copy pass to delete
/// here, so the saving is the enumerator alone and it is small; what matters for correctness is that nothing
/// else moved.</para>
///
/// <para>Order is the half of that claim a loop rewrite can break silently. Two mobs can stand on the same
/// tile, a show and a despawn for the same client are ordered events, and
/// <c>EntitySweepBatchTests.OneMobSweepShowsKeepsDespawnsAndReassertsInListOrder</c> already pins that the
/// SENDS come out in the order the decisions were taken. What this file pins is the other end: that the
/// decision order is the ARRAY's order — not the ids' ascending order, and not the order the mobs were
/// allocated, both of which a wrong loop would accidentally agree with if the fixture happened to be built in
/// array order. So the array here is deliberately the reverse of both. This is the mob twin of
/// <c>PeerSweepArrayOrderTests</c>.</para>
///
/// <para>Geometry, as in <c>EntitySweepBatchTests</c>: a content-free map is 12x12, narrower and shorter than
/// the 17x15 viewport, so <c>EdgeAwareAnchor</c> centres it — vx = x+2, vy = y+1. From (5,10) the rect origin
/// is (-2,-1), the strict rect is x in [-2,15) and the drawn rect (pad 1) is x in [-3,16). On row 10, x=5 is
/// in the strict rect and x=16 is past the drawn rect.</para>
///
/// <para>Hygiene, as in <c>EntitySweepBatchTests</c>: the fixture's <c>World</c> is shared and has no
/// teardown, so the seated viewer is removed in a <c>finally</c>. The mobs are local objects handed straight
/// to the sweep and never registered with the world, so they need none.</para>
/// </summary>
[Collection("world")]
public class MobSweepArrayOrderTests
{
    private readonly SessionFixture _fx;

    public MobSweepArrayOrderTests(SessionFixture fx) => _fx = fx;

    private const ushort OrderMap = 60135;

    private const ushort ViewerX = 5, ViewerY = 10;
    private const ushort InStrict = 5;      // x in [-2,15)
    private const ushort PastDrawn = 16;    // outside both rects

    /// <summary>Every frame the recorder holds, as (opcode, entity id) in the order it was handed over — the
    /// same decode <c>EntitySweepBatchTests.Wire</c> uses. A <c>0x07</c> creature list carries
    /// <c>count(u16BE)</c> and then 12-byte entries whose id is at entry offset 4; a 4.95 <c>0x0E</c> despawn
    /// carries a count byte and then that many u32BE ids.</summary>
    private static List<(byte op, uint id)> Wire(RecordingOutbound outbound)
    {
        var seq = new List<(byte, uint)>();
        foreach (var frame in outbound.Frames)
        {
            byte op = frame[3];
            byte[] body = TkCrypt.Crypt(frame[5..], frame[4], TkCrypt.LoginKey);
            if (op == 0x07)
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

    private Mob MobAt(string name, ushort x, ushort y) => new(_fx.World.AllocateMobId(), 1, x, y, name, 100);

    /// <summary>Four mobs, handed to the sweep in the reverse of the order they were allocated (and so in
    /// descending id order), with one of them moved past the drawn rect: the show, the despawn and the second
    /// show come out in the ARRAY's order.
    ///
    /// <para>Three of the four produce a frame and the fourth produces none, so the fact also pins that a mob
    /// with nothing to send does not disturb the order of the ones around it.</para>
    ///
    /// <para><b>Falsified</b> by walking the array backwards in <c>SyncMobs</c>
    /// (<c>for (int i = mobs.Length - 1; i &gt;= 0; i--)</c>): red with the three frames in ascending id
    /// order instead ("expected 0x07#third, 0x0E#second, 0x07#first but the wire carried" the three reversed).
    /// <c>EntitySweepBatchTests.OneMobSweepShowsKeepsDespawnsAndReassertsInListOrder</c> goes red on that same
    /// edit, but its array is in ascending id order, so it cannot tell array order from id order and would
    /// stay green on a loop that sorted. This one's array is the reverse of both.</para></summary>
    [Fact]
    public void TheMobSweepDecidesInArrayOrderNotInIdOrder()
    {
        var (viewer, outbound, character) = _fx.PlayerWith("MobOrderViewer", _ => { }, OrderMap, ViewerX, ViewerY);
        var first = MobAt("OrderMobFirst", InStrict, ViewerY);
        var second = MobAt("OrderMobSecond", InStrict, ViewerY);
        var third = MobAt("OrderMobThird", InStrict, ViewerY);
        var fourth = MobAt("OrderMobFourth", InStrict, ViewerY);
        try
        {
            Assert.Equal((12, 12), (character.MapXs, character.MapYs));   // the geometry this is read off

            // Ids ascend in allocation order, so "array order" and "id order" are opposites below.
            Assert.True(first.Id < second.Id && second.Id < third.Id && third.Id < fourth.Id,
                        "the fixture must hand out ascending mob ids for the reversal to mean anything");

            // Draw two of the four so they are tracked; the other two are never swept, so they are untracked
            // and need a show. Of the tracked pair, one will walk out of view and one will do nothing.
            viewer.SyncMobs(new[] { fourth, second });
            outbound.Clear();

            second.X = PastDrawn;         // tracked, past the drawn rect -> 0x0E
            viewer.SyncMobs(new[]
            {
                fourth,                   // tracked, in view      -> nothing
                third,                    // untracked, in view    -> 0x07
                second,                   // tracked, out of view  -> 0x0E
                first,                    // untracked, in view    -> 0x07
            });

            var seq = Wire(outbound);
            var expected = new List<(byte, uint)>
            {
                (0x07, third.Id),
                (0x0E, second.Id),
                (0x07, first.Id),
            };
            Assert.True(seq.SequenceEqual(expected),
                $"the sweep must decide in the snapshot array's order, not the ids' " +
                $"(array order is #{fourth.Id}, #{third.Id}, #{second.Id}, #{first.Id}); " +
                $"expected {Render(expected)} but the wire carried {Render(seq)}");
        }
        finally { _fx.World.LeaveMap(viewer, OrderMap); }
    }
}
