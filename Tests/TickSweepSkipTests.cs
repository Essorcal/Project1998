using System.Buffers.Binary;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The tick's sweep skip (<c>P1998_TICK_SWEEP_SKIP</c>): <c>World.ReconcileViews</c> runs a viewer's three
/// sweeps only when something they read has changed since that viewer last swept.
///
/// <para>What the skip rests on is a claim about every WRITE in the server, not about the three sweeps —
/// that every change to what a sweep of a map reads bumps <c>World.MapState.ViewGen</c> under
/// <c>World._lock</c>, and every change to what a sweep of a SESSION reads sets that session's pending
/// flag. A missed bump is not a crash and not a slow beat: it is one client left looking at a peer, a
/// monster or a floor item that is not there any more, silently, which is exactly the failure
/// <c>AGENTS.md</c> rule 3 says a test here exists for. So most of what follows is a roll-call of change
/// sources, one case per class, each driven through the production path that really performs it.</para>
///
/// <para>The strongest fact is the first, and it is built the way PR #264's sequence fact was: one scripted
/// scenario is run TWICE in one process, once with the switch OFF — which is the pre-change code path, line
/// for line — and once ON, and the viewer's two frame streams are compared opcode for opcode, id for id and
/// tile for tile. Nothing is hand-copied; the base arm is the base running.</para>
///
/// <para>Hygiene, as in <c>ViewportRectTests</c>: the fixture's <c>World</c> is shared and has no teardown,
/// so every seated player is removed in a <c>finally</c>, and the switch is restored in one too.</para>
/// </summary>
[Collection("world")]
public class TickSweepSkipTests
{
    private readonly SessionFixture _fx;

    public TickSweepSkipTests(SessionFixture fx) => _fx = fx;

    // Content-free maps (no registry row, no terrain, no warps, no spawns, no forage areas), one per fact so
    // nothing is shared and no other fact's roster can bump a generation underneath this one.
    private const ushort SeqOffMap = 60160, SeqOnMap = 60161, QuietMap = 60162, SwitchMap = 60163;
    private const ushort ChangeMap = 60164, ChangeAwayMap = 60165, HopFromMap = 60166, HopToMap = 60167;
    private const ushort MidSweepMap = 60168, ParkedShowMap = 60169;
    private const ushort CountOffMap = 60170, CountOnMap = 60171;

    // The maps are 100x100, so the viewer's rect is the plain edge-aware one: vx = 8, vy = 7, origin
    // (22,13), strict rect x in [22,39) and y in [13,28), drawn rect x in [21,40) and y in [12,29).
    private const ushort ViewerX = 30, ViewerY = 20;
    private const ushort InStrict = 25, EntityRow = 21;
    private const ushort StartFar = 45;     // outside the drawn rect, with room to walk in
    private const ushort Nowhere = 90;      // far outside every rect in this file, for a nudger that is never seen

    private static void Wide(Character c) { c.MapXs = 100; c.MapYs = 100; }

    // ---- reading the wire -----------------------------------------------------------------------------

    private static byte[] Body(byte[] frame) => TkCrypt.Crypt(frame[5..], frame[4], TkCrypt.LoginKey);

    /// <summary><see cref="Wire"/>'s stream with each entity id replaced by the ROLE that entity plays in
    /// the scenario — walker 1, visitor 2, mob 3, item 4, viewer 5 — because the two arms run on two maps
    /// with two sets of sessions and so cannot share raw ids. An id with no role maps to 0, which makes a
    /// stray entity visible in the failure message rather than papering over it.</summary>
    private static List<(byte Op, uint Id, ushort X, ushort Y)> ByRole(
        RecordingOutbound outbound, Dictionary<uint, uint> roles) =>
        Wire(outbound).Select(f => (f.Op, roles.TryGetValue(f.Id, out uint r) ? r : 0u, f.X, f.Y)).ToList();

