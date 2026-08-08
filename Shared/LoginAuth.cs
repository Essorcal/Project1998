using System.Text;

namespace Shared;

/// <summary>Outcome of a login attempt. The caller turns this into the client-visible message; keeping the
/// three cases distinct is what lets the login screen say "that character doesn't exist" instead of
/// silently creating one.</summary>
public enum LoginResult
{
    /// <summary>Character exists and the password matched.</summary>
    Ok,
    /// <summary>No character record for that name — the player must go through character creation first.</summary>
    NoCharacter,
    /// <summary>Character exists but the password is wrong.</summary>
    BadPassword,
    /// <summary>Character exists but has no password on file (a record migrated from before auth existed).
    /// NOT auto-adopted: an admin must set one (LoginServer --set-password) or enable NEXUS_ALLOW_TOFU=1.</summary>
    NoPassword,
}

/// <summary>
/// Shared login authentication, used by BOTH the login server (primary login on 2000/2001) and the game
/// server (re-login: when the client exits to the select screen via Alt+X it re-sends 0x03 on the still-
/// open game connection, and the game server must answer it the way the original single-process server
/// did). Keeping the verify rule here means the two processes can't drift.
///
/// STRICT since the hosting pass: logging in NEVER creates an account or a character. The only way a
/// character comes into existence is the client's creation flow (login channel 0x02 name-check + 0x04
/// appearance -> LoginSession.HandleCreate). Trust-on-first-use — which used to silently adopt whatever
/// password was sent for an unknown name — is gone: it made every unregistered name a free account and,
/// worse, let anyone claim a legacy passwordless character.
/// </summary>
public static class LoginAuth
{
    // Escape hatch for the handful of characters that predate the accounts table (they exist in
    // `characters` with no `accounts` row). Off by default; set NEXUS_ALLOW_TOFU=1 for one login to adopt a
    // password for such a record, then turn it back off. Never applies to a name with NO character.
    private static bool AllowLegacyAdopt =>
        (Environment.GetEnvironmentVariable("NEXUS_ALLOW_TOFU") ?? "0").Trim() == "1";

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

    /// <summary>Authenticate a login attempt. Username matching is case-insensitive throughout (both the
    /// `accounts` and `characters` tables key on <see cref="Auth.Key"/> and are COLLATE NOCASE), so
    /// "Snuggle" and "snuggle" are the same login. Never creates anything.</summary>
    public static LoginResult Authenticate(string user, string pass)
    {
        if (!CharacterStore.CharacterExists(user)) return LoginResult.NoCharacter;

        var hash = Accounts.GetHash(user);
        if (hash is null)
        {
            if (!AllowLegacyAdopt) return LoginResult.NoPassword;
            Accounts.SetPassword(user, Auth.Hash(pass));   // one-time legacy adoption, opt-in only
            return LoginResult.Ok;
        }
        return Auth.Verify(pass, hash) ? LoginResult.Ok : LoginResult.BadPassword;
    }

    /// <summary>The client-visible one-line message for a failed <see cref="Authenticate"/>. Kept here so
    /// the login channel and the game channel's re-login path say exactly the same thing. Deliberately
    /// does NOT distinguish "no such character" from "wrong password" any more than the player needs:
    /// the name-existence half is already public (the creation name-check reveals it), so there's no new
    /// disclosure, and telling a real player "that character doesn't exist" is the whole point.</summary>
    public static string MessageFor(LoginResult r) => r switch
    {
        LoginResult.NoCharacter => "That character does not exist.",
        LoginResult.BadPassword => "Incorrect password.",
        LoginResult.NoPassword  => "That character has no password set. Contact the server admin.",
        _                       => "",
    };
}
