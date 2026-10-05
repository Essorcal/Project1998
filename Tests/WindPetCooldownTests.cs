using Server;
using Xunit;

namespace Tests;

/// <summary>
/// The eight wind pets recast once every 480 seconds and cost no mana, read through
/// <see cref="Content.PetSpellFor"/>: the only place the pet cast takes either number from (the
/// <c>pet_summon</c> verb's <c>ctx.petMana</c> and <c>ctx.petCooldown</c>, which are
/// <c>Session.LuaPetMana</c> and <c>Session.LuaPetCooldownMs</c>).
///
/// <para>The failure this pins is silent. <c>game-data/Pets.csv</c> gave Wind dancer and the three Champions
/// cooldown 0 and mana 10, so a poet could summon them back to back. The 480000 aether on their
/// <c>spell_effects.csv</c> rows never reached the cast: the pet dispatch returns before the generic aether
/// gate that reads that file.</para>
///
/// <para>Where each value comes from:</para>
/// <list type="bullet">
/// <item><b>cooldownMs 480000, all eight.</b> Nexus Atlas's poet page,
/// https://www.nexusatlas.com/spells/poet.php (Sources.csv <c>atlas-poet-spells</c>), gives Wind dancer, the
/// Kwi-Sin, Ming-ken and Ohaeng Champions, Summon warrior and the three Avatars "Aethers - 480 Seconds". RTK
/// agrees; the RTK line for each row is beside its case below (rtklua/Accepted/Spells/poet/).</item>
/// <item><b>mana 0, all eight.</b> The same Nexus Atlas page: "Mana Cost - 0". RTK disagrees for the dancer
/// tier: cotw_wind_dancer.lua charges <c>magicCost = 10</c> for Wind dancer and each Champion (lines 3, 47,
/// 91 and 135), while cotw_wind_warrior.lua has no mana check for Wind warrior or the Avatars. The fan site
/// wins because RTK is weight 0 (AGENTS.md Rule 2); Caleb chose mana 0 on 2026-09-29.</item>
/// </list>
/// </summary>
public class WindPetCooldownTests
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

    /// <summary>Asserted as one (mana, cooldownMs) pair, so a failure prints both numbers the cast would use.</summary>
    [Theory]
    [InlineData("cotw_wind_dancer_poet")]    // RTK cotw_wind_dancer.lua:6    aether 480000; magicCost 10 at :3
    [InlineData("kwisin_champion_poet")]     // RTK cotw_wind_dancer.lua:50   aether 480000; magicCost 10 at :47
    [InlineData("mingken_champion_poet")]    // RTK cotw_wind_dancer.lua:94   aether 480000; magicCost 10 at :91
    [InlineData("ohaeng_champion_poet")]     // RTK cotw_wind_dancer.lua:138  aether 480000; magicCost 10 at :135
    [InlineData("cotw_wind_warrior_poet")]   // RTK cotw_wind_warrior.lua:5   aethers 480000; no mana check
    [InlineData("kwisin_avatar_poet")]       // RTK cotw_wind_warrior.lua:35  aethers 480000; no mana check
    [InlineData("mingken_avatar_poet")]      // RTK cotw_wind_warrior.lua:65  aethers 480000; no mana check
    [InlineData("ohaeng_avatar_poet")]       // RTK cotw_wind_warrior.lua:95  aethers 480000; no mana check
    public void WindPetRecastsEveryEightMinutesAndCostsNoMana(string key)
    {
        EnsureLoaded();
        var sp = Content.SpellByKey(key);
        Assert.True(sp is not null, $"spell '{key}' is missing from Spells.csv");
        var pet = Content.PetSpellFor(sp!);
        Assert.True(pet is not null, $"'{key}' has no Pets.csv row, so its cast never reaches pet_summon");
        Assert.Equal((0, 480_000), (pet!.Value.Mana, pet.Value.CooldownMs));
    }
}
