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
/// <c>EnterState</c> is re-entrant there, as for Gateway and Return. Before its map checks it reads where the target
/// stands once, inside the target's monitor (<see cref="LuaReadApproachPlace"/>, row 2, by <c>StateRank</c>); the
/// move goes to that place and takes no monitor of the target's. Summon moves ANOTHER player:
/// <see cref="LuaSummonTarget"/> takes the target's monitor (row 2 of docs/common/Locking.md, a second session
/// monitor, which <c>EnterState</c> orders by <c>StateRank</c>), and <c>EnterMap</c> takes <c>World._lock</c>
/// (row 3) inside it and releases it before returning. That is <c>@bring</c>'s sequence, with the Lua gate
/// outermost, which is also where the poet Resurrect family already moves another player from a spell verb
/// (<see cref="LuaReviveTarget"/> -&gt; <see cref="ReviveAt"/> -&gt; <c>EnterMap</c>). Before the move, Summon
/// ends the target's open exchange itself, with <c>EnterMap</c>'s own <c>EndTrade</c> call (which takes the
/// partner's monitor, a third row-2 monitor, by rank like every pair), and checks again after each end: see
/// <see cref="LuaSummonTarget"/> for why. No new lock and no new cross-session write path.</para>
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
        _approachPlace = null;   // a place read for an earlier target, or an earlier cast, is not this target's
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

    /// <summary>Where the resolved player target stood when <see cref="LuaReadApproachPlace"/> read it this cast: the
    /// place Approach checked and moves to. Cleared whenever a cast resolves a player target
    /// (<see cref="LuaResolveNamedPcTarget"/>, <see cref="LuaResolvePcTarget"/>), so a move never goes to a place read
    /// for another target or another cast. Written and read on the caster's own thread, inside its monitor.</summary>
    private (ushort Map, ushort Xs, ushort Ys, ushort X, ushort Y)? _approachPlace;

    /// <summary>Approach's one read of where the resolved player target stands (<c>ctx:readTargetPlace</c>): their
    /// map, its dimensions and their tile, read together inside their monitor by <see cref="PlaceForPeer"/>, and kept
    /// for this cast's move (<see cref="LuaApproachTarget"/>). Returns the map, which every check the verb makes after
    /// this is about; 0 with no target, and nothing kept.
    /// <para>Before, the verb checked the target's map from a bare read (<c>ctx.targetMap</c>) and the move read the
    /// place again, after the checks, the cost and the animation. A target who stepped through a door between the two
    /// took the caster with them onto a map no check had looked at, an indoor or PvP map included (#345, follow-up 2).
    /// Now the checks and the move are about the one place read here.</para>
    /// <para><b>Locks.</b> This is the acquisition the move used to make, moved up the same verb with the same
    /// holdings: the caster's packet thread, inside the Lua gate and the caster's monitor and never <c>World._lock</c>
    /// (<c>FindPlayer</c> releases it before it returns), takes the target's monitor by <c>StateRank</c> and releases
    /// it before returning. No new lock and no new order. When the target ranks below the caster, <c>EnterState</c>
    /// drops the caster's monitor while it waits, as it did at the move. The checks after this read see whatever
    /// landed on the caster in that gap. The mana check before it does not: <c>debitMana</c> then takes what is left,
    /// floored at 0, the exposure Summon already has (its move, which can drop the caster's monitor the same way, sits
    /// between its mana check and its debit).</para></summary>
    internal ushort LuaReadApproachPlace()
    {
        _approachPlace = _pcSpellTarget?.PlaceForPeer();
        return _approachPlace?.Map ?? 0;
    }

    /// <summary>Approach's move: this caster to the first free cardinal tile beside the place
    /// <see cref="LuaReadApproachPlace"/> read for the resolved target (N, E, S, W), else onto that tile.
    /// <c>ApproachCmd</c>'s call, argument for argument, with the place the checks were made on in place of a second
    /// read.
    /// <para><b>A target who has moved since the read is not followed.</b> The caster lands where the target stood
    /// when the spell looked, on the map every check passed: a walk leaves the caster a step or more away from them,
    /// a door leaves the caster on the map the target has just left. Nothing a player reads changes: the cast says
    /// "You cast Approach.", costs what it always does and tells the target nothing, as when the target stays put.
    /// The window is the verb's own lines between the read and this call, inside the Lua gate.</para>
    /// <para>False, and nobody moves, with no target, or no place read since the target was resolved. The shipped
    /// verb always reads first, so only a verb that skips <c>readTargetPlace</c> meets the second.</para></summary>
    internal bool LuaApproachTarget(SpellDef sp)
    {
        if (_pcSpellTarget is not { } target) return false;
        if (_approachPlace is not { } place)
        {
            Log.Info($"      -x {sp.Name}(lua): no place read for '{target._char.Name}' this cast — nobody moved");
            return false;
        }
        ApproachMoveProbeForTest?.Invoke(target);   // null except under test
        var (map, xs, ys, tx, ty) = place;
        string mapName = Content.TryMap(map, out var md) ? md.Name : "Nexus";
        // The caster's own move, on the caster's own read loop: EnterMap refuses only a player who is logging out
        // (#330), which the caster cannot be here, so the null arm is never taken.
        if (EnterMap(map, xs, ys, tx, ty, mapName, ArrivalPolicy.AdjacentFreeElseStack) is not (var x, var y))
            return false;
        Log.Info($"      {sp.Name}(lua): '{_char.Name}' -> '{target._char.Name}' at map {map} ({x},{y})");
        return true;
    }

    /// <summary>Where THIS player stands, for another player's move to them (<c>@approach</c>, and the Approach
    /// spell's one read, <see cref="LuaReadApproachPlace"/>): the map, its dimensions and the tile, read in one section
    /// inside this player's monitor (#29), taken by <c>StateRank</c>. The mover's thread holds the mover's monitor
    /// (and, for the spell, the Lua gate) and never <c>World._lock</c>; <c>FindPlayer</c> releases it before it
    /// returns.
    /// <para>A warp writes the five as one critical section on this player (<see cref="EnterMap"/>: the map and its
    /// dimensions first, then the tile through <c>World.PlacePlayer</c>), and a walk writes the tile under the same
    /// monitor. Read bare, a mover could take the new map with the old tile, and land on that tile clamped onto the
    /// new map, where this player never stood, or the new tile on the old map. Read here, the mover lands beside where
    /// this player was at one moment, or where they are now.</para></summary>
    internal (ushort Map, ushort Xs, ushort Ys, ushort X, ushort Y) PlaceForPeer()
    {
        using var _ = EnterState();   // #29: cross-thread entry into this session's state
        return (_char.Map, _char.MapXs, _char.MapYs, _char.X, _char.Y);
    }

    /// <summary>Summon's move: the resolved target to the first free cardinal tile beside this caster, else onto
    /// the caster's own tile. <c>BringCmd</c>'s call, argument for argument, made under the target's own
    /// monitor, which <c>EnterMap</c> would take anyway (its own <c>EnterState</c> is then re-entrant).
    ///
    /// <para><b>Nothing may drop that monitor between the last check and the move</b> (PR #325 review, F1). A
    /// target who logged out after the name was looked up must stay off every map: their teardown sets
    /// <c>_leaving</c> under this monitor before it takes them off their map, and a move after that puts a departed
    /// session back on one, found by name and still writing its row. Two things can drop the monitor here:</para>
    /// <list type="bullet">
    /// <item><b>This acquisition.</b> When the target ranks below the caster, <c>EnterState</c> drops the caster's
    ///   monitor while it waits; the target is not held yet, so the first check below sees anything that ran.</item>
    /// <item><b>Ending an open exchange.</b> <c>EnterMap</c> does that first (<c>Session.Navigation.cs</c>, the
    ///   <c>EndTrade</c> before <c>LeaveMap</c>), through <c>WithStatePair</c>, which takes the lower-ranked side
    ///   first: with a partner ranked below the target it drops this monitor, and the caster's, while it waits for
    ///   the partner, and a whole logout fits in that gap. So the exchange is ended HERE, by the same call, and
    ///   both checks run again after every end, until the target is neither leaving nor trading. Then
    ///   <c>EnterMap</c>'s own exchange step has nothing to end, and nothing else in it gives up this monitor
    ///   before the target is on the new map.</item>
    /// </list>
    /// <para>An exchange that keeps re-opening is ended at most <see cref="SummonExchangeEndsMax"/> times; the next
    /// one refuses the move. Every refusal returns false and moves nobody, and the verb answers "Fizzle." at no
    /// cost. The caster's map and tile are read only after the last check, under both monitors again, since the
    /// caster's was dropped too. Since #330 <c>EnterMap</c> refuses a player who is logging out itself, after its
    /// own exchange step, and returns null; that answer is read below as a refusal too. After these checks it is
    /// never null here: the target's monitor has been held since the last one, and there is no exchange left for
    /// <c>EnterMap</c> to end.</para></summary>
    internal bool LuaSummonTarget(SpellDef sp)
    {
        if (_pcSpellTarget is not { } target) return false;
        using var _ = target.EnterState();
        for (int ended = 0; ; ended++)
        {
            if (target._leaving)
            {
                Log.Info($"      {sp.Name}(lua): '{target._char.Name}' logged out before the move — nobody moved");
                return false;
            }
            if (target._trade is not { } open) break;
            if (ended == SummonExchangeEndsMax)
            {
                Log.Info($"      {sp.Name}(lua): '{target._char.Name}' had an exchange open again after {ended} " +
                         "were ended — nobody moved");
                return false;
            }
            EndTrade(open, "Exchange cancelled.");   // EnterMap's own call; may drop and retake this monitor
            SummonExchangeEndedProbeForTest?.Invoke(target, ended + 1);   // null except under test
        }

        ushort map = _char.Map, xs = _char.MapXs, ys = _char.MapYs;
        string mapName = Content.TryMap(map, out var md) ? md.Name : "Nexus";
        if (target.EnterMap(map, xs, ys, _char.X, _char.Y, mapName, ArrivalPolicy.AdjacentFreeElseStack)
            is not (var x, var y))
        {
            Log.Info($"      {sp.Name}(lua): '{target._char.Name}' logged out before the move — nobody moved");
            return false;
        }
        Log.Info($"      {sp.Name}(lua): '{target._char.Name}' -> '{_char.Name}' at map {map} ({x},{y})");
        return true;
    }

    /// <summary>How many of the target's exchanges one Summon ends before it gives up on the move. One is the
    /// ordinary case (the exchange that was open when the cast arrived); each further one is an exchange that
    /// opened while ending the last dropped the target's monitor.</summary>
    internal const int SummonExchangeEndsMax = 3;

    /// <summary>Test seam: called on the caster's thread with the target and the running count each time
    /// <see cref="LuaSummonTarget"/> has ended one of the target's exchanges, with both monitors held again. A fact
    /// re-opens an exchange here to stand in for one that opened while the end had dropped the target's monitor,
    /// which pins the bound at <see cref="SummonExchangeEndsMax"/>. Null outside the test host.</summary>
    internal static Action<Session, int>? SummonExchangeEndedProbeForTest;

    /// <summary>Test seam: called on the caster's thread with the target just before <see cref="LuaApproachTarget"/>
    /// moves the caster: after every check, the cost and the animation, with the Lua gate and the caster's monitor
    /// held. A fact warps the target here, standing in for one who stepped through a door after the spell read where
    /// they stood, which pins that the caster goes to the place read and not to a second read. Null outside the test
    /// host.</summary>
    internal static Action<Session>? ApproachMoveProbeForTest;
}
