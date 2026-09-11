using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Protocol.Tk495;
using Server;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>The HTTP status sniff runs before game framing. These loopback facts drive the real Session read
/// loop so a split probe cannot pass by testing a copy of the hook while the production hook still drops it.</summary>
[Collection("world")]
public sealed class StatusProbePrefixTests(SessionFixture fixture)
{
    private static readonly byte[] Request = Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\n\r\n");

    /// <summary>A proper prefix waits for the rest of GET, then receives one status response and EOF.
    /// Falsified by restoring the old one-read LooksLikeHttp gate in Session's buffered hook.</summary>
    [Fact]
    public async Task GetSplitBeforeSpaceIsAnsweredOnceThenClosed()
    {
        using var connection = await OpenSessionAsync();

        await connection.Stream.WriteAsync(Request.AsMemory(0, 3));
        await Task.Delay(100);
        Assert.False(connection.Run.IsCompleted);

        await connection.Stream.WriteAsync(Request.AsMemory(3));
        string response = await ReadToEofAsync(connection.Stream);
        connection.ShutdownSend();   // the hook drains to EOF now, so the peer's own close is what ends the session
        await connection.Run.WaitAsync(TimeSpan.FromSeconds(5));

        AssertStatusResponse(response);
        Assert.Equal(1, Count(response, "HTTP/1.1 200 OK"));
    }

    /// <summary>The F2 case from the PR #228 review: the request tail arrives AFTER the fourth byte, so it is
    /// still unread when the session closes. The peer must receive the whole response, not an RST. Falsified by
    /// removing the shutdown-and-drain from the hook: the read then fails with "An established connection was
    /// aborted by the software in your host machine".</summary>
    [Fact]
    public async Task RequestTailArrivingAfterTheFourthByteStillReceivesTheResponse()
    {
        using var connection = await OpenSessionAsync();

        await connection.Stream.WriteAsync(Request.AsMemory(0, 2));    // "GE"
        await Task.Delay(100);
        await connection.Stream.WriteAsync(Request.AsMemory(2, 2));    // "T " — the buffer is now exactly "GET "
        await Task.Delay(100);
        await connection.Stream.WriteAsync(Request.AsMemory(4));       // the tail, unread at close before this fix

        string response = await ReadToEofAsync(connection.Stream);
        connection.ShutdownSend();
        await connection.Run.WaitAsync(TimeSpan.FromSeconds(5));

        AssertStatusResponse(response);
        Assert.Equal(1, Count(response, "HTTP/1.1 200 OK"));
    }

    /// <summary>A scanner beginning with G is dropped promptly when its next byte breaks the HTTP prefix.
    /// Falsified by making the hook wait for four bytes without checking the prefix after each read.</summary>
    [Fact]
    public async Task BrokenGetPrefixIsDroppedAsSoonAsItBreaks()
    {
        using var connection = await OpenSessionAsync();

        await connection.Stream.WriteAsync(new byte[] { (byte)'G' });
        await Task.Delay(100);
        Assert.False(connection.Run.IsCompleted);

        await connection.Stream.WriteAsync(new byte[] { 0xAA });
        await connection.Run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(string.Empty, await ReadToEofAsync(connection.Stream));
    }

    /// <summary>The existing one-write probe shape is unchanged. Falsified by disabling the complete GET
    /// branch while leaving the new proper-prefix branch intact.</summary>
    [Fact]
    public async Task CompleteGetInOneWriteIsStillAnsweredOnceThenClosed()
    {
        using var connection = await OpenSessionAsync();

        await connection.Stream.WriteAsync(Request);
        string response = await ReadToEofAsync(connection.Stream);
        connection.ShutdownSend();
        await connection.Run.WaitAsync(TimeSpan.FromSeconds(5));

        AssertStatusResponse(response);
        Assert.Equal(1, Count(response, "HTTP/1.1 200 OK"));
    }

