using System.Buffers.Binary;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Who receives a player's <c>0x0C</c> move and <c>0x11</c> turn, now that both are gated on the recipient's
/// drawn set exactly as <c>MoveMob</c> and <c>SideMob</c> always were.
///
/// <para><b>Why this needs tests by the "would this fail loudly?" rule.</b> Nothing about a dropped
/// <c>0x0C</c> throws, logs or fails a build. If the gate is one state too strict — drawn-inside only, with
/// the overdraw band treated as undrawn — a neighbour who walks along the edge of your screen simply stops
/// animating, and the next thing that repairs it is a sweep that has no reason to run. That is a feel change,
/// it is silent, and it is the single hazard this change carries. So the facts below pin the frames a
/// recipient gets in each of the four states a peer can be in for that recipient, and fact (g) pins the whole
/// SEQUENCE against the ungated shape rather than against a description of it.</para>
///
/// <para><b>Geometry.</b> Everyone stands on a content-free map the character hook widens to 100x100, so
/// <c>EdgeAwareAnchor</c> takes the plain follow branch and the anchor is (8,7). For a viewer at
/// (<see cref="ViewerX"/>, <see cref="ViewerY"/>) = (30,20) the STRICT 17x15 rect is x in [22,39) and the
/// DRAWN 19x17 rect (pad 1) is x in [21,40). The walkers all stand on row <see cref="WalkRow"/> = 21, which
/// is inside both rects vertically and one row clear of the viewer, so a walk along it is never refused for
/// occupancy. So on row 21: x=25 is inside the strict rect, x=39 is in the overdraw band, and x=40 and
/// beyond are past the drawn rect.</para>
///
/// <para><b>The steps are real.</b> Every move below is the client's own <c>0x06</c> walk packet — dir, step
/// counter, the believed tile as two big-endian u16 — ciphered, framed and handed to
/// <c>Session.Receive</c>, and every turn is a real <c>0x11</c>. No production seam was added to drive them.
/// The recipient's sweeps are driven explicitly, because in production they are driven by the recipient's own
/// step or by the world tick, neither of which the walker's broadcast waits for: the drawn state the gate
/// reads is whatever the last sweep left.</para>
///
/// <para><b>Falsifications</b> (run, recorded red, reverted by hand) are named on each fact and quoted in
/// <c>briefs/reports/gate-move-broadcast-opus.md</c>.</para>
///
/// <para>Hygiene, as in <c>PeerSweepStalenessTests</c>: the fixture's <c>World</c> is shared and has no
/// teardown, so every seated player is removed in a <c>finally</c>.</para>
/// </summary>
[Collection("world")]
public class PeerMoveGateTests
{
    private readonly SessionFixture _fx;

    public PeerMoveGateTests(SessionFixture fx) => _fx = fx;

    // Content-free maps (no registry row, no terrain, no warps, no spawns), one per fact so nothing is shared.
    private const ushort InsideMap = 60110, BandMap = 60111, OutsideMap = 60112, LeavingMap = 60113;
    private const ushort TurnMap = 60114, SwitchMap = 60115, SeqBaseMap = 60116, SeqHeadMap = 60117;
    private const ushort MorphMap = 60118, RedrawSetMap = 60119;
    private const ushort ResyncBackMap = 60120, ResyncAwayMap = 60121;

    private const ushort ViewerX = 30, ViewerY = 20;
    private const ushort WalkRow = 21;

    private const ushort InStrict = 25;    // x in [22,39)
    private const ushort InBand = 39;      // outside the strict rect, inside the drawn 19x17
    private const ushort PastDrawn = 41;   // outside both, with room to step west twice and still be outside

    private const byte East = 1, West = 3;

    private static void Wide(Character c) { c.MapXs = 100; c.MapYs = 100; }

    // ---- driving the real client packets ------------------------------------------------------------------

    /// <summary>The client's own <c>0x06</c>: dir, step counter, then the tile the client believes it is on
    /// as two big-endian u16 — which is the tile the walker is really on here, so the handler's
    /// client-authoritative resync is a no-op and the step is an ordinary one.</summary>
    private static void Walk(Session walker, byte dir)
    {
        ushort x = walker.PlayerX, y = walker.PlayerY;
        byte[] body = { dir, 0, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y };
        walker.Receive(SessionFixture.Frame(ClientOp.Walk, body));
    }

    /// <summary>The real <c>0x11</c> turn: one byte of facing, no movement.</summary>
    private static void Turn(Session walker, byte side) =>
        walker.Receive(SessionFixture.Frame(ClientOp.Turn, new[] { side }));

    // ---- reading the wire ---------------------------------------------------------------------------------

    private static byte[] Body(byte[] frame) => TkCrypt.Crypt(frame[5..], frame[4], TkCrypt.LoginKey);

