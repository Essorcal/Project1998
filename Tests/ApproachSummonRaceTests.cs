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
/// left to drop the monitor for.</para>
///
/// <para><b>#330: the same window for <c>@bring</c> and Resurrect.</b> Both move another player through
/// <c>EnterMap</c> with no check of their own, so a target who logged out after the lookup, or inside
/// <c>EnterMap</c>'s exchange step, was put back on a map. <c>EnterMap</c> now refuses a player whose
/// <c>_leaving</c> is set, after its exchange step, and says nobody moved; <c>@bring</c> then answers "'X' isn't
/// online.". Resurrect (<c>Session.ReviveAt</c>) reads the flag before its heal, with the exchange step taken
/// before that read, so a raced Resurrect neither moves the target nor writes a revived row. The four facts in
/// that section force both ways for both moves with this harness.</para>
///
/// <para><b>How the interleavings are forced.</b> Threads, real sessions, the real 0x0F cast frame through
/// <c>Session.Receive</c>, and the real <c>TearDownWorldState</c> under the leaver's own monitor (how
/// <c>DepartedSessionFenceTests</c> drives a logout). A cast is "parked" when, for 200 ms without a break, its
/// thread is blocked, the monitors are in the expected state, and the caster's player target is the target
/// (<see cref="Resolved"/>). Ported from the reviewer's probes N1d, N1e and N2a
/// (<c>reviews/PR325-review-1-scratch/PR325ReviewProbes.final.cs</c>).</para>
///
/// <para><b>Why the player target (the PR #325 re-check's F6).</b> A cast waiting for the process-wide Lua gate
/// holds no monitor (the gate's slow path drops them) and has not touched the target, so it meets the other two
/// conditions for as long as another test holds the gate. The facts then ran their logout before the cast
/// reached the window, the name lookup failed, and the verb's "Fizzle." at no cost passed them whatever Summon
/// did. Only the verb sets the player target, inside the gate, and each fact's caster is a new session, so a
/// cast that has not got the gate is never parked; a busy gate only delays the park.</para>
///
/// <para><b>Collection "world", on a World of its own.</b> The bound fact sets a static test hook
/// (<see cref="Session.SummonExchangeEndedProbeForTest"/>), which would fire for every session in the process, and
/// the constructor loads the staff roster for the <c>@bring</c> facts, which is one for the process too, so the
/// class runs in <c>"world"</c>, the collection that owns those hooks and the roster
/// (<see cref="TestSeamCollectionTests"/>). It is not one of the collections that run alone: with the check
/// above, what runs beside it can delay a fact but not pass it. The <see cref="SessionFixture"/> is still a class
/// fixture, which xunit hands the class before the collection's: the facts find players by name, and leave
/// departed sessions behind.</para>
/// </summary>
[Collection("world")]
public sealed class ApproachSummonRaceTests : IClassFixture<SessionFixture>
{
    // Content-free maps (no Maps.csv row), claimed by no other class; 61313-61332 are ApproachSummonTests'.
    private const ushort BusyHere = 61333, BusyThere = 61334, OwnHere = 61335, OwnThere = 61336;
    private const ushort QuitHere = 61337, QuitThere = 61338, TradeHere = 61339, TradeThere = 61340;
    private const ushort LoopHere = 61341, LoopThere = 61342;   // + 2 per case: 61345/61346 and 61347/61348
    // #330's @bring facts: content-free too, and claimed by no other class.
    private const ushort BringQuitHere = 61354, BringQuitThere = 61355, BringBusyHere = 61356, BringBusyThere = 61357;
    // #330's Resurrect facts need REAL maps: ReviveAt moves the target only onto a map Content.Maps knows, and on a
    // content-free one it heals in place, where EnterMap is never reached. Northern Pass (1003) and Islets (1008):
    // outdoors, not PvP, casting allowed, and named by no other test class. This class's World is its own (the class
    // fixture below), so nothing here is left standing on them for anyone else.
    private const ushort ResQuitMap = 1003, ResBusyMap = 1008;
    // The same class through ReviveInPlace (PR #358 review, R5): @rez <name> and a direct call on a departed ghost.
    // Content-free, claimed by no other class: ReviveInPlace does not move anyone, so no real map is needed.
    private const ushort RezHere = 61359, RezThere = 61360, RezGoneMap = 61361;

