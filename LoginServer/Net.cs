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
    // defaults than the game port would be reasonable; tune via NEXUS_LOGIN_* (see ConnGuard.FromEnv).
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
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Log.Info($"listening on 0.0.0.0:{port}");
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

            var ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
            if (!_guard.TryAdmit(ip, out var reason))
            {
                Log.Info($"!! REJECT {ip} on :{port} ({reason}); {_guard.Total} live");
                try { client.Close(); } catch { /* already gone */ }
                continue;
            }
            _ = RunAndReleaseAsync(client, port, ip);   // fire-and-forget; releases the admission slot on exit
        }
    }

    // Runs one login session and guarantees the admission slot is released exactly once when it ends.
    private async Task RunAndReleaseAsync(TcpClient client, int port, IPAddress ip)
    {
        try { await new LoginSession(client, port, _store).RunAsync(); }
        catch (Exception e) { Log.Info($"!! login session {ip} on :{port} faulted: {e.Message}"); }
        finally { _guard.Release(ip); }
    }
}
