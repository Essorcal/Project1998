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
    /// <para><b>Why the bound is one byte per call and not zero.</b> The peers snapshot is rented from
    /// <c>ArrayPool&lt;Session&gt;.Shared</c>, which is process-wide and trims its per-core stacks on a Gen2
    /// callback. A big roster ELSEWHERE in the same run — <c>StatusTickAllocationTests</c>'s 400 sessions,
    /// the 400-player viewport survey fixture — empties the stack this thread rents from, and the next rent
    /// then allocates one fresh buffer. That is a cost of about one kilobyte per RUN, not per call, and it
    /// took this fact red twice on a head nobody disputes: 520 B beside the status roster (unrecorded) and
    /// 1,152 B on PR #261's upstream check (recorded, <c>reviews/server-261.review.json</c>). So the
    /// assertion states what the claim means — no PER-CALL allocation — at a bound of under one byte a call,
    /// <c>bytes &lt; Calls</c> = 2,000 B over 2,000 calls. One pool re-rent passes it; both falsifications
    /// below miss it by three orders of magnitude. Do not put <c>Assert.Equal(0, bytes)</c> back: it asserts
    /// the state of a process-wide pool as well as this helper, and only the second half is a fact about
    /// <c>World.Broadcast</c>.</para>
    ///
    /// <para>Falsification, twice, and both were re-run against this bound. (a) Put the capture back —
    /// <c>var p = peers[i]; Try(() =&gt; send(p), "Broadcast")</c> — and this goes red at <b>3,864 B a call
    /// over 40 peers</b> (7,728,000 B over the 2,000 calls, Debug and Release alike): 96 B a peer, the
    /// display class and the delegate. The 4,880 B the earlier report records is this plus (b), from before
    /// the snapshot was pooled. (b) Put the LINQ snapshot back —
    /// <c>peers = m.Players.Where(p =&gt; p != except).ToArray()</c> with the <c>foreach</c> over it — and it
    /// goes red at <b>1,032 B a call over the same 40 peers</b> (2,064,000 B over 2,000 calls, 1,032x the
    /// bound). Run each, confirm red, restore. The reds against the earlier zero bound are in
    /// <c>briefs/reports/broadcast-alloc-opus.md</c>; the reds against this one are in
    /// <c>reviews/test-hygiene-evidence/</c>.</para></summary>
    [Fact]
    public void BroadcastAllocatesNothingPerPeer()
    {
        const int Calls = 2_000;
        const int Crowd = 40;
        var crowd = ManyPlayers(Crowd, "Alloc");
        try
        {
            long bytes = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                _peers = 0;
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < Calls; i++) _fx.World.Broadcast(SessionFixture.HomeMap, CountPeer);
                bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            }

            Assert.True(_peers >= Crowd * Calls,
                        $"only {_peers} peer deliveries over {Calls} calls — the arrangement is wrong, not the code");
            // Not zero: a pool re-rent after someone else's Gen2 costs one buffer PER RUN (~1 KB). Under one
            // byte a call still refuses every per-peer and per-call shape. See the doc comment.
            Assert.True(bytes < Calls,
                        $"{bytes} B over {Calls} calls to {Crowd} peers — at {(double)bytes / Calls:0.##} B a "
                      + $"call this is a per-call allocation, not the pool's one re-rented buffer");
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
