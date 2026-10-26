using Server;
using Shared;
using Xunit;

namespace Tests;

/// <summary>
/// <c>P1998_START_POINT</c>, where a brand-new character starts (<see cref="CharacterFactory.StartFor(byte)"/>,
/// resolved at the login server's start-up by <see cref="CharacterFactory.UseStartPoint(ServerConfig)"/>). The
/// setting is for a content set that has no Welcome (4711) and no home cities, so every fact here is about one of
/// three promises: blank changes nothing, set wins over the era and the nation with the map's size taken from the
/// map table, and anything the login server cannot use stops it rather than falling back to the era's start.
///
/// <para>Collection <c>"tile-translation"</c>, which runs alone. Two seams are touched: the process's start
/// (<c>UseStartPoint</c>), which every character created in the process reads, and the era date
/// (<c>EraCalendar.PathOverrideForTests</c>). Each fact that changes either puts it back in a finally.</para>
/// </summary>
[Collection("tile-translation")]
public class StartPointTests
{
    private const string Knob = "P1998_START_POINT";

    /// <summary>Every nation byte a creation can carry; 2 (Buya) is the one with its own home.</summary>
    private static readonly byte[] Nations = { 0, 1, 2, 3 };

    private static ServerConfig Setting(string? raw) => ServerConfig.From(name => name == Knob ? raw : null);

    private static CharacterFactory.MapTable Table(params (ushort Id, ushort Xs, ushort Ys)[] maps) =>
        new(maps.ToDictionary(m => m.Id, m => (m.Xs, m.Ys)), "the facts' own table");

    /// <summary>A character created the way the login server's 0x04 handler creates one: decoded from the
    /// creation body, then placed (<c>LoginServer/LoginSession.cs</c>, <c>HandleCreate</c>).</summary>
    private static (ushort Map, ushort X, ushort Y, ushort Xs, ushort Ys) Created(byte nation)
    {
        var c = CharacterFactory.FromCreate("StartPointer", new byte[] { 0, 0, nation, 0, 0 });
        Assert.Equal(nation, c.Nation);
        CharacterFactory.PlaceNewCharacter(c);
        return (c.Map, c.X, c.Y, c.MapXs, c.MapYs);
    }

