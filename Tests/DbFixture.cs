using Xunit;

namespace Tests;

/// <summary>
/// The database collection does not run alongside any other collection, and that is load-bearing rather than
/// tidiness: <c>PersistenceTests.SaveMany_LeavesNothingWritten_WhenTheWriteFails</c> deliberately takes
/// SQLite's write lock (<c>BEGIN IMMEDIATE</c>) and holds it while a save is attempted against it, and there
/// is ONE database file for the whole test process (<c>TestProcessState</c> points P1998_STATE at a single
/// per-run temp directory). A database-wide write lock is therefore a database-wide write lock for every
/// other collection running at that moment.
///
/// <para>The window is not the 5s of <c>Db.Open</c>'s <c>busy_timeout</c>, which is what makes this worth
/// spelling out: Microsoft.Data.Sqlite retries a busy statement up to the COMMAND timeout — 30s by default —
/// so a concurrent save waits half a minute before it fails. That is what fork CI run 35170089187 attempt 1
/// hit: <c>[db] !! SaveMany(2) failed: SQLite Error 5: 'database is locked'</c> from the fact that holds the
/// lock (30s on that runner), and then <c>AnnounceMonitorTests</c>' setup save failing the same way from the
/// parallel <c>world</c> collection, which reported it as "@ban refuses a name with no character row".</para>
///
/// <para>Giving that one fact its own database file would be the other answer, and it is not available from
/// the test side: <c>CharacterStore</c> writes through <c>Db.Open()</c>, whose path is a process-wide static
/// with no seam. Serializing this collection costs the suite the db collection's own runtime once and removes
/// the overlap entirely.</para>
/// </summary>
[CollectionDefinition("db", DisableParallelization = true)]
public sealed class DbCollection : ICollectionFixture<DbFixture> { }

public sealed class DbFixture
{
    public string StateDirectory => TestProcessState.StateDirectory;
}
