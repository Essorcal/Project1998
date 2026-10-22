using System.Reflection;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Every damage verb refuses an NPC before it spends anything (#349's review, F1, and #349's follow-up). An NPC is a
/// mob, so the damage resolvers take one as a victim, but <c>World.TryDamage</c> refuses it and no blow can hurt it.
/// #349 made Drain and Kamikaze refuse it. The other damage verbs still charged for a blow that struck nothing:
/// <list type="bullet">
/// <item><b>Bolt and Freeze</b> (<c>magic_damage</c>): "You cast X.", the mana (4 and 800), the pose.</item>
/// <item><b>The NPC Ambush and the other <c>generic</c> damage spells</b>: "You cast X.", 5 mana, the pose and the
/// zap over the NPC. Their gate, <c>hasDamageTarget</c>, still answered yes for an NPC.</item>
/// <item><b>Call Lightning and the other 73 Damage archetype spells</b> (<c>arch_damage</c>): "You cast X.", the
/// mana and the pose, and, through <c>ApplyCast</c>'s tail, most of the pool for Hellfire and all of it for Dooms
/// Fire, Assault's shout and half the caster's HP, and each row's aether. <c>ctx:magicDamage</c> resolved its own
/// victim, so #349's fix could not reach it.</item>
/// <item><b>Venom and Burn</b> (<c>venom</c>): "You cast X.", 60 or 200 mana, the pose, and a venom on the NPC that
/// cannot tick (Venom's animation repeating over it).</item>
/// <item><b>Whirlwind and the other sacrifice strikes</b> (<c>sacrifice</c>): "You cast X.", the mana, the aether, the
/// swing, the strike's animation and an HP bar over the NPC, and for most of them the shout and the strike's own HP
/// cost. At a later <c>EraDate</c> the strike's overkill off the NPC's 1 HP was splashed onto the creatures beside it or
/// refunded to the caster (<see cref="NpcSacrificeEraTests"/>).</item>
/// <item><b>The stock client reaches them through procs</b> as well: the Poet whip's Freeze (800 mana a proc), the
/// Blood weapons' Venom (60) and Flamefang's Burn (200), each at whatever is faced.</item>
/// </list>
///
/// <para>Each verb now refuses an NPC with its own answer to "nothing there", before it spends anything: silence for
/// Bolt, Freeze, the <c>generic</c> damage spells and the Damage archetype, and Venom's "It doesn't work.". The
/// sacrifice strikes take RTK's reading instead, as Caleb chose (2026-10-19): an NPC in front is nothing in front. It
/// costs exactly what a strike at nothing costs, the mana, the aether, the swing and the shout, and strikes
/// nothing.</para>
///
/// <para>The controls: each verb still strikes a creature (by the slot alone, by its id, and through #343's off-map
/// fallback), each proc still lands on one, a sacrifice strike at nothing still charges for it, the area spells still
/// skip an NPC and strike the creatures around it, and the rogue's Ambush, which spends nothing, still leaps behind an
/// NPC.</para>
///
/// <para>Caleb's decision of 2026-10-21 (decision sheet item 9) added three refusals, each before anything is spent:
/// Bolt and Freeze at nothing off a PvP map, silently (9a); Bolt and Freeze, the Poet whip's proc included, at a player
/// off a PvP map, with "You can't attack that target." (9b); and every curse, from the book or one of the five curse
/// procs, at an NPC, silently (9c). On a PvP map a cast at a player is unchanged.</para>
///
/// <para>A later decision of the same day made Bolt and Freeze at nothing free and silent on a PvP map too, and silent
/// with too little mana on any map, as RTK's zap is (it returns at a missing target first). A cast aimed at the
/// caster's own id is not nothing and still strikes them where PvP is on.</para>
///
/// <para>Every cast is the real 0x0F frame through <c>Session.Receive</c>, and every swing the real 0x13 frame, by a
/// fresh caster facing a fresh target. The NPC is built as <c>World.PlaceNpc</c> builds one: 1 HP, <c>IsNpc</c>, no Will
/// and no Protection. The arena is a real PvP map with no spawns and no NPCs; the other two maps are content-free, so
/// PvP is off on them. No other class uses any of the three.</para>
/// </summary>
[Collection("world")]
public sealed class NpcDamageVerbTests
{
    /// <summary>Forsaken Arena (Maps.csv MapPvP 1, MapSpells 1). No spawns and no NPCs.</summary>
    private const ushort Arena = 2591;
    /// <summary>Content-free: no Maps.csv row, so <c>Content.IsPvpMap</c> is false, as it is in every town.</summary>
    private const ushort Town = 62503, Elsewhere = 62504;

    private const ushort CX = 10, CY = 10;      // where every caster stands, facing east at (CX + 1, CY)
    private const uint Hp = 5_000, MaxHp = 10_000, Mana = 50_000;
    /// <summary>A bound on the swings a proc row may take. At 4% a swing, 2000 swings without three procs is a chance
    /// of about 1 in 10^30.</summary>
    private const int MaxSwings = 2_000;

    private static readonly FieldInfo NextActionTick =
        typeof(Session).GetField("_nextActionTick", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>What the cast is aimed at: the slot alone (the stock client's frame), the target's own id (what a proc
    /// passes), or the id of a player on another map, which #343 sends to the faced tile.</summary>
    public enum Aim { Slot, TargetId, OffMapPlayerId }

    private readonly SessionFixture _fx;
    private static int _serial;

    public NpcDamageVerbTests(SessionFixture fx) => _fx = fx;

    // ===== at an NPC ===================================================================================

    /// <summary>One spell per refusing verb and per kind of tail, from the book: Bolt and Freeze (<c>magic_damage</c>),
    /// the NPC Ambush (<c>generic</c>), Call Lightning, Assault and Hellfire (<c>arch_damage</c>: plain, the shout and
    /// vita tail, the pool drain), and Venom and Burn (<c>venom</c>).</summary>
    private static readonly string[] BookSpells =
    {
        "bolt_mage", "freeze", "ambush", "call_lightning_mage", "assault_warrior", "hellfire_mage",
        "venom_mage", "burn",
    };

    /// <summary>One sacrifice strike per family: Whirlwind, Berserk, Lethal Strike, Desperate Attack, Siege and Focused
    /// Blow.</summary>
    private static readonly string[] Strikes =
    {
        "whirlwind_warrior", "berserk_warrior", "lethal_strike_rogue", "desperate_attack_rogue", "siege_warrior",
        "focused_blow_rogue",
    };

    /// <summary>The spells a client can aim by id ("Which target? &gt;"), one per verb that reads an aimed id.</summary>
    private static readonly string[] AimedSpells = { "bolt_mage", "ambush", "call_lightning_mage", "venom_mage" };

    public static TheoryData<string, ushort, Aim> NpcCases
    {
        get
        {
            var data = new TheoryData<string, ushort, Aim>();
            foreach (var map in new[] { Arena, Town })
            {
                foreach (var key in BookSpells) data.Add(key, map, Aim.Slot);
                foreach (var key in AimedSpells)
                    foreach (var aim in new[] { Aim.TargetId, Aim.OffMapPlayerId })
                        data.Add(key, map, aim);
            }
            return data;
        }
    }

    /// <summary>With an NPC faced, or aimed at by its id, every damage verb refuses before it spends anything. Venom
    /// says "It doesn't work."; the others say nothing. No mana, no pose, no shout, no animation, no aether, the caster's
    /// HP kept, the NPC untouched and carrying nothing, and the player on the other map neither hit nor told anything.
    /// <para>Red on 63efdd8 for all of them: "You cast X.", the mana and the pose, and each verb's own extras (see the
    /// class summary).</para></summary>
    [Theory]
    [MemberData(nameof(NpcCases))]
    public void ADamageVerbRefusesAnNpcBeforeItSpendsAnything(string key, ushort map, Aim aim)
    {
        var sp = Spell(key);
        Assert.Equal(map == Arena, Content.IsPvpMap(map));
        var r = Cast(sp, map, aim, npc: true);
        string expectLines = Content.SpellParams.TryGetValue(key, out var row) && row.GetValueOrDefault("verb") == "venom"
            ? "It doesn't work." : "";
        Assert.True(r.Lines == expectLines && r.Spent == 0 && r.HpDelta == 0 && r.Pose == 0 && r.Speech == 0 && r.Fx == 0
                    && r.Aether == 0 && !r.Moved,
                    $"{sp.Name} at {aim} on map {map}, an NPC faced: {r}");
        Assert.True(r.TargetHp == 1 && r.TargetState == "" && r.FarHp == Hp && r.FarLines == "",
                    $"{sp.Name} at {aim} on map {map}: {r}");
    }

    /// <summary>A curse from the book for each category: Pestilence and the poet's Scourge (<c>curses</c>), Vex
    /// (<c>minorcurses</c>) and Dishearten (<c>disheartens</c>). Every one is a "Which target? &gt;" spell.</summary>
    private static readonly string[] Curses = { "pestilence_mage", "scourge_poet", "vex_mage", "dishearten_poet" };

    public static TheoryData<string, ushort, Aim> CurseNpcCases
    {
        get
        {
            var data = new TheoryData<string, ushort, Aim>();
            foreach (var map in new[] { Arena, Town })
                foreach (var key in Curses)
                    foreach (var aim in new[] { Aim.Slot, Aim.TargetId, Aim.OffMapPlayerId })
                        data.Add(key, map, aim);
            return data;
        }
    }

    /// <summary>With an NPC faced, or aimed at by its id, a curse refuses before it spends anything (Caleb, 2026-10-21,
    /// decision sheet item 9c), and says nothing, the curse's answer to a cast that finds nothing. No mana, no pose, no
    /// animation, no "You cast X."; the NPC carries nothing, and neither does the caster: the NPC ends the search for a
    /// target, so on the arena the cast never falls through to its caster. Through #343's fallback as well, where an id
    /// naming a player on another map takes the faced tile.
    /// <para>Red on 6de1d24 for all of them: the curse's mana, the pose and "You cast X.", and the NPC cursed, or, when
    /// the curse lost its roll, "Something went wrong." for the same mana.</para></summary>
    [Theory]
    [MemberData(nameof(CurseNpcCases))]
    public void ACurseRefusesAnNpcBeforeItSpendsAnything(string key, ushort map, Aim aim)
    {
        var sp = Spell(key);
        Assert.Equal("curse", VerbOf(key));
        Assert.Equal(map == Arena, Content.IsPvpMap(map));
        var r = Cast(sp, map, aim, npc: true);
        Assert.True(r.Lines == "" && r.Spent == 0 && r.HpDelta == 0 && r.Pose == 0 && r.Fx == 0 && !r.CasterCarries,
                    $"{sp.Name} at {aim} on map {map}, an NPC faced: {r}");
        Assert.True(r.TargetHp == 1 && r.TargetState == "" && r.FarHp == Hp && r.FarLines == "",
                    $"{sp.Name} at {aim} on map {map}: {r}");
    }

    public static TheoryData<string, ushort> CurseCreatureCases
    {
        get
        {
            var data = new TheoryData<string, ushort>();
            foreach (var map in new[] { Arena, Town })
                foreach (var key in Curses) data.Add(key, map);
            return data;
        }
    }

    /// <summary>The control: the same curses at a faced creature still land. Each cast costs the curse's mana and says
    /// "You cast X."; a curse rolls to take hold (75 to 85%), and one that loses the roll says "Something went wrong."
    /// first. Fresh casters at fresh creatures until one takes hold, at most 20: the creature then carries the curse's
    /// slot.
    /// <para>Green on 6de1d24 as well.</para></summary>
    [Theory]
    [MemberData(nameof(CurseCreatureCases))]
    public void ACurseStillLandsOnACreature(string key, ushort map)
    {
        var sp = Spell(key);
        uint cost = (uint)int.Parse(Content.SpellParams[key]["mana"]);
        string category = Content.SpellParams[key]["category"];
        string slot = category == "minorcurses" ? "curses" : category;   // Session.CatFamily
        for (int i = 0; i < 20; i++)
        {
            var r = Cast(sp, map, Aim.Slot, npc: false);
            string what = $"{sp.Name} on map {map}, a creature faced: {r}";
            Assert.True(r.Lines.EndsWith($"You cast {sp.Name}.", StringComparison.Ordinal) && r.Spent == cost && r.Pose == 1,
                        what);
            if (r.TargetState.Contains(slot, StringComparison.Ordinal)) return;
            Assert.True(r.Lines == $"Something went wrong. | You cast {sp.Name}.", what);
        }
        Assert.Fail($"{sp.Name} on map {map}: no curse took hold on a creature in 20 casts");
    }

    /// <summary>The weapons whose on-swing proc casts a spell at whatever is faced (game-data/WeaponProcs.csv), one per
    /// refusing verb's proc: the Poet whip's Freeze (<c>magic_damage</c>), a Blood weapon's Venom and Flamefang's Burn
    /// (<c>venom</c>), and the five curse procs (<c>curse</c>): the Cursed, Corrupted, Tainted and Forsaken blades and
    /// the Frost sabre.</summary>
    private static readonly (string Item, string Spell)[] ProcWeapons =
    {
        ("poet_whip", "freeze"), ("blood", "venom"), ("flamefang", "burn"),
        ("cursed_blade", "curse"), ("frost_sabre", "chill"), ("corrupted_blade", "corrupt"), ("tainted_blade", "taint"),
        ("forsaken_blade", "forsake"),
    };

    public static TheoryData<string, string, ushort> ProcCases
    {
        get
        {
            var data = new TheoryData<string, string, ushort>();
            foreach (var map in new[] { Arena, Town })
                foreach (var (item, spell) in ProcWeapons)
                    data.Add(item, spell, map);
            return data;
        }
    }

    private static string VerbOf(string key) => Content.SpellParams[key].GetValueOrDefault("verb", "");

    /// <summary>The stock client's procs, through real swings at a faced NPC, cost nothing: three procs or more fired
    /// (each one's own log line, naming the NPC's id), and the caster kept every point of mana and HP and heard no
    /// shout. The Poet whip's Freeze and the curse procs say nothing; the Venom and Burn procs say Venom's "It doesn't
    /// work.", once a proc, as the Charm weapons' Endear proc and the Forsaken staff's Blind proc already do at an NPC.
    /// The NPC is untouched and carries no venom and no curse.
    /// <para>Red on 63efdd8: Freeze cost 800 a proc; the first Venom or Burn proc cost 60 or 200 and venomed the NPC,
    /// and each later one said "You already cast that spell.".</para>
    /// <para>The curse procs were added for Caleb's decision of 2026-10-21 (decision sheet item 9c). Red on 6de1d24:
    /// the first proc cost the curse's mana (40 to 500) and cursed the NPC, or lost the curse's roll and said "Something
    /// went wrong." for the same mana; each later one said "You already cast that spell." once the NPC carried
    /// it.</para></summary>
    [Theory]
    [MemberData(nameof(ProcCases))]
    public void AProcAtAnNpcCostsNothing(string item, string spell, ushort map)
    {
        var npc = Npc(map, CX + 1);
        var (caster, cOut, cc) = Swinger(map, item, spell);
        try
        {
            using var log = LogLineSink.Acquire();
            int procs = 0, swings = 0, speech = 0, fx = 0;
            var lines = new List<string>();
            while (procs < 3 && swings < MaxSwings)
            {
                cOut.Clear();
                Swing(caster);
                swings++;
                procs = Procs(log, item, spell, npc.Id);
                speech += cOut.BodiesOf(ServerOp.Speech).Count;
                fx += cOut.BodiesOf(ServerOp.Effect).Count(b => b.Length >= 4 && EntityId(b) == npc.Id);
                lines.AddRange(SpellCastSupport.MiniTexts(cOut));
            }
            string what = $"{item} on map {map}: {procs} procs in {swings} swings; spent {Mana - cc.Mp}, hp {cc.Hp}, " +
                          $"speech {speech}, fx over the NPC {fx}, lines [{string.Join(" | ", lines)}], npc hp {npc.Hp}, " +
                          $"npc carries [{State(npc)}]";
            Assert.True(procs >= 3, what);
            bool venom = VerbOf(spell) == "venom";
            Assert.True(cc.Mp == Mana && cc.Hp == Hp && speech == 0 && fx == 0, what);
            Assert.True(venom ? lines.Count == procs && lines.All(l => l == "It doesn't work.") : lines.Count == 0, what);
            Assert.True(npc.Hp == 1 && State(npc) == "", what);
        }
        finally
        {
            _fx.World.LeaveMap(caster, map);
            Remove(map, npc);
        }
    }

    public static TheoryData<string, ushort> StrikeCases
    {
        get
        {
            var data = new TheoryData<string, ushort>();
            foreach (var map in new[] { Arena, Town })
                foreach (var key in Strikes) data.Add(key, map);
            return data;
        }
    }

    /// <summary>A sacrifice strike at a faced NPC costs exactly what the same strike at nothing costs, RTK's reading
    /// (Caleb, 2026-10-19): the same lines ("You cast X."), mana, aether, swing and shout, and the caster's HP kept. The
    /// strike lands nothing on the NPC: no animation and no HP bar over it, and the NPC untouched and carrying nothing.
    /// The control is the same strike, by a fresh caster, with nothing in front.
    /// <para>Red on 63efdd8: the strike's HP cost (Whirlwind left its caster on 10, Desperate Attack took all the mana),
    /// and its animation and an HP bar over the NPC. Red on 1f08f20 too, which refused the NPC for free.</para></summary>
    [Theory]
    [MemberData(nameof(StrikeCases))]
    public void ASacrificeStrikeAtAnNpcCostsWhatNothingInFrontCosts(string key, ushort map)
    {
        var sp = Spell(key);
        var atNpc = Cast(sp, map, Aim.Slot, npc: true);
        var atNothing = Cast(sp, map, Aim.Slot, npc: null);
        string what = $"{sp.Name} on map {map}: at an NPC {atNpc}; at nothing {atNothing}";
        Assert.True(atNothing.Lines == $"You cast {sp.Name}." && atNothing.Spent > 0 && atNothing.Aether > 0, what);
        Assert.True(atNpc.Lines == atNothing.Lines && atNpc.Spent == atNothing.Spent && atNpc.HpDelta == atNothing.HpDelta
                    && atNpc.Pose == atNothing.Pose && atNpc.Speech == atNothing.Speech && atNpc.Fx == atNothing.Fx
                    && atNpc.Bars == atNothing.Bars && Math.Abs(atNpc.Aether - atNothing.Aether) < 2_000, what);
        Assert.True(atNpc.HpDelta == 0 && atNpc.Fx == 0 && atNpc.Bars == 0 && atNpc.TargetHp == 1 && atNpc.TargetState == ""
                    && !atNpc.Moved, what);
    }

    public static TheoryData<string, bool> NpcTileCases
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (var key in Strikes)
                foreach (var byPlayerId in new[] { false, true }) data.Add(key, byPlayerId);
            return data;
        }
    }

    /// <summary>A sacrifice strike at a faced NPC never falls through to a player on the NPC's tile (PR #351 re-check,
    /// F1). On the arena, a PvP map, a strike with only a player in front takes that player (the PvP fallback in
    /// <c>Session.LuaSacFrontMob</c>); with an NPC in front the search ends at the NPC. So a player placed on the NPC's
    /// tile (a walk onto an NPC is refused, so only a placement or a no-clipper shares one) keeps every point of HP and
    /// mana and sees exactly what a bystander sees, frame for frame and byte for byte: the swing, and the shout where
    /// the strike has one. Cast by the slot alone, and aimed at that player's id.
    /// <para>Red under the re-check's mutant FT, which skips the NPC and lets the search go on
    /// (<c>if (m is { IsNpc: true }) m = null;</c>): every strike took the player, who lost HP or read more than the
    /// bystander.</para></summary>
    [Theory]
    [MemberData(nameof(NpcTileCases))]
    public void ASacrificeStrikeAtAnNpcNeverStrikesAPlayerOnItsTile(string key, bool byPlayerId)
    {
        var sp = Spell(key);
        Assert.True(Content.IsPvpMap(Arena));
        var npc = Npc(Arena, CX + 1);
        var (onTile, tOut, tc) = Onlooker("NdvOnTile", CX + 1, CY);
        var (by, bOut, _) = Onlooker("NdvBy", CX, CY + 3);
        var (caster, cOut, _) = Caster(Arena, ch => ch.Spells.Add(sp.Id));
        try
        {
            Assert.True(ReferenceEquals(_fx.World.MobAt(Arena, CX + 1, CY), npc)
                        && ReferenceEquals(_fx.World.PeerAt(Arena, CX + 1, CY), onTile),
                        $"{sp.Name}: the NPC and the player are not both on the faced tile");
            uint hp0 = tc.Hp, mp0 = tc.Mp;
            cOut.Clear(); tOut.Clear(); bOut.Clear();

            caster.Receive(SpellCastSupport.CastFrame(0, byPlayerId ? onTile.PlayerId : null));

            var seen = Wire(tOut);
            var bySeen = Wire(bOut);
            string what = $"{sp.Name} on map {Arena}{(byPlayerId ? ", aimed at the player's id" : "")}: caster " +
                          $"[{string.Join(" | ", SpellCastSupport.MiniTexts(cOut))}]; the player on the NPC's tile on " +
                          $"{tc.Hp}/{tc.Mp} (was {hp0}/{mp0}), saw [{string.Join(" ", seen)}]; the bystander saw " +
                          $"[{string.Join(" ", bySeen)}]; the NPC on {npc.Hp}, carrying [{State(npc)}]";
            Assert.True(SpellCastSupport.MiniTexts(cOut).SequenceEqual(new[] { $"You cast {sp.Name}." })
                        && bOut.BodiesOf(ServerOp.Action).Count == 1, what);
            Assert.True(tc.Hp == hp0 && tc.Mp == mp0 && seen.SequenceEqual(bySeen), what);
            Assert.True(npc.Hp == 1 && State(npc) == "", what);
        }
        finally
        {
            _fx.World.LeaveMap(caster, Arena);
            _fx.World.LeaveMap(by, Arena);
            _fx.World.LeaveMap(onTile, Arena);
            Remove(Arena, npc);
        }
    }

    // ===== the controls ================================================================================

    public static TheoryData<string, ushort, Aim> CreatureCases
    {
        get
        {
            var data = new TheoryData<string, ushort, Aim>();
            foreach (var map in new[] { Arena, Town })
            {
                foreach (var key in BookSpells.Concat(new[] { "whirlwind_warrior", "desperate_attack_rogue" }))
                    data.Add(key, map, Aim.Slot);
                foreach (var key in AimedSpells)
                    foreach (var aim in new[] { Aim.TargetId, Aim.OffMapPlayerId })
                        data.Add(key, map, aim);
            }
            return data;
        }
    }

    /// <summary>The control: the same casts at a faced creature still land. "You cast X.", the mana, the pose, and the
    /// creature struck (or, for Venom and Burn, venomed). By the slot alone, by the creature's id, and through #343's
    /// fallback, where an id naming a player on another map takes the faced tile.
    /// <para>Green on 63efdd8 as well.</para></summary>
    [Theory]
    [MemberData(nameof(CreatureCases))]
    public void ADamageVerbStillStrikesACreature(string key, ushort map, Aim aim)
    {
        var sp = Spell(key);
        var r = Cast(sp, map, aim, npc: false);
        bool venom = Content.SpellParams.TryGetValue(key, out var row) && row.GetValueOrDefault("verb") == "venom";
        Assert.True(r.Lines.EndsWith($"You cast {sp.Name}.", StringComparison.Ordinal) && r.Spent > 0 && r.Pose == 1,
                    $"{sp.Name} at {aim} on map {map}, a creature faced: {r}");
        Assert.True(venom ? r.TargetState.Contains("venoms", StringComparison.Ordinal) : r.TargetHp < Creature,
                    $"{sp.Name} at {aim} on map {map}: {r}");
        Assert.True(r.FarHp == Hp && r.FarLines == "", $"{sp.Name} at {aim} on map {map}: {r}");
    }

    /// <summary>The control: each proc, through real swings at a faced creature, still lands. Until it fires a swing
    /// costs no mana; the swing it fires on costs the spell's mana and strikes the creature (Freeze), venoms it (Venom,
    /// Burn) or curses it (the curse procs). A curse rolls to take hold (75% for this category), and a proc that loses
    /// the roll costs the mana too and says "Something went wrong."; the swings go on until one takes hold. The
    /// creature's HP is out of the swings' reach.
    /// <para>Green on 63efdd8 as well, and on 6de1d24 with the curse procs.</para></summary>
    [Theory]
    [MemberData(nameof(ProcCases))]
    public void AProcAtACreatureStillLands(string item, string spell, ushort map)
    {
        var mob = CreatureAt(map, CX + 1);
        var (caster, cOut, cc) = Swinger(map, item, spell);
        try
        {
            using var log = LogLineSink.Acquire();
            uint cost = (uint)int.Parse(Content.SpellParams[spell]["mana"]);
            string verb = VerbOf(spell);
            for (int swings = 1; swings <= MaxSwings; swings++)
            {
                uint mp0 = cc.Mp;
                int procsBefore = Procs(log, item, spell, mob.Id);
                cOut.Clear();
                Swing(caster);
                int procs = Procs(log, item, spell, mob.Id) - procsBefore;
                if (procs == 0)
                {
                    Assert.True(cc.Mp == mp0, $"{item} on map {map}, swing {swings}: no proc, yet mana {mp0} -> {cc.Mp}");
                    continue;
                }
                var lines = SpellCastSupport.MiniTexts(cOut);
                string what = $"{item} on map {map}, swing {swings}: spent {mp0 - cc.Mp} (the spell costs {cost}), " +
                              $"lines [{string.Join(" | ", lines)}], the creature on {mob.Hp}, carrying [{State(mob)}]";
                Assert.True(mp0 - cc.Mp == cost, what);
                if (verb == "curse" && !State(mob).Contains("curses", StringComparison.Ordinal))
                {
                    Assert.True(lines.SequenceEqual(new[] { "Something went wrong." }), what);   // lost the roll
                    continue;
                }
                Assert.True(verb switch
                {
                    "magic_damage" => mob.Hp < Creature,
                    "venom" => State(mob).Contains("venoms", StringComparison.Ordinal),
                    _ => State(mob).Contains("curses", StringComparison.Ordinal),
                }, what);
                return;
            }
            Assert.Fail($"{item} on map {map}: no proc landed in {MaxSwings} swings");
        }
        finally
        {
            _fx.World.LeaveMap(caster, map);
            _fx.World.DespawnMob(map, mob);
        }
    }

    public static TheoryData<string, ushort, Aim, string, int, int, int> NothingCases => new()
    {
        // key, map, aim, the line, mana, pose, shout
        { "bolt_mage", Town, Aim.Slot, "", 0, 0, 0 },
        { "bolt_mage", Town, Aim.OffMapPlayerId, "", 0, 0, 0 },
        { "freeze", Town, Aim.Slot, "", 0, 0, 0 },
        { "whirlwind_warrior", Town, Aim.Slot, "You cast Whirlwind.", 120, 1, 1 },
        { "whirlwind_warrior", Arena, Aim.Slot, "You cast Whirlwind.", 120, 1, 1 },
    };

    /// <summary>What a cast at nothing costs, off a PvP map and with the mana to pay for it. Bolt and Freeze
    /// (<c>magic_damage</c>) cost nothing and say nothing (Caleb, 2026-10-21, decision sheet item 9a): by the slot alone
    /// with nothing faced, and aimed at a player on another map with nothing faced, which #343 answers as nothing. A
    /// sacrifice strike still charges its mana and aether for nothing in front (RTK's order, kept on purpose; see the
    /// verb), on either map.
    /// <para>Red on 6de1d24 for Bolt and Freeze: "You cast X.", 4 or 800 mana and the pose, for a blow that struck
    /// nothing. The strikes are green there as well.</para></summary>
    [Theory]
    [MemberData(nameof(NothingCases))]
    public void WhatACastAtNothingCosts(string key, ushort map, Aim aim, string line, int mana, int pose, int shout)
    {
        var sp = Spell(key);
        Assert.Equal(map == Arena, Content.IsPvpMap(map));
        var r = Cast(sp, map, aim, npc: null);
        Assert.True(r.Lines == line && r.Spent == mana && r.HpDelta == 0 && r.Pose == pose && r.Speech == shout
                    && r.FarHp == Hp && r.FarLines == "",
                    $"{sp.Name} at nothing ({aim}) on map {map}: {r}");
    }

    public static TheoryData<string, ushort, uint> ShortManaNothingCases => new()
    {
        // key, map, the caster's mana (one short of the row's cost)
        { "bolt_mage", Town, 3 },
        { "bolt_mage", Arena, 3 },
        { "freeze", Town, 799 },
        { "freeze", Arena, 799 },
    };

    /// <summary>With too little mana, Bolt or Freeze at nothing says nothing, on either map (Caleb, 2026-10-21:
    /// "silent like RTK"). RTK's zap returns at a missing target before it looks at the mana
    /// (<c>Spells/common/global_zap.lua:8-10</c>, then <c>:14-17</c>). No line, no mana, no pose.
    /// <para>Red on e651252 (this branch's merge of fc14434): "Not enough mana to cast Bolt." (or Freeze), because the
    /// verb asked for its target only when the cast could be paid for.</para></summary>
    [Theory]
    [MemberData(nameof(ShortManaNothingCases))]
    public void BoltOrFreezeAtNothingWithTooLittleManaSaysNothing(string key, ushort map, uint mana)
    {
        var sp = Spell(key);
        var r = Cast(sp, map, Aim.Slot, npc: null, mana: mana);
        Assert.True(r.Lines == "" && r.Spent == 0 && r.Pose == 0 && r.HpDelta == 0,
                    $"{sp.Name} at nothing with {mana} mana on map {map}: {r}");
    }

    /// <summary>The control for the silent short cast: at a real target, with too little mana, the line stays, as RTK
    /// orders it (mana before the PvP refusal, <c>global_zap.lua:14-17</c>). A creature faced here; a player faced is
    /// <see cref="BoltAtAPlayerWithTooLittleManaSaysWhatItDid"/>.
    /// <para>Green on e651252 as well.</para></summary>
    [Theory]
    [InlineData(Town)]
    [InlineData(Arena)]
    public void BoltAtACreatureWithTooLittleManaStillSaysSo(ushort map)
    {
        var sp = Spell("bolt_mage");
        var r = Cast(sp, map, Aim.Slot, npc: false, mana: 3);
        Assert.True(r.Lines == "Not enough mana to cast Bolt." && r.Spent == 0 && r.Pose == 0 && r.TargetHp == Creature,
                    $"{sp.Name} at a creature with 3 mana on map {map}: {r}");
    }

    public static TheoryData<string, Aim> PvpNothingCases => new()
    {
        { "bolt_mage", Aim.Slot },
        { "freeze", Aim.Slot },
        { "bolt_mage", Aim.OffMapPlayerId },
    };

    /// <summary>On a PvP map a cast at nothing is free and silent (Caleb, 2026-10-21), as RTK's zap is: it returns at a
    /// missing target before anything else (<c>Spells/common/global_zap.lua:8-10</c>). No line, no mana, no pose, no
    /// aether, the caster's HP kept, and the player on the other map neither hit nor told anything. This replaces #356's
    /// <c>OnAPvpMapACastAtNothingIsUnchanged</c>, which pinned the old answer on purpose and left the change to this
    /// decision.
    /// <para>Red on e651252 (this branch's merge of fc14434). By the slot alone with nothing faced, the damage resolver's
    /// PvP fallback gave the caster, and Bolt and Freeze landed on them: "You cast X.", 4 or 800 mana, the pose and the
    /// caster's HP down. Aimed at a player on another map with nothing faced, the resolver gave nobody (#343), and the
    /// cast still cost its mana, its line and the pose for a blow that struck nothing.</para></summary>
    [Theory]
    [MemberData(nameof(PvpNothingCases))]
    public void OnAPvpMapACastAtNothingIsFreeAndSilent(string key, Aim aim)
    {
        var sp = Spell(key);
        Assert.True(Content.IsPvpMap(Arena));
        var r = Cast(sp, Arena, aim, npc: null);
        Assert.True(r.Lines == "" && r.Spent == 0 && r.Pose == 0 && r.HpDelta == 0 && r.Aether == 0
                    && r.FarHp == Hp && r.FarLines == "",
                    $"{sp.Name} at nothing ({aim}) on map {Arena}: {r}");
    }

    /// <summary>The control: on a PvP map Bolt aimed at its caster's own id is aimed, not nothing, and still strikes
    /// them, for its 4 mana and its line. Only the resolver's fallback for a cast aimed at nothing is refused.
    /// <para>Green on e651252 as well.</para></summary>
    [Fact]
    public void OnAPvpMapBoltAimedAtItsCastersOwnIdStillStrikesThem()
    {
        var sp = Spell("bolt_mage");
        var (caster, cOut, cc) = Caster(Arena, ch => ch.Spells.Add(sp.Id));
        try
        {
            uint mp0 = cc.Mp, hp0 = cc.Hp;
            cOut.Clear();
            caster.Receive(SpellCastSupport.CastFrame(0, caster.PlayerId));
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            Assert.True(lines == "You cast Bolt." && mp0 - cc.Mp == 4 && cc.Hp < hp0
                        && cOut.BodiesOf(ServerOp.Action).Count == 1,
                        $"{sp.Name} at its caster's id on map {Arena}: [{lines}], spent {mp0 - cc.Mp}, hp {hp0} -> {cc.Hp}");
        }
        finally
        {
            _fx.World.LeaveMap(caster, Arena);
        }
    }

    public static TheoryData<string, ushort, int, int> AreaCases
    {
        get
        {
            var data = new TheoryData<string, ushort, int, int>();
            foreach (var map in new[] { Arena, Town })
            {
                // The 4-way zap: the NPC on the faced tile (east), a creature on the tile behind the caster (west).
                data.Add("erupt_mage", map, CX + 1, CX - 1);
                // The dog 5-way: aimed at the faced NPC, centred on it, so a creature beside it (east of it) is in the blast.
                data.Add("fissure", map, CX + 1, CX + 2);
            }
            return data;
        }
    }

    /// <summary>The control for the area spells: they skip an NPC and keep striking the creatures in their area. The
    /// 4-way zap with an NPC on one side and a creature on another, and the dog 5-way aimed at a faced NPC with a
    /// creature beside it: "You cast X.", the mana, the creature struck, the NPC untouched. Neither ever charged
    /// because an NPC was faced: each charges for a cast at nothing too.
    /// <para>Green on 63efdd8 as well.</para></summary>
    [Theory]
    [MemberData(nameof(AreaCases))]
    public void AnAreaSpellSkipsTheNpcAndStrikesTheCreature(string key, ushort map, int npcX, int creatureX)
    {
        var sp = Spell(key);
        var npc = Npc(map, (ushort)npcX);
        var mob = CreatureAt(map, (ushort)creatureX);
        var (caster, cOut, cc) = Caster(map, ch => ch.Spells.Add(sp.Id));
        try
        {
            uint mp0 = cc.Mp;
            cOut.Clear();
            caster.Receive(SpellCastSupport.CastFrame(0));
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            string what = $"{sp.Name} on map {map}: [{lines}], spent {mp0 - cc.Mp}, the creature on {mob.Hp}, the NPC on {npc.Hp}";
            Assert.True(lines == $"You cast {sp.Name}." && mp0 - cc.Mp > 0, what);
            Assert.True(mob.Hp < Creature && npc.Hp == 1 && State(npc) == "", what);
        }
        finally
        {
            _fx.World.LeaveMap(caster, map);
            _fx.World.DespawnMob(map, mob);
            Remove(map, npc);
        }
    }

    /// <summary>The control for the rogue's Ambush, which spends nothing: at a faced NPC it still leaps behind it and
    /// swings ("You cast Ambush.", no mana, the caster moved). RTK's <c>Spells/rogue/ambush.lua</c> takes an NPC as its
    /// third choice of target, after a mob and a player. Refusing it would take away a free move, not a cost, so it is
    /// left for a decision of its own.
    /// <para>Green on 63efdd8 as well.</para></summary>
    [Theory]
    [InlineData(Arena)]
    [InlineData(Town)]
    public void TheRoguesAmbushStillLeapsBehindAnNpc(ushort map)
    {
        var sp = Spell("ambush_rogue");
        var r = Cast(sp, map, Aim.Slot, npc: true);
        Assert.True(r.Lines == "You cast Ambush." && r.Spent == 0 && r.HpDelta == 0 && r.Moved && r.TargetHp == 1,
                    $"{sp.Name} on map {map}, an NPC faced: {r}");
    }

    // ===== at a player =================================================================================

    public static TheoryData<string, ushort, bool> PlayerCases
    {
        get
        {
            var data = new TheoryData<string, ushort, bool>();
            foreach (var map in new[] { Town, Arena })
            {
                data.Add("bolt_mage", map, false);
                data.Add("bolt_mage", map, true);
                data.Add("freeze", map, false);
            }
            return data;
        }
    }

    /// <summary>Bolt and Freeze at a faced player. Off a PvP map they are refused for free (Caleb, 2026-10-21, decision
    /// sheet item 9b), with the line the blow's PvP gate has always sent, "You can't attack that target."
    /// (<c>Session.HitPlayerWithSpell</c>): no mana, no pose, no "You cast X.", and the player keeps their HP and is
    /// told nothing. On a PvP map, the control, the blow lands as it did: "You cast X.", the mana, the pose, and the
    /// player's HP down. By the slot alone, the stock client's frame, and for Bolt by the player's id as well.
    /// <para>Red on 6de1d24 off a PvP map: the gate ran after the spend, so the refusal cost 4 or 800 mana, the pose and
    /// "You cast X." too. The PvP rows are green there as well.</para></summary>
    [Theory]
    [MemberData(nameof(PlayerCases))]
    public void ABoltOrFreezeAtAPlayerIsRefusedForFreeOffAPvpMap(string key, ushort map, bool byId)
    {
        var sp = Spell(key);
        uint cost = (uint)int.Parse(Content.SpellParams[key]["mana"]);
        var r = CastAtAPlayer(sp, map, byId, Mana);
        string what = $"{sp.Name} at a faced player{(byId ? ", by their id," : "")} on map {map}: {r}";
        if (Content.IsPvpMap(map))
            Assert.True(r.Lines == $"You cast {sp.Name}." && r.Spent == cost && r.Pose == 1 && r.TargetHp < Creature, what);
        else
            Assert.True(r.Lines == "You can't attack that target." && r.Spent == 0 && r.Pose == 0 && r.TargetHp == Creature
                        && r.TargetLines == "", what);
    }

    /// <summary>Bolt aimed at its caster's own id off a PvP map is refused for free the same way: the blow's PvP gate
    /// covers the caster too (<c>Session.HitPlayerWithSpell</c>), and it is now asked before the spend.
    /// <para>Red on 6de1d24: "You can't attack that target. | You cast Bolt.", 4 mana and the pose.</para></summary>
    [Fact]
    public void BoltAtYourselfOffAPvpMapIsRefusedForFree()
    {
        var sp = Spell("bolt_mage");
        var (caster, cOut, cc) = Caster(Town, ch => ch.Spells.Add(sp.Id));
        try
        {
            uint mp0 = cc.Mp, hp0 = cc.Hp;
            cOut.Clear();
            caster.Receive(SpellCastSupport.CastFrame(0, caster.PlayerId));
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            Assert.True(lines == "You can't attack that target." && cc.Mp == mp0 && cc.Hp == hp0
                        && cOut.BodiesOf(ServerOp.Action).Count == 0,
                        $"{sp.Name} at its caster's id on map {Town}: [{lines}], spent {mp0 - cc.Mp}, hp {hp0} -> {cc.Hp}");
        }
        finally
        {
            _fx.World.LeaveMap(caster, Town);
        }
    }

    /// <summary>With too little mana, Bolt at a player off a PvP map still answers what it did: "Not enough mana to cast
    /// Bolt.", for nothing. RTK's zap orders it the same way, mana before the PvP refusal
    /// (<c>Spells/common/global_zap.lua:14-17</c>, <c>:26-30</c>).
    /// <para>Green on 6de1d24 as well.</para></summary>
    [Fact]
    public void BoltAtAPlayerWithTooLittleManaSaysWhatItDid()
    {
        var sp = Spell("bolt_mage");
        var r = CastAtAPlayer(sp, Town, byId: false, mana: 3);
        Assert.True(r.Lines == "Not enough mana to cast Bolt." && r.Spent == 0 && r.Pose == 0 && r.TargetHp == Creature,
                    $"{sp.Name} at a faced player on map {Town}: {r}");
    }

    /// <summary>The Poet whip's Freeze proc, through real swings at a faced player. Off a PvP map each proc is refused
    /// for free: "You can't attack that target.", once a proc, and no mana. On a PvP map, the control, each proc costs
    /// its 800 as before.
    /// <para>Red on 6de1d24 off a PvP map: 800 mana a proc, with the same line. The PvP row is green there as
    /// well.</para></summary>
    [Theory]
    [InlineData(Town)]
    [InlineData(Arena)]
    public void ThePoetWhipsFreezeProcAtAPlayerCostsNothingOffAPvpMap(ushort map)
    {
        var (target, tOut, tc) = Onlooker("NdvProcTarget", CX + 1, CY, map);
        var (caster, cOut, cc) = Swinger(map, "poet_whip", "freeze");
        try
        {
            using var log = LogLineSink.Acquire();
            int procs = 0, swings = 0;
            var lines = new List<string>();
            while (procs < 3 && swings < MaxSwings)
            {
                cOut.Clear();
                Swing(caster);
                swings++;
                procs = Procs(log, "poet_whip", "freeze", target.PlayerId);
                lines.AddRange(SpellCastSupport.MiniTexts(cOut));
            }
            string what = $"poet_whip on map {map}: {procs} procs in {swings} swings; spent {Mana - cc.Mp}, " +
                          $"lines [{string.Join(" | ", lines)}], the player's lines [{string.Join(" | ", SpellCastSupport.MiniTexts(tOut))}]";
            Assert.True(procs >= 3, what);
            if (Content.IsPvpMap(map))
                Assert.True(Mana - cc.Mp == 800 * procs, what);
            else
                Assert.True(cc.Mp == Mana && lines.Count == procs && lines.All(l => l == "You can't attack that target.")
                            && tc.Hp == Creature, what);
        }
        finally
        {
            _fx.World.LeaveMap(caster, map);
            _fx.World.LeaveMap(target, map);
        }
    }

    // ===== the cast ======================================================================================

    private const int Creature = 10_000_000;

    private sealed record Outcome(string Lines, long Spent, int HpDelta, int Pose, int Speech, int Fx, int Bars,
                                  long Aether, bool Moved, int TargetHp, string TargetState, uint FarHp, string FarLines,
                                  bool CasterCarries);

    /// <summary>A fresh caster on <paramref name="map"/> faces a fresh NPC (<paramref name="npc"/> true), a fresh
    /// creature (false) or nothing (null), with a fresh player on a content-free map, and casts <paramref name="sp"/>
    /// from slot 0 at <paramref name="aim"/>, holding <paramref name="mana"/>. Returns what the caster saw, spent, kept
    /// and carries of the spell, and what the target and the other player kept.</summary>
    private Outcome Cast(SpellDef sp, ushort map, Aim aim, bool? npc, uint mana = Mana)
    {
        Mob? target = npc switch { true => Npc(map, CX + 1), false => CreatureAt(map, CX + 1), null => null };
        var (far, farOut, farc) = Player("NdvFar", Elsewhere);
        var (caster, cOut, cc) = Caster(map, ch => { ch.Spells.Add(sp.Id); ch.Mp = mana; });
        try
        {
            uint? id = aim switch
            {
                Aim.Slot => null,
                Aim.TargetId => target!.Id,
                Aim.OffMapPlayerId => far.PlayerId,
                _ => throw new ArgumentOutOfRangeException(nameof(aim)),
            };
            uint mp0 = cc.Mp, hp0 = cc.Hp;
            cOut.Clear(); farOut.Clear();

            caster.Receive(SpellCastSupport.CastFrame(0, id));

            return new Outcome(string.Join(" | ", SpellCastSupport.MiniTexts(cOut)), (long)mp0 - cc.Mp, (int)cc.Hp - (int)hp0,
                               cOut.BodiesOf(ServerOp.Action).Count, cOut.BodiesOf(ServerOp.Speech).Count,
                               cOut.BodiesOf(ServerOp.Effect).Count, cOut.BodiesOf(ServerOp.Damage).Count,
                               SpellCastSupport.AetherLeft(caster, sp.Key),
                               cc.X != CX || cc.Y != CY, target?.Hp ?? 0, target is null ? "" : State(target),
                               farc.Hp, string.Join(" | ", SpellCastSupport.MiniTexts(farOut)),
                               SpellCastSupport.BuffEntry(caster, sp.Key) is not null);
        }
        finally
        {
            _fx.World.LeaveMap(caster, map);
            _fx.World.LeaveMap(far, Elsewhere);
            if (target is { IsNpc: true }) Remove(map, target);
            else if (target is not null) _fx.World.DespawnMob(map, target);
        }
    }

    private sealed record AtAPlayer(string Lines, long Spent, int Pose, uint TargetHp, string TargetLines);

    /// <summary>A fresh caster on <paramref name="map"/>, holding <paramref name="mana"/>, faces a fresh player and casts
    /// <paramref name="sp"/> from slot 0, by the slot alone or by that player's id. Returns what the caster saw and
    /// spent, and what the player kept and read.</summary>
    private AtAPlayer CastAtAPlayer(SpellDef sp, ushort map, bool byId, uint mana)
    {
        var (target, tOut, tc) = Onlooker("NdvTarget", CX + 1, CY, map);
        var (caster, cOut, cc) = Caster(map, ch => { ch.Spells.Add(sp.Id); ch.Mp = mana; });
        try
        {
            uint mp0 = cc.Mp;
            cOut.Clear(); tOut.Clear();

            caster.Receive(SpellCastSupport.CastFrame(0, byId ? target.PlayerId : null));

            return new AtAPlayer(string.Join(" | ", SpellCastSupport.MiniTexts(cOut)), (long)mp0 - cc.Mp,
                                 cOut.BodiesOf(ServerOp.Action).Count, tc.Hp,
                                 string.Join(" | ", SpellCastSupport.MiniTexts(tOut)));
        }
        finally
        {
            _fx.World.LeaveMap(caster, map);
            _fx.World.LeaveMap(target, map);
        }
    }

    // ===== helpers =======================================================================================

    private static SpellDef Spell(string key)
    {
        var sp = Content.SpellByKey(key);
        Assert.NotNull(sp);
        return sp!;
    }

    /// <summary>An NPC at (<paramref name="x"/>, CY), built as <c>World.PlaceNpc</c> builds one from NPCs.csv.</summary>
    private Mob Npc(ushort map, ushort x)
    {
        Assert.Null(_fx.World.MobAt(map, x, CY));
        var npc = new Mob(_fx.World.AllocateMobId(), 1, x, CY, $"Ndv npc {Interlocked.Increment(ref _serial)}", hp: 1)
        {
            IsNpc = true, Dir = 3,
        };
        _fx.World.AddMob(map, npc);
        return npc;
    }

    /// <summary>A creature at (<paramref name="x"/>, CY) whose HP no blow here can take.</summary>
    private Mob CreatureAt(ushort map, ushort x)
    {
        Assert.Null(_fx.World.MobAt(map, x, CY));
        var mob = new Mob(_fx.World.AllocateMobId(), 1, x, CY, $"Ndv creature {Interlocked.Increment(ref _serial)}", Creature);
        _fx.World.AddMob(map, mob);
        return mob;
    }

    /// <summary><c>World.DespawnMob</c> refuses an NPC, so the test clears the flag first and checks the tile is empty
    /// for the next row.</summary>
    private void Remove(ushort map, Mob npc)
    {
        npc.IsNpc = false;
        Assert.True(_fx.World.DespawnMob(map, npc));
        Assert.Null(_fx.World.MobAt(map, npc.X, CY));
    }

    /// <summary>What a cast can leave on a mob: a live status (its exclusivity slot), a venom, a repeating animation.
    /// Empty when it carries nothing.</summary>
    private static string State(Mob m)
    {
        long now = Environment.TickCount64;
        var parts = new List<string>();
        if (m.Statuses is { } st) parts.AddRange(st.Where(kv => kv.Value.Until > now).Select(kv => kv.Key));
        if (m.PoisonUntil > now) parts.Add("poison");
        if (m.FxRepeatUntil > now) parts.Add("fx repeat");
        return string.Join(",", parts);
    }

    /// <summary>One swing: the real 0x13 frame, after freeing the shared cast/swing slot the last swing armed
    /// (<c>Session.ArmActionSlot</c>), so the swings need not wait out the swing interval.</summary>
    private static void Swing(Session s)
    {
        s.WithState(() => NextActionTick.SetValue(s, 0L));
        s.Receive(SessionFixture.Frame(ClientOp.Attack, new byte[] { 0 }));
    }

    /// <summary>How many procs of <paramref name="spell"/> <c>Session.FireWeaponProcs</c> has logged at
    /// <paramref name="targetId"/>. The line is written as the proc fires, before its cast runs.</summary>
    private static int Procs(LogLineSink log, string item, string spell, uint targetId)
    {
        string needle = $"weapon proc: {item} (4%) casts {spell} on {targetId}";
        return log.Lines.Count(l => l.Line.TrimEnd().EndsWith(needle, StringComparison.Ordinal));
    }

    private static uint EntityId(byte[] b) => (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);

    /// <summary>A level-99 caster at half its HP with plenty of mana and Will 255, facing east.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Caster(ushort map, Action<Character> more) =>
        _fx.PlayerWith($"NdvCaster{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 99;
            ch.MaxHp = MaxHp; ch.Hp = Hp;
            ch.MaxMp = Mana; ch.Mp = Mana;
            ch.Will = 255;
            ch.Dir = 1;
            Wide(ch, map);
            more(ch);
        }, map, CX, CY);

    /// <summary>The same caster, wielding <paramref name="item"/>, whose on-swing proc casts <paramref name="spell"/>
    /// (game-data/WeaponProcs.csv), and knowing no spell. Its base pool is 1,000 above its mana, so a weapon that lowers
    /// the pool (the Corrupted blade's ItmMana is -200) never clamps the mana at the first stats push, which would read
    /// as a cost.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Swinger(ushort map, string item, string spell)
    {
        var def = Content.ItemByKey(item);
        Assert.NotNull(def);
        Assert.Equal(spell, Content.WeaponProcFor(item)?.Spell);
        var s = Caster(map, ch =>
        {
            ch.MaxMp = Mana + 1_000;
            ch.Equipment.Add(new InvItem { Slot = 1, ItemId = def!.Id });
        });
        s.outbound.Clear();
        return s;
    }

    /// <summary>A level-50 player at half HP with nothing running, on a content-free map.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Player(string name, ushort map) =>
        _fx.PlayerWith($"{name}{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 50;
            ch.MaxHp = MaxHp; ch.Hp = Hp;
            ch.MaxMp = 1_000; ch.Mp = 1_000;
            Wide(ch, map);
        }, map, CX, CY);

    /// <summary>A level-50 player on <paramref name="map"/> (the arena unless named) at (<paramref name="x"/>,
    /// <paramref name="y"/>), with an HP pool no strike here can empty, so a strike that lands shows as lost HP and not
    /// as a death.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Onlooker(string name, ushort x, ushort y,
                                                                               ushort map = Arena) =>
        _fx.PlayerWith($"{name}{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 50;
            ch.MaxHp = Creature; ch.Hp = Creature;
            ch.MaxMp = 1_000; ch.Mp = 1_000;
            Wide(ch, map);
        }, map, x, y);

    /// <summary>Every frame the recorder was sent, in order, as its opcode and its decrypted body in hex.</summary>
    private static List<string> Wire(RecordingOutbound o)
    {
        var frames = new List<string>();
        foreach (var frame in o.Frames)
        {
            Assert.True(TkPacket.TryParse(frame, out var pkt, out _));
            frames.Add($"{pkt.Opcode:x2}:{Convert.ToHexString(TkCrypt.Crypt(pkt.Body, pkt.Increment, TkCrypt.LoginKey))}");
        }
        return frames;
    }

    /// <summary>The arena carries its own size if its row gives one; content-free maps have none, and the handlers
    /// need one.</summary>
    private static void Wide(Character c, ushort map)
    {
        if (Content.Maps.TryGetValue(map, out var m) && m.Xs > 0) { c.MapXs = m.Xs; c.MapYs = m.Ys; }
        else { c.MapXs = 100; c.MapYs = 100; }
    }
}