    /// <summary>The whole frame stream as (opcode, entity id, x, y), in the order it was handed over — the
    /// shape the sequence fact compares. The tile rides along for the draw opcodes because an identical
    /// opcode sequence that drew a peer on the wrong tile would otherwise pass. Anything that is not one of
    /// the four entity opcodes is reported with id 0, so an unexpected frame shows up in the failure message
    /// instead of being filtered out of it. Same shape as <c>PeerMoveGateTests.Wire</c>.</summary>
    private static List<(byte Op, uint Id, ushort X, ushort Y)> Wire(RecordingOutbound outbound)
    {
        var seq = new List<(byte, uint, ushort, ushort)>();
        foreach (var frame in outbound.Frames)
        {
            byte op = frame[3];
            byte[] body = Body(frame);
            if (op == 0x0C)                                   // move: id(u32BE) x(u16BE) y(u16BE) dir
                seq.Add((op, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0)),
                         BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(4)),
                         BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(6))));
            else if (op == 0x33)                              // look: x(u16BE) y(u16BE) dir id(u32BE)
                seq.Add((op, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(5)),
                         BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(0)),
                         BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(2))));
            else if (op == 0x07)                              // creature list: count(u16BE), 12B per entity
            {
                int n = BinaryPrimitives.ReadUInt16BigEndian(body);
                for (int i = 0; i < n; i++)
                    seq.Add((op, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(2 + i * 12 + 4)),
                             BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(2 + i * 12)),
                             BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(2 + i * 12 + 2))));
            }
            else if (op == 0x0E)                              // despawn: count(u8) then that many u32BE ids
                for (int i = 0; i < body[0]; i++)
                    seq.Add((op, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1 + i * 4)), 0, 0));
            else seq.Add((op, 0u, 0, 0));
        }
        return seq;
    }

    private static string Render(List<(byte Op, uint Id, ushort X, ushort Y)> seq) =>
        string.Join(", ", seq.Select(f => $"0x{f.Op:X2}#{f.Id}@({f.X},{f.Y})"));

    // ---- driving the world through its own paths ------------------------------------------------------

    /// <summary>A real player position write: <c>World.SetPlayerPosition</c> under the mover's own state
    /// monitor, so it lands in <c>Session.SetPositionUnderWorldLock</c> — the one seam every world writer of
    /// a player tile goes through, and where the map's generation is bumped.</summary>
    private void Put(Session s, ushort x, ushort y) =>
        s.WithState(() => _fx.World.SetPlayerPosition(s, x, y));

    /// <summary>A real mob step: <c>World.MobMovement.StepMobTo</c> through the existing
    /// <c>StepToForTest</c> seam, under the world lock with the tick's own per-map context. That method is
    /// the world's ONLY mob commit and therefore the only place a mob's tile is written. Home follows the
    /// mob, as in <c>ViewportRectTests.Move</c>, so the leash never gives it a reason to walk back and the
    /// beats after the step are genuinely quiet ones.</summary>
    private void StepMob(ushort map, Mob mob, int nx, int ny)
    {
        bool stepped = false;
        _fx.World.UnderWorldLockForTest(() =>
        {
            var ctx = _fx.World.MobTickContextForTest(map);
            stepped = World.MobMovement.StepToForTest(ctx, mob, nx, ny, 0);
            mob.HomeX = mob.X; mob.HomeY = mob.Y;
        });
        Assert.True(stepped, $"the mob step to ({nx},{ny}) was refused — the fixture is wrong, not the code");
    }

    private Mob Parked(ushort map, ushort x, ushort y, string name)
    {
        var mob = new Mob(_fx.World.AllocateMobId(), 1, x, y, name, 100);
        _fx.World.AddMob(map, mob);
        return mob;
    }

    private static GroundItem Loot(uint id, ushort x, ushort y) =>
        new() { Id = id, ItemId = 1, X = x, Y = y, Amount = 1, Graphic = 22 };

    /// <summary>Beat the world once and return how many sweeps <paramref name="viewer"/> actually ran in it
    /// — 1 if it swept, 0 if it skipped.</summary>
    private long Beat(Session viewer)
    {
        long before = viewer.TickSweepsForTest;
        _fx.World.TickOnceForTest();
        return viewer.TickSweepsForTest - before;
    }

    /// <summary>Beat until the viewer stops sweeping, so a case starts from a settled skip rather than from
    /// whatever map entry left behind. Fails loudly rather than looping forever.</summary>
    private void Settle(Session viewer)
    {
        for (int i = 0; i < 20; i++) if (Beat(viewer) == 0) return;
        Assert.Fail("the viewer never settled into a skip — something is bumping the generation every beat");
    }

    // ---- (a) the frames are the same, measured against the switch OFF ----------------------------------

    /// <summary>(a) SEQUENCE EQUALITY. One scripted scenario — a peer walking into the viewer's rect and out
    /// again, a mob stepping across it, an item dropped and picked up, a peer entering the map and leaving
    /// it, the redraw pair every morph / revert / equip change broadcasts, and the viewer itself moving —
    /// run twice in one process on two fresh maps, once with the skip OFF (the pre-change path, line for
    /// line) and once ON. The viewer's two frame streams must be identical: same opcodes, same ids, same
    /// tiles, same order.
    ///
    /// <para>This is the no-feel-change rule as a fact. The skip may remove WORK; it may not remove, add,
    /// reorder or alter one frame, and nothing else here can tell the difference. The base arm is the switch
    /// off rather than a hand-written expectation for the reason PR #264's fact (g) gives: a hand copy of
    /// the base's behaviour is a copy of what its author believed, and what is wanted is what the base
    /// DOES.</para>
    ///
    /// <para>Falsified by dropping any one bump; with <c>m.ViewGen++</c> removed from
    /// <c>MobMovement.StepMobTo</c> the ON arm never draws the mob at all and this is red on a missing
    /// <c>0x07</c>. The full falsification table is in
    /// <c>briefs/reports/tick-sweep-skip-opus.md</c>.</para></summary>
    [Fact]
    public void TheFramesAreIdenticalWithTheSkipOnAndOff()
    {
        bool saved = Session.TickSweepSkipForTest;
        try
        {
            Session.TickSweepSkipForTest = false;
            var off = RunScenario(SeqOffMap, "SeqOff");
            Session.TickSweepSkipForTest = true;
            var on = RunScenario(SeqOnMap, "SeqOn");

            Assert.True(off.Count > 0, "the scenario produced no frames at all — it exercises nothing");
            Assert.True(off.SequenceEqual(on),
                "the skip must change no frame the viewer receives.\n" +
                $"  switch OFF ({off.Count} frames): {Render(off)}\n" +
                $"  switch ON  ({on.Count} frames): {Render(on)}");
        }
        finally { Session.TickSweepSkipForTest = saved; }
    }

    /// <summary>The scripted scenario both arms run. Ids come out of the sessions and the mob, so the two
    /// arms are compared structurally; every tile is the same on both maps by construction. Each change is
    /// followed by ONE beat, which is the beat the skip either takes or does not.</summary>
    private List<(byte Op, uint Id, ushort X, ushort Y)> RunScenario(ushort map, string tag)
    {
        ushort away = (ushort)(map + 100);
        var (viewer, outbound, _) = _fx.PlayerWith($"{tag}Viewer", Wide, map, ViewerX, ViewerY);
        var (walker, _, _) = _fx.PlayerWith($"{tag}Walker", Wide, map, StartFar, EntityRow);
        var (visitor, _, _) = _fx.PlayerWith($"{tag}Visitor", Wide, away, InStrict, EntityRow);
        var mob = Parked(map, StartFar, EntityRow, $"{tag}Mob");
        try
        {
            // Settle whatever map entry produced, then record only what the script causes. Both arms settle
            // the same way, so neither is compared against the other's entry chatter.
            for (int i = 0; i < 8; i++) _fx.World.TickOnceForTest();
            outbound.Clear();

            // 1. a quiet beat: nothing has changed, and on the ON arm this is a beat that is skipped.
            _fx.World.TickOnceForTest();

            // 2. the peer walks in, from outside the drawn rect to inside the strict one, one tile a beat.
            for (ushort x = StartFar; x > InStrict; x--) { Put(walker, (ushort)(x - 1), EntityRow); _fx.World.TickOnceForTest(); }

            // 3. a mob steps the same way, through the world's only mob commit.
            for (int x = StartFar; x > InStrict; x--) { StepMob(map, mob, x - 1, EntityRow); _fx.World.TickOnceForTest(); }

            // 4. an item is dropped in view and picked up again.
            var gi = Loot(700_000u + map, InStrict, EntityRow);
            _fx.World.DropItem(map, gi);
            _fx.World.TickOnceForTest();
            Assert.NotNull(_fx.World.PickUp(map, InStrict, EntityRow));
            _fx.World.TickOnceForTest();

            // 5. a peer joins the map and leaves it — the roster change, with the newcomer standing inside
            //    the viewer's strict rect so it is a draw and not a no-op.
            _fx.World.EnterMap(visitor, map);
            _fx.World.TickOnceForTest();
            _fx.World.LeaveMap(visitor, map);
            _fx.World.TickOnceForTest();

            // 6. the redraw pair, as RefreshAppearance / CastMorph / RevertMorph broadcast it, one recipient
            //    at a time. It changes nothing about the MAP, which is why the per-viewer flag exists.
            viewer.DespawnEntity(walker.PlayerId);
            viewer.ShowPlayer(walker);
            _fx.World.TickOnceForTest();
            _fx.World.TickOnceForTest();

            // 7. the mob is taken off the map while it is DRAWN on this client — the roster removal class,
            //    which broadcasts its own 0x0E and leaves the next sweep a shorter roster to walk.
            Assert.True(_fx.World.DespawnMob(map, mob));
            _fx.World.TickOnceForTest();

            // 8. a spot-traps marker is registered on an in-view tile. AddTrapMarker deliberately DRAWS
            //    NOTHING — "the DRAW is left to SyncGroundItems" — so the 0x07 below comes from the sweep on
            //    the next beat and from nothing else, which is the whole of why that pending flag exists.
            var marker = Loot(730_000u + map, (ushort)(InStrict + 1), EntityRow);
            Assert.True(viewer.AddTrapMarker(77u, marker));
            _fx.World.TickOnceForTest();

            // 9. ResyncPeers — every drawn peer downgraded to the band state, as death and in-place revive
            //    do it — then a beat, which is the one that has to re-assert what the downgrade left.
            viewer.ResyncPeers();
            _fx.World.TickOnceForTest();

            // 10. RedrawWorld, through the client's own Ctrl+R (0x38): SendMapInfo, SendXy, SendSelfLook,
            //     PrimeViewport and then ForgetShownMobs + the three sweeps. It is the only path that clears
            //     all four stores and both marker sets, and the only input to the viewer's RECT that is not
            //     its tile (the refresh re-anchors ViewAnchor with no position write at all).
            viewer.Receive(SessionFixture.Frame(ClientOp.Refresh, Array.Empty<byte>()));
            _fx.World.TickOnceForTest();

            // 11. the viewer itself moves, twice, carrying the walker out of its rect.
            Put(viewer, (ushort)(ViewerX + 10), ViewerY);
            _fx.World.TickOnceForTest();
            Put(viewer, (ushort)(ViewerX + 20), ViewerY);
            _fx.World.TickOnceForTest();

            // 12. and two more quiet beats, which the ON arm skips and the OFF arm sweeps for nothing.
            _fx.World.TickOnceForTest();
            _fx.World.TickOnceForTest();

            return ByRole(outbound, new Dictionary<uint, uint>
            {
                [walker.PlayerId] = 1, [visitor.PlayerId] = 2, [mob.Id] = 3, [gi.Id] = 4,
                [viewer.PlayerId] = 5, [marker.Id] = 6,
            });
        }
        finally
        {
            foreach (var s in new[] { viewer, walker, visitor }) _fx.World.LeaveMap(s, map);
            _fx.World.LeaveMap(visitor, away);
        }
    }

    // ---- (b) the skip fires ---------------------------------------------------------------------------

    /// <summary>(b) THE SKIP FIRES. On a map where nothing changes, a viewer sweeps once and then not again,
    /// however many beats pass — the point of the slice, as a number. Read off
    /// <c>Session.TickSweepsForTest</c>, the counter <c>EndTickSweep</c> increments on the beats that were
    /// not skipped.
    ///
    /// <para>The counter is incremented at the END of a sweep, not at the decision, which is what makes this
    /// a fact about work rather than about intent: a falsification that keeps the decision and calls the
    /// three sweeps anyway — <c>_ = t.Session.BeginTickSweep(...)</c> in place of the early return in
    /// <c>ReconcileViews</c> — is caught, where a decision-counter would have read zero and passed.</para>
    ///
    /// <para>Falsified by removing the early-out from <c>Session.BeginTickSweep</c>: red at 20 sweeps
    /// instead of none. Falsified again by the <c>_ =</c> form above: same red, and that is the point of
    /// counting at the far end.</para></summary>
    [Fact]
    public void AViewerOnAnUnchangedMapSweepsOnceAndThenNotAgain()
    {
        bool saved = Session.TickSweepSkipForTest;
        var (viewer, _, _) = _fx.PlayerWith("QuietViewer", Wide, QuietMap, ViewerX, ViewerY);
        var (peer, _, _) = _fx.PlayerWith("QuietPeer", Wide, QuietMap, InStrict, EntityRow);
        try
        {
            Session.TickSweepSkipForTest = true;
            Settle(viewer);                              // whatever map entry left behind is resolved first

            long before = viewer.TickSweepsForTest;
            for (int i = 0; i < 20; i++) _fx.World.TickOnceForTest();
            Assert.Equal(0, viewer.TickSweepsForTest - before);

            // And the counter is not simply stuck: one change and the very next beat sweeps.
            Put(peer, (ushort)(InStrict + 1), EntityRow);
            Assert.Equal(1, Beat(viewer));
        }
        finally
        {
            Session.TickSweepSkipForTest = saved;
            foreach (var s in new[] { viewer, peer }) _fx.World.LeaveMap(s, QuietMap);
        }
    }

    // ---- (c) every change source un-skips -------------------------------------------------------------

    /// <summary>(c) THE ROLL-CALL. One case per class of change a sweep reacts to, each driven through the
    /// production path that performs it, each asserting the viewer swept on the beat AFTER the change. A
    /// class of change with no bump is a viewer that never hears about it, so this is what the report's
    /// inventory table is checked by.
    ///
    /// <para>Table-driven rather than ten facts, because the arrangement is identical and only the change
    /// differs; the case name is in the failure message.</para>
    ///
    /// <para>Falsified one bump at a time — removing <c>m.ViewGen++</c> beside <c>m.Mobs.Add</c> reds "mob
    /// added", removing it beside <c>m.Players.Remove</c> reds "player left", removing the
    /// <c>MarkSweepPending</c> from <c>ShowPlayer</c>'s band re-add reds "appearance refreshed". The full
    /// table is in the report.</para></summary>
    [Fact]
    public void EveryChangeSourceMakesTheNextBeatSweep()
    {
        bool saved = Session.TickSweepSkipForTest;
        var (viewer, _, _) = _fx.PlayerWith("ChangeViewer", Wide, ChangeMap, ViewerX, ViewerY);
        var (peer, _, _) = _fx.PlayerWith("ChangePeer", Wide, ChangeMap, InStrict, EntityRow);
        var (joiner, _, _) = _fx.PlayerWith("ChangeJoiner", Wide, ChangeAwayMap, InStrict, EntityRow);
        try
        {
            Session.TickSweepSkipForTest = true;

            Mob? standing = null;
            GroundItem? lying = null;
            uint nextItemId = 710_000;

            var cases = new (string Name, Action Change)[]
            {
                ("player moved",         () => Put(peer, (ushort)(peer.PlayerX + 1), EntityRow)),
                ("player joined",        () => _fx.World.EnterMap(joiner, ChangeMap)),
                ("player left",          () => _fx.World.LeaveMap(joiner, ChangeMap)),
                ("mob added",            () => standing = Parked(ChangeMap, InStrict, (ushort)(EntityRow + 1), "Stander")),
                ("mob moved",            () => StepMob(ChangeMap, standing!, InStrict + 1, EntityRow + 1)),
                ("mob removed",          () => Assert.True(_fx.World.DespawnMob(ChangeMap, standing!))),
                ("item added",           () => _fx.World.DropItem(ChangeMap, lying = Loot(nextItemId++, InStrict, EntityRow))),
                ("item removed",         () => Assert.NotNull(_fx.World.PickUp(ChangeMap, lying!.X, lying.Y))),
                ("appearance refreshed", () => { viewer.DespawnEntity(peer.PlayerId); viewer.ShowPlayer(peer); }),
                ("viewer moved",         () => Put(viewer, (ushort)(viewer.PlayerX + 1), ViewerY)),
            };

            foreach (var (name, change) in cases)
            {
                Settle(viewer);
                change();
                Assert.True(Beat(viewer) == 1, $"'{name}' must make the next beat sweep, and it did not");
            }
        }
        finally
        {
            Session.TickSweepSkipForTest = saved;
            foreach (var s in new[] { viewer, peer, joiner }) _fx.World.LeaveMap(s, ChangeMap);
            _fx.World.LeaveMap(joiner, ChangeAwayMap);
        }
    }

    // ---- (d) a generation is never read across maps ---------------------------------------------------

    /// <summary>(d) A MAP CHANGE ALWAYS SWEEPS. A viewer that arrives on another map sweeps on its first
    /// beat there — and the case this really guards is the one where the two maps sit at the SAME
    /// generation, because a record that carried only the number would then skip the arrival and leave the
    /// new map's peers, mobs and floor items undrawn until something on it moved.
    ///
    /// <para>The two generations are made equal on purpose: the destination is bumped to one short of what
    /// the viewer recorded on the map it came from, and <c>EnterMap</c>'s own bump lands it exactly there.
    /// Asserted before the beat, so a fixture that drifted fails as a fixture rather than as the fact.</para>
    ///
    /// <para>Falsified by dropping the <c>_sweptMap != mapId</c> term from
    /// <c>Session.BeginTickSweep</c>: red, the arrival beat skips.</para></summary>
    [Fact]
    public void AViewerArrivingOnAnotherMapSweepsEvenAtTheSameGeneration()
    {
        bool saved = Session.TickSweepSkipForTest;
        var (viewer, _, _) = _fx.PlayerWith("HopViewer", Wide, HopFromMap, ViewerX, ViewerY);
        var (here, _, _) = _fx.PlayerWith("HopHere", Wide, HopFromMap, InStrict, EntityRow);
        var (there, _, _) = _fx.PlayerWith("HopThere", Wide, HopToMap, InStrict, EntityRow);
        try
        {
            Session.TickSweepSkipForTest = true;
            Settle(viewer);                                      // the viewer's record is now (from, g)
            long g = _fx.World.ViewGenForTest(HopFromMap);

            // Bring the destination to g-1, so its own EnterMap bump below lands it on exactly g.
            long to = _fx.World.ViewGenForTest(HopToMap);
            Assert.True(to < g, $"the fixture needs the destination behind the origin ({to} < {g})");
            while (_fx.World.ViewGenForTest(HopToMap) < g - 1) _fx.World.BumpViewGenForTest(HopToMap);

            _fx.World.LeaveMap(viewer, HopFromMap);
            _fx.World.EnterMap(viewer, HopToMap);

            Assert.Equal(g, _fx.World.ViewGenForTest(HopToMap));  // the same number the viewer has recorded
            Assert.Equal(1, Beat(viewer));
        }
        finally
        {
            Session.TickSweepSkipForTest = saved;
            _fx.World.LeaveMap(viewer, HopFromMap);
            _fx.World.LeaveMap(viewer, HopToMap);
            _fx.World.LeaveMap(here, HopFromMap);
            _fx.World.LeaveMap(there, HopToMap);
        }
    }

    // ---- (e) the kill switch --------------------------------------------------------------------------

    /// <summary>(e) THE SWITCH OFF RESTORES THE PER-BEAT SWEEP. With <c>P1998_TICK_SWEEP_SKIP</c> off a
    /// viewer on a map where nothing changes sweeps on every beat, which is the pre-change code path and the
    /// thing the switch has to be able to get back to. The same twenty beats that produce none in fact (b)
    /// produce twenty here, in the same process and on the same map, so the difference is the switch and
    /// nothing else.</summary>
    [Fact]
    public void TheKillSwitchOffRestoresTheSweepOnEveryBeat()
    {
        bool saved = Session.TickSweepSkipForTest;
        var (viewer, _, _) = _fx.PlayerWith("SwitchViewer", Wide, SwitchMap, ViewerX, ViewerY);
        var (peer, _, _) = _fx.PlayerWith("SwitchPeer", Wide, SwitchMap, InStrict, EntityRow);
        try
        {
            Session.TickSweepSkipForTest = false;
            long before = viewer.TickSweepsForTest;
            for (int i = 0; i < 20; i++) _fx.World.TickOnceForTest();
            Assert.Equal(20, viewer.TickSweepsForTest - before);

            Session.TickSweepSkipForTest = true;
            Settle(viewer);
            before = viewer.TickSweepsForTest;
            for (int i = 0; i < 20; i++) _fx.World.TickOnceForTest();
            Assert.Equal(0, viewer.TickSweepsForTest - before);
        }
        finally
        {
            Session.TickSweepSkipForTest = saved;
            foreach (var s in new[] { viewer, peer }) _fx.World.LeaveMap(s, SwitchMap);
        }
    }

    // ---- (f) and (g) the two disciplines a single-threaded beat cannot reach ---------------------------
    //
    // Both are the round-1 reviewer's probes, lifted verbatim in shape from
    // scratchpad\tick-sweep-skip-review\TickSweepSkipReviewProbes.cs (P1 and P3). They exist because the five
    // facts above drive beats on one thread, so nothing can land BETWEEN the snapshot and the end of the
    // sweeps — which is exactly the window the two disciplines in BeginTickSweep and EndTickSweep are for.
    // Perturbation A (EndTickSweep recording a live read instead of the captured generation) and
    // perturbation B (the pending flag consumed in EndTickSweep rather than in Begin, before the sweeps)
    // both leave facts (a) to (e) green; these two are what catch them.

    /// <summary>Every <c>0x33</c> look id the recorder holds, in order.</summary>
    private static List<uint> LookIds(RecordingOutbound o) =>
        Wire(o).Where(f => f.Op == 0x33).Select(f => f.Id).ToList();

    /// <summary>Every id carried by a <c>0x0E</c> despawn, in order.</summary>
    private static List<uint> DespawnIds(RecordingOutbound o) =>
        Wire(o).Where(f => f.Op == 0x0E).Select(f => f.Id).ToList();

    /// <summary>(f) A POSITION WRITTEN BY A SECOND THREAD MID-SWEEP IS SWEPT ON THE NEXT BEAT — the fact that
    /// pins <see cref="Session.EndTickSweep"/> recording the CAPTURED generation rather than a live read.
    ///
    /// <para>The arrangement is the reviewer's. A far-away nudger steps, so THIS beat sweeps; the static
    /// <c>PeerSweepProbeForTest</c> seam then lands the peer's step from a SECOND thread at a point that is
    /// after the snapshot captured (generation, roster) and before this viewer's <c>EndTickSweep</c> — the
    /// counter increments at the far end, which is how the probe knows this viewer has not finished. The
    /// sweep therefore decides on the snapshot tile (45,21) and draws nothing, and the write is a change the
    /// NEXT beat must see.</para>
    ///
    /// <para>Red under perturbation A (<c>_sweptGen = _world.ViewGenForTest(mapId)</c> in place of
    /// <c>_sweptGen = mapGen</c>): the live read picks up the mid-sweep bump, the record is already current,
    /// the next beat skips and the <c>0x33</c> never goes out. That is a peer standing in front of you that
    /// your client never draws.</para></summary>
    [Fact]
    public void APositionWrittenBySecondThreadMidSweepIsSweptOnTheNextBeat()
    {
        bool saved = Session.TickSweepSkipForTest;
        var (viewer, outbound, _) = _fx.PlayerWith("MidSweepViewer", Wide, MidSweepMap, ViewerX, ViewerY);
        var (peer, _, _) = _fx.PlayerWith("MidSweepPeer", Wide, MidSweepMap, StartFar, EntityRow);   // outside
        var (nudger, _, _) = _fx.PlayerWith("MidSweepNudger", Wide, MidSweepMap, Nowhere, Nowhere);  // never in view
        Exception? failed = null;
        try
        {
            Session.TickSweepSkipForTest = true;
            Settle(viewer);
            outbound.Clear();

            Put(nudger, (ushort)(Nowhere + 1), Nowhere);          // makes this beat sweep
            long beforeSweeps = viewer.TickSweepsForTest;
            bool fired = false;
            Session.PeerSweepProbeForTest = () =>
            {
                // The seam is static and fires for every SyncPeers of the beat, in snapshot order. Accept the
                // first firing at which THIS viewer has not yet completed its sweep, so the write lands after
                // the snapshot and before this viewer's EndTickSweep whichever session's sweep is first.
                if (fired || viewer.TickSweepsForTest != beforeSweeps) return;
                fired = true;
                var t = new Thread(() => { try { Put(peer, InStrict, EntityRow); } catch (Exception ex) { failed = ex; } });
                t.Start();
                Assert.True(t.Join(5000), "the writer thread never finished");
            };
            long sweptNow;
            try { sweptNow = Beat(viewer); }
            finally { Session.PeerSweepProbeForTest = null; }
            Assert.Null(failed);
            Assert.True(fired, "the probe never fired before this viewer's sweep ended");
            Assert.Equal(1, sweptNow);
            Assert.DoesNotContain(peer.PlayerId, LookIds(outbound));   // decided on the snapshot tile: not drawn

            outbound.Clear();
            Assert.True(Beat(viewer) == 1, "the beat after a mid-sweep position write must sweep");
            Assert.Contains(peer.PlayerId, LookIds(outbound));         // and draws the peer now standing in view
            Assert.Equal(0, Beat(viewer));                             // then the skip resumes
        }
        finally
        {
            Session.PeerSweepProbeForTest = null;
            Session.TickSweepSkipForTest = saved;
            foreach (var s in new[] { viewer, peer, nudger }) _fx.World.LeaveMap(s, MidSweepMap);
        }
    }

    /// <summary>(g) A DESPAWN LANDING AFTER THE DECIDE PASS IS RE-ASSERTED ON THE NEXT BEAT — the fact that
    /// pins <see cref="Session.BeginTickSweep"/> consuming the pending flag BEFORE the sweeps run.
    ///
    /// <para>Again the reviewer's arrangement, and it parks the tick deterministically rather than racing it.
    /// The peer steps into the strict rect under its own state monitor, held; the tick runs on another thread,
    /// decides Show for that peer, releases <c>_viewLock</c> and parks inside
    /// <c>ShowPlayer -&gt; peer.Snapshot()</c> on the monitor this thread holds — after the decide pass and
    /// before the <c>0x33</c> goes out. The morph pair's first half is broadcast into that window, from under
    /// the subject's monitor, which is the production lock order. <c>DespawnEntity</c> sets the flag while
    /// the sweep is still running, and the parked <c>ShowPlayer</c> then re-adds the id as
    /// <c>DrawnBand</c> — a band entry the NEXT beat has to re-assert.</para>
    ///
    /// <para>Red under perturbation B (the <c>Interlocked.Exchange</c> moved out of
    /// <c>BeginTickSweep</c> and into <c>EndTickSweep</c>): the flag the despawn set mid-sweep is cleared by
    /// the very sweep that never saw it, the next beat skips, and the re-assert <c>0x33</c> is never sent.</para>
    /// </summary>
    [Fact]
    public void ADespawnLandingAfterTheDecidePassIsReassertedOnTheNextBeat()
    {
        bool saved = Session.TickSweepSkipForTest;
        var (viewer, outbound, _) = _fx.PlayerWith("ParkedViewer", Wide, ParkedShowMap, ViewerX, ViewerY);
        var (peer, _, _) = _fx.PlayerWith("ParkedPeer", Wide, ParkedShowMap, StartFar, EntityRow);   // outside
        Exception? failed = null;
        Thread? tick = null;
        try
        {
            Session.TickSweepSkipForTest = true;
            Settle(viewer);
            outbound.Clear();

            peer.WithState(() =>
            {
                _fx.World.SetPlayerPosition(peer, InStrict, EntityRow);   // into the strict rect: next beat decides Show
                tick = new Thread(() => { try { _fx.World.TickOnceForTest(); } catch (Exception ex) { failed = ex; } });
                tick.Start();
                Assert.True(SpinWait.SpinUntil(
                    () => tick.ThreadState.HasFlag(System.Threading.ThreadState.WaitSleepJoin), 5000),
                    "the tick never parked on the peer's monitor");
                viewer.DespawnEntity(peer.PlayerId);                     // the morph pair's first half
            });
            Assert.True(tick!.Join(5000), "the tick never finished");
            Assert.Null(failed);
            // This beat: the 0x0E, then the parked 0x33 (ShowPlayer re-added the id as DrawnBand).
            Assert.Equal(new[] { peer.PlayerId }, DespawnIds(outbound).ToArray());
            Assert.Equal(new[] { peer.PlayerId }, LookIds(outbound).ToArray());

            outbound.Clear();
            Assert.True(Beat(viewer) == 1, "the beat after a despawn that landed inside ShowPlayer must sweep");
            Assert.Equal(new[] { peer.PlayerId }, LookIds(outbound).ToArray());
            Assert.Equal(0, Beat(viewer));
        }
        finally
        {
            Session.TickSweepSkipForTest = saved;
            foreach (var s in new[] { viewer, peer }) _fx.World.LeaveMap(s, ParkedShowMap);
        }
    }

    // ---- (h)/(i) the WORLD's two counters -------------------------------------------------------------

    /// <summary>The three numbers a counter fact reads: the world's two, and the sweeps the SESSIONS
    /// themselves recorded. The third is what makes the other two attributable — see <see cref="Span"/>.</summary>
    private (long Viewers, long Run, long SessionSweeps) Counters() =>
        (_fx.World.SweepViewers, _fx.World.SweepsRun, _fx.World.Online.All().Sum(s => s.TickSweepsForTest));

    /// <summary>Beat the world <paramref name="beats"/> times and return what that span added to each of
    /// <see cref="Counters"/>, plus what it added to <paramref name="mine"/>'s own per-session sweep counts.
    ///
    /// <para><b>Why a fact here cannot read the world's counters as absolutes, or even as a stable
    /// per-beat baseline.</b> The <c>world</c> collection shares one <see cref="World"/>, the counters are the
    /// WORLD's, and <c>TickOnceForTest</c> runs the whole beat — including the mob AI of every other class's
    /// leftover map. A foreign mob that steps bumps that map's generation and un-skips its viewers, so the
    /// foreign contribution to <c>sweepsRun</c> is genuinely not constant from beat to beat (measured: 1, 1,
    /// 0 over three beats with 301 foreign viewers). So the facts below are stated on the part of each delta
    /// that is attributable to THIS fact's own players, and the arithmetic that makes it attributable is the
    /// per-session counter <c>Session.TickSweepsForTest</c>, which the same call site increments.</para></summary>
    private (long Viewers, long Run, long Foreign, long Mine) Span(int beats, IReadOnlyCollection<Session> mine)
    {
        var mineSet = new HashSet<Session>(mine);
        long Mine() => _fx.World.Online.All().Where(mineSet.Contains).Sum(s => s.TickSweepsForTest);

        var (v0, r0, all0) = Counters();
        long mine0 = Mine();
        for (int i = 0; i < beats; i++) _fx.World.TickOnceForTest();
        var (v1, r1, all1) = Counters();

        long dMine = Mine() - mine0;
        return (v1 - v0, r1 - r0, (all1 - all0) - dMine, dMine);
    }

    /// <summary>What ONE beat adds to <c>sweepViewers</c> with none of this fact's own players seated: the
    /// roster of every other populated map, which cannot change while this class holds the collection. Three
    /// consecutive beats must agree, so a fixture that is not quiet fails HERE, naming itself, rather than as
    /// a wrong arithmetic result in the fact. Only the viewer count is baselined: the foreign
    /// <c>sweepsRun</c> is not constant, for the reason <see cref="Span"/> gives.</summary>
    private long ForeignViewersPerBeat()
    {
        for (int i = 0; i < 5; i++) _fx.World.TickOnceForTest();   // let any foreign viewer settle into its skip
        var none = Array.Empty<Session>();
        long a = Span(1, none).Viewers, b = Span(1, none).Viewers, c = Span(1, none).Viewers;
        Assert.True(a == b && b == c,
            $"the shared world's roster is not stable: three single beats considered {a}, {b} and {c} " +
            "viewers with none of this fact's players seated");
        return a;
    }

    /// <summary>(h) WITH THE SKIP OFF, EVERY VIEWER THE TICK CONSIDERED SWEPT. Over N beats with P players on
    /// one map, the world's <c>sweepsRun</c> moves exactly as far as its <c>sweepViewers</c>, and both move
    /// by N × P above the fixture's own baseline.
    ///
    /// <para>Which is the reading the pair exists to make possible: <c>1 - Δ sweepsRun / Δ sweepViewers</c> is
    /// the share of viewers a span skipped, so the switch-off arm has to read exactly 0. A counter pair that
    /// disagreed here would make every later load run's skip fraction a fiction — the silent-failure shape
    /// <c>AGENTS.md</c> rule 3 names, since nothing about a wrong ratio throws.</para>
    ///
    /// <para>Falsified by deleting <c>_sweepViewers += players.Length;</c> from <c>World.ReconcileViews</c>:
    /// red on the N × P assertion, which then reads 0 considered viewers while 12 sweeps ran. The output is
    /// in <c>briefs/reports/sweep-counter-opus.md</c>.</para></summary>
    [Fact]
    public void WithTheSkipOffTheWorldCountsOneSweepForEveryViewerItConsidered()
    {
        const int Beats = 4, Players = 3;
        bool saved = Session.TickSweepSkipForTest;
        var seated = new List<Session>();
        try
        {
            Session.TickSweepSkipForTest = false;
            long foreignViewers = ForeignViewersPerBeat();

            for (int i = 0; i < Players; i++)
                seated.Add(_fx.PlayerWith($"OffCounter{i}", Wide, CountOffMap,
                                          (ushort)(ViewerX + i), ViewerY).session);

            var span = Span(Beats, seated);

            // The headline: with the switch off nothing is skipped anywhere, so the two world counters move
            // by the same number over the same span, whatever else is seated in this fixture.
            Assert.Equal(span.Viewers, span.Run);
            // And that number is the one the tick really considered: N beats x P players on top of the
            // foreign roster's own per-beat contribution.
            Assert.Equal(Beats * Players, span.Viewers - Beats * foreignViewers);
            // The world's sweep count is the sum of the sessions' own, so the N x P is THIS fact's players:
            // every one of them swept on every beat.
            Assert.Equal(Beats * Players, span.Mine);
            Assert.Equal(span.Run, span.Mine + span.Foreign);
        }
        finally
        {
            Session.TickSweepSkipForTest = saved;
            foreach (var s in seated) _fx.World.LeaveMap(s, CountOffMap);
        }
    }

    /// <summary>(i) WITH THE SKIP ON, A STILL MAP KEEPS COUNTING VIEWERS AND STOPS COUNTING SWEEPS. Once the
    /// P players on an unchanging map have settled, every beat adds P to <c>sweepViewers</c> and nothing to
    /// <c>sweepsRun</c>; one change adds P to <c>sweepsRun</c> on the next beat and on that beat only.
    ///
    /// <para>This is the hypothesis the two hold assignments could not read. With the skip on,
    /// <c>(3) viewports</c> repeated to 22% (standing clump) and 61% (walking spread) run to run against 3-4%
    /// with it off, and the labelled explanation was that the phase's cost follows the number of DIRTY
    /// viewers, which the load script does not hold fixed
    /// (<c>briefs/reports/hold-sweep-skip-2-opus.md</c>, "Secondary"). The counter pair is what turns that
    /// into a number a hold can quote.</para>
    ///
    /// <para>Falsified by deleting <c>_world.CountSweepRunOnTickThread();</c> from
    /// <c>Session.EndTickSweep</c>: red on the change beat, which then reports 0 sweeps run where 3 viewers
    /// swept — the exact failure that would make a hold read a 100% skip rate off a busy map. The output is
    /// in <c>briefs/reports/sweep-counter-opus.md</c>.</para></summary>
    [Fact]
    public void WithTheSkipOnAStillMapCountsViewersButNoSweeps()
    {
        const int Beats = 4, Players = 3;
        bool saved = Session.TickSweepSkipForTest;
        var seated = new List<Session>();
        try
        {
            Session.TickSweepSkipForTest = true;
            long foreignViewers = ForeignViewersPerBeat();

            for (int i = 0; i < Players; i++)
                seated.Add(_fx.PlayerWith($"OnCounter{i}", Wide, CountOnMap,
                                          (ushort)(ViewerX + i), ViewerY).session);
            foreach (var s in seated) Settle(s);

            // Nothing on this map changes for these beats, so the tick keeps CONSIDERING all three viewers
            // every beat and sweeps none of them.
            var still = Span(Beats, seated);
            Assert.Equal(Beats * Players, still.Viewers - Beats * foreignViewers);
            Assert.Equal(0, still.Mine);
            Assert.Equal(still.Run, still.Mine + still.Foreign);   // the world counted what the sessions did

            // One change source — a real player position write through the world's own seam — and the next
            // beat sweeps all three viewers of the map, once each.
            var mover = seated[0];
            Put(mover, (ushort)(mover.PlayerX + 1), ViewerY);
            var changed = Span(1, seated);
            Assert.Equal(Players, changed.Viewers - foreignViewers);
            Assert.Equal(Players, changed.Mine);
            Assert.Equal(Players, changed.Run - changed.Foreign);

            // ... and only that beat: the map is still again, so the sweep count stops moving.
            var after = Span(Beats, seated);
            Assert.Equal(Beats * Players, after.Viewers - Beats * foreignViewers);
            Assert.Equal(0, after.Mine);
            Assert.Equal(after.Run, after.Mine + after.Foreign);
        }
        finally
        {
            Session.TickSweepSkipForTest = saved;
            foreach (var s in seated) _fx.World.LeaveMap(s, CountOnMap);
        }
    }
}
