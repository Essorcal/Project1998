using Microsoft.Data.Sqlite;
using Protocol.Tk495;
using Server;
using Shared;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Tests;

/// <summary>
/// Tests for the persistence guarantees the trade/parcel paths now depend on. These run against a real
/// SQLite database in a throwaway state directory, because the thing under test is SQLite transaction
/// behaviour — a mock would only prove the mock is transactional.
/// </summary>
[Collection("db")]
public class PersistenceTests : IDisposable
{
    // Distinct per test class run, and prefixed so a leaked row is obviously test debris.
    private readonly string _a = $"zz_test_a_{Guid.NewGuid():N}"[..24];
    private readonly string _b = $"zz_test_b_{Guid.NewGuid():N}"[..24];
    private readonly CharacterStore _store = new(RepoPaths.CharsDir());
    private readonly DbFixture _fixture;
    private readonly ITestOutputHelper _out;

    public PersistenceTests(DbFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _out = output;
    }

    /// <summary>A measurement worth keeping from every run: to the test output, and to the console as well,
    /// because CI's log runs at normal verbosity, which drops a passed test's output, and CI is where these
    /// waits are measured on Linux.</summary>
    private void Measured(string line)
    {
        _out.WriteLine(line);
        Console.WriteLine($"[{nameof(PersistenceTests)}] {line}");
    }

    private static Character Make(string name, uint coins, params (int id, int amount)[] items)
    {
        var c = new Character
        {
            SchemaVersion = Character.CurrentSchemaVersion,
            Name = name,
            Coins = coins,
        };
        byte slot = 0;
        foreach (var (id, amount) in items) c.Inventory.Add(new InvItem(slot++, id, amount));
        return c;
    }

    private Character LoadOk(string name) => LoadOk(_store, name);

    private static Character LoadOk(CharacterStore store, string name)
    {
        var result = store.Load(name);
        Assert.Equal(CharacterLoadStatus.Ok, result.Status);
        return Assert.IsType<Character>(result.Character);
    }

    public void Dispose()
    {
        // Leave no debris in the live database.
        try
        {
            using var cn = Db.Open();
            foreach (var n in new[] { _a, _b })
            {
                using var cmd = cn.CreateCommand();
                cmd.CommandText = "DELETE FROM characters WHERE username=$u;";
                cmd.Parameters.AddWithValue("$u", CharacterStore.Key(n));
                cmd.ExecuteNonQuery();
            }
        }
        catch { /* best effort cleanup */ }
    }

    [Fact]
    public void DatabaseUsesTheThrowawayStateDirectory()
    {
        Assert.Equal(Path.Combine(_fixture.StateDirectory, "project1998.db"), Db.Path);
        Assert.True(File.Exists(Db.Path));
    }

    [Fact]
    public void MissingRow_IsNotReportedAsUnreadable()
    {
        var load = _store.Load(_a);

        Assert.Equal(CharacterLoadStatus.NotFound, load.Status);
        Assert.Null(load.Character);
        Assert.Null(load.Reason);
    }

    [Fact]
    public void ProductionDatabaseIsUnchanged()
    {
        Assert.Equal(TestProcessState.ProductionDatabaseExisted,
                     File.Exists(TestProcessState.ProductionDatabasePath));
        if (TestProcessState.ProductionDatabaseExisted)
            Assert.Equal(TestProcessState.ProductionDatabaseLastWriteTimeUtc,
                         File.GetLastWriteTimeUtc(TestProcessState.ProductionDatabasePath));
    }

    /// <summary>The trade guarantee: both sides land together. This is the happy path — the failure path is
    /// the next test, and it's the one that actually matters.</summary>
    [Fact]
    public void SaveMany_PersistsEveryCharacter()
    {
        var a = Make(_a, 100, (1, 5));
        var b = Make(_b, 200);

        Assert.True(_store.SaveMany(new[] { a, b }));

        Assert.Equal(100u, LoadOk(_a).Coins);
        Assert.Equal(200u, LoadOk(_b).Coins);
        Assert.Equal(5, LoadOk(_a).Inventory.Single().Amount);
    }

    /// <summary>
    /// THE test. A trade moves goods from one character to the other; if the write fails partway, NEITHER
    /// side may be left changed — a half-applied exchange is a dupe or a vanish depending on which half won.
    ///
    /// <para>The failure is induced the way it would really happen: another connection holds the write lock,
    /// so SaveMany's transaction cannot commit. That exercises the actual contention path (SQLITE_BUSY after
    /// the busy_timeout) rather than a synthetic serializer error, which is why this test spends about
    /// <see cref="Db.BusyTimeoutMs"/> waiting before it gets its answer. The comment here used to claim "~5s"
    /// while the test actually took 30: the connection's DefaultTimeout was the provider's 30s default, and
    /// the busy_timeout pragma bounded nothing a caller could observe. <c>Db.Open</c> now sets both from one
    /// constant, and <see cref="SaveMany_FailsWithinTheBusyTimeout_WhenTheWriteLockIsHeld"/> is the fact that
    /// holds that true — this one is about the ROLLBACK, not about the clock.</para>
    ///
    /// <para>On its OWN database file (<see cref="IsolatedDatabase"/>): the lock below is database-wide, and
    /// on the process database it would be a database-wide lock for every collection running beside this
    /// one.</para>
    /// </summary>
    [Fact]
    public void SaveMany_LeavesNothingWritten_WhenTheWriteFails()
    {
        using var db = new IsolatedDatabase();

        // Seed both at a known state: `a` holds 10 of item 1 and 100 coin, `b` holds nothing.
        var a = Make(_a, 100, (1, 10));
        var b = Make(_b, 100);
        Assert.True(db.Store.SaveMany(new[] { a, b }));

        // Stage the trade in memory: 5 of item 1 and 50 coin move from a to b.
        a.Inventory[0].Amount = 5;
        a.Coins = 50;
        b.Inventory.Add(new InvItem(0, 1, 5));
        b.Coins = 150;

        // Block all writes to THIS file for the duration of the attempt.
        using (var blocker = db.Open())
        {
            using var begin = blocker.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE;";   // takes the write lock and holds it
            begin.ExecuteNonQuery();

            Assert.False(db.Store.SaveMany(new[] { a, b }));

            using var rollback = blocker.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }

        // Neither side moved. `a` in particular must NOT have been debited — that is the half which, had it
        // committed alone, would have destroyed the goods outright.
        var reloadedA = LoadOk(db.Store, _a);
        var reloadedB = LoadOk(db.Store, _b);
        Assert.Equal(100u, reloadedA.Coins);
        Assert.Equal(10, reloadedA.Inventory.Single().Amount);
        Assert.Equal(100u, reloadedB.Coins);
        Assert.Empty(reloadedB.Inventory);
    }

