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
    /// <summary>When each creature was last killed on each map, in unix seconds — RTK's per-map registry
    /// (<c>setMapRegistry(mob.m, "lastDeathCitelam", os.time())</c>, stamped by the boss's own after_death and
    /// read by the trap spawner before it is allowed to roll for another). Only creatures whose
    /// MobSpawnRules row asks for a cooldown are recorded, so this stays a handful of entries rather than one
    /// per kill. Deliberately NOT persisted: a restart is already a world reset for spawns.</summary>
    private readonly Dictionary<(ushort Map, string Key), long> _lastDeath = new();
    private long _tick;                                                  // heartbeat counter (600ms each)

    private const int TickMs = 600;         // world heartbeat period; also the unit MoveTimer accumulates in

    /// <summary>Poison/venom damage cadence, RTK's <c>while_cast_1500</c>. Shared by the mob DoT, the Rogue
    /// poison trap and the player-side venom, so the rate NexusAtlas quotes ("1000 damage a second") converts
    /// against one number in one place — see <see cref="Session.ReceivePoison"/>.</summary>
    public const int PoisonTickMs = 1500;
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
    // Mythic boss animations RTK hardcodes rather than carrying per-boss: Last Stand flashes 11
    // (Spells/last_stand.lua `sendAnimation(11)`), a curse shrug flashes 10 and plays no sound
    // (mob_ai_mythic.move). The heal animation/sound pair IS per-boss and lives in MobBosses.csv.
    private const int LastStandAnim  = 11;
    private const int CurseShrugAnim = 10;
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
    // Forage spawn boxes are data-driven (game-data/ForageAreas.csv -> Content.ForageAreas); hot-reloads
    // via @reload. See TopUpForageLocked.
    private const int ForageTicks = 30;   // top up ~every 18s (30 * 600ms), like RTK's periodic itemspawner

    // ---- world calendar (opcode 0x20) ---------------------------------------------------------
    // The calendar itself lives in Shared.GameCalendar — a pure function of wall-clock time since a fixed
    // epoch, with RTK's own cadence constants and the reasoning for deriving rather than counting. It is in
    // Shared because the LOGIN server, a separate process with no World, stamps a new character's "Born in
    // ..." legend with the same date this server is showing.
    //
    // What World adds is the broadcast: RTK's change_time_char (map.c:1661) pushes clif_sendtime to every
    // connected session on each in-game hour, so we cache the calendar and watch for the hour to roll over.
    // Only hour+year go on the wire (see Session.SendTime); day/season are tracked because the year cadence
    // is defined in terms of them, and "@time" reports the season.
    private int _hour, _day = 1, _season = 1, _year = 1;
    private long _gameHour = -1;          // whole in-game hours since the epoch; -1 = not yet synced
    public (byte hour, byte year) Time => ((byte)_hour, (byte)_year);
    public string SeasonName => GameCalendar.SeasonName(_season);

    /// <summary>Re-read the calendar; true when the in-game hour changed, i.e. it is time to broadcast
    /// <c>0x20</c>.</summary>
    private bool SyncClock()
    {
        long gameHour = GameCalendar.HoursNow();
        if (gameHour == _gameHour) return false;
        _gameHour = gameHour;
        (_hour, _day, _season, _year) = GameCalendar.At(gameHour);
        return true;
    }

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

    // Effects raised from inside the lock (a boss shrugging off a killing blow, say) and flushed by the next
    // Tick — TryDamage can't broadcast where it stands, and its callers only know how to draw the damage.
    private readonly List<(ushort map, uint id, int anim, int sound)> _deferredFx = new();

    // Lua mob hooks raised from inside the lock, run by the next Tick OUTSIDE it. This queue is the whole
    // reason MobScript is safe: a hook is free to speak, heal, vanish or touch a player's quest registry,
    // all of which re-enter the world — doing that while still holding _lock would deadlock it.
    private readonly List<(string key, string hook, ushort map, Mob mob, Session? actor)> _hooks = new();

    /// <summary>Queue a Lua AI hook for the creature, if it defines that hook. Cheap (one hash lookup) for
    /// the overwhelming majority of mobs, which define none. Safe to call under <c>_lock</c>.</summary>
    private void QueueHook(string hook, ushort map, Mob mob, Session? actor)
    {
        if (MobScript.Has(mob.Key, hook)) _hooks.Add((mob.Key, hook, map, mob, actor));
    }

    /// <summary>Point a creature at whoever has earned it (RTK <c>threat.calcHighestThreat</c>). Two rules,
    /// and the order is the point:
    /// <list type="number">
    /// <item><b>Cornered.</b> If the mob is boxed in by players and the one it is fighting isn't within
    /// reach, it turns on the highest-threat player it CAN reach. This is what stops a mob standing in a
    /// crowd swinging uselessly at someone behind a wall.</item>
    /// <item><b>Otherwise</b> it fights whoever has hurt it most, anywhere in sight.</item>
    /// </list>
    /// Threat only ever accrues from damage, so this can never make a mob attack an innocent bystander —
    /// the worst it does is move aggro between people already in the fight, which is the intent (a group
    /// CAN peel a mob off whoever pulled it, by out-damaging them).
    /// <para>Callers hold <c>_lock</c>. Players who have left the map simply aren't considered; their threat
    /// stays banked in case they come back, exactly as RTK's per-mob table does.</para></summary>
    private static void RetargetByThreat(MapState m, Mob mob)
    {
        bool Adjacent(Session p) =>
            (p.PlayerX == mob.X && Math.Abs(p.PlayerY - mob.Y) == 1) ||
            (p.PlayerY == mob.Y && Math.Abs(p.PlayerX - mob.X) == 1);

        // Rule 1's precondition: someone is in arm's reach and the current target is not.
        bool cornered = false;
        if (mob.TargetId != 0)
        {
            var current = m.Players.FirstOrDefault(p => p.PlayerId == mob.TargetId);
            if (current is null || !Adjacent(current))
                cornered = m.Players.Any(p => !p.IsDead && Adjacent(p));
        }

        long now = Environment.TickCount64;
        Session? best = null;
        long bestThreat = 0;
        foreach (var p in m.Players)
        {
            if (p.IsDead) continue;
            // RTK's non-cornered scan is `mob:getObjectsInArea(BL_PC)` — what the creature can see, not the
            // whole map. Without the bound a mob would swap onto someone who hurt it once and then walked to
            // the far side of the level, and chase a player it has no way of knowing is there.
            if (Math.Max(Math.Abs(p.PlayerX - mob.X), Math.Abs(p.PlayerY - mob.Y)) > AggroRadius) continue;
            if (cornered && !Adjacent(p)) continue;
            if (mob.HasForgotten(p.PlayerId, now)) continue;   // Amnesia: this one isn't here as far as it knows
            long t = mob.ThreatOf(p.PlayerId);
            if (t > bestThreat) { bestThreat = t; best = p; }
        }

        if (best is null || best.PlayerId == mob.TargetId) return;
        mob.TargetId = best.PlayerId;
        mob.TargetMobId = 0;
        mob.DetourDir = NoDetour;
        mob.DetourLeft = 0;
    }

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

    // The NpcDef each placed NPC was built from, so @reload can tell an edited row from an unchanged one
    // (see ReconcileNpcToggles). Guarded by _lock, same as the maps it mirrors.
    private readonly Dictionary<int, NpcDef> _npcPlaced = new();
    private uint _nextItemId = 500_000;

    /// <summary>The scheduled-restart clock (@restart, or the run/restart_at file a deploy writes). Kept on
    /// the World because a restart warning is a server-wide broadcast and AllPlayers lives here.</summary>
    public RestartSchedule Restarts { get; }

    public World()
    {
        PopulateSpawns();                 // build the persistent roster from Content.Spawns (needs Content.Load first)
        PopulateNpcs();                   // place the stationary NPCs (Content.Npcs) as non-fighting mobs
        SyncClock();                      // derive the in-game calendar from the fixed real-world epoch
        Log.Info($"=== clock: Yuri {_year}, {SeasonName}, day {_day}, hour {_hour}:00");
        Restarts = new RestartSchedule(this);

        // DEDICATED THREADS, not Task.Run. Both of these used to be thread-pool work items, which put the
        // world heartbeat behind every other pool item in the process: session read-loop continuations, the
        // synchronous SQLite saves below, Lua, and any stray blocking call. When the pool ran out of threads
        // the runtime injected replacements at only ~1-2 per second, and the tick simply did not run in the
        // meantime — a multi-second, self-recovering freeze of the entire world with nothing in the log to
        // show for it. A dedicated thread cannot be starved by pool pressure.
        new Thread(TickLoop)     { IsBackground = true, Name = "world-tick" }.Start();
        new Thread(AutoSaveLoop) { IsBackground = true, Name = "world-autosave" }.Start();

        // Pool headroom + the pool-latency and client-silence probes. Started here because this is the
        // first point where a World exists for the silence scanner to walk.
        Watchdog.RaiseMinThreads();
        Watchdog.Start(this);

        _ = Task.Run(Restarts.Loop);      // restart-warning ladder + the deploy's file trigger (1s cadence, not latency-critical)
        _ = Task.Run(() => StatusFile.Loop(this));   // run/status.json for the launcher's "N online" pill
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
        // Don't stack on a mob spawn sharing the tile, but DO stand where NPCs.csv says, wall or not.
        var (nx, ny) = FreeSpawnTile(n.Map, n.X, n.Y, avoidSolid: false);
        _npcPlaced[n.Id] = n;   // the def this instance was built from — see ReconcileNpcToggles
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
            _npcPlaced.Remove(npcId);
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
    /// <c>NPCs.csv</c>): re-sync stationary-NPC placement against the just-reloaded defs — spawns any NPC newly
    /// enabled, despawns any newly disabled, and re-places any whose def CHANGED. That last case used to be
    /// missed entirely: <see cref="EnableNpc"/> returns false as soon as the NPC is placed at all, so an edited
    /// tile (or look, or colour) sat in the CSV doing nothing until the next full restart, and the world quietly
    /// disagreed with the file. <see cref="NpcDef"/> is a record, so one structural compare covers every column.
    /// Returns how many NPCs' placement changed.</summary>
    public int ReconcileNpcToggles()
    {
        int changed = 0;
        foreach (var n in Content.Npcs)
        {
            if (!n.Enabled) { if (DisableNpc(n.Id) > 0) changed++; continue; }

            bool moved;
            lock (_lock) moved = _npcPlaced.TryGetValue(n.Id, out var prev) && !prev.Equals(n);
            if (moved) DisableNpc(n.Id);          // drop the stale instance, then fall through and re-place it
            if (EnableNpc(n.Id)) changed++;
        }
        return changed;
    }

    /// <summary>Create the live mob for a spawn point and register it. Caller holds <c>_lock</c>.</summary>
    private void Materialize(ushort mapId, Spawn sp)
    {
        var d = sp.Def;
        Content.MobSpawnRules.TryGetValue(d.Key, out var rule);

        // Population cap (RTK strange_thing's on_spawn, which counts its own kind across two maps and
        // vanishes if one is already out there). Checked before anything is built: the spawn point simply
        // doesn't fire, and will try again on its next refill.
        if (rule is { MaxAlive: > 0 })
        {
            int alive = 0;
            foreach (var capMap in rule.CapMaps.Length > 0 ? rule.CapMaps : new[] { mapId })
                if (_maps.TryGetValue(capMap, out var cm))
                    foreach (var other in cm.Mobs)
                        if (other.Alive && other.Key == d.Key) alive++;
            if (alive >= rule.MaxAlive) { sp.RespawnTick = NextRespawnTick(sp); return; }
        }

        // Death cooldown, then the roll — RTK's trap spawner asks all three questions in this order before it
        // will put Citelam or Maletic on the ground:
        //     if os.time() >= lastDeath + 1800 and bossAlive == 0 then
        //         local chance = math.random(1, 10); if chance == 1 then ... spawn the boss
        // Failing either just means the point tries again on its next refill, which is why the boss reads as
        // a find: with a 1-in-10 roll on a 30-minute point you meet it every few hours, not every lap.
        //
        // RTK asks on a trap TILE being stepped on; we ask on the spawn point's own refill, because this
        // server has no trap tiles (the whole ambush system is approximated by AreaSpawnsTrap.csv). The
        // gating — one at a time, never within half an hour of its last death, and rarely even then — is the
        // part that decides how often you actually see one, and that is reproduced exactly.
        if (rule is { DeathCooldownSec: > 0 }
            && _lastDeath.TryGetValue((mapId, d.Key), out var slain)
            && DateTimeOffset.UtcNow.ToUnixTimeSeconds() < slain + rule.DeathCooldownSec)
        { sp.RespawnTick = NextRespawnTick(sp); return; }

        if (rule is { SpawnChance: > 1 } && Random.Shared.Next(rule.SpawnChance) != 0)
        { sp.RespawnTick = NextRespawnTick(sp); return; }

        // Boss placement (RTK's `on_spawn = function(mob) mob:warp(map, x, y) end`, usually a random pick
        // among the boss's rooms). The spawn POINT stays where the table put it — respawn bookkeeping is
        // keyed to it — but the creature is built in the room it belongs in. A room on an unrenderable map
        // is ignored rather than stranding the boss nowhere.
        if (rule is { Rooms.Length: > 0 })
        {
            var room = rule.Rooms[Random.Shared.Next(rule.Rooms.Length)];
            if (Content.TryMap(room.Map, out _)) { mapId = room.Map; sp.X = room.X; sp.Y = room.Y; sp.Placed = true; }
        }

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
            // Wander is the default; MobStationary.csv opts a creature out (a penned captive whose RTK AI
            // script only ever turns on the spot — see Content.LoadMobStationary).
            Dir = 2, HomeX = sp.X, HomeY = sp.Y, Wander = !d.Stationary, Leash = WanderRadius,
            MoveTime = d.MoveTime, MoveTimer = Random.Shared.Next(d.MoveTime),   // stagger so they don't all step at once
        };

        // Spawn HP jitter (RTK AI/mob_on_spawn.lua — the DEFAULT on_spawn every creature without its own
        // gets): max HP moves by up to +/-(minDam + maxDam) * 2, so two of the same creature are never
        // quite the same fight. Floored at 1 — RTK's own version can drive a small, hard-hitting mob to
        // zero HP, which would spawn it already dead.
        if (Content.MobHpJitter)
        {
            int swing = Math.Max(1, (d.MinDam + d.MaxDam) * 2);
            int delta = Random.Shared.Next(1, swing + 1) * (Random.Shared.Next(2) == 0 ? 1 : -1);
            mob.MaxHp = Math.Max(1, mob.MaxHp + delta);
            mob.Hp = mob.MaxHp;
        }
        Map(mapId).Mobs.Add(mob);
        QueueHook(MobScript.OnSpawn, mapId, mob, null);
        _mobSpawn[mob.Id] = sp;
        sp.Live = mob;
        sp.RespawnTick = 0;
    }

    /// <summary>The spawn tile if it's open, else the nearest tile (within 2) that's in-bounds, not already
    /// occupied by a live mob, and — for a real creature — not solid, so two spawns on one tile (or a respawn
    /// onto a wanderer) don't stack. Falls back to the spawn tile if everything nearby is taken.
    ///
    /// <paramref name="avoidSolid"/> is false for NPCs: standing on solid ground is NORMAL for them and has to
    /// be honoured, not corrected. RTK's own authored placements do it (Mignok 4716(4,9) and Tominaru 4716(13,8)
    /// are both wall tiles), it's how an NPC stands behind a counter or on a shrine block, and nothing about an
    /// NPC needs walkable ground: it renders through the same 0x07 creature path as any mob and
    /// <see cref="Session.HandleClickInfo"/> opens its dialog by entity id with no adjacency check. Bumping them
    /// silently moved every such NPC a few tiles off its authored spot. Caller holds <c>_lock</c>.</summary>
    private (ushort x, ushort y) FreeSpawnTile(ushort mapId, ushort x, ushort y, bool avoidSolid = true)
    {
        var m = Map(mapId);
        var dims = Content.Maps.TryGetValue(mapId, out var mi) ? (mi.Xs, mi.Ys) : ((ushort)0, (ushort)0);
        var terrain = dims.Item1 > 0 ? MapData.For(mapId, dims.Item1, dims.Item2) : null;

        bool Free(int tx, int ty)
        {
            if (tx < 0 || ty < 0 || (dims.Item1 > 0 && (tx >= dims.Item1 || ty >= dims.Item2))) return false;
            if (avoidSolid && terrain is not null && terrain.Solid(tx, ty)) return false;
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
            PlayerById(ownerId)?.AwardKillExp(reward, mapId, mob.X, mob.Y);
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
            // …but NOT for a conjured victim. Now that a poet's pets will turn on a sibling he has attacked,
            // paying exp here would be the same summon-and-kill loop Session.ResolveSwing refuses, just
            // routed through a second pet. Same rule, same reason.
            if (!victim.Summoned)
            {
                uint reward = (uint)(victim.Exp > 0 ? victim.Exp : victim.MaxHp);
                PlayerById(attacker.OwnerId)?.AwardKillExp(reward, mapId, victim.X, victim.Y);
            }
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
        // BEFORE the lock: first entry to a map runs EnsureMaterialized, whose spawn placement reads the
        // terrain (FreeSpawnTile -> MapData.For) — a disk read, a full cell decode and a SQLite query. Held
        // under _lock that froze every player on every OTHER map too, for as long as the load took. Warming
        // the cache out here makes the locked section a pure in-memory hit. See MapData.Prewarm.
        MapData.Prewarm(mapId);

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

    /// <summary>Apply a hostile categorised status to a mob (RTK <c>checkIfCast</c> + <c>setDuration</c>): the
    /// exclusivity slot, whichever AI field the status actually drives, and — for the ones RTK re-draws in
    /// <c>while_cast</c> — the repeating over-head animation.
    /// <para>Returns <b>false</b> if a status of <paramref name="category"/> is already running, which is the
    /// whole point: an offensive hold cannot be stacked or refreshed on top of itself, so the victim gets the
    /// hold's full window back before it can be re-applied.</para>
    /// <paramref name="hold"/> freezes movement (paralyze/sleep/slow), <paramref name="blind"/> takes its
    /// sight. Both false = a pure stat curse, which only occupies the slot.</summary>
    public bool ApplyMobStatus(Mob mob, string category, int durMs, bool hold, bool blind,
                               int fxAnim = 0, int fxSound = 0, int fxEveryMs = 0, string spellKey = "")
    {
        if (durMs <= 0) return false;
        lock (_lock)
        {
            long now = Environment.TickCount64;
            if (mob.HasStatus(category, now)) return false;          // RTK checkIfCast — no stacking, no refresh
            long until = now + durMs;
            mob.SetStatus(category, until, spellKey);
            // Take the LATER of any running hold and this one, so a short paralyze can't cut a long sleep
            // short — the two are different categories and are allowed to overlap.
            if (hold)  mob.FrozenUntil = Math.Max(mob.FrozenUntil, until);
            if (blind) { mob.BlindUntil = Math.Max(mob.BlindUntil, until); mob.TargetId = 0; }
            if (fxAnim > 0 && fxEveryMs > 0) mob.SetFxRepeat(fxAnim, fxSound, fxEveryMs, until, now);
            return true;
        }
    }

    /// <summary>Does this mob already carry a status of <paramref name="category"/>? (The read-only half of
    /// <see cref="ApplyMobStatus"/>, for a verb that needs to check before it commits to anything.)</summary>
    public bool MobHasStatus(Mob mob, string category)
    {
        lock (_lock) return mob.HasStatus(category, Environment.TickCount64);
    }

    /// <summary>The spell key holding <paramref name="category"/>'s slot on this mob right now, or "" if it is
    /// free. Lets a blocked cast say whether it bounced off ITS OWN running spell or somebody else's.</summary>
    public string MobStatusKey(Mob mob, string category)
    {
        lock (_lock) return mob.StatusKey(category, Environment.TickCount64);
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
    /// <param name="fxAnim">Effect id to re-draw over the victim on every 1500ms tick, mirroring RTK's
    /// <c>while_cast_1500</c>, which calls <c>target:sendAnimation(1)</c> each time it deals damage — the
    /// poison is meant to keep flashing for its whole window, not once at cast. 0 = no repeat (the trap
    /// path, whose RTK script draws nothing per tick).</param>
    public bool PoisonMob(Mob mob, int tickCap, int lowMs, int highMs, uint ownerId, int flatTick = 0,
                          int fxAnim = 0, int fxSound = 0, string spellKey = "")
    {
        lock (_lock)
        {
            long now = Environment.TickCount64;
            if (mob.PoisonUntil > now) return false;                        // already venomed — RTK checkIfCast(venoms)
            mob.PoisonUntil     = now + 1 + Random.Shared.Next(lowMs, highMs + 1);
            mob.PoisonNextTick  = now + PoisonTickMs;
            mob.PoisonTickDam   = flatTick > 0 ? flatTick : Math.Clamp((int)(mob.MaxHp * 0.01), 1, tickCap);
            mob.PoisonOwnerId   = ownerId;
            mob.SetStatus("venoms", mob.PoisonUntil, spellKey);
            // Same 1500ms cadence as the damage tick, so the flash and the hit land together.
            if (fxAnim > 0) mob.SetFxRepeat(fxAnim, fxSound, PoisonTickMs, mob.PoisonUntil, now);
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
            return PlayerByIdLocked(id);
    }

    /// <summary>Same lookup for callers that already hold <c>_lock</c>. The monitor is re-entrant so taking it
    /// twice would work, but saying which methods expect it is how this file stays readable.</summary>
    private Session? PlayerByIdLocked(uint id) =>
        _maps.Values.SelectMany(m => m.Players).FirstOrDefault(p => p.PlayerId == id);

    /// <summary>
    /// Hot-reload every file-backed registry and rebuild the world population from it — the work behind the
    /// <c>@reload</c> GM command, lifted out of <see cref="Session"/> because the OTHER caller has no session:
    /// a content-only deploy drops a <c>run/reload_now</c> sentinel and <see cref="RestartSchedule.Loop"/>
    /// calls this. That is the whole point of the content lane — a CSV or Lua fix ships without kicking anyone.
    ///
    /// <para>Returns (ok, report). A load error keeps the OLD content and comes back <c>ok: false</c> with the
    /// message, rather than throwing — a bad CSV must not take down a running world.</para>
    /// </summary>
    public (bool ok, string report) ReloadFromDisk()
    {
        string summary;
        try { summary = Content.Reload(); }
        catch (Exception e)
        {
            Log.Info($"!! content reload failed: {e}");
            return (false, e.Message);
        }
        MapData.Invalidate();
        StaffAccounts.Load();   // the staff rosters are file-backed config too — promote/demote without a restart
        // Pre-warm the terrain cache for populated maps OUTSIDE _lock, so RebuildPopulation's re-materialization
        // (FreeSpawnTile/PickAreaHome -> MapData.For) hits a warm cache instead of reading .map files from disk
        // while holding the world lock (the old reload-stall).
        foreach (var mapId in PopulatedMapIds())
            if (Content.Maps.TryGetValue(mapId, out var mi)) MapData.For(mapId, mi.Xs, mi.Ys);
        var (mobs, npcs, maps) = RebuildPopulation();
        return (true, $"{summary}. Rebuilt population: {mobs} mob(s) torn down, {npcs} NPC(s) placed, " +
                      $"{maps} live map(s) re-materialized; map cache cleared.");
    }

    /// <summary>Every connected player, across every map — a server-wide (not map-scoped) roster snapshot.
    /// Used by channels that reach beyond one map, like subpath chat (RTK clif_sendsubpathmessage loops
    /// every session, not just one map's block list).</summary>
    public List<Session> AllPlayers()
    {
        lock (_lock)
            return _maps.Values.SelectMany(m => m.Players).ToList();
    }

    /// <summary>How many players are in the world right now. Separate from <see cref="AllPlayers"/> because
    /// the status publisher wants only the number, and materialising every session into a list on a timer to
    /// read <c>.Count</c> off it is pure garbage.</summary>
    public int OnlinePlayerCount()
    {
        lock (_lock)
        {
            var n = 0;
            foreach (var m in _maps.Values) n += m.Players.Count;
            return n;
        }
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

    // Own thread (see the constructor): each FlushNow serializes a multi-KB character graph to JSON and does
    // a synchronous SQLite write, so a sweep of a full server is a long block. On the thread pool that was
    // a pool thread held for the duration, competing with the heartbeat.
    private void AutoSaveLoop()
    {
        while (true)
        {
            Thread.Sleep(Session.AutoSaveMs);
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
            // Sleep-family amplifier: "the NEXT attack upon the target" lands harder (Doze 1.3x, Sleep 1.5x).
            // Consumed here so it applies to exactly one hit, and applied before the HP subtraction so the
            // over-head bar and any kill it causes both reflect the amplified number.
            double amp = mob.TakeDamageAmp(Environment.TickCount64);
            if (amp > 1.0) dmg = (int)Math.Round(dmg * amp);
            // A mythic boss does not simply die (RTK mob_ai_mythic.on_attacked). Its lethal-blow ladder, in
            // RTK's order, because the order is the mechanic:
            //
            //   1. FIRST lethal blow of its life -> Last Stand (RTK gates this on `mob.magic == 100`, and the
            //      spell spends all 100 with nothing to give it back, so it is once per life).
            //   2. Otherwise OVERKILL: a blow big enough to punch through its HP *and* the heal it would get
            //      (`attacker.damage >= mob.health + healAmount`) kills it outright, no roll. This is the
            //      whole reason a boss is beatable — you out-damage the save rather than out-waiting it.
            //      Note RTK skips this test on the Last Stand branch, so the first brink is always survivable.
            //   3. Then the save roll — 1/2, 2/3 or 3/4 by tier. Fail it and the blow lands normally.
            //
            // Runs BEFORE the subtraction, so the heal is what the blow lands on.
            if (dmg >= mob.Hp && Content.MobBosses.TryGetValue(mob.Key, out var boss) && boss.HealAmount > 0)
            {
                bool lastStand = !mob.SecondWindUsed && boss.LastStandMs > 0;
                // The save roll runs on the Last Stand branch too: RTK casts the spell and *then* rolls, so a
                // boss really can go into its last stand and drop dead on the same blow.
                bool overkill  = !lastStand && dmg >= mob.Hp + boss.HealAmount;
                bool saved     = !overkill && Random.Shared.Next(Math.Max(2, boss.HealChance)) != 0;

                if (lastStand)
                {
                    // RTK Spells/last_stand.lua: scrub own curses, animation 11, 8s duration, and PARALYSE
                    // ITSELF for it. The boss stands frozen and heals every tick while the window runs — it
                    // is a window to burst it down or back off, not an untouchable enrage.
                    mob.SecondWindUsed = true;
                    mob.LastStandUntil = Environment.TickCount64 + boss.LastStandMs;
                    mob.FrozenUntil = Math.Max(mob.FrozenUntil, mob.LastStandUntil);
                    mob.ClearStatus("curses"); mob.ClearStatus("minorcurses");
                    _deferredFx.Add((mapId, mob.Id, LastStandAnim, boss.Sound));
                }

                if (saved)
                {
                    mob.Hp = Math.Min(mob.MaxHp, mob.Hp + boss.HealAmount);
                    if (!lastStand) _deferredFx.Add((mapId, mob.Id, boss.Anim, boss.Sound));
                }
            }

            mob.Hp -= dmg;
            died = !mob.Alive;
            // Threat accrues with the damage (RTK swing.lua `player:addThreat(mob.ID, damage)`), whether or
            // not this hit takes the target — Tick's retarget then reads it. Counted BEFORE the death check
            // so the killing blow still counts, which matters for a pet deciding what to assist against.
            mob.AddThreat(attackerId, dmg);
            // Hitting something that has forgotten you reminds it (RTK amnesia.lua on_takedamage_while_cast).
            // Only the forgotten player breaks it — anyone else can keep hitting it without giving you away.
            if (mob.AmnesiaBy != 0 && mob.AmnesiaBy == attackerId) { mob.AmnesiaBy = 0; mob.AmnesiaUntil = 0; }

            // Lua AI hooks for this creature, if it has any (queued — see QueueHook).
            var actor = attackerId == 0 ? null : PlayerByIdLocked(attackerId);
            QueueHook(MobScript.OnAttacked, mapId, mob, actor);
            if (died) QueueHook(MobScript.AfterDeath, mapId, mob, actor);
            // Provoked -> fight back (mob_ai_normal on_attacked). Getting hit ALWAYS wins: it drops whatever
            // mob it was scrapping with (a pet) and re-points it at the player, and it overrides the
            // stuck-mob retarget in Tick — so zapping something always drags its aggro onto you, wall or no
            // wall, however unreachable you are.
            if (!died && attackerId != 0) { mob.TargetId = attackerId; mob.TargetMobId = 0; mob.DetourDir = NoDetour; mob.DetourLeft = 0; }
            // Being hit wakes a sleeping creature (RTK sleep.lua on_takedamage_while_cast). Paralyze
            // deliberately does NOT clear here — a paralyzed mob stays held while you beat on it.
            if (!died && mob.HasStatus("sleeps", Environment.TickCount64))
            {
                mob.ClearStatus("sleeps"); mob.FrozenUntil = 0; mob.FxRepeatUntil = 0;
            }
            // …unless it's PREY, which has no fight in it: being hurt by anything (a spell, a trap, a swing)
            // panics it instead. Tick clears the TargetId set just above before it can ever be acted on; this
            // is what makes a spell as alarming as a sword. A pure MISS never reaches here — Session.ResolveSwing
            // calls Spook directly for that case.
            if (!died && mob.Flees) mob.PanicUntil = Environment.TickCount64 + PanicMs;
            if (died && _maps.TryGetValue(mapId, out var m))
            {
                m.Mobs.Remove(mob);
                // Stamp the death registry for creatures gated on it (RTK's boss after_death hooks). Written
                // here rather than in the Lua hook so the clock starts on the kill itself, not on whether a
                // script happens to be loaded.
                if (Content.MobSpawnRules.TryGetValue(mob.Key, out var dRule) && dRule.DeathCooldownSec > 0)
                    _lastDeath[(mapId, mob.Key)] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
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

    /// <summary>A tick this slow (work OR scheduling delay, ms) gets a diagnostic line. 150ms is a quarter of
    /// the heartbeat — well clear of normal jitter, low enough to catch a stall long before a player would
    /// call it lag. <c>P1998_SLOW_TICK_MS</c> tunes it; 0 disables the watchdog.</summary>
    private static readonly int SlowTickMs =
        int.TryParse(Environment.GetEnvironmentVariable("P1998_SLOW_TICK_MS"), out var st) && st >= 0 ? st : 150;

    private long _lockWaitMs;   // how long the last Tick() waited to acquire _lock (watchdog attribution)

    // Fixed-cadence heartbeat on its own thread. Schedules against an absolute deadline rather than sleeping
    // TickMs between iterations, so the tick's own work doesn't accumulate into drift (the old
    // `await Task.Delay(600)` loop actually ran at ~612ms). If we fall a whole period behind we resync to
    // now instead of trying to catch up — the world would rather skip a beat than run several back-to-back.
    private void TickLoop()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long next = TickMs;
        while (true)
        {
            int wait = (int)(next - clock.ElapsedMilliseconds);
            if (wait > 0) Thread.Sleep(wait);

            // Measured BEFORE the tick body: this is time the thread was not running at all (GC pause, OS
            // preemption, the machine swapping) as opposed to time the tick spent working.
            long late = clock.ElapsedMilliseconds - next;
            next += TickMs;
            if (next <= clock.ElapsedMilliseconds) next = clock.ElapsedMilliseconds + TickMs;

            long t0 = clock.ElapsedMilliseconds;
            var gc0 = GC.GetTotalPauseDuration();
            _lockWaitMs = 0;
            try { Tick(); }
            catch (Exception e) { Log.Info($"!! world tick error: {e.Message}"); }

            if (SlowTickMs <= 0) continue;
            long work = clock.ElapsedMilliseconds - t0;
            if (work < SlowTickMs && late < SlowTickMs) continue;
            long gcMs = (long)(GC.GetTotalPauseDuration() - gc0).TotalMilliseconds;
            // Read this line as: LATE with gc ~= late  -> a GC pause. LATE with gc ~0 -> the OS didn't
            // schedule us (machine-wide contention). WORK with lock ~= work -> a session thread was holding
            // _lock (something slow ran inside a critical section). WORK with lock ~0 -> the tick body
            // itself is genuinely too big for the population it's driving.
            Log.Info($"!! SLOW TICK: work {work}ms (lock-wait {_lockWaitMs}ms), late {late}ms, gc {gcMs}ms — " +
                     $"{PlayerCount} player(s), {MobCount} mob(s) on {ActiveMapCount} active map(s)");
        }
    }

    /// <summary>Counts for the slow-tick diagnostic. Cheap, and only read on the watchdog path.</summary>
    private int PlayerCount    { get { lock (_lock) return _maps.Sum(kv => kv.Value.Players.Count); } }
    private int MobCount       { get { lock (_lock) return _maps.Sum(kv => kv.Value.Mobs.Count); } }
    private int ActiveMapCount { get { lock (_lock) return _maps.Count(kv => kv.Value.Players.Count > 0); } }

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
        // A creature's spell at a player (MobSpells.csv) and its idle flavour line — both queued for the same
        // reason as `hits`: landing a spell broadcasts, curses, and can kill.
        var mobCasts = new List<(Mob mob, Session target, Content.MobSpellDef spell)>();
        var chatter = new List<(ushort map, Mob mob, byte channel, string line)>();
        // A pet's swing at another mob (mob-on-mob, the only place that happens) — deferred out of the lock
        // for the same reason as `hits`: applying it broadcasts and can award the owner exp.
        var mobHits = new List<(ushort map, Mob attacker, Mob victim)>();
        // Real damage from a triggered trap (instant hit) or a poison tick — both need Session-facing
        // broadcasts (damage number, death despawn, owner exp) that must run outside the lock, same as `hits`.
        var trapDamage = new List<(ushort map, Mob mob, int dmg, uint ownerId)>();
        // Repeating status effects (RTK `while_cast`): venom re-draws its animation every poison tick, doze
        // and sleep re-draw theirs for as long as the hold runs. Broadcasting is socket I/O, so — like every
        // other visual below — the tick only QUEUES them under the lock and sends them after it's released.
        var fxRepeats = new List<(ushort map, uint id, int anim, int sound)>();
        var expiredPets = new List<(ushort map, Mob mob)>();
        var expiredMorphs = new List<Session>();
        var expiredStealth = new List<Session>();
        List<(ushort map, GroundItem gi)>? forage = null;
        bool timeChanged = false;
        List<(ushort map, byte weather)>? weatherChanges = null;

        // (0) Warm every active map's terrain BEFORE taking the lock. Both the respawn refill (Materialize ->
        // FreeSpawnTile) and the wander loop below call MapData.For, which on a miss reads the .map off disk,
        // decodes every cell and runs a SQLite query. Under _lock that stalled the whole world; out here it
        // costs nothing on the overwhelmingly common cache-hit path. See MapData.Prewarm.
        ushort[] active;
        lock (_lock) active = _maps.Where(kv => kv.Value.Players.Count > 0).Select(kv => kv.Key).ToArray();
        foreach (var id in active) MapData.Prewarm(id);

        long lockT0 = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_lock)
        {
            // Time spent BLOCKED here means another thread was inside a _lock critical section. The
            // slow-tick watchdog prints it, which is what distinguishes "someone else stalled us" from
            // "this tick body is too slow".
            _lockWaitMs = (System.Diagnostics.Stopwatch.GetTimestamp() - lockT0) * 1000 / System.Diagnostics.Stopwatch.Frequency;

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

            // (1.6) day/night clock (see the Epoch doc): re-derive the shared calendar from wall-clock time
            // and, on an in-game hour rollover, flag every connected session for a fresh 0x20 broadcast.
            // Checked every tick rather than every 750th, so the broadcast lands within 600ms of the true
            // rollover instead of drifting by however far into an hour the process happened to start.
            if (SyncClock()) timeChanged = true;

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
                        // Re-home it where it actually is. A charmed creature that chased something you were
                        // fighting can end up well outside the leash box around the Home it spawned at, and
                        // the wander step below leashes on ABSOLUTE distance from Home — without this it would
                        // fail every candidate step and stand frozen. (The walk-home in the wander block
                        // covers the same hazard for wild mobs; this one has no spawn point to walk back to.)
                        mob.HomeX = mob.X; mob.HomeY = mob.Y;
                    }

                    // Poison trap DOT (RTK poison_dart_trap.lua while_cast_1500): ticks every 1500ms regardless
                    // of freeze/wander state, and — per RTK — never fires a tick that would finish the kill.
                    if (mob.PoisonUntil > Environment.TickCount64 && Environment.TickCount64 >= mob.PoisonNextTick)
                    {
                        mob.PoisonNextTick = Environment.TickCount64 + PoisonTickMs;
                        // "Poison will not kill a target but rather bring them to the lowest possible health"
                        // (NexusAtlas). RTK's while_cast says the same in code: `if health > damage then
                        // remove else health = 1`. This used to SKIP the tick once HP fell to the tick
                        // amount, which left the victim parked wherever it happened to be instead of at 1 —
                        // so a venomed creature stopped short of the state the spell is supposed to leave it in.
                        int lethal = Math.Max(0, mob.Hp - 1);
                        int dam = Math.Min(mob.PoisonTickDam, lethal);
                        if (dam > 0) trapDamage.Add((mapId, mob, dam, mob.PoisonOwnerId));
                    }

                    // Repeating status animation (RTK `while_cast`): venom's per-tick zap, doze/sleep's drowse.
                    // Driven here rather than off each status's own timer so one cadence covers them all, and
                    // so it keeps running while the mob is frozen — which is exactly when you need to see it.
                    if (mob.FxRepeatUntil > Environment.TickCount64)
                    {
                        if (Environment.TickCount64 >= mob.FxRepeatNext)
                        {
                            mob.FxRepeatNext = Environment.TickCount64 + mob.FxRepeatEvery;
                            fxRepeats.Add((mapId, mob.Id, mob.FxRepeatAnim, mob.FxRepeatSound));
                        }
                    }
                    else if (mob.FxRepeatUntil != 0) mob.FxRepeatUntil = 0;

                    // Last Stand: while it runs, the boss claws HP back every heartbeat (RTK mob_ai_mythic
                    // heals on every move tick that `mob:hasDuration("last_stand")`). Before the freeze check,
                    // so a paralysed boss still regenerates — being held is not a free win against one.
                    if (mob.LastStandUntil != 0 && Content.MobBosses.TryGetValue(mob.Key, out var lsBoss))
                    {
                        if (Environment.TickCount64 >= mob.LastStandUntil) mob.LastStandUntil = 0;
                        else if (mob.Hp < mob.MaxHp)
                        {
                            mob.Hp = Math.Min(mob.MaxHp, mob.Hp + lsBoss.HealAmount);
                            fxRepeats.Add((mapId, mob.Id, lsBoss.Anim, lsBoss.Sound));
                        }
                    }

                    // …and while it is held it HEALS THROUGH the hold — every ~3s, a 1-in-2 roll for another
                    // full heal (RTK mob_ai_mythic.move: `os.time() % 3 == 0 and mob.paralyzed`). Note it does
                    // NOT break free: RTK never clears `mob.paralyzed` here, so paralysis on a boss still
                    // holds it still — it just stops being a way to win, because the boss out-heals the hold.
                    if (mob.FrozenUntil > Environment.TickCount64
                        && Content.MobBosses.TryGetValue(mob.Key, out var pBoss) && pBoss.ParaBreakChance > 0
                        && Environment.TickCount64 >= mob.ParaBreakAt)
                    {
                        mob.ParaBreakAt = Environment.TickCount64 + 3000;   // RTK's `os.time() % 3 == 0` cadence
                        if (Random.Shared.Next(pBoss.ParaBreakChance) == 0 && mob.Hp < mob.MaxHp)
                        {
                            mob.Hp = Math.Min(mob.MaxHp, mob.Hp + pBoss.HealAmount);
                            fxRepeats.Add((mapId, mob.Id, pBoss.Anim, pBoss.Sound));
                        }
                    }

                    // Curse shrug (RTK mob_ai_mythic.move: `os.time() % 10 == 0` and not paralysed, a 1-in-3
                    // roll to wipe EVERY curse on itself and flash animation 10). A mythic boss will not stay
                    // debuffed: land one and you have a few seconds of it, not the fight.
                    if (mob.FrozenUntil <= Environment.TickCount64
                        && Content.MobBosses.TryGetValue(mob.Key, out var cBoss) && cBoss.HealAmount > 0
                        && Environment.TickCount64 >= mob.CurseShrugAt)
                    {
                        mob.CurseShrugAt = Environment.TickCount64 + 10_000;
                        if (Random.Shared.Next(3) == 0
                            && (mob.HasStatus("curses", Environment.TickCount64) || mob.HasStatus("minorcurses", Environment.TickCount64)))
                        {
                            mob.ClearStatus("curses"); mob.ClearStatus("minorcurses");
                            fxRepeats.Add((mapId, mob.Id, CurseShrugAnim, 0));
                        }
                    }

                    if (mob.FrozenUntil > Environment.TickCount64) continue;   // paralyzed/asleep — hold still

                    // Blind (RTK's `target.blind = true`): a blinded creature can't SEE. It drops whoever it
                    // was fighting, the unprovoked-aggro scan below is skipped, and — this is the part that
                    // used to be wrong — it does NOT wander either. A mob with no sight has nowhere to go, so
                    // it holds its ground; the old code fell straight through to the wander block, which made
                    // a blinded mob spin on the spot and read as though the spell had done nothing.
                    // What it CAN still do is lash out at whatever is within arm's reach, turning to face it:
                    // being blind doesn't stop you swinging at someone who walks into you.
                    if (mob.BlindUntil > Environment.TickCount64)
                    {
                        mob.TargetId = 0; mob.TargetMobId = 0;
                        // Prey never fights (see the flee block below), and an owned creature has no business
                        // swinging at people off a PK map — the same two exemptions the sighted paths apply.
                        Session? reach = null;
                        if (!mob.Flees && (mob.OwnerId == 0 || Content.IsPvpMap(mapId)))
                            foreach (var p in m.Players)
                            {
                                if (p.IsDead || p.PlayerId == mob.OwnerId) continue;
                                int bdx = p.PlayerX - mob.X, bdy = p.PlayerY - mob.Y;
                                if ((bdx == 0 && Math.Abs(bdy) == 1) || (bdy == 0 && Math.Abs(bdx) == 1)) { reach = p; break; }
                            }
                        if (reach is null) { mob.AttackTimer = 0; continue; }
                        byte bface = FaceDelta(reach.PlayerX - mob.X, reach.PlayerY - mob.Y);
                        if (bface != mob.Dir) { mob.Dir = bface; turns.Add((mapId, mob.Id, bface)); }
                        mob.AttackTimer += TickMs;
                        if (mob.AttackTimer >= mob.AttackTime) { mob.AttackTimer = 0; hits.Add((mapId, mob, reach)); }
                        continue;
                    }

                    // Wounded rout (RTK bosses/nine_tailed_fox.lua + ogre_maletic.lua, which is Maletic AND
                    // Citelam): below 15% of its max HP the creature STOPS FIGHTING for good and bolts —
                    // `local rand = math.random(0,3); mob.side = rand; mob:move() mob:move() mob:move()`
                    // replaces the whole of move AND attack, so it will not swing again even if you corner it.
                    // Not our prey-flee (MobDef.Flees), which is about a rabbit backing away from anyone: this
                    // is an unwounded boss fighting normally right up to the moment it breaks.
                    //
                    // RTK re-rolls the direction once per AI tick and then covers three tiles in it. This
                    // heartbeat can only carry one tile per tick, so the direction is re-rolled on the
                    // creature's own MoveTime and it steps EVERY tick in between — at their MoveTime of 2000ms
                    // that is the same three tiles per direction, at the same speed.
                    // (`Hp < MaxHp` first so an untouched creature — nearly all of them, every tick — costs a
                    // comparison rather than a dictionary probe. A threshold of 100 would break this, which is
                    // why the loader's range is capped below it.)
                    if (mob.Hp < mob.MaxHp
                        && Content.MobSpawnRules.TryGetValue(mob.Key, out var fleeRule) && fleeRule.FleeBelowPct > 0
                        && mob.Hp * 100 <= mob.MaxHp * fleeRule.FleeBelowPct)
                    {
                        mob.TargetId = 0; mob.TargetMobId = 0; mob.AttackTimer = 0; mob.Returning = false;
                        mob.MoveTimer += TickMs;
                        if (mob.MoveTimer >= mob.MoveTime)
                        {
                            mob.MoveTimer -= mob.MoveTime;
                            byte side = (byte)Random.Shared.Next(4);
                            if (side != mob.Dir) { mob.Dir = side; turns.Add((mapId, mob.Id, side)); }
                        }
                        int fx = mob.X, fy = mob.Y;
                        switch (mob.Dir) { case 0: fy--; break; case 1: fx++; break; case 2: fy++; break; default: fx--; break; }
                        bool fok = fx >= 0 && fy >= 0
                                   && (dims.Item1 == 0 || (fx < dims.Item1 && fy < dims.Item2))
                                   && !occupied.Contains(((ushort)fx, (ushort)fy))
                                   && !mobTiles.Contains((fx, fy))
                                   && (terrain is null || !terrain.BlockedMove(fx, fy, mob.Dir));
                        if (fok)                                     // no leash: it is running away, not wandering
                        {
                            ushort fox = mob.X, foy = mob.Y;
                            mobTiles.Remove((mob.X, mob.Y));
                            mob.X = (ushort)fx; mob.Y = (ushort)fy;
                            mobTiles.Add((fx, fy));
                            moves.Add((mapId, mob.Id, fox, foy, mob.Dir));
                        }
                        continue;
                    }

                    // ---- PET AI: a mob with an OWNER (a Poet's Call of the Wild summon, or an Endear'd
                    // captive) does not behave like a wild one. Three rules, applied in order:
                    //   1. never fight your owner,
                    //   2. fight what your owner has attacked, or what has attacked your owner,
                    //   3. otherwise stand still — where you were summoned.
                    // Before this, Mob.OwnerId existed but drove NO behaviour at all, which is why both halves
                    // looked broken from the outside: a CotW pet (every cotw_* MobDef is MobBehavior 0) just
                    // wandered off on its spawn leash and never swung at anything, and Endear on an aggressive
                    // creature handed it to you for a fraction of a second before the unprovoked-aggro scan
                    // below re-acquired the nearest player — you — and it turned right back around.
                    //
                    // All three are RTK's (mob_ai_cotw.move/attack), including the standing still: its move
                    // ends `target = mob:getBlock(mob.owner)` and then `if target.blType == BL_PC then return
                    // end`, so an idle pet never takes a step toward its owner. A summon is a thing you PLACE.
                    //
                    // NOT ported, deliberately: a pet does not fight back when something hits IT and only it.
                    // RTK's `cotw.on_attacked` looks like retaliation (`if mob.target == mob.owner then
                    // mob.target = attacker.ID`), but the very next move tick recomputes the target from the
                    // owner's threat list and throws it away, so it never survives to be acted on.
                    //
                    // What RTK does establish, and we honour, is that a mob's damage is credited to
                    // `mob->owner` when that id is a player (clif.c) — that part is ApplyMobOnMobHit.
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
                            // Rule 2, recomputed from scratch every tick (RTK's move and attack branches both
                            // re-walk the threat list; there is no sticky target) so the pet picks up a new
                            // attacker the moment its current one dies, leashes off, or is out-threatened.
                            // Never an NPC, and never the pet itself — everything else is fair game, gated
                            // purely on threat (see the OWNED-CREATURE note below).
                            //
                            // A PET IS REACTIVE, NOT A BODYGUARD, and it fights exactly two kinds of creature:
                            //
                            //   what you have attacked   -> `o.ThreatOf(owner) > 0`. Our threat table is keyed
                            //                               by the player who DEALT the damage, so any mob you
                            //                               have hit carries threat from you. This is RTK's
                            //                               `mobs[i]:checkThreat(mob.owner) > 0` list.
                            //   what has attacked you    -> `owner.RecentMobAttackerId`, stamped by
                            //                               Session.ApplyMobHit on a LANDED blow. RTK's
                            //                               `owner.attacker` fallback, and the reason a pet
                            //                               defends you from something you never touched.
                            //
                            // Both halves need a real blow to have been struck by somebody. A creature that has
                            // merely noticed you, or is walking at you, is invisible to the pet — which is what
                            // makes the corner-wall real: stand in a corner with two summons and nothing moves
                            // until the first hit lands, in either direction.
                            //
                            // AN OWNED CREATURE IS NOT EXEMPT. This used to filter `o.OwnerId == 0`, so a pet
                            // would never look at another pet — and since the owner can now swing at his own
                            // summons, that made hitting one of your own a fight nobody would join. The
                            // threat test is the whole gate: a sibling only becomes a target once you have
                            // actually hit it, so pets still ignore each other (and other poets' pets)
                            // completely until someone starts something. `o.Id != mob.Id` keeps a pet from
                            // picking ITSELF once you've hit it.
                            //
                            // Bounded by AggroRadius because RTK's list comes from `getObjectsInArea` — the pet
                            // fights what is around it, and won't cross a dungeon to reach a high-threat mob it
                            // cannot see. Distance only breaks ties, so it still walks past a rabbit to reach
                            // whatever is actually killing you. The threat list is searched first and the
                            // attacker is the fallback, in RTK's order.
                            uint bit = owner.RecentMobAttackerId;
                            var foe = m.Mobs.Where(o => o.Alive && !o.IsNpc && o.Id != mob.Id
                                             && (o.ThreatOf(owner.PlayerId) > 0 || o.Id == bit)
                                             && Math.Max(Math.Abs(o.X - mob.X), Math.Abs(o.Y - mob.Y)) <= AggroRadius)
                                             .OrderByDescending(o => o.ThreatOf(owner.PlayerId))
                                             .ThenBy(o => Math.Max(Math.Abs(o.X - mob.X), Math.Abs(o.Y - mob.Y)))
                                             .FirstOrDefault();
                            mob.TargetMobId = foe?.Id ?? 0;

                            // A pet steps EVERY heartbeat rather than on its own MobMoveTime. That cadence is a
                            // wander timer (a panda's is 2s), far too slow to close on something mid-fight.
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

                            // Nothing to fight: a pet HOLDS ITS GROUND. It does not heel, does not follow, does
                            // not drift — it stands where it was summoned until something hits you or you hit
                            // something (RTK's move ends `target = getBlock(mob.owner)` and then bails on
                            // `target.blType == BL_PC`, so its pet never paths to its owner either). Walk away
                            // from your summons and you leave them behind, which is what makes them placeable:
                            // two of them in a doorway are a wall, not an escort.
                            continue;
                        }
                    }

                    // ---- PREY AI (MobDef.Flees, game-data/MobFlees.csv): a rabbit or a blue rooster does
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
                    // (No blind check here any more — a blinded mob never reaches this line; its own branch
                    // above handles it and continues.)
                    if (mob.TargetId == 0 && mob.Aggressive && mob.OwnerId == 0)
                    {
                        var victim = m.Players.FirstOrDefault(p => !p.IsDead
                            && Math.Max(Math.Abs(p.PlayerX - mob.X), Math.Abs(p.PlayerY - mob.Y)) <= AggroRadius);
                        if (victim is not null) mob.TargetId = victim.PlayerId;
                    }

                    // Idle flavour (MobChatter.csv). RTK puts these in each mob's `move` hook — the whole
                    // "custom AI" of the grim ogre is a 1-in-100 roll to grunt — so it belongs here, before
                    // any of the targeting work and regardless of whether the mob is fighting.
                    if (Content.MobChatter.TryGetValue(mob.Key, out var chat) && Random.Shared.Next(chat.Chance) == 0)
                        chatter.Add((mapId, mob, chat.Channel, chat.Lines[Random.Shared.Next(chat.Lines.Length)]));

                    // Amnesia (RTK amnesia.lua while_cast): the mob drops the player it has forgotten, then
                    // re-picks from the rest of its threat table below. Checked before the retarget so the
                    // forgotten player can't simply be chosen straight back.
                    if (mob.AmnesiaBy != 0)
                    {
                        if (Environment.TickCount64 >= mob.AmnesiaUntil) { mob.AmnesiaBy = 0; mob.AmnesiaUntil = 0; }
                        else if (mob.TargetId == mob.AmnesiaBy) { mob.TargetId = 0; mob.AttackTimer = 0; }
                    }

                    // A PASSIVE creature forgets anyone who leaves the map entirely (RTK mob_ai_basic:
                    // `if mob.behavior == 0 and target.m ~= mob.m then ... setThreat(mob.ID, 0)`). An
                    // aggressive one keeps the grudge banked, so walking out and back in doesn't launder it.
                    if (!mob.Aggressive && mob.TargetId != 0 && m.Players.All(p => p.PlayerId != mob.TargetId))
                    {
                        mob.ClearThreat(mob.TargetId);
                        mob.TargetId = 0; mob.AttackTimer = 0;
                    }

                    // Threat (RTK mob_ai_normal calls threat.calcHighestThreat at the top of both its move and
                    // attack branches, so it is re-evaluated every tick a mob is in a fight — not just when it
                    // is hit). Owned creatures are exempt: a pet's target comes from its owner, above.
                    if (mob.Threat is { Count: > 0 } && mob.OwnerId == 0) RetargetByThreat(m, mob);

                    // Combat AI (RTK mob_ai_normal: on_attacked sets the target; move/attack chase + swing at
                    // it): a provoked mob (World.TryDamage set TargetId) abandons wandering to path toward and
                    // melee its attacker instead, until the target dies/leaves/logs off or strays past
                    // ChaseLeash tiles from the mob's home — then it falls back to normal wandering below.
                    if (mob.TargetId != 0)
                    {
                        mob.Returning = false;   // RTK: `if (mob.target ~= 0) then mob.returning = false end`
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

                            // Spellcasting (MobSpells.csv). Rolled here rather than in the swing branch below
                            // because RTK's casters do it from `move`, at their own range — a mythic boss
                            // throws lightning from five tiles out, a raven pecks from arm's reach. Cast is IN
                            // ADDITION to the swing, never instead of it (RTK's raven runs `peck.cast(...)`
                            // and then falls straight through to mob_ai_basic.attack).
                            if (Content.MobSpells.TryGetValue(mob.Key, out var repertoire)
                                && Environment.TickCount64 >= mob.SpellReadyAt)
                            {
                                int reach = Math.Max(Math.Abs(tdx), Math.Abs(tdy));
                                foreach (var sp in repertoire)
                                {
                                    if (reach > sp.Range || Random.Shared.Next(Math.Max(1, sp.Chance)) != 0) continue;
                                    // A `melee` row is a BONUS SWING with the creature's own weapon rather
                                    // than a spell — RTK's Gim Yi (bosses/gimyi.lua) casts `ambush`, whose
                                    // whole payload for an already-adjacent mob is a shout and a second
                                    // `mob:attack(target.ID)`. Routing it through the normal hit queue means
                                    // it uses his real damage band, hit, crit and your AC, instead of a flat
                                    // number in a CSV that would drift from him the moment his stats changed.
                                    // (ApplyMobSpell says the line for a real cast, so a melee row says its
                                    // own — RTK's ambush uses `mob:talk(2, ...)`, the unattributed channel.)
                                    if (sp.Effect == "melee")
                                    {
                                        hits.Add((mapId, mob, target));
                                        if (sp.Say.Length > 0) chatter.Add((mapId, mob, (byte)2, sp.Say));
                                    }
                                    else mobCasts.Add((mob, target, sp));
                                    mob.SpellReadyAt = Environment.TickCount64 + sp.EveryMs;
                                    break;   // one spell per opportunity, first match in file order wins
                                }
                            }

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

                    // Walk home after giving up a chase (RTK mob_ai_basic.move's `returning` block: once the
                    // creature is retDist from its start it sets `mob.newMove = 250` and paths back via
                    // toStart, clearing the flag on arrival).
                    //
                    // This is not cosmetic. A mob that chased you to the ChaseLeash edge and gave up is
                    // sitting several tiles outside its wander box, and EVERY candidate tile in the wander
                    // block below fails its `|nx - HomeX| <= Leash` test — so it would stand frozen on that
                    // tile forever, and a pulled-and-dropped patch of a map would slowly fill up with
                    // statues. It sprints back (RTK's 250ms, which at this heartbeat is a tile per tick)
                    // rather than strolling, so the patch resets promptly.
                    if (!mob.Returning && mob.Leash > 1
                        && Math.Max(Math.Abs(mob.X - mob.HomeX), Math.Abs(mob.Y - mob.HomeY)) > mob.Leash)
                        mob.Returning = true;

                    if (mob.Returning)
                    {
                        if (mob.X == mob.HomeX && mob.Y == mob.HomeY) { mob.Returning = false; mob.MoveTimer = 0; }
                        else
                        {
                            StepMobToward(mapId, m, mob, mob.HomeX, mob.HomeY,
                                          dims, terrain, occupied, mobTiles, moves, turns, trapDamage);
                            continue;
                        }
                    }

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

        // Repeating status effects queued above (venom's per-tick zap, doze/sleep's drowse) — the same 0x29 +
        // 0x19 pair a cast plays, re-sent over the afflicted creature for as long as the status holds.
        foreach (var fr in fxRepeats)
            Broadcast(fr.map, p => { p.EffectOver(fr.id, fr.anim); if (fr.sound > 0) p.SoundAt(fr.sound, fr.id); });

        // …and anything raised from inside TryDamage, which has no way to send where it stands.
        List<(ushort map, uint id, int anim, int sound)> deferred;
        List<(string key, string hook, ushort map, Mob mob, Session? actor)> hooks;
        lock (_lock)
        {
            deferred = new List<(ushort, uint, int, int)>(_deferredFx); _deferredFx.Clear();
            hooks = new List<(string, string, ushort, Mob, Session?)>(_hooks); _hooks.Clear();
        }
        foreach (var fx in deferred)
            Broadcast(fx.map, p => { if (fx.anim > 0) p.EffectOver(fx.id, fx.anim); if (fx.sound > 0) p.SoundAt(fx.sound, fx.id); });

        // Lua AI hooks, run here and only here — outside the lock (see _hooks).
        foreach (var h in hooks)
            Try(() => MobScript.Fire(h.key, h.hook, new MobContext(this, h.map, h.mob, h.actor)));

        // The PLAYER half of the same thing: a dozed player's drowse redraws and their hold lapses. Kept out
        // here with the other broadcasts rather than in the mob loop — it is per-session, not per-mob, and it
        // sends. Only sleepers do any work; TickSleep returns immediately for everyone else.
        foreach (var s in AllPlayers()) { Try(s.TickSleep); Try(s.TickPoison); }

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

        // Creature spells + idle flavour queued above — both broadcast, and a spell can kill, so neither can
        // run under the lock.
        foreach (var c in mobCasts) Try(() => c.target.ApplyMobSpell(c.mob, c.spell));
        foreach (var ch in chatter)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(
                ch.channel == 0 ? $"{ch.mob.Name}: {ch.line}" : ch.line);   // RTK talk(0) attributes, talk(2) doesn't
            Broadcast(ch.map, p => p.SpeakEntity(ch.channel, ch.mob.Id, bytes));
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
            // Nothing to persist: the calendar is derived from the epoch, so a restart resumes it exactly.
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
        // Floor items ride along with the mobs: a forage top-up or another player's drop lands on the map
        // while we're standing still, and its own broadcast is viewport-gated like every other 0x07 — so the
        // tick is what eventually draws it for whoever is close enough. Hence `|| m.Items.Count > 0`: a map
        // with items but no mobs still needs reconciling.
        (Session[] players, Mob[] mobs, GroundItem[] items)[] snapshot;
        lock (_lock)
        {
            snapshot = _maps.Values
                .Where(m => m.Players.Count > 0 && (m.Mobs.Count > 0 || m.Items.Count > 0))
                .Select(m => (m.Players.ToArray(), m.Mobs.ToArray(), m.Items.ToArray()))
                .ToArray();
        }
        foreach (var (players, mobs, items) in snapshot)
            foreach (var p in players) Try(() => { p.SyncMobs(mobs); p.SyncGroundItems(items); });
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
