namespace LoginServer;

// Minimal logger, mirroring the game server's Server.Log format so both processes' output reads the same
// in a combined terminal. Kept local (not shared) so the login server has no dependency on the game
// project; Log is trivial enough that a small copy is cheaper than a shared-project coupling.
public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _file;
    private static string _path = "";
    private static long _written;

    private static readonly long MaxBytes =
        long.TryParse(Environment.GetEnvironmentVariable("P1998_LOG_MAX_BYTES"), out var mb) && mb > 0 ? mb : 32L * 1024 * 1024;

    /// <summary>
    /// Whether to dump raw/decrypted packet bytes.
    ///
    /// DEFAULT OFF here, unlike the game server — and that asymmetry is deliberate, not an oversight. The
    /// login channel's packets carry the player's PASSWORD in the clear (0x02 name-check and 0x03 login are
    /// both `nameLen name pwLen pw`), and 4.95's cipher is a fixed, published XOR, so the "raw" dump is
    /// every bit as readable as the decrypted one. Leaving this on writes every player's password into
    /// logs/login.log and the systemd journal in plaintext, where log shipping, backups and a support
    /// screenshot all quietly spread it further.
    ///
    /// Set P1998_LOG_WIRE=1 to turn it back on for protocol work on a machine with no real accounts.
    /// </summary>
    public static readonly bool WireEnabled = Environment.GetEnvironmentVariable("P1998_LOG_WIRE") == "1";

    /// <summary>Tee log lines into a file, with the same size-based rotation the game server uses (see
    /// Server/Log.cs): at MaxBytes the file becomes &lt;name&gt;.1 and a fresh one opens.</summary>
    public static void AttachFile(string path)
    {
        lock (Gate)
        {
            // Same reasoning as Server/Log.AttachFile: logs/ is gitignored and nothing else creates it, and
            // the open below reports-and-continues, so an absent directory would silently cost us the file.
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); } catch { /* OpenLocked reports */ }
            _path = path;
            OpenLocked("opened");
        }
    }

    public static void Info(string msg)
    {
        lock (Gate)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            Console.WriteLine(line);
            if (_file is null) return;
            _file.WriteLine(line);
            _written += line.Length + 2;
            if (_written >= MaxBytes) RotateLocked();
        }
    }

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
        OpenLocked("rotated");
    }

    public static string Hex(byte[] b)
    {
        var parts = new string[b.Length];
        for (int i = 0; i < b.Length; i++) parts[i] = b[i].ToString("x2");
        var ascii = new char[b.Length];
        for (int i = 0; i < b.Length; i++) ascii[i] = (b[i] >= 32 && b[i] < 127) ? (char)b[i] : '.';
        return $"{string.Join(" ", parts)}    |{new string(ascii)}|";
    }
}
