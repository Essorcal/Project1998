using System.Collections.Concurrent;
using Shared;

namespace Tests.Support;

/// <summary>
/// The one way a test asserts on a log LINE: installs <c>Log.LineSinkForTest</c>, collects every formatted
/// line the process writes while it is held, and uninstalls on Dispose.
///
/// <para><b>Why this instead of the file sink.</b> Reading a line back off <c>Log.AttachFile</c> costs the
/// process-global file handle, a real <c>Log.Shutdown</c> to flush it and a <c>RestartWriterForTest</c> to
/// put the logger back — so a test that only wants to know what was logged has to join collection
/// <c>"log"</c> and sit away from the behaviour it pins. This sees the same string on the calling thread,
/// synchronously, with no writer thread, no file and no shutdown in the way. The tests that pin the BYTES ON
/// DISK (<see cref="LogDropPolicyTests"/>, <see cref="LogWarnErrorLineTests"/>,
/// <see cref="SharedLoggerTests"/>) are a different claim and still use the file.</para>
///
/// <para><b>Exclusive</b>, for <see cref="ConsoleTap"/>'s reason: <c>Log.LineSinkForTest</c> is a single
/// process-global slot, so two tests installing at once would have the second one's uninstall discard the
/// first one's sink. <see cref="Acquire"/> takes a process-wide gate and holds it until Dispose. Lines
/// written by OTHER tests during that window land here too, which is harmless — every caller looks for its
/// own needle.</para>
///
/// <para>The hook fires before admission, so a line appears here whether or not the queue took it (see
/// <c>Log.LineSinkForTest</c>), and it fires on whichever thread logged, so the collection is
/// concurrent.</para>
/// </summary>
internal sealed class LogLineSink : IDisposable
{
    // Not a lock object: an async test can await the acquisition rather than block a pool thread on it.
    private static readonly SemaphoreSlim Exclusive = new(1, 1);

    /// <summary>Long enough that no honest capture window can hit it, short enough to fail as a deadlock
    /// report rather than as a suite that never ends. <see cref="ConsoleTap"/>'s bound, for its reason.</summary>
    private static readonly TimeSpan AcquireBound = TimeSpan.FromSeconds(120);

    /// <summary>One observed line: the exact string that was formatted, the level <c>Log.LevelOf</c> gave it,
    /// and the thread it was written on — the hook runs on the caller's thread, and
    /// <see cref="LogLineSinkTests"/> pins that rather than assuming it.
    ///
    /// <para><c>Probe</c> is the value of the probe <see cref="Acquire"/> was handed, read on that same thread at
    /// that same moment (false when there was none) — for a test whose claim is about the STATE the line was
    /// written in, such as whether a lock was held.</para></summary>
    internal readonly record struct Entry(string Line, LogLevel Level, int ThreadId, bool Probe = false);

    private readonly ConcurrentQueue<Entry> _lines = new();
    private int _released;

    private LogLineSink() { }

    /// <summary>Take the line sink for this test. Dispose (a <c>using</c>) uninstalls it and lets the next
    /// asserting test in. <paramref name="probe"/>, when given, is evaluated on the logging thread as each
    /// line is written and recorded as <see cref="Entry.Probe"/>; like the hook itself it must be cheap,
    /// thread-safe, and must not log.</summary>
    public static LogLineSink Acquire(Func<bool>? probe = null)
    {
        if (!Exclusive.Wait(AcquireBound))
            throw new TimeoutException(
                $"no test released the log line sink within {AcquireBound.TotalSeconds:0}s; an asserting test "
                + "is holding it (a missing Dispose, or a capture that waits on something that never happens).");

        var sink = new LogLineSink();
        Log.LineSinkForTest = (line, level) =>
            sink._lines.Enqueue(new Entry(line, level, Environment.CurrentManagedThreadId, probe?.Invoke() ?? false));
        return sink;
    }

    /// <summary>Every line written since this sink was installed, in the order the hook saw them.</summary>
    public IReadOnlyList<Entry> Lines => _lines.ToArray();

    /// <summary>The one collected line carrying <paramref name="needle"/>, stamp included — the caller
    /// asserts on <c>line.Substring(Log.StampLength)</c> when it wants the message alone, so the stamp stays
    /// pinned rather than assumed. Throws naming everything collected if there is no such line, which is the
    /// failure report a missing log line should produce.</summary>
    public string LineContaining(string needle) => EntryContaining(needle).Line;

    /// <summary>The same, as the whole observation — for a caller that asserts on the level or the thread as
    /// well as the text.</summary>
    public Entry EntryContaining(string needle)
    {
        foreach (var entry in _lines)
            if (entry.Line.Contains(needle, StringComparison.Ordinal)) return entry;

        throw new Xunit.Sdk.XunitException(
            $"no logged line containing '{needle}'; collected {_lines.Count}:\n"
            + string.Join("\n", _lines.Select(l => l.Line)));
    }

    /// <summary>Whether any collected line carries <paramref name="needle"/>. For the negative claim — that
    /// nothing was logged — where <see cref="LineContaining"/> would throw.</summary>
    public bool Has(string needle) => _lines.Any(l => l.Line.Contains(needle, StringComparison.Ordinal));

    public void Dispose()
    {
        // Idempotent: releasing the gate twice would let two tests own the sink at once, which is the exact
        // bug this type exists to prevent.
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            Log.LineSinkForTest = null;
            Exclusive.Release();
        }
    }
}
