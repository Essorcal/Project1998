using System.Net.Sockets;
using System.Threading.Channels;

namespace Shared;

/// <summary>
/// The numbers one channel's outbound queue runs with. There is one <see cref="TcpOutbound"/> type for both
/// processes; this is what differs between them, and having the two profiles side by side is the point — the
/// game's and the login's numbers are now readable against each other instead of restating the same design
/// twice in two files.
/// </summary>
/// <param name="Capacity">Frames the queue holds before <c>Send</c> starts refusing them.</param>
/// <param name="WriteTimeoutMs">How long ONE socket write may take before the peer is dropped. 0 disables the
/// bound, which is the game channel: there the queue filling is what drops a stuck peer.</param>
/// <param name="SlowSendMs">Warn when a frame waits this long to reach the socket, or when the socket write
/// itself takes that long. 0 disables the warning.</param>
/// <param name="LogLabel">Prefix the writer's own log lines carry after the peer label, so each process keeps
/// the exact text it printed before ("" for the game, <c>"login "</c> for the login channel).</param>
public sealed record OutboundOptions(int Capacity, int WriteTimeoutMs, int SlowSendMs, string LogLabel)
{
    /// <summary><c>P1998_SLOW_SEND_MS</c> tunes it; 0 disables. 250ms is well under the ~1s a player would
    /// notice, so the log names the stall before anyone complains about it.</summary>
    private static readonly int DefaultSlowSendMs =
        int.TryParse(Environment.GetEnvironmentVariable("P1998_SLOW_SEND_MS"), out var ss) && ss >= 0 ? ss : 250;

    /// <summary><c>P1998_LOGIN_WRITE_MS</c> tunes it. The login conversation is a few hundred bytes: a peer
    /// that cannot accept them inside ten seconds is not a client anyone is waiting on, and holding the
    /// connection open for it is the slow-loris the read-side handshake watchdog already refuses.</summary>
    private static readonly int DefaultLoginWriteMs =
        int.TryParse(Environment.GetEnvironmentVariable("P1998_LOGIN_WRITE_MS"), out var wt) && wt > 0 ? wt : 10_000;

    /// <summary>The game channel: a burst of world-entry packets is well under 2048 frames; a truly stuck
    /// socket hits it and we drop the connection. No per-write bound — the queue is the bound here, and a
    /// player on a bad link is warned about (<c>SLOW SEND</c>) long before anything drops them.</summary>
    public static OutboundOptions Game { get; } = new(2048, 0, DefaultSlowSendMs, "");

    /// <summary>The login channel: a whole conversation — welcome, an availability reply, a status line or
    /// two, the redirect — is under a dozen frames, so 64 is several times the worst legitimate burst and
    /// anything that reaches it is a peer that has stopped reading. The per-write bound is here because that
    /// queue bound alone does not do the job on this channel: a peer that stalls on the very FIRST frame
    /// leaves the queue at depth 1 and the writer parked inside <c>WriteAsync</c> forever.</summary>
    public static OutboundOptions Login { get; } = new(64, DefaultLoginWriteMs, DefaultSlowSendMs, "login ");
}

/// <summary>
/// The production <see cref="IOutbound"/>, and the ONE outbound both processes use: one TCP socket, the
/// bounded queue every <c>Send()</c> enqueues onto, and the single writer task that drains it.
///
/// <para>Outbound decoupling (DDoS / tick-stall defense). This is the fix for the worst availability bug:
/// peer broadcasts and mob AI run on the shared <c>World.TickLoop</c> thread and used to call a SYNCHRONOUS
/// stream write — so one client whose TCP receive buffer was full (slow, or deliberately not reading) would
/// block that write and freeze mob movement/combat for EVERYONE on the map. Now the tick thread only does an
/// O(1) TryWrite and moves on; if a client's queue backs up past <see cref="Capacity"/> it is the SLOW CLIENT
/// that gets dropped, not the world. The single-reader channel also guarantees packets never interleave
/// mid-frame on the wire, which is what the old <c>_sendLock</c> protected.</para>
///
/// <para>The LOGIN channel had the same problem for the same reason and wants the same answer, so it is the
/// same type: <c>LoginSession.Send</c> used to be a synchronous <c>_stream.Write</c> under a lock on the
/// session's own read loop, which on the internet-facing front door is a free way to pin a connection open
/// indefinitely. What differs between the two channels is only numbers, and those live in
/// <see cref="OutboundOptions"/>.</para>
/// </summary>
public sealed class TcpOutbound : IOutbound
{
    /// <summary>A queued frame plus the moment it was handed to the queue. The log line for a packet is
    /// written when it is ENQUEUED, so without this timestamp a multi-second delay between "the server
    /// decided to send this" and "the bytes actually left the machine" is completely invisible — the log
    /// looks perfect while the player is frozen. See <see cref="RunWriterAsync"/>'s slow-send watchdog.</summary>
    private readonly record struct Frame(byte[] Buf, long QueuedAtMs);

