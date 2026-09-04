using System.Collections;

namespace Shared;

/// <summary>How a table's file fared. Anything but <see cref="Ok"/> means the registry built from it is
/// empty or short, and something said so out loud.</summary>
public enum CsvStatus
{
    /// <summary>A header and at least one data row.</summary>
    Ok,
    /// <summary>No path resolved, or the file is not there.</summary>
    Missing,
    /// <summary>The file is there and could not be read (permissions, a lock, a bad share).</summary>
    Unreadable,
    /// <summary>Read fine and carries no data rows — a header alone, or nothing but comments.</summary>
    Empty,
}

/// <summary>
/// The CSV reader for every file-backed table in this repo. It exists because the reader it replaces was
/// SILENT: a missing file and a parse failure both ended in a bare <c>yield break</c>, and every column read
/// went through <c>GetValueOrDefault("Vita", "0")</c>, so renaming a header zeroed a whole column with
/// nothing in the log. A server that starts, listens and accepts logins into a world with no mobs is the
/// characteristic failure of this codebase, not a crash.
///
/// <para>Three things are loud here and were not before: a file that is missing or unreadable, a column a
/// loader <see cref="CsvRow.Require">requires</see> that the header does not have, and the per-table
/// (read, kept, skipped) census that <c>Content.LoadReport</c> is built from.</para>
///
/// <para><b>Value semantics are deliberately identical to the dictionary this replaces.</b>
/// <c>Require(col, fallback)</c> returns <c>fallback</c> in exactly the two cases
/// <c>GetValueOrDefault(col, fallback)</c> did: the header has no such column, and the row is short and has
/// no cell at that index. The old reader only ever filled <c>min(header, values)</c> slots, so a ragged row
/// fell back then and must fall back now. Only the first of those two warns — a ragged row is a row
/// problem, not a schema problem, and the loaders already skip those.</para>
/// </summary>
public static class Csv
{
    /// <summary>Where a missing file, an unreadable file and a renamed column are reported.
    /// <para>Shared cannot see <c>Server.Log</c> (Server references Shared, not the other way round), so the
    /// game server assigns this to <c>Log.Warn</c> as it loads content. The default writes to the console in
    /// the same shape as Shared's other direct writers (CharacterStore, Moderation) so a consumer that
    /// forgets to wire it up is noisy rather than silent — which is the entire point of this file.</para></summary>
    public static Action<string> Warn { get; set; } =
        m => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [csv] !! {m}");

    /// <summary>Read a CSV table. Never throws: a missing or unreadable file comes back as an empty table
    /// carrying the <see cref="CsvStatus"/> that says which, having already reported itself.</summary>
    /// <param name="name">What this table is called in the log and the load report — the bare file name
    /// (<c>mobs.csv</c>), not the resolved path, so the message reads the same on every host.</param>
    public static CsvTable Open(string name, string? path) => CsvTable.Open(name, path, null);

    /// <summary>Read a headerless CSV table whose column names are supplied by its consumer. This is the
    /// same reader and reporting path as <see cref="Open(string, string?)"/>; only the source of the header
    /// differs, and every non-comment line is therefore a data row.</summary>
    public static CsvTable Open(string name, string? path, params string[] header) =>
        CsvTable.Open(name, path, header);

    // '#' opens a comment line, anywhere including above the header — these tables are hand-maintained and
    // the ones carrying a derivation (ArmorDyeRamps.csv) are unusable without somewhere to write it down.
    internal static bool IsSkippable(string s) => string.IsNullOrWhiteSpace(s) || s.TrimStart().StartsWith('#');

    /// <summary>Split one CSV line, honouring quoted fields with embedded commas and doubled quotes.</summary>
    public static List<string> Split(string line)
    {
        var outp = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '"') { if (q && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; } else q = !q; }
            else if (ch == ',' && !q) { outp.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(ch);
        }
        outp.Add(cur.ToString());
        return outp;
    }
}

