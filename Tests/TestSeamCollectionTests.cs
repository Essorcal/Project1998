using System.Reflection;
using System.Text.RegularExpressions;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The guard for flaky tests as a class (workflow review 2026-09-24, item 2a): a test class that touches a
/// process-global seam does it from a collection that owns that seam, or this file is red.
///
/// <para><b>The failure it prevents.</b> Most of the facts that turned CI red in the week to 2026-09-24 were
/// not wrong about the code they pin. They were run beside another class that changed something the whole
/// process shares: the console (<c>LoginOutboundTests</c>, fork run 35164315790), the log's open/closed
/// state (<c>GmOverridesTests</c>, upstream run 35544168952), the content snapshot
/// (<c>NagnangShieldQuestTests</c>, fork run 35906110389), SQLite's write lock
/// (<c>AnnounceMonitorTests</c>, upstream run 35176466008). Each was fixed one at a time, and nothing
/// stopped the next new fact from doing the same thing from a fresh class. This reads every class under
/// <c>Tests/</c> and names any that does.</para>
///
/// <para><b>The rule.</b> A seam may be touched from its owning collection, or from a collection that runs
/// ALONE (<c>DisableParallelization</c>; today <c>"log"</c> and <c>"tile-translation"</c>, see
/// <c>Tests/Support/ExclusiveCollections.cs</c>), because nothing else is running to be disturbed. The owning
/// collections are: <c>"world"</c> for the captures and the static world hooks, since it is the one
/// collection of the parallel phase that holds them and it runs its classes one at a time; <c>"db"</c> for
/// the character store's warning sink; none for the rest, which may only be touched where nothing else runs.
/// A class with no <c>[Collection]</c> owns nothing: xunit runs it beside everything.</para>
///
/// <para><b>What it reads, and which class a use belongs to.</b>
/// <list type="bullet">
/// <item>Code with comments and the text of every string literal blanked out, so a seam named in a doc comment
/// or a message does not count. An interpolation hole is code: <c>$"{LogLineSink.Acquire()}"</c> counts, and a
/// string inside the hole is text again. SQLite's lock statement lives inside a string, and is read with
/// comments blanked only.</item>
/// <item>A test class is any type that declares a <c>[Fact]</c> or a <c>[Theory]</c>, nested ones included. Its
/// collection is its own <c>[Collection]</c>: xunit takes it off the test class, never off the type it is nested
/// in (the PR #326 review ran a nested class beside <c>"world"</c> while it sat inside a <c>"world"</c> class).
/// The parts of a partial class are one class, so a <c>[Collection]</c> on any part covers all of them.</item>
/// <item>A use belongs to the innermost test class around it; code in a nested helper type runs as the test class
/// it is nested in. Code with no test class around it can be called from anywhere, so a type like that which
/// touches a seam must be a registered wrapper under <c>Tests/Support/</c>, whose entry points are in the table,
/// or its callers would be invisible here. <c>TestProcessState</c>'s module initializer sets the environment
/// before any test runs, and is the one exemption.</item>
/// <item>An import of a seam's type reaches the seam as the type's own name does: after
/// <c>using static Shared.Log;</c> a bare <c>Shutdown()</c> counts, and after <c>using L = Shared.Log;</c>
/// <c>L.Shutdown()</c> does. A <c>global using</c> counts in every file.</item>
/// </list>
/// What it does not follow: inheritance between test classes. A <c>[Collection]</c> or a test method that a
/// class has only through a base type is not seen. No class in <c>Tests/</c> is built that way today, and
/// <see cref="TheScannerSeesEveryTestClassXunitSeesInTheCollectionXunitGivesIt"/> fails the day one is.</para>
///
/// <para><b>Not seams</b> (inventoried, deliberately left out): <c>TestProcessState.LoadContent</c>
/// re-publishes the real content and is safe anywhere; <c>SendCounters.Game</c> and
/// <c>Session.PositionWritesUnderWorldLock</c> are process-wide counters their facts read as bounds or deltas;
/// the instance hooks on a <c>World</c> (<c>SweepProbeForTest</c>, <c>PreSweepProbeForTest</c>) reach only
/// the world a test built.</para>
///
/// <para><b>Also not seams today: the four Lua script hosts</b> (<c>SpellScript.Load</c>, <c>ItemScript.Load</c>,
/// <c>NpcScript.Load</c>, <c>MobScript.Load</c>). Each is one host for the whole process, so loading a file of
/// your own into one changes what every running test's casts and dialogs do. The one class that does it,
/// <c>SpellDispatchRouteTests</c>, loads a probe copy of the real <c>spell_verbs.lua</c> whose wrapper calls the
/// real verb for every caster but its own probe caster, so a cast anywhere else behaves as with the real file;
/// it holds <c>TestProcessState.Gate</c> from the probe's load until the real file is back, so no content load
/// can land in between. A load that changed an answer for other casters would be a seam, and would belong in a
/// collection that runs alone (the PR #326 review, F5).</para>
///
/// <para>Falsified three ways, each red in Debug and Release and green again once restored: deleting
/// <c>[Collection("log")]</c> from <c>SendMapLogTests</c> names its <c>ConsoleTap.AcquireAsync</c> (line 31)
/// and its reflected write to the wire switch (line 35); a new class with no collection that takes
/// <c>LogLineSink.Acquire</c> is named at that line; and dropping <c>DisableParallelization</c> from
/// <c>"log"</c> fails <see cref="TheCollectionsTheRuleTrustsRunAloneAndTheParallelOnesDoNot"/> and names
/// the classes in <c>"log"</c> that hold a seam, since <c>"log"</c> owns seams only by running alone. The
/// shapes the PR #326 review found missed or wrongly flagged are pinned in
/// <see cref="TheScannerFindsSeamsInCodeAndOnlyInCode"/> and <see cref="APartialClassIsOneClassAcrossItsFiles"/>.</para>
/// </summary>
public class TestSeamCollectionTests
{
    /// <summary>One process-global seam: what it is, why it is one, which collections own it (besides the ones
    /// that run alone, which own everything), and how to find it: <see cref="Accesses"/> are the members of the
    /// types it lives on, matched as <c>Type.Member</c> and through any import of the type; <see cref="Bare"/>
    /// matches forms that need no type; <see cref="Text"/> is read with string literals kept.</summary>
    internal sealed record Seam(string Name, string Why, string[] Owners, Access[] Accesses,
                                Regex? Bare = null, Regex? Text = null, Regex? ExemptCode = null)
    {
        /// <summary>Every <c>Type.Member</c> form, as one pattern.</summary>
        public Regex? Qualified { get; } = Accesses.Length == 0
            ? null
            : Rx(string.Join("|", Accesses.Select(a => a.Type == AnyType
                ? $@"(?<![\w.])(?:[A-Z]\w*\s*\.\s*)+(?:{a.Member})"
                : $@"\b{a.Type}\s*\.\s*(?:{a.Member})")));

        /// <summary>The forms an import adds: <c>Alias.Member</c> for an alias of one of the seam's types, and a
        /// bare <c>Member</c> for a <c>using static</c> of one.</summary>
        public IEnumerable<Regex> ThroughImport(Import import)
        {
            var members = Accesses.Where(a => a.Type == AnyType ? import.Production : a.Type == import.TypeName)
                                  .Select(a => a.Member).ToList();
            if (members.Count == 0) yield break;
            string any = string.Join("|", members);
            yield return import.Alias is { } alias
                ? Rx($@"(?<![\w.@]){Regex.Escape(alias)}\s*\.\s*(?:{any})")
                : Rx($@"(?<![\w.])(?:{any})");
        }
    }

