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

    // The maps are 100x100, so the viewer's rect is the plain edge-aware one: vx = 8, vy = 7, origin
    // (22,13), strict rect x in [22,39) and y in [13,28), drawn rect x in [21,40) and y in [12,29).
    private const ushort ViewerX = 30, ViewerY = 20;
    private const ushort InStrict = 25, EntityRow = 21;
    private const ushort StartFar = 45;     // outside the drawn rect, with room to walk in

    private static void Wide(Character c) { c.MapXs = 100; c.MapYs = 100; }

    // ---- reading the wire -----------------------------------------------------------------------------

    private static byte[] Body(byte[] frame) => TkCrypt.Crypt(frame[5..], frame[4], TkCrypt.LoginKey);

    /// <summary>The whole frame stream as (opcode, entity id, x, y), in the order it was handed over — the
    /// shape the sequence fact compares. The tile rides along for the draw opcodes because an identical
    /// opcode sequence that drew a peer on the wrong tile would otherwise pass. Anything that is not one of
    /// the four entity opcodes is reported with id 0, so an unexpected frame shows up in the failure message
    /// instead of being filtered out of it. Same shape as <c>PeerMoveGateTests.Wire</c>.</summary>
    /// <summary>The same stream with each entity id replaced by the ROLE that entity plays in the scenario
    /// — walker 1, visitor 2, mob 3, item 4, viewer 5 — because the two arms run on two maps with two sets
    /// of sessions and so cannot share raw ids. An id with no role maps to 0, which makes a stray entity
    /// visible in the failure message rather than papering over it.</summary>
    private static List<(byte Op, uint Id, ushort X, ushort Y)> ByRole(
        RecordingOutbound outbound, Dictionary<uint, uint> roles) =>
        Wire(outbound).Select(f => (f.Op, roles.TryGetValue(f.Id, out uint r) ? r : 0u, f.X, f.Y)).ToList();

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

            // 7. the viewer itself moves, twice, carrying the walker and the mob out of its rect.
            Put(viewer, (ushort)(ViewerX + 10), ViewerY);
            _fx.World.TickOnceForTest();
            Put(viewer, (ushort)(ViewerX + 20), ViewerY);
            _fx.World.TickOnceForTest();

            // 8. and two more quiet beats, which the ON arm skips and the OFF arm sweeps for nothing.
            _fx.World.TickOnceForTest();
            _fx.World.TickOnceForTest();

            return ByRole(outbound, new Dictionary<uint, uint>
            {
                [walker.PlayerId] = 1, [visitor.PlayerId] = 2, [mob.Id] = 3, [gi.Id] = 4, [viewer.PlayerId] = 5,
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
    /// <c>Session.TickSweepsForTest</c>, the counter <c>BeginTickSweep</c> increments on the beats it lets
    /// through.
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
}
