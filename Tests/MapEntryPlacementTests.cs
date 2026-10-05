using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// #122: what a map entry places, and where. <c>World.EnterMap</c> runs <c>SpawnDirector.EnsureMaterialized</c>
/// before it adds the newcomer to the map's player list, and three of the placements that makes build the
/// tiles they must avoid from that list (<c>World.OccupiedTiles</c>): a due batch group's fill, the cave
/// ambush traps' top-up and Sute's Cave's cold tiles. The newcomer is not on the list yet, so the tile they
/// were arriving on read as free, and a creature, a hidden ambush trap or a cold tile could be put on it. The
/// tick's refills never had the gap: a player the tick sees is already on the list. The fix hands the arrival
/// tile to all three as taken (the issue's first option) and keeps the locked section's order: the newcomer
/// still joins the list after the room is filled, where it always did.
///
/// <para>The first-entry spawn POINTS are not part of this. <c>Materialize</c> places through
/// <c>World.FreeSpawnTile</c>, which tests the ground and the live creatures and has never looked at any
/// player, on this path or the tick's, so it reads no taken set to add the tile to. That is unchanged.</para>
///
/// <para>The group fact runs on the shared fixture world, on map 485 (Buya Legend, 8x8, nothing of its own
/// spawns there), like <see cref="SpawnDirectorTests"/>' group facts. The two trap facts each build a
/// <see cref="World"/> of their own: they cover a real cave map with filler traps that no seam can take back
/// off, and forget its spawn roster so that nothing but the top-up under test places anything there.</para>
/// </summary>
[Collection("world")]
public class MapEntryPlacementTests
{
    private readonly SessionFixture _fx;

    public MapEntryPlacementTests(SessionFixture fx) => _fx = fx;

    private const ushort GroupMap = 485;   // Buya Legend: 8x8, a map file on disk, nothing spawns on it

    /// <summary>A trap kind nothing springs, counts or tops up: it only makes its tile taken.</summary>
    private const string FillerKind = "test_filler";

    private World.SpawnDirector Spawns => _fx.World.SpawnsForTest;

    private static MobDef Creature(int id, string key) =>
        new()
        {
            Id = id,
            Key = key,
            Name = "Test " + key,
            Look = 1,
            Color = 0,
            Hp = 100,
            Exp = 0,
            Level = 1,
            MoveTime = 1_000_000,
            Stationary = true,
        };

    private List<Mob> MobsOf(Session viewer, ushort map, MobDef def) =>
        _fx.World.View(viewer, map).mobs.Where(m => m.DefId == def.Id).ToList();

    private static void AssertGroupMapIsEmptyContent()
    {
        Assert.True(Content.Maps.TryGetValue(GroupMap, out var info) && info.Xs == 8 && info.Ys == 8);
        Assert.Empty(Content.SpawnsFor(GroupMap));
        Assert.DoesNotContain(Content.AreaSpawns, a => a.Map == GroupMap);
        Assert.DoesNotContain(Content.Npcs, n => n.Map == GroupMap);
        Assert.False(Content.Ambushes.ContainsKey(GroupMap));
        Assert.DoesNotContain(GroupMap, SuteAi.CaveMaps);
    }

    /// <summary>A session on <paramref name="world"/> standing on <paramref name="tile"/>, NOT yet on any
    /// map's list: <see cref="SessionFixture.PlayerWith"/> enters for the caller and drops what
    /// <c>EnterMap</c> returns, and these facts need the entry itself.</summary>
    private Session Seat(World world, string name, ushort map, (ushort X, ushort Y) tile)
    {
        var character = new Character
        {
            SchemaVersion = Character.CurrentSchemaVersion,
            Id = world.AllocatePlayerId(),
            Name = name,
            Map = map,
            X = tile.X,
            Y = tile.Y,
        };
        return new Session(new RecordingOutbound($"recorder:{name}"), 2005, _fx.Store, world, character);
    }

    // =====================================================================================================
    // The batch group: deterministic, because a group's fill is exhaustive.
    // =====================================================================================================

