using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Tests;

/// <summary>
/// The exhaustive half of the thread-wiring guard #195 asked for. #37 section 5 pinned that
/// <c>TkListener.StartWorld</c> creates exactly two named threads and <c>World.cs</c> creates none — see
/// <see cref="OnlineRegistryAutoSaveTests.TheThreadWiringAndTheTwoFlushWordingsAreWhereTheyWere"/> — but that
/// pin only ever read two files by name, so it was blind to three things a refactor could do without it
/// noticing: (a) a thread start moved to a third file, such as a new partial <c>Program.cs</c>; (b) a
/// dedicated thread started without ever writing the literal text <c>new Thread(</c>, e.g.
/// <c>Task.Factory.StartNew(..., TaskCreationOptions.LongRunning)</c>, which gives the work a thread of its
/// own for its lifetime with nothing for a <c>new Thread(</c> count to see; and (c) a <c>//</c> comment that
/// merely mentions <c>new Thread(</c>, which a plain substring count cannot tell from the real thing.
///
/// <para>(c) is fixed by stripping <c>//</c> line comments, <c>/* */</c> block comments and <c>///</c> doc
/// comments before any pattern is matched — to a tokenizer a doc comment is just a line comment whose text
/// happens to start with a third slash, so one pass handles all three (<see cref="StripCsComments"/>). String
/// literals are deliberately left in place rather than blanked the same way, because unlike a comment they
/// cannot start a thread by existing — but <see cref="StripCsComments"/> also reports which source positions
/// sit inside one, so a pattern that lands inside a string is counted as that and not folded silently into
/// the total. Nothing under <c>Server/</c> does this today (checked by grepping every pinned pattern inside
/// quotes), but a line like <c>Log.Info("not literally starting new Thread(...) here")</c> would otherwise
/// inflate the count the day somebody wrote it, and <see cref="TheDedicatedThreadStartsUnderServerAreThisExactPinnedSet"/>
/// fails loudly if that ever happens rather than staying silent about it either way.</para>
///
/// <para>(a) and (b) are fixed by scanning every <c>.cs</c> file under <c>Server/</c> — not just
/// <c>World.cs</c> and <c>Net.cs</c> — for every form of dedicated-thread start: <c>new Thread(</c>,
/// <c>TaskCreationOptions.LongRunning</c>, and <c>ThreadPool.UnsafeRegisterWaitForSingleObject</c> (#195's own
/// example of a form worth grepping for). A fourth candidate, <c>ThreadPool.UnsafeQueueUserWorkItem</c> in
/// <c>Watchdog.cs</c>'s probe, was checked and deliberately left out: it queues onto the shared pool rather
/// than handing out a thread of its own, which is the exact distinction #195 is about. Each form is pinned as
/// file:count pairs, not a single total — a bare total of three would tolerate the count silently moving from
/// <c>Net.cs</c> to a new file, which is the blind spot #195 opened with.</para>
///
/// <para>On master at 1e95345 the only dedicated-thread-start text under <c>Server/</c> is three <c>new
/// Thread(</c> calls: two in <c>Net.cs</c> (the tick and autosave threads that <see
/// cref="OnlineRegistryAutoSaveTests.TheThreadWiringAndTheTwoFlushWordingsAreWhereTheyWere"/> already pins by
/// name) and one in <c>Watchdog.cs</c> (the pool-latency probe's own thread, started from
/// <c>Watchdog.Start</c>). Neither <c>TaskCreationOptions.LongRunning</c> nor
/// <c>ThreadPool.UnsafeRegisterWaitForSingleObject</c> appears anywhere under <c>Server/</c> today, so both
/// pinned sets are empty — which is itself the fence: the first use of either fails this test until the set
/// is updated deliberately, the same as a fourth <c>new Thread(</c> would.</para>
///
/// <para><b>Known gap.</b> The tokenizer below handles the C# this codebase actually writes — line comments,
/// block comments, regular/verbatim/interpolated strings (including nested interpolation holes, e.g.
/// <c>$"{(x ? "a" : "b")}"</c>, which <c>Content.cs</c> and <c>ArmorQuestAbility.cs</c> both do) and char
/// literals — but not C# 11 raw string literals (<c>"""…"""</c>), because none exist under <c>Server/</c>
/// today (checked by grepping for <c>"""</c>). If one is ever added, the scan throws rather than silently
/// mis-tokenizing the file around it, so this stays an honest gap, not a silent one.</para>
///
/// <para>Falsified by adding <c>// new Thread(</c> to <c>World.cs</c>: GREEN — comments are stripped before
/// matching, which is the one behavior this file exists to add over the substring count it replaces. Falsified
/// by adding a real <c>new Thread(() =&gt; {}).Start();</c> to <c>World.cs</c>'s constructor: red with
/// "new Thread(: World.cs: expected 0, found 1 — update this list deliberately". Falsified by adding
/// <c>Task.Factory.StartNew(() =&gt; {}, TaskCreationOptions.LongRunning);</c> to <c>Watchdog.Start</c>: red
/// with "TaskCreationOptions.LongRunning: Watchdog.cs: expected 0, found 1 — update this list deliberately",
/// and <c>Watchdog.cs</c>'s pinned <c>new Thread(</c> count of 1 stays green throughout — the two forms are
/// tracked, and violate, independently. Falsified by moving the <c>world-autosave</c> start out of
/// <c>Net.cs</c> into <c>World.cs</c>'s constructor (as a no-op stand-in, so the move does not also start a
/// real autosave loop in every other test that constructs a <c>World</c>): red twice in one run, "new
/// Thread(: Net.cs: expected 2, found 1 — update this list deliberately" and "new Thread(: World.cs: expected
/// 0, found 1 — update this list deliberately" — and, as a bystander, <see
/// cref="OnlineRegistryAutoSaveTests.TheThreadWiringAndTheTwoFlushWordingsAreWhereTheyWere"/> reds too, on its
/// own <c>world-autosave</c> name pattern, confirming that fact still checks identity correctly on its own.
/// Full console output for all four is in <c>briefs/reports/thread-start-tripwire-sonnet.md</c> (#195).</para>
/// </summary>
public class ServerThreadStartsTests
{
    // =====================================================================================================
    // The pinned set. File paths are relative to Server/, forward-slash. A file absent from a form's list
    // is pinned at zero for that form.
    // =====================================================================================================

