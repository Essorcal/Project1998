using Shared;

namespace Server;

/// <summary>
/// Barrier and Human Barrier (Poet; their alignment twins Spirit Barrier, Life Barrier, Balance Barrier and Block
/// Entry, Distance Self, Protect Sides): the four tiles beside the Poet are closed, and what stands on them is held
/// until the barrier wears off (#334).
///
/// <para><b>What the sources say.</b> The Nexus Atlas Poet page (Wayback 2002-12-30, Sources.csv
/// <c>atlas-2002-12-30-spells-classes</c>), Barrier: "4 Way Invisible Blockade surrounds the caster, disabling any
/// animals from walking next to them. If an animal is in one of those spaces, they are paralyzed until the barrier
/// wears off." 300 mana, 0 aether, 22 s. Human Barrier: the same words with "players", 300 mana, 86 s aether, 22 s.
/// tswolf's Poet page (2001, <c>tswolf-2001-spells-classes</c>): Barrier is a "4 Way Invisible Blockade Against
/// Animals, disableing them to move on any of the four spaces beside you. If an animal is on one of those spaces
/// when the spell is casted, they will be stuck in that space. Duration is 22 Seconds."; Human Barrier "Similar to
/// Barrier. Lasts for 22 Seconds. Has 86 Second Aethers." RTK (poet/barrier.lua, blockade_human.lua, weight 0)
/// charges Barrier 0, gives Human Barrier a 56 s aether, holds only attackable players, and snares the caster;
/// the era pages win on all four (Caleb, 2026-10-08).</para>
///
/// <para><b>Caleb's reading (2026-10-08).</b> What is on the four tiles at the cast is held, paralysed until the
/// barrier wears off; for the 22 s nothing else may step onto them. The tiles stay where they were cast, so the
/// Poet may walk away and they stay closed. The caster is never held. Human Barrier holds any player, in towns
/// and off PvP maps too.</para>
///
/// <para><b>Data and engine.</b> The eight spell_effects.csv rows carry the cast: 300 mana, the 22 s run, Human
/// Barrier's 86 s aether, and the run's slot (<c>barriers</c> / <c>humanBarriers</c>), which <c>arch_buff</c>
/// fills as for any slot-only buff, so the buff box shows it and a recast is refused. arch_buff then raises the
/// barrier (game-data/spell_verbs.lua, <c>BARRIER_KIND</c>) through <see cref="LuaRaiseBarrier"/>. The barrier
/// itself is a <see cref="BarrierZone"/> on the map (<see cref="World.RaiseBarrier"/>): the creature AI's step
/// reads it through the tick's collision index, and a player's step through <see cref="World.TryMovePlayer"/>.</para>
///
/// <para><b>Locks.</b> No new lock. The cast runs inside the caster's monitor and the Lua gate. The zone is
/// written, and a creature held, inside one acquisition of <c>World._lock</c> (row 3 after row 2). A held player
/// is paralysed after that lock is released, inside the player's own monitor, taken by <c>StateRank</c> like
/// every peer write (<see cref="ReceiveParalysis"/>, as Doze's <see cref="ReceiveSleep"/>).</para>
/// </summary>
public sealed partial class Session
{
    /// <summary>The slot a player's paralysis takes. Holding it is the hold (<see cref="Paralyzed"/>): the four
    /// gates (walk, turn, attack, cast) read it, it lapses with its 22 s, and a relog restores it, for what is left
    /// of the 22 s, with the rest of <c>_buffs</c>. No cure spell frees a held player: the Cure Paralysis rows cure
    /// <c>paras</c>, but <c>arch_cure</c> cures only its own caster, and a held player cannot cast (PR #338 review,
    /// F3). Four things end it: the 22 s; death; <c>@dispel</c>; and another player's Dispell-family cast (Dispell,
    /// Remove Magic, Return Natural, Restore Balance) on a won roll (PR #338 re-check, F7), except on a GM, at whom
    /// it fizzles (Caleb, 2026-10-10). Its <c>cleanse</c> verb runs <c>FlushDurations</c>, which clears every
    /// entry in the player's buff list, this hold included, and the fury, stealth, Backstab, Flank and four-way
    /// timers. It does not clear the ward flags (Harden Body), the Sanctuary and Cunning damage reductions or the
    /// enchant, which <c>@dispel</c> and death do (PR #338 re-check 2, F10).</summary>
    internal const string ParalysisSlot = "paras";

    /// <summary>Is this player paralysed (a Human Barrier's hold)? Read under this session's own monitor by the
    /// walk, turn, attack and cast handlers. Like RTK's <c>sd->paralyzed</c> it stops walking, attacking and
    /// casting (clif.c:5043, :10190, :11425); it also stops turning, as the server's sleep hold already does.
    /// There is no staff exemption, as there is none for the sleep hold. RTK lets a GM walk while held
    /// (clif.c:5043, <c>!sd->status.gm_level</c>); this server's hold has never ported that.</summary>
    internal bool Paralyzed => HasStatusCategory(ParalysisSlot);

    /// <summary>Raise the barrier this cast's slot names, on the four tiles beside the caster, for
    /// <paramref name="durMs"/>: <paramref name="kind"/> "creatures" is Barrier, "players" is Human Barrier.
    /// Every player standing on a Human Barrier's tiles is then paralysed and told who did it, through the same
    /// <see cref="TellTarget"/> line every other spell cast on a player uses. Called from arch_buff, after the
    /// mana and the run's slot.</summary>
    internal void LuaRaiseBarrier(SpellDef sp, string kind, int durMs)
    {
        bool players = kind == "players";
        if (!players && kind != "creatures")
        {
            Log.Warn($"{sp.Key}: raiseBarrier kind '{kind}' is neither 'creatures' nor 'players'; nothing raised");
            return;
        }
        var standing = _world.RaiseBarrier(_char.Map, _char.X, _char.Y, durMs, players, _char.Id, sp.Key, out int creatures);
        int held = 0;
        foreach (var pc in standing)
        {
            if (!pc.ReceiveParalysis(durMs, $"{sp.Key}:held", sp.Name)) continue;
            TellTarget(pc, sp);
            held++;
        }
        Log.Info($"      (lua) {sp.Name} -> {(players ? "human " : "")}barrier at ({_char.X},{_char.Y}) map {_char.Map} " +
                 $"for {durMs}ms; held {(players ? $"{held} of {standing.Count} player(s)" : $"{creatures} creature(s)")}");
    }

    /// <summary>Paralyse THIS player for <paramref name="durMs"/> (a Human Barrier's hold). Cross-session: called on
    /// the held player's own Session, which takes its own monitor. Not refreshed and not stacked: a player already
    /// held keeps the hold they have, as RTK's <c>checkIfCast</c> leaves them (blockade_human.lua:26). Taking
    /// damage does not end it; it lasts "until the barrier wears off". <paramref name="key"/> is the caster's
    /// spell key with a suffix, so the entry cannot replace a run of the same spell that this player cast.</summary>
    internal bool ReceiveParalysis(int durMs, string key, string name)
    {
        using var _ = EnterState();   // #29: cross-thread entry into this session's state
        // Dead is checked again here, under the monitor where Hp is exact: RaiseBarrier skips the dead under
        // World._lock, but a player can die between that scan and this write (PR #338 re-check, F8).
        if (durMs <= 0 || IsDead || HasStatusCategory(ParalysisSlot)) return false;
        ReceiveCurse("", 0, durMs, key, name, ParalysisSlot);   // no stat: the slot IS the hold
        return true;
    }
}
