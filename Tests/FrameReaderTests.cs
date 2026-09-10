using System.Text;
using System.Threading.Channels;
using Protocol.Tk495;
using Xunit;

namespace Tests;

/// <summary>
/// Stream-level facts for the one frame reader both processes now run (Protocol.Tk495.FrameReader).
///
/// <para>These are silent-failure guards, not unit tests for their own sake. Framing off a socket fails
/// QUIETLY: a frame delivered a byte at a time and yielded early is a truncated packet, a partial tail
/// dropped instead of retained is a lost command, and a handshake watchdog that fires after the client has
/// spoken disconnects a real player for no reason — none of which throws, and none of which any existing
/// test reached, because the read loop only ever ran against a live socket.</para>
///
/// <para>The stream is in-memory and hands back exactly one pushed chunk per read, so "one byte per read"
/// and "two frames plus half of a third in one read" are things a test can state exactly. Every wait is
/// bounded.</para>
/// </summary>
public sealed class FrameReaderTests
{
    // ---- facts ---------------------------------------------------------------------------------------

    /// <summary>Fact 1: a frame dribbled in one byte per read is yielded ONCE, whole, and not before its
    /// last byte has arrived.</summary>
    [Fact]
    public async Task AFrameDeliveredOneBytePerReadIsYieldedOnceWholeOnItsLastByte()
    {
        var frame = TkPacket.Build(0x10, 0x7F, new byte[] { 1, 2, 3 });
        await using var h = new Harness();
        h.Start();

        for (int i = 0; i < frame.Length; i++)
        {
            h.Stream.Push(new[] { frame[i] });
            Assert.True(await h.NextReadAsync());
            Assert.Equal(i == frame.Length - 1 ? 1 : 0, h.Frames.Count);
        }

        Assert.Equal(0x10, h.Frames[0].Opcode);
        Assert.Equal(0x7F, h.Frames[0].Increment);
        Assert.Equal(new byte[] { 1, 2, 3 }, h.Frames[0].Body);
        Assert.Equal(Enumerable.Repeat(1, frame.Length), h.ReadSizes);
    }

    /// <summary>Fact 2: two whole frames in one read come back in order, and the partial third that shared
    /// that read is RETAINED and completed by the next one.</summary>
    [Fact]
    public async Task TwoFramesInOneReadComeBackInOrderAndAPartialTailWaitsForTheNextRead()
    {
        var a = TkPacket.Build(0x03, 0x01, new byte[] { 0xA0 });
        var b = TkPacket.Build(0x04, 0x02, new byte[] { 0xB0, 0xB1 });
        var c = TkPacket.Build(0x05, 0x03, new byte[] { 0xC0, 0xC1, 0xC2 });

        await using var h = new Harness();
        h.Start();

        h.Stream.Push(a.Concat(b).Concat(c.Take(4)).ToArray());
        Assert.True(await h.NextReadAsync());
        Assert.Equal(new byte[] { 0x03, 0x04 }, h.Frames.Select(f => f.Opcode).ToArray());

        h.Stream.Push(c.Skip(4).ToArray());
        Assert.True(await h.NextReadAsync());
        Assert.Equal(new byte[] { 0x03, 0x04, 0x05 }, h.Frames.Select(f => f.Opcode).ToArray());
        Assert.Equal(new byte[] { 0xC0, 0xC1, 0xC2 }, h.Frames[2].Body);
    }

    /// <summary>Fact 3: a buffer whose head byte is not 0xAA DROPS the connection — the enumeration ends, no
    /// frame is yielded, and the after-read hook never runs for that read.
    ///
    /// <para>This used to be a stall: framing only advances while the head byte is 0xAA, so one stray byte
    /// wedged the connection's inbound half forever and nothing noticed except, before the handshake, the
    /// watchdog. The watchdog is NOT what ends this: the budget here is a minute and the hook count is
    /// asserted at zero, so the drop is the reader's own.</para>
    ///
    /// <para>Falsified by deleting the head-byte block from ReadFramesAsync: the pump never completes, the
    /// wait in Completion times out and the fact is red on the enumeration, with h.Frames still empty.</para>
    /// </summary>
    [Fact]
    public async Task ABufferWhoseHeadByteIsNotAaDropsTheConnection()
    {
        var frame = TkPacket.Build(0x10, 0x00, new byte[] { 9 });
        var junkThenFrame = new byte[] { 0x00 }.Concat(frame).ToArray();

        await using var h = new Harness();
        h.Start();

        h.Stream.Push(junkThenFrame);
        await h.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(h.Completion.IsCompletedSuccessfully);
        Assert.Empty(h.Frames);                            // nothing was framed past the stray byte
        Assert.Equal(junkThenFrame, h.BufferedSnapshot);   // and nothing was skipped looking for one
        Assert.Equal(0, Volatile.Read(ref h.TimeoutCalls));
        Assert.Equal(0, h.AfterReads);
    }

