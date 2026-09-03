using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The two mob-state writes #103 moved under <c>World._lock</c>, and the Lua gate's half of the #90 rule.
///
/// <para>Both writes were on a player's read loop with no lock held, on state the world reads under one.
/// The item hand-off was the worse of the two: <c>Mob.Handed</c> is a plain <c>List</c> that
/// <c>World.TryDamage</c> ENUMERATES under the lock when the creature dies, so a hand landing at the wrong
/// moment threw "Collection was modified" inside the world lock rather than merely losing a race.</para>
///
/// <para>The Session callers are now one-line delegations to the World methods tested here, so what these
/// pin is the semantics those methods have to keep.</para>
/// </summary>
[Collection("world")]
public class MobStateLockTests
{
    private readonly SessionFixture _fx;

    public MobStateLockTests(SessionFixture fx) => _fx = fx;

    private const ushort HandMap = 60020, ClaimMap = 60021, RaceMap = 60022, GateMap = 60023;

    /// <summary>A world mob standing on <paramref name="map"/>, registered so the world can find it.</summary>
    private Mob NodeOn(ushort map, string name, int hp = 100)
    {
        var mob = new Mob(_fx.World.AllocateMobId(), 1, 5, 5, name, hp);
        _fx.World.AddMob(map, mob);
        return mob;
    }

    // =====================================================================================================
    // The item hand-off.
    // =====================================================================================================

    /// <summary>The creature carries what it is handed, and a second hand-off joins the first rather than
    /// replacing the list — the <c>??=</c> that used to run unlocked is inside the lock now, so two hands
    /// cannot both see a null list and both create one.</summary>
    [Fact]
    public void HandingAnItemPutsItOnTheCreature()
    {
        var mob = NodeOn(HandMap, "PackMule");

        _fx.World.HandItemToMob(mob, new InvItem(0, 42, 3, 100) { CustomName = "a bent sword", Owner = "someone" });
        _fx.World.HandItemToMob(mob, new InvItem(0, 43, 1, 50));

        Assert.NotNull(mob.Handed);
        Assert.Equal(2, mob.Handed!.Count);
        Assert.Equal(42, mob.Handed[0].ItemId);
        Assert.Equal(3, mob.Handed[0].Amount);
        Assert.Equal("a bent sword", mob.Handed[0].CustomName);
        Assert.Equal("someone", mob.Handed[0].Owner);   // bonded items ride along still bound
        Assert.Equal(43, mob.Handed[1].ItemId);
    }

    /// <summary>Many hands at once lose nothing. Without the lock this is the <c>??=</c> race: two threads
    /// see a null list, both allocate, and whichever assigns second throws the other's item away — dropping
    /// the lock from <c>HandItemToMob</c> and changing nothing else loses 237 of these 1000 items.</summary>
    [Fact]
    public void ConcurrentHandOffsLoseNothing()
    {
        const int PerThread = 500;
        var mob = NodeOn(HandMap, "CrowdedMule");
        var start = new ManualResetEventSlim();
        Exception? fault = null;

        void Hand(int itemId)
        {
            try
            {
                start.Wait();
                for (int i = 0; i < PerThread; i++) _fx.World.HandItemToMob(mob, new InvItem(0, itemId, 1, 0));
            }
            catch (Exception e) { fault = e; }
        }

        var a = new Thread(() => Hand(1)) { IsBackground = true };
        var b = new Thread(() => Hand(2)) { IsBackground = true };
        a.Start(); b.Start();
        start.Set();
        Assert.True(a.Join(TimeSpan.FromSeconds(30)) && b.Join(TimeSpan.FromSeconds(30)), "a hand-off thread hung");

        Assert.Null(fault);
        Assert.Equal(PerThread * 2, mob.Handed!.Count);
    }

    // =====================================================================================================
    // The harvest claim.
    // =====================================================================================================

    /// <summary>The four claim rules, unchanged from when they ran unlocked in Session.Harvest: a free node
    /// is taken, its owner may keep swinging, everyone else is refused, and a lapsed claim heals the node to
    /// full and frees it for the next person.</summary>
    [Fact]
    public void ClaimSemanticsAreWhatTheyWere()
    {
        var world = _fx.World;
        var node = NodeOn(ClaimMap, "Iron Vein", hp: 100);
        long now = 1_000_000;

        Assert.True(world.TryClaimHarvestNode(node, claimant: 7, now, claimMs: 120_000));
        Assert.Equal(7u, node.HarvestClaimBy);
        Assert.Equal(now + 120_000, node.HarvestClaimUntil);

        // The owner keeps it, and the expiry is pushed out again on every swing.
        Assert.True(world.TryClaimHarvestNode(node, claimant: 7, now + 1_000, claimMs: 120_000));
        Assert.Equal(now + 1_000 + 120_000, node.HarvestClaimUntil);

        // Anyone else is refused, and nothing about the node changes.
        node.Hp = 40;
        Assert.False(world.TryClaimHarvestNode(node, claimant: 9, now + 2_000, claimMs: 120_000));
        Assert.Equal(7u, node.HarvestClaimBy);
        Assert.Equal(40, node.Hp);

        // Past the expiry the node heals to full and the next comer takes it.
        Assert.True(world.TryClaimHarvestNode(node, claimant: 9, node.HarvestClaimUntil + 1, claimMs: 120_000));
        Assert.Equal(9u, node.HarvestClaimBy);
        Assert.Equal(node.MaxHp, node.Hp);
    }

