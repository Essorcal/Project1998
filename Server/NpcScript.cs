using MoonSharp.Interpreter;
using Shared;

namespace Server;

/// <summary>
/// Hosts data-driven NPC dialog scripts in Lua (<c>data/game-data/npc_dialog.lua</c>) — the async cousin of the
/// spell/item verb system. Where a spell verb is a one-shot function, an NPC conversation SUSPENDS on every
/// prompt (menu / say / input) waiting for the player's 0x3A reply, so a script can't be a plain call. Instead
/// each migrated NPC is a Lua <b>coroutine</b>: the script calls <c>ctx:say(...)</c> / <c>ctx:menu(...)</c>,
/// which <c>coroutine.yield</c> a request table; this C# driver awaits the matching <see cref="NpcContext"/>
/// dialog Task (the SAME primitive the C# abilities use) and <c>Resume</c>s the coroutine with the reply. So a
/// script reads as linear code — <c>local c = ctx:menu(...); if c == 1 then ... end</c> — exactly like the C#
/// abilities, but hot-reloadable.
///
/// The Lua file defines a global <c>npcs</c> table (<c>npcKey -&gt; function(ctx)</c>) plus a <c>__make_ctx</c>
/// factory (the ctx API — every method is a thin yield; see the file). A missing/broken file just disables the
/// Lua NPC path (<see cref="Has"/> returns false), so <see cref="Session.RunNpcAsync"/> falls through to the C#
/// abilities — a broken script can never take NPCs offline.
///
/// Concurrency: a MoonSharp <see cref="Script"/> isn't thread-safe and a conversation spans many awaits, so we
/// can't hold a lock across one. Instead each individual <c>Resume</c>/<c>Call</c> is locked (they're fast and
/// synchronous); the awaits happen outside the lock. Two players' conversations therefore interleave their
/// coroutine steps safely — never resumed simultaneously — which is how a single Lua VM is meant to be driven.
/// </summary>
public static class NpcScript
{
    private static readonly object _lock = new();
    private static Script? _script;
    private static Table? _npcs;       // npcs[key]      = function(ctx)         -- click dialog
    private static Table? _npcsSay;    // npcs_say[key]  = function(ctx, speech)  -- spoken trigger, returns consumed?

    /// <summary>(Re)load the NPC dialog script — ATOMICALLY, for the same reason as
    /// <see cref="LuaVerbHost.Load"/>: the new script only replaces the running one once it has compiled and
    /// produced both `npcs` and `__make_ctx`. A broken edit leaves the previous dialogs working and logs.
    /// <para>This matters MORE here than for the verb hosts, because Lua NPC dialog has no C# fallback at all —
    /// nulling first meant one typo in npc_dialog.lua struck every scripted NPC in the world mute until the
    /// next good reload. Returns true if the Lua NPC path is live afterwards.</para></summary>
    public static bool Load(string? path)
    {
        lock (_lock)
        {
            if (path is null || !File.Exists(path))
            {
                Log.Info($"!! npc_dialog.lua: no file at '{path ?? "(null)"}' — keeping {(_npcs is null ? "the Lua NPC path disabled" : "the previously-loaded dialogs")}");
                return _npcs is not null;
            }
            try
            {
                // Hard sandbox (no io/os) PLUS the coroutine module — the whole dialog model is Lua coroutines
                // (ctx:say/menu/input yield); Preset_HardSandbox omits `coroutine`, so add it back explicitly.
                var s = new Script(CoreModules.Preset_HardSandbox | CoreModules.Coroutine);
                s.DoString(File.ReadAllText(path), null, "npc_dialog");
                var n = s.Globals.Get("npcs");
                if (n.Type == DataType.Table && s.Globals.Get("__make_ctx").Type == DataType.Function)
                {
                    _script = s;
                    _npcs = n.Table;
                    var sy = s.Globals.Get("npcs_say");            // optional — speech-trigger handlers
                    _npcsSay = sy.Type == DataType.Table ? sy.Table : null;
                    return true;
                }
                Log.Info("!! npc_dialog.lua missing global `npcs` table or `__make_ctx` — reload REJECTED, keeping the previous dialogs");
                return _npcs is not null;
            }
            catch (Exception e)
            {
                Log.Info($"!! npc_dialog.lua load failed: {e.Message} — reload REJECTED, keeping the previous dialogs");
                return _npcs is not null;
            }
        }
    }

