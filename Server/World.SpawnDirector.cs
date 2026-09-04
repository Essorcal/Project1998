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
    /// move — and the four public statics that only read <see cref="Content"/> and the terrain cache
    /// (<see cref="PlacementBox"/>, <see cref="Placeable"/>, <see cref="OpenTiles"/>) need none.</para>
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

        /// <summary>Build the <c>_spawns</c> + <c>_groups</c> rosters from the current <see cref="Content.Spawns"/>
        /// and <see cref="Content.AreaSpawns"/>. Caller holds <c>_lock</c> and has already cleared both if
        /// rebuilding. Shared by startup <see cref="PopulateSpawns"/> and the live <see cref="RebuildPopulation"/>.</summary>
        internal (int points, int skipped, int groups, int capped) Build()
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            int points = 0, skipped = 0, capped = 0;
            foreach (var sd in Content.Spawns)
            {
                if (!Content.Maps.ContainsKey(sd.Map)) { skipped++; continue; }   // map the client can't render
                var def = Content.MobById(sd.MobId);
                if (def is null) { skipped++; continue; }                           // unknown mob id

                AddSpawn(sd.Map, new Spawn { Def = def, X = sd.X, Y = sd.Y, RespawnEvery = SpawnTicksFor(def) });
                points++;
            }

            // Area rows fork by system (see AreaSpawnDef.Timer): a timer means one member of a batch group, no
            // timer means the trap supplement, which stays on the point model it was ported to.
            var byGroup = new Dictionary<(ushort Map, int Group), SpawnGroup>();
            foreach (var ad in Content.AreaSpawns)
            {
                if (!Content.Maps.ContainsKey(ad.Map)) { skipped += ad.Count; continue; }   // unrenderable map
                var def = Content.MobById(ad.MobId);
                if (def is null) { skipped += ad.Count; continue; }                          // unknown mob id

                if (ad.Timer > 0)
                {
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
                    continue;
                }

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

        /// <summary>Append a spawn point to its map's roster. Caller holds <c>_lock</c>.</summary>
        private void AddSpawn(ushort mapId, Spawn sp)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            if (!_spawns.TryGetValue(mapId, out var list)) { list = new(); _spawns[mapId] = list; }
            list.Add(sp);
        }

        /// <summary>Instantiate a map's spawn roster the first time anyone enters it (idempotent). Until this runs
        /// the map's mobs don't exist, so a newcomer must trigger it BEFORE the room's mob list is read for them.
        /// Caller holds <c>_lock</c>.</summary>
        internal void EnsureMaterialized(ushort mapId)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            // Batch groups get a chance to fill BEFORE the entering player's room list is read, so a room whose
            // timer came due while nobody was in it is already full when they walk in rather than popping into
            // existence around them. Not inside the _materialized guard: this has to be reconsidered on every
            // entry, since that is the only moment a due group on an unwatched map gets looked at.
            RefillGroups(mapId);
            world.RefillAmbushLocked(mapId);   // top up this map's hidden ambush traps (also every entry, same reason)
            world.RefillFrigidLocked(mapId);   // …and Sute's Cave's hidden cold tiles (Server/SuteAi.cs)

            if (!_materialized.Add(mapId)) return;              // already done
            if (!_spawns.TryGetValue(mapId, out var list)) return;
            foreach (var sp in list)
            {
                // A rare boss doesn't appear the instant someone walks in — it's a surprise. Leave it pending
                // with a random first-appearance somewhere in its respawn window; the Tick refill loop spawns it
                // once due (and only while the map is being hunted, since that loop skips empty maps).
                if (sp.Rare) { sp.RespawnTick = world._tick + 1 + Random.Shared.Next(sp.RespawnEvery); continue; }
                Materialize(mapId, sp);
            }
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
        /// a visit is worth more than the re-randomisation, and a cleared room is supposed to stay dead.</summary>
        private void RefillGroups(ushort mapId)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            if (!_groups.TryGetValue(mapId, out var groups)) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            HashSet<(int, int)>? taken = null;               // built once, only if something is actually due
            foreach (var g in groups)
            {
                if (now < g.NextBatchUnix) continue;
                taken ??= world.OccupiedTiles(mapId);
                foreach (var (def, cap) in g.Members) FillMember(g, def, cap, taken);
                g.NextBatchUnix = now + g.TimerSec;
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
        /// die. Caller holds <c>_lock</c>.</summary>
        internal void RespawnDuePoints(long tick)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            foreach (var (mapId, list) in _spawns)
            {
                if (!world._maps.TryGetValue(mapId, out var pm) || pm.Players.Count == 0) continue;
                foreach (var sp in list)
                    if (sp.Live is null && sp.RespawnTick != 0 && tick >= sp.RespawnTick)
                        Materialize(mapId, sp);
            }
        }

        /// <summary>Phase (1.1): every due spawn group on a map someone is hunting (RTK's spawner NPC, whose
        /// own <c>#pc &gt; 0</c> test this mirrors). A map nobody is on is skipped here and caught by
        /// <see cref="EnsureMaterialized"/> when someone walks in. Sampled every <c>BatchSweepTicks</c> —
        /// these clocks are in whole seconds and the shortest is 2s, so there is nothing to gain from looking
        /// every beat. Caller holds <c>_lock</c>.</summary>
        internal void RefillDueGroups(long tick)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            if (tick % BatchSweepTicks != 0) return;
            foreach (var (mapId, _) in _groups)
            {
                if (!world._maps.TryGetValue(mapId, out var pm) || pm.Players.Count == 0) continue;
                RefillGroups(mapId);
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

        /// <summary>Drop both rosters and the materialised set ahead of a <see cref="Build"/> from re-read
        /// content (<see cref="RebuildPopulation"/>). The death registry is deliberately kept: a reload is
        /// not a world reset for a boss cooldown. Caller holds <c>_lock</c>.</summary>
        internal void Clear()
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            _spawns.Clear();
            _groups.Clear();
            _materialized.Clear();
        }

        /// <summary>How many maps carry at least one spawn point / batch group — the start-up log lines.</summary>
        internal int PointMapCount => _spawns.Count;
        internal int GroupMapCount => _groups.Count;
    }
}
