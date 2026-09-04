namespace Server;

/// <summary>A warpable map: id (== TK&lt;id&gt;.map and the 0x15 mapId), display name, and dimensions.</summary>
public sealed record MapInfo(ushort Id, string Name, ushort Xs, ushort Ys);

public static partial class Content
{
    // id -> map. Only maps whose dims were validated against the client's own TK&lt;id&gt;.map (see
    // re/build_map_index.py) are present, so a warp target here is always renderable.
    public static IReadOnlyDictionary<ushort, MapInfo> Maps
    {
        get => _snapshotBuilder?.Maps ?? Snapshot.Maps;
        private set => Builder.Maps = value;
    }

    // Portals/doors: (sourceMap, x, y) -> (destMap, x, y). Only warps whose DESTINATION is a renderable
    // client map are kept (a warp to a 7.x-only map would strand the player on a black screen).
    public static IReadOnlyDictionary<(ushort m, ushort x, ushort y), (ushort m, ushort x, ushort y)> Warps
    {
        get => _snapshotBuilder?.Warps ?? Snapshot.Warps;
        private set => Builder.Warps = value;
    }

    // Per-map region + warp-out flag (RTK Maps table: MapRegion / MapWarpout). Region groups maps into
    // kingdoms (0 Kugnae · 1 Buya · 2 Mythic · 3 Nagnang · …) and is what the Gateway spell keys off to pick
    // the destination city; warpOut==false is a map that blocks Gateway/Return ("It doesn't work here").
    // Also carries the warp-entry gate (RTK map_data.reqlvl/reqvita/reqmana/reqmark/reqpath/*max/rejectmsg,
    // map.c:1102) and the PvP flag (MapPvP — durability loss is disabled on PvP maps, RTK clif.c:6650).
    // Loaded from the full RTK Maps.csv (map_index.csv, the renderable subset, doesn't carry these columns).
    public sealed record MapMetaInfo(int Region, bool WarpOut, bool Pvp, bool CanTalk, bool CanCast, int ReqLvl, int ReqPath, int ReqMark,
        long ReqVita, long ReqMana, int LvlMax, long VitaMax, long ManaMax, string RejectMsg, bool Indoor);

    public static IReadOnlyDictionary<ushort, MapMetaInfo> MapMeta
    {
        get => _snapshotBuilder?.MapMeta ?? Snapshot.MapMeta;
        private set => Builder.MapMeta = value;
    }

    // Era-gated content (game-data/EraFeatures.csv + the EraDate scalar) is NOT loaded here: it lives in
    // Shared.EraCalendar, because the LOGIN server needs the same calendar to place new characters. Load()
    // stages EraCalendar with the snapshot so @reload still picks up edits atomically. See Server/Era.cs.


    // ---- Mythic Nexus zodiac cave entrances (game-data/MythicCaves.csv) ------------------------------
    // The 12 zodiac caves' entrance tiles, destination, and per-tier (cave 1/2/3) level+vita/mana gates.
    // Requirement numbers are archival (cross-referenced against 4 tutor posts — see the row Sources and
    // Sources.csv tutor-caves-*); the tile/destination geometry is RTK routing (onScriptedTilesMythic.lua).
    // Consumed by Session.TryMythicCaveEntrance. A tier is met when level >= T{n}Level AND
    // (baseMaxHP >= T{n}Vita OR baseMaxMP >= T{n}Mana); the deepest met tier wins.
    public readonly record struct MythicTier(byte Level, uint Vita, uint Mana);
    public sealed record MythicCaveDef(string Animal, ushort EntranceMap, (ushort X, ushort Y)[] Tiles,
        ushort DestMap, ushort DestX, ushort DestY, MythicTier[] Tiers, string Sources);

    public static IReadOnlyList<MythicCaveDef> MythicCaves
    {
        get => _snapshotBuilder?.MythicCaves ?? Snapshot.MythicCaves;
        private set => Builder.MythicCaves = value;
    }

