namespace Server;

public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _file;

    // Tee every log line into a persistent file (data/server.log). The console window vanishes with the
    // process — a crash trace printed there is unrecoverable (learned the hard way debugging the nmail
    // send "crash" whose console output was lost). AutoFlush so the tail survives a hard death.
    public static void AttachFile(string path)
    {
        lock (Gate)
        {
            try
            {
                _file = new StreamWriter(path, append: true) { AutoFlush = true };
                _file.WriteLine($"===== log opened {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
            }
            catch (Exception e) { Console.WriteLine($"!! log file unavailable ({path}): {e.Message}"); }
        }
    }

    public static void Info(string msg)
    {
        lock (Gate)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            Console.WriteLine(line);
            _file?.WriteLine(line);
        }
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