    /// <summary>A member of a type a seam lives on: <see cref="Type"/> is the type's name, or
    /// <see cref="AnyType"/> for any capitalised type (a static <c>*ForTest</c> hook can be on any production
    /// type); <see cref="Member"/> is a pattern for what follows <c>Type.</c>.</summary>
    internal sealed record Access(string Type, string Member);

    private const string AnyType = "*";

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static readonly Seam[] Seams =
    {
        new("the log line sink",
            "Log.LineSinkForTest is one process-wide slot, and a capture collects every running test's lines",
            new[] { "world" },
            new[] { new Access("LogLineSink", @"Acquire\b"), new Access("Log", @"LineSinkForTest\b") }),
        new("Console.Out",
            "Console.Out is one process-wide slot, and a capture collects every running test's output",
            new[] { "world" },
            new[] { new Access("ConsoleTap", @"Acquire(?:Async)?\b"), new Access("Console", @"Set(?:Out|Error|In)\s*\(") }),
        new("the log's state",
            "closing the log, changing what it admits or attaching its file changes logging for every running test",
            Array.Empty<string>(),
            new[]
            {
                new Access("Log", @"(?:Shutdown|RestartWriterForTest|AttachFile|Configure|AdmitOverrideForTest|" +
                                  @"ResetDroppedCountsForTest|DroppedCountsForTest)\b"),
                new Access("LogShutdownWindow", @"Enter\b"),
            }),
        new("a static field set by reflection",
            "a static flipped by reflection (Log's wire switch, say) changes it for every running test",
            Array.Empty<string>(), Array.Empty<Access>(),
            Bare: Rx(@"\.\s*SetValue\s*\(\s*null\s*,")),
        new("a static test hook",
            "a static *ForTest hook on a production type fires for every session and world in the process",
            new[] { "world" },
            new[] { new Access(AnyType, @"\w+ForTests?\s*=(?!=)"), new Access("PhaseProbe", @"CostingAtLeast\b") }),
        new("GmOverrides' world-wide statics",
            "GmOverrides' statics are read by every World in the process",
            new[] { "world" },
            new[] { new Access("GmOverrides", @"\w+\s*=(?!=)") }),
        new("the staff roster",
            "StaffAccounts.Load replaces the roster for the whole process",
            new[] { "world" },
            new[] { new Access("StaffAccounts", @"Load\s*\(") }),
        new("the Lua gate",
            "Session.EnterScriptGate is one gate for every script in the process",
            new[] { "world" }, Array.Empty<Access>(),
            Bare: Rx(@"\bEnterScriptGate\s*\(")),
        new("a stubbed content snapshot",
            "every test in the process reads the content snapshot a swapped table publishes",
            Array.Empty<string>(),
            new[]
            {
                new Access("EraCalendar", @"PathOverrideForTests\b"),
                new Access("Csv", @"(?:WarningObserverForTests|OpenObserverForTests|Warn)\s*=(?!=)"),
            },
            Bare: Rx(@"\b(?:ReplaceSpecForTests|OverridePathForTests)\s*\(|\bLoadStepForTests\b")),
        new("process configuration",
            "the environment, ServerConfig and the channel-port pair are read by every test in the process",
            Array.Empty<string>(),
            new[]
            {
                new Access("Environment", @"SetEnvironmentVariable\s*\("),
                new Access("ServerConfig", @"ReloadForTests\s*\("),
                new Access("ChannelPorts", @"(?:ConfigureLoginPair|ConfigureGamePair|ResetForTests)\s*\("),
            }),
        new("the character store's warning sink",
            "CharacterStore.Warn is one process-wide sink",
            new[] { "db" },
            new[] { new Access("CharacterStore", @"Warn\s*=(?!=)") }),
        new("SQLite's write lock on the process database",
            "the write lock is per file and the process has one database file; take it on an IsolatedDatabase",
            Array.Empty<string>(), Array.Empty<Access>(),
            Text: Rx(@"\bBEGIN\s+(IMMEDIATE|EXCLUSIVE)\b"),
            ExemptCode: Rx(@"\bIsolatedDatabase\b")),
    };

