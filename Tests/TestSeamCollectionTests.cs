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
/// <item>A use belongs to the member it sits in, and is judged by the code that can run that member, since a
/// helper runs in its caller's collection (the PR #326 review, F8). A test method, and a test class's constructor,
/// <c>Dispose</c>, <c>DisposeAsync</c> and <c>InitializeAsync</c>, are judged by their own class's collection: xunit
/// needs them public and calls them for that class's tests, and no derived class's tests run them, because no test
/// class may derive from another (the next item). Two other routes into them are not followed: another class calling
/// a test class's public static test method directly, and another class building a test class with <c>new</c> (the
/// PR #326 re-check, F8b). A test class's static constructor runs when the type is first touched, in whichever
/// class touches it first, so a seam use there is reported (F8a). So is one in an initializer of its static fields
/// or properties, which runs with it, whenever another class can touch the type first: when the class has a static
/// constructor, or a static member other classes can reach. With every static private, only its own code, run for
/// its own tests, touches it first. Any other member is called by whatever code its accessibility admits:
/// <list type="bullet">
/// <item>A private member, or any member of a private nested type, can be named only in the type that holds it.
/// It is judged by the innermost test class around it, and by every other test class in that type whose test
/// methods or lifecycle members name it, directly or through other private members, so a nested test class that
/// calls its outer class's private helper is judged by its own collection.</item>
/// <item>Any other member of a test class, or of a type nested in one, can be called by code in other classes, in
/// their collections, so it is reported, as code with no test class around it is; so is a private member that one
/// of them names. That includes <c>protected</c> and <c>private protected</c>: a derived class calls them from its
/// own collection, and need not inherit a test method to do it (a base whose tests are private, say), so the
/// cross-check below would not see it either.</item>
/// </list>
/// Names are matched as whole words in the holding type's code, which can only over-count callers, and naming a
/// nested type counts as naming its members, since an instance carries them wherever it is handed. Code with no
/// test class around it can be called from anywhere, so a type like that which touches a seam must be a
/// registered wrapper under <c>Tests/Support/</c>, whose entry points are in the table, or its callers would be
/// invisible here. <c>TestProcessState</c>'s module initializer sets the environment before any test runs, and
/// is the one exemption.</item>
/// <item>No test class derives from a test class, at any depth: from a type that declares a <c>[Fact]</c> or a
/// <c>[Theory]</c>, inherits one, or has a <c>[Collection]</c>. A derived class runs its base's constructor and
/// <c>Dispose</c>, its virtual test methods, and whatever it calls through <c>base.</c>, in its own collection, while
/// the guard judges that code by the base's. Rather than follow it there, the guard reports the derivation itself,
/// naming both classes (the PR #326 review, F8a). An abstract base whose tests run only in its derived classes is
/// one too. Base types are matched by simple name, through any alias, which can only over-count them; a base
/// that is not a test class is allowed.</item>
/// <item>An import of a seam's type reaches the seam as the type's own name does: after
/// <c>using static Shared.Log;</c> a bare <c>Shutdown()</c> counts, and after <c>using L = Shared.Log;</c>
/// <c>L.Shutdown()</c> does. A <c>global using</c> counts in every file. The bare form is matched by name alone,
/// so it counts where C# binds the name to something else: after <c>using static Shared.Log;</c> a class's own
/// <c>Configure()</c> counts as the log's, and after a <c>using static</c> of any type outside System, Microsoft and
/// Xunit a local <c>countForTest = 1</c> counts as a static test hook. Qualify the name, rename it, or drop the
/// import (the PR #326 re-check, F1a).</item>
/// <item>An <c>#if</c> region is read as each configuration compiles it. The rule reads every file twice, with
/// Debug's symbols and with Release's (<c>DEBUG</c> or <c>RELEASE</c>, with <c>TRACE</c>, as the SDK defines them),
/// and reports what either reading finds: CI builds and tests Release only (<c>.github/workflows/ci.yml</c>), and a
/// seam under <c>#if DEBUG</c> must not pass there unjudged (the PR #326 re-check, F7a). The cross-check below
/// reads this build's own symbols (<see cref="BuildSymbols"/>), because it compares the scanner with what xunit ran
/// in this build: a fact under <c>#if DEBUG</c> is a fact in a Debug run and nothing at all in a Release one. A
/// symbol the scanner does not know stops the scan with its name and line, rather than being guessed.</item>
/// </list>
/// What it does not follow: the code a base test class runs for a derived one, which is why that derivation is
/// reported instead. Behind that rule stands
/// <see cref="TheScannerSeesEveryTestClassXunitSeesInTheCollectionXunitGivesIt"/>: the scanner judges each class
/// as written, by the test methods it declares and its own <c>[Collection]</c>, while xunit runs every test method
/// a class declares or inherits, in its own or a base type's collection, and the cross-check fails the day the two
/// differ, however the base list names the base. Nor does it follow code that a nested helper type runs when it
/// is built rather than called (a field initializer, a static constructor): that is judged by the accessibility
/// its own declaration writes, as any member is.</para>
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
/// <see cref="TheScannerFindsSeamsInCodeAndOnlyInCode"/>, <see cref="APartialClassIsOneClassAcrossItsFiles"/> and
/// <see cref="AHelperIsJudgedByEveryClassThatCanCallIt"/>.</para>
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
    /// class, the seam, the collection the class is in and the collections it belongs in; or, for a test class
    /// that derives from another, both classes. It reads every file as Debug compiles it and as Release does, and
    /// reports what either reading finds, so a run in one configuration judges the other's code too.</summary>
    [Fact]
    public void EveryClassThatTouchesAProcessGlobalSeamSitsInACollectionThatOwnsIt()
    {
        var files = TestSources();
        Assert.True(files.Count > 100, $"found only {files.Count} source files under Tests/; is that the Tests folder?");

        var exclusive = ExclusiveCollections();
        var violations = Violations(files, exclusive, SymbolsFor(debug: true))
            .Union(Violations(files, exclusive, SymbolsFor(debug: false)), StringComparer.Ordinal).ToList();
        Assert.True(violations.Count == 0,
            "classes touching a process-global seam from a collection that does not own it, or deriving from a test " +
            "class:\n" + string.Join("\n", violations));
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

    /// <summary>The scanner's judgement of every test class, held against what xunit runs from the compiled
    /// assembly: the same classes, each with the same number of test methods, in the same collection.
    ///
    /// <para>The scanner judges a class as written: the <c>[Fact]</c> and <c>[Theory]</c> methods it declares (the
    /// parts of a partial class together, <c>#if</c> regions as this build compiled them) and its own
    /// <c>[Collection]</c>. xunit runs every exported class that is not abstract, with every test method it
    /// declares or inherits, in its own <c>[Collection]</c> or a base type's (<see cref="XunitTestClasses"/>).
    /// Where the two differ, the rule above judges some test method by the wrong collection or not at all, and
    /// this names the class. A class that inherits its test methods, which the PR #326 review planted (F6),
    /// shows up on xunit's side with methods the scanner does not give it, so it cannot pass unseen.</para></summary>
    [Fact]
    public void TheScannerSeesEveryTestClassXunitSeesInTheCollectionXunitGivesIt()
    {
        var scanned = TestClasses(TestSources(), BuildSymbols);
        var run = XunitTestClasses();
        Assert.True(run.Count > 100, $"only {run.Count} test classes in the assembly");

        var disagreements = new List<string>();
        foreach (string name in scanned.Keys.Union(run.Keys).Order(StringComparer.Ordinal))
        {
            string read = scanned.TryGetValue(name, out var s) ? Describe(s.Collection, s.Tests) : "no test class";
            string runs = run.TryGetValue(name, out var r) ? Describe(r.Collection, r.Tests) : "nothing";
            if (read != runs) disagreements.Add($"{name}: the scanner reads {read}; xunit runs {runs}");
        }
        Assert.True(disagreements.Count == 0,
            "the scanner and xunit disagree about these test classes. The scanner reads each class as written, by " +
            "the test methods it declares and its own [Collection]; xunit runs a class's inherited test methods too, " +
            "in a base type's [Collection] if it has none, and never runs an abstract class itself. Declare the tests " +
            "and the [Collection] on the class that runs them:\n" + string.Join("\n", disagreements));
    }

    private static string Describe(string? collection, int tests) =>
        $"{tests} test method{(tests == 1 ? "" : "s")} in " + (collection is null ? "no collection" : $"\"{collection}\"");

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
    // A private helper type nested in a test class runs as that class.
    [InlineData("""[Collection("world")] public class A { [Fact] public void F() => H.T(); private static class H { public static void T() { using var s = LogLineSink.Acquire(); } } }""", "")]
    // The [Collection] of a partial class can sit on any part (PR #326 review, F2).
    [InlineData("""[Collection("log")] public partial class P { [Fact] public void A() { } } public partial class P { [Fact] public void B() { Log.Shutdown(); } }""", "")]
    // An import of a type that carries no seam changes nothing.
    [InlineData("""using static System.Math; public class A { [Fact] public void F() { var loadForTest = Max(1, 2); } }""", "")]
    // A helper runs in its caller's collection (PR #326 review, F8). One that code in other classes can call is
    // reported, whatever the collection of the class it sits in: an internal or public member, a member of a nested
    // type that is not private, a protected or private protected member (a derived class's), a field whose
    // initializer touches the seam, a private member that one of those names, and a private nested type's member
    // reached through an instance one of those hands out...
    [InlineData("""[Collection("world")] public class A { [Fact] public void F() => T(); internal static IDisposable T() => LogLineSink.Acquire(); }""", "the log line sink")]
    [InlineData("""[Collection("world")] public class A { [Fact] public void F() => N.T(); internal static class N { public static IDisposable T() => LogLineSink.Acquire(); } }""", "the log line sink")]
    [InlineData("""[Collection("world")] public class A { [Fact] public void F() => T(); protected static void T() { StaffAccounts.Load(); } }""", "the staff roster")]
    [InlineData("""[Collection("world")] public class A { [Fact] public void F() => T(); private protected static void T() { StaffAccounts.Load(); } }""", "the staff roster")]
    [InlineData("""[Collection("world")] public class A { [Fact] public void F() { } internal static readonly Func<IDisposable> T = () => LogLineSink.Acquire(); }""", "the log line sink")]
    [InlineData("""[Collection("world")] public class A { [Fact] public void F() => T(); public static IDisposable T() => U(); private static IDisposable U() => LogLineSink.Acquire(); }""", "the log line sink")]
    [InlineData("""[Collection("world")] public class A { [Fact] public void F() { } internal static IDisposable Make() => new H(); private sealed class H : IDisposable { public void Dispose() { Session.TradeOpenProbeForTest = null; } } }""", "a static test hook")]
    // ...and a private helper is judged by every test class that can name it, a nested one included.
    [InlineData("""[Collection("world")] public class A { private static IDisposable T() => LogLineSink.Acquire(); [Fact] public void F() => T(); public class Inner { [Fact] public void G() => T(); } }""", "the log line sink")]
    // What only the class's own tests reach is judged by its collection: a private helper beside a non-private
    // member that does not name it, the constructor and lifecycle members xunit calls around each test (an explicit
    // interface implementation included), and a private nested type the tests build.
    [InlineData("""[Collection("world")] public class A { [Fact] public void F() => T(); private static IDisposable T() => LogLineSink.Acquire(); internal static int Count() => 1; }""", "")]
    [InlineData("""[Collection("world")] public class A : IDisposable { public A() { StaffAccounts.Load(); } public void Dispose() { Session.TradeOpenProbeForTest = null; } [Fact] public void F() { } }""", "")]
    [InlineData("""[Collection("world")] public class A : IAsyncLifetime { public Task InitializeAsync() { StaffAccounts.Load(); return Task.CompletedTask; } Task IAsyncLifetime.DisposeAsync() { Session.TradeOpenProbeForTest = null; return Task.CompletedTask; } [Fact] public void F() { } }""", "")]
    [InlineData("""[Collection("world")] public class A { [Fact] public void F() { using var h = new H(); } private sealed class H : IDisposable { public H() { StaffAccounts.Load(); } public void Dispose() { Session.TradeOpenProbeForTest = null; } } }""", "")]
    // A test class's static constructor runs in whichever class first touches the type (PR #326 review, F8a), and
    // its static initializers run with it, once another class can touch the type first: through a static
    // constructor, or a static member it can reach (xunit itself reads a public TheoryData member while discovering).
    [InlineData("""[Collection("world")] public class W { static W() { StaffAccounts.Load(); } [Fact] public void F() { } internal static int Count() => 1; }""", "the staff roster")]
    [InlineData("""[Collection("world")] public class W { static void Hold() => StaffAccounts.Load(); static W() => Hold(); [Fact] public void F() { } }""", "the staff roster")]
    [InlineData("""[Collection("world")] public class W { private static readonly IDisposable Held = LogLineSink.Acquire(); [Fact] public void F() { } public static TheoryData<int> Rows => new() { 1 }; }""", "the log line sink")]
    [InlineData("""[Collection("world")] public class W { private static IDisposable Held { get; } = LogLineSink.Acquire(); static W() { } [Fact] public void F() { } }""", "the log line sink")]
    // With every static private, only the class's own tests touch the type first, as an instance field's
    // initializer runs only in the constructor.
    [InlineData("""[Collection("world")] public class W { private static readonly IDisposable Held = LogLineSink.Acquire(); [Fact] public void F() => Held.Dispose(); }""", "")]
    [InlineData("""[Collection("world")] public class W { private readonly IDisposable _held = LogLineSink.Acquire(); [Fact] public void F() => _held.Dispose(); }""", "")]
    [InlineData("""[Collection("world")] public class W { private static IDisposable Held => LogLineSink.Acquire(); [Fact] public void F() => Held.Dispose(); }""", "")]
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

    /// <summary>No test class derives from a test class (the PR #326 review, F8a): a derived class runs its base's
    /// constructor and <c>Dispose</c>, its virtual test methods and whatever it calls through <c>base</c>, in its own
    /// collection, where the guard does not follow that code. The first three cases are the review's round-5
    /// routes, each green before this rule; a base that is not a test class stays allowed.
    /// <paramref name="expected"/> is the report's "derived derives from base", or empty for none.</summary>
    [Theory]
    [InlineData("""[Collection("world")] public class B { [Fact] public virtual void F() { using var s = LogLineSink.Acquire(); } } [Collection("db")] public class D : B { [Fact] public override void F() => base.F(); }""", "D derives from B")]
    [InlineData("""[Collection("world")] public class B { public B() { StaffAccounts.Load(); } [Fact] private void F() { } } [Collection("db")] public class D : B { [Fact] public void G() { } }""", "D derives from B")]
    [InlineData("""[Collection("world")] public class B : IDisposable { public void Dispose() { Session.TradeOpenProbeForTest = null; } [Fact] private void F() { } } [Collection("db")] public class D : B { [Fact] public void G() { } }""", "D derives from B")]
    // An abstract base whose tests run only in its derived classes, two levels down; a base that has only a
    // [Collection]; a base named through an alias; a generic base.
    [InlineData("""public abstract class B { [Fact] public void F() { } } public abstract class M : B { } public class D : M, IDisposable { public void Dispose() { } }""", "D derives from B")]
    [InlineData("""public abstract class B { [Fact] public void F() { } } public abstract class M : B { } public class D : M { }""", "M derives from B")]
    [InlineData("""[Collection("world")] public abstract class B { } public class D : B { [Fact] public void F() { } }""", "D derives from B")]
    [InlineData("""using Base = Tests.B; public class B { [Fact] public void F() { } } [Collection("db")] public class D : Base { [Fact] public void G() { } }""", "D derives from B")]
    [InlineData("""public abstract class B<T> where T : new() { [Fact] public void F() { } } public sealed class D : B<object> { }""", "D derives from B")]
    // A base that is not a test class, interfaces, and a generic constraint naming a test class, are all allowed.
    [InlineData("""public abstract class Base { protected static int Two() => 2; } [Collection("world")] public class D : Base, IDisposable { [Fact] public void F() { } public void Dispose() { } }""", "")]
    [InlineData("""[Collection("world")] public class B { [Fact] public void F() { } } public class Box<T> where T : B { } [Collection("world")] public class D : IClassFixture<SessionFixture> { [Fact] public void G() { } }""", "")]
    public void ATestClassMayNotDeriveFromATestClass(string source, string expected)
    {
        var found = Violations(new[] { ("Sample.cs", "namespace Tests;\n" + source) }, new[] { "log", "tile-translation" });
        if (expected.Length == 0)
            Assert.DoesNotContain(found, v => v.Contains(" derives from ", StringComparison.Ordinal));
        else
            Assert.Contains(found, v => v.Contains($" {expected}, a test class. ", StringComparison.Ordinal));
    }

    /// <summary>A helper runs in the collection of whoever calls it (the PR #326 review, F8: a class with no
    /// collection called a <c>"world"</c> class's internal capture helper, and ran it beside <c>"world"</c>). Each
    /// helper that code in other classes can call is named with the member that lets them in and why, and a nested
    /// test class that calls its outer class's private helper is named with the helper it goes through.</summary>
    [Fact]
    public void AHelperIsJudgedByEveryClassThatCanCallIt()
    {
        const string open = """
            namespace Tests;
            [Collection("world")]
            public class Owner
            {
                [Fact] public void F() { using var tap = Tap(); }
                internal static IDisposable Tap() => LogLineSink.Acquire();
                internal static class Nested { internal static IDisposable Tap() => LogLineSink.Acquire(); }
                private static IDisposable Hidden() => LogLineSink.Acquire();
                public static IDisposable Door() => Hidden();
            }
            public class Caller { [Fact] public void G() { using var tap = Owner.Tap(); } }
            """;
        var found = Violations(new[] { ("Owner.cs", open) }, new[] { "log", "tile-translation" });
        Assert.Equal(3, found.Count);
        Assert.StartsWith("Owner.cs:6 Owner.Tap touches the log line sink (`LogLineSink.Acquire();`); Owner.Tap is internal, ", found[0]);
        Assert.StartsWith("Owner.cs:7 Owner+Nested.Tap touches the log line sink (`LogLineSink.Acquire(); }`); Owner+Nested.Tap is internal, ", found[1]);
        Assert.StartsWith("Owner.cs:8 Owner.Hidden touches the log line sink (`LogLineSink.Acquire();`), and Owner.Door names it; Owner.Door is public, ", found[2]);

        const string nested = """
            namespace Tests;
            [Collection("world")]
            public class Outer
            {
                private static IDisposable Tap() => LogLineSink.Acquire();
                [Fact] public void F() { using var tap = Tap(); }
                public class Inner { [Fact] public void G() { using var tap = Tap(); } }
            }
            """;
        var inner = Assert.Single(Violations(new[] { ("Outer.cs", nested) }, new[] { "log", "tile-translation" }));
        Assert.StartsWith("Outer.cs:5 Outer+Inner touches the log line sink (`LogLineSink.Acquire();`) through Outer.Tap from no collection; ", inner);
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

    /// <summary>An <c>#if</c> region is read the way a build with those symbols compiles it, so a seam or a test
    /// method in a branch that build leaves out is not there (the PR #326 review, F7: a class whose only fact was
    /// under <c>#if DEBUG</c> was a test class to the scanner in a Release run, and not to the assembly).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnIfRegionIsReadTheWayTheBuildCompiledIt(bool debug)
    {
        var symbols = SymbolsFor(debug);
        const string seamInDebug =
            "namespace Tests;\npublic class A\n{\n#if DEBUG\n    [Fact] public void F() { Log.Shutdown(); }\n#else\n" +
            "    [Fact] public void G() { }\n#endif\n}\n";
        var found = Violations(new[] { ("A.cs", seamInDebug) }, new[] { "log", "tile-translation" }, symbols);
        Assert.Equal(debug, found.Any(v => v.Contains("touches the log's state ", StringComparison.Ordinal)));
        var foundCrLf = Violations(new[] { ("A.cs", seamInDebug.Replace("\n", "\r\n")) }, new[] { "log", "tile-translation" }, symbols);
        Assert.Equal(found.Count, foundCrLf.Count);

        const string factOnlyInDebug = "namespace Tests;\npublic class D\n{\n#if DEBUG\n    [Fact] public void F() { }\n#endif\n}\n";
        var classes = TestClasses(new[] { ("D.cs", factOnlyInDebug) }, symbols);
        Assert.Equal(debug ? 1 : 0, classes.TryGetValue("Tests.D", out var d) ? d.Tests : 0);
    }

    /// <summary>The expression forms C# allows, <c>#elif</c>, and a <c>#define</c> in the file, each with DEBUG
    /// and TRACE defined and RELEASE not: <paramref name="compiled"/> is whether the fact under the condition,
    /// which touches the log's state, is compiled.</summary>
    [Theory]
    [InlineData("#if !DEBUG", false)]
    [InlineData("#if DEBUG && TRACE", true)]
    [InlineData("#if DEBUG && !TRACE", false)]
    [InlineData("#if (RELEASE || TRACE) && DEBUG == true", true)]
    [InlineData("#if RELEASE != DEBUG", true)]
    [InlineData("#if RELEASE\n#elif DEBUG", true)]
    [InlineData("#if DEBUG\n#elif DEBUG\n#else", false)]
    [InlineData("#if FEATURE // defined at the top of the file", true)]
    public void IfExpressionsAreEvaluatedAsCSharpEvaluatesThem(string condition, bool compiled)
    {
        string source = "#define FEATURE\nnamespace Tests;\npublic class A\n{\n    [Fact] public void G() { }\n" + condition +
                        "\n    [Fact] public void F() { Log.Shutdown(); }\n#endif\n}\n";
        var found = Violations(new[] { ("A.cs", source) }, new[] { "log", "tile-translation" }, SymbolsFor(debug: true));
        Assert.Equal(compiled, found.Count > 0);
    }

    /// <summary>A symbol the scanner was not given stops the scan with the file, the line and the symbol rather
    /// than being guessed. One inside a region the build leaves out is never evaluated, as the compiler never
    /// evaluates it either.</summary>
    [Fact]
    public void AnUnknownSymbolStopsTheScanWithItsFileLineAndName()
    {
        var symbols = SymbolsFor(debug: true);
        const string unknown = "namespace Tests;\npublic class A\n{\n#if NET8_0_OR_GREATER\n    [Fact] public void F() { }\n#endif\n}\n";
        var stopped = Assert.Throws<InvalidOperationException>(
            () => Violations(new[] { ("A.cs", unknown) }, new[] { "log" }, symbols));
        Assert.Contains("A.cs:4 ", stopped.Message);
        Assert.Contains("'NET8_0_OR_GREATER'", stopped.Message);

        const string leftOut =
            "namespace Tests;\npublic class A\n{\n#if !DEBUG\n#if NET8_0_OR_GREATER\n#endif\n#endif\n    [Fact] public void F() { }\n}\n";
        Assert.Empty(Violations(new[] { ("A.cs", leftOut) }, new[] { "log" }, symbols));
    }

    /// <summary>A line that starts with <c>#</c> inside a string or a comment is text, not a directive: read as
    /// one, its unknown symbol would stop the scan.</summary>
    [Fact]
    public void AHashLineInsideAStringOrACommentIsNotADirective()
    {
        const string source = "namespace Tests;\npublic class A\n{\n    const string S = \"\"\"\n#if NOT_A_DIRECTIVE\n\"\"\";\n" +
                              "    /*\n#if NOT_A_DIRECTIVE_EITHER\n    */\n    [Fact] public void F() { Log.Shutdown(); }\n}\n";
        var found = Violations(new[] { ("A.cs", source) }, new[] { "tile-translation" }, SymbolsFor(debug: true));
        Assert.Contains(found, v => v.Contains("touches the log's state ", StringComparison.Ordinal));
    }

    private static Dictionary<string, bool> SymbolsFor(bool debug) =>
        new(StringComparer.Ordinal) { ["DEBUG"] = debug, ["RELEASE"] = !debug, ["TRACE"] = true };

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

    /// <summary>
    /// The test classes xunit runs from this assembly, by full name, each with the number of test methods it
    /// runs and the collection it runs them in.
    ///
    /// <para>Measured rather than assumed, on 2026-10-05: a probe class of each shape below, listed with
    /// <c>dotnet test --list-tests</c>. xunit runs only exported types (a public class nested in an internal one
    /// was not listed; an internal test class does not compile here, xunit's analyzer rule xUnit1000). It never
    /// runs an abstract class itself, but runs a static class, which is abstract and sealed in IL. A class runs
    /// every test method it declares or inherits: an abstract base's, a concrete base's (which the base also runs
    /// itself), and private ones. The methods counted are the ones <c>GetRuntimeMethods</c> returns, which is
    /// how xunit lists them.</para>
    /// </summary>
    private static SortedDictionary<string, (string? Collection, int Tests)> XunitTestClasses()
    {
        var classes = new SortedDictionary<string, (string? Collection, int Tests)>(StringComparer.Ordinal);
        foreach (var type in typeof(TestSeamCollectionTests).Assembly.GetTypes())
        {
            if (!type.IsVisible || (type.IsAbstract && !type.IsSealed)) continue;
            int tests = type.GetRuntimeMethods().Count(m => m.IsDefined(typeof(FactAttribute), inherit: true));
            if (tests > 0) classes[type.FullName!] = (XunitCollectionOf(type), tests);
        }
        return classes;
    }

    /// <summary>
    /// The conditional-compilation symbols this test assembly was compiled with, as far as the scanner reads
    /// them. Each value is set by the compiler itself, through the <c>#if</c> around it, so the scanner reads an
    /// <c>#if</c> region exactly as this build did. A symbol not listed here stops the scan with its name and
    /// line: guessing it would let the scanner read code this build never compiled, or miss code it did (the PR
    /// #326 review, F7).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, bool> BuildSymbols = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
#if DEBUG
        ["DEBUG"] = true,
#else
        ["DEBUG"] = false,
#endif
#if RELEASE
        ["RELEASE"] = true,
#else
        ["RELEASE"] = false,
#endif
#if TRACE
        ["TRACE"] = true,
#else
        ["TRACE"] = false,
#endif
    };

    /// <summary>Every test class the scanner finds, by full name (<c>Namespace.Outer+Inner</c>, as reflection
    /// writes it), with the collection it reads for it and the number of test methods it declares.</summary>
    private static SortedDictionary<string, (string? Collection, int Tests)> TestClasses(
        IEnumerable<(string Path, string Source)> files, IReadOnlyDictionary<string, bool> symbols)
    {
        var scan = new Scan(files, symbols);
        var classes = new SortedDictionary<string, (string? Collection, int Tests)>(StringComparer.Ordinal);
        foreach (var type in scan.Files.SelectMany(f => f.Types).Where(scan.IsTestClass))
            classes[type.FullName] = (scan.CollectionOf(type), scan.TestsOf(type));
        return classes;
    }

    /// <summary>Every violation in <paramref name="files"/> (relative path, source), one line each, with
    /// <c>#if</c> regions read for <paramref name="symbols"/> (this build's, by default).</summary>
    internal static List<string> Violations(IEnumerable<(string Path, string Source)> files, IReadOnlyCollection<string> exclusive,
                                            IReadOnlyDictionary<string, bool>? symbols = null)
    {
        var scan = new Scan(files, symbols ?? BuildSymbols);
        var violations = new List<string>();
        foreach (var file in scan.Files)
        {
            bool support = file.Path.StartsWith("Support/", StringComparison.Ordinal);
            var imports = file.Imports.Where(i => !i.Global).Concat(scan.GlobalImports).ToList();
            foreach (var seam in Seams)
            {
                var allowed = seam.Owners.Concat(exclusive).ToArray();
                var reported = new HashSet<object>();   // each class, and each member other classes can call, once per seam
                foreach (var (at, via) in Hits(seam, file, imports).OrderBy(h => h.At))
                {
                    var innermost = file.Innermost(at);
                    var outermost = innermost;
                    while (outermost?.Parent is not null) outermost = outermost.Parent;
                    if (outermost is not null && Exempt.Contains(outermost.Name)) continue;

                    var owner = scan.TestClassAround(innermost);   // the innermost test class around the use
                    var subject = owner ?? outermost;
                    if (subject is not null && seam.ExemptCode is { } exempt
                        && exempt.IsMatch(file.Code.AsSpan(subject.Start, subject.Close + 1 - subject.Start)))
                        continue;

                    int line = 1 + file.Source.AsSpan(0, at).Count('\n');
                    string use = $"touches {seam.Name} (`{Snippet(file.Source, at)}`{via})";
                    if (owner is null)
                    {
                        if (support && outermost is not null && RegisteredWrappers.Contains(outermost.Name)) continue;
                        if (outermost is not null && !reported.Add(outermost)) continue;
                        violations.Add($"{file.Path}:{line} {outermost?.DisplayName ?? "(outside any type)"} {use} and is " +
                                       "neither a test class nor a registered wrapper: no [Fact] or [Theory] is in it or " +
                                       "around it, so the classes that call it are invisible here. Move the seam into the " +
                                       "test class, or make it a Tests/Support wrapper: add its entry points to " +
                                       "TestSeamCollectionTests.Seams and its name to RegisteredWrappers");
                        continue;
                    }

                    // Which code can run the member the use sits in: other classes, through a member they can call,
                    // or only the test classes whose tests reach it, each in its own collection.
                    var member = innermost!.MemberAt(at);
                    string where = member?.DisplayName ?? innermost.DisplayName;
                    var reach = scan.ReachOf(innermost, member);
                    if (reach.Door is { } door)
                    {
                        if (!reported.Add(door)) continue;
                        violations.Add($"{file.Path}:{line} {where} {use}" +
                                       (door == member ? "" : $", and {door.DisplayName} names it") +
                                       $"; {door.DisplayName} {reach.Why}, out of this guard's sight. " +
                                       (door.IsTypeInitializer
                                           ? "Move the seam into the tests that need it"
                                           : $"Make it private, or a type around it, so that only {owner.DisplayName}'s own tests run it") +
                                       "; or make the seam a Tests/Support wrapper: add its entry points to " +
                                       "TestSeamCollectionTests.Seams and its name to RegisteredWrappers");
                        continue;
                    }

                    foreach (var runner in reach.Classes.Prepend(owner).Distinct())
                    {
                        string? collection = scan.CollectionOf(runner);
                        if (collection is not null && allowed.Contains(collection, StringComparer.Ordinal)) continue;
                        if (!reported.Add(runner)) continue;
                        violations.Add($"{file.Path}:{line} {runner.DisplayName} {use}" +
                                       (runner == owner ? "" : $" through {where}") + " from " +
                                       (collection is null ? "no collection" : $"collection \"{collection}\"") +
                                       $"; {seam.Why}, so it belongs in " +
                                       string.Join(" or ", allowed.Select(a => $"[Collection(\"{a}\")]")));
                    }
                }
            }
        }

        // No test class derives from a test class: the code a base runs for a derived class is not followed (F8a).
        var derivations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in scan.Files)
            foreach (var type in file.Types.Where(t => t.BaseNames.Count > 0))
            {
                if (scan.TestBaseOf(type) is not { } testBase || !derivations.Add(type.FullName)) continue;
                int line = 1 + file.Source.AsSpan(0, type.Start).Count('\n');
                violations.Add($"{file.Path}:{line} {type.DisplayName} derives from {testBase.DisplayName}, a test class. " +
                               $"{type.DisplayName}'s tests run the base's constructor and Dispose, its virtual test methods " +
                               $"and whatever they call through base, all in {type.DisplayName}'s own collection, while this " +
                               "guard judges that code by the base's collection and does not follow it there. So no test class " +
                               "may derive from another (the PR #326 review, F8a). Move what they share into a type with no " +
                               "[Fact], [Theory] or [Collection], and derive from that or call it");
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
        private readonly ILookup<string, TypeDecl> _byName;

        public Scan(IEnumerable<(string Path, string Source)> files, IReadOnlyDictionary<string, bool> symbols)
        {
            Files = files.Select(f => new SourceFile(f.Path, f.Source, symbols)).ToList();
            GlobalImports = Files.SelectMany(f => f.Imports).Where(i => i.Global).ToList();
            _parts = Files.SelectMany(f => f.Types).GroupBy(t => t.FullName, StringComparer.Ordinal)
                          .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            _byName = Files.SelectMany(f => f.Types).ToLookup(t => t.Name, StringComparer.Ordinal);
        }

        public List<SourceFile> Files { get; }
        public List<Import> GlobalImports { get; }

        /// <summary>A type is a test class if any of its parts declares a test method.</summary>
        public bool IsTestClass(TypeDecl type) => TestsOf(type) > 0;

        /// <summary>The test methods the class declares, across all its parts.</summary>
        public int TestsOf(TypeDecl type) => _parts[type.FullName].Sum(p => p.Tests);

        /// <summary>The <c>[Collection]</c> on any part of the class; C# allows it on one part only.</summary>
        public string? CollectionOf(TypeDecl type) =>
            _parts[type.FullName].Select(p => p.OwnCollection).FirstOrDefault(c => c is not null);

        /// <summary>The innermost test class around <paramref name="type"/>, itself included, or null.</summary>
        public TypeDecl? TestClassAround(TypeDecl? type)
        {
            while (type is not null && !IsTestClass(type)) type = type.Parent;
            return type;
        }

        /// <summary>
        /// Which code can run <paramref name="member"/> of <paramref name="type"/> (null: the type's own
        /// declaration). It follows every member that names it, inside the one type whose code can, and on through
        /// whatever names those, until each path ends at a test method or lifecycle member of a test class (xunit
        /// runs those as that class, so the path ends in <see cref="Reach.Classes"/>), or at code that other classes
        /// run (<see cref="Reach.Door"/>, which ends the search): a member they can call, or a test class's type
        /// initializer, which runs in whichever class first touches the type.
        /// </summary>
        public Reach ReachOf(TypeDecl type, MemberDecl? member)
        {
            var classes = new List<TypeDecl>();
            var seen = new HashSet<object> { (object?)member ?? type };
            var queue = new Queue<(TypeDecl Type, MemberDecl? Member)>();
            queue.Enqueue((type, member));
            while (queue.TryDequeue(out var next))
            {
                var (t, m) = next;
                bool initializer = m is { IsTypeInitializer: true } && IsTestClass(t) && OthersCanInitialize(t);
                if (initializer && m!.IsConstructor) return TypeInitializer(t, m, classes);
                if (m is null || (IsTestClass(t) && (m.IsTest || IsLifecycle(t, m))))
                {
                    if (TestClassAround(t) is { } runner && !classes.Contains(runner)) classes.Add(runner);
                    continue;
                }
                var (holder, names, why) = Confinement(t, m);
                if (holder is null) return new Reach(m, why, classes);
                if (initializer) return TypeInitializer(t, m, classes);
                foreach (var caller in Callers(holder, names, m))
                    if (seen.Add((object?)caller.Member ?? caller.Type)) queue.Enqueue(caller);
            }
            return new Reach(null, null, classes);
        }

        /// <summary>A test class's static constructor, or a static field or property initializer: the runtime runs it
        /// when the type is first touched, so it runs in whichever class touches the type first (the PR #326 review,
        /// F8a). A non-private one is reported for its accessibility first, since that names the plainer fix.</summary>
        private static Reach TypeInitializer(TypeDecl testClass, MemberDecl member, List<TypeDecl> classes) =>
            new(member, (member.IsConstructor ? "runs" : "is initialized") + $" when {testClass.DisplayName} is first " +
                        "touched, in whichever class touches it first, and in that class's collection", classes);

        /// <summary>Whether code in another class can be the first to touch <paramref name="testClass"/>, and so run its
        /// type initializer there. It can when the class has a static constructor, which also runs when any class
        /// builds it, or a static member other classes can reach, its own or a nested type's. With neither, every static
        /// it has is private, so only its own code, run for its own tests, touches the type first.</summary>
        private bool OthersCanInitialize(TypeDecl testClass)
        {
            string nested = testClass.FullName + "+";
            foreach (var type in Files.SelectMany(f => f.Types)
                                      .Where(t => t.FullName == testClass.FullName || t.FullName.StartsWith(nested, StringComparison.Ordinal)))
                foreach (var member in type.Members.Where(m => m.IsStatic))
                {
                    if (member.IsConstructor && type.FullName == testClass.FullName) return true;
                    if (Confinement(type, member).Holder is null) return true;
                }
            return false;
        }

        /// <summary>A test class's constructor, <c>Dispose</c>, <c>DisposeAsync</c> or <c>InitializeAsync</c>: xunit
        /// needs them public, and calls them around that class's own tests. No other class's tests run them, since
        /// no test class may derive from another (<see cref="TestBaseOf"/>).</summary>
        private static bool IsLifecycle(TypeDecl testClass, MemberDecl member) =>
            member.Name == testClass.Name || member.Name is "Dispose" or "DisposeAsync" or "InitializeAsync";

        /// <summary>The one type whose code can name <paramref name="member"/>, with the names that code would use
        /// for it: its own, and those of the nested types between it and that type, since whoever holds an
        /// instance of one can call its members. No type when code in other classes can call it, with what makes
        /// that so.</summary>
        private (TypeDecl? Holder, List<string> Names, string Why) Confinement(TypeDecl type, MemberDecl member)
        {
            var names = new List<string> { member.Name };
            if (member.IsPrivate) return (type, names, "");
            for (var t = type; t.Parent is not null; t = t.Parent)
            {
                names.Add(t.Name);
                if (IsPrivate(t)) return (t.Parent, names, "");
            }
            string access = member.ExplicitImplementation ? "an explicit interface implementation" : member.Modifier ?? "public";
            return (null, names, $"is {access}, so code in other classes can call it, and it runs in their collections");
        }

        /// <summary>The nearest type <paramref name="type"/> derives from, at any depth, that is a test class to
        /// this rule: one that declares a test method or has a <c>[Collection]</c>. Null when there is none, or when
        /// <paramref name="type"/> is no test class itself: it declares no test method, inherits none and has no
        /// <c>[Collection]</c>.</summary>
        public TypeDecl? TestBaseOf(TypeDecl type)
        {
            var ancestors = new List<TypeDecl>();
            var seen = new HashSet<string>(StringComparer.Ordinal) { type.FullName };
            var queue = new Queue<TypeDecl>(BasesOf(type));
            while (queue.TryDequeue(out var next))
            {
                if (!seen.Add(next.FullName)) continue;
                ancestors.Add(next);
                foreach (var further in BasesOf(next)) queue.Enqueue(further);
            }
            var testBase = ancestors.FirstOrDefault(a => IsTestClass(a) || CollectionOf(a) is not null);
            bool testClass = IsTestClass(type) || CollectionOf(type) is not null || ancestors.Any(IsTestClass);
            return testClass ? testBase : null;
        }

        /// <summary>The scanned types that <paramref name="type"/>'s base lists, on any of its parts, can name: each
        /// name through any alias its file or a <c>global using</c> gives it, matched by simple name, which can only
        /// over-count them.</summary>
        private IEnumerable<TypeDecl> BasesOf(TypeDecl type)
        {
            foreach (var part in _parts[type.FullName])
                foreach (string name in part.BaseNames)
                {
                    string target = part.File.Imports.Where(i => !i.Global).Concat(GlobalImports)
                                        .FirstOrDefault(i => i.Alias == name)?.TypeName ?? name;
                    foreach (var candidate in _byName[target])
                        if (candidate.FullName != type.FullName) yield return candidate;
                }
        }

        /// <summary>A nested type that is private: declared so on one of its parts, or with no accessibility at
        /// all, which is a nested type's default outside an interface.</summary>
        private bool IsPrivate(TypeDecl type) =>
            type.Parent is not null
            && (_parts[type.FullName].Select(p => p.Modifier).FirstOrDefault(m => m is not null)
                ?? (type.Parent.IsInterface ? "public" : "private")) == "private";

        /// <summary>Every member in the code of <paramref name="holder"/> (each of its parts, nested types
        /// included) that names any of <paramref name="names"/>, other than <paramref name="self"/>, with its type;
        /// a name outside any member comes with no member. A name counts wherever it stands as a whole word, which
        /// can only over-count callers.</summary>
        private IEnumerable<(TypeDecl Type, MemberDecl? Member)> Callers(TypeDecl holder, List<string> names, MemberDecl self)
        {
            var word = new Regex($@"(?<![\w@])(?:{string.Join("|", names.Distinct().Select(Regex.Escape))})(?!\w)",
                                 RegexOptions.CultureInvariant);
            foreach (var part in _parts[holder.FullName])
                for (var m = word.Match(part.File.Code, part.Start, part.Close + 1 - part.Start); m.Success; m = m.NextMatch())
                {
                    if (part.File == self.Type.File && self.Start <= m.Index && m.Index <= self.Close) continue;
                    var type = part.File.Innermost(m.Index)!;
                    yield return (type, type.MemberAt(m.Index));
                }
        }
    }

    /// <summary>Who can run a piece of code in a test class: the test classes whose test methods and lifecycle
    /// members reach it (<see cref="Classes"/>), or, when <see cref="Door"/> is set, code in any class, through that
    /// member, for the reason <see cref="Why"/>.</summary>
    private sealed record Reach(MemberDecl? Door, string? Why, List<TypeDecl> Classes);

    /// <summary>One source file: its source, its text with comments blanked, its code with comments and literal
    /// text blanked (see <see cref="Blank"/>), the types it declares and the imports it makes.</summary>
    private sealed class SourceFile
    {
        public SourceFile(string path, string source, IReadOnlyDictionary<string, bool> symbols)
        {
            Path = path;
            Source = source;
            try
            {
                (Text, Code) = Blank(source, symbols);
            }
            catch (UnknownSymbolException e)
            {
                throw new InvalidOperationException($"{path}:{e.Line} uses the conditional-compilation symbol " +
                    $"'{e.Symbol}', which the seam guard's scanner does not know, so it cannot tell which code this " +
                    "build compiled there. Add it to TestSeamCollectionTests.BuildSymbols with the #if that sets it.", e);
            }
            Types = ParseTypes(Text, Code);
            foreach (var type in Types) type.File = this;
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
    /// partial class's parts), the name a report shows, the type it is nested in, its own <c>[Collection]</c>, the
    /// accessibility it writes, the simple names its base list gives, how many test methods it declares, its members,
    /// and its span from the declaration keyword to the closing brace.</summary>
    private sealed class TypeDecl
    {
        public required string Name { get; init; }
        public required string FullName { get; init; }
        public required string DisplayName { get; init; }
        public TypeDecl? Parent { get; init; }
        public string? OwnCollection { get; init; }
        public string? Modifier { get; init; }
        public bool IsInterface { get; init; }
        public List<string> BaseNames { get; init; } = new();
        public required int Start { get; init; }
        public int Close { get; set; }
        public int Tests { get; set; }
        public List<MemberDecl> Members { get; } = new();
        public SourceFile File { get; set; } = null!;

        /// <summary>The member of this type whose declaration spans <paramref name="at"/>, or null.</summary>
        public MemberDecl? MemberAt(int at) => Members.FirstOrDefault(m => m.Start <= at && at <= m.Close);
    }

    /// <summary>A member of a type (a method, constructor, property, field, event or indexer), from its first
    /// attribute or modifier to the <c>;</c> or closing brace that ends it: the name it declares (a constructor's is
    /// its type's), the accessibility it writes, whether it is static and has an initializer, whether it implements
    /// an interface member explicitly (<c>void IDisposable.Dispose()</c>), and whether it is a test method.</summary>
    private sealed class MemberDecl
    {
        public required TypeDecl Type { get; init; }
        public required int Start { get; init; }
        public required int Close { get; init; }
        public required string Name { get; init; }
        public string? Modifier { get; init; }
        public bool IsStatic { get; init; }
        public bool HasInitializer { get; init; }
        public bool ExplicitImplementation { get; init; }
        public bool IsTest { get; set; }

        public bool IsConstructor => Name == Type.Name;

        /// <summary>Code the runtime runs when the type is first touched, whoever touches it: a static constructor,
        /// or the initializer of a static field or property.</summary>
        public bool IsTypeInitializer => IsStatic && (IsConstructor || HasInitializer);

        public string DisplayName => IsConstructor ? $"{Type.DisplayName}'s {(IsStatic ? "static " : "")}constructor"
                                                   : $"{Type.DisplayName}.{Name}";

        /// <summary>Declared private, or with no accessibility outside an interface, which is a member's default
        /// there. An explicit interface implementation is called through the interface, by whoever holds one.</summary>
        public bool IsPrivate => !ExplicitImplementation && (Modifier ?? (Type.IsInterface ? "public" : "private")) == "private";
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

    /// <summary>Every type declared in a file, nested ones included, with its members, read off the code view: a
    /// brace that does not open a namespace or a type body opens code, and no type or member is looked for inside
    /// code. A member runs from where the previous one ended to the <c>;</c> that ends it, or to the brace that
    /// closes its body when nothing of it follows (an initializer after a property's accessors does).</summary>
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
                scopes.TryPop(out var closed);
                if (closed?.Kind == ScopeKind.Type) closed.Type!.Close = i;
                var under = scopes.Count == 0 ? null : scopes.Peek();
                if (under?.Kind == ScopeKind.Code) continue;
                if (closed?.Kind == ScopeKind.Code && under?.Kind == ScopeKind.Type)
                {
                    if (MemberGoesOn(code, i + 1)) continue;   // `{ get; } = 1;`, a lambda's `};`, `new() { ... };`
                    AddMember(code, regionStart, i, under.Type!);
                }
                regionStart = i + 1;
                continue;
            }
            if (top is { Kind: ScopeKind.Code }) continue;
            if (ch == ';')
            {
                if (top is { Kind: ScopeKind.Type }) AddMember(code, regionStart, i, top.Type!);
                regionStart = i + 1;
                continue;
            }
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
                Modifier = Accessibility(DeclarationWords(code, regionStart, m.Index)),
                IsInterface = m.Value.StartsWith("interface", StringComparison.Ordinal),
                BaseNames = BaseNames(code, m.Index + m.Length, open),
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
            if (types.Where(t => t.Start <= at && at <= t.Close).MaxBy(t => t.Start) is not { } owner) continue;
            owner.Tests++;
            if (owner.MemberAt(at) is { } method) method.IsTest = true;
        }
        return types;
    }

    /// <summary>Whether the member whose body closed just before <paramref name="i"/> goes on: it ends unless what
    /// follows can only start the next member (a name or modifier, an attribute, a tuple type, a finalizer) or close
    /// the type.</summary>
    private static bool MemberGoesOn(string code, int i)
    {
        while (i < code.Length && char.IsWhiteSpace(code[i])) i++;
        return i < code.Length && !(char.IsLetter(code[i]) || code[i] is '_' or '@' or '[' or '(' or '~' or '}');
    }

    private static void AddMember(string code, int start, int close, TypeDecl type)
    {
        var words = DeclarationWords(code, start, close + 1);
        if (words.Count == 0) return;
        var (name, at) = words[^1];
        int before = at - 1;
        while (before >= start && char.IsWhiteSpace(code[before])) before--;
        type.Members.Add(new MemberDecl
        {
            Type = type,
            Start = start,
            Close = close,
            Name = name,
            Modifier = Accessibility(words.Take(words.Count - 1)),
            IsStatic = words.Take(words.Count - 1).Any(w => w.Word == "static"),
            HasInitializer = HasInitializer(code, start, close),
            ExplicitImplementation = before >= start && code[before] == '.',
        });
    }

    /// <summary>Whether a member has an initializer: an <c>=</c> outside its brackets, parameters and bodies (a
    /// field's, or a property's after its accessors), as opposed to an expression body's <c>=&gt;</c>.</summary>
    private static bool HasInitializer(string code, int start, int close)
    {
        int depth = 0;
        for (int i = start; i <= close; i++)
        {
            char c = code[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == '=' && depth == 0) return i + 1 > close || code[i + 1] != '>';
        }
        return false;
    }

    /// <summary>The simple names a type's base list gives (<c>class D : Outer.B&lt;int&gt;, IDisposable</c> gives
    /// <c>B</c> and <c>IDisposable</c>), read from <paramref name="from"/>, just past the type's name, to its body at
    /// <paramref name="to"/>: past any type parameters and primary-constructor parameters, and up to any
    /// <c>where</c> clause.</summary>
    private static List<string> BaseNames(string code, int from, int to)
    {
        var names = new List<string>();
        int depth = 0, colon = -1;
        for (int i = from; i < to; i++)
        {
            char c = code[i];
            if (c is '<' or '(' or '[') depth++;
            else if (c is '>' or ')' or ']') depth = Math.Max(0, depth - 1);
            else if (depth > 0) continue;
            else if (c == ':' && colon < 0) colon = i;
            else if (c == 'w' && string.CompareOrdinal(code, i, "where", 0, 5) == 0 && !char.IsLetterOrDigit(code[i - 1])
                     && i + 5 < to && !char.IsLetterOrDigit(code[i + 5]) && code[i + 5] != '_')
            {
                to = i;
                break;
            }
        }
        if (colon < 0 || colon >= to) return names;
        int entry = colon + 1;
        depth = 0;
        for (int i = entry; i <= to; i++)
        {
            char c = i < to ? code[i] : ',';
            if (c is '<' or '(' or '[') depth++;
            else if (c is '>' or ')' or ']') depth = Math.Max(0, depth - 1);
            else if (c == ',' && depth == 0)
            {
                if (DeclarationWords(code, entry, i) is { Count: > 0 } words) names.Add(words[^1].Word);
                entry = i + 1;
            }
        }
        return names;
    }

    private static readonly HashSet<string> AccessWords = new(StringComparer.Ordinal) { "public", "private", "protected", "internal", "file" };

    private static readonly HashSet<string> ModifierWords = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal", "file", "static", "readonly", "const", "volatile", "virtual",
        "override", "abstract", "sealed", "extern", "unsafe", "new", "async", "partial", "required", "ref", "event",
        "fixed", "implicit", "explicit",
    };

    /// <summary>The words of a declaration from <paramref name="from"/>, each with where it starts, outside
    /// attribute sections, type argument lists and tuple types, up to its parameter list, body or initializer (or
    /// <paramref name="to"/>). The last is the name it declares; the accessibility is among the others.</summary>
    private static List<(string Word, int At)> DeclarationWords(string code, int from, int to)
    {
        var words = new List<(string Word, int At)>();
        int depth = 0;
        for (int i = from; i < to; i++)
        {
            char c = code[i];
            if (depth == 0 && c is '{' or ';' or '=') break;
            if (depth == 0 && c == '(' && words.Count > 0 && !ModifierWords.Contains(words[^1].Word)) break;   // the parameters
            if (c is '[' or '<' or '(') depth++;
            else if (c is ']' or '>' or ')') depth = Math.Max(0, depth - 1);
            else if (depth == 0 && (char.IsLetter(c) || c == '_'))
            {
                int end = i + 1;
                while (end < to && (char.IsLetterOrDigit(code[end]) || code[end] == '_')) end++;
                words.Add((code[i..end], i));
                i = end - 1;
            }
        }
        return words;
    }

    /// <summary>The accessibility among <paramref name="words"/> (<c>protected internal</c>, say), or null.</summary>
    private static string? Accessibility(IEnumerable<(string Word, int At)> words)
    {
        var access = words.Select(w => w.Word).Where(AccessWords.Contains).ToList();
        if (access.Contains("private") && access.Contains("protected")) return "private protected";
        if (access.Contains("protected") && access.Contains("internal")) return "protected internal";
        return access.FirstOrDefault();
    }

    /// <summary>The source twice, offsets kept: <c>Text</c> with every comment blanked, and <c>Code</c> with the
    /// text of every string and character literal blanked as well. An interpolation hole is code, so it stays in
    /// <c>Code</c>, with any literal inside it blanked in turn; its braces and any format clause are text.
    /// Preprocessor lines are blanked in both, and so is every line of an <c>#if</c> region that
    /// <paramref name="symbols"/> leave out, as the compiler leaves it out. Newlines survive both, so line numbers
    /// do.</summary>
    internal static (string Text, string Code) Blank(string source, IReadOnlyDictionary<string, bool> symbols)
    {
        var lexer = new Lexer(source, symbols);
        lexer.ScanCode(0, inHole: false);
        return (new string(lexer.Text), new string(lexer.Code));
    }

    /// <summary>An <c>#if</c> names a symbol the scanner was not given a value for.</summary>
    private sealed class UnknownSymbolException(string symbol, int line) : Exception($"line {line}: unknown symbol {symbol}")
    {
        public string Symbol { get; } = symbol;
        public int Line { get; } = line;
    }

    private sealed class Lexer(string s, IReadOnlyDictionary<string, bool> symbols)
    {
        public char[] Text { get; } = s.ToCharArray();
        public char[] Code { get; } = s.ToCharArray();

        /// <summary>The symbols in force: this build's, then any <c>#define</c> or <c>#undef</c> in the file.</summary>
        private readonly Dictionary<string, bool> _defined = new(symbols, StringComparer.Ordinal);

        /// <summary>One entry per open <c>#if</c>: whether the code around it is compiled, and whether a branch of
        /// it has been taken yet.</summary>
        private readonly Stack<(bool Outer, bool Taken)> _conditions = new();

        /// <summary>Whether the code at the current line is compiled.</summary>
        private bool _active = true;

        private static readonly Regex DirectiveLine = new(@"^\s*#\s*(?<keyword>[a-z]+)\b(?<rest>[^\n]*)", RegexOptions.Compiled);

        private static readonly Regex ExpressionToken = new(@"\|\||&&|==|!=|!|\(|\)|[A-Za-z_][A-Za-z0-9_]*|\S", RegexOptions.Compiled);

        /// <summary>At the start of a line of code: a preprocessor directive is read and blanked, and a line in an
        /// <c>#if</c> region this build leaves out is blanked. Returns the index to go on from, or -1 when the line
        /// is ordinary compiled code.</summary>
        private int AtLineStart(int i)
        {
            int end = s.IndexOf('\n', i);
            end = end < 0 ? s.Length : end;
            var directive = DirectiveLine.Match(s, i, end - i);
            if (directive.Success)
            {
                Directive(directive.Groups["keyword"].Value, directive.Groups["rest"].Value, i);
                BlankBoth(i, end);
                return end;
            }
            if (_active) return -1;
            BlankBoth(i, end);
            return end;
        }

        private void Directive(string keyword, string rest, int at)
        {
            int comment = rest.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) rest = rest[..comment];
            rest = rest.Trim();
            switch (keyword)
            {
                case "if":
                {
                    bool value = _active && Evaluate(rest, at);
                    // Inside a left-out region every branch counts as taken, so no #elif or #else there compiles.
                    _conditions.Push((_active, value || !_active));
                    _active = value;
                    break;
                }
                case "elif" when _conditions.TryPop(out var open):
                {
                    bool value = open.Outer && !open.Taken && Evaluate(rest, at);
                    _conditions.Push((open.Outer, open.Taken || value));
                    _active = value;
                    break;
                }
                case "else" when _conditions.TryPop(out var open):
                    _conditions.Push((open.Outer, true));
                    _active = open.Outer && !open.Taken;
                    break;
                case "endif" when _conditions.TryPop(out var open):
                    _active = open.Outer;
                    break;
                case "define" when _active:
                    _defined[rest] = true;
                    break;
                case "undef" when _active:
                    _defined[rest] = false;
                    break;
            }
        }

        /// <summary>An <c>#if</c> or <c>#elif</c> expression: symbols, <c>true</c>, <c>false</c>, <c>!</c>,
        /// <c>==</c>, <c>!=</c>, <c>&amp;&amp;</c>, <c>||</c> and parentheses, with C#'s precedence. Every symbol is
        /// looked up, so an unknown one is reported wherever it sits in the expression.</summary>
        private bool Evaluate(string expression, int at)
        {
            var tokens = ExpressionToken.Matches(expression).Select(t => t.Value).ToList();
            int p = 0;
            string? Peek() => p < tokens.Count ? tokens[p] : null;

            bool Or()
            {
                bool value = And();
                while (Peek() == "||") { p++; value = And() | value; }
                return value;
            }

            bool And()
            {
                bool value = Equality();
                while (Peek() == "&&") { p++; value = Equality() & value; }
                return value;
            }

            bool Equality()
            {
                bool value = Unary();
                while (Peek() is "==" or "!=")
                {
                    bool equal = tokens[p++] == "==";
                    bool right = Unary();
                    value = equal ? value == right : value != right;
                }
                return value;
            }

            bool Unary()
            {
                string? token = tokens.ElementAtOrDefault(p++);
                switch (token)
                {
                    case "!":
                        return !Unary();
                    case "(":
                    {
                        bool value = Or();
                        p++;   // the closing parenthesis
                        return value;
                    }
                    case "true":
                        return true;
                    case "false":
                        return false;
                    case not null when char.IsLetter(token[0]) || token[0] == '_':
                        return _defined.TryGetValue(token, out bool defined)
                            ? defined
                            : throw new UnknownSymbolException(token, 1 + s.AsSpan(0, at).Count('\n'));
                    default:
                        throw new UnknownSymbolException(token ?? "(nothing)", 1 + s.AsSpan(0, at).Count('\n'));
                }
            }

            return Or();
        }

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
                if (!inHole && (i == 0 || s[i - 1] == '\n') && AtLineStart(i) is var after and >= 0)
                {
                    i = after < s.Length && s[after] == '\n' ? after + 1 : after;   // past the newline, so an empty line moves on
                    continue;
                }
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
