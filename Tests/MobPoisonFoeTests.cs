using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// #208: a creature's venom has no player attacker to remember, while a player's venom still marks its
/// caster. <see cref="Session.TickPoison"/> feeds the stored source to the arena-pet foe marker after a
/// damaging tick, so both facts wait for that tick rather than inspecting poison setup alone.
/// </summary>
[Collection("world")]
public class MobPoisonFoeTests
{
    private const ushort MobArena = 32, PlayerArena = 33;
    private const uint CreatureId = 5000;

    private readonly SessionFixture _fx;

    public MobPoisonFoeTests(SessionFixture fx) => _fx = fx;

    /// <summary>A creature-sourced venom damages its victim without putting a creature id in PvpFoeId.</summary>
    [Fact]
    public void AMobSourcedPoisonLeavesNoPvpFoe()
    {
        Assert.True(Content.IsPvpMap(MobArena));
        Assert.True(Content.TryMap(MobArena, out var arena));

        var (victim, _, character) = _fx.PlayerWith(
            "MobPoisonVictim", c => { c.MapXs = arena.Xs; c.MapYs = arena.Ys; }, MobArena, 5, 10);
        var caster = new Mob(CreatureId, sprite: 1, x: 6, y: 10, name: "venom creature", hp: 10);
        var venom = new Content.MobSpellDef(
            MobKey: "test_venom_creature", Name: "venom", Effect: "poison", Chance: 1, EveryMs: 0,
            Range: 1, Amount: 1, Stat: "", Category: "venoms", DurationMs: 5000, Anim: 0, Sound: 0,
            Say: "", PerTick: 1, TickMinMs: 1, TickMaxMs: 1);

        victim.ApplyMobSpell(caster, venom);

        TickUntilDamage(victim, character);
        Assert.Equal(0u, victim.PvpFoeId);
    }

    /// <summary>A player-sourced venom still records its caster for arena-pet retaliation.</summary>
    [Fact]
    public void APlayerSourcedVenomStillMarksItsCaster()
    {
        Assert.True(Content.IsPvpMap(PlayerArena));
        Assert.True(Content.TryMap(PlayerArena, out var arena));

        var (attacker, _) = _fx.Player("PlayerPoisonAttacker", PlayerArena, 6, 10);
        var (victim, _, character) = _fx.PlayerWith(
            "PlayerPoisonVictim", c => { c.MapXs = arena.Xs; c.MapYs = arena.Ys; }, PlayerArena, 5, 10);

        victim.ReceivePoison(
            dps: 1, durMs: 5000, by: attacker.PlayerId, anim: 0, key: "test_player_venom", name: "venom",
            perTick: 1, tickMinMs: 1, tickMaxMs: 1);

        TickUntilDamage(victim, character);
        Assert.Equal(attacker.PlayerId, victim.PvpFoeId);
    }

    private static void TickUntilDamage(Session victim, Character character)
    {
        uint hpBefore = character.Hp;
        Assert.True(
            SpinWait.SpinUntil(() =>
            {
                victim.TickPoison();
                return character.Hp < hpBefore;
            }, 1000),
            "the first poison tick did not land within one second");
    }
}
