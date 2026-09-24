using Server;
using Xunit;

namespace Tests;

[Collection("tile-translation")]
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
                Content.TableSpecifications.Select(spec => spec.File)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count());

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

    [Fact]
    public void ReadmeRendererRejectsAFailedCsvLoad()
    {
        lock (TestProcessState.Gate)
        {
            string missing = Path.Combine(Path.GetTempPath(), $"p1998-readme-not-here-{Guid.NewGuid():N}.csv");
            var id = Content.TableId.MobFlees;
            var original = Content.Spec(id);
            try
            {
                Content.ReplaceSpecForTests(id, original with { PathOverride = missing });
                TestProcessState.LoadContent();

                var error = Assert.Throws<InvalidOperationException>(() => Content.RenderTableReadmeBlock(out _));

                Assert.Contains("MobFlees.csv", error.Message);
                Assert.Contains("FILE NOT FOUND", error.Message);
            }
            finally
            {
                Content.ReplaceSpecForTests(id, original);
                TestProcessState.LoadContent();
            }
        }
    }

    /// <summary>The pre-load fallbacks use the same spec objects as <see cref="Content.Load"/>. Changing a
    /// spec's in-process path seam must redirect the fallback without changing the fallback itself.</summary>
    [Fact]
    public void LazyFallbacksResolveTheirFilesThroughTheSpec()
    {
        lock (TestProcessState.Gate)
        {
            AssertFallbackUsesSpec(Content.TableId.ObjectFlagOverrides, ObjectFlags.OpenOverrides,
                "777,0x0F,test\n", "Obj", "777");
            AssertFallbackUsesSpec(Content.TableId.Obj533Fix, TileTranslation.OpenObj533,
                "777,suppress,0,0,0,0,free\n", "Legacy", "777");
            AssertFallbackUsesSpec(Content.TableId.Tile533Map, TileTranslation.OpenSheet2,
                "777,1,888\n", "StartLegacy", "777");
        }
    }

    [Fact]
    public void EmptySuppliedHeaderUsesTheFilesOwnHeader()
    {
        string path = Path.Combine(Path.GetTempPath(), $"p1998-empty-spec-header-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(path, "Id,Name\n1,alpha\n");
            var spec = new TableSpec("empty-header.csv", header: []) { PathOverride = path };

            var table = Content.OpenTable(spec);

            Assert.Null(spec.Header);
            var row = Assert.Single(table);
            Assert.Equal("1", row.Require("Id"));
            Assert.Equal("alpha", row.Require("Name"));
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup of a test fixture */ }
        }
    }

    /// <summary>A retired per-file variable cannot redirect a table. The file still opens beneath
    /// <c>P1998_GAME_DATA</c>; restoring the old environment read makes this fact fail on the missing path.</summary>
    [Fact]
    public void A_retired_per_table_override_is_ignored()
    {
        lock (TestProcessState.Gate)
        {
            string? previous = Environment.GetEnvironmentVariable("P1998_MOB_FLEES");
            try
            {
                Environment.SetEnvironmentVariable("P1998_MOB_FLEES",
                    Path.Combine(Path.GetTempPath(), $"p1998-retired-{Guid.NewGuid():N}.csv"));

                var table = Content.OpenTable(Content.TableId.MobFlees);

                Assert.Equal(Shared.RepoPaths.GameData("MobFlees.csv"), table.Path);
                Assert.NotEmpty(table);
            }
            finally
            {
                Environment.SetEnvironmentVariable("P1998_MOB_FLEES", previous);
            }
        }
    }

    [Fact]
    public void PublishedSpecificationListCannotReplaceAnEntry()
    {
        var list = Assert.IsAssignableFrom<IList<TableSpec>>(Content.TableSpecifications);

        Assert.Throws<NotSupportedException>(() => list[0] = list[0]);
    }

    [Fact]
    public void MissingLuaScriptIncludesItsSpecifiedConsequence()
    {
        lock (TestProcessState.Gate)
        {
            const string consequence = "test script consequence";
            string missing = Path.Combine(Path.GetTempPath(), $"p1998-script-not-here-{Guid.NewGuid():N}.lua");
            var id = Content.TableId.SpellVerbs;
            var original = Content.Spec(id);
            try
            {
                Content.ReplaceSpecForTests(id, original with
                {
                    PathOverride = missing,
                    MissingConsequence = consequence,
                });
                TestProcessState.LoadContent();

                Assert.Contains(Content.LoadReport.Problems,
                    problem => problem.Contains(original.File) && problem.Contains(consequence));
            }
            finally
            {
                Content.ReplaceSpecForTests(id, original);
                TestProcessState.LoadContent();
            }
        }
    }

    private static void AssertFallbackUsesSpec(Content.TableId id, Func<Shared.CsvTable> open,
                                               string contents, string column, string expected)
    {
        string path = Path.Combine(Path.GetTempPath(), $"p1998-spec-fallback-{Guid.NewGuid():N}.csv");
        var original = Content.Spec(id);
        try
        {
            File.WriteAllText(path, contents);
            Content.ReplaceSpecForTests(id, original with { PathOverride = path });

            var table = open();

            Assert.Equal(path, table.Path);
            Assert.Equal(expected, Assert.Single(table).Require(column));
        }
        finally
        {
            Content.ReplaceSpecForTests(id, original);
            try { File.Delete(path); } catch { /* best-effort cleanup of a test fixture */ }
        }
    }
}
