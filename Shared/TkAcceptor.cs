using System.Net;
using System.Net.Sockets;

namespace Shared;

/// <summary>
/// The accept loop both front doors run: bind, guarded accept, TCP_NODELAY, the two-stage PROXY-protocol
/// admission, and <see cref="ConnGuard"/> with a release that happens exactly once per admitted connection.
///
/// <para><b>Why this is one class.</b> The login and game processes carried the same loop twice — the same
/// gates in the same order, differing only in comments, in four log shapes, and in which session type was
/// constructed at the end. A duplicated accept path is the worst place for a divergence to live: it is the
/// admission control for an internet-facing port, so a fix applied to one copy and not the other is a
/// security hole that nothing in either process complains about. Everything a process actually differs in
/// is passed in: the ports, its own <see cref="ConnGuard"/> (which is what namespaces the P1998_LOGIN_* /
/// P1998_GAME_* limits), and one delegate that runs a session on an admitted socket.</para>
///
/// <para>The host object stays with each process — <c>Server.TkListener</c> still owns the world, the
/// thread start-up and the shutdown flush; <c>LoginServer.LoginListener</c> still owns the character store.
/// This class owns nothing but the socket path.</para>
/// </summary>
public sealed class TkAcceptor
{
    private readonly int[] _ports;
    private readonly ConnGuard _guard;
    private readonly Func<TcpClient, int, IPAddress?, Task> _runSession;

    /// <param name="ports">Every port to listen on; one accept loop per port, all awaited together.</param>
    /// <param name="guard">This process's admission control. One instance for the whole process — the
    /// global cap is a per-process load-shed budget, so a guard per port would multiply it by the port
    /// count.</param>
    /// <param name="runSession">Runs one session on an admitted socket: (client, port, realIp). realIp is
    /// non-null only on the proxied path, where it is what the session must use for handoff tokens and
    /// logging instead of the socket's peer. The slot is released when the returned task completes,
    /// however it completes.</param>
    public TkAcceptor(int[] ports, ConnGuard guard, Func<TcpClient, int, IPAddress?, Task> runSession)
    {
        _ports = ports;
        _guard = guard;
        _runSession = runSession;
    }

    /// <summary>Listen on every configured port until the process ends.</summary>
    public async Task RunAsync()
    {
        var tasks = new List<Task>();
        foreach (var p in _ports) tasks.Add(ListenAsync(p));
        await Task.WhenAll(tasks);
    }

