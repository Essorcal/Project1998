using System.Text.RegularExpressions;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The two owners #37 section 4 pulled out of <c>World</c>: <c>World.Online</c> (the duplicate-login slot
/// table and the lookups by name, by entity id and across the whole world) and <c>World.AutoSave</c> (the
/// periodic sweep, the per-player isolation fence, the shutdown flush).
///
/// <para>These are the tests the extraction makes possible rather than the ones it needed. Before it, the
/// duplicate-login slot table was a private dictionary with no reader but <c>HandleArrival</c>, so the
/// compare-and-remove in <c>Unregister</c> could only be exercised by driving two whole logins down a socket;
/// and the sweep was a private method on the world reachable only from a thread that sleeps
/// <c>Session.AutoSaveMs</c> first. Both are now objects a test can hold. Nothing here reaches state the
/// production code does not reach the same way.</para>
///
/// <para>Hygiene: the fixture's <c>World</c> is shared by every class in the <c>world</c> collection and has
/// no teardown, so every test here leaves the maps it entered and drops the registry slots it claimed in a
/// <c>finally</c> — a leaked session would be flushed by the next test's sweep, and a leaked slot would sit
/// in the duplicate-login table for the rest of the run.</para>
/// </summary>
[Collection("world")]
public class OnlineRegistryAutoSaveTests
{
    private readonly SessionFixture _fx;

    public OnlineRegistryAutoSaveTests(SessionFixture fx) => _fx = fx;

    // Content-free maps: no Maps.csv row, so nothing spawns on them and nobody else is standing there. One
    // per test so no two tests share a Players list.
    private const ushort SlotMap = 60080, LookupMap = 60081, SnapshotMap = 60082,
                         SweepMap = 60083, FenceMap = 60084, LockMap = 60085;

    // =====================================================================================================
    // The registry.
    // =====================================================================================================

    /// <summary>The duplicate-login contract, both halves. <c>Register</c> hands the caller back whoever held
    /// the slot — that return value is the ONLY handle <c>HandleArrival</c> has on the session it has to kick
    /// — and <c>Unregister</c> is a compare-and-remove, so the session that was replaced cannot evict the
    /// session that replaced it when its own (later, slower) teardown finally runs.
    ///
    /// <para>That second half is the one with teeth: a stale teardown that DID evict would leave the account
    /// with no slot at all, and the next login for it would see <c>old == null</c> and skip the kick — two
    /// live sessions on one character, which is the exact failure the registry exists to prevent.</para>
    ///
    /// <para>Falsified by dropping the <c>ReferenceEquals(cur, s)</c> test from
    /// <c>OnlineRegistry.Unregister</c> (leaving a bare <c>_online.Remove(key)</c>): red on the assertion
    /// that the newcomer still holds the slot, "Assert.True() Failure / Expected: True / Actual:
    /// False".</para></summary>
    [Fact]
    public void ASecondLoginTakesTheSlotAndTheStaleSessionCannotEvictItsReplacement()
    {
        var (first, _) = _fx.Player("SlotFirst", SlotMap, 5, 5);
        var (second, _) = _fx.Player("SlotSecond", SlotMap, 6, 5);
        string key = CharacterStore.Key("SlotAccount");
        var online = _fx.World.Online;

        try
        {
            online.Register(key, first, out var beforeAnyone);
            Assert.Null(beforeAnyone);                                  // a fresh login has nobody to kick
            Assert.True(online.HoldsSlotForTest(key, first));

            // The second arrival for the same account gets the first one back, which is what it kicks.
            online.Register(key, second, out var displaced);
            Assert.Same(first, displaced);
            Assert.True(online.HoldsSlotForTest(key, second));
            Assert.False(online.HoldsSlotForTest(key, first));

            // The stale teardown lands late. It must not take the slot its replacement now owns.
            online.Unregister(key, first);
            Assert.True(online.HoldsSlotForTest(key, second));

            // The session that DOES own the slot gives it back, and the account is free again.
            online.Unregister(key, second);
            Assert.False(online.HoldsSlotForTest(key, second));
            online.Register(key, first, out var afterRelease);
            Assert.Null(afterRelease);
        }
        finally
        {
            online.Unregister(key, first);
            online.Unregister(key, second);
            _fx.World.LeaveMap(first, SlotMap);
            _fx.World.LeaveMap(second, SlotMap);
        }
    }

