namespace Protocol.Tk495;

/// <summary>
/// NexusTK wire framing:  AA | length(u16 BE) | opcode | increment | body[...].
/// length counts opcode + increment + body. Game packets append 3 index bytes.
/// </summary>
public readonly struct TkPacket
{
    public byte Opcode { get; init; }
    public byte Increment { get; init; }
    public byte[] Body { get; init; }   // raw (still encrypted for login-style packets)

    public static bool TryParse(ReadOnlySpan<byte> buf, out TkPacket pkt, out int consumed)
    {
        pkt = default;
        consumed = 0;
        if (buf.Length < 5 || buf[0] != 0xAA) return false;
        int len = (buf[1] << 8) | buf[2];
        int total = 3 + len;
        if (buf.Length < total) return false;
        pkt = new TkPacket
        {
            Opcode = buf[3],
            Increment = buf[4],
            Body = buf.Slice(5, total - 5).ToArray()
        };
        consumed = total;
        return true;
    }

    /// <summary>Build a simple (login-style) packet. Body should already be encrypted if needed.</summary>
    public static byte[] Build(byte opcode, byte inc, ReadOnlySpan<byte> body)
    {
        int len = 2 + body.Length;
        var p = new byte[3 + len];
        p[0] = 0xAA;
        p[1] = (byte)(len >> 8);
        p[2] = (byte)(len & 0xFF);
        p[3] = opcode;
        p[4] = inc;
        body.CopyTo(p.AsSpan(5));
        return p;
    }
}
