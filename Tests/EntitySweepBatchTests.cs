using System.Buffers.Binary;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// What the MOB and GROUND-ITEM sweeps put on the wire now that they decide for every entity under ONE
/// viewport lock and send afterwards — the twin of <see cref="PeerSweepBatchTests"/>, which pins the same
/// thing for peers.
///
/// <para>The cut is a cost cut with two different shapes behind it. <c>SyncMobs</c> already took one
/// acquisition for its whole loop, but it built and sent every packet under it, so the hold span on a beat
/// that sends was the entire sweep (measured: 51.5us of a 51.7us sweep, Debug, 400 viewers x 305 mobs of which
/// 40 leave and 40 enter each beat). <c>SyncGroundItems</c> took one acquisition PER ITEM — the shape
/// <c>ReconcilePeer</c> had before PR #245 — and sent without revalidating. Both now capture outside the lock,
/// decide under one acquisition and send after releasing it.</para>
///
/// <para>What a cost cut can silently break is the frames, so that is what these facts pin: the exact
/// <c>(opcode, id)</c> sequence one sweep produces, and the silence of a sweep with nothing to do. All three
/// are green on the shape they replace as well, deliberately — they are the regression guard on a refactor,
/// not a description of new behaviour. What takes them red is breaking the new shape; see the falsifications
/// in briefs/reports/sweep-deferred-sends-opus.md.</para>
///
/// <para>Geometry, as in <see cref="PeerSweepBatchTests"/>: a content-free map is 12x12, narrower and shorter
/// than the 17x15 viewport, so <c>EdgeAwareAnchor</c> centres it — vx = x + (17-12)/2 = x+2, vy = y +
/// (15-12)/2 = y+1. From (5,10) the rect origin is (-2,-1), the strict rect is x in [-2,15) and the drawn rect
/// (pad 1) is x in [-3,16). So on row 10: x=5 is inside the strict rect, x=15 is in the overdraw band, and
/// x=16 is past the drawn rect. Mobs and items are parked at tiles by the caller, exactly as
/// <c>ViewportRectTests</c> parks its creatures, so they are not limited to tiles the 12x12 map has.</para>
///
/// <para>Hygiene, as in <c>ViewportRectTests</c>: the fixture's <c>World</c> is shared and has no teardown, so
/// every seated player is removed in a <c>finally</c>. The mobs and items here are local objects handed
/// straight to the sweep and never registered with the world, so they need no teardown.</para>
/// </summary>
[Collection("world")]
public class EntitySweepBatchTests
{
    private readonly SessionFixture _fx;

    public EntitySweepBatchTests(SessionFixture fx) => _fx = fx;

    // Content-free maps (no registry row, no terrain, no warps, no spawns), one per fact so nothing is shared.
    private const ushort MobBatchMap = 60110, MobSteadyMap = 60111, ItemBatchMap = 60112;

    private const ushort ViewerX = 5, ViewerY = 10;
    private const ushort InStrict = 5;      // x in [-2,15)
    private const ushort InBand = 15;       // outside the strict rect, inside the drawn 19x17
    private const ushort PastDrawn = 16;    // outside both

    /// <summary>Every frame the recorder holds, as (opcode, entity id) in the order it was handed over. A
    /// <c>0x07</c> creature list carries <c>count(u16BE)</c> and then 12-byte entries whose id is at entry
    /// offset 4 (x u16, y u16, then the id u32BE); a 4.95 <c>0x0E</c> despawn carries a count byte and then
    /// its ids. Everything else is reported with id 0 so an unexpected frame shows up in the failure message
    /// rather than being filtered out of it.</summary>
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

    /// <summary>A floor item the sweep can draw without touching the content table: <c>ItemId = -1</c> makes
    /// <c>ShowGroundItem</c> send <c>Graphic</c> straight through, which is the path a coin/loot drop with no
    /// item definition already takes (see <c>World</c>'s drop roll).</summary>
    private GroundItem ItemAt(ushort x, ushort y) =>
        new() { Id = _fx.World.AllocateItemId(), ItemId = -1, Graphic = 100, X = x, Y = y };

