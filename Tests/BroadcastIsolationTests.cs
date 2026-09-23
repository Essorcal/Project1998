using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The two broadcast helpers' per-peer boundary — <c>World.Broadcast</c> and <c>World.BroadcastArea</c> —
/// across the change that stopped them allocating a closure per peer per call.
///
/// <para><b>What needs a test here, by the "would this fail loudly?" rule.</b> Two things, and both are
/// silent.</para>
///
/// <para>The first is the isolation. Each helper wraps its per-peer send in <c>World.Try</c> so that one
/// peer whose delivery throws is skipped and logged while every other peer on the map still receives. On the
/// healthy path nothing observable happens, so an edit that moved the <c>catch</c>, widened it, narrowed it,
/// or reordered the peers would look exactly like an edit that did not — until one session throws on a live
/// server and everyone behind it in the loop silently misses a mob move.</para>
///
/// <para>The second is the allocation, which is what the change was for. <c>Try(() =&gt; send(p), …)</c>
/// captures both <c>send</c> and the loop's own <c>p</c>, so it is a display class plus a delegate PER PEER
/// PER CALL: measured on this repo at 96 B a peer, 38,408 B of the 45,361 B one <c>Broadcast</c> cost at 400
/// players, on a helper the tick calls once per queued mob move and once per turn and every player action
/// reaches from its own session thread. A later edit that puts a capture back is invisible at the call site,
/// in a review diff and in every behaviour.</para>
/// </summary>
[Collection("world")]
public class BroadcastIsolationTests
{
    private readonly SessionFixture _fx;

    public BroadcastIsolationTests(SessionFixture fx) => _fx = fx;

    private const string Boom = "this peer's send refused";

    /// <summary>A peer whose <c>send</c> throws is skipped and logged under the site name
    /// <c>'Broadcast'</c>, and every other peer on the map still receives, in the peers' own order.
    ///
    /// <para>Falsification: remove the <c>catch</c> from <c>World.Try&lt;T&gt;</c> and the throw escapes
    /// <c>Broadcast</c> — the two peers queued behind the thrower are never sent to and this fact goes red on
    /// the <c>InvalidOperationException</c> itself. Run it, confirm red, restore.</para></summary>
    [Fact]
    public void BroadcastSkipsOnlyTheThrowingPeerAndLogsItsSite()
    {
        var (a, b, c, d) = FourPlayers("Bcast");
        try
        {
            var seen = new List<uint>();
            using var sink = LogLineSink.Acquire();

            _fx.World.Broadcast(SessionFixture.HomeMap, p =>
            {
                if (p == b) throw new InvalidOperationException(Boom);
                seen.Add(p.PlayerId);
            });

            Assert.Equal(new[] { a.PlayerId, c.PlayerId, d.PlayerId }, Ours(seen, a, b, c, d));
            Assert.Contains("isolated step 'Broadcast' threw — skipped, the sweep continues",
                            sink.LineContaining("isolated step 'Broadcast' threw"));
            Assert.Equal(LogLevel.Error, sink.EntryContaining("isolated step 'Broadcast' threw").Level);
        }
        finally { Leave(a, b, c, d); }
    }

    /// <summary>The same fact for <c>World.BroadcastArea</c>, whose site name is <c>'BroadcastArea'</c>. The
    /// box is the SAMEAREA one every sound and speech line uses; on a 12x12 map it collapses to the whole
    /// map, so all four players are peers and the claim is about the loop, not the filter.
    ///
    /// <para>Same falsification as above, through the same helper.</para></summary>
    [Fact]
    public void BroadcastAreaSkipsOnlyTheThrowingPeerAndLogsItsSite()
    {
        var (a, b, c, d) = FourPlayers("Area");
        try
        {
            var seen = new List<uint>();
            using var sink = LogLineSink.Acquire();

            _fx.World.BroadcastArea(SessionFixture.HomeMap, 5, 10, World.SoundHalfW, World.SoundHalfH, p =>
            {
                if (p == b) throw new InvalidOperationException(Boom);
                seen.Add(p.PlayerId);
            });

            Assert.Equal(new[] { a.PlayerId, c.PlayerId, d.PlayerId }, Ours(seen, a, b, c, d));
            Assert.Contains("isolated step 'BroadcastArea' threw — skipped, the sweep continues",
                            sink.LineContaining("isolated step 'BroadcastArea' threw"));
            Assert.Equal(LogLevel.Error, sink.EntryContaining("isolated step 'BroadcastArea' threw").Level);
        }
        finally { Leave(a, b, c, d); }
    }

