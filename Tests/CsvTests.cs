using System.Text;
using Shared;
using Xunit;

namespace Tests;

public sealed class CsvTests
{
    [Fact]
    public void SuppliedHeaderMakesEveryLineData()
    {
        using var file = new TempCsv("1,alpha\n2,beta\n");

        var rows = Csv.Open("headerless.csv", file.Path, "Id", "Name").ToArray();

        Assert.Equal(2, rows.Length);
        Assert.Equal("1", rows[0].Require("Id"));
        Assert.Equal("alpha", rows[0].Require("Name"));
        Assert.Equal("2", rows[1].Require("Id"));
        Assert.Equal("beta", rows[1].Require("Name"));
    }

    [Fact]
    public void RaggedHeaderlessRowFallsBackWithoutAHeaderWarning()
    {
        using var file = new TempCsv("1\n");
        var table = Csv.Open("ragged.csv", file.Path, "Id", "Name");

        var row = Assert.Single(table);

        Assert.Equal(1, row.FieldCount);
        Assert.Equal("missing", row.Require("Name", "missing"));
        Assert.Empty(table.MissingColumns);
    }

    [Fact]
    public void TrailingCommaIsAnEmptyFinalField()
    {
        using var file = new TempCsv("1,alpha,\n");

        var row = Assert.Single(Csv.Open("trailing.csv", file.Path, "Id", "Name", "Note"));

        Assert.Equal(3, row.FieldCount);
        Assert.Equal("", row.Require("Note", "missing"));
    }

    [Fact]
    public void CommentsOnlyHeaderlessFileIsEmpty()
    {
        using var file = new TempCsv("\n  # first\n# second\n");

        var table = Csv.Open("comments.csv", file.Path, "Id", "Name");

        Assert.Equal(CsvStatus.Empty, table.Status);
        Assert.Equal(new[] { "Id", "Name" }, table.Header);
        Assert.Empty(table);
    }

    [Fact]
    public void HeaderLookingLineIsDataWhenHeaderIsSupplied()
    {
        using var file = new TempCsv("Id,Name\n1,alpha\n");

        var rows = Csv.Open("still-headerless.csv", file.Path, "Id", "Name").ToArray();

        Assert.Equal(2, rows.Length);
        Assert.Equal("Id", rows[0].Require("Id"));
        Assert.Equal("Name", rows[0].Require("Name"));
    }

    [Fact]
    public void Utf8BomIsRemovedFromFirstHeaderlessField()
    {
        using var file = new TempCsv("1,alpha\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var row = Assert.Single(Csv.Open("bom.csv", file.Path, "Id", "Name"));

        Assert.Equal("1", row.Require("Id"));
        Assert.Equal("alpha", row.Require("Name"));
    }

    [Fact]
    public void MissingColumnRequireFallsBackAndReportsTheColumnOnce()
    {
        using var file = new TempCsv("1\n2\n");
        var table = Csv.Open("missing-column.csv", file.Path, "Id");

        var rows = table.ToArray();
        Assert.All(rows, row => Assert.Equal("fallback", row.Require("Name", "fallback")));

        Assert.Equal(new[] { "Name" }, table.MissingColumns);
        Assert.All(rows, row => Assert.Equal(1, row.FieldCount));
    }

    [Fact(Skip = "#131")]
    public void QuotedHashRowIsTreatedAsAComment()
    {
        using var file = new TempCsv("\"# comment\",ignored\n");

        Assert.Empty(Csv.Open("quoted-comment.csv", file.Path, "Id", "Name"));
    }

    private sealed class TempCsv : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"p1998-csv-{Guid.NewGuid():N}.csv");

        public TempCsv(string contents, Encoding? encoding = null) =>
            File.WriteAllText(Path, contents, encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        public void Dispose()
        {
            try { File.Delete(Path); } catch { /* best-effort cleanup of a test fixture */ }
        }
    }
}
