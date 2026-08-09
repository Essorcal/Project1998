using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Shared;

/// <summary>
/// The login→game handoff token. When login succeeds the login server MINTS a random 5-byte nonce (the
/// exact size of the 4.95 client's handoff-token slot, verified by wire probe — the client echoes those 5
/// bytes verbatim in its 0x10 arrival) and records it in the shared DB. The game server CONSUMES it on
/// arrival: the token must exist, be unexpired, be unconsumed, and be bound to the same username the
/// client claims. This closes the impersonation hole where the game port trusted whatever username the
/// client sent.
///
/// Security model: the nonce is a server-minted secret (not derivable by the client), single-use, and
/// short-lived (TTL seconds), and it is bound to the SOURCE ADDRESS the login came from. Only the
/// SHA-256 of the significant bytes is stored, so a DB leak doesn't reveal live tokens.
///
/// THE TRUNCATION RULE (this is the whole reason this class is more than a hash lookup). The 4.95 client
/// does not keep the token in a slot of its own: it copies the TAIL of our 0x03 handoff reply —
/// <c>&lt;ulen&gt;&lt;username&gt;&lt;nonce&gt;</c> — into one fixed 13-byte NUL-terminated field, i.e. 12
/// bytes plus a forced terminator, and echoes that field back verbatim in its 0x10 arrival (which is why
/// every 0x10 body is exactly 23 bytes regardless of name length). So the username and the nonce SHARE
/// one budget, and the number of nonce bytes that survive is <c>11 - username.Length</c>:
///
///   7-char name -> 4 bytes survive (32 bits)   ..the case the original "the client keeps 4 and zeroes
///   8-char name -> 3 bytes survive (24 bits)     the 5th" note was probed on, and mistook for a fixed slot
///   9-char name -> 2 bytes                     10-char name -> 1 byte    11+ -> nothing at all
///
/// Validating a fixed 4 bytes therefore rejected every name longer than 7 characters outright — a
/// freshly created 8-character account could log in but never enter the world. Both sides now derive the
/// surviving length from the username, so they agree by construction.
///
/// Because that leaves as little as one byte (or none), the nonce alone is NOT the security boundary for
/// long names — the address binding is. An attacker must both be at the login's source address and guess
/// whatever entropy survived, inside the TTL, once.
/// </summary>
public static class HandoffTokens
{
    private const int TtlSeconds = 60;   // generous: the client opens the game port within ~1s of the 0x03 reply
    private const int SigBytes = 4;      // nonce bytes we mint; how many SURVIVE depends on the name (see above)

    /// <summary>How many nonce bytes the client will still be carrying when it re-sends them in its 0x10
    /// arrival, given the username it shares the field with. Both minting and consuming key off this, so
    /// the two sides can never disagree about which prefix is being compared.</summary>
    public static int SurvivingBytes(string username) =>
        Math.Clamp(11 - (username ?? "").Length, 0, SigBytes);

    /// <summary>Mint a fresh nonce for <paramref name="username"/> arriving from <paramref name="ip"/>,
    /// store the hash of the prefix that will survive the client's truncation, and return the raw bytes
    /// for the 0x03 handoff reply's 5-byte token slot. The significant bytes are kept non-zero so the
    /// client's NUL-terminated copy can't end early; the trailing 5th byte is the terminator.</summary>
    public static byte[] Mint(string username, string ip)
    {
        var sig = new byte[SigBytes];
        RandomNumberGenerator.Fill(sig);
        for (int i = 0; i < SigBytes; i++) if (sig[i] == 0) sig[i] = 1;   // keep all significant bytes non-zero
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = @"INSERT INTO handoff_tokens(nonce_hash, username, expires_utc, consumed, ip)
                                VALUES($h, $u, $e, 0, $ip);";
            cmd.Parameters.AddWithValue("$h", HashHex(sig[..SurvivingBytes(username)]));
            cmd.Parameters.AddWithValue("$u", Auth.Key(username));
            cmd.Parameters.AddWithValue("$e", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + TtlSeconds);
            cmd.Parameters.AddWithValue("$ip", ip ?? "");
            cmd.ExecuteNonQuery();
        }
        catch { /* if minting fails the game side will reject; caller still sends the reply */ }
        return new byte[] { sig[0], sig[1], sig[2], sig[3], 0 };   // 5-byte wire token; the client keeps a prefix
    }

    /// <summary>Validate + single-use-consume a token for the claimed username and source address. Compares
    /// exactly the prefix the client was able to carry (<see cref="SurvivingBytes"/>) — a caller cannot
    /// shorten the comparison by claiming a longer name, because the length is derived from the SAME name
    /// the row is keyed on. Returns true only if the row exists, matches username + address, is unexpired,
    /// and was not already consumed: one atomic UPDATE, so a replayed 0x10 fails the second time.</summary>
    public static bool Consume(byte[] token, string expectedUser, string ip)
    {
        int need = SurvivingBytes(expectedUser);
        if (token is null || token.Length < need) return false;
        var sig = token[..need];
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = @"UPDATE handoff_tokens SET consumed=1
                                WHERE nonce_hash=$h AND username=$u AND ip=$ip
                                  AND consumed=0 AND expires_utc>$now;";
            cmd.Parameters.AddWithValue("$h", HashHex(sig));
            cmd.Parameters.AddWithValue("$u", Auth.Key(expectedUser));
            cmd.Parameters.AddWithValue("$ip", ip ?? "");
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return cmd.ExecuteNonQuery() == 1;   // exactly one row updated -> valid & now consumed
        }
        catch { return false; }
    }

    /// <summary>Best-effort cleanup of expired/consumed rows so the table doesn't grow unbounded.</summary>
    public static void Purge()
    {
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "DELETE FROM handoff_tokens WHERE consumed=1 OR expires_utc<=$now;";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }
        catch { /* best effort */ }
    }

    private static string HashHex(byte[] sig) => Convert.ToHexString(SHA256.HashData(sig));
}