    private static readonly (string File, int Count)[] PinnedNewThread =
    {
        ("Net.cs", 2),
        ("Watchdog.cs", 1),
    };

    private static readonly (string File, int Count)[] PinnedLongRunningTask = Array.Empty<(string, int)>();

    private static readonly (string File, int Count)[] PinnedUnsafeRegisterWait = Array.Empty<(string, int)>();

    private sealed record ThreadStartForm(string Label, Regex Pattern, (string File, int Count)[] Pinned);

    private static readonly ThreadStartForm[] Forms =
    {
        new("new Thread(",
            new Regex(Regex.Escape("new Thread("), RegexOptions.Compiled),
            PinnedNewThread),
        new("TaskCreationOptions.LongRunning",
            new Regex(Regex.Escape("TaskCreationOptions.LongRunning"), RegexOptions.Compiled),
            PinnedLongRunningTask),
        new("ThreadPool.UnsafeRegisterWaitForSingleObject",
            new Regex(Regex.Escape("ThreadPool.UnsafeRegisterWaitForSingleObject"), RegexOptions.Compiled),
            PinnedUnsafeRegisterWait),
    };

    [Fact]
    public void TheDedicatedThreadStartsUnderServerAreThisExactPinnedSet()
    {
        string serverDir = Path.Combine(RepoRoot().FullName, "Server");
        var actualByForm = Forms.ToDictionary(f => f.Label, _ => new Dictionary<string, int>());
        var stringHits = new List<string>();

        foreach (string file in Directory.EnumerateFiles(serverDir, "*.cs", SearchOption.AllDirectories).Order())
        {
            string relative = Path.GetRelativePath(serverDir, file).Replace('\\', '/');
            // bin/ and obj/ hold build output (including generated .cs, e.g. GlobalUsings.g.cs), not source.
            if (relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal))
                continue;

            string source = File.ReadAllText(file);
            if (source.Contains("\"\"\"", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"{relative} contains a raw string literal (\"\"\"); StripCsComments does not tokenize " +
                    "those and would mis-read the file around it. Extend it before trusting this scan again.");

            (string stripped, bool[] inString) = StripCsComments(source);

            foreach (ThreadStartForm form in Forms)
            {
                foreach (Match m in form.Pattern.Matches(stripped))
                {
                    if (inString[m.Index])
                    {
                        stringHits.Add($"{form.Label}: {relative}:{LineOf(source, m.Index)}: " +
                                        "inside a string literal, not counted as a thread start");
                        continue;
                    }
                    Dictionary<string, int> counts = actualByForm[form.Label];
                    counts[relative] = counts.GetValueOrDefault(relative) + 1;
                }
            }
        }

        var violations = new List<string>();
        foreach (ThreadStartForm form in Forms)
        {
            Dictionary<string, int> expected = form.Pinned.ToDictionary(p => p.File, p => p.Count);
            Dictionary<string, int> actual = actualByForm[form.Label];

            foreach (string file in expected.Keys.Union(actual.Keys).Order())
            {
                int exp = expected.GetValueOrDefault(file);
                int act = actual.GetValueOrDefault(file);
                if (exp != act)
                    violations.Add($"{form.Label}: {file}: expected {exp}, found {act} — " +
                                    "update this list deliberately");
            }
        }

        Assert.True(stringHits.Count == 0,
            "A dedicated-thread-start pattern text appears inside a string literal. Decide whether it is a " +
            "real thread start under another name or just a quoted mention, then either fix the source or " +
            "extend the scan — it is never silently folded into the pinned count either way:\n" +
            string.Join('\n', stringHits));

        Assert.True(violations.Count == 0,
            "Server/'s dedicated thread starts no longer match the pinned set in ServerThreadStartsTests — " +
            "a start moved file, a new one appeared, or one was removed. If that is the intended change, " +
            "update the pinned set deliberately:\n" + string.Join('\n', violations));
    }

