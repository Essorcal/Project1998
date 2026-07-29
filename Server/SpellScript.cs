using MoonSharp.Interpreter;

namespace Server;

/// <summary>
/// The spell half of the data-driven verb/row model: <c>data/game-data/spell_verbs.lua</c> defines the verbs,
/// <c>SpellParams.csv</c> supplies each spell's row, and <see cref="SpellContext"/> is the facade a verb acts
/// through. A thin static wrapper over a shared <see cref="LuaVerbHost"/> (the actual MoonSharp engine); both
/// the CSV and this script hot-reload on <c>!reload</c> (see <see cref="Content.Load"/>). See
/// <see cref="Session.ApplyCast"/> for the additive dispatch (a spell with no row / a Lua error falls through
/// to the C# <c>CastX</c> path unchanged, so a broken verb can never take spells offline).
/// </summary>
public static class SpellScript
{
    private static readonly LuaVerbHost _host = new("spell_verbs.lua");

    static SpellScript() => UserData.RegisterType<SpellContext>();

    public static void Load(string? path) => _host.Load(path);

    public static bool HasVerb(string verb) => _host.HasVerb(verb);

    /// <summary>Run the spell verb. Returns true if the verb actually ran (whatever its in-game result); false
    /// if there was no such verb or it raised a Lua error — the caller then falls back to the C# dispatch.</summary>
    public static bool Run(string verb, SpellContext ctx, IReadOnlyDictionary<string, string> row) =>
        _host.Invoke(verb, ctx, row) is not null;
}
