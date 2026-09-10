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

    /// <summary>The most unframed bytes one connection may hold before it is dropped: one maximum legal
    /// frame, 65,538 bytes.
    ///
    /// <para><b>Where the number comes from.</b> 4.95 framing is <c>AA | length(u16 BE) | opcode | increment |
    /// body</c> and "Total bytes on the wire = <c>3 + length</c>" (<c>docs/4.x/Protocol.md</c> §2, and
    /// <see cref="TkPacket.TryParse"/> computes exactly that), so with a u16 length the largest frame this
    /// reader can ever consume is <c>3 + 0xFFFF</c>. A buffer holding MORE than one maximum frame and framing
    /// nothing is holding something the reader will never consume, however many more bytes arrive.</para>
    ///
    /// <para><b>Why not tighter.</b> There is no citable smaller maximum. A client's board post and its native
    /// nmail carry <c>bodyLen</c>/<c>msgLen</c> as <b>u16 BE</b> (<c>Protocol.md</c> §11h, <c>0x3B</c> sub-4
    /// and sub-6), so the wire format lets a legitimate client frame run to the u16 limit; every smaller number
    /// would be a guess about packets we have not implemented, and a wrong guess drops a legal one. Not
    /// env-tunable for the same reason: this is a fact about the length field, not a policy an operator could
    /// set correctly.</para>
    ///
    /// <para><b>Where it is checked, and what it can catch.</b> After framing, so that a read which completes
    /// frames shrinks the buffer first and no completable frame is ever refused. Checking the appended buffer
    /// BEFORE framing was rejected: <see cref="TkPacket.TryParse"/> takes any frame once
    /// <c>buf.Count &gt;= 3 + length</c>, so a pre-framing buffer over this bound ALWAYS holds a complete legal
    /// frame, and an early check could only ever refuse one.</para>
    ///
    /// <para>The consequence, stated plainly: while the head-byte rule below stands, this cap cannot fire. The
    /// framing loop stops only with fewer than five bytes left, or on a 0xAA head whose length is not yet
    /// satisfied — necessarily fewer than <c>3 + 0xFFFF</c> bytes, or <see cref="TkPacket.Parse"/> would have
    /// taken it — or on a non-0xAA head, which the head-byte rule drops, or on a length field under
    /// <see cref="TkPacket.MinLength"/>, which the malformed rule drops on the very read that first exposes
    /// five bytes of it. So the rules bound the unframed buffer at this many bytes
    /// (plus at most one <see cref="ReadBufferBytes"/> chunk in flight), which is the guarantee, and the
    /// head-byte rule is what delivers it today. The cap states the bound in one place and is what still
    /// enforces it if that rule is ever relaxed — a re-syncing reader, say — for the cost of one comparison per
    /// read.</para>
    /// </summary>
    public const int MaxUnframedBytes = 3 + 0xFFFF;

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
        /// live one — read it, never mutate it.
        /// <para>This runs BEFORE all three drop rules (<see cref="MaxUnframedBytes"/>, the non-0xAA head
        /// byte and a length field under <see cref="TkPacket.MinLength"/>), which is what keeps the status
        /// probe working: "GET " is a non-0xAA head, so a probe the hook did not answer first would be
        /// dropped as an unframed stream.</para></summary>
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

    /// <summary>Read frames until the peer closes (<c>ReadAsync</c> returns 0), a hook asks to stop, the peer
    /// stops framing (below), or the stream throws — the caller's <c>catch</c>/<c>finally</c> handles the last
    /// of those exactly as it did when this loop was inline.
    ///
    /// <para><b>Three bounds on a peer that is not framing.</b> All three end the enumeration with one
    /// <c>Log.Warn</c> line naming the peer, and each process's existing <c>finally</c> closes the
    /// socket — the same exit the status probe already takes. Both are checked AFTER
    /// <see cref="Hooks.OnBufferedAsync"/> so that hook still wins (see its doc).</para>
    /// <list type="number">
    ///   <item>The unframed buffer may not exceed <see cref="MaxUnframedBytes"/>. Before the handshake the
    ///     watchdog bounds the TIME but not the BYTES; after it there is no read timeout at all (an AFK player
    ///     is never disconnected), so before this pair a connection that stopped framing grew a
    ///     <c>List&lt;byte&gt;</c> for as long as it held the socket open. Read
    ///     <see cref="MaxUnframedBytes"/> for which of the two rules actually delivers that bound.</item>
    ///   <item>A head byte that is not <c>0xAA</c> after framing drops the connection. It used to stall the
    ///     inbound half forever — the framing loop only advances while <c>arr[off] == 0xAA</c>, so one stray
    ///     byte meant nothing was ever framed again and nothing noticed except, before the handshake, the
    ///     watchdog. Frames already parsed out of that same read are yielded first and are never lost.</item>
    ///   <item>A <c>0xAA</c> head whose length field is under <see cref="TkPacket.MinLength"/> drops the
    ///     connection. <see cref="TkPacket.Parse"/> calls it <see cref="TkPacket.FrameStatus.Malformed"/>
    ///     rather than "wait for more", because <c>3 + length</c> is satisfied by the five bytes already
    ///     here and no further byte can change the answer. It used to throw
    ///     <see cref="ArgumentOutOfRangeException"/> straight out of this loop — a negative slice length —
    ///     which crossed <c>MoveNextAsync</c> into each session's catch and cost the game one stackful
    ///     Error line per connection. Frames parsed ahead of it in the same read are yielded first, as in
    ///     the rule above.</item>
    /// </list>
    ///
    /// <para><b>Why drop and not re-sync.</b> Scanning forward to the next <c>0xAA</c> needs a stream-level
    /// anchor to tell a real header from a coincidence, and 4.95 has none: the framing is
    /// <c>AA | length(u16 BE) | opcode | increment | body</c> with "no trailer and no checksum"
    /// (<c>docs/4.x/Protocol.md</c> §2, <see cref="TkPacket"/>), and bodies are XOR-ciphered, so a body byte
    /// is <c>0xAA</c> about once every 256 bytes. A scan would lock onto a false header with a random u16
    /// length and then either stall or misparse — silently, which is the failure mode this codebase is least
    /// able to see. Dropping is loud and costs a real client nothing: every client frame starts with
    /// <c>0xAA</c> (§2, "while at least 5 bytes and <c>buf[0]==0xAA</c>, read length, consume <c>3+length</c>,
    /// repeat"), and <c>Protocol.md</c> documents no case in which a 4.95 client legitimately sends a leading
    /// byte that is not <c>0xAA</c>.</para>
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
            int malformedLength = -1;
            while (arr.Length - off >= 5 && arr[off] == 0xAA)
            {
                var status = TkPacket.Parse(arr.AsSpan(off), out var pkt, out int consumed, out int length);
                if (status == TkPacket.FrameStatus.Malformed) { malformedLength = length; break; }
                if (status != TkPacket.FrameStatus.Frame) break;
                off += consumed;
                yield return pkt;
            }
            if (off > 0)
            {
                buf.RemoveRange(0, off);
                _hooks.OnEstablished();   // first valid frame parsed -> handshake satisfied
            }
            // Bound 1: more unframed bytes than one maximum legal frame. Every byte here has already been
            // offered to the framing loop, so nothing at this head is a frame more bytes could complete. It
            // is the stated cap and the layer that keeps the buffer bounded if the head-byte rule below is
            // ever relaxed; while that rule stands this cannot fire (see MaxUnframedBytes).
            if (buf.Count > MaxUnframedBytes)
            {
                Log.Warn($"{_remote} {buf.Count}B unframed, over the {MaxUnframedBytes}B cap — dropping");
                yield break;
            }

            // Bound 2: a head byte that is not 0xAA. Whatever the peer is speaking, it is not this protocol,
            // and framing can never advance past this byte. A 0xAA head with fewer than five bytes, or with a
            // length not yet satisfied, is an ordinary partial frame and waits exactly as it always has.
            if (buf.Count > 0 && buf[0] != 0xAA)
            {
                Log.Warn($"{_remote} head byte 0x{buf[0]:x2} is not 0xAA — stream not framed, dropping");
                yield break;
            }

            // Bound 3: a 0xAA head whose length field is under TkPacket.MinLength. The head IS 0xAA, so the
            // rule above can never reach this and the framing loop can never advance past it: 3 + length is
            // already satisfied by five bytes, so no further byte changes the answer. It used to be an
            // ArgumentOutOfRangeException out of this loop (a negative slice length) and one stackful Error
            // per connection in the game's catch; it is a drop like the other two now.
            if (malformedLength >= 0)
            {
                Log.Warn($"{_remote} frame length {malformedLength} under the {TkPacket.MinLength}-byte " +
                         "minimum — malformed, dropping");
                yield break;
            }

            if (buf.Count > 0 && Log.WireEnabled)
                Log.Info($"   (… {buf.Count}B buffered/unframed: {Log.Hex(buf.ToArray())})");

            _hooks.AfterRead?.Invoke();
        }
    }
}
