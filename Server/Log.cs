namespace Server;

public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _file;
    private static string _path = "";
    private static long _written;

    // Size-based rotation. With the wire dump on, this log grows by megabytes per player-hour — fine on a
    // dev box with a big disk, an availability bug on a small VPS where a full filesystem takes the SQLite
    // database down with it. At MaxBytes the current file is renamed to <name>.1 (replacing any previous
    // .1) and a fresh one opened, so disk use is bounded at ~2x MaxBytes. Env-tunable.
    private static readonly long MaxBytes =
        long.TryParse(Environment.GetEnvironmentVariable("NEXUS_LOG_MAX_BYTES"), out var mb) && mb > 0 ? mb : 64L * 1024 * 1024;

    // Tee every log line into a persistent file (data/server.log). The console window vanishes with the
    // process — a crash trace printed there is unrecoverable (learned the hard way debugging the nmail
    // send "crash" whose console output was lost). AutoFlush so the tail survives a hard death.
    public static void AttachFile(string path)
    {
        lock (Gate)
        {
            _path = path;
            OpenLocked(note: "opened");
        }
    }

    public static void Info(string msg)
    {
        lock (Gate)
        {
            // Millisecond resolution: whole-second stamps can't tell a client that repeats a held key every
            // ~30ms from one that repeats every ~300ms, which is exactly the question any "does it feel like
            // the real game" pacing bug turns into (cast spam, swing rate, walk rate).
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
            Console.WriteLine(line);
            if (_file is null) return;
            _file.WriteLine(line);
            _written += line.Length + 2;
            if (_written >= MaxBytes) RotateLocked();
        }
    }

    // Caller holds Gate.
    private static void OpenLocked(string note)
    {
        try
        {
            var fi = new FileInfo(_path);
            _written = fi.Exists ? fi.Length : 0;
            _file = new StreamWriter(_path, append: true) { AutoFlush = true };
            _file.WriteLine($"===== log {note} {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        }
        catch (Exception e)
        {
            _file = null;
            Console.WriteLine($"!! log file unavailable ({_path}): {e.Message}");
        }
    }

    // Caller holds Gate. Never throws: losing rotation must not take the process (or the log) down.
    private static void RotateLocked()
    {
        try
        {
            _file?.Dispose();
            _file = null;
            var prev = _path + ".1";
            if (File.Exists(prev)) File.Delete(prev);
            File.Move(_path, prev);
        }
        catch (Exception e) { Console.WriteLine($"!! log rotate failed: {e.Message}"); }
        OpenLocked(note: "rotated");
    }

    /// <summary>Whether to emit the per-packet WIRE dump (raw read, opcode line, decrypted body). On by
    /// default — it's the backbone of the protocol RE work — but every logged packet costs a hex-string
    /// build plus a synchronous Console.WriteLine and an AutoFlush file write, all under <see cref="Gate"/>,
    /// ON the packet-handling path. That is dead time sitting BETWEEN the sends of two back-to-back packets,
    /// which is exactly where a "these should be simultaneous but sound flammed" complaint would come from.
    /// Set <c>NEXUS_LOG_WIRE=0</c> to silence it and find out. Guard call sites with this flag rather than
    /// letting Log.Hex run and throwing the string away.</summary>
    public static readonly bool WireEnabled = Environment.GetEnvironmentVariable("NEXUS_LOG_WIRE") != "0";

    public static string Hex(byte[] b)
    {
        var parts = new string[b.Length];
        for (int i = 0; i < b.Length; i++) parts[i] = b[i].ToString("x2");
        var ascii = new char[b.Length];
        for (int i = 0; i < b.Length; i++) ascii[i] = (b[i] >= 32 && b[i] < 127) ? (char)b[i] : '.';
        return $"{string.Join(" ", parts)}    |{new string(ascii)}|";
    }
}
