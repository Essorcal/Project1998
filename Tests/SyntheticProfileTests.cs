using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Server;
using Shared;
using Tools;
using Xunit;

namespace Tests;

/// <summary>
/// The synthetic content profile (<c>test-content/synthetic/</c>, written by
/// <c>dotnet run --project Tools -- synthetic-profile</c>) is what a client test points a server at instead of
/// <c>game-data/</c>. Two of its promises would break silently:
/// <list type="bullet">
/// <item>the committed files are the generator's output. A hand edit, or a generator change nobody re-ran, would
/// leave a manifest that describes maps the server does not serve;</item>
/// <item>nothing in it comes from <c>game-data/</c>. The profile is public test content for a client that must
/// carry no retail world, so one copied row, name or map id defeats its purpose.</item>
/// </list>
/// Neither touches process-wide state: the generator is pure and both facts only read files.
/// </summary>
public class SyntheticProfileTests
{
    // Resolved once: RepoPaths.Root() walks the directory tree on every call, which cost 12 s when it ran per CSV
    // line below.
    private static readonly string ProfileDir = SyntheticProfile.DefaultDirectory();

    // The repository's own content, not RepoPaths.GameDataDir(): the comparison is with what game-data/ ships,
    // whatever P1998_GAME_DATA a run happens to carry.
    private static readonly string GameDataDir = Path.Combine(RepoPaths.Root(), "game-data");

