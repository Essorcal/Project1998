using Server;
using Xunit;

namespace Tests;

/// <summary>Drop policy for <see cref="Log"/>'s bounded queue.
/// <para><b>Any test that touches <see cref="Log"/>'s statics must join collection "log".</b> The counters,
/// the admission override and the file sink are process-global, so two classes exercising them in parallel
/// read each other's records: with the override set, every line the rest of the suite logs lands in
/// <c>DroppedByLevel</c> (measured at ~240 records/second during a full run), and a second class repeating
/// the entry-point fact broke it outright. xUnit runs one collection at a time, which is the only thing that
/// keeps these facts honest.</para></summary>
[Collection("log")]
public class LogDropPolicyTests
{
    // The stamp Log's entry points write before the message text, and so before any hand-written marker.
    private const string Stamp = "[12:34:56.789] ";

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

    [Fact]
    public void Level_is_the_max_of_the_entry_point_and_the_hand_written_marker()
    {
        // The marker is read at the offset the entry points stamp; if that format ever changes, this fails
        // here rather than by quietly demoting every hand-prefixed line back to Info.
        Assert.Equal(Log.StampLength, $"[{DateTime.Now:HH:mm:ss.fff}] ".Length);

        Assert.Equal(LogLevel.Info, Log.LevelOf(Stamp + "walking 3,4 -> 3,5", LogLevel.Info));
        Assert.Equal(LogLevel.Warn, Log.LevelOf(Stamp + "!! SLOW TICK: work 900ms", LogLevel.Info));
        Assert.Equal(LogLevel.Error, Log.LevelOf(Stamp + "!!! FATAL unhandled exception", LogLevel.Info));

        // The trailing space is part of the marker: "!!!" must not be read as "!!", and an unspaced run of
        // bangs is not a marker at all.
        Assert.Equal(LogLevel.Info, Log.LevelOf(Stamp + "!!!no space", LogLevel.Info));
        Assert.Equal(LogLevel.Info, Log.LevelOf(Stamp + "!!no space", LogLevel.Info));

        // The level can only rise. Detail(e)'s indented continuation text carries no marker, and a Warn
        // marker cannot pull an Error entry point down.
        Assert.Equal(LogLevel.Error, Log.LevelOf(Stamp + "      System.InvalidOperationException: boom", LogLevel.Error));
        Assert.Equal(LogLevel.Error, Log.LevelOf(Stamp + "!! reads like a warning", LogLevel.Error));
        Assert.Equal(LogLevel.Warn, Log.LevelOf("open", LogLevel.Warn));
    }

    [Fact]
    public void Prefix_derived_level_keeps_the_reserve_for_hand_written_diagnostics()
    {
        int queued = Log.QueueCapacity - Log.ReservedLines;

        Assert.False(Log.Admits(Log.LevelOf(Stamp + "walking 3,4 -> 3,5", LogLevel.Info), queued));
        Assert.True(Log.Admits(Log.LevelOf(Stamp + "!! SLOW TICK: work 900ms", LogLevel.Info), queued));
        Assert.True(Log.Admits(Log.LevelOf(Stamp + "!!! FATAL unhandled exception", LogLevel.Info), queued));
    }

    [Fact]
    public void Hand_prefixed_info_lines_are_refused_as_warnings_and_errors()
    {
        Log.ResetDroppedCountsForTest();
        Log.AdmitOverrideForTest = (_, _) => false;
        try
        {
            Log.Info("!! hand-written warning");
            Log.Info("!!! hand-written error");

            Assert.Equal((0, 1, 1), Log.DroppedCountsForTest());
        }
        finally
        {
            Log.AdmitOverrideForTest = null;
            Log.ResetDroppedCountsForTest();
        }
    }

    [Fact]
    public void The_file_control_line_is_admitted_even_when_every_record_is_refused()
    {
        // AttachFile hands the path to the writer thread as a control line. There is no detach, so this
        // deliberately leaves the sink open and the temp directory behind rather than fight an open handle;
        // the cost is that the rest of this process's log lines are also teed to that file.
        string path = Path.Combine(
            Path.GetTempPath(), "p1998-log-marker-" + Guid.NewGuid().ToString("N"), "server.log");

        Log.AdmitOverrideForTest = (_, _) => false;
        try
        {
            Log.AttachFile(path);

            // The writer thread owns the handle, so wait for it rather than assume it has run.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(path) && DateTime.UtcNow < deadline) Thread.Sleep(10);

            Assert.True(File.Exists(path), $"the control line was refused: {path} never opened");
        }
        finally
        {
            Log.AdmitOverrideForTest = null;
        }
    }
}
