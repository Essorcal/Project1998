using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// What <see cref="StallWatch.RunUntilDoneOrStalled"/> says when it gives up at its cap, pinned in both
/// directions, because the two messages send a reader to different places.
///
/// <para><b>Why the cap needs two messages.</b> The quiet window counts only time the watcher was awake to see
/// (<see cref="StallWatch.MaxCreditPerPoll"/>), so a watcher that is starved itself fills it slowly, and the
/// wall-clock cap can come first. Before PR #326 the cap could only be reached by a run that had completed a
/// round in its last quiet window, so its one message, "still moving", was true. With watched time it is not:
/// the PR #326 review (F4) starved only the watcher, 2 s before each read, beside a real ABBA deadlock, and the
/// cap failed it as "still moving" while the pair sat at 200 rounds. The run is red either way; the wording is
/// what tells a reader whether to look for a cycle or at the machine.</para>
///
/// <para>Each fact starves the watcher by making the progress read itself slow (500 ms), so a poll credits
/// only its 200 ms to the quiet window, and gives a 1.5 s cap with a 1 s window: the cap comes first. The
/// watched thread is parked on an event and released in a <c>finally</c>.</para>
/// </summary>
public class StallWatchTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(1.5);
    private const int SlowReadMs = 500;

    /// <summary>No round in the last quiet window of wall clock: the failure says so, in wall clock and in
    /// watched time, and does not claim the threads are moving.</summary>
    [Fact]
    public void ACapReachedAfterAQuietWindowWithNoRoundSaysSoAndNotStillMoving()
    {
        string message = FailureAtTheCap(progress: () => 0);

        Assert.Contains("no round for", message);
        Assert.Contains("s of wall clock", message);
        Assert.Contains("s watched", message);
        Assert.DoesNotContain("still moving", message);
    }

    /// <summary>A round in every poll, so never a quiet window: the cap's failure is the old one, a run still
    /// moving past anything the machine should need.</summary>
    [Fact]
    public void ACapReachedWhileRoundsStillCompleteSaysTheThreadsAreStillMoving()
    {
        long rounds = 0;
        string message = FailureAtTheCap(progress: () => ++rounds);

        Assert.Contains("still moving", message);
        Assert.DoesNotContain("no round for", message);
    }

    private static string FailureAtTheCap(Func<long> progress)
    {
        using var release = new ManualResetEventSlim(false);
        var parked = new Thread(() => release.Wait()) { IsBackground = true, Name = "stall-watch-parked" };
        parked.Start();
        try
        {
            var failure = Record.Exception(() => StallWatch.RunUntilDoneOrStalled(
                new[] { parked },
                () => { Thread.Sleep(SlowReadMs); return progress(); },
                Quiet, Cap, "the parked thread"));
            Assert.NotNull(failure);
            Assert.Contains("cap", failure!.Message);
            return failure.Message;
        }
        finally
        {
            release.Set();
            parked.Join();
        }
    }
}
