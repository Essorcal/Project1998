using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The in-process hook for the FORMATTED log line — <c>Log.LineSinkForTest</c> and the
/// <see cref="LogLineSink"/> helper every test uses it through.
///
/// <para><b>What needs a test here.</b> The hook exists so a log assertion can stop attaching the file sink,
/// which means a test believing the hook is the test believing its own evidence. Two ways that goes silently
/// wrong. It could hand over something that is not what got written — a message without the stamp, or a line
/// whose marker the level does not match — and every test built on it would then pin a string the log never
/// contained. Or it could survive its own uninstall, in which case a later test's assertion would be reading
/// lines an earlier test's sink collected. So this file pins the exact string, its level and its thread, and
/// pins that the uninstall is real.</para>
///
/// <para>No <c>[Collection]</c>: this touches no queue, no counter, no file and no writer thread — only the
/// hook, which <see cref="LogLineSink.Acquire"/> makes exclusive on its own. Other tests logging into the
/// sink during the window are expected and harmless; every assertion below is anchored on a per-run
/// GUID.</para>
/// </summary>
public class LogLineSinkTests
{
    /// <summary>The hook receives what the entry point formatted, byte for byte: the stamp
    /// <see cref="Log.StampLength"/> describes, then the marker that entry point writes (none, <c>!!</c>,
    /// <c>!!!</c>), then the text — and the level it is handed is the one <see cref="Log.LevelOf"/> would
    /// read back off that line. All three entry points, because the marker is the thing that differs.
    ///
    /// <para>The last row is the marker rule <c>LevelOf</c> exists for: 25 diagnostic sites in
    /// <c>Server/</c> still hand-write <c>!!</c> through <see cref="Log.Info"/>, and those lines are warnings
    /// whichever method wrote them. A test asserting "this was logged as a warning" has to see that.</para>
    ///
    /// <para>Falsification: assert the message WITHOUT the stamp (<c>Assert.Equal($"info {tag}",
    /// info.Line)</c>) and it fails against the stamped line; or move the hook in <c>Enqueue</c> above
    /// <c>LevelOf</c> and hand it <c>entryPoint</c> instead, and the hand-marked row's level assertion fails
    /// (Info against the expected Warn). Run each re-break, confirm red, restore.</para></summary>
    [Fact]
    public void The_hook_receives_the_formatted_line_and_its_level_for_every_entry_point()
    {
        string tag = "line-sink-" + Guid.NewGuid().ToString("N");

        using var sink = LogLineSink.Acquire();
        Log.Info($"info {tag}");
        Log.Warn($"warn {tag}");
        Log.Error($"error {tag}");
        Log.Info($"!! handmarked {tag}");

        var info = sink.EntryContaining($"info {tag}");
        var warn = sink.EntryContaining($"warn {tag}");
        var error = sink.EntryContaining($"error {tag}");
        var handmarked = sink.EntryContaining($"handmarked {tag}");

        // The stamp is real, and it is the one StampLength describes.
        Assert.Equal(Log.StampLength, $"[{DateTime.Now:HH:mm:ss.fff}] ".Length);
        Assert.Equal($"info {tag}", info.Line.Substring(Log.StampLength));
        Assert.Equal($"!! warn {tag}", warn.Line.Substring(Log.StampLength));
        Assert.Equal($"!!! error {tag}", error.Line.Substring(Log.StampLength));
        Assert.Equal($"!! handmarked {tag}", handmarked.Line.Substring(Log.StampLength));

        // ... and the level handed over is the line's own, not the entry point's.
        Assert.Equal(LogLevel.Info, info.Level);
        Assert.Equal(LogLevel.Warn, warn.Level);
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Equal(LogLevel.Warn, handmarked.Level);   // written through Info; the marker is what decides
    }

    /// <summary>The hook runs synchronously, on the thread that logged — which is what lets a test assert
    /// immediately after the call that caused the line, with no writer thread, no flush and no wait. Pinned
    /// two ways: the line is already collected when <see cref="Log.Info"/> returns, and it carries the id of
    /// the thread that called it, including a thread that is not the test's.
    ///
    /// <para>Falsification: queue the invocation onto the thread pool in <c>Enqueue</c>
    /// (<c>Task.Run(() =&gt; hook(line, level))</c>) and the first assertion fails — nothing is collected yet
    /// when Info returns. Run it, confirm red, restore.</para></summary>
    [Fact]
    public void The_hook_runs_on_the_calling_thread_before_the_log_call_returns()
    {
        string tag = "line-sink-thread-" + Guid.NewGuid().ToString("N");

        using var sink = LogLineSink.Acquire();

        Log.Info($"here {tag}");
        // No wait anywhere in this fact: if the hook were asynchronous this is where it would be flaky.
        Assert.Equal(Environment.CurrentManagedThreadId, sink.EntryContaining($"here {tag}").ThreadId);

        int otherId = 0;
        var other = new Thread(() => { otherId = Environment.CurrentManagedThreadId; Log.Info($"there {tag}"); });
        other.Start();
        other.Join();

        Assert.Equal(otherId, sink.EntryContaining($"there {tag}").ThreadId);
        Assert.NotEqual(Environment.CurrentManagedThreadId, otherId);
    }

    /// <summary>Dispose uninstalls. A line written after the <c>using</c> block is not collected by the sink
    /// that block owned — otherwise one test's sink would keep observing (and keep growing) for the rest of
    /// the process, and the next test's assertions would be reading somebody else's evidence.
    ///
    /// <para>Falsification: drop the <c>Log.LineSinkForTest = null</c> from
    /// <see cref="LogLineSink.Dispose"/> and the <c>Assert.False</c> below fails — the released sink still
    /// collects. Run it, confirm red, restore.</para></summary>
    [Fact]
    public void A_disposed_sink_stops_receiving_lines()
    {
        string tag = "line-sink-uninstall-" + Guid.NewGuid().ToString("N");

        LogLineSink released;
        using (var sink = LogLineSink.Acquire())
        {
            released = sink;
            Log.Info($"during {tag}");
            Assert.True(sink.Has($"during {tag}"));
        }

        Log.Info($"after {tag}");

        Assert.False(released.Has($"after {tag}"));
        Assert.True(released.Has($"during {tag}"));   // and it kept what it did collect
    }
}
