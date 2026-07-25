using Shared;

namespace Server;

/// <summary>A read-only snapshot of a player entity, so a peer can draw it without touching that
/// session's mutable state. Built under no lock (fields are only written by the owning session's
/// read-loop; a torn read at worst mis-places a peer by one tile until its next move packet).</summary>
public readonly record struct PlayerSnapshot(
    uint Id, ushort X, ushort Y, byte Dir, byte Sex, byte Face, byte Armor, byte Weapon, byte Shield, string Name);

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
}

/// <summary>
/// The single shared game world: every connected player and every live mob, grouped by map. One
/// instance is created in <see cref="TkListener"/> and handed to every <see cref="Session"/>, so all
/// clients observe the SAME entities — players see each other, and everyone fights the same mobs.
///
/// This replaces the old per-Session mob ownership for GAMEPLAY mobs (!summon / !rabbit). The debug
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
    }
    private readonly Dictionary<ushort, MapState> _maps = new();

    /// <summary>A fixed spawn point: one <see cref="Live"/> mob at a time, respawned <see cref="RespawnTick"/>
    /// ticks after it dies. Built once from <see cref="Content.Spawns"/>; drives the persistent world roster.</summary>
    private sealed class Spawn
    {
        public MobDef Def = null!;
        public ushort X, Y;
        public Mob?   Live;          // the currently-alive mob for this point (null while dead/pending)
        public long   RespawnTick;   // tick at which a dead point may respawn (0 = not pending)
    }
    private readonly Dictionary<ushort, List<Spawn>> _spawns = new();   // map -> its spawn points
    private readonly Dictionary<uint, Spawn> _mobSpawn = new();          // live mob id -> its spawn point
    private long _tick;                                                  // heartbeat counter (600ms each)

    private const int TickMs = 600;         // world heartbeat period; also the unit MoveTimer accumulates in
    // A dead spawn point respawns after this many ticks (~18s at 600ms/tick), mirroring RTK's short town
    // respawn cadence so a cleared patch of Buya refills while the player is still nearby.
    private const int RespawnTicks = 30;
    // How far a mob may wander from its spawn tile (Chebyshev). Kept small so town critters hug their
    // spawn points instead of clustering into a dense knot that constantly overlaps on screen.
    private const int WanderRadius = 2;

    // Disjoint entity-id pools so a player id can never collide with a shared-mob id.
    //   players:     1 ..            (bound to each client's camera via 0x05)
    //   world mobs:  100000 ..       (session-local debug dummies use their own 5000+ pool, invisible
    //                                 to other clients, so those ranges never need to be globally unique)
    //   ground items: 500000 ..    (disjoint from players + mobs so a floor-item id never collides)
    private uint _nextPlayerId = 1;
    private uint _nextMobId = 100_000;
    private uint _nextItemId = 500_000;

    public World()
    {
        PopulateSpawns();                 // build the persistent roster from Content.Spawns (needs Content.Load first)
        _ = Task.Run(TickLoop);           // start the shared mob-AI + respawn heartbeat
    }

    // ---- persistent spawn roster --------------------------------------------------------------

    /// <summary>Materialize one live mob per RTK spawn point on every renderable map. Runs once at
    /// startup (Content is already loaded). Mobs exist immediately; each client only receives the ones
    /// in its viewport (Session.SyncMobs), and dead points refill via <see cref="Tick"/>.</summary>
    private void PopulateSpawns()
    {
        int points = 0, skipped = 0;
        lock (_lock)
        {
            foreach (var sd in Content.Spawns)
            {
                if (!Content.Maps.ContainsKey(sd.Map)) { skipped++; continue; }   // map the client can't render
                var def = Content.MobById(sd.MobId);
                if (def is null) { skipped++; continue; }                           // unknown mob id

                var sp = new Spawn { Def = def, X = sd.X, Y = sd.Y };
                if (!_spawns.TryGetValue(sd.Map, out var list)) { list = new(); _spawns[sd.Map] = list; }
                list.Add(sp);
                Materialize(sd.Map, sp);
                points++;
            }
        }
        Log.Info($"spawns: {points} live spawn points across {_spawns.Count} map(s)" +
                 (skipped > 0 ? $" ({skipped} skipped — unknown map/mob)" : ""));
    }

    /// <summary>Create the live mob for a spawn point and register it. Caller holds <c>_lock</c>.</summary>
    private void Materialize(ushort mapId, Spawn sp)
    {
        var d = sp.Def;
        // Don't stack: several RTK spawn points share a tile, and a respawn can land where another mob has
        // wandered. Place on the spawn tile if free, else the nearest open one (home stays the spawn tile).
        var (sx, sy) = FreeSpawnTile(mapId, sp.X, sp.Y);
        var mob = new Mob(_nextMobId++, d.Look, sx, sy, d.Name, d.Hp)
        {
            // Color byte = RTK's MobLookColor. (The client Monster.tbl palette turned out wrong here — it
            // rendered every mob green — so we use RTK's per-mob colour, which matches for most creatures.)
            Color = d.Color, Exp = d.Exp, Dir = 2, HomeX = sp.X, HomeY = sp.Y, Wander = true,
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

    public uint AllocatePlayerId() { lock (_lock) return _nextPlayerId++; }
    public uint AllocateMobId()    { lock (_lock) return _nextMobId++; }
    public uint AllocateItemId()   { lock (_lock) return _nextItemId++; }

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

    // ---- mobs ---------------------------------------------------------------------------------

    /// <summary>Add a shared mob to a map and stream it to everyone whose viewport it falls in (players
    /// out of range receive it later, as they approach, via <see cref="Tick"/>'s per-player sync).</summary>
    public void AddMob(ushort mapId, Mob mob)
    {
        lock (_lock) Map(mapId).Mobs.Add(mob);
        var one = new[] { mob };
        Broadcast(mapId, p => p.SyncMobs(one));
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
    /// method drops + broadcasts). The caller still broadcasts the damage number / corpse despawn.</summary>
    public bool TryDamage(ushort mapId, Mob mob, int dmg, out bool died)
    {
        died = false;
        List<GroundItem>? drops = null;
        lock (_lock)
        {
            if (!mob.Alive) return false;
            mob.Hp -= dmg;
            died = !mob.Alive;
            if (died && _maps.TryGetValue(mapId, out var m))
            {
                m.Mobs.Remove(mob);
                if (_mobSpawn.TryGetValue(mob.Id, out var sp))
                {
                    sp.Live = null;
                    sp.RespawnTick = _tick + RespawnTicks;   // refill this point shortly
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
        foreach (var (item, amount) in Content.RollDrops(def, Random.Shared))
        {
            var gi = new GroundItem
            {
                Id = _nextItemId++, ItemId = item.Id, X = mob.X, Y = mob.Y,
                Amount = amount, Graphic = item.Icon, Dura = item.Durability,
            };
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
    /// grabbing the same tile can't both win — and despawn it for everyone. Null if the tile is empty.</summary>
    public GroundItem? PickUp(ushort mapId, int x, int y)
    {
        GroundItem? gi = null;
        lock (_lock)
        {
            if (_maps.TryGetValue(mapId, out var m))
            {
                // last match = most recently dropped (drawn on top)
                for (int i = m.Items.Count - 1; i >= 0; i--)
                    if (m.Items[i].X == x && m.Items[i].Y == y) { gi = m.Items[i]; m.Items.RemoveAt(i); break; }
            }
        }
        if (gi is not null) Broadcast(mapId, p => p.DespawnEntity(gi.Id));
        return gi;
    }

    /// <summary>Despawn every mob on a map for all its players (the shared !kill).</summary>
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

    // One heartbeat: (1) refill dead spawn points that are due, (2) wander every live mob, (3) stream the
    // moves to observers who can see them, (4) reconcile each player's viewport (mobs that wandered in/out
    // of view, plus this tick's respawns, appear/disappear). All map mutation happens under the lock; no
    // socket I/O does. Only maps with at least one player are processed — an empty map's roster stays put.
    private void Tick()
    {
        _tick++;
        var moves = new List<(ushort map, uint id, ushort x, ushort y, byte dir)>();
        var turns = new List<(ushort map, uint id, byte dir)>();
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
                    if (!mob.Wander || !mob.Alive) continue;
                    if (mob.FrozenUntil > Environment.TickCount64) continue;   // paralyzed/asleep — hold still
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
                              && Math.Abs(nx - mob.HomeX) <= WanderRadius
                              && Math.Abs(ny - mob.HomeY) <= WanderRadius                          // leash to spawn
                              && !occupied.Contains(((ushort)nx, (ushort)ny))                     // not onto a player
                              && !mobTiles.Contains((nx, ny))                                      // not onto another mob
                              && (terrain is null || !terrain.Solid(nx, ny));                     // walls + water/cliffs (obj|pass)
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

        // (5) natural HP/MP regen for EVERY connected player (not gated on mobs/viewport, unlike the
        // steps above). Each session tracks its own 25s accumulator and only emits a status packet on a
        // real change — see Session.RegenTick. Snapshot the player list under the lock, tick outside it.
        Session[] players2;
        lock (_lock) players2 = _maps.Values.SelectMany(m => m.Players).ToArray();
        foreach (var p in players2) Try(() => p.RegenTick(TickMs));
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
}
