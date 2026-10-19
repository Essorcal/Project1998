using System.Reflection;
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
/// cost.</item>
/// <item><b>The stock client reaches them through procs</b> as well: the Poet whip's Freeze (800 mana a proc), the
/// Blood weapons' Venom (60) and Flamefang's Burn (200), each at whatever is faced.</item>
/// </list>
///
/// <para>Each verb now refuses an NPC with its own answer to "nothing there", before it spends anything: silence for
/// Bolt, Freeze, the <c>generic</c> damage spells, the Damage archetype and the sacrifice strikes, and Venom's
/// "It doesn't work.".</para>
///
/// <para>The controls: each verb still strikes a creature (by the slot alone, by its id, and through #343's off-map
/// fallback), each proc still lands on one, a cast at nothing still costs what it did (Bolt and the sacrifice strikes
/// still charge for one), the area spells still skip an NPC and strike the creatures around it, and the rogue's Ambush,
/// which spends nothing, still leaps behind an NPC.</para>
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

    /// <summary>One spell per damage verb and per kind of tail, from the book: Bolt and Freeze (<c>magic_damage</c>),
    /// the NPC Ambush (<c>generic</c>), Call Lightning, Assault and Hellfire (<c>arch_damage</c>: plain, the shout and
    /// vita tail, the pool drain), Venom and Burn (<c>venom</c>), and Whirlwind and Desperate Attack
    /// (<c>sacrifice</c>).</summary>
    private static readonly string[] BookSpells =
    {
        "bolt_mage", "freeze", "ambush", "call_lightning_mage", "assault_warrior", "hellfire_mage",
        "venom_mage", "burn", "whirlwind_warrior", "desperate_attack_rogue",
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

    public static TheoryData<string, string, ushort> ProcCases
    {
        get
        {
            var data = new TheoryData<string, string, ushort>();
            foreach (var map in new[] { Arena, Town })
                foreach (var (item, spell) in new[] { ("poet_whip", "freeze"), ("blood", "venom"), ("flamefang", "burn") })
                    data.Add(item, spell, map);
            return data;
        }
    }

    /// <summary>The stock client's procs, through real swings at a faced NPC, cost nothing: three procs or more fired
    /// (each one's own log line, naming the NPC's id), and the caster kept every point of mana and HP and heard no
    /// shout. The Poet whip's Freeze says nothing; the Venom and Burn procs say Venom's "It doesn't work.", once a
    /// proc, as the Charm weapons' Endear proc and the Forsaken staff's Blind proc already do at an NPC. The NPC is
    /// untouched and carries no venom.
    /// <para>Red on 63efdd8: Freeze cost 800 a proc; the first Venom or Burn proc cost 60 or 200 and venomed the NPC,
    /// and each later one said "You already cast that spell.".</para></summary>
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
            bool venom = spell != "freeze";
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

    // ===== the controls ================================================================================

    public static TheoryData<string, ushort, Aim> CreatureCases
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
    /// costs no mana; the swing it fires on costs the spell's mana and strikes the creature (Freeze) or venoms it
    /// (Venom, Burn). The creature's HP is out of the swings' reach.
    /// <para>Green on 63efdd8 as well.</para></summary>
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
            for (int swings = 1; swings <= MaxSwings; swings++)
            {
                uint mp0 = cc.Mp;
                int procsBefore = Procs(log, item, spell, mob.Id);
                Swing(caster);
                int procs = Procs(log, item, spell, mob.Id) - procsBefore;
                if (procs == 0)
                {
                    Assert.True(cc.Mp == mp0, $"{item} on map {map}, swing {swings}: no proc, yet mana {mp0} -> {cc.Mp}");
                    continue;
                }
                string what = $"{item} on map {map}, swing {swings}: spent {mp0 - cc.Mp} (the spell costs {cost}), " +
                              $"the creature on {mob.Hp}, carrying [{State(mob)}]";
                Assert.True(mp0 - cc.Mp == cost, what);
                Assert.True(spell == "freeze" ? mob.Hp < Creature : State(mob).Contains("venoms", StringComparison.Ordinal), what);
                return;
            }
            Assert.Fail($"{item} on map {map}: no proc in {MaxSwings} swings");
        }
        finally
        {
            _fx.World.LeaveMap(caster, map);
            _fx.World.DespawnMob(map, mob);
        }
    }

    public static TheoryData<string, ushort, string, int, int, int> NothingCases => new()
    {
        // key, map, the line, mana, pose, shout
        { "bolt_mage", Town, "You cast Bolt.", 4, 1, 0 },
        { "freeze", Town, "You cast Freeze.", 800, 1, 0 },
        { "whirlwind_warrior", Town, "You cast Whirlwind.", 120, 1, 1 },
        { "whirlwind_warrior", Arena, "You cast Whirlwind.", 120, 1, 1 },
    };

    /// <summary>The control for "only an NPC": a cast at nothing costs what it did. <c>magic_damage</c> charges for a
    /// blow at nothing off a PvP map, and a sacrifice strike charges its mana and aether for nothing in front (RTK's
    /// order, kept on purpose; see the verb). Neither changes here: only an NPC is refused for free.
    /// <para>Green on 63efdd8 as well.</para></summary>
    [Theory]
    [MemberData(nameof(NothingCases))]
    public void ACastAtNothingCostsWhatItDid(string key, ushort map, string line, int mana, int pose, int shout)
    {
        var sp = Spell(key);
        var r = Cast(sp, map, Aim.Slot, npc: null);
        Assert.True(r.Lines == line && r.Spent == mana && r.HpDelta == 0 && r.Pose == pose && r.Speech == shout,
                    $"{sp.Name} at nothing on map {map}: {r}");
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

    // ===== the cast ======================================================================================

    private const int Creature = 10_000_000;

    private sealed record Outcome(string Lines, long Spent, int HpDelta, int Pose, int Speech, int Fx, long Aether,
                                  bool Moved, int TargetHp, string TargetState, uint FarHp, string FarLines);

    /// <summary>A fresh caster on <paramref name="map"/> faces a fresh NPC (<paramref name="npc"/> true), a fresh
    /// creature (false) or nothing (null), with a fresh player on a content-free map, and casts <paramref name="sp"/>
    /// from slot 0 at <paramref name="aim"/>. Returns what the caster saw, spent and kept, and what the target and the
    /// other player kept.</summary>
    private Outcome Cast(SpellDef sp, ushort map, Aim aim, bool? npc)
    {
        Mob? target = npc switch { true => Npc(map, CX + 1), false => CreatureAt(map, CX + 1), null => null };
        var (far, farOut, farc) = Player("NdvFar", Elsewhere);
        var (caster, cOut, cc) = Caster(map, ch => ch.Spells.Add(sp.Id));
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
                               cOut.BodiesOf(ServerOp.Effect).Count, SpellCastSupport.AetherLeft(caster, sp.Key),
                               cc.X != CX || cc.Y != CY, target?.Hp ?? 0, target is null ? "" : State(target),
                               farc.Hp, string.Join(" | ", SpellCastSupport.MiniTexts(farOut)));
        }
        finally
        {
            _fx.World.LeaveMap(caster, map);
            _fx.World.LeaveMap(far, Elsewhere);
            if (target is { IsNpc: true }) Remove(map, target);
            else if (target is not null) _fx.World.DespawnMob(map, target);
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
    /// (game-data/WeaponProcs.csv), and knowing no spell.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Swinger(ushort map, string item, string spell)
    {
        var def = Content.ItemByKey(item);
        Assert.NotNull(def);
        Assert.Equal(spell, Content.WeaponProcFor(item)?.Spell);
        var s = Caster(map, ch => ch.Equipment.Add(new InvItem { Slot = 1, ItemId = def!.Id }));
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

    /// <summary>The arena carries its own size if its row gives one; content-free maps have none, and the handlers
    /// need one.</summary>
    private static void Wide(Character c, ushort map)
    {
        if (Content.Maps.TryGetValue(map, out var m) && m.Xs > 0) { c.MapXs = m.Xs; c.MapYs = m.Ys; }
        else { c.MapXs = 100; c.MapYs = 100; }
    }
}
