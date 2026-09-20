using System.Buffers.Binary;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The drawn/edge HashSet PAIR and the one drawn-state store decide the same thing, on every transition,
/// for the same entity — the equivalence fact for the representation change in <c>Session.DrawnInside</c>.
///
/// <para><b>Why this needs a test by the "would this fail loudly?" rule.</b> Nothing about the change can
/// throw. The two representations differ in one branch each — "was it in the band" is a value compare where
/// it used to be a second set probe, and a state that has not changed is no longer written back — and a
/// mistake in either is a viewport bug nobody sees for hours: an entity the server believes it drew and the
/// client culled (no 0x0E ever follows it off the screen), or a re-assert that never fires (a peer you walk
/// up to who stays invisible until a room change). Both are exactly the class of defect the overdraw band
/// exists to prevent, and neither produces a log line.</para>
///
/// <para><b>The reference model below is the OLD representation</b>, the two <c>HashSet&lt;uint&gt;</c> the
/// server no longer keeps, written out statement for statement from the shape this change replaced
/// (upstream master @ <c>719e19d</c>, <c>Session.WorldApi.cs</c>'s <c>DecidePeerUnderViewLock</c>). It lives
/// HERE and nowhere else: it is a test oracle, not a second implementation the server can drift onto. The
/// facts drive the REAL sweeps and the model side by side through one scripted walk and compare what each
/// says at every step.</para>
///
/// <para><b>What is compared, at every step.</b> The model's decision (nothing / show / despawn) against the
/// frames the real sweep put on the wire, and the model's <c>shownAfter</c> — is this entity drawn NOW —
/// against the server's own answer, which for a mob is observable: <c>MoveMob</c> sends a 0x0C only for an
/// entity the viewer has drawn. The band half of the state is not directly observable and is not asserted
/// directly; the script is built so that it CANNOT be wrong silently, because every band state it reaches is
/// left through the branch that distinguishes it — a re-entry from the band sends a re-assert where a
/// re-entry from inside sends nothing, and an exit from the band despawns.</para>
///
/// <para><b>The script covers every transition of the three-state machine</b>: not drawn and out of view;
/// first show into the strict rect; a steady beat inside it; a drift into the overdraw band; a second beat
/// in the band; a return to the strict rect (the re-assert); an exit past the drawn rect from inside; a beat
/// while gone; a re-entry; and an exit past the drawn rect from the BAND rather than from inside.</para>
///
/// <para>Geometry, as in <c>EntitySweepBatchTests</c>: a content-free map is 12x12, narrower and shorter than
/// the 17x15 viewport, so <c>EdgeAwareAnchor</c> centres it — from (5,10) the strict rect is x in [-2,15) and
/// the drawn rect (pad 1) is x in [-3,16). So on row 10: x=5 is inside the strict rect, x=15 is in the
/// overdraw band, and x=16 is past the drawn rect. The model is given the same two rect answers the sweep
/// computes, as constants of the script, so what is compared is the STATE MACHINE and not the rect
/// arithmetic, which <c>ViewportRectTests</c> pins.</para>
///
/// <para>Falsification (run, confirm red, restore — recorded in
/// <c>briefs/reports/drawn-set-state-opus.md</c>): in <c>DecidePeerUnderViewLock</c>, change the re-assert
/// branch's <c>if (state != DrawnBand) return EntityDraw.Nothing;</c> to <c>if (state == DrawnBand) return
/// EntityDraw.Nothing;</c>. That is one transition of the machine inverted and nothing else.</para>
///
/// <para>Hygiene, as in <c>ViewportRectTests</c>: the fixture's <c>World</c> is shared and has no teardown,
/// so every seated player is removed in a <c>finally</c>. The mobs here are local objects handed straight to
/// the sweep and never registered with the world, so they need no teardown.</para>
/// </summary>
[Collection("world")]
public class DrawnStateEquivalenceTests
{
    private readonly SessionFixture _fx;

    public DrawnStateEquivalenceTests(SessionFixture fx) => _fx = fx;

    // Content-free maps (no registry row, no terrain, no warps, no spawns), one per fact so nothing is shared.
    private const ushort MobWalkMap = 60140, PeerWalkMap = 60141;

    private const ushort ViewerX = 5, ViewerY = 10;
    private const ushort InStrict = 5;      // x in [-2,15)
    private const ushort InBand = 15;       // outside the strict rect, inside the drawn 19x17
    private const ushort PastDrawn = 16;    // outside both

    /// <summary>What one beat's decision came to. The same three outcomes <c>Session.EntityDraw</c> has,
    /// which is private to the server.</summary>
    private enum Decision { Nothing, Show, Despawn }

