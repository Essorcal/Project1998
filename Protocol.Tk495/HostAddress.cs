namespace Protocol.Tk495;

/// <summary>
/// The four address octets a redirect carries. Both processes build a <see cref="LoginRedirect"/> — the login
/// server pointing a client at the game server, the game server bouncing it back to login — so both had to
/// turn a configured "a.b.c.d" into octets, and both carried their own copy of the same parser.
///
/// <para>The configured text comes from <c>ServerConfig</c> — <c>P1998_GAME_HOST</c> and the
/// <c>P1998_LOGIN_HOST</c> fallback chain are resolved there, so this class takes a value and never a
/// variable name.</para>
/// </summary>
public static class HostAddress
{
    /// <summary>"a.b.c.d" -> 4 octets in normal order (the handoff packet reverses them). Falls back to
    /// loopback for null, blank, or anything that is not four parseable bytes — a misconfigured host must
    /// leave a same-box deployment working rather than send clients somewhere unreachable.</summary>
    /// <param name="host">The configured value (NOT an environment variable name), or null when unset.</param>
    public static byte[] Parse(string? host)
    {
        var def = new byte[] { 127, 0, 0, 1 };
        if (string.IsNullOrWhiteSpace(host)) return def;
        var parts = host.Split('.');
        if (parts.Length != 4) return def;
        var o = new byte[4];
        for (int i = 0; i < 4; i++)
            if (!byte.TryParse(parts[i], out o[i])) return def;
        return o;
    }
}
