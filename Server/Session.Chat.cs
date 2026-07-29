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

        // Appearance look-lab: drive 0x33 appearance bytes live so we can read the sprite id-space
        // off the screen instead of guessing.  "!look b0 b1 b2 b3 b4 b5 b6" spawns one test dummy with
        // those 7 bytes; "!row i lo hi" spawns a labeled row sweeping appearance[i] from lo..hi.
        if (text.StartsWith("!look", StringComparison.OrdinalIgnoreCase)) { LookOne(text); return; }
        if (text.StartsWith("!row", StringComparison.OrdinalIgnoreCase)) { LookRow(text); return; }
        // ---- REAL monsters via 0x07 (Monster.epf). check !crecol/!crow before the generic !cre ----
        if (text.StartsWith("!crecol", StringComparison.OrdinalIgnoreCase)) { CreatureColorRow(text); return; } // sweep the 0x07 color byte for one look id
        if (text.StartsWith("!crow", StringComparison.OrdinalIgnoreCase)) { CreatureRow(text); return; }  // sweep monster look ids
        if (text.StartsWith("!cre", StringComparison.OrdinalIgnoreCase)) { CreatureOne(text); return; }    // spawn one real monster [look] [hp] [color]
        // ---- navigation + data-driven content (registries loaded at startup from external data) ----
        if (text.StartsWith("!music", StringComparison.OrdinalIgnoreCase)) { PlayMusicCmd(text); return; } // play a specific track (0x19)
        // ---- whisper/tell: a private line to one online player (RTK clif_parsewisp) ----
        if (text.StartsWith("!whisper ", StringComparison.OrdinalIgnoreCase)) { HandleWhisper(text[9..]); return; }
        if (text.StartsWith("!w ", StringComparison.OrdinalIgnoreCase)) { HandleWhisper(text[3..]); return; }
        // "!ignore [add|remove] <name>" (RTK ignorelist_add/remove — blocks whispers both ways, see
        // DoWhisper) / "!friend [add|remove] <name>" (no RTK equivalent — see Character.Friends' doc).
        if (text.StartsWith("!ignore", StringComparison.OrdinalIgnoreCase)) { HandleIgnoreCommand(text); return; }
        if (text.StartsWith("!friend", StringComparison.OrdinalIgnoreCase)) { HandleFriendCommand(text); return; }
        // "!mailflag" MUST be checked before "!mail" (StartsWith("!mail") would otherwise swallow it).
        if (text.StartsWith("!mailflag", StringComparison.OrdinalIgnoreCase)) { MailFlagProbe(text); return; }  // sweep the 0x08 tail mail/parcel notify byte
        // "!mail" — RTK nmail (see HandleMailCommand's doc for why compose is chat-command-only).
        if (text.StartsWith("!mail", StringComparison.OrdinalIgnoreCase)) { HandleMailCommand(text); return; }
        // ---- party (RTK clif_addgroup/clif_leavegroup, §11) + trade (RTK clif_handitem &c., §11) ----
        if (text.StartsWith("!leaveparty", StringComparison.OrdinalIgnoreCase)) { LeaveParty(); return; }
        if (text.StartsWith("!party", StringComparison.OrdinalIgnoreCase)) { HandlePartyCommand(text); return; }  // "!party <name>" invite/kick, "!party" list
        if (text.StartsWith("!trade", StringComparison.OrdinalIgnoreCase)) { HandleTradeCommand(text); return; } // "!trade <name>" open the trade menu
        if (text.StartsWith("!travel", StringComparison.OrdinalIgnoreCase)) { _ = RunWorldMapMenuAsync(); return; }   // dialog fallback for §11m if the native screen ever regresses
        // "!wmpos <i> <x> <y>" -- live-tune destination i's clickable dot to field10 pixel (x,y) and re-open
        // the map so you can eyeball it against the real town. i is the index in Content.WorldDests (0=Kugnae,
        // 1=Buya, 2=Mythic Nexus, 3=Arctic Land, 4=KaMing's). The tweak is an ephemeral in-session override
        // (WorldDotOverride); once happy, bake the number into data/game-data/WorldMapDests.csv (DotX/DotY) +
        // !reload. "!wmpos" with no args lists the effective positions. See §11m.
        if (text.StartsWith("!wmpos", StringComparison.OrdinalIgnoreCase))
        {
            var dests = Content.WorldDests;
            (int X, int Y) DotOf(int i) => WorldDotOverride.TryGetValue(i, out var ov) ? ov : (dests[i].DotX, dests[i].DotY);
            var p = text["!wmpos".Length..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 3 && int.TryParse(p[0], out var wi) && int.TryParse(p[1], out var wx)
                && int.TryParse(p[2], out var wy) && wi >= 0 && wi < dests.Count)
            {
                WorldDotOverride[wi] = (Math.Clamp(wx, 0, 639), Math.Clamp(wy, 0, 479));
                var dot = DotOf(wi);
                SendMiniText($"{wi} {dests[wi].Name} -> ({dot.X},{dot.Y})  [bake into WorldMapDests.csv + !reload]");
                SendWorldMap("field10");
            }
            else
            {
                for (int k = 0; k < dests.Count; k++)
                {
                    var dot = DotOf(k);
                    SendMiniText($"{k} {dests[k].Name}: ({dot.X},{dot.Y})");
                }
            }
            return;
        }
        // "!wmtest [name]" -- native world-map screen (§11m) with an explicit background name (defaults to
        // field10 = "Map of the Kingdom", the overview world-map art. The framing bug that used to crash
        // this is fixed; this stays as a way to try alternate backgrounds (field1, title, other fieldNN).
        if (text.StartsWith("!wmtest", StringComparison.OrdinalIgnoreCase))
        {
            string bg = text.Length > "!wmtest".Length ? text["!wmtest".Length..].Trim() : "";
            if (bg.Length == 0) bg = "field10";
            SendWorldMap(bg);
            return;
        }
        // Real RTK typed chat commands (client-native, not our "!" debug prefix) — see speech.lua's /help list.
        if (text.StartsWith("/subpathchat ", StringComparison.OrdinalIgnoreCase)) { DoSubpathChat(text[13..].Trim()); return; }
        if (text.StartsWith("/sp ", StringComparison.OrdinalIgnoreCase)) { DoSubpathChat(text[4..].Trim()); return; }
        if (text.StartsWith("!warp", StringComparison.OrdinalIgnoreCase)) { Warp(text); return; }        // warp to a map by name/id [x y]
        if (text.StartsWith("!maps", StringComparison.OrdinalIgnoreCase)) { ListMaps(text); return; }    // list/fuzzy-search maps
        if (text.StartsWith("!mobs", StringComparison.OrdinalIgnoreCase)) { ListMobs(text); return; }    // list/fuzzy-search mobs (BEFORE !mob*)
        if (text.StartsWith("!summon", StringComparison.OrdinalIgnoreCase)) { Summon(text); return; }    // spawn a named mob from the registry
        if (text.StartsWith("!reload", StringComparison.OrdinalIgnoreCase)) { ReloadContent(); return; } // hot-reload file-backed content (no restart)
        // ---- items (check !items before !item) ----
        if (text.StartsWith("!icons", StringComparison.OrdinalIgnoreCase)) { IconSweep(text); return; }  // fill bag with client Item.epf frames N..N+26 (icon RE)
        if (text.StartsWith("!items", StringComparison.OrdinalIgnoreCase)) { ListItems(text); return; }  // list/fuzzy-search the item registry
        if (text.StartsWith("!item", StringComparison.OrdinalIgnoreCase)) { GiveItemCmd(text); return; } // summon a named item into the bag
        if (text.StartsWith("!clearinv", StringComparison.OrdinalIgnoreCase)) { ClearInventory(); return; } // empty the bag + gear
        // ---- mobs / combat (check !mobrow before !mob, !spawn before the catch-all !s) ----
        if (text.StartsWith("!rabbit", StringComparison.OrdinalIgnoreCase)) { SpawnRabbit(); return; }  // MVP: one wandering, killable rabbit
        if (text.StartsWith("!mobrow", StringComparison.OrdinalIgnoreCase)) { MobRow(text); return; }   // sweep graphic ids
        if (text.StartsWith("!mob", StringComparison.OrdinalIgnoreCase)) { MobOne(text); return; }       // spawn one creature
        if (text.StartsWith("!kill", StringComparison.OrdinalIgnoreCase)) { KillMobs(); return; }         // despawn all mobs
        if (text.StartsWith("!weapon", StringComparison.OrdinalIgnoreCase)) { SetWeapon(text); return; }  // equip weapon sprite
        if (text.StartsWith("!ride", StringComparison.OrdinalIgnoreCase) || text.StartsWith("!mount", StringComparison.OrdinalIgnoreCase)) { ToggleMount(text); return; } // get on/off the horse (form byte 3)
        if (text.StartsWith("!coins", StringComparison.OrdinalIgnoreCase) || text.StartsWith("!gold", StringComparison.OrdinalIgnoreCase)) { GiveCoinsCmd(text); return; }  // add coins to the purse
        if (text.StartsWith("!npc", StringComparison.OrdinalIgnoreCase)) { NpcToggleCmd(text); return; }   // show NPC on/off status (config file + !reload to change)
        if (text.StartsWith("!craft", StringComparison.OrdinalIgnoreCase)) { CraftToggleCmd(text); return; } // show crafting era-gate status (config file + !reload to change)
        if (text.StartsWith("!lvl", StringComparison.OrdinalIgnoreCase)) { var la = ParseInts(text); SetLevel(la.Length > 0 ? la[0] : _char.Level); return; }   // become level n with accurate stats (full HP/MP)
        if (text.StartsWith("!might", StringComparison.OrdinalIgnoreCase)) { SetBaseStat("might", text); return; } // set base might (test wear reqs)
        if (text.StartsWith("!class", StringComparison.OrdinalIgnoreCase)) { SetClass(text); return; }  // set the profile class/path line
        if (text.StartsWith("!spells", StringComparison.OrdinalIgnoreCase)) { TeachClassSpells(); return; }      // learn ALL my class's spells up to my level
        if (text.StartsWith("!learnspell", StringComparison.OrdinalIgnoreCase)) { LearnSpellCmd(text); return; } // learn one spell by name/id
        if (text.StartsWith("!forgetspells", StringComparison.OrdinalIgnoreCase)) { ForgetSpells(); return; }    // clear the spellbook
        if (text.StartsWith("!align", StringComparison.OrdinalIgnoreCase)) { SetAlignment(text); return; }        // set sub-alignment (Kwisin/Mingken/Ohaeng) for !spells
        if (text.StartsWith("!swingsnd", StringComparison.OrdinalIgnoreCase)) { SetSwingSound(text); return; }  // set + audition the melee swing sfx id
        if (text.StartsWith("!fistsnd", StringComparison.OrdinalIgnoreCase)) { SetFistSound(text); return; }  // set + audition the UNARMED swing sfx id
        if (text.StartsWith("!hitsnd", StringComparison.OrdinalIgnoreCase)) { SetHitSound(text); return; }  // set + audition the on-connect impact sfx id (0x13 hitSound byte)
        if (text.StartsWith("!snd", StringComparison.OrdinalIgnoreCase)) { SoundProbe(text); return; }   // play raw client sound ids (calibrate the NexusTK.snd id space)
        if (text.StartsWith("!efx", StringComparison.OrdinalIgnoreCase)) { EffectProbe(text); return; }  // play raw Effect.tbl animation ids over self (calibrate the effect id space)
        if (text.StartsWith("!mtx", StringComparison.OrdinalIgnoreCase)) { MiniTextProbe(text); return; }  // audition a raw SendMiniText type (0=wisp,3=mini/status,5=system,11=group,12=clan)
        if (text.StartsWith("!weather", StringComparison.OrdinalIgnoreCase)) { WeatherProbe(text); return; }  // force this map's weather (0=clear,1=rain,2=snow) — UNVERIFIED wire format, see SendWeather's doc
        if (text.StartsWith("!hit", StringComparison.OrdinalIgnoreCase)) { HitProbe(text); return; }      // 0x13 over-head HP bar + hit anim on the faced mob (calibrate NEXUS_HIT_CRIT)
        if (text.StartsWith("!spawn", StringComparison.OrdinalIgnoreCase)) { SpawnCritters(text); return; } // squirrel/rabbit pack
        if (text.StartsWith("!sweep", StringComparison.OrdinalIgnoreCase)) { StatSweep(text); return; }
        if (text.StartsWith("!batch", StringComparison.OrdinalIgnoreCase)) { StatBatch(text); return; }
        if (text.StartsWith("!r6", StringComparison.OrdinalIgnoreCase)) { StatReplay6x(text); return; }
        if (text.StartsWith("!stg", StringComparison.OrdinalIgnoreCase)) { StatGradient(text); return; }
        if (text.StartsWith("!leg", StringComparison.OrdinalIgnoreCase)) { SendProfileReplay6x(); return; }   // exact 6.x 0x39 replay
        if (text.StartsWith("!self", StringComparison.OrdinalIgnoreCase)) { SendSelfProfile(); return; }        // native 0x39 builder
        if (text.StartsWith("!ckm", StringComparison.OrdinalIgnoreCase)) { SendClickMarker(); return; }             // 0x34 with marker strings
        if (text.StartsWith("!click", StringComparison.OrdinalIgnoreCase)) { ClickProfileCmd(text); return; }  // native 0x34 click-profile: self, or "!click <name>" for another player
        if (text.StartsWith("!nat", StringComparison.OrdinalIgnoreCase)) { StatNation(text); return; }              // sweep nation id -> HUD name
        if (text.StartsWith("!totem", StringComparison.OrdinalIgnoreCase)) { StatTotem(text); return; }             // sweep totem id -> HUD name
        if (text.StartsWith("!time", StringComparison.OrdinalIgnoreCase))  { ShowTime(); return; }                  // report the game clock + totem-time status
        if (text.StartsWith("!dye", StringComparison.OrdinalIgnoreCase)) { DyeProbe(text); return; }                // calibrate the war-paint dye: !dye <n> sets appearance[4]
        if (text.StartsWith("!hurt", StringComparison.OrdinalIgnoreCase)) { HurtSelfCmd(text); return; }             // take n damage (after deduction) to test Sanctuary/Cunning
        if (text.StartsWith("!hp", StringComparison.OrdinalIgnoreCase)) { StatHpTest(text); return; }               // verify maxHP/maxMP offsets
        if (text.StartsWith("!s", StringComparison.OrdinalIgnoreCase)) { StatProbe(text); return; }

        // "A" (uppercase, exact) — remove ALL equipped items at once, same effect as clicking every worn
        // slot's 0x1F unequip in a row. Case-sensitive so ordinary chat ("a", "aww", …) still speaks
        // normally; nothing else in this client sends a bare capital letter as a message.
        if (text == "A") { UnequipAll(); return; }

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

    // ---- whisper/tell (RTK clif_parsewisp, clif.c:7644-7790) ---------------------------------------------
    // Native client input: Shift+' opens the whisper prompt, then a name + Enter, then a message + Enter.
    // LIVE-confirmed 2026-07-26 (real capture): op=0x19 body = dstlen(u8) dst_name[dstlen] msglen(u8)
    // msg[msglen] 00 — exactly RTK's wire layout. The "!whisper"/"!w" chat commands are kept as a fallback
    // entry point (same DoWhisper core) for anyone who'd rather type it. Message TEXT is RTK's real wording
    // wherever portable (not-found, map-silenced). Not modelled: per-player whisper on/off, silence/mute,
    // and ignore lists — none of those exist yet.
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

    // "!whisper <name> <message>" / "!w <name> <message>" — chat-command fallback for the same feature.
    private void HandleWhisper(string rest)
    {
        rest = rest.Trim();
        int sp = rest.IndexOf(' ');
        if (sp < 0) { SendLog("Whisper what to whom? Try: !whisper <name> <message>"); return; }
        DoWhisper(rest[..sp].Trim(), rest[(sp + 1)..].Trim());
    }

    private void DoWhisper(string name, string msg)
    {
        if (name.Length == 0 || msg.Length == 0) return;

        // RTK: map[sd->bl.m].cantalk == 1 blocks whisper with this exact line (only 2 maps set it).
        if (!Content.CanTalk(_char.Map)) { SendLog("Your voice is swept away by a strange wind."); return; }

        var target = _world.FindPlayer(name);
        if (target is null) { SendLog($"{name} is nowhere to be found."); return; }   // RTK's literal wording

        // RTK clif_isignore: a whisper is blocked if EITHER side has the other on their ignore list — not
        // just the recipient blocking the sender, but also the sender's own list (so you can't be pestered
        // by someone you've muted even if THEY never muted you). canwhisper's real wording on failure.
        if (IsIgnoring(target._char.Name) || target.IsIgnoring(_char.Name))
        { SendLog("They cannot hear you right now."); return; }

        target.ReceiveWhisper(_char.Name, msg);
        SendMiniText($"{_char.Name}: {msg}", type: 0);   // sender's own echo — same line the receiver sees
    }

    // Case-insensitive membership check against THIS character's own ignore list (RTK strcmpi).
    private bool IsIgnoring(string name) => _char.IgnoreList.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

    // "!ignore" (list) / "!ignore add <name>" / "!ignore remove <name>" — RTK's ignorelist_add/remove
    // (clif.c:7523/7551), ported as a chat command rather than the raw 0x0D-sub-opcode client packet
    // (clif_parseignore) since that's a UI-driven right-click action from a later client's context menu —
    // no evidence the 4.95 client has it at all (same "chat command primary" precedent as !party).
    private void HandleIgnoreCommand(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) { ListNames("Ignoring", _char.IgnoreList); return; }
        string sub = parts[1].ToLowerInvariant();
        if ((sub is "add" or "remove") && parts.Length < 3)
        { SendLog($"usage: !ignore {sub} <name>"); return; }

        switch (sub)
        {
            case "add":
                string addName = parts[2];
                if (addName.Equals(_char.Name, StringComparison.OrdinalIgnoreCase)) { SendLog("You can't ignore yourself."); return; }
                if (IsIgnoring(addName)) { SendLog($"{addName} is already on your ignore list."); return; }
                _char.IgnoreList.Add(addName);
                SaveChar();
                SendLog($"Ignoring {addName}.");
                break;
            case "remove":
                string remName = parts[2];
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

    // "!friend" (list, shows who's currently online) / "!friend add <name>" / "!friend remove <name>". No
    // RTK equivalent exists at all (see Character.Friends' doc) — a saved name list plus an online check,
    // nothing more; there's no cross-session login/logout notification, just a live lookup when listed.
    private void HandleFriendCommand(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) { ListFriends(); return; }
        string sub = parts[1].ToLowerInvariant();
        if ((sub is "add" or "remove") && parts.Length < 3)
        { SendLog($"usage: !friend {sub} <name>"); return; }

        switch (sub)
        {
            case "add":
                string addName = parts[2];
                if (addName.Equals(_char.Name, StringComparison.OrdinalIgnoreCase)) { SendLog("You can't friend yourself."); return; }
                if (_char.Friends.Any(n => n.Equals(addName, StringComparison.OrdinalIgnoreCase))) { SendLog($"{addName} is already on your friend list."); return; }
                _char.Friends.Add(addName);
                SaveChar();
                SendLog($"Added {addName} to your friend list.");
                break;
            case "remove":
                string remName = parts[2];
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
        if (_char.Friends.Count == 0) { SendLog("Your friend list is empty. Try: !friend add <name>"); return; }
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