    /// <summary>A due group whose cap is every open tile on the map, met by a newcomer arriving on one of
    /// them. <c>FillMember</c> rolls its budget and then falls back to <c>OpenTiles</c>, so the fill takes
    /// every tile its taken set leaves open: before the fix that included the newcomer's own tile, and a
    /// creature was built on it. Now the arrival tile is taken, and the fill takes every OTHER open tile,
    /// which is what shows the tile was skipped rather than missed by the roll.
    ///
    /// <para>Red on master's code: "Assert.DoesNotContain() Failure", a creature on the arrival tile.
    /// Falsified by passing <c>null</c> for the arrival tile to <c>OccupiedTiles</c> in
    /// <c>RefillGroups</c>: the same.</para></summary>
    [Fact]
    public void ADueGroupThatWantsEveryTileLeavesTheArrivalTileFree()
    {
        AssertGroupMapIsEmptyContent();
        var open = World.SpawnDirector.OpenTiles(GroupMap, new HashSet<(int, int)>(), 0, 0, 0, 0);
        Assert.True(open.Count > 2, $"map {GroupMap} has {open.Count} open tiles; the fact needs a room to fill");
        var arrival = open[open.Count / 2];   // ground a creature could stand on, which is the whole question
        int cap = open.Count;                 // the group wants every open tile on the map
        var def = Creature(990_201, "test_entry_fill");

        _fx.World.UnderWorldLockForTest(() => Spawns.AddGroupForTest(GroupMap, timerSec: 300, (def, cap)));
        var (newcomer, _) = _fx.Player("FillNewcomer", GroupMap, arrival.X, arrival.Y);
        try
        {
            var placed = MobsOf(newcomer, GroupMap, def);
            Assert.DoesNotContain(placed, m => m.X == arrival.X && m.Y == arrival.Y);
            Assert.Equal(cap - 1, placed.Count);   // every other open tile has its creature
            Assert.Equal(cap - 1, placed.Select(m => (m.X, m.Y)).Distinct().Count());
        }
        finally
        {
            _fx.World.LeaveMap(newcomer, GroupMap);
            _fx.World.UnderWorldLockForTest(() => Spawns.ForgetMapForTest(GroupMap));
        }
    }

    // =====================================================================================================
    // The two trap top-ups: random draws with a budget, so the arrival tile is made the only tile left.
    // =====================================================================================================

    /// <summary>The cave ambush traps, on map 140 (Crystaline Isle, 30x18; <c>AmbushConfig.csv</c> gives it
    /// ten traps under a cap of twelve live creatures).
    ///
    /// <para>The trap top-ups have no exhaustive fallback the way a group's fill does: each draws a fixed
    /// budget of random tiles (<c>World.TryPickMapTile</c>: x in [1, xs-1], y in [1, ys-1]) and stops. So the
    /// fact makes the arrival tile the ONLY tile a trap could take: every other tile of the draw range carries
    /// a filler trap, which the top-up counts as taken. The newcomer then enters again and again, leaving
    /// between entries (someone already on the list has their tile taken, before the fix too). Before the
    /// fix every entry had its budget's odds of putting the trap on the arrival tile; here 29x17 = 493 tiles
    /// are drawn from, ten traps times four tries is 40 draws an entry, and the chance that 500 entries all
    /// miss is (492/493)^20000, about 2e-18.</para>
    ///
    /// <para>The second half is the control that keeps the first honest. A newcomer arriving ELSEWHERE does
    /// get a trap on that same tile within as many entries: the tile is one the top-up can and does use, and
    /// it is the newcomer standing on it that keeps it free.</para>
    ///
    /// <para>Red on master's code: "entry N: a 'ambush' trap was placed on (x,y)". Falsified by passing
    /// <c>null</c> for the arrival tile to <c>OccupiedTiles</c> in <c>RefillAmbushLocked</c>: the
    /// same.</para></summary>
    [Fact]
    public void NoAmbushTrapIsPlacedOnTheArrivalTile()
    {
        const ushort map = 140;
        Assert.True(Content.Ambushes.TryGetValue(map, out var cfg) && cfg.Count > 0 && cfg.MobCap > 0,
                    $"map {map} should carry ambush traps (AmbushConfig.csv)");
        Assert.True(Content.Maps.TryGetValue(map, out var info) && info.Xs == 30 && info.Ys == 18);
        TheArrivalTileStaysFreeOf("ambush", map, entries: 500);
    }

    /// <summary>Sute's Cave's cold tiles, on map 445 (Death's Isthmus, 28x20, one of the seven rooms of
    /// <see cref="SuteAi.CaveMaps"/>; <see cref="SuteAi.FrigidTrapsPerMap"/> tiles a room).
    ///
    /// <para>The same construction as the ambush fact above, for the same reason. 27x19 = 513 tiles are drawn
    /// from, four tiles times four tries is 16 draws an entry, and the chance that 1,000 entries all miss
    /// before the fix is (512/513)^16000, about 3e-14.</para>
    ///
    /// <para>Red on master's code: "entry N: a 'frigid' trap was placed on (x,y)". Falsified by passing
    /// <c>null</c> for the arrival tile to <c>OccupiedTiles</c> in <c>RefillFrigidLocked</c>: the
    /// same.</para></summary>
    [Fact]
    public void NoColdTileIsPlacedOnTheArrivalTile()
    {
        const ushort map = 445;
        Assert.Contains(map, SuteAi.CaveMaps);
        Assert.Equal(4, SuteAi.FrigidTrapsPerMap);
        Assert.True(Content.Maps.TryGetValue(map, out var info) && info.Xs == 28 && info.Ys == 20);
        TheArrivalTileStaysFreeOf(SuteAi.FrigidTrapKind, map, entries: 1000);
    }

