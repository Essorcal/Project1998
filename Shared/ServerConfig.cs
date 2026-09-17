using System.Globalization;

namespace Shared;

/// <summary>Where a knob belongs in <c>docs/common/Configuration.md</c> and in the startup banner.</summary>
public enum ConfigArea
{
    /// <summary>Where the four deployment roots live on disk.</summary>
    Paths,
    /// <summary>What the logger writes and how big it lets the file get.</summary>
    Logging,
    /// <summary>The accept path: the bind address, PROXY protocol and the peers allowed to speak it, the
    /// handshake budget, and the bounds on one outbound socket write.</summary>
    Transport,
    /// <summary>Abuse control: the connection-admission caps on each front door and the failed-login
    /// throttle. These are the numbers a flood runs into, so they are named here one by one rather than
    /// built from a prefix at runtime.</summary>
    Abuse,
    /// <summary>The handoff gate and the addresses a redirect names.</summary>
    Login,
    /// <summary>Per-session behaviour the operator may retune without a rebuild.</summary>
    Session,
    /// <summary>The world heartbeat and the watchdog that reports a slow one.</summary>
    World,
    /// <summary>Process-health probes: thread-pool scheduling latency and client input silence.</summary>
    Diagnostics,
    /// <summary>The status document the launcher polls.</summary>
    Status,
    /// <summary>Staff rosters, unioned with the files under the state directory.</summary>
    Staff,
    /// <summary>4.95 self-walk calibration — diagnostics, not gameplay values.</summary>
    Movement,
    /// <summary>Tile/object translation and the world-light probes.</summary>
    Rendering,
    /// <summary>Knobs that USED to be environment variables and are now data. Setting one warns.</summary>
    Retired,
}

/// <summary>
/// The one declaration of every <c>P1998_*</c> environment knob this server reads: its name, its type, its
/// default and what it does, stated once, in one place, and resolved ONCE per process.
///
/// <para><b>Why this exists.</b> Configuration used to be 131 distinct <c>P1998_*</c> literals spread over 22
/// files, of which 32 were mentioned anywhere a person could find them. A knob's name, its default and its
/// meaning lived in three different places — an inline <c>TryParse</c>, a comment above it, and (for a lucky
/// third) a line in the README — so the only reliable way to answer "what does this deployment actually do"
/// was to grep the source. Four of them were re-read from the environment on EVERY call. This file replaces
/// the grep: one declaration per knob, a generated reference built from those declarations, and a startup
/// banner that prints the effective value of each with its source.</para>
///
/// <para><b>Resolve once, immutable after.</b> <see cref="Current"/> reads the environment the first time it
/// is touched and never again. Every call site that used to parse a variable per packet now reads a field.
/// The process environment is not a live control surface and never was: half the call sites already cached
/// their value in a <c>static readonly</c>, so the four that did not were simply inconsistent with the rest.
/// Nothing here is a secret — the whole set is addresses, sizes, timeouts, file paths and diagnostic
/// toggles — so <see cref="Describe"/> prints values verbatim; that was checked knob by knob when this file
/// was written, and a future knob that IS a secret must not be printed.</para>
///
/// <para>The retired per-file content overrides remain declared here only so a deployment that still sets
/// one receives a startup warning. They no longer affect path resolution; <see cref="GameDataDir"/> is the
/// single content-path override.</para>
/// </summary>
public sealed class ServerConfig
{
    // ---- declarations ----------------------------------------------------------------------------------
    // One entry per knob. Name, area, default, doc string. Nothing else declares a P1998_ literal.

    /// <summary>Retired per-file content overrides. The 68 CSV tables and four Lua inputs now all resolve
    /// beneath <c>P1998_GAME_DATA</c>; these names remain only to warn operators who still set them.</summary>
    public static IReadOnlyList<string> RetiredContentOverrides { get; } = Array.AsReadOnly(new[]
    {
        "P1998_OBJECT_FLAG_OVERRIDES", "P1998_OBJ533_FIX", "P1998_TILE533_MAP", "P1998_MAP_INDEX",
        "P1998_MOB_FLEES", "P1998_MOB_STATIONARY", "P1998_MOBS", "P1998_ITEMS", "P1998_WARPS",
        "P1998_SPAWNS", "P1998_AREASPAWNS", "P1998_AREASPAWNS_TRAP", "P1998_AREASPAWNS_CRAFT",
        "P1998_SERVER_TUNING", "P1998_ERA_FEATURES", "P1998_NPCS", "P1998_MINORQUESTS",
        "P1998_SHOPSTOCK", "P1998_SHOPBUYSFROM", "P1998_PATHS", "P1998_LEVELEXP",
        "P1998_SPELL_LEVELS", "P1998_SPELLS", "P1998_SPELL_FX", "P1998_SPELL_TEXT",
        "P1998_SPELL_COSTS", "P1998_MOB_PALETTES_5X", "P1998_ARMOR_DYE_RAMPS", "P1998_MAPS_FULL",
        "P1998_MOB_DROPS", "P1998_CRAFTING_TOGGLES", "P1998_WARP_QUEST_LOCKS", "P1998_ARMOR_QUESTS",
        "P1998_MYTHIC_CAVES", "P1998_MYTHIC_ALLIANCES", "P1998_ARENA_DOORS",
        "P1998_EVENT_CAVE_TIERS", "P1998_EVENT_CAVES", "P1998_MUSIC_TRACKS", "P1998_MAP_BGM",
        "P1998_INNS", "P1998_FORAGE", "P1998_HARVEST", "P1998_MOB_SPELLS", "P1998_MOB_CHATTER",
        "P1998_MOB_SPAWN_RULES", "P1998_MOB_BOSSES", "P1998_PATHHALLS", "P1998_GATEWAY",
        "P1998_WORLDMAP_DESTS", "P1998_WORLDMAP_TRIGGERS", "P1998_FALLROOMS",
        "P1998_AMBUSH_BURSTS", "P1998_AMBUSH_CONFIG", "P1998_BOARD_LOCATIONS",
        "P1998_SHOP_CATALOGUES", "P1998_SPELL_PARAMS", "P1998_SPELL_VERBS", "P1998_ITEM_PARAMS",
        "P1998_ITEM_VERBS", "P1998_NPC_DIALOG", "P1998_MOB_AI", "P1998_PETS", "P1998_WEAPON_PROCS",
        "P1998_TRAPS", "P1998_MORPHS", "P1998_SPELL_MODS", "P1998_NPC_ABILITIES",
        "P1998_PATH_GROWTH", "P1998_DOOR_OBJECTS", "P1998_DOORS", "P1998_MAP_CELLS",
    });

    /// <summary>Every declared knob. The generated reference and the startup banner both walk this list, in
    /// this order, so adding a knob below is the whole of adding a knob.</summary>
    public static class Knobs
    {
        // --- paths ---
        public static readonly PathKnob GameData = new(
            "P1998_GAME_DATA", ConfigArea.Paths, "game-data",
            "Authored content: the CSVs, the Lua, the .map terrain, SObj.tbl. Read-only at runtime; a deploy " +
            "replaces it wholesale.");
        public static readonly PathKnob State = new(
            "P1998_STATE", ConfigArea.Paths, "state",
            "Live instance state: the SQLite database, the character store, the staff rosters. The whole of " +
            "what a backup must capture.");
        public static readonly PathKnob Logs = new(
            "P1998_LOGS", ConfigArea.Paths, "logs",
            "Append-only stdout captures. Grows without bound, regenerable, never backed up.");
        public static readonly PathKnob Run = new(
            "P1998_RUN", ConfigArea.Paths, "run",
            "Deploy-to-server control triggers (restart_at, reload_now), consumed and deleted by the running " +
            "process. Not state.");
        public static readonly StringKnob MapsDir = new(
            "P1998_MAPS", ConfigArea.Paths, "",
            "First directory searched for the 4.x headerless `.map` terrain files. Blank searches only the " +
            "built-in list: `<game-data>/maps`, then the two Windows client installs. Point this at a " +
            "client's `Maps` directory on a host that has no client installed. The value is used as given, " +
            "not trimmed.",
            defaultText: "*(blank — search `<game-data>/maps` then the client installs)*");
        public static readonly StringKnob SObjTable = new(
            "P1998_SOBJ", ConfigArea.Paths, "",
            "First path tried for the client's `SObj.tbl` object-collision table. Blank tries " +
            "`<game-data>/SObj.tbl`, then the RTK-Server copy. Prefer the client extract: its object-id " +
            "space is the one the `.map` files index. The value is used as given, not trimmed.",
            defaultText: "*(blank — try `<game-data>/SObj.tbl` then the RTK-Server copy)*");

