using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The lock-free pre-check in <see cref="Session.RegenTick"/>: the method decides whether any of its four
/// jobs is due this beat before it takes the session monitor, and enters only then.
///
/// <para><b>Why this needs tests by the "would this fail loudly?" rule.</b> Every failure mode is silent.
/// Put the pre-check back under the monitor and nothing is wrong except that the tick thread waits behind
/// 400 session monitors a beat again, which no test and no log line would show. Drop one term of the
/// <c>due</c> expression and a buff simply never fades, or a fury never charges its price, or the mail
/// arrow never appears, or nobody ever regenerates — no throw, no error line, just a game that quietly
/// stops doing one of the four things this method exists for. So there is a fact per term, and each one is
/// falsified by dropping exactly its term.</para>
/// </summary>
[Collection("world")]
public class RegenEarlyOutTests
{
    /// <summary>Long enough that a blocked call is unmistakable, short enough that a red does not stall the
    /// suite.</summary>
    private const int HoldMs = 3_000;

    /// <summary>What the no-op path may take while another thread owns the monitor. Two orders of magnitude
    /// under <see cref="HoldMs"/>, so the fact cannot pass by being slow.</summary>
    private const int NoOpBudgetMs = 250;

    private readonly SessionFixture _fx;

    public RegenEarlyOutTests(SessionFixture fx) => _fx = fx;