    /// <summary>
    /// The BOUND on a contended write, which is the number the whole design rests on: a save that cannot get
    /// the write lock gives up after ONE of SQLite's waits, about <see cref="Db.BusyTimeoutMs"/> — not after
    /// two, and not after the provider's 30s default. That difference is twenty-five seconds of a session
    /// thread — the save paths run on the session's own thread and on the autosave sweep.
    ///
    /// <para>Both ends are asserted on purpose. The upper bound, 1.5x the busy_timeout, is what one wait
    /// allows: about 5.0 s on Linux and 5.5 s on Windows, whose sleeps run over. It is red at about 10 s if
    /// a wait ends a little early while <c>Db.Open</c>'s <c>DefaultTimeout</c> is not below the busy_timeout
    /// (the provider then steps again for a second whole wait: fork CI's "took 10.0s", twice), and at about
    /// 30 s if that line goes away, because <c>PRAGMA busy_timeout</c> alone does not bound the statement.
    /// A full-length wait seldom ends early, so this fact alone rarely sees the second wait;
    /// <see cref="AContendedStatement_EndsAfterOneSqliteWait_WhenThatWaitEndsEarly"/> makes one end early on
    /// purpose. The lower bound is the opposite failure — the save giving up before SQLite's own wait has run:
    /// the busy_timeout trimmed, or the pragma gone, so that only the provider's shorter window bounds the
    /// save (about 3 s, under the floor). See
    /// <see cref="SaveMany_StillSucceeds_WhenTheContentionClearsInsideTheWindow"/> for the behavioural half of
    /// that.</para>
    ///
    /// <para>Bounds are derived from the constant rather than written as literals, so changing the timeout
    /// moves this fact with it instead of breaking it.</para>
    ///
    /// <para>On its OWN database file (<see cref="IsolatedDatabase"/>): this one holds the write lock for the
    /// entire five-second window, which is the longest any fact in the suite holds it, and on the process
    /// database that is five seconds every other collection spends locked out.</para>
    /// </summary>
    [Fact]
    public void SaveMany_FailsWithinTheBusyTimeout_WhenTheWriteLockIsHeld()
    {
        using var db = new IsolatedDatabase();

        var a = Make(_a, 100);
        Assert.True(db.Store.SaveMany(new[] { a }));
        a.Coins = 200;

        var elapsed = TimeSpan.Zero;
        bool saved;
        using (var blocker = db.Open())
        {
            using var begin = blocker.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE;";   // held for the whole attempt: this never clears
            begin.ExecuteNonQuery();

            var clock = System.Diagnostics.Stopwatch.StartNew();
            saved = db.Store.SaveMany(new[] { a });
            clock.Stop();
            elapsed = clock.Elapsed;

            using var rollback = blocker.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }

        Assert.False(saved);

        Measured($"a contended save failed after {elapsed.TotalMilliseconds:F0} ms " +
                 $"(busy_timeout {Db.BusyTimeoutMs} ms)");

        // Room both ways for a loaded runner, which stretches a wait. What is pinned is ONE wait: about five
        // seconds, not ten and not thirty.
        var floor   = TimeSpan.FromMilliseconds(Db.BusyTimeoutMs * 0.8);
        var ceiling = TimeSpan.FromMilliseconds(Db.BusyTimeoutMs * 1.5);
        Assert.True(elapsed >= floor,
            $"a contended save gave up after {elapsed.TotalSeconds:F1}s, before SQLite's own busy_timeout of " +
            $"{Db.BusyTimeoutMs}ms had run — the pragma is not being set, or is set short");
        Assert.True(elapsed <= ceiling,
            $"a contended save took {elapsed.TotalSeconds:F1}s to fail, against a busy_timeout of " +
            $"{Db.BusyTimeoutMs}ms. About 10s: the connection's DefaultTimeout is not below the busy_timeout, so " +
            "the provider stepped again for a second whole wait. About 30s: it is not being set at all, so the " +
            "provider retried for its own default and the pragma bounds nothing.");

        // The failed save changed nothing, and the row is still writable afterwards.
        Assert.Equal(100u, LoadOk(db.Store, _a).Coins);
        Assert.True(db.Store.SaveMany(new[] { a }));
    }

    /// <summary>
    /// A contended statement ends after ONE of SQLite's busy waits even when that wait ends early; fork CI's
    /// "a contended save took 10.0s" (twice) was two. Microsoft.Data.Sqlite re-runs a statement that came back
    /// SQLITE_BUSY until its own window, the connection's <c>DefaultTimeout</c>, has run out, and it reads that
    /// clock only after SQLite's wait returns. While the window was as long as the busy_timeout, a wait that
    /// ended a little early by the provider's clock was reset and stepped again for a second whole wait;
    /// <c>Db</c> now sets the window shorter than the wait.
    ///
    /// <para>The early end is made on purpose, in the shape of the diagnosis probe: SQLite's own wait just
    /// under the old window. The waiting connection is a real <see cref="Db.Open(string)"/> one, with its
    /// busy_timeout then lowered by a whole second — on Linux, whose build sleeps in whole seconds, the most
    /// one early wake can take from a wait; on Windows, a wait that ends a second early. The blocking statement
    /// is the save path's own, <c>BeginTransaction(deferred: false)</c>, which runs BEGIN IMMEDIATE.</para>
    ///
    /// <para>Counted, not timed: SQLite's trace callback fires each time the statement starts to run, and the
    /// provider resets it and steps it again for every further wait, so the count is the number of waits. A
    /// busy machine only stretches a wait, which makes a second one less likely, never more. The time is in
    /// the message and the test output.</para>
    ///
    /// <para>Red with the window back at the busy_timeout (<c>DefaultTimeout</c> 5 s): "a contended BEGIN
    /// IMMEDIATE took 2 SQLite wait(s) in 9016 ms, with busy_timeout 4000 ms and DefaultTimeout 5 s" (Windows;
    /// on Linux the two waits are four whole seconds each).</para>
    ///
    /// <para>On its OWN database file (<see cref="IsolatedDatabase"/>), like every fact here that holds the
    /// write lock.</para>
    /// </summary>
    [Fact]
    public void AContendedStatement_EndsAfterOneSqliteWait_WhenThatWaitEndsEarly()
    {
        const int earlyMs = 1000;
        int waitMs = Db.BusyTimeoutMs - earlyMs;

        using var db = new IsolatedDatabase();
        using var blocker = db.Open();
        using (var begin = blocker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";   // held until the attempt below has given up
            begin.ExecuteNonQuery();
        }

        using var waiter = db.Open();
        // 0 means no limit to the provider: the attempt below would then wait for ever instead of failing.
        Assert.True(waiter.DefaultTimeout >= 1, $"Db.Open set a DefaultTimeout of {waiter.DefaultTimeout}");
        using (var shorten = waiter.CreateCommand())
        {
            shorten.CommandText = $"PRAGMA busy_timeout={waitMs};";
            shorten.ExecuteNonQuery();
        }

        int waits = 0;
        SQLitePCL.raw.sqlite3_trace(waiter.Handle, (SQLitePCL.strdelegate_trace)((_, sql) =>
        {
            if (sql is not null && sql.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref waits);
        }), null);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var error = Record.Exception(() =>
        {
            using var tx = waiter.BeginTransaction(deferred: false);
        });
        clock.Stop();
        SQLitePCL.raw.sqlite3_trace(waiter.Handle, (SQLitePCL.strdelegate_trace)null!, null);

        using (var rollback = blocker.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }

        string seen = $"{waits} SQLite wait(s) in {clock.Elapsed.TotalMilliseconds:F0} ms, with busy_timeout " +
                      $"{waitMs} ms and DefaultTimeout {waiter.DefaultTimeout} s";
        Measured(seen);

        var busy = Assert.IsType<SqliteException>(error);
        Assert.Equal(5, busy.SqliteErrorCode);   // SQLITE_BUSY: it gave up on the lock, nothing else
        Assert.True(waits == 1,
            $"a contended BEGIN IMMEDIATE took {seen}. More than one: the provider's window had not run out " +
            "when SQLite's wait ended early, so it stepped again and SQLite waited again — DefaultTimeout is " +
            "not far enough below the busy_timeout.");
    }