        // --- logging ---
        public static readonly OptionalBoolKnob LogWire = new(
            "P1998_LOG_WIRE", ConfigArea.Logging,
            "Hex-dump every frame. Unset takes the ENTRY POINT's default, which differs by process: ON in the " +
            "game server (the backbone of the protocol RE work, no credentials on that channel) and OFF in the " +
            "login server (4.95's cipher is a fixed published XOR, so a dump writes plaintext passwords). " +
            "Must be EXACTLY `0` or `1`: surrounding whitespace is not trimmed for this one knob, so a " +
            "padded `\" 1\"` warns and keeps the process default rather than turning the dump on.");
        public static readonly OptionalLongKnob LogMaxBytes = new(
            "P1998_LOG_MAX_BYTES", ConfigArea.Logging,
            "Rotate the log file at this many bytes. Unset takes the entry point's default: 64MB in the game " +
            "server, 32MB in the login server.", min: 1);

        // --- transport ---
        public static readonly BoolKnob TrustProxy = new(
            "P1998_TRUST_PROXY", ConfigArea.Transport, false,
            "Read and trust a PROXY protocol v2 header on every accepted connection. Off means the accept path " +
            "behaves exactly as it always has, so a bare clone with no proxy in front never waits for a header " +
            "that is not coming.");
        public static readonly IntKnob ProxyHeaderMs = new(
            "P1998_PROXY_HEADER_MS", ConfigArea.Transport, 5_000,
            "How long a trusted peer has to deliver its PROXY header before the connection is dropped. Separate " +
            "from the handshake budget, which covers the first GAME packet and cannot start until this is done.",
            min: 1);
        public static readonly StringKnob ProxyAllow = new(
            "P1998_PROXY_ALLOW", ConfigArea.Transport, "127.0.0.0/8,::1/128",
            "Peers allowed to send a PROXY header, as comma-separated addresses or CIDR blocks. This gate is the " +
            "entire security model — a header is just bytes, so anyone who can reach the port could otherwise " +
            "claim any source address. A containerised proxy needs its bridge network added.");
        public static readonly StringKnob BindAddress = new(
            "P1998_BIND", ConfigArea.Transport, "",
            "Local interface both listeners bind to. Blank binds every interface (0.0.0.0), which is right " +
            "for a real deployment. Set a specific LAN address to keep the servers OFF loopback, which " +
            "matters only when the launcher's loopback proxy runs on the same box and would otherwise " +
            "compete with the server for the client's connection. An address this server cannot parse " +
            "falls back to 0.0.0.0 — the parse lives in `Shared/NetBind.cs`, so a bad value is not " +
            "reported on the startup warning line.",
            defaultText: "*(blank — 0.0.0.0, every interface)*");
        public static readonly IntKnob HandshakeMs = new(
            "P1998_HANDSHAKE_MS", ConfigArea.Transport, 15_000,
            "Slow-loris budget: a freshly accepted connection must send its first VALID framed packet within " +
            "this long or it is dropped. Only the first packet is gated, so an in-world player standing AFK " +
            "is never disconnected. Shared by both processes. 15s is far more than a real client needs.",
            min: 1);
        public static readonly IntKnob SlowSendMs = new(
            "P1998_SLOW_SEND_MS", ConfigArea.Transport, 250,
            "Warn when a frame waits this long to reach the socket, or when the socket write itself takes " +
            "that long. 0 disables the warning. 250ms is well under the ~1s a player would notice, so the " +
            "log names the stall before anyone complains about it.",
            min: 0);
        public static readonly IntKnob LoginWriteMs = new(
            "P1998_LOGIN_WRITE_MS", ConfigArea.Transport, 10_000,
            "How long ONE socket write on the LOGIN channel may take before the peer is dropped. The login " +
            "conversation is a few hundred bytes: a peer that cannot accept them inside ten seconds is not a " +
            "client anyone is waiting on. The game channel has no per-write bound — there the queue filling " +
            "is what drops a stuck peer — so this knob does not apply to it.",
            min: 1);

        // --- abuse control: connection admission and the failed-login throttle ---
        //
        // ConnGuard used to build these ten names from a prefix at runtime ($"P1998_{prefix}_MAXCONN"), which
        // is why none of them appeared in any reference: there was no literal to find. The cross product is
        // written out instead. P1998_LOGIN_EXEMPT_LOOPBACK is declared ONCE and read by two consumers — the
        // accept-path gates here and the failed-login throttle below — which is what the old code did too.
        public static readonly IntKnob LoginMaxConn = new(
            "P1998_LOGIN_MAXCONN", ConfigArea.Abuse, 2_000,
            "Concurrent connections the LOGIN process will hold before it sheds load by accepting and " +
            "immediately closing. Load-shedding, not a player cap: past the ceiling an overload costs a " +
            "closed socket rather than exhausted threads and memory.",
            min: 1);
        public static readonly IntKnob LoginPerIp = new(
            "P1998_LOGIN_PERIP", ConfigArea.Abuse, 8,
            "Live LOGIN connections one address may hold at once. 8 is sized to reliably SUPPORT about two " +
            "players per address — two steady sockets, the brief login-to-game overlap, a lingering half-open " +
            "ghost — without being a hard two-player quota. Raise it for NAT'd addresses sharing more players.",
            min: 1);
        public static readonly IntKnob LoginRate = new(
            "P1998_LOGIN_RATE", ConfigArea.Abuse, 30,
            "LOGIN connections one address may OPEN per window, which is what catches a connect/disconnect " +
            "churn flood that the concurrent cap alone would not. 30 per 10s already covers two players " +
            "logging in and reconnecting with retries.",
            min: 1);
        public static readonly IntKnob LoginRateWindowMs = new(
            "P1998_LOGIN_RATEWIN_MS", ConfigArea.Abuse, 10_000,
            "Length of that fixed rate window on the LOGIN front door, in milliseconds.",
            min: 1);
        public static readonly BoolKnob LoginExemptLoopback = new(
            "P1998_LOGIN_EXEMPT_LOOPBACK", ConfigArea.Abuse, true,
            "Exempt loopback from BOTH login-side per-address gates: the accept path's per-IP and rate caps, " +
            "and the failed-login throttle. Local dev, the client test box and a same-box login-to-game hop " +
            "all originate from 127.0.0.1 and must never be throttled; loopback still counts toward the " +
            "global cap so load-shedding stays uniform. Set 0 on a host where loopback is not trusted.");
        public static readonly IntKnob GameMaxConn = new(
            "P1998_GAME_MAXCONN", ConfigArea.Abuse, 2_000,
            "The same concurrent-connection load-shedding ceiling for the GAME process.",
            min: 1);
        public static readonly IntKnob GamePerIp = new(
            "P1998_GAME_PERIP", ConfigArea.Abuse, 8,
            "Live GAME connections one address may hold at once. Same sizing as the login door.",
            min: 1);
        public static readonly IntKnob GameRate = new(
            "P1998_GAME_RATE", ConfigArea.Abuse, 30,
            "GAME connections one address may OPEN per window.",
            min: 1);
        public static readonly IntKnob GameRateWindowMs = new(
            "P1998_GAME_RATEWIN_MS", ConfigArea.Abuse, 10_000,
            "Length of that fixed rate window on the GAME front door, in milliseconds.",
            min: 1);
        public static readonly BoolKnob GameExemptLoopback = new(
            "P1998_GAME_EXEMPT_LOOPBACK", ConfigArea.Abuse, true,
            "Exempt loopback from the GAME accept path's per-IP and rate caps. Loopback still counts toward " +
            "the global cap.");
        public static readonly IntKnob LoginFails = new(
            "P1998_LOGIN_FAILS", ConfigArea.Abuse, 10,
            "Failed logins one source IP may spend inside the window before further attempts are refused " +
            "without touching the password hash.", min: 1);
        public static readonly LongKnob LoginFailWindowMs = new(
            "P1998_LOGIN_FAIL_WINDOW_MS", ConfigArea.Abuse, 300_000,
            "Length of that rolling failure window, in milliseconds. A successful login clears the counter.",
            min: 1);

