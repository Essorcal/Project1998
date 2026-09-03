using System.Diagnostics;

namespace Server;

/// <summary>
/// The session's state monitor — one lock per player, taken around every entry into that player's state
/// from any thread (#29).
///
/// <para>Before this, the documented invariant was "session state is only touched on the session's read
/// loop" and the code said otherwise in four places at once. <c>_buffs</c> is a plain
/// <c>List&lt;ActiveBuff&gt;</c>: removed from on the WORLD-TICK thread (<c>ExpireBuffs</c>, via
/// <c>RegenTick</c>), added to on the READ LOOP (eighteen sites in Session.Spells.cs), enumerated on the
/// AUTOSAVE thread (<c>CaptureTimedEffects</c>), and reached across from a PEER's read loop (PvP damage,
/// trade, a GM command aimed at someone else). <c>_statusFlags</c>, <c>_char.Equipment</c> and <c>_char</c>
/// have the same shape. The visible failures were buffs vanishing, a save skipped, and nothing logged.</para>
///
/// <para><b>Three rules.</b> They are what make this deadlock-free rather than merely locked.</para>
///
/// <para><b>1. Lock order is session-state, THEN <c>World._lock</c>.</b> Never the other way round; the
/// assert at the head of every acquisition here is what says so. That order is feasible because the tick
/// already queues every session-facing call and applies it after releasing <c>_lock</c> (see World.Tick's
/// <c>hits</c>/<c>mobCasts</c>/<c>trapDamage</c>/<c>expiredMorphs</c> lists, and the snapshot-then-send
/// shape of every <c>Broadcast</c>), so the tick never enters a session while holding the world.</para>
///
/// <para><b>2. Session monitors are only ever entered in ASCENDING <see cref="StateRank"/>.</b> This is the
/// interesting one. A handler runs under its OWN monitor and can reach a peer — A casts at B while B casts
/// at A is the textbook ABBA deadlock, and on a PvP map it is a thing two players can do by accident in the
/// same instant. Acquiring in a total order makes the cycle unrepresentable. When a nested acquisition would
/// be DESCENDING, the acquisition drops the higher-ranked monitors this thread holds, takes the new one,
/// retakes them in ascending order, runs the body with all of them held, and restores exactly what the
/// caller held on the way out. The caller's own state is unguarded only while we sit in
/// <c>Monitor.Enter</c>, when none of the caller's code is running — and even that window is strictly better
/// than the status quo it replaces, which was no lock at all.</para>
///
/// <para><b>3. Re-entrant.</b> Already holding it means just running the body. That is what lets a method
/// that is called both on <c>this</c> (from inside a handler) and on a peer be wrapped exactly once, at its
/// own definition, instead of at every call site.</para>
///
/// <para>Step two of #29 — replacing the monitor with a <c>Channel&lt;Action&gt;</c> drained by the read
/// loop — is deliberately NOT here. It is the follow-up; this is the seam it needs.</para>
/// </summary>
public sealed partial class Session
{
    private readonly object _state = new();

    private static long _nextStateRank;

    /// <summary>This session's position in the global acquisition order (rule 2). Allocation order, assigned
    /// once and never changed — any total order works, and the cheapest one that cannot go stale is a
    /// counter. Compared, never interpreted.</summary>
    internal long StateRank { get; } = Interlocked.Increment(ref _nextStateRank);

    /// <summary>The session monitors the CURRENT thread holds, in the order it took them. Thread-static
    /// because it is a property of the thread's stack, not of any session: rule 2 has to be decided from
    /// what this thread already holds, and <c>Monitor.IsEntered</c> can only answer for one object at a
    /// time.</summary>
    [ThreadStatic] private static List<Session>? _held;

    /// <summary>Whether the calling thread is inside this session's monitor. The Debug guards on the
    /// mutation chokepoints (<see cref="AssertStateHeld"/>) read it; so does the deadlock test.</summary>
    internal bool StateHeld => Monitor.IsEntered(_state);

