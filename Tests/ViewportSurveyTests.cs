using System.Diagnostics;
using System.Text.Json;
using Server;
using Shared;
using Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Tests;

/// <summary>
/// What <c>run/viewport.json</c> publishes: where players and mobs stand, and the share of them a viewer can
/// actually see.
///
/// <para>Why it is worth guarding. The document exists to decide one thing — whether a per-map spatial index
/// is worth building, which turns on the in-view fraction <c>f</c> being well under 1 in real play. A
/// fraction computed with the pads crossed, or averaged over the wrong denominator, is not a number that
/// throws: it is a plausible number that would be read off the deployment after a day and used to justify (or
/// to abandon) a large change to the viewport sweep. So the facts below are about the ARITHMETIC over an
/// arrangement whose answer is computed by hand, not about the document parsing.</para>
///
/// <para>The arrangement is driven through <see cref="ViewportSurvey.Render(World.OnlineRegistry.MapPositions[], long, int)"/>,
/// the computation seam that takes copies, because the collection's <c>World</c> is shared and every other
/// class in it leaves players standing on maps — an exact fraction cannot be stated over a world whose
/// population this test does not own. The two facts that need a real world (the lock discipline, and an empty
/// world) drive the <c>World</c> seam instead.</para>
/// </summary>
[Collection("world")]
public class ViewportSurveyTests
{
    private readonly SessionFixture _fx;
    private readonly ITestOutputHelper _out;

    public ViewportSurveyTests(SessionFixture fx, ITestOutputHelper output) { _fx = fx; _out = output; }

    /// <summary>Mythic Nexus, 60x60 — big enough that the tiles below are all in the anchor's non-edge
    /// branch, so the rect origin is simply (x-8, y-7).</summary>
    private const ushort MapA = 41;

    /// <summary>Yuri's Flower Garden, 80x80 — the second map, carrying one player, which is the case where
    /// there is no peer to see.</summary>
    private const ushort MapB = 260;

    // =====================================================================================================

