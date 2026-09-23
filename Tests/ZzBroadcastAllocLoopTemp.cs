// TEMPORARY LOOP COMMIT for server-broadcast-alloc-flake-1: reverted before the final head. Repeats the
// per-call BroadcastAllocatesNothingPerPeer fact on the CI runner and prints one BCASTPROBE log line per
// iteration. Each iteration runs the REAL fact once (its verdict is "new"), then one paired pass measured per
// call, which gives the old summed verdict (bytes < 2000) and the new per-call verdict on the same 2,000 calls.
// Odd iterations run beside a forced-Gen2 storm (one GC.Collect(2) every 0.2 ms on another thread); even
// iterations run with no added load. The runner's memory pressure is printed so the High-pressure condition
// can be read off the log.
using System.Diagnostics;
using Server;
using Tests.Support;
using Xunit;

namespace Tests;

[Collection("world")]
public sealed class ZzBroadcastAllocLoopTemp
{
    private readonly SessionFixture _fx;
    public ZzBroadcastAllocLoopTemp(SessionFixture fx) => _fx = fx;

    private static int _runs, _factRed, _oldRed, _newRed, _anyReRent, _maxAllocating;
    private static int _peers;
    private static readonly Action<Session> CountPeer = static _ => _peers++;

    public static IEnumerable<object[]> Iterations()
    {
        int n = int.TryParse(Environment.GetEnvironmentVariable("BCAST_PROBE_N"), out var v) ? v : 400;
        for (int i = 0; i < n; i++) yield return new object[] { i };
    }

    [Theory]
    [MemberData(nameof(Iterations))]
    public void Probe(int iteration)
    {
        bool storm = iteration % 2 == 1;
        bool stop = false;
        Thread? noise = null;
        if (storm)
        {
            noise = new Thread(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                    var sw = Stopwatch.StartNew();
                    while (sw.Elapsed.TotalMilliseconds < 0.2) { }
                }
            }) { IsBackground = true };
            noise.Start();
        }

        string factVerdict = "green";
        int allocating = 0; long bytes = 0; var sizes = new List<long>();
        try
        {
            try { new BroadcastIsolationTests(_fx).BroadcastAllocatesNothingPerPeer(); }
            catch (Exception e) { factVerdict = "RED: " + e.Message.Replace(',', ';').Replace('\n', ' '); }

            const int Calls = 2_000, Crowd = 40;
            var crowd = new Session[Crowd];
            for (int i = 0; i < Crowd; i++)
                (crowd[i], _) = _fx.Player($"Loop{i:000}", SessionFixture.HomeMap, x: (ushort)(i % 11), y: (ushort)(i / 11));
            try
            {
                var spikes = new long[Calls];
                for (int pass = 0; pass < 2; pass++)
                {
                    allocating = 0; bytes = 0;
                    for (int i = 0; i < Calls; i++)
                    {
                        long b = GC.GetAllocatedBytesForCurrentThread();
                        _fx.World.Broadcast(SessionFixture.HomeMap, CountPeer);
                        long d = GC.GetAllocatedBytesForCurrentThread() - b;
                        if (d == 0) continue;
                        spikes[allocating++] = d;
                        bytes += d;
                    }
                }
                for (int s = 0; s < Math.Min(allocating, 8); s++) sizes.Add(spikes[s]);
            }
            finally { foreach (var s in crowd) _fx.World.LeaveMap(s, SessionFixture.HomeMap); }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            noise?.Join();
        }

        bool oldRed = bytes >= 2_000, newRed = allocating >= 20;
        var info = GC.GetGCMemoryInfo();
        double ratio = (double)info.MemoryLoadBytes / info.HighMemoryLoadThresholdBytes;
        int runs = Interlocked.Increment(ref _runs);
        if (factVerdict != "green") Interlocked.Increment(ref _factRed);
        if (oldRed) Interlocked.Increment(ref _oldRed);
        if (newRed) Interlocked.Increment(ref _newRed);
        if (allocating > 0) Interlocked.Increment(ref _anyReRent);
        if (allocating > _maxAllocating) _maxAllocating = allocating;
        Shared.Log.Warn($"BCASTPROBE,{iteration},{(storm ? "storm" : "plain")},ratio={ratio:0.000},allocating={allocating},"
                      + $"bytes={bytes},sizes={string.Join('/', sizes)},old={(oldRed ? "RED" : "green")},"
                      + $"new={(newRed ? "RED" : "green")},fact={factVerdict}"
                      + $" | totals runs={runs} factRed={_factRed} oldRed={_oldRed} newRed={_newRed} "
                      + $"withReRent={_anyReRent} maxAllocating={_maxAllocating}");
    }
}
