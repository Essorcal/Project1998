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

    // Era-gating overrides for crafting skills (see Server/CraftingToggles.cs + docs/common/Crafting-Values.md).
    // File is optional and sparse: only skills listed here override CraftingToggles.DefaultDisabled;
    // anything absent keeps the code-level default. Columns: Skill,Enabled(0/1).
    public static IReadOnlyDictionary<string, bool> CraftingToggleOverrides { get; private set; } =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    // ---- Location / warp geometry (Tier-1 extraction; game-data/*.csv) ------------------------------
    // RTK/RE geometry that used to be hard-coded in the game logic, moved to flat files so it hot-reloads via
    // @reload like every other registry. Consumers read these Content.* properties.

    // Which of the two soundtracks a track belongs to. They are separate id SPACES, not one list — mp3 ids
    // 2/3/4 in the 5.x set collide with midi ids 2/3/4 in the old one — so every lookup takes a set.
    //   Old = the 12 stock midis (1.mid..12.mid). In NexusTK.snd on 4.95 and Snd.dat on 5.33, so BOTH
    //         clients can play them, and they stay the default.
    //   New = the 25 mp3s + 52 playlists in the 5.33 client's Mus000.dat. 5.33 ONLY: the 4.95 client has
    //         an mp3 engine (see Session.SendMusic) but ships none of the files, so offering it there
    //         would be silence. See docs/5.x/Wire-Divergences.md §"0x19 music".
    public enum MusicSet { Old, New }

    // The client's background tracks, by id and by NAME (the files are numbered, but the songs have real
    // names — see MusicTracks.csv, which is also what lets "@music mist" work). Type is the 0x19 channel:
    // 2 = midi, 1 = mp3. Playlist is true for the 5.x .LST/.LSR entries, where the id names a list of ten
    // tracks the client cycles by itself rather than one song.
    //
    // Shuffle separates the two kinds of playlist, and map music MUST NOT use a shuffled one. Both cycle
    // fine, but the 5.33 advance (0x4a7b40, on WM_USER+8) picks the next entry as `rand() % count + 1` for
    // an .LSR, and the play function (0x4a5f80 @0x4a6078) early-outs to a NO-OP when the index it is handed
    // equals the one already playing. On that 1-in-10 collision nothing is opened, and because the previous
    // stream has already ended there is no further end-of-stream callback — the music is dead until the
    // server sends another 0x19. An .LST advances `cur + 1` (wrapping 10 -> 1), which can never collide.
    // Measured live 2026-08-22: 2 stalls in 40 shuffled advances, 0 in 24 ordered ones.
    public sealed record MusicTrack(ushort Id, string Name, byte Type, MusicSet Set, bool Playlist,
                                    bool Shuffle = false);
    public static IReadOnlyList<MusicTrack> MusicTracks { get; private set; } = new List<MusicTrack>();

    // Area -> BGM track (BgmFor). A design assignment, not RTK data: RTK's own Maps table has one track
    // (902) on 9799 of 9850 maps, and the 4.95 client files carry no map->track table at all. Zones match by
    // explicit map id/range first, then by map-NAME glob; a map in no zone keeps whatever is already playing
    // (see Session.PlayMapMusic) so walking into a shop or a cave never restarts the song. See MapBgm.csv.
    // Track/Type is the Old (midi) pick, Track5x/Type5x the New (5.x mp3-playlist) one; a zone that names no
    // Track5x falls back to its midi, which on 5.33 still plays.
    public sealed record BgmZone(string Zone, ushort Track, byte Type, ushort Track5x, byte Type5x,
        IReadOnlyList<(ushort Lo, ushort Hi)> Maps, IReadOnlyList<string> Names);
    public static IReadOnlyList<BgmZone> BgmZones { get; private set; } = new List<BgmZone>();

    // Resolved map -> track, built once at load (BuildBgmMap): the zones' own maps at Hops 0, then every
    // other map inherits its NEAREST zone through the warp graph. That spill is what makes a building or a
    // cave play its area's theme without being listed, and — unlike leaving it to "whatever is already
    // playing" — it also works when you LOG IN inside one, where there is no previous song to inherit.
    public sealed record BgmPick(ushort Track, byte Type, ushort Track5x, byte Type5x, string Zone, int Hops);
    private static Dictionary<ushort, BgmPick> _bgmByMap = new();

    /// <summary>The track to start on a zone-less map when nothing is playing yet (a fresh session): the
    /// "Default" row of MapBgm.csv. Null leaves such a session silent until it reaches a zoned map.</summary>
    public static (ushort bgm, byte type)? DefaultBgm { get; private set; }

    /// <summary>The <see cref="MusicSet.New"/> half of the "Default" row (its <c>Track5x</c>).</summary>
    public static (ushort bgm, byte type)? DefaultBgmNew { get; private set; }

    /// <summary>The fresh-session fallback for one soundtrack, falling back to the midi when the Default row
    /// names no 5.x track.</summary>
    public static (ushort bgm, byte type)? DefaultBgmFor(MusicSet set) =>
        set == MusicSet.New ? DefaultBgmNew ?? DefaultBgm : DefaultBgm;

    // Per-class level-up HP/MP gain ranges (game-data/PathGrowth.csv), keyed by path id (0 Peasant / 1
    // Warrior / 2 Rogue / 3 Mage / 4 Poet). Each is the pair of args to Random.Shared.Next(min, max) — max is
    // EXCLUSIVE, matching the original C# switch. The which-stat-is-primary logic stays in Session.LevelUp.
    public static IReadOnlyDictionary<int, (int HpMin, int HpMax, int MpMin, int MpMax)> PathGrowth { get; private set; } =
        new Dictionary<int, (int, int, int, int)>();
    /// <summary>Level-up gain ranges for a path, falling back to Peasant (0) then a hardcoded default.</summary>
    public static (int HpMin, int HpMax, int MpMin, int MpMax) PathGrowthFor(int path) =>
        PathGrowth.TryGetValue(path, out var g) ? g : PathGrowth.TryGetValue(0, out var p) ? p : (45, 56, 32, 37);

    // Named engine scalars a deployment may retune without a rebuild (game-data/ServerTuning.csv, key,value).
    // These sit on the tier-1/tier-3 line — real mechanics, but harmless to expose as hand-editable config. Typed
    // accessors fall back to the historical hardcoded default if the key is absent, so a missing file is safe.
    public static IReadOnlyDictionary<string, double> Tuning { get; private set; } =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    private static double Tune(string key, double dflt) => Tuning.TryGetValue(key, out var v) ? v : dflt;
    public static int MailMinLevel => (int)Tune("MailMinLevel", 10);   // min level to view/send nmail
    public static int SpeechRange  => (int)Tune("SpeechRange", 8);     // tiles (Chebyshev) an NPC "hears" from
    public static uint BankMax     => (uint)Tune("BankMax", 100_000_000);   // per-account coin cap

    // NOTE: EraDate is deliberately NOT a Tune() accessor here. It is read by Shared.EraCalendar so the
    // login server sees the same value; a second copy on this side could drift from it by a stale default.
    // Reach it via Era.Today / EraCalendar.RawDate.
    // Highest minor-quest tier a path leader will hand out: 1 = Minor only (4.95 — the only tier that
    // existed), 2 adds Major, 3 adds Epic. The Major/Epic rows stay in MinorQuests.csv either way; this only
    // gates whether the "which type of quest?" menu is offered at all. See Server/MinorQuest.cs.
    public static int MinorQuestTiers => (int)Tune("MinorQuestTiers", 1);
    // Hours a path leader makes you wait after COMPLETING a minor quest before handing out another. RTK starts
    // its cooldown only when you ABANDON one, which leaves the completion path with no limit at all: turn one
    // in, say "quest" again, and the next is yours — an exp faucet whose only cost is one kill (the reward
    // scales with level, so it's worth more the higher you climb). 24 = one quest per real-world day, per the
    // user (2026-08-12). Real hours, not game time: the timer is a unix-second deadline like every other
    // persisted cooldown, so logging out doesn't pause it. 0 restores RTK's behavior.
    //
    // The ABANDON cooldown stays on RTK's own per-tier value (Minor = 2h) rather than following this. It
    // gates a quest you dropped without being paid for, so it isn't part of the reward rate limit — and
    // making a failed quest cost a full day would just teach players to sit on one they can't finish.
    public static int MinorQuestCooldownHours => (int)Tune("MinorQuestCooldownHours", 24);
    // (SilentDelReason is GONE, 2026-08-07. It existed to probe whether an out-of-range 0x10 reason was the
    // client's silent path; the live answer was no — 15 renders "<item> removed.", the same line reason 0
    // gives, so the handler clamps/defaults and NO reason byte is silent. Every path that used it has since
    // moved to a real reason (bank deposit and shop sale both hand the item over: 10, "You gave X."), and a
    // path that must truly say nothing sends no 0x10 at all — see EquipDelReason.)
    // Equipping is the one removal that ought to be TRULY silent: the item didn't leave you, it moved onto
    // your body, and the real game says nothing. Suppressing the 0x10 entirely was tried (default -1) and is
    // WRONG — it leaves a ghost row in the bag that can't be dropped, equipped or used, because the server
    // has already dropped the item while the client still draws it.
    //
    // The reason it can't work: the equip window and the bag are SEPARATE client structures. The bag is a
    // 164-byte-stride array and the ONLY thing that clears an entry is 0x48f0b0, reached only from the 0x10
    // handler (0x48fe10) — which range-checks the slot and ignores the reason byte completely. The 0x37
    // equip-window entry never touches that array, so it cannot stand alone.
    //
    // Reason 12 is the one code that says NOTHING, so equipping gets both: the bag entry is cleared and the
    // player isn't told they "used" their armour. Full table swept live 2026-08-07 (@delreason):
    //   0 "<item> removed."   1 "You dropped"   2 "You ate"     3 "You smoked" (herb/sonhi pipes)
    //   4 "You threw"         5 "You shot"      6 "You used"    7 "You posted"
    //   8 "<item> decayed."   9 "You gave"     10 "You sold"   11 "<item> removed."
    //  12 SILENT             13 "<item> broken."               14+ all "<item> removed."
    public static int EquipDelReason => (int)Tune("EquipDelReason", 12);
    /// <summary>Open the board request straight into the MAILBOX when the player has unread n-mail, instead
    /// of the board list. 'm' is armed only while the mail arrow is up and sends the same `3b 01 00` as 'b',
    /// so this would be the only way to make 'm' behave like a mailbox key — at the cost of 'b' doing the same
    /// while mail is unread. 0 = always show the board list (Mailbox is still its last entry).
    /// <para>DEFAULT 0 BECAUSE 1 HARD-FREEZES THE 4.95 CLIENT (live 2026-08-08): answering sub-1 "Show Board"
    /// with a POSTS body (0x31 flags2=4) instead of the LIST body locks the client up — it stops pumping
    /// input entirely and never sends another packet. The identical posts bytes render fine when they answer
    /// sub-2, so the window ctor 0x406e80(1) evidently arms a list-shaped parse that a posts body walks off
    /// the end of. Don't turn this back on without RE'ing that ctor first. See Session.HandleBoard case 1.</para></summary>
    public static bool MailFirstOnBoard => Tune("MailFirstOnBoard", 0) != 0;

    /// <summary>Patch a peer's appearance with <c>0x1d</c> (look-update-in-place) instead of the
    /// despawn(<c>0x0E</c>) + respawn(<c>0x33</c>) pair. The old pair exists because a bare <c>0x33</c>
    /// re-send orphans the entity and leaks its nameplate marker; <c>0x1d</c> sidesteps that entirely by
    /// never destroying or creating anything. Morph and stealth still take the full path regardless —
    /// see Session.RefreshAppearance. 0 = always use the old pair.</summary>
    public static bool LookUpdateInPlace => Tune("LookUpdateInPlace", 1) != 0;

    /// <summary>Draw nameplates over other players. The plate is rendered from the NAME string in the
    /// <c>0x33</c> spawn, so sending an empty name is a pure server-side way to suppress it — no client
    /// patch needed (cf. re/patch_no_nametag.py, which does it on disk). Applies to PEERS only; your own
    /// name is never in a peer packet. 0 = no plates.</summary>
    public static bool ShowNameplates => Tune("ShowNameplates", 1) != 0;

    /// <summary>Which nations the user-list window (0x36) gets columns and a name for — the ids sent in the
    /// 0x59 sub-1 town table. Default is the three this server actually plays: 0 Neutral, 1 Koguryo,
    /// 2 Buya. Deliberately NOT the same thing as <c>Character.Nations</c>, which is the HUD crest id space
    /// (0x08 stats, calibrated via @nat) and must keep all 8 entries.
    /// <para>A nation absent from this table cannot be resolved by the client: it scans the table for the
    /// viewer's own nation id and falls back to entry 0 when it misses, at which point every row whose
    /// nation nibble isn't 0 drops out of the columns. So a player whose nation is off this list sees an
    /// empty window, not a partial one.</para></summary>
    /// <para>ServerTuning holds scalars only, so this is a BITMASK over the nation ids: bit i = nation i.
    /// Default 7 = 0b111 = Neutral + Koguryo + Buya. 255 restores all eight.</para></summary>
    // User-list name colours — row byte +2, a palette index measured live (`@users hunters`). 0..15 is the
    // standard 16-colour palette and **0 paints black on black**, which is what made every name invisible
    // until 2026-08-08. Same three cases RTK colours (default / same clan / GM), in the palette this client
    // actually has. Values above 15 reach further into the 256-entry palette if a deployment wants them.
    // Highest rule wins: self, then GM, then clan, then default. 0 turns an OPTIONAL rule off — safe to
    // overload that way because 0 is the invisible colour and can never be a deliberate choice. Only
    // UserListColorDefault has no off switch.
    //   0 black(invisible) 1 dk blue  2 dk green 3 teal      4 dk red  5 magenta 6 brown   7 lt gray
    //   8 dk gray          9 lt blue 10 lt green 11 lt cyan 12 red    13 pink   14 yellow 15 white
    public static int UserListColorDefault => (int)Tune("UserListColorDefault", 15);   // white
    public static int UserListColorClan    => (int)Tune("UserListColorClan",    10);   // light green — RTK's same-clan highlight
    public static int UserListColorGm      => (int)Tune("UserListColorGm",      12);   // red
    public static int UserListColorSelf    => (int)Tune("UserListColorSelf",    14);   // yellow — no RTK equivalent, ours

    public static IReadOnlyList<byte> UserListNations
    {
        get
        {
            int mask = (int)Tune("UserListNationMask", 7);
            var ids = new List<byte>();
            for (byte i = 0; i < 8; i++) if ((mask & (1 << i)) != 0) ids.Add(i);
            return ids.Count > 0 ? ids : new List<byte> { 0 };   // the client bails on an empty table
        }
    }

    /// <summary>Replace every file-backed content registry from disk in its required dependency order.</summary>
    /// <remarks>Any runtime caller must hold the world reload gate by going through
    /// <see cref="World.ReloadFromDisk"/>. Startup calls this before the World and its scheduler exist; tests
    /// must use TestProcessState.LoadContent so environment mutation and direct loads stay serialized.</remarks>
    public static void Load()
    {
        // Every content file goes through this: it records the table in `entries` so the load report can
        // say what happened to all 68 of them, in load order. `ToLoad` is a method group, evaluated at the
        // end, so the counts it captures are the ones the loader finished with. `entries` is a LOCAL, so a
        // second Load() building its own report cannot interleave with this one, and LoadReport swaps by
        // reference at the end exactly like every other registry here.
        Csv.Warn = Log.Warn;   // Shared cannot see Server.Log; hand it over before the first file is opened
        var entries = new List<Func<TableLoad>>();
        CsvTable T(string envVar, string file)
        {
            var t = Csv.Open(file, ResolvePath(envVar, file));
            entries.Add(t.ToLoad);
            return t;
        }
        // The Lua scripts have no rows, so they report 1/1 loaded and 1/0 rejected — see TableLoad.IsScript.
        // They belong in the same report as the CSVs because "68 tables" is what an operator has to account
        // for, and a rejected script is the loudest thing a reload can have to say.
        bool Script(string envVar, string file, Func<string?, bool> load)
        {
            var scriptPath = ResolvePath(envVar, file);
            bool present = scriptPath is not null && File.Exists(scriptPath);
            bool ok = load(scriptPath);
            var entry = new TableLoad(file, scriptPath, present ? CsvStatus.Ok : CsvStatus.Missing,
                                      Read: 1, Kept: ok ? 1 : 0, Array.Empty<string>()) { IsScript = true };
            entries.Add(() => entry);
            return ok;
        }

        Maps = LoadMaps(T("P1998_MAP_INDEX", "map_index.csv"));
        MobFleeOverrides = LoadMobFlees(T("P1998_MOB_FLEES", "MobFlees.csv"));   // BEFORE Mobs: LoadMobs folds it in
        MobStationaryOverrides = LoadMobStationary(T("P1998_MOB_STATIONARY", "MobStationary.csv"));   // likewise
        Mobs = LoadMobs(T("P1998_MOBS", "mobs.csv"));
        Items = LoadItems(T("P1998_ITEMS", "Items.csv"));
        Warps = LoadWarps(T("P1998_WARPS", "Warps.csv"));   // needs Maps
        Spawns = LoadSpawns(T("P1998_SPAWNS", "Spawns.csv"));
        // Base area spawns + trap-ambush populations (tiger cave, rabbit boss-tier, trapdoor spiders) that RTK
        // spawns via trap/mob_spawn.lua rather than handleSpawn (rare-boss rows carry RespawnSec; generated by
        // re/extract_trap_spawns.py). Concatenated into a LOCAL and assigned to AreaSpawns ONCE — so a
        // concurrent reader on @reload never sees the base list without its 362 trap mobs (the old two-step
        // assign had that tear window).
        AreaSpawns = LoadAreaSpawns(T("P1998_AREASPAWNS", "AreaSpawns.csv"), grouped: true)
            .Concat(LoadAreaSpawns(T("P1998_AREASPAWNS_TRAP", "AreaSpawnsTrap.csv"), grouped: false))
            // …plus the crafting nodes (ore veins, ginko trees), which come from RTK's OTHER two spawner
            // NPCs — mining/woodcuttingSpawnHandler.lua. Kept in their own file for the same reason as the
            // trap rows: re-running the main extractor must not be able to drop them.
            .Concat(LoadAreaSpawns(T("P1998_AREASPAWNS_CRAFT", "AreaSpawnsCrafting.csv"), grouped: true))
            .ToList();
        Shared.EraCalendar.Reload();   // era date + windows live in Shared (login server shares them)
        // BEFORE LoadNpcs, which now asks it whether an NPC existed yet (NPCs.csv EraFeature). Left where it
        // was, this read the PREVIOUS calendar on @reload, so moving EraDate and reloading placed NPCs by the
        // old date — with nothing to say so, since a wrong era never throws.
        var npcs = LoadNpcs(T("P1998_NPCS", "NPCs.csv"));   // needs Maps + the era calendar
        _npcById = npcs.ToDictionary(n => n.Id);   // assign the index BEFORE the public list, so a reader that
        Npcs = npcs;                               // sees the new Npcs always sees the matching new _npcById
        MinorQuests = LoadMinorQuests(T("P1998_MINORQUESTS", "MinorQuests.csv"));
        ShopStock = LoadShopStock(T("P1998_SHOPSTOCK", "ShopStock.csv"));
        ShopBuysFrom = LoadShopBuysFrom(T("P1998_SHOPBUYSFROM", "ShopBuysFrom.csv"));
        Paths = LoadPaths(T("P1998_PATHS", "Paths.csv"));
        LevelExp = LoadLevelExp(T("P1998_LEVELEXP", "LevelExp.csv"));
        SpellLevelOverrides = LoadSpellLevels(T("P1998_SPELL_LEVELS", "SpellLevels.csv"));   // BEFORE Spells: LoadSpells reads it
        Spells = LoadSpells(T("P1998_SPELLS", "Spells.csv"));
        // O(1) lookup indexes (0.1) — rebuilt every Load()/@reload so they swap with the lists above. Nothing
        // in Load reads them (RollDrops is the only in-Content consumer, and it runs at mob-death, not load).
        _itemById  = IndexFirst(Items, i => i.Id);
        _itemByKey = IndexFirst(Items, i => i.Key, StringComparer.OrdinalIgnoreCase);
        _mobById   = IndexFirst(Mobs, m => m.Id);
        _mobByKey  = IndexFirst(Mobs, m => m.Key, StringComparer.OrdinalIgnoreCase);
        _spellById = IndexFirst(Spells, s => s.Id);
        _spellByKey = IndexFirst(Spells, s => s.Key, StringComparer.OrdinalIgnoreCase);
        _ladderOf = BuildSpellLadders(Spells);
        // name -> id, first wins. BASE names go in first so a string that is one path's class name and
        // another's rank title (Paths.csv has a few) always resolves to the class, never the rank.
        var pathIdByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pathRankByName = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in Paths)
            if (!string.IsNullOrEmpty(kv.Value)) { pathIdByName.TryAdd(kv.Value, kv.Key); pathRankByName.TryAdd(kv.Value, (kv.Key, 0)); }
        foreach (var (id, ladder) in PathRanks)
            for (int m = 1; m < ladder.Length; m++)
                if (ladder[m].Length > 0) { pathIdByName.TryAdd(ladder[m], id); pathRankByName.TryAdd(ladder[m], (id, m)); }
        _pathIdByName = pathIdByName;
        _pathRankByName = pathRankByName;
        SpellFx = LoadSpellFx(T("P1998_SPELL_FX", "spell_effects.csv"));
        SpellTexts = LoadSpellTexts(T("P1998_SPELL_TEXT", "SpellText.csv"));
        SpellCosts = LoadSpellCosts(T("P1998_SPELL_COSTS", "SpellLearnCosts.csv"));
        Mob5xPalettes = LoadMob5xPalettes(T("P1998_MOB_PALETTES_5X", "Mob5xPalettes.csv"));   // (Look,Colour)->Palette, V533-only remap
        ArmorDyeRamps = LoadArmorDyeRamps(T("P1998_ARMOR_DYE_RAMPS", "ArmorDyeRamps.csv"));
        MapMeta = LoadMapMeta(T("P1998_MAPS_FULL", "Maps.csv"));   // region + warpOut for Gateway
        MobDrops = LoadMobDrops(T("P1998_MOB_DROPS", "MobDrops.csv"));
        CraftingToggleOverrides = LoadCraftingToggles(T("P1998_CRAFTING_TOGGLES", "CraftingToggles.csv"));
        WarpQuestLocks = LoadWarpQuestLocks(T("P1998_WARP_QUEST_LOCKS", "WarpQuestLocks.csv"));
        ArmorQuestGates = LoadArmorQuestGates(T("P1998_ARMOR_QUESTS", "ArmorQuests.csv"));
        var mythicCaves = LoadMythicCaves(T("P1998_MYTHIC_CAVES", "MythicCaves.csv"));
        MythicCaveTiles = mythicCaves   // assign the derived tile index BEFORE the public list (same reason as Npcs/_npcById)
            .SelectMany(c => c.Tiles.Select(t => (key: (c.EntranceMap, t.X, t.Y), cave: c)))
            .ToDictionary(e => e.key, e => e.cave);
        MythicCaves = mythicCaves;
        MythicAlliances = LoadMythicAlliances(T("P1998_MYTHIC_ALLIANCES", "MythicAlliances.csv"));
        var arenaDoors = LoadArenaDoors(T("P1998_ARENA_DOORS", "ArenaDoors.csv"));
        ArenaDoorTiles = arenaDoors   // derived tile index first, public list second (same reason as Npcs/_npcById)
            .SelectMany(d => d.Tiles.Select(t => (key: (d.Map, t.X, t.Y), door: d)))
            .ToDictionary(e => e.key, e => e.door);
        ArenaDoors = arenaDoors;
        EventCaveBands = LoadEventCaveBands(T("P1998_EVENT_CAVE_TIERS", "EventCaveTiers.csv"));
        var eventCaves = LoadEventCaves(T("P1998_EVENT_CAVES", "EventCaves.csv"));
        EventCaveTiles = eventCaves   // derived tile index first, public list second (same reason as Npcs/_npcById)
            .SelectMany(c => c.Tiles.Select(t => (key: (c.EntranceMap, t.X, t.Y), cave: c)))
            .ToDictionary(e => e.key, e => e.cave);
        EventCaves = eventCaves;
        MusicTracks = LoadMusicTracks(T("P1998_MUSIC_TRACKS", "MusicTracks.csv"));
        (BgmZones, DefaultBgm, DefaultBgmNew) = LoadBgmZones(T("P1998_MAP_BGM", "MapBgm.csv"));
        _bgmByMap = BuildBgmMap();   // needs Maps + Warps + BgmZones — resolves every map to a track
        Inns = LoadInns(T("P1998_INNS", "Inns.csv"));
        ForageAreas = LoadForageAreas(T("P1998_FORAGE", "ForageAreas.csv"));
        HarvestNodes = LoadHarvestNodes(T("P1998_HARVEST", "HarvestNodes.csv"));
        MobSpells    = LoadMobSpells(T("P1998_MOB_SPELLS", "MobSpells.csv"));
        MobChatter   = LoadMobChatter(T("P1998_MOB_CHATTER", "MobChatter.csv"));
        MobSpawnRules = LoadMobSpawnRules(T("P1998_MOB_SPAWN_RULES", "MobSpawnRules.csv"));
        MobBosses    = LoadMobBosses(T("P1998_MOB_BOSSES", "MobBosses.csv"));
        PathHalls = LoadPathHalls(T("P1998_PATHHALLS", "PathHalls.csv"));
        GatewayRegions = LoadGatewayGates(T("P1998_GATEWAY", "GatewayGates.csv"));
        WorldDests = LoadWorldDests(T("P1998_WORLDMAP_DESTS", "WorldMapDests.csv"));
        WorldMapTriggers = LoadWorldTriggers(T("P1998_WORLDMAP_TRIGGERS", "WorldMapTriggers.csv"));
        FallRooms = LoadFallRooms(T("P1998_FALLROOMS", "FallRooms.csv"));
        AmbushBursts = LoadAmbushBursts(T("P1998_AMBUSH_BURSTS", "AmbushBursts.csv"));
        Ambushes = LoadAmbushConfig(T("P1998_AMBUSH_CONFIG", "AmbushConfig.csv"), AmbushBursts);
        BoardLocations = LoadBoardLocations(T("P1998_BOARD_LOCATIONS", "BoardLocations.csv"));
        ShopCatalogues = LoadShopCatalogues(T("P1998_SHOP_CATALOGUES", "ShopCatalogues.csv"));
        SpellParams = LoadKeyedRows(T("P1998_SPELL_PARAMS", "SpellParams.csv"));
        // The three Lua files load ATOMICALLY (see LuaVerbHost.Load): a broken edit is REJECTED and the
        // previously-loaded script keeps running. RejectedScripts records which ones didn't take so @reload can
        // say so to the GM's face — a silent "reload ok" after a typo is how you end up debugging the wrong thing.
        var rejected = new List<string>();
        if (!Script("P1998_SPELL_VERBS", "spell_verbs.lua", SpellScript.Load)) rejected.Add("spell_verbs.lua");
        ItemParams = LoadKeyedRows(T("P1998_ITEM_PARAMS", "ItemParams.csv"));   // same "whole row keyed by `key`" shape as SpellParams
        if (!Script("P1998_ITEM_VERBS", "item_verbs.lua", ItemScript.Load)) rejected.Add("item_verbs.lua");
        if (!Script("P1998_NPC_DIALOG", "npc_dialog.lua", NpcScript.Load)) rejected.Add("npc_dialog.lua");
        if (!Script("P1998_MOB_AI", "mob_ai.lua", MobScript.Load)) rejected.Add("mob_ai.lua");
        RejectedScripts = rejected;
        // Phase-1 spell-DATA tables (extracted from Content.cs literals; see re/extract_spell_tables.py).
        PetSpells = LoadPets(T("P1998_PETS", "Pets.csv"));
        WeaponProcs = LoadWeaponProcs(T("P1998_WEAPON_PROCS", "WeaponProcs.csv"));
        TrapSpells = LoadTrapSpells(T("P1998_TRAPS", "Traps.csv"));
        (MorphSpells, MorphDispatchSpells) = LoadMorphs(T("P1998_MORPHS", "Morphs.csv"));
        (RageAmount, EnchantSpells) = LoadSpellMods(T("P1998_SPELL_MODS", "SpellMods.csv"));
        NpcCompositions = LoadNpcCompositions(T("P1998_NPC_ABILITIES", "NpcAbilities.csv"));
        PathGrowth = LoadPathGrowth(T("P1998_PATH_GROWTH", "PathGrowth.csv"));
        (DoorSwaps, DoorDeltas, DoorDefaultOpen) = LoadDoorObjects(T("P1998_DOOR_OBJECTS", "DoorObjects.csv"));
        Tuning = LoadTuning(T("P1998_SERVER_TUNING", "ServerTuning.csv"));
        Doors.SetConfig(LoadDoors(T("P1998_DOORS", "Doors.csv")));
        (_mapCells, var mapCellCount) = LoadMapCells(T("P1998_MAP_CELLS", "MapCells.csv"));
        MapCellCount = mapCellCount;
        // The startup summary. This replaces a hand-written line that named 36 registries and could say
        // nothing at all about the other 32 — MobSpells, Doors, SpellParams, NpcAbilities, MapCells and
        // WarpQuestLocks among them could load zero rows in silence. Problems go out first and through
        // Log.Warn (so they carry the `!!` marker the rest of the codebase greps for); the census follows,
        // several tables per line, and every entry still carries its file name so it greps too.
        var report = new ContentLoadReport(entries.Select(f => f()));
        LoadReport = report;
        foreach (var problem in report.Problems) Log.Warn("content: " + problem);
        foreach (var line in report.Census()) Log.Info(line);
        if (Maps.Count == 0 || Mobs.Count == 0)
            Log.Warn("content: no maps and/or no mobs — run re/build_map_index.py and check game-data/mobs.csv");
    }

    /// <summary>
    /// Hot-reload every file-backed registry WITHOUT a restart (the <c>@reload</c> GM command), so content
    /// fixes ship without kicking players. Re-runs the exact ordered <see cref="Load"/> sequence — which
    /// re-reads every CSV and rebuilds the derived <c>_npcById</c> — reassigning the public registries. Each
    /// registry is a lock-free reference, and a reference assignment is atomic, so a reader always sees a whole
    /// old-or-new dictionary, never a torn one (a reader that straddles the swap across two registries is
    /// harmless — they're independent). Returns a one-line count summary.
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
        var before = CaptureReloadTables();
        try { Load(); }
        catch (Exception e)
        {
            var replaced = CaptureReloadTables()
                .Where(kv => before.TryGetValue(kv.Key, out var old) && !ReferenceEquals(old, kv.Value))
                .Select(kv => kv.Key)
                .ToArray();
            string progress = replaced.Length == 0
                ? "No public content tables were replaced (private tables, the era calendar and Lua scripts are not tracked until #33)."
                : $"Public content tables replaced before failure: {string.Join(", ", replaced)}.";
            throw new InvalidOperationException($"{e.Message} {progress}", e);
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

    /// <summary>Snapshot the public reference-backed tables so a failed pre-#33 reload can say which tracked
    /// tables already swapped. Deliberately not exhaustive: private tables, the era calendar and Lua script
    /// hosts stay outside this bounded operator hint until #33 makes the whole load atomic.</summary>
    private static Dictionary<string, object?> CaptureReloadTables() =>
        typeof(Content).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(p => !p.PropertyType.IsValueType && p.GetSetMethod(nonPublic: true)?.IsPrivate == true)
            .OrderBy(p => p.MetadataToken)
            .ToDictionary(p => p.Name, p => p.GetValue(null));

    /// <summary>Lua files whose most recent (re)load was rejected for a compile/shape error — their previously
    /// loaded version is still live. Empty when everything took. See <see cref="Reload"/>.</summary>
    public static IReadOnlyList<string> RejectedScripts { get; private set; } = Array.Empty<string>();

    /// <summary>What every one of the 68 content files did on the last <see cref="Load"/>: its status, and
    /// how many rows it read, kept and skipped. Swapped by reference at the end of Load like every other
    /// registry, so a reader always sees one whole report.
    ///
    /// <para>This is the thing the old startup line could not be: it covers ALL of them. The hand-written
    /// summary named 36 registries, which meant MobSpells, Doors, SpellParams, NpcAbilities, MapCells,
    /// WarpQuestLocks and about twenty-five others could load zero rows and say nothing — and the reader
    /// underneath swallowed a missing file and a parse failure alike. ContentSmokeTests asserts a floor over
    /// this for every table, so a registry that collapses fails CI instead of shipping.</para></summary>
    public static ContentLoadReport LoadReport { get; private set; } = ContentLoadReport.Empty;

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
            ["player.level"] = 50, ["player.will"] = 30, ["player.grace"] = 20, ["player.might"] = 40,
            ["player.magic"] = 200, ["player.maxMagic"] = 200, ["player.health"] = 1000, ["player.maxHealth"] = 1000,
        };
        Line("--- Formula.Eval (level50 will30 grace20 might40 mana200 hp1000) ---");
        foreach (var key in new[] { "spark_mage", "heal_mage", "invoke_mage", "thunder_bolt_mage", "singe_mage" })
        {
            if (!SpellFx.TryGetValue(key, out var fx)) { Line($"    {key,-20} (no fx row)"); continue; }
            string amt = string.IsNullOrEmpty(fx.AmountExpr) ? "" : $" amount={Formula.Eval(fx.AmountExpr, vars):0}";
            string hc  = string.IsNullOrEmpty(fx.HealthCost) ? "" : $" healthCost={Formula.Eval(fx.HealthCost, vars):0}";
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
             $"{_bgmByMap.Count} maps resolved, " +
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
                        : track.Shuffle   ? "SHUFFLED — will stall dead"
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

    // ---- background music (0x19) --------------------------------------------------------------
    // The stock 4.95 client keeps its audio in NexusTK.snd, which ships exactly 12 background tracks
    // (1.mid .. 12.mid); the 0x19 music packet plays one by id with type 2 = MIDI. There is no original
    // map->track table in the client files, so we assign them ourselves — by AREA, not by map (MapBgm.csv).
    //
    // The 5.33 client keeps those same 12 midis (in Snd.dat) AND a second, larger soundtrack in Mus000.dat:
    // 25 mp3s plus 52 playlists, played over 0x19 type 1. That is the MusicSet.New half of every table here,
    // and it is 5.33-only because 4.95 ships none of those files. Players opt in per character with
    // "@music new" (Session.PlayMusicCmd); the midis stay the default for everyone.

    /// <summary>The background track for a map in one soundtrack: (bgm id, 0x19 type), or null only for a map
    /// that no zone claims AND that has no warp path to one — in which case the caller keeps whatever is
    /// already playing (see Session.PlayMapMusic).</summary>
    public static (ushort bgm, byte type)? BgmFor(ushort mapId, MusicSet set = MusicSet.Old) =>
        _bgmByMap.TryGetValue(mapId, out var p)
            ? (set == MusicSet.New ? (p.Track5x, p.Type5x) : (p.Track, p.Type))
            : null;

    /// <summary>The zone a map's music comes from, for "@music" feedback ("" if none). Maps that inherited
    /// it through the warp graph rather than being listed are shown with their hop distance.</summary>
    public static string BgmZoneOf(ushort mapId) =>
        _bgmByMap.TryGetValue(mapId, out var p) ? (p.Hops == 0 ? p.Zone : $"{p.Zone} +{p.Hops}") : "";

    // Resolve every map to a track, once per Load(). Three passes, each only filling maps still unclaimed:
    //   1. explicit ids/ranges  -> so a single map can be carved out of an area another zone claims by name
    //   2. map-name globs       -> "Buya *" and friends
    //   3. warp-graph spill     -> multi-source BFS from everything claimed above, so each remaining map
    //                             takes its NEAREST claimed map's track (Buya's shops/caves become Tiger
    //                             without being listed; a login inside one starts on the right song)
    private static Dictionary<ushort, BgmPick> BuildBgmMap()
    {
        var byMap = new Dictionary<ushort, BgmPick>();

        foreach (var z in BgmZones)
            foreach (var (lo, hi) in z.Maps)
                for (int id = lo; id <= hi; id++)
                    if ((Maps.ContainsKey((ushort)id) || lo == hi) && !byMap.ContainsKey((ushort)id))
                        byMap[(ushort)id] = new BgmPick(z.Track, z.Type, z.Track5x, z.Type5x, z.Zone, 0);

        foreach (var z in BgmZones)
            foreach (var pat in z.Names)
                foreach (var m in Maps.Values)
                    if (!byMap.ContainsKey(m.Id) && GlobMatch(m.Name, pat))
                        byMap[m.Id] = new BgmPick(z.Track, z.Type, z.Track5x, z.Type5x, z.Zone, 0);

        // Map-level adjacency from the tile warp table, treated as undirected: a one-way drop still tells us
        // the two maps are the same neighbourhood, and most warps are paired anyway.
        var adj = new Dictionary<ushort, List<ushort>>();
        void Link(ushort a, ushort b)
        {
            if (a == b) return;
            if (!adj.TryGetValue(a, out var l)) adj[a] = l = new List<ushort>();
            if (!l.Contains(b)) l.Add(b);
        }
        foreach (var (from, to) in Warps)
        {
            if (!Maps.ContainsKey(from.m) || !Maps.ContainsKey(to.m)) continue;
            Link(from.m, to.m);
            Link(to.m, from.m);
        }

        var queue = new Queue<ushort>(byMap.Keys.Where(Maps.ContainsKey).OrderBy(id => id));
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!adj.TryGetValue(cur, out var neighbours)) continue;
            var here = byMap[cur];   // NB: not `from` — that's a LINQ query keyword and breaks `with`
            foreach (var n in neighbours)
            {
                if (byMap.ContainsKey(n)) continue;
                byMap[n] = here with { Hops = here.Hops + 1 };
                queue.Enqueue(n);
            }
        }
        return byMap;
    }

    /// <summary>A track by name ("mist") or by number ("6"); prefix match as a fallback so "mon" finds
    /// "monkey". Null when nothing matches.
    ///
    /// <para><paramref name="set"/> is searched FIRST and the other set second, so the id spaces can overlap
    /// (midi 2 = "dragon", mp3 2 = "buyeo") while a player in either mode can still name any track he can
    /// hear. An id with no row resolves to an unnamed track in <paramref name="set"/> rather than to null —
    /// the client will happily play a number we have never given a name.</para></summary>
    public static MusicTrack? FindTrack(string query, MusicSet set = MusicSet.Old)
    {
        query = query.Trim();
        if (query.Length == 0) return null;
        var (mine, theirs) = (MusicTracks.Where(t => t.Set == set), MusicTracks.Where(t => t.Set != set));
        if (ushort.TryParse(query, out var id))
            return mine.FirstOrDefault(t => t.Id == id)
                ?? theirs.FirstOrDefault(t => t.Id == id)
                ?? new MusicTrack(id, "", set == MusicSet.New ? (byte)1 : (byte)2, set, false);
        return mine.FirstOrDefault(t => t.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? theirs.FirstOrDefault(t => t.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? mine.FirstOrDefault(t => t.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            ?? theirs.FirstOrDefault(t => t.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The name of a track id within one soundtrack, or "" if it has none (only some of the 12 stock
    /// midis are named).</summary>
    public static string TrackName(ushort id, MusicSet set = MusicSet.Old) =>
        MusicTracks.FirstOrDefault(t => t.Id == id && t.Set == set)?.Name ?? "";

    // Case-insensitive '*' glob (no '?', no escaping — map names have neither). Used for the MapBgm.csv
    // name patterns, e.g. "Buya *" matching "Buya Kan Shop" but not "Buyan Stables".
    private static bool GlobMatch(string text, string pattern)
    {
        if (pattern.Length == 0) return false;
        var parts = pattern.Split('*');
        if (parts.Length == 1) return text.Equals(pattern, StringComparison.OrdinalIgnoreCase);

        int pos = 0;
        if (!text.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase)) return false;
        pos = parts[0].Length;
        for (int i = 1; i < parts.Length - 1; i++)
        {
            if (parts[i].Length == 0) continue;
            int at = text.IndexOf(parts[i], pos, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            pos = at + parts[i].Length;
        }
        var tail = parts[^1];
        return tail.Length == 0
            ? true
            : text.Length - pos >= tail.Length && text.EndsWith(tail, StringComparison.OrdinalIgnoreCase);
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

    // Per-path cumulative-exp-to-level table (RTK rtk/db/level_db.txt, classdb_level): LevelExp[path][level] =
    // total exp needed to LEAVE `level` (i.e. reach level+1). Long-format CSV (game-data/LevelExp.csv,
    // generated from the RTK file — see awk one-liner in git history) with one row per (Path, Level). Path ids
    // match PathIdForClass (0 Peasant/1 Warrior/2 Rogue/3 Mage/4 Poet); level 99 is the cap and has no entry.
    private static Dictionary<int, Dictionary<int, uint>> LevelExp = new();

    /// <summary>Total exp required to advance past <paramref name="level"/> on <paramref name="pathId"/>
    /// (0 at the level-99 cap or on a lookup miss — treated as "no further threshold").</summary>
    public static uint ExpToNext(int pathId, int level)
    {
        if (level >= 99) return 0;
        if (!LevelExp.TryGetValue(pathId, out var byLevel) && !LevelExp.TryGetValue(0, out byLevel)) return 0;
        return byLevel.GetValueOrDefault(level, 0u);
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
