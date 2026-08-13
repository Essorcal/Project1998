using Shared;

namespace Server;

/// <summary>
/// Date-based content gating: which features exist depends on WHEN the server is pretending it is.
///
/// NexusTK ran for years and the world kept changing under a stable client — quests were added, moved
/// between givers, and in a few cases taken away again. "Which client do we target" therefore does not
/// answer "what content should exist"; 4.95 shipped 2001-07-09, but a player logging in the week it
/// shipped saw a different tutorial than one logging in eleven months earlier. So the server carries a
/// TARGET DATE (<see cref="Today"/>) and every dated feature declares the window it existed in
/// (<c>game-data/EraFeatures.csv</c>).
///
/// <para><b>Sparse and fail-open, like <see cref="CraftingToggles"/>.</b> A feature with no row is always
/// on, and a missing/malformed file gates nothing. Only content we have actually DATED against a source
/// gets a row, so the file doubles as the list of things whose introduction we can prove. Gating something
/// therefore requires evidence, and the absence of evidence never silently removes content.</para>
///
/// <para><b>Retirement is exclusive.</b> <c>Retired=2000-10-06</c> means the feature is gone ON that day —
/// the natural reading when one thing replaces another, since the replacement's <c>Introduced</c> is the
/// same date and exactly one of the pair must be live. The three tutorial eras are built out of that pair:
/// <c>tutor_novice_chain</c> retires the day <c>newbie_tutorial_area</c> arrives.</para>
///
/// <para><b>Unset means off.</b> <c>EraDate=0</c> (or absent) leaves gating inert and every feature on, so
/// a deployment that doesn't care about historical fidelity never has to think about this file.</para>
///
/// See docs/Era-Gating.md for the timeline and the sources behind each date.
///
/// <para><b>This is a facade.</b> The evaluation and both file reads live in
/// <see cref="Shared.EraCalendar"/>, because the LOGIN server also needs the calendar — it is what decides
/// where a brand-new character is placed, and after 2000-10-06 that is the newbie area rather than the
/// tutor's home. Keeping one implementation in Shared is what stops the two processes disagreeing about
/// the date. This type adds the game-server-facing surface: the feature-key constants and the
/// <see cref="KnownFeatures"/> list <c>@era</c> reports.</para>
/// </summary>
public static class Era
{
    /// <summary>The date the server is pretending it is, or null when era gating is switched off
    /// (<c>EraDate</c> absent or 0 in ServerTuning.csv). Stored there as a yyyymmdd integer — a scalar, so
    /// it fits that file's shape, and sortable so a bad edit degrades to an obvious date rather than a
    /// subtly wrong one.</summary>
    public static DateOnly? Today => EraCalendar.Today;

    /// <summary>Does this feature exist at the target date? True when gating is off, and true for any
    /// feature with no row — see the class doc on why the default is "present".</summary>
    public static bool Has(string feature) => EraCalendar.Has(feature);

    /// <summary>The declared window for a feature, or null if it has no row. For the <c>@era</c> readout
    /// and for callers that want to explain WHY something is missing rather than just that it is.</summary>
    public static EraWindow? Window(string feature) => EraCalendar.Window(feature);

    // ---- feature keys -------------------------------------------------------------------------------
    // Re-exported from Shared.EraCalendar (which has to own them, since CharacterFactory names one) so
    // call sites here read as Era.X. Named constants rather than bare strings at the call sites so a typo
    // is a compile error — the fail-open default makes a misspelled key invisible at runtime.

    /// <summary>The separate newbie tutorial AREA (maps 4711-4718). Before this, the tutor taught its
    /// beats directly; see <see cref="TutorNoviceChain"/>, which retires the same day.</summary>
    public const string NewbieArea = EraCalendar.NewbieArea;

    /// <summary>The tutor-delivered first-steps beats (wooden saber, rabbits, squirrels, Soothe). These
    /// did not disappear from the game — they MOVED into the newbie area — so this retires on the day the
    /// area opens rather than being a second copy of the same content.</summary>
    public const string TutorNoviceChain = EraCalendar.TutorNoviceChain;

    /// <summary>Tutorial stage 11, the missing-brother/Haguru quest on Du Mountain. The MOUNTAIN and
    /// Haguru himself are older than this and are not gated — only the quest is. ("The old guy named
    /// Haguru that was stranded on the mountain in early 4.0 now warns us…", TSWolf 2001-03-18.)</summary>
    public const string DuMountainQuest = EraCalendar.DuMountainQuest;

    /// <summary>Tutorial stage 13, the student cap. Shipped the same day as the Du Mountain quest —
    /// "there are 2 new ones at the end" (TSWolf 2001-03-19).</summary>
    public const string StudentCapQuest = EraCalendar.StudentCapQuest;

    /// <summary>The Druid bouquet quest (2005-05-31), and with it <b>Yarlof</b> on Du Mountain, who exists to
    /// run its flower test and nothing else. Nearly four years past our target date, so he is not in the world
    /// by default — the gate is on his PLACEMENT (<c>NPCs.csv</c> <c>EraFeature</c>), not on a script, because
    /// there is no earlier version of him to leave standing. Haguru shares map 1321 and is untouched.
    ///
    /// <para>Declared here rather than in <see cref="EraCalendar"/>: only the game server places NPCs, and the
    /// login server has no reason to know this key. <see cref="EraCalendar"/> owns the other four because
    /// <see cref="CharacterFactory"/> names one of them.</para></summary>
    public const string DruidBouquetQuest = "druid_bouquet_quest";

    /// <summary>Every key this server actually gates on, for the <c>@era</c> readout. A row in the CSV
    /// that isn't listed here is still honoured by <see cref="Has"/> — it just isn't something our code
    /// asks about yet, which is the normal state for a date we've researched but not wired up.</summary>
    public static readonly IReadOnlyList<string> KnownFeatures =
        new[] { NewbieArea, TutorNoviceChain, DuMountainQuest, StudentCapQuest, DruidBouquetQuest };
}
