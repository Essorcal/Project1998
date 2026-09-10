using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Shared;

internal enum LogLevel
{
    Info,
    Warn,
    Error,
}

/// <summary>
/// Asynchronous, non-blocking logger. <see cref="Info"/> formats a line and ENQUEUES it; a single dedicated
/// background thread does every console + file write.
///
/// <para><b>Why this is a queue and not a lock.</b> This used to hold one process-global lock across a
/// synchronous <c>Console.WriteLine</c> AND an AutoFlush file write — on the packet path, and on the world
/// tick thread. Anything that made a console write slow therefore froze the whole server for as long as it
/// lasted. Two real cases, both of which presented as "the game hung for several seconds":</para>
/// <list type="bullet">
///   <item><b>QuickEdit selection.</b> Windows consoles enable QuickEdit by default, and run-server.bat
///     starts the game in a real console window. Clicking (or accidentally dragging) in that window blocks
///     every console write until Esc/Enter — so with the lock held, the world tick, mob AI and all packet
///     handling stopped dead until somebody noticed.</item>
///   <item><b>Rotation.</b> Disposing, deleting and moving a 64MB file happened under that same lock, with
///     an on-access virus scanner in the path. Every thread in the process waited on it.</item>
/// </list>
/// <para>Now a stuck console blocks only this one writer thread. The game keeps ticking, and the queue
/// absorbs the backlog; if the console stays stuck long enough to fill it, we DROP lines (and say how many)
/// rather than grow without bound or apply back-pressure to the game. Losing log lines is always better than
/// stalling the world — that trade is the entire point of this file.</para>
/// <para><b>Both processes.</b> This is the ONE logger: the game server and the login server share it, with
/// the same format, the same rotation and the same non-blocking guarantee. It lives in the package-free
/// <c>Shared.Core</c> leaf project so the internet-facing login process picks it up without taking a
/// dependency on the game core (the reason its own copy gave for staying local). Until then the login
/// server — the process actually exposed to the internet — was the one still holding a process-global lock
/// across a synchronous console write, so a QuickEdit selection in ITS window blocked every login until
/// somebody pressed Esc. The two per-process differences are declared by the entry point through
/// <see cref="Configure"/>: the wire-dump default (ON for the game, OFF for the login channel, whose packets
/// carry passwords in the clear — see <see cref="WireEnabled"/>) and the rotation size.</para>
/// </summary>
public static class Log
{
    // Bounded on purpose: see the class doc. At ~120 bytes/line this caps the backlog at roughly 8MB, which
    // is minutes of a stuck console at wire-dump volume — far longer than any real stall — while still
    // guaranteeing a hung terminal can never OOM the server.
    internal const int QueueCapacity = 65_536;
    // Keep 4,096 slots (about 480KB at the 120-bytes-per-line estimate above) for warnings and errors.
    // That preserves thousands of diagnostic records while reducing the ordinary backlog by only 6.25%.
    internal const int ReservedLines = 4_096;
    // How many queued lines one wake-up may coalesce into a single console + file write. Batching is what
    // makes the wire dump affordable: the cost of a console write is dominated by the call, not the bytes.
    private const int BatchLines = 512;

    // Not readonly, and the writer thread is handed its own queue rather than reading this field: Shutdown
    // completes the collection for good, so the only way back is a fresh queue + a fresh writer. Production
    // never takes that path (a process shuts its log down once, on the way out); RestartWriterForTest does,
    // so the Shutdown fact does not leave the rest of the test process without a log.
    private static BlockingCollection<string> _queue = NewQueue();

    private static BlockingCollection<string> NewQueue() => new(new ConcurrentQueue<string>(), QueueCapacity);

    // Set before CompleteAdding so a line that arrives during shutdown is dropped rather than met with the
    // InvalidOperationException TryAdd throws on a completed collection. The login server logs from every
    // session thread, and Shutdown now runs from its Ctrl+C/SIGTERM/ProcessExit hooks while those threads are
    // still live: without this, flushing the tail would trade a lost tail for a crash on the way out.
    private static volatile bool _closed;

