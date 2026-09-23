using System.Diagnostics;
using System.Text;
using Shared;

namespace Server;

// The mob-AI sweep's fault records (#109). World.Tick's two guards (one per creature, one per map) catch a
// throw under World._lock; this file is what happens to it afterwards, once the lock is released. Its own
// file so the tick body keeps only the two lines that queue a fault.
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
}
