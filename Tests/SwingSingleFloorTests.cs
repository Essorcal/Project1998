using System.Reflection;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// #78 on the player's own swing (owner, 2026-10-21: "multiply everything, round down once, minimum 1"). The swing's
/// armor, rear x2 and reach used to be floored step by step on the attacker's side (<c>Session.PlayerSwingDamage</c>:
/// <c>Combat.ApplyArmor</c>'s floor, then x2, then a second floor for reach), and a PvP swing then went through the
/// defender's intake, which rounded its own amplifier and deduction again. Now the swing is one product of armor and
/// reach (<c>Session.PlayerSwingRaw</c>): a creature target floors it once, and a PvP swing hands it UNFLOORED to the
/// defender's intake, which multiplies its own terms in and floors once. A hit from behind is doubled after that
/// floor, in RTK's order (owner, 2026-10-21: "Round down once (minimum 1), then double for a hit from behind.
/// Ordinary hits from behind stay exactly as today.").
///
/// <para>The swings are real 0x13 frames through <c>Session.Receive</c>. The hit and crit rolls and the bare hand's
/// S roll (1 or 2) are random, so each fact swings until a non-crit hit lands, and every hit that lands must be one
/// of the figures the single floor gives for the four (S, crit) cases, and none of them the step rounding's figure
/// for a non-crit hit. The attacker is a Peasant of might 40 with no gear: no class factor and no Dam, so the swing is
/// S/2 + floor(40/4)/2 - 1 = 4.5 or 5.0, and three times that on a crit.</para>
///
/// <para>Maps no other class uses: Island Arena (3017, MapPvP 1) and a content-free map for the creature.</para>
/// </summary>
[Collection("world")]
public sealed class SwingSingleFloorTests
{
    private const ushort Arena = 3017;
    private const ushort Field = 62621;
    private const uint FullHp = 100_000;
    private const int Swings = 400;

