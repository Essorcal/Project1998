using System.Reflection;
using System.Text.RegularExpressions;
using Server;
using Shared;
using Xunit;

namespace Tests;

/// <summary>
/// <c>Scripts/Serve.ps1 -Stop</c>'s last graceful step for a GAME process still running 30 s after its console
/// signals: it writes <c>run/restart_at</c> with a deadline and waits, and <see cref="RestartSchedule"/> reads the
/// file on its poll, books the restart, and exits through the same flush a signal takes. The two halves are in
/// two languages and agree only by arithmetic, and they have come apart once already: the script wrote "now",
/// the poll runs every <c>FilePollMs</c> and refuses a deadline that is not in the future, so the fallback was
/// always refused as a stale file ("restart_at: deadline is 0s in the past — ignoring") and the process was
/// terminated with no flush, until #305 booked it 8 s ahead. This reads both files as they are, so a change to
/// either side that makes the fallback stale again fails here rather than on somebody's stop.
/// <para>Falsification: book the deadline 7000 ms ahead instead of 8000 in Serve.ps1 (or restore the pre-#305
/// line, which books no lead at all), or raise <c>FilePollMs</c> to 8000, and this fact goes red.</para>
/// </summary>
public class ServeStopFallbackTests
{
    [Fact]
    public void The_stops_restart_at_deadline_outlives_the_poll_and_its_wait_covers_the_exit()
    {
        string script = File.ReadAllText(Path.Combine(RepoPaths.Root(), "Scripts", "Serve.ps1"));
        var lead = Regex.Match(script, @"\$deadline\s*=\s*\[DateTimeOffset\]::UtcNow\.ToUnixTimeMilliseconds\(\)\s*\+\s*(\d+)");
        Assert.True(lead.Success, "Serve.ps1 no longer books run\\restart_at as UtcNow plus a lead in ms");
        var wait = Regex.Match(script, @"WriteAllText\(\$trigger[^\n]*\n\s*if \(Wait-Exit \$id (\d+)\)");
        Assert.True(wait.Success, "Serve.ps1 no longer waits on the process right after writing run\\restart_at");
        long leadMs = long.Parse(lead.Groups[1].Value);
        long waitMs = long.Parse(wait.Groups[1].Value) * 1000;

        string schedule = File.ReadAllText(Path.Combine(RepoPaths.Root(), "Server", "RestartSchedule.cs"));
        var tick = Regex.Match(schedule, @"new PeriodicTimer\(TimeSpan\.FromSeconds\((\d+)\)\)");
        Assert.True(tick.Success, "RestartSchedule.Loop no longer ticks on a PeriodicTimer of whole seconds");
        long tickMs = long.Parse(tick.Groups[1].Value) * 1000;
        long pollMs = PrivateConst("FilePollMs");
        long graceMs = PrivateConst("FinalGraceMs");

        // The file is polled on the first tick at least FilePollMs after the previous poll, so a file written
        // just after a poll is read up to FilePollMs plus one tick later, and the deadline must still be ahead.
        Assert.True(leadMs > pollMs + tickMs,
            $"restart_at is booked {leadMs} ms ahead but can be read {pollMs + tickMs} ms after it is written, when the poll refuses it as stale");
        // The deadline is then noticed on the next tick, and the process exits FinalGraceMs after that.
        long latestExitMs = leadMs + tickMs + graceMs;
        Assert.True(waitMs > latestExitMs,
            $"Serve.ps1 waits {waitMs} ms after writing restart_at, but the exit can come {latestExitMs} ms after the write");
    }

    private static long PrivateConst(string name)
    {
        var field = typeof(RestartSchedule).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return Convert.ToInt64(field!.GetRawConstantValue());
    }
}
