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

    /// <summary>Constructing a World attaches NOTHING to the process: no tick thread, no autosave sweep, no
    /// watchdog, no restart scheduler, no status writer. The fixture builds one and never starts it, so if
    /// this ever comes back true, something with a heartbeat has moved back into the constructor.</summary>
    [Fact]
    public void ConstructingAWorldStartsNothing() => Assert.False(_fx.World.IsStarted);

    /// <summary>A Session exists, holds a character, is registered in the world and can be addressed by id —
    /// with no socket anywhere.</summary>
    [Fact]
    public void ASessionCanBeBuiltWithNoSocket()
    {
        var (session, outbound) = _fx.Player("SeamProbe");

        Assert.Equal("recorder:SeamProbe", outbound.Remote);
        Assert.Same(session, _fx.World.PlayerById(session.PlayerId));
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