    /// <summary>Fact 3b: the frames that shared the read with the stray byte are yielded FIRST. The drop is
    /// checked after framing, so a client that sends two good packets and then goes wrong loses neither.
    ///
    /// <para>Falsified by moving the head-byte block above the framing loop: the head byte at that point is
    /// the first frame's own 0xAA, so the read passes the rule, the two frames are yielded and the stray byte
    /// is left for a read that never comes — the enumeration never ends and the fact is red on its
    /// Completion wait. That is the whole content of this fact: the drop belongs to the read that exposed the
    /// stray byte, not to the next one.</para></summary>
    [Fact]
    public async Task FramesAheadOfAStrayByteAreYieldedBeforeTheConnectionIsDropped()
    {
        var a = TkPacket.Build(0x03, 0x01, new byte[] { 0xA0 });
        var b = TkPacket.Build(0x04, 0x02, new byte[] { 0xB0, 0xB1 });

        await using var h = new Harness();
        h.Start();

        h.Stream.Push(a.Concat(b).Concat(new byte[] { 0x00 }).ToArray());
        await h.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(h.Completion.IsCompletedSuccessfully);
        Assert.Equal(new byte[] { 0x03, 0x04 }, h.Frames.Select(f => f.Opcode).ToArray());
        Assert.Equal(0, Volatile.Read(ref h.TimeoutCalls));
    }

    /// <summary>Fact 3c: the status probe still wins. "GET " is a non-0xAA head, so the game's probe hook has
    /// to see those bytes BEFORE the head-byte rule does or the docs site's status poll (and Test-Branch.ps1's
    /// readiness check) becomes a dropped connection. The hook stopping the loop is what ends this
    /// enumeration: the hook is recorded as having run, with the probe's bytes.
    ///
    /// <para>No Warn line can be written on this path — the hook's <c>yield break</c> is ahead of both drop
    /// rules in the method, so neither is reached. That ORDER is what the fact pins: move either rule above
    /// the OnBufferedAsync call and the hook never runs, leaving BufferedCounts empty and the fact red.</para>
    /// </summary>
    [Fact]
    public async Task TheStatusProbeHookRunsBeforeTheNonAaHeadIsDropped()
    {
        var probe = Encoding.ASCII.GetBytes("GET /status HTTP/1.1\r\n\r\n");

        await using var h = new Harness { StopAfterBuffered = buf => buf.Count >= 4 && buf[0] == (byte)'G' };
        h.Start();

        h.Stream.Push(probe);
        await h.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(h.Completion.IsCompletedSuccessfully);
        Assert.Equal(new[] { probe.Length }, h.BufferedCounts.ToArray());   // the hook saw the probe
        Assert.Equal(probe, h.BufferedSnapshot);
        Assert.Empty(h.Frames);
        Assert.Equal(0, h.AfterReads);
    }

    /// <summary>Fact 3d: the unframed buffer is BOUNDED before the handshake, where the watchdog bounds the
    /// time but not the bytes. A peer that opens a maximum-length header and then streams filler is dropped,
    /// and the buffer it got to hold never passed one maximum frame plus the read that carried it there. The
    /// budget here is a minute and the timeout hook is asserted at zero, so the watchdog is not what ended it.
    ///
    /// <para>Which rule fires, exactly: the header claims 3 + 0xFFFF bytes, so the framing loop takes that
    /// frame on the read that completes it (garbage the session ignores — pinned below, because delivering it
    /// at all is part of the behaviour) and leaves the filler tail, whose head byte is not 0xAA. The
    /// <see cref="FrameReader.MaxUnframedBytes"/> cap cannot fire while that rule stands — see its doc — which
    /// is why this fact pins the BOUND, the thing both rules exist to guarantee, rather than the cap's branch.
    /// </para>
    ///
    /// <para>Falsified by deleting the head-byte block: nothing drops the tail, no further bytes arrive, the
    /// pump sits in ReadAsync and the wait in Completion times out. Falsified a second way, for the bound
    /// itself, by deleting BOTH blocks and pushing filler without end (300,000 bytes): the buffer peaked at
    /// 234,462B and the last assertion went red. With the cap block alone put back, the same stream stays
    /// inside the bound and is dropped — which is the layering its doc claims.</para></summary>
    [Fact]
    public async Task APeerThatOpensAMaximumHeaderAndStreamsFillerIsDroppedWithABoundedBuffer()
    {
        await using var h = new Harness();
        h.Start();

        h.Stream.Push(NeverFramingBytes(FrameReader.MaxUnframedBytes + 1));
        await h.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(h.Completion.IsCompletedSuccessfully);
        Assert.Single(h.Frames);                            // the maximum-length garbage its header claimed
        Assert.Equal(0, Volatile.Read(ref h.TimeoutCalls));
        Assert.True(h.BufferedCounts.Max() <= FrameReader.MaxUnframedBytes + FrameReader.ReadBufferBytes,
            $"unframed buffer peaked at {h.BufferedCounts.Max()}B");
    }

