using System.Diagnostics;
using Shared;

namespace Server;

// The spawn half of World (#37, section 1). World.cs keeps the lock, the heartbeat, NPC placement, the
// traps and the mob-death path; this file keeps what decides WHEN and WHERE a world creature appears: the
// POINT roster (Spawn) and the GROUP roster (SpawnGroup), their build from Content, the lazy per-map
// materialise, the batch refill, the boss death registry and the tile picking. Nested in World, like
// MobAiTick, so it reaches MapState, Map(), BuildMob, FreeSpawnTile, OccupiedTiles and the tick constants
// as they are, without widening any of them — and so Spawn and SpawnGroup stay private to the one type
// that reads them.
public sealed partial class World
{
    /// <summary>
    /// The two spawn rosters and everything that runs them. One per <see cref="World"/>, built by the
    /// constructor before <c>PopulateSpawns</c> and called at the same five moments the code was called
    /// when it lived in World.cs: <see cref="Build"/> at start-up and on <c>@reload</c>
    /// (<see cref="RebuildPopulation"/>), <see cref="EnsureMaterialized"/> when a player enters a map,
    /// <see cref="RespawnDuePoints"/> and <see cref="RefillDueGroups"/> from the tick's phases (1) and (1.1),
    /// and <see cref="RecordDeath"/> / <see cref="ReleasePoint"/> when a creature dies or is ridden away.
    ///
    /// <para><b>Takes no lock of its own.</b> Every method that touches map state asserts
    /// <see cref="HoldsWorldLock"/> — the callers already hold <c>_lock</c>, exactly as they did before the
    /// move — and the three public statics that only read <see cref="Content"/> and the terrain cache
    /// (<see cref="PlacementBox"/>, <see cref="Placeable(ushort, IReadOnlySet{ValueTuple{int, int}}, int, int)"/>, <see cref="OpenTiles"/>) need none.</para>
    /// </summary>
    internal sealed class SpawnDirector
    {
        private const string LockNote = "SpawnDirector runs under World._lock and nowhere else";

        private readonly World world;

        internal SpawnDirector(World world) => this.world = world;
        // ---- the two spawn systems ------------------------------------------------------------------
        //
        // RTK runs two, and so do we, because they behave nothing alike:
        //
        //   POINT (Spawn, below) — the C engine's static table. One mob per point, revived on its OWN tile
        //   `MobSpawnTime` seconds after it dies. Towns, and the trap-ambush supplement. Per-kill, per-point.
        //
        //   GROUP (SpawnGroup, below) — the Lua spawner NPC, which is every hunting map. Kills do nothing at
        //   all. One clock per handleSpawn call; when it elapses the whole group is topped back up to its caps
        //   in a single batch at freshly-rolled tiles. That is what makes clearing a room mean something: the
        //   room stays cleared for the full timer, and no amount of camping the corpse makes it come back.
        //
        // The old code ran everything on the point model with one global ~18s cadence, which turned every cave
        // into a treadmill — kill, wait 18s, kill the same mob again — and is exactly the farming loop the
        // group model exists to prevent.

        /// <summary>A spawn point: one <see cref="Live"/> mob at a time, respawned <see cref="RespawnTick"/>
        /// ticks after it dies. Built once from <see cref="Content.Spawns"/> (fixed tile, <see cref="Placed"/>
        /// already true) and from the trap-ambush supplement (a random home tile is chosen in the box the first
        /// time the point materializes). Drives the persistent world roster.
        ///
        /// <para>Filed under <see cref="Map"/>, the map its row names, unless its creature has rooms
        /// (<see cref="RoomMaps"/>, #333): then it is filed under its rooms' maps instead, and lives in
        /// <c>_roomPoints</c> rather than <c>_spawns</c>. See <see cref="AddSpawn"/>.</para></summary>
        private sealed class Spawn
        {
            public MobDef Def = null!;
            public ushort Map;           // the map its row names: where Content put it, and what Materialize is asked about
            public ushort[]? RoomMaps;   // its creature's rooms' maps (#333): filed under these, not Map; null ⇒ filed under Map
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
        /// <summary>One <c>handleSpawn</c> call: a map, a bounding box, a batch clock, and the mobs it caps.
        /// RTK keys the clock off the call's FIRST mob (<c>spawnTable[map][mobs[1]]</c>) and refills every mob in
        /// the call together, so the twelve Wilderness creatures share one 300s cycle rather than each running
        /// its own. <see cref="NextBatchUnix"/> is absolute unix seconds, so the clock keeps running while nobody
        /// is on the map — walking out and back in neither pauses nor restarts it.</summary>
        private sealed class SpawnGroup
        {
            public ushort Map;
            public int    TimerSec;                          // seconds between batches (RTK's `timer` argument)
            public long   NextBatchUnix;                     // when this group may next top up (0 = immediately)
            public ushort MinX, MinY, MaxX, MaxY;            // placement box; all-zero ⇒ anywhere walkable
            public readonly List<(MobDef Def, int Cap)> Members = new();
        }
        private readonly Dictionary<ushort, List<Spawn>> _spawns = new();   // map -> its spawn points
        /// <summary>The points whose creature has rooms (#333), each once, filed under every map in its
        /// <see cref="Spawn.RoomMaps"/>: built on the first entry to any of them
        /// (<see cref="EnsureMaterialized"/>) and refilled while any of them has a player
        /// (<see cref="RespawnDuePoints"/>). A list rather than a per-map index: there are a few dozen, and
        /// the per-map question is asked once per map per roster, on its first entry.</summary>
        private readonly List<Spawn> _roomPoints = new();
        private readonly Dictionary<ushort, List<SpawnGroup>> _groups = new();   // map -> its batch spawn groups
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
        /// <summary>When each map-entry spawn fault last had its stack written, in unix seconds
        /// (<see cref="AdmitEntryStack"/>). The map-entry path's only shared log state, and it lives here, under
        /// the lock, rather than in a <see cref="FaultThrottle{TKey}"/>: entries come from every session thread,
        /// and that type has one owner thread. Bounded by the content (one entry per row kind, exception type,
        /// map and creature) and emptied by <see cref="Clear"/> on <c>@reload</c>. The tick never reads it.</summary>
        private readonly Dictionary<PhaseFaultKey, long> _entryStacks = new();
        /// <summary>An entry fault's stack is written again once its last one is this old: an hour, the span the
        /// tick's throttle re-stacks a fault that never stops (<c>MobFaultRestackBeats</c>).</summary>
        private const long EntryRestackSec = 3600;

        /// <summary>Build the <c>_spawns</c> + <c>_groups</c> rosters from the current <see cref="Content.Spawns"/>
        /// and <see cref="Content.AreaSpawns"/>. Caller holds <c>_lock</c> and has already cleared both if
        /// rebuilding. Shared by startup <see cref="PopulateSpawns"/> and the live <see cref="RebuildPopulation"/>.</summary>
        internal (int points, int skipped, int groups, int capped) Build()
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            int points = 0, skipped = 0, capped = 0;

