namespace Shared;

/// <summary>
/// The one reading of <c>map_index.csv</c>, the table of maps the client can render: each map's id, name and size.
/// Two processes read it. The game server builds its map registry from it (<c>Server.Content.Maps</c>), and the
/// login server, which loads no content, reads from it the size of the map <c>P1998_START_POINT</c> names
/// (<see cref="CharacterFactory.UseStartPoint(ServerConfig)"/>). A new character carries that size, and the game
/// server sends the saved size in its map-info packet, so the two must read the file the same way. Both go through
/// <see cref="Rows"/>, so they cannot drift apart.
/// </summary>
public static class MapIndexTable
{
    /// <summary>The file's name, under <c>P1998_GAME_DATA</c>.</summary>
    public const string File = "map_index.csv";

    /// <summary>One entry per map id, from the LAST row that has that id and whose <c>id</c>, <c>xs</c> and
    /// <c>ys</c> each parse as a whole number from 0 to 65535. Any other row is skipped and is not counted as kept
    /// in the load report, so a later row that does not parse never replaces an earlier one that does. This is
    /// the rule the game server's registry has always had; the row is handed back so the caller can read the
    /// map's other columns from it.</summary>
    public static IReadOnlyDictionary<ushort, (CsvRow Row, ushort Xs, ushort Ys)> Rows(CsvTable csv)
    {
        var rows = new Dictionary<ushort, (CsvRow Row, ushort Xs, ushort Ys)>();
        foreach (var row in csv)
        {
            if (ushort.TryParse(row.Require("id"), out var id)
                && ushort.TryParse(row.Require("xs"), out var xs)
                && ushort.TryParse(row.Require("ys"), out var ys))
            {
                rows[id] = (row, xs, ys);
                row.Keep();
            }
        }
        return rows;
    }
}