    /// <summary>Fact 3e: and the same AFTER the handshake, which is the case nothing else bounds at all —
    /// there is no read timeout once a connection has spoken, by design. The frame that established it is
    /// delivered and kept; the filler that follows is bounded and dropped.
    ///
    /// <para>Falsified exactly as 3d is.</para></summary>
    [Fact]
    public async Task AnEstablishedConnectionThatStopsFramingIsDroppedWithABoundedBuffer()
    {
        await using var h = new Harness();
        h.Start();

        h.Stream.Push(TkPacket.Build(0x10, 0x00, new byte[] { 1 }));
        Assert.True(await h.NextReadAsync());
        Assert.Single(h.Frames);

        h.Stream.Push(NeverFramingBytes(FrameReader.MaxUnframedBytes + 1));
        await h.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(h.Completion.IsCompletedSuccessfully);
        Assert.Equal(2, h.Frames.Count);                    // the real one, then the garbage the header claimed
        Assert.Equal(0x10, h.Frames[0].Opcode);
        Assert.Equal(0, Volatile.Read(ref h.TimeoutCalls));
        Assert.True(h.BufferedCounts.Max() <= FrameReader.MaxUnframedBytes + FrameReader.ReadBufferBytes,
            $"unframed buffer peaked at {h.BufferedCounts.Max()}B");
    }

    /// <summary>Fact 3f: the bound is not too tight. A frame of the MAXIMUM legal length — 3 + 0xFFFF bytes,
    /// the largest the u16 length field can describe — delivered in 4KB reads is yielded whole, once, and
    /// never trips the cap. This is the boundary case that makes the number right rather than merely large.
    ///
    /// <para>Falsified by shrinking <see cref="FrameReader.MaxUnframedBytes"/> below 3 + 0xFFFF — 0xFFFF, say:
    /// the 16th read leaves 65,536 unframed bytes, the cap drops the connection two reads before the frame it
    /// is holding completes, and the fact is red on the 16th NextReadAsync, which times out with no frame
    /// yielded. That is the falsification that makes the NUMBER the thing under test, and it is why the
    /// constant is not compared to the frame length until the end.</para></summary>
    [Fact]
    public async Task AMaximumLengthFrameIsYieldedWholeAndNeverTripsTheCap()
    {
        // len = 2 + body = 0xFFFF, so the whole frame is 3 + 0xFFFF bytes: the largest this wire format has.
        var frame = TkPacket.Build(0x10, 0x00, new byte[0xFFFF - 2]);

        await using var h = new Harness();
        h.Start();

        h.Stream.Push(frame);
        int reads = (frame.Length + FrameReader.ReadBufferBytes - 1) / FrameReader.ReadBufferBytes;
        for (int i = 0; i < reads; i++) Assert.True(await h.NextReadAsync());

        Assert.False(h.Completion.IsCompleted);   // still connected: the cap did not fire
        var yielded = Assert.Single(h.Frames);
        Assert.Equal(0x10, yielded.Opcode);
        Assert.Equal(0xFFFF - 2, yielded.Body.Length);
        // Last, so that a cap set below the maximum is caught by the reads above rather than here: the cap IS
        // one maximum legal frame, not merely at least one.
        Assert.Equal(FrameReader.MaxUnframedBytes, frame.Length);
    }

