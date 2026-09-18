using System.Diagnostics;
using Server;

namespace Tests.Support;

/// <summary>
/// A phase of the world's beat that costs a KNOWN number of milliseconds, installed through
/// <c>World.PhaseProbeForTest</c> — which runs inside <c>(4.3) status</c>, immediately before that bucket
/// closes.
///
/// <para>Why it exists: the watchdog's phase line folds any bucket under a millisecond into <c>other</c>,
/// so every fact about what the line NAMES used to rest on the premise "enough seeded creatures that a beat
/// costs whole milliseconds on any machine". That is a statement about the machine, and it failed on a
/// shared CI runner once (PR #245): the beat came in fast enough that every bucket truncated to zero and
/// the line carried only <c>other</c>. A tolerance cannot fix a threshold — the assertion is precisely that
/// the line names a phase — but a phase whose cost the test SETS can, and the fact is then green at any
/// runner speed, with no creatures seeded at all.</para>
///
/// <para>The same seam gives the phase-total facts a beat whose cost is arithmetic rather than a reading of
/// how busy the laptop was.</para>
/// </summary>
internal static class PhaseProbe
{
    /// <summary>Make one named phase of every beat cost at least <paramref name="ms"/> milliseconds until
    /// the returned handle is disposed. Disposal is not optional: the hook is static and the <c>world</c>
    /// collection's <c>World</c> is shared, so a probe left installed would tax every later class in it.</summary>
    public static IDisposable CostingAtLeast(double ms)
    {
        World.PhaseProbeForTest = () => Spin(ms);
        return new Handle();
    }

    /// <summary>Busy-wait for <paramref name="ms"/> milliseconds. A spin and not <c>Thread.Sleep</c>: the
    /// point is to spend the time INSIDE the phase, and a sleep hands the rest of its quantum to the
    /// scheduler, which on a loaded machine overshoots a sub-millisecond request by an order of
    /// magnitude.</summary>
    public static void Spin(double ms)
    {
        long until = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000.0);
        while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(50);
    }

    private sealed class Handle : IDisposable
    {
        public void Dispose() => World.PhaseProbeForTest = null;
    }
}
