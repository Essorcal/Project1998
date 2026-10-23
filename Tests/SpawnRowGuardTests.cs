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
/// <para>The map-entry path (<c>SpawnDirector.EnsureMaterialized</c> with a newcomer, from
/// <c>World.EnterMap</c>) has the same two guards, the #292 follow-up: the entry goes ahead, every other row is
/// placed, and the fault is written once per entry by the entering thread after the lock, never through the
/// tick's throttle, with its stack only when one is due. <c>@reload</c>'s re-materialise (the same method with
/// no newcomer) is still unguarded, and the last fact pins that, for the follow-up that changes it.</para>
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
/// would see its first throw counted rather than stacked. The map-entry path's record of which stacks it
/// has written is per world as well; <c>ForgetMapForTest</c> clears a map's, and the ids stay unique
/// anyway.</para>
/// </summary>
[Collection("world")]
public class SpawnRowGuardTests
{
    private readonly SessionFixture _fx;

    public SpawnRowGuardTests(SessionFixture fx) => _fx = fx;

    // Content-free maps for the points (no registry row, no terrain, no spawns); map 485 for the groups,
    // which need a real box to roll in (SpawnDirectorTests' map, asserted empty below).
    private const ushort ThrottleMap = 60218, EntryPointMap = 60219, ObserverMap = 60220, ReloadMap = 60221;
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
            // The whole sentence, past the map: a group member's tick line names its map alone, never the room-boss
            // suffix of a boss filed under its rooms' maps (the tick-side twin of #367 review's F1).
            string memberStack = $"world tick phase (1.1) refills: a spawn group member threw — 'test_row_broken' (creature id {broken.Id}) on map {GroupMap} is skipped this beat, the rest of the phase continues (repeats are counted, not re-logged)";
            Assert.True(stack.Line.Contains(memberStack), $"the beat's line should carry \"{memberStack}\"; it reads: {stack.Line.Split('\n')[0]}");
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
            string pointStack = $"{row} is skipped this beat, the rest of the phase continues (repeats are counted, not re-logged)";   // an ordinary point's line names its map alone
            Assert.True(stack.Line.Contains(pointStack), $"the stack line should carry \"{pointStack}\"; it reads: {stack.Line.Split('\n')[0]}");
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
    // The map-entry path: the same two guards, written by the entering thread (the #292 follow-up).
    // =====================================================================================================

    private const int GroupEntrants = 3;

