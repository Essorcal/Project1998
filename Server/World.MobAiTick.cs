using System.Diagnostics;
using Shared;

namespace Server;

// The per-mob half of World.Tick (#36). World.cs keeps the heartbeat, the lock and the flush; this file
// keeps what one creature does on one beat. Both types are NESTED in World rather than top-level so they
// reach MapState, the step helpers (Dart, StepMobToward, TriggerTrapLocked...) and the AI constants as
// they are, without widening any of them to internal — the one widening the split needed is MapState
// itself, because a field on an internal type cannot be of a private one.
public sealed partial class World
{
    /// <summary>
    /// Everything <see cref="MobAiTick.Step"/> needs about the map it is stepping on, built ONCE per map per
    /// tick by <see cref="Tick"/> just before the mob loop. The two tile sets are the beat's collision index
    /// (kept current as mobs move, so two creatures cannot step onto the same tile in one sweep); the lists
    /// are the tick's outbound queues — a step never sends, it only queues, and the tick flushes every list
    /// after <c>_lock</c> is released (see <c>docs/common/Locking.md</c>, "decide under the lock, act outside
    /// it"). The queues are the tick's own lists, shared by every map's context that beat, so their order
    /// across maps is the order the maps were walked in — exactly as before the split.
    /// </summary>
    internal sealed class MobTickContext
    {
        public readonly World World;
        public readonly ushort MapId;
        public readonly MapState Map;
        /// <summary>The map's size from the registry, or (0,0) for a map with no registry row — the step
        /// helpers read a zero width as "unbounded", which is what a content-free test map wants.</summary>
        public readonly (ushort Xs, ushort Ys) Dims;
        /// <summary>The map's collision layers, or null when <see cref="Dims"/> is zero.</summary>
        public readonly MapData? Terrain;
        /// <summary>Every player's tile this beat — a mob never steps onto one.</summary>
        public readonly HashSet<(ushort, ushort)> Occupied;
        /// <summary>Every living mob's tile — kept current as they move, so a mob won't step onto another.</summary>
        public readonly HashSet<(int, int)> MobTiles;

        public readonly List<(ushort map, uint id, ushort x, ushort y, byte dir)> Moves;
        public readonly List<(ushort map, uint id, byte dir)> Turns;
        public readonly List<(ushort map, Mob mob, Session target)> Hits;
        public readonly List<(Mob mob, Session target, Content.MobSpellDef spell)> MobCasts;
        public readonly List<(ushort map, Mob mob, byte channel, string line)> Chatter;
        public readonly List<(ushort map, Mob attacker, Mob victim)> MobHits;
        public readonly List<(ushort map, Mob mob, int dmg, uint ownerId)> TrapDamage;
        public readonly List<(ushort map, uint id, ushort x, ushort y, int anim, int sound)> FxRepeats;
        public readonly List<(ushort map, Mob mob)> HealthShows;
        public readonly List<(ushort map, Mob mob)> ExpiredPets;

        /// <summary>Caller holds <c>_lock</c>: the two tile sets are read off live player and mob lists.</summary>
        public MobTickContext(World world, ushort mapId, MapState map,
                              List<(ushort map, uint id, ushort x, ushort y, byte dir)> moves,
                              List<(ushort map, uint id, byte dir)> turns,
                              List<(ushort map, Mob mob, Session target)> hits,
                              List<(Mob mob, Session target, Content.MobSpellDef spell)> mobCasts,
                              List<(ushort map, Mob mob, byte channel, string line)> chatter,
                              List<(ushort map, Mob attacker, Mob victim)> mobHits,
                              List<(ushort map, Mob mob, int dmg, uint ownerId)> trapDamage,
                              List<(ushort map, uint id, ushort x, ushort y, int anim, int sound)> fxRepeats,
                              List<(ushort map, Mob mob)> healthShows,
                              List<(ushort map, Mob mob)> expiredPets)
        {
            Debug.Assert(world.HoldsWorldLock, "MobTickContext reads the live player and mob lists; build it under World._lock");
            World = world; MapId = mapId; Map = map;
            Moves = moves; Turns = turns; Hits = hits; MobCasts = mobCasts; Chatter = chatter; MobHits = mobHits;
            TrapDamage = trapDamage; FxRepeats = fxRepeats; HealthShows = healthShows; ExpiredPets = expiredPets;

            Dims = Content.Maps.TryGetValue(mapId, out var mi) ? (mi.Xs, mi.Ys) : ((ushort)0, (ushort)0);
            Terrain = Dims.Item1 > 0 ? MapData.For(mapId, Dims.Item1, Dims.Item2) : null;
            Occupied = map.Players.Select(p => (p.PlayerX, p.PlayerY)).ToHashSet();
            // Every living mob's tile — so a mob won't step onto another (kept current as they move below).
            MobTiles = new HashSet<(int, int)>();
            foreach (var mo in map.Mobs) if (mo.Alive) MobTiles.Add((mo.X, mo.Y));
        }
    }

    /// <summary>
    /// One creature's turn on one heartbeat: buff and ownership expiry, the status timers, then whichever
    /// of pet / prey / blind / rout / chase / retaliation / wander applies. Mutates the mob's own fields and
    /// the context's tile sets, and adds to the context's queues; it never sends and never enters a
    /// session (<c>docs/common/Locking.md</c> row 2) or the Lua gate (row 1) — everything session-facing is
    /// queued for the tick to apply after <c>_lock</c> is released.
    ///
    /// <para>Runs under <c>World._lock</c>, and only there; the assert at the top is the contract. The
    /// exception boundary is the CALLER's: <see cref="World.Tick"/> wraps each call so one creature that
    /// throws is logged and skipped while the rest of the sweep — and every packet already queued — goes
    /// on. A test that wants to see a throw drives this directly and expects it to propagate.</para>
    /// </summary>
    internal static class MobAiTick
    {
        public static void Step(MobTickContext ctx, Mob mob)
        {
            Debug.Assert(ctx.World.HoldsWorldLock, "MobAiTick.Step runs under World._lock and nowhere else");
        }
    }
}
