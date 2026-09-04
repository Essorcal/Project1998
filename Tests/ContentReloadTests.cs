using Server;
using Xunit;

namespace Tests;

/// <summary>Regression guards for serializing reload writers and publishing every content table atomically.</summary>
public class ContentReloadTests
{
    [Fact]
    public void LoadDoesNotReadFacadesOnTheLoadingThread()
    {
        lock (TestProcessState.Gate)
        {
            TestProcessState.LoadContent();

            Assert.Equal(0, Content.LoadingThreadFacadeReadsForTests);
        }
    }

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

    // The duplicate tile throws after the era prepare but before Doors and Lua prepare, so this test's unique
    // job is guarding the era commit against a real mid-load data failure.
    [Fact]
    public void RealMidLoadFailureKeepsEraDoorsLuaAndSnapshot()
    {
        lock (TestProcessState.Gate)
        {
            TestProcessState.LoadContent();
            object beforeSnapshot = Content.SnapshotIdentityForTests;
            int beforeEra = Shared.EraCalendar.RawDate;
            var beforeDoor = Doors.For(64000, 1, 1);
            bool beforeHook = MobScript.Has("content_reload_probe", MobScript.OnSpawn);

            string dir = Path.Combine(Path.GetTempPath(), "project1998-reload-failure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string caves = Path.Combine(dir, "MythicCaves.csv");
            File.WriteAllText(caves,
                "Animal,EntranceMap,EntranceTiles,DestMap,DestX,DestY,T1Level,T1Vita,T1Mana,T2Level,T2Vita,T2Mana,T3Level,T3Vita,T3Mana,Sources\n" +
                "Broken,41,1:1;1:1,201,1,1,1,0,0,1,0,0,1,0,0,test\n");
            string tuning = Path.Combine(dir, "ServerTuning.csv");
            File.WriteAllText(tuning, "key,value\nEraDate,19990102\n");
            string doors = Path.Combine(dir, "Doors.csv");
            File.WriteAllText(doors,
                "Map,X,Y,Locked,Key,ConsumeKey,ForceOpen,StartDx,ClosedObj,OpenObj,DefaultClosed,Sources\n" +
                "64000,1,1,1,probe_key,1,1,0,,,0,review-probe\n");
            string mobAi = Path.Combine(dir, "mob_ai.lua");
            File.WriteAllText(mobAi,
                "mobs = { content_reload_probe = { on_spawn = function(ctx) end } }\n");

            string? previousCaves = Environment.GetEnvironmentVariable("P1998_MYTHIC_CAVES");
            string? previousTuning = Environment.GetEnvironmentVariable("P1998_SERVER_TUNING");
            string? previousDoors = Environment.GetEnvironmentVariable("P1998_DOORS");
            string? previousMobAi = Environment.GetEnvironmentVariable("P1998_MOB_AI");
            try
            {
                Environment.SetEnvironmentVariable("P1998_MYTHIC_CAVES", caves);
                Environment.SetEnvironmentVariable("P1998_SERVER_TUNING", tuning);
                Environment.SetEnvironmentVariable("P1998_DOORS", doors);
                Environment.SetEnvironmentVariable("P1998_MOB_AI", mobAi);

                var error = Assert.Throws<InvalidOperationException>(() => Content.Reload());

                Assert.Contains("Reload failed (previous content kept).", error.Message);
                Assert.Same(beforeSnapshot, Content.SnapshotIdentityForTests);
                Assert.Equal(beforeEra, Shared.EraCalendar.RawDate);
                Assert.Same(beforeDoor, Doors.For(64000, 1, 1));
                Assert.Equal(beforeHook, MobScript.Has("content_reload_probe", MobScript.OnSpawn));
            }
            finally
            {
                Environment.SetEnvironmentVariable("P1998_MYTHIC_CAVES", previousCaves);
                Environment.SetEnvironmentVariable("P1998_SERVER_TUNING", previousTuning);
                Environment.SetEnvironmentVariable("P1998_DOORS", previousDoors);
                Environment.SetEnvironmentVariable("P1998_MOB_AI", previousMobAi);
                TestProcessState.LoadContent();
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup of a test fixture */ }
            }
        }
    }

