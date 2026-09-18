using System.Buffers.Binary;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The tick's per-player isolation boundary — <c>World.Try</c> — across the change that stopped it
/// allocating a delegate per player per beat.
///
/// <para><b>What needs a test here, by the "would this fail loudly?" rule.</b> Two things, and both are
/// silent.</para>
///
/// <para>The first is the boundary itself. <c>Try</c> exists so that a throw in one player's step skips only
/// that step: the sweep goes on, and the log names the site. Nothing observable happens on the healthy path,
/// so a refactor that moved the <c>catch</c>, widened it, narrowed it or dropped the message would look
/// exactly like a refactor that did not — until the day something throws on a live server with 400 players
/// on it and the beat is abandoned with no line saying which step did it. <c>ReconcileViews</c> is the site
/// this is pinned at because it is the one per-player step that can be made to throw through an existing
/// seam: <c>IOutbound.Send</c>. No production line is added for the test.</para>
///
/// <para>The second is the allocation, which is the whole point of the generic overload. A static lambda is
/// cached by the compiler; a lambda that captures ONE local silently stops being cached and starts costing a
/// display class and a delegate per call, and nothing about that is visible at the call site, in a review
/// diff, or in any behaviour. Measured on this repo at 400 players, the three per-beat sites were 320 B a
/// player a beat — 128 KB a beat, 385 KB a second. A later edit that adds a capture would put it straight
/// back with nothing to notice.</para>
/// </summary>
[Collection("world")]
public class TickStepIsolationTests
{
    private readonly SessionFixture _fx;

    public TickStepIsolationTests(SessionFixture fx) => _fx = fx;

    /// <summary>An <see cref="IOutbound"/> that throws on the first frame after it is armed — the seam a
    /// per-player tick step is made to throw through, without touching the step itself. Disarmed while the
    /// session logs in, because world entry sends, and the fact is about the TICK's isolation, not the
    /// login's.</summary>
    private sealed class ThrowingOutbound : IOutbound
    {
        internal const string Boom = "the outbound refused this frame";

        public string Remote => "thrower";
        public int Capacity => int.MaxValue;
        public int QueueDepth => 0;
        internal bool Armed;
        internal int Throws;

        public bool Send(byte[] frame)
        {
            if (!Armed) return true;
            Throws++;
            throw new InvalidOperationException(Boom);
        }

        public void Close() { }
    }

    /// <summary>A throw in ONE player's <c>ReconcileViews</c> step does not stop the sweep: the two other
    /// players on the map are still reconciled on the same beat and still get their frames, and the log
    /// carries the isolation line naming the site.
    ///
    /// <para>The arrangement is the state a real beat reconciles from. Three players enter one map, which
    /// draws everybody for everybody; then each one's view of the others is rubbed out with
    /// <c>DespawnEntity</c>, so the next sweep has a real show to send for every one of them. The first
    /// session's outbound is then armed to throw, which takes its <c>SyncPeers</c> down inside
    /// <c>Session.Send</c> — a genuine mid-step throw, not a fake one at the top.</para>
    ///
    /// <para>Falsification: drop the <c>catch</c> from the generic <c>World.Try&lt;T&gt;</c> and the throw
    /// escapes <c>FlushTick</c> — the other two players are never swept and this fact goes red on the
    /// <c>InvalidOperationException</c> itself. Run it, confirm red, restore.</para></summary>
    [Fact]
    public void AThrowInOnePlayersStepIsLoggedWithItsSiteAndTheOtherPlayersStillRun()
    {
        var thrower = new ThrowingOutbound();
        var a = NewPlayer("IsoThrower", thrower, x: 5, y: 10);
        var (b, bOut) = _fx.Player("IsoWatcherB", SessionFixture.HomeMap, x: 6, y: 10);
        var (c, cOut) = _fx.Player("IsoWatcherC", SessionFixture.HomeMap, x: 7, y: 10);
        try
        {
            // Rub each player's view of the others out, so the next sweep has something real to send for
            // every one of the three — otherwise the steady state is silent and nothing would throw.
            a.DespawnEntity(b.PlayerId); a.DespawnEntity(c.PlayerId);
            b.DespawnEntity(a.PlayerId); c.DespawnEntity(a.PlayerId);
            bOut.Clear(); cOut.Clear();
            thrower.Armed = true;

            using var sink = LogLineSink.Acquire();
            _fx.World.FlushTickForTest(new World.TickQueues());

            // The thrower's own step died where it was told to...
            Assert.True(thrower.Throws > 0, "the armed outbound was never reached — the arrangement is wrong, not the code");
            var line = sink.LineContaining("isolated step 'ReconcileViews' threw");
            Assert.Contains("isolated step 'ReconcileViews' threw — skipped, the sweep continues", line);
            Assert.Equal(LogLevel.Error, sink.EntryContaining("isolated step 'ReconcileViews' threw").Level);

            // ...and the sweep carried on to everyone after it: both other players were drawn the thrower.
            Assert.Contains(a.PlayerId, ShownPlayerIds(bOut));
            Assert.Contains(a.PlayerId, ShownPlayerIds(cOut));
        }
        finally
        {
            thrower.Armed = false;
            _fx.World.LeaveMap(a, SessionFixture.HomeMap);
            _fx.World.LeaveMap(b, SessionFixture.HomeMap);
            _fx.World.LeaveMap(c, SessionFixture.HomeMap);
        }
    }

