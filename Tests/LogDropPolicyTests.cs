using Server;
using Xunit;

namespace Tests;

public class LogDropPolicyTests
{
    [Fact]
    public void Reserved_threshold_refuses_info_but_admits_warn_and_error()
    {
        int queued = Log.QueueCapacity - Log.ReservedLines;

        Assert.False(Log.Admits(LogLevel.Info, queued));
        Assert.True(Log.Admits(LogLevel.Warn, queued));
        Assert.True(Log.Admits(LogLevel.Error, queued));
    }

    [Fact]
    public void Full_capacity_refuses_every_level()
    {
        foreach (LogLevel level in Enum.GetValues<LogLevel>())
            Assert.False(Log.Admits(level, Log.QueueCapacity));
    }

    [Fact]
    public void Below_reserved_threshold_admits_every_level()
    {
        foreach (LogLevel level in Enum.GetValues<LogLevel>())
            Assert.True(Log.Admits(level, Log.QueueCapacity - Log.ReservedLines - 1));
    }
}
