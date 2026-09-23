namespace Tests.Support;

/// <summary>
/// Held by a test for as long as it has the log CLOSED — from its <c>Log.Shutdown</c> to its
/// <c>Log.RestartWriterForTest</c>. While it is held, no <see cref="LogLineSink"/> and no
/// <see cref="ConsoleTap"/> can be, in any collection.
///
/// <para><b>Why.</b> <c>Log.Shutdown</c> sets the logger's closed flag, and <c>Log.Enqueue</c> returns on
/// that flag before it calls <c>Log.LineSinkForTest</c> or queues anything for the console. So every line
/// written in the closed window is gone before either capture can see it, whichever thread or collection
/// wrote it. The shutdown facts live in collection <c>"log"</c>; the captures they starve do not. Upstream run
/// 35895702560 attempt 1 went red on exactly that: <c>MobAiTickTests.ThrowingCreaturesCostOneStackPerFaultAndOneCountLinePerBeat</c>
/// and <c>ASweepThatThrowsCostsOneStackAndOneCountLinePerBeat</c> collected none of their fault lines while
/// <c>SharedLoggerTests.Shutdown_flushes_the_tail_and_a_line_after_it_is_dropped_not_thrown</c> had the log
/// closed — its 200 flushed lines are in the job log immediately before the two failures.</para>
///
/// <para><b>Why the captures' own gates rather than a new one.</b> Each capture type already serialises
/// against its own kind with a process-wide gate. Taking both of those here is the whole fix: the captures
/// themselves do not change, and two captures of different kinds still run side by side, as they always
/// have. A new shared gate would have to be taken by every capture as well, and would serialise a
/// <see cref="LogLineSink"/> against a <see cref="ConsoleTap"/> for no reason.</para>
///
/// <para><b>Order.</b> The line-sink gate first, then the console gate. A test that ever holds a capture of
/// each kind at once must take them in that same order, or it can deadlock against this (bounded: both
/// sides give up after 120 s with a <see cref="TimeoutException"/>). No test does today.</para>
/// </summary>
internal sealed class LogShutdownWindow : IDisposable
{
    private int _released;

    private LogShutdownWindow() { }

    /// <summary>Wait for every capture in flight to finish, then keep new ones out until Dispose. Take it
    /// BEFORE the first <c>Log.Shutdown</c> and release it AFTER the last <c>Log.RestartWriterForTest</c>
    /// (a <c>using</c> at the top of the fact does both).</summary>
    public static LogShutdownWindow Enter()
    {
        LogLineSink.TakeGate();
        try
        {
            ConsoleTap.TakeGate();
        }
        catch
        {
            LogLineSink.ReleaseGate();
            throw;
        }
        return new LogShutdownWindow();
    }

    public void Dispose()
    {
        // Idempotent for the reason LogLineSink.Dispose gives: a double release would admit two holders.
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            ConsoleTap.ReleaseGate();
            LogLineSink.ReleaseGate();
        }
    }
}
