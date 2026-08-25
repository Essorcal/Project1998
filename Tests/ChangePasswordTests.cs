using Shared;
using Xunit;

namespace Tests;

/// <summary>
/// The login-screen password change (0x26). Two silent failures live here: a mis-parsed triple body
/// (three length-prefixed strings back-to-back — one offset error and the OLD password silently reads
/// as the NEW one, "changing" the password to itself), and a change that doesn't actually replace the
/// stored hash (Accounts.SetPassword's upsert taking the INSERT arm's no-op instead of the UPDATE).
/// Both leave the wire conversation looking perfectly healthy — the client shows "password changed"
/// and the player finds out at their next login.
///
/// DB tests run against the REAL database with throwaway usernames, same as HandoffTokenTests, because
/// the persistence guarantee under test is the SQL upsert's.
/// </summary>
public class ChangePasswordTests : IDisposable
{
    private readonly List<string> _made = new();

    private string Name()
    {
        var n = ("zz" + Guid.NewGuid().ToString("N"))[..10];
        _made.Add(n);
        return n;
    }

    public void Dispose()
    {
        try
        {
            using var cn = Db.Open();
            foreach (var n in _made)
            {
                using var cmd = cn.CreateCommand();
                cmd.CommandText = "DELETE FROM accounts WHERE username=$u;";
                cmd.Parameters.AddWithValue("$u", Auth.Key(n));
                cmd.ExecuteNonQuery();
            }
        }
        catch { /* best effort cleanup */ }
    }

    /// <summary>Build the decrypted 0x26 body the 5.33 client sends: nameLen name oldLen old newLen new.
    /// No trailing NUL — the live 2026-08-25 capture was exactly 18B for a 4+5+6 triple, unlike 0x03
    /// which does carry one.</summary>
    private static byte[] Body(string name, string oldPass, string newPass)
    {
        var b = new List<byte> { (byte)name.Length };
        b.AddRange(System.Text.Encoding.ASCII.GetBytes(name));
        b.Add((byte)oldPass.Length);
        b.AddRange(System.Text.Encoding.ASCII.GetBytes(oldPass));
        b.Add((byte)newPass.Length);
        b.AddRange(System.Text.Encoding.ASCII.GetBytes(newPass));
        return b.ToArray();
    }

    [Fact]
    public void Parses_the_observed_triple_shape()
    {
        // The live capture (2026-08-25) was name=4, old=5, new=6 chars -> an 18-byte body. Same shape here.
        var dec = Body("Test", "abcd1", "efghi2");
        Assert.Equal(18, dec.Length);

        Assert.True(LoginAuth.TryReadChangePassword(dec, out var name, out var oldPass, out var newPass));
        Assert.Equal("Test", name);
        Assert.Equal("abcd1", oldPass);    // THE regression: an offset slip makes old and new collapse
        Assert.Equal("efghi2", newPass);
    }

    [Theory]
    [InlineData(0)]   // empty body
    [InlineData(1)]   // name length only
    [InlineData(6)]   // name but no old password
    [InlineData(12)]  // old password but no new one
    public void Refuses_a_truncated_body_instead_of_misreading_it(int keep)
    {
        var dec = Body("Test", "abcd1", "efghi2")[..keep];
        Assert.False(LoginAuth.TryReadChangePassword(dec, out _, out _, out _));
    }

    [Fact]
    public void Refuses_a_name_length_that_overruns_the_body()
    {
        var dec = Body("Test", "abcd1", "efghi2");
        dec[0] = 200;   // nameLen claims more bytes than exist
        Assert.False(LoginAuth.TryReadChangePassword(dec, out _, out _, out _));
    }

    [Fact]
    public void A_change_replaces_the_stored_hash()
    {
        var user = Name();
        Accounts.SetPassword(user, Auth.Hash("old1"));
        Assert.True(Auth.Verify("old1", Accounts.GetHash(user)!));

        // What HandleChangePassword does on success. The guard is that the upsert takes the UPDATE arm:
        // if it doesn't, the old password still verifies and nothing anywhere reports it.
        Accounts.SetPassword(user, Auth.Hash("new2"));

        var hash = Accounts.GetHash(user);
        Assert.NotNull(hash);
        Assert.False(Auth.Verify("old1", hash!));
        Assert.True(Auth.Verify("new2", hash!));
    }
}