        // --- login / handoff ---
        public static readonly StringKnob GameHost = new(
            "P1998_GAME_HOST", ConfigArea.Login, "127.0.0.1",
            "The game server's address as the CLIENT must reach it (a.b.c.d), for the login handoff redirect. " +
            "Not the bind address. Anything unparseable falls back to loopback rather than sending players " +
            "somewhere unreachable.");
        public static readonly StringKnob LoginHost = new(
            "P1998_LOGIN_HOST", ConfigArea.Login, "",
            "The login server's address for the exit-to-select bounce. Blank falls back to P1998_GAME_HOST, " +
            "because the common deployment runs both processes on one box and behind a proxy both front doors " +
            "share one public address.");
        public static readonly OptionalIntKnob LoginPort = new(
            "P1998_LOGIN_PORT", ConfigArea.Login,
            "Login port the exit-to-select bounce names. Unset derives it from the port the session arrived on, " +
            "because the channels are PAIRED by client version — bouncing a 5.33 player onto the 4.95 login " +
            "would round-trip them straight back.",
            defaultText: "paired with the arrival channel", min: 1);
        public static readonly BoolKnob EnforceHandoff = new(
            "P1998_ENFORCE_HANDOFF", ConfigArea.Login, true,
            "Refuse a game connection whose single-use handoff token does not verify. 0 downgrades the failure " +
            "to a warning and lets the connection in — a fallback for a deployment with a token problem, and " +
            "the only thing standing between the game port and a client claiming any username.");
        public static readonly BoolKnob AllowTofu = new(
            "P1998_ALLOW_TOFU", ConfigArea.Login, false,
            "TRUST SWITCH — leave it off. On, a login for a name that exists in `characters` with NO " +
            "`accounts` row adopts whatever password was sent as that character's password, permanently. It " +
            "is the escape hatch for the handful of characters that predate the accounts table: set it, log " +
            "in once as that character, turn it back off. While it is on, anyone who guesses such a name " +
            "claims the character. It never applies to a name with no character at all, so it cannot create " +
            "an account.");

        // --- session ---
        public static readonly IntKnob AutoSaveMs = new(
            "P1998_AUTOSAVE_MS", ConfigArea.Session, 15_000,
            "Ceiling on how often a dirty character is flushed to the store, and so on worst-case data loss in " +
            "a hard crash. The session's own read loop and World's idle sweep both use this one cadence.",
            min: 1);
        public static readonly BoolKnob CastQueue = new(
            "P1998_CAST_QUEUE", ConfigArea.Session, true,
            "Hold over-budget casts until the next action window instead of discarding them, so a held cast key " +
            "lands as one animation and one sound rather than an audible flam. 0 restores the plain drop-gate.");
        public static readonly BoolKnob PassEnforce = new(
            "P1998_PASS", ConfigArea.Session, true,
            "Server-side passability (collision). 0 lets players walk through anything — an escape hatch for a " +
            "map whose 4.x top-2-bits polarity turns out wrong.");

        // --- world heartbeat ---
        public static readonly IntKnob TickMs = new(
            "P1998_TICK_MS", ConfigArea.World, 333,
            "The world heartbeat in milliseconds — the smallest action interval the world can express at " +
            "all. Mob timers are carried, not reset, so a 2000ms creature moves every 2000ms whatever this " +
            "is; what changes is GRANULARITY. 333 divides Sute's observed 333/333/rest rhythm exactly. The " +
            "tick body runs proportionally more often, so raising this back is the lever if the slow-tick " +
            "watchdog starts firing.",
            min: 50);
        public static readonly OptionalIntKnob SlowTickMs = new(
            "P1998_SLOW_TICK_MS", ConfigArea.World,
            "A tick this slow — work OR scheduling delay, in milliseconds — gets a diagnostic line. 0 " +
            "disables the watchdog. Unset derives a quarter of the heartbeat, which is well clear of normal " +
            "jitter and low enough to catch a stall long before a player would call it lag, and which is why " +
            "it is derived rather than a fixed number: retuning the heartbeat retunes this with it.",
            defaultText: "a quarter of the heartbeat (83 at the default 333)", min: 0);

        // --- process-health probes ---
        public static readonly IntKnob PoolLagMs = new(
            "P1998_POOL_LAG_MS", ConfigArea.Diagnostics, 100,
            "Report thread-pool scheduling latency at or above this many milliseconds. 0 disables the probe " +
            "entirely. Read alongside SLOW SEND: high pool latency with a high queued time means starvation, " +
            "and something is blocking pool threads.",
            min: 0);
        public static readonly IntKnob SilentMs = new(
            "P1998_SILENT_MS", ConfigArea.Diagnostics, 4_000,
            "Report a client that has sent NOTHING for this long while the server is still actively sending " +
            "to it. That asymmetry is the exact shape of \"the mobs keep moving but my character cannot act\". " +
            "0 disables the probe.",
            min: 0);

        // --- the status document ---
        public static readonly StringKnob StatusFile = new(
            "P1998_STATUS_FILE", ConfigArea.Status, "",
            "Where to publish the small document the launcher polls for \"N online\". Blank publishes " +
            "`<run>/status.json`. The single value `-` disables publishing entirely. Trimmed.",
            trim: true, defaultText: "`<run>/status.json`");
        public static readonly IntKnob StatusMs = new(
            "P1998_STATUS_MS", ConfigArea.Status, 10_000,
            "How often that document is rewritten, in milliseconds. The launcher polls every 30s, so the " +
            "10s default means the number is never more than one poll stale. Values below the 1000ms floor " +
            "are refused: this is a file write on a timer, not a metric.",
            min: 1_000);
        public static readonly StringKnob StatusMessage = new(
            "P1998_STATUS_MESSAGE", ConfigArea.Status, "",
            "Optional operator note published beside the player count. Blank leaves the launcher's own " +
            "wording. Trimmed.",
            trim: true, defaultText: "*(blank — the launcher's own wording)*");

        // --- staff rosters ---
        public static readonly StringKnob Gms = new(
            "P1998_GMS", ConfigArea.Staff, "",
            "GM account names, comma-separated. UNIONED with `<state>/gms.txt` rather than replacing it, so " +
            "this adds a GM for one run without editing the file. Entries are trimmed and blank ones " +
            "dropped. With no GM configured anywhere, the GM tier is disabled for everyone.");
        public static readonly StringKnob Testers = new(
            "P1998_TESTERS", ConfigArea.Staff, "",
            "Tester account names, comma-separated, unioned with `<state>/testers.txt` on the same rules as " +
            "the GM roster above. Tester is the tier below GM.");

        // --- 4.95 movement calibration ---
        public static readonly IntKnob WalkMs = new(
            "P1998_V495_WALK_MS", ConfigArea.Movement, 200,
            "Delay before the 0x04 that completes a 4.95 self-walk. The client caps local prediction at ~180ms " +
            "and then freezes, so 0x04 must land just after that window. 0 is the old same-frame slide.");
        public static readonly IntKnob SelfMove = new(
            "P1998_V495_SELF_MOVE", ConfigArea.Movement, 7,
            "Self-walk drive mode with fast-move ON. 0 = 0x04 only (no legs); 1 = legacy 0x0C(dest)+0x04; " +
            "2 = 0x0C(source)+0x04; 3 = send nothing; 5 = delay then 0x04; 7 = nothing on a good walk, 0x04 only " +
            "as a correction (default, RTK-faithful).");
        public static readonly IntKnob AckMs = new(
            "P1998_V495_ACK_MS", ConfigArea.Movement, 360,
            "Delay before the mode-5 unblock 0x04. Must be at least the client's local walk animation (~360ms) " +
            "or the legs are cut short.");
        public static readonly IntKnob SlowMove = new(
            "P1998_V495_SLOW_MOVE", ConfigArea.Movement, 5,
            "Self-walk drive mode with fast-move OFF. 0 = 0x04 only; 1 = 0x0C(dest); 2 = 0x0C(source)+delayed " +
            "0x04; 3 = 0x0C(dest)+delayed 0x04; 4 = 0x0C(source) only; 5 = the 0x26 self-walk primitive (default).");
        public static readonly BoolKnob RealmCenter = new(
            "P1998_V495_REALM", ConfigArea.Movement, false,
            "Set the 0x15 realm-center flag, which locks the client camera dead-centre on the character.");
        public static readonly BoolKnob FastMoveDefault = new(
            "P1998_V495_FASTMOVE_DEFAULT", ConfigArea.Movement, false,
            "Pre-world-entry placeholder for a session's fast-move flag. The real value is restored from the " +
            "character (SettingFlags bit 9); 1 forces the old assumed-ON placeholder, which was the desync bug.");
        public static readonly BoolKnob FastMoveTrustToggle = new(
            "P1998_V495_FASTMOVE_TRUST_TOGGLE", ConfigArea.Movement, true,
            "Drive the walk reply off the session's fast-move flag (toggled in lockstep via 0x1b/09). 0 forces " +
            "the old always-0x26 behaviour.");
        public static readonly BoolKnob PushMap = new(
            "P1998_V495_PUSHMAP", ConfigArea.Movement, true,
            "Push a margin strip of terrain ahead of a walking 5.33 client instead of waiting for it to ask. " +
            "0 disables the push entirely and restores the visible black cells.");
        public static readonly IntKnob PushGraceSteps = new(
            "P1998_V495_PUSHGRACE", ConfigArea.Movement, 0,
            "Steps to defer that push for. 0 pushes on every step; a higher value restores the old deferral to " +
            "the client's own requests.", min: 0);

