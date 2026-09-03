namespace Shared;

/// <summary>
/// The two client versions get their own pair of front doors, and the pair must stay together for the whole
/// round trip: login 2000 → game 2005 is V495, login 2001 → game 2006 is V533. The server stamps the client
/// version from the port a connection arrived on (<c>Session.ClientVersion</c>) rather than sniffing the wire,
/// so crossing the channels does not fail loudly — it hands a 5.33 client to the 4.95 protocol path (or the
/// reverse) and the player gets a black screen or garbled terrain.
///
/// Both directions live here because both are now used: the login server hands off outward, and the game
/// server bounces back to login when the player exits to the select screen (<c>0x0B</c>).
/// </summary>
public static class ChannelPorts
{
    private sealed record Pair(int Login495, int Login533, int Game495, int Game533)
    {
        public bool Contains(int port) =>
            port == Login495 || port == Login533 || port == Game495 || port == Game533;
    }

    private static readonly Pair DefaultPair = new(2000, 2001, 2005, 2006);
    private static Pair? _configuredPair;

    /// <summary>Game port paired with a login port.</summary>
    public static int GameFor(int loginPort) => loginPort + 5;

    /// <summary>Login port paired with a game port.</summary>
    public static int LoginFor(int gamePort) => gamePort - 5;

    /// <summary>True when <paramref name="port"/> is one of the configured pair's login channels.</summary>
    public static bool IsLogin(int port)
    {
        Pair pair = CurrentPairFor(port);
        return port == pair.Login495 || port == pair.Login533;
    }

    /// <summary>True when <paramref name="port"/> is the second (5.33) channel in either half of the pair.</summary>
    public static bool IsV533(int port)
    {
        Pair pair = CurrentPairFor(port);
        return port == pair.Login533 || port == pair.Game533;
    }

    /// <summary>Set this process's channels from the login pair supplied on its command line.</summary>
    public static void ConfigureLoginPair(IReadOnlyList<int> loginPorts)
    {
        RequirePair(loginPorts);
        Volatile.Write(ref _configuredPair,
            new Pair(loginPorts[0], loginPorts[1], GameFor(loginPorts[0]), GameFor(loginPorts[1])));
    }

    /// <summary>Set this process's channels from the game pair supplied on its command line.</summary>
    public static void ConfigureGamePair(IReadOnlyList<int> gamePorts)
    {
        RequirePair(gamePorts);
        Volatile.Write(ref _configuredPair,
            new Pair(LoginFor(gamePorts[0]), LoginFor(gamePorts[1]), gamePorts[0], gamePorts[1]));
    }

    private static Pair CurrentPairFor(int port)
    {
        Pair? configured = Volatile.Read(ref _configuredPair);
        return configured is not null && configured.Contains(port) ? configured : DefaultPair;
    }

    private static void RequirePair(IReadOnlyList<int> ports)
    {
        if (ports.Count != 2)
            throw new ArgumentException("Exactly two ports are required: first 4.95, then 5.33.", nameof(ports));
    }
}
