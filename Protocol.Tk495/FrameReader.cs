using Shared;

namespace Protocol.Tk495;

/// <summary>
/// The ONE inbound read loop for both processes: read a chunk off the stream, append it to a per-connection
/// buffer, and hand back every whole <see cref="TkPacket"/> the buffer now holds. It also owns the handshake
/// watchdog (the slow-loris defense) and the two wire dumps, because those are per-READ events and only the
/// loop knows where a read begins and ends.
///
/// <para>This used to be 26 lines copied into <c>LoginServer/LoginSession.RunAsync</c> and
/// <c>Server/Session.RunAsync</c> — the same watchdog, the same 4KB chunk, the same
/// <see cref="TkPacket.TryParse"/> walk, the same two dumps. The framing of the internet-facing process and
/// the framing of the game process could drift apart with nothing to catch it.</para>
///
/// <para><b>Why an enumerable with hooks and not a bare <c>await foreach</c>.</b> The two processes agree on
/// every FRAME but not on every READ. The game stamps its silence watchdog, sniffs an HTTP status probe and
/// runs its throttled autosave once per read — including reads that complete no frame at all — while the
/// login channel does none of that. Yielding frames alone would silently move those three side effects onto
/// the frame boundary, which is a different cadence. So the frames come back through the enumerable (the
/// common case, where the <c>await foreach</c> body IS each process's per-frame code) and the per-read points
/// are explicit callbacks fired at exactly the offsets they occupy today:
/// <see cref="Hooks.OnRead"/> before the raw dump, <see cref="Hooks.OnBufferedAsync"/> after the append and
/// before framing (and able to stop the loop, which is how the status probe breaks out), and
/// <see cref="Hooks.AfterRead"/> after the unframed dump.</para>
///
/// <para>The handshake latch itself stays a field of each session (<c>_established</c>): the game's status
/// probe reads it from outside this loop, and <c>StatusResponder</c> documents it there. The reader reads and
/// sets it through <see cref="Hooks.Established"/> / <see cref="Hooks.OnEstablished"/>, keeping each
/// session's existing <c>Volatile</c> semantics — a late watchdog fire can never drop a connection that has
/// already spoken.</para>
/// </summary>
public sealed class FrameReader
{
    /// <summary>Chunk pulled off the socket per read. 4KB is what both loops have always used.</summary>
    public const int ReadBufferBytes = 4096;

    /// <summary>The default handshake budget in milliseconds, from <c>P1998_HANDSHAKE_MS</c>.
    ///
    /// <para>Slow-loris defense: a freshly-accepted connection must send its FIRST valid framed packet (0x10
    /// world arrival, or 0x03 re-login on the game channel; any login opcode on the login channel) within
    /// this budget or it is dropped. A client that connects and then holds the socket open sending nothing
    /// costs us a session slot for free otherwise. Only the FIRST packet is gated — once established there is
    /// no read timeout, so an in-world player standing AFK (or an Alt+X connection idling between world-exit
    /// and re-login) is never disconnected. The login port is the internet-facing front door, which is why it
    /// is gated too. Env-tunable and shared by both processes; 15s is far more than a real client needs (it
    /// speaks in milliseconds) yet kills a hold.</para></summary>
    public static int DefaultHandshakeMs { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("P1998_HANDSHAKE_MS"), out var hs) && hs > 0 ? hs : 15_000;

    /// <summary>The few things the two processes do differently around the shared loop. Everything here is
    /// called on the reading thread, in the order the class doc describes.</summary>
    public sealed class Hooks
    {
        /// <summary>Has this connection already produced a valid frame? Read by the handshake watchdog only,
        /// and it must be the session's own latch — the watchdog fires on a timer thread, so this is what
        /// makes "a late fire can never drop an established connection" true.</summary>
        public required Func<bool> Established { get; init; }

        /// <summary>Set the session's latch: the first valid frame of a read has been parsed and the
        /// handshake is satisfied. Called once per read that framed anything, after the frames were
        /// delivered — the point the two loops set it today.</summary>
        public required Action OnEstablished { get; init; }

        /// <summary>Drop the connection: the handshake budget expired with no valid frame. The reader has
        /// already written the log line. The login process closes the socket; the game process routes it
        /// through its own <c>CloseConnection</c> so the outbound writer unwinds too.</summary>
        public required Action OnHandshakeTimeout { get; init; }

