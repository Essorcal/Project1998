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

    // The archetype path passes its data via ctx (ctx.amount / ctx.mana), not a CSV row, so it runs against a
    // shared empty row (a stable object keeps LuaVerbHost's per-row Table cache warm).
    private static readonly IReadOnlyDictionary<string, string> _noRow = new Dictionary<string, string>();

    /// <summary>Run an archetype verb (arch_damage/arch_heal/…) whose numbers come from <paramref name="ctx"/>.
    /// Returns null if the verb isn't defined (caller falls back to the C# CastX handler); otherwise the verb's
    /// success bool (a verb that ran but returned no boolean counts as success; a Lua error counts as a failed
    /// cast — false — rather than null, so the caller does NOT re-run it via the fallback and double-apply).</summary>
    public static bool? RunArch(string verb, SpellContext ctx)
    {
        if (!_host.HasVerb(verb)) return null;
        var ret = _host.Invoke(verb, ctx, _noRow);
        if (ret is null) return false;                          // present but errored — treat as failed, no fallback
        return ret.Type != DataType.Boolean || ret.Boolean;     // non-boolean return = ran = success
    }

    /// <summary>Run a per-spell verb (bound via a SpellParams row). Tri-state, mirroring <see cref="RunArch"/>:
    /// null = no such verb OR a Lua error (the caller falls through to the C# dispatch); true = the cast succeeded
    /// (a non-boolean return counts as success, so a verb needn't bother returning true); false = the verb ran
    /// but DECLINED (no mana / blocked / no target — it already sent its own notice). A false result must NOT
    /// print the central "You cast X." or fall through to C#, so the caller returns it straight to HandleCast.</summary>
    public static bool? RunResult(string verb, SpellContext ctx, IReadOnlyDictionary<string, string> row)
    {
        if (!_host.HasVerb(verb)) return null;
        var ret = _host.Invoke(verb, ctx, row);
        if (ret is null) return null;                          // Lua error -> fall through to the C# path
        return ret.Type != DataType.Boolean || ret.Boolean;    // non-boolean return = ran = success
    }
}
