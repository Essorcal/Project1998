using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// #173. <c>TryStartTrade</c> opens a trade on BOTH sessions, and the target's half is another session's
/// state. It used to read the target's gates and write the target's <c>_trade</c> holding only the
/// initiator's monitor; it now does both inside <c>WithStatePair</c>. These facts pin what happens when the
/// target is on its way out, or wanted by someone else, while the open runs.
///
/// <para>The races are forced, not waited for. The two teardown facts drive the real
/// <c>TearDownWorldState</c> by reflection; the party-gap one parks it on a held member's monitor. The
/// two-initiator and death facts park an initiator on <c>Session.TradeOpenProbeForTest</c>, past every gate
/// and before the writes, and release it once the competing thread has either reached the same point
/// (the unlocked code) or is blocked on a monitor the initiator holds (the pair).</para>
/// </summary>
[Collection("world")]
public sealed class TradeOpenRaceTests
{
    private const byte ExchangeIn = 0x4a;
    private const byte ExchangeOut = 0x42;
    private const byte MiniTextOut = 0x0A;
    private const byte ExcOpen = 0;
    private const string Refuse = "That person refuses to exchange with you.";

    private const ushort PartyTeardownMap = 60200, DoneTeardownMap = 60201, RefusalMap = 60202, ElsewhereMap = 60203,
                         RaceMap = 60204, DeathMap = 60205;
    private const string CancelText = "Exchange cancelled.";
    private const string Spirits = "Spirits can't do that.";
    private const string Busy = "You are already trading.";
    private const string Bored = "You move your items from one hand to another, but quickly get bored.";

