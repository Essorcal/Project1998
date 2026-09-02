using Shared;

namespace Server;

/// <summary>
/// The scheduled-restart clock: a deadline, a ladder of warnings announced to every connected player as it
/// is crossed, and the exit that hands the process back to systemd.
///
/// WHY A DEADLINE AND NOT A COUNTDOWN. The schedule is an ABSOLUTE unix-ms instant, exactly like the
/// timed-effect deadlines in Character (buffs/curses/stances). A countdown that decrements on a tick drifts
/// whenever the tick is late, and a GC pause or a busy save sweep during a 30-minute window would quietly
/// move the restart. Storing the instant means a late tick announces late but restarts on time.
///
/// TWO TRIGGERS, ON PURPOSE:
///   • <c>@restart 30 [reason]</c> — a GM in game, for the unplanned case.
///   • <c>run/restart_at</c>     — a file holding "unixMs|reason", for CI. A deploy has no GM logged in,
///     so it needs a way in that doesn't involve the game protocol at all. The file is CONSUMED (deleted)
///     the moment it is read, which is what stops a restart from replaying forever: the newly-started
///     process would otherwise find the same file and schedule itself again.
///
/// WHAT HAPPENS AT ZERO. <see cref="Environment.Exit"/>, which runs the ProcessExit hook in
/// <see cref="TkListener.Shutdown"/> — the same graceful flush SIGTERM takes, saving every connected player
/// before the sockets die. systemd's <c>Restart=always</c> then brings the process back up; because the
/// deploy has already swapped the <c>current</c> symlink, coming back up IS the deploy.
/// </summary>
public sealed class RestartSchedule
{
    /// <summary>Minutes-remaining marks that get an announcement, descending. Each fires at most once per
    /// schedule; a restart booked for less than 30 minutes simply starts partway down the ladder.</summary>
    public static readonly int[] WarnMinutes = { 30, 20, 15, 10, 5, 2, 1 };

    /// <summary>Grace between the final announcement and the exit, so the last packet actually reaches the
    /// client. Everything here is fire-and-forget over TCP; exiting in the same breath as the write would
    /// race the socket flush and players would just drop with no last word.</summary>
    private const int FinalGraceMs = 3000;

    /// <summary>How often the file trigger is re-stat'd. The warning ladder itself is checked every second
    /// (see <see cref="Loop"/>); re-reading a file that changes once a week does not need that cadence.</summary>
    private const int FilePollMs = 6000;

    private readonly World _world;
    private readonly object _lock = new();

    /// <summary>Control-file problems that are currently being reported, so each is logged ONCE when it
    /// starts and once when it clears rather than every poll. Everything in <see cref="Loop"/> re-runs every
    /// <see cref="FilePollMs"/> forever, so an unlatched line for a stuck file would be thousands of
    /// identical entries a day — which buries the first one, the only one that carries any information.
    /// Keyed by a short site name, not by exception, so a failure that changes its message mid-outage
    /// (sharing violation -> access denied) is still one event.</summary>
    private readonly HashSet<string> _reportedFileProblems = new();

    /// <summary>Log a control-file failure the first time it happens and stay quiet while it persists.</summary>
    private void FileProblem(string what, string consequence, Exception e)
    {
        lock (_reportedFileProblems)
            if (!_reportedFileProblems.Add(what)) return;
        Log.Error($"{what} — {consequence}. This is logged once; the next line about it will be recovery", e);
    }

    /// <summary>Clear a latched problem, announcing the recovery if one was outstanding.</summary>
    private void FileProblemCleared(string what)
    {
        lock (_reportedFileProblems)
            if (!_reportedFileProblems.Remove(what)) return;
        Log.Info($"   -> {what}: recovered, working normally again");
    }

    private long _deadlineMs;        // absolute unix ms; 0 = nothing scheduled
    private string _reason = "";
    private int _nextWarn;           // index into WarnMinutes of the next unfired mark
    private bool _firing;            // final announcement sent; the exit is already on its way

    public RestartSchedule(World world) => _world = world;

    // run/, not state/: a trigger is a message from the deploy to the running process, consumed and deleted
    // on read. Restoring one out of a backup would schedule a restart nobody asked for.
    private string TriggerFile => Path.Combine(RepoPaths.RunDir(), "restart_at");

    /// <summary>The CONTENT lane's trigger: a sentinel dropped by a content-only deploy, meaning "re-read the
    /// CSVs and Lua, nobody needs to be kicked". Same consume-on-read discipline as the restart trigger.
    /// Its contents, if any, are used as the note announced to players.</summary>
    private string ReloadFile => Path.Combine(RepoPaths.RunDir(), "reload_now");

    /// <summary>Is a restart booked, and how long until it.</summary>
    public bool Pending { get { lock (_lock) return _deadlineMs != 0; } }