        // --- rendering / tile diagnostics ---
        public static readonly IntKnob LightValue = new(
            "P1998_LIGHT", ConfigArea.Rendering, 232,
            "The map light/darkness value sent on the 0x15, 0..65535. 232 is proven bright on 4.95.");
        public static readonly EnumKnob LightFormat = new(
            "P1998_LIGHT_FMT", ConfigArea.Rendering, "beu16", ["beu16", "leu16", "u8"],
            "How that light value is encoded on the 0x15. Sweeping this isolates whether a client reads the " +
            "field at a different width or endianness.");
        public static readonly StringKnob MapDiag = new(
            "P1998_MAP_DIAG", ConfigArea.Rendering, "",
            "5.33 terrain-render probe for the 0x06 stream. Blank sends real map tiles. \"sweep\" ramps the " +
            "ground index across the visible rect, \"solid:N\" fills with index N (both untranslated), " +
            "\"ground:N\" puts the 4.x ground WORD N through the real translation, \"passtest:N\" fills the " +
            "pass bits with N.", normalize: true);
        public static readonly IntKnob GroundOff533 = new(
            "P1998_TILE_OFF_533", ConfigArea.Rendering, 0,
            "Uniform shift applied to sheet-1 ground indices on 5.33. Should stay 0; it exists so a future " +
            "sheet revision can be probed without a rebuild.");
        public static readonly IntKnob GroundOff495 = new(
            "P1998_TILE_OFF_495", ConfigArea.Rendering, 0,
            "The same shift for 4.95 ground indices.");
        public static readonly IntKnob ObjectOff533 = new(
            "P1998_OBJ_OFF_533", ConfigArea.Rendering, 0,
            "Uniform shift applied to SObj.tbl object ids on 5.33.");
        public static readonly IntKnob ObjectOff495 = new(
            "P1998_OBJ_OFF_495", ConfigArea.Rendering, 0,
            "The same shift for 4.95 object ids.");
        public static readonly OptionalIntKnob LegacyTileOff = new(
            "P1998_TILE_OFF", ConfigArea.Rendering,
            "Superseded single knob that shifts GROUND ONLY, on both client versions. Kept so existing run " +
            "scripts keep working; when set it overrides both per-version ground offsets.",
            defaultText: "unset (per-version offsets apply)");
        public static readonly EnumKnob ObjFix533 = new(
            "P1998_OBJ_FIX_533", ConfigArea.Rendering, "free", ["off", "free", "decor", "all", "structural"],
            "How much of the 5.33 object-collision workaround to apply. \"off\" none; \"free\" only proven " +
            "visually identical substitutions (default — nothing on screen changes); \"decor\" also blanks " +
            "fully walkable decoration; \"all\" (alias \"structural\") also blanks real directional blockers, " +
            "which deletes visible structures.");
        public static readonly IntKnob EfxWireOffset = new(
            "P1998_EFX_WIRE_OFFSET", ConfigArea.Rendering, 0,
            "Adjustment added to the effect id on the 0x29 spell-animation packet. Proven 0 live; kept as a " +
            "calibration hatch.");
        public static readonly StringKnob Look533Extra = new(
            "P1998_LOOK533_EXTRA", ConfigArea.Rendering, "",
            "Defaults for the two 5.33 appearance slots 4.95 has no equivalent for, as \"hair,tail\". Blank " +
            "sends 0 for both.");

        // --- retired: gameplay values that are now data ---
        public static readonly RetiredKnob HitCrit = new(
            "P1998_HIT_CRIT", "HitCrit",
            "The 0x13 hit-type byte, which selects the over-head hit overlay (RTK uses 33 normal / 255 crit).");
        public static readonly RetiredKnob HealCrit = new(
            "P1998_HEAL_CRIT", "HealCrit",
            "The 0x13 critical byte a HEAL carries. RTK passes 0.");
        public static readonly RetiredKnob DeathDelayMs = new(
            "P1998_DEATH_DELAY_MS", "DeathDespawnMs",
            "How long a killed mob's corpse is held before the 0x0E despawn.");
        public static readonly RetiredKnob SpellbookCap = new(
            "P1998_SPELLBOOK_CAP", "SpellBookCap",
            "Slots the client's spellbook array can hold before a teach would overrun it.");

        /// <summary>Declaration order — the generated reference and the startup banner both walk this.</summary>
        public static readonly IReadOnlyList<ConfigKnob> All =
        [
            GameData, State, Logs, Run, MapsDir, SObjTable,
            LogWire, LogMaxBytes,
            TrustProxy, ProxyHeaderMs, ProxyAllow, BindAddress, HandshakeMs, SlowSendMs, LoginWriteMs,
            LoginMaxConn, LoginPerIp, LoginRate, LoginRateWindowMs, LoginExemptLoopback,
            GameMaxConn, GamePerIp, GameRate, GameRateWindowMs, GameExemptLoopback,
            LoginFails, LoginFailWindowMs,
            GameHost, LoginHost, LoginPort, EnforceHandoff, AllowTofu,
            AutoSaveMs, CastQueue, PassEnforce,
            TickMs, SlowTickMs,
            PoolLagMs, SilentMs,
            StatusFile, StatusMs, StatusMessage,
            Gms, Testers,
            WalkMs, SelfMove, AckMs, SlowMove, RealmCenter,
            FastMoveDefault, FastMoveTrustToggle, PushMap, PushGraceSteps,
            LightValue, LightFormat, MapDiag,
            GroundOff533, GroundOff495, ObjectOff533, ObjectOff495, LegacyTileOff, ObjFix533,
            EfxWireOffset, Look533Extra,
            HitCrit, HealCrit, DeathDelayMs, SpellbookCap,
        ];
    }

    // ---- the resolved snapshot -------------------------------------------------------------------------

    /// <summary>One knob's resolved state: what the environment said, what the server is using, and whether
    /// the value came from the environment or the declared default.</summary>
    public sealed record Entry(ConfigKnob Knob, string? Raw, object? Value, string Text,
                               bool FromEnvironment, string? Warning);

    private readonly Dictionary<string, Entry> _byName;
    private readonly IReadOnlyList<(string Name, string Raw)> _setRetiredContentOverrides;

    /// <summary>Every knob's resolved state, in declaration order.</summary>
    public IReadOnlyList<Entry> Entries { get; }

    private ServerConfig(Func<string, string?> source)
    {
        var entries = new List<Entry>(Knobs.All.Count);
        foreach (var knob in Knobs.All)
        {
            string? raw = source(knob.Name);
            var (value, text, warning) = knob.Resolve(raw);
            // "From the environment" means the environment actually supplied the value in force. A blank or
            // whitespace-only variable supplied nothing, and a REJECTED one supplied nothing either — the
            // declared default is what the process is running with, so the banner must not tag it
            // "(environment)" or count it in "N set from the environment". The `!!` warning line printed
            // above the banner is where a rejected value gets named.
            entries.Add(new Entry(knob, raw, value, text,
                                  FromEnvironment: !string.IsNullOrWhiteSpace(raw) && warning is null,
                                  warning));
        }
        Entries = entries;
        _byName = entries.ToDictionary(e => e.Knob.Name, StringComparer.Ordinal);
        _setRetiredContentOverrides = RetiredContentOverrides
            .Select(name => (Name: name, Raw: source(name)))
            .Where(entry => !string.IsNullOrEmpty(entry.Raw))
            .Select(entry => (entry.Name, entry.Raw!))
            .ToArray();
    }

    /// <summary>Resolve a configuration against an arbitrary source. Exists so a test can pin a knob's
    /// default, an override and a bad value without mutating the process environment, which is shared with
    /// every other test in the run and frozen by the time most of them start.</summary>
    public static ServerConfig From(Func<string, string?> source) => new(source);

