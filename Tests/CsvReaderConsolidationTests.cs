using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Server;
using Shared;
using Xunit;

namespace Tests;

/// <summary>Proof for #35's one-reader extraction: the four server tables keep their pre-swap parsed
/// values, every CSV is opened once per content load, and the formerly silent era failure is observable.</summary>
public class CsvReaderConsolidationTests
{
    [Fact]
    public void SwappedReadersMatchTheirPreConversionHashes()
    {
        lock (TestProcessState.Gate)
        {
            TestProcessState.LoadContent();

            var features = EraCalendar.FeaturesForTests
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => string.Join('|',
                    kv.Key,
                    Date(kv.Value.Introduced),
                    Date(kv.Value.Retired),
                    kv.Value.Source,
                    kv.Value.Notes));
            Assert.Equal(10, EraCalendar.FeatureCount);
            Assert.Equal("f96f6ce6fd3b232fa8de8e27b4aba957ad1e9cb778f518056007b75de290e7b2",
                Hash(features));

            var overrides = ObjectFlags.OverridesForTests
                .OrderBy(v => v.Id)
                .Select(v => $"{v.Id}|{v.Flag}");
            Assert.Single(ObjectFlags.OverridesForTests);
            Assert.Equal("378bd85e787ab9a124cca76e4315685f40e5409a90faede9f83bed1a16d8b0e7",
                Hash(overrides));

            var obj533 = TileTranslation.Obj533ForTests
                .OrderBy(v => v.Legacy)
                .Select(v => $"{v.Legacy}|{v.Replacement}|{v.Scope}");
            Assert.Equal(128, TileTranslation.Obj533ForTests.Count);
            Assert.Equal("7859375b8e6cde626459fcebe0992eea505f2a8e52f26219a374f28baad8bfd5",
                Hash(obj533));

            var sheet2 = TileTranslation.Sheet2ForTests
                .OrderBy(kv => kv.Key)
                .Select(kv => $"{kv.Key}|{kv.Value}");
            Assert.Equal(8930, TileTranslation.Sheet2ForTests.Count);
            Assert.Equal("0eea3e797a4f5b573a1cfdf2194bb610b3da14e63dff3526d6dcde28b257531f",
                Hash(sheet2));
        }
    }

    [Fact]
    public void ContentLoadOpensEveryCsvExactlyOnce()
    {
        lock (TestProcessState.Gate)
        {
            var opens = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Csv.OpenObserverForTests = (name, _) => opens.AddOrUpdate(name, 1, (_, n) => n + 1);
                TestProcessState.LoadContent();
            }
            finally { Csv.OpenObserverForTests = null; }

            Assert.Equal(68, opens.Count);
            Assert.All(opens, entry => Assert.Equal(1, entry.Value));
            foreach (string name in new[]
                     {
                         "ServerTuning.csv", "EraFeatures.csv", "ObjectFlagOverrides.csv",
                         "Obj533Fix.csv", "Tile533Map.csv",
                     })
                Assert.Equal(1, opens[name]);
        }
    }

    [Fact]
    public void MissingEraFeaturesIsWarnedAndReported()
    {
        lock (TestProcessState.Gate)
        {
            string? previous = Environment.GetEnvironmentVariable("P1998_ERA_FEATURES");
            string missing = Path.Combine(Path.GetTempPath(), $"p1998-era-not-here-{Guid.NewGuid():N}.csv");
            var warnings = new ConcurrentQueue<string>();
            try
            {
                Environment.SetEnvironmentVariable("P1998_ERA_FEATURES", missing);
                Csv.WarningObserverForTests = warnings.Enqueue;
                TestProcessState.LoadContent();

                var entry = Content.LoadReport["EraFeatures.csv"];
                Assert.NotNull(entry);
                Assert.Equal(CsvStatus.Missing, entry!.Status);
                Assert.Equal(0, entry.Kept);
                Assert.Contains(Content.LoadReport.Problems,
                    p => p.Contains("EraFeatures.csv") && p.Contains("FILE NOT FOUND"));
                Assert.Contains(warnings,
                    warning => warning.Contains("EraFeatures.csv") && warning.Contains("file not found"));
            }
            finally
            {
                Csv.WarningObserverForTests = null;
                Environment.SetEnvironmentVariable("P1998_ERA_FEATURES", previous);
                TestProcessState.LoadContent();
            }
        }
    }

    private static string Date(DateOnly? date) =>
        date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

    private static string Hash(IEnumerable<string> lines) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))))
            .ToLowerInvariant();
}
