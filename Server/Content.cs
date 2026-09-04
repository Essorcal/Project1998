using Shared;

namespace Server;

/// <summary>
/// In-memory game-content registries loaded ONCE at startup from EXTERNAL, gitignored data
/// (RTK-derived — see docs §17.1). The loader lives in the repo; the data does not, keeping this a
/// logic-only server. Everything here is read-only after <see cref="Load"/>, so it is safe to share
/// across all sessions without locking. Missing data degrades gracefully (empty registries + a log).
/// </summary>
public static partial class Content
{

    // Build a "first occurrence wins" index (TryAdd keeps the first, matching the replaced FirstOrDefault).
    private static Dictionary<TK, TV> IndexFirst<TK, TV>(IEnumerable<TV> items, Func<TV, TK> key, IEqualityComparer<TK>? cmp = null) where TK : notnull
    {
        var d = new Dictionary<TK, TV>(cmp);
        foreach (var v in items) d.TryAdd(key(v), v);
        return d;
    }

    /// <summary>Replace every file-backed content registry from disk in its required dependency order.</summary>
    /// <remarks>Any runtime caller must hold the world reload gate by going through
    /// <see cref="World.ReloadFromDisk"/>. Startup calls this before the World and its scheduler exist; tests
    /// must use TestProcessState.LoadContent so environment mutation and direct loads stay serialized. Loaders
    /// and <c>Load</c> read no content facade on the loading thread, as the facade-read counter test proves;
    /// the builder-backed facade view remains as a safety net.</remarks>
    public static void Load()
    {
        var snapshotBuilder = BeginSnapshotBuild();
        try
        {
            // Every content file goes through this: it records the table in `entries` so the load report can
            // say what happened to all 72 of them, in load order. `ToLoad` is a method group, evaluated at the
            // end, so the counts it captures are the ones the loader finished with. `entries` is a LOCAL, so a
            // second Load() building its own report cannot interleave with this one, and LoadReport joins the
            // immutable snapshot published at the end exactly like every other registry here.
            Csv.Warn = Log.Warn;   // Shared cannot see Server.Log; hand it over before the first file is opened
            var entries = new List<Func<TableLoad>>();
            CsvTable T(TableSpec spec)
            {
                if (spec.Kind != ContentTableKind.Csv)
                    throw new InvalidOperationException($"Programming error: {spec.File} is not a CSV table");
                string? path = ResolvePath(spec.EnvironmentVariable, spec.File);
                var t = spec.Header is null
                    ? Csv.Open(spec.File, path)
                    : Csv.Open(spec.File, path, spec.Header.ToArray());
                entries.Add(() => t.ToLoad(spec.MissingConsequence));
                return t;
            }
            // The Lua scripts have no rows, so they report 1/1 loaded and 1/0 rejected — see TableLoad.IsScript.
            // They belong in the same report as the CSVs because an operator must account for every content
            // input, and a rejected script is the loudest thing a reload can have to say.
            bool Script<T>(TableSpec spec, Func<string?, (bool Ok, T? Prepared)> prepare,
                Action<T> stage) where T : class
            {
                if (spec.Kind != ContentTableKind.Lua)
                    throw new InvalidOperationException($"Programming error: {spec.File} is not a Lua script");
                var scriptPath = ResolvePath(spec.EnvironmentVariable, spec.File);
                bool present = scriptPath is not null && File.Exists(scriptPath);
                var (ok, prepared) = prepare(scriptPath);
                if (prepared is not null) stage(prepared);
                var entry = new TableLoad(spec.File, scriptPath, present ? CsvStatus.Ok : CsvStatus.Missing,
                                          Read: 1, Kept: ok ? 1 : 0, Array.Empty<string>())
                { IsScript = true };
                entries.Add(() => entry);
                return ok;
            }

            snapshotBuilder.ObjectFlagOverrides = ObjectFlags.PrepareOverrides(
                T(Spec(TableId.ObjectFlagOverrides)));
            snapshotBuilder.TileTranslations = TileTranslation.PrepareReload(
                T(Spec(TableId.Obj533Fix)),
                T(Spec(TableId.Tile533Map)));
            var maps = LoadMaps(T(Spec(TableId.MapIndex)));
            Maps = maps;
            var mobFleeOverrides = LoadMobFlees(T(Spec(TableId.MobFlees)));
            MobFleeOverrides = mobFleeOverrides;
            var mobStationaryOverrides = LoadMobStationary(T(Spec(TableId.MobStationary)));
            MobStationaryOverrides = mobStationaryOverrides;
            var mobs = LoadMobs(T(Spec(TableId.Mobs)), mobFleeOverrides, mobStationaryOverrides);
            Mobs = mobs;
            var items = LoadItems(T(Spec(TableId.Items)));
            Items = items;
            LoadStepForTests?.Invoke("ItemsLoaded");
            var warps = LoadWarps(T(Spec(TableId.Warps)), maps);
            Warps = warps;
            Spawns = LoadSpawns(T(Spec(TableId.Spawns)));
            // Base area spawns + trap-ambush populations (tiger cave, rabbit boss-tier, trapdoor spiders) that RTK
            // spawns via trap/mob_spawn.lua rather than handleSpawn (rare-boss rows carry RespawnSec; generated by
            // re/extract_trap_spawns.py). Concatenated into a LOCAL and assigned to AreaSpawns ONCE — so a
            // Dependency order stays explicit in the unpublished builder: base rows first, then both generated
            // sources, before the combined list is assigned once.
            AreaSpawns = LoadAreaSpawns(T(Spec(TableId.AreaSpawns)), grouped: true)
                .Concat(LoadAreaSpawns(T(Spec(TableId.AreaSpawnsTrap)), grouped: false))
                // …plus the crafting nodes (ore veins, ginko trees), which come from RTK's OTHER two spawner
                // NPCs — mining/woodcuttingSpawnHandler.lua. Kept in their own file for the same reason as the
                // trap rows: re-running the main extractor must not be able to drop them.
                .Concat(LoadAreaSpawns(T(Spec(TableId.AreaSpawnsCrafting)), grouped: true))
                .ToList();
            var tuning = LoadTuning(T(Spec(TableId.ServerTuning)));
            Tuning = tuning;
            int eraDate = tuning.TryGetValue("EraDate", out var configuredEraDate)
                ? (int)configuredEraDate
                : Shared.EraCalendar.DefaultDate;
            var eraCalendar = Shared.EraCalendar.PrepareReload(
                eraDate, T(Spec(TableId.EraFeatures)));
            snapshotBuilder.EraCalendar = eraCalendar;
            // Era.Has resolves to the prepared calendar on this loading thread; serving threads keep the old one.
            // Era gating remains ambient until its prepared value exposes the same fail-open query semantics.
            var npcs = LoadNpcs(T(Spec(TableId.Npcs)), maps);
            NpcByIdIndex = npcs.ToDictionary(n => n.Id);   // derived from npcs; this dependency order matters
            Npcs = npcs;                                   // only while the unpublished builder is assembled
            MinorQuests = LoadMinorQuests(T(Spec(TableId.MinorQuests)));
            ShopStock = LoadShopStock(T(Spec(TableId.ShopStock)));
            ShopBuysFrom = LoadShopBuysFrom(T(Spec(TableId.ShopBuysFrom)));
            var (paths, pathRanks, pathBase, pathIcon) = LoadPaths(T(Spec(TableId.Paths)));
            Paths = paths;
            PathRanks = pathRanks;
            PathBase = pathBase;
            PathIcon = pathIcon;
            LevelExp = LoadLevelExp(T(Spec(TableId.LevelExp)));
            var spellLevelOverrides = LoadSpellLevels(T(Spec(TableId.SpellLevels)));
            SpellLevelOverrides = spellLevelOverrides;
            var spells = LoadSpells(T(Spec(TableId.Spells)), spellLevelOverrides);
            Spells = spells;
            // O(1) lookup indexes (0.1) — rebuilt every Load()/@reload so they swap with the lists above. Nothing
            // in Load reads them (RollDrops is the only in-Content consumer, and it runs at mob-death, not load).
            ItemByIdIndex = IndexFirst(items, i => i.Id);
            ItemByKeyIndex = IndexFirst(items, i => i.Key, StringComparer.OrdinalIgnoreCase);
            MobByIdIndex = IndexFirst(mobs, m => m.Id);
            MobByKeyIndex = IndexFirst(mobs, m => m.Key, StringComparer.OrdinalIgnoreCase);
            SpellByIdIndex = IndexFirst(spells, s => s.Id);
            SpellByKeyIndex = IndexFirst(spells, s => s.Key, StringComparer.OrdinalIgnoreCase);
            LadderOf = BuildSpellLadders(
                spells,
                sp => snapshotBuilder.SpellFx.GetValueOrDefault(sp.Key));
            // name -> id, first wins. BASE names go in first so a string that is one path's class name and
            // another's rank title (Paths.csv has a few) always resolves to the class, never the rank.
            var pathIdByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var pathRankByName = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in paths)
                if (!string.IsNullOrEmpty(kv.Value)) { pathIdByName.TryAdd(kv.Value, kv.Key); pathRankByName.TryAdd(kv.Value, (kv.Key, 0)); }
            foreach (var (id, ladder) in pathRanks)
                for (int m = 1; m < ladder.Length; m++)
                    if (ladder[m].Length > 0) { pathIdByName.TryAdd(ladder[m], id); pathRankByName.TryAdd(ladder[m], (id, m)); }
            PathIdByNameIndex = pathIdByName;
            PathRankByNameIndex = pathRankByName;
            SpellFx = LoadSpellFx(T(Spec(TableId.SpellEffects)));
            SpellTexts = LoadSpellTexts(T(Spec(TableId.SpellText)));
            SpellCosts = LoadSpellCosts(T(Spec(TableId.SpellLearnCosts)));
            Mob5xPalettes = LoadMob5xPalettes(T(Spec(TableId.Mob5xPalettes)));   // (Look,Colour)->Palette, V533-only remap
            ArmorDyeRamps = LoadArmorDyeRamps(T(Spec(TableId.ArmorDyeRamps)));
            MapMeta = LoadMapMeta(T(Spec(TableId.Maps)));   // region + warpOut for Gateway
            MobDrops = LoadMobDrops(T(Spec(TableId.MobDrops)));
            CraftingToggleOverrides = LoadCraftingToggles(T(Spec(TableId.CraftingToggles)));
            WarpQuestLocks = LoadWarpQuestLocks(T(Spec(TableId.WarpQuestLocks)));
            ArmorQuestGates = LoadArmorQuestGates(T(Spec(TableId.ArmorQuests)));
            var mythicCaves = LoadMythicCaves(T(Spec(TableId.MythicCaves)));
            MythicCaveTiles = mythicCaves   // build the derived tile index from the same local list
                .SelectMany(c => c.Tiles.Select(t => (key: (c.EntranceMap, t.X, t.Y), cave: c)))
                .ToDictionary(e => e.key, e => e.cave);
            MythicCaves = mythicCaves;
            MythicAlliances = LoadMythicAlliances(T(Spec(TableId.MythicAlliances)));
            var arenaDoors = LoadArenaDoors(T(Spec(TableId.ArenaDoors)));
            ArenaDoorTiles = arenaDoors   // build the derived tile index from the same local list
                .SelectMany(d => d.Tiles.Select(t => (key: (d.Map, t.X, t.Y), door: d)))
                .ToDictionary(e => e.key, e => e.door);
            ArenaDoors = arenaDoors;
            EventCaveBands = LoadEventCaveBands(T(Spec(TableId.EventCaveTiers)));
            var eventCaves = LoadEventCaves(T(Spec(TableId.EventCaves)));
            EventCaveTiles = eventCaves   // build the derived tile index from the same local list
                .SelectMany(c => c.Tiles.Select(t => (key: (c.EntranceMap, t.X, t.Y), cave: c)))
                .ToDictionary(e => e.key, e => e.cave);
            EventCaves = eventCaves;
            var musicTracks = LoadMusicTracks(T(Spec(TableId.MusicTracks)));
            MusicTracks = musicTracks;
            var (bgmZones, defaultBgm, defaultBgmNew) = LoadBgmZones(
                T(Spec(TableId.MapBgm)), musicTracks);
            BgmZones = bgmZones;
            DefaultBgm = defaultBgm;
            DefaultBgmNew = defaultBgmNew;
            BgmByMap = BuildBgmMap(bgmZones, maps, warps);
            Inns = LoadInns(T(Spec(TableId.Inns)));
            ForageAreas = LoadForageAreas(T(Spec(TableId.ForageAreas)));
            HarvestNodes = LoadHarvestNodes(T(Spec(TableId.HarvestNodes)));
            MobSpells = LoadMobSpells(T(Spec(TableId.MobSpells)));
            MobChatter = LoadMobChatter(T(Spec(TableId.MobChatter)));
            MobSpawnRules = LoadMobSpawnRules(T(Spec(TableId.MobSpawnRules)));
            MobBosses = LoadMobBosses(T(Spec(TableId.MobBosses)));
            PathHalls = LoadPathHalls(T(Spec(TableId.PathHalls)));
            GatewayRegions = LoadGatewayGates(T(Spec(TableId.GatewayGates)));
            WorldDests = LoadWorldDests(T(Spec(TableId.WorldMapDests)));
            WorldMapTriggers = LoadWorldTriggers(T(Spec(TableId.WorldMapTriggers)));
            FallRooms = LoadFallRooms(T(Spec(TableId.FallRooms)));
            var ambushBursts = LoadAmbushBursts(T(Spec(TableId.AmbushBursts)));
            AmbushBursts = ambushBursts;
            Ambushes = LoadAmbushConfig(T(Spec(TableId.AmbushConfig)), ambushBursts);
            BoardLocations = LoadBoardLocations(T(Spec(TableId.BoardLocations)));
            ShopCatalogues = LoadShopCatalogues(T(Spec(TableId.ShopCatalogues)));
            SpellParams = LoadKeyedRows(T(Spec(TableId.SpellParams)));
            // The four Lua files compile into candidates: a broken edit is REJECTED and the
            // previously-loaded script keeps running. RejectedScripts records which ones didn't take so @reload can
            // say so to the GM's face. Accepted candidates commit only after the matching row snapshot publishes.
            var rejected = new List<string>();
            if (!Script(Spec(TableId.SpellVerbs), SpellScript.PrepareReload,
                        prepared => snapshotBuilder.SpellScript = prepared)) rejected.Add(Spec(TableId.SpellVerbs).File);
            ItemParams = LoadKeyedRows(T(Spec(TableId.ItemParams)));   // same "whole row keyed by `key`" shape as SpellParams
            if (!Script(Spec(TableId.ItemVerbs), ItemScript.PrepareReload,
                        prepared => snapshotBuilder.ItemScript = prepared)) rejected.Add(Spec(TableId.ItemVerbs).File);
            if (!Script(Spec(TableId.NpcDialog), NpcScript.PrepareReload,
                        prepared => snapshotBuilder.NpcScript = prepared)) rejected.Add(Spec(TableId.NpcDialog).File);
            if (!Script(Spec(TableId.MobAi), MobScript.PrepareReload,
                        prepared => snapshotBuilder.MobScript = prepared)) rejected.Add(Spec(TableId.MobAi).File);
            RejectedScripts = rejected;
            // Phase-1 spell-DATA tables (extracted from Content.cs literals; see re/extract_spell_tables.py).
            PetSpells = LoadPets(T(Spec(TableId.Pets)));
            WeaponProcs = LoadWeaponProcs(T(Spec(TableId.WeaponProcs)));
            TrapSpells = LoadTrapSpells(T(Spec(TableId.Traps)));
            (MorphSpells, MorphDispatchSpells) = LoadMorphs(T(Spec(TableId.Morphs)));
            (RageAmount, EnchantSpells) = LoadSpellMods(T(Spec(TableId.SpellMods)));
            NpcCompositions = LoadNpcCompositions(T(Spec(TableId.NpcAbilities)));
            PathGrowth = LoadPathGrowth(T(Spec(TableId.PathGrowth)));
            (DoorSwaps, DoorDeltas, DoorDefaultOpen) = LoadDoorObjects(T(Spec(TableId.DoorObjects)));
            snapshotBuilder.Doors = LoadDoors(T(Spec(TableId.Doors)));
            (MapCells, var mapCellCount) = LoadMapCells(T(Spec(TableId.MapCells)));
            MapCellCount = mapCellCount;
            // The startup summary. This replaces a hand-written line that named 36 registries and could say
            // nothing at all about the other 36 — MobSpells, Doors, SpellParams, NpcAbilities, MapCells and
            // WarpQuestLocks among them could load zero rows in silence. Problems go out first and through
            // Log.Warn (so they carry the `!!` marker the rest of the codebase greps for); the census follows,
            // several tables per line, and every entry still carries its file name so it greps too.
            var report = new ContentLoadReport(entries.Select(f => f()));
            LoadReport = report;
            foreach (var problem in report.Problems) Log.Warn("content: " + problem);
            foreach (var line in report.Census()) Log.Info(line);
            if (maps.Count == 0 || mobs.Count == 0)
                Log.Warn("content: no maps and/or no mobs — run re/build_map_index.py and check game-data/mobs.csv");
            PublishSnapshot(snapshotBuilder);
        }
        finally
        {
            if (snapshotBuilder.EraCalendar is { } eraCalendar) Shared.EraCalendar.EndReload(eraCalendar);
            EndSnapshotBuild(snapshotBuilder);
        }
    }

    /// <summary>
    /// Hot-reload every file-backed registry WITHOUT a restart (the <c>@reload</c> GM command), so content
    /// fixes ship without kicking players. Re-runs the exact ordered <see cref="Load"/> sequence in an
    /// unpublished builder, then publishes the immutable snapshot with one write and commits the era calendar,
    /// Doors configuration, object/tile tables and four Lua hosts at that boundary under the shared Lua gate.
    /// Readers therefore see all registries and their derived indexes from the old load or all of them from the new load. Returns
    /// a one-line count summary.
    ///
    /// SCOPE: file-backed content only (every registry above is CSV/Lua-backed now — map BGM moved to
    /// MapBgm.csv, so there's no compile-time content table left that a restart would be needed for). The
    /// world population is rebuilt separately by the @reload caller (World.RebuildPopulation), which re-reads
    /// spawns/NPCs so added/removed/repositioned rows take effect.
    /// Runtime callers must hold the gate owned by <see cref="World.ReloadFromDisk"/> around this method and
    /// every cache/population refresh that follows it.
    /// </summary>
    public static string Reload()
    {
        try { Load(); }
        catch (Exception e)
        {
            throw new InvalidOperationException($"{e.Message} Reload failed (previous content kept).", e);
        }

        var summary = $"{Maps.Count} maps, {Mobs.Count} mobs, {Items.Count} items, {Warps.Count} warps, " +
                      $"{Spawns.Count + AreaSpawns.Count} spawns, {Npcs.Count} npcs, {Spells.Count} spells, {ShopStock.Count} shops, " +
                      $"{CraftingToggleOverrides.Count} crafting-toggle overrides, " +
                      $"era {(Era.Today?.ToString("yyyy-MM-dd") ?? "off")} ({Shared.EraCalendar.FeatureCount} dated features)";
        // A table that lost its file, its header or all of its rows is the other thing @reload has to say out
        // loud. The detail is in the log, but the person who just edited a CSV is standing at the console, not
        // reading logs — and this is the reply to a command they typed. Scripts are left out: the REJECTED
        // lead below already names those, and more precisely. Nothing is added on a healthy reload.
        var degraded = LoadReport.Where(t => !t.Ok && !t.IsScript).Select(t => t.Name).ToArray();
        if (degraded.Length > 0)
            summary = $"*** {degraded.Length} table(s) DEGRADED (see log): {string.Join(", ", degraded)} *** — {summary}";
        // A rejected .lua is the single most important thing @reload can tell you: your edit did NOT take, the
        // old script is still running, and the reason is in the server log. Lead with it.
        return RejectedScripts.Count == 0 ? summary
             : $"*** REJECTED (still running the previous version, see log): {string.Join(", ", RejectedScripts)} *** — {summary}";
    }

    /// <summary>Lua files whose most recent (re)load was rejected for a compile/shape error — their previously
    /// loaded version is still live. Empty when everything took. See <see cref="Reload"/>.</summary>
    public static IReadOnlyList<string> RejectedScripts
    {
        get => _snapshotBuilder?.RejectedScripts ?? Snapshot.RejectedScripts;
        private set => Builder.RejectedScripts = value;
    }

    /// <summary>What every CSV and Lua input opened by the last <see cref="Load"/> did: its status, and how
    /// many rows it read, kept and skipped. The report is a member of the published snapshot, so a reader
    /// always sees the report for the same load as every registry.
    ///
    /// <para>This is the thing the old startup line could not be: it covers ALL of them. The hand-written
    /// summary named 36 registries, which meant MobSpells, Doors, SpellParams, NpcAbilities, MapCells,
    /// WarpQuestLocks and about twenty-five others could load zero rows and say nothing — and the reader
    /// underneath swallowed a missing file and a parse failure alike. ContentSmokeTests asserts a floor over
    /// this for every table, so a registry that collapses fails CI instead of shipping.</para></summary>
    public static ContentLoadReport LoadReport
    {
        get => _snapshotBuilder?.LoadReport ?? Snapshot.LoadReport;
        private set => Builder.LoadReport = value;
    }

    /// <summary>Offline check of the registries + fuzzy lookups (run via <c>--selftest</c>).</summary>
    public static void SelfTest()
    {
        Load();
        void Line(string s) => Log.Info(s);

        Line("--- FindMap (exact id / exact name / substring / subsequence) ---");
        foreach (var q in new[] { "0", "kugnae", "buya", "walsuk tavern", "kgne" })
        {
            var m = FindMap(q);
            Line($"  @warp {q,-16} -> " + (m is null ? "(no match)" : $"map {m.Id} '{m.Name}' {m.Xs}x{m.Ys}"));
        }

        Line("--- FindMob (name / key / id / fuzzy) ---");
        foreach (var q in new[] { "rabbit", "1", "great_horns", "great horns", "grhrn", "fox" })
        {
            var mob = FindMob(q);
            Line($"  @summon {q,-14} -> " + (mob is null ? "(no match)" : $"'{mob.Name}' look {mob.Look} c{mob.Color} {mob.Hp}hp {mob.Exp}xp"));
        }

        Line("--- FindItem (name / key / id) ---");
        foreach (var q in new[] { "apple", "stick", "leather", "sword", "0" })
        {
            var it = FindItem(q);
            Line($"  @item {q,-12} -> " + (it is null ? "(no match)"
                : $"#{it.Id} '{it.Name}' type{it.Type} icon{it.Icon} look{it.Look} {(it.IsEquip ? $"EQUIP slot{it.EquipSlot}" : "use")}"));
        }

        Line("--- SearchMaps(\"buya\", 5) ---");
        foreach (var m in SearchMaps("buya", 5)) Line($"    {m.Id}: {m.Name} ({m.Xs}x{m.Ys})");
        Line("--- SearchMobs(\"wolf\", 5) ---");
        foreach (var m in SearchMobs("wolf", 5)) Line($"    {m.Name} look {m.Look} c{m.Color} {m.Hp}hp");
        Line("--- SearchItems(\"sword\", 5) ---");
        foreach (var i in SearchItems("sword", 5)) Line($"    #{i.Id} {i.Name} type{i.Type} dam{i.Dam} icon{i.Icon}");

        // --- Magic engine: archetype coverage + formula evaluation against known RTK values ---
        Line($"--- Spell fx: {SpellFx.Count} rows ---");
        var byArch = SpellFx.Values.GroupBy(f => f.Archetype).OrderByDescending(g => g.Count());
        Line("    " + string.Join("  ", byArch.Select(g => $"{g.Key}={g.Count()}")));
        // A representative caster: level 50, will 30, grace 20, might 40, 200 mana, 1000 HP.
        var vars = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["player.level"] = 50,
            ["player.will"] = 30,
            ["player.grace"] = 20,
            ["player.might"] = 40,
            ["player.magic"] = 200,
            ["player.maxMagic"] = 200,
            ["player.health"] = 1000,
            ["player.maxHealth"] = 1000,
        };
        Line("--- Formula.Eval (level50 will30 grace20 might40 mana200 hp1000) ---");
        foreach (var key in new[] { "spark_mage", "heal_mage", "invoke_mage", "thunder_bolt_mage", "singe_mage" })
        {
            if (!SpellFx.TryGetValue(key, out var fx)) { Line($"    {key,-20} (no fx row)"); continue; }
            string amt = string.IsNullOrEmpty(fx.AmountExpr) ? "" : $" amount={Formula.Eval(fx.AmountExpr, vars):0}";
            string hc = string.IsNullOrEmpty(fx.HealthCost) ? "" : $" healthCost={Formula.Eval(fx.HealthCost, vars):0}";
            Line($"    {key,-20} {fx.Archetype,-11} mana={fx.Mana,-4}{amt}{hc}  [{fx.AmountExpr}]");
        }
        // spot-check the arithmetic evaluator itself (independent of any spell row)
        Line("--- Formula sanity ---");
        foreach (var (expr, want) in new (string, double)[]
                 {
                     ("15 + math.floor(player.level / 2) + math.floor((player.will + 3) / 4)", 48),  // spark @50/30
                     ("math.ceil(player.magic * 2.15)", 430),
                     ("100 + (player.level * 2) + math.floor(((player.will + 1) / 2) * 2)", 230),
                     ("math.floor(player.maxMagic * .4)", 80),                                        // invoke cost
                 })
        {
            double got = Formula.Eval(expr, vars);
            Line($"    {(Math.Abs(got - want) < 0.5 ? "ok " : "XX ")}{got,6:0} (want {want,4:0})  {expr}");
        }

        // --- Effect graphic resolution (pcalign ladder → Effect.tbl id) ---
        Line("--- EffectAnim (spell → Effect.tbl id) ---");
        foreach (var (key, path) in new[]
                 {
                     ("spark_mage", 3), ("glimpse_of_the_void_mage", 3), ("bolt_mage", 3),
                     ("thunder_bolt_mage", 3), ("heal_mage", 3), ("ancestors_touch_mage", 3),
                     ("invoke_mage", 3), ("might_mage", 3),
                 })
        {
            if (!SpellFx.TryGetValue(key, out var fx)) { Line($"    {key,-24} (no fx)"); continue; }
            Line($"    {key,-24} arch={fx.Archetype,-11} pcalign={fx.PcAlign,-5} -> anim {EffectAnim(fx, path),3}  sound {EffectSound(fx, path)}");
        }

        bool spellsOk = SpellFx.Count > 0
            && SpellFx.TryGetValue("spark_mage", out var spk) && spk.Archetype == "Damage"
            && Math.Abs(Formula.Eval(spk.AmountExpr, vars) - 48) < 0.5
            && Math.Abs(Formula.Eval("math.ceil(player.magic * 2.15)", vars) - 430) < 0.5
            && EffectAnim(spk, 3) == 28                                          // spark → Effect.tbl 28
            && SpellFx.TryGetValue("heal_mage", out var hl) && EffectAnim(hl, 3) == 5;   // unaligned heal → 5

        // --- Background music: track names + area zoning (MusicTracks.csv / MapBgm.csv) ---
        Line($"--- Music: {MusicTracks.Count(t => t.Set == MusicSet.Old && t.Name.Length > 0)} named midis + " +
             $"{MusicTracks.Count(t => t.Set == MusicSet.New && !t.Playlist)} 5.x mp3s / " +
             $"{MusicTracks.Count(t => t.Playlist && !t.Shuffle)} ordered + " +
             $"{MusicTracks.Count(t => t.Shuffle)} shuffled playlists, {BgmZones.Count} zones, " +
                 $"{BgmByMap.Count} maps resolved, " +
             $"default {(DefaultBgm is null ? "(none)" : $"{DefaultBgm.Value.bgm} '{TrackName(DefaultBgm.Value.bgm)}'")}" +
             $" / 5.x {(DefaultBgmNew is null ? "(none)" : $"{DefaultBgmNew.Value.bgm} '{TrackName(DefaultBgmNew.Value.bgm, MusicSet.New)}'")} ---");
        foreach (var q in new[] { "mist", "tiger", "mon", "6", "10", "nope" })
            Line($"    @music {q,-6} -> " + (FindTrack(q) is { } t ? $"track {t.Id} '{t.Name}' type{t.Type}" : "(no match)"));
        // The 5.x set is a SEPARATE id space: 2/3/4 must resolve to the mp3s, not the midis of the same id.
        foreach (var q in new[] { "2", "underwater", "nexus", "902", "pole" })
            Line($"    @music {q,-10} (new) -> " + (FindTrack(q, MusicSet.New) is { } t
                ? $"track {t.Id} '{t.Name}' type{t.Type}{(t.Playlist ? " playlist" : "")}" : "(no match)"));
        // (map, expected track) — the six areas the assignment was specified for, plus a building inside each
        // hub (which must resolve to the SAME track so walking through a door never restarts the song).
        var bgmWant = new (ushort Map, string Track)[]
        {
            (137, "mist"), (3812, "mist"),        // Arctic Land / Arctic Tavern
            (3815, "mist"), (3816, "mist"),       // Crystalline Chapel / Kamchatka Ballroom — same song
            (3819, "mist"),                       // Lovers' Lake, an outdoor spot off the village
            (330, "tiger"), (365, "tiger"),       // Buya / Buya Salon
            (114, "dark"), (457, "dark"),         // Hamgyong Nam-Do / Ruined House (Haunted Houses)
            (3800, "sorrow"), (3806, "sorrow"),   // KaMing's Encampment / KaMing
            (0, "dragon"), (1011, "dragon"),      // Kugnae / Kugnae Gathering
            (41, "lake"),                         // Mythic Nexus
            // Unlisted maps that must inherit their area through the warp graph, NOT the default track:
            (332, "tiger"),                       // Spring Tavern — a shop off Buya
            (367, "tiger"),                       // Eldritch Sanctum — 2 hops in from Buya (the login case)
            (2, "dragon"),                        // Walsuk Tavern — a shop off Kugnae
            (1013, "mist"),                       // Haeng Tavern — inside Arctic Village
            (324, "mist"), (511, "mist"),         // Kwi-sin Shrine / Snow Dungeon — off the village, spill-only
            (1121, "mist"),                       // Sanhae Valley — 3 hops out through the Arctic
        };
        bool bgmOk = true;
        foreach (var (map, want) in bgmWant)
        {
            var got = BgmFor(map);
            string name = got is null ? "(none)" : TrackName(got.Value.bgm);
            bool hit = name.Equals(want, StringComparison.OrdinalIgnoreCase);
            bgmOk &= hit;
            Line($"    {(hit ? "ok " : "XX ")}map {map,-6} {(Maps.TryGetValue(map, out var bm) ? bm.Name : "?"),-22} -> " +
                 $"{name,-8} zone '{BgmZoneOf(map)}' (want {want})");
        }
        int resolved = Maps.Values.Count(m => BgmFor(m.Id) is not null);
        bool sticky = resolved > 0 && resolved < Maps.Count;   // some maps have no warp path to any zone
        Line($"    {resolved}/{Maps.Count} maps resolved to a track; the rest keep whatever is playing " +
             $"(and start on the default at login)");

        // The 5.x soundtrack rides the SAME zone/spill resolution, so the only thing that can go wrong is a
        // zone whose Track5x didn't resolve — which shows up as a midi id leaking onto the mp3 channel.
        // Every 5.x map pick must be an ORDERED playlist (.LST): a single mp3 never advances off its one
        // song, and a SHUFFLED list (.LSR) eventually stalls dead on the client's index collision — see the
        // MusicTrack doc. Both failures are silent (the area just goes quiet), so they are asserted here.
        var bgm5xWant = new (ushort Map, string Track)[]
        {
            (0, "town2"), (2, "town2"),           // Kugnae / Walsuk Tavern (spill)
            (330, "town3"), (332, "town3"),       // Buya / Spring Tavern (spill)
            (137, "town10"), (1013, "town10"),    // Arctic Land / Haeng Tavern (spill)
            (114, "cave5"), (457, "cave5"),       // Hamgyong Nam-Do / Ruined House
            (3800, "field3"),                     // KaMing's Encampment
            (41, "nexus"),                        // Mythic Nexus — ClassicTK's own 908
        };
        bool bgm5xOk = true;
        foreach (var (map, want) in bgm5xWant)
        {
            var got = BgmFor(map, MusicSet.New);
            string name = got is null ? "(none)" : TrackName(got.Value.bgm, MusicSet.New);
            var track = got is null ? null
                : MusicTracks.FirstOrDefault(t => t.Set == MusicSet.New && t.Id == got.Value.bgm);
            bool ordered = track is { Playlist: true, Shuffle: false };
            bool hit = name.Equals(want, StringComparison.OrdinalIgnoreCase) && got?.type == 1 && ordered;
            bgm5xOk &= hit;
            string kind = track is null ? "?"
                        : !track.Playlist ? "SINGLE — never advances"
                        : track.Shuffle ? "SHUFFLED — will stall dead"
                                          : "ordered playlist";
            Line($"    {(hit ? "ok " : "XX ")}map {map,-6} (5.x) -> {name,-8} " +
                 $"id {(got?.bgm.ToString() ?? "-"),-4} type{got?.type} {kind} (want {want})");
        }

        // --- PvP arena doors: every configured door must lead somewhere renderable, and each destination
        // must have its return leg in Warps.csv (a one-way door strands the player in the arena).
        Line($"--- Arena doors: {ArenaDoors.Count} doors / {ArenaDoorTiles.Count} tiles ---");
        bool doorsOk = ArenaDoors.Count > 0;
        foreach (var d in ArenaDoors)
        {
            bool dest = Maps.ContainsKey(d.DestMap);
            bool back = Warps.Any(w => w.Key.m == d.DestMap && w.Value.m == d.Map);
            doorsOk &= dest && back;
            string band = d.MaxLevel > 0 ? $"{d.MinLevel}-{d.MaxLevel}"
                        : d.MaxVita > 0 ? $"{d.MinLevel}+, <= {d.MaxVita}v/{d.MaxMana}m"
                        : $"{d.MinLevel}+";
            Line($"    {(dest && back ? "ok " : "XX ")}map {d.Map} {string.Join("/", d.Tiles.Select(t => $"{t.X}:{t.Y}")),-13} -> " +
                 $"{d.DestMap} '{(Maps.TryGetValue(d.DestMap, out var am) ? am.Name : "?")}' " +
                 $"level {band}{(dest ? "" : "  [NO MAP DATA]")}{(back ? "" : "  [NO RETURN WARP]")}");
        }

        bool ok = Maps.Count > 0 && Mobs.Count > 0 && Items.Count > 0
                  && FindMap("kugnae") is not null && FindMob("rabbit") is not null && spellsOk
                  && bgmOk && bgm5xOk && sticky && doorsOk;
        Line(ok ? "SELFTEST: PASS" : "SELFTEST: FAIL (empty registry or missing expected entry)");
    }

    // ---- fuzzy ranking (shared by maps + mobs) ----

    private static T? BestByName<T>(IEnumerable<T> items, string q, Func<T, string> name) where T : class =>
        RankByName(items, q, name).FirstOrDefault();

    // Rank: exact (0) < prefix (1) < substring (2) < subsequence (3); ties broken by shorter name.
    // A blank query returns everything alphabetically (so "@maps" with no arg lists all).
    private static IEnumerable<T> RankByName<T>(IEnumerable<T> items, string q, Func<T, string> name)
    {
        q = q.Trim().ToLowerInvariant();
        return items
            .Select(it => (it, s: Score((name(it) ?? "").ToLowerInvariant(), q), n: name(it) ?? ""))
            .Where(t => t.s >= 0)
            .OrderBy(t => t.s).ThenBy(t => t.n.Length).ThenBy(t => t.n, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.it);
    }

    private static int Score(string name, string q)
    {
        if (q.Length == 0) return 4;            // no filter -> keep all (alphabetical)
        if (name.Length == 0) return -1;
        if (name == q) return 0;
        if (name.StartsWith(q)) return 1;
        if (name.Contains(q)) return 2;
        return IsSubsequence(q, name) ? 3 : -1; // "grhrn" matches "great horns"
    }

    private static bool IsSubsequence(string q, string name)
    {
        int i = 0;
        foreach (var c in name) if (i < q.Length && c == q[i]) i++;
        return i == q.Length;
    }

    // Resolve a content file under the game-data root: per-file env override first, else
    // <root>/game-data/<parts...>. This used to carry its own copy of the walk up to the repo root, one of
    // five that had drifted apart; Shared/RepoPaths is now the single implementation, and its class doc
    // explains why every resolver has to agree on the fallback (briefly: a layout where the database
    // resolved but the content did not gave a server that started, listened and accepted logins into a
    // world with zero maps, zero mobs and zero NPCs, with nothing in the log that read as an error).
    private static string? ResolvePath(string envVar, params string[] parts) =>
        RepoPaths.GameData(envVar, parts);
}
