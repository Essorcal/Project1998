using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// #183: the online-registry lookups (<c>World.Online.FindPlayer</c>, <c>ById</c>, <c>ByIdLocked</c>) skip a
/// session whose connection is closed.
///
/// <para><b>The window.</b> On an account's second login the NEW session's <c>HandleArrival</c> runs
/// <c>Session.KickForReplacement</c> on the OLD one, which closes its connection and leaves it standing on its
/// map's <c>Players</c> list until its own read loop unwinds into <c>TearDownWorldState</c>. For that moment two
/// sessions answer to one name, and the old one's <c>Send</c> drops every frame. Before the fix the lookups
/// returned whichever they met first, so a whisper or GM command aimed at that name could land on the dead
/// session and be lost without a word to anyone. The tests below rebuild the window exactly: the old session
/// enters the map first (so a lookup that does not skip it meets it first), the new one second, and the old one
/// is kicked through the real <c>KickForReplacement</c>. "Not on the map yet" is the other shape of the same
/// window: the kick runs before the new session loads and enters its map.</para>
///
/// <para><b>Before and after, for the sender and the target</b> (asserted below; the "before" is what the
/// falsifications print):</para>
/// <list type="bullet">
/// <item>Whisper, new session already on the map. Before: the sender got the normal echo, the new session got
/// nothing, the line was lost. After: the new session gets the whisper and the sender gets the same echo.</item>
/// <item>Whisper, new session not on the map yet. Before: the sender got the normal echo and nobody got the
/// line. After: the sender gets "&lt;name&gt; is nowhere to be found." on the blue channel, the line a whisper
/// to an offline name has always got.</item>
/// <item>A GM command by name (<c>@rez</c> here). Before: the GM got the success reply and the effect landed
/// on the dead session. After: it lands on the new session, or the GM gets "'&lt;name&gt;' isn't online."</item>
/// </list>
///
/// <para>The party-invite, <c>@kick</c> and exchange facts pin the other visible effects the same skip has at
/// those callers, found while doing this; they are stated in the #183 report for a decision.</para>
///
/// <para>Hygiene: every session leaves its map in a <c>finally</c>, and every name is unique to this class, so
/// nothing here can be found by another class's lookup. Map ids are content-free (no Maps.csv row).</para>
/// </summary>
[Collection("world")]
public class OnlineRegistryClosedSessionTests
{
    private const ushort RegistryMap = 60186, MonitorMap = 60187, WhisperMap = 60188, WhisperEarlyMap = 60189,
                         RezMap = 60190, RezEarlyMap = 60191, PartyMap = 60192, KickMap = 60193,
                         ExchangeMap = 60194;

    private const byte WhisperIn = 0x19;

    /// <summary>The staff name the GM facts run as — the same name and roster content
    /// <c>ResolveOnlinePlayerTests</c> and <c>CommandTableTests</c> write, for the reason given there:
    /// <c>StaffAccounts.Load</c> replaces the roster wholesale.</summary>
    private const string GmName = "cmdgm";

    private readonly SessionFixture _fx;

    public OnlineRegistryClosedSessionTests(SessionFixture fx)
    {
        _fx = fx;
        lock (TestProcessState.Gate)
        {
            Directory.CreateDirectory(TestProcessState.StateDirectory);
            File.WriteAllText(Path.Combine(TestProcessState.StateDirectory, "gm_accounts.txt"), GmName + "\n");
            StaffAccounts.Load();
        }
    }

    // =====================================================================================================
    // The registry itself.
    // =====================================================================================================

