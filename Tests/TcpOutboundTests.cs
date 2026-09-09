using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Protocol.Tk495;
using Server;
using Shared;
using Xunit;

namespace Tests;

[Collection("db")]
public sealed class TcpOutboundTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectionsReachTheWireBeforeEofEvenWhenWriterStartsAfterClose(bool writerStarted)
    {
        var world = new World();
        var store = new CharacterStore(RepoPaths.CharsDir());
        foreach (bool banned in new[] { false, true })
        {
            // Short names retain all nonce bytes; long names hash an empty prefix and cannot mint
            // a second token until the consumed row is purged (an unrelated handoff constraint).
            string user = $"d{Guid.NewGuid():N}"[..6];
            using (var cn = Db.Open())
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO characters(username,json,updated_utc) VALUES($u,'{broken',1)";
                cmd.Parameters.AddWithValue("$u", user);
                cmd.ExecuteNonQuery();
            }
            if (banned) Assert.True(Moderation.Ban(user, Moderation.Forever, "drain probe", "test"));
            for (int run = 0; run < 30; run++)
            {
                using var pair = await SocketPair.Connect();
                var session = new Session(pair.Outbound, 7005, store, world);
                Task? writer = writerStarted ? pair.Outbound.RunWriterAsync(_ => { }) : null;
                session.Receive(Arrival(user));
                Assert.False(pair.Outbound.Send(new byte[] { 1 }));
                writer ??= pair.Outbound.RunWriterAsync(_ => { });
                byte[] bytes = await ReadToEof(pair.Client.GetStream());
                Assert.True(TkPacket.TryParse(bytes, out var packet, out int consumed));
                Assert.Equal(bytes.Length, consumed);
                Assert.Equal(0x02, packet.Opcode);
                byte[] body = TkCrypt.Crypt(packet.Body, packet.Increment, TkCrypt.LoginKey);
                string expected = banned ? LoginAuth.BanMessageFor(user)
                    : "Your character record could not be loaded. Please contact an administrator.";
                Assert.Equal(expected, Encoding.ASCII.GetString(body, 2, body[1]));
                await writer.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, world.Online.Count);
            }
        }
    }

    [Fact]
    public async Task EstablishedSessionDisconnectDeliversItsMessageAndRejectsFurtherCommands()
    {
        var world = new World();
        for (int run = 0; run < 30; run++)
        {
            using var pair = await SocketPair.Connect();
            var session = new Session(pair.Outbound, 7005, new CharacterStore(RepoPaths.CharsDir()), world);
            Task writer = pair.Outbound.RunWriterAsync(_ => { });
            session.LuaMessage("You were disconnected by a GM."); // same SendMessage encoder as KickCmd
            session.Disconnect("kicked");
            // An arrival after disconnect must not even consume its handoff token. Merely asserting
            // no reply would miss dispatch: Session.Send already suppresses frames after closure.
            byte[] ignoredArrival = Arrival("zzclosed");
            session.Receive(ignoredArrival);
            Assert.True(HandoffTokens.Consume(ignoredArrival[^5..], "zzclosed", IPAddress.Loopback.ToString()));
            byte[] bytes = await ReadToEof(pair.Client.GetStream());
            Assert.True(TkPacket.TryParse(bytes, out var packet, out int consumed));
            Assert.Equal(bytes.Length, consumed);
            byte[] body = TkCrypt.Crypt(packet.Body, packet.Increment, TkCrypt.LoginKey);
            Assert.Equal("You were disconnected by a GM.", Encoding.ASCII.GetString(body, 2, body[1]));
            Assert.Equal(0, world.Online.Count);
            await writer.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ConcurrentDrainingClosesDeliverTheWholeFrameOnce()
    {
        using var pair = await SocketPair.Connect();
        byte[] expected = Enumerable.Range(0, 65536).Select(i => (byte)i).ToArray();
        Assert.True(pair.Outbound.Send(expected));
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(pair.Outbound.CloseAfterDrain)));
        Task writer = pair.Outbound.RunWriterAsync(_ => { });
        Assert.Equal(expected, await ReadToEof(pair.Client.GetStream()));
        await writer.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OrdinaryCloseDoesNotWaitForAWriterOrQueuedData()
    {
        using var pair = await SocketPair.Connect();
        Assert.True(pair.Outbound.Send(new byte[] { 1 }));
        var watch = Stopwatch.StartNew();
        pair.Outbound.Close();
        Assert.Empty(await ReadToEof(pair.Client.GetStream()));
        Assert.True(watch.ElapsedMilliseconds < TcpOutbound.DrainTimeoutMs / 2);
    }

    [Fact]
    public async Task DrainDeadlineIncludesAWriterThatNeverStarts()
    {
        using var pair = await SocketPair.Connect();
        Assert.True(pair.Outbound.Send(new byte[] { 1 }));
        pair.Outbound.CloseAfterDrain();
        Assert.Empty(await ReadToEof(pair.Client.GetStream()));
        // Starting late cannot resurrect the closed transport or hang its writer.
        await pair.Outbound.RunWriterAsync(_ => { }).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DrainDeadlineAbortsAnInFlightWriteWithoutBlockingTheCaller()
    {
        using var pair = await SocketPair.Connect();
        pair.Server.SendBufferSize = 1024;
        pair.Client.ReceiveBufferSize = 1024;
        Assert.True(pair.Outbound.Send(new byte[8 * 1024 * 1024]));
        Task writer = pair.Outbound.RunWriterAsync(_ => { });
        // RunWriterAsync executes synchronously up to the blocked WriteAsync: the channel is empty,
        // but the frame is still in flight. Waiting only on channel completion would lose this distinction.
        Assert.Equal(0, pair.Outbound.QueueDepth);
        Assert.False(writer.IsCompleted);
        var watch = Stopwatch.StartNew();
        pair.Outbound.CloseAfterDrain();
        Assert.True(watch.ElapsedMilliseconds < TcpOutbound.DrainTimeoutMs / 2);
        await writer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pair.Outbound.Send(new byte[] { 1 }));
    }

    private static byte[] Arrival(string user)
    {
        var body = new List<byte> { 9 };
        body.AddRange(Encoding.ASCII.GetBytes("NexonInc."));
        body.Add((byte)user.Length);
        body.AddRange(Encoding.ASCII.GetBytes(user));
        body.AddRange(HandoffTokens.Mint(user, IPAddress.Loopback.ToString()));
        return TkPacket.Build(Opcode.Arrival, 0, body.ToArray());
    }

    private static async Task<byte[]> ReadToEof(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var received = new MemoryStream();
        await stream.CopyToAsync(received, timeout.Token);
        return received.ToArray();
    }

    private sealed class SocketPair : IDisposable
    {
        public TcpClient Client { get; }
        public TcpClient Server { get; }
        public TcpOutbound Outbound { get; }

        private SocketPair(TcpClient client, TcpClient server)
        {
            Client = client;
            Server = server;
            Outbound = new TcpOutbound(server);
        }

        public static async Task<SocketPair> Connect()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            return new SocketPair(client, await listener.AcceptTcpClientAsync());
        }

        public void Dispose()
        {
            Outbound.Close();
            Client.Dispose();
            Server.Dispose();
        }
    }
}
