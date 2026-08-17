using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Protocol.Tk495;
using Shared;

namespace Server;

public sealed partial class Session
{

    // Client chat (0x0E): chatType(u8) msgLen(u8) msg[]. Echo it back as over-head speech (0x0D)
    // attributed to the sender's entity so the bubble appears above the character.
    private void HandleChat(byte[] dec)
    {
        if (dec.Length < 2) return;
        byte chatType = dec[0];
        int msgLen = dec[1];
        if (msgLen < 0 || 2 + msgLen > dec.Length) return;
        var msg = dec[2..(2 + msgLen)];
        var text = Encoding.ASCII.GetString(msg);

        // ==== chat commands ======================================================================
        // Every command, player and GM alike, lives in the Server/Commands.cs table: that file owns the
        // '@' prefix, the name -> handler map, the per-command GM gate and @help. Nothing here needs to
        // know a single command name. Anything that isn't a command falls through to speech below.
        if (TryRunCommand(text)) return;

        // Client-native RTK typed commands. A DIFFERENT namespace from our '@' prefix (these are what the
        // real client's speech.lua /help lists), so they stay as their own checks rather than table rows.
        if (text.StartsWith("/subpathchat ", StringComparison.OrdinalIgnoreCase)) { DoSubpathChat(text[13..].Trim()); return; }
        if (text.StartsWith("/sp ", StringComparison.OrdinalIgnoreCase)) { DoSubpathChat(text[4..].Trim()); return; }

        // Commands moved from '!' to '@'. Staff leading with '!' are near-certainly reaching for the old
        // prefix, and letting that through would SHOUT the old-style command to everyone on the map — so nudge
        // instead. Only for staff (either tier): for an ordinary player '!' has never meant anything, and
        // their shouts must keep working.
        if (Access > AccessLevel.Player && text.Length > 1 && text[0] == '!' && char.IsLetter(text[1]))
        { SendLog($"Commands now start with '{Prefix}' — try  {Prefix}{text[1..]}"); return; }

        // "A" (uppercase, exact) — remove ALL equipped items at once, same effect as clicking every worn
        // slot's 0x1F unequip in a row. Case-sensitive so ordinary chat ("a", "aww", …) still speaks
        // normally; nothing else in this client sends a bare capital letter as a message.
        if (text == "A") { UnequipAll(); return; }

        // Muted players may still run '@' commands (handled above) but cannot SPEAK. The gate sits here,
        // after the command table, so a mute silences the player without also taking away @ignore or @friend.
        if (IsMuted()) { ReportMuted(); return; }

        // Real chat (not a ! command): everyone on the map hears it. Broadcast the over-head bubble (0x0D)
        // to all co-located players INCLUDING us, so we see our own bubble too. Prefix with who said it
        // (client keybind help, re/str_eng.res:102/105, documents dedicated Say ''' and Shout '!' hotkeys —
        // this is the server-side text the client shows in both the bubble and the chat-log line for either).
        // chatType is passed through UNCHANGED from the client's own byte: whatever mode it used to pick the
        // hotkey ('=say vs !=shout) is presumably also what the client uses to pick bubble/log color on
        // playback, so relaying its own value back should already render correctly without us inventing a
        // color scheme. UNCONFIRMED which raw byte value means shout — logged below so a live '!' test can
        // pin it down (say=0 is confirmed by every chat message that has worked so far).
        bool shout = chatType != 0;
        string formatted = shout ? $"{_char.Name}! {text}" : $"{_char.Name}: {text}";
        if (formatted.Length > 250) formatted = formatted[..250];
        var outMsg = Encoding.ASCII.GetBytes(formatted);
        _world.Broadcast(_char.Map, p => p.SpeakEntity(chatType, _char.Id, outMsg));
        Log.Info($"   -> speech type={chatType}{(shout ? " (presumed SHOUT)" : "")}: \"{text}\" -> map {_char.Map}");

        // …and let a nearby NPC react to it (RTK onSayClick: "i'd like to fish", a tutor's name, …).
        DispatchSpeech(text);
    }

    // ---- mute ---------------------------------------------------------------------------------------
    //
    // Held as an absolute unix-SECONDS deadline on the SESSION, not re-read from the database per line. A
    // DB round-trip on every chat message would put a synchronous read on the packet path for state that
    // changes maybe twice a week. It is loaded once at world entry (LoadModerationState) and pushed
    // directly onto the live session by @mute/@unmute (Session.ApplyMute), so both the placement and the
    // lifting are immediate; the deadline being absolute is what makes EXPIRY work with no timer at all.
    private long _mutedUntil;
    private string _muteReason = "";