    /// <summary>
    /// The negative control for the shorter timeout: a contention that CLEARS inside the window must still
    /// end in a successful save, not a failed one. Two writers overlapping for a moment is the ordinary case
    /// — the login process stamping last_login while the game autosaves, two sessions flushing together —
    /// and shortening the bound must not convert those into lost writes that wait for the next sweep.
    ///
    /// <para>The lock is held for a second, well inside <see cref="Db.BusyTimeoutMs"/>, on another thread;
    /// the save must block and then succeed once the lock goes.</para>
    ///
    /// <para>On its OWN database file (<see cref="IsolatedDatabase"/>): a second of database-wide lock is
    /// still a second every other collection would spend waiting.</para>
    /// </summary>
    [Fact]
    public void SaveMany_StillSucceeds_WhenTheContentionClearsInsideTheWindow()
    {
        using var db = new IsolatedDatabase();

        var a = Make(_a, 100);
        Assert.True(db.Store.SaveMany(new[] { a }));
        a.Coins = 777;

        const int holdMs = 1000;
        Assert.True(holdMs < Db.BusyTimeoutMs, "the hold has to be inside the window for this to test anything");

        using var locked = new ManualResetEventSlim(false);
        // A plain thread rather than a Task: nothing here is awaited, and the test body has to block on the
        // save itself, which is what is being measured.
        var holder = new Thread(() =>
        {
            using var blocker = db.Open();
            using var begin = blocker.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();
            locked.Set();
            Thread.Sleep(holdMs);
            using var rollback = blocker.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }) { IsBackground = true };
        holder.Start();

        Assert.True(locked.Wait(TimeSpan.FromSeconds(30)), "the blocking thread never took the write lock");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool saved = db.Store.SaveMany(new[] { a });
        clock.Stop();
        Assert.True(holder.Join(TimeSpan.FromSeconds(30)), "the blocking thread never released the write lock");

        Assert.True(saved,
            $"a save contended for {holdMs}ms — well inside the {Db.BusyTimeoutMs}ms window — failed after " +
            $"{clock.Elapsed.TotalSeconds:F1}s instead of waiting the lock out");
        Assert.Equal(777u, LoadOk(db.Store, _a).Coins);
        // It really did wait rather than slipping in before the lock was taken, which would make the pass
        // meaningless.
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(holdMs * 0.5),
            $"the save returned in {clock.Elapsed.TotalMilliseconds:F0}ms, so it never contended at all");
    }

    /// <summary>
    /// A contention that clears AFTER the provider's window has run out, but inside SQLite's own wait, still
    /// ends in a successful save. The window (<c>DefaultTimeout</c>, 3 s) is shorter than the busy_timeout
    /// (5,000 ms), so this band is new: a lock let go at about four seconds has to be waited out inside
    /// SQLite, not given up on when the provider's window ends. The fact above holds the lock for a second,
    /// inside both, and cannot see the band.
    ///
    /// <para>In one wait: SQLite's first wait lasts about five seconds, so at four the save is still inside
    /// it and takes the lock there. The provider could only add a second wait after a first one that ended
    /// before its window, which the floor of
    /// <see cref="SaveMany_FailsWithinTheBusyTimeout_WhenTheWriteLockIsHeld"/> rules out.</para>
    ///
    /// <para>Why 4 s sits inside the band on both builds. With no SQLite wait, the provider alone tries every
    /// 150 ms and makes its last try at about 3.1 s, most of a second before the lock goes. SQLite tries the
    /// lock again after each of its sleeps: on Windows after sleeps of at most 100 ms (a little longer in
    /// practice), until about 5.5 s; on Linux after each whole second, the last try at 5 s, so a lock let go
    /// at 4 s is taken at the 4 s try or the 5 s one. Either way about a second inside the wait.</para>
    ///
    /// <para>Red with the busy_timeout pragma taken out of <c>Db.Open</c>: the provider alone gives up at
    /// about 3.1 s while the lock is still held, and the save fails.</para>
    ///
    /// <para>On its OWN database file (<see cref="IsolatedDatabase"/>), like every fact here that holds the
    /// write lock.</para>
    /// </summary>
    [Fact]
    public void SaveMany_StillSucceeds_WhenTheContentionClearsAfterTheProvidersWindow()
    {
        using var db = new IsolatedDatabase();

        var a = Make(_a, 100);
        Assert.True(db.Store.SaveMany(new[] { a }));
        a.Coins = 777;

        const int holdMs = 4000;
        int windowMs;
        using (var probe = db.Open()) windowMs = probe.DefaultTimeout * 1000;
        Assert.True(windowMs < holdMs && holdMs < Db.BusyTimeoutMs,
            $"the hold ({holdMs}ms) has to outlast the provider's window ({windowMs}ms) and end inside SQLite's " +
            $"wait ({Db.BusyTimeoutMs}ms) for this to test anything");

        using var locked = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            using var blocker = db.Open();
            using var begin = blocker.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();
            locked.Set();
            Thread.Sleep(holdMs);
            using var rollback = blocker.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }) { IsBackground = true };
        holder.Start();

        Assert.True(locked.Wait(TimeSpan.FromSeconds(30)), "the blocking thread never took the write lock");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool saved = db.Store.SaveMany(new[] { a });
        clock.Stop();
        Assert.True(holder.Join(TimeSpan.FromSeconds(30)), "the blocking thread never released the write lock");

        Measured($"a save contended for {holdMs} ms {(saved ? "saved" : "FAILED")} after " +
                 $"{clock.Elapsed.TotalMilliseconds:F0} ms (window {windowMs} ms, busy_timeout {Db.BusyTimeoutMs} ms)");

        Assert.True(saved,
            $"a save contended for {holdMs}ms — past the provider's {windowMs}ms window, inside the " +
            $"{Db.BusyTimeoutMs}ms busy_timeout — failed after {clock.Elapsed.TotalSeconds:F1}s: SQLite did not " +
            "wait the lock out, so only the provider's window bounded the save");
        Assert.Equal(777u, LoadOk(db.Store, _a).Coins);
        // It waited until about the release, past the window, rather than slipping in early.
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(holdMs * 0.9),
            $"the save returned in {clock.Elapsed.TotalMilliseconds:F0}ms, before the lock was let go");
    }

    /// <summary>
    /// Startup against a first-ever database another connection holds behaves as it always has: it keeps its
    /// own 5 s window (<c>Db</c>'s <c>StartupTimeoutSeconds</c>), not the 3 s window every <c>Db.Open</c>
    /// connection gets. Its first statement, <c>PRAGMA journal_mode=WAL</c>, runs before any busy_timeout, so
    /// against a held file SQLite fails it busy at once and only the provider's window bounds it; with the 3 s
    /// window, startup would give up two seconds sooner than it always has.
    ///
    /// <para>The file is a first-ever one, still in rollback-journal mode, held throughout with
    /// <c>BEGIN EXCLUSIVE</c>, which shuts out even a reader. Bounds as the save fact's: at least 0.8x and at
    /// most 1.5x the busy_timeout. Red with startup's window at 3 s: "startup against a held file gave up after
    /// 3.0s".</para>
    /// </summary>
    [Fact]
    public void InitializeDatabase_GivesUpAtItsOwnFiveSecondWindow_WhenAFirstEverFileIsHeld()
    {
        string path = FirstEverDatabaseFile();
        using var holder = new SqliteConnection($"Data Source={path};Pooling=False");
        holder.Open();
        Assert.Equal("delete", Text(holder, "PRAGMA journal_mode;"));
        Run(holder, "BEGIN EXCLUSIVE;");   // held until the attempt below has given up

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var error = Record.Exception(() => Db.InitializeDatabase(path));
        clock.Stop();
        Run(holder, "ROLLBACK;");

        Measured($"startup against a held file gave up after {clock.Elapsed.TotalMilliseconds:F0} ms " +
                 $"(busy_timeout {Db.BusyTimeoutMs} ms)");

        var busy = Assert.IsType<SqliteException>(error);
        Assert.Equal(5, busy.SqliteErrorCode);   // SQLITE_BUSY: it gave up on the lock, nothing else
        var floor   = TimeSpan.FromMilliseconds(Db.BusyTimeoutMs * 0.8);
        var ceiling = TimeSpan.FromMilliseconds(Db.BusyTimeoutMs * 1.5);
        Assert.True(clock.Elapsed >= floor,
            $"startup against a held file gave up after {clock.Elapsed.TotalSeconds:F1}s, sooner than the 5 s " +
            "window startup has always had — it is getting the shorter window of Db.Open's connections");
        Assert.True(clock.Elapsed <= ceiling,
            $"startup against a held file took {clock.Elapsed.TotalSeconds:F1}s to give up, against a " +
            $"busy_timeout of {Db.BusyTimeoutMs}ms: its window is longer than it always was, or not set at all");

        // Once the hold is gone the same file initializes.
        Db.InitializeDatabase(path);
        using var check = new SqliteConnection($"Data Source={path};Pooling=False");
        check.Open();
        Assert.Equal("wal", Text(check, "PRAGMA journal_mode;"));
    }

    /// <summary>
    /// The same first-ever file held with <c>BEGIN EXCLUSIVE</c> and let go at 4 s: startup initializes, as it
    /// always has. Its 5 s window outlasts the hold, and the provider tries the statement again every 150 ms or
    /// so, so it is taken within about a fifth of a second of the release. Red with startup's window at 3 s: the provider gives
    /// up at about 3 s while the file is still held, "startup ... FAILED after 3.0s".
    /// </summary>
    [Fact]
    public void InitializeDatabase_WaitsOutAFourSecondExclusiveHold_OnAFirstEverFile()
    {
        var (error, elapsed) = StartupAgainstAHold("BEGIN EXCLUSIVE;", holdMs: 4000);
        AssertStartupWaitedOut("an exclusive hold", error, elapsed, holdMs: 4000);
    }

    /// <summary>
    /// A first-ever file whose write lock another connection holds (<c>BEGIN IMMEDIATE</c>), let go at 4 s:
    /// startup initializes, as it always has. Switching a rollback-journal file to WAL reads it and then takes
    /// its write lock inside the one statement, and SQLite never waits for that lock, busy_timeout or not, so
    /// the provider's 5 s window is what waits this hold out.
    ///
    /// <para>Red with startup's window at 3 s, and red at the previous round's code, which set the
    /// busy_timeout before the batch but kept the 3 s window: the switch was failed busy at about 3.1 s while
    /// the lock was still held.</para>
    /// </summary>
    [Fact]
    public void InitializeDatabase_WaitsOutAFourSecondWriteLock_OnAFirstEverFile()
    {
        var (error, elapsed) = StartupAgainstAHold("BEGIN IMMEDIATE;", holdMs: 4000);
        AssertStartupWaitedOut("a write lock", error, elapsed, holdMs: 4000);
    }

    /// <summary>Startup against a first-ever file another connection takes with <paramref name="begin"/>,
    /// holds for <paramref name="holdMs"/> and then lets go: what startup threw, if anything, and how long it
    /// took.</summary>
    private static (Exception? Error, TimeSpan Elapsed) StartupAgainstAHold(string begin, int holdMs)
    {
        string path = FirstEverDatabaseFile();
        Assert.True(holdMs < Db.BusyTimeoutMs, "the hold has to end inside startup's window for this to test anything");

        using var locked = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            using var blocker = new SqliteConnection($"Data Source={path};Pooling=False");
            blocker.Open();
            Run(blocker, begin);
            locked.Set();
            Thread.Sleep(holdMs);
            Run(blocker, "ROLLBACK;");
        }) { IsBackground = true };
        holder.Start();
        Assert.True(locked.Wait(TimeSpan.FromSeconds(30)), "the blocking thread never took the lock");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var error = Record.Exception(() => Db.InitializeDatabase(path));
        clock.Stop();
        Assert.True(holder.Join(TimeSpan.FromSeconds(30)), "the blocking thread never let the lock go");

        if (error is null)
        {
            using var check = new SqliteConnection($"Data Source={path};Pooling=False");
            check.Open();
            Assert.Equal("wal", Text(check, "PRAGMA journal_mode;"));
            Assert.Equal(Db.CurrentSchemaVersion.ToString(), Text(check, "PRAGMA user_version;"));
        }
        return (error, clock.Elapsed);
    }

    private void AssertStartupWaitedOut(string hold, Exception? error, TimeSpan elapsed, int holdMs)
    {
        Measured($"startup against {hold} of {holdMs} ms {(error is null ? "initialized" : "FAILED")} after " +
                 $"{elapsed.TotalMilliseconds:F0} ms");
        Assert.True(error is null,
            $"startup against {hold} of {holdMs}ms FAILED after {elapsed.TotalSeconds:F1}s ({error?.Message}): " +
            "its window ran out before the hold did — it is not getting the 5 s window startup has always had");
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(holdMs * 0.9),
            $"startup returned in {elapsed.TotalMilliseconds:F0}ms, before the lock was let go");
    }

    /// <summary>A database file nobody has initialized yet: one table, so the file exists, still in SQLite's
    /// default rollback-journal mode. Inside the run's state directory, which the run's end deletes.</summary>
    private static string FirstEverDatabaseFile()
    {
        string dir = Path.Combine(TestProcessState.StateDirectory, $"first-ever-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "project1998.db");
        using var cn = new SqliteConnection($"Data Source={path};Pooling=False");
        cn.Open();
        Run(cn, "CREATE TABLE zz_first_ever(x);");
        return path;
    }

    private static void Run(SqliteConnection cn, string sql)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Text(SqliteConnection cn, string sql)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>
    /// The other half of shortening the timeout: a failed UNCONDITIONAL save must be retried, not lost. The
    /// throttled autosave path always put the dirty flag back when a write failed, but
    /// <c>Session.StoreSave</c> — the spellbook, legend and profile writes that persist whether or not the
    /// flag happens to be set — did not, because <c>CaptureAndWrite</c> only re-dirtied under
    /// <c>dirtyGated</c>. A write lock held between five and thirty seconds used to be waited out and the
    /// save landed; with the shorter bound it fails, and without the re-dirty there is nothing left to
    /// retry it.
    ///
    /// <para>Driven through the real 0x4F change-profile frame rather than a helper, because the player-
    /// facing half is part of the same fact: the reply must not claim the profile was saved when it was
    /// not. Then the lock goes and the next flush — <c>FlushNow</c>, the same call World's autosave sweep
    /// makes — has to land the row.</para>
    ///
    /// <para>Red without the re-dirty in <c>CaptureAndWrite</c>: the session reports dirty False and
    /// <c>FlushNow</c> finds nothing pending, so the profile never reaches the database.</para>
    ///
    /// <para>On its OWN database file (<see cref="IsolatedDatabase"/>), and the session is handed that
    /// store: the lock is held across the whole 0x4F round trip, which is the five-second window plus the
    /// assertions after it, and on the process database every other collection would be locked out for all
    /// of it.</para>
    /// </summary>
    [Fact]
    public void FailedUnconditionalSave_IsRetriedByTheNextFlush_AndNotReportedAsSaved()
    {
        const string blurb = "written while the database was locked";

        using var db = new IsolatedDatabase();

        TestProcessState.LoadContent();      // World's constructor reads the spawn roster out of Content
        Assert.True(db.Store.SaveMany(new[] { Make(_a, 100) }));

        var character = new Character
        {
            SchemaVersion = Character.CurrentSchemaVersion,
            Name = _a,
        };
        var outbound = new RecordingOutbound($"recorder:{_a}");
        // The five-argument constructor is the one that hands the session a loaded character, which is what
        // sets _enteredWorld — and _enteredWorld is what gates StoreSave at the 0x4F handler.
        var session = new Session(outbound, 2005, db.Store, new World(), character);
        outbound.Clear();

        // A second thread holds the write lock until this one says it may let go, so the save below fails
        // for the real reason (SQLITE_BUSY after the provider's window) rather than a synthetic error.
        using var locked = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            using var blocker = db.Open();
            using var begin = blocker.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();
            locked.Set();
            release.Wait(TimeSpan.FromSeconds(60));
            using var rollback = blocker.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }) { IsBackground = true };
        holder.Start();
        Assert.True(locked.Wait(TimeSpan.FromSeconds(30)), "the blocking thread never took the write lock");

        session.Receive(SessionFixture.Frame(ClientOp.ChangeProfile, ChangeProfileBody(blurb)));

        // The player is told the truth: not "saved", and in plain words that it will be written again.
        string reply = MessageText(Assert.Single(outbound.BodiesOf(0x02)));
        Assert.DoesNotContain("has been saved", reply);
        Assert.Contains("saved again shortly", reply);

        // Nothing reached the database, and the session knows it still owes a write.
        Assert.NotEqual(blurb, LoadOk(db.Store, _a).ProfileText);
        Assert.Contains("dirty True", session.DiagState());

        release.Set();
        Assert.True(holder.Join(TimeSpan.FromSeconds(30)), "the blocking thread never released the write lock");

        // The retry the dirty flag buys: the same call World.AutoSaveLoop makes on an idle dirty session.
        session.FlushNow();
        Assert.Equal(blurb, LoadOk(db.Store, _a).ProfileText);
    }

    /// <summary>
    /// #88: a SUCCESSFUL unconditional save leaves the session the way a successful gated flush does — clean,
    /// with the AutoSaveMs throttle reset — because it has just written the whole character. Before, the flag
    /// stayed up and the throttle stayed put, so the next <c>FlushIfDue</c> or autosave sweep wrote the
    /// identical row again, at once.
    ///
    /// <para>Driven through the real 0x4F change-profile frame, the same <c>StoreSave</c> the fact above
    /// fails. The session is dirtied first, which is the state any of the twelve call sites can be in when
    /// its edit lands (a pickup or a step earlier in the same interval).</para>
    ///
    /// <para>"No redundant write" is asserted as a write, not only as a flag: after the save, a probe value is
    /// put into the character WITHOUT <c>MarkDirty</c> and <c>FlushNow</c> — the sweep's call — is run. A
    /// flush that still thought something was pending writes the probe; one that knows the row is current
    /// writes nothing, and the row keeps the profile.</para>
    ///
    /// <para>Falsified by restoring the old gating — <c>if (dirtyGated) { if (!_dirty) return true; _dirty =
    /// false; }</c> and <c>if (dirtyGated) _lastSaveAtMs = ...</c> in <c>CaptureAndWrite</c>: red on the flag,
    /// "Assert.Contains() Failure: Sub-string not found / Not found: dirty False". With the flag restored and
    /// only the throttle line reverted, red on the throttle, "Assert.InRange() Failure: Value not in range /
    /// Range: (…) / Actual: 0".</para>
    /// </summary>
    [Fact]
    public void SuccessfulUnconditionalSave_ClearsTheDirtyFlag_AndResetsTheAutosaveThrottle()
    {
        const string blurb = "written once, not twice";

        TestProcessState.LoadContent();      // World's constructor reads the spawn roster out of Content
        Assert.True(_store.SaveMany(new[] { Make(_a, 100) }));

        var character = new Character
        {
            SchemaVersion = Character.CurrentSchemaVersion,
            Name = _a,
        };
        var outbound = new RecordingOutbound($"recorder:{_a}");
        var session = new Session(outbound, 2005, _store, new World(), character);
        outbound.Clear();

        // A mutation is already pending, and nothing has been written by this session yet.
        session.WithState(session.MarkDirty);
        Assert.Contains("dirty True", session.DiagState());
        Assert.Equal(0, session.LastSaveAtMsForTest);

        long before = Environment.TickCount64;
        session.Receive(SessionFixture.Frame(ClientOp.ChangeProfile, ChangeProfileBody(blurb)));
        long after = Environment.TickCount64;

        // The save itself is unchanged: it landed and the player is told so.
        Assert.Contains("has been saved", MessageText(Assert.Single(outbound.BodiesOf(0x02))));
        Assert.Equal(blurb, LoadOk(_a).ProfileText);

        // The whole row just landed, so nothing is pending and the throttle counts from this write.
        Assert.Contains("dirty False", session.DiagState());
        Assert.InRange(session.LastSaveAtMsForTest, before, after);

        // And the sweep's next call writes nothing: the unmarked probe never reaches the row.
        session.WithState(() => character.ProfileText = "probe: a second write happened");
        session.FlushNow();
        Assert.Equal(blurb, LoadOk(_a).ProfileText);
    }

    /// <summary>The 0x4F body the profile editor sends: picSize(u16 BE) pic[] blurbLen(u8) blurb[] 00. No
    /// picture here — the blurb is the part that has to survive, and an empty picture is a legal frame.</summary>
    private static byte[] ChangeProfileBody(string blurb)
    {
        byte[] text = Encoding.ASCII.GetBytes(blurb);
        var body = new List<byte> { 0x00, 0x00, (byte)text.Length };
        body.AddRange(text);
        body.Add(0x00);
        return body.ToArray();
    }

    /// <summary>The text out of a 0x02 message body: kind(0x0F) len(u8) text[] 00.</summary>
    private static string MessageText(byte[] body) => Encoding.ASCII.GetString(body, 2, body[1]);

    /// <summary>The parcel guarantee: the queue row and the character save commit together. Here the
    /// callback reports failure, standing in for "the parcel was already claimed by another path".</summary>
    [Fact]
    public void SaveWith_RollsBackTheRowAndTheCharacter_WhenTheWorkFails()
    {
        var a = Make(_a, 100);
        Assert.True(_store.SaveMany(new[] { a }));

        // Put a parcel-shaped row in the queue.
        using (var cn = Db.Open())
        {
            using var cmd = cn.CreateCommand();
            cmd.CommandText = @"INSERT INTO parcels(recipient, position, sender, item_id, item_amount, item_dura, engrave, month, day)
                                VALUES($r, 1, 'tester', 5, 3, 0, '', 1, 1);";
            cmd.Parameters.AddWithValue("$r", _a);
            cmd.ExecuteNonQuery();
        }

        // A claim that deletes the row, credits the character, then reports failure. Both halves must revert.
        a.Coins = 999;
        bool committed = _store.SaveWith(a, (cn, tx) =>
        {
            using var del = cn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM parcels WHERE recipient=$r COLLATE NOCASE AND position=1;";
            del.Parameters.AddWithValue("$r", _a);
            Assert.Equal(1, del.ExecuteNonQuery());
            return false;   // "already claimed" / the work decided not to proceed
        });

        Assert.False(committed);
        Assert.Equal(100u, LoadOk(_a).Coins);   // the character save rolled back with it
        Assert.Equal(1, ParcelCount(_a));             // and the parcel is still in the queue

        // Cleanup.
        using (var cn = Db.Open())
        {
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "DELETE FROM parcels WHERE recipient=$r COLLATE NOCASE;";
            cmd.Parameters.AddWithValue("$r", _a);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>And the committing path: the row goes and the character change stays.</summary>
    [Fact]
    public void SaveWith_CommitsBothHalvesTogether()
    {
        var a = Make(_a, 100);
        Assert.True(_store.SaveMany(new[] { a }));

        using (var cn = Db.Open())
        {
            using var cmd = cn.CreateCommand();
            cmd.CommandText = @"INSERT INTO parcels(recipient, position, sender, item_id, item_amount, item_dura, engrave, month, day)
                                VALUES($r, 1, 'tester', 5, 3, 0, '', 1, 1);";
            cmd.Parameters.AddWithValue("$r", _a);
            cmd.ExecuteNonQuery();
        }

        a.Coins = 400;
        bool committed = _store.SaveWith(a, (cn, tx) =>
        {
            using var del = cn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM parcels WHERE recipient=$r COLLATE NOCASE AND position=1;";
            del.Parameters.AddWithValue("$r", _a);
            return del.ExecuteNonQuery() == 1;
        });

        Assert.True(committed);
        Assert.Equal(400u, LoadOk(_a).Coins);
        Assert.Equal(0, ParcelCount(_a));
    }

    /// <summary>Facing survives a save/load round trip.
    ///
    /// <para>It didn't used to exist on the character at all — <c>Session._facing</c> was session-local, so
    /// every login snapped the player back to north no matter which way they walked out. The character blob is
    /// System.Text.Json over public FIELDS, and a field that isn't on <see cref="Character"/> serialises to
    /// nothing at all silently, which is exactly how that stayed invisible.</para></summary>
    [Fact]
    public void Dir_SurvivesASaveAndLoad()
    {
        var c = Make(_a, 0);
        c.Dir = 3;                                   // 0=N 1=E 2=S 3=W — anything but the default
        Assert.True(_store.Save(c));

        Assert.Equal(3, LoadOk(_a).Dir);
    }

    /// <summary>
    /// The checked-in blob is the rename alarm. It contains every current <see cref="Character"/> field and
    /// populated instances of every nested persisted type. Renaming a field without first consuming its old
    /// name in <see cref="CharacterUpgrader"/> therefore leaves an unmapped fixture member and fails here;
    /// do not weaken or delete this test when adding an upgrade step.
    /// </summary>
    [Fact]
    public void CurrentFixture_RoundTripsThroughTheUpgraderWithNoUnmappedMembers()
    {
        string json = File.ReadAllText(FixturePath());

        var character = CharacterStore.Deserialize(json);

        Assert.Equal(Character.CurrentSchemaVersion, character.SchemaVersion);
        Assert.Equal("FixtureHero", character.Name);
        Assert.Equal(123456u, character.Coins);
        Assert.Equal("Keepsake", character.Inventory.Single().CustomName);
        Assert.Equal("fixture-buff", character.Effects.Buffs.Single().Key);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(json),
            JsonNode.Parse(CharacterStore.Serialize(character))));
    }

    [Fact]
    public void SchemaZeroBlob_IsStampedBeforeStrictDeserialization()
    {
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(FixturePath())));
        Assert.True(root.Remove(nameof(Character.SchemaVersion)));

        var character = CharacterStore.Deserialize(root.ToJsonString());

        Assert.Equal(Character.CurrentSchemaVersion, character.SchemaVersion);
        Assert.Equal("FixtureHero", character.Name);
    }

    [Fact]
    public void LegacyJsonImport_UsesTheRawJsonUpgrader()
    {
        string legacyDir = Path.Combine(_fixture.StateDirectory, $"legacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(legacyDir);
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(FixturePath())));
        Assert.True(root.Remove(nameof(Character.SchemaVersion)));
        root[nameof(Character.Name)] = _a;
        File.WriteAllText(Path.Combine(legacyDir, $"{_a}.json"), root.ToJsonString());

        var importingStore = new CharacterStore(legacyDir);
        var load = importingStore.Load(_a);

        Assert.Equal(CharacterLoadStatus.Ok, load.Status);
        Assert.Equal(Character.CurrentSchemaVersion, Assert.IsType<Character>(load.Character).SchemaVersion);
        Assert.Equal(Character.CurrentSchemaVersion,
            Assert.IsType<JsonObject>(JsonNode.Parse(ReadRawCharacter(_a)))[nameof(Character.SchemaVersion)]!.GetValue<int>());
    }

    [Fact]
    public void UnknownPersistedMember_IsRejectedInsteadOfSilentlyDropped()
    {
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(FixturePath())));
        root["FieldRenamedWithoutUpgrade"] = 123;

        Assert.Throws<JsonException>(() => CharacterStore.Deserialize(root.ToJsonString()));
    }

    /// <summary>An unreadable row is evidence to preserve, not an absent character to recreate. The arrival
    /// path gives the player a distinct message and closes before entering the world; every store write path
    /// independently refuses to replace the row, covering a stale duplicate session that flushes first.</summary>
    [Fact]
    public void UnreadableRow_IsRejectedDistinctlyAndNeverOverwritten()
    {
        const string corrupt = "{\"SchemaVersion\":1,\"Name\":\"FixtureHero\",\"RemovedField\":17}";
        InsertRawCharacter(_a, corrupt);

        var load = _store.Load(_a);
        Assert.Equal(CharacterLoadStatus.Unreadable, load.Status);
        Assert.Null(load.Character);
        Assert.False(string.IsNullOrWhiteSpace(load.Reason));

        Assert.False(_store.Save(Make(_a, 999)));
        Assert.Equal(corrupt, ReadRawCharacter(_a));
        Assert.False(_store.SaveMany(new[] { Make(_a, 888) }));
        bool callbackRan = false;
        Assert.False(_store.SaveWith(Make(_a, 777), (_, _) => callbackRan = true));
        Assert.False(callbackRan);
        Assert.Equal(corrupt, ReadRawCharacter(_a));

        var outbound = new RecordingOutbound();
        var session = new Session(outbound, 2005, _store, new World());
        session.Receive(ArrivalFrame(_a));

        Assert.True(outbound.Closed);
        var message = Assert.Single(outbound.BodiesOf(0x02));
        Assert.Equal("Your character record could not be loaded. Please contact an administrator.",
            Encoding.ASCII.GetString(message, 2, message[1]));
        Assert.Equal(corrupt, ReadRawCharacter(_a));
    }

    [Fact]
    public void UnreadableMarker_IsTheSaveGuardAndACurrentBuildCanClearIt()
    {
        const string corrupt = "{\"SchemaVersion\":1,\"Name\":\"FixtureHero\",\"RemovedField\":17}";
        InsertRawCharacter(_a, corrupt);
        Assert.Equal(CharacterLoadStatus.Unreadable, _store.Load(_a).Status);
        Assert.NotNull(ReadUnreadableSince(_a));

        // Make the bytes readable without touching the marker. A writer that reparses inside its transaction
        // would now overwrite this row; the marker-based guard must still refuse it.
        string current = CharacterStore.Serialize(Make(_a, 123));
        ReplaceRawJson(_a, current);
        Assert.False(_store.Save(Make(_a, 999)));
        Assert.Equal(current, ReadRawCharacter(_a));

        // A later build that can read a formerly bad row removes the stale marker during Load, after which
        // normal saves resume.
        Assert.Equal(CharacterLoadStatus.Ok, _store.Load(_a).Status);
        Assert.Null(ReadUnreadableSince(_a));
        Assert.True(_store.Save(Make(_a, 999)));
    }

    [Fact]
    public void RefusedOverwrite_ReachesTheConfiguredWarningSink()
    {
        const string corrupt = "{\"SchemaVersion\":1,\"Name\":\"FixtureHero\",\"RemovedField\":17}";
        InsertRawCharacter(_a, corrupt);
        Assert.Equal(CharacterLoadStatus.Unreadable, _store.Load(_a).Status);

        var warnings = new ConcurrentQueue<string>();
        var previous = CharacterStore.Warn;
        try
        {
            CharacterStore.Warn = ChainedWarnSink(warnings, previous);
            Assert.False(_store.Save(Make(_a, 999)));
        }
        finally
        {
            CharacterStore.Warn = previous;
        }

        string warning = Assert.Single(warnings, w => w.Contains(CharacterStore.Key(_a), StringComparison.Ordinal));
        Assert.Contains("Refused to overwrite unreadable character row", warning);
    }

    /// <summary>A warning sink for the two facts that capture <see cref="CharacterStore.Warn"/>. That property is
    /// one process-wide slot, and this collection runs beside <c>"world"</c>, whose write-lock facts fail saves
    /// on purpose: a plain <c>warnings.Add</c> in the slot would count their warnings as this fact's (the old
    /// <c>Assert.Single</c> would then fail on someone else's row) and would be a <c>List</c> written from two
    /// threads. So the capture is a concurrent queue, every warning still goes on to the sink that was there
    /// before, and each fact asserts on the warnings that name its own row or file.</summary>
    private static Action<string> ChainedWarnSink(ConcurrentQueue<string> into, Action<string> previous) =>
        message =>
        {
            into.Enqueue(message);
            previous(message);
        };

    [Fact]
    public void LegacyImport_ReportsTheFileItCannotUpgrade()
    {
        string legacyDir = Path.Combine(_fixture.StateDirectory, $"legacy-bad-{Guid.NewGuid():N}");
        Directory.CreateDirectory(legacyDir);
        string path = Path.Combine(legacyDir, "stale.json");
        File.WriteAllText(path,
            "{\"SchemaVersion\":1,\"Name\":\"FixtureHero\",\"RemovedField\":17}");

        var warnings = new ConcurrentQueue<string>();
        var previous = CharacterStore.Warn;
        try
        {
            CharacterStore.Warn = ChainedWarnSink(warnings, previous);
            _ = new CharacterStore(legacyDir);
        }
        finally
        {
            CharacterStore.Warn = previous;
        }

        string warning = Assert.Single(warnings, w => w.Contains(path, StringComparison.Ordinal));
        Assert.Contains("RemovedField", warning);
    }

    [Fact]
    public void DatabaseMigrations_AreVersionedAndIdempotent()
    {
        string missingColumnsPath = Path.Combine(
            _fixture.StateDirectory,
            $"migration-missing-{Guid.NewGuid():N}.db");
        using (var legacy = new SqliteConnection($"Data Source={missingColumnsPath}"))
        {
            legacy.Open();
            using var schema = legacy.CreateCommand();
            schema.CommandText = @"
CREATE TABLE handoff_tokens (
  nonce_hash TEXT PRIMARY KEY,
  username TEXT NOT NULL,
  expires_utc INTEGER NOT NULL,
  consumed INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE parcels (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  recipient TEXT NOT NULL COLLATE NOCASE,
  position INTEGER NOT NULL,
  sender TEXT,
  item_id INTEGER NOT NULL DEFAULT -1,
  item_amount INTEGER NOT NULL DEFAULT 0,
  item_dura INTEGER NOT NULL DEFAULT 0,
  engrave TEXT,
  month INTEGER,
  day INTEGER
);";
            schema.ExecuteNonQuery();
        }

        Db.InitializeDatabase(missingColumnsPath);
        Db.InitializeDatabase(missingColumnsPath);
        AssertMigrationState(missingColumnsPath);

        // Every deployed database already has the columns declared by CREATE TABLE, but predates the
        // user_version stamp. This is the real migration shape and exercises every ColumnExists skip.
        string deployedShapePath = Path.Combine(
            _fixture.StateDirectory,
            $"migration-deployed-{Guid.NewGuid():N}.db");
        Db.InitializeDatabase(deployedShapePath);
        using (var deployed = new SqliteConnection($"Data Source={deployedShapePath}"))
        {
            deployed.Open();
            using var resetVersion = deployed.CreateCommand();
            resetVersion.CommandText = "PRAGMA user_version = 0;";
            resetVersion.ExecuteNonQuery();
        }

        Db.InitializeDatabase(deployedShapePath);
        Db.InitializeDatabase(deployedShapePath);
        AssertMigrationState(deployedShapePath);
    }

    private static string FixturePath() =>
        Path.Combine(RepoPaths.Root(), "Tests", "Fixtures", "character-v1.json");

    private static byte[] ArrivalFrame(string user) =>
        Handoff.ArrivalFrame(user, System.Net.IPAddress.None.ToString());

    private static void InsertRawCharacter(string user, string json)
    {
        using var cn = Db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO characters(username, json, updated_utc) VALUES($u, $j, 1);";
        cmd.Parameters.AddWithValue("$u", CharacterStore.Key(user));
        cmd.Parameters.AddWithValue("$j", json);
        cmd.ExecuteNonQuery();
    }

    private static string ReadRawCharacter(string user)
    {
        using var cn = Db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT json FROM characters WHERE username=$u;";
        cmd.Parameters.AddWithValue("$u", CharacterStore.Key(user));
        return Assert.IsType<string>(cmd.ExecuteScalar());
    }

    private static long? ReadUnreadableSince(string user)
    {
        using var cn = Db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT unreadable_since FROM characters WHERE username=$u;";
        cmd.Parameters.AddWithValue("$u", CharacterStore.Key(user));
        object? value = cmd.ExecuteScalar();
        return value is long timestamp ? timestamp : null;
    }

    private static void ReplaceRawJson(string user, string json)
    {
        using var cn = Db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "UPDATE characters SET json=$j WHERE username=$u;";
        cmd.Parameters.AddWithValue("$j", json);
        cmd.Parameters.AddWithValue("$u", CharacterStore.Key(user));
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    private static int ColumnCount(SqliteConnection cn, string table, string column)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$column;";
        cmd.Parameters.AddWithValue("$column", column);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void AssertMigrationState(string path)
    {
        using var cn = new SqliteConnection($"Data Source={path}");
        cn.Open();
        using var version = cn.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(Db.CurrentSchemaVersion, Convert.ToInt32(version.ExecuteScalar()));
        Assert.Equal(1, ColumnCount(cn, "handoff_tokens", "ip"));
        Assert.Equal(1, ColumnCount(cn, "parcels", "item_owner"));
        Assert.Equal(1, ColumnCount(cn, "characters", "unreadable_since"));
        Assert.Equal(1, ColumnCount(cn, "handoff_tokens", "key_hash"));
    }

    private static int ParcelCount(string recipient)
    {
        using var cn = Db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM parcels WHERE recipient=$r COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$r", recipient);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
