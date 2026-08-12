using Xunit;

// Every test class in this assembly talks to the SAME SQLite file (state/nexus.db) — that is deliberate,
// because the guarantees under test (single-use consume, transactional rollback, WAL behaviour) live in the
// SQL rather than in C#, and a mock would only prove the mock works.
//
// xUnit parallelizes across test COLLECTIONS by default, and shared mutable state plus parallelism is how
// you get a suite that fails one run in five. It bit immediately: PersistenceTests deliberately holds an
// exclusive write lock (BEGIN IMMEDIATE) to prove a failed trade write rolls both characters back, and that
// lock starved HandoffTokenTests in another class until it blew its busy_timeout — a green test failing
// because of an unrelated one running beside it.
//
// Serial execution costs a few seconds and buys a suite whose result means something.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
