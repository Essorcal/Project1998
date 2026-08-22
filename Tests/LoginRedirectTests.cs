using Protocol.Tk495;
using Shared;
using Xunit;

namespace Tests;

/// <summary>
/// The `0x03` redirect struct and the login/game channel pairing.
///
/// These exist because neither one fails loudly. The client parses the redirect as a FIXED-SIZE record and
/// then acts on it silently: a reversed octet, a little-endian port, a tail of the wrong width or a byte off
/// in the `23 00 09` / "NexonInc." constants does not throw anywhere — the client just connects to the wrong
/// place, or to nowhere, and sits on a screen forever. That is exactly how the character-creation hang
/// presented (game server never answered `0x0B`, so the client stayed on the game socket where creation is
/// not implemented), and it cost a debugging session to even locate.
///
/// Layout under test: Protocol.md §4.1. Three call sites build this packet — the login handoff, the game
/// re-login fallback, and the exit-to-select bounce — and they now share one builder precisely so they cannot
/// disagree; the last test here is the guard on that.
/// </summary>
public class LoginRedirectTests
{
    private static readonly byte[] Nonce = { 0x01, 0x12, 0x11, 0x2A, 0x3B };

    [Fact]
    public void Redirect_has_the_documented_field_layout()
    {
        var p = LoginRedirect.Build(new byte[] { 203, 0, 113, 7 }, 2005, "facetwo", Nonce);

        Assert.Equal(0xAA, p[0]);                       // frame start
        Assert.Equal(0x00, p[1]);                       // length high byte (this packet never reaches 256B)
        Assert.Equal(p.Length - 3, p[2]);               // length excludes AA + the two length bytes
        Assert.Equal(Opcode.Login, p[3]);               // 0x03

        // IP octets REVERSED on the wire: 203.0.113.7 -> 07 71 00 CB.
        Assert.Equal(new byte[] { 7, 113, 0, 203 }, p[4..8]);

        // Port big-endian: 2005 = 0x07D5.
        Assert.Equal(new byte[] { 0x07, 0xD5 }, p[8..10]);

        // The three constants observed in the working handoff, then the echoed key string.
        Assert.Equal(new byte[] { 23, 0, 9 }, p[10..13]);
        Assert.Equal("NexonInc."u8.ToArray(), p[13..22]);

        // Length-prefixed username, then the 5-byte tail.
        Assert.Equal((byte)"facetwo".Length, p[22]);
        Assert.Equal("facetwo"u8.ToArray(), p[23..30]);
        Assert.Equal(Nonce, p[30..35]);
        Assert.Equal(35, p.Length);
    }

    /// <summary>
    /// The client copies `nameLen name tail` into ONE fixed 13-byte NUL-terminated field, so the packet width
    /// tracks the name length exactly — nothing pads it back out. A stale hardcoded length byte would send a
    /// client that logs in fine and then hangs only for players whose names are a different length, which is
    /// the worst possible shape for a bug like this.
    /// </summary>
    [Theory]
    [InlineData("ab")]
    [InlineData("facetwo")]
    [InlineData("eightchr")]
    [InlineData("elevenchars")]
    public void Length_byte_tracks_the_username(string user)
    {
        var p = LoginRedirect.Build(new byte[] { 127, 0, 0, 1 }, 2005, user, Nonce);
        Assert.Equal(p.Length - 3, p[2]);
        Assert.Equal(23 + user.Length + LoginRedirect.TailBytes, p.Length);
    }

    /// <summary>
    /// A 16-byte token was live-proven to corrupt the client's parse and break login outright (Protocol.md
    /// §4.1). Growing the tail is therefore a change that must be deliberate, not a slip in a caller.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(16)]
    public void Tail_must_be_exactly_five_bytes(int len)
    {
        Assert.Throws<ArgumentException>(() =>
            LoginRedirect.Build(new byte[] { 127, 0, 0, 1 }, 2005, "facetwo", new byte[len]));
    }

    /// <summary>
    /// Login 2000 ↔ game 2005 is V495; login 2001 ↔ game 2006 is V533. The server stamps the client version
    /// from the arrival port instead of sniffing the wire, so crossing the channels hands a 5.33 client to the
    /// 4.95 code path — a black screen, not an exception. Both directions are in use now: login hands off
    /// outward, and exit-to-select (0x0B) bounces back.
    /// </summary>
    [Fact]
    public void Channel_pairing_round_trips()
    {
        Assert.Equal(2005, ChannelPorts.GameFor(2000));
        Assert.Equal(2006, ChannelPorts.GameFor(2001));
        Assert.Equal(2000, ChannelPorts.LoginFor(2005));
        Assert.Equal(2001, ChannelPorts.LoginFor(2006));

        foreach (var login in new[] { 2000, 2001 })
            Assert.Equal(login, ChannelPorts.LoginFor(ChannelPorts.GameFor(login)));
        foreach (var game in new[] { 2005, 2006 })
            Assert.Equal(game, ChannelPorts.GameFor(ChannelPorts.LoginFor(game)));
    }

    /// <summary>
    /// The drift guard. The exit-to-select bounce (game → login, zero tail) and the handoff (login → game,
    /// real nonce) must differ ONLY in the address and those five bytes. If a future change to one path edits
    /// the struct, this catches it — which is the whole reason the builder was pulled out of both processes.
    /// </summary>
    [Fact]
    public void Bounce_and_handoff_differ_only_in_address_and_tail()
    {
        var toGame  = LoginRedirect.Build(new byte[] { 127, 0, 0, 1 }, 2005, "facetwo", Nonce);
        var toLogin = LoginRedirect.Build(new byte[] { 127, 0, 0, 1 }, 2000, "facetwo", new byte[LoginRedirect.TailBytes]);

        Assert.Equal(toGame.Length, toLogin.Length);
        Assert.Equal(toGame[..8], toLogin[..8]);         // frame + opcode + address
        Assert.Equal(toGame[10..30], toLogin[10..30]);   // constants, key, name — identical
        Assert.Equal(new byte[] { 0x07, 0xD5 }, toGame[8..10]);    // 2005
        Assert.Equal(new byte[] { 0x07, 0xD0 }, toLogin[8..10]);   // 2000
    }
}
