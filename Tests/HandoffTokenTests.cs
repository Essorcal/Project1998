using Shared;
using Xunit;

namespace Tests;

/// <summary>
/// The login→game handoff token, mint through consume. These run against the REAL database (state/project1998.db)
/// with throwaway usernames, because the guarantees under test are single-use and expiry — both enforced by
/// the SQL, so a mock would only prove the mock works.
///
/// The regression that motivated this file: a freshly created 8-character account could log in but was then
/// bounced at world entry with "invalid/expired handoff token". The client shares ONE fixed 13-byte field
/// between the username and the nonce, so a name longer than 7 characters truncates the nonce — and the
/// game server was comparing a fixed 4 bytes it would never receive.
/// </summary>
public class HandoffTokenTests : IDisposable
{
    private const string Ip = "203.0.113.7";           // TEST-NET-3; never a real client
    private readonly List<string> _minted = new();

    /// <summary>A throwaway username of EXACTLY <paramref name="length"/> characters — the length is the
    /// whole point of these tests — and unique, so two names in one test are genuinely different accounts.</summary>
    private string Name(int length)
    {
        var n = ("zz" + Guid.NewGuid().ToString("N"))[..length];
        _minted.Add(n);
        return n;
    }

    public void Dispose()
    {
        try
        {
            using var cn = Db.Open();
            foreach (var n in _minted)
            {
                using var cmd = cn.CreateCommand();
                cmd.CommandText = "DELETE FROM handoff_tokens WHERE username=$u;";
                cmd.Parameters.AddWithValue("$u", Auth.Key(n));
                cmd.ExecuteNonQuery();
            }
        }
        catch { /* best effort cleanup */ }
    }

    /// <summary>Simulate the client's copy of our 0x03 reply tail: it strncpy's
    /// &lt;ulen&gt;&lt;username&gt;&lt;nonce&gt; into a 13-byte field (12 bytes + a forced NUL) and echoes
    /// the field back in 0x10. This is the exact transform that broke long names.</summary>
    private static byte[] ClientEcho(string user, byte[] nonce)
    {
        var blob = new List<byte> { (byte)user.Length };
        blob.AddRange(System.Text.Encoding.ASCII.GetBytes(user));
        blob.AddRange(nonce);
        while (blob.Count < 13) blob.Add(0);   // strncpy pads the remainder with NULs
        blob = blob.GetRange(0, 13);
        blob[12] = 0;                          // ...and the field is always NUL-terminated
        return blob.GetRange(1 + user.Length, 12 - user.Length).ToArray();   // what lands in the token slot
    }

    // 7 chars was the only length ever probed, which is how the fixed-4-bytes assumption survived. The 8-
    // to 10-char cases are the bug: each one used to be rejected at world entry.
    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void TruncatedTokenStillConsumes(int nameLength)
    {
        var user = Name(nameLength);
        var nonce = HandoffTokens.Mint(user, Ip);
        Assert.Equal(5, nonce.Length);   // the wire slot is always 5 bytes, however much survives

        Assert.True(HandoffTokens.Consume(ClientEcho(user, nonce), user, Ip),
            $"a {nameLength}-character name must survive the client's truncation");
    }

    [Fact]
    public void TokenIsSingleUse()
    {
        var user = Name(8);
        var nonce = HandoffTokens.Mint(user, Ip);
        var echo = ClientEcho(user, nonce);

        Assert.True(HandoffTokens.Consume(echo, user, Ip));
        Assert.False(HandoffTokens.Consume(echo, user, Ip));   // replayed 0x10
    }

    [Fact]
    public void TokenIsBoundToTheLoginAddress()
    {
        var user = Name(8);
        var echo = ClientEcho(user, HandoffTokens.Mint(user, Ip));
        Assert.False(HandoffTokens.Consume(echo, user, "198.51.100.9"));
    }

    [Fact]
    public void TokenIsBoundToTheUsername()
    {
        var user = Name(8);
        var other = Name(8);
        var echo = ClientEcho(user, HandoffTokens.Mint(user, Ip));
        Assert.False(HandoffTokens.Consume(echo, other, Ip));
    }

    /// <summary>A wrong nonce must still fail — the truncation fix must not have degraded into accepting
    /// anything of the right length.</summary>
    [Fact]
    public void WrongNonceIsRejected()
    {
        var user = Name(8);
        HandoffTokens.Mint(user, Ip);
        Assert.False(HandoffTokens.Consume(new byte[] { 1, 2, 3, 0 }, user, Ip));
    }

    /// <summary>The truncation budget the two sides derive independently. If this table ever changes, the
    /// login and game servers must change together or every login breaks.</summary>
    [Theory]
    [InlineData(3, 4)]
    [InlineData(7, 4)]
    [InlineData(8, 3)]
    [InlineData(9, 2)]
    [InlineData(10, 1)]
    [InlineData(11, 0)]
    [InlineData(12, 0)]
    public void SurvivingBytesMatchesTheClientField(int nameLength, int expected) =>
        Assert.Equal(expected, HandoffTokens.SurvivingBytes(new string('x', nameLength)));
}
