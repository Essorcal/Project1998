using System.Linq;
using Server;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Lua <c>ctx:setHp(n)</c> cannot kill (#175).
///
/// <para>Hp 0 is this server's whole dead state (<c>Session.IsDead</c>), but only <c>Die()</c> makes a death:
/// the ghost redraw, the penalties, the save. <c>setHp</c> used to clamp to [0, max], so a verb passing 0 left
/// a living player at Hp 0 with none of that, and <c>TakeDamage</c>'s <c>if (IsDead) return 0</c> then made
/// them immune to every later blow. It now floors a living caster at 1. No shipped verb passes 0 to a living
/// caster (see the #175 report), so nothing a player can reach changes.</para>
///
/// <para>Driven through the Lua-facing binding (<see cref="SpellContext.setHp"/>, which rounds and forwards to
/// <c>Session.LuaSetHp</c>) under the caster's state monitor, which is the lock a verb runs under.</para>
/// </summary>
[Collection("world")]
public sealed class LuaSetHpTests
{
    private readonly SessionFixture _fx;

    public LuaSetHpTests(SessionFixture fx) => _fx = fx;

    /// <summary>The bug. A living caster set to 0 (or below, or to a fraction that rounds to 0) is left on
    /// 1 hp and alive. Red on the base: Hp 0 and <c>IsDead</c> true, with no death sequence.</summary>
    [Theory]
    [InlineData("LuaSetHpZero", 0.0)]
    [InlineData("LuaSetHpNegative", -20.0)]
    [InlineData("LuaSetHpRoundsToZero", 0.4)]
    public void ASetAtOrBelowZeroLeavesALivingCasterOnOneHp(string name, double value)
    {
        var (session, _, character) = _fx.PlayerWith(name, c => { c.MaxHp = 100; c.Hp = 50; c.Level = 1; });

        SetHp(session, value);

        Assert.Equal(((uint)1, false), (character.Hp, session.IsDead));
    }

    /// <summary>Control: a caster who is ALREADY dead keeps the old floor of 0, so a set to 0 cannot lift a
    /// ghost to 1 hp (which would un-ghost the number without the redraw that <c>reviveSelf</c> does).</summary>
    [Fact]
    public void ASetToZeroLeavesADeadCasterDead()
    {
        var (session, _, character) = _fx.PlayerWith("LuaSetHpGhost", c => { c.MaxHp = 100; c.Hp = 0; c.Level = 1; });

        SetHp(session, 0);

        Assert.Equal(((uint)0, true), (character.Hp, session.IsDead));
    }

    /// <summary>Control: an ordinary set is taken as given, and a set above the cap still clamps to it.</summary>
    [Theory]
    [InlineData("LuaSetHpOrdinary", 37.0, 37u)]
    [InlineData("LuaSetHpOne", 1.0, 1u)]
    [InlineData("LuaSetHpOverCap", 5000.0, 100u)]
    public void AnOrdinarySetIsUnchanged(string name, double value, uint expected)
    {
        var (session, _, character) = _fx.PlayerWith(name, c => { c.MaxHp = 100; c.Hp = 50; c.Level = 1; });

        SetHp(session, value);

        Assert.Equal(expected, character.Hp);
    }

    private static void SetHp(Session session, double value)
    {
        var sp = Content.Spells.First();
        session.WithState(() => new SpellContext(session, sp, null, null).setHp(value));
    }
}
