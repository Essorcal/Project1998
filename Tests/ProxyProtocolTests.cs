using System.Net;
using Shared;
using Xunit;

namespace Tests;

/// <summary>
/// The PROXY v2 header parser and its allow-list. Both are hand-rolled byte/bit work sitting on the
/// internet-facing accept path, and both fail in ways that are invisible at runtime:
///
///   - a parser that consumes one byte too few or too many leaves the session's first game packet
///     corrupt, which surfaces as an unrelated protocol error much later;
///   - an allow-list that matches too widely lets any peer forge its source address, which surfaces as
///     nothing at all until someone abuses it.
///
/// So these test the byte layout exactly, and the CIDR arithmetic on both sides of every boundary.
/// </summary>
public class ProxyProtocolTests
{
    private static readonly byte[] Signature =
        { 0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A };

    /// <summary>Build a v2 header. <paramref name="trailer"/> stands in for the game bytes that follow it
    /// on a real connection — every test asserts they survive untouched.</summary>
    private static MemoryStream Header(byte verCmd, byte famProto, byte[] body, byte[]? trailer = null)
    {
        var ms = new MemoryStream();
        ms.Write(Signature);
        ms.WriteByte(verCmd);
        ms.WriteByte(famProto);
        ms.WriteByte((byte)(body.Length >> 8));
        ms.WriteByte((byte)(body.Length & 0xFF));
        ms.Write(body);
        if (trailer is not null) ms.Write(trailer);
        ms.Position = 0;
        return ms;
    }

    private static byte[] V4Body(byte[] src, byte[] dst, ushort sport, ushort dport)
    {
        var b = new List<byte>();
        b.AddRange(src); b.AddRange(dst);
        b.Add((byte)(sport >> 8)); b.Add((byte)(sport & 0xFF));
        b.Add((byte)(dport >> 8)); b.Add((byte)(dport & 0xFF));
        return b.ToArray();
    }

    // ---- the address, and the bytes after it ------------------------------------------------------------

    [Fact]
    public async Task Ipv4_header_yields_the_source_address()
    {
        var body = V4Body(new byte[] { 203, 0, 113, 9 }, new byte[] { 10, 0, 0, 1 }, 51234, 2005);
        var s = Header(0x21, 0x11, body);

        var ip = await ProxyProtocol.ReadHeaderAsync(s);

        Assert.Equal(IPAddress.Parse("203.0.113.9"), ip);
    }

    /// <summary>The guarantee the session downstream depends on: the parser stops on the boundary, so the
    /// first game byte is still there to be read. An off-by-one here is a protocol desync, not a crash.</summary>
    [Fact]
    public async Task Parser_consumes_the_header_and_not_one_byte_more()
    {
        var game = new byte[] { 0xAA, 0x00, 0x13, 0x7E };
        var body = V4Body(new byte[] { 198, 51, 100, 4 }, new byte[] { 10, 0, 0, 1 }, 40000, 2005);
        var s = Header(0x21, 0x11, body, game);

        await ProxyProtocol.ReadHeaderAsync(s);

        var rest = new byte[game.Length];
        Assert.Equal(game.Length, await s.ReadAsync(rest));
        Assert.Equal(game, rest);
        Assert.Equal(s.Length, s.Position);
    }

    /// <summary>HAProxy appends TLVs (ALPN, authority, its own health metadata) after the addresses. The
    /// declared length covers them, and every one must be drained — a leftover TLV byte would be handed to
    /// the game parser as though the client had sent it.</summary>
    [Fact]
    public async Task Tlv_tail_is_drained_before_the_game_bytes()
    {
        var game = new byte[] { 0x0A, 0x0B };
        var body = V4Body(new byte[] { 192, 0, 2, 33 }, new byte[] { 10, 0, 0, 1 }, 1234, 2005)
            .Concat(new byte[] { 0x03, 0x00, 0x04, 0xDE, 0xAD, 0xBE, 0xEF })   // one TLV: type, len, value
            .ToArray();
        var s = Header(0x21, 0x11, body, game);

        var ip = await ProxyProtocol.ReadHeaderAsync(s);

        Assert.Equal(IPAddress.Parse("192.0.2.33"), ip);
        var rest = new byte[game.Length];
        Assert.Equal(game.Length, await s.ReadAsync(rest));
        Assert.Equal(game, rest);
    }

    [Fact]
    public async Task Ipv6_header_yields_the_source_address()
    {
        var src = IPAddress.Parse("2001:db8::1").GetAddressBytes();
        var dst = IPAddress.Parse("2001:db8::2").GetAddressBytes();
        var body = src.Concat(dst).Concat(new byte[] { 0xC3, 0x50, 0x07, 0xD5 }).ToArray();
        var s = Header(0x21, 0x21, body);

        var ip = await ProxyProtocol.ReadHeaderAsync(s);

        Assert.Equal(IPAddress.Parse("2001:db8::1"), ip);
    }