    /// <summary>Every <c>0x0C</c> the recorder holds for <paramref name="id"/>, as the (x, y, dir) it carries:
    /// entityId(u32BE) X(u16BE) Y(u16BE) dir(u8).</summary>
    private static List<(ushort X, ushort Y, byte Dir)> MovesOf(RecordingOutbound outbound, uint id)
    {
        var moves = new List<(ushort, ushort, byte)>();
        foreach (var frame in outbound.Frames)
        {
            if (frame[3] != 0x0C) continue;
            byte[] body = Body(frame);
            if (BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0)) != id) continue;
            moves.Add((BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(4)),
                       BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(6)),
                       body[8]));
        }
        return moves;
    }

    /// <summary>Every <c>0x11</c> the recorder holds for <paramref name="id"/>, as the side byte it carries:
    /// entityId(u32BE) side(u8) 00.</summary>
    private static List<byte> TurnsOf(RecordingOutbound outbound, uint id)
    {
        var sides = new List<byte>();
        foreach (var frame in outbound.Frames)
        {
            if (frame[3] != 0x11) continue;
            byte[] body = Body(frame);
            if (BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0)) == id) sides.Add(body[4]);
        }
        return sides;
    }

    /// <summary>Every <c>0x33</c> look the recorder holds for <paramref name="id"/>, as the tile it draws it
    /// at: x(u16BE) y(u16BE) dir(u8) id(u32BE).</summary>
    private static List<(ushort X, ushort Y)> LooksOf(RecordingOutbound outbound, uint id)
    {
        var looks = new List<(ushort, ushort)>();
        foreach (var frame in outbound.Frames)
        {
            if (frame[3] != 0x33) continue;
            byte[] body = Body(frame);
            if (BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(5)) == id)
                looks.Add((BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(0)),
                           BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(2))));
        }
        return looks;
    }

    /// <summary>The whole frame stream as (opcode, entity id), in the order it was handed over — the shape
    /// fact (g) compares. A <c>0x0E</c> despawn carries a count byte and then its ids; everything that is not
    /// one of the four entity opcodes is reported with id 0 so an unexpected frame shows up in the failure
    /// message rather than being filtered out of it.</summary>
    private static List<(byte Op, uint Id)> Wire(RecordingOutbound outbound)
    {
        var seq = new List<(byte, uint)>();
        foreach (var frame in outbound.Frames)
        {
            byte op = frame[3];
            byte[] body = Body(frame);
            if (op == 0x0C || op == 0x11) seq.Add((op, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0))));
            else if (op == 0x33) seq.Add((op, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(5))));
            else if (op == 0x0E)
                for (int i = 0; i < body[0]; i++)
                    seq.Add((op, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1 + i * 4))));
            else seq.Add((op, 0u));
        }
        return seq;
    }

    private static string Render(IEnumerable<(byte Op, uint Id)> seq)
    {
        string s = string.Join(", ", seq.Select(f => $"0x{f.Op:X2}#{f.Id}"));
        return s.Length == 0 ? "(nothing)" : s;
    }

    // =======================================================================================================

    /// <summary>(a) A peer INSIDE the viewer's strict rect steps: the viewer receives the <c>0x0C</c> with the
    /// SOURCE tile and the direction, exactly as on the base.
    ///
    /// <para><b>Falsification:</b> gate on <c>DrawnInside</c> only —
    /// <c>if (!_drawnPeers.TryGetValue(id, out byte st) || st != DrawnInside) return;</c> — which leaves this
    /// fact green and takes (b) red, which is the point of having both.</para></summary>
    [Fact]
    public void APeerInsideTheStrictRectStillDeliversItsMove()
    {
        var (viewer, outbound, character) = _fx.PlayerWith("GateInsideViewer", Wide, InsideMap, ViewerX, ViewerY);
        var (walker, _, _) = _fx.PlayerWith("GateInsideWalker", Wide, InsideMap, InStrict, WalkRow);
        try
        {
            Assert.Equal((100, 100), (character.MapXs, character.MapYs));   // the geometry this is read off

            viewer.SyncPeers(new[] { new PeerTile(walker, InStrict, WalkRow) });   // settle: drawn, inside
            outbound.Clear();

            Walk(walker, East);

            Assert.Equal(new[] { (InStrict, WalkRow, East) }, MovesOf(outbound, walker.PlayerId));
            Assert.Equal((ushort)(InStrict + 1), walker.PlayerX);              // the step really happened
        }
        finally
        {
            foreach (var s in new[] { viewer, walker }) _fx.World.LeaveMap(s, InsideMap);
        }
    }

    /// <summary>(b) A peer in the viewer's overdraw BAND — drawn, but suspect — steps: the viewer still
    /// receives the <c>0x0C</c>. <c>_drawnPeers</c> holds both <c>DrawnInside</c> and <c>DrawnBand</c>
    /// entries, and both mean the client has the entity, which is why the gate is a membership test and not a
    /// state test.
    ///
    /// <para><b>Falsification:</b> the drawn-inside-only gate above. Recorded red in the report — the viewer
    /// receives nothing where it must receive one move.</para></summary>
    [Fact]
    public void APeerInTheOverdrawBandStillDeliversItsMove()
    {
        var (viewer, outbound, _) = _fx.PlayerWith("GateBandViewer", Wide, BandMap, ViewerX, ViewerY);
        var (walker, _, _) = _fx.PlayerWith("GateBandWalker", Wide, BandMap, (ushort)(InBand - 1), WalkRow);
        try
        {
            // Drawn inside the strict rect, then one step east into the band, then swept there: the sweep
            // writes DrawnBand and sends nothing, which is the state this fact is about.
            viewer.SyncPeers(new[] { new PeerTile(walker, (ushort)(InBand - 1), WalkRow) });
            Walk(walker, East);
            Assert.Equal(InBand, walker.PlayerX);
            viewer.SyncPeers(new[] { new PeerTile(walker, InBand, WalkRow) });
            outbound.Clear();

            Walk(walker, East);                                                // 39 -> 40, source tile 39

            Assert.Equal(new[] { (InBand, WalkRow, East) }, MovesOf(outbound, walker.PlayerId));
        }
        finally
        {
            foreach (var s in new[] { viewer, walker }) _fx.World.LeaveMap(s, BandMap);
        }
    }

    /// <summary>(c) A peer OUTSIDE the viewer's drawn rect steps: the viewer receives NO <c>0x0C</c> — and
    /// when that peer steps into the strict rect, the viewer's next sweep draws it with a <c>0x33</c> at the
    /// new tile, exactly as on the base. That second half is what makes the first half harmless: the client
    /// never had the entity, so there was nothing on its screen to animate, and the walker arrives drawn at
    /// the tile it is really on.
    ///
    /// <para><b>Falsification:</b> remove the gate from <c>MoveEntity</c> (back to
    /// <c>=&gt; SendMove(id, x, y, dir)</c>). Recorded red in the report: three moves where there must be
    /// none.</para></summary>
    [Fact]
    public void APeerOutsideTheDrawnRectDeliversNoMoveAndIsDrawnWhenItArrives()
    {
        var (viewer, outbound, _) = _fx.PlayerWith("GateOutsideViewer", Wide, OutsideMap, ViewerX, ViewerY);
        var (walker, _, _) = _fx.PlayerWith("GateOutsideWalker", Wide, OutsideMap, PastDrawn, WalkRow);
        try
        {
            viewer.DespawnEntity(walker.PlayerId);                             // forget the map entry's draw
            viewer.SyncPeers(new[] { new PeerTile(walker, PastDrawn, WalkRow) });   // undrawn, outside: nothing
            outbound.Clear();

            // 41 -> 40 -> 39 -> 38. Each step's decision is taken on the drawn state the previous sweep left,
            // and that state is "absent" until the walker reaches the strict rect at x=38.
            Walk(walker, West); viewer.SyncPeers(new[] { new PeerTile(walker, walker.PlayerX, WalkRow) });
            Walk(walker, West); viewer.SyncPeers(new[] { new PeerTile(walker, walker.PlayerX, WalkRow) });
            Assert.Empty(MovesOf(outbound, walker.PlayerId));

            Walk(walker, West);                                                // source 39, still undrawn
            Assert.Equal((ushort)(InBand - 1), walker.PlayerX);                // x = 38, inside the strict rect
            Assert.Empty(MovesOf(outbound, walker.PlayerId));

            viewer.SyncPeers(new[] { new PeerTile(walker, walker.PlayerX, WalkRow) });
            Assert.Equal(new[] { ((ushort)(InBand - 1), WalkRow) }, LooksOf(outbound, walker.PlayerId));
        }
        finally
        {
            foreach (var s in new[] { viewer, walker }) _fx.World.LeaveMap(s, OutsideMap);
        }
    }

    /// <summary>(d) A DRAWN peer steps OUT of the viewer's drawn rect: the viewer receives the <c>0x0C</c> —
    /// the peer was drawn when the decision was taken — and then the sweep's despawn, in that order, as on the
    /// base. This is the case the gate must not eat: the frame that carries the peer off the edge of the
    /// screen.</summary>
    [Fact]
    public void ADrawnPeerLeavingTheDrawnRectDeliversItsLastMoveThenTheDespawn()
    {
        var (viewer, outbound, _) = _fx.PlayerWith("GateLeaveViewer", Wide, LeavingMap, ViewerX, ViewerY);
        var (walker, _, _) = _fx.PlayerWith("GateLeaveWalker", Wide, LeavingMap, (ushort)(InBand - 1), WalkRow);
        try
        {
            // Drawn inside the strict rect, then swept in the band: drawn, suspect — the state a peer is in
            // on the beat before it leaves the screen.
            viewer.SyncPeers(new[] { new PeerTile(walker, (ushort)(InBand - 1), WalkRow) });
            Walk(walker, East);
            Assert.Equal(InBand, walker.PlayerX);
            viewer.SyncPeers(new[] { new PeerTile(walker, InBand, WalkRow) });
            outbound.Clear();

            Walk(walker, East);                                                 // 39 -> 40, source 39
            viewer.SyncPeers(new[] { new PeerTile(walker, walker.PlayerX, WalkRow) });   // 40: past the drawn rect

            Assert.Equal(new[] { (InBand, WalkRow, East) }, MovesOf(outbound, walker.PlayerId));
            var seq = Wire(outbound).Where(f => f.Id == walker.PlayerId).ToList();
            var expected = new List<(byte, uint)> { (0x0C, walker.PlayerId), (0x0E, walker.PlayerId) };
            Assert.True(seq.SequenceEqual(expected),
                $"the last move must reach the client before the despawn; expected {Render(expected)} but the "
              + $"wire carried {Render(seq)}");
        }
        finally
        {
            foreach (var s in new[] { viewer, walker }) _fx.World.LeaveMap(s, LeavingMap);
        }
    }

    /// <summary>(e) The same four cases for a TURN (<c>0x11</c>), which goes through <c>SideEntity</c> and is
    /// gated the same way. A turn does not move the turner, so each case is set up by placing it and settling
    /// the viewer's drawn state, then turning.
    ///
    /// <para><b>Falsification:</b> remove the gate from <c>SideEntity</c>. Recorded red in the report on the
    /// outside case, which is the only one that changes.</para></summary>
    [Fact]
    public void TheSameFourCasesHoldForATurn()
    {
        var (viewer, outbound, _) = _fx.PlayerWith("GateTurnViewer", Wide, TurnMap, ViewerX, ViewerY);
        var (turner, _, _) = _fx.PlayerWith("GateTurnTurner", Wide, TurnMap, InStrict, WalkRow);
        try
        {
            // inside the strict rect, drawn
            viewer.SyncPeers(new[] { new PeerTile(turner, InStrict, WalkRow) });
            outbound.Clear();
            Turn(turner, East);
            Assert.Equal(new[] { East }, TurnsOf(outbound, turner.PlayerId));

            // the overdraw band, still drawn
            turner.WithState(() => _fx.World.SetPlayerPosition(turner, InBand, WalkRow));
            viewer.SyncPeers(new[] { new PeerTile(turner, InBand, WalkRow) });
            outbound.Clear();
            Turn(turner, West);
            Assert.Equal(new[] { West }, TurnsOf(outbound, turner.PlayerId));

            // stepped out of the drawn rect and swept away: undrawn
            turner.WithState(() => _fx.World.SetPlayerPosition(turner, (ushort)(InBand + 1), WalkRow));
            viewer.SyncPeers(new[] { new PeerTile(turner, (ushort)(InBand + 1), WalkRow) });
            outbound.Clear();
            Turn(turner, East);
            Assert.Empty(TurnsOf(outbound, turner.PlayerId));

            // back inside the strict rect and drawn again: the turn is delivered again
            turner.WithState(() => _fx.World.SetPlayerPosition(turner, InStrict, WalkRow));
            viewer.SyncPeers(new[] { new PeerTile(turner, InStrict, WalkRow) });
            outbound.Clear();
            Turn(turner, West);
            Assert.Equal(new[] { West }, TurnsOf(outbound, turner.PlayerId));
        }
        finally
        {
            foreach (var s in new[] { viewer, turner }) _fx.World.LeaveMap(s, TurnMap);
        }
    }

    /// <summary>(h) The redraw pair. <c>RefreshAppearance</c> (<c>Server/Session.Entity.cs:408-409</c>),
    /// <c>CastMorph</c> (<c>Server/Session.Spells.cs:2556-2557</c>) and <c>RevertMorph</c> (<c>:3014-3015</c>)
    /// each broadcast <c>DespawnEntity</c> and then <c>ShowPlayer</c> to every session on the map, which is
    /// the one peer draw that does not go through <c>DecidePeerUnderViewLock</c>. The despawn takes the id out
    /// of the recipient's drawn store and the draw puts the entity back on the recipient's screen, so without
    /// the re-add in <c>ShowPlayer</c> the store is wrong for up to one beat and the gate would drop every
    /// move in that window: an in-view peer that equips something or morphs would freeze and then snap. The
    /// pair is driven here by calling the two public methods in the order those three sites call them.
    ///
    /// <para>This is the finding in <c>reviews/PR263-by-opus.md</c> (F1, MEDIUM), and it is the fact that
    /// makes this change safe rather than the one that makes it fast.</para>
    ///
    /// <para><b>Falsification:</b> delete the <c>_drawnPeers.TryAdd(s.Id, DrawnBand)</c> line from
    /// <c>ShowPlayer</c>. Recorded red in the report: no move and no turn where both must arrive.</para>
    ///
    /// <para>The second half of the fact is that the <c>0x33</c> the next sweep sends is unchanged — the
    /// re-add uses the band state precisely so that sweep still re-asserts the draw, exactly as it does today
    /// from a store that has forgotten the id.</para></summary>
    [Fact]
    public void AnInViewPeerRedrawnByTheMorphPairStillDeliversItsMove()
    {
        var (viewer, outbound, _) = _fx.PlayerWith("GateMorphViewer", Wide, MorphMap, ViewerX, ViewerY);
        var (walker, _, _) = _fx.PlayerWith("GateMorphWalker", Wide, MorphMap, InStrict, WalkRow);
        try
        {
            viewer.SyncPeers(new[] { new PeerTile(walker, InStrict, WalkRow) });   // settle: drawn, inside

            // The pair, as the three production sites broadcast it.
            viewer.DespawnEntity(walker.PlayerId);
            viewer.ShowPlayer(walker);
            outbound.Clear();

            Walk(walker, East);
            Turn(walker, West);

            Assert.Equal(new[] { (InStrict, WalkRow, East) }, MovesOf(outbound, walker.PlayerId));
            Assert.Equal(new[] { West }, TurnsOf(outbound, walker.PlayerId));

            // ...and the next sweep still re-asserts the draw, which is the frame the base sends here too.
            outbound.Clear();
            viewer.SyncPeers(new[] { new PeerTile(walker, walker.PlayerX, WalkRow) });
            Assert.Equal(new[] { (walker.PlayerX, WalkRow) }, LooksOf(outbound, walker.PlayerId));

            // A peer that has not moved is not re-asserted a second time: the sweep's own DrawnInside write
            // stands, so this is not a 0x33 per in-view peer per beat.
            outbound.Clear();
            viewer.SyncPeers(new[] { new PeerTile(walker, walker.PlayerX, WalkRow) });
            Assert.Empty(LooksOf(outbound, walker.PlayerId));
        }
        finally
        {
            foreach (var s in new[] { viewer, walker }) _fx.World.LeaveMap(s, MorphMap);
        }
    }

    /// <summary>(i) After that same pair, the drawn store agrees with what the recipient's CLIENT holds, in
    /// all three cases. The client accepts a <c>0x33</c>/<c>0x07</c> only inside the strict rect (<c>ShowPad</c>
    /// is 0 for exactly that reason), so the store must say drawn there and undrawn in the overdraw band and
    /// outside the drawn rect — where the draw the pair sent is thrown away by the client's own gate and the
    /// despawn that preceded it took the entity off the screen.
    ///
    /// <para>The store is asked the way <c>DrawnStateEquivalenceTests</c> asks it of the mob store: a move
    /// goes out only for an entity the server records as drawn, so a probe step answers "is it drawn now"
    /// without a test-only accessor.</para></summary>
    [Fact]
    public void TheStoreAfterTheRedrawPairAgreesWithWhatTheClientHolds()
    {
        var (viewer, outbound, _) = _fx.PlayerWith("GateStoreViewer", Wide, RedrawSetMap, ViewerX, ViewerY);
        var (peer, _, _) = _fx.PlayerWith("GateStorePeer", Wide, RedrawSetMap, InStrict, WalkRow);
        try
        {
            foreach (var (tile, clientHolds, what) in new (ushort Tile, bool Holds, string What)[]
                     {
                         (InStrict, true, "inside the strict rect: the client takes the draw"),
                         (InBand, false, "the overdraw band: the client's gate throws the draw away"),
                         ((ushort)(InBand + 1), false, "past the drawn rect: the same"),
                     })
            {
                peer.WithState(() => _fx.World.SetPlayerPosition(peer, tile, WalkRow));
                viewer.DespawnEntity(peer.PlayerId);
                viewer.ShowPlayer(peer);

                outbound.Clear();
                viewer.MoveEntity(peer.PlayerId, tile, WalkRow, East);        // the probe: drawn or not?
                bool storeSaysDrawn = MovesOf(outbound, peer.PlayerId).Count > 0;

                Assert.True(storeSaysDrawn == clientHolds,
                    $"{what}: the client holds={clientHolds} but the store says drawn={storeSaysDrawn}");
            }
        }
        finally
        {
            foreach (var s in new[] { viewer, peer }) _fx.World.LeaveMap(s, RedrawSetMap);
        }
    }

    /// <summary>(j) <c>ResyncPeers</c> — every death (<c>Session.Entity.cs</c>'s <c>Die</c>) and every in-place
    /// revive (<c>ReviveInPlace</c>) — must not make a peer the client HOLDS look undrawn, because the gate
    /// would then drop its moves.
    ///
    /// <para>PR #264's review found this as F1 (HIGH), <c>reviews/PR264-by-fable.md</c>. <c>ResyncPeers</c>
    /// sends the client nothing to forget with — no 0x15, no 0x0E — so after it the client still holds every
    /// peer it held; clearing the drawn store said otherwise, and the sweep it runs re-draws only the STRICT
    /// rect, so a peer loitering in the one-tile overdraw band came back held-by-the-client and
    /// absent-from-the-store. Its next step was then dropped and the one after that SNAPPED it into place.
    /// The store is downgraded to the band state instead, which keeps membership (so no move is lost) and
    /// keeps the sweep's frames identical.</para>
    ///
    /// <para>Both halves are the reviewer's own probe, and both assert the BASE's wire — this is a repair, so
    /// the head must now match what the ungated server put on the wire, not merely something reasonable.</para>
    ///
    /// <para><b>Falsification:</b> restore <c>_drawnPeers.Clear()</c> in <c>ResyncPeers</c>. Recorded red in
    /// the report: the westward half loses its <c>0x0C</c> and carries only the snap <c>0x33</c>, and the
    /// eastward half carries nothing at all.</para></summary>
    [Fact]
    public void AResyncDoesNotStrandAPeerWhoWalksBackIntoTheStrictRect()
    {
        var (viewer, outbound, _) = _fx.PlayerWith("GateResyncViewer", Wide, ResyncBackMap, ViewerX, ViewerY);
        var (walker, _, _) = _fx.PlayerWith("GateResyncWalker", Wide, ResyncBackMap, (ushort)(InBand - 1), WalkRow);
        try
        {
            viewer.SyncPeers(new[] { new PeerTile(walker, (ushort)(InBand - 1), WalkRow) });   // drawn, inside
            Walk(walker, East);                                                                // 38 -> 39
            Assert.Equal(InBand, walker.PlayerX);
            viewer.SyncPeers(new[] { new PeerTile(walker, InBand, WalkRow) });                  // swept: the band

            outbound.Clear();
            viewer.ResyncPeers();                                                              // the death / revive
            // the resync itself sends this peer nothing: it is in the band, so the sweep decides Nothing
            Assert.DoesNotContain(Wire(outbound), f => f.Id == walker.PlayerId);

            Walk(walker, West);                                                                // 39 -> 38, source 39
            viewer.SyncPeers(new[] { new PeerTile(walker, walker.PlayerX, WalkRow) });

            var seq = Wire(outbound).Where(f => f.Id == walker.PlayerId).ToList();
            var expected = new List<(byte, uint)> { (0x0C, walker.PlayerId), (0x33, walker.PlayerId) };
            Assert.True(seq.SequenceEqual(expected),
                $"after a resync the band-held peer must still deliver its move and then be re-asserted, as on "
              + $"the base; expected {Render(expected)} but the wire carried {Render(seq)}");
            Assert.Equal(new[] { (InBand, WalkRow, West) }, MovesOf(outbound, walker.PlayerId));
            Assert.Equal(new[] { ((ushort)(InBand - 1), WalkRow) }, LooksOf(outbound, walker.PlayerId));
        }
        finally
        {
            foreach (var s in new[] { viewer, walker }) _fx.World.LeaveMap(s, ResyncBackMap);
        }
    }

    /// <summary>(j.2) The other half of the same defect: a band-held peer that walks OUT of the drawn rect
    /// after a resync must deliver its last move and then the despawn. With the store cleared it delivered
    /// neither, and the client kept a frozen sprite of that peer on the band tile — the 0x0E was leaked on
    /// the base too, so this half is a repair as well as a guard. PR #264's F1, second probe.
    ///
    /// <para><b>Falsification:</b> the same one — restore <c>_drawnPeers.Clear()</c>. Recorded red: the wire
    /// carries nothing at all where it must carry a move and a despawn.</para></summary>
    [Fact]
    public void AResyncDoesNotStrandAPeerWhoWalksOffTheDrawnRect()
    {
        var (v2, out2, _) = _fx.PlayerWith("GateResyncAwayViewer", Wide, ResyncAwayMap, ViewerX, ViewerY);
        var (w2, _, _) = _fx.PlayerWith("GateResyncAwayWalker", Wide, ResyncAwayMap, (ushort)(InBand - 1), WalkRow);
        try
        {
            v2.SyncPeers(new[] { new PeerTile(w2, (ushort)(InBand - 1), WalkRow) });
            Walk(w2, East);
            Assert.Equal(InBand, w2.PlayerX);
            v2.SyncPeers(new[] { new PeerTile(w2, InBand, WalkRow) });

            out2.Clear();
            v2.ResyncPeers();

            Walk(w2, East);                                                                     // 39 -> 40, source 39
            Assert.Equal((ushort)(InBand + 1), w2.PlayerX);                                      // past the drawn rect
            v2.SyncPeers(new[] { new PeerTile(w2, w2.PlayerX, WalkRow) });

            var seq = Wire(out2).Where(f => f.Id == w2.PlayerId).ToList();
            var expected = new List<(byte, uint)> { (0x0C, w2.PlayerId), (0x0E, w2.PlayerId) };
            Assert.True(seq.SequenceEqual(expected),
                $"a band-held peer walking off the drawn rect after a resync must deliver its last move and "
              + $"then the despawn; expected {Render(expected)} but the wire carried {Render(seq)}");
            Assert.Equal(new[] { (InBand, WalkRow, East) }, MovesOf(out2, w2.PlayerId));
        }
        finally
        {
            foreach (var s in new[] { v2, w2 }) _fx.World.LeaveMap(s, ResyncAwayMap);
        }
    }

    /// <summary>(f) With <c>P1998_GATE_PEER_MOVES</c> off, case (c) is the base's again: the undrawn peer's
    /// move and turn both reach the viewer. This is the kill switch doing the one thing it exists for.</summary>
    [Fact]
    public void TheKillSwitchOffRestoresTheUngatedMoveAndTurn()
    {
        var (viewer, outbound, _) = _fx.PlayerWith("GateSwitchViewer", Wide, SwitchMap, ViewerX, ViewerY);
        var (walker, _, _) = _fx.PlayerWith("GateSwitchWalker", Wide, SwitchMap, PastDrawn, WalkRow);
        bool restore = Session.GatePeerMovesForTest;
        try
        {
            Assert.True(restore, "the knob defaults ON, so the gated arm below is the shipped behaviour");

            viewer.DespawnEntity(walker.PlayerId);
            viewer.SyncPeers(new[] { new PeerTile(walker, PastDrawn, WalkRow) });
            outbound.Clear();

            Walk(walker, East);
            Turn(walker, West);
            Assert.Empty(MovesOf(outbound, walker.PlayerId));
            Assert.Empty(TurnsOf(outbound, walker.PlayerId));

            Session.GatePeerMovesForTest = false;
            ushort from = walker.PlayerX;
            outbound.Clear();

            Walk(walker, East);
            Turn(walker, West);
            Assert.Equal(new[] { (from, WalkRow, East) }, MovesOf(outbound, walker.PlayerId));
            Assert.Equal(new[] { West }, TurnsOf(outbound, walker.PlayerId));
        }
        finally
        {
            Session.GatePeerMovesForTest = restore;
            foreach (var s in new[] { viewer, walker }) _fx.World.LeaveMap(s, SwitchMap);
        }
    }

    /// <summary>(g) The SEQUENCE. One scripted walk of a peer across the viewer's rect and back, run twice:
    /// once with the switch OFF, which IS the base's shape line for line, and once with it on. The claim is
    /// that the second is the first with the undrawn stretch's <c>0x0C</c> frames removed and nothing else
    /// changed — so the two are compared three ways: the head's stream is a sub-sequence of the base's; the
    /// two streams are IDENTICAL once the walker's <c>0x0C</c> frames are filtered out of both, which is what
    /// pins the draws, the despawns and their order; and the head's surviving moves are exactly the
    /// hand-computed set of steps whose SOURCE tile was drawn on the viewer.
    ///
    /// <para>The walk, with the viewer at x=30 (strict x in [22,39), drawn x in [21,40)) and the walker on
    /// row 21: from x=42 west to x=20, then back east to x=42, with the viewer sweeping after every step.
    /// Going west the walker is undrawn until the sweep at x=38 draws it, so the first move that survives is
    /// the one whose source is 38; it stays drawn — through the band at x=21 — until the sweep at x=20
    /// despawns it, so the last move that survives is the one whose source is 21. Coming back east it is
    /// undrawn until the sweep at x=22 draws it, and drawn thereafter until the sweep at x=40 despawns it, so
    /// the surviving sources are 22 through 39.</para></summary>
    [Fact]
    public void TheGatedSequenceIsTheUngatedOneWithOnlyTheUndrawnMovesRemoved()
    {
        bool restore = Session.GatePeerMovesForTest;
        try
        {
            Session.GatePeerMovesForTest = false;
            var (baseSeq, baseMoves, baseId) = ScriptedWalk(SeqBaseMap, "SeqBase");
            Session.GatePeerMovesForTest = true;
            var (headSeq, headMoves, headId) = ScriptedWalk(SeqHeadMap, "SeqHead");

            // The base really is ungated: every step of the walk produced a move.
            var everyStep = Enumerable.Range(21, 22).Select(x => (ushort)x)                 // west: sources 42..21
                .Reverse().Concat(Enumerable.Range(20, 22).Select(x => (ushort)x)).ToArray();  // east: sources 20..41
            Assert.Equal(everyStep, baseMoves.Select(m => m.X).ToArray());

            // The head's surviving moves are exactly the steps taken from a tile the viewer had drawn.
            var expected = Enumerable.Range(21, 18).Select(x => (ushort)x).Reverse()        // west: 38 down to 21
                .Concat(Enumerable.Range(22, 18).Select(x => (ushort)x)).ToArray();         // east: 22 up to 39
            Assert.Equal(expected, headMoves.Select(m => m.X).ToArray());

            // Nothing else moved: with the walker's 0x0C frames filtered out, the two streams are identical —
            // same opcodes, same ids, same order. The ids differ between the two runs (two sets of sessions),
            // so both are compared with the walker's own id normalised to 0.
            var baseRest = baseSeq.Where(f => !(f.Op == 0x0C && f.Id == baseId))
                                  .Select(f => (f.Op, Id: f.Id == baseId ? 0u : f.Id)).ToList();
            var headRest = headSeq.Where(f => !(f.Op == 0x0C && f.Id == headId))
                                  .Select(f => (f.Op, Id: f.Id == headId ? 0u : f.Id)).ToList();
            Assert.True(baseRest.SequenceEqual(headRest),
                $"the gate must remove move frames and nothing else; without them the base carried "
              + $"{Render(baseRest)} and the head carried {Render(headRest)}");

            // ...and what the head DOES carry is a sub-sequence of what the base carried, in order.
            Assert.True(IsSubsequence(
                    headSeq.Select(f => (f.Op, Id: f.Id == headId ? 0u : f.Id)).ToList(),
                    baseSeq.Select(f => (f.Op, Id: f.Id == baseId ? 0u : f.Id)).ToList()),
                "the head's frame stream must be the base's with frames removed, never reordered");
        }
        finally
        {
            Session.GatePeerMovesForTest = restore;
        }
    }

    /// <summary>The scripted walk of fact (g), on its own map with its own pair of sessions: west from x=42
    /// to x=20 and back, the viewer sweeping after every step. Returns the viewer's whole frame stream, the
    /// walker's moves, and the walker's id.</summary>
    private (List<(byte Op, uint Id)> Seq, List<(ushort X, ushort Y, byte Dir)> Moves, uint Id) ScriptedWalk(
        ushort map, string who)
    {
        const ushort Start = 42, Far = 20;
        var (viewer, outbound, _) = _fx.PlayerWith($"{who}Viewer", Wide, map, ViewerX, ViewerY);
        var (walker, _, _) = _fx.PlayerWith($"{who}Walker", Wide, map, Start, WalkRow);
        try
        {
            viewer.DespawnEntity(walker.PlayerId);                             // forget the map entry's draw
            viewer.SyncPeers(new[] { new PeerTile(walker, Start, WalkRow) });
            outbound.Clear();

            for (int i = 0; i < Start - Far; i++)
            {
                Walk(walker, West);
                viewer.SyncPeers(new[] { new PeerTile(walker, walker.PlayerX, WalkRow) });
            }
            Assert.Equal(Far, walker.PlayerX);
            for (int i = 0; i < Start - Far; i++)
            {
                Walk(walker, East);
                viewer.SyncPeers(new[] { new PeerTile(walker, walker.PlayerX, WalkRow) });
            }
            Assert.Equal(Start, walker.PlayerX);

            return (Wire(outbound), MovesOf(outbound, walker.PlayerId), walker.PlayerId);
        }
        finally
        {
            foreach (var s in new[] { viewer, walker }) _fx.World.LeaveMap(s, map);
        }
    }

    /// <summary>Is <paramref name="part"/> obtainable from <paramref name="whole"/> by deleting entries, with
    /// the survivors in their original order?</summary>
    private static bool IsSubsequence(List<(byte Op, uint Id)> part, List<(byte Op, uint Id)> whole)
    {
        int i = 0;
        foreach (var f in whole)
        {
            if (i < part.Count && part[i] == f) i++;
        }
        return i == part.Count;
    }
}
