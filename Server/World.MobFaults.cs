using System.Diagnostics;
using System.Text;
using Shared;

namespace Server;

// The tick's fault records. World.Tick's guards catch a throw under World._lock — one guard per pre-sweep
// phase (1)-(1.7) (#106), one per spawn row inside phases (1) and (1.1) (World.SpawnDirector.cs), and two
// in the mob-AI sweep, one per creature and one per map (#36, #109); this file is what happens to a caught
// throw afterwards, once the lock is released. Its own file so the tick body keeps only the lines that queue
// a fault.
public sealed partial class World
{
    /// <summary>One throw caught by the mob-AI sweep, as the guard saw it. Queued under <c>_lock</c> —
    /// the guard copies four references and two numbers and nothing else — and formatted after the lock is
    /// released by <see cref="LogMobFaults"/>. <paramref name="MobKey"/> is the reference the creature held
    /// at the throw, not a copy.</summary>
    /// <param name="WholeMap">True for the per-map guard, which has no creature to name.</param>
    internal readonly record struct MobFault(ushort Map, uint MobId, string? MobKey, Exception Error, bool WholeMap);

    /// <summary>What the throttle counts as "the same fault": the map, the creature's content key (null for
    /// a whole-map fault) and the exception's type.
    ///
    /// <para><b>Why the content key and not the creature id.</b> The faults this exists for are content
    /// faults — a bad boss or spell row throws for every creature built from it — so by id, 200 creatures of
    /// one bad row would still write 200 identical stacks on the first beat, and a respawn (new id, same row)
    /// would write another. By key they write one.</para>
    ///
    /// <para><b>Why the map and the exception type as well.</b> Each keeps a different fault from hiding
    /// behind one already logged. A second, unrelated bug on the same row (another exception type) gets its
    /// own stack instead of being folded into the first one's count; the same row failing on another map,
    /// where terrain, warps and neighbours differ, gets its own stack too. Both stay bounded by the content,
    /// so the throttle's table cannot grow with uptime.</para></summary>
    internal readonly record struct MobFaultKey(ushort Map, string? MobKey, Type ErrorType, bool WholeMap);

    /// <summary>A fault quiet for longer than this (about 10 s at the default 333 ms beat) logs its stack
    /// again when it returns.
    ///
    /// <para>This and <see cref="MobFaultRestackBeats"/> read the beat length from
    /// <c>ServerConfig.Current.TickMs</c>, the same source as <see cref="TickMs"/>, rather than reading
    /// <see cref="TickMs"/> itself. That field is declared in World.cs, and C# leaves the order of static
    /// initializers across partial files to the compiler, so reading it from here would depend on the order
    /// the files are compiled in.</para></summary>
    private static readonly int MobFaultQuietBeats = Math.Max(1, 10_000 / ServerConfig.Current.TickMs);

    /// <summary>A fault that never stops logs its stack again once an hour, so a log rotation cannot leave
    /// its count lines pointing at a stack that is no longer on disk.</summary>
    private static readonly int MobFaultRestackBeats = Math.Max(1, 3_600_000 / ServerConfig.Current.TickMs);

    /// <summary>Tick thread only, and only outside <c>_lock</c> (see <see cref="FaultThrottle{TKey}"/>).</summary>
    private readonly FaultThrottle<MobFaultKey> _mobFaultThrottle = new(MobFaultQuietBeats, MobFaultRestackBeats);

    internal static int MobFaultQuietBeatsForTest => MobFaultQuietBeats;

    /// <summary>A test seam, null in every process that is not the test host: run inside the sweep's PER-MAP
    /// guard, after the map's context is built and before its first creature, with the map id. A throw from
    /// it is the per-map guard's to catch, which is the only way a test reaches that guard — nothing a
    /// content-free map can hold makes the context or the roster walk throw on its own. An instance field,
    /// not a static, so it reaches only the world a test installed it on. Its production cost is one field
    /// read per watched map per beat.</summary>
    internal Action<ushort>? SweepProbeForTest;

