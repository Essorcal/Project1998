using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The staff-override seam added by #57 finding 31: <see cref="GmOverrides"/>, the <c>@toggles</c> readout
/// over it, and the one log line every override write now goes through.
///
/// <para><b>What needs a test and what does not.</b> Gathering nine loose fields onto one class is a change
/// the compiler proves — every read site is inside <c>Session</c>, which is one partial class, so a missed
/// site does not build. Two things it does NOT prove, and they are the two things this file pins. First,
/// that <c>@toggles</c> still lists EVERY override: a tenth field added later, or one dropped from the
/// readout during a merge, compiles perfectly and simply never appears, which is the silent failure
/// AGENTS.md rule 3 is about. So fact (a) pins the whole line set for a fresh session rather than probing
/// one entry. Second, that the write path really routes through <see cref="GmOverrides.Log"/>: the old
/// hand-written lines would keep working if one command were left behind, and three of them named no actor
/// at all — which is exactly the record you want when a world-wide knob like the mob swing pose has been
/// moved by somebody.</para>
///
/// <para><b>Collection "log"</b>, because facts (b) and (c) read the formatted line back off the file sink,
/// which is process-global — the reason <see cref="LogWarnErrorLineTests"/> gives. The
/// <see cref="SessionFixture"/> is taken as a CLASS fixture rather than the shared "world" collection one,
/// so this class drives its own unstarted <c>World</c> and cannot race the collection that owns that one:
/// the <c>@clock</c> and zone-weather lines in the readout are per-<c>World</c> state and would otherwise be
/// whatever another test had just pinned.</para>
/// </summary>
[Collection("log")]
public sealed class GmOverridesTests : IClassFixture<SessionFixture>
{
    /// <summary>The same roster name and the same file content <see cref="CommandTableTests"/> writes, on
    /// purpose: <c>StaffAccounts.Load</c> replaces the roster wholesale, so a second test class declaring a
    /// DIFFERENT staff name would silently demote the first one's sessions depending on run order. Writing
    /// the identical roster makes the two classes idempotent with respect to each other.</summary>
    private const string GmName = "cmdgm";

    private readonly SessionFixture _fx;

    public GmOverridesTests(SessionFixture fx)
    {
        _fx = fx;
        lock (TestProcessState.Gate)
        {
            Directory.CreateDirectory(TestProcessState.StateDirectory);
            File.WriteAllText(Path.Combine(TestProcessState.StateDirectory, "gm_accounts.txt"), GmName + "\n");
            StaffAccounts.Load();
        }
    }

    /// <summary>A brand-new GM session with the world-entry chatter dropped. Fresh per case, because the
    /// overrides under test are session state.</summary>
    private (Session session, RecordingOutbound outbound) Gm()
    {
        var (session, outbound) = _fx.Player(GmName);
        outbound.Clear();
        return (session, outbound);
    }

    /// <summary>A command the way the read loop runs one: a framed <c>0x0E</c> chat packet. Same entry point
    /// <see cref="CommandTableTests"/> uses, and for the same reason — the state monitor wraps
    /// <c>Session.Handle</c>, so reaching past it would run the handler outside the monitor.</summary>
    private static void Run(Session session, string command)
    {
        var text = Encoding.ASCII.GetBytes(command);
        var body = new byte[2 + text.Length];
        body[0] = 0;
        body[1] = (byte)text.Length;
        text.CopyTo(body, 2);
        session.Receive(SessionFixture.Frame(0x0E, body));
    }

    /// <summary>Every override, in the order <c>@toggles</c> prints them, for a session that has run no
    /// override command at all. The pane rule is absent because <c>ReplyList</c>'s header IS this listing's
    /// separator (Server/Commands.cs), and every line is inside the 30-character pane.
    ///
    /// <para>The last four lines are the ones that are not session state: the mob swing pose and the
    /// <c>@wmpos</c> dot table are world-wide statics, and the <c>@clock</c> hour and the zone weather are
    /// pins that live on <c>World</c> and are shown read-only. They read as their defaults here because this
    /// class drives its own <c>World</c> and nothing in the suite writes the two statics.</para>
    ///
    /// <para>Falsification: delete any single entry from <c>TogglesCmd</c>'s array (dropping
    /// <c>swing sfx</c> is the cheapest) and this fails with a 13-line actual against a 14-line expected.
    /// Run the re-break, confirm red, restore.</para></summary>
    [Fact]
    public void Toggles_lists_every_override_for_a_fresh_session()
    {
        var (session, outbound) = Gm();

        Run(session, "@toggles");

        Assert.Equal(new[]
        {
            "pane3|= overrides =",
            "pane3|No-clip          :OFF",
            "pane3|Peace            :OFF",
            "pane3|Any-warp         :OFF",
            "pane3|Show warps       :OFF",
            "pane3|marker frames 877/877",
            "pane3|swing sfx 0",
            "pane3|fist sfx 9",
            "pane3|hit sfx 349",
            "pane3|mob swing type 1 time 20",
            "pane3|world-map dots pinned 0",
            "pane3|clock hour real",
            "pane3|zone weather clear",
        }, CommandTableTests.Transcript(outbound));
    }