    private readonly Channel<Frame> _queue;
    private readonly OutboundOptions _options;

    private readonly TcpClient _client;
    private readonly TaskCompletionSource _writerFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _socketClosed;
    private int _draining;
    internal const int DrainTimeoutMs = 1000;

    /// <param name="client">The accepted connection. This type owns every write to it.</param>
    /// <param name="options">This channel's numbers; <see cref="OutboundOptions.Game"/> when omitted.</param>
    /// <param name="realIp">The client's true address when a trusted proxy sits in front and the listener
    /// has already consumed its PROXY header (see Shared/ProxyProtocol.cs). Null on a direct connection,
    /// where the socket's peer IS the client. Everything downstream — the handoff token binding at
    /// HandoffTokens.Mint/Consume, the ban and moderation surface, and every log line — has to see the
    /// player rather than the proxy, or it is reasoning about one address shared by the whole server.</param>
    /// <param name="remote">The peer label for log lines when the caller has already computed it (the login
    /// session does, for its own throttle). Null derives it here from the socket and
    /// <paramref name="realIp"/>, which is what the game does.</param>
    public TcpOutbound(TcpClient client, OutboundOptions? options = null,
                       System.Net.IPAddress? realIp = null, string? remote = null)
    {
        _client = client;
        _options = options ?? OutboundOptions.Game;
        _queue = Channel.CreateBounded<Frame>(
            new BoundedChannelOptions(_options.Capacity) { SingleReader = true, SingleWriter = false,
                                                           FullMode = BoundedChannelFullMode.Wait });
        Stream = client.GetStream();
        var peer = client.Client.RemoteEndPoint as System.Net.IPEndPoint;
        PeerAddress = peer?.Address ?? System.Net.IPAddress.None;
        // Keep the proxy's own address in the log line: when the allow-list or the HAProxy backend is
        // misconfigured, "which proxy claimed this" is the only thing that distinguishes a real player
        // from a forged header, and it is not recoverable after the fact.
        Remote = remote ?? (realIp is not null ? $"{realIp} (via {peer?.Address})" : peer?.ToString() ?? "?");
        RemoteIp = (realIp ?? peer?.Address ?? System.Net.IPAddress.None).ToString();
    }

    /// <inheritdoc/>
    public string Remote { get; }

    /// <summary>The PLAYER's address, no port — handoff tokens are bound to it.</summary>
    public string RemoteIp { get; }

    /// <summary>The SOCKET's own peer address, i.e. the proxy's when one sits in front.</summary>
    public System.Net.IPAddress PeerAddress { get; }

    /// <summary>The inbound half. <see cref="IOutbound"/> does not cover reading, so the session's read loop
    /// reaches for this directly — it is the one place that knows this session came off a socket.</summary>
    public NetworkStream Stream { get; }

    /// <inheritdoc/>
    public int Capacity => _options.Capacity;

    /// <inheritdoc/>
    public int QueueDepth => _queue.Reader.Count;

    /// <summary>Why the peer was dropped, or null if it was not. Set once, by the per-write bound or by the
    /// session through <see cref="NoteQueueFull"/>; the same text the Warn line carries. Only a channel with
    /// two drop paths needs it (the login's), so that one line per dropped CONNECTION can name the bound that
    /// actually tripped.</summary>
    public string? DropReason { get; private set; }

    /// <inheritdoc/>
    public bool Send(byte[] frame) => _queue.Writer.TryWrite(new Frame(frame, Environment.TickCount64));

    /// <inheritdoc/>
    public void Close()
    {
        _queue.Writer.TryComplete();
        if (Interlocked.Exchange(ref _socketClosed, 1) != 0) return;
        // EXPECTED: teardown races itself (reader, writer and a full-queue Send all reach here) and the loser
        // finds the socket already closed.
        try { _client.Close(); } catch { /* already closing */ }
    }

    /// <summary>Stop accepting frames and close after the writer finishes, or after a bounded deadline.
    /// Completion covers the in-flight write AND writer startup: an empty channel alone cannot prove
    /// delivery. This method never blocks its caller, including a GM command holding the world lock.</summary>
    public void CloseAfterDrain()
    {
        if (Interlocked.Exchange(ref _draining, 1) != 0) return;
        _queue.Writer.TryComplete();
        _ = DrainAndCloseAsync();
    }

