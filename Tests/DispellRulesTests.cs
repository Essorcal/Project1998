using System.Diagnostics;
using System.Reflection;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Caleb's three rules of 2026-10-10 for the Dispell family (Dispell, Remove Magic, Return Natural, Restore Balance)
/// cast at ANOTHER player. The sources are surveyed in the coordination workspace's
/// <c>briefs/reports/dispell-sources-survey-2026-10-10.md</c>; the rules are Caleb's, made on that survey.
/// <list type="number">
/// <item><b>Staff are exempt.</b> A cast at a GM fizzles: "Fizzle.", no mana, nothing cleared, whoever casts it.</item>
/// <item><b>A won cast ends sleep and venom.</b> Besides the buff list PR #339 clears, the target wakes from a Doze
///   (the hold, and the harder next hit it leaves) and stops being poisoned, inside the target's own monitor.</item>
/// <item><b>A lost roll matches RTK</b> (<c>poet/dispell.lua:37-40</c>): "Something went wrong." alone, no "You cast
///   X.", no cast pose, and no mana.</item>
/// </list>
/// On 2026-10-11 Caleb made the second and third hold for a Dispell on yourself too ("Same rules on self", section 4
/// below). The first stays a rule about another player: a GM's Dispell on themselves still lands.
///
/// <para>Every cast is the real 0x0F frame through <c>Session.Receive</c>, from a caster who casts once, so the
/// packet handler's action budget (three gated actions a second) never decides a fact. A won roll is certain where
/// a fact needs one: a caster with Will 255 against a target with the default AC (99, clamped to 70) and Will 3
/// rolls against 108. A lost one is likely where a fact needs one, and the fact casts again from a fresh caster
/// until it has one: a caster with Will 0 against Will 255 and AC -60 rolls against 18. Content-free maps no other
/// class uses.</para>
///
/// <para>The sleep, the harder next hit and the venom are put on the target with the writers the casts use
/// (<c>ReceiveSleep</c> and <c>ArmDamageAmp</c> for a Doze, <c>ReceivePoison</c> for a venom), with ten minutes to
/// run and no world tick, so nothing but the Dispell can end them inside a fact.</para>
///
/// <para><b>Collection "world".</b> The constructor writes the staff roster (<c>StaffAccounts.Load</c>), which is
/// one for the whole process, with the same name and content every GM class writes, so the order the classes run
/// in cannot demote anyone.</para>
/// </summary>
[Collection("world")]
public sealed class DispellRulesTests
{
    private const ushort StaffMap = 62396, StaffSelfMap = 62397, WakeMap = 62398, HeldMap = 62399, RollMap = 62400,
                         SelfLostMap = 62401, SelfWonMap = 62402, SelfAsleepMap = 62403;

    /// <summary>The staff name and roster every GM class in the suite writes (see <c>ApproachSummonTests</c>).</summary>
    private const string GmName = "cmdgm";

    /// <summary>The probe entries a target carries before a Dispell: a stat buff and a categorised status in the
    /// <c>paras</c> slot (a Human Barrier's hold), as in <c>DispellTargetLockTests</c>; and a Doze and a venom.</summary>
    private const string BuffKey = "dispell_rule_might", HoldKey = "dispell_rule_hold",
                         DozeKey = "dispell_rule_doze", VenomKey = "dispell_rule_venom";

    private const uint Mana = 10_000, Cost = 200;
    private const int TenMinutes = 600_000;
    private const double DozeAmp = 1.3;

    private readonly SessionFixture _fx;
    private static int _serial;

