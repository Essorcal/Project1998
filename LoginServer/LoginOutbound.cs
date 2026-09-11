using System.Net.Sockets;
using System.Threading.Channels;
using Shared;

namespace LoginServer;

/// <summary>
/// The login channel's outbound half: one TCP socket, a small bounded queue every send enqueues onto, and
/// the single writer task that performs the only socket writes for that connection.
///
/// <para>This is the game side's <c>Server.TcpOutbound</c> in miniature, and it exists for the same reason.
/// <c>LoginSession.Send</c> used to be a SYNCHRONOUS <c>_stream.Write</c> under a lock, called from the
/// session's own read loop — so a peer whose TCP receive window was full (slow, or deliberately not reading)
/// blocked that write, and with it the read loop, for as long as the kernel was willing to wait. On the
/// internet-facing front door that is a free way to pin a connection open indefinitely. Now the read loop
/// only does an O(1) <see cref="Send"/> and moves on; a peer that will not drain its socket is dropped by
/// one of the two bounds below instead of being waited on.</para>
///
/// <para>Two bounds, because a login conversation is only a handful of frames and would rarely fill a queue
/// on its own: <see cref="Capacity"/> frames outstanding, and <see cref="WriteTimeoutMs"/> inside a single
/// socket write. The first catches a peer we are talking to quickly, the second catches the ordinary case —
/// one small frame that simply never lands. Either one drops the connection with one Warn line naming the
/// peer.</para>
///
/// <para>The single-reader channel is what replaced the old send lock: one writer task means frames cannot
/// interleave mid-packet on the wire, and FIFO means the bytes and their order are exactly what they were.</para>
///
/// <para>Deliberately NOT the game's type reused. Moving <c>TcpOutbound</c> into <c>Shared</c> so both
/// processes share one outbound is the better end state, but it is a cross-project extraction that touches
/// the game's session, its tests and its drop line; it is not this change.</para>
/// </summary>
public sealed class LoginOutbound
{
    /// <summary>Frames this queue holds before <see cref="Send"/> starts refusing them. A whole login
    /// conversation — welcome, an availability reply, a status line or two, the redirect — is under a dozen
    /// frames, so this is several times the worst legitimate burst and anything that reaches it is a peer
    /// that has stopped reading.</summary>
    public const int Capacity = 64;

    /// <summary>How long one socket write may take before the peer is dropped, in milliseconds.
    /// <c>P1998_LOGIN_WRITE_MS</c> tunes it. The login conversation is a few hundred bytes: a peer that
    /// cannot accept them inside ten seconds is not a client anyone is waiting on, and holding the
    /// connection open for it is the slow-loris the read-side handshake watchdog already refuses.</summary>
    public static readonly int WriteTimeoutMs =
        int.TryParse(Environment.GetEnvironmentVariable("P1998_LOGIN_WRITE_MS"), out var wt) && wt > 0 ? wt : 10_000;