    // =====================================================================================================
    // A small hand-written tokenizer. Not a full C# lexer — only enough of one to correctly skip past every
    // comment and string/char literal shape this codebase actually uses (see the class doc's "Known gap").
    // =====================================================================================================

    /// <summary>Returns the source with every <c>//</c>, <c>/* */</c> and <c>///</c> comment replaced by
    /// spaces (newlines kept, so line numbers in the result match the original 1:1), plus a same-length
    /// array flagging which source positions fall inside a string or char literal. String/char literal text
    /// itself is left untouched — only comments are blanked.</summary>
    internal static (string Stripped, bool[] InString) StripCsComments(string source)
    {
        var sb = new StringBuilder(source.Length);
        var inString = new bool[source.Length];
        ScanCode(source, 0, sb, inString, stopAt: null);
        return (sb.ToString(), inString);
    }

    /// <summary>Scans plain code from <paramref name="i"/>, dispatching into comments and string/char
    /// literals as it meets them, until either the source ends or (when <paramref name="stopAt"/> is set) it
    /// meets that character at brace depth 0 — used to find the end of an interpolation hole (<c>{...}</c>)
    /// without being fooled by a nested <c>{ }</c> (a lambda body, an array initializer) inside it. Returns
    /// the index of the stopping character (unconsumed) or the source length.</summary>
    private static int ScanCode(string s, int i, StringBuilder sb, bool[] inString, char? stopAt)
    {
        int n = s.Length;
        int depth = 0;
        while (i < n)
        {
            char c = s[i];
            char next = i + 1 < n ? s[i + 1] : '\0';

            if (stopAt.HasValue && c == stopAt.Value && depth == 0)
                return i;

            if (c == '{') { depth++; sb.Append(c); i++; continue; }
            if (c == '}') { depth--; sb.Append(c); i++; continue; }

            if (c == '/' && next == '/') { i = SkipLineComment(s, i, sb); continue; }
            if (c == '/' && next == '*') { i = SkipBlockComment(s, i, sb); continue; }

            int litStart = i;
            if (c == '@' && next == '$' && i + 2 < n && s[i + 2] == '"') { i = ScanInterpolatedString(s, litStart, i + 3, sb, inString, verbatim: true); continue; }
            if (c == '$' && next == '@' && i + 2 < n && s[i + 2] == '"') { i = ScanInterpolatedString(s, litStart, i + 3, sb, inString, verbatim: true); continue; }
            if (c == '@' && next == '"') { i = ScanVerbatimString(s, litStart, i + 2, sb, inString); continue; }
            if (c == '$' && next == '"') { i = ScanInterpolatedString(s, litStart, i + 2, sb, inString, verbatim: false); continue; }
            if (c == '"') { i = ScanRegularString(s, litStart, i + 1, sb, inString); continue; }
            if (c == '\'') { i = ScanCharLiteral(s, litStart, i + 1, sb, inString); continue; }

            sb.Append(c);
            i++;
        }
        return i;
    }