        /// <summary>A read returned <c>n &gt; 0</c> bytes. Fires BEFORE the raw wire dump, for every read,
        /// including one that completes no frame. The game stamps its input-silence watchdog here; the login
        /// channel has nothing to do.</summary>
        public Action<int>? OnRead { get; init; }

        /// <summary>The read has been appended to the connection buffer and nothing has been framed yet.
        /// Return <c>true</c> to stop the loop (the enumeration ends, and the caller's <c>finally</c> closes
        /// the connection as it would on any other exit). The game sniffs the HTTP status probe here and
        /// answers it with a direct stream write, which is why this hook is asynchronous. The buffer is the
        /// live one — read it, never mutate it.</summary>
        public Func<List<byte>, ValueTask<bool>>? OnBufferedAsync { get; init; }

        /// <summary>The read is completely done: frames delivered, latch set, unframed tail dumped. Fires for
        /// every read, including one that completed no frame. The game runs its throttled autosave here.
        /// </summary>
        public Action? AfterRead { get; init; }
    }

    private readonly Stream _stream;
    private readonly int _port;
    private readonly string _remote;
    private readonly int _handshakeMs;
    private readonly Hooks _hooks;

    /// <param name="stream">The connection's inbound half.</param>
    /// <param name="port">The listener port this connection arrived on — the raw dump names it.</param>
    /// <param name="remote">The peer, as the log lines should print it.</param>
    /// <param name="hooks">The per-process side effects; see <see cref="Hooks"/>.</param>
    /// <param name="handshakeMs">Handshake budget override, for tests. Null takes
    /// <see cref="DefaultHandshakeMs"/>.</param>
    public FrameReader(Stream stream, int port, string remote, Hooks hooks, int? handshakeMs = null)
    {
        _stream = stream;
        _port = port;
        _remote = remote;
        _hooks = hooks;
        _handshakeMs = handshakeMs ?? DefaultHandshakeMs;
    }

    /// <summary>Read frames until the peer closes (<c>ReadAsync</c> returns 0), a hook asks to stop, or the
    /// stream throws — the caller's <c>catch</c>/<c>finally</c> handles the last of those exactly as it did
    /// when this loop was inline.
    ///
    /// <para>Framing is deliberately unchanged from the two loops it replaces, including the part that is
    /// arguably wrong: a byte that is not <c>0xAA</c> at the head of the buffer stalls framing forever and is
    /// never skipped. Re-syncing the stream is a behaviour change and belongs in its own change.</para>
    /// </summary>
    public async IAsyncEnumerable<TkPacket> ReadFramesAsync()
    {
        // Handshake watchdog: fires once if no valid packet arrives within the budget. Gated on the session's
        // latch so a late fire can never drop a connection that has already spoken; the close hook makes the
        // pending ReadAsync below throw and unwind into the caller's cleanup.
        using var handshake = new CancellationTokenSource(_handshakeMs);
        handshake.Token.Register(() =>
        {
            if (_hooks.Established()) return;
            Log.Warn($"{_remote} handshake timeout ({_handshakeMs}ms) — no valid packet, dropping");
            _hooks.OnHandshakeTimeout();
        });

        var buf = new List<byte>();
        var tmp = new byte[ReadBufferBytes];
        while (true)
        {
            int n = await _stream.ReadAsync(tmp);
            if (n == 0) break;
            _hooks.OnRead?.Invoke(n);
            // Wire dumps are OFF by default on the login channel — those bytes contain the player's
            // password, and 4.95's cipher is a fixed published XOR, so "encrypted" is not a defense.
            // See Log.WireEnabled.
            if (Log.WireEnabled) Log.Info($"   <~ RAW {n}B on :{_port}: {Log.Hex(tmp[..n])}");
            for (int i = 0; i < n; i++) buf.Add(tmp[i]);

            if (_hooks.OnBufferedAsync is { } onBuffered && await onBuffered(buf)) yield break;

            var arr = buf.ToArray();
            int off = 0;
            while (arr.Length - off >= 5 && arr[off] == 0xAA)
            {
                if (!TkPacket.TryParse(arr.AsSpan(off), out var pkt, out int consumed)) break;
                off += consumed;
                yield return pkt;
            }
            if (off > 0)
            {
                buf.RemoveRange(0, off);
                _hooks.OnEstablished();   // first valid frame parsed -> handshake satisfied
            }
            if (buf.Count > 0 && Log.WireEnabled)
                Log.Info($"   (… {buf.Count}B buffered/unframed: {Log.Hex(buf.ToArray())})");

            _hooks.AfterRead?.Invoke();
        }
    }
}