    /// <summary>THE REFERENCE MODEL: the two-<c>HashSet</c> representation the server kept before this
    /// change, copied statement for statement from <c>DecidePeerUnderViewLock</c> as upstream master
    /// @ <c>719e19d</c> had it, with the rect tests lifted out into the caller's two booleans and the stamp
    /// (which is orthogonal to the representation, and pinned by the staleness facts) left out.
    ///
    /// <para>It exists only to be disagreed with. Nothing in the server calls it and nothing should: a
    /// second live implementation of this rule is exactly what <c>DecidePeerUnderViewLock</c>'s "it is the
    /// ONLY place that rule is written" forbids.</para></summary>
    private sealed class TwoSetModel
    {
        private readonly HashSet<uint> _shown = new();
        private readonly HashSet<uint> _edge = new();

        internal (Decision draw, bool drawnBefore, bool shownAfter) Decide(uint id, bool core, bool inDrawnRect)
        {
            bool drawnBefore = _shown.Contains(id);
            if (!drawnBefore)
            {
                if (!core) return (Decision.Nothing, false, false);
                _shown.Add(id);
                return (Decision.Show, false, true);
            }
            if (!inDrawnRect)
            {
                _shown.Remove(id); _edge.Remove(id);
                return (Decision.Despawn, true, false);
            }
            if (core)
            {
                if (!_edge.Remove(id)) return (Decision.Nothing, true, true);
                return (Decision.Show, true, true);
            }
            _edge.Add(id);
            return (Decision.Nothing, true, true);
        }
    }

    /// <summary>One beat of the script: where the entity stands, and the two rect answers that tile gives on
    /// the fixture's geometry. <paramref name="What"/> names the transition so a failure says which one.</summary>
    private readonly record struct Beat(string What, ushort X, bool Core, bool InDrawnRect);

    /// <summary>The walk, covering every transition of the three-state machine once.</summary>
    private static readonly Beat[] Walk =
    {
        new("out of view, never drawn", PastDrawn, false, false),
        new("first show into the strict rect", InStrict, true, true),
        new("a steady beat inside the strict rect", InStrict, true, true),
        new("drifts into the overdraw band", InBand, false, true),
        new("a steady beat in the band", InBand, false, true),
        new("returns to the strict rect from the band", InStrict, true, true),
        new("a steady beat inside again", InStrict, true, true),
        new("leaves the drawn rect from INSIDE", PastDrawn, false, false),
        new("a beat while gone", PastDrawn, false, false),
        new("re-enters the strict rect", InStrict, true, true),
        new("drifts into the band again", InBand, false, true),
        new("leaves the drawn rect from the BAND", PastDrawn, false, false),
        new("re-enters once more", InStrict, true, true),
        new("a last steady beat", InStrict, true, true),
    };

