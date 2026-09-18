using Xunit;

namespace Tests;

/// <summary>
/// The database collection: the classes that write to the ONE database file the test process has
/// (<c>TestProcessState</c> points P1998_STATE at a single per-run temp directory, so <c>Db.Path</c> is the
/// same file for every collection). Grouping them keeps their row cleanup and their account/handoff/board
/// keys from interleaving with each other — <c>BoardWireTests</c> in particular asserts on the AUTOINCREMENT
/// id of the first board post it writes.
///
/// <para>What this collection no longer has to guarantee is exclusivity. It ran with
/// <c>DisableParallelization = true</c> because four facts in <c>PersistenceTests</c> take SQLite's write
/// lock with <c>BEGIN IMMEDIATE</c> to prove what a contended save does, that lock is per FILE, and with one
/// file in the process it was a lock on every other collection's writes too. Fork CI run 35170089187
/// attempt 1 is the record of that: a held lock here, and <c>AnnounceMonitorTests</c> over in <c>world</c>
/// reporting the resulting failed setup save as "@ban refuses a name with no character row". Those four
/// facts now run against a database file of their own (<c>Tests/Support/IsolatedDatabase.cs</c>), so nothing
/// outside them can feel the lock and the collection can run beside the others again.</para>
///
/// <para>The bound on a write that IS locked out is about five seconds — <c>Db.BusyTimeoutMs</c>. Both
/// halves of it come from that one constant: <c>Db.Open</c> sets the connection's <c>DefaultTimeout</c> as
/// well as <c>PRAGMA busy_timeout</c>, because Microsoft.Data.Sqlite re-runs a statement that came back
/// SQLITE_BUSY up to the COMMAND timeout and the pragma alone bounds nothing a caller can observe. The
/// command timeout was still the provider's 30s default on the CI run above, which is why the lock-out
/// there cost half a minute rather than five seconds.</para>
///
/// <para>So the rule for a new fact here: an ordinary write is fine, and a fact that deliberately holds a
/// database-wide lock takes an <c>IsolatedDatabase</c>, never <c>Db.Open()</c>. Anything that cannot — a
/// fact that has to lock the process database specifically — needs this attribute back, and the reason
/// written down.</para>
/// </summary>
[CollectionDefinition("db")]
public sealed class DbCollection : ICollectionFixture<DbFixture> { }

public sealed class DbFixture
{
    public string StateDirectory => TestProcessState.StateDirectory;
}