    /// <summary>Milliseconds until the booked restart, or -1 if none is booked.</summary>
    public long RemainingMs
    {
        get { lock (_lock) return _deadlineMs == 0 ? -1 : _deadlineMs - Now; }
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Book a restart <paramref name="minutes"/> from now. Replaces any existing booking (so
    /// "@restart 5" during a 30-minute window pulls it in rather than queueing a second one) and announces
    /// the new time immediately, whatever it is — the ladder below only covers the standard marks.</summary>
    public void Schedule(double minutes, string reason)
    {
        if (minutes < 0) minutes = 0;
        reason = (reason ?? "").Trim();
        long deadline = Now + (long)(minutes * 60_000);
        lock (_lock)
        {
            if (_firing) return;                 // past the point of no return; a new booking is meaningless
            _deadlineMs = deadline;
            _reason = reason;
            // Start the ladder at the first mark STRICTLY below what we just announced, so booking exactly
            // 30 minutes doesn't immediately re-announce "30 minutes" a second time on the next tick.
            _nextWarn = 0;
            while (_nextWarn < WarnMinutes.Length && WarnMinutes[_nextWarn] >= minutes) _nextWarn++;
        }
        Log.Info($"=== restart scheduled in {minutes:0.##} min ({FormatReason(reason)}) ===");
        Announce(RestartLine(minutes));
    }

    /// <summary>Call off a booked restart. Returns false if nothing was booked (or it is already firing, at
    /// which point there is nothing left to call off).</summary>
    public bool Cancel()
    {
        lock (_lock)
        {
            if (_deadlineMs == 0 || _firing) return false;
            _deadlineMs = 0;
            _reason = "";
        }
        // A stale trigger file would otherwise re-book the restart within FilePollMs of cancelling it.
        TryDeleteTrigger();
        Log.Info("=== restart cancelled ===");
        Announce("The scheduled server restart has been cancelled.");
        return true;
    }

    /// <summary>The clock. Started by <see cref="World"/>'s constructor; runs for the process lifetime.</summary>
    internal async Task Loop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        long lastPoll = 0;
        while (await timer.WaitForNextTickAsync())
        {
            try
            {
                if (Now - lastPoll >= FilePollMs) { lastPoll = Now; PollTriggerFile(); PollReloadFile(); }
                TickWarnings();
            }
            catch (Exception e) { Log.Error("restart-schedule tick threw — the clock keeps running", e); }
        }
    }

    // ---- the ladder -------------------------------------------------------------------------------

    private void TickWarnings()
    {
        string? line = null;
        string reason = "";
        bool fire = false;

        lock (_lock)
        {
            if (_deadlineMs == 0 || _firing) return;
            long remaining = _deadlineMs - Now;

            if (remaining <= 0)
            {
                _firing = true;
                fire = true;
            }
            else if (_nextWarn < WarnMinutes.Length && remaining <= WarnMinutes[_nextWarn] * 60_000L)
            {
                // Crossing several marks between two ticks (a long stall, or a booking made just above a
                // mark) must not spam every one of them — jump straight to the LOWEST mark now due and
                // announce only that.
                while (_nextWarn + 1 < WarnMinutes.Length && remaining <= WarnMinutes[_nextWarn + 1] * 60_000L)
                    _nextWarn++;
                line = RestartLine(WarnMinutes[_nextWarn]);
                reason = _reason;
                _nextWarn++;
            }
        }

        // The reason rides along in the LOG only — see RestartLine.
        if (line is not null) { Log.Info($"   -> restart warning: {line} ({FormatReason(reason)})"); Announce(line); }
        if (fire) _ = FireAsync();
    }

    private async Task FireAsync()
    {
        Log.Info("=== restart deadline reached — announcing and exiting ===");
        Announce("The server is restarting now. You will be able to log back in shortly.");
        TryDeleteTrigger();                 // belt and braces; PollTriggerFile already consumed it

        // EXPECTED: the only thing that cancels this delay is the process going down, which is what the next
        // line does deliberately. Nothing to report — there is no failure here to hide.
        try { await Task.Delay(FinalGraceMs); } catch { /* shutting down anyway */ }

        // Exit(0) runs the ProcessExit hook in TkListener.Shutdown — the graceful save-everyone flush.
        // systemd's Restart=always brings us straight back up on the newly-symlinked build.
        Environment.Exit(0);
    }

    // ---- the file trigger -------------------------------------------------------------------------

    /// <summary>Read and CONSUME <c>run/restart_at</c> if present. Format: <c>unixMs</c> or
    /// <c>unixMs|reason</c>. Deleting on read is load-bearing — see the class doc.</summary>
    private void PollTriggerFile()
    {
        string path = TriggerFile;
        if (!File.Exists(path)) return;

        string raw;
        try { raw = File.ReadAllText(path).Trim(); }
        catch (IOException e)
        {
            // Normally the deploy script mid-write, and the next poll gets it. But if it NEVER becomes
            // readable, a booked restart silently never happens — so say so once rather than retry forever
            // in silence.
            FileProblem("restart_at unreadable", "a scheduled restart will not fire until this clears", e);
            return;
        }
        FileProblemCleared("restart_at unreadable");

        TryDeleteTrigger();
        if (raw.Length == 0) return;

        var parts = raw.Split('|', 2);
        if (!long.TryParse(parts[0].Trim(), out long deadline))
        {
            Log.Info($"!! restart_at: not a unix-ms timestamp: \"{raw}\"");
            return;
        }
        string reason = parts.Length > 1 ? parts[1].Trim() : "";

        // A deadline already in the past means a stale file — a restart that fired, a deploy that wrote it
        // while the server was down, a clock skew. Restarting immediately on boot because of one would be a
        // crash loop, so refuse it rather than obey it.
        long remaining = deadline - Now;
        if (remaining <= 0)
        {
            Log.Info($"!! restart_at: deadline is {(-remaining) / 1000}s in the past — ignoring (stale file)");
            return;
        }

        Schedule(remaining / 60_000.0, reason);
    }

    /// <summary>Read and CONSUME <c>run/reload_now</c> — the content lane's "the CSVs on disk are newer than
    /// the ones I have loaded" sentinel. Any text in the file is announced to players as a note; an empty file
    /// reloads silently, which is the right default for a typo fix nobody needs to hear about.</summary>
    private void PollReloadFile()
    {
        string path = ReloadFile;
        if (!File.Exists(path)) return;

        string note;
        try { note = File.ReadAllText(path).Trim(); }
        catch (IOException e)
        {
            FileProblem("reload_now unreadable", "content will NOT reload until this clears", e);
            return;
        }
        FileProblemCleared("reload_now unreadable");

        // Consume-before-act, and a delete failure ABANDONS the reload — otherwise the sentinel survives and
        // every poll reloads the whole content set again, forever. That makes this the most consequential
        // swallowed catch in the file: the deploy lane's entire promise is "a CSV fix ships without kicking
        // anyone", and if the sentinel cannot be deleted, content never reloads and, before this, nothing
        // anywhere said so. The operator sees a deploy that reported success and a world still running the
        // old data.
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            FileProblem("reload_now cannot be deleted",
                        "the content reload is ABANDONED (deleting first is what stops it looping every poll), " +
                        "so the server keeps serving the OLD content — delete run/reload_now by hand", e);
            return;
        }
        FileProblemCleared("reload_now cannot be deleted");

        Log.Info("=== content reload requested (run/reload_now) ===");
        var (ok, report) = _world.ReloadFromDisk();
        Log.Info(ok ? $"   -> reloaded: {report}" : $"!! reload FAILED: {report} (previous content kept)");

        if (ok && note.Length > 0) Announce(note);
    }

