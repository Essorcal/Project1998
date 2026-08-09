using Shared;

namespace Server;

/// <summary>
/// Persistence of the session's RUNNING timed effects — buffs, curse statuses, ward flags, the fury/
/// deduction/stance timers, stealth and morph.
///
/// These all used to be session-local: every one of them was dropped on disconnect, so a player who cast
/// a 10-minute Might and relogged (or who was online when the process died) lost it silently. They live
/// on Environment.TickCount64 in the session — a monotonic clock, which is the right choice for a running
/// process because an NTP step can't drag a duration around mid-fight — but TickCount64 is meaningless
/// across a restart, so the persisted form (Shared/TimedEffects) is ABSOLUTE unix milliseconds. Capture
/// converts one way, restore the other, and anything already past its deadline is simply not restored.
///
/// Wall-clock, not remaining-duration, is deliberate: a buff keeps ticking while you're logged off. That
/// makes a quick relog lossless (the thing the player actually notices) without turning logout into a way
/// to bank a fury, and without handing everyone free duration every time the server restarts.
/// </summary>
public sealed partial class Session
{
    private static long NowUnixMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // TickCount64 deadline -> absolute unix ms, and back. Both go through "now" in each clock, so the two
    // never need to agree on an epoch — only on how far away the deadline is.
    private static long TickToUnix(long tick) => NowUnixMs + (tick - Environment.TickCount64);
    private static long UnixToTick(long unix) => Environment.TickCount64 + (unix - NowUnixMs);

    /// <summary>Snapshot the live timers into <c>_char.Effects</c>. Called by StoreSave immediately before
    /// every character write (autosave, high-value save, shutdown flush, duplicate-login kick), so the
    /// snapshot is never staler than the row it ships in. Expired entries are dropped rather than written.</summary>
    private void CaptureTimedEffects()
    {
        long now = Environment.TickCount64;
        var e = new TimedEffects();

        foreach (var b in _buffs)
        {
            if (b.Expires <= now) continue;
            e.Buffs.Add(new SavedBuff
            {
                Stat = b.Stat, Amount = b.Amount, Until = TickToUnix(b.Expires),
                Key = b.Key, Name = b.Name, Category = b.Category,
            });
        }

        foreach (var (key, until) in _statusFlags)
            if (until > now) e.StatusFlags[key] = TickToUnix(until);

        if (now < _rageUntil)          { e.RageUntil = TickToUnix(_rageUntil); e.RageAmount = _rageAmount; e.RageName = _rageName; }
        if (now < _sancDeductUntil)    { e.SancUntil = TickToUnix(_sancDeductUntil); e.SancMult = _sancDeduct; e.SancName = _sancDeductName; }
        if (now < _cunningDeductUntil) { e.CunningUntil = TickToUnix(_cunningDeductUntil); e.CunningMult = _cunningDeduct; }
        if (now < _backstabUntil)      e.BackstabUntil = TickToUnix(_backstabUntil);
        if (now < _flankUntil)         e.FlankUntil    = TickToUnix(_flankUntil);
        if (now < _stealthUntil)       { e.StealthUntil = TickToUnix(_stealthUntil); e.StealthName = _stealthName; }
        if (_morphLook != 0 && now < _morphUntil)
        { e.MorphUntil = TickToUnix(_morphUntil); e.MorphLook = _morphLook; e.MorphColor = _morphColor; e.MorphKey = _morphKey; }

        _char.Effects = e;
    }

