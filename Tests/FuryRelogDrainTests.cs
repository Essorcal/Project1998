using System.Reflection;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Chung Ryong's Rage cannot be dodged by logging out — the capture/restore half of the rule whose other
/// half is <see cref="Session.RegenTick"/>'s fury term (covered by <c>RegenEarlyOutTests</c> (b3)).
///
/// <para>The fury is a fixed 938 s run that drains a slice of vita when it WEARS OUT, at the tier it reached
/// (Warrior Tutor SoulHunter's board post, mirrored in the <c>ChungRyongRageTiers</c> table in
/// <c>Server/Session.Spells.cs</c>; renewing before it lapses costs nothing). <c>RegenTick</c> fires that
/// drain on the first beat at or after the deadline while the tier is still set, so an unpaid fury is
/// exactly the pair (<c>_crRageTier &gt; 0</c>, deadline in the past) — and that pair has to survive a
/// relog or the price is simply never charged.</para>
///
/// <para><b>Why this needs tests by the "would this fail loudly?" rule.</b> Every failure mode here is
/// silent and none of them throws. Capture the fury only while its deadline is ahead and a player who logs
/// out and stays out past it comes back owing nothing — no error, no log line, just a tier-6 fury that was
/// free. Capture it without checking the tier and a fury that already paid, or one that death or @dispel
/// STRIPPED (which owes nothing), is written back and charges a second time. Restore it by calling
/// <c>ChungRyongRageWearOff</c> directly and the drain lands on the arrival thread before the entry
/// sequence has drawn the character. So there is a fact per window, a fact for the double charge, and two
/// facts pinning the unchanged paths.</para>
/// </summary>
[Collection("world")]
public class FuryRelogDrainTests
{
    private const string RageName = "Chung Ryong's Rage";

    /// <summary>Long enough that a red is unmistakable, short enough that it does not stall the suite.</summary>
    private const int LapseBudgetMs = 5_000;

    private readonly SessionFixture _fx;

    public FuryRelogDrainTests(SessionFixture fx) => _fx = fx;

