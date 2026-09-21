using System.Text.Json;
using Server;
using Shared;
using Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Tests;

/// <summary>
/// The per-map accepted-step counter that <c>run/viewport.json</c> publishes as <c>steps</c>.
///
/// <para>Why it is worth guarding, and it is the same argument the fraction beside it carries. The document
/// exists to price a per-map spatial index. The fraction says what share of the TICK's sweep an index would
/// skip; the step rate says how many WALK reconciles it would skip as well — and a walk reconcile is the
/// dearer of the two (one accepted step is about nineteen times one viewer's share of the tick's sweep in
/// Release: <c>briefs/reports/walk-reconcile-profile-opus.md</c>). A counter that counted REFUSED steps too
/// would not throw and would not look wrong: it would read high by whatever share of steps the population
/// is bumping into each other, which on the clump holds on record was 112,000 of 112,000. That is exactly
/// the silently-wrong number this suite exists for, so the fact below drives REAL walk packets through the
/// real handler and reads the published document rather than the field.</para>
///
/// <para>The map is synthetic and carries no <c>Maps.csv</c> row, which is deliberate: <c>MapData.For</c>
/// then returns null, so no terrain refuses a step and the only refusals are the two this fact asks for —
/// a living peer on the destination tile, and the map edge. The character's own <c>MapXs</c>/<c>MapYs</c>
/// are what <c>HandleWalk</c> bounds-checks against, so they are set explicitly.</para>
/// </summary>
[Collection("world")]
public class WalkStepCounterTests
{
    private readonly SessionFixture _fx;
    private readonly ITestOutputHelper _out;

    public WalkStepCounterTests(SessionFixture fx, ITestOutputHelper output) { _fx = fx; _out = output; }

    private const ushort StepMap = 60950;
    private const ushort OtherMap = 60951;
    private const ushort Span = 40;

    private static byte[] Walk(byte dir, ushort x, ushort y) =>
        SessionFixture.Frame(ClientOp.Walk,
            new byte[] { dir, 0, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y });

    /// <summary>One real 0x06 walk packet, reporting the tile the server believes the session is on — which
    /// is what a healthy client reports, and what makes this the handler's normal path rather than its
    /// resync path.</summary>
    private static void Step(Session s, byte dir) => s.Receive(Walk(dir, s.PlayerX, s.PlayerY));

    /// <summary>The map's published <c>steps</c>, read back out of the document the deployment writes.</summary>
    private long PublishedSteps(ushort map)
    {
        string json = ViewportSurvey.Render(_fx.World.Online.PositionSurvey(),
                                            t: 1_758_400_000_000, intervalMs: 10_000);
        using var doc = JsonDocument.Parse(json);
        foreach (var m in doc.RootElement.GetProperty("maps").EnumerateArray())
            if (m.GetProperty("map").GetUInt16() == map) return m.GetProperty("steps").GetInt64();
        throw new Xunit.Sdk.XunitException($"map {map} is not in the survey document: {json}");
    }

    // =====================================================================================================

    /// <summary>
    /// <b>N accepted steps publish <c>steps</c> = N, and a refused step does not count.</b>
    ///
    /// <para>The walker starts in the map's west corner and walks five tiles east through five real walk
    /// packets, so the count is a number this test chose rather than one it read. Then the two refusals:
    /// a living peer standing on the destination tile (<c>World.TryMovePlayer</c>'s occupancy branch), and
    /// the map edge (<c>HandleWalk</c>'s <c>offMap</c> branch, which never reaches the lock at all). Neither
    /// may move the counter, and the walker must still be where it was — a refusal that counted AND moved
    /// would be two bugs, and the position assertions tell them apart.</para>
    ///
    /// <para>FALSIFIED by moving the increment in <c>World.TryMovePlayer</c> above the
    /// <c>if (otherwiseBlocked || …) return false;</c> line, so a refused step counts too: this fact then
    /// goes red on the peer-refusal assertion with 6 against the expected 5 (recorded in the report).</para>
    /// </summary>
    [Fact]
    public void AcceptedStepsAreCountedPerMapAndRefusedStepsAreNot()
    {
        var (walker, _, _) = _fx.PlayerWith("StepWalker", c => { c.MapXs = Span; c.MapYs = Span; },
                                            StepMap, 0, 10);

        Assert.Equal(0, PublishedSteps(StepMap));

        for (int i = 0; i < 5; i++) Step(walker, 1);                 // east
        Assert.Equal((ushort)5, walker.PlayerX);
        Assert.Equal(5, PublishedSteps(StepMap));

        // REFUSAL 1: a living peer occupies the destination tile.
        var (blocker, _, _) = _fx.PlayerWith("StepBlocker", c => { c.MapXs = Span; c.MapYs = Span; },
                                             StepMap, 6, 10);
        Step(walker, 1);
        Assert.Equal((ushort)5, walker.PlayerX);                     // held, as the handler's snap-back says
        Assert.Equal(5, PublishedSteps(StepMap));                    // and NOT counted

        // Back west to the edge — five more accepted steps — and then one step off the map.
        for (int i = 0; i < 5; i++) Step(walker, 3);                 // west
        Assert.Equal((ushort)0, walker.PlayerX);
        Assert.Equal(10, PublishedSteps(StepMap));

        // REFUSAL 2: the map edge. This branch returns before World.TryMovePlayer is called at all.
        Step(walker, 3);
        Assert.Equal((ushort)0, walker.PlayerX);
        Assert.Equal(10, PublishedSteps(StepMap));

        // The counter is the MAP's, not the world's: a step on another map moves that map's total and
        // leaves this one alone.
        var (elsewhere, _, _) = _fx.PlayerWith("StepElsewhere", c => { c.MapXs = Span; c.MapYs = Span; },
                                               OtherMap, 10, 10);
        Step(elsewhere, 1);
        Assert.Equal(1, PublishedSteps(OtherMap));
        Assert.Equal(10, PublishedSteps(StepMap));

        _out.WriteLine($"StepMap {PublishedSteps(StepMap)}, OtherMap {PublishedSteps(OtherMap)}, " +
                       $"blocker at ({blocker.PlayerX},{blocker.PlayerY})");
    }
}
