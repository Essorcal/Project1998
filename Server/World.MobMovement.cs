using System.Diagnostics;
using Shared;

namespace Server;

// The movement half of World (#37, section 2). World.cs keeps the lock, the tick, the traps, the AI
// constants and the session-facing Spook; this file keeps how one creature takes one validated step: the
// chase step, the retreat step, the straight hop, the flee dart that strings them together, the commit that
// moves the tile index and queues the draw, and the mob-only collision gate. Nested in World, like
// MobAiTick and SpawnDirector, so it reaches MapState, _deferredFx, NoDetour, the Ice Beast constants,
// IsPcOnlyTrap and TriggerTrapLocked as they are, without widening any of them.
public sealed partial class World
{
    /// <summary>
    /// The mob step primitives, called by <see cref="MobAiTick.Step"/> at exactly the moments the code was
    /// called when it lived in World.cs: <see cref="Dart"/> for the three fleers (prey, the wounded rout,
    /// Sute), <see cref="StepMobToward"/> for the chase, the pet movers and the walk home, and
    /// <see cref="MobBlocked"/> from the tick's own wander step.
    ///
    /// <para>Static, as #37 named it: every helper is parameter-pure over the tick's per-map context — the
    /// map, the collision sets and the outbound queues — and touches no <c>World</c> field of its own. The
    /// one instance reach is <see cref="StepMobTo"/>'s: the Ice Beast melt writes <c>_deferredFx</c> and
    /// a sprung trap runs <c>TriggerTrapLocked</c>, so every helper that can commit a step carries the
    /// <see cref="World"/> as its first parameter and hands it down.</para>
    ///
    /// <para><b>Takes no lock of its own.</b> Every helper that moves a creature asserts
    /// <see cref="HoldsWorldLock"/> on the world it was handed — the callers already hold <c>_lock</c>,
    /// exactly as they did before the move — and <see cref="MobBlocked"/>, which reads only its parameters
    /// and <see cref="Content"/>, asserts nothing, as before; each of its callers asserts first.</para>
    /// </summary>
    internal static class MobMovement
    {
        private const string LockNote = "MobMovement runs under World._lock and nowhere else";

        /// <summary>One step of a chase toward <c>(tx,ty)</c> — a port of RTK's <c>FindCoords</c>
        /// (<c>rtklua/Accepted/Mobs/mob.lua:299</c>), which is the real 4.95 chase step. Caller holds
        /// <c>_lock</c>. True if the mob moved.
        /// <para>The single chase-movement path in the world: the provoked-mob chase, both pet movers (closing on
        /// the foe it is assisting against, and heeling back to its owner), and pet retaliation all run through
        /// here, so obstacle handling can't differ between them.</para>
        ///
        /// <para><b>This is deliberately dumb. Do NOT make it a pathfinder.</b> No A*, no map search, no
        /// lookahead, no wall-following, no memory of where it has been. RTK's version tries ONLY the one or two
        /// directions that close on the target — vertical then horizontal, or horizontal then vertical, on a
        /// coin flip (<c>checkmove = math.random(0, 2)</c>, ≥1 picks vertical-first) — and takes the first that
        /// isn't blocked. That coin flip is the entire cleverness of 4.95 mob pathing.</para>
        ///
        /// <para>What that produces: a mob diagonal from you rounds an ordinary corner by itself, because when one
        /// axis is blocked the other one is still "toward" you. When NO toward-step is open (squared up against a
        /// wall, cornered, or pitted) it falls into RTK's "nothing worked" branch — one step in a fully RANDOM
        /// direction, up to 11 random draws until a tile is free (the random side can be sideways OR straight
        /// away). MEASURED behaviour (re/scratchpad sim, 2026-08-24): clears a 1-wide rock and rounds a wall of any
        /// width when you're diagonal to the mob, but JITTERS at the face — never reaching you — when you stand
        /// directly behind a wall ≥3 wide (re/sim_mob_stepping.py), because the toward-step re-aligns it each tick; and it does not escape
        /// an enclosed pit. It is a random walk with a restoring pull, not a solver.</para>
        ///
        /// <para><b>History:</b> this used to replace RTK's random flail with a sideways-ONLY shuffle. On 2026-08-24
        /// the user reported "not enough pacing/exploration to get to the user if they're blocked"; a simulation of
        /// the alternatives showed a committed-run rule reaches you behind head-on walls (100%) while literal RTK
        /// does not (0%), yet the user chose the literal RTK port anyway, for accuracy over reach, with that
        /// tradeoff in front of them. So the jitter-at-a-head-on-wall and no-pit-escape are the CHOSEN behaviour,
        /// not bugs — don't quietly upgrade this to the committed-run version without re-asking.</para>
        ///
        /// <para>One departure from RTK's branch remains: RTK also re-rolls <c>mob.target</c> to a random nearby
        /// player when stuck; that half lives in the caller's <c>towardBlocked</c> block (gated on
        /// <see cref="Mob.Aggressive"/>), not here.</para></summary>
        internal static bool StepMobToward(World world, ushort mapId, MapState m, Mob mob, int tx, int ty,
                                           (ushort Xs, ushort Ys) dims, MapData? terrain,
                                           HashSet<(ushort, ushort)> occupied, HashSet<(int, int)> mobTiles,
                                           List<(ushort map, uint id, ushort x, ushort y, byte dir)> moves,
                                           List<(ushort map, uint id, byte dir)> turns,
                                           List<(ushort map, Mob mob, int dmg, uint ownerId)> trapDamage)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            return StepMobToward(world, mapId, m, mob, tx, ty, dims, terrain, occupied, mobTiles, moves, turns, trapDamage, out _);
        }

