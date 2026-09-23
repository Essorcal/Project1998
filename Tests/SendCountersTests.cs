using System.Net;
using System.Net.Sockets;
using Shared;
using Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Tests;

/// <summary>
/// The game channel's send counters (<see cref="SendCounters"/>): <c>framesSent</c>, <c>bytesSent</c> and
/// <c>slowSends</c> with its queued/write split, as published in <c>run/status.json</c>.
///
/// <para>The silent failure these guard is a counter that does not count, or counts the wrong thing. Nothing
/// throws, the status document parses, and a load run divides a wrong delta by a right span and publishes a
/// confident rate — a send path that "has no slow sends" reads like good news. The one that matters most is
/// the reason the counters exist: <c>SLOW SEND</c> is rate-limited to one line per session per second, so a
/// counter placed after that limit would count log lines, which is what the log already does.</para>
///
/// <para>Every fact counts into a PRIVATE <see cref="SendCounters"/> through a game-profile outbound built as
/// <c>OutboundOptions.Game with { Counters = ... }</c>, so the arithmetic is exact while other test classes
/// send game frames into <see cref="SendCounters.Game"/> in parallel. The writer is always awaited to its end
/// before a counter is read: the peer can hold the bytes before the writer's continuation has counted them,
/// and reading on the bytes alone would be a race.</para>
///
/// <para>In the <c>log</c> collection because the queued-stall fact takes the console with
/// <see cref="ConsoleTap"/> to show the log suppressing lines the counter still carries.</para>
/// </summary>
[Collection("log")]
public sealed class SendCountersTests
{
    private readonly ITestOutputHelper _out;
    public SendCountersTests(ITestOutputHelper output) => _out = output;

    /// <summary>A threshold no frame on any machine can reach.</summary>
    private const int NeverSlow = 60_000;

    /// <summary>The production wiring the status document depends on: the game profile counts into the
    /// published totals, and the login profile — the same <see cref="TcpOutbound"/> type, in the other
    /// process — counts into nothing, which is how a login frame is kept out of the game's figures.</summary>
    [Fact]
    public void TheGameProfileCountsIntoThePublishedTotalsAndTheLoginProfileCountsNowhere()
    {
        Assert.Same(SendCounters.Game, OutboundOptions.Game.Counters);
        Assert.Null(OutboundOptions.Login.Counters);
    }

