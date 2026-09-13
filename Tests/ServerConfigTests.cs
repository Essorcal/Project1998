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
    [InlineData(" 1 ", true)]     // surrounding whitespace is trimmed for every boolean, uniformly
    public void A_recognised_boolean_is_read_the_same_whatever_the_knobs_default(string raw, bool expected)
    {
        Assert.Equal(expected, With(("P1998_TRUST_PROXY", raw)).TrustProxy);
        Assert.Equal(expected, With(("P1998_PASS", raw)).PassEnforce);
        Assert.Equal(expected, With(("P1998_LOG_WIRE", raw)).LogWire);
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
