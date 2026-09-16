using System.Diagnostics;
using Shared;
using Xunit;

namespace Tests;

/// <summary>The parts of <see cref="Log"/> that became shared when the game server's queue logger moved to
/// <c>Shared.Core</c> and the login server's lock-and-console copy was deleted: <see cref="Log.Configure"/>,
/// the non-blocking guarantee the login server did not have, and the exit flush that pays for it.
/// <para>The <c>P1998_LOG_WIRE</c> / <c>P1998_LOG_MAX_BYTES</c> parse rules moved with the environment read
/// itself, into <c>ServerConfig</c>; <see cref="ServerConfigTests"/> owns those rows now, and applies them to
/// every boolean knob rather than to this one variable.</para>
/// <para><b>Collection "log", for the reason <see cref="LogDropPolicyTests"/> gives:</b> the counters, the
/// admission override, the file sink and now the configured defaults are all process-global. The shutdown
/// fact below additionally closes the queue for real, which is why it restarts the writer before it
/// leaves.</para></summary>
[Collection("log")]
public class SharedLoggerTests
{
    /// <summary>The login server's proof, since that process is never started from a test: one
    /// <see cref="Log.Configure"/> call with ITS settings leaves the wire dump off and the rotation limit at
    /// 32MB, and a second call throws rather than letting a later caller move a value that
    /// <c>Log.WireEnabled</c> call sites have already branched on.
    /// <para>The arguments are the ALREADY-RESOLVED settings now: the entry point reaches this through
    /// <c>ServerConfig.ConfigureLogging</c>, which is what combines the two environment knobs with the
    /// per-process default. That combination is pinned in <see cref="ServerConfigTests"/>, on a configuration
    /// built from an explicit source rather than from this process's environment — which cannot be changed
    /// here anyway, because Configure runs at most once per process and this fact owns that call.</para>
    /// <para>Falsification: make Configure's guard a silent return instead of a throw and the Assert.Throws
    /// below fails; have it ignore its arguments and the first assert fails.</para></summary>
    [Fact]
    public void Configure_applies_the_login_servers_settings_and_refuses_a_second_call()
    {
        Log.Configure(wireEnabled: false, maxBytes: 32L * 1024 * 1024);

        Assert.False(Log.WireEnabled);
        Assert.Equal(32L * 1024 * 1024, Log.MaxBytesForTest());

        Assert.Throws<InvalidOperationException>(
            () => Log.Configure(wireEnabled: true, maxBytes: 64L * 1024 * 1024));
        // ... and the refused call changed nothing.
        Assert.False(Log.WireEnabled);
        Assert.Equal(32L * 1024 * 1024, Log.MaxBytesForTest());
    }

    // ---- the guarantee the login server did not have ------------------------------------------------------