    /// <summary>Resolve a configuration against the process environment.</summary>
    public static ServerConfig FromEnvironment() => new(Environment.GetEnvironmentVariable);

    private static readonly object Gate = new();
    private static ServerConfig? _current;

    /// <summary>This process's configuration, read from the environment the first time it is touched and
    /// never again.</summary>
    public static ServerConfig Current => Volatile.Read(ref _current) ?? Build();

    private static ServerConfig Build()
    {
        lock (Gate)
        {
            var built = _current ?? new ServerConfig(Environment.GetEnvironmentVariable);
            Volatile.Write(ref _current, built);
            return built;
        }
    }

    /// <summary>Re-read the environment. The ONE legitimate caller is the test harness, which redirects
    /// <c>P1998_STATE</c> at a temp directory in a module initializer and must not be defeated by a snapshot
    /// some earlier static initializer happened to take first. Nothing in a server process may call it: every
    /// call site has already cached the old answer.</summary>
    internal static void ReloadForTests()
    {
        lock (Gate) Volatile.Write(ref _current, new ServerConfig(Environment.GetEnvironmentVariable));
    }

    private Entry Of(ConfigKnob knob) => _byName[knob.Name];

    private T Get<T>(ConfigKnob knob) => (T)Of(knob).Value!;

    private T? GetOrNull<T>(ConfigKnob knob) where T : struct => (T?)Of(knob).Value;

    // ---- typed accessors -------------------------------------------------------------------------------

    /// <summary>&lt;root&gt;/game-data, or the override.</summary>
    public string GameDataDir => Get<string>(Knobs.GameData);
    /// <summary>&lt;root&gt;/state, or the override.</summary>
    public string StateDir => Get<string>(Knobs.State);
    /// <summary>&lt;root&gt;/logs, or the override.</summary>
    public string LogsDir => Get<string>(Knobs.Logs);
    /// <summary>&lt;root&gt;/run, or the override.</summary>
    public string RunDir => Get<string>(Knobs.Run);
    /// <summary>First directory searched for <c>.map</c> terrain, or "" for the built-in search list.</summary>
    public string MapsDir => Get<string>(Knobs.MapsDir);
    /// <summary>First path tried for <c>SObj.tbl</c>, or "" for the built-in candidates.</summary>
    public string SObjTable => Get<string>(Knobs.SObjTable);

    /// <summary>Wire dump, or null to take the entry point's per-process default.</summary>
    public bool? LogWire => GetOrNull<bool>(Knobs.LogWire);
    /// <summary>Rotation limit, or null to take the entry point's per-process default.</summary>
    public long? LogMaxBytes => GetOrNull<long>(Knobs.LogMaxBytes);

    /// <summary>Read a PROXY v2 header from trusted peers.</summary>
    public bool TrustProxy => Get<bool>(Knobs.TrustProxy);
    /// <summary>Budget for a trusted peer's PROXY header.</summary>
    public int ProxyHeaderMs => Get<int>(Knobs.ProxyHeaderMs);
    /// <summary>Raw allow-list text; <c>ProxyProtocol</c> owns the CIDR parse.</summary>
    public string ProxyAllow => Get<string>(Knobs.ProxyAllow);
    /// <summary>Configured bind address as text, or "" for every interface; <c>NetBind</c> owns the parse.</summary>
    public string BindAddress => Get<string>(Knobs.BindAddress);
    /// <summary>Budget for a new connection's first valid frame.</summary>
    public int HandshakeMs => Get<int>(Knobs.HandshakeMs);
    /// <summary>Slow-send warning threshold; 0 disables the warning.</summary>
    public int SlowSendMs => Get<int>(Knobs.SlowSendMs);
    /// <summary>Per-write bound on the login channel.</summary>
    public int LoginWriteMs => Get<int>(Knobs.LoginWriteMs);

    /// <summary>Login-door concurrent connection ceiling.</summary>
    public int LoginMaxConn => Get<int>(Knobs.LoginMaxConn);
    /// <summary>Login-door live connections per address.</summary>
    public int LoginPerIp => Get<int>(Knobs.LoginPerIp);
    /// <summary>Login-door connection opens per address per window.</summary>
    public int LoginRate => Get<int>(Knobs.LoginRate);
    /// <summary>Length of the login-door rate window.</summary>
    public int LoginRateWindowMs => Get<int>(Knobs.LoginRateWindowMs);
    /// <summary>Game-door concurrent connection ceiling.</summary>
    public int GameMaxConn => Get<int>(Knobs.GameMaxConn);
    /// <summary>Game-door live connections per address.</summary>
    public int GamePerIp => Get<int>(Knobs.GamePerIp);
    /// <summary>Game-door connection opens per address per window.</summary>
    public int GameRate => Get<int>(Knobs.GameRate);
    /// <summary>Length of the game-door rate window.</summary>
    public int GameRateWindowMs => Get<int>(Knobs.GameRateWindowMs);
    /// <summary>Exempt loopback from the game door's per-address gates.</summary>
    public bool GameExemptLoopback => Get<bool>(Knobs.GameExemptLoopback);

    /// <summary>Game host a redirect names, as configured text.</summary>
    public string GameHost => Get<string>(Knobs.GameHost);
    /// <summary>Login host a bounce names, falling back to <see cref="GameHost"/> when unset.</summary>
    public string LoginHost
    {
        get
        {
            string configured = Get<string>(Knobs.LoginHost);
            return string.IsNullOrWhiteSpace(configured) ? GameHost : configured;
        }
    }
    /// <summary>Login port a bounce names, or null to pair it with the arrival channel.</summary>
    public int? LoginPort => GetOrNull<int>(Knobs.LoginPort);
    /// <summary>Refuse a game connection with an invalid handoff token.</summary>
    public bool EnforceHandoff => Get<bool>(Knobs.EnforceHandoff);
    /// <summary>Failed logins allowed per source IP per window.</summary>
    public int LoginFails => Get<int>(Knobs.LoginFails);
    /// <summary>Length of that window in milliseconds.</summary>
    public long LoginFailWindowMs => Get<long>(Knobs.LoginFailWindowMs);
    /// <summary>Exempt loopback from the login door's per-address gates and the failed-login throttle.</summary>
    public bool LoginExemptLoopback => Get<bool>(Knobs.LoginExemptLoopback);
    /// <summary>Adopt a password for a legacy character that has no accounts row. A trust switch.</summary>
    public bool AllowTofu => Get<bool>(Knobs.AllowTofu);

    /// <summary>Dirty-character flush cadence in milliseconds.</summary>
    public int AutoSaveMs => Get<int>(Knobs.AutoSaveMs);
    /// <summary>Hold over-budget casts rather than dropping them.</summary>
    public bool CastQueue => Get<bool>(Knobs.CastQueue);
    /// <summary>Enforce server-side collision.</summary>
    public bool PassEnforce => Get<bool>(Knobs.PassEnforce);

    /// <summary>The world heartbeat in milliseconds.</summary>
    public int TickMs => Get<int>(Knobs.TickMs);
    /// <summary>Slow-tick threshold, or null to derive a quarter of <see cref="TickMs"/>.</summary>
    public int? SlowTickMs => GetOrNull<int>(Knobs.SlowTickMs);

    /// <summary>Pool-latency warning threshold; 0 disables the probe.</summary>
    public int PoolLagMs => Get<int>(Knobs.PoolLagMs);
    /// <summary>Input-silence warning threshold; 0 disables the probe.</summary>
    public int SilentMs => Get<int>(Knobs.SilentMs);

    /// <summary>Status document path as configured, or "" for <c>&lt;run&gt;/status.json</c>. <c>-</c>
    /// disables publishing.</summary>
    public string StatusFile => Get<string>(Knobs.StatusFile);
    /// <summary>How often the status document is rewritten.</summary>
    public int StatusMs => Get<int>(Knobs.StatusMs);
    /// <summary>Operator note published beside the count, or "" for none.</summary>
    public string StatusMessage => Get<string>(Knobs.StatusMessage);

    /// <summary>GM names from the environment, comma-separated and unparsed, or "".</summary>
    public string Gms => Get<string>(Knobs.Gms);
    /// <summary>Tester names from the environment, comma-separated and unparsed, or "".</summary>
    public string Testers => Get<string>(Knobs.Testers);