    /// <summary>Three peers — one inside the strict rect, one in the overdraw band only, one outside — and
    /// three mobs in the same three cases produce the fractions computed by hand below, on both maps.
    ///
    /// <para>The layout, on MapA (60x60). Every viewer is in the anchor's middle branch, so its drawn rect's
    /// top-left is (x-8, y-7): the STRICT rect is x in [ox, ox+16], y in [oy, oy+14], and the DRAWN rect is
    /// one tile wider on every side.
    /// <list type="bullet">
    /// <item>V (30,30) — origin (22,23); strict x 22..38, y 23..37; drawn x 21..39, y 22..38</item>
    /// <item>A (35,33) — inside V's strict rect</item>
    /// <item>B (39,30) — one column past V's strict rect, inside its drawn rect: the band case</item>
    /// <item>C (50,30) — outside both</item>
    /// <item>mobs (33,33) inside, (21,30) band only, (5,5) outside</item>
    /// </list>
    /// Every one of the four players is a VIEWER as well as a peer, which is the arithmetic that matters:
    /// the published figure is the mean per viewer, because a map's sweep cost is paid once per viewer.
    /// Hand-computed, viewer by viewer (peers in drawn / strict, mobs in drawn / strict):
    /// V 2/1 and 2/1, A 2/2 and 1/1, B 2/1 and 1/1, C 0/0 and 0/0, over 3 peers and 3 mobs each. So the means
    /// are peerDrawn (2/3+2/3+2/3+0)/4 = 0.5, peerStrict (1/3+2/3+1/3)/4 = 0.3333, mobDrawn
    /// (2/3+1/3+1/3)/4 = 0.3333, mobStrict (1/3+1/3+1/3)/4 = 0.25, with the per-viewer drawn peer share
    /// running from 0 (C) to 0.6667.</para>
    ///
    /// <para>FALSIFIED by swapping <c>ShowPad</c> and <c>HidePad</c> in <c>ViewportSurvey</c>: the band peer
    /// and the band mob then count as strict and the inside ones stop counting as drawn, and this fact goes
    /// red on peerStrictFraction (recorded in the report).</para></summary>
    [Fact]
    public void ThePublishedFractionsAreTheHandComputedOnesForAKnownArrangement()
    {
        // The rect arithmetic is anchored on the map's real size, so state the sizes this fact was computed
        // against: a content change that resized either map must fail here rather than move the numbers.
        Assert.Equal((ushort)60, Content.Maps[MapA].Xs);
        Assert.Equal((ushort)60, Content.Maps[MapA].Ys);
        Assert.Equal((ushort)80, Content.Maps[MapB].Xs);
        Assert.Equal((ushort)80, Content.Maps[MapB].Ys);

        var maps = new[]
        {
            new World.OnlineRegistry.MapPositions(MapA,
                new (uint, ushort, ushort)[] { (1, 30, 30), (2, 35, 33), (3, 39, 30), (4, 50, 30) },
                new (ushort, ushort)[] { (33, 33), (21, 30), (5, 5) }),
            // One player, two mobs: the single-viewer case, where there is no peer to see and the peer
            // fractions are 0 rather than undefined. Viewer (40,40) -> origin (32,33); (41,40) is inside,
            // (70,40) is far outside.
            new World.OnlineRegistry.MapPositions(MapB,
                new (uint, ushort, ushort)[] { (5, 40, 40) },
                new (ushort, ushort)[] { (41, 40), (70, 40) }),
        };

        string json = ViewportSurvey.Render(maps, t: 1_758_300_000_000, intervalMs: 10_000);
        _out.WriteLine(json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1_758_300_000_000, root.GetProperty("t").GetInt64());
        Assert.Equal(10_000, root.GetProperty("interval").GetInt32());
        Assert.Equal(2, root.GetProperty("mapsSurveyed").GetInt32());
        Assert.Equal(5, root.GetProperty("playersSurveyed").GetInt32());

        var a = Map(root, MapA);
        Assert.Equal(4, a.GetProperty("players").GetInt32());
        Assert.Equal(3, a.GetProperty("mobs").GetInt32());
        // The dimensions the rects were anchored against, published so the raw tiles can be turned back
        // into rects offline.
        Assert.Equal(60, a.GetProperty("xs").GetInt32());
        Assert.Equal(60, a.GetProperty("ys").GetInt32());
        Assert.Equal(0.5,    a.GetProperty("peerDrawnFraction").GetDouble());
        Assert.Equal(0.3333, a.GetProperty("peerStrictFraction").GetDouble());
        Assert.Equal(0.3333, a.GetProperty("mobDrawnFraction").GetDouble());
        Assert.Equal(0.25,   a.GetProperty("mobStrictFraction").GetDouble());
        Assert.Equal(0.0,    a.GetProperty("peerDrawnMin").GetDouble());
        Assert.Equal(0.6667, a.GetProperty("peerDrawnMax").GetDouble());
        Assert.False(a.GetProperty("rawCapped").GetBoolean());

        // The raw tiles are the survey's own input, unchanged: everything above can be recomputed from them
        // offline, which is why they are published at all.
        Assert.Equal(new[] { new[] { 1, 30, 30 }, new[] { 2, 35, 33 }, new[] { 3, 39, 30 }, new[] { 4, 50, 30 } },
                     Triples(a.GetProperty("playerPositions")));
        Assert.Equal(new[] { new[] { 33, 33 }, new[] { 21, 30 }, new[] { 5, 5 } },
                     Triples(a.GetProperty("mobPositions")));

        var b = Map(root, MapB);
        Assert.Equal(80, b.GetProperty("xs").GetInt32());
        Assert.Equal(1, b.GetProperty("players").GetInt32());
        Assert.Equal(2, b.GetProperty("mobs").GetInt32());
        // No peer on the map: a share of nothing is published as 0, not as NaN and not as 1.
        Assert.Equal(0.0, b.GetProperty("peerDrawnFraction").GetDouble());
        Assert.Equal(0.0, b.GetProperty("peerStrictFraction").GetDouble());
        Assert.Equal(0.0, b.GetProperty("peerDrawnMin").GetDouble());
        Assert.Equal(0.0, b.GetProperty("peerDrawnMax").GetDouble());
        Assert.Equal(0.5, b.GetProperty("mobDrawnFraction").GetDouble());
        Assert.Equal(0.5, b.GetProperty("mobStrictFraction").GetDouble());
    }

