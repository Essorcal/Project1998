using System.Collections.Concurrent;
using System.Net;

namespace Shared;

/// <summary>
/// Connection-admission control for a TCP front door — the app-layer half of DDoS resistance (the other
/// half is infra: upstream scrubbing, SYN-flood protection, a firewall on the game ports; see the plan).
/// Three cheap, lock-light gates, checked on every accept BEFORE a session is spawned:
///
///   1. Global cap (load-shedding): never run more than N concurrent connections on this process. Past the
///      ceiling we accept-then-close so an overload sheds load instead of exhausting sockets/threads/memory.
///   2. Per-IP concurrent cap: one address can't hold more than K live connections at once (a single-source
///      socket-exhaustion flood).
///   3. Per-IP connection RATE (fixed window): one address can't OPEN more than R connections per window (a
///      connect/disconnect churn flood, which the concurrent cap alone wouldn't catch).
///
/// Loopback is exempt from the per-IP and rate gates by default (local dev, the client test box, and a
/// same-box login->game hop all originate from 127.0.0.1 and must never be throttled); it still counts
/// toward the global cap so load-shedding stays uniform. All limits are env-tunable via <see cref="FromEnv"/>.
///
/// This is deliberately not a token bucket or a sliding log — under a flood the guard itself must stay O(1)
/// and bounded, so it uses interlocked counters + a fixed-window counter per IP, with the rate table capped
/// (it fails OPEN past the cap rather than growing unbounded, which would hand the attacker a memory-DoS).
/// </summary>
public sealed class ConnGuard
{
    private readonly int _globalMax;
    private readonly int _perIpMax;
    private readonly int _rateMax;
    private readonly long _rateWindowMs;
    private readonly bool _exemptLoopback;
    private readonly int _rateTableCap;

    private int _total;
    private readonly ConcurrentDictionary<IPAddress, int> _perIp = new();
    private readonly ConcurrentDictionary<IPAddress, Window> _rate = new();

    private sealed class Window { public long Start; public int Count; }

    public ConnGuard(int globalMax, int perIpMax, int rateMax, int rateWindowMs,
                     bool exemptLoopback = true, int rateTableCap = 100_000)
    {
        _globalMax = globalMax;
        _perIpMax = perIpMax;
        _rateMax = rateMax;
        _rateWindowMs = rateWindowMs;
        _exemptLoopback = exemptLoopback;
        _rateTableCap = rateTableCap;
    }

    /// <summary>Build a guard from environment variables, all with sane defaults so an unconfigured deploy
    /// is still protected. <paramref name="prefix"/> namespaces the vars per process (e.g. "LOGIN"/"GAME"):
    ///   NEXUS_&lt;prefix&gt;_MAXCONN (global, default 2000), _PERIP (default 8),
    ///   _RATE (opens per window, default 30), _RATEWIN_MS (window, default 10000),
    ///   _EXEMPT_LOOPBACK (default 1).
    ///   PERIP=8 is sized to reliably SUPPORT ~2 players per IP — 2 steady game sockets, the brief login->game
    ///   overlap where one player holds two, a lingering half-open "ghost" socket, plus margin — without being
    ///   a hard 2-player quota (enforcing that at the socket layer would falsely reject legit pairs; real
    ///   per-IP player limits belong in the login logic). Raise PERIP if you expect NAT'd IPs sharing more
    ///   players. RATE=30/10s already covers 2 players logging in / reconnecting with retries.</summary>
    public static ConnGuard FromEnv(string prefix)
    {
        int I(string k, int d) => int.TryParse(Environment.GetEnvironmentVariable($"NEXUS_{prefix}_{k}"), out var v) && v > 0 ? v : d;
        bool loop = (Environment.GetEnvironmentVariable($"NEXUS_{prefix}_EXEMPT_LOOPBACK") ?? "1").Trim() != "0";
        return new ConnGuard(I("MAXCONN", 2000), I("PERIP", 8), I("RATE", 30), I("RATEWIN_MS", 10_000), loop);
    }

