using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Doze and Blind ask whether they may hold their target before they roll, as a curse does. #343's review, F4:
/// <c>arch_debuff</c> (Doze, the mage Blind, Paralyze and kin) and the NPC <c>blind</c> verb rolled their take-hold
/// chance, and charged for a loss, before <c>holdTarget</c>, whose refusals are free. So a Doze at a player in a
/// town answered "You can't attack that target." for nothing about four times in five, and the fifth time charged
/// 30 mana, said "Something went wrong." and "You cast Doze.", played the pose and armed Doze's 36-second aether.
/// A Blind at a player or an NPC, or the NPC Blind at a creature it already holds, did the same, one time in four.
///
/// <para>The verbs now call <c>ctx:canHold</c>, <c>holdTarget</c>'s own refusals asked first, before the deflect and
/// the roll: a player for anything but the Doze family ("It doesn't work."), a player off a PvP map ("You can't
/// attack that target."), an NPC ("It doesn't work."), nothing at all (silent), and an occupied slot. Every one is
/// free, every time. A target the spell may hold still rides the roll, and a lost roll still costs the mana.</para>
///
/// <para>The stock 4.95 client reaches all of this: every cast here is the slot alone, its own frame, through
/// <c>Session.Receive</c>, by a fresh caster at a fresh target. The arena is a real PvP map with no spawns and no
/// NPCs; the town is content-free, so PvP is off there. No other class uses either.</para>
/// </summary>
[Collection("world")]
public sealed class HoldGateBeforeRollTests
{
    /// <summary>Daegu Arena (Maps.csv MapPvP 1, MapSpells 1).</summary>
    private const ushort Arena = 3019;
    /// <summary>Content-free: no Maps.csv row, so <c>Content.IsPvpMap</c> is false, as it is in every town.</summary>
    private const ushort Town = 62483;

    private const ushort CX = 10, CY = 10;   // the caster stands here facing east, at (CX + 1, CY)
    private const uint Hp = 5_000, MaxHp = 10_000, Mana = 50_000;

    /// <summary>A refused row is cast this many times. Doze's roll loses one time in five and Blind's one in four,
    /// so before the fix a run of 40 shows at least one charged loss except about once in 7,500 runs.</summary>
    private const int RefusedCasts = 40;
    /// <summary>A control row is cast this many times; at least one must land.</summary>
    private const int LandingCasts = 10;

    /// <summary>What the caster faces.</summary>
    public enum Faced { Player, Npc, Nothing, HeldCreature, Creature }

    private readonly SessionFixture _fx;
    private static int _serial;

    public HoldGateBeforeRollTests(SessionFixture fx) => _fx = fx;

    public static TheoryData<string, ushort, Faced, string> RefusedCases => new()
    {
        { "doze_mage", Town, Faced.Player, "You can't attack that target." },
        { "blind_mage", Town, Faced.Player, "It doesn't work." },
        { "blind_mage", Arena, Faced.Player, "It doesn't work." },
        { "blind", Town, Faced.Player, "It doesn't work." },
        { "blind", Arena, Faced.Player, "It doesn't work." },
        { "doze_mage", Town, Faced.Npc, "It doesn't work." },
        { "blind_mage", Town, Faced.Npc, "It doesn't work." },
        { "blind", Town, Faced.Npc, "It doesn't work." },
        { "blind", Town, Faced.Nothing, "" },
        { "blind", Arena, Faced.Nothing, "It doesn't work." },   // nothing faced on a PvP map resolves the caster
        { "blind", Town, Faced.HeldCreature, "You already cast that spell." },
    };

    /// <summary>Each target the spell may not hold, cast at <see cref="RefusedCasts"/> times with the slot alone: every
    /// cast gets the refusal and nothing else. No mana, no pose, no aether, and the target is not held and is told
    /// nothing.
    /// <para>Red on 6be6be3 for all eleven: some casts lose the roll first and are charged ("Something went wrong.",
    /// "You cast X.", the pose, and Doze's aether).</para></summary>
    [Theory]
    [MemberData(nameof(RefusedCases))]
    public void ATargetTheSpellMayNotHoldIsRefusedBeforeTheRollAndFree(string key, ushort map, Faced faced, string refusal)
    {
        var sp = Content.SpellByKey(key)!;
        Assert.Equal(map == Arena, Content.IsPvpMap(map));
        var tally = new Dictionary<string, int>();
        for (int i = 0; i < RefusedCasts; i++)
        {
            var o = Cast(sp, map, faced);
            tally[o.ToString()] = tally.GetValueOrDefault(o.ToString()) + 1;
        }
        var want = new Outcome(refusal, 0, 0, false, false, "");
        Assert.True(tally.Count == 1 && tally.ContainsKey(want.ToString()),
                    $"{sp.Name} at {faced} on map {map}, {RefusedCasts} casts: " +
                    string.Join("; ", tally.Select(kv => $"{kv.Value} x {kv.Key}")));
    }

    public static TheoryData<string, ushort, Faced> LandingCases => new()
    {
        { "doze_mage", Arena, Faced.Player },
        { "doze_mage", Town, Faced.Creature },
        { "blind_mage", Town, Faced.Creature },
        { "blind", Town, Faced.Creature },
    };

