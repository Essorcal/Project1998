using System.Text;

namespace Shared;

/// <summary>
/// Shared login authentication, used by BOTH the login server (primary login on 2000/2001) and the game
/// server (re-login: when the client exits to the select screen via Alt+X it re-sends 0x03 on the still-
/// open game connection, and the game server must answer it the way the original single-process server
/// did). Keeping the verify/TOFU rule here means the two processes can't drift.
/// </summary>
public static class LoginAuth
{
    /// <summary>Parse the `pwLen pw` that follows the length-prefixed username at <paramref name="off"/> in
    /// a decrypted 0x02/0x03 body. Returns "" if the password field is absent/malformed.</summary>
    public static string ReadPassword(byte[] dec, int off)
    {
        try
        {
            if (off >= dec.Length) return "";
            int plen = dec[off];
            if (plen <= 0 || off + 1 + plen > dec.Length) return "";
            return Encoding.ASCII.GetString(dec, off + 1, plen);
        }
        catch { return ""; }
    }

    /// <summary>Authenticate a login. An account WITH a stored hash must match it. An account with NO hash
    /// (never registered, or a legacy character migrated before auth existed) is trust-on-first-use: the
    /// first login SETS the password to what was sent. Returns false only when a hash exists and the
    /// password doesn't match.</summary>
    public static bool Authenticate(string user, string pass)
    {
        var hash = Accounts.GetHash(user);
        if (hash is null) { Accounts.SetPassword(user, Auth.Hash(pass)); return true; }
        return Auth.Verify(pass, hash);
    }
}
