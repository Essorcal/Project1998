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
/// <para><b>No collection.</b> Facts (b) and (c) used to read the formatted line back off the file sink,
/// which is process-global, so this class had to sit in collection <c>"log"</c> — away from the world it
/// pins, and paying a real <c>Log.Shutdown</c> and <c>RestartWriterForTest</c> per fact. They now observe the
/// line in process through <see cref="LogLineSink"/>, which is exclusive on its own, so neither reason is
/// left. It does NOT join <c>"world"</c> either: the <see cref="SessionFixture"/> is taken as a CLASS fixture
/// so this class drives its own unstarted <c>World</c>, and the <c>@clock</c> and zone-weather lines in fact
/// (a)'s readout are per-<c>World</c> state that would otherwise be whatever the shared world had last
/// pinned. A class cannot take both the "world" collection fixture and its own, so the isolation and the
/// collection are the same choice. Without a <c>[Collection]</c> xunit gives the class its own, which is
/// exactly what it wants: its own World, and parallel with everything else.</para>
/// </summary>
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

        using var log = LogLineSink.Acquire();
        Run(session, "@clip 1");
        Assert.Equal(new[] { "pane3|" + Session.PaneRule, "pane3|No-clip          :ON" },
                     CommandTableTests.Transcript(outbound));

        outbound.Clear();
        Run(session, "@toggles");
        Assert.Contains("pane3|No-clip          :ON", CommandTableTests.Transcript(outbound));

        Assert.Equal($"   -> OVERRIDE '{GmName}' no-clip: off -> on",
                     log.LineContaining("no-clip").Substring(Log.StampLength));
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

        using var log = LogLineSink.Acquire();
        try
        {
            Run(session, "@mobact 2 20");

            Assert.Equal(2, GmOverrides.MobSwingActionType);
            Assert.Equal(20, GmOverrides.MobSwingActionTime);

            Assert.Equal($"   -> OVERRIDE '{GmName}' mob swing action: type=1 time=20 -> type=2 time=20",
                         log.LineContaining("mob swing action").Substring(Log.StampLength));
        }
        finally
        {
            GmOverrides.MobSwingActionType = type;
            GmOverrides.MobSwingActionTime = time;
        }
    }
}