    /// <summary>The name lookup is case-insensitive and an offline name is <c>null</c>, not an exception and
    /// not somebody else. Case-insensitivity is the whisper contract: RTK's target lookup does not care how
    /// the sender typed the name, and neither does <c>CharacterStore.Key</c>, so a case-sensitive lookup here
    /// would refuse tells to a player the store considers the same account.
    ///
    /// <para>The id lookup is asserted on the same pair, because <c>ById</c> and <c>FindPlayer</c> walk the
    /// same <c>Players</c> lists and a move that broke the walk would break both.</para>
    ///
    /// <para>Falsified by changing <c>StringComparison.OrdinalIgnoreCase</c> to <c>Ordinal</c> in
    /// <c>OnlineRegistry.FindPlayer</c>: red on the first spelling that is not the stored one,
    /// "Assert.Same() Failure: Values are not the same instance / Expected: Session { ... PlayerId = 1 ... }
    /// / Actual: null".</para></summary>
    [Fact]
    public void FindPlayerIgnoresCaseAndAnOfflineNameComesBackNull()
    {
        var (session, _) = _fx.Player("LookupCase", LookupMap, 5, 5);
        var online = _fx.World.Online;

        try
        {
            Assert.Same(session, online.FindPlayer("LookupCase"));
            Assert.Same(session, online.FindPlayer("lookupcase"));
            Assert.Same(session, online.FindPlayer("LOOKUPCASE"));
            Assert.Same(session, online.ById(session.PlayerId));

            // Nobody by that name is online. Not an exception, not the player standing next to them.
            Assert.Null(online.FindPlayer("LookupCaseWhoIsNotHere"));
            Assert.Null(online.ById(uint.MaxValue));
        }
        finally
        {
            _fx.World.LeaveMap(session, LookupMap);
        }
    }

    /// <summary>Two properties of the roster in one test, because they are the same property from two sides:
    /// <c>All</c> is a SNAPSHOT taken under the lock (a list the caller owns, which the world going on to
    /// change does not touch) and <c>Count</c> is the live number.
    ///
    /// <para>The snapshot is not a nicety. The autosave sweep and the tick both iterate what <c>All</c>
    /// returns while flushing to SQLite and calling into sessions — work that must happen OUTSIDE
    /// <c>World._lock</c>. If <c>All</c> handed back a view over the live <c>Players</c> lists instead, either
    /// the sweep would hold the world lock for its whole duration or a login mid-sweep would throw
    /// <c>InvalidOperationException</c> out of the <c>foreach</c>.</para>
    ///
    /// <para>Deltas, not absolutes: the fixture's world is shared, so other classes' sessions are standing on
    /// other maps and the absolute count is not this test's to predict.</para>
    ///
    /// <para>Falsified twice. Dropping the <c>.ToList()</c> from <c>OnlineRegistry.All</c> — returning
    /// <c>IEnumerable&lt;Session&gt;</c> over the lazy <c>SelectMany</c>, which also needs a <c>.ToList()</c>
    /// added at <c>Session.UserList.cs:186</c> to compile at all — goes red on the re-count of the snapshot
    /// taken before the second player entered: "Assert.Equal() Failure: Values differ / Expected: 1 /
    /// Actual: 2". Counting maps instead of players in <c>Count</c> (<c>n += m.Players.Count > 0 ? 1 : 0</c>)
    /// goes red on the live count: "Assert.Equal() Failure: Values differ / Expected: 2 / Actual: 1" — 2 and
    /// 1 rather than absolutes because the assertion is on the delta.</para></summary>
    [Fact]
    public void AllIsASnapshotAndCountIsLive()
    {
        var (first, _) = _fx.Player("SnapFirst", SnapshotMap, 5, 5);
        Session? second = null;
        var online = _fx.World.Online;

        try
        {
            int countBefore = online.Count;
            var snapshot = online.All();
            var onSnapshotMapBefore = snapshot.Where(p => p.CharMap == SnapshotMap).ToList();
            Assert.Equal(new[] { first }, onSnapshotMapBefore);

            // The world changes under the snapshot's feet.
            (second, _) = _fx.Player("SnapSecond", SnapshotMap, 6, 5);

            // The list the caller already holds does not: it is theirs, taken as of the moment it was asked
            // for. This is what lets the sweep iterate it outside the lock.
            Assert.Equal(onSnapshotMapBefore.Count, snapshot.Where(p => p.CharMap == SnapshotMap).Count());
            Assert.DoesNotContain(second, snapshot);

            // A fresh ask sees the newcomer, and the live count moved by exactly one.
            Assert.Contains(second, online.All());
            Assert.Equal(countBefore + 1, online.Count);
        }
        finally
        {
            _fx.World.LeaveMap(first, SnapshotMap);
            if (second is not null) _fx.World.LeaveMap(second, SnapshotMap);
        }
    }

