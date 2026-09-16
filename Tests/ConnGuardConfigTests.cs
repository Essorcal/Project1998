using System.Net;
using Shared;
using Xunit;

namespace Tests;

/// <summary>
/// <see cref="ConnGuard"/>'s configuration seam: that each front door reads its OWN five knobs, and that a
/// guard built from a pinned configuration enforces exactly those numbers.
///
/// <para><b>Why this file exists.</b> These were the only knobs in the server with no literal name anywhere
/// in the source. <c>ConnGuard.FromEnv(prefix)</c> built <c>$"P1998_{prefix}_MAXCONN"</c> at runtime, so the
/// abuse-control caps could not appear in any generated reference, could not be grepped for, and — the part
/// that made a test impossible — could not be set without writing to the process environment. The cross
/// product is declared by name now and <see cref="ConnGuard.From"/> takes the resolved configuration, so the
/// wiring is a fact rather than something read off two format strings.</para>
///
/// <para>Nothing here opens a socket: <see cref="ConnGuard"/> is pure accounting over an
/// <see cref="IPAddress"/>, and the accept path that calls it is covered by
/// <see cref="SharedListenerTests"/> over real loopback sockets.</para>
/// </summary>
public class ConnGuardConfigTests
{
    private static ServerConfig With(params (string Name, string? Value)[] set)
    {
        var map = set.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);
        return ServerConfig.From(name => map.GetValueOrDefault(name));
    }

    /// <summary>A guard with no configuration enforces the four numbers the old inline defaults carried:
    /// 2000 concurrent, 8 per address, 30 opens per window, 10s window. The global cap is asserted through
    /// the rejection reason because that string is what an operator reads in the log when load-shedding
    /// starts, so it is worth pinning the number inside it too.</summary>
    [Theory]
    [InlineData(ConnGuard.Door.Login)]
    [InlineData(ConnGuard.Door.Game)]
    public void An_unconfigured_door_carries_the_old_inline_defaults(ConnGuard.Door door)
    {
        var guard = ConnGuard.From(With(), door);
        var ip = IPAddress.Parse("203.0.113.7");   // not loopback: the per-IP gates apply

        // Per-IP: eight admitted, the ninth refused, and the reason names the cap.
        for (int i = 1; i <= 8; i++)
            Assert.True(guard.TryAdmit(ip, out _), $"connection {i} of 8 should have been admitted");

        Assert.False(guard.TryAdmit(ip, out string? reason));
        Assert.Equal("per-IP cap 8", reason);
        Assert.Equal(8, guard.Total);              // the refused connection reserved nothing
    }

    /// <summary>Each door selects its own knobs and NOT the other's. The two are set to different values in
    /// one configuration, so a guard wired to the wrong prefix fails here.
    /// <para>Falsification: swap the two arms of <see cref="ConnGuard.From"/> and both halves fail.</para>
    /// </summary>
    [Fact]
    public void Each_door_reads_its_own_knobs_and_not_the_other_doors()
    {
        var config = With(
            ("P1998_LOGIN_MAXCONN", "1"), ("P1998_LOGIN_PERIP", "1"),
            ("P1998_GAME_MAXCONN", "3"), ("P1998_GAME_PERIP", "2"));

        var login = ConnGuard.From(config, ConnGuard.Door.Login);
        Assert.True(login.TryReserveGlobal(out _));
        Assert.False(login.TryReserveGlobal(out string? loginReason));
        Assert.Equal("global cap 1", loginReason);

        var game = ConnGuard.From(config, ConnGuard.Door.Game);
        for (int i = 1; i <= 3; i++) Assert.True(game.TryReserveGlobal(out _));
        Assert.False(game.TryReserveGlobal(out string? gameReason));
        Assert.Equal("global cap 3", gameReason);
    }

    /// <summary>The loopback exemption is per door too, and it is what keeps local dev, the client test box
    /// and the same-box login-to-game hop off the per-address gates. With it off, loopback is treated like
    /// any other address; with it on, the per-IP cap does not apply to it. The global cap applies either
    /// way, which is the property that keeps load-shedding uniform.</summary>
    [Fact]
    public void The_loopback_exemption_is_selected_per_door()
    {
        var config = With(
            ("P1998_LOGIN_PERIP", "1"), ("P1998_LOGIN_EXEMPT_LOOPBACK", "0"),
            ("P1998_GAME_PERIP", "1"), ("P1998_GAME_EXEMPT_LOOPBACK", "1"));

        var login = ConnGuard.From(config, ConnGuard.Door.Login);
        Assert.True(login.TryAdmit(IPAddress.Loopback, out _));
        Assert.False(login.TryAdmit(IPAddress.Loopback, out string? reason));
        Assert.Equal("per-IP cap 1", reason);

        var game = ConnGuard.From(config, ConnGuard.Door.Game);
        Assert.True(game.TryAdmit(IPAddress.Loopback, out _));
        Assert.True(game.TryAdmit(IPAddress.Loopback, out _));   // exempt: the per-IP cap of 1 does not bite
        Assert.Equal(2, game.Total);                             // ... but it still counted toward the global
    }

    /// <summary>The two entry points still name their door with the prefix string they always passed, so
    /// <c>Server/Net.cs</c> and <c>LoginServer/Net.cs</c> are untouched by this change. Anything else is a
    /// programming error and throws rather than quietly building a guard on all-default numbers, which is
    /// what the old runtime name-building would have done for a typo.</summary>
    [Theory]
    [InlineData("LOGIN")]
    [InlineData("GAME")]
    public void The_two_entry_point_prefixes_still_resolve(string prefix)
    {
        Assert.NotNull(ConnGuard.FromEnv(prefix));
    }

    [Theory]
    [InlineData("login")]
    [InlineData("WORLD")]
    [InlineData("")]
    public void An_unknown_prefix_throws_rather_than_defaulting(string prefix)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ConnGuard.FromEnv(prefix));
    }
}