    /// <summary>The generic isolation helper allocates NOTHING when its step is a static lambda — the claim
    /// the whole overload exists for, measured on the real helper rather than a copy of it.
    ///
    /// <para>Both shapes the tick uses: a bare argument (the <c>TickSleep</c>/<c>TickPoison</c>/<c>RegenTick</c>
    /// sites) and a tuple of several (the <c>ReconcileViews</c> and <c>SendTime</c> sites, which used to
    /// capture three arrays and two bytes respectively). 10,000 calls, so a single stray allocation of any
    /// size is 10,000 bytes rather than something a tolerance could hide.</para>
    ///
    /// <para>Falsification: make either lambda capture — drop the <c>static</c> and use a local from the
    /// enclosing method inside it — and the count goes to 10,000 × the display class plus its delegate,
    /// which is what the old call sites were paying. Run it, confirm red, restore.</para></summary>
    [Fact]
    public void TheIsolationHelperAllocatesNothingWithAStaticLambda()
    {
        const int Calls = 10_000;
        var subject = new object();
        var tuple = (subject, h: (byte)1, y: (byte)2);

        long oneArg = 0, tupleArg = 0, firstOne = 0, firstTuple = 0;

        // TWO passes over the SAME two call sites, not a warm-up written separately: each lambda expression
        // in the source has its own cache slot, so a "warm" call written as a second, identical-looking
        // lambda would warm a different site and leave its 64 bytes inside the measurement. The first pass
        // pays each site's one cached delegate; the second is the claim.
        for (int pass = 0; pass < 2; pass++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Calls; i++) World.TryForTest(subject, static o => GC.KeepAlive(o), "probe");
            oneArg = GC.GetAllocatedBytesForCurrentThread() - before;

            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Calls; i++) World.TryForTest(tuple, static t => GC.KeepAlive(t.subject), "probe");
            tupleArg = GC.GetAllocatedBytesForCurrentThread() - before;

            if (pass == 0) { firstOne = oneArg; firstTuple = tupleArg; }
        }

        // The cached delegate is ONE object for the life of the process, not one per call: even the pass that
        // allocates it pays it once across 10,000 calls.
        Assert.True(firstOne < 1024, $"first pass, one argument: {firstOne} B over {Calls} calls");
        Assert.True(firstTuple < 1024, $"first pass, tuple argument: {firstTuple} B over {Calls} calls");
        Assert.Equal(0, oneArg);
        Assert.Equal(0, tupleArg);
    }

    // ---- arrangement helpers -------------------------------------------------------------------------

    /// <summary>A session on the fixture's world with an outbound of the caller's choosing.
    /// <see cref="SessionFixture.Player"/> always builds a <see cref="RecordingOutbound"/>, and this fact
    /// needs one that throws.</summary>
    private Session NewPlayer(string name, IOutbound outbound, ushort x, ushort y)
    {
        var character = new Character
        {
            SchemaVersion = Character.CurrentSchemaVersion,
            Id = _fx.World.AllocatePlayerId(),
            Name = name,
            Map = SessionFixture.HomeMap,
            X = x,
            Y = y,
        };
        var session = new Session(outbound, 2005, _fx.Store, _fx.World, character);
        _fx.World.EnterMap(session, SessionFixture.HomeMap);
        return session;
    }

    /// <summary>The entity ids this recorder was told to draw as players, in order. The 0x33 body is
    /// <c>x(u16 BE) | y(u16 BE) | dir(u8) | id(u32 BE) | …</c> — see <c>Session.SendLook</c> — so the id
    /// starts at offset 5.</summary>
    private static List<uint> ShownPlayerIds(RecordingOutbound outbound) =>
        outbound.BodiesOf(0x33).Select(b => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(5))).ToList();
}