    /// <summary>A caller never waits on the log, even when nothing can be written.
    /// <para>The refused-admission seam stands in for the stalled console this whole design exists for: a
    /// writer thread blocked on a QuickEdit selection fills the queue, after which every record is refused —
    /// which is exactly the state this fact puts the logger in. Until this change the login server answered
    /// that state by holding a process-global lock across <c>Console.WriteLine</c>, so its 47 <c>Log.Info</c>
    /// sites — on the accept path and inside every login — waited for the terminal.</para>
    /// <para>The refusals are counted too: without that, a run where the override quietly stopped applying
    /// would still be fast, and the fact would be measuring nothing. The count is kept by the override
    /// itself rather than read from <c>DroppedCountsForTest</c>. That began as a way round the writer thread
    /// zeroing the drop counters every time it drained the queue to print the overflow notice — over a
    /// 10,000-line window it reliably carried some of them away (measured: 878 of 10,000 on the first run of
    /// this fact). The writer no longer zeroes anything, so that hazard is gone; the override keeps its own
    /// tally anyway, because here it is the stronger fact: it counts the calls that reached the seam on THIS
    /// thread, not what the logger recorded.</para>
    /// <para>Falsification: put a <c>Thread.Sleep(1)</c> in Enqueue and the elapsed assert fails by two
    /// orders of magnitude.</para></summary>
    [Fact]
    public void Info_returns_without_waiting_when_no_record_can_be_admitted()
    {
        const int Lines = 10_000;

        int refused = 0;
        int owner = Environment.CurrentManagedThreadId;
        Log.ResetDroppedCountsForTest();
        // Refuse THIS thread's records only, for the reason LogDropPolicyTests gives at length: the override
        // is one process-global field consulted on the calling thread, so a blanket refusal would also swallow
        // whatever a parallel collection logs while the window is open. Tally what was refused.
        Log.AdmitOverrideForTest = (level, queued) =>
        {
            if (Environment.CurrentManagedThreadId != owner) return Log.Admits(level, queued);
            Interlocked.Increment(ref refused);
            return false;
        };
        try
        {
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < Lines; i++) Log.Info($"stalled-console stand-in {i}");
            clock.Stop();

            Assert.Equal(Lines, refused);
            Assert.True(clock.ElapsedMilliseconds < 1_000,
                $"{Lines} refused Info calls took {clock.ElapsedMilliseconds}ms — a caller is waiting on the log");
        }
        finally
        {
            Log.AdmitOverrideForTest = null;
            Log.ResetDroppedCountsForTest();
        }
    }

    // ---- the price of the queue: the tail has to be flushed -----------------------------------------------

    /// <summary>Everything logged before <see cref="Log.Shutdown"/> is on disk after it returns, and a line
    /// logged AFTER it is dropped rather than thrown at.
    /// <para>This is what <see cref="Log.FlushOnExit"/> buys the login server. A queue logger with no exit
    /// flush is worse than the synchronous one it replaces for the only lines anybody reads — the last ones
    /// before a stop or a crash.</para>
    /// <para>Like the control-line fact in <see cref="LogDropPolicyTests"/>, this attaches a real file and
    /// there is no detach, so the sink is left pointing at a temp directory and the rest of this process's
    /// log lines are teed there too. It also calls the REAL Shutdown, which completes the queue for good;
    /// <c>RestartWriterForTest</c> puts a fresh queue and writer back so no later test in this process is
    /// logging into a closed one.</para>
    /// <para><b>The writer-stopped assert is the load-bearing one</b>, and reading the file alone is not
    /// enough: measured, gutting Shutdown to a no-op still left all 200 lines in the file, because on an idle
    /// test box the writer simply keeps up. The thread leaves <c>GetConsumingEnumerable</c> only once the
    /// queue is both empty and COMPLETED, and flushes in its finally on the way out — so "the writer has
    /// stopped" is exactly the drain-and-flush this fact is about. Falsifications: remove
    /// <c>CompleteAdding</c>/<c>Join</c> from Shutdown and the writer is still alive; remove the _closed
    /// guard and the catch in Enqueue's TryAdd and the post-shutdown Info throws InvalidOperationException
    /// instead of being dropped (both re-broken here before this fact was trusted).</para></summary>
    [Fact]
    public void Shutdown_flushes_the_tail_and_a_line_after_it_is_dropped_not_thrown()
    {
        const int Lines = 200;
        string tag = "flush-fact-" + Guid.NewGuid().ToString("N");
        string path = Path.Combine(Path.GetTempPath(), "p1998-log-flush-" + tag, "login.log");

        try
        {
            Log.AttachFile(path);
            Assert.True(Log.WriterRunningForTest());   // the state this fact starts from
            for (int i = 0; i < Lines; i++) Log.Info($"{tag} {i}");

            Log.Shutdown();

            Assert.False(Log.WriterRunningForTest(),
                "Shutdown returned with the writer thread still running — the queue was not drained");

            // No throw: this is the session thread that logged one line late, on the way out.
            Log.Info($"{tag} after shutdown");

            string text = ReadSharing(path);
            for (int i = 0; i < Lines; i++)
                Assert.Contains($"{tag} {i}\n", text);
            Assert.DoesNotContain($"{tag} after shutdown", text);
        }
        finally
        {
            Log.RestartWriterForTest();
        }
    }

    /// <summary>The second <see cref="Log.Shutdown"/> returns at the once-guard without touching the queue,
    /// and <c>RestartWriterForTest</c> reopens the guard along with the queue.
    /// <para>Both halves matter and they are the same fact. The guard is what the comments at BOTH call sites
    /// have always claimed — the Ctrl+C handler's <c>Environment.Exit(0)</c> "re-raises ProcessExit below;
    /// Shutdown's own guard makes that a no-op", and the catch inside Shutdown named the double-call as the
    /// only thing it ever swallowed. Before this there was no guard: the second call ran the body and threw
    /// <c>CompleteAdding</c> on a completed collection into that catch, which is a comment describing a
    /// mechanism that did not exist. And a guard that <c>RestartWriterForTest</c> did not reset would be worse
    /// than none: every Shutdown fact after the first in this process would return at the guard, leave the
    /// writer running, and pass while measuring nothing.</para>
    /// <para><c>ShutdownRunsForTest</c> counts the calls that got PAST the guard, because that is the only
    /// observable difference — a guarded and an unguarded second call both return, and both leave the writer
    /// stopped. Deltas, not absolutes: the counter is process-global and the fact above shuts down too.</para>
    /// <para>Falsifications: remove the <c>Interlocked.Exchange</c> guard from Shutdown and the second call
    /// runs the body, so the "returned at the guard" assert sees the count go up by one. Remove the reset from
    /// <c>RestartWriterForTest</c> and the restarted writer is still alive after the third Shutdown, and the
    /// count does not move.</para></summary>
    [Fact]
    public void A_second_Shutdown_returns_at_the_guard_and_the_test_restart_reopens_it()
    {
        try
        {
            Log.Shutdown();
            Assert.False(Log.WriterRunningForTest());
            int afterFirst = Log.ShutdownRunsForTest();

            Log.Shutdown();   // the double call the two exit hooks make by design
            Assert.Equal(afterFirst, Log.ShutdownRunsForTest());
            Assert.False(Log.WriterRunningForTest());

            Log.RestartWriterForTest();
            Assert.True(Log.WriterRunningForTest());

            Log.Shutdown();   // and this one works again, because the reset reopened the guard
            Assert.False(Log.WriterRunningForTest(),
                "RestartWriterForTest left the once-guard set — every later Shutdown fact is measuring nothing");
            Assert.Equal(afterFirst + 1, Log.ShutdownRunsForTest());
        }
        finally
        {
            Log.RestartWriterForTest();
        }
    }

    /// <summary>Read the log while the writer still holds it open (there is no detach).</summary>
    private static string ReadSharing(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }
}