    /// <summary>A viewer standing on a map edge is surveyed with the camera the client actually shows it —
    /// the clamped, non-following one — and not with a rect centred on a tile the client never centres on.
    ///
    /// <para>Separate from the fact above because it fails for a different reason: an anchor that always
    /// subtracted half the viewport would put a corner viewer's rect half off the map, and every fraction
    /// taken near an edge would come out too low. The town maps are where players actually stand.</para>
    ///
    /// <para>Viewer at (0,0) on a 60x60 map: the camera cannot scroll left or up, so the anchor is (0,0) and
    /// the rect's origin is the viewer's own tile — strict x 0..16, y 0..14, drawn x -1..17, y -1..15. Both
    /// peers below therefore fall inside its drawn rect, while each of THEM, standing in the middle branch,
    /// sees only the other.</para></summary>
    [Fact]
    public void AViewerInAMapCornerGetsTheClampedCameraTheClientDraws()
    {
        var maps = new[]
        {
            new World.OnlineRegistry.MapPositions(MapA,
                new (uint, ushort, ushort)[] { (1, 0, 0), (2, 16, 14), (3, 17, 15) },
                Array.Empty<(ushort, ushort)>()),
        };

        using var doc = JsonDocument.Parse(ViewportSurvey.Render(maps, t: 0, intervalMs: 10_000));
        var a = Map(doc.RootElement, MapA);
        _out.WriteLine(a.ToString());

        // Viewer 1 (0,0) has both peers in its drawn rect -> 1.0. Viewer 2 (16,14) is in the middle branch
        // -> origin (8,7), drawn x 7..25, y 6..22: it sees (17,15) but not (0,0) -> 0.5. Viewer 3 (17,15) ->
        // origin (9,8), drawn x 8..26, y 7..23: the same answer, 0.5. Mean (1 + 0.5 + 0.5)/3 = 0.6667.
        //
        // A rect centred with no clamp would give viewer 1 an origin of (-8,-7), drawn x -9..9, y -8..8, so
        // it would see NEITHER peer and the mean would come out 0.3333 — the shape of the error this fact
        // exists to catch, and the reason the numbers below are 0.6667/0.5/1.0 rather than a single value.
        Assert.Equal(0.6667, a.GetProperty("peerDrawnFraction").GetDouble());
        Assert.Equal(0.5, a.GetProperty("peerDrawnMin").GetDouble());
        Assert.Equal(1.0, a.GetProperty("peerDrawnMax").GetDouble());
    }

    // =====================================================================================================

    /// <summary>The survey copies under <c>World._lock</c> and computes outside it — and says so by refusing
    /// to run at all for a caller that is holding the lock.
    ///
    /// <para>The silent failure: the computation is O(players² + players × mobs) rect tests, about 282,000 of
    /// them at 400 players and 305 mobs on one map. Run inside <c>_lock</c> it would produce exactly the same
    /// document while stalling the world tick for as long as it took, on a ten-second cadence, forever — a
    /// performance defect that no test and no log line would report. The guard is cheap (one
    /// <c>Monitor.IsEntered</c> per document) and it is the only thing that makes the discipline checkable
    /// from outside.</para>
    ///
    /// <para>FALSIFIED by moving the render inside the lock — wrapping the body of
    /// <c>ViewportSurvey.Render(World)</c> in <c>world.UnderWorldLockForTest</c> makes the first half of this
    /// fact (the normal path) throw (recorded in the report).</para></summary>
    [Fact]
    public void TheSurveyComputesOutsideTheWorldLockAndRefusesToComputeInsideIt()
    {
        // The production path: nobody holds the lock, so the document renders.
        string json = ViewportSurvey.Render(_fx.World);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("mapsSurveyed").GetInt32() >= 0);

