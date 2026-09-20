using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The natural-regeneration beat is the WORLD's, not each player's.
///
/// <para><b>What changed and why it is pinned here.</b> Until this pass each session carried its own 25 s
/// accumulator, reset at the topped-off check, so the first regen after damage was always exactly 25 s
/// later and a player chipped every 20 s never regenerated at all. The original game ran one global timer
/// (RTK <c>Player.regen</c> on <c>timerTick%50</c> at 0.5 s a tick): everyone below full heals in the same
/// moment, damage restarts nothing, and two players in a fight always tick together. Caleb decided on
/// 2026-09-19 to go back to that, and it is the one player-visible change in this PR — so it is written
/// down as a fact rather than left to a comment, including the edge that makes it a CHANGE (a player
/// damaged one beat before the boundary regenerates on that boundary, not 25 s later).</para>
///
/// <para>This also has to be exercised through the real <c>FlushTick</c>, because the clock lives beside
/// the beat and is advanced once per beat there; a fact that called <c>RegenTick</c> directly would pin
/// the parameter and not the cadence.</para>
/// </summary>
[Collection("world")]
public class RegenGlobalClockTests
{
    private readonly SessionFixture _fx;

    public RegenGlobalClockTests(SessionFixture fx) => _fx = fx;

    /// <summary>(d) The global clock: the first regen beat is the 76th beat of 333 ms after the clock
    /// starts, the next is 75 beats later, every player below full regenerates on the SAME beat, and damage
    /// taken one beat before a boundary does not push that player's regen 25 s out.
    ///
    /// <para>76 rather than 75 because 25,000 / 333 is 75.08: the beat fires on the first beat at or after
    /// the boundary. After it the clock carries the 308 ms remainder, which is why the following boundary is
    /// 75 beats on and not 76.</para>
    ///
    /// <para>Falsification: move the clock's advance INSIDE the per-player loop in <c>World.FlushTick</c>, so
    /// it is a per-player clock again. Run, confirm red on the same-beat assertion, restore by hand. The red
    /// is recorded in <c>briefs/reports/regen-early-out-opus.md</c>.</para></summary>
    [Fact]
    public void EveryPlayerBelowFullRegeneratesOnTheSameGlobalBeat()
    {
        // The arithmetic below is 25,000 / 333 rounded up. If the heartbeat is ever retuned this fact
        // should fail loudly rather than quietly assert the wrong beat number.
        Assert.Equal(333, ServerConfig.Current.TickMs);
        const int FirstBeat = 76;      // ceil(25,000 / 333)
        const int Interval  = 75;      // (25,000 - 308) / 333 rounded up, the steady-state gap

        var (a, _, ca) = _fx.PlayerWith(
            "RegenClockA", c => { c.MaxHp = 500; c.Hp = 100; c.MaxMp = 100; c.Mp = 10; },
            SessionFixture.HomeMap, x: 4, y: 13);
        var (b, _, cb) = _fx.PlayerWith(
            "RegenClockB", c => { c.MaxHp = 500; c.Hp = 100; c.MaxMp = 100; c.Mp = 10; },
            SessionFixture.HomeMap, x: 5, y: 13);
        try
        {
            _fx.World.RegenClockMsForTest = 0;   // start from a known position, not wherever the collection left it

            // ---- beats 1 to 75: nobody regenerates -------------------------------------------------
            for (int beat = 1; beat < FirstBeat; beat++)
            {
                Beat();
                Assert.True(ca.Hp == 100 && cb.Hp == 100,
                            $"a player regenerated on beat {beat}, before the first 25,000 ms boundary");
            }

            // ---- beat 76: BOTH regenerate, on the same beat ----------------------------------------
            Beat();
            Assert.True(ca.Hp > 100 && cb.Hp > 100,
                        $"the two players did not regenerate on the same beat (beat {FirstBeat}: "
                      + $"A {ca.Hp}, B {cb.Hp}) — the regen clock is not global");
            uint gained = ca.Hp - 100;
            Assert.Equal(gained, cb.Hp - 100);   // identical characters, identical gain

            // ---- the next boundary is 75 beats later, and damage in between does not move it -------
            ca.Hp = ca.MaxHp; ca.Mp = ca.MaxMp;      // both topped off, so nothing can regenerate
            cb.Hp = cb.MaxHp; cb.Mp = cb.MaxMp;
            for (int beat = 1; beat < Interval; beat++)
            {
                Beat();
                Assert.True(ca.Hp == ca.MaxHp && cb.Hp == cb.MaxHp,
                            $"a topped-off player's health moved on beat {FirstBeat + beat}");
            }

            // One beat before the boundary, B takes a hit. On the old per-player clock that reset B's
            // accumulator and B's first tick was 25 s away; on the world's clock B rides the beat that
            // was already coming.
            cb.Hp = 400;
            Beat();
            Assert.Equal(400u + gained, cb.Hp);
            Assert.Equal(ca.MaxHp, ca.Hp);   // A was topped off, so A still gets nothing
        }
        finally
        {
            _fx.World.LeaveMap(a, SessionFixture.HomeMap);
            _fx.World.LeaveMap(b, SessionFixture.HomeMap);
        }
    }

    /// <summary>One whole world beat through the real <c>FlushTick</c>, with nothing queued — the regen
    /// clock is advanced inside it, which is the point.</summary>
    private void Beat() => _fx.World.FlushTickForTest(new World.TickQueues());
}
