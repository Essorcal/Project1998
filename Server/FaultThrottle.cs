namespace Server;

/// <summary>
/// Decides, for one repeating fault, whether this occurrence earns its full record (the stack) or is only
/// counted. The first occurrence of a key is recorded in full; every later one is counted, until the key has
/// been quiet for <see cref="QuietSpan"/> or its last full record is <see cref="RestackSpan"/> old, and
/// then the next occurrence is recorded in full again.
///
/// <para><b>Why it exists (#109).</b> The tick's per-mob guard used to write a full <c>Log.Error</c> — type,
/// message and stack — for every creature that threw, on every beat. A content-shaped fault (a bad boss or
/// spell row) hits every creature of that row on every beat, so 200 of them wrote 2,000 stacks and ~1.1 MB of
/// log in ten beats. The first stack says everything the 1,999 after it do; what a reader of the log still
/// needs from the rest is that the fault is STILL happening and how often, which a count carries.</para>
///
/// <para><b>Why the two spans.</b> <see cref="QuietSpan"/>: a fault that stops and later comes back may be
/// a different occurrence of the same bug with a different cause, and its return is news, so it gets its
/// stack again. <see cref="RestackSpan"/>: a fault that never stops would otherwise have its one stack
/// rotated out of the log eventually, leaving only counts that point at nothing.</para>
///
/// <para>Units are the caller's: the tick counts beats. Nothing here reads a clock, so a test drives it
/// with plain numbers and the same shape serves a caller that counts milliseconds (the wire-dump throttle
/// #73 asks for is that shape).</para>
///
/// <para><b>Not thread-safe, on purpose.</b> One owner thread; the world tick is the only one today, and
/// it touches this only AFTER releasing <c>World._lock</c>. Adding a lock here would be a new row in
/// <c>docs/common/Locking.md</c>, which a log throttle is not worth.</para>
///
/// <para><b>Bounded.</b> One entry per distinct key ever seen, never removed. The tick's keys are
/// (map, creature key, exception type) triples, so the table is bounded by the content, not by time: a
/// respawning creature has a new id but the same key.</para>
/// </summary>
internal sealed class FaultThrottle<TKey> where TKey : notnull
{
    private readonly Dictionary<TKey, (long LastSeen, long LastFull)> _seen = new();

    /// <summary>A key that has not occurred for MORE than this many units is recorded in full on its
    /// next occurrence.</summary>
    public long QuietSpan { get; }

    /// <summary>A key that keeps occurring is recorded in full again once its last full record is at least
    /// this many units old.</summary>
    public long RestackSpan { get; }

    public FaultThrottle(long quietSpan, long restackSpan)
    {
        if (quietSpan < 1) throw new ArgumentOutOfRangeException(nameof(quietSpan));
        if (restackSpan < 1) throw new ArgumentOutOfRangeException(nameof(restackSpan));
        QuietSpan = quietSpan;
        RestackSpan = restackSpan;
    }

    /// <summary>Record one occurrence of <paramref name="key"/> at <paramref name="now"/>. True when this
    /// occurrence should be written in full, false when it should only be counted.</summary>
    public bool Admit(TKey key, long now)
    {
        if (_seen.TryGetValue(key, out var s) && now - s.LastSeen <= QuietSpan && now - s.LastFull < RestackSpan)
        {
            _seen[key] = (now, s.LastFull);
            return false;
        }
        _seen[key] = (now, now);
        return true;
    }

    /// <summary>Distinct keys ever admitted. For tests and diagnostics.</summary>
    public int Count => _seen.Count;
}