    // Point the calendar at a throwaway ServerTuning.csv holding just the date, as EraGatingTests does.
    private static void WithEraDate(int yyyymmdd, Action body)
    {
        lock (TestProcessState.Gate)
        {
            var dir = Path.Combine(Path.GetTempPath(), "nexus-start-era-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var tuning = Path.Combine(dir, "ServerTuning.csv");
            File.WriteAllText(tuning, "key,value\nEraDate," + yyyymmdd + "\n");

            var previous = EraCalendar.PathOverrideForTests;
            try
            {
                EraCalendar.PathOverrideForTests = file => file == "ServerTuning.csv" ? tuning : null;
                EraCalendar.Reload();
                body();
            }
            finally
            {
                EraCalendar.PathOverrideForTests = previous;
                EraCalendar.Reload();
                try { Directory.Delete(dir, true); } catch { /* temp dir; not worth failing a test over */ }
            }
        }
    }

    // Put the process's start back to the era's own, which is what every other class expects to find.
    private static void RestoreTheErasStart() => CharacterFactory.UseStartPoint(Setting(null), () => Table());

    /// <summary>Blank, the default, is today's behaviour: the login server's start-up accepts it without reading
    /// any map table, and a character created after it lands exactly where master placed one, in both eras and for
    /// every nation: Welcome (4711) at (3,5) on its 16x16 while the newbie area is in era, Ironheart's Home (36) at
    /// (5,10) or, for Buya, Jadespear's Home (351) at (3,6), both 12x12, before it.
    /// <para>Falsification: make <c>UseStartPoint</c> read the table before it looks at the setting and every row
    /// fails on the read count; move Welcome's tile in <c>StartFor</c> and the area-era half fails.</para></summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_setting_places_a_new_character_exactly_where_the_era_does(string? raw)
    {
        int reads = 0;
        try
        {
            Assert.Null(CharacterFactory.UseStartPoint(Setting(raw), () => { reads++; return Table(); }));
            Assert.Equal(0, reads);

            WithEraDate(EraCalendar.DefaultDate, () =>
            {
                foreach (byte nation in Nations)
                    Assert.Equal(((ushort)4711, (ushort)3, (ushort)5, (ushort)16, (ushort)16), Created(nation));
            });
            WithEraDate(20001005, () =>
            {
                foreach (byte nation in Nations)
                    Assert.Equal(nation == 2
                            ? ((ushort)351, (ushort)3, (ushort)6, (ushort)12, (ushort)12)
                            : ((ushort)36, (ushort)5, (ushort)10, (ushort)12, (ushort)12),
                        Created(nation));
            });
        }
        finally { RestoreTheErasStart(); }
    }

    /// <summary>Set, the start wins over the era and the nation for every new character, through the very call the
    /// login server makes at start-up (the real <c>map_index.csv</c>) and the very call it makes at creation. Open
    /// Field (4712) is 20x20 in that table, so (19,0) is its last column and first row, and the size the character
    /// carries is the table's: the setting has none. The revive point and the nation homes do not move.
    /// <para>Falsification: drop the write in <c>UseStartPoint</c>, or have <c>StartFor</c> skip the configured
    /// start, and every placement here is the era's again.</para></summary>
    [Fact]
    public void A_set_start_places_every_new_character_there_whatever_the_era_or_nation()
    {
        var table = CharacterFactory.ReadMapIndex(RepoPaths.GameData(CharacterFactory.MapIndexFile));
        Assert.Equal(((ushort)20, (ushort)20), table.Sizes[4712]);   // asserted, so a content edit fails here by name

        try
        {
            Assert.Null(CharacterFactory.UseStartPoint(Setting("4712,19,0")));
            foreach (int date in new[] { EraCalendar.DefaultDate, 20001005 })
                WithEraDate(date, () =>
                {
                    foreach (byte nation in Nations)
                        Assert.Equal(((ushort)4712, (ushort)19, (ushort)0, (ushort)20, (ushort)20), Created(nation));
                    Assert.Equal(((ushort)36, (ushort)5, (ushort)10), CharacterFactory.HomeCityFor(1));
                    Assert.Equal(((ushort)351, (ushort)3, (ushort)6), CharacterFactory.HomeCityFor(2));
                });
        }
        finally { RestoreTheErasStart(); }
    }

    /// <summary>The map's size is the table's, whatever map is named: a 64x48 map (a size no real start has) gives a
    /// start carrying 64x48, and its last tile, (63,47), is accepted. <c>StartFor</c> hands that start to every
    /// nation without asking the calendar.
    /// <para>Falsification: take the size from anywhere but the table row (Welcome's 16x16, say) and the start's
    /// size fails; move the bound in by one (<c>x &gt;= xs - 1</c>) and the last tile is refused.</para></summary>
    [Fact]
    public void The_start_carries_its_maps_size_from_the_map_table()
    {
        var (start, refusal) = CharacterFactory.ResolveStartPoint(Setting("7,63,47"), () => Table((7, 64, 48)));

        Assert.Null(refusal);
        Assert.Equal(new CharacterFactory.ConfiguredStart(7, 63, 47, 64, 48), start);
        foreach (byte nation in Nations)
            Assert.Equal(((ushort)7, (ushort)63, (ushort)47, (ushort)64, (ushort)48),
                         CharacterFactory.StartFor(nation, start));
    }

    /// <summary>A map the table does not list, and a tile off a map it does, each stop the login server with a line
    /// that names the setting, the value, the map, and what is wrong with it. One row of each runs against the real
    /// <c>map_index.csv</c> (map 60000 is not in it; Welcome is 16x16, so x 16 is one past its edge).
    /// <para>Falsification: remove the map check and the unknown-map rows lose their reason; remove the bound check
    /// and the tile rows are accepted.</para></summary>
    [Theory]
    [InlineData("8,0,0", false, "names map 8, which map_index.csv (the facts' own table) does not list")]
    [InlineData("7,64,0", false, "puts the tile (64,0) outside map 7, which is 64x48")]
    [InlineData("7,0,48", false, "puts the tile (0,48) outside map 7, which is 64x48")]
    [InlineData("7,65535,65535", false, "outside map 7")]
    [InlineData("60000,0,0", true, "names map 60000, which map_index.csv")]
    [InlineData("4711,16,5", true, "puts the tile (16,5) outside map 4711, which is 16x16")]
    public void An_unknown_map_or_a_tile_off_the_map_is_refused(string raw, bool realTable, string reason)
    {
        var (start, refusal) = realTable
            ? CharacterFactory.ResolveStartPoint(Setting(raw),
                () => CharacterFactory.ReadMapIndex(RepoPaths.GameData(CharacterFactory.MapIndexFile)))
            : CharacterFactory.ResolveStartPoint(Setting(raw), () => Table((7, 64, 48)));

        Assert.Null(start);
        Assert.NotNull(refusal);
        Assert.StartsWith($"{Knob}='{raw}' ", refusal, StringComparison.Ordinal);
        Assert.Contains(reason, refusal, StringComparison.Ordinal);
        Assert.EndsWith("refused; the login server will not start with it.", refusal, StringComparison.Ordinal);
    }

    /// <summary>A value that is not <c>map,x,y</c> stops the login server's start-up with the configuration's own
    /// refusal, before any map table is read, and leaves the process's start as it was: nothing is placed by a value
    /// that was refused.
    /// <para>Falsification: have <c>UseStartPoint</c> store a start for a refused value, or let a refused value through
    /// as if it were blank, and these rows fail.</para></summary>
    [Theory]
    [InlineData("4711")]
    [InlineData("4711,3,5,0")]
    [InlineData("4711,-3,5")]
    [InlineData("Welcome,3,5")]
    public void A_malformed_value_is_refused_before_any_map_is_read(string raw)
    {
        int reads = 0;
        try
        {
            Assert.Null(CharacterFactory.UseStartPoint(Setting("7,1,2"), () => Table((7, 64, 48))));

            string? refusal = CharacterFactory.UseStartPoint(Setting(raw),
                () => { reads++; return Table((7, 64, 48)); });

            Assert.Equal(Setting(raw).StartPointRefusal, refusal);
            Assert.StartsWith($"{Knob}='{raw}' is not map,x,y", refusal, StringComparison.Ordinal);
            Assert.Equal(0, reads);
            Assert.Equal(((ushort)7, (ushort)1, (ushort)2, (ushort)64, (ushort)48), CharacterFactory.StartFor(1));
        }
        finally { RestoreTheErasStart(); }
    }

    /// <summary>The login server loads no content, so it reads <c>map_index.csv</c> itself; the game server builds
    /// <c>Content.Maps</c> from the same file, and a character's saved size is what the game server sends it in the
    /// map-info packet. The two readings must agree on every map, or a start the login server accepted would be
    /// drawn at a size the game server does not use.
    /// <para>Falsification: swap xs and ys in <c>ReadMapIndex</c> and every non-square map fails (Walsuk Tavern,
    /// map 2, is 30x14).</para></summary>
    [Fact]
    public void The_login_servers_map_sizes_are_the_game_servers()
    {
        lock (TestProcessState.Gate)
        {
            TestProcessState.LoadContent();
            var table = CharacterFactory.ReadMapIndex(RepoPaths.GameData(CharacterFactory.MapIndexFile));

            Assert.True(Content.Maps.Count > 1000, $"only {Content.Maps.Count} maps loaded");
            Assert.Equal(Content.Maps.Keys.OrderBy(id => id), table.Sizes.Keys.OrderBy(id => id));
            foreach (var (id, map) in Content.Maps)
                Assert.True((map.Xs, map.Ys) == table.Sizes[id],
                    $"map {id}: the game server reads {map.Xs}x{map.Ys}, the login server " +
                    $"{table.Sizes[id].Xs}x{table.Sizes[id].Ys}");
        }
    }
}
