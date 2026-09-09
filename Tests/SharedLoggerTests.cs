using System.Diagnostics;
using Shared;
using Xunit;

namespace Tests;

/// <summary>The parts of <see cref="Log"/> that became shared when the game server's queue logger moved to
/// <c>Shared.Core</c> and the login server's lock-and-console copy was deleted: the one meaning of
/// <c>P1998_LOG_WIRE</c> with a per-process default, <see cref="Log.Configure"/>, and the non-blocking
/// guarantee the login server did not have.
/// <para><b>Collection "log", for the reason <see cref="LogDropPolicyTests"/> gives:</b> the counters, the
/// admission override, the file sink and now the configured defaults are all process-global.</para></summary>
[Collection("log")]
public class SharedLoggerTests
{
    // ---- P1998_LOG_WIRE: one rule, two defaults -----------------------------------------------------------
    // Before this change the same variable was read two ways: `== "1"` in the login server (so "true" meant
    // OFF) and `!= "0"` in the game server (so "true" meant ON). These rows are that table after: the value
    // decides, the process only supplies the answer for "unset".

    [Theory]
    // unset — each process's own default, and nothing else
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    [InlineData("", true, true)]
    [InlineData("", false, false)]
    // "0" — off in both processes (the live deployment's setting)
    [InlineData("0", true, false)]
    [InlineData("0", false, false)]
    // "1" — on in both processes (protocol work, on a box with no real accounts)
    [InlineData("1", true, true)]
    [InlineData("1", false, true)]
    public void Wire_flag_reads_the_same_in_both_processes(string? raw, bool processDefault, bool expected)
    {
        Assert.Equal(expected, Log.ParseWire(raw, processDefault, out var warning));
        Assert.Null(warning);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("2")]
    [InlineData(" 1")]
    public void An_unrecognised_wire_value_takes_the_process_default_and_warns(string raw)
    {
        // The dangerous half: a mistyped value must never be read as "on" in the process whose packets carry
        // passwords. It takes the default — off for the login server — and says so out loud.
        Assert.False(Log.ParseWire(raw, processDefault: false, out var loginWarning));
        Assert.Contains(raw, loginWarning);
        Assert.Contains("stays off", loginWarning);

        Assert.True(Log.ParseWire(raw, processDefault: true, out var gameWarning));
        Assert.Contains("stays ON", gameWarning);
    }

    [Theory]
    [InlineData(null, 32L * 1024 * 1024)]       // unset: the process default
    [InlineData("", 32L * 1024 * 1024)]
    [InlineData("nonsense", 32L * 1024 * 1024)] // unparseable: fall back, as both old copies did
    [InlineData("0", 32L * 1024 * 1024)]        // non-positive would disable rotation entirely
    [InlineData("-5", 32L * 1024 * 1024)]
    [InlineData("1048576", 1048576L)]
    public void Rotation_limit_falls_back_to_the_process_default(string? raw, long expected)
    {
        Assert.Equal(expected, Log.ParseMaxBytes(raw, 32L * 1024 * 1024));
    }

    /// <summary>The login server's proof, since that process is never started from a test: one
    /// <see cref="Log.Configure"/> call with ITS defaults leaves the wire dump off and the rotation limit at
    /// 32MB, and a second call throws rather than letting a later caller move a default that
    /// <c>Log.WireEnabled</c> call sites have already branched on.
    /// <para>Configure runs at most once per process, so this fact owns the one call in the test process; no
    /// other test may call it. Falsification: make Configure's guard a silent return instead of a throw and
    /// the Assert.Throws below fails; give ParseWire the old <c>!= "0"</c> rule and the first assert
    /// fails.</para></summary>
    [Fact]
    public void Configure_applies_the_login_servers_defaults_and_refuses_a_second_call()
    {
        var previous = Environment.GetEnvironmentVariable("P1998_LOG_WIRE");
        var previousMax = Environment.GetEnvironmentVariable("P1998_LOG_MAX_BYTES");
        Environment.SetEnvironmentVariable("P1998_LOG_WIRE", null);
        Environment.SetEnvironmentVariable("P1998_LOG_MAX_BYTES", null);
        try
        {
            Log.Configure(wireDefault: false, maxBytesDefault: 32L * 1024 * 1024);

            Assert.False(Log.WireEnabled);
            Assert.Equal(32L * 1024 * 1024, Log.MaxBytesForTest());

            Assert.Throws<InvalidOperationException>(
                () => Log.Configure(wireDefault: true, maxBytesDefault: 64L * 1024 * 1024));
            // ... and the refused call changed nothing.
            Assert.False(Log.WireEnabled);
            Assert.Equal(32L * 1024 * 1024, Log.MaxBytesForTest());
        }
        finally
        {
            Environment.SetEnvironmentVariable("P1998_LOG_WIRE", previous);
            Environment.SetEnvironmentVariable("P1998_LOG_MAX_BYTES", previousMax);
        }
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
    /// itself rather than read from <c>DroppedCountsForTest</c>, because the writer thread ZEROES those
    /// counters every time it drains the queue to print the overflow notice — over a 10,000-line window it
    /// reliably carries some of them away (measured: 878 of 10,000 on the first run of this fact).</para>
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
}
