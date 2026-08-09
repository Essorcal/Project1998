namespace Shared;

/// <summary>One account's moderation state. <see cref="BanUntil"/>/<see cref="MuteUntil"/> are unix seconds;
/// 0 means "not in effect". Compare with <see cref="Moderation.Now"/> — an expired row is left in place
/// rather than deleted, so the history of who did what stays readable.</summary>
public sealed record ModRecord(
    string Username,
    long BanUntil, string BanReason, string BanBy, long BanAt,
    long MuteUntil, string MuteReason, string MuteBy, long MuteAt)
{
    public bool IsBanned => BanUntil > Moderation.Now;
    public bool IsMuted  => MuteUntil > Moderation.Now;
}

/// <summary>
/// Bans and mutes, for both accounts and source IPs, plus the append-only <c>mod_log</c> of every action
/// taken. Lives in Shared because BOTH processes need it and for different halves: the login server refuses
/// a banned account at authentication, and the game server enforces mutes and runs the GM commands.
///
/// <para><b>Duration encoding.</b> Everything is an absolute unix-SECONDS deadline, never a remaining
/// duration — the same reasoning as the timed-effect deadlines and the restart schedule. A one-hour mute is
/// still over an hour from now after a server restart, and a player cannot wait out a ban by staying logged
/// off. "Permanent" is <see cref="Forever"/> (year 9999) rather than a negative sentinel, so every check in
/// the codebase is the same <c>until &gt; now</c> comparison with no special case to forget.</para>
/// </summary>
public static class Moderation
{
    /// <summary>A permanent action's deadline: 9999-12-31. Chosen over -1 so no caller has to special-case it.</summary>
    public const long Forever = 253402300799L;

    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Turn a duration in minutes into an absolute deadline. Zero or negative means permanent —
    /// which is why every command that takes a duration treats "no duration given" as permanent rather than
    /// as "expires immediately".</summary>
    public static long Deadline(double minutes)
        => minutes <= 0 ? Forever : Now + (long)(minutes * 60);

    /// <summary>Human-readable form of a deadline, for the GM's confirmation line and the @bans listing.</summary>
    public static string Describe(long until)
    {
        if (until <= 0) return "not in effect";
        if (until >= Forever) return "permanent";
        long left = until - Now;
        if (left <= 0) return "expired";
        if (left < 3600) return $"{left / 60}m";
        if (left < 86400) return $"{left / 3600}h {left % 3600 / 60}m";
        return $"{left / 86400}d {left % 86400 / 3600}h";
    }

    // ---- account state ---------------------------------------------------------------------------

