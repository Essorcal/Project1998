using MoonSharp.Interpreter;

namespace Server;

/// <summary>
/// The item-use half of the verb/row model: <c>game-data/item_verbs.lua</c> defines the effect verbs
/// (heal / drink / ward / hardenbody / warphome / …), <c>ItemParams.csv</c> supplies each consumable's row, and
/// <see cref="ItemContext"/> is the facade a verb acts through. A thin static wrapper over a shared
/// <see cref="LuaVerbHost"/>; both files hot-reload on <c>@reload</c> (see <see cref="Content.Load"/>). See
/// <see cref="Session.ApplyItemEffect"/> for the dispatch.
/// </summary>
public static class ItemScript
{
    private static readonly LuaVerbHost _host = new("item_verbs.lua");

    static ItemScript() => UserData.RegisterType<ItemContext>();

    public static bool Load(string? path) => _host.Load(path);

    public static bool HasVerb(string verb) => _host.HasVerb(verb);

    /// <summary>Run an item-effect verb. The four outcomes are <see cref="VerbResult"/>'s; how the use funnel
    /// acts on each — consume, refuse, fall back to the item DB, refuse-and-say-so — is
    /// <see cref="Session.ApplyItemEffect"/>'s business. This used to fold the result to <c>bool?</c>, and that
    /// fold is where a Lua error became "not handled" and the item got eaten anyway.</summary>
    public static VerbResult Apply(string verb, ItemContext ctx, IReadOnlyDictionary<string, string> row) =>
        _host.Invoke(verb, ctx, row);
}
