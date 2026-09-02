using Server;

namespace Tests;

/// <summary>Serializes test-only changes to process-wide configuration with content loads that consume it.
/// xUnit can still run every unrelated test collection in parallel.</summary>
internal static class TestProcessState
{
    public static object Gate { get; } = new();

    public static void LoadContent()
    {
        lock (Gate) Content.Load();
    }
}