    /// <summary>The body of both trap facts: a world of its own, the map's roster forgotten, every tile of
    /// the draw range but one covered by a filler trap; then a newcomer arriving on that one tile, entering
    /// <paramref name="entries"/> times; then the control, a newcomer arriving elsewhere.</summary>
    private void TheArrivalTileStaysFreeOf(string kind, ushort map, int entries)
    {
        var world = new World();   // constructed, never started: nothing moves unless this fact moves it
        world.UnderWorldLockForTest(() => world.SpawnsForTest.ForgetMapForTest(map));
        var info = Content.Maps[map];

        var free = Placeable(map, except: null);
        for (int y = 1; y < info.Ys; y++)
            for (int x = 1; x < info.Xs; x++)
                if (x != free.X || y != free.Y) world.PlaceTrap(map, (ushort)x, (ushort)y, FillerKind, ownerId: 0);

        // The protection: the newcomer arrives on the one tile left.
        var newcomer = Seat(world, "ArrivesOnTheLastTile", map, free);
        for (int i = 1; i <= entries; i++)
        {
            world.EnterMap(newcomer, map);
            bool onArrival = world.TrapsNear(map, free.X, free.Y, 0).Any(t => t.Kind == kind);
            world.LeaveMap(newcomer, map);
            Assert.False(onArrival, $"entry {i}: a '{kind}' trap was placed on ({free.X},{free.Y}), the tile the newcomer was arriving on");
        }

        // The control: a newcomer arriving anywhere else, and the same tile is used.
        var other = Seat(world, "ArrivesElsewhere", map, Placeable(map, except: free));
        int placedOn = 0;
        for (int i = 1; i <= entries && placedOn == 0; i++)
        {
            world.EnterMap(other, map);
            if (world.TrapsNear(map, free.X, free.Y, 0).Any(t => t.Kind == kind)) placedOn = i;
            world.LeaveMap(other, map);
        }
        Assert.True(placedOn > 0,
            $"no '{kind}' trap reached ({free.X},{free.Y}) in {entries} entries by a newcomer standing elsewhere, so the "
          + "protected half above would prove nothing: the top-up does not place there at all");
    }

    /// <summary>The first tile of the trap draw range, in row order, that a trap or a creature may take:
    /// walkable, not a warp (<see cref="World.SpawnDirector.Placeable(ushort, IReadOnlySet{ValueTuple{int, int}}, int, int)"/>).</summary>
    private static (ushort X, ushort Y) Placeable(ushort map, (ushort X, ushort Y)? except)
    {
        var info = Content.Maps[map];
        var none = new HashSet<(int, int)>();
        for (int y = 1; y < info.Ys; y++)
            for (int x = 1; x < info.Xs; x++)
                if (World.SpawnDirector.Placeable(map, none, x, y) && (except is not { } e || e.X != x || e.Y != y))
                    return ((ushort)x, (ushort)y);
        throw new InvalidOperationException($"map {map} has no placeable tile in its trap draw range");
    }

    // =====================================================================================================
    // Unchanged: the newcomer's draw list.
    // =====================================================================================================

    /// <summary>What <c>World.EnterMap</c> hands back for the newcomer to draw is built as it was: every
    /// OTHER player on the map, at the tile they hold, and every creature on it, the ones this entry's own
    /// fill just placed included. A resident enters first (before the group exists, so their entry fills
    /// nothing); the newcomer's entry finds the group due, fills it, and its draw list carries the three
    /// creatures and the resident, and not the newcomer. After the entry the newcomer is on the map's list.
    ///
    /// <para>Not a guard on this change: a fact that passes on master's code and after it, pinning that the
    /// fix moved nothing in the snapshot the caller draws from.</para></summary>
    [Fact]
    public void TheNewcomersDrawListIsTheRoomItsEntryLeft()
    {
        AssertGroupMapIsEmptyContent();
        var def = Creature(990_202, "test_entry_drawlist");
        var open = World.SpawnDirector.OpenTiles(GroupMap, new HashSet<(int, int)>(), 0, 0, 0, 0);
        var (resident, _) = _fx.Player("DrawListResident", GroupMap, open[0].X, open[0].Y);   // before the group
        Session? newcomer = null;
        try
        {
            _fx.World.UnderWorldLockForTest(() => Spawns.AddGroupForTest(GroupMap, timerSec: 300, (def, 3)));
            newcomer = Seat(_fx.World, "DrawListNewcomer", GroupMap, open[^1]);
            var (peers, mobs) = _fx.World.EnterMap(newcomer, GroupMap);

            var peer = Assert.Single(peers);
            Assert.Same(resident, peer.Session);
            Assert.Equal(resident.PlayerId, peer.Id);
            Assert.Equal((resident.PlayerX, resident.PlayerY), (peer.X, peer.Y));

            var placed = MobsOf(resident, GroupMap, def);
            Assert.Equal(3, placed.Count);
            Assert.Equal(placed.Select(m => m.Id).OrderBy(id => id), mobs.Select(m => m.Id).OrderBy(id => id));

            Assert.Contains(_fx.World.View(resident, GroupMap).peers, p => p.Session == newcomer);
        }
        finally
        {
            _fx.World.LeaveMap(resident, GroupMap);
            if (newcomer is not null) _fx.World.LeaveMap(newcomer, GroupMap);
            _fx.World.UnderWorldLockForTest(() => Spawns.ForgetMapForTest(GroupMap));
        }
    }
}
