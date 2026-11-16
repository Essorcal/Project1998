using System.Diagnostics;
using System.Text.RegularExpressions;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The server tests' scratch (<see cref="TestScratch"/>): a run deletes its folder at its end; the sweep deletes the
/// folders of runs that have ended and keeps a live run's, whether the marker's process or the lock says so; a link
/// inside an ended run's folder goes as a link and its target keeps everything; the sweep touches nothing outside its
/// root and no folder without a run's marker; the keep switch keeps a run's folder for a day; xunit runs this assembly
/// through the framework that ends the run; and no test makes scratch outside its run's folder.
///
/// <para>Every fact plants its runs under a probe folder of its own inside this run's folder, never in the real root,
/// except the one that checks this run's own folder survives a sweep of the real root.</para>
/// </summary>
public class TestScratchTests
{
    /// <summary>A process id no process can have: Windows' are multiples of four, Linux's stay under 2^22.</summary>
    private const int NoSuchProcess = int.MaxValue;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <summary>A run's folder goes at its end, everything in it, a file this process still held open until the end's
    /// release let go of it included; the root and the rest of it stay.</summary>
    [Fact]
    public void A_run_deletes_its_folder_at_its_end()
    {
        string root = NewArea();
        string beside = Path.Combine(root, "beside.txt");
        File.WriteAllText(beside, "not the run's");
        var run = TestScratch.Start(root, keep: false, inheritable: false);
        Directory.CreateDirectory(Path.Combine(run.Folder, "state", "isolated-1"));
        File.WriteAllText(Path.Combine(run.Folder, "state", "isolated-1", "project1998.db"), "db");
        Directory.CreateDirectory(Path.Combine(run.Folder, "p1998-log-warn-1"));
        string log = Path.Combine(run.Folder, "p1998-log-warn-1", "server.log");
        var held = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.Read);   // as the log's writer holds its file
        try
        {
            Assert.Equal(TestScratch.RunState.Live, TestScratch.StateOf(run.Folder, DateTime.UtcNow));

            Assert.True(run.End(Patience, release: held.Dispose), $"the run's folder is still there: {Listing(run.Folder)}");
            Assert.False(Directory.Exists(run.Folder));
            Assert.True(File.Exists(beside), "the run's end deleted something outside its own folder");
        }
        finally
        {
            held.Dispose();
            run.ReleaseLock();
        }
    }

    /// <summary>The sweep deletes the folder of a run whose process is gone, and of one whose process id now names a
    /// later process (a start time that is not the marker's), when nothing holds their locks.</summary>
    [Fact]
    public void The_sweep_deletes_the_folders_of_runs_that_have_ended()
    {
        string root = NewArea();
        var now = DateTime.UtcNow;
        string gone = Plant(root, "run-gone0001", NoSuchProcess, now.AddHours(-1));
        string reused = Plant(root, "run-reused01", Environment.ProcessId, ThisProcessStarted().AddHours(-1));
        Assert.Equal(TestScratch.RunState.Ended, TestScratch.StateOf(gone, now));
        Assert.Equal(TestScratch.RunState.Ended, TestScratch.StateOf(reused, now));

        var result = TestScratch.Sweep(root, now);

        Assert.False(Directory.Exists(gone), $"an ended run's folder is still there: {Listing(gone)}");
        Assert.False(Directory.Exists(reused), $"a run whose id was reused is still there: {Listing(reused)}");
        Assert.Equal(new[] { "run-gone0001", "run-reused01" }, result.Removed.Order(StringComparer.Ordinal));
        Assert.Empty(result.Left);
    }

    /// <summary>The sweep keeps a live run's folder: a run this process started (its lock held), a marker naming this
    /// process with nothing holding its lock, and a marker naming another live process with nothing holding its lock.
    /// And this run's own folder survives a sweep of the real root, its marker and lock in place.</summary>
    [Fact]
    public void The_sweep_keeps_the_folder_of_a_run_that_is_still_going()
    {
        string root = NewArea();
        var now = DateTime.UtcNow;
        var own = TestScratch.Start(root, keep: false, inheritable: false);
        using var other = Child.Start();
        try
        {
            string mine = Plant(root, "run-mine0001", Environment.ProcessId, ThisProcessStarted());
            string theirs = Plant(root, "run-other001", other.Process.Id, other.Process.StartTime.ToUniversalTime());
            var before = new[] { own.Folder, mine, theirs }.ToDictionary(f => f, Listing);

            var result = TestScratch.Sweep(root, now);

            foreach (var (folder, listing) in before)
                Assert.True(Directory.Exists(folder) && Listing(folder) == listing,
                            $"a live run's folder was touched: {folder}\nbefore: {listing}\nafter: {Listing(folder)}");
            Assert.Empty(result.Removed);

            TestScratch.Sweep(TestScratch.DefaultRoot, DateTime.UtcNow);
            Assert.True(File.Exists(Path.Combine(TestProcessState.ScratchDirectory, TestScratch.MarkerName)),
                        "this run's marker went in a sweep of the real root");
            Assert.True(File.Exists(Path.Combine(TestProcessState.ScratchDirectory, TestScratch.LockName)),
                        "this run's lock went in a sweep of the real root");
            Assert.True(Directory.Exists(TestProcessState.StateDirectory), "this run's state directory went in a sweep");
        }
        finally
        {
            other.Stop();
            own.ReleaseLock();
        }
    }

    /// <summary>A held lock keeps a run's folder although its marker's process is gone, and once it is let go the
    /// sweep deletes the folder.</summary>
    [Fact]
    public void A_held_lock_keeps_a_runs_folder_after_its_process_has_gone()
    {
        string root = NewArea();
        var now = DateTime.UtcNow;
        string folder = Plant(root, "run-held0001", NoSuchProcess, now.AddHours(-1));
        using (new FileStream(Path.Combine(folder, TestScratch.LockName), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(TestScratch.RunState.Live, TestScratch.StateOf(folder, now));
            TestScratch.Sweep(root, now);
            Assert.True(File.Exists(Path.Combine(folder, "state", "project1998.db")), "a folder whose lock is held was swept");
        }

        TestScratch.Sweep(root, now);
        Assert.False(Directory.Exists(folder), "the folder stayed after its lock was let go");
    }

    /// <summary>A process the run started keeps the run's folder after the run's own process is gone: it inherited the
    /// run's lock. Played here by a run this process starts, a child started after it, and the run's marker then
    /// pointed at no process and its handle on the lock let go of as its process's death would
    /// (<see cref="TestScratch.AbandonLock"/>: on Linux a dispose would unlock for the child too, and a death does not).
    /// <para>Once the child has stopped, the fact waits for the folder to read as ended rather than reading it once: a
    /// process another test started while this run's lock was open (the synthetic boot's server) inherited it too,
    /// and holds it until it stops.</para></summary>
    [Fact]
    public void A_process_the_run_started_keeps_its_folder_after_the_run_is_gone()
    {
        string root = NewArea();
        var now = DateTime.UtcNow;
        var run = TestScratch.Start(root, keep: false);
        var child = Child.Start();
        try
        {
            File.WriteAllText(Path.Combine(run.Folder, TestScratch.MarkerName),
                              TestScratch.FormatMarker(new TestScratch.Marker(NoSuchProcess, now.AddHours(-1), Keep: false)));
            run.AbandonLock();

            Assert.Equal(TestScratch.RunState.Live, TestScratch.StateOf(run.Folder, now));
            TestScratch.Sweep(root, now);
            Assert.True(Directory.Exists(run.Folder), "the folder went while a process its run started was still running");

            child.Stop();
            var waited = Stopwatch.StartNew();
            while (TestScratch.StateOf(run.Folder, now) != TestScratch.RunState.Ended && waited.Elapsed < TimeSpan.FromMinutes(3))
                Thread.Sleep(250);
            Assert.Equal(TestScratch.RunState.Ended, TestScratch.StateOf(run.Folder, now));
            TestScratch.Sweep(root, now);
            Assert.False(Directory.Exists(run.Folder), "the folder stayed after the run's last process had gone");
        }
        finally
        {
            child.Dispose();
            run.ReleaseLock();
        }
    }

    /// <summary>A link inside an ended run's folder, at its top and deeper down, is removed as a link: the folder goes,
    /// and the folder the links pointed at, outside it, keeps every file and folder in it. A junction on Windows (what
    /// a user can make there without a privilege), a symbolic link elsewhere.</summary>
    [Fact]
    public void A_link_inside_an_ended_runs_folder_goes_as_a_link_and_its_target_keeps_everything()
    {
        string root = NewArea();
        string target = Path.Combine(NewArea(), "target");
        Directory.CreateDirectory(Path.Combine(target, "sub"));
        File.WriteAllText(Path.Combine(target, "keep.txt"), "target");
        File.WriteAllText(Path.Combine(target, "sub", "keep.txt"), "target");
        string targetBefore = Listing(target);
        var now = DateTime.UtcNow;
        string ended = Plant(root, "run-links001", NoSuchProcess, now.AddHours(-1));
        MakeLink(Path.Combine(ended, "linked"), target);
        MakeLink(Path.Combine(ended, "state", "linked-deeper"), target);

        var result = TestScratch.Sweep(root, now);

        Assert.False(Directory.Exists(ended), $"the ended run's folder is still there: {Listing(ended)}");
        Assert.Equal(new[] { "run-links001" }, result.Removed);
        Assert.Equal(targetBefore, Listing(target));
    }

    /// <summary>The sweep touches nothing outside its root: not an ended run's folder beside the root, not an ended
    /// run's folder a link in the root points at (the link is not a run folder, so it stays too), and nothing at all
    /// when the root is itself a link.</summary>
    [Fact]
    public void The_sweep_touches_nothing_outside_its_root()
    {
        string area = NewArea();
        var now = DateTime.UtcNow;
        string root = Path.Combine(area, "root");
        Directory.CreateDirectory(root);
        string beside = Plant(area, "run-beside01", NoSuchProcess, now.AddHours(-1));
        string elsewhere = Path.Combine(area, "elsewhere");
        string behind = Plant(elsewhere, "run-behind01", NoSuchProcess, now.AddHours(-1));
        string link = Path.Combine(root, "run-linked01");
        MakeLink(link, behind);
        string linkedRoot = Path.Combine(area, "linked-root");
        MakeLink(linkedRoot, elsewhere);
        var before = new[] { beside, behind }.ToDictionary(f => f, Listing);

        var swept = TestScratch.Sweep(root, now);
        var throughLink = TestScratch.Sweep(linkedRoot, now);

        foreach (var (folder, listing) in before)
            Assert.True(Directory.Exists(folder) && Listing(folder) == listing,
                        $"an ended run's folder outside the root was touched: {folder}\nbefore: {listing}\nafter: {Listing(folder)}");
        Assert.True(new DirectoryInfo(link).Exists && TestScratch.IsLink(new DirectoryInfo(link)), "the link in the root went");
        Assert.Empty(swept.Removed);
        Assert.Empty(throughLink.Removed);
        Assert.NotNull(throughLink.Skipped);
    }

    /// <summary>The sweep leaves every entry without a run's well-formed marker: a folder laid out as the old
    /// <c>project1998-tests-*</c> state directories were, a run's folder whose marker is cut short, one whose marker
    /// is from another format, one with a lock and no marker yet (a run starting up), and a file.</summary>
    [Fact]
    public void The_sweep_leaves_every_folder_without_a_runs_marker()
    {
        string root = NewArea();
        var now = DateTime.UtcNow;
        string legacy = Path.Combine(root, "project1998-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(legacy, "isolated-1"));
        File.WriteAllText(Path.Combine(legacy, "project1998.db"), "db");
        File.WriteAllText(Path.Combine(legacy, "isolated-1", "project1998.db"), "db");
        string cut = Plant(root, "run-cut00001", NoSuchProcess, now.AddHours(-1));
        string marker = TestScratch.FormatMarker(new TestScratch.Marker(NoSuchProcess, now.AddHours(-1), Keep: false));
        File.WriteAllText(Path.Combine(cut, TestScratch.MarkerName), marker[..^"keep 0\n".Length]);
        string other = Plant(root, "run-other001", NoSuchProcess, now.AddHours(-1));
        File.WriteAllText(Path.Combine(other, TestScratch.MarkerName), marker.Replace(" 1\n", " 2\n", StringComparison.Ordinal));
        string starting = Path.Combine(root, "run-start001");
        Directory.CreateDirectory(starting);
        File.WriteAllText(Path.Combine(starting, TestScratch.LockName), "");
        string file = Path.Combine(root, "run-file0001");
        File.WriteAllText(file, "a file");
        var before = new[] { legacy, cut, other, starting }.ToDictionary(f => f, Listing);

        var result = TestScratch.Sweep(root, now);

        foreach (var (folder, listing) in before)
            Assert.True(Directory.Exists(folder) && Listing(folder) == listing,
                        $"a folder without a run's marker was touched: {folder}\nbefore: {listing}\nafter: {Listing(folder)}");
        Assert.True(File.Exists(file));
        Assert.Empty(result.Removed);
    }

    /// <summary>The keep switch: exactly <c>1</c> turns it on; a run started with it keeps its folder at its end, with
    /// the switch in its marker; and once such a run has ended, the sweep leaves its folder for a day from the run's
    /// start, and deletes it after.</summary>
    [Fact]
    public void The_keep_switch_keeps_a_runs_folder_for_a_day()
    {
        Assert.True(TestScratch.KeepRequested("1"));
        foreach (string? off in new[] { null, "", "0", "true", "yes", " 1", "1 " })
            Assert.False(TestScratch.KeepRequested(off), $"{TestScratch.KeepVariable}={off ?? "(unset)"} kept the folder");

        string root = NewArea();
        var now = DateTime.UtcNow;
        var kept = TestScratch.Start(root, keep: true, inheritable: false);
        try
        {
            Assert.False(kept.End(TimeSpan.Zero));
            Assert.True(Directory.Exists(kept.Folder), "a kept run deleted its folder at its end");
            Assert.True(TestScratch.TryReadMarker(kept.Folder, out var written) && written.Keep, "the kept run's marker does not say so");

            string young = Plant(root, "run-young001", NoSuchProcess, now.AddHours(-23), keep: true);
            string old = Plant(root, "run-old00001", NoSuchProcess, now.AddHours(-25), keep: true);
            Assert.Equal(TestScratch.RunState.Kept, TestScratch.StateOf(young, now));

            var result = TestScratch.Sweep(root, now);

            Assert.True(Directory.Exists(young), "a kept run's folder went within a day of its start");
            Assert.False(Directory.Exists(old), "a kept run's folder stayed more than a day after its start");
            Assert.Equal(new[] { "run-old00001" }, result.Removed);
        }
        finally
        {
            kept.ReleaseLock();
        }
    }

    /// <summary>xunit runs this assembly through <see cref="ScratchTestFramework"/>, so the run's end runs. xunit falls
    /// back to its own framework without a word when the attribute names a type it cannot find.</summary>
    [Fact]
    public void The_assembly_runs_through_the_framework_that_ends_the_run()
    {
        Assert.True(ScratchTestFramework.RunnerInUse,
                    "xunit did not run this assembly through ScratchTestFramework, so nothing deletes the run's folder at " +
                    "its end: check the [assembly: TestFramework] attribute in Tests/Support/ScratchTestFramework.cs");
    }

    /// <summary>No test reads the temp directory itself: everything a test writes goes in its run's folder
    /// (<see cref="TestProcessState.ScratchDirectory"/>), and <see cref="TestScratch"/> alone knows where the root is.
    /// A folder made straight in the temp directory is one nothing deletes if its test is cut short.</summary>
    [Fact]
    public void No_test_makes_scratch_outside_its_runs_folder()
    {
        string tests = Path.Combine(RepoPaths.Root(), "Tests");
        var call = new Regex(@"\b(?:GetTempPath|GetTempFileName|CreateTempSubdirectory)\s*\(", RegexOptions.CultureInvariant);
        var found = Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(tests, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith("bin/", StringComparison.Ordinal) && !f.StartsWith("obj/", StringComparison.Ordinal)
                        && f != "Support/TestScratch.cs")
            .SelectMany(f => File.ReadLines(Path.Combine(tests, f)).Select((line, i) => (File: f, Line: i + 1, Text: line)))
            .Where(l => call.IsMatch(l.Text))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToList();
        Assert.True(found.Count == 0,
                    "these read the temp directory; put the file or folder in TestProcessState.ScratchDirectory instead:\n" +
                    string.Join("\n", found));
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    /// <summary>A new folder of this fact's own, inside this run's folder.</summary>
    private static string NewArea()
    {
        string area = Path.Combine(TestProcessState.ScratchDirectory, "scratch-fact-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(area);
        return area;
    }

    /// <summary>A run folder as a run leaves one under <paramref name="root"/>: its lock (held by nothing), its marker,
    /// and a state directory with a database in it and in an isolated folder.</summary>
    private static string Plant(string root, string name, int processId, DateTime startedUtc, bool keep = false)
    {
        string folder = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(folder, "state", "isolated-1"));
        File.WriteAllText(Path.Combine(folder, "state", "project1998.db"), "db");
        File.WriteAllText(Path.Combine(folder, "state", "isolated-1", "project1998.db"), "db");
        File.WriteAllText(Path.Combine(folder, TestScratch.LockName), "");
        File.WriteAllText(Path.Combine(folder, TestScratch.MarkerName),
                          TestScratch.FormatMarker(new TestScratch.Marker(processId, startedUtc, keep)));
        return folder;
    }

    private static DateTime ThisProcessStarted()
    {
        using var me = Process.GetCurrentProcess();
        return me.StartTime.ToUniversalTime();
    }

    /// <summary>Every file and folder under <paramref name="folder"/>, with each file's length (a held lock is not
    /// opened); a link is listed as one and not entered.</summary>
    private static string Listing(string folder)
    {
        if (!Directory.Exists(folder)) return "(gone)";
        var lines = new List<string>();
        void Walk(DirectoryInfo dir, string prefix)
        {
            foreach (var entry in dir.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                string rel = prefix + entry.Name;
                if (TestScratch.IsLink(entry)) lines.Add(rel + " -> link");
                else if (entry is DirectoryInfo sub) { lines.Add(rel + "/"); Walk(sub, rel + "/"); }
                else lines.Add(rel + " " + ((FileInfo)entry).Length);
            }
        }
        Walk(new DirectoryInfo(folder), "");
        return string.Join("; ", lines);
    }

    /// <summary>A link at <paramref name="link"/> to the folder <paramref name="target"/>: a junction on Windows, made by
    /// <c>mklink /J</c> since .NET has no call for one, and a symbolic link elsewhere.</summary>
    private static void MakeLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(arg);
            using var mklink = Process.Start(start)!;
            string output = mklink.StandardOutput.ReadToEnd() + mklink.StandardError.ReadToEnd();
            mklink.WaitForExit();
            Assert.True(mklink.ExitCode == 0, $"mklink /J failed ({mklink.ExitCode}): {output}");
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
        Assert.True(TestScratch.IsLink(new DirectoryInfo(link)), $"{link} is not a link");
    }

    /// <summary>A process that runs until its input closes: <c>cmd</c> on Windows, <c>cat</c> elsewhere. Started with
    /// .NET's own <see cref="Process.Start(ProcessStartInfo)"/>, as the synthetic boot starts its server.</summary>
    private sealed class Child : IDisposable
    {
        public Process Process { get; }

        private Child(Process process) => Process = process;

        public static Child Start()
        {
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "cat")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (OperatingSystem.IsWindows())
                foreach (string arg in new[] { "/d", "/q" }) start.ArgumentList.Add(arg);
            var process = Process.Start(start)!;
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return new Child(process);
        }

        /// <summary>Close its input and wait for it to exit; kill it if it does not within ten seconds.</summary>
        public void Stop()
        {
            try { Process.StandardInput.Close(); }
            catch (Exception e) when (e is IOException or InvalidOperationException) { /* already gone */ }
            if (!Process.WaitForExit(10_000))
            {
                Process.Kill(entireProcessTree: true);
                Process.WaitForExit();
            }
        }

        public void Dispose()
        {
            if (!Process.HasExited) Stop();
            Process.Dispose();
        }
    }
}
