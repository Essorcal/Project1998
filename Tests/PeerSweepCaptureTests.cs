using System.Buffers.Binary;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// What <c>Session.SyncPeers</c>' capture pass reads. Since the id rides in <see cref="PeerTile"/>
/// (<c>PeerTileIdTests</c>), the pass copies three values out of the snapshot array and dereferences no
/// peer at all — the viewport profile's largest single line, 10.2 us per viewer per beat in Debug.
///
/// <para><b>Why this needs tests by the "would this fail loudly?" rule.</b> Both failure modes are silent.
/// A pass that goes back to reading through <c>peer.Session</c> is invisible in every observable the server
/// has — the ids are identical in production, so only the cost changes, and 399 foreign cache lines a beat
/// per viewer reach a log no earlier than a hold's slow-beat line. A pass that reaches further, for a value
/// behind the peer's state monitor, is worse and just as quiet: the tick thread starts waiting on 400
/// session monitors a beat and nothing anywhere says so.</para>
///
/// <para>The two facts are: the capture pass enters no peer's monitor; and it decides with the id the
/// snapshot carried rather than one re-read off the session.</para>
///
/// <para>Geometry, as in <c>PeerSweepBatchTests</c>: a content-free map is 12x12, narrower and shorter than
/// the 17x15 viewport, so <c>EdgeAwareAnchor</c> centres it — from (5,10) the strict rect is x in [-2,15)
/// and the drawn rect (pad 1) is x in [-3,16). Peer tiles come from <see cref="PeerTile"/>, which the caller
/// supplies, so they are not limited to tiles the 12x12 map actually has.</para>
///
/// <para>Hygiene, as in <c>ViewportRectTests</c>: the fixture's <c>World</c> is shared and has no teardown,
/// so every seated player is removed in a <c>finally</c>.</para>
/// </summary>
[Collection("world")]
public class PeerSweepCaptureTests
{
    private readonly SessionFixture _fx;

    public PeerSweepCaptureTests(SessionFixture fx) => _fx = fx;

    // Content-free maps (no registry row, no terrain, no warps, no spawns), one per fact so nothing is shared.
    private const ushort MonitorMap = 60132, DecideMap = 60133;

    private const ushort ViewerX = 5, ViewerY = 10;
    private const ushort InStrict = 5;      // x in [-2,15)
    private const ushort PastDrawn = 16;    // outside both rects

    /// <summary>A hold long enough that a blocked capture pass is unmistakable and short enough that a red
    /// fact does not stall the suite.</summary>
    private const int HoldMs = 3_000;

    /// <summary>What a steady-state sweep is allowed to take while another thread owns a peer's monitor.
    /// An order of magnitude under <see cref="HoldMs"/>, so the fact cannot pass by being slow.</summary>
    private const int SweepBudgetMs = 250;

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