    // Derived (map,x,y) -> cave lookup so the per-step entrance check is a single hash probe on any map.
    public static IReadOnlyDictionary<(ushort Map, ushort X, ushort Y), MythicCaveDef> MythicCaveTiles
    {
        get => _snapshotBuilder?.MythicCaveTiles ?? Snapshot.MythicCaveTiles;
        private set => Builder.MythicCaveTiles = value;
    }

    // ---- Tiered "event cave" entrances (game-data/EventCaves.csv + EventCaveTiers.csv) --------------
    // A doorway that reads the character and drops them into one of FIVE parallel copies of the same
    // dungeon. RTK keeps the ladder in one shared place (Player.getEventCaveLevel / eventCaveLevelPrompt in
    // rtklua/Accepted/player.lua) and calls it from each entrance; so do we. The Buya Library Caverns
    // doorway is the only entrance wired today. Consumed by Session.TryEventCaveEntrance.
    //
    // The LADDER is a list of disjoint bands matched in file order — first row whose level AND mark ranges
    // both contain the character wins. Alt > 0 marks a SPLIT band, where two depths are open and the player
    // picks a tunnel. No match at all = refused (below the chart's level floor). See the CSV headers for
    // where the numbers come from and which half of them is ours rather than the archive's.
    public readonly record struct EventCaveBand(int Tier, int Alt, int MinLevel, int MaxLevel,
        int MinMark, int MaxMark, string Label, string Sources)
    {
        public bool Contains(int level, int mark) =>
            level >= MinLevel && level <= MaxLevel && mark >= MinMark && mark <= MaxMark;
    }

    public static IReadOnlyList<EventCaveBand> EventCaveBands
    {
        get => _snapshotBuilder?.EventCaveBands ?? Snapshot.EventCaveBands;
        private set => Builder.EventCaveBands = value;
    }

    /// <summary>The band a character of this level/subpath-rank falls in, or null when nothing matches —
    /// which is the refusal case, not an error.</summary>
    public static EventCaveBand? EventCaveBandFor(int level, int mark)
    {
        foreach (var b in EventCaveBands) if (b.Contains(level, mark)) return b;
        return null;
    }

    public sealed record EventCaveDef(string Key, ushort EntranceMap, (ushort X, ushort Y)[] Tiles,
        ushort[] TierMaps, ushort DestX, ushort DestY, string[] Pages,
        string Prompt, string OptionNear, string OptionFar, string DenyMsg, string Sources)
    {
        /// <summary>Map id for a 1-based tier. A ladder deeper than this cave's map list clamps to the
        /// deepest map it does have, so adding a band never strands a player on a map that isn't there.</summary>
        public ushort MapForTier(int tier) =>
            TierMaps.Length == 0 ? (ushort)0 : TierMaps[Math.Clamp(tier, 1, TierMaps.Length) - 1];
    }

    public static IReadOnlyList<EventCaveDef> EventCaves
    {
        get => _snapshotBuilder?.EventCaves ?? Snapshot.EventCaves;
        private set => Builder.EventCaves = value;
    }

    // Derived (map,x,y) -> entrance lookup, so the per-step check is one hash probe (same shape as
    // MythicCaveTiles / ArenaDoorTiles).
    public static IReadOnlyDictionary<(ushort Map, ushort X, ushort Y), EventCaveDef> EventCaveTiles
    {
        get => _snapshotBuilder?.EventCaveTiles ?? Snapshot.EventCaveTiles;
        private set => Builder.EventCaveTiles = value;
    }

