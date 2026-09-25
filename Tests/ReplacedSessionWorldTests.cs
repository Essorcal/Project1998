using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// #168 item 5: the world stops acting on a session a second login has REPLACED (<c>Session.IsReplaced</c>).
///
/// <para><b>The window.</b> An account's second login runs <c>KickForReplacement</c> on the old session and then
/// loads the character. The old session stays on its map until its read loop unwinds into its teardown. #183
/// taught the three registry lookups to skip it; everything that walks a map's <c>Players</c> list directly still
/// found it. The harmful case is a death in the window: <c>Die()</c> spills coins and part of the bag onto the
/// floor while the new session, which loaded before the death, still carries them. That is a duplication.</para>
///
/// <para><b>What changed</b> (the design is Caleb's, option 5b of the #168 decision sheet): <c>World.PeerAt</c>,
/// <c>World.RetargetByThreat</c>'s three player reads and the three <c>MobAiTick</c> reads the sheet names skip a
/// replaced session, and <c>Session.TakeDamage</c> lands nothing on one. Every read is a volatile field read
/// with no session monitor, the #183 shape, so it is legal under <c>World._lock</c>.</para>
///
/// <para><b>How the facts are shaped.</b> Each mob-AI fact runs three times: the old session <c>Live</c> (never
/// kicked, which is today's behaviour and must not move), <c>Replaced</c> (kicked, still on its map) and
/// <c>Left</c> (kicked and off its map, as its teardown leaves it). The assertion is that <c>Replaced</c> behaves
/// exactly like <c>Left</c>: in the window the creature already treats the old session as gone. The damage facts
/// run each of the five intakes against a live and a replaced session with a lethal blow.</para>
///
/// <para>Hygiene: every map is content-free and used by this class alone, every name is unique to it, and every
/// session and creature is removed in a <c>finally</c>. The maps sit above the 59000-65000 instance band on
/// purpose: a death there charges only exp, and the live controls need the full penalty, pile included, to show
/// what the gate prevents. The one real map is Chonsa Arena (357), a <c>MapPvP=1</c> row with no spawn, because
/// the pet's PvP-foe read runs only on a PvP map.</para>
/// </summary>
[Collection("world")]
public class ReplacedSessionWorldTests
{
    private const ushort PeerMap = 65301, MonitorMap = 65302;
    /// <summary>Each mob-AI fact takes three consecutive maps, one per <see cref="Shape"/>.</summary>
    private const ushort TargetMap = 65303, OwnerMap = 65306, PickMap = 65309, CornerMap = 65312, CurrentMap = 65315;
    /// <summary>The damage facts take ten: two per <see cref="Blow"/>, live then replaced.</summary>
    private const ushort BlowMap = 65320;
    /// <summary>Chonsa Arena: a real PvP map (<c>game-data/Maps.csv</c>, 33x33) with no spawn row, used by no other
    /// test class.</summary>
    private const ushort Arena = 357;

    /// <summary>Where the new session stands when a fact wants it out of the way: outside every creature's
    /// notice box (<c>World.NoticeX</c>/<c>NoticeY</c>, 10 and 9) and adjacent to nobody.</summary>
    private const ushort FarX = 30, FarY = 30;

    private const int Lethal = 100_000;

    public enum Shape { Live, Replaced, Left }

    public enum Blow { MobSwing, MobSpell, PlayerSwing, PlayerSpell, Room }

    private readonly SessionFixture _fx;

    public ReplacedSessionWorldTests(SessionFixture fx) => _fx = fx;

    /// <summary>Sessions and creatures a fact created, removed in its <c>finally</c>.</summary>
    private sealed class Cleanup(SessionFixture fx)
    {
        private readonly List<(Session s, ushort map)> _players = new();
        private readonly List<(Mob mob, ushort map)> _mobs = new();
        public Session Add(Session s, ushort map) { _players.Add((s, map)); return s; }
        public Mob Add(Mob mob, ushort map) { fx.World.AddMob(map, mob); _mobs.Add((mob, map)); return mob; }
        public void Forget(Session s) => _players.RemoveAll(p => ReferenceEquals(p.s, s));
        public void Run()
        {
            foreach (var (s, map) in _players) fx.World.LeaveMap(s, map);
            foreach (var (mob, map) in _mobs) fx.World.DespawnMob(map, mob);
        }
    }