    internal bool IsMuted() => _mutedUntil > Moderation.Now;

    /// <summary>Load this account's mute state into the session. Called once, at world entry.</summary>
    internal void LoadModerationState()
    {
        if (Moderation.IsMuted(_user, out var reason, out var until)) { _mutedUntil = until; _muteReason = reason; }
        else { _mutedUntil = 0; _muteReason = ""; }
    }

    /// <summary>Apply a mute/unmute to an ALREADY-ONLINE session, so a GM's command takes effect on the next
    /// line the player types rather than at their next login.</summary>
    internal void ApplyMute(long until, string reason)
    {
        _mutedUntil = until;
        _muteReason = reason ?? "";
        if (IsMuted()) ReportMuted();
        else SendLog("You are no longer muted.");
    }

    private void ReportMuted()
    {
        string left = _mutedUntil >= Moderation.Forever ? "" : $" ({Moderation.Describe(_mutedUntil)} remaining)";
        SendLog(string.IsNullOrWhiteSpace(_muteReason)
            ? $"You are muted and cannot speak{left}."
            : $"You are muted and cannot speak{left}: {_muteReason}");
    }

    // ---- whisper/tell (RTK clif_parsewisp, clif.c:7644-7790) ---------------------------------------------
    // Native client input: Shift+' opens the whisper prompt, then a name + Enter, then a message + Enter.
    // LIVE-confirmed 2026-07-26 (real capture): op=0x19 body = dstlen(u8) dst_name[dstlen] msglen(u8)
    // msg[msglen] 00 — exactly RTK's wire layout, and the ONLY entry point: the "@whisper"/"@w" chat
    // commands that used to wrap DoWhisper were removed once this was confirmed real. Message TEXT is RTK's
    // real wording wherever portable (not-found, map-silenced). Not modelled: per-player whisper on/off,
    // silence/mute, and ignore lists — none of those exist yet.
    private void HandleWhisperPacket(byte[] dec)
    {
        if (dec.Length < 1) return;
        int dstLen = dec[0];
        if (dstLen <= 0 || 1 + dstLen + 1 > dec.Length) return;
        string name = Encoding.ASCII.GetString(dec, 1, dstLen);
        int msgLen = dec[1 + dstLen];
        int msgStart = 1 + dstLen + 1;
        if (msgLen < 0 || msgStart + msgLen > dec.Length) return;
        string msg = Encoding.ASCII.GetString(dec, msgStart, msgLen);
        DoWhisper(name, msg);
    }

    private void DoWhisper(string name, string msg)
    {
        if (name.Length == 0 || msg.Length == 0) return;

        // A mute that only stopped map speech would be no mute at all — whisper is the obvious way around it.
        if (IsMuted()) { ReportMuted(); return; }

        // RTK: map[sd->bl.m].cantalk == 1 blocks whisper with this exact line (only 2 maps set it).
        if (!Content.CanTalk(_char.Map)) { SendLog("Your voice is swept away by a strange wind."); return; }

        var target = _world.FindPlayer(name);
        if (target is null) { SendLog($"{name} is nowhere to be found."); return; }   // RTK's literal wording

        // RTK clif_isignore: a whisper is blocked if EITHER side has the other on their ignore list — not
        // just the recipient blocking the sender, but also the sender's own list (so you can't be pestered
        // by someone you've muted even if THEY never muted you). canwhisper's real wording on failure.
        if (IsIgnoring(target._char.Name) || target.IsIgnoring(_char.Name))
        { SendLog("They can't hear you right now."); return; }

        target.ReceiveWhisper(_char.Name, msg);
        SendMiniText($"{_char.Name}: {msg}", type: 0);   // sender's own echo — same line the receiver sees
    }

    // Case-insensitive membership check against THIS character's own ignore list (RTK strcmpi).
    private bool IsIgnoring(string name) => _char.IgnoreList.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

    // "@ignore" (list) / "@ignore add <name>" / "@ignore remove <name>" — RTK's ignorelist_add/remove
    // (clif.c:7523/7551), ported as a chat command rather than the raw 0x0D-sub-opcode client packet
    // (clif_parseignore) since that's a UI-driven right-click action from a later client's context menu —
    // no evidence the 4.95 client has it at all. That's the bar for a chat command surviving in the player
    // tier: no native path exists, not merely "typing it is convenient".
    private void HandleIgnoreCommand(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1) { ListNames("Ignoring", _char.IgnoreList); return; }
        string sub = parts[0].ToLowerInvariant();
        if ((sub is "add" or "remove") && parts.Length < 2)
        { SendLog($"usage: @ignore {sub} <name>"); return; }

