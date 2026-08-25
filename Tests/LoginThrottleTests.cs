using System.Net;
using Shared;
using Xunit;

namespace Tests;

/// <summary>
/// The per-IP failed-login throttle. The silent failure this guards against: a throttle that never
/// actually counts. That exact symptom was reported from live logs — three BadPassword rejections all
/// showing "(10 attempt(s) left)" — and turned out to be the loopback exemption doing its documented job,
/// but nothing distinguished "exempt, no budget" from "budget frozen". If RecordFailure ever stops
/// persisting per-IP state (a keying bug, a window that resets on every call), rejected logins still log,
/// clients still see the error message, and brute force just... works. Nothing throws.
///
/// LoginThrottle is static shared state, so every test uses its own TEST-NET address — budgets for
/// distinct IPs are independent (that independence is itself asserted below).
/// </summary>
public class LoginThrottleTests
{
    /// <summary>A unique documentation-range address (RFC 5737 TEST-NET-1) per call, so tests can't
    /// contaminate each other through the throttle's static table.</summary>
    private static int _next;
    private static IPAddress TestIp() => IPAddress.Parse($"192.0.2.{Interlocked.Increment(ref _next)}");

    [Fact]
    public void Failures_count_down_and_the_block_engages_at_zero()
    {
        var ip = TestIp();
        Assert.False(LoginThrottle.IsBlocked(ip));

        // Derive the budget from the first call rather than hardcoding 10 — P1998_LOGIN_FAILS can
        // legitimately resize it in the environment the tests run in.
        int first = LoginThrottle.RecordFailure(ip);
        int budget = first + 1;
        Assert.True(budget > 1, "budget must allow more than one attempt for this test to mean anything");

        for (int used = 2; used <= budget; used++)
        {
            Assert.False(LoginThrottle.IsBlocked(ip));   // still under budget before this attempt
            int left = LoginThrottle.RecordFailure(ip);
            Assert.Equal(budget - used, left);           // THE regression: a non-counting throttle stays at budget-1
        }

        Assert.True(LoginThrottle.IsBlocked(ip));        // budget spent -> refused before password verify
    }

    [Fact]
    public void A_successful_login_clears_the_counter()
    {
        var ip = TestIp();
        int first = LoginThrottle.RecordFailure(ip);
        LoginThrottle.RecordFailure(ip);
        LoginThrottle.RecordFailure(ip);

        LoginThrottle.RecordSuccess(ip);

        Assert.False(LoginThrottle.IsBlocked(ip));
        Assert.Equal(first, LoginThrottle.RecordFailure(ip));   // budget is fresh, not resumed at -3
    }

    [Fact]
    public void Distinct_addresses_have_independent_budgets()
    {
        var a = TestIp();
        var b = TestIp();
        int first = LoginThrottle.RecordFailure(a);
        LoginThrottle.RecordFailure(a);

        Assert.Equal(first, LoginThrottle.RecordFailure(b));    // b's budget untouched by a's failures
    }

    [Fact]
    public void An_ipv4_mapped_ipv6_peer_shares_the_plain_ipv4_budget()
    {
        // A dual-stack listener reports the same machine as ::ffff:a.b.c.d while a v4-bound listener
        // reports a.b.c.d. Keyed naively those are two dictionary entries — a doubled budget, and a
        // RecordSuccess on one listener that fails to clear the other's counter.
        var v4 = TestIp();
        var mapped = v4.MapToIPv6();
        Assert.True(mapped.IsIPv4MappedToIPv6);

        int first = LoginThrottle.RecordFailure(v4);
        Assert.Equal(first - 1, LoginThrottle.RecordFailure(mapped));

        LoginThrottle.RecordSuccess(mapped);
        Assert.Equal(first, LoginThrottle.RecordFailure(v4));   // cleared across both spellings
    }

    [Fact]
    public void Loopback_is_exempt_and_never_counts()
    {
        // This is the behaviour behind the live "(10 attempt(s) left), three times in a row" report:
        // loopback (local dev + the same-box login->game hop) is deliberately outside the throttle.
        if (!LoginThrottle.IsExempt(IPAddress.Loopback)) return;   // P1998_LOGIN_EXEMPT_LOOPBACK=0 here
        foreach (var ip in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback,
                                   IPAddress.Parse("::ffff:127.0.0.1") })
        {
            Assert.True(LoginThrottle.IsExempt(ip));
            int first = LoginThrottle.RecordFailure(ip);
            for (int i = 0; i < 30; i++) LoginThrottle.RecordFailure(ip);
            Assert.Equal(first, LoginThrottle.RecordFailure(ip));   // nothing accumulates
            Assert.False(LoginThrottle.IsBlocked(ip));
        }
    }
}