    private static StreamWriter? _file;    // writer thread only, after AttachFile hands it over
    private static string _path = "";
    private static long _written;
    // Refused Info, Warn, Error. Interlocked, and MONOTONE: the writer thread reads it to build the overflow
    // notice but never resets it (see TakeDropped). Only ResetDroppedCountsForTest, which no production path
    // calls, ever puts a count back.
    //
    // It used to be the writer that zeroed these, which made every test that refuses a few records and then
    // reads the counters a race against the writer's idle flush: upstream CI run 34434683832 read (0, 0, 0)
    // against an expected (0, 1, 1), with "dropped 0 info, 1 warn, 1 error" in the same captured log.
    private static readonly int[] DroppedByLevel = new int[3];

    // How much of DroppedByLevel each notice has already accounted for. WRITER THREAD ONLY — it is the one
    // thread that formats notices, so this needs no interlocking, and keeping the bookkeeping here instead of
    // in the shared counter is what removes the race: there is no moment when a refusal is in neither place.
    private static readonly int[] NoticedByLevel = new int[3];

    // Pure admission seam plus a narrow end-to-end test hook. The production path always uses Admits;
    // tests can force refusal without racing the writer to fill a 65,536-line queue.
    internal static Func<LogLevel, int, bool>? AdmitOverrideForTest { get; set; }

    // Size-based rotation. With the wire dump on, this log grows by megabytes per player-hour — fine on a
    // dev box with a big disk, an availability bug on a small VPS where a full filesystem takes the SQLite
    // database down with it. At the limit the current file is renamed to <name>.1 (replacing any previous
    // .1) and a fresh one opened, so disk use is bounded at ~2x the limit. Env-tunable, per-process default
    // through Configure (64MB game, 32MB login) — writer thread reads it, Configure writes it before the
    // process has a file at all.
    private static long _maxBytes = UnconfiguredMaxBytes;

    private static Thread _writer = null!;   // replaced only by RestartWriterForTest; see Shutdown

    static Log() => StartWriter(_queue);

    private static void StartWriter(BlockingCollection<string> queue)
    {
        _writer = new Thread(() => WriterLoop(queue))
        {
            IsBackground = true,   // must never keep the process alive; Shutdown() is what flushes the tail
            Name = "log-writer",
        };
        _writer.Start();
    }

    // ---- per-process configuration -----------------------------------------------------------------------

    private static int _configured;   // Interlocked: Configure is a startup declaration, not a setting

    /// <summary>The rotation limit a process that never called <see cref="Configure"/> gets: the game
    /// server's historical 64MB. A forgotten <see cref="Configure"/> should not shrink somebody's log file
    /// behind their back, so the unconfigured default is the more permissive of the two.</summary>
    private const long UnconfiguredMaxBytes = 64L * 1024 * 1024;

    /// <summary>Declare this process's logging defaults. Called ONCE, at the top of the entry point, before
    /// <see cref="AttachFile"/> — the rotation limit has to be known before there is a file to rotate.
    /// <para><paramref name="wireDefault"/> and <paramref name="maxBytesDefault"/> are DEFAULTS: the
    /// environment still wins (<c>P1998_LOG_WIRE</c>, <c>P1998_LOG_MAX_BYTES</c>). They are the one thing the
    /// two processes disagree about, and making the entry point say so is what let the two copies of this
    /// class become one — the env var now has a single meaning (see <see cref="ParseWire"/>) with a
    /// per-process default, instead of meaning <c>== "1"</c> in one process and <c>!= "0"</c> in the
    /// other.</para>
    /// <para>A second call throws rather than re-reading the environment: every <see cref="WireEnabled"/>
    /// call site has already branched on the first answer, so a late change would make the log disagree with
    /// itself about whether it is dumping passwords.</para></summary>
    /// <exception cref="InvalidOperationException">Configure has already run in this process.</exception>
    public static void Configure(bool wireDefault, long maxBytesDefault)
    {
        if (Interlocked.Exchange(ref _configured, 1) != 0)
            throw new InvalidOperationException(
                "Log.Configure has already run in this process; the wire-dump default is a startup " +
                "declaration, not a setting (call sites have already branched on Log.WireEnabled).");

        _wireEnabled = ParseWire(Environment.GetEnvironmentVariable("P1998_LOG_WIRE"), wireDefault, out var bad);
        _maxBytes = ParseMaxBytes(Environment.GetEnvironmentVariable("P1998_LOG_MAX_BYTES"), maxBytesDefault);
        // Held rather than logged here: Configure runs before AttachFile, so a line written now would reach
        // the console only, and an operator who mistyped the variable has to be able to find it in logs/
        // afterwards. AttachFile enqueues it straight after the open marker.
        if (bad is not null) _pendingWarning = bad;
    }