    private const int SummonSlot = 1;
    private const uint StartMp = 100;
    private const uint PoetMp = 10_000;
    private const string ResurrectKey = "resurrect_poet";

    /// <summary>The staff name. Same roster file and content every GM-command class writes (see
    /// <see cref="ResolveOnlinePlayerTests"/>), so the run order of the classes cannot demote anyone.</summary>
    private const string GmName = "cmdgm";

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

    public ApproachSummonRaceTests(SessionFixture fx)
    {
        _fx = fx;
        // The @bring facts run a GM command. The roster is one for the whole process, which is why this class
        // runs in "world", the collection that owns it (ApproachSummonTests writes it the same way).
        lock (TestProcessState.Gate)
        {
            Directory.CreateDirectory(TestProcessState.StateDirectory);
            File.WriteAllText(Path.Combine(TestProcessState.StateDirectory, "gm_accounts.txt"), GmName + "\n");
            StaffAccounts.Load();
        }
    }

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
        Assert.True(Stable(() => Parked(cast) && Resolved(c, t) && MonitorFree(t) && MonitorFree(c)),
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
    /// <para>Red on 72c6e13 through the charge alone: its check refuses the move but has already taken 30 and said
    /// "You cast Summon." (F3). It cannot leave a departed target on a map there: the logout sets <c>_leaving</c>
    /// before its own exchange step drops the target's monitor, so a summon that slips in at that step refuses too.
    /// With an idle partner, 72c6e13's ghost needs the caster's thread descheduled for a whole logout, between
    /// dropping the target's monitor and taking the free partner's, inside <c>EnterState</c>; no test can force
    /// that, and the fix's re-check covers it exactly as it covers the forced case above.</para>
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
        Assert.True(Stable(() => Parked(cast) && Resolved(c, t) && MonitorFree(c) && !MonitorFree(t)),
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
        Assert.True(Stable(() => Parked(cast) && Resolved(c, t) && MonitorFree(c) && !MonitorFree(t)),
                    "the summon never parked on the target's monitor");

        go.Set();
        Assert.True(cast.Join(5000) && own.Join(5000), "deadlock: the summon or the logout never finished");

        Assert.Equal(("Fizzle.", StartMp, 0), (string.Join("|", CastLines(co)), cc.Mp, MapsHolding(t)));
    }

    // ---- F2: summoning a trader --------------------------------------------------------------------

    /// <summary>
    /// <b>Summoning someone mid-exchange cancels the exchange for both traders</b>, and each sees the client's
    /// "Exchange cancelled." box, the line every other cancelled exchange gets. The partner is not in the group
    /// and is not moved. The rule is #57's: any move ends an exchange. Caleb kept this at PR #325's merge
    /// (2026-10-06), rather than have Summon fizzle on a trader; this pins it.
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

    // ---- #330: @bring and Resurrect ------------------------------------------------------------------

    /// <summary>
    /// <b>A <c>@bring</c> waiting on a target who logs out leaves them off every map</b> (#330, the first way).
    /// The target's own thread is mid-packet, holding its monitor. The GM's <c>@bring</c> finds them by name and
    /// waits on that monitor inside <c>EnterMap</c>. The target then disconnects: the real teardown takes them off
    /// their map, departed and saved. The <c>@bring</c> must move nobody, and say what the lookup would now say.
    /// <para>Red on 6de1d24 in Debug and Release: "Expected (0, False) / Actual (1, True)", the departed target back
    /// on the GM's map and found by name. Red the same way with <c>EnterMap</c>'s check disabled.</para>
    /// <para>The "no Brought line" check reads the pane, where the command's confirmation goes (<c>Reply</c>, 0x0A).
    /// It read the 0x0D speech frames before, where that line never goes, so it could not fail (PR #358's review, F2).
    /// Red in Debug and Release with the confirmation forced through on the refused path.</para>
    /// </summary>
    [Fact]
    public void ABringWaitingOnATargetWhoLogsOutLeavesThemOffEveryMap()
    {
        var (t, to, tc) = Make("BringQuitT", BringQuitThere, 10, 10);
        var (gm, gmo) = Gm(BringQuitHere, 5, 5);
        Assert.True(t.StateRank < gm.StateRank, "the target was made first");

        using var tHeld = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        var own = Run("target", () => t.WithState(() => { tHeld.Set(); go.Wait(); TearDown.Invoke(t, null); }));
        Assert.True(tHeld.Wait(5000), "the target's thread never took its monitor");

        // Waiting on the target's monitor is the only place this command can park: the GM's own monitor is free
        // (the descending acquisition dropped it) and the target's is held.
        var bring = Run("gm", () => gm.Receive(ChatFrame($"@bring {tc.Name}")));
        Assert.True(Stable(() => Parked(bring) && MonitorFree(gm) && !MonitorFree(t)),
                    "the @bring never parked on the target's monitor");

        go.Set();
        Assert.True(bring.Join(5000) && own.Join(5000), "deadlock: the @bring or the logout never finished");

        Assert.Equal((0, false), (MapsHolding(t), _fx.World.Online.FindPlayer(tc.Name) is not null));
        Assert.Equal((BringQuitThere, (ushort)10, (ushort)10), (tc.Map, tc.X, tc.Y));
        Assert.Contains(Speech(gmo), s => s.Contains($"'{tc.Name}' isn't online.", StringComparison.Ordinal));
        Assert.DoesNotContain(MiniTexts(gmo), l => l.Contains("Brought", StringComparison.Ordinal));   // Reply's pane line
        Assert.DoesNotContain(MiniTexts(to), l => l.Contains("summoned", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>A <c>@bring</c> parked on a busy partner does not put a logged-out target back on a map</b> (#330, the
    /// second way: the teardown runs inside <c>EnterMap</c>'s exchange step). Ranks partner &lt; target &lt; GM.
    /// The target has an exchange open with the partner, whose own thread holds its monitor. <c>EnterMap</c>'s
    /// exchange step parks on the partner, which drops the target's monitor and the GM's. In that gap the partner
    /// cancels the exchange and the target logs out, all the way. Then the partner lets go.
    /// <para>Red on 6de1d24 in Debug and Release: "Expected (0, False) / Actual (1, True)", the step resumed and the
    /// move went on. Red the same way with <c>EnterMap</c>'s check moved BEFORE its exchange step, which is why it
    /// sits after it.</para>
    /// <para>The "no Brought line" check reads the pane, as in
    /// <see cref="ABringWaitingOnATargetWhoLogsOutLeavesThemOffEveryMap"/>: red in Debug and Release with the
    /// confirmation forced through on the refused path.</para>
    /// </summary>
    [Fact]
    public void ABringParkedOnABusyPartnerDoesNotPutALoggedOutTargetBackOnAMap()
    {
        var (p, _, _) = Make("BringBusyP", BringBusyThere, 9, 10);
        var (t, _, tc) = Make("BringBusyT", BringBusyThere, 10, 10);
        var (gm, gmo) = Gm(BringBusyHere, 5, 5);
        Assert.True(p.StateRank < t.StateRank && t.StateRank < gm.StateRank, "the window needs partner < target < GM");
        object trade = OpenTrade(t, p);

        using var busy = BusyPartner(p, trade);
        // Both monitors dropped while the GM's thread waits: only a descending acquisition does that, and the one
        // that waits here is the pair the exchange step takes for the partner.
        var bring = Run("gm", () => gm.Receive(ChatFrame($"@bring {tc.Name}")));
        Assert.True(Stable(() => Parked(bring) && MonitorFree(t) && MonitorFree(gm)),
                    "the @bring never parked on the partner with the target's and the GM's monitors dropped");

        busy.CancelNow.Set();
        Assert.True(busy.Cancelled.Wait(5000), "the partner's cancel did not finish");
        var logout = Run("logout", () => t.WithState(() => TearDown.Invoke(t, null)));
        Assert.True(logout.Join(5000), "the logout did not finish while the @bring was parked");
        Assert.Equal(0, MapsHolding(t));   // the logout took the target off its map

        busy.Release.Set();
        Assert.True(bring.Join(5000) && busy.Thread.Join(5000), "deadlock: the @bring or the partner never finished");
        Assert.Null(busy.Fault);

        Assert.Equal((0, false), (MapsHolding(t), _fx.World.Online.FindPlayer(tc.Name) is not null));
        Assert.Equal((BringBusyThere, (ushort)10, (ushort)10), (tc.Map, tc.X, tc.Y));
        Assert.Contains(Speech(gmo), s => s.Contains($"'{tc.Name}' isn't online.", StringComparison.Ordinal));
        Assert.DoesNotContain(MiniTexts(gmo), l => l.Contains("Brought", StringComparison.Ordinal));   // Reply's pane line
    }

    /// <summary>
    /// <b>A Resurrect waiting on a ghost who logs out heals nobody, moves nobody and saves no revived row</b>
    /// (#330, the first way). The ghost's own thread holds its monitor; the Poet's Resurrect, aimed at the ghost's
    /// id, resolves them, pays, and waits on that monitor in <c>ReviveAt</c>. The ghost disconnects: off the map,
    /// the corpse saved. The revive must find them leaving before its heal.
    /// <para>The caster's cost is unchanged and pinned here: the verb pays the mana and arms the cooldown before
    /// it calls the move, so a raced Resurrect still costs both and still says "You cast Resurrect.". Making it
    /// free is not approved.</para>
    /// <para>Red on 6de1d24 in Debug and Release: "Expected (0, False, 0, 0) / Actual (1, True, 50, 50)", back on
    /// the map, found by name, healed in memory and the healed row on disk. Red with <c>ReviveAt</c>'s own check
    /// disabled (<c>EnterMap</c>'s alone): "(0, False, 50, 0)", nobody moved and nothing saved, but the heal stayed on
    /// the departed session, which is what the next login's fence writes.</para>
    /// </summary>
    [Fact]
    public void AResurrectWaitingOnAGhostWhoLogsOutRevivesAndMovesNobody()
    {
        var (t, _, tc) = Make("ResQuitT", ResQuitMap, 10, 10, ghost: true);
        var (c, co, cc) = Poet("ResQuitC", ResQuitMap, 12, 10);
        Assert.True(t.StateRank < c.StateRank, "the ghost was made first");

        using var tHeld = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        var own = Run("target", () => t.WithState(() => { tHeld.Set(); go.Wait(); TearDown.Invoke(t, null); }));
        Assert.True(tHeld.Wait(5000), "the ghost's thread never took its monitor");

        var cast = Run("caster", () => c.Receive(SpellCastSupport.CastFrame(0, t.PlayerId)));
        Assert.True(Stable(() => Parked(cast) && Resolved(c, t) && MonitorFree(c) && !MonitorFree(t)),
                    "the Resurrect never parked on the ghost's monitor");

        go.Set();
        Assert.True(cast.Join(5000) && own.Join(5000), "deadlock: the Resurrect or the logout never finished");

        AssertNotRevived(t, tc, ResQuitMap);
        AssertPaid(c, co, cc);
    }

    /// <summary>
    /// <b>A Resurrect parked on a busy partner heals nobody, moves nobody and saves no revived row</b> (#330, the
    /// second way). A ghost holds an exchange only inside <c>Die()</c>, between Hp reaching 0 and its last statement,
    /// the exchange's end, after a peer resync that can drop the monitor; the fact puts the target there directly:
    /// the exchange opened while alive, then Hp 0 under its monitor. Ranks partner &lt; ghost &lt; Poet. The
    /// Resurrect's exchange step parks on the busy partner with the ghost's and the Poet's monitors dropped; the
    /// partner cancels, the ghost logs out (the corpse saved), the partner lets go.
    /// <para>Red on 6de1d24 in Debug and Release: "Expected (0, False, 0, 0) / Actual (1, True, 50, 50)". Red without
    /// <c>ReviveAt</c>'s own exchange step ahead of its check: "(0, False, 50, 50)": <c>EnterMap</c> refused the move,
    /// but the heal came before the gap, so the logout's save wrote the HEALED row. Red without <c>ReviveAt</c>'s
    /// check: "(0, False, 50, 0)".</para>
    /// </summary>
    [Fact]
    public void AResurrectParkedOnABusyPartnerRevivesAndMovesNobody()
    {
        var (p, _, _) = Make("ResBusyP", ResBusyMap, 9, 10);
        var (t, _, tc) = Make("ResBusyT", ResBusyMap, 10, 10);
        var (c, co, cc) = Poet("ResBusyC", ResBusyMap, 12, 10);
        Assert.True(p.StateRank < t.StateRank && t.StateRank < c.StateRank, "the window needs partner < ghost < Poet");
        object trade = OpenTrade(t, p);
        t.WithState(() => { tc.Hp = 0; });   // Die()'s window: a ghost, the exchange not yet ended
        Assert.True(t.IsDead && TradeField.GetValue(t) is not null, "the target is not a ghost with an exchange open");

        using var busy = BusyPartner(p, trade);
        var cast = Run("caster", () => c.Receive(SpellCastSupport.CastFrame(0, t.PlayerId)));
        Assert.True(Stable(() => Parked(cast) && Resolved(c, t) && MonitorFree(t) && MonitorFree(c)),
                    "the Resurrect never parked on the partner with the ghost's and the Poet's monitors dropped");

        busy.CancelNow.Set();
        Assert.True(busy.Cancelled.Wait(5000), "the partner's cancel did not finish");
        var logout = Run("logout", () => t.WithState(() => TearDown.Invoke(t, null)));
        Assert.True(logout.Join(5000), "the logout did not finish while the Resurrect was parked");
        Assert.Equal(0, MapsHolding(t));

        busy.Release.Set();
        Assert.True(cast.Join(5000) && busy.Thread.Join(5000), "deadlock: the Resurrect or the partner never finished");
        Assert.Null(busy.Fault);

        AssertNotRevived(t, tc, ResBusyMap);
        AssertPaid(c, co, cc);
    }

    /// <summary>
    /// <b>A <c>@rez</c> waiting on a ghost who logs out revives nobody and saves no revived row</b> (#330's class through
    /// <c>ReviveInPlace</c>; PR #358 review, R5; Caleb, 2026-10-21). The ghost's own thread holds its monitor; the GM's
    /// <c>@rez &lt;name&gt;</c> finds them by name and waits on that monitor inside <c>ReviveInPlace</c>; the ghost
    /// disconnects (the real teardown, off the map, the corpse saved). The revive must find them leaving before its
    /// heal: still a ghost in memory and in the row, nothing sent to them, and the GM reads the lookup's own line.
    /// <para>Red on 34c9c93 in Debug and Release: "Expected (0, False, 0, 0) / Actual (0, False, 50, 50)", the departed
    /// ghost healed in memory and the healed row on disk. Red the same way with the check removed; with the check moved
    /// after the heal, "(0, False, 50, 0)", the heal left on the departed session; with <c>@rez</c> ignoring the answer,
    /// on the GM's line alone.</para>
    /// </summary>
    [Fact]
    public void ARezWaitingOnAGhostWhoLogsOutRevivesNobody()
    {
        var (t, to, tc) = Make("RezQuitT", RezThere, 10, 10, ghost: true);
        var (gm, gmo) = Gm(RezHere, 5, 5);
        Assert.True(t.StateRank < gm.StateRank, "the ghost was made first");

        using var tHeld = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        var own = Run("target", () => t.WithState(() => { tHeld.Set(); go.Wait(); TearDown.Invoke(t, null); }));
        Assert.True(tHeld.Wait(5000), "the ghost's thread never took its monitor");

        var rez = Run("gm", () => gm.Receive(ChatFrame($"@rez {tc.Name}")));
        Assert.True(Stable(() => Parked(rez) && MonitorFree(gm) && !MonitorFree(t)),
                    "the @rez never parked on the ghost's monitor");
        to.Clear();

        go.Set();
        Assert.True(rez.Join(5000) && own.Join(5000), "deadlock: the @rez or the logout never finished");

        var stored = _fx.Store.Load(tc.Name);
        Assert.Equal(CharacterLoadStatus.Ok, stored.Status);
        Assert.Equal((0, false, 0u, 0u), (MapsHolding(t), _fx.World.Online.FindPlayer(tc.Name) is not null,
                                          tc.Hp, stored.Character!.Hp));
        Assert.Empty(MiniTexts(to));
        Assert.Contains(Speech(gmo), s => s.Contains($"'{tc.Name}' isn't online.", StringComparison.Ordinal));
        Assert.DoesNotContain(MiniTexts(gmo), l => l.Contains("Restored", StringComparison.Ordinal));   // Reply's pane line
    }

    /// <summary>
    /// <b><c>ReviveInPlace</c> on a ghost who has already logged out heals nothing and saves nothing</b>: R5's shape, the
    /// call made straight on the departed session (as a caller that looked them up before the logout would make it).
    /// The ghost (500 vita) dies for real, logs out (the corpse row saved), and the revive is asked for.
    /// <para>Red on 34c9c93 in Debug and Release: "Expected (True, 0, 0) / Actual (False, 500, 500)", healed in memory
    /// and the row rewritten from Hp 0 to 500, as the review's probe found. Red the same way with the check removed, and
    /// "(False, 500, 0)" with it moved after the heal.</para>
    /// </summary>
    [Fact]
    public void ReviveInPlaceOnADepartedGhostRevivesNobody()
    {
        var (t, to, tc) = _fx.PlayerWith("RezGoneT", ch => { Dims(ch, RezGoneMap); ch.MaxHp = 500; ch.Hp = 500; },
                                         RezGoneMap, 10, 10);
        t.ReceiveEnvironmentDamage(9999, "a test");
        Assert.True(t.IsDead, "the kill did not land");
        t.WithState(() => TearDown.Invoke(t, null));
        Assert.Equal(0u, _fx.Store.Load(tc.Name).Character!.Hp);   // the logout saved the corpse
        to.Clear();

        t.ReviveInPlace("A test revived you.");   // its answer is @rez's to read; the state is what this pins

        Assert.Equal((true, 0u, 0u), (t.IsDead, tc.Hp, _fx.Store.Load(tc.Name).Character!.Hp));
        Assert.Empty(MiniTexts(to));
    }

    /// <summary>Nobody revived: on no map's list, not found by name, still a ghost in memory (what the next login's
    /// fence would write) and on disk (the corpse the logout saved), and left where the logout saved them.</summary>
    private void AssertNotRevived(Session t, Character tc, ushort map)
    {
        Assert.True(Content.Maps.ContainsKey(map), $"map {map} is not one Content.Maps knows: ReviveAt would not move");
        var stored = _fx.Store.Load(tc.Name);
        Assert.Equal(CharacterLoadStatus.Ok, stored.Status);
        Assert.Equal((0, false, 0u, 0u), (MapsHolding(t), _fx.World.Online.FindPlayer(tc.Name) is not null,
                                          tc.Hp, stored.Character!.Hp));
        Assert.Equal((map, (ushort)10, (ushort)10), (stored.Character.Map, stored.Character.X, stored.Character.Y));
    }

    /// <summary>The Poet paid as a Resurrect always has: its mana, its 8-second cooldown and the cast line.</summary>
    private static void AssertPaid(Session c, RecordingOutbound co, Character cc)
    {
        Assert.Equal(PoetMp - 3000, cc.Mp);
        Assert.True(SpellCastSupport.AetherLeft(c, ResurrectKey) > 0, "the Resurrect's cooldown was not armed");
        Assert.Contains("You cast Resurrect.", MiniTexts(co));
    }

    // ---- fixture -------------------------------------------------------------------------------------

    /// <summary>A grouping-willing, exchange-willing Koguryo level 30 on a 20x20 content-free map (a real map's own
    /// size on a real one), 100 mana; a caster also knows Approach (slot 0) and Summon (slot 1); a ghost has 0 Hp.
    /// Sessions made in order rank in order.</summary>
    private (Session s, RecordingOutbound o, Character c) Make(string name, ushort map, ushort x, ushort y,
                                                              bool caster = false, bool ghost = false)
    {
        var r = _fx.PlayerWith(name, ch =>
        {
            Dims(ch, map);
            ch.Level = 30; ch.Nation = 1;
            ch.Grouped = true; ch.Exchange = true;
            ch.MaxMp = StartMp; ch.Mp = StartMp;
            if (ghost) ch.Hp = 0;
            if (caster)
                ch.Spells = new List<int> { Content.SpellByKey("approach_spell")!.Id, Content.SpellByKey("summon_spell")!.Id };
        }, map, x, y);
        r.outbound.Clear();
        return r;
    }

    /// <summary>A level-99 Poet with Resurrect in slot 0 and <see cref="PoetMp"/> mana.</summary>
    private (Session s, RecordingOutbound o, Character c) Poet(string name, ushort map, ushort x, ushort y)
    {
        var r = _fx.PlayerWith(name, ch =>
        {
            Dims(ch, map);
            ch.Level = 99;
            ch.MaxMp = PoetMp; ch.Mp = PoetMp;
            ch.Spells = new List<int> { Content.SpellByKey(ResurrectKey)!.Id };
        }, map, x, y);
        r.outbound.Clear();
        return r;
    }

    /// <summary>A GM (<see cref="GmName"/>, on the roster the constructor loads). Every GM session on this World shares
    /// the name; nothing here looks a GM up by it.</summary>
    private (Session s, RecordingOutbound o) Gm(ushort map, ushort x, ushort y)
    {
        var (s, o, _) = _fx.PlayerWith(GmName, ch => { Dims(ch, map); ch.Level = 99; }, map, x, y);
        o.Clear();
        return (s, o);
    }

    private static void Dims(Character c, ushort map)
    {
        if (Content.TryMap(map, out var mi)) { c.MapXs = mi.Xs; c.MapYs = mi.Ys; }
        else { c.MapXs = 20; c.MapYs = 20; }
    }

    /// <summary>The partner's own thread, inside the partner's monitor (fact :86's shape): it holds the monitor until
    /// <c>CancelNow</c>, then cancels <paramref name="trade"/> as the partner would and sets <c>Cancelled</c>, then
    /// holds on until <c>Release</c>. Returned once it holds the monitor. Disposing lets it go, so a fact that fails
    /// half-way leaves no monitor held for the next one.</summary>
    private static BusyPartnerRun BusyPartner(Session p, object trade)
    {
        var run = new BusyPartnerRun();
        run.Thread = Run("partner", () =>
        {
            try
            {
                p.WithState(() =>
                {
                    run.Held.Set();
                    run.CancelNow.Wait();
                    EndTrade.Invoke(null, new object[] { trade, "Exchange cancelled.", false });   // the partner's cancel
                    run.Cancelled.Set();
                    run.Release.Wait();
                });
            }
            catch (Exception e) { run.Fault = e; }
        });
        Assert.True(run.Held.Wait(5000), "the partner never took its monitor");
        return run;
    }

    private sealed class BusyPartnerRun : IDisposable
    {
        public readonly ManualResetEventSlim Held = new(), CancelNow = new(), Cancelled = new(), Release = new();
        public Thread Thread = null!;
        public Exception? Fault;

        public void Dispose()
        {
            CancelNow.Set();
            Release.Set();
            Thread.Join(5000);
        }
    }

    /// <summary>A command the way the read loop runs one: a framed 0x0E chat line.</summary>
    private static byte[] ChatFrame(string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        var body = new byte[2 + bytes.Length];
        body[1] = (byte)bytes.Length;
        bytes.CopyTo(body, 2);
        return SessionFixture.Frame(ClientOp.Chat, body);
    }

    /// <summary>The ASCII of every 0x0D speech frame, where a command's refusal lands (<c>Refuse</c>, <c>SendLog</c>). Its
    /// readouts and confirmations go to the status pane instead (<c>Reply</c>, 0x0A): read those with
    /// <see cref="MiniTexts"/>.</summary>
    private static List<string> Speech(RecordingOutbound o) =>
        o.BodiesOf(ServerOp.Speech).Select(b => Encoding.ASCII.GetString(b)).ToList();

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

    /// <summary>Has <paramref name="caster"/>'s cast resolved <paramref name="target"/> as its player target? Only
    /// the verb does that (<c>ctx:pcTargetNamed</c>), inside the Lua gate, so a cast still waiting for the gate
    /// has not. Each fact's caster is a new session that has cast nothing before, so no earlier cast's target can
    /// stand in for this one's.</summary>
    private static bool Resolved(Session caster, Session target) => ReferenceEquals(caster.LuaPcTarget, target);

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
