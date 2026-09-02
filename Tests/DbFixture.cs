using Xunit;

namespace Tests;

[CollectionDefinition("db")]
public sealed class DbCollection : ICollectionFixture<DbFixture> { }

public sealed class DbFixture
{
    public string StateDirectory => TestProcessState.StateDirectory;
}
