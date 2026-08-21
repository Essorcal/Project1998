using System.Net;

namespace Shared;

/// <summary>
/// Which local interface the login and game listeners bind to.
///
/// Defaults to <see cref="IPAddress.Any"/> (0.0.0.0 — every interface), which is correct for a
/// real deployment: the server should answer on the LAN, the internet-facing port-forward, and
/// loopback alike.
///
/// The escape hatch is P1998_BIND. Set it to a specific interface — e.g. your LAN IP
/// 192.168.1.50 — to keep the servers OFF the loopback interface. That matters only when the
/// launcher's loopback proxy runs on the SAME machine as the server: with a 0.0.0.0 bind the
/// server also answers 127.0.0.1:&lt;port&gt; and competes with the proxy for the client's
/// connection, which splits the login and game legs across two source IPs and breaks the
/// IP-bound handoff token. Binding a specific interface frees 127.0.0.1 for the proxy to own
/// outright. A port-forward still reaches the server (the router DNATs to this same LAN IP), so
/// remote players are unaffected. Purely a same-box aid; leave it unset in production.
/// </summary>
public static class NetBind
{
    public static IPAddress Address { get; } = Resolve();

    /// <summary>Human-readable form for log lines ("0.0.0.0" for Any, else the literal address).</summary>
    public static string Describe => Address.Equals(IPAddress.Any) ? "0.0.0.0" : Address.ToString();

    private static IPAddress Resolve()
    {
        var raw = Environment.GetEnvironmentVariable("P1998_BIND");
        return !string.IsNullOrWhiteSpace(raw) && IPAddress.TryParse(raw.Trim(), out var addr)
            ? addr
            : IPAddress.Any;
    }
}