    /// <summary>Is there a Lua CLICK dialog script for this NPC identifier?</summary>
    public static bool Has(string npcKey)
    {
        lock (_lock)
        {
            return _npcs is not null && npcKey.Length > 0 && _npcs.Get(npcKey).Type == DataType.Function;
        }
    }

    /// <summary>Is there a Lua SPEECH-trigger script (npcs_say) for this NPC identifier?</summary>
    public static bool HasSay(string npcKey)
    {
        lock (_lock)
        {
            return _npcsSay is not null && npcKey.Length > 0 && _npcsSay.Get(npcKey).Type == DataType.Function;
        }
    }

    /// <summary>Drive one Lua NPC conversation to completion. Creates the coroutine, then loops: resume it
    /// (locked), await the yielded dialog op via <paramref name="ctx"/> (unlocked), resume with the reply. A Lua
    /// error aborts just this conversation (logged); it can't crash the session (the caller wraps this in
    /// try/catch too).</summary>
    public static Task RunAsync(NpcContext ctx, string npcKey)
    {
        DynValue coro, ctxTable;
        lock (_lock)
        {
            if (_script is null || _npcs is null || _npcs.Get(npcKey).Type != DataType.Function) return Task.CompletedTask;
            ctxTable = _script.Call(_script.Globals.Get("__make_ctx"));
            coro = _script.CreateCoroutine(_npcs.Get(npcKey));
        }
        return Drive(ctx, coro, new[] { ctxTable });
    }

    /// <summary>Run a Lua SPEECH handler <c>npcs_say[key](ctx, speech)</c> to completion. Returns true if the
    /// script CONSUMED the speech (explicit Lua <c>return true</c>) — the caller then stops dispatching; false
    /// (or no return) lets other NPCs / the C# say-handlers have a go.</summary>
    public static async Task<bool> RunSayAsync(NpcContext ctx, string npcKey, string speech)
    {
        DynValue coro, ctxTable;
        lock (_lock)
        {
            if (_script is null || _npcsSay is null || _npcsSay.Get(npcKey).Type != DataType.Function) return false;
            ctxTable = _script.Call(_script.Globals.Get("__make_ctx"));
            coro = _script.CreateCoroutine(_npcsSay.Get(npcKey));
        }
        var ret = await Drive(ctx, coro, new[] { ctxTable, DynValue.NewString(speech) });
        return ret.Type == DataType.Boolean && ret.Boolean;
    }

    // Drive a coroutine to completion: resume (locked), await the yielded dialog op (unlocked), repeat. Returns
    // the coroutine's final return DynValue. Shared by the click and speech paths.
    private static async Task<DynValue> Drive(NpcContext ctx, DynValue coro, DynValue[] firstArgs)
    {
        DynValue yielded;
        lock (_lock) { yielded = coro.Coroutine.Resume(firstArgs); }
        while (coro.Coroutine.State == CoroutineState.Suspended)
        {
            var reply = await Dispatch(ctx, yielded);
            lock (_lock) { yielded = coro.Coroutine.Resume(reply); }
        }
        return yielded;
    }

