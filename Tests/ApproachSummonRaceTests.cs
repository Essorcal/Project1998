using System.Diagnostics;
using System.Reflection;
using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Summon moves ANOTHER player, so it meets everything else that player can be doing at the same time: an open
/// exchange, a logout, a partner busy on its own thread (PR #325 review, F1-F3). These facts pin how Summon
/// behaves when they overlap.
///
/// <para><b>The window (F1).</b> <c>Session.EnterMap</c> ends the mover's open exchange before it leaves the old
/// map, through <c>WithStatePair</c>. When the trade partner ranks below the mover, that pair takes the
/// partner's monitor first and so drops the mover's (and the caster's) while it waits. A logout that runs to the
/// end in that gap takes the mover off its map; the move then resumed and put the departed session back on the
/// caster's map, where it was found by name and kept writing its row. <c>Session.LuaSummonTarget</c> now ends the
/// exchange itself, under the target's monitor, and checks again for a logout and a new exchange after every
/// end, until neither is there. Only then does it move them, and <c>EnterMap</c>'s own exchange step has nothing
/// left to drop the monitor for. <c>EnterMap</c> is unchanged (#330 fixes it for <c>@bring</c> and Resurrect).</para>
///
/// <para><b>How the interleavings are forced.</b> Threads, real sessions, the real 0x0F cast frame through
/// <c>Session.Receive</c>, and the real <c>TearDownWorldState</c> under the leaver's own monitor (how
/// <c>DepartedSessionFenceTests</c> drives a logout). A cast is "parked" when its thread is blocked with the
/// monitors in the expected state, held for 200 ms: a cast waiting a moment for the process-wide Lua gate looks
/// the same for an instant, but not for that long. Ported from the reviewer's probes N1d, N1e and N2a
/// (<c>reviews/PR325-review-1-scratch/PR325ReviewProbes.final.cs</c>).</para>
///
/// <para>A World of its own (class fixture): the facts find players by name, and leave departed sessions behind.</para>
/// </summary>
public sealed class ApproachSummonRaceTests : IClassFixture<SessionFixture>
{
    // Content-free maps (no Maps.csv row), claimed by no other class; 61313-61332 are ApproachSummonTests'.
    private const ushort BusyHere = 61333, BusyThere = 61334, OwnHere = 61335, OwnThere = 61336;
    private const ushort QuitHere = 61337, QuitThere = 61338, TradeHere = 61339, TradeThere = 61340;
    private const ushort LoopHere = 61341, LoopThere = 61342;   // + 2 per case: 61345/61346 and 61347/61348

    private const int SummonSlot = 1;
    private const uint StartMp = 100;

