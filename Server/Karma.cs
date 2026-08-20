namespace Server;

/// <summary>
/// Karma — the hidden virtue score, ported from RTK <c>Accepted/player.lua</c> (<c>Player.karmaLevel</c>,
/// <c>Player.karmaCheck</c>, <c>Player.addKarma</c>/<c>removeKarma</c>) and <c>Accepted/Tools/Tools.lua</c>
/// (<c>Tools.checkKarma</c>).
///
/// Two things use it. Quests PAY it (Chu Rua's one point, the dog linguist, the mythic alliance, totems…),
/// and NPCs GATE on it: RTK's four class trainers refuse to teach whole tiers of spells below "rabbit" /
/// "ox" / "tiger", and the Exp Seller's wind-armor branch wants "spirit". The gates are named, never
/// numeric, which is why <see cref="Meets"/> takes a tier name — that is the vocabulary every caller uses.
///
/// NOT SHOWN ON THE 4.95 PROFILE. The 0x39 self-profile grammar (Session.SendSelfProfile) has no karma
/// field — the client cannot render one — so the only feedback the player gets is the minitext + sparkle
/// that <see cref="Session.AddKarma"/> sends, exactly as in RTK. <see cref="LevelName"/> exists for NPC
/// dialog and GM inspection, not for the profile pane.
/// </summary>
public static class Karma
{
    /// <summary>Effect played on any karma change (RTK <c>player:sendAnimation(49)</c>).</summary>
    public const int Effect = 49;

    /// <summary>RTK's "scum" floor: at or below this, <see cref="Session.KarmaTooLow"/> refuses NPC talk.</summary>
    public const double ScumFloor = -3;

    // The named ladder, highest first. Thresholds are RTK's karmaCheck values, and the bands in its karmaLevel
    // are the same numbers read as ranges — the two agree everywhere except at exactly 0 (see LevelName).
    private static readonly (double Min, string Name)[] Ladder =
    {
        (30, "Angel"), (24, "Angel's tear"), (19, "Spirit"), (14, "Dragon"), (11, "Tiger"),
        (8,  "Bear"),  (6,  "Ox"),           (4,  "Monkey"), (3,  "Dog"),    (2,  "Rabbit"),
        (1,  "Squirrel"), (0, "Cat"),
    };

    /// <summary>The player's karma tier as a display name (RTK <c>Player.karmaLevel</c>).
    ///
    /// DIVERGENCE, deliberate: RTK's karmaLevel tests <c>karma == 0</c> first and returns "Rat" for it, then
    /// has a <c>karma >= 0 and karma &lt; 1</c> branch for "Cat" that exactly 0 can never reach. Its own
    /// karmaCheck disagrees — there "rat" needs <c>karma &lt; 0</c> and "cat" needs <c>karma >= 0</c>, so 0
    /// is a Cat. Shipping both readings would mean a fresh character (karma 0) that displays as "Rat" while
    /// every gate treats it as "Cat". karmaCheck is self-consistent and is the one the game actually branches
    /// on, so 0 is a Cat here and "Rat" covers the open band between the scum floor and zero.</summary>
    public static string LevelName(double karma)
    {
        if (karma <= ScumFloor) return "Snake";
        foreach (var (min, name) in Ladder)
            if (karma >= min) return name;
        return "Rat";   // ScumFloor < karma < 0
    }

    /// <summary>RTK <c>Player.karmaCheck</c>, verbatim in its semantics: does this karma satisfy the named
    /// tier? Callers use it as a MINIMUM ("not karmaCheck('tiger')" -> refuse), which is what every tier from
    /// "cat" up means. The bottom two invert and are kept that way: "snake" is true only at or below the scum
    /// floor and "rat" only below zero, so they read as "is this player that bad", not "at least this good".
    /// An unknown tier name is false, as in the Lua's fall-through.</summary>
    public static bool Meets(double karma, string tier)
    {
        tier = (tier ?? "").Trim().ToLowerInvariant();
        if (tier == "snake") return karma <= ScumFloor;
        if (tier == "rat")   return karma < 0;
        foreach (var (min, name) in Ladder)
            if (string.Equals(name, tier, StringComparison.OrdinalIgnoreCase)) return karma >= min;
        return false;
    }

    /// <summary>Every named band, best-first: the Ladder plus the two below-zero bands. For GM tooling and
    /// usage strings — the vocabulary <see cref="ValueForName"/> accepts.</summary>
    public static IReadOnlyList<string> TierNames { get; } =
        Ladder.Select(t => t.Name).Append("Rat").Append("Snake").ToArray();

    /// <summary>The setter half of <see cref="LevelName"/>: resolve a tier NAME to a karma value that lands
    /// squarely in that band, so setting "dog" and reading the level back agree. Case- and space-insensitive;
    /// null for anything that isn't a known tier (so a caller can fall through to parsing a raw number). The
    /// two below-zero bands are open-ended, so they get a representative interior value: "rat" halfway between
    /// the scum floor and zero, "snake" one step past the floor.</summary>
    public static double? ValueForName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return null;
        if (string.Equals(name, "snake", StringComparison.OrdinalIgnoreCase)) return ScumFloor - 1;
        if (string.Equals(name, "rat",   StringComparison.OrdinalIgnoreCase)) return ScumFloor / 2;
        foreach (var (min, tier) in Ladder)
            if (string.Equals(tier, name, StringComparison.OrdinalIgnoreCase)) return min;
        return null;
    }
}

public sealed partial class Session
{
    /// <summary>RTK <c>Player.addKarma</c>: raise karma, sparkle, and say which of the two lines applies.
    /// The &lt; 1 split is RTK's own — fractional awards read as "slightly".</summary>
    internal void AddKarma(double amount)
    {
        if (amount == 0) return;   // a no-op award must not claim the player's karma moved
        _char.Karma += amount;
        SendEffect(_char.Id, Karma.Effect);
        SendMiniText(amount < 1 ? "Your karma rises slightly." : "Your karma has risen.");
        Log.Info($"   -> KARMA +{amount} = {_char.Karma:0.###} ({Karma.LevelName(_char.Karma)})");
    }

    /// <summary>RTK <c>Player.removeKarma</c>. Not clamped, as in RTK: karma is allowed to go arbitrarily
    /// negative, and the scum floor is a threshold that behaviour reads, not a bound on the value.</summary>
    internal void RemoveKarma(double amount)
    {
        if (amount == 0) return;
        _char.Karma -= amount;
        SendEffect(_char.Id, Karma.Effect);
        SendMiniText(amount < 1 ? "Your karma decreases." : "Your karma has decreased.");
        Log.Info($"   -> KARMA -{amount} = {_char.Karma:0.###} ({Karma.LevelName(_char.Karma)})");
    }

    /// <summary>RTK <c>Tools.checkKarma</c>: is this player too vile to be spoken to? Returns true AND sends
    /// the brush-off, so a caller reads <c>if (KarmaTooLow()) return;</c>.
    ///
    /// RTK calls this at the top of most NPC handlers but implements it as a dialog whose "0" argument kills
    /// the whole Lua call stack — its own comment flags that as a hack to rework. Ours returns a plain bool
    /// instead, so the caller decides, which is why it has to be checked rather than merely called.</summary>
    internal bool KarmaTooLow()
    {
        if (_char.Karma > Karma.ScumFloor) return false;
        SendMiniText("Go away scum!");
        return true;
    }
}