/// <summary>One CSV file, read whole. Iterate it for rows; read <see cref="Read"/>/<see cref="Kept"/>/
/// <see cref="Skipped"/> and <see cref="MissingColumns"/> afterwards for the load report.</summary>
public sealed class CsvTable : IEnumerable<CsvRow>
{
    private readonly List<CsvRow> _rows;
    // Column name -> index. Case-insensitive; a duplicate header name resolves to the LAST column of that
    // name. That is ALMOST what the row dictionary this replaces did — it filled `row[header[c]] = vals[c]`
    // for min(header, values) slots, so on a SHORT row the later duplicate was never written and the
    // earlier one won, where this resolves to the later index and falls back. Unreachable today: no header
    // in game-data repeats a column name, and the loaders' full-data output is byte-identical either way.
    // Named rather than papered over — if #35 ever meets a file with duplicate headers, this is the seam.
    private readonly Dictionary<string, int> _index;
    private readonly List<string> _missing = new();
    private readonly HashSet<string> _missingSeen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file name this table is known by — <c>mobs.csv</c>. Used in every message.</summary>
    public string Name { get; }
    /// <summary>The resolved path, or null if none resolved.</summary>
    public string? Path { get; }
    public CsvStatus Status { get; }
    /// <summary>The header, in file order. Empty when the file is missing, unreadable or headerless.</summary>
    public IReadOnlyList<string> Header { get; }
    /// <summary>Data rows the file yielded — blank and <c>#</c> lines excluded.</summary>
    public int Read => _rows.Count;
    /// <summary>Rows a loader called <see cref="CsvRow.Keep"/> on: rows that produced something.</summary>
    public int Kept { get; private set; }
    /// <summary>Rows the loader read and dropped. The complement of <see cref="Kept"/>, so a loader signals
    /// a skip by simply not keeping — no bare <c>continue</c> has to grow a reason string to be counted.</summary>
    public int Skipped => Read - Kept;
    /// <summary>Columns a loader <see cref="CsvRow.Require">required</see> that this header does not have,
    /// first-asked order. Non-empty means a renamed (or misspelled) header, and every row read that column
    /// as its fallback.</summary>
    public IReadOnlyList<string> MissingColumns => _missing;

    private CsvTable(string name, string? path, CsvStatus status, IReadOnlyList<string> header,
                     List<CsvRow> rows, Dictionary<string, int> index)
    {
        Name = name; Path = path; Status = status; Header = header; _rows = rows; _index = index;
    }

    internal static CsvTable Open(string name, string? path, IReadOnlyList<string>? suppliedHeader)
    {
        static CsvTable Barren(string name, string? path, CsvStatus status) =>
            new(name, path, status, Array.Empty<string>(), new List<CsvRow>(),
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

        if (path is null || !File.Exists(path))
        {
            Csv.Warn($"{name}: file not found ({path ?? "no path resolved"}) — the table is EMPTY");
            return Barren(name, path, CsvStatus.Missing);
        }

        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception e)
        {
            Csv.Warn($"{name}: unreadable ({path}): {e.Message} — the table is EMPTY");
            return Barren(name, path, CsvStatus.Unreadable);
        }

        int first = 0;
        while (first < lines.Length && Csv.IsSkippable(lines[first])) first++;
        if (first >= lines.Length && suppliedHeader is null)
        {
            Csv.Warn($"{name}: no header row ({path}) — the table is EMPTY");
            return Barren(name, path, CsvStatus.Empty);
        }

        IReadOnlyList<string> header = suppliedHeader is null ? Csv.Split(lines[first]) : suppliedHeader.ToArray();
        int dataStart = suppliedHeader is null ? first + 1 : first;
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int c = 0; c < header.Count; c++) index[header[c]] = c;

        var rows = new List<CsvRow>();
        var table = new CsvTable(name, path, CsvStatus.Ok, header, rows, index);
        for (int i = dataStart; i < lines.Length; i++)
        {
            if (Csv.IsSkippable(lines[i])) continue;
            rows.Add(new CsvRow(table, Csv.Split(lines[i]), i + 1));
        }