    /// <summary>(a) A steady-state sweep reads no field of any peer's <c>Session</c>: with a background
    /// thread holding a peer's state monitor for three seconds, <c>SyncPeers</c> returns on the test thread
    /// inside a quarter of a second.
    ///
    /// <para><b>This fact pins the contract, not the change.</b> It is green on the shape this replaces as
    /// well, and honestly so: <c>other.PlayerId</c> was a plain field read and a plain field read takes no
    /// monitor, so the base passes it too. What it forbids is the next edit — anything in the capture pass
    /// that reaches into a peer for a value it could have been handed (a name, a snapshot, an appearance,
    /// anything behind <c>EnterState</c>) puts 399 monitor acquisitions a beat per viewer on the tick
    /// thread, and this is where that shows up instead of in a hold's slow-beat log.</para>
    ///
    /// <para>The sweep runs in the steady state deliberately: a sweep with something to SEND calls
    /// <c>ShowPlayer</c> -&gt; <c>other.Snapshot()</c> after the release, which takes the subject's monitor
    /// by design (#29). The capture pass is what must not.</para>
    ///
    /// <para>Falsification: read <c>peer.Session.PlayerId</c> in the capture pass again and this fact is
    /// still green — it cannot see a field read. Put the read behind the monitor
    /// (<c>using (peer.Session.EnterState()) …</c>) and it goes red at the full hold. Run, confirm red,
    /// restore. Recorded in <c>briefs/reports/peertile-id-opus.md</c>.</para>
    ///
    /// <para>The holder is released in a <c>finally</c>, so a red here never leaves a monitor owned by a
    /// dead test's thread.</para></summary>
    [Fact]
    public void TheCapturePassEntersNoPeersMonitor()
    {
        var (viewer, outbound, _) = _fx.PlayerWith("TileIdLockViewer", _ => { }, MonitorMap, ViewerX, ViewerY);
        var (held, _, _) = _fx.PlayerWith("TileIdLockPeer", _ => { }, MonitorMap, InStrict, ViewerY);
        var peers = new[] { new PeerTile(held, held.PlayerId, InStrict, ViewerY) };

        using var holding = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            using var _ = held.EnterState();
            holding.Set();
            release.Wait(HoldMs * 2);
        }) { IsBackground = true, Name = "PeerTileIdHolder" };
        Thread? swept = null;

        try
        {
            // Settle first: entering the map already drew the peer, and one sweep with nothing to send is the
            // steady state the 400-player beat spends its time in.
            viewer.SyncPeers(peers);
            outbound.Clear();

            holder.Start();
            Assert.True(holding.Wait(HoldMs), "the background thread never took the peer's monitor");

            Exception? thrown = null;
            var sweep = new Thread(() =>
            {
                try { viewer.SyncPeers(peers); }
                catch (Exception e) { thrown = e; }
            }) { IsBackground = true, Name = "PeerTileIdSweep" };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            sweep.Start();
            bool done = sweep.Join(SweepBudgetMs);
            sw.Stop();

            Assert.True(done,
                $"SyncPeers did not return in {SweepBudgetMs} ms (waited {sw.ElapsedMilliseconds} ms) with "
              + "another thread holding a peer's session monitor — the capture pass is reaching into the peer");
            Assert.Null(thrown);
            swept = sweep;

            Assert.True(Wire(outbound).Count == 0,
                $"the settled sweep must send nothing; the wire carried {Render(Wire(outbound))}");
        }
        finally
        {
            release.Set();
            holder.Join(HoldMs * 2);
            swept?.Join(HoldMs * 2);   // a red leaves it blocked on the monitor; let it finish after the release
            _fx.World.LeaveMap(viewer, MonitorMap);
            _fx.World.LeaveMap(held, MonitorMap);
        }
    }

    /// <summary>(b) The sweep decides with the id the SNAPSHOT carried, not with one re-read off the session.
    /// Handed a <see cref="PeerTile"/> whose <c>Id</c> deliberately differs from its session's
    /// <c>PlayerId</c>, the sweep tracks and then despawns the peer under the tile's id.
    ///
    /// <para>The two ids can never differ in production — every construction site fills <c>Id</c> from the
    /// session under the same lock — so this is not a behaviour anyone can reach; it is the only way to ask
    /// the capture pass WHICH id it used and get a distinguishable answer. On the shape this replaces the
    /// despawn carries the session's id, so the fact is red there.</para>
    ///
    /// <para>The show frame is not part of the claim: <c>ShowPlayer</c> builds its 0x33 from
    /// <c>Snapshot().Id</c>, off the subject, which is why a wrong id in the tile is silent on screen and
    /// only the despawn can see it.</para>
    ///
    /// <para>Falsification: restore <c>other.PlayerId</c> in the capture pass. Run, confirm red, restore.
    /// Recorded in <c>briefs/reports/peertile-id-opus.md</c>.</para></summary>
    [Fact]
    public void TheCapturePassDecidesWithTheSnapshotsId()
    {
        const uint Marked = 0xDEAD_0001;   // no allocated player id can reach this; _nextPlayerId starts at 1

        var (viewer, outbound, _) = _fx.PlayerWith("TileIdDecideViewer", _ => { }, DecideMap, ViewerX, ViewerY);
        var (peer, _, _) = _fx.PlayerWith("TileIdDecidePeer", _ => { }, DecideMap, 1, 1);
        try
        {
            Assert.NotEqual(Marked, peer.PlayerId);
            viewer.DespawnEntity(peer.PlayerId);            // forget the draw the map entry made
            outbound.Clear();

            viewer.SyncPeers(new[] { new PeerTile(peer, Marked, InStrict, ViewerY) });     // untracked, in view -> show
            viewer.SyncPeers(new[] { new PeerTile(peer, Marked, PastDrawn, ViewerY) });    // tracked, gone -> despawn

            var seq = Wire(outbound);
            var expected = new List<(byte, uint)> { (0x33, peer.PlayerId), (0x0E, Marked) };
            Assert.True(seq.SequenceEqual(expected),
                "the sweep must track and despawn under the id the snapshot carried, and draw off the "
              + $"subject's own snapshot; expected {Render(expected)} but the wire carried {Render(seq)}");
        }
        finally
        {
            viewer.DespawnEntity(Marked);                   // do not leave a phantom id in the viewer's sets
            _fx.World.LeaveMap(viewer, DecideMap);
            _fx.World.LeaveMap(peer, DecideMap);
        }
    }
}
