using Server;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// What a trainer charges, and who offers it, for three sets of rank spells (owner, 2026-10-24, decision sheet
/// items 8 and 9), as <c>game-data/SpellLearnCosts.csv</c> now has them.
///
/// <para><b>Six Sam san rows</b> (Dragon's harness, Blade's charm, Explorer's remedy, Power stream, Energy flow,
/// Suppress) were hand-written with the source <c>extrapolated-from-ee-san</c> and asked costs no source prints:
/// Blade's charm 100,000 coins and three items, Energy flow 300,000, Suppress 250,000 and no items. Each now asks
/// what its live Nexus Atlas page prints (<c>atlas-warrior-spells</c>, <c>atlas-rogue-spells</c>,
/// <c>atlas-mage-spells</c>), which the tutor-board tier tables print too for all but Dragon's harness
/// (<c>re/archive_rogue_spells.md</c>, <c>re/archive_mage_spells.md</c>; the warrior table has no row for it).</para>
///
/// <para><b>Race</b> and its three alignment names (Il san) had no row, so a trainer taught them free. They now
/// ask a Blood, a Splendid tiger pelt and 25,000 coins (the live rogue page and the rogue tier table). The level
/// stays 99, so who is offered them is unchanged.</para>
///
/// <para><b>Dagger's remedy</b> and its three names (Ee san) had no row, and their Spells.csv rows carry
/// SplPthId 99, so no trainer offered them at all. They now ask 3 Mountain ginseng and 75,000 coins (both sources,
/// and RTK's requirements()), and the city lock in <c>Content.CityLockedSpells</c> keeps them to the rogue trainers
/// in Nagnang, at mark 2, as RTK's Nagnang trainer teaches them.</para>
///
/// <para>Every fact reads what the trainer reads: <see cref="Content.LearnCostFor"/> with the base path (the fee),
/// <see cref="Content.SpellsForClass"/> (the Learn Secret menu) and <see cref="Content.TeachableInRegion"/> on the
/// trainer's map (the city filter). Red on 821914e (that commit's CSV under this suite): all fourteen costs, and
/// Dagger's remedy offered nowhere. Race's offer fact is green on both, as it should be: the row changes only the
/// fee.</para>
/// </summary>
[Collection("world")]
public sealed class RankLearnCostTests
{
    private const int Rogue = 2;
    private const int Baekho = 7;

    public RankLearnCostTests(SessionFixture fx) => _ = fx;   // the fixture loads Content

    /// <summary>Each changed row: key, the class whose trainer charges it, coins, then the items as "n key".</summary>
    public static TheoryData<string, int, int, string> Rows => new()
    {
        // Sam san, the live pages (and the tier tables, all but Dragon's harness)
        { "dragons_harness_warrior",      1,  80_000, "" },
        { "blades_charm_rogue",           2, 500_000, "" },
        { "explorers_remedy_rogue",       2, 100_000, "4 mountain_ginseng" },
        { "power_stream_rogue",           2, 100_000, "2 mountain_ginseng" },
        { "energy_flow_mage",             3,  50_000, "10 mountain_ginseng" },
        { "suppress_mage",                3, 120_000, "1 sam_san_surge, 6 star_staff, 6 scribes_book" },
        // Race and its alignment names, Il san
        { "race_rogue",                   2,  25_000, "1 blood, 1 splendid_tiger_pelt" },
        { "spiritual_jump_rogue",         2,  25_000, "1 blood, 1 splendid_tiger_pelt" },
        { "leap_of_faith_rogue",          2,  25_000, "1 blood, 1 splendid_tiger_pelt" },
        { "transport_rogue",              2,  25_000, "1 blood, 1 splendid_tiger_pelt" },
        // Dagger's remedy and its alignment names, Ee san
        { "daggers_remedy_rogue",         2,  75_000, "3 mountain_ginseng" },
        { "kwisin_daggers_remedy_rogue",  2,  75_000, "3 mountain_ginseng" },
        { "mingken_daggers_remedy_rogue", 2,  75_000, "3 mountain_ginseng" },
        { "ohaeng_daggers_remedy_rogue",  2,  75_000, "3 mountain_ginseng" },
    };

    /// <summary>The fee a trainer of that class asks: level 99, the coins and the items above, and every item is
    /// one Items.csv has.</summary>
    [Theory]
    [MemberData(nameof(Rows))]
    public void TheTrainerAsksThePrintedPrice(string key, int pathId, int gold, string items)
    {
        var sp = Content.SpellByKey(key);
        Assert.True(sp is not null, $"Spells.csv has no '{key}'");
        var cost = Content.LearnCostFor(sp!, pathId);
        Assert.True(cost is not null, $"{key} has no learn row for path {pathId}");
        string got = string.Join(", ", cost!.Items.Select(i => $"{i.Amount} {i.Item}"));
        Assert.Equal((99, gold, items), (cost.Level, cost.Gold, got));
        foreach (var (item, _) in cost.Items)
            Assert.True(Content.ItemByKey(item) is not null, $"{key} asks for '{item}', which Items.csv does not have");
    }

    /// <summary>Race's four names are offered as before the row: each to its own alignment at level 99 and Il san,
    /// to a Baekho as to a Rogue, and not before Il san or below level 99.</summary>
    [Theory]
    [InlineData("race_rogue")]
    [InlineData("spiritual_jump_rogue")]
    [InlineData("leap_of_faith_rogue")]
    [InlineData("transport_rogue")]
    public void RaceIsOfferedAtIlSanAsBefore(string key)
    {
        var sp = Content.SpellByKey(key)!;
        bool Offered(int path, int level, int mark) =>
            Content.SpellsForClass(path, level, sp.Alignment, mark).Any(s => s.Key == key && s.Level == 99);
        Assert.True(Offered(Rogue, 99, 1) && Offered(Baekho, 99, 1), $"{sp.Name} is not offered at Il san");
        Assert.False(Offered(Rogue, 99, 0) || Offered(Rogue, 98, 1), $"{sp.Name} is offered before Il san or level 99");
    }

    /// <summary>Dagger's remedy's four names: each offered to its own alignment from Ee san (mark 2) and level 99, to
    /// a Baekho as to a Rogue, never at Il san; and of the rogue trainers, only the four in Nagnang (Dagger and
    /// its Kwi-Sin, Ming-Ken and Ohaeng rooms) may teach it. Red on 821914e: offered to no one.</summary>
    [Theory]
    [InlineData("daggers_remedy_rogue")]
    [InlineData("kwisin_daggers_remedy_rogue")]
    [InlineData("mingken_daggers_remedy_rogue")]
    [InlineData("ohaeng_daggers_remedy_rogue")]
    public void DaggersRemedyIsOfferedInNagnangFromEeSan(string key)
    {
        var sp = Content.SpellByKey(key)!;
        bool Offered(int path, int level, int mark) =>
            Content.SpellsForClass(path, level, sp.Alignment, mark).Any(s => s.Key == key && s.Level == 99);
        Assert.True(Offered(Rogue, 99, 2) && Offered(Baekho, 99, 2), $"{sp.Name} is not offered at Ee san");
        Assert.False(Offered(Rogue, 99, 1) || Offered(Rogue, 98, 2), $"{sp.Name} is offered before Ee san or level 99");

        var teaching = Content.Npcs
            .Where(n => n.Enabled && n.Key == "RogueTrainerNpc")
            .Where(n => Content.TeachableInRegion(sp, Content.RegionOf(n.Map)))
            .Select(n => n.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "Dagger", "Kwi-Sin Dagger", "Ming-Ken Dagger", "Ohaeng Dagger" }, teaching);
    }
}
