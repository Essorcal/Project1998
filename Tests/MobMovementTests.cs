using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The mob step primitives after their move out of <c>World.cs</c> into <c>World.MobMovement</c> (#37,
/// section 2), each driven DIRECTLY through its seam on the tick's own per-map context
/// (<c>MobTickContextForTest</c>, built under <c>World._lock</c>) — which is what the extraction makes
/// possible: before it, the helpers were private to <c>World</c> and reachable only through a whole beat of
/// <c>MobAiTick.Step</c>, so none of the rules below could be pinned on its own.
///
/// <para>Every case is deterministic by construction, not by seeding. <c>StepMobToward</c> and
/// <c>StepMobAway</c> flip a coin for the axis order, but a target on the mob's own column leaves one
/// candidate direction, so the flip picks between a list of one and nothing; the boxed case rolls the
/// eleven random sides and every one of them is occupied. The maps are content-free (no registry row, so no
/// terrain and no warps, and nothing spawns), one per test so nothing is shared; the creatures left on them
/// are inert, as in <c>MobAiTickTests</c>.</para>
/// </summary>
[Collection("world")]
public class MobMovementTests
{
    private readonly SessionFixture _fx;

    public MobMovementTests(SessionFixture fx) => _fx = fx;

    private const ushort BoxMap = 60050, AboutFaceMap = 60051, ReachMap = 60052, TrapMap = 60053, LockMap = 60054,
                         NoWarpMap = 60055;

    private Mob Registered(ushort map, ushort x, ushort y, string name, byte dir = 0)
    {
        var mob = new Mob(_fx.World.AllocateMobId(), 1, x, y, name, 100) { Dir = dir };
        _fx.World.AddMob(map, mob);
        return mob;
    }

    /// <summary>RTK <c>FindCoords</c>' last line: a creature with another creature on each of its four
    /// neighbours cannot close on its target and cannot flail sideways either. It stays put, reports
    /// <c>towardBlocked</c> (the caller's cue to look for a target it CAN reach), queues no move, and turns
    /// to face the target so it reads as wanting to get there — one turn, north, from a creature that was
    /// facing south. The collision index is untouched.
    ///
    /// <para>Falsified by deleting the "Boxed in on all four sides" turn line in
    /// <c>World.MobMovement.cs</c>: red, <c>Assert.Single() Failure: The collection was empty</c>.</para></summary>
    [Fact]
    public void ChaseStepBoxedInOnAllFourSidesFacesTheTargetAndReportsItWalledOff()
    {
        var mob = Registered(BoxMap, 5, 5, "Boxed", dir: 2);   // facing south; the target is north
        Registered(BoxMap, 5, 4, "North");
        Registered(BoxMap, 6, 5, "East");
        Registered(BoxMap, 5, 6, "South");
        Registered(BoxMap, 4, 5, "West");

        World.MobTickContext ctx = null!;
        bool stepped = true, walled = false;
        _fx.World.UnderWorldLockForTest(() =>
        {
            ctx = _fx.World.MobTickContextForTest(BoxMap);
            stepped = World.MobMovement.StepTowardForTest(ctx, mob, 5, 2, out walled);
        });

        Assert.False(stepped);
        Assert.True(walled, "nothing that closes the gap was open: towardBlocked should say so");
        Assert.Equal(((ushort)5, (ushort)5), (mob.X, mob.Y));
        Assert.Empty(ctx.Moves);
        Assert.Empty(ctx.TrapDamage);
        var turn = Assert.Single(ctx.Turns);
        Assert.Equal((BoxMap, mob.Id, (byte)0), turn);   // faced north, toward the target
        Assert.Equal(0, mob.Dir);
        Assert.Equal(5, ctx.MobTiles.Count);
        Assert.Contains((5, 5), ctx.MobTiles);
    }

    /// <summary>RTK <c>RunAway</c>'s <c>moveIntent</c> branch: a creature with the player right next to it
    /// turns 180 degrees from where it was FACING and bolts — not along the axis that opens the gap. Facing
    /// east with the player to the north, that is one step west; the away-axis rule alone would have sent
    /// it south. The move is queued with its SOURCE tile (the 0x0C overshoot rule), the turn is queued,
    /// and the creature is re-homed on the tile it lands on (the doc's leash note).
    ///
    /// <para>Falsified by deleting the <c>if (adjacent &amp;&amp; Step(...)) return true;</c> line in
    /// <c>World.MobMovement.cs</c>: red, the creature is on (5,6) instead of (4,5).</para></summary>
    [Fact]
    public void RetreatStepFromAnAdjacentPlayerAboutFacesAndReHomes()
    {
        var (threat, _) = _fx.Player("AboutFaceThreat", AboutFaceMap, x: 5, y: 4);
        try
        {
            var mob = Registered(AboutFaceMap, 5, 5, "Cornered", dir: 1);   // facing east; the threat is north

            World.MobTickContext ctx = null!;
            bool stepped = false;
            _fx.World.UnderWorldLockForTest(() =>
            {
                ctx = _fx.World.MobTickContextForTest(AboutFaceMap);
                Assert.Contains(((ushort)5, (ushort)4), ctx.Occupied);
                stepped = World.MobMovement.StepAwayForTest(ctx, mob, threat.PlayerX, threat.PlayerY);
            });

            Assert.True(stepped);
            Assert.Equal(((ushort)4, (ushort)5), (mob.X, mob.Y));   // west: the about-face of east
            Assert.Equal(3, mob.Dir);
            var turn = Assert.Single(ctx.Turns);
            Assert.Equal((AboutFaceMap, mob.Id, (byte)3), turn);
            var move = Assert.Single(ctx.Moves);
            Assert.Equal((AboutFaceMap, mob.Id, (ushort)5, (ushort)5, (byte)3), move);   // source tile
            Assert.Equal(((ushort)4, (ushort)5), (mob.HomeX, mob.HomeY));
            Assert.Contains((4, 5), ctx.MobTiles);
            Assert.DoesNotContain((5, 5), ctx.MobTiles);
        }
        finally { _fx.World.LeaveMap(threat, AboutFaceMap); }
    }

    /// <summary>A <c>Toward</c> dart stops the moment the creature is in reach rather than walking into its
    /// target: three tiles south of the target with a three-tile dart, it covers two hops and reports two;
    /// darted again from there, it covers none. Both moves carry their source tiles, and the one turn (south
    /// to north) comes on the first hop.
    ///
    /// <para>The target is a tile, not a player, on purpose: with the tile free the falsification is clean.
    /// Deleting the in-reach <c>return hops;</c> line in <c>World.MobMovement.cs</c> walks the creature onto
    /// (5,2) and is red with <c>Expected: 2 Actual: 3</c>; with a player standing there the occupancy gate
    /// would catch the third hop instead and the random side-step would decide the count.</para></summary>
    [Fact]
    public void DartTowardStopsOneTileShortOfTheTarget()
    {
        var mob = Registered(ReachMap, 5, 5, "Closer", dir: 2);   // facing south; the target is north

        World.MobTickContext ctx = null!;
        int hops = -1, again = -1;
        _fx.World.UnderWorldLockForTest(() =>
        {
            ctx = _fx.World.MobTickContextForTest(ReachMap);
            hops = World.MobMovement.DartForTest(ctx, World.MobMovement.DartMode.Toward, 3, mob, 5, 2);
            again = World.MobMovement.DartForTest(ctx, World.MobMovement.DartMode.Toward, 3, mob, 5, 2);
        });

        Assert.Equal(2, hops);
        Assert.Equal(0, again);
        Assert.Equal(((ushort)5, (ushort)3), (mob.X, mob.Y));
        Assert.Equal(2, ctx.Moves.Count);
        Assert.Equal((ReachMap, mob.Id, (ushort)5, (ushort)5, (byte)0), ctx.Moves[0]);
        Assert.Equal((ReachMap, mob.Id, (ushort)5, (ushort)4, (byte)0), ctx.Moves[1]);
        var turn = Assert.Single(ctx.Turns);
        Assert.Equal((ReachMap, mob.Id, (byte)0), turn);
        Assert.Contains((5, 3), ctx.MobTiles);
        Assert.DoesNotContain((5, 5), ctx.MobTiles);
    }

    /// <summary>The commit: a step onto a Rogue trap tile removes the trap and queues its damage against the
    /// creature, credited to the trap's owner; a step onto a player-only tile (an "ambush" spawn trigger)
    /// leaves it where it is. Two committed steps north through a dart trap and then an ambush tile: one
    /// damage entry, owned by the caster, and the ambush is the only trap left on the map. The damage
    /// figure is the trap block's, not pinned here.
    ///
    /// <para>Falsified by deleting the <c>if (trap is not null) { ... }</c> line in <c>StepMobTo</c>: red,
    /// <c>Assert.Single() Failure: The collection was empty</c> on the damage queue.</para></summary>
    [Fact]
    public void CommittedStepOntoATrapTileSpringsItAndLeavesAPlayerOnlyTrapAlone()
    {
        var mob = Registered(TrapMap, 5, 5, "Stepper", dir: 0);
        _fx.World.PlaceTrap(TrapMap, 5, 4, "dart", ownerId: 77);
        var ambush = _fx.World.PlaceTrap(TrapMap, 5, 3, "ambush", ownerId: 0);

        World.MobTickContext ctx = null!;
        _fx.World.UnderWorldLockForTest(() =>
        {
            ctx = _fx.World.MobTickContextForTest(TrapMap);
            Assert.True(World.MobMovement.StepToForTest(ctx, mob, 5, 4, 0));
            Assert.True(World.MobMovement.StepToForTest(ctx, mob, 5, 3, 0));
        });

        Assert.Equal(((ushort)5, (ushort)3), (mob.X, mob.Y));
        Assert.Equal(2, ctx.Moves.Count);
        Assert.Equal((TrapMap, mob.Id, (ushort)5, (ushort)5, (byte)0), ctx.Moves[0]);
        Assert.Equal((TrapMap, mob.Id, (ushort)5, (ushort)4, (byte)0), ctx.Moves[1]);
        var hit = Assert.Single(ctx.TrapDamage);
        Assert.Same(mob, hit.mob);
        Assert.Equal(TrapMap, hit.map);
        Assert.True(hit.dmg > 0);
        Assert.Equal(77u, hit.ownerId);
        var survivor = Assert.Single(_fx.World.TrapsNear(TrapMap, 5, 4, 5));
        Assert.Same(ambush, survivor);
        Assert.Contains((5, 3), ctx.MobTiles);
        Assert.DoesNotContain((5, 4), ctx.MobTiles);
        Assert.DoesNotContain((5, 5), ctx.MobTiles);
    }

    /// <summary>The mob-only collision gate refuses a warp source tile whatever the terrain says — the
    /// deliberate deviation from RTK's pass-only mob move recorded in its doc (user, 2026-08-24): a creature
    /// cannot warp, so a door tile is a wall to it. Any door in the content will do; the same coordinates on
    /// a map with no warp there are open, so it is the warp and not the tile that blocks.
    ///
    /// <para>Falsified by deleting the <c>|| Content.TryWarp(...)</c> clause in <c>World.MobMovement.cs</c>:
    /// red, <c>Assert.True() Failure</c>.</para></summary>
    [Fact]
    public void MobBlockedRefusesAWarpTileWhateverTheTerrainSays()
    {
        var (map, x, y) = Content.Warps.Keys.First();
        Assert.False(Content.TryWarp(NoWarpMap, x, y, out _));   // precondition: the control map has no door there

        Assert.True(World.MobMovement.MobBlocked(map, terrain: null, x, y, dir: 0));
        Assert.False(World.MobMovement.MobBlocked(NoWarpMap, terrain: null, x, y, dir: 0));
    }

#if DEBUG
    /// <summary>The contract at the top of every helper that can commit a step: it refuses to run without
    /// <c>World._lock</c>. Both directions, in the <c>MobAiTickTests</c> shape — under the lock the commit is
    /// silent and the creature moves; off it, each of the five seams is loud and the creature stays where the
    /// locked step left it. Debug-only by construction, like every lock assert in <c>docs/common/Locking.md</c>:
    /// the assert is compiled out of Release, so the test is too (the test host turns a failed
    /// <c>Debug.Assert</c> into an exception carrying the message; a Debug server process would fail fast).
    ///
    /// <para>The asserts are layered: the chase, retreat, straight and dart helpers each assert before they
    /// reach <c>StepMobTo</c>, which asserts again. So the leaf is the falsification: deleting
    /// <c>StepMobTo</c>'s assert alone lets the unlocked commit run to completion and the test is red on
    /// "StepMobTo ran outside World._lock without tripping its assert"; the other four still trip on their
    /// own. <c>MobBlocked</c> has no assert and is not here: it reads only its parameters and
    /// <c>Content</c>, and each of its callers asserts first.</para></summary>
    [Fact]
    public void StepHelpersRefuseToRunOutsideTheWorldLock()
    {
        var mob = Registered(LockMap, 5, 5, "Unlocked", dir: 0);

        World.MobTickContext ctx = null!;
        var rightWay = Record.Exception(() => _fx.World.UnderWorldLockForTest(() =>
        {
            ctx = _fx.World.MobTickContextForTest(LockMap);
            World.MobMovement.StepToForTest(ctx, mob, 5, 4, 0);
        }));
        Assert.Null(rightWay);
        Assert.Equal(((ushort)5, (ushort)4), (mob.X, mob.Y));

        Assert.False(_fx.World.HoldsWorldLock);
        var offTheLock = new (string name, Action call)[]
        {
            ("StepMobToward", () => World.MobMovement.StepTowardForTest(ctx, mob, 5, 1, out _)),
            ("StepMobAway", () => World.MobMovement.StepAwayForTest(ctx, mob, 5, 3)),
            ("StepMobStraight", () => World.MobMovement.StepStraightForTest(ctx, mob)),
            ("Dart", () => World.MobMovement.DartForTest(ctx, World.MobMovement.DartMode.Straight, 1, mob, 5, 4)),
            ("StepMobTo", () => World.MobMovement.StepToForTest(ctx, mob, 5, 3, 0)),
        };
        foreach (var (name, call) in offTheLock)
        {
            var wrongWay = Record.Exception(call);
            Assert.True(wrongWay is not null, $"{name} ran outside World._lock without tripping its assert");
            Assert.Contains("nowhere else", wrongWay!.Message);
        }
        Assert.Equal(((ushort)5, (ushort)4), (mob.X, mob.Y));   // none of the refused calls moved it
        Assert.Single(ctx.Moves);                                 // nor queued anything
    }
#endif
}
