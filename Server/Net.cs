using System.Net;
using System.Net.Sockets;
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
        // Anchor the character store to the repo root, NOT the current working directory. Anchoring to
        // cwd caused a nasty "regression": launching the server from Server\ instead of the repo root
        // pointed the store at an empty Server\data\chars, so every login missed its saved character and
        // rendered the default face/gender. RepoDataDir walks up from the binary to the project root so
        // the store is the same folder no matter where the process is started from.
        _store = new CharacterStore(RepoDataDir());
        Log.Info($"character store: {_store.Directory}");
    }

    // Find <repo>/data/chars by walking up from the executable location until we hit the directory that
    // holds the solution/project (marked by the Server\ folder or a .sln). Falls back to cwd if no
    // marker is found, preserving the old behavior for unusual layouts.
    internal static string RepoDataDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            bool isRoot = dir.GetFiles("*.sln").Length > 0
                       || (System.IO.Directory.Exists(Path.Combine(dir.FullName, "Server"))
                           && System.IO.Directory.Exists(Path.Combine(dir.FullName, "Shared")));
            if (isRoot) return Path.Combine(dir.FullName, "data", "chars");
            dir = dir.Parent;
        }
        return Path.Combine(System.IO.Directory.GetCurrentDirectory(), "data", "chars");
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

        var tasks = new List<Task>();
        foreach (var p in _ports) tasks.Add(ListenAsync(p));
        await Task.WhenAll(tasks);
    }

    private int _shutdownOnce;   // Interlocked guard: Environment.Exit(0) below re-raises ProcessExit, so
                                  // both handlers can reach Shutdown -- make sure the flush runs exactly once.
    private void Shutdown(string reason)
    {
        if (Interlocked.Exchange(ref _shutdownOnce, 1) != 0) return;
        Log.Info($"=== shutdown signal ({reason}) — flushing connected players ===");
        int n = _world.SaveAllPlayers();
        Log.Info($"   -> flushed {n} player(s)");
    }

    private async Task ListenAsync(int port)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Log.Info($"listening on 0.0.0.0:{port}");
        while (true)
        {
            // Guard the accept loop: a transient AcceptTcpClientAsync throw (e.g. a connection reset
            // between the SYN and accept, or a per-socket resource hiccup) must NOT fault this task and
            // unwind the whole process. Log and keep accepting. One bad connection can't take the server
            // down; the per-session try/finally in Session.RunAsync isolates everything after accept.
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch (Exception e) { Log.Info($"!! accept on :{port} failed: {e.Message}"); continue; }

            // Admission control BEFORE spawning a session: shed load / throttle floods at the cheapest point.
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

    // Runs one session and guarantees the admission slot is released exactly once when it ends (however it
    // ends — clean disconnect, error, or slow-client drop).
    private async Task RunAndReleaseAsync(TcpClient client, int port, IPAddress ip)
    {
        try { await new Session(client, port, _store, _world).RunAsync(); }
        catch (Exception e) { Log.Info($"!! session {ip} on :{port} faulted: {e.Message}"); }
        finally { _guard.Release(ip); }
    }
}