    /// <summary>A due group whose middle member throws, met on map ENTRY by three players in turn. Every
    /// entry goes ahead: the first fills the member before the bad one AND the member after it to their caps,
    /// the bad one places nothing, and all three players end up on the map's roster. The group's clock is not
    /// stamped, the tick path's rule, so the group stays due and each later entry meets the bad member again.
    /// The fault is written once per entry, at Error, by the entering thread with the lock released: the
    /// first entry writes it with its stack, and each entry after that names it on one line with no stack.
    /// That is the bound on a broken map entered again and again: one stack, then one short line an entry.
    ///
    /// <para>Falsified, Debug and Release (the w2-map-entry report): the guard off on the entry path
    /// (<c>guarded: false</c> on <c>EnsureMaterialized</c>'s <c>RefillGroups</c> call) is red, the first
    /// entry throws <see cref="ArgumentOutOfRangeException"/>; the stack written on every entry
    /// (<c>AdmitEntryStack</c> always true) is red on the second entry's line; the clock stamped whatever
    /// happened (<c>if (!threw)</c> dropped in <c>RefillGroups</c>) is red on the clock. The room-boss suffix written
    /// on every stack line (#367 review's R4, <c>World.EntryMapOf</c> bypassed) is red on the first entry's line.</para></summary>
    [Fact]
    public void OnEntryABadGroupMemberCostsOnlyThatMember()
    {
        AssertGroupMapIsEmptyContent();
        var before = Creature(990_131, "test_entry_before");
        var broken = Creature(990_132, "test_entry_broken", moveTime: -1);   // BuildMob throws for it
        var after = Creature(990_133, "test_entry_after");

        var (observer, _) = _fx.Player("EntryGroupObserver", ObserverMap, x: 5, y: 10);   // reads GroupMap from elsewhere
        var entrants = new List<Session>();
        try
        {
            _fx.World.UnderWorldLockForTest(() =>
                Spawns.AddGroupForTest(GroupMap, timerSec: 300, (before, 2), (broken, 1), (after, 2)));

            IReadOnlyList<LogLineSink.Entry> lines;
            using (var sink = LogLineSink.Acquire(probe: () => _fx.World.HoldsWorldLock))
            {
                for (int i = 0; i < GroupEntrants; i++)
                    entrants.Add(_fx.Player($"EntryGroupEntrant{i}", GroupMap, x: (ushort)(1 + i), y: 1).session);
                lines = sink.Lines;
            }

            Assert.Equal(2, MobsOf(observer, GroupMap, before).Count);
            Assert.Equal(2, MobsOf(observer, GroupMap, after).Count);   // reached, past the bad member
            Assert.Empty(MobsOf(observer, GroupMap, broken));
            Assert.Equal(0, Locked(() => Spawns.GroupClockForTest(GroupMap)));   // the batch did not finish
            Assert.Equal(entrants.Select(s => s.PlayerId).OrderBy(id => id),
                         _fx.World.View(observer, GroupMap).peers.Select(p => p.Id).OrderBy(id => id));

            var ours = lines.Where(e => e.Line.Contains($"creature id {broken.Id}")).ToList();
            Assert.Equal(GroupEntrants, ours.Count);   // once per entry
            Assert.All(ours, e => Assert.Equal(LogLevel.Error, e.Level));
            Assert.All(ours, e => Assert.False(e.Probe, $"written with World._lock held: {e.Line}"));

            // The whole sentence, past the map: a group member's line names its map alone (#367 review, F1). Printed
            // in full on failure, because xUnit's own Contains message cuts the line off before the map.
            var first = ours[0];
            string groupStack = $"map entry: a spawn group member threw — 'test_entry_broken' (creature id {broken.Id}) on map {GroupMap} is skipped, the entry and the map's other spawns go ahead (a repeat on a later entry is named, not re-logged)";
            Assert.True(first.Line.Contains(groupStack), $"the first entry's line should carry \"{groupStack}\"; it reads: {first.Line.Split('\n')[0]}");
            Assert.Contains(nameof(ArgumentOutOfRangeException), first.Line);
            Assert.Contains("\n      ", first.Line);   // Log.Detail's continuation: the stack is on it

            foreach (var later in ours.Skip(1))
            {
                Assert.Contains("map entry: spawn rows still throwing", later.Line);
                Assert.Contains($"a spawn group member 'test_entry_broken' (creature id {broken.Id}) on map {GroupMap} {nameof(ArgumentOutOfRangeException)} x1", later.Line);
                Assert.DoesNotContain("\n", later.Line);   // named, with no stack
            }
        }
        finally
        {
            foreach (var s in entrants) _fx.World.LeaveMap(s, GroupMap);
            _fx.World.LeaveMap(observer, ObserverMap);
            _fx.World.UnderWorldLockForTest(() => Spawns.ForgetMapForTest(GroupMap));
        }
    }

    /// <summary>Three points, the middle one bad, met on a map's FIRST entry. The entry goes ahead with the
    /// point before the bad one AND the point after it materialised on their tiles; the bad one places
    /// nothing, and is written once, at Error with its stack, by the entering thread with the lock released.
    /// The bad point keeps no clock, so nothing retries it: the beats after the entry write nothing more for
    /// it, and a second entry does not walk the points again. It costs itself until an <c>@reload</c>
    /// rebuilds the roster, where before the guard every point after it was lost with it.
    ///
    /// <para>Falsified, Debug and Release (the w2-map-entry report), by the point guard's catch filter made
    /// false on the entry path: red, the entry throws <see cref="ArgumentNullException"/>. By the room-boss suffix
    /// written on every stack line (#367 review's R4): red on the entry's line.</para></summary>
    [Fact]
    public void OnFirstEntryABadPointCostsOnlyThatPoint()
    {
        var before = Creature(990_141, "test_entry_point_before");
        var broken = Creature(990_142, null!);   // Materialize's first line refuses it
        var after = Creature(990_143, "test_entry_point_after");

        _fx.World.UnderWorldLockForTest(() =>
        {
            Spawns.AddPointForTest(EntryPointMap, before, 3, 5, respawnEvery: 1);
            Spawns.AddPointForTest(EntryPointMap, broken, 5, 5, respawnEvery: 1);
            Spawns.AddPointForTest(EntryPointMap, after, 7, 5, respawnEvery: 1);
        });
        Session? first = null, second = null;
        try
        {
            IReadOnlyList<LogLineSink.Entry> lines;
            using (var sink = LogLineSink.Acquire(probe: () => _fx.World.HoldsWorldLock))
            {
                (first, _) = _fx.Player("EntryPointFirst", EntryPointMap, x: 5, y: 10);
                for (int beat = 0; beat < 3; beat++) _fx.World.TickOnceForTest();
                (second, _) = _fx.Player("EntryPointSecond", EntryPointMap, x: 6, y: 10);
                lines = sink.Lines;
            }

            Assert.True(Locked(() => Spawns.IsMaterializedForTest(EntryPointMap)));
            var b = Assert.Single(MobsOf(first, EntryPointMap, before));
            Assert.Equal(((ushort)3, (ushort)5), (b.X, b.Y));
            var a = Assert.Single(MobsOf(first, EntryPointMap, after));   // the same entry, past the bad point
            Assert.Equal(((ushort)7, (ushort)5), (a.X, a.Y));
            Assert.Empty(MobsOf(first, EntryPointMap, broken));
            Assert.Contains(_fx.World.View(second, EntryPointMap).peers, p => p.Session == first);   // on the roster

            var ours = lines.Where(e => e.Line.Contains($"(creature id {broken.Id}) on map {EntryPointMap}")).ToList();
            var stack = Assert.Single(ours);   // the entry's; not the beats', not the second entry's
            // The whole sentence, past the map: a map's own point's line names its map alone (#367 review, F1).
            string pointStack = $"map entry: a spawn point threw — '' (creature id {broken.Id}) on map {EntryPointMap} is skipped, the entry and the map's other spawns go ahead (a repeat on a later entry is named, not re-logged)";
            Assert.True(stack.Line.Contains(pointStack), $"the entry's line should carry \"{pointStack}\"; it reads: {stack.Line.Split('\n')[0]}");
            Assert.Contains(nameof(ArgumentNullException), stack.Line);
            Assert.Contains("\n      ", stack.Line);
            Assert.Equal(LogLevel.Error, stack.Level);
            Assert.False(stack.Probe, $"written with World._lock held: {stack.Line}");
        }
        finally
        {
            if (first is not null) _fx.World.LeaveMap(first, EntryPointMap);
            if (second is not null) _fx.World.LeaveMap(second, EntryPointMap);
            _fx.World.UnderWorldLockForTest(() => Spawns.ForgetMapForTest(EntryPointMap));
        }
    }