        // The same call from inside the lock is refused rather than quietly served.
        var ex = Assert.Throws<InvalidOperationException>(
            () => _fx.World.UnderWorldLockForTest(() => ViewportSurvey.Render(_fx.World)));
        _out.WriteLine(ex.Message);
        Assert.Contains("World._lock", ex.Message);
    }

    /// <summary>A world with nobody in it renders a valid document that says so, rather than throwing or
    /// publishing a fraction of nothing.
    ///
    /// <para>A fresh <see cref="World"/>, not the collection's: that one is shared and other classes leave
    /// players standing on maps, so "empty" cannot be asserted over it.</para></summary>
    [Fact]
    public void AnEmptyWorldRendersAValidDocumentWithNothingSurveyed()
    {
        var empty = new World();

        using var doc = JsonDocument.Parse(ViewportSurvey.Render(empty));
        var root = doc.RootElement;
        _out.WriteLine(root.ToString());

        Assert.Equal(0, root.GetProperty("mapsSurveyed").GetInt32());
        Assert.Equal(0, root.GetProperty("playersSurveyed").GetInt32());
        Assert.Equal(0, root.GetProperty("maps").GetArrayLength());
        Assert.True(root.GetProperty("t").GetInt64() > 0);
    }

    // =====================================================================================================

    /// <summary>The cost, at the population the spatial-index question is about: 400 players and 305 mobs on
    /// one map. Prints the lock hold and the whole document's render time; asserts only that the document is
    /// the right shape, because a timing assertion on a laptop with three other workers on it would be a
    /// flake rather than a fact. The numbers are in the report.</summary>
    [Fact]
    public void FourHundredPlayersAndThreeHundredMobsRenderOnOneMap()
    {
        const int Players = 400, Mobs = 305;

        var world = new World();
        var rng = new Random(1998);
        for (int i = 0; i < Players; i++)
        {
            var character = new Character
            {
                SchemaVersion = Character.CurrentSchemaVersion,
                Id = world.AllocatePlayerId(),
                Name = $"bench{i}",
                Map = MapA,
                X = (ushort)rng.Next(0, 60),
                Y = (ushort)rng.Next(0, 60),
            };
            var session = new Session(new RecordingOutbound($"bench:{i}"), 2005, _fx.Store, world, character);
            world.EnterMap(session, MapA);
        }
        for (int i = 0; i < Mobs; i++)
            world.AddMob(MapA, new Mob
            {
                Id = (uint)(900_000 + i), Name = "bench", Key = "bench",
                X = (ushort)rng.Next(0, 60), Y = (ushort)rng.Next(0, 60), Hp = 10, MaxHp = 10,
            });

        // The lock hold on its own — the part that can touch the tick. Best of several, because the median
        // on a machine running three other workers' builds is a measure of them, not of this.
        var hold = new List<double>();
        for (int i = 0; i < 20; i++)
        {
            var sw = Stopwatch.StartNew();
            var snap = world.Online.PositionSurvey();
            sw.Stop();
            Assert.Single(snap);
            hold.Add(sw.Elapsed.TotalMilliseconds);
        }

        // The whole document: snapshot, rect tests and JSON.
        var whole = new List<double>();
        string json = "";
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            json = ViewportSurvey.Render(world);
            sw.Stop();
            whole.Add(sw.Elapsed.TotalMilliseconds);
        }

        // The first sample of each is reported separately: it carries the JIT of everything below it, and a
        // max that turns out to BE the first sample is a different fact from one that is not.
        double holdFirst = hold[0], wholeFirst = whole[0];
        hold.Sort(); whole.Sort();
        _out.WriteLine($"{Players} players + {Mobs} added mobs on map {MapA}: " +
                       $"lock hold first {holdFirst:F3}ms whole-document first {wholeFirst:F1}ms; " +
                       $"lock hold min {hold[0]:F3}ms median {hold[hold.Count / 2]:F3}ms max {hold[^1]:F3}ms; " +
                       $"whole document min {whole[0]:F1}ms median {whole[whole.Count / 2]:F1}ms " +
                       $"max {whole[^1]:F1}ms; {json.Length} bytes");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("mapsSurveyed").GetInt32());
        Assert.Equal(Players, root.GetProperty("playersSurveyed").GetInt32());

        var m = Map(root, MapA);
        Assert.Equal(Players, m.GetProperty("players").GetInt32());
        // At least the mobs this fact added: entering the map also materialised the map's own spawn roster,
        // which is the world doing its job and is counted like any other mob.
        int mobsOnMap = m.GetProperty("mobs").GetInt32();
        Assert.True(mobsOnMap >= Mobs, $"{mobsOnMap} alive mobs on the map against the {Mobs} added");
        Assert.Equal(Players, m.GetProperty("playerPositions").GetArrayLength());
        Assert.Equal(mobsOnMap, m.GetProperty("mobPositions").GetArrayLength());
        foreach (var name in new[] { "peerDrawnFraction", "peerStrictFraction", "mobDrawnFraction",
                                     "mobStrictFraction", "peerDrawnMin", "peerDrawnMax" })
        {
            double v = m.GetProperty(name).GetDouble();
            Assert.True(v >= 0 && v <= 1, $"{name} is {v}, which is not a fraction");
        }
        // The strict rect is inside the drawn one, so its share can never be the larger of the two.
        Assert.True(m.GetProperty("peerStrictFraction").GetDouble() <= m.GetProperty("peerDrawnFraction").GetDouble());
        Assert.True(m.GetProperty("mobStrictFraction").GetDouble() <= m.GetProperty("mobDrawnFraction").GetDouble());
    }

    // =====================================================================================================

    private static JsonElement Map(JsonElement root, ushort id)
    {
        foreach (var m in root.GetProperty("maps").EnumerateArray())
            if (m.GetProperty("map").GetUInt16() == id) return m;
        throw new Xunit.Sdk.XunitException($"the document carries no entry for map {id}");
    }

    private static int[][] Triples(JsonElement array)
    {
        var rows = new List<int[]>();
        foreach (var row in array.EnumerateArray())
        {
            var cells = new List<int>();
            foreach (var c in row.EnumerateArray()) cells.Add(c.GetInt32());
            rows.Add(cells.ToArray());
        }
        return rows.ToArray();
    }
}
