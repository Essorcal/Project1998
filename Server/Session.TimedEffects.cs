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
/// converts one way, restore the other, and anything already past its deadline is simply not restored — with
/// one exception, a Chung Ryong fury that still owes its wear-out drain (see CaptureTimedEffects).
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

    /// <summary>Snapshot the live timers into <c>_char.Effects</c>. Called by <c>CaptureAndWrite</c>
    /// immediately before every character write (autosave, high-value save, shutdown flush, duplicate-login
    /// kick) and under the session's state monitor, so the snapshot is never staler than the row it ships in
    /// and cannot be taken while a buff is being added. Expired entries are dropped rather than written.</summary>
    private void CaptureTimedEffects()
    {
        AssertStateHeld("_char.Effects");
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

        // A Chung Ryong fury is captured while its TIER is still set, not only while its deadline is ahead: the
        // tier is what prices the wear-out drain, and between the deadline passing and the RegenTick beat that
        // fires ChungRyongRageWearOff there is a window in which the fury is over but unpaid. Dropping it there
        // (the old `now < _rageUntil` alone) let a player log out inside that window — or log out running and
        // stay out past the deadline — and come back owing nothing. A tier of 0 with a past deadline is a fury
        // that already paid, or was STRIPPED by FlushDurations/ClearAllTimedEffects (death, @dispel, Cleanse),
        // which owes no vita: those zero both fields, so neither term holds and nothing is written.
        if (_crRageTier > 0 || now < _rageUntil) { e.RageUntil = TickToUnix(_rageUntil); e.RageAmount = _rageAmount; e.RageName = _rageName; e.CrRageTier = _crRageTier; }
        if (now < _sancDeductUntil)    { e.SancUntil = TickToUnix(_sancDeductUntil); e.SancMult = _sancDeduct; e.SancName = _sancDeductName; }
        if (now < _cunningDeductUntil) { e.CunningUntil = TickToUnix(_cunningDeductUntil); e.CunningMult = _cunningDeduct; }
        if (now < _backstabUntil)      e.BackstabUntil = TickToUnix(_backstabUntil);
        if (now < _flankUntil)         e.FlankUntil    = TickToUnix(_flankUntil);
        if (now < _fourWayUntil)       e.FourWayUntil  = TickToUnix(_fourWayUntil);
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

        BuffClear();
        foreach (var b in e.Buffs)
        {
            if (b.Until <= nowUnix) continue;   // lapsed while logged off
            BuffAdd(new ActiveBuff
            {
                Stat = b.Stat, Amount = b.Amount, Expires = UnixToTick(b.Until),
                Key = b.Key, Name = b.Name, Category = b.Category,
            });
        }

        ClearStatusFlags();
        foreach (var (key, until) in e.StatusFlags)
            if (until > nowUnix) SetStatusFlagUntil(key, UnixToTick(until));

        if (e.RageUntil > nowUnix)     { _rageUntil = UnixToTick(e.RageUntil); _rageAmount = e.RageAmount; _rageName = e.RageName ?? ""; _crRageTier = e.CrRageTier; }
        // A Chung Ryong fury whose deadline went by while we were logged off: re-arm the TIER and the (past)
        // deadline and nothing else — no multiplier, no name, and no AC buff, because the fury is over and its
        // buff was captured expired. That pair is exactly what RegenTick's `_crRageTier > 0` pre-check reads, so
        // the drain fires from the line it always fired from, on the first regen beat after the entry sequence
        // has drawn us. Calling ChungRyongRageWearOff here instead would push a mini-text and a stats packet
        // before the character exists on screen, on the arrival thread rather than the tick thread.
        else if (e.CrRageTier > 0)     { _rageUntil = UnixToTick(e.RageUntil); _crRageTier = e.CrRageTier; }
        if (e.SancUntil > nowUnix)     { _sancDeductUntil = UnixToTick(e.SancUntil); _sancDeduct = e.SancMult; _sancDeductName = e.SancName ?? ""; }
        if (e.CunningUntil > nowUnix)  { _cunningDeductUntil = UnixToTick(e.CunningUntil); _cunningDeduct = e.CunningMult; }
        if (e.BackstabUntil > nowUnix) _backstabUntil = UnixToTick(e.BackstabUntil);
        if (e.FlankUntil > nowUnix)    _flankUntil    = UnixToTick(e.FlankUntil);
        if (e.FourWayUntil > nowUnix)  _fourWayUntil  = UnixToTick(e.FourWayUntil);
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
        if (restored > 0 || _rageUntil > 0 || _crRageTier > 0 || _stealthUntil > 0 || _morphLook != 0 || _sancDeductUntil > 0)
            Log.Info($"   -> restored timed effects: {_buffs.Count} buff(s), {_statusFlags.Count} ward flag(s)" +
                     $"{(_rageUntil > 0 || _crRageTier > 0 ? ", fury" : "")}{(_sancDeductUntil > 0 || _cunningDeductUntil > 0 ? ", deduction" : "")}" +
                     $"{(_backstabUntil > 0 || _flankUntil > 0 ? ", stance" : "")}" +
                     $"{(_stealthUntil > 0 ? ", stealth" : "")}{(_morphLook != 0 ? $", morph({_morphLook})" : "")}");
    }

    /// <summary>An unconditional character write — the spellbook and legend edits that persist whether or
    /// not the dirty flag happens to be set. Shares <see cref="CaptureAndWrite"/> with the throttled autosave
    /// so there is one definition of a consistent row (snapshot under the state monitor, write outside it)
    /// and one sequence deciding which snapshot wins. It used to serialize inline under no lock at all.
    ///
    /// <para>On success it leaves the dirty flag and the AutoSaveMs throttle exactly where it found them,
    /// which is what it always did. Clearing them would arguably be more correct — the whole character has
    /// just been written — but it is a behaviour change, and #29 is not the ticket for it.</para>
    ///
    /// <para>On failure the flag is SET, not left alone: <see cref="CaptureAndWrite"/> re-dirties the session
    /// whether or not the write was dirty-gated, so a failed unconditional write is retried by the next
    /// FlushIfDue or autosave sweep exactly as a failed gated one is. Without that the edit would simply be
    /// gone, because nothing else was going to write it — and since the database's command timeout now gives
    /// up after about five seconds instead of thirty, a contended write fails where it used to be waited
    /// out.</para></summary>
    private bool StoreSave() => CaptureAndWrite(dirtyGated: false);

    /// <summary>
    /// Save TWO sessions atomically — both character rows land in one transaction, or neither does. Used by
    /// the trade finalizer, where two independent saves can tear an exchange in half and leave the world
    /// with a duplicated or destroyed stack (see <see cref="CharacterStore.SaveMany"/>).
    ///
    /// <para><b>Ordering is <see cref="StateRank"/>'s, not the argument order's.</b> Both sessions' state
    /// has to be frozen across the capture, and two threads taking the two monitors in opposite orders is a
    /// textbook ABBA deadlock. <see cref="WithStatePair"/> takes them in the one global order every other
    /// nested acquisition uses (#29), so this composes with the rest of the locking instead of being a
    /// second, private ordering. It used to order on UserKey and take the two save gates; the save gates no
    /// longer guard state.</para>
    ///
    /// <para>Both snapshots are taken under both monitors and the transaction is committed with them
    /// released — the same split as <see cref="FlushNow"/>, and for the same two reasons.</para>
    ///
    /// <para>Failure restores BOTH dirty flags, so the next ordinary flush retries — the same contract
    /// <see cref="FlushNow"/> has, extended across the pair.</para>
    /// </summary>
    internal static bool FlushPair(Session a, Session b)
    {
        if (ReferenceEquals(a, b)) { a.FlushNow(); return true; }
        if (!a._enteredWorld || !b._enteredWorld) { a.FlushNow(); b.FlushNow(); return true; }

        (string User, string Json)[] rows = null!;
        long seqA = 0, seqB = 0;
        WithStatePair(a, b, () =>
        {
            a._dirty = false; b._dirty = false;
            try
            {
                a.CaptureTimedEffects(); b.CaptureTimedEffects();
                seqA = ++a._saveSeq; seqB = ++b._saveSeq;
                rows = new[]
                {
                    (CharacterStore.Key(a._char.Name), CharacterStore.Serialize(a._char)),
                    (CharacterStore.Key(b._char.Name), CharacterStore.Serialize(b._char)),
                };
            }
            catch
            {
                // #179, the pair form of CaptureAndWrite's restore: a capture that throws (either side's
                // serializer) wrote nothing, so BOTH flags go back up exactly as a failed write puts them back
                // below. Otherwise the trade just finalized in memory is captured by no later flush.
                a._dirty = true; b._dirty = true;
                throw;
            }
        });

        // BOTH write gates, in StateRank order — the same order the monitors use, so two trades finalizing
        // from opposite sides cannot ABBA here either. Skipping this is not an option: the pair write and a
        // concurrent single-session write (an autosave sweep, a StoreSave) target the same rows, and without
        // a shared gate and the same sequence the older one can land last and roll the trade back. That is the
        // exact hazard the gate exists for; the pair path is not an exception to it.
        var (firstGate, secondGate) = a.StateRank <= b.StateRank ? (a, b) : (b, a);
        bool ok;
        lock (firstGate._writeGate)
        lock (secondGate._writeGate)
        {
            // Superseded: a NEWER snapshot of one of these characters already landed, and it necessarily
            // already contains this trade (it was captured after the transfer). Writing ours would roll that
            // character back, so we don't — we take the same exit a failed write takes, and both sides are
            // left dirty for the next ordinary flush.
            if (seqA < a._writtenSeq || seqB < b._writtenSeq) ok = false;
            else
            {
                ok = a._store.SaveManyJson(rows);
                if (ok)
                {
                    a._writtenSeq = seqA; b._writtenSeq = seqB;
                    a._lastSaveAtMs = b._lastSaveAtMs = Environment.TickCount64;
                }
            }
        }
        if (ok) return true;
        a._dirty = true; b._dirty = true;   // retried by the next FlushIfDue / autosave sweep
        return false;
    }
}
