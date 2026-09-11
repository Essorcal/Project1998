using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LoginServer;
using Protocol.Tk495;
using Xunit;

namespace Tests;

/// <summary>
/// The login channel's outbound queue, over real loopback sockets inside this process.
///
/// <para>What these guard is a silent failure, not a crash: <c>LoginSession.Send</c> used to be a
/// synchronous <c>_stream.Write</c> under a lock, on the session's own read loop. A peer that stops reading
/// does not make it throw — it makes it WAIT, for as long as the kernel is willing to, with the read loop
/// stuck behind it and the log showing nothing at all. Every fact below is written so that restoring the old
/// shape makes it hang to its own timeout rather than fail an assert, which is exactly the point.</para>
/// </summary>
public sealed class LoginOutboundTests
{
    // Short enough that a stalled write is observable in well under a second, long enough that a loaded
    // build agent cannot trip it by accident on a peer that IS reading.
    private const int WriteBoundMs = 300;

    /// <summary>Fact 1a, the whole reason this type exists: a peer that never reads does not hold the
    /// sender. Every Send returns promptly and the connection is dropped once the write bound is exceeded.
    ///
    /// <para>FALSIFIED by replacing the queue with the old synchronous write (<c>Stream.Write(frame, 0,
    /// frame.Length)</c> in place of <c>_out.Send</c>): the second Send never returns, and the test fails on
    /// the xunit timeout rather than on an assert — the red is a hang, because the bug is a hang.</para></summary>
    [Fact]
    public async Task APeerThatNeverReadsDoesNotBlockTheSenderAndIsDroppedAtTheWriteBound()
    {
        using var pair = await SocketPair.Connect(WriteBoundMs);
        // Fixed buffers on both ends so a few megabytes is enough to fill the path: Windows auto-tunes the
        // send buffer otherwise and absorbs megabytes on loopback without ever waiting for the peer. 64KB
        // rather than a kilobyte, for fact 3's reason — the peer below has to drain whatever the kernel
        // accepted before it can see EOF, and pushing megabytes through kilobyte buffers takes seconds on
        // Linux. The client is never read from until then: this is the "stopped reading" peer.
        pair.Server.SendBufferSize = 64 * 1024;
        pair.Client.ReceiveBufferSize = 64 * 1024;

        Task writer = pair.Outbound.RunWriterAsync();
        // Bulk, to fill the path rather than to park this particular write. Measured while fixing this: a
        // multi-megabyte WriteAsync to a peer that is not reading COMPLETES in about a millisecond on
        // Windows, because the kernel takes the bytes. What parks is a later write, once the path is
        // genuinely full — which is why the small frames below are part of the setup and not just noise.
        Assert.True(pair.Outbound.Send(new byte[4 * 1024 * 1024]));

        // The frames behind the bulk are what the read loop would be enqueuing while the peer sulks — and one
        // of them is the write that actually parks, which is what the bound below is measured against.
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 8; i++) pair.Outbound.Send(Status(0x0F, $"frame {i}"));
        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < 250,
                    $"enqueuing behind a stalled peer took {watch.ElapsedMilliseconds}ms; it must not wait at all");

        await writer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal($"write stalled past {WriteBoundMs}ms", pair.Outbound.DropReason);
        // Dropped, not merely stalled. Deliberately NOT asserted by counting delivered bytes: once the peer
        // starts reading again the loopback stack happily flushes whatever it had already accepted, so a
        // byte count measures Windows, not us. What is ours is that the connection ENDS — the peer reaches
        // EOF instead of the session hanging on it forever, and further sends are refused.
        Assert.False(pair.Outbound.Send(Status(0x0F, "after the drop")));
        // ReadToEof's own 10s deadline IS the assertion: it throws if the connection never ends, which is the
        // red for a peer that was not dropped. Deliberately no tighter sub-deadline on top of it — the peer
        // must first drain whatever the kernel accepted before the drop, and how long that takes is the
        // runner's property, not ours. A 5s one here is what made this fact red on upstream CI at d6a48b4
        // ("the dropped peer took 6071ms to see the connection end") while the same commit passed on the fork.
        await ReadToEof(pair.Client.GetStream());
    }

    /// <summary>Fact 1b, the other bound. With a peer that is not reading and no write bound in play, the
    /// queue itself is finite: once <c>LoginOutbound.Capacity</c> frames are outstanding, Send refuses, and
    /// refusing is what the session turns into a drop. Neither bound relies on the other.
    ///
    /// <para>FALSIFIED by widening the channel to unbounded: Send never returns false and the assert on the
    /// refusal fails.</para></summary>
    [Fact]
    public async Task TheQueueRefusesFramesOnceCapacityIsReached()
    {
        // A generous write bound so this fact is about the queue and nothing else.
        using var pair = await SocketPair.Connect(60_000);

        // The writer is NOT started yet, and that is the whole trick: with nothing dequeuing, the count below
        // is the queue's own bound rather than a race against the thread pool. The first shape of this fact
        // started the writer and tried to park it with a four-megabyte frame; on this platform that write
        // completes in about a millisecond, so the writer went on dequeuing during the fill and `accepted`
        // came out at 65 on any run that was not under full-suite load. Nothing here depends on timing.
        int accepted = 0;
        var watch = Stopwatch.StartNew();
        // Sixteen more than the capacity: the queue takes exactly Capacity of them and refuses every one
        // after that, which is what the session turns into a drop.
        for (int i = 0; i < LoginOutbound.Capacity + 16; i++)
            if (pair.Outbound.Send(Status(0x0F, $"frame {i}"))) accepted++;
        watch.Stop();

        Assert.Equal(LoginOutbound.Capacity, accepted);
        Assert.Equal(LoginOutbound.Capacity, pair.Outbound.QueueDepth);   // accepted AND still held, none lost
        Assert.True(watch.ElapsedMilliseconds < 250,
                    $"filling and overflowing the queue took {watch.ElapsedMilliseconds}ms; no Send may wait");

        // Only now does anything drain: the writer picks the backlog up and the teardown closes behind it.
        Task writer = pair.Outbound.RunWriterAsync();
        pair.Outbound.Close();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Fact 2: a peer that reads normally gets every frame, whole and in order, byte for byte
    /// against the same <c>TkPacket.Build</c> bodies. This is the "same bytes, same order" half of the
    /// change — the single-reader channel is what replaced the send lock, and a second writer or a reordered
    /// queue would show up here as interleaved or transposed frames.
    ///
    /// <para>FALSIFIED by reversing the enqueue order in the loop below: the comparison fails on the first
    /// transposed frame.</para></summary>
    [Fact]
    public async Task ANormalPeerReceivesEveryFrameWholeAndInOrder()
    {
        using var pair = await SocketPair.Connect(WriteBoundMs);
        Task writer = pair.Outbound.RunWriterAsync();

        var expected = new List<byte>();
        // The welcome, then the shapes a real login conversation actually sends: the availability OK, a
        // handful of status lines, and the redirect last.
        byte[] welcome = Welcome.Bytes;
        Assert.True(pair.Outbound.Send(welcome));
        expected.AddRange(welcome);
        for (int i = 0; i < 24; i++)
        {
            byte[] frame = Status(i % 2 == 0 ? (byte)0x0F : (byte)0x00, $"status line {i}");
            Assert.True(pair.Outbound.Send(frame));
            expected.AddRange(frame);
        }
        byte[] redirect = Redirect();
        Assert.True(pair.Outbound.Send(redirect));
        expected.AddRange(redirect);

        await pair.Outbound.CloseAfterDrainAsync();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(expected.ToArray(), await ReadToEof(pair.Client.GetStream()));
        Assert.Null(pair.Outbound.DropReason);
    }

    /// <summary>Fact 3, the drain: the redirect is the last frame a successful login sends, and the client
    /// learns the game host and port from exactly those bytes. A peer that is SLOW but does eventually read
    /// still gets that final frame, and the socket closes only behind it.
    ///
    /// <para>The peer here is a window that stays shut for <c>StallMs</c> and then opens, with enough bulk
    /// ahead of the redirect to park the writer. The bulk is stand-in volume rather than a real login frame:
    /// what is being measured is the peer's window, not the opcode.</para>
    ///
    /// <para>What is asserted is that the teardown WAITS for the last queued frame rather than racing it —
    /// and not that a bare close would lose bytes, which on this platform it does not. Measured while writing
    /// this: Windows completes a pending overlapped send through a graceful <c>closesocket</c>, so every byte
    /// still arrived with the drain removed and with the socket closed mid-write in fact 1a. A byte count
    /// would therefore assert Windows' behaviour, not ours. The drain is what makes the session's
    /// <c>-- CLOSE</c> line mean the bytes are gone, and it is the guarantee on any platform or path that
    /// does NOT flush — an RST, a peer that closed first, SO_LINGER 0.</para>
    ///
    /// <para>FALSIFIED by replacing <c>CloseAfterDrainAsync</c>'s body with <c>await Task.Yield(); Close();</c>:
    /// it returns in about a millisecond, while the writer is still parked mid-bulk waiting on the peer, and
    /// the elapsed-time assert below fails.</para></summary>
    [Fact]
    public async Task TheRedirectsLastFrameReachesASlowButReadingPeerBeforeTheClose()
    {
        const int StallMs = 150;        // shut window, then open — comfortably inside the 1s drain bound
        // Enough to park the writer several times over (the two 64KB buffers below are all the kernel can
        // hold), and small enough that the peer's catch-up read is not itself a race against the drain's
        // one-second bound. It was four megabytes, which is 4MB of catch-up through 64KB buffers on a
        // shared Linux runner: fork run 34552742122 failed here with "the drain took 1118ms and hit its own
        // bound". Same mis-sizing as fact 1a's, one fact further along.
        const int BulkBytes = 512 * 1024;

        // A generous write bound: this fact is about the drain, and the peer's deliberate stall must not be
        // mistaken for the stalled-write drop that fact 1a covers.
        using var pair = await SocketPair.Connect(5_000);
        // Setting the socket buffers at all is what makes the sender park: Windows auto-tunes the send
        // buffer otherwise and will absorb megabytes on loopback without ever waiting for the peer (an 8MB
        // version of this fact still passed with the drain removed, for exactly that reason). 64KB, the same
        // size fact 1a uses, because a bound measured in a second cannot also be waiting on kilobyte buffers
        // to pass megabytes on Linux — which is how CI first failed this.
        pair.Server.SendBufferSize = 64 * 1024;
        pair.Client.ReceiveBufferSize = 64 * 1024;
        var expected = new List<byte>();
        // Opaque bulk, not a framed packet: TkPacket's length field is 16 bits and this is deliberately
        // larger than anything the kernel will absorb in one go. Built BEFORE the peer's stall clock starts,
        // so that filling the buffer is not silently charged against the stall.
        byte[] bulk = Enumerable.Range(0, BulkBytes).Select(i => (byte)i).ToArray();

        Task writer = pair.Outbound.RunWriterAsync();
        Task<byte[]> reader = ReadAfterStallToEof(pair.Client.GetStream(), StallMs);

        Assert.True(pair.Outbound.Send(bulk));
        expected.AddRange(bulk);
        byte[] redirect = Redirect();
        Assert.True(pair.Outbound.Send(redirect));
        expected.AddRange(redirect);

        // The writer cannot possibly have finished before the peer's window opens, so a teardown that returns
        // sooner than that is one that did not wait for the redirect.
        var drain = Stopwatch.StartNew();
        await pair.Outbound.CloseAfterDrainAsync();
        drain.Stop();
        // Half the stall, not all of it: the peer's timer and this stopwatch start a few scheduler ticks
        // apart, and the distinction being drawn is between ~1ms (did not wait) and ~150ms (waited).
        Assert.True(drain.ElapsedMilliseconds >= StallMs / 2,
                    $"the teardown returned after {drain.ElapsedMilliseconds}ms without waiting for the "
                    + $"queued frames; the peer had not even started reading until {StallMs}ms");
        Assert.True(drain.ElapsedMilliseconds < LoginOutbound.DrainTimeoutMs,
                    $"the drain took {drain.ElapsedMilliseconds}ms and hit its own bound");
        await writer.WaitAsync(TimeSpan.FromSeconds(10));

        byte[] received = await reader.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(expected.ToArray(), received);
        // ...and the redirect is the LAST thing on the wire, with the socket closed behind it.
        Assert.Equal(redirect, received[^redirect.Length..]);
        Assert.Null(pair.Outbound.DropReason);
    }

    /// <summary>Fact 4: Close is idempotent, it completes the writer exactly once, and a Send afterwards is
    /// refused rather than throwing — the session's teardown races itself (the read loop's finally, the
    /// writer's, and a refused Send all reach Close), and a throw on any of those paths would surface as a
    /// bogus "error:" line on an ordinary logout.
    ///
    /// <para>FALSIFIED by restoring the old synchronous send (<c>Stream.Write(frame, 0, frame.Length)</c>):
    /// the send after the close throws ObjectDisposedException instead of being refused, which on the real
    /// session is the bogus <c>error:</c> line an ordinary logout used to be capable of printing.</para>
    ///
    /// <para>Two things this does NOT prove, checked rather than assumed: removing the
    /// <c>Interlocked.Exchange</c> gate in <c>Close</c>, and swapping <c>TrySetResult</c> for
    /// <c>SetResult</c>, both leave it green — .NET's socket Dispose is already idempotent and the writer's
    /// finally only runs once. The gate and the Try are cheap insurance, not load-bearing here.</para></summary>
    [Fact]
    public async Task CloseIsIdempotentCompletesTheWriterAndRefusesLaterSends()
    {
        using var pair = await SocketPair.Connect(WriteBoundMs);
        Task writer = pair.Outbound.RunWriterAsync();
        Assert.True(pair.Outbound.Send(Status(0x0F, "before the close")));

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => pair.Outbound.Close())));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => pair.Outbound.CloseAfterDrainAsync()));

        await writer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(pair.Outbound.Send(Status(0x0F, "after the close")));   // refused, not thrown
    }

    /// <summary>The login channel's one server-&gt;client frame, built exactly as
    /// <c>LoginSession.SendStatus</c> builds it.</summary>
    private static byte[] Status(byte code, string text)
    {
        var t = Encoding.ASCII.GetBytes(text);
        var body = new List<byte> { code, (byte)t.Length };
        body.AddRange(t);
        body.Add(0);
        return TkPacket.Build(0x02, 0x02, TkCrypt.Crypt(body.ToArray(), 0x02, TkCrypt.LoginKey));
    }

    private static byte[] Redirect() =>
        LoginRedirect.Build(new byte[] { 127, 0, 0, 1 }, 2005, "drainprobe", new byte[] { 0, 1, 18, 17, 0 });

    private static async Task<byte[]> ReadToEof(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var received = new MemoryStream();
        try { await stream.CopyToAsync(received, timeout.Token); }
        catch (IOException) { /* a dropped peer sees a reset, not a graceful EOF */ }
        return received.ToArray();
    }

    /// <summary>A peer whose receive window stays shut for <paramref name="stallMs"/> and then opens. Reading
    /// nothing at all is what parks a sender; reading eventually is what makes this a slow peer rather than a
    /// dead one.</summary>
    private static async Task<byte[]> ReadAfterStallToEof(NetworkStream stream, int stallMs)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.Delay(stallMs, timeout.Token);
        using var received = new MemoryStream();
        await stream.CopyToAsync(received, timeout.Token);
        return received.ToArray();
    }

    private sealed class SocketPair : IDisposable
    {
        public TcpClient Client { get; }
        public TcpClient Server { get; }
        public LoginOutbound Outbound { get; }

        private SocketPair(TcpClient client, TcpClient server, int writeTimeoutMs)
        {
            Client = client;
            Server = server;
            Outbound = new LoginOutbound(server, "127.0.0.1:0 (test)", writeTimeoutMs);
        }

        public static async Task<SocketPair> Connect(int writeTimeoutMs)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            return new SocketPair(client, await listener.AcceptTcpClientAsync(), writeTimeoutMs);
        }

        public void Dispose()
        {
            Outbound.Close();
            Client.Dispose();
            Server.Dispose();
        }
    }
}
