using Shared;

namespace Server;

/// <summary>A read-only snapshot of a player entity, so a peer can draw it without touching that
/// session's mutable state. Built under no lock (fields are only written by the owning session's
/// read-loop; a torn read at worst mis-places a peer by one tile until its next move packet).</summary>
public readonly record struct PlayerSnapshot(
    uint Id, ushort X, ushort Y, byte Dir, byte Sex, byte Face, byte Armor, byte Weapon, byte Shield, bool Mounted, bool Dead, string Name,
    byte ArmorColor = 0, ushort MorphLook = 0, byte MorphColor = 0, bool Faded = false);

/// <summary>A stack of an item lying on the map floor, drawn to every client on that map via 0x16
/// (Item.epf frame = <see cref="Graphic"/>). <see cref="Id"/> is the entity id (find/despawn key). Carries
/// enough to reconstruct an <see cref="Shared.InvItem"/> when a player picks it up.</summary>
public sealed class GroundItem
{
    public uint   Id;
    public int    ItemId;
    public ushort X, Y;
    public int    Amount = 1;
    public ushort Dura;
    public ushort Graphic;       // Item.epf frame (item's Icon) — the 0x16 graphic id
    public string CustomName = "";

    // LOOTER LOCK (RTK flooritem_data.looters[] + .timer, gated by player.lua's canLoot/isYours). 0 = ordinary
    // free-for-all floor loot, which is almost everything. Non-zero means this stack was torn off a corpse and
    // belongs to that player id until LockedUntil passes — nobody else can pick it up, filch it, or grab it out
    // from under them, and the owner can pull it back from two tiles away via F1 "Recover Death Pile" even with
    // a would-be thief parked on top of it. RTK stores the drop time and adds 300s at every read; an absolute
    // Environment.TickCount64 deadline is the same rule with the arithmetic done once.
    public uint LooterId;
    public long LockedUntil;

    /// <summary>True while this stack is still reserved for someone other than <paramref name="pickerId"/>.</summary>
    public bool LockedAgainst(uint pickerId)
        => LooterId != 0 && LooterId != pickerId && Environment.TickCount64 < LockedUntil;

    /// <summary>True if this stack is <paramref name="pickerId"/>'s own death pile and the lock is still live —
    /// the only thing "Recover Death Pile" will pick up (RTK <c>isYours</c>, which tests <c>looters[1]</c>).</summary>
    public bool BelongsTo(uint pickerId)
        => LooterId != 0 && LooterId == pickerId && Environment.TickCount64 < LockedUntil;
}

/// <summary>A hidden hazard placed by a Rogue trap spell (RTK NPCs/trap/rogue_traps/*): invisible — no
/// ground graphic is ever drawn for it (unlike <see cref="GroundItem"/>) — until a mob steps onto its
/// tile, at which point its effect fires once and it's removed. See <see cref="World.PlaceTrap"/>/
/// <see cref="World.TrapAt"/> and Session.CastTrap/CastSpotTraps.</summary>
public sealed class Trap
{
    public uint   Id;
    public ushort X, Y;
    public string Kind = "";   // "dart"/"snare"/"repeating"/"flash"/"spear"/"poison"/"death"/"sleep"/"bladestorm"
    public uint   OwnerId;     // caster's player id — credited with any exp from a trap kill
    public long   ExpiresAt;   // 0 = never (the 8-kind hazard family); "bladestorm" auto-clears if untriggered
}

/// <summary>
/// The single shared game world: every connected player and every live mob, grouped by map. One
/// instance is created in <see cref="TkListener"/> and handed to every <see cref="Session"/>, so all
/// clients observe the SAME entities — players see each other, and everyone fights the same mobs.
///
/// This replaces the old per-Session mob ownership for GAMEPLAY mobs (@summon / @rabbit). The debug
/// lab (look-lab dummies, monster/colour sweeps) stays session-local — those are single-screen
/// diagnostics that shouldn't broadcast to the whole map.
///
/// Threading: all collections are guarded by <c>_lock</c>. Socket writes NEVER happen while holding
/// the lock — broadcasts snapshot the recipient list under the lock, then send outside it, and each
/// send is exception-guarded so a peer whose socket just closed can't break a broadcast.
/// </summary>
public sealed class World
{
    private readonly object _lock = new();

    private sealed class MapState
    {
        public readonly List<Session> Players = new();
        public readonly List<Mob> Mobs = new();
        public readonly List<GroundItem> Items = new();
        public readonly List<Trap> Traps = new();
        // RTK map[m].weather (map.h WRAIN=1/WSNOW=2, 0=clear) — sl.c's setWeatherM/getWeatherM Lua API is the
        // only place RTK ever changes this itself (an admin/quest-script lever, no automatic scheduler exists
        // anywhere in the C engine); see WeatherRollTicks below for our own periodic-drift substitute.
        public byte Weather;
    }
    private readonly Dictionary<ushort, MapState> _maps = new();

    // Server-wide online-account registry (independent of the per-map Players lists above, which a session
    // only joins AFTER its own arrival/load logic runs). Keyed by CharacterStore.Key(username). Exists
    // solely for the duplicate-login guard: RegisterOnline lets HandleArrival atomically detect + evict a
    // stale session for the same account BEFORE loading, so a slow-to-unwind old session can never clobber
    // the new one's fresher save (SQLite's persistence is blind last-write-wins). Guarded by the same _lock
    // as everything else here — registration/eviction is rare (once per login), so sharing the lock costs
    // nothing measurable against the map operations.
    private readonly Dictionary<string, Session> _online = new();

    /// <summary>A spawn point: one <see cref="Live"/> mob at a time, respawned <see cref="RespawnTick"/>
    /// ticks after it dies. Built once from <see cref="Content.Spawns"/> (fixed tile, <see cref="Placed"/>
    /// already true) and <see cref="Content.AreaSpawns"/> (a random home tile is chosen in the box the first
    /// time the point materializes). Drives the persistent world roster.</summary>
    private sealed class Spawn
    {
        public MobDef Def = null!;
        public ushort X, Y;          // home tile — fixed for a static spawn, chosen lazily for an area spawn
        public Mob?   Live;          // the currently-alive mob for this point (null while dead/pending)
        public long   RespawnTick;   // tick at which a dead point may respawn (0 = not pending)
        public bool   Placed = true; // false ⇒ area spawn whose home tile isn't chosen yet (see Box)
        public ushort MinX, MinY, MaxX, MaxY;   // area-spawn bounding box; all-zero ⇒ anywhere walkable on the map
        // RARE spawn (RTK trap-ambush bosses): RespawnEvery > 0 overrides the global ~18s cadence with a long
        // per-point delay (ticks), and Rare makes the point start un-spawned + appear at a random hunt-time
        // (a surprise rather than always-present). Both 0/false for ordinary spawns. See NextRespawnTick.
        public int    RespawnEvery;  // 0 ⇒ use the global RespawnTicks; >0 ⇒ this point's own respawn delay
        public bool   Rare;          // true ⇒ boss: delayed first appearance + jittered long respawn
    }
    private readonly Dictionary<ushort, List<Spawn>> _spawns = new();   // map -> its spawn points
    private readonly Dictionary<uint, Spawn> _mobSpawn = new();          // live mob id -> its spawn point
    // Maps whose spawn roster has been materialized (mobs instantiated). Populated lazily on first player
    // entry (EnsureMaterialized) so the world's ~21k hunting-map mobs don't all instantiate — and load their
    // map files — at boot; a cave stays a cheap point-list until someone actually walks into it.
    private readonly HashSet<ushort> _materialized = new();
    private long _tick;                                                  // heartbeat counter (600ms each)

    private const int TickMs = 600;         // world heartbeat period; also the unit MoveTimer accumulates in
    // A dead spawn point respawns after this many ticks (~18s at 600ms/tick), mirroring RTK's short town
    // respawn cadence so a cleared patch of Buya refills while the player is still nearby.
    private const int RespawnTicks = 30;
    // How far a mob may wander from its spawn tile (Chebyshev). Kept small so town critters hug their
    // spawn points instead of clustering into a dense knot that constantly overlaps on screen.
    private const int WanderRadius = 2;
    // Farthest (Chebyshev, from its home tile) a provoked mob will chase an attacker before giving up and
    // resuming normal wandering — bigger than WanderRadius so a fight can range beyond the idle-hop leash,
    // but still bounded so a player can outrun pursuit rather than being chased across the whole map.
    private const int ChaseLeash = 8;
    // How far (Chebyshev, from the mob's CURRENT tile) an aggressive mob (MobDef.Aggressive, RTK MobBehavior==1)
    // scans for an unprovoked target each move tick — RTK's mob_find_target runs over a full-screen-ish area;
    // this is scoped to roughly what the player can see on their own screen (17x15 viewport, Session.InView).
    private const int AggroRadius = 8;
    // The mirror of AggroRadius for PREY (MobDef.Flees — rabbit, blue rooster): how close (Chebyshev) a player
    // gets before the creature starts backing away. Deliberately much shorter than the aggro scan — a rabbit
    // notices you when you're nearly on top of it, not from across the screen, otherwise a town full of them
    // would evaporate the moment anyone walked in. Doubled while the creature is panicking (see PanicMs), so a
    // swing sends it running from further off than a stroll past does.
    private const int FleeRadius = 2;
    // A retreating prey creature moves at DOUBLE its usual pace, spooked or not — running is not the same
    // motion as browsing, and at its idle MoveTime a rabbit (3000ms) simply cannot get away from a walking
    // player. RTK expresses this as an absolute (mysterious_merchant's on_attacked sets `mob.newMove = 500`);
    // a multiplier says the same thing against whatever pace the creature actually has.
    //
    // CEILING: the world steps a mob at most once per Tick, so nothing can exceed one tile per TickMs (600ms)
    // however small this makes the interval. A rabbit genuinely doubles (3000 -> 1500). The blue rooster's
    // 500ms MoveTime is already under the heartbeat, so it is ALREADY moving as fast as this server can render
    // and cannot speed up further — its flee shows as direction, not pace.
    private const int FleeSpeedup = 2;
    // How long a prey creature stays spooked after a player swings at it or damages it (World.Spook /
    // TryDamage), refreshed by each further hit. Panic doesn't add speed on top of FleeSpeedup — it widens the
    // notice radius and keeps the creature running after you stop chasing, so a swing sends it properly away
    // instead of it settling down the moment you step back.
    private const int PanicMs = 4000;

    // Ground-item forage spawns (RTK itemspawner.lua): keep up to Max stacks of a gatherable item scattered on
    // passable tiles within a box, topped up periodically. Chestnuts fill the Kugnae farm (map 0) and a Buya
    // patch (map 330) — the tutorial's stage-3 gather. A stack is MinQty..MaxQty items on one tile.
    // Forage spawn boxes are data-driven (data/game-data/ForageAreas.csv -> Content.ForageAreas); hot-reloads
    // via @reload. See TopUpForageLocked.
    private const int ForageTicks = 30;   // top up ~every 18s (30 * 600ms), like RTK's periodic itemspawner

    // ---- day/night clock (RTK map.c change_time_char, opcode 0x20) ----------------------------
    // RTK: timer_insert(450000, 450000, change_time_char, ...) — every 450000ms (7.5min) real time, cur_time
    // (hour, 0..23) ticks up by one and every connected session gets a fresh clif_sendtime broadcast; on
    // hour rollover cur_day advances (1..91), and only once cur_day wraps (every 92 days) does cur_season
    // advance (1..4) — cur_year only ticks once every 4 seasons (~368 in-game days, matching the community
    // "1 Yuri ⟺ ~41-46 real days" Time Chart, NOT once a day). We model day/season internally purely to get
    // that real-world cadence right, even though the 0x20 packet only ever carries hour+year (see
    // Session.SendTime) — day/season have no client-visible effect via this packet.
    private const int HourTicks = 750;    // 450000ms / TickMs(600) — one in-game hour per real 7.5 minutes
    private int _hour = 16, _day, _season = 1, _year = 50;
                                           // hour/year starting values match what this server always sent
                                           // before this was wired up live (the old hardcoded 0x10/0x32
                                           // placeholder), so deploying this doesn't jump the clock for
                                           // anyone already playing; day/season start mid-cycle arbitrarily
                                           // (RTK itself loads these from a DB Time table we don't persist)
    public (byte hour, byte year) Time => ((byte)_hour, (byte)_year);

    /// <summary>Whether the shared world clock is currently in <paramref name="totem"/>'s totem time
    /// (RTK isTotemTime) — the +5% kill-exp window. Reads the live hour; see <see cref="Content.IsTotemTime"/>.</summary>
    public bool IsTotemTime(int totem) => Content.IsTotemTime(_hour, totem);

    // ---- weather (RTK map[m].weather / clif_sendweather, opcode 0x1F) --------------------------
    // No automatic scheduler exists in the RTK C engine for this (setWeatherM/getWeatherM are pure admin/
    // quest-script levers — see MapState.Weather's doc) so there's no real cadence/odds to port; this rolls
    // a low-probability per-active-map change on a slow tick so weather occasionally drifts rather than
    // sitting fixed at "clear" forever. 0=clear, 1=WRAIN, 2=WSNOW (RTK map.h enum).
    private const int WeatherRollTicks = 1500;     // ~15 minutes real time (1500 * 600ms)
    private const int WeatherChangePct = 20;       // 20% chance per eligible map each roll

    // Facing (0=N 1=E 2=S 3=W) toward a delta, preferring the larger axis — used to turn a mob to face
    // whatever it's about to melee.
    private static byte FaceDelta(int dx, int dy) =>
        Math.Abs(dx) >= Math.Abs(dy) ? (dx >= 0 ? (byte)1 : (byte)3) : (dy >= 0 ? (byte)2 : (byte)0);

