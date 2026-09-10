using System.Net;
using System.Net.Sockets;
using Shared;

namespace LoginServer;

/// <summary>The login process's host: holds the character store and runs <see cref="TkAcceptor"/> on the
/// configured ports with one <c>LoginSession</c> per admitted connection. The accept loop, NoDelay, the
/// PROXY-protocol admission and <see cref="ConnGuard"/> are the shared ones — this process used to carry its
/// own copy of all four.</summary>
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
        // The login port is the internet-facing front door, so admission control matters most here. Tighter
        // defaults than the game port would be reasonable; tune via P1998_LOGIN_* (see ConnGuard.FromEnv).
        await new TkAcceptor(_ports, ConnGuard.FromEnv("LOGIN"),
                             (TcpClient client, int port, IPAddress? realIp) =>
                                 new LoginSession(client, port, _store, realIp).RunAsync())
            .RunAsync();
    }
}
