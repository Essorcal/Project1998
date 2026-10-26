using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The game server boots on the synthetic profile (<c>test-content/synthetic/</c>) as a separate process, with
/// the environment its <c>manifest.json</c> names, and stops cleanly. Run as a subprocess because the content
/// root, the configuration snapshot and the log are process-wide: a boot in this process would change them for
/// every other test, and would not be a boot.
///
/// <para><b>What "boots cleanly" means here.</b> No error line (<c>!!!</c>) and no <c>FATAL</c> anywhere in the
/// log; the startup terrain line counts 2/2 maps, searched inside the profile only; and every warning written
/// before that line is one of an explicit list. The profile carries five of the 68 tables, so the list is long
/// and fixed: two lines for each table it does not carry, the 48 built-in spell-ladder rungs that find no spell
/// without a <c>Spells.csv</c> (<c>Server/Content.Spells.cs</c>, <c>BuildSpellLadders</c>), and the "no maps
/// and/or no mobs" line. A warning that is not on the list (a table the profile lost, a column a loader now
/// requires, a new table the server reads) or a listed one that went missing turns this red.</para>
///
/// <para><b>Ports.</b> Two consecutive ports drawn at random from <see cref="PortsFrom"/> up to
/// <see cref="PortsTo"/>, kept clear of every multiple of 1000, and checked free on 0.0.0.0 and on loopback
/// before the server starts. So never the live channels 2000-2006 or a development server's port base, which sit on round
/// thousands, and never the OS's dynamic range (49152 up on Windows, 32768 up on Linux). That range was the
/// first choice and lost about one boot in eight: Windows hands its ports out in sequence, so the next socket
/// any process on the box opened took the pair between the check and the server's bind. The server binds
/// loopback only (<c>P1998_BIND</c>). A port someone takes anyway shows as "could not listen on"; that boot is
/// killed at once and retried on a new pair.</para>
///
/// <para><b>Stopping.</b> Through <c>run/restart_at</c> (<c>Server/RestartSchedule.cs</c>), the file trigger a
/// deploy uses, in a temp <c>P1998_RUN</c>: the server exits 0 through the same flush SIGTERM takes, on Windows
/// and Linux alike. The trigger is polled every 6 s and its deadline must still be ahead when it is read, so
/// the deadline is written <see cref="TriggerLead"/> ahead and a stop takes about 13 s. A server that does not
/// stop in time, or a test that fails before asking it to, is killed with its process tree in a
/// <c>finally</c>, so a failure never leaves a process behind. A test host that dies outright runs no
/// <c>finally</c>, so a first trigger <see cref="Leash"/> ahead is in place before the server starts: an
/// orphaned server stops itself.</para>
///
/// <para>The state, log and run folders sit in one new temp folder per boot, deleted afterwards. A test host that
/// dies cannot delete its own, so each run first sweeps the ones nobody has written to for
/// <see cref="StaleAfter"/> (<see cref="SweepStaleBootFolders"/>). Every <c>P1998_*</c> variable of this
/// process is dropped from the child's environment first, so a developer's shell cannot leak into the
/// boot.</para>
/// </summary>
public class SyntheticProfileBootTests
{
    /// <summary>The ports the boot is drawn from: above the hand-picked development bases and below either OS's
    /// dynamic range.</summary>
    private const int PortsFrom = 22_000, PortsTo = 32_000;

    private static readonly TimeSpan BootBound = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StopBound = TimeSpan.FromSeconds(60);

    /// <summary>The restart trigger's deadline, from the moment it is written: the 6 s poll plus margin.</summary>
    private static readonly TimeSpan TriggerLead = TimeSpan.FromSeconds(10);

    /// <summary>The deadline of the trigger written before the server starts, which stops a server whose test
    /// host died: past <see cref="BootBound"/>, so it never cuts a slow boot short.</summary>
    private static readonly TimeSpan Leash = TimeSpan.FromSeconds(120);

