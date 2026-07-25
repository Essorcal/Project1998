using Shared;

namespace Server;

/// <summary>A read-only snapshot of a player entity, so a peer can draw it without touching that
/// session's mutable state. Built under no lock (fields are only written by the owning session's
/// read-loop; a torn read at worst mis-places a peer by one tile until its next move packet).</summary>
public readonly record struct PlayerSnapshot(
    uint Id, ushort X, ushort Y, byte Dir, byte Sex, byte Face, byte Armor, byte Weapon, string Name);

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

    // Disjoint entity-id pools so a player id can never collide with a shared-mob id.
    //   players:     1 ..            (bound to each client's camera via 0x05)
    //   world mobs:  100000 ..       (session-local debug dummies use their own 5000+ pool, invisible
    //                                 to other clients, so those ranges never need to be globally unique)
    //   ground items: 500000 ..    (disjoint from players + mobs so a floor-item id never collides)
    private uint _nextPlayerId = 1;
    private uint _nextMobId = 100_000;
    private uint _nextItemId = 500_000;

    public World() => _ = Task.Run(TickLoop);   // start the shared mob-AI heartbeat

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

    /// <summary>Add a shared mob to a map and draw it for everyone standing there.</summary>
    public void AddMob(ushort mapId, Mob mob)
    {
        lock (_lock) Map(mapId).Mobs.Add(mob);
        Broadcast(mapId, p => p.ShowMob(mob));
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

    /// <summary>Apply damage under the lock (so concurrent attackers can't double-kill). Returns
    /// false if the mob was already dead; otherwise sets <paramref name="died"/> and, on death,
    /// removes it from the map. The caller broadcasts the number / despawn.</summary>
    public bool TryDamage(ushort mapId, Mob mob, int dmg, out bool died)
    {
        died = false;
        lock (_lock)
        {
            if (!mob.Alive) return false;
            mob.Hp -= dmg;
            died = !mob.Alive;
            if (died && _maps.TryGetValue(mapId, out var m)) m.Mobs.Remove(mob);
        }
        return true;
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
            try { await Task.Delay(600); Tick(); }
            catch (Exception e) { Log.Info($"!! world tick error: {e.Message}"); }
        }
    }

    // Compute all moves under the lock (fast, no I/O), then broadcast them outside it. Mirrors RTK's
    // per-mob walk_timer: pick a random step, validate bounds + collision + leash + player tiles, then
    // send 0x0C to everyone on the mob's map.
    private void Tick()
    {
        var moves = new List<(ushort map, uint id, ushort x, ushort y, byte dir)>();
        lock (_lock)
        {
            foreach (var (mapId, m) in _maps)
            {
                if (m.Mobs.Count == 0 || m.Players.Count == 0) continue;   // no observers -> don't bother
                var dims = Content.Maps.TryGetValue(mapId, out var mi) ? (mi.Xs, mi.Ys) : ((ushort)0, (ushort)0);
                var terrain = dims.Item1 > 0 ? MapData.For(mapId, dims.Item1, dims.Item2) : null;
                var occupied = m.Players.Select(p => (p.PlayerX, p.PlayerY)).ToHashSet();

                foreach (var mob in m.Mobs)
                {
                    if (!mob.Wander || !mob.Alive) continue;
                    byte dir = (byte)Random.Shared.Next(4);
                    int nx = mob.X, ny = mob.Y;
                    switch (dir) { case 0: ny--; break; case 1: nx++; break; case 2: ny++; break; case 3: nx--; break; }

                    bool ok = nx >= 0 && ny >= 0
                              && (dims.Item1 == 0 || (nx < dims.Item1 && ny < dims.Item2))
                              && Math.Abs(nx - mob.HomeX) <= 3 && Math.Abs(ny - mob.HomeY) <= 3   // leash
                              && !occupied.Contains(((ushort)nx, (ushort)ny))                     // not onto a player
                              && (terrain is null || terrain.Obj(nx, ny) == 0);                   // walls/objects
                    mob.Dir = dir;
                    if (!ok) continue;   // blocked/leashed: turn in place, don't move

                    mob.X = (ushort)nx; mob.Y = (ushort)ny;
                    moves.Add((mapId, mob.Id, mob.X, mob.Y, dir));
                }
            }
        }
        // Broadcast each moved mob outside the lock; Broadcast re-reads the (possibly changed) player set.
        foreach (var mv in moves)
            Broadcast(mv.map, p => p.MoveEntity(mv.id, mv.x, mv.y, mv.dir));
    }

    private static void Try(Action a)
    {
        try { a(); } catch { /* dead/closing socket — its own read-loop will clean it up */ }
    }
}
