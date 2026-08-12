using Shared;

namespace LoginServer;

/// <summary>
/// Offline account administration, run as one-shot LoginServer arguments instead of opening the ports.
/// This exists because login is now STRICT (Shared/LoginAuth): nothing creates or repairs an account
/// implicitly any more, so an operator needs a real way to reset a forgotten password, see who exists, and
/// clear out a test character — without hand-editing SQLite or handing anyone a "!" command that could do
/// it over the wire.
///
///   dotnet run --project LoginServer -- --list-accounts
///   dotnet run --project LoginServer -- --set-password &lt;name&gt; &lt;password&gt;
///   dotnet run --project LoginServer -- --delete-character &lt;name&gt;
///
/// Names match case-insensitively (Auth.Key), same as login. Run these with the servers STOPPED where
/// possible: the DB is WAL and handles concurrent writers safely, but deleting a character who is online
/// would just be re-saved by that session's next autosave flush.
/// </summary>
public static class Admin
{
    /// <summary>Handle an admin argument if present. Returns true if the process should exit (the command
    /// ran, or its usage was wrong) — the caller then never opens a listening port.</summary>
    public static bool TryRun(string[] args)
    {
        if (args.Length == 0) return false;

        switch (args[0])
        {
            case "--list-accounts":
                ListAccounts();
                return true;

            case "--set-password":
                if (args.Length < 3) { Console.WriteLine("usage: --set-password <name> <password>"); return true; }
                SetPassword(args[1], args[2]);
                return true;

            case "--delete-character":
                if (args.Length < 2) { Console.WriteLine("usage: --delete-character <name>"); return true; }
                DeleteCharacter(args[1]);
                return true;

            default:
                return false;   // not an admin invocation — fall through to normal server startup
        }
    }

    private static void ListAccounts()
    {
        Db.EnsureInitialized();
        using var cn = Db.Open();
        using var cmd = cn.CreateCommand();
        // LEFT JOIN from characters: a character with no accounts row is exactly the "no password set"
        // case login now refuses, so it must show up here rather than being invisible.
        cmd.CommandText = @"
            SELECT c.username,
                   (a.pass_hash IS NOT NULL) AS has_pw,
                   a.last_login_utc,
                   c.updated_utc
            FROM characters c LEFT JOIN accounts a ON a.username = c.username
            ORDER BY c.username;";
        using var r = cmd.ExecuteReader();
        Console.WriteLine($"{"character",-16} {"password",-9} {"last login",-20} last saved");
        int n = 0, noPw = 0;
        while (r.Read())
        {
            string name = r.GetString(0);
            bool hasPw = !r.IsDBNull(1) && r.GetInt64(1) != 0;
            string last = r.IsDBNull(2) ? "never" : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(2)).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
            string saved = r.IsDBNull(3) ? "?" : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(3)).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
            Console.WriteLine($"{name,-16} {(hasPw ? "set" : "MISSING"),-9} {last,-20} {saved}");
            n++;
            if (!hasPw) noPw++;
        }
        Console.WriteLine($"\n{n} character(s), {noPw} with no password (cannot log in — fix with --set-password).");
        Console.WriteLine($"db: {Db.Path}");
    }

    private static void SetPassword(string name, string password)
    {
        Db.EnsureInitialized();
        if (!CharacterStore.CharacterExists(name))
        {
            // Refuse to create an account row with no character behind it: login checks the CHARACTER
            // first, so such a row would be dead weight and misleading in --list-accounts.
            Console.WriteLine($"No character named '{name}'. Create it in the client first (--list-accounts to see the roster).");
            return;
        }
        Accounts.SetPassword(name, Auth.Hash(password));
        Console.WriteLine($"Password set for '{name}' (matches case-insensitively).");
    }

    private static void DeleteCharacter(string name)
    {
        Db.EnsureInitialized();
        if (!CharacterStore.CharacterExists(name)) { Console.WriteLine($"No character named '{name}'."); return; }

        using var cn = Db.Open();
        using var tx = cn.BeginTransaction();
        int rows = 0;
        foreach (var sql in new[] { "DELETE FROM characters WHERE username=$u;", "DELETE FROM accounts WHERE username=$u;" })
        {
            using var cmd = cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$u", Auth.Key(name));
            rows += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        // The legacy state/chars/*.json backup is left alone deliberately — but CharacterStore's migration
        // would re-import it into an empty DB, so say so rather than leaving a surprise.
        Console.WriteLine($"Deleted '{name}' ({rows} row(s)). Note: any state/chars/{Auth.Key(name)}.json backup " +
                          "still exists and would be re-imported if the DB were ever rebuilt from scratch.");
    }
}