    /// <summary>LOCAL is what HAProxy sends for its own health checks. It carries no address, and the
    /// listener falls back to the peer — which for a health check is the proxy, correctly.</summary>
    [Fact]
    public async Task Local_command_returns_null_rather_than_throwing()
    {
        var s = Header(0x20, 0x00, Array.Empty<byte>());
        Assert.Null(await ProxyProtocol.ReadHeaderAsync(s));
    }

    // ---- rejection --------------------------------------------------------------------------------------

    /// <summary>A trusted peer that is NOT sending PROXY protocol (a misconfigured HAProxy backend line,
    /// or the operator telnetting the port to check it is up). Must fail loudly at the header rather than
    /// silently feeding 16 bytes of someone's game traffic to the parser as an address.</summary>
    [Fact]
    public async Task Plain_traffic_is_rejected_not_misread()
    {
        var s = new MemoryStream(new byte[] { 0xAA, 0x00, 0x13, 0x7E, 0x1B, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });
        await Assert.ThrowsAsync<InvalidDataException>(() => ProxyProtocol.ReadHeaderAsync(s));
    }

    [Fact]
    public async Task Version_one_is_rejected()
    {
        // v1 is the text "PROXY TCP4 ..." grammar; a v1-configured proxy must not be silently half-parsed.
        var s = Header(0x11, 0x11, V4Body(new byte[] { 1, 1, 1, 1 }, new byte[] { 2, 2, 2, 2 }, 1, 2));
        await Assert.ThrowsAsync<InvalidDataException>(() => ProxyProtocol.ReadHeaderAsync(s));
    }

    [Fact]
    public async Task Truncated_body_is_rejected()
    {
        // Declares 12 bytes of address block, supplies 4.
        var ms = new MemoryStream();
        ms.Write(Signature);
        ms.Write(new byte[] { 0x21, 0x11, 0x00, 0x0C, 1, 2, 3, 4 });
        ms.Position = 0;
        await Assert.ThrowsAsync<EndOfStreamException>(() => ProxyProtocol.ReadHeaderAsync(ms));
    }

    // ---- the allow-list ---------------------------------------------------------------------------------

    [Theory]
    // Loopback default, both directions of the /8 boundary.
    [InlineData("127.0.0.1", "127.0.0.0/8", true)]
    [InlineData("127.255.255.254", "127.0.0.0/8", true)]
    [InlineData("128.0.0.1", "127.0.0.0/8", false)]
    [InlineData("126.255.255.255", "127.0.0.0/8", false)]
    // The docker-bridge case from the class docs: 172.16/12 spans 172.16 through 172.31 and stops there.
    [InlineData("172.16.0.1", "172.16.0.0/12", true)]
    [InlineData("172.31.255.255", "172.16.0.0/12", true)]
    [InlineData("172.32.0.1", "172.16.0.0/12", false)]
    [InlineData("172.15.255.255", "172.16.0.0/12", false)]
    // A bare address is a single host, not a network.
    [InlineData("10.0.0.5", "10.0.0.5", true)]
    [InlineData("10.0.0.6", "10.0.0.5", false)]
    // Multiple entries, and whitespace around them.
    [InlineData("10.0.0.6", "127.0.0.0/8, 10.0.0.0/24", true)]
    // Families must not cross-match.
    [InlineData("::1", "127.0.0.0/8", false)]
    [InlineData("::1", "::1/128", true)]
    public void Allow_list_matches_exactly_at_the_boundaries(string ip, string allow, bool expected)
        => Assert.Equal(expected, ProxyProtocol.IsTrustedPeer(IPAddress.Parse(ip), allow));

    /// <summary>An empty or absent allow-list must fall back to loopback, never to "everything" — the
    /// direction of this default is the difference between a misconfiguration that drops connections and
    /// one that lets anyone forge a source address.</summary>
    [Fact]
    public void Empty_allow_list_falls_back_to_loopback_only()
    {
        Assert.True(ProxyProtocol.IsTrustedPeer(IPAddress.Loopback, ""));
        Assert.False(ProxyProtocol.IsTrustedPeer(IPAddress.Parse("203.0.113.1"), ""));
    }

    /// <summary>A malformed entry is skipped rather than widening the list. "not-an-ip" and an out-of-range
    /// prefix both have to vanish, leaving only the valid entry in force.</summary>
    [Fact]
    public void Malformed_entries_are_dropped_not_widened()
    {
        const string allow = "not-an-ip,10.0.0.0/99,192.168.1.0/24";
        Assert.True(ProxyProtocol.IsTrustedPeer(IPAddress.Parse("192.168.1.7"), allow));
        Assert.False(ProxyProtocol.IsTrustedPeer(IPAddress.Parse("10.0.0.1"), allow));
        Assert.False(ProxyProtocol.IsTrustedPeer(IPAddress.Parse("203.0.113.1"), allow));
    }
}
