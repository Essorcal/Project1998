using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The spawn row guards inside the tick's phases (1) and (1.1): a spawn point whose materialisation throws,
/// or a group member whose fill throws, costs only ITSELF. The other points and members still spawn in the
/// same beat, on the same map and on the maps after it, and the fault is written after <c>World._lock</c> is
/// released, through the phase-fault throttle, keyed by the row's map and its creature's content id.
/// Before the guards (#289's shape) the throw ended the whole phase and everything after it waited until
/// the content was fixed. <see cref="TickPhaseGuardTests"/> has the three-point fact.
///
/// <para>The map-entry path is NOT guarded (<c>SpawnDirector.EnsureMaterialized</c>, from
/// <c>World.EnterMap</c>), and the last two facts here pin what a throw there does today, so that the
/// follow-up which changes it has to change them on purpose: the throw leaves <c>World.EnterMap</c>, the
/// entering player is on no map's roster, and the rows after the bad one are lost.</para>
///
/// <para>The throws are real lines reached with synthetic content that no loader produces. A point whose
/// creature has a null key makes <c>Materialize</c>'s <c>MobSpawnRules.TryGetValue(d.Key, ...)</c> throw
/// <see cref="ArgumentNullException"/>. A group member whose creature has a negative <c>MoveTime</c> makes
/// <c>BuildMob</c>'s <c>Random.Shared.Next(d.MoveTime)</c> throw <see cref="ArgumentOutOfRangeException"/>
/// before anything is added to the map (<c>Content.Csv</c> floors a real row's move time at a positive
/// default).</para>
///
/// <para>Hygiene as in <see cref="SpawnDirectorTests"/>: the fixture's <c>World</c> is shared by the
/// <c>world</c> collection, so every seated player is removed and every registration undone in
/// <c>finally</c>. The fault throttle is per world, so each fact uses creature ids no other fact uses: a
/// fact that reused another's (phase, map, creature, exception type) inside the throttle's quiet window
/// would see its first throw counted rather than stacked.</para>
/// </summary>
[Collection("world")]
public class SpawnRowGuardTests
{
    private readonly SessionFixture _fx;

    public SpawnRowGuardTests(SessionFixture fx) => _fx = fx;

    // Content-free maps for the points (no registry row, no terrain, no spawns); map 485 for the groups,
    // which need a real box to roll in (SpawnDirectorTests' map, asserted empty below).
    private const ushort ThrottleMap = 60200, EntryPointMap = 60201, ObserverMap = 60202;
    private const ushort GroupMap = 485;

    private const string RowRepeatNeedle = "spawn rows still throwing";

    private World.SpawnDirector Spawns => _fx.World.SpawnsForTest;

    private static MobDef Creature(int id, string key, int moveTime = 1_000_000) =>
        new()
        {
            Id = id,
            Key = key,
            Name = "Test " + id,
            Look = 1,
            Color = 0,
            Hp = 100,
            Exp = 0,
            Level = 1,
            MoveTime = moveTime,
            Stationary = true,
        };

    private List<Mob> MobsOf(Session viewer, ushort map, MobDef def) =>
        _fx.World.View(viewer, map).mobs.Where(m => m.DefId == def.Id).ToList();

    private T Locked<T>(Func<T> read)
    {
        T value = default!;
        _fx.World.UnderWorldLockForTest(() => value = read());
        return value;
    }

    private static void AssertGroupMapIsEmptyContent()
    {
        Assert.True(Content.Maps.TryGetValue(GroupMap, out var info) && info.Xs == 8 && info.Ys == 8);
        Assert.Empty(Content.SpawnsFor(GroupMap));
        Assert.DoesNotContain(Content.AreaSpawns, a => a.Map == GroupMap);
        Assert.DoesNotContain(Content.Npcs, n => n.Map == GroupMap);
    }