    // The two production methods under test are private; there is no seam for them and this file is not the
    // place to cut one. Reflection into Session's non-public surface is the pattern AnnounceMonitorTests
    // already uses. Both run under the session's state monitor, which is what CaptureTimedEffects asserts.
    private static readonly MethodInfo CaptureMi =
        typeof(Session).GetMethod("CaptureTimedEffects", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo RestoreMi =
        typeof(Session).GetMethod("RestoreTimedEffects", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static void Capture(Session s) => s.WithState(() => CaptureMi.Invoke(s, null));
    private static void Restore(Session s) => s.WithState(() => RestoreMi.Invoke(s, null));

    /// <summary>(a) Window one — logged out with the fury RUNNING, back after it lapsed. The tier is captured
    /// with its deadline, the deadline goes by while the character is offline, and the first
    /// <c>RegenTick</c> beat after the restore charges the tier's vita price exactly once.
    ///
    /// <para>Tier 4: 40% of vita (<c>ChungRyongRageTiers</c>), and its -15 AC buff is on <c>_buffs</c> on
    /// purpose — it is captured while running, lapses offline with the fury, and must NOT come back.</para>
    ///
    /// <para>Falsification: revert the <c>else if (e.CrRageTier &gt; 0)</c> branch in
    /// <c>RestoreTimedEffects</c>. The fury is never re-armed, the tier reads 0 after the restore and no
    /// beat ever drains. Run, confirm red, restore by hand. Recorded in
    /// <c>briefs/reports/fury-relog-drain-opus.md</c>.</para></summary>
    [Fact]
    public void AFuryThatLapsesWhileLoggedOffDrainsOnTheFirstBeatBack()
    {
        var (before, _, c1) = _fx.PlayerWith("FuryRelogRunning", ch => { ch.Hp = 500; ch.MaxHp = 500; },
                                             SessionFixture.HomeMap, x: 5, y: 15);
        TimedEffects saved;
        try
        {
            before.WithState(() => before.LuaSetCrRage(tier: 4, mult: 18, ac: -15, durMs: 400, name: RageName));
            Assert.Equal(4, before.LuaCrRageTier);

            Capture(before);                                     // the autosave/logout snapshot
            saved = c1.Effects;
            Assert.Equal(4, saved.CrRageTier);
            Assert.True(saved.RageUntil > NowUnix, "the running fury was captured with a deadline already past");
            Assert.Single(saved.Buffs, b => b.Stat == "armor");   // the tier's AC buff, still running
        }
        finally
        {
            _fx.World.LeaveMap(before, SessionFixture.HomeMap);
        }

        Assert.True(SpinWait.SpinUntil(() => NowUnix > saved.RageUntil, LapseBudgetMs),
                    "the captured fury never lapsed");

        var (after, outbound, c2) = _fx.PlayerWith(
            "FuryRelogRunningBack", ch => { ch.Hp = 500; ch.MaxHp = 500; ch.Effects = saved; },
            SessionFixture.HomeMap, x: 6, y: 15);
        try
        {
            Restore(after);
            Assert.Equal(4, after.LuaCrRageTier);               // restored owing its drain
            Assert.False(after.LuaRageActive);                  // but NOT its swing multiplier
            Assert.Equal(long.MaxValue, after.NextBuffExpiryForTest);   // and NOT its AC buff
            outbound.Clear();

            after.RegenTick(0, regenDue: false);
            Assert.Equal(300u, c2.Hp);                          // tier 4 costs 40% of vita
            Assert.Contains("Chung Ryong's rage leaves you drained.", MiniTexts(outbound));
            Assert.Equal(0, after.LuaCrRageTier);

            outbound.Clear();
            for (int i = 0; i < 5; i++) after.RegenTick(0, regenDue: false);
            Assert.Equal(300u, c2.Hp);                          // and exactly once
            Assert.Empty(MiniTexts(outbound));
        }
        finally
        {
            _fx.World.LeaveMap(after, SessionFixture.HomeMap);
        }
    }

    /// <summary>(b) Window two — logged out AFTER the deadline passed but before the regen beat that would
    /// have fired the wear-off. The fury is over and unpaid, and nothing but the tier says so, so the tier
    /// has to be what decides the fury is captured.
    ///
    /// <para>Tier 1 with no AC on purpose: a keyed AC buff would put an entry on <c>_buffs</c> and carry the
    /// restored beat for reasons that have nothing to do with the fury.</para>
    ///
    /// <para>Falsification: revert <c>CaptureTimedEffects</c> to <c>if (now &lt; _rageUntil)</c>. The lapsed
    /// fury is not written at all, <c>CrRageTier</c> reads 0 in the saved effects and nothing can be
    /// restored. Run, confirm red, restore by hand.</para></summary>
    [Fact]
    public void AFuryAlreadyPastItsDeadlineIsCapturedWithItsTier()
    {
        var (before, _, c1) = _fx.PlayerWith("FuryRelogLapsed", ch => { ch.Hp = 500; ch.MaxHp = 500; },
                                             SessionFixture.HomeMap, x: 7, y: 15);
        TimedEffects saved;
        try
        {
            before.WithState(() => before.LuaSetCrRage(tier: 1, mult: 6, ac: 0, durMs: 1, name: RageName));
            Assert.True(SpinWait.SpinUntil(() => !before.LuaRageActive, LapseBudgetMs),
                        "the fury never lapsed");
            Assert.Equal(1, before.LuaCrRageTier);   // over, and unpaid: no RegenTick beat has run

            Capture(before);
            saved = c1.Effects;
            Assert.Equal(1, saved.CrRageTier);
            Assert.True(saved.RageUntil > 0, "the lapsed fury was captured without its deadline");
        }
        finally
        {
            _fx.World.LeaveMap(before, SessionFixture.HomeMap);
        }

        var (after, outbound, c2) = _fx.PlayerWith(
            "FuryRelogLapsedBack", ch => { ch.Hp = 500; ch.MaxHp = 500; ch.Effects = saved; },
            SessionFixture.HomeMap, x: 8, y: 15);
        try
        {
            Restore(after);
            Assert.Equal(1, after.LuaCrRageTier);
            outbound.Clear();

            after.RegenTick(0, regenDue: false);
            Assert.Equal(400u, c2.Hp);              // tier 1 costs 20% of vita
            Assert.Contains("Chung Ryong's rage leaves you drained.", MiniTexts(outbound));
            Assert.Equal(0, after.LuaCrRageTier);
        }
        finally
        {
            _fx.World.LeaveMap(after, SessionFixture.HomeMap);
        }
    }

    /// <summary>(c) No double charge. A fury that lapsed while the player was ONLINE has already paid on the
    /// beat that fired its wear-off; nothing of it is written to the character afterwards, so a later relog
    /// restores nothing and no beat drains again.
    ///
    /// <para><c>ChungRyongRageWearOff</c> zeroes the tier but leaves <c>_rageUntil</c> on its past deadline,
    /// so the tier is the only thing separating a paid fury from an unpaid one at capture time.</para>
    ///
    /// <para>Falsification: drop the <c>_crRageTier &gt; 0</c> test from <c>CaptureTimedEffects</c> (capture
    /// the fury unconditionally). The paid fury's past deadline is written to <c>RageUntil</c> and the first
    /// assertion below goes red. Run, confirm red, restore by hand.</para></summary>
    [Fact]
    public void AFuryThatAlreadyPaidIsNotSavedAndDoesNotChargeAgain()
    {
        var (before, outbound1, c1) = _fx.PlayerWith("FuryRelogPaid", ch => { ch.Hp = 500; ch.MaxHp = 500; },
                                                     SessionFixture.HomeMap, x: 9, y: 15);
        TimedEffects saved;
        try
        {
            before.WithState(() => before.LuaSetCrRage(tier: 1, mult: 6, ac: 0, durMs: 1, name: RageName));
            outbound1.Clear();
            Assert.True(
                SpinWait.SpinUntil(() => { before.RegenTick(0, regenDue: false); return before.LuaCrRageTier == 0; },
                                   LapseBudgetMs),
                "the fury never wore off while logged in");
            Assert.Equal(400u, c1.Hp);
            Assert.Contains("Chung Ryong's rage leaves you drained.", MiniTexts(outbound1));

            Capture(before);
            saved = c1.Effects;
            Assert.Equal(0, saved.CrRageTier);
            Assert.Equal(0, saved.RageUntil);       // nothing of the paid fury is persisted
        }
        finally
        {
            _fx.World.LeaveMap(before, SessionFixture.HomeMap);
        }

        var (after, outbound2, c2) = _fx.PlayerWith(
            "FuryRelogPaidBack", ch => { ch.Hp = 400; ch.MaxHp = 500; ch.Effects = saved; },
            SessionFixture.HomeMap, x: 10, y: 15);
        try
        {
            Restore(after);
            Assert.Equal(0, after.LuaCrRageTier);
            outbound2.Clear();

            for (int i = 0; i < 5; i++) after.RegenTick(0, regenDue: false);
            Assert.Equal(400u, c2.Hp);
            Assert.Empty(MiniTexts(outbound2));
        }
        finally
        {
            _fx.World.LeaveMap(after, SessionFixture.HomeMap);
        }
    }

    /// <summary>(d) A short absence, unchanged. A fury captured while still RUNNING is restored whole — tier,
    /// deadline, swing multiplier and name — and charges nothing, because it has not lapsed. This is the path
    /// that already worked and the one the change must not disturb: restoring a running fury owing a drain
    /// would price a fury the player is still holding.</summary>
    [Fact]
    public void AFuryStillRunningIsRestoredWholeAndChargesNothing()
    {
        var (before, _, c1) = _fx.PlayerWith("FuryRelogShort", ch => { ch.Hp = 500; ch.MaxHp = 500; },
                                             SessionFixture.HomeMap, x: 11, y: 15);
        TimedEffects saved;
        try
        {
            before.WithState(() => before.LuaSetCrRage(tier: 2, mult: 9, ac: 0, durMs: 60_000, name: RageName));
            Capture(before);
            saved = c1.Effects;
            Assert.Equal(2, saved.CrRageTier);
            Assert.Equal(9, saved.RageAmount);
            Assert.Equal(RageName, saved.RageName);
        }
        finally
        {
            _fx.World.LeaveMap(before, SessionFixture.HomeMap);
        }

        var (after, outbound, c2) = _fx.PlayerWith(
            "FuryRelogShortBack", ch => { ch.Hp = 500; ch.MaxHp = 500; ch.Effects = saved; },
            SessionFixture.HomeMap, x: 12, y: 15);
        try
        {
            Restore(after);
            Assert.Equal(2, after.LuaCrRageTier);
            Assert.True(after.LuaRageActive, "the running fury came back without its swing multiplier");
            outbound.Clear();

            for (int i = 0; i < 5; i++) after.RegenTick(0, regenDue: false);
            Assert.Equal(500u, c2.Hp);
            Assert.Empty(MiniTexts(outbound));
            Assert.Equal(2, after.LuaCrRageTier);
        }
        finally
        {
            _fx.World.LeaveMap(after, SessionFixture.HomeMap);
        }
    }

    /// <summary>(e) A plain, non-Chung-Ryong fury (<c>LuaSetRage</c>: a whole-swing multiplier with no tier)
    /// is captured and restored exactly as before — saved while running, restored while running, and simply
    /// dropped once its deadline is past. It owes no drain, so the tier term must never pick it up.</summary>
    [Fact]
    public void APlainFuryIsCapturedAndRestoredAsBefore()
    {
        var (running, _, c1) = _fx.PlayerWith("FuryRelogPlain", ch => { ch.Hp = 500; ch.MaxHp = 500; },
                                              SessionFixture.HomeMap, x: 5, y: 16);
        TimedEffects saved;
        try
        {
            running.WithState(() => running.LuaSetRage(amount: 3, durMs: 60_000, name: "Fury"));
            Capture(running);
            saved = c1.Effects;
            Assert.Equal(0, saved.CrRageTier);
            Assert.Equal(3, saved.RageAmount);
            Assert.Equal("Fury", saved.RageName);
        }
        finally
        {
            _fx.World.LeaveMap(running, SessionFixture.HomeMap);
        }

        var (after, outbound, c2) = _fx.PlayerWith(
            "FuryRelogPlainBack", ch => { ch.Hp = 500; ch.MaxHp = 500; ch.Effects = saved; },
            SessionFixture.HomeMap, x: 6, y: 16);
        try
        {
            Restore(after);
            Assert.True(after.LuaRageActive);
            Assert.Equal(0, after.LuaCrRageTier);
            outbound.Clear();
            for (int i = 0; i < 5; i++) after.RegenTick(0, regenDue: false);
            Assert.Equal(500u, c2.Hp);
            Assert.Empty(MiniTexts(outbound));
        }
        finally
        {
            _fx.World.LeaveMap(after, SessionFixture.HomeMap);
        }

        // Lapsed, and therefore not captured at all: no tier, nothing owed, nothing written.
        var (lapsed, _, c3) = _fx.PlayerWith("FuryRelogPlainLapsed", ch => { ch.Hp = 500; ch.MaxHp = 500; },
                                             SessionFixture.HomeMap, x: 7, y: 16);
        try
        {
            lapsed.WithState(() => lapsed.LuaSetRage(amount: 3, durMs: 1, name: "Fury"));
            Assert.True(SpinWait.SpinUntil(() => !lapsed.LuaRageActive, LapseBudgetMs), "the plain fury never lapsed");
            Capture(lapsed);
            Assert.Equal(0, c3.Effects.RageUntil);
            Assert.Equal(0, c3.Effects.CrRageTier);
        }
        finally
        {
            _fx.World.LeaveMap(lapsed, SessionFixture.HomeMap);
        }
    }

    private static long NowUnix => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>The mini-text lines this recorder was sent. The 0x0A body is
    /// <c>type(u8) | len(u16 BE) | ascii[len]</c> — see <c>Session.SendMiniText</c>.</summary>
    private static List<string> MiniTexts(RecordingOutbound outbound) =>
        outbound.BodiesOf(ServerOp.MiniText)
                .Where(b => b.Length >= 3)
                .Select(b => System.Text.Encoding.ASCII.GetString(b, 3, Math.Min((b[1] << 8) | b[2], b.Length - 3)))
                .ToList();
}
