using Shared;

namespace Server;

/// <summary>
/// The GM-facing moderation commands: @ban / @unban / @mute / @unmute / @kick / @banip / @bans / @modlog.
/// The state and the audit log live in <see cref="Moderation"/> (Shared, because the login process enforces
/// bans too); this file is only the command surface.
///
/// <para><b>Two axes, deliberately.</b> An account ban is evaded by making a new character; an IP ban
/// catches everyone behind one address. RTK keeps both (<c>ChaBanned</c> + a <c>BannedIP</c> table) and so
/// do we — the GM picks which fits, and for a serious case uses both.</para>
///
/// <para><b>Duration defaults to permanent.</b> Every command here reads "no duration given" as permanent
/// rather than as zero. The alternative — a mistyped command silently expiring instantly — fails in the
/// direction where nobody notices; this one fails in the direction a GM notices immediately and can undo.</para>
///
/// <para><b>Applying to an online player is immediate.</b> A ban kicks them now, a mute lands on their
/// session so the next line they type is already blocked. Waiting for the next login would make every
/// moderation action useless against the behaviour that prompted it.</para>
/// </summary>
public sealed partial class Session
{
    // "<name> [minutes] [reason]" — the tail after an optional numeric duration is free text.
    // Returns false (and complains) if no name was given.
    private bool ParseModArgs(string args, string usage, out string name, out long until, out string reason)
    {
        name = ""; until = Moderation.Forever; reason = "";
        var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) { SendLog(usage); return false; }

