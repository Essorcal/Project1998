// TEMPORARY LOOP COMMIT for server-drain-bound-1: reverted before the final head. Repeats the drain fact's old
// and new shapes, interleaved, on the CI runner and prints each result as a DRAINPROBE log line.
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

namespace Tests;

public sealed class DrainProbeTemp
{
    private static readonly object FileGate = new();
    private static int _failOld, _failNew, _runOld, _runNew;

    public static IEnumerable<object[]> Iterations()
    {
        int n = int.TryParse(Environment.GetEnvironmentVariable("DRAIN_PROBE_N"), out var v) ? v : 600;
        for (int i = 0; i < n; i++) yield return new object[] { i };
    }

    [Theory]
    [MemberData(nameof(Iterations))]
    public async Task Probe(int iteration)
    {
        await Run(iteration, shape: (iteration % 2) switch { 0 => "old", _ => "new" });
    }

    private static async Task Run(int iteration, string shape)
    {
        bool newShape = shape == "new"; bool newVerdict = shape == "new";
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
        byte[] bulk = Enumerable.Range(0, BulkBytes).Select(i => (byte)i).ToArray();

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
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    eof = clock.ElapsedMilliseconds;
                    done.TrySetResult(ms.Length);
                }
                catch (Exception e) { done.TrySetException(e); }
            }) { IsBackground = true };
            t.Start();
            reader = done.Task;
        }
        else
        {
            reader = OldPeer();
            async Task<long> OldPeer()
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await Task.Delay(StallMs, timeout.Token);
                stallEnd = clock.ElapsedMilliseconds;
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, timeout.Token);
                eof = clock.ElapsedMilliseconds;
                return ms.Length;
            }
        }
        outbound.Send(bulk);
        outbound.Send(new byte[64]);

        gate.Set();
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
            verdict = returned < StallMs / 2 ? "FAIL-early" : w >= TcpOutbound.DrainTimeoutMs ? "FAIL-timedout"
                    : !writerFirst ? "FAIL-didnotwait" : "pass";
        string line = string.Join(',', DateTime.Now.ToString("HH:mm:ss.fff"), iteration, shape,
            returned, w, writerFirst, stallEnd - drainStart, eof - drainStart, got == BulkBytes + 64, gcPause, verdict);
        int fails, runs;
        lock (FileGate)
        {
            if (verdict != "pass") { if (newShape) _failNew++; else _failOld++; }
            if (newShape) _runNew++; else _runOld++;
            fails = newShape ? _failNew : _failOld;
            runs = newShape ? _runNew : _runOld;
        }
        Shared.Log.Warn($"DRAINPROBE,{line},{shape} fails {fails}/{runs}");
    }
}