    /// <summary>The <c>Tests/Support/</c> types that wrap a seam. Their entry points are in <see cref="Seams"/>,
    /// so a class that calls them is checked; a new wrapper has to be added to both.</summary>
    private static readonly HashSet<string> RegisteredWrappers =
        new(StringComparer.Ordinal) { "ConsoleTap", "LogLineSink", "LogShutdownWindow", "PhaseProbe" };

    /// <summary>The module initializer: it points <c>P1998_STATE</c> at the run's temp directory before any
    /// test exists, so it runs beside nothing.</summary>
    private static readonly HashSet<string> Exempt = new(StringComparer.Ordinal) { "TestProcessState" };

    /// <summary>The fact the whole rule exists for. Each violation it reports names the file and line, the
    /// class, the seam, the collection the class is in and the collections it belongs in.</summary>
    [Fact]
    public void EveryClassThatTouchesAProcessGlobalSeamSitsInACollectionThatOwnsIt()
    {
        var files = TestSources();
        Assert.True(files.Count > 100, $"found only {files.Count} source files under Tests/; is that the Tests folder?");

        var violations = Violations(files, ExclusiveCollections());
        Assert.True(violations.Count == 0,
            "classes touching a process-global seam from a collection that does not own it:\n"
            + string.Join("\n", violations));
    }

    /// <summary>The premise of the rule, pinned: the two collections the rule hands every seam to are the
    /// ones xunit runs alone, and the two parallel-phase collections are not. If <c>"log"</c> lost its
    /// <c>DisableParallelization</c>, every capture in it would run beside <c>"world"</c>'s again and the
    /// rule above would still be green, so this is the other half of it.</summary>
    [Fact]
    public void TheCollectionsTheRuleTrustsRunAloneAndTheParallelOnesDoNot()
    {
        var exclusive = ExclusiveCollections();
        Assert.Contains("log", exclusive);
        Assert.Contains("tile-translation", exclusive);

        var defined = CollectionDefinitions();
        Assert.True(defined.ContainsKey("world"), "no [CollectionDefinition(\"world\")]");
        Assert.True(defined.ContainsKey("db"), "no [CollectionDefinition(\"db\")]");
        Assert.DoesNotContain("world", exclusive);
        Assert.DoesNotContain("db", exclusive);
    }

    /// <summary>The scanner's two judgements about a class, held against what the compiled assembly says: the
    /// types it takes for test classes are exactly the types that declare a <c>[Fact]</c> or <c>[Theory]</c>
    /// method, nested ones included, and the collection it gives each is the one xunit reads, the class's own
    /// <c>[Collection]</c> or a base type's. A difference means the rule above is judging a class by the wrong
    /// collection, or not judging it at all.</summary>
    [Fact]
    public void TheScannerSeesEveryTestClassXunitSeesInTheCollectionXunitGivesIt()
    {
        var scanned = TestClasses(TestSources());

        var compiled = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in typeof(TestSeamCollectionTests).Assembly.GetTypes())
        {
            const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                          | BindingFlags.Static | BindingFlags.DeclaredOnly;
            if (!type.GetMethods(Declared).Any(m => m.IsDefined(typeof(FactAttribute), inherit: true))) continue;
            compiled[type.FullName!] = XunitCollectionOf(type) ?? "(none)";
        }

