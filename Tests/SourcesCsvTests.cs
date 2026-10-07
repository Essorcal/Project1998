using Shared;
using Xunit;

namespace Tests;

/// <summary>
/// <c>game-data/Sources.csv</c>, the provenance registry that content rows cite, read as a strict RFC 4180 reader
/// reads it.
///
/// <para>Its one reader is <c>re/build_confidence.py</c> (<c>csv.DictReader</c>); the server never loads it
/// (<c>game-data/README.md</c>). Python's default dialect is lenient: row 21 once escaped its quotes with a
/// backslash (<c>\"</c>), every other row doubles them (<c>""</c>), and that one row came back as 12 columns
/// against the header's 10, with no error. Python's strict mode raised on it. The content code's <c>Csv</c> is no
/// check either: it read that row as 10 columns and skips a blank line, so a fact built on it was green with both
/// faults in the file. Hence the few lines of state machine below rather than any reader the repository
/// has.</para>
/// </summary>
public sealed class SourcesCsvTests
{
    [Fact]
    public void SourcesCsvReadsAsStrictRfc4180()
    {
        string path = Path.Combine(RepoPaths.Root(), "game-data", "Sources.csv");
        var problems = StrictProblems(File.ReadAllText(path));
        Assert.True(problems.Count == 0, "game-data/Sources.csv is not strict RFC 4180:\n" + string.Join("\n", problems));
    }

    /// <summary>Each fault a strict reader would raise, by line: inside a quoted field a quote must be followed by
    /// a quote (an escaped one), a comma or the end of the line; a quote may not sit inside an unquoted field; no
    /// line is blank; every record has the header's column count. No field in this file spans lines, so a quoted
    /// field still open at the end of its line is a fault too.</summary>
    private static List<string> StrictProblems(string text)
    {
        var problems = new List<string>();
        string[] lines = (text.EndsWith('\n') ? text[..^1] : text).Split('\n');
        int columns = -1;
        for (int n = 1; n <= lines.Length; n++)
        {
            string line = lines[n - 1].TrimEnd('\r');
            if (line.Length == 0) { problems.Add($"line {n} is blank"); continue; }

            int fields = 1;
            bool quoted = false, atFieldStart = true;
            string? fault = null;
            for (int i = 0; i < line.Length && fault is null; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c != '"') continue;
                    if (i + 1 == line.Length || line[i + 1] == ',') quoted = false;
                    else if (line[i + 1] == '"') i++;
                    else fault = $"line {n}, character {i + 1}: a quote inside a quoted field is followed by " +
                                 $"'{line[i + 1]}', not by a quote, a comma or the end of the line";
                    continue;
                }
                if (c == ',') { fields++; atFieldStart = true; continue; }
                if (c == '"' && atFieldStart) quoted = true;
                else if (c == '"') fault = $"line {n}, character {i + 1}: a quote inside an unquoted field";
                atFieldStart = false;
            }
            if (fault is null && quoted) fault = $"line {n}: a quoted field is still open at the end of the line";
            if (fault is null && columns < 0) columns = fields;
            else if (fault is null && fields != columns)
                fault = $"line {n} has {fields} columns; the header has {columns}";
            if (fault is not null) problems.Add(fault);
        }
        return problems;
    }
}
