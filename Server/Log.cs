using System.Collections.Concurrent;
using System.Text;

namespace Server;

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
/// </summary>
public static class Log
{
    // Bounded on purpose: see the class doc. At ~120 bytes/line this caps the backlog at roughly 8MB, which
    // is minutes of a stuck console at wire-dump volume — far longer than any real stall — while still
    // guaranteeing a hung terminal can never OOM the server.
    private const int QueueCapacity = 65_536;
    // How many queued lines one wake-up may coalesce into a single console + file write. Batching is what
    // makes the wire dump affordable: the cost of a console write is dominated by the call, not the bytes.
    private const int BatchLines = 512;

    private static readonly BlockingCollection<string> Queue =
        new(new ConcurrentQueue<string>(), QueueCapacity);

    private static StreamWriter? _file;    // writer thread only, after AttachFile hands it over
    private static string _path = "";
    private static long _written;
    private static int _dropped;           // Interlocked: lines lost while the queue was full

    // Size-based rotation. With the wire dump on, this log grows by megabytes per player-hour — fine on a
    // dev box with a big disk, an availability bug on a small VPS where a full filesystem takes the SQLite
    // database down with it. At MaxBytes the current file is renamed to <name>.1 (replacing any previous
    // .1) and a fresh one opened, so disk use is bounded at ~2x MaxBytes. Env-tunable.
    private static readonly long MaxBytes =
        long.TryParse(Environment.GetEnvironmentVariable("P1998_LOG_MAX_BYTES"), out var mb) && mb > 0 ? mb : 64L * 1024 * 1024;

    private static readonly Thread Writer;

    static Log()
    {
        Writer = new Thread(WriterLoop)
        {
            IsBackground = true,   // must never keep the process alive; Shutdown() is what flushes the tail
            Name = "log-writer",
        };
        Writer.Start();
    }

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
    }

    private const string OpenMarker = "open";   // control line; never appears in a real message

    public static void Info(string msg)
    {
        // Millisecond resolution: whole-second stamps can't tell a client that repeats a held key every
        // ~30ms from one that repeats every ~300ms, which is exactly the question any "does it feel like
        // the real game" pacing bug turns into (cast spam, swing rate, walk rate). Formatting happens on the
        // CALLING thread so the timestamp is the moment of the event, not the moment it got written.
        Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
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
    public static void Warn(string msg) => Info("!! " + msg);

    public static void Warn(string msg, Exception e) => Info("!! " + msg + Detail(e));

    /// <summary>A caught exception that should not have happened — a handler threw, a flush failed, a thread
    /// loop's body raised. Always carries the full exception; there is deliberately no string-only overload
    /// that would let a call site drop the stack again.</summary>
    public static void Error(string msg, Exception e) => Info("!!! " + msg + Detail(e));

    private static string Detail(Exception e) =>
        "\n      " + e.ToString().Replace("\n", "\n      ");

    private static void Enqueue(string line)
    {
        // TryAdd with no wait: a full queue means the writer is stuck (see the class doc) and the ONE thing
        // we must not do is block the caller — that caller is the world tick or a packet handler.
        if (!Queue.TryAdd(line)) Interlocked.Increment(ref _dropped);
    }

    /// <summary>Flush the tail and stop the writer. Called from the shutdown hooks so a clean stop doesn't
    /// lose the last lines (a hard kill still can — that is what the file's AutoFlush-equivalent idle flush
    /// below bounds). Waits a bounded time: shutdown must not hang on a stuck console.</summary>
    public static void Shutdown()
    {
        try
        {
            Queue.CompleteAdding();
            Writer.Join(TimeSpan.FromSeconds(2));
        }
        // EXEMPT (the logger cannot log through itself, and this is the log shutting down): the only thing
        // that lands here is a second Shutdown racing the first on an already-completed queue, which is the
        // documented double-call from the two exit hooks. There is no failure to hide.
        catch { /* already shutting down */ }
    }

    // ---- writer thread: the ONLY place that touches the console or the file --------------------------

    private static void WriterLoop()
    {
        var batch = new StringBuilder(BatchLines * 96);
        try
        {
            foreach (var first in Queue.GetConsumingEnumerable())
            {
                batch.Clear();
                int n = Append(batch, first);
                // Coalesce whatever else is already queued into this one write. At wire-dump volume this
                // turns thousands of console calls per second into a handful.
                while (n < BatchLines && Queue.TryTake(out var more)) n += Append(batch, more);

                if (batch.Length > 0) Emit(batch.ToString());

                // Idle flush: once the backlog is drained, get the tail onto disk. This replaces the old
                // per-line AutoFlush — same durability in practice (we are almost always idle between
                // events) at a tiny fraction of the syscalls.
                if (Queue.Count == 0) TryFlush();

                int lost = Interlocked.Exchange(ref _dropped, 0);
                if (lost > 0) Emit($"[{DateTime.Now:HH:mm:ss.fff}] !! log queue overflowed — {lost} line(s) dropped");
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
            if (_written >= MaxBytes) Rotate();
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

    /// <summary>Whether to emit the per-packet WIRE dump (raw read, opcode line, decrypted body). On by
    /// default — it's the backbone of the protocol RE work. It no longer blocks the game (see the class
    /// doc), but it still costs a hex-string build per packet and floods the file, so
    /// <c>P1998_LOG_WIRE=0</c> remains the right setting for a live server (our own deployment sets it in
    /// the unit file, in Project1998-infra). Guard call sites with this flag rather than letting Log.Hex
    /// run and throwing the string away.</summary>
    public static readonly bool WireEnabled = Environment.GetEnvironmentVariable("P1998_LOG_WIRE") != "0";

    public static string Hex(byte[] b)
    {
        var parts = new string[b.Length];
        for (int i = 0; i < b.Length; i++) parts[i] = b[i].ToString("x2");
        var ascii = new char[b.Length];
        for (int i = 0; i < b.Length; i++) ascii[i] = (b[i] >= 32 && b[i] < 127) ? (char)b[i] : '.';
        return $"{string.Join(" ", parts)}    |{new string(ascii)}|";
    }
}