    // =====================================================================================================
    // The sweep.
    // =====================================================================================================

    /// <summary>The sweep's whole reason to exist: an IDLE dirty player. The session mutates, then stops
    /// sending packets — so its own read-loop <c>FlushIfDue</c> never gets another iteration to fire on — and
    /// the periodic sweep is the only thing left that will write it to disk before a crash does not.
    ///
    /// <para>Asserted through the store, not through the session's dirty flag: the claim is "the row moved",
    /// and a flush that cleared the flag without writing would satisfy any assertion made on the session
    /// itself. <c>Tick</c> is driven directly rather than through the <c>world-autosave</c> thread, which
    /// sleeps <c>Session.AutoSaveMs</c> before its first sweep.</para>
    ///
    /// <para>Falsified by making <c>AutoSaveLoop.Tick</c> iterate nothing (<c>foreach (var s in
    /// Enumerable.Empty&lt;Session&gt;())</c>): red on the load itself, "Assert.Equal() Failure: Values
    /// differ / Expected: Ok / Actual: NotFound" — the fixture never writes the row, so a sweep that flushes
    /// nobody leaves the store with no row for this account at all.</para></summary>
    [Fact]
    public void TheSweepWritesAnIdleDirtyPlayerToTheStore()
    {
        const string name = "SweepIdleDirty";
        var (session, _, character) = _fx.PlayerWith(name, _ => { }, SweepMap, 5, 5);

        try
        {
            character.Coins = 4242;
            session.WithState(session.MarkDirty);

            _fx.World.AutoSave.Tick();

            var load = _fx.Store.Load(name);
            Assert.Equal(CharacterLoadStatus.Ok, load.Status);
            var reloaded = Assert.IsType<Character>(load.Character);
            Assert.Equal(4242u, reloaded.Coins);
        }
        finally
        {
            _fx.World.LeaveMap(session, SweepMap);
        }
    }

