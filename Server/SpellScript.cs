using MoonSharp.Interpreter;

namespace Server;

/// <summary>
/// The spell half of the data-driven verb/row model: <c>data/game-data/spell_verbs.lua</c> defines the verbs,
/// <c>SpellParams.csv</c> supplies each spell's row, and <see cref="SpellContext"/> is the facade a verb acts
/// through. A thin static wrapper over a shared <see cref="LuaVerbHost"/> (the actual MoonSharp engine); both
/// the CSV and this script hot-reload on <c>!reload</c> (see <see cref="Content.Load"/>). See
/// <see cref="Session.ApplyCast"/> for the additive dispatch: a spell with no row and no verb falls through to
/// the C# <c>CastX</c> path unchanged. A verb that ERRORS does not fall through — see <see cref="Run"/>.
/// </summary>
public static class SpellScript
{
    private static readonly LuaVerbHost _host = new("spell_verbs.lua");

    static SpellScript() => UserData.RegisterType<SpellContext>();

    public static bool Load(string? path) => _host.Load(path);

    public static bool HasVerb(string verb) => _host.HasVerb(verb);

    /// <summary>The empty row an archetype-style call runs against: those verbs take their numbers off
    /// <c>ctx</c> rather than a CSV row. A single stable instance keeps LuaVerbHost's per-row Table cache warm
    /// (it is keyed by row identity).</summary>
    public static readonly IReadOnlyDictionary<string, string> NoRow = new Dictionary<string, string>();

    /// <summary>Run a spell verb. ONE entry point for both bindings — a verb reached by its SpellParams row
    /// (<paramref name="row"/> carries the numbers) and one reached from a C# dispatch site (pass
    /// <see cref="NoRow"/>; the verb reads <c>ctx</c> instead). The verbs themselves already straddle both, e.g.
    /// <c>local cost = row.mana or ctx.spellMana</c>, so the split only ever lived in these wrappers.
    ///
    /// <para>Tri-state result:</para>
    /// <list type="bullet">
    /// <item><b>null</b> — no such verb. Lua isn't handling this spell; the caller falls through to its C#
    ///   dispatch. This is the ordinary case for the ~600 spells with no row.</item>
    /// <item><b>true</b> — the cast succeeded. (A verb that returns no boolean at all counts as success, so a
    ///   verb needn't bother with <c>return true</c>.)</item>
    /// <item><b>false</b> — the verb ran and DECLINED (no mana / no target / blocked) and has already sent its
    ///   own notice. The caller must return this straight up: no central "You cast X.", and NO fallthrough.</item>
    /// </list>
    ///
    /// <para><b>A Lua error counts as false, never null.</b> The two wrappers this replaces disagreed about
    /// exactly that — the archetype one returned false ("failed cast, don't re-run it") while the row one
    /// returned null, which quietly re-ran the spell through a C# handler whose behaviour has since drifted
    /// (filch barks over-head from Lua and into the status box from C#). Silently substituting a different
    /// implementation is the worst of the three options: the player sees the spell misbehave, the log says
    /// nothing about a fallback, and a verb that errored *after* spending mana gets its effect applied twice.
    /// A failed cast that says so is strictly better, and the error is already logged by
    /// <see cref="LuaVerbHost.Invoke"/>.</para></summary>
    public static bool? Run(string verb, SpellContext ctx, IReadOnlyDictionary<string, string>? row = null)
    {
        if (!_host.HasVerb(verb)) return null;                  // not a Lua spell -> C# dispatch
        var ret = _host.Invoke(verb, ctx, row ?? NoRow);
        if (ret is null) return false;                          // ran but errored (already logged) -> failed cast, no fallthrough
        return ret.Type != DataType.Boolean || ret.Boolean;     // non-boolean return = ran = success
    }
}