    /// <summary>Delay before the 0x04 completing a 4.95 self-walk.</summary>
    public int WalkMs => Get<int>(Knobs.WalkMs);
    /// <summary>Self-walk drive mode with fast-move on.</summary>
    public int SelfMove => Get<int>(Knobs.SelfMove);
    /// <summary>Delay before the mode-5 unblock 0x04.</summary>
    public int AckMs => Get<int>(Knobs.AckMs);
    /// <summary>Self-walk drive mode with fast-move off.</summary>
    public int SlowMove => Get<int>(Knobs.SlowMove);
    /// <summary>Lock the client camera dead-centre.</summary>
    public bool RealmCenter => Get<bool>(Knobs.RealmCenter);
    /// <summary>Pre-entry placeholder for a session's fast-move flag.</summary>
    public bool FastMoveDefault => Get<bool>(Knobs.FastMoveDefault);
    /// <summary>Drive the walk reply off the session's fast-move flag.</summary>
    public bool FastMoveTrustToggle => Get<bool>(Knobs.FastMoveTrustToggle);
    /// <summary>Push terrain ahead of a walking 5.33 client.</summary>
    public bool PushMap => Get<bool>(Knobs.PushMap);
    /// <summary>Steps to defer that push for.</summary>
    public int PushGraceSteps => Get<int>(Knobs.PushGraceSteps);

    /// <summary>Map light value sent on the 0x15.</summary>
    public int LightValue => Get<int>(Knobs.LightValue);
    /// <summary>Encoding of that light value.</summary>
    public string LightFormat => Get<string>(Knobs.LightFormat);
    /// <summary>5.33 terrain-render probe selector.</summary>
    public string MapDiag => Get<string>(Knobs.MapDiag);
    /// <summary>Sheet-1 ground shift for 5.33.</summary>
    public int GroundOff533 => Get<int>(Knobs.GroundOff533);
    /// <summary>Sheet-1 ground shift for 4.95.</summary>
    public int GroundOff495 => Get<int>(Knobs.GroundOff495);
    /// <summary>Object id shift for 5.33.</summary>
    public int ObjectOff533 => Get<int>(Knobs.ObjectOff533);
    /// <summary>Object id shift for 4.95.</summary>
    public int ObjectOff495 => Get<int>(Knobs.ObjectOff495);
    /// <summary>Superseded ground-only shift; null when unset.</summary>
    public int? LegacyTileOff => GetOrNull<int>(Knobs.LegacyTileOff);
    /// <summary>Scope of the 5.33 object-collision workaround.</summary>
    public string ObjFix533 => Get<string>(Knobs.ObjFix533);
    /// <summary>Adjustment added to the 0x29 effect id.</summary>
    public int EfxWireOffset => Get<int>(Knobs.EfxWireOffset);
    /// <summary>Defaults for the two extra 5.33 appearance slots, as "hair,tail".</summary>
    public string Look533Extra => Get<string>(Knobs.Look533Extra);

    // ---- startup banner --------------------------------------------------------------------------------

    /// <summary>The effective configuration, one line per knob, grouped by area: the value in force and
    /// whether it came from the environment or the declared default. No knob in this set is a secret, so
    /// values are printed verbatim.</summary>
    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>();
        int fromEnv = Entries.Count(e => e.FromEnvironment && e.Knob.Area != ConfigArea.Retired);
        // The retired content override names are not knobs — they have no value, no default and no row in
        // the generated document this line points at — so they are counted nowhere here. A set one is
        // reported by its startup warning and by the [Retired] line below.
        lines.Add($"=== config: {Knobs.All.Count} knob(s), " +
                  $"{fromEnv} set from the environment " +
                  "(see docs/common/Configuration.md) ===");
        foreach (var area in Entries.Select(e => e.Knob.Area).Distinct())
        {
            // A retired knob nobody set is not news; the whole point of retiring it is that it is gone. One
            // that IS set always carries a warning, so this row asks whether the variable is present rather
            // than whether it supplied the value in force — which for a retired knob it never does.
            var inArea = Entries.Where(e => e.Knob.Area == area)
                                .Where(e => area != ConfigArea.Retired || !string.IsNullOrEmpty(e.Raw))
                                .ToArray();
            if (inArea.Length == 0) continue;
            lines.Add($"    [{area}]");
            foreach (var entry in inArea)
                lines.Add($"      {entry.Knob.Name,-34} = {entry.Text}" +
                          // A retired knob's value comes from neither source — its own text says IGNORED.
                          (area == ConfigArea.Retired ? ""
                           : entry.FromEnvironment ? "   (environment)" : "   (default)"));
        }
        if (_setRetiredContentOverrides.Count > 0)
        {
            if (!lines.Contains("    [Retired]")) lines.Add("    [Retired]");
            foreach (var entry in _setRetiredContentOverrides)
                lines.Add($"      {entry.Name,-34} = (retired — IGNORED, was '{entry.Raw}')");
        }
        return lines;
    }

    /// <summary>Every warning the resolution produced: an unparseable number, an out-of-range value, a
    /// boolean that is neither 0 nor 1, or a retired gameplay variable that is still set.</summary>
    public IReadOnlyList<string> Warnings =>
        Entries.Where(e => e.Warning is not null).Select(e => e.Warning!)
            .Concat(_setRetiredContentOverrides.Select(entry =>
                $"{entry.Name}='{entry.Raw}' is RETIRED and is being IGNORED. Content files now resolve " +
                "under P1998_GAME_DATA; set P1998_GAME_DATA to relocate the content directory. " +
                "Unset the variable to silence this."))
            .ToArray();

    /// <summary>Log the effective configuration and any warnings. Called once per process, after
    /// <c>Log.AttachFile</c> so the banner reaches the file as well as the console.</summary>
    public static void LogEffective()
    {
        var config = Current;
        foreach (var warning in config.Warnings) Log.Warn("config: " + warning);
        foreach (var line in config.Describe()) Log.Info(line);
    }

    /// <summary>Declare this process's logging defaults, letting <c>P1998_LOG_WIRE</c> and
    /// <c>P1998_LOG_MAX_BYTES</c> override them. Replaces the two direct <c>Log.Configure</c> calls so the
    /// environment read happens in exactly one place; <see cref="Log"/> keeps the rest.</summary>
    /// <param name="wireDefault">Wire dump when the environment says nothing.</param>
    /// <param name="maxBytesDefault">Rotation limit when the environment says nothing.</param>
    public static void ConfigureLogging(bool wireDefault, long maxBytesDefault)
    {
        var config = Current;
        Log.Configure(config.LogWire ?? wireDefault, config.LogMaxBytes ?? maxBytesDefault);
    }

    // ---- generated documentation -----------------------------------------------------------------------

    /// <summary>Opening marker of the generated block in <c>docs/common/Configuration.md</c>.</summary>
    public const string DocStartMarker = "<!-- generated: config -->";
    /// <summary>Closing marker of that block.</summary>
    public const string DocEndMarker = "<!-- /generated -->";

    /// <summary>Render the generated reference from the declarations alone — no environment, no process
    /// state, so the committed document is a function of the source and a test can prove it has not drifted.
    /// Follows the same marker convention as the generated table block in <c>game-data/README.md</c>.</summary>
    public static string RenderDocBlock()
    {
        var lines = new List<string> { DocStartMarker };
        foreach (var area in Knobs.All.Select(k => k.Area).Distinct())
        {
            lines.Add("");
            lines.Add($"### {AreaHeading(area)}");
            lines.Add("");
            lines.Add(area == ConfigArea.Retired
                ? "| Variable | Now lives in | What it was |"
                : "| Variable | Type | Default | What it does |");
            lines.Add(area == ConfigArea.Retired ? "|---|---|---|" : "|---|---|---|---|");
            foreach (var knob in Knobs.All.Where(k => k.Area == area))
                lines.Add(area == ConfigArea.Retired
                    ? $"| `{knob.Name}` | `{((RetiredKnob)knob).TuningKey}` in `game-data/ServerTuning.csv` | {knob.Doc} |"
                    : $"| `{knob.Name}` | {knob.TypeName} | {knob.DefaultText} | {knob.Doc} |");
        }
        lines.Add("");
        lines.Add(DocEndMarker);
        return string.Join('\n', lines);
    }

    private static string AreaHeading(ConfigArea area) => area switch
    {
        ConfigArea.Paths => "Deployment roots",
        ConfigArea.Logging => "Logging",
        ConfigArea.Transport => "Transport and the accept path",
        ConfigArea.Abuse => "Abuse control — connection admission and login throttling",
        ConfigArea.Login => "Login, handoff and redirects",
        ConfigArea.Session => "Session behaviour",
        ConfigArea.World => "World heartbeat",
        ConfigArea.Diagnostics => "Process-health probes",
        ConfigArea.Status => "The status document",
        ConfigArea.Staff => "Staff rosters",
        ConfigArea.Movement => "4.95 movement calibration",
        ConfigArea.Rendering => "Rendering and tile diagnostics",
        ConfigArea.Retired => "Retired — moved into ServerTuning.csv",
        _ => area.ToString(),
    };
}