    /// <summary>Regenerate into a temp folder; the committed folder must hold the same files (plus the
    /// hand-written README) with the same bytes.</summary>
    [Fact]
    public void The_committed_profile_is_the_generators_output_byte_for_byte()
    {
        string temp = Path.Combine(Path.GetTempPath(), $"p1998-synthetic-profile-{Guid.NewGuid():N}");
        try
        {
            var written = SyntheticProfile.Write(temp);
            var generated = Listing(temp);
            Assert.Equal(written.OrderBy(p => p, StringComparer.Ordinal), generated);
            Assert.Contains("manifest.json", generated);

            Assert.True(Directory.Exists(ProfileDir), $"{ProfileDir} is missing; run `{SyntheticProfile.Command}`");
            Assert.True(File.Exists(Path.Combine(ProfileDir, SyntheticProfile.HandWrittenFile)),
                        $"{SyntheticProfile.RelativeDirectory}/{SyntheticProfile.HandWrittenFile} is missing");
            var committed = Listing(ProfileDir).Where(p => p != SyntheticProfile.HandWrittenFile).ToList();
            Assert.Equal(generated, committed);

            var differ = new List<string>();
            foreach (string rel in generated)
            {
                byte[] want = File.ReadAllBytes(Path.Combine(temp, rel));
                byte[] have = File.ReadAllBytes(Path.Combine(ProfileDir, rel));
                if (!want.AsSpan().SequenceEqual(have)) differ.Add($"{rel}: {FirstDifference(want, have)}");
            }
            Assert.True(differ.Count == 0,
                        $"committed {SyntheticProfile.RelativeDirectory} files differ from the generator's; run " +
                        $"`{SyntheticProfile.Command}`:\n" + string.Join("\n", differ));
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>No profile file has the bytes of a game-data file; no profile data row is a game-data row; no
    /// map name or NPC identifier or description appears anywhere in game-data's text; no map id or NPC id is
    /// one game-data uses. File names and CSV header lines are schema the loader fixes, so they are not
    /// compared.</summary>
    [Fact]
    public void Nothing_in_the_profile_is_shared_with_game_data()
    {
        var gameFiles = Directory.EnumerateFiles(GameDataDir, "*", SearchOption.AllDirectories).Order().ToList();
        var profileFiles = Directory.EnumerateFiles(ProfileDir, "*", SearchOption.AllDirectories).Order().ToList();
        Assert.True(gameFiles.Count > 1000, $"found only {gameFiles.Count} files under {GameDataDir}");
        Assert.True(profileFiles.Count >= 10, $"found only {profileFiles.Count} files under {ProfileDir}");
        var shared = new List<string>();

        // Bytes.
        var gameHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string f in gameFiles) gameHashes.TryAdd(Sha256(f), Rel(GameDataDir, f));
        foreach (string f in profileFiles)
            if (gameHashes.TryGetValue(Sha256(f), out var twin))
                shared.Add($"{Rel(ProfileDir, f)} has the bytes of game-data/{twin}");

        // Data rows. Every non-comment line of every game-data CSV goes in (its header too, which no data row can
        // equal); on the profile side the header line is left out.
        var gameRows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string f in gameFiles.Where(IsCsv))
            foreach (string line in File.ReadLines(f))
                if (!Csv.IsSkippable(line)) gameRows.TryAdd(NormalizeRow(line), Rel(GameDataDir, f));
        int profileRows = 0;
        foreach (string f in profileFiles.Where(IsCsv))
            foreach (string row in DataRows(f))
            {
                profileRows++;
                if (gameRows.TryGetValue(NormalizeRow(row), out var where))
                    shared.Add($"{Rel(ProfileDir, f)} row '{row}' is a row of game-data/{where}");
            }
        Assert.True(profileRows >= 7, $"read only {profileRows} profile data rows");

        // Content names, from the CSVs and the manifest, against every text file game-data has.
        var names = new SortedSet<string>(StringComparer.Ordinal);
        names.UnionWith(Column(ProfilePath("map_index.csv"), "name"));
        names.UnionWith(Column(ProfilePath("NPCs.csv"), "NpcIdentifier"));
        names.UnionWith(Column(ProfilePath("NPCs.csv"), "NpcDescription"));
        using (var manifest = JsonDocument.Parse(File.ReadAllBytes(ProfilePath("manifest.json"))))
        {
            foreach (var map in manifest.RootElement.GetProperty("maps").EnumerateArray())
                names.Add(map.GetProperty("name").GetString()!);
            foreach (var entity in manifest.RootElement.GetProperty("entities").EnumerateArray())
            {
                names.Add(entity.GetProperty("identifier").GetString()!);
                names.Add(entity.GetProperty("name").GetString()!);
            }
        }
        Assert.True(names.Count >= 4, $"collected only {names.Count} profile names");
        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n), "a profile name is blank"));
        var gameText = gameFiles.Where(IsText).Select(f => (File: Rel(GameDataDir, f), Text: File.ReadAllText(f))).ToList();
        foreach (string name in names)
            foreach (var (file, text) in gameText)
                if (text.Contains(name, StringComparison.OrdinalIgnoreCase))
                    shared.Add($"profile name '{name}' appears in game-data/{file}");

        // Map ids: game-data's three map lists against every map id the profile names.
        var gameMaps = new HashSet<int>();
        gameMaps.UnionWith(Ids(Column(GamePath("map_index.csv"), "id")));
        gameMaps.UnionWith(Ids(Column(GamePath("Maps.csv"), "MapId")));
        gameMaps.UnionWith(gameFiles.Select(MapFileId).OfType<int>());
        Assert.True(gameMaps.Count > 1000, $"read only {gameMaps.Count} game-data map ids");

        var profileMaps = new SortedSet<int>();
        profileMaps.UnionWith(Ids(Column(ProfilePath("map_index.csv"), "id")));
        profileMaps.UnionWith(Ids(Column(ProfilePath("Maps.csv"), "MapId")));
        profileMaps.UnionWith(Ids(Column(ProfilePath("Warps.csv"), "SourceMapId")));
        profileMaps.UnionWith(Ids(Column(ProfilePath("Warps.csv"), "DestinationMapId")));
        profileMaps.UnionWith(Ids(Column(ProfilePath("NPCs.csv"), "NpcMapId")));
        profileMaps.UnionWith(profileFiles.Select(MapFileId).OfType<int>());
        Assert.True(profileMaps.Count >= 2, $"read only {profileMaps.Count} profile map ids");
        foreach (int id in profileMaps.Where(gameMaps.Contains))
            shared.Add($"map id {id} is also a game-data map id");

        // NPC ids, so a profile NPC can never pick up a game-data row keyed on its id.
        var gameNpcs = Ids(Column(GamePath("NPCs.csv"), "NpcId")).ToHashSet();
        Assert.True(gameNpcs.Count > 100, $"read only {gameNpcs.Count} game-data NPC ids");
        foreach (int id in Ids(Column(ProfilePath("NPCs.csv"), "NpcId")).Where(gameNpcs.Contains))
            shared.Add($"NPC id {id} is also a game-data NPC id");

        Assert.True(shared.Count == 0, "the synthetic profile shares content with game-data:\n" + string.Join("\n", shared));
    }

    // ---- plumbing --------------------------------------------------------------------------------------------

    private static string ProfilePath(string file) => Path.Combine(ProfileDir, file);
    private static string GamePath(string file) => Path.Combine(GameDataDir, file);

    private static List<string> Listing(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Rel(dir, f))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    private static string Rel(string dir, string file) => Path.GetRelativePath(dir, file).Replace('\\', '/');

    private static string FirstDifference(byte[] want, byte[] have)
    {
        int n = Math.Min(want.Length, have.Length);
        for (int i = 0; i < n; i++)
            if (want[i] != have[i]) return $"first difference at byte {i} (generator 0x{want[i]:X2}, committed 0x{have[i]:X2})";
        return $"generator wrote {want.Length} bytes, committed has {have.Length}";
    }

    private static string Sha256(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    private static bool IsCsv(string file) => file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

    private static bool IsText(string file) =>
        new[] { ".csv", ".lua", ".json", ".md", ".txt" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);

    private static string NormalizeRow(string line) => string.Join(",", Csv.Split(line).Select(f => f.Trim()));

    /// <summary>A profile CSV's data rows: its non-comment lines after the header, or all of them for a table
    /// whose header the server supplies (<c>ObjectFlagOverrides.csv</c>).</summary>
    private static IEnumerable<string> DataRows(string file)
    {
        string name = Path.GetFileName(file);
        bool headerless = Content.TableSpecifications.Any(s => s.File == name && s.Header is not null);
        return File.ReadLines(file).Where(l => !Csv.IsSkippable(l)).Skip(headerless ? 0 : 1);
    }

    /// <summary>The named column's values from a CSV with a header line.</summary>
    private static List<string> Column(string file, string column)
    {
        var lines = File.ReadLines(file).Where(l => !Csv.IsSkippable(l)).ToList();
        Assert.NotEmpty(lines);
        int index = Csv.Split(lines[0]).FindIndex(h => string.Equals(h.Trim(), column, StringComparison.OrdinalIgnoreCase));
        Assert.True(index >= 0, $"{file} has no '{column}' column");
        return lines.Skip(1)
            .Select(Csv.Split)
            .Where(fields => index < fields.Count)
            .Select(fields => fields[index].Trim())
            .ToList();
    }

    private static IEnumerable<int> Ids(IEnumerable<string> values) =>
        values.Select(v => int.TryParse(v, out int id) ? id : (int?)null).OfType<int>();

    private static readonly Regex MapFileName = new(@"^TK(\d+)\.map$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static int? MapFileId(string file)
    {
        var m = MapFileName.Match(Path.GetFileName(file));
        return m.Success && int.TryParse(m.Groups[1].Value, out int id) ? id : null;
    }
}
