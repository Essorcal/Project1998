using System.Collections;
using System.Reflection;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The Do spells slice (owner, 2026-10-26: P5 "each spell as its RTK script", P1 and P3 "on sale once it works, at its
/// script's price", Q8 "Spirit blade x10, as RTK"): Dash, Blend, Hear footstep, Assess opponent, Balance weapon, Master
/// weapon and Crossing swords do what their RTK scripts do, and the Do teacher sells them. Before, all seven fell to the
/// <c>generic</c> verb: 5 mana and nothing else (<c>Tests/Fixtures/spell-dispatch-routes.txt</c> had them on
/// <c>verb=generic</c>). Spirit blade's x10 is <see cref="SpiritBladeSpellTests"/>'; Ki strike is unchanged
/// (<c>SubpathDamageSpellTests</c>).
///
/// <para>The scripts: <c>RTK-Server/rtklua/Accepted/Spells/Subpaths/Do/</c>, <c>dash.lua</c>, <c>blend.lua</c>,
/// <c>hear_footstep.lua</c>, <c>assess_opponent.lua</c>, <c>balance_weapon.lua</c>, <c>master_weapon.lua</c> and
/// <c>crossing_swords.lua</c>. Dash, Hear footstep, Assess opponent and Crossing swords are bound by their
/// <c>game-data/SpellParams.csv</c> rows; Blend is in the stealth set and the two weapons are enchants
/// (<c>game-data/SpellMods.csv</c>).</para>
///
/// <para>Every cast is the real 0x0F frame through <c>Session.Receive</c>, by a fresh level-99 Do. Dash needs terrain, so
/// it runs on the Do Guide map (3606, a 23x23 copy of the Do Circle that no NPC stands on and no other class uses); the
/// rest run on content-free maps (no Maps.csv row, so PvP is off, as in a town). A run's clock is moved on by moving its
/// deadlines back, as <c>ShamanSpellTests</c> moves them, and then running the heartbeat's call.</para>
/// </summary>
[Collection("world")]
public sealed class DoSpellTests
{
    private const ushort DashMap = 3606;
    private const ushort BlendMap = 62970, HearMap = 62971, AssessMap = 62972, RageMap = 62973, EnchantMap = 62974,
                         LogoutMap = 62975, SneakMap = 62976, EndMap = 62977;
    private const ushort CX = 16, CY = 16;
    private const uint MaxMana = 200_000, Mana = 200_000, Hp = 10_000;

    /// <summary>Slack on a wait read straight after the cast.</summary>
    private const long SlackMs = 5_000;

    /// <summary>Blend's sneak multiplier, its script's <c>invis = 9</c> (the project owner's Q15), which its SpellParams.csv row carries
    /// as <c>amount</c>: the one figure these facts expect of it.</summary>
    private const int BlendTimes = 9;

    private static readonly FieldInfo TicksField =
        typeof(Session).GetField("_spellTicks", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo PublishTickDue =
        typeof(Session).GetMethod("PublishSpellTickDue", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo StealthUntilField =
        typeof(Session).GetField("_stealthUntil", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo RageUntilField =
        typeof(Session).GetField("_rageUntil", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo BuffBoxMethod =
        typeof(Session).GetMethod("BuffBoxText", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo DieMethod =
        typeof(Session).GetMethod("Die", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo CaptureMi =
        typeof(Session).GetMethod("CaptureTimedEffects", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo RestoreMi =
        typeof(Session).GetMethod("RestoreTimedEffects", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo TearDownMethod =
        typeof(Session).GetMethod("TearDownWorldState", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo StateField =
        typeof(Session).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly SessionFixture _fx;
    private static int _serial;

    public DoSpellTests(SessionFixture fx) => _fx = fx;

    // ===== Dash =================================================================================================

    /// <summary>Three tiles in the faced direction over open ground, a 150 s wait, "You cast Dash." and no mana spent:
    /// the script checks its 250 and never takes it. Red on 0fadbfc: 5 mana spent and no move.</summary>
    [Fact]
    public void DashJumpsThreeTilesAndArmsItsWait()
    {
        var md = MapData.For(DashMap)!;
        for (int x = 5; x <= 7; x++) Assert.False(md.BlockedMove(x, 11, 1), $"({x},11) is blocked; the fact needs open ground");
        var (caster, cOut, cc) = Caster("dash_do", DashMap, 4, 11);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            long wait = SpellCastSupport.AetherLeft(caster, "dash_do");
            Assert.True((cc.X, cc.Y) == (7, 11) && cc.Mp == Mana && lines == "You cast Dash."
                        && wait > 150_000 - SlackMs && wait <= 150_000,
                        $"at ({cc.X},{cc.Y}) (want (7,11)), spent {Mana - cc.Mp}, lines [{lines}], wait {wait} ms");
        }
        finally { _fx.World.LeaveMap(caster, DashMap); }
    }

    /// <summary>The walk stops before the first blocked tile, as RTK's <c>checkSpecificTile</c> loop stops: one tile
    /// before a block lands one tile on; a block on the first tile moves nobody, yet the wait is armed and the cast
    /// lands, as the script arms its wait before its warp. Red on 0fadbfc: no move and no wait either way.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void DashStopsBeforeABlockedTile(int open)
    {
        var (x, y) = TileEastOfOpenThenBlocked(open);
        var (caster, cOut, cc) = Caster("dash_do", DashMap, x, y);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            long wait = SpellCastSupport.AetherLeft(caster, "dash_do");
            Assert.True((cc.X, cc.Y) == (x + open, y) && lines == "You cast Dash." && wait > 0,
                        $"from ({x},{y}) with {open} open: at ({cc.X},{cc.Y}), lines [{lines}], wait {wait} ms");
        }
        finally { _fx.World.LeaveMap(caster, DashMap); }
    }

    /// <summary>Short of 250 mana, "Your will is too weak.", and nothing moves or waits; inside its wait, "Dash isn't
    /// ready yet.", for free. Red on 0fadbfc: generic's 5 mana and "You cast Dash." both times.</summary>
    [Fact]
    public void DashRefusesShortOfManaAndInsideItsWait()
    {
        var (weak, wOut, wc) = Caster("dash_do", DashMap, 4, 13, mana: 249);
        var (twice, tOut, tc) = Caster("dash_do", DashMap, 4, 9);
        try
        {
            weak.Receive(SpellCastSupport.CastFrame(0));
            string weakLines = string.Join(" | ", SpellCastSupport.MiniTexts(wOut));
            Assert.True(weakLines == "Your will is too weak." && (wc.X, wc.Y) == (4, 13) && wc.Mp == 249
                        && SpellCastSupport.AetherLeft(weak, "dash_do") == 0,
                        $"weak: [{weakLines}] at ({wc.X},{wc.Y}), mana {wc.Mp}");

            twice.Receive(SpellCastSupport.CastFrame(0));
            tOut.Clear();
            twice.Receive(SpellCastSupport.CastFrame(0));
            string again = string.Join(" | ", SpellCastSupport.MiniTexts(tOut));
            Assert.True(again == "Dash isn't ready yet." && (tc.X, tc.Y) == (7, 9) && tc.Mp == Mana,
                        $"again: [{again}] at ({tc.X},{tc.Y}) (want (7,9)), spent {Mana - tc.Mp}");
        }
        finally { _fx.World.LeaveMap(weak, DashMap); _fx.World.LeaveMap(twice, DashMap); }
    }

    // ===== Balance weapon and Master weapon =====================================================================

    /// <summary>Each arms its enchant at its script's multiplier for its script's mana: Balance weapon x15 for 15,000,
    /// Master weapon x20 for 25,000. Red on 0fadbfc: the enchant stays at 1 and generic's 5 is spent.</summary>
    [Theory]
    [InlineData("balance_weapon", 15.0, 15_000)]
    [InlineData("master_weapon", 20.0, 25_000)]
    public void EachWeaponArmsItsEnchant(string key, double times, int mana)
    {
        Assert.Equal((times, mana), Content.EnchantFor(Content.SpellByKey(key)!));
        var (caster, cOut, cc) = Caster(key, EnchantMap, CX, CY);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            double armed = SpellCastSupport.Enchant(caster);
            Assert.True(armed == times && cc.Mp == Mana - (uint)mana && lines == $"You cast {Content.SpellByKey(key)!.Name}.",
                        $"{key}: enchant {armed} (want {times}), spent {Mana - cc.Mp} (want {mana}), lines [{lines}]");
        }
        finally { _fx.World.LeaveMap(caster, EnchantMap); }
    }

    /// <summary>The Do enchants share one slot with every enchant, as RTK's <c>enchants</c> table holds all three: with
    /// Spirit blade up, Master weapon answers "This spell is already active." for free; short of its 25,000 it answers
    /// the enchant family's "You do not have enough mana.". Red on 0fadbfc: generic's 5 mana and "You cast" both times.</summary>
    [Fact]
    public void MasterWeaponRefusesWhileAnEnchantRunsOrShortOfMana()
    {
        var (caster, cOut, cc) = Caster("spirit_blade", EnchantMap, CX, CY, extra: "master_weapon");
        var (poor, pOut, pc) = Caster("master_weapon", EnchantMap, CX, CY + 2, mana: 24_999);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            uint after = cc.Mp;
            cOut.Clear();
            caster.Receive(SpellCastSupport.CastFrame(1));
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            Assert.True(lines == "This spell is already active." && cc.Mp == after && SpellCastSupport.Enchant(caster) == 10.0,
                        $"over Spirit blade: [{lines}], spent {after - cc.Mp}, enchant {SpellCastSupport.Enchant(caster)}");

            poor.Receive(SpellCastSupport.CastFrame(0));
            string poorLines = string.Join(" | ", SpellCastSupport.MiniTexts(pOut));
            Assert.True(poorLines == "You do not have enough mana." && pc.Mp == 24_999 && SpellCastSupport.Enchant(poor) == 1.0,
                        $"short: [{poorLines}], mana {pc.Mp}");
        }
        finally { _fx.World.LeaveMap(caster, EnchantMap); _fx.World.LeaveMap(poor, EnchantMap); }
    }

    // ===== Blend ================================================================================================
    // As its RTK script (owner, 2026-10-26, Q15): its own `hide` verb on the rogue's stealth state, x9, its own lines.
    // The rogue's four stealths keep x5 and the set's rules; the facts at the end of this section pin that.

    /// <summary>The caster turns invisible (RTK state 2) for 120 s for 4,000 mana, with a 30 s wait, "You cast Blend."
    /// and the script's graphic 16 over them, and the next swing x9. It arms no cast slot: RTK's script has none, and the
    /// rogue's Invisible keeps its 1 s one (<c>Content.CastDelayMs</c>). Red on 0fadbfc: generic's 5 mana and nothing
    /// else; red on 65cfc39 for the x9 and the slot.</summary>
    [Fact]
    public void BlendHidesItsCasterForTwoMinutes()
    {
        var (caster, cOut, cc) = Caster("blend", BlendMap, CX, CY);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            long hidden = StealthLeft(caster), wait = SpellCastSupport.AetherLeft(caster, "blend");
            var drawn = Effects(cOut, caster.PlayerId);
            long slot = caster.ActionSlotLeft;
            Assert.True(caster.IsInvisible && hidden > 120_000 - SlackMs && hidden <= 120_000 && cc.Mp == Mana - 4_000
                        && wait > 30_000 - SlackMs && wait <= 30_000 && lines == "You cast Blend." && drawn.SequenceEqual(new[] { 16 })
                        && StealthTimes(caster) == BlendTimes && slot == 0,
                        $"invisible {caster.IsInvisible} for {hidden} ms, spent {Mana - cc.Mp}, wait {wait} ms, lines [{lines}], " +
                        $"graphics [{string.Join(",", drawn)}], x{StealthTimes(caster)}, cast slot {slot} ms");
        }
        finally { _fx.World.LeaveMap(caster, BlendMap); }
    }

    /// <summary>Blend's refusals, each free and as its script: inside its 30 s wait, "Blend isn't ready yet."; short of
    /// 4,000, "Your will is too weak."; in a creature's form, "You can't do that while transformed." (the script's
    /// canCast(1, 1, 1)). Red on 65cfc39, where Blend rode the stealth set: "Blend isn't ready yet (29s).", "You do not
    /// have enough mana." and "You can't cast that spell now.".</summary>
    [Fact]
    public void BlendRefusesInsideItsWaitShortOfManaAndTransformed()
    {
        var (caster, cOut, cc) = Caster("blend", BlendMap, CX, CY);
        var (poor, pOut, pc) = Caster("blend", BlendMap, CX, CY + 2, mana: 3_999);
        var (morphed, mOut, mc) = Caster("blend", BlendMap, CX, CY + 6);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            uint after = cc.Mp;
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            poor.Receive(SpellCastSupport.CastFrame(0));
            Morph(morphed);
            mOut.Clear();
            morphed.Receive(SpellCastSupport.CastFrame(0));
            string N(RecordingOutbound o) => string.Join(" | ", SpellCastSupport.MiniTexts(o));
            Assert.True(N(cOut) == "Blend isn't ready yet." && cc.Mp == after
                        && N(pOut) == "Your will is too weak." && pc.Mp == 3_999 && !poor.IsInvisible
                        && N(mOut) == "You can't do that while transformed." && mc.Mp == Mana && !morphed.IsInvisible,
                        $"waiting [{N(cOut)}] spent {after - cc.Mp}; short [{N(pOut)}] mana {pc.Mp}; transformed [{N(mOut)}] " +
                        $"spent {Mana - mc.Mp}");
        }
        finally { Leave(BlendMap, caster, poor, morphed); }
    }

    /// <summary>A cast while already invisible casts again, as the script does (it has no check): 4,000 more, the 30 s
    /// wait again, "You cast Blend." again, still x9; but the run keeps its end, since RTK's setDuration sets a running
    /// duration again only to a shorter time (rtk/src/map/sl.c, pcl_setduration). Here the first run has 60 s left and
    /// still has 60 s after. Red on 65cfc39 ("You already cast that spell.", free); red with the run lengthened (120 s).</summary>
    [Fact]
    public void ABlendCastWhileInvisibleCastsAgainAndKeepsTheRunsEnd()
    {
        var (caster, cOut, cc) = Caster("blend", BlendMap, CX, CY + 8);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            caster.WithState(() => StealthUntilField.SetValue(caster, Environment.TickCount64 + 60_000));
            SpellCastSupport.ClearAether(caster, "blend");
            uint before = cc.Mp;
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            long hidden = StealthLeft(caster), wait = SpellCastSupport.AetherLeft(caster, "blend");
            Assert.True(lines == "You cast Blend." && before - cc.Mp == 4_000 && caster.IsInvisible
                        && hidden > 60_000 - SlackMs && hidden <= 60_000 && wait > 30_000 - SlackMs && StealthTimes(caster) == BlendTimes,
                        $"again: [{lines}], spent {before - cc.Mp}, invisible {caster.IsInvisible} for {hidden} ms (want 60 s), " +
                        $"wait {wait} ms, x{StealthTimes(caster)}");
        }
        finally { _fx.World.LeaveMap(caster, BlendMap); }
    }

    /// <summary>The next landed swing after a stealth hits its stealth's multiplier, and ends it: Blend's x9, as its
    /// script's invis = 9; the rogue's four x5, as they hit today. Each swing is measured against the same caster's swings
    /// with no stealth (<c>Session.PlayerSwingRaw</c>, before the floor, crits left out). One caster holds Blend and the
    /// rogue stealth and casts Blend first, so the rogue case is also where Blend's x9 would leak. Red on 65cfc39 for
    /// Blend (x5); red for the rogue's four with the reset taken out of their arming (x9 leaks).</summary>
    [Theory]
    [InlineData("invisible_rogue")]
    [InlineData("spirits_form_rogue")]
    [InlineData("lifes_cloak_rogue")]
    [InlineData("glass_form_rogue")]
    public void TheNextLandedSwingHitsTheStealthsOwnMultiplier(string rogue)
    {
        var (caster, _, _) = Swinger(SneakMap, CX, CY + Array.IndexOf(RogueStealths, rogue) * 2, "blend", rogue);
        try
        {
            var plain = PlainSwings(caster);
            var blend = StealthSwings(caster, 0);
            var their = StealthSwings(caster, 1);
            Assert.True(blend.Count > 0 && their.Count > 0
                        && blend.All(r => plain.Contains(Math.Round(r / BlendTimes, 6))) && their.All(r => plain.Contains(Math.Round(r / 5, 6))),
                        $"plain [{string.Join(", ", plain)}]; after Blend [{string.Join(", ", blend)}] (want x{BlendTimes}); " +
                        $"after {rogue} [{string.Join(", ", their)}] (want x5)");
        }
        finally { _fx.World.LeaveMap(caster, SneakMap); }
    }

    /// <summary>Blend says "You are no longer invisible." when it ends, as its script's uncast does, every way RTK runs that
    /// uncast with the player alive: its time (the world's tick), a landed hit, a pickup, a won Dispell. Not at death: RTK
    /// sets the player dead before the uncast, whose own check then says nothing. The rogue's Invisible says nothing at
    /// any of these, as today. Red on 65cfc39 for Blend (no line); red at death with the death path's quiet taken out.</summary>
    [Theory]
    [InlineData("blend", "time", true)]
    [InlineData("blend", "hit", true)]
    [InlineData("blend", "pickup", true)]
    [InlineData("blend", "dispell", true)]
    [InlineData("blend", "death", false)]
    [InlineData("invisible_rogue", "time", false)]
    [InlineData("invisible_rogue", "hit", false)]
    [InlineData("invisible_rogue", "pickup", false)]
    [InlineData("invisible_rogue", "dispell", false)]
    public void BlendSaysItsEndLineWhereItsScriptDoes(string key, string how, bool says)
    {
        const string Line = "You are no longer invisible.";
        var (caster, cOut, _) = Swinger(EndMap, CX + Interlocked.Increment(ref _serial) % 40, CY, key);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(caster.IsInvisible, $"{key} did not hide its caster");
            cOut.Clear();
            switch (how)
            {
                case "time":
                    caster.WithState(() => StealthUntilField.SetValue(caster, Environment.TickCount64 - 1));
                    _fx.World.TickOnceForTest();
                    break;
                case "hit":
                    for (int i = 0; i < 200 && caster.IsInvisible; i++) SwingOnce(caster);
                    break;
                case "pickup":
                    caster.Receive(SessionFixture.Frame(ClientOp.Pickup, new byte[] { 0, 0 }));
                    break;
                case "dispell":
                    caster.ReceiveFlush(endHolds: true);
                    _fx.World.TickOnceForTest();
                    break;
                default:
                    caster.WithState(() => DieMethod.Invoke(caster, null));
                    break;
            }
            int said = SpellCastSupport.MiniTexts(cOut).Count(l => l == Line);
            Assert.True(!caster.IsInvisible && said == (says ? 1 : 0),
                        $"{key} ended by {how}: invisible {caster.IsInvisible}, the end line said {said} times (want {(says ? 1 : 0)})");
        }
        finally { _fx.World.LeaveMap(caster, EndMap); }
    }

    /// <summary>A Blend that runs through a relog keeps its x9 and its end line, read back from the stealth's saved name
    /// with nothing new saved: the saved effects hold the deadline and "Blend" only. Red without the restore's lookup
    /// (<c>Session.RestoreHideRule</c>): x5 and no line, as the rogue's.</summary>
    [Fact]
    public void ABlendThatRunsThroughARelogKeepsItsRule()
    {
        var (before, _, c1) = Swinger(EndMap, CX + 1, CY + 6, "blend");
        TimedEffects saved;
        try
        {
            before.Receive(SpellCastSupport.CastFrame(0));
            before.WithState(() => CaptureMi.Invoke(before, null));
            saved = c1.Effects;
            Assert.Equal("Blend", saved.StealthName);
        }
        finally { _fx.World.LeaveMap(before, EndMap); }

        var (after, aOut, _) = Swinger(EndMap, CX + 1, CY + 8, "blend", configure: ch => ch.Effects = saved);
        try
        {
            after.WithState(() => RestoreMi.Invoke(after, null));
            Assert.True(after.IsInvisible && StealthTimes(after) == BlendTimes, $"restored: invisible {after.IsInvisible}, x{StealthTimes(after)}");
            double? clean = null;
            int landed = 0;
            for (int i = 0; i < 200 && after.IsInvisible; i++)
            {
                var (hit, raw, crit) = SwingOnce(after);
                if (hit) { landed++; if (!crit) clean = raw; }
            }
            var plain = PlainSwings(after);   // the same swinger, the stealth now ended by that swing
            int said = SpellCastSupport.MiniTexts(aOut).Count(l => l == "You are no longer invisible.");
            Assert.True(landed == 1 && clean is double c && plain.Contains(Math.Round(c / BlendTimes, 6)) && said == 1,
                        $"after the relog: landed {landed}, clean swing {clean} against plain [{string.Join(", ", plain)}] (want x{BlendTimes}), " +
                        $"end line said {said}");
        }
        finally { _fx.World.LeaveMap(after, EndMap); }
    }

    /// <summary>The rogue's Invisible still arms no wait and keeps the set's own refusal, "You already cast that spell.",
    /// for free. A guard on the stealth set, which Blend no longer rides, green on 0fadbfc too.</summary>
    [Fact]
    public void InvisibleStillHasNoWait()
    {
        var (caster, cOut, cc) = Caster("invisible_rogue", BlendMap, CX, CY + 4);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            uint after = cc.Mp;
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            Assert.True(caster.IsInvisible && SpellCastSupport.AetherLeft(caster, "invisible_rogue") == 0
                        && lines == "You already cast that spell." && cc.Mp == after,
                        $"invisible {caster.IsInvisible}, wait {SpellCastSupport.AetherLeft(caster, "invisible_rogue")} ms, again [{lines}]");
        }
        finally { _fx.World.LeaveMap(caster, BlendMap); }
    }

    // ===== Hear footstep ========================================================================================

    /// <summary>The cast starts a 32 s run on its caster, with no creature, whose beat comes every 5 s; it spends its 0
    /// mana, arms its 60 s wait, says "You cast Hear footstep." and draws the script's graphic 11 over the caster; the
    /// buff box lists it. Red on 0fadbfc: generic's 5 mana and nothing else.</summary>
    [Fact]
    public void HearFootstepStartsARunOnItsCaster()
    {
        var (caster, cOut, cc) = Caster("hear_footstep_do", HearMap, CX, CY);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            var t = Ticks(caster).SingleOrDefault();
            long length = t is null ? 0 : t.UntilIn - t.NextIn + t.Every;
            long wait = SpellCastSupport.AetherLeft(caster, "hear_footstep_do");
            string box = BuffBox(caster);
            var drawn = Effects(cOut, caster.PlayerId);
            Assert.True(t is { Spell: "hear_footstep_do", Verb: "hear_footstep_tick", Self: true, Every: 5_000, Ended: false }
                        && length == 32_000 && cc.Mp == Mana && wait > 60_000 - SlackMs && wait <= 60_000
                        && lines == "You cast Hear footstep." && drawn.SequenceEqual(new[] { 11 }) && box.Contains("Hear footstep 32s"),
                        $"tick {t}, length {length} ms, spent {Mana - cc.Mp}, wait {wait} ms, lines [{lines}], " +
                        $"graphics [{string.Join(",", drawn)}], box [{box}]");
        }
        finally { _fx.World.LeaveMap(caster, HearMap); }
    }

    /// <summary>Each beat names, one line each, every other player invisible within 4 tiles on both axes: one at
    /// (4, 4) is named; one at 5 tiles, one visible beside the caster, and the caster, invisible too, are not. Red on
    /// 0fadbfc: no run, so no line.</summary>
    [Fact]
    public void EachBeatNamesTheOthersHiddenWithinFourTiles()
    {
        var (near, _, _) = Player(HearMap, CX + 4, CY - 4);
        var (far, _, _) = Player(HearMap, CX + 5, CY);
        var (seen, _, _) = Player(HearMap, CX + 1, CY);
        var (caster, cOut, _) = Caster("hear_footstep_do", HearMap, CX, CY);
        try
        {
            foreach (var s in new[] { near, far, caster }) s.WithState(() => s.LuaSetStealth(600_000));
            caster.Receive(SpellCastSupport.CastFrame(0));
            cOut.Clear();
            Advance(caster, 5_000);
            var lines = SpellCastSupport.MiniTexts(cOut);
            Assert.True(lines.SequenceEqual(new[] { $"{near.CharName} is hidden in the area." }),
                        $"beat: [{string.Join(" | ", lines)}] (want only {near.CharName})");
        }
        finally { Leave(HearMap, caster, near, far, seen); }
    }

    /// <summary>The run beats every 5 s strictly before its 32 s end, six times, and is then gone. Red on 0fadbfc: no
    /// run.</summary>
    [Fact]
    public void TheRunBeatsSixTimesThenEnds()
    {
        var (hider, _, _) = Player(HearMap, CX + 2, CY + 2);
        var (caster, cOut, _) = Caster("hear_footstep_do", HearMap, CX, CY);
        try
        {
            hider.WithState(() => hider.LuaSetStealth(600_000));
            caster.Receive(SpellCastSupport.CastFrame(0));
            cOut.Clear();
            for (int i = 0; i < 8; i++) Advance(caster, 5_000);
            int told = SpellCastSupport.MiniTexts(cOut).Count(l => l == $"{hider.CharName} is hidden in the area.");
            Assert.True(told == 6 && Ticks(caster).Count == 0, $"told {told} times (want 6), runs left {Ticks(caster).Count}");
        }
        finally { Leave(HearMap, caster, hider); }
    }

    /// <summary>The world heartbeat runs a run on its caster, with nothing else calling it. Red without the self branch in
    /// the beat (a run with no creature would end on its first beat).</summary>
    [Fact]
    public void TheWorldHeartbeatRunsTheCastersBeat()
    {
        var (hider, _, _) = Player(HearMap, CX - 1, CY + 3);
        var (caster, cOut, _) = Caster("hear_footstep_do", HearMap, CX, CY + 6);
        try
        {
            hider.WithState(() => hider.LuaSetStealth(600_000));
            caster.Receive(SpellCastSupport.CastFrame(0));
            cOut.Clear();
            Advance(caster, 5_000, beat: false);
            _fx.World.TickOnceForTest();
            Assert.Contains($"{hider.CharName} is hidden in the area.", SpellCastSupport.MiniTexts(cOut));
        }
        finally { Leave(HearMap, caster, hider); }
    }

    /// <summary>What ends the caster's own durations ends the run, as RTK's pc_diescript and flushDuration end a duration
    /// its spell lets be dispelled (Hear footstep's SplDispel is 0): death, <c>@dispel</c> and a won Dispell. Red with
    /// the end taken out of <c>ClearAllTimedEffects</c> (death, <c>@dispel</c>) or <c>FlushDurations</c> (Dispell).</summary>
    [Theory]
    [InlineData("death")]
    [InlineData("dispel")]
    [InlineData("flush")]
    public void WhatEndsTheCastersDurationsEndsTheRun(string how)
    {
        var (caster, _, _) = Caster("hear_footstep_do", HearMap, CX + 8, CY + 8);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.Single(Ticks(caster));
            switch (how)
            {
                case "death":  caster.WithState(() => DieMethod.Invoke(caster, null)); break;
                case "dispel": caster.WithState(caster.DispelSelf); break;
                default:       caster.ReceiveFlush(endHolds: true); break;
            }
            Advance(caster, 5_000);
            Assert.True(Ticks(caster).Count == 0 && Due(caster) == 0,
                        $"after {how}: {Ticks(caster).Count} runs left, the beat's hint {Due(caster)} (want 0)");
        }
        finally { _fx.World.LeaveMap(caster, HearMap); }
    }

    /// <summary>A logout ends a run on its caster and nothing is said after it: the heartbeat takes its roster before it
    /// reaches each player, so a logout can run to its end in between. Here a beat is made due with a player hiding beside
    /// the caster, the roster taken, the real teardown run under the caster's monitor, and the heartbeat's call made from
    /// that roster. No name is said and the run is gone: #406's logout check at the top of the beat
    /// (<c>Session.EndSpellTicksIfLeaving</c>) ends every run, this one included, before its verb runs. Red with that
    /// check and <c>Session.LuaHiddenNear</c>'s both taken out: the beat names the hider to the player who has gone
    /// (either one alone keeps the line from being said).</summary>
    [Fact]
    public void NoSelfBeatRunsAfterTheCastersLogout()
    {
        var (hider, _, _) = Player(LogoutMap, CX + 1, CY + 1);
        var (caster, cOut, _) = Caster("hear_footstep_do", LogoutMap, CX, CY);
        try
        {
            hider.WithState(() => hider.LuaSetStealth(600_000));
            caster.Receive(SpellCastSupport.CastFrame(0));
            Advance(caster, 5_000, beat: false);                           // a beat is due
            var roster = _fx.World.Online.All();                           // the heartbeat's snapshot
            Assert.Contains(caster, roster);
            caster.WithState(() => TearDownMethod.Invoke(caster, null));   // the logout, run to its end
            cOut.Clear();
            foreach (var s in roster) if (ReferenceEquals(s, caster)) s.TickSpellTicks();
            var lines = SpellCastSupport.MiniTexts(cOut);
            Assert.True(lines.Count == 0 && Ticks(caster).Count == 0 && Due(caster) == 0,
                        $"after the logout: lines [{string.Join(" | ", lines)}], runs left {Ticks(caster).Count}, " +
                        $"the beat's hint {Due(caster)} (want 0)");
        }
        finally { _fx.World.LeaveMap(hider, LogoutMap); }
    }

    /// <summary>A logout that gets in while a self beat waits for the Lua gate says nothing either. The beat runs its verb
    /// in the caster's monitor and enters the gate from there; on the gate's slow path the monitor is dropped while it
    /// waits, and a logout can run to its end in that gap, after the beat's own logout check. Here another thread holds
    /// the gate, the beat starts, takes the monitor and waits at the gate; the teardown runs on this thread; only then is
    /// the gate let go, and the verb runs for a player who has gone. The order is checked as #406's twin fact checks it.
    /// No name is said and the run is gone. Red without the logout check in <c>Session.LuaHiddenNear</c>: the beat names
    /// the hider after the teardown.</summary>
    [Fact]
    public void ALogoutWhileTheSelfBeatWaitsForTheLuaGateSaysNothing()
    {
        var (hider, _, _) = Player(LogoutMap, CX + 6, CY + 1);
        var (caster, cOut, _) = Caster("hear_footstep_do", LogoutMap, CX + 5, CY);
        var gateHeld = new ManualResetEventSlim();
        var releaseGate = new ManualResetEventSlim();
        Thread? holder = null, beat = null;
        Exception? beatFault = null;
        try
        {
            hider.WithState(() => hider.LuaSetStealth(600_000));
            caster.Receive(SpellCastSupport.CastFrame(0));
            Advance(caster, 5_000, beat: false);                           // a beat is due

            holder = new Thread(() => { using (Session.EnterScriptGate()) { gateHeld.Set(); releaseGate.Wait(Bound); } })
                     { IsBackground = true, Name = "gate holder" };
            holder.Start();
            Assert.True(gateHeld.Wait(Bound), "the gate holder never took the gate");

            beat = new Thread(() => { try { caster.TickSpellTicks(); } catch (Exception e) { beatFault = e; } })
                   { IsBackground = true, Name = "heartbeat" };
            beat.Start();
            Assert.True(SpinWait.SpinUntil(() => (beat.ThreadState & ThreadState.WaitSleepJoin) != 0, Bound),
                        "the beat never waited");

            caster.WithState(() => TearDownMethod.Invoke(caster, null));   // the logout, in the gap the gate leaves
            Assert.False(SpinWait.SpinUntil(() => !beat.IsAlive, 250),
                         "the beat finished before the gate was let go: the teardown came first, not in the gate's gap");
            Assert.False(Monitor.IsEntered(StateField.GetValue(caster)!));
            cOut.Clear();

            releaseGate.Set();
            Assert.True(beat.Join(Bound), "the beat never finished once the gate was let go");
            Assert.Null(beatFault);
            var lines = SpellCastSupport.MiniTexts(cOut);
            Assert.True(lines.Count == 0 && Ticks(caster).Count == 0 && Due(caster) == 0,
                        $"after the logout in the gate's gap: lines [{string.Join(" | ", lines)}], runs left {Ticks(caster).Count}, " +
                        $"the beat's hint {Due(caster)} (want 0)");
        }
        finally
        {
            releaseGate.Set();
            holder?.Join(Bound);
            beat?.Join(Bound);
            _fx.World.LeaveMap(hider, LogoutMap);
        }
    }

    /// <summary>A death or a won Dispell that gets in while a self beat waits for the Lua gate ends the run, and the
    /// beat, let through afterwards, says nothing: the order is checked as the logout's twin fact checks it. Red without
    /// the run check in <c>Session.LuaHiddenNear</c> (#407's review, optional): the beat names the hider once.</summary>
    [Theory]
    [InlineData("death")]
    [InlineData("dispell")]
    public void AnEndWhileTheSelfBeatWaitsForTheLuaGateSaysNothing(string how)
    {
        int at = how == "death" ? 0 : 8;
        var (hider, _, _) = Player(LogoutMap, CX + 21, CY + at);
        var (caster, cOut, _) = Caster("hear_footstep_do", LogoutMap, CX + 20, CY + at);
        var gateHeld = new ManualResetEventSlim();
        var releaseGate = new ManualResetEventSlim();
        Thread? holder = null, beat = null;
        Exception? beatFault = null;
        try
        {
            hider.WithState(() => hider.LuaSetStealth(600_000));
            caster.Receive(SpellCastSupport.CastFrame(0));
            Advance(caster, 5_000, beat: false);                           // a beat is due

            holder = new Thread(() => { using (Session.EnterScriptGate()) { gateHeld.Set(); releaseGate.Wait(Bound); } })
                     { IsBackground = true, Name = "gate holder" };
            holder.Start();
            Assert.True(gateHeld.Wait(Bound), "the gate holder never took the gate");

            beat = new Thread(() => { try { caster.TickSpellTicks(); } catch (Exception e) { beatFault = e; } })
                   { IsBackground = true, Name = "heartbeat" };
            beat.Start();
            Assert.True(SpinWait.SpinUntil(() => (beat.ThreadState & ThreadState.WaitSleepJoin) != 0, Bound),
                        "the beat never waited");

            if (how == "death") caster.WithState(() => DieMethod.Invoke(caster, null));   // in the gap the gate leaves
            else caster.ReceiveFlush(endHolds: true);
            Assert.False(SpinWait.SpinUntil(() => !beat.IsAlive, 250),
                         $"the beat finished before the gate was let go: the {how} came first, not in the gate's gap");
            cOut.Clear();

            releaseGate.Set();
            Assert.True(beat.Join(Bound), "the beat never finished once the gate was let go");
            Assert.Null(beatFault);
            var lines = SpellCastSupport.MiniTexts(cOut).Where(l => l.EndsWith("is hidden in the area.", StringComparison.Ordinal)).ToList();
            Assert.True(lines.Count == 0 && Ticks(caster).Count == 0 && Due(caster) == 0,
                        $"after a {how} in the gate's gap: lines [{string.Join(" | ", lines)}], runs left {Ticks(caster).Count}, " +
                        $"the beat's hint {Due(caster)} (want 0)");
        }
        finally
        {
            releaseGate.Set();
            holder?.Join(Bound);
            beat?.Join(Bound);
            Leave(LogoutMap, caster, hider);
        }
    }

    // ===== Assess opponent ======================================================================================

    /// <summary>At a creature: "[&lt;name&gt;] VIT: &lt;max vita&gt;" and nothing else (the readout replaces "You cast"),
    /// the 25 s wait, and no mana spent: the script checks its 550 and never takes it. Red on 0fadbfc: generic's 5
    /// mana and "You cast Assess opponent.".</summary>
    [Fact]
    public void AssessReadsACreaturesMaxVita()
    {
        var mob = AddCreature(AssessMap, CX + 1, CY, hp: 1_234, maxHp: 98_765);
        var (caster, cOut, cc) = Caster("assess_opponent_do", AssessMap, CX, CY);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            var lines = SpellCastSupport.MiniTexts(cOut);
            long wait = SpellCastSupport.AetherLeft(caster, "assess_opponent_do");
            Assert.True(lines.SequenceEqual(new[] { $"[{mob.Name}] VIT: 98765" }) && cc.Mp == Mana
                        && wait > 25_000 - SlackMs && wait <= 25_000,
                        $"lines [{string.Join(" | ", lines)}], spent {Mana - cc.Mp}, wait {wait} ms");
        }
        finally { _fx.World.LeaveMap(caster, AssessMap); _fx.World.DespawnMob(AssessMap, mob); }
    }

    /// <summary>At a player, aimed by id: "[&lt;class and rank&gt; &lt;name&gt;]", then their vita and mana with
    /// thousands commas (RTK's Tools.formatNumber; the script's Tools.formatValue exists nowhere in RTK). A Do of mark 2
    /// is a Sulsa-Do (Paths.csv). Red on 0fadbfc: generic's 5 mana and "You cast Assess opponent.".</summary>
    [Fact]
    public void AssessReadsAPlayersTitleVitaAndMana()
    {
        var (target, _, _) = _fx.PlayerWith($"DoTarget{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.ClassName = "Do"; ch.Mark = 2; ch.Level = 99;
            ch.MaxHp = 2_000_000; ch.Hp = 1_234_567;
            ch.MaxMp = 100_000; ch.Mp = 12_345;
            Wide(ch);
        }, AssessMap, CX + 3, CY + 3);
        var (caster, cOut, cc) = Caster("assess_opponent_do", AssessMap, CX, CY + 3);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0, target.PlayerId));
            var lines = SpellCastSupport.MiniTexts(cOut);
            Assert.True(lines.SequenceEqual(new[] { $"[Sulsa-Do {target.CharName}]", "VIT: 1,234,567", "MANA: 12,345" }) && cc.Mp == Mana,
                        $"lines [{string.Join(" | ", lines)}], spent {Mana - cc.Mp}");
        }
        finally { Leave(AssessMap, caster, target); }
    }

    /// <summary>Its refusals, each free and with no wait armed: at nothing, silence; short of 550, "Your will is too
    /// weak."; a creature whose protection deflects every spell, "Your magic has been deflected." (RTK's engine rolls the
    /// deflect before the script, and this row can fail); inside the wait, "Assess opponent isn't ready yet.". Red on
    /// 0fadbfc: generic's 5 mana and "You cast" each time.</summary>
    [Fact]
    public void AssessRefusalsAreFree()
    {
        var shield = AddCreature(AssessMap, CX + 1, CY + 6, maxHp: 50_000);
        shield.Protection = 100;   // 0.9^100: a fail chance that rounds to 100
        var plain = AddCreature(AssessMap, CX + 1, CY + 8, maxHp: 50_000);
        var other = AddCreature(AssessMap, CX + 1, CY + 12, maxHp: 50_000);
        var (nothing, nOut, nc) = Caster("assess_opponent_do", AssessMap, CX, CY + 10);
        var (weak, wOut, _) = Caster("assess_opponent_do", AssessMap, CX, CY + 8, mana: 549);
        var (deflected, dOut, _) = Caster("assess_opponent_do", AssessMap, CX, CY + 6);
        var (twice, tOut, _) = Caster("assess_opponent_do", AssessMap, CX, CY + 12);
        try
        {
            nothing.Receive(SpellCastSupport.CastFrame(0));
            weak.Receive(SpellCastSupport.CastFrame(0));
            deflected.Receive(SpellCastSupport.CastFrame(0));
            twice.Receive(SpellCastSupport.CastFrame(0));
            tOut.Clear();
            twice.Receive(SpellCastSupport.CastFrame(0));
            string N(RecordingOutbound o) => string.Join(" | ", SpellCastSupport.MiniTexts(o));
            long W(Session s) => SpellCastSupport.AetherLeft(s, "assess_opponent_do");
            Assert.True(N(nOut) == "" && nc.Mp == Mana && W(nothing) == 0
                        && N(wOut) == "Your will is too weak." && W(weak) == 0
                        && N(dOut) == "Your magic has been deflected." && W(deflected) == 0
                        && N(tOut) == "Assess opponent isn't ready yet.",
                        $"nothing [{N(nOut)}] wait {W(nothing)}; weak [{N(wOut)}] wait {W(weak)}; " +
                        $"deflected [{N(dOut)}] wait {W(deflected)}; twice [{N(tOut)}]");
        }
        finally
        {
            Leave(AssessMap, nothing, weak, deflected, twice);
            _fx.World.DespawnMob(AssessMap, shield); _fx.World.DespawnMob(AssessMap, plain); _fx.World.DespawnMob(AssessMap, other);
        }
    }

    // ===== Crossing swords ======================================================================================

    /// <summary>Six casts climb RTK's copy of Chung ryong rage: rage 6/9/12/18/27/50 for 3,000/10,000/22,000/38,000/
    /// 78,000/150,000 mana, AC 0 then 5 worse a tier (the caster takes more damage, as the script's recast adds armor; #407's
    /// review, F1), each tier's own line (the first names Chung Ryong's rage, as the
    /// script does), one 938 s run armed by the first cast, a 115 s wait; a seventh cast at the top does nothing,
    /// silently and for free. The wait is cleared between casts. Red on 0fadbfc: generic's 5 mana a cast and no rage.</summary>
    [Fact]
    public void CrossingSwordsClimbsSixTiers()
    {
        var (caster, cOut, cc) = Caster("crossing_swords", RageMap, CX, CY, mana: 400_000, maxMana: 400_000);
        int[] mults = { 6, 9, 12, 18, 27, 50 }, mana = { 3_000, 10_000, 22_000, 38_000, 78_000, 150_000 };
        string[] said =
        {
            "[Rage 1] You cast Chung Ryong's rage.", "[Rage 2] Crossing swords grows within you.",
            "[Rage 3] Crossing swords inspires you.", "[Rage 4] Your body trembles with incredible strength.",
            "[Rage 5] You enter a mindless frenzy.", "[Rage 6] Your body is torn apart with Crossing swords.",
        };
        try
        {
            int baseArmor = caster.ItemArmor;
            long firstEnd = 0;
            for (int tier = 1; tier <= 6; tier++)
            {
                uint before = cc.Mp;
                cOut.Clear();
                FreeSlot(caster);
                caster.Receive(SpellCastSupport.CastFrame(0));
                var lines = SpellCastSupport.MiniTexts(cOut);
                var (amount, left) = SpellCastSupport.Rage(caster);
                long wait = SpellCastSupport.AetherLeft(caster, "crossing_swords");
                int armor = caster.ItemArmor - baseArmor;
                if (tier == 1) firstEnd = Environment.TickCount64 + left;
                long end = Environment.TickCount64 + left;
                Assert.True(lines.SequenceEqual(new[] { said[tier - 1] }) && amount == mults[tier - 1] && before - cc.Mp == mana[tier - 1]
                            && armor == 5 * (tier - 1) && wait > 115_000 - SlackMs && wait <= 115_000
                            && left > 938_000 - SlackMs * 2 && Math.Abs(end - firstEnd) < SlackMs,
                            $"tier {tier}: [{string.Join(" | ", lines)}], rage {amount}, spent {before - cc.Mp}, armor {armor}, " +
                            $"wait {wait} ms, run left {left} ms");
                SpellCastSupport.ClearAether(caster, "crossing_swords");
            }
            uint top = cc.Mp;
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(SpellCastSupport.MiniTexts(cOut).Count == 0 && cc.Mp == top && SpellCastSupport.Rage(caster).Amount == 50,
                        $"at the top: [{string.Join(" | ", SpellCastSupport.MiniTexts(cOut))}], spent {top - cc.Mp}");
        }
        finally { _fx.World.LeaveMap(caster, RageMap); }
    }

    /// <summary>Inside its 115 s wait, "Crossing swords isn't ready yet."; short of a tier's mana, "Your will is too
    /// weak.". Both free. Red on 0fadbfc: generic's 5 mana and "You cast Crossing swords.".</summary>
    [Fact]
    public void CrossingSwordsRefusesInsideItsWaitAndShortOfMana()
    {
        var (caster, cOut, cc) = Caster("crossing_swords", RageMap, CX, CY + 2);
        var (poor, pOut, pc) = Caster("crossing_swords", RageMap, CX, CY + 4, mana: 2_999);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            uint after = cc.Mp;
            cOut.Clear();
            caster.Receive(SpellCastSupport.CastFrame(0));
            string again = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            poor.Receive(SpellCastSupport.CastFrame(0));
            string short_ = string.Join(" | ", SpellCastSupport.MiniTexts(pOut));
            Assert.True(again == "Crossing swords isn't ready yet." && cc.Mp == after && SpellCastSupport.Rage(caster).Amount == 6
                        && short_ == "Your will is too weak." && pc.Mp == 2_999 && SpellCastSupport.Rage(poor).Amount == 1,
                        $"again [{again}] spent {after - cc.Mp}; short [{short_}] mana {pc.Mp}");
        }
        finally { _fx.World.LeaveMap(caster, RageMap); _fx.World.LeaveMap(poor, RageMap); }
    }

    /// <summary>A Crossing swords climb that lapses keeps its tier's share of vita (RTK's uncast: 0.8, 0.8, 0.8, 0.6,
    /// 0.4, 0.1), takes no mana and says nothing. Red without the row ladder's price in
    /// <c>Session.ChungRyongRageWearOff</c>: Chung Ryong's table (tier 6 leaves 1 vita and 1 mana, and every tier says
    /// "Chung Ryong's rage leaves you drained.").</summary>
    [Theory]
    [InlineData(1, 8_000u)]
    [InlineData(4, 6_000u)]
    [InlineData(5, 4_000u)]
    [InlineData(6, 1_000u)]
    public void ALapsedClimbKeepsItsTiersShareOfVita(int tier, uint kept)
    {
        var (caster, cOut, cc) = Caster("crossing_swords", RageMap, CX + 6, CY, mana: 400_000, maxMana: 400_000);
        try
        {
            for (int i = 0; i < tier; i++)
            {
                FreeSlot(caster);
                caster.Receive(SpellCastSupport.CastFrame(0));
                SpellCastSupport.ClearAether(caster, "crossing_swords");
            }
            uint mana = cc.Mp;
            cOut.Clear();
            caster.WithState(() => RageUntilField.SetValue(caster, Environment.TickCount64 - 1));
            caster.RegenTick(0, regenDue: false);
            var lines = SpellCastSupport.MiniTexts(cOut);
            Assert.True(cc.Hp == kept && cc.Mp == mana && lines.Count == 0 && caster.LuaCrRageTier == 0,
                        $"tier {tier}: vita {cc.Hp} (want {kept}), mana {mana} -> {cc.Mp}, lines [{string.Join(" | ", lines)}], " +
                        $"tier left {caster.LuaCrRageTier}");
        }
        finally { _fx.World.LeaveMap(caster, RageMap); }
    }

    /// <summary>A climb that lapsed while its caster was logged off pays Crossing swords' price on the first beat back, a
    /// tenth of the vita at tier 6, found by the fury's saved name; nothing new is saved for it. Red without the name
    /// restored in <c>RestoreTimedEffects</c>: Chung Ryong's price, 1 vita and 1 mana and its line.</summary>
    [Fact]
    public void ALapseRestoredAtLoginPaysCrossingSwordsPrice()
    {
        var name = Content.SpellByKey("crossing_swords")!.Name;
        var (before, _, c1) = _fx.PlayerWith($"DoRelog{Interlocked.Increment(ref _serial)}", ch =>
            { ch.Hp = 5_000; ch.MaxHp = 5_000; ch.Mp = 900; ch.MaxMp = 900; }, SessionFixture.HomeMap, x: 5, y: 15);
        TimedEffects saved;
        try
        {
            before.WithState(() => before.LuaSetCrRage(tier: 6, mult: 50, ac: -25, durMs: 300, name: name));
            before.WithState(() => CaptureMi.Invoke(before, null));
            saved = c1.Effects;
            Assert.Equal((6, name), (saved.CrRageTier, saved.RageName));
        }
        finally { _fx.World.LeaveMap(before, SessionFixture.HomeMap); }

        Assert.True(SpinWait.SpinUntil(() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > saved.RageUntil, 5_000),
                    "the saved climb never lapsed");

        var (after, outbound, c2) = _fx.PlayerWith($"DoRelogBack{Interlocked.Increment(ref _serial)}", ch =>
            { ch.Hp = 5_000; ch.MaxHp = 5_000; ch.Mp = 900; ch.MaxMp = 900; ch.Effects = saved; },
            SessionFixture.HomeMap, x: 6, y: 15);
        try
        {
            after.WithState(() => RestoreMi.Invoke(after, null));
            outbound.Clear();
            after.RegenTick(0, regenDue: false);
            var lines = SpellCastSupport.MiniTexts(outbound);
            Assert.True(c2.Hp == 500 && c2.Mp == 900 && !lines.Contains("Chung Ryong's rage leaves you drained.") && after.LuaCrRageTier == 0,
                        $"vita {c2.Hp} (want 500), mana {c2.Mp} (want 900), lines [{string.Join(" | ", lines)}]");
        }
        finally { _fx.World.LeaveMap(after, SessionFixture.HomeMap); }
    }

    /// <summary>A climb continues only its own fury: with Chung Ryong's rage at tier 3, Crossing swords starts at its
    /// tier 1, as the script's <c>hasDuration("crossing_swords")</c> reads it. Red with the name check taken out of the
    /// verb: it climbs to tier 4 (rage 18, 38,000 mana).</summary>
    [Fact]
    public void CrossingSwordsDoesNotClimbAnotherFury()
    {
        var (caster, cOut, cc) = Caster("crossing_swords", RageMap, CX + 6, CY + 4);
        try
        {
            caster.WithState(() => caster.LuaSetCrRage(tier: 3, mult: 12, ac: -5, durMs: 600_000, name: "Chung Ryong's rage"));
            caster.Receive(SpellCastSupport.CastFrame(0));
            var lines = SpellCastSupport.MiniTexts(cOut);
            Assert.True(lines.SequenceEqual(new[] { "[Rage 1] You cast Chung Ryong's rage." }) && SpellCastSupport.Rage(caster).Amount == 6
                        && cc.Mp == Mana - 3_000 && caster.LuaCrRageTier == 1,
                        $"lines [{string.Join(" | ", lines)}], rage {SpellCastSupport.Rage(caster).Amount}, spent {Mana - cc.Mp}");
        }
        finally { _fx.World.LeaveMap(caster, RageMap); }
    }

    /// <summary>Chung Ryong's own rage, on the same verb, keeps its own table: tier 1 for 2,000 with its line, rage x6.
    /// The second cast, straight after with nothing cleared, meets its 120 s wait (Q16) and is refused for free. A guard on
    /// the row-driven verb; red with Chung Ryong's gate taken out (#407's review, F2: the cast climbed to tier 2).</summary>
    [Fact]
    public void ChungRyongsRageClimbsItsOwnTable()
    {
        var (caster, cOut, cc) = Caster("chung_ryongs_rage", RageMap, CX + 6, CY + 8, cls: "Chung ryong");
        string name = Content.SpellByKey("chung_ryongs_rage")!.Name;
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            caster.Receive(SpellCastSupport.CastFrame(0));
            var lines = SpellCastSupport.MiniTexts(cOut);
            Assert.True(lines.SequenceEqual(new[] { "[Rage 1] You cast Chung Ryong's rage.", $"{name} isn't ready yet." })
                        && cc.Mp == Mana - 2_000 && SpellCastSupport.Rage(caster).Amount == 6 && caster.LuaCrRageTier == 1,
                        $"lines [{string.Join(" | ", lines)}], spent {Mana - cc.Mp}, rage {SpellCastSupport.Rage(caster).Amount}, " +
                        $"tier {caster.LuaCrRageTier}");
        }
        finally { _fx.World.LeaveMap(caster, RageMap); }
    }

    /// <summary>Chung Ryong's rage refuses a climb inside its 120 s wait, with the same line as Crossing swords' wait and
    /// for free, and the next climb after the wait lands (owner, 2026-10-26, Q16: "As RTK"). No staff waiver, as for
    /// Crossing swords. Red on 65cfc39, where nothing checked the wait and the second cast climbed at once.</summary>
    [Fact]
    public void ChungRyongsRageRefusesAClimbInsideItsWait()
    {
        var (caster, cOut, cc) = Caster("chung_ryongs_rage", RageMap, CX + 6, CY + 12, cls: "Chung ryong");
        string name = Content.SpellByKey("chung_ryongs_rage")!.Name;
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            uint after = cc.Mp;
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            string refused = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            int tierThen = caster.LuaCrRageTier;
            uint spentThen = after - cc.Mp;

            SpellCastSupport.ClearAether(caster, "chung_ryongs_rage");
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            string climbed = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            Assert.True(refused == $"{name} isn't ready yet." && tierThen == 1 && spentThen == 0
                        && climbed == "[Rage 2] Chung Ryong's power grows in you." && caster.LuaCrRageTier == 2 && after - cc.Mp == 7_200,
                        $"inside the wait: [{refused}], tier {tierThen}, spent {spentThen}; after it: [{climbed}], tier " +
                        $"{caster.LuaCrRageTier}, spent {after - cc.Mp}");
        }
        finally { _fx.World.LeaveMap(caster, RageMap); }
    }

    // ===== the rows and the teacher ===========================================================================

    /// <summary>All nine are Do's path's rows at the level their scripts' <c>requirements()</c> ask, 99, as #406 set
    /// Shaman's; Balance weapon and Master weapon keep their marks 1 and 2. Red on ccdf2da for the six unmarked rows that
    /// said 0; Ki strike's (already 99) and the two marked rows' (their mark already loads them at 99,
    /// <c>Content.MarkSpellLevel</c>) are guards.</summary>
    [Theory]
    [InlineData("dash_do", 0)]
    [InlineData("spirit_blade", 0)]
    [InlineData("blend", 0)]
    [InlineData("hear_footstep_do", 0)]
    [InlineData("assess_opponent_do", 0)]
    [InlineData("balance_weapon", 1)]
    [InlineData("master_weapon", 2)]
    [InlineData("ki_strike", 0)]
    [InlineData("crossing_swords", 0)]
    public void TheRowsAreDosAtTheirScriptsLevels(string key, int mark)
    {
        var sp = Content.SpellByKey(key)!;
        Assert.Equal((18, 99, mark), (sp.PathId, sp.Level, sp.Mark));
    }

    /// <summary>The Do teacher's list by rank: the seven unmarked spells from mark 0, Balance weapon from mark 1 and
    /// Master weapon from mark 2, each at its script's level, 99. Red without the price rows: Ki strike and Spirit blade
    /// alone.</summary>
    [Theory]
    [InlineData(0, "assess_opponent_do blend crossing_swords dash_do hear_footstep_do ki_strike spirit_blade")]
    [InlineData(1, "assess_opponent_do balance_weapon blend crossing_swords dash_do hear_footstep_do ki_strike spirit_blade")]
    [InlineData(2, "assess_opponent_do balance_weapon blend crossing_swords dash_do hear_footstep_do ki_strike master_weapon spirit_blade")]
    public void TheDoTeacherSellsByRank(int mark, string expected)
    {
        var sold = Content.SubpathTeacherSpells(18, 99, mark);
        Assert.All(sold, s => Assert.Equal(99, s.Level));
        Assert.Equal(expected, string.Join(" ", sold.Select(s => s.Key).OrderBy(k => k, StringComparer.Ordinal)));
    }

    // ===== helpers ============================================================================================

    private sealed record TickView(string Spell, string Verb, bool Self, int Every, long NextIn, long UntilIn, bool Ended);

    /// <summary>The caster's running ticks, read under their monitor; times are milliseconds from now.</summary>
    private static List<TickView> Ticks(Session s)
    {
        var list = new List<TickView>();
        s.WithState(() =>
        {
            long now = Environment.TickCount64;
            foreach (var t in (IList)TicksField.GetValue(s)!)
                list.Add(new TickView((string)F(t, "SpellKey"), (string)F(t, "Verb"), (bool)F(t, "Self"), (int)F(t, "EveryMs"),
                                      (long)F(t, "Next") - now, (long)F(t, "Until") - now, (bool)F(t, "Ended")));
        });
        return list;
    }

    /// <summary>Move the caster's runs <paramref name="ms"/> on, as <c>ShamanSpellTests</c> moves them, then, unless
    /// <paramref name="beat"/> is false, run the heartbeat's call.</summary>
    private static void Advance(Session s, long ms, bool beat = true)
    {
        s.WithState(() =>
        {
            foreach (var t in (IList)TicksField.GetValue(s)!)
            {
                t.GetType().GetField("Next")!.SetValue(t, (long)F(t, "Next") - ms);
                t.GetType().GetField("Until")!.SetValue(t, (long)F(t, "Until") - ms);
            }
            PublishTickDue.Invoke(s, null);
        });
        if (beat) s.TickSpellTicks();
    }

    private static object F(object o, string name) => o.GetType().GetField(name)!.GetValue(o)!;

    private static readonly FieldInfo DueField =
        typeof(Session).GetField("_spellTickDue", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>The beat's pre-check hint (<c>Session._spellTickDue</c>): 0 when the caster holds no run at all, so the
    /// heartbeat enters nobody's monitor for it. An ended run leaves it due at once until a beat drops the run.</summary>
    private static long Due(Session s)
    {
        long due = 0;
        s.WithState(() => due = (long)DueField.GetValue(s)!);
        return due;
    }

    private static readonly FieldInfo NextActionTick =
        typeof(Session).GetField("_nextActionTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ActionCount =
        typeof(Session).GetField("_actionCount", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>Let the next cast run now: free the shared cast/swing slot a stealth cast arms
    /// (<c>Session.ArmActionSlot</c>, <c>Content.CastDelayMs</c>) and start a fresh action budget (RTK's three actions
    /// a second, <c>Session.ActionAllowed</c>), either of which would otherwise hold a quick recast in the queue.</summary>
    private static void FreeSlot(Session s) => s.WithState(() => { NextActionTick.SetValue(s, 0L); ActionCount.SetValue(s, 0); });

    private static long StealthLeft(Session s)
    {
        long left = 0;
        s.WithState(() => left = (long)StealthUntilField.GetValue(s)! - Environment.TickCount64);
        return left;
    }

    private static string BuffBox(Session s)
    {
        string box = "";
        s.WithState(() => box = (string)BuffBoxMethod.Invoke(s, null)!);
        return box;
    }

    private static uint Id(byte[] effectBody) =>
        (uint)((effectBody[0] << 24) | (effectBody[1] << 16) | (effectBody[2] << 8) | effectBody[3]);

    /// <summary>The spell graphics (0x29) <paramref name="outbound"/> was sent over <paramref name="id"/>, as effect ids.</summary>
    private static List<int> Effects(RecordingOutbound outbound, uint id) =>
        outbound.BodiesOf(ServerOp.Effect).Where(b => Id(b) == id).Select(b => b[4] - ServerConfig.Current.EfxWireOffset).ToList();

    /// <summary>The first tile on the Do Guide map facing east from which <paramref name="open"/> tiles are walkable and
    /// the next is blocked, the caster's own tile walkable.</summary>
    private static (int X, int Y) TileEastOfOpenThenBlocked(int open)
    {
        var md = MapData.For(DashMap)!;
        for (int y = 1; y < 22; y++)
            for (int x = 1; x + open + 1 < 23; x++)
            {
                if (md.Solid(x, y)) continue;
                bool fits = true;
                for (int i = 1; i <= open; i++) fits &= !md.BlockedMove(x + i, y, 1);
                if (fits && md.BlockedMove(x + open + 1, y, 1)) return (x, y);
            }
        throw new InvalidOperationException($"no tile on map {DashMap} with {open} open and then a block to the east");
    }

    private Mob AddCreature(ushort map, int x, int y, int hp = 50_000, int maxHp = 50_000)
    {
        var mob = new Mob(_fx.World.AllocateMobId(), 1, (ushort)x, (ushort)y, $"Do target {Interlocked.Increment(ref _serial)}", maxHp);
        mob.Hp = hp;
        _fx.World.AddMob(map, mob);
        return mob;
    }

    /// <summary>A level-99 Do at (<paramref name="x"/>, <paramref name="y"/>) facing east, with <paramref name="key"/> in
    /// slot 0 (and <paramref name="extra"/> in slot 1). On the Do Guide map the map's own size; elsewhere a wide one.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Caster(string key, ushort map, int x, int y,
                                                                            uint mana = Mana, uint maxMana = MaxMana,
                                                                            string? extra = null, string cls = "Do") =>
        _fx.PlayerWith($"Do{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.ClassName = cls;
            ch.Level = 99;
            ch.Ac = 0;   // the base AC sits inside ItemArmor's clamp, so a tier's AC shows
            ch.MaxHp = Hp; ch.Hp = Hp;
            ch.MaxMp = maxMana; ch.Mp = mana;
            ch.Dir = 1;
            if (map == DashMap) { ch.MapXs = 23; ch.MapYs = 23; } else Wide(ch);
            ch.Spells.Add(Content.SpellByKey(key)!.Id);
            if (extra is not null) ch.Spells.Add(Content.SpellByKey(extra)!.Id);
        }, map, (ushort)x, (ushort)y);

    private (Session session, RecordingOutbound outbound, Character c) Player(ushort map, int x, int y) =>
        _fx.PlayerWith($"DoOther{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 50;
            ch.MaxHp = 1_000; ch.Hp = 1_000;
            Wide(ch);
        }, map, (ushort)x, (ushort)y);

    // ---- round 3: the stealth swings ----------------------------------------------------------------------------

    private static readonly string[] RogueStealths = { "invisible_rogue", "spirits_form_rogue", "lifes_cloak_rogue", "glass_form_rogue" };

    private static readonly FieldInfo StealthTimesField =
        typeof(Session).GetField("_stealthTimes", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly Type SwingTargetType =
        typeof(Session).GetNestedType("SwingTarget", BindingFlags.NonPublic)!;
    private static readonly MethodInfo SwingRaw =
        typeof(Session).GetMethod("PlayerSwingRaw", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>The armed stealth's sneak multiplier (<c>Session._stealthTimes</c>).</summary>
    private static int StealthTimes(Session s)
    {
        int times = 0;
        s.WithState(() => times = (int)StealthTimesField.GetValue(s)!);
        return times;
    }

    /// <summary>One swing's raw figure (<c>Session.PlayerSwingRaw</c>, before the floor) at an AC-0, level-1 target, run in
    /// the swinger's monitor as the swing handler runs it: whether it landed, the figure, and whether it was a crit. A
    /// landed swing ends a stealth, as every landed swing does.</summary>
    private static (bool Hit, double Raw, bool Crit) SwingOnce(Session s)
    {
        object target = Activator.CreateInstance(SwingTargetType, 0, -95, 0, 1, 11, 10, (byte)3)!;
        (bool, double, bool) got = default;
        s.WithState(() =>
        {
            var (hit, raw, crit, _) = ((bool, double, bool, bool))SwingRaw.Invoke(s, new[] { target, 1.0 })!;
            got = (hit, raw, crit);
        });
        return got;
    }

    /// <summary>Every distinct landed non-crit figure, rounded to 6 places, of 600 swings with no stealth up. With the
    /// novice sword's S 10-12 that is three figures.</summary>
    private static SortedSet<double> PlainSwings(Session s)
    {
        Assert.False(s.IsInvisible, "plain swings measured while invisible");
        var seen = new SortedSet<double>();
        for (int i = 0; i < 600; i++)
        {
            var (hit, raw, crit) = SwingOnce(s);
            if (hit && !crit) seen.Add(Math.Round(raw, 6));
        }
        return seen;
    }

    /// <summary>Up to five landed non-crit figures, each the first landed swing after casting the stealth in book slot
    /// <paramref name="slot"/> (the real 0x0F frame; the slot, the action budget and the wait freed first, so it casts
    /// at once). A swing that misses leaves the stealth up and the next swing tries again.</summary>
    private static List<double> StealthSwings(Session s, int slot)
    {
        var key = Content.SpellById(CharSpells(s)[slot])!.Key;
        var seen = new List<double>();
        for (int i = 0; i < 300 && seen.Count < 5; i++)
        {
            if (!s.IsInvisible)
            {
                FreeSlot(s);
                SpellCastSupport.ClearAether(s, key);
                s.Receive(SpellCastSupport.CastFrame(slot));
                Assert.True(s.IsInvisible, $"{key} did not hide its caster");
            }
            var (hit, raw, crit) = SwingOnce(s);
            if (hit && !crit) seen.Add(raw);
        }
        return seen;
    }

    private static readonly FieldInfo CharField =
        typeof(Session).GetField("_char", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static List<int> CharSpells(Session s)
    {
        List<int> spells = new();
        s.WithState(() => spells = new List<int>(((Character)CharField.GetValue(s)!).Spells));
        return spells;
    }

    /// <summary>A level-99 Do with a novice sword, Might 50 and Grace 50, facing east, with <paramref name="keys"/> in its
    /// book from slot 0 and lots of mana.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Swinger(ushort map, int x, int y, params string[] keys) =>
        Swinger(map, x, y, keys, null);

    private (Session session, RecordingOutbound outbound, Character c) Swinger(ushort map, int x, int y, string key,
                                                                             Action<Character> configure) =>
        Swinger(map, x, y, new[] { key }, configure);

    private (Session session, RecordingOutbound outbound, Character c) Swinger(ushort map, int x, int y, string[] keys,
                                                                             Action<Character>? configure) =>
        _fx.PlayerWith($"DoSwing{Interlocked.Increment(ref _serial)}", ch =>
        {
            var sword = Content.ItemByKey("novice_sword")!;
            ch.ClassName = "Do";
            ch.Level = 99;
            ch.Ac = 0;
            ch.Might = 50; ch.Grace = 50;
            ch.MaxHp = Hp; ch.Hp = Hp;
            ch.MaxMp = MaxMana; ch.Mp = Mana;
            ch.Dir = 1;
            Wide(ch);
            ch.Equipment.Add(new InvItem { Slot = sword.EquipSlot, ItemId = sword.Id, Dura = sword.Durability });
            foreach (var k in keys) ch.Spells.Add(Content.SpellByKey(k)!.Id);
            configure?.Invoke(ch);
        }, map, (ushort)x, (ushort)y);

    /// <summary>Transformed for a minute, through the same two steps the morph verb takes, with a real Morphs.csv row
    /// (<c>EmotePickupStateTests</c>' helper).</summary>
    private static void Morph(Session s)
    {
        var sp = Content.SpellByKey("kwisin_cloak_rogue")!;
        var m = Content.MorphFor(sp)!.Value;
        s.WithState(() =>
        {
            s.LuaSetPendingMorph(m.Look, m.LookFemale, 0, 60_000);
            s.LuaApplyMorph(sp);
        });
        Assert.True(s.IsMorphed);
    }

    /// <summary>Content-free maps have no size, and the cast handler needs one.</summary>
    private static void Wide(Character c) { c.MapXs = 100; c.MapYs = 100; }

    private void Leave(ushort map, params Session[] sessions)
    {
        foreach (var s in sessions) _fx.World.LeaveMap(s, map);
    }
}