    /// <summary>Every frame the recorder holds, as (opcode, entity id) in the order it was handed over — the
    /// same decode <c>EntitySweepBatchTests.Wire</c> uses, plus the 0x33 peer look and the 0x0C move this
    /// fact uses as its "is it drawn" probe. A <c>0x07</c> creature list carries <c>count(u16BE)</c> and then
    /// 12-byte entries whose id is at entry offset 4; a <c>0x33</c> look carries its id at body offset 5; a
    /// <c>0x0C</c> move carries its id first; a 4.95 <c>0x0E</c> despawn carries a count byte and then its
    /// ids. Everything else is reported with id 0 so an unexpected frame shows up in the failure message.</summary>
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
            else if (op == 0x33) seq.Add((op, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(5))));
            else if (op == 0x0C) seq.Add((op, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0))));
            else if (op == 0x0E)
                for (int i = 0; i < body[0]; i++) seq.Add((op, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1 + i * 4))));
            else seq.Add((op, 0u));
        }
        return seq;
    }

    private static string Render(List<(byte op, uint id)> seq) =>
        seq.Count == 0 ? "(nothing)" : string.Join(", ", seq.Select(f => $"0x{f.op:X2}#{f.id}"));

    private Mob MobAt(string name, ushort x, ushort y) => new(_fx.World.AllocateMobId(), 1, x, y, name, 100);

    /// <summary>(a) MOBS: the real <c>SyncMobs</c> and the two-set model agree on every beat of the walk —
    /// the same decision, and the same answer to "is it drawn now".
    ///
    /// <para>The drawn half is read off the server rather than asserted from the inside: <c>MoveMob</c> sends
    /// a 0x0C for a mob the viewer has drawn and returns silently for one it has not, which is the store's
    /// membership and nothing else. So each beat asserts two things — the frames the sweep produced are
    /// exactly what the model decided, and the store's membership afterwards is exactly the model's
    /// <c>shownAfter</c>.</para></summary>
    [Fact]
    public void TheMobSweepDecidesWhatTheTwoSetRepresentationDecided()
    {
        var (viewer, outbound, character) = _fx.PlayerWith("DrawnStateMobViewer", _ => { }, MobWalkMap, ViewerX, ViewerY);
        var mob = MobAt("DrawnStateMob", PastDrawn, ViewerY);
        var model = new TwoSetModel();
        var mobs = new[] { mob };
        try
        {
            Assert.Equal((12, 12), (character.MapXs, character.MapYs));   // the geometry this is read off

            for (int i = 0; i < Walk.Length; i++)
            {
                var beat = Walk[i];
                var (draw, _, shownAfter) = model.Decide(mob.Id, beat.Core, beat.InDrawnRect);

                mob.X = beat.X;
                outbound.Clear();
                viewer.SyncMobs(mobs);

                var seq = Wire(outbound);
                var expected = draw switch
                {
                    Decision.Show => new List<(byte, uint)> { (0x07, mob.Id) },
                    Decision.Despawn => new List<(byte, uint)> { (0x0E, mob.Id) },
                    _ => new List<(byte, uint)>(),
                };
                Assert.True(seq.SequenceEqual(expected),
                    $"beat {i} ({beat.What}, x={beat.X}): the two-set representation decided {draw}, so the "
                  + $"wire must carry {Render(expected)} — it carried {Render(seq)}");

                // "Is it drawn now", asked of the server: a 0x0C goes out only for a drawn mob.
                outbound.Clear();
                viewer.MoveMob(mob.Id, beat.X, ViewerY, 0);
                bool serverSaysDrawn = Wire(outbound).Any(fr => fr.op == 0x0C && fr.id == mob.Id);
                Assert.True(serverSaysDrawn == shownAfter,
                    $"beat {i} ({beat.What}, x={beat.X}): the two-set representation says drawn={shownAfter} "
                  + $"after this beat; the server says drawn={serverSaysDrawn}");
            }
        }
        finally
        {
            _fx.World.LeaveMap(viewer, MobWalkMap);
        }
    }

    /// <summary>(b) PEERS: the same walk through the real <c>SyncPeers</c>, against the same model. A peer
    /// has no <c>MoveMob</c>-shaped membership probe — <c>MoveEntity</c> sends unconditionally — so this fact
    /// compares the frames alone, which is where a peer-store mistake reaches a player: a missing re-assert
    /// is an invisible player, and a missing despawn is one who never leaves the screen.
    ///
    /// <para>The peer is seated far away and its draw from the map entry forgotten first, so the walk starts
    /// from the same "not drawn" state the mob fact starts from. The tiles come from the
    /// <see cref="PeerTile"/> the caller supplies, so they are not limited to tiles the 12x12 map has.</para></summary>
    [Fact]
    public void ThePeerSweepDecidesWhatTheTwoSetRepresentationDecided()
    {
        var (viewer, outbound, _) = _fx.PlayerWith("DrawnStatePeerViewer", _ => { }, PeerWalkMap, ViewerX, ViewerY);
        var (peer, _, _) = _fx.PlayerWith("DrawnStatePeer", _ => { }, PeerWalkMap, 1, 1);
        var model = new TwoSetModel();
        try
        {
            viewer.DespawnEntity(peer.PlayerId);            // forget the draw the map entry made

            for (int i = 0; i < Walk.Length; i++)
            {
                var beat = Walk[i];
                var (draw, _, _) = model.Decide(peer.PlayerId, beat.Core, beat.InDrawnRect);

                outbound.Clear();
                viewer.SyncPeers(new[] { new PeerTile(peer, peer.PlayerId, beat.X, ViewerY) });

                var seq = Wire(outbound);
                var expected = draw switch
                {
                    Decision.Show => new List<(byte, uint)> { (0x33, peer.PlayerId) },
                    Decision.Despawn => new List<(byte, uint)> { (0x0E, peer.PlayerId) },
                    _ => new List<(byte, uint)>(),
                };
                Assert.True(seq.SequenceEqual(expected),
                    $"beat {i} ({beat.What}, x={beat.X}): the two-set representation decided {draw}, so the "
                  + $"wire must carry {Render(expected)} — it carried {Render(seq)}");
            }
        }
        finally
        {
            viewer.DespawnEntity(peer.PlayerId);
            _fx.World.LeaveMap(viewer, PeerWalkMap);
            _fx.World.LeaveMap(peer, PeerWalkMap);
        }
    }
}
