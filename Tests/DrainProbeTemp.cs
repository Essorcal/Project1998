// TEMPORARY LOOP COMMIT for server-drain-bound-1: reverted before the final head. Repeats the drain fact's old
// and new shapes, interleaved, on the CI runner and prints each result as a DRAINPROBE log line. Round 2: the
// bulk is shared and the peer counts bytes instead of buffering them, so the probe's own LOH churn is not the load.
// Runs the fact's two shapes interleaved under the same load and records, for each run, when the teardown got
// back to the test, when the writer itself finished, and when the peer opened its window and saw EOF.
//   shape "old": peer is the async Task.Delay + CopyToAsync helper started from the test; the verdict is the
//                old one (teardown return < DrainTimeoutMs).
//   shape "new": peer on its own thread (sleep + blocking reads); the verdict is the new one (writer finished
//                < DrainTimeoutMs, and finished before the teardown returned).
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Shared;
using Xunit;

// Round 4: shape "final" is the committed fact at 7864db0 (async peer with ConfigureAwait(false), started at the
// teardown's clock; verdict: writer finished < DrainTimeoutMs - 50), against shape "old" (the fact at 155ff7b).
namespace Tests;

public sealed class DrainProbeTemp
{
    private static readonly object FileGate = new();
    private static readonly int[] Fails = new int[3], Runs = new int[3];
    private static readonly byte[] Bulk = Enumerable.Range(0, 512 * 1024).Select(i => (byte)i).ToArray();
    private static long Drain(Stream s) { var buf = new byte[64 * 1024]; long n = 0; int r; while ((r = s.Read(buf)) > 0) n += r; return n; }
    private static async Task<long> DrainAsync(Stream s, CancellationToken ct) { var buf = new byte[64 * 1024]; long n = 0; int r; while ((r = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0) n += r; return n; }   // as CopyToAsync does

    public static IEnumerable<object[]> Iterations()
    {
        int n = int.TryParse(Environment.GetEnvironmentVariable("DRAIN_PROBE_N"), out var v) ? v : 600;
        for (int i = 0; i < n; i++) yield return new object[] { i };
    }

    [Theory]
    [MemberData(nameof(Iterations))]
    public async Task Probe(int iteration)
    {
        await Run(iteration, shape: (iteration % 2) switch { 0 => "old", _ => "final" });
    }

    private static async Task Run(int iteration, string shape)
    {
        bool newShape = false; bool newVerdict = shape == "final"; bool poolShape = shape == "final";
        using var gate = new ManualResetEventSlim(!newShape);
        const int StallMs = 150;
        const int BulkBytes = 512 * 1024;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await listener.AcceptTcpClientAsync();
        var outbound = new TcpOutbound(server, OutboundOptions.Login with { WriteTimeoutMs = 5_000 }, remote: "probe");
        server.SendBufferSize = 64 * 1024;
        client.ReceiveBufferSize = 64 * 1024;
        byte[] bulk = Bulk;   // shared across iterations so the probe itself adds no LOH churn

        var clock = Stopwatch.StartNew();
        long stallEnd = -1, eof = -1;
        Task writer = outbound.RunWriterAsync();
        var stream = client.GetStream();
        Task<long> reader;
        if (newShape)
        {
            var done = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            var t = new Thread(() =>
            {
                try
                {
                    stream.ReadTimeout = 10_000;
                    gate.Wait();
                    Thread.Sleep(StallMs);
                    stallEnd = clock.ElapsedMilliseconds;
                    long n = Drain(stream);
                    eof = clock.ElapsedMilliseconds;
                    done.TrySetResult(n);
                }
                catch (Exception e) { done.TrySetException(e); }
            }) { IsBackground = true };
            t.Start();
            reader = done.Task;
        }
        else if (poolShape)
        {
            reader = null!;   // started at the gate, below
        }
        else
        {
            reader = OldPeer();
            async Task<long> OldPeer()
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await Task.Delay(StallMs, timeout.Token);
                stallEnd = clock.ElapsedMilliseconds;
                long n = await DrainAsync(stream, timeout.Token);
                eof = clock.ElapsedMilliseconds;
                return n;
            }
        }
        outbound.Send(bulk);
        outbound.Send(new byte[64]);

        gate.Set();
        if (poolShape)
            reader = FinalPeer();
        async Task<long> FinalPeer()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Task.Delay(StallMs, timeout.Token).ConfigureAwait(false);
            stallEnd = clock.ElapsedMilliseconds;
            long n = await DrainAsync(stream, timeout.Token).ConfigureAwait(false);
            eof = clock.ElapsedMilliseconds;
            return n;
        }
        long drainStart = clock.ElapsedMilliseconds;
        Task<long> writerAt = writer.ContinueWith(_ => clock.ElapsedMilliseconds, CancellationToken.None,
                                                  TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        long gcPause0 = (long)GC.GetTotalPauseDuration().TotalMilliseconds;
        await outbound.CloseAfterDrainAsync();
        bool writerFirst = writer.IsCompleted;
        long returned = clock.ElapsedMilliseconds - drainStart;
        long w = -1;
        try { w = await writerAt.WaitAsync(TimeSpan.FromSeconds(10)) - drainStart; } catch { }
        long got = -1;
        try { got = await reader.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        long gcPause = (long)GC.GetTotalPauseDuration().TotalMilliseconds - gcPause0;

        string verdict;
        if (!newVerdict)
            verdict = returned < StallMs / 2 ? "FAIL-early" : returned < TcpOutbound.DrainTimeoutMs ? "pass" : "FAIL-bound";
        else
            verdict = returned < StallMs / 2 ? "FAIL-early" : w >= TcpOutbound.DrainTimeoutMs - 50 ? "FAIL-timedout"
                    : "pass";
        string line = string.Join(',', DateTime.Now.ToString("HH:mm:ss.fff"), iteration, shape,
            returned, w, writerFirst, stallEnd - drainStart, eof - drainStart, got == BulkBytes + 64, gcPause, verdict);
        int fails, runs;
        lock (FileGate)
        {
            int k = shape == "old" ? 0 : 1;
            if (verdict != "pass") Fails[k]++;
            Runs[k]++;
            fails = Fails[k]; runs = Runs[k];
        }
        Shared.Log.Warn($"DRAINPROBE,{line},{shape} fails {fails}/{runs}");
    }
}
