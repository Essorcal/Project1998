using System.Diagnostics;
using Xunit;

namespace Tests.Support;

internal static class StallWatch
{
    /// <summary>How long the round counter may sit still before a deadlock fact calls it a deadlock. Any
    /// completed round on either thread restarts this window, so slowness only makes the wait longer, never
    /// a failure; only a thread that has stopped moving altogether spends the window.</summary>
    internal static readonly TimeSpan StallQuiet = TimeSpan.FromSeconds(10);

    /// <summary>The safety net, not the detector. A run still creeping forward after this long is not the
    /// cycle under test — it is a machine in trouble — and the fact says which of the two it saw.</summary>
    internal static readonly TimeSpan StallCap = TimeSpan.FromSeconds(600);

    /// <summary>Poll interval for the progress watch. Short enough that the failure message's round count is
    /// current, long enough that the watcher is not itself load on the threads it is watching.</summary>
    private const int StallPollMs = 100;

    /// <summary>Rounds completed across the watched threads: the workers bump it once per round and the
    /// waiting test thread reads it. Interlocked on both sides, because "has anything at all happened lately?"
    /// is the entire question and a torn or cached read answers it wrongly.</summary>
    internal sealed class RoundCounter
    {
        private long _rounds;

        public long Rounds => Interlocked.Read(ref _rounds);

        public void Bump() => Interlocked.Increment(ref _rounds);
    }

    /// <summary>
    /// Waits for threads to finish, and fails on a STALL rather than on a stopwatch:
    /// <paramref name="progress"/> not moving for <paramref name="quiet"/> is a cycle, and everything else is
    /// just a slow machine that is still allowed to finish.
    ///
    /// <para><paramref name="cap"/> is a backstop so a wedged run cannot hold the agent forever; reaching it
    /// while still making progress is reported as its own, differently worded failure, because it means
    /// something other than a deadlock (a machine at a standstill, a round that got orders of magnitude more
    /// expensive) and should not be read as the watched cycle having closed.</para>
    /// </summary>
    internal static void RunUntilDoneOrStalled(
        Thread[] threads, Func<long> progress, TimeSpan quiet, TimeSpan cap, string what)
    {
        // Most per-round workers finish in milliseconds. Join each one for at most a polling slice so the
        // common path returns as each thread exits without allocating a stopwatch or entering the poll loop.
        // A thread that misses the slice is not failed on time: it falls through to the progress watch.
        bool allDone = true;
        foreach (var thread in threads)
        {
            if (thread.Join(StallPollMs)) continue;
            allDone = false;
            break;
        }
        if (allDone)
            return;

        var elapsed = Stopwatch.StartNew();
        long last = progress();
        var lastMoved = TimeSpan.Zero;

        while (true)
        {
            // Completion is checked before the counter is judged, so a run that has just finished returns
            // rather than being convicted of the silence that follows its last round.
            if (threads.All(t => t.Join(0)))
                return;

            long now = progress();
            if (now != last)
            {
                last = now;
                lastMoved = elapsed.Elapsed;
            }
            else if (elapsed.Elapsed - lastMoved >= quiet)
            {
                Assert.Fail($"{what}: no round completed for {quiet.TotalSeconds:0} s, stopped at {last} rounds " +
                            $"{elapsed.Elapsed.TotalSeconds:0} s in — the threads are stuck, not slow");
            }

            if (elapsed.Elapsed >= cap)
            {
                Assert.Fail($"{what}: still running after the {cap.TotalSeconds:0} s cap at {last} rounds — still " +
                            "moving, so not this cycle, but far past anything this machine should need");
            }

            // Join wakes the instant this live thread exits. Unlike a sleep between polls, this adds no fixed
            // delay when short-lived threads finish, which matters to facts that wait once per race round.
            var live = threads.FirstOrDefault(t => t.IsAlive);
            if (live is null)
                return;
            live.Join(StallPollMs);
        }
    }
}