    /// <summary>The control: a target the spell may hold still rides the roll. Each cast either holds it, or loses the
    /// roll ("Something went wrong."); both cost the mana and get "You cast X." and the pose. At least one of
    /// <see cref="LandingCasts"/> holds.
    /// <para>Green on 6be6be3 as well.</para></summary>
    [Theory]
    [MemberData(nameof(LandingCases))]
    public void ATargetTheSpellMayHoldStillRidesTheRoll(string key, ushort map, Faced faced)
    {
        var sp = Content.SpellByKey(key)!;
        int mana = key == "blind" ? 300 : Content.FxFor(sp)!.Mana;
        bool aether = Content.FxFor(sp) is { Aether: > 0 };
        var held = new Outcome($"You cast {sp.Name}.", (uint)mana, 1, aether, true, "");
        var lost = new Outcome($"Something went wrong. | You cast {sp.Name}.", (uint)mana, 1, aether, false, "");
        int landed = 0;
        for (int i = 0; i < LandingCasts; i++)
        {
            var o = Cast(sp, map, faced);
            // A player held by another player's Doze is told so; a creature is told nothing.
            if (o.Held) { landed++; Assert.True(o with { TargetLines = "" } == held, $"{sp.Name} at {faced} on map {map}: {o}"); }
            else Assert.True(o == lost, $"{sp.Name} at {faced} on map {map}: {o}");
        }
        Assert.True(landed > 0, $"{sp.Name} at {faced} on map {map}: none of {LandingCasts} held");
    }

    // ===== the cast ======================================================================================

    private sealed record Outcome(string Lines, uint Spent, int Pose, bool Aether, bool Held, string TargetLines);

    /// <summary>A fresh caster on <paramref name="map"/> faces a fresh <paramref name="faced"/> and casts
    /// <paramref name="sp"/> from slot 0 with the slot alone. Returns what the caster saw and spent, whether the
    /// spell's aether is running, whether the target is now held, and what the target was told.</summary>
    private Outcome Cast(SpellDef sp, ushort map, Faced faced)
    {
        string cat = sp.Key == "blind" ? "blinds" : Content.FxFor(sp)!.Debuff switch
        {
            "sleep" => "sleeps", "blind" => "blinds", "paralyze" => "paras", _ => "slows",
        };
        Mob? mob = null;
        Session? player = null;
        RecordingOutbound? pOut = null;
        if (faced is Faced.Npc or Faced.HeldCreature or Faced.Creature)
        {
            mob = new Mob(_fx.World.AllocateMobId(), 1, CX + 1, CY, $"Hgr creature {Interlocked.Increment(ref _serial)}", 50_000)
            { IsNpc = faced == Faced.Npc };
            _fx.World.AddMob(map, mob);
            if (faced == Faced.HeldCreature) Assert.True(_fx.World.ApplyMobStatus(mob, cat, 600_000, false, true, spellKey: sp.Key));
        }
        else if (faced == Faced.Player)
        {
            (player, pOut, _) = _fx.PlayerWith($"HgrFaced{Interlocked.Increment(ref _serial)}", ch =>
            {
                ch.Level = 50;
                ch.MaxHp = MaxHp; ch.Hp = Hp;
                Wide(ch, map);
            }, map, CX + 1, CY);
        }
        var (caster, cOut, cc) = _fx.PlayerWith($"HgrCaster{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 99;
            ch.MaxHp = MaxHp; ch.Hp = Hp;
            ch.MaxMp = Mana; ch.Mp = Mana;
            ch.Will = 255;
            ch.Dir = 1;
            Wide(ch, map);
            ch.Spells.Add(sp.Id);
        }, map, CX, CY);
        try
        {
            uint mp0 = cc.Mp;
            cOut.Clear();
            pOut?.Clear();

            caster.Receive(SpellCastSupport.CastFrame(0));

            bool held = faced switch
            {
                Faced.Player => player!.Asleep || SpellCastSupport.BuffEntry(player, sp.Key) is not null,
                Faced.Creature => _fx.World.MobStatusKey(mob!, cat) == sp.Key,
                Faced.Npc => _fx.World.MobHasStatus(mob!, cat),
                Faced.Nothing => caster.Asleep || SpellCastSupport.BuffEntry(caster, sp.Key) is not null,
                _ => false,   // a held creature was held before the cast; the question is only what the cast cost
            };
            return new Outcome(string.Join(" | ", SpellCastSupport.MiniTexts(cOut)), mp0 - cc.Mp,
                               cOut.BodiesOf(ServerOp.Action).Count, SpellCastSupport.AetherLeft(caster, sp.Key) > 0, held,
                               pOut is null ? "" : string.Join(" | ", SpellCastSupport.MiniTexts(pOut)));
        }
        finally
        {
            _fx.World.LeaveMap(caster, map);
            if (player is not null) _fx.World.LeaveMap(player, map);
            if (mob is not null)
            {
                mob.IsNpc = false;   // World.DespawnMob refuses an NPC, and one left standing would be the next cast's target
                Assert.True(_fx.World.DespawnMob(map, mob), $"{mob.Name} was not despawned");
            }
        }
    }

    /// <summary>The arena carries its own size; content-free maps have none, and the cast handler needs one.</summary>
    private static void Wide(Character c, ushort map)
    {
        if (Content.Maps.TryGetValue(map, out var m)) { c.MapXs = m.Xs; c.MapYs = m.Ys; }
        else { c.MapXs = 100; c.MapYs = 100; }
    }
}
