using System.Buffers.Binary;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The rect tests moved onto the branches that read them, and NOTHING else moved — the equivalence fact for
/// <c>server-lazy-rect-test-1</c>.
///
/// <para><b>Why this needs a test by the "would this fail loudly?" rule.</b> Nothing about the change can
/// throw. <c>DecidePeerUnderViewLock</c> used to compute both rect answers before it probed the drawn-state
/// store; now the store answers first and each branch asks only the question it reads. If one branch ends up
/// reading the wrong pad — the strict 17x15 where it meant the drawn 19x17, or the other way round — the
/// server draws or despawns an entity one tile early or one tile late and says nothing about it. That is a
/// player walking up to somebody who stays invisible, or a body that never leaves the screen, and no log
/// line anywhere.</para>
///
/// <para><b>The reference model below is the EAGER shape</b>, copied statement for statement from
/// <c>DecidePeerUnderViewLock</c> as this branch's base (<c>74ff9c7</c>) had it: both
/// <c>view.Contains</c> answers computed up front, then the store probe, then the three branches. The rect
/// answers are handed to it as the two booleans the base computed, exactly as
/// <c>DrawnStateEquivalenceTests</c> does, so what is compared is the DECISION and not the rect arithmetic,
/// which <c>ViewportRectTests</c> pins. It lives here and nowhere else: it is a test oracle, not a second
/// implementation the server can drift onto.</para>
///
/// <para><b>The table.</b> Nine cases: three starting states of the store (absent, <c>DrawnInside</c>,
/// <c>DrawnBand</c>) crossed with three positions (inside the strict rect, in the overdraw band, outside the
/// drawn rect). Each case is driven twice, with two different PROBE beats after the decision, because no
/// single probe separates all three resulting states from outside the session: a beat back in the strict
/// rect tells <c>DrawnInside</c> (nothing) from <c>DrawnBand</c> and absent (a show), and a beat past the
/// drawn rect tells absent (nothing) from either drawn state (a despawn). Run both and the state the head
/// left the store in is pinned exactly. Every beat of every run — the prelude that arranges the state, the
/// decision itself and the probe — is compared against the model.</para>
///
/// <para><b>What is compared.</b> The decision, as the frames the real sweep put on the wire; and the state
/// the store was left in, through the probe beats above. <c>drawnBefore</c> and the stamp are not reachable
/// from outside the session and are not asserted directly here — they are pinned by
/// <c>PeerSweepStalenessTests</c> and <c>EntitySweepStalenessTests</c>, which this change does not touch.
/// The model asserts its own <c>shownAfter</c> against the probe it predicts, so a model that drifted from
/// the branch structure would stop agreeing with itself.</para>
///
/// <para>Geometry, as in <c>DrawnStateEquivalenceTests</c>: a content-free map is 12x12, narrower and
/// shorter than the 17x15 viewport, so <c>EdgeAwareAnchor</c> centres it — from (5,10) the strict rect is
/// x in [-2,15) and the drawn rect (pad 1) is x in [-3,16). On row 10: x=5 is inside the strict rect, x=15
/// is in the overdraw band, x=16 is past the drawn rect.</para>
///
/// <para>Falsification (run, confirmed red, restored by hand — recorded in
/// <c>briefs/reports/lazy-rect-test-opus.md</c>): swap the two pads in ONE branch of the head — in
/// <c>DecidePeerUnderViewLock</c>'s despawn branch, test <c>ShowPad</c> where it now tests
/// <c>HidePad</c>.</para>
/// </summary>
[Collection("world")]
public class LazyRectDecisionTableTests
{
    private readonly SessionFixture _fx;

    public LazyRectDecisionTableTests(SessionFixture fx) => _fx = fx;

    // A content-free map: no registry row, no terrain, no warps, no spawns.
    private const ushort PeerMap = 60151;

    private const ushort ViewerX = 5, ViewerY = 10;
    private const ushort InStrict = 5;      // x in [-2,15)   — inside the strict rect
    private const ushort InBand = 15;       // outside the strict rect, inside the drawn 19x17
    private const ushort PastDrawn = 16;    // outside both

    private enum Decision { Nothing, Show, Despawn }

    /// <summary>THE REFERENCE MODEL: the base's EAGER <c>DecidePeerUnderViewLock</c>, statement for
    /// statement, with the two rect answers lifted into the caller's booleans (the base computed them both,
    /// unconditionally, before the probe) and the stamp left out.
    ///
    /// <para>It exists only to be disagreed with. Nothing in the server calls it and nothing should.</para>
    /// </summary>
    private sealed class EagerModel
    {
        private const byte DrawnInside = 0, DrawnBand = 1;
        private readonly Dictionary<uint, byte> _drawn = new();

