using Server;
using Xunit;

namespace Tests;

/// <summary>Regression guards for failures that must not abort a live content reload after earlier tables
/// have already swapped.</summary>
public class ContentReloadTests
{
    [Fact]
    public async Task ConcurrentReloadReportsThatReloadIsAlreadyInProgress()
    {
        TestProcessState.LoadContent();
        var field = typeof(World).GetField("ReloadGate",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var reloadGate = Assert.IsType<SemaphoreSlim>(field?.GetValue(null));
        // The constructor starts the live world's background machinery on this branch. The contended path
        // returns before touching instance state, so an uninitialized instance isolates the gate response.
        var world = (World)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(World));
        using var started = new ManualResetEventSlim();
        Task<(bool ok, string report)>? waiting = null;
        bool gateHeld = false;
        try
        {
            reloadGate.Wait();
            gateHeld = true;
            waiting = Task.Run(() => { started.Set(); return world.ReloadFromDisk(); });
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "the concurrent reload task did not start");

            var result = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.ok);
            Assert.Equal("reload already in progress", result.report);
        }
        finally
        {
            // If an assertion failed against an unbounded Wait, release it first; either way the task must be
            // finished before LoadContent restores process-wide registries for every later test.
            if (waiting is not null && !waiting.IsCompleted)
            {
                reloadGate.Release();
                gateHeld = false;
            }
            try { if (waiting is not null) await waiting.WaitAsync(TimeSpan.FromSeconds(15)); }
            finally
            {
                if (gateHeld) reloadGate.Release();
                TestProcessState.LoadContent();
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
                Assert.DoesNotContain("Maps", error.Message);
                Assert.Contains("CraftingToggleOverrides", error.Message);
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
