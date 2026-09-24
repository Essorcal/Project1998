// TEMPORARY probe for server-anormalpeer-flake-1. Never committed.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Protocol.Tk495;
using Shared;
using Xunit;
using Xunit.Abstractions;

namespace Tests;

[Collection("log")]
public sealed class ZzAnormalProbe
{
    private readonly ITestOutputHelper _out;
    public ZzAnormalProbe(ITestOutputHelper o) => _out = o;

    private static int Env(string n, int d) => int.TryParse(Environment.GetEnvironmentVariable(n), out var v) ? v : d;

    [Fact]
    public async Task Probe()
    {
        int iters = Env("PROBE_ITERS", Environment.GetEnvironmentVariable("CI") == "true" ? 500 : 0);
        if (iters == 0) return;
        int starve = Env("PROBE_STARVE", 0);          // 1 = park the pool before each iteration
        int sleepMs = Env("PROBE_SLEEP_MS", 1500);
        string shape = Environment.GetEnvironmentVariable("PROBE_SHAPE") ?? "both";
        int oldFail = 0, newFail = 0, oldRuns = 0, newRuns = 0;
        var kinds = new Dictionary<string, int>();
        for (int i = 0; i < iters; i++)
        {
            foreach (var s in new[] { "old", "new" })
            {
                if (shape != "both" && shape != s) continue;
                if (starve == 1) Starve(sleepMs);
                try
                {
                    if (s == "old") await OldShape(); else await new LoginOutboundTests().ANormalPeerReceivesEveryFrameWholeAndInOrder();
                }
                catch (Exception e)
                {
                    if (s == "old") oldFail++; else newFail++;
                    string k = s + ": " + e.GetType().Name + " " + e.Message.Replace("\n", " | ");
                    if (k.Length > 220) k = k[..220];
                    kinds[k] = kinds.GetValueOrDefault(k) + 1;
                }
                if (s == "old") oldRuns++; else newRuns++;
            }
        }
        string summary = $"PROBE starve={starve} sleep={sleepMs} old {oldFail}/{oldRuns} failed, new {newFail}/{newRuns} failed";
        _out.WriteLine(summary);
        Console.Out.WriteLine("ANORMALPROBE " + summary);
        foreach (var kv in kinds) Console.Out.WriteLine($"ANORMALPROBE   {kv.Value}x {kv.Key}");
        foreach (var kv in kinds) _out.WriteLine($"  {kv.Value}x {kv.Key}");
        File.AppendAllText(Environment.GetEnvironmentVariable("PROBE_OUT") ?? Path.Combine(Path.GetTempPath(), "anormalprobe-out.txt"),
            summary + Environment.NewLine + string.Join(Environment.NewLine, kinds.Select(kv => $"  {kv.Value}x {kv.Key}")) + Environment.NewLine);
    }

    private static void Starve(int sleepMs)
    {
        ThreadPool.GetMinThreads(out int w, out _);
        for (int j = 0; j < w + 2; j++) ThreadPool.UnsafeQueueUserWorkItem(_ => Thread.Sleep(sleepMs), null);
    }

    // The fact exactly as it was at 4058fee.
    private static async Task OldShape()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await listener.AcceptTcpClientAsync();
        var outbound = new TcpOutbound(server, OutboundOptions.Login with { WriteTimeoutMs = 300 }, remote: "127.0.0.1:0 (probe)");
        try
        {
            Task writer = outbound.RunWriterAsync();
            var expected = new List<byte>();
            byte[] welcome = Welcome.Bytes;
            Assert.True(outbound.Send(welcome));
            expected.AddRange(welcome);
            for (int i = 0; i < 24; i++)
            {
                byte[] frame = Status(i % 2 == 0 ? (byte)0x0F : (byte)0x00, $"status line {i}");
                Assert.True(outbound.Send(frame));
                expected.AddRange(frame);
            }
            byte[] redirect = LoginRedirect.Build(new byte[] { 127, 0, 0, 1 }, 2005, "drainprobe", new byte[] { 0, 1, 18, 17, 0 });
            Assert.True(outbound.Send(redirect));
            expected.AddRange(redirect);

            await outbound.CloseAfterDrainAsync();
            await writer.WaitAsync(TimeSpan.FromSeconds(10));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var received = new MemoryStream();
            try { await client.GetStream().CopyToAsync(received, timeout.Token); } catch (IOException) { }
            Assert.Equal(expected.ToArray(), received.ToArray());
            Assert.Null(outbound.DropReason);
        }
        finally { outbound.Close(); }
    }

    private static byte[] Status(byte code, string text)
    {
        var t = Encoding.ASCII.GetBytes(text);
        var body = new List<byte> { code, (byte)t.Length };
        body.AddRange(t);
        body.Add(0);
        return TkPacket.Build(0x02, 0x02, TkCrypt.Crypt(body.ToArray(), 0x02, TkCrypt.LoginKey));
    }
}
