using Shared;

namespace Server;

/// <summary>The kind of content input described by a <see cref="TableSpec"/>.</summary>
internal enum ContentTableKind
{
    Csv,
    Lua,
}

/// <summary>One file-backed content input. The ordered inventory is the single source for its path,
/// optional supplied header, missing-file consequence and position in the load report.</summary>
internal sealed record TableSpec
{
    internal string EnvironmentVariable { get; init; }
    internal string File { get; init; }
    internal ContentTableKind Kind { get; init; }
    internal IReadOnlyList<string>? Header { get; init; }
    internal string? MissingConsequence { get; init; }

    internal TableSpec(string environmentVariable, string file,
                       ContentTableKind kind = ContentTableKind.Csv,
                       IReadOnlyList<string>? header = null,
                       string? missingConsequence = null)
    {
        EnvironmentVariable = environmentVariable;
        File = file;
        Kind = kind;
        Header = header is { Count: > 0 } ? Array.AsReadOnly(header.ToArray()) : null;
        MissingConsequence = missingConsequence;
    }
}

public static partial class Content
{
    public const string TableReadmeStartMarker = "<!-- generated: tables -->";
    public const string TableReadmeEndMarker = "<!-- /generated -->";

    internal enum TableId
    {
        ObjectFlagOverrides,
        Obj533Fix,
        Tile533Map,
        MapIndex,
        MobFlees,
        MobStationary,
        Mobs,
        Items,
        Warps,
        Spawns,
        AreaSpawns,
        AreaSpawnsTrap,
        AreaSpawnsCrafting,
        ServerTuning,
        EraFeatures,
        Npcs,
        MinorQuests,
        ShopStock,
        ShopBuysFrom,
        Paths,
        LevelExp,
        SpellLevels,
        Spells,
        SpellEffects,
        SpellText,
        SpellLearnCosts,
        Mob5xPalettes,
        ArmorDyeRamps,
        Maps,
        MobDrops,
        CraftingToggles,
        WarpQuestLocks,
        ArmorQuests,
        MythicCaves,
        MythicAlliances,
        ArenaDoors,
        EventCaveTiers,
        EventCaves,
        MusicTracks,
        MapBgm,
        Inns,
        ForageAreas,
        HarvestNodes,
        MobSpells,
        MobChatter,
        MobSpawnRules,
        MobBosses,
        PathHalls,
        GatewayGates,
        WorldMapDests,
        WorldMapTriggers,
        FallRooms,
        AmbushBursts,
        AmbushConfig,
        BoardLocations,
        ShopCatalogues,
        SpellParams,
        SpellVerbs,
        ItemParams,
        ItemVerbs,
        NpcDialog,
        MobAi,
        Pets,
        WeaponProcs,
        Traps,
        Morphs,
        SpellMods,
        NpcAbilities,
        PathGrowth,
        DoorObjects,
        Doors,
        MapCells,
    }

