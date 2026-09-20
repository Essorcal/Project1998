using System.Text.Json;
using System.Text.Json.Serialization;
using Shared;

namespace Server;

/// <summary>
/// Publishes <c>run/viewport.json</c> — where players and mobs actually STAND, and what share of them a
/// viewer can see, sampled on the status thread's cadence.
///
/// <para>WHY IT EXISTS. The viewport sweep is about 80% of the beat at 400 players
/// (<c>briefs/reports/viewport-profile-opus.md</c>): every viewer tests every entity on its map. A per-map
/// spatial index would cut that scan to the viewer's own cells, and its saving is exactly
/// <c>(1 - f)</c>, where <c>f</c> is the share of a map's entities that fall inside one viewer's rect. Every
/// number we have for <c>f</c> comes from the load script, which stands every bot in one clump — there
/// <c>f</c> is about 1 and an index would save nothing. Nobody has measured it on real players, and the
/// index is a large change to make on an unmeasured fraction. This document is the measurement: run it on
/// the deployment for a day of real play and the decision has a number behind it.</para>
///
/// <para>THE DOCUMENT. Written beside <c>run/status.json</c>, on the same interval
/// (<c>P1998_STATUS_MS</c>), by its OWN loop rather than the status loop's — see <see cref="Loop"/>.
/// <code>
/// {
///   "t": 1758300000000, "interval": 10000,
///   "maps": [
///     { "map": 1, "players": 12, "mobs": 40, "xs": 220, "ys": 220,
///       "peerDrawnFraction": 0.18, "peerStrictFraction": 0.15,
///       "mobDrawnFraction": 0.31, "mobStrictFraction": 0.28,
///       "peerDrawnMin": 0.0, "peerDrawnMax": 0.45,
///       "rawCapped": false,
///       "playerPositions": [[1001,40,52],…], "mobPositions": [[44,50],…] }
///   ],
///   "mapsSurveyed": 1, "playersSurveyed": 12
/// }
/// </code>
/// <list type="table">
/// <item><term><c>t</c></term><description>when the snapshot was taken, Unix milliseconds</description></item>
/// <item><term><c>interval</c></term><description>the publishing period in ms, so a reader knows what two consecutive documents are separated by</description></item>
/// <item><term><c>maps</c></term><description>one entry per map that had at least one player at <c>t</c>; a map with no viewer has no fraction to take</description></item>
/// <item><term><c>map</c></term><description>the map id</description></item>
/// <item><term><c>players</c></term><description>players on that map (the COUNT; the tiles are <c>playerPositions</c>)</description></item>
/// <item><term><c>mobs</c></term><description>ALIVE mobs on that map, NPCs included — the sweep tests them too (the count; tiles are <c>mobPositions</c>)</description></item>
/// <item><term><c>xs</c>, <c>ys</c></term><description>the map's tile dimensions AS THE SURVEY RESOLVED THEM. Published because the rect anchor clamps at a map edge, so the raw tiles below cannot be turned back into rects offline without them — and because a map missing from the content registry shows up here as 65535 rather than as a silently wrong fraction</description></item>
/// <item><term><c>peerDrawnFraction</c></term><description>mean over viewers of (peers inside that viewer's DRAWN 19x17 rect) / (peers on the map). 0 when the map has one player, because there is no peer to see</description></item>
/// <item><term><c>peerStrictFraction</c></term><description>the same mean against the STRICT 17x15 rect — the rect a 0x07/0x33 spawn is accepted in</description></item>
/// <item><term><c>mobDrawnFraction</c></term><description>mean over viewers of (alive mobs inside the drawn rect) / (alive mobs on the map). 0 when the map has no mob</description></item>
/// <item><term><c>mobStrictFraction</c></term><description>the same mean against the strict rect</description></item>
/// <item><term><c>peerDrawnMin</c>, <c>peerDrawnMax</c></term><description>the lowest and highest SINGLE viewer's drawn peer fraction on that map. The mean is what a spatial index saves on average; these two are what it saves for the unluckiest and the luckiest viewer, and a mean of 0.2 made of 0.0 and 1.0 is a different world from one made of twenty 0.2s</description></item>
/// <item><term><c>rawCapped</c></term><description>true when the raw arrays below were cut to keep one map's raw part under 256 KB. The counts above are always the real totals; only the tile lists are cut</description></item>
/// <item><term><c>playerPositions</c></term><description><c>[[id, x, y], …]</c> — the raw tiles, so a reader can recompute any of the fractions above offline, or try a different cell size against them, without a server change. That is the whole point of publishing them: the fractions answer today's question and the positions answer the next one</description></item>
/// <item><term><c>mobPositions</c></term><description><c>[[x, y], …]</c> — the same, for mobs. No id: nothing offline can do anything with a transient mob id</description></item>
/// <item><term><c>mapsSurveyed</c></term><description>how many maps are in <c>maps</c></description></item>
/// <item><term><c>playersSurveyed</c></term><description>players across all of them — the population the fractions were taken over</description></item>
/// </list>
/// The raw arrays are NOT named <c>players</c>/<c>mobs</c> even though the brief's sketch called them that:
/// those two names are already the counts on the same object, and one name cannot be both.</para>
///
/// <para>LOCK DISCIPLINE, which is the only part of this with any risk in it.
/// <see cref="World.OnlineRegistry.PositionSurvey"/> holds <c>World._lock</c> for two array fills per map and
/// returns. Everything below — every rect test, every division, every byte of JSON — runs on the copies,
/// outside the lock, and <see cref="Render(World)"/> REFUSES to run if its caller is holding the world lock
/// (see there). The cost is what makes that mandatory rather than tidy: the fractions are
/// O(players² + players × mobs) rect tests per map, about 282,000 of them at 400 players and 305 mobs on one
/// map, and doing that inside <c>_lock</c> would stall the tick for as long as it took.</para>
///
/// <para>NOTHING HERE IS ON THE TICK PATH and no player can see it. It is a file on a timer, like
/// <c>status.json</c>, and <c>status.json</c> is deliberately not touched: its first three fields are the
/// launcher's DTO, and a hold driver's samples of it should not change because a diagnostic was added.</para>
///
/// <para>THE RECT IS REPLICATED, NOT SHARED, and that is a real limitation worth stating. The live test is
/// <c>Session.ViewRect.Contains</c> (<c>Server/Session.WorldApi.cs</c>), a private struct on a session,
/// anchored by <c>Session.EdgeAwareAnchor</c> (<c>Server/Session.Entity.cs:1297</c>) with
/// <c>ViewW</c>/<c>ViewH</c> = 17/15 and <c>ShowPad</c>/<c>HidePad</c> = 0/1
/// (<c>Server/Session.cs:1477</c>). None of that is reachable from a thread that holds no session, so
/// <see cref="Anchor"/> below reproduces the arithmetic against <c>Content.Maps</c>'s map size. Two
/// differences follow, both on purpose: realm-centre (F4, the frozen camera) is not modelled, so a viewer
/// holding F4 is surveyed as if its camera followed it; and the map size comes from the content registry
/// rather than from the viewer's character record, which differ only across a <c>@reload</c> that resized a
/// map. Both are one-viewer errors in an aggregate, and neither can affect the server.</para>
/// </summary>
internal static class ViewportSurvey
{
    /// <summary>Beside the status document, and derived from it rather than from a knob of its own: this is a
    /// diagnostic for a question that will be answered in a few days, and a permanent public knob is a bigger
    /// commitment than that. <c>P1998_STATUS_FILE=-</c> therefore disables this too — the one switch that
    /// turns off "the server writes documents into run/" turns off both.</summary>
    private static readonly string Path =
        ServerConfig.Current.StatusFile is { Length: > 0 } configured
            ? (configured == "-" ? "-" : System.IO.Path.Combine(
                   System.IO.Path.GetDirectoryName(configured) is { Length: > 0 } dir
                       ? dir : Shared.RepoPaths.RunDir(),
                   "viewport.json"))
            : System.IO.Path.Combine(Shared.RepoPaths.RunDir(), "viewport.json");

