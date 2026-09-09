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

    [Theory]
    [InlineData(12_431, 0, 0, "!! log queue overflowed — dropped 12,431 info, 0 warn, 0 error")]
    [InlineData(0, 3, 1, "!! log queue overflowed — dropped 0 info, 3 warn, 1 error")]
    [InlineData(0, 0, 0, "")]
    public void Overflow_notice_names_each_dropped_level(
        int info, int warn, int error, string expected)
    {
        Assert.Equal(expected, Log.FormatOverflowNotice(info, warn, error));
    }

    [Fact]
    public void Real_entry_points_count_refused_info_and_warn_separately()
    {
        Log.ResetDroppedCountsForTest();
        Log.AdmitOverrideForTest = (_, _) => false;
        try
        {
            Log.Info("refused info test record");
            Log.Warn("refused warn test record");

            Assert.Equal((1, 1, 0), Log.DroppedCountsForTest());
        }
        finally
        {
            Log.AdmitOverrideForTest = null;
            Log.ResetDroppedCountsForTest();
        }
    }
}