    /// <summary>One MOB sweep, one mob in each of the four states the reconcile can be in — show, keep,
    /// despawn, re-assert from the overdraw band — and the frames come out in list order with nothing extra.
    ///
    /// <para>This is the fact the deferral had to not break. Under the old shape each mob's packet was built
    /// under the viewport lock between its own decision and the next mob's; under the new one every decision
    /// is made first, the lock is released, and the frames are built afterwards from the recorded decisions.
    /// The order is the list's either way, and a send loop that walked the buffer backwards, that dropped the
    /// entries with nothing to send, or that lost the re-assert branch would come out here rather than on a
    /// player's screen.</para></summary>
    [Fact]
    public void OneMobSweepShowsKeepsDespawnsAndReassertsInListOrder()
    {
        var (viewer, outbound, character) = _fx.PlayerWith("MobBatchViewer", _ => { }, MobBatchMap, ViewerX, ViewerY);
        var toShow = MobAt("BatchShow", InStrict, ViewerY);
        var toKeep = MobAt("BatchKeep", InStrict, ViewerY);
        var toDespawn = MobAt("BatchGone", InStrict, ViewerY);
        var toReassert = MobAt("BatchBand", InStrict, ViewerY);
        try
        {
            Assert.Equal((12, 12), (character.MapXs, character.MapYs));   // the geometry this is read off

            // Arrange the three states that are not "untracked": draw all three, then step one of them into
            // the overdraw band so the sweep flags it as suspect (_edgeMobs) without sending anything.
            viewer.SyncMobs(new[] { toKeep, toDespawn, toReassert });
            toReassert.X = InBand;
            viewer.SyncMobs(new[] { toKeep, toDespawn, toReassert });

            outbound.Clear();
            toDespawn.X = PastDrawn;      // tracked, past the drawn rect  -> 0x0E
            toReassert.X = InStrict;      // tracked, back from the band   -> 0x07
            viewer.SyncMobs(new[] { toShow, toKeep, toDespawn, toReassert });

            var seq = Wire(outbound);
            var expected = new List<(byte, uint)>
            {
                (0x07, toShow.Id),
                (0x0E, toDespawn.Id),
                (0x07, toReassert.Id),
            };
            Assert.True(seq.SequenceEqual(expected),
                $"one mob sweep must send exactly a show, a despawn and a re-assert, in list order; " +
                $"expected {Render(expected)} but the wire carried {Render(seq)}");
        }
        finally { _fx.World.LeaveMap(viewer, MobBatchMap); }
    }

    /// <summary>The state the load runs actually measure: every mob already drawn, nobody moved, so the sweep
    /// decides "nothing" for all of them and the wire stays silent.
    ///
    /// <para>This is the case the cut is for — 305 mobs a beat that produce no frames. If the deferral ever
    /// started re-asserting a mob it had already drawn, the hold would look shorter and every client would be
    /// getting a redundant 0x07 per mob per beat, which is the silent failure AGENTS.md rule 3 is
    /// about.</para></summary>
    [Fact]
    public void ASweepOverMobsThatHaveNotMovedSendsNothing()
    {
        var (viewer, outbound, character) = _fx.PlayerWith("MobSteadyViewer", _ => { }, MobSteadyMap, ViewerX, ViewerY);
        var a = MobAt("SteadyMobA", InStrict, ViewerY);
        var b = MobAt("SteadyMobB", InStrict, ViewerY);
        try
        {
            Assert.Equal((12, 12), (character.MapXs, character.MapYs));

            var mobs = new[] { a, b };
            viewer.SyncMobs(mobs);              // settle: both drawn and tracked

            outbound.Clear();
            viewer.SyncMobs(mobs);              // and again, with nothing changed
            viewer.SyncMobs(mobs);

            var seq = Wire(outbound);
            Assert.True(seq.Count == 0,
                $"a sweep over mobs that have not moved must send nothing; the wire carried {Render(seq)}");
        }
        finally { _fx.World.LeaveMap(viewer, MobSteadyMap); }
    }

    /// <summary>The same fact on the GROUND-ITEM sweep, with two items on the map: one untracked and in view,
    /// one tracked and past the drawn rect. Items have no overdraw band — a stationary item can only leave or
    /// enter by US moving — so two states is all there are, and the sweep must produce exactly one 0x07 and
    /// one 0x0E, in list order.
    ///
    /// <para>This sweep changed shape twice over: one acquisition for the whole list instead of one per item
    /// (plus a second on every despawn), and every send revalidated where none was before. Either change
    /// dropping, reordering or duplicating a frame lands here.</para></summary>
    [Fact]
    public void OneItemSweepShowsAndDespawnsInListOrder()
    {
        var (viewer, outbound, character) = _fx.PlayerWith("ItemBatchViewer", _ => { }, ItemBatchMap, ViewerX, ViewerY);
        var toShow = ItemAt(InStrict, ViewerY);
        var toDespawn = ItemAt(InStrict, ViewerY);
        try
        {
            Assert.Equal((12, 12), (character.MapXs, character.MapYs));

            viewer.SyncGroundItems(new[] { toShow, toDespawn });    // settle: both drawn and tracked
            viewer.DespawnEntity(toShow.Id);                        // forget one, so the sweep must draw it

            outbound.Clear();
            toDespawn.X = PastDrawn;
            viewer.SyncGroundItems(new[] { toShow, toDespawn });

            var seq = Wire(outbound);
            var expected = new List<(byte, uint)> { (0x07, toShow.Id), (0x0E, toDespawn.Id) };
            Assert.True(seq.SequenceEqual(expected),
                $"one item sweep must send exactly a show and a despawn, in list order; " +
                $"expected {Render(expected)} but the wire carried {Render(seq)}");

            // And the show is tracked like any other: the next sweep over the same two items is silent.
            outbound.Clear();
            viewer.SyncGroundItems(new[] { toShow, toDespawn });
            Assert.True(Wire(outbound).Count == 0,
                $"the item drawn by the previous sweep must be tracked, so the next sweep is silent; " +
                $"the wire carried {Render(Wire(outbound))}");
        }
        finally { _fx.World.LeaveMap(viewer, ItemBatchMap); }
    }
}