    private static readonly int IntervalMs = ServerConfig.Current.StatusMs;

    private static bool Disabled => Path == "-";

    // The client's drawn tile viewport and the two pads the sweeps test with, copied from Session
    // (Session.Entity.cs:1270-1271 and Session.cs:1477-1478) because they are private consts on a type this
    // thread cannot reach. ShowPad is the strict rect a spawn is accepted in; HidePad is the wider rect the
    // client actually DRAWS, one tile past the viewport on every side.
    private const int ViewW = 17, ViewH = 15;
    private const int ShowPad = 0, HidePad = 1;

    /// <summary>Rough JSON cost of one raw entry, used only to decide where to cut. Deliberately generous:
    /// cutting a little early on a map with thousands of entities costs a reader nothing, and the counts and
    /// the fractions are never cut.</summary>
    private const int PlayerRawBytes = 24, MobRawBytes = 14;

    /// <summary>The per-map raw budget. 256 KB is enough for roughly ten thousand players or twenty thousand
    /// mobs on one map — far past anything the deployment will see — and it bounds the document against a
    /// runaway spawn rather than against normal play.</summary>
    private const int RawBudgetBytes = 256 * 1024;

    private sealed record MapDoc(
        [property: JsonPropertyName("map")]                ushort Map,
        [property: JsonPropertyName("players")]            int Players,
        [property: JsonPropertyName("mobs")]               int Mobs,
        [property: JsonPropertyName("xs")]                 int Xs,
        [property: JsonPropertyName("ys")]                 int Ys,
        [property: JsonPropertyName("peerDrawnFraction")]  double PeerDrawnFraction,
        [property: JsonPropertyName("peerStrictFraction")] double PeerStrictFraction,
        [property: JsonPropertyName("mobDrawnFraction")]   double MobDrawnFraction,
        [property: JsonPropertyName("mobStrictFraction")]  double MobStrictFraction,
        [property: JsonPropertyName("peerDrawnMin")]       double PeerDrawnMin,
        [property: JsonPropertyName("peerDrawnMax")]       double PeerDrawnMax,
        [property: JsonPropertyName("rawCapped")]          bool RawCapped,
        [property: JsonPropertyName("playerPositions")]    int[][] PlayerPositions,
        [property: JsonPropertyName("mobPositions")]       int[][] MobPositions);

