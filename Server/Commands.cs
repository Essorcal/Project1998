using Shared;

namespace Server;

/// <summary>
/// The chat-command surface: the prefix, the name -> handler table, and the dispatcher. Everything about
/// "what is a command" lives here; <see cref="Session.HandleChat"/> just asks this to try the message first.
///
/// WHY A TABLE. This used to be ~70 <c>text.StartsWith(...)</c> lines in HandleChat, each embedding the
/// prefix as a literal (as did every usage string and comment that named a command — some 500 mentions in
/// all, which is what made changing the prefix a research project). Worse, ORDER was load-bearing: the
/// plural item lister had to be tested before the singular item granter, the mail-flag probe before the
/// mailbox, the spellbook before the one-letter status probe — otherwise the shorter name swallowed the
/// longer one, and adding a command whose name was a prefix of another silently broke that other one.
/// Exact-name lookup removes that whole class of bug, and <see cref="Prefix"/> makes the prefix itself a
/// one-character change.
///
/// CONTRACT: a handler receives the ARGUMENT TAIL — the message with the prefix, the command name and the
/// separating space already removed, then trimmed. "@item Fine Sword 3" reaches GiveItemCmd as
/// "Fine Sword 3". Handlers must not re-parse the command name out of it.
/// </summary>
public sealed partial class Session
{
    /// <summary>The one definition of the chat-command prefix. Everything user-visible builds its text from
    /// this, so it can be changed here alone.</summary>
    internal const char Prefix = '@';

    /// <param name="Names">Canonical name first, then aliases. Lower-case; matching is case-insensitive.</param>
    /// <param name="GmOnly">Gated on <see cref="IsGm"/>. A non-GM gets the same answer as for a name that
    /// doesn't exist, so the tooling stays invisible rather than merely locked.</param>
    /// <param name="Run">Receives the argument tail (see the class doc).</param>
    /// <param name="Args">Argument shape for @help, e.g. "&lt;name|id&gt; [amount]"; "" for none.</param>
    private sealed record Command(string[] Names, bool GmOnly, Action<Session, string> Run, string Args, string Help);

    private static Command P(string names, Action<Session, string> run, string args, string help)
        => new(names.Split('|'), false, run, args, help);
    private static Command G(string names, Action<Session, string> run, string args, string help)
        => new(names.Split('|'), true, run, args, help);

