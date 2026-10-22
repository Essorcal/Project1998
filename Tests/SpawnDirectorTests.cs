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
/// unaggressive, so nothing in the AI sweep moves them and no <c>MobSpawnRules</c> row gates them — except
/// the death-cooldown test, whose creature borrows a real rule by KEY. The POINT tests run on content-free
/// maps (no dimensions, so <c>FreeSpawnTile</c> checks only occupancy); the GROUP tests need a real box to
/// roll in, so they run on map 485 (Buya Legend, 8x8), which carries no spawn, NPC, forage, trap or ambush
/// content — asserted, not assumed. Every test undoes its registrations in <c>finally</c>
/// (<c>ForgetMapForTest</c>), since the fixture World is shared by the collection.</para>
/// </summary>
[Collection("world")]
public class SpawnDirectorTests
{
    private readonly SessionFixture _fx;

    public SpawnDirectorTests(SessionFixture fx) => _fx = fx;

    private const ushort PointMap = 60040, LockMap = 60041, BossMap = 60043, FirstEntryMap = 60222;
    private const ushort GroupMap = 485;   // Buya Legend: 8x8, a map file on disk, nothing spawns on it

    private World.SpawnDirector Spawns => _fx.World.SpawnsForTest;

    private static MobDef Creature(int id, string key) =>
        new()
        {
            Id = id,
            Key = key,
            Name = "Test " + key,
            Look = 1,
            Color = 0,
            Hp = 100,
            Exp = 0,
            Level = 1,
            MoveTime = 1_000_000,
            Stationary = true,
        };

    private List<Mob> MobsOf(Session viewer, ushort map, MobDef def) =>
        _fx.World.View(viewer, map).mobs.Where(m => m.DefId == def.Id).ToList();

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>A director read under the lock its seams assert, returned to the test.</summary>
    private T Locked<T>(Func<T> read)
    {
        T value = default!;
        _fx.World.UnderWorldLockForTest(() => value = read());
        return value;
    }

    private void SweepGroupsOnce()
    {
        // Phase (1.1) samples the groups every BatchSweepTicks beats; this many beats hits it exactly once.
        for (int i = 0; i < World.BatchSweepTicksForTest; i++) _fx.World.TickOnceForTest();
    }