    // ---- PvP arena doors (game-data/ArenaDoors.csv) -------------------------------------------------
    // Tower Arena's five side doors are RTK Lua tile-scripts (onScriptedTilesArena.lua ->
    // arenaPVPCheckAndWarp.lua), NOT rows in the SQL warp table — which is why every one of them was dead
    // here: only the RETURN leg (each arena's 15:2/16:2 exit back into Tower Arena) is in Warps.csv, so the
    // ring was one-way. Each door is a level-banded gateway into one PvP arena map:
    //   west  0:5/0:6   -> Kugnae Adventure  6-35     east 21:5/21:6   -> Kugnae Legends  86-98
    //   west  0:11/0:12 -> Kugnae Heroes    36-65     east 21:11/21:12 -> Kugnae Ancients 99, capped vitals
    //   west  0:17/0:18 -> Kugnae Glory     66-85
    // Consumed by Session.TryArenaDoor. Gate = level >= MinLevel (and unmarked, when Unmarked=1), rejected
    // high when level > MaxLevel (0 = no cap) or — RTK uses OR here, unlike the engine's map-req check —
    // baseMaxHP > MaxVita or baseMaxMP > MaxMana (0 = no cap). DestX may be a "lo-hi" range (RTK picks a
    // random landing column so two entrants don't stack). Tiles is ';'-separated "x:y", same as MythicCaves.
    //
    // NOT ported: Tower Arena's NORTH row (y=2), which RTK gates on a live "carnage" minigame event id — we
    // have no minigame scheduler, so in RTK-with-no-event those tiles only ever bounce you back anyway.
    public sealed record ArenaDoorDef(ushort Map, (ushort X, ushort Y)[] Tiles, ushort DestMap,
        ushort DestX, ushort DestX2, ushort DestY, int MinLevel, int MaxLevel,
        uint MaxVita, uint MaxMana, bool Unmarked, string Label, string Sources);

    public static IReadOnlyList<ArenaDoorDef> ArenaDoors
    {
        get => _snapshotBuilder?.ArenaDoors ?? Snapshot.ArenaDoors;
        private set => Builder.ArenaDoors = value;
    }

    // Derived (map,x,y) -> door lookup, so the per-step check is one hash probe (same shape as MythicCaveTiles).
    public static IReadOnlyDictionary<(ushort Map, ushort X, ushort Y), ArenaDoorDef> ArenaDoorTiles
    {
        get => _snapshotBuilder?.ArenaDoorTiles ?? Snapshot.ArenaDoorTiles;
        private set => Builder.ArenaDoorTiles = value;
    }

    // ---- Board-sign locations (game-data/BoardLocations.csv) -----------------------------------------
    // RTK's onSign board-sign system (on_event.lua onSign / selectBulletinBoard): a board SPRITE tile that,
    // when faced from the south (player looking north), opens ONE specific board (Server/Boards.cs) straight
    // to its posts. Keyed by the board tile (map,x,y) + the target BoardId; consumed by Session via TryBoardAt
    // with RTK's ±1 X tolerance. Distinct from the `b` mailbox/board-list — this jumps directly to a board.
    public static IReadOnlyList<(ushort Map, ushort X, ushort Y, int BoardId)> BoardLocations
    {
        get => _snapshotBuilder?.BoardLocations ?? Snapshot.BoardLocations;
        private set => Builder.BoardLocations = value;
    }

    // Return tiles for Return / yellow_scroll / qui_hyang (Session.ReturnToInn). Grouped by Kugnae/Buya/
    // Nagnang (chosen by nation), Wilderness (the Neutral nation's), and Sanhae/Hausson (bound by a mayor
    // and overriding the nation set). The player->group choice stays in code (Session.HomeGroup).
    // X2/Y2 are an optional bottom-right corner: the wilderness clearing has no bed, so RTK lands you on a
    // random tile in a box there. Blank X2/Y2 -> the box is the single tile X,Y, which is every tavern.
    public sealed record InnDef(ushort Map, ushort X, ushort Y, ushort X2, ushort Y2);
    public static IReadOnlyDictionary<string, IReadOnlyList<InnDef>> Inns
    {
        get => _snapshotBuilder?.Inns ?? Snapshot.Inns;
        private set => Builder.Inns = value;
    }

