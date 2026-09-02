using Server;
using Xunit;

namespace Tests;

/// <summary>Regression guards for failures that must not abort a live content reload after earlier tables
/// have already swapped.</summary>
public class ContentReloadTests
{
    [Fact]
    public void ConcurrentReloadWaitsForTheCurrentReload()
    {
        lock (TestProcessState.Gate)
        {
            string dir = Path.Combine(Path.GetTempPath(), "project1998-reload-gate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "map_index.csv");
            File.WriteAllText(path, "id,name,xs,ys\n65000,Reload gate fixture,1,1\n");

            string? previous = Environment.GetEnvironmentVariable("P1998_MAP_INDEX");
            try
            {
                Environment.SetEnvironmentVariable("P1998_MAP_INDEX", path);
                var mapsBefore = Content.Maps;
                var field = typeof(Content).GetField("ReloadGate",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var reloadGate = Assert.IsType<SemaphoreSlim>(field?.GetValue(null));
                using var started = new ManualResetEventSlim();
                reloadGate.Wait();
                Task? waiting = null;
                try
                {
                    waiting = Task.Run(() => { started.Set(); Content.Reload(); });
                    Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "the concurrent reload task did not start");
                    Assert.False(SpinWait.SpinUntil(() => !ReferenceEquals(mapsBefore, Content.Maps),
                            TimeSpan.FromSeconds(1)),
                        "a second Content.Reload replaced Maps while the first caller held the reload gate");
                }
                finally { reloadGate.Release(); }

                Assert.True(SpinWait.SpinUntil(() => waiting.IsCompleted, TimeSpan.FromSeconds(10)),
                    "the waiting reload did not resume after release");
                Assert.Null(waiting.Exception);
                Assert.Equal("Reload gate fixture", Assert.Single(Content.Maps).Value.Name);
            }
            finally
            {
                Environment.SetEnvironmentVariable("P1998_MAP_INDEX", previous);
                TestProcessState.LoadContent();
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup of a test fixture */ }
            }
        }
    }

    [Fact]
    public void MalformedMobDropRowsAreSkippedWithoutAbortingTheLoad()
    {
        lock (TestProcessState.Gate)
        {
            string dir = Path.Combine(Path.GetTempPath(), "project1998-mob-drops-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "MobDrops.csv");
            File.WriteAllText(path,
                "MobKey,Loot,RareLoot\n" +
                "valid_drop_row,GOLD:2:50,\n" +
                "bad_number,GOLD:not-an-integer:25,\n" +
                "bad_two_parts,apple:5,\n" +
                "bad_one_part,apple,\n" +
                "bad_four_parts,apple:1:2:3,\n" +
                "bad_negative_amount,apple:-5:50,\n" +
                "bad_negative_rate,apple:1:-50,\n" +
                "bad_infinite_rate,apple:1:Infinity,\n" +
                "bad_nan_rate,apple:1:NaN,\n" +
                "bad_rare_rate,,amber:Infinity\n" +
                "after_bad_row,,amber:12.5\n");

            string? previous = Environment.GetEnvironmentVariable("P1998_MOB_DROPS");
            try
            {
                Environment.SetEnvironmentVariable("P1998_MOB_DROPS", path);
                TestProcessState.LoadContent();

                var valid = Assert.IsType<MobDropDef>(Content.MobDrops["valid_drop_row"]);
                Assert.Equal(2, Assert.Single(valid.Loot).MaxAmount);
                foreach (string badKey in new[]
                {
                    "bad_number", "bad_two_parts", "bad_one_part", "bad_four_parts",
                    "bad_negative_amount", "bad_negative_rate", "bad_infinite_rate", "bad_nan_rate",
                    "bad_rare_rate",
                })
                    Assert.False(Content.MobDrops.ContainsKey(badKey), $"{badKey} should have been skipped");
                Assert.Equal(12.5, Assert.Single(Content.MobDrops["after_bad_row"].Rare).RatePercent);
            }
            finally
            {
                Environment.SetEnvironmentVariable("P1998_MOB_DROPS", previous);
                TestProcessState.LoadContent();
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup of a test fixture */ }
            }
        }
    }

    [Fact]
    public void FailedReloadNamesTablesReplacedBeforeTheFailure()
    {
        lock (TestProcessState.Gate)
        {
            string dir = Path.Combine(Path.GetTempPath(), "project1998-mythic-caves-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "MythicCaves.csv");
            File.WriteAllText(path,
                "Animal,EntranceMap,EntranceTiles,DestMap,DestX,DestY,T1Level,T1Vita,T1Mana,T2Level,T2Vita,T2Mana,T3Level,T3Vita,T3Mana,Sources\n" +
                "Broken,41,1:1;1:1,201,1,1,1,0,0,1,0,0,1,0,0,test\n");

            string? previous = Environment.GetEnvironmentVariable("P1998_MYTHIC_CAVES");
            try
            {
                Environment.SetEnvironmentVariable("P1998_MYTHIC_CAVES", path);
                var error = Assert.Throws<InvalidOperationException>(() => Content.Reload());

                Assert.Contains("Public content tables replaced before failure:", error.Message);
                Assert.Contains("Maps", error.Message);
                Assert.Contains("MobDrops", error.Message);
                Assert.DoesNotContain("previous content kept", error.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Environment.SetEnvironmentVariable("P1998_MYTHIC_CAVES", previous);
                TestProcessState.LoadContent();
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup of a test fixture */ }
            }
        }
    }
}
