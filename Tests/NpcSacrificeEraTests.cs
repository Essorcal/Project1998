using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// A sacrifice strike at an NPC strikes nothing at a later <c>EraDate</c> too, where the strikes' overkill is live:
/// warrior overflow (2007-04-10) and rogue overkill (2008-09-18) both spend the damage a killing strike did not need
/// (<c>game-data/EraFeatures.csv</c>, gated in <c>Session.LuaOverflow</c> and <c>Session.LuaBackflow</c>).
///
/// <para><b>What it used to do.</b> <c>Session.LuaSacApply</c> took the overkill off the NPC's 1 HP although
/// <c>World.TryDamage</c> refused the blow, and the verb spent it. At 2008-09-18 a Whirlwind or a Berserk at an NPC
/// splashed thousands onto the creatures beside it, and a Lethal Strike or a Desperate Attack refunded its own HP cost
/// and handed back half the pool (#351's review, hunt 1). At the shipped date (2001-07-09) both are off, so only the
/// strike's own cost showed; <see cref="NpcDamageVerbTests"/> pins that date.</para>
///
/// <para><b>Now</b> an NPC in front is nothing in front, RTK's reading, as Caleb chose (2026-10-19): the strike costs
/// what a strike at nothing costs and strikes nothing, so there is no overkill to spend.</para>
///
/// <para><b>Why a collection that runs alone.</b> The era date is read from <c>ServerTuning.csv</c> through
/// <c>EraCalendar</c>, one calendar for the whole process. Swapping it is a stubbed content snapshot
/// (<see cref="TestSeamCollectionTests"/>), so this class sits in <c>"tile-translation"</c>, which runs alone, and holds
/// <c>TestProcessState.Gate</c> across the swap, as <see cref="EraGatingTests"/> does. It builds its own
/// <see cref="SessionFixture"/>, so its world is its own; its maps (2593, a PvP arena with no spawns and no NPCs, and the
/// content-free 62505) are used by no other class.</para>
/// </summary>
[Collection("tile-translation")]
public sealed class NpcSacrificeEraTests : IClassFixture<SessionFixture>
{
    /// <summary>Stone pit (Maps.csv MapPvP 1, MapSpells 1). No spawns and no NPCs.</summary>
    private const ushort Arena = 2593;
    /// <summary>Content-free: no Maps.csv row, so PvP is off.</summary>
    private const ushort Town = 62505;

    private const ushort CX = 10, CY = 10;      // where every caster stands, facing east at (CX + 1, CY)
    private const uint Hp = 5_000, MaxHp = 10_000, Mana = 50_000;
    private const int Creature = 10_000_000;
    /// <summary>Rogue overkill's own date; warrior overflow's (2007-04-10) has passed by then.</summary>
    private const int LaterEra = 20080918;

    /// <summary>The faced tile's three other neighbours, where an overflow off the faced tile lands (the fourth is the
    /// caster's own tile).</summary>
    private static readonly (ushort x, ushort y)[] Beside = { (CX + 1, CY - 1), (CX + 1, CY + 1), (CX + 2, CY) };

    private readonly SessionFixture _fx;
    private static int _serial;

    public NpcSacrificeEraTests(SessionFixture fx) => _fx = fx;

    public static TheoryData<string, ushort> Cases
    {
        get
        {
            var data = new TheoryData<string, ushort>();
            foreach (var map in new[] { Arena, Town })
                foreach (var key in new[] { "whirlwind_warrior", "berserk_warrior", "lethal_strike_rogue", "desperate_attack_rogue" })
                    data.Add(key, map);
            return data;
        }
    }

    /// <summary>At 2008-09-18, a strike at a faced NPC, with three creatures around the NPC, costs exactly what the same
    /// strike costs with nothing in front and the same three creatures there: the same lines, mana, aether, swing and
    /// shout, the caster's HP and mana where the cost left them, nothing drawn and no HP bar. Every creature keeps every
    /// point in both, and the NPC is untouched.
    /// <para>Red on 63efdd8: Whirlwind and Berserk splash the creatures, Lethal Strike and Desperate Attack give their
    /// cost back. Red on 1f08f20 too, which refused the NPC for free.</para></summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void AtALaterEraAStrikeAtAnNpcStillStrikesNothing(string key, ushort map)
    {
        var sp = Content.SpellByKey(key);
        Assert.NotNull(sp);
        WithEraDate(LaterEra, () =>
        {
            Assert.True(Era.Has(Era.WarriorOverflow) && Era.Has(Era.RogueOverkill), "the later era should have both");
            var atNpc = Strike(sp!, map, npc: true);
            var atNothing = Strike(sp!, map, npc: false);
            string what = $"{sp!.Name} on map {map} at {LaterEra}: at an NPC {atNpc}; at nothing {atNothing}";
            Assert.True(atNothing.Lines == $"You cast {sp.Name}." && atNothing.Spent > 0 && atNothing.Aether > 0, what);
            Assert.True(atNpc.Lines == atNothing.Lines && atNpc.Spent == atNothing.Spent && atNpc.HpDelta == atNothing.HpDelta
                        && atNpc.Pose == atNothing.Pose && atNpc.Speech == atNothing.Speech && atNpc.Fx == atNothing.Fx
                        && atNpc.Bars == atNothing.Bars && Math.Abs(atNpc.Aether - atNothing.Aether) < 2_000, what);
            Assert.True(atNpc.Creatures == atNothing.Creatures && atNpc.Creatures == $"{Creature},{Creature},{Creature}"
                        && atNpc.NpcHp == 1, what);
        });
    }

