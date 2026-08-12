namespace Shared;

/// <summary>The window a dated feature existed in. Blank <see cref="Introduced"/> means "as far back as we
/// know"; blank <see cref="Retired"/> means "still present". See <see cref="EraCalendar"/>.</summary>
public sealed record EraWindow(DateOnly? Introduced, DateOnly? Retired, string Source, string Notes);

/// <summary>
/// The era calendar: what date the world is pretending it is, and which dated content that includes.
/// <b>Lives in Shared because BOTH processes need it.</b>
///
/// <para>The game server is the obvious consumer, but the LOGIN server decides where a brand-new character
/// is placed (<see cref="CharacterFactory.PlaceNewCharacter"/>), and after 2000-10-06 that is the newbie
/// area rather than the tutor's home. A login server that couldn't read the calendar would place every
/// character by the pre-2000 rule no matter what the game server believed — the two processes disagreeing
/// about the date in a way nothing would report. So the evaluation lives here, once, and
/// <c>Server.Era</c> is a thin facade over it.</para>
///
/// <para><b>Lazy by design.</b> The login server "deliberately does NOT load the game world/content, so it
/// starts instantly" (LoginServer/Program.cs) and this must not change that: the two small files below are
/// read on first use, not at startup, so the login server pays for them only if it actually creates a
/// character. The game server forces a re-read from <c>Content.Load</c> so <c>@reload</c> picks up edits.</para>
///
/// <para><b>Caveat — the login server caches for its process lifetime.</b> It has no <c>@reload</c>, so a
/// date change reaches it only on restart. That is the one way the two processes can disagree; restart the
/// login server after moving <c>EraDate</c>.</para>
///
/// Semantics (sparse, fail-open, exclusive retirement) are documented on <c>Server.Era</c> and in
/// docs/Era-Gating.md. Both files are read defensively: anything unparseable is treated as absent, because
/// the safe failure direction is "content is present" — never "content silently vanished".
/// </summary>
public static class EraCalendar
{
    // ---- feature keys ---------------------------------------------------------------------------------
    // Here rather than in Server.Era because CharacterFactory (Shared) has to name one of them. Server.Era
    // re-exports all four so existing call sites read the same as before.

    /// <summary>The separate newbie tutorial AREA (maps 4711-4718), opened ~2000-10-06.</summary>
    public const string NewbieArea = "newbie_tutorial_area";
    /// <summary>The tutor-delivered first-steps beats, which MOVED into the area on that same day.</summary>
    public const string TutorNoviceChain = "tutor_novice_chain";
    /// <summary>Tutorial stage 11, the Haguru/Du Mountain quest (2001-03-18).</summary>
    public const string DuMountainQuest = "tutor_du_mountain_quest";
    /// <summary>Tutorial stage 13, the student cap (2001-03-18).</summary>
    public const string StudentCapQuest = "tutor_student_cap_quest";

    /// <summary>Target date when <c>EraDate</c> is absent — the day client 4.95 shipped.</summary>
    public const int DefaultDate = 20010709;

    private static readonly object _gate = new();
    private static volatile bool _loaded;
    private static int _date;
    private static IReadOnlyDictionary<string, EraWindow> _features =
        new Dictionary<string, EraWindow>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Force a re-read of both files. Called by <c>Content.Load</c> so <c>@reload</c> works.</summary>
    public static void Reload() { lock (_gate) { LoadLocked(); } }

    private static void Ensure() { if (!_loaded) lock (_gate) { if (!_loaded) LoadLocked(); } }

    private static void LoadLocked()
    {
        _date = ReadEraDate();
        _features = ReadFeatures();
        _loaded = true;   // last: a reader must never see a half-built calendar
    }

    /// <summary>The configured <c>EraDate</c> as the raw yyyymmdd integer (0 = gating off).</summary>
    public static int RawDate { get { Ensure(); return _date; } }

    /// <summary>How many dated features are declared — for the startup/reload line.</summary>
    public static int FeatureCount { get { Ensure(); return _features.Count; } }

