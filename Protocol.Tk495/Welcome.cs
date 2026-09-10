namespace Protocol.Tk495;

/// <summary>
/// The plaintext greeting a login channel sends the moment a client connects. Both processes send it — the
/// login server on every connection, the game server on its login-port connections — and they built the same
/// bytes from two copies of the same builder until this became one.
///
/// <para>AA 00 13 7E 1B "CONNECTED SERVER\n" (plaintext welcome, as the 6.x reference sends on connect).</para>
/// </summary>
public static class Welcome
{
    /// <summary>The frame, built once per process. Shared by every connection: read it, never mutate it.
    /// </summary>
    public static readonly byte[] Bytes = Build();

    private static byte[] Build()
    {
        var head = new byte[] { 0xAA, 0x00, 0x13, 0x7E, 0x1B };
        var text = "CONNECTED SERVER\n"u8.ToArray();
        var all = new byte[head.Length + text.Length];
        head.CopyTo(all, 0);
        text.CopyTo(all, head.Length);
        return all;
    }
}
