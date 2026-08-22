using System.Text;
using Shared;

namespace Protocol.Tk495;

/// <summary>
/// The `0x03` redirect — "close this screen and connect to ip:port instead". It is the ONE packet that moves
/// a 4.95 client between the two front doors, and all three of our transitions are the same struct with a
/// different address in it:
///
/// <list type="bullet">
/// <item>login → game (the handoff after a successful <c>0x03</c> login) — <c>LoginSession.HandleLogin</c></item>
/// <item>game → game (re-login on the game socket, the fallback path) — <c>Session.HandleReLogin</c></item>
/// <item>game → login (exit to the select screen, <c>0x0B</c>) — <c>Session.HandleExitToSelect</c></item>
/// </list>
///
/// It lived hand-rolled in each of those, which is a drift risk with teeth: the client parses this reply as a
/// FIXED-SIZE redirect struct, so a byte of disagreement between the two processes does not throw — the client
/// simply hangs on a screen forever, which is exactly how the character-creation bug presented. One builder,
/// one wire format.
///
/// Layout (Protocol.md §4.1), after the standard <c>AA len op</c> frame header:
/// <code>
///   ip[3] ip[2] ip[1] ip[0]   octets REVERSED (127.0.0.1 -> 01 00 00 7F)
///   port(u16BE)
///   23 00 09                  constants observed in the working handoff
///   "NexonInc."               the 9-byte key string, echoed
///   nameLen name
///   tail[5]
/// </code>
/// </summary>
public static class LoginRedirect
{
    /// <summary>Total width of the client's single NUL-terminated field holding <c>nameLen name tail</c>.</summary>
    public const int TailBytes = 5;

    /// <param name="host">Address the CLIENT must be able to reach — a bind address or a loopback default is
    /// wrong behind a proxy. Four octets, in normal order; this reverses them for the wire.</param>
    /// <param name="tail">The 5 bytes after the username: a single-use handoff nonce when the client is being
    /// sent to a GAME port, zero padding when it is being sent back to LOGIN (which mints its own). Must stay
    /// exactly <see cref="TailBytes"/> long — the client reads a fixed-size struct, and a 16-byte token was
    /// live-proven to corrupt the parse and break login outright.</param>
    public static byte[] Build(byte[] host, int port, string user, byte[] tail)
    {
        if (host.Length != 4) throw new ArgumentException("host must be 4 octets", nameof(host));
        if (tail.Length != TailBytes) throw new ArgumentException($"tail must be {TailBytes} bytes", nameof(tail));

        var p = new List<byte>
        {
            0xAA, 0, 0, Opcode.Login,
            host[3], host[2], host[1], host[0],
            (byte)(port >> 8), (byte)(port & 0xFF),
            23, 0, 9
        };
        p.AddRange(TkCrypt.LoginKey);
        var uname = Encoding.ASCII.GetBytes(user);
        p.Add((byte)uname.Length);
        p.AddRange(uname);
        p.AddRange(tail);
        p[2] = (byte)(p.Count - 3);   // frame length excludes AA + the 2 length bytes
        return p.ToArray();
    }
}