    /// <summary>The date the server is pretending it is, or null when gating is off or the configured
    /// value isn't a real calendar date.</summary>
    public static DateOnly? Today
    {
        get
        {
            Ensure();
            int v = _date;
            if (v <= 0) return null;
            int y = v / 10000, m = v / 100 % 100, d = v % 100;
            if (y < 1 || y > 9999 || m is < 1 or > 12) return null;
            if (d < 1 || d > DateTime.DaysInMonth(y, m)) return null;
            return new DateOnly(y, m, d);
        }
    }

    /// <summary>Does this feature exist at the target date? True when gating is off, and true for any
    /// feature with no row — the fail-open default.</summary>
    public static bool Has(string feature)
    {
        var now = Today;                       // Ensure()s
        if (now is null) return true;
        if (!_features.TryGetValue(feature, out var w)) return true;
        if (w.Introduced is { } intro && now.Value < intro) return false;
        if (w.Retired    is { } ret   && now.Value >= ret)   return false;
        return true;
    }

    /// <summary>The declared window for a feature, or null if it has no row.</summary>
    public static EraWindow? Window(string feature)
    {
        Ensure();
        return _features.TryGetValue(feature, out var w) ? w : null;
    }

    // ---- reading --------------------------------------------------------------------------------------
    // ServerTuning.csv is also parsed by Server.Content for its other scalars; re-reading it here for one
    // key is the price of Shared not depending on Server, and it is two files and a few hundred bytes.

    private static int ReadEraDate()
    {
        foreach (var row in ReadCsv(RepoPaths.GameData("P1998_SERVER_TUNING", "ServerTuning.csv")))
            if (row.TryGetValue("key", out var k) && k.Trim().Equals("EraDate", StringComparison.OrdinalIgnoreCase)
                && row.TryGetValue("value", out var v)
                && double.TryParse(v.Trim(), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out var d))
                return (int)d;
        return DefaultDate;
    }

    private static Dictionary<string, EraWindow> ReadFeatures()
    {
        var feats = new Dictionary<string, EraWindow>(StringComparer.OrdinalIgnoreCase);
        static DateOnly? Date(string s) =>
            DateOnly.TryParse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

        foreach (var row in ReadCsv(RepoPaths.GameData("P1998_ERA_FEATURES", "EraFeatures.csv")))
        {
            var key = row.GetValueOrDefault("Feature", "").Trim();
            if (key.Length == 0) continue;
            feats[key] = new EraWindow(
                Date(row.GetValueOrDefault("Introduced", "")),
                Date(row.GetValueOrDefault("Retired", "")),
                row.GetValueOrDefault("Source", "").Trim(),
                row.GetValueOrDefault("Notes", "").Trim());
        }
        return feats;
    }

    // Minimal CSV reader, matching Server.Content.ReadCsv's shape: '#' opens a comment line anywhere
    // including above the header, and quoted fields may contain commas.
    private static IEnumerable<Dictionary<string, string>> ReadCsv(string? path)
    {
        if (path is null || !File.Exists(path)) yield break;
        string[] lines;
        try { lines = File.ReadAllLines(path); } catch { yield break; }

        static bool Skip(string s) => string.IsNullOrWhiteSpace(s) || s.TrimStart().StartsWith('#');
        int h = 0;
        while (h < lines.Length && Skip(lines[h])) h++;
        if (h >= lines.Length - 1) yield break;

        var header = SplitCsv(lines[h]);
        for (int i = h + 1; i < lines.Length; i++)
        {
            if (Skip(lines[i])) continue;
            var vals = SplitCsv(lines[i]);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < header.Count && c < vals.Count; c++) row[header[c]] = vals[c];
            yield return row;
        }
    }

    private static List<string> SplitCsv(string line)
    {
        var outp = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '"') { if (q && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; } else q = !q; }
            else if (ch == ',' && !q) { outp.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(ch);
        }
        outp.Add(cur.ToString());
        return outp;
    }
}