    private static readonly Command[] CommandTable =
    {
        // ---- player commands ------------------------------------------------------------------------
        P("whisper|w",  (s, a) => s.HandleWhisper(a),          "<name> <message>", "private message one online player"),
        P("ignore",     (s, a) => s.HandleIgnoreCommand(a),    "[add|remove <name>]", "block whispers both ways"),
        P("friend",     (s, a) => s.HandleFriendCommand(a),    "[add|remove <name>]", "saved name list + online check"),
        P("mail",       (s, a) => s.HandleMailCommand(a),      "[read|send …]",    "mailbox (board 0)"),
        P("party",      (s, a) => s.HandlePartyCommand(a),     "[name]",           "invite/kick, or list the group"),
        P("leaveparty", (s, a) => s.LeaveParty(),              "",                 "leave the current group"),
        P("trade",      (s, a) => s.HandleTradeCommand(a),     "<name>",           "open the trade menu with a player"),
        P("travel",     (s, a) => { _ = s.RunWorldMapMenuAsync(); }, "",           "world-map travel (dialog fallback)"),
        P("time",       (s, a) => s.ShowTime(),                "",                 "game clock + totem-time status"),

        // ---- world / navigation ---------------------------------------------------------------------
        G("warp",    (s, a) => s.Warp(a),          "<map name|id> [x y]", "teleport"),
        G("maps",    (s, a) => s.ListMaps(a),      "[filter]",            "list/fuzzy-search maps"),
        G("mobs",    (s, a) => s.ListMobs(a),      "[filter]",            "list/fuzzy-search the mob registry"),
        G("summon",  (s, a) => s.Summon(a),        "<mob name|id>",       "spawn a registry mob in front of you"),
        G("reload",  (s, a) => s.ReloadContent(),  "",                    "hot-reload file-backed content"),
        G("rabbit",  (s, a) => s.SpawnRabbit(),    "",                    "one wandering, killable rabbit"),
        G("kill",    (s, a) => s.KillMobs(),       "",                    "despawn every mob on this map"),

        // ---- character ------------------------------------------------------------------------------
        G("lvl",     (s, a) => { var i = ParseInts(a); s.SetLevel(i.Length > 0 ? i[0] : s._char.Level); },
                                                   "<1-99>",              "become level n with accurate stats"),
        G("stats",   (s, a) => s.SetStatsCmd(a),   "<vita> <mana> <all> | <vita> <mana> <might> <grace> <will>",
                                                                          "set vitals and stats directly"),
        G("might",   (s, a) => s.SetBaseStat("might", a), "<n>",          "set base might"),
        G("class",   (s, a) => s.SetClass(a),      "<Warrior|Rogue|Mage|Poet|Peasant>", "set the class/path"),
        G("mark",    (s, a) => s.SetMark(a),       "<0-5>",               "set the subpath rank"),
        G("align",   (s, a) => s.SetAlignment(a),  "<Unaligned|Kwisin|Mingken|Ohaeng|0-3>", "set sub-alignment"),
        G("coins|gold", (s, a) => s.GiveCoinsCmd(a), "[n]",               "add coins to the purse"),
        G("ride|mount", (s, a) => s.ToggleMount(a), "[0|1]",              "get on/off the horse"),
        G("weapon",  (s, a) => s.SetWeapon(a),     "<sprite>",            "set the weapon appearance byte"),
        G("hurt",    (s, a) => s.HurtSelfCmd(a),   "<n>",                 "take n damage (after deduction)"),

        // ---- items ----------------------------------------------------------------------------------
        G("items",    (s, a) => s.ListItems(a),     "[filter]",           "list/fuzzy-search the item registry"),
        G("item",     (s, a) => s.GiveItemCmd(a),   "<name|id> [amount]", "summon an item into the bag"),
        G("clearinv", (s, a) => s.ClearInventory(), "",                   "empty the bag and gear"),
        G("icons",    (s, a) => s.IconSweep(a),     "[start]",            "fill the bag with client Item.epf frames"),
        G("iteminfo", (s, a) => s.ItemInfoCmd(a),   "<slot> | mode <m> | sep <s>", "fire the examine reply; switch how it's rendered"),
        G("bind",     (s, a) => s.BindItemCmd(a),   "<slot> [name|off]",  "bind a bag item to a character (or clear it)"),

        // ---- spells ---------------------------------------------------------------------------------
        G("spells",        (s, a) => s.TeachClassSpells(), "",            "learn every class spell up to your level"),
        G("learnspell",    (s, a) => s.LearnSpellCmd(a),   "<name|id>",   "learn one spell"),
        G("forgetspells",  (s, a) => s.ForgetSpells(),     "",            "clear the spellbook"),

        // ---- config read-outs -----------------------------------------------------------------------
        G("npc",   (s, a) => s.NpcToggleCmd(a),   "", "which NPCs are switched off (config + @reload to change)"),
        G("craft", (s, a) => s.CraftToggleCmd(a), "", "crafting era-gate status (config + @reload to change)"),

        // ---- sprite / appearance lab ----------------------------------------------------------------
        G("look",   (s, a) => s.LookOne(a),          "b0..b6",          "spawn a dummy with those appearance bytes"),
        G("row",    (s, a) => s.LookRow(a),          "<i> <lo> <hi>",   "sweep appearance byte i"),
        G("cre",    (s, a) => s.CreatureOne(a),      "[look] [hp] [color]", "spawn one real monster (0x07)"),
        G("crow",   (s, a) => s.CreatureRow(a),      "<lo> <hi> [step]", "sweep monster look ids across a row"),
        G("crecol", (s, a) => s.CreatureColorRow(a), "<look> [lo] [hi] [step]", "sweep the 0x07 colour byte"),
        G("mob",    (s, a) => s.MobOne(a),           "<hi> <lo> [hp]",  "spawn one creature by raw sprite"),
        G("mobrow", (s, a) => s.MobRow(a),           "<lo> <hi> [step]", "sweep graphic ids"),
        G("spawn",  (s, a) => s.SpawnCritters(a),    "[look] [hp]",     "a small pack around you"),
        G("dye",    (s, a) => s.DyeProbe(a),         "<n>",             "war-paint dye: set appearance[4]"),

        // ---- media ----------------------------------------------------------------------------------
        G("music",    (s, a) => s.PlayMusicCmd(a),  "<name|id>", "play a track (0x19)"),
        G("snd",      (s, a) => s.SoundProbe(a),    "<id>",      "play a raw client sound id"),
        G("swingsnd", (s, a) => s.SetSwingSound(a), "<id>",      "set + audition the melee swing sfx"),
        G("fistsnd",  (s, a) => s.SetFistSound(a),  "<id>",      "set + audition the unarmed swing sfx"),
        G("hitsnd",   (s, a) => s.SetHitSound(a),   "<id>",      "set + audition the on-connect impact sfx"),
        G("efx",      (s, a) => s.EffectProbe(a),   "<id>",      "play a raw Effect.tbl animation over self"),
        G("mtx",      (s, a) => s.MiniTextProbe(a), "<type>",    "audition a raw SendMiniText channel"),
        G("weather",  (s, a) => s.WeatherProbe(a),  "clear|rain|snow | raw <n>", "force this map's weather"),
        G("setting",  (s, a) => s.SettingCmd(a),    "[name] [on|off]", "read/set any 0x1b Options toggle"),

        // ---- protocol probes ------------------------------------------------------------------------
        G("hit",      (s, a) => s.HitProbe(a),          "[dmg]",   "0x13 over-head HP bar on the faced mob"),
        G("hp",       (s, a) => s.StatHpTest(a),        "<cur> <max>", "pin the maxHP/maxMP offsets"),
        G("s",        (s, a) => s.StatProbe(a),         "<hexop> [hexflags]", "fire a sentinel status packet"),
        G("stg",      (s, a) => s.StatGradient(a),      "",        "self-describing gradient stats packet"),
        G("r6",       (s, a) => s.StatReplay6x(a),      "[hexop]", "replay a captured 6.x stats packet"),
        G("batch",    (s, a) => s.StatBatch(a),         "",        "sentinel probe over a curated opcode set"),
        G("sweep",    (s, a) => s.StatSweep(a),         "",        "disabled (crashes the client)"),
        G("mailflag", (s, a) => s.MailFlagProbe(a),     "<off> [valHex]", "sweep the 0x08 mail/parcel notify byte"),
        G("nat",      (s, a) => s.StatNation(a),        "<id>",    "sweep nation id -> HUD name"),
        G("users",    (s, a) => s.UserListCmd(a),       "[sort|sweep]", "0x36 user list (sweep = label every cell)"),
        G("askpic",   (s, a) => s.SendResendProfilePic(), "",      "0x49 - make the client re-upload users/<name>.epf"),
        G("totem",    (s, a) => s.StatTotem(a),         "<id>",    "sweep totem id -> HUD name"),
        G("self",     (s, a) => s.SendSelfProfile(),    "",        "native 0x39 self-profile"),
        G("leg",      (s, a) => s.SendProfileReplay6x(), "",       "exact 6.x 0x39 replay"),
        G("ckm",      (s, a) => s.SendClickMarker(),    "",        "0x34 with marker strings"),
        G("click",    (s, a) => s.ClickProfileCmd(a),   "[name]",  "native 0x34 click-profile"),
        G("boardobj", (s, a) => s.BoardObjProbe(),      "",        "board-sign calibration for the faced tile"),
        G("wmpos",    (s, a) => s.WorldMapPosCmd(a),    "<i> <x> <y>", "live-tune a world-map destination dot"),
        G("wmtest",   (s, a) => s.WorldMapTestCmd(a),   "[bg]",    "native world-map screen with a given background"),
        G("pkt",      (s, a) => s.RawPacketCmd(a),      "<hexop> [tokens] | add | send | show | clear | file <name>",
                                                        "send a raw server->client packet"),
        G("delreason", (s, a) => s.DelReasonSweep(a),   "[lo] [hi]", "sweep the 0x10 reason byte for a silent one"),
    };