    private static readonly TableSpec[] TableSpecs =
    [
        new("P1998_OBJECT_FLAG_OVERRIDES", "ObjectFlagOverrides.csv", header: ["Obj", "Flag", "Note"]),
        new("P1998_OBJ533_FIX", "Obj533Fix.csv",
            header: ["Legacy", "Action", "Replacement", "FiveId", "Flag495", "Flag533", "Scope"],
            missingConsequence: "5.33 will over-block ~18k cells"),
        new("P1998_TILE533_MAP", "Tile533Map.csv", header: ["StartLegacy", "Count", "Start533"],
            missingConsequence: "5.33 sheet-2 cells (30% of terrain) will be blank"),
        new("P1998_MAP_INDEX", "map_index.csv"),
        new("P1998_MOB_FLEES", "MobFlees.csv"),
        new("P1998_MOB_STATIONARY", "MobStationary.csv"),
        new("P1998_MOBS", "mobs.csv"),
        new("P1998_ITEMS", "Items.csv"),
        new("P1998_WARPS", "Warps.csv"),
        new("P1998_SPAWNS", "Spawns.csv"),
        new("P1998_AREASPAWNS", "AreaSpawns.csv"),
        new("P1998_AREASPAWNS_TRAP", "AreaSpawnsTrap.csv"),
        new("P1998_AREASPAWNS_CRAFT", "AreaSpawnsCrafting.csv"),
        new("P1998_SERVER_TUNING", "ServerTuning.csv"),
        new("P1998_ERA_FEATURES", "EraFeatures.csv"),
        new("P1998_NPCS", "NPCs.csv"),
        new("P1998_MINORQUESTS", "MinorQuests.csv"),
        new("P1998_SHOPSTOCK", "ShopStock.csv"),
        new("P1998_SHOPBUYSFROM", "ShopBuysFrom.csv"),
        new("P1998_PATHS", "Paths.csv"),
        new("P1998_LEVELEXP", "LevelExp.csv"),
        new("P1998_SPELL_LEVELS", "SpellLevels.csv"),
        new("P1998_SPELLS", "Spells.csv"),
        new("P1998_SPELL_FX", "spell_effects.csv"),
        new("P1998_SPELL_TEXT", "SpellText.csv"),
        new("P1998_SPELL_COSTS", "SpellLearnCosts.csv"),
        new("P1998_MOB_PALETTES_5X", "Mob5xPalettes.csv"),
        new("P1998_ARMOR_DYE_RAMPS", "ArmorDyeRamps.csv"),
        new("P1998_MAPS_FULL", "Maps.csv"),
        new("P1998_MOB_DROPS", "MobDrops.csv"),
        new("P1998_CRAFTING_TOGGLES", "CraftingToggles.csv"),
        new("P1998_WARP_QUEST_LOCKS", "WarpQuestLocks.csv"),
        new("P1998_ARMOR_QUESTS", "ArmorQuests.csv"),
        new("P1998_MYTHIC_CAVES", "MythicCaves.csv"),
        new("P1998_MYTHIC_ALLIANCES", "MythicAlliances.csv"),
        new("P1998_ARENA_DOORS", "ArenaDoors.csv"),
        new("P1998_EVENT_CAVE_TIERS", "EventCaveTiers.csv"),
        new("P1998_EVENT_CAVES", "EventCaves.csv"),
        new("P1998_MUSIC_TRACKS", "MusicTracks.csv"),
        new("P1998_MAP_BGM", "MapBgm.csv"),
        new("P1998_INNS", "Inns.csv"),
        new("P1998_FORAGE", "ForageAreas.csv"),
        new("P1998_HARVEST", "HarvestNodes.csv"),
        new("P1998_MOB_SPELLS", "MobSpells.csv"),
        new("P1998_MOB_CHATTER", "MobChatter.csv"),
        new("P1998_MOB_SPAWN_RULES", "MobSpawnRules.csv"),
        new("P1998_MOB_BOSSES", "MobBosses.csv"),
        new("P1998_PATHHALLS", "PathHalls.csv"),
        new("P1998_GATEWAY", "GatewayGates.csv"),
        new("P1998_WORLDMAP_DESTS", "WorldMapDests.csv"),
        new("P1998_WORLDMAP_TRIGGERS", "WorldMapTriggers.csv"),
        new("P1998_FALLROOMS", "FallRooms.csv"),
        new("P1998_AMBUSH_BURSTS", "AmbushBursts.csv"),
        new("P1998_AMBUSH_CONFIG", "AmbushConfig.csv"),
        new("P1998_BOARD_LOCATIONS", "BoardLocations.csv"),
        new("P1998_SHOP_CATALOGUES", "ShopCatalogues.csv"),
        new("P1998_SPELL_PARAMS", "SpellParams.csv"),
        new("P1998_SPELL_VERBS", "spell_verbs.lua", ContentTableKind.Lua),
        new("P1998_ITEM_PARAMS", "ItemParams.csv"),
        new("P1998_ITEM_VERBS", "item_verbs.lua", ContentTableKind.Lua),
        new("P1998_NPC_DIALOG", "npc_dialog.lua", ContentTableKind.Lua),
        new("P1998_MOB_AI", "mob_ai.lua", ContentTableKind.Lua),
        new("P1998_PETS", "Pets.csv"),
        new("P1998_WEAPON_PROCS", "WeaponProcs.csv"),
        new("P1998_TRAPS", "Traps.csv"),
        new("P1998_MORPHS", "Morphs.csv"),
        new("P1998_SPELL_MODS", "SpellMods.csv"),
        new("P1998_NPC_ABILITIES", "NpcAbilities.csv"),
        new("P1998_PATH_GROWTH", "PathGrowth.csv"),
        new("P1998_DOOR_OBJECTS", "DoorObjects.csv"),
        new("P1998_DOORS", "Doors.csv"),
        new("P1998_MAP_CELLS", "MapCells.csv"),
    ];