    private static readonly FieldInfo NextActionTick =
        typeof(Session).GetField("_nextActionTick", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly SessionFixture _fx;
    private static int _serial;

    public SwingSingleFloorTests(SessionFixture fx) => _fx = fx;

    /// <summary>A PvP swing at a player of AC +75 under a 1.5x amplifier: 4.5 x 1.75 x 1.5 = 11.8 and
    /// 5.0 x 1.75 x 1.5 = 13.1, so 11 or 13 (35 or 39 on a crit).
    /// <para>Red on 6de1d24: 10 or 12. The attacker floored 4.5 x 1.75 = 7.875 to 7 and the intake rounded
    /// 7 x 1.5 = 10.5 to the even 10; 8.75 went to 8 and then 12.</para></summary>
    [Fact]
    public void APvpSwingHandsItsUnflooredFigureToTheIntake()
    {
        Assert.True(Content.IsPvpMap(Arena));
        var (victim, _, vc) = _fx.PlayerWith($"SsfVictim{Interlocked.Increment(ref _serial)}", c =>
        {
            c.MaxHp = FullHp; c.Hp = FullHp; c.Ac = 75; c.Level = 1;
            c.Dir = 3;   // facing the attacker, so no rear x2
            c.MapXs = 100; c.MapYs = 100;
        }, Arena, 11, 10);
        var (attacker, _, _) = Attacker(Arena);
        try
        {
            var landed = SwingUntilANonCritLands(attacker, () => vc.Hp, () => victim.ArmDamageAmp(1.5, 60_000),
                                                 nonCrit: new uint[] { 11, 13 });
            Assert.True(landed.Count > 0 && landed.All(d => d is 11 or 13 or 35 or 39) && landed.Any(d => d is 11 or 13),
                        $"landed: {string.Join(", ", landed)}");
        }
        finally
        {
            _fx.World.LeaveMap(attacker, Arena);
            _fx.World.LeaveMap(victim, Arena);
        }
    }

    /// <summary>A swing from behind a creature (both facing east, the attacker to its west) at AC -65: the rear x2
    /// doubles the once-floored figure, floor(4.5 x 0.35) = floor(5.0 x 0.35) = 1, so 2 (8 or 10 on a crit), exactly
    /// as on 6de1d24.
    /// <para>Red on d5edcf8, which doubled inside the floor: 4.5 x 0.35 x 2 = 3.15, so 3 (9 or 10 on a
    /// crit).</para></summary>
    [Fact]
    public void ASwingFromBehindACreatureDoublesAfterTheOneFloor()
    {
        var mob = new Mob(_fx.World.AllocateMobId(), 1, 11, 10, $"Ssf creature {Interlocked.Increment(ref _serial)}",
                          10_000_000) { Ac = -65, Dir = 1 };
        Assert.Null(_fx.World.MobAt(Field, 11, 10));
        _fx.World.AddMob(Field, mob);
        var (attacker, _, _) = Attacker(Field);
        try
        {
            var landed = SwingUntilANonCritLands(attacker, () => (uint)mob.Hp, () => { }, nonCrit: new uint[] { 2 });
            Assert.True(landed.Count > 0 && landed.All(d => d is 2 or 8 or 10) && landed.Any(d => d is 2),
                        $"landed: {string.Join(", ", landed)}");
        }
        finally
        {
            _fx.World.LeaveMap(attacker, Field);
            _fx.World.DespawnMob(Field, mob);
        }
    }

    /// <summary>A PvP swing from behind (both facing east, the attacker to the victim's west) at AC +75, nothing else:
    /// the defender's intake floors the unfloored swing once and then doubles it, floor(4.5 x 1.75) = 7 and
    /// floor(5.0 x 1.75) = 8, so 14 or 16 (46 or 52 on a crit), exactly as on 6de1d24.
    /// <para>Red on d5edcf8, which doubled inside the floor: 15 or 17 (47 or 52).</para></summary>
    [Fact]
    public void APvpSwingFromBehindDoublesAfterTheDefendersFloor()
    {
        Assert.True(Content.IsPvpMap(Arena));
        var (victim, _, vc) = _fx.PlayerWith($"SsfRearVictim{Interlocked.Increment(ref _serial)}", c =>
        {
            c.MaxHp = FullHp; c.Hp = FullHp; c.Ac = 75; c.Level = 1;
            c.Dir = 1;   // facing east, the way the attacker faces: the attacker is behind
            c.MapXs = 100; c.MapYs = 100;
        }, Arena, 11, 10);
        var (attacker, _, _) = Attacker(Arena);
        try
        {
            var landed = SwingUntilANonCritLands(attacker, () => vc.Hp, () => { }, nonCrit: new uint[] { 14, 16 });
            Assert.True(landed.Count > 0 && landed.All(d => d is 14 or 16 or 46 or 52) && landed.Any(d => d is 14 or 16),
                        $"landed: {string.Join(", ", landed)}");
        }
        finally
        {
            _fx.World.LeaveMap(attacker, Arena);
            _fx.World.LeaveMap(victim, Arena);
        }
    }

    // ===== helpers =======================================================================================

    /// <summary>A level-99 Peasant of might 40 and grace 100 (the hit roll at its ceiling), no gear, at (10, 10)
    /// facing east.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Attacker(ushort map) =>
        _fx.PlayerWith($"SsfAttacker{Interlocked.Increment(ref _serial)}", c =>
        {
            c.Level = 99; c.Grace = 100; c.Might = 40; c.Dir = 1;
            c.MaxHp = FullHp; c.Hp = FullHp;
            c.MapXs = 100; c.MapYs = 100;
        }, map, 10, 10);

    /// <summary>Swing (the real 0x13 frame, after freeing the shared cast/swing slot the last swing armed) until a
    /// hit lands for one of <paramref name="nonCrit"/>, at most <see cref="Swings"/> times. At the hit ceiling (62%)
    /// and a 97% non-crit share, 400 swings without one is a chance of about 1 in 10^160. Returns every figure that
    /// landed, read off <paramref name="hp"/>; <paramref name="before"/> runs ahead of each swing.</summary>
    private static List<uint> SwingUntilANonCritLands(Session attacker, Func<uint> hp, Action before, uint[] nonCrit)
    {
        var landed = new List<uint>();
        for (int i = 0; i < Swings && !landed.Any(nonCrit.Contains); i++)
        {
            before();
            uint was = hp();
            attacker.WithState(() => NextActionTick.SetValue(attacker, 0L));
            attacker.Receive(SessionFixture.Frame(ClientOp.Attack, new byte[] { 0 }));
            if (hp() != was) landed.Add(was - hp());
        }
        return landed;
    }
}
