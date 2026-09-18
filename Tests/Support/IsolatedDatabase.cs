using Microsoft.Data.Sqlite;
using Shared;

namespace Tests.Support;

/// <summary>
/// A database file of this test's own, with its own <see cref="CharacterStore"/> on it.
///
/// <para>The reason it exists: SQLite's write lock is per FILE, and the test process has exactly one file —
/// <c>TestProcessState</c> points P1998_STATE at a single per-run temp directory, so every collection writes
/// to the same <c>Db.Path</c>. A fact that takes <c>BEGIN IMMEDIATE</c> to prove what a failed save does
/// therefore locks out every test running beside it, which is how fork CI run 35170089187 attempt 1 turned a
/// held write lock in <c>PersistenceTests</c> into "@ban refuses a name with no character row" over in
/// <c>AnnounceMonitorTests</c>. A lock taken on this file is felt by nothing but this test.</para>
///
/// <para>The directory lives INSIDE the process state directory, so the process-exit cleanup that already
/// removes that tree removes these too. <see cref="Dispose"/> deletes it eagerly and gives up quietly if the
/// file is still held: a leaked temp file inside a directory that is about to be deleted is not worth failing
/// a test over, and pooled connections belonging to other collections must not be closed to hurry it.</para>
/// </summary>
internal sealed class IsolatedDatabase : IDisposable
{
    private readonly string _dir;

    public IsolatedDatabase()
    {
        _dir = System.IO.Path.Combine(TestProcessState.StateDirectory, $"isolated-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(_dir);
        Path = System.IO.Path.Combine(_dir, "project1998.db");
        // Builds the schema on this file; the process database is not opened, initialized or touched.
        Store = CharacterStore.ForDatabase(System.IO.Path.Combine(_dir, "chars"), Path);
    }

    /// <summary>The database file. Never <c>Db.Path</c>.</summary>
    public string Path { get; }

    /// <summary>A store writing to <see cref="Path"/> and nowhere else.</summary>
    public CharacterStore Store { get; }

    /// <summary>A raw connection on the same file, for the tests that take the write lock by hand. Same
    /// timeouts and pragmas as any production connection — the contention being measured has to be the
    /// real one.</summary>
    public SqliteConnection Open() => Db.Open(Path);

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_dir, recursive: true); }
        catch { /* the process-exit cleanup of the state directory takes it */ }
    }
}