    // ===== the strike ====================================================================================

    private sealed record Outcome(string Lines, long Spent, int HpDelta, int Pose, int Speech, int Fx, int Bars,
                                  long Aether, string Creatures, int NpcHp);

    /// <summary>A fresh caster on <paramref name="map"/>, facing an NPC (<paramref name="npc"/>) or nothing, with three
    /// fresh creatures on the faced tile's other neighbours, casts <paramref name="sp"/> from slot 0 by the slot alone.
    /// Returns what the caster saw, spent and kept, and what the creatures and the NPC kept.</summary>
    private Outcome Strike(SpellDef sp, ushort map, bool npc)
    {
        var mobs = new List<Mob>();
        foreach (var (x, y) in Beside)
        {
            Assert.Null(_fx.World.MobAt(map, x, y));
            var m = new Mob(_fx.World.AllocateMobId(), 1, x, y, $"Nse creature {Interlocked.Increment(ref _serial)}", Creature);
            _fx.World.AddMob(map, m);
            mobs.Add(m);
        }
        Mob? target = null;
        if (npc)
        {
            Assert.Null(_fx.World.MobAt(map, CX + 1, CY));
            target = new Mob(_fx.World.AllocateMobId(), 1, CX + 1, CY, $"Nse npc {Interlocked.Increment(ref _serial)}", hp: 1)
            {
                IsNpc = true, Dir = 3,
            };
            _fx.World.AddMob(map, target);
        }
        var (caster, cOut, cc) = _fx.PlayerWith($"NseCaster{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 99;
            ch.MaxHp = MaxHp; ch.Hp = Hp;
            ch.MaxMp = Mana; ch.Mp = Mana;
            ch.Will = 255;
            ch.Dir = 1;
            if (Content.Maps.TryGetValue(map, out var mi) && mi.Xs > 0) { ch.MapXs = mi.Xs; ch.MapYs = mi.Ys; }
            else { ch.MapXs = 100; ch.MapYs = 100; }
            ch.Spells.Add(sp.Id);
        }, map, CX, CY);
        try
        {
            uint mp0 = cc.Mp, hp0 = cc.Hp;
            cOut.Clear();
            caster.Receive(SpellCastSupport.CastFrame(0));
            return new Outcome(string.Join(" | ", SpellCastSupport.MiniTexts(cOut)), (long)mp0 - cc.Mp, (int)cc.Hp - (int)hp0,
                               cOut.BodiesOf(ServerOp.Action).Count, cOut.BodiesOf(ServerOp.Speech).Count,
                               cOut.BodiesOf(ServerOp.Effect).Count, cOut.BodiesOf(ServerOp.Damage).Count,
                               SpellCastSupport.AetherLeft(caster, sp.Key), string.Join(",", mobs.Select(m => m.Hp)),
                               target?.Hp ?? 0);
        }
        finally
        {
            _fx.World.LeaveMap(caster, map);
            if (target is not null)
            {
                target.IsNpc = false;      // World.DespawnMob refuses an NPC
                Assert.True(_fx.World.DespawnMob(map, target));
            }
            foreach (var m in mobs)
            {
                if (m.Hp <= 0) m.Hp = 1;   // a splash that killed one leaves it for DespawnMob to take
                _fx.World.DespawnMob(map, m);
            }
            Assert.Null(_fx.World.MobAt(map, CX + 1, CY));
        }
    }

    /// <summary>Point the era calendar at a throwaway <c>ServerTuning.csv</c> holding only <paramref name="yyyymmdd"/>,
    /// run <paramref name="body"/>, and put the real calendar back, as <see cref="EraGatingTests"/> does.
    /// <c>EraFeatures.csv</c> stays the real one.</summary>
    private static void WithEraDate(int yyyymmdd, Action body)
    {
        lock (TestProcessState.Gate)
        {
            var dir = Path.Combine(Path.GetTempPath(), "nexus-era-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var tuning = Path.Combine(dir, "ServerTuning.csv");
            File.WriteAllText(tuning, "key,value\nEraDate," + yyyymmdd + "\n");

            var previous = EraCalendar.PathOverrideForTests;
            try
            {
                EraCalendar.PathOverrideForTests = file => file == "ServerTuning.csv" ? tuning : null;
                EraCalendar.Reload();
                body();
            }
            finally
            {
                EraCalendar.PathOverrideForTests = previous;
                EraCalendar.Reload();
                try { Directory.Delete(dir, true); } catch { /* a temp dir; not worth failing a test over */ }
            }
        }
    }
}
