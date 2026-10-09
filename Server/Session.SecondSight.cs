namespace Server;

/// <summary>
/// Second Sight (Poet; its alignment twins Hear Spirits, Improve Sight and Show Hidden): while it runs, the
/// caster is told who is hiding nearby (#334).
///
/// <para><b>What the sources say.</b> tswolf's Poet page (Wayback 2001-03-09, Sources.csv
/// <c>tswolf-2001-spells-classes</c>): "Enables Text Notices of Invisible Rogues", 140 mana, "Shows Text Notices in
/// your Information Bar about the whereabouts of Invisible People." The Nexus Atlas Poet page (2002-12-30,
/// <c>atlas-2002-12-30-spells-classes</c>): "Reveals Invisible People", 140 mana, 0 aether, 325 s, and "Shows a text
/// notification that says "Name is Hidden in the Area"", which is the line sent here. RTK
/// (<c>rtklua/Accepted/Spells/poet/second_sight.lua</c>, weight 0) charges 240 and gives the two numbers the pages do
/// not. Its <c>while_cast</c> (lines 27-41) speaks when <c>player.timerTick % 15 == 0</c>: <c>bl_duratimer</c> runs it
/// once a second and <c>pc_scripttimer</c> advances the counter every 500 ms (rtk/src/map/pc.c:192, :197, :2955), so
/// once in every 15 s. It looks at the players on the same map within 9 tiles on both axes
/// (<c>distanceSquare</c>, Scripts/scripts.lua:378) who are invisible (<c>state == 2</c>, <c>PC_INVIS</c>, which
/// rogue/invisible.lua sets). Caleb chose the era pages' numbers on 2026-10-08.</para>
///
/// <para><b>Data and engine.</b> The four spell_effects.csv rows carry the cast: 140 mana, a 325 s run and the
/// <see cref="SecondSightSlot"/> slot, which <c>arch_buff</c> fills as it fills any slot-only buff. That entry is
/// the run: the buff box shows it, a recast meets "You already cast that spell.", a relog restores it, and death or
/// a dispel clears it. This file gives the slot its meaning, because a periodic notice needs a beat and a verb has
/// none: <see cref="RegenTick"/>, the per-player beat, asks <see cref="SecondSightScanDue"/> before it takes the
/// monitor and runs <see cref="SecondSightScan"/> inside it.</para>
///
/// <para><b>Locks.</b> No new lock and no cross-session write. The pre-check reads its fields without the monitor,
/// as <c>RegenTick</c>'s others do: <see cref="_secondSightUntil"/>, an exact hint the four <c>_buffs</c> writers keep
/// beside <c>_nextBuffExpiry</c>; <see cref="_secondSightRuns"/>, the run count <c>BuffAdd</c> keeps with an
/// interlocked add; and <see cref="_secondSightNextScan"/> and <see cref="_secondSightRunSeen"/>, which only the
/// tick thread reads and writes.
/// The scan takes <c>World._lock</c> inside this session's monitor (rows 2 then 3 of docs/common/Locking.md, the
/// order every cast that touches the world already takes) and reads each player's tile, invisibility and name
/// there, the unsynchronised scalar reads the tick makes under that lock (<see cref="World.HiddenPlayersNear"/>).
/// What it sends is this session's own mini-text.</para>
/// </summary>
public sealed partial class Session
{
    /// <summary>The exclusivity slot the four Second Sight rows carry in spell_effects.csv's <c>cureCat</c>. Holding
    /// it is what makes <see cref="SecondSightScan"/> run.</summary>
    internal const string SecondSightSlot = "secondSights";

    /// <summary>How often a running Second Sight tells the caster (RTK, the only source with a number).</summary>
    internal const int SecondSightEveryMs = 15_000;

    /// <summary>How far, in tiles on both axes, Second Sight looks (RTK's <c>distanceSquare(player, pc, 9)</c>).</summary>
    internal const int SecondSightRange = 9;

    /// <summary>The deadline of the running Second Sight entry in <c>_buffs</c>, 0 when there is none. Kept exact by
    /// <c>RecomputeNextBuffExpiry</c>, under the monitor every <c>_buffs</c> writer holds, and published with a
    /// volatile write for <see cref="SecondSightScanDue"/>, which reads it without the monitor.</summary>
    private long _secondSightUntil;

    /// <summary>The earliest tick the next scan may run. Tick-thread-owned like <c>_mailAccum</c>: only
    /// <see cref="RegenTick"/>'s pre-check and <see cref="SecondSightScan"/> touch it, both on the tick thread, so
    /// it is not shared. 0 at the start of every run (<see cref="_secondSightRuns"/>), so a fresh run is told on its
    /// first beat.</summary>
    private long _secondSightNextScan;

    /// <summary>How many Second Sight runs this session has started: <c>BuffAdd</c> counts every entry it adds in
    /// <see cref="SecondSightSlot"/> (a cast, a recast, a relog's restore), under the monitor, with an interlocked
    /// add. The pre-check compares it with <see cref="_secondSightRunSeen"/>, and a new run starts its clock again
    /// from 0. Before this, a run cast within 15 s of the last one's final notice waited out that notice's clock
    /// (PR #338 review, F5). A count and not the run's deadline, because two casts inside one tick of the
    /// system clock (15.6 ms on Windows) have the same deadline.</summary>
    private long _secondSightRuns;

    /// <summary>The run count the scan clock belongs to. Tick-thread-owned, like <see cref="_secondSightNextScan"/>.</summary>
    private long _secondSightRunSeen;

    /// <summary>The pre-check, without the monitor: a run is up and the scan's 15 s have passed. A cast that lands
    /// between this read and the return is seen on the next beat, 333 ms later, the benign race
    /// <c>RegenTick</c>'s other pre-checks have.</summary>
    private bool SecondSightScanDue()
    {
        long now = Environment.TickCount64;
        long runs = Interlocked.Read(ref _secondSightRuns);
        if (runs != _secondSightRunSeen) { _secondSightRunSeen = runs; _secondSightNextScan = 0; }
        return now < Volatile.Read(ref _secondSightUntil) && now >= _secondSightNextScan;
    }

    /// <summary>One scan, under this session's monitor: if the slot still holds (the pre-check's hint can be a beat
    /// stale, and the expiry pass before this may just have dropped the entry), name each invisible player in
    /// range, the Atlas's line once per name, and wait 15 s for the next. A run with nobody hiding sends
    /// nothing.</summary>
    private void SecondSightScan()
    {
        AssertStateHeld("Second Sight's scan");
        if (!HasStatusCategory(SecondSightSlot)) return;
        _secondSightNextScan = Environment.TickCount64 + SecondSightEveryMs;
        foreach (var name in _world.HiddenPlayersNear(_char.Map, _char.X, _char.Y, SecondSightRange))
            SendMiniText($"{name} is Hidden in the Area");
    }
}
