using System.Diagnostics;

namespace Server;

// The Debug-only half of the #90 rule: nothing may enter the Lua gate (Session.EnterScriptGate) while holding
// ANY World's _lock (docs/common/Locking.md, gate row 1 over World._lock row 3). The gate is static and has no
// World to ask, so every World hands its lock object to this registry when it is built, and the gate asks
// Monitor.IsEntered of each one. Nothing here takes a lock, and no `lock (_lock)` site changes: registration is
// a copy-on-write array swapped with Interlocked.CompareExchange, and the check only reads.
public sealed partial class World
{
    /// <summary>Weak references to the <c>_lock</c> object of every World built in this process — Debug builds
    /// only. Weak so the registry never keeps a dead World's lock alive: the test suite builds a World per
    /// fixture and several more inline, and a strong list would grow for the whole run. Dead entries are
    /// dropped the next time a World registers, so the array holds the live worlds plus whatever died since
    /// the last construction.</summary>
    private static WeakReference<object>[] _scriptGateWorldLocks = Array.Empty<WeakReference<object>>();

    /// <summary>Add this world's lock to the registry the Lua gate checks. <c>[Conditional("DEBUG")]</c>: in
    /// a Release build the compiler removes the call from the constructor entirely, so the registry stays
    /// empty and nothing is allocated.</summary>
    [Conditional("DEBUG")]
    private static void RegisterForScriptGateAssert(object worldLock)
    {
        while (true)
        {
            var old = Volatile.Read(ref _scriptGateWorldLocks);
            var next = new List<WeakReference<object>>(old.Length + 1);
            foreach (var w in old)
                if (w.TryGetTarget(out _)) next.Add(w);
            next.Add(new WeakReference<object>(worldLock));
            if (ReferenceEquals(Interlocked.CompareExchange(ref _scriptGateWorldLocks, next.ToArray(), old), old))
                return;
        }
    }

    /// <summary>Whether the calling thread is inside ANY World's <c>_lock</c> — the static counterpart of
    /// <see cref="HoldsWorldLock"/>, for <c>Session.EnterScriptGate</c>, which has no World to ask.
    ///
    /// <para><b>Debug builds only, and only inside <c>Debug.Assert</c>.</b> In a Release build no World
    /// registers (see <see cref="RegisterForScriptGateAssert"/>), so this is always false there; a Release
    /// caller would be reading a registry that was never filled.</para></summary>
    internal static bool HoldsAnyWorldLock
    {
        get
        {
            foreach (var w in Volatile.Read(ref _scriptGateWorldLocks))
                if (w.TryGetTarget(out var worldLock) && Monitor.IsEntered(worldLock)) return true;
            return false;
        }
    }
}