    private static readonly FieldInfo TradeField =
        typeof(Session).GetField("_trade", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo PartyField =
        typeof(Session).GetField("_party", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo TearDown =
        typeof(Session).GetMethod("TearDownWorldState", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo StartTrade =
        typeof(Session).GetMethod("TryStartTrade", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly SessionFixture _fx;

    public TradeOpenRaceTests(SessionFixture fx) => _fx = fx;

    private static object? TradeOf(Session s) => TradeField.GetValue(s);

    private static byte[] Be32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static byte[] OpenRequest(uint targetId) =>
        SessionFixture.Frame(ExchangeIn, new byte[] { ExcOpen }.Concat(Be32(targetId)).Concat(new byte[] { 0 }).ToArray());

    private static List<string> MiniTexts(RecordingOutbound o) =>
        o.BodiesOf(MiniTextOut).Select(b => Encoding.ASCII.GetString(b, 3, (b[1] << 8) | b[2])).ToList();

    /// <summary>Sub-type 4 as it goes out: <c>04 | 00 | len | text</c>, the box that closes a window.</summary>
    private static byte[] Message(string text) =>
        new byte[] { 4, 0, (byte)text.Length }.Concat(Encoding.ASCII.GetBytes(text)).ToArray();

    /// <summary>Whether <paramref name="t"/> is blocked (here, on a monitor). A parked initiator reads it to
    /// learn that the competing thread cannot get past the monitor the initiator holds.</summary>
    private static bool Parked(Thread? t) =>
        t is not null && (t.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0;

    private static void WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < 5000) Thread.Yield();
    }

    /// <summary>
    /// The target has finished its disconnect teardown, but the initiator resolved it just before (the
    /// 0x4a path's <c>ById</c>, or the hand gesture's <c>PeerAt</c>). Driven by calling <c>TryStartTrade</c>
    /// with the resolved target under the initiator's monitor, which is exactly where the handler is when the
    /// resolution has returned. The open must be refused: the target has left the map and will never close a
    /// window again.
    /// </summary>
    [Fact]
    public void AnOpenAimedAtATargetWhoseTeardownHasFinishedIsRefused()
    {
        var (leaver, leaverOut) = _fx.Player("OpenDoneLeaver", DoneTeardownMap, 5, 10);
        var (opener, openerOut) = _fx.Player("OpenDoneOpener", DoneTeardownMap, 6, 10);

        leaver.WithState(() => TearDown.Invoke(leaver, null));
        leaverOut.Clear();
        openerOut.Clear();

        opener.WithState(() => StartTrade.Invoke(opener, new object[] { leaver }));

        Assert.Null(TradeOf(opener));
        Assert.Null(TradeOf(leaver));
        Assert.Empty(openerOut.BodiesOf(ExchangeOut));
        Assert.Equal(Refuse, Assert.Single(MiniTexts(openerOut)));
    }

    /// <summary>
    /// The target is INSIDE its disconnect teardown, past its <c>_trade</c> check, and the teardown has
    /// dropped the target's monitor: <c>RemoveFromParty</c> broadcasts "is leaving the group" to a party
    /// member who ranks below the leaver, and #29 rule 2 exits the leaver's monitor while it waits for that
    /// member's. A third thread holds the member's monitor so the teardown stays parked in that gap; the
    /// initiator's open runs in it. The open must be refused, or the teardown goes on to leave the map with a
    /// trade planted on it that nothing will close.
    /// </summary>
    [Fact]
    public void AnOpenInsideTheTeardownsPartyGapIsRefused()
    {
        // Created in rank order: the member ranks below the leaver, so the leaver's broadcast descends.
        var (member, _, _) = _fx.PlayerWith("OpenGapMember", c => c.Grouped = true, PartyTeardownMap, 5, 10);
        var (leaver, leaverOut) = _fx.Player("OpenGapLeaver", PartyTeardownMap, 6, 10);
        var (opener, openerOut) = _fx.Player("OpenGapOpener", PartyTeardownMap, 7, 10);
        Assert.True(member.StateRank < leaver.StateRank);

        SessionFixture.FormParty(leaver, member);
        Assert.NotNull(PartyField.GetValue(leaver));
        leaverOut.Clear();
        openerOut.Clear();

        var progress = new StallWatch.RoundCounter();
        var memberHeld = new ManualResetEventSlim();
        var releaseMember = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            member.WithState(() => { memberHeld.Set(); releaseMember.Wait(); });
            progress.Bump();
        }) { IsBackground = true, Name = "member-holder" };
        holder.Start();
        Assert.True(memberHeld.Wait(5000));

        var teardown = new Thread(() =>
        {
            leaver.WithState(() => TearDown.Invoke(leaver, null));
            progress.Bump();
        }) { IsBackground = true, Name = "leaver-teardown" };
        teardown.Start();

        // _party is nulled inside RemoveFromParty, under the leaver's monitor, AFTER the teardown's _trade check
        // and BEFORE the broadcast that parks on the member. Seeing it null places the teardown in that span.
        var sw = Stopwatch.StartNew();
        while (PartyField.GetValue(leaver) is not null && sw.ElapsedMilliseconds < 5000) Thread.Yield();
        Assert.Null(PartyField.GetValue(leaver));

        // The open, on its own thread. It needs the leaver's monitor, which the parked teardown has dropped.
        var openDone = new ManualResetEventSlim();
        var open = new Thread(() =>
        {
            opener.Receive(OpenRequest(leaver.PlayerId));
            openDone.Set();
            progress.Bump();
        }) { IsBackground = true, Name = "opener" };
        open.Start();
        Assert.True(openDone.Wait(5000), "the open never got the leaver's monitor");
        Assert.True(teardown.IsAlive, "the teardown got past the party broadcast while the member was held");

        releaseMember.Set();
        StallWatch.RunUntilDoneOrStalled(new[] { teardown, holder }, () => progress.Rounds,
            StallWatch.StallQuiet, StallWatch.StallCap, "the teardown and the member holder");

        Assert.Null(TradeOf(leaver));
        Assert.Null(TradeOf(opener));
        Assert.Empty(openerOut.BodiesOf(ExchangeOut));
        Assert.Equal(Refuse, Assert.Single(MiniTexts(openerOut)));
    }

    /// <summary>
    /// Two players open on the same target at once. The first is parked past every gate, before the writes;
    /// the second is sent in. Exactly one trade opens, both of its sides point at it, and the second opener
    /// gets the refusal and no window.
    ///
    /// <para>Red on the unlocked open: both pass the gates, both write, and the target ends up holding the
    /// first opener's trade while the second opener holds one whose other side does not point back.</para>
    /// </summary>
    [Fact]
    public void TwoInitiatorsRacingForOneTargetOpenExactlyOneTrade()
    {
        var (target, targetOut) = _fx.Player("OpenRaceTarget", RaceMap, 5, 10);
        var (first, firstOut) = _fx.Player("OpenRaceFirst", RaceMap, 6, 10);
        var (second, secondOut) = _fx.Player("OpenRaceSecond", RaceMap, 7, 10);

        var progress = new StallWatch.RoundCounter();
        var firstAtGate = new ManualResetEventSlim();
        var secondAtGate = new ManualResetEventSlim();
        var secondDone = new ManualResetEventSlim();
        Thread? secondThread = null;
        Session.TradeOpenProbeForTest = initiator =>
        {
            if (ReferenceEquals(initiator, second)) { secondAtGate.Set(); return; }
            if (!ReferenceEquals(initiator, first)) return;
            firstAtGate.Set();
            WaitUntil(() => secondAtGate.IsSet || secondDone.IsSet || Parked(Volatile.Read(ref secondThread)));
        };
        try
        {
            var firstThread = new Thread(() => { first.Receive(OpenRequest(target.PlayerId)); progress.Bump(); })
            { IsBackground = true, Name = "first-opener" };
            firstThread.Start();
            Assert.True(firstAtGate.Wait(5000), "the first opener never reached the probe");

            var t2 = new Thread(() => { second.Receive(OpenRequest(target.PlayerId)); secondDone.Set(); progress.Bump(); })
            { IsBackground = true, Name = "second-opener" };
            Volatile.Write(ref secondThread, t2);
            t2.Start();

            StallWatch.RunUntilDoneOrStalled(new[] { firstThread, t2 }, () => progress.Rounds,
                StallWatch.StallQuiet, StallWatch.StallCap, "the two openers");
        }
        finally { Session.TradeOpenProbeForTest = null; }

        var trade = TradeOf(target);
        Assert.NotNull(trade);
        Assert.Same(trade, TradeOf(first));
        Assert.Null(TradeOf(second));
        Assert.Single(targetOut.BodiesOf(ExchangeOut));
        Assert.Single(firstOut.BodiesOf(ExchangeOut));
        Assert.Empty(secondOut.BodiesOf(ExchangeOut));
        Assert.Equal(Refuse, Assert.Single(MiniTexts(secondOut)));
    }

    /// <summary>
    /// Our OWN side changes while the pair has our monitor dropped. The opener ranks above its target, so
    /// #29 rule 2 exits the opener's monitor while it waits for the target's (held here by a third thread). In
    /// that gap a third player opens a trade with the opener and gets it. When the opener's open resumes it
    /// must see that trade and refuse with "You are already trading.", leaving the third player's trade
    /// intact on both sides and the target untouched. Without the re-check inside the body, the opener's
    /// _trade is overwritten and the third player is left holding a trade whose other side points elsewhere.
    /// </summary>
    [Fact]
    public void AnOpenWithUsDuringTheDroppedWindowIsSeenInside()
    {
        // Rank order is creation order: the target below the opener, so the opener's pair descends.
        var (target, targetOut) = _fx.Player("OpenGapOwnTarget", RaceMap, 20, 10);
        var (opener, openerOut) = _fx.Player("OpenGapOwnOpener", RaceMap, 21, 10);
        var (third, _) = _fx.Player("OpenGapOwnThird", RaceMap, 22, 10);
        Assert.True(target.StateRank < opener.StateRank);

        var progress = new StallWatch.RoundCounter();
        var targetHeld = new ManualResetEventSlim();
        var releaseTarget = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            target.WithState(() => { targetHeld.Set(); releaseTarget.Wait(); });
            progress.Bump();
        }) { IsBackground = true, Name = "target-holder" };
        holder.Start();
        Assert.True(targetHeld.Wait(5000));

