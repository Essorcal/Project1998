using Shared;

namespace Server;

/// <summary>
/// The Session half of the Approach / Summon binding (#313). <see cref="SpellContext"/> is the Lua-facing half;
/// these are the members it needs and cannot reach on its own, because what they wrap is private to
/// <see cref="Session"/>: the world's name lookup, the staff test, the cast's player target and, above all,
/// <see cref="EnterMap"/>, the move <c>@approach</c> and <c>@bring</c> already make.
///
/// <para><b>Nothing here is a new path.</b> Each member wraps one the server already runs, so a spell and the
/// GM command it mirrors cannot drift apart: the lookup is whisper's and the commands' (<c>FindPlayer</c>), the
/// target slot is the one <see cref="LuaResolvePcTarget"/> fills, and both moves are
/// <c>EnterMap(..., ArrivalPolicy.AdjacentFreeElseStack)</c> with the same arguments
/// <c>ApproachCmd</c> and <c>BringCmd</c> pass (Session.GmCommands.cs). The verbs in spell_verbs.lua own every
/// guard and every line the player reads; these do the engine work.</para>
///
/// <para><b>Locks.</b> A verb runs on the caster's packet thread, inside the caster's state monitor
/// (<c>Session.Handle</c> wraps <c>Dispatch</c> in <c>WithState</c>) and inside the Lua gate
/// (<c>SpellScript.Run</c> -&gt; <c>LuaVerbHost.Invoke</c>). Approach moves the CASTER: <c>EnterMap</c>'s own
/// <c>EnterState</c> is re-entrant there, as for Gateway and Return. Summon moves ANOTHER player:
/// <see cref="LuaSummonTarget"/> takes the target's monitor (row 2 of docs/common/Locking.md, a second session
/// monitor, which <c>EnterState</c> orders by <c>StateRank</c>), and <c>EnterMap</c> takes <c>World._lock</c>
/// (row 3) inside it and releases it before returning. That is <c>@bring</c>'s sequence, with the Lua gate
/// outermost, which is also where the poet Resurrect family already moves another player from a spell verb
/// (<see cref="LuaReviveTarget"/> -&gt; <see cref="ReviveAt"/> -&gt; <c>EnterMap</c>). No new lock and no new
/// cross-session write path.</para>
/// </summary>
public sealed partial class Session
{
    /// <summary>Resolve the online player whose name was TYPED into the spell's prompt as this cast's player
    /// target: the typed-answer twin of <see cref="LuaResolvePcTarget"/>, which resolves an aimed id or the
    /// faced peer. Any map, case-insensitive, a replaced session skipped: <c>FindPlayer</c>, the lookup whisper
    /// and the GM commands use. False on a blank name or nobody online by it, and silent then (the verb answers),
    /// with a log line, since "nothing happened" is the hardest report to diagnose.</summary>
    internal bool LuaResolveNamedPcTarget(SpellDef sp, string? name)
    {
        string typed = (name ?? "").Trim();
        _pcSpellTarget = typed.Length == 0 ? null : _world.Online.FindPlayer(typed);
        if (_pcSpellTarget is not null) return true;
        Log.Info($"      -x {sp.Name}: nobody online named '{typed}' — the verb answers");
        return false;
    }

    /// <summary>The player target this cast resolved, for <see cref="SpellContext"/>'s reads of their kingdom,
    /// map, level and base vita/mana. Bare field reads of another session, as <see cref="LuaTargetLevel"/> and
    /// the rest of the target getters already make.</summary>
    internal Session? LuaPcTarget => _pcSpellTarget;

    /// <summary>Is this session staff? The same test the command table's tier gate makes.</summary>
    internal bool LuaIsGm => IsGm;

    /// <summary>Approach's move: this caster to the first free cardinal tile beside the resolved target (N, E, S,
    /// W), else onto the target's own tile. <c>ApproachCmd</c>'s call, argument for argument: the target's map,
    /// its dimensions and tile read off their character directly, as the command reads them.</summary>
    internal bool LuaApproachTarget(SpellDef sp)
    {
        if (_pcSpellTarget is not { } target) return false;
        ushort map = target._char.Map, xs = target._char.MapXs, ys = target._char.MapYs;
        string mapName = Content.TryMap(map, out var md) ? md.Name : "Nexus";
        var (x, y) = EnterMap(map, xs, ys, target._char.X, target._char.Y, mapName,
                              ArrivalPolicy.AdjacentFreeElseStack);
        Log.Info($"      {sp.Name}(lua): '{_char.Name}' -> '{target._char.Name}' at map {map} ({x},{y})");
        return true;
    }

    /// <summary>Summon's move: the resolved target to the first free cardinal tile beside this caster, else onto
    /// the caster's own tile. <c>BringCmd</c>'s call, argument for argument, made under the target's own
    /// monitor, which <c>EnterMap</c> would take anyway (its own <c>EnterState</c> is then re-entrant).
    ///
    /// <para>Taken one statement early so a target who logged out after the name was looked up is left alone:
    /// their teardown sets <c>_leaving</c> under this same monitor before it takes them off their map, and moving
    /// them after it would put a departed session back on one. False then, with a log line. <c>@bring</c> has no
    /// such check; the narrower window inside <c>EnterMap</c> itself, while it ends an open trade, is shared
    /// with <c>@bring</c> and the Resurrect family and is not this method's to close.</para></summary>
    internal bool LuaSummonTarget(SpellDef sp)
    {
        if (_pcSpellTarget is not { } target) return false;
        ushort map = _char.Map, xs = _char.MapXs, ys = _char.MapYs;
        string mapName = Content.TryMap(map, out var md) ? md.Name : "Nexus";
        using var _ = target.EnterState();
        if (target._leaving)
        {
            Log.Info($"      {sp.Name}(lua): '{target._char.Name}' logged out before the move — nobody moved");
            return false;
        }
        var (x, y) = target.EnterMap(map, xs, ys, _char.X, _char.Y, mapName, ArrivalPolicy.AdjacentFreeElseStack);
        Log.Info($"      {sp.Name}(lua): '{target._char.Name}' -> '{_char.Name}' at map {map} ({x},{y})");
        return true;
    }
}