    /// <summary>Each boot's temp folder is this prefix and 32 hex digits; the sweep touches nothing else.</summary>
    private const string BootFolderPrefix = "p1998-synthetic-boot-";
    private static readonly Regex BootFolderName = new($"^{BootFolderPrefix}[0-9a-f]{{32}}$");

    /// <summary>How long a boot folder must have gone unwritten before the sweep deletes it. A boot in progress
    /// writes into its folder from the start (the server's log, then <c>run/status.json</c> every 10 s while it
    /// runs), and the longest a boot lives is three attempts of <see cref="BootBound"/> plus
    /// <see cref="StopBound"/>, or an orphan's <see cref="Leash"/>: 6 minutes at most, against 15.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    /// <summary>The tables the profile does not carry, in load order (the order of <c>Content.TableSpecs</c>).
    /// Each is one "file not found" line when it is opened and one <c>content:</c> line in the load report.</summary>
    private static readonly string[] AbsentTables =
    [
        "Obj533Fix.csv", "Tile533Map.csv", "MobFlees.csv", "MobStationary.csv", "mobs.csv", "Items.csv",
        "Spawns.csv", "AreaSpawns.csv", "AreaSpawnsTrap.csv", "AreaSpawnsCrafting.csv", "ServerTuning.csv",
        "EraFeatures.csv", "MinorQuests.csv", "ShopStock.csv", "ShopBuysFrom.csv", "Paths.csv", "LevelExp.csv",
        "SpellLevels.csv", "Spells.csv", "spell_effects.csv", "SpellText.csv", "SpellLearnCosts.csv",
        "Mob5xPalettes.csv", "ArmorDyeRamps.csv", "MobDrops.csv", "CraftingToggles.csv", "WarpQuestLocks.csv",
        "ArmorQuests.csv", "MythicCaves.csv", "MythicAlliances.csv", "ArenaDoors.csv", "EventCaveTiers.csv",
        "EventCaves.csv", "MusicTracks.csv", "MapBgm.csv", "Inns.csv", "ForageAreas.csv", "HarvestNodes.csv",
        "MobSpells.csv", "MobChatter.csv", "MobSpawnRules.csv", "MobBosses.csv", "PathHalls.csv",
        "GatewayGates.csv", "WorldMapDests.csv", "WorldMapTriggers.csv", "FallRooms.csv", "AmbushBursts.csv",
        "AmbushConfig.csv", "BoardLocations.csv", "ShopCatalogues.csv", "SpellParams.csv", "ItemParams.csv",
        "Pets.csv", "WeaponProcs.csv", "Traps.csv", "Morphs.csv", "SpellMods.csv", "NpcAbilities.csv",
        "PathGrowth.csv", "DoorObjects.csv", "Doors.csv", "MapCells.csv",
    ];

    /// <summary>The two absent tables whose report line names what their absence costs.</summary>
    private static readonly Dictionary<string, string> AbsentConsequences = new(StringComparer.Ordinal)
    {
        ["Obj533Fix.csv"] = "5.33 will over-block ~18k cells",
        ["Tile533Map.csv"] = "5.33 sheet-2 cells (30% of terrain) will be blank",
    };