            // Area rows fork by system (see AreaSpawnDef.Timer): a timer means one member of a batch group, no
            // timer means the trap supplement, which stays on the point model it was ported to. The groups are
            // built first, so a boss point filed below can see a batch group of its own creature on its rooms'
            // maps (AddSpawn, #333); the trap points are filed after the Spawns.csv points, as they always were,
            // so every map's point list keeps its order.
            var byGroup = new Dictionary<(ushort Map, int Group), SpawnGroup>();
            foreach (var ad in Content.AreaSpawns)
            {
                if (ad.Timer <= 0) continue;                                                 // a trap row: below
                if (!Content.Maps.ContainsKey(ad.Map)) { skipped += ad.Count; continue; }   // unrenderable map
                var def = Content.MobById(ad.MobId);
                if (def is null) { skipped += ad.Count; continue; }                          // unknown mob id

                if (!byGroup.TryGetValue((ad.Map, ad.Group), out var g))
                {
                    g = new SpawnGroup
                    {
                        Map = ad.Map, TimerSec = ad.Timer,
                        MinX = ad.MinX, MinY = ad.MinY, MaxX = ad.MaxX, MaxY = ad.MaxY,
                    };
                    byGroup[(ad.Map, ad.Group)] = g;
                    if (!_groups.TryGetValue(ad.Map, out var list)) { list = new(); _groups[ad.Map] = list; }
                    list.Add(g);
                }
                g.Members.Add((def, ad.Count));
                capped += ad.Count;
            }

            foreach (var sd in Content.Spawns)
            {
                if (!Content.Maps.ContainsKey(sd.Map)) { skipped++; continue; }   // map the client can't render
                var def = Content.MobById(sd.MobId);
                if (def is null) { skipped++; continue; }                           // unknown mob id

                AddSpawn(sd.Map, new Spawn { Def = def, X = sd.X, Y = sd.Y, RespawnEvery = SpawnTicksFor(def) });
                points++;
            }

            foreach (var ad in Content.AreaSpawns)
            {
                if (ad.Timer > 0) continue;                                                  // a group member: above
                if (!Content.Maps.ContainsKey(ad.Map)) { skipped += ad.Count; continue; }   // unrenderable map
                var def = Content.MobById(ad.MobId);
                if (def is null) { skipped += ad.Count; continue; }                          // unknown mob id

                // RespawnSec > 0 ⇒ a rare trap-ambush boss with its own long delay; the rest of the trap rows
                // respawn on their creature's own MobSpawnTime like any other point.
                int respawnEvery = ad.RespawnSec > 0
                    ? Math.Max(1, ad.RespawnSec * 1000 / TickMs)
                    : SpawnTicksFor(def);
                for (int i = 0; i < ad.Count; i++)
                    AddSpawn(ad.Map, new Spawn
                    {
                        Def = def, Placed = false,
                        MinX = ad.MinX, MinY = ad.MinY, MaxX = ad.MaxX, MaxY = ad.MaxY,
                        RespawnEvery = respawnEvery, Rare = ad.RespawnSec > 0,
                    });
                points += ad.Count;
            }
            return (points, skipped, byGroup.Count, capped);
        }

        /// <summary>A creature's static respawn delay in ticks — RTK's <c>Mobs.MobSpawnTime</c> (seconds), which
        /// is per CREATURE: a town rat is back in 18s, a Mythic elite takes 360. A genuine 0 in that table means
        /// "revive on the next pass", so it floors at one tick rather than being read as "unset".</summary>
        private static int SpawnTicksFor(MobDef def) =>
            def.SpawnTime > 0 ? Math.Max(1, def.SpawnTime * 1000 / TickMs) : 1;

        /// <summary>When a dead point may next refill: its creature's own delay (or a rare boss's long override),
        /// plus up to +50% jitter for a rare boss so it comes back as an irregular surprise rather than on a
        /// predictable clock. Points only — a batch group's clock is <see cref="SpawnGroup.NextBatchUnix"/>.</summary>
        private long NextRespawnTick(Spawn sp)
        {
            if (sp.RespawnEvery <= 0) return world._tick + RespawnTicks;
            return world._tick + sp.RespawnEvery + (sp.Rare ? Random.Shared.Next(sp.RespawnEvery / 2 + 1) : 0);
        }

        /// <summary>File a spawn point whose row names <paramref name="mapId"/>. Caller holds <c>_lock</c>.
        ///
        /// <para><b>A point whose creature has rooms is filed under its rooms' maps (#333).</b> Its creature
        /// is never built on the row's map: <see cref="Materialize"/> builds it in a room picked at random from
        /// its <c>MobSpawnRules.csv</c> row. Filed under the row's map, as every point was before, the bosses
        /// whose rows sit on map 4 (the NPC Chamber, which nobody enters; every <c>Spawns.csv</c> point with
        /// rooms is one of them) did not exist until someone entered map 4, and a killed one came back only
        /// while someone stood there. Filed under the rooms' maps, the boss is built on the first entry to any
        /// of them and refilled while any of them has a player: the normal rules of a point, applied where the
        /// creature actually lives. Every other point is filed under its row's map, as before.</para>
        ///
        /// <para><b>Unless a batch group already places its creature in its rooms.</b> Then the point stays
        /// filed under its row's map, as before #333. Gim Yi's Den (1308) is the one case: <c>gimyi</c>'s room
        /// is 1308:2:5 and the Den's own group (<c>AreaSpawns.csv</c>) keeps one Gim Yi anywhere on it. Filed
        /// under 1308, the point added a second on every first entry, because a map's groups fill before its
        /// points. Caleb's word (2026-10-21): one Gim Yi in its den, as before. The test is the creature's id
        /// among the members of a group on one of its rooms' maps, which <see cref="Build"/> files before any
        /// point for this.</para></summary>
        private void AddSpawn(ushort mapId, Spawn sp)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            sp.Map = mapId;
            var rooms = RoomMapsOf(sp.Def);
            if (rooms.Length > 0 && !GroupedInItsRooms(sp.Def.Id, rooms)) { sp.RoomMaps = rooms; _roomPoints.Add(sp); return; }
            if (!_spawns.TryGetValue(mapId, out var list)) { list = new(); _spawns[mapId] = list; }
            list.Add(sp);

            bool GroupedInItsRooms(int creature, ushort[] maps)
            {
                foreach (var m in maps)
                    if (_groups.TryGetValue(m, out var groups))
                        foreach (var g in groups)
                            foreach (var (def, _) in g.Members)
                                if (def.Id == creature) return true;
                return false;
            }
        }

