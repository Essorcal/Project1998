using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Spirit Blade arms its enchant (the test client's spell census, defect 1).
///
/// <para><c>spirit_blade</c> is one of the <c>EnchantFor</c> spells (<c>game-data/SpellMods.csv</c>: x9 for 10000
/// mana) and the only one with no <c>spell_effects.csv</c> row. <c>Session.ApplyCast</c> used to send every
/// spell without a row to the keyword fallback (the <c>generic</c> verb) before it reached the enchant
/// dispatch, so a Spirit Blade cast charged generic's 5 mana, printed "You cast Spirit blade." and armed
/// nothing. Silent: the player paid, was told it worked, and swung at x1. It now runs <c>stance_enchant</c>
/// with the multiplier and mana every other enchant gets from the same table.</para>
///
/// <para>Driven end to end: the real 0x0F frame through <c>Session.Receive</c>, on a content-free map (no
/// registry row, so casting is allowed and nothing spawns).</para>
/// </summary>
[Collection("world")]
public sealed class SpiritBladeSpellTests
{
    private const ushort Map = 62000;

    private readonly SessionFixture _fx;

    public SpiritBladeSpellTests(SessionFixture fx) => _fx = fx;

    private static SpellDef SpiritBlade => Content.SpellByKey("spirit_blade")!;

    /// <summary>The cast arms the enchant at x9 and charges 10000 mana. Red on 9c00b98: the enchant stays at 1,
    /// the pool loses generic's 5, and the cast still answers "You cast Spirit blade.".</summary>
    [Fact]
    public void ACastArmsTheEnchantAtItsOwnMultiplierAndMana()
    {
        var sp = SpiritBlade;
        Assert.Null(Content.FxFor(sp));                                    // the precondition the defect lived on
        Assert.Equal((9.0, 10000), Content.EnchantFor(sp));                // SpellMods.csv, the table every enchant uses

        var (session, outbound, c) = _fx.PlayerWith("SpBlade1", ch => Shape(ch, sp), Map, x: 5, y: 5);
        try
        {
            session.Receive(SpellCastSupport.CastFrame(0));

            Assert.Equal((true, 9.0, 15_000u),
                         (session.LuaEnchantActive, SpellCastSupport.Enchant(session), c.Mp));
            Assert.Contains("You cast Spirit blade.", SpellCastSupport.MiniTexts(outbound));
        }
        finally
        {
            _fx.World.LeaveMap(session, Map);
        }
    }

    /// <summary>The enchant stance's own refusal: a second cast while it is up says "This spell is already
    /// active.", costs nothing and does not answer "You cast". Red on 9c00b98, where the second cast is a second
    /// generic cast: 5 more mana and another "You cast Spirit blade.".</summary>
    [Fact]
    public void ASecondCastWhileTheEnchantIsUpIsRefusedAndFree()
    {
        var sp = SpiritBlade;
        var (session, outbound, c) = _fx.PlayerWith("SpBlade2", ch => Shape(ch, sp), Map, x: 7, y: 5);
        try
        {
            session.Receive(SpellCastSupport.CastFrame(0));
            uint afterFirst = c.Mp;
            outbound.Clear();

            session.Receive(SpellCastSupport.CastFrame(0));

            var lines = SpellCastSupport.MiniTexts(outbound);
            Assert.Equal((15_000u, 15_000u), (afterFirst, c.Mp));
            Assert.Contains("This spell is already active.", lines);
            Assert.DoesNotContain("You cast Spirit blade.", lines);
        }
        finally
        {
            _fx.World.LeaveMap(session, Map);
        }
    }

    /// <summary>A level-99 caster with Spirit Blade in book slot 0 and 25000 mana, enough for one cast.</summary>
    private static void Shape(Character c, SpellDef sp)
    {
        c.Level = 99;
        c.MaxHp = 1_000; c.Hp = 1_000;
        c.MaxMp = 25_000; c.Mp = 25_000;
        c.Spells.Add(sp.Id);
    }
}