    /// <summary>Every rung of the built-in spell ladders, which name spell keys a profile without
    /// <c>Spells.csv</c> does not have. Written between the <c>Spells.csv</c> and <c>spell_effects.csv</c>
    /// opens, in the ladders' own order.</summary>
    private static readonly string[] LadderWarnings =
    [
        "spell ladder 1/heal_self: no spell keyed 'soothe' — rung ignored",
        "spell ladder 1/heal_self: no spell keyed 'relief_warrior' — rung ignored",
        "spell ladder 1/heal_self: no spell keyed 'vigor_warrior' — rung ignored",
        "spell ladder 1/heal_target: no spell keyed 'fleshspeak_warrior' — rung ignored",
        "spell ladder 2/zap: no spell keyed 'singe_rogue' — rung ignored",
        "spell ladder 2/zap: no spell keyed 'ignite_rogue' — rung ignored",
        "spell ladder 2/heal_self: no spell keyed 'soothe' — rung ignored",
        "spell ladder 2/heal_self: no spell keyed 'maros_remedy_rogue' — rung ignored",
        "spell ladder 2/heal_target: no spell keyed 'fleshspeak_rogue' — rung ignored",
        "spell ladder 2/heal_target: no spell keyed 'mend_wounds_rogue' — rung ignored",
        "spell ladder 2/heal_target: no spell keyed 'recover_rogue' — rung ignored",
        "spell ladder 2/heal_target: no spell keyed 'seal_wounds_rogue' — rung ignored",
        "spell ladder 3/zap: no spell keyed 'thunder_bolt_mage' — rung ignored",
        "spell ladder 3/zap: no spell keyed 'spark_mage' — rung ignored",
        "spell ladder 3/zap: no spell keyed 'singe_mage' — rung ignored",
        "spell ladder 3/zap: no spell keyed 'ignite_mage' — rung ignored",
        "spell ladder 3/zap: no spell keyed 'ion_mage' — rung ignored",
        "spell ladder 3/zap: no spell keyed 'impact_mage' — rung ignored",
        "spell ladder 3/zap: no spell keyed 'call_lightning_mage' — rung ignored",
        "spell ladder 3/zap: no spell keyed 'stormstrike_mage' — rung ignored",
        "spell ladder 3/zap_area: no spell keyed 'erupt_mage' — rung ignored",
        "spell ladder 3/zap_area: no spell keyed 'ion_charge_mage' — rung ignored",
        "spell ladder 3/zap_area: no spell keyed 'explode_mage' — rung ignored",
        "spell ladder 3/zap_area: no spell keyed 'electrocute_mage' — rung ignored",
        "spell ladder 3/zap_area: no spell keyed 'tempest_mage' — rung ignored",
        "spell ladder 3/heal_self: no spell keyed 'soothe' — rung ignored",
        "spell ladder 3/heal_self: no spell keyed 'lay_hands_mage' — rung ignored",
        "spell ladder 3/heal_self: no spell keyed 'relief_mage' — rung ignored",
        "spell ladder 3/heal_target: no spell keyed 'fleshspeak_mage' — rung ignored",
        "spell ladder 3/heal_target: no spell keyed 'mend_wounds_mage' — rung ignored",
        "spell ladder 3/heal_target: no spell keyed 'recover_mage' — rung ignored",
        "spell ladder 3/heal_target: no spell keyed 'heal_mage' — rung ignored",
        "spell ladder 3/heal_target: no spell keyed 'rejuvenate_mage' — rung ignored",
        "spell ladder 4/zap: no spell keyed 'spark_poet' — rung ignored",
        "spell ladder 4/zap: no spell keyed 'singe_poet' — rung ignored",
        "spell ladder 4/zap: no spell keyed 'ignite_poet' — rung ignored",
        "spell ladder 4/zap: no spell keyed 'earthquake_poet' — rung ignored",
        "spell ladder 4/zap: no spell keyed 'flare_poet' — rung ignored",
        "spell ladder 4/heal_area: no spell keyed 'vital_spark_poet' — rung ignored",
        "spell ladder 4/heal_area: no spell keyed 'anoint_poet' — rung ignored",
        "spell ladder 4/heal_area: no spell keyed 'remedy_poet' — rung ignored",
        "spell ladder 4/heal_area: no spell keyed 'heavens_kiss_poet' — rung ignored",
        "spell ladder 4/heal_self: no spell keyed 'lay_hands_poet' — rung ignored",
        "spell ladder 4/heal_self: no spell keyed 'fortify_poet' — rung ignored",
        "spell ladder 4/heal_target: no spell keyed 'recover_poet' — rung ignored",
        "spell ladder 4/heal_target: no spell keyed 'heal_poet' — rung ignored",
        "spell ladder 4/heal_target: no spell keyed 'revitalize_poet' — rung ignored",
        "spell ladder 4/heal_target: no spell keyed 'water_of_life_poet' — rung ignored",
    ];