// ======================================================================================================
// Knob declarations. Each type owns exactly one parse rule, so "what does a bad value do" is answered once
// per type instead of once per call site.
// ======================================================================================================

/// <summary>One declared environment knob: its name, where it belongs in the reference, and what it does.</summary>
public abstract class ConfigKnob
{
    /// <param name="name">The <c>P1998_*</c> variable name.</param>
    /// <param name="area">Where it belongs in the generated reference.</param>
    /// <param name="doc">One or two sentences: what it does and what a non-default value costs.</param>
    protected ConfigKnob(string name, ConfigArea area, string doc)
    {
        Name = name;
        Area = area;
        Doc = doc;
    }

    /// <summary>The environment variable's name.</summary>
    public string Name { get; }
    /// <summary>Where this knob belongs in the reference and the startup banner.</summary>
    public ConfigArea Area { get; }
    /// <summary>What it does, for the generated reference.</summary>
    public string Doc { get; }
    /// <summary>How the reference names this knob's type.</summary>
    public abstract string TypeName { get; }
    /// <summary>How the reference renders this knob's default.</summary>
    public abstract string DefaultText { get; }

    /// <summary>Turn the raw environment value into the typed value, the text the banner prints, and a
    /// warning when the raw value could not be used.</summary>
    internal abstract (object? Value, string Text, string? Warning) Resolve(string? raw);

    /// <summary>The one message a rejected value produces. Every knob type funnels through it, so an operator
    /// who mistyped anything gets the same sentence: what was set, that it was ignored, and what is in force
    /// instead. Nothing here ever silently becomes zero.</summary>
    protected string Rejected(string? raw, string reason, string inForce) =>
        $"{Name}='{raw}' {reason} — ignored; using {inForce}.";
}

/// <summary>A directory that defaults to a named folder under the deployment root.</summary>
public sealed class PathKnob : ConfigKnob
{
    private readonly string _folder;

    /// <param name="name">The variable name.</param>
    /// <param name="area">Reference section.</param>
    /// <param name="folder">Folder name under the deployment root when unset.</param>
    /// <param name="doc">What lives there.</param>
    public PathKnob(string name, ConfigArea area, string folder, string doc) : base(name, area, doc) =>
        _folder = folder;

    /// <inheritdoc/>
    public override string TypeName => "path";
    /// <inheritdoc/>
    public override string DefaultText => $"`<root>/{_folder}`";

    internal override (object?, string, string?) Resolve(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? (Path.Combine(RepoPaths.Root(), _folder), Path.Combine(RepoPaths.Root(), _folder), null)
            : (raw, raw, null);
}

/// <summary>Free text. The <c>normalize</c> flag trims and lowercases, for the knobs whose call sites
/// always did; <c>trim</c> trims only, for the ones that trimmed a path or a message but obviously must not
/// case-fold it. Which of the three a knob gets is not a style choice — it is the rule its old inline read
/// had, transcribed.</summary>
public sealed class StringKnob : ConfigKnob
{
    private readonly string _default;
    private readonly bool _normalize;
    private readonly bool _trim;
    private readonly string? _defaultText;

    /// <param name="name">The variable name.</param>
    /// <param name="area">Reference section.</param>
    /// <param name="default">Value when unset or blank.</param>
    /// <param name="doc">What it does.</param>
    /// <param name="normalize">Trim and lowercase the value.</param>
    /// <param name="trim">Trim the value without case-folding it.</param>
    /// <param name="defaultText">How the reference describes the default, when the value in force for a
    /// blank knob is computed by the call site (a path under another knob's directory, say) rather than
    /// being the declared string itself.</param>
    public StringKnob(string name, ConfigArea area, string @default, string doc, bool normalize = false,
                      bool trim = false, string? defaultText = null)
        : base(name, area, doc)
    {
        _default = @default;
        _normalize = normalize;
        _trim = trim;
        _defaultText = defaultText;
    }

    /// <summary>The value in force when the variable is unset — exposed so a call site with its own blank
    /// fallback can share this one string instead of keeping a copy that can drift from it.</summary>
    public string Default => _default;

    /// <inheritdoc/>
    public override string TypeName => "text";
    /// <inheritdoc/>
    public override string DefaultText =>
        _defaultText ?? (_default.Length == 0 ? "*(blank)*" : $"`{_default}`");

    internal override (object?, string, string?) Resolve(string? raw)
    {
        string value = string.IsNullOrWhiteSpace(raw) ? _default : raw;
        if (_normalize) value = value.Trim().ToLowerInvariant();
        else if (_trim) value = value.Trim();
        return (value, value.Length == 0 ? "(blank)" : value, null);
    }
}

/// <summary>Free text restricted to a declared set of words. Anything else warns and takes the default,
/// rather than falling into it silently the way a <c>switch</c>'s discard arm did.</summary>
public sealed class EnumKnob : ConfigKnob
{
    private readonly string _default;
    private readonly string[] _allowed;

    /// <param name="name">The variable name.</param>
    /// <param name="area">Reference section.</param>
    /// <param name="default">Value when unset.</param>
    /// <param name="allowed">Every accepted word, lowercase.</param>
    /// <param name="doc">What each word does.</param>
    public EnumKnob(string name, ConfigArea area, string @default, string[] allowed, string doc)
        : base(name, area, doc)
    {
        _default = @default;
        _allowed = allowed;
    }

    /// <inheritdoc/>
    public override string TypeName => "one of " + string.Join(" / ", _allowed.Select(a => $"`{a}`"));
    /// <inheritdoc/>
    public override string DefaultText => $"`{_default}`";

    internal override (object?, string, string?) Resolve(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (_default, _default, null);
        string value = raw.Trim().ToLowerInvariant();
        return Array.IndexOf(_allowed, value) >= 0
            ? (value, value, null)
            : (_default, _default, Rejected(raw, $"is not one of {string.Join(", ", _allowed)}", $"'{_default}'"));
    }
}

/// <summary>The one meaning of a boolean knob: <c>"0"</c> off, <c>"1"</c> on, unset means the declared
/// default, and ANY other value is a mistake that warns and takes the default rather than being read as a
/// truthy string.
/// <para>This rule started life on <c>P1998_LOG_WIRE</c> alone, because the login side tested <c>== "1"</c>
/// and the game side tested <c>!= "0"</c>, so <c>P1998_LOG_WIRE=true</c> silently turned the dump OFF in one
/// process and ON in the other. Every boolean knob in the server now shares it: the nine call sites this
/// replaced were split between those same two tests, and each of them would have read <c>true</c> as
/// whichever answer its own spelling happened to give.</para></summary>
public sealed class BoolKnob : ConfigKnob
{
    private readonly bool _default;

    /// <param name="name">The variable name.</param>
    /// <param name="area">Reference section.</param>
    /// <param name="default">Value when unset.</param>
    /// <param name="doc">What it does.</param>
    public BoolKnob(string name, ConfigArea area, bool @default, string doc) : base(name, area, doc) =>
        _default = @default;

    /// <inheritdoc/>
    public override string TypeName => "`0` / `1`";
    /// <inheritdoc/>
    public override string DefaultText => _default ? "`1` (on)" : "`0` (off)";

    internal override (object?, string, string?) Resolve(string? raw)
    {
        var (value, warning) = ParseBool(raw, _default, this);
        return (value, value ? "1 (on)" : "0 (off)", warning);
    }

    internal static (bool Value, string? Warning) ParseBool(string? raw, bool fallback, ConfigKnob knob)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (fallback, null);
        string trimmed = raw.Trim();
        if (trimmed == "0") return (false, null);
        if (trimmed == "1") return (true, null);
        return (fallback, $"{knob.Name}='{raw}' is not 0 or 1 — ignored; staying {(fallback ? "ON" : "off")}.");
    }
}