    /// <summary><c>World.Broadcast</c> allocates NOTHING PER CALL of its own — no closure per peer, and
    /// since the peers snapshot became a pooled rent, no snapshot array either. The only bytes a broadcast
    /// costs are the caller's own <c>send</c> delegate, and this fact hands it a cached static one so that
    /// share is zero too and the measurement is the helper alone.
    ///
    /// <para>Two passes over the SAME call site, as <see cref="TickStepIsolationTests"/> does: each lambda
    /// expression in the source has its own cache slot, so the first pass pays the one cached delegate for
    /// this site and the second pass is the claim. A crowd, not four peers: at four the closure shape's 96 B
    /// apiece is small enough that a bound loose enough to cover a snapshot would swallow it, and the
    /// falsification would come back green and pin nothing.</para>
    ///
    /// <para><b>Why each call is measured on its own, and why the bound is not zero.</b> The peers snapshot
    /// is rented from <c>ArrayPool&lt;Session&gt;.Shared</c>, which is process-wide and trims itself on
    /// every Gen2 callback. At HIGH memory pressure (the machine's memory load at 90% or more of the GC's
    /// high-load threshold, about 81% of physical RAM by default) that trim empties every thread's own
    /// cached buffer as well as the shared stacks. When it lands between one call's return of the buffer and
    /// the next call's rent, that next rent allocates one fresh buffer: 536 B, the <c>Session[64]</c> a
    /// 40-peer crowd rents (1,048 B for a <c>Session[128]</c> if more than 64 stand on the map). That is one
    /// buffer per Gen2, never per call, but nothing bounds how many Gen2s fall inside one pass on a busy,
    /// memory-loaded machine. The old assertion summed the pass, <c>bytes &lt; Calls</c> = 2,000 B, so four
    /// re-rents took it red. It went red on heads that never touched <c>World.Broadcast</c>: 520 B and
    /// 1,152 B earlier (PR #261's upstream check, <c>reviews/server-261.review.json</c>), which is why the
    /// bound left zero, and then on 2026-09-23 3,728 B on PR #274's upstream check (run 35891352840),
    /// 5,400 B on the send-counters worker's local Debug suite, and about 1 in 20 fork runs on the drain
    /// branch. Reproduced (<c>briefs/reports/broadcast-alloc-flake-opus.md</c>): beside a forced-Gen2 storm
    /// at high pressure the summed fact went red 20 times in 400, every time at an exact multiple of 536 B
    /// (2,144, 2,680 and 3,216 B: four, five and six buffers). At the same storm below high pressure it went
    /// red 0 times in 400, because the thread's own cached buffer survives the trim there.</para>
    ///
    /// <para>So the claim is measured one call at a time. Each call is its own window: a re-rent is one call
    /// allocating one buffer, and a per-call cost is every call allocating. The assertion is that fewer than
    /// one call in a hundred allocates anything (fewer than 20 of 2,000). Under the same storm that is red
    /// 0 times in 400. In a paired run of 400 passes, 272 saw at least one re-rent and the summed bound
    /// would have been red on 35 of them. Every allocating call was exactly 536 B, and no pass had more
    /// than six. Both falsifications below make all 2,000 calls allocate. The resolution is the honest
    /// limit: an allocation on fewer than one call in a hundred passes this fact. Do not put
    /// <c>Assert.Equal(0, bytes)</c> back, or a summed byte bound. Either asserts the state of a
    /// process-wide pool as well as this helper, and only the second half is a fact about
    /// <c>World.Broadcast</c>.</para>
    ///
    /// <para>Two other ways were weighed. A <c>GC.TryStartNoGCRegion</c> around the pass was tried and does
    /// not hold: ANY induced collection anywhere in the process ends the region (<c>StatusFileTests</c> calls
    /// <c>GC.Collect(2)</c> from its own parallel collection), it failed loudly 8 times in 8 under the storm,
    /// and its own opening full collection queues the very trim it is meant to keep out. A slope, bytes at
    /// N calls against bytes at 2N, was not built: under a steady Gen2 rate the re-rents grow with the
    /// pass's length, and so with N, so a slope separates the two no better than a summed bound does.</para>
    ///
    /// <para>Falsification, twice, both re-run against the per-call bound. (a) Put the capture back:
    /// <c>var p = peers[i]; Try(() =&gt; send(p), "Broadcast")</c>. This goes red with <b>2,000 of 2,000
    /// calls allocating, 3,864 B each</b> over 40 peers, 96 B a peer for the display class and the
    /// delegate. (b) Put the LINQ snapshot back: <c>peers = m.Players.Where(p =&gt; p != except).ToArray()</c>,
    /// with the <c>foreach</c> over it. This goes red with <b>2,000 of 2,000 calls allocating, 1,032 B
    /// each</b>. Both read the same in Debug and Release. Run each, confirm red, restore. The reds against
    /// the zero bound are in <c>briefs/reports/broadcast-alloc-opus.md</c>, the reds against the summed bound
    /// in <c>reviews/test-hygiene-evidence/</c>, and the reds against this one in
    /// <c>briefs/reports/broadcast-alloc-flake-opus.md</c>.</para></summary>
    [Fact]
    public void BroadcastAllocatesNothingPerPeer()
    {
        const int Calls = 2_000;
        const int Crowd = 40;
        // Fewer than one call in a hundred may allocate anything. See the doc comment for why it is not zero.
        const int AllocatingCallsBound = Calls / 100;
        var crowd = ManyPlayers(Crowd, "Alloc");
        try
        {
            int allocating = 0;
            long bytes = 0;
            var firstSizes = new long[AllocatingCallsBound];   // allocated here, outside every measured call
            ZzBcastFactProbeTemp.Start();   // TEMPORARY probe
            for (int pass = 0; pass < 2; pass++)
            {
                _peers = 0;
                ZzBcastFactProbeTemp.StartPass();   // TEMPORARY probe
                allocating = 0;
                bytes = 0;
                for (int i = 0; i < Calls; i++)
                {
                    ZzBcastFactProbeTemp.Begin();   // TEMPORARY probe
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    _fx.World.Broadcast(SessionFixture.HomeMap, ZzBcastFactProbeTemp.ProbePeer);   // TEMPORARY probe delegate
                    long taken = GC.GetAllocatedBytesForCurrentThread() - before;
                    ZzBcastFactProbeTemp.End(i);   // TEMPORARY probe
                    if (taken == 0) continue;
                    if (allocating < firstSizes.Length) firstSizes[allocating] = taken;
                    allocating++;
                    bytes += taken;
                }
            }

            ZzBcastFactProbeTemp.Stop("realFact", Calls); _peers = ZzBcastFactProbeTemp.Peers;   // TEMPORARY probe
            Assert.True(_peers >= Crowd * Calls,
                        $"only {_peers} peer deliveries over {Calls} calls — the arrangement is wrong, not the code");
            // Counted per CALL, not summed per pass: a pool re-rent after a Gen2 trim is ONE call allocating
            // one buffer, a per-call cost is EVERY call allocating. See the doc comment.
            Assert.True(allocating < AllocatingCallsBound,
                        $"{allocating} of {Calls} calls to {Crowd} peers allocated ({bytes} B in all; the first "
                      + $"{Math.Min(allocating, firstSizes.Length)}: "
                      + $"{string.Join(", ", firstSizes.Take(Math.Min(allocating, firstSizes.Length)))} B) — that is a "
                      + "per-call allocation, not the pool re-renting one buffer after a Gen2 trim");
        }
        finally { Leave(crowd); }
    }