    // ---- the viewport lock, and why it is counted -----------------------------------------------------
    // _viewLock (Session.cs) guards the "what have I drawn" sets. It is a SECOND per-session lock, so unlike
    // World._lock a thread can hold one session's and want another session's monitor — which is exactly the
    // cycle that showed up when Snapshot() started taking the monitor: the viewport reconcile held viewer B's
    // _viewLock across ShowPlayer -> subject A's Snapshot, while a morph/stealth/death revert on A's own read
    // loop held A's monitor and broadcast DespawnEntity into B's _viewLock. Both run constantly.
    //
    // The rule is therefore the same shape as rule 1: session monitors are OUTSIDE _viewLock, never inside.
    // Monitor.IsEntered can only answer for one object, and the offending lock belongs to a DIFFERENT session,
    // so the only way to assert it is to count.
    [ThreadStatic] private static int _viewDepth;

    /// <summary>Whether this thread is inside ANY session's viewport lock.</summary>
    internal static bool HoldsAnyViewLock => _viewDepth > 0;

    /// <summary>Take this session's viewport lock: <c>using (EnterView()) { ... }</c>. The only way to take
    /// it — a bare <c>lock (_viewLock)</c> would not be counted, and the assert would go blind.</summary>
    private ViewGuard EnterView()
    {
        Monitor.Enter(_viewLock);
        _viewDepth++;
        return new ViewGuard(this);
    }

    /// <summary>Run <paramref name="body"/> holding this session's viewport lock — for the test that pins
    /// the lock-order assert, the counterpart of <c>World.UnderWorldLockForTest</c>. Production code takes it
    /// through <see cref="EnterView"/>.</summary>
    internal void UnderViewLockForTest(Action body)
    {
        using (EnterView()) body();
    }

    private readonly struct ViewGuard : IDisposable
    {
        private readonly Session _owner;
        internal ViewGuard(Session owner) => _owner = owner;
        public void Dispose()
        {
            _viewDepth--;
            Monitor.Exit(_owner._viewLock);
        }
    }

    /// <summary>
    /// Hold this session's state monitor for the lifetime of the returned guard:
    /// <c>using var _ = session.EnterState();</c>.
    ///
    /// <para>The allocation-free form, for the hot paths — <c>Snapshot</c> runs once per peer per player per
    /// 600ms tick, and a delegate per call there is thousands of dead objects a second on a busy map.
    /// Everything less frequent reads better as <see cref="WithState(Action)"/>.</para>
    /// </summary>
    internal StateGuard EnterState()
    {
        // Rule 1. Entering a session monitor with the world lock already held is the inversion this whole
        // ordering rests on not happening; asserting here catches it at the exact call that did it.
        Debug.Assert(!_world.HoldsWorldLock,
            "lock order violated: World._lock is held while entering a session monitor. " +
            "The order is session-state THEN World._lock — queue the session-facing call and apply it " +
            "after releasing the world lock, the way World.Tick does.");
        Debug.Assert(!HoldsAnyViewLock,
            "lock order violated: a session viewport lock is held while entering a session monitor. " +
            "The order is session-state THEN _viewLock — decide under the viewport lock and send outside " +
            "it, the way SyncGroundItems and ReconcilePeer do.");

        var held = _held ??= new List<Session>();

        // Rule 3: re-entrant. This is the COMMON case for a method wrapped at its own definition and reached
        // from a handler that already holds the monitor.
        //
        // This short-circuit is also load-bearing for the Lua gate, which is not obvious from here: because
        // the re-entrant case never calls Monitor.Enter again, a thread's recursion count on any one session
        // monitor is always exactly 1 — so the gate's slow path can release it with a single Monitor.Exit.
        // If a nested acquisition ever starts re-entering for real, EnterScriptGate stops being able to drop
        // what it holds, and with it goes the invariant that a thread waiting for the gate holds no monitor.
        if (held.Contains(this)) return default;

        // Rule 2: ascending only. Anything this thread holds that outranks us has to come off first.
        List<Session>? descended = null;
        foreach (var s in held)
            if (s.StateRank > StateRank) (descended ??= new List<Session>()).Add(s);

        if (descended is null)
        {
            Monitor.Enter(_state);
            held.Add(this);
            return new StateGuard(this);
        }

        // Out of order. Drop the offenders, take ours, put them back on top in ascending rank — so every
        // monitor this thread holds is once again held in ascending order, and no cycle can form with a
        // thread doing the same thing from the other side.
        descended.Sort(static (a, b) => a.StateRank.CompareTo(b.StateRank));
        foreach (var s in descended) Monitor.Exit(s._state);
        Monitor.Enter(_state);
        foreach (var s in descended) Monitor.Enter(s._state);
        held.Add(this);
        return new StateGuard(this);
    }

