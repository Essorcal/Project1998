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

    /// <summary>A key as a 4.95 hand-off mints one: nine bytes, none zero, high bits set, the shape of the live
    /// check's <c>a1 b2 c3 d4 e5 f6 17 28 3b</c>.</summary>
    private static readonly byte[] Key = { 0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6, 0x17, 0x28, 0x3B };

    private static readonly byte[] Loopback = { 127, 0, 0, 1 };

    [Fact]
    public void Redirect_has_the_documented_field_layout()
    {
        var p = LoginRedirect.Build(new byte[] { 203, 0, 113, 7 }, 2005, "facetwo", TkCrypt.LoginKey, Nonce);

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
        var p = LoginRedirect.Build(new byte[] { 127, 0, 0, 1 }, 2005, user, Key, Nonce);
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
            LoginRedirect.Build(new byte[] { 127, 0, 0, 1 }, 2005, "facetwo", Key, new byte[len]));
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
    /// The drift guard. The exit-to-select bounce (game → login, "NexonInc.", zero tail) and the hand-off (login →
    /// game, a one-time key, a real nonce) must differ ONLY in the address, the nine key bytes and the five tail
    /// bytes: same width, same <c>23 00 09</c>, same name. If a future change to one path edits the struct, this
    /// catches it — which is the whole reason the builder was pulled out of both processes.
    /// </summary>
    [Fact]
    public void Bounce_and_handoff_differ_only_in_address_key_and_tail()
    {
        var toGame  = LoginRedirect.Build(Loopback, 2005, "facetwo", new HandoffTokens.Grant(2005, Nonce, Key));
        var toLogin = LoginRedirect.Build(Loopback, 2000, "facetwo", TkCrypt.LoginKey, new byte[LoginRedirect.TailBytes]);

        Assert.Equal(toGame.Length, toLogin.Length);
        Assert.Equal(toGame[..8], toLogin[..8]);         // frame + opcode + address
        Assert.Equal(toGame[10..13], toLogin[10..13]);   // 23 00 09
        Assert.Equal(toGame[22..30], toLogin[22..30]);   // name
        Assert.Equal(Key, toGame[13..22]);
        Assert.Equal("NexonInc."u8.ToArray(), toLogin[13..22]);
        Assert.Equal(new byte[] { 0x07, 0xD5 }, toGame[8..10]);    // 2005
        Assert.Equal(new byte[] { 0x07, 0xD0 }, toLogin[8..10]);   // 2000
    }

    /// <summary>The key field carries the bytes it is given, whole, and <c>klen</c> stays 9: a keyed hand-off is
    /// exactly as wide as the <c>"NexonInc."</c> one, which is what the client's fixed-size parse and the 23-byte
    /// <c>0x10</c> body depend on. Falsified by writing <see cref="TkCrypt.LoginKey"/> in place of the
    /// <c>key</c> argument (the builder before this change): the key assert fails.</summary>
    [Fact]
    public void Key_field_carries_the_given_bytes_and_klen_stays_nine()
    {
        var keyed = LoginRedirect.Build(Loopback, 2005, "elevenchars", Key, Nonce);
        var plain = LoginRedirect.Build(Loopback, 2005, "elevenchars", TkCrypt.LoginKey, Nonce);

        Assert.Equal(9, keyed[12]);
        Assert.Equal(Key, keyed[13..22]);
        Assert.Equal(plain.Length, keyed.Length);
        Assert.Equal(plain[..13], keyed[..13]);
        Assert.Equal(plain[22..], keyed[22..]);
        Assert.Equal(LoginRedirect.KeyBytes, TkCrypt.LoginKey.Length);   // the bounce's key fits the same field
    }

    /// <summary>The key field is fixed at nine bytes like the tail is at five: <c>klen</c> other than 9 was never
    /// tried against the client, so a key of another width is a caller's slip, refused before it reaches the wire.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(16)]
    public void Key_must_be_exactly_nine_bytes(int len)
    {
        Assert.Throws<ArgumentException>(() =>
            LoginRedirect.Build(new byte[] { 127, 0, 0, 1 }, 2005, "facetwo", new byte[len], Nonce));
    }

    /// <summary>A grant puts its key in the key field, and a grant with none (the 5.33 channel's) puts
    /// <c>"NexonInc."</c> there, as every hand-off did before. A grant for another port is refused: the redirect
    /// must name the port whose rule the grant was minted under, or the arrival would be checked by another.</summary>
    [Fact]
    public void A_grant_sends_its_key_or_NexonInc_and_only_to_its_own_port()
    {
        var keyed = LoginRedirect.Build(Loopback, 2005, "facetwo", new HandoffTokens.Grant(2005, Nonce, Key));
        var plain = LoginRedirect.Build(Loopback, 2006, "facetwo", new HandoffTokens.Grant(2006, Nonce, null));

        Assert.Equal(Key, keyed[13..22]);
        Assert.Equal(Nonce, keyed[^5..]);
        Assert.Equal("NexonInc."u8.ToArray(), plain[13..22]);
        Assert.Equal(Nonce, plain[^5..]);
        Assert.Throws<ArgumentException>(() =>
            LoginRedirect.Build(Loopback, 2006, "facetwo", new HandoffTokens.Grant(2005, Nonce, Key)));
    }
}
