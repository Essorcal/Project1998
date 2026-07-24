using System.Net;
using System.Net.Sockets;
using Shared;

namespace Server;

/// <summary>Accepts TCP connections on each configured port; one Session per connection.</summary>
public sealed class TkListener
{
    private readonly int[] _ports;
    private readonly CharacterStore _store;

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
    private static string RepoDataDir()
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
            var client = await listener.AcceptTcpClientAsync();
            _ = new Session(client, port, _store).RunAsync();   // fire-and-forget per connection
        }
    }
}