    /// <summary>The three lookups, on the duplicate-login window. The first assertion is the setup's proof:
    /// before the kick the name resolves to the OLD session, so the old one really is the first hit and a
    /// lookup that did not skip it would return it.
    ///
    /// <para>Falsified (both red, see the #183 report): drop the <c>!p.IsClosed</c> test from
    /// <c>FindPlayer</c> and <c>ByIdLocked</c>, or make <c>Session.IsClosed</c> return false.</para></summary>
    [Fact]
    public void TheLookupsSkipTheKickedSessionAndFindItsReplacement()
    {
        var (old, _) = _fx.Player("DupRegistry", RegistryMap, 5, 5);
        var (fresh, _) = _fx.Player("DupRegistry", RegistryMap, 6, 5);
        var online = _fx.World.Online;

        try
        {
            Assert.Same(old, online.FindPlayer("DupRegistry"));     // old entered first: it is the first hit

            old.KickForReplacement();
            Assert.True(old.IsClosed);
            Assert.False(fresh.IsClosed);

            Assert.Same(fresh, online.FindPlayer("DupRegistry"));
            Assert.Same(fresh, online.FindPlayer("dupregistry"));   // still case-insensitive
            Assert.Null(online.ById(old.PlayerId));                 // the kicked session's entity id finds nobody
            Assert.Same(fresh, online.ById(fresh.PlayerId));

            Session? lockedOld = old, lockedFresh = null;
            _fx.World.UnderWorldLockForTest(() =>
            {
                lockedOld = online.ByIdLocked(old.PlayerId);
                lockedFresh = online.ByIdLocked(fresh.PlayerId);
            });
            Assert.Null(lockedOld);
            Assert.Same(fresh, lockedFresh);

            // The kicked session is still on the map — the skip is the lookups', not a removal.
            Assert.Contains(old, online.All());
        }
        finally
        {
            _fx.World.LeaveMap(old, RegistryMap);
            _fx.World.LeaveMap(fresh, RegistryMap);
        }
    }

    /// <summary>The skip takes no session monitor. The lookups run under <c>World._lock</c>, and Locking.md
    /// rule 1 forbids entering a session's state monitor there (#29: session state THEN <c>_lock</c>). With the
    /// kicked session's monitor held on another thread, both lookups must still finish: an <c>IsClosed</c> that
    /// entered the monitor would block here until the holder let go.</summary>
    [Fact]
    public async Task TheLookupsDoNotWaitOnTheKickedSessionsMonitor()
    {
        var (old, _) = _fx.Player("DupMonitor", MonitorMap, 5, 5);
        var (fresh, _) = _fx.Player("DupMonitor", MonitorMap, 6, 5);
        var online = _fx.World.Online;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task? holder = null;

        try
        {
            old.KickForReplacement();
            holder = Task.Run(() => old.WithState(() => { held.Set(); release.Wait(TimeSpan.FromSeconds(30)); }));
            Assert.True(held.Wait(TimeSpan.FromSeconds(10)), "the holder never took the kicked session's monitor");

            var lookup = Task.Run(() => (online.FindPlayer("DupMonitor"), online.ById(old.PlayerId)));
            bool finished = await Task.WhenAny(lookup, Task.Delay(TimeSpan.FromSeconds(5))) == lookup;
            release.Set();

            Assert.True(finished, "a lookup blocked on the kicked session's state monitor");
            var (byName, byOldId) = await lookup;
            Assert.Same(fresh, byName);
            Assert.Null(byOldId);
        }
        finally
        {
            release.Set();
            if (holder is not null) await holder.WaitAsync(TimeSpan.FromSeconds(30));
            _fx.World.LeaveMap(old, MonitorMap);
            _fx.World.LeaveMap(fresh, MonitorMap);
        }
    }

    // =====================================================================================================
    // Whisper: the sender and the target, before and after.
    // =====================================================================================================

