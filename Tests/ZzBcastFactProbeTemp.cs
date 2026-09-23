// TEMPORARY LOOP COMMIT for server-broadcast-alloc-flake-1: reverted before the final head (loop round 3).
// Localises an allocating Broadcast call on the CI runner. For every call it splits the call's bytes into
// entry -> first peer (lock + rent), first -> last peer (the sends), and last peer -> exit (clear + return),
// and records the GCs, the JIT compilations on this thread and the first-chance exceptions on this thread
// that happened inside the call. The real fact calls Begin/End around each Broadcast; twenty clones repeat
// the measurement right after a throwing broadcast, the way the fact follows the two isolation facts.
using System.Diagnostics;
using System.Runtime;
using Server;
using Tests.Support;
using Xunit;

namespace Tests;

[Collection("world")]
public sealed class ZzBcastFactProbeTemp
{
    private readonly SessionFixture _fx;
    public ZzBcastFactProbeTemp(SessionFixture fx) => _fx = fx;

    internal static int Peers;
    private static int _peerInCall;
    private static long _firstPeer, _lastPeer, _before;
    private static int _c0, _c1, _c2, _fceBefore;
    private static long _jitBefore;
    private static int _tid, _fce;
    private static string _lastFce = "";

    private const int Max = 64;
    private static readonly string[] Rows = new string[Max];
    private static readonly long[] RIdx = new long[Max], RSize = new long[Max], RS1 = new long[Max], RS2 = new long[Max], RS3 = new long[Max], RJit = new long[Max];
    private static readonly int[] RG0 = new int[Max], RG1 = new int[Max], RG2 = new int[Max], RFce = new int[Max], RPeers = new int[Max];
    private static int _rows, _allocating; private static long _bytes;

    internal static readonly Action<Session> ProbePeer = static _ =>
    {
        long b = GC.GetAllocatedBytesForCurrentThread();
        if (_peerInCall++ == 0) _firstPeer = b;
        _lastPeer = b;
        Peers++;
    };

    private static void OnFce(object? s, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        if (Environment.CurrentManagedThreadId != _tid) return;
        _fce++;
        _lastFce = e.Exception.GetType().Name + ": " + e.Exception.Message;
    }

    internal static void Start()
    {
        _tid = Environment.CurrentManagedThreadId; _fce = 0; _lastFce = "";
        _rows = 0; _allocating = 0; _bytes = 0; Peers = 0;
        AppDomain.CurrentDomain.FirstChanceException += OnFce;
    }

    internal static void StartPass() { _rows = 0; _allocating = 0; _bytes = 0; Peers = 0; }

    internal static void Begin()
    {
        _peerInCall = 0;
        _c0 = GC.CollectionCount(0); _c1 = GC.CollectionCount(1); _c2 = GC.CollectionCount(2);
        _fceBefore = _fce;
        _jitBefore = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true);
        _before = GC.GetAllocatedBytesForCurrentThread();
    }

    internal static void End(int i)
    {
        long after = GC.GetAllocatedBytesForCurrentThread();
        long d = after - _before;
        if (d == 0) return;
        _allocating++; _bytes += d;
        if (_rows >= Max) return;
        int r = _rows++;
        RIdx[r] = i; RSize[r] = d;
        RS1[r] = _peerInCall > 0 ? _firstPeer - _before : -1;
        RS2[r] = _peerInCall > 0 ? _lastPeer - _firstPeer : -1;
        RS3[r] = _peerInCall > 0 ? after - _lastPeer : -1;
        RJit[r] = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true) - _jitBefore;
        RG0[r] = GC.CollectionCount(0) - _c0; RG1[r] = GC.CollectionCount(1) - _c1; RG2[r] = GC.CollectionCount(2) - _c2;
        RFce[r] = _fce - _fceBefore; RPeers[r] = _peerInCall;
    }

    internal static (int allocating, long bytes) Stop(string label, int calls)
    {
        AppDomain.CurrentDomain.FirstChanceException -= OnFce;
        var info = GC.GetGCMemoryInfo();
        var parts = new List<string>();
        for (int r = 0; r < _rows; r++)
            parts.Add($"#{RIdx[r]}:{RSize[r]}B[rent {RS1[r]}|sends {RS2[r]}|return {RS3[r]}] jit{RJit[r]} gc{RG0[r]}/{RG1[r]}/{RG2[r]} fce{RFce[r]} peers{RPeers[r]}");
        Shared.Log.Warn($"BCASTFACT,{label},allocating={_allocating},bytes={_bytes},old={(_bytes >= calls ? "RED" : "green")},"
                      + $"new={(_allocating >= calls / 100 ? "RED" : "green")},peersPerCall={Peers / calls},"
                      + $"ratio={(double)info.MemoryLoadBytes / info.HighMemoryLoadThresholdBytes:0.000},tid={_tid},"
                      + $"jitThread={System.Runtime.JitInfo.GetCompiledMethodCount(true)},fce={_fce},lastFce={_lastFce.Replace(',', ';')},"
                      + $"tiered={AppContext.GetData("System.Runtime.TieredCompilation") ?? "default"},spikes={string.Join(" ; ", parts)}");
        return (_allocating, _bytes);
    }

    public static IEnumerable<object[]> Clones()
    {
        for (int i = 0; i < 20; i++) yield return new object[] { i };
    }

    [Theory]
    [MemberData(nameof(Clones))]
    public void Clone(int n)
    {
        // What the fact follows in the suite: a broadcast whose send throws, logged under a line sink.
        var (a, _) = _fx.Player($"ProbeThrowA{n}", SessionFixture.HomeMap, x: 5, y: 10);
        var (b, _) = _fx.Player($"ProbeThrowB{n}", SessionFixture.HomeMap, x: 6, y: 10);
        using (LogLineSink.Acquire())
            _fx.World.Broadcast(SessionFixture.HomeMap, p => { if (p == b) throw new InvalidOperationException("probe throw"); });
        _fx.World.LeaveMap(a, SessionFixture.HomeMap);
        _fx.World.LeaveMap(b, SessionFixture.HomeMap);

        const int Calls = 2_000, Crowd = 40;
        var crowd = new Session[Crowd];
        for (int i = 0; i < Crowd; i++)
            (crowd[i], _) = _fx.Player($"Probe{n}x{i:000}", SessionFixture.HomeMap, x: (ushort)(i % 11), y: (ushort)(i / 11));
        try
        {
            Start();
            for (int pass = 0; pass < 2; pass++)
            {
                StartPass();
                for (int i = 0; i < Calls; i++) { Begin(); _fx.World.Broadcast(SessionFixture.HomeMap, ProbePeer); End(i); }
            }
            Stop($"clone{n}", Calls);
        }
        finally { foreach (var s in crowd) _fx.World.LeaveMap(s, SessionFixture.HomeMap); }
    }
}
