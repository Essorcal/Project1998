using Shared;
using Xunit;

namespace Tests;

[CollectionDefinition("db")]
public sealed class DbCollection : ICollectionFixture<DbFixture> { }

public sealed class DbFixture : IDisposable
{
    private readonly string? _previousStateDirectory;

    public string StateDirectory { get; } =
        Path.Combine(Path.GetTempPath(), $"project1998-tests-{Guid.NewGuid():N}");

    public DbFixture()
    {
        _previousStateDirectory = Environment.GetEnvironmentVariable("P1998_STATE");
        Directory.CreateDirectory(StateDirectory);
        Environment.SetEnvironmentVariable("P1998_STATE", StateDirectory);
        Db.ResetForTests();
    }

    public void Dispose()
    {
        Db.ResetForTests();
        Environment.SetEnvironmentVariable("P1998_STATE", _previousStateDirectory);
        try { Directory.Delete(StateDirectory, recursive: true); }
        catch { /* best-effort cleanup of a per-run temp directory */ }
    }
}
