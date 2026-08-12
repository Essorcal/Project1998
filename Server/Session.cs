using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Protocol.Tk495;
using Shared;

namespace Server;

/// <summary>
/// One client connection. Frames incoming packets, decrypts, dispatches, and replies.
/// This is the disposable 4.95 adapter behavior; the reusable world logic will live elsewhere.
/// </summary>
public sealed partial class Session
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly int _port;
    private readonly string _remote;
    private readonly string _remoteIp;   // address only (no port) — handoff tokens are bound to it
    private readonly CharacterStore _store;
    private readonly World _world;   // the shared world (players + mobs); every broadcast goes through it
    private string _user = "?";
    private bool _enteredWorld;   // true once world entry loaded _char; gates the disconnect save

    // Party/trade (§11 party+trade): transient, session-owned, never persisted — matches RTK, where both
    // "groups" and "exchange" live only in the in-memory USER struct, not the DB.
    private Party? _party;
    private Trade? _trade;

    // Outbound decoupling (DDoS / tick-stall defense). Every Send() ENQUEUES onto this bounded channel;
    // a single dedicated writer task (WriterLoop) drains it and does the actual blocking socket write.
    // This is the fix for the worst availability bug: peer broadcasts and mob AI run on the shared
    // World.TickLoop thread and used to call a SYNCHRONOUS _stream.Write here — so one client whose TCP
    // receive buffer was full (slow, or deliberately not reading) would block that write and freeze mob
    // movement/combat for EVERYONE on the map. Now the tick thread only does an O(1) TryWrite and moves on;
    // if a client's queue backs up past OutboundCapacity it is the SLOW CLIENT that gets dropped, not the
    // world. The single-reader channel also guarantees packets never interleave mid-frame on the wire, which
    // is what the old _sendLock protected.
    private const int OutboundCapacity = 2048;   // ~a burst of world-entry packets is well under this; a
                                                 // truly stuck socket hits it and we drop the connection.

    /// <summary>A queued frame plus the moment it was handed to the queue. The log line for a packet is
    /// written when it is ENQUEUED, so without this timestamp a multi-second delay between "the server
    /// decided to send this" and "the bytes actually left the machine" is completely invisible — the log
    /// looks perfect while the player is frozen. See WriterLoop's slow-send watchdog.</summary>
    private readonly record struct Outbound(byte[] Buf, long QueuedAtMs);

    private readonly Channel<Outbound> _outbound = Channel.CreateBounded<Outbound>(
        new BoundedChannelOptions(OutboundCapacity) { SingleReader = true, SingleWriter = false,
                                                      FullMode = BoundedChannelFullMode.Wait });

    /// <summary>Warn when a frame waits this long (ms) to reach the socket, or when the socket write itself
    /// blocks that long. <c>NEXUS_SLOW_SEND_MS</c> tunes it; 0 disables. 250ms is well under the ~1s a player
    /// would notice, so the log names the stall before anyone complains about it.</summary>
    private static readonly int SlowSendMs =
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_SLOW_SEND_MS"), out var ss) && ss >= 0 ? ss : 250;
    private int _closed;   // 0 until the connection is being torn down; set once (Interlocked) — idempotent close

    // Slow-loris defense: a freshly-accepted connection must send its FIRST valid framed packet (0x10 world
    // arrival, or 0x03 re-login) within this budget or it is dropped. A client that connects and then holds
    // the socket open sending nothing costs us a session slot for free otherwise. Only the FIRST packet is
    // gated — once established there is no read timeout, so an in-world player standing AFK (or an Alt+X
    // connection idling between world-exit and re-login) is never disconnected. Env-tunable; 15s is far more
    // than a real client needs (it speaks in milliseconds) yet kills a hold.
    private static readonly int HandshakeMs =
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_HANDSHAKE_MS"), out var hs) && hs > 0 ? hs : 15_000;
    private int _established;   // 0 until the first valid packet is parsed; gates the handshake timeout

    // --- robust persistence (dirty-flag autosave, see MarkDirty/FlushNow) ---
    // Bounds worst-case data loss on a hard server-process crash (OOM / kill / power loss, none of which
    // can run the graceful-shutdown flush hook) to roughly AutoSaveMs, without rewriting the whole
    // multi-KB character blob on every single mutation. A mutation site calls MarkDirty(); the session's
    // own read-loop thread flushes it (FlushIfDue, race-free — mutations and the flush both run on this
    // same thread) at most once per AutoSaveMs, and World's periodic sweep (AutoSaveLoop) catches an IDLE
    // dirty player (one who mutated state, then stopped sending packets) on the same cadence.
    // Internal (not private) so World.AutoSaveLoop ticks on the exact same cadence as this session's own
    // FlushIfDue, instead of duplicating the env-var parsing and risking the two drifting apart.
    internal static readonly int AutoSaveMs =
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_AUTOSAVE_MS"), out var asv) && asv > 0 ? asv : 15_000;
    private volatile bool _dirty;
    private long _lastSaveAtMs;

    // ---- input-silence diagnostics (see Watchdog.ScanSessions) --------------------------------------
    // The reported symptom is "mobs keep moving but my character can't move or act": the world is fine and
    // we are still sending to this client, but it has stopped sending US anything. Neither side of that is
    // visible in the packet log — an idle player and a wedged one look identical — so record the last time
    // each direction carried traffic and let the watchdog spot the asymmetry.
    private long _lastInboundMs = Environment.TickCount64;
    private long _lastOutboundMs = Environment.TickCount64;
    internal long LastInboundMs  => Volatile.Read(ref _lastInboundMs);
    internal long LastOutboundMs => Volatile.Read(ref _lastOutboundMs);
    internal byte LastInboundOp;    // opcode of the last packet the client sent us
    internal byte LastOutboundOp;   // opcode of the last frame we queued for it
    internal string Remote => _remote;

    /// <summary>One-line dump of everything that could plausibly be gating this client's input, for the
    /// silence watchdog. Deliberately reads only cheap fields — it runs off the watchdog thread.</summary>
    internal string DiagState()
    {
        long now = Environment.TickCount64;
        return $"last-in 0x{LastInboundOp:x2} {now - LastInboundMs}ms ago, " +
               $"last-out 0x{LastOutboundOp:x2} {now - LastOutboundMs}ms ago, " +
               $"outq {_outbound.Reader.Count}, pos ({_char.X},{_char.Y}) map {_char.Map}, " +
               $"action-budget {_actionCount}/{ActionBudget} (window {_actionWindow}), " +
               // A non-null _dlgReply means we are awaiting a 0x3A and the client is sitting in a MODAL
               // dialog — which is itself a state where it will not send walks. Prime suspect for a
               // "can't move but the world keeps going" report, so it is called out explicitly.
               $"queued-casts {_queuedCasts.Count}, awaiting-dialog-reply {(_dlgReply is not null ? "YES" : "no")}, " +
               $"trade {(_trade is not null ? "OPEN" : "none")}, dirty {_dirty}";
    }
    // Set once this session has been superseded by a newer login for the same account (duplicate-login
    // guard, see World.RegisterOnline/Session.KickForReplacement). Gates the read-loop's disconnect save
    // so a slow-to-unwind OLD session can never clobber the NEW session's fresher state.
    private int _replaced;
    // Serializes concurrent Save attempts for THIS session (the on-thread FlushIfDue vs. World's
    // background sweep vs. a duplicate-login kick) — never held across a mutation, so it can't deadlock
    // or stall the read loop.
    private readonly object _saveGate = new();

    // --- client version, tagged by the port the connection arrived on (unified dual-client server) ---
    // 4.95 speaks the original protocol (local map files; incoming 0x06 = walk-sync). 5.33 streams
    // terrain from the server (incoming 0x05/0x06 = map-data requests -> reply 0x06). Rather than sniff
    // the wire we give 5.33 its own listener ports and stamp the version here; the proven 4.95 path is
    // never entered by a 5.33 session. Login 2000 / game 2005 = V495; login 2001 / game 2006 = V533.
    public enum ClientVersion { V495, V533 }
    private readonly ClientVersion _ver;
    private bool IsLoginPort => _port == 2000 || _port == 2001;

    // --- world-light diagnostic knobs (env-tweakable, no rebuild needed to sweep) ---
    //   NEXUS_LIGHT      integer 0..65535, the map light/darkness value (default 232, proven bright on 4.95)
    //   NEXUS_LIGHT_FMT  how to encode it on the 0x15: "beu16" (default, 4.95), "leu16", or "u8"
    // 5.33 draws terrain black with the 4.95-proven be-u16 232; sweeping these isolates whether the
    // client reads the light field at a different width/endianness (leading 00 -> light 0 -> black).
    private static readonly int LightValue =
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_LIGHT"), out var lv) ? lv : 232;
    private static readonly string LightFmt =
        (Environment.GetEnvironmentVariable("NEXUS_LIGHT_FMT") ?? "beu16").Trim().ToLowerInvariant();

    // 5.33 terrain-stream tile offset. The 4.x .map stores raw frame indices (the 4.95 client draws
    // them as-is); some client eras expect index+1 (0 = "no tile", client subtracts 1). Mithia 7.x
    // streams raw, so default 0 — but if 5.33 terrain comes out shifted by one frame, set NEXUS_TILE_OFF=1
    // (or -1) to correct it without a rebuild. Applied to the ground + object shorts, not passability.
    private static readonly int CellOff =
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_TILE_OFF"), out var co) ? co : 0;

    // 5.33 terrain-render diagnostic for the 0x06 stream (set via a .bat so PowerShell's `set` quirk
    // can't bite). "" = real map tiles. "sweep" = ramp the ground index across the whole visible rect
    // over the FULL 16-bit range (0..28550) so we can read off which indices actually draw a tile.
    // "solid:N" = fill with ground index N. Sweep/solid send the tile UNMASKED (real tiles are still
    // masked to 14 bits since 4.x ground packs passability in the top 2 bits).
    private static readonly string MapDiag =
        (Environment.GetEnvironmentVariable("NEXUS_MAP_DIAG") ?? "").Trim().ToLowerInvariant();

    // Server-side passability (collision). NEXUS_PASS=0 disables it (walk through anything) if the 4.x
    // top-2-bits polarity turns out wrong for a given map. Default on. Mithia 7.x: read_pass!=0 => blocked.
    private static readonly bool PassEnforce =
        (Environment.GetEnvironmentVariable("NEXUS_PASS") ?? "1").Trim() != "0";
    // Parsed block value for the passtest:N diagnostic (default 1); shared by the map stream + collision.
    private static readonly int PassTestN =
        MapDiag.StartsWith("passtest:") && int.TryParse(MapDiag.AsSpan(9), out var ptn) ? ptn : 1;

    // 4.95 self-walk: 0x0C starts a LOCAL-PREDICTION animation, entirely self-timed client-side (confirmed
    // live via Frida: the walk-frame counter at entity+0x18e advances on its own, ~90ms/tick, with ZERO
    // packets from us in between). But the client caps local prediction at exactly 2 ticks (~180ms) and
    // then FREEZES — it will not advance further until our 0x04 arrives, at which point it snaps straight
    // to completion. So 0x04 must land just AFTER that natural ~180ms window: too early truncates the
    // prediction (looks like an instant snap); too late just prolongs the freeze before the snap.
    // NEXUS_V495_WALK_MS tunes it (0 = old same-frame slide, sent before ANY tick can play).
    private static readonly int V495WalkMs =
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_V495_WALK_MS"), out var wm) ? wm : 200;

    // Self-walk drive mode for 4.95, chosen by static RE of the client's animation path.
    //   start-walk @0x462320  sets walk-active [+0x18c]=1 and registers the anim timer (0x41b5d0)
    //                         UNCONDITIONALLY, then repositions logical=dest ONLY if current!=dest.
    //   walk-render @0x44b140 draws screen = logical + forward_step*(frameCtr/4)  [dir->delta @0x44aad0].
    //   0x04 handler @0x44faf0 -> @0x44c660 sets decaying scroll offsets [+0xb8]=-12/[+0xbc]=-10 = a
    //                         SMOOTH camera scroll, independent of 0x0C.
    // So the OVERSHOOT (sprite lands on dest then slides past) comes only from the reposition, which is
    // gated on current!=dest. The LEG cycle comes from walk-active + anim timer, set before that gate.
    // Modes (NEXUS_V495_SELF_MOVE):
    //   0 = 0x04 only            -> smooth camera scroll, but NO legs (walk-active never set)
    //   1 = 0x0C(dest)+delay+0x04-> legacy; legs but forward overshoot + snap on the final step
    //   2 = 0x0C(SOURCE)+0x04    -> legs on (walk-active set) with reposition SKIPPED (dest==current);
    //                              still cancelled by 0x04's move-commit, so no visible legs.
    //   3 = send nothing         -> pure local walk; 1 PERFECT animated step, then the client blocks
    //                              awaiting a server ack (proven live: local controller does move+legs+camera).
    //   5 = delay + 0x04         -> DEFAULT: send nothing immediately so the LOCAL controller animates the
    //                              full step (legs + slide + camera), then after the anim completes send 0x04
    //                              to unblock the next step. By then move-commit's unregister is a no-op, so
    //                              the legs are NOT cancelled. NEXUS_V495_ACK_MS tunes the delay.
    //   7 = nothing on a good walk (RTK-faithful) -> DEFAULT: client moves/animates/scrolls locally; 0x04
    //       is sent ONLY as a correction (desync/block). Stops our per-step 0x04 from re-scrolling the
    //       camera the client already moved (the residual "wonkiness" + fighting realm-center).
    private static readonly int V495SelfMove =
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_V495_SELF_MOVE"), out var sm) ? sm : 7;

    // Delay (ms) before the mode-5 unblock 0x04. Must be >= the client's local walk animation (~4 frames,
    // ~360ms) so the 0x04 lands AFTER the legs finish and doesn't cancel them. Too short => truncated legs;
    // too long => sluggish walk cadence (the client gates the next step on this ack).
    private static readonly int V495AckMs =
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_V495_ACK_MS"), out var am) ? am : 360;

    // FAST-MOVE OFF (server-authoritative) response strategy for 4.95. When fast-move is off the client
    // makes NO local prediction — it sends the walk request and waits for the server to assign the step
    // before moving/animating. The old behavior (0x04 only) slides with no legs because 0x04's handler
    // (0x44faf0 -> move-commit) teleports the sprite and never runs the leg cycle. But 0x0C runs the SAME
    // start-walk (0x462320) a PEER uses: it sets walk-active(+0x18c)=1, frameCtr(+0x18e)=0, REGISTERS the
    // anim list (0x41b5d0), moves logical->dest, and sets up the source->dest screen interpolation
    // (0x44b090) — a full animated step with legs. And 0x0C does NOT branch self-vs-peer, so the self
    // animates exactly like the peers we already drive smoothly. Strategies:
    // The render (0x44b140) draws  screen = screen(entity.logical) + FORWARD_unit*(frameCtr/4)  where
    // FORWARD_unit is the dir delta (0x44aad0) and start-walk (0x462320) sets logical=DEST at frame 0 —
    // BUT ONLY IF the 0x0C tile != current logical (the reposition guard). So the tile we put in the 0x0C
    // decides the anchor:  0x0C(DEST) -> logical jumps to dest, sprite starts ON dest and drifts to +2
    // (OVERSHOOT); 0x0C(SOURCE) -> guard trips, logical stays at source, sprite animates source->dest
    // correctly, and the delayed 0x04(dest) commits the landing. Strategies:
    //   0 = 0x04 only            -> legacy: assigns tile + camera, but SLIDES (no legs).
    //   1 = 0x0C(dest) only      -> legs but OVERSHOOTS to +2 then needs a commit to snap back.
    //   2 = 0x0C(SOURCE)+delayed 0x04(dest) -> DEFAULT: source-anchored legs (no overshoot); the delayed
    //                               0x04 commits logical=dest AFTER the legs finish so it can't cancel them.
    //   3 = 0x0C(dest)+delayed 0x04 -> the overshoot variant (kept to compare against 2).
    //   4 = 0x0C(SOURCE) only    -> source-anchored legs but no commit -> may snap back to source.
    //   5 = 0x26 self-walk (DEFAULT) -> the real smooth primitive: routes to handlerB (0x4903d0) which
    //       move-commits the step + starts the next locally ([+0x65f3]=1, no wait, no 0x04, no forced
    //       scroll). Same packet 5.33 uses; respects realm-center. See HandleWalk for the full RE trail.
    private static readonly int V495SlowMove =
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_V495_SLOW_MOVE"), out var slm) ? slm : 5;

    // "Realm center" (F4 in RTK) — a CLIENT camera mode signalled by a flag byte in the 0x15 mapinfo
    // packet (RTK clif_sendmapinfo byte +12; our SendMapInfo body[7]). When ON the client locks the camera
    // dead-center on the character (the world scrolls under a fixed sprite); when OFF the camera uses the
    // edge-aware/offset behavior that can lag during a walk. The 4.95 client honors it: its 0x15 handler
    // (@0x44f8b0) reads this byte, computes (realm==0), and feeds it to the view/camera rebuild (@0x44c570).
    // NEXUS_V495_REALM=1 enables it to test whether a locked camera fixes the walk "wonkiness".
    private static readonly byte RealmCenter =
        Environment.GetEnvironmentVariable("NEXUS_V495_REALM") == "1" ? (byte)1 : (byte)0;

    // AA 00 13 7E 1B "CONNECTED SERVER\n"  (plaintext welcome, as the 6.x reference sends)
    private static readonly byte[] Welcome =
        BuildWelcome();

    private static byte[] BuildWelcome()
    {
        var head = new byte[] { 0xAA, 0x00, 0x13, 0x7E, 0x1B };
        var text = "CONNECTED SERVER\n"u8.ToArray();
        var all = new byte[head.Length + text.Length];
        head.CopyTo(all, 0);
        text.CopyTo(all, head.Length);
        return all;
    }

    /// <param name="realIp">The client's true address when a trusted proxy sits in front and the listener
    /// has already consumed its PROXY header (see Shared/ProxyProtocol.cs). Null on a direct connection,
    /// where the socket's peer IS the client. Everything downstream — the handoff token binding at
    /// HandoffTokens.Mint/Consume, the ban and moderation surface, and every log line — has to see the
    /// player rather than the proxy, or it is reasoning about one address shared by the whole server.</param>
    public Session(TcpClient client, int port, CharacterStore store, World world,
                   System.Net.IPAddress? realIp = null)
    {
        _client = client;
        _stream = client.GetStream();
        _port = port;
        _store = store;
        _world = world;
        _ver = (port == 2001 || port == 2006) ? ClientVersion.V533 : ClientVersion.V495;
        var peer = client.Client.RemoteEndPoint as System.Net.IPEndPoint;
        // Keep the proxy's own address in the log line: when the allow-list or the HAProxy backend is
        // misconfigured, "which proxy claimed this" is the only thing that distinguishes a real player
        // from a forged header, and it is not recoverable after the fact.
        _remote = realIp is not null ? $"{realIp} (via {peer?.Address})" : peer?.ToString() ?? "?";
        _remoteIp = (realIp ?? peer?.Address ?? System.Net.IPAddress.None).ToString();
    }

    public async Task RunAsync()
    {
        Log.Info($"++ CONNECT from {_remote} on port {_port} [{_ver}]");
        // Start the dedicated outbound writer BEFORE any Send() so the very first packet (the welcome) is
        // enqueued and flushed in order. All socket writes happen on this one task; the read loop below never
        // writes to the stream directly.
        var writer = Task.Run(WriterLoop);
        try
        {
            if (IsLoginPort)   // login channel: send the 0x7E welcome
            {
                Send(Welcome);
                Log.Info($"   -> sent welcome ({Welcome.Length}B)");
            }
            else                 // game channel: the client speaks first (sends 0x10). Send NOTHING now.
            {
                // Reversing NexusTK.exe shows the game socket's receive path is identical to login's
                // (mode 0x3aa4c=5 -> recv loop 0x477fc0 -> decrypt 0x478680 -> WndProc queue). There is
                // no server greeting on the game port and no seed/ack handshake. Sending unsolicited
                // packets before the client's 0x10 only risks desyncing its frame assembler, so we wait.
                Log.Info("   == game connect: waiting for client 0x10 arrival (no pre-arrival sends) ==");
            }

            // Handshake watchdog: fires once if no valid packet arrives within HandshakeMs. Gated on
            // _established so a late fire can never drop a connection that has already spoken; closing the
            // socket makes the pending ReadAsync below throw and unwind into the finally cleanup.
            using var handshake = new CancellationTokenSource(HandshakeMs);
            handshake.Token.Register(() =>
            {
                if (Volatile.Read(ref _established) != 0) return;
                Log.Info($"!! {_remote} handshake timeout ({HandshakeMs}ms) — no valid packet, dropping");
                CloseConnection("handshake timeout");
            });

            var buf = new List<byte>();
            var tmp = new byte[4096];
            while (true)
            {
                int n = await _stream.ReadAsync(tmp);
                if (n == 0) break;
                Volatile.Write(ref _lastInboundMs, Environment.TickCount64);   // silence watchdog
                if (Log.WireEnabled) Log.Info($"   <~ RAW {n}B on :{_port}: {Log.Hex(tmp[..n])}");
                for (int i = 0; i < n; i++) buf.Add(tmp[i]);

                var arr = buf.ToArray();
                int off = 0;
                while (arr.Length - off >= 5 && arr[off] == 0xAA)
                {
                    if (!TkPacket.TryParse(arr.AsSpan(off), out var pkt, out int consumed)) break;
                    off += consumed;
                    LastInboundOp = pkt.Opcode;
                    Handle(pkt);
                }
                if (off > 0)
                {
                    buf.RemoveRange(0, off);
                    Volatile.Write(ref _established, 1);   // first valid frame parsed -> handshake satisfied
                }
                if (buf.Count > 0 && Log.WireEnabled)
                    Log.Info($"   (… {buf.Count}B buffered/unframed: {Log.Hex(buf.ToArray())})");

                FlushIfDue();   // throttled autosave; no-op unless MarkDirty()'d and AutoSaveMs has elapsed
            }
        }
        catch (Exception e) { Log.Info($"!! {_remote} error: {e.Message}"); }
        finally
        {
            // Drop out of any live party/trade so the other side(s) aren't left waiting on someone who's
            // gone (RTK: a dropped exchange partner's session simply vanishes from map_id2sd, which is
            // exactly what a disconnect does here too — the difference is we also say so in chat).
            if (_trade is not null) EndTrade(_trade, "Exchange cancelled.");
            if (_party is not null) RemoveFromParty(this);

            // Leave the shared world: despawn us for the other players on our map. World mobs persist
            // (they belong to the map, not this session), so they keep wandering for whoever remains.
            if (_enteredWorld) _world.LeaveMap(this, _char.Map);
            if (_enteredWorld) _world.Unregister(UserKey, this);
            // Persist the last state (position/stats) only for a session that actually entered the world
            // AND wasn't superseded by a newer login for the same account (KickForReplacement already
            // flushed the freshest state; saving again here from this now-stale session would clobber it —
            // see the duplicate-login guard, World.RegisterOnline). The login-channel session never
            // populates _char, so saving it would clobber the real record with defaults.
            if (_enteredWorld && Volatile.Read(ref _replaced) == 0)
            {
                _dirty = true;
                FlushNow();
                Log.Info($"   -> persisted '{_char.Name}' at map {_char.Map} ({_char.X},{_char.Y})");
            }
            CloseConnection("read-loop exit");   // completes the outbound channel + closes the socket
            try { await writer; } catch { /* writer logs its own errors */ }
            Log.Info($"-- CLOSE {_remote}");
        }
    }

    // Drains the outbound queue and performs the ONLY socket writes for this session. Runs on its own task
    // so a slow/blocked WriteAsync can never stall the World tick thread or this session's read loop.
    private async Task WriterLoop()
    {
        long lastWarnMs = 0;
        int suppressed = 0;
        try
        {
            await foreach (var item in _outbound.Reader.ReadAllAsync())
            {
                long queuedMs = Environment.TickCount64 - item.QueuedAtMs;   // enqueue -> we picked it up
                long w0 = Environment.TickCount64;
                await _stream.WriteAsync(item.Buf);
                long writeMs = Environment.TickCount64 - w0;                 // time inside the socket write

                if (SlowSendMs <= 0) continue;
                if (queuedMs < SlowSendMs && writeMs < SlowSendMs) continue;

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
                Log.Info($"!! SLOW SEND {_remote}: queued {queuedMs}ms, write {writeMs}ms, " +
                         $"{_outbound.Reader.Count} frame(s) still queued" +
                         (suppressed > 0 ? $" (+{suppressed} more suppressed since the last line)" : ""));
                suppressed = 0;
            }
        }
        catch (Exception e) { Log.Info($"!! {_remote} writer stopped: {e.Message}"); }
        finally { CloseConnection("writer exit"); }   // e.g. client closed the socket -> unblock the reader
    }

    // Idempotent teardown: completes the outbound channel (so WriterLoop finishes after draining) and closes
    // the socket (which unblocks the read loop's ReadAsync). Safe to call from the reader, the writer, or a
    // Send() that found the queue full — whichever gets here first wins; the rest are no-ops.
    private void CloseConnection(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _outbound.Writer.TryComplete();
        try { _client.Close(); } catch { /* already closing */ }
        Log.Info($"   -> connection teardown ({reason})");
    }

    // ---- RTK's global action budget (clif_parse gates + pc_timer) ------------------------------------
    // NOTHING to do with aethers. RTK counts *every* action packet into one shared per-second budget,
    // `sd->time`, which pc_timer zeroes on a fixed 1000ms tick (pc.c:613). Each gated opcode does
    // `sd->time += 1; if (sd->time < 4) ...`, so the 1st/2nd/3rd action in a window run and the 4th onward
    // are DROPPED SILENTLY — no minitext, no reply, no log to the player. This is what actually stops spell
    // spam for the ~87% of spells whose script never calls setAether (114 of 394 RTK spell scripts do).
    //
    // The budget is SHARED across opcodes, and attack (0x13) increments it WITHOUT being gated by it (0x13
    // has its own attack_speed timer instead) — so swinging at full speed really does eat your own cast
    // allowance. Surprising, but it's RTK's, so it's kept.
    //
    // FIXED window, not sliding: RTK resets to 0 on a wall-clock tick, so 3 casts at the end of one second
    // plus 3 at the start of the next (6 back-to-back) is legal. TickCount64/1000 reproduces that exactly
    // without needing a per-session timer; the only drift is that RTK's window is anchored to the player's
    // login and ours to server boot, which nothing can observe.
    private const int ActionBudget = 4;   // RTK's `if (sd->time < 4)` -> 3 actions per window
    private long _actionWindow;           // which 1s window _actionCount belongs to
    private int  _actionCount;            // RTK sd->time

    private void RollActionWindow()
    {
        long window = Environment.TickCount64 / 1000;
        if (window != _actionWindow) { _actionWindow = window; _actionCount = 0; }
    }

    // RTK `sd->time += 1` — the raw increment, for 0x13 which pays into the budget but isn't gated by it.
    private int BumpActionTime() { RollActionWindow(); return ++_actionCount; }

    // RTK `sd->time += 1; if (sd->time < 4)` — the usual gated form.
    private bool ActionAllowed(byte op)
    {
        if (BumpActionTime() < ActionBudget) return true;
        Log.Info($"   -- op=0x{op:x2} dropped: action budget spent ({_actionCount} this second)");
        return false;
    }

    // RTK `if (sd->time < 4)` with NO increment — unequip (0x1F) only.
    private bool ActionBudgetLeft() { RollActionWindow(); return _actionCount < ActionBudget; }

    // ---- Queue over-budget casts instead of discarding them (NEXUS_CAST_QUEUE=0 to disable) ------------
    // Holding a cast key makes the client send 0x0F every ~31ms (OS auto-repeat, after a ~260ms initial
    // delay — both measured live). With a plain drop-gate that yields three casts spaced 31ms apart, then
    // ~940ms of silence, and 31ms of separation on three identical sounds is an audible flam. Real NexusTK
    // is ONE animation and ONE sound with three casts landing together, which is what you get if the
    // over-budget casts are HELD and released at the next window boundary instead of thrown away.
    //
    // INFERRED, NOT SOURCED. RTK discards (clif_parsemagic just falls through) and there is no 4.95 source
    // for this either way — it is here because it reproduces the real game's behavior by ear, which is the
    // only authority available. Treat it as a working reconstruction: if a real 4.95 source ever contradicts
    // it, the source wins. Depth is capped at the budget and keeps the NEWEST casts, so the queue means
    // "the key is still down", never a 30-deep backlog that keeps firing after you let go.
    private static readonly bool CastQueueEnabled = Environment.GetEnvironmentVariable("NEXUS_CAST_QUEUE") != "0";
    // How long a queued cast stays valid. Depth-capping bounds how MANY casts wait; this bounds how LONG,
    // which is the part that matters once the key comes up: the client stops sending, so nothing triggers a
    // drain, and the next packet of ANY kind (a walk step, seconds later) would otherwise fire casts from
    // the last time you held the key. While the key really is held the client repeats every ~31ms, so the
    // newest 3 are at most ~125ms old when the drain runs; 250ms clears that with room for the ~46ms jitter
    // seen live, while staying far below the ~1s that would let a stale cast leak somewhere visible.
    private const int CastQueueMaxAgeMs = 250;
    private readonly Queue<(byte[] Body, long Tick)> _queuedCasts = new();

    private void QueueCast(byte[] dec)
    {
        while (_queuedCasts.Count >= ActionBudget - 1) _queuedCasts.Dequeue();   // keep the newest only
        _queuedCasts.Enqueue((dec, Environment.TickCount64));
    }

    // Drained at the top of every inbound packet: the client is repeating at ~31ms while the key is held, so
    // this fires within ~31ms of the boundary without needing a timer. Uses the same bump-then-test as the
    // live gate, so a released cast costs budget exactly like a live one and the net rate stays 3/second —
    // the queue only changes WHEN they land (together), not HOW MANY.
    private void DrainQueuedCasts()
    {
        if (_queuedCasts.Count == 0) return;

        // Expire stale entries BEFORE the spent-window early-return, or a queue left over from a released
        // key would sit untouched until something else happened to drain it. FIFO with monotonic stamps, so
        // the head is always the oldest.
        long now = Environment.TickCount64;
        while (_queuedCasts.Count > 0 && now - _queuedCasts.Peek().Tick > CastQueueMaxAgeMs)
        {
            _queuedCasts.Dequeue();
            Log.Info("   -- queued cast expired (cast key released)");
        }
        if (_queuedCasts.Count == 0) return;

        RollActionWindow();
        if (_actionCount >= ActionBudget) return;               // still inside the spent window
        while (_queuedCasts.Count > 0 && BumpActionTime() < ActionBudget)
        {
            Log.Info("   -- queued cast released at window start");
            HandleCast(_queuedCasts.Dequeue().Body);
        }
    }

    private void Handle(TkPacket pkt)
    {
        var dec = TkCrypt.Crypt(pkt.Body, pkt.Increment, TkCrypt.LoginKey);
        if (Log.WireEnabled)
        {
            Log.Info($"   <- pkt op=0x{pkt.Opcode:x2} inc=0x{pkt.Increment:x2} len={pkt.Body.Length + 2} body={pkt.Body.Length}B");
            Log.Info($"        dec : {Log.Hex(dec)}");
        }

        StartEntryMusicIfArmed(pkt.Opcode);   // login music waits for proof the client's world object is live

        switch (pkt.Opcode)
        {
            case Opcode.Arrival:          HandleArrival(pkt); break;
            // NameCheck (0x02) and CreateAppearance (0x04) are account-creation opcodes handled by the
            // separate LoginServer process and never arrive on the game port. Login (0x03) DOES arrive here:
            // when the client exits to the select screen (Alt+X) it re-sends 0x03 on the still-open game
            // connection, so the game server must answer it (re-auth + hand back) like the old unified
            // server did — otherwise re-login hangs. See HandleReLogin.
            case Opcode.Login:            HandleReLogin(dec); break;
            case 0x32:                    HandleWalk(dec); break;   // client walk step -> confirm move
            // 0x11 = "side" (turn to face a direction, NO movement) for BOTH clients. The 4.95 client's
            // 0x11 recv handler (@0x450350) reads id(u32)@+1, side(u8)@+5, looks the entity up and calls
            // its turn method (@0x462410) -- exactly SendSide's layout. Previously dropped for 4.95, which
            // left facing unconfirmed until the next walk ("press a new direction, first step goes the OLD
            // way"). In NexusTK the first press in a new direction turns in place; only the second walks.
            case 0x11:
                HandleTurn(dec);
                break;
            // 0x1b = client setting toggle. body[0] = which setting (RTK settings-parse cases):
            //   0x07 = Realm center (F4)   0x09 = Fast move   ... others not yet handled.
            case 0x1b:
                HandleSetting(dec);
                break;
            // Map/walk split — the SAME for both clients (4.95 corrected 2026-08-07):
            //   0x05 = map-data request (view rect) -> stream terrain back as 0x06 (HandleMapRequest).
            //   0x06 = walk (dir @ body[0], + reported pos/viewport) -> confirm move.
            // This block used to read "4.95 differs: 0x05 unused" and dropped every 4.95 0x05 with a
            // "no V495 handler" log line. That was wrong: 4.95 sends the IDENTICAL request, 2161 of them
            // in one session log, body =
            //     x0(u16BE) y0(u16BE) w(u8) h(u8) 00 checksum(u16BE) 00
            //     00 00 00 00 0c 0c 00 63 c2 00   -> (0,0)     12x12
            //     00 6d 00 7e 13 11 00 d1 1c 00   -> (109,126) 19x17   (the 17x15 viewport + pad)
            // — which is exactly what HandleMapRequest already parses. The 4.95 client streams its terrain
            // from the server like every later client; the local .map files are a CACHE it verifies (hence
            // the checksum, which the walk 0x06 also carries), not the only source. That's why some 4.x
            // client distributions ship no Maps directory at all.
            case 0x05:
                HandleMapRequest(dec);
                break;
            case 0x06:
                HandleWalk(dec);
                break;
            // 0x38 = hard refresh (Ctrl+R): the client grays the screen and asks the server to re-assert
            // authoritative state. RTK's clif_refresh replies with sendmapinfo + sendxy + re-drawn entities
            // (0x04 is the re-anchor primitive here — authoritative position + recentered camera). See §refresh.
            case 0x38:
                HandleRefresh(dec);
                break;
            case 0x0E:                    HandleChat(dec); break;   // client chat -> echo as over-head speech
            // 0x1d = emotion request (the ':' emote wheel). body[0] = emote index; the client plays action
            // type = index + 11 (RTK clif_parseemotion: sendaction(index+11)). Broadcast as a 0x1A action.
            case 0x1D:                    if (ActionAllowed(0x1D)) HandleEmotion(dec); break;
            // 0x13 pays into the shared action budget but is NOT gated by it (RTK clif.c:11446) — melee has
            // its own attack_speed timer, and HandleAttack applies our equivalent swing pacing.
            case 0x13:                    BumpActionTime(); HandleAttack(dec); break;  // client attack (spacebar) -> echo 0x13 anim
            case 0x2D:                    HandleProfileRequest(dec); break;  // profile key -> self-profile (0x39)
            case 0x43:                    HandleClickInfo(dec); break;       // click entity -> profile / NPC dialog
            // 0x3A = NPC dialog response (RTK clif_parsenpcdialog): the client sends this after the player
            // acts on a dialog we opened via 0x30. body[0] = kind (01 text next/close, 02 menu pick, 04 input
            // text). See HandleNpcDialog — a logging stub until the 0x30 send format is confirmed live.
            case 0x3A:                    HandleNpcDialog(dec); break;
            case 0x4F:                    HandleChangeProfile(dec); break;   // edit profile -> save pic + blurb
            // ---- items (opcode numbers from RTK 7.x recv dispatch; confirmed to align with 4.95 by the
            // walk/turn/chat/attack/setting opcodes already matching). See §11c. ----
            case 0x07:                    if (ActionAllowed(0x07)) HandlePickup(dec); break;    // pick up the floor item under me
            case 0x08:                    HandleDropItem(dec); break;  // drop a bag slot to the floor
            case 0x17:                    HandleThrow(dec); break;     // throw a bag slot (flies ahead)
            case 0x1A:                    HandleUseItem(dec, eat: true); break;   // eat/consume a slot
            // 0x12 = the WIELD hotkey (press 'w', then the item's letter). Body = [slot(1-based), 00] — the
            // same shape as 0x1C, confirmed by live capture (wield sent `12 01 00`). Double-click already used
            // 0x1C; the hotkey just uses a different opcode, so route it to the same use/equip path.
            case 0x12:                    HandleUseItem(dec, eat: false); break;  // wield hotkey -> equip a slot
            case 0x1C:                    HandleUseItem(dec, eat: false); break;  // use/equip a slot
            // RTK checks the budget here WITHOUT incrementing (clif.c:11514) — unequip is free but blocked
            // once the second's allowance is already gone. 0x12/0x1C (wield/use) are ungated in RTK too.
            case 0x1F:                    if (ActionBudgetLeft()) HandleUnequip(dec); break;   // remove a worn item back to the bag
            case 0x24:                    HandleDropGold(dec); break;  // drop a gold amount
            // 0x20 = the 'o' / Open key (RTK clif_parse case 0x20 "Clicked 'O'" -> clif_cancelafk + clif_open_sub
            // -> onOpen script). A deliberate action (RTK's handler clears AFK, so NOT a heartbeat): in NexusTK it
            // toggles the faced door object's open/closed graphic in place. See HandleOpen (swaps the object tile
            // via the 0x06 cell-patch and broadcasts it to the map).
            case 0x20:                    HandleOpen(dec); break;
            // 0x0F = cast a learned spell (RTK clif_parsemagic): body[0]=book slot+1, then per spell type
            // 1 -> typed answer string, type 2 -> target entity id (u32BE), type 5 -> nothing. See HandleCast.
            case 0x0F:
                if (ActionAllowed(0x0F)) HandleCast(dec);
                else if (CastQueueEnabled) QueueCast(dec);
                break;
            // 0x66 = right-click "examine item" on a bag slot. Answered with a 0x66 reply that the client's
            // handler 0x4511b0 renders as the item-detail popup (stats + wear requirements). Both directions
            // are RE'd from the 4.95 binary — see HandleItemInfoRequest / SendItemInfo for the wire formats
            // and the builder/handler addresses. Leaving it unanswered is what made the client retry ~6×.
            // body[0] splits the two: 0 = examine (`00 cursorX 00 01 01 SLOT 01 00 00 00`), 1 = "send me
            // the town/nation table" (the fixed `01 00 01 01 00 01 01 00` the client emits from 0x449ed0
            // when its own table is empty, right before the 0x18 user-list request). See Session.UserList.
            case 0x66:
                if (dec.Length > 0 && dec[0] == 1) HandleTownListRequest(dec);
                else HandleItemInfoRequest(dec);
                break;
            // 0x09 = the ';' Look key (RTK clif_parselookat_2). No coordinates in the body — it always
            // inspects the tile immediately in front of us (facing direction). See HandleLookAt.
            case 0x09:                    HandleLookAt(dec); break;
            // 0x19 = whisper (Shift+' , type a name, Enter, type the message, Enter). LIVE-confirmed
            // 2026-07-26: body = dstlen(u8) dst_name[dstlen] msglen(u8) msg[msglen] 00 — exactly RTK
            // clif_parsewisp's wire layout (clif.c:7644). See HandleWhisperPacket.
            case 0x19:                    HandleWhisperPacket(dec); break;
            // 0x3B = the 'b' key (Board). LIVE-confirmed 2026-07-26: body `01 00` = sub-command 1
            // ("Show Board"). Matches RTK's clif_parse dispatch exactly (clif.c:11613: `case 0x3B:
            // clif_handle_boards(sd);`). See HandleBoard.
            case 0x3B:                    HandleBoard(dec); break;
            // 0x41 = the mail-arrow widget's PARCEL-bag click (empty body). RE'd 2026-07-28: the widget's
            // parcel branch (0x469760) stages the single byte 0x41 and sends it. RTK maps it to
            // clif_parseparcel (clif.c:15508) = a minitext pointing at the messenger — and that's exactly what
            // a parcel needs (collect it from a MessengerNpc, see MessengerAbility), so we mirror it verbatim.
            case 0x41:                    SendMiniText("You should go see your kingdom's messenger to collect this parcel."); break;
            // 0x2E = RTK's party-invite opcode (clif_addgroup: body = nameLen(u8) name[nameLen], same shape
            // as 0x19 whisper above). Unlike the items/0x0F/whisper opcodes this one has never been seen in
            // a live 4.95 capture, so it's wired defensively (bad/garbage bytes just fail the name lookup —
            // no risky reply is ever sent back). "@party <name>" is the confirmed-safe primary entry point.
            case 0x2E:                    HandlePartyInvite(dec); break;
            // 0x4A = RTK's exchange sub-protocol (clif_parse_exchange). Only sub-type 0 ("initiate exchange
            // with this target id") is handled — the click that opens a trade from another player's profile
            // window (§11l); RTK's other sub-types (1-5) belong to its real trade WINDOW, which this server
            // doesn't render (dialogs drive the rest instead). Like 0x2E, this is a real opcode 4.95 has
            // been seen sending before, not a speculative wiring.
            case 0x4A:                    HandleExchangeRequest(dec); break;
            // 0x3F = world-map click / ESC reply (§11m). LIVE-CONFIRMED 2026-07-26: body =
            // mapId(u32BE) x(u16BE) y(u16BE) 00 -- RTK's case 0x3F map-change. See HandleWorldMapSelect.
            case 0x3F:                    HandleWorldMapSelect(dec); break;
            // 0x18 = "send me the user list" (empty body; client 0x490e00 stages the lone byte 0x18 and
            // sends length 1). Reached from modifier+'W' (0x48e5cf) and menu-action 3 (table 0x430914),
            // both via 0x48e3d0. RTK dispatches the same opcode to clif_user_list. See Session.UserList.
            case 0x18:                    HandleUserListRequest(); break;
            // 0x39 in = the answer from any 0x2f merchant window (RTK case 0x39 -> clif_handle_menuinput),
            // tagged by the byte the server put at body[1]. Unrelated to 0x39 OUT, which is the self-profile.
            case 0x39:                    HandleShopReply(dec); break;
            // Dump the BODY, not just the opcode. An unhandled opcode is nearly always one we're mid-way
            // through decoding, and its bytes are the whole point — without them every probe needs Frida
            // running on the client just to read what the client already told us.
            default:
                Log.Info($"   ?? no handler for opcode 0x{pkt.Opcode:x2} {dec.Length}B: " +
                         Convert.ToHexString(dec).ToLowerInvariant());
                break;
        }

        // AFTER the switch, deliberately. Draining first let queued casts claim a fresh window's budget
        // before an attack packet in that same window ever bumped it — and since 0x13 pays into the budget
        // without being gated by it, casting and swinging stopped competing at all. They are supposed to:
        // RTK spends ONE shared counter on both (`sd->time`), so a held attack key starves casting outright.
        // Draining here means this packet's own action is charged first and the queue only gets what's left.
        DrainQueuedCasts();   // no-op unless a held cast key left casts waiting on a window roll
    }

    // ---- game server ----
    // NOTE: account creation (name-check 0x02, appearance 0x04) lives in the separate LoginServer process.
    // The game server handles world-entry arrival (0x10) and RE-LOGIN (0x03, below); shared
    // appearance/placement helpers moved to Shared/CharacterFactory so both processes decode the same way.

    // Re-login on the game port. When the client exits to the select screen (Alt+X) it does NOT drop the
    // game connection — it re-sends the login packet (0x03) on it and waits for a handoff redirect, exactly
    // as it would on the login channel. The old single-process server answered 0x03 on every port; after
    // the split the game server must still answer it or the client hangs. Re-authenticate (shared rule)
    // and hand the client back to THIS game server with a fresh single-use token.
    private void HandleReLogin(byte[] dec)
    {
        int ulen = dec[0];
        var user = Encoding.ASCII.GetString(dec, 1, ulen);
        var pass = LoginAuth.ReadPassword(dec, 1 + ulen);
        // Same per-IP failed-attempt budget the login channel enforces — the re-login path accepts the very
        // same credentials, so leaving it ungated would just move the brute-force target to port 2005.
        var ip = (_client.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address ?? System.Net.IPAddress.None;
        if (LoginThrottle.IsBlocked(ip))
        {
            Log.Info($"   -> RE-LOGIN BLOCKED (failed-attempt budget exhausted) for user='{user}'");
            SendMessage(LoginThrottle.BlockedMessage);
            return;
        }

        if (Moderation.IsIpBanned(ip.ToString(), out var ipReason))
        {
            Log.Info($"   -> RE-LOGIN REJECTED (ip banned) for user='{user}'");
            SendMessage(string.IsNullOrWhiteSpace(ipReason)
                ? "This address is banned from the server."
                : $"This address is banned from the server: {ipReason}");
            return;
        }

        var auth = LoginAuth.Authenticate(user, pass);
        if (auth != LoginResult.Ok)
        {
            // See the matching note in LoginSession: a ban is not a failed credential, so it must not eat
            // the per-IP failed-attempt budget.
            if (auth == LoginResult.Banned)
            {
                Log.Info($"   -> RE-LOGIN REJECTED (banned) for user='{user}'");
                SendMessage(LoginAuth.BanMessageFor(user));
                return;
            }
            int left = LoginThrottle.RecordFailure(ip);
            Log.Info($"   -> RE-LOGIN REJECTED ({auth}) for user='{user}' ({left} attempt(s) left)");
            SendMessage(LoginAuth.MessageFor(auth));
            return;   // no handoff; the client stays on the login screen
        }
        LoginThrottle.RecordSuccess(ip);

        var nonce = HandoffTokens.Mint(user, _remoteIp);
        var host = ParseGameHost();
        int gport = _port;   // redirect back to this same game port (2005 V495 / 2006 V533)
        var p = new List<byte>
        {
            0xAA, 0, 0, Opcode.Login,
            host[3], host[2], host[1], host[0],
            (byte)(gport >> 8), (byte)(gport & 0xFF),
            23, 0, 9
        };
        p.AddRange(TkCrypt.LoginKey);
        var uname = Encoding.ASCII.GetBytes(user);
        p.Add((byte)uname.Length);
        p.AddRange(uname);
        p.AddRange(nonce);   // 5-byte single-use token; validated on the next 0x10 arrival
        p[2] = (byte)(p.Count - 3);
        Send(p.ToArray());
        Log.Info($"   -> RE-LOGIN ok for '{user}' — handoff back to {host[0]}.{host[1]}.{host[2]}.{host[3]}:{gport} (token minted)");
    }

    // Game host the re-login handoff redirects to (must match how the client reached this game server).
    // Defaults to loopback; set NEXUS_GAME_HOST for a split-box deployment (same var the login server uses).
    private static byte[] ParseGameHost()
    {
        var def = new byte[] { 127, 0, 0, 1 };
        var h = Environment.GetEnvironmentVariable("NEXUS_GAME_HOST");
        if (string.IsNullOrWhiteSpace(h)) return def;
        var parts = h.Split('.');
        if (parts.Length != 4) return def;
        var o = new byte[4];
        for (int i = 0; i < 4; i++) if (!byte.TryParse(parts[i], out o[i])) return def;
        return o;
    }

    private void HandleArrival(TkPacket pkt)
    {
        // plaintext body: <klen> "NexonInc." <ulen> "<user>" <token>
        var body = pkt.Body;
        byte[] token = Array.Empty<byte>();
        try
        {
            int klen = body[0];
            int ulen = body[1 + klen];
            _user = Encoding.ASCII.GetString(body, 2 + klen, ulen);
            int tokenStart = 2 + klen + ulen;
            // What's left of the handoff nonce. NOT always 5 bytes: the client copies <ulen><user><nonce>
            // into one fixed 13-byte NUL-terminated field, so a longer username eats into the nonce (see
            // Shared/HandoffTokens.SurvivingBytes). Take whatever is here and let Consume decide how much
            // of it must match.
            if (tokenStart < body.Length) token = body[tokenStart..];
        }
        catch { /* keep default */ }

        // Validate the single-use handoff token the login server minted for this username (see
        // Shared/HandoffTokens). This is what stops a client from connecting straight to the game port and
        // claiming ANY username — identity now rests on a login-verified secret, not the client's claim.
        // Safety valve: NEXUS_ENFORCE_HANDOFF=0 downgrades a failure to a warning (fallback only if a
        // deployment hits a token problem); the default is to enforce.
        if (!HandoffTokens.Consume(token, _user, _remoteIp))
        {
            bool enforce = (Environment.GetEnvironmentVariable("NEXUS_ENFORCE_HANDOFF") ?? "1").Trim() != "0";
            if (enforce)
            {
                Log.Info($"   -> ARRIVAL REJECTED: invalid/expired handoff token for user='{_user}' from {_remoteIp} " +
                         $"(token {Log.Hex(token)}, {HandoffTokens.SurvivingBytes(_user)} byte(s) expected) — closing connection");
                _client.Close();
                return;
            }
            Log.Info($"   -> ARRIVAL WARN: invalid handoff token for user='{_user}' — allowed (NEXUS_ENFORCE_HANDOFF=0)");
        }

        // Ban check AGAIN, here at the actual door to the world. The login channel already refused a banned
        // account, but a handoff token minted moments BEFORE the ban was placed is still valid until it
        // expires, and NEXUS_ENFORCE_HANDOFF=0 skips the token check entirely. This is the authoritative
        // gate: nothing enters the world without passing it.
        if (Moderation.IsBanned(_user, out _, out _))
        {
            Log.Info($"   -> ARRIVAL REJECTED: account '{_user}' is banned — closing connection");
            SendMessage(LoginAuth.BanMessageFor(_user));
            _client.Close();
            return;
        }

        // Duplicate-login guard: if this account is already connected (stale client + a fresh reconnect
        // after a network blip, or someone else with the password), force the OLD session out and flush it
        // FIRST — otherwise its eventual disconnect save could clobber THIS session with stale data, since
        // CharacterStore.Save is a blind last-write-wins upsert. Must run BEFORE _store.Load below so the
        // kicked session's flush (if any) is visible to our own load.
        _world.RegisterOnline(CharacterStore.Key(_user), this, out var oldSession);
        if (oldSession is not null)
        {
            Log.Info($"   -> ARRIVAL: '{_user}' already online — kicking previous session");
            oldSession.KickForReplacement();
        }

        // Load the persisted character (created on the login channel, or saved at last logout). There is NO
        // fallback spawn any more: world entry never invents a character. A missing record here means the
        // login server minted a token for a name that has no character (it checks first, so this is either a
        // race with an admin deleting the record, or a hand-rolled client with NEXUS_ENFORCE_HANDOFF=0).
        // Creating one on the spot is what let anyone materialize a character by typing a new name at login.
        var loaded = _store.Load(_user);
        if (loaded is null)
        {
            Log.Info($"   -> ARRIVAL REJECTED: no character record for user='{_user}' — closing connection");
            _world.Unregister(CharacterStore.Key(_user), this);   // give back the online slot we just claimed
            _client.Close();
            return;
        }
        _char = loaded;
        // Keep the CASING the character was created with — _user is whatever the player typed at the login
        // prompt, and logins are case-insensitive, so assigning it here would rewrite "Snuggle" to "snuggle"
        // (and then broadcast that to every peer) on the first lowercase login.
        if (string.IsNullOrEmpty(_char.Name)) _char.Name = _user;
        CharacterFactory.ApplyAppearance(_char);   // re-derive appearance (incl. nation/totem) for records saved before this existed
        RestoreTimedEffects();                     // buffs/curses/stances/morph/stealth that were still running at logout
        LoadModerationState();                     // mute deadline onto the session, so the chat path needs no DB read
        _char.Ac = (sbyte)Math.Clamp(100 - _char.Level, -128, 127);   // naked base AC = 100-level; recompute on load so records saved under the old decrement/gate logic self-correct
        _enteredWorld = true;
        // Assign a UNIQUE world entity id (the old default was 1 for everyone, which made every player
        // collide on the shared-world broadcast key). This id binds the client's camera (0x05/SendId) and
        // is how peers address this player's move/speech/despawn packets. It is a runtime handle, not a
        // persistent key, so we overwrite whatever was loaded and never save it back meaningfully.
        _char.Id = _world.AllocatePlayerId();
        Log.Info($"   -> ARRIVAL user='{_user}' — loaded character '{_char.Name}' at map {_char.Map} ({_char.X},{_char.Y}) (entity id {_char.Id})");

        // *** THE MISSING TRIGGER (found by reversing NexusTK.exe) ***
        // After 0x10 the client is on the loading screen; its game-WORLD object doesn't exist yet,
        // so the world dispatcher (handles opcodes 0x03-0x68) never runs and every world packet is
        // dropped. Handler 0x444de0 shows the client only builds that world object when it receives
        // opcode 0x02 whose first payload byte is 0x00. The 6.x/7.x reference servers never send this,
        // which is why every prior attempt sat silent. Send it FIRST.
        SendMap(0x02, _gameInc++, new byte[] { 0x00 }, "ENTER-WORLD (0x02.00)");

        // Now the world object exists. Replicate the PROVEN 6.x entry order (Replay6x): the map
        // alone loads (confirmed by Frida: CreateFileW("Maps\TK32.map") ok) but the client stays
        // black and won't move because it was never told its OWN entity id. 0x05 supplies that.
        //   0x1E ack, 0x20 time  -> handshake acks (harmless, part of the working sequence)
        //   0x05 = YOUR entity id (binds camera/input to the self player)  <-- the missing piece
        //   0x15 = enter-map (loads Maps\TK<mapId>.map), 0x04 = coords, 0x33 = our appearance
        SendMap(0x1E, _gameInc++, new byte[] { 0x06, 0x00, 0x00 }, "ack(0x1E)");
        { var (h, y) = _world.Time; SendTime(h, y); }
        SendId();
        SendMapInfo(_char.Map, _char.MapXs, _char.MapYs, "Nexus", 232, _gameInc++);
        Log.Info("   -> mapinfo(0x15)");
        SendXy();
        SendSelfLook();
        PrimeViewport("login");   // 0x06 fill the window now — don't wait on the client's own 0x05
        SendStats();
        ArmEntryMusic();           // 0x19 music: ARMED here, sent on the client's first packet — see Handle()
        SendWeather(_world.GetWeather(_char.Map));   // 0x1F: whatever this map's weather already is
        SendSound(412, _char.Id);  // "successfully logging in" sfx, confirmed live 2026-07-27

        Log.Info("   == entry sent: 0x02 trigger + 0x1E/0x20 acks + 0x05 id + 0x15 map + 0x04 xy + 0x33 self + 0x08 stats + 412 login sfx (music armed) ==");

        // Join the shared world: register on this map, draw everyone/everything already here for us, and
        // let EnterMap broadcast US to them. From now on peers see our moves/speech and we see theirs.
        var (peers, mobs) = _world.EnterMap(this, _char.Map);
        foreach (var p in peers) ShowPlayer(p);   // existing players -> draw on our client (0x33)
        SyncMobs(mobs);                            // shared mobs in view -> draw on our client (0x07, streamed)
        foreach (var gi in _world.ItemsOn(_char.Map)) ShowGroundItem(gi);  // floor items (0x16)
        RefreshInventory();                       // fill the bag + equipment windows (0x0F / 0x37)
        RefreshSpells();                          // fill the spell/skill book (0x17) with learned spells
        // No login-time "you have mail" line: the HUD already says it. Unread mail raises the 0x08 mail-arrow
        // flag (SendStats, flags2 bit 0x10) and a waiting parcel lights the bottom-left bag icon — both are
        // standing indicators, so a minitext on top of them is a duplicate the player didn't ask for.
        Log.Info($"   == world join: map {_char.Map} has {peers.Length} other player(s), {mobs.Length} mob(s) ==");
    }

    // ---- terrain streaming (opcode 0x06) — BOTH clients ----
    // The client asks for the tiles in its viewport with a view-rect request (0x05), and we reply with an
    // 0x06 cell block. Request layout is identical on 4.95 and 5.33 (confirmed from the 5.33 binary handler
    // sub_469060, the Mithia 7.x reference clif_parsemap/clif_sendmapdata, AND a live 4.95 capture):
    //   request body : x0(BE u16) y0(BE u16) w(u8) h(u8) [00 checksum(BE u16) 00 on 4.95]
    // The REPLY differs in cell width, because the two clients pack passability differently:
    //   5.33 : x0 y0 w h | { tile(BE) pass(BE) obj(BE) } * w*h        -- 3 shorts, pass in its own short
    //   4.95 : x0 y0 w h | { ground(BE) object(BE) }    * w*h        -- 2 shorts, pass in ground's top 2 bits
    // 4.95's form is the same cell shape SendObjRow already sends for doors (recv handler 0x44fb90, which
    // writes each cell into the client's LIVE map array and then redraws the patched rect — so the write
    // itself is not viewport-gated). No leading flag byte on either: the 5.33 handler reads x0 immediately
    // after op+inc (a spurious 0x00 shifts every field by one, w reads as 0, and you get a black void).
    // Mithia 7.x's clif_sendmapdata DOES emit a leading 0 here; both of these clients differ.
    private void HandleMapRequest(byte[] dec)
    {
        if (dec.Length < 6) { Log.Info($"   ?? map-req too short ({dec.Length}B)"); return; }
        // The 4.95 client fires two (0,0) 12x12 requests at connect, BEFORE 0x15 enter-map — at that point
        // _char has no map or dims, so MapData.For would build a 0-cell map and we'd answer with an empty
        // rect. Ignore anything that arrives before the world join; the client re-asks once it's in.
        if (!_enteredWorld || _char.MapXs == 0 || _char.MapYs == 0) return;
        _stepsSinceMapReq = 0;   // the client is keeping itself fed; hold the push off (see StreamViewport)
        SendMapRect((dec[0] << 8) | dec[1], (dec[2] << 8) | dec[3], dec[4], dec[5], "req");
    }

    /// <summary>Emit one 0x06 cell block for a rectangle, clamped to the map. Shared by the client's own
    /// 0x05 request and by the walk-driven viewport stream (<see cref="StreamViewport"/>).</summary>
    private void SendMapRect(int x0, int y0, int reqW, int reqH, string why)
    {
        var map = MapData.For(_char.Map, _char.MapXs, _char.MapYs);
        if (map is null) { Log.Info($"   ?? map-req for map {_char.Map}: no server-side tile data"); return; }

        // Clamp the rect to the map so the header w/h EXACTLY match the emitted cell count — the client
        // reads w*h cells sequentially, so a mismatch desyncs its stream.
        if (x0 < 0) x0 = 0;
        if (y0 < 0) y0 = 0;
        int w = Math.Clamp(reqW, 0, Math.Max(0, map.Xs - x0));
        int h = Math.Clamp(reqH, 0, Math.Max(0, map.Ys - y0));
        if (w <= 0 || h <= 0) return;

        // NO leading flag byte: the 5.33 client (handler sub_469060) reads x0 immediately after op+inc
        // — calibrated via the 0x15 handler and confirmed by the client-side Frida probe (a spurious
        // leading 0x00 shifted every field by one byte, making w read as 0 -> zero cells -> black void).
        // (Mithia 7.x's clif_sendmapdata DOES emit a leading 0 here; 5.33 differs.)
        var b = new List<byte>();
        b.AddRange(Be((ushort)x0));
        b.AddRange(Be((ushort)y0));
        b.Add((byte)w);
        b.Add((byte)h);

        int total = w * h;
        bool solid = MapDiag.StartsWith("solid:");
        int solidN = solid && int.TryParse(MapDiag.AsSpan(6), out var sn) ? sn : 0;
        // passtest:N -> floor 651 everywhere (all visible), but stamp pass=N onto a vertical wall-line
        // every 5 tiles (mx % 5 == 2). Same tile graphic on wall/non-wall cells, so any movement block is
        // purely collision from the `pass` short and any visual change is purely `pass` affecting render.
        // The exact same pattern is enforced by PassAt() in HandleWalk, so seen wall == blocked wall.
        bool passtest = MapDiag.StartsWith("passtest:");
        int ci = 0;
        for (int iy = 0; iy < h; iy++)
        for (int ix = 0; ix < w; ix++, ci++)
        {
            int mx = x0 + ix, my = y0 + iy;
            ushort tile, pass, obj;
            if (MapDiag == "sweep")
            {
                tile = (ushort)((long)ci * 28550 / Math.Max(1, total));   // 0..28550 across the rect, unmasked
                pass = 0; obj = 0;
            }
            else if (solid)
            {
                tile = (ushort)solidN; pass = 0; obj = 0;
            }
            else if (passtest)
            {
                // Floor 651 everywhere; put a VISIBLE object marker on the wall columns so the tester can
                // see the walls they're blocked by. Objects render but don't collide (only `pass` does),
                // so the marker is purely visual and the block is purely `pass` — the two stay separable.
                bool wall = (mx % 5 == 2);
                tile = 651;
                obj  = (ushort)(wall ? 1542 : 0);
                pass = PassAt(map, mx, my);   // pass=N on the same wall columns (collision)
            }
            else
            {
                tile = (ushort)((map.Tile(mx, my) + CellOff) & 0x3FFF);
                pass = PassAt(map, mx, my);
                obj  = (ushort)((map.Obj(mx, my) + CellOff) & 0x3FFF);
            }
            if (_ver == ClientVersion.V533)
            {
                b.AddRange(Be(tile));
                b.AddRange(Be(pass));
                b.AddRange(Be(obj));
            }
            else
            {
                // 4.95 packs passability into the ground short's top 2 bits — the same word the .map file
                // stores and MapData.GroundWord rebuilds, so a streamed cell is byte-identical to what the
                // client would have read from its own local copy.
                b.AddRange(Be((ushort)((tile & 0x3FFF) | (pass << 14))));
                b.AddRange(Be(obj));
            }
        }

        string mode = MapDiag.Length == 0 ? $"real tileOff={CellOff}" : $"DIAG={MapDiag}";
        Log.Info($"   -> map-data(0x06) [{why}] rect ({x0},{y0}) req {reqW}x{reqH} -> {w}x{h} cells={total} " +
                 $"[{mode}] {(_ver == ClientVersion.V533 ? "3-short" : "2-short")} cells");
        Send(MapBuild(0x06, _gameInc++, b.ToArray()));
        NoteStreamed(x0, y0, w, h);
    }

    // ---- walk-driven viewport streaming ----------------------------------------------------------------
    // The client only sends its 0x05 map-data request on MAP ENTRY, not per step. Walk far enough from where
    // you arrived and you run off the end of what was streamed and hit unwritten cells — a black wall. (It
    // shows as a hard edge rather than a gradual fade because the client's map array is the memory-mapped
    // cache file, freshly zero-filled for a map it has never cached.) So the SERVER has to push terrain as
    // the player moves; that is what the walk 0x06's view checksum is for.
    //
    // We track the rectangle already sent for the current map and, after each step, extend it. Only the
    // newly-exposed strip goes out (a step exposes one row or one column, ~21 cells / ~84 bytes) rather than
    // the whole viewport, which would be ~1.3 KB per step per player for no benefit.
    private ushort _streamMap;          // which map _stream* describes (0 = nothing streamed yet)
    private bool _streamValid;
    private int _streamX0, _streamY0, _streamX1, _streamY1;   // inclusive bounds already sent

    // The client asks for 18x16 / 19x17 around itself. Stream a larger window so a walking player can never
    // outrun the coverage. The DRAWN rect is 19x17 (the builder at 0x44c950 extends one tile past the 17x15
    // viewport on every side — see Session.Entity.cs), so measure margin from that, not from 17x15: a 27x25
    // window centred on the player puts its edge 13 tiles out against a visible edge ~9 tiles out, i.e. 4
    // tiles of lookahead. Cost is per-STRIP, not per-window (one step exposes one 25-cell column = ~100 B),
    // so widening the window is nearly free once the initial fill is done.
    private const int StreamW = 27, StreamH = 25;

    // The client normally re-requests on its own about every 5 steps (measured: 0.20 requests per walk,
    // the same with or without the server replying, so it is scroll-driven rather than retrying). But its
    // own request is only 18x16 around itself — barely wider than the 19x17 it draws — so five steps in one
    // direction runs the drawn edge past the requested rect and you see black cells a step or two ahead of
    // you. And it does NOT always re-request: on map 1000 (18x25, exactly viewport-width so its x0 is pinned
    // at 0) a player walked 11 tiles out of the requested rect with no further request at all.
    //
    // So the push now runs on EVERY step (grace 0). The old 8-step grace deferred to the client's own
    // requests, which is where the visible black boxes came from. The duplication that grace was avoiding is
    // one ~100-byte strip per step; NoteStreamed no longer lets a client request shrink the tracked window,
    // so a request costs one margin fill, not a full re-send every time. NEXUS_V495_PUSHGRACE restores the
    // old deferral, NEXUS_V495_PUSHMAP=0 disables the push entirely.
    private static readonly bool PushMap =
        (Environment.GetEnvironmentVariable("NEXUS_V495_PUSHMAP") ?? "1").Trim() != "0";
    private static readonly int PushGraceSteps =
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_V495_PUSHGRACE"), out var g) && g >= 0 ? g : 0;
    private int _stepsSinceMapReq;

    // Deliberately the LAST window, not a running union of everything sent. A union is a bounding box, and a
    // bounding box claims coverage that was never sent: walk a long way north (box grows tall) then east, and
    // the new eastern columns only went out for the CURRENT rows, yet the box would mark them covered for the
    // whole accumulated height — walk back north along that edge and you'd hit black with the tracker
    // insisting it was already streamed. Last-window can't lie. The cost is re-sending a strip when a player
    // walks back over ground they've seen, which is ~84 bytes.
    private void NoteStreamed(int x0, int y0, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        int x1 = x0 + w - 1, y1 = y0 + h - 1;
        // A rect strictly INSIDE the tracked one adds no coverage, so keep the larger one — it was itself
        // sent as a single rect, so it stays an honest claim (this is not the union the note above rejects;
        // the tracked rect only ever holds a rectangle we actually sent whole). This matters because the
        // client's own 0x05 (18x16) is smaller than our push window: without it, every client request would
        // shrink the tracker and the next step would re-send the whole margin back out to 27x25.
        if (_streamValid && _streamMap == _char.Map
            && x0 >= _streamX0 && y0 >= _streamY0 && x1 <= _streamX1 && y1 <= _streamY1) return;
        _streamMap = _char.Map; _streamValid = true;
        _streamX0 = x0; _streamY0 = y0; _streamX1 = x1; _streamY1 = y1;
    }

    /// <summary>Reset the coverage tracker — the player changed map, so nothing is streamed yet.</summary>
    private void ResetStreamCoverage() { _streamValid = false; _streamMap = 0; }

    /// <summary>The rect we want covered around the player: the stream window clamped to the map (so a map
    /// smaller than the window is simply sent whole).</summary>
    private (int x0, int y0, int w, int h) StreamWindow()
    {
        int xs = _char.MapXs, ys = _char.MapYs;
        int w = Math.Min(StreamW, xs), h = Math.Min(StreamH, ys);
        return (Math.Clamp(_char.X - w / 2, 0, xs - w), Math.Clamp(_char.Y - h / 2, 0, ys - h), w, h);
    }

    /// <summary>Send the whole stream window NOW, ignoring the grace and the coverage tracker. Call right
    /// after the 0x15/0x04/0x33 entry trio on every map entry (login, warp, refresh).
    ///
    /// Warping is the worst case for terrain and used to be the one path with no push at all: the client's
    /// map array is its memory-mapped cache file, freshly zero-filled for a map it has never visited, and
    /// the only thing that filled it was the client's own 0x05 — which lags the 0x15, covers just 18x16
    /// around the arrival tile, and (map 1000) sometimes never comes. So you landed inside a small island
    /// of real tiles with black in every direction, and the walk-driven push only repaired it a strip at a
    /// time as you walked into it. Priming the full window on arrival is ~2.7 KB once per map entry.</summary>
    private void PrimeViewport(string why)
    {
        if (!PushMap || !_enteredWorld || _char.MapXs == 0 || _char.MapYs == 0) return;
        var (x0, y0, w, h) = StreamWindow();
        _stepsSinceMapReq = 0;   // we just fed the client a full window; any configured grace restarts here
        SendMapRect(x0, y0, w, h, why);
    }

    /// <summary>Push any terrain the player's viewport now covers but we haven't sent. Call after a
    /// committed step.</summary>
    private void StreamViewport()
    {
        if (!_enteredWorld || _char.MapXs == 0 || _char.MapYs == 0) return;
        _stepsSinceMapReq++;
        if (!PushMap || _stepsSinceMapReq < PushGraceSteps) return;   // the client is asking for itself

        var (x0, y0, w, h) = StreamWindow();
        int x1 = x0 + w - 1, y1 = y0 + h - 1;

        // Nothing streamed for this map yet, or the window has jumped clear of the last one (a warp landing
        // inside the same map) -> send the whole thing; strips only make sense against an overlapping rect.
        bool overlaps = _streamValid && _streamMap == _char.Map
                        && x0 <= _streamX1 && x1 >= _streamX0 && y0 <= _streamY1 && y1 >= _streamY0;
        if (!overlaps) { SendMapRect(x0, y0, w, h, "walk-init"); return; }
        if (x0 >= _streamX0 && y0 >= _streamY0 && x1 <= _streamX1 && y1 <= _streamY1) return;   // already sent

        // A step moves the window one tile on one axis, so normally exactly one of these fires. Corners can
        // be sent twice when two do; re-writing a cell the client already has is harmless.
        // NoteStreamed runs inside SendMapRect, so capture the old bounds before the first send.
        int ox0 = _streamX0, oy0 = _streamY0, ox1 = _streamX1, oy1 = _streamY1;
        if (x1 > ox1) SendMapRect(ox1 + 1, y0, x1 - ox1, h, "walk-e");
        if (x0 < ox0) SendMapRect(x0, y0, ox0 - x0, h, "walk-w");
        if (y1 > oy1) SendMapRect(x0, oy1 + 1, w, y1 - oy1, "walk-s");
        if (y0 < oy0) SendMapRect(x0, y0, w, oy0 - y0, "walk-n");
        NoteStreamed(x0, y0, w, h);   // the window as a whole is now covered
    }

    private Character _char = new();
    private byte[] _encTable = Array.Empty<byte>();
    private byte _gameInc = 0;   // per-packet increment for game-channel sends


    // Live creatures the player can fight. Server-authoritative HP; the client only draws them.
    // Populated by the mob commands (@mob/@mobrow/@spawn); entries are removed on death (0x0E).
    private readonly List<Mob> _mobs = new();
    private uint _nextMobId = 5000;      // entity-id pool for spawned creatures (well above the self id)
    private byte _facing = 0;            // last direction the player faced (0=N 1=E 2=S 3=W); drives melee
    private byte _realm = RealmCenter;   // realm-center camera lock; toggled live by F4 (0x1b sub-cmd 0x07)
    private int _lockOx, _lockOy;        // camera origin frozen when realm-center turned ON (map top-left tile)
    // Fast-move = the client's movement model (RTK clif_parsewalk gates on FLAG_FASTMOVE):
    //   ON  = client-authoritative: the client moves/animates freely and is only corrected on desync.
    //         The server must send the walker NOTHING on a good step (0x26 self-walk is skipped).
    //   OFF = server-authoritative: the client will NOT move until the server assigns the tile, so every
    //         step must be answered with a position/move packet.
    // The client toggles it locally and notifies us via 0x1b sub-cmd 0x09 (it does NOT report its state on
    // connect). The client PERSISTS fast-move across launches, and the working/smooth setup is fast-move
    // ON (client-authoritative), so we default ON to match a client that already has it enabled. Each
    // 0x1b/09 notification flips it to stay in sync. (If a fresh client actually boots OFF, one toggle
    // re-syncs; NEXUS_V495_FASTMOVE_DEFAULT can override the assumed startup state.)
    private bool _fastMove = FastMoveDefault;


    private static readonly bool FastMoveDefault =
        Environment.GetEnvironmentVariable("NEXUS_V495_FASTMOVE_DEFAULT") == "0" ? false : true;

    // Viewport-streamed world mobs: the set of shared-mob ids currently drawn on THIS client. The client's
    // 0x07 spawn silently drops entities outside the camera rect, so a 400-mob map can't be blanket-sent —
    // instead SyncMobs spawns mobs as they enter view and despawns them as they leave, keeping the client to
    // a screenful. Guarded by _viewLock (touched by both this read-loop and the World tick thread). Send()
    // no longer takes a lock (it's a lock-free channel enqueue), so _viewLock can't participate in a deadlock.
    private readonly HashSet<uint> _shownMobs = new();
    // Shown mobs currently sitting in the overdraw band (outside the strict 17x15, inside the drawn 19x17).
    // The client MAY have culled these; if one steps back into the strict rect we re-assert its 0x07 rather
    // than assume it's still there. This is what makes HidePad > ShowPad safe — see SyncMobs.
    private readonly HashSet<uint> _edgeMobs = new();
    private readonly object _viewLock = new();
    // SHOW at the strict 17x15 edge, HIDE at the drawn edge one tile further out.
    //
    // ShowPad must stay 0: the 0x07 spawn is viewport-gated (0x424310 is a rect test against the camera
    // viewport), so a spawn for an off-screen tile is silently dropped and would mark a mob "shown" that
    // the client never created.
    //
    // HidePad is 1 because the client DRAWS one tile past the viewport on every side: the viewport builder
    // 0x44c950 clamps to originX-1 .. originX+ViewW+1 (see Session.Entity.cs), i.e. a 19x17 drawn rect
    // around a 17x15 viewport. Despawning at 17x15 therefore yanks mobs off a tile that is still on screen
    // — the reported "mobs pop out one tile too soon". The dead zone this note used to warn about (we think
    // it's drawn, the client already culled it, we never re-send) is closed by _edgeMobs: anything that
    // spends time in the band gets a fresh 0x07 when it re-enters the strict rect.
    private const int ShowPad = 0;
    private const int HidePad = 1;

}