    /// <summary>The new session is already on the map. After: it receives the whisper (RTK's
    /// <c>name" msg</c> on the blue channel) and the sender gets the usual <c>target&gt; msg</c> echo. Before:
    /// the lookup returned the kicked session, the sender got the same echo and the new session got
    /// nothing.</summary>
    [Fact]
    public void AWhisperInTheWindowReachesTheNewSession()
    {
        var (old, oldOut) = _fx.Player("DupWhisperee", WhisperMap, 5, 5);
        var (fresh, freshOut) = _fx.Player("DupWhisperee", WhisperMap, 6, 5);
        var (sender, senderOut) = _fx.Player("DupWhisperer", WhisperMap, 7, 5);

        try
        {
            old.KickForReplacement();
            oldOut.Clear(); freshOut.Clear(); senderOut.Clear();

            sender.Receive(WhisperFrame("DupWhisperee", "are you back"));

            Assert.Equal(new[] { ((byte)0, "DupWhisperee> are you back") }, MiniTexts(senderOut));   // sender
            Assert.Equal(new[] { ((byte)0, "DupWhisperer\" are you back") }, MiniTexts(freshOut));   // target
            Assert.Empty(oldOut.Frames);
        }
        finally
        {
            _fx.World.LeaveMap(old, WhisperMap);
            _fx.World.LeaveMap(fresh, WhisperMap);
            _fx.World.LeaveMap(sender, WhisperMap);
        }
    }

    /// <summary>The new session has not entered its map yet (the kick runs before its load). After: the sender
    /// gets the offline line, "&lt;name&gt; is nowhere to be found.", on the blue channel, and no echo. Before:
    /// the sender got the echo "&lt;name&gt;&gt; msg" as if it had been delivered, and nobody received it.
    /// </summary>
    [Fact]
    public void AWhisperBeforeTheNewSessionArrivesGetsTheOfflineLine()
    {
        var (old, oldOut) = _fx.Player("DupEarlyWhisperee", WhisperEarlyMap, 5, 5);
        var (sender, senderOut) = _fx.Player("DupEarlyWhisperer", WhisperEarlyMap, 7, 5);

        try
        {
            old.KickForReplacement();
            oldOut.Clear(); senderOut.Clear();

            sender.Receive(WhisperFrame("DupEarlyWhisperee", "are you back"));

            Assert.Equal(new[] { ((byte)0, "DupEarlyWhisperee is nowhere to be found.") }, MiniTexts(senderOut));
            Assert.Empty(oldOut.Frames);
        }
        finally
        {
            _fx.World.LeaveMap(old, WhisperEarlyMap);
            _fx.World.LeaveMap(sender, WhisperEarlyMap);
        }
    }

    // =====================================================================================================
    // A GM command by name: the GM and the target, before and after.
    // =====================================================================================================

    /// <summary><c>@rez &lt;name&gt;</c> with the new session on the map. After: the new session gets "You are
    /// restored to full health." and the GM gets "Restored &lt;name&gt; to full health.". Before: the GM got the
    /// same reply, but the restore and its line went to the kicked session and the new session got
    /// nothing.</summary>
    [Fact]
    public void AGmCommandInTheWindowActsOnTheNewSession()
    {
        var (old, oldOut) = _fx.Player("DupRez", RezMap, 5, 5);
        var (fresh, freshOut) = _fx.Player("DupRez", RezMap, 6, 5);
        var (gm, gmOut) = _fx.Player(GmName, RezMap, 7, 5);

        try
        {
            old.KickForReplacement();
            oldOut.Clear(); freshOut.Clear(); gmOut.Clear();

            Run(gm, "@rez DupRez");

            Assert.Contains("Restored DupRez to full health.", PaneText(gmOut));                      // GM
            Assert.Contains(MiniTexts(freshOut), t => t.Text == "You are restored to full health.");   // target
            Assert.Empty(oldOut.Frames);
        }
        finally
        {
            _fx.World.LeaveMap(old, RezMap);
            _fx.World.LeaveMap(fresh, RezMap);
            _fx.World.LeaveMap(gm, RezMap);
        }
    }

