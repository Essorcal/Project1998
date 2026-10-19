using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// A hostile status aimed by id at a player on another map, with nothing faced, no longer falls back to its caster.
/// This is M5 of #343: the third of the alternatives to #343's fallback that its author pinned with a mutant, and
/// the one its review found to be the only one that stops "aim at someone who just left the map, poison yourself".
/// Caleb sent it to this slice to be considered ("M5 is considered there"); whether it ships is his call at merge.
///
/// <para>#343 answered such a cast, with nothing faced, as it answers an id nobody has. In the buff resolver that is
/// its last line, which gives the caster to a hostile status on a PvP map (<c>selfIfUnaimedInPvp</c>). So in an arena
/// a Venom aimed at a player who had just left poisoned its caster, for 60 mana, and a Vex cursed its caster or
/// fizzled, for 60, both with "You cast X." and the pose. That fallback exists for a cast aimed at nothing; this one
/// was aimed, at someone who is not here.</para>
///
/// <para>Now <c>ResolveTargetBuff</c> keeps that line for a cast with no id and for an id nobody has, and skips it
/// for the id of a player on another map. Venom answers "It doesn't work.", its line for no target. A curse and the
/// NPC Blind say nothing. All are free. Heal is untouched: it heals its caster through its own answer to no target,
/// not the resolver's line. A Doze already said nothing here, since <c>arch_debuff</c> asks <c>targetKind</c>, which
/// never takes that line.</para>
///
/// <para>Only a client that sends a target id reaches this. The stock 4.95 client casts with the slot alone, and its
/// weapon procs pass the id of what it faces on its own map.</para>
///
/// <para>Every cast is the real 0x0F frame through <c>Session.Receive</c>, by a fresh caster facing an empty tile. The
/// arena is a real PvP map with no spawns and no NPCs; the far map is content-free. No other class uses either.</para>
/// </summary>
[Collection("world")]
public sealed class OffMapIdNoCasterFallbackTests
{
    /// <summary>Haunted Arena (Maps.csv MapPvP 1, MapSpells 1).</summary>
    private const ushort Arena = 3016;
    /// <summary>Content-free: no Maps.csv row, so PvP is off there.</summary>
    private const ushort Far = 62484;

    private const ushort CX = 10, CY = 10;      // the caster stands here facing north, at the empty (CX, CY - 1)
    private const uint NobodyId = 0x7FFF_FFD0;   // an id nobody online has
    private const uint Hp = 5_000, MaxHp = 10_000, Mana = 50_000;

    private readonly SessionFixture _fx;
    private static int _serial;

    public OffMapIdNoCasterFallbackTests(SessionFixture fx) => _fx = fx;

    /// <summary>Aimed at the id of a player on another map, from an arena, with nothing faced: the refusal and nothing
    /// else, no mana, no pose, and the caster carries nothing new. The far player is untouched. Then the contrast,
    /// unchanged: the same cast at an id nobody has still resolves the caster, as the slot alone does. A Venom poisons
    /// them for 60. A Vex costs 60 and the pose, whether it takes or fizzles. The NPC Blind answers "It doesn't work.",
    /// free, since a player is not something it holds.
    /// <para>Red on 6be6be3 for all three: the off-map id resolves the caster too. A Venom poisons them, a Vex costs
    /// 60, and the NPC Blind answers "It doesn't work." (and before this slice's gate, one time in four, "Something
    /// went wrong." for 300).</para></summary>
    [Theory]
    [InlineData("venom_mage", "It doesn't work.")]
    [InlineData("vex_mage", "")]
    [InlineData("blind", "")]
    public void AHostileStatusAimedAtAPlayerOnAnotherMapNeverFallsToTheCaster(string key, string refusal)
    {
        var sp = Content.SpellByKey(key)!;
        Assert.True(Content.IsPvpMap(Arena));
        Assert.False(Content.IsPvpMap(Far));

        var offMap = Cast(sp, aimAtFar: true);
        Assert.True(offMap.Lines == refusal && offMap.Spent == 0 && offMap.Pose == 0 && !offMap.CasterChanged,
                    $"{sp.Name} at the id of a player on another map: {offMap}");

        var nobody = Cast(sp, aimAtFar: false);
        switch (key)
        {
            case "venom_mage":
                Assert.True(nobody.CasterChanged && nobody.Spent == 60 && nobody.Pose == 1, $"{sp.Name} at an id nobody has: {nobody}");
                break;
            case "vex_mage":
                Assert.True(nobody.Spent == 60 && nobody.Pose == 1, $"{sp.Name} at an id nobody has: {nobody}");
                break;
            case "blind":
                Assert.True(nobody.Lines == "It doesn't work." && nobody.Spent == 0 && nobody.Pose == 0,
                            $"{sp.Name} at an id nobody has: {nobody}");
                break;
            default:
                Assert.Fail($"no contrast for '{key}'");
                break;
        }
    }

