using System;
using System.Linq;
using Server;
using Xunit;

namespace Tests;

/// <summary>
/// The White Moon Axe (<see cref="WhiteMoonAxeAbility"/>) is heard, not clicked, and shares the word "moon"
/// with the armor chains at the same NPC. Both of its failure modes are silent: a Maso copy missing from the
/// gate hides the quest from aligned rogues, and a composition order that puts <c>armor_quest</c> first makes
/// Maso answer with the armor chain and never this.
/// </summary>
public class WhiteMoonAxeQuestTests
{
    private static readonly object _gate = new();
    private static bool _loaded;

    private static void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            TestProcessState.LoadContent();
            _loaded = true;
        }
    }

    /// <summary>Every Maso (the Buya guildmaster and his alignment copies), and no other rogue trainer, carries
    /// the quest through its NpcAbilities.csv narrowing, and hears "moon" before the armor chain does.</summary>
    [Fact]
    public void EveryMasoCarriesTheQuestAheadOfTheArmorChain()
    {
        EnsureLoaded();

        // The row's narrowing IS the gate (the ability no longer checks), so it must name every Maso.
        var masos = Content.Npcs.Where(n => n.Key == "RogueTrainerNpc" && n.Name.EndsWith("Maso")).ToList();
        var entry = Content.NpcCompositions["RogueTrainerNpc"].Single(r => r.Name == "white_moon_axe");
        Assert.True(entry.NpcIds is not null, "white_moon_axe is on every rogue trainer — it must be narrowed to the Masos");
        Assert.Equal(entry.NpcIds!.OrderBy(i => i), masos.Select(n => n.Id).OrderBy(i => i));
        foreach (var npc in Content.Npcs.Where(n => n.Key == "RogueTrainerNpc" && !masos.Contains(n)))
            Assert.DoesNotContain(NpcScripts.For(npc), a => a is WhiteMoonAxeAbility);

        foreach (var npc in masos)
        {
            var abilities = NpcScripts.For(npc).ToList();
            int wma = abilities.FindIndex(a => a is WhiteMoonAxeAbility);
            int armor = abilities.FindIndex(a => a is ArmorQuestAbility);
            Assert.True(wma >= 0, $"{npc.Name} (npc {npc.Id}) is missing the White Moon Axe ability");
            Assert.True(armor < 0 || wma < armor, $"{npc.Name} hears 'moon' as armor before the axe quest");
        }
    }

    /// <summary>The items and mobs the quest names exist, the axe row is loose (tswolf: "Non Bonded when
    /// Aquired in WMA Buya Quest"), and both targets spawn somewhere.</summary>
    [Fact]
    public void EveryKeyResolvesAndBothTargetsSpawn()
    {
        EnsureLoaded();

        Assert.NotNull(Content.ItemByKey(WhiteMoonAxeAbility.Bracelet));
        var axe = Content.ItemByKey(WhiteMoonAxeAbility.Axe);
        Assert.NotNull(axe);
        Assert.False(axe!.Bonded);

        foreach (var key in new[] { WhiteMoonAxeAbility.ScorpionMob, WhiteMoonAxeAbility.JuMob })
        {
            var mob = Content.MobByKey(key);
            Assert.True(mob is not null, $"{key} is missing from mobs.csv");
            Assert.True(Content.AreaSpawns.Any(s => s.MobId == mob!.Id), $"{key} spawns nowhere; the step can never be passed");
        }
    }
}