    /// <summary>A header claiming the maximum length, then filler. Until the claim is satisfied the head byte
    /// stays 0xAA and nothing frames, so the buffer grows one read at a time — this is the stream the bound is
    /// about. At <paramref name="total"/> = one byte past <see cref="FrameReader.MaxUnframedBytes"/> the claim
    /// is finally satisfied on the last read: the maximum-length garbage is framed and the single filler byte
    /// behind it is the non-0xAA head that ends the connection.</summary>
    private static byte[] NeverFramingBytes(int total)
    {
        var bytes = new byte[total];
        bytes[0] = 0xAA;
        bytes[1] = 0xFF;
        bytes[2] = 0xFF;
        return bytes;
    }

    /// <summary>Fact 3g: a frame whose length field is under <see cref="TkPacket.MinLength"/> drops the
    /// connection, and the good frame that shared its read is yielded first.
    ///
    /// <para>This is the third drop rule, and the only one whose absence was an EXCEPTION rather than a
    /// stall: <c>3 + len</c> with <c>len</c> 0 or 1 sliced a negative body length, and the throw crossed
    /// MoveNextAsync into each session's catch — on the game side the stackful Error clause, one line per
    /// connection. So what this pins is that the loop ends here NORMALLY: the enumeration completes without
    /// throwing, the timeout hook never fires (the budget is a minute), the after-read hook does not run for
    /// the dropping read, and the frame ahead of the malformed head is delivered. The head byte here is
    /// 0xAA, so the head-byte rule cannot be what ended it.</para>
    ///
    /// <para>Falsified by deleting the malformed block from ReadFramesAsync: the framing loop still breaks on
    /// the malformed head, nothing drops it, no further bytes arrive, and the fact is red on its Completion
    /// wait with a TimeoutException — the same wedge the head-byte rule's falsification produces, which is
    /// exactly why returning false from the parser alone would not have been enough. Falsified a second way
    /// by putting the old parser body back (see <c>PacketCodecTests</c>): the enumeration ends, but through
    /// the throw, and the fact is red on IsCompletedSuccessfully.</para></summary>
    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    public async Task AFrameWhoseLengthFieldIsUnderTwoDropsTheConnectionAfterTheFrameAheadOfIt(byte length)
    {
        var good = TkPacket.Build(0x10, 0x7F, new byte[] { 1, 2, 3 });
        var malformed = new byte[] { 0xAA, 0x00, length, 0x00, 0x00 };

        await using var h = new Harness();
        h.Start();

        h.Stream.Push(good.Concat(malformed).ToArray());
        await h.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(h.Completion.IsCompletedSuccessfully);   // it ended, and not by throwing
        var yielded = Assert.Single(h.Frames);               // the good frame came first and was not lost
        Assert.Equal(0x10, yielded.Opcode);
        Assert.Equal(new byte[] { 1, 2, 3 }, yielded.Body);
        Assert.Equal(0xAA, h.BufferedSnapshot[good.Length]);   // the head the drop was made on IS 0xAA
        Assert.Equal(0, Volatile.Read(ref h.TimeoutCalls));
        Assert.Equal(0, h.AfterReads);
    }

    /// <summary>Fact 3h: the buffered hook still wins over the malformed rule, as it does over the other two.
    /// The hook is where the game answers an HTTP status probe, and a drop rule ahead of it would take that
    /// answer away.
    ///
    /// <para>The probe's own bytes are a non-0xAA head, so fact 3c pins the hook against the head-byte rule
    /// with the real thing. The malformed rule needs a 0xAA head to reach, which no HTTP request has — so
    /// this fact stands the hook in front of a malformed frame instead and asserts the hook RAN, with the
    /// bytes, before the connection went.</para>
    ///
    /// <para>Falsified by moving the malformed block above the OnBufferedAsync call: the connection is
    /// dropped before the hook is ever called, BufferedCounts is empty and the fact is red on that
    /// assertion.</para></summary>
    [Fact]
    public async Task TheBufferedHookRunsBeforeAMalformedLengthIsDropped()
    {
        var malformed = new byte[] { 0xAA, 0x00, 0x01, 0x00, 0x00 };

        await using var h = new Harness { StopAfterBuffered = buf => buf.Count >= 5 && buf[0] == 0xAA };
        h.Start();

        h.Stream.Push(malformed);
        await h.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(h.Completion.IsCompletedSuccessfully);
        Assert.Equal(new[] { malformed.Length }, h.BufferedCounts.ToArray());   // the hook saw the bytes
        Assert.Equal(malformed, h.BufferedSnapshot);
        Assert.Empty(h.Frames);
        Assert.Equal(0, h.AfterReads);
    }

