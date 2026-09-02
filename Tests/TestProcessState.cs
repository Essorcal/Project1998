using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Server;
using Shared;

namespace Tests;

/// <summary>Serializes test-only changes to process-wide configuration with content loads that consume it.
/// xUnit can still run every unrelated test collection in parallel.</summary>
internal static class TestProcessState
{
    public static string StateDirectory { get; private set; } = null!;
    public static string ProductionDatabasePath { get; private set; } = null!;
    public static bool ProductionDatabaseExisted { get; private set; }
    public static DateTime ProductionDatabaseLastWriteTimeUtc { get; private set; }

    [ModuleInitializer]
    internal static void Initialize()
    {
        ProductionDatabasePath = Path.Combine(RepoPaths.Root(), "state", "project1998.db");
        ProductionDatabaseExisted = File.Exists(ProductionDatabasePath);
        if (ProductionDatabaseExisted)
            ProductionDatabaseLastWriteTimeUtc = File.GetLastWriteTimeUtc(ProductionDatabasePath);

        StateDirectory = Path.Combine(Path.GetTempPath(), $"project1998-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(StateDirectory);
        Environment.SetEnvironmentVariable("P1998_STATE", StateDirectory);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(StateDirectory, recursive: true);
            }
            catch { /* best-effort cleanup after every test and connection has finished */ }
        };
    }

    // Some gated facts take Gate -> their class's private _gate while EnsureLoaded takes _gate -> Gate; this is
    // safe only because every _gate is private to one xUnit class and therefore never participates cross-class.
    public static object Gate { get; } = new();

    public static void LoadContent()
    {
        lock (Gate) Content.Load();
    }
}