    /// <summary>How long <see cref="CloseAfterDrainAsync"/> waits for the queued frames to reach the socket
    /// before closing anyway. The redirect is the reason this exists: a login that closes before the handoff
    /// packet flushes is a login that never completes, because the client learns the game host and port from
    /// exactly those bytes.</summary>
    public const int DrainTimeoutMs = 1000;

    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(Capacity) { SingleReader = true, SingleWriter = false,
                                              FullMode = BoundedChannelFullMode.Wait });

    private readonly TcpClient _client;
    private readonly int _writeTimeoutMs;
    private readonly TaskCompletionSource _writerFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _socketClosed;

    /// <param name="client">The accepted connection. This type owns every write to it.</param>
    /// <param name="remote">The peer label for log lines, as the session computes it (the player's address
    /// rather than the proxy's when a trusted proxy sits in front).</param>
    /// <param name="writeTimeoutMs">Override for <see cref="WriteTimeoutMs"/>; tests use a short bound so a
    /// stalled peer can be observed in milliseconds rather than seconds.</param>
    public LoginOutbound(TcpClient client, string remote, int? writeTimeoutMs = null)
    {
        _client = client;
        Stream = client.GetStream();
        Remote = remote;
        _writeTimeoutMs = writeTimeoutMs ?? WriteTimeoutMs;
    }

    /// <summary>Peer label for log lines.</summary>
    public string Remote { get; }

    /// <summary>The inbound half. This type covers writing only, so the session's read loop reaches for the
    /// stream directly — it is the same <see cref="NetworkStream"/>, never written to from anywhere else.</summary>
    public NetworkStream Stream { get; }

    /// <summary>Frames handed over but not yet picked up by the writer. Diagnostics and tests only.</summary>
    public int QueueDepth => _queue.Reader.Count;

    /// <summary>Why the peer was dropped, or null if it was not. Set once, by whichever bound tripped; the
    /// same text the Warn line carries.</summary>
    public string? DropReason { get; private set; }

    /// <summary>Hand over one framed packet. Never blocks and never writes to the socket. False means the
    /// queue is full or already closed — the caller answers that by dropping the peer (see
    /// <c>LoginSession.Send</c>), which is the whole point: nobody waits on a peer that will not read.</summary>
    public bool Send(byte[] frame) => _queue.Writer.TryWrite(frame);

    /// <summary>Stop accepting frames and drop the transport now, discarding anything still queued.
    /// Idempotent.</summary>
    public void Close()
    {
        _queue.Writer.TryComplete();
        if (Interlocked.Exchange(ref _socketClosed, 1) != 0) return;
        // EXPECTED: teardown races itself (the read loop's finally, the writer's, and a refused Send all
        // reach here) and the loser finds the socket already closed.
        try { _client.Close(); } catch { /* already closing */ }
    }

    /// <summary>Stop accepting frames, let the writer finish what is queued, then close — bounded by
    /// <see cref="DrainTimeoutMs"/> so a stalled peer cannot hold the connection. This is the ordinary
    /// end-of-session path, and the redirect's last frame is what it is protecting.</summary>
    public async Task CloseAfterDrainAsync()
    {
        _queue.Writer.TryComplete();
        try { await _writerFinished.Task.WaitAsync(TimeSpan.FromMilliseconds(DrainTimeoutMs)); }
        catch (TimeoutException) { /* a stalled peer must not retain the socket */ }
        finally { Close(); }
    }

    /// <summary>Drains the queue and performs the ONLY socket writes for this connection. Runs on its own
    /// task so a blocked write can never stall the session's read loop. Returns when the queue is completed
    /// and empty, when a write exceeds the bound, or when the socket goes away; the connection is closed
    /// either way by the time it does.</summary>
    public async Task RunWriterAsync()
    {
        try
        {
            await foreach (var frame in _queue.Reader.ReadAllAsync())
            {
                // NetworkStream does not reliably honour a cancellation token once a send is in flight, so
                // the bound is enforced by racing the write and then closing the socket underneath it —
                // which is what actually aborts a send that the kernel has parked.
                var write = Stream.WriteAsync(frame).AsTask();
                using var done = new CancellationTokenSource();
                if (await Task.WhenAny(write, Task.Delay(_writeTimeoutMs, done.Token)) != write)
                {
                    // The abandoned write faults as soon as the finally closes the socket; observe it so it
                    // never reaches TaskScheduler.UnobservedTaskException.
                    _ = write.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                    DropReason = $"write stalled past {_writeTimeoutMs}ms";
                    Log.Warn($"{Remote} login write stalled past {_writeTimeoutMs}ms — dropping slow client");
                    return;
                }
                done.Cancel();     // retire the losing delay rather than leaving one timer per frame
                await write;       // surface a write failure to the handlers below
            }
        }
        // Same split as the read loop's: a socket-family exception is the peer going away (expected, no
        // stack); anything else is ours and keeps the stack.
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            Log.Warn($"{Remote} login writer stopped: {e.GetType().Name}: {e.Message}");
        }
        catch (Exception e) { Log.Error($"{Remote} login writer threw — dropping the connection", e); }
        finally
        {
            // Publish completion only after the last write has actually returned, never merely after the
            // channel emptied: an empty queue alone does not prove the bytes left the machine.
            Close();
            _writerFinished.TrySetResult();
        }
    }

    /// <summary>Records why the queue-full bound dropped this peer. The Warn line itself belongs to the
    /// session, which is the half that knows the connection is being torn down.</summary>
    internal void NoteQueueFull(string reason) => DropReason ??= reason;
}