    /// <summary>The isolation fence: one player's throwing flush costs that player its interval and nobody
    /// else theirs. Before the fence, the throw unwound the whole <c>foreach</c> into <c>Run</c>'s catch and
    /// every player AFTER the unlucky one in that snapshot silently missed the sweep — and idle dirty players
    /// are exactly who the sweep exists for, so a skipped sweep is lost state rather than a late save.
    ///
    /// <para>The throw is injected without touching production code: <c>Character.Karma</c> is a
    /// <c>double</c>, and <c>System.Text.Json</c> refuses to write NaN as JSON unless told otherwise, so
    /// <c>CharacterStore.Serialize</c> throws inside <c>FlushNow</c> — a serializer failure, which is the
    /// shape of throw this fence was written for. Both players stand on the same map so the thrower is ahead
    /// of the saver in that map's <c>Players</c> list, which is what makes "the next one" meaningful.</para>
    ///
    /// <para><c>FlushIsolated</c> is then called directly, both ways round, to pin the return value the two
    /// callers branch on. The log wordings themselves are pinned by
    /// <see cref="TheThreadWiringAndTheTwoFlushWordingsAreWhereTheyWere"/> — there is no log sink in this
    /// process to assert them through.</para>
    ///
    /// <para>Falsified by removing the try/catch from <c>AutoSaveLoop.FlushIsolated</c> (<c>s.FlushNow();
    /// return true;</c>): red at the sweep itself, "Assert.Null() Failure: Value is not null / Actual:
    /// System.ArgumentException: .NET number values such as positive and negative infinity cannot be written
    /// as valid JSON" — the thrower's exception escapes <c>Tick</c> and the saver is never
    /// reached.</para></summary>
    [Fact]
    public void OneThrowingFlushDoesNotCostTheNextPlayerItsInterval()
    {
        const string throwerName = "FenceThrower", saverName = "FenceSaver";
        var (thrower, _, throwerChar) = _fx.PlayerWith(throwerName, _ => { }, FenceMap, 5, 5);
        var (saver, _, saverChar) = _fx.PlayerWith(saverName, _ => { }, FenceMap, 6, 5);

        try
        {
            // The thrower is ahead of the saver in the map's Players list — otherwise "the NEXT player" is
            // not what this test is measuring.
            var order = _fx.World.Online.All().Where(p => p.CharMap == FenceMap).ToList();
            Assert.Equal(new[] { thrower, saver }, order);

            throwerChar.Karma = double.NaN;      // System.Text.Json cannot write NaN: Serialize throws
            saverChar.Coins = 777;
            thrower.WithState(thrower.MarkDirty);
            saver.WithState(saver.MarkDirty);

            Assert.Null(Record.Exception(() => _fx.World.AutoSave.Tick()));

            // The player after the throw got its interval.
            var saved = _fx.Store.Load(saverName);
            Assert.Equal(CharacterLoadStatus.Ok, saved.Status);
            Assert.Equal(777u, Assert.IsType<Character>(saved.Character).Coins);

            // The thrower did not: nothing was ever written for that account.
            Assert.NotEqual(CharacterLoadStatus.Ok, _fx.Store.Load(throwerName).Status);

            // The fence says so in its return value — which is what the shutdown flush counts and what keeps
            // a lost save from being reported as a clean one. Re-dirtied before each call because a THROWN
            // flush leaves the session clean: CaptureAndWrite clears _dirty before the capture and only
            // restores it when the write returns false, so a serializer throw skips that restore and the
            // next flush no-ops on the dirty gate. That is a real hole in the "retried next sweep" wording,
            // it predates this PR, and it is filed rather than fixed here (#37 section 4 is a move).
            thrower.WithState(thrower.MarkDirty);
            Assert.False(World.AutoSaveLoop.FlushIsolated(thrower, "autosave"));
            thrower.WithState(thrower.MarkDirty);
            Assert.False(World.AutoSaveLoop.FlushIsolated(thrower, "shutdown save", lastChance: true));
            saver.WithState(saver.MarkDirty);
            Assert.True(World.AutoSaveLoop.FlushIsolated(saver, "autosave"));

            // The shutdown flush counts it as failed, not as saved — the (saved, failed) tuple TkListener
            // reports. Deltas again: other classes' sessions are in the same sweep.
            thrower.WithState(thrower.MarkDirty);
            saver.WithState(saver.MarkDirty);
            var (savedCount, failedCount) = _fx.World.AutoSave.SaveAll();
            Assert.True(failedCount >= 1, $"the shutdown flush counted no failure: {savedCount} saved, {failedCount} failed");
            Assert.True(savedCount >= 1, $"the shutdown flush saved nobody: {savedCount} saved, {failedCount} failed");
        }
        finally
        {
            throwerChar.Karma = 0;
            thrower.WithState(thrower.MarkDirty);
            _fx.World.LeaveMap(thrower, FenceMap);
            _fx.World.LeaveMap(saver, FenceMap);
        }
    }

