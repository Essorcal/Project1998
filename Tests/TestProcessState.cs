using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Server;
using Shared;
using Tests.Support;

namespace Tests;

/// <summary>Serializes test-only changes to process-wide configuration with content loads that consume it.
/// xUnit can still run every unrelated test collection in parallel.</summary>
internal static class TestProcessState
{
    public static string StateDirectory { get; private set; } = null!;
    public static string ProductionDatabasePath { get; private set; } = null!;
    public static bool ProductionDatabaseExisted { get; private set; }
    public static DateTime ProductionDatabaseLastWriteTimeUtc { get; private set; }

    /// <summary>This run's scratch folder (<see cref="TestScratch"/>): a test that makes a file or a folder of its own
    /// makes it in here, never straight in the temp directory, and the run deletes the folder at its end. The state
    /// directory is its <c>state</c> folder.</summary>
    public static string ScratchDirectory => Scratch.Folder;

    /// <summary>This process's run.</summary>
    internal static TestScratch Scratch { get; private set; } = null!;

    /// <summary>Whether <see cref="EndRun"/> has run in this process.</summary>
    internal static bool RunEnded { get; private set; }

    /// <summary>How long the run's end keeps trying to delete its folder before it leaves the rest to the next run's
    /// sweep. A full run's folder goes in well under a second; the retries are for the handles this process still
    /// holds when the last test finishes (<see cref="EndRun"/>).</summary>
    private static readonly TimeSpan EndPatience = TimeSpan.FromSeconds(5);

    [ModuleInitializer]
    internal static void Initialize()
    {
        ProductionDatabasePath = Path.Combine(RepoPaths.Root(), "state", "project1998.db");
        ProductionDatabaseExisted = File.Exists(ProductionDatabasePath);
        if (ProductionDatabaseExisted)
            ProductionDatabaseLastWriteTimeUtc = File.GetLastWriteTimeUtc(ProductionDatabasePath);

        Scratch = TestScratch.Start(TestScratch.DefaultRoot,
                                    TestScratch.KeepRequested(Environment.GetEnvironmentVariable(TestScratch.KeepVariable)));
        StateDirectory = Path.Combine(Scratch.Folder, "state");
        Directory.CreateDirectory(StateDirectory);
        Environment.SetEnvironmentVariable("P1998_STATE", StateDirectory);
        // The terrain search stops at game-data/maps for the whole run. With the client-install fallback on, a
        // map missing from the content would be found in a developer's retail install and
        // ContentSmokeTests.EveryMapHasItsTerrainFile would pass on that box while a Linux host had nothing.
        Environment.SetEnvironmentVariable("P1998_MAPS_INSTALL_FALLBACK", "0");
        // ServerConfig reads the environment ONCE and caches it, so the two settings above only take effect if
        // nothing sampled the configuration before this initializer ran. Re-resolving makes that explicit
        // rather than depending on static-initialization order: without it, one early RepoPaths.StateDir()
        // anywhere in the process would pin the snapshot to the REAL state directory and every test that
        // saves a character would write into the live deployment.
        ServerConfig.ReloadForTests();

        // The runs before this one: the folders of those that have ended go; this run's and a live run's never do.
        // Never a throw from here, either, which would fail every test in the process over another run's folder.
        try { TestScratch.Sweep(TestScratch.DefaultRoot, DateTime.UtcNow); }
        catch { /* best-effort: the next run sweeps again */ }

        // A backstop, not the run's end (EndRun is, from ScratchTestFramework): a process that never ran a test, such
        // as a discovery, still goes here. vstest kills the host about 100 ms after the run reports finished, so this
        // may be cut off; the next run's sweep deletes whatever it leaves.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                SqliteConnection.ClearAllPools();
                Scratch.End(TimeSpan.Zero);
            }
            catch { /* best-effort, as above */ }
        };
    }

    /// <summary>The run's end, from <see cref="ScratchTestFramework"/> once every test has finished: the run's folder is
    /// deleted, unless the keep switch is on. Returns null when the folder is gone or kept, else what stayed.
    /// <para>Two kinds of handle this process may still hold in the folder are let go of first, or between tries:
    /// pooled SQLite connections, and the log's file sink. A log fact attaches the log to a file in this folder, and
    /// the log has no detach, so that file stays open, and Windows will not delete an open file. Attaching the log to
    /// the null device leaves the writer that held it unreferenced, and a collection closes its handle. It runs after
    /// every test, beside nothing, which is why it may touch the log's state here (<c>TestSeamCollectionTests</c>
    /// exempts this class for the same reason as its module initializer).</para></summary>
    internal static string? EndRun()
    {
        RunEnded = true;
        if (Scratch.Keep) return null;
        SqliteConnection.ClearAllPools();
        bool logReleased = false;
        bool gone = Scratch.End(EndPatience, release: () =>
        {
            SqliteConnection.ClearAllPools();
            if (!logReleased)
            {
                Log.AttachFile(OperatingSystem.IsWindows() ? "NUL" : "/dev/null");
                logReleased = true;
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        });
        return gone
            ? null
            : $"this run's scratch folder was not wholly deleted at the run's end ({Scratch.Folder}); the next run's sweep deletes the rest";
    }

    // Some gated facts take Gate -> their class's private _gate while EnsureLoaded takes _gate -> Gate; this is
    // safe only because every _gate is private to one xUnit class and therefore never participates cross-class.
    public static object Gate { get; } = new();

    public static void LoadContent()
    {
        lock (Gate) Content.Load();
    }
}
