using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using Shared;

namespace Server;

/// <summary>Accepts TCP connections on each configured port; one Session per connection.</summary>
public sealed class TkListener
{
    private readonly int[] _ports;
    private readonly CharacterStore _store;
    private readonly World _world = new();   // the one shared world every session broadcasts through
    private readonly ConnGuard _guard = ConnGuard.FromEnv("GAME");   // per-IP/global/rate admission control

    public TkListener(int[] ports)
    {
        _ports = ports;
        // Anchor the character store to the deployment root, NOT the current working directory. Anchoring
        // to cwd caused a nasty "regression": launching the server from Server\ instead of the repo root
        // pointed the store at an empty Server\data\chars, so every login missed its saved character and
        // rendered the default face/gender. RepoPaths walks up from the binary to the root so the store is
        // the same folder no matter where the process is started from.
        _store = new CharacterStore(Shared.RepoPaths.CharsDir());
        Log.Info($"character store: {_store.Directory}");
    }

    public async Task RunAsync()
    {
        // Graceful-shutdown flush hook (robust persistence, complements the per-session autosave in
        // World.AutoSaveLoop/Session.FlushIfDue): on a clean stop, save every connected player's pending
        // mutation before the process actually exits. This is the flush half of a graceful restart — it
        // CANNOT help against a hard crash/kill -9/power loss, which is exactly what the periodic autosave
        // sweep + each session's own on-thread flush already bound to ~AutoSaveMs instead.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;   // we exit ourselves once the flush completes, not mid-write
            Shutdown("Ctrl+C");
            Environment.Exit(0);   // also re-raises ProcessExit below; the _shutdownOnce guard makes that a no-op
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown("ProcessExit");

        // SIGTERM explicitly. This is how a Linux host actually stops the server — `systemctl restart`,
        // `docker stop`, and an OOM-killer-adjacent supervisor all send SIGTERM, never Ctrl+C. The .NET
        // runtime does raise ProcessExit for SIGTERM, so the handler above would cover it, but registering
        // here makes the deployment-critical path explicit rather than an implementation detail, and gets
        // the flush started at the top of the shutdown rather than at the very end of it. The
        // _shutdownOnce guard means running through both paths flushes exactly once.
        // (Under systemd, TimeoutStopSec is what bounds this before SIGKILL; our unit sets 30s.)
        _sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            Shutdown("SIGTERM");
            ctx.Cancel = false;   // let the runtime carry on terminating; we only wanted the flush first
        });

        var tasks = new List<Task>();
        foreach (var p in _ports) tasks.Add(ListenAsync(p));
        await Task.WhenAll(tasks);
    }

    private PosixSignalRegistration? _sigterm;   // held for the process lifetime; disposing would unhook it

    private int _shutdownOnce;   // Interlocked guard: Environment.Exit(0) below re-raises ProcessExit, so
                                  // both handlers can reach Shutdown -- make sure the flush runs exactly once.
    private void Shutdown(string reason)
    {
        if (Interlocked.Exchange(ref _shutdownOnce, 1) != 0) return;
        Log.Info($"=== shutdown signal ({reason}) — flushing connected players ===");
        int n = _world.SaveAllPlayers();
        Log.Info($"   -> flushed {n} player(s)");
        // Logging is asynchronous now (see Log), so the lines above are still queued at this point and the
        // Environment.Exit(0) that follows Ctrl+C would discard them. Drain the queue last, once nothing
        // else has anything left to say.
        Log.Shutdown();
    }

    private async Task ListenAsync(int port)
    {
        var listener = new TcpListener(NetBind.Address, port);
        listener.Start();
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
            catch (Exception e) { Log.Info($"!! accept on :{port} failed: {e.Message}"); continue; }

            // DISABLE NAGLE. Every packet this server sends is small (a cast is ~5 tiny packets: 0x29 effect,
            // 0x19 sound, 0x1A action, 0x08 stats, 0x0A text), and .NET leaves TCP_NODELAY off by default.
            // Nagle then holds each small segment until the previous one is ACKed, and the client's delayed-ACK
            // timer can sit on that for tens of ms — so back-to-back packets get delivered with variable jitter
            // instead of back-to-back. Audible symptom: casts that should land in unison (three per action-budget
            // window while a key is held) play their sounds slightly flammed. Latency matters here, throughput
            // does not; there is no case where batching this game's packets is worth the delay.
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
                    Log.Info($"!! REJECT {peer} on :{port} (not in NEXUS_PROXY_ALLOW); {_guard.Total} live");
                    try { client.Close(); } catch { /* already gone */ }
                    continue;
                }
                if (!_guard.TryReserveGlobal(out var greason))
                {
                    Log.Info($"!! REJECT {peer} on :{port} ({greason}); {_guard.Total} live");
                    try { client.Close(); } catch { /* already gone */ }
                    continue;
                }
                _ = RunProxiedAsync(client, port, peer);
                continue;
            }

            // Admission control BEFORE spawning a session: shed load / throttle floods at the cheapest point.
            if (!_guard.TryAdmit(peer, out var reason))
            {
                Log.Info($"!! REJECT {peer} on :{port} ({reason}); {_guard.Total} live");
                try { client.Close(); } catch { /* already gone */ }
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
            Log.Info($"!! PROXY header from {peer} on :{port} failed: {e.Message}");
            _guard.ReleaseGlobal();
            try { client.Close(); } catch { /* already gone */ }
            return;
        }

        if (!_guard.BindIp(ip, out var reason))
        {
            Log.Info($"!! REJECT {ip} on :{port} ({reason}); {_guard.Total} live");
            _guard.ReleaseGlobal();
            try { client.Close(); } catch { /* already gone */ }
            return;
        }
        await RunAndReleaseAsync(client, port, ip, ip);
    }

    // Runs one session and guarantees the admission slot is released exactly once when it ends (however it
    // ends — clean disconnect, error, or slow-client drop). realIp is non-null only on the proxied path,
    // where it is what the session must use for handoff tokens and logging instead of the socket's peer.
    private async Task RunAndReleaseAsync(TcpClient client, int port, IPAddress ip, IPAddress? realIp)
    {
        try { await new Session(client, port, _store, _world, realIp).RunAsync(); }
        catch (Exception e) { Log.Info($"!! session {ip} on :{port} faulted: {e.Message}"); }
        finally { _guard.Release(ip); }
    }
}
