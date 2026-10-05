using System.Reflection;
using System.Text;
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
/// <para><b>What it reads.</b> Code with comments and string literals blanked out, so a seam named in a doc
/// comment does not count; one exception, SQLite's lock statement, lives inside a string and is read with
/// comments blanked only. A helper type under <c>Tests/Support/</c> that touches a seam must be one of the
/// registered wrappers, whose entry points are in the table; otherwise its callers would be invisible here.
/// <c>TestProcessState</c>'s module initializer sets the environment before any test runs, and is the one
/// exemption.</para>
///
/// <para><b>Not seams</b> (inventoried, deliberately left out): <c>TestProcessState.LoadContent</c>
/// re-publishes the real content and is safe anywhere; <c>SendCounters.Game</c> and
/// <c>Session.PositionWritesUnderWorldLock</c> are process-wide counters their facts read as bounds or deltas;
/// the instance hooks on a <c>World</c> (<c>SweepProbeForTest</c>, <c>PreSweepProbeForTest</c>) reach only
/// the world a test built.</para>
///
/// <para>Falsified three ways, each red in Debug and Release and green again once restored: deleting
/// <c>[Collection("log")]</c> from <c>SendMapLogTests</c> names its <c>ConsoleTap.AcquireAsync</c> (line 31)
/// and its reflected write to the wire switch (line 35); a new class with no collection that takes
/// <c>LogLineSink.Acquire</c> is named at that line; and dropping <c>DisableParallelization</c> from
/// <c>"log"</c> fails <see cref="TheCollectionsTheRuleTrustsRunAloneAndTheParallelOnesDoNot"/> and names
/// the classes in <c>"log"</c> that hold a seam, since <c>"log"</c> owns seams only by running alone.</para>
/// </summary>
public class TestSeamCollectionTests
{
    /// <summary>One process-global seam: what it is, why it is one, how to find it in code, and which
    /// collections own it (besides the ones that run alone, which own everything).</summary>
    internal sealed record Seam(string Name, string Why, Regex? Code, Regex? Text, string[] Owners,
                                Regex? ExemptCode = null);

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static readonly Seam[] Seams =
    {
        new("the log line sink",
            "Log.LineSinkForTest is one process-wide slot, and a capture collects every running test's lines",
            Rx(@"\bLogLineSink\s*\.\s*Acquire\b|\bLineSinkForTest\b"), null, new[] { "world" }),
        new("Console.Out",
            "Console.Out is one process-wide slot, and a capture collects every running test's output",
            Rx(@"\bConsoleTap\s*\.\s*Acquire(Async)?\b|\bConsole\s*\.\s*Set(Out|Error|In)\s*\("), null, new[] { "world" }),
        new("the log's state",
            "closing the log, changing what it admits or attaching its file changes logging for every running test",
            Rx(@"\bLog\s*\.\s*(Shutdown|RestartWriterForTest|AttachFile|Configure|AdmitOverrideForTest|" +
               @"ResetDroppedCountsForTest|DroppedCountsForTest)\b|\bLogShutdownWindow\s*\.\s*Enter\b"),
            null, Array.Empty<string>()),
        new("a static field set by reflection",
            "a static flipped by reflection (Log's wire switch, say) changes it for every running test",
            Rx(@"\.\s*SetValue\s*\(\s*null\s*,"), null, Array.Empty<string>()),
        new("a static test hook",
            "a static *ForTest hook on a production type fires for every session and world in the process",
            Rx(@"(?<![\w.])(?:[A-Z]\w*\s*\.\s*)+\w+ForTests?\s*=(?!=)|\bPhaseProbe\s*\.\s*CostingAtLeast\b"),
            null, new[] { "world" }),
        new("GmOverrides' world-wide statics",
            "GmOverrides' statics are read by every World in the process",
            Rx(@"\bGmOverrides\s*\.\s*\w+\s*=(?!=)"), null, new[] { "world" }),
        new("the staff roster",
            "StaffAccounts.Load replaces the roster for the whole process",
            Rx(@"\bStaffAccounts\s*\.\s*Load\s*\("), null, new[] { "world" }),
        new("the Lua gate",
            "Session.EnterScriptGate is one gate for every script in the process",
            Rx(@"\bEnterScriptGate\s*\("), null, new[] { "world" }),
        new("a stubbed content snapshot",
            "every test in the process reads the content snapshot a swapped table publishes",
            Rx(@"\b(ReplaceSpecForTests|OverridePathForTests)\s*\(|\bLoadStepForTests\b|" +
               @"\bEraCalendar\s*\.\s*PathOverrideForTests\b|\bCsv\s*\.\s*(WarningObserverForTests|OpenObserverForTests|Warn)\s*=(?!=)"),
            null, Array.Empty<string>()),
        new("process configuration",
            "the environment, ServerConfig and the channel-port pair are read by every test in the process",
            Rx(@"\bEnvironment\s*\.\s*SetEnvironmentVariable\s*\(|\bServerConfig\s*\.\s*ReloadForTests\s*\(|" +
               @"\bChannelPorts\s*\.\s*(ConfigureLoginPair|ConfigureGamePair|ResetForTests)\s*\("),
            null, Array.Empty<string>()),
        new("the character store's warning sink",
            "CharacterStore.Warn is one process-wide sink",
            Rx(@"\bCharacterStore\s*\.\s*Warn\s*=(?!=)"), null, new[] { "db" }),
        new("SQLite's write lock on the process database",
            "the write lock is per file and the process has one database file; take it on an IsolatedDatabase",
            null, Rx(@"\bBEGIN\s+(IMMEDIATE|EXCLUSIVE)\b"), Array.Empty<string>(),
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
        string root = Path.Combine(RepoPaths.Root(), "Tests");
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(root, f))
            .ToList();
        Assert.True(files.Count > 100, $"found only {files.Count} source files under {root}; is that the Tests folder?");

        var violations = Violations(files.Select(f => (Path.GetRelativePath(root, f).Replace('\\', '/'),
                                                       File.ReadAllText(f))),
                                    ExclusiveCollections());
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

    /// <summary>The scanner, pinned on sources whose answer is known, so a change to how it reads code
    /// cannot quietly turn the fact above into one that finds nothing. Each case is one class; the expected
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

    // ===== the scanner ===================================================================================

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

    /// <summary>Every violation in <paramref name="files"/> (relative path, source), one line each.</summary>
    internal static List<string> Violations(IEnumerable<(string Path, string Source)> files, IReadOnlyCollection<string> exclusive)
    {
        var violations = new List<string>();
        foreach (var (path, source) in files)
        {
            var (text, code) = Blank(source);
            bool support = path.StartsWith("Support/", StringComparison.Ordinal);
            foreach (var type in TopLevelTypes(source, text, code))
            {
                if (Exempt.Contains(type.Name)) continue;
                foreach (var seam in Seams)
                {
                    int at = Find(seam, type);
                    if (at < 0) continue;
                    int line = 1 + source.AsSpan(0, at).Count('\n');
                    string snippet = Snippet(source, at);
                    if (support)
                    {
                        if (!RegisteredWrappers.Contains(type.Name))
                            violations.Add($"{path}:{line} {type.Name} touches {seam.Name} (`{snippet}`) and is not a " +
                                           "registered wrapper: add its entry points to TestSeamCollectionTests.Seams " +
                                           "and its name to RegisteredWrappers, so the classes that call it are checked");
                        continue;
                    }
                    var allowed = seam.Owners.Concat(exclusive).ToArray();
                    if (type.Collection is { } c && allowed.Contains(c, StringComparer.Ordinal)) continue;
                    violations.Add($"{path}:{line} {type.Name} touches {seam.Name} (`{snippet}`) from " +
                                   (type.Collection is null ? "no collection" : $"collection \"{type.Collection}\"") +
                                   $"; {seam.Why}, so it belongs in " +
                                   string.Join(" or ", allowed.Select(a => $"[Collection(\"{a}\")]")));
                }
            }
        }
        return violations;
    }

    private static int Find(Seam seam, TypeSource type)
    {
        if (seam.ExemptCode is { } exempt && exempt.IsMatch(type.Code)) return -1;
        if (seam.Code is { } code && code.Match(type.Code) is { Success: true } m) return type.Start + m.Index;
        if (seam.Text is { } text && text.Match(type.Text) is { Success: true } t) return type.Start + t.Index;
        return -1;
    }

    private static string Snippet(string source, int at)
    {
        int end = source.IndexOf('\n', at);
        string s = source[at..(end < 0 ? source.Length : end)].Trim();
        return s.Length > 60 ? s[..60] + "..." : s;
    }

    /// <summary>A top-level type: its name, its <c>[Collection]</c> (null when it has none), and its text from
    /// the declaration keyword to the closing brace, nested types included, as it reads with comments blanked
    /// (<see cref="Text"/>) and with comments and literals blanked (<see cref="Code"/>).</summary>
    private sealed record TypeSource(string Name, string? Collection, int Start, string Text, string Code);

    private static readonly Regex TypeKeyword =
        new(@"\G(?:record\s+(?:class|struct)|class|record|struct|interface|enum)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.Compiled);

    private static readonly Regex CollectionAttribute =
        new(@"\[\s*(?:Xunit\s*\.\s*)?Collection\s*\(\s*""(?<name>[^""]*)""\s*\)\s*\]", RegexOptions.Compiled);

    private static readonly Regex NamespaceBlock =
        new(@"\Gnamespace\s+[A-Za-z_][\w.]*\s*\{", RegexOptions.Compiled);

    private static IEnumerable<TypeSource> TopLevelTypes(string source, string text, string code)
    {
        int depth = 0, regionStart = 0;
        var namespaceBraces = new Stack<int>();   // the depth each open namespace block's brace sits at
        for (int i = 0; i < code.Length; i++)
        {
            char ch = code[i];
            if (ch == '{') { depth++; continue; }
            if (ch == '}')
            {
                if (namespaceBraces.Count > 0 && namespaceBraces.Peek() == depth) { namespaceBraces.Pop(); regionStart = i + 1; }
                depth--;
                continue;
            }
            if (depth != namespaceBraces.Count) continue;   // inside a type body
            if (i > 0 && (char.IsLetterOrDigit(code[i - 1]) || code[i - 1] == '_')) continue;

            if (NamespaceBlock.Match(code, i) is { Success: true } ns)
            {
                depth++;
                namespaceBraces.Push(depth);
                i = ns.Index + ns.Length - 1;
                regionStart = i + 1;
                continue;
            }

            var m = TypeKeyword.Match(code, i);
            if (!m.Success) continue;
            int open = code.IndexOf('{', m.Index + m.Length);
            int semi = code.IndexOf(';', m.Index + m.Length);
            if (open < 0 || (semi >= 0 && semi < open)) { i = m.Index + m.Length - 1; regionStart = (semi < 0 ? i : semi) + 1; continue; }

            int close = open, d = 0;
            for (; close < code.Length; close++)
            {
                if (code[close] == '{') d++;
                else if (code[close] == '}' && --d == 0) break;
            }

            string header = text[regionStart..m.Index];
            var attribute = CollectionAttribute.Matches(header).LastOrDefault();
            yield return new TypeSource(m.Groups["name"].Value, attribute?.Groups["name"].Value, m.Index,
                                        text[m.Index..Math.Min(close + 1, text.Length)],
                                        code[m.Index..Math.Min(close + 1, code.Length)]);
            i = close;
            regionStart = close + 1;
        }
    }

    /// <summary>The source twice, offsets preserved: <c>text</c> with every comment blanked, and <c>code</c>
    /// with string and character literals blanked as well. Newlines survive both so line numbers do.</summary>
    internal static (string Text, string Code) Blank(string s)
    {
        var text = new StringBuilder(s);
        var code = new StringBuilder(s);
        void BlankBoth(int from, int to) { for (int k = from; k < to; k++) if (s[k] != '\n') { text[k] = ' '; code[k] = ' '; } }
        void BlankCode(int from, int to) { for (int k = from; k < to; k++) if (s[k] != '\n') code[k] = ' '; }

        int i = 0;
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
            }
            else if (c == '/' && next == '*')
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? s.Length : end + 2;
                BlankBoth(i, end);
                i = end;
            }
            else if (c == '"' || ((c == '@' || c == '$') && (next == '"' || next == '@' || next == '$')))
            {
                int end = StringLiteralEnd(s, i);
                BlankCode(i, end);
                i = end;
            }
            else if (c == '\'')
            {
                int end = i + 1;
                while (end < s.Length && s[end] != '\'' && s[end] != '\n') end += s[end] == '\\' ? 2 : 1;
                end = Math.Min(end + 1, s.Length);
                BlankCode(i, end);
                i = end;
            }
            else i++;
        }
        return (text.ToString(), code.ToString());
    }

    /// <summary>One past the end of the string literal starting at <paramref name="start"/>: regular, verbatim
    /// (<c>@"..."</c>, quotes doubled), interpolated (<c>$"..."</c>, holes treated as part of the literal) and
    /// raw (<c>"""..."""</c>, any number of quotes, with or without <c>$</c>).</summary>
    private static int StringLiteralEnd(string s, int start)
    {
        int i = start;
        bool verbatim = false;
        while (i < s.Length && (s[i] == '@' || s[i] == '$')) { verbatim |= s[i] == '@'; i++; }
        int quotes = 0;
        while (i + quotes < s.Length && s[i + quotes] == '"') quotes++;
        if (quotes >= 3)
        {
            int close = s.IndexOf(new string('"', quotes), i + quotes, StringComparison.Ordinal);
            return close < 0 ? s.Length : close + quotes;
        }
        i++;   // past the opening quote
        while (i < s.Length)
        {
            if (verbatim && s[i] == '"' && i + 1 < s.Length && s[i + 1] == '"') { i += 2; continue; }
            if (!verbatim && s[i] == '\\') { i += 2; continue; }
            if (s[i] == '"') return i + 1;
            if (!verbatim && s[i] == '\n') return i;   // unterminated: stop at the line
            i++;
        }
        return s.Length;
    }
}
