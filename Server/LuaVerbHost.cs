using System.Runtime.CompilerServices;
using MoonSharp.Interpreter;

namespace Server;

/// <summary>
/// A reusable embedded-MoonSharp (pure-C# Lua) "verb" host: it owns ONE <c>.lua</c> file that defines a global
/// <c>verbs</c> table (<c>verbName -&gt; function(ctx, row)</c>) and runs those verbs against a C# facade object
/// (<c>ctx</c>) plus a data row (the "row" half of the verb/row model). Both spells (<see cref="SpellScript"/>)
/// and item use-effects (<see cref="ItemScript"/>) are just instances of this over different verb files and
/// facade types — one engine, no duplicated MoonSharp plumbing.
///
/// Safety: the script runs under MoonSharp's hard sandbox (no io/os/file access), and every verb call is wrapped
/// so a Lua error is logged and reported as "did not run" (<see cref="Invoke"/> returns null) rather than
/// crashing the caller — the caller then falls back to its C# path. A MoonSharp <see cref="Script"/> is not
/// thread-safe, so calls are serialized under a lock (fine — casts/item-uses are low-frequency vs movement/IO).
///
/// The facade type(s) a host will pass as <c>ctx</c> must be registered with MoonSharp
/// (<c>UserData.RegisterType&lt;T&gt;()</c>) before the first <see cref="Invoke"/>; the static wrapper classes do
/// that in their static constructors.
/// </summary>
public sealed class LuaVerbHost
{
    private readonly object _lock = new();
    private readonly string _name;      // the verb file's name, for log messages
    private Script? _script;
    private Table? _verbs;
    // Cache the parsed Lua Table per row object, so a hot cast/use doesn't rebuild it + re-parse every CSV cell
    // each call (matters once hundreds of spells route through here). Keyed by the row's IDENTITY: Content
    // rebuilds every row object on !reload, so old entries fall out of this weak table naturally, and Load()
    // clears it anyway (the cached Tables belong to the OLD Script and can't be reused by the new one). Verbs
    // treat `row` as read-only, so sharing one Table across calls is safe.
    private readonly ConditionalWeakTable<object, Table> _rowCache = new();

    public LuaVerbHost(string name) => _name = name;

    /// <summary>(Re)load the verb script. A missing/broken file just disables this host — every caller then falls
    /// through to its C# path, so this can never take a system offline.</summary>
    public void Load(string? path)
    {
        lock (_lock)
        {
            _script = null;
            _verbs = null;
            _rowCache.Clear();   // cached Tables belong to the old Script — drop them on reload
            if (path is null || !File.Exists(path)) return;
            try
            {
                var s = new Script(CoreModules.Preset_HardSandbox);
                s.DoString(File.ReadAllText(path), null, _name);
                var v = s.Globals.Get("verbs");
                if (v.Type == DataType.Table) { _script = s; _verbs = v.Table; }
                else Log.Info($"!! {_name} defines no global `verbs` table — Lua path disabled");
            }
            catch (Exception e)
            {
                Log.Info($"!! {_name} load failed: {e.Message} — Lua path disabled");
            }
        }
    }

    /// <summary>Is <paramref name="verb"/> a function in the loaded verb table?</summary>
    public bool HasVerb(string verb)
    {
        lock (_lock)
        {
            return _verbs is not null && verb.Length > 0 && _verbs.Get(verb).Type == DataType.Function;
        }
    }

    /// <summary>Run <c>verb(ctx, row)</c> and return the verb's Lua return value, or <c>null</c> if there was no
    /// such verb or it raised a Lua error (in which case the caller should use its C# fallback). Empty CSV cells
    /// become nil, so a verb's <c>row.x or default</c> works; numeric-looking cells are passed as Lua numbers.</summary>
    public DynValue? Invoke(string verb, object ctx, IReadOnlyDictionary<string, string> row)
    {
        lock (_lock)
        {
            if (_script is null || _verbs is null) return null;
            var fn = _verbs.Get(verb);
            if (fn.Type != DataType.Function) return null;

            if (!_rowCache.TryGetValue(row, out var rowTable))
            {
                rowTable = new Table(_script);
                foreach (var (k, val) in row)
                {
                    if (string.IsNullOrEmpty(val)) continue;   // empty cell -> nil, so `row.x or default` works
                    rowTable.Set(k, double.TryParse(val, out var d) ? DynValue.NewNumber(d) : DynValue.NewString(val));
                }
                _rowCache.Add(row, rowTable);
            }

            try
            {
                return _script.Call(fn, UserData.Create(ctx), DynValue.NewTable(rowTable));
            }
            catch (Exception e)
            {
                Log.Info($"!! {_name} verb '{verb}' error: {e.Message}");
                return null;
            }
        }
    }
}