    /// <summary>The old session for one account in the given shape. <c>Live</c>: one ordinary session. Otherwise a
    /// second session for the same name enters at (<see cref="FarX"/>, <see cref="FarY"/>) and the old one is kicked
    /// through the real <c>KickForReplacement</c>; for <c>Left</c> it then leaves its map, which is what its
    /// teardown does.</summary>
    private Session Account(Cleanup c, string name, Shape shape, ushort map, ushort x, ushort y)
    {
        var (old, _) = _fx.Player(name, map, x, y);
        c.Add(old, map);
        if (shape == Shape.Live) return old;

        var (fresh, _) = _fx.Player(name, map, FarX, FarY);
        c.Add(fresh, map);
        old.KickForReplacement();
        Assert.True(old.IsReplaced);
        if (shape == Shape.Left) { _fx.World.LeaveMap(old, map); c.Forget(old); }
        return old;
    }

    private Session Bystander(Cleanup c, string name, ushort map, ushort x, ushort y)
    {
        var (s, _) = _fx.Player(name, map, x, y);
        return c.Add(s, map);
    }

    /// <summary>A plain creature: no wander (so a beat never rolls a step), and the three timers zero so nothing
    /// fires that the fact did not set up.</summary>
    private Mob Creature(ushort x, ushort y, string name, bool aggressive = false) =>
        new(_fx.World.AllocateMobId(), 1, x, y, name, 100) { Wander = false, Aggressive = aggressive };

    private World.MobTickContext Beat(ushort map, Mob mob, World.TickQueues? q = null)
    {
        World.MobTickContext ctx = null!;
        _fx.World.UnderWorldLockForTest(() =>
        {
            ctx = _fx.World.MobTickContextForTest(map, q);
            World.MobAiTick.Step(ctx, mob);
        });
        return ctx;
    }

    private static ushort MapFor(ushort first, Shape shape) => (ushort)(first + (int)shape);

    // =====================================================================================================
    // PeerAt.
    // =====================================================================================================

    /// <summary>The new login arrives where the old one stood, because it loads the old one's row: two sessions
    /// on one tile. Before the kick the tile answers with the old session (it entered first, so a read that did
    /// not skip it would return it). After the kick it answers with the new one, and a live player on the next
    /// tile is still found. The old session is still on the map: the skip is the read's, not a removal.
    ///
    /// <para>Before the change the tile kept answering with the old session, so a PvP swing, a faced-tile spell,
    /// a hand-over or the look key at that tile reached a session nobody reads, and a lethal swing killed it.</para>
    /// </summary>
    [Fact]
    public void PeerAtAnswersWithTheNewSessionNotTheReplacedOne()
    {
        var c = new Cleanup(_fx);
        try
        {
            var (old, _) = _fx.Player("RswPeer", PeerMap, 5, 5);
            c.Add(old, PeerMap);
            var (fresh, _) = _fx.Player("RswPeer", PeerMap, 5, 5);
            c.Add(fresh, PeerMap);
            var neighbour = Bystander(c, "RswPeerNeighbour", PeerMap, 6, 5);

            Assert.Same(old, _fx.World.PeerAt(PeerMap, 5, 5));   // setup proof: the old session is the first hit

            old.KickForReplacement();

            Assert.Same(fresh, _fx.World.PeerAt(PeerMap, 5, 5));
            Assert.Same(neighbour, _fx.World.PeerAt(PeerMap, 6, 5));   // a live player: exactly as before
            Assert.Contains(old, _fx.World.Online.All());
        }
        finally { c.Run(); }
    }