    /// <summary>A broadcast to a map whose player list is EMPTY does nothing and, in particular, does not
    /// throw. The case is worth its own fact because the pooled snapshot made it a boundary it was not
    /// before: <c>ArrayPool&lt;T&gt;.Rent(0)</c> answers with the shared empty array rather than a pooled
    /// buffer, and that value still has to survive the clear and the return in the <c>finally</c>. The map
    /// entry outlives its last player — <c>World.LeaveMap</c> removes the session, not the
    /// <c>MapState</c> — so this is a real runtime state, not a contrived one.
    ///
    /// <para>On a map of its own, not the fixture's home map: the world is shared across the collection and
    /// other classes leave sessions standing on <c>HomeMap</c>, so "everyone has left" is only arrangeable
    /// somewhere nobody else goes.</para></summary>
    [Fact]
    public void BroadcastOnAMapWithNoPlayersLeftIsAQuietNoOp()
    {
        const ushort Lonely = 60900;   // outside the map registry, so nothing else in the suite enters it
        var (only, _) = _fx.Player("EmptyMapA", Lonely, x: 5, y: 10);
        _fx.World.LeaveMap(only, Lonely);

        int hits = 0;
        _fx.World.Broadcast(Lonely, _ => hits++);
        _fx.World.BroadcastArea(Lonely, 5, 10, World.SoundHalfW, World.SoundHalfH, _ => hits++);
        Assert.Equal(0, hits);
    }

