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

    /// <summary>Fact 3: a buffer whose first byte is not 0xAA yields nothing, keeps every byte, and never
    /// re-syncs — not an endorsement, a PIN. This is what both read loops have always done; changing it is a
    /// behaviour change and belongs in its own PR (see the reader's ReadFramesAsync doc).</summary>
    [Fact]
    public async Task ABufferNotStartingWithAaYieldsNothingKeepsItsBytesAndNeverResyncs()
    {
        var frame = TkPacket.Build(0x10, 0x00, new byte[] { 9 });
        var junkThenFrame = new byte[] { 0x00 }.Concat(frame).ToArray();

        await using var h = new Harness();
        h.Start();

        h.Stream.Push(junkThenFrame);
        Assert.True(await h.NextReadAsync());
        Assert.Empty(h.Frames);
        Assert.Equal(junkThenFrame, h.BufferedSnapshot);   // every byte still there, nothing skipped

        h.Stream.Push(TkPacket.Build(0x11, 0x00, new byte[] { 8 }));
        Assert.True(await h.NextReadAsync());
        Assert.Empty(h.Frames);                            // and the stall is permanent
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
