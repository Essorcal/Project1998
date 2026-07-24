using System.Text.Json;

namespace Shared;

/// <summary>
/// File-backed character persistence: one JSON file per account, keyed by (lowercased) username.
/// Replaces the hardcoded spawn so the name/appearance chosen at creation and the last position/stats
/// survive a logout.
///
/// Why file-backed and not in-memory: the login channel (creation, port 2000) and the game channel
/// (world entry, port 2005) are SEPARATE TCP connections — different Session objects — so the record
/// written at creation must round-trip through disk before world entry can read it.
/// </summary>
public sealed class CharacterStore
{
    private readonly string _dir;

    // Character exposes public FIELDS (not properties); System.Text.Json ignores fields unless told.
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        IncludeFields = true,
    };

    public CharacterStore(string dir)
    {
        _dir = dir;
        System.IO.Directory.CreateDirectory(_dir);
    }

    /// <summary>Absolute path of the store directory (logged at startup so records are findable).</summary>
    public string Directory => _dir;

    private string PathFor(string name) => Path.Combine(_dir, Key(name) + ".json");

    // Normalize to a safe, case-insensitive filename so "Snuggle" and "snuggle" are one account.
    private static string Key(string name)
    {
        var s = new string((name ?? string.Empty).ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        return string.IsNullOrEmpty(s) ? "_" : s;
    }

    public bool Exists(string name) => File.Exists(PathFor(name));

    public Character? Load(string name)
    {
        var p = PathFor(name);
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<Character>(File.ReadAllText(p), Json); }
        catch { return null; }   // corrupt/legacy file -> treat as absent, caller falls back to a fresh char
    }

    public void Save(Character c)
    {
        try { File.WriteAllText(PathFor(c.Name), JsonSerializer.Serialize(c, Json)); }
        catch { /* best effort; persistence must never crash a session */ }
    }
}