    /// <summary>A peer that is answered and then never closes its own side is ended by the existing handshake
    /// watchdog, not held forever on the drain read — the drain adds no timer of its own. Falsified by removing
    /// the watchdog close (the session never ends) or the drain (it ends immediately, well under the budget).
    /// </summary>
    [Fact]
    public async Task AnsweredPeerThatNeverClosesIsEndedByTheHandshakeWatchdog()
    {
        using var connection = await OpenSessionAsync();
        var elapsed = Stopwatch.StartNew();

        await connection.Stream.WriteAsync(Encoding.ASCII.GetBytes("GET "));
        string response = await ReadToEofAsync(connection.Stream);   // response, then our FIN — the peer stays open
        AssertStatusResponse(response);
        Assert.False(connection.Run.IsCompleted);

        await connection.Run.WaitAsync(TimeSpan.FromMilliseconds(FrameReader.DefaultHandshakeMs + 5_000));
        elapsed.Stop();

        Assert.True(elapsed.ElapsedMilliseconds >= FrameReader.DefaultHandshakeMs - 500,
            $"connection closed after {elapsed.ElapsedMilliseconds}ms, before the handshake watchdog");
    }

    /// <summary>An incomplete HTTP prefix is held only for the existing handshake budget. Falsified by
    /// restoring the old one-read gate (immediate drop) and by removing the watchdog close (never ends).</summary>
    [Fact]
    public async Task IncompleteGetPrefixEndsAtHandshakeWatchdog()
    {
        using var connection = await OpenSessionAsync();
        var elapsed = Stopwatch.StartNew();

        await connection.Stream.WriteAsync(Request.AsMemory(0, 3));
        await Task.Delay(100);
        Assert.False(connection.Run.IsCompleted);

        await connection.Run.WaitAsync(TimeSpan.FromMilliseconds(FrameReader.DefaultHandshakeMs + 5_000));
        elapsed.Stop();

        Assert.True(elapsed.ElapsedMilliseconds >= FrameReader.DefaultHandshakeMs - 500,
            $"connection closed after {elapsed.ElapsedMilliseconds}ms, before the handshake watchdog");
        Assert.Equal(string.Empty, await ReadToEofAsync(connection.Stream));
    }

    private async Task<ProbeConnection> OpenSessionAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            // NoDelay: these facts depend on each small write arriving as its own read, which is the split a
            // probe over a slow link produces and what Nagle would coalesce away.
            var client = new TcpClient { NoDelay = true };
            Task connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            TcpClient accepted = await listener.AcceptTcpClientAsync();
            await connect;

            var session = new Session(accepted, 2005, fixture.Store, fixture.World);
            return new ProbeConnection(client, session.RunAsync());
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<string> ReadToEofAsync(NetworkStream stream)
    {
        using var response = new MemoryStream();
        var chunk = new byte[1024];
        while (true)
        {
            int n = await stream.ReadAsync(chunk).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            if (n == 0) break;
            response.Write(chunk, 0, n);
        }
        return Encoding.ASCII.GetString(response.ToArray());
    }

    private static void AssertStatusResponse(string response)
    {
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", response);
        Assert.Contains("Content-Type: application/json\r\n", response);
        Assert.Contains("Connection: close\r\n\r\n", response);
        Assert.Contains("{\"up\":true,\"players\":", response);
    }

    private static int Count(string haystack, string needle) =>
        (haystack.Length - haystack.Replace(needle, string.Empty).Length) / needle.Length;

    private sealed class ProbeConnection(TcpClient client, Task run) : IDisposable
    {
        public NetworkStream Stream { get; } = client.GetStream();
        public Task Run { get; } = run;

        /// <summary>Close the peer's send side, the way a probe that has read its response does. The hook drains
        /// to EOF before the socket closes, so this is what lets an answered session finish promptly.</summary>
        public void ShutdownSend()
        {
            try { client.Client.Shutdown(SocketShutdown.Send); }
            catch (SocketException) { /* already gone */ }
        }

        public void Dispose() => client.Dispose();
    }
}
