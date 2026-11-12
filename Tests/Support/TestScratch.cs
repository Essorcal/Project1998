using System.Diagnostics;
using System.Globalization;

namespace Tests.Support;

/// <summary>
/// The server tests' scratch on disk: one root, <c>%TEMP%\p1998-server-tests</c>, and in it one folder per test
/// process, a <b>run</b>. Every file or folder a test makes for itself goes in its run's folder
/// (<see cref="TestProcessState.ScratchDirectory"/>), and so does the state directory <c>P1998_STATE</c> points at. The
/// run deletes its folder at its end (<see cref="End"/>, from <see cref="ScratchTestFramework"/>), and each run's start
/// sweeps the folders of runs that have ended (<see cref="Sweep"/>).
///
/// <para><b>A run folder</b> (<c>r</c> and 8 hex digits: the root's and the run's names are short because
/// <c>ReLoginAddressTests</c> binds Unix-domain sockets in the run's folder, and a socket's path is at most 107 bytes,
/// temp directory included) holds <c>run.lock</c>, which the run holds open with no
/// sharing from before its marker exists until its end, and <c>run.marker</c>: the run's process id, that process's
/// start time, and whether the keep switch was on. The lock is opened inheritable, so a process the run starts (the
/// synthetic boot's server) holds it too, and goes on holding it if the test host dies first.</para>
///
/// <para><b>Ended is decided from the marker and the lock, never from age.</b> A run has ended when no running process
/// has the marker's id with the marker's start time (there is none, or the id now names a later process) AND nothing
/// holds its lock (it opens with no sharing). Either sign of life keeps the folder, and so does any doubt: a process
/// whose start time cannot be read, a lock refused for any reason. Each sign covers a case the other misses: a dead
/// host's orphaned server is not the marker's process but holds the lock, and on Linux, where the lock is advisory, a
/// process that turned locking off (<c>DOTNET_SYSTEM_IO_DISABLEFILELOCKING</c>) is still seen by its id.</para>
///
/// <para><b>What the sweep may delete:</b> a folder directly under the root, not itself a link, with a well-formed
/// marker of a run that has ended; a kept run's (<see cref="KeepVariable"/>) only once the run started more than
/// <see cref="KeptFor"/> ago. Nothing else: not a folder without that marker (the layout before this one, a run that
/// has not written its marker yet), not anything outside the root, and nothing at all when the root is itself a
/// link.</para>
///
/// <para><b>How a run folder is deleted</b> (<see cref="DeleteRun"/>): entry by entry, never following a link. A
/// junction or a symbolic link is recognised by its own attributes before anything is opened, and removed as itself,
/// so its target keeps everything in it. (.NET's own <c>Directory.Delete(path, true)</c> does not enter a junction
/// either, but on Windows it cannot delete a tree that holds one: it asks <c>DeleteVolumeMountPoint</c> to unmount
/// the junction first, which refuses, and the delete throws <c>UnauthorizedAccessException</c> with the folders above
/// the junction still there. Measured on .NET 8.0.31, for a junction at the top of the tree and one a level down.)
/// The marker goes last: when anything is refused, the lock and the marker stay, and a later sweep
/// finishes the folder once whatever held it has gone. On Windows every path is extended-length (<c>\\?\</c>), so a
/// name ending in a dot or a space is deleted as itself, not as the name Win32 would trim it to.</para>
/// </summary>
internal sealed class TestScratch
{
    /// <summary>The root's name, under the temp directory.</summary>
    public const string RootName = "p1998-server-tests";

    public const string LockName = "run.lock";
    public const string MarkerName = "run.marker";

    /// <summary>The keep switch, named as the test client names its own: exactly <c>1</c> keeps the run's folder.</summary>
    public const string KeepVariable = "P1998_KEEP_TEST_TEMP";

    /// <summary>The marker's first line, which also versions its format.</summary>
    internal const string MarkerHeader = "project1998-server-test-run 1";

    /// <summary>How long after its run started a kept folder is left by the sweep once the run has ended.</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(1);

