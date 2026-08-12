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
        Announce(OpeningLine(minutes, reason));
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
            catch (Exception e) { Log.Info($"!! restart-schedule tick error: {e.Message}"); }
        }
    }

    // ---- the ladder -------------------------------------------------------------------------------

    private void TickWarnings()
    {
        string? line = null;
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
                line = WarnLine(WarnMinutes[_nextWarn], _reason);
                _nextWarn++;
            }
        }

        if (line is not null) { Log.Info($"   -> restart warning: {line}"); Announce(line); }
        if (fire) _ = FireAsync();
    }

    private async Task FireAsync()
    {
        Log.Info("=== restart deadline reached — announcing and exiting ===");
        Announce("The server is restarting now. You will be able to log back in shortly.");
        TryDeleteTrigger();                 // belt and braces; PollTriggerFile already consumed it

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
        catch (IOException) { return; }        // mid-write by the deploy script; try again next poll

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
        catch (IOException) { return; }        // mid-write by the deploy script; try again next poll

        try { File.Delete(path); }
        catch (IOException) { return; }        // couldn't consume it — do NOT reload, or we'd loop every poll
        catch (UnauthorizedAccessException) { return; }

        Log.Info("=== content reload requested (run/reload_now) ===");
        var (ok, report) = _world.ReloadFromDisk();
        Log.Info(ok ? $"   -> reloaded: {report}" : $"!! reload FAILED: {report} (previous content kept)");

        if (ok && note.Length > 0) Announce(note);
    }

    private void TryDeleteTrigger()
    {
        try { if (File.Exists(TriggerFile)) File.Delete(TriggerFile); }
        catch (IOException) { /* it'll be re-read and re-deleted next poll */ }
        catch (UnauthorizedAccessException) { }
    }

    // ---- wording ----------------------------------------------------------------------------------

    private void Announce(string text)
    {
        foreach (var s in _world.AllPlayers())
        {
            try { s.SystemAnnounce(text); }
            catch { /* one dead socket must not stop the announcement reaching everyone else */ }
        }
    }

    private static string FormatReason(string reason)
        => string.IsNullOrWhiteSpace(reason) ? "no reason given" : reason;

    private static string Suffix(string reason)
        => string.IsNullOrWhiteSpace(reason) ? "" : $" ({reason})";

    private static string OpeningLine(double minutes, string reason)
    {
        string when = minutes >= 1
            ? $"in {Math.Round(minutes)} minute{(Math.Round(minutes) == 1 ? "" : "s")}"
            : "in less than a minute";
        // No reason suffix: the notice ends at the instruction. What players need is WHEN and WHAT TO DO,
        // and "(staging release ef55003…)" is neither — it's deploy bookkeeping, and it's already in the
        // server log via Schedule's own line, so nothing is lost by keeping it out of their faces.
        return $"The server will restart {when}. Please find a safe place to log out.";
    }

    private static string WarnLine(int minutes, string reason)
        => minutes == 1
            ? $"The server restarts in 1 minute. Log out now to be safe.{Suffix(reason)}"
            : $"The server restarts in {minutes} minutes.{Suffix(reason)}";
}
