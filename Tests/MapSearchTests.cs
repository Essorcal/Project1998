using Server;
using Shared;
using Xunit;

namespace Tests;

/// <summary>
/// Where <see cref="MapData"/> looks for a <c>TK&lt;id&gt;.map</c>, and what <c>P1998_MAPS_INSTALL_FALLBACK</c>
/// changes about it. Each fact builds its configuration with <see cref="ServerConfig.From"/> and its folders in
/// a temp directory, so nothing here reads or changes the process environment or a real client install.
/// </summary>
public sealed class MapSearchTests : IDisposable
{
    // Upstream master 0627024's SearchDirs, transcribed: the two literals it yielded after game-data/maps.
    private const string NextAeonMaps = @"C:\Program Files (x86)\Nexon\NextAeon\Maps";
    private const string NextAeon5Maps = @"C:\Program Files (x86)\Nexon\NextAeon5\Maps";

    private const ushort MapId = 4242;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"p1998-map-search-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup of a test fixture */ }
    }

    private static ServerConfig With(params (string Name, string Value)[] set)
    {
        var map = set.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);
        return ServerConfig.From(name => map.GetValueOrDefault(name));
    }

    private string Folder(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string PutMap(string dir)
    {
        string path = Path.Combine(dir, $"TK{MapId}.map");
        File.WriteAllBytes(path, new byte[16]);
        return path;
    }

    /// <summary>A deployment that does not set the new knob searches exactly what master searched, in
    /// master's order: <c>P1998_MAPS</c> when set, <c>&lt;game-data&gt;/maps</c>, then the two client installs
    /// at their unchanged paths.</summary>
    [Fact]
    public void Unset_the_search_order_is_the_one_master_had()
    {
        const string gameData = @"D:\p1998\game-data";
        string gameDataMaps = Path.Combine(gameData, "maps");

        Assert.Equal(new[] { NextAeonMaps, NextAeon5Maps }, MapData.ClientInstallMapDirs);

        var blank = With(("P1998_GAME_DATA", gameData));
        Assert.True(blank.MapsInstallFallback);
        Assert.Equal(new[] { gameDataMaps, NextAeonMaps, NextAeon5Maps },
                     MapData.SearchDirs(blank, MapData.ClientInstallMapDirs));

        var mapsSet = With(("P1998_GAME_DATA", gameData), ("P1998_MAPS", @"E:\Maps"));
        Assert.Equal(new[] { @"E:\Maps", gameDataMaps, NextAeonMaps, NextAeon5Maps },
                     MapData.SearchDirs(mapsSet, MapData.ClientInstallMapDirs));

        // "1" is the default spelled out, not a different order.
        var on = With(("P1998_GAME_DATA", gameData), ("P1998_MAPS_INSTALL_FALLBACK", "1"));
        Assert.Equal(MapData.SearchDirs(blank, MapData.ClientInstallMapDirs),
                     MapData.SearchDirs(on, MapData.ClientInstallMapDirs));
    }

    /// <summary>The knob's whole effect: with it at 0, a map that only a client install holds is not located,
    /// where the default finds it there. <c>P1998_MAPS</c> and <c>&lt;game-data&gt;/maps</c> are still searched.
    /// <para>Falsification: drop the <c>MapsInstallFallback</c> check from <c>MapData.SearchDirs</c> and the
    /// "off" assertions fail, because the install copy is found.</para></summary>
    [Fact]
    public void Off_a_map_only_an_install_holds_is_not_located()
    {
        string gameData = Folder("game-data");
        Folder(Path.Combine("game-data", "maps"));
        string install = Folder("install");
        string installCopy = PutMap(install);
        string[] installs = { install };

        var stock = With(("P1998_GAME_DATA", gameData));
        var off = With(("P1998_GAME_DATA", gameData), ("P1998_MAPS_INSTALL_FALLBACK", "0"));

        Assert.Equal(installCopy, MapData.Locate(MapId, MapData.SearchDirs(stock, installs)));
        Assert.Null(MapData.Locate(MapId, MapData.SearchDirs(off, installs)));
        Assert.DoesNotContain(install, MapData.SearchDirs(off, installs));
    }

    /// <summary>Off narrows the search to the content and the override; it does not skip either of them.</summary>
    [Fact]
    public void Off_the_content_and_the_override_are_still_searched()
    {
        string gameData = Folder("game-data");
        string contentCopy = PutMap(Folder(Path.Combine("game-data", "maps")));
        string install = Folder("install");
        PutMap(install);
        string[] installs = { install };

        var off = With(("P1998_GAME_DATA", gameData), ("P1998_MAPS_INSTALL_FALLBACK", "0"));
        Assert.Equal(contentCopy, MapData.Locate(MapId, MapData.SearchDirs(off, installs)));

        string overrideDir = Folder("override");
        string overrideCopy = PutMap(overrideDir);
        var offWithOverride = With(("P1998_GAME_DATA", gameData), ("P1998_MAPS", overrideDir),
                                   ("P1998_MAPS_INSTALL_FALLBACK", "0"));
        Assert.Equal(overrideCopy, MapData.Locate(MapId, MapData.SearchDirs(offWithOverride, installs)));
    }
}