    /// <summary>Every warning a boot on the profile writes before its terrain line, in order, with the
    /// profile's path written <c>&lt;profile&gt;</c>.</summary>
    private static List<string> ExpectedBootWarnings()
    {
        var lines = new List<string>();
        foreach (string table in AbsentTables)
        {
            lines.Add($"{table}: file not found (<profile>/{table}) — the table is EMPTY");
            if (table == "Spells.csv") lines.AddRange(LadderWarnings);
        }
        foreach (string table in AbsentTables)
            lines.Add($"content: {table}: FILE NOT FOUND (<profile>/{table}) — nothing loaded" +
                      (AbsentConsequences.TryGetValue(table, out var consequence) ? "; " + consequence : ""));
        lines.Add("content: no maps and/or no mobs — run re/build_map_index.py and check game-data/mobs.csv");
        return lines;
    }

    [Fact]
    public async Task The_game_server_boots_on_the_synthetic_profile_and_stops_cleanly()
    {
        Assert.Equal(63, AbsentTables.Length);
        Assert.Equal(48, LadderWarnings.Length);

        var manifest = SyntheticManifest.Read();
        string profile = manifest.Value("P1998_GAME_DATA");
        Assert.Equal(manifest.ProfileDir, profile);

        SweepStaleBootFolders(Path.GetTempPath(), DateTime.UtcNow);

        BootRun run;
        int attempts = 0;
        do run = await BootRun.BootAndStopAsync(manifest, FreePortPair());
        while (run.LostItsPorts && ++attempts < 3);

        string transcript = $"server.log:\n{string.Join("\n", run.Log)}\nconsole:\n{run.Console}";
        Assert.False(run.LostItsPorts, "another process took the picked ports three times running:\n" + transcript);
        Assert.True(run.Ready, $"the server did not report listening on 127.0.0.1:{run.Port} and :{run.Port + 1} " +
                               $"within {BootBound.TotalSeconds:0}s:\n" + transcript);
        Assert.True(run.StoppedOnItsOwn,
                    $"the server did not exit on its restart trigger within {StopBound.TotalSeconds:0}s and was " +
                    "killed:\n" + transcript);

        var messages = Messages(run.Log, profile);
        Assert.True(messages.Count > 0, "server.log is empty:\n" + transcript);

        // No error line, and no FATAL anywhere (the unhandled-exception handler writes "FATAL ...").
        var errors = messages.Where(m => m.StartsWith("!!! ", StringComparison.Ordinal)).ToList();
        Assert.True(errors.Count == 0, "the boot logged errors:\n" + string.Join("\n", errors));
        Assert.DoesNotContain("FATAL", string.Join("\n", run.Log), StringComparison.Ordinal);
        Assert.DoesNotContain("FATAL", run.Console, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", run.Console, StringComparison.OrdinalIgnoreCase);

        // The terrain line: both maps found, and nowhere searched but the profile's own maps folder.
        int terrain = messages.FindIndex(m => m.StartsWith("=== terrain: ", StringComparison.Ordinal));
        Assert.True(terrain >= 0, "no startup terrain line:\n" + transcript);
        var terrainLine = Regex.Match(messages[terrain], @"^=== terrain: (\d+)/(\d+) map file\(s\) found; searched: (.*)$");
        Assert.True(terrainLine.Success, $"unexpected terrain line: {messages[terrain]}");
        Assert.Equal("2/2", $"{terrainLine.Groups[1].Value}/{terrainLine.Groups[2].Value}");
        Assert.All(terrainLine.Groups[3].Value.Split(" | "), dir => Assert.Equal("<profile>/maps", dir));

        // Every warning up to the terrain line is on the list, in order, and nothing on the list is missing.
        var warnings = messages.Take(terrain)
            .Where(m => m.StartsWith("!! ", StringComparison.Ordinal))
            .Select(m => m["!! ".Length..])
            .ToList();
        var expected = ExpectedBootWarnings();
        var unexpected = warnings.Except(expected, StringComparer.Ordinal).ToList();
        var missing = expected.Except(warnings, StringComparer.Ordinal).ToList();
        Assert.True(unexpected.Count == 0 && missing.Count == 0,
                    $"the boot's warnings are not the expected {expected.Count}:\n" +
                    $"not on the list ({unexpected.Count}):\n  " + string.Join("\n  ", unexpected) + "\n" +
                    $"on the list but not logged ({missing.Count}):\n  " + string.Join("\n  ", missing));
        Assert.Equal(expected, warnings);

        // It served, then stopped through the restart trigger's graceful flush, and exited 0.
        Assert.Contains(messages, m => m == $"listening on 127.0.0.1:{run.Port}");
        Assert.Contains(messages, m => m == $"listening on 127.0.0.1:{run.Port + 1}");
        Assert.Contains(messages, m => m.StartsWith("=== restart deadline reached", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("=== shutdown signal (ProcessExit)", StringComparison.Ordinal));
        Assert.Contains(messages, m => m == "   -> saved 0 player(s)");
        Assert.Equal(0, run.ExitCode);
    }

    /// <summary>The sweep deletes a boot folder nothing has been written to for <see cref="StaleAfter"/>, and
    /// leaves alone a fresh one, an old one with something written inside it lately (a boot still running), and a
    /// folder whose name is not a boot folder's. Run on a folder of its own, not the real temp directory.</summary>
    [Fact]
    public void The_sweep_deletes_only_boot_folders_nothing_has_written_to_lately()
    {
        string root = Path.Combine(Path.GetTempPath(), $"p1998-sweep-probe-{Guid.NewGuid():N}");
        var now = DateTime.UtcNow;
        try
        {
            string Folder(string name, TimeSpan age, TimeSpan? statusAge = null)
            {
                string dir = Path.Combine(root, name);
                Directory.CreateDirectory(Path.Combine(dir, "logs"));
                Directory.CreateDirectory(Path.Combine(dir, "run"));
                File.WriteAllText(Path.Combine(dir, "logs", "server.log"), "log");
                File.WriteAllText(Path.Combine(dir, "run", "status.json"), "{}");
                // Files first, then folders: a folder's own write time moves when an entry is added inside it.
                foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    File.SetLastWriteTimeUtc(file, now - age);
                if (statusAge is { } recent) File.SetLastWriteTimeUtc(Path.Combine(dir, "run", "status.json"), now - recent);
                foreach (string sub in Directory.EnumerateDirectories(dir)) Directory.SetLastWriteTimeUtc(sub, now - age);
                Directory.SetLastWriteTimeUtc(dir, now - age);
                return dir;
            }

            string stale = Folder(BootFolderPrefix + new string('a', 32), TimeSpan.FromMinutes(20));
            string fresh = Folder(BootFolderPrefix + new string('b', 32), TimeSpan.FromMinutes(1));
            string running = Folder(BootFolderPrefix + new string('c', 32), TimeSpan.FromMinutes(20),
                                    statusAge: TimeSpan.FromSeconds(10));
            string foreign = Folder(BootFolderPrefix + "notes", TimeSpan.FromMinutes(20));

            int deleted = SweepStaleBootFolders(root, now);
            Assert.False(Directory.Exists(stale), "a boot folder untouched for 20 minutes was kept");
            Assert.True(Directory.Exists(fresh), "a boot folder written a minute ago was deleted");
            Assert.True(Directory.Exists(running), "a boot folder whose status file was written 10 s ago was deleted");
            Assert.True(Directory.Exists(foreign), "a folder whose name is not a boot folder's was deleted");
            Assert.Equal(1, deleted);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Delete every boot folder under <paramref name="tempRoot"/> that nothing has been written to for
    /// <see cref="StaleAfter"/>: the newest write time of the folder and of everything in it is what counts. Only
    /// names that are a boot folder's are considered. Best effort: a folder that cannot be read or deleted (a file
    /// still open, on Windows) is left for a later run. Returns how many were deleted.</summary>
    private static int SweepStaleBootFolders(string tempRoot, DateTime nowUtc)
    {
        int deleted = 0;
        foreach (string dir in Directory.EnumerateDirectories(tempRoot, BootFolderPrefix + "*"))
        {
            if (!BootFolderName.IsMatch(Path.GetFileName(dir))) continue;
            try
            {
                var folder = new DirectoryInfo(dir);
                DateTime lastWrite = folder.EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
                    .Select(entry => entry.LastWriteTimeUtc)
                    .Append(folder.LastWriteTimeUtc)
                    .Max();
                if (nowUtc - lastWrite < StaleAfter) continue;
                folder.Delete(recursive: true);
                deleted++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Gone already (another run swept it) or still held open: either way, not this run's to force.
            }
        }
        return deleted;
    }

    /// <summary>Each log record's text without its <c>[HH:mm:ss.fff] </c> stamp, continuation lines (an
    /// exception's detail) joined to the record they belong to, the profile's path written
    /// <c>&lt;profile&gt;</c> and every backslash a slash.</summary>
    private static List<string> Messages(IEnumerable<string> log, string profile)
    {
        var stamp = new Regex(@"^\[\d\d:\d\d:\d\d\.\d{3}\] ");
        var messages = new List<string>();
        foreach (string line in log)
        {
            var m = stamp.Match(line);
            if (m.Success) messages.Add(line[m.Length..]);
            else if (messages.Count > 0) messages[^1] += "\n" + line;
            else messages.Add(line);
        }
        return messages.Select(m => m.Replace(profile, "<profile>", StringComparison.Ordinal).Replace('\\', '/')).ToList();
    }

    /// <summary>A port P with P+1 (the 4.95 and 5.33 game ports must be consecutive), drawn from
    /// <see cref="PortsFrom"/>-<see cref="PortsTo"/>, both at least 20 from a multiple of 1000, and both free on
    /// 0.0.0.0 and on loopback: a listener on 0.0.0.0 would otherwise share loopback traffic with the test's
    /// server.</summary>
    private static int FreePortPair()
    {
        static bool OffRoundThousands(int p) => p % 1000 is >= 20 and <= 979;
        static bool Free(int p)
        {
            foreach (var address in new[] { IPAddress.Any, IPAddress.Loopback })
            {
                var probe = new TcpListener(address, p);
                try { probe.Start(); }
                catch (SocketException) { return false; }   // taken, or in a range the OS has reserved
                finally { probe.Stop(); }
            }
            return true;
        }

        for (int attempt = 0; attempt < 64; attempt++)
        {
            int port = Random.Shared.Next(PortsFrom, PortsTo);
            if (OffRoundThousands(port) && OffRoundThousands(port + 1) && Free(port) && Free(port + 1)) return port;
        }
        throw new InvalidOperationException($"no two consecutive free ports in {PortsFrom}-{PortsTo} in 64 draws");
    }

    /// <summary>One boot of the server as a child process: started, waited on until it listens, asked to stop,
    /// waited on until it exits, and killed if any of that does not happen. Its temp folders are deleted.</summary>
    private sealed record BootRun(int Port, bool Ready, bool StoppedOnItsOwn, int ExitCode, bool LostItsPorts,
                                  IReadOnlyList<string> Log, string Console)
    {
        public static async Task<BootRun> BootAndStopAsync(SyntheticManifest manifest, int port)
        {
            string temp = Path.Combine(Path.GetTempPath(), $"{BootFolderPrefix}{Guid.NewGuid():N}");
            string state = Path.Combine(temp, "state"), logs = Path.Combine(temp, "logs"), run = Path.Combine(temp, "run");
            foreach (string dir in new[] { state, logs, run }) Directory.CreateDirectory(dir);

            var start = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = temp,
            };
            start.ArgumentList.Add(typeof(Server.Session).Assembly.Location);
            start.ArgumentList.Add("--ports");
            start.ArgumentList.Add($"{port},{port + 1}");
            foreach (string key in start.Environment.Keys
                         .Where(k => k.StartsWith("P1998_", StringComparison.OrdinalIgnoreCase)).ToList())
                start.Environment.Remove(key);
            foreach (var (name, value) in manifest.ResolvedEnvironment()) start.Environment[name] = value;
            start.Environment["P1998_STATE"] = state;
            start.Environment["P1998_LOGS"] = logs;
            start.Environment["P1998_RUN"] = run;
            start.Environment["P1998_BIND"] = "127.0.0.1";

            var console = new ConcurrentQueue<string>();
            var listening = new ConcurrentDictionary<int, bool>();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var portLost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int staleTriggers = 0;
            void Seen(string? line)
            {
                if (line is null) return;
                console.Enqueue(line);
                foreach (int p in new[] { port, port + 1 })
                    if (line.EndsWith($"listening on 127.0.0.1:{p}", StringComparison.Ordinal)) listening[p] = true;
                if (listening.Count == 2) ready.TrySetResult();
                // A failed bind does not end the process: the other port's accept loop keeps it running.
                if (line.Contains("could not listen on", StringComparison.Ordinal)) portLost.TrySetResult();
                if (line.Contains("restart_at: deadline is", StringComparison.Ordinal)) Interlocked.Increment(ref staleTriggers);
            }

            using var process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, e) => Seen(e.Data);
            process.ErrorDataReceived += (_, e) => Seen(e.Data);
            bool started = false, isReady = false, stoppedOnItsOwn = false;
            string[] log = Array.Empty<string>();
            try
            {
                WriteRestartTrigger(run, Leash);
                started = process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                var exited = process.WaitForExitAsync();

                isReady = await Task.WhenAny(ready.Task, portLost.Task, exited, Task.Delay(BootBound)) == ready.Task;
                if (isReady)
                {
                    var stopBy = DateTime.UtcNow + StopBound;
                    int staleSeen = 0;
                    WriteRestartTrigger(run, TriggerLead);
                    while (!exited.IsCompleted && DateTime.UtcNow < stopBy)
                    {
                        await Task.WhenAny(exited, Task.Delay(250));
                        // A poll that came late finds the deadline already past and refuses it as stale; book again.
                        int stale = Volatile.Read(ref staleTriggers);
                        if (stale > staleSeen && staleSeen < 3)
                        {
                            staleSeen = stale;
                            WriteRestartTrigger(run, TriggerLead);
                        }
                    }
                    stoppedOnItsOwn = exited.IsCompleted;
                }
            }
            finally
            {
                if (started && !process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { /* it exited between the check and the kill */ }
                }
                if (started) process.WaitForExit();
                string serverLog = Path.Combine(logs, "server.log");
                log = File.Exists(serverLog) ? File.ReadAllLines(serverLog) : Array.Empty<string>();
                DeleteBestEffort(temp);
            }

            return new BootRun(port, isReady, stoppedOnItsOwn, process.ExitCode, portLost.Task.IsCompleted, log,
                               string.Join("\n", console));
        }

        /// <summary>Book the restart <paramref name="lead"/> from now, replacing any earlier booking. Written
        /// beside the trigger and moved into place, so the server's poll never reads a half-written file.</summary>
        private static void WriteRestartTrigger(string runDir, TimeSpan lead)
        {
            long deadline = DateTimeOffset.UtcNow.Add(lead).ToUnixTimeMilliseconds();
            string staging = Path.Combine(runDir, "restart_at.staging");
            File.WriteAllText(staging, $"{deadline}|synthetic profile boot test");
            File.Move(staging, Path.Combine(runDir, "restart_at"), overwrite: true);
        }

        private static void DeleteBestEffort(string dir)
        {
            for (int attempt = 0; attempt < 5 && Directory.Exists(dir); attempt++)
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (IOException) { Thread.Sleep(200); }
                catch (UnauthorizedAccessException) { Thread.Sleep(200); }
            }
        }
    }
}