    /// <summary>Fact 4a: the handshake watchdog calls the close hook EXACTLY once when no valid frame
    /// arrives inside the budget.</summary>
    [Fact]
    public async Task TheHandshakeWatchdogDropsASilentConnectionExactlyOnce()
    {
        await using var h = new Harness(handshakeMs: 200);
        h.Start();

        Assert.True(await h.WaitForTimeoutAsync());
        await Task.Delay(500);   // several more budgets: a re-fire would show up here
        Assert.Equal(1, Volatile.Read(ref h.TimeoutCalls));
        Assert.Empty(h.Frames);
    }

    /// <summary>Fact 4b: and it NEVER fires against a connection that has already framed a packet, even when
    /// that connection then goes silent for several budgets. This is the invariant that keeps an AFK player
    /// connected.</summary>
    [Fact]
    public async Task TheHandshakeWatchdogNeverDropsAConnectionThatAlreadyFramedAPacket()
    {
        await using var h = new Harness(handshakeMs: 200);
        h.Stream.Push(TkPacket.Build(0x10, 0x00, new byte[] { 1 }));   // queued before the budget starts
        h.Start();

        Assert.True(await h.NextReadAsync());
        Assert.Single(h.Frames);

        await Task.Delay(800);   // four budgets of silence
        Assert.Equal(0, Volatile.Read(ref h.TimeoutCalls));
    }

    /// <summary>Fact 5: a read returning 0 (the peer closed) ends the enumeration.</summary>
    [Fact]
    public async Task AReadReturningZeroEndsTheEnumeration()
    {
        await using var h = new Harness();
        h.Start();

        h.Stream.Push(TkPacket.Build(0x10, 0x00, new byte[] { 1 }));
        Assert.True(await h.NextReadAsync());
        Assert.False(h.Completion.IsCompleted);

        h.Stream.Eof();
        await h.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(h.Completion.IsCompletedSuccessfully);
    }

    /// <summary>Fact 6: the per-read hooks fire for a read that completes NO frame. This is the whole reason
    /// the reader has hooks instead of only yielding frames — the game's silence stamp, status probe and
    /// throttled autosave all run per read, and a two-byte read is still a read.</summary>
    [Fact]
    public async Task ThePerReadHooksFireForAReadThatCompletesNoFrame()
    {
        await using var h = new Harness();
        h.Start();

        h.Stream.Push(new byte[] { 0xAA, 0x00 });   // not even a whole header
        Assert.True(await h.NextReadAsync());

        Assert.Empty(h.Frames);
        Assert.Equal(new[] { 2 }, h.ReadSizes);
        Assert.Equal(new[] { 2 }, h.BufferedCounts);
        Assert.Equal(1, h.AfterReads);
    }

    /// <summary>The buffered hook can stop the loop before anything is framed, which is how the game answers
    /// an HTTP status probe and breaks out (Server/StatusResponder). Nothing is yielded and the after-read
    /// hook does not run for that read.</summary>
    [Fact]
    public async Task ABufferedHookThatAsksToStopEndsTheLoopBeforeFraming()
    {
        await using var h = new Harness { StopAfterBuffered = _ => true };
        h.Start();

        h.Stream.Push(TkPacket.Build(0x10, 0x00, new byte[] { 1 }));
        await h.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(h.Frames);
        Assert.Equal(0, h.AfterReads);
    }

    /// <summary>The welcome both processes send is one array now; these are the bytes their two identical
    /// builders produced. A drifted greeting is a hung login screen, not an exception.</summary>
    [Fact]
    public void TheSharedWelcomeIsTheGreetingBothProcessesUsedToBuildSeparately()
    {
        var expected = new byte[] { 0xAA, 0x00, 0x13, 0x7E, 0x1B }
            .Concat(Encoding.ASCII.GetBytes("CONNECTED SERVER\n")).ToArray();
        Assert.Equal(expected, Welcome.Bytes);
    }