    // Ground-item forage spawn boxes (World forage tick / RTK itemspawner.lua). See ForageAreas.csv.
    public sealed record ForageAreaDef(string ItemKey, ushort Map, int MinX, int MaxX, int MinY, int MaxY,
        int Max, int MinQty, int MaxQty);
    public static IReadOnlyList<ForageAreaDef> ForageAreas
    {
        get => _snapshotBuilder?.ForageAreas ?? Snapshot.ForageAreas;
        private set => Builder.ForageAreas = value;
    }

    // Class path-hall doorways (Session.TryPathHallWarp), keyed by the hall map. Sanctum[0..3] indexed by
    // Character.Alignment (Unaligned/Kwisin/Mingken/Ohaeng). See PathHalls.csv.
    public sealed record PathHallDef(int BaseClass, ushort GuildMap, ushort[] Sanctum);
    public static IReadOnlyDictionary<ushort, PathHallDef> PathHalls
    {
        get => _snapshotBuilder?.PathHalls ?? Snapshot.PathHalls;
        private set => Builder.PathHalls = value;
    }

    // Gateway spell gate-boxes per kingdom region 0-3 (Session.CastGateway). Gates keyed by 'n'/'e'/'s'/'w'.
    // See GatewayGates.csv.
    public sealed record GatewayDef(ushort Map, string City,
        IReadOnlyDictionary<char, (int Xlo, int Xhi, int Ylo, int Yhi)> Gates);
    public static IReadOnlyDictionary<int, GatewayDef> GatewayRegions
    {
        get => _snapshotBuilder?.GatewayRegions ?? Snapshot.GatewayRegions;
        private set => Builder.GatewayRegions = value;
    }

    // Inter-continent world-map travel destinations (Session world-map), order-significant (the wire dots are
    // sent in this order). DotX/DotY are field10 pixel coords. See WorldMapDests.csv.
    public sealed record WorldDestDef(string Name, ushort Map, ushort X, ushort Y, int DotX, int DotY);
    public static IReadOnlyList<WorldDestDef> WorldDests
    {
        get => _snapshotBuilder?.WorldDests ?? Snapshot.WorldDests;
        private set => Builder.WorldDests = value;
    }

    // World-map trigger tiles, keyed by the source (town) map. Hits when the FixedAxis coord is in
    // [FixedLo,FixedHi] AND the other axis is in [RangeLo,RangeHi]. See WorldMapTriggers.csv.
    public sealed record WorldTriggerDef(char FixedAxis, int FixedLo, int FixedHi, int RangeLo, int RangeHi)
    {
        public bool Hits(int x, int y)
        {
            int fixedC = FixedAxis == 'x' ? x : y;
            int rangeC = FixedAxis == 'x' ? y : x;
            return fixedC >= FixedLo && fixedC <= FixedHi && rangeC >= RangeLo && rangeC <= RangeHi;
        }
    }
    public static IReadOnlyDictionary<ushort, WorldTriggerDef> WorldMapTriggers
    {
        get => _snapshotBuilder?.WorldMapTriggers ?? Snapshot.WorldMapTriggers;
        private set => Builder.WorldMapTriggers = value;
    }

    // Mythic cave fall-room landings (Session.TryMythicFallRoom), keyed by the source sub-map, ALREADY
    // tier-expanded (+0/+3000/+4000) at load. See FallRooms.csv. Most rows come straight from RTK's
    // onScriptedTilesMythicFallRooms.lua (a 1/500-per-step roll). The Tiger row (Dark Pen 109 -> Guardroom
    // 110) is an APPROXIMATION: RTK reaches the tiger guardroom via a single hidden warp-trap NPC on Dark Pen
    // (trap/tiger_spawn/warp_trap_guardroom.lua), not a fall-room tile — but this server has no trap-NPC
    // tiles, so we reuse the fall mechanic to make the pure-sentry guardroom reachable. Tagged
    // rtk-lua-warptrap in the CSV to mark it as the one non-fall-room source.
    public static IReadOnlyDictionary<ushort, (ushort Map, ushort X, ushort Y)> FallRooms
    {
        get => _snapshotBuilder?.FallRooms ?? Snapshot.FallRooms;
        private set => Builder.FallRooms = value;
    }
    // SplitTrapSpells (0/1, default 0) also lives here — accessor is next to the trap block it gates,
    // see SplitTrapSpellsEnabled / IsOutOfEraSplitTrap.

