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
/// docs/common/Era-Gating.md. Both files are read defensively: anything unparseable is treated as absent, because
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

    internal sealed record State(int Date, IReadOnlyDictionary<string, EraWindow> Features);

    /// <summary>An opaque calendar built from disk but not yet visible to serving threads.</summary>
    internal sealed class PreparedReload
    {
        private readonly State _state;
        internal PreparedReload(State state) => _state = state;
        internal State Value => _state;
    }

    private static readonly object _gate = new();
    private static State? _published;

    // Content.Load must evaluate era-gated rows against the candidate calendar while it builds them, without
    // exposing that calendar to serving threads before the content snapshot commits. Only the loading thread
    // receives this view, and Content's finally always clears it after success or failure.
    [ThreadStatic]
    private static PreparedReload? _preparing;

    /// <summary>Force a re-read of both files. Direct callers still receive the old one-step behaviour;
    /// <c>Content.Load</c> uses <see cref="PrepareReload"/> and <see cref="CommitReload"/> so the calendar
    /// publishes with the rest of content.</summary>
    public static void Reload()
    {
        lock (_gate)
        {
            var prepared = PrepareReload();
            try { CommitReload(prepared); }
            finally { EndReload(prepared); }
        }
    }

    /// <summary>Read a candidate calendar and expose it only to this loading thread.</summary>
    internal static PreparedReload PrepareReload()
    {
        if (_preparing is not null)
            throw new InvalidOperationException("Programming error: EraCalendar reloads cannot be nested on the same thread.");
        lock (_gate)
        {
            return PrepareReload(ReadEraDate(), OpenFeatures());
        }
    }

    /// <summary>Build the game server's candidate calendar from its already-loaded tuning value and the
    /// feature table it opened for the content report. The login server keeps the parameterless lazy path.</summary>
    internal static PreparedReload PrepareReload(int eraDate, CsvTable features)
    {
        if (_preparing is not null)
            throw new InvalidOperationException("Programming error: EraCalendar reloads cannot be nested on the same thread.");
        lock (_gate)
        {
            return _preparing = new PreparedReload(new State(eraDate, ReadFeatures(features)));
        }
    }

    /// <summary>Publish a previously prepared calendar with one reference write.</summary>
    internal static void CommitReload(PreparedReload prepared) =>
        Volatile.Write(ref _published, prepared.Value);

    /// <summary>Remove the loading thread's candidate view after success or failure.</summary>
    internal static void EndReload(PreparedReload prepared)
    {
        if (ReferenceEquals(_preparing, prepared)) _preparing = null;
    }

    private static State Current
    {
        get
        {
            if (_preparing is { } preparing) return preparing.Value;
            var published = Volatile.Read(ref _published);
            if (published is not null) return published;
            lock (_gate)
            {
                return _published ??= new State(ReadEraDate(), ReadFeatures(OpenFeatures()));
            }
        }
    }

    /// <summary>The configured <c>EraDate</c> as the raw yyyymmdd integer (0 = gating off).</summary>
    public static int RawDate => Current.Date;

    /// <summary>How many dated features are declared — for the startup/reload line.</summary>
    public static int FeatureCount => Current.Features.Count;

    /// <summary>The date the server is pretending it is, or null when gating is off or the configured
    /// value isn't a real calendar date.</summary>
    public static DateOnly? Today
    {
        get => Date(Current.Date);
    }

    /// <summary>Does this feature exist at the target date? True when gating is off, and true for any
    /// feature with no row — the fail-open default.</summary>
    public static bool Has(string feature)
    {
        var state = Current;
        var now = Date(state.Date);
        if (now is null) return true;
        if (!state.Features.TryGetValue(feature, out var w)) return true;
        if (w.Introduced is { } intro && now.Value < intro) return false;
        if (w.Retired    is { } ret   && now.Value >= ret)   return false;
        return true;
    }

    /// <summary>The declared window for a feature, or null if it has no row.</summary>
    public static EraWindow? Window(string feature)
    {
        var features = Current.Features;
        return features.TryGetValue(feature, out var w) ? w : null;
    }

    private static DateOnly? Date(int v)
    {
        if (v <= 0) return null;
        int y = v / 10000, m = v / 100 % 100, d = v % 100;
        if (y < 1 || y > 9999 || m is < 1 or > 12) return null;
        if (d < 1 || d > DateTime.DaysInMonth(y, m)) return null;
        return new DateOnly(y, m, d);
    }

    // ---- reading --------------------------------------------------------------------------------------
    // The login server has no Content.Load, so its lazy path opens both files here. The game server passes
    // the value and feature table already opened by Content.Load, keeping one read of each file per load.

    private static int ReadEraDate()
    {
        foreach (var row in Csv.Open("ServerTuning.csv", RepoPaths.GameData("P1998_SERVER_TUNING", "ServerTuning.csv")))
            if (row.Require("key").Trim().Equals("EraDate", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(row.Require("value").Trim(), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out var d))
            {
                row.Keep();
                return (int)d;
            }
        return DefaultDate;
    }

    private static CsvTable OpenFeatures() =>
        Csv.Open("EraFeatures.csv", RepoPaths.GameData("P1998_ERA_FEATURES", "EraFeatures.csv"));

    private static Dictionary<string, EraWindow> ReadFeatures(CsvTable csv)
    {
        var feats = new Dictionary<string, EraWindow>(StringComparer.OrdinalIgnoreCase);
        static DateOnly? Date(string s) =>
            DateOnly.TryParse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

        foreach (var row in csv)
        {
            var key = row.Require("Feature", "").Trim();
            if (key.Length == 0) continue;
            feats[key] = new EraWindow(
                Date(row.Require("Introduced", "")),
                Date(row.Require("Retired", "")),
                row.Require("Source", "").Trim(),
                row.Require("Notes", "").Trim());
            row.Keep();
        }
        return feats;
    }
}
