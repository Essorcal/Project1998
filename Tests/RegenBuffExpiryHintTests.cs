using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The exact lock-free hint that tells <see cref="Session.RegenTick"/> whether a buff is due to fade this
/// beat, without taking the session monitor to find out.
///
/// <para><b>Why this needs a test by the "would this fail loudly?" rule.</b> Every failure mode is silent.
/// A writer that forgets to refresh the hint leaves a buff that never fades: no throw, no log line, just a
/// Might that lasts forever and a fade line the player never reads. A hint that is too EARLY costs nothing
/// but a wasted monitor entry, which no test would ever show either. So the fact pins the one property the
/// early-out depends on — the hint is the real minimum after every writer, never an estimate.</para>
/// </summary>
[Collection("world")]
public class RegenBuffExpiryHintTests
{
    private readonly SessionFixture _fx;

    public RegenBuffExpiryHintTests(SessionFixture fx) => _fx = fx;

    /// <summary>(c) The hint equals the minimum <c>Expires</c> in <c>_buffs</c> after each of the four
    /// writers, and <c>long.MaxValue</c> when the list is empty — reached through the real appliers, not by
    /// writing the list.
    ///
    /// <list type="bullet">
    /// <item><c>BuffAdd</c>: two curses with different durations, through <c>ReceiveCurse</c>.</item>
    /// <item><c>BuffRemoveAll</c>: a cure by category, which removes without adding anything back.</item>
    /// <item><c>BuffRemoveAt</c>: the expiry pass in <c>RegenTick</c> dropping a lapsed entry.</item>
    /// <item><c>BuffClear</c>: <c>DispelSelf</c>, which strips every timed effect at once.</item>
    /// </list>
    ///
    /// <para>Every step asserts the hint against the expiry of a SURVIVING entry, never merely that it
    /// moved, because a stale hint is a live value and "not the old one" is cheap to satisfy by accident.
    /// The first shape of this fact asserted only that the hint had become a time in the future, and
    /// <c>Environment.TickCount64</c>'s 15 ms granularity satisfied that from the lapsed value itself: the
    /// <c>BuffRemoveAt</c> and <c>BuffRemoveAll</c> falsifications both stayed green and the test was
    /// rewritten rather than recorded.</para>
    ///
    /// <para>Falsification: drop the <c>RecomputeNextBuffExpiry()</c> call from any ONE of the four writers.
    /// Four reds, one per writer, recorded in <c>briefs/reports/regen-early-out-opus.md</c>. Restore by
    /// hand.</para></summary>
    [Fact]
    public void TheBuffExpiryHintIsTheRealMinimumAfterEveryWriter()
    {
        var (s, _, _) = _fx.PlayerWith("RegenHint", c => { c.Hp = 500; c.MaxHp = 500; },
                                       SessionFixture.HomeMap, x: 4, y: 12);
        try
        {
            // Nothing on the list yet: no monitor entry can be owed to a buff.
            Assert.Equal(long.MaxValue, s.NextBuffExpiryForTest);

            // ---- BuffAdd: the hint is the EARLIER of the two ---------------------------------------
            long t0 = Environment.TickCount64;
            s.ReceiveCurse("might", 1, 60_000, "hint_late", "late", "");
            s.ReceiveCurse("might", 1, 30_000, "hint_early", "early", "curses");
            long t1 = Environment.TickCount64;
            Assert.InRange(s.NextBuffExpiryForTest, t0 + 30_000, t1 + 30_000);

            // ---- BuffRemoveAll: a cure by category takes the earlier entry off the list and puts
            // ---- nothing back, so the hint has to fall back to the later one -----------------------
            // LuaCureCategory is a Lua verb on the caster's own session and has no monitor of its own;
            // its real caller runs under Handle's WithState, so the fact enters the state the same way.
            Assert.Equal(1, s.WithState(() => s.LuaCureCategory("curses")));
            Assert.InRange(s.NextBuffExpiryForTest, t0 + 60_000, t1 + 60_000);

            // ---- BuffRemoveAt: an entry that lapses at once, dropped by RegenTick's expiry pass -----
            s.ReceiveCurse("might", 1, 1, "hint_gone", "gone", "");
            Assert.True(s.NextBuffExpiryForTest <= t1 + 1_000,
                        "the immediately-lapsing entry did not become the hint");
            Assert.True(
                SpinWait.SpinUntil(
                    () => { Tick(s); return s.NextBuffExpiryForTest >= t0 + 60_000; }, 2_000),
                "ExpireBuffs never dropped the lapsed entry, or BuffRemoveAt left the hint on it");
            Assert.InRange(s.NextBuffExpiryForTest, t0 + 60_000, t1 + 60_000);

            // ---- BuffClear: nothing on the list, nothing owed --------------------------------------
            // DispelSelf has no monitor of its own either, and for the same reason: "@dispel" runs under
            // Handle's WithState.
            s.WithState(s.DispelSelf);
            Assert.Equal(long.MaxValue, s.NextBuffExpiryForTest);
        }
        finally
        {
            _fx.World.LeaveMap(s, SessionFixture.HomeMap);
        }
    }

    /// <summary>One beat of the regen step for this session, in whichever shape the branch carries.</summary>
    private static void Tick(Session s) => s.RegenTick(0);
}