    /// <summary>(a) A player with no buff due, no fury, a fresh mail accumulator and no regen beat is ticked
    /// without entering their session monitor at all: with a background thread holding that monitor for
    /// three seconds, <c>RegenTick</c> returns on the test thread inside a quarter of a second.
    ///
    /// <para>Falsification: move the <c>due</c> pre-check back under <c>EnterState()</c>. Run, confirm red,
    /// restore by hand. Recorded in <c>briefs/reports/regen-early-out-opus.md</c>.</para>
    ///
    /// <para>The holder is released in a <c>finally</c>, so a red never leaves a monitor owned by a dead
    /// test's thread.</para></summary>
    [Fact]
    public void TheNoOpPathEntersNoMonitor()
    {
        var (idle, _) = _fx.Player("RegenEarlyOutIdle", SessionFixture.HomeMap, x: 6, y: 13);
        using var holding = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            using var _ = idle.EnterState();
            holding.Set();
            release.Wait(HoldMs * 2);
        }) { IsBackground = true, Name = "RegenEarlyOutHolder" };
        Thread? sweeps = null;

        try
        {
            holder.Start();
            Assert.True(holding.Wait(HoldMs), "the background thread never took the monitor");
            Assert.Equal(long.MaxValue, idle.NextBuffExpiryForTest);   // nothing due: no buff…

            Exception? thrown = null;
            var sweep = new Thread(() =>
            {
                try { idle.RegenTick(333, regenDue: false); }         // …no fury, no regen beat, fresh mail
                catch (Exception e) { thrown = e; }
            }) { IsBackground = true, Name = "RegenEarlyOutSweep" };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            sweep.Start();
            bool done = sweep.Join(NoOpBudgetMs);
            sw.Stop();

            Assert.True(done,
                $"RegenTick did not return in {NoOpBudgetMs} ms (waited {sw.ElapsedMilliseconds} ms) with "
              + "another thread holding the session monitor — the no-op path is entering the monitor");
            Assert.Null(thrown);
            sweeps = sweep;
        }
        finally
        {
            release.Set();
            holder.Join(HoldMs * 2);
            sweeps?.Join(HoldMs * 2);   // a red leaves it blocked on the monitor; let it finish after the release
            _fx.World.LeaveMap(idle, SessionFixture.HomeMap);
        }
    }

    /// <summary>(b1) The buff term. A buff still fades on the first <c>RegenTick</c> at or after its expiry,
    /// with its live fade line and the refreshed stats packet, and not before.
    ///
    /// <para>Falsification: drop <c>Volatile.Read(ref _nextBuffExpiry) &lt;= Environment.TickCount64</c>
    /// from <c>due</c>. The buff never fades. Run, confirm red, restore by hand.</para></summary>
    [Fact]
    public void ABuffStillFadesOnTheFirstBeatAtOrAfterItsExpiry()
    {
        var (s, outbound, _) = _fx.PlayerWith("RegenEarlyOutFade", c => { c.Hp = 500; c.MaxHp = 500; },
                                              SessionFixture.HomeMap, x: 7, y: 13);
        try
        {
            s.ReceiveCurse("might", 1, 250, "might_mage", "Might", "");
            Assert.True(s.NextBuffExpiryForTest < long.MaxValue, "the hint did not pick the buff up");
            outbound.Clear();

            // Before the expiry: nothing fades, however many beats run.
            for (int i = 0; i < 5; i++) s.RegenTick(0, regenDue: false);
            Assert.Empty(MiniTexts(outbound));

            Assert.True(
                SpinWait.SpinUntil(() => { s.RegenTick(0, regenDue: false); return MiniTexts(outbound).Count > 0; }, 5_000),
                "the buff never faded — RegenTick is not entering the monitor on the beat its expiry passes");
            Assert.Contains("Your strength returns to normal.", MiniTexts(outbound));
            Assert.NotEmpty(outbound.BodiesOf(ServerOp.Stats));   // ExpireBuffs pushes the dropped caps to the HUD
            Assert.Equal(long.MaxValue, s.NextBuffExpiryForTest);
        }
        finally
        {
            _fx.World.LeaveMap(s, SessionFixture.HomeMap);
        }
    }

    /// <summary>(b2) The mail term. The 30 s mail backstop still fires, on the first beat at or after the
    /// accumulator crosses <c>MailBackstopMs</c> and not before: 30,000 / 333 is 90.1, so the 91st call is
    /// the one that re-reads the mailbox and pushes the arrow.
    ///
    /// <para>The observation is the stats packet the refresh sends when the flag CHANGES, which is the seam
    /// <c>RefreshMailFlags</c> actually has — so the letter is posted after the session exists and the
    /// recorder is cleared, and the flag therefore goes 0 to 0x10 on the backstop beat.</para>
    ///
    /// <para>Falsification: drop <c>_mailAccum &gt;= MailBackstopMs</c> from <c>due</c>. The backstop never
    /// runs. Run, confirm red, restore by hand.</para></summary>
    [Fact]
    public void TheThirtySecondMailBackstopStillFires()
    {
        const string Name = "RegenEarlyOutMail";
        var (s, outbound, _) = _fx.PlayerWith(Name, c => { c.Hp = 500; c.MaxHp = 500; },
                                              SessionFixture.HomeMap, x: 8, y: 13);
        try
        {
            Mail.Send(Name, "Postmaster", "A letter", "Body", 1, 2);
            outbound.Clear();

            for (int i = 0; i < 90; i++) s.RegenTick(333, regenDue: false);
            Assert.Empty(outbound.BodiesOf(ServerOp.Stats));   // 29,970 ms: not yet

            s.RegenTick(333, regenDue: false);                 // 30,303 ms: the backstop beat
            Assert.NotEmpty(outbound.BodiesOf(ServerOp.Stats));
        }
        finally
        {
            _fx.World.LeaveMap(s, SessionFixture.HomeMap);
        }
    }

    /// <summary>(b3) The fury term. A Chung Ryong fury still wears off on the first beat at or after
    /// <c>_rageUntil</c>, charging its vita price.
    ///
    /// <para>Tier 1 with no AC on purpose: the tier's keyed AC buff would put an entry on <c>_buffs</c> and
    /// the buff term would carry the beat, which is the one thing this fact must not let happen.</para>
    ///
    /// <para>Falsification: drop <c>Volatile.Read(ref _crRageTier) &gt; 0</c> from <c>due</c>. The fury
    /// never wears off and never charges. Run, confirm red, restore by hand.</para></summary>
    [Fact]
    public void AChungRyongFuryStillWearsOff()
    {
        var (s, outbound, c) = _fx.PlayerWith("RegenEarlyOutFury", ch => { ch.Hp = 500; ch.MaxHp = 500; },
                                              SessionFixture.HomeMap, x: 9, y: 13);
        try
        {
            s.WithState(() => s.LuaSetCrRage(tier: 1, mult: 2, ac: 0, durMs: 1, name: "Chung Ryong's Rage"));
            Assert.Equal(1, s.LuaCrRageTier);
            Assert.Equal(long.MaxValue, s.NextBuffExpiryForTest);   // no AC buff: only the fury term can fire
            outbound.Clear();

            Assert.True(
                SpinWait.SpinUntil(() => { s.RegenTick(0, regenDue: false); return s.LuaCrRageTier == 0; }, 5_000),
                "the fury never wore off — RegenTick is not entering the monitor while a tier is up");
            Assert.Contains("Chung Ryong's rage leaves you drained.", MiniTexts(outbound));
            Assert.Equal(400u, c.Hp);   // tier 1 costs 20% of vita
        }
        finally
        {
            _fx.World.LeaveMap(s, SessionFixture.HomeMap);
        }
    }

    /// <summary>(b4) The regen term. A damaged player regenerates on a beat the world's clock says is due,
    /// by the same 2% of max scaled by Grace and Will as before, and on no other beat.
    ///
    /// <para>Falsification: drop <c>regenDue</c> from <c>due</c>. Nobody regenerates. Run, confirm red,
    /// restore by hand.</para></summary>
    [Fact]
    public void ADamagedPlayerRegeneratesOnlyOnAGlobalBeat()
    {
        var (s, _, c) = _fx.PlayerWith(
            "RegenEarlyOutGain", ch => { ch.MaxHp = 500; ch.Hp = 100; ch.MaxMp = 100; ch.Mp = 10; },
            SessionFixture.HomeMap, x: 10, y: 13);
        try
        {
            for (int i = 0; i < 20; i++) s.RegenTick(0, regenDue: false);
            Assert.Equal(100u, c.Hp);
            Assert.Equal(10u, c.Mp);

            s.RegenTick(0, regenDue: true);
            // ceil(500 * 0.02 * (1 + Grace 3 / 100)) = ceil(10.3) = 11
            // ceil(100 * 0.02 * (1 + Will  3 / 100)) = ceil(2.06) = 3
            Assert.Equal(111u, c.Hp);
            Assert.Equal(13u, c.Mp);

            for (int i = 0; i < 20; i++) s.RegenTick(0, regenDue: false);
            Assert.Equal(111u, c.Hp);
            Assert.Equal(13u, c.Mp);
        }
        finally
        {
            _fx.World.LeaveMap(s, SessionFixture.HomeMap);
        }
    }

    /// <summary>The mini-text lines this recorder was sent. The 0x0A body is
    /// <c>type(u8) | len(u16 BE) | ascii[len]</c> — see <c>Session.SendMiniText</c>.</summary>
    private static List<string> MiniTexts(RecordingOutbound outbound) =>
        outbound.BodiesOf(ServerOp.MiniText)
                .Where(b => b.Length >= 3)
                .Select(b => System.Text.Encoding.ASCII.GetString(b, 3, Math.Min((b[1] << 8) | b[2], b.Length - 3)))
                .ToList();
}