    /// <summary>The peers a broadcast reaches are the map's players in the map's own order, and
    /// <c>except</c> removes exactly one of them — the two properties every later edit to the snapshot has
    /// to preserve, and the ones a change to how the peers are collected could silently break.</summary>
    [Fact]
    public void BroadcastKeepsThePeerOrderAndTheExceptFilter()
    {
        var (a, b, c, d) = FourPlayers("Order");
        try
        {
            var all = new List<uint>();
            _fx.World.Broadcast(SessionFixture.HomeMap, p => all.Add(p.PlayerId));
            Assert.Equal(new[] { a.PlayerId, b.PlayerId, c.PlayerId, d.PlayerId }, Ours(all, a, b, c, d));

            var minusC = new List<uint>();
            _fx.World.Broadcast(SessionFixture.HomeMap, p => minusC.Add(p.PlayerId), except: c);
            Assert.Equal(new[] { a.PlayerId, b.PlayerId, d.PlayerId }, Ours(minusC, a, b, c, d));
        }
        finally { Leave(a, b, c, d); }
    }

    // ---- arrangement helpers -------------------------------------------------------------------------

    /// <summary>Counted through a static field so the delegate below captures nothing and the allocation
    /// fact measures the helper rather than its own probe.</summary>
    private static int _peers;

    private static readonly Action<Session> CountPeer = static _ => _peers++;

    private (Session a, Session b, Session c, Session d) FourPlayers(string tag)
    {
        var (a, _) = _fx.Player($"{tag}A", SessionFixture.HomeMap, x: 5, y: 10);
        var (b, _) = _fx.Player($"{tag}B", SessionFixture.HomeMap, x: 6, y: 10);
        var (c, _) = _fx.Player($"{tag}C", SessionFixture.HomeMap, x: 7, y: 10);
        var (d, _) = _fx.Player($"{tag}D", SessionFixture.HomeMap, x: 8, y: 10);
        return (a, b, c, d);
    }

    /// <summary><paramref name="n"/> sessions on the fixture's home map, in entry order. The allocation
    /// fact needs a CROWD, not four: at four peers the closure shape's 96 B apiece is small enough to sit
    /// under any bound loose enough to cover the snapshot's own fixed cost, so the falsification would come
    /// back green and pin nothing.</summary>
    private Session[] ManyPlayers(int n, string tag)
    {
        var all = new Session[n];
        for (int i = 0; i < n; i++)
            (all[i], _) = _fx.Player($"{tag}{i:000}", SessionFixture.HomeMap, x: (ushort)(i % 11), y: (ushort)(i / 11));
        return all;
    }

    private void Leave(params Session[] sessions)
    {
        foreach (var s in sessions) _fx.World.LeaveMap(s, SessionFixture.HomeMap);
    }

    /// <summary>The ids of THIS test's four sessions, in the order the broadcast delivered them. The world
    /// is shared across the collection, so a peer list can carry a session another test left standing; the
    /// claim is about the relative order of the four this test put there.</summary>
    private static uint[] Ours(IEnumerable<uint> seen, params Session[] mine)
    {
        var ids = mine.Select(s => s.PlayerId).ToHashSet();
        return seen.Where(ids.Contains).ToArray();
    }
}
