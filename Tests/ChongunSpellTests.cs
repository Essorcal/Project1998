using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The Chongun spells slice (the project owner, 2026-10-26, P5 "each spell as its RTK script", P1 and P3 "on sale once it
/// works, at its script's price"; 2026-10-30, Q4 "Yes, as RTK": Spirit of war at RTK's tiers and armour sign; 2026-11-09,
/// the slice's questions: Art of war as the readout its script reaches for, Q-A; Inspire valor's +3 Might as meant, Q-B);
/// Title of nobility's wait is saved as RTK under P5. Before, all fourteen Chongun rows fell to the <c>generic</c> verb, 5
/// mana and "You cast X." and nothing else (<c>Tests/Fixtures/spell-dispatch-routes.txt</c> had them on
/// <c>verb=generic</c>); Recognize honorable act, which has no script, still does.
///
/// <para>The scripts: <c>RTK-Server/rtklua/Accepted/Spells/Subpaths/Chongun/</c>, one file each, every spell bound by its
/// <c>game-data/SpellParams.csv</c> row to a verb in the Chongun region of <c>game-data/spell_verbs.lua</c> (Evaluate
/// opponent to Do's <c>assess</c>, Spirit of war to <c>chungryong_rage</c>'s <c>rtk_rage</c> ladder).</para>
///
/// <para>Every cast is the real 0x0F frame through <c>Session.Receive</c>, by a fresh level-99 Chongun facing east, on
/// content-free maps 63300-63329 (no Maps.csv row: no terrain, PvP off). A menu or an input box is answered from a thread
/// of its own with no synchronisation context, as a read loop answers it, so the step resumes inline as on the
/// server.</para>
/// </summary>
[Collection("world")]
public sealed class ChongunSpellTests
{
    private const ushort EvalMap = 63300, ValorMap = 63301, ValorRefuseMap = 63302, ValorHeldMap = 63303, ArtMap = 63304,
                         InvMap = 63305, InvTickMap = 63306, FuryMap = 63307, WeaponMap = 63308, DrinkMap = 63309,
                         UniformMap = 63310, TroopMap = 63311, TroopBlockMap = 63312, TitleMap = 63313, TitleFarMap = 63314,
                         TitleHeldMap = 63315, WarMap = 63316, TroopRefuseMap = 63317, TitleWaitMap = 63318,
                         TitleRefuseMap = 63319, ArtNothingMap = 63320, InvRefuseMap = 63321, UniformEndMap = 63322;
    // Fix round 1 (#420's review): content-free too, claimed by no other class; and the Chongun Guide map, which has terrain
    // and no NPC, spawn or other class's facts on it.
    private const ushort InvEndMap = 63323, TroopEdgeMap = 63324, TitleReplacedMap = 63325, GuideMap = 3611;
    // Fix round 2 (the re-check's F8): content-free, claimed by no other class.
    private const ushort InvClearMap = 63326;
    // Fix round 3 (Invincibility stops swings only): content-free, claimed by no other class.
    private const ushort WardKindMap = 63327;
    private const ushort CX = 30, CY = 30;
    private const uint MaxMana = 200_000, Mana = 200_000, Hp = 10_000;

    /// <summary>Slack on a wait or a run read straight after the cast.</summary>
    private const long SlackMs = 5_000;

    private static readonly FieldInfo NextActionTick =
        typeof(Session).GetField("_nextActionTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ActionCount =
        typeof(Session).GetField("_actionCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo RageUntilField =
        typeof(Session).GetField("_rageUntil", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo MorphLookField =
        typeof(Session).GetField("_morphLook", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo MorphColorField =
        typeof(Session).GetField("_morphColor", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo MorphUntilField =
        typeof(Session).GetField("_morphUntil", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo StatusFlagsField =
        typeof(Session).GetField("_statusFlags", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo DurationLineDueField =
        typeof(Session).GetField("_durationLineDue", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ReplacedField =
        typeof(Session).GetField("_replaced", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo DirtyField =
        typeof(Session).GetField("_dirty", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly SessionFixture _fx;
    private static int _serial;

    public ChongunSpellTests(SessionFixture fx) => _fx = fx;

    // ===== Evaluate opponent ====================================================================================

    /// <summary>A creature reads "[&lt;name&gt;] VIT: &lt;max vita&gt;"; a player "[&lt;title&gt; &lt;name&gt;]",
    /// "VIT: &lt;vita&gt;" and "MANA: &lt;mana&gt;" with commas. No mana is taken and no wait armed (the script checks its 200
    /// and never takes it, and arms none). Short of 200, "Your will is too weak.", for free. Red on 5feb96a: generic's 5
    /// mana and "You cast Evaluate opponent.".</summary>
    [Fact]
    public void EvaluateOpponentReadsACreatureOrAPlayerAndTakesNoMana()
    {
        var (caster, cOut, cc) = Caster("evaluate_opponent", EvalMap, CX, CY);
        var (poor, pOut, pc) = Caster("evaluate_opponent", EvalMap, CX, CY + 4, mana: 199);
        var (target, _, _) = Player(EvalMap, CX + 3, CY, configure: ch => { ch.Hp = 1_234; ch.Mp = 5_678; });
        var mob = AddCreature(EvalMap, CX + 1, CY, hp: 100, maxHp: 12_345);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0, mob.Id));
            string creature = Lines(cOut);
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0, target.PlayerId));
            string player = Lines(cOut);
            poor.Receive(SpellCastSupport.CastFrame(0, mob.Id));
            string expectPlayer = $"[{target.ClassTitle} {target.CharName}] | VIT: 1,234 | MANA: 5,678";
            Assert.True(creature == $"[{mob.Name}] VIT: 12345" && player == expectPlayer && cc.Mp == Mana
                        && SpellCastSupport.AetherLeft(caster, "evaluate_opponent") == 0
                        && Lines(pOut) == "Your will is too weak." && pc.Mp == 199,
                        $"creature [{creature}], player [{player}] (want [{expectPlayer}]), spent {Mana - cc.Mp}, " +
                        $"short [{Lines(pOut)}] mana {pc.Mp}");
        }
        finally { _fx.World.DespawnMob(EvalMap, mob); Leave(EvalMap, caster, poor, target); }
    }

    // ===== Inspire valor ========================================================================================

    /// <summary>For 1,000 mana, a 25 s wait and "You cast Inspire valor.", every OTHER player within 10 tiles on both axes
    /// (one at the corner of that square, a ghost beside) has their might buff ended and takes +3 Might for 112 s in the
    /// mights slot, under the spell's name, with graphic 11 over them and "&lt;caster&gt; cast Inspire valor on you.";
    /// sound 12 plays at the caster. One 11 tiles off is untouched, and so is the caster. Red on 5feb96a (generic); red
    /// with the category's buffs left (Q-B's "replaces their other Might buffs").</summary>
    [Fact]
    public void InspireValorGivesEveryOtherPlayerInReachThreeMight()
    {
        var (caster, cOut, cc) = Caster("inspire_valor", ValorMap, CX, CY);
        var (near, nOut, _) = Player(ValorMap, CX + 10, CY - 10);
        var (ghost, gOut, _) = Player(ValorMap, CX - 1, CY, hp: 0);
        var (far, fOut, _) = Player(ValorMap, CX + 11, CY);
        try
        {
            int baseMight = near.LuaMight;
            near.ReceiveTimedBuff(new[] { "might" }, new[] { "5" }, 600_000, "might_rogue", "Might", "mights");
            int casterMight = caster.LuaMight;
            caster.Receive(SpellCastSupport.CastFrame(0));
            var problems = new List<string>();
            string line = $"{caster.CharName} cast Inspire valor on you.";
            foreach (var (who, o, name) in new[] { (near, nOut, "near"), (ghost, gOut, "ghost") })
            {
                var entry = SpellCastSupport.BuffEntry(who, "inspire_valor");
                if (entry is not { Category: "mights", Name: "Inspire valor" } e || e.LeftMs <= 112_000 - SlackMs || e.LeftMs > 112_000)
                    problems.Add($"{name}: entry {entry}");
                if (!Lines(o).Equals(line)) problems.Add($"{name}: lines [{Lines(o)}]");
                if (!Effects(cOut, who.PlayerId).SequenceEqual(new[] { 11 })) problems.Add($"{name}: graphics [{string.Join(",", Effects(cOut, who.PlayerId))}]");
            }
            if (SpellCastSupport.BuffEntry(near, "might_rogue") is not null) problems.Add("near: the might buff was left running");
            if (near.LuaMight != baseMight + 3) problems.Add($"near: might {near.LuaMight} (want {baseMight + 3})");
            if (SpellCastSupport.BuffEntry(far, "inspire_valor") is not null || Lines(fOut).Length > 0) problems.Add($"far: reached, [{Lines(fOut)}]");
            if (SpellCastSupport.BuffEntry(caster, "inspire_valor") is not null || caster.LuaMight != casterMight) problems.Add("the caster took it");
            long wait = SpellCastSupport.AetherLeft(caster, "inspire_valor");
            if (Lines(cOut) != "You cast Inspire valor." || Mana - cc.Mp != 1_000 || wait <= 25_000 - SlackMs || wait > 25_000)
                problems.Add($"caster: [{Lines(cOut)}], spent {Mana - cc.Mp}, wait {wait} ms");
            if (!Sounds(cOut).Contains(12)) problems.Add($"sounds [{string.Join(",", Sounds(cOut))}]");
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }
        finally { Leave(ValorMap, caster, near, ghost, far); }
    }

    /// <summary>Inside its 25 s wait, "Inspire valor isn't ready yet."; short of 1,000, "Your will is too weak."; both free.
    /// With nobody near, the cast still costs and says its line. Red on 5feb96a.</summary>
    [Fact]
    public void InspireValorRefusesInsideItsWaitAndShortOfMana()
    {
        var (caster, cOut, cc) = Caster("inspire_valor", ValorRefuseMap, CX, CY);
        var (poor, pOut, pc) = Caster("inspire_valor", ValorRefuseMap, CX, CY + 20, mana: 999);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string first = Lines(cOut);
            uint after = cc.Mp;
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            poor.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(first == "You cast Inspire valor." && Mana - after == 1_000 && Lines(cOut) == "Inspire valor isn't ready yet."
                        && cc.Mp == after && Lines(pOut) == "Your will is too weak." && pc.Mp == 999,
                        $"first [{first}] spent {Mana - after}; again [{Lines(cOut)}]; short [{Lines(pOut)}] mana {pc.Mp}");
        }
        finally { Leave(ValorRefuseMap, caster, poor); }
    }

    /// <summary>Each other player's slot is decided and written inside their own monitor: while a third thread holds a
    /// player's monitor the cast waits and that player carries no Inspire valor, in both <c>StateRank</c> orders; once
    /// the monitor is free, they do. Red with <c>Session.ReceiveAreaBuff</c> outside the player's monitor (a Release
    /// build, where the list writers' monitor asserts are compiled out).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InspireValorWritesEachPlayerInsideTheirOwnMonitor(bool casterRanksFirst)
    {
        Session caster, near;
        Character cc;
        if (casterRanksFirst)
        {
            (caster, _, cc) = Caster("inspire_valor", ValorHeldMap, CX, CY);
            (near, _, _) = Player(ValorHeldMap, CX + 1, CY);
        }
        else
        {
            (near, _, _) = Player(ValorHeldMap, CX + 1, CY);
            (caster, _, cc) = Caster("inspire_valor", ValorHeldMap, CX, CY);
        }
        Assert.Equal(casterRanksFirst, caster.StateRank < near.StateRank);
        var run = new HeldRun();
        try
        {
            WhileAnotherThreadHolds(run, near, () => caster.Receive(SpellCastSupport.CastFrame(0)),
                                    untouched: () => SpellCastSupport.BuffEntry(near, "inspire_valor") is null);
            Assert.Null(run.HolderFault);
            Assert.Null(run.ActorFault);
            Assert.False(run.FinishedWhileHeld, "the cast finished while another thread held a player's monitor");
            Assert.True(run.UntouchedWhileHeld, "a player's slot was written while another thread held their monitor");
            Assert.True(SpellCastSupport.BuffEntry(near, "inspire_valor") is not null && cc.Mp == Mana - 1_000,
                        $"after: entry {SpellCastSupport.BuffEntry(near, "inspire_valor")}, spent {Mana - cc.Mp}");
        }
        finally { if (run.Settled) Leave(ValorHeldMap, caster, near); }
    }

    // ===== Art of war ===========================================================================================

    /// <summary>The creature on the faced tile reads "&lt;name&gt; Max Health: &lt;max vita&gt;" and the 80 is taken; the
    /// readout replaces "You cast Art of war.". Red on 5feb96a: generic's 5 mana and the line.</summary>
    [Fact]
    public void ArtOfWarReadsTheFacedCreaturesMaxVita()
    {
        var (caster, cOut, cc) = Caster("art_of_war", ArtMap, CX, CY);
        var mob = AddCreature(ArtMap, CX + 1, CY, hp: 100, maxHp: 4_321);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(cOut) == $"{mob.Name} Max Health: 4321" && Mana - cc.Mp == 80,
                        $"lines [{Lines(cOut)}], spent {Mana - cc.Mp}");
        }
        finally { _fx.World.DespawnMob(ArtMap, mob); Leave(ArtMap, caster); }
    }

    /// <summary>Facing nothing, a player or an NPC, nothing happens and nothing is spent (the script's <c>if target.blType
    /// == BL_MOB</c> falls through); short of 80, "You do not have enough mana.", for free, before anything is looked at.
    /// Red on 5feb96a: 5 mana and "You cast Art of war." each time.</summary>
    [Theory]
    [InlineData("nothing")]
    [InlineData("player")]
    [InlineData("npc")]
    public void ArtOfWarFacingNoCreatureDoesNothing(string facing)
    {
        int row = facing switch { "nothing" => 0, "player" => 1, _ => 2 };
        ushort y = (ushort)(CY + 4 * row);
        var (caster, cOut, cc) = Caster("art_of_war", ArtNothingMap, CX, y);
        var (poor, pOut, pc) = Caster("art_of_war", ArtNothingMap, CX + 6, y, mana: 79);
        Session? other = null;
        Mob? npc = null;
        if (facing == "player") (other, _, _) = Player(ArtNothingMap, CX + 1, y);
        if (facing == "npc") npc = AddCreature(ArtNothingMap, CX + 1, y, npc: true);
        var poorMob = AddCreature(ArtNothingMap, CX + 7, y);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            poor.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(cOut) == "" && cc.Mp == Mana && Lines(pOut) == "You do not have enough mana." && pc.Mp == 79,
                        $"{facing}: [{Lines(cOut)}] spent {Mana - cc.Mp}; short [{Lines(pOut)}] mana {pc.Mp}");
        }
        finally
        {
            _fx.World.DespawnMob(ArtNothingMap, poorMob);
            if (npc is not null) _fx.World.DespawnMob(ArtNothingMap, npc);
            Leave(ArtNothingMap, caster, poor);
            if (other is not null) Leave(ArtNothingMap, other);
        }
    }

    // ===== Invincibility ========================================================================================

    /// <summary>At the best armour (clamped to -80, where RTK's roll lands 71 times in 100) a cast that lands takes 1,500,
    /// says the script's two lines, wards its caster for 3 s (a creature's swing takes nothing) and arms a 3 s wait, with
    /// graphic 11 and sound 5; a cast that fails takes 1,500 too, says "Something went wrong." and arms nothing. Under the
    /// ward, a cast says "You already cast that spell.", for free. Red on 5feb96a (generic); red without the ward key in
    /// <c>Session.HardBodyWarded</c> (the swing lands, and the ward goes unseen).</summary>
    [Fact]
    public void InvincibilityLandsByRtksRollAndWardsItsCaster()
    {
        var (caster, cOut, cc) = Caster("invincibility", InvMap, CX, CY, configure: ch => ch.Ac = -100);
        var mob = AddCreature(InvMap, CX + 1, CY);
        try
        {
            bool landed = false;
            var problems = new List<string>();
            for (int i = 0; i < 40 && !landed; i++)
            {
                uint before = cc.Mp;
                cOut.Clear();
                FreeSlot(caster);
                caster.Receive(SpellCastSupport.CastFrame(0));
                string lines = Lines(cOut);
                long wait = SpellCastSupport.AetherLeft(caster, "invincibility");
                if (lines == "Harden body spell | You cast Invincibility.")
                {
                    landed = true;
                    if (before - cc.Mp != 1_500 || wait <= 3_000 - SlackMs || wait > 3_000 || !caster.HardBodyWarded
                        || !Effects(cOut, caster.PlayerId).Contains(11) || !Sounds(cOut).Contains(5))
                        problems.Add($"landed: spent {before - cc.Mp}, wait {wait}, ward {caster.HardBodyWarded}, " +
                                     $"graphics [{string.Join(",", Effects(cOut, caster.PlayerId))}], sounds [{string.Join(",", Sounds(cOut))}]");
                }
                else if (lines != "Something went wrong." || before - cc.Mp != 1_500 || wait != 0 || caster.HardBodyWarded)
                    problems.Add($"cast {i}: [{lines}], spent {before - cc.Mp}, wait {wait}, ward {caster.HardBodyWarded}");
            }
            Assert.True(landed && problems.Count == 0, $"landed {landed}; " + string.Join("\n", problems));

            uint hp = cc.Hp;
            caster.ApplyMobHit(mob, 500);
            SpellCastSupport.ClearAether(caster, "invincibility");
            uint mp = cc.Mp;
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(cc.Hp == hp && Lines(cOut) == "You already cast that spell." && cc.Mp == mp,
                        $"after a swing {hp} -> {cc.Hp}; again [{Lines(cOut)}] spent {mp - cc.Mp}");
        }
        finally { _fx.World.DespawnMob(InvMap, mob); Leave(InvMap, caster); }
    }

    /// <summary>RTK's roll, not Harden Body's: at armour +70 (the clamp's top) Invincibility fails whenever
    /// <c>math.random(1, 100) &lt; (140 + 70) / 2</c>, which is always, so thirty casts all fail, each taking its 1,500 and
    /// arming no wait. Harden Body's roll (<c>50 + floor(-armour / 3)</c>, 27 in 100 at +70) would land one of thirty but
    /// for 1 time in 12,000. Red with <c>verbs.harden_body</c>'s roll.</summary>
    [Fact]
    public void AtArmourSeventyInvincibilityNeverLands()
    {
        var (caster, cOut, cc) = Caster("invincibility", InvMap, CX, CY + 6, configure: ch => ch.Ac = 100);
        try
        {
            Assert.Equal(70, caster.ItemArmor);
            for (int i = 0; i < 30; i++)
            {
                uint before = cc.Mp;
                cOut.Clear();
                FreeSlot(caster);
                caster.Receive(SpellCastSupport.CastFrame(0));
                Assert.True(Lines(cOut) == "Something went wrong." && before - cc.Mp == 1_500
                            && SpellCastSupport.AetherLeft(caster, "invincibility") == 0 && !caster.HardBodyWarded,
                            $"cast {i}: [{Lines(cOut)}], spent {before - cc.Mp}, ward {caster.HardBodyWarded}");
            }
        }
        finally { Leave(InvMap, caster); }
    }

    /// <summary>Short of 1,500, "Your will is too weak."; under Harden Body, "You already cast that spell." (RTK's
    /// checkIfCast(hardBodies)); both free; mana is checked first, as the script orders it. Red on 5feb96a.</summary>
    [Fact]
    public void InvincibilityRefusesShortOfManaAndUnderAWard()
    {
        var (poor, pOut, pc) = Caster("invincibility", InvRefuseMap, CX, CY, mana: 1_499);
        var (warded, wOut, wc) = Caster("invincibility", InvRefuseMap, CX, CY + 4);
        try
        {
            poor.ItemSetStatus("harden_body_poet", 60_000);
            warded.ItemSetStatus("harden_body_poet", 60_000);
            poor.Receive(SpellCastSupport.CastFrame(0));
            warded.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(pOut) == "Your will is too weak." && pc.Mp == 1_499
                        && Lines(wOut) == "You already cast that spell." && wc.Mp == Mana,
                        $"short [{Lines(pOut)}] mana {pc.Mp}; warded [{Lines(wOut)}] spent {Mana - wc.Mp}");
        }
        finally { Leave(InvRefuseMap, poor, warded); }
    }

    /// <summary>Under Invincibility a creature's swing takes nothing, but a venom tick still does (RTK's Invincibility is
    /// read only by its swing script, not by removeHealthExtend, which venom's tick calls); under Harden
    /// Body the tick takes nothing, as before. Red with Invincibility among the tick's wards (<c>Session.DamageImmune</c>),
    /// and red with the gate not reading it for a swing.</summary>
    [Fact]
    public void AVenomTickLandsUnderInvincibilityAndASwingDoesNot()
    {
        var (p, o, c) = Player(InvTickMap, CX, CY, hp: 1_000);
        var (hb, hbOut, hbc) = Player(InvTickMap, CX, CY + 4, hp: 1_000);
        var mob = AddCreature(InvTickMap, CX + 1, CY);
        try
        {
            p.ItemSetStatus("invincibility", 60_000);
            hb.ItemSetStatus("harden_body_poet", 60_000);
            p.ApplyMobHit(mob, 500);
            uint afterSwing = c.Hp;
            Venom(p, 300);
            Venom(hb, 300);
            Tick(p, o);
            Tick(hb, hbOut);
            Assert.True(afterSwing == 1_000 && c.Hp == 700 && hbc.Hp == 1_000,
                        $"swing left {afterSwing}; tick left {c.Hp} (want 700); under Harden Body {hbc.Hp}");
        }
        finally { _fx.World.DespawnMob(InvTickMap, mob); Leave(InvTickMap, p, hb); }
    }

    /// <summary>Invincibility stops a swing only, as RTK's, which reads its ward in <c>Scripts/hitCritChance.lua</c> and
    /// nowhere else that stops a blow: under it a creature's swing and another player's swing deal nothing, while a
    /// creature's spell and another player's spell land (the project owner, 2026-11-09, "Invincibility swings-only
    /// first"). Each damage kind through its real entry point, as <c>DamageIntakeTests</c> drives them. Red with the gate
    /// reading Invincibility for every kind (the spells) or for none (the swings).</summary>
    [Theory]
    [InlineData(DamageKind.MobMelee, false)]
    [InlineData(DamageKind.PlayerMelee, false)]
    [InlineData(DamageKind.MobSpell, true)]
    [InlineData(DamageKind.PlayerSpell, true)]
    public void UnderInvincibilitySwingsDoNotLandAndSpellsDo(DamageKind kind, bool lands)
    {
        var (victim, _, c) = Player(WardKindMap, CX, CY, hp: 100_000);
        var (peer, _, _) = Player(WardKindMap, CX, CY + 4);
        var mob = AddCreature(WardKindMap, CX + 1, CY);
        try
        {
            victim.ItemSetStatus("invincibility", 60_000);
            Strike(victim, kind, peer, mob);
            Assert.True((c.Hp < 100_000) == lands, $"{kind} under Invincibility: vita {c.Hp} of 100000 (want {(lands ? "less" : "all")})");
        }
        finally { _fx.World.DespawnMob(WardKindMap, mob); Leave(WardKindMap, victim, peer); }
    }

    /// <summary>Harden Body is unchanged by the split: under the poet's Harden Body every one of those four blows, a creature's
    /// spell and another player's spell included, deals nothing, as on master. Red with the gate letting a spell past the
    /// Harden Body wards.</summary>
    [Theory]
    [InlineData(DamageKind.MobMelee)]
    [InlineData(DamageKind.PlayerMelee)]
    [InlineData(DamageKind.MobSpell)]
    [InlineData(DamageKind.PlayerSpell)]
    public void HardenBodyStillStopsSwingsAndSpells(DamageKind kind)
    {
        var (victim, _, c) = Player(WardKindMap, CX + 10, CY, hp: 100_000);
        var (peer, _, _) = Player(WardKindMap, CX + 10, CY + 4);
        var mob = AddCreature(WardKindMap, CX + 11, CY);
        try
        {
            victim.ItemSetStatus("harden_body_poet", 60_000);
            Strike(victim, kind, peer, mob);
            Assert.True(c.Hp == 100_000, $"{kind} under Harden Body: vita {c.Hp} of 100000");
        }
        finally { _fx.World.DespawnMob(WardKindMap, mob); Leave(WardKindMap, victim, peer); }
    }

    /// <summary>When the 3 s ward has run out, the caster reads "Your skin turns back to flesh." (RTK's uncast,
    /// <c>invincibility.lua:53-55</c>) once, on the heartbeat, which wakes for it by itself (no regen due); before then the
    /// heartbeat says nothing, and after, nothing again. The cast schedules the heartbeat's wake for the ward's end, about
    /// 3 s on. Red without the line in the verb, without the heartbeat saying it, without the heartbeat waking for it, or
    /// without the cast scheduling that wake. (#420's review, F3; the re-check's F7.)</summary>
    [Fact]
    public void InvincibilityEndsWithItsLine()
    {
        var (caster, cOut, _) = Caster("invincibility", InvEndMap, CX, CY, configure: ch => ch.Ac = -100);
        try
        {
            Assert.True(LandInvincibility(caster, cOut), "no Invincibility cast landed in forty");
            long wake = 0;
            caster.WithState(() => wake = (long)DurationLineDueField.GetValue(caster)! - Environment.TickCount64);
            cOut.Clear();
            caster.RegenTick(0, regenDue: true);
            string before = Lines(cOut);
            caster.WithState(() =>
            {
                ((Dictionary<string, long>)StatusFlagsField.GetValue(caster)!)["invincibility"] = Environment.TickCount64 - 1;
                DurationLineDueField.SetValue(caster, Environment.TickCount64 - 1);
            });
            caster.RegenTick(0, regenDue: false);
            string ended = Lines(cOut);
            cOut.Clear();
            caster.RegenTick(0, regenDue: true);
            Assert.True(wake > 3_000 - SlackMs && wake <= 3_000 && before == "" && ended == "Your skin turns back to flesh."
                        && Lines(cOut) == "" && !caster.HardBodyWarded,
                        $"wake in {wake} ms (want about 3,000), before [{before}], at its end [{ended}], after [{Lines(cOut)}], " +
                        $"ward {caster.HardBodyWarded}");
        }
        finally { Leave(InvEndMap, caster); }
    }

    /// <summary>A ward cleared early (@dispel here; death takes the same path, <c>ClearAllTimedEffects</c>) says "Your skin
    /// turns back to flesh." at the clear, on the next heartbeat with nothing forced, as RTK's pc_diescript runs the uncast;
    /// once, not again at the ward's own end. Red without the clear waking the heartbeat: the line would wait for the ward's
    /// original 3 s end. (The re-check's F8.)</summary>
    [Fact]
    public void AClearedWardSaysItsLineAtTheClear()
    {
        var (caster, cOut, _) = Caster("invincibility", InvClearMap, CX, CY, configure: ch => ch.Ac = -100);
        try
        {
            Assert.True(LandInvincibility(caster, cOut), "no Invincibility cast landed in forty");
            cOut.Clear();
            caster.WithState(() => caster.DispelSelf());
            bool wardAfterClear = caster.HardBodyWarded;
            caster.RegenTick(0, regenDue: false);
            string atClear = Lines(cOut);
            cOut.Clear();
            caster.RegenTick(0, regenDue: true);
            Assert.True(!wardAfterClear && atClear == "Your skin turns back to flesh." && Lines(cOut) == "",
                        $"ward after the clear {wardAfterClear}, at the clear [{atClear}], after [{Lines(cOut)}]");
        }
        finally { Leave(InvClearMap, caster); }
    }

    // ===== Protectors fury ======================================================================================

    /// <summary>For 1,000, rage x3 for 250 s, "You cast Protectors fury.", graphic 11 and sound 4; no wait. A second cast
    /// while it runs, "This spell is already active."; short of mana with a fury running, "You do not have enough mana."
    /// (the script asks the mana first); both free. Red on 5feb96a: generic, no rage.</summary>
    [Fact]
    public void ProtectorsFuryTriplesTheSwingFor250Seconds()
    {
        var (caster, cOut, cc) = Caster("protectors_fury", FuryMap, CX, CY);
        var (poor, pOut, pc) = Caster("protectors_fury", FuryMap, CX, CY + 4, mana: 999);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            var (amount, left) = SpellCastSupport.Rage(caster);
            string first = Lines(cOut);
            bool fx = Effects(cOut, caster.PlayerId).Contains(11) && Sounds(cOut).Contains(4);
            uint after = cc.Mp;
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            poor.WithState(() => poor.LuaSetRage(5, 60_000, "Some fury"));
            poor.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(first == "You cast Protectors fury." && amount == 3 && left > 250_000 - SlackMs && left <= 250_000 && fx
                        && Mana - after == 1_000 && SpellCastSupport.AetherLeft(caster, "protectors_fury") == 0
                        && Lines(cOut) == "This spell is already active." && cc.Mp == after
                        && Lines(pOut) == "You do not have enough mana." && pc.Mp == 999,
                        $"first [{first}] rage {amount} for {left} ms, fx {fx}, spent {Mana - after}; again [{Lines(cOut)}]; " +
                        $"short [{Lines(pOut)}] mana {pc.Mp}");
        }
        finally { Leave(FuryMap, caster, poor); }
    }

    // ===== Harden weapon, Charge weapon, Sharpen weapon =========================================================

    /// <summary>Armed, "&lt;weapon&gt; shines with holy light." (the weapon's Items.csv name), 60 spent and the weapon
    /// enchant x6, x8 or x4; while an enchant runs, "This spell is already active."; short of 60, "You do not have enough
    /// mana."; unarmed, nothing happens and nothing is spent (the script reads the missing weapon's name and stops). Red on
    /// 5feb96a (generic); red with the unarmed stop taken out.</summary>
    [Theory]
    [InlineData("harden_weapon", 6)]
    [InlineData("charge_weapon", 8)]
    [InlineData("sharpen_weapon", 4)]
    public void EachWeaponNamesTheWieldedWeaponAndArmsItsEnchant(string key, int mult)
    {
        int n = key switch { "harden_weapon" => 0, "charge_weapon" => 1, _ => 2 };
        ushort y = (ushort)(CY + 6 * n);
        var sword = Content.ItemByKey("novice_sword")!;
        void Arm(Character ch) => ch.Equipment.Add(new InvItem { Slot = sword.EquipSlot, ItemId = sword.Id, Dura = sword.Durability });
        var (caster, cOut, cc) = Caster(key, WeaponMap, CX, y, configure: Arm);
        var (bare, bOut, bc) = Caster(key, WeaponMap, CX + 4, y);
        var (poor, pOut, pc) = Caster(key, WeaponMap, CX + 8, y, mana: 59, configure: Arm);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string first = Lines(cOut);
            double enchant = SpellCastSupport.Enchant(caster);
            uint after = cc.Mp;
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            bare.Receive(SpellCastSupport.CastFrame(0));
            poor.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(first == $"{sword.Name} shines with holy light." && enchant == mult && Mana - after == 60
                        && Lines(cOut) == "This spell is already active." && cc.Mp == after
                        && Lines(bOut) == "" && bc.Mp == Mana && SpellCastSupport.Enchant(bare) == 1
                        && Lines(pOut) == "You do not have enough mana." && pc.Mp == 59,
                        $"armed [{first}] x{enchant} spent {Mana - after}; again [{Lines(cOut)}]; unarmed [{Lines(bOut)}] spent " +
                        $"{Mana - bc.Mp} x{SpellCastSupport.Enchant(bare)}; short [{Lines(pOut)}] mana {pc.Mp}");
        }
        finally { Leave(WeaponMap, caster, bare, poor); }
    }

    // ===== Enhanced drink =======================================================================================

    /// <summary>The typed answer, lowered: "water jug" with a Water jug in the bag, "Red fog spreads around Water jug.";
    /// without one, "Oh! if only you had that."; anything else, "You can't imbibe that.". Every answer arms the 112 s wait,
    /// and no mana is asked or spent. Red on 5feb96a: generic's 5 mana and "You cast Enhanced drink.".</summary>
    [Theory]
    [InlineData("water jug", true, "Red fog spreads around Water jug.")]
    [InlineData("Water Jug", true, "Red fog spreads around Water jug.")]
    [InlineData("water jug", false, "Oh! if only you had that.")]
    [InlineData("wine", true, "You can't imbibe that.")]
    public void EnhancedDrinkSaysItsLineAndArmsItsWait(string answer, bool jug, string expected)
    {
        var item = Content.ItemByKey("water_jug")!;
        var (caster, cOut, cc) = Caster("enhanced_drink", DrinkMap, CX + 4 * Interlocked.Increment(ref _serial) % 60, CY, configure: ch =>
        {
            if (jug) ch.Inventory.Add(new InvItem { Slot = 0, ItemId = item.Id, Amount = 1 });
        });
        try
        {
            caster.Receive(AnswerFrame(0, answer));
            long wait = SpellCastSupport.AetherLeft(caster, "enhanced_drink");
            Assert.True(Lines(cOut) == expected && cc.Mp == Mana && wait > 112_000 - SlackMs && wait <= 112_000,
                        $"[{Lines(cOut)}], spent {Mana - cc.Mp}, wait {wait} ms");
        }
        finally { Leave(DrinkMap, caster); }
    }

    /// <summary>Inside its wait, "Enhanced drink isn't ready yet.", and nothing else. Red on 5feb96a.</summary>
    [Fact]
    public void EnhancedDrinkWaitsItsTime()
    {
        var (caster, cOut, _) = Caster("enhanced_drink", DrinkMap, CX, CY + 10);
        try
        {
            caster.Receive(AnswerFrame(0, "wine"));
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(AnswerFrame(0, "water jug"));
            Assert.Equal("Enhanced drink isn't ready yet.", Lines(cOut));
        }
        finally { Leave(DrinkMap, caster); }
    }

    // ===== Chongun uniform ======================================================================================

    /// <summary>For 25, look 37 for 162 s in the caster's war paint (colour 5 here), with graphic 3 and "You cast Chongun
    /// uniform."; cast again while worn, it ends, silently and for free, with no exit sound. Red on 5feb96a (generic); red
    /// with the morph's own colour 0; red with the morph's exit sound 411.</summary>
    [Fact]
    public void TheUniformWearsLookThirtySevenInTheWarPaintAndEndsWhenCastAgain()
    {
        var (caster, cOut, cc) = Caster("chongun_uniform", UniformMap, CX, CY, configure: ch => ch.ArmorColor = 5);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            var (look, color, left) = Morph(caster);
            string first = Lines(cOut);
            bool graphic = Effects(cOut, caster.PlayerId).Contains(3);
            uint after = cc.Mp;
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            var (offLook, _, _) = Morph(caster);
            Assert.True(look == 37 && color == 5 && left > 162_000 - SlackMs && left <= 162_000 && graphic
                        && first == "You cast Chongun uniform." && Mana - after == 25
                        && offLook == 0 && Lines(cOut) == "" && cc.Mp == after && !Sounds(cOut).Contains(411),
                        $"look {look} c{color} {left} ms, graphic {graphic}, [{first}], spent {Mana - after}; " +
                        $"again: look {offLook}, [{Lines(cOut)}], spent {after - cc.Mp}, sounds [{string.Join(",", Sounds(cOut))}]");
        }
        finally { Leave(UniformMap, caster); }
    }

    /// <summary>At its time the uniform ends on the world heartbeat with nothing played (its uncast plays nothing). Red with
    /// the morph's exit sound 411.</summary>
    [Fact]
    public void TheUniformEndsAtItsTimeWithNoSound()
    {
        var (caster, cOut, _) = Caster("chongun_uniform", UniformEndMap, CX, CY);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.Equal(37, Morph(caster).Look);
            cOut.Clear();
            caster.WithState(() => MorphUntilField.SetValue(caster, Environment.TickCount64 - 1));
            _fx.World.TickOnceForTest();
            Assert.True(SpinWait.SpinUntil(() => Morph(caster).Look == 0, 5_000), "the uniform did not end at its time");
            Assert.False(Sounds(cOut).Contains(411), $"sounds [{string.Join(",", Sounds(cOut))}]");
        }
        finally { Leave(UniformEndMap, caster); }
    }

    /// <summary>Invisible, "You cannot cast Chongun Uniform while invisible."; short of 25, "Your will is too weak."; both
    /// free and no look. Red on 5feb96a.</summary>
    [Fact]
    public void TheUniformRefusesWhileInvisibleAndShortOfMana()
    {
        var (hidden, hOut, hc) = Caster("chongun_uniform", UniformMap, CX, CY + 6);
        var (poor, pOut, pc) = Caster("chongun_uniform", UniformMap, CX, CY + 10, mana: 24);
        try
        {
            hidden.WithState(() => hidden.LuaHide(Content.SpellByKey("blend")!, 60_000, 9));
            hidden.Receive(SpellCastSupport.CastFrame(0));
            poor.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(hOut) == "You cannot cast Chongun Uniform while invisible." && hc.Mp == Mana && Morph(hidden).Look == 0
                        && Lines(pOut) == "Your will is too weak." && pc.Mp == 24 && Morph(poor).Look == 0,
                        $"invisible [{Lines(hOut)}] spent {Mana - hc.Mp}; short [{Lines(pOut)}] mana {pc.Mp}");
        }
        finally { Leave(UniformMap, hidden, poor); }
    }

    // ===== Employ troop =========================================================================================

    /// <summary>Sound 22, "You cast Employ troop.", 1,000 and the 600 s wait, all before RTK's menu, "Summon what?" with
    /// Scout, Spearman and Swordsman at rank 0 (and Master at rank 2). The pick puts that troop on the faced tile, owned by
    /// the caster for 600 s, and "You summon a &lt;troop&gt;". Red on 5feb96a: generic, no menu.</summary>
    [Theory]
    [InlineData(0, 3, "employ_troop_swordsman")]
    [InlineData(2, 4, "employ_troop_master")]
    public void EmployTroopOffersRtksMenuAndPutsThePickedTroopOnTheFacedTile(int mark, int pick, string troop)
    {
        ushort y = (ushort)(CY + 4 * mark);
        var (caster, cOut, cc) = Caster("employ_troop", TroopMap, CX, y, configure: ch => ch.Mark = (byte)mark);
        Mob? made = null;
        try
        {
            string menu = "", first = "", summoned = "";
            int[] sounds = Array.Empty<int>();
            long wait = 0;
            uint spent = 0;
            Bare(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                first = Lines(cOut);
                sounds = Sounds(cOut).ToArray();
                menu = Menus(cOut).SingleOrDefault() ?? "";
                wait = SpellCastSupport.AetherLeft(caster, "employ_troop");
                spent = Mana - cc.Mp;
                cOut.Clear();
                caster.Receive(MenuFrame((byte)pick));
                summoned = Lines(cOut);
            });
            made = _fx.World.MobAt(TroopMap, CX + 1, y);
            string want = "Summon what?|Scout|Spearman|Swordsman" + (mark >= 2 ? "|Master" : "");
            var def = Content.MobByKey(troop)!;
            long life = made is null ? 0 : made.PetExpiresAt - Environment.TickCount64;
            Assert.True(first == "You cast Employ troop." && sounds.Contains(22) && menu == want && spent == 1_000
                        && wait > 600_000 - SlackMs && wait <= 600_000
                        && made is { Key: var k } && k == troop && made.OwnerId == caster.PlayerId && made.Summoned
                        && life > 600_000 - SlackMs && life <= 600_000 && summoned == $"You summon a {def.Name}",
                        $"[{first}], sounds [{string.Join(",", sounds)}], menu [{menu}], spent {spent}, wait {wait}; " +
                        $"faced: {made?.Key ?? "nothing"} owner {made?.OwnerId} life {life}; [{summoned}]");
        }
        finally
        {
            if (made is not null) _fx.World.DespawnMob(TroopMap, made);
            Leave(TroopMap, caster);
        }
    }

    /// <summary>RTK's cotw test: a creature or a player (a ghost too) on the faced tile sends the troop to the caster's own
    /// tile; an NPC there does not (RTK asks for BL_MOB and BL_PC, and its NPCs are BL_NPC). Red with every creature
    /// blocking (the NPC case) or with players not looked at (the player and ghost cases).</summary>
    [Theory]
    [InlineData("creature", false)]
    [InlineData("player", false)]
    [InlineData("ghost", false)]
    [InlineData("npc", true)]
    public void ABlockedFacedTileSendsTheTroopToTheCastersTile(string blocker, bool front)
    {
        int row = blocker switch { "creature" => 0, "player" => 1, "ghost" => 2, _ => 3 };
        ushort y = (ushort)(CY + 4 * row);
        var (caster, cOut, _) = Caster("employ_troop", TroopBlockMap, CX, y);
        Session? other = null;
        Mob? block = null;
        if (blocker == "creature") block = AddCreature(TroopBlockMap, CX + 1, y);
        if (blocker == "npc") block = AddCreature(TroopBlockMap, CX + 1, y, npc: true);
        if (blocker == "player") (other, _, _) = Player(TroopBlockMap, CX + 1, y);
        if (blocker == "ghost") (other, _, _) = Player(TroopBlockMap, CX + 1, y, hp: 0);
        var made = new List<Mob>();
        try
        {
            Bare(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                caster.Receive(MenuFrame(1));
            });
            made.AddRange(_fx.World.MobsAt(TroopBlockMap, front ? CX + 1 : CX, y).Where(m => m.Key == "employ_troop_scout"));
            var elsewhere = _fx.World.MobsAt(TroopBlockMap, front ? CX : CX + 1, y).Where(m => m.Key == "employ_troop_scout").ToList();
            made.AddRange(elsewhere);
            Assert.True(made.Count == 1 && elsewhere.Count == 0,
                        $"{blocker}: {made.Count - elsewhere.Count} scout(s) on the {(front ? "faced" : "caster's")} tile, {elsewhere.Count} on the other; [{Lines(cOut)}]");
        }
        finally
        {
            foreach (var m in made) _fx.World.DespawnMob(TroopBlockMap, m);
            if (block is not null) _fx.World.DespawnMob(TroopBlockMap, block);
            Leave(TroopBlockMap, caster);
            if (other is not null) Leave(TroopBlockMap, other);
        }
    }

    /// <summary>RTK's <c>getpass</c> answers 1 past the map's last column (<c>rtk/src/map/sl.c:683-695</c>), so a caster on
    /// the last column facing east puts the troop on their own tile. Red without the bounds check in
    /// <c>Session.CotwTileOpen</c>. (#420's review, F4.)</summary>
    [Fact]
    public void FacingTheMapsEdgeTheTroopStaysOnTheCastersTile()
    {
        var (caster, _, cc) = Caster("employ_troop", TroopEdgeMap, 99, CY);   // a content-free map is 100 wide here
        var made = new List<Mob>();
        try
        {
            Assert.Equal(100, cc.MapXs);
            Bare(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                caster.Receive(MenuFrame(1));
            });
            var home = _fx.World.MobsAt(TroopEdgeMap, 99, CY).Where(m => m.Key == "employ_troop_scout").ToList();
            var past = _fx.World.MobsAt(TroopEdgeMap, 100, CY).Where(m => m.Key == "employ_troop_scout").ToList();
            made.AddRange(home);
            made.AddRange(past);
            Assert.True(home.Count == 1 && past.Count == 0, $"{home.Count} on the caster's tile, {past.Count} past the edge");
        }
        finally
        {
            foreach (var m in made) _fx.World.DespawnMob(TroopEdgeMap, m);
            Leave(TroopEdgeMap, caster);
        }
    }

    /// <summary>On a map with terrain, a faced tile <c>getPass</c> calls blocked sends the troop to the caster's own tile,
    /// and an open one takes it. Red without the blocked-ground check in <c>Session.CotwTileOpen</c>. (#420's review,
    /// F4.)</summary>
    [Fact]
    public void FacingBlockedGroundTheTroopStaysOnTheCastersTile()
    {
        var md = MapData.For(GuideMap)!;
        bool Open(int x, int y) => md.Pass(x, y) == 0 && !Content.TryWarp(GuideMap, (ushort)x, (ushort)y, out _);
        (int X, int Y) blocked = (-1, -1), open = (-1, -1);
        for (int y = 2; y < md.Ys - 2 && (blocked.X < 0 || open.X < 0); y++)
            for (int x = 2; x < md.Xs - 3; x++)
            {
                if (!Open(x, y)) continue;
                if (blocked.X < 0 && md.Pass(x + 1, y) != 0) blocked = (x, y);
                else if (open.X < 0 && Open(x + 1, y) && (Math.Abs(x - blocked.X) > 3 || Math.Abs(y - blocked.Y) > 3)) open = (x, y);
            }
        Assert.True(blocked.X >= 0 && open.X >= 0, $"map {GuideMap} has no blocked-facing tile {blocked} or open-facing tile {open}");
        var (bCaster, _, _) = Caster("employ_troop", GuideMap, blocked.X, blocked.Y);
        var (oCaster, _, _) = Caster("employ_troop", GuideMap, open.X, open.Y);
        var made = new List<Mob>();
        try
        {
            Bare(() =>
            {
                bCaster.Receive(SpellCastSupport.CastFrame(0));
                bCaster.Receive(MenuFrame(1));
                oCaster.Receive(SpellCastSupport.CastFrame(0));
                oCaster.Receive(MenuFrame(1));
            });
            List<Mob> At((int X, int Y) t) => _fx.World.MobsAt(GuideMap, t.X, t.Y).Where(m => m.Key == "employ_troop_scout").ToList();
            var bHome = At(blocked);
            var bFront = At((blocked.X + 1, blocked.Y));
            var oFront = At((open.X + 1, open.Y));
            var oHome = At(open);
            made.AddRange(bHome); made.AddRange(bFront); made.AddRange(oFront); made.AddRange(oHome);
            Assert.True(bHome.Count == 1 && bFront.Count == 0 && oFront.Count == 1 && oHome.Count == 0,
                        $"facing blocked {blocked}: {bHome.Count} home, {bFront.Count} on the blocked tile; " +
                        $"facing open {open}: {oFront.Count} in front, {oHome.Count} home");
        }
        finally
        {
            foreach (var m in made) _fx.World.DespawnMob(GuideMap, m);
            Leave(GuideMap, bCaster, oCaster);
        }
    }

    /// <summary>Short of 1,000, "Your will is too weak."; inside its wait, "Employ troop isn't ready yet."; both free and
    /// with no menu. Red on 5feb96a.</summary>
    [Fact]
    public void EmployTroopRefusesShortOfManaAndInsideItsWait()
    {
        var (caster, cOut, cc) = Caster("employ_troop", TroopRefuseMap, CX, CY);
        var (poor, pOut, pc) = Caster("employ_troop", TroopRefuseMap, CX, CY + 4, mana: 999);
        try
        {
            uint after = 0;
            Bare(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                after = cc.Mp;
                cOut.Clear();
                FreeSlot(caster);
                caster.Receive(SpellCastSupport.CastFrame(0));
                poor.Receive(SpellCastSupport.CastFrame(0));
            });
            Assert.True(Lines(cOut) == "Employ troop isn't ready yet." && Menus(cOut).Count == 0 && cc.Mp == after
                        && Lines(pOut) == "Your will is too weak." && Menus(pOut).Count == 0 && pc.Mp == 999,
                        $"again [{Lines(cOut)}] menus {Menus(cOut).Count}; short [{Lines(pOut)}] menus {Menus(pOut).Count} mana {pc.Mp}");
        }
        finally { Leave(TroopRefuseMap, caster, poor); }
    }

    // ===== Title of nobility ====================================================================================

    /// <summary>The cast asks a name; the name, typed in any case, finds a player on another map and asks a title; the
    /// title becomes that player's (saved with them: marked for their next save), they read in blue "Chongun &lt;caster&gt;
    /// has bestowed upon you a new title.", the caster reads "It is done.", and the four-hour wait is kept in the saved
    /// registry (<c>title_of_nobility_timer</c>, a Unix time). No mana, no aether, no "You cast" line. Red on 5feb96a
    /// (generic); red with the typed name not carried to the title's step.</summary>
    [Fact]
    public void TitleOfNobilityAsksANameThenATitleAndGivesIt()
    {
        var (target, tOut, tc) = Player(TitleFarMap, CX, CY, name: Letters("Noble"));
        var (caster, cOut, cc) = Caster("title_of_nobility", TitleMap, CX, CY, name: Letters("Chongun"));
        try
        {
            List<Box> asked = new(), second = new();
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                asked = Boxes(cOut);
                cOut.Clear();
                caster.Receive(InputFrame(target.CharName.ToLowerInvariant()));
                second = Boxes(cOut);
                cOut.Clear();
                tOut.Clear();
                caster.Receive(InputFrame("Lord of Swords"));
            });
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int timer = QuestState.Over(cc, QuestState.Registry).Get("title_of_nobility_timer");
            bool dirty = false;
            target.WithState(() => dirty = (bool)DirtyField.GetValue(target)!);
            var blue = Typed(tOut, 0);
            Assert.True(asked is [{ Kind: 0x0404, Text: "Whom would you like to bestow a title?" }]
                        && second is [{ Kind: 0x0404, Text: "What would you choose the title to be?" }]
                        && tc.Title == "Lord of Swords" && dirty
                        && blue.SequenceEqual(new[] { $"Chongun {caster.CharName} has bestowed upon you a new title." })
                        && Lines(cOut) == "It is done." && Math.Abs(timer - (now + 14_400)) <= 5 && cc.Mp == Mana
                        && SpellCastSupport.AetherLeft(caster, "title_of_nobility") == 0,
                        $"asked [{string.Join(" / ", asked)}], then [{string.Join(" / ", second)}]; title '{tc.Title}' dirty {dirty}; " +
                        $"blue [{string.Join(" | ", blue)}]; caster [{Lines(cOut)}]; timer {timer} (now {now}); spent {Mana - cc.Mp}");
        }
        finally { Leave(TitleFarMap, target); Leave(TitleMap, caster); }
    }

    /// <summary>While the saved wait runs, the cast says in blue "You must wait 00 days 00 hours 59 minutes 59 seconds
    /// before giving another title." and asks nothing; once it has passed, the cast asks a name again. Red on 5feb96a.</summary>
    [Fact]
    public void ASecondTitleWaitsForTheSavedTimer()
    {
        var (caster, cOut, _) = Caster("title_of_nobility", TitleWaitMap, CX, CY, name: Letters("Waiter"));
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            caster.WithState(() => caster.Quest(QuestState.Registry).Set("title_of_nobility_timer", (int)(now + 3_599)));
            List<Box> waiting = new(), after = new();
            List<string> blue = new();
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                waiting = Boxes(cOut);
                blue = Typed(cOut, 0);
                cOut.Clear();
                caster.WithState(() => caster.Quest(QuestState.Registry).Set("title_of_nobility_timer", (int)(now - 1)));
                FreeSlot(caster);
                caster.Receive(SpellCastSupport.CastFrame(0));
                after = Boxes(cOut);
            });
            Assert.True(waiting.Count == 0 && blue.Count == 1
                        && Regex.IsMatch(blue[0], @"^You must wait 00 days 00 hours 59 minutes 5\d seconds before giving another title\.$")
                        && after is [{ Kind: 0x0404, Text: "Whom would you like to bestow a title?" }],
                        $"waiting: boxes {waiting.Count}, blue [{string.Join(" | ", blue)}]; after [{string.Join(" / ", after)}]");
        }
        finally { Leave(TitleWaitMap, caster); }
    }

    /// <summary>A name with anything but letters, spaces and an apostrophe, the box "You can only enter letters and
    /// spaces."; nobody by that name, "That player is either not online or unable to be found."; a title over 16
    /// characters, the box "Your entered title must be no greater than 16 characters."; a title with a digit, the letters
    /// box. None gives a title or sets the wait. Red on 5feb96a.</summary>
    [Theory]
    [InlineData("name digit")]
    [InlineData("nobody")]
    [InlineData("long title")]
    [InlineData("title digit")]
    public void TitleOfNobilityRefusals(string refusal)
    {
        var (target, _, tc) = Player(TitleRefuseMap, CX + 10, CY, name: Letters("Untitled"));
        var (caster, cOut, cc) = Caster("title_of_nobility", TitleRefuseMap, CX, CY, name: Letters("Asker"));
        try
        {
            string name = refusal switch { "name digit" => "Bad1", "nobody" => Letters("Nobody"), _ => target.CharName };
            string title = refusal == "long title" ? "Seventeen Letters" : "Duke 9";
            List<Box> boxes = new();
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                cOut.Clear();
                caster.Receive(InputFrame(name));
                if (refusal is "long title" or "title digit")
                {
                    cOut.Clear();
                    caster.Receive(InputFrame(title));
                }
                boxes = Boxes(cOut);
            });
            string want = refusal switch
            {
                "nobody" => "",
                "long title" => "Your entered title must be no greater than 16 characters.",
                _ => "You can only enter letters and spaces.",
            };
            bool ok = refusal == "nobody"
                ? Lines(cOut) == "That player is either not online or unable to be found." && boxes.Count == 0
                : boxes is [{ Kind: 0x0001 } b] && b.Text == want && Lines(cOut) == "";
            int timer = QuestState.Over(cc, QuestState.Registry).Get("title_of_nobility_timer");
            Assert.True(ok && tc.Title == "" && timer == 0,
                        $"{refusal}: boxes [{string.Join(" / ", boxes)}], lines [{Lines(cOut)}], title '{tc.Title}', timer {timer}");
        }
        finally { Leave(TitleRefuseMap, target, caster); }
    }

    /// <summary>The title is written inside the named player's own monitor: while a third thread holds it, the step that
    /// gives the title waits and the title is untouched, in both <c>StateRank</c> orders. Red with
    /// <c>Session.ReceiveTitle</c> outside the player's monitor (a Release build).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheTitleIsWrittenInsideTheNamedPlayersOwnMonitor(bool casterRanksFirst)
    {
        Session caster, target;
        RecordingOutbound cOut;
        Character tc;
        if (casterRanksFirst)
        {
            (caster, cOut, _) = Caster("title_of_nobility", TitleHeldMap, CX, CY, name: Letters("Giver"));
            (target, _, tc) = Player(TitleHeldMap, CX + 5, CY, name: Letters("Taker"));
        }
        else
        {
            (target, _, tc) = Player(TitleHeldMap, CX + 5, CY, name: Letters("Taker"));
            (caster, cOut, _) = Caster("title_of_nobility", TitleHeldMap, CX, CY, name: Letters("Giver"));
        }
        Assert.Equal(casterRanksFirst, caster.StateRank < target.StateRank);
        var run = new HeldRun();
        try
        {
            // The first two steps on a thread of their own; the third, which writes the title, while the target is held.
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                caster.Receive(InputFrame(target.CharName));
            });
            WhileAnotherThreadHolds(run, target, () => caster.Receive(InputFrame("Marquis")), untouched: () => tc.Title == "");
            Assert.Null(run.HolderFault);
            Assert.Null(run.ActorFault);
            Assert.False(run.FinishedWhileHeld, "the title's step finished while another thread held the player's monitor");
            Assert.True(run.UntouchedWhileHeld, "the title was written while another thread held the player's monitor");
            Assert.True(tc.Title == "Marquis" && Lines(cOut).EndsWith("It is done."), $"after: title '{tc.Title}', [{Lines(cOut)}]");
        }
        finally { if (run.Settled) Leave(TitleHeldMap, caster, target); }
    }

    /// <summary>A session a second login has replaced takes no title: its save is fenced, so a title written there would
    /// be lost while the caster took the four-hour wait. <c>Session.ReceiveTitle</c> answers false and changes nothing, so
    /// the step says no "It is done." and sets no wait. The name lookup already passes over a replaced session; this is the
    /// guard for one replaced between the lookup and the write. Red without the <c>IsReplaced</c> guard. (#420's review,
    /// F6.)</summary>
    [Fact]
    public void AReplacedSessionTakesNoTitle()
    {
        var (target, tOut, tc) = Player(TitleReplacedMap, CX, CY, name: Letters("Replaced"));
        try
        {
            ReplacedField.SetValue(target, 1);
            Assert.True(target.IsReplaced);
            bool landed = target.ReceiveTitle("Duke", "Chongun Someone has bestowed upon you a new title.");
            Assert.True(!landed && tc.Title == "" && Typed(tOut, 0).Count == 0,
                        $"landed {landed}, title '{tc.Title}', blue [{string.Join(" | ", Typed(tOut, 0))}]");
        }
        finally
        {
            ReplacedField.SetValue(target, 0);
            Leave(TitleReplacedMap, target);
        }
    }

    // ===== Spirit of war ========================================================================================

    /// <summary>Six casts climb RTK's copy of Chung ryong rage: rage 6/9/12/18/27/50 for 3,000/10,000/22,000/38,000/
    /// 78,000/150,000 mana, AC 0 then 5 worse a tier, "[Rage N] You cast Spirit of war." at each, one 938 s run armed by
    /// the first, a 115 s wait, graphic 106; a seventh at the top does nothing, silently and for free. Red on 5feb96a
    /// (generic); red without RAGE_LINES.spirit_of_war; red without the effect row (no 115 s wait).</summary>
    [Fact]
    public void SpiritOfWarClimbsSixTiers()
    {
        var (caster, cOut, cc) = Caster("spirit_of_war", WarMap, CX, CY, mana: 400_000, maxMana: 400_000);
        int[] mults = { 6, 9, 12, 18, 27, 50 }, mana = { 3_000, 10_000, 22_000, 38_000, 78_000, 150_000 };
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
                long wait = SpellCastSupport.AetherLeft(caster, "spirit_of_war");
                int armor = caster.ItemArmor - baseArmor;
                if (tier == 1) firstEnd = Environment.TickCount64 + left;
                long end = Environment.TickCount64 + left;
                Assert.True(lines.SequenceEqual(new[] { $"[Rage {tier}] You cast Spirit of war." }) && amount == mults[tier - 1]
                            && before - cc.Mp == mana[tier - 1] && armor == 5 * (tier - 1) && wait > 115_000 - SlackMs && wait <= 115_000
                            && left > 938_000 - SlackMs * 2 && Math.Abs(end - firstEnd) < SlackMs
                            && Effects(cOut, caster.PlayerId).Contains(106),
                            $"tier {tier}: [{string.Join(" | ", lines)}], rage {amount}, spent {before - cc.Mp}, armor {armor}, " +
                            $"wait {wait} ms, run left {left} ms, graphics [{string.Join(",", Effects(cOut, caster.PlayerId))}]");
                SpellCastSupport.ClearAether(caster, "spirit_of_war");
            }
            uint top = cc.Mp;
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(SpellCastSupport.MiniTexts(cOut).Count == 0 && cc.Mp == top && SpellCastSupport.Rage(caster).Amount == 50,
                        $"at the top: [{Lines(cOut)}], spent {top - cc.Mp}");
        }
        finally { Leave(WarMap, caster); }
    }

    /// <summary>A Spirit of war climb that lapses at tier 6 keeps a tenth of its vita (RTK's uncast, <c>health * .1</c>),
    /// takes no mana and says nothing: the rtk_rage ladder's price, found by the fury's name. Red on 5feb96a.</summary>
    [Fact]
    public void ALapsedSpiritOfWarKeepsATenthOfItsVitaAtTierSix()
    {
        var (caster, cOut, cc) = Caster("spirit_of_war", WarMap, CX, CY + 6, mana: 400_000, maxMana: 400_000);
        try
        {
            for (int i = 0; i < 6; i++)
            {
                FreeSlot(caster);
                caster.Receive(SpellCastSupport.CastFrame(0));
                SpellCastSupport.ClearAether(caster, "spirit_of_war");
            }
            Assert.Equal(6, caster.LuaCrRageTier);
            uint mana = cc.Mp;
            cOut.Clear();
            caster.WithState(() => RageUntilField.SetValue(caster, Environment.TickCount64 - 1));
            caster.RegenTick(0, regenDue: false);
            Assert.True(cc.Hp == 1_000 && cc.Mp == mana && SpellCastSupport.MiniTexts(cOut).Count == 0 && caster.LuaCrRageTier == 0,
                        $"vita {cc.Hp} (want 1000), mana {mana} -> {cc.Mp}, lines [{Lines(cOut)}]");
        }
        finally { Leave(WarMap, caster); }
    }

    // ===== the teacher ==========================================================================================

    /// <summary>The Chongun teacher's list by rank, each at its script's level 99: nine from mark 0, Harden weapon from mark 1
    /// and Charge weapon from mark 2, their rows' rank marks. Employ troop and Title of nobility, whose price names an item
    /// no table has, and Recognize honorable act, which has no script, are not on sale. Red without the price rows: an
    /// empty list.</summary>
    [Theory]
    [InlineData(0, "art_of_war chongun_uniform enhanced_drink evaluate_opponent inspire_valor invincibility protectors_fury sharpen_weapon spirit_of_war")]
    [InlineData(1, "art_of_war chongun_uniform enhanced_drink evaluate_opponent harden_weapon inspire_valor invincibility protectors_fury sharpen_weapon spirit_of_war")]
    [InlineData(2, "art_of_war charge_weapon chongun_uniform enhanced_drink evaluate_opponent harden_weapon inspire_valor invincibility protectors_fury sharpen_weapon spirit_of_war")]
    public void TheChongunTeacherSellsByRank(int mark, string expected)
    {
        int path = Content.PathIdForClass("Chongun");
        var sold = Content.SubpathTeacherSpells(path, 99, mark);
        Assert.Equal(expected, string.Join(" ", sold.Select(s => s.Key).OrderBy(k => k, StringComparer.Ordinal)));
        Assert.All(sold, s => Assert.Equal(99, s.Level));
        foreach (var key in new[] { "employ_troop", "title_of_nobility", "recognize_honorable_act" })
            Assert.Null(Content.LearnCostFor(Content.SpellByKey(key)!, path));
    }

    // ===== helpers ==============================================================================================

    /// <summary>One 0x30 dialog box (<c>Session.SendScriptMessageP</c>, <c>SendInputBox</c>): its kind (0x0001 a text box,
    /// 0x0404 an input box) and text, as <c>DivinerSpellTests</c> reads them.</summary>
    private sealed record Box(int Kind, string Text)
    {
        public override string ToString() => $"{Kind:x4} \"{Text}\"";
    }

    private static List<Box> Boxes(RecordingOutbound o) =>
        o.BodiesOf(ServerOp.NpcDialog).Where(b => b.Length >= 23 && b[7] == 1).Select(b =>
        {
            int len = (b[21] << 8) | b[22];
            return new Box((b[0] << 8) | b[1], Encoding.ASCII.GetString(b, 23, Math.Min(len, b.Length - 23)));
        }).ToList();

    /// <summary>An input box's answer (0x3A kind 4, a submit).</summary>
    private static byte[] InputFrame(string text)
    {
        byte[] t = Encoding.ASCII.GetBytes(text);
        var body = new byte[11 + t.Length];
        body[0] = 0x04;
        body[8] = 0x02;
        body[10] = (byte)t.Length;
        t.CopyTo(body, 11);
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
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

    /// <summary>The 0x0F cast frame for a question spell: the slot byte, then the typed answer, NUL-terminated.</summary>
    private static byte[] AnswerFrame(int slot, string answer)
    {
        var body = new List<byte> { (byte)(slot + 1) };
        body.AddRange(Encoding.ASCII.GetBytes(answer));
        body.Add(0);
        return SessionFixture.Frame(ClientOp.Cast, body.ToArray());
    }

    private static string Lines(RecordingOutbound o) => string.Join(" | ", SpellCastSupport.MiniTexts(o));

    /// <summary>The 0x0A lines of one type (0 the blue whisper channel, 3 the minitext).</summary>
    private static List<string> Typed(RecordingOutbound o, byte type) =>
        o.BodiesOf(ServerOp.MiniText).Where(b => b.Length >= 3 && b[0] == type)
         .Select(b => Encoding.ASCII.GetString(b, 3, Math.Min((b[1] << 8) | b[2], b.Length - 3))).ToList();

    private static uint U32(byte[] b, int at) => (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]);

    /// <summary>The spell graphics (0x29) sent over <paramref name="id"/>, as effect ids.</summary>
    private static List<int> Effects(RecordingOutbound outbound, uint id) =>
        outbound.BodiesOf(ServerOp.Effect).Where(b => U32(b, 0) == id).Select(b => b[4] - ServerConfig.Current.EfxWireOffset).ToList();

    /// <summary>The sounds (0x19) sent.</summary>
    private static List<int> Sounds(RecordingOutbound outbound) =>
        outbound.BodiesOf(ServerOp.Audio).Where(b => b.Length >= 4).Select(b => (b[2] << 8) | b[3]).ToList();

    private static (int Look, int Color, long LeftMs) Morph(Session s)
    {
        (int, int, long) got = default;
        s.WithState(() => got = ((ushort)MorphLookField.GetValue(s)!, (byte)MorphColorField.GetValue(s)!,
                                 (long)MorphUntilField.GetValue(s)! - Environment.TickCount64));
        return got;
    }

    private static void FreeSlot(Session s) => s.WithState(() => { NextActionTick.SetValue(s, 0L); ActionCount.SetValue(s, 0); });

    /// <summary>One 500 blow of <paramref name="kind"/> on <paramref name="victim"/> through its production entry point (as
    /// <c>DamageIntakeTests</c> drives each kind): a creature's swing, another player's swing, a creature's spell, another
    /// player's spell.</summary>
    private static void Strike(Session victim, DamageKind kind, Session peer, Mob mob)
    {
        switch (kind)
        {
            case DamageKind.MobMelee: victim.ApplyMobHit(mob, 500); break;
            case DamageKind.PlayerMelee: victim.ReceiveMeleeDamage(500, peer, crit: false); break;
            case DamageKind.MobSpell: victim.ReceiveMobSpell(500, mob, "Ice ray"); break;
            case DamageKind.PlayerSpell: victim.ReceiveSpellDamage(500, peer, "Spark"); break;
            default: throw new NotSupportedException(kind.ToString());
        }
    }

    /// <summary>Cast Invincibility until a cast lands (RTK's roll lands 71 in 100 at the best armour), clearing the wait
    /// between; true once one has. Each try is a fresh cast slot.</summary>
    private static bool LandInvincibility(Session caster, RecordingOutbound o)
    {
        for (int i = 0; i < 40; i++)
        {
            o.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            if (Lines(o) == "Harden body spell | You cast Invincibility.") return true;
            SpellCastSupport.ClearAether(caster, "invincibility");
        }
        return false;
    }

    /// <summary>A map's size as Maps.csv has it, or a wide 100 x 100 for a content-free map.</summary>
    private static void Size(Character c, ushort map)
    {
        if (Content.Maps.TryGetValue(map, out var m)) { c.MapXs = m.Xs; c.MapYs = m.Ys; }
        else { c.MapXs = 100; c.MapYs = 100; }
    }

    /// <summary>A minute-long venom from no one, ticking <paramref name="perTick"/> every 1 ms (VenomWardTests' shape).</summary>
    private static void Venom(Session p, int perTick)
    {
        p.ReceivePoison(dps: 1, durMs: 60_000, by: 0, anim: 7, key: "chongun_venom_probe", name: "Probe Venom",
                        perTick: perTick, tickMinMs: 1, tickMaxMs: 1);
        Assert.True(p.Poisoned, "the venom did not take");
    }

    /// <summary>Run the heartbeat's venom call until a tick has run, which shows as its line.</summary>
    private static void Tick(Session p, RecordingOutbound o) =>
        Assert.True(SpinWait.SpinUntil(() =>
        {
            p.TickPoison();
            return SpellCastSupport.MiniTexts(o).Contains("Poison is spreading through your veins.");
        }, 1000), "no venom tick ran within one second");

    /// <summary>Run <paramref name="body"/> on a thread of its own with no synchronisation context, as a read loop runs a
    /// packet: an await inside it (a menu's or a box's) then resumes inline on the thread that answers it.</summary>
    private static void Bare(Action body)
    {
        Exception? fault = null;
        var t = new Thread(() => fault = Record.Exception(body)) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(30)), "the packet thread never finished");
        Assert.Null(fault);
    }

    private static void OnOwnThread(Action body) => Bare(body);

    /// <summary>A letters-only name, unique in this process: Title of nobility's answers take letters, spaces and an
    /// apostrophe only, and the fixture's other players carry digits.</summary>
    private static string Letters(string stem)
    {
        int n = Interlocked.Increment(ref _serial);
        var sb = new StringBuilder(stem);
        do { sb.Append((char)('a' + n % 26)); n /= 26; } while (n > 0);
        return sb.ToString();
    }

    private Mob AddCreature(ushort map, int x, int y, int hp = 100_000, int maxHp = 100_000, bool npc = false)
    {
        var mob = new Mob(_fx.World.AllocateMobId(), 1, (ushort)x, (ushort)y, $"Chongun target {Interlocked.Increment(ref _serial)}", maxHp)
        {
            IsNpc = npc,
        };
        mob.Hp = hp;
        _fx.World.AddMob(map, mob);
        return mob;
    }

    /// <summary>A level-99 Chongun at (<paramref name="x"/>, <paramref name="y"/>) facing east, with <paramref name="key"/>
    /// in slot 0 of the book, on the map's own size (a wide one for a content-free map).</summary>
    private (Session session, RecordingOutbound outbound, Character c) Caster(string key, ushort map, int x, int y,
                                                                            uint mana = Mana, uint maxMana = MaxMana,
                                                                            Action<Character>? configure = null, string? name = null) =>
        _fx.PlayerWith(name ?? $"Chongun{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.ClassName = "Chongun";
            ch.Level = 99;
            ch.Ac = 0;
            ch.MaxHp = Hp; ch.Hp = Hp;
            ch.MaxMp = maxMana; ch.Mp = mana;
            ch.Dir = 1;
            Size(ch, map);
            ch.Spells.Add(Content.SpellByKey(key)!.Id);
            configure?.Invoke(ch);
        }, map, (ushort)x, (ushort)y);

    private (Session session, RecordingOutbound outbound, Character c) Player(ushort map, int x, int y, uint hp = Hp,
                                                                            Action<Character>? configure = null, string? name = null) =>
        _fx.PlayerWith(name ?? $"ChongunOther{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 50;
            ch.Ac = 0;
            ch.MaxHp = Math.Max(hp, 1_000); ch.Hp = hp;
            Size(ch, map);
            configure?.Invoke(ch);
        }, map, (ushort)x, (ushort)y);

    private void Leave(ushort map, params Session[] sessions)
    {
        foreach (var s in sessions) _fx.World.LeaveMap(s, map);
    }

    // ---- the held-monitor run (MuseSpellTests' shape) ----------------------------------------------------------------

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
