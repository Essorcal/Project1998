using Protocol.Tk495;
using Shared;

namespace Tests.Support;

/// <summary>
/// The <c>0x10</c> arrival a client sends after a hand-off, built the way <c>LoginProbe</c> builds it: mint, build
/// the real <c>0x03</c> redirect with <see cref="LoginRedirect.Build(byte[], int, string, HandoffTokens.Grant)"/>,
/// and copy its bytes from <c>klen</c> on (<c>raw[12..]</c>) into the arrival body, so the key field, the name and
/// the whole 5-byte token come back exactly as the redirect carried them. The stock client returns the same key
/// bytes and a prefix of the token (<c>HandoffTokenTests.ClientEcho</c>); the game server compares only that
/// prefix, so both shapes enter.
/// </summary>
internal static class Handoff
{
    /// <summary>The 4.95 game port, where a hand-off carries a one-time key.</summary>
    public const int Game495 = 2005;

    /// <summary>The 5.33 game port, where a hand-off carries <c>"NexonInc."</c> and no key.</summary>
    public const int Game533 = 2006;

    /// <summary>Mint a hand-off for <paramref name="user"/> from <paramref name="ip"/> to
    /// <paramref name="gamePort"/> and frame the arrival that returns it.</summary>
    public static byte[] ArrivalFrame(string user, string ip, int gamePort = Game495) =>
        Frame(ArrivalBody(user, HandoffTokens.Mint(user, ip, gamePort)));

    /// <summary>The arrival body for a grant already minted: <c>klen key ulen user token</c>, the redirect's own
    /// bytes from <c>klen</c> on.</summary>
    public static byte[] ArrivalBody(string user, HandoffTokens.Grant grant) =>
        LoginRedirect.Build(new byte[] { 127, 0, 0, 1 }, grant.GamePort, user, grant)[12..];

    /// <summary>Frame an arrival body: <c>0x10</c> is unciphered, so the body goes on the wire as it is.</summary>
    public static byte[] Frame(byte[] body) => TkPacket.Build(Opcode.Arrival, 0, body);
}