    /// <summary>The skip takes no session monitor. <c>PeerAt</c> and a creature's beat run under
    /// <c>World._lock</c>, and <c>docs/common/Locking.md</c> forbids entering a session monitor there. With the
    /// replaced session's monitor held on another thread, both must still finish, and both must skip it: a
    /// <c>PeerAt</c> on its tile finds nobody, and a creature that was fighting it drops it without a swing.</summary>
    [Fact]
    public async Task TheWorldsReadsDoNotWaitOnTheReplacedSessionsMonitor()
    {
        var c = new Cleanup(_fx);
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task? holder = null;
        try
        {
            var old = Account(c, "RswMonitor", Shape.Replaced, MonitorMap, 5, 5);
            var mob = c.Add(Creature(5, 4, "RswMonitorMob"), MonitorMap);
            mob.TargetId = old.PlayerId;
            mob.AttackTime = 1;

            holder = Task.Run(() => old.WithState(() => { held.Set(); release.Wait(TimeSpan.FromSeconds(30)); }));
            Assert.True(held.Wait(TimeSpan.FromSeconds(10)), "the holder never took the replaced session's monitor");

            var reads = Task.Run(() => (_fx.World.PeerAt(MonitorMap, 5, 5), Beat(MonitorMap, mob)));
            bool finished = await Task.WhenAny(reads, Task.Delay(TimeSpan.FromSeconds(5))) == reads;
            release.Set();

            Assert.True(finished, "a world read blocked on the replaced session's state monitor");
            var (peer, ctx) = await reads;
            Assert.Null(peer);
            Assert.Equal(0u, mob.TargetId);
            Assert.Empty(ctx.Hits);
        }
        finally
        {
            release.Set();
            if (holder is not null) await holder.WaitAsync(TimeSpan.FromSeconds(30));
            c.Run();
        }
    }

    // =====================================================================================================
    // The creature's own reads (World.MobAiTick).
    // =====================================================================================================

    /// <summary>The chase-and-swing read. A creature fighting the old session, which stands in arm's reach. Live:
    /// it keeps the target and swings (today's behaviour). Replaced: it drops the target and queues nothing, which
    /// is exactly what it does once the old session has left.</summary>
    [Theory]
    [InlineData(Shape.Live)]
    [InlineData(Shape.Replaced)]
    [InlineData(Shape.Left)]
    public void ACreatureDropsAReplacedTargetInsteadOfSwingingAtIt(Shape shape)
    {
        ushort map = MapFor(TargetMap, shape);
        var c = new Cleanup(_fx);
        try
        {
            var old = Account(c, $"RswTarget{shape}", shape, map, 5, 6);
            var mob = c.Add(Creature(5, 5, "RswTargetMob"), map);
            mob.TargetId = old.PlayerId;
            mob.AttackTime = 1;   // swings on its first beat, whatever TickMs is configured to

            var ctx = Beat(map, mob);

            if (shape == Shape.Live)
            {
                Assert.Equal(old.PlayerId, mob.TargetId);
                Assert.Same(old, Assert.Single(ctx.Hits).target);
            }
            else
            {
                Assert.Equal(0u, mob.TargetId);
                Assert.Empty(ctx.Hits);
                Assert.Empty(ctx.MobCasts);
            }
        }
        finally { c.Run(); }
    }

    /// <summary>The pet's owner read. A conjured pet whose owner is the old session. Live: the owner is there, so
    /// the pet stays (it holds its ground). Replaced: the pet treats its owner as gone and vanishes, as it does a
    /// moment later when the owner's teardown takes it off the map.</summary>
    [Theory]
    [InlineData(Shape.Live)]
    [InlineData(Shape.Replaced)]
    [InlineData(Shape.Left)]
    public void APetTreatsAReplacedOwnerAsGone(Shape shape)
    {
        ushort map = MapFor(OwnerMap, shape);
        var c = new Cleanup(_fx);
        try
        {
            var owner = Account(c, $"RswOwner{shape}", shape, map, 5, 8);
            var pet = c.Add(new Mob(_fx.World.AllocateMobId(), 1, 5, 5, "RswPet", 100)
            {
                OwnerId = owner.PlayerId,
                Summoned = true,   // conjured: with no owner here it vanishes (RTK mob_ai_cotw.move)
            }, map);

            var ctx = Beat(map, pet);

            if (shape == Shape.Live) Assert.Empty(ctx.ExpiredPets);
            else Assert.Same(pet, Assert.Single(ctx.ExpiredPets).mob);
        }
        finally { c.Run(); }
    }