    [Fact]
    public void RealMidLoadFailureKeepsObjectFlagsAndTileTranslations()
    {
        lock (TestProcessState.Gate)
        {
            string dir = Path.Combine(Path.GetTempPath(), "project1998-external-content-failure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string oldOverrides = Path.Combine(dir, "old-overrides.csv");
            string newOverrides = Path.Combine(dir, "new-overrides.csv");
            string oldObj533 = Path.Combine(dir, "old-obj533.csv");
            string newObj533 = Path.Combine(dir, "new-obj533.csv");
            string oldSheet2 = Path.Combine(dir, "old-sheet2.csv");
            string newSheet2 = Path.Combine(dir, "new-sheet2.csv");
            string caves = Path.Combine(dir, "MythicCaves.csv");
            File.WriteAllText(oldOverrides, "60000,0x01,old\n");
            File.WriteAllText(newOverrides, "60001,0x02,new\n");
            File.WriteAllText(oldObj533, "60000,suppress,0,0,0,0,structural\n");
            File.WriteAllText(newObj533, "60001,suppress,0,0,0,0,structural\n");
            File.WriteAllText(oldSheet2, "60000,1,61000\n");
            File.WriteAllText(newSheet2, "60001,1,61001\n");
            File.WriteAllText(caves,
                "Animal,EntranceMap,EntranceTiles,DestMap,DestX,DestY,T1Level,T1Vita,T1Mana,T2Level,T2Vita,T2Mana,T3Level,T3Vita,T3Mana,Sources\n" +
                "Broken,41,1:1;1:1,201,1,1,1,0,0,1,0,0,1,0,0,test\n");

            string? previousOverrides = Environment.GetEnvironmentVariable("P1998_OBJECT_FLAG_OVERRIDES");
            string? previousObj533 = Environment.GetEnvironmentVariable("P1998_OBJ533_FIX");
            string? previousSheet2 = Environment.GetEnvironmentVariable("P1998_TILE533_MAP");
            string? previousCaves = Environment.GetEnvironmentVariable("P1998_MYTHIC_CAVES");
            try
            {
                Environment.SetEnvironmentVariable("P1998_OBJECT_FLAG_OVERRIDES", oldOverrides);
                Environment.SetEnvironmentVariable("P1998_OBJ533_FIX", oldObj533);
                Environment.SetEnvironmentVariable("P1998_TILE533_MAP", oldSheet2);
                TestProcessState.LoadContent();

                Assert.Equal((60000, (byte)1), Assert.Single(ObjectFlags.OverridesForTests));
                Assert.Equal((ushort)60000, Assert.Single(TileTranslation.Obj533ForTests).Legacy);
                Assert.Equal((ushort)61000, TileTranslation.Sheet2ForTests[60000]);

                Environment.SetEnvironmentVariable("P1998_OBJECT_FLAG_OVERRIDES", newOverrides);
                Environment.SetEnvironmentVariable("P1998_OBJ533_FIX", newObj533);
                Environment.SetEnvironmentVariable("P1998_TILE533_MAP", newSheet2);
                Environment.SetEnvironmentVariable("P1998_MYTHIC_CAVES", caves);

                var error = Assert.Throws<InvalidOperationException>(() => Content.Reload());

                Assert.Contains("Reload failed (previous content kept).", error.Message);
                Assert.Equal((60000, (byte)1), Assert.Single(ObjectFlags.OverridesForTests));
                Assert.Equal((ushort)60000, Assert.Single(TileTranslation.Obj533ForTests).Legacy);
                Assert.Equal((ushort)61000, TileTranslation.Sheet2ForTests[60000]);
                Assert.False(TileTranslation.Sheet2ForTests.ContainsKey(60001));
            }
            finally
            {
                Environment.SetEnvironmentVariable("P1998_OBJECT_FLAG_OVERRIDES", previousOverrides);
                Environment.SetEnvironmentVariable("P1998_OBJ533_FIX", previousObj533);
                Environment.SetEnvironmentVariable("P1998_TILE533_MAP", previousSheet2);
                Environment.SetEnvironmentVariable("P1998_MYTHIC_CAVES", previousCaves);
                TestProcessState.LoadContent();
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup of a test fixture */ }
            }
        }
    }