    /// <summary><c>@rez &lt;name&gt;</c> before the new session is on the map. After: the GM gets
    /// "'&lt;name&gt;' isn't online." and no success line. Before: the GM got "Restored &lt;name&gt; to full
    /// health." for a restore that reached nobody.</summary>
    [Fact]
    public void AGmCommandBeforeTheNewSessionArrivesGetsTheOfflineLine()
    {
        var (old, oldOut) = _fx.Player("DupEarlyRez", RezEarlyMap, 5, 5);
        var (gm, gmOut) = _fx.Player(GmName, RezEarlyMap, 7, 5);

        try
        {
            old.KickForReplacement();
            oldOut.Clear(); gmOut.Clear();

            Run(gm, "@rez DupEarlyRez");

            Assert.DoesNotContain("Restored DupEarlyRez", PaneText(gmOut));                       // no success
            Assert.Contains(SpeechText(gmOut), t => t.Contains("'DupEarlyRez' isn't online."));   // refusal
            Assert.Empty(oldOut.Frames);
        }
        finally
        {
            _fx.World.LeaveMap(old, RezEarlyMap);
            _fx.World.LeaveMap(gm, RezEarlyMap);
        }
    }

    // =====================================================================================================
    // Other callers of the same lookups: the effects the skip has there, pinned so they are decided.
    // =====================================================================================================

    /// <summary>A party invite by name (the profile window's Group button, <c>0x2E</c>) in the window. After:
    /// the NEW session is seated and hears "&lt;name&gt; is joining the group.". Before: the KICKED session was
    /// seated (the inviter saw the same joining lines) and dropped out again when its teardown ran; the new
    /// session was not in the group.</summary>
    [Fact]
    public void APartyInviteInTheWindowSeatsTheNewSession()
    {
        var (old, oldOut, _) = _fx.PlayerWith("DupInvitee", c => c.Grouped = true, PartyMap, 5, 5);
        var (fresh, freshOut, _) = _fx.PlayerWith("DupInvitee", c => c.Grouped = true, PartyMap, 6, 5);
        var (inviter, inviterOut) = _fx.Player("DupInviter", PartyMap, 7, 5);

        try
        {
            old.KickForReplacement();
            oldOut.Clear(); freshOut.Clear(); inviterOut.Clear();

            inviter.Receive(SessionFixture.PartyInviteFrame("DupInvitee"));

            Assert.Contains(MiniTexts(inviterOut), t => t.Text == "DupInvitee is joining the group.");
            Assert.Contains(MiniTexts(freshOut), t => t.Text == "DupInvitee is joining the group.");
            Assert.Empty(oldOut.Frames);
        }
        finally
        {
            _fx.World.LeaveMap(old, PartyMap);
            _fx.World.LeaveMap(fresh, PartyMap);
            _fx.World.LeaveMap(inviter, PartyMap);
        }
    }

    /// <summary><c>@kick &lt;name&gt;</c> in the window. After: the NEW session is told and disconnected, and
    /// the GM gets "Kicked &lt;name&gt;.". Before: the GM got the same line, the kick went to the session that
    /// was already closed, and the new session stayed online.</summary>
    [Fact]
    public void AGmKickInTheWindowDisconnectsTheNewSession()
    {
        var (old, oldOut) = _fx.Player("DupKick", KickMap, 5, 5);
        var (fresh, freshOut) = _fx.Player("DupKick", KickMap, 6, 5);
        var (gm, gmOut) = _fx.Player(GmName, KickMap, 7, 5);

        try
        {
            old.KickForReplacement();
            oldOut.Clear(); freshOut.Clear(); gmOut.Clear();

            Run(gm, "@kick DupKick");

            Assert.Contains(SpeechText(gmOut), t => t.Contains("Kicked DupKick."));   // GM: same line before and after
            Assert.True(freshOut.Closed, "the new session should be the one the kick disconnects");
            Assert.Contains(Messages(freshOut), t => t.Contains("You were disconnected by a GM."));
            Assert.Empty(oldOut.Frames);
        }
        finally
        {
            _fx.World.LeaveMap(old, KickMap);
            _fx.World.LeaveMap(fresh, KickMap);
            _fx.World.LeaveMap(gm, KickMap);
        }
    }