    /// <summary>The pet's PvP-foe read, on an arena. The pet's owner is live and is trading blows with the old
    /// session (its <c>PvpFoeId</c>), and a creature the owner has hit stands next to the pet. Live: the pet goes
    /// for the foe and ignores the creature (today's behaviour). Replaced: the foe counts as gone, so the pet
    /// serves its owner instead and swings at the creature, exactly as it does once the foe has left.
    ///
    /// <para>Without this read's skip the pet would still pick the replaced foe and the target read below would
    /// drop it on the same beat: the pet would stand idle for the window instead of defending its owner.</para>
    /// </summary>
    [Theory]
    [InlineData(Shape.Live)]
    [InlineData(Shape.Replaced)]
    [InlineData(Shape.Left)]
    public void AnArenaPetDoesNotChaseAReplacedFoe(Shape shape)
    {
        Assert.True(Content.IsPvpMap(Arena), "the pet's PvP-foe read runs only where IsPvpMap is true");
        var c = new Cleanup(_fx);
        try
        {
            var owner = Bystander(c, $"RswPetOwner{shape}", Arena, 10, 14);
            var foe = Account(c, $"RswPetFoe{shape}", shape, Arena, 10, 20);
            owner.MarkPvpFoe(foe.PlayerId);
            var pet = c.Add(new Mob(_fx.World.AllocateMobId(), 1, 10, 10, "RswArenaPet", 100)
            {
                OwnerId = owner.PlayerId,
                AttackTime = 1,
            }, Arena);
            var hit = c.Add(Creature(11, 10, "RswOwnersPrey"), Arena);   // cardinally adjacent to the pet
            hit.AddThreat(owner.PlayerId, 10);                              // the owner has hit it

            var ctx = Beat(Arena, pet);

            if (shape == Shape.Live)
            {
                Assert.Equal(foe.PlayerId, pet.TargetId);
                Assert.Empty(ctx.MobHits);
            }
            else
            {
                Assert.Equal(0u, pet.TargetId);
                var swing = Assert.Single(ctx.MobHits);
                Assert.Same(pet, swing.attacker);
                Assert.Same(hit, swing.victim);
            }
        }
        finally { c.Run(); }
    }

    // =====================================================================================================
    // RetargetByThreat's three reads. One fact per read: each is red without its own skip.
    // =====================================================================================================

    /// <summary>The pick. The old session has out-damaged a live player, neither stands in reach, and the
    /// creature has no target yet. Live: it picks the old session. Replaced: it picks the live player, as it does
    /// once the old session has left. (Without the skip in the pick loop it would pick the replaced session and
    /// the target read would drop it on the same beat: a creature that fights nobody.)</summary>
    [Theory]
    [InlineData(Shape.Live)]
    [InlineData(Shape.Replaced)]
    [InlineData(Shape.Left)]
    public void TheThreatPickSkipsAReplacedSession(Shape shape)
    {
        ushort map = MapFor(PickMap, shape);
        var c = new Cleanup(_fx);
        try
        {
            var old = Account(c, $"RswPickOld{shape}", shape, map, 10, 13);
            var live = Bystander(c, $"RswPickLive{shape}", map, 13, 10);
            var mob = c.Add(Creature(10, 10, "RswPickMob"), map);
            mob.AddThreat(old.PlayerId, 100);
            mob.AddThreat(live.PlayerId, 10);

            Beat(map, mob);

            Assert.Equal(shape == Shape.Live ? old.PlayerId : live.PlayerId, mob.TargetId);
        }
        finally { c.Run(); }
    }