    /// <summary>
    /// Write one beat's sweep faults, after <c>_lock</c> is released. A fault's first throw — or its first
    /// after <see cref="MobFaultQuietBeats"/> quiet, or <see cref="MobFaultRestackBeats"/> after its last
    /// stack — is written at Error with the exception in full, in the same words the guards always used.
    /// Every other throw this beat is only counted, and the counts go out as ONE Error line for the whole
    /// beat, naming each fault and how many of its throws went without a stack. A throw whose stack was
    /// written this beat is NOT in that count: on a fault's first beat, 20 throwing creatures give one stack
    /// and a count of 19. So a beat costs the log at most one stack per new fault plus one line, however
    /// many creatures throw.
    /// </summary>
    private void LogMobFaults(List<MobFault> faults, long beat)
    {
        Debug.Assert(!HoldsWorldLock, "mob faults are formatted after World._lock is released, never under it");

        List<(MobFaultKey Key, int Count)>? repeats = null;
        int creatures = 0, sweeps = 0;
        foreach (var f in faults)
        {
            var key = new MobFaultKey(f.Map, f.WholeMap ? null : f.MobKey, f.Error.GetType(), f.WholeMap);
            if (_mobFaultThrottle.Admit(key, beat))
            {
                Log.Error(f.WholeMap
                    ? $"mob AI sweep threw — map {f.Map} is skipped this beat, the other maps continue (repeats are counted, not re-logged)"
                    : $"mob AI step threw — {f.MobKey}#{f.MobId} on map {f.Map} is skipped this beat, the rest of the sweep continues (repeats are counted, not re-logged)",
                    f.Error);
                continue;
            }

            if (f.WholeMap) sweeps++; else creatures++;
            repeats ??= new();
            int i = 0;
            while (i < repeats.Count && !repeats[i].Key.Equals(key)) i++;
            if (i == repeats.Count) repeats.Add((key, 1));
            else repeats[i] = (key, repeats[i].Count + 1);
        }
        if (repeats is null) return;

        var sb = new StringBuilder("mob AI still throwing — skipped this beat with no stack (each fault's stack is logged at its first throw): ")
            .Append(creatures).Append(" creature(s), ").Append(sweeps).Append(" map sweep(s) — ");
        for (int i = 0; i < repeats.Count; i++)
        {
            var (k, n) = repeats[i];
            if (i > 0) sb.Append(", ");
            if (k.WholeMap) sb.Append("map ").Append(k.Map).Append(" sweep ");
            else sb.Append('\'').Append(k.MobKey).Append("' on map ").Append(k.Map).Append(' ');
            sb.Append(k.ErrorType.Name).Append(" x").Append(n);
        }
        Log.Error(sb.ToString());
    }

    // ---- the pre-sweep phases (1)-(1.7) (#106) ------------------------------------------------------
    //
    // A sibling of MobFault rather than a MobFault with a phase added. The two describe different things: a
    // mob fault names a map and a creature, a phase fault names neither — its phase is the whole world's —
    // so one shared record would carry three fields that mean nothing for half its rows, and every reader
    // would branch on which half it holds. A separate key and throttle also mean a phase fault can never be
    // folded into a creature's count, or the other way round, and the mob-fault wording the #109 facts pin
    // stays exactly as it was. What the two DO share is the throttle's shape and its two spans, and those
    // are reused as they are.

    /// <summary>One throw caught inside a pre-sweep phase, as the guard saw it: which phase (a <c>Ph*</c>
    /// constant) and the exception. Queued under <c>_lock</c> — the guard copies a few numbers and
    /// references and nothing else — and formatted after the lock is released by
    /// <see cref="LogPhaseFaults"/>.
    ///
    /// <para>Two guards write these. The phase's own guard in <see cref="Tick"/> (<paramref name="Row"/>
    /// false) ends the phase at its throw, so a beat holds at most one of those per phase. The SPAWN ROW
    /// guards inside phases (1) and (1.1) (<see cref="SpawnDirector.RespawnDuePoints"/>, one per due point,
    /// and <see cref="SpawnDirector.RefillDueGroups"/>, one per group member) end only that row, so a beat
    /// can hold many of those: <paramref name="Map"/> is the row's roster map and <paramref name="Def"/> the
    /// creature the row spawns, the reference as the row held it, not a copy.</para>
    ///
    /// <para>A row fault is a phase fault narrowed to one row, which is why it shares this record rather
    /// than being a sibling like <see cref="MobFault"/>: same phase names, same lock rule, same log path,
    /// and the beat's one list of them already runs from the phase guards to the flush's finally.</para></summary>
    internal readonly record struct PhaseFault(int Phase, Exception Error, bool Row = false, ushort Map = 0, MobDef? Def = null);

    /// <summary>What the throttle counts as "the same phase fault": the phase and the exception's type, so a
    /// second, unrelated bug in the same phase gets its own stack instead of hiding in the first one's
    /// count. A spawn row's fault adds its roster map and its creature's CONTENT id (<see cref="MobDef.Id"/>),
    /// never a live mob id: a bad row throws again on every beat it is due, and a key by mob id would be a
    /// new key each time. By content id the table is bounded by the content — two phases times the maps
    /// times the creatures on them times the exception types — however long the process runs. A whole-phase
    /// fault leaves the three row fields at their defaults, so its key is what it always was.</summary>
    internal readonly record struct PhaseFaultKey(int Phase, Type ErrorType, bool Row = false, ushort Map = 0, int DefId = 0);

    /// <summary>Tick thread only, and only outside <c>_lock</c> (see <see cref="FaultThrottle{TKey}"/>).
    /// The mob throttle's spans: a stack at the first throw, again after ~10 s quiet, again hourly.</summary>
    private readonly FaultThrottle<PhaseFaultKey> _phaseFaultThrottle = new(MobFaultQuietBeats, MobFaultRestackBeats);