    // Map one yielded request table {op=..., ...} to the real NpcContext primitive. Suspending ops (say/menu/
    // input) await the dialog Task; everything else runs immediately and resumes with the result. Add a case
    // here (and its yield stub in __make_ctx) to expose a new primitive to scripts.
    private static async Task<DynValue> Dispatch(NpcContext ctx, DynValue req)
    {
        if (req.Type != DataType.Table) return DynValue.Nil;
        var t = req.Table;
        string op = Str(t, "op");
        switch (op)
        {
            // ---- suspending (await the player's reply) ----
            case "say":     await ctx.Say(Arr(t, "pages"));                       return DynValue.Nil;
            case "sayItem": await ctx.SayItem(Str(t, "item"), Arr(t, "pages"));   return DynValue.Nil;
            case "sayLook": await ctx.SayLook(Int(t, "look"), Int(t, "color"), Arr(t, "pages")); return DynValue.Nil;
            case "menu":    return DynValue.NewNumber(await ctx.Menu(Str(t, "prompt"), Arr(t, "options")));
            case "input":   { var s = await ctx.Input(Str(t, "prompt")); return s is null ? DynValue.Nil : DynValue.NewString(s); }

            // ---- immediate (no wait) ----
            case "bubble":     ctx.Bubble(Str(t, "text"));                       return DynValue.Nil;
            case "notify":     ctx.Notify(Str(t, "text"));                       return DynValue.Nil;
            case "giveItem":   return DynValue.NewBoolean(ctx.GiveItem(Str(t, "key"), Int(t, "n", 1)));
            case "takeItem":   return DynValue.NewBoolean(ctx.TakeItem(Str(t, "key"), Int(t, "n", 1)));
            case "hasItem":    return DynValue.NewBoolean(ctx.HasItem(Str(t, "key"), Int(t, "n", 1)));
            case "countItem":  return DynValue.NewNumber(ctx.CountItem(Str(t, "key")));
            case "itemName":   return DynValue.NewString(ctx.ItemName(Str(t, "key")));
            case "awardExp":   ctx.AwardExp((uint)Math.Max(0, Int(t, "n")));     return DynValue.Nil;
            case "awardGold":  ctx.AwardGold((uint)Math.Max(0, Int(t, "n")));    return DynValue.Nil;
            case "stage":      return DynValue.NewNumber(ctx.Stage(Str(t, "key")));
            case "setStage":   ctx.SetStage(Str(t, "key"), Int(t, "n"));         return DynValue.Nil;
            case "reg":        return DynValue.NewNumber(ctx.Reg(Str(t, "key")));
            case "setReg":     ctx.SetReg(Str(t, "key"), Int(t, "n"));           return DynValue.Nil;
            case "hasLegend":  return DynValue.NewBoolean(ctx.HasLegend(Str(t, "name")));
            case "addLegend":  ctx.AddLegend(Str(t, "text"), Str(t, "name"), (byte)Int(t, "icon"), (byte)Int(t, "color")); return DynValue.Nil;
            case "removeLegend": ctx.RemoveLegend(Str(t, "name"));               return DynValue.Nil;
            case "warp":       return DynValue.NewBoolean(ctx.Warp(Int(t, "map"), Int(t, "x"), Int(t, "y")));
            case "level":      return DynValue.NewNumber(ctx.Level);
            case "sex":        return DynValue.NewNumber(ctx.Sex);
            case "nation":     return DynValue.NewNumber(ctx.Nation);
            case "coins":      return DynValue.NewNumber(ctx.Coins);
            case "spendGold":  return DynValue.NewBoolean(ctx.SpendGold((uint)Math.Max(0, Int(t, "n"))));
            case "gameDate":   return DynValue.NewString(Character.GameDate);

            default:
                Log.Info($"!! npc_dialog: unknown op '{op}' — ignored");
                return DynValue.Nil;
        }
    }

    // ---- request-table readers ----
    private static string Str(Table t, string k) => t.Get(k).CastToString() ?? "";
    private static int Int(Table t, string k, int def = 0)
    { var v = t.Get(k); return v.Type == DataType.Number ? (int)v.Number : def; }

    private static string[] Arr(Table t, string k)
    {
        var a = t.Get(k);
        if (a.Type != DataType.Table) return Array.Empty<string>();
        int n = a.Table.Length;
        var r = new string[n];
        for (int i = 1; i <= n; i++) r[i - 1] = a.Table.Get(i).CastToString() ?? "";
        return r;
    }
}
