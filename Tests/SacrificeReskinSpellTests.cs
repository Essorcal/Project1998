using Server;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The six Rogue sacrifice reskins hold the shared cast/swing slot for a second, as Lethal Strike and Desperate
/// Attack do (Caleb, 2026-09-29, "2 yes").
///
/// <para>Afterlife's Embrace, Ming-Ken's Judgement and Calculating Blow are Lethal Strike under the other three
/// alignments; The Void's Measure, Beastly Frenzy and Tilting the Balance are Desperate Attack. RTK's scripts make
/// each one a one-line wrapper over its original's <c>cast</c> (<c>Spells/rogue/lethal_strike.lua</c>,
/// <c>desperate_attack.lua</c>), and <c>Content.SacrificeFamilyFor</c> already sends all eight to the same
/// <c>sacrifice</c> verb. The cast delay was the one place they parted: <c>Content.CastDelayMs</c> reads the
/// archetype cell, the originals' rows say Damage and the reskins' rows say Utility
/// (<c>game-data/spell_effects.csv</c>), so the reskins cast with no delay at all and never blocked a swing.</para>
///
/// <para>The six keys are named here on purpose. The Warrior Berserk and Whirlwind reskins have Utility rows too
/// and the same gap, but no decision covers them, so they stay as they are and the last fact pins that.</para>
/// </summary>
[Collection("world")]
public sealed class SacrificeReskinSpellTests
{
    private const ushort Map = 62020;

    private static readonly string[] ReskinKeys =
    {
        "afterlifes_embrace_rogue", "mingkens_judgement_rogue", "calculating_blow_rogue",   // Lethal Strike's
        "the_voids_measure_rogue", "beastly_frenzy_rogue", "tilting_the_balance_rogue",    // Desperate Attack's
    };

    private readonly SessionFixture _fx;
    private static int _serial;

    public SacrificeReskinSpellTests(SessionFixture fx) => _fx = fx;

    public static TheoryData<string> Reskins
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var key in ReskinKeys) data.Add(key);
            return data;
        }
    }

    /// <summary>Each reskin's cast delay is its original's: one second. Red on 9c00b98, where the reskin's is 0.</summary>
    [Theory]
    [MemberData(nameof(Reskins))]
    public void EachReskinHasItsOriginalsOneSecondDelay(string key)
    {
        var reskin = Content.SpellByKey(key)!;
        var original = Content.SacrificeFamilyFor(reskin) switch
        {
            Content.SacrificeFamily.LethalStrike    => Content.SpellByKey("lethal_strike_rogue")!,
            Content.SacrificeFamily.DesperateAttack => Content.SpellByKey("desperate_attack_rogue")!,
            var other => throw new InvalidOperationException($"{key} is in the {other} family"),
        };

        Assert.Equal((Content.ZapCastDelayMs, Content.ZapCastDelayMs),
                     (Content.CastDelayMs(original), Content.CastDelayMs(reskin)));
    }

    /// <summary>The slot that delay claims, through a real cast: right after the reskin goes off, the shared
    /// cast/swing slot is held, so a swing or another delayed cast has to wait. Red on 9c00b98: nothing is
    /// held.</summary>
    [Theory]
    [MemberData(nameof(Reskins))]
    public void ACastHoldsTheSharedCastSwingSlot(string key)
    {
        var sp = Content.SpellByKey(key)!;
        int n = Interlocked.Increment(ref _serial);
        var (session, outbound, _) = _fx.PlayerWith($"Reskin{n}", ch =>
        {
            ch.Level = 99;
            ch.MaxHp = 1_000; ch.Hp = 1_000;
            ch.MaxMp = 1_000; ch.Mp = 1_000;
            ch.Spells.Add(sp.Id);
        }, Map, x: 5, y: 5);
        try
        {
            session.Receive(SpellCastSupport.CastFrame(0));

            Assert.Contains($"You cast {sp.Name}.", SpellCastSupport.MiniTexts(outbound));
            Assert.InRange(session.ActionSlotLeft, 1, Content.ZapCastDelayMs);
        }
        finally
        {
            _fx.World.LeaveMap(session, Map);
        }
    }

    /// <summary>No other spell's cast delay moves: every spell in <c>Content</c> is compared with what
    /// <c>Content.CastDelayMs</c> gave it on 9c00b98, and only the six reskins may differ, each from 0 to 1000.
    /// That includes the six Warrior reskins with the same Utility-row gap.</summary>
    [Fact]
    public void NoOtherSpellsCastDelayMoves()
    {
        var reskins = new HashSet<string>(ReskinKeys, StringComparer.OrdinalIgnoreCase);
        var moved = new List<string>();
        int seen = 0;
        foreach (var sp in Content.Spells)
        {
            seen++;
            int before = CastDelayOnMaster(sp), after = Content.CastDelayMs(sp);
            var want = reskins.Contains(sp.Key) ? (0, Content.ZapCastDelayMs) : (before, before);
            if ((before, after) != want) moved.Add($"{sp.Key} ({sp.Id}): {before} -> {after}");
        }

        Assert.True(seen > 800, $"only {seen} spells loaded");
        Assert.Empty(moved);
    }

    /// <summary><c>Content.CastDelayMs</c> as it stood at upstream/master 9c00b98
    /// (<c>Server/Content.Spells.cs</c>, lines 1390-1413 there): the dog fire family 0, the stealth family one
    /// second, then one second for a Damage row and 0 for everything else.</summary>
    private static int CastDelayOnMaster(SpellDef sp) =>
        Content.IsDogFireSpell(sp) ? 0
        : Content.IsStealthSpell(sp) ? Content.ZapCastDelayMs
        : Content.FxFor(sp) is { Archetype: "Damage" } ? Content.ZapCastDelayMs
        : 0;
}
