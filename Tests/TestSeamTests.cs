using Server;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The seam itself (#27), pinned so it cannot quietly close again.
///
/// <para>Two things used to make the server untestable: <c>Session</c>'s only constructor took a
/// <c>TcpClient</c> and called <c>GetStream()</c>, and <c>World</c>'s constructor started two dedicated
/// threads, the watchdog, the restart ladder and the status writer. Between them, no test could hold either
/// object — which is why none of the 42 opcode handlers had one. These facts are asserted rather than
/// assumed, because both are the kind of thing a later change re-introduces by accident: a field initializer
/// that starts a task, or a constructor that reaches for a socket.</para>
/// </summary>
[Collection("world")]
public class TestSeamTests
{
    private readonly SessionFixture _fx;

    public TestSeamTests(SessionFixture fx) => _fx = fx;

    /// <summary>Constructing a World attaches nothing OF ITS OWN to the process: no tick thread, no autosave
    /// sweep, no watchdog, no restart scheduler, no status writer. The fixture builds one and never starts it,
    /// so if this ever comes back true, something with a heartbeat has moved back into the constructor.
    ///
    /// <para>The constructor does still LOG — the spawn, NPC and clock lines — and <c>Log</c> owns a
    /// process-wide writer thread that its static initializer starts. That thread is NOT world machinery and
    /// moving <c>Log</c> inside the host's start-up would not make this assertion truer: nothing can construct
    /// a World without <c>Content.Load</c> running first, and that logs several hundred lines, so the writer
    /// is always already up. Measured on this branch, <c>new World()</c> costs zero threads — the constructor
    /// is clean, and the shared logger is infrastructure the whole process depends on.</para></summary>
    [Fact]
    public void ConstructingAWorldStartsNothing() => Assert.False(_fx.World.IsStarted);

    /// <summary>The other half of that promise: the one-shot guard the process host checks before it attaches
    /// anything. #37 section 5 moved the threads themselves to <c>TkListener.StartWorld</c> and left the flag
    /// here, so the guarantee above still has something to read; <c>MarkStarted</c> is what the host asks, and
    /// it answers true exactly once per World. That is what makes a second <c>RunAsync</c> a no-op rather than
    /// a second tick thread, and it is why <c>IsStarted</c> flips once and stays flipped.
    ///
    /// <para>A throwaway World, not the fixture's: marking the shared one would make the assertion above
    /// depend on test order. Constructing one is exactly the thing this class asserts is free of side
    /// effects, and nothing here starts a thread.</para>
    ///
    /// <para>Falsified by replacing the <c>Interlocked.Exchange</c> in <c>World.MarkStarted</c> with
    /// <c>_started = 1; return true;</c>: red with "Assert.False() Failure / Expected: False / Actual: True"
    /// on the second call. Falsified again by the racy version the sequential half cannot see,
    /// <c>if (IsStarted) return false; _started = 1; return true;</c>: red on the race with "Assert.Equal()
    /// Failure: Values differ / Expected: 1 / Actual: 2" (five runs out of five, actuals 2-4). An event-based
    /// gate did NOT catch it — nine runs, all green — which is why the gate is a spin.</para></summary>
    [Fact]
    public void MarkStartedIsOneShotAndIsStartedFlipsOnce()
    {
        var world = new World();   // constructed, never started — the same deal the fixture's World gets
        Assert.False(world.IsStarted);

        Assert.True(world.MarkStarted());
        Assert.True(world.IsStarted);

        Assert.False(world.MarkStarted());
        Assert.True(world.IsStarted);

        // And exactly one winner when the calls race, which is the shape a supervisor that restarts RunAsync
        // would produce. A spin gate rather than an event: the threads have to arrive inside the same few
        // microseconds for a check-then-set to lose, and an event's wake-up skew is wide enough to hide one.
        for (int round = 0; round < 40; round++)
        {
            var raced = new World();
            int winners = 0, go = 0;
            var threads = new Thread[4];
            for (int i = 0; i < threads.Length; i++)
            {
                threads[i] = new Thread(() =>
                {
                    while (Volatile.Read(ref go) == 0) Thread.SpinWait(1);
                    if (raced.MarkStarted()) Interlocked.Increment(ref winners);
                });
                threads[i].Start();
            }
            Thread.Sleep(1);   // let all four reach the spin before releasing them
            Volatile.Write(ref go, 1);
            foreach (var t in threads) t.Join();

            Assert.Equal(1, winners);
            Assert.True(raced.IsStarted);
        }
    }

    /// <summary>A Session exists, holds a character, is registered in the world and can be addressed by id —
    /// with no socket anywhere.</summary>
    [Fact]
    public void ASessionCanBeBuiltWithNoSocket()
    {
        var (session, outbound) = _fx.Player("SeamProbe");

        Assert.Equal("recorder:SeamProbe", outbound.Remote);
        Assert.Same(session, _fx.World.Online.ById(session.PlayerId));
    }

    /// <summary>The read loop is the one thing that genuinely needs a socket, and it says so instead of
    /// failing somewhere deeper. Everything else — every handler — works either way.</summary>
    [Fact]
    public async Task RunAsyncRefusesASocketFreeSession()
    {
        var (session, _) = _fx.Player("SeamNoReadLoop");

        await Assert.ThrowsAsync<InvalidOperationException>(session.RunAsync);
    }
}