    /// <summary>This account's moderation row, or null if it has never been actioned.</summary>
    public static ModRecord? Get(string username)
    {
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = @"SELECT ban_until, ban_reason, ban_by, ban_at,
                                       mute_until, mute_reason, mute_by, mute_at
                                FROM moderation WHERE username=$u LIMIT 1;";
            cmd.Parameters.AddWithValue("$u", Auth.Key(username));
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new ModRecord(Auth.Key(username),
                r.GetInt64(0), r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) ? 0 : r.GetInt64(3),
                r.GetInt64(4), r.IsDBNull(5) ? "" : r.GetString(5), r.IsDBNull(6) ? "" : r.GetString(6), r.IsDBNull(7) ? 0 : r.GetInt64(7));
        }
        catch { return null; }
    }

    /// <summary>Is this account currently banned? Returns the reason too, so the caller can say why.
    /// <b>Fails CLOSED is wrong here</b> — a database hiccup must not lock every player out of the game, so a
    /// read failure returns "not banned" and is logged by <see cref="Get"/>'s caller instead.</summary>
    public static bool IsBanned(string username, out string reason, out long until)
    {
        var r = Get(username);
        reason = r?.BanReason ?? "";
        until = r?.BanUntil ?? 0;
        return r?.IsBanned == true;
    }

    public static bool IsMuted(string username, out string reason, out long until)
    {
        var r = Get(username);
        reason = r?.MuteReason ?? "";
        until = r?.MuteUntil ?? 0;
        return r?.IsMuted == true;
    }

    public static bool Ban(string username, long until, string reason, string by)
        => SetBan(username, until, reason, by) && Log(by, "ban", username,
               $"until={Describe(until)} reason={reason}");

    public static bool Unban(string username, string by)
        => SetBan(username, 0, "", by) && Log(by, "unban", username, "");

    public static bool Mute(string username, long until, string reason, string by)
        => SetMute(username, until, reason, by) && Log(by, "mute", username,
               $"until={Describe(until)} reason={reason}");

    public static bool Unmute(string username, string by)
        => SetMute(username, 0, "", by) && Log(by, "unmute", username, "");

    private static bool SetBan(string username, long until, string reason, string by)
        => Upsert(username, @"INSERT INTO moderation(username, ban_until, ban_reason, ban_by, ban_at)
                              VALUES($u, $until, $reason, $by, $at)
                              ON CONFLICT(username) DO UPDATE SET
                                ban_until=$until, ban_reason=$reason, ban_by=$by, ban_at=$at;",
                  until, reason, by);

    private static bool SetMute(string username, long until, string reason, string by)
        => Upsert(username, @"INSERT INTO moderation(username, mute_until, mute_reason, mute_by, mute_at)
                              VALUES($u, $until, $reason, $by, $at)
                              ON CONFLICT(username) DO UPDATE SET
                                mute_until=$until, mute_reason=$reason, mute_by=$by, mute_at=$at;",
                  until, reason, by);

    private static bool Upsert(string username, string sql, long until, string reason, string by)
    {
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$u", Auth.Key(username));
            cmd.Parameters.AddWithValue("$until", until);
            cmd.Parameters.AddWithValue("$reason", reason ?? "");
            cmd.Parameters.AddWithValue("$by", by ?? "");
            cmd.Parameters.AddWithValue("$at", Now);
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [mod] !! write failed for '{username}': {e.Message}");
            return false;
        }
    }

    /// <summary>Every account with a ban or mute still in effect, most recently actioned first.</summary>
    public static List<ModRecord> ActiveList(int limit = 50)
    {
        var list = new List<ModRecord>();
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = @"SELECT username, ban_until, ban_reason, ban_by, ban_at,
                                       mute_until, mute_reason, mute_by, mute_at
                                FROM moderation
                                WHERE ban_until > $now OR mute_until > $now
                                ORDER BY MAX(COALESCE(ban_at,0), COALESCE(mute_at,0)) DESC
                                LIMIT $n;";
            cmd.Parameters.AddWithValue("$now", Now);
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new ModRecord(r.GetString(0),
                    r.GetInt64(1), r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3), r.IsDBNull(4) ? 0 : r.GetInt64(4),
                    r.GetInt64(5), r.IsDBNull(6) ? "" : r.GetString(6), r.IsDBNull(7) ? "" : r.GetString(7), r.IsDBNull(8) ? 0 : r.GetInt64(8)));
        }
        catch { /* an unreadable list is an empty list, not a crash */ }
        return list;
    }

    // ---- IP bans ---------------------------------------------------------------------------------

    /// <summary>Is this source address banned? Called on the LOGIN path only — checking it per-packet would
    /// put a database read on the hot path for no benefit, since a ban placed mid-session is followed by an
    /// explicit kick.</summary>
    public static bool IsIpBanned(string ip, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(ip)) return false;
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT until, reason FROM banned_ips WHERE ip=$ip LIMIT 1;";
            cmd.Parameters.AddWithValue("$ip", ip);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return false;
            long until = r.GetInt64(0);
            reason = r.IsDBNull(1) ? "" : r.GetString(1);
            return until > Now;
        }
        catch { return false; }
    }

    public static bool BanIp(string ip, long until, string reason, string by)
    {
        // Locals rather than reassigning the parameters: callers reach this through Lua/command paths where
        // the compiler cannot prove non-null, and a definite local is what makes that provable here.
        string why = reason ?? "", actor = by ?? "";
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = @"INSERT INTO banned_ips(ip, until, reason, banned_by, banned_at)
                                VALUES($ip, $until, $reason, $by, $at)
                                ON CONFLICT(ip) DO UPDATE SET
                                  until=$until, reason=$reason, banned_by=$by, banned_at=$at;";
            cmd.Parameters.AddWithValue("$ip", ip);
            cmd.Parameters.AddWithValue("$until", until);
            cmd.Parameters.AddWithValue("$reason", why);
            cmd.Parameters.AddWithValue("$by", actor);
            cmd.Parameters.AddWithValue("$at", Now);
            cmd.ExecuteNonQuery();
            return Log(actor, until > 0 ? "banip" : "unbanip", ip, $"until={Describe(until)} reason={why}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [mod] !! ip ban write failed for '{ip}': {e.Message}");
            return false;
        }
    }

    public static bool UnbanIp(string ip, string by) => BanIp(ip, 0, "", by);

    // ---- the log ---------------------------------------------------------------------------------

    /// <summary>Append one action to <c>mod_log</c>. Every mutator above routes through here, including the
    /// undo operations — "who lifted this ban" is a question that gets asked as often as "who placed it".</summary>
    public static bool Log(string actor, string action, string target, string detail)
    {
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = @"INSERT INTO mod_log(at_utc, actor, action, target, detail)
                                VALUES($at, $actor, $action, $target, $detail);";
            cmd.Parameters.AddWithValue("$at", Now);
            cmd.Parameters.AddWithValue("$actor", actor ?? "");
            cmd.Parameters.AddWithValue("$action", action ?? "");
            cmd.Parameters.AddWithValue("$target", target ?? "");
            cmd.Parameters.AddWithValue("$detail", detail ?? "");
            cmd.ExecuteNonQuery();
            return true;
        }
        catch { return true; }   // a failed AUDIT write must not fail the ACTION it describes
    }

    /// <summary>The most recent actions, newest first — the <c>@modlog</c> command.</summary>
    public static List<(long At, string Actor, string Action, string Target, string Detail)> RecentLog(int limit = 20)
    {
        var list = new List<(long, string, string, string, string)>();
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT at_utc, actor, action, target, detail FROM mod_log ORDER BY id DESC LIMIT $n;";
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2),
                          r.IsDBNull(3) ? "" : r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4)));
        }
        catch { }
        return list;
    }
}