    [Fact]
    public void SuccessfulReloadPublishesObjectFlagsAndTileTranslations()
    {
        lock (TestProcessState.Gate)
        {
            string dir = Path.Combine(Path.GetTempPath(), "project1998-external-content-success-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string oldOverrides = Path.Combine(dir, "old-overrides.csv");
            string newOverrides = Path.Combine(dir, "new-overrides.csv");
            string oldObj533 = Path.Combine(dir, "old-obj533.csv");
            string newObj533 = Path.Combine(dir, "new-obj533.csv");
            string oldSheet2 = Path.Combine(dir, "old-sheet2.csv");
            string newSheet2 = Path.Combine(dir, "new-sheet2.csv");
            File.WriteAllText(oldOverrides, "60000,0x01,old\n");
            File.WriteAllText(newOverrides, "60001,0x02,new\n");
            File.WriteAllText(oldObj533, "60000,suppress,0,0,0,0,structural\n");
            File.WriteAllText(newObj533, "60001,suppress,0,0,0,0,structural\n");
            File.WriteAllText(oldSheet2, "60000,1,61000\n");
            File.WriteAllText(newSheet2, "60001,1,61001\n");

            string? previousOverrides = Environment.GetEnvironmentVariable("P1998_OBJECT_FLAG_OVERRIDES");
            string? previousObj533 = Environment.GetEnvironmentVariable("P1998_OBJ533_FIX");
            string? previousSheet2 = Environment.GetEnvironmentVariable("P1998_TILE533_MAP");
            try
            {
                Environment.SetEnvironmentVariable("P1998_OBJECT_FLAG_OVERRIDES", oldOverrides);
                Environment.SetEnvironmentVariable("P1998_OBJ533_FIX", oldObj533);
                Environment.SetEnvironmentVariable("P1998_TILE533_MAP", oldSheet2);
                TestProcessState.LoadContent();

                Environment.SetEnvironmentVariable("P1998_OBJECT_FLAG_OVERRIDES", newOverrides);
                Environment.SetEnvironmentVariable("P1998_OBJ533_FIX", newObj533);
                Environment.SetEnvironmentVariable("P1998_TILE533_MAP", newSheet2);
                Content.Reload();

                Assert.Equal((60001, (byte)2), Assert.Single(ObjectFlags.OverridesForTests));
                Assert.Equal((ushort)60001, Assert.Single(TileTranslation.Obj533ForTests).Legacy);
                Assert.Equal((ushort)61001, TileTranslation.Sheet2ForTests[60001]);
                Assert.False(TileTranslation.Sheet2ForTests.ContainsKey(60000));
            }
            finally
            {
                Environment.SetEnvironmentVariable("P1998_OBJECT_FLAG_OVERRIDES", previousOverrides);
                Environment.SetEnvironmentVariable("P1998_OBJ533_FIX", previousObj533);
                Environment.SetEnvironmentVariable("P1998_TILE533_MAP", previousSheet2);
                TestProcessState.LoadContent();
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup of a test fixture */ }
            }
        }
    }