    // =====================================================================================================
    // @reload's re-materialise: still unguarded (acceptance item 4 of the map-entry packet; a follow-up).
    // =====================================================================================================

    /// <summary>The call <c>@reload</c>'s re-materialise makes for each populated map
    /// (<c>World.RebuildPopulation</c>: <c>EnsureMaterialized(mapId)</c>, with no newcomer), on a roster
    /// whose middle point is bad. It throws, as it always has: the point before the bad one is materialised
    /// and the one after it is not. The same roster met by a map entry costs only the bad point
    /// (<see cref="OnFirstEntryABadPointCostsOnlyThatPoint"/>).
    ///
    /// <para>A characterisation, not a guard. The map-entry guard is in the same method, but this call keeps
    /// the unguarded walk on purpose. Its faults would have to be written by <c>RebuildPopulation</c> after it
    /// releases the lock (nothing is formatted under it, #109), which is a change to that method; and catching
    /// here would turn a failed <c>@reload</c> into one reported as done, a visible change of its own. Both
    /// belong to the follow-up, which will turn this red on purpose. Driven through the method rather than
    /// through <c>RebuildPopulation</c>, because the rebuild re-reads its rows from <see cref="Content"/>, so
    /// a test-registered bad row does not survive to the re-materialise.</para></summary>
    [Fact]
    public void OnReloadABadPointStillFailsTheRematerialise()
    {
        var before = Creature(990_151, "test_reload_point_before");
        var broken = Creature(990_152, null!);
        var after = Creature(990_153, "test_reload_point_after");

        var (observer, _) = _fx.Player("ReloadObserver", ObserverMap, x: 5, y: 10);   // reads ReloadMap from elsewhere
        try
        {
            _fx.World.UnderWorldLockForTest(() =>
            {
                Spawns.AddPointForTest(ReloadMap, before, 3, 5, respawnEvery: 1);
                Spawns.AddPointForTest(ReloadMap, broken, 5, 5, respawnEvery: 1);
                Spawns.AddPointForTest(ReloadMap, after, 7, 5, respawnEvery: 1);
            });

            Assert.Throws<ArgumentNullException>(() =>
                _fx.World.UnderWorldLockForTest(() => Spawns.EnsureMaterialized(ReloadMap)));

            Assert.Single(MobsOf(observer, ReloadMap, before));
            Assert.Empty(MobsOf(observer, ReloadMap, broken));
            Assert.Empty(MobsOf(observer, ReloadMap, after));   // never reached
        }
        finally
        {
            _fx.World.LeaveMap(observer, ObserverMap);
            _fx.World.UnderWorldLockForTest(() => Spawns.ForgetMapForTest(ReloadMap));
        }
    }
}