    /// <summary>The cornered test. The creature is fighting a live player out of reach, the old session stands
    /// in reach, and a second live player out of reach has done the most damage. Live: the old session in reach
    /// corners the creature, so it turns on the one it can reach, the old session. Replaced: nobody live is in
    /// reach, so it is not cornered and it turns to the top-threat player, as it does once the old session has
    /// left. (Without this skip the replaced session would still corner it, the pick would be confined to arm's
    /// reach, the pick loop would skip the replaced session there, and the creature would keep chasing its
    /// out-of-reach target.)</summary>
    [Theory]
    [InlineData(Shape.Live)]
    [InlineData(Shape.Replaced)]
    [InlineData(Shape.Left)]
    public void AReplacedSessionDoesNotCornerACreature(Shape shape)
    {
        ushort map = MapFor(CornerMap, shape);
        var c = new Cleanup(_fx);
        try
        {
            var chased = Bystander(c, $"RswCornerChased{shape}", map, 10, 16);
            var old = Account(c, $"RswCornerOld{shape}", shape, map, 10, 11);   // in reach
            var top = Bystander(c, $"RswCornerTop{shape}", map, 13, 10);
            // Aggressive, so the passive creature's "forget whoever left the map" rule does not fire first in the
            // Left shape; the target is non-zero, so the unprovoked-aggro scan does not run either.
            var mob = c.Add(Creature(10, 10, "RswCornerMob", aggressive: true), map);
            mob.TargetId = chased.PlayerId;
            mob.AddThreat(chased.PlayerId, 10);
            mob.AddThreat(old.PlayerId, 50);
            mob.AddThreat(top.PlayerId, 100);

            Beat(map, mob);

            Assert.Equal(shape == Shape.Live ? old.PlayerId : top.PlayerId, mob.TargetId);
        }
        finally { c.Run(); }
    }

    /// <summary>The current-target read. The creature is fighting the old session, which stands in reach; a live
    /// player also stands in reach, and a second live player out of reach has done the most damage. Live: its
    /// current target is in reach, so it is not cornered and it turns to the top-threat player. Replaced: its
    /// current target counts as gone, the live player in reach corners it, and it turns on that one, as it does
    /// once the old session has left.</summary>
    [Theory]
    [InlineData(Shape.Live)]
    [InlineData(Shape.Replaced)]
    [InlineData(Shape.Left)]
    public void AReplacedCurrentTargetCountsAsGone(Shape shape)
    {
        ushort map = MapFor(CurrentMap, shape);
        var c = new Cleanup(_fx);
        try
        {
            var old = Account(c, $"RswCurrentOld{shape}", shape, map, 10, 11);   // in reach
            var reach = Bystander(c, $"RswCurrentReach{shape}", map, 9, 10);    // in reach
            var top = Bystander(c, $"RswCurrentTop{shape}", map, 10, 16);
            var mob = c.Add(Creature(10, 10, "RswCurrentMob", aggressive: true), map);   // see the fact above
            mob.TargetId = old.PlayerId;
            mob.AddThreat(old.PlayerId, 50);
            mob.AddThreat(reach.PlayerId, 10);
            mob.AddThreat(top.PlayerId, 100);

            Beat(map, mob);

            Assert.Equal(shape == Shape.Live ? top.PlayerId : reach.PlayerId, mob.TargetId);
        }
        finally { c.Run(); }
    }

    // =====================================================================================================
    // TakeDamage: a lethal blow on a replaced session lands nothing, so it cannot die and drop its pile.
    // =====================================================================================================

