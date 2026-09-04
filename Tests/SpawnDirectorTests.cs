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

    private const ushort PointMap = 60040, LockMap = 60041, BossMap = 60043;
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
    /// tile. Does not test the watcher's tile: on map entry the room fills BEFORE the newcomer joins the
    /// map's player list, which is the pre-existing order this PR keeps (#122, not a change here).</summary>
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
/// rebuild tears down every creature on every map; that puts it in its own xUnit collection, so its
/// <c>LoadContent()</c> can run in parallel with the shared world fixture's — the established pattern for
/// tests that build a World of their own. Falsified by deleting the <c>_spawnDirector.Clear()</c> line in
/// <c>RebuildPopulation</c>: red on the test point's count (still 1).</summary>
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

        var (mobs, npcs, maps) = world.RebuildPopulation();

        Assert.True(mobs >= 1, "the torn-down count should include the test point's creature");
        Assert.True(npcs > 0, "the NPCs are re-placed from Content");
        Assert.Equal(0, maps);                                 // nobody is on any map of this world
        Assert.Equal(0, Locked(world, () => spawns.PointCountForTest(RebuildMap)));   // the test registrations are gone…
        Assert.Equal(0, Locked(world, () => spawns.GroupCountForTest(RebuildMap)));
        Assert.False(Locked(world, () => spawns.IsMaterializedForTest(RebuildMap)));
        Assert.Equal(0, world.MobCountForTest(RebuildMap));
        Assert.Equal(kugnaeBefore, Locked(world, () => spawns.PointCountForTest(Kugnae)));   // …and Content's roster is back as it was
    }
}
