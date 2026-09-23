// TEMPORARY LOOP COMMIT for server-broadcast-alloc-flake-1: reverted before the final head. Twenty clones of
// BroadcastAllocatesNothingPerPeer's measurement, each printing one BCASTFACT line: the allocating calls, their
// sizes, the peers each call reached (which fixes the rented bucket), the GC counts that moved during the pass
// and during each allocating call, the pass's wall time and the memory pressure. It asserts nothing.
using System.Diagnostics;
using Server;
using Tests.Support;
using Xunit;

namespace Tests;

[Collection("world")]
public sealed class ZzBcastFactProbeTemp
{
    private readonly SessionFixture _fx;
    public ZzBcastFactProbeTemp(SessionFixture fx) => _fx = fx;

    private static int _peers;
    private static readonly Action<Session> CountPeer = static _ => _peers++;

    public static IEnumerable<object[]> Clones()
    {
        for (int i = 0; i < 20; i++) yield return new object[] { i };
    }

    [Theory]
    [MemberData(nameof(Clones))]
    public void Clone(int i) => Measure(_fx, $"clone{i}");

    internal static void Measure(SessionFixture fx, string label)
    {
        const int Calls = 2_000, Crowd = 40;
        var crowd = new Session[Crowd];
        for (int i = 0; i < Crowd; i++)
            (crowd[i], _) = fx.Player($"Probe{label}{i:000}", SessionFixture.HomeMap, x: (ushort)(i % 11), y: (ushort)(i / 11));
        try
        {
            var idx = new int[Calls]; var size = new long[Calls]; var g0 = new int[Calls]; var g1 = new int[Calls]; var g2 = new int[Calls];
            int allocating = 0; long bytes = 0; int p0 = 0, p1 = 0, p2 = 0; long ms = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                _peers = 0; allocating = 0; bytes = 0;
                int a0 = GC.CollectionCount(0), a1 = GC.CollectionCount(1), a2 = GC.CollectionCount(2);
                long t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < Calls; i++)
                {
                    int c0 = GC.CollectionCount(0), c1 = GC.CollectionCount(1), c2 = GC.CollectionCount(2);
                    long b = GC.GetAllocatedBytesForCurrentThread();
                    fx.World.Broadcast(SessionFixture.HomeMap, CountPeer);
                    long d = GC.GetAllocatedBytesForCurrentThread() - b;
                    if (d == 0) continue;
                    idx[allocating] = i; size[allocating] = d;
                    g0[allocating] = GC.CollectionCount(0) - c0; g1[allocating] = GC.CollectionCount(1) - c1; g2[allocating] = GC.CollectionCount(2) - c2;
                    allocating++; bytes += d;
                }
                ms = (Stopwatch.GetTimestamp() - t0) * 1000 / Stopwatch.Frequency;
                p0 = GC.CollectionCount(0) - a0; p1 = GC.CollectionCount(1) - a1; p2 = GC.CollectionCount(2) - a2;
            }
            var info = GC.GetGCMemoryInfo();
            var parts = new List<string>();
            for (int s = 0; s < Math.Min(allocating, 30); s++) parts.Add($"#{idx[s]}:{size[s]}B(gc{g0[s]}/{g1[s]}/{g2[s]})");
            Shared.Log.Warn($"BCASTFACT,{label},allocating={allocating},bytes={bytes},old={(bytes >= Calls ? "RED" : "green")},"
                          + $"new={(allocating >= Calls / 100 ? "RED" : "green")},peersPerCall={_peers / Calls},passMs={ms},"
                          + $"passGC={p0}/{p1}/{p2},ratio={(double)info.MemoryLoadBytes / info.HighMemoryLoadThresholdBytes:0.000},"
                          + $"tid={Environment.CurrentManagedThreadId},spikes={string.Join(' ', parts)}");
        }
        finally { foreach (var s in crowd) fx.World.LeaveMap(s, SessionFixture.HomeMap); }
    }
}