        if (rows.Count == 0)
        {
            Csv.Warn($"{name}: header but no data rows ({path}) — the table is EMPTY");
            return new CsvTable(name, path, CsvStatus.Empty, header, rows, index);
        }
        return table;
    }

    internal bool TryColumn(string column, out int idx) => _index.TryGetValue(column, out idx);

    // Reported ONCE per (table, column): a renamed header hits this on every one of the file's rows, and 9,850
    // identical lines is how a real message gets lost.
    internal void NoteMissingColumn(string column)
    {
        if (!_missingSeen.Add(column)) return;
        _missing.Add(column);
        Csv.Warn($"{Name}: column '{column}' is not in the header — every row reads it as its default. " +
                 $"Header is: {string.Join(", ", Header)}");
    }

    internal void NoteKept() => Kept++;

    /// <summary>This table's line in the load report, as it stands now. Call it after the loader has run.</summary>
    public TableLoad ToLoad() => new(Name, Path, Status, Read, Kept, MissingColumns.ToArray());

    public IEnumerator<CsvRow> GetEnumerator() => _rows.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>One data row. Columns are read by name through <see cref="Require"/>, and a row that produced
/// something says so with <see cref="Keep"/>.
/// <para>There is deliberately no "optional column" read. A column a loader names is a column the file is
/// expected to have, and the one place that looked like an exception — three files sharing one loader with
/// three different headers — was really a per-FILE difference, and is now passed as one (see
/// <c>Content.LoadAreaSpawns</c>). Making it a per-read opt-out instead would have put the loudest columns
/// in that loader straight back into the silence this type exists to end.</para></summary>
public sealed class CsvRow
{
    private readonly CsvTable _table;
    private readonly List<string> _values;
    private bool _kept;

    /// <summary>1-based line number in the file — what an operator needs to find the row.</summary>
    public int Line { get; }

    internal CsvRow(CsvTable table, List<string> values, int line)
    {
        _table = table; _values = values; Line = line;
    }

    /// <summary>The cell for <paramref name="column"/>, or <paramref name="fallback"/> when this row has no
    /// such cell. A column the HEADER does not carry is reported once per table (see
    /// <see cref="CsvTable.MissingColumns"/>) — that is the renamed-header detector. A column the header
    /// does carry but this (short) row stops before is NOT reported: a ragged row is a row problem, and the
    /// loaders already drop those.</summary>
    public string Require(string column, string fallback = "")
    {
        if (!_table.TryColumn(column, out int i)) { _table.NoteMissingColumn(column); return fallback; }
        return i < _values.Count ? _values[i] : fallback;
    }

    /// <summary>This row produced something — count it as KEPT rather than skipped. Idempotent, so a row
    /// that fans out into several registry entries still counts once.</summary>
    public void Keep()
    {
        if (_kept) return;
        _kept = true;
        _table.NoteKept();
    }

    /// <summary>The whole row as a case-insensitive column-&gt;value map, for the loaders that hand a row on
    /// verbatim (SpellParams/ItemParams, whose Lua verb reads whatever columns it needs). Carries only the
    /// cells this row actually has, which is what the dictionary this replaces carried.</summary>
    public IReadOnlyDictionary<string, string> ToDictionary()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int c = 0; c < _table.Header.Count && c < _values.Count; c++) d[_table.Header[c]] = _values[c];
        return d;
    }
}

