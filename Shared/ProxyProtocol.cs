using System.Net;
using System.Net.Sockets;

namespace Shared;

/// <summary>
/// PROXY protocol v2 support for the accept path — the piece that makes putting HAProxy (or any TCP load
/// balancer) in front of the game safe rather than actively harmful.
///
/// WHY THIS EXISTS. <see cref="ConnGuard"/> and <see cref="HandoffTokens"/> both key off the address a
/// connection arrives from. Behind a TCP proxy that address is the PROXY'S, identically for every player,
/// and the failure mode depends only on where the proxy runs:
///
///   proxy on the same box (backend 127.0.0.1) -> every connection is loopback -> ConnGuard's loopback
///       exemption bypasses the per-IP cap and the rate gate entirely. The abuse protection silently
///       turns OFF and nothing in the log says so.
///   proxy in a container (backend across the bridge) -> every connection arrives from the gateway
///       address -> P1998_*_PERIP (default 8) becomes a GLOBAL cap and the ninth player is rejected.
///
/// Same cause, opposite symptoms, and the handoff token degrades either way: binding a nonce to "came
/// from the proxy" is binding it to nothing, since that is true of every connection including an
/// attacker's. So this is not a nice-to-have that can follow the proxy rollout — it has to land first.
///
/// THE TRUSTED-PEER CHECK IS THE WHOLE SECURITY MODEL. A PROXY header is just bytes at the head of a
/// connection: anyone who can reach the port can send one and claim to be any address they like. That
/// would turn this from a fix for the per-IP gates into a total bypass of them (and would let an attacker
/// forge the source address a handoff token is bound to). The header is therefore only read from peers in
/// <c>P1998_PROXY_ALLOW</c>, checked BEFORE a single byte is read, and every other peer is dropped at
/// accept. A misconfigured allow-list fails closed — nobody connects — which is the right direction.
///
/// OFF BY DEFAULT. <c>P1998_TRUST_PROXY=0</c> means the accept path behaves exactly as it always has, so
/// a bare `dotnet run` clone with no proxy in front is unaffected and never waits for a header that is
/// not coming.
///
/// v2 ONLY. HAProxy's `send-proxy-v2` is what the infra config uses. v1 (the human-readable "PROXY TCP4
/// ..." line) is deliberately not parsed: supporting both means sniffing the first bytes to decide which
/// grammar applies, and the binary signature below is unambiguous by design.
/// </summary>
public static class ProxyProtocol
{
    // 12-byte v2 signature. Chosen by the spec so it can never collide with a real protocol's first bytes.
    private static readonly byte[] Signature =
        { 0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A };

    /// <summary>Read and trust a PROXY header on every accepted connection. Default off.</summary>
    public static bool Enabled { get; } =
        (Environment.GetEnvironmentVariable("P1998_TRUST_PROXY") ?? "0").Trim() == "1";

    /// <summary>How long a trusted peer has to deliver the header before the connection is dropped. The
    /// proxy writes it immediately on connect, so this only ever fires on a broken or hostile peer; it is
    /// separate from P1998_HANDSHAKE_MS because that budget covers the first GAME packet, which cannot
    /// start being parsed until this is out of the way.</summary>
    private static readonly int HeaderMs =
        int.TryParse(Environment.GetEnvironmentVariable("P1998_PROXY_HEADER_MS"), out var v) && v > 0 ? v : 5_000;

    /// <summary>Peers allowed to speak PROXY protocol, as addresses or CIDR blocks. Defaults to loopback
    /// (the same-box HAProxy case). A containerised proxy needs its bridge network added, e.g.
    /// P1998_PROXY_ALLOW=127.0.0.1/8,::1/128,172.16.0.0/12</summary>
    private static readonly (IPAddress Net, int Bits)[] Allow =
        ParseAllow(Environment.GetEnvironmentVariable("P1998_PROXY_ALLOW"));

    /// <summary>Human-readable allow-list, for the startup banner.</summary>
    public static string DescribeAllow =>
        Allow.Length == 0 ? "(none)" : string.Join(",", Allow.Select(a => $"{a.Net}/{a.Bits}"));

    /// <summary>May this peer send us a PROXY header? Checked before any read; see the class remarks for
    /// why this gate is the entire security model.</summary>
    public static bool IsTrustedPeer(IPAddress ip) => Matches(ip, Allow);

