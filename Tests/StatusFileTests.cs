using System.Text.Json;
using Server;
using Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Tests;

/// <summary>
/// The two counters <c>run/status.json</c> publishes beside the player count: <c>ticks</c>, every beat the
/// world has run, and <c>slowTicks</c>, every beat the watchdog reported.
///
/// <para>Why they exist: both load reports state their headline — the share of beats that were slow — as a
/// wall-clock span divided by the 333ms period, because the server published no total. That is a division,
/// not a measurement, and it cannot be checked. These two make it <c>slowTicks / ticks</c>.</para>
///
/// <para>Which makes the silent failure worth guarding against an obvious one: a counter that does not
/// count. Nothing would throw, nothing would look wrong, and the next load run would publish a confident
/// wrong number — a 0% slow-beat rate reads like good news. So the facts below are about the ARITHMETIC
/// over a known number of beats, not about the document parsing.</para>
///
/// <para>Driven through <c>World.TickOnceWatchedForTest</c>, the same seam the phase tests use: it runs a
/// real beat and then the real watchdog with the threshold passed in. The <c>late</c> argument is what makes
/// a beat slow or healthy on purpose here — <c>work</c> on a near-empty map is a race with the machine,
/// while <c>late</c> is a number this test supplies, so "three slow beats and two healthy ones" is a fact
/// rather than a hope.</para>
///
/// <para>Deltas, never absolutes: the <c>world</c> collection's <c>World</c> is shared and every other test
/// class in it has been ticking the same counter. The collection serialises its classes, so a delta across
/// this method is exactly this method's beats.</para>
/// </summary>
[Collection("world")]
public class StatusFileTests
{
    private readonly SessionFixture _fx;
    private readonly ITestOutputHelper _out;

    public StatusFileTests(SessionFixture fx, ITestOutputHelper output) { _fx = fx; _out = output; }

    /// <summary>A threshold no beat on any machine can reach, so the beat is healthy by construction.</summary>
    private const int NeverSlow = 60_000;

    // =====================================================================================================

    /// <summary>Five beats, three of them slow, move <c>ticks</c> by five and <c>slowTicks</c> by three — and
    /// the numbers the status document carries are those, not a copy kept somewhere else.</summary>
    [Fact]
    public void TheStatusDocumentCountsEveryBeatAndEverySlowBeat()
    {
        long ticks0 = _fx.World.Ticks, slow0 = _fx.World.SlowTicks;

        // `late` past the threshold is the watchdog's other gate (work OR scheduling delay), so these three
        // are slow whatever the machine does with the beat itself.
        _fx.World.TickOnceWatchedForTest(slowMs: 1, late: 5_000);
        _fx.World.TickOnceWatchedForTest(slowMs: NeverSlow);
        _fx.World.TickOnceWatchedForTest(slowMs: 1, late: 5_000);
        _fx.World.TickOnceWatchedForTest(slowMs: NeverSlow);
        _fx.World.TickOnceWatchedForTest(slowMs: 1, late: 5_000);

        string json = StatusFile.Render(_fx.World, online: true, players: 7);
        _out.WriteLine(json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(5, root.GetProperty("ticks").GetInt64() - ticks0);
        Assert.Equal(3, root.GetProperty("slowTicks").GetInt64() - slow0);

        // The document the launcher already reads is unchanged: same three fields, same names, same meaning.
        // The counters are an addition to it, not a replacement for it.
        Assert.True(root.GetProperty("online").GetBoolean());
        Assert.Equal(7, root.GetProperty("players").GetInt32());
        Assert.True(root.TryGetProperty("message", out _), "the launcher's `message` field must survive");
    }

    /// <summary>A healthy beat moves <c>ticks</c> and leaves <c>slowTicks</c> alone. Stated separately from
    /// the count above because it fails for a different reason: a <c>slowTicks</c> wired to the beat instead
    /// of to the watchdog would pass an all-slow test and make every load run read 100%.</summary>
    [Fact]
    public void AHealthyBeatIsCountedButNotCountedAsSlow()
    {
        long ticks0 = _fx.World.Ticks, slow0 = _fx.World.SlowTicks;

        _fx.World.TickOnceWatchedForTest(slowMs: NeverSlow);

        using var doc = JsonDocument.Parse(StatusFile.Render(_fx.World, online: true, players: 0));
        Assert.Equal(1, doc.RootElement.GetProperty("ticks").GetInt64() - ticks0);
        Assert.Equal(0, doc.RootElement.GetProperty("slowTicks").GetInt64() - slow0);
    }
}