    // Door-object graphic toggle table (game-data/DoorObjects.csv, transcribed from RTK open.lua `openDoors`).
    // Two lookups: DoorSwaps maps a faced object id -> (startDx, new object ids) for the explicit doors (single-tile
    // swings and 3-tile-wide runs where the faced piece tells us which corner we're on); DoorDeltas is the set of
    // ranges whose open<->closed pair differs by a fixed signed delta (single tile). See Content.DoorToggleFor.
    public static IReadOnlyDictionary<int, (int StartDx, ushort[] Objs)> DoorSwaps
    {
        get => _snapshotBuilder?.DoorSwaps ?? Snapshot.DoorSwaps;
        private set => Builder.DoorSwaps = value;
    }
    public static IReadOnlyList<(int Lo, int Hi, int Delta)> DoorDeltas
    {
        get => _snapshotBuilder?.DoorDeltas ?? Snapshot.DoorDeltas;
        private set => Builder.DoorDeltas = value;
    }

    // Closed-door object id -> the open id that replaces it, applied cell-by-cell as a .map file is read
    // (MapData.Load). This is how a door "starts open" without editing the client's own map files: the
    // 4.95 client draws its LOCAL copy, so opening one also needs the 0x06 cell-patch every session gets on
    // map entry (Session.SyncMapDoors). Populated from DoorObjects.csv rows flagged defaultOpen=1.
    public static IReadOnlyDictionary<int, ushort> DoorDefaultOpen
    {
        get => _snapshotBuilder?.DoorDefaultOpen ?? Snapshot.DoorDefaultOpen;
        private set => Builder.DoorDefaultOpen = value;
    }

    // ---- authored cell overrides (game-data/MapCells.csv) ------------------------------------------
    // "The shipped map is wrong here." One row per cell: Map,X,Y,Tile,Pass,Obj — any of the three value
    // columns left BLANK is inherited from the .map file, so you can fix passability without touching the
    // graphic (or vice versa). Applied by MapData.Load as the LAST authored layer, so a hand-written row
    // beats DoorDefaultOpen / DefaultClosed / ForceOpen. The .map files themselves are never modified.
    public sealed record CellOverride(ushort Map, ushort X, ushort Y, ushort? Tile, ushort? Pass, ushort? Obj);
    private static IReadOnlyDictionary<ushort, List<CellOverride>> MapCells
    {
        get => _snapshotBuilder?.MapCells ?? Snapshot.MapCells;
        set => Builder.MapCells = value;
    }
    /// <summary>Total authored cell overrides loaded (for the startup summary).</summary>
    public static int MapCellCount
    {
        get => _snapshotBuilder?.MapCellCount ?? Snapshot.MapCellCount;
        private set => Builder.MapCellCount = value;
    }
    /// <summary>Authored cell overrides for one map (empty if none).</summary>
    public static IReadOnlyList<CellOverride> MapCellsFor(ushort map) =>
        MapCells.TryGetValue(map, out var l) ? l : (IReadOnlyList<CellOverride>)Array.Empty<CellOverride>();
    /// <summary>Given the object a player faces, return the swapped door run (startDx + new ids), or null if it
    /// isn't a door. Mirrors the old Session.Movement.DoorToggle switch, now data-driven.</summary>
    public static (int StartDx, ushort[] Objs)? DoorToggleFor(int obj)
    {
        if (DoorSwaps.TryGetValue(obj, out var s)) return s;
        foreach (var (lo, hi, delta) in DoorDeltas)
            if (obj >= lo && obj <= hi) return (0, new[] { (ushort)(obj + delta) });
        return null;
    }