    /// <summary>A test seam, null in every process that is not the test host: run first inside each
    /// pre-sweep phase's guard, (1) to (1.7), with that phase's <c>Ph*</c> constant. A throw from it is that
    /// phase's guard's to catch, which is how a test reaches the seven guards: no content-free setup can make
    /// any phase throw on its own. (1) and (1.1) have natural throws — a point or a group member whose spawn
    /// throws — but those are caught one level down, by the spawn row guards, and never reach the phase's.
    /// An instance field, like <see cref="SweepProbeForTest"/>, so it reaches only the world a test
    /// installed it on. Its production cost is seven field reads per beat.</summary>
    internal Action<int>? PreSweepProbeForTest;

    /// <summary>The pre-sweep phases, (1) to (1.7), in the order <see cref="Tick"/> runs them — the
    /// values <see cref="PreSweepProbeForTest"/> is called with.</summary>
    internal static readonly int[] PreSweepPhasesForTest = { PhRespawns, PhRefills, PhMorphs, PhDecoys, PhForage, PhClock, PhWeather };

    /// <summary>A phase's name as the log and the watchdog print it, e.g. <c>(1.6) clock</c>.</summary>
    internal static string PhaseNameForTest(int phase) => PhaseNames[phase];

    /// <summary>
    /// Write one beat's pre-sweep phase faults, after <c>_lock</c> is released. A fault's first throw — or
    /// its first after <see cref="MobFaultQuietBeats"/> quiet, or <see cref="MobFaultRestackBeats"/> after its
    /// last stack — is written at Error with the exception in full. Every other throw this beat is only
    /// named, and the names go out as at most TWO Error lines for the whole beat:
    /// <list type="bullet">
    /// <item>one for whole-phase faults. A phase throws at most once a beat, so that line carries no counts:
    /// each name on it is one phase that lost its rest this beat;</item>
    /// <item>one for spawn row faults, with a count per (phase, map, creature, exception type). Several
    /// points of one bad creature can throw in one beat, so these are counted the way
    /// <see cref="LogMobFaults"/> counts creatures: a throw whose stack was written this beat is not in the
    /// count.</item>
    /// </list>
    /// </summary>
    private void LogPhaseFaults(List<PhaseFault> faults, long beat)
    {
        Debug.Assert(!HoldsWorldLock, "phase faults are formatted after World._lock is released, never under it");

        StringBuilder? repeats = null;
        List<(PhaseFault First, PhaseFaultKey Key, int Count)>? rowRepeats = null;
        foreach (var f in faults)
        {
            string phase = PhaseNames[f.Phase];
            var key = f.Row
                ? new PhaseFaultKey(f.Phase, f.Error.GetType(), Row: true, f.Map, f.Def?.Id ?? 0)
                : new PhaseFaultKey(f.Phase, f.Error.GetType());
            if (_phaseFaultThrottle.Admit(key, beat))
            {
                Log.Error(f.Row
                    ? $"world tick phase {phase}: {RowKind(f.Phase)} threw — '{f.Def?.Key}' (creature id {key.DefId}) on map {f.Map} is skipped this beat, the rest of the phase continues (repeats are counted, not re-logged)"
                    : $"world tick phase {phase} threw — the rest of that phase is skipped this beat, the later phases, the mob sweep and the flush continue (repeats are named, not re-logged)",
                    f.Error);
                continue;
            }

            if (f.Row)
            {
                rowRepeats ??= new();
                int i = 0;
                while (i < rowRepeats.Count && !rowRepeats[i].Key.Equals(key)) i++;
                if (i == rowRepeats.Count) rowRepeats.Add((f, key, 1));
                else rowRepeats[i] = (rowRepeats[i].First, key, rowRepeats[i].Count + 1);
                continue;
            }

            if (repeats is null)
                repeats = new StringBuilder("world tick phase still throwing — the rest of it skipped this beat with no stack (each fault's stack is logged at its first throw): ");
            else
                repeats.Append(", ");
            repeats.Append(phase).Append(' ').Append(f.Error.GetType().Name);
        }
        if (repeats is not null) Log.Error(repeats.ToString());
        if (rowRepeats is null) return;

        var sb = new StringBuilder("spawn rows still throwing — each skipped this beat with no stack (each fault's stack is logged at its first throw): ");
        for (int i = 0; i < rowRepeats.Count; i++)
        {
            var (f, k, n) = rowRepeats[i];
            if (i > 0) sb.Append(", ");
            sb.Append(PhaseNames[k.Phase]).Append(" '").Append(f.Def?.Key).Append("' (creature id ").Append(k.DefId)
              .Append(") on map ").Append(k.Map).Append(' ').Append(k.ErrorType.Name).Append(" x").Append(n);
        }
        Log.Error(sb.ToString());
    }

    /// <summary>What a spawn row is in each phase that has them, for the log.</summary>
    private static string RowKind(int phase) => phase == PhRespawns ? "a spawn point" : "a spawn group member";
}