    private static readonly IReadOnlyList<TableSpec> ReadOnlyTableSpecs = Array.AsReadOnly(TableSpecs);

    /// <summary>All 68 CSV tables and four Lua scripts, in load/report order.</summary>
    internal static IReadOnlyList<TableSpec> TableSpecifications => ReadOnlyTableSpecs;

    internal static TableSpec Spec(TableId id)
    {
        lock (TableSpecs) return TableSpecs[(int)id];
    }

    internal static TableSpec ReplaceSpecForTests(TableId id, TableSpec replacement)
    {
        lock (TableSpecs)
        {
            var previous = TableSpecs[(int)id];
            TableSpecs[(int)id] = replacement;
            return previous;
        }
    }

    internal static CsvTable OpenTable(TableId id) => OpenTable(Spec(id));

    internal static CsvTable OpenTable(TableSpec spec)
    {
        if (spec.Kind != ContentTableKind.Csv)
            throw new InvalidOperationException($"Programming error: {spec.File} is not a CSV table");
        string? path = ResolvePath(spec.EnvironmentVariable, spec.File);
        return spec.Header is null
            ? Csv.Open(spec.File, path)
            : Csv.Open(spec.File, path, spec.Header.ToArray());
    }

    /// <summary>Render the generated README block from the declared CSV specs and the last load report.</summary>
    public static string RenderTableReadmeBlock() => RenderTableReadmeBlock(out _);

    /// <summary>Render the generated README block, refusing a degraded load so bad counts cannot be committed.</summary>
    public static string RenderTableReadmeBlock(out int csvCount)
    {
        var csvSpecs = TableSpecs.Where(spec => spec.Kind == ContentTableKind.Csv).ToArray();
        csvCount = csvSpecs.Length;
        var problems = csvSpecs
            .Select(spec => (Spec: spec, Load: LoadReport[spec.File]))
            .Where(entry => entry.Load is null || !entry.Load.Ok)
            .Select(entry => entry.Load?.Problem ?? $"{entry.Spec.File}: absent from the load report")
            .ToArray();
        if (problems.Length > 0)
            throw new InvalidOperationException(
                "README table generation refused because CSV content did not load cleanly:\n" +
                string.Join('\n', problems));

        var lines = new List<string>
        {
            TableReadmeStartMarker,
            "| File | Environment override | Rows read | Rows kept | Header |",
            "|---|---|---:|---:|---|",
        };
        foreach (var spec in csvSpecs)
        {
            var load = LoadReport[spec.File]
                ?? throw new InvalidOperationException($"Load report has no entry for {spec.File}");
            string header = spec.Header is null
                ? "from file"
                : $"supplied (`{string.Join("`, `", spec.Header)}`)";
            lines.Add(FormattableString.Invariant(
                $"| `{spec.File}` | `{spec.EnvironmentVariable}` | {load.Read:N0} | {load.Kept:N0} | {header} |"));
        }
        lines.Add(TableReadmeEndMarker);
        return string.Join('\n', lines);
    }
}