    private async Task ListenAsync(int port)
    {
        var listener = new TcpListener(NetBind.Address, port);
        try { listener.Start(); }
        catch (SocketException e)
        {
            // Binding still kills the process; add the endpoint context before preserving the same exception.
            Log.Error($"could not listen on {NetBind.Describe}:{port}: {e.Message}", e);
            throw;
        }
        Log.Info($"listening on {NetBind.Describe}:{port}"
                 + (ProxyProtocol.Enabled ? $" [PROXY protocol trusted from {ProxyProtocol.DescribeAllow}]" : ""));
        while (true)
        {
            // Guard the accept loop: a transient AcceptTcpClientAsync throw (e.g. a connection reset
            // between the SYN and accept, or a per-socket resource hiccup) must NOT fault this task and
            // unwind the whole process. Log and keep accepting. One bad connection can't take the server
            // down; the per-session try/finally in Session.RunAsync isolates everything after accept.
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch (Exception e) { Log.Warn($"accept on :{port} failed — still listening", e); continue; }

            // DISABLE NAGLE. Every packet this server sends is small (a cast is ~5 tiny packets: 0x29 effect,
            // 0x19 sound, 0x1A action, 0x08 stats, 0x0A text), and .NET leaves TCP_NODELAY off by default.
            // Nagle then holds each small segment until the previous one is ACKed, and the client's delayed-ACK
            // timer can sit on that for tens of ms — so back-to-back packets get delivered with variable jitter
            // instead of back-to-back. Audible symptom: casts that should land in unison (three per action-budget
            // window while a key is held) play their sounds slightly flammed. Latency matters here, throughput
            // does not; there is no case where batching this game's packets is worth the delay.
            // EXPECTED: the peer reset between accept and here. Nothing is lost by not reporting it — the
            // read loop is about to fail on the same socket and log that with its address.
            try { client.NoDelay = true; } catch { /* socket already dead — the read loop will notice */ }

            var peer = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;

            // BEHIND A PROXY, the address to gate on is in a header we have not read yet, and reading it
            // means an await — which cannot happen here, because one peer that connects and then says
            // nothing would stall every other pending connection for the header timeout. So this branch
            // reserves only the global slot (load-shedding stays at the cheapest point) and hands off; the
            // per-IP and rate gates are applied against the real address inside RunProxiedAsync.
            if (ProxyProtocol.Enabled)
            {
                // The peer check comes FIRST, before a byte is read. A PROXY header is just bytes at the
                // head of a connection, so anything allowed to send one can claim any source address it
                // likes — which would make this a bypass of the per-IP gates rather than a fix for them.
                if (!ProxyProtocol.IsTrustedPeer(peer))
                {
                    Log.Info($"!! REJECT {peer} on :{port} (not in P1998_PROXY_ALLOW); {_guard.Total} live");
                    try { client.Close(); } catch { /* EXPECTED: closing a socket we are rejecting anyway */ }
                    continue;
                }
                if (!_guard.TryReserveGlobal(out var greason))
                {
                    Log.Info($"!! REJECT {peer} on :{port} ({greason}); {_guard.Total} live");
                    try { client.Close(); } catch { /* EXPECTED: closing a socket we are rejecting anyway */ }
                    continue;
                }
                _ = RunProxiedAsync(client, port, peer);
                continue;
            }

            // Admission control BEFORE spawning a session: shed load / throttle floods at the cheapest point.
            if (!_guard.TryAdmit(peer, out var reason))
            {
                Log.Info($"!! REJECT {peer} on :{port} ({reason}); {_guard.Total} live");
                try { client.Close(); } catch { /* EXPECTED: closing a socket we are rejecting anyway */ }
                continue;
            }
            _ = RunAndReleaseAsync(client, port, peer, null);   // fire-and-forget; releases the slot on exit
        }
    }

    // Reads the PROXY header, then applies the gates the accept loop had to defer. Owns the global slot
    // reserved above from entry: every exit path either releases it here or hands it to RunAndReleaseAsync.
    private async Task RunProxiedAsync(TcpClient client, int port, IPAddress peer)
    {
        IPAddress ip;
        try
        {
            // A LOCAL header (the proxy's own health check) carries no address; fall back to the peer,
            // which is the proxy itself and therefore correctly exempt from the per-IP gates.
            ip = await ProxyProtocol.ReadHeaderAsync(client.GetStream()) ?? peer;
        }
        catch (Exception e)
        {
            Log.Warn($"PROXY header from {peer} on :{port} rejected — connection dropped", e);
            _guard.ReleaseGlobal();
            try { client.Close(); } catch { /* EXPECTED: closing a socket we are rejecting anyway */ }
            return;
        }

        if (!_guard.BindIp(ip, out var reason))
        {
            Log.Info($"!! REJECT {ip} on :{port} ({reason}); {_guard.Total} live");
            _guard.ReleaseGlobal();
            try { client.Close(); } catch { /* EXPECTED: closing a socket we are rejecting anyway */ }
            return;
        }
        await RunAndReleaseAsync(client, port, ip, ip);
    }

    // Runs one session and guarantees the admission slot is released exactly once when it ends (however it
    // ends — clean disconnect, error, or slow-client drop). realIp is non-null only on the proxied path,
    // where it is what the session must use for handoff tokens and logging instead of the socket's peer.
    private async Task RunAndReleaseAsync(TcpClient client, int port, IPAddress ip, IPAddress? realIp)
    {
        try { await _runSession(client, port, realIp); }
        // RunAsync catches its own read-loop failures; only its finally-block teardown can reach here.
        catch (Exception e) { Log.Error($"session {ip} on :{port} faulted during teardown", e); }
        finally { _guard.Release(ip); }
    }
}
