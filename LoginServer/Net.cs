using System.Net;
using System.Net.Sockets;
using Shared;

namespace LoginServer;

/// <summary>Accepts login-channel connections; one LoginSession per connection.</summary>
public sealed class LoginListener
{
    private readonly int[] _ports;
    private readonly CharacterStore _store;

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
            _ = new LoginSession(client, port, _store).RunAsync();   // fire-and-forget per connection
        }
    }
}