    private static readonly FieldInfo TradeField =
        typeof(Session).GetField("_trade", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo StateField =
        typeof(Session).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo TearDown =
        typeof(Session).GetMethod("TearDownWorldState", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo StartTrade =
        typeof(Session).GetMethod("TryStartTrade", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo EndTrade =
        typeof(Session).GetMethod("EndTrade", BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly SessionFixture _fx;

    public ApproachSummonRaceTests(SessionFixture fx) => _fx = fx;

    // ---- F1: the exchange window -----------------------------------------------------------------------

    /// <summary>
    /// <b>A summon parked on a busy partner does not put a logged-out target back on a map</b> (the review's
    /// N1e, forced). Ranks partner &lt; target &lt; caster, the order the three log in. The target has an
    /// exchange open with the partner, whose own thread holds its monitor. The summon reaches the exchange and
    /// parks on the partner, which drops the target's monitor and the caster's. In that gap the partner cancels
    /// the exchange and the target logs out, all the way: off its map, departed, saved. Then the partner lets go.
    /// <para>The summon must see the logout and refuse: "Fizzle.", nothing spent, nobody on any map.</para>
    /// <para>Red on 72c6e13: "Expected (0, False, Fizzle., 100) / Actual (1, True, You cast Summon., 70)" — the
    /// departed target back on the caster's map, found by name, and 30 mana charged for it.</para>
    /// </summary>
    [Fact]
    public void ASummonParkedOnABusyPartnerDoesNotPutALoggedOutTargetBackOnAMap()
    {
        var (p, _, _) = Make("BusyP", BusyThere, 9, 10);
        var (t, _, tc) = Make("BusyT", BusyThere, 10, 10);
        var (c, co, cc) = Make("BusyC", BusyHere, 5, 5, caster: true);
        Assert.True(p.StateRank < t.StateRank && t.StateRank < c.StateRank, "the window needs partner < target < caster");
        Group(c, t);
        object trade = OpenTrade(t, p);
        co.Clear();

        using var pHeld = new ManualResetEventSlim();
        using var cancelNow = new ManualResetEventSlim();
        using var cancelled = new ManualResetEventSlim();
        using var releaseP = new ManualResetEventSlim();
        var partner = Run("partner", () => p.WithState(() =>
        {
            pHeld.Set();
            cancelNow.Wait();
            EndTrade.Invoke(null, new object[] { trade, "Exchange cancelled.", false });   // the partner's cancel
            cancelled.Set();
            releaseP.Wait();
        }));
        Assert.True(pHeld.Wait(5000), "the partner never took its monitor");

        var cast = Run("caster", () => Cast(c, SummonSlot, tc.Name));
        Assert.True(Stable(() => Parked(cast) && MonitorFree(t) && MonitorFree(c)),
                    "the summon never parked on the partner with the target's and the caster's monitors dropped");

        cancelNow.Set();
        Assert.True(cancelled.Wait(5000), "the partner's cancel did not finish");
        var logout = Run("logout", () => t.WithState(() => TearDown.Invoke(t, null)));
        Assert.True(logout.Join(5000), "the logout did not finish while the summon was parked");
        Assert.Equal(0, MapsHolding(t));   // the logout took the target off its map

        releaseP.Set();
        Assert.True(cast.Join(5000) && partner.Join(5000), "deadlock: the summon or the partner never finished");

        Assert.Equal((0, false, "Fizzle.", StartMp),
                     (MapsHolding(t), _fx.World.Online.FindPlayer(tc.Name) is not null,
                      string.Join("|", CastLines(co)), cc.Mp));
    }

    /// <summary>
    /// <b>The same with no busy partner</b>: the partner's thread is idle. The target's own thread is mid-packet,
    /// holding its monitor, so the summon passes its checks and waits on the target. The target then disconnects:
    /// its logout ends the open exchange (with the idle partner) and takes it off its map.
    /// <para>The summon must refuse: "Fizzle.", nothing spent, the target on no map, the exchange closed for both.</para>
    /// <para>Red on 72c6e13, by one of two routes. The logout's own exchange step drops the target's monitor for a
    /// moment; if the waiting summon takes it there, it reaches the exchange itself, parks on the partner (held now
    /// by the logout) and later moves the departed target: the F1 window, opened by the target's own thread. If
    /// it does not, 72c6e13's check refuses the move but has already charged 30 and said "You cast Summon." (F3).</para>
    /// </summary>
    [Fact]
    public void ASummonWaitingOnATraderWhoLogsOutMovesNobodyAndCostsNothing()
    {
        var (p, po, _) = Make("OwnP", OwnThere, 9, 10);
        var (t, _, tc) = Make("OwnT", OwnThere, 10, 10);
        var (c, co, cc) = Make("OwnC", OwnHere, 5, 5, caster: true);
        Group(c, t);
        OpenTrade(t, p);
        co.Clear(); po.Clear();

        using var tHeld = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        var own = Run("target", () => t.WithState(() =>
        {
            tHeld.Set();
            go.Wait();
            TearDown.Invoke(t, null);   // the disconnect: ends the exchange first, then leaves the map
        }));
        Assert.True(tHeld.Wait(5000), "the target's thread never took its monitor");

        var cast = Run("caster", () => Cast(c, SummonSlot, tc.Name));
        Assert.True(Stable(() => Parked(cast) && MonitorFree(c) && !MonitorFree(t)),
                    "the summon never parked on the target's monitor");

        go.Set();
        Assert.True(cast.Join(5000) && own.Join(5000), "deadlock: the summon or the logout never finished");

        Assert.Equal((0, false, "Fizzle.", StartMp),
                     (MapsHolding(t), _fx.World.Online.FindPlayer(tc.Name) is not null,
                      string.Join("|", CastLines(co)), cc.Mp));
        Assert.Equal((false, false), (TradeField.GetValue(t) is not null, TradeField.GetValue(p) is not null));
        Assert.Equal(new[] { "Exchange cancelled." }, ExchangeBoxes(po));
    }

    /// <summary>
    /// <b>The loop's bound.</b> Every time Summon ends the target's exchange, the seam re-opens one with the same
    /// partner, standing in for an exchange that opened while the end had the target's monitor dropped. With two
    /// re-opens Summon ends all three exchanges and moves the target. With three it finds a fourth open after
    /// <see cref="Session.SummonExchangeEndsMax"/> ends and refuses: "Fizzle.", nothing spent, nobody moved, and that
    /// last exchange left open. Either way it terminates, and the partner sees one "Exchange cancelled." per end.
    /// </summary>
    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void ASummonEndsAtMostThreeExchangesBeforeItRefuses(int reopens, bool moves)
    {
        var (p, po, _) = Make($"LoopP{reopens}", (ushort)(LoopThere + 2 * reopens), 9, 10);
        var (t, _, tc) = Make($"LoopT{reopens}", (ushort)(LoopThere + 2 * reopens), 10, 10);
        var (c, co, cc) = Make($"LoopC{reopens}", (ushort)(LoopHere + 2 * reopens), 5, 5, caster: true);
        Group(c, t);
        OpenTrade(t, p);
        co.Clear(); po.Clear();
        int reopened = 0;
        Session.SummonExchangeEndedProbeForTest = (target, ended) =>
        {
            if (!ReferenceEquals(target, t) || reopened == reopens) return;
            reopened++;
            OpenTrade(t, p);
        };
        try { Cast(c, SummonSlot, tc.Name); }
        finally { Session.SummonExchangeEndedProbeForTest = null; }

        Assert.Equal(reopens, reopened);
        Assert.Equal(3, ExchangeBoxes(po).Count(b => b == "Exchange cancelled."));
        if (moves)
        {
            Assert.Equal(((ushort)(LoopHere + 2 * reopens), "You cast Summon.", StartMp - 30, false),
                         (tc.Map, string.Join("|", MiniTexts(co)), cc.Mp, TradeField.GetValue(t) is not null));
        }
        else
        {
            Assert.Equal(((ushort)(LoopThere + 2 * reopens), "Fizzle.", StartMp, true),
                         (tc.Map, string.Join("|", MiniTexts(co)), cc.Mp, TradeField.GetValue(t) is not null));
        }
    }

    // ---- F3: a refusal after the checks costs nothing ------------------------------------------------

    /// <summary>
    /// <b>A target who logs out after the checks leaves the caster's mana alone</b> (the review's N2a). No
    /// exchange this time. The target's thread holds its monitor; the summon passes every check and waits on it;
    /// the target logs out. The move is refused, so the cast is: "Fizzle." and no mana, like every other refusal.
    /// <para>Red on 72c6e13: "Expected (Fizzle., 100, 0) / Actual (You cast Summon., 70, 0)" — the verb charged
    /// and played the cast before a move that never happened.</para>
    /// </summary>
    [Fact]
    public void ATargetWhoLogsOutAfterTheChecksCostsTheCasterNothing()
    {
        var (t, _, tc) = Make("QuitT", QuitThere, 10, 10);
        var (c, co, cc) = Make("QuitC", QuitHere, 5, 5, caster: true);
        Group(c, t);
        co.Clear();

        using var tHeld = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        var own = Run("target", () => t.WithState(() => { tHeld.Set(); go.Wait(); TearDown.Invoke(t, null); }));
        Assert.True(tHeld.Wait(5000), "the target's thread never took its monitor");

        var cast = Run("caster", () => Cast(c, SummonSlot, tc.Name));
        Assert.True(Stable(() => Parked(cast) && MonitorFree(c) && !MonitorFree(t)),
                    "the summon never parked on the target's monitor");

        go.Set();
        Assert.True(cast.Join(5000) && own.Join(5000), "deadlock: the summon or the logout never finished");

        Assert.Equal(("Fizzle.", StartMp, 0), (string.Join("|", CastLines(co)), cc.Mp, MapsHolding(t)));
    }

    // ---- F2: summoning a trader --------------------------------------------------------------------

    /// <summary>
    /// <b>Summoning someone mid-exchange cancels the exchange for both traders</b>, and each sees the client's
    /// "Exchange cancelled." box, the line every other cancelled exchange gets. The partner is not in the group
    /// and is not moved. The rule is #57's: any move ends an exchange. Whether Summon should instead fizzle on a
    /// trader is Caleb's call (PR #325); this pins the behaviour as shipped.
    /// </summary>
    [Fact]
    public void SummoningATraderCancelsTheExchangeForBothTraders()
    {
        var (p, po, pc) = Make("TradeP", TradeThere, 9, 10);
        var (t, to, tc) = Make("TradeT", TradeThere, 10, 10);
        var (c, co, cc) = Make("TradeC", TradeHere, 5, 5, caster: true);
        Group(c, t);
        OpenTrade(t, p);
        co.Clear(); to.Clear(); po.Clear();

        Cast(c, SummonSlot, tc.Name);

        Assert.Equal((TradeHere, (ushort)5, (ushort)4), (tc.Map, tc.X, tc.Y));
        Assert.Equal((TradeThere, (ushort)9, (ushort)10), (pc.Map, pc.X, pc.Y));
        Assert.Equal((false, false), (TradeField.GetValue(t) is not null, TradeField.GetValue(p) is not null));
        Assert.Equal(new[] { "Exchange cancelled." }, ExchangeBoxes(to));
        Assert.Equal(new[] { "Exchange cancelled." }, ExchangeBoxes(po));
        Assert.Equal(("You cast Summon.", StartMp - 30), (string.Join("|", MiniTexts(co)), cc.Mp));
    }

    // ---- fixture -------------------------------------------------------------------------------------

    /// <summary>A grouping-willing, exchange-willing Koguryo level 30 on a 20x20 content-free map, 100 mana; a
    /// caster also knows Approach (slot 0) and Summon (slot 1). Sessions made in order rank in order.</summary>
    private (Session s, RecordingOutbound o, Character c) Make(string name, ushort map, ushort x, ushort y,
                                                              bool caster = false)
    {
        var r = _fx.PlayerWith(name, ch =>
        {
            ch.MapXs = 20; ch.MapYs = 20;
            ch.Level = 30; ch.Nation = 1;
            ch.Grouped = true; ch.Exchange = true;
            ch.MaxMp = StartMp; ch.Mp = StartMp;
            if (caster)
                ch.Spells = new List<int> { Content.SpellByKey("approach_spell")!.Id, Content.SpellByKey("summon_spell")!.Id };
        }, map, x, y);
        r.outbound.Clear();
        return r;
    }

    /// <summary>The real 0x2E invite, and the two must then share one party, or every refusal would be the group's.</summary>
    private static void Group(Session leader, Session member)
    {
        SessionFixture.FormParty(leader, member);
        Assert.NotNull(PartyOf(member));
        Assert.Same(PartyOf(leader), PartyOf(member));
    }

    private static readonly FieldInfo PartyField =
        typeof(Session).GetField("_party", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static object? PartyOf(Session s) => PartyField.GetValue(s);

    /// <summary>Open an exchange between two players on one map, the way the exchange packet does, and return it.</summary>
    private static object OpenTrade(Session initiator, Session other)
    {
        initiator.WithState(() => StartTrade.Invoke(initiator, new object[] { other }));
        var trade = TradeField.GetValue(initiator);
        Assert.True(trade is not null && ReferenceEquals(trade, TradeField.GetValue(other)), "the exchange did not open");
        return trade!;
    }

    /// <summary>The real 0x0F cast frame for a typed-answer spell: <c>slot+1</c>, the answer, a NUL.</summary>
    private static void Cast(Session caster, int slot, string answer)
    {
        byte[] text = Encoding.ASCII.GetBytes(answer);
        byte[] body = new byte[text.Length + 2];
        body[0] = (byte)(slot + 1);
        text.CopyTo(body, 1);
        caster.Receive(SessionFixture.Frame(ClientOp.Cast, body));
    }

    private static List<string> MiniTexts(RecordingOutbound o)
    {
        var lines = new List<string>();
        foreach (var body in o.BodiesOf(ServerOp.MiniText))
            if (body.Length >= 3) lines.Add(Encoding.ASCII.GetString(body, 3, body.Length - 3));
        return lines;
    }

    /// <summary>The caster's mini-text lines minus the group notices a target's logout sends them ("X is leaving
    /// the group.", "Your group has disbanded." and the "Join a group" toggle line). No cast line mentions a group,
    /// so whatever the cast itself said, including an unexpected line, is still here.</summary>
    private static List<string> CastLines(RecordingOutbound o) =>
        MiniTexts(o).Where(l => !l.Contains("group", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The text of every exchange message box (0x42 sub-4: <c>4, 0, len(u8), text</c>).</summary>
    private static List<string> ExchangeBoxes(RecordingOutbound o)
    {
        var boxes = new List<string>();
        foreach (var body in o.BodiesOf(ServerOp.Exchange))
            if (body.Length >= 3 && body[0] == 4 && body.Length >= 3 + body[2])
                boxes.Add(Encoding.ASCII.GetString(body, 3, body[2]));
        return boxes;
    }

    /// <summary>How many maps' player lists hold <paramref name="s"/>.</summary>
    private int MapsHolding(Session s) => _fx.World.Online.All().Count(p => ReferenceEquals(p, s));

    private static bool Parked(Thread t) => (t.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0;

    private static bool MonitorFree(Session s)
    {
        var gate = StateField.GetValue(s)!;
        if (!Monitor.TryEnter(gate)) return false;
        Monitor.Exit(gate);
        return true;
    }

    /// <summary>True once <paramref name="cond"/> has held without a break for 200 ms; false after 10 s.</summary>
    private static bool Stable(Func<bool> cond)
    {
        var total = Stopwatch.StartNew();
        Stopwatch? held = null;
        while (total.ElapsedMilliseconds < 10_000)
        {
            if (cond()) { held ??= Stopwatch.StartNew(); if (held.ElapsedMilliseconds >= 200) return true; }
            else held = null;
            Thread.Sleep(1);
        }
        return false;
    }

    private static Thread Run(string name, Action body)
    {
        var t = new Thread(() => body()) { IsBackground = true, Name = name };
        t.Start();
        return t;
    }
}
