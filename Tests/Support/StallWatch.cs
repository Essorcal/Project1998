using System.Diagnostics;
using Xunit;

namespace Tests.Support;

internal static class StallWatch
{
    /// <summary>How long the round counter may sit still, while this watcher is awake to see it, before a
    /// deadlock fact calls it a deadlock. Any completed round on either thread restarts this window, so
    /// slowness only makes the wait longer, never a failure; only a thread that has stopped moving
    /// altogether spends the window. "Awake" is <see cref="MaxCreditPerPoll"/>'s point: time the whole
    /// process spent stopped does not count.</summary>
    internal static readonly TimeSpan StallQuiet = TimeSpan.FromSeconds(10);

    /// <summary>The safety net, not the detector. A run still creeping forward after this long is not the
    /// cycle under test — it is a machine in trouble — and the fact says which of the two it saw. A run that
    /// reaches it having completed no round for a whole quiet window of wall clock had a starved watcher as
    /// well, and is reported as exactly that: no round for so long, so much of it watched.</summary>
    internal static readonly TimeSpan StallCap = TimeSpan.FromSeconds(600);

    /// <summary>Poll interval for the progress watch. Short enough that the failure message's round count is
    /// current, long enough that the watcher is not itself load on the threads it is watching.</summary>
    private const int StallPollMs = 100;

    /// <summary>
    /// The most one poll may add to the quiet window, however long the gap since the previous poll was.
    ///
    /// <para><b>Why the quiet window is not plain wall clock.</b> The watcher polls every
    /// <see cref="StallPollMs"/>. A gap much longer than that between two polls means the watcher was not
    /// running either: the process was stopped for a garbage collection, paged out, or starved of CPU as a
    /// whole. The watched threads were stopped by the same thing, so the silence in that gap is not evidence
    /// that they are stuck on each other, and a deadlock fact must not convict them of it. That is the
    /// failure <c>SessionActorTests.LuaGateAgainstAPeerMonitorCannotDeadlock</c> produced on 2026-09-28
    /// (<c>reviews/PR312-by-fable.md</c> line 56): "no round completed for 10 s, stopped at 31365 rounds
    /// 18 s in", in a full Release run on a laptop, green on every rerun. Measured on 2026-10-05, that fact
    /// spent 761 ms of its 2.3 s in garbage-collection pauses in a full Debug run on the same machine (it was
    /// allocating a frame for every session the suite had left on its map; see the fact), which is the kind of
    /// pause a busier machine stretches.</para>
    ///
    /// <para><b>Why this is safe.</b> A real cycle does not need the gap: the watcher keeps running beside
    /// two parked threads, each poll adds its ~100 ms, and the window fills in ten seconds of watching, as
    /// before. On a machine slowed rather than stopped, a late poll still adds up to twice the interval, so
    /// the window fills more slowly, not never; <see cref="StallCap"/> still bounds the whole wait on the
    /// wall clock.</para>
    ///
    /// <para><b>Rejected:</b> a longer window. It would have to be longer than any pause a loaded machine
    /// can produce, which nobody can state, and it delays every real deadlock report by the same amount.
    /// Also rejected: crediting silence only while every watched thread reports <c>WaitSleepJoin</c>. A
    /// thread deadlocked on a spinning lock reports <c>Running</c>, so that version would never convict
    /// it.</para>
    /// </summary>
    internal static readonly TimeSpan MaxCreditPerPoll = TimeSpan.FromMilliseconds(2 * StallPollMs);

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
    /// <paramref name="progress"/> not moving for <paramref name="quiet"/> of watching is a cycle, and
    /// everything else is just a slow machine that is still allowed to finish. Watching time is wall time
    /// with each poll's contribution capped at <see cref="MaxCreditPerPoll"/>, so a stopped process does not
    /// spend the window.
    ///
    /// <para><paramref name="cap"/> is a backstop so a wedged run cannot hold the agent forever, on the wall
    /// clock. Reaching it while still making progress is reported as its own, differently worded failure,
    /// because it means something other than a deadlock (a machine at a standstill, a round that got orders of
    /// magnitude more expensive) and should not be read as the watched cycle having closed. Reaching it with no
    /// round for a whole <paramref name="quiet"/> of wall clock is a third message, giving both figures: the
    /// watcher was starved too, so the run cannot say which it was (<c>Tests/StallWatchTests.cs</c>).</para>
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
        var lastMoved = TimeSpan.Zero;   // wall clock at the last movement: for the message only
        var previousPoll = TimeSpan.Zero;
        var watchedQuiet = TimeSpan.Zero;

        while (true)
        {
            // Completion is checked before the counter is judged, so a run that has just finished returns
            // rather than being convicted of the silence that follows its last round.
            if (threads.All(t => t.Join(0)))
                return;

            var at = elapsed.Elapsed;
            var gap = at - previousPoll;
            previousPoll = at;

            long now = progress();
            if (now != last)
            {
                last = now;
                lastMoved = at;
                watchedQuiet = TimeSpan.Zero;
            }
            else
            {
                watchedQuiet += gap < MaxCreditPerPoll ? gap : MaxCreditPerPoll;
                if (watchedQuiet >= quiet)
                {
                    Assert.Fail($"{what}: no round completed for {quiet.TotalSeconds:0} s of watching " +
                                $"({(at - lastMoved).TotalSeconds:0} s on the wall clock), stopped at {last} rounds " +
                                $"{at.TotalSeconds:0} s in — the threads are stuck, not slow");
                }
            }

            if (at >= cap)
            {
                // Watched time fills slowly while the watcher itself is starved, so the cap can come first on a run
                // that has completed no round for a whole quiet window of wall clock. That run is not "still
                // moving", and the message must not send its reader away from a possible cycle (PR #326 review, F4).
                var silent = at - lastMoved;
                if (silent >= quiet)
                {
                    Assert.Fail($"{what}: reached the {cap.TotalSeconds:0.#} s cap at {last} rounds with no round for " +
                                $"{silent.TotalSeconds:0.#} s of wall clock, {watchedQuiet.TotalSeconds:0.#} s watched — " +
                                "the watcher was starved as well, so this run cannot tell stuck threads from a stopped " +
                                "machine");
                }
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
