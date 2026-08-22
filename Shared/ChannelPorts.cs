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
    /// <summary>Game port paired with a login port. Unknown ports fall back to the V495 channel.</summary>
    public static int GameFor(int loginPort) => loginPort == 2001 ? 2006 : 2005;

    /// <summary>Login port paired with a game port. Unknown ports fall back to the V495 channel.</summary>
    public static int LoginFor(int gamePort) => gamePort == 2006 ? 2001 : 2000;
}