    /// <summary>Try to admit a connection from <paramref name="ip"/>. On success the caller MUST pair it with
    /// exactly one <see cref="Release"/> when the connection ends. On failure nothing is reserved and
    /// <paramref name="reason"/> explains which gate rejected it (for logging); the caller should close the
    /// socket immediately.</summary>
    public bool TryAdmit(IPAddress ip, out string? reason)
    {
        if (!TryReserveGlobal(out reason)) return false;
        if (!BindIp(ip, out reason)) { ReleaseGlobal(); return false; }
        return true;
    }

    /// <summary>
    /// The global load-shed gate on its own, for the case where the real client address is not known yet.
    ///
    /// Behind a PROXY-protocol front end the address the per-IP gates need only arrives after the header
    /// has been read, and reading it means an await — which must not happen inline in the accept loop or
    /// one silent peer stalls every other connection. So the accept loop reserves the global slot here
    /// (keeping load-shedding at the cheapest point, before anything is spawned), and <see cref="BindIp"/>
    /// applies the per-IP and rate gates once the header has resolved. Pair with <see cref="ReleaseGlobal"/>
    /// if the header never arrives, or with <see cref="Release"/> once BindIp has succeeded.
    /// </summary>
    public bool TryReserveGlobal(out string? reason)
    {
        reason = null;
        if (Interlocked.Increment(ref _total) > _globalMax)
        {
            Interlocked.Decrement(ref _total);
            reason = $"global cap {_globalMax}";
            return false;
        }
        return true;
    }

    /// <summary>Apply the per-IP concurrent and rate gates to an already-reserved global slot. Never
    /// touches the global counter — on failure the caller releases it with <see cref="ReleaseGlobal"/>,
    /// which keeps this composable with <see cref="TryReserveGlobal"/> in either order of failure.</summary>
    public bool BindIp(IPAddress ip, out string? reason)
    {
        reason = null;
        bool exempt = _exemptLoopback && IPAddress.IsLoopback(ip);

        if (!exempt)
        {
            if (RateExceeded(ip))
            {
                reason = $"rate {_rateMax}/{_rateWindowMs}ms";
                return false;
            }
            // Per-IP concurrent cap.
            int cur = _perIp.AddOrUpdate(ip, 1, (_, v) => v + 1);
            if (cur > _perIpMax)
            {
                _perIp.AddOrUpdate(ip, 0, (_, v) => v - 1);
                reason = $"per-IP cap {_perIpMax}";
                return false;
            }
        }
        else
        {
            _perIp.AddOrUpdate(ip, 1, (_, v) => v + 1);   // track loopback too so Release stays symmetric
        }
        return true;
    }

    /// <summary>Give back a slot taken by <see cref="TryReserveGlobal"/> when no <see cref="BindIp"/> ever
    /// succeeded for it (a PROXY header that never arrived, or one that was rejected).</summary>
    public void ReleaseGlobal() => Interlocked.Decrement(ref _total);

    /// <summary>Release the reservation taken by a successful <see cref="TryAdmit"/>. Idempotency is the
    /// caller's job (call it once per admitted connection, in a finally).</summary>
    public void Release(IPAddress ip)
    {
        Interlocked.Decrement(ref _total);
        int left = _perIp.AddOrUpdate(ip, 0, (_, v) => Math.Max(0, v - 1));
        if (left <= 0) _perIp.TryRemove(ip, out _);   // bound the table to currently-connected addresses
    }

    public int Total => Volatile.Read(ref _total);

    // Fixed-window per-IP open-rate check. Returns true if this open would exceed the window budget.
    private bool RateExceeded(IPAddress ip)
    {
        // Fail OPEN if the rate table is saturated: growing it per distinct source IP under a spoofed/
        // distributed flood would be its own memory-DoS. The global + per-IP caps still apply.
        if (_rate.Count >= _rateTableCap && !_rate.ContainsKey(ip)) return false;

        long now = Environment.TickCount64;
        var w = _rate.GetOrAdd(ip, _ => new Window { Start = now, Count = 0 });
        lock (w)
        {
            if (now - w.Start >= _rateWindowMs) { w.Start = now; w.Count = 0; }   // window rolled over
            w.Count++;
            return w.Count > _rateMax;
        }
    }
}