    // =====================================================================================================
    // The wiring the move had to preserve.
    // =====================================================================================================

    /// <summary>Two things this move could have broken silently, pinned at the source because neither is
    /// observable from inside the test process.
    ///
    /// <para><b>The threads.</b> <c>World.Start</c> must still create exactly two, one named
    /// <c>world-tick</c> running <c>TickLoop</c> and one named <c>world-autosave</c> running the sweep — now
    /// <c>AutoSave.Run</c> rather than <c>World.AutoSaveLoop</c>. The .NET runtime exposes no way to
    /// enumerate managed threads by name (<c>Process.Threads</c> hands out OS threads with no managed
    /// identity), so the alternative to reading the source would be starting a second <c>World</c> in the
    /// test process — which starts the watchdog, the status writer and the restart ladder, and that ladder
    /// polls <c>run/restart_at</c> and calls <c>Environment.Exit</c> when it finds one. Not worth it for this
    /// assertion.</para>
    ///
    /// <para><b>The two flush wordings.</b> A sweep failure "is retried next sweep"; a shutdown failure is
    /// "save LOST". Reporting the second as the first is the one thing an operator reading the last lines of
    /// a log must not be told, and the strings moved files in this PR. <c>Log</c> writes through a background
    /// writer thread with no sink to attach, so the text is pinned here.</para>
    ///
    /// <para>Falsified by renaming the thread body (<c>Run</c> to <c>Run2</c> on both sides, so the server
    /// still builds and still starts a <c>world-autosave</c> thread): red with "Assert.Matches() Failure:
    /// Pattern not found in value / Regex: new Thread\(AutoSave\.Run\)...". Falsified again by softening the
    /// periodic wording to "retried later": red with "Assert.Contains() Failure: Sub-string not found /
    /// Not found: that player's save is retried next sweep,...".</para></summary>
    [Fact]
    public void TheThreadWiringAndTheTwoFlushWordingsAreWhereTheyWere()
    {
        string serverDir = Path.Combine(RepoRoot().FullName, "Server");
        string worldSource = File.ReadAllText(Path.Combine(serverDir, "World.cs"));
        string sweepSource = File.ReadAllText(Path.Combine(serverDir, "World.AutoSaveLoop.cs"));

        // Exactly two threads, still started from World.Start, still named what the logs and the ops runbook
        // call them.
        Assert.Equal(2, Regex.Matches(worldSource, @"new Thread\(").Count);
        Assert.Matches(@"new Thread\(TickLoop\)\s*\{[^}]*Name = ""world-tick""[^}]*\}\.Start\(\);", worldSource);
        Assert.Matches(@"new Thread\(AutoSave\.Run\)\s*\{[^}]*Name = ""world-autosave""[^}]*\}\.Start\(\);", worldSource);
        Assert.Contains("IsBackground = true, Name = \"world-tick\"", worldSource);
        Assert.Contains("IsBackground = true, Name = \"world-autosave\"", worldSource);

        // The sweep itself no longer lives in World.cs at all.
        Assert.DoesNotContain("private void AutoSaveTick()", worldSource);
        Assert.DoesNotContain("FlushIsolated", worldSource);

        // Both wordings, unchanged by the move.
        Assert.Contains("that player's save is retried next sweep, the others continue", sweepSource);
        Assert.Contains("save LOST — process is exiting, there is no retry", sweepSource);
        Assert.Contains("FlushIsolated(s, \"autosave\")", sweepSource);
        Assert.Contains("FlushIsolated(s, \"shutdown save\", lastChance: true)", sweepSource);
    }

    private static DirectoryInfo RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Project1998.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!;
    }

