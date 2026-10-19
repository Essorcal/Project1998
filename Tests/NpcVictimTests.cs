using System.Reflection;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Drain and Kamikaze take no NPC as their victim (#348's review, F1). The damage resolver
/// (<c>Session.ResolveDamageTarget</c>) takes any living mob on the faced tile or by its id, and an NPC is a mob, so
/// the two verbs read one as their victim. But the blow cannot hurt it: <c>World.TryDamage</c> refuses an NPC. So:
/// <list type="bullet">
/// <item><b>Drain at a faced NPC</b>: "You cast Drain.", 60 mana, the pose, the spell's animation over the NPC, and the
/// caster healed by the NPC's HP (1 for an NPC placed from NPCs.csv). The NPC is untouched.</item>
/// <item><b>Kamikaze at a faced NPC</b>: "You cast Kamikaze.", 120 mana, the pose, "Kamikaze~!", and the caster left on
/// 10 HP. The NPC is untouched.</item>
/// <item><b>The stock client reaches the Kamikaze row</b> without sending an id: a swing fires the weapon procs whatever
/// is faced, and the Ilbon knife's 4% proc (<c>game-data/WeaponProcs.csv</c>) casts Kamikaze at the faced NPC's id.
/// Each proc cost 120 mana, the shout, and the caster's HP down to 10.</item>
/// </list>
///
/// <para>A victim the blow cannot hurt is now no victim (<c>Session.LuaDamageVictim</c>), so the verbs refuse it as they
/// refuse no creature at all, before anything is spent: Drain with "It doesn't work.", Kamikaze silently.</para>
///
/// <para>The control: a Kamikaze proc at a creature still lands. The creature rows (the slot alone, a creature's id,
/// #343's off-map fallback) are <see cref="DrainKamikazeVictimTests"/>' controls and are not repeated here. The other
/// damage verbs at an NPC (Bolt, the <c>generic</c> verb, the Damage archetype and the rest), which this class pinned
/// unchanged, now refuse one too: see <see cref="NpcDamageVerbTests"/>.</para>
///
/// <para>Every cast is the real 0x0F frame through <c>Session.Receive</c>, and every swing the real 0x13 frame, by a fresh
/// caster facing a fresh target. The NPC is built as <c>World.PlaceNpc</c> builds one: 1 HP, <c>IsNpc</c>, no Will and
/// no Protection. The arena is a real PvP map with no spawns and no NPCs; the other two maps are content-free, so PvP is
/// off on them. No other class uses any of the three.</para>
/// </summary>
[Collection("world")]
public sealed class NpcVictimTests
{
    /// <summary>Yi-chook Arena (Maps.csv MapPvP 1, MapSpells 1). No spawns and no NPCs.</summary>
    private const ushort Arena = 3032;
    /// <summary>Content-free: no Maps.csv row, so <c>Content.IsPvpMap</c> is false, as it is in every town.</summary>
    private const ushort Town = 62501, Elsewhere = 62502;

    private const ushort CX = 10, CY = 10;      // where every caster stands, facing east at (CX + 1, CY)
    private const uint Hp = 5_000, MaxHp = 10_000, Mana = 50_000;
    private const int IlbonKnife = 48034;        // Items.csv; its on_swing proc is Kamikaze at 4%
    /// <summary>Kamikaze's blast at the caster's 5000 HP: ceil(5000 × 1.75) (SpellParams.csv's coeff), netted through a
    /// test creature's AC of 0.</summary>
    private const int Blast = 8_750;
    /// <summary>A bound on the swings a proc row may take. At 4% a swing, 2000 swings without three procs is a chance
    /// of about 1 in 10^30.</summary>
    private const int MaxSwings = 2_000;

    private static readonly FieldInfo NextActionTick =
        typeof(Session).GetField("_nextActionTick", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>What the cast is aimed at: the slot alone (the stock client's frame), the NPC's own id (what the proc
    /// passes), or the id of a player on another map, which #343 sends to the faced tile.</summary>
    public enum Aim { Slot, NpcId, OffMapPlayerId }

    private readonly SessionFixture _fx;
    private static int _serial;

    public NpcVictimTests(SessionFixture fx) => _fx = fx;

    public static TheoryData<string, ushort, Aim> RefusedCases
    {
        get
        {
            var data = new TheoryData<string, ushort, Aim>();
            foreach (var map in new[] { Arena, Town })
            {
                // The four book spells on the drain verb (SpellParams.csv) and Kamikaze, by the stock frame.
                foreach (var key in new[] { "drain_rogue", "drink_of_souls_rogue", "parasite_rogue", "absorb_rogue", "kamikaze" })
                    data.Add(key, map, Aim.Slot);
                foreach (var key in new[] { "drain_rogue", "kamikaze" })
                    foreach (var aim in new[] { Aim.NpcId, Aim.OffMapPlayerId })
                        data.Add(key, map, aim);
            }
            return data;
        }
    }

    /// <summary>With an NPC faced, Drain and Kamikaze refuse and charge nothing: Drain says "It doesn't work." and
    /// nothing else, Kamikaze says nothing. No pose, no shout, no animation, no mana, the caster's HP as it was, the NPC
    /// untouched, and the player on the other map neither hit nor told anything.
    /// <para>Red on c7878f2 for all eighteen. Drain: "You cast Drain.", 60 mana, the pose, its animation over the NPC
    /// and the heal's over the caster, the caster +1. Kamikaze: "You cast Kamikaze.", 120 mana, the pose,
    /// "Kamikaze~!", the caster on 10 HP.</para></summary>
    [Theory]
    [MemberData(nameof(RefusedCases))]
    public void AnNpcIsNoVictimAndTheCastCostsNothing(string key, ushort map, Aim aim)
    {
        var sp = Content.SpellByKey(key);
        Assert.NotNull(sp);
        Assert.Equal(map == Arena, Content.IsPvpMap(map));
        var r = Cast(sp!, map, aim);
        string expectLines = key == "kamikaze" ? "" : "It doesn't work.";
        Assert.True(r.Lines == expectLines && r.Spent == 0 && r.HpDelta == 0 && r.Pose == 0 && r.Speech == 0 && r.Fx == 0,
                    $"{sp!.Name} at {aim} on map {map}, an NPC faced: {r}");
        Assert.True(r.NpcHp == 1 && r.FarHp == Hp && r.FarLines == "", $"{sp.Name} at {aim} on map {map}: {r}");
    }

    /// <summary>The Ilbon knife's Kamikaze proc, through real swings at a faced NPC, costs nothing: three procs or more
    /// fired (each one's own log line, naming the NPC's id), and the caster kept every point of mana and HP, heard no
    /// shout and saw no line. The NPC is untouched.
    /// <para>Red on c7878f2: every proc cost 120 mana and the shout, and the first left the caster on 10 HP.</para></summary>
    [Theory]
    [InlineData(Arena)]
    [InlineData(Town)]
    public void TheIlbonKnifesKamikazeProcAtAnNpcCostsNothing(ushort map)
    {
        var npc = Npc(map);
        var (caster, cOut, cc) = Swinger(map);
        try
        {
            using var log = LogLineSink.Acquire();
            int procs = 0, swings = 0, speech = 0;
            var lines = new List<string>();
            while (procs < 3 && swings < MaxSwings)
            {
                cOut.Clear();
                Swing(caster);
                swings++;
                procs = Procs(log, npc.Id);
                speech += cOut.BodiesOf(ServerOp.Speech).Count;
                lines.AddRange(SpellCastSupport.MiniTexts(cOut));
            }
            string what = $"map {map}: {procs} procs in {swings} swings; spent {Mana - cc.Mp}, hp {cc.Hp}, " +
                          $"speech {speech}, lines [{string.Join(" | ", lines)}], npc hp {npc.Hp}";
            Assert.True(procs >= 3, what);
            Assert.True(cc.Mp == Mana && cc.Hp == Hp && speech == 0 && lines.Count == 0, what);
            Assert.True(npc.Hp == 1, what);
        }
        finally
        {
            _fx.World.LeaveMap(caster, map);
            Remove(map, npc);
        }
    }

    /// <summary>The control: the same proc, through real swings at a faced creature, still lands. Until it fires a swing
    /// costs no mana; the swing it fires on costs 120, shouts, takes the blast off the creature and leaves the caster on
    /// 10 HP. The creature's HP is out of the swings' reach, so only the proc decides the row.
    /// <para>Green on c7878f2 as well.</para></summary>
    [Theory]
    [InlineData(Arena)]
    [InlineData(Town)]
    public void TheIlbonKnifesKamikazeProcAtACreatureStillLands(ushort map)
    {
        var mob = new Mob(_fx.World.AllocateMobId(), 1, CX + 1, CY, $"Npv creature {Interlocked.Increment(ref _serial)}", 10_000_000);
        _fx.World.AddMob(map, mob);
        var (caster, cOut, cc) = Swinger(map);
        try
        {
            using var log = LogLineSink.Acquire();
            for (int swings = 1; swings <= MaxSwings; swings++)
            {
                cOut.Clear();
                uint mp0 = cc.Mp, hp0 = cc.Hp;
                int mobHp0 = mob.Hp;
                Swing(caster);
                int procs = Procs(log, mob.Id);
                if (procs == 0)
                {
                    Assert.True(cc.Mp == mp0 && cc.Hp == hp0, $"map {map}, swing {swings}: no proc, yet mana {mp0} -> {cc.Mp}, hp {hp0} -> {cc.Hp}");
                    continue;
                }
                string what = $"map {map}, swing {swings} ({procs} proc): spent {mp0 - cc.Mp}, hp {cc.Hp}, " +
                              $"speech {cOut.BodiesOf(ServerOp.Speech).Count}, the creature {mobHp0} -> {mob.Hp}";
                Assert.True(procs == 1 && mp0 - cc.Mp == 120 && cc.Hp == 10 && cOut.BodiesOf(ServerOp.Speech).Count >= 1, what);
                Assert.True(mobHp0 - mob.Hp >= Blast, what);
                return;
            }
            Assert.Fail($"map {map}: no proc in {MaxSwings} swings");
        }
        finally
        {
            _fx.World.LeaveMap(caster, map);
            _fx.World.DespawnMob(map, mob);
        }
    }

    // ===== the cast ======================================================================================

    private sealed record Outcome(string Lines, uint Spent, int HpDelta, int Pose, int Speech, int Fx, int FxOverNpc,
                                  int NpcHp, uint FarHp, string FarLines);

    /// <summary>A fresh caster on <paramref name="map"/> faces a fresh NPC, with a fresh player on a content-free map,
    /// and casts <paramref name="sp"/> from slot 0 at <paramref name="aim"/>. Returns what the caster saw, spent and kept,
    /// and what the NPC and the other player kept.</summary>
    private Outcome Cast(SpellDef sp, ushort map, Aim aim)
    {
        var npc = Npc(map);
        var (far, farOut, farc) = Player("NpvFar", Elsewhere);
        var (caster, cOut, cc) = Caster(map, ch => ch.Spells.Add(sp.Id));
        try
        {
            uint? target = aim switch
            {
                Aim.Slot => null,
                Aim.NpcId => npc.Id,
                Aim.OffMapPlayerId => far.PlayerId,
                _ => throw new ArgumentOutOfRangeException(nameof(aim)),
            };
            uint mp0 = cc.Mp, hp0 = cc.Hp;
            cOut.Clear(); farOut.Clear();

            caster.Receive(SpellCastSupport.CastFrame(0, target));

            var fx = cOut.BodiesOf(ServerOp.Effect);
            return new Outcome(string.Join(" | ", SpellCastSupport.MiniTexts(cOut)), mp0 - cc.Mp, (int)cc.Hp - (int)hp0,
                               cOut.BodiesOf(ServerOp.Action).Count, cOut.BodiesOf(ServerOp.Speech).Count,
                               fx.Count, fx.Count(b => b.Length >= 4 && EntityId(b) == npc.Id),
                               npc.Hp, farc.Hp, string.Join(" | ", SpellCastSupport.MiniTexts(farOut)));
        }
        finally
        {
            _fx.World.LeaveMap(caster, map);
            _fx.World.LeaveMap(far, Elsewhere);
            Remove(map, npc);
        }
    }

    // ===== helpers =======================================================================================

    /// <summary>An NPC on the tile every caster faces, built as <c>World.PlaceNpc</c> builds one from NPCs.csv.</summary>
    private Mob Npc(ushort map)
    {
        Assert.Null(_fx.World.MobAt(map, CX + 1, CY));
        var npc = new Mob(_fx.World.AllocateMobId(), 1, CX + 1, CY, $"Npv npc {Interlocked.Increment(ref _serial)}", hp: 1)
        {
            IsNpc = true, Dir = 3,
        };
        _fx.World.AddMob(map, npc);
        return npc;
    }

    /// <summary><c>World.DespawnMob</c> refuses an NPC, so the test clears the flag first and checks the tile is empty
    /// for the next row.</summary>
    private void Remove(ushort map, Mob npc)
    {
        npc.IsNpc = false;
        Assert.True(_fx.World.DespawnMob(map, npc));
        Assert.Null(_fx.World.MobAt(map, CX + 1, CY));
    }

    /// <summary>One swing: the real 0x13 frame, after freeing the shared cast/swing slot the last swing armed
    /// (<c>Session.ArmActionSlot</c>), so the swings need not wait out the swing interval.</summary>
    private static void Swing(Session s)
    {
        s.WithState(() => NextActionTick.SetValue(s, 0L));
        s.Receive(SessionFixture.Frame(ClientOp.Attack, new byte[] { 0 }));
    }

    /// <summary>How many Kamikaze procs <c>Session.FireWeaponProcs</c> has logged at <paramref name="targetId"/>. The
    /// line is written as the proc fires, before its cast runs.</summary>
    private static int Procs(LogLineSink log, uint targetId)
    {
        string needle = $"weapon proc: ilbon_knife (4%) casts kamikaze on {targetId}";
        return log.Lines.Count(l => l.Line.TrimEnd().EndsWith(needle, StringComparison.Ordinal));
    }

    private static uint EntityId(byte[] b) => (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);

    /// <summary>A level-99 caster at half its HP with plenty of mana and Will 255, facing east.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Caster(ushort map, Action<Character> more) =>
        _fx.PlayerWith($"NpvCaster{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 99;
            ch.MaxHp = MaxHp; ch.Hp = Hp;
            ch.MaxMp = Mana; ch.Mp = Mana;
            ch.Will = 255;
            ch.Dir = 1;
            Wide(ch, map);
            more(ch);
        }, map, CX, CY);

    /// <summary>The same caster, wielding the Ilbon knife and knowing no spell.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Swinger(ushort map)
    {
        var s = Caster(map, ch => ch.Equipment.Add(new InvItem { Slot = 1, ItemId = IlbonKnife }));
        Assert.Equal("ilbon_knife", Content.ItemById(IlbonKnife)?.Key);
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

    /// <summary>The arena carries its own size if it has a map file; content-free maps have none, and the handlers need
    /// one.</summary>
    private static void Wide(Character c, ushort map)
    {
        if (Content.Maps.TryGetValue(map, out var m)) { c.MapXs = m.Xs; c.MapYs = m.Ys; }
        else { c.MapXs = 100; c.MapYs = 100; }
    }
}
