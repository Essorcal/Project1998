using System.Text.RegularExpressions;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The server's own loaders read the synthetic profile (<c>test-content/synthetic/</c>) as its
/// <c>manifest.json</c> says, so a client test that writes its collision table and its walk from the manifest
/// meets the server it is talking to. Each fact points this process at the profile with the manifest's
/// environment, runs <c>Content.Load</c>, checks one promise against the server's registries, terrain and
/// object table, and puts game-data back.
///
/// <para><b>Collection <c>"tile-translation"</c>, which runs alone</b>
/// (<c>Tests/Support/ExclusiveCollections.cs</c>): the environment, <c>ServerConfig</c>, the content snapshot,
/// the <c>.map</c> cache and the <c>SObj.tbl</c> cache are one slot each for the whole process, and while a fact
/// here holds the profile every other test would read it. The <c>finally</c> restores the environment,
/// re-resolves the configuration, reloads game-data and drops both caches, and each fact then checks game-data is
/// back.</para>
///
/// <para>What each promise guards, silently broken otherwise: a <c>.map</c> shorter than its size loads as no
/// terrain at all; <c>SObj.tbl</c> falls back to <c>&lt;root&gt;/RTK-Server/rtk/SObj.tbl</c> when the profile has
/// none, and the install fallback switch does not stop that; the start and the warp landings are trusted by
/// the server with no terrain test; and a warp the manifest lists but the server dropped (an unknown destination
/// map) is just a tile.</para>
/// </summary>
[Collection("tile-translation")]
public class SyntheticProfileLoadTests
{
    private static readonly (int Dx, int Dy)[] Steps = { (0, -1), (1, 0), (0, 1), (-1, 0) };   // N E S W, the walk's dir 0-3

    /// <summary>Both maps are in the registry with the manifest's name and size, each <c>.map</c> the server read
    /// is the profile's own and holds exactly width x height x 4 bytes, and both maps are indoor.</summary>
    [Fact]
    public void Maps_load_at_their_manifest_size_from_the_profiles_own_files()
    {
        WithProfileLoaded(manifest =>
        {
            Assert.Equal(manifest.Maps.Select(m => m.Id).Order(), Content.Maps.Keys.Order());
            foreach (var map in manifest.Maps)
            {
                var info = Content.Maps[map.Id];
                Assert.Equal((map.Name, map.Width, map.Height), (info.Name, info.Xs, info.Ys));

                string file = Path.GetFullPath(Path.Combine(manifest.ProfileDir, map.File));
                Assert.Equal((long)map.Width * map.Height * 4, new FileInfo(file).Length);

                MapData? terrain;
                string loaded;
                using (var sink = LogLineSink.Acquire())
                {
                    terrain = MapData.For(map.Id);
                    loaded = sink.LineContaining($"loaded map TK{map.Id}.map");
                }
                Assert.NotNull(terrain);
                Assert.Equal((map.Width, map.Height), (terrain.Xs, terrain.Ys));
                Assert.EndsWith($" from {file}", loaded);

                Assert.Equal(map.Indoor, Content.IsIndoor(map.Id));
            }
        });
    }

