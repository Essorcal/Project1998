using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The gate in <see cref="ConsoleTap"/>, pinned.
///
/// <para><b>What needs a test here.</b> <c>Console.Out</c> is a single process-global slot and every
/// capturing test does a read-modify-write on it, so two captures running at once end with the second one's
/// restore putting back the writer IT saw — silently discarding the first one's redirect. The first test then
/// reads an empty console and fails claiming a line was never written that is sitting in the job's own
/// stdout. That is not a hypothesis: fork CI run 35164315790 attempt 1 failed
/// <c>LoginOutboundTests.AFrameThatWaitsTooLongToReachTheSocketIsNamedOnTheLoginChannel</c> that way, and
/// <c>MobAiTickTests.OneMobThrowingDoesNotCostTheOthersTheirPackets</c> failed CI once for the same reason
/// (PR #257, attempt 1) — the <c>world</c> collection's capture crossing the <c>log</c> collection's.</para>
///
/// <para><b>Why a gate rather than a loud failure.</b> Both were on the table. Failing loudly when a second
/// capture starts would be the smaller CHANGE only if the type had no gate; it has one already
/// (<c>SemaphoreSlim Exclusive</c>, taken in <c>Acquire</c> and released in <c>Dispose</c>), so serialising
/// is what the existing design supports and refusing would be a new behaviour that turns an honest overlap —
/// two capturing tests in two collections, which is the normal arrangement here — into a failed run for no
/// gain. The gate is also the only option that keeps the redirect a fact for the whole window instead of a
/// race. What was missing was not the gate but this fact: nothing failed if a later edit dropped it, and the
/// CI red it would cause is a different test, in a different collection, blaming its own production code.</para>
///
/// <para>No <c>[Collection]</c>: the claim is exactly that this works ACROSS collections, and the tap's own
/// gate is what keeps this test from disturbing anyone else's capture.</para>
/// </summary>
public class ConsoleTapExclusionTests
{
    /// <summary>Two captures on two threads serialise: while the first holds the console the second is
    /// waiting, and it installs only once the first has restored.
    ///
    /// <para><b>The 500 ms.</b> It is not a timing claim — with the gate gone the second capture installs in
    /// microseconds, because nothing at all is in its way. The wait is a window in which the ungated shape
    /// cannot avoid being caught, not a threshold anything real sits near.</para>
    ///
    /// <para>Falsification: delete the <c>Exclusive.Wait</c> in <c>ConsoleTap.Acquire</c> and the
    /// <c>Exclusive.Release</c> in <c>Dispose</c>, and this goes red on the second capture installing while
    /// the first still held the console. Run it, confirm red, restore. The red is recorded in
    /// <c>reviews/test-hygiene-evidence/</c>.</para></summary>
    [Fact]
    public async Task A_second_capture_waits_for_the_first_to_release_the_console()
    {
        var first = ConsoleTap.Acquire();
        ConsoleTap? second = null;
        var secondHeld = new ManualResetEventSlim(false);
        var contender = Task.Run(() => { second = ConsoleTap.Acquire(); secondHeld.Set(); });

        bool installedWhileHeld;
        try
        {
            installedWhileHeld = secondHeld.Wait(TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            // Always, and before any assertion: a leaked gate would not fail this test, it would time out
            // every later capturing test 120 seconds at a time.
            first.Dispose();
        }

        var done = await Task.WhenAny(contender, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(ReferenceEquals(done, contender),
                    "the second capture never got the console after the first released it");
        await contender;   // it did; surface anything it threw
        second!.Dispose();

        Assert.False(installedWhileHeld,
                     "a second capture installed while the first still held the console — Console.Out is one "
                   + "process-global slot, and the second restore would put back the first tap and lose the "
                   + "real writer for the rest of the run");
    }
}