    /// <summary>The portal at (map, x, y), if the player just stepped on a door tile.</summary>
    public static bool TryWarp(ushort map, ushort x, ushort y, out (ushort m, ushort x, ushort y) dest)
        => Warps.TryGetValue((map, x, y), out dest);

    /// <summary>The board a board-sprite tile (map, x, y) belongs to, if any — RTK's onSign board-sign lookup
    /// (selectBulletinBoard). Applies RTK's ±1 X tolerance (a board sprite spans a few columns) and an exact Y,
    /// so a player facing north into any column of the board resolves the same board id.</summary>
    public static bool TryBoardAt(ushort map, int x, int y, out int boardId)
    {
        foreach (var b in BoardLocations)
            if (b.Map == map && b.Y == y && Math.Abs(b.X - x) <= 1) { boardId = b.BoardId; return true; }
        boardId = 0;
        return false;
    }

    /// <summary>The RTK region a map belongs to (0 Kugnae · 1 Buya · 2 Mythic · 3 Nagnang · …), or -1 if the
    /// map has no region row. Used by the Gateway spell to resolve the caster's kingdom.</summary>
    public static int RegionOf(ushort mapId) => MapMeta.TryGetValue(mapId, out var m) ? m.Region : -1;

    // ---- sage geography (the reach of the Share Wisdom ladder) --------------------------------------
    //
    // WHERE A SAGE RUNG ACTUALLY REACHES THE KINGDOM. Every rung but the last works only in certain
    // places, and outside them the cast does NOT fail — it behaves as the Mentor spell, and the aether
    // burns either way ("block sage and mentor spell usage in non-saging areas instead of casting
    // Aethers — Won't be changed. Was written to be like that", Nexus Atlas answering a suggestion).
    // The policy per rung lives in spell_verbs.lua's `sage_shout`; this answers only "where am I".
    //
    // REGION 2 IS THE LIST, and it is a startlingly exact fit rather than a proxy. tswolf names the sage
    // areas as "Mythic, Wilderness, and Kamings Encampment"; region 2 holds Mythic Nexus (41), Wilderness
    // (1002), KaMing's Encampment (3800) + KaMing (3806) and the mythic zones, and nothing else. The Atlas
    // calls the same set "Mythic Nexus, Carnage, Events, and other 4.0 designated areas" — region 2 IS
    // that designation, which is also why RegionCityName already calls it "the Mythic".
    private const int SageRegion = 2;

    // Carnage and event maps, which the sources list alongside the Mythic but which sit in their kingdoms'
    // own regions rather than in region 2. Named explicitly because there are five and no flag groups them.
    // The ARENAS are deliberately NOT here: sage works in "carnage GAMES", an event state this server does
    // not model, and a permanently-sagable arena is not what any source describes. "Carnage test" (4002)
    // and "Test Event1" (8105) are left out for the same reason the rest of the test maps are — they are
    // not places a player is.
    private static readonly HashSet<ushort> SageEventMaps = new() { 2007, 3010, 4050, 4524, 4525 };

    /// <summary>Is this map one of the "4.0 designated areas" where a sage rung reaches the whole kingdom —
    /// the Mythic/Wilderness/KaMing's region, or a carnage/event map? True for every rung; rungs 3-4 also
    /// reach <see cref="IsOwnKingdom"/>, and rung 5 reaches everywhere regardless of this.</summary>
    public static bool IsSageArea(ushort mapId) =>
        RegionOf(mapId) == SageRegion || SageEventMaps.Contains(mapId);