    /// <summary>The same drain, awaited. The login session's <c>finally</c> has nothing else to do, so it
    /// waits and prints its <c>-- CLOSE</c> line after the bytes have actually gone; the redirect is the
    /// reason — a login that closes before the handoff packet flushes is a login that never completes,
    /// because the client learns the game host and port from exactly those bytes. Deliberately NOT gated on
    /// <c>_draining</c>: that gate exists so sixteen fire-and-forget closes start one drain, and an awaited
    /// call that returned early because someone else was draining would be a lie about the bytes.</summary>
    public Task CloseAfterDrainAsync()
    {
        _queue.Writer.TryComplete();
        return DrainAndCloseAsync();
    }

    private async Task DrainAndCloseAsync()
    {
        try { await _writerFinished.Task.WaitAsync(TimeSpan.FromMilliseconds(DrainTimeoutMs)); }
        catch (TimeoutException) { /* A stalled peer or an unscheduled writer must not retain the socket. */ }
        finally { Close(); }
    }

    /// <summary>Drains the queue and performs the ONLY socket writes for this connection. Runs on its own
    /// task so a slow/blocked WriteAsync can never stall the World tick thread or the session's read loop.
    /// <paramref name="onExit"/> is the session's teardown, run once the drain ends for any reason; the login
    /// channel passes none, because its own <c>finally</c> is already the teardown.</summary>
    public async Task RunWriterAsync(Action<string>? onExit = null)
    {
        long lastWarnMs = 0;
        int suppressed = 0;
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync())
            {
                long queuedMs = Environment.TickCount64 - item.QueuedAtMs;   // enqueue -> we picked it up
                long w0 = Environment.TickCount64;
                if (_options.WriteTimeoutMs > 0)
                {
                    // NetworkStream does not reliably honour a cancellation token once a send is in flight, so
                    // the bound is enforced by racing the write and then closing the socket underneath it —
                    // which is what actually aborts a send that the kernel has parked.
                    var write = Stream.WriteAsync(item.Buf).AsTask();
                    using var done = new CancellationTokenSource();
                    if (await Task.WhenAny(write, Task.Delay(_options.WriteTimeoutMs, done.Token)) != write)
                    {
                        // The abandoned write faults as soon as the finally closes the socket; observe it so it
                        // never reaches TaskScheduler.UnobservedTaskException.
                        _ = write.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                        DropReason = $"write stalled past {_options.WriteTimeoutMs}ms";
                        Log.Warn($"{Remote} {_options.LogLabel}write stalled past {_options.WriteTimeoutMs}ms"
                                 + " — dropping slow client");
                        return;
                    }
                    done.Cancel();     // retire the losing delay rather than leaving one timer per frame
                    await write;       // surface a write failure to the handlers below
                }
                else
                {
                    await Stream.WriteAsync(item.Buf);
                }
                long writeMs = Environment.TickCount64 - w0;                 // time inside the socket write

                if (_options.SlowSendMs <= 0) continue;
                if (queuedMs < _options.SlowSendMs && writeMs < _options.SlowSendMs) continue;

                // Rate-limited to one line/second per session: a genuinely bad link would otherwise fill the
                // log with thousands of these and bury the first (most useful) one.
                long now = Environment.TickCount64;
                if (now - lastWarnMs < 1000) { suppressed++; continue; }
                lastWarnMs = now;

                // Read this line as: WRITE high -> the kernel send buffer is full, i.e. the client is not
                // ACKing fast enough (packet loss + TCP retransmit backoff, or plain bandwidth). That stall
                // is on the network, not in the server, and no amount of server tuning shortens it.
                // QUEUED high with WRITE low -> we were slow to pick the frame up: this task is a thread-pool
                // work item, so that means pool starvation (cross-check the pool-latency line from Watchdog).
                Log.Warn($"SLOW SEND {Remote}: queued {queuedMs}ms, write {writeMs}ms, " +
                         $"{_queue.Reader.Count} frame(s) still queued" +
                         (suppressed > 0 ? $" (+{suppressed} more suppressed since the last line)" : ""));
                suppressed = 0;
            }
        }
        // Same split as the read loop's: a socket-family exception is the client going away (expected, no
        // stack); anything else is ours and keeps the stack.
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            Log.Warn($"{Remote} {_options.LogLabel}writer stopped: {e.GetType().Name}: {e.Message}");
        }
        catch (Exception e)
        {
            Log.Error($"{Remote} {_options.LogLabel}writer threw — dropping the connection", e);
        }
        finally
        {
            // Close here as well: Session's idempotence gate may already have accepted a draining close.
            // Publish completion only after the last WriteAsync, never merely after dequeueing its frame.
            Close();
            _writerFinished.TrySetResult();
            onExit?.Invoke("writer exit");
        }
    }

    /// <summary>Records why the queue-full bound dropped this peer. The Warn line itself belongs to the
    /// session, which is the half that knows the connection is being torn down.</summary>
    public void NoteQueueFull(string reason) => DropReason ??= reason;
}