    /// <summary>Phase (1.1): one due group of three members on a watched map, the middle one bad. On the
    /// one sampled beat of <c>BatchSweepTicks</c>, the member before the bad one and the member after it are
    /// both topped up to their caps, the bad one places nothing, and its fault is written once, with its
    /// stack, after the lock — as a row fault naming the creature and the map. The group's clock is NOT
    /// stamped, because the batch did not finish: that is what the unguarded throw did as well, and the
    /// guard keeps it (see <c>SpawnDirector.RefillGroups</c>).
    ///
    /// <para>Falsified by removing the member guard in <c>RefillGroups</c> (the bare <c>FillMember</c>
    /// call, the #289 shape): red, the third member is empty (#289's phase guard ends the phase at the
    /// throw). Falsified by stamping the clock whatever happened (<c>if (!threw)</c> removed): red on the
    /// clock.</para></summary>
    [Fact]
    public void AGroupMemberWhoseFillThrowsCostsOnlyThatMember()
    {
        AssertGroupMapIsEmptyContent();
        var before = Creature(990_111, "test_row_before");
        var broken = Creature(990_112, "test_row_broken", moveTime: -1);   // BuildMob throws for it
        var after = Creature(990_113, "test_row_after");

        var (watcher, _) = _fx.Player("RowGroupWatcher", GroupMap, x: 1, y: 1);   // before the group: entry fills nothing
        try
        {
            _fx.World.UnderWorldLockForTest(() =>
                Spawns.AddGroupForTest(GroupMap, timerSec: 300, (before, 2), (broken, 1), (after, 2)));

            IReadOnlyList<LogLineSink.Entry> lines;
            using (var sink = LogLineSink.Acquire(probe: () => _fx.World.HoldsWorldLock))
            {
                for (int i = 0; i < World.BatchSweepTicksForTest; i++) _fx.World.TickOnceForTest();   // one sampled beat
                lines = sink.Lines;
            }

            Assert.Equal(2, MobsOf(watcher, GroupMap, before).Count);
            Assert.Equal(2, MobsOf(watcher, GroupMap, after).Count);
            Assert.Empty(MobsOf(watcher, GroupMap, broken));
            Assert.Equal(0, Locked(() => Spawns.GroupClockForTest(GroupMap)));   // the batch did not finish

            var ours = lines.Where(e => e.Line.Contains($"creature id {broken.Id}")).ToList();
            var stack = Assert.Single(ours);
            Assert.Contains($"world tick phase (1.1) refills: a spawn group member threw — 'test_row_broken' (creature id {broken.Id}) on map {GroupMap}", stack.Line);
            Assert.Contains(nameof(ArgumentOutOfRangeException), stack.Line);
            Assert.Contains("\n      ", stack.Line);   // Log.Detail's continuation: the stack is on it
            Assert.Equal(LogLevel.Error, stack.Level);
            Assert.False(stack.Probe, $"written with World._lock held: {stack.Line}");
        }
        finally
        {
            _fx.World.LeaveMap(watcher, GroupMap);
            _fx.World.UnderWorldLockForTest(() => Spawns.ForgetMapForTest(GroupMap));
        }
    }

    private const int ThrottleBeats = 5;

    /// <summary>Two bad points of one creature, due on every beat (a point that throws keeps its clock), and
    /// one good point after them. Over <see cref="ThrottleBeats"/> beats the log gets ONE stack and then one
    /// count line a beat: on the first beat the second point's throw is counted ("x1") because the first
    /// one's was stacked; on every later beat both are counted ("x2"). Every line is written with the lock
    /// released, and the good point materialised on the first beat regardless.
    ///
    /// <para>Falsified by making <c>LogPhaseFaults</c> write the stack for every fault (its <c>Admit</c>
    /// test OR'd with <c>true</c>): red, "Assert.Single() Failure: The collection contained 10 matching
    /// items".</para></summary>
    [Fact]
    public void ABadRowThatThrowsOnEveryBeatLogsOneStackThenCountLines()
    {
        var broken = Creature(990_121, null!);   // Materialize's first line refuses it
        var good = Creature(990_122, "test_row_throttle_good");

        var (watcher, _) = _fx.Player("RowThrottleWatcher", ThrottleMap, x: 5, y: 10);   // before the points
        try
        {
            _fx.World.UnderWorldLockForTest(() =>
            {
                Spawns.AddPointForTest(ThrottleMap, broken, 3, 5, respawnEvery: 1, dueAt: 1);
                Spawns.AddPointForTest(ThrottleMap, broken, 4, 5, respawnEvery: 1, dueAt: 1);
                Spawns.AddPointForTest(ThrottleMap, good, 5, 5, respawnEvery: 1, dueAt: 1);
            });

            IReadOnlyList<LogLineSink.Entry> lines;
            using (var sink = LogLineSink.Acquire(probe: () => _fx.World.HoldsWorldLock))
            {
                for (int beat = 0; beat < ThrottleBeats; beat++) _fx.World.TickOnceForTest();
                lines = sink.Lines;
            }

            Assert.Single(MobsOf(watcher, ThrottleMap, good));
            Assert.Empty(MobsOf(watcher, ThrottleMap, broken));

            string row = $"(creature id {broken.Id}) on map {ThrottleMap}";
            var ours = lines.Where(e => e.Line.Contains(row)).ToList();
            Assert.All(ours, e => Assert.False(e.Probe, $"written with World._lock held: {e.Line}"));

            var stack = Assert.Single(ours, e => e.Line.Contains("a spawn point threw"));
            Assert.Contains(nameof(ArgumentNullException), stack.Line);
            Assert.Contains("\n      ", stack.Line);

            var counts = ours.Where(e => e.Line.Contains(RowRepeatNeedle)).Select(e => e.Line).ToList();
            Assert.Equal(ThrottleBeats, counts.Count);
            Assert.Contains($"{row} {nameof(ArgumentNullException)} x1", counts[0]);
            foreach (var later in counts.Skip(1)) Assert.Contains($"{row} {nameof(ArgumentNullException)} x2", later);
        }
        finally
        {
            _fx.World.LeaveMap(watcher, ThrottleMap);
            _fx.World.UnderWorldLockForTest(() => Spawns.ForgetMapForTest(ThrottleMap));
        }
    }