    /// <summary>A lethal blow from each of the five intakes. The creature swing goes through the tick's own queue
    /// and flush, which is also the shape of a swing queued before the kick and applied after it.
    ///
    /// <para>Live (today's behaviour, pinned): the blow kills, a coin pile locked to the dead player lands on its
    /// tile, and the death's save writes a row with 0 HP and the coins gone. Replaced: nothing lands. HP, coins
    /// and the row are exactly as the kick left them, nothing is on the floor, and the session is not dead. That
    /// is the duplication closed: the new session loaded those coins, and the old one can no longer drop
    /// them.</para>
    ///
    /// <para>Coins rather than bag items because the coin spill is certain (5-35% of 1000 is never 0) and the bag
    /// spill is a coin flip per stack.</para></summary>
    [Theory]
    [InlineData(Blow.MobSwing, false)]
    [InlineData(Blow.MobSwing, true)]
    [InlineData(Blow.MobSpell, false)]
    [InlineData(Blow.MobSpell, true)]
    [InlineData(Blow.PlayerSwing, false)]
    [InlineData(Blow.PlayerSwing, true)]
    [InlineData(Blow.PlayerSpell, false)]
    [InlineData(Blow.PlayerSpell, true)]
    [InlineData(Blow.Room, false)]
    [InlineData(Blow.Room, true)]
    public void ALethalBlowOnAReplacedSessionLandsNothingAndDropsNoPile(Blow blow, bool replaced)
    {
        ushort map = (ushort)(BlowMap + (int)blow * 2 + (replaced ? 1 : 0));
        string name = $"RswBlow{blow}{(replaced ? "Old" : "Live")}";
        var c = new Cleanup(_fx);
        try
        {
            var (victim, _, ch) = _fx.PlayerWith(name, v => { v.MaxHp = 1000; v.Hp = 10; v.Coins = 1000; v.Level = 1; },
                                                 map, 5, 5);
            c.Add(victim, map);
            var attacker = Bystander(c, name + "Foe", map, 5, 7);
            Assert.True(_fx.Store.Save(ch));   // a row to compare against
            if (replaced)
            {
                var (fresh, _) = _fx.Player(name, map, 5, 5);   // the new login, on the tile it loaded
                c.Add(fresh, map);
                victim.KickForReplacement();
            }
            string rowBefore = Row(name);
            Assert.Empty(_fx.World.ItemsOn(map));

            Strike(blow, map, victim, attacker);

            if (replaced)
            {
                Assert.False(victim.IsDead);
                Assert.Equal(10u, ch.Hp);
                Assert.Equal(1000u, ch.Coins);
                Assert.Empty(_fx.World.ItemsOn(map));
                Assert.Equal(rowBefore, Row(name));
            }
            else
            {
                Assert.True(victim.IsDead);
                Assert.Equal(0u, ch.Hp);
                Assert.True(ch.Coins < 1000u, "the death should have spilled coins");
                var pile = Assert.Single(_fx.World.ItemsOn(map));
                Assert.Equal((-1, (ushort)5, (ushort)5, ch.Id), (pile.ItemId, pile.X, pile.Y, pile.LooterId));
                Assert.Equal(1000u - ch.Coins, (uint)pile.Amount);
                var saved = _fx.Store.Load(name).Character!;
                Assert.Equal((0u, ch.Coins), (saved.Hp, saved.Coins));
            }
        }
        finally { c.Run(); }
    }

    private void Strike(Blow blow, ushort map, Session victim, Session attacker)
    {
        var brute = new Mob { Id = _fx.World.AllocateMobId(), Name = "Rsw brute", Key = "", Level = 1,
                              MinDam = 3 * Lethal, MaxDam = 3 * Lethal, X = 5, Y = 6, Dir = 0 };
        switch (blow)
        {
            case Blow.MobSwing:
                var q = new World.TickQueues();
                q.Hits.Add((map, brute, victim));
                _fx.World.FlushTickForTest(q);
                break;
            case Blow.MobSpell:    victim.ReceiveMobSpell(Lethal, brute, "Ice ray"); break;
            case Blow.PlayerSwing: victim.ReceiveMeleeDamage(Lethal, attacker, crit: false); break;
            case Blow.PlayerSpell: victim.ReceiveSpellDamage(Lethal, attacker, "Spark"); break;
            case Blow.Room:        victim.ReceiveEnvironmentDamage(Lethal, "The cold bites."); break;
            default: throw new ArgumentOutOfRangeException(nameof(blow));
        }
    }

    private string Row(string name)
    {
        var loaded = _fx.Store.Load(name);
        Assert.Equal(CharacterLoadStatus.Ok, loaded.Status);
        return CharacterStore.Serialize(loaded.Character!);
    }
}