    private void TryDeleteTrigger()
    {
        try { if (File.Exists(TriggerFile)) File.Delete(TriggerFile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A trigger that cannot be consumed is re-read on the NEXT poll and re-books the same restart,
            // which is a restart loop, not a no-op — the deadline is in the past by then, so PollTriggerFile
            // rejects it as stale, but only after this has fired at least once. Worth one line.
            FileProblem("restart_at cannot be deleted",
                        "the trigger will be re-read next poll; a stale deadline is refused, but delete " +
                        "run/restart_at by hand to be sure", e);
            return;
        }
        FileProblemCleared("restart_at cannot be deleted");
    }

    // ---- wording ----------------------------------------------------------------------------------

    private void Announce(string text)
    {
        foreach (var s in _world.AllPlayers())
        {
            // One player's failure must not stop the announcement reaching everyone else. (Send itself never
            // throws — see Session.Send — so anything caught here is a bug worth the stack.)
            try { s.SystemAnnounce(text); }
            catch (Exception e) { Log.Error($"restart announcement to {s.Remote} threw — the others still hear it", e); }
        }
    }

    private static string FormatReason(string reason)
        => string.IsNullOrWhiteSpace(reason) ? "no reason given" : reason;

    /// <summary>The ONE player-facing countdown line, used by the booking announcement and by every rung of
    /// the warning ladder alike. There used to be two wordings — an opening "The server will restart in N
    /// minutes. Please find a safe place to log out." and a terser ladder line — which read to players like
    /// two different events, and only the ladder one carried the reason, so "(deploying …)" leaked into the
    /// game from the CI trigger. One sentence, said the same way every time, is what a countdown should
    /// sound like.
    ///
    /// NO REASON SUFFIX, ANYWHERE. What players need is WHEN and WHAT TO DO; "(staging release ef55003…)" is
    /// neither — it's deploy bookkeeping. It still reaches the server log (<see cref="Schedule"/> and the
    /// warning log line below both print it), so nothing is lost by keeping it out of their faces.</summary>
    private static string RestartLine(double minutes)
    {
        long m = (long)Math.Round(minutes);
        string when = m >= 1 ? $"in {m} minute{(m == 1 ? "" : "s")}" : "in less than a minute";
        return $"The server will restart {when}. Please find a safe place to log out.";
    }
}
