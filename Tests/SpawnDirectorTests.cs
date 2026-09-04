using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The two spawn systems after their move out of <c>World.cs</c> into <c>World.SpawnDirector</c> (#37,
/// section 1): the POINT model revives a dead point on its own tile when its clock is due, the GROUP model
/// tops a room back up to its cap on the group's clock and never on a kill, and both run only under
/// <c>World._lock</c>. Driven through the real beat (<c>TickOnceForTest</c>) and the real map entry
/// (<c>World.EnterMap</c>), so what is pinned is the wiring from <c>World.Tick</c> into the director, not
/// the director alone — the falsification for each is to skip the director call in <c>Tick</c>.
///
/// <para>The creatures are synthetic <see cref="MobDef"/>s with ids no content row uses, stationary and
/// unaggressive, so nothing in the AI sweep moves them and no <c>MobSpawnRules</c> row gates them. The
/// POINT test runs on a content-free map (no dimensions, so <c>FreeSpawnTile</c> checks only occupancy);
/// the GROUP test needs a real box to roll in, so it runs on map 485 (Buya Legend, 8x8), which carries no
/// spawn, NPC, forage, trap or ambush content — asserted, not assumed. Every test undoes its registrations
/// in <c>finally</c> (<c>ForgetMapForTest</c>), since the fixture World is shared by the collection.</para>
/// </summary>
[Collection("world")]
public class SpawnDirectorTests
{
    private readonly SessionFixture _fx;

    public SpawnDirectorTests(SessionFixture fx) => _fx = fx;

    private const ushort PointMap = 60040, LockMap = 60041;
    private const ushort GroupMap = 485;   // Buya Legend: 8x8, a map file on disk, nothing spawns on it

    private World.SpawnDirector Spawns => _fx.World.SpawnsForTest;

    private static MobDef Creature(int id, string key) =>
        new(id, key, "Test " + key, Look: 1, Color: 0, Hp: 100, Exp: 0, Level: 1, MoveTime: 1_000_000, Stationary: true);

    private List<Mob> MobsOf(Session viewer, ushort map, MobDef def) =>
        _fx.World.View(viewer, map).mobs.Where(m => m.DefId == def.Id).ToList();

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private void SweepGroupsOnce()
    {
        // Phase (1.1) samples the groups every BatchSweepTicks beats; this many beats hits it exactly once.
        for (int i = 0; i < World.BatchSweepTicksForTest; i++) _fx.World.TickOnceForTest();
    }

    // =====================================================================================================
    // POINT model.
    // =====================================================================================================

    /// <summary>One spawn point on an empty map, respawn delay one beat. Entering the map materialises it on
    /// its home tile; riding it away frees the point and arms its clock (the same path a kill takes, minus
    /// the loot); the next beat's phase (1) puts a NEW creature back on the same tile.
    ///
    /// <para>Falsified by deleting the <c>_spawnDirector.RespawnDuePoints(_tick)</c> line in
    /// <c>World.Tick</c>: red on the second <c>Assert.Single</c> (the point stays empty).</para></summary>
    [Fact]
    public void ADeadPointComesBackOnItsHomeTileWhenItsClockIsDue()
    {
        var def = Creature(990_001, "test_point");
        _fx.World.UnderWorldLockForTest(() => Spawns.AddPointForTest(PointMap, def, 5, 5, respawnEvery: 1));
        var (watcher, _) = _fx.Player("PointWatcher", PointMap, x: 5, y: 10);
        try
        {
            var first = Assert.Single(MobsOf(watcher, PointMap, def));   // EnterMap materialised the point
            Assert.Equal(((ushort)5, (ushort)5), (first.X, first.Y));

            Assert.True(_fx.World.DespawnMob(PointMap, first));           // frees the point, clock = next beat
            Assert.Empty(MobsOf(watcher, PointMap, def));

            _fx.World.TickOnceForTest();

            var second = Assert.Single(MobsOf(watcher, PointMap, def));
            Assert.NotEqual(first.Id, second.Id);                         // a new creature, not the old one back
            Assert.Equal(((ushort)5, (ushort)5), (second.X, second.Y));   // on the point's home tile
            Assert.True(second.WorldSpawned);
        }
        finally
        {
            _fx.World.LeaveMap(watcher, PointMap);
            _fx.World.UnderWorldLockForTest(() => Spawns.ForgetMapForTest(PointMap));
        }
    }

    // =====================================================================================================
    // GROUP model.
    // =====================================================================================================

    /// <summary>One group, cap 5, one-second clock, on a real 8x8 map. Entering the map fills it to the cap
    /// (every member on walkable, non-warp ground, no two on one tile). Removing two brings nothing back on
    /// its own: a sweep before the clock is due leaves the room short. Once the clock passes, one sweep tops
    /// it back up to five — the three survivors untouched, the two newcomers on placeable tiles nobody is
    /// standing on, the watcher included.
    ///
    /// <para>The "before the clock" half is guarded on the wall clock: the group's clock is whole unix
    /// seconds, so if the second rolled over between the fill and the sweep the negative assertion is
    /// skipped rather than flaky. The positive half waits for the clock (at most one second).</para>
    ///
    /// <para>Falsified by deleting the <c>_spawnDirector.RefillDueGroups(_tick)</c> line in
    /// <c>World.Tick</c>: red on the refilled count (3, not 5).</para></summary>
    [Fact]
    public void AGroupRefillsToItsCapOnItsOwnClockAndNotOnAKill()
    {
        const int cap = 5;
        var def = Creature(990_002, "test_group");

        // The map is what the class doc says it is: dimensions, ground to stand on, and nothing of its own.
        Assert.True(Content.Maps.TryGetValue(GroupMap, out var info) && info.Xs == 8 && info.Ys == 8);
        Assert.Empty(Content.SpawnsFor(GroupMap));
        Assert.DoesNotContain(Content.AreaSpawns, a => a.Map == GroupMap);
        Assert.DoesNotContain(Content.Npcs, n => n.Map == GroupMap);
        Assert.True(World.SpawnDirector.OpenTiles(GroupMap, new HashSet<(int, int)>(), 0, 0, 0, 0).Count > cap + 1,
                    "the group needs more open tiles than its cap, plus the watcher's");

        _fx.World.UnderWorldLockForTest(() => Spawns.AddGroupForTest(GroupMap, timerSec: 1, (def, cap)));
        var (watcher, _) = _fx.Player("GroupWatcher", GroupMap, x: 1, y: 1);
        try
        {
            Assert.DoesNotContain(_fx.World.View(watcher, GroupMap).mobs, m => m.DefId != def.Id);   // nothing else here

            var filled = MobsOf(watcher, GroupMap, def);                  // EnterMap ran the due group
            Assert.Equal(cap, filled.Count);
            AssertOnOpenGround(filled);

            foreach (var m in filled.Take(2)) Assert.True(_fx.World.DespawnMob(GroupMap, m));
            Assert.Equal(cap - 2, MobsOf(watcher, GroupMap, def).Count);

            long due = 0;
            _fx.World.UnderWorldLockForTest(() => due = Spawns.GroupClockForTest(GroupMap));
            Assert.True(due > 0, "the fill should have stamped the group's clock");

            // Before the clock: a kill (or a ride-away) brings nothing back.
            SweepGroupsOnce();
            if (Now() < due) Assert.Equal(cap - 2, MobsOf(watcher, GroupMap, def).Count);

            // On the clock: one sweep tops the room back up to its cap.
            while (Now() < due) Thread.Sleep(25);
            SweepGroupsOnce();

            var refilled = MobsOf(watcher, GroupMap, def);
            Assert.Equal(cap, refilled.Count);
            AssertOnOpenGround(refilled);
            foreach (var survivor in filled.Skip(2))
                Assert.Contains(refilled, m => m.Id == survivor.Id);      // a top-up, not a replacement

            var newcomers = refilled.Where(m => filled.All(f => f.Id != m.Id)).ToList();
            Assert.Equal(2, newcomers.Count);
            foreach (var n in newcomers)
            {
                Assert.NotEqual(((ushort)watcher.PlayerX, (ushort)watcher.PlayerY), (n.X, n.Y));
                var others = new HashSet<(int, int)>(refilled.Where(m => m != n).Select(m => ((int)m.X, (int)m.Y)))
                    { (watcher.PlayerX, watcher.PlayerY) };
                Assert.True(World.SpawnDirector.Placeable(GroupMap, others, n.X, n.Y),
                            $"newcomer #{n.Id} on ({n.X},{n.Y}) is not on a placeable tile");
            }
        }
        finally
        {
            _fx.World.LeaveMap(watcher, GroupMap);
            _fx.World.UnderWorldLockForTest(() => Spawns.ForgetMapForTest(GroupMap));
        }
    }

    /// <summary>Every member on ground a creature may stand on — walkable, not a warp — and no two on one
    /// tile. Does not test the watcher's tile: on map entry the room fills BEFORE the newcomer joins the
    /// map's player list, which is the pre-existing order this PR keeps (a follow-up, not a change here).</summary>
    private static void AssertOnOpenGround(List<Mob> members)
    {
        var tiles = members.Select(m => ((int)m.X, (int)m.Y)).ToList();
        Assert.Equal(tiles.Count, tiles.Distinct().Count());
        foreach (var m in members)
            Assert.True(World.SpawnDirector.Placeable(GroupMap, new HashSet<(int, int)>(), m.X, m.Y),
                        $"#{m.Id} on ({m.X},{m.Y}) is on a wall or a warp");
    }

    // =====================================================================================================
    // The lock.
    // =====================================================================================================

#if DEBUG
    /// <summary>Every director entry point the tick, the map entry and the death path use refuses to run
    /// without <c>World._lock</c>: under the lock it is silent, off it the assert at the top of the method
    /// fires before any state is touched. Debug-only by construction, like every lock assert in
    /// <c>docs/common/Locking.md</c> (the test host turns a failed <c>Debug.Assert</c> into an exception
    /// carrying the message; a Debug server process would fail fast).
    ///
    /// <para>Falsified by deleting the assert at the top of <c>ReleasePoint</c>: red with "ReleasePoint ran
    /// outside World._lock without tripping its assert". Deleting <c>EnsureMaterialized</c>'s assert alone
    /// does NOT go red — the call reaches <c>RefillGroups</c>, whose own assert trips — which is the layering
    /// the moved code already had (every private method that touches map state asserts for itself), so the
    /// method to falsify on is one with no asserting callee.</para></summary>
    [Fact]
    public void DirectorMethodsRefuseToRunOutsideTheWorldLock()
    {
        try
        {
            Assert.Null(Record.Exception(() => _fx.World.UnderWorldLockForTest(() => Spawns.EnsureMaterialized(LockMap))));
            Assert.False(_fx.World.HoldsWorldLock);

            var stray = new Mob(_fx.World.AllocateMobId(), 1, 1, 1, "Stray", 100);
            foreach (var (name, call) in new (string, Action)[]
            {
                ("EnsureMaterialized", () => Spawns.EnsureMaterialized(LockMap)),
                ("RespawnDuePoints",   () => Spawns.RespawnDuePoints(0)),
                ("RefillDueGroups",    () => Spawns.RefillDueGroups(0)),
                ("RecordDeath",        () => Spawns.RecordDeath(LockMap, stray)),
                ("ReleasePoint",       () => Spawns.ReleasePoint(stray)),
            })
            {
                var ex = Record.Exception(call);
                Assert.True(ex is not null, $"{name} ran outside World._lock without tripping its assert");
                Assert.Contains("nowhere else", ex!.Message);
            }
        }
        finally { _fx.World.UnderWorldLockForTest(() => Spawns.ForgetMapForTest(LockMap)); }
    }
#endif
}

/// <summary>The live population rebuild (<c>@reload</c>): <c>World.RebuildPopulation</c> drops both rosters and
/// the materialised set and rebuilds them from <see cref="Content"/>. On its own <c>World</c>, because the
/// rebuild tears down every creature on every map. Falsified by deleting the <c>_spawnDirector.Clear()</c>
/// line in <c>RebuildPopulation</c>: red on the test point's count (still 1).</summary>
public class SpawnDirectorRebuildTests
{
    private const ushort RebuildMap = 60042;
    private const ushort Kugnae = 0;   // has Spawns.csv rows, so its point count is a real number to compare

    [Fact]
    public void RebuildPopulationClearsTheRostersAndRebuildsThemFromContent()
    {
        TestProcessState.LoadContent();
        var world = new World();
        var spawns = world.SpawnsForTest;

        int kugnaeBefore = spawns.PointCountForTest(Kugnae);
        Assert.True(kugnaeBefore > 0, "Kugnae should have spawn points from Spawns.csv");

        var def = new MobDef(990_003, "test_rebuild", "Test rebuild", Look: 1, Color: 0, Hp: 100, Exp: 0, Level: 1,
                             MoveTime: 1_000_000, Stationary: true);
        world.UnderWorldLockForTest(() =>
        {
            spawns.AddPointForTest(RebuildMap, def, 3, 3, respawnEvery: 1);
            spawns.AddGroupForTest(RebuildMap, timerSec: 1, (def, 2));
            spawns.EnsureMaterialized(RebuildMap);
        });
        Assert.Equal(1, spawns.PointCountForTest(RebuildMap));
        Assert.Equal(1, spawns.GroupCountForTest(RebuildMap));
        Assert.True(spawns.IsMaterializedForTest(RebuildMap));
        Assert.Equal(1, world.MobCountForTest(RebuildMap));   // the point's creature (the group has no box to roll in here)

        var (mobs, npcs, maps) = world.RebuildPopulation();

        Assert.True(mobs >= 1, "the torn-down count should include the test point's creature");
        Assert.True(npcs > 0, "the NPCs are re-placed from Content");
        Assert.Equal(0, maps);                                 // nobody is on any map of this world
        Assert.Equal(0, spawns.PointCountForTest(RebuildMap));  // the test registrations are gone…
        Assert.Equal(0, spawns.GroupCountForTest(RebuildMap));
        Assert.False(spawns.IsMaterializedForTest(RebuildMap));
        Assert.Equal(0, world.MobCountForTest(RebuildMap));
        Assert.Equal(kugnaeBefore, spawns.PointCountForTest(Kugnae));   // …and Content's roster is back as it was
    }
}