    private static readonly MethodInfo ApplyCast =
        typeof(Session).GetMethod("ApplyCast", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo RageUntilField =
        typeof(Session).GetField("_rageUntil", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo RageAmountField =
        typeof(Session).GetField("_rageAmount", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo DamageAmpField =
        typeof(Session).GetField("_dmgAmp", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo DamageAmpUntilField =
        typeof(Session).GetField("_dmgAmpUntil", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo PoisonNextTickField =
        typeof(Session).GetField("_poisonNextTick", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public DispellRulesTests(SessionFixture fx)
    {
        _fx = fx;
        lock (TestProcessState.Gate)
        {
            Directory.CreateDirectory(TestProcessState.StateDirectory);
            File.WriteAllText(Path.Combine(TestProcessState.StateDirectory, "gm_accounts.txt"), GmName + "\n");
            StaffAccounts.Load();
        }
    }

    public static TheoryData<string> DispellFamily => new()
        { "dispell_poet", "remove_magic_poet", "return_natural_poet", "restore_balance_poet" };

    public static TheoryData<string, bool> DispellFamilyByCaster
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (var key in new[] { "dispell_poet", "remove_magic_poet", "return_natural_poet", "restore_balance_poet" })
            {
                data.Add(key, false);
                data.Add(key, true);
            }
            return data;
        }
    }

    // ===== 1. staff are exempt ========================================================================

    /// <summary>Each of the four, cast at a GM through the real frame, fizzles: the caster reads "Fizzle." and
    /// nothing else, keeps all 200 mana and shows no cast pose; the GM is told nothing and keeps the buff, the hold,
    /// the fury, the Doze and the venom. A GM caster is refused the same as a player: the exemption is about the
    /// target (Approach and Summon let a GM through to staff; this does not).
    /// <para>Red on 93b079c: the cast goes through. The caster pays 200 and reads "You cast X.", and the GM loses
    /// the buff, the hold and the fury.</para></summary>
    [Theory]
    [MemberData(nameof(DispellFamilyByCaster))]
    public void ADispellFamilyCastAtStaffFizzlesAndCostsNothing(string key, bool gmCaster)
    {
        var sp = Content.SpellByKey(key)!;
        var (gm, gmOut, _) = Plain(GmName, StaffMap, 10, 10);
        var (d, dOut, dc) = Poet(gmCaster ? GmName : null, StaffMap, 12, 10, will: 255, sp);
        try
        {
            Assert.True(gm.LuaIsGm, "the target is not on the staff roster");
            Assert.Equal(gmCaster, d.LuaIsGm);
            GiveEffects(gm);
            GiveHolds(gm);
            gmOut.Clear();

            d.Receive(SpellCastSupport.CastFrame(0, gm.PlayerId));

            Assert.Equal(new[] { "Fizzle." }, SpellCastSupport.MiniTexts(dOut));
            Assert.Equal(Mana, dc.Mp);
            Assert.Empty(dOut.BodiesOf(ServerOp.Action));
            Assert.Empty(SpellCastSupport.MiniTexts(gmOut));
            AssertEffectsKept(gm);
            AssertHoldsKept(gm);
        }
        finally
        {
            _fx.World.LeaveMap(gm, StaffMap);
            _fx.World.LeaveMap(d, StaffMap);
        }
    }

    /// <summary>What the exemption leaves alone. A GM's Dispell on themselves still clears their own buff and fury
    /// (against yourself the rate is at most 95%, so a lost roll is cast again; since 2026-10-11 a lost roll on
    /// yourself is refused at no cost, as for anyone, so only the won cast returns true and pays 200). A GM's Dispell
    /// at a player still lands, for 200 and "You cast Dispell.". Green before and after the staff rule.</summary>
    [Fact]
    public void AGmStillDispellsThemselvesAndAPlayer()
    {
        var sp = Content.SpellByKey("dispell_poet")!;
        var (gm, gmOut, gmc) = Poet(GmName, StaffSelfMap, 10, 10, will: 255, sp);
        var (v, vOut, _) = Plain("DispellRuleV", StaffSelfMap, 12, 10);
        try
        {
            Assert.True(gm.LuaIsGm);
            GiveEffects(gm);
            int casts = 0;
            while (SpellCastSupport.BuffEntry(gm, BuffKey) is not null && casts < 20)
            {
                bool ok = false;
                gm.WithState(() => ok = (bool)ApplyCast.Invoke(gm, new object?[] { sp, gm.PlayerId, null })!);
                Assert.Equal(SpellCastSupport.BuffEntry(gm, BuffKey) is null, ok);
                casts++;
            }
            AssertEffectsCleared(gm);
            Assert.Equal(Mana - Cost, gmc.Mp);
            Assert.DoesNotContain("Fizzle.", SpellCastSupport.MiniTexts(gmOut));

            GiveEffects(v);
            gmOut.Clear();
            uint before = gmc.Mp;
            gm.Receive(SpellCastSupport.CastFrame(0, v.PlayerId));
            Assert.Equal(new[] { "You cast Dispell." }, SpellCastSupport.MiniTexts(gmOut));
            Assert.Equal(before - Cost, gmc.Mp);
            AssertEffectsCleared(v);
            Assert.Contains($"{gm.CharName} casts Dispell on you.", SpellCastSupport.MiniTexts(vOut));
        }
        finally
        {
            _fx.World.LeaveMap(gm, StaffSelfMap);
            _fx.World.LeaveMap(v, StaffSelfMap);
        }
    }

    // ===== 2. a won cast ends sleep and venom =========================================================

    /// <summary>Each of the four, won at another player through the real frame, wakes them from a Doze, takes the
    /// harder next hit the Doze left armed, and ends their venom, as well as clearing the buff, the hold and the
    /// fury. The target reads "You wake up." and "The poison passes." (the lines the server's own wake and cure
    /// send) and "&lt;caster&gt; casts X on you."; the caster pays 200 and reads "You cast X.".
    /// <para>Red on 93b079c: the slot entries go, but the target is still asleep and still poisoned, and the next
    /// hit on them is still 1.3x.</para></summary>
    [Theory]
    [MemberData(nameof(DispellFamily))]
    public void AWonDispellFamilyCastWakesAnotherPlayerAndEndsTheirVenom(string key)
    {
        var sp = Content.SpellByKey(key)!;
        var (v, vOut, _) = Plain("DispellWakeV", WakeMap, 20, 20);
        var (d, dOut, dc) = Poet(null, WakeMap, 22, 20, will: 255, sp);
        try
        {
            GiveEffects(v);
            GiveHolds(v);
            vOut.Clear();

            d.Receive(SpellCastSupport.CastFrame(0, v.PlayerId));

            Assert.Equal(new[] { $"You cast {sp.Name}." }, SpellCastSupport.MiniTexts(dOut));
            Assert.Equal(Mana - Cost, dc.Mp);
            AssertEffectsCleared(v);
            AssertHoldsEnded(v);
            var vLines = SpellCastSupport.MiniTexts(vOut);
            Assert.Contains("You wake up.", vLines);
            Assert.Contains("The poison passes.", vLines);
            Assert.Contains($"{d.CharName} casts {sp.Name} on you.", vLines);
        }
        finally
        {
            _fx.World.LeaveMap(v, WakeMap);
            _fx.World.LeaveMap(d, WakeMap);
        }
    }

    /// <summary>The wake and the cure are written inside the target's monitor, the shape PR #339 gave the buff list.
    /// A third thread takes the target's monitor and holds it while the caster's won Dispell runs on its own thread:
    /// while it holds it, the cast does not finish and the target is still asleep, still under the harder next hit
    /// and still poisoned. Once it lets go, the cast finishes and ends all three. Both orders of the two players'
    /// <c>StateRank</c>, so both of <c>EnterState</c>'s branches are the ones that wait.
    /// <para>Red on 93b079c: the cast waits, but ends none of the three. Red with the wake or the cure moved
    /// outside the target's monitor: in Release they end while the holder still holds it, and in Debug the
    /// <c>_buffs</c> guard fires inside the verb.</para></summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AWonDispellEndsSleepAndVenomOnlyInsideTheTargetsMonitor(bool casterRanksFirst)
    {
        var sp = Content.SpellByKey("dispell_poet")!;
        Session d, v;
        RecordingOutbound dOut, vOut;
        Character dc;
        if (casterRanksFirst)
        {
            (d, dOut, dc) = Poet(null, HeldMap, 30, 30, will: 255, sp);
            (v, vOut, _) = Plain("DispellWakeHeldV", HeldMap, 32, 30);
        }
        else
        {
            (v, vOut, _) = Plain("DispellWakeHeldV", HeldMap, 32, 30);
            (d, dOut, dc) = Poet(null, HeldMap, 30, 30, will: 255, sp);
        }
        Assert.Equal(casterRanksFirst, d.StateRank < v.StateRank);

        var held = new ManualResetEventSlim();
        var letGo = new ManualResetEventSlim();
        bool heldWhileHeld = false;
        Exception? holderFault = null, casterFault = null;
        var holder = new Thread(() =>
        {
            try
            {
                v.WithState(() =>
                {
                    held.Set();
                    letGo.Wait();
                    heldWhileHeld = v.Asleep && v.Poisoned && (double)DamageAmpField.GetValue(v)! == DozeAmp;
                });
            }
            catch (Exception e) { holderFault = e; }
        }) { IsBackground = true };
        var caster = new Thread(() =>
        {
            try { d.Receive(SpellCastSupport.CastFrame(0, v.PlayerId)); }
            catch (Exception e) { casterFault = e; }
        }) { IsBackground = true };
        try
        {
            GiveEffects(v);
            GiveHolds(v);
            holder.Start();
            Assert.True(held.Wait(TimeSpan.FromSeconds(30)), "the holder never took the target's monitor");

            caster.Start();
            WaitUntilDoneOrBlocked(caster);
            bool finishedWhileHeld = !caster.IsAlive;
            letGo.Set();
            Assert.True(holder.Join(TimeSpan.FromSeconds(60)), "the holder never let go");
            Assert.True(caster.Join(TimeSpan.FromSeconds(60)), "the cast never finished after the monitor was free");

            Assert.Null(holderFault);
            Assert.Null(casterFault);
            Assert.False(finishedWhileHeld, "the Dispell finished while another thread held the target's monitor");
            Assert.True(heldWhileHeld, "the target woke or was cured while another thread held its monitor");

            Assert.Contains($"You cast {sp.Name}.", SpellCastSupport.MiniTexts(dOut));
            Assert.Equal(Mana - Cost, dc.Mp);
            AssertEffectsCleared(v);
            AssertHoldsEnded(v);
        }
        finally
        {
            letGo.Set();
            // A cast still stuck on the monitor would hold the Lua gate; leaving the map would then wait on it.
            if (!holder.IsAlive && !caster.IsAlive)
            {
                _fx.World.LeaveMap(v, HeldMap);
                _fx.World.LeaveMap(d, HeldMap);
            }
        }
    }

    // ===== 3. a lost roll matches RTK =================================================================

    /// <summary>Each of the four, cast at another player through the real frame and lost: the caster reads
    /// "Something went wrong." and nothing else, shows no cast pose and keeps all 200 mana; the target is told
    /// nothing and keeps everything. RTK's <c>dispell.lua</c> and its three twins return on a lost roll before the
    /// pose and the debit. Each attempt is a fresh caster, so every attempt is one frame from a full action budget;
    /// a won attempt (18%) is given its effects back and cast again.
    /// <para>The two refusals before the roll are unchanged and free: too little mana ("You do not have enough
    /// mana.") and nobody to aim at (silent). Green before and after.</para>
    /// <para>Red on 93b079c: the lost cast reads "Something went wrong." and then "You cast X.", shows the cast
    /// pose, and costs 200 (the verb spent before it rolled).</para></summary>
    [Theory]
    [MemberData(nameof(DispellFamily))]
    public void ADispellFamilyCastThatLosesItsRollSaysOneLineAndCostsNothing(string key)
    {
        var sp = Content.SpellByKey(key)!;
        var (v, vOut, _) = Plain("DispellRuleRollV", RollMap, 50, 50, c => { c.Will = 255; c.Ac = -60; });
        var casters = new List<Session>();
        try
        {
            var (poor, poorOut, poorc) = Poet(null, RollMap, 54, 50, will: 255, sp);
            casters.Add(poor);
            poorc.Mp = Cost - 1;
            poor.Receive(SpellCastSupport.CastFrame(0, v.PlayerId));
            Assert.Equal(Cost - 1, poorc.Mp);
            Assert.Equal(new[] { "You do not have enough mana." }, SpellCastSupport.MiniTexts(poorOut));

            var (aimless, aimlessOut, aimlessc) = Poet(null, RollMap, 56, 50, will: 255, sp);
            casters.Add(aimless);
            // An id nobody online has, and nobody on the faced tile: nothing resolves.
            aimless.Receive(SpellCastSupport.CastFrame(0, 0x7FFF_FFF0));
            Assert.Equal(Mana, aimlessc.Mp);
            Assert.Empty(SpellCastSupport.MiniTexts(aimlessOut));

            GiveEffects(v);
            GiveHolds(v);
            vOut.Clear();
            bool lost = false;
            for (int attempt = 1; attempt <= 60 && !lost; attempt++)
            {
                var (d, dOut, dc) = Poet(null, RollMap, (ushort)(10 + attempt), 52, will: 0, sp);
                casters.Add(d);
                d.Receive(SpellCastSupport.CastFrame(0, v.PlayerId));
                lost = SpellCastSupport.BuffEntry(v, BuffKey) is not null;
                if (!lost)
                {
                    GiveEffects(v);
                    GiveHolds(v);
                    vOut.Clear();
                    continue;
                }
                Assert.Equal(new[] { "Something went wrong." }, SpellCastSupport.MiniTexts(dOut));
                Assert.Empty(dOut.BodiesOf(ServerOp.Action));
                Assert.Equal(Mana, dc.Mp);
                Assert.Empty(SpellCastSupport.MiniTexts(vOut));
                AssertEffectsKept(v);
                AssertHoldsKept(v);
            }
            Assert.True(lost, "60 casts at 18% never lost a roll");
        }
        finally
        {
            _fx.World.LeaveMap(v, RollMap);
            foreach (var s in casters) _fx.World.LeaveMap(s, RollMap);
        }
    }

    // ===== 4. on yourself, the same rules =============================================================

    /// <summary>Each of the four, cast on yourself through the real frame with your own id and lost: you read
    /// "Something went wrong." and nothing else, show no cast pose and keep all 200 mana, and your buff, fury and
    /// venom stay. Caleb, 2026-10-11: "Same rules on self". RTK's <c>dispell.lua</c> and its three twins make no
    /// difference between yourself and another player before the roll, which returns ahead of the pose and the
    /// debit (<c>poet/dispell.lua:37-40</c>). Each attempt is a fresh caster with AC -60, so the rate on yourself is
    /// 30% and every attempt is one frame from a full action budget; a won attempt is put aside for the next.
    /// <para>Red on db2a48d: the lost cast reads "Something went wrong." and then "You cast X.", shows the cast
    /// pose, and costs 200 (on yourself the verb still spent before it rolled).</para></summary>
    [Theory]
    [MemberData(nameof(DispellFamily))]
    public void ADispellFamilyCastOnYourselfThatLosesItsRollSaysOneLineAndCostsNothing(string key)
    {
        var sp = Content.SpellByKey(key)!;
        var casters = new List<Session>();
        try
        {
            bool lost = false;
            for (int attempt = 1; attempt <= 60 && !lost; attempt++)
            {
                var (p, pOut, pc) = Poet(null, SelfLostMap, (ushort)(10 + attempt), 20, will: 255, sp);
                casters.Add(p);
                pc.Ac = -60;
                GiveOwnEffects(p);
                GiveVenom(p);
                pOut.Clear();

                p.Receive(SpellCastSupport.CastFrame(0, p.PlayerId));

                lost = SpellCastSupport.BuffEntry(p, BuffKey) is not null;
                if (!lost) continue;
                Assert.Equal(new[] { "Something went wrong." }, SpellCastSupport.MiniTexts(pOut));
                Assert.Empty(pOut.BodiesOf(ServerOp.Action));
                Assert.Equal(Mana, pc.Mp);
                Assert.Equal(3, SpellCastSupport.Rage(p).Amount);
                Assert.True(p.Poisoned, "a lost Dispell ended your venom");
                Assert.NotNull(SpellCastSupport.BuffEntry(p, VenomKey));
            }
            Assert.True(lost, "60 casts on yourself at 30% never lost a roll");
        }
        finally
        {
            foreach (var s in casters) _fx.World.LeaveMap(s, SelfLostMap);
        }
    }

    /// <summary>Each of the four, cast on yourself through the real frame with your own id and won, ends your venom
    /// and the harder next hit a Doze left armed, as well as your buff and fury, the same as at another player.
    /// Caleb, 2026-10-11: "Same rules on self". You read "The poison passes." and "You cast X.", nothing that ends
    /// "on you.", and pay 200.
    /// <para>The Doze is the realistic one a caster can still cast under: a venom tick has woken them ("The pain
    /// wakes you.", <c>TickPoison</c>), which ends the sleep and leaves the multiplier the Doze armed for its
    /// length. A caster still asleep cannot cast through the frame at all; the next fact covers that. Against
    /// yourself the rate is at most 95%, so each attempt is a fresh caster and a lost one is put aside.</para>
    /// <para>Red on db2a48d: the buff and fury go, but you are still poisoned and your next hit is still
    /// 1.3x.</para></summary>
    [Theory]
    [MemberData(nameof(DispellFamily))]
    public void AWonDispellFamilyCastOnYourselfEndsYourVenomAndYourDozesHarderHit(string key)
    {
        var sp = Content.SpellByKey(key)!;
        var casters = new List<Session>();
        try
        {
            bool won = false;
            for (int attempt = 1; attempt <= 20 && !won; attempt++)
            {
                var (p, pOut, pc) = Poet(null, SelfWonMap, (ushort)(10 + attempt), 20, will: 255, sp);
                casters.Add(p);
                GiveOwnEffects(p);
                GiveHolds(p);
                WakeByVenomTick(p);
                pOut.Clear();

                p.Receive(SpellCastSupport.CastFrame(0, p.PlayerId));

                won = SpellCastSupport.BuffEntry(p, BuffKey) is null;
                if (!won) continue;
                var lines = SpellCastSupport.MiniTexts(pOut);
                Assert.False(p.Poisoned, "a won Dispell on yourself left your venom");
                Assert.Null(SpellCastSupport.BuffEntry(p, VenomKey));
                Assert.Equal(1.0, DamageAmp(p));
                Assert.Equal((1, 0L), SpellCastSupport.Rage(p));
                Assert.Equal(Mana - Cost, pc.Mp);
                Assert.Equal(new[] { "The poison passes.", $"You cast {sp.Name}." }, lines);
            }
            Assert.True(won, "20 casts on yourself at 95% never won a roll");
        }
        finally
        {
            foreach (var s in casters) _fx.World.LeaveMap(s, SelfWonMap);
        }
    }

    /// <summary>Each of the four, won on yourself while you are asleep, wakes you ("You wake up.") and ends your
    /// venom and the Doze's harder next hit: the cast HandleCast would make, through <c>ApplyCast</c> under your own
    /// monitor. The real frame never gets that far for a sleeper, and the fact checks that first: HandleCast answers
    /// "You are asleep." before it reads the slot, for every spell, at no cost. So the wake is the verb's rule
    /// (<c>flushTarget(true)</c> on yourself), pinned below the gate.
    /// <para>Red on db2a48d: the buff entries go, but you are still asleep, still under the harder next hit and
    /// still poisoned.</para></summary>
    [Theory]
    [MemberData(nameof(DispellFamily))]
    public void AWonDispellFamilyCastOnYourselfWakesYou(string key)
    {
        var sp = Content.SpellByKey(key)!;
        var casters = new List<Session>();
        try
        {
            bool won = false;
            for (int attempt = 1; attempt <= 20 && !won; attempt++)
            {
                var (p, pOut, pc) = Poet(null, SelfAsleepMap, (ushort)(10 + attempt), 20, will: 255, sp);
                casters.Add(p);
                GiveOwnEffects(p);
                GiveHolds(p);
                pOut.Clear();

                p.Receive(SpellCastSupport.CastFrame(0, p.PlayerId));
                Assert.Equal(new[] { "You are asleep." }, SpellCastSupport.MiniTexts(pOut));
                Assert.Equal(Mana, pc.Mp);
                Assert.NotNull(SpellCastSupport.BuffEntry(p, BuffKey));
                AssertHoldsKept(p);
                pOut.Clear();

                bool ok = false;
                p.WithState(() => ok = (bool)ApplyCast.Invoke(p, new object?[] { sp, p.PlayerId, null })!);

                won = SpellCastSupport.BuffEntry(p, BuffKey) is null;
                if (!won) continue;
                Assert.True(ok, "a won Dispell on yourself was refused");
                AssertHoldsEnded(p);
                Assert.Equal((1, 0L), SpellCastSupport.Rage(p));
                Assert.Equal(Mana - Cost, pc.Mp);
                Assert.Equal(new[] { "You wake up.", "The poison passes." }, SpellCastSupport.MiniTexts(pOut));
            }
            Assert.True(won, "20 casts on yourself at 95% never won a roll");
        }
        finally
        {
            foreach (var s in casters) _fx.World.LeaveMap(s, SelfAsleepMap);
        }
    }

    // ===== helpers ====================================================================================

    /// <summary>A caster's own effects before a Dispell on themselves: a stat buff and a fury, each with 10 minutes
    /// to run. Not the hold <see cref="GiveEffects"/> adds, since a held player cannot cast at all.</summary>
    private static void GiveOwnEffects(Session p)
    {
        p.ReceiveCurse("might", 5, TenMinutes, BuffKey, "Probe Might", "");
        p.WithState(() =>
        {
            RageAmountField.SetValue(p, 3);
            RageUntilField.SetValue(p, Environment.TickCount64 + TenMinutes);
        });
    }

    /// <summary>A venom alone, with 10 minutes to run, through the writer the casts use.</summary>
    private static void GiveVenom(Session p)
    {
        p.ReceivePoison(dps: 1, durMs: TenMinutes, by: 0, anim: 0, key: VenomKey, name: "Probe Venom");
        Assert.True(p.Poisoned, "the venom did not take");
    }

    /// <summary>Bring the venom's next tick forward and run it: <c>TickPoison</c>, the world heartbeat's call, takes
    /// health and wakes a sleeper as damage does ("The pain wakes you."). The Doze's harder next hit stays armed,
    /// and the venom keeps running.</summary>
    private static void WakeByVenomTick(Session p)
    {
        p.WithState(() => PoisonNextTickField.SetValue(p, 0L));
        p.TickPoison();
        Assert.False(p.Asleep, "the venom tick did not wake the caster");
        Assert.True(p.Poisoned, "the venom ended on its tick");
        Assert.Equal(DozeAmp, DamageAmp(p));
    }

    /// <summary>The target's effects before a Dispell, as in <c>DispellTargetLockTests</c>: a stat buff, the hold
    /// (a categorised status in the <c>paras</c> slot) and a fury, each with 10 minutes to run.</summary>
    private static void GiveEffects(Session v)
    {
        v.ReceiveCurse("might", 5, TenMinutes, BuffKey, "Probe Might", "");
        v.ReceiveCurse("", 0, TenMinutes, HoldKey, "Probe Hold", Session.ParalysisSlot);
        v.WithState(() =>
        {
            RageAmountField.SetValue(v, 3);
            RageUntilField.SetValue(v, Environment.TickCount64 + TenMinutes);
        });
    }

    /// <summary>A Doze and a venom, each with 10 minutes to run, through the writers the casts use: the Doze's sleep
    /// (<c>ReceiveSleep</c>, the <c>sleeps</c> slot) and the harder next hit it arms (<c>ArmDamageAmp</c>), and the
    /// venom's poison (<c>ReceivePoison</c>, the <c>venoms</c> slot).</summary>
    private static void GiveHolds(Session v)
    {
        v.ReceiveSleep("sleeps", TenMinutes, DozeKey, "Probe Doze", anim: 0, repeatFxMs: 0);
        v.ArmDamageAmp(DozeAmp, TenMinutes);
        v.ReceivePoison(dps: 1, durMs: TenMinutes, by: 0, anim: 0, key: VenomKey, name: "Probe Venom");
        Assert.True(v.Asleep && v.Poisoned, "the Doze or the venom did not take");
    }

    private static void AssertEffectsKept(Session v)
    {
        Assert.NotNull(SpellCastSupport.BuffEntry(v, BuffKey));
        Assert.NotNull(SpellCastSupport.BuffEntry(v, HoldKey));
        Assert.True(v.Paralyzed);
        Assert.Equal(3, SpellCastSupport.Rage(v).Amount);
    }

    private static void AssertEffectsCleared(Session v)
    {
        Assert.Null(SpellCastSupport.BuffEntry(v, BuffKey));
        Assert.Null(SpellCastSupport.BuffEntry(v, HoldKey));
        Assert.False(v.Paralyzed);
        Assert.Equal((1, 0L), SpellCastSupport.Rage(v));
    }

    private static void AssertHoldsKept(Session v)
    {
        Assert.True(v.Asleep, "the target woke");
        Assert.NotNull(SpellCastSupport.BuffEntry(v, DozeKey));
        Assert.Equal(DozeAmp, DamageAmp(v));
        Assert.True(v.Poisoned, "the target's venom ended");
        Assert.NotNull(SpellCastSupport.BuffEntry(v, VenomKey));
    }

    private static void AssertHoldsEnded(Session v)
    {
        Assert.False(v.Asleep, "the target is still asleep");
        Assert.Null(SpellCastSupport.BuffEntry(v, DozeKey));
        Assert.Equal(1.0, DamageAmp(v));
        Assert.False(v.Poisoned, "the target is still poisoned");
        Assert.Null(SpellCastSupport.BuffEntry(v, VenomKey));
    }

    /// <summary>The multiplier the next hit on <paramref name="v"/> would take, read as <c>TakeDamageAmp</c> reads
    /// it but without spending it: 1 when none is armed or it has run out.</summary>
    private static double DamageAmp(Session v)
    {
        double amp = 1.0;
        v.WithState(() =>
        {
            double armed = (double)DamageAmpField.GetValue(v)!;
            long until = (long)DamageAmpUntilField.GetValue(v)!;
            if (armed > 1.0 && until > Environment.TickCount64) amp = armed;
        });
        return amp;
    }

    /// <summary>Wait until <paramref name="t"/> has finished, or has sat blocked for 100 ms straight (a cast
    /// waiting for a monitor), or 10 s have passed.</summary>
    private static void WaitUntilDoneOrBlocked(Thread t)
    {
        var clock = Stopwatch.StartNew();
        long blockedSince = -1;
        while (clock.ElapsedMilliseconds < 10_000 && !t.Join(5))
        {
            if ((t.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0) blockedSince = -1;
            else if (blockedSince < 0) blockedSince = clock.ElapsedMilliseconds;
            else if (clock.ElapsedMilliseconds - blockedSince >= 100) return;
        }
    }

    /// <summary>A level-99 caster with <paramref name="book"/> in slots 0.., on a 100x100 walkable map. Named
    /// <paramref name="name"/> exactly when one is given (the GM), else a fresh name.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Poet(string? name, ushort map, ushort x, ushort y,
                                                                          byte will, params SpellDef[] book) =>
        _fx.PlayerWith(name ?? $"DispellRuler{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 99;
            ch.MaxHp = 1_000; ch.Hp = 1_000;
            ch.MaxMp = Mana; ch.Mp = Mana;
            ch.Will = will;
            Wide(ch, map);
            foreach (var sp in book) ch.Spells.Add(sp.Id);
        }, map, x, y);

    /// <summary>A player with no spells, on a 100x100 walkable map. Named <see cref="GmName"/> exactly when that is
    /// the name asked for.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Plain(string name, ushort map, ushort x, ushort y,
                                                                           Action<Character>? shape = null) =>
        _fx.PlayerWith(name == GmName ? name : $"{name}{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 50; ch.MaxHp = 1_000; ch.Hp = 1_000;
            Wide(ch, map);
            shape?.Invoke(ch);
        }, map, x, y);

    /// <summary>Content-free maps have no size of their own; the cast handler needs one.</summary>
    private static void Wide(Character c, ushort map)
    {
        if (Content.Maps.TryGetValue(map, out var m)) { c.MapXs = m.Xs; c.MapYs = m.Ys; }
        else { c.MapXs = 100; c.MapYs = 100; }
    }
}