        /// <summary>The maps a creature's rooms are on, each once and in its rule's order, counting only the
        /// rooms a boss may be built in (<see cref="UsableRoomMaps"/>); empty for a creature with no rooms, or
        /// none left, which is then filed under its row's map. Reads only <see cref="Content"/>.
        ///
        /// <para>Never throws for a creature <see cref="Materialize"/> would refuse. <see cref="Build"/> has no
        /// guard per row, so a throw here would cost the whole roster, where the same row refused by
        /// <see cref="Materialize"/> costs only itself (the row guards, <c>Tests/SpawnRowGuardTests.cs</c>,
        /// whose bad creature has no key): a creature with no key has no rule, and is filed under its row's
        /// map.</para></summary>
        private static ushort[] RoomMapsOf(MobDef def)
        {
            string? key = def.Key;
            if (key is null || !Content.MobSpawnRules.TryGetValue(key, out var rule)) return Array.Empty<ushort>();
            return UsableRoomMaps(rule.Rooms);
        }

        /// <summary>The maps of the <see cref="UsableRoom"/>s among <paramref name="rooms"/>, each once and in
        /// their order: what a point with those rooms is filed under. Internal so the pick fact
        /// (<c>Tests/SpawnDirectorTests.cs</c>) can hand it a room on a map no rule has today.</summary>
        internal static ushort[] UsableRoomMaps((ushort Map, ushort X, ushort Y)[] rooms)
        {
            if (rooms.Length == 0) return Array.Empty<ushort>();
            var maps = new List<ushort>(rooms.Length);
            foreach (var room in rooms)
                if (UsableRoom(room) && !maps.Contains(room.Map)) maps.Add(room.Map);
            return maps.ToArray();
        }

        /// <summary>May a boss be built in this room (#333)? Its map is one the client renders (a <c>Maps.csv</c>
        /// row, <see cref="Content.Maps"/>), and the room is not off that map (<see cref="OffItsMap"/>). The one
        /// test both the filing (<see cref="UsableRoomMaps"/>) and the pick (<see cref="PickRoom"/>) ask, so the
        /// pick never lands in a room the filing left out. Reads only <see cref="Content"/>.
        ///
        /// <para>They used to ask two: the filing skipped both kinds of room, the pick only the room off its map,
        /// and <see cref="Materialize"/> dropped a drawn room on an unrenderable map after the draw. A point whose
        /// rooms included one built its creature, on that draw, at its row's tile on its row's map: map 4 for
        /// the map-4 bosses, a map the point is not filed under, where it stood until an <c>@reload</c>
        /// (#357's review, F3). No rule has such a room today (<c>Tests/ContentSmokeTests.cs</c> requires every
        /// room's map to render); this keeps one added later from stranding its boss.</para></summary>
        private static bool UsableRoom((ushort Map, ushort X, ushort Y) room) =>
            Content.Maps.ContainsKey(room.Map) && !OffItsMap(room);

        /// <summary>Is a boss room so far outside its map that no tile of the map lies within
        /// <see cref="SpawnTileRadius"/> of it (#333)? Such a room is not a <see cref="UsableRoom"/>, so it is
        /// left out of the filing and of the pick.
        ///
        /// <para><c>FreeSpawnTile</c> looks that far past a spawn tile for an open tile on the map and, finding
        /// none, builds the creature on the spawn tile itself: for a room this far out, outside the map, where
        /// nobody can reach or kill it, so its point never refills until an <c>@reload</c>. One room is like
        /// that today: <c>monkey_avenger</c>'s 4164:24:32, which is RTK's warp, on a map the 4.95 client draws
        /// 20x30. A room only just outside its map keeps its place in both, because a tile of the map is within
        /// reach and <c>FreeSpawnTile</c> builds the boss there: <c>divine_sheep</c>'s 3472:11:8 and
        /// <c>sheep_avenger</c>'s 4472:10:8, both on 10-wide maps. Caleb's rule (2026-10-21): skip only a room
        /// with no tile on its map within 2 of it. A map with no dimensions has no edge to be outside of. Reads
        /// only <see cref="Content"/>.</para></summary>
        private static bool OffItsMap((ushort Map, ushort X, ushort Y) room) =>
            Content.Maps.TryGetValue(room.Map, out var mi) && mi.Xs > 0 && mi.Ys > 0
            && (room.X >= mi.Xs + SpawnTileRadius || room.Y >= mi.Ys + SpawnTileRadius);

        /// <summary>A boss's room picked at random, uniformly among the rooms it may be built in
        /// (<see cref="UsableRoom"/>), which are the rooms whose maps its point is filed under; null when there are
        /// none, and such a point is filed under its row's map. With every room usable this is the single draw
        /// over all of them it always was: the same <c>Random.Shared.Next</c> over the same count, landing on the
        /// same room. Internal so the pick fact (<c>Tests/SpawnDirectorTests.cs</c>) can hand it a room on a map
        /// no rule has today.</summary>
        internal static (ushort Map, ushort X, ushort Y)? PickRoom((ushort Map, ushort X, ushort Y)[] rooms)
        {
            int usable = 0;
            foreach (var room in rooms) if (UsableRoom(room)) usable++;
            if (usable == 0) return null;
            int pick = Random.Shared.Next(usable);
            foreach (var room in rooms)
                if (UsableRoom(room) && pick-- == 0) return room;
            return null;   // not reached: `pick` is below the count of rooms the loop accepts
        }

        /// <summary>Warm the terrain of every map that a boss with a room on one of <paramref name="watched"/>
        /// may be built on, from a caller that holds NO lock (#333).
        ///
        /// <para>A boss's point is filed under all its rooms' maps and its creature is built in a room picked
        /// at random among them, so a player on one of them can be what builds it on another: on that map's
        /// first entry, on the tick's respawn pass, or on <c>@reload</c>'s re-materialise. The placement reads
        /// the terrain of the map it builds on (<c>FreeSpawnTile</c> -&gt; <c>MapData.For</c>), which on a miss
        /// is a disk read, a cell decode and a SQLite query. Each of those three callers warms the maps it
        /// is about to touch before it takes <c>_lock</c> (<c>World.EnterMap</c>, <c>World.Tick</c>'s step
        /// (0), <c>World.ReloadFromDisk</c>), and calls this beside that warm with the same maps, so the
        /// rooms' maps are a cache hit under the lock as theirs are.</para>
        ///
        /// <para>Reads only <see cref="Content"/> and the terrain cache, like <see cref="PlacementBox"/>. It
        /// asks the rules, not the roster (which is under <c>_lock</c>): a rule whose creature has no point
        /// costs a warm nobody needed, never a cold read anyone did. A map with no <c>Maps.csv</c> row, and a
        /// map already cached, costs a dictionary probe (<see cref="MapData.Prewarm"/>).</para></summary>
        internal static void PrewarmRoomMaps(ReadOnlySpan<ushort> watched)
        {
            if (watched.IsEmpty) return;
            foreach (var rule in Content.MobSpawnRules.Values)
            {
                var rooms = rule.Rooms;
                for (int i = 0; i < rooms.Length; i++)
                {
                    if (!watched.Contains(rooms[i].Map)) continue;
                    foreach (var room in rooms) MapData.Prewarm(room.Map);
                    break;
                }
            }
        }

