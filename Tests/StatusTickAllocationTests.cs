using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The no-op path of <see cref="Session.TickSleep"/> and <see cref="Session.TickPoison"/> allocates nothing.
///
/// <para><b>Why this needs tests by the "would this fail loudly?" rule.</b> Both failure modes are silent.
/// Move either captured local back up to method scope and the compiler builds its display class at method
/// entry again — 32 B on every one of the 400 calls a beat, 25,600 B a beat at 400 players — and nothing
/// throws, nothing logs, and every behavioural test stays green; the only symptom is a GC that runs more
/// often under load.</para>
///
/// <para>Measured in <c>briefs/reports/status-tick-scope-opus.md</c>; evidence in
/// <c>reviews/status-tick-scope-evidence/</c>.</para>
/// </summary>
[Collection("world")]
public class StatusTickAllocationTests
{
    /// <summary>The roster the production tick sweeps. The claim is per CALL, so the count only sets the
    /// resolution: at the old 64 B per player per beat a broken head reads 25,600 B a beat here, not a
    /// number a tolerance could hide.</summary>
    private const int Players = 400;

    /// <summary>Beats swept per pass. 400 x 20 = 8,000 calls of each method.</summary>
    private const int Beats = 20;

    private readonly SessionFixture _fx;

    public StatusTickAllocationTests(SessionFixture fx) => _fx = fx;

    /// <summary>(a) 400 sessions with neither a hold nor a venom: <c>TickSleep</c> + <c>TickPoison</c> over
    /// 20 beats allocate <b>0 B</b> on the calling thread.
    ///
    /// <para>Two passes over the same loop, the pattern
    /// <c>TickStepIsolationTests.TheIsolationHelperAllocatesNothingWithAStaticLambda</c> established: the
    /// first pass pays each call site's one cached delegate and whatever the JIT wants, and the second is
    /// the claim. Tiering is not a factor for the ALLOCATION number — a tier-0 and a tier-1 body allocate
    /// the same objects — so no tiering switch is needed here, unlike the timed profile.</para>
    ///
    /// <para><b>Falsification.</b> Restore one method-scoped capture: put
    /// <c>int a = _sleepFxAnim; _world.BroadcastWideArea(…, p =&gt; p.EffectOver(_char.Id, a));</c> back
    /// inline at the foot of <c>TickSleep</c> in place of the <c>BroadcastStatusFx</c> call. Run, confirm
    /// red, restore by hand. Recorded in the report: red at <b>256,000 B over 8,000 calls — 32 B a
    /// call</b>.</para></summary>
    [Fact]
    public void TheNoOpStatusSweepAllocatesNothing()
    {
        var (sessions, _) = Roster(Players, "AllocIdle");
        try
        {
            foreach (var s in sessions) { Assert.False(s.Asleep); Assert.False(s.Poisoned); }

            long first = 0, second = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int beat = 0; beat < Beats; beat++)
                    foreach (var s in sessions) { s.TickSleep(); s.TickPoison(); }
                long taken = GC.GetAllocatedBytesForCurrentThread() - before;
                if (pass == 0) first = taken; else second = taken;
            }

            // The first pass may pay each site's cached delegate once for the life of the process; it is
            // still a handful of objects across 8,000 calls, not a per-call cost.
            Assert.True(first < 4096,
                $"first pass: {first} B over {Players * Beats} calls of each method");
            Assert.Equal(0, second);
        }
        finally
        {
            foreach (var s in sessions) _fx.World.LeaveMap(s, SessionFixture.HomeMap);
        }
    }

    // ---- arrangement and readers ---------------------------------------------------------------------

    /// <summary><paramref name="count"/> plain sessions on the fixture's world, with neither status.</summary>
    private (Session[] sessions, RecordingOutbound[] outbounds) Roster(int count, string prefix)
    {
        var sessions = new Session[count];
        var outbounds = new RecordingOutbound[count];
        for (int i = 0; i < count; i++)
        {
            var (s, o) = _fx.Player($"{prefix}{i:0000}", SessionFixture.HomeMap, x: 5, y: 10);
            sessions[i] = s;
            outbounds[i] = o;
        }
        return (sessions, outbounds);
    }
}