    /// <summary>What <see cref="EnterState"/> hands back. A struct, so the hot paths pay nothing for it; a
    /// <c>default</c> one (the re-entrant case) releases nothing.</summary>
    internal readonly struct StateGuard : IDisposable
    {
        private readonly Session? _owner;

        internal StateGuard(Session owner) => _owner = owner;

        /// <summary>Releases the monitor this guard took, and only that. The caller's own monitors — the ones
        /// the descending path had to drop and retake on the way IN — simply stay held, which is exactly the
        /// state it walked in with. (An earlier version exited and re-entered them here as well; Monitor has
        /// no LIFO requirement, so that bought nothing and opened a second window where the caller's state
        /// was unheld.)</summary>
        public void Dispose()
        {
            if (_owner is null) return;   // re-entrant: someone above us owns the release
            _held!.Remove(_owner);
            Monitor.Exit(_owner._state);
        }
    }

    // ---- the Lua world, and why it is one gate outside the monitors ----------------------------------
    // A MoonSharp Script is not thread-safe, so each script host used to serialize itself under its own lock:
    // LuaVerbHost (spell_verbs, item_verbs), NpcScript, MobScript. Once Handle runs under the session monitor
    // those locks became the second cycle #29 introduced, and this one StateRank cannot break, because the
    // host lock sits outside the session ordering entirely:
    //
    //   B's read loop:  B._state (Handle)  ->  host lock (a Lua cast)  ->  A._state  (healTarget/applyCurse/
    //                                                                      amplify all reach a peer)
    //   A's read loop:  A._state (Handle)  ->  host lock (any Lua-backed cast)
    //
    // B waits on A._state, A waits on the host lock. A poet healing a party member while the other player
    // casts anything is enough.
    //
    // The fix is one gate for the whole Lua world, ranked OUTSIDE every session monitor. One lock rather than
    // three because the hosts nest (an NPC dialog can run an item verb) and a re-entrant single gate makes
    // host-against-host ordering a non-question; the cost is that all Lua is now serialized process-wide,
    // which each host already very nearly was, and a verb is microseconds.
    //
    // The ordering is enforced in the only place it can go wrong:
    //   FAST PATH  — TryEnter without waiting. Acquiring a lock you never block on cannot complete a cycle,
    //                so a handler already holding its own monitor keeps it. This is the overwhelmingly common
    //                case, and it is why an ordinary cast does not lose its atomicity.
    //   SLOW PATH  — someone else is in Lua. Drop every session monitor this thread holds BEFORE waiting, then
    //                retake them in rank order. That is what guarantees the invariant everything rests on:
    //                a thread waiting for the gate holds no session monitor, so whoever holds the gate can
    //                always take any monitor it needs and finish.
    private static readonly object ScriptGateLock = new();

    /// <summary>Take the Lua gate: <c>using (Session.EnterScriptGate()) { ... }</c>. Every entry into a
    /// MoonSharp <c>Script</c> in this process goes through here.</summary>
    internal static ScriptGateGuard EnterScriptGate()
    {
        Debug.Assert(!HoldsAnyViewLock,
            "lock order violated: a session viewport lock is held while entering the Lua gate. Nothing under " +
            "_viewLock may run a script — decide under the viewport lock and act outside it.");
        // The OTHER half of the #90 rule — that World._lock must not be held on the way IN here — is asserted
        // at the Lua host entry points rather than in this method, because this method is static and has no
        // World to ask (EnterState can, being an instance). See MobScript.Fire. LuaVerbHost and NpcScript hold
        // no World at all, so their entries are documented by that rule rather than checked by it; the path
        // that can realistically be reached from inside the lock is the tick's hook drain, which is Fire's.

        if (Monitor.IsEntered(ScriptGateLock)) return default;   // re-entrant: one host reaching another
        if (Monitor.TryEnter(ScriptGateLock)) return new ScriptGateGuard(true);

        var held = _held;
        if (held is { Count: > 0 })
        {
            var dropped = held.ToArray();
            Array.Sort(dropped, static (a, b) => a.StateRank.CompareTo(b.StateRank));
            foreach (var s in dropped) Monitor.Exit(s._state);
            try { Monitor.Enter(ScriptGateLock); }
            finally { foreach (var s in dropped) Monitor.Enter(s._state); }
        }
        else Monitor.Enter(ScriptGateLock);

        return new ScriptGateGuard(true);
    }