    // =====================================================================================================
    // The map-entry path, unguarded: what a throw there does TODAY (item 4 of the spawn-row-guards packet).
    // =====================================================================================================

    /// <summary>A due group whose middle member throws, met on map ENTRY rather than on the tick. The throw
    /// leaves <c>World.EnterMap</c> (so a real entry's per-packet catch, or the login path's, is what logs
    /// it), after the member before the bad one was filled and before the one after it; the group's clock is
    /// not stamped; the entering player is on no map's roster; and because the group stays due, the NEXT
    /// entry throws the same way. While the content is broken, nobody can enter the map, and the tick never
    /// refills it either, since phase (1.1) skips a map with nobody on it.
    ///
    /// <para>A characterisation, not a guard: it pins that this slice left the entry path as it was. The
    /// follow-up that guards it will turn it red on purpose.</para></summary>
    [Fact]
    public void OnEntryABadGroupMemberFailsTheEntryAndEveryEntryAfterIt()
    {
        AssertGroupMapIsEmptyContent();
        var before = Creature(990_131, "test_entry_before");
        var broken = Creature(990_132, "test_entry_broken", moveTime: -1);
        var after = Creature(990_133, "test_entry_after");

        var (observer, _) = _fx.Player("EntryGroupObserver", ObserverMap, x: 5, y: 10);   // reads GroupMap from elsewhere
        try
        {
            _fx.World.UnderWorldLockForTest(() =>
                Spawns.AddGroupForTest(GroupMap, timerSec: 300, (before, 2), (broken, 1), (after, 2)));

            Assert.Throws<ArgumentOutOfRangeException>(() => _fx.Player("EntryGroupVictim1", GroupMap, x: 1, y: 1));

            Assert.Equal(2, MobsOf(observer, GroupMap, before).Count);   // filled before the throw
            Assert.Empty(MobsOf(observer, GroupMap, after));              // never reached
            Assert.Equal(0, Locked(() => Spawns.GroupClockForTest(GroupMap)));
            Assert.Empty(_fx.World.View(observer, GroupMap).peers);       // the victim is on no roster here

            Assert.Throws<ArgumentOutOfRangeException>(() => _fx.Player("EntryGroupVictim2", GroupMap, x: 1, y: 1));
            Assert.Empty(_fx.World.View(observer, GroupMap).peers);
            Assert.Empty(MobsOf(observer, GroupMap, after));
        }
        finally
        {
            _fx.World.LeaveMap(observer, ObserverMap);
            _fx.World.UnderWorldLockForTest(() => Spawns.ForgetMapForTest(GroupMap));
        }
    }

    /// <summary>Three points, the middle one bad, met on a map's FIRST entry. The first entry throws out of
    /// <c>World.EnterMap</c> with the first point materialised; the map is already marked materialised
    /// (the flag is set before the walk), so the second entry succeeds but never walks the points again;
    /// and the third point never appears, not on that entry and not on the beats after it, because a point
    /// that was never materialised has no respawn clock for phase (1) to find. It stays missing until an
    /// <c>@reload</c> rebuilds the rosters.
    ///
    /// <para>A characterisation, not a guard, like the fact above.</para></summary>
    [Fact]
    public void OnFirstEntryABadPointFailsTheEntryAndLosesThePointsAfterIt()
    {
        var before = Creature(990_141, "test_entry_point_before");
        var broken = Creature(990_142, null!);
        var after = Creature(990_143, "test_entry_point_after");

        _fx.World.UnderWorldLockForTest(() =>
        {
            Spawns.AddPointForTest(EntryPointMap, before, 3, 5, respawnEvery: 1);
            Spawns.AddPointForTest(EntryPointMap, broken, 5, 5, respawnEvery: 1);
            Spawns.AddPointForTest(EntryPointMap, after, 7, 5, respawnEvery: 1);
        });
        Session? watcher = null;
        try
        {
            Assert.Throws<ArgumentNullException>(() => _fx.Player("EntryPointVictim", EntryPointMap, x: 5, y: 10));
            Assert.True(Locked(() => Spawns.IsMaterializedForTest(EntryPointMap)));

            (watcher, _) = _fx.Player("EntryPointWatcher", EntryPointMap, x: 5, y: 10);   // succeeds now
            for (int beat = 0; beat < 3; beat++) _fx.World.TickOnceForTest();

            Assert.Single(MobsOf(watcher, EntryPointMap, before));
            Assert.Empty(MobsOf(watcher, EntryPointMap, broken));
            Assert.Empty(MobsOf(watcher, EntryPointMap, after));   // lost until a reload
            Assert.DoesNotContain(_fx.World.View(watcher, EntryPointMap).peers, p => p.Session.CharName == "EntryPointVictim");
        }
        finally
        {
            if (watcher is not null) _fx.World.LeaveMap(watcher, EntryPointMap);
            _fx.World.UnderWorldLockForTest(() => Spawns.ForgetMapForTest(EntryPointMap));
        }
    }
}
