namespace Server;

/// <summary>
/// Process-health probe: measures THREAD-POOL SCHEDULING LATENCY and reports it when it goes bad.
///
/// <para>Why this exists. The world heartbeat runs on its own thread now (see <c>World.TickLoop</c>) and the
/// slow-tick watchdog can prove it is healthy. But most of the server does NOT run there: every session's
/// read loop and its outbound <c>WriterLoop</c> are async continuations, i.e. thread-pool work items. If the
/// pool is starved, those stall while the tick keeps running perfectly — the world advances, the log looks
/// pristine, and players are frozen because nothing is being read from or written to their sockets. That is
/// a failure mode with NO other symptom in the log, which is precisely why it needs its own probe.</para>
///
/// <para>The measurement: schedule an empty work item and time how long until it actually starts. On a
/// healthy pool that is sub-millisecond. When the pool has no free thread it climbs to hundreds of ms or
/// seconds, because the runtime only injects replacement threads at roughly one or two per second — the
/// characteristic multi-second, self-recovering freeze.</para>
///
/// <para>Reading the output alongside the other two watchdogs:</para>
/// <list type="bullet">
///   <item>pool latency high + SLOW SEND with <c>queued</c> high → starvation is the cause; find what is
///     blocking pool threads (a synchronous DB write, a <c>Thread.Sleep</c>, a <c>.Result</c>).</item>
///   <item>pool latency fine + SLOW SEND with <c>write</c> high → the socket is backed up; the stall is on
///     the network between us and that client, not in this process.</item>
///   <item>all three watchdogs silent during a freeze → the server did its job on time; look at the client
///     or the path beyond our socket.</item>
/// </list>
/// </summary>
public static class Watchdog
{
    /// <summary>Report pool scheduling latency at or above this many ms. <c>P1998_POOL_LAG_MS</c> tunes it;
    /// 0 disables the probe entirely.</summary>
    private static readonly int PoolLagMs =
        int.TryParse(Environment.GetEnvironmentVariable("P1998_POOL_LAG_MS"), out var pl) && pl >= 0 ? pl : 100;

    private const int ProbeIntervalMs = 1000;

    /// <summary>Report a client that has sent us NOTHING for this long while we are still actively sending
    /// to it. That asymmetry — world going out, nothing coming back — is the exact shape of "the mobs keep
    /// moving but my character can't move or act". <c>P1998_SILENT_MS</c> tunes it; 0 disables.</summary>
    private static readonly int SilentMs =
        int.TryParse(Environment.GetEnvironmentVariable("P1998_SILENT_MS"), out var sm) && sm >= 0 ? sm : 4000;

    private static World? _world;

    public static void Start(World world)
    {
        _world = world;
        if (PoolLagMs <= 0 && SilentMs <= 0) { Log.Info("watchdog: disabled"); return; }
        // Its own thread, obviously: a probe for pool starvation cannot itself live on the pool.
        new Thread(Loop) { IsBackground = true, Name = "watchdog" }.Start();
        Log.Info($"watchdog: pool-latency probe {(PoolLagMs > 0 ? $"warns at {PoolLagMs}ms" : "off")}, " +
                 $"input-silence probe {(SilentMs > 0 ? $"warns at {SilentMs}ms" : "off")}");
    }

    private static void Loop()
    {
        long worstMs = 0;   // high-water mark between reports, so a one-off spike isn't lost to the interval
        while (true)
        {
            Thread.Sleep(ProbeIntervalMs);
            try { ScanSessions(); }
            catch (Exception e) { Log.Info($"!! watchdog session scan error: {e.Message}"); }
            if (PoolLagMs <= 0) continue;
            try
            {
                var scheduled = Environment.TickCount64;
                long observed = -1;
                using var started = new ManualResetEventSlim(false);
                ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    observed = Environment.TickCount64 - scheduled;
                    started.Set();
                }, null);

                // Bounded wait: if the pool is so wedged the probe never runs, report THAT rather than hang
                // the watchdog thread forever.
                if (!started.Wait(10_000))
                {
                    Log.Info($"!! POOL STALLED: probe did not start within 10000ms — " +
                             $"{ThreadPool.ThreadCount} pool thread(s), {ThreadPool.PendingWorkItemCount} pending");
                    continue;
                }

                if (observed > worstMs) worstMs = observed;
                if (worstMs < PoolLagMs) continue;

                ThreadPool.GetMinThreads(out int minW, out _);
                Log.Info($"!! POOL LAG: work item waited {worstMs}ms to start — " +
                         $"{ThreadPool.ThreadCount} pool thread(s) (min {minW}), " +
                         $"{ThreadPool.PendingWorkItemCount} pending, {ThreadPool.CompletedWorkItemCount} completed");
                worstMs = 0;
            }
            catch (Exception e) { Log.Info($"!! watchdog probe error: {e.Message}"); }
        }
    }

    // One entry per session currently being reported silent, so the log gets ONE line when the freeze
    // starts and ONE when it ends — not a line every second for the whole duration.
    private static readonly HashSet<Session> Reported = new();

    /// <summary>Find clients that have gone quiet while we are still sending them the world.</summary>
    private static void ScanSessions()
    {
        if (SilentMs <= 0 || _world is null) return;
        long now = Environment.TickCount64;
        var live = _world.AllPlayers();

        foreach (var s in live)
        {
            long inAge  = now - s.LastInboundMs;
            long outAge = now - s.LastOutboundMs;
            // Silent INBOUND while OUTBOUND is still flowing. The outbound test is what separates a wedged
            // client from an ordinary idle one: if we had nothing to send either, the player is simply
            // standing somewhere quiet and there is nothing to report.
            bool stuck = inAge >= SilentMs && outAge <= 2000;

            if (stuck && Reported.Add(s))
                Log.Info($"!! CLIENT SILENT {s.Remote}: no inbound for {inAge}ms while still sending — {s.DiagState()}");
            else if (!stuck && Reported.Remove(s))
                Log.Info($"   -> client {s.Remote} talking again after {inAge}ms of silence — {s.DiagState()}");
        }

        Reported.RemoveWhere(s => !live.Contains(s));   // disconnected mid-freeze
    }

    /// <summary>Raise the pool's floor so a burst of blocking work can't cause a multi-second stall while the
    /// runtime injects threads one or two per second. The pool grows past this on its own; all this changes
    /// is that the first <paramref name="floor"/> threads are created ON DEMAND with no injection delay.
    ///
    /// <para>This is a mitigation, not a fix — blocking calls on pool threads (a synchronous SQLite write, a
    /// <c>Thread.Sleep</c>) are still wrong. It buys headroom so a burst degrades gracefully instead of
    /// freezing the world, and the probe above still reports it if it happens.</para></summary>
    public static void RaiseMinThreads(int floor = 32)
    {
        ThreadPool.GetMinThreads(out int minW, out int minIo);
        int want = Math.Max(floor, Environment.ProcessorCount * 2);
        if (minW >= want) return;
        if (ThreadPool.SetMinThreads(want, Math.Max(minIo, want)))
            Log.Info($"thread pool: min worker threads {minW} -> {want} (cores {Environment.ProcessorCount})");
        else
            Log.Info($"!! thread pool: SetMinThreads({want}) refused — leaving min at {minW}");
    }
}