        name = parts[0];
        int i = 1;
        // A leading number is a duration in minutes. If the second word ISN'T a number the whole tail is the
        // reason — so "@ban cheater duping items" works without the GM having to type a duration first.
        if (parts.Length > 1 && double.TryParse(parts[1], out double mins)) { until = Moderation.Deadline(mins); i = 2; }
        if (i < parts.Length) reason = string.Join(' ', parts[i..]);
        return true;
    }

    // @ban <name> [minutes] [reason]
    private void BanCmd(string args)
    {
        if (!ParseModArgs(args, $"Usage: {Prefix}ban <name> [minutes] [reason]   (no duration = permanent)",
                          out var name, out var until, out var reason)) return;

        if (!CharacterStore.CharacterExists(name)) { SendLog($"No character named \"{name}\"."); return; }
        if (StaffAccounts.IsGm(name)) { SendLog("You cannot ban a GM. Remove them from the GM roster first."); return; }
        if (string.Equals(Auth.Key(name), UserKey, StringComparison.Ordinal)) { SendLog("You cannot ban yourself."); return; }

        if (!Moderation.Ban(name, until, reason, _char.Name)) { SendLog("Ban FAILED — the write did not land. See the log."); return; }

        // Kick them off NOW if they're online. A ban that waits for the next login lets the behaviour that
        // triggered it carry on for as long as they stay connected.
        var online = _world.FindPlayer(name);
        if (online is not null)
        {
            online.SendMessage(LoginAuth.BanMessageFor(name));
            online.Disconnect("banned");
        }

        SendLog($"Banned {name} ({Moderation.Describe(until)})"
              + (reason.Length > 0 ? $": {reason}" : ".") + (online is not null ? "  [kicked]" : ""));
        Log.Info($"   -> {Prefix}ban by '{_char.Name}': {name} until={Moderation.Describe(until)} reason='{reason}'");
    }

    // @unban <name>
    private void UnbanCmd(string args)
    {
        var name = args.Trim();
        if (name.Length == 0) { SendLog($"Usage: {Prefix}unban <name>"); return; }

        var rec = Moderation.Get(name);
        if (rec?.IsBanned != true) { SendLog($"{name} is not banned."); return; }

        SendLog(Moderation.Unban(name, _char.Name) ? $"Unbanned {name}." : "Unban FAILED — the write did not land.");
        Log.Info($"   -> {Prefix}unban by '{_char.Name}': {name}");
    }

    // @mute <name> [minutes] [reason]
    private void MuteCmd(string args)
    {
        if (!ParseModArgs(args, $"Usage: {Prefix}mute <name> [minutes] [reason]   (no duration = permanent)",
                          out var name, out var until, out var reason)) return;

        if (!CharacterStore.CharacterExists(name)) { SendLog($"No character named \"{name}\"."); return; }
        if (StaffAccounts.IsGm(name)) { SendLog("You cannot mute a GM."); return; }

        if (!Moderation.Mute(name, until, reason, _char.Name)) { SendLog("Mute FAILED — the write did not land."); return; }

        // Push it onto the live session so the very next line they type is already blocked.
        var online = _world.FindPlayer(name);
        online?.ApplyMute(until, reason);

        SendLog($"Muted {name} ({Moderation.Describe(until)})" + (reason.Length > 0 ? $": {reason}" : "."));
        Log.Info($"   -> {Prefix}mute by '{_char.Name}': {name} until={Moderation.Describe(until)} reason='{reason}'");
    }

    // @unmute <name>
    private void UnmuteCmd(string args)
    {
        var name = args.Trim();
        if (name.Length == 0) { SendLog($"Usage: {Prefix}unmute <name>"); return; }

        var rec = Moderation.Get(name);
        if (rec?.IsMuted != true) { SendLog($"{name} is not muted."); return; }

        if (!Moderation.Unmute(name, _char.Name)) { SendLog("Unmute FAILED — the write did not land."); return; }
        _world.FindPlayer(name)?.ApplyMute(0, "");

        SendLog($"Unmuted {name}.");
        Log.Info($"   -> {Prefix}unmute by '{_char.Name}': {name}");
    }

    // @kick <name> [reason] — disconnect without any lasting record beyond the mod log.
    private void KickCmd(string args)
    {
        var parts = args.Trim().Split(' ', 2);
        var name = parts[0].Trim();
        var reason = parts.Length > 1 ? parts[1].Trim() : "";
        if (name.Length == 0) { SendLog($"Usage: {Prefix}kick <name> [reason]"); return; }

        var target = _world.FindPlayer(name);
        if (target is null) { SendLog($"{name} is not online."); return; }
        if (target == this) { SendLog("You cannot kick yourself."); return; }

        // Save before dropping them — a kick must never cost the player progress they'd earned.
        target.FlushNow();
        target.SendMessage(reason.Length > 0 ? $"You were disconnected by a GM: {reason}" : "You were disconnected by a GM.");
        target.Disconnect("kicked");

        Moderation.Log(_char.Name, "kick", name, reason);
        SendLog($"Kicked {name}." + (reason.Length > 0 ? $" ({reason})" : ""));
        Log.Info($"   -> {Prefix}kick by '{_char.Name}': {name} reason='{reason}'");
    }

    // @banip <ip> [minutes] [reason] | @banip remove <ip>
    private void BanIpCmd(string args)
    {
        args = args.Trim();
        if (args.Length == 0) { SendLog($"Usage: {Prefix}banip <ip> [minutes] [reason] | {Prefix}banip remove <ip>"); return; }

        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts[0].Equals("remove", StringComparison.OrdinalIgnoreCase)
            || parts[0].Equals("unban", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 2) { SendLog($"Usage: {Prefix}banip remove <ip>"); return; }
            SendLog(Moderation.UnbanIp(parts[1], _char.Name) ? $"Lifted the ban on {parts[1]}." : "Failed.");
            return;
        }

        if (!System.Net.IPAddress.TryParse(parts[0], out _)) { SendLog($"\"{parts[0]}\" is not an IP address."); return; }

        long until = Moderation.Forever;
        int i = 1;
        if (parts.Length > 1 && double.TryParse(parts[1], out double mins)) { until = Moderation.Deadline(mins); i = 2; }
        string reason = i < parts.Length ? string.Join(' ', parts[i..]) : "";

        SendLog(Moderation.BanIp(parts[0], until, reason, _char.Name)
            ? $"Banned {parts[0]} ({Moderation.Describe(until)})" + (reason.Length > 0 ? $": {reason}" : ".")
            : "Failed.");
        Log.Info($"   -> {Prefix}banip by '{_char.Name}': {parts[0]} until={Moderation.Describe(until)}");
    }

    // @bans — everyone currently banned or muted.
    private void BansCmd(string args)
    {
        var list = Moderation.ActiveList();
        if (list.Count == 0) { SendLog("Nobody is banned or muted."); return; }

        SendLog($"{list.Count} active:");
        foreach (var r in list.Take(20))
        {
            if (r.IsBanned)
                SendLog($"  {r.Username}  BAN {Moderation.Describe(r.BanUntil)} by {r.BanBy}"
                      + (r.BanReason.Length > 0 ? $" — {r.BanReason}" : ""));
            if (r.IsMuted)
                SendLog($"  {r.Username}  MUTE {Moderation.Describe(r.MuteUntil)} by {r.MuteBy}"
                      + (r.MuteReason.Length > 0 ? $" — {r.MuteReason}" : ""));
        }
        if (list.Count > 20) SendLog($"  … and {list.Count - 20} more.");
    }

    // @modlog [n] — the last n moderation actions, newest first.
    private void ModLogCmd(string args)
    {
        int n = int.TryParse(args.Trim(), out var parsed) ? Math.Clamp(parsed, 1, 40) : 15;
        var rows = Moderation.RecentLog(n);
        if (rows.Count == 0) { SendLog("The moderation log is empty."); return; }

        foreach (var (at, actor, action, target, detail) in rows)
        {
            var when = DateTimeOffset.FromUnixTimeSeconds(at).ToLocalTime().ToString("MM-dd HH:mm");
            SendLog($"  {when}  {actor} {action} {target}" + (detail.Length > 0 ? $"  [{detail}]" : ""));
        }
    }

    /// <summary>Drop this session's connection. The read loop's finally block does the rest (party/trade
    /// teardown, map exit, final save), exactly as it does for a player who closed their client.</summary>
    internal void Disconnect(string why)
    {
        Log.Info($"   -> disconnecting '{_char.Name}': {why}");
        // EXPECTED: the player may have dropped before the kick reached them; the outcome we want (they are
        // not connected) is the same either way, and the line above already records the intent.
        try { _client.Close(); } catch { /* already gone */ }
    }
}
