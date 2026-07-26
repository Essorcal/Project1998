using Microsoft.Data.Sqlite;

namespace Shared;

/// <summary>
/// Password hashing (BCrypt) + the accounts table. Accounts are keyed by the same normalized username as
/// characters. Passwords are hashed with BCrypt (cost 11); the salt is embedded in the hash string, so no
/// separate salt column is needed. The 4.95 client caps passwords at 3-8 chars (RTK login/clif.c), so the
/// wire secret is low-entropy — hashing protects the at-rest DB and is paired with per-IP login rate
/// limiting (Phase 3) against online guessing.
/// </summary>
public static class Auth
{
    private const int Cost = 11;

    public static string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password ?? "", Cost);

    public static bool Verify(string password, string hash)
    {
        try { return BCrypt.Net.BCrypt.Verify(password ?? "", hash); }
        catch { return false; }   // malformed hash -> treat as non-match rather than throw
    }

    // Normalize to the same case-insensitive key CharacterStore uses so accounts and characters line up.
    public static string Key(string name)
    {
        var s = new string((name ?? string.Empty).ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        return string.IsNullOrEmpty(s) ? "_" : s;
    }
}

/// <summary>The `accounts` table: username -> password hash. Separate from the character JSON so auth can
/// be reasoned about (and rate-limited) independently of game state.</summary>
public static class Accounts
{
    /// <summary>The stored BCrypt hash for an account, or null if the account has never set a password
    /// (never registered, or a legacy character migrated before auth existed → eligible for TOFU).</summary>
    public static string? GetHash(string username)
    {
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT pass_hash FROM accounts WHERE username=$u LIMIT 1;";
            cmd.Parameters.AddWithValue("$u", Auth.Key(username));
            return cmd.ExecuteScalar() as string;   // null if no row OR pass_hash is NULL
        }
        catch { return null; }
    }

    public static bool Exists(string username)
    {
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM accounts WHERE username=$u LIMIT 1;";
            cmd.Parameters.AddWithValue("$u", Auth.Key(username));
            return cmd.ExecuteScalar() is not null;
        }
        catch { return false; }
    }

    /// <summary>Create the account or set/replace its password hash (used by registration and TOFU).</summary>
    public static void SetPassword(string username, string passHash)
    {
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = @"INSERT INTO accounts(username, pass_hash, created_utc, last_login_utc)
                                VALUES($u, $h, $t, NULL)
                                ON CONFLICT(username) DO UPDATE SET pass_hash=$h;";
            cmd.Parameters.AddWithValue("$u", Auth.Key(username));
            cmd.Parameters.AddWithValue("$h", passHash);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }
        catch { /* best effort */ }
    }

    public static void TouchLogin(string username)
    {
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "UPDATE accounts SET last_login_utc=$t WHERE username=$u;";
            cmd.Parameters.AddWithValue("$u", Auth.Key(username));
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }
        catch { /* best effort */ }
    }
}