        /// <summary>Instantiate a map's spawn roster the first time anyone enters it (idempotent), and top up its
        /// due batch groups, ambush traps and cold tiles on every entry. Until this runs the map's mobs don't
        /// exist, so a newcomer must trigger it BEFORE the room's mob list is read for them. Called by
        /// <c>World.EnterMap</c> with the newcomer's tile, and by <c>@reload</c>'s re-materialise
        /// (<c>World.RebuildPopulation</c>) with none. Caller holds <c>_lock</c>.
        ///
        /// <para><b>On map entry, one guard per spawn row, as on the tick.</b> A group member whose fill
        /// throws, or a point whose <see cref="Materialize"/> throws, costs only itself: it is returned as a
        /// fault, the walk goes on to the next member, group and point, and the player is added to the map as
        /// usual. Before the guard (the #292 follow-up) the throw left <c>World.EnterMap</c> with the player on
        /// no map's roster; and because a group with a bad member stays due, every later entry threw the same
        /// way, so one broken group member made its map impossible to enter. The group with the bad member
        /// does not stamp its clock, the tick path's rule (<see cref="RefillGroups"/>), so the tick retries it
        /// once someone is on the map. A point that threw keeps no clock, so nothing retries it until an
        /// <c>@reload</c> rebuilds the roster: before the guard that was the fate of every point after the
        /// bad one, and now it is the bad point's alone.</para>
        ///
        /// <para><b>Logged by the entering thread, never through the tick's throttle.</b> That throttle
        /// (<see cref="FaultThrottle{TKey}"/>, behind <c>LogPhaseFaults</c>) has one owner, the tick thread,
        /// and this runs on a session thread. <c>World.EnterMap</c> writes the returned faults itself, after it
        /// releases the lock (<c>World.LogEntryFaults</c>). The one decision that needs state shared between
        /// entries, whether a fault's stack is due, is made here, under the lock every entry already holds
        /// (<see cref="AdmitEntryStack"/>); nothing the tick reads or writes is involved. Null when nothing
        /// threw, which is every ordinary entry: that path allocates nothing for the guard.</para>
        ///
        /// <para><b><c>@reload</c> keeps the unguarded walk.</b> With no <paramref name="arriving"/> the catch
        /// filters are false, so a row that throws there throws out of the rebuild, as it always has, and the
        /// return is always null. Guarding it is a follow-up of its own: its faults would have to be written by
        /// <c>RebuildPopulation</c> after it releases the lock, and that method is outside this change.</para>
        ///
        /// <para><paramref name="arriving"/> is the entering player's tile. They are not on the map's player list
        /// until this returns, so the three placements below that avoid that list's tiles (the group fill, the
        /// ambush traps, the cold tiles) are handed their tile as taken (#122). The points are not:
        /// <see cref="Materialize"/> places through <c>FreeSpawnTile</c>, which has never looked at players on
        /// any path.</para></summary>
        internal List<EntryFault>? EnsureMaterialized(ushort mapId, (int X, int Y)? arriving = null)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            bool entry = arriving is not null;   // a newcomer is what makes this the map-entry path, and guarded
            List<PhaseFault>? rows = null;       // stays null on @reload: the filters are false, nothing is caught

            // Batch groups get a chance to fill BEFORE the entering player's room list is read, so a room whose
            // timer came due while nobody was in it is already full when they walk in rather than popping into
            // existence around them. Not inside the _materialized guard: this has to be reconsidered on every
            // entry, since that is the only moment a due group on an unwatched map gets looked at.
            RefillGroups(mapId, guarded: entry, ref rows, arriving);
            world.RefillAmbushLocked(mapId, arriving);   // top up this map's hidden ambush traps (also every entry, same reason)
            world.RefillFrigidLocked(mapId, arriving);   // …and Sute's Cave's hidden cold tiles (Server/SuteAi.cs)

            if (_materialized.Add(mapId))   // first entry only
            {
                if (_spawns.TryGetValue(mapId, out var list))
                    foreach (var sp in list) OnFirstEntry(mapId, sp);

                // The bosses filed under this map as one of their rooms' maps (#333, AddSpawn). Built on the first
                // entry to ANY of their maps, so one already built, or pending on its clock, from another of them
                // is left to the tick's respawn pass: built twice it would be two bosses on one point. One whose
                // Materialize threw is neither, so the first entry to another of its maps tries it again. Asked
                // about under its row's map, as it always was; Materialize then picks its room, which may be on
                // another of its maps than this one (the rooms' maps are warm: see PrewarmRoomMaps).
                foreach (var sp in _roomPoints)
                    if (sp.Live is null && sp.RespawnTick == 0 && Array.IndexOf(sp.RoomMaps!, mapId) >= 0)
                        OnFirstEntry(sp.Map, sp);
            }

            if (rows is null) return null;                      // the ordinary entry: nothing threw
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var faults = new List<EntryFault>(rows.Count);
            foreach (var f in rows) faults.Add(new EntryFault(f, AdmitEntryStack(f, now), mapId));
            return faults;