    /// <summary>Heal is not the resolver's line. Aimed at the id of a player on another map with nothing faced, it heals
    /// its caster, as it does with the slot alone and at an id nobody has: "You cast Heal.", the mana and the pose. The
    /// far player is untouched.
    /// <para>Green on 6be6be3 as well.</para></summary>
    [Fact]
    public void HealAimedAtAPlayerOnAnotherMapStillHealsItsCaster()
    {
        var sp = Content.SpellByKey("heal_mage")!;
        var offMap = Cast(sp, aimAtFar: true);
        var nobody = Cast(sp, aimAtFar: false);
        Assert.True(offMap.HpDelta > 0 && offMap.Lines == "You cast Heal." && offMap.Pose == 1, $"Heal, off-map id: {offMap}");
        Assert.Equal(nobody, offMap);
    }

    // ===== the cast ======================================================================================

    private sealed record Outcome(string Lines, uint Spent, int HpDelta, int Pose, bool CasterChanged);

    /// <summary>A fresh caster in the arena, facing an empty tile, casts <paramref name="sp"/> from slot 0 at the id of a
    /// fresh player on the far map or at an id nobody has. Asserts the far player is untouched, and returns what the
    /// caster saw, spent and gained: HP, and whether they now carry a status they did not.</summary>
    private Outcome Cast(SpellDef sp, bool aimAtFar)
    {
        var (far, farOut, farc) = _fx.PlayerWith($"OmfFar{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 50;
            ch.MaxHp = MaxHp; ch.Hp = Hp;
            Wide(ch, Far);
        }, Far, CX, CY);
        var (caster, cOut, cc) = _fx.PlayerWith($"OmfCaster{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 99;
            ch.MaxHp = MaxHp; ch.Hp = Hp;
            ch.MaxMp = Mana; ch.Mp = Mana;
            ch.Will = 255;
            ch.Dir = 0;
            Wide(ch, Arena);
            ch.Spells.Add(sp.Id);
        }, Arena, CX, CY);
        try
        {
            uint mp0 = cc.Mp, hp0 = cc.Hp;
            cOut.Clear();
            farOut.Clear();

            caster.Receive(SpellCastSupport.CastFrame(0, aimAtFar ? far.PlayerId : NobodyId));

            string what = $"{sp.Name} at {(aimAtFar ? "the id of a player on another map" : "an id nobody has")}";
            Assert.True(farc.Hp == Hp && !far.Poisoned && !far.Asleep && SpellCastSupport.BuffEntry(far, sp.Key) is null
                        && SpellCastSupport.MiniTexts(farOut).Count == 0, $"{what}: the far player was reached");
            bool changed = caster.Poisoned || caster.Asleep || SpellCastSupport.BuffEntry(caster, sp.Key) is not null;
            return new Outcome(string.Join(" | ", SpellCastSupport.MiniTexts(cOut)), mp0 - cc.Mp, (int)cc.Hp - (int)hp0,
                               cOut.BodiesOf(ServerOp.Action).Count, changed);
        }
        finally
        {
            _fx.World.LeaveMap(caster, Arena);
            _fx.World.LeaveMap(far, Far);
        }
    }

    /// <summary>The arena carries its own size; content-free maps have none, and the cast handler needs one.</summary>
    private static void Wide(Character c, ushort map)
    {
        if (Content.Maps.TryGetValue(map, out var m)) { c.MapXs = m.Xs; c.MapYs = m.Ys; }
        else { c.MapXs = 100; c.MapYs = 100; }
    }
}