    /// <summary>"@clip 1" says the same thing to the player it always did, shows up in the readout, and
    /// writes ONE log line naming the actor, the override, the value it had and the value it has now.
    ///
    /// <para>Falsification: drop <c>{actor}</c> from <see cref="GmOverrides.LogLine"/> and the
    /// <c>Assert.Equal</c> on the log line fails; drop <c>{from}</c> and it fails the same way. Removing
    /// <c>No-clip</c> from the readout fails the middle assertion instead. Run each re-break, confirm red,
    /// restore.</para></summary>
    [Fact]
    public void Clip_reads_back_on_and_logs_the_actor_with_both_values()
    {
        var (session, outbound) = Gm();
        string path = LogPath("clip");

        Log.AttachFile(path);
        try
        {
            Run(session, "@clip 1");
            Assert.Equal(new[] { "pane3|" + Session.PaneRule, "pane3|No-clip          :ON" },
                         CommandTableTests.Transcript(outbound));

            outbound.Clear();
            Run(session, "@toggles");
            Assert.Contains("pane3|No-clip          :ON", CommandTableTests.Transcript(outbound));

            Log.Shutdown();
            Assert.Equal($"   -> OVERRIDE '{GmName}' no-clip: off -> on",
                         LineContaining(path, "no-clip").Substring(Log.StampLength));
        }
        finally
        {
            Log.RestartWriterForTest();
        }
    }

    /// <summary>"@mobact 2 20" moves the WORLD-WIDE mob swing pose — the pair World reads with no session in
    /// hand — and that write is now attributable. It was the one override command whose log line named
    /// nobody at all, which is the whole reason finding 31 called it out.
    ///
    /// <para>The pair is a process-global static, so it is restored in a <c>finally</c>: nothing else in the
    /// suite reads it, but leaving a swept value behind would make the readout fact above depend on run
    /// order.</para>
    ///
    /// <para>Falsification: drop <c>{actor}</c> from <see cref="GmOverrides.LogLine"/>, or route
    /// <c>MobActionProbe</c>'s write around <see cref="GmOverrides.Log"/> back to a bare
    /// <c>Log.Info</c>, and the log assertion fails. Run both re-breaks, confirm red, restore.</para>
    /// </summary>
    [Fact]
    public void Mobact_moves_the_world_wide_swing_pair_and_logs_the_actor()
    {
        var (session, _) = Gm();
        byte type = GmOverrides.MobSwingActionType;
        ushort time = GmOverrides.MobSwingActionTime;
        string path = LogPath("mobact");

        Log.AttachFile(path);
        try
        {
            Run(session, "@mobact 2 20");

            Assert.Equal(2, GmOverrides.MobSwingActionType);
            Assert.Equal(20, GmOverrides.MobSwingActionTime);

            Log.Shutdown();
            Assert.Equal($"   -> OVERRIDE '{GmName}' mob swing action: type=1 time=20 -> type=2 time=20",
                         LineContaining(path, "mob swing action").Substring(Log.StampLength));
        }
        finally
        {
            GmOverrides.MobSwingActionType = type;
            GmOverrides.MobSwingActionTime = time;
            Log.RestartWriterForTest();
        }
    }

    private static string LogPath(string tag) =>
        Path.Combine(Path.GetTempPath(), $"p1998-gmoverrides-{tag}-{Guid.NewGuid():N}", "server.log");

    /// <summary>Read the log while the writer still holds it open (there is no detach — see
    /// <see cref="SharedLoggerTests"/>) and return the one line carrying <paramref name="needle"/>.</summary>
    private static string LineContaining(string path, string needle)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        string text = reader.ReadToEnd();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Contains(needle, StringComparison.Ordinal)) return line;
        }
        throw new Xunit.Sdk.XunitException($"no line containing '{needle}' in {path}:\n{text}");
    }
}
