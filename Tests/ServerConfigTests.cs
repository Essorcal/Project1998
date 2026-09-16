using System.Globalization;
using Shared;
using Xunit;

namespace Tests;

/// <summary>
/// <see cref="ServerConfig"/>: the declaration table, the parse rules each knob type owns, and the generated
/// reference that has to stay in step with them.
///
/// <para>Everything here builds a configuration from an EXPLICIT source rather than from the process
/// environment. That is deliberate: the environment is shared with every other test in the run, the real
/// snapshot is resolved once in a module initializer long before any of these run, and a fact that mutated
/// it would be testing xUnit's scheduler as much as the code. <see cref="ServerConfig.From"/> exists for
/// exactly this.</para>
/// </summary>
public class ServerConfigTests
{
    private static ServerConfig With(params (string Name, string? Value)[] set)
    {
        var map = set.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);
        return ServerConfig.From(name => map.GetValueOrDefault(name));
    }

    private static ServerConfig Defaults() => ServerConfig.From(_ => null);

    // ---- a knob's default ---------------------------------------------------------------------------------

    /// <summary>A deployment that sets nothing gets exactly the values the code carried before this file
    /// existed. These numbers are the ones the inline <c>TryParse</c> fallbacks used, transcribed knob by
    /// knob; a typo in a declared default would silently retune a live server, so they are pinned.</summary>
    [Fact]
    public void An_unset_knob_resolves_to_its_declared_default()
    {
        var config = Defaults();

        Assert.Equal(15_000, config.AutoSaveMs);
        Assert.Equal(232, config.LightValue);
        Assert.Equal("beu16", config.LightFormat);
        Assert.Equal("", config.MapDiag);
        Assert.True(config.PassEnforce);
        Assert.True(config.CastQueue);
        Assert.Equal(200, config.WalkMs);
        Assert.Equal(7, config.SelfMove);
        Assert.Equal(360, config.AckMs);
        Assert.Equal(5, config.SlowMove);
        Assert.False(config.RealmCenter);
        Assert.False(config.FastMoveDefault);
        Assert.True(config.FastMoveTrustToggle);
        Assert.True(config.PushMap);
        Assert.Equal(0, config.PushGraceSteps);
        Assert.Equal(0, config.GroundOff533);
        Assert.Equal(0, config.GroundOff495);
        Assert.Equal(0, config.ObjectOff533);
        Assert.Equal(0, config.ObjectOff495);
        Assert.Null(config.LegacyTileOff);
        Assert.Equal("free", config.ObjFix533);
        Assert.Equal(0, config.EfxWireOffset);
        Assert.Equal("", config.Look533Extra);

        Assert.False(config.TrustProxy);
        Assert.Equal(5_000, config.ProxyHeaderMs);
        Assert.Equal("127.0.0.0/8,::1/128", config.ProxyAllow);

        Assert.Equal("127.0.0.1", config.GameHost);
        Assert.Equal("127.0.0.1", config.LoginHost);   // falls back to the game host
        Assert.Null(config.LoginPort);                 // caller pairs it with the arrival channel
        Assert.True(config.EnforceHandoff);
        Assert.Equal(10, config.LoginFails);
        Assert.Equal(300_000L, config.LoginFailWindowMs);
        Assert.True(config.LoginExemptLoopback);

        Assert.Null(config.LogWire);                   // caller supplies the per-process default
        Assert.Null(config.LogMaxBytes);

        Assert.Empty(config.Warnings);
    }

    /// <summary>A stock server starts silent. Every declared default has to satisfy its own declared range,
    /// or the banner would carry a warning on a deployment that configured nothing — and a banner that cries
    /// wolf at every startup is one nobody reads.</summary>
    [Fact]
    public void No_declared_default_violates_its_own_declared_range()
    {
        Assert.Empty(Defaults().Warnings);
        Assert.Equal(ServerConfig.Knobs.All.Count, Defaults().Entries.Count);
    }

    // ---- a knob overridden from the environment -----------------------------------------------------------

    [Fact]
    public void An_environment_value_wins_over_the_default()
    {
        var config = With(
            ("P1998_AUTOSAVE_MS", "3000"),
            ("P1998_PASS", "0"),
            ("P1998_V495_REALM", "1"),
            ("P1998_LOGIN_PORT", "2100"),
            ("P1998_LOGIN_FAIL_WINDOW_MS", "60000"),
            ("P1998_OBJ_FIX_533", "DECOR"),
            ("P1998_LIGHT_FMT", " U8 "),
            ("P1998_TILE_OFF", "-3"),
            ("P1998_GAME_HOST", "10.0.0.5"),
            ("P1998_LOG_WIRE", "1"),
            ("P1998_LOG_MAX_BYTES", "1048576"),
            ("P1998_STATE", @"D:\somewhere\state"));

        Assert.Equal(3000, config.AutoSaveMs);
        Assert.False(config.PassEnforce);
        Assert.True(config.RealmCenter);
        Assert.Equal(2100, config.LoginPort);
        Assert.Equal(60_000L, config.LoginFailWindowMs);
        Assert.Equal("decor", config.ObjFix533);        // word-list knobs are case- and space-insensitive
        Assert.Equal("u8", config.LightFormat);
        Assert.Equal(-3, config.LegacyTileOff);         // a negative shift is a legitimate value, not an error
        Assert.Equal("10.0.0.5", config.GameHost);
        Assert.Equal("10.0.0.5", config.LoginHost);     // still falls back to the game host
        Assert.True(config.LogWire);
        Assert.Equal(1048576L, config.LogMaxBytes);
        Assert.Equal(@"D:\somewhere\state", config.StateDir);

        Assert.Empty(config.Warnings);
        Assert.Contains(config.Entries, e => e.Knob.Name == "P1998_AUTOSAVE_MS" && e.FromEnvironment);
        Assert.Contains(config.Entries, e => e.Knob.Name == "P1998_LIGHT" && !e.FromEnvironment);
    }

    /// <summary>An explicit login host stops the game-host fallback, which is the only conditional default in
    /// the set and the one an operator with a genuinely split deployment depends on.</summary>
    [Fact]
    public void An_explicit_login_host_overrides_the_game_host_fallback()
    {
        var config = With(("P1998_GAME_HOST", "10.0.0.5"), ("P1998_LOGIN_HOST", "10.0.0.6"));

        Assert.Equal("10.0.0.5", config.GameHost);
        Assert.Equal("10.0.0.6", config.LoginHost);
    }

    // ---- the type-parse failure path ----------------------------------------------------------------------

    /// <summary>THE failure this mechanism exists to prevent: a mistyped number must not become 0. Every one
    /// of these values would have been a silent disaster under a bare <c>int.Parse</c> — a 0ms autosave, a
    /// 0-failure login budget, a 0-byte log rotation — and under the <c>TryParse ? v : default</c> idiom it
    /// replaced they were silent in the other direction: the default applied and nothing said why.
    /// <para>The rule: keep the declared default, and say so out loud with the variable, the value that was
    /// rejected and what is in force instead. Falsification: make any knob type return 0 (or drop the
    /// warning) on a parse failure and a row here fails.</para></summary>
    [Theory]
    [InlineData("P1998_AUTOSAVE_MS", "soon")]
    [InlineData("P1998_AUTOSAVE_MS", "0")]           // below the declared minimum of 1
    [InlineData("P1998_AUTOSAVE_MS", "-1")]
    [InlineData("P1998_LOGIN_FAILS", "lots")]
    [InlineData("P1998_LOGIN_FAILS", "0")]
    [InlineData("P1998_PROXY_HEADER_MS", "5s")]      // a plausible mistake: units in the value
    [InlineData("P1998_V495_PUSHGRACE", "-1")]
    [InlineData("P1998_LIGHT", "bright")]
    public void A_non_numeric_value_keeps_the_default_and_warns(string name, string bad)
    {
        var config = With((name, bad));
        var stock = Defaults();

        var entry = Assert.Single(config.Entries, e => e.Knob.Name == name);
        var stockEntry = Assert.Single(stock.Entries, e => e.Knob.Name == name);
        Assert.Equal(stockEntry.Value, entry.Value);   // the DEFAULT, never 0 and never null-by-accident
        Assert.NotNull(entry.Warning);

        string warning = Assert.Single(config.Warnings);
        Assert.Contains(name, warning);
        Assert.Contains(bad, warning);
        Assert.Contains("ignored", warning);
    }

    /// <summary>The same rule for the knobs whose default belongs to the caller: a bad value resolves to
    /// "unset" (so the caller's own fallback applies), not to 0.</summary>
    [Theory]
    [InlineData("P1998_LOGIN_PORT", "twenty-one-hundred")]
    [InlineData("P1998_LOGIN_PORT", "0")]
    [InlineData("P1998_LOG_MAX_BYTES", "64MB")]
    [InlineData("P1998_LOG_MAX_BYTES", "0")]
    [InlineData("P1998_LOG_MAX_BYTES", "-5")]
    public void A_bad_value_on_a_caller_defaulted_knob_resolves_to_unset(string name, string bad)
    {
        var config = With((name, bad));

        Assert.Null(Assert.Single(config.Entries, e => e.Knob.Name == name).Value);
        Assert.Contains(name, Assert.Single(config.Warnings));
    }

    /// <summary>One meaning for every boolean knob: "0" off, "1" on, unset the declared default, and anything
    /// else a mistake that warns and takes the default. This rule started on <c>P1998_LOG_WIRE</c>, where the
    /// login side tested <c>== "1"</c> and the game side <c>!= "0"</c>, so <c>true</c> meant OFF in one
    /// process and ON in the other. The nine boolean knobs this replaced were split between those same two
    /// spellings; the rows below are the table after.</summary>
    [Theory]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("2")]
    public void An_unrecognised_boolean_takes_the_default_and_warns(string bad)
    {
        // The dangerous direction first: a knob that defaults OFF must not be read as on.
        var off = With(("P1998_TRUST_PROXY", bad));
        Assert.False(off.TrustProxy);
        Assert.Contains("staying off", Assert.Single(off.Warnings));

        // ... and one that defaults ON must not be read as off.
        var on = With(("P1998_PASS", bad));
        Assert.True(on.PassEnforce);
        Assert.Contains("staying ON", Assert.Single(on.Warnings));

        // The tri-state knob keeps "unset" rather than guessing a process default it does not own.
        var wire = With(("P1998_LOG_WIRE", bad));
        Assert.Null(wire.LogWire);
        Assert.Single(wire.Warnings);
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData(" 1 ", true)]     // surrounding whitespace is trimmed for every plain boolean, uniformly
    public void A_recognised_boolean_is_read_the_same_whatever_the_knobs_default(string raw, bool expected)
    {
        Assert.Equal(expected, With(("P1998_TRUST_PROXY", raw)).TrustProxy);
        Assert.Equal(expected, With(("P1998_PASS", raw)).PassEnforce);
    }

    /// <summary>The exception to the trimming rule, and the reason it is an exception:
    /// <c>P1998_LOG_WIRE</c> ON writes plaintext passwords into the login server's log, so the value must be
    /// EXACTLY "0" or "1" and a padded one is a mistake, not an instruction. <c>set P1998_LOG_WIRE= 1</c> in
    /// a cmd launcher produces " 1"; the rule this replaced (<c>Log.ParseWire</c>) pinned that same row as
    /// warn-and-stay-off, and it must never be read as "on" in the process whose packets carry passwords.
    /// <para>Falsification: trim the value in <c>OptionalBoolKnob.Resolve</c> and every row below fails.
    /// </para></summary>
    [Theory]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData(" 1 ")]
    [InlineData(" 0 ")]
    public void A_padded_wire_dump_value_stays_unset_and_warns(string raw)
    {
        var config = With(("P1998_LOG_WIRE", raw));

        Assert.Null(config.LogWire);
        // Either process's default survives it — off in the login server above all.
        Assert.False(config.LogWire ?? false);
        Assert.True(config.LogWire ?? true);
        Assert.Contains(raw, Assert.Single(config.Warnings));
    }

    /// <summary>The unpadded rows still mean what they say, in both processes.</summary>
    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void An_exact_wire_dump_value_is_read_in_both_processes(string raw, bool expected)
    {
        Assert.Equal(expected, With(("P1998_LOG_WIRE", raw)).LogWire);
        Assert.Empty(With(("P1998_LOG_WIRE", raw)).Warnings);
    }

    /// <summary>The per-process logging default composes with the knob exactly as each entry point needs:
    /// unset takes the process's own answer, set overrides both processes identically. This is the fact
    /// <c>ServerConfig.ConfigureLogging</c> implements for the two <c>Program.cs</c> files.</summary>
    [Theory]
    [InlineData(null, true, true)]     // game server, unset
    [InlineData(null, false, false)]   // login server, unset
    [InlineData("", true, true)]
    [InlineData("", false, false)]
    [InlineData("0", true, false)]
    [InlineData("0", false, false)]
    [InlineData("1", true, true)]
    [InlineData("1", false, true)]
    public void The_wire_dump_combines_the_knob_with_the_entry_points_default(
        string? raw, bool processDefault, bool expected)
    {
        Assert.Equal(expected, With(("P1998_LOG_WIRE", raw)).LogWire ?? processDefault);
    }

    [Theory]
    [InlineData(null, 32L * 1024 * 1024)]
    [InlineData("", 32L * 1024 * 1024)]
    [InlineData("nonsense", 32L * 1024 * 1024)]
    [InlineData("0", 32L * 1024 * 1024)]          // non-positive would disable rotation entirely
    [InlineData("-5", 32L * 1024 * 1024)]
    [InlineData("1048576", 1048576L)]
    public void The_rotation_limit_combines_the_knob_with_the_entry_points_default(string? raw, long expected)
    {
        Assert.Equal(expected, With(("P1998_LOG_MAX_BYTES", raw)).LogMaxBytes ?? 32L * 1024 * 1024);
    }

    /// <summary>A word-list knob given a word that is not on the list takes the default AND says so. The
    /// switch this replaced had a discard arm, so <c>P1998_OBJ_FIX_533=decore</c> fell silently into the
    /// default scope and an operator who meant to blank 1,915 decoration cells saw nothing happen and nothing
    /// explaining why.</summary>
    [Fact]
    public void An_unlisted_word_takes_the_default_and_warns()
    {
        var config = With(("P1998_OBJ_FIX_533", "decore"));

        Assert.Equal("free", config.ObjFix533);
        string warning = Assert.Single(config.Warnings);
        Assert.Contains("decore", warning);
        Assert.Contains("off, free, decor, all, structural", warning);
    }

    // ---- the knobs part 2 declared ------------------------------------------------------------------------
    //
    // Every fact below transcribes the inline rule the knob had at upstream/master b73c879, so a declared
    // default or a clamp that drifted from it fails here rather than silently retuning a live server. The
    // old rule is quoted in each summary; the boundary of every clamp is pinned on both sides.

    /// <summary>The reads part 2 converted keep the exact fallback their inline <c>TryParse</c> carried.
    /// These numbers and strings are transcribed from the old expressions, knob by knob:
    /// <c>P1998_TICK_MS</c> 333, <c>P1998_SLOW_SEND_MS</c> 250, <c>P1998_LOGIN_WRITE_MS</c> 10000,
    /// <c>P1998_POOL_LAG_MS</c> 100, <c>P1998_SILENT_MS</c> 4000, <c>P1998_STATUS_MS</c> 10000,
    /// <c>P1998_HANDSHAKE_MS</c> 15000, the four ConnGuard numbers 2000 / 8 / 30 / 10000 on each of the two
    /// front doors, both loopback exemptions ON, trust-on-first-use OFF, and seven knobs whose "unset" is a
    /// blank string the call site turns into its own fallback.</summary>
    [Fact]
    public void Every_knob_part_two_declared_keeps_its_old_inline_default()
    {
        var config = Defaults();

        Assert.Equal(333, config.TickMs);
        Assert.Null(config.SlowTickMs);           // the caller derives TickMs / 4
        Assert.Equal(250, config.SlowSendMs);
        Assert.Equal(10_000, config.LoginWriteMs);
        Assert.Equal(100, config.PoolLagMs);
        Assert.Equal(4_000, config.SilentMs);
        Assert.Equal(10_000, config.StatusMs);
        Assert.Equal(15_000, config.HandshakeMs);

        Assert.Equal(2_000, config.LoginMaxConn);
        Assert.Equal(8, config.LoginPerIp);
        Assert.Equal(30, config.LoginRate);
        Assert.Equal(10_000, config.LoginRateWindowMs);
        Assert.True(config.LoginExemptLoopback);
        Assert.Equal(2_000, config.GameMaxConn);
        Assert.Equal(8, config.GamePerIp);
        Assert.Equal(30, config.GameRate);
        Assert.Equal(10_000, config.GameRateWindowMs);
        Assert.True(config.GameExemptLoopback);

        Assert.False(config.AllowTofu);

        Assert.Equal("", config.BindAddress);     // NetBind turns blank into IPAddress.Any
        Assert.Equal("", config.MapsDir);
        Assert.Equal("", config.SObjTable);
        Assert.Equal("", config.StatusFile);      // StatusFile turns blank into <run>/status.json
        Assert.Equal("", config.StatusMessage);
        Assert.Equal("", config.Gms);
        Assert.Equal("", config.Testers);

        Assert.Empty(config.Warnings);
    }

    /// <summary>Each clamp pinned on BOTH sides of its edge: the smallest value the old inline rule accepted
    /// is still accepted, and the one below it still falls back to the same default. These are the exact
    /// comparisons the old expressions used - <c>tm &gt;= 50</c>, <c>ss &gt;= 0</c>, <c>wt &gt; 0</c>,
    /// <c>pl &gt;= 0</c>, <c>sm &gt;= 0</c>, <c>ms &gt;= 1000</c>, <c>hs &gt; 0</c> and ConnGuard's
    /// <c>v &gt; 0</c> - so a <c>min</c> off by one would change what a live deployment accepts.
    /// <para>Falsification: change any declared <c>min</c> by one and the row for that knob fails on one
    /// side or the other.</para></summary>
    [Theory]
    [InlineData("P1998_TICK_MS", 50, 49, 333)]
    [InlineData("P1998_SLOW_SEND_MS", 0, -1, 250)]
    [InlineData("P1998_LOGIN_WRITE_MS", 1, 0, 10_000)]
    [InlineData("P1998_POOL_LAG_MS", 0, -1, 100)]
    [InlineData("P1998_SILENT_MS", 0, -1, 4_000)]
    [InlineData("P1998_STATUS_MS", 1_000, 999, 10_000)]
    [InlineData("P1998_HANDSHAKE_MS", 1, 0, 15_000)]
    [InlineData("P1998_LOGIN_MAXCONN", 1, 0, 2_000)]
    [InlineData("P1998_LOGIN_PERIP", 1, 0, 8)]
    [InlineData("P1998_LOGIN_RATE", 1, 0, 30)]
    [InlineData("P1998_LOGIN_RATEWIN_MS", 1, 0, 10_000)]
    [InlineData("P1998_GAME_MAXCONN", 1, 0, 2_000)]
    [InlineData("P1998_GAME_PERIP", 1, 0, 8)]
    [InlineData("P1998_GAME_RATE", 1, 0, 30)]
    [InlineData("P1998_GAME_RATEWIN_MS", 1, 0, 10_000)]
    public void The_smallest_accepted_value_is_taken_and_the_one_below_it_is_refused(
        string name, int lowest, int below, int fallback)
    {
        var accepted = With((name, lowest.ToString(CultureInfo.InvariantCulture)));
        Assert.Equal(lowest, Assert.Single(accepted.Entries, e => e.Knob.Name == name).Value);
        Assert.Empty(accepted.Warnings);

        var refused = With((name, below.ToString(CultureInfo.InvariantCulture)));
        Assert.Equal(fallback, Assert.Single(refused.Entries, e => e.Knob.Name == name).Value);
        Assert.Contains("below the minimum", Assert.Single(refused.Warnings));
    }

    /// <summary>The one knob in the set whose default is DERIVED from another: the old rule was
    /// <c>int.TryParse(env, out var st) &amp;&amp; st &gt;= 0 ? st : TickMs / 4</c>, so 0 is a real value
    /// meaning "watchdog off" and only an absent or refused value derives the quarter. That is why it is an
    /// <c>OptionalIntKnob</c> with <c>min: 0</c> and the divide stays at the call site in
    /// <c>Server/World.cs</c> - a declared constant would stop tracking a retuned heartbeat.
    /// <para>Falsification: give the knob <c>min: 1</c> and the "0 disables" row fails; declare it a plain
    /// <c>IntKnob(83)</c> and the retuned-heartbeat row fails.</para></summary>
    [Theory]
    [InlineData(null, "333", 83)]      // unset at the stock heartbeat: a quarter of 333
    [InlineData(null, "400", 100)]     // ... and it tracks a retuned heartbeat rather than staying at 83
    [InlineData("", "333", 83)]
    [InlineData("nonsense", "333", 83)]
    [InlineData("-1", "333", 83)]      // below the old rule's st >= 0, so it derives
    [InlineData("0", "333", 0)]        // 0 is a VALUE: the watchdog is off, not a quarter of the heartbeat
    [InlineData("150", "333", 150)]
    public void The_slow_tick_threshold_derives_a_quarter_of_the_heartbeat_only_when_it_is_not_set(
        string? raw, string tick, int expected)
    {
        var config = With(("P1998_SLOW_TICK_MS", raw), ("P1998_TICK_MS", tick));

        Assert.Equal(expected, config.SlowTickMs ?? config.TickMs / 4);
    }

    /// <summary>The two loopback exemptions, against the rule they replaced verbatim:
    /// <c>(Environment.GetEnvironmentVariable(name) ?? "1").Trim() != "0"</c>. Because that rule read
    /// EVERYTHING except a trimmed "0" as on, and <c>BoolKnob(true)</c> also keeps its ON default for an
    /// unrecognised value, the accepted-value set is identical row for row - the only difference is that a
    /// garbage value now says so on the startup warning line instead of passing for a deliberate "on".
    /// <para>Falsification: declare either knob <c>BoolKnob(false)</c> and every row but the "0" ones fails.
    /// </para></summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("1", true)]
    [InlineData(" 1 ", true)]
    [InlineData("0", false)]
    [InlineData(" 0 ", false)]
    [InlineData("true", true)]         // the old rule read it as on; so does the default
    [InlineData("2", true)]
    public void A_loopback_exemption_is_on_unless_the_value_is_exactly_zero(string? raw, bool expected)
    {
        Assert.Equal(expected, With(("P1998_LOGIN_EXEMPT_LOOPBACK", raw)).LoginExemptLoopback);
        Assert.Equal(expected, With(("P1998_GAME_EXEMPT_LOOPBACK", raw)).GameExemptLoopback);
    }

    /// <summary>The trust switch, against ITS old rule verbatim:
    /// <c>(Environment.GetEnvironmentVariable("P1998_ALLOW_TOFU") ?? "0").Trim() == "1"</c>. That rule read
    /// everything except a trimmed "1" as off, and <c>BoolKnob(false)</c> keeps its OFF default for an
    /// unrecognised value, so again the accepted set is identical. It matters more here than anywhere else
    /// in the set: while this is on, a login for a legacy character with no accounts row adopts whatever
    /// password was sent, so the direction that must never happen by accident is off-to-on.
    /// <para>Falsification: declare it <c>BoolKnob(true)</c> and every row but "1" fails.</para></summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]        // the old rule read it as OFF; so does the default
    [InlineData("yes", false)]
    [InlineData("2", false)]
    [InlineData("1", true)]
    [InlineData(" 1 ", true)]
    public void Trust_on_first_use_is_off_unless_the_value_is_exactly_one(string? raw, bool expected)
    {
        Assert.Equal(expected, With(("P1998_ALLOW_TOFU", raw)).AllowTofu);
    }

    /// <summary>Which text knobs trim and which do not is transcribed, not chosen. The status path and the
    /// status message were read as <c>value.Trim()</c> and a whitespace-only value counted as unset, so they
    /// trim; the maps directory, the SObj path and the two staff rosters were yielded RAW to a
    /// <c>File.Exists</c> or a comma split, so they must stay raw or a path with a meaningful trailing space
    /// would resolve differently than it did. None of them case-folds - <c>normalize</c> would break every
    /// one of them on a case-sensitive filesystem.</summary>
    [Fact]
    public void A_text_knob_trims_only_where_its_old_read_did()
    {
        var config = With(
            ("P1998_STATUS_FILE", "  /srv/p1998/Status.json  "),
            ("P1998_STATUS_MESSAGE", "  Back At Eight  "),
            ("P1998_MAPS", "  /srv/Maps  "),
            ("P1998_SOBJ", "  /srv/SObj.tbl  "),
            ("P1998_GMS", " Alice , Bob ,, "));

        Assert.Equal("/srv/p1998/Status.json", config.StatusFile);
        Assert.Equal("Back At Eight", config.StatusMessage);
        Assert.Equal("  /srv/Maps  ", config.MapsDir);
        Assert.Equal("  /srv/SObj.tbl  ", config.SObjTable);
        Assert.Equal(" Alice , Bob ,, ", config.Gms);
        Assert.Empty(config.Warnings);
    }

    /// <summary>A whitespace-only value on any of those is "unset", exactly as the old
    /// <c>IsNullOrWhiteSpace</c> / <c>Trim().Length &gt; 0</c> tests had it, so the call site's own fallback
    /// applies rather than an empty path being tried and failing.</summary>
    [Theory]
    [InlineData("P1998_STATUS_FILE")]
    [InlineData("P1998_STATUS_MESSAGE")]
    [InlineData("P1998_MAPS")]
    [InlineData("P1998_SOBJ")]
    [InlineData("P1998_GMS")]
    [InlineData("P1998_TESTERS")]
    [InlineData("P1998_BIND")]
    public void A_whitespace_only_text_value_is_unset(string name)
    {
        var config = With((name, "   "));

        Assert.Equal("", Assert.Single(config.Entries, e => e.Knob.Name == name).Value);
        Assert.Empty(config.Warnings);
    }

    /// <summary>The status file's one sentinel. <c>-</c> is not a path and never was: it survives the trim
    /// intact so <c>StatusFile.Disabled</c> still recognises it, which is the documented way to turn
    /// publishing off without deleting the run directory.</summary>
    [Theory]
    [InlineData("-")]
    [InlineData("  -  ")]
    public void The_status_file_sentinel_survives_the_trim(string raw)
    {
        Assert.Equal("-", With(("P1998_STATUS_FILE", raw)).StatusFile);
    }

    /// <summary>The bind address is the one knob whose value is validated at the call site rather than by
    /// its knob type, because <c>Shared/NetBind.cs</c> owns the <c>IPAddress.TryParse</c> and a configuration
    /// type that imported <c>System.Net</c> to re-do it would be the wrong place for it. The knob therefore
    /// accepts any text and never warns; the fallback to 0.0.0.0 for an unparseable value is NetBind's, and
    /// is unchanged. This fact exists so that difference is pinned rather than discovered.</summary>
    [Fact]
    public void The_bind_address_knob_passes_text_through_without_validating_it()
    {
        Assert.Equal("192.168.1.50", With(("P1998_BIND", "192.168.1.50")).BindAddress);
        Assert.Equal("not-an-address", With(("P1998_BIND", "not-an-address")).BindAddress);
        Assert.Empty(With(("P1998_BIND", "not-an-address")).Warnings);
    }

    // ---- retired gameplay variables -----------------------------------------------------------------------

    /// <summary>The four gameplay values that moved into <c>game-data/ServerTuning.csv</c>. An operator who
    /// still has one of the old variables set gets a loud line naming the key to set instead — the whole
    /// reason the declarations stay in the table after the reads are gone. Silence here would mean a value
    /// somebody deliberately configured stopping dead with nothing anywhere to explain it.</summary>
    [Theory]
    [InlineData("P1998_HIT_CRIT", "HitCrit")]
    [InlineData("P1998_HEAL_CRIT", "HealCrit")]
    [InlineData("P1998_DEATH_DELAY_MS", "DeathDespawnMs")]
    [InlineData("P1998_SPELLBOOK_CAP", "SpellBookCap")]
    public void A_retired_gameplay_variable_that_is_still_set_warns_loudly(string name, string tuningKey)
    {
        var config = With((name, "42"));

        string warning = Assert.Single(config.Warnings);
        Assert.Contains(name, warning);
        Assert.Contains("RETIRED", warning);
        Assert.Contains("IGNORED", warning);
        Assert.Contains(tuningKey, warning);
        Assert.Contains("ServerTuning.csv", warning);
    }

    /// <summary>An unset retired variable is not news, and must not clutter the banner every startup.</summary>
    [Fact]
    public void An_unset_retired_variable_says_nothing()
    {
        Assert.Empty(Defaults().Warnings);
        Assert.DoesNotContain(Defaults().Describe(), line => line.Contains("P1998_HIT_CRIT"));
    }

    /// <summary>Every retired knob names a key that actually exists in the shipped tuning file. A rename on
    /// one side without the other would send an operator to a key the loader ignores.</summary>
    [Fact]
    public void Every_retired_knob_names_a_key_that_ships_in_ServerTuning_csv()
    {
        var keys = File.ReadAllLines(Path.Combine(RepoPaths.GameDataDir(), "ServerTuning.csv"))
                       .Skip(1)
                       .Select(line => line.Split(',')[0].Trim())
                       .ToHashSet(StringComparer.Ordinal);

        foreach (var retired in ServerConfig.Knobs.All.OfType<RetiredKnob>())
            Assert.Contains(retired.TuningKey, keys);
    }

    // ---- the startup banner -------------------------------------------------------------------------------

    /// <summary>The banner answers "what is this process actually running with", which means every knob, its
    /// value, and whether that value was configured or defaulted.</summary>
    [Fact]
    public void The_banner_names_every_knob_with_its_value_and_its_source()
    {
        var config = With(("P1998_AUTOSAVE_MS", "3000"));
        string banner = string.Join('\n', config.Describe());

        Assert.Contains("P1998_AUTOSAVE_MS", banner);
        Assert.Contains("3000", banner);
        Assert.Contains("(environment)", banner);
        Assert.Contains("(default)", banner);
        // Every non-retired knob appears; the reference and the banner cannot disagree about the set.
        foreach (var knob in ServerConfig.Knobs.All.Where(k => k.Area != ConfigArea.Retired))
            Assert.Contains(knob.Name, banner);
    }

    /// <summary>A value the server REFUSED did not configure anything: the declared default is what the
    /// process is running with, so its row says "(default)" and it is not counted in "N set from the
    /// environment". The banner's whole job is to answer "what is this process actually doing" — a row
    /// reading <c>P1998_V495_WALK_MS = 200 (environment)</c> for a deployment that set <c>abc</c> answers
    /// the wrong question, and hides the mistake behind a plausible-looking number. The `!!` warning line
    /// is where the rejected value is named.
    /// <para>The same goes for a variable set to whitespace, which supplied nothing at all.</para>
    /// <para>Falsification: make FromEnvironment <c>!IsNullOrEmpty(raw)</c> again and both rows fail.
    /// </para></summary>
    [Theory]
    [InlineData("abc")]
    [InlineData("   ")]
    public void A_rejected_value_is_marked_default_and_not_counted_as_set(string raw)
    {
        var config = With(("P1998_V495_WALK_MS", raw), ("P1998_AUTOSAVE_MS", "3000"));
        var banner = config.Describe();

        Assert.Equal(200, config.WalkMs);
        string row = Assert.Single(banner, line => line.Contains("P1998_V495_WALK_MS"));
        Assert.Contains("= 200", row);
        Assert.EndsWith("(default)", row, StringComparison.Ordinal);
        // One knob was really set, and only that one is counted.
        Assert.Contains("1 set from the environment", banner[0]);
        // ... and the one that WAS set still says so.
        Assert.EndsWith("(environment)", Assert.Single(banner, line => line.Contains("P1998_AUTOSAVE_MS")),
                        StringComparison.Ordinal);
    }

    /// <summary>A retired variable that is still set keeps its banner row — it is the loudest thing the
    /// banner has to say — but carries no source tag, because a retired value is in force from neither the
    /// environment nor a default. It is also not counted as configured.</summary>
    [Fact]
    public void A_set_retired_variable_still_appears_in_the_banner_without_a_source_tag()
    {
        var config = With(("P1998_HIT_CRIT", "33"));
        var banner = config.Describe();

        string row = Assert.Single(banner, line => line.Contains("P1998_HIT_CRIT"));
        Assert.Contains("IGNORED, was '33'", row);
        Assert.DoesNotContain("(environment)", row);
        Assert.DoesNotContain("(default)", row);
        Assert.Contains("0 set from the environment", banner[0]);
    }

    // ---- the declaration table itself ---------------------------------------------------------------------

    /// <summary>Two knobs sharing a name would have one silently shadow the other in the lookup, and would
    /// render two identical rows in the reference.</summary>
    [Fact]
    public void Every_knob_name_is_unique_and_carries_a_description()
    {
        Assert.Equal(ServerConfig.Knobs.All.Count,
                     ServerConfig.Knobs.All.Select(k => k.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(ServerConfig.Knobs.All, knob =>
        {
            Assert.StartsWith("P1998_", knob.Name, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(knob.Doc), $"{knob.Name} has no description");
            Assert.False(string.IsNullOrWhiteSpace(knob.DefaultText), $"{knob.Name} has no default text");
        });
    }

    // ---- the generated reference --------------------------------------------------------------------------

    /// <summary>The committed <c>docs/common/Configuration.md</c> block is generated from the declarations
    /// above. Adding a knob, renaming one, or editing a default without re-running
    /// <c>dotnet run --project Tools -- config-doc</c> must fail here rather than shipping a reference that
    /// disagrees with the server — which is exactly how 99 of 131 variables ended up undocumented.</summary>
    [Fact]
    public void The_committed_reference_matches_the_generator()
    {
        string path = Path.Combine(RepoPaths.Root(), "docs", "common", "Configuration.md");
        string doc = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);

        int start = doc.IndexOf(ServerConfig.DocStartMarker, StringComparison.Ordinal);
        int end = doc.IndexOf(ServerConfig.DocEndMarker, StringComparison.Ordinal);
        Assert.True(start >= 0 && end >= start, "Configuration.md generated markers are missing or reversed");
        end += ServerConfig.DocEndMarker.Length;

        Assert.Equal(ServerConfig.RenderDocBlock(), doc[start..end]);
    }

    /// <summary>Every declared knob reaches the reference, in one row, under a heading. A knob left out of
    /// the render would be undocumented while looking documented.</summary>
    [Fact]
    public void The_generated_reference_covers_every_declared_knob()
    {
        string block = ServerConfig.RenderDocBlock();

        foreach (var knob in ServerConfig.Knobs.All)
            Assert.Equal(1, block.Split($"`{knob.Name}`").Length - 1);
    }
}
