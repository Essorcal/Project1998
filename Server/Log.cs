namespace Server;

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string msg)
    {
        lock (Gate)
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
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
