namespace Server;

/// <summary>
/// Which crafting skills are switched ON vs OFF — same flat-file + <c>!reload</c> pattern as every other
/// registry in <see cref="Content"/> (maps/mobs/items/etc.), NOT a live SQLite-backed toggle: era config is
/// meant to be edited in <c>data/game-data/CraftingToggles.csv</c> and picked up on the next <c>!reload</c>,
/// no restart required.
///
///   1. <see cref="DefaultDisabled"/> — a code-level default-OFF set for skills that are real but
///      out-of-era for the 4.95 client this project targets: Jewelry (news-dated introduction
///      2004-12-09, lived through 5.x for ~10 months before the 6.5 client shipped 2005-10-11 — an
///      ambiguous era fit, neither clearly in nor out) and Food Preparation/Chef (dated 2001-01-13/
///      2001-01-31 — technically predates 4.95 by ~5 months, so arguably in-era, but still off by
///      default so a GM opts in deliberately). See docs/Crafting-Values.md for the full era research.
///   2. <see cref="Content.CraftingToggleOverrides"/> — rows from the CSV file. Only skills actually
///      listed there override the code default; anything absent falls through to (1). An override
///      always wins, so the file can enable a default-off skill or disable a default-on one.
///
/// Farming has no entry here at all — confirmed 2007-11-07 (6.x era), out of scope entirely rather than
/// toggle-gated.
/// </summary>
public static class CraftingToggles
{
    public static readonly IReadOnlyList<string> AllSkills = new[]
    {
        "woodcutting", "mining", "fishing",
        "weaving", "smelting", "gemcutting",
        "tailoring", "carpentry", "smithing", "jewelry",
        "food_preparation", "chef",
        "scribing", "potion_making",
    };

    public static readonly IReadOnlySet<string> DefaultDisabled =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "jewelry", "food_preparation", "chef" };

    /// <summary>Whether a crafting skill is usable. File override wins; else on unless default-off.</summary>
    public static bool IsEnabled(string skill) =>
        Content.CraftingToggleOverrides.TryGetValue(skill, out var forced) ? forced : !DefaultDisabled.Contains(skill);
}