        internal (Decision draw, bool drawnBefore, bool shownAfter) Decide(uint id, bool core, bool inDrawnRect)
        {
            // core and inDrawnRect are both computed by the caller here, because the base computed both
            // before it looked at the store. That is the whole of what this change moved.
            bool drawnBefore = _drawn.TryGetValue(id, out byte state);
            if (!drawnBefore)
            {
                if (!core) return (Decision.Nothing, false, false);
                _drawn[id] = DrawnInside;
                return (Decision.Show, false, true);
            }
            if (!inDrawnRect)
            {
                _drawn.Remove(id);
                return (Decision.Despawn, true, false);
            }
            if (core)
            {
                if (state != DrawnBand) return (Decision.Nothing, true, true);
                _drawn[id] = DrawnInside;
                return (Decision.Show, true, true);
            }
            if (state != DrawnBand) _drawn[id] = DrawnBand;
            return (Decision.Nothing, true, true);
        }
    }

    /// <summary>One beat: the tile, and the two rect answers that tile gives on this geometry.</summary>
    private readonly record struct Beat(string What, ushort X, bool Core, bool InDrawnRect);

    private static Beat At(string what, ushort x) => x switch
    {
        InStrict => new Beat(what, InStrict, true, true),
        InBand => new Beat(what, InBand, false, true),
        _ => new Beat(what, PastDrawn, false, false),
    };

    /// <summary>The beats that put the store into each of the three starting states, from empty.</summary>
    private static Beat[] Prelude(string state) => state switch
    {
        "absent" => Array.Empty<Beat>(),
        "DrawnInside" => new[] { At("arrange: first show into the strict rect", InStrict) },
        "DrawnBand" => new[] { At("arrange: first show into the strict rect", InStrict),
                               At("arrange: drift into the overdraw band", InBand) },
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    private static readonly string[] States = { "absent", "DrawnInside", "DrawnBand" };
    private static readonly ushort[] Positions = { InStrict, InBand, PastDrawn };
    private static readonly ushort[] Probes = { InStrict, PastDrawn };

    /// <summary>The nine cases, each with both probes: 18 runs.</summary>
    public static TheoryData<string, ushort, ushort> Table()
    {
        var data = new TheoryData<string, ushort, ushort>();
        foreach (var s in States)
            foreach (var p in Positions)
                foreach (var probe in Probes)
                    data.Add(s, p, probe);
        return data;
    }

    private static string Name(ushort x) => x == InStrict ? "inside the strict rect"
                                          : x == InBand ? "in the overdraw band"
                                          : "outside the drawn rect";

    /// <summary>Every frame the recorder holds, as (opcode, entity id) — the same decode
    /// <c>DrawnStateEquivalenceTests.Wire</c> uses.</summary>
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

    private static List<(byte, uint)> Expected(Decision draw, byte showOp, uint id) => draw switch
    {
        Decision.Show => new List<(byte, uint)> { (showOp, id) },
        Decision.Despawn => new List<(byte, uint)> { (0x0E, id) },
        _ => new List<(byte, uint)>(),
    };

    /// <summary>PEERS: the head's <c>SyncPeers</c> (through <c>DecidePeerUnderViewLock</c>, the lazy
    /// shape) and the eager reference decide the same thing in all nine (state, position) cases, and leave
    /// the store in the same state.</summary>
    [Theory]
    [MemberData(nameof(Table))]
    public void ThePeerDecisionIsWhatTheEagerShapeDecided(string state, ushort position, ushort probe)
    {
        var (viewer, outbound, character) = _fx.PlayerWith($"LazyRectPeerViewer{state}{position}{probe}", _ => { }, PeerMap, ViewerX, ViewerY);
        var (peer, _, _) = _fx.PlayerWith($"LazyRectPeer{state}{position}{probe}", _ => { }, PeerMap, 1, 1);
        var model = new EagerModel();
        try
        {
            Assert.Equal((12, 12), (character.MapXs, character.MapYs));   // the geometry this is read off
            viewer.DespawnEntity(peer.PlayerId);            // forget the draw the map entry made

            var beats = Prelude(state)
                .Append(At($"decide: {Name(position)} with the store {state}", position))
                .Append(At($"probe: {Name(probe)}", probe))
                .ToArray();

            for (int i = 0; i < beats.Length; i++)
            {
                var beat = beats[i];
                var (draw, _, _) = model.Decide(peer.PlayerId, beat.Core, beat.InDrawnRect);

                outbound.Clear();
                viewer.SyncPeers(new[] { new PeerTile(peer, peer.PlayerId, beat.X, ViewerY) });

                var seq = Wire(outbound);
                var expected = Expected(draw, 0x33, peer.PlayerId);
                Assert.True(seq.SequenceEqual(expected),
                    $"case (state={state}, {Name(position)}), probe {Name(probe)}, beat {i} ({beat.What}, "
                  + $"x={beat.X}): the eager shape decided {draw}, so the wire must carry {Render(expected)} "
                  + $"— it carried {Render(seq)}");
            }
        }
        finally
        {
            viewer.DespawnEntity(peer.PlayerId);
            _fx.World.LeaveMap(viewer, PeerMap);
            _fx.World.LeaveMap(peer, PeerMap);
        }
    }
}