    internal readonly struct ScriptGateGuard : IDisposable
    {
        private readonly bool _owned;
        internal ScriptGateGuard(bool owned) => _owned = owned;
        /// <summary>Releases the gate only — the caller keeps whatever session monitors it walked in with,
        /// including any the slow path had to put back.</summary>
        public void Dispose() { if (_owned) Monitor.Exit(ScriptGateLock); }
    }

    /// <summary>Run <paramref name="body"/> holding this session's state monitor. THE entry point: every
    /// tick, peer, autosave and read-loop path into this session's state goes through here, through
    /// <see cref="EnterState"/>, or through <see cref="WithStatePair"/>.</summary>
    internal void WithState(Action body)
    {
        using var _ = EnterState();
        body();
    }

    /// <summary>The value-returning form — <c>ReceiveSpellDamage</c> hands its caller the damage it applied,
    /// so it cannot go through the <c>Action</c> overload.</summary>
    internal T WithState<T>(Func<T> body)
    {
        using var _ = EnterState();
        return body();
    }

    /// <summary>
    /// Run <paramref name="body"/> holding BOTH sessions' monitors — for the operations that are genuinely
    /// about a pair and would tear if either half moved: the trade finalizer, and the paired save it ends in
    /// (<c>Session.FlushPair</c>).
    ///
    /// <para>Taken in <see cref="StateRank"/> order like everything else, so this composes with rule 2
    /// instead of being a second, private ordering. The pair form exists because the alternative — the
    /// caller's monitor plus a nested peer acquisition — would make the finalizer's two halves two separate
    /// critical sections with a gap in the middle, which is the exact tear it has to prevent.</para>
    ///
    /// <para><b>A paired body must not enter the Lua gate.</b> Under contention the gate drops the monitors
    /// this thread holds while it waits — which for a pair is precisely the gap this method exists to close,
    /// and it would reopen it silently. Nothing paired reaches Lua today (the trade finalizer and the
    /// wedding are both Content lookups, sends and saves); this is the note that keeps it that way.</para>
    /// </summary>
    internal static void WithStatePair(Session a, Session b, Action body)
    {
        if (ReferenceEquals(a, b)) { a.WithState(body); return; }
        var (first, second) = a.StateRank <= b.StateRank ? (a, b) : (b, a);
        using var outer = first.EnterState();
        using var inner = second.EnterState();
        body();
    }

    /// <summary>
    /// Debug-only guard on a mutation chokepoint: this write must be happening under the monitor.
    ///
    /// <para>Acceptance box 1 of #29 — "every mutation of <c>_buffs</c>, <c>_statusFlags</c>,
    /// <c>Equipment</c>, <c>_char</c> happens under the session monitor" — is a claim about roughly thirty
    /// scattered sites, and prose is not evidence. Each family funnels through helpers that open with this,
    /// so a Debug build (which is what <c>dotnet test</c> and a dev server run) fails loudly on any path this
    /// refactor missed, instead of silently losing a buff the way the unlocked code did.</para>
    ///
    /// <para><b>The three collections are covered exactly; <c>_char</c> is covered by PROXY, and the
    /// difference matters.</b> <c>_buffs</c>, <c>_statusFlags</c> and <c>_char.Equipment</c> have four, three
    /// and three writers respectively and every one of them is a guarded helper, enforced by a source scan.
    /// <c>_char</c>'s scalars are assigned in hundreds of places and cannot be funnelled without a rewrite
    /// the ticket does not ask for, so what is guarded is <c>MarkDirty</c>/<c>SaveChar</c> — which every
    /// mutation worth persisting reaches. A <c>_char</c> write that never marks the character dirty is
    /// therefore neither guarded nor detected. That is the honest bound on this box.</para>
    /// </summary>
    [Conditional("DEBUG")]
    private void AssertStateHeld(string what)
    {
        Debug.Assert(StateHeld,
            $"{what} mutated session state outside the state monitor. Wrap the entry point that reaches " +
            "it in Session.WithState — see Server/Session.State.cs.");
    }
}
