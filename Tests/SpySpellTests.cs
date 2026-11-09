using System.Diagnostics;
using System.Reflection;
using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The Spy spells slice (the project owner, 2026-10-26, P5 "each spell as its RTK script", P1 and P3 "on sale once it
/// works, at its script's price"; 2026-10-30, Q3 "Spells only; chain later" and Q4, Dagger to throat on any player on
/// any map with no PK check; 2026-11-09, Spy kit's traps: "Tick its full time" and "Line and slot only"; the smaller calls
/// as RTK under P5). Before, every Spy row but Assassinate fell to the <c>generic</c> verb, 5 mana and "You cast X." and
/// nothing else (<c>Tests/Fixtures/spell-dispatch-routes.txt</c> had them on <c>verb=generic</c>).
///
/// <para>The scripts: <c>RTK-Server/rtklua/Accepted/Spells/Subpaths/Spy/</c> and Spy kit's three traps,
/// <c>NPCs/trap/rogue_traps/Spy/</c>. Each spell is bound by its <c>game-data/SpellParams.csv</c> row: Skill up to the
/// climbing fury on its own ladder, Incognito to Blend's <c>hide</c>, Hear footstep and Assess opponent to Do's verbs,
/// Mimic to Muse's <c>disguise</c>, Spy kit and Dagger to throat to the Spy region of <c>game-data/spell_verbs.lua</c>,
/// Apply stealth to <c>not_castable</c>; the traps' effects are <c>World</c>'s (the Spy kit block) and
/// <c>Session.SpringSpyTrap</c>.</para>
///
/// <para>Every cast is the real 0x0F frame through <c>Session.Receive</c>, by a fresh level-99 Spy facing east, on
/// content-free maps 63500-63521 (no Maps.csv row: no terrain, PvP off) and on Pyongon-Baram Arena (3033), a PvP map no
/// other class's facts use. A player springs a trap by a real walk frame. A menu is answered from a thread of its own with
/// no synchronisation context, as a read loop answers it.</para>
/// </summary>
[Collection("world")]
public sealed class SpySpellTests
{
    private const ushort RageMap = 63500, IncognitoMap = 63501, HearMap = 63502, AssessMap = 63503, MimicMap = 63504,
                         KitMap = 63505, KitRefuseMap = 63506, CreatureTrapMap = 63507, OffPvpMap = 63508,
                         ThroatMap = 63509, ThroatHeldMap = 63510, StealthMap = 63511, RevealMap = 63512, Arena = 3033;
    private const ushort CX = 30, CY = 30;
    private const uint MaxMana = 400_000, Mana = 400_000, Hp = 10_000;
    private const byte Will = 41;

    /// <summary>Slack on a wait, a run or a status read straight after the cast.</summary>
    private const long SlackMs = 5_000;

    private static readonly FieldInfo NextActionTick =
        typeof(Session).GetField("_nextActionTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ActionCount =
        typeof(Session).GetField("_actionCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo RageUntilField =
        typeof(Session).GetField("_rageUntil", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo StealthUntilField =
        typeof(Session).GetField("_stealthUntil", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo StealthTimesField =
        typeof(Session).GetField("_stealthTimes", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo TicksField =
        typeof(Session).GetField("_spellTicks", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo PoisonNextTickField =
        typeof(Session).GetField("_poisonNextTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo PoisonUntilField =
        typeof(Session).GetField("_poisonUntil", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo PoisonPerTickField =
        typeof(Session).GetField("_poisonPerTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo PoisonByField =
        typeof(Session).GetField("_poisonBy", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo RevealMethod =
        typeof(Session).GetMethod("RevealableTrapsNear", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly SessionFixture _fx;
    private static int _serial;

    public SpySpellTests(SessionFixture fx) => _fx = fx;

    // ===== Skill up =============================================================================================

    /// <summary>Six casts climb RTK's ladder: rage 6, 7, 9, 10, 12, 14 for 3,000, 4,711, 17,960, 53,646, 135,327 and
    /// 269,420 mana, each the script's line after "[Skill up N] ", no AC at any tier, the 150 s wait each time, and one 938 s
    /// run that the first cast arms. A seventh says "You have reached your max potential." and spends nothing. Red on
    /// 461e2d9: generic's 5 mana and "You cast Skill up.".</summary>
    [Fact]
    public void SkillUpClimbsItsSixTiersAndHoldsAtTheTop()
    {
        var (caster, cOut, cc) = Caster("skill_up_spy", RageMap, CX, CY);
        int[] mults = { 6, 7, 9, 10, 12, 14 }, mana = { 3_000, 4_711, 17_960, 53_646, 135_327, 269_420 };
        string[] said =
        {
            "[Skill up 1] Your senses sharpen.", "[Skill up 2] You hone your skills.",
            "[Skill up 3] Agility becomes your strongest suit..", "[Skill up 4] Acquiring cat-like reflex.",
            "[Skill up 5] Incredible skill!", "[Skill up 6] Sagat would be jealous.",
        };
        try
        {
            caster.WithState(() => cc.Mp = 600_000);
            cc.MaxMp = 600_000;
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
                long wait = SpellCastSupport.AetherLeft(caster, "skill_up_spy");
                if (tier == 1) firstEnd = Environment.TickCount64 + left;
                long end = Environment.TickCount64 + left;
                Assert.True(lines.SequenceEqual(new[] { said[tier - 1] }) && amount == mults[tier - 1]
                            && before - cc.Mp == mana[tier - 1] && caster.ItemArmor == baseArmor
                            && wait > 150_000 - SlackMs && wait <= 150_000
                            && left > 938_000 - SlackMs * 2 && Math.Abs(end - firstEnd) < SlackMs,
                            $"tier {tier}: [{string.Join(" | ", lines)}], rage {amount}, spent {before - cc.Mp}, armor " +
                            $"{caster.ItemArmor - baseArmor}, wait {wait} ms, run left {left} ms");
                SpellCastSupport.ClearAether(caster, "skill_up_spy");
            }
            uint top = cc.Mp;
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(cOut) == "You have reached your max potential." && cc.Mp == top
                        && SpellCastSupport.Rage(caster).Amount == 14 && SpellCastSupport.AetherLeft(caster, "skill_up_spy") == 0,
                        $"at the top: [{Lines(cOut)}], spent {top - cc.Mp}, rage {SpellCastSupport.Rage(caster).Amount}");
        }
        finally { Leave(RageMap, caster); }
    }

    /// <summary>Inside its 150 s wait, "Skill up isn't ready yet."; short of tier 1's 3,000, "You do not have enough
    /// mana.". Both free. Red on 461e2d9: generic's 5 mana and "You cast Skill up.".</summary>
    [Fact]
    public void SkillUpRefusesInsideItsWaitAndShortOfMana()
    {
        var (caster, cOut, cc) = Caster("skill_up_spy", RageMap, CX, CY + 2);
        var (poor, pOut, pc) = Caster("skill_up_spy", RageMap, CX, CY + 4, mana: 2_999);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            uint after = cc.Mp;
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            poor.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(cOut) == "Skill up isn't ready yet." && cc.Mp == after && SpellCastSupport.Rage(caster).Amount == 6
                        && Lines(pOut) == "You do not have enough mana." && pc.Mp == 2_999 && SpellCastSupport.Rage(poor).Amount == 1,
                        $"again [{Lines(cOut)}] spent {after - cc.Mp}; poor [{Lines(pOut)}] mana {pc.Mp}");
        }
        finally { Leave(RageMap, caster, poor); }
    }

    /// <summary>A lapsed run, at any tier, keeps all its vita and says nothing: Skill up's uncast only sets rage 1. Red
    /// without Skill up's entry in <c>Session.RageLadderVitaKept</c>: Chung Ryong's price ("Chung Ryong's rage leaves you
    /// drained." and a fifth of the vita at tier 1, 1 vita and 1 mana at tier 6).</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void ALapsedSkillUpKeepsItsVita(int tier)
    {
        var (caster, cOut, cc) = Caster("skill_up_spy", RageMap, CX + 6, CY + 6 * tier, maxMana: 600_000, mana: 600_000);
        try
        {
            for (int i = 0; i < tier; i++)
            {
                FreeSlot(caster);
                caster.Receive(SpellCastSupport.CastFrame(0));
                SpellCastSupport.ClearAether(caster, "skill_up_spy");
            }
            uint mana = cc.Mp;
            cOut.Clear();
            caster.WithState(() => RageUntilField.SetValue(caster, Environment.TickCount64 - 1));
            caster.RegenTick(0, regenDue: false);
            Assert.True(cc.Hp == Hp && cc.Mp == mana && SpellCastSupport.MiniTexts(cOut).Count == 0 && caster.LuaCrRageTier == 0,
                        $"tier {tier}: vita {cc.Hp}, mana {mana} -> {cc.Mp}, lines [{Lines(cOut)}], tier left {caster.LuaCrRageTier}");
        }
        finally { Leave(RageMap, caster); }
    }

    // ===== Incognito, Hear footstep, Assess opponent, Mimic: the verbs that exist, on Spy's rows ==================

    /// <summary>Incognito is Vanish's script under its own key: invisible for 1,000 s with the next landed swing x9, no mana,
    /// no wait, no graphic, the engine's "You cast Incognito."; in a creature's form, "You can't do that while
    /// transformed.", free. Red on 461e2d9: generic's 5 mana and no stealth.</summary>
    [Fact]
    public void IncognitoHidesItsCasterForAThousandSeconds()
    {
        var (caster, cOut, cc) = Caster("incognito", IncognitoMap, CX, CY);
        var (morphed, mOut, _) = Caster("incognito", IncognitoMap, CX, CY + 4);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            long hidden = StealthLeft(caster), wait = SpellCastSupport.AetherLeft(caster, "incognito");
            var drawn = Effects(cOut, caster.PlayerId);
            Morph(morphed);
            mOut.Clear();
            morphed.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(caster.IsInvisible && hidden > 1_000_000 - SlackMs && hidden <= 1_000_000 && cc.Mp == Mana && wait == 0
                        && Lines(cOut) == "You cast Incognito." && drawn.Count == 0 && StealthTimes(caster) == 9
                        && Lines(mOut) == "You can't do that while transformed." && !morphed.IsInvisible,
                        $"invisible {caster.IsInvisible} for {hidden} ms, spent {Mana - cc.Mp}, wait {wait} ms, lines [{Lines(cOut)}], " +
                        $"graphics [{string.Join(",", drawn)}], x{StealthTimes(caster)}; morphed [{Lines(mOut)}]");
        }
        finally { Leave(IncognitoMap, caster, morphed); }
    }

    /// <summary>Hear footstep is Do's: a 32 s run on its caster whose beat comes every 5 s, 0 mana, its 60 s wait under
    /// Spy's own key, "You cast Hear footstep." and graphic 11. Red on 461e2d9: generic's 5 mana and no run.</summary>
    [Fact]
    public void HearFootstepIsDosOnSpysRow()
    {
        var (caster, cOut, cc) = Caster("hear_footstep_spy", HearMap, CX, CY);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            var ticks = Ticks(caster);
            long wait = SpellCastSupport.AetherLeft(caster, "hear_footstep_spy");
            var drawn = Effects(cOut, caster.PlayerId);
            Assert.True(ticks.Count == 1 && ticks[0] == ("hear_footstep_spy", "hear_footstep_tick", 5_000) && cc.Mp == Mana
                        && wait > 60_000 - SlackMs && wait <= 60_000 && SpellCastSupport.AetherLeft(caster, "hear_footstep_do") == 0
                        && Lines(cOut) == "You cast Hear footstep." && drawn.SequenceEqual(new[] { 11 }),
                        $"ticks [{string.Join(", ", ticks)}], spent {Mana - cc.Mp}, wait {wait} ms, lines [{Lines(cOut)}], " +
                        $"graphics [{string.Join(",", drawn)}]");
        }
        finally { Leave(HearMap, caster); }
    }

    /// <summary>Assess opponent is Do's: at a creature "[&lt;name&gt;] VIT: &lt;max vita&gt;" and nothing else, its 25 s wait
    /// under Spy's own key, no mana spent (the script checks its 550 and never takes it); short of 550, "Your will is too
    /// weak.", free. Red on 461e2d9: generic's 5 mana and "You cast Assess opponent.".</summary>
    [Fact]
    public void AssessOpponentIsDosOnSpysRow()
    {
        var mob = AddCreature(AssessMap, CX + 1, CY, hp: 1_234, maxHp: 98_765);
        var (caster, cOut, cc) = Caster("assess_opponent_spy", AssessMap, CX, CY);
        var other = AddCreature(AssessMap, CX + 1, CY + 4);
        var (weak, wOut, wc) = Caster("assess_opponent_spy", AssessMap, CX, CY + 4, mana: 549);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            long wait = SpellCastSupport.AetherLeft(caster, "assess_opponent_spy");
            weak.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(cOut) == $"[{mob.Name}] VIT: 98765" && cc.Mp == Mana && wait > 25_000 - SlackMs && wait <= 25_000
                        && Lines(wOut) == "Your will is too weak." && wc.Mp == 549,
                        $"lines [{Lines(cOut)}], spent {Mana - cc.Mp}, wait {wait} ms; weak [{Lines(wOut)}]");
        }
        finally
        {
            Leave(AssessMap, caster, weak);
            _fx.World.DespawnMob(AssessMap, mob);
            _fx.World.DespawnMob(AssessMap, other);
        }
    }

    /// <summary>Mimic is Costume's script and numbers under its own key: at a creature the caster takes its form for 150 s,
    /// pays 1,600, has graphic 16 over them and waits 225 s; short of 1,600, "Your will is too weak.", free. Red on 461e2d9:
    /// generic's 5 mana and no form.</summary>
    [Fact]
    public void MimicIsCostumesOnSpysRow()
    {
        var mob = AddCreature(MimicMap, CX + 1, CY);
        var (caster, cOut, cc) = Caster("mimic_spy", MimicMap, CX, CY);
        var (weak, wOut, wc) = Caster("mimic_spy", MimicMap, CX, CY + 4, mana: 1_599);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            long wait = SpellCastSupport.AetherLeft(caster, "mimic_spy");
            weak.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(caster.IsMorphed && cc.Mp == Mana - 1_600 && Lines(cOut) == "You cast Mimic."
                        && Effects(cOut, caster.PlayerId).SequenceEqual(new[] { 16 }) && wait > 225_000 - SlackMs && wait <= 225_000
                        && Lines(wOut) == "Your will is too weak." && wc.Mp == 1_599 && !weak.IsMorphed,
                        $"morphed {caster.IsMorphed}, spent {Mana - cc.Mp}, lines [{Lines(cOut)}], graphics " +
                        $"[{string.Join(",", Effects(cOut, caster.PlayerId))}], wait {wait} ms; weak [{Lines(wOut)}]");
        }
        finally { Leave(MimicMap, caster, weak); _fx.World.DespawnMob(MimicMap, mob); }
    }

    /// <summary>Apply stealth's script has an empty cast, so RTK casts nothing: no line, no mana, no wait, no pose. Both of
    /// its rows (one identifier). Red on 461e2d9: generic's 5 mana and "You cast Apply stealth.".</summary>
    [Fact]
    public void ApplyStealthCastsNothing()
    {
        var rows = Content.Spells.Where(s => s.Key == "apply_stealth").Select(s => s.Id).OrderBy(i => i).ToList();
        Assert.Equal(new[] { 30807, 30810 }, rows.Select(i => (int)i).ToArray());
        var (caster, cOut, cc) = Caster("apply_stealth", StealthMap, CX, CY);
        try
        {
            caster.WithState(() => cc.Spells.Add(rows[1]));
            caster.Receive(SpellCastSupport.CastFrame(0));
            caster.Receive(SpellCastSupport.CastFrame(1));
            Assert.True(SpellCastSupport.MiniTexts(cOut).Count == 0 && cc.Mp == Mana
                        && SpellCastSupport.AetherLeft(caster, "apply_stealth") == 0 && caster.ActionSlotLeft == 0,
                        $"lines [{Lines(cOut)}], spent {Mana - cc.Mp}, wait {SpellCastSupport.AetherLeft(caster, "apply_stealth")}, " +
                        $"slot {caster.ActionSlotLeft}");
        }
        finally { Leave(StealthMap, caster); }
    }

    // ===== Spy kit ==============================================================================================

    /// <summary>The cast: "You cast Spy Kit.", the 10 s wait and RTK's menu, and no mana (the script checks 1 and never takes
    /// it). The pick sets that trap on the caster's tile, owned by the caster, gone in 20 s, and says the script's line. Red
    /// on 461e2d9: generic's 5 mana, "You cast Spy kit." and no menu.</summary>
    [Theory]
    [InlineData(1, "toxic_spray", "You make a toxic spray.")]
    [InlineData(2, "tripwire", "The tripwire has been set.")]
    [InlineData(3, "smoke_screen", "The smoke screen is set.")]
    public void SpyKitSetsThePickedTrapOnTheCastersTile(byte pick, string kind, string line)
    {
        int y = CY + 4 * pick;
        var (caster, cOut, cc) = Caster("spy_kit", KitMap, CX, y);
        try
        {
            string cast = "", menu = "", picked = "";
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                cast = Lines(cOut);
                menu = string.Join(" / ", Menus(cOut));
                cOut.Clear();
                caster.Receive(MenuFrame(pick));
                picked = Lines(cOut);
            });
            long wait = SpellCastSupport.AetherLeft(caster, "spy_kit");
            var trap = _fx.World.TrapsNear(KitMap, CX, y, 0).SingleOrDefault();
            long life = trap is null ? 0 : trap.ExpiresAt - Environment.TickCount64;
            Assert.True(cast == "You cast Spy Kit." && menu == "Which trap would you like to use?|Toxic spray|Tripwire|Smoke screen"
                        && picked == line && cc.Mp == Mana && wait > 10_000 - SlackMs && wait <= 10_000
                        && trap is not null && trap.Kind == kind && trap.OwnerId == caster.PlayerId
                        && life > 20_000 - SlackMs && life <= 20_000,
                        $"cast [{cast}], menu [{menu}], pick [{picked}], spent {Mana - cc.Mp}, wait {wait} ms, trap " +
                        $"{trap?.Kind ?? "none"} owner {trap?.OwnerId} life {life} ms");
        }
        finally { ClearTraps(KitMap); Leave(KitMap, caster); }
    }

    /// <summary>Short of the 1 mana, "Your will is too weak.", free; inside the wait, "Spy kit isn't ready yet."; a menu
    /// closed without a pick sets nothing, but its wait is armed (it came before the menu). Red on 461e2d9 (generic).</summary>
    [Fact]
    public void SpyKitRefusalsAndAClosedMenu()
    {
        var (poor, pOut, _) = Caster("spy_kit", KitRefuseMap, CX, CY, mana: 0);
        var (caster, cOut, _) = Caster("spy_kit", KitRefuseMap, CX, CY + 4);
        try
        {
            poor.Receive(SpellCastSupport.CastFrame(0));
            OnOwnThread(() => caster.Receive(SpellCastSupport.CastFrame(0)));
            long wait = SpellCastSupport.AetherLeft(caster, "spy_kit");
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(pOut) == "Your will is too weak." && Menus(pOut).Count == 0 && SpellCastSupport.AetherLeft(poor, "spy_kit") == 0
                        && wait > 10_000 - SlackMs && Lines(cOut) == "Spy kit isn't ready yet."
                        && _fx.World.TrapsNear(KitRefuseMap, CX, CY, 10).Length == 0,
                        $"poor [{Lines(pOut)}]; wait {wait} ms; again [{Lines(cOut)}]; traps {_fx.World.TrapsNear(KitRefuseMap, CX, CY, 10).Length}");
        }
        finally { Leave(KitRefuseMap, poor, caster); }
    }

    /// <summary>Spot traps and Watchful Eye show none of the three: RTK's rogue list names only the rogue's eight. Red with
    /// the three not left out of <c>Session.RevealableTrapsNear</c>.</summary>
    [Fact]
    public void SpotTrapsShowsNoSpyTrap()
    {
        var (rogue, _, _) = _fx.PlayerWith(Letters("Rogue"), ch => { ch.ClassName = "Rogue"; ch.Level = 99; Size(ch, RevealMap); },
                                           RevealMap, CX, CY);
        try
        {
            foreach (var (kind, i) in new[] { "toxic_spray", "tripwire", "smoke_screen" }.Select((k, i) => (k, i)))
                _fx.World.PlaceTrap(RevealMap, (ushort)(CX + 1 + i), CY, kind, 1, Environment.TickCount64 + 20_000);
            _fx.World.PlaceTrap(RevealMap, CX, CY + 1, "dart", 1);
            Trap[] seen = Array.Empty<Trap>();
            rogue.WithState(() => seen = (Trap[])RevealMethod.Invoke(rogue, null)!);
            Assert.Equal("dart", string.Join(",", seen.Select(t => t.Kind)));
        }
        finally { ClearTraps(RevealMap); Leave(RevealMap, rogue); }
    }

    // ===== the traps on a creature ==============================================================================

    /// <summary>A creature that steps onto a Spy kit trap springs it, and the trap is gone. Toxic spray: the poison,
    /// ceil(1% of its vita) a 1.5 s beat (544 for 54,321, at most 1,000), credited to the trap's owner, graphic 1 each beat,
    /// for RTK's 185,000 at its creature timer (277.5 s). Tripwire: AC +30 in its curses slot and graphic 34 each beat for
    /// 37,000 (55.5 s). Smoke screen: blind for 25,000 (37.5 s). Red on 461e2d9: nothing springs (the kinds do not
    /// exist); red with the creature timer read as clock time.</summary>
    [Theory]
    [InlineData("toxic_spray")]
    [InlineData("tripwire")]
    [InlineData("smoke_screen")]
    public void ACreatureSpringsASpyTrap(string kind)
    {
        int y = kind switch { "toxic_spray" => CY, "tripwire" => CY + 4, _ => CY + 8 };
        var mob = AddCreature(CreatureTrapMap, CX, y, hp: 54_321, maxHp: 54_321);
        try
        {
            _fx.World.PlaceTrap(CreatureTrapMap, CX + 1, (ushort)y, kind, 77, Environment.TickCount64 + 20_000);
            Step(CreatureTrapMap, mob, CX + 1, y, 1);
            long now = Environment.TickCount64;
            bool gone = _fx.World.TrapsNear(CreatureTrapMap, CX + 1, y, 0).Length == 0;
            switch (kind)
            {
                case "toxic_spray":
                {
                    long left = mob.PoisonUntil - now;
                    Assert.True(gone && left > 277_500 - SlackMs && left <= 277_500 && mob.PoisonTickDam == 544 && mob.PoisonOwnerId == 77
                                && _fx.World.MobStatusKey(mob, "venoms") == "toxic_spray" && mob.FxRepeatAnim == 1 && mob.FxRepeatEvery == 1_500,
                                $"gone {gone}, poison {left} ms at {mob.PoisonTickDam} by {mob.PoisonOwnerId}, " +
                                $"slot {_fx.World.MobStatusKey(mob, "venoms")}, repeat {mob.FxRepeatAnim}/{mob.FxRepeatEvery}");
                    break;
                }
                case "tripwire":
                {
                    long left = mob.FxRepeatUntil - now;
                    Assert.True(gone && mob.Ac == 30 && _fx.World.MobStatusKey(mob, "curses") == "tripwire"
                                && left > 55_500 - SlackMs && left <= 55_500 && mob.FxRepeatAnim == 34 && mob.FxRepeatEvery == 1_500,
                                $"gone {gone}, AC {mob.Ac}, slot {_fx.World.MobStatusKey(mob, "curses")}, repeat " +
                                $"{mob.FxRepeatAnim}/{mob.FxRepeatEvery} for {left} ms");
                    break;
                }
                default:
                {
                    long left = mob.BlindUntil - now;
                    Assert.True(gone && left > 37_500 - SlackMs && left <= 37_500 && _fx.World.MobStatusKey(mob, "blinds") == "smoke_screen",
                                $"gone {gone}, blind {left} ms, slot {_fx.World.MobStatusKey(mob, "blinds")}");
                    break;
                }
            }
        }
        finally { ClearTraps(CreatureTrapMap); _fx.World.DespawnMob(CreatureTrapMap, mob); }
    }

    /// <summary>A trap whose effect is refused is still used up: a poisoned creature springs Toxic spray (its poison
    /// unchanged), a protected one Tripwire (no curse, AC unchanged: RTK's checkIfCast(protections)), one carrying only a
    /// minor curse Tripwire too (RTK's curses table lists the minor curses, Vex among them), a blinded one Smoke screen. Red
    /// with Tripwire's curse-or-protection check taken out of <c>World.SpringSpyTrapLocked</c> (the protected creature is
    /// cursed), and with its minor-curse test alone taken out (the Vexed one is).</summary>
    [Fact]
    public void ARefusedEffectStillUsesTheTrap()
    {
        var poisoned = AddCreature(CreatureTrapMap, CX + 10, CY, hp: 54_321, maxHp: 54_321);
        var guarded = AddCreature(CreatureTrapMap, CX + 10, CY + 4);
        var blinded = AddCreature(CreatureTrapMap, CX + 10, CY + 8);
        var vexed = AddCreature(CreatureTrapMap, CX + 10, CY + 12);
        try
        {
            Assert.True(_fx.World.PoisonMob(poisoned, 1000, 5_000, 5_000, 5, spellKey: "venom"));
            Assert.True(_fx.World.ApplyMobStatus(guarded, "protections", 60_000, false, false, spellKey: "curse_protection"));
            Assert.True(_fx.World.ApplyMobStatus(blinded, "blinds", 60_000, false, true, spellKey: "blind"));
            Assert.True(_fx.World.ApplyMobStatus(vexed, "minorcurses", 60_000, false, false, spellKey: "vex"));
            long poisonEnd = poisoned.PoisonUntil, blindEnd = blinded.BlindUntil;
            var pairs = new[] { (poisoned, "toxic_spray", CY), (guarded, "tripwire", CY + 4), (blinded, "smoke_screen", CY + 8),
                                (vexed, "tripwire", CY + 12) };
            foreach (var (mob, kind, y) in pairs)
            {
                _fx.World.PlaceTrap(CreatureTrapMap, CX + 11, (ushort)y, kind, 77, Environment.TickCount64 + 20_000);
                Step(CreatureTrapMap, mob, CX + 11, y, 1);
            }
            Assert.True(_fx.World.TrapsNear(CreatureTrapMap, CX + 11, CY + 6, 6).Length == 0
                        && poisoned.PoisonUntil == poisonEnd && poisoned.PoisonOwnerId == 5
                        && _fx.World.MobStatusKey(poisoned, "venoms") == "venom"
                        && guarded.Ac == 0 && _fx.World.MobStatusKey(guarded, "curses") == ""
                        && blinded.BlindUntil == blindEnd && _fx.World.MobStatusKey(blinded, "blinds") == "blind"
                        && vexed.Ac == 0 && _fx.World.MobStatusKey(vexed, "curses") == "",
                        $"traps left {_fx.World.TrapsNear(CreatureTrapMap, CX + 11, CY + 6, 6).Length}; poison owner " +
                        $"{poisoned.PoisonOwnerId}, AC {guarded.Ac} curse [{_fx.World.MobStatusKey(guarded, "curses")}], blind slot " +
                        $"{_fx.World.MobStatusKey(blinded, "blinds")}; Vexed AC {vexed.Ac} curse [{_fx.World.MobStatusKey(vexed, "curses")}]");
        }
        finally
        {
            ClearTraps(CreatureTrapMap);
            foreach (var m in new[] { poisoned, guarded, blinded, vexed }) _fx.World.DespawnMob(CreatureTrapMap, m);
        }
    }

    /// <summary>A trap nobody springs is gone when its 20 s are up (the world tick's expiry sweep). Red with the lifetime
    /// not passed to the trap (it would stay forever).</summary>
    [Fact]
    public void AnUnsprungTrapIsGoneAfterItsTime()
    {
        var (caster, cOut, _) = Caster("spy_kit", KitMap, CX + 20, CY);
        try
        {
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                caster.Receive(MenuFrame(2));
            });
            var trap = Assert.Single(_fx.World.TrapsNear(KitMap, CX + 20, CY, 0));
            _fx.World.UnderWorldLockForTest(() => trap.ExpiresAt = Environment.TickCount64 - 1);
            _fx.World.TickOnceForTest();
            Assert.Empty(_fx.World.TrapsNear(KitMap, CX + 20, CY, 0));
        }
        finally { ClearTraps(KitMap); Leave(KitMap, caster); }
    }

    // ===== the traps on a player ================================================================================

    /// <summary>Off a PvP map a player who walks onto a Spy kit trap springs nothing and the trap stays, as RTK's click
    /// returns for <c>not block:canPK(block)</c>; a ghost on a PvP map the same (<c>block.state == 1</c>). Red with the
    /// player gate taken out of <c>World.CheckPlayerTrapTrigger</c>.</summary>
    [Fact]
    public void APlayerSpringsNothingOffAPvpMapOrAsAGhost()
    {
        Assert.False(Content.IsPvpMap(OffPvpMap));
        Assert.True(Content.IsPvpMap(Arena));
        var (walker, wOut, _) = Player(OffPvpMap, CX, CY);
        var run = OpenRun(Arena, 3, skip: 0);
        var (ghost, gOut, _) = Player(Arena, run.X, run.Y, hp: 0);
        try
        {
            Assert.True(ghost.IsDead);
            _fx.World.PlaceTrap(OffPvpMap, CX + 1, CY, "toxic_spray", 77, Environment.TickCount64 + 20_000);
            _fx.World.PlaceTrap(Arena, (ushort)(run.X + 1), (ushort)run.Y, "smoke_screen", 77, Environment.TickCount64 + 20_000);
            wOut.Clear(); gOut.Clear();
            Walk(walker, East);
            Walk(ghost, East);
            Assert.True((walker.PlayerX, ghost.PlayerX) == (CX + 1, run.X + 1)
                        && _fx.World.TrapsNear(OffPvpMap, CX + 1, CY, 0).Length == 1 && !walker.Poisoned && Lines(wOut) == ""
                        && _fx.World.TrapsNear(Arena, run.X + 1, run.Y, 0).Length == 1
                        && SpellCastSupport.BuffEntry(ghost, "smoke_screen") is null && Lines(gOut) == "",
                        $"walker at {walker.PlayerX} [{Lines(wOut)}] poisoned {walker.Poisoned}; ghost at {ghost.PlayerX} [{Lines(gOut)}]");
        }
        finally { ClearTraps(OffPvpMap); ClearTraps(Arena); Leave(OffPvpMap, walker); Leave(Arena, ghost); }
    }

    /// <summary>Toxic spray on a player who walks onto it on a PvP map (the trap gone): "Poison.", graphic 1, the venoms slot
    /// for 40 s, a beat every second taking ceil(1% of their base vita) (544 of 54,321), credited to the trap's owner. A beat
    /// on an even second says "Poison is spreading through your veins.", on an odd one nothing; each draws graphic 1. A
    /// self-cast spell does not stop it (the project owner, "Tick its full time"); it never takes the last vita, and at 1
    /// vita an even second still says its line (the script's <c>health = 1</c> branch); cured, it says no "The poison
    /// passes." (the script has no uncast). Red on 461e2d9: nothing springs.</summary>
    [Fact]
    public void ToxicSprayPoisonsAPlayerForItsFullTime()
    {
        var run = OpenRun(Arena, 3, skip: 1);
        var (victim, vOut, vc) = Player(Arena, run.X, run.Y, hp: 54_321, maxHp: 54_321, mana: 10_000, maxMana: 10_000,
                                        configure: ch => ch.Spells.Add(Content.SpellByKey("incognito")!.Id));
        var oldClock = Session.PoisonLineClock;
        try
        {
            _fx.World.PlaceTrap(Arena, (ushort)(run.X + 1), (ushort)run.Y, "toxic_spray", 77, Environment.TickCount64 + 20_000);
            vOut.Clear();
            Walk(victim, East);
            string sprung = Lines(vOut);
            var drawn = Effects(vOut, victim.PlayerId);
            var slot = SpellCastSupport.BuffEntry(victim, "toxic_spray");
            int perTick = 0; uint by = 0;
            victim.WithState(() => { perTick = (int)PoisonPerTickField.GetValue(victim)!; by = (uint)PoisonByField.GetValue(victim)!; });
            Assert.True(sprung == "Poison." && drawn.SequenceEqual(new[] { 1 }) && victim.Poisoned
                        && slot is { Category: "venoms" } && slot.Value.LeftMs > 40_000 - SlackMs && slot.Value.LeftMs <= 40_000
                        && perTick == 544 && by == 77 && _fx.World.TrapsNear(Arena, run.X + 1, run.Y, 0).Length == 0,
                        $"[{sprung}], graphics [{string.Join(",", drawn)}], slot {slot}, per tick {perTick}, by {by}");

            Session.PoisonLineClock = () => 1_000;     // an even second
            var even = Beat(victim, vOut);
            Session.PoisonLineClock = () => 1_001;     // an odd second
            var odd = Beat(victim, vOut);
            victim.Receive(SpellCastSupport.CastFrame(0));   // Incognito: a self-cast spell, which RTK's engine zeroes the attacker on
            Session.PoisonLineClock = () => 1_002;
            var afterCast = Beat(victim, vOut);
            Assert.True(even == (544u, "Poison is spreading through your veins.", 1) && odd == (544u, "", 1) && afterCast.Took == 544,
                        $"even {even}, odd {odd}, after a cast {afterCast}");

            victim.WithState(() => vc.Hp = 300);
            var low = Beat(victim, vOut);
            Session.PoisonLineClock = () => 1_004;
            var atOne = Beat(victim, vOut);
            vOut.Clear();
            victim.WithState(() => victim.CurePoison());     // a cure while it holds: the script has no uncast line
            Assert.True(low == (299u, "Poison is spreading through your veins.", 1)
                        && atOne == (0u, "Poison is spreading through your veins.", 1)
                        && vc.Hp == 1 && !victim.Poisoned && Lines(vOut) == "",
                        $"low {low}, at 1 vita {atOne}, vita {vc.Hp}, poisoned {victim.Poisoned}, cured [{Lines(vOut)}]");
        }
        finally { Session.PoisonLineClock = oldClock; ClearTraps(Arena); Leave(Arena, victim); }
    }

    /// <summary>Tripwire on a player on a PvP map: "Tripwire", graphic 1, AC +30 (more damage taken) in the curses slot for
    /// 37 s; already cursed (a minor curse, Vex), nothing, and the trap is still used; carrying a protection (RTK's
    /// checkIfCast(protections)), nothing either. Red on 461e2d9: nothing springs; red with the protection test taken out of
    /// <c>Session.SpringSpyTrap</c> (the protected player is cursed).</summary>
    [Fact]
    public void TripwireCursesAPlayer()
    {
        var run = OpenRun(Arena, 3, skip: 2);
        var cursedRun = OpenRun(Arena, 3, skip: 3);
        var (victim, vOut, _) = Player(Arena, run.X, run.Y);
        var (cursed, cOut, _) = Player(Arena, cursedRun.X, cursedRun.Y);
        var guardedRun = OpenRun(Arena, 3, skip: 7);
        var (guarded, gOut, _) = Player(Arena, guardedRun.X, guardedRun.Y);
        try
        {
            cursed.WithState(() => cursed.ReceiveCurse("armor", 5, 60_000, "vex", "Vex", "minorcurses"));
            guarded.WithState(() => guarded.ReceiveCurse("", 0, 60_000, "curse_protection", "Curse protection", "protections"));
            int baseArmor = victim.ItemArmor, cursedArmor = cursed.ItemArmor, guardedArmor = guarded.ItemArmor;
            _fx.World.PlaceTrap(Arena, (ushort)(run.X + 1), (ushort)run.Y, "tripwire", 77, Environment.TickCount64 + 20_000);
            _fx.World.PlaceTrap(Arena, (ushort)(cursedRun.X + 1), (ushort)cursedRun.Y, "tripwire", 77, Environment.TickCount64 + 20_000);
            _fx.World.PlaceTrap(Arena, (ushort)(guardedRun.X + 1), (ushort)guardedRun.Y, "tripwire", 77, Environment.TickCount64 + 20_000);
            vOut.Clear(); cOut.Clear(); gOut.Clear();
            Walk(victim, East);
            Walk(cursed, East);
            Walk(guarded, East);
            var slot = SpellCastSupport.BuffEntry(victim, "tripwire");
            Assert.True(Lines(vOut) == "Tripwire" && Effects(vOut, victim.PlayerId).SequenceEqual(new[] { 1 })
                        && victim.ItemArmor - baseArmor == 30 && slot is { Category: "curses" }
                        && slot.Value.LeftMs > 37_000 - SlackMs && slot.Value.LeftMs <= 37_000
                        && Lines(cOut) == "" && cursed.ItemArmor == cursedArmor && SpellCastSupport.BuffEntry(cursed, "tripwire") is null
                        && _fx.World.TrapsNear(Arena, cursedRun.X + 1, cursedRun.Y, 0).Length == 0
                        && guarded.PlayerX == guardedRun.X + 1 && Lines(gOut) == "" && guarded.ItemArmor == guardedArmor
                        && SpellCastSupport.BuffEntry(guarded, "tripwire") is null
                        && _fx.World.TrapsNear(Arena, guardedRun.X + 1, guardedRun.Y, 0).Length == 0,
                        $"victim [{Lines(vOut)}] AC +{victim.ItemArmor - baseArmor} slot {slot}; cursed [{Lines(cOut)}] AC " +
                        $"{cursedArmor} -> {cursed.ItemArmor}; protected at {guarded.PlayerX} [{Lines(gOut)}] AC {guardedArmor} -> " +
                        $"{guarded.ItemArmor} slot {SpellCastSupport.BuffEntry(guarded, "tripwire")}");
        }
        finally { ClearTraps(Arena); Leave(Arena, victim, cursed, guarded); }
    }

    /// <summary>Smoke screen on a player on a PvP map: "You cannot see anything." and the blinds slot for 25 s, nothing
    /// drawn and nothing else (the project owner, "Line and slot only": the 4.95 client has no known way to darken the
    /// view). Red on 461e2d9: nothing springs.</summary>
    [Fact]
    public void SmokeScreenOnAPlayerIsItsLineAndSlot()
    {
        var run = OpenRun(Arena, 3, skip: 4);
        var (victim, vOut, _) = Player(Arena, run.X, run.Y);
        try
        {
            _fx.World.PlaceTrap(Arena, (ushort)(run.X + 1), (ushort)run.Y, "smoke_screen", 77, Environment.TickCount64 + 20_000);
            vOut.Clear();
            Walk(victim, East);
            var slot = SpellCastSupport.BuffEntry(victim, "smoke_screen");
            Assert.True(Lines(vOut) == "You cannot see anything." && Effects(vOut, victim.PlayerId).Count == 0
                        && slot is { Category: "blinds" } && slot.Value.LeftMs > 25_000 - SlackMs && slot.Value.LeftMs <= 25_000,
                        $"[{Lines(vOut)}], graphics [{string.Join(",", Effects(vOut, victim.PlayerId))}], slot {slot}");
        }
        finally { ClearTraps(Arena); Leave(Arena, victim); }
    }

    /// <summary>On a player whose effect is refused the trap is still used up and nothing else happens: a poisoned player
    /// springs Toxic spray (no "Poison.", their poison's source unchanged), a blinded one Smoke screen (no line, the first
    /// blind's time kept). Red with either refusal taken out of <c>Session.SpringSpyTrap</c>.</summary>
    [Fact]
    public void ARefusedEffectOnAPlayerStillUsesTheTrap()
    {
        var poisonRun = OpenRun(Arena, 3, skip: 5);
        var blindRun = OpenRun(Arena, 3, skip: 6);
        var (poisoned, pOut, _) = Player(Arena, poisonRun.X, poisonRun.Y);
        var (blinded, bOut, _) = Player(Arena, blindRun.X, blindRun.Y);
        try
        {
            poisoned.ReceivePoison(1_000, 60_000, 5, 0, "venom", "Venom");
            blinded.WithState(() => blinded.ReceiveCurse("", 0, 60_000, "blind", "Blind", "blinds"));
            long blindLeft = SpellCastSupport.BuffEntry(blinded, "blind")!.Value.LeftMs;
            _fx.World.PlaceTrap(Arena, (ushort)(poisonRun.X + 1), (ushort)poisonRun.Y, "toxic_spray", 77, Environment.TickCount64 + 20_000);
            _fx.World.PlaceTrap(Arena, (ushort)(blindRun.X + 1), (ushort)blindRun.Y, "smoke_screen", 77, Environment.TickCount64 + 20_000);
            pOut.Clear(); bOut.Clear();
            Walk(poisoned, East);
            Walk(blinded, East);
            uint by = 0;
            poisoned.WithState(() => by = (uint)PoisonByField.GetValue(poisoned)!);
            Assert.True(Lines(pOut) == "" && by == 5 && SpellCastSupport.BuffEntry(poisoned, "toxic_spray") is null
                        && Lines(bOut) == "" && SpellCastSupport.BuffEntry(blinded, "smoke_screen") is null
                        && SpellCastSupport.BuffEntry(blinded, "blind")!.Value.LeftMs <= blindLeft
                        && _fx.World.TrapsNear(Arena, poisonRun.X + 1, poisonRun.Y, 0).Length == 0
                        && _fx.World.TrapsNear(Arena, blindRun.X + 1, blindRun.Y, 0).Length == 0,
                        $"poisoned [{Lines(pOut)}] source {by}; blinded [{Lines(bOut)}]");
        }
        finally { ClearTraps(Arena); Leave(Arena, poisoned, blinded); }
    }

    // ===== Dagger to throat =====================================================================================

    /// <summary>Off a PvP map (Q4): 5,000 mana, the 1,000 s wait and "You cast Dagger to throat."; the faced player reads
    /// "You feel the cold steel from a blade pressed to your neck. ((&lt;caster&gt;))", carries the mute for 20 s, and saying
    /// anything reads "Your anxiety renders you speechless." with nothing heard. Red on 461e2d9: generic's 5 mana and no
    /// mute.</summary>
    [Fact]
    public void DaggerToThroatMutesTheFacedPlayerOnAnyMap()
    {
        Assert.False(Content.IsPvpMap(ThroatMap));
        var (caster, cOut, cc) = Caster("dagger_to_throat", ThroatMap, CX, CY);
        var (other, oOut, oc) = Player(ThroatMap, CX + 1, CY);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            long wait = SpellCastSupport.AetherLeft(caster, "dagger_to_throat");
            var mute = SpellCastSupport.BuffEntry(other, "dagger_to_throat");
            string told = Lines(oOut);
            oOut.Clear();
            other.Receive(ChatFrame(Phrase));
            Assert.True(Lines(cOut) == "You cast Dagger to throat." && cc.Mp == Mana - 5_000 && wait > 1_000_000 - SlackMs && wait <= 1_000_000
                        && told == $"You feel the cold steel from a blade pressed to your neck. (({cc.Name}))"
                        && mute is { Category: Session.MutedSpeech, Name: "Dagger to throat" } && mute.Value.LeftMs > 20_000 - SlackMs
                        && Lines(oOut) == Session.SpeechlessLine && !Speech(oOut).Any(s => s.Contains(Phrase[..10])),
                        $"caster [{Lines(cOut)}] spent {Mana - cc.Mp} wait {wait}; told [{told}]; mute {mute}; said [{Lines(oOut)}]");
        }
        finally { Leave(ThroatMap, caster, other); }
    }

    /// <summary>At nobody it still costs its 5,000 and its wait; short of 5,000, "Your will is too weak.", free; a player
    /// already muted reads the line again and keeps the first mute's time. Red with the line sent only when the mute
    /// lands.</summary>
    [Fact]
    public void DaggerToThroatCostsAtNobodyAndLeavesARunningMute()
    {
        var (lone, lOut, lc) = Caster("dagger_to_throat", ThroatMap, CX, CY + 4);
        var (poor, pOut, pc) = Caster("dagger_to_throat", ThroatMap, CX, CY + 8, mana: 4_999);
        var (caster, _, cc) = Caster("dagger_to_throat", ThroatMap, CX, CY + 12);
        var (other, oOut, _) = Player(ThroatMap, CX + 1, CY + 12);
        try
        {
            lone.Receive(SpellCastSupport.CastFrame(0));
            poor.Receive(SpellCastSupport.CastFrame(0));
            caster.Receive(SpellCastSupport.CastFrame(0));
            long first = SpellCastSupport.BuffEntry(other, "dagger_to_throat")!.Value.LeftMs;
            oOut.Clear();
            SpellCastSupport.ClearAether(caster, "dagger_to_throat");
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            long second = SpellCastSupport.BuffEntry(other, "dagger_to_throat")!.Value.LeftMs;
            Assert.True(Lines(lOut) == "You cast Dagger to throat." && lc.Mp == Mana - 5_000 && SpellCastSupport.AetherLeft(lone, "dagger_to_throat") > 0
                        && Lines(pOut) == "Your will is too weak." && pc.Mp == 4_999 && SpellCastSupport.AetherLeft(poor, "dagger_to_throat") == 0
                        && Lines(oOut) == $"You feel the cold steel from a blade pressed to your neck. (({cc.Name}))" && second <= first,
                        $"lone [{Lines(lOut)}]; poor [{Lines(pOut)}]; again [{Lines(oOut)}] left {first} -> {second}");
        }
        finally { Leave(ThroatMap, lone, poor, caster, other); }
    }

    /// <summary>At its time the mute ends with "Your anxiety subsides." (its SpellText.csv fade line, the script's uncast)
    /// and the player speaks again. Red without the fade row (no line).</summary>
    [Fact]
    public void ADaggerToThroatEndsWithItsLine()
    {
        var (caster, _, _) = Caster("dagger_to_throat", ThroatMap, CX + 10, CY);
        var (other, oOut, oc) = Player(ThroatMap, CX + 11, CY);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            SpellCastSupport.EndBuff(other, "dagger_to_throat");
            oOut.Clear();
            other.RegenTick(0, regenDue: true);
            string end = Lines(oOut);
            oOut.Clear();
            other.Receive(ChatFrame(Phrase));
            Assert.True(end == "Your anxiety subsides." && Speech(oOut).Any(s => s.Contains($"{oc.Name}: {Phrase}")),
                        $"end [{end}], speech [{string.Join(" | ", Speech(oOut))}]");
        }
        finally { Leave(ThroatMap, caster, other); }
    }

    /// <summary>The mute and the line are written inside the player's own monitor: while another thread holds it the cast
    /// waits and the player carries no mute, in both <c>StateRank</c> orders. Red with <c>Session.LuaSpeechStatus</c>'s and
    /// <c>LuaTellTargetLine</c>'s monitor entry taken out.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DaggerToThroatWritesThePlayerInsideTheirOwnMonitor(bool casterRanksFirst)
    {
        int y = casterRanksFirst ? CY : CY + 6;
        Session caster, target;
        if (casterRanksFirst)
        {
            (caster, _, _) = Caster("dagger_to_throat", ThroatHeldMap, CX, y);
            (target, _, _) = Player(ThroatHeldMap, CX + 1, y);
        }
        else
        {
            (target, _, _) = Player(ThroatHeldMap, CX + 1, y);
            (caster, _, _) = Caster("dagger_to_throat", ThroatHeldMap, CX, y);
        }
        Assert.Equal(casterRanksFirst, caster.StateRank < target.StateRank);
        var run = new HeldRun();
        try
        {
            WhileAnotherThreadHolds(run, target, () => caster.Receive(SpellCastSupport.CastFrame(0)),
                                    untouched: () => SpellCastSupport.BuffEntry(target, "dagger_to_throat") is null);
            Assert.Null(run.HolderFault);
            Assert.Null(run.ActorFault);
            Assert.False(run.FinishedWhileHeld, "the cast finished while another thread held the player's monitor");
            Assert.True(run.UntouchedWhileHeld, "the player was muted while another thread held their monitor");
            Assert.NotNull(SpellCastSupport.BuffEntry(target, "dagger_to_throat"));
        }
        finally { if (run.Settled) Leave(ThroatHeldMap, caster, target); }
    }

    // ===== the teacher ==========================================================================================

    /// <summary>The Spy teacher sells eight, each at its script's level: Skill up, Spy kit and Mimic (no requirements())
    /// from their rows' 0, the other five from 99. Apply stealth, whose cast is empty, has no price. Red without the price
    /// rows: Assassinate alone.</summary>
    [Fact]
    public void TheSpyTeacherSellsEight()
    {
        int path = Content.PathIdForClass("Spy");
        Assert.Equal(19, path);
        var sold = Content.SubpathTeacherSpells(path, 99, Content.MaxMark);
        Assert.Equal("assassinate:99 assess_opponent_spy:99 dagger_to_throat:99 hear_footstep_spy:99 incognito:99 mimic_spy:0 " +
                     "skill_up_spy:0 spy_kit:0",
                     string.Join(" ", sold.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}:{s.Level}")));
        Assert.All(sold, s => Assert.Equal(path, s.PathId));
        Assert.Null(Content.LearnCostFor(Content.SpellByKey("apply_stealth")!, path));
        Assert.Equal("mimic_spy skill_up_spy spy_kit",
                     string.Join(" ", Content.SubpathTeacherSpells(path, 98, Content.MaxMark).Select(s => s.Key).OrderBy(k => k, StringComparer.Ordinal)));
    }

    // ===== helpers ==============================================================================================

    private const string Phrase = "the quick brown fox jumps over the lazy dog";
    private const byte East = 1;

    private static string Lines(RecordingOutbound o) => string.Join(" | ", SpellCastSupport.MiniTexts(o));

    private static List<string> Speech(RecordingOutbound o) =>
        o.BodiesOf(ServerOp.Speech).Select(b => Encoding.ASCII.GetString(b)).ToList();

    private static byte[] ChatFrame(string text)
    {
        byte[] m = Encoding.ASCII.GetBytes(text);
        var body = new List<byte> { 0, (byte)m.Length };
        body.AddRange(m);
        return SessionFixture.Frame(ClientOp.Chat, body.ToArray());
    }

    /// <summary>A menu pick (0x3A kind 2, the option's 1-based index at [10]).</summary>
    private static byte[] MenuFrame(byte index)
    {
        var body = new byte[11];
        body[0] = 0x02;
        body[10] = index;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>Every menu (0x30 kind 2) the player was shown, as "prompt|option|option...".</summary>
    private static List<string> Menus(RecordingOutbound o)
    {
        var menus = new List<string>();
        foreach (var b in o.BodiesOf(ServerOp.NpcDialog).Where(b => b.Length >= 23 && b[0] == 2 && b[1] == 2))
        {
            int len = (b[21] << 8) | b[22];
            var parts = new List<string> { Encoding.ASCII.GetString(b, 23, len) };
            int at = 23 + len, n = b[at++];
            for (int i = 0; i < n; i++)
            {
                int ol = b[at++];
                parts.Add(Encoding.ASCII.GetString(b, at, ol));
                at += ol;
            }
            menus.Add(string.Join("|", parts));
        }
        return menus;
    }

    private static uint U32(byte[] b, int at) => (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]);

    /// <summary>The spell graphics (0x29) sent over <paramref name="id"/>, as effect ids.</summary>
    private static List<int> Effects(RecordingOutbound outbound, uint id) =>
        outbound.BodiesOf(ServerOp.Effect).Where(b => U32(b, 0) == id).Select(b => b[4] - ServerConfig.Current.EfxWireOffset).ToList();

    private static void FreeSlot(Session s) => s.WithState(() => { NextActionTick.SetValue(s, 0L); ActionCount.SetValue(s, 0); });

    private static long StealthLeft(Session s)
    {
        long left = 0;
        s.WithState(() => left = (long)StealthUntilField.GetValue(s)! - Environment.TickCount64);
        return left;
    }

    private static int StealthTimes(Session s)
    {
        int times = 0;
        s.WithState(() => times = (int)StealthTimesField.GetValue(s)!);
        return times;
    }

    /// <summary>The caster's running periodic ticks, as (spell, verb, beat ms).</summary>
    private static List<(string Spell, string Verb, int Every)> Ticks(Session s)
    {
        var list = new List<(string, string, int)>();
        s.WithState(() =>
        {
            foreach (var t in (System.Collections.IList)TicksField.GetValue(s)!)
                list.Add(((string)F(t, "SpellKey"), (string)F(t, "Verb"), (int)F(t, "EveryMs")));
        });
        return list;
    }

    /// <summary>Put <paramref name="s"/> in a creature's form (a rogue cloak's), as DivinerSpellTests does.</summary>
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

    private static object F(object o, string name) => o.GetType().GetField(name)!.GetValue(o)!;

    /// <summary>One poison beat on <paramref name="s"/>, forced due: the vita it took, its line ("" for none) and how many
    /// graphics it drew over them.</summary>
    private static (uint Took, string Line, int Drawn) Beat(Session s, RecordingOutbound o)
    {
        o.Clear();
        uint before = 0, after = 0;
        s.WithState(() =>
        {
            before = s.CharHp;
            PoisonNextTickField.SetValue(s, 0L);
            s.TickPoison();
            after = s.CharHp;
        });
        return (before - after, Lines(o), Effects(o, s.PlayerId).Count);
    }

    /// <summary>A map's size as Maps.csv has it, or a wide 100 x 100 for a content-free map.</summary>
    private static void Size(Character c, ushort map)
    {
        if (Content.Maps.TryGetValue(map, out var m)) { c.MapXs = m.Xs; c.MapYs = m.Ys; }
        else { c.MapXs = 100; c.MapYs = 100; }
    }

    /// <summary>The <paramref name="skip"/>'th east-west run of <paramref name="length"/> open tiles on
    /// <paramref name="map"/> (no wall, no warp), the runs at least four rows apart, so each fact walks its own. The
    /// arena has no terrain file here, so any run is open; the search keeps the facts right if one is ever added.</summary>
    private static (int X, int Y) OpenRun(ushort map, int length, int skip)
    {
        // A map with no terrain file here (no map_index row) is open everywhere, on the fixture's wide 100 x 100.
        ushort xs = 100, ys = 100;
        if (Content.Maps.TryGetValue(map, out var info)) { xs = info.Xs; ys = info.Ys; }
        var terrain = MapData.For(map, xs, ys);
        int found = 0, lastY = -100;
        for (int y = 2; y < ys - 2; y++)
        {
            if (y - lastY < 4) continue;
            for (int x = 2; x + length < xs - 2; x++)
            {
                bool open = Enumerable.Range(0, length).All(i =>
                    (terrain is null || !terrain.Solid(x + i, y)) && !Content.TryWarp(map, (ushort)(x + i), (ushort)y, out _));
                if (!open) continue;
                if (found++ == skip) return (x, y);
                lastY = y;
                break;
            }
        }
        throw new InvalidOperationException($"map {map} has no open run #{skip}");
    }

    /// <summary>A step on the walk frame (0x06), from where the player stands.</summary>
    private static void Walk(Session s, byte dir)
    {
        ushort x = s.PlayerX, y = s.PlayerY;
        s.Receive(SessionFixture.Frame(ClientOp.Walk, new byte[] { dir, 0, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y }));
    }

    /// <summary>A creature's committed step onto (<paramref name="nx"/>, <paramref name="ny"/>), as the mob tick commits it.</summary>
    private void Step(ushort map, Mob mob, int nx, int ny, byte dir) =>
        _fx.World.UnderWorldLockForTest(() =>
        {
            var ctx = _fx.World.MobTickContextForTest(map);
            Assert.True(World.MobMovement.StepToForTest(ctx, mob, nx, ny, dir));
        });

    /// <summary>Every trap left on <paramref name="map"/> lapses and the world tick's sweep takes it.</summary>
    private void ClearTraps(ushort map)
    {
        _fx.World.UnderWorldLockForTest(() =>
        {
            foreach (var t in _fx.World.TrapsNear(map, 0, 0, 10_000)) t.ExpiresAt = 1;
        });
        _fx.World.TickOnceForTest();
    }

    /// <summary>Run <paramref name="body"/> on a thread of its own with no synchronisation context, as a read loop runs a
    /// packet: an await inside it (a menu's) then resumes inline on the thread that answers it.</summary>
    private static void OnOwnThread(Action body)
    {
        Exception? fault = null;
        var t = new Thread(() => fault = Record.Exception(body)) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(30)), "the packet thread never finished");
        Assert.Null(fault);
    }

    /// <summary>A letters-only name, unique in this process.</summary>
    private static string Letters(string stem)
    {
        int n = Interlocked.Increment(ref _serial);
        var sb = new StringBuilder(stem);
        do { sb.Append((char)('a' + n % 26)); n /= 26; } while (n > 0);
        return sb.ToString();
    }

    private Mob AddCreature(ushort map, int x, int y, int hp = 100_000, int maxHp = 100_000)
    {
        var mob = new Mob(_fx.World.AllocateMobId(), 1, (ushort)x, (ushort)y, $"Spy target {Interlocked.Increment(ref _serial)}", maxHp);
        mob.Hp = hp;
        _fx.World.AddMob(map, mob);
        return mob;
    }

    /// <summary>A level-99 Spy at (<paramref name="x"/>, <paramref name="y"/>) facing east, with <paramref name="key"/> in
    /// slot 0 of the book, Will 41, on the map's own size (a wide one for a content-free map).</summary>
    private (Session session, RecordingOutbound outbound, Character c) Caster(string key, ushort map, int x, int y,
                                                                            uint mana = Mana, uint maxMana = MaxMana) =>
        _fx.PlayerWith(Letters("Spy"), ch =>
        {
            ch.ClassName = "Spy";
            ch.Level = 99;
            ch.Ac = 0;
            ch.Will = Will;
            ch.MaxHp = Hp; ch.Hp = Hp;
            ch.MaxMp = maxMana; ch.Mp = mana;
            ch.Dir = 1;
            Size(ch, map);
            ch.Spells.Add(Content.SpellByKey(key)!.Id);
        }, map, (ushort)x, (ushort)y);

    private (Session session, RecordingOutbound outbound, Character c) Player(ushort map, int x, int y, uint hp = Hp,
                                                                            uint maxHp = Hp, uint mana = 0, uint maxMana = 1_000,
                                                                            Action<Character>? configure = null) =>
        _fx.PlayerWith(Letters("Other"), ch =>
        {
            ch.Level = 50;
            ch.Ac = 0;
            ch.MaxHp = maxHp; ch.Hp = hp;
            ch.MaxMp = maxMana; ch.Mp = mana;
            ch.Dir = 1;
            Size(ch, map);
            configure?.Invoke(ch);
        }, map, (ushort)x, (ushort)y);

    private void Leave(ushort map, params Session[] sessions)
    {
        foreach (var s in sessions) _fx.World.LeaveMap(s, map);
    }

    // ---- the held-monitor run (MonkSpellTests' shape) -----------------------------------------------------------------

    private sealed class HeldRun
    {
        public bool Settled = true;
        public bool FinishedWhileHeld;
        public bool UntouchedWhileHeld;
        public Exception? HolderFault, ActorFault;
    }

    /// <summary>A third thread takes <paramref name="target"/>'s monitor; then <paramref name="act"/> runs on a thread of its
    /// own until it finishes or sits blocked for 100 ms; then, still inside the third thread's section,
    /// <paramref name="untouched"/> says whether the target's state is as the third thread left it. Then the third thread
    /// lets go and both are joined.</summary>
    private static void WhileAnotherThreadHolds(HeldRun run, Session target, Action act, Func<bool> untouched)
    {
        var held = new ManualResetEventSlim();
        var letGo = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            try
            {
                target.WithState(() =>
                {
                    held.Set();
                    letGo.Wait();
                    run.UntouchedWhileHeld = untouched();
                });
            }
            catch (Exception e) { run.HolderFault = e; }
        }) { IsBackground = true };
        var actor = new Thread(() =>
        {
            try { act(); }
            catch (Exception e) { run.ActorFault = e; }
        }) { IsBackground = true };

        run.Settled = false;
        try
        {
            holder.Start();
            Assert.True(held.Wait(TimeSpan.FromSeconds(30)), "the holder never took the target's monitor");
            actor.Start();
            WaitUntilDoneOrBlocked(actor);
            run.FinishedWhileHeld = !actor.IsAlive;
        }
        finally
        {
            letGo.Set();
        }
        Assert.True(holder.Join(TimeSpan.FromSeconds(60)), "the holder never let go");
        Assert.True(actor.Join(TimeSpan.FromSeconds(60)), "the action never finished after the monitor was free");
        run.Settled = true;
    }

    /// <summary>Wait until <paramref name="t"/> has finished, or has sat blocked for 100 ms straight, or 10 s have passed.</summary>
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
}
