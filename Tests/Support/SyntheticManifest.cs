using System.Text.Json;
using Shared;
using Tools;

namespace Tests.Support;

/// <summary>
/// The synthetic profile's <c>manifest.json</c> (<c>test-content/synthetic/</c>), read the way a client test reads
/// it: the contract <see cref="SyntheticProfileBootTests"/> and <see cref="SyntheticProfileLoadTests"/> hold the
/// server to. Only the fields they assert on are read, and every one of them is required, so a renamed or missing
/// field fails here by name rather than as a check that silently compares nothing.
///
/// <para>Touches no process-global state: it reads one file.</para>
/// </summary>
internal sealed class SyntheticManifest
{
    internal readonly record struct Tile(ushort Map, ushort X, ushort Y)
    {
        public override string ToString() => $"{Map} ({X},{Y})";
    }

    internal sealed record MapEntry(ushort Id, string Name, ushort Width, ushort Height, string File, bool Indoor);

    internal sealed record ObjectEntry(int Id, int FileFlag, int ServerFlag, string Role, IReadOnlyList<Tile> Cells);

    internal sealed record WarpEntry(Tile From, Tile To);

    internal sealed record NpcEntry(int NpcId, uint RuntimeId, string Identifier, string Name, Tile At, ushort Look,
                                    bool Stationary);

    /// <summary>The profile folder the manifest was read from.</summary>
    internal string ProfileDir { get; }
    internal Tile Start { get; }
    internal IReadOnlyList<MapEntry> Maps { get; }
    /// <summary>The <c>SObj.tbl</c> file name and its header count.</summary>
    internal string ObjectTableFile { get; }
    internal int ObjectCount { get; }
    internal IReadOnlyList<ObjectEntry> Objects { get; }
    internal IReadOnlyList<WarpEntry> Warps { get; }
    internal IReadOnlyList<NpcEntry> Npcs { get; }
    /// <summary>The environment the manifest says to set, as written (paths relative to the repository root;
    /// a null value means "the caller supplies one", which is <c>P1998_STATE</c>'s fresh directory).</summary>
    internal IReadOnlyList<(string Name, string? Value)> Environment { get; }

    private SyntheticManifest(string profileDir, JsonElement root)
    {
        ProfileDir = profileDir;
        Start = TileOf(root.GetProperty("start"), root.GetProperty("start").GetProperty("map").GetUInt16());
        Maps = root.GetProperty("maps").EnumerateArray().Select(m => new MapEntry(
            m.GetProperty("id").GetUInt16(), m.GetProperty("name").GetString()!,
            m.GetProperty("width").GetUInt16(), m.GetProperty("height").GetUInt16(),
            m.GetProperty("file").GetString()!, m.GetProperty("indoor").GetBoolean())).ToList();

        var table = root.GetProperty("objectTable");
        ObjectTableFile = table.GetProperty("file").GetString()!;
        ObjectCount = table.GetProperty("count").GetInt32();
        Objects = root.GetProperty("objects").EnumerateArray().Select(o => new ObjectEntry(
            o.GetProperty("id").GetInt32(), o.GetProperty("fileFlag").GetInt32(), o.GetProperty("serverFlag").GetInt32(),
            o.GetProperty("role").GetString()!,
            o.GetProperty("cells").EnumerateArray().Select(c => TileOf(c, c.GetProperty("map").GetUInt16())).ToList()))
            .ToList();

        Warps = root.GetProperty("warps").EnumerateArray().Select(w => new WarpEntry(
            TileOf(w.GetProperty("from"), w.GetProperty("from").GetProperty("map").GetUInt16()),
            TileOf(w.GetProperty("to"), w.GetProperty("to").GetProperty("map").GetUInt16()))).ToList();

        Npcs = root.GetProperty("entities").EnumerateArray()
            .Where(e => e.GetProperty("kind").GetString() == "npc")
            .Select(e => new NpcEntry(
                e.GetProperty("npcId").GetInt32(), e.GetProperty("runtimeId").GetUInt32(),
                e.GetProperty("identifier").GetString()!, e.GetProperty("name").GetString()!,
                TileOf(e, e.GetProperty("map").GetUInt16()), e.GetProperty("look").GetUInt16(),
                e.GetProperty("stationary").GetBoolean()))
            .ToList();

        Environment = root.GetProperty("environment").GetProperty("variables").EnumerateArray()
            .Select(v => (v.GetProperty("name").GetString()!,
                          v.GetProperty("value").ValueKind == JsonValueKind.Null ? null : v.GetProperty("value").GetString()))
            .ToList();
    }

    private static Tile TileOf(JsonElement e, ushort map) =>
        new(map, e.GetProperty("x").GetUInt16(), e.GetProperty("y").GetUInt16());

    /// <summary>Read <c>manifest.json</c> from the committed profile.</summary>
    internal static SyntheticManifest Read()
    {
        string dir = Path.GetFullPath(SyntheticProfile.DefaultDirectory());
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "manifest.json")));
        return new SyntheticManifest(dir, doc.RootElement);
    }

    /// <summary>The manifest's environment with every value that names a path under the profile made absolute
    /// against the repository root, since the server uses a path knob as given. Variables whose value the
    /// manifest leaves to the caller (null) are left out.</summary>
    internal IReadOnlyList<(string Name, string Value)> ResolvedEnvironment()
    {
        string root = RepoPaths.Root();
        var resolved = new List<(string, string)>();
        foreach (var (name, value) in Environment)
        {
            if (value is null) continue;
            bool isProfilePath = value == SyntheticProfile.RelativeDirectory
                                 || value.StartsWith(SyntheticProfile.RelativeDirectory + "/", StringComparison.Ordinal);
            resolved.Add((name, isProfilePath ? Path.GetFullPath(Path.Combine(root, value)) : value));
        }
        return resolved;
    }

    /// <summary>The value the manifest gives <paramref name="name"/>, resolved as above.</summary>
    internal string Value(string name) =>
        ResolvedEnvironment().Single(v => v.Name == name).Value;
}