    // Disjoint entity-id pools so a player id can never collide with a shared-mob id.
    //   players:     1 ..            (bound to each client's camera via 0x05)
    //   world mobs:  100000 ..       (session-local debug dummies use their own 5000+ pool, invisible
    //                                 to other clients, so those ranges never need to be globally unique)
    //   ground items: 500000 ..    (disjoint from players + mobs so a floor-item id never collides)
    private uint _nextPlayerId = 1;
    private uint _nextMobId = 100_000;
    private uint _nextNpcId = 300_000;    // NPCs get their own id band (disjoint from mobs) so a click can tell them apart
    private uint _nextItemId = 500_000;

    public World()
    {
        PopulateSpawns();                 // build the persistent roster from Content.Spawns (needs Content.Load first)
        PopulateNpcs();                   // place the stationary NPCs (Content.Npcs) as non-fighting mobs
        _ = Task.Run(TickLoop);           // start the shared mob-AI + respawn heartbeat
        _ = Task.Run(AutoSaveLoop);       // periodic crash-safety backstop (idle-dirty players); see AutoSaveLoop
    }

    // ---- persistent spawn roster --------------------------------------------------------------

    /// <summary>Build the persistent spawn-point roster from the static table (<see cref="Content.Spawns"/>,
    /// fixed tiles) and the Lua area spawns (<see cref="Content.AreaSpawns"/>, a count of mobs per map/box).
    /// Runs once at startup (Content is already loaded). This only builds cheap point objects — no mob is
    /// instantiated and no map file is read until the first player enters that map (<see cref="EnsureMaterialized"/>),
    /// so the ~21k hunting-map mobs don't flood memory or stall boot. Dead points refill via <see cref="Tick"/>.</summary>
    private void PopulateSpawns()
    {
        int points, skipped;
        lock (_lock) (points, skipped) = BuildSpawnRosterLocked();
        Log.Info($"spawns: {points} spawn points (materialized lazily) across {_spawns.Count} map(s)" +
                 (skipped > 0 ? $" ({skipped} skipped — unknown map/mob)" : ""));
    }

    /// <summary>Build the <c>_spawns</c> roster from the current <see cref="Content.Spawns"/> +
    /// <see cref="Content.AreaSpawns"/>. Caller holds <c>_lock</c> and has already cleared <c>_spawns</c> if
    /// rebuilding. Returns (points, skipped). Shared by startup <see cref="PopulateSpawns"/> and the live
    /// <see cref="RebuildPopulation"/>.</summary>
    private (int points, int skipped) BuildSpawnRosterLocked()
    {
        int points = 0, skipped = 0;
        foreach (var sd in Content.Spawns)
        {
            if (!Content.Maps.ContainsKey(sd.Map)) { skipped++; continue; }   // map the client can't render
            var def = Content.MobById(sd.MobId);
            if (def is null) { skipped++; continue; }                           // unknown mob id

            AddSpawn(sd.Map, new Spawn { Def = def, X = sd.X, Y = sd.Y });
            points++;
        }

        foreach (var ad in Content.AreaSpawns)
        {
            if (!Content.Maps.ContainsKey(ad.Map)) { skipped += ad.Count; continue; }   // unrenderable map
            var def = Content.MobById(ad.MobId);
            if (def is null) { skipped += ad.Count; continue; }                          // unknown mob id

            // RespawnSec > 0 ⇒ a rare trap-ambush boss: convert seconds → ticks for the per-point delay.
            int respawnEvery = ad.RespawnSec > 0 ? Math.Max(1, ad.RespawnSec * 1000 / TickMs) : 0;
            for (int i = 0; i < ad.Count; i++)
                AddSpawn(ad.Map, new Spawn
                {
                    Def = def, Placed = false,
                    MinX = ad.MinX, MinY = ad.MinY, MaxX = ad.MaxX, MaxY = ad.MaxY,
                    RespawnEvery = respawnEvery, Rare = ad.RespawnSec > 0,
                });
            points += ad.Count;
        }
        return (points, skipped);
    }

    /// <summary>When a dead point may next refill. Ordinary points use the short global <see cref="RespawnTicks"/>
    /// cadence; a rare trap-ambush boss uses its own long <see cref="Spawn.RespawnEvery"/> plus up to +50%
    /// jitter, so it comes back as an irregular surprise rather than on a predictable clock.</summary>
    private long NextRespawnTick(Spawn sp)
    {
        if (sp.RespawnEvery <= 0) return _tick + RespawnTicks;
        return _tick + sp.RespawnEvery + (sp.Rare ? Random.Shared.Next(sp.RespawnEvery / 2 + 1) : 0);
    }

    /// <summary>Append a spawn point to its map's roster. Caller holds <c>_lock</c>.</summary>
    private void AddSpawn(ushort mapId, Spawn sp)
    {
        if (!_spawns.TryGetValue(mapId, out var list)) { list = new(); _spawns[mapId] = list; }
        list.Add(sp);
    }

    /// <summary>Instantiate a map's spawn roster the first time anyone enters it (idempotent). Until this runs
    /// the map's mobs don't exist, so a newcomer must trigger it BEFORE the room's mob list is read for them.
    /// Caller holds <c>_lock</c>.</summary>
    private void EnsureMaterialized(ushort mapId)
    {
        if (!_materialized.Add(mapId)) return;              // already done
        if (!_spawns.TryGetValue(mapId, out var list)) return;
        foreach (var sp in list)
        {
            // A rare boss doesn't appear the instant someone walks in — it's a surprise. Leave it pending
            // with a random first-appearance somewhere in its respawn window; the Tick refill loop spawns it
            // once due (and only while the map is being hunted, since that loop skips empty maps).
            if (sp.Rare) { sp.RespawnTick = _tick + 1 + Random.Shared.Next(sp.RespawnEvery); continue; }
            Materialize(mapId, sp);
        }
    }

    /// <summary>Place every stationary NPC (Content.Npcs) into the world as a non-fighting mob. NPCs ride
    /// the exact same 0x07 creature render + viewport streaming as a real mob (see Session.ShowMob/SyncMobs),
    /// so they render + stream for free; they simply never wander, never respawn, and can't be damaged
    /// (World.TryDamage rejects <see cref="Mob.IsNpc"/>). Clicking one opens its dialog (Session.HandleClickInfo).
    /// Runs once at startup after Content.Load; the NPC's home tile is its spawn tile and it holds position.</summary>
    private void PopulateNpcs()
    {
        int placed = 0;
        lock (_lock)
        {
            foreach (var n in Content.Npcs)
            {
                if (!n.Enabled) continue;   // switched off in NPCs.csv (Enabled=0)
                PlaceNpc(n);
                placed++;
            }
        }
        Log.Info($"npcs: {placed} stationary NPC(s) placed");
    }

    /// <summary>Instantiate one NPC def as a stationary (or pacing) non-fighting mob and add it to its map.
    /// Shared by startup placement and the live <see cref="EnableNpc"/> toggle. Caller holds <c>_lock</c>.</summary>
    private void PlaceNpc(NpcDef n)
    {
        var (nx, ny) = FreeSpawnTile(n.Map, n.X, n.Y);   // don't stack on a mob spawn sharing the tile
        // RTK gives some NPCs (animals, town dogs, roaming merchants) a MoveTime + ReturnDistance so they
        // pace; the rest stand still. A leash of 0 means "don't stray", i.e. stationary.
        bool paces = n.MoveTime > 0 && n.ReturnDistance > 0;
        var npc = new Mob(_nextNpcId++, n.Look, nx, ny, n.Name, hp: 1)
        {
            IsNpc = true, NpcDefId = n.Id, Color = n.Color, Dir = n.Dir,
            Wander = paces, MoveTime = paces ? n.MoveTime : 2500, Leash = n.ReturnDistance,
        };
        Map(n.Map).Mobs.Add(npc);
    }

    /// <summary>Remove every placed instance of NPC def <paramref name="npcId"/> from the world and despawn
    /// it (0x0E) for everyone watching. Returns how many instances were removed. Called by
    /// <see cref="ReconcileNpcToggles"/> on <c>@reload</c> — toggling is config (the Enabled column of
    /// NPCs.csv), not a live GM action, so this has no separate persistence step of its own.</summary>
    public int DisableNpc(int npcId)
    {
        var removed = new List<(ushort map, uint id)>();
        lock (_lock)
        {
            foreach (var (mapId, m) in _maps)
            {
                var gone = m.Mobs.Where(x => x.IsNpc && x.NpcDefId == npcId).ToList();
                foreach (var g in gone) { m.Mobs.Remove(g); removed.Add((mapId, g.Id)); }
            }
        }
        foreach (var (map, id) in removed)
            Broadcast(map, p => p.DespawnEntity(id));   // socket I/O — outside the lock
        return removed.Count;
    }

    /// <summary>Place NPC def <paramref name="npcId"/> back into the world (idempotent — a no-op if it's
    /// already placed). The periodic viewport sync streams it to anyone in range. Returns true if it was
    /// placed. See <see cref="DisableNpc"/>.</summary>
    public bool EnableNpc(int npcId)
    {
        lock (_lock)
        {
            foreach (var (_, m) in _maps)
                if (m.Mobs.Any(x => x.IsNpc && x.NpcDefId == npcId)) return false;   // already present
            var def = Content.Npcs.FirstOrDefault(n => n.Id == npcId);
            if (def is null) return false;
            PlaceNpc(def);
        }
        return true;
    }

    /// <summary>Hot-reload hook (the <c>@reload</c> command, after <see cref="Content.Reload"/> re-reads
    /// <c>NPCs.csv</c>): re-sync stationary-NPC placement against the just-reloaded Enabled flags — spawns any
    /// NPC newly enabled, despawns any newly disabled. <see cref="EnableNpc"/>/<see cref="DisableNpc"/> are
    /// already no-ops when there's nothing to change, so this is safe to run unconditionally on every reload.
    /// Returns how many NPCs' placement changed.</summary>
    public int ReconcileNpcToggles()
    {
        int changed = 0;
        foreach (var n in Content.Npcs)
        {
            if (n.Enabled) { if (EnableNpc(n.Id)) changed++; }
            else if (DisableNpc(n.Id) > 0) changed++;
        }
        return changed;
    }

    /// <summary>Create the live mob for a spawn point and register it. Caller holds <c>_lock</c>.</summary>
    private void Materialize(ushort mapId, Spawn sp)
    {
        var d = sp.Def;
        // Area spawn's first materialize: choose a walkable home tile inside its box (or anywhere on the map
        // for a zero box). Fixed once, so respawns hug the same patch like RTK's sentries.
        if (!sp.Placed) { (sp.X, sp.Y) = PickAreaHome(mapId, sp); sp.Placed = true; }
        // Don't stack: several RTK spawn points share a tile, and a respawn can land where another mob has
        // wandered. Place on the spawn tile if free, else the nearest open one (home stays the spawn tile).
        var (sx, sy) = FreeSpawnTile(mapId, sp.X, sp.Y);
        var mob = new Mob(_nextMobId++, d.Look, sx, sy, d.Name, d.Hp)
        {
            // Color byte = RTK's MobLookColor. (The client Monster.tbl palette turned out wrong here — it
            // rendered every mob green — so we use RTK's per-mob colour, which matches for most creatures.)
            Key = d.Key,   // carry the MobDef identifier so quest kill-matching can key on it
            Color = d.Color, Exp = d.Exp, Level = d.Level, Will = d.Will, Aggressive = d.Aggressive, Flees = d.Flees,
            MinDam = d.MinDam, MaxDam = d.MaxDam, Hit = d.Hit, IsBoss = d.IsBoss, Protection = d.Protection, Ac = d.Ac, Grace = d.Grace,
            Dir = 2, HomeX = sp.X, HomeY = sp.Y, Wander = true, Leash = WanderRadius,
            MoveTime = d.MoveTime, MoveTimer = Random.Shared.Next(d.MoveTime),   // stagger so they don't all step at once
        };
        Map(mapId).Mobs.Add(mob);
        _mobSpawn[mob.Id] = sp;
        sp.Live = mob;
        sp.RespawnTick = 0;
    }