    private static string? _pendingWarning;

    /// <summary>The one meaning of <c>P1998_LOG_WIRE</c>: <c>"0"</c> off, <c>"1"</c> on, unset (or empty)
    /// means the process's own default, and ANY other value is a mistake — it takes the default and warns,
    /// rather than being read as a truthy string. That last row is the reason this is a method: the old
    /// login-side test was <c>== "1"</c> and the old game-side test was <c>!= "0"</c>, so
    /// <c>P1998_LOG_WIRE=true</c> silently turned the dump OFF in one process and ON in the other.</summary>
    /// <param name="raw">The environment variable's value, or null when it is unset.</param>
    /// <param name="processDefault">What this process wants when the variable says nothing.</param>
    /// <param name="warning">A startup warning when <paramref name="raw"/> is neither "0" nor "1", else null.</param>
    internal static bool ParseWire(string? raw, bool processDefault, out string? warning)
    {
        warning = null;
        if (string.IsNullOrEmpty(raw)) return processDefault;
        if (raw == "0") return false;
        if (raw == "1") return true;
        warning = $"P1998_LOG_WIRE='{raw}' is not 0 or 1 — ignored; wire dump stays " +
                  (processDefault ? "ON" : "off");
        return processDefault;
    }

    /// <summary>Rotation limit from <c>P1998_LOG_MAX_BYTES</c>, or the process default. Unparseable and
    /// non-positive values fall back silently, exactly as both copies of this class already did.</summary>
    internal static long ParseMaxBytes(string? raw, long processDefault) =>
        long.TryParse(raw, out var mb) && mb > 0 ? mb : processDefault;

    internal static long MaxBytesForTest() => Volatile.Read(ref _maxBytes);