    /// <summary>Is this map inside <paramref name="nation"/>'s own kingdom — what rungs 3 and 4 add on top
    /// of <see cref="IsSageArea"/> ("Sage also works in one's home town stated on the spell name").
    ///
    /// <para>Nation ids are Neutral 0 · Koguryo 1 · Buya 2 · Nagnang 3; map regions are Kugnae 0 · Buya 1 ·
    /// Mythic 2 · Nagnang 3. The two spaces are NOT the same numbers, which is the whole reason this exists.
    /// A NEUTRAL caster has no kingdom to add, so rungs 3-4 fall back to rung-2 behaviour for them — exactly
    /// what the tutor board says ("Works just like level 2 for neutral villagers") and why the archive's
    /// rung-3/4 names run "Buya, Kugnae, Nagnang, or NEUTRAL Wisdom".</para></summary>
    public static bool IsOwnKingdom(ushort mapId, int nation)
    {
        int region = nation switch { 1 => 0, 2 => 1, 3 => 3, _ => -1 };   // Neutral (and anything odd) -> none
        return region >= 0 && RegionOf(mapId) == region;
    }

    /// <summary>Whether a map is indoors (RTK <c>MapIndoor</c> — town interiors, caves, dungeons). The weather
    /// gate: <see cref="WeatherModel"/> never draws rain or snow here. Maps with no metadata row default to
    /// outdoor.</summary>
    public static bool IsIndoor(ushort mapId) => MapMeta.TryGetValue(mapId, out var m) && m.Indoor;

    /// <summary>Whether a map allows warp-out spells (Gateway/Return). Unknown maps default to true (only an
    /// explicit MapWarpout==0 blocks); RTK shows "It doesn't work here" when this is false.</summary>
    public static bool WarpOut(ushort mapId) => !MapMeta.TryGetValue(mapId, out var m) || m.WarpOut;

    /// <summary>Whether a map is flagged PvP (RTK MapPvP) — disables equipment durability loss there
    /// (clif_deductdura, clif.c:6650: "disable dura loss from mobs on pvp map").</summary>
    public static bool IsPvpMap(ushort mapId) => MapMeta.TryGetValue(mapId, out var m) && m.Pvp;

    /// <summary>The newbie tutorial AREA's own maps — 4711 Welcome, 4712 Open Field, 4713 Forest Path,
    /// 4714 Deep Forest, 4715 Country Farm, 4716 Mignok's Home, 4717 City Limits, 4718 Angel's Blessing.
    /// Authored as one contiguous block, hence the range.
    ///
    /// <para>This is a "where the player physically is" test, NOT an era or quest-progress test, and that is
    /// deliberate: it has to hold for a character who was GM-warped in, who abandoned the chain half way, or
    /// whose progress flag says something the map disagrees with. The area has no Shaman and no exit a ghost
    /// could walk to, so anything that would strand or eject a player from it keys off this — see
    /// <c>Session.SilverThread</c> and the ghost branch in <c>Session.RunNpcAsync</c>.</para></summary>
    public static bool IsTutorialMap(ushort mapId) => mapId is >= 4711 and <= 4718;

    /// <summary>Whether speech (incl. whisper) is allowed on this map (RTK cantalk). False on the rare
    /// silenced map — RTK: "Your voice is swept away by a strange wind." (clif_parsewisp, clif.c:7666).</summary>
    public static bool CanTalk(ushort mapId) => !MapMeta.TryGetValue(mapId, out var m) || m.CanTalk;