    private static int SkipLineComment(string s, int i, StringBuilder sb)
    {
        int n = s.Length;
        while (i < n && s[i] != '\n') { sb.Append(' '); i++; }
        return i; // leaves the '\n' itself for ScanCode's default branch to append
    }

    private static int SkipBlockComment(string s, int i, StringBuilder sb)
    {
        int n = s.Length;
        sb.Append(' ').Append(' ');
        i += 2;
        while (i < n && !(s[i] == '*' && i + 1 < n && s[i + 1] == '/'))
        {
            sb.Append(s[i] == '\n' ? '\n' : ' ');
            i++;
        }
        if (i < n) { sb.Append(' ').Append(' '); i += 2; }
        return i;
    }

    private static int ScanRegularString(string s, int start, int i, StringBuilder sb, bool[] inString)
    {
        sb.Append('"');
        int n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < n) { sb.Append(c).Append(s[i + 1]); i += 2; continue; }
            if (c == '"') { sb.Append('"'); i++; break; }
            if (c == '\n') break; // unterminated string; bail rather than eat the rest of the file
            sb.Append(c);
            i++;
        }
        MarkString(inString, start, i);
        return i;
    }

    private static int ScanVerbatimString(string s, int start, int i, StringBuilder sb, bool[] inString)
    {
        sb.Append('@').Append('"');
        int n = s.Length;
        while (i < n)
        {
            if (s[i] == '"')
            {
                if (i + 1 < n && s[i + 1] == '"') { sb.Append('"').Append('"'); i += 2; continue; }
                sb.Append('"'); i++; break;
            }
            sb.Append(s[i]);
            i++;
        }
        MarkString(inString, start, i);
        return i;
    }

    private static int ScanCharLiteral(string s, int start, int i, StringBuilder sb, bool[] inString)
    {
        sb.Append('\'');
        int n = s.Length;
        if (i < n && s[i] == '\\' && i + 1 < n) { sb.Append(s[i]).Append(s[i + 1]); i += 2; }
        else if (i < n) { sb.Append(s[i]); i++; }
        if (i < n && s[i] == '\'') { sb.Append('\''); i++; }
        MarkString(inString, start, i);
        return i;
    }

    /// <summary>Handles both <c>$"..."</c> and the verbatim forms (<c>@$"..."</c>/<c>$@"..."</c>). An
    /// interpolation hole (<c>{expr}</c>) is handed back to <see cref="ScanCode"/> so a nested string,
    /// comment-looking text inside a nested string, or nested braces inside the hole are all read correctly
    /// — the case this codebase actually has, e.g. <c>$"...{(cond ? "a" : "b")}..."</c>.</summary>
    private static int ScanInterpolatedString(string s, int start, int i, StringBuilder sb, bool[] inString, bool verbatim)
    {
        int n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (!verbatim && c == '\\' && i + 1 < n) { sb.Append(c).Append(s[i + 1]); i += 2; continue; }
            if (verbatim && c == '"' && i + 1 < n && s[i + 1] == '"') { sb.Append('"').Append('"'); i += 2; continue; }
            if (c == '"') { sb.Append('"'); i++; break; }
            if (c == '{' && i + 1 < n && s[i + 1] == '{') { sb.Append('{').Append('{'); i += 2; continue; }
            if (c == '}' && i + 1 < n && s[i + 1] == '}') { sb.Append('}').Append('}'); i += 2; continue; }
            if (c == '{')
            {
                sb.Append('{');
                i++;
                i = ScanCode(s, i, sb, inString, stopAt: '}');
                if (i < n && s[i] == '}') { sb.Append('}'); i++; }
                continue;
            }
            sb.Append(c);
            i++;
        }
        MarkString(inString, start, i);
        return i;
    }

    private static void MarkString(bool[] inString, int start, int end)
    {
        for (int k = start; k < end && k < inString.Length; k++)
            inString[k] = true;
    }

    private static int LineOf(string text, int index)
    {
        int line = 1;
        for (int k = 0; k < index && k < text.Length; k++)
            if (text[k] == '\n') line++;
        return line;
    }

    private static DirectoryInfo RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Project1998.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!;
    }
}