    /// <summary>The object table is the profile's <c>SObj.tbl</c>, never the RTK-Server fallback; it holds the
    /// manifest's count; every object's server flag (the file's flag after <c>ObjectFlagOverrides.csv</c>) is the
    /// manifest's <c>serverFlag</c>; and every object sits on the cells the manifest gives it.</summary>
    [Fact]
    public void Object_flags_come_from_the_profiles_own_table_and_equal_the_manifest()
    {
        WithProfileLoaded(manifest =>
        {
            int count;
            string loaded;
            ObjectFlags.Invalidate();
            using (var sink = LogLineSink.Acquire())
            {
                count = ObjectFlags.Count;
                loaded = sink.LineContaining("SObj.tbl");
            }
            var line = Regex.Match(loaded,
                @"-> loaded SObj\.tbl \((\d+) objects\) from (.+?)(?: — (\d+) sprite flag\(s\) overridden by ObjectFlagOverrides\.csv)?$");
            Assert.True(line.Success, $"SObj.tbl did not load: {loaded}");
            string path = Path.GetFullPath(line.Groups[2].Value);
            Assert.Equal(Path.Combine(manifest.ProfileDir, manifest.ObjectTableFile), path);
            Assert.StartsWith(manifest.ProfileDir + Path.DirectorySeparatorChar, path);

            Assert.Equal(manifest.ObjectCount, count);
            Assert.Equal(manifest.ObjectCount.ToString(), line.Groups[1].Value);

            // Object by object first, so a wrong flag or a lost override row fails naming the object.
            Assert.Equal(Enumerable.Range(1, manifest.ObjectCount), manifest.Objects.Select(o => o.Id).Order());
            foreach (var o in manifest.Objects)
            {
                Assert.True(o.ServerFlag == ObjectFlags.Flag(o.Id),
                            $"object {o.Id} ({o.Role}): the server reads flag 0x{ObjectFlags.Flag(o.Id):X2}, " +
                            $"the manifest says 0x{o.ServerFlag:X2} (0x{o.FileFlag:X2} in {manifest.ObjectTableFile}" +
                            (o.FileFlag != o.ServerFlag ? $", 0x{o.ServerFlag:X2} after ObjectFlagOverrides.csv)" : ")"));
                foreach (var cell in o.Cells)
                    Assert.True(MapData.For(cell.Map)!.Obj(cell.X, cell.Y) == o.Id,
                                $"object {o.Id} is not on {cell}: the map holds {MapData.For(cell.Map)!.Obj(cell.X, cell.Y)}");
            }

            // Then the override count the load line reports: an override row that changes nothing, or one for an
            // object the manifest does not list as overridden, shows here.
            var overridden = manifest.Objects.Where(o => o.FileFlag != o.ServerFlag).Select(o => o.Id).ToList();
            string reported = line.Groups[3].Value.Length == 0 ? "0" : line.Groups[3].Value;
            Assert.True(reported == overridden.Count.ToString(),
                        $"ObjectFlagOverrides.csv changed {reported} flag(s) in {manifest.ObjectTableFile}; the manifest " +
                        $"overrides {overridden.Count} (object(s) {string.Join(", ", overridden)}): {loaded}");
        });
    }

    /// <summary>The NPC row loads as the manifest describes it, stationary, and a fresh world places it on its
    /// tile under the manifest's runtime id.</summary>
    [Fact]
    public void The_NPC_stands_on_its_manifest_tile()
    {
        WithProfileLoaded(manifest =>
        {
            var want = Assert.Single(manifest.Npcs);
            var row = Assert.Single(Content.Npcs);
            Assert.Equal((want.NpcId, want.Identifier, want.Name, want.At.Map, want.At.X, want.At.Y, want.Look),
                         (row.Id, row.Key, row.Name, row.Map, row.X, row.Y, row.Look));
            Assert.True(row.Enabled);
            Assert.True(want.Stationary);
            Assert.False(row.MoveTime > 0 && row.ReturnDistance > 0, "the NPC would pace");
            Assert.False(MapData.For(want.At.Map)!.Solid(want.At.X, want.At.Y), $"the NPC's tile {want.At} is solid");

            var world = new World();
            var placed = world.MobById(want.At.Map, want.RuntimeId);
            Assert.NotNull(placed);
            Assert.True(placed.IsNpc);
            Assert.Equal(want.NpcId, placed.NpcDefId);
            Assert.Equal((want.At.X, want.At.Y), (placed.X, placed.Y));
            Assert.Same(placed, world.MobAt(want.At.Map, want.At.X, want.At.Y));
        });
    }

    /// <summary>The start tile is on a known map, its ground is walkable, it can be entered heading any way, and
    /// it is no warp and no NPC's tile in a fresh world. The game server never checks a new character's tile, so
    /// this is the only check it gets.</summary>
    [Fact]
    public void The_start_tile_is_walkable()
    {
        WithProfileLoaded(manifest =>
        {
            var start = manifest.Start;
            Assert.Contains(manifest.Maps, m => m.Id == start.Map && m.Width > start.X && m.Height > start.Y);
            AssertWalkable(start, "the start");
            Assert.False(Content.TryWarp(start.Map, start.X, start.Y, out _), $"the start {start} is a warp");
            Assert.Null(new World().MobAt(start.Map, start.X, start.Y));
        });
    }