    /// <summary>The spawn tile if it's open, else the nearest tile (within 2) that's in-bounds, not solid,
    /// and not already occupied by a live mob — so two spawns on one tile (or a respawn onto a wanderer)
    /// don't stack. Falls back to the spawn tile if everything nearby is taken. Caller holds <c>_lock</c>.</summary>
    private (ushort x, ushort y) FreeSpawnTile(ushort mapId, ushort x, ushort y)
    {
        var m = Map(mapId);
        var dims = Content.Maps.TryGetValue(mapId, out var mi) ? (mi.Xs, mi.Ys) : ((ushort)0, (ushort)0);
        var terrain = dims.Item1 > 0 ? MapData.For(mapId, dims.Item1, dims.Item2) : null;

        bool Free(int tx, int ty)
        {
            if (tx < 0 || ty < 0 || (dims.Item1 > 0 && (tx >= dims.Item1 || ty >= dims.Item2))) return false;
            if (terrain is not null && terrain.Solid(tx, ty)) return false;
            foreach (var mo in m.Mobs) if (mo.Alive && mo.X == tx && mo.Y == ty) return false;
            return true;
        }

        if (Free(x, y)) return (x, y);
        for (int r = 1; r <= 2; r++)
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;   // walk the ring at radius r
                    if (Free(x + dx, y + dy)) return ((ushort)(x + dx), (ushort)(y + dy));
                }
        return (x, y);   // everything nearby is taken — accept the overlap rather than drop the mob
    }

    /// <summary>Pick a random walkable home tile for an area spawn: inside its box, or anywhere on the map
    /// when the box is zero (RTK's "no bounds" form). Samples a handful of random tiles and takes the first
    /// walkable one; falls back to the box centre if the patch is dense/unloaded. Caller holds <c>_lock</c>.</summary>
    // Refill every forage box to its target stack count on random passable tiles (RTK itemspawner.lua:
    // count existing stacks of the item in the box, drop the shortfall). Runs under _lock; returns the new
    // drops (with their map) so the caller can broadcast them once the lock is released.
    private List<(ushort map, GroundItem gi)>? TopUpForageLocked()
    {
        List<(ushort, GroundItem)>? drops = null;
        foreach (var area in Content.ForageAreas)
        {
            if (!_maps.TryGetValue(area.Map, out var m) || m.Players.Count == 0) continue;   // no one watching
            var def = Content.ItemByKey(area.ItemKey);
            if (def is null) continue;

            int have = m.Items.Count(gi => gi.ItemId == def.Id &&
                                           gi.X >= area.MinX && gi.X <= area.MaxX &&
                                           gi.Y >= area.MinY && gi.Y <= area.MaxY);
            int need = area.Max - have;
            if (need <= 0) continue;

            var (xs, ys) = Content.Maps.TryGetValue(area.Map, out var mi) ? (mi.Xs, mi.Ys) : ((ushort)0, (ushort)0);
            var terrain = xs > 0 ? MapData.For(area.Map, xs, ys) : null;
            for (int i = 0; i < need; i++)
            {
                int tx = Random.Shared.Next(area.MinX, area.MaxX + 1);
                int ty = Random.Shared.Next(area.MinY, area.MaxY + 1);
                if (terrain is not null && terrain.Solid(tx, ty)) continue;   // passable tiles only (getPass==0)
                var gi = new GroundItem
                {
                    Id = _nextItemId++, ItemId = def.Id, X = (ushort)tx, Y = (ushort)ty,
                    Amount = Random.Shared.Next(area.MinQty, area.MaxQty + 1), Graphic = def.Icon,
                };
                m.Items.Add(gi);
                (drops ??= new()).Add((area.Map, gi));
            }
        }
        return drops;
    }

    private (ushort x, ushort y) PickAreaHome(ushort mapId, Spawn sp)
    {
        var (xs, ys) = Content.Maps.TryGetValue(mapId, out var mi) ? (mi.Xs, mi.Ys) : ((ushort)0, (ushort)0);
        int minX = sp.MinX, minY = sp.MinY, maxX = sp.MaxX, maxY = sp.MaxY;
        bool wholeMap = minX == 0 && minY == 0 && maxX == 0 && maxY == 0;
        if (wholeMap && xs > 0) { maxX = xs - 1; maxY = ys - 1; }
        if (xs > 0) { maxX = Math.Min(maxX, xs - 1); maxY = Math.Min(maxY, ys - 1); }
        if (maxX < minX) maxX = minX;
        if (maxY < minY) maxY = minY;

        var terrain = xs > 0 ? MapData.For(mapId, xs, ys) : null;
        for (int tries = 0; tries < 48; tries++)
        {
            int tx = Random.Shared.Next(minX, maxX + 1);
            int ty = Random.Shared.Next(minY, maxY + 1);
            if (terrain is null || !terrain.Solid(tx, ty)) return ((ushort)tx, (ushort)ty);
        }
        return ((ushort)((minX + maxX) / 2), (ushort)((minY + maxY) / 2));   // dense/solid box — accept centre
    }

    public uint AllocatePlayerId() { lock (_lock) return _nextPlayerId++; }
    public uint AllocateMobId()    { lock (_lock) return _nextMobId++; }
    public uint AllocateItemId()   { lock (_lock) return _nextItemId++; }

    private uint _nextTrapId = 1;

    /// <summary>Place a hidden trap (see <see cref="Trap"/>). Never broadcast — traps have no ground
    /// graphic; only <see cref="TrapsNear"/> (spot_traps) ever reveals one, and only to its caster.</summary>
    public Trap PlaceTrap(ushort mapId, ushort x, ushort y, string kind, uint ownerId, long expiresAt = 0)
    {
        var t = new Trap { Id = _nextTrapId++, X = x, Y = y, Kind = kind, OwnerId = ownerId, ExpiresAt = expiresAt };
        lock (_lock) Map(mapId).Traps.Add(t);
        return t;
    }

    // RTK bladestorm_trap.lua's block.side -> {x[],y[]} table: 4 tiles fanned out AHEAD of the TRIGGER's own
    // facing (0=N/1=E/2=S/3=W, this codebase's usual Dir convention) — not the caster's facing at cast time.
    private static readonly (int dx, int dy)[][] BladestormFan =
    {
        new[] { (0,-1), (-1,-2), (0,-2), (1,-2) },   // dir 0 = north
        new[] { (1,0), (2,-1), (2,0), (2,1) },       // dir 1 = east
        new[] { (0,1), (-1,2), (0,2), (1,2) },       // dir 2 = south
        new[] { (-1,0), (-2,1), (-2,0), (-2,-1) },   // dir 3 = west
    };

    /// <summary>Bladestorm's PC-trigger path (see Content.IsBladestormTrap) — the only trap kind a PLAYER can
    /// set off; the hazard family (dart/snare/…) stays mob-only. Called from Session.HandleWalk right after a
    /// successful step commits the new tile — HandleWalk holds no lock of its own, so the resulting damage
    /// can be applied directly here with no deferred queue (unlike the mob-trigger case in
    /// TriggerTrapLocked, which fires from inside World.Tick's own lock).</summary>
    public void CheckPlayerTrapTrigger(Session player, ushort mapId, ushort x, ushort y, byte facing)
    {
        Trap? trap;
        var coneTargets = new List<Mob>();
        lock (_lock)
        {
            var m = Map(mapId);
            trap = m.Traps.FirstOrDefault(t => t.Kind == "bladestorm" && t.X == x && t.Y == y);
            if (trap is null) return;
            m.Traps.Remove(trap);
            foreach (var (dx, dy) in BladestormFan[facing & 3])
            {
                var t = m.Mobs.FirstOrDefault(o => o.Alive && o.X == x + dx && o.Y == y + dy);
                if (t is not null) coneTargets.Add(t);
            }
        }
        // ONE damage number, computed from the trigger (RTK applies it uniformly, not per-target) — Session
        // owns the armor/HP math for a player trigger and caps its OWN loss to leave 1 HP; the cone targets
        // it catches take the same (uncapped) value via the existing trap-damage pipeline.
        int dmg = player.ApplyBladestormSelfDamage();
        foreach (var t in coneTargets) Try(() => ApplyTrapDamage(mapId, t, dmg, player.PlayerId));
    }

    /// <summary>Every trap within <paramref name="radius"/> tiles (Chebyshev) of a point — spot_traps'
    /// reveal, or a debug listing. Doesn't consume/remove anything.</summary>
    public Trap[] TrapsNear(ushort mapId, int x, int y, int radius)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return Array.Empty<Trap>();
            return m.Traps.Where(t => Math.Max(Math.Abs(t.X - x), Math.Abs(t.Y - y)) <= radius).ToArray();
        }
    }

    // Flat damage for the four "instant hit" trap kinds (RTK NPCs/trap/rogue_traps/*.lua — dart/repeating
    // are byte-for-byte the same script despite the name difference; only their spell-side level gate and
    // mana cost differ).
    private static readonly Dictionary<string, int> TrapDamage = new()
        { ["dart"] = 500, ["repeating"] = 500, ["spear"] = 3500, ["death"] = 11650 };

    // Caller holds _lock (called mid-movement-loop, mob has just stepped onto the trap's tile). Damage
    // kinds are queued for World.Tick's deferred pass (needs Session-facing broadcasts, which mustn't run
    // under the lock); status kinds (snare/sleep/flash — all simplified to the same "can't act" mechanic as
    // a cast Debuff, since this server has no separate armor-debuff/blind stat) and poison (a real DOT,
    // ticked every 1500ms by the poison check above) mutate the mob directly since that's lock-only state.
    private void TriggerTrapLocked(ushort mapId, Mob mob, Trap trap, List<(ushort map, Mob mob, int dmg, uint ownerId)> damageQueue)
    {
        long now = Environment.TickCount64;
        switch (trap.Kind)
        {
            case "dart" or "repeating" or "spear" or "death":
                damageQueue.Add((mapId, mob, TrapDamage[trap.Kind], trap.OwnerId));
                break;
            case "snare": mob.FrozenUntil = Math.Max(mob.FrozenUntil, now + 75000); break;   // RTK: armor+20 debuff, simplified to a hold
            case "sleep": mob.FrozenUntil = Math.Max(mob.FrozenUntil, now + 38000); break;
            case "flash": mob.FrozenUntil = Math.Max(mob.FrozenUntil, now + 10000); break;    // RTK: blind.cast, simplified to a hold
            case "poison":
                mob.PoisonUntil = now + 1 + Random.Shared.Next(1500, 30001);   // RTK: 1 + random(1500,30000) for a MOB target
                mob.PoisonNextTick = now + 1500;
                mob.PoisonTickDam = Math.Clamp((int)(mob.MaxHp * 0.01), 1, 1000);
                mob.PoisonOwnerId = trap.OwnerId;
                break;
            case "bladestorm":
            {
                // ONE HP-percent damage number computed from the trigger (RTK block.health*0.75, or *0.05 on
                // instance/high maps ids >= 60000), applied uniformly to the trigger itself AND every mob the
                // facing cone catches — see Content.IsBladestormTrap. The PC-trigger case (World.
                // CheckPlayerTrapTrigger) mirrors this but nets against the trigger's OWN armor instead.
                var mm = Map(mapId);
                int dmg = Math.Max(1, (int)(mob.Hp * (mapId < 60000 ? 0.75 : 0.05)));
                foreach (var (dx, dy) in BladestormFan[mob.Dir & 3])
                {
                    var t = mm.Mobs.FirstOrDefault(o => o.Alive && !ReferenceEquals(o, mob) && o.X == mob.X + dx && o.Y == mob.Y + dy);
                    if (t is not null) damageQueue.Add((mapId, t, dmg, trap.OwnerId));
                }
                damageQueue.Add((mapId, mob, dmg, mob.Id));   // the trigger takes it too (RTK block.health -= damage)
                break;
            }
        }
    }

    /// <summary>Apply a trap hit / poison tick's damage (deferred out of the movement lock — see
    /// <see cref="TriggerTrapLocked"/>): mutate HP via the normal <see cref="TryDamage"/> path, broadcast the
    /// over-head damage number (and death despawn), and credit the trap owner with exp on a kill.</summary>
    private void ApplyTrapDamage(ushort mapId, Mob mob, int dmg, uint ownerId)
    {
        if (!TryDamage(mapId, mob, dmg, out bool died, ownerId)) return;
        byte pct = died ? (byte)0 : (byte)Math.Clamp(mob.Hp * 100 / Math.Max(1, mob.MaxHp), 1, 100);
        Broadcast(mapId, p => p.DamageOver(mob.Id, pct, 33));
        if (died)
        {
            uint mobId = mob.Id;
            _ = Task.Run(async () => { try { await Task.Delay(600); Broadcast(mapId, p => p.DespawnEntity(mobId)); } catch { } });
            uint reward = (uint)(mob.Exp > 0 ? mob.Exp : mob.MaxHp);
            PlayerById(ownerId)?.AwardExp(reward, killExp: true);
        }
    }

    /// <summary>A pet's swing landing on another mob. Same shape as <see cref="ApplyTrapDamage"/> — damage,
    /// over-head bar, delayed corpse despawn, exp to the human who owns the attacker — plus the melee
    /// swing/impact sfx a mob-on-mob hit should make, and retaliation.
    /// <para><b>attackerId stays 0 into TryDamage on purpose.</b> That parameter marks a PLAYER as the
    /// victim's new target, and routing the pet's owner in there would make every pet swing pull the mob
    /// straight onto its owner — the opposite of what a pet is for. Retaliation is handled below instead,
    /// and only when the victim isn't already busy with a player, so a pet can't soak a fight that was never
    /// aimed at it.</para></summary>
    private void ApplyMobOnMobHit(ushort mapId, Mob attacker, Mob victim, int dmg)
    {
        // Either side can have died to an earlier entry in this same batch — don't play a dead pet's swing.
        if (!attacker.Alive || !victim.Alive) return;
        Broadcast(mapId, p => p.SoundAt(Session.MobSwingSfx, attacker.Id));   // 009.wav on the swing itself
        if (!TryDamage(mapId, victim, dmg, out bool died)) return;
        Broadcast(mapId, p => p.DamageOver(victim.Id, died ? (byte)0 : (byte)Math.Clamp(victim.Hp * 100 / Math.Max(1, victim.MaxHp), 1, 100),
                                            0, Session.MobHitSfx));
        if (died)
        {
            uint victimId = victim.Id;
            _ = Task.Run(async () => { try { await Task.Delay(600); Broadcast(mapId, p => p.DespawnEntity(victimId)); } catch { } });
            // The owner gets the kill: RTK credits a mob's damage to map_id2sd(mob->owner) the same way
            // (clif.c's `tmob->owner < MOB_START_NUM` lookup), so a pet kill counts as yours.
            uint reward = (uint)(victim.Exp > 0 ? victim.Exp : victim.MaxHp);
            PlayerById(attacker.OwnerId)?.AwardExp(reward, killExp: true);
            return;
        }
        lock (_lock) if (victim.Alive && victim.TargetId == 0) victim.TargetMobId = attacker.Id;
    }

    /// <summary>One step of a chase toward <c>(tx,ty)</c> — a port of RTK's <c>FindCoords</c>
    /// (<c>rtklua/Accepted/Mobs/mob.lua:299</c>), which is the real 4.95 chase step. Caller holds
    /// <c>_lock</c>. True if the mob moved.
    /// <para>The single chase-movement path in the world: the provoked-mob chase, both pet movers (closing on
    /// the foe it is assisting against, and heeling back to its owner), and pet retaliation all run through
    /// here, so obstacle handling can't differ between them.</para>
    ///
    /// <para><b>This is deliberately, permanently stupid. Do not make it smarter.</b> No A*, no map search, no
    /// lookahead, no wall-following, no memory of where it has been. RTK's version tries ONLY the one or two
    /// directions that close on the target — vertical then horizontal, or horizontal then vertical, on a
    /// coin flip (<c>checkmove = math.random(0, 2)</c>, ≥1 picks vertical-first) — and takes the first that
    /// isn't blocked. That coin flip is the entire cleverness of 4.95 mob pathing.</para>
    ///
    /// <para>What that produces, and what it's SUPPOSED to produce: a mob diagonal from you rounds an
    /// ordinary corner by itself, because when one axis is blocked the other one is still "toward" you. A mob
    /// squared up against a wall with you straight behind it has no toward-step left, and shuffles sideways
    /// instead — one tile out, one tile back, because next tick the step back is the one that closes on you.
    /// <see cref="Mob.DetourDir"/>/<see cref="Mob.DetourLeft"/> exist only to stretch that shuffle to a
    /// random 1-3 tiles so it isn't metronomic. It <b>never steps directly away</b>, so a mob in a pit with
    /// you on the near side will never find the stairs on the far side — it will pace at the bottom forever.
    /// That is correct 4.95 behaviour, not a bug to fix.</para>
    ///
    /// <para>Two deliberate departures from RTK's <c>FindCoords</c>, both in its "nothing worked" branch:
    /// RTK flails at up to 11 fully random sides (which lets it walk straight away from you), and it re-rolls
    /// <c>mob.target</c> to a random other player standing nearby. The sideways-only shuffle replaces the
    /// first; the second is just dropped — target acquisition belongs to the aggro scan, not to being stuck
    /// behind a rock.</para></summary>
    private bool StepMobToward(ushort mapId, MapState m, Mob mob, int tx, int ty,
                               (ushort Xs, ushort Ys) dims, MapData? terrain,
                               HashSet<(ushort, ushort)> occupied, HashSet<(int, int)> mobTiles,
                               List<(ushort map, uint id, ushort x, ushort y, byte dir)> moves,
                               List<(ushort map, uint id, byte dir)> turns,
                               List<(ushort map, Mob mob, int dmg, uint ownerId)> trapDamage)
        => StepMobToward(mapId, m, mob, tx, ty, dims, terrain, occupied, mobTiles, moves, turns, trapDamage, out _);

    /// <param name="towardBlocked">True when NOTHING that closes the gap was open this step — the mob is
    /// walled off from its target rather than merely taking a longer route. RTK's <c>canmove == false</c>.
    /// Callers use it to decide whether to go looking for a target they can actually reach.</param>
    /// <inheritdoc cref="StepMobToward(ushort, MapState, Mob, int, int, ValueTuple{ushort, ushort}, MapData, HashSet{ValueTuple{ushort, ushort}}, HashSet{ValueTuple{int, int}}, List{ValueTuple{ushort, uint, ushort, ushort, byte}}, List{ValueTuple{ushort, uint, byte}}, List{ValueTuple{ushort, Mob, int, uint}})"/>
    private bool StepMobToward(ushort mapId, MapState m, Mob mob, int tx, int ty,
                               (ushort Xs, ushort Ys) dims, MapData? terrain,
                               HashSet<(ushort, ushort)> occupied, HashSet<(int, int)> mobTiles,
                               List<(ushort map, uint id, ushort x, ushort y, byte dir)> moves,
                               List<(ushort map, uint id, byte dir)> turns,
                               List<(ushort map, Mob mob, int dmg, uint ownerId)> trapDamage,
                               out bool towardBlocked)
    {
        towardBlocked = false;
        int dx = tx - mob.X, dy = ty - mob.Y;
        if (dx == 0 && dy == 0) { mob.DetourDir = NoDetour; mob.DetourLeft = 0; return false; }

        // Take one cardinal step if that tile is free (bounds + no player + no other mob + the two-layer
        // terrain test), turning onto it first and springing any trap it lands on. RTK's mob:move().
        bool Step(byte dir)
        {
            int nx = mob.X + (dir == 1 ? 1 : dir == 3 ? -1 : 0);
            int ny = mob.Y + (dir == 2 ? 1 : dir == 0 ? -1 : 0);
            if (nx < 0 || ny < 0) return false;
            if (dims.Xs != 0 && (nx >= dims.Xs || ny >= dims.Ys)) return false;
            if (occupied.Contains(((ushort)nx, (ushort)ny))) return false;      // never onto a player
            if (mobTiles.Contains((nx, ny))) return false;                      // nor another creature
            if (terrain is not null && terrain.BlockedMove(nx, ny, dir)) return false;   // pass flag OR SObj wall
            if (mob.Dir != dir) { mob.Dir = dir; turns.Add((mapId, mob.Id, dir)); }
            return StepMobTo(mapId, m, mob, nx, ny, dir, mobTiles, moves, trapDamage);
        }

        // A sideways shuffle already under way keeps going for its remaining tiles. Without this the step
        // that closes on the target always wins the next tick, so every shuffle would be exactly one tile
        // out and one tile back — right most of the time, but too regular to look alive.
        if (mob.DetourDir != NoDetour && mob.DetourLeft > 0)
        {
            if (Step(mob.DetourDir)) { if (--mob.DetourLeft == 0) mob.DetourDir = NoDetour; return true; }
            mob.DetourDir = NoDetour; mob.DetourLeft = 0;   // that way is blocked too — abandon the run
        }

        // RTK FindCoords proper: only the directions that close the gap, on a coin-flipped axis order.
        var toward = new List<byte>(2);
        byte? vert = dy > 0 ? (byte)2 : dy < 0 ? (byte)0 : null;
        byte? horz = dx > 0 ? (byte)1 : dx < 0 ? (byte)3 : null;
        if (Random.Shared.Next(3) >= 1) { if (vert is byte a) toward.Add(a); if (horz is byte b) toward.Add(b); }
        else                            { if (horz is byte c) toward.Add(c); if (vert is byte d) toward.Add(d); }

        foreach (byte dir in toward)
            if (Step(dir)) { mob.DetourDir = NoDetour; mob.DetourLeft = 0; return true; }

        // Nothing that closes the gap is open.
        towardBlocked = true;

        // Shuffle sideways — and ONLY sideways: the two directions perpendicular to the axis we're stuck on,
        // never the one straight back. If the target is diagonal there is no purely-sideways option at all
        // (both remaining sides retreat on one axis), so the mob just stands there facing you, which is
        // exactly what a cornered 4.95 mob does.
        var sides = new List<byte>(2);
        if (dx == 0) { sides.Add(1); sides.Add(3); }        // stuck on the vertical -> try east/west
        else if (dy == 0) { sides.Add(0); sides.Add(2); }   // stuck on the horizontal -> try north/south
        if (sides.Count == 2 && Random.Shared.Next(2) == 1) sides.Reverse();

        foreach (byte dir in sides)
            if (Step(dir))
            {
                // Run length: 1-3 tiles most of the time, occasionally stretching to 6. Long runs are fine —
                // it's the DIRECTION that has to stay honest. (RTK's flail is 11 tries at any of the four
                // sides, which lets a stuck mob walk 11 tiles straight away from you; that never happens in
                // the real game, so the away side simply isn't a candidate here.)
                int run = Random.Shared.Next(1, 4);
                if (Random.Shared.Next(4) == 0) run += Random.Shared.Next(1, 4);
                mob.DetourLeft = (byte)(run - 1);            // this step, plus the rest of the run
                mob.DetourDir = mob.DetourLeft == 0 ? NoDetour : dir;
                return true;
            }

        // Boxed in. Face the target so it at least reads as wanting to reach you.
        mob.DetourDir = NoDetour; mob.DetourLeft = 0;
        if (toward.Count > 0 && mob.Dir != toward[0]) { mob.Dir = toward[0]; turns.Add((mapId, mob.Id, toward[0])); }
        return false;
    }

    /// <summary>No sideways shuffle in progress — see <see cref="Mob.DetourDir"/>.</summary>
    private const byte NoDetour = 0xFF;

    /// <summary>Commit a validated one-tile move: update the tile index, queue the broadcast, spring a trap.</summary>
    /// <summary>One step of a RETREAT from <c>(tx,ty)</c> — the mirror image of <see cref="StepMobToward"/>, and
    /// a port of RTK's <c>RunAway</c> (<c>rtklua/Accepted/Mobs/mob.lua:427</c>). Caller holds <c>_lock</c>.
    /// True if the mob moved.
    /// <para>RTK's routine has two cases and this keeps both. Standing right next to the player
    /// (<c>moveIntent == 1</c>): turn 180° and go, which is the bolt when you close to melee range. Otherwise:
    /// try each direction that increases the gap, coin-flipping whether the vertical or the horizontal one is
    /// attempted first, and take the first that isn't blocked — the away-mirror of FindCoords' axis flip.</para>
    /// <para>The last resort differs. RTK, having nowhere to run, picks a random nearby player as its new target
    /// and flails at up to 10 random sides; a prey creature has no target to pick, so a cornered one takes any
    /// open SIDEWAYS step instead (never back toward what is chasing it) and simply stands still if even that is
    /// walled off. Cornering a rabbit against a cliff is supposed to be how you catch it.</para>
    /// <para>Home moves with the mob on every retreat step. The wander leash below tests the DESTINATION against
    /// Home, so a creature that fled past its leash could never step anywhere again — it would freeze the moment
    /// you walked away. Re-homing keeps it wandering wherever it ends up, which is also what a spooked animal
    /// looks like: it doesn't run back to the exact tile it was born on.</para></summary>
    private bool StepMobAway(ushort mapId, MapState m, Mob mob, int tx, int ty,
                             (ushort Xs, ushort Ys) dims, MapData? terrain,
                             HashSet<(ushort, ushort)> occupied, HashSet<(int, int)> mobTiles,
                             List<(ushort map, uint id, ushort x, ushort y, byte dir)> moves,
                             List<(ushort map, uint id, byte dir)> turns,
                             List<(ushort map, Mob mob, int dmg, uint ownerId)> trapDamage)
    {
        int dx = tx - mob.X, dy = ty - mob.Y;

        bool Step(byte dir)
        {
            int nx = mob.X + (dir == 1 ? 1 : dir == 3 ? -1 : 0);
            int ny = mob.Y + (dir == 2 ? 1 : dir == 0 ? -1 : 0);
            if (nx < 0 || ny < 0) return false;
            if (dims.Xs != 0 && (nx >= dims.Xs || ny >= dims.Ys)) return false;
            if (occupied.Contains(((ushort)nx, (ushort)ny))) return false;
            if (mobTiles.Contains((nx, ny))) return false;
            if (terrain is not null && terrain.BlockedMove(nx, ny, dir)) return false;
            if (mob.Dir != dir) { mob.Dir = dir; turns.Add((mapId, mob.Id, dir)); }
            if (!StepMobTo(mapId, m, mob, nx, ny, dir, mobTiles, moves, trapDamage)) return false;
            mob.HomeX = mob.X; mob.HomeY = mob.Y;   // see the doc note on re-homing
            return true;
        }

        // Cornered by an adjacent player (RTK's moveIntent branch): about-face and bolt.
        bool adjacent = (dx == 0 && Math.Abs(dy) == 1) || (dy == 0 && Math.Abs(dx) == 1);
        if (adjacent && Step((byte)((mob.Dir + 2) & 3))) return true;

        // Otherwise: the directions that open the gap, vertical-or-horizontal first on a coin flip.
        var away = new List<byte>(2);
        byte? vert = dy > 0 ? (byte)0 : dy < 0 ? (byte)2 : null;   // player south of us -> run north
        byte? horz = dx > 0 ? (byte)3 : dx < 0 ? (byte)1 : null;   // player east of us  -> run west
        if (Random.Shared.Next(3) >= 1) { if (vert is byte a) away.Add(a); if (horz is byte b) away.Add(b); }
        else                            { if (horz is byte c) away.Add(c); if (vert is byte d) away.Add(d); }
        foreach (byte dir in away) if (Step(dir)) return true;

        // Nowhere to retreat: slip sideways rather than back into them.
        var sides = new List<byte>(2);
        if (dx == 0) { sides.Add(1); sides.Add(3); }
        else if (dy == 0) { sides.Add(0); sides.Add(2); }
        if (sides.Count == 2 && Random.Shared.Next(2) == 1) sides.Reverse();
        foreach (byte dir in sides) if (Step(dir)) return true;
        return false;
    }

    /// <summary>A player swung at <paramref name="mob"/> — hit OR miss. A prey creature (<see cref="Mob.Flees"/>)
    /// bolts: it moves on half its usual timer for <see cref="PanicMs"/>, refreshed by each further swing (RTK
    /// Instances/mysterious_merchant.lua <c>on_attacked</c>: <c>mob.newMove = 500</c>). No effect on anything
    /// else — an ordinary mob is provoked by <see cref="TryDamage"/>, which needs damage to have landed.</summary>
    public void Spook(Mob mob)
    {
        if (!mob.Flees) return;
        lock (_lock) mob.PanicUntil = Environment.TickCount64 + PanicMs;
    }

    private bool StepMobTo(ushort mapId, MapState m, Mob mob, int nx, int ny, byte dir,
                           HashSet<(int, int)> mobTiles,
                           List<(ushort map, uint id, ushort x, ushort y, byte dir)> moves,
                           List<(ushort map, Mob mob, int dmg, uint ownerId)> trapDamage)
    {
        ushort ox = mob.X, oy = mob.Y;
        mobTiles.Remove((mob.X, mob.Y));
        mob.X = (ushort)nx; mob.Y = (ushort)ny;
        mobTiles.Add((nx, ny));
        moves.Add((mapId, mob.Id, ox, oy, dir));
        var trap = m.Traps.FirstOrDefault(t => t.X == nx && t.Y == ny);
        if (trap is not null) { m.Traps.Remove(trap); TriggerTrapLocked(mapId, mob, trap, trapDamage); }
        return true;
    }

    private MapState Map(ushort id)
    {
        if (!_maps.TryGetValue(id, out var m)) { m = new MapState(); _maps[id] = m; }
        return m;
    }

    // ---- players joining / leaving a map ------------------------------------------------------

    /// <summary>Register <paramref name="s"/> on <paramref name="mapId"/>, broadcast it to everyone
    /// already there, and return the peers + mobs the caller should draw for the newcomer.</summary>
    public (Session[] peers, Mob[] mobs) EnterMap(Session s, ushort mapId)
    {
        Session[] peers; Mob[] mobs;
        lock (_lock)
        {
            EnsureMaterialized(mapId);                 // instantiate this map's spawns on first entry
            var m = Map(mapId);
            if (!m.Players.Contains(s)) m.Players.Add(s);
            peers = m.Players.Where(p => p != s).ToArray();
            mobs = m.Mobs.ToArray();
        }
        foreach (var p in peers) Try(() => p.ShowPlayer(s));   // tell the room about the newcomer
        return (peers, mobs);
    }

    /// <summary>Read-only: the peers + mobs on <paramref name="mapId"/> (excluding <paramref name="s"/>),
    /// WITHOUT registering or broadcasting. Used to re-assert the view after a client-side map rebuild
    /// (e.g. an in-place 0x15 refresh) drops all foreign entities.</summary>
    public (Session[] peers, Mob[] mobs) View(Session s, ushort mapId)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return (Array.Empty<Session>(), Array.Empty<Mob>());
            return (m.Players.Where(p => p != s).ToArray(), m.Mobs.ToArray());
        }
    }

    /// <summary>Remove <paramref name="s"/> from <paramref name="mapId"/> and despawn it for the rest.</summary>
    public void LeaveMap(Session s, ushort mapId)
    {
        Session[] peers;
        uint id = s.PlayerId;
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return;
            m.Players.Remove(s);
            peers = m.Players.ToArray();
        }
        foreach (var p in peers) Try(() => p.DespawnEntity(id));
    }

    // ---- broadcasts ---------------------------------------------------------------------------

    /// <summary>Run <paramref name="send"/> for every player on <paramref name="mapId"/> (except
    /// <paramref name="except"/>), outside the lock and exception-guarded.</summary>
    public void Broadcast(ushort mapId, Action<Session> send, Session? except = null)
    {
        Session[] peers;
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return;
            peers = m.Players.Where(p => p != except).ToArray();
        }
        foreach (var p in peers) Try(() => send(p));
    }

    /// <summary>Current weather for a map (0=clear/1=WRAIN/2=WSNOW), for a player entering/re-entering it —
    /// see MapState.Weather. Unpopulated maps default to clear (never rolled — see the Tick's WeatherRollTicks
    /// pass, which only touches maps with at least one player).</summary>
    public byte GetWeather(ushort mapId) { lock (_lock) return _maps.TryGetValue(mapId, out var m) ? m.Weather : (byte)0; }

    /// <summary>Force a map's weather (the "@weather" debug command — RTK's own setWeatherM is the same kind
    /// of admin/quest-script lever). Broadcasts immediately to everyone already on that map.</summary>
    public void SetWeather(ushort mapId, byte weather)
    {
        lock (_lock) Map(mapId).Weather = weather;
        Broadcast(mapId, p => p.SendWeather(weather));
    }

    // ---- mobs ---------------------------------------------------------------------------------

    /// <summary>Full population hot-reload (the <c>@reload</c> path, after <see cref="Content.Reload"/> swapped
    /// the spawn/NPC registries): tear down every spawn-backed mob AND stationary NPC, rebuild the spawn roster
    /// + NPC placement from the fresh <see cref="Content"/>, and re-materialize the maps that currently have
    /// players (the rest build lazily on next entry). Unlike the old in-place re-stat, this picks up
    /// ADDED / REMOVED / REPOSITIONED spawn rows and NPCs — editing <c>AreaSpawns.csv</c>/<c>Spawns.csv</c> or
    /// an NPC's tile now takes effect on <c>@reload</c> without a restart. Cost is bounded: only populated maps
    /// re-materialize; the ~23k lazy points elsewhere are cheap point objects again. Players on a populated map
    /// briefly see mobs blink (despawn now, re-stream on the next <see cref="Tick"/>) — acceptable for an admin
    /// reload; a wounded mob also resets to full, since area spawns have no stable identity to preserve.
    /// Returns (mobs torn down, NPCs placed, maps re-materialized).</summary>
    public (int mobs, int npcs, int maps) RebuildPopulation()
    {
        var despawn = new List<(ushort map, uint id)>();
        var populated = new List<ushort>();
        int npcs = 0;
        lock (_lock)
        {
            // 1. tear down every shared mob (spawn-backed AND stationary NPC) on every map, remembering which
            //    maps have players so we know what to re-materialize. (Session-local debug dummies aren't in
            //    _maps, so they're untouched.)
            foreach (var (mapId, m) in _maps)
            {
                if (m.Players.Count > 0) populated.Add(mapId);
                foreach (var g in m.Mobs) despawn.Add((mapId, g.Id));
                m.Mobs.Clear();
            }
            // 2. rebuild the spawn roster + NPC placement from the just-reloaded Content (fresh defs, positions,
            //    and any added/removed rows). NPCs are placed on every map (cheap, ~340); mobs stay lazy.
            _spawns.Clear();
            _materialized.Clear();
            BuildSpawnRosterLocked();
            foreach (var n in Content.Npcs) if (n.Enabled) { PlaceNpc(n); npcs++; }
            // 3. re-materialize only the maps that currently have players; the rest build lazily on next entry.
            foreach (var mapId in populated) EnsureMaterialized(mapId);
        }
        // Despawn the torn-down entities on clients (socket I/O outside the lock). The freshly placed NPCs +
        // materialized mobs stream back to players via the next Tick's viewport sync (~one tick later).
        foreach (var (map, id) in despawn) Broadcast(map, p => p.DespawnEntity(id));
        return (despawn.Count, npcs, populated.Count);
    }

    /// <summary>Map ids that currently have at least one player — used by the @reload path to pre-warm the
    /// terrain cache OUTSIDE this lock before <see cref="RebuildPopulation"/> re-materializes them (so the
    /// .map re-reads don't happen under the world lock, per the reload-stall fix).</summary>
    public List<ushort> PopulatedMapIds()
    {
        lock (_lock) return _maps.Where(kv => kv.Value.Players.Count > 0).Select(kv => kv.Key).ToList();
    }

    /// <summary>Add a shared mob to a map and stream it to everyone whose viewport it falls in (players
    /// out of range receive it later, as they approach, via <see cref="Tick"/>'s per-player sync).</summary>
    public void AddMob(ushort mapId, Mob mob)
    {
        lock (_lock) Map(mapId).Mobs.Add(mob);
        var one = new[] { mob };
        Broadcast(mapId, p => p.SyncMobs(one));
    }

    /// <summary>Apply a player's targeted timed stat buff (Session.CastTargetBuff — e.g. Valor/Harden Armor on a
    /// pet) to a mob, refresh-not-stack by spell key. Taken under <c>_lock</c> so the mob.Buffs list mutation
    /// can't race the Tick's expiry-revert pass (which runs the same list under the lock).</summary>
    public void ApplyMobBuff(Mob mob, string stat, int amount, int durMs, string key)
    {
        if (string.IsNullOrEmpty(stat) || amount == 0 || durMs <= 0) return;
        lock (_lock)
        {
            mob.Buffs ??= new();
            for (int i = mob.Buffs.Count - 1; i >= 0; i--)   // refresh: revert + drop any prior cast of THIS spell
                if (mob.Buffs[i].Key == key) { mob.AdjustBuffField(mob.Buffs[i].Stat, mob.Buffs[i].Amount, -1); mob.Buffs.RemoveAt(i); }
            mob.AdjustBuffField(stat, amount, +1);
            mob.Buffs.Add(new Mob.TimedBuff { Stat = stat, Amount = amount, ExpiresAt = Environment.TickCount64 + durMs, Key = key });
        }
    }

    /// <summary>Apply a venom/poison damage-over-time to a mob (RTK mage venom.lua family — the SAME engine the
    /// Rogue poison-dart trap drives, see <see cref="TriggerTrapLocked"/>'s "poison" case): ticks MaxHp*1% every
    /// 1500ms for a random window (1 + random(<paramref name="lowMs"/>, <paramref name="highMs"/>)), the per-tick
    /// damage clamped to [1, <paramref name="tickCap"/>] so it can never itself land the killing blow (World.Tick
    /// only ticks while Hp > the tick amount). Returns false if the mob is already venomed (checkIfCast(venoms)).
    /// <para><paramref name="flatTick"/> &gt; 0 replaces the proportional MaxHp*1% with a FLAT per-tick amount —
    /// RTK's Burn (Spells/NPCs/burn.lua) is the one member of this family whose while_cast deals a hardcoded
    /// 1000 rather than a percentage, and clamping a flat 1000 through <paramref name="tickCap"/> would silently
    /// weaken it against anything under 100k HP.</para></summary>
    public bool PoisonMob(Mob mob, int tickCap, int lowMs, int highMs, uint ownerId, int flatTick = 0)
    {
        lock (_lock)
        {
            long now = Environment.TickCount64;
            if (mob.PoisonUntil > now) return false;                        // already venomed — RTK checkIfCast(venoms)
            mob.PoisonUntil     = now + 1 + Random.Shared.Next(lowMs, highMs + 1);
            mob.PoisonNextTick  = now + 1500;
            mob.PoisonTickDam   = flatTick > 0 ? flatTick : Math.Clamp((int)(mob.MaxHp * 0.01), 1, tickCap);
            mob.PoisonOwnerId   = ownerId;
            return true;
        }
    }

    /// <summary>How many of this owner's pets (RTK Poet "Call of the Wild" summons) are currently alive on
    /// this map — the spawn cap in Content.PetCapFor is checked against this (RTK cotw_spawnCheck: same-map
    /// only, matching <c>player:getObjectsInMap</c>).</summary>
    public int PetCountFor(ushort mapId, uint ownerId)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return 0;
            return m.Mobs.Count(mo => mo.Alive && mo.OwnerId == ownerId);
        }
    }

    /// <summary>The first living mob on (x,y) of <paramref name="mapId"/>, or null.</summary>
    public Mob? MobAt(ushort mapId, int x, int y)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return null;
            return m.Mobs.FirstOrDefault(mo => mo.Alive && mo.X == x && mo.Y == y);
        }
    }

    /// <summary>The nearest living, non-NPC mob within <paramref name="radius"/> tiles (Chebyshev) of a
    /// point matching <paramref name="match"/>, or null. Used by the 'r' ride key (RTK clif_findmount) to
    /// locate a rideable "horse" mob — called with <c>radius 0</c> at the player's <c>FrontTile()</c> so it
    /// only matches the exact tile faced (cardinal only, matching the player's own melee reach).</summary>
    public Mob? MobNear(ushort mapId, int x, int y, int radius, Func<Mob, bool> match)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return null;
            return m.Mobs.Where(mo => mo.Alive && !mo.IsNpc && match(mo)
                                       && Math.Max(Math.Abs(mo.X - x), Math.Abs(mo.Y - y)) <= radius)
                          .OrderBy(mo => Math.Max(Math.Abs(mo.X - x), Math.Abs(mo.Y - y)))
                          .FirstOrDefault();
        }
    }

    /// <summary>Remove a live mob from the map WITHOUT a kill (no loot roll, no exp) — used when a player
    /// rides it away ('r' key). If it's a spawn point's mob, the point is freed to respawn like a normal
    /// death; an ad-hoc mob (e.g. one summoned by a dismount) is just dropped. Broadcasts the despawn.</summary>
    public bool DespawnMob(ushort mapId, Mob mob)
    {
        lock (_lock)
        {
            if (!mob.Alive || mob.IsNpc) return false;
            if (!_maps.TryGetValue(mapId, out var m)) return false;
            m.Mobs.Remove(mob);
            if (_mobSpawn.TryGetValue(mob.Id, out var sp))
            {
                sp.Live = null;
                sp.RespawnTick = NextRespawnTick(sp);
                _mobSpawn.Remove(mob.Id);
            }
        }
        Broadcast(mapId, p => p.DespawnEntity(mob.Id));
        return true;
    }

    /// <summary>The player standing on (x,y) of <paramref name="mapId"/>, or null. Used by the ';' look key
    /// (RTK clif_parselookat checks PC before mob/item/NPC).</summary>
    public Session? PeerAt(ushort mapId, int x, int y)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return null;
            return m.Players.FirstOrDefault(p => p.PlayerX == x && p.PlayerY == y);
        }
    }

    /// <summary>The connected player with this character name (case-insensitive, any map), or null if
    /// they're offline. Used by whisper/tell (RTK clif_parsewisp's target lookup).</summary>
    public Session? FindPlayer(string name)
    {
        lock (_lock)
            return _maps.Values.SelectMany(m => m.Players)
                                .FirstOrDefault(p => string.Equals(p.Snapshot().Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The connected player with this entity id (any map), or null. Used by click-profile's "view
    /// another player" path (RTK <c>clif_clickonplayer</c>, §9.5/§11l) and the exchange-initiate opcode
    /// <c>0x4A</c> (RTK <c>clif_parse_exchange</c> type 0), both of which address a player by id — the
    /// client already knows it from the entity it rendered — rather than by name.</summary>
    public Session? PlayerById(uint id)
    {
        lock (_lock)
            return _maps.Values.SelectMany(m => m.Players).FirstOrDefault(p => p.PlayerId == id);
    }

    /// <summary>Every connected player, across every map — a server-wide (not map-scoped) roster snapshot.
    /// Used by channels that reach beyond one map, like subpath chat (RTK clif_sendsubpathmessage loops
    /// every session, not just one map's block list).</summary>
    public List<Session> AllPlayers()
    {
        lock (_lock)
            return _maps.Values.SelectMany(m => m.Players).ToList();
    }

    /// <summary>Duplicate-login guard: atomically register <paramref name="s"/> as the online session for
    /// <paramref name="key"/> (CharacterStore.Key(username)), returning whatever session previously held
    /// that slot via <paramref name="old"/> (null if this is a fresh login). Called from HandleArrival
    /// BEFORE the character is loaded from disk, so a second concurrent arrival for the same account can
    /// never both pass unnoticed — the dictionary write is atomic under _lock. The caller (HandleArrival)
    /// is responsible for kicking <paramref name="old"/> (Session.KickForReplacement) so its state is
    /// flushed before the new session's own Load runs.</summary>
    public void RegisterOnline(string key, Session s, out Session? old)
    {
        lock (_lock)
        {
            _online.TryGetValue(key, out old);
            _online[key] = s;
        }
    }

    /// <summary>Remove <paramref name="s"/> from the online registry, but ONLY if it still owns that slot —
    /// a compare-and-remove so a session that was already kicked/replaced (RegisterOnline overwrote its
    /// slot with the newer session) can't accidentally evict the session that replaced it when its own
    /// (now-stale) teardown finally runs.</summary>
    public void Unregister(string key, Session s)
    {
        lock (_lock)
        {
            if (_online.TryGetValue(key, out var cur) && ReferenceEquals(cur, s))
                _online.Remove(key);
        }
    }

    /// <summary>Periodic crash-safety backstop (see AutoSaveLoop): flush every connected player's pending
    /// mutation, regardless of the per-session AutoSaveMs throttle. Its unique job is an IDLE dirty player
    /// (mutated, then stopped sending packets, so their own read-loop FlushIfDue never gets another
    /// iteration to fire on) — an ACTIVE player is already covered by their own on-thread flush.</summary>
    private void AutoSaveTick()
    {
        foreach (var s in AllPlayers()) s.FlushNow();
    }

    private async Task AutoSaveLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Session.AutoSaveMs));
        while (await timer.WaitForNextTickAsync())
        {
            try { AutoSaveTick(); }
            catch (Exception e) { Log.Info($"!! autosave sweep error: {e.Message}"); }
        }
    }

    /// <summary>Graceful-shutdown flush: force-save every connected player right now, ignoring the dirty
    /// flag entirely is NOT needed here — FlushNow already no-ops a clean session cheaply. Returns the
    /// number of players swept (for the shutdown-hook log line). Cannot help against a hard crash/kill —
    /// that's what the periodic AutoSaveLoop sweep + each session's own on-thread flush bound instead.</summary>
    public int SaveAllPlayers()
    {
        var players = AllPlayers();
        foreach (var s in players) s.FlushNow();
        return players.Count;
    }

    /// <summary>NPCs (stationary, IsNpc) within <paramref name="radius"/> tiles (Chebyshev) of a point, nearest
    /// first. Used to route a player's speech to a nearby NPC's say-handler (RTK onSayClick).</summary>
    public List<Mob> NpcsNear(ushort mapId, int x, int y, int radius)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return new();
            return m.Mobs.Where(mo => mo.IsNpc &&
                                      Math.Max(Math.Abs(mo.X - x), Math.Abs(mo.Y - y)) <= radius)
                         .OrderBy(mo => Math.Max(Math.Abs(mo.X - x), Math.Abs(mo.Y - y)))
                         .ToList();
        }
    }

    /// <summary>The live world mob with this entity id on the map, or null (used by targeted spell casts,
    /// where the client sends the target's entity id rather than a tile).</summary>
    public Mob? MobById(ushort mapId, uint id)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return null;
            return m.Mobs.FirstOrDefault(mo => mo.Alive && mo.Id == id);
        }
    }

    /// <summary>Apply damage under the lock (so concurrent attackers can't double-kill). Returns
    /// false if the mob was already dead; otherwise sets <paramref name="died"/> and, on death,
    /// removes it from the map, schedules its spawn point to respawn, and rolls floor loot (which this
    /// method drops + broadcasts). The caller still broadcasts the damage number / corpse despawn.
    /// <paramref name="attackerId"/> (0 = none, e.g. a session-local debug hit) marks the mob as targeting
    /// that player — <see cref="Tick"/> then has it chase and fight back instead of just wandering.</summary>
    public bool TryDamage(ushort mapId, Mob mob, int dmg, out bool died, uint attackerId = 0)
    {
        died = false;
        List<GroundItem>? drops = null;
        lock (_lock)
        {
            if (!mob.Alive || mob.IsNpc) return false;   // NPCs are indestructible (a click talks to them, not fights)
            mob.Hp -= dmg;
            died = !mob.Alive;
            // Provoked -> fight back (mob_ai_normal on_attacked). Getting hit ALWAYS wins: it drops whatever
            // mob it was scrapping with (a pet) and re-points it at the player, and it overrides the
            // stuck-mob retarget in Tick — so zapping something always drags its aggro onto you, wall or no
            // wall, however unreachable you are.
            if (!died && attackerId != 0) { mob.TargetId = attackerId; mob.TargetMobId = 0; mob.DetourDir = NoDetour; mob.DetourLeft = 0; }
            // …unless it's PREY, which has no fight in it: being hurt by anything (a spell, a trap, a swing)
            // panics it instead. Tick clears the TargetId set just above before it can ever be acted on; this
            // is what makes a spell as alarming as a sword. A pure MISS never reaches here — Session.ResolveSwing
            // calls Spook directly for that case.
            if (!died && mob.Flees) mob.PanicUntil = Environment.TickCount64 + PanicMs;
            if (died && _maps.TryGetValue(mapId, out var m))
            {
                m.Mobs.Remove(mob);
                if (_mobSpawn.TryGetValue(mob.Id, out var sp))
                {
                    sp.Live = null;
                    sp.RespawnTick = NextRespawnTick(sp);    // refill shortly (rare bosses: long jittered delay)
                    _mobSpawn.Remove(mob.Id);
                    drops = RollDropsLocked(m, mob, sp.Def);  // adds to m.Items under the lock
                }
            }
        }
        if (drops is not null)
            foreach (var gi in drops) Broadcast(mapId, p => p.ShowGroundItem(gi));
        return true;
    }

    /// <summary>Roll a slain mob's loot, add each stack to the floor, and return them for broadcasting.
    /// Caller holds <c>_lock</c>.</summary>
    private List<GroundItem> RollDropsLocked(MapState m, Mob mob, MobDef def)
    {
        var drops = new List<GroundItem>();
        foreach (var roll in Content.RollDrops(def, Random.Shared))
        {
            GroundItem gi;
            if (roll.Gold)
            {
                // Mirrors Session.HandleDropGold's icon tiering (coins_1 / _2_99 / _100_999).
                ushort gfx = roll.Amount < 2 ? (ushort)22 : roll.Amount < 100 ? (ushort)73 : (ushort)72;
                gi = new GroundItem { Id = _nextItemId++, ItemId = -1, X = mob.X, Y = mob.Y, Amount = roll.Amount, Graphic = gfx };
            }
            else
            {
                gi = new GroundItem { Id = _nextItemId++, ItemId = roll.Item!.Id, X = mob.X, Y = mob.Y,
                    Amount = roll.Amount, Graphic = roll.Item.Icon, Dura = roll.Item.Durability };
            }
            m.Items.Add(gi);
            drops.Add(gi);
        }
        return drops;
    }

    // ---- ground items (dropped/thrown stacks lying on the floor) -------------------------------

    /// <summary>Drop <paramref name="gi"/> onto <paramref name="mapId"/> and draw it for everyone there.</summary>
    public void DropItem(ushort mapId, GroundItem gi)
    {
        lock (_lock) Map(mapId).Items.Add(gi);
        Broadcast(mapId, p => p.ShowGroundItem(gi));
    }

    /// <summary>Read-only snapshot of the floor items on a map (for drawing to a newcomer / on redraw).</summary>
    public GroundItem[] ItemsOn(ushort mapId)
    {
        lock (_lock)
            return _maps.TryGetValue(mapId, out var m) ? m.Items.ToArray() : Array.Empty<GroundItem>();
    }

    /// <summary>Remove the topmost (last-dropped) floor item on (x,y) under the lock — so two players
    /// grabbing the same tile can't both win — and despawn it for everyone. Null if the tile is empty.
    /// <para><paramref name="pickerId"/> is who is grabbing (0 = an anonymous/system grab, which ignores locks).
    /// Death-pile stacks reserved for someone else are SKIPPED rather than taken, and
    /// <paramref name="blocked"/> comes back true so the caller can say why nothing happened — RTK
    /// <c>canLoot</c>'s "That item does not belong to you." Set <paramref name="ownOnly"/> to take ONLY the
    /// picker's own still-locked pile and pass over everything else (RTK <c>isYours</c>, the F1 recovery).</para></summary>
    public GroundItem? PickUp(ushort mapId, int x, int y, uint pickerId = 0, bool ownOnly = false)
        => PickUp(mapId, x, y, pickerId, ownOnly, out _);

    /// <inheritdoc cref="PickUp(ushort, int, int, uint, bool)"/>
    public GroundItem? PickUp(ushort mapId, int x, int y, uint pickerId, bool ownOnly, out bool blocked)
    {
        GroundItem? gi = null;
        blocked = false;
        lock (_lock)
        {
            if (_maps.TryGetValue(mapId, out var m))
            {
                // last match = most recently dropped (drawn on top)
                for (int i = m.Items.Count - 1; i >= 0; i--)
                {
                    var it = m.Items[i];
                    if (it.X != x || it.Y != y) continue;
                    if (ownOnly) { if (!it.BelongsTo(pickerId)) continue; }
                    else if (pickerId != 0 && it.LockedAgainst(pickerId)) { blocked = true; continue; }
                    gi = it; m.Items.RemoveAt(i); break;
                }
            }
        }
        if (gi is not null) { blocked = false; Broadcast(mapId, p => p.DespawnEntity(gi.Id)); }
        return gi;
    }

    /// <summary>Despawn every mob on a map for all its players (the shared @kill).</summary>
    public int ClearMap(ushort mapId)
    {
        uint[] ids;
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m) || m.Mobs.Count == 0) return 0;
            ids = m.Mobs.Select(mo => mo.Id).ToArray();
            m.Mobs.Clear();
        }
        foreach (var id in ids) Broadcast(mapId, p => p.DespawnEntity(id));
        return ids.Length;
    }

    // ---- shared mob AI (one heartbeat drives every wandering mob on every map) -----------------

    private async Task TickLoop()
    {
        while (true)
        {
            try { await Task.Delay(TickMs); Tick(); }
            catch (Exception e) { Log.Info($"!! world tick error: {e.Message}"); }
        }
    }

    // One heartbeat: (1) refill dead spawn points that are due, (2) wander every live mob OR, if provoked,
    // chase and swing at its target instead (queuing any landed swings), (3) reconcile each player's
    // viewport (mobs that moved in/out of view, plus this tick's respawns, appear/disappear), (4) stream
    // moves/turns to observers, (4.5) apply this tick's queued mob swings. All map mutation happens under
    // the lock; no socket I/O does. Only maps with at least one player are processed — an empty map's
    // roster stays put.
    private void Tick()
    {
        _tick++;
        var moves = new List<(ushort map, uint id, ushort x, ushort y, byte dir)>();
        var turns = new List<(ushort map, uint id, byte dir)>();
        var hits = new List<(ushort map, Mob mob, Session target)>();
        // A pet's swing at another mob (mob-on-mob, the only place that happens) — deferred out of the lock
        // for the same reason as `hits`: applying it broadcasts and can award the owner exp.
        var mobHits = new List<(ushort map, Mob attacker, Mob victim)>();
        // Real damage from a triggered trap (instant hit) or a poison tick — both need Session-facing
        // broadcasts (damage number, death despawn, owner exp) that must run outside the lock, same as `hits`.
        var trapDamage = new List<(ushort map, Mob mob, int dmg, uint ownerId)>();
        var expiredPets = new List<(ushort map, Mob mob)>();
        var expiredMorphs = new List<Session>();
        var expiredStealth = new List<Session>();
        List<(ushort map, GroundItem gi)>? forage = null;
        bool timeChanged = false;
        List<(ushort map, byte weather)>? weatherChanges = null;
        lock (_lock)
        {
            // (1) respawns: refill any due spawn point on a map someone is watching.
            foreach (var (mapId, list) in _spawns)
            {
                if (!_maps.TryGetValue(mapId, out var pm) || pm.Players.Count == 0) continue;
                foreach (var sp in list)
                    if (sp.Live is null && sp.RespawnTick != 0 && _tick >= sp.RespawnTick)
                        Materialize(mapId, sp);
            }

            // (1.2) morph expiry (Session.CastMorph/RevertMorph): purely cosmetic per-player visual state
            // with no server-side entity of its own — the revert broadcast is socket I/O, so it's deferred
            // outside the lock same as trapDamage/expiredPets below.
            foreach (var (_, pm) in _maps)
                foreach (var p in pm.Players)
                    if (p.IsMorphExpired) expiredMorphs.Add(p);
            foreach (var (_, pm) in _maps)
                foreach (var p in pm.Players)
                    if (p.IsStealthExpired) expiredStealth.Add(p);   // faded (invisible-spell) look lapsed with no hit — revert

            // (1.3) bladestorm auto-expiry: an untriggered decoy despawns silently after its 21s lifetime —
            // traps have no ground graphic (same precedent as the hazard family), so this is a plain in-lock
            // removal, no broadcast/deferral needed.
            foreach (var (_, pm) in _maps)
                pm.Traps.RemoveAll(t => t.ExpiresAt != 0 && Environment.TickCount64 >= t.ExpiresAt);

            // (1.5) forage top-up: on a slow cadence, refill each forage box (chestnuts &c.) to its target count.
            if (_tick % ForageTicks == 0) forage = TopUpForageLocked();

            // (1.6) day/night clock (RTK change_time_char, ported 1:1 — see HourTicks doc): advance the
            // shared hour/year and flag every connected session for a fresh 0x20 broadcast this tick.
            if (_tick % HourTicks == 0)
            {
                _hour++;
                if (_hour >= 24)
                {
                    _hour = 0;
                    _day++;
                    if (_day >= 92)   // RTK: cur_day == 92 -> cur_day = 1, cur_season++
                    {
                        _day = 1;
                        _season++;
                        if (_season >= 5) { _season = 1; _year++; }   // RTK: cur_season == 5 -> cur_season = 1, cur_year++
                    }
                }
                timeChanged = true;
            }

            // (1.7) weather drift (see WeatherRollTicks doc — no real RTK scheduler exists to port): every
            // active map gets a low chance to shift to a new state on this slow cadence.
            if (_tick % WeatherRollTicks == 0)
            {
                weatherChanges = new List<(ushort, byte)>();
                foreach (var (mapId, pm) in _maps)
                {
                    if (pm.Players.Count == 0) continue;
                    if (Random.Shared.Next(100) >= WeatherChangePct) continue;
                    byte w = (byte)Random.Shared.Next(3);   // 0 clear / 1 WRAIN / 2 WSNOW
                    if (w == pm.Weather) continue;
                    pm.Weather = w;
                    weatherChanges.Add((mapId, w));
                }
            }

            // (2) wander: each mob acts only when its own MoveTime has elapsed (RTK MobMoveTime), and even
            // then usually just turns instead of stepping — mirroring RTK mob_ai_normal (checkmove: pick a
            // random side, only step when it matches the current facing, else 4-in-11 step straight ahead).
            // This paces a rabbit (3000ms) to a hop every few seconds, not every 600ms heartbeat.
            foreach (var (mapId, m) in _maps)
            {
                if (m.Mobs.Count == 0 || m.Players.Count == 0) continue;   // no observers -> don't bother
                var dims = Content.Maps.TryGetValue(mapId, out var mi) ? (mi.Xs, mi.Ys) : ((ushort)0, (ushort)0);
                var terrain = dims.Item1 > 0 ? MapData.For(mapId, dims.Item1, dims.Item2) : null;
                var occupied = m.Players.Select(p => (p.PlayerX, p.PlayerY)).ToHashSet();
                // Every living mob's tile — so a mob won't step onto another (kept current as they move below).
                var mobTiles = new HashSet<(int, int)>();
                foreach (var mo in m.Mobs) if (mo.Alive) mobTiles.Add((mo.X, mo.Y));

                foreach (var mob in m.Mobs)
                {
                    if (!mob.Alive) continue;

                    // Targeted-buff expiry (Session.CastTargetBuff, e.g. Valor/Harden Armor on a pet): revert each
                    // lapsed buff's stat delta off the mob's raw combat fields. Field-only, so it's safe in-lock.
                    if (mob.Buffs is { Count: > 0 })
                    {
                        long bnow = Environment.TickCount64;
                        for (int i = mob.Buffs.Count - 1; i >= 0; i--)
                            if (mob.Buffs[i].ExpiresAt <= bnow)
                            {
                                mob.AdjustBuffField(mob.Buffs[i].Stat, mob.Buffs[i].Amount, -1);
                                mob.Buffs.RemoveAt(i);
                            }
                    }

                    // Ownership expiry — two DIFFERENT endings, keyed on Mob.Summoned:
                    //   conjured (CotW pet, Giasomo bird)  -> plain despawn, no kill/loot/exp, same as riding a
                    //     mob away (RTK cotw_SpawnSetThreat's spawnTime). DespawnMob does socket I/O so it must
                    //     run outside this lock, hence the deferred list.
                    //   mind-controlled (Endear & kin)     -> the creature was always a real world mob, so it
                    //     just stops being yours: RTK endear's `uncast` is exactly `mob.owner = 0; mob.target = 0`.
                    //     Clearing TargetId means it forgets whoever it was fighting FOR you and re-acquires
                    //     normally next tick — including you.
                    if (mob.OwnerId != 0 && mob.PetExpiresAt != 0 && Environment.TickCount64 >= mob.PetExpiresAt)
                    {
                        if (mob.Summoned) { expiredPets.Add((mapId, mob)); continue; }
                        mob.OwnerId = 0; mob.TargetId = 0; mob.TargetMobId = 0; mob.PetExpiresAt = 0;
                        // Re-home it where it actually is. A creature that followed you across the map is
                        // now nowhere near the Home it spawned at, and the wander step below leashes on
                        // ABSOLUTE distance from Home — so without this it would fail every candidate step
                        // and stand frozen forever. (Doesn't arise in RTK, which has no follow behaviour.)
                        mob.HomeX = mob.X; mob.HomeY = mob.Y;
                    }

                    // Poison trap DOT (RTK poison_dart_trap.lua while_cast_1500): ticks every 1500ms regardless
                    // of freeze/wander state, and — per RTK — never fires a tick that would finish the kill.
                    if (mob.PoisonUntil > Environment.TickCount64 && Environment.TickCount64 >= mob.PoisonNextTick && mob.Hp > mob.PoisonTickDam)
                    {
                        mob.PoisonNextTick = Environment.TickCount64 + 1500;
                        trapDamage.Add((mapId, mob, mob.PoisonTickDam, mob.PoisonOwnerId));
                    }

                    if (mob.FrozenUntil > Environment.TickCount64) continue;   // paralyzed/asleep — hold still

                    // Blind (RTK's `target.blind = true`): a blinded mob can't SEE, so it drops whoever it was
                    // fighting and its aggro scan below is skipped — it falls through to plain wandering until
                    // the duration lapses. Unlike FrozenUntil it does NOT stop movement; blind is an aiming
                    // debuff, not a hold, which is why the two stack usefully.
                    if (mob.BlindUntil > Environment.TickCount64) mob.TargetId = 0;

                    // ---- PET AI: a mob with an OWNER (a Poet's Call of the Wild summon, or an Endear'd
                    // captive) does not behave like a wild one. Three rules, applied in order:
                    //   1. never fight your owner,
                    //   2. fight whatever is fighting your owner,
                    //   3. otherwise stay at their heel.
                    // Before this, Mob.OwnerId existed but drove NO behaviour at all, which is why both halves
                    // looked broken from the outside: a CotW pet (every cotw_* MobDef is MobBehavior 0) just
                    // wandered off on its spawn leash and never swung at anything, and Endear on an aggressive
                    // creature handed it to you for a fraction of a second before the unprovoked-aggro scan
                    // below re-acquired the nearest player — you — and it turned right back around.
                    //
                    // NOT ported from RTK, because RTK has none of it: its mob_find_target only ever scans
                    // BL_PC and has no owner check, and its cotw_controller (the threat-redirect half) is
                    // later-server and ships disabled. What RTK does establish is that a mob's damage is
                    // credited to `mob->owner` when that id is a player (clif.c) — that part is honoured by
                    // ApplyMobOnMobHit.
                    if (mob.OwnerId != 0)
                    {
                        if (mob.TargetId == mob.OwnerId) mob.TargetId = 0;   // rule 1, every tick
                        // Off a PK map a pet has no business fighting PEOPLE at all (RTK cotw: `blType ==
                        // BL_PC -> return`), so drop any player target it picked up — from a PvP map it was
                        // just led off, say. On a PK map it keeps them until they die or leave, exactly like
                        // any other mob, so a pet being beaten on can still fight back.
                        if (mob.TargetId != 0 && !Content.IsPvpMap(mapId)) mob.TargetId = 0;
                        var owner = m.Players.FirstOrDefault(p => p.PlayerId == mob.OwnerId && !p.IsDead);

                        if (owner is null)
                        {
                            mob.TargetMobId = 0;
                            // A CONJURED pet with no owner here vanishes — RTK mob_ai_cotw.move opens with
                            // exactly this (`owner == nil` or `owner.m ~= mob.m` -> `mob:vanish()`), and it
                            // also saves us a stranded summon wandering a map its poet left. A merely
                            // mind-controlled creature is a real world mob and stays put, wandering until its
                            // charm lapses (RTK routes those through mob_ai_normal, which has no vanish).
                            if (mob.Summoned) { expiredPets.Add((mapId, mob)); continue; }
                        }
                        else
                        {
                            // Rule 2a — PvP. On a PK map a pet also fights PEOPLE: whoever its owner is
                            // currently trading blows with (Session.PvpFoeId, set on both sides of a player
                            // spell exchange and expiring after 15s so nobody is chased across the map over a
                            // stale grudge). This is a deliberate departure — RTK's cotw AI refuses player
                            // targets outright (`if attacker.blType == BL_PC then return`), which is right for
                            // the open world and wrong for an arena — so it is scoped to maps already flagged
                            // MapPvP, the same gate that lets a player's own spell damage land at all.
                            // Setting TargetId and NOT continuing hands the pet to the ordinary player-chase
                            // branch below, which already knows how to close on a Session and swing at it.
                            if (Content.IsPvpMap(mapId) && owner.PvpFoeId != 0 && owner.PvpFoeId != mob.OwnerId
                                && m.Players.Any(p => p.PlayerId == owner.PvpFoeId && !p.IsDead))
                            {
                                mob.TargetId = owner.PvpFoeId;
                                mob.TargetMobId = 0;
                            }
                        }

                        if (owner is not null && mob.TargetId == 0)   // no person to fight — serve the owner
                        {
                            // Rule 2, re-evaluated every tick so the pet picks up a new attacker the moment
                            // its current one dies, leashes off, or loses interest in the owner. Only wild
                            // mobs qualify: never an NPC, never another player's pet, never a sibling of ours.
                            var foe = mob.TargetMobId == 0 ? null : m.Mobs.FirstOrDefault(o => o.Alive && o.Id == mob.TargetMobId);
                            if (foe is null || foe.TargetId != owner.PlayerId)
                                foe = m.Mobs.Where(o => o.Alive && !o.IsNpc && o.OwnerId == 0 && o.TargetId == owner.PlayerId)
                                             .OrderBy(o => Math.Max(Math.Abs(o.X - mob.X), Math.Abs(o.Y - mob.Y)))
                                             .FirstOrDefault();
                            mob.TargetMobId = foe?.Id ?? 0;

                            // A pet steps EVERY heartbeat rather than on its own MobMoveTime. That cadence is a
                            // wander timer (a panda's is 2s); pacing a follower by it would leave it hopelessly
                            // behind a walking player. One tile per 600ms tick is the most this heartbeat can
                            // express, so a pet still trails you while you're moving and closes up when you stop.
                            if (foe is not null)
                            {
                                int fdx = foe.X - mob.X, fdy = foe.Y - mob.Y;
                                if ((fdx == 0 && Math.Abs(fdy) == 1) || (fdy == 0 && Math.Abs(fdx) == 1))
                                {
                                    byte face = FaceDelta(fdx, fdy);
                                    if (face != mob.Dir) { mob.Dir = face; turns.Add((mapId, mob.Id, face)); }
                                    mob.AttackTimer += TickMs;
                                    if (mob.AttackTimer >= mob.AttackTime) { mob.AttackTimer = 0; mobHits.Add((mapId, mob, foe)); }
                                }
                                else StepMobToward(mapId, m, mob, foe.X, foe.Y, dims, terrain, occupied, mobTiles, moves, turns, trapDamage);
                                continue;
                            }

                            // Rule 3: heel. Stop once cardinally adjacent so the pet doesn't shove itself into
                            // the owner's tile or jitter around them; standing still is the correct idle pose.
                            if (Math.Abs(owner.PlayerX - mob.X) + Math.Abs(owner.PlayerY - mob.Y) > 1)
                                StepMobToward(mapId, m, mob, owner.PlayerX, owner.PlayerY, dims, terrain, occupied, mobTiles, moves, turns, trapDamage);
                            continue;
                        }
                    }

                    // ---- PREY AI (MobDef.Flees, data/game-data/MobFlees.csv): a rabbit or a blue rooster does
                    // not fight and does not stand there. It backs away at double its wander pace from anyone
                    // who gets within FleeRadius, and a swing (PanicMs) widens that radius and keeps it running
                    // after you back off. This runs BEFORE everything below because
                    // TryDamage sets TargetId on any landed hit — without this intercept, hitting a rabbit
                    // would drop it straight into the ordinary chase-and-swing branch and it would fight back.
                    // Clearing the target every tick is what "no attacking" actually means here: the attack
                    // branch is reached only via TargetId/TargetMobId, so a prey creature can never enter it.
                    // An OWNED prey creature (Endear'd) is exempt — it's a pet now, and the pet block above
                    // already returned for every case where it has an owner to serve.
                    if (mob.Flees && mob.OwnerId == 0)
                    {
                        mob.TargetId = 0; mob.TargetMobId = 0; mob.AttackTimer = 0;
                        bool panicking = mob.PanicUntil > Environment.TickCount64;
                        int notice = panicking ? FleeRadius * 2 : FleeRadius;
                        Session? scare = null;
                        int nearest = int.MaxValue;
                        foreach (var p in m.Players)
                        {
                            if (p.IsDead) continue;   // a ghost doesn't frighten anything
                            int d = Math.Max(Math.Abs(p.PlayerX - mob.X), Math.Abs(p.PlayerY - mob.Y));
                            if (d <= notice && d < nearest) { nearest = d; scare = p; }
                        }
                        if (scare is not null)
                        {
                            // Retreat pace: FleeSpeedup times the idle wander rate, whether it was startled by
                            // a swing or merely by someone walking up. Floored at one heartbeat because the
                            // tick is the hard ceiling on how often anything can step (see FleeSpeedup).
                            int pace = Math.Max(TickMs, mob.MoveTime / FleeSpeedup);
                            mob.MoveTimer += TickMs;
                            if (mob.MoveTimer < pace) continue;
                            mob.MoveTimer -= pace;
                            StepMobAway(mapId, m, mob, scare.PlayerX, scare.PlayerY,
                                        dims, terrain, occupied, mobTiles, moves, turns, trapDamage);
                            continue;
                        }
                        // Nobody near enough to spook it: fall through to the ordinary wander below.
                    }

                    // Unprovoked aggro (RTK mob.c mob_find_target, gated on MobBehavior==1 "type": engine-level,
                    // separate from and runs before mob_ai_normal.lua): an aggressive mob with no target yet
                    // locks onto the nearest living player within AggroRadius, same as if it had just been hit —
                    // the chase/attack branch right below then takes over on this same tick.
                    // OWNED mobs are excluded outright: a charmed creature must not re-acquire the poet who
                    // just charmed it (the pet block above already returned for every case where it has an
                    // owner it can serve, so this only skips the "owner isn't here" leftovers).
                    if (mob.TargetId == 0 && mob.Aggressive && mob.OwnerId == 0 && mob.BlindUntil <= Environment.TickCount64)
                    {
                        var victim = m.Players.FirstOrDefault(p => !p.IsDead
                            && Math.Max(Math.Abs(p.PlayerX - mob.X), Math.Abs(p.PlayerY - mob.Y)) <= AggroRadius);
                        if (victim is not null) mob.TargetId = victim.PlayerId;
                    }

                    // Combat AI (RTK mob_ai_normal: on_attacked sets the target; move/attack chase + swing at
                    // it): a provoked mob (World.TryDamage set TargetId) abandons wandering to path toward and
                    // melee its attacker instead, until the target dies/leaves/logs off or strays past
                    // ChaseLeash tiles from the mob's home — then it falls back to normal wandering below.
                    if (mob.TargetId != 0)
                    {
                        var target = m.Players.FirstOrDefault(p => p.PlayerId == mob.TargetId);
                        // An OWNED creature has no leash: it belongs to a player, not to a spawn point, so
                        // tethering it to the tile it was summoned on would make it quit mid-fight.
                        bool inRange = target is not null && !target.IsDead
                                       && (mob.OwnerId != 0
                                           || Math.Max(Math.Abs(target.PlayerX - mob.HomeX), Math.Abs(target.PlayerY - mob.HomeY)) <= ChaseLeash);
                        if (!inRange) { mob.TargetId = 0; mob.AttackTimer = 0; mob.DetourDir = NoDetour; mob.DetourLeft = 0; }
                        else
                        {
                            int tdx = target!.PlayerX - mob.X, tdy = target.PlayerY - mob.Y;
                            // Cardinal adjacency ONLY (matches the player's own melee, which only ever checks
                            // its single FrontTile — a diagonal target is neither attackable by the player nor,
                            // now, by a mob; RTK has no 8-way reach either). A diagonal target falls through to
                            // the chase step below, which moves on a single axis and closes to cardinal in ~1 tick.
                            bool adjacent = (tdx == 0 && Math.Abs(tdy) == 1) || (tdy == 0 && Math.Abs(tdx) == 1);
                            if (adjacent)
                            {
                                byte face = FaceDelta(tdx, tdy);
                                if (face != mob.Dir) { mob.Dir = face; turns.Add((mapId, mob.Id, face)); }
                                mob.AttackTimer += TickMs;
                                if (mob.AttackTimer >= mob.AttackTime) { mob.AttackTimer = 0; hits.Add((mapId, mob, target)); }
                                continue;   // adjacent: swing instead of stepping
                            }

                            mob.MoveTimer += TickMs;
                            if (mob.MoveTimer < mob.MoveTime) continue;   // not this mob's turn yet
                            mob.MoveTimer -= mob.MoveTime;

                            // Step toward the target — the direction(s) that close the gap first, then a
                            // sideways shuffle. See StepMobToward: this used to be an inline greedy step that
                            // gave up when blocked, i.e. "mob stands on one tile facing you through a wall".
                            StepMobToward(mapId, m, mob, target.PlayerX, target.PlayerY,
                                          dims, terrain, occupied, mobTiles, moves, turns, trapDamage,
                                          out bool towardBlocked);

                            // Can't get at them at all? Look for somebody it CAN reach. This is RTK's own
                            // FindCoords fallback (`tList = mob:getObjectsInArea(BL_PC)` then a random pick,
                            // skipping GMs) — usually a no-op, since there's usually only one player nearby.
                            // Gated on Aggressive: a creature that only fights because you provoked it should
                            // keep pacing after YOU, not go find someone else. Note this can only ever hand a
                            // stuck mob a NEW victim — landing a hit re-points it at whoever hit it
                            // (World.TryDamage), so zapping something always drags its aggro back to you,
                            // wall or no wall.
                            if (towardBlocked && mob.Aggressive)
                            {
                                var reachable = m.Players.Where(p => !p.IsDead && p.PlayerId != mob.TargetId
                                        && Math.Max(Math.Abs(p.PlayerX - mob.X), Math.Abs(p.PlayerY - mob.Y)) <= AggroRadius)
                                    .ToList();
                                if (reachable.Count > 0)
                                {
                                    mob.TargetId = reachable[Random.Shared.Next(reachable.Count)].PlayerId;
                                    mob.AttackTimer = 0; mob.DetourDir = NoDetour; mob.DetourLeft = 0;
                                }
                            }
                            continue;
                        }
                    }

                    // Retaliation against a PET. ApplyMobOnMobHit points a mob at whatever pet just hit it,
                    // but only when it wasn't already busy with a player — so this is purely the "a pet can't
                    // beat on something with total impunity" case, not a general mob-vs-mob war. Same leash as
                    // the player chase: stray too far from home and it gives up and goes back to wandering.
                    if (mob.TargetId == 0 && mob.TargetMobId != 0)
                    {
                        var foe = m.Mobs.FirstOrDefault(o => o.Alive && o.Id == mob.TargetMobId);
                        if (foe is null || Math.Max(Math.Abs(foe.X - mob.HomeX), Math.Abs(foe.Y - mob.HomeY)) > ChaseLeash)
                        { mob.TargetMobId = 0; mob.AttackTimer = 0; mob.DetourDir = NoDetour; mob.DetourLeft = 0; }
                        else
                        {
                            int rdx = foe.X - mob.X, rdy = foe.Y - mob.Y;
                            if ((rdx == 0 && Math.Abs(rdy) == 1) || (rdy == 0 && Math.Abs(rdx) == 1))
                            {
                                byte face = FaceDelta(rdx, rdy);
                                if (face != mob.Dir) { mob.Dir = face; turns.Add((mapId, mob.Id, face)); }
                                mob.AttackTimer += TickMs;
                                if (mob.AttackTimer >= mob.AttackTime) { mob.AttackTimer = 0; mobHits.Add((mapId, mob, foe)); }
                            }
                            else
                            {
                                mob.MoveTimer += TickMs;
                                if (mob.MoveTimer >= mob.MoveTime)
                                {
                                    mob.MoveTimer -= mob.MoveTime;
                                    StepMobToward(mapId, m, mob, foe.X, foe.Y, dims, terrain, occupied, mobTiles, moves, turns, trapDamage);
                                }
                            }
                            continue;
                        }
                    }

                    if (!mob.Wander) continue;
                    mob.MoveTimer += TickMs;
                    if (mob.MoveTimer < mob.MoveTime) continue;   // not this mob's turn yet
                    mob.MoveTimer -= mob.MoveTime;                // carry the remainder (steady cadence)

                    byte oldside = mob.Dir;
                    byte stepDir;
                    if (Random.Shared.Next(0, 11) >= 4)          // ~64%: reconsider facing
                    {
                        byte side = (byte)Random.Shared.Next(4);
                        mob.Dir = side;
                        if (side != oldside) { turns.Add((mapId, mob.Id, side)); continue; }  // just turned
                        stepDir = side;                          // faced the same way -> take the step
                    }
                    else stepDir = mob.Dir;                       // ~36%: step straight ahead

                    int nx = mob.X, ny = mob.Y;
                    switch (stepDir) { case 0: ny--; break; case 1: nx++; break; case 2: ny++; break; case 3: nx--; break; }

                    bool ok = nx >= 0 && ny >= 0
                              && (dims.Item1 == 0 || (nx < dims.Item1 && ny < dims.Item2))
                              && Math.Abs(nx - mob.HomeX) <= mob.Leash
                              && Math.Abs(ny - mob.HomeY) <= mob.Leash                             // leash to spawn
                              && !occupied.Contains(((ushort)nx, (ushort)ny))                     // not onto a player
                              && !mobTiles.Contains((nx, ny))                                      // not onto another mob
                              && (terrain is null || !terrain.BlockedMove(nx, ny, stepDir));       // pass flag OR SObj object-wall
                    if (!ok) continue;   // blocked/leashed: hold position (already facing stepDir)

                    ushort ox = mob.X, oy = mob.Y;                   // SOURCE tile (see the move broadcast below)
                    mobTiles.Remove((mob.X, mob.Y));                 // vacate the old tile
                    mob.X = (ushort)nx; mob.Y = (ushort)ny;
                    mobTiles.Add((nx, ny));                          // occupy the new one
                    // Broadcast the SOURCE tile, not the destination: the 4.95 client's 0x0C walk always ends
                    // one tile PAST the packet tile in the walk direction (forward-slide overshoot, proven by
                    // live trace), and for a single-stepping mob there's no 0x04 commit to correct it. Sending
                    // source makes client_final = source + forward(dir) = the real destination.
                    moves.Add((mapId, mob.Id, ox, oy, stepDir));
                    var wanderTrap = m.Traps.FirstOrDefault(t => t.X == nx && t.Y == ny);
                    if (wanderTrap is not null) { m.Traps.Remove(wanderTrap); TriggerTrapLocked(mapId, mob, wanderTrap, trapDamage); }
                }
            }
        }

        // (3) Reconcile viewports FIRST, using the mobs' NEW positions: spawn any that stepped into view,
        // despawn any that stepped out. Doing this before the moves means a mob that just left the screen is
        // despawned (0x0E) rather than sent an off-screen 0x0C the client would cull — the desync that made
        // mobs vanish for good.
        ReconcileViews();

        // (4) Now stream moves/turns, but only to players who still have that mob in view (MoveMob/SideMob
        // are no-ops otherwise) — bounding on-wire traffic to on-screen mobs even on a 400-spawn map.
        foreach (var mv in moves)
            Broadcast(mv.map, p => p.MoveMob(mv.id, mv.x, mv.y, mv.dir));
        foreach (var tn in turns)
            Broadcast(tn.map, p => p.SideMob(tn.id, tn.dir));

        // Newly-foraged ground items (chestnuts &c.): draw them for everyone on that map (0x16).
        if (forage is not null)
            foreach (var (map, gi) in forage)
                Broadcast(map, p => p.ShowGroundItem(gi));

        // (4.5) Resolve this tick's mob swings (queued above while still under the lock) — applying player
        // damage runs Session-side (HUD update + broadcast + possible death), so it happens out here like
        // every other socket-touching step.
        foreach (var h in hits)
        {
            // 009.wav on the SWING itself, hit or miss — this is the point where the mob commits to the attack.
            // The landed-hit sound (001.wav) is layered on separately by ApplyMobHit. Mobs play no swing ACTION
            // (0x1A) today, so this sound is the only cue a bystander gets that a mob took a swing at someone.
            Broadcast(h.map, p => p.SoundAt(Session.MobSwingSfx, h.mob.Id));
            int dmg = MobSwingDamage(h.mob.MinDam, h.mob.MaxDam);
            Try(() => h.target.ApplyMobHit(h.mob, dmg));
        }

        // Pet swings queued above: same damage roll as any other mob swing, but landing on a mob.
        foreach (var ph in mobHits)
            Try(() => ApplyMobOnMobHit(ph.map, ph.attacker, ph.victim, MobSwingDamage(ph.attacker.MinDam, ph.attacker.MaxDam)));

        // Trap hits + poison ticks queued above (same reasoning as the mob-swing pass: Session-facing
        // broadcasts/exp can't run under the lock).
        foreach (var td in trapDamage)
            Try(() => ApplyTrapDamage(td.map, td.mob, td.dmg, td.ownerId));

        // Expired pets queued above — plain despawn, no kill/loot.
        foreach (var ep in expiredPets)
            Try(() => DespawnMob(ep.map, ep.mob));

        // Expired morphs queued above — revert the peer-visible disguise back to our real human look.
        foreach (var mp in expiredMorphs)
            Try(() => mp.RevertMorph());

        // Expired stealth queued above — restore the normal look once the invisible-spell timer lapses w/o a hit.
        foreach (var sp in expiredStealth)
            Try(() => sp.RevertStealth());

        // (5) natural HP/MP regen for EVERY connected player (not gated on mobs/viewport, unlike the
        // steps above). Each session tracks its own 25s accumulator and only emits a status packet on a
        // real change — see Session.RegenTick. Snapshot the player list under the lock, tick outside it.
        Session[] players2;
        lock (_lock) players2 = _maps.Values.SelectMany(m => m.Players).ToArray();
        foreach (var p in players2) Try(() => p.RegenTick(TickMs));

        // (6) day/night + weather broadcasts queued above — every connected session hears the new hour
        // (RTK broadcasts clif_sendtime server-wide, not per-map), each affected map hears its own weather.
        if (timeChanged)
        {
            var (h, y) = Time;
            foreach (var p in players2) Try(() => p.SendTime(h, y));
        }
        if (weatherChanges is not null)
            foreach (var (map, w) in weatherChanges)
                Broadcast(map, p => p.SendWeather(w));
    }

    // Snapshot each populated map's (players, mobs) under the lock, then reconcile every player's viewport
    // outside it. Cheap: a few hundred in-view checks per player per tick, no allocation on the hot path
    // beyond the snapshot arrays.
    private void ReconcileViews()
    {
        (Session[] players, Mob[] mobs)[] snapshot;
        lock (_lock)
        {
            snapshot = _maps.Values
                .Where(m => m.Players.Count > 0 && m.Mobs.Count > 0)
                .Select(m => (m.Players.ToArray(), m.Mobs.ToArray()))
                .ToArray();
        }
        foreach (var (players, mobs) in snapshot)
            foreach (var p in players) Try(() => p.SyncMobs(mobs));
    }

    private static void Try(Action a)
    {
        try { a(); } catch { /* dead/closing socket — its own read-loop will clean it up */ }
    }

    /// <summary>A mob's raw melee swing (RTK <c>swingDamage.lua</c> <c>_getMobSwingDamage</c>): three
    /// independent uniform draws over the range split into thirds, summed and floored, +1. This is NOT a
    /// flat roll across [MinDam,MaxDam] — three thirded draws concentrate the result near the midpoint
    /// (Irwin-Hall-ish), matching RTK's actual distribution. The target's armor is applied separately, by
    /// the target itself (<see cref="Session.ApplyMobHit"/>), since AC/gear/buffs are session-local state.</summary>
    private static int MobSwingDamage(int minDam, int maxDam)
    {
        double lo = minDam / 3.0, hi = maxDam / 3.0;
        double sum = 0;
        for (int i = 0; i < 3; i++) sum += lo + Random.Shared.NextDouble() * (hi - lo);
        return 1 + (int)Math.Floor(sum);
    }
}