    /// <summary>Whether spells may be cast on this map (RTK Maps.MapSpells → <c>map[m].spell</c>). RTK gates
    /// the whole 0x0F opcode on it — <c>clif.c:11427</c>, <c>if (map[sd->bl.m].spell || sd->status.gm_level)
    /// … else clif_sendminitext(sd, "That doesn't work here.")</c> — so it is a blanket no-casting flag, not a
    /// per-spell one. Unknown maps default to allowed; only an explicit <c>MapSpells=0</c> blocks.
    ///
    /// <para>This is what makes the towns' indoor spaces spell-free: taverns, shops, kan houses, the three
    /// Gathering halls, the class trainers' buildings. Note it is NOT the same thing as <c>MapIndoor</c>,
    /// which is set on every cave and dungeon in the game too (Bat Cave, the Mythic caves, …) — casting has
    /// to work in those, so "indoors" alone can't be the rule and this explicit column is.</para>
    ///
    /// <para>RTK's own dump is inconsistent about the class trainers' buildings: Nagnang's (2510/2512/2514/
    /// 2516) and the later-era set (3820-3835) block casting, while Kugnae's and Buya's — the same rooms one
    /// era earlier — do not. The 40 outliers (halls 11-18 / 341-344, sanctums 366-369, alignment rooms
    /// 300-323) are corrected IN Maps.csv rather than through an override file, per the rule that these
    /// tables are ours now and are never re-extracted.</para></summary>
    public static bool SpellsAllowed(ushort mapId) => !MapMeta.TryGetValue(mapId, out var m) || m.CanCast;

    /// <summary>Totem index → display name (RTK player.lua getTotemName): 0 Ju Jak · 1 Baekho · 2 Hyun Moo ·
    /// 3 Chung Ryong · 4 (or anything else) None.</summary>
    public static string TotemName(int totem) => totem switch
    {
        0 => "Ju Jak", 1 => "Baekho", 2 => "Hyun Moo", 3 => "Chung Ryong", _ => "None"
    };

    /// <summary>Whether the game <paramref name="hour"/> (0..23) falls inside <paramref name="totem"/>'s
    /// six-hour "totem time" window (RTK player.lua isTotemTime). The four totems partition the day; totem 4
    /// (None) never has one. Chung Ryong 04–09 · Ju Jak 10–15 · Baekho 16–21 · Hyun Moo 22–03. During its
    /// window RTK grants +5% kill exp (checkTotemTimeXP) and a 1/25 bonus crafting-skill point.</summary>
    public static bool IsTotemTime(int hour, int totem) => totem switch
    {
        3 => hour >= 4  && hour <= 9,    // Chung Ryong (morning)
        0 => hour >= 10 && hour <= 15,   // Ju Jak (mid-day)
        1 => hour >= 16 && hour <= 21,   // Baekho (evening)
        2 => hour >= 22 || hour <= 3,    // Hyun Moo (mid-night, wraps midnight)
        _ => false,                       // 4 = None (or unset)
    };

    // ---- lookups (used by the @warp / @maps / @mobs / @summon commands) ----

    public static bool TryMap(ushort id, out MapInfo map) => Maps.TryGetValue(id, out map!);

    /// <summary>Best map for a query: exact id, then exact (case-insensitive) name, then substring, then subsequence.</summary>
    public static MapInfo? FindMap(string query)
    {
        query = query.Trim();
        if (ushort.TryParse(query, out var id) && Maps.TryGetValue(id, out var byId)) return byId;
        return BestByName(Maps.Values, query, m => m.Name);
    }

    public static List<MapInfo> SearchMaps(string query, int limit) =>
        RankByName(Maps.Values, query, m => m.Name).Take(limit).ToList();

    // Map ranges removed as "not classic": whole regions that are RTK-authored reskins of existing classic
    // dungeons rather than original NexusTK content, cut out of the warp graph (not deleted from the CSVs) so
    // they're simply unreachable — revertable by trimming this list.
    // 410-419 "Buya Scorpion Cave": a scorpion-reskinned clone of the Kugnae Spider Cave (90-96) — same
    // level-42 gate, same shared mob-id pool (carrion_raven/pale_scorpion/massive_scorpion) with the spider
    // ids swapped for scorpion ids (giant_spider->vile_scorpion, radiant_spider->radiant_scorpion, plus an
    // extra scorpion_lurker/crimson_scorpion boss). Entrance was Buya (68,93)/(69,93).
    private static readonly (ushort lo, ushort hi)[] ExcludedMapRanges = { (410, 419) };
}