    /// <summary>The one host parser: four octets in normal order, loopback for anything else. A wrong
    /// fallback sends every client to an address that is not this server.</summary>
    [Theory]
    [InlineData(null, new byte[] { 127, 0, 0, 1 })]
    [InlineData("", new byte[] { 127, 0, 0, 1 })]
    [InlineData("   ", new byte[] { 127, 0, 0, 1 })]
    [InlineData("10.0.0.7", new byte[] { 10, 0, 0, 7 })]
    [InlineData("255.255.255.255", new byte[] { 255, 255, 255, 255 })]
    [InlineData("1.2.3", new byte[] { 127, 0, 0, 1 })]
    [InlineData("1.2.3.4.5", new byte[] { 127, 0, 0, 1 })]
    [InlineData("1.2.3.256", new byte[] { 127, 0, 0, 1 })]
    [InlineData("a.b.c.d", new byte[] { 127, 0, 0, 1 })]
    public void TheHostParserTakesFourOctetsAndFallsBackToLoopback(string? host, byte[] expected) =>
        Assert.Equal(expected, HostAddress.Parse(host));

    // ---- harness -------------------------------------------------------------------------------------

    /// <summary>A reader wired to an in-memory stream, with every hook recorded. Hook state is written on
    /// the pump thread and read on the test thread only after <see cref="NextReadAsync"/> has handed over,
    /// which is the barrier.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public readonly ChunkStream Stream = new();
        public readonly List<TkPacket> Frames = new();
        public readonly List<int> ReadSizes = new();
        public readonly List<int> BufferedCounts = new();
        public byte[] BufferedSnapshot = Array.Empty<byte>();
        public int AfterReads;
        public int TimeoutCalls;

        /// <summary>Stands in for the game's status probe: return true to stop the loop.</summary>
        public Func<List<byte>, bool>? StopAfterBuffered { get; init; }

        private readonly int _handshakeMs;
        private readonly SemaphoreSlim _reads = new(0);
        private int _established;
        private Task _pump = Task.CompletedTask;

        public Harness(int handshakeMs = 60_000) => _handshakeMs = handshakeMs;

        public Task Completion => _pump;

        /// <summary>Begin reading. Separate from the constructor so a test can queue bytes BEFORE the
        /// handshake budget starts, which is what makes the short-budget facts deterministic.</summary>
        public void Start()
        {
            var hooks = new FrameReader.Hooks
            {
                Established = () => Volatile.Read(ref _established) != 0,
                OnEstablished = () => Volatile.Write(ref _established, 1),
                OnHandshakeTimeout = () => Interlocked.Increment(ref TimeoutCalls),
                OnRead = n => ReadSizes.Add(n),
                OnBufferedAsync = buf =>
                {
                    BufferedCounts.Add(buf.Count);
                    BufferedSnapshot = buf.ToArray();
                    return new ValueTask<bool>(StopAfterBuffered?.Invoke(buf) ?? false);
                },
                AfterRead = () => { AfterReads++; _reads.Release(); },
            };
            var reader = new FrameReader(Stream, 2005, "test-peer", hooks, _handshakeMs);
            _pump = Task.Run(async () =>
            {
                await foreach (var pkt in reader.ReadFramesAsync()) Frames.Add(pkt);
            });
        }

        /// <summary>Wait for one more read to finish end to end (frames delivered, latch set, dumps done).
        /// False on timeout, which is a failed fact, never a hang.</summary>
        public Task<bool> NextReadAsync() => _reads.WaitAsync(TimeSpan.FromSeconds(5));

        public async Task<bool> WaitForTimeoutAsync()
        {
            var deadline = Environment.TickCount64 + 5_000;
            while (Environment.TickCount64 < deadline)
            {
                if (Volatile.Read(ref TimeoutCalls) > 0) return true;
                await Task.Delay(10);
            }
            return false;
        }

        public async ValueTask DisposeAsync()
        {
            Stream.Eof();
            try { await _pump.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { /* the assertion that already failed is the report */ }
        }
    }

    /// <summary>An in-memory stream that returns exactly one pushed chunk per read and blocks until one is
    /// pushed — the two things a socket does that a MemoryStream does not, and the two the framing facts are
    /// about. No sockets: this is framing, not networking.</summary>
    private sealed class ChunkStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private byte[] _pending = Array.Empty<byte>();
        private int _offset;

        public void Push(byte[] data)
        {
            Assert.NotEmpty(data);   // an empty chunk would read as EOF, which is Eof()'s job
            Assert.True(_chunks.Writer.TryWrite(data));
        }

        /// <summary>The peer closed: the next read returns 0.</summary>
        public void Eof() => _chunks.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (_offset >= _pending.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(ct).ConfigureAwait(false)) return 0;
                if (!_chunks.Reader.TryRead(out var next)) continue;
                _pending = next;
                _offset = 0;
            }
            int n = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsSpan(_offset, n).CopyTo(buffer.Span);
            _offset += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
