using System.Globalization;

namespace Shared;

/// <summary>Where a knob belongs in <c>docs/common/Configuration.md</c> and in the startup banner.</summary>
public enum ConfigArea
{
    /// <summary>Where the four deployment roots live on disk.</summary>
    Paths,
    /// <summary>What the logger writes and how big it lets the file get.</summary>
    Logging,
    /// <summary>The accept path: PROXY protocol and the peers allowed to speak it.</summary>
    Transport,
    /// <summary>Login throttling, the handoff gate and the addresses a redirect names.</summary>
    Login,
    /// <summary>Per-session behaviour the operator may retune without a rebuild.</summary>
    Session,
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
/// <para><b>What is NOT here yet.</b> The 68 per-table content-path overrides (<c>P1998_&lt;TABLE&gt;</c>,
/// see <c>Server/Content.Tables.cs</c>) are declared by <c>TableSpec</c> and stay there: retiring them in
/// favour of <see cref="ServerConfig.GameDataDir"/> is a behaviour change entangled with the TableSpec work.
/// A handful of reads in files that were being edited alongside this one are also still inline. Both sets
/// are named in the generated reference so the gap is visible rather than assumed closed.</para>
/// </summary>
public sealed class ServerConfig
{
    // ---- declarations ----------------------------------------------------------------------------------
    // One entry per knob. Name, area, default, doc string. Nothing else declares a P1998_ literal.

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

        // --- logging ---
        public static readonly OptionalBoolKnob LogWire = new(
            "P1998_LOG_WIRE", ConfigArea.Logging,
            "Hex-dump every frame. Unset takes the ENTRY POINT's default, which differs by process: ON in the " +
            "game server (the backbone of the protocol RE work, no credentials on that channel) and OFF in the " +
            "login server (4.95's cipher is a fixed published XOR, so a dump writes plaintext passwords).");
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
        public static readonly IntKnob LoginFails = new(
            "P1998_LOGIN_FAILS", ConfigArea.Login, 10,
            "Failed logins one source IP may spend inside the window before further attempts are refused " +
            "without touching the password hash.", min: 1);
        public static readonly LongKnob LoginFailWindowMs = new(
            "P1998_LOGIN_FAIL_WINDOW_MS", ConfigArea.Login, 300_000,
            "Length of that rolling failure window, in milliseconds. A successful login clears the counter.",
            min: 1);
        public static readonly BoolKnob LoginExemptLoopback = new(
            "P1998_LOGIN_EXEMPT_LOOPBACK", ConfigArea.Login, true,
            "Exempt loopback from the failed-login throttle (local dev and the same-box login->game hop). " +
            "Set 0 on a host where loopback is not automatically trusted.");

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
            GameData, State, Logs, Run,
            LogWire, LogMaxBytes,
            TrustProxy, ProxyHeaderMs, ProxyAllow,
            GameHost, LoginHost, LoginPort, EnforceHandoff,
            LoginFails, LoginFailWindowMs, LoginExemptLoopback,
            AutoSaveMs, CastQueue, PassEnforce,
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

    /// <summary>Every knob's resolved state, in declaration order.</summary>
    public IReadOnlyList<Entry> Entries { get; }

    private ServerConfig(Func<string, string?> source)
    {
        var entries = new List<Entry>(Knobs.All.Count);
        foreach (var knob in Knobs.All)
        {
            string? raw = source(knob.Name);
            var (value, text, warning) = knob.Resolve(raw);
            entries.Add(new Entry(knob, raw, value, text,
                                  FromEnvironment: !string.IsNullOrEmpty(raw), warning));
        }
        Entries = entries;
        _byName = entries.ToDictionary(e => e.Knob.Name, StringComparer.Ordinal);
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
    /// <summary>Exempt loopback from the failed-login throttle.</summary>
    public bool LoginExemptLoopback => Get<bool>(Knobs.LoginExemptLoopback);

    /// <summary>Dirty-character flush cadence in milliseconds.</summary>
    public int AutoSaveMs => Get<int>(Knobs.AutoSaveMs);
    /// <summary>Hold over-budget casts rather than dropping them.</summary>
    public bool CastQueue => Get<bool>(Knobs.CastQueue);
    /// <summary>Enforce server-side collision.</summary>
    public bool PassEnforce => Get<bool>(Knobs.PassEnforce);

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
        lines.Add($"=== config: {Knobs.All.Count} knob(s), {fromEnv} set from the environment " +
                  "(see docs/common/Configuration.md) ===");
        foreach (var area in Entries.Select(e => e.Knob.Area).Distinct())
        {
            // A retired knob nobody set is not news; the whole point of retiring it is that it is gone.
            var inArea = Entries.Where(e => e.Knob.Area == area)
                                .Where(e => area != ConfigArea.Retired || e.FromEnvironment)
                                .ToArray();
            if (inArea.Length == 0) continue;
            lines.Add($"    [{area}]");
            foreach (var entry in inArea)
                lines.Add($"      {entry.Knob.Name,-34} = {entry.Text}" +
                          (entry.FromEnvironment ? "   (environment)" : "   (default)"));
        }
        return lines;
    }

    /// <summary>Every warning the resolution produced: an unparseable number, an out-of-range value, a
    /// boolean that is neither 0 nor 1, or a retired gameplay variable that is still set.</summary>
    public IReadOnlyList<string> Warnings =>
        Entries.Where(e => e.Warning is not null).Select(e => e.Warning!).ToArray();

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
        ConfigArea.Login => "Login, handoff and redirects",
        ConfigArea.Session => "Session behaviour",
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
/// always did.</summary>
public sealed class StringKnob : ConfigKnob
{
    private readonly string _default;
    private readonly bool _normalize;

    /// <param name="name">The variable name.</param>
    /// <param name="area">Reference section.</param>
    /// <param name="default">Value when unset or blank.</param>
    /// <param name="doc">What it does.</param>
    /// <param name="normalize">Trim and lowercase the value.</param>
    public StringKnob(string name, ConfigArea area, string @default, string doc, bool normalize = false)
        : base(name, area, doc)
    {
        _default = @default;
        _normalize = normalize;
    }

    /// <summary>The value in force when the variable is unset — exposed so a call site with its own blank
    /// fallback can share this one string instead of keeping a copy that can drift from it.</summary>
    public string Default => _default;

    /// <inheritdoc/>
    public override string TypeName => "text";
    /// <inheritdoc/>
    public override string DefaultText => _default.Length == 0 ? "*(blank)*" : $"`{_default}`";

    internal override (object?, string, string?) Resolve(string? raw)
    {
        string value = string.IsNullOrWhiteSpace(raw) ? _default : raw;
        if (_normalize) value = value.Trim().ToLowerInvariant();
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
/// distinguishable from "explicitly off". Resolves to null when unset.</summary>
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
        string trimmed = raw.Trim();
        if (trimmed == "0") return (false, "0 (off)", null);
        if (trimmed == "1") return (true, "1 (on)", null);
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
