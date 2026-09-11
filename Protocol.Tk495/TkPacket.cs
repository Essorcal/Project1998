namespace Protocol.Tk495;

/// <summary>
/// NexusTK wire framing:  AA | length(u16 BE) | opcode | increment | body[...].
/// length counts opcode + increment + body. These clients have no game-packet trailer.
/// </summary>
public readonly struct TkPacket
{
    public byte Opcode { get; init; }
    public byte Increment { get; init; }
    public byte[] Body { get; init; }   // raw (still encrypted for login-style packets)

    /// <summary>The smallest value the u16 length field may carry: 2. The field counts opcode + increment +
    /// body (see the class doc and <c>docs/4.x/Protocol.md</c> §2, "Total bytes on the wire = 3 + length"), so
    /// the shortest legal frame is a body-less one — <c>AA 00 02 op inc</c>, five bytes — and both builders
    /// here write <c>len = 2 + body.Length</c>, which no producer can drive below 2.
    ///
    /// <para>A smaller value is not a short frame that more bytes could complete: it is arithmetic that
    /// cannot describe a frame at all. Before this constant existed, <c>3 + len</c> with <c>len</c> 0 or 1
    /// passed the "have I got all the bytes" check and then sliced a NEGATIVE body length, throwing
    /// <see cref="ArgumentOutOfRangeException"/> out of the read loop for any peer that sent five bytes
    /// starting <c>AA 00 00</c> or <c>AA 00 01</c>.</para></summary>
    public const int MinLength = 2;

    /// <summary>What a buffer holds at its head, for a caller that has to tell "wait for more bytes" apart
    /// from "these bytes can never be a frame".</summary>
    public enum FrameStatus
    {
        /// <summary>A whole frame was parsed; <c>consumed</c> says how many bytes it took.</summary>
        Frame,

        /// <summary>Not a frame YET: fewer than three bytes, or a length field whose bytes have not all
        /// arrived. More bytes may complete it, so the caller waits. A head byte that is not <c>0xAA</c> also
        /// lands here — that is not this parser's rule to make, and <c>FrameReader</c> owns it (and drops the
        /// connection for it) with a message of its own.</summary>
        NeedMore,

        /// <summary>A <c>0xAA</c> head whose length field is under <see cref="MinLength"/>. No number of
        /// further bytes can make this a frame; the caller must not wait on it.</summary>
        Malformed,
    }

    /// <summary>Parse one frame off the head of <paramref name="buf"/>. Never throws, for any input.</summary>
    /// <param name="buf">The connection buffer, from the head of a would-be frame.</param>
    /// <param name="pkt">The frame, when the result is <see cref="FrameStatus.Frame"/>.</param>
    /// <param name="consumed">Bytes the frame took, when the result is <see cref="FrameStatus.Frame"/>;
    /// otherwise 0.</param>
    /// <param name="length">The value of the u16 length field when the head byte is <c>0xAA</c> and at least
    /// three bytes are present; 0 otherwise. It is what a caller prints when it reports a malformed frame,
    /// so it comes back from the one place that reads the field rather than being re-derived at the log line.</param>
    public static FrameStatus Parse(ReadOnlySpan<byte> buf, out TkPacket pkt, out int consumed, out int length)
    {
        pkt = default;
        consumed = 0;
        length = 0;
        if (buf.Length < 3 || buf[0] != 0xAA) return FrameStatus.NeedMore;
        length = (buf[1] << 8) | buf[2];
        if (length < MinLength) return FrameStatus.Malformed;
        int total = 3 + length;
        if (buf.Length < total) return FrameStatus.NeedMore;
        pkt = new TkPacket
        {
            Opcode = buf[3],
            Increment = buf[4],
            Body = buf.Slice(5, total - 5).ToArray()
        };
        consumed = total;
        return FrameStatus.Frame;
    }

    /// <summary>The two-out form every caller outside the read loop uses: true only for a whole frame, with
    /// "malformed" and "need more bytes" both false, exactly as before. It no longer throws on a length field
    /// under <see cref="MinLength"/> — that input now returns false.</summary>
    public static bool TryParse(ReadOnlySpan<byte> buf, out TkPacket pkt, out int consumed) =>
        Parse(buf, out pkt, out consumed, out _) == FrameStatus.Frame;

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

    /// <summary>4.95/5.33 game framing uses the same NexonInc cipher as login, with no 7.x trailer.
    /// Proven by the 4.95 decrypt routine at 0x478680 and its key buffer at 0x50211c.</summary>
    public static byte[] BuildGame(byte opcode, byte inc, byte[] body) =>
        Build(opcode, inc, TkCrypt.Crypt(body, inc, TkCrypt.LoginKey));
}