    /// <summary>Re-arm the live timers from the loaded character. Runs in HandleArrival right after the load
    /// and BEFORE the entry sequence draws us, so a restored morph/stealth is rendered by the normal
    /// SendSelfLook / ShowPlayer path instead of needing a second appearance push.</summary>
    private void RestoreTimedEffects()
    {
        var e = _char.Effects;
        if (e is null) { _char.Effects = new TimedEffects(); return; }
        long nowUnix = NowUnixMs;

        _buffs.Clear();
        foreach (var b in e.Buffs)
        {
            if (b.Until <= nowUnix) continue;   // lapsed while logged off
            _buffs.Add(new ActiveBuff
            {
                Stat = b.Stat, Amount = b.Amount, Expires = UnixToTick(b.Until),
                Key = b.Key, Name = b.Name, Category = b.Category,
            });
        }

        _statusFlags.Clear();
        foreach (var (key, until) in e.StatusFlags)
            if (until > nowUnix) _statusFlags[key] = UnixToTick(until);

        if (e.RageUntil > nowUnix)     { _rageUntil = UnixToTick(e.RageUntil); _rageAmount = e.RageAmount; _rageName = e.RageName ?? ""; }
        if (e.SancUntil > nowUnix)     { _sancDeductUntil = UnixToTick(e.SancUntil); _sancDeduct = e.SancMult; _sancDeductName = e.SancName ?? ""; }
        if (e.CunningUntil > nowUnix)  { _cunningDeductUntil = UnixToTick(e.CunningUntil); _cunningDeduct = e.CunningMult; }
        if (e.BackstabUntil > nowUnix) _backstabUntil = UnixToTick(e.BackstabUntil);
        if (e.FlankUntil > nowUnix)    _flankUntil    = UnixToTick(e.FlankUntil);
        if (e.StealthUntil > nowUnix)
        {
            _stealthUntil = UnixToTick(e.StealthUntil);
            _stealthName  = string.IsNullOrEmpty(e.StealthName) ? "Invisible" : e.StealthName;
            _stealthShown = true;   // SendSelfLook/ShowPlayer draw the faded form-5 sprite from this
        }
        if (e.MorphLook != 0 && e.MorphUntil > nowUnix)
        {
            _morphUntil = UnixToTick(e.MorphUntil);
            _morphLook  = e.MorphLook;
            _morphColor = e.MorphColor;
            _morphKey   = e.MorphKey ?? "";
        }

        int restored = _buffs.Count + _statusFlags.Count;
        if (restored > 0 || _rageUntil > 0 || _stealthUntil > 0 || _morphLook != 0 || _sancDeductUntil > 0)
            Log.Info($"   -> restored timed effects: {_buffs.Count} buff(s), {_statusFlags.Count} ward flag(s)" +
                     $"{(_rageUntil > 0 ? ", fury" : "")}{(_sancDeductUntil > 0 || _cunningDeductUntil > 0 ? ", deduction" : "")}" +
                     $"{(_backstabUntil > 0 || _flankUntil > 0 ? ", stance" : "")}" +
                     $"{(_stealthUntil > 0 ? ", stealth" : "")}{(_morphLook != 0 ? $", morph({_morphLook})" : "")}");
    }

    /// <summary>The ONE place a character row is written. Refreshes the timed-effect snapshot first, so no
    /// save path can persist a character whose buffs are a save older than the rest of it. Every former
    /// direct <c>_store.Save(_char)</c> call goes through here.</summary>
    private bool StoreSave()
    {
        CaptureTimedEffects();
        return _store.Save(_char);
    }

    /// <summary>
    /// Save TWO sessions atomically — both character rows land in one transaction, or neither does. Used by
    /// the trade finalizer, where two independent saves can tear an exchange in half and leave the world
    /// with a duplicated or destroyed stack (see <see cref="CharacterStore.SaveMany"/>).
    ///
    /// <para><b>Lock order is by UserKey, not by argument order.</b> Both sessions' save gates have to be
    /// held across the capture-and-write, and two threads taking them in opposite orders is a textbook ABBA
    /// deadlock. Ordering on a value both threads compute identically makes that impossible regardless of
    /// which side finalizes.</para>
    ///
    /// <para>Failure restores BOTH dirty flags, so the next ordinary flush retries — the same contract
    /// <see cref="FlushNow"/> has, extended across the pair.</para>
    /// </summary>
    internal static bool FlushPair(Session a, Session b)
    {
        if (ReferenceEquals(a, b)) { a.FlushNow(); return true; }
        if (!a._enteredWorld || !b._enteredWorld) { a.FlushNow(); b.FlushNow(); return true; }

        // Deterministic order. UserKey is unique per online account (the duplicate-login guard enforces it),
        // but tie-break on identity anyway rather than rely on that invariant holding forever.
        int cmp = string.CompareOrdinal(a.UserKey, b.UserKey);
        if (cmp == 0) cmp = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(a)
                          .CompareTo(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(b));
        var (first, second) = cmp <= 0 ? (a, b) : (b, a);

        lock (first._saveGate)
        lock (second._saveGate)
        {
            a._dirty = false; b._dirty = false;
            a.CaptureTimedEffects(); b.CaptureTimedEffects();

            if (a._store.SaveMany(new[] { a._char, b._char }))
            {
                a._lastSaveAtMs = b._lastSaveAtMs = Environment.TickCount64;
                return true;
            }
            a._dirty = true; b._dirty = true;   // retried by the next FlushIfDue / autosave sweep
            return false;
        }
    }
}