#if DEBUG
    /// <summary>The lock contract of both owners, both directions, in the
    /// <c>MobAiTickTests.StepOutsideTheWorldLockAsserts</c> shape — and this pair is unusual in that the two
    /// owners want OPPOSITE things, so each is the other's control.
    ///
    /// <para><c>OnlineRegistry.ByIdLocked</c> is for callers already inside <c>World._lock</c> and is loud
    /// outside it. <c>AutoSaveLoop.Tick</c> and <c>SaveAll</c> must never run inside it — they call
    /// <c>Session.FlushNow</c>, which enters the session's state monitor, and entering a session monitor with
    /// the world lock held is the inversion rule 1 forbids (Session.State.cs) — so they are loud inside it.
    /// A stray <c>lock</c> around everything, or a stray removal of one, therefore cannot pass this test in
    /// both halves.</para>
    ///
    /// <para>The four methods that take the lock for themselves (<c>All</c>, <c>Count</c>, <c>FindPlayer</c>,
    /// <c>ById</c>) are the second control: they are legal from both sides, and asserting that keeps a
    /// blanket assert from being read as a passing grade.</para>
    ///
    /// <para>Debug-only by construction, like every lock assert in <c>docs/common/Locking.md</c>: the asserts
    /// are compiled out of Release, so the test is too. Falsified by deleting each of the three assert lines
    /// in turn: red on that method's <c>Assert.NotNull</c>, "Assert.NotNull() Failure: Value is null", three
    /// times.</para>
    ///
    /// <para><b>What the two sweep asserts buy, found by falsifying them.</b> Deleting the assert from
    /// <c>Tick</c> does not merely downgrade the diagnosis — it produces NO exception at all. The rule-1
    /// assert in <c>Session.EnterState</c> does still fire, one player at a time, but every one of those
    /// firings is caught by <see cref="World.AutoSaveLoop.FlushIsolated"/>'s own try/catch and logged as that
    /// player's flush failing. A sweep run under the world lock is therefore SILENT without these two lines,
    /// with the lock-order violation disguised as a bad disk.</para></summary>
    [Fact]
    public void TheTwoOwnersRefuseTheWrongSideOfTheWorldLock()
    {
        var (session, _) = _fx.Player("LockSideCheck", LockMap, 5, 5);

        try
        {
            Assert.False(_fx.World.HoldsWorldLock);

            // Outside the lock: the locked-only lookup is loud, everything that takes the lock is fine.
            var byIdLockedOff = Record.Exception(() => _fx.World.Online.ByIdLocked(session.PlayerId));
            Assert.NotNull(byIdLockedOff);
            Assert.Contains("nowhere else", byIdLockedOff!.Message);

            Assert.Same(session, _fx.World.Online.ById(session.PlayerId));
            Assert.Same(session, _fx.World.Online.FindPlayer("LockSideCheck"));
            Assert.Contains(session, _fx.World.Online.All());
            Assert.True(_fx.World.Online.Count >= 1);

            // Inside the lock: the sweep and the shutdown flush are loud, the lookups are still fine.
            var tickUnder = Record.Exception(() =>
                _fx.World.UnderWorldLockForTest(() => _fx.World.AutoSave.Tick()));
            Assert.NotNull(tickUnder);
            Assert.Contains("nowhere else", tickUnder!.Message);

            var saveAllUnder = Record.Exception(() =>
                _fx.World.UnderWorldLockForTest(() => _fx.World.AutoSave.SaveAll()));
            Assert.NotNull(saveAllUnder);
            Assert.Contains("nowhere else", saveAllUnder!.Message);

            var lookupsUnder = Record.Exception(() => _fx.World.UnderWorldLockForTest(() =>
            {
                Assert.Same(session, _fx.World.Online.ByIdLocked(session.PlayerId));
                Assert.Same(session, _fx.World.Online.ById(session.PlayerId));
                Assert.Same(session, _fx.World.Online.FindPlayer("LockSideCheck"));
                Assert.Contains(session, _fx.World.Online.All());
                Assert.True(_fx.World.Online.Count >= 1);
            }));
            Assert.Null(lookupsUnder);
        }
        finally
        {
            _fx.World.LeaveMap(session, LockMap);
        }
    }
#endif
}
