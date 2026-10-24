using Shared;

namespace Protocol.Tk495;

/// <summary>
/// The <c>0x02</c> status frame: <c>0x02</c> under increment <c>0x02</c>, its body NexonInc-encrypted, the
/// plaintext <c>code len text 00</c>. It is the login channel's one server-&gt;client reply shape and the game
/// channel's login-style message box, and both processes build it here:
///
/// <list type="bullet">
/// <item><c>LoginSession.SendStatus</c> — every OK and refusal on the login channel (codes 0x00, 0x03, 0x05,
/// and 0x0F through <c>LoginSession.SendMessage</c>), ban notices included (<c>LoginAuth.BanMessageFor</c>)</item>
/// <item><c>Session.SendMessage</c> — the game channel's 0x0F box: ban and kick notices, refused arrivals and
/// re-logins, script <c>message</c> lines</item>
/// </list>
///
/// <para><b>The length is one byte.</b> Each sender used to write <c>(byte)text.Length</c> itself, unchecked,
/// so a text over 255 bytes wrapped: a 300-byte ban notice went out with a length byte of 44, and the other
/// 256 bytes and the terminator trailed after the 44 the length byte described. A ban reason is typed by a
/// GM and the notice adds its own prefix to it, so the total can pass 255. Now the text is cut to
/// <see cref="MaxTextBytes"/> here, the
/// length byte is the length of the text actually sent, and the 00 terminator follows it. A text that
/// already fits is sent byte for byte as before.</para>
///
/// <para><b>No half characters on the wire.</b> Both callers hand in ASCII: the login server's
/// <c>Encoding.ASCII.GetBytes</c> and the game server's <c>Session.AsciiBytes</c> (the same encoder after it
/// turns typographic punctuation into ASCII). Every byte either writes is 0x00-0x7F: anything else becomes
/// <c>?</c>, one per UTF-16 unit, so a character beyond U+FFFF becomes two. With no lead or trail bytes on the
/// wire, a cut at byte 255 never leaves half of a multi-byte character; at worst it keeps one of two <c>?</c>.</para>
/// </summary>
public static class LoginStatus
{
    /// <summary>The most text one frame can describe: its length field is a single byte.</summary>
    public const int MaxTextBytes = 255;

    /// <summary>The frame's increment, which is also the cipher's increment for its body.</summary>
    private const byte Increment = 0x02;

    /// <summary>The plaintext body, <c>code len text 00</c>, with <paramref name="ascii"/> cut to
    /// <see cref="MaxTextBytes"/>.</summary>
    /// <param name="ascii">The text, already encoded as one byte per character (see the class remarks).</param>
    public static byte[] Body(byte code, ReadOnlySpan<byte> ascii)
    {
        if (ascii.Length > MaxTextBytes) ascii = ascii[..MaxTextBytes];
        var body = new byte[2 + ascii.Length + 1];
        body[0] = code;
        body[1] = (byte)ascii.Length;   // the real length: the cut above keeps it within one byte
        ascii.CopyTo(body.AsSpan(2));
        // body[^1] stays 0: the terminator.
        return body;
    }

    /// <summary>The whole frame as it goes on the wire: <c>AA len 02 02</c> and the encrypted body.</summary>
    public static byte[] Build(byte code, ReadOnlySpan<byte> ascii) =>
        TkPacket.Build(ServerOp.Message, Increment, TkCrypt.Crypt(Body(code, ascii), Increment, TkCrypt.LoginKey));
}