    /// <summary>name -> command, aliases included. Built once; a duplicate name is a programming error and
    /// throws at startup rather than silently shadowing.</summary>
    private static readonly Dictionary<string, Command> CommandsByName = BuildCommandIndex();

    private static Dictionary<string, Command> BuildCommandIndex()
    {
        var map = new Dictionary<string, Command>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in CommandTable)
            foreach (var n in c.Names)
                if (!map.TryAdd(n, c))
                    throw new InvalidOperationException($"duplicate chat command '{n}'");
        return map;
    }

    /// <summary>Split a chat message into command name and ARGUMENT TAIL. False when it isn't a command
    /// (no prefix, or prefix alone), in which case the message is ordinary speech. Pure and static so the
    /// dispatch split can be executed in a test rather than only read — the args-tail contract is exactly
    /// where the move to the command table went wrong once already.</summary>
    internal static bool SplitCommand(string text, out string name, out string args)
    {
        name = args = "";
        if (text.Length < 2 || text[0] != Prefix) return false;
        int sp = text.IndexOf(' ');
        name = sp < 0 ? text[1..] : text[1..sp];
        args = sp < 0 ? "" : text[(sp + 1)..].Trim();
        return true;
    }

    /// <summary>Try to run <paramref name="text"/> as a chat command. Returns false if it isn't one (no
    /// prefix), in which case the caller treats it as ordinary speech.</summary>
    private bool TryRunCommand(string text)
    {
        if (!SplitCommand(text, out string name, out string args)) return false;

        if (name.Equals("help", StringComparison.OrdinalIgnoreCase)) { ShowCommandHelp(args); return true; }

        bool known = CommandsByName.TryGetValue(name, out var cmd);
        if (!known || (cmd!.GmOnly && !IsGm))
        {
            // A non-GM must not be able to tell a GM command from a typo, so both answers are identical
            // for them. A GM gets the more useful message.
            if (known) Log.Info($"   -> denied GM command from non-GM '{_char.Name}': \"{text}\"");
            SendLog(IsGm ? $"Unknown command '{Prefix}{name}'. Try {Prefix}help." : "Unknown command.");
            return true;
        }

        cmd.Run(this, args);
        return true;
    }

    /// <summary>"@help [filter]" — every command this session may actually run, so the list a non-GM sees
    /// contains no GM tooling at all.</summary>
    private void ShowCommandHelp(string filter)
    {
        bool gm = IsGm;
        var rows = CommandTable
            .Where(c => !c.GmOnly || gm)
            .Where(c => filter.Length == 0 || c.Names.Any(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase))
                                           || c.Help.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (rows.Count == 0) { SendLog($"No command matches \"{filter}\"."); return; }

        SendLog($"Commands ({rows.Count}) — all start with '{Prefix}':");
        foreach (var c in rows)
        {
            string names = string.Join('/', c.Names.Select(n => Prefix + n));
            SendLog($"  {names}{(c.Args.Length > 0 ? " " + c.Args : "")} — {c.Help}");
        }
    }
}