    private static void AssertGroupMapIsWhatTheDocSays(int cap)
    {
        // The map is what the class doc says it is: dimensions, ground to stand on, and nothing of its own.
        Assert.True(Content.Maps.TryGetValue(GroupMap, out var info) && info.Xs == 8 && info.Ys == 8);
        Assert.Empty(Content.SpawnsFor(GroupMap));
        Assert.DoesNotContain(Content.AreaSpawns, a => a.Map == GroupMap);
        Assert.DoesNotContain(Content.Npcs, n => n.Map == GroupMap);
        Assert.True(World.SpawnDirector.OpenTiles(GroupMap, new HashSet<(int, int)>(), 0, 0, 0, 0).Count > cap + 1,
                    "the group needs more open tiles than its cap, plus the watcher's");
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

    /// <summary>A map's first entry materialises every ordinary point on its own tile, and leaves a rare boss
    /// point pending rather than lost: the boss is absent straight after the entry and appears on its tile
    /// when its first-appearance clock comes round, inside its window (one beat after the entry plus up to
    /// <c>RespawnEvery - 1</c> more; three beats here). The map-entry row guard wraps each ordinary point's
    /// <c>Materialize</c> and leaves the rare branch as it was, which is what this pins.
    ///
    /// <para>Falsified by deleting the rare branch in <c>SpawnDirector.EnsureMaterialized</c>: red, the boss
    /// is on the map straight after the entry.</para></summary>
    [Fact]
    public void AFirstEntryMaterialisesEveryOrdinaryPointAndLeavesARareOnePending()
    {
        const int bossWindow = 3;   // beats
        var west = Creature(990_203, "test_first_entry_west");
        var east = Creature(990_204, "test_first_entry_east");
        var boss = Creature(990_205, "test_first_entry_rare");
        _fx.World.UnderWorldLockForTest(() =>
        {
            Spawns.AddPointForTest(FirstEntryMap, west, 3, 5, respawnEvery: 1);
            Spawns.AddPointForTest(FirstEntryMap, boss, 5, 5, respawnEvery: bossWindow, rare: true);
            Spawns.AddPointForTest(FirstEntryMap, east, 7, 5, respawnEvery: 1);
        });
        var (watcher, _) = _fx.Player("FirstEntryWatcher", FirstEntryMap, x: 5, y: 10);
        try
        {
            var w = Assert.Single(MobsOf(watcher, FirstEntryMap, west));
            Assert.Equal(((ushort)3, (ushort)5), (w.X, w.Y));
            var e = Assert.Single(MobsOf(watcher, FirstEntryMap, east));
            Assert.Equal(((ushort)7, (ushort)5), (e.X, e.Y));
            Assert.Empty(MobsOf(watcher, FirstEntryMap, boss));   // pending, not materialised

            for (int beat = 0; beat < bossWindow && MobsOf(watcher, FirstEntryMap, boss).Count == 0; beat++)
                _fx.World.TickOnceForTest();
            var b = Assert.Single(MobsOf(watcher, FirstEntryMap, boss));   // and not lost: its clock came round
            Assert.Equal(((ushort)5, (ushort)5), (b.X, b.Y));
        }
        finally
        {
            _fx.World.LeaveMap(watcher, FirstEntryMap);
            _fx.World.UnderWorldLockForTest(() => Spawns.ForgetMapForTest(FirstEntryMap));
        }
    }

    /// <summary>The boss death registry (RTK's <c>lastDeathCitelam</c>): a creature whose spawn rule carries
    /// a <c>DeathCooldownSec</c> does not come back inside the window after it is killed, and does once the
    /// window has passed. The creature is synthetic but keyed <c>ogre_citelam</c>: <c>MobSpawnRules</c> is
    /// looked up by creature KEY, so the real rule (one alive, a 1-in-10 roll per refill, a 1800 s cooldown)
    /// applies to it with no content seam. The roll is what makes the two "it appears" steps a bounded loop
    /// of beats (400: the chance of no appearance is 0.9^400); the "it stays away" step is deterministic,
    /// because <c>Materialize</c> asks the cooldown before it rolls. The kill's bookkeeping is
    /// <c>RecordDeath</c> under the lock, the call <c>TryDamage</c> makes; the window is moved into the past
    /// with <c>StampDeathForTest</c> rather than waited out.
    ///
    /// <para>Falsified by deleting the two-line death stamp in <c>SpawnDirector.RecordDeath</c>: red inside
    /// the cooldown loop ("the point re-materialised on beat N of the death cooldown"; with the stamp gone
    /// the point is back on the roll, and 200 beats miss a 1-in-10 roll with probability 0.9^200).</para></summary>
    [Fact]
    public void AKilledBossStaysDeadForItsCooldownAndReturnsAfterIt()
    {
        var rule = Content.MobSpawnRules["ogre_citelam"];
        Assert.True(rule.DeathCooldownSec > 0 && rule.SpawnChance > 1 && rule.Rooms.Length == 0 && rule.MaxAlive == 1,
                    "the test leans on the shape of the citelam rule; MobSpawnRules.csv changed under it");
        var def = Creature(990_005, "ogre_citelam");   // the rule's key, a synthetic creature

        _fx.World.UnderWorldLockForTest(() => Spawns.AddPointForTest(BossMap, def, 5, 5, respawnEvery: 1));
        var (watcher, _) = _fx.Player("BossWatcher", BossMap, x: 5, y: 10);
        try
        {
            var first = AwaitThePoint(watcher, def, "before any death");
            Assert.Equal(((ushort)5, (ushort)5), (first.X, first.Y));

            Assert.True(_fx.World.DespawnMob(BossMap, first));                            // off the map, point freed
            _fx.World.UnderWorldLockForTest(() => Spawns.RecordDeath(BossMap, first));   // the kill's stamp
            long killed = Now();

            for (int beat = 1; beat <= 200; beat++)
            {
                _fx.World.TickOnceForTest();
                int back = MobsOf(watcher, BossMap, def).Count;
                Assert.True(back == 0, $"the point re-materialised on beat {beat} of the death cooldown");
            }

            _fx.World.UnderWorldLockForTest(() =>
                Spawns.StampDeathForTest(BossMap, def.Key, killed - rule.DeathCooldownSec - 1));   // window over
            var second = AwaitThePoint(watcher, def, "after the cooldown");
            Assert.NotEqual(first.Id, second.Id);
            Assert.Equal(((ushort)5, (ushort)5), (second.X, second.Y));
        }
        finally
        {
            _fx.World.LeaveMap(watcher, BossMap);
            _fx.World.UnderWorldLockForTest(() => Spawns.ForgetMapForTest(BossMap));
        }
    }

    /// <summary>Beats until the boss point's roll comes up, bounded; the creature it placed.</summary>
    private Mob AwaitThePoint(Session watcher, MobDef def, string when)
    {
        const int maxBeats = 400;
        for (int beat = 0; beat <= maxBeats; beat++)
        {
            var live = MobsOf(watcher, BossMap, def);
            if (live.Count == 1) return live[0];
            Assert.True(live.Count == 0, $"{live.Count} of a MaxAlive-1 creature on the map {when}");
            _fx.World.TickOnceForTest();
        }
        Assert.Fail($"the point did not materialise in {maxBeats} beats {when} (a 1-in-{Content.MobSpawnRules[def.Key].SpawnChance} roll per beat)");
        return null!;
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
        AssertGroupMapIsWhatTheDocSays(cap);

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

            long due = Locked(() => Spawns.GroupClockForTest(GroupMap));
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

    /// <summary>Phase (1.1)'s sampling, on its own: the groups are looked at only on beats where the tick
    /// counter is a multiple of <c>BatchSweepTicks</c>. With the group due and the room one short, beats are
    /// driven one at a time and the counter read after each: on a beat phase (1.1) skips, neither the count
    /// nor the group's clock moves; on the sampled beat the room is topped up and the clock stamped. Exactly
    /// one beat in <c>BatchSweepTicks</c> is sampled.
    ///
    /// <para>Pinned separately because the move inverted this one condition (the loop's <c>== 0</c> became
    /// the sweep's <c>!= 0 return</c>) and the test above cannot see it: its one-second clock would keep it
    /// green with the guard inverted back. Falsified by inverting the guard (<c>!=</c> to <c>==</c>): red on
    /// the first beat the loop drives, whichever kind it is.</para></summary>
    [Fact]
    public void GroupsAreSampledOnlyOnEveryBatchSweepTicksThBeat()
    {
        const int cap = 3;
        int period = World.BatchSweepTicksForTest;
        var def = Creature(990_004, "test_cadence");
        AssertGroupMapIsWhatTheDocSays(cap);

        _fx.World.UnderWorldLockForTest(() => Spawns.AddGroupForTest(GroupMap, timerSec: 1, (def, cap)));
        var (watcher, _) = _fx.Player("CadenceWatcher", GroupMap, x: 1, y: 1);
        try
        {
            var filled = MobsOf(watcher, GroupMap, def);
            Assert.Equal(cap, filled.Count);
            Assert.True(_fx.World.DespawnMob(GroupMap, filled[0]));
            long due = Locked(() => Spawns.GroupClockForTest(GroupMap));
            while (Now() < due) Thread.Sleep(25);                          // the group is due; only the sampling gates it now

            int prevCount = cap - 1, sampledBeats = 0;
            long prevClock = due;
            for (int i = 0; i < period; i++)
            {
                _fx.World.TickOnceForTest();
                long tick = _fx.World.TickForTest;
                int count = MobsOf(watcher, GroupMap, def).Count;
                long clock = Locked(() => Spawns.GroupClockForTest(GroupMap));
                if (tick % period == 0)
                {
                    sampledBeats++;
                    Assert.True(count == cap, $"beat {tick} is sampled (tick % {period} == 0) but the room was not topped up: {count} of {cap}");
                    Assert.True(clock > due, $"beat {tick} is sampled but the group's clock was not stamped");
                }
                else
                {
                    Assert.True(count == prevCount, $"beat {tick} is not sampled (tick % {period} != 0) but the room changed: {prevCount} -> {count}");
                    Assert.True(clock == prevClock, $"beat {tick} is not sampled but the group's clock moved");
                }
                prevCount = count; prevClock = clock;
            }
            Assert.Equal(1, sampledBeats);
            Assert.Equal(cap, MobsOf(watcher, GroupMap, def).Count);
        }
        finally
        {
            _fx.World.LeaveMap(watcher, GroupMap);
            _fx.World.UnderWorldLockForTest(() => Spawns.ForgetMapForTest(GroupMap));
        }
    }

    /// <summary>Every member on ground a creature may stand on — walkable, not a warp — and no two on one
    /// tile. Does not test the watcher's tile: that the fill on map entry leaves the newcomer's tile free
    /// (#122) is <see cref="MapEntryPlacementTests"/>' fact.</summary>
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
            List<World.PhaseFault>? faults = null;
            foreach (var (name, call) in new (string, Action)[]
            {
                ("EnsureMaterialized", () => Spawns.EnsureMaterialized(LockMap)),
                ("RespawnDuePoints",   () => Spawns.RespawnDuePoints(0, ref faults)),
                ("RefillDueGroups",    () => Spawns.RefillDueGroups(0, ref faults)),
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
/// rebuild tears down every creature on every map; that puts it in its own xUnit collection, so its
/// <c>LoadContent()</c> can run in parallel with the shared world fixture's — the established pattern for
/// tests that build a World of their own. Falsified by deleting the <c>_spawnDirector.Clear()</c> line in
/// <c>RebuildPopulation</c>: red on the test point's count (still 1).
///
/// <para>#121: the rebuild also empties the live-mob-to-spawn-point index. Before it, the test point's
/// creature — torn down by the rebuild, its id never to be reused — stayed indexed for the life of the
/// process. Nobody is on any map of this world, so the rebuild re-materialises nothing and the index is
/// exactly the test point's one entry before and nothing after. Falsified by deleting
/// <c>_mobSpawn.Clear()</c> from <c>SpawnDirector.Clear</c>: red on the index count after the rebuild
/// (still 1).</para></summary>
public class SpawnDirectorRebuildTests
{
    private const ushort RebuildMap = 60042;
    private const ushort Kugnae = 0;   // has Spawns.csv rows, so its point count is a real number to compare

    private static T Locked<T>(World world, Func<T> read)
    {
        T value = default!;
        world.UnderWorldLockForTest(() => value = read());
        return value;
    }

    [Fact]
    public void RebuildPopulationClearsTheRostersAndRebuildsThemFromContent()
    {
        TestProcessState.LoadContent();
        var world = new World();
        var spawns = world.SpawnsForTest;

        int kugnaeBefore = Locked(world, () => spawns.PointCountForTest(Kugnae));
        Assert.True(kugnaeBefore > 0, "Kugnae should have spawn points from Spawns.csv");

        var def = new MobDef
        {
            Id = 990_003,
            Key = "test_rebuild",
            Name = "Test rebuild",
            Look = 1,
            Color = 0,
            Hp = 100,
            Exp = 0,
            Level = 1,
            MoveTime = 1_000_000,
            Stationary = true,
        };
        world.UnderWorldLockForTest(() =>
        {
            spawns.AddPointForTest(RebuildMap, def, 3, 3, respawnEvery: 1);
            spawns.AddGroupForTest(RebuildMap, timerSec: 1, (def, 2));
            spawns.EnsureMaterialized(RebuildMap);
        });
        Assert.Equal(1, Locked(world, () => spawns.PointCountForTest(RebuildMap)));
        Assert.Equal(1, Locked(world, () => spawns.GroupCountForTest(RebuildMap)));
        Assert.True(Locked(world, () => spawns.IsMaterializedForTest(RebuildMap)));
        Assert.Equal(1, world.MobCountForTest(RebuildMap));   // the point's creature (the group has no box to roll in here)
        Assert.Equal(1, Locked(world, () => spawns.MobSpawnIndexCountForTest()));   // …indexed to its point

        var (mobs, npcs, maps) = world.RebuildPopulation();

        Assert.True(mobs >= 1, "the torn-down count should include the test point's creature");
        Assert.True(npcs > 0, "the NPCs are re-placed from Content");
        Assert.Equal(0, maps);                                 // nobody is on any map of this world
        Assert.Equal(0, Locked(world, () => spawns.PointCountForTest(RebuildMap)));   // the test registrations are gone…
        Assert.Equal(0, Locked(world, () => spawns.GroupCountForTest(RebuildMap)));
        Assert.False(Locked(world, () => spawns.IsMaterializedForTest(RebuildMap)));
        Assert.Equal(0, world.MobCountForTest(RebuildMap));
        Assert.Equal(0, Locked(world, () => spawns.MobSpawnIndexCountForTest()));    // #121: no stale entry left behind
        Assert.Equal(kugnaeBefore, Locked(world, () => spawns.PointCountForTest(Kugnae)));   // …and Content's roster is back as it was
    }
}

/// <summary>
/// #333: a spawn point whose creature has rooms (<c>MobSpawnRules.csv</c>'s <c>Rooms</c>, RTK's
/// <c>on_spawn = function(mob) mob:warp(map, x, y) end</c>) is filed under its rooms' maps, not under the
/// map its row names. Before the fix the 61 map-4 bosses were filed under map 4, the NPC Chamber nobody
/// enters, so none of them existed until someone entered map 4, and a killed one came back only while
/// someone stood there.
///
/// <para>Each fact builds a <see cref="World"/> of its own, as <see cref="MapEntryPlacementTests"/>' trap
/// facts do: the rooms are real maps with real content, and entering one on the shared fixture would leave
/// that map's creatures, the real boss among them, in every later class's world. Where a fact needs a
/// point of its own, its creature is synthetic but keyed by a real rule, the way
/// <see cref="SpawnDirectorTests"/>' death-cooldown fact borrows <c>ogre_citelam</c>'s: <c>MobSpawnRules</c>
/// is looked up by creature KEY. Those points' row maps are content-free ids nobody enters. The other facts
/// use the real map-4 points that <c>Spawns.csv</c> gives every <c>World</c>. Each borrowed rule's room maps
/// are used by no other class, and the warm fact's by no other fact. The sessions use the shared fixture's
/// store, which is why the class is in the <c>world</c> collection.</para>
/// </summary>
[Collection("world")]
public class SpawnDirectorRoomTests
{
    private readonly SessionFixture _fx;

    public SpawnDirectorRoomTests(SessionFixture fx) => _fx = fx;

    private const ushort RowMap = 60206, TwoRoomRowMap = 60207;   // synthetic points' rows: no Maps.csv row, nobody enters
    private const ushort NpcChamber = 4;   // where Spawns.csv puts the 61 map-4 bosses and the three nine-tailed foxes
    private const ushort Gnaw1 = 157;      // mythic_rat's one room, 157:23:15 (Gnaw 1, 30x30)
    private const ushort RatEyes2 = 3155, Gnaw2 = 3157;   // divine_rat's two rooms, 3155:10:6 and 3157:23:15
    private const ushort DogRoomA = 3193, DogRoomB = 3195;   // divine_dog's two rooms; the warm fact's alone
    private const ushort GimYisDen = 1308;   // gimyi's one room, 1308:2:5, and its batch group (AreaSpawns.csv)
    private const ushort OffMapRowMap = 60208;   // the off-map fact's synthetic point's row
    private const ushort MonkeyFlow3 = 4163, MonkeyDance3 = 4164, MonkeyWasteland3 = 4167;   // monkey_avenger's rooms' maps
    private const ushort WoolenSqueeze2 = 3472, WoolenSqueeze3 = 4472;   // divine_sheep's and sheep_avenger's 10-wide rooms
    private const ushort DragonRowMap = 60209;   // the Jeweled Dragon fact's synthetic point's row
    private const ushort DragonsGold3 = 4254, DragonDeath3 = 4253, JeweledDragon3 = 4258;   // spirit_dragon's rooms' maps
    private const ushort BrokenRowMap = 60210;   // the entry-fault fact's synthetic point's row
    private const ushort BullsSong2 = 3175, OxPath2 = 3178;   // divine_ox's two rooms, 3175:12:21 and 3178:15:20
    private const ushort BoarRise2 = 3189, CloveHill2 = 3187;   // divine_pig's two rooms, 3189:7:9 and 3187:20:23
    private const ushort Unrenderable = 60211;   // the pick fact's room map: no Maps.csv row, so the client cannot render it

    /// <summary>A director read under the lock its seams assert, returned to the test.</summary>
    private static T Locked<T>(World world, Func<T> read)
    {
        T value = default!;
        world.UnderWorldLockForTest(() => value = read());
        return value;
    }

    /// <summary>A rule this class leans on, with its shape checked: rooms on exactly these maps, and no
    /// population cap, roll or death cooldown, so a point of its creature is built every time it is asked.</summary>
    private static Content.MobSpawnRuleDef RoomRule(string key, params ushort[] maps)
    {
        var rule = Content.MobSpawnRules[key];
        Assert.True(rule.Rooms.Select(r => r.Map).Distinct().OrderBy(m => m).SequenceEqual(maps.OrderBy(m => m))
                    && rule.MaxAlive == 0 && rule.SpawnChance == 0 && rule.DeathCooldownSec == 0,
                    $"the fact leans on the shape of {key}'s rule; MobSpawnRules.csv changed under it");
        return rule;
    }

    /// <summary>Every creature of <paramref name="def"/> on any of <paramref name="rule"/>'s room maps, with
    /// the room it is standing in.</summary>
    private static List<(Mob Mob, (ushort Map, ushort X, ushort Y) Room)> InTheirRooms(
        World world, Session viewer, Content.MobSpawnRuleDef rule, MobDef def) =>
        rule.Rooms.SelectMany(room => world.View(viewer, room.Map).mobs
                                          .Where(m => m.DefId == def.Id)
                                          .Select(m => (m, room)))
            .GroupBy(t => t.m.Id).Select(g => g.First()).ToList();

    private static MobDef Creature(int id, string key, int moveTime = 1_000_000) =>
        new()
        {
            Id = id,
            Key = key,
            Name = "Test " + key,
            Look = 1,
            Color = 0,
            Hp = 100,
            Exp = 0,
            Level = 1,
            MoveTime = moveTime,
            Stationary = true,
        };

    /// <summary>A session on <paramref name="world"/> standing on <paramref name="tile"/> of
    /// <paramref name="map"/>, not yet on any map's list (the fact enters it itself).</summary>
    private Session Seat(World world, string name, ushort map, (ushort X, ushort Y) tile)
    {
        var character = new Character
        {
            SchemaVersion = Character.CurrentSchemaVersion,
            Id = world.AllocatePlayerId(),
            Name = name,
            Map = map,
            X = tile.X,
            Y = tile.Y,
        };
        return new Session(new RecordingOutbound($"recorder:{name}"), 2005, _fx.Store, world, character);
    }

    private static List<Mob> MobsOf(World world, Session viewer, ushort map, MobDef def) =>
        world.View(viewer, map).mobs.Where(m => m.DefId == def.Id).ToList();

    /// <summary>On the room's tile or within two of it: <c>Materialize</c> places through
    /// <c>FreeSpawnTile</c>, which takes the nearest open tile within two when the room's own is a wall or
    /// has a creature on it.</summary>
    private static void AssertInTheRoom(Mob boss, (ushort Map, ushort X, ushort Y) room)
    {
        int off = Math.Max(Math.Abs(boss.X - room.X), Math.Abs(boss.Y - room.Y));
        Assert.True(off <= 2, $"#{boss.Id} stands on ({boss.X},{boss.Y}), {off} tiles from its room ({room.X},{room.Y})");
    }

    /// <summary>The issue's own test. A point on a row map nobody enters, whose creature borrows
    /// <c>mythic_rat</c>'s rule (one room, 157:23:15). A player who enters only the room's map finds the boss
    /// there. Killed, it stays away for its respawn window and is back when the window closes, with nobody on
    /// the point's row map at any time.
    ///
    /// <para>Red on master's code at the first check: the point is filed under its row map, which nobody
    /// enters, so entering the room builds nothing.</para></summary>
    [Fact]
    public void ABossWaitsInItsRoomForAPlayerWhoNeverEntersItsRowMap()
    {
        var rule = Content.MobSpawnRules["mythic_rat"];
        Assert.True(rule.Rooms.Length == 1 && rule.Rooms[0] == (Gnaw1, (ushort)23, (ushort)15)
                    && rule.MaxAlive == 0 && rule.SpawnChance == 0 && rule.DeathCooldownSec == 0,
                    "the fact leans on the shape of mythic_rat's rule; MobSpawnRules.csv changed under it");
        var room = rule.Rooms[0];
        const int window = 3;   // beats
        var world = new World();   // constructed, never started: nothing moves unless this fact moves it
        var def = Creature(990_206, "mythic_rat");
        world.UnderWorldLockForTest(() => world.SpawnsForTest.AddPointForTest(RowMap, def, 3, 3, respawnEvery: window));

        var watcher = Seat(world, "RoomWatcher", Gnaw1, (1, 1));
        world.EnterMap(watcher, Gnaw1);

        var boss = Assert.Single(MobsOf(world, watcher, Gnaw1, def));   // the first entry to its room built it
        AssertInTheRoom(boss, room);
        Assert.Equal(0, world.MobCountForTest(RowMap));

        Assert.True(world.DespawnMob(Gnaw1, boss));                                     // off the map, point freed
        world.UnderWorldLockForTest(() => world.SpawnsForTest.RecordDeath(Gnaw1, boss));   // the kill's stamp
        for (int beat = 1; beat < window; beat++)
        {
            world.TickOnceForTest();
            Assert.True(MobsOf(world, watcher, Gnaw1, def).Count == 0, $"the boss was back on beat {beat} of a {window}-beat window");
        }
        world.TickOnceForTest();

        var back = Assert.Single(MobsOf(world, watcher, Gnaw1, def));   // served with nobody on its row map
        Assert.NotEqual(boss.Id, back.Id);
        AssertInTheRoom(back, room);
        Assert.Equal(0, world.MobCountForTest(RowMap));
        Assert.Empty(world.View(watcher, RowMap).peers);
    }

    /// <summary>A boss whose rooms span two maps (<c>divine_rat</c>: 3155:10:6 and 3157:23:15, the issue's own
    /// example) has one point, filed under both. The first entry to either builds it, in a room picked at
    /// random among both, so possibly on the other map; the first entry to the second map does NOT build it
    /// again. Killed, it is refilled while a player is on either map: here only on 3157, the second of its
    /// rooms' maps. Killed again with nobody on either map, it stays away; once someone is back on either,
    /// the next beat refills it.
    ///
    /// <para>Falsified by dropping the "not yet built or pending" test from <c>EnsureMaterialized</c>'s room
    /// loop: red, two bosses after the second map's first entry. By making the respawn pass ask only the
    /// first of a boss's rooms' maps for a player: red, the boss does not come back for the player on 3157.
    /// By dropping the player test from the respawn pass's room loop: red, the boss is back with nobody on
    /// either map.</para></summary>
    [Fact]
    public void ABossWhoseRoomsSpanTwoMapsIsBuiltOnceAndServedFromEither()
    {
        var rule = RoomRule("divine_rat", RatEyes2, Gnaw2);
        Assert.Equal(RatEyes2, rule.Rooms[0].Map);   // the respawn half stands its player on the OTHER map
        const int window = 3;   // beats
        var world = new World();
        var def = Creature(990_207, "divine_rat");
        world.UnderWorldLockForTest(() => world.SpawnsForTest.AddPointForTest(TwoRoomRowMap, def, 3, 3, respawnEvery: window));

        var east = Seat(world, "TwoRoomEast", RatEyes2, (1, 1));
        world.EnterMap(east, RatEyes2);
        var (boss, room) = Assert.Single(InTheirRooms(world, east, rule, def));   // built, on either map
        AssertInTheRoom(boss, room);

        var west = Seat(world, "TwoRoomWest", Gnaw2, (1, 1));
        world.EnterMap(west, Gnaw2);                                             // its other map's first entry
        Assert.Equal(boss.Id, Assert.Single(InTheirRooms(world, east, rule, def)).Mob.Id);   // not built twice

        // Killed; the player on its first rooms' map leaves, the one on its second stays.
        Assert.True(world.DespawnMob(room.Map, boss));
        world.UnderWorldLockForTest(() => world.SpawnsForTest.RecordDeath(room.Map, boss));
        world.LeaveMap(east, RatEyes2);
        for (int beat = 0; beat < window; beat++) world.TickOnceForTest();
        var (back, backRoom) = Assert.Single(InTheirRooms(world, west, rule, def));
        Assert.NotEqual(boss.Id, back.Id);
        AssertInTheRoom(back, backRoom);

        // Killed again with nobody on either map: it stays away, however long.
        Assert.True(world.DespawnMob(backRoom.Map, back));
        world.UnderWorldLockForTest(() => world.SpawnsForTest.RecordDeath(backRoom.Map, back));
        world.LeaveMap(west, Gnaw2);
        for (int beat = 0; beat < 4 * window; beat++) world.TickOnceForTest();
        Assert.Empty(InTheirRooms(world, west, rule, def));

        // Someone is back on one of its maps: the next beat refills it. The entry itself does not (the map
        // is not new), which is the ordinary point's rule too.
        world.EnterMap(east, RatEyes2);
        world.TickOnceForTest();
        var (again, againRoom) = Assert.Single(InTheirRooms(world, east, rule, def));
        AssertInTheRoom(again, againRoom);
    }

    /// <summary><c>@reload</c> (<c>World.RebuildPopulation</c>) with players on a boss's rooms' maps: the
    /// real <c>divine_rat</c> point, filed under 3155 and 3157, with a player on each. The rebuild tears the
    /// boss down with everything else and builds it again itself, once, though both its maps have a player
    /// and both are re-materialised; and the rebuilt roster files the point under each map once, not on top
    /// of the old one.
    ///
    /// <para>Red on master's code at the first check (the point is filed under map 4). Falsified by
    /// dropping <c>_roomPoints.Clear()</c> from <c>SpawnDirector.Clear</c>: red, the point is filed twice
    /// under 3155 after the rebuild (one boss still: the stale point's creature was torn down, so it is not
    /// rebuilt, but it would be held for the life of the process, #121's leak).</para></summary>
    [Fact]
    public void AReloadRebuildsARoomBossOnceForThePlayersOnItsRoomsMaps()
    {
        var rule = RoomRule("divine_rat", RatEyes2, Gnaw2);
        var def = Content.Mobs.Single(m => m.Key == "divine_rat");
        var world = new World();
        var east = Seat(world, "ReloadEast", RatEyes2, (1, 1));
        var west = Seat(world, "ReloadWest", Gnaw2, (1, 1));
        world.EnterMap(east, RatEyes2);
        world.EnterMap(west, Gnaw2);
        var before = Assert.Single(InTheirRooms(world, east, rule, def)).Mob;

        var (_, _, maps) = world.RebuildPopulation();

        Assert.Equal(2, maps);                                                   // both rooms' maps re-materialised
        var after = Assert.Single(InTheirRooms(world, east, rule, def));         // built by the rebuild, once
        Assert.NotEqual(before.Id, after.Mob.Id);
        AssertInTheRoom(after.Mob, after.Room);
        foreach (var map in new[] { RatEyes2, Gnaw2 })
            Assert.Single(Locked(world, () => world.SpawnsForTest.PointKeysForTest(map)), k => k == def.Key);
    }

    /// <summary>The roster <c>Spawns.csv</c> gives every <see cref="World"/>: each point on map 4 whose
    /// creature has rooms is filed under every one of its rooms' maps but a room off its map (no tile of the map
    /// within two; <see cref="ARoomWithNoTileOfItsMapWithinTwoIsLeftOutOfTheFilingAndThePick"/> pins the one
    /// there is), and not under map 4. Each room is on a map the client renders (a room elsewhere would be left
    /// out of the filing and the pick, <see cref="ThePickSkipsEveryRoomTheFilingSkips"/>). What stays on map 4 is the three
    /// nine-tailed foxes, which have no rule, and <c>gimyi</c>, whose room's map already has a batch group of
    /// it (<see cref="GimYisDenKeepsItsOneGimYiAndItsPointStaysOnMapFour"/>).
    ///
    /// <para>Falsified by filing every point under its row's map again (<c>AddSpawn</c>): red, a boss is
    /// filed under map 4.</para></summary>
    [Fact]
    public void TheMapFourBossesAreFiledUnderTheirRoomsMapsAndTheFoxesStay()
    {
        var world = new World();
        var onChamber = Locked(world, () => world.SpawnsForTest.PointKeysForTest(NpcChamber));
        int bosses = 0;
        foreach (var row in Content.SpawnsFor(NpcChamber))
        {
            if (Content.MobById(row.MobId) is not { } def) continue;   // Build skips an unknown creature too
            if (!Content.MobSpawnRules.TryGetValue(def.Key, out var rule) || rule.Rooms.Length == 0
                || Content.AreaSpawns.Any(a => a.Timer > 0 && a.MobId == def.Id && rule.Rooms.Any(r => r.Map == a.Map)))
            {
                Assert.Contains(def.Key, onChamber);
                continue;
            }
            bosses++;
            Assert.DoesNotContain(def.Key, onChamber);
            foreach (var room in rule.Rooms)
            {
                Assert.True(Content.Maps.TryGetValue(room.Map, out var info), $"{def.Key}'s room map {room.Map} is not one the client renders");
                var filed = Locked(world, () => world.SpawnsForTest.PointKeysForTest(room.Map));
                if (room.X >= info.Xs + 2 || room.Y >= info.Ys + 2) Assert.DoesNotContain(def.Key, filed);   // off its map
                else Assert.Contains(def.Key, filed);
            }
        }
        Assert.True(bosses > 0, "no point on map 4 has rooms any more; this fact pins nothing");
        Assert.Equal(new[] { "gimyi", "nine_tailed_fox_buya", "nine_tailed_fox_buya2", "nine_tailed_fox_buya3" },
                     onChamber.OrderBy(k => k, StringComparer.Ordinal));
    }

    /// <summary>Caleb's rule for a room off its map (2026-10-21): a room with no tile of its map within two of it
    /// is left out of the filing and the pick, and only such a room. <c>monkey_avenger</c>'s rooms are 4163:6:5,
    /// 4164:24:32 and 4167:18:22; the second is RTK's warp, and map 4164 is 20x30, so <c>FreeSpawnTile</c>
    /// found no tile of it within reach and built the boss at (24,32), outside the map, where nobody could kill
    /// it. <c>divine_sheep</c>'s 3472:11:8 and <c>sheep_avenger</c>'s 4472:10:8 are one column outside their
    /// 10-wide maps and keep their rooms: a tile of the map is within two, and the boss is built there.
    ///
    /// <para>The real roster files <c>monkey_avenger</c> under 4163 and 4167 only, and the two sheep under
    /// their 10-wide maps. Then a synthetic point keyed <c>monkey_avenger</c>, with a one-beat window, is
    /// built sixty times for a player on 4163, and never in the off-map room: drawn uniformly over all three
    /// rooms, as before the rule, sixty builds all miss it with probability (2/3)^60, about 3e-11.</para>
    ///
    /// <para>Falsified by filing every room's map again (<c>UsableRoomMaps</c> without <c>UsableRoom</c>): red,
    /// filed under 4164. By picking over every room again (<c>PickRoom</c> without <c>UsableRoom</c>): red,
    /// a build in the off-map room. By skipping every room outside its map, not only those with no tile
    /// within two (<c>OffItsMap</c> without the reach): red, the sheep lose their rooms.</para></summary>
    [Fact]
    public void ARoomWithNoTileOfItsMapWithinTwoIsLeftOutOfTheFilingAndThePick()
    {
        var rule = RoomRule("monkey_avenger", MonkeyFlow3, MonkeyDance3, MonkeyWasteland3);
        var offMap = rule.Rooms.Single(r => r.Map == MonkeyDance3);
        var dance = Content.Maps[MonkeyDance3];
        Assert.True(offMap.X >= dance.Xs + 2, $"the fact leans on {MonkeyDance3}:{offMap.X}:{offMap.Y} lying more than two tiles off its {dance.Xs}x{dance.Ys} map");
        foreach (var (key, map) in new[] { ("divine_sheep", WoolenSqueeze2), ("sheep_avenger", WoolenSqueeze3) })
        {
            var room = Content.MobSpawnRules[key].Rooms.Single(r => r.Map == map);
            var info = Content.Maps[map];
            Assert.True(room.X >= info.Xs && room.X < info.Xs + 2 && room.Y < info.Ys,
                        $"the fact leans on {key}'s {map}:{room.X}:{room.Y} lying just outside its {info.Xs}x{info.Ys} map");
        }
        var world = new World();
        Assert.Equal(new[] { MonkeyFlow3, MonkeyWasteland3 },
                     new[] { MonkeyFlow3, MonkeyDance3, MonkeyWasteland3 }
                         .Where(m => Locked(world, () => world.SpawnsForTest.PointKeysForTest(m)).Contains("monkey_avenger")));
        Assert.Contains("divine_sheep", Locked(world, () => world.SpawnsForTest.PointKeysForTest(WoolenSqueeze2)));
        Assert.Contains("sheep_avenger", Locked(world, () => world.SpawnsForTest.PointKeysForTest(WoolenSqueeze3)));

        var def = Creature(990_208, "monkey_avenger");
        world.UnderWorldLockForTest(() => world.SpawnsForTest.AddPointForTest(OffMapRowMap, def, 3, 3, respawnEvery: 1));
        var watcher = Seat(world, "OffMapWatcher", MonkeyFlow3, (1, 1));
        world.EnterMap(watcher, MonkeyFlow3);
        for (int build = 1; build <= 60; build++)
        {
            var (boss, room) = Assert.Single(InTheirRooms(world, watcher, rule, def));
            Assert.True(room.Map != MonkeyDance3, $"build {build} put the boss in the off-map room, at ({boss.X},{boss.Y}) on map {room.Map}");
            AssertInTheRoom(boss, room);
            Assert.True(world.DespawnMob(room.Map, boss));
            world.TickOnceForTest();   // the one-beat window: rebuilt on this beat, for the player on 4163
        }
    }

    /// <summary>Every tile of <paramref name="map"/> a player can walk to from a tile a warp lands on: the
    /// review's terrain check (#357, its probe P3), a four-way flood fill over <see cref="MapData.BlockedMove"/>,
    /// which is the player walk's own test (directional object walls included), indexed [x, y].</summary>
    private static bool[,] WalkableFromItsLandings(ushort map, MapInfo info, MapData terrain)
    {
        var landings = Content.Warps.Values.Where(w => w.m == map).Select(w => ((int)w.x, (int)w.y)).Distinct().ToList();
        Assert.True(landings.Count > 0, $"no warp lands on map {map}; the fact has nowhere to walk from");
        var reached = new bool[info.Xs, info.Ys];
        var queue = new Queue<(int X, int Y)>();
        foreach (var (x, y) in landings)
            if (x < info.Xs && y < info.Ys && !reached[x, y]) { reached[x, y] = true; queue.Enqueue((x, y)); }
        int[] dx = { 0, 1, 0, -1 }, dy = { -1, 0, 1, 0 };   // heading 0=N 1=E 2=S 3=W, BlockedMove's order
        while (queue.Count > 0)
        {
            var (cx, cy) = queue.Dequeue();
            for (int d = 0; d < 4; d++)
            {
                int nx = cx + dx[d], ny = cy + dy[d];
                if (nx < 0 || ny < 0 || nx >= info.Xs || ny >= info.Ys || reached[nx, ny]) continue;
                if (terrain.BlockedMove(nx, ny, d)) continue;
                reached[nx, ny] = true;
                queue.Enqueue((nx, ny));
            }
        }
        return reached;
    }

    /// <summary><c>spirit_dragon</c>'s room on Jeweled Dragon 3 (4258, 18x36) is a tile a player can walk to,
    /// and the boss is built on it. RTK's script puts that room at 4258:7:13 (<c>mythic_dragon.lua</c>,
    /// <c>spirit_dragon</c>'s <c>on_spawn</c>). On the 4.95 map that tile is open ground walled in on all four
    /// sides, so the boss stood where nobody could walk to it or swing at it (#357's review, F1). The row names the
    /// corridor tile two east instead, 4258:9:13, on Caleb's word (2026-10-22): a deliberate deviation for the
    /// 4.95 terrain, noted beside the row in <c>MobSpawnRules.csv</c>.
    ///
    /// <para>The terrain half is the review's check (<see cref="WalkableFromItsLandings"/>). The room tile is
    /// reached, and so is every open tile beside it: that is where <c>FreeSpawnTile</c> puts the boss when a
    /// creature already stands on the room's own tile. RTK's tile is not reached, which is the reason for the
    /// deviation; if a change to the map ever opens it, this goes red so the deviation can be reconsidered.</para>
    ///
    /// <para>The build half: a synthetic point keyed <c>spirit_dragon</c>, with a one-beat window, built sixty
    /// times for a player on 4258. The map's own creatures are put out of the way first
    /// (<c>ForgetMapForTest</c> drops 4258's batch group and the real Spirit dragon's point), so a build there
    /// finds its room tile free, and every build on 4258 stands on the room tile itself. The pick is uniform over
    /// three rooms, so sixty builds all miss 4258 with probability (2/3)^60, about 3e-11.</para>
    ///
    /// <para>Falsified by putting RTK's 4258:7:13 back in the row: red, the room is not reached.</para></summary>
    [Fact]
    public void TheSpiritDragonsJeweledDragonRoomIsOnTheCorridorAPlayerWalks()
    {
        var rule = RoomRule("spirit_dragon", DragonsGold3, JeweledDragon3, DragonDeath3);
        var room = rule.Rooms.Single(r => r.Map == JeweledDragon3);
        var info = Content.Maps[JeweledDragon3];
        var terrain = MapData.For(JeweledDragon3, info.Xs, info.Ys);
        Assert.NotNull(terrain);
        var reached = WalkableFromItsLandings(JeweledDragon3, info, terrain);

        Assert.True(reached[room.X, room.Y],
                    $"spirit_dragon's room {JeweledDragon3}:{room.X}:{room.Y} is not reached from the map's warp landings: no player can walk to the boss");
        for (int x = room.X - 1; x <= room.X + 1; x++)
            for (int y = room.Y - 1; y <= room.Y + 1; y++)
                if (x >= 0 && y >= 0 && x < info.Xs && y < info.Ys && !terrain.Solid(x, y))
                    Assert.True(reached[x, y], $"({x},{y}), an open tile beside the room, is not reached from the map's warp landings");
        Assert.False(reached[7, 13], $"RTK's room {JeweledDragon3}:7:13 is reached on this map now; the row's deviation from RTK can be reconsidered");

        var world = new World();
        world.UnderWorldLockForTest(() => world.SpawnsForTest.ForgetMapForTest(JeweledDragon3));
        var def = Creature(990_209, "spirit_dragon");
        world.UnderWorldLockForTest(() => world.SpawnsForTest.AddPointForTest(DragonRowMap, def, 3, 3, respawnEvery: 1));
        var watcher = Seat(world, "JeweledWatcher", JeweledDragon3, (9, 2));   // a tile a warp lands on
        world.EnterMap(watcher, JeweledDragon3);
        int onJeweled = 0;
        for (int build = 1; build <= 60; build++)
        {
            var (boss, at) = Assert.Single(InTheirRooms(world, watcher, rule, def));
            if (at.Map == JeweledDragon3)
            {
                onJeweled++;
                Assert.True(boss.X == room.X && boss.Y == room.Y,
                            $"build {build} put the boss at ({boss.X},{boss.Y}) on {JeweledDragon3}, not on its room tile ({room.X},{room.Y})");
            }
            else AssertInTheRoom(boss, at);
            Assert.True(world.DespawnMob(at.Map, boss));
            world.TickOnceForTest();   // the one-beat window: rebuilt on this beat, for the player on 4258
        }
        Assert.True(onJeweled > 0, $"sixty builds and none on {JeweledDragon3}; the fact pinned nothing there");
    }

    /// <summary>A room boss whose build throws on a map entry is logged under the map the player entered, with
    /// its row's map beside it. The fault is recorded under the row's map, which is what <c>Materialize</c> is
    /// asked about and what the entry's stack record is keyed on; for the sixty map-4 bosses that is map 4,
    /// which nobody enters, and the line named only that (#357's review, F2).
    ///
    /// <para>A synthetic point on a row map nobody enters, keyed <c>divine_ox</c> (rooms on 3175 and 3178),
    /// whose creature <c>BuildMob</c> refuses: a negative move time, as <see cref="SpawnRowGuardTests"/>' bad
    /// group member. The first entry to 3175 writes the fault with its stack, under 3175. The first entry to
    /// 3178 tries the point again (one that threw is neither built nor pending) and names the fault without a
    /// stack, under 3178. Both lines name the row's map too, and both are written with the lock released.</para>
    ///
    /// <para>Falsified by naming the recorded map alone again (<c>World.EntryMapOf</c> returning the row's map,
    /// the old line): red, the stack line says "on map 60210".</para></summary>
    [Fact]
    public void ARoomBossWhoseBuildThrowsOnEntryIsLoggedUnderTheMapEntered()
    {
        RoomRule("divine_ox", BullsSong2, OxPath2);
        var world = new World();
        var broken = Creature(990_210, "divine_ox", moveTime: -1);   // BuildMob throws for it
        world.UnderWorldLockForTest(() => world.SpawnsForTest.AddPointForTest(BrokenRowMap, broken, 3, 3, respawnEvery: 1));
        var first = Seat(world, "BrokenRoomFirst", BullsSong2, (1, 1));
        var second = Seat(world, "BrokenRoomSecond", OxPath2, (1, 1));

        IReadOnlyList<LogLineSink.Entry> lines;
        using (var sink = LogLineSink.Acquire(probe: () => world.HoldsWorldLock))
        {
            world.EnterMap(first, BullsSong2);
            world.EnterMap(second, OxPath2);
            lines = sink.Lines;
        }

        Assert.Empty(MobsOf(world, first, BullsSong2, broken));
        Assert.Empty(MobsOf(world, second, OxPath2, broken));
        var ours = lines.Where(e => e.Line.Contains($"(creature id {broken.Id})")).ToList();
        Assert.Equal(2, ours.Count);   // once per entry
        Assert.All(ours, e => Assert.Equal(LogLevel.Error, e.Level));
        Assert.All(ours, e => Assert.False(e.Probe, $"written with World._lock held: {e.Line}"));

        // Asserted with the whole first line in the message: xUnit's own Contains failure cuts the line short of
        // the map it names, which is the one thing this fact is about.
        string row = $"(a room boss whose row names map {BrokenRowMap})";
        string stack = $"map entry: a spawn point threw — 'divine_ox' (creature id {broken.Id}) on map {BullsSong2} {row} is skipped";
        Assert.True(ours[0].Line.Contains(stack), $"the first entry's line should carry \"{stack}\"; it reads: {ours[0].Line.Split('\n')[0]}");
        Assert.Contains(nameof(ArgumentOutOfRangeException), ours[0].Line);
        Assert.Contains("\n      ", ours[0].Line);   // Log.Detail's continuation: the stack is on it
        string repeat = $"a spawn point 'divine_ox' (creature id {broken.Id}) on map {OxPath2} {row} {nameof(ArgumentOutOfRangeException)} x1";
        Assert.True(ours[1].Line.Contains("map entry: spawn rows still throwing") && ours[1].Line.Contains(repeat),
                    $"the second entry's line should name \"{repeat}\"; it reads: {ours[1].Line}");
        Assert.DoesNotContain("\n", ours[1].Line);   // named, with no stack
    }

    /// <summary>The pick draws only among the rooms the filing keeps: one test, <c>SpawnDirector.UsableRoom</c>,
    /// for both (#357's review, F3). A borrowed rule, <c>divine_pig</c>'s two rooms on 3189 and 3187, with two
    /// rooms added: one on a map the client cannot render (60211, no <c>Maps.csv</c> row) and one off its map
    /// (3189 at a column ten past its edge). The filing keeps the two real rooms' maps, and sixty draws each land on
    /// one of the two real rooms. The pick used to skip only the room off its map, so it drew the unrenderable
    /// room one time in three, and <c>Materialize</c> then dropped it and built the boss at its row's tile on its
    /// row's map, which the point is not filed under; sixty draws all miss it with probability (2/3)^60, about
    /// 3e-11. A rule with no usable room at all has nothing to be filed under and no pick: its point stays on its
    /// row's map and is built there.
    ///
    /// <para>No rule has a room on an unrenderable map today (<c>ContentSmokeTests</c> requires every room's map
    /// to render), so such a rule cannot reach <c>Materialize</c> through <see cref="Content"/>. The fact hands
    /// the rooms to the filing's and the pick's own halves instead (<c>UsableRoomMaps</c>,
    /// <c>PickRoom</c>).</para>
    ///
    /// <para>Falsified by the pick's old test (<c>PickRoom</c> asking <c>!OffItsMap</c> where it asks
    /// <c>UsableRoom</c>): red at the first draw that lands on 60211 ("draw 3 picked (60211, 3, 3)").</para></summary>
    [Fact]
    public void ThePickSkipsEveryRoomTheFilingSkips()
    {
        var rule = RoomRule("divine_pig", BoarRise2, CloveHill2);
        Assert.False(Content.Maps.ContainsKey(Unrenderable), $"map {Unrenderable} has a Maps.csv row now; the fact needs a map the client cannot render");
        var boarRise = Content.Maps[BoarRise2];
        (ushort Map, ushort X, ushort Y) unrenderable = (Unrenderable, 3, 3);
        (ushort Map, ushort X, ushort Y) offItsMap = (BoarRise2, (ushort)(boarRise.Xs + 10), 3);
        var rooms = rule.Rooms.Append(unrenderable).Append(offItsMap).ToArray();

        var filed = World.SpawnDirector.UsableRoomMaps(rooms);
        Assert.Equal(new[] { BoarRise2, CloveHill2 }, filed);
        for (int draw = 1; draw <= 60; draw++)
        {
            var pick = World.SpawnDirector.PickRoom(rooms);
            Assert.True(pick is { } room && Array.IndexOf(rule.Rooms, room) >= 0 && Array.IndexOf(filed, room.Map) >= 0,
                        $"draw {draw} picked {pick}, a room the filing leaves out");
        }

        var noneUsable = new[] { unrenderable, offItsMap };
        Assert.Empty(World.SpawnDirector.UsableRoomMaps(noneUsable));
        var none = World.SpawnDirector.PickRoom(noneUsable);
        Assert.True(none is null, $"a rule with no usable room picked {none}");
    }

    /// <summary>Gim Yi's Den (1308) is the one room map with a batch group of the same creature
    /// (<c>AreaSpawns.csv</c>: one <c>gimyi</c>, a 300 s clock, anywhere on the map). A map's first entry runs
    /// its groups before its points, so a <c>gimyi</c> point filed under 1308 added a second Gim Yi to the
    /// group's on every first visit. Caleb's word (2026-10-21): one Gim Yi in its den, as before #333. So the
    /// point stays filed under map 4, its row's map; the Den's first entry finds the group's one Gim Yi and no
    /// other.
    ///
    /// <para>Falsified by dropping the batch-group test from <c>AddSpawn</c>: red, <c>gimyi</c> is filed under
    /// 1308, not map 4, and the first entry finds two.</para></summary>
    [Fact]
    public void GimYisDenKeepsItsOneGimYiAndItsPointStaysOnMapFour()
    {
        var rule = RoomRule("gimyi", GimYisDen);
        var def = Content.Mobs.Single(m => m.Key == "gimyi");
        var group = Assert.Single(Content.AreaSpawns, a => a.MobId == def.Id);
        Assert.True(group.Map == GimYisDen && group.Count == 1 && group.Timer > 0, "gimyi's batch group changed under the fact");
        var world = new World();
        Assert.Contains(def.Key, Locked(world, () => world.SpawnsForTest.PointKeysForTest(NpcChamber)));
        Assert.DoesNotContain(def.Key, Locked(world, () => world.SpawnsForTest.PointKeysForTest(GimYisDen)));

        var visitor = Seat(world, "DenVisitor", GimYisDen, (9, 22));   // the tile the Den's warps land on
        world.EnterMap(visitor, GimYisDen);

        Assert.Single(InTheirRooms(world, visitor, rule, def));   // the group's, anywhere on the map
    }

    /// <summary>The two gates <c>Materialize</c> reads under the map it is asked about, not the map the
    /// creature lives on: the death cooldown (the registry is read under that map, and <c>RecordDeath</c>
    /// stamps the map the creature died on) and a population cap with no <c>CapMaps</c> (counted on that map).
    /// For a boss filed under its rooms that map is its row's (map 4), where it never stands, so either gate
    /// on a rule with rooms would never bite. That was so before #333 and stays so: the boss is still asked
    /// about under its row's map. No rule with rooms carries either today; this pins that, so a row that adds
    /// one is told why it will not work rather than finding out on the live server.
    ///
    /// <para>Falsified by giving a rule with rooms a <c>DeathCooldownSec</c> in <c>MobSpawnRules.csv</c>:
    /// red, naming it.</para></summary>
    [Fact]
    public void NoRuleWithRoomsCarriesAGateReadUnderItsRowsMap()
    {
        var gated = Content.MobSpawnRules.Values
            .Where(r => r.Rooms.Length > 0 && (r.DeathCooldownSec > 0 || (r.MaxAlive > 0 && r.CapMaps.Length == 0)))
            .Select(r => r.MobKey).ToList();
        Assert.True(gated.Count == 0,
                    $"a rule with rooms carries a death cooldown or an uncapped MaxAlive, which Materialize reads under its row's map: {string.Join(", ", gated)}");
    }

    /// <summary>A map's entry warms the terrain of its bosses' other rooms' maps BEFORE it takes
    /// <c>World._lock</c>, so a boss built on one of them by that entry is placed from a warm cache rather
    /// than by a disk read, a cell decode and a SQLite query under the lock (<c>docs/common/Locking.md</c>,
    /// "How long a hold may be"). <c>divine_dog</c>'s rooms are on 3193 and 3195; 3195 carries no NPC (a
    /// World's construction would load it) and nothing else in the suite touches it, so it is cold until
    /// this fact's entry.
    ///
    /// <para>The fact holds the world lock itself and starts the entry on another thread, which warms and
    /// then waits for the lock. While the lock is held, 3195 must come into the cache: only the warm can
    /// have put it there, since the placement runs under the lock. Falsified by deleting the
    /// <c>SpawnDirector.PrewarmRoomMaps</c> call in <c>World.EnterMap</c>: red after the five-second wait,
    /// 3195 still cold.</para></summary>
    [Fact]
    public void AnEntryWarmsItsBossesOtherRoomsMapsBeforeItTakesTheLock()
    {
        RoomRule("divine_dog", DogRoomA, DogRoomB);
        Assert.DoesNotContain(Content.Npcs, n => n.Map == DogRoomB);
        var world = new World();
        Assert.False(MapData.IsLoadedForTest(DogRoomB), $"map {DogRoomB} should be cold to start with; something else loaded it");

        var entrant = Seat(world, "WarmEntrant", DogRoomA, (1, 1));
        var entering = new Thread(() => world.EnterMap(entrant, DogRoomA)) { IsBackground = true, Name = "warm-entrant" };
        bool warmWhileHeld = false;
        world.UnderWorldLockForTest(() =>
        {
            entering.Start();
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (!MapData.IsLoadedForTest(DogRoomB) && waited.ElapsedMilliseconds < 5000) Thread.Sleep(5);
            warmWhileHeld = MapData.IsLoadedForTest(DogRoomB);
        });
        Assert.True(entering.Join(TimeSpan.FromSeconds(30)), "the entry did not finish once the lock was released");
        Assert.True(warmWhileHeld, $"map {DogRoomB} was not warmed before the entry took the world lock");
    }
}