    private sealed record Doc(
        [property: JsonPropertyName("t")]               long T,
        [property: JsonPropertyName("interval")]        int Interval,
        [property: JsonPropertyName("maps")]            IReadOnlyList<MapDoc> Maps,
        [property: JsonPropertyName("mapsSurveyed")]    int MapsSurveyed,
        [property: JsonPropertyName("playersSurveyed")] int PlayersSurveyed);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>The document, as text, from the world — the seam the tests drive, and the one place the
    /// snapshot and the computation meet.
    ///
    /// <para>The guard is the lock discipline, stated as code rather than as a comment: the snapshot is the
    /// only thing allowed inside <c>World._lock</c>, so a caller that is ALREADY holding it would drag the
    /// whole 282,000-test computation in there with it. That is a stall of the tick, and it is silent — the
    /// document would still come out correct. So it throws instead, and a test holds the lock and watches it
    /// throw.</para></summary>
    internal static string Render(World world)
    {
        // Under the lock: copies only. See World.OnlineRegistry.PositionSurvey.
        var maps = world.Online.PositionSurvey();

        if (world.HoldsWorldLock)
            throw new InvalidOperationException(
                "ViewportSurvey.Render computes outside World._lock and was called by a caller holding it — " +
                "the rect tests are O(players^2 + players*mobs) and would stall the tick");

        return Render(maps, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), IntervalMs);
    }

    /// <summary>The computation, on copies, with nothing world-shaped in reach — which is how a test states
    /// an exact fraction for an exact arrangement of players and mobs.</summary>
    internal static string Render(World.OnlineRegistry.MapPositions[] maps, long t, int intervalMs)
    {
        var docs = new List<MapDoc>(maps.Length);
        int players = 0;

        foreach (var m in maps)
        {
            players += m.Players.Length;

            var (xs, ys) = MapSize(m.Map);

            // Sums over viewers, so the published figure is the mean per VIEWER rather than a ratio of
            // totals: a map's cost is paid once per viewer, and it is the per-viewer share an index cuts.
            double peerDrawn = 0, peerStrict = 0, mobDrawn = 0, mobStrict = 0;
            double minDrawn = double.MaxValue, maxDrawn = 0;

            int peers = m.Players.Length - 1;     // every OTHER player on the map
            int mobs  = m.Mobs.Length;

            foreach (var viewer in m.Players)
            {
                var (ox, oy) = Origin(viewer.X, viewer.Y, xs, ys);

                int pd = 0, ps = 0;
                foreach (var other in m.Players)
                {
                    if (other.Id == viewer.Id) continue;
                    if (Contains(ox, oy, other.X, other.Y, HidePad))  pd++;
                    if (Contains(ox, oy, other.X, other.Y, ShowPad))  ps++;
                }

                int md = 0, ms = 0;
                foreach (var mob in m.Mobs)
                {
                    if (Contains(ox, oy, mob.X, mob.Y, HidePad)) md++;
                    if (Contains(ox, oy, mob.X, mob.Y, ShowPad)) ms++;
                }

                double drawnShare = peers > 0 ? (double)pd / peers : 0;
                peerDrawn  += drawnShare;
                peerStrict += peers > 0 ? (double)ps / peers : 0;
                mobDrawn   += mobs  > 0 ? (double)md / mobs  : 0;
                mobStrict  += mobs  > 0 ? (double)ms / mobs  : 0;

                if (drawnShare < minDrawn) minDrawn = drawnShare;
                if (drawnShare > maxDrawn) maxDrawn = drawnShare;
            }

            int viewers = m.Players.Length;       // never 0: PositionSurvey skips maps with no player
            if (minDrawn == double.MaxValue) minDrawn = 0;

            // The raw tiles, cut only if one map's raw part would run past the budget. Players first: an id
            // is the only part of this document that can be joined to anything else.
            int budget = RawBudgetBytes;
            int pTake = Math.Min(m.Players.Length, budget / PlayerRawBytes);
            budget -= pTake * PlayerRawBytes;
            int mTake = Math.Min(m.Mobs.Length, Math.Max(0, budget) / MobRawBytes);

            var rawPlayers = new int[pTake][];
            for (int i = 0; i < pTake; i++)
                rawPlayers[i] = new[] { (int)m.Players[i].Id, m.Players[i].X, m.Players[i].Y };
            var rawMobs = new int[mTake][];
            for (int i = 0; i < mTake; i++)
                rawMobs[i] = new int[] { m.Mobs[i].X, m.Mobs[i].Y };

            docs.Add(new MapDoc(
                m.Map, m.Players.Length, m.Mobs.Length, xs, ys,
                Round(peerDrawn / viewers), Round(peerStrict / viewers),
                Round(mobDrawn / viewers),  Round(mobStrict / viewers),
                Round(minDrawn), Round(maxDrawn),
                pTake < m.Players.Length || mTake < m.Mobs.Length,
                rawPlayers, rawMobs));
        }

        return JsonSerializer.Serialize(new Doc(t, intervalMs, docs, docs.Count, players), Json);
    }

    /// <summary>Four decimals. These are shares of a population that is rarely above a few hundred, so the
    /// fifth decimal is below the quantisation of the thing being measured, and a full double per field
    /// would triple the document for no reader.</summary>
    private static double Round(double v) => Math.Round(v, 4);

    /// <summary>The map's tile dimensions from the content registry. A map with no row — which should not
    /// happen for a map somebody is standing on — is treated as large, which is the non-edge branch of the
    /// anchor below and the right answer for every map big enough to matter.</summary>
    private static (int xs, int ys) MapSize(ushort map) =>
        Content.Maps.TryGetValue(map, out var mi) && mi.Xs > 0 && mi.Ys > 0
            ? (mi.Xs, mi.Ys)
            : (ushort.MaxValue, ushort.MaxValue);

    /// <summary>The top-left tile of this viewer's drawn rect — <c>(X - vx, Y - vy)</c>, the same origin the
    /// 0x04 camera writes. See the class doc for why this is a replica.</summary>
    private static (int ox, int oy) Origin(int cx, int cy, int xs, int ys) =>
        (cx - Anchor(cx, xs, ViewW), cy - Anchor(cy, ys, ViewH));

    /// <summary>One axis of <c>Session.EdgeAwareAnchor</c> (<c>Server/Session.Entity.cs:1297</c>): the
    /// screen tile the self is drawn at. A map narrower/shorter than the view is centred in it; near a map
    /// edge the camera stops and the self walks across a static view; otherwise the self is centred.</summary>
    private static int Anchor(int c, int span, int view)
    {
        int half = view / 2;
        int v = span < view ? c + (view - span) / 2
              : c < half    ? c
              : c >= span - half ? c - span + view
              : half;
        return Math.Clamp(v, 0, view - 1);
    }

    /// <summary><c>Session.ViewRect.Contains</c>, against an origin already computed — the identical four
    /// integer compares (<c>Server/Session.WorldApi.cs:104</c>).</summary>
    private static bool Contains(int ox, int oy, int mx, int my, int pad) =>
        mx >= ox - pad && mx < ox + ViewW + pad
     && my >= oy - pad && my < oy + ViewH + pad;

    /// <summary>
    /// Its OWN loop, not a second call inside <see cref="StatusFile.Loop"/>'s iteration, and the reason is
    /// the status document rather than this one. The status loop writes, then sleeps the interval; a survey
    /// call in that body would put this document's cost — 282,000 rect tests and a few hundred kilobytes of
    /// JSON at 400 players — between two status writes, so the launcher's pill and every hold driver's
    /// sample would drift by however long the survey took, and a survey that threw would land in the status
    /// loop's catch and be logged as a status write failure. A separate task shares nothing but the clock:
    /// this document can be late, or absent, and <c>status.json</c> keeps its cadence exactly.
    /// </summary>
    public static async Task Loop(World world)
    {
        if (Disabled) { Log.Info("viewport survey: publishing disabled (P1998_STATUS_FILE=-)"); return; }

        Log.Info($"viewport survey: publishing {Path} every {IntervalMs}ms");

        while (true)
        {
            try { Write(world); }
            catch (Exception ex) { Log.Warn("viewport survey write failed — retrying next interval", ex); }
            // EXPECTED: cancellation at process exit is the only thing that lands here, exactly as in
            // StatusFile.Loop. A write that FAILS is logged above.
            try { await Task.Delay(IntervalMs); } catch { return; }
        }
    }

    /// <summary>Temp file plus an atomic rename, for the same reason <see cref="StatusFile"/> does it: a
    /// reader polling on its own schedule otherwise eventually catches a half-written document.</summary>
    private static void Write(World world)
    {
        if (Disabled) return;
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, Render(world));
        File.Move(tmp, Path, overwrite: true);
    }
}
