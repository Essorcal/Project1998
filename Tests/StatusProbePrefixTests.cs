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
        await connection.Run.WaitAsync(TimeSpan.FromSeconds(5));

        AssertStatusResponse(response);
        Assert.Equal(1, Count(response, "HTTP/1.1 200 OK"));
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
            var client = new TcpClient();
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
        public void Dispose() => client.Dispose();
    }
}