        /// <param name="towardBlocked">True when NOTHING that closes the gap was open this step — the mob is
        /// walled off from its target rather than merely taking a longer route. RTK's <c>canmove == false</c>.
        /// Callers use it to decide whether to go looking for a target they can actually reach.</param>
        /// <inheritdoc cref="StepMobToward(World, ushort, MapState, Mob, int, int, ValueTuple{ushort, ushort}, MapData, HashSet{ValueTuple{ushort, ushort}}, HashSet{ValueTuple{int, int}}, List{ValueTuple{ushort, uint, ushort, ushort, byte}}, List{ValueTuple{ushort, uint, byte}}, List{ValueTuple{ushort, Mob, int, uint}})"/>
        internal static bool StepMobToward(World world, ushort mapId, MapState m, Mob mob, int tx, int ty,
                                           (ushort Xs, ushort Ys) dims, MapData? terrain,
                                           HashSet<(ushort, ushort)> occupied, HashSet<(int, int)> mobTiles,
                                           List<(ushort map, uint id, ushort x, ushort y, byte dir)> moves,
                                           List<(ushort map, uint id, byte dir)> turns,
                                           List<(ushort map, Mob mob, int dmg, uint ownerId)> trapDamage,
                                           out bool towardBlocked)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
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
                if (MobBlocked(mapId, terrain, nx, ny, dir)) return false;   // pass flag / SObj wall / warp tile
                if (mob.Dir != dir) { mob.Dir = dir; turns.Add((mapId, mob.Id, dir)); }
                return StepMobTo(world, mapId, m, mob, nx, ny, dir, mobTiles, moves, trapDamage);
            }

            // RTK FindCoords proper (mob.lua:307-352): only the directions that close the gap, on a coin-flipped
            // axis order. First unblocked one wins.
            var toward = new List<byte>(2);
            byte? vert = dy > 0 ? (byte)2 : dy < 0 ? (byte)0 : null;
            byte? horz = dx > 0 ? (byte)1 : dx < 0 ? (byte)3 : null;
            if (Random.Shared.Next(3) >= 1) { if (vert is byte a) toward.Add(a); if (horz is byte b) toward.Add(b); }
            else                            { if (horz is byte c) toward.Add(c); if (vert is byte d) toward.Add(d); }

            foreach (byte dir in toward)
                if (Step(dir)) return true;

            // Nothing that closes the gap is open.
            towardBlocked = true;

            // RTK's "nothing worked" branch (mob.lua:361-382), ported faithfully: take ONE step in a fully random
            // direction, retrying up to 11 random draws until a tile is open —
            //     for i = 0, 10 do if (not found) then mob.side = math.random(0, 3); found = mob:move() end end
            // Any of the four sides is fair game — sideways OR straight AWAY from the target. One tile per call, at
            // the mob's move cadence; the loop only searches for an open side, it doesn't stack hops.
            //
            // What this ACTUALLY produces (measured, re/sim_mob_stepping.py, 2026-08-24 — don't re-optimise on a hunch):
            // it clears a 1-wide rock and rounds a wall of any width when the player is DIAGONAL to the mob, but
            // when the player is directly behind a wall ≥3 tiles wide the mob JITTERS at the face (~0% reach) —
            // every random sideways step is undone next tick by the toward-step snapping it back into alignment.
            // For the same reason it does NOT escape an enclosed pit. Both are faithful RTK, and the user chose it
            // over the more capable committed-run alternative AFTER seeing this exact data (2026-08-24): the goal
            // was RTK-accuracy, not maximal reach. If a report says "mobs jitter at a wall / can't reach me behind
            // one", that is this branch being literally RTK — revisit the decision, don't silently make it smarter.
            // RTK's companion move here — re-rolling the target to a random nearby player — lives in the caller's
            // `towardBlocked` block, keyed off the flag set just above.
            for (int i = 0; i < 11; i++)
                if (Step((byte)Random.Shared.Next(4))) return true;

            // Boxed in on all four sides. Face the target so it at least reads as wanting to reach you.
            if (toward.Count > 0 && mob.Dir != toward[0]) { mob.Dir = toward[0]; turns.Add((mapId, mob.Id, toward[0])); }
            return false;
        }

        /// <summary>A tile a MOB may not step onto. The two static collision layers — ground pass flag plus the
        /// client's <c>SObj.tbl</c> directional object-walls, via <see cref="MapData.BlockedMove"/> — PLUS warp
        /// source tiles. A mob can't warp, so letting it wander onto a door/stair/portal tile just parks it on the
        /// threshold, and following you onto one reads as walking through the wall the warp sits in — the reported
        /// "mobs no-clip … warps". Blocking warp tiles for mobs is a deliberate deviation (user, 2026-08-24): RTK's
        /// own mob move is pass-only and clips these, and the PLAYER walk still treats a warp as walkable-and-
        /// transiting (<see cref="Session"/>.HandleWalk) — this gate is mob-only. Caller has already bounds-checked
        /// (nx,ny) ≥ 0 and in-dims, so the ushort casts are safe.</summary>
        internal static bool MobBlocked(ushort mapId, MapData? terrain, int nx, int ny, byte dir) =>
            (terrain is not null && terrain.BlockedMove(nx, ny, dir))
            || Content.TryWarp(mapId, (ushort)nx, (ushort)ny, out _);

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
        private static bool StepMobAway(World world, ushort mapId, MapState m, Mob mob, int tx, int ty,
                                        (ushort Xs, ushort Ys) dims, MapData? terrain,
                                        HashSet<(ushort, ushort)> occupied, HashSet<(int, int)> mobTiles,
                                        List<(ushort map, uint id, ushort x, ushort y, byte dir)> moves,
                                        List<(ushort map, uint id, byte dir)> turns,
                                        List<(ushort map, Mob mob, int dmg, uint ownerId)> trapDamage)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            int dx = tx - mob.X, dy = ty - mob.Y;

            bool Step(byte dir)
            {
                int nx = mob.X + (dir == 1 ? 1 : dir == 3 ? -1 : 0);
                int ny = mob.Y + (dir == 2 ? 1 : dir == 0 ? -1 : 0);
                if (nx < 0 || ny < 0) return false;
                if (dims.Xs != 0 && (nx >= dims.Xs || ny >= dims.Ys)) return false;
                if (occupied.Contains(((ushort)nx, (ushort)ny))) return false;
                if (mobTiles.Contains((nx, ny))) return false;
                if (MobBlocked(mapId, terrain, nx, ny, dir)) return false;
                if (mob.Dir != dir) { mob.Dir = dir; turns.Add((mapId, mob.Id, dir)); }
                if (!StepMobTo(world, mapId, m, mob, nx, ny, dir, mobTiles, moves, trapDamage)) return false;
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

        // ---- the flee DART: the one way anything in this world runs away --------------------------------
        /// <summary>How each hop of a <see cref="Dart"/> picks its direction.</summary>
        internal enum DartMode
        {
            /// <summary>Open the gap from (tx,ty) — <see cref="StepMobAway"/>, sideways slip and all.</summary>
            Away,
            /// <summary>Close on (tx,ty), stopping the moment the mob is in reach — <see cref="StepMobToward"/>.</summary>
            Toward,
            /// <summary>Straight ahead in <see cref="Mob.Dir"/>, wherever that points (the blind rout).</summary>
            Straight,
        }

        /// <summary>
        /// Cover up to <paramref name="tiles"/> tiles inside ONE move turn, stopping early at the first hop that
        /// can't be taken. Returns how many were actually covered — 0 means boxed in, which is what every caller
        /// reads as "cornered".
        ///
        /// <para><b>This is RTK's own idiom for running away, and the only one this server uses.</b> RTK expresses
        /// a break-off by calling <c>mob:move()</c> several times in a single script invocation —
        /// <c>AI/bosses/nine_tailed_fox.lua</c> does three in a row — which the client draws as a creature
        /// covering several tiles at once rather than walking faster. The three fleers here (prey, the wounded
        /// rout, Sute) all used to approximate it differently: prey ran on a shortened timer, the rout stepped
        /// every heartbeat, and Sute paced tile by tile. They now share this, so "runs away" looks the same
        /// everywhere and there is one place to change it.</para>
        ///
        /// <para>Every hop goes through the ordinary step helpers, so walls, occupancy, map bounds and trap tiles
        /// all apply to each one individually — a fleeing creature can absolutely bolt onto a trap. (The old
        /// hand-rolled rout skipped the trap check by moving the mob itself; going through
        /// <see cref="StepMobTo"/> fixes that inconsistency.)</para>
        ///
        /// <para>Caller holds <c>_lock</c> and has already decided this is the mob's move turn.</para>
        /// </summary>
        internal static int Dart(World world, DartMode mode, int tiles, ushort mapId, MapState m, Mob mob, int tx, int ty,
                                 (ushort Xs, ushort Ys) dims, MapData? terrain,
                                 HashSet<(ushort, ushort)> occupied, HashSet<(int, int)> mobTiles,
                                 List<(ushort map, uint id, ushort x, ushort y, byte dir)> moves,
                                 List<(ushort map, uint id, byte dir)> turns,
                                 List<(ushort map, Mob mob, int dmg, uint ownerId)> trapDamage)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            int hops = 0;
            while (hops < tiles)
            {
                bool stepped;
                switch (mode)
                {
                    case DartMode.Toward:
                        int dx = tx - mob.X, dy = ty - mob.Y;
                        // In reach: stop here rather than trying to walk through them.
                        if ((dx == 0 && Math.Abs(dy) == 1) || (dy == 0 && Math.Abs(dx) == 1)) return hops;
                        stepped = StepMobToward(world, mapId, m, mob, tx, ty, dims, terrain, occupied, mobTiles, moves, turns, trapDamage);
                        break;
                    case DartMode.Straight:
                        stepped = StepMobStraight(world, mapId, m, mob, dims, terrain, occupied, mobTiles, moves, trapDamage);
                        break;
                    default:
                        stepped = StepMobAway(world, mapId, m, mob, tx, ty, dims, terrain, occupied, mobTiles, moves, turns, trapDamage);
                        break;
                }
                if (!stepped) break;
                hops++;
            }
            return hops;
        }

        /// <summary>One hop straight ahead in the mob's current facing, with the same bounds/occupancy/terrain
        /// checks every other step takes. The blind half of the wounded rout: RTK picks a random side once and
        /// then runs, so the creature does not steer around anything — it just stops when it hits something.
        /// Caller holds <c>_lock</c>.</summary>
        private static bool StepMobStraight(World world, ushort mapId, MapState m, Mob mob,
                                            (ushort Xs, ushort Ys) dims, MapData? terrain,
                                            HashSet<(ushort, ushort)> occupied, HashSet<(int, int)> mobTiles,
                                            List<(ushort map, uint id, ushort x, ushort y, byte dir)> moves,
                                            List<(ushort map, Mob mob, int dmg, uint ownerId)> trapDamage)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            byte dir = mob.Dir;
            int nx = mob.X + (dir == 1 ? 1 : dir == 3 ? -1 : 0);
            int ny = mob.Y + (dir == 2 ? 1 : dir == 0 ? -1 : 0);
            if (nx < 0 || ny < 0) return false;
            if (dims.Xs != 0 && (nx >= dims.Xs || ny >= dims.Ys)) return false;
            if (occupied.Contains(((ushort)nx, (ushort)ny))) return false;
            if (mobTiles.Contains((nx, ny))) return false;
            if (MobBlocked(mapId, terrain, nx, ny, dir)) return false;
            return StepMobTo(world, mapId, m, mob, nx, ny, dir, mobTiles, moves, trapDamage);
        }

        /// <summary>Commit a validated mob step and trigger any trap on its destination. Caller holds
        /// <c>_lock</c>.</summary>
        private static bool StepMobTo(World world, ushort mapId, MapState m, Mob mob, int nx, int ny, byte dir,
                                      HashSet<(int, int)> mobTiles,
                                      List<(ushort map, uint id, ushort x, ushort y, byte dir)> moves,
                                      List<(ushort map, Mob mob, int dmg, uint ownerId)> trapDamage)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            ushort ox = mob.X, oy = mob.Y;
            mobTiles.Remove((mob.X, mob.Y));
            mob.X = (ushort)nx; mob.Y = (ushort)ny;
            mobTiles.Add((nx, ny));
            moves.Add((mapId, mob.Id, ox, oy, dir));
            // The Ice Beast melts the instant it steps onto its lava (RTK ice_beast.lua move hook). Lethal
            // self-damage is queued like a trap hit so it flows through the normal death path: its Ice heart drops
            // on the tile (MobDrops 100%) and its spawn frees. ownerId 0 pays no exp — which is what RTK's lava
            // kill does; the beast is worth none, the reward is the heart on the floor.
            if (mapId == IceBeastMap && mob.Key == IceBeastKey && IsIceBeastLava(nx, ny))
            {
                trapDamage.Add((mapId, mob, mob.MaxHp, 0));
                world._deferredFx.Add((mapId, mob.Id, mob.X, mob.Y, IceBeastMeltAnim, 0));
            }
            var trap = m.Traps.FirstOrDefault(t => t.X == nx && t.Y == ny && !IsPcOnlyTrap(t.Kind));
            if (trap is not null) { m.Traps.Remove(trap); world.TriggerTrapLocked(mapId, mob, trap, trapDamage); }
            return true;
        }
    }
}