            // One point on the map's first entry, for the map's own points and the bosses filed under it alike.
            // rosterMap is what Materialize is asked about and what a fault records and is throttled under: the
            // entered map for its own points, the row's map for a boss filed under its rooms. The log names the
            // entered map as well (EntryFault.Entered), so a boss's fault is not reported against map 4.
            void OnFirstEntry(ushort rosterMap, Spawn sp)
            {
                // A rare boss doesn't appear the instant someone walks in — it's a surprise. Leave it pending
                // with a random first-appearance somewhere in its respawn window; the Tick refill loop spawns it
                // once due (and only while the map is being hunted, since that loop skips empty maps).
                if (sp.Rare) { sp.RespawnTick = world._tick + 1 + Random.Shared.Next(sp.RespawnEvery); return; }
                // The point guard, phase (1)'s shape. The map is already marked materialised, so a point this
                // skips is not walked again: it costs itself, and only itself.
                try { Materialize(rosterMap, sp); }
                catch (Exception e) when (entry) { (rows ??= new()).Add(new PhaseFault(PhRespawns, e, Row: true, rosterMap, sp.Def)); }
            }
        }

        /// <summary>Whether a map-entry fault's stack is written on this entry: yes the first time the fault is
        /// met on any entry, and again once its last stack is <see cref="EntryRestackSec"/> old (a log rotation
        /// must not leave its named repeats pointing at a stack that is no longer on disk), and again after an
        /// <c>@reload</c>, which empties the record (<see cref="Clear"/>). Otherwise the entry names the fault
        /// without its stack. Keyed as the tick keys its row faults: the row kind, the exception type, the
        /// roster map and the creature's content id. Caller holds <c>_lock</c>.</summary>
        private bool AdmitEntryStack(PhaseFault f, long now)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            var key = new PhaseFaultKey(f.Phase, f.Error.GetType(), Row: true, f.Map, f.Def?.Id ?? 0);
            if (_entryStacks.TryGetValue(key, out long last) && now - last < EntryRestackSec) return false;
            _entryStacks[key] = now;
            return true;
        }

        /// <summary>Run every due batch group on one map: RTK's <c>spawnMob</c>. Caller holds <c>_lock</c>.
        ///
        /// The shape is RTK's, and each part of it is load-bearing:
        ///   * one clock per GROUP, not per mob and never per kill — so killing something brings nothing back,
        ///     and a cleared room is genuinely cleared until the timer comes round;
        ///   * the live count is taken MAP-WIDE (RTK's <c>getObjectsInMap</c> filtered by mob id), not within the
        ///     box, so survivors count against the cap and two groups naming the same creature can't stack it;
        ///   * refill tops up to the cap and stops — never above it, never a fixed number per cycle;
        ///   * the clock is stamped when the batch RUNS, so it is the refill cycle that paces the room, not how
        ///     fast the room was emptied.
        /// Deliberately NOT ported from RTK: its <c>deleteMob</c> (which wipes a group off a player-free map, so
        /// mobs you walked away from are gone when you return) and its accelerator (which pulls the clock back
        /// 10s every 10s on a mob-free map, roughly halving the wait on a fully cleared room). Continuity across
        /// a visit is worth more than the re-randomisation, and a cleared room is supposed to stay dead.
        ///
        /// <para><b>One guard per group member, on the tick's path and on map entry.</b> With
        /// <paramref name="guarded"/> (phase (1.1), <see cref="RefillDueGroups"/>, and
        /// <see cref="EnsureMaterialized"/> when a newcomer is entering) a member whose <see cref="FillMember"/>
        /// throws is recorded in <paramref name="faults"/> and the batch goes on to the next member, the next
        /// group and the next map. Before the guard the throw ended the whole phase, so every member and every
        /// group after the bad one waited too, for as long as the content stayed broken.</para>
        ///
        /// <para><b>A group with a member that threw does not stamp its clock</b>, which is what the unguarded
        /// throw did too: the batch did not finish. So the group stays due and is run again on the next
        /// sampled beat, its good members topped up and its bad one retried, exactly as the members before
        /// the bad one were before this guard. Stamping the clock anyway would put the good members back on
        /// the group's timer — arguably what a batch should do — but it would change how often they refill on
        /// beats where nothing throws, which is a decision this guard does not make. Nothing else is unwound:
        /// whatever the member placed before it threw stands, as it did.</para>
        ///
        /// <para>The map-entry path records its faults in the same list shape, but they are written by the
        /// entering thread after it releases the lock (<c>World.LogEntryFaults</c>), never through the throttle
        /// behind <c>LogPhaseFaults</c>, whose one owner is the tick thread. Unguarded only on <c>@reload</c>'s
        /// re-materialise (<see cref="EnsureMaterialized"/> with no newcomer): the filter below is false there,
        /// so a throw leaves this method exactly as it always did.</para>
        ///
        /// <para><paramref name="arriving"/>: the entering player's tile on the map-entry path, taken for the
        /// whole fill (#122); null on the tick's and on <c>@reload</c>'s, whose players are all on the map's
        /// list already.</para></summary>
        private void RefillGroups(ushort mapId, bool guarded, ref List<PhaseFault>? faults, (int X, int Y)? arriving)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            if (!_groups.TryGetValue(mapId, out var groups)) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            HashSet<(int, int)>? taken = null;               // built once, only if something is actually due
            foreach (var g in groups)
            {
                if (now < g.NextBatchUnix) continue;
                taken ??= world.OccupiedTiles(mapId, arriving);
                bool threw = false;
                foreach (var (def, cap) in g.Members)
                {
                    try { FillMember(g, def, cap, taken); }
                    catch (Exception e) when (guarded)
                    {
                        threw = true;
                        (faults ??= new()).Add(new PhaseFault(PhRefills, e, Row: true, g.Map, def));
                    }
                }
                if (!threw) g.NextBatchUnix = now + g.TimerSec;
            }
        }

        /// <summary>Top one creature in a group back up to its cap, placing each new mob on a freshly-rolled tile
        /// inside the group's box. Caller holds <c>_lock</c>.
        ///
        /// <para><b>The cap is a guarantee, not an average.</b> RTK rolls a random tile at a time and gives up
        /// after <c>maxMobs[z] * 4</c> failures (mobSpawnHandler.lua:3003), which is a SPIN guard — but it doubles
        /// as a coverage guard, and that second job it does badly. The failure is invisible at a big cap and total
        /// at a small one: 15 Yachi get 60 rolls to find 15 tiles and never come up short, while <b>Sute</b> — cap
        /// 1, a zero box, so every roll is uniform over the whole 30x30 nest of which 52% is walkable — gets FOUR
        /// coin flips and is simply absent from a measured <b>6.9%</b> of refills (2000 trials against the real
        /// map). One boss in fourteen visits, and because the rest of the room hits its caps every time it reads
        /// as "everything spawned except him", for the full 300s until the group's clock next comes round.</para>
        ///
        /// <para>So the roll stays as the cheap, RTK-shaped common path, and when it runs out of budget still
        /// short we <see cref="OpenTiles">enumerate</see> the box instead of shrugging: if a tile exists, the
        /// creature is placed. The point-spawn system has always worked this way — <see cref="PickAreaHome"/>
        /// falls back to the box centre and <see cref="FreeSpawnTile"/> to the spawn tile itself, so a point
        /// spawn cannot silently vanish. This is the batch system agreeing with it.</para>
        ///
        /// <para>Cost of the fallback is one pass over the box, and only for a member the rolls left short — so
        /// the ordinary refill of a room that has space never reaches it.</para></summary>
        private void FillMember(SpawnGroup g, MobDef def, int cap, HashSet<(int, int)> taken)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            var map = world.Map(g.Map);
            int alive = 0;
            foreach (var m in map.Mobs)
                if (m.Alive && !m.IsNpc && m.DefId == def.Id) alive++;
            if (alive >= cap) return;                       // already full — this is the no-overfill guarantee

            // RTK's give-up rule: a fixed budget of failed placements for the whole top-up, so a box that is
            // mostly wall (or a room the party is standing in) ends the attempt instead of spinning.
            int budget = cap * PlacementTriesPerMob;
            while (alive < cap && budget-- > 0)
            {
                if (!TryPickGroupTile(g, taken, out var x, out var y)) continue;
                world.BuildMob(g.Map, def, x, y);
                taken.Add((x, y));
                alive++;
            }
            if (alive >= cap) return;                       // the rolls did it — the overwhelmingly common case

            // Still short: ask the map what is actually free rather than rolling again. Picks are drawn at random
            // from the list (swap-remove, so each remaining tile stays equally likely) — the fallback must not
            // clump the leftovers into whatever corner the scan happens to start in.
            var open = OpenTiles(g.Map, taken, g.MinX, g.MinY, g.MaxX, g.MaxY);
            while (alive < cap && open.Count > 0)
            {
                int i = Random.Shared.Next(open.Count);
                var (x, y) = open[i];
                open[i] = open[^1]; open.RemoveAt(open.Count - 1);
                world.BuildMob(g.Map, def, x, y);
                taken.Add((x, y));
                alive++;
            }
        }

        /// <summary>Create the live mob for a spawn point and register it. Caller holds <c>_lock</c>.</summary>
        private void Materialize(ushort mapId, Spawn sp)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            var d = sp.Def;
            Content.MobSpawnRules.TryGetValue(d.Key, out var rule);

            // Population cap (RTK strange_thing's on_spawn, which counts its own kind across two maps and
            // vanishes if one is already out there). Checked before anything is built: the spawn point simply
            // doesn't fire, and will try again on its next refill.
            if (rule is { MaxAlive: > 0 })
            {
                int alive = 0;
                foreach (var capMap in rule.CapMaps.Length > 0 ? rule.CapMaps : new[] { mapId })
                    if (world._maps.TryGetValue(capMap, out var cm))
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
            // among the boss's rooms). The spawn POINT keeps its row's map — respawn bookkeeping is keyed to it,
            // and it is the mapId this is called with — but it is filed under its rooms' maps (AddSpawn, #333),
            // and the creature is built in the room it belongs in. PickRoom draws only among the rooms the filing
            // keeps (UsableRoom): never a room on a map the client cannot render, nor one off its map. With none
            // left there is no pick, and the creature is built at its row's tile on its row's map, which is where
            // such a point is filed.
            if (rule is { Rooms.Length: > 0 } && PickRoom(rule.Rooms) is { } room)
            { mapId = room.Map; sp.X = room.X; sp.Y = room.Y; sp.Placed = true; }

            // Area spawn's first materialize: choose a walkable home tile inside its box (or anywhere on the map
            // for a zero box). Fixed once, so respawns hug the same patch like RTK's sentries.
            if (!sp.Placed) { (sp.X, sp.Y) = PickAreaHome(mapId, sp); sp.Placed = true; }
            // Don't stack: several RTK spawn points share a tile, and a respawn can land where another mob has
            // wandered. Place on the spawn tile if free, else the nearest open one (home stays the spawn tile).
            var (sx, sy) = world.FreeSpawnTile(mapId, sp.X, sp.Y);
            var mob = world.BuildMob(mapId, d, sx, sy, sp.X, sp.Y);
            _mobSpawn[mob.Id] = sp;
            sp.Live = mob;
            sp.RespawnTick = 0;
        }

        /// <summary>Roll one placement tile for a batch group, or false if this attempt failed (the caller retries
        /// against its budget, then falls back to <see cref="OpenTiles"/>). RTK's own test, tile for tile:
        /// passable ground, nobody standing there — player or mob — and not a warp tile, since a creature parked
        /// on a warp is one a player can't avoid walking into. Caller holds <c>_lock</c>.</summary>
        private bool TryPickGroupTile(SpawnGroup g, HashSet<(int, int)> taken, out ushort x, out ushort y)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            x = y = 0;
            var (minX, minY, maxX, maxY) = PlacementBox(g.Map, g.MinX, g.MinY, g.MaxX, g.MaxY);
            if (maxX < minX || maxY < minY) return false;

            int tx = Random.Shared.Next(minX, maxX + 1), ty = Random.Shared.Next(minY, maxY + 1);
            if (!Placeable(g.Map, taken, tx, ty)) return false;

            x = (ushort)tx; y = (ushort)ty;
            return true;
        }

        /// <summary>A spawn box clamped to the map, as RTK clamps it: an ALL-ZERO box means "anywhere on the
        /// map" (that is what every <c>handleSpawn</c> row extracts to, since RTK's spawner takes no box at all),
        /// and a box that overhangs the edge is cut to it. Returns a box with <c>maxX &lt; minX</c> for a map with
        /// no dimensions — i.e. nothing to place on.</summary>
        public static (int minX, int minY, int maxX, int maxY) PlacementBox(ushort mapId, int minX, int minY, int maxX, int maxY)
        {
            var (xs, ys) = Content.Maps.TryGetValue(mapId, out var mi) ? (mi.Xs, mi.Ys) : ((ushort)0, (ushort)0);
            if (xs == 0 || ys == 0) return (0, 0, -1, -1);
            if (minX == 0 && minY == 0 && maxX == 0 && maxY == 0) { maxX = xs - 1; maxY = ys - 1; }
            return (minX, minY, Math.Min(maxX, xs - 1), Math.Min(maxY, ys - 1));
        }

        /// <summary>May a creature be placed on this tile right now? Walkable ground, nobody already standing on
        /// it, and not a warp tile.</summary>
        public static bool Placeable(ushort mapId, IReadOnlySet<(int, int)> taken, int x, int y) =>
            Placeable(mapId, TerrainOf(mapId), taken, x, y);

        /// <summary>The tile test with the terrain already in hand — <see cref="OpenTiles"/> resolves the map
        /// once and runs this per cell rather than re-entering the <see cref="MapData"/> cache thousands of
        /// times. Tests are ordered cheapest-first: array index, then hash probe, then dictionary probe. A map
        /// with no <c>.map</c> file has no ground to reject on, which is the pre-existing rule.</summary>
        private static bool Placeable(ushort mapId, MapData? terrain, IReadOnlySet<(int, int)> taken, int x, int y)
        {
            if (terrain is not null && terrain.Solid(x, y)) return false;   // wall (and out-of-bounds)
            if (taken.Contains((x, y))) return false;                       // a mob or a player is standing there
            return !Content.TryWarp(mapId, (ushort)x, (ushort)y, out _);    // a creature parked on a warp is unavoidable
        }

        private static MapData? TerrainOf(ushort mapId) =>
            Content.Maps.TryGetValue(mapId, out var mi) && mi.Xs > 0 && mi.Ys > 0 ? MapData.For(mapId, mi.Xs, mi.Ys) : null;

        /// <summary>Every tile of a spawn box a creature could stand on right now. This is what turns a group's
        /// cap into a guarantee — see <see cref="FillMember"/> for why the random roll alone isn't one — and it
        /// is deliberately exhaustive rather than sampled: the whole point is that it cannot come up empty while
        /// a free tile exists.</summary>
        public static List<(ushort X, ushort Y)> OpenTiles(ushort mapId, IReadOnlySet<(int, int)> taken,
                                                          int minX, int minY, int maxX, int maxY)
        {
            var open = new List<(ushort, ushort)>();
            var (x0, y0, x1, y1) = PlacementBox(mapId, minX, minY, maxX, maxY);
            var terrain = TerrainOf(mapId);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (Placeable(mapId, terrain, taken, x, y)) open.Add(((ushort)x, (ushort)y));
            return open;
        }

        /// <summary>Pick a random walkable home tile for an area spawn: inside its box, or anywhere on the map
        /// when the box is zero (RTK's "no bounds" form). Samples a handful of random tiles and takes the first
        /// walkable one; falls back to the box centre if the patch is dense/unloaded. Caller holds <c>_lock</c>.</summary>
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

        // ---- the tick's two sweeps (World.Tick phases (1) and (1.1)) ---------------------------------

        /// <summary>Phase (1): refill any due spawn point on a map someone is watching. Points only — the
        /// hunting maps refill in batches in <see cref="RefillDueGroups"/>, not one mob at a time as they
        /// die. Caller holds <c>_lock</c>.
        ///
        /// <para><b>One guard per due point.</b> A point whose <see cref="Materialize"/> throws — a bad
        /// content row, typically — costs only itself: it is recorded in <paramref name="faults"/> as a row
        /// fault and the walk goes on to the next point, on this map and on every map after it. Before this
        /// guard the throw ended the whole phase, so every point after the bad one waited, on its own map and
        /// on the maps visited after it, for as long as the content stayed broken.</para>
        ///
        /// <para>Nothing is unwound. A point that threw keeps the clock it had, so it is due again next beat
        /// and throws again until the content is fixed — exactly as it did before, when it was the first
        /// thing each beat's phase (1) reached — and whatever <see cref="Materialize"/> did before the throw
        /// stands. Nothing is logged here (#109): <c>faults</c> is the tick's own list, written after the
        /// lock is released by <c>LogPhaseFaults</c>. Plain try/catch, no closure: a beat with no throw
        /// allocates nothing and leaves <c>faults</c> as it found it.</para></summary>
        internal void RespawnDuePoints(long tick, ref List<PhaseFault>? faults)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            foreach (var (mapId, list) in _spawns)
            {
                if (!world._maps.TryGetValue(mapId, out var pm) || pm.Players.Count == 0) continue;
                foreach (var sp in list)
                {
                    if (sp.Live is not null || sp.RespawnTick == 0 || tick < sp.RespawnTick) continue;
                    try { Materialize(mapId, sp); }
                    catch (Exception e) { (faults ??= new()).Add(new PhaseFault(PhRespawns, e, Row: true, mapId, sp.Def)); }
                }
            }

            // The bosses filed under their rooms' maps (#333): each walked once, and refilled while ANY of its
            // rooms' maps has a player, wherever its room pick then puts it. Asked about under its row's map,
            // as on its first entry (EnsureMaterialized); the same guard, the same fault, recorded and throttled
            // under that row's map. The log names the room map a player is on beside it (PhaseFault.RoomMap), so
            // a boss's fault is not reported against map 4, as a map-entry fault names the map entered.
            foreach (var sp in _roomPoints)
            {
                if (sp.Live is not null || sp.RespawnTick == 0 || tick < sp.RespawnTick) continue;
                if (!AnyPlayerOn(sp.RoomMaps!)) continue;
                try { Materialize(sp.Map, sp); }
                catch (Exception e) { (faults ??= new()).Add(new PhaseFault(PhRespawns, e, Row: true, sp.Map, sp.Def, FirstMapWithAPlayer(sp.RoomMaps!))); }
            }

            bool AnyPlayerOn(ushort[] maps)
            {
                foreach (var m in maps)
                    if (world._maps.TryGetValue(m, out var pm) && pm.Players.Count > 0) return true;
                return false;
            }

            // AnyPlayerOn's walk, naming the map it stopped at: the room map the pass is serving. Asked only
            // when the build threw, so a beat that throws nothing does no more than it did.
            ushort? FirstMapWithAPlayer(ushort[] maps)
            {
                foreach (var m in maps)
                    if (world._maps.TryGetValue(m, out var pm) && pm.Players.Count > 0) return m;
                return null;
            }
        }

        /// <summary>Phase (1.1): every due spawn group on a map someone is hunting (RTK's spawner NPC, whose
        /// own <c>#pc &gt; 0</c> test this mirrors). A map nobody is on is skipped here and caught by
        /// <see cref="EnsureMaterialized"/> when someone walks in. Sampled every <c>BatchSweepTicks</c> —
        /// these clocks are in whole seconds and the shortest is 2s, so there is nothing to gain from looking
        /// every beat. Caller holds <c>_lock</c>.
        ///
        /// <para>Guarded per group member (see <see cref="RefillGroups"/>): a member whose fill throws is
        /// recorded in <paramref name="faults"/> and costs only itself.</para></summary>
        internal void RefillDueGroups(long tick, ref List<PhaseFault>? faults)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            if (tick % BatchSweepTicks != 0) return;
            foreach (var (mapId, _) in _groups)
            {
                if (!world._maps.TryGetValue(mapId, out var pm) || pm.Players.Count == 0) continue;
                RefillGroups(mapId, guarded: true, ref faults, arriving: null);
            }
        }

        // ---- the death path's spawn bookkeeping (World.TryDamage, World.DespawnMob) ---------------------

        /// <summary>A creature was killed: stamp the death registry if its creature is gated on one, and free
        /// its spawn point. Caller holds <c>_lock</c>.</summary>
        internal void RecordDeath(ushort mapId, Mob mob)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            // Stamp the death registry for creatures gated on it (RTK's boss after_death hooks). Written
            // here rather than in the Lua hook so the clock starts on the kill itself, not on whether a
            // script happens to be loaded.
            if (Content.MobSpawnRules.TryGetValue(mob.Key, out var dRule) && dRule.DeathCooldownSec > 0)
                _lastDeath[(mapId, mob.Key)] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ReleasePoint(mob);
        }

        /// <summary>A creature left the map without a kill (ridden away) or with one: if it was a spawn
        /// POINT's mob the point frees up and starts its creature's respawn clock; a batch-group mob has no
        /// point to free. Caller holds <c>_lock</c>.</summary>
        internal void ReleasePoint(Mob mob)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            if (_mobSpawn.TryGetValue(mob.Id, out var sp))
            {
                // A spawn POINT frees up and starts its creature's respawn clock. A batch-group mob has
                // no point to free: its group refills on its own timer and this death changes nothing.
                sp.Live = null;
                sp.RespawnTick = NextRespawnTick(sp);    // refill shortly (rare bosses: long jittered delay)
                _mobSpawn.Remove(mob.Id);
            }
        }

        /// <summary>Drop both rosters (the points filed under their rooms' maps with them), the materialised set
        /// and the mob-to-point index ahead of a <see cref="Build"/> from re-read content
        /// (<see cref="RebuildPopulation"/>). The death registry is
        /// deliberately kept: a reload is not a world reset for a boss cooldown. Caller holds <c>_lock</c>.
        ///
        /// <para><c>_mobSpawn</c> goes with the rosters (#121). The rebuild has already torn down every
        /// creature it indexes, and mob ids are never reused, so after a reload no lookup can ever hit one of
        /// its entries again; kept, they only held every pre-reload point mob's entry, and the old
        /// <c>Spawn</c> behind it, for the life of the process.</para>
        ///
        /// <para>The record of which map-entry faults have had their stack written goes too: the rows are
        /// re-read, so a row that still throws after a reload is news and writes its stack again.</para></summary>
        internal void Clear()
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            _spawns.Clear();
            _roomPoints.Clear();
            _groups.Clear();
            _materialized.Clear();
            _mobSpawn.Clear();
            _entryStacks.Clear();
        }

        /// <summary>How many maps carry at least one spawn point of their own row / batch group — the start-up
        /// log lines.</summary>
        internal int PointMapCount => _spawns.Count;
        internal int GroupMapCount => _groups.Count;
        /// <summary>How many points are filed under their rooms' maps instead (#333), and on how many maps —
        /// the start-up log line.</summary>
        internal int RoomPointCount => _roomPoints.Count;
        internal int RoomMapCount => _roomPoints.SelectMany(sp => sp.RoomMaps!).Distinct().Count();

        // ---- test seams (Tests/SpawnDirectorTests.cs) ------------------------------------------------
        // Kept beside the rosters they open rather than in the test project, for the same reason as
        // UnderWorldLockForTest: a reader of the roster should be able to see everything that writes it.

        /// <summary>Register one spawn point with a fixed home tile and its own respawn delay, the way
        /// <see cref="Build"/> registers a <c>Spawns.csv</c> row. <paramref name="dueAt"/>, when non-zero,
        /// registers it as a point whose creature is dead and whose respawn clock falls due on that beat, so
        /// the tick's phase (1) materialises it without a map entry first. <paramref name="rare"/> registers
        /// it as a rare boss point (an <c>AreaSpawnsTrap.csv</c> row with a <c>RespawnSec</c>), which a map's
        /// first entry leaves pending rather than materialising. Caller holds <c>_lock</c>.</summary>
        internal void AddPointForTest(ushort mapId, MobDef def, ushort x, ushort y, int respawnEvery, long dueAt = 0, bool rare = false)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            AddSpawn(mapId, new Spawn { Def = def, X = x, Y = y, RespawnEvery = respawnEvery, RespawnTick = dueAt, Rare = rare });
        }

        /// <summary>How many live creatures the mob-to-point index holds, on every map. Caller holds
        /// <c>_lock</c>.</summary>
        internal int MobSpawnIndexCountForTest()
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            return _mobSpawn.Count;
        }

        /// <summary>Register one batch group over the whole map, due immediately, the way <see cref="Build"/>
        /// registers a timed <c>AreaSpawns.csv</c> row. Caller holds <c>_lock</c>.</summary>
        internal void AddGroupForTest(ushort mapId, int timerSec, params (MobDef Def, int Cap)[] members)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            var g = new SpawnGroup { Map = mapId, TimerSec = timerSec };
            foreach (var m in members) g.Members.Add(m);
            if (!_groups.TryGetValue(mapId, out var list)) { list = new(); _groups[mapId] = list; }
            list.Add(g);
        }

        /// <summary>When the map's one test group may next top up (unix seconds). Caller holds <c>_lock</c>.</summary>
        internal long GroupClockForTest(ushort mapId)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            return _groups[mapId].Single().NextBatchUnix;
        }

        /// <summary>Roster reads for the rebuild test. Reads, but reads of state every writer holds the lock
        /// for, so they hold it too. Caller holds <c>_lock</c>.</summary>
        internal int PointCountForTest(ushort mapId)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            return _spawns.TryGetValue(mapId, out var l) ? l.Count : 0;
        }

        internal int GroupCountForTest(ushort mapId)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            return _groups.TryGetValue(mapId, out var l) ? l.Count : 0;
        }

        /// <summary>The creature key of every point filed under a map, one entry per point: the map's own
        /// rows' points and the bosses filed under it as one of their rooms' maps (#333). Caller holds
        /// <c>_lock</c>.</summary>
        internal List<string> PointKeysForTest(ushort mapId)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            var keys = _spawns.TryGetValue(mapId, out var l) ? l.Select(sp => sp.Def.Key).ToList() : new List<string>();
            foreach (var sp in _roomPoints)
                if (Array.IndexOf(sp.RoomMaps!, mapId) >= 0) keys.Add(sp.Def.Key);
            return keys;
        }

        internal bool IsMaterializedForTest(ushort mapId)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            return _materialized.Contains(mapId);
        }

        /// <summary>Overwrite one death-registry stamp, so the death-cooldown test can move a kill into the
        /// past instead of waiting the real half hour out. Caller holds <c>_lock</c>.</summary>
        internal void StampDeathForTest(ushort mapId, string key, long unixSeconds)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            _lastDeath[(mapId, key)] = unixSeconds;
        }

        /// <summary>Undo a test's registrations on one map: its points, groups, materialised flag, death stamps,
        /// map-entry stack stamps and every world-spawned creature on it, so the shared fixture World is as it
        /// was. A boss point filed under its rooms' maps (#333) goes too when its row names this map or it is
        /// filed under this map, with its creature, on whichever of its maps that stands. Caller holds
        /// <c>_lock</c>.</summary>
        internal void ForgetMapForTest(ushort mapId)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            if (_spawns.Remove(mapId, out var points))
                foreach (var sp in points) if (sp.Live is { } live) _mobSpawn.Remove(live.Id);
            _roomPoints.RemoveAll(sp =>
            {
                if (sp.Map != mapId && Array.IndexOf(sp.RoomMaps!, mapId) < 0) return false;
                if (sp.Live is { } live)
                {
                    _mobSpawn.Remove(live.Id);
                    foreach (var m in sp.RoomMaps!.Append(sp.Map))
                        if (world._maps.TryGetValue(m, out var rm) && rm.Mobs.Remove(live)) rm.ViewGen++;
                }
                return true;
            });
            _groups.Remove(mapId);
            _materialized.Remove(mapId);
            foreach (var stamp in _lastDeath.Keys.Where(k => k.Map == mapId).ToList()) _lastDeath.Remove(stamp);
            foreach (var stamp in _entryStacks.Keys.Where(k => k.Map == mapId).ToList()) _entryStacks.Remove(stamp);
            var fm = world.Map(mapId);
            fm.Mobs.RemoveAll(m => m.WorldSpawned);
            fm.ViewGen++;                   // a mob roster change — a test seam, bumped for the same reason
        }
    }

    // ---- test seams (Tests/SpawnDirectorTests.cs) ----------------------------------------------------

    /// <summary>The director itself, so a test can register a point or a group and read the rosters back.</summary>
    internal SpawnDirector SpawnsForTest => _spawnDirector;

    /// <summary>How many beats a test must drive to be sure phase (1.1) sampled the groups once.</summary>
    internal static int BatchSweepTicksForTest => BatchSweepTicks;

    /// <summary>The heartbeat counter, so a test driving beats one at a time can tell a sampled beat
    /// (<c>tick % BatchSweepTicks == 0</c>) from the ones phase (1.1) skips.</summary>
    internal long TickForTest { get { lock (_lock) return _tick; } }

    /// <summary>Every creature on a map, NPCs included — for a test that has no session to <see cref="View"/> with.</summary>
    internal int MobCountForTest(ushort mapId) { lock (_lock) return Map(mapId).Mobs.Count; }
}