        Assert.True(compiled.Count > 100, $"only {compiled.Count} test classes in the assembly");
        Assert.Equal(compiled.Select(kv => $"{kv.Key} -> {kv.Value}"),
                     scanned.Select(kv => $"{kv.Key} -> {kv.Value ?? "(none)"}"));
    }

    /// <summary>The scanner, pinned on sources whose answer is known, so a change to how it reads code
    /// cannot quietly turn the fact above into one that finds nothing. Each case is one source; the expected
    /// violations are listed by seam.</summary>
    [Theory]
    [InlineData("""[Collection("world")] public class A { [Fact] public void F() { Log.Shutdown(); } }""", "the log's state")]
    [InlineData("""public class A { [Fact] public void F() { using var s = LogLineSink.Acquire(); } }""", "the log line sink")]
    [InlineData("""[Collection("db")] public class A { [Fact] public void F() { Session.TradeOpenProbeForTest = s => { }; } }""", "a static test hook")]
    [InlineData("""[Collection("world")] public class A { [Fact] public void F() { Content.OverridePathForTests(id, p); } }""", "a stubbed content snapshot")]
    [InlineData("""public class A { [Fact] public void F() { Environment.SetEnvironmentVariable("X", "1"); } }""", "process configuration")]
    [InlineData("""[Collection("world")] public class A { [Fact] public void F() { WireFlag.SetValue(null, true); } }""", "a static field set by reflection")]
    [InlineData("""public class A { [Fact] public void F() { using var cn = Db.Open(); Run(cn, "BEGIN """ + "IMMEDIATE" + """;"); } }""", "SQLite's write lock on the process database")]
    [InlineData("""public class A { private static class Helper { static void G() => StaffAccounts.Load(); } [Fact] public void F() { } }""", "the staff roster")]
    // A nested test class is its own class to xunit: the outer class's collection is not its collection.
    [InlineData("""[Collection("world")] public class Outer { public class Inner { [Fact] public void F() { using var s = LogLineSink.Acquire(); } } }""", "the log line sink")]
    // An import of a seam type reaches the seam without naming the type at the call.
    [InlineData("""using static Shared.Log; public class A { [Fact] public void F() { Shutdown(); } }""", "the log's state")]
    [InlineData("""using static Tests.Support.LogLineSink; public class A { [Fact] public void F() { using var s = Acquire(); } }""", "the log line sink")]
    [InlineData("""using L = Shared.Log; public class A { [Fact] public void F() { L.Shutdown(); } }""", "the log's state")]
    [InlineData("""using s = Server.Session; public class A { [Fact] public void F() { s.TradeOpenProbeForTest = null; } }""", "a static test hook")]
    // An interpolation hole is code, in every kind of interpolated string.
    [InlineData("""public class A { [Fact] public void F() { var a = $"lines: {Count(LogLineSink.Acquire())}"; } }""", "the log line sink")]
    [InlineData("""public class A { [Fact] public void F() { var a = $@"x {Count(LogLineSink.Acquire())} y"; } }""", "the log line sink")]
    [InlineData(""""public class A { [Fact] public void F() { var a = $$"""{x} {{Count(LogLineSink.Acquire())}}"""; } }"""", "the log line sink")]
    // Parts of a partial class are one class; with no [Collection] on any part, there is none.
    [InlineData("""public partial class P { [Fact] public void A() { } } public partial class P { [Fact] public void B() { using var s = LogLineSink.Acquire(); } }""", "the log line sink")]
    // ...and the same seams where they are allowed, or not seams at all:
    [InlineData("""[Collection("log")] public class A { [Fact] public void F() { Log.Shutdown(); Log.RestartWriterForTest(); } }""", "")]
    [InlineData("""[Collection("world")] public class A { [Fact] public void F() { using var s = LogLineSink.Acquire(); Session.TradeOpenProbeForTest = null; } }""", "")]
    [InlineData("""[Collection("tile-translation")] public class A { [Fact] public void F() { Content.OverridePathForTests(id, p); } }""", "")]
    [InlineData("""public class A { [Fact] public void F() { _fx.World.SweepProbeForTest = m => { }; var x = Content.SnapshotIdentityForTests; } }""", "")]
    [InlineData("public class A { /// Calls Log.Shutdown() and LogLineSink.Acquire(). <see cref=\"Log.Shutdown\"/>\n" +
                "[Fact] public void F() { var s = \"Log.Shutdown(); ConsoleTap.Acquire()\"; var v = @\"Log.Shutdown(\"\"x\"\")\"; " +
                "var r = \"\"\"Console.SetOut(t);\"\"\"; /* Log.Shutdown(); */ char q = '\"'; } }", "")]
    [InlineData("""public class A { [Fact] public void F() { using var db = new IsolatedDatabase(); Run(db.Open(), "BEGIN """ + "IMMEDIATE" + """;"); } }""", "")]
    [InlineData("""[Collection("db")] public class A { [Fact] public void F() { CharacterStore.Warn = m => { }; } }""", "")]
    // A seam named in a string inside a hole is text (PR #326 review, F2), as is the text around the holes.
    [InlineData("""public class A { [Fact] public void F() { bool closed = false; var a = $"state: {(closed ? "after Log.Shutdown()" : "open")}"; } }""", "")]
    [InlineData(""""public class A { [Fact] public void F() { string who = "x"; var a = $"{who} would call Log.Shutdown() and LogLineSink.Acquire() here"; var b = $@"{who} Console.SetOut({who}) ""ConsoleTap.Acquire()"""; var c = $$"""{{who}} Environment.SetEnvironmentVariable("X", "1") and Session.TradeOpenProbeForTest = null"""; } }"""", "")]
    [InlineData("""public class A { [Fact] public void F() { double x = 1; var a = $"{x,8:F2} {x:0.0} {(x > 0 ? 1 : 2)} {new[] { 1 }.Length}"; } }""", "")]
    // A nested test class with a collection of its own, in an outer class that has none.
    [InlineData("""public class C { [Collection("log")] public class Inner { [Fact] public void F() { Log.Shutdown(); } } }""", "")]
    // A helper type nested in a test class runs as that class.
    [InlineData("""[Collection("world")] public class A { [Fact] public void F() => H.T(); private static class H { public static void T() { using var s = LogLineSink.Acquire(); } } }""", "")]
    // The [Collection] of a partial class can sit on any part (PR #326 review, F2).
    [InlineData("""[Collection("log")] public partial class P { [Fact] public void A() { } } public partial class P { [Fact] public void B() { Log.Shutdown(); } }""", "")]
    // An import of a type that carries no seam changes nothing.
    [InlineData("""using static System.Math; public class A { [Fact] public void F() { var loadForTest = Max(1, 2); } }""", "")]
    public void TheScannerFindsSeamsInCodeAndOnlyInCode(string source, string expectedSeam)
    {
        var found = Violations(new[] { ("Sample.cs", "namespace Tests;\n" + source) }, new[] { "log", "tile-translation" });
        if (expectedSeam.Length == 0)
            Assert.Empty(found);
        else
            Assert.Contains(found, v => v.Contains($"touches {expectedSeam} ", StringComparison.Ordinal));
    }

    /// <summary>A class under <c>Tests/Support/</c> that touches a seam and is not a registered wrapper is a
    /// violation too: its callers would never match the table.</summary>
    [Fact]
    public void AnUnregisteredSupportHelperThatTouchesASeamIsReported()
    {
        var found = Violations(
            new[] { ("Support/NewTap.cs", "namespace Tests.Support;\ninternal sealed class NewTap { void I() => Console.SetOut(null!); }") },
            new[] { "log", "tile-translation" });
        Assert.Contains(found, v => v.Contains("NewTap", StringComparison.Ordinal)
                                    && v.Contains("registered wrapper", StringComparison.Ordinal));
    }

    /// <summary>A partial class is one class to C# and to xunit, so a <c>[Collection]</c> on one part covers
    /// the seam in another part, in another file; with no <c>[Collection]</c> on any part, the seam is
    /// reported (the PR #326 review's plants <c>ZzPartialProbe</c> and <c>ZzPartialBareProbe</c>).</summary>
    [Fact]
    public void APartialClassIsOneClassAcrossItsFiles()
    {
        const string partWithSeam =
            "namespace Tests;\npublic partial class P { [Fact] public void B() { using var s = LogLineSink.Acquire(); } }";
        var attributedElsewhere = Violations(
            new[] { ("P.A.cs", "namespace Tests;\n[Collection(\"log\")]\npublic partial class P { [Fact] public void A() { } }"),
                    ("P.B.cs", partWithSeam) },
            new[] { "log", "tile-translation" });
        Assert.Empty(attributedElsewhere);

        var attributedNowhere = Violations(
            new[] { ("P.A.cs", "namespace Tests;\npublic partial class P { [Fact] public void A() { } }"),
                    ("P.B.cs", partWithSeam) },
            new[] { "log", "tile-translation" });
        Assert.Contains(attributedNowhere, v => v.StartsWith("P.B.cs:2 P touches the log line sink ", StringComparison.Ordinal));
    }

    // ===== the scanner ===================================================================================

    /// <summary>Every <c>.cs</c> file under <c>Tests/</c> but build output, as (path relative to it, source).</summary>
    private static List<(string Path, string Source)> TestSources()
    {
        string root = Path.Combine(RepoPaths.Root(), "Tests");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(root, f))
            .Select(f => (Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText(f)))
            .ToList();
    }

    private static bool IsBuildOutput(string root, string file)
    {
        var parts = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p.Equals("bin", StringComparison.OrdinalIgnoreCase)
                              || p.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The collections xunit runs alone in this assembly: every <c>[CollectionDefinition]</c> with
    /// <c>DisableParallelization = true</c>.</summary>
    internal static string[] ExclusiveCollections() =>
        CollectionDefinitions().Where(kv => kv.Value).Select(kv => kv.Key).ToArray();

    /// <summary>Every collection definition in this assembly, by name, with its <c>DisableParallelization</c>.
    /// Read off the attribute's metadata, because xunit's attribute does not keep its constructor's name
    /// argument in a property; xunit itself reads it the same way.</summary>
    private static Dictionary<string, bool> CollectionDefinitions() =>
        typeof(TestSeamCollectionTests).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributesData())
            .Where(d => d.AttributeType == typeof(CollectionDefinitionAttribute))
            .ToDictionary(
                d => (string)d.ConstructorArguments[0].Value!,
                d => d.NamedArguments.Any(n => n.MemberName == nameof(CollectionDefinitionAttribute.DisableParallelization)
                                               && n.TypedValue.Value is true),
                StringComparer.Ordinal);

    /// <summary>The <c>[Collection]</c> xunit gives a test class: its own, or the nearest base type's (the
    /// attribute is inherited). Null when there is none.</summary>
    private static string? XunitCollectionOf(Type type)
    {
        for (var t = type; t is not null; t = t.BaseType)
            foreach (var d in t.GetCustomAttributesData())
                if (d.AttributeType == typeof(CollectionAttribute))
                    return (string)d.ConstructorArguments[0].Value!;
        return null;
    }

    /// <summary>Every test class the scanner finds, by full name (<c>Namespace.Outer+Inner</c>, as reflection
    /// writes it), with the collection it reads for it.</summary>
    private static SortedDictionary<string, string?> TestClasses(IEnumerable<(string Path, string Source)> files)
    {
        var scan = new Scan(files);
        var classes = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var type in scan.Files.SelectMany(f => f.Types).Where(scan.IsTestClass))
            classes[type.FullName] = scan.CollectionOf(type);
        return classes;
    }

    /// <summary>Every violation in <paramref name="files"/> (relative path, source), one line each.</summary>
    internal static List<string> Violations(IEnumerable<(string Path, string Source)> files, IReadOnlyCollection<string> exclusive)
    {
        var scan = new Scan(files);
        var violations = new List<string>();
        foreach (var file in scan.Files)
        {
            bool support = file.Path.StartsWith("Support/", StringComparison.Ordinal);
            var imports = file.Imports.Where(i => !i.Global).Concat(scan.GlobalImports).ToList();
            foreach (var seam in Seams)
            {
                var reported = new HashSet<TypeDecl>();
                foreach (var (at, via) in Hits(seam, file, imports).OrderBy(h => h.At))
                {
                    var innermost = file.Innermost(at);
                    var outermost = innermost;
                    while (outermost?.Parent is not null) outermost = outermost.Parent;
                    if (outermost is not null && Exempt.Contains(outermost.Name)) continue;

                    var owner = innermost;   // the innermost test class around the use
                    while (owner is not null && !scan.IsTestClass(owner)) owner = owner.Parent;
                    var subject = owner ?? outermost;
                    if (subject is not null && seam.ExemptCode is { } exempt
                        && exempt.IsMatch(file.Code.AsSpan(subject.Start, subject.Close + 1 - subject.Start)))
                        continue;
                    if (subject is not null && !reported.Add(subject)) continue;

                    int line = 1 + file.Source.AsSpan(0, at).Count('\n');
                    string use = $"touches {seam.Name} (`{Snippet(file.Source, at)}`{via})";
                    if (owner is null)
                    {
                        if (support && outermost is not null && RegisteredWrappers.Contains(outermost.Name)) continue;
                        violations.Add($"{file.Path}:{line} {outermost?.DisplayName ?? "(outside any type)"} {use} and is " +
                                       "neither a test class nor a registered wrapper: no [Fact] or [Theory] is in it or " +
                                       "around it, so the classes that call it are invisible here. Move the seam into the " +
                                       "test class, or make it a Tests/Support wrapper: add its entry points to " +
                                       "TestSeamCollectionTests.Seams and its name to RegisteredWrappers");
                        continue;
                    }

                    string? collection = scan.CollectionOf(owner);
                    var allowed = seam.Owners.Concat(exclusive).ToArray();
                    if (collection is not null && allowed.Contains(collection, StringComparer.Ordinal)) continue;
                    violations.Add($"{file.Path}:{line} {owner.DisplayName} {use} from " +
                                   (collection is null ? "no collection" : $"collection \"{collection}\"") +
                                   $"; {seam.Why}, so it belongs in " +
                                   string.Join(" or ", allowed.Select(a => $"[Collection(\"{a}\")]")));
                }
            }
        }
        return violations;
    }

    /// <summary>Where <paramref name="seam"/> is touched in <paramref name="file"/>, with how it was reached
    /// when that was through an import.</summary>
    private static IEnumerable<(int At, string Via)> Hits(Seam seam, SourceFile file, IEnumerable<Import> imports)
    {
        foreach (var rx in new[] { seam.Qualified, seam.Bare })
            if (rx is not null)
                foreach (Match m in rx.Matches(file.Code)) yield return (m.Index, "");
        if (seam.Text is { } text)
            foreach (Match m in text.Matches(file.Text)) yield return (m.Index, "");
        foreach (var import in imports)
            foreach (var rx in seam.ThroughImport(import))
                foreach (Match m in rx.Matches(file.Code)) yield return (m.Index, $", through `{import.Directive}`");
    }

    private static string Snippet(string source, int at)
    {
        int end = source.IndexOf('\n', at);
        string s = source[at..(end < 0 ? source.Length : end)].Trim();
        return s.Length > 60 ? s[..60] + "..." : s;
    }

    /// <summary>Every file of one scan, and what holds across files: the parts of a partial class, which are
    /// one class, and <c>global using</c> directives, which apply to every file.</summary>
    private sealed class Scan
    {
        private readonly Dictionary<string, List<TypeDecl>> _parts;

        public Scan(IEnumerable<(string Path, string Source)> files)
        {
            Files = files.Select(f => new SourceFile(f.Path, f.Source)).ToList();
            GlobalImports = Files.SelectMany(f => f.Imports).Where(i => i.Global).ToList();
            _parts = Files.SelectMany(f => f.Types).GroupBy(t => t.FullName, StringComparer.Ordinal)
                          .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        }

        public List<SourceFile> Files { get; }
        public List<Import> GlobalImports { get; }

        /// <summary>A type is a test class if any of its parts declares a test method.</summary>
        public bool IsTestClass(TypeDecl type) => _parts[type.FullName].Any(p => p.DeclaresTests);

        /// <summary>The <c>[Collection]</c> on any part of the class; C# allows it on one part only.</summary>
        public string? CollectionOf(TypeDecl type) =>
            _parts[type.FullName].Select(p => p.OwnCollection).FirstOrDefault(c => c is not null);
    }

    /// <summary>One source file: its source, its text with comments blanked, its code with comments and literal
    /// text blanked (see <see cref="Blank"/>), the types it declares and the imports it makes.</summary>
    private sealed class SourceFile
    {
        public SourceFile(string path, string source)
        {
            Path = path;
            Source = source;
            (Text, Code) = Blank(source);
            Types = ParseTypes(Text, Code);
            Imports = ParseImports(Code);
        }

        public string Path { get; }
        public string Source { get; }
        public string Text { get; }
        public string Code { get; }
        public List<TypeDecl> Types { get; }
        public List<Import> Imports { get; }

        /// <summary>The innermost type whose declaration spans <paramref name="at"/>, or null.</summary>
        public TypeDecl? Innermost(int at) =>
            Types.Where(t => t.Start <= at && at <= t.Close).MaxBy(t => t.Start);
    }

    /// <summary>A type declaration: the name reflection gives the type (<see cref="FullName"/>, the identity of a
    /// partial class's parts), the name a report shows, the type it is nested in, its own <c>[Collection]</c>,
    /// whether it declares a test method, and its span from the declaration keyword to the closing brace.</summary>
    private sealed class TypeDecl
    {
        public required string Name { get; init; }
        public required string FullName { get; init; }
        public required string DisplayName { get; init; }
        public TypeDecl? Parent { get; init; }
        public string? OwnCollection { get; init; }
        public required int Start { get; init; }
        public int Close { get; set; }
        public bool DeclaresTests { get; set; }
    }

    /// <summary>A <c>using static</c> (no <see cref="Alias"/>) or <c>using Alias = </c> directive naming
    /// <see cref="TypeName"/>; <see cref="Production"/> unless the type is the framework's or xunit's.</summary>
    internal sealed record Import(string Directive, string? Alias, string TypeName, bool Production, bool Global);

    private static readonly Regex UsingDirective =
        new(@"(?<![\w.])(?<global>global\s+)?using\s+(?:static\s+|(?<alias>[A-Za-z_]\w*)\s*=\s*)" +
            @"(?:global\s*::\s*)?(?<target>[A-Za-z_][\w.]*)(?:\s*<[^;]*>)?\s*;", RegexOptions.Compiled);

    private static List<Import> ParseImports(string code)
    {
        var imports = new List<Import>();
        foreach (Match m in UsingDirective.Matches(code))
        {
            string target = m.Groups["target"].Value;
            string typeName = target[(target.LastIndexOf('.') + 1)..];
            bool production = !(target.StartsWith("System", StringComparison.Ordinal)
                                || target.StartsWith("Xunit", StringComparison.Ordinal)
                                || target.StartsWith("Microsoft", StringComparison.Ordinal));
            string directive = Regex.Replace(m.Value, @"\s+", " ");
            imports.Add(new Import(directive, m.Groups["alias"].Success ? m.Groups["alias"].Value : null,
                                   typeName, production, m.Groups["global"].Success));
        }
        return imports;
    }

    private static readonly Regex TypeKeyword =
        new(@"\G(?:record\s+(?:class|struct)|class|record|struct|interface|enum)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.Compiled);

    private static readonly Regex NamespaceDecl =
        new(@"\Gnamespace\s+(?<name>[A-Za-z_][\w.]*)\s*(?<end>[{;])", RegexOptions.Compiled);

    private static readonly Regex CollectionAttribute =
        new(@"\[\s*(?:Xunit\s*\.\s*)?Collection\s*\(\s*""(?<name>[^""]*)""\s*\)\s*\]", RegexOptions.Compiled);

    /// <summary>A <c>[Fact]</c> or <c>[Theory]</c> (alone or in an attribute list) where a member can start.</summary>
    private static readonly Regex TestAttribute =
        new(@"(?:^|[{};\]])\s*\[\s*(?:[^\]\[]*?,\s*)?(?:Xunit\s*\.\s*)?(?:Fact|Theory)(?:Attribute)?\s*[\](,]",
            RegexOptions.Compiled | RegexOptions.Multiline);

    private enum ScopeKind { Namespace, Type, Code }

    private sealed record Scope(ScopeKind Kind, string Namespace, TypeDecl? Type);

    /// <summary>Every type declared in a file, nested ones included, read off the code view: a brace that does not
    /// open a namespace or a type body opens code, and no type is looked for inside code.</summary>
    private static List<TypeDecl> ParseTypes(string text, string code)
    {
        var types = new List<TypeDecl>();
        var scopes = new Stack<Scope>();
        string fileNamespace = "";
        int regionStart = 0;   // where the current member's attributes and modifiers begin
        for (int i = 0; i < code.Length; i++)
        {
            char ch = code[i];
            Scope? top = scopes.Count == 0 ? null : scopes.Peek();
            string ns = top?.Namespace ?? fileNamespace;
            if (ch == '{') { scopes.Push(new Scope(ScopeKind.Code, ns, top?.Type)); continue; }
            if (ch == '}')
            {
                if (scopes.TryPop(out var closed) && closed.Kind == ScopeKind.Type) closed.Type!.Close = i;
                if (scopes.Count == 0 || scopes.Peek().Kind != ScopeKind.Code) regionStart = i + 1;
                continue;
            }
            if (top is { Kind: ScopeKind.Code }) continue;
            if (ch == ';') { regionStart = i + 1; continue; }
            if (!char.IsLetter(ch) || (i > 0 && (char.IsLetterOrDigit(code[i - 1]) || code[i - 1] is '_' or '@'))) continue;

            if (NamespaceDecl.Match(code, i) is { Success: true } nsDecl)
            {
                string name = nsDecl.Groups["name"].Value;
                int end = nsDecl.Groups["end"].Index;
                if (code[end] == ';') fileNamespace = name;
                else scopes.Push(new Scope(ScopeKind.Namespace, ns.Length == 0 ? name : ns + "." + name, null));
                i = end;
                regionStart = end + 1;
                continue;
            }

            var m = TypeKeyword.Match(code, i);
            if (!m.Success) continue;
            int before = i - 1;
            while (before >= 0 && char.IsWhiteSpace(code[before])) before--;
            if (before >= 0 && code[before] is ':' or ',' or '(' or '<' or '.' or '=')   // `where T : class`, not a type
            {
                i = m.Index + m.Length - 1;
                continue;
            }
            int open = code.IndexOf('{', m.Index + m.Length);
            int semi = code.IndexOf(';', m.Index + m.Length);
            if (open < 0 || (semi >= 0 && semi < open))   // no body: a positional record
            {
                i = (semi < 0 ? code.Length : semi) - 1;
                continue;
            }

            var parent = top?.Type;
            string typeName = m.Groups["name"].Value;
            var decl = new TypeDecl
            {
                Name = typeName,
                FullName = parent is not null ? parent.FullName + "+" + typeName
                         : ns.Length == 0 ? typeName : ns + "." + typeName,
                DisplayName = parent is not null ? parent.DisplayName + "+" + typeName : typeName,
                Parent = parent,
                OwnCollection = CollectionAttribute.Matches(text[regionStart..m.Index]).LastOrDefault()?.Groups["name"].Value,
                Start = m.Index,
                Close = code.Length - 1,
            };
            types.Add(decl);
            scopes.Push(new Scope(ScopeKind.Type, ns, decl));
            i = open;
            regionStart = open + 1;
        }

        foreach (Match attribute in TestAttribute.Matches(code))
        {
            int at = attribute.Index + attribute.Length - 1;
            if (types.Where(t => t.Start <= at && at <= t.Close).MaxBy(t => t.Start) is { } owner) owner.DeclaresTests = true;
        }
        return types;
    }

    /// <summary>The source twice, offsets kept: <c>Text</c> with every comment blanked, and <c>Code</c> with the
    /// text of every string and character literal blanked as well. An interpolation hole is code, so it stays in
    /// <c>Code</c>, with any literal inside it blanked in turn; its braces and any format clause are text.
    /// Newlines survive both, so line numbers do.</summary>
    internal static (string Text, string Code) Blank(string source)
    {
        var lexer = new Lexer(source);
        lexer.ScanCode(0, inHole: false);
        return (new string(lexer.Text), new string(lexer.Code));
    }

    private sealed class Lexer(string s)
    {
        public char[] Text { get; } = s.ToCharArray();
        public char[] Code { get; } = s.ToCharArray();

        private void BlankBoth(int from, int to)
        {
            for (int k = from; k < to; k++) if (s[k] != '\n') { Text[k] = ' '; Code[k] = ' '; }
        }

        private void BlankCode(int from, int to)
        {
            for (int k = from; k < to; k++) if (s[k] != '\n') Code[k] = ' ';
        }

        /// <summary>Code from <paramref name="i"/>: to the end of the source, or, in an interpolation hole, to the
        /// brace that closes the hole, whose index it returns. In a hole, a <c>:</c> outside every bracket starts
        /// the format clause, which is text up to that brace (C# makes a conditional in a hole parenthesise).</summary>
        public int ScanCode(int i, bool inHole)
        {
            int braces = 0, brackets = 0;
            while (i < s.Length)
            {
                char c = s[i];
                char next = i + 1 < s.Length ? s[i + 1] : '\0';
                if (c == '/' && next == '/')
                {
                    int end = s.IndexOf('\n', i);
                    end = end < 0 ? s.Length : end;
                    BlankBoth(i, end);
                    i = end;
                    continue;
                }
                if (c == '/' && next == '*')
                {
                    int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    end = end < 0 ? s.Length : end + 2;
                    BlankBoth(i, end);
                    i = end;
                    continue;
                }
                if (StartsString(i)) { i = ScanString(i); continue; }
                if (c == '\'') { i = ScanChar(i); continue; }
                if (inHole)
                {
                    if (c == '}' && braces == 0) return i;
                    if (c == '{') braces++;
                    else if (c == '}') braces--;
                    else if (c is '(' or '[') brackets++;
                    else if (c is ')' or ']') brackets--;
                    else if (c == ':' && next == ':') { i += 2; continue; }   // global::
                    else if (c == ':' && braces == 0 && brackets == 0)
                    {
                        int close = s.IndexOf('}', i);
                        close = close < 0 ? s.Length : close;
                        BlankCode(i, close);
                        return close;
                    }
                }
                i++;
            }
            return s.Length;
        }

        private bool StartsString(int i)
        {
            int k = i;
            while (k < s.Length && (s[k] == '$' || s[k] == '@')) k++;
            return k < s.Length && s[k] == '"';
        }

        /// <summary>One past the end of the string literal at <paramref name="start"/>: regular, verbatim
        /// (<c>@"..."</c>), interpolated (<c>$"..."</c>, <c>$@"..."</c>) or raw (<c>"""..."""</c>, any number of
        /// quotes, with any number of <c>$</c>).</summary>
        private int ScanString(int start)
        {
            int i = start, dollars = 0;
            bool verbatim = false;
            while (s[i] == '$' || s[i] == '@') { if (s[i] == '$') dollars++; else verbatim = true; i++; }
            int quotes = 0;
            while (i + quotes < s.Length && s[i + quotes] == '"') quotes++;
            return !verbatim && quotes >= 3
                ? ScanRaw(start, i + quotes, quotes, dollars)
                : ScanQuoted(start, i + 1, verbatim, dollars > 0);
        }

        private int ScanQuoted(int start, int i, bool verbatim, bool interpolated)
        {
            int textFrom = start;   // the prefix and the opening quote are text
            while (i < s.Length)
            {
                char c = s[i];
                if (verbatim && c == '"' && i + 1 < s.Length && s[i + 1] == '"') { i += 2; continue; }
                if (!verbatim && c == '\\') { i += 2; continue; }
                if (c == '"') { BlankCode(textFrom, i + 1); return i + 1; }
                if (!verbatim && c == '\n') { BlankCode(textFrom, i); return i; }   // unterminated: stop at the line
                if (interpolated && c == '{')
                {
                    if (i + 1 < s.Length && s[i + 1] == '{') { i += 2; continue; }   // an escaped brace is text
                    BlankCode(textFrom, i + 1);
                    int close = ScanCode(i + 1, inHole: true);
                    textFrom = close;   // the closing brace is text, with whatever follows it
                    i = Math.Min(close + 1, s.Length);
                    continue;
                }
                i++;
            }
            BlankCode(textFrom, s.Length);
            return s.Length;
        }

        /// <summary>A raw literal: with n <c>$</c>, a run of at least n braces opens a hole with its last n, and n
        /// braces close it; fewer are text.</summary>
        private int ScanRaw(int start, int i, int quotes, int dollars)
        {
            string closing = new('"', quotes);
            int textFrom = start;
            while (i < s.Length)
            {
                if (s[i] == '"' && string.CompareOrdinal(s, i, closing, 0, quotes) == 0)
                {
                    BlankCode(textFrom, i + quotes);
                    return i + quotes;
                }
                if (dollars > 0 && s[i] == '{')
                {
                    int run = 0;
                    while (i + run < s.Length && s[i + run] == '{') run++;
                    if (run < dollars) { i += run; continue; }
                    BlankCode(textFrom, i + run);
                    int close = ScanCode(i + run, inHole: true);
                    textFrom = close;
                    i = Math.Min(close + dollars, s.Length);
                    continue;
                }
                i++;
            }
            BlankCode(textFrom, s.Length);
            return s.Length;
        }

        private int ScanChar(int start)
        {
            int i = start + 1;
            while (i < s.Length && s[i] != '\'' && s[i] != '\n') i += s[i] == '\\' ? 2 : 1;
            int end = Math.Min(i + 1, s.Length);
            BlankCode(start, end);
            return end;
        }
    }
}