    /// <summary>How far apart two reads of one process's start time may be and still name the same process. Windows
    /// reads the creation time the kernel stored, exactly; Linux works it out from the boot time, and two processes'
    /// reads can differ by a second. A misjudgement here only hands the decision to the lock.</summary>
    internal static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>One folder's own entries, hidden and system ones included; the walk recurses itself.</summary>
    private static readonly EnumerationOptions Flat = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>The root for this user: <c>%TEMP%\p1998-server-tests</c>.</summary>
    public static string DefaultRoot => Path.Combine(Path.GetTempPath(), RootName);

    /// <summary>Whether the keep switch is on: exactly <c>1</c>, as the test client reads it.</summary>
    public static bool KeepRequested(string? value) => value == "1";

    /// <summary>The run's folder.</summary>
    public string Folder { get; }

    /// <summary>The keep switch, as the run started with it.</summary>
    public bool Keep { get; }

    private readonly object _gate = new();
    private FileStream? _lock;

    private TestScratch(string folder, bool keep, FileStream held) => (Folder, Keep, _lock) = (folder, keep, held);

    /// <summary>A new run under <paramref name="root"/>, for this process: its folder, its lock held, then its marker.
    /// The marker is written beside and renamed into place, so a sweep reads a whole marker or none.
    /// <paramref name="inheritable"/> is false only for a fact's own probe run: every process started anywhere in the
    /// test host while the lock is open inherits it, and a probe run's lock must not be held by a server some other
    /// test started beside the fact.</summary>
    public static TestScratch Start(string root, bool keep, bool inheritable = true)
    {
        Directory.CreateDirectory(root);
        DateTime started;
        using (var me = Process.GetCurrentProcess()) started = me.StartTime.ToUniversalTime();
        for (int attempt = 1; ; attempt++)
        {
            string folder = Path.Combine(root, "r" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            string lockPath = Path.Combine(folder, LockName);
            FileStream held;
            try
            {
                held = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite,
                                      inheritable ? FileShare.None | FileShare.Inheritable : FileShare.None);
            }
            catch (IOException) when (attempt < 5 && File.Exists(lockPath))
            {
                continue;   // another run's name: that folder is left as it is
            }
            string pending = Path.Combine(folder, MarkerName + ".new");
            File.WriteAllText(pending, FormatMarker(new Marker(Environment.ProcessId, started, keep)));
            File.Move(pending, Path.Combine(folder, MarkerName));
            return new TestScratch(folder, keep, held);
        }
    }