    /// <summary>N frames of different sizes through a game-profile outbound to a peer that reads them all
    /// move <c>framesSent</c> by exactly N and <c>bytesSent</c> by exactly their total, and move no slow-send
    /// counter: a <c>slowSends</c> wired to every frame would pass the slow facts below and read 100%.</summary>
    [Fact]
    public async Task EveryFrameWrittenIsCountedWithItsBytes()
    {
        const int N = 50;
        var counters = new SendCounters();
        using var pair = await SocketPair.Connect(counters, NeverSlow);

        // Sizes 1..N, so a bytes counter that counts frames, or a fixed size, cannot come out right.
        var frames = Enumerable.Range(1, N).Select(n => Enumerable.Repeat((byte)n, n).ToArray()).ToArray();
        long total = frames.Sum(f => (long)f.Length);
        foreach (var f in frames) Assert.True(pair.Outbound.Send(f));

        Task writer = pair.Outbound.RunWriterAsync(_ => { });
        var got = await ReadExactly(pair.Client.GetStream(), (int)total);
        Assert.Equal(frames.SelectMany(f => f).ToArray(), got);
        await pair.Outbound.CloseAfterDrainAsync();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));

        _out.WriteLine($"framesSent {counters.FramesSent}, bytesSent {counters.BytesSent} (expected {N}, {total})");
        Assert.Equal(N, counters.FramesSent);
        Assert.Equal(total, counters.BytesSent);
        Assert.Equal(0, counters.SlowSends);
        Assert.Equal(0, counters.SlowSendsQueued);
        Assert.Equal(0, counters.SlowSendsWrite);
    }

    /// <summary>Twenty frames that each waited past the threshold in the queue are twenty slow sends and
    /// twenty queued-slow sends — while the log, rate-limited to one line per second, prints fewer lines
    /// than that for this peer.
    ///
    /// <para>The stall is the queued half, produced exactly the way <c>LoginOutboundTests</c> fact 5 does:
    /// the frames sit in the queue with no writer running, so their queued time IS the delay, with no socket
    /// buffer sizing and no race against the thread pool. The write half is not asserted here — a loaded
    /// machine could make a small write slow too, and that frame would then be in both halves, correctly.</para>
    ///
    /// <para>The log assertion is the evidence that this run DID suppress lines, so the equality above it is
    /// a count past the rate limit rather than a count that happened to agree with it.</para></summary>
    [Fact]
    public async Task EverySlowSendIsCountedIncludingTheOnesTheLogSuppresses()
    {
        const int N = 20, SlowSendMs = 50, HoldMs = 120;
        var counters = new SendCounters();
        using var captured = await ConsoleTap.AcquireAsync();
        using var pair = await SocketPair.Connect(counters, SlowSendMs);

        byte[] frame = { 1, 2, 3, 4, 5, 6, 7, 8 };
        for (int i = 0; i < N; i++) Assert.True(pair.Outbound.Send(frame));
        await Task.Delay(HoldMs);    // nothing is dequeuing: every frame's queued time is at least this

        Task writer = pair.Outbound.RunWriterAsync(_ => { });
        await ReadExactly(pair.Client.GetStream(), N * frame.Length);
        await pair.Outbound.CloseAfterDrainAsync();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));

        string needle = $"SLOW SEND {pair.Outbound.Remote}: queued ";
        string log = await captured.WaitForAsync(needle, TimeSpan.FromSeconds(3));
        int lines = CountOf(log, needle);
        _out.WriteLine($"slowSends {counters.SlowSends}, queued {counters.SlowSendsQueued}, write " +
                       $"{counters.SlowSendsWrite}; the log printed {lines} SLOW SEND line(s) for this peer");

        Assert.Equal(N, counters.FramesSent);
        Assert.Equal(N, counters.SlowSends);
        Assert.Equal(N, counters.SlowSendsQueued);
        Assert.True(lines >= 1, $"the watchdog printed no SLOW SEND line at all; the console carried:\n{log}");
        Assert.True(lines < N, $"the log printed {lines} lines for {N} slow frames, so nothing was suppressed " +
                               "and this run does not show the counter carrying suppressed sends");
    }

    /// <summary>A frame whose socket write itself stalls past the threshold is one slow send and one
    /// write-slow send.
    ///
    /// <para>The stall is a peer that does not read for 300ms against a 1MB frame, with the SENDER's socket
    /// buffer at 0 and the peer's receive buffer left at its default. The evidence for that shape is the
    /// slice's Windows probe (<c>scratchpad\send-counters\stall\results.txt</c> in the
    /// <c>server-slow-send-rate-1</c> report, 5 runs per configuration, peer receive buffer at its default)
    /// and three fork CI runs on ubuntu:
    /// <list type="bullet">
    /// <item>The sender's 0 is load-bearing on Windows. With a 1KB send buffer (the sizing
    /// <c>TcpOutboundTests</c> uses), the whole 8MB write completed before the peer read anything in 5 of 5
    /// probe runs, so the write never stalled. With <c>SO_SNDBUF</c> 0 the write was still pending when the
    /// peer began reading in 5 of 5 runs at each of 256KB, 1MB, 2MB and 8MB.</item>
    /// <item>The peer's default is load-bearing on Linux. A first version also shrank the peer's receive
    /// buffer to 1KB, and on run 35888854073 draining 8MB through that window overran this fact's 10s read
    /// deadline. At the default, run 35889677092 passed but took 4s for 8MB, hence 1MB: the Windows probe
    /// drained 1MB in 0-1ms once the peer read, and run 35890526009 passed this fact at 1MB on ubuntu, which
    /// it can only do if the write stalled there too.</item>
    /// </list>
    /// The race then only runs one way: the write can only take LONGER than the peer's delay, never less, so
    /// the write half is certain. The queued half is not asserted — the writer starts as soon as the frame is
    /// queued, but a starved pool could still make its pickup slow.</para></summary>
    [Fact]
    public async Task ASocketWriteThatStallsIsCountedAsWriteSlow()
    {
        const int SlowSendMs = 50, StallMs = 300, Size = 1024 * 1024;
        var counters = new SendCounters();
        using var pair = await SocketPair.Connect(counters, SlowSendMs);
        pair.Server.SendBufferSize = 0;

        Assert.True(pair.Outbound.Send(new byte[Size]));
        Task writer = pair.Outbound.RunWriterAsync(_ => { });
        await Task.Delay(StallMs);
        await ReadExactly(pair.Client.GetStream(), Size);
        await pair.Outbound.CloseAfterDrainAsync();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));

        _out.WriteLine($"slowSends {counters.SlowSends}, queued {counters.SlowSendsQueued}, " +
                       $"write {counters.SlowSendsWrite}");
        Assert.Equal(1, counters.FramesSent);
        Assert.Equal(Size, counters.BytesSent);
        Assert.Equal(1, counters.SlowSends);
        Assert.Equal(1, counters.SlowSendsWrite);
    }

    // =====================================================================================================

    private static int CountOf(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static async Task<byte[]> ReadExactly(NetworkStream stream, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, timeout.Token);
        return buffer;
    }

    private sealed class SocketPair : IDisposable
    {
        public TcpClient Client { get; }
        public TcpClient Server { get; }
        public TcpOutbound Outbound { get; }

        private SocketPair(TcpClient client, TcpClient server, SendCounters counters, int slowSendMs)
        {
            Client = client;
            Server = server;
            // The game profile, with this fact's own counters and threshold; a peer label no other test uses,
            // so a SLOW SEND line in the shared console is this fact's.
            var options = OutboundOptions.Game with { Counters = counters, SlowSendMs = slowSendMs };
            Outbound = new TcpOutbound(server, options, remote: $"send-counters-{Guid.NewGuid():N}");
        }

        public static async Task<SocketPair> Connect(SendCounters counters, int slowSendMs)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            return new SocketPair(client, await listener.AcceptTcpClientAsync(), counters, slowSendMs);
        }

        public void Dispose()
        {
            Outbound.Close();
            Client.Dispose();
            Server.Dispose();
        }
    }
}
