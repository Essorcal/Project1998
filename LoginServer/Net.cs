using System.Net;
using System.Net.Sockets;
using Shared;

namespace LoginServer;

/// <summary>Accepts login-channel connections; one LoginSession per connection.</summary>
public sealed class LoginListener
{
    private readonly int[] _ports;
    private readonly CharacterStore _store;
    // The login port is the internet-facing front door, so admission control matters most here. Tighter
    // defaults than the game port would be reasonable; tune via P1998_LOGIN_* (see ConnGuard.FromEnv).
    private readonly ConnGuard _guard = ConnGuard.FromEnv("LOGIN");

    public LoginListener(int[] ports, CharacterStore store)
    {
        _ports = ports;
        _store = store;
    }

    public async Task RunAsync()
    {
        var tasks = new List<Task>();
        foreach (var p in _ports) tasks.Add(ListenAsync(p));
        await Task.WhenAll(tasks);
    }

    private async Task ListenAsync(int port)
    {
        var listener = new TcpListener(NetBind.Address, port);
        listener.Start();
        Log.Info($"listening on {NetBind.Describe}:{port}"
                 + (ProxyProtocol.Enabled ? $" [PROXY protocol trusted from {ProxyProtocol.DescribeAllow}]" : ""));
        while (true)
        {
            // Guard the accept loop so a transient accept throw can't fault this task and unwind the
            // whole login process (the front door must stay up). Log and keep accepting.
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch (Exception e) { Log.Info($"!! accept on :{port} failed: {e.Message}"); continue; }

            // Same TCP_NODELAY reasoning as the game server's accept loop: small packets, latency-sensitive,
            // no throughput to gain from Nagle batching. Here it just makes login/redirect feel snappier.
            try { client.NoDelay = true; } catch { /* socket already dead */ }

            var peer = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;

            // Same two-stage admission as the game listener behind a proxy: the real address only exists
            // once the PROXY header has been read, and that read must not block the accept loop. See
            // Server/Net.cs and the ProxyProtocol class remarks. It matters more here — this is the port
            // the per-IP failed-login throttle protects, and that throttle is worthless keyed on the proxy.
            if (ProxyProtocol.Enabled)
            {
                if (!ProxyProtocol.IsTrustedPeer(peer))
                {
                    Log.Info($"!! REJECT {peer} on :{port} (not in P1998_PROXY_ALLOW); {_guard.Total} live");
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

            if (!_guard.TryAdmit(peer, out var reason))
            {
                Log.Info($"!! REJECT {peer} on :{port} ({reason}); {_guard.Total} live");
                try { client.Close(); } catch { /* already gone */ }
                continue;
            }
            _ = RunAndReleaseAsync(client, port, peer, null);   // fire-and-forget; releases the slot on exit
        }
    }

    // Reads the PROXY header, then applies the gates the accept loop deferred. Owns the reserved global
    // slot from entry: every exit path either releases it or hands it to RunAndReleaseAsync.
    private async Task RunProxiedAsync(TcpClient client, int port, IPAddress peer)
    {
        IPAddress ip;
        try
        {
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

    // Runs one login session and guarantees the admission slot is released exactly once when it ends.
    private async Task RunAndReleaseAsync(TcpClient client, int port, IPAddress ip, IPAddress? realIp)
    {
        try { await new LoginSession(client, port, _store, realIp).RunAsync(); }
        catch (Exception e) { Log.Info($"!! login session {ip} on :{port} faulted: {e.Message}"); }
        finally { _guard.Release(ip); }
    }
}
