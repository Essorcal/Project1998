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
/// short-lived (TTL seconds). NOTE the 4.95 client preserves only the FIRST 4 BYTES of the 5-byte token
/// slot and zeroes the 5th (verified live), so the nonce is effectively 4 bytes (32 bits). Small, but
/// blind guessing is defeated by single-use + the short window + per-IP rate limiting on the game port
/// (Phase 3). Only the SHA-256 of the nonce is stored, so a DB leak doesn't reveal live tokens.
/// </summary>
public static class HandoffTokens
{
    private const int TtlSeconds = 60;   // generous: the client opens the game port within ~1s of the 0x03 reply
    private const int SigBytes = 4;      // the 4.95 client keeps only the first 4 token bytes (5th forced to 0)

    /// <summary>Mint a fresh nonce for <paramref name="username"/>, store its hash, and return the raw
    /// bytes to place in the 0x03 handoff reply's 5-byte token slot. Only the first 4 bytes are
    /// significant (the client zeroes the 5th); they are kept non-zero so the client can't treat the slot
    /// as an early-terminated string. The trailing 5th byte is 0 to match exactly what the client echoes.</summary>
    public static byte[] Mint(string username)
    {
        var sig = new byte[SigBytes];
        RandomNumberGenerator.Fill(sig);
        for (int i = 0; i < SigBytes; i++) if (sig[i] == 0) sig[i] = 1;   // keep all significant bytes non-zero
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = @"INSERT INTO handoff_tokens(nonce_hash, username, expires_utc, consumed)
                                VALUES($h, $u, $e, 0);";
            cmd.Parameters.AddWithValue("$h", HashHex(sig));
            cmd.Parameters.AddWithValue("$u", Auth.Key(username));
            cmd.Parameters.AddWithValue("$e", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + TtlSeconds);
            cmd.ExecuteNonQuery();
        }
        catch { /* if minting fails the game side will reject; caller still sends the reply */ }
        return new byte[] { sig[0], sig[1], sig[2], sig[3], 0 };   // 5-byte wire token (client echoes 4 + a 0)
    }

    /// <summary>Validate + single-use-consume a token for the claimed username. Only the first 4 bytes are
    /// used (the client zeroes the rest). Returns true only if it exists, matches the username, is
    /// unexpired, and was not already consumed — a single atomic UPDATE, so a replayed 0x10 fails the
    /// second time.</summary>
    public static bool Consume(byte[] token, string expectedUser)
    {
        if (token is null || token.Length < SigBytes) return false;
        var sig = token[..SigBytes];
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = @"UPDATE handoff_tokens SET consumed=1
                                WHERE nonce_hash=$h AND username=$u AND consumed=0 AND expires_utc>$now;";
            cmd.Parameters.AddWithValue("$h", HashHex(sig));
            cmd.Parameters.AddWithValue("$u", Auth.Key(expectedUser));
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