    [Fact]
    public void ReaderNeverSeesItemsWithoutMatchingIndexDuringReload()
    {
        const int reloadCount = 10;
        const int readsPerReload = 10_000;

        lock (TestProcessState.Gate)
        {
            TestProcessState.LoadContent();
            var firstItems = Content.Items;
            using var itemsLoaded = new SemaphoreSlim(0);
            using var resumeLoad = new SemaphoreSlim(0);
            Thread? reloadThread = null;
            Exception? reloadFailure = null;
            int mismatches = 0;

            try
            {
                Content.LoadStepForTests = step =>
                {
                    if (step != "ItemsLoaded") return;
                    itemsLoaded.Release();
                    if (!resumeLoad.Wait(TimeSpan.FromSeconds(15)))
                        throw new TimeoutException("reader did not release the paused content load");
                };

                reloadThread = new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < reloadCount; i++) Content.Reload();
                    }
                    catch (Exception e)
                    {
                        reloadFailure = e;
                    }
                });
                reloadThread.Start();

                for (int reload = 0; reload < reloadCount; reload++)
                {
                    Assert.True(itemsLoaded.Wait(TimeSpan.FromSeconds(15)), $"reload {reload + 1} did not reach ItemsLoaded");
                    for (int read = 0; read < readsPerReload; read++)
                    {
                        var items = Content.Items;
                        var known = items[0];
                        if (!ReferenceEquals(known, Content.ItemById(known.Id))) mismatches++;
                    }
                    resumeLoad.Release();
                }

                Assert.True(reloadThread.Join(TimeSpan.FromSeconds(30)), "reload thread did not finish");
                Assert.Null(reloadFailure);
                Assert.Equal(0, mismatches);
                Assert.NotSame(firstItems, Content.Items); // proves the final snapshot write was not removed
            }
            finally
            {
                Content.LoadStepForTests = null;
                for (int i = 0; i < reloadCount; i++) resumeLoad.Release();
                if (reloadThread is { IsAlive: true })
                    Assert.True(reloadThread.Join(TimeSpan.FromSeconds(15)), "reload thread did not stop during cleanup");
                TestProcessState.LoadContent();
            }
        }
    }

    // BeforePublish is the widest failure window, after every candidate is prepared; this is the test that
    // guards the Doors and all four Lua commits as well as the snapshot and era.
    [Fact]
    public void FailedReloadKeepsEveryPublishedFacade()
    {
        lock (TestProcessState.Gate)
        {
            TestProcessState.LoadContent();
            object beforeSnapshot = Content.SnapshotIdentityForTests;
            var before = SnapshotBackedFacades();
            Assert.Equal(64, before.Count);
            int beforeEra = Shared.EraCalendar.RawDate;
            var beforeDoor = Doors.For(64000, 1, 1);
            bool beforeHook = MobScript.Has("content_reload_probe", MobScript.OnSpawn);

            string dir = Path.Combine(Path.GetTempPath(), "project1998-before-publish-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string tuning = Path.Combine(dir, "ServerTuning.csv");
            File.WriteAllText(tuning, "key,value\nEraDate,19990103\n");
            string doors = Path.Combine(dir, "Doors.csv");
            File.WriteAllText(doors,
                "Map,X,Y,Locked,Key,ConsumeKey,ForceOpen,StartDx,ClosedObj,OpenObj,DefaultClosed,Sources\n" +
                "64000,1,1,1,probe_key,1,1,0,,,0,review-probe\n");
            string mobAi = Path.Combine(dir, "mob_ai.lua");
            File.WriteAllText(mobAi,
                "mobs = { content_reload_probe = { on_spawn = function(ctx) end } }\n");

            string? previousTuning = Environment.GetEnvironmentVariable("P1998_SERVER_TUNING");
            string? previousDoors = Environment.GetEnvironmentVariable("P1998_DOORS");
            string? previousMobAi = Environment.GetEnvironmentVariable("P1998_MOB_AI");

            try
            {
                Environment.SetEnvironmentVariable("P1998_SERVER_TUNING", tuning);
                Environment.SetEnvironmentVariable("P1998_DOORS", doors);
                Environment.SetEnvironmentVariable("P1998_MOB_AI", mobAi);
                Content.LoadStepForTests = step =>
                {
                    if (step == "BeforePublish") throw new InvalidOperationException("injected loader failure");
                };

                var error = Assert.Throws<InvalidOperationException>(() => Content.Reload());

                Assert.Contains("Reload failed (previous content kept).", error.Message);
                Assert.Same(beforeSnapshot, Content.SnapshotIdentityForTests);
                Assert.Equal(88, Content.SnapshotMemberCountForTests);
                Assert.Equal(beforeEra, Shared.EraCalendar.RawDate);
                Assert.Same(beforeDoor, Doors.For(64000, 1, 1));
                Assert.Equal(beforeHook, MobScript.Has("content_reload_probe", MobScript.OnSpawn));

                var after = SnapshotBackedFacades();
                Assert.Equal(before.Keys, after.Keys);
                foreach (string name in before.Keys)
                {
                    var property = typeof(Content).GetProperty(name)!;
                    if (property.PropertyType.IsValueType) Assert.Equal(before[name], after[name]);
                    else Assert.Same(before[name], after[name]);
                }
            }
            finally
            {
                Content.LoadStepForTests = null;
                Environment.SetEnvironmentVariable("P1998_SERVER_TUNING", previousTuning);
                Environment.SetEnvironmentVariable("P1998_DOORS", previousDoors);
                Environment.SetEnvironmentVariable("P1998_MOB_AI", previousMobAi);
                TestProcessState.LoadContent();
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup of a test fixture */ }
            }
        }
    }

    private static SortedDictionary<string, object?> SnapshotBackedFacades() =>
        new(typeof(Content)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(p => p.GetIndexParameters().Length == 0 && p.GetSetMethod(nonPublic: true)?.IsPrivate == true)
            .ToDictionary(p => p.Name, p => p.GetValue(null)));
}