    /// <summary>Tee every log line into a persistent file (logs/server.log). The console window vanishes with
    /// the process — a crash trace printed there is unrecoverable (learned the hard way debugging the nmail
    /// send "crash" whose console output was lost).</summary>
    public static void AttachFile(string path)
    {
        // Create the directory rather than assume it. logs/ is gitignored and outside both the content set
        // and the state dir, so nothing else brings it into existence on a fresh host — and OpenFile
        // swallows its exception, meaning a missing directory would degrade silently to console-only
        // logging. That is precisely the failure this file exists to prevent.
        // EXEMPT (the logger cannot log through itself): if this fails, OpenFile fails on the same path a
        // moment later and prints the reason to the console, so the failure IS reported — once, by the code
        // that owns the file handle.
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); } catch { /* OpenFile reports */ }
        // Hand the path to the writer thread as a control line rather than opening here: the file handle is
        // owned by that thread alone, so there is no lock anywhere on the caller's side.
        _path = path;
        Enqueue(OpenMarker);
        // Configure's startup warning, if it had one: enqueued AFTER the open marker so it lands in the file
        // as well as on the console. Ordering is guaranteed — one FIFO queue, one writer thread.
        var bad = Interlocked.Exchange(ref _pendingWarning, null);
        if (bad is not null) Warn(bad);
    }

    private const string OpenMarker = "open";   // control line; never appears in a real message

    public static void Info(string msg)
    {
        // Millisecond resolution: whole-second stamps can't tell a client that repeats a held key every
        // ~30ms from one that repeats every ~300ms, which is exactly the question any "does it feel like
        // the real game" pacing bug turns into (cast spam, swing rate, walk rate). Formatting happens on the
        // CALLING thread so the timestamp is the moment of the event, not the moment it got written.
        Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] {msg}", LogLevel.Info);
    }

    // ---- severity ---------------------------------------------------------------------------------------
    // Three levels, told apart by a prefix rather than a column so the file stays greppable the way it always
    // has been: `!!` was already the hand-written marker for "something is wrong" in ~60 Info lines, so Warn
    // formalizes exactly that, and Error adds a third bang. `grep '!!!'` is every exception the server caught.
    //
    // The Exception overloads are the whole reason the levels exist. Until they did, every catch in the
    // process wrote `e.Message` — one line, no type, no stack — and a NullReferenceException in a 955-line
    // tick read as "!! world tick error: Object reference not set to an instance of an object." with nothing
    // to say which of a hundred call sites. Exception.ToString() carries the type, the message, the stack and
    // any inner exception; continuation lines are indented so a multi-line entry reads as one event and a
    // timestamp-anchored grep still finds its first line.

    /// <summary>Something is wrong but the server handled it — a refused reload, a slow client, a malformed
    /// packet. Recoverable, worth a look, not a bug in this process.</summary>
    public static void Warn(string msg) =>
        Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] !! {msg}", LogLevel.Warn);

    public static void Warn(string msg, Exception e) =>
        Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] !! {msg}{Detail(e)}", LogLevel.Warn);

    /// <summary>A caught exception that should not have happened — a handler threw, a flush failed, a thread
    /// loop's body raised. Always carries the full exception; there is deliberately no string-only overload
    /// that would let a call site drop the stack again.</summary>
    public static void Error(string msg, Exception e) =>
        Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] !!! {msg}{Detail(e)}", LogLevel.Error);

    private static string Detail(Exception e) =>
        "\n      " + e.ToString().Replace("\n", "\n      ");

    internal static bool Admits(LogLevel level, int queued) => level switch
    {
        LogLevel.Info => queued < QueueCapacity - ReservedLines,
        LogLevel.Warn or LogLevel.Error => queued < QueueCapacity,
        _ => false,
    };

    // Length of the stamp every entry point above writes, "[HH:mm:ss.fff] " — where the message text, and so
    // any hand-written marker, begins. Internal so a test can pin it against the real format string.
    internal const int StampLength = 15;

    /// <summary>The level a line is admitted at: the higher of the entry point it came through and the level
    /// its hand-written marker claims. The class doc above already states the rule — "<c>!!</c> was already
    /// the hand-written marker for 'something is wrong' in ~60 Info lines, so Warn formalizes exactly that" —
    /// and 25 diagnostic sites in <c>Server/</c> still write that marker by hand through <see cref="Info"/>
    /// (the fatal handler and the unobserved-task handler in Program.cs, SLOW TICK, the outbound-queue-full
    /// line, and 21 more). Those are precisely the records that fire when the backlog is deepest, so reading
    /// the marker is what keeps the reserve theirs too; classifying by entry point alone would refuse them
    /// 4,096 lines earlier than before the reserve existed. Routing those sites onto <see cref="Warn(string)"/>
    /// and <see cref="Error(string, Exception)"/> is a separate change; this reads what they already write.
    /// <para>The trailing space is part of the marker, so <c>!!!</c> is never read as <c>!!</c> and an
    /// unspaced run of bangs is not a marker at all. The level can only rise: <see cref="Detail"/>'s indented
    /// continuation lines carry no marker and must not demote an Error.</para></summary>
    internal static LogLevel LevelOf(string line, LogLevel entryPoint)
    {
        ReadOnlySpan<char> text = line.Length > StampLength ? line.AsSpan(StampLength) : default;
        LogLevel marker =
            text.StartsWith("!!! ") ? LogLevel.Error :
            text.StartsWith("!! ") ? LogLevel.Warn :
            LogLevel.Info;
        return marker > entryPoint ? marker : entryPoint;
    }

    /// <summary>Enqueue one formatted line. The level it is admitted at is <see cref="LevelOf"/>'s, not the
    /// entry point's, because the class doc's marker rule means a hand-written <c>!!</c>/<c>!!!</c> line is a
    /// warning or an error whichever method wrote it.</summary>
    private static void Enqueue(string line, LogLevel entryPoint = LogLevel.Info)
    {
        // The AttachFile marker is a control line, not a log record: it carries the file path to the writer
        // thread, which is the only thread allowed to open the handle (Append matches it by reference).
        // Refusing it would spend the whole process running console-only with _file null, silently and
        // permanently, to save one queue slot — so it skips admission entirely. TryAdd's hard capacity still
        // applies; losing it there is counted as an Error because the loss is permanent, not a dropped Info.
        // Past Shutdown there is no sink left to reach and TryAdd would THROW on the completed collection.
        // A late line is dropped, silently and without a counter: the queue it would be counted against has
        // already been drained and the notice printed. See _closed.
        if (_closed) return;
        var queue = _queue;

        if (ReferenceEquals(line, OpenMarker))
        {
            if (!TryAdd(queue, line)) Interlocked.Increment(ref DroppedByLevel[(int)LogLevel.Error]);
            return;
        }

        LogLevel level = LevelOf(line, entryPoint);
        int queued = queue.Count;
        var admit = AdmitOverrideForTest;
        // Count then TryAdd is deliberately not atomic: an Info line may land on either side of the reserve
        // at the boundary, while TryAdd still enforces the hard capacity. Even Error never waits, because a
        // stuck writer must never block the world tick or a packet handler through the logger.
        if (!(admit?.Invoke(level, queued) ?? Admits(level, queued)) || !TryAdd(queue, line))
            Interlocked.Increment(ref DroppedByLevel[(int)level]);
    }

    /// <summary>TryAdd, treating "the queue closed under me" as a refusal rather than an exception. The
    /// _closed check in <see cref="Enqueue"/> covers the ordinary case; this covers the race, where a session
    /// thread read _closed as false a moment before Shutdown completed the collection. Logging must never be
    /// the thing that throws on the way out of the process.</summary>
    private static bool TryAdd(BlockingCollection<string> queue, string line)
    {
        try { return queue.TryAdd(line); }
        catch (InvalidOperationException) { return false; }   // CompleteAdding raced us
    }

    /// <summary>How many refusals at this level the next overflow notice has to report: everything counted
    /// since the previous notice. WRITER THREAD ONLY.
    /// <para>This is a high-water mark rather than the exchange-to-zero it replaces, and that is the whole
    /// point: the counter a test reads is never emptied, so a notice landing between a refusal and a test's
    /// read cannot change what the test sees. The notice itself says exactly what it said before — each one
    /// reports the refusals since the last one.</para>
    /// <para>The subtraction is deliberately unchecked, so it stays correct if the counter ever wraps (a
    /// console stalled long enough to refuse two billion records). A negative delta means only one thing —
    /// <see cref="ResetDroppedCountsForTest"/> moved the counter back under the writer — and resyncing to the
    /// current value is the right answer there: no production caller resets.</para></summary>
    private static int TakeDropped(LogLevel level)
    {
        int total = Volatile.Read(ref DroppedByLevel[(int)level]);
        int taken = unchecked(total - NoticedByLevel[(int)level]);
        NoticedByLevel[(int)level] = total;
        return taken > 0 ? taken : 0;
    }

    internal static string FormatOverflowNotice(int info, int warn, int error)
    {
        if (info == 0 && warn == 0 && error == 0) return "";
        return FormattableString.Invariant(
            $"!! log queue overflowed — dropped {info:N0} info, {warn:N0} warn, {error:N0} error");
    }

    /// <summary>Everything refused at each level since the last <see cref="ResetDroppedCountsForTest"/>,
    /// whether or not an overflow notice has already reported it. Because <see cref="TakeDropped"/> only
    /// reads these counters, this answer does not depend on when the writer thread last drained the
    /// queue.</summary>
    internal static (int Info, int Warn, int Error) DroppedCountsForTest() =>
        (Volatile.Read(ref DroppedByLevel[(int)LogLevel.Info]),
         Volatile.Read(ref DroppedByLevel[(int)LogLevel.Warn]),
         Volatile.Read(ref DroppedByLevel[(int)LogLevel.Error]));

    /// <summary>Put the drop counters back to zero. TEST ONLY, and the one thing that can move a counter
    /// backwards: the writer resyncs its own high-water mark the next time it looks, so the reset costs at
    /// most one notice for records refused right beside it (see <see cref="TakeDropped"/>).</summary>
    internal static void ResetDroppedCountsForTest()
    {
        Interlocked.Exchange(ref DroppedByLevel[(int)LogLevel.Info], 0);
        Interlocked.Exchange(ref DroppedByLevel[(int)LogLevel.Warn], 0);
        Interlocked.Exchange(ref DroppedByLevel[(int)LogLevel.Error], 0);
    }

    private static int _shutdownOnce;
    private static int _shutdownRuns;   // TEST SEAM only; see ShutdownRunsForTest.

    /// <summary>Flush the tail and stop the writer. Called from the shutdown hooks so a clean stop doesn't
    /// lose the last lines (a hard kill still can — that is what the file's AutoFlush-equivalent idle flush
    /// below bounds). Waits a bounded time: shutdown must not hang on a stuck console.
    /// <para>Once per process. Every exit a deployed process takes can reach this more than once — the
    /// Ctrl+C handler flushes and then calls <c>Environment.Exit(0)</c>, which re-raises ProcessExit; the
    /// game server arrives from <c>TkListener.Shutdown</c> under the same two hooks — and both call sites
    /// have always been commented as if this guard existed. It does now, so the second call returns here
    /// rather than throwing its way through <c>CompleteAdding</c> on a completed collection.</para></summary>
    public static void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdownOnce, 1) != 0) return;
        Interlocked.Increment(ref _shutdownRuns);
        try
        {
            _closed = true;   // before CompleteAdding: a line still in flight is dropped, not thrown at
            _queue.CompleteAdding();
            _writer.Join(TimeSpan.FromSeconds(2));
        }
        // EXEMPT (the logger cannot log through itself, and this is the log shutting down): the once-guard
        // above means the documented double-call from the exit hooks now returns before it gets here, so
        // nothing routine lands in this catch at all. It stays because the way out of a process is the one
        // place a throw from the logger is unrecoverable — what is left is a stop racing the test-only
        // writer restart, or a Join against a thread the runtime is already tearing down.
        catch { /* already shutting down */ }
    }

    /// <summary>How many <see cref="Shutdown"/> calls have got PAST the once-guard and touched the queue.
    /// TEST ONLY, and the only observable difference between a guarded second call and an unguarded one:
    /// both return, and both leave the writer stopped.</summary>
    internal static int ShutdownRunsForTest() => Volatile.Read(ref _shutdownRuns);

    private static int _exitHookOnce;
    private static PosixSignalRegistration? _sigterm;   // held for the process lifetime; disposing unhooks it

    /// <summary>Flush the queue on the way out, for a process whose shutdown has nothing else to do.
    /// <para>The game server does this inside <c>TkListener.Shutdown</c>, where the log flush is the last step
    /// after saving every connected player. The login server holds no state worth saving, so this IS its whole
    /// shutdown — and without it, moving it onto a queue logger would have traded its console freezes for a
    /// LOST TAIL: the last lines before a stop or a crash, which are the ones anybody reads. All three exits a
    /// deployed process actually takes are covered — Ctrl+C in the console window, SIGTERM from
    /// <c>systemctl restart</c>/<c>docker stop</c>, and ProcessExit for a plain return from Main — and the
    /// Interlocked guard means running through more than one of them flushes exactly once. A fatal exception
    /// does NOT reach ProcessExit (the runtime aborts), so that path calls <see cref="Shutdown"/> itself, in
    /// the handler that writes the trace.</para>
    /// <para>Each handler STAMPS the log before it flushes. Without that line the login server's exit left no
    /// trace at all: its log simply stopped, and a reader could not tell a clean <c>systemctl restart</c>
    /// from the process dying — which is the same confusion the game server's log already resolves (see the
    /// note in <c>Server/Program.cs</c>: a stray Ctrl+C in the console window is a CLEAN exit that reads like
    /// a crash). The wording is <c>TkListener.Shutdown</c>'s, minus its "flushing connected players" — this
    /// process holds no player state, which is why this is its whole shutdown.</para></summary>
    public static void FlushOnExit()
    {
        if (Interlocked.Exchange(ref _exitHookOnce, 1) != 0) return;

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;   // exit ourselves once the flush is done, not mid-write
            Info("=== shutdown signal (Ctrl+C) ===");
            Shutdown();
            Environment.Exit(0);   // re-raises ProcessExit below; Shutdown's own guard makes that a no-op
        };
        // The stamp goes through the queue like everything else, so on the Ctrl+C path above it is already
        // written and the queue already closed by the time this fires — the second stamp is dropped with the
        // second Shutdown, and the log carries exactly one shutdown line naming the signal that got there first.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { Info("=== shutdown signal (ProcessExit) ==="); Shutdown(); };
        _sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            Info("=== shutdown signal (SIGTERM) ===");
            Shutdown();
            ctx.Cancel = false;   // let the runtime carry on terminating; we only wanted the flush first
        });
    }

    /// <summary>Whether the writer thread is still running. TEST ONLY, and the load-bearing half of the
    /// Shutdown fact: the thread only leaves <c>GetConsumingEnumerable</c> once the queue is BOTH empty and
    /// completed, and its finally block flushes on the way out — so "not alive" is the drain and the flush,
    /// where reading the file alone cannot tell a flushed tail from a writer that simply kept up.</summary>
    internal static bool WriterRunningForTest() => _writer.IsAlive;

    /// <summary>Undo <see cref="Shutdown"/> with a fresh queue and a fresh writer thread. TEST ONLY: the
    /// Shutdown fact has to call the real Shutdown, and Shutdown is one-way (a completed BlockingCollection
    /// stays completed), so without this one fact would leave every later test in the process logging into a
    /// closed queue. Production never calls it — a process shuts its log down once, on its way out.
    /// <para>It has to reset the once-guard along with the queue. The guard is what makes a second Shutdown a
    /// no-op, and a no-op Shutdown on a FRESH queue would leave the writer running and the tail unflushed —
    /// so without this line every Shutdown fact after the first would silently stop testing anything.</para></summary>
    internal static void RestartWriterForTest()
    {
        _queue = NewQueue();
        _closed = false;
        Interlocked.Exchange(ref _shutdownOnce, 0);
        StartWriter(_queue);
    }

    // ---- writer thread: the ONLY place that touches the console or the file --------------------------

    private static void WriterLoop(BlockingCollection<string> queue)
    {
        var batch = new StringBuilder(BatchLines * 96);
        try
        {
            foreach (var first in queue.GetConsumingEnumerable())
            {
                batch.Clear();
                int n = Append(batch, first);
                // Coalesce whatever else is already queued into this one write. At wire-dump volume this
                // turns thousands of console calls per second into a handful.
                while (n < BatchLines && queue.TryTake(out var more)) n += Append(batch, more);

                if (batch.Length > 0) Emit(batch.ToString());

                // Idle flush: once the backlog is drained, get the tail onto disk. This replaces the old
                // per-line AutoFlush — same durability in practice (we are almost always idle between
                // events) at a tiny fraction of the syscalls.
                if (queue.Count == 0) TryFlush();

                string notice = FormatOverflowNotice(
                    TakeDropped(LogLevel.Info), TakeDropped(LogLevel.Warn), TakeDropped(LogLevel.Error));
                if (notice.Length > 0) Emit($"[{DateTime.Now:HH:mm:ss.fff}] {notice}");
            }
        }
        catch (Exception e)
        {
            // The writer thread dying silently would take the log with it and leave no trace of why. Full
            // exception, straight to the console, because the queue this would normally go through is the
            // thing that just stopped being drained. EXEMPT (nested): if the console write ALSO fails there
            // is no sink left in the process to report it to.
            try { Console.WriteLine($"!! log writer stopped: {e}"); } catch { }
        }
        finally { TryFlush(); }
    }

    /// <summary>Add one queued line to the pending batch, handling the AttachFile control line. Returns how
    /// many real lines were added (0 for a control line).</summary>
    private static int Append(StringBuilder batch, string line)
    {
        if (ReferenceEquals(line, OpenMarker) || line == OpenMarker)
        {
            if (batch.Length > 0) { Emit(batch.ToString()); batch.Clear(); }
            OpenFile(note: "opened");
            return 0;
        }
        batch.Append(line).Append('\n');
        return 1;
    }

    private static void Emit(string text)
    {
        // EXEMPT (the logger cannot log through itself): both halves of the sink are the thing that failed.
        // Reporting a console failure would need the console; reporting a file failure would need the file.
        // The design is deliberately half-alive — one sink dying must not take the other with it, and
        // neither may kill the writer thread, which would take the whole log down for a full disk.
        try { Console.Out.Write(text); } catch { /* console gone (detached/redirected to a closed pipe) */ }
        if (_file is null) return;
        try
        {
            _file.Write(text);
            _written += text.Length;
            if (_written >= _maxBytes) Rotate();
        }
        // EXEMPT: see the head of this method — the file sink is what failed.
        catch { /* disk full / handle lost — keep the console half alive rather than kill the writer */ }
    }

    private static void TryFlush()
    {
        // EXEMPT: same sink-is-the-failure case as Emit, whose comment explains it.
        try { _file?.Flush(); } catch { /* see Emit */ }
    }

    // Writer thread only.
    private static void OpenFile(string note)
    {
        try
        {
            var fi = new FileInfo(_path);
            _written = fi.Exists ? fi.Length : 0;
            // AutoFlush deliberately OFF — WriterLoop flushes whenever the queue drains, which is both
            // cheaper and, since we are idle almost all the time, just as durable.
            _file = new StreamWriter(_path, append: true) { AutoFlush = false };
            _file.Write($"===== log {note} {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====\n");
            _file.Flush();
        }
        catch (Exception e)
        {
            _file = null;
            // EXEMPT (nested): the file half is what failed, so this reports it to the console; if that
            // fails too, nothing is left to report to.
            try { Console.WriteLine($"!! log file unavailable ({_path}): {e}"); } catch { }
        }
    }

    // Writer thread only. Never throws: losing rotation must not take the process (or the log) down. This
    // is slow — a 64MB delete + move, often with a virus scanner in the path — which is exactly why it now
    // runs here instead of under a lock every other thread needs.
    private static void Rotate()
    {
        try
        {
            _file?.Flush();
            _file?.Dispose();
            _file = null;
            var prev = _path + ".1";
            if (File.Exists(prev)) File.Delete(prev);
            File.Move(_path, prev);
        }
        // EXEMPT (nested), as above: rotation is file work, reported to the console because the file is what
        // broke. OpenFile below then re-opens (or reports) whatever state that left.
        catch (Exception e) { try { Console.WriteLine($"!! log rotate failed: {e}"); } catch { } }
        OpenFile(note: "rotated");
    }

    /// <summary>Whether to emit the per-packet WIRE dump (raw read, opcode line, decrypted body). It no longer
    /// blocks either process (see the class doc), but it still costs a hex-string build per packet and floods
    /// the file, so <c>P1998_LOG_WIRE=0</c> remains the right setting for a live server (our own deployment
    /// sets it in the unit file, in Project1998-infra). Guard call sites with this flag rather than letting
    /// <see cref="Hex"/> run and throwing the string away.
    ///
    /// <para>The DEFAULT is the calling process's, declared through <see cref="Configure"/>: ON for the game
    /// server — it's the backbone of the protocol RE work — and OFF for the login server. That asymmetry is
    /// deliberate, not an oversight. The login channel's packets carry the player's PASSWORD in the clear
    /// (0x02 name-check and 0x03 login are both <c>nameLen name pwLen pw</c>), and 4.95's cipher is a fixed,
    /// published XOR, so the "raw" dump is every bit as readable as the decrypted one. Leaving this on writes
    /// every player's password into logs/login.log and the systemd journal in plaintext, where log shipping,
    /// backups and a support screenshot all quietly spread it further.</para>
    ///
    /// <para>Set P1998_LOG_WIRE=1 to turn it back on for protocol work on a machine with no real
    /// accounts.</para>
    ///
    /// <para>A process that never called <see cref="Configure"/> gets OFF: an entry point that declared no
    /// policy is not one whose log may carry passwords.</para></summary>
    public static bool WireEnabled => _wireEnabled;

    private static bool _wireEnabled;   // Configure's answer; see the doc above for the unconfigured case

    public static string Hex(byte[] b)
    {
        var parts = new string[b.Length];
        for (int i = 0; i < b.Length; i++) parts[i] = b[i].ToString("x2");
        var ascii = new char[b.Length];
        for (int i = 0; i < b.Length; i++) ascii[i] = (b[i] >= 32 && b[i] < 127) ? (char)b[i] : '.';
        return $"{string.Join(" ", parts)}    |{new string(ascii)}|";
    }
}