    /// <summary>The run's end: unless the keep switch is on, this process's lock released and the folder deleted as
    /// the sweep deletes an ended run's (<see cref="DeleteRun"/>). Anything refused is tried again every 50 ms for up
    /// to <paramref name="patience"/>, with <paramref name="release"/> run before each retry to let go of what this
    /// process still holds. True when the folder is gone; false when kept or when something stayed, which the next
    /// run's sweep then finishes. Never throws.</summary>
    public bool End(TimeSpan patience, Action? release = null)
    {
        lock (_gate)
        {
            if (Keep) return false;
            ReleaseLock();
            var clock = Stopwatch.StartNew();
            while (true)
            {
                if (DeleteRun(Folder)) return true;
                if (clock.Elapsed >= patience) return false;
                try { release?.Invoke(); }
                catch { /* a release that fails leaves the next attempt where this one was */ }
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>Close this process's handle on the run's lock, as the process's exit would. A process the run started
    /// keeps the handle it inherited.</summary>
    internal void ReleaseLock()
    {
        lock (_gate)
        {
            _lock?.Dispose();
            _lock = null;
        }
    }

    // ---- the marker ---------------------------------------------------------------------------------------------

    /// <summary>What a run folder's marker says.</summary>
    internal readonly record struct Marker(int ProcessId, DateTime StartedUtc, bool Keep);

    /// <summary>The marker's text: four lines, each ended.</summary>
    internal static string FormatMarker(Marker marker) => string.Create(CultureInfo.InvariantCulture,
        $"{MarkerHeader}\npid {marker.ProcessId}\nstarted {marker.StartedUtc.ToUniversalTime():O}\nkeep {(marker.Keep ? 1 : 0)}\n");

    /// <summary>Read <paramref name="text"/> as a marker: exactly <see cref="FormatMarker"/>'s four lines, or nothing.</summary>
    internal static bool TryParseMarker(string text, out Marker marker)
    {
        marker = default;
        string[] lines = text.Split('\n');
        if (lines.Length != 5 || lines[4].Length != 0 || lines[0] != MarkerHeader) return false;
        if (!lines[1].StartsWith("pid ", StringComparison.Ordinal)
            || !int.TryParse(lines[1].AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0)
            return false;
        if (!lines[2].StartsWith("started ", StringComparison.Ordinal)
            || !DateTime.TryParseExact(lines[2].AsSpan(8), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                                       out DateTime started)
            || started.Kind != DateTimeKind.Utc)
            return false;
        if (lines[3] is not ("keep 0" or "keep 1")) return false;
        marker = new Marker(pid, started, lines[3] == "keep 1");
        return true;
    }

    /// <summary>The marker of the run folder <paramref name="folder"/>, when it has a well-formed one that is a plain
    /// file (a link is never read through).</summary>
    internal static bool TryReadMarker(string folder, out Marker marker)
    {
        marker = default;
        try
        {
            var file = new FileInfo(Path.Combine(Long(folder), MarkerName));
            if (!file.Exists || IsLink(file) || file.Length > 1024) return false;
            return TryParseMarker(File.ReadAllText(file.FullName), out marker);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // ---- liveness -----------------------------------------------------------------------------------------------

    /// <summary>What the sweep makes of a folder under the root.</summary>
    internal enum RunState
    {
        /// <summary>No well-formed marker: not a run folder, or a run that has not written its marker yet.</summary>
        NotARun,
        /// <summary>Its process runs, its lock is held, or either cannot be told.</summary>
        Live,
        /// <summary>Ended, with the keep switch on, and started less than <see cref="KeptFor"/> ago.</summary>
        Kept,
        /// <summary>Ended: the sweep deletes it.</summary>
        Ended,
    }

    /// <summary>The state of the run folder <paramref name="folder"/> at <paramref name="nowUtc"/>.</summary>
    internal static RunState StateOf(string folder, DateTime nowUtc)
    {
        if (!TryReadMarker(folder, out var marker)) return RunState.NotARun;
        if (ProcessIsRunning(marker.ProcessId, marker.StartedUtc) != false) return RunState.Live;
        if (LockIsHeld(folder) != false) return RunState.Live;
        if (marker.Keep && nowUtc - marker.StartedUtc < KeptFor) return RunState.Kept;
        return RunState.Ended;
    }

    /// <summary>Whether a process with id <paramref name="processId"/> that started at <paramref name="startedUtc"/> is
    /// running: false when no process has the id, or the one that has it started at another time (the id was
    /// reused); null when that cannot be told.</summary>
    internal static bool? ProcessIsRunning(int processId, DateTime startedUtc)
    {
        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { return false; }   // no process has that id
        catch (Exception) { return null; }
        using (process)
        {
            try
            {
                if (process.HasExited) return false;
                return (process.StartTime.ToUniversalTime() - startedUtc).Duration() <= StartTimeTolerance;
            }
            catch (InvalidOperationException) { return false; }   // it exited between the reads
            catch (Exception) { return null; }                     // its start time cannot be read from here
        }
    }

    /// <summary>Whether something holds the lock of the run folder <paramref name="folder"/>: false when it opens with
    /// no sharing, or there is no lock; true when the open is refused (a sharing violation on Windows, another
    /// holder's lock on Linux); null when the lock is not a plain file or cannot be opened for another reason.</summary>
    internal static bool? LockIsHeld(string folder)
    {
        var file = new FileInfo(Path.Combine(Long(folder), LockName));
        try
        {
            if (!file.Exists) return Directory.Exists(file.FullName) ? null : false;
            if (IsLink(file)) return null;
            using var probe = new FileStream(file.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }
        catch (Exception) { return null; }
    }

    // ---- the sweep ----------------------------------------------------------------------------------------------

    /// <summary>What a sweep did: the names of the folders it deleted and of the entries it left, or why it did not
    /// look at all.</summary>
    internal sealed record SweepResult(IReadOnlyList<string> Removed, IReadOnlyList<string> Left, string? Skipped);

    /// <summary>Delete the folders of ended runs directly under <paramref name="root"/>, and nothing else. Never
    /// throws: an entry it cannot read or delete is left, and the sweep goes on to the next.</summary>
    public static SweepResult Sweep(string root, DateTime nowUtc)
    {
        var removed = new List<string>();
        var left = new List<string>();
        List<FileSystemInfo> entries;
        try
        {
            var top = new DirectoryInfo(Long(root));
            if (!top.Exists) return new SweepResult(removed, left, null);
            if (IsLink(top)) return new SweepResult(removed, left, $"the root is a link: {root}");
            entries = top.EnumerateFileSystemInfos("*", Flat).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new SweepResult(removed, left, e.Message);
        }

        foreach (var entry in entries)
        {
            bool gone = false;
            try
            {
                gone = entry is DirectoryInfo && !IsLink(entry)
                       && StateOf(entry.FullName, nowUtc) == RunState.Ended
                       && DeleteRun(entry.FullName);
            }
            catch (Exception) { /* left for the next sweep */ }
            (gone ? removed : left).Add(entry.Name);
        }
        return new SweepResult(removed, left, null);
    }

    /// <summary>Delete the run folder <paramref name="folder"/>: every entry but the lock and the marker, never
    /// following a link (<see cref="TryDelete"/>); then the lock, the marker and the folder, which is empty by then.
    /// When anything is refused the lock and the marker stay, so the folder is still a run folder that a later sweep
    /// finishes. True when the folder is gone; never throws.</summary>
    internal static bool DeleteRun(string folder)
    {
        folder = Long(folder);
        List<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(folder).EnumerateFileSystemInfos("*", Flat).ToList(); }
        catch (DirectoryNotFoundException) { return true; }   // gone already: another sweep finished it
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }

        bool clean = true;
        foreach (var entry in entries)
            if (entry.Name is not (LockName or MarkerName))
                clean &= TryDelete(entry);
        if (!clean) return false;
        try
        {
            // File.Delete removes a link as itself and does nothing for a file that is not there.
            File.Delete(Path.Combine(folder, LockName));
            File.Delete(Path.Combine(folder, MarkerName));
            Directory.Delete(folder);   // never recursive: a folder something wrote into meanwhile stays
            return true;
        }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Delete one entry. A link (a junction, a symbolic link to a folder or a file, or any other reparse point)
    /// is removed as itself and never entered, so its target keeps everything in it; a folder is emptied entry by
    /// entry, then removed. False when anything stayed.</summary>
    private static bool TryDelete(FileSystemInfo entry)
    {
        try
        {
            if (IsLink(entry))
            {
                // RemoveDirectory removes a junction or a folder link itself on Windows; unlink removes any link on Linux.
                if (entry is DirectoryInfo && OperatingSystem.IsWindows()) Directory.Delete(entry.FullName);
                else File.Delete(entry.FullName);
                return true;
            }
            if (entry is DirectoryInfo folder)
            {
                bool clean = true;
                foreach (var child in folder.EnumerateFileSystemInfos("*", Flat).ToList()) clean &= TryDelete(child);
                if (clean) Directory.Delete(folder.FullName);
                return clean;
            }
            if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(entry.FullName, entry.Attributes & ~FileAttributes.ReadOnly);
            File.Delete(entry.FullName);
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return true; }   // gone already
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Whether <paramref name="entry"/> is itself a link or another reparse point, from its own attributes and
    /// link target, never its target's. An entry that cannot be read counts as one, so it is never entered.</summary>
    internal static bool IsLink(FileSystemInfo entry)
    {
        try { return (entry.Attributes & FileAttributes.ReparsePoint) != 0 || entry.LinkTarget is not null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary><paramref name="path"/> as an extended-length path on Windows (<c>\\?\</c>, or <c>\\?\UNC\</c>), which
    /// Win32 takes as written; unchanged elsewhere.</summary>
    internal static string Long(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;
        string full = Path.GetFullPath(path);
        return full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full
             : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..]
             : @"\\?\" + full;
    }
}
