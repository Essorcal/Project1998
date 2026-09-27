namespace Shared;

/// <summary>
/// The shape a new character's name must have: its length and its letters. The login server's name gate
/// (<c>LoginSession.NameProblem</c>) runs this first and then checks the database for a taken name, which
/// stays there. It lives in Shared so the tests can hold the rule without a reference to the login server.
///
/// Letters only, because both the <c>characters</c> and <c>accounts</c> tables key on
/// <see cref="CharacterStore.Key"/> (lowercased, non-alphanumerics stripped): anything that normalization
/// folds away would let "Bo b" and "Bob" be one account under two display names. The ceiling is not ours
/// to choose. It is <see cref="HandoffTokens.MaxNameLength"/>, the most the client's handoff field carries
/// back whole; a longer name is created and then never enters the world (#299).
/// </summary>
public static class NameRules
{
    public const int MinNameLength = 3;

    /// <summary>Null if <paramref name="name"/> has a usable shape, else the player-facing reason. Does not
    /// look at the database: whether the name is taken is the caller's check.</summary>
    public static string? ShapeProblem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Please enter a name.";
        if (name.Length < MinNameLength || name.Length > HandoffTokens.MaxNameLength)
            return $"Names must be {MinNameLength} to {HandoffTokens.MaxNameLength} letters.";
        foreach (var ch in name)
            if (!char.IsAsciiLetter(ch)) return "Names may only use letters.";
        return null;
    }
}
