using System.Collections.Concurrent;
using System.Net;

namespace Shared;

/// <summary>
/// Per-IP failed-login throttle — the online-guessing half of the auth story that <see cref="Auth"/>'s
/// hashing note has been pointing at.
///
/// Why this is not optional here: the 4.95 client caps a password at 3-8 characters (RTK login/clif.c), so
/// the wire secret is genuinely low-entropy. BCrypt protects the at-rest database, but nothing stopped a
/// client from opening ONE connection and firing 0x03 login attempts at line rate forever —
/// <see cref="ConnGuard"/> rate-limits how often an address may CONNECT, not what it does once connected.
/// A few thousand guesses per minute cracks a 4-digit password in the time it takes to make coffee.
///
/// Policy: count failures per source IP in a rolling window. Past the budget every further attempt is
/// refused outright — without touching the password hash, so the expensive BCrypt verify isn't a free CPU
/// sink for the attacker either — until the window expires. A SUCCESSFUL login clears that IP's counter,
/// so a real player who fat-fingers their password a few times and then gets it right is never left in a
/// penalty box. Loopback is exempt (local dev + the same-box login->game hop).
///
/// Deliberately per-IP and not per-account: locking an ACCOUNT out on failed attempts would hand anyone a
/// way to deny a specific player access by guessing at their name on purpose.
/// </summary>
public static class LoginThrottle
{
    private static readonly int MaxFails =
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_LOGIN_FAILS"), out var f) && f > 0 ? f : 10;
    private static readonly long WindowMs =
        long.TryParse(Environment.GetEnvironmentVariable("NEXUS_LOGIN_FAIL_WINDOW_MS"), out var w) && w > 0 ? w : 300_000;
    private static readonly bool ExemptLoopback =
        (Environment.GetEnvironmentVariable("NEXUS_LOGIN_EXEMPT_LOOPBACK") ?? "1").Trim() != "0";

    private sealed class Window { public long Start; public int Fails; }
    private static readonly ConcurrentDictionary<IPAddress, Window> Table = new();

    // Bound the table so a spoofed-source flood can't turn the throttle itself into a memory DoS. Past the
    // cap a new address simply isn't tracked (fails open) — ConnGuard's global/per-IP caps still apply.
    private const int TableCap = 100_000;

    /// <summary>True if <paramref name="ip"/> has burned through its failure budget and should be refused
    /// WITHOUT verifying the password. Also sweeps the window if it has rolled over.</summary>
    public static bool IsBlocked(IPAddress ip)
    {
        if (ExemptLoopback && IPAddress.IsLoopback(ip)) return false;
        if (!Table.TryGetValue(ip, out var w)) return false;
        lock (w)
        {
            if (Environment.TickCount64 - w.Start >= WindowMs) { w.Start = Environment.TickCount64; w.Fails = 0; }
            return w.Fails >= MaxFails;
        }
    }

    /// <summary>Record one failed attempt. Returns the number of tries left before the block engages
    /// (0 = now blocked), purely for the log line.</summary>
    public static int RecordFailure(IPAddress ip)
    {
        if (ExemptLoopback && IPAddress.IsLoopback(ip)) return MaxFails;
        if (Table.Count >= TableCap && !Table.ContainsKey(ip)) return MaxFails;
        var w = Table.GetOrAdd(ip, _ => new Window { Start = Environment.TickCount64, Fails = 0 });
        lock (w)
        {
            if (Environment.TickCount64 - w.Start >= WindowMs) { w.Start = Environment.TickCount64; w.Fails = 0; }
            w.Fails++;
            return Math.Max(0, MaxFails - w.Fails);
        }
    }

    /// <summary>Clear this address's counter after a successful login, so an honest player's earlier typos
    /// don't accumulate toward a lockout across a whole session.</summary>
    public static void RecordSuccess(IPAddress ip) => Table.TryRemove(ip, out _);

    /// <summary>The refusal message shown to a blocked client. Says nothing about which half (name or
    /// password) was wrong.</summary>
    public static string BlockedMessage => "Too many failed attempts. Please wait and try again.";
}