/// <summary>A boolean whose default is declared by the entry point rather than here, so "unset" has to stay
/// distinguishable from "explicitly off". Resolves to null when unset.
/// <para>Unlike <see cref="BoolKnob"/> this type does NOT trim: the value must be exactly <c>"0"</c> or
/// <c>"1"</c>, and a padded <c>" 1"</c> warns and keeps the entry point's default. The only knob of this
/// type is <c>P1998_LOG_WIRE</c>, whose ON state writes plaintext passwords into the login server's log
/// (4.95's cipher is a fixed published XOR), so the one direction that must never happen by accident is
/// off→on. <c>set P1998_LOG_WIRE= 1</c> in a cmd launcher produces exactly that padded value, and the rule
/// this replaced (<c>Log.ParseWire</c>) was exact-match for the same reason.</para></summary>
public sealed class OptionalBoolKnob : ConfigKnob
{
    /// <param name="name">The variable name.</param>
    /// <param name="area">Reference section.</param>
    /// <param name="doc">What it does, including what each process defaults to.</param>
    public OptionalBoolKnob(string name, ConfigArea area, string doc) : base(name, area, doc) { }

    /// <inheritdoc/>
    public override string TypeName => "`0` / `1`";
    /// <inheritdoc/>
    public override string DefaultText => "per process (see notes)";

    internal override (object?, string, string?) Resolve(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, "(entry point's default)", null);
        if (raw == "0") return (false, "0 (off)", null);
        if (raw == "1") return (true, "1 (on)", null);
        return (null, "(entry point's default)",
                $"{Name}='{raw}' is not 0 or 1 — ignored; keeping this process's own default.");
    }
}

/// <summary>A whole number. A value that is not a number, or falls outside the declared range, keeps the
/// declared default and warns — it never becomes 0.</summary>
public sealed class IntKnob : ConfigKnob
{
    private readonly int _default;
    private readonly int? _min;
    private readonly int? _max;

    /// <param name="name">The variable name.</param>
    /// <param name="area">Reference section.</param>
    /// <param name="default">Value when unset.</param>
    /// <param name="doc">What it does.</param>
    /// <param name="min">Smallest accepted value, if the call site had one.</param>
    /// <param name="max">Largest accepted value, if the call site had one.</param>
    public IntKnob(string name, ConfigArea area, int @default, string doc, int? min = null, int? max = null)
        : base(name, area, doc)
    {
        _default = @default;
        _min = min;
        _max = max;
    }

    /// <inheritdoc/>
    public override string TypeName => "integer" + Range(_min, _max);
    /// <inheritdoc/>
    public override string DefaultText => $"`{_default.ToString(CultureInfo.InvariantCulture)}`";

    internal static string Range(int? min, int? max) =>
        (min, max) switch
        {
            (null, null) => "",
            (not null, null) => $" ≥ {min}",
            (null, not null) => $" ≤ {max}",
            _ => $" {min}..{max}",
        };

    internal override (object?, string, string?) Resolve(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (_default, Text(_default), null);
        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            return (_default, Text(_default), Rejected(raw, "is not a whole number", Text(_default)));
        if (_min is { } lo && value < lo)
            return (_default, Text(_default), Rejected(raw, $"is below the minimum {lo}", Text(_default)));
        if (_max is { } hi && value > hi)
            return (_default, Text(_default), Rejected(raw, $"is above the maximum {hi}", Text(_default)));
        return (value, Text(value), null);
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A whole number whose default is supplied by the call site (a paired port, a per-process size),
/// so "unset" has to stay distinguishable from a real value. Resolves to null when unset or rejected.</summary>
public sealed class OptionalIntKnob : ConfigKnob
{
    private readonly int? _min;
    private readonly string _defaultText;

    /// <param name="name">The variable name.</param>
    /// <param name="area">Reference section.</param>
    /// <param name="doc">What it does.</param>
    /// <param name="defaultText">How the reference describes the caller's fallback.</param>
    /// <param name="min">Smallest accepted value, if the call site had one.</param>
    public OptionalIntKnob(string name, ConfigArea area, string doc, string defaultText, int? min = null)
        : base(name, area, doc)
    {
        _min = min;
        _defaultText = defaultText;
    }

    /// <inheritdoc/>
    public override string TypeName => "integer" + IntKnob.Range(_min, null);
    /// <inheritdoc/>
    public override string DefaultText => _defaultText;

    internal override (object?, string, string?) Resolve(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, $"({_defaultText})", null);
        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            return (null, $"({_defaultText})", Rejected(raw, "is not a whole number", _defaultText));
        if (_min is { } lo && value < lo)
            return (null, $"({_defaultText})", Rejected(raw, $"is below the minimum {lo}", _defaultText));
        return (value, value.ToString(CultureInfo.InvariantCulture), null);
    }
}

/// <summary>A 64-bit whole number. Same rejection rule as <see cref="IntKnob"/>.</summary>
public sealed class LongKnob : ConfigKnob
{
    private readonly long _default;
    private readonly long? _min;

    /// <param name="name">The variable name.</param>
    /// <param name="area">Reference section.</param>
    /// <param name="default">Value when unset.</param>
    /// <param name="doc">What it does.</param>
    /// <param name="min">Smallest accepted value, if the call site had one.</param>
    public LongKnob(string name, ConfigArea area, long @default, string doc, long? min = null)
        : base(name, area, doc)
    {
        _default = @default;
        _min = min;
    }

    /// <inheritdoc/>
    public override string TypeName => "integer" + (_min is null ? "" : $" ≥ {_min}");
    /// <inheritdoc/>
    public override string DefaultText => $"`{_default.ToString(CultureInfo.InvariantCulture)}`";

    internal override (object?, string, string?) Resolve(string? raw)
    {
        string text = _default.ToString(CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(raw)) return (_default, text, null);
        if (!long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
            return (_default, text, Rejected(raw, "is not a whole number", text));
        if (_min is { } lo && value < lo)
            return (_default, text, Rejected(raw, $"is below the minimum {lo}", text));
        return (value, value.ToString(CultureInfo.InvariantCulture), null);
    }
}

/// <summary>A 64-bit number whose default belongs to the entry point. Null when unset or rejected.</summary>
public sealed class OptionalLongKnob : ConfigKnob
{
    private readonly long? _min;

    /// <param name="name">The variable name.</param>
    /// <param name="area">Reference section.</param>
    /// <param name="doc">What it does, including what each process defaults to.</param>
    /// <param name="min">Smallest accepted value.</param>
    public OptionalLongKnob(string name, ConfigArea area, string doc, long? min = null)
        : base(name, area, doc) => _min = min;

    /// <inheritdoc/>
    public override string TypeName => "integer" + (_min is null ? "" : $" ≥ {_min}");
    /// <inheritdoc/>
    public override string DefaultText => "per process (see notes)";

    internal override (object?, string, string?) Resolve(string? raw)
    {
        const string fallback = "(entry point's default)";
        if (string.IsNullOrWhiteSpace(raw)) return (null, fallback, null);
        if (!long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
            return (null, fallback, Rejected(raw, "is not a whole number", "this process's own default"));
        if (_min is { } lo && value < lo)
            return (null, fallback, Rejected(raw, $"is below the minimum {lo}", "this process's own default"));
        return (value, value.ToString(CultureInfo.InvariantCulture), null);
    }
}

/// <summary>A gameplay value that USED to be an environment variable and is now a row in
/// <c>game-data/ServerTuning.csv</c>, per the data-vs-code rule. The declaration stays so that an operator
/// who still has the old variable set is TOLD, loudly, at startup — rather than watching a value they
/// configured stop taking effect with nothing anywhere to explain why.</summary>
public sealed class RetiredKnob : ConfigKnob
{
    /// <param name="name">The retired variable name.</param>
    /// <param name="tuningKey">The <c>ServerTuning.csv</c> key that replaced it.</param>
    /// <param name="doc">What the value does.</param>
    public RetiredKnob(string name, string tuningKey, string doc)
        : base(name, ConfigArea.Retired, doc) => TuningKey = tuningKey;

    /// <summary>The <c>ServerTuning.csv</c> key that replaced this variable.</summary>
    public string TuningKey { get; }

    /// <inheritdoc/>
    public override string TypeName => "retired";
    /// <inheritdoc/>
    public override string DefaultText => $"`{TuningKey}` in `game-data/ServerTuning.csv`";

    internal override (object?, string, string?) Resolve(string? raw) =>
        string.IsNullOrEmpty(raw)
            ? (null, "(retired)", null)
            : (null, $"(retired — IGNORED, was '{raw}')",
               $"{Name}='{raw}' is RETIRED and is being IGNORED. This value moved into " +
               $"game-data/ServerTuning.csv as the key '{TuningKey}'; set it there and run @reload. " +
               "Unset the variable to silence this.");
}