    /// <summary>The exchange-open click (<c>0x4A</c> sub 0) on the kicked session's entity id, which peers still
    /// have drawn until its teardown despawns it. After: nothing happens — no window, no line. Before: an
    /// exchange window opened on the sender against the kicked session (and closed again with "Exchange
    /// cancelled." when its teardown ran).</summary>
    [Fact]
    public void AnExchangeClickOnTheKickedSessionOpensNothing()
    {
        var (old, oldOut) = _fx.Player("DupTrader", ExchangeMap, 5, 5);
        var (fresh, freshOut) = _fx.Player("DupTrader", ExchangeMap, 6, 5);
        var (sender, senderOut) = _fx.Player("DupTradeSender", ExchangeMap, 7, 5);

        try
        {
            old.KickForReplacement();
            oldOut.Clear(); freshOut.Clear(); senderOut.Clear();

            sender.Receive(ExchangeOpenFrame(old.PlayerId));

            Assert.Empty(senderOut.BodiesOf(ServerOp.Exchange));
            Assert.Empty(freshOut.Frames);
            Assert.Empty(oldOut.Frames);
        }
        finally
        {
            _fx.World.LeaveMap(old, ExchangeMap);
            _fx.World.LeaveMap(fresh, ExchangeMap);
            _fx.World.LeaveMap(sender, ExchangeMap);
        }
    }

    // ===== plumbing =====================================================================================

    private static void Run(Session session, string command)
    {
        var text = Encoding.ASCII.GetBytes(command);
        var body = new byte[2 + text.Length];
        body[0] = 0;
        body[1] = (byte)text.Length;
        text.CopyTo(body, 2);
        session.Receive(SessionFixture.Frame(0x0E, body));
    }

    private static byte[] WhisperFrame(string to, string msg)
    {
        byte[] n = Encoding.ASCII.GetBytes(to), m = Encoding.ASCII.GetBytes(msg);
        var body = new List<byte> { (byte)n.Length };
        body.AddRange(n);
        body.Add((byte)m.Length);
        body.AddRange(m);
        body.Add(0);
        return SessionFixture.Frame(WhisperIn, body.ToArray());
    }

    /// <summary><c>0x4A</c> sub 0 (open): <c>sub(u8) targetId(u32BE)</c>, the shape
    /// <c>HandleExchangeRequest</c> parses.</summary>
    private static byte[] ExchangeOpenFrame(uint id) =>
        SessionFixture.Frame(ClientOp.Exchange,
                             new byte[] { 0, (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id });

    /// <summary><c>0x0A</c> minitext frames as (type, text): type 0 is the blue wisp channel, 3 the status
    /// pane. Layout <c>type(u8) len(u16BE) text</c>.</summary>
    private static List<(byte Type, string Text)> MiniTexts(RecordingOutbound o)
    {
        var lines = new List<(byte, string)>();
        foreach (var body in o.BodiesOf(ServerOp.MiniText))
            if (body.Length >= 3) lines.Add((body[0], Encoding.ASCII.GetString(body, 3, body.Length - 3)));
        return lines;
    }

    /// <summary>The status pane as one string: a command reply is wrapped to the pane width across several
    /// minitext lines, so a sentence is matched against the lines joined back with spaces.</summary>
    private static string PaneText(RecordingOutbound o) => string.Join(" ", MiniTexts(o).Select(t => t.Text));

    /// <summary><c>0x0D</c> speech frames as ASCII — where <c>SendLog</c> and a command reply land.</summary>
    private static List<string> SpeechText(RecordingOutbound o) =>
        o.BodiesOf(ServerOp.Speech).Select(b => Encoding.ASCII.GetString(b)).ToList();

    /// <summary><c>0x02</c> login-box messages as ASCII — where <c>SendMessage</c> (the kick notice) lands.
    /// </summary>
    private static List<string> Messages(RecordingOutbound o) =>
        o.BodiesOf(ServerOp.Message).Select(b => Encoding.ASCII.GetString(b)).ToList();
}
