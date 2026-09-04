using Server;
using Xunit;

namespace Tests;

public sealed class TableSpecTests
{
    private static readonly string[] ExpectedLoadOrder =
    [
        "ObjectFlagOverrides.csv", "Obj533Fix.csv", "Tile533Map.csv", "map_index.csv",
        "MobFlees.csv", "MobStationary.csv", "mobs.csv", "Items.csv", "Warps.csv", "Spawns.csv",
        "AreaSpawns.csv", "AreaSpawnsTrap.csv", "AreaSpawnsCrafting.csv", "ServerTuning.csv",
        "EraFeatures.csv", "NPCs.csv", "MinorQuests.csv", "ShopStock.csv", "ShopBuysFrom.csv",
        "Paths.csv", "LevelExp.csv", "SpellLevels.csv", "Spells.csv", "spell_effects.csv",
        "SpellText.csv", "SpellLearnCosts.csv", "Mob5xPalettes.csv", "ArmorDyeRamps.csv", "Maps.csv",
        "MobDrops.csv", "CraftingToggles.csv", "WarpQuestLocks.csv", "ArmorQuests.csv", "MythicCaves.csv",
        "MythicAlliances.csv", "ArenaDoors.csv", "EventCaveTiers.csv", "EventCaves.csv", "MusicTracks.csv",
        "MapBgm.csv", "Inns.csv", "ForageAreas.csv", "HarvestNodes.csv", "MobSpells.csv",
        "MobChatter.csv", "MobSpawnRules.csv", "MobBosses.csv", "PathHalls.csv", "GatewayGates.csv",
        "WorldMapDests.csv", "WorldMapTriggers.csv", "FallRooms.csv", "AmbushBursts.csv",
        "AmbushConfig.csv", "BoardLocations.csv", "ShopCatalogues.csv", "SpellParams.csv",
        "spell_verbs.lua", "ItemParams.csv", "item_verbs.lua", "npc_dialog.lua", "mob_ai.lua", "Pets.csv",
        "WeaponProcs.csv", "Traps.csv", "Morphs.csv", "SpellMods.csv", "NpcAbilities.csv", "PathGrowth.csv",
        "DoorObjects.csv", "Doors.csv", "MapCells.csv",
    ];

    /// <summary>The spec array owns the historical load order, and every declared input feeds the report once.
    /// A missing, duplicated or moved call would otherwise quietly change the loaded world or startup census.</summary>
    [Fact]
    public void SpecificationsAndReportKeepTheEstablishedLoadOrder()
    {
        lock (TestProcessState.Gate)
        {
            Assert.Equal(ExpectedLoadOrder, Content.TableSpecifications.Select(spec => spec.File));
            Assert.Equal(68, Content.TableSpecifications.Count(spec => spec.Kind == ContentTableKind.Csv));
            Assert.Equal(4, Content.TableSpecifications.Count(spec => spec.Kind == ContentTableKind.Lua));
            Assert.Equal(Content.TableSpecifications.Count,
                Content.TableSpecifications.Select(spec => spec.EnvironmentVariable)
                    .Distinct(StringComparer.Ordinal).Count());

            TestProcessState.LoadContent();

            Assert.Equal(ExpectedLoadOrder, Content.LoadReport.Select(table => table.Name));
        }
    }

    /// <summary>The committed README block is generated from the same specs and load counts as the server.
    /// Editing a count or forgetting to regenerate after adding a table must fail in CI.</summary>
    [Fact]
    public void ReadmeTableBlockMatchesGenerator()
    {
        lock (TestProcessState.Gate)
        {
            TestProcessState.LoadContent();
            string readme = File.ReadAllText(Path.Combine(Shared.RepoPaths.GameDataDir(), "README.md"))
                .Replace("\r\n", "\n", StringComparison.Ordinal);
            int start = readme.IndexOf(Content.TableReadmeStartMarker, StringComparison.Ordinal);
            int end = readme.IndexOf(Content.TableReadmeEndMarker, StringComparison.Ordinal);
            Assert.True(start >= 0 && end >= start, "README generated-table markers are missing or reversed");
            end += Content.TableReadmeEndMarker.Length;

            Assert.Equal(Content.RenderTableReadmeBlock(), readme[start..end]);
        }
    }
}