    /// <summary>
    /// <b>Two players swinging at one free node — exactly one gets the claim.</b> This is why the claim is a
    /// single World method and not the reset/claim pair the issue sketched: with the lapse check, the owner
    /// test and the stamp in three separate acquisitions, both callers could see the node free and both stamp
    /// their own id, which is the check-then-act shape #26 exists to remove.
    ///
    /// <para>Barrier-released, so the two calls overlap rather than queue, and the node is freed between
    /// rounds by the post-phase action. Not a tautology: splitting the method into that reset/check/claim
    /// trio and changing nothing else double-claims 4 of these 300 rounds.</para>
    /// </summary>
    [Fact]
    public void TwoHarvestersRacingForOneNodeExactlyOneWins()
    {
        const int Rounds = 300;
        var world = _fx.World;
        var node = NodeOn(RaceMap, "Contested Vein", hp: 100);

        var got = new bool[2];
        int rounds = 0, winners = 0, doubleWins = 0;
        Exception? fault = null;

        var barrier = new Barrier(2, _ =>
        {
            try
            {
                rounds++;
                if (got[0] && got[1]) doubleWins++;
                if (got[0] || got[1]) winners++;
                node.HarvestClaimBy = 0;      // free it for the next round; nothing else is running
                node.HarvestClaimUntil = 0;
                got[0] = got[1] = false;
            }
            catch (Exception e) { fault = e; }
        });

        void Swing(int slot, uint id)
        {
            for (int i = 0; i < Rounds; i++)
            {
                got[slot] = world.TryClaimHarvestNode(node, id, 5_000_000, 120_000);
                barrier.SignalAndWait();
            }
        }

        var a = new Thread(() => Swing(0, 11)) { IsBackground = true, Name = "harvester-a" };
        var b = new Thread(() => Swing(1, 22)) { IsBackground = true, Name = "harvester-b" };
        a.Start(); b.Start();
        Assert.True(a.Join(TimeSpan.FromSeconds(30)) && b.Join(TimeSpan.FromSeconds(30)), "a harvester hung");

        Assert.Null(fault);
        Assert.Equal(Rounds, rounds);
        Assert.Equal(0, doubleWins);
        Assert.Equal(Rounds, winners);   // and it is never refused to both
    }

#if DEBUG
    /// <summary>
    /// The Lua gate's half of the #90 rule. A script may reach back into the world — <c>MobContext.vanish</c>
    /// and <c>say</c> always have — so the gate calling INTO the world is legal; entering Lua while already
    /// holding <c>World._lock</c> is the direction that deadlocks, and until #103 nothing said so.
    ///
    /// <para>Asserted at <see cref="MobScript.Fire"/> rather than inside <c>Session.EnterScriptGate</c>,
    /// because the gate is static and has no World to ask, and Fire is the entry that can realistically be
    /// reached from inside the lock — the tick queues these hooks precisely so it can drain them after
    /// releasing it. <c>LuaVerbHost</c> and <c>NpcScript</c> hold no World at all, so their gate entries are
    /// covered by the documented rule rather than by this assert; see the report on #103.</para>
    ///
    /// <para>Both directions, because only the pair means anything: the legal one has to stay silent.
    /// Debug-only by construction, like the #29 lock-order test.</para>
    /// </summary>
    [Fact]
    public void FiringALuaHookUnderTheWorldLockAsserts()
    {
        var mob = NodeOn(GateMap, "HookedCreature");
        var ctx = new MobContext(_fx.World, GateMap, mob, null);

        // The key need not have a script: Fire asserts before it looks the hook up, which is the point —
        // the rule is about the thread's lock state, not about whether there is Lua to run.
        var wrongWay = Record.Exception(
            () => _fx.World.UnderWorldLockForTest(() => MobScript.Fire("nobody", MobScript.OnAttacked, ctx)));
        Assert.NotNull(wrongWay);
        Assert.Contains("lock order violated", wrongWay!.Message);

        var rightWay = Record.Exception(() => MobScript.Fire("nobody", MobScript.OnAttacked, ctx));
        Assert.Null(rightWay);
    }
#endif
}