    /// <summary>Allow-list match against an explicit list rather than the environment's. Exists so the
    /// CIDR arithmetic — the part of this file most likely to be subtly wrong, and the part that decides
    /// whether an attacker can forge a source address — is testable without reaching into process
    /// environment state that is already frozen by the time a test runs.</summary>
    public static bool IsTrustedPeer(IPAddress ip, string allowList) => Matches(ip, ParseAllow(allowList));

    private static bool Matches(IPAddress ip, (IPAddress Net, int Bits)[] allow)
    {
        foreach (var (net, bits) in allow) if (InNetwork(ip, net, bits)) return true;
        return false;
    }

    /// <summary>
    /// Consume exactly one PROXY v2 header from the head of <paramref name="stream"/> and return the real
    /// client address, or null for the LOCAL command (health checks and the proxy's own probes carry no
    /// address — the caller should fall back to the peer address).
    ///
    /// Reads EXACTLY the header's bytes and not one more, so the stream is left positioned on the first
    /// game byte and the session that follows parses its protocol unchanged. Callers must pass an
    /// UNBUFFERED stream (NetworkStream is) — a buffering wrapper would read ahead into the game bytes and
    /// strand them where the session can never see them.
    /// </summary>
    public static async Task<IPAddress?> ReadHeaderAsync(Stream stream)
    {
        using var cts = new CancellationTokenSource(HeaderMs);
        var head = new byte[16];
        await stream.ReadExactlyAsync(head, cts.Token);

        for (int i = 0; i < Signature.Length; i++)
            if (head[i] != Signature[i])
                throw new InvalidDataException("bad PROXY v2 signature (is the proxy sending send-proxy-v2?)");

        // [12] = version (high nibble, must be 2) + command (low nibble: 0 LOCAL, 1 PROXY)
        if ((head[12] >> 4) != 0x2) throw new InvalidDataException($"unsupported PROXY version {head[12] >> 4}");
        int command = head[12] & 0x0F;

        // [13] = address family (high nibble: 1 IPv4, 2 IPv6) + transport (low nibble: 1 STREAM)
        int family = head[13] >> 4;

        // [14..15] = big-endian length of everything that follows: the address block plus any TLVs. Always
        // drain the full declared length even when the contents are unusable, otherwise the leftover bytes
        // would be handed to the game parser as if they were a packet.
        int len = (head[14] << 8) | head[15];
        var body = new byte[len];
        if (len > 0) await stream.ReadExactlyAsync(body, cts.Token);

        // LOCAL: the proxy is talking about itself (health check), not relaying a client. No address here.
        if (command == 0x0) return null;
        if (command != 0x1) throw new InvalidDataException($"unsupported PROXY command {command}");

        // Source address leads the block; the destination and ports follow and we have no use for them.
        return family switch
        {
            0x1 when len >= 12 => new IPAddress(body.AsSpan(0, 4).ToArray()),
            0x2 when len >= 36 => new IPAddress(body.AsSpan(0, 16).ToArray()),
            // AF_UNIX or AF_UNSPEC. Not an error — the connection is real, we just have no address for it,
            // so it is treated like LOCAL and falls back to the peer.
            _ => null,
        };
    }

    // ---- allow-list plumbing ---------------------------------------------------------------------------

    private static (IPAddress, int)[] ParseAllow(string? raw)
    {
        // Loopback covers the same-box HAProxy deployment, which is the default topology.
        if (string.IsNullOrWhiteSpace(raw)) raw = "127.0.0.0/8,::1/128";

        var list = new List<(IPAddress, int)>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var slash = part.IndexOf('/');
            var addrText = slash < 0 ? part : part[..slash];
            if (!IPAddress.TryParse(addrText, out var addr)) continue;

            int full = addr.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
            int bits = full;
            if (slash >= 0 && (!int.TryParse(part[(slash + 1)..], out bits) || bits < 0 || bits > full)) continue;
            list.Add((addr, bits));
        }
        return list.ToArray();
    }

    private static bool InNetwork(IPAddress ip, IPAddress net, int bits)
    {
        if (ip.AddressFamily != net.AddressFamily) return false;
        var a = ip.GetAddressBytes();
        var b = net.GetAddressBytes();

        int whole = bits / 8, rest = bits % 8;
        for (int i = 0; i < whole; i++) if (a[i] != b[i]) return false;
        if (rest == 0) return true;

        int mask = 0xFF << (8 - rest) & 0xFF;
        return (a[whole] & mask) == (b[whole] & mask);
    }
}