/// <summary>What one content file did on the last load. <see cref="Skipped"/> is rows the loader read and
/// dropped; for a Lua script (which has no rows) read/kept are 1/1 loaded, 1/0 rejected.</summary>
public sealed record TableLoad(string Name, string? Path, CsvStatus Status,
                               int Read, int Kept, IReadOnlyList<string> MissingColumns)
{
    public int Skipped => Read - Kept;

    /// <summary>True for a Lua script rather than a CSV — it has no rows, so its 1/1 or 1/0 read/kept is a
    /// stand-in for "loaded" / "rejected" and only the wording of <see cref="Problem"/> differs.</summary>
    public bool IsScript { get; init; }

    /// <summary>Nothing to report: the file was there, every required column was there, and it kept rows.</summary>
    public bool Ok => Status == CsvStatus.Ok && MissingColumns.Count == 0 && Kept > 0;

    /// <summary>Why this table is not <see cref="Ok"/>, or null when it is.</summary>
    public string? Problem => IsScript ? ScriptProblem : Status switch
    {
        CsvStatus.Missing => $"{Name}: FILE NOT FOUND ({Path ?? "no path resolved"}) — nothing loaded",
        CsvStatus.Unreadable => $"{Name}: UNREADABLE ({Path}) — nothing loaded",
        CsvStatus.Empty => $"{Name}: NO DATA ROWS ({Path}) — nothing loaded",
        _ when MissingColumns.Count > 0 =>
            $"{Name}: column(s) {string.Join(", ", MissingColumns.Select(c => $"'{c}'"))} " +
            "missing from the header — every row read them as their default (renamed column?)",
        _ when Kept == 0 => $"{Name}: 0 of {Read} row(s) kept — every row was skipped",
        _ => null,
    };

    // A Lua script fails differently: it is loaded atomically and a rejected one leaves the PREVIOUS version
    // running, which is the single most important thing to say (a silent "reload ok" after a typo is how you
    // end up debugging the wrong thing).
    private string? ScriptProblem =>
        Status == CsvStatus.Missing ? $"{Name}: FILE NOT FOUND ({Path ?? "no path resolved"}) — nothing loaded"
      : Kept == 0 ? $"{Name}: REJECTED — the previously loaded version is still running (the compile error is above)"
      : null;
}

/// <summary>Every content file's <see cref="TableLoad"/> from one load, in load order — what replaced the
/// hand-written startup summary that covered 36 of 68 tables and could not tell you about the other 32.</summary>
public sealed class ContentLoadReport : IReadOnlyList<TableLoad>
{
    private readonly IReadOnlyList<TableLoad> _tables;

    public ContentLoadReport(IEnumerable<TableLoad> tables) => _tables = tables.ToArray();

    public static ContentLoadReport Empty { get; } = new(Array.Empty<TableLoad>());

    public TableLoad this[int i] => _tables[i];
    public int Count => _tables.Count;
    public IEnumerator<TableLoad> GetEnumerator() => _tables.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The table by file name, or null if this load did not have one.</summary>
    public TableLoad? this[string name] =>
        _tables.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>One line per table that is not <see cref="TableLoad.Ok"/>. Empty on a healthy load.</summary>
    public IReadOnlyList<string> Problems =>
        _tables.Select(t => t.Problem).Where(p => p is not null).Select(p => p!).ToArray();

    /// <summary>The full census: every table with its read/kept/skipped counts, several per line so 68
    /// tables cost a handful of lines rather than 68 — each entry still carries its file name, so it greps.
    /// The first line is the roll-up.</summary>
    public IEnumerable<string> Census(int perLine = 4)
    {
        int read = _tables.Sum(t => t.Read), kept = _tables.Sum(t => t.Kept);
        int bad = _tables.Count(t => !t.Ok);
        yield return $"content: {Count} tables, {read:N0} rows read, {kept:N0} kept, {read - kept:N0} skipped" +
                     (bad == 0 ? "" : $"   *** {bad} table(s) with problems, above ***");
        var line = new List<string>(perLine);
        foreach (var t in _tables)
        {
            line.Add($"{(t.Ok ? " " : "!")}{t.Name} {t.Read}/{t.Kept}/{t.Skipped}");
            if (line.Count < perLine) continue;
            yield return "content:  " + string.Join("   ", line);
            line.Clear();
        }
        if (line.Count > 0) yield return "content:  " + string.Join("   ", line);
    }
}