        var open = new Thread(() => { opener.Receive(OpenRequest(target.PlayerId)); progress.Bump(); })
        { IsBackground = true, Name = "opener" };
        open.Start();
        // Blocked on the target's monitor means the descending pair has already exited the opener's.
        WaitUntil(() => Parked(open));
        Assert.True(Parked(open), "the opener never blocked on the target's monitor");

        third.Receive(OpenRequest(opener.PlayerId));   // lands in the gap: the opener's monitor is free
        var thirds = TradeOf(third);
        Assert.NotNull(thirds);
        Assert.Same(thirds, TradeOf(opener));
        openerOut.Clear();

        releaseTarget.Set();
        StallWatch.RunUntilDoneOrStalled(new[] { open, holder }, () => progress.Rounds,
            StallWatch.StallQuiet, StallWatch.StallCap, "the opener and the target holder");

        Assert.Same(thirds, TradeOf(opener));
        Assert.Same(thirds, TradeOf(third));
        Assert.Null(TradeOf(target));
        Assert.Equal("You are already trading.", Assert.Single(MiniTexts(openerOut)));
        Assert.Empty(openerOut.BodiesOf(ExchangeOut));
        Assert.Empty(targetOut.BodiesOf(ExchangeOut));
    }

    /// <summary>
    /// The target is killed while an open aimed at it is past its gates. No half-open trade survives: under
    /// the pair the death waits for the open, then its own teardown closes both windows, so the initiator sees
    /// its window open and then close with the teardown's line.
    ///
    /// <para>Red on the unlocked open: the whole death runs in the gap, finds no trade to end, and the open
    /// then plants one on a ghost that nothing will ever close.</para>
    /// </summary>
    [Fact]
    public void AnOpenRacingTheTargetsDeathLeavesNoHalfOpenTrade()
    {
        var (victim, _) = _fx.Player("OpenDeathVictim", DeathMap, 5, 10);
        var (opener, openerOut) = _fx.Player("OpenDeathOpener", DeathMap, 6, 10);

        var progress = new StallWatch.RoundCounter();
        var killed = new ManualResetEventSlim();
        Thread? killer = null;
        Session.TradeOpenProbeForTest = initiator =>
        {
            if (!ReferenceEquals(initiator, opener)) return;
            var k = new Thread(() =>
            {
                victim.ReceiveEnvironmentDamage(9999, "a cold tile, in a test");
                killed.Set();
                progress.Bump();
            }) { IsBackground = true, Name = "killer" };
            Volatile.Write(ref killer, k);
            k.Start();
            WaitUntil(() => killed.IsSet || Parked(k));
        };
        try
        {
            opener.Receive(OpenRequest(victim.PlayerId));
            var k = Volatile.Read(ref killer);
            Assert.NotNull(k);
            StallWatch.RunUntilDoneOrStalled(new[] { k! }, () => progress.Rounds,
                StallWatch.StallQuiet, StallWatch.StallCap, "the killer");
        }
        finally { Session.TradeOpenProbeForTest = null; }

        Assert.True(victim.IsDead);
        Assert.Null(TradeOf(victim));
        Assert.Null(TradeOf(opener));
        var window = openerOut.BodiesOf(ExchangeOut);
        Assert.Equal(2, window.Count);
        Assert.Equal(ExcOpen, window[0][0]);
        Assert.Equal(Message(CancelText), window[1]);
    }

    /// <summary>
    /// Every refusal <c>TryStartTrade</c> can give, for the inputs that produce it in normal play, driven through
    /// the real 0x4a open, including the pairs of conditions whose ORDER decides which line comes out. Each
    /// case builds fresh sessions. A refused open sends no 0x42 to anyone and leaves no trade on either side;
    /// the accepted one sends exactly one open frame to each side.
    /// </summary>
    [Fact]
    public void RefusalsComeOutInTheSameOrderWithTheSameText()
    {
        int n = 0;
        (Session s, RecordingOutbound o) P(Action<Character>? shape = null, ushort map = RefusalMap)
        {
            var (s, o, _) = _fx.PlayerWith($"OpenRefusal{++n}", shape ?? (_ => { }), map, (ushort)(5 + n % 40), 10);
            return (s, o);
        }
        void Busy_(Session a, Session b)
        {
            a.Receive(OpenRequest(b.PlayerId));
            Assert.NotNull(TradeOf(a));
        }
        void Expect(Session from, RecordingOutbound fromOut, Session to, RecordingOutbound toOut, string line)
        {
            var mine = TradeOf(from); var theirs = TradeOf(to);
            fromOut.Clear(); toOut.Clear();
            from.Receive(OpenRequest(to.PlayerId));
            Assert.Equal(line, Assert.Single(MiniTexts(fromOut)));
            Assert.Empty(fromOut.BodiesOf(ExchangeOut));
            if (!ReferenceEquals(from, to)) Assert.Empty(toOut.BodiesOf(ExchangeOut));
            Assert.Same(mine, TradeOf(from));
            Assert.Same(theirs, TradeOf(to));
        }

        // Dead initiator: first, ahead of everything, self included.
        { var (a, ao) = P(c => c.Hp = 0); var (b, bo) = P(); Expect(a, ao, b, bo, Spirits); Expect(a, ao, a, ao, Spirits); }
        // Already trading, aimed at someone else or at ourselves: the busy line, ahead of the self line.
        { var (a, ao) = P(); var (x, _) = P(); var (b, bo) = P(); Busy_(a, x); Expect(a, ao, b, bo, Busy); Expect(a, ao, a, ao, Busy); }
        // Self, alive and free.
        { var (a, ao) = P(); Expect(a, ao, a, ao, Bored); }
        // Target on another map.
        { var (a, ao) = P(); var (b, bo) = P(map: ElsewhereMap); Expect(a, ao, b, bo, Refuse); }
        // Target already trading.
        { var (a, ao) = P(); var (b, bo) = P(); var (x, _) = P(); Busy_(b, x); Expect(a, ao, b, bo, Refuse); }
        // Target dead.
        { var (a, ao) = P(); var (b, bo) = P(c => c.Hp = 0); Expect(a, ao, b, bo, Refuse); }
        // Target's Exchange flag off.
        { var (a, ao) = P(); var (b, bo) = P(c => c.Exchange = false); Expect(a, ao, b, bo, Refuse); }

        // And the open itself: one 0x42 sub-0 to each side, naming the other, no status line.
        {
            var (a, ao) = P(); var (b, bo) = P();
            a.Receive(OpenRequest(b.PlayerId));
            Assert.Empty(MiniTexts(ao));
            Assert.Equal(b.PlayerId, OpenedWith(Assert.Single(ao.BodiesOf(ExchangeOut))));
            Assert.Equal(a.PlayerId, OpenedWith(Assert.Single(bo.BodiesOf(ExchangeOut))));
            Assert.Same(TradeOf(a), TradeOf(b));
        }
    }

    private static uint OpenedWith(byte[] body)
    {
        Assert.Equal(ExcOpen, body[0]);
        return (uint)((body[1] << 24) | (body[2] << 16) | (body[3] << 8) | body[4]);
    }
}
