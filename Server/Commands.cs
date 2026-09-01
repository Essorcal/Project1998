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
    /// <param name="Min">The lowest <see cref="AccessLevel"/> that may run it, compared against
    /// <see cref="Access"/>. Someone below it gets the same answer as for a name that doesn't exist, so the
    /// tooling stays invisible rather than merely locked.</param>
    /// <param name="Run">Receives the argument tail (see the class doc).</param>
    /// <param name="Args">Argument shape for @help, e.g. "&lt;name|id&gt; [amount]"; "" for none.</param>
    private sealed record Command(string[] Names, AccessLevel Min, Action<Session, string> Run, string Args, string Help);

    private static Command P(string names, Action<Session, string> run, string args, string help)
        => new(names.Split('|'), AccessLevel.Player, run, args, help);
    private static Command T(string names, Action<Session, string> run, string args, string help)
        => new(names.Split('|'), AccessLevel.Tester, run, args, help);
    private static Command G(string names, Action<Session, string> run, string args, string help)
        => new(names.Split('|'), AccessLevel.Gm, run, args, help);

    private static readonly Command[] CommandTable =
    {
        // ---- help / discovery -----------------------------------------------------------------------
        // The compact companion to @help: @help pages the DESCRIBED list (a screenful at a time), this shows
        // every name you can run at once. Player-tier on purpose — a plain player sees only their own handful.
        P("commands|cmds", (s, a) => s.ShowCommandIndex(), "", "compact index of every command you can use (names only; @help <word> for details)"),

        // ---- world / navigation ---------------------------------------------------------------------
        // A command earns its place here only when the 4.95 client has no native way to reach the feature:
        // whisper (0x19), mail (the 0x3B board window), party (0x2E) and trade (0x4A) all do, so their
        // chat-command fallbacks (and the old @friend/@ignore/@travel list-management fallbacks) were removed
        // rather than kept as a second, non-authentic UI. @music is the one player-tier survivor and is listed
        // down in the media block, with the rest of the 0x19 tooling.
        T("warp",    (s, a) => s.Warp(a),          "<map name|id> [x y]", "teleport"),
        T("go",      (s, a) => s.GoCmd(a),         "<x> <y>",             "jump to a tile on the map you're already on (bad/out-of-range coords -> 0 0)"),
        T("rez",     (s, a) => s.RezCmd(a),        "[username]",          "revive a player (or yourself) to full HP/MP (a full heal if already alive)"),
        T("approach",(s, a) => s.ApproachCmd(a),   "<username>",          "teleport to an online player"),
        G("where",   (s, a) => s.WhereCmd(a),      "[username]",          "where a player is, without going there (bare = everyone online, with locations)"),
        G("bring",   (s, a) => s.BringCmd(a),      "<username>",          "pull an online player to your side (the inverse of @approach)"),
        T("die",     (s, a) => s.DieCmd(),         "",                    "kill yourself (ghost form + real death penalties; @rez to get back up)"),
        T("clip",    (s, a) => s.ClipCmd(a),       "[0|1]",               "no-clip: walk through walls, mobs and players (this session only; warps still work)"),
        T("peace",   (s, a) => s.PeaceCmd(a),      "[0|1]",               "unprovoked mobs don't notice you (this session only; anything you attack still fights back)"),
        T("anywarp", (s, a) => s.AnyWarpCmd(a),    "[0|1]",               "use any warp despite level/mark/path/quest requirements (this session only; echoes the denial it waived)"),
        T("showwarps", (s, a) => s.ShowWarpsCmd(a), "[0|1] | look [warpFrame] [doorFrame]", "mark every warp + scripted doorway on the map (you only; follows across maps; lists destinations)"),
        T("maps",    (s, a) => s.ListMaps(a),      "[filter]",            "list/fuzzy-search maps"),
        G("mobs",    (s, a) => s.ListMobs(a),      "[filter]",            "list/fuzzy-search the mob registry"),
        G("summon",  (s, a) => s.Summon(a),        "<mob name|id>",       "spawn a registry mob in front of you"),
        G("reload",  (s, a) => s.ReloadContent(),  "",                    "hot-reload file-backed content"),
        G("restart", (s, a) => s.RestartCmd(a),    "[minutes] [reason] | cancel", "schedule a server restart, warning everyone as it nears"),
        G("announce",(s, a) => s.AnnounceCmd(a),   "<message>",           "system line to every player online (the restart-countdown channel)"),
        G("rabbit",  (s, a) => s.SpawnRabbit(),    "",                    "one wandering, killable rabbit"),
        G("kill",    (s, a) => s.KillMobs(),       "",                    "despawn every mob on this map"),

        // ---- character ------------------------------------------------------------------------------
        // These REBUILD the character (see Session.RespecTo): stats and the spellbook always come out exactly
        // right for the resulting class/level/mark/alignment. That's why there is no longer a "@spells" —
        // there is nothing left for it to do.
        T("lvl",     (s, a) => { var i = ParseInts(a); s.RespecLevel(i.Length > 0 ? i[0] : s._char.Level); },
                                                   "<1-99>",              "rebuild as level n: accurate stats + the matching spellbook"),
        // @lvl's complement, not its rival: @lvl REBUILDS at a level, @exp earns one — it's the only way to
        // exercise the real leveling path (the curve, multi-level carries, the Peasant wall, LevelUp gains).
        T("exp",     (s, a) => s.ExpCmd(a),        "<n> [kill]",          "gain experience through the real leveling path (kill = eligible for the totem-time bonus)"),
        T("mark",    (s, a) => s.SetMark(a),       "<0-3>",               "subpath rank on top of 99 (Il san…Sam san): its stats + spells"),
        T("class",   (s, a) => s.SetClass(a),      "<Warrior|Rogue|Mage|Poet|Peasant>", "set the class/path and rebuild for it"),
        T("dog",     (s, a) => s.SetDogFlag(a),    "[0|1]",               "the Dog-quest flag: unlocks Dog spells for a base class or NPC subpath"),
        // The Sage ladder has no other staff route: its five spells are locked to one NPC, so no rebuild
        // grants them, and buying it honestly is 500,000 gold across 360 real days of upgrade waits.
        T("sage",    (s, a) => s.SetSageRung(a),   "[0-5]",               "Share Wisdom rung: sets the spell AND clears the 90-day upgrade wait (bare @sage reports)"),
        // The generic quest-state pair (docs/common/Quest-Registry.md is the key catalogue). Most chains gate
        // on the LEGEND, not the stage, so re-testing one usually takes both commands.
        T("quest",   (s, a) => s.QuestCmd(a),      "[key] [stage]",       "read/set the raw quest registry (bare = dump your keys; stage 0 clears; non-numeric sets the string registry)"),
        T("legend",  (s, a) => s.LegendCmd(a),     "[key] [0 | <icon> <color> <text...>]", "list legend marks with their internal keys; remove one, or (re)create one by key"),
        // 0x0A's `type` decides which pane/colour a line lands in, and a wrong one is INVISIBLE from the
        // server side — the packet sends, the log says so, the client draws nothing. See TextChannelCmd.
        T("text",    (s, a) => s.TextChannelCmd(a), "[type] [message]",    "send yourself one 0x0A line on a channel; bare @text sweeps them to compare panes/colours"),
        T("align",   (s, a) => s.SetAlignment(a),  "<Unaligned|Kwisin|Mingken|Ohaeng|0-3>", "set sub-alignment and rebuild the book"),
        T("stats",   (s, a) => s.SetStatsCmd(a),   "<vita> <mana> <all> | <vita> <mana> <might> <grace> <will>",
                                                                          "set vitals and stats directly (overrides the curve)"),
        T("might",   (s, a) => s.SetBaseStat("might", a), "<n>",          "set base might"),
        T("will",    (s, a) => s.SetBaseStat("will", a),  "<n>",          "set base will"),
        T("grace",   (s, a) => s.SetBaseStat("grace", a), "<n>",          "set base grace"),
        T("hp",      (s, a) => s.SetMaxPool(hp: true, a), "<n>",          "set max HP (vita) and refill"),
        T("mp",      (s, a) => s.SetMaxPool(hp: false, a),"<n>",          "set max MP (mana) and refill"),
        T("nation",  (s, a) => s.SetNationCmd(a),   "<id>",               "set your nation crest (persists)"),
        T("totem",   (s, a) => s.SetTotemCmd(a),    "<id>",               "set your totem crest (persists)"),
        T("karma",   (s, a) => s.SetKarmaCmd(a),    "<value|tier>",       "set karma outright: a number, or a tier (cat, dog, angel, …)"),
        T("dispel",  (s, a) => s.DispelCmd(),       "",                   "strip every buff and debuff on you"),
        T("killtrack", (s, a) => s.KillTrackCmd(a), "[clear]",         "the 8-slot kill track the mythic alliances count (clear = what accepting one does)"),
        T("coins|gold", (s, a) => s.GiveCoinsCmd(a), "[n]",               "add coins to the purse"),
        T("ride|mount", (s, a) => s.ToggleMount(a), "[0|1]",              "get on/off the horse"),

        // ---- items ----------------------------------------------------------------------------------
        T("items",    (s, a) => s.ListItems(a),     "[filter]",           "list/fuzzy-search the item registry"),
        T("item",     (s, a) => s.GiveItemCmd(a),   "<name|id> [amount]", "summon an item into the bag"),
        T("take",     (s, a) => s.TakeItemCmd(a),   "<name|id> [amount|all]", "remove an item from the bag (worn gear untouched)"),
        T("dura",     (s, a) => s.DuraCmd(a),       "<name|id> <n>",      "set an item's durability, bag first then worn (repair/breakage testing)"),
        T("clearinv", (s, a) => s.ClearInventory(), "",                   "empty the bag and gear"),
        G("icons",    (s, a) => s.IconSweep(a),     "[start]",            "fill the bag with client Item.epf frames"),

        // ---- spells ---------------------------------------------------------------------------------
        // @lvl / @class / @mark / @align each resync the WHOLE book to what the character is entitled to, and
        // that stays the main path. @spell is the one additive exception — a single named ability, off-class
        // and off-level, for testing one thing. The bulk grants (@spells, @forgetspells) are still gone: they
        // could only ever grow the book, so a character who had been three classes carried all three.
        T("spell",   (s, a) => s.TeachSpellCmd(a),  "<name|id>",           "learn one ability outright, any class or level (a rebuild forgets it)"),

        // ---- moderation -----------------------------------------------------------------------------
        // GM-only, all of them. See Session.Moderation.cs: no duration means PERMANENT, and anything applied
        // to an online player takes effect immediately rather than at their next login.
        //
        // NOT here yet: GM invisibility (@hide), the observe-unseen half of moderation. Deliberately deferred
        // while everyone online is staff — there is no one to observe. Wanted once real players arrive.
        // Feasibility note for then: entity visibility is entirely server-side (keep the hidden GM out of the
        // entity broadcasts), but that touches every broadcast/PeerAt path, so it's a change, not a table row.
        G("ban",    (s, a) => s.BanCmd(a),    "<name> [minutes] [reason]", "ban an account (no duration = permanent); kicks them if online"),
        G("unban",  (s, a) => s.UnbanCmd(a),  "<name>",                    "lift an account ban"),
        G("mute",   (s, a) => s.MuteCmd(a),   "<name> [minutes] [reason]", "silence speech/whisper/subpath chat; '@' commands still work"),
        G("unmute", (s, a) => s.UnmuteCmd(a), "<name>",                    "lift a mute"),
        G("kick",   (s, a) => s.KickCmd(a),   "<name> [reason]",           "disconnect an online player (saves them first)"),
        G("banip",  (s, a) => s.BanIpCmd(a),  "<ip> [minutes] [reason] | remove <ip>", "ban a source address"),
        G("bans",   (s, a) => s.BansCmd(a),   "",                          "list everyone currently banned or muted"),
        G("modlog", (s, a) => s.ModLogCmd(a), "[n]",                       "recent moderation actions, newest first"),

        // ---- events ---------------------------------------------------------------------------------
        // Carnage is a HOSTED event, so its result is something a GM records rather than something the
        // server derives. Warrior Sun armor's first step reads the tally. See ArmorQuest.CarnageWinsReg.
        G("carnage", (s, a) => s.CarnageWinCmd(a), "<name> [n] | <name> = <n>", "record carnage victories (n<0 removes)"),

        // ---- config read-outs -----------------------------------------------------------------------
        // @npc is tester-tier since gaining its finder half: the warp-to-NPC is @warp-grade tooling, and the
        // bare readout it kept is harmless config info.
        T("npc",   (s, a) => s.NpcCmd(a),         "[name|id]", "bare: which NPCs are switched off; with a name: find that NPC and jump beside it"),
        G("craft", (s, a) => s.CraftToggleCmd(a), "", "crafting era-gate status (config + @reload to change)"),
        G("era",   (s, a) => s.EraCmd(a),         "", "target date + which dated content it includes"),

        // ---- sprite / appearance lab ----------------------------------------------------------------
        G("look",   (s, a) => s.LookOne(a),          "b0..b6",          "spawn a dummy with those appearance bytes"),
        G("row",    (s, a) => s.LookRow(a),          "<i> <lo> <hi>",   "sweep appearance byte i"),
        G("cre",    (s, a) => s.CreatureOne(a),      "[look] [hp] [color]", "spawn one real monster (0x07)"),
        G("crow",   (s, a) => s.CreatureRow(a),      "<lo> <hi> [step]", "sweep monster look ids across a row"),
        G("crecol", (s, a) => s.CreatureColorRow(a), "<look> [lo] [hi] [step]", "sweep the 0x07 colour byte"),
        G("mob",    (s, a) => s.MobOne(a),           "<look> [hp] [color]", "spawn one SHARED world monster everyone sees"),
        G("mobraw", (s, a) => s.MobRaw(a),           "<hi> <lo> [hp]",  "raw-sprite 0x16 probe (self-only)"),
        G("mobrow", (s, a) => s.MobRow(a),           "<lo> <hi> [step]", "sweep graphic ids (0x16, self-only)"),
        G("spawn",  (s, a) => s.SpawnCritters(a),    "[look] [hp]",     "a small pack around you"),
        G("dye",    (s, a) => s.DyeProbe(a),         "<n>",             "war-paint dye: set appearance[4]"),

        // ---- media ----------------------------------------------------------------------------------
        // @music is the one PLAYER-tier command in this block: it's a personal jukebox, not a probe. Every
        // 0x19 it sends goes to the caller's own session, so the loudest a player can be is loud at himself,
        // and the client's own Options menu has no way to pick a track. The rest of the block stays GM-only.
        P("music",    (s, a) => s.PlayMusicCmd(a),  "[name|id] [vol] [mp3|midi] | old|new | stop",
                                                    "play a music track, or pick the soundtrack (no argument lists them)"),
        G("snd",      (s, a) => s.SoundProbe(a),    "<id>",      "play a raw client sound id"),
        G("swingsnd", (s, a) => s.SetSwingSound(a), "<id>",      "set + audition the melee swing sfx"),
        G("fistsnd",  (s, a) => s.SetFistSound(a),  "<id>",      "set + audition the unarmed swing sfx"),
        G("hitsnd",   (s, a) => s.SetHitSound(a),   "<id>",      "set + audition the on-connect impact sfx"),
        G("mobact",   (s, a) => s.MobActionProbe(a), "<type> [time]", "set + preview the mob attack-pose action (0x1A) on the faced mob"),
        G("efx",      (s, a) => s.EffectProbe(a),   "<id>",      "play a raw Effect.tbl animation over self"),
        G("mtx",      (s, a) => s.MiniTextProbe(a), "<type>",    "audition a raw SendMiniText channel"),
        G("weather",  (s, a) => s.WeatherProbe(a),  "clear|rain|snow | raw <n>", "force this map's weather"),
        G("clock",    (s, a) => s.ClockCmd(a),      "[0-23 | real]", "read or pin the shared in-game hour (totem-time windows follow it; real = release)"),
        G("setting",  (s, a) => s.SettingCmd(a),    "[name] [on|off]", "read/set any 0x1b Options toggle"),
        G("doze",     (s, a) => s.DozeSelfCmd(a),   "[secs|off]", "put YOURSELF to sleep (Doze can't be self-targeted on the wire)"),

        // ---- protocol probes ------------------------------------------------------------------------
        G("hit",      (s, a) => s.HitProbe(a),          "[dmg]",   "0x13 over-head HP bar on the faced mob"),
        G("hpprobe",  (s, a) => s.StatHpTest(a),        "<cur> <max>", "diag: pin the maxHP/maxMP offsets (@hp is the setter)"),
        G("s",        (s, a) => s.StatProbe(a),         "<hexop> [hexflags]", "fire a sentinel status packet"),
        G("stg",      (s, a) => s.StatGradient(a),      "",        "self-describing gradient stats packet"),
        G("r6",       (s, a) => s.StatReplay6x(a),      "[hexop]", "replay a captured 6.x stats packet"),
        G("batch",    (s, a) => s.StatBatch(a),         "",        "sentinel probe over a curated opcode set"),
        G("sweep",    (s, a) => s.StatSweep(a),         "",        "disabled (crashes the client)"),
        G("mailflag", (s, a) => s.MailFlagProbe(a),     "<off> [valHex]", "sweep the 0x08 mail/parcel notify byte"),
        G("nat",      (s, a) => s.StatNation(a),        "<id>",    "sweep nation id -> HUD name"),
        G("users",    (s, a) => s.UserListCmd(a),       "[sort|sweep]", "0x36 user list (sweep = label every cell)"),
        G("askpic",   (s, a) => s.SendResendProfilePic(), "",      "0x49 - make the client re-upload users/<name>.epf"),
        G("totemsweep",(s, a) => s.StatTotem(a),        "<id>",    "diag: sweep totem id -> HUD name (@totem is the setter)"),
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
        G("look533",  (s, a) => s.Look533Cmd(a),        "[i v | clear]", "5.33: override one of the 11 appearance bytes and redraw"),
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

        // @help — special-cased before the table so it works at every tier. Accepts a page (as a suffix,
        // "@help2", or an argument, "@help 2") or a keyword filter ("@help item"). The full detail list runs
        // past the chat pane, so a bare @help pages it rather than dumping all ~90 lines (see ShowCommandHelp).
        if (name.Equals("help", StringComparison.OrdinalIgnoreCase)
            || (name.Length > 4 && name.StartsWith("help", StringComparison.OrdinalIgnoreCase)
                                && int.TryParse(name.AsSpan(4), out _)))
        {
            int page = 1; string filter = "";
            if (name.Length > 4) int.TryParse(name.AsSpan(4), out page);   // "@help2"
            else if (int.TryParse(args, out var p)) page = p;             // "@help 2"
            else filter = args;                                          // "@help item"
            ShowCommandHelp(filter, page);
            return true;
        }

        var access = Access;
        bool known = CommandsByName.TryGetValue(name, out var cmd);
        if (!known || access < cmd!.Min)
        {
            // Someone below the tier must not be able to tell a gated command from a typo, so both answers
            // are identical for them. Staff get the more useful message.
            if (known) Log.Info($"   -> denied {cmd!.Min} command from {access} '{_char.Name}': \"{text}\"");
            SendLog(access > AccessLevel.Player ? $"Unknown command '{Prefix}{name}'. Try {Prefix}help." : "Unknown command.");
            return true;
        }

        cmd.Run(this, args);
        return true;
    }

    /// <summary>How many detailed command lines one <c>@help</c> page shows. The client's chat pane holds
    /// only a handful of lines before the earliest scroll off unreachably, and a GM reaches ~90 commands, so
    /// the full list HAS to be paged. Sized to leave room for the header and footer line within the pane.</summary>
    private const int HelpPageSize = 12;

    /// <summary>"@help [page|filter]" — every command this session may actually run, so the list a player sees
    /// contains no staff tooling at all, and a tester's contains no GM tooling.
    ///
    /// The full detail list (name + args + description, one command per line) is what people want, but it runs
    /// past the chat pane. So a keyword filter shows just its matches in full, and an unfiltered list is PAGED:
    /// each page is a screenful of full-detail lines with a footer pointing at the next page ("@help2"). We
    /// page rather than drop the descriptions — the descriptions are the point of @help.</summary>
    private void ShowCommandHelp(string filter, int page)
    {
        var access = Access;
        var reachable = CommandTable.Where(c => access >= c.Min).ToList();

        // Keyword filter: show every match in full. A keyword narrows ~90 commands to a few, so this fits.
        if (filter.Length > 0)
        {
            var matches = reachable
                .Where(c => c.Names.Any(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase))
                         || c.Help.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0) { SendLog($"No command matches \"{filter}\"."); return; }
            SendLog($"Commands ({matches.Count}) matching \"{filter}\" — all start with '{Prefix}':");
            foreach (var c in matches) SendLog(HelpLine(c));
            return;
        }

        // No filter: page the whole reachable list, descriptions intact.
        int pages = Math.Max(1, (reachable.Count + HelpPageSize - 1) / HelpPageSize);
        page = Math.Clamp(page, 1, pages);
        int first = (page - 1) * HelpPageSize;
        int last = Math.Min(first + HelpPageSize, reachable.Count);

        SendLog($"Commands {first + 1}–{last} of {reachable.Count} (page {page}/{pages}) — all start with '{Prefix}':");
        foreach (var c in reachable.Skip(first).Take(HelpPageSize)) SendLog(HelpLine(c));
        if (page < pages)
            SendLog($"  more: '{Prefix}help{page + 1}' next page  |  '{Prefix}commands' the whole index  |  '{Prefix}help <word>' to search");
        else
            SendLog($"  end of list  |  '{Prefix}commands' the whole index  |  '{Prefix}help <word>' to search");
    }

    /// <summary>One detailed @help row: "@name/@alias &lt;args&gt; — what it does".</summary>
    private static string HelpLine(Command c)
    {
        string names = string.Join('/', c.Names.Select(n => Prefix + n));
        return $"  {names}{(c.Args.Length > 0 ? " " + c.Args : "")} — {c.Help}";
    }

    /// <summary>"@commands" / "@cmds" — the compact index: just the NAMES of every command this session can
    /// run, packed several per line so the whole roster fits the chat pane at once. The quick companion to the
    /// paged, described <see cref="ShowCommandHelp"/>: reach for this to SEE everything you have, then
    /// '@help &lt;word&gt;' to read what one does. Access-filtered like @help, so a player sees no staff tooling.</summary>
    private void ShowCommandIndex()
    {
        var access = Access;
        var names = CommandTable.Where(c => access >= c.Min).Select(c => Prefix + c.Names[0]).ToList();
        SendLog($"Commands ({names.Count}) — '{Prefix}help <word>' for what one does, '{Prefix}help' to page them with descriptions:");
        foreach (var chunk in names.Chunk(7))
            SendLog("  " + string.Join("  ", chunk));
    }
}
