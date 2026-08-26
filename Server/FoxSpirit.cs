namespace Server;

/// <summary>
/// The Fox spirits of the Worn path (RTK <c>NPCs/nagnang/fox_spirit.lua</c>, fired from the "Worn path" /
/// "Worn trail" block of <c>onScriptedTiles/onScriptedTilesQuest.lua</c>). The Border patrol warns you about
/// them on the way in — "look out for those tricky Fox spirits, they enjoy their little games" — and this is
/// the game: walk the two maps between Nagnang and the Blight pen and one in ten steps a fox pops up with a
/// riddle. Answer it and you keep a <c>fox_charm</c>; miss it and the fox throws you back to Nagnang.
///
/// <para>Unlike every other NPC here the fox is never PLACED. NPCs.csv carries it (id 86) parked on map 4,
/// the NPC Chamber — RTK's template holding map, not anywhere a player goes — because RTK conjures it with
/// <c>NPC("Fox spirit")</c> at the moment of the encounter and uses the row only for the portrait. We do the
/// same: <see cref="Look"/>/<see cref="Color"/> are that row's, and the dialog goes out through
/// <c>Session.DlgPush</c>/<c>DlgInputPush</c>, which speak without a mob in front of the player.</para>
///
/// <para>The charm is a one-per-player trophy — nothing else in the game consumes it — so a fox that finds
/// someone already carrying one just pays them the compliment and leaves.</para>
/// </summary>
public static class FoxSpirit
{
    /// <summary>Worn path and Worn trail — the two maps between the Border patrol and the pen.</summary>
    public static readonly ushort[] Maps = { 2542, 2543 };

    /// <summary>RTK <c>math.random(1, 10) == 1</c>: one step in ten, per map step.</summary>
    public const int OddsOneIn = 10;

    public const string Charm = "fox_charm";

    // NPCs.csv id 86 "Fox spirit" (look 34, colour 18) — the portrait only; the fox itself is never spawned.
    public const int Look = 34, Color = 18;

    /// <summary>Where a wrong answer lands you: back out in Nagnang, the far side of the border. RTK
    /// <c>player:warp(2500, 110, 141)</c>. Getting back in means another green squirrel pelt for the guard.</summary>
    public const ushort FailMap = 2500;
    public const int FailX = 110, FailY = 141;

    public const string Finds   = "A fox spirit finds you!";
    public const string Success = "Craftily done!";

    /// <summary>What a fox says to someone who already bested one. ("that talisman" is the charm — the fox's
    /// word for it, not the leviathan talisman.)</summary>
    public const string AlreadyCharmed =
        "Ah, I see you have met my kind and have bested us. Perhaps in the future we will be able to do " +
        "business... Do not lose that talisman, for I never remember a face.";

    /// <summary>The prompt line on the input box (RTK's <c>inputSeq(question, "", "My honorable trickster")</c>
    /// third argument).</summary>
    public const string InputTitle = "My honorable trickster";

    /// <summary>The four riddles, verbatim from RTK — and confirmed word for word, with these answers, by the
    /// archived quest guide the user supplied (2026-08-25). Answers are compared lower-cased and trimmed:
    /// RTK does <c>string.lower(givenAnswer)</c> too, so the guide's "spelling them correctly using all
    /// lower-case letters" is advice to the player rather than a rule the server enforces.</summary>
    public static readonly (string Question, string Answer)[] Riddles =
    {
        ("Within a room of green is a room of white, and within that, one of red. In there, thousands reside. What am I?", "watermelon"),
        ("I have fingers but no bone, a palm but no blood. What am I?", "glove"),
        ("In a chest without locks or hinges, a golden treasure awaits. What am I?", "egg"),
        ("Within ten, there are three of me. Within three, there are five. What am I?", "letters"),
    };

    public static bool IsFoxCountry(ushort map) => Maps.Contains(map);
}