        switch (sub)
        {
            case "add":
                string addName = parts[1];
                if (addName.Equals(_char.Name, StringComparison.OrdinalIgnoreCase)) { SendLog("You can't ignore yourself."); return; }
                if (IsIgnoring(addName)) { SendLog($"{addName} is already on your ignore list."); return; }
                _char.IgnoreList.Add(addName);
                SaveChar();
                SendLog($"Ignoring {addName}.");
                break;
            case "remove":
                string remName = parts[1];
                int removed = _char.IgnoreList.RemoveAll(n => n.Equals(remName, StringComparison.OrdinalIgnoreCase));
                if (removed == 0) { SendLog($"{remName} isn't on your ignore list."); return; }
                SaveChar();
                SendLog($"No longer ignoring {remName}.");
                break;
            default:
                ListNames("Ignoring", _char.IgnoreList);
                break;
        }
    }

    // "@friend" (list, shows who's currently online) / "@friend add <name>" / "@friend remove <name>". No
    // RTK equivalent exists at all (see Character.Friends' doc) — a saved name list plus an online check,
    // nothing more; there's no cross-session login/logout notification, just a live lookup when listed.
    private void HandleFriendCommand(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1) { ListFriends(); return; }
        string sub = parts[0].ToLowerInvariant();
        if ((sub is "add" or "remove") && parts.Length < 2)
        { SendLog($"usage: @friend {sub} <name>"); return; }

        switch (sub)
        {
            case "add":
                string addName = parts[1];
                if (addName.Equals(_char.Name, StringComparison.OrdinalIgnoreCase)) { SendLog("You can't friend yourself."); return; }
                if (_char.Friends.Any(n => n.Equals(addName, StringComparison.OrdinalIgnoreCase))) { SendLog($"{addName} is already on your friend list."); return; }
                _char.Friends.Add(addName);
                SaveChar();
                SendLog($"Added {addName} to your friend list.");
                break;
            case "remove":
                string remName = parts[1];
                int removed = _char.Friends.RemoveAll(n => n.Equals(remName, StringComparison.OrdinalIgnoreCase));
                if (removed == 0) { SendLog($"{remName} isn't on your friend list."); return; }
                SaveChar();
                SendLog($"Removed {remName} from your friend list.");
                break;
            default:
                ListFriends();
                break;
        }
    }

    private void ListFriends()
    {
        if (_char.Friends.Count == 0) { SendLog("Your friend list is empty. Try: @friend add <name>"); return; }
        var online = _char.Friends.Where(n => _world.FindPlayer(n) is not null).ToList();
        ListNames("Friends", _char.Friends);
        SendLog(online.Count == 0 ? "(none online right now)" : $"Online now: {string.Join(", ", online)}");
    }

    private void ListNames(string label, List<string> names)
    {
        if (names.Count == 0) { SendLog($"{label} list is empty."); return; }
        SendLog($"{label}: {string.Join(", ", names)}");
    }

    /// <summary>Deliver a whisper's text to THIS session (the recipient), via the non-entity 0x0A channel
    /// (SendMiniText, type 0 = RTK's "Wisp/blue text") rather than 0x0D over-head speech: a whisper must
    /// reach the chat log with NO head bubble and work cross-map, and 0x0D is entity-bound (bubble always
    /// shown; delivering via our own entity would misattribute it as self-speech, delivering via the
    /// sender's entity id would silently vanish whenever sender/recipient aren't on the same map — the
    /// common case). 0x0A itself is already proven live (look-at names, item-pickup text both use it via
    /// SendMiniText's type=3); only the type=0/"blue chat window, not the status box" routing is unconfirmed.</summary>
    internal void ReceiveWhisper(string fromName, string msg) => SendMiniText($"{fromName}: {msg}", type: 0);

    /// <summary>Same status line as the existing <see cref="Notify"/> (used for trade's cross-session
    /// messages below) but on RTK's type=11 "group" minitext channel (see SendMiniText's type-table
    /// comment) — used for party join/leave/kick/disband broadcasts specifically.</summary>
    internal void NotifyGroup(string text) => SendMiniText(text, type: 11);

}
