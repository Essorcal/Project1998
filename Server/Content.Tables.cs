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
    internal string File { get; init; }
    internal ContentTableKind Kind { get; init; }
    internal IReadOnlyList<string>? Header { get; init; }
    internal string? MissingConsequence { get; init; }
    /// <summary>In-process path seam for tests that exercise one file without relocating all content.</summary>
    internal string? PathOverride { get; init; }

    internal TableSpec(string file,
                       ContentTableKind kind = ContentTableKind.Csv,
                       IReadOnlyList<string>? header = null,
                       string? missingConsequence = null)
    {
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
        new("ObjectFlagOverrides.csv", header: ["Obj", "Flag", "Note"]),
        new("Obj533Fix.csv",
            header: ["Legacy", "Action", "Replacement", "FiveId", "Flag495", "Flag533", "Scope"],
            missingConsequence: "5.33 will over-block ~18k cells"),
        new("Tile533Map.csv", header: ["StartLegacy", "Count", "Start533"],
            missingConsequence: "5.33 sheet-2 cells (30% of terrain) will be blank"),
        new("map_index.csv"),
        new("MobFlees.csv"),
        new("MobStationary.csv"),
        new("mobs.csv"),
        new("Items.csv"),
        new("Warps.csv"),
        new("Spawns.csv"),
        new("AreaSpawns.csv"),
        new("AreaSpawnsTrap.csv"),
        new("AreaSpawnsCrafting.csv"),
        new("ServerTuning.csv"),
        new("EraFeatures.csv"),
        new("NPCs.csv"),
        new("MinorQuests.csv"),
        new("ShopStock.csv"),
        new("ShopBuysFrom.csv"),
        new("Paths.csv"),
        new("LevelExp.csv"),
        new("SpellLevels.csv"),
        new("Spells.csv"),
        new("spell_effects.csv"),
        new("SpellText.csv"),
        new("SpellLearnCosts.csv"),
        new("Mob5xPalettes.csv"),
        new("ArmorDyeRamps.csv"),
        new("Maps.csv"),
        new("MobDrops.csv"),
        new("CraftingToggles.csv"),
        new("WarpQuestLocks.csv"),
        new("ArmorQuests.csv"),
        new("MythicCaves.csv"),
        new("MythicAlliances.csv"),
        new("ArenaDoors.csv"),
        new("EventCaveTiers.csv"),
        new("EventCaves.csv"),
        new("MusicTracks.csv"),
        new("MapBgm.csv"),
        new("Inns.csv"),
        new("ForageAreas.csv"),
        new("HarvestNodes.csv"),
        new("MobSpells.csv"),
        new("MobChatter.csv"),
        new("MobSpawnRules.csv"),
        new("MobBosses.csv"),
        new("PathHalls.csv"),
        new("GatewayGates.csv"),
        new("WorldMapDests.csv"),
        new("WorldMapTriggers.csv"),
        new("FallRooms.csv"),
        new("AmbushBursts.csv"),
        new("AmbushConfig.csv"),
        new("BoardLocations.csv"),
        new("ShopCatalogues.csv"),
        new("SpellParams.csv"),
        new("spell_verbs.lua", ContentTableKind.Lua),
        new("ItemParams.csv"),
        new("item_verbs.lua", ContentTableKind.Lua),
        new("npc_dialog.lua", ContentTableKind.Lua),
        new("mob_ai.lua", ContentTableKind.Lua),
        new("Pets.csv"),
        new("WeaponProcs.csv"),
        new("Traps.csv"),
        new("Morphs.csv"),
        new("SpellMods.csv"),
        new("NpcAbilities.csv"),
        new("PathGrowth.csv"),
        new("DoorObjects.csv"),
        new("Doors.csv"),
        new("MapCells.csv"),
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

    internal static TableSpec OverridePathForTests(TableId id, string path)
    {
        lock (TableSpecs)
        {
            var previous = TableSpecs[(int)id];
            TableSpecs[(int)id] = previous with { PathOverride = path };
            return previous;
        }
    }

    internal static CsvTable OpenTable(TableId id) => OpenTable(Spec(id));

    internal static CsvTable OpenTable(TableSpec spec)
    {
        if (spec.Kind != ContentTableKind.Csv)
            throw new InvalidOperationException($"Programming error: {spec.File} is not a CSV table");
        string? path = ResolvePath(spec);
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
            "| File | Rows read | Rows kept | Header |",
            "|---|---:|---:|---|",
        };
        foreach (var spec in csvSpecs)
        {
            var load = LoadReport[spec.File]
                ?? throw new InvalidOperationException($"Load report has no entry for {spec.File}");
            string header = spec.Header is null
                ? "from file"
                : $"supplied (`{string.Join("`, `", spec.Header)}`)";
            lines.Add(FormattableString.Invariant(
                $"| `{spec.File}` | {load.Read:N0} | {load.Kept:N0} | {header} |"));
        }
        lines.Add(TableReadmeEndMarker);
        return string.Join('\n', lines);
    }
}
