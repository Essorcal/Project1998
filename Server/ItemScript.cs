using MoonSharp.Interpreter;

namespace Server;

/// <summary>
/// The item-use half of the verb/row model: <c>data/game-data/item_verbs.lua</c> defines the effect verbs
/// (heal / drink / ward / hardenbody / warphome / …), <c>ItemParams.csv</c> supplies each consumable's row, and
/// <see cref="ItemContext"/> is the facade a verb acts through. A thin static wrapper over a shared
/// <see cref="LuaVerbHost"/>; both files hot-reload on <c>!reload</c> (see <see cref="Content.Load"/>). See
/// <see cref="Session.ApplyItemEffect"/> for the dispatch.
/// </summary>
public static class ItemScript
{
    private static readonly LuaVerbHost _host = new("item_verbs.lua");

    static ItemScript() => UserData.RegisterType<ItemContext>();

    public static bool Load(string? path) => _host.Load(path);

    public static bool HasVerb(string verb) => _host.HasVerb(verb);

    /// <summary>Run an item-effect verb. Returns:
    /// <list type="bullet">
    /// <item><c>true</c> — the verb ran and the item should be consumed;</item>
    /// <item><c>false</c> — the verb ran but a gate refused it (e.g. ward already active): do NOT consume;</item>
    /// <item><c>null</c> — no such verb or a Lua error: not handled, the caller uses its C# fallback.</item>
    /// </list>
    /// A verb that returns nothing (nil) counts as consumed — only an explicit Lua <c>return false</c> refuses.</summary>
    public static bool? Apply(string verb, ItemContext ctx, IReadOnlyDictionary<string, string> row)
    {
        var ret = _host.Invoke(verb, ctx, row);
        if (ret is null) return null;
        return ret.Type != DataType.Boolean || ret.Boolean;
    }
}
