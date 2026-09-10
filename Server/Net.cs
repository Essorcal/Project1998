using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using Shared;

namespace Server;

/// <summary>The game process's host: owns the <see cref="World"/> and the character store, starts the world's
/// threads, registers the exit hooks that flush every connected player, and runs <see cref="TkAcceptor"/> on
/// the configured ports with one <c>Session</c> per admitted connection.</summary>
public sealed class TkListener
{
    private readonly int[] _ports;
    private readonly CharacterStore _store;
    private readonly World _world = new();   // the one shared world every session broadcasts through; RunAsync starts it

    public TkListener(int[] ports)
    {
        _ports = ports;
        // Anchor the character store to the deployment root, NOT the current working directory. Anchoring
        // to cwd caused a nasty "regression": launching the server from Server\ instead of the repo root
        // pointed the store at an empty Server\data\chars, so every login missed its saved character and
        // rendered the default face/gender. RepoPaths walks up from the binary to the root so the store is
        // the same folder no matter where the process is started from.
        _store = new CharacterStore(Shared.RepoPaths.CharsDir());
        Log.Info($"character store: {_store.Directory}");
    }

    public async Task RunAsync()
    {
        // The World is constructed as a field initializer (in-memory state only); its tick thread, autosave
        // sweep, watchdog, restart ladder and status writer start HERE, at the point the process commits to
        // being a running server. Constructing a World has no side effects of its own, which is what lets a
        // test hold one without threads — see StartWorld.
        StartWorld();

        // Graceful-shutdown flush hook (robust persistence, complements the per-session autosave in
        // World.AutoSaveLoop/Session.FlushIfDue): on a clean stop, save every connected player's pending
        // mutation before the process actually exits. This is the flush half of a graceful restart — it
        // CANNOT help against a hard crash/kill -9/power loss, which is exactly what the periodic autosave
        // sweep + each session's own on-thread flush already bound to ~AutoSaveMs instead.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;   // we exit ourselves once the flush completes, not mid-write
            Shutdown("Ctrl+C");
            Environment.Exit(0);   // also re-raises ProcessExit below; the _shutdownOnce guard makes that a no-op
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown("ProcessExit");

        // SIGTERM explicitly. This is how a Linux host actually stops the server — `systemctl restart`,
        // `docker stop`, and an OOM-killer-adjacent supervisor all send SIGTERM, never Ctrl+C. The .NET
        // runtime does raise ProcessExit for SIGTERM, so the handler above would cover it, but registering
        // here makes the deployment-critical path explicit rather than an implementation detail, and gets
        // the flush started at the top of the shutdown rather than at the very end of it. The
        // _shutdownOnce guard means running through both paths flushes exactly once.
        // (Under systemd, TimeoutStopSec is what bounds this before SIGKILL; our unit sets 30s.)
        _sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            Shutdown("SIGTERM");
            ctx.Cancel = false;   // let the runtime carry on terminating; we only wanted the flush first
        });

        // The accept loop itself is Shared.TkAcceptor, the one copy both processes run: bind, guarded accept,
        // NoDelay, the two-stage PROXY admission and ConnGuard. ConnGuard.FromEnv("GAME") is what namespaces
        // this process's P1998_GAME_* limits (per-IP/global/rate admission control); everything else the game
        // differs in is the delegate below, which is the line the old loop ended on.
        await new TkAcceptor(_ports, ConnGuard.FromEnv("GAME"),
                             (TcpClient client, int port, IPAddress? realIp) =>
                                 new Session(client, port, _store, _world, realIp).RunAsync())
            .RunAsync();
    }

    private PosixSignalRegistration? _sigterm;   // held for the process lifetime; disposing would unhook it

    /// <summary>Start the world's background machinery: the tick and autosave threads, the watchdog probes,
    /// the restart ladder and the status writer. It lives on the host rather than on <see cref="World"/>
    /// because starting threads is process lifecycle, not world state, and this is the one place that decides
    /// the process is a running server. Idempotent — a second call is a no-op rather than a duplicate set of
    /// threads (the guard is <c>World.MarkStarted</c>, next to the <c>IsStarted</c> flag it sets).</summary>
    private void StartWorld()
    {
        if (!_world.MarkStarted()) return;

        // DEDICATED THREADS, not Task.Run. Both of these used to be thread-pool work items, which put the
        // world heartbeat behind every other pool item in the process: session read-loop continuations, the
        // synchronous SQLite saves below, Lua, and any stray blocking call. When the pool ran out of threads
        // the runtime injected replacements at only ~1-2 per second, and the tick simply did not run in the
        // meantime — a multi-second, self-recovering freeze of the entire world with nothing in the log to
        // show for it. A dedicated thread cannot be starved by pool pressure.
        new Thread(_world.TickLoop)     { IsBackground = true, Name = "world-tick" }.Start();
        new Thread(_world.AutoSave.Run) { IsBackground = true, Name = "world-autosave" }.Start();

        // Pool headroom + the pool-latency and client-silence probes. Started here because this is the
        // first point where a World exists for the silence scanner to walk.
        Watchdog.RaiseMinThreads();
        Watchdog.Start(_world);

        _ = Task.Run(_world.Restarts.Loop);      // restart-warning ladder + the deploy's file trigger (1s cadence, not latency-critical)
        _ = Task.Run(() => StatusFile.Loop(_world));   // run/status.json for the launcher's "N online" pill
    }

    private int _shutdownOnce;   // Interlocked guard: Environment.Exit(0) below re-raises ProcessExit, so
                                  // both handlers can reach Shutdown -- make sure the flush runs exactly once.
    private void Shutdown(string reason)
    {
        if (Interlocked.Exchange(ref _shutdownOnce, 1) != 0) return;
        Log.Info($"=== shutdown signal ({reason}) — flushing connected players ===");
        // Both numbers, always. A failure here is a player's last state gone for good (there is no next
        // sweep past this point), so "saved N" alone — which is what this line used to print, and it
        // printed the CONNECTED count at that — would report a lossy shutdown as a clean one.
        var (saved, failed) = _world.AutoSave.SaveAll();
        // Warn, not Error, deliberately: this is a COUNT, not an exception, and Log.Error takes a real
        // exception on purpose (see Log). The losses themselves are already logged at Error, one line per
        // character, each with the stack that caused it — this line only makes the tally impossible to miss.
        if (failed == 0) Log.Info($"   -> saved {saved} player(s)");
        else Log.Warn($"   -> saved {saved} player(s), LOST {failed} — their last state is GONE; see the flush errors above");
        // Logging is asynchronous now (see Log), so the lines above are still queued at this point and the
        // Environment.Exit(0) that follows Ctrl+C would discard them. Drain the queue last, once nothing
        // else has anything left to say.
        Log.Shutdown();
    }
}