    /// <summary>Every warp the manifest lists is one the server kept, and each round-trips: step into its source,
    /// land; step from that landing into the return source beside it, land beside the first source; step back
    /// into the first source and land on the first landing again. Each landing is walkable and not itself a
    /// warp, since arriving never fires one and the server checks no terrain where it puts the player.</summary>
    [Fact]
    public void Every_warp_round_trips()
    {
        WithProfileLoaded(manifest =>
        {
            Assert.Equal(manifest.Warps.Count, Content.Warps.Count);
            foreach (var w in manifest.Warps)
            {
                var first = Take(w.From);
                Assert.Equal(w.To, first);
                AssertLanding(first);

                var back = AdjacentSource(first);
                var second = Take(back);
                AssertLanding(second);

                Assert.True(IsOneStep(second, w.From),
                            $"from {second}, where the return {back} lands, the source {w.From} is not one step away");
                Assert.Equal(first, Take(w.From));
            }
        });
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private static SyntheticManifest.Tile Take(SyntheticManifest.Tile source)
    {
        Assert.True(Content.TryWarp(source.Map, source.X, source.Y, out var dest), $"{source} is not a warp on the server");
        return new SyntheticManifest.Tile(dest.m, dest.x, dest.y);
    }

    private static void AssertLanding(SyntheticManifest.Tile landing)
    {
        Assert.True(Content.Maps.ContainsKey(landing.Map), $"the landing {landing} is on an unknown map");
        AssertWalkable(landing, "the landing");
        Assert.False(Content.TryWarp(landing.Map, landing.X, landing.Y, out _), $"the landing {landing} is itself a warp");
    }

    /// <summary>The one warp source a step from <paramref name="tile"/>, on its map.</summary>
    private static SyntheticManifest.Tile AdjacentSource(SyntheticManifest.Tile tile)
    {
        var sources = Steps
            .Select(s => (X: tile.X + s.Dx, Y: tile.Y + s.Dy))
            .Where(p => p.X >= 0 && p.Y >= 0 && Content.TryWarp(tile.Map, (ushort)p.X, (ushort)p.Y, out _))
            .Select(p => new SyntheticManifest.Tile(tile.Map, (ushort)p.X, (ushort)p.Y))
            .ToList();
        Assert.True(sources.Count == 1, $"expected one warp source a step from {tile}, found {sources.Count}");
        return sources[0];
    }

    private static bool IsOneStep(SyntheticManifest.Tile a, SyntheticManifest.Tile b) =>
        a.Map == b.Map && Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) == 1;

    /// <summary>In bounds, walkable ground, and enterable heading every way (no object wall bit set).</summary>
    private static void AssertWalkable(SyntheticManifest.Tile tile, string what)
    {
        var terrain = MapData.For(tile.Map);
        Assert.True(terrain is not null, $"{what} {tile}: the map has no terrain");
        Assert.False(terrain.Solid(tile.X, tile.Y), $"{what} {tile}: the ground is solid");
        for (int dir = 0; dir < 4; dir++)
            Assert.False(terrain.BlockedMove(tile.X, tile.Y, dir),
                         $"{what} {tile}: a step heading {"NESW"[dir]} into it is blocked (object {terrain.Obj(tile.X, tile.Y)})");
    }

    /// <summary>Point this process at the profile with the manifest's environment, load it, run
    /// <paramref name="check"/>, and put game-data back whatever happens. The state directory stays this run's
    /// own temp one: the profile's maps have no persisted cells in it.</summary>
    private static void WithProfileLoaded(Action<SyntheticManifest> check)
    {
        var manifest = SyntheticManifest.Read();
        var environment = manifest.ResolvedEnvironment();
        var previous = environment.Select(v => (v.Name, Value: Environment.GetEnvironmentVariable(v.Name))).ToList();
        string gameData = RepoPaths.GameDataDir();

        lock (TestProcessState.Gate)
        {
            try
            {
                foreach (var (name, value) in environment) Environment.SetEnvironmentVariable(name, value);
                ServerConfig.ReloadForTests();
                MapData.Invalidate();
                TestProcessState.LoadContent();
                Assert.Equal(manifest.ProfileDir, RepoPaths.GameDataDir());

                check(manifest);
            }
            finally
            {
                foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
                ServerConfig.ReloadForTests();
                TestProcessState.LoadContent();
                MapData.Invalidate();
                ObjectFlags.Invalidate();
            }

            Assert.Equal(gameData, RepoPaths.GameDataDir());
            Assert.DoesNotContain(manifest.Start.Map, Content.Maps.Keys);
            Assert.True(Content.Maps.Count > 1000, $"game-data did not come back: {Content.Maps.Count} map(s) loaded");
        }
    }
}
