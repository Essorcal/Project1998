using System.Diagnostics;
using System.Reflection;
using System.Text;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Tests;

/// <summary>
/// A broken-off engagement or a divorce clears only the vow that names the player who asked for it, and a half whose
/// partner no longer names it back ends at that player's next login, for free (PR #341's tier A review,
/// <c>reviews/PR341-by-fable.md</c>, F1; Caleb, 2026-10-11, "Fix it", with two rules).
///
/// <para><b>Before.</b> The Chapel ends a vow on both sides when the partner is online, and on the clicker's side only
/// when they are offline: the partner's saved row keeps its legend, its fiancé or spouse and its keepsake. Nothing read
/// that row again. So in ordinary play: A divorces B while B is offline, A marries C, and B logs in still married to A.
/// Nobody can propose to B (the proposal refuses anyone holding an "engaged" or "married" legend), and the Chapel
/// charges B the full price to divorce. When B pays while A is online, B's divorce ran A's half of it without asking
/// whom A's half named, and wiped A's marriage to C on A's side only: A ends with no spouse while C stays married to A.
/// The engagement path did the same to a new fiancé.</para>
///
/// <para><b>Now.</b> Rule 1: inside the partner's monitor, the partner's half is cleared only when it names the
/// clicker; otherwise nothing of theirs is touched. The clicker's own half takes the same check, so a confirm that
/// arrives after the partner already ended the vow takes no second keepsake. Rule 2: an arrival whose fiancé or spouse
/// no longer names it back, or no longer exists, ends that half (the legend, the name, the keepsake) without a Chapel
/// visit or a charge, and says so in one status line. The partner is read inside their monitor when they are online
/// and from their saved row through the arrival's own store when they are not. A consistent pair, and a partner whose
/// row cannot be read, are left as they are.</para>
///
/// <para><b>And then</b> (#344's Follow-ups; Caleb, 2026-10-18, "As recommended"). A confirm whose vow the partner
/// already ended is free: the dialog re-checks the clicker's own half, under the clicker's own monitor, before it
/// charges, and Crystal says "Your marriage to X has already ended." (or engagement) instead; she says the same to a
/// "No" there, to a Marriage picked from a menu built before the end, and to either answer to the ceremony's question
/// (PR #350's review, N3). And a partner whose half a Chapel visit ends while they are online reads the login line,
/// "Your marriage to X has ended." (or engagement), sent from inside their monitor where the end runs; a partner who is
/// offline, whose logout reaches their monitor before the end does, or whose connection the server has already
/// closed, reads it once, at their next login; and a partner whose half names someone else, or nobody, reads
/// nothing.</para>
///
/// <para><b>The path.</b> Every player logs in through the real <c>0x10</c> arrival on an isolated database and logs
/// out through the read loop's real exit (<c>Session.EndReadLoopAsync</c>, whose teardown saves). The Chapel is
/// Crystal's dialog on the Crystalline Chapel (map 3815) through the real <c>0x43</c> click and <c>0x3A</c> replies,
/// driven from a thread of its own because the dialog resumes inline on the thread that delivers the reply. The
/// proposal is the real <c>0x0F</c> cast of "propose" and its name box. A vow's starting state is written the way the
/// proposal and the wedding write it (the legend, the name and, for a marriage, one love keepsake), in memory under the
/// player's monitor for an online player and into the row for an offline one.</para>
///
/// <para><b>Collection "world", on a World of its own.</b> The <see cref="SessionFixture"/> is a class fixture, so
/// Crystal on map 3815 is this class's alone. The other players stand on content-free maps 62456-62460 and
/// 62470-62479, which no other class uses and <c>game-data/Maps.csv</c> has no row for. Each fact has a database of
/// its own (<see cref="IsolatedDatabase"/>).</para>
/// </summary>
[Collection("world")]
public sealed class VowHalvesTests : IClassFixture<SessionFixture>
{
    private const ushort StaleMap = 62472, HeldMap = 62473, ProposeMap = 62474, SceneMap = 62475,
                         WrongVowMap = 62476, ArrivalRaceMap = 62477, UnreadableMap = 62478, HeldArrivalMap = 62479;
    private const ushort RankedHeldMap = 62470, RankedArrivalMap = 62471;
    private const ushort PartnerAwayMap = 62456, LeavingPartnerMap = 62457, MonitorProbeMap = 62458, ClosedPartnerMap = 62459;
    private const ushort RankedPartnerMap = 62460;

    /// <summary>A session's state monitor object, so a fact can try it with a timeout (<c>Monitor.TryEnter</c>)
    /// instead of entering it through <c>EnterState</c>, which would wait for good on a monitor that is never
    /// free.</summary>
    private static readonly FieldInfo StateField =
        typeof(Session).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>A session's read-loop exit in its two parts, so a fact can stop between them: the teardown
    /// (<c>TearDownWorldState</c>, run under the session's monitor) and the close that <c>EndReadLoopAsync</c> makes
    /// after it lets go (<c>CloseConnection(string)</c>).</summary>
    private static readonly MethodInfo TearDownWorldState =
        typeof(Session).GetMethod("TearDownWorldState", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo CloseConnection =
        typeof(Session).GetMethod("CloseConnection", BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(string) })!;

    /// <summary>A session's two "going away" flags, read by a fact to show which of them a case has set:
    /// <c>_leaving</c> (the teardown's first statement, under the monitor) and <c>_closed</c> (the connection's close,
    /// set once, without the monitor).</summary>
    private static readonly FieldInfo LeavingField =
        typeof(Session).GetField("_leaving", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo ClosedField =
        typeof(Session).GetField("_closed", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>Crystal, the Crystalline Chapel's Chapel NPC (game-data/NPCs.csv, NpcId 397, map 3815, tile 9,9).</summary>
    private const int ChapelNpcId = 397;

    /// <summary>The two tiles beside Crystal the clickers stand on.</summary>
    private static readonly (ushort X, ushort Y) NearCrystal = (9, 11), AlsoNearCrystal = (11, 11);

    private const int ChapelRaceRounds = 100;
    private const int ArrivalRaceRounds = 60;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly SessionFixture _fx;
    private readonly ITestOutputHelper _out;
    private static int _serial;

    public VowHalvesTests(SessionFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    /// <summary>Where the partner a stale half names stands when its owner logs in.</summary>
    public enum PartnerState
    {
        /// <summary>Offline, their row vowed to someone else.</summary>
        MovedOnOffline,
        /// <summary>Offline, their row vowed to nobody.</summary>
        EndedOffline,
        /// <summary>Online, vowed to someone else.</summary>
        MovedOnOnline,
        /// <summary>Online, vowed to nobody.</summary>
        EndedOnline,
        /// <summary>No row: the character was deleted (LoginServer/Admin.cs deletes the row).</summary>
        Deleted,
    }

    // ===== rule 2: a stale half ends at login, for free ==================================================

    /// <summary>
    /// <b>A half whose partner no longer names it back ends at login, for free.</b> B's row is engaged or married to A;
    /// A has moved on to C, or ended it and stayed single, or no longer exists, and is offline or online. B logs in:
    /// B's legend, fiancé or spouse and (a marriage) love keepsake are gone, in memory and in B's saved row, B's
    /// experience is what it was, and B reads one status line naming A. A's and C's rows are byte for byte as they were,
    /// and an online A still names whom it named.
    /// <para>Red on 3690542, Debug and Release: B logs in still engaged or married to A, its row unchanged.</para>
    /// </summary>
    [Theory]
    [InlineData(false, PartnerState.MovedOnOffline)]
    [InlineData(false, PartnerState.EndedOffline)]
    [InlineData(false, PartnerState.MovedOnOnline)]
    [InlineData(false, PartnerState.EndedOnline)]
    [InlineData(false, PartnerState.Deleted)]
    [InlineData(true, PartnerState.MovedOnOffline)]
    [InlineData(true, PartnerState.EndedOffline)]
    [InlineData(true, PartnerState.MovedOnOnline)]
    [InlineData(true, PartnerState.EndedOnline)]
    [InlineData(true, PartnerState.Deleted)]
    public void AStaleHalfEndsAtLoginForFree(bool married, PartnerState state)
    {
        using var db = new IsolatedDatabase();
        string a = Name("A"), b = Name("B"), c = Name("C");
        bool movedOn = state is PartnerState.MovedOnOffline or PartnerState.MovedOnOnline;
        bool online = state is PartnerState.MovedOnOnline or PartnerState.EndedOnline;
        var aRow = NewCharacter(a, StaleMap, 5, 5);
        var bRow = NewCharacter(b, StaleMap, 7, 5);
        var cRow = NewCharacter(c, StaleMap, 9, 5);
        if (movedOn) { VowRow(aRow, c, married); VowRow(cRow, a, married); }
        VowRow(bRow, a, married);
        Seed(db, aRow, bRow, cRow);
        if (state == PartnerState.Deleted) DeleteRow(db, a);

        Session? aS = null, bS = null;
        try
        {
            if (online) (aS, _) = Arrive(db.Store, a);
            string? aJson = RowJsonOrNull(db, a);
            string cJson = RowJson(db, c);

            (bS, var bOut) = Arrive(db.Store, b);
            string atLogin = VowState(bS);
            var lines = SpellCastSupport.MiniTexts(bOut);
            _out.WriteLine($"[{Kind(married)}, A {state}] B at login: {atLogin}; B's row: {RowVowState(db.Store, b)}; " +
                           $"lines: [{string.Join(" | ", lines)}]");

            Assert.True(VowGone(bS, married), $"B logged in still holding its half: {atLogin}");
            if (married) Assert.Equal(0, bS.WithState(() => bS.CountItem("love")));
            Assert.True(RowVowGone(db.Store, b, married, keepsakes: 0), $"B's row kept its half: {RowVowState(db.Store, b)}");
            Assert.Equal(bRow.Exp, bS.CharExp);
            Assert.Equal(1, lines.Count(l => l == Line(married, a)));

            Assert.Equal(aJson, RowJsonOrNull(db, a));
            Assert.Equal(cJson, RowJson(db, c));
            if (aS is not null)
                Assert.Equal(movedOn ? c : "", aS.WithState(() => married ? aS.CharSpouseName : aS.CharFiance));
        }
        finally
        {
            Release(a, aS);
            Release(b, bS);
        }
    }

    /// <summary>
    /// <b>A consistent pair is left as it is at login.</b> A and B are engaged or married to each other; A is offline
    /// or online. B logs in still holding its whole half, B's row is not written by the arrival (byte for byte as it
    /// was), A's row is byte for byte as it was, and B reads no line. With A online, A's own login made the same check
    /// against B's offline row, and kept A's half too.
    /// <para>Not a guard against the fix's absence (green on 3690542): it is red against a check that ends a half
    /// without asking the partner.</para>
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AConsistentPairIsLeftAsItIsAtLogin(bool married, bool partnerOnline)
    {
        using var db = new IsolatedDatabase();
        string a = Name("A"), b = Name("B");
        var aRow = NewCharacter(a, StaleMap, 11, 5);
        var bRow = NewCharacter(b, StaleMap, 13, 5);
        VowRow(aRow, b, married);
        VowRow(bRow, a, married);
        Seed(db, aRow, bRow);

        Session? aS = null, bS = null;
        try
        {
            RecordingOutbound? aOut = null;
            if (partnerOnline) (aS, aOut) = Arrive(db.Store, a);
            string aJson = RowJson(db, a), bJson = RowJson(db, b);

            (bS, var bOut) = Arrive(db.Store, b);

            Assert.True(Vowed(bS, a, married, keepsakes: 1), $"B lost its half of a consistent pair: {VowState(bS)}");
            Assert.Equal(bJson, RowJson(db, b));
            Assert.Equal(aJson, RowJson(db, a));
            Assert.DoesNotContain(SpellCastSupport.MiniTexts(bOut), l => l.Contains(" has ended."));
            if (aS is not null)
            {
                Assert.True(Vowed(aS, b, married, keepsakes: 1), $"A lost its half of a consistent pair: {VowState(aS)}");
                Assert.DoesNotContain(SpellCastSupport.MiniTexts(aOut!), l => l.Contains(" has ended."));
            }
        }
        finally
        {
            Release(a, aS);
            Release(b, bS);
        }
    }

    /// <summary>
    /// <b>A partner whose row cannot be read leaves the half as it is.</b> A's row holds JSON the store cannot parse
    /// (<c>CharacterLoadStatus.Unreadable</c>), so there is no evidence either way. B logs in still holding its half,
    /// reads no line, and A's stored JSON is as it was.
    /// <para>Not a guard against the fix's absence (green on 3690542): it is red against a check that reads an
    /// unreadable row as a deleted one.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APartnerWhoseRowCannotBeReadLeavesTheHalf(bool married)
    {
        using var db = new IsolatedDatabase();
        string a = Name("A"), b = Name("B");
        var aRow = NewCharacter(a, UnreadableMap, 5, 5);
        var bRow = NewCharacter(b, UnreadableMap, 7, 5);
        VowRow(bRow, a, married);
        Seed(db, aRow, bRow);
        Exec(db, "UPDATE characters SET json=$j WHERE username=$u;", ("$j", "{\"Name\":"), ("$u", CharacterStore.Key(a)));
        Assert.Equal(CharacterLoadStatus.Unreadable, db.Store.Load(a).Status);
        string aJson = RowJson(db, a), bJson = RowJson(db, b);

        Session? bS = null;
        try
        {
            (bS, var bOut) = Arrive(db.Store, b);

            Assert.True(Vowed(bS, a, married, keepsakes: 1), $"B lost its half on an unreadable partner row: {VowState(bS)}");
            Assert.Equal(bJson, RowJson(db, b));
            Assert.Equal(aJson, RowJson(db, a));
            Assert.DoesNotContain(SpellCastSupport.MiniTexts(bOut), l => l.Contains(" has ended."));
        }
        finally
        {
            Release(b, bS);
        }
    }

    /// <summary>
    /// <b>The arrival reads an online partner inside that partner's monitor, once it is on its own map.</b> A is online
    /// and married (or engaged) to C; B's row still names A. A third thread holds A's monitor while B's arrival runs on a
    /// thread of its own: the arrival waits for it, B's row keeps its half while it waits, and B can already be found by
    /// name (it is on its map), so a Chapel visit of A's at that moment would reach B; once the third thread lets go B's
    /// half ends. Both <c>StateRank</c> orders, so both of <c>EnterState</c>'s branches are the ones that wait. A and B
    /// stand on different maps, so B's own map entry does not reach A.
    /// <para>Red on 3690542, Debug and Release: the arrival never reads A, so it finishes while A's monitor is held,
    /// and B is still vowed after it. Red with the read made before the map entry: B cannot be found while it
    /// waits.</para>
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void TheArrivalReadsAnOnlinePartnerInsideTheirMonitor(bool married, bool arrivingRanksFirst)
    {
        using var db = new IsolatedDatabase();
        string a = Name("A"), b = Name("B"), c = Name("C");
        var aRow = NewCharacter(a, HeldMap, 5, 5);
        var bRow = NewCharacter(b, HeldArrivalMap, 5, 5);
        var cRow = NewCharacter(c, HeldMap, 7, 5);
        VowRow(aRow, c, married);
        VowRow(cRow, a, married);
        VowRow(bRow, a, married);
        Seed(db, aRow, bRow, cRow);

        Session aS, bS;
        RecordingOutbound bOut;
        if (arrivingRanksFirst)
        {
            (bS, bOut) = NewSession(db.Store, b);
            (aS, _) = Arrive(db.Store, a);
        }
        else
        {
            (aS, _) = Arrive(db.Store, a);
            (bS, bOut) = NewSession(db.Store, b);
        }
        Assert.Equal(arrivingRanksFirst, bS.StateRank < aS.StateRank);

        var run = new HeldRun();
        bool findable = false;
        try
        {
            WhileAnotherThreadHolds(run, aS, () => bS.Receive(ArrivalFrame(b)), untouched: () =>
            {
                findable = _fx.World.Online.FindPlayer(b) is not null;
                return RowVowed(db.Store, b, a, married);
            });
            AssertWaited(run, "the arrival", "B's row lost its half before A's monitor was free");
            Assert.True(findable, "B could not be found by name while it waited to read A: a Chapel visit of A's then misses B");

            Assert.False(bOut.Closed, "the arrival was refused");
            Assert.True(VowGone(bS, married), $"B kept its half after reading A: {VowState(bS)}");
            Assert.Equal(1, SpellCastSupport.MiniTexts(bOut).Count(l => l == Line(married, a)));
            Assert.True(Vowed(aS, c, married, keepsakes: 1), $"A's own vow changed: {VowState(aS)}");
        }
        finally
        {
            if (run.Settled)
            {
                Release(a, aS);
                Release(b, bS);
            }
        }
    }

    /// <summary>
    /// <b>The arrival lets go of its own monitor while it waits for a partner who ranks before it.</b> A is online,
    /// married (or engaged) to C, and ranks before B; B's row still names A. A third thread takes A's monitor, as A's
    /// Chapel visit does, and B's arrival, on a thread of its own, waits to read A. Then the third thread, still inside
    /// A, takes B's monitor, which is what A's visit does next when it ends B's half (an ascending entry, so it keeps
    /// A's). It gets it at once: the read is entered by <c>StateRank</c>, and <c>EnterState</c>'s descending case
    /// drops B's own monitor while it waits for A's. Once the third thread lets go, B's half ends. Deterministic, and
    /// bounded: the take is a <c>Monitor.TryEnter</c> with a 5 s timeout, so a monitor that never comes free fails
    /// the fact instead of hanging it. A and B stand on different maps, so B's own map entry does not reach A.
    /// <para>Red with the read under a plain lock on A's monitor object (<c>lock (online._state)</c>, the review's
    /// NC1), Debug and Release: B keeps its own monitor while it waits, and the third thread cannot take it in 5 s.
    /// In play that is A's Chapel visit and B's arrival waiting on each other for good. The held fact above does not
    /// see it, since it asks only that the arrival waits (PR #344 review, F2).</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheArrivalDropsItsOwnMonitorWhileItWaitsForALowerRankedPartner(bool married)
    {
        using var db = new IsolatedDatabase();
        string a = Name("A"), b = Name("B"), c = Name("C");
        var aRow = NewCharacter(a, RankedHeldMap, 5, 5);
        var bRow = NewCharacter(b, RankedArrivalMap, 5, 5);
        var cRow = NewCharacter(c, RankedHeldMap, 7, 5);
        VowRow(aRow, c, married);
        VowRow(cRow, a, married);
        VowRow(bRow, a, married);
        Seed(db, aRow, bRow, cRow);

        var (aS, _) = Arrive(db.Store, a);
        var (bS, bOut) = NewSession(db.Store, b);
        Assert.True(aS.StateRank < bS.StateRank, "A must rank before B, so that B's read of A is the descending entry");
        object bMonitor = StateField.GetValue(bS)!;

        var run = new HeldRun();
        bool tookB = false;
        long waitedMs = -1;
        try
        {
            // The check runs inside the third thread's section on A, once the arrival is seen waiting.
            WhileAnotherThreadHolds(run, aS, () => bS.Receive(ArrivalFrame(b)), untouched: () =>
            {
                var clock = Stopwatch.StartNew();
                tookB = Monitor.TryEnter(bMonitor, TimeSpan.FromSeconds(5));
                waitedMs = clock.ElapsedMilliseconds;
                if (tookB) Monitor.Exit(bMonitor);
                return tookB;
            });
            _out.WriteLine($"[{Kind(married)}] the arrival waited: {!run.FinishedWhileHeld}; the third thread, inside A, " +
                           $"took B's monitor: {tookB} after {waitedMs} ms; B after: {VowState(bS)}");

            AssertWaited(run, "the arrival",
                "B kept its own monitor while it waited for A's: A's Chapel visit, which takes B's next, would deadlock with it");
            Assert.False(bOut.Closed, "the arrival was refused");
            Assert.True(VowGone(bS, married), $"B kept its half after reading A: {VowState(bS)}");
        }
        finally
        {
            if (run.Settled)
            {
                Release(a, aS);
                Release(b, bS);
            }
        }
    }

    /// <summary>
    /// <b>A player who logs in with a stale half can be proposed to.</b> B's row is engaged or married to A, who has
    /// ended it. B logs in holding an engagement ring D gave them, and D casts "propose" (the real <c>0x0F</c>) and
    /// names B in its box: the proposal reaches B ("D proposes marriage. Do you accept?"), D is not told B is
    /// committed, and B's "Yes" engages the two.
    /// <para>Red on 3690542, Debug and Release: D reads "B is already committed to someone else!" and B is never
    /// asked.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStalePlayerCanBeProposedToOnceTheyLogIn(bool married)
    {
        using var db = new IsolatedDatabase();
        string a = Name("A"), b = Name("B"), d = Name("D");
        var propose = Content.SpellByKey("propose")!;
        var aRow = NewCharacter(a, ProposeMap, 5, 5);
        var bRow = NewCharacter(b, ProposeMap, 7, 5);
        var dRow = NewCharacter(d, ProposeMap, 9, 5);
        VowRow(bRow, a, married);
        GiveRow(bRow, "engagement_ring");
        dRow.Spells.Add(propose.Id);
        int proposeSlot = dRow.Spells.IndexOf(propose.Id);
        Seed(db, aRow, bRow, dRow);

        Session? bHeld = null, dHeld = null;
        try
        {
            var (bS, bOut) = Arrive(db.Store, b);
            bHeld = bS;
            var (dS, dOut) = Arrive(db.Store, d);
            dHeld = dS;
            dOut.Clear();
            bOut.Clear();

            OnOwnThread(() =>
            {
                dS.Receive(SpellCastSupport.CastFrame(proposeSlot));
                Assert.True(SpinWait.SpinUntil(() => dS.DialogBusy, Bound), "the proposal never asked for a name");
                dS.Receive(DialogInputFrame(b));
            });

            var dLines = SpellCastSupport.MiniTexts(dOut);
            _out.WriteLine($"[{Kind(married)}] D's lines: [{string.Join(" | ", dLines)}]");
            Assert.DoesNotContain($"{b} is already committed to someone else!", dLines);
            var answer = LastMenu(bOut, $"{d} proposes marriage. Do you accept?");
            Assert.Equal(2, answer.Count);

            OnOwnThread(() => bS.Receive(MenuFrame(1)));
            Assert.True(Vowed(bS, d, married: false, keepsakes: 0), $"B is not engaged to D: {VowState(bS)}");
            Assert.True(Vowed(dS, b, married: false, keepsakes: 0), $"D is not engaged to B: {VowState(dS)}");
        }
        finally
        {
            Release(b, bHeld);
            Release(d, dHeld);
        }
    }

    // ===== the whole story, through the Chapel and the arrival ===========================================

    /// <summary>
    /// <b>An ex who comes back never ends their former partner's new vow.</b> The review's sequence, end to end: A and
    /// B are engaged (or married); B logs out; A ends it at Crystal's dialog (B's row keeps its half, as the Chapel's
    /// offline path does); A gets engaged (or married) to C; B logs back in and visits Crystal, taking Break Off
    /// Engagement (or Divorce) if she offers it. A and C are still vowed to each other, in memory and in their rows;
    /// B holds nothing of the vow and paid nothing; and Crystal no longer offers B a vow to end.
    /// <para>Red on 3690542, Debug and Release: B is offered the entry, pays the full price, and B's visit clears A's
    /// vow to C on A's side only (A ends with no fiancé or spouse, C still names A).</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnExWhoComesBackNeverEndsTheirFormerPartnersNewVow(bool married)
    {
        using var db = new IsolatedDatabase();
        var (npc, def) = ChapelNpc();
        string a = Name("A"), b = Name("B"), c = Name("C");
        Seed(db, NewCharacter(a, def.Map, NearCrystal.X, NearCrystal.Y),
                 NewCharacter(b, def.Map, AlsoNearCrystal.X, AlsoNearCrystal.Y),
                 NewCharacter(c, SceneMap, 5, 5));

        Session? aHeld = null, bHeld = null, cHeld = null;
        try
        {
            var (aS, aOut) = Arrive(db.Store, a);
            aHeld = aS;
            var (bFirst, _) = Arrive(db.Store, b);
            bHeld = bFirst;
            var (cS, _) = Arrive(db.Store, c);
            cHeld = cS;
            Vow(aS, b, married);
            Vow(bFirst, a, married);

            Logout(bFirst);
            Release(b, bFirst);
            bHeld = null;
            Assert.True(RowVowed(db.Store, b, a, married), "B's row lost its half at logout");

            OnOwnThread(() =>
            {
                OpenVowEntry(aS, aOut, npc, def, married);
                aS.Receive(MenuFrame(1));
                aS.Receive(NextFrame());
            });
            Assert.Equal(1, Shown(aOut, married ? "You are now divorced." : "It is done."));
            Assert.True(VowGone(aS, married), $"A's own visit did not end A's half: {VowState(aS)}");
            Assert.True(RowVowed(db.Store, b, a, married), "the Chapel's offline path touched B's row");

            Vow(aS, c, married);
            Vow(cS, a, married);
            uint bExp = db.Store.Load(b).Character!.Exp;

            var (bS, bOut) = Arrive(db.Store, b);
            bHeld = bS;
            string bAtLogin = VowState(bS);
            bool offered = false;
            OnOwnThread(() => offered = OfferedVowEntry(bS, bOut, npc, def, married));
            if (offered)
            {
                OnOwnThread(() =>
                {
                    OpenVowEntry(bS, bOut, npc, def, married);
                    bS.Receive(MenuFrame(1));
                    bS.Receive(NextFrame());
                });
            }
            _out.WriteLine($"[{Kind(married)}] B at login: {bAtLogin}; Crystal offered B the entry: {offered}; after: " +
                           $"A {VowState(aS)}, C {VowState(cS)}, B {VowState(bS)}, B paid {bExp - bS.CharExp}");

            Assert.True(Vowed(aS, c, married, keepsakes: 1), $"A's vow to C was ended: {VowState(aS)}");
            Assert.True(Vowed(cS, a, married, keepsakes: 1), $"C's vow to A was ended: {VowState(cS)}");
            Assert.True(RowVowed(db.Store, a, c, married), $"A's row lost its vow to C: {RowVowState(db.Store, a)}");
            Assert.True(RowVowed(db.Store, c, a, married), $"C's row lost its vow to A: {RowVowState(db.Store, c)}");
            Assert.True(VowGone(bS, married), $"B still holds its half: {VowState(bS)}");
            Assert.True(RowVowGone(db.Store, b, married, keepsakes: 0), $"B's row still holds its half: {RowVowState(db.Store, b)}");
            Assert.Equal(bExp, bS.CharExp);
            Assert.False(offered, "Crystal still offered B a vow to end");
        }
        finally
        {
            Release(a, aHeld);
            Release(b, bHeld);
            Release(c, cHeld);
        }
    }

    // ===== rule 1: the partner's half is cleared only when it names the clicker ===========================

    /// <summary>
    /// <b>A Chapel visit on a stale half leaves the partner's new vow alone.</b> B, online, still names A (a half that
    /// outlived its login: the partner's row could not be read then, say); A is engaged (or married) to C. B takes Break
    /// Off Engagement (or Divorce) at Crystal: B's half ends and B pays as before, and A, whose half names C, is not
    /// touched: A and C keep their vow, keepsakes and rows.
    /// <para>Red on 3690542, Debug and Release: B's visit runs A's half unconditionally, so A ends with no fiancé or
    /// spouse and no keepsake while C still names A.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AChapelVisitOnAStaleHalfLeavesThePartnersNewVowAlone(bool married)
    {
        using var db = new IsolatedDatabase();
        var (npc, def) = ChapelNpc();
        string a = Name("A"), b = Name("B"), c = Name("C");
        Seed(db, NewCharacter(a, WrongVowMap, 5, 5),
                 NewCharacter(b, def.Map, NearCrystal.X, NearCrystal.Y),
                 NewCharacter(c, WrongVowMap, 7, 5));

        Session? aHeld = null, bHeld = null, cHeld = null;
        try
        {
            var (aS, _) = Arrive(db.Store, a);
            aHeld = aS;
            var (bS, bOut) = Arrive(db.Store, b);
            bHeld = bS;
            var (cS, _) = Arrive(db.Store, c);
            cHeld = cS;
            Vow(bS, a, married);
            Vow(aS, c, married);
            Vow(cS, a, married);
            uint bExp = bS.CharExp;

            OnOwnThread(() =>
            {
                OpenVowEntry(bS, bOut, npc, def, married);
                bS.Receive(MenuFrame(1));
                bS.Receive(NextFrame());
            });
            _out.WriteLine($"[{Kind(married)}] after B's visit: A {VowState(aS)}, C {VowState(cS)}, B {VowState(bS)}");

            Assert.True(Vowed(aS, c, married, keepsakes: 1), $"B's visit ended A's vow to C: {VowState(aS)}");
            Assert.True(RowVowed(db.Store, a, c, married), $"A's row lost its vow to C: {RowVowState(db.Store, a)}");
            Assert.True(Vowed(cS, a, married, keepsakes: 1), $"C's vow changed: {VowState(cS)}");
            Assert.Equal(1, Shown(bOut, married ? "You are now divorced." : "It is done."));
            Assert.True(VowGone(bS, married), $"B's own half was not ended: {VowState(bS)}");
            Assert.Equal(bExp - (married ? 1_000u * 2550 : 100u * 1000), bS.CharExp);
        }
        finally
        {
            Release(a, aHeld);
            Release(b, bHeld);
            Release(c, cHeld);
        }
    }

    /// <summary>
    /// <b>A confirm that arrives after the partner already ended the marriage takes no second keepsake.</b> A and B are
    /// married and each also carries a love bought from the Chapel's shop: two each. A opens Divorce at Crystal and
    /// leaves its last question open; B divorces at Crystal meanwhile, which ends both halves and takes one keepsake
    /// from each; then A answers "Yes". A's half, already ended, is not ended again: each ends with the bought love,
    /// one each.
    /// <para>Red on 3690542, Debug and Release: A's late "Yes" runs A's own half whatever it holds and takes A's
    /// bought love (A ends with 0). B keeps its own: A's half is empty by then, so the partner looked up by that
    /// empty name is nobody. The partner's side of a crossing is the race below.</para>
    /// <para>What A pays and reads for the late "Yes" is <see cref="AConfirmWhoseVowThePartnerAlreadyEndedIsFree"/>'s
    /// business; this fact pins the keepsake only.</para>
    /// </summary>
    [Fact]
    public void AConfirmAfterThePartnerEndedTheMarriageTakesNoSecondKeepsake()
    {
        using var db = new IsolatedDatabase();
        var (npc, def) = ChapelNpc();
        string a = Name("A"), b = Name("B");
        Seed(db, NewCharacter(a, def.Map, NearCrystal.X, NearCrystal.Y),
                 NewCharacter(b, def.Map, AlsoNearCrystal.X, AlsoNearCrystal.Y));

        Session? aHeld = null, bHeld = null;
        try
        {
            var (aS, aOut) = Arrive(db.Store, a);
            aHeld = aS;
            var (bS, bOut) = Arrive(db.Store, b);
            bHeld = bS;
            Vow(aS, b, married: true);
            Vow(bS, a, married: true);
            TopUpLove(aS, 2);
            TopUpLove(bS, 2);
            uint aExp = aS.CharExp;

            OnOwnThread(() => OpenVowEntry(aS, aOut, npc, def, divorce: true));
            OnOwnThread(() =>
            {
                OpenVowEntry(bS, bOut, npc, def, divorce: true);
                bS.Receive(MenuFrame(1));
                bS.Receive(NextFrame());
            });
            Assert.True(VowGone(aS, married: true), $"B's divorce did not end A's half: {VowState(aS)}");
            Assert.Equal(1, aS.WithState(() => aS.CountItem("love")));

            OnOwnThread(() =>
            {
                aS.Receive(MenuFrame(1));
                aS.Receive(NextFrame());
            });
            _out.WriteLine($"after A's late Yes: A {VowState(aS)}, B {VowState(bS)}; A paid {aExp - aS.CharExp}");

            Assert.False(aS.DialogBusy, "A's dialog did not close");
            Assert.Equal(1, aS.WithState(() => aS.CountItem("love")));
            Assert.Equal(1, bS.WithState(() => bS.CountItem("love")));
            Assert.True(VowGone(aS, married: true));
            Assert.True(VowGone(bS, married: true));
        }
        finally
        {
            Release(a, aHeld);
            Release(b, bHeld);
        }
    }

    // ===== the Chapel's follow-ups: a late confirm is free, and an online partner is told ==================

    /// <summary>Where the late player's Chapel dialog waits while the partner ends the vow.</summary>
    public enum LateConfirm
    {
        /// <summary>Break Off Engagement's "Are you sure you want to end the engagement?".</summary>
        BreakAtTheQuestion,
        /// <summary>Crystal's top menu, which offered Break Off Engagement.</summary>
        BreakAtTheMenu,
        /// <summary>Divorce's "It will cost N experience. Are you sure you want this divorce?", experience enough.</summary>
        DivorceAtTheQuestion,
        /// <summary>Crystal's top menu, which offered Divorce, experience enough.</summary>
        DivorceAtTheMenu,
        /// <summary>The same question with too little experience, so a "Yes" leads to the base-pool sacrifice.</summary>
        SacrificeAtTheQuestion,
        /// <summary>The sacrifice's last question, "It will cost you 8000 base Vita as a penalty. Continue?".</summary>
        SacrificeAtTheConfirm,
        /// <summary>Break Off Engagement's question, answered "No, I need to consider further.".</summary>
        BreakAtTheQuestionNo,
        /// <summary>Divorce's question, experience enough, answered "No".</summary>
        DivorceAtTheQuestionNo,
        /// <summary>Crystal's top menu, which offered Marriage (and Break Off Engagement), answered "Marriage".</summary>
        MarriageAtTheMenu,
        /// <summary>The ceremony's "Are you certain you wish to devote yourself to this man or woman for life?", A the
        /// proposee and the wait over, answered "Yes".</summary>
        MarriageAtTheQuestion,
        /// <summary>The same question, answered "No".</summary>
        MarriageAtTheQuestionNo,
    }

    /// <summary>
    /// <b>A confirm whose vow the partner already ended is free.</b> A and B stand beside Crystal, engaged (or married)
    /// to each other. A's dialog is left waiting (<see cref="LateConfirm"/>), and B ends the vow at Crystal meanwhile,
    /// which ends both halves. Then A answers yes to everything, to the end of the dialog; or, in the last five cases,
    /// answers "No" to the waiting question, or picks Marriage from the waiting top menu, and then yes to the end. A pays
    /// nothing (experience, base Vita and base Mana, in memory and in A's row) and reads Crystal's "Your engagement to B
    /// has already ended." (or marriage) once, never "It is done." / "You are now divorced.", never the sacrifice's offer
    /// and never a line for a vow that still holds (<see cref="StillVowedLines"/>); A read the status line "Your
    /// engagement to B has ended." (or marriage) once, when B's visit ended A's half. B pays and reads as before, and
    /// reads no status line.
    /// <para>The first six cases are red on c7878f2, Debug and Release: A is charged (<c>MaxMp * 1000</c> or
    /// <c>MaxHp * 2550</c> exp, or 8000 base Vita on the sacrifice path) and reads "It is done." or "You are now
    /// divorced."; A reads no status line.</para>
    /// <para>The next three cases (PR #350's review, N3) are red on 6de1d24, Debug and Release: A, who pays nothing there
    /// either, reads "I hope you can salvage your relationship.", "Patience and love will save your marriage. ..." or
    /// "The proposee should start the marriage ceremony.", and no "has already ended." page. The last two, the
    /// ceremony's own question, are red on bf0e6e6, Debug and Release: A reads "Both parties must be present for the
    /// ceremony to commence" or "Come back when you are firm in your resolve to marry.", and no "has already ended."
    /// page.</para>
    /// </summary>
    [Theory]
    [InlineData(LateConfirm.BreakAtTheQuestion)]
    [InlineData(LateConfirm.BreakAtTheMenu)]
    [InlineData(LateConfirm.DivorceAtTheQuestion)]
    [InlineData(LateConfirm.DivorceAtTheMenu)]
    [InlineData(LateConfirm.SacrificeAtTheQuestion)]
    [InlineData(LateConfirm.SacrificeAtTheConfirm)]
    [InlineData(LateConfirm.BreakAtTheQuestionNo)]
    [InlineData(LateConfirm.DivorceAtTheQuestionNo)]
    [InlineData(LateConfirm.MarriageAtTheMenu)]
    [InlineData(LateConfirm.MarriageAtTheQuestion)]
    [InlineData(LateConfirm.MarriageAtTheQuestionNo)]
    public void AConfirmWhoseVowThePartnerAlreadyEndedIsFree(LateConfirm when)
    {
        using var db = new IsolatedDatabase();
        var (npc, def) = ChapelNpc();
        bool ceremony = when is LateConfirm.MarriageAtTheQuestion or LateConfirm.MarriageAtTheQuestionNo;
        bool married = !ceremony && when is not (LateConfirm.BreakAtTheQuestion or LateConfirm.BreakAtTheMenu
                                                 or LateConfirm.BreakAtTheQuestionNo or LateConfirm.MarriageAtTheMenu);
        bool sacrifice = when is LateConfirm.SacrificeAtTheQuestion or LateConfirm.SacrificeAtTheConfirm;
        bool atTheMenu = when is LateConfirm.BreakAtTheMenu or LateConfirm.DivorceAtTheMenu or LateConfirm.MarriageAtTheMenu;
        bool no = when is LateConfirm.BreakAtTheQuestionNo or LateConfirm.DivorceAtTheQuestionNo
                       or LateConfirm.MarriageAtTheQuestionNo;
        string label = when == LateConfirm.MarriageAtTheMenu ? "Marriage" : married ? "Divorce" : "Break Off Engagement";
        string a = Name("A"), b = Name("B");
        // On the sacrifice path A has 10,000 Vita, so a divorce costs 25,500,000 exp, against 1,000 held, and the
        // 8000 Vita sacrifice is allowed.
        var aRow = NewCharacter(a, def.Map, NearCrystal.X, NearCrystal.Y, maxHp: sacrifice ? 10_000u : 1_000u);
        if (sacrifice) aRow.Exp = 1_000;
        Seed(db, aRow, NewCharacter(b, def.Map, AlsoNearCrystal.X, AlsoNearCrystal.Y));

        Session? aHeld = null, bHeld = null;
        try
        {
            var (aS, aOut) = Arrive(db.Store, a);
            aHeld = aS;
            var (bS, bOut) = Arrive(db.Store, b);
            bHeld = bS;
            Vow(aS, b, married);
            Vow(bS, a, married);
            // The ceremony asks its question only of the proposee, once the three days' wait is over.
            if (ceremony)
                aS.WithState(() => aS.SetEngaged(b, isProposee: true, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1));
            var aPools = Pools(aS);
            var aRowPools = Pools(Row(db.Store, a));
            uint bExp = bS.CharExp;

            int entry = 0;
            OnOwnThread(() =>
            {
                if (atTheMenu)
                {
                    Click(aS, npc);
                    entry = LastMenu(aOut, $"{def.Name}: How can I help you today?").IndexOf(label) + 1;
                }
                else if (ceremony)
                {
                    Click(aS, npc);
                    int marriage = LastMenu(aOut, $"{def.Name}: How can I help you today?").IndexOf("Marriage") + 1;
                    Assert.True(marriage > 0, "Crystal's top menu did not offer A Marriage");
                    aS.Receive(MenuFrame((byte)marriage));
                    LastMenu(aOut, "Are you certain you wish to devote yourself to this man or woman for life?");
                }
                else
                {
                    OpenVowEntry(aS, aOut, npc, def, married);
                    if (when == LateConfirm.SacrificeAtTheConfirm)
                    {
                        aS.Receive(MenuFrame(1));   // "Yes": too little experience, so "Hmmm.. ..."
                        aS.Receive(NextFrame());
                        aS.Receive(MenuFrame(1));   // "Sacrifice 8000 Vita"
                        LastMenu(aOut, "It will cost you 8000 base Vita as a penalty. Continue?");
                    }
                }
            });
            Assert.True(aS.DialogBusy, $"A's dialog is not waiting at {when}");
            if (atTheMenu) Assert.True(entry > 0, $"Crystal's top menu did not offer A {label}");
            aOut.Clear();
            bOut.Clear();

            OnOwnThread(() =>
            {
                OpenVowEntry(bS, bOut, npc, def, married);
                bS.Receive(MenuFrame(1));
                bS.Receive(NextFrame());
            });
            Assert.True(VowGone(aS, married), $"B's visit did not end A's half: {VowState(aS)}");

            OnOwnThread(() =>
            {
                aS.Receive(MenuFrame((byte)(atTheMenu ? entry : no ? 2 : 1)));
                AnswerYesToTheEnd(aS, aOut);
            });
            var aLines = SpellCastSupport.MiniTexts(aOut);
            var aNow = Pools(aS);
            _out.WriteLine($"[{when}] A paid exp {aPools.Exp - aNow.Exp}, Vita {aPools.MaxHp - aNow.MaxHp}, " +
                           $"Mana {aPools.MaxMp - aNow.MaxMp}; A read: already {Shown(aOut, AlreadyLine(married, b))}, " +
                           $"done {Shown(aOut, DonePage(married))}, offer {Shown(aOut, SacrificeOffer)}, still vowed " +
                           $"[{string.Join(" | ", StillVowedLines.Where(l => Shown(aOut, l) > 0))}]; " +
                           $"A's lines: [{string.Join(" | ", aLines)}]; A {VowState(aS)}, B {VowState(bS)}");

            Assert.Equal(aPools, aNow);
            Assert.Equal(aRowPools, Pools(Row(db.Store, a)));
            foreach (string still in StillVowedLines)
                Assert.True(Shown(aOut, still) == 0, $"A, whose vow had ended, read \"{still}\"");
            Assert.Equal(1, Shown(aOut, AlreadyLine(married, b)));
            Assert.Equal(0, Shown(aOut, DonePage(married)));
            Assert.Equal(0, Shown(aOut, SacrificeOffer));
            Assert.Equal(1, aLines.Count(l => l == Line(married, b)));
            Assert.Equal(1, aLines.Count(l => l.Contains(" has ended.")));
            Assert.True(VowGone(aS, married), $"A holds a half again: {VowState(aS)}");

            Assert.True(VowGone(bS, married), $"B's own visit did not end B's half: {VowState(bS)}");
            Assert.Equal(bExp - Price(married), bS.CharExp);
            Assert.Equal(1, Shown(bOut, DonePage(married)));
            Assert.DoesNotContain(SpellCastSupport.MiniTexts(bOut), l => l.Contains(" has ended."));
            if (married)
            {
                Assert.Equal(0, aS.WithState(() => aS.CountItem("love")));
                Assert.Equal(0, bS.WithState(() => bS.CountItem("love")));
            }
        }
        finally
        {
            Release(a, aHeld);
            Release(b, bHeld);
        }
    }

    /// <summary>Where the partner whose half A's Chapel visit is about stands, and what their half names.</summary>
    public enum PartnerAtTheVisit
    {
        /// <summary>Online, on another map, their half naming A.</summary>
        Online,
        /// <summary>Offline, their row's half naming A; they log in after the visit.</summary>
        Offline,
        /// <summary>Online, their half naming C (A's half outlived its login).</summary>
        OnlineMovedOn,
        /// <summary>Online, their half naming nobody.</summary>
        OnlineSingle,
    }

    /// <summary>
    /// <b>A partner whose half a Chapel visit ends reads the login line, once.</b> A, beside Crystal, names B; A ends
    /// the vow at Crystal and pays and reads as before, with no status line of its own.
    /// <list type="bullet">
    /// <item>B online, naming A: B's half ends (in memory and in the row) and B reads "Your engagement to A has ended."
    /// (or marriage) once, at the visit.</item>
    /// <item>B offline: the visit leaves B's row as it was, and B reads the same line once, at the next login (the
    /// arrival's settle), not twice.</item>
    /// <item>B online, naming C or nobody: nothing of B's ends, and B and C read no line.</item>
    /// </list>
    /// <para>Red on c7878f2, Debug and Release, for B online naming A: B reads no line (only the client's own "Love
    /// removed." for a marriage, which is not a status line the server sends). The other cases are green there: they
    /// guard against a line sent where nothing ended, or sent twice.</para>
    /// </summary>
    [Theory]
    [InlineData(false, PartnerAtTheVisit.Online)]
    [InlineData(false, PartnerAtTheVisit.Offline)]
    [InlineData(false, PartnerAtTheVisit.OnlineMovedOn)]
    [InlineData(false, PartnerAtTheVisit.OnlineSingle)]
    [InlineData(true, PartnerAtTheVisit.Online)]
    [InlineData(true, PartnerAtTheVisit.Offline)]
    [InlineData(true, PartnerAtTheVisit.OnlineMovedOn)]
    [InlineData(true, PartnerAtTheVisit.OnlineSingle)]
    public void APartnerWhoseHalfAChapelVisitEndsReadsTheLineOnce(bool married, PartnerAtTheVisit state)
    {
        using var db = new IsolatedDatabase();
        var (npc, def) = ChapelNpc();
        string a = Name("A"), b = Name("B"), c = Name("C");
        Seed(db, NewCharacter(a, def.Map, NearCrystal.X, NearCrystal.Y),
                 NewCharacter(b, PartnerAwayMap, 5, 5),
                 NewCharacter(c, PartnerAwayMap, 7, 5));

        Session? aHeld = null, bHeld = null, cHeld = null;
        try
        {
            var (aS, aOut) = Arrive(db.Store, a);
            aHeld = aS;
            var (bS, bOut) = Arrive(db.Store, b);
            bHeld = bS;
            var (cS, cOut) = Arrive(db.Store, c);
            cHeld = cS;
            Vow(aS, b, married);
            if (state is PartnerAtTheVisit.Online or PartnerAtTheVisit.Offline) Vow(bS, a, married);
            if (state == PartnerAtTheVisit.OnlineMovedOn)
            {
                Vow(bS, c, married);
                Vow(cS, b, married);
            }
            if (state == PartnerAtTheVisit.Offline)
            {
                Logout(bS);
                Release(b, bS);
                bHeld = null;
                Assert.True(RowVowed(db.Store, b, a, married), "B's row lost its half at logout");
            }
            uint aExp = aS.CharExp;
            aOut.Clear();
            bOut.Clear();
            cOut.Clear();

            OnOwnThread(() =>
            {
                OpenVowEntry(aS, aOut, npc, def, married);
                aS.Receive(MenuFrame(1));
                aS.Receive(NextFrame());
            });

            Assert.Equal(1, Shown(aOut, DonePage(married)));
            Assert.Equal(aExp - Price(married), aS.CharExp);
            Assert.True(VowGone(aS, married), $"A's own visit did not end A's half: {VowState(aS)}");
            Assert.DoesNotContain(SpellCastSupport.MiniTexts(aOut), l => l.Contains(" has ended."));

            var bLines = SpellCastSupport.MiniTexts(bOut);
            switch (state)
            {
                case PartnerAtTheVisit.Online:
                    _out.WriteLine($"[{Kind(married)}, B {state}] B {VowState(bS)}; B's lines: [{string.Join(" | ", bLines)}]");
                    Assert.True(VowGone(bS, married), $"A's visit did not end B's half: {VowState(bS)}");
                    Assert.True(RowVowGone(db.Store, b, married, keepsakes: 0), $"B's row: {RowVowState(db.Store, b)}");
                    Assert.Equal(1, bLines.Count(l => l == Line(married, a)));
                    Assert.Equal(1, bLines.Count(l => l.Contains(" has ended.")));
                    break;
                case PartnerAtTheVisit.Offline:
                    Assert.True(RowVowed(db.Store, b, a, married), "the Chapel's offline path touched B's row");
                    var (bBack, bBackOut) = Arrive(db.Store, b);
                    bHeld = bBack;
                    var backLines = SpellCastSupport.MiniTexts(bBackOut);
                    _out.WriteLine($"[{Kind(married)}, B {state}] B at login {VowState(bBack)}; B's lines: " +
                                   $"[{string.Join(" | ", backLines)}]");
                    Assert.True(VowGone(bBack, married), $"B logged in still holding its half: {VowState(bBack)}");
                    Assert.Equal(1, backLines.Count(l => l == Line(married, a)));
                    Assert.Equal(1, backLines.Count(l => l.Contains(" has ended.")));
                    break;
                default:
                    _out.WriteLine($"[{Kind(married)}, B {state}] B {VowState(bS)}; B's lines: [{string.Join(" | ", bLines)}]");
                    if (state == PartnerAtTheVisit.OnlineMovedOn)
                    {
                        Assert.True(Vowed(bS, c, married, keepsakes: 1), $"A's visit changed B's vow to C: {VowState(bS)}");
                        Assert.True(Vowed(cS, b, married, keepsakes: 1), $"A's visit changed C's vow to B: {VowState(cS)}");
                    }
                    else
                        Assert.Equal("[married=False engaged=False spouse='' fiance='' love=0]", VowState(bS));
                    Assert.DoesNotContain(bLines, l => l.Contains(" has ended."));
                    Assert.DoesNotContain(SpellCastSupport.MiniTexts(cOut), l => l.Contains(" has ended."));
                    break;
            }
        }
        finally
        {
            Release(a, aHeld);
            Release(b, bHeld);
            Release(c, cHeld);
        }
    }

    /// <summary>How far the partner's logout has got when the Chapel's end reaches their monitor.</summary>
    public enum LogoutAt
    {
        /// <summary>The whole read-loop exit has run (<c>EndReadLoopAsync</c>): the teardown, then the close.</summary>
        Closed,
        /// <summary>The teardown has run and let go of the monitor (<c>TearDownWorldState</c>: <c>_leaving</c>, off the
        /// map, the slot parked, the row saved); the connection is closed only after the end, as
        /// <c>EndReadLoopAsync</c> closes it after the section.</summary>
        BeforeTheClose,
    }

    /// <summary>How the partner whose logout won their monitor comes back.</summary>
    public enum Relog
    {
        /// <summary>At once: the arrival is handed the departed session and fences it
        /// (<c>ClaimAccountSlot</c>, <c>KickForReplacement</c>).</summary>
        Fenced,
        /// <summary>After the departed entry is gone, an autosave interval later: the arrival loads the row as it
        /// stands.</summary>
        Later,
    }

    /// <summary>
    /// <b>A partner whose logout reaches their monitor before the Chapel's end does is told once, at the next
    /// login.</b> A, beside Crystal, and B, on another map, are engaged (or married) to each other. A's "Yes" finds B on
    /// its map, charges A and ends A's own half, then waits for B's monitor, which a third thread holds. Inside that
    /// section the third thread runs B's real read-loop exit (<c>EndReadLoopAsync</c>: off the map, the slot parked,
    /// the final save, the close) and then lets go, so A's end reaches B only once B is leaving. B's half is left as
    /// the offline path leaves it, in memory and in the row; B, logging back in at once (fenced) or later, reads
    /// "Your engagement to A has ended." (or marriage) exactly once, at that login, and holds no half after it. A pays
    /// and reads as before. Both kinds and both <c>StateRank</c> orders. Two cases stop B's logout between the
    /// teardown and the close (<see cref="LogoutAt.BeforeTheClose"/>), the gap in which B is leaving but its
    /// connection is not yet closed: the line sent there would not be read either, since the client has gone.
    /// <para>Red on 453e459, Debug and Release (PR #350's verdict F1; the blind check's D1): A's end ran on the departed
    /// session, so B's half ended in memory and in the row, the line went into the closed connection (or, before the
    /// close, into a connection about to close), and the next login had nothing left to settle: B was never told. The
    /// two cases before the close are also red with the check made on the closed connection instead of the leaving
    /// flag.</para>
    /// </summary>
    [Theory]
    [InlineData(false, false, Relog.Fenced, LogoutAt.Closed)]
    [InlineData(false, true, Relog.Fenced, LogoutAt.Closed)]
    [InlineData(true, false, Relog.Fenced, LogoutAt.Closed)]
    [InlineData(true, true, Relog.Fenced, LogoutAt.Closed)]
    [InlineData(false, true, Relog.Later, LogoutAt.Closed)]
    [InlineData(true, true, Relog.Later, LogoutAt.Closed)]
    [InlineData(false, false, Relog.Fenced, LogoutAt.BeforeTheClose)]
    [InlineData(true, true, Relog.Later, LogoutAt.BeforeTheClose)]
    public void APartnerWhoseLogoutWinsTheirMonitorIsToldOnceAtTheNextLogin(bool married, bool partnerRanksFirst, Relog relog,
                                                                             LogoutAt logout)
    {
        using var db = new IsolatedDatabase();
        var (npc, def) = ChapelNpc();
        string a = Name("A"), b = Name("B");
        var aRow = NewCharacter(a, def.Map, NearCrystal.X, NearCrystal.Y);
        var bRow = NewCharacter(b, LeavingPartnerMap, 5, 5);
        VowRow(aRow, b, married);
        VowRow(bRow, a, married);
        Seed(db, aRow, bRow);

        Session aS, bS;
        RecordingOutbound aOut, bOut;
        if (partnerRanksFirst)
        {
            (bS, bOut) = Arrive(db.Store, b);
            (aS, aOut) = Arrive(db.Store, a);
        }
        else
        {
            (aS, aOut) = Arrive(db.Store, a);
            (bS, bOut) = Arrive(db.Store, b);
        }
        Assert.Equal(partnerRanksFirst, bS.StateRank < aS.StateRank);
        Assert.True(Vowed(aS, b, married, keepsakes: 1) && Vowed(bS, a, married, keepsakes: 1), "the pair did not log in vowed");

        var run = new HeldRun();
        Session? bBack = null;
        try
        {
            uint aExp = aS.CharExp;
            OnOwnThread(() => OpenVowEntry(aS, aOut, npc, def, married));
            aOut.Clear();
            bOut.Clear();

            bool foundWhileAWaited = false, aPaidAndEndedFirst = false, rowKeptThroughTeardown = false, goneFromMap = false;
            WhileAnotherThreadHolds(run, bS, () =>
            {
                aS.Receive(MenuFrame(1));   // "Yes": the re-check, the charge, A's own half, then B's (waits for B)
                aS.Receive(NextFrame());
            }, untouched: () =>
            {
                // Inside B's section, once A's thread is seen waiting for it: the window, then B's real logout.
                foundWhileAWaited = ReferenceEquals(_fx.World.Online.FindPlayer(b), bS);
                var aNow = Row(db.Store, a);
                aPaidAndEndedFirst = aNow.Exp == aExp - Price(married) && RowVowGone(db.Store, a, married, keepsakes: 0);
                if (logout == LogoutAt.Closed) Logout(bS);
                else TearDownWorldState.Invoke(bS, null);
                rowKeptThroughTeardown = RowVowed(db.Store, b, a, married);
                goneFromMap = _fx.World.Online.FindPlayer(b) is null;
                return foundWhileAWaited && aPaidAndEndedFirst && rowKeptThroughTeardown && goneFromMap;
            });
            _out.WriteLine($"[{Kind(married)}, B ranks first {partnerRanksFirst}, relog {relog}, {logout}] A waited for B: " +
                           $"{!run.FinishedWhileHeld}; B found then {foundWhileAWaited}; A charged and its half saved first " +
                           $"{aPaidAndEndedFirst}; through B's teardown B's row kept its half {rowKeptThroughTeardown}, B off its map " +
                           $"{goneFromMap}");
            AssertWaited(run, "A's end", "the window was not opened as described");
            if (logout == LogoutAt.BeforeTheClose) CloseConnection.Invoke(bS, new object[] { "read-loop exit" });

            string bAfterVisit = VowState(bS), bRowAfterVisit = RowVowState(db.Store, b);
            bool bKeptItsHalf = Vowed(bS, a, married, keepsakes: 1) && RowVowed(db.Store, b, a, married);
            int toldAtVisit = SpellCastSupport.MiniTexts(bOut).Count(l => l.Contains(" has ended."));

            if (relog == Relog.Later) Release(b, bS);   // the departed entry pruned, as the sweep does an interval later
            (bBack, var bBackOut) = Arrive(db.Store, b);
            var atLogin = SpellCastSupport.MiniTexts(bBackOut);
            _out.WriteLine($"   after A's end: B {bAfterVisit}, B's row {bRowAfterVisit}; B's lines then {toldAtVisit}; " +
                           $"B back {VowState(bBack)}, row {RowVowState(db.Store, b)}; lines at login [{string.Join(" | ", atLogin)}]; " +
                           $"A paid {aExp - aS.CharExp}, A {VowState(aS)}");

            Assert.Equal(aExp - Price(married), aS.CharExp);
            Assert.Equal(1, Shown(aOut, DonePage(married)));
            Assert.True(VowGone(aS, married), $"A holds a half: {VowState(aS)}");
            Assert.Equal(1, toldAtVisit + atLogin.Count(l => l.Contains(" has ended.")));
            Assert.Equal(1, atLogin.Count(l => l == Line(married, a)));
            Assert.True(bKeptItsHalf, $"A's end ran on B's departed session: B {bAfterVisit}, B's row {bRowAfterVisit}");
            Assert.True(VowGone(bBack, married), $"B logged back in still holding its half: {VowState(bBack)}");
            Assert.True(RowVowGone(db.Store, b, married, keepsakes: 0), $"B's row: {RowVowState(db.Store, b)}");
            Assert.True(RowVowGone(db.Store, a, married, keepsakes: 0), $"A's row: {RowVowState(db.Store, a)}");
        }
        finally
        {
            if (run.Settled)
            {
                Release(a, aS);
                Release(b, bS);
                Release(b, bBack);
            }
        }
    }

    /// <summary>How the server closes a partner's connection before that partner's read loop runs its teardown.</summary>
    public enum ServerClose
    {
        /// <summary>A GM kick or ban: <c>Session.Disconnect</c>, a close with a drain, which on a socket keeps the read
        /// loop alive while the writer drains.</summary>
        Kick,
        /// <summary>The outbound writer's exit after a write failed on a dead socket: its <c>onExit</c> is
        /// <c>CloseConnection</c> ("writer exit"). The slow-client drop in <c>Send</c> makes the same call.</summary>
        WriterExit,
    }

    /// <summary>
    /// <b>A partner whose connection the server closed first is told once, at the next login.</b> A, beside Crystal, and
    /// B, on another map, are engaged (or married) to each other, and A's dialog waits at its last question. The server
    /// closes B's connection itself (a GM kick, or the writer's exit), so B is closed but not yet leaving: still on its
    /// map, found by name, its read loop not yet woken. A's real "Yes" ends the vow; then B's read loop exits for real
    /// (<c>EndReadLoopAsync</c>: the teardown and its save), and B logs back in, at once (fenced) or later. B's half is
    /// kept through A's end, in memory and in the row, and B reads "Your engagement to A has ended." (or marriage)
    /// exactly once, at that login. A pays and reads as before.
    /// <para>The order of the two flags is the other way round from the logout fact above: here the server's close sets
    /// <c>_closed</c> first, without the monitor, and the read loop's teardown sets <c>_leaving</c> only later. A kick
    /// does the same in play: it sets <c>_closed</c> before it closes the socket the read loop waits on. The writer's
    /// exit does it here only because the fact makes its close directly, before the read loop exits; in play it closes
    /// the socket first and sets <c>_closed</c> after, so it races the teardown (<c>Session.EndEngagement</c>'s doc).
    /// The told end checks both.</para>
    /// <para>Red on b787430, Debug and Release (PR #350 re-check, Finding 1, probe P1): the told end checked
    /// <c>_leaving</c> alone, so it ended B's half and wrote the row; the line was dropped at the closed connection;
    /// and the teardown and the next login had nothing left to settle: B was never told.</para>
    /// </summary>
    [Theory]
    [InlineData(false, false, ServerClose.Kick, Relog.Fenced)]
    [InlineData(true, true, ServerClose.Kick, Relog.Fenced)]
    [InlineData(false, true, ServerClose.WriterExit, Relog.Fenced)]
    [InlineData(true, false, ServerClose.WriterExit, Relog.Fenced)]
    [InlineData(false, true, ServerClose.Kick, Relog.Later)]
    [InlineData(true, false, ServerClose.WriterExit, Relog.Later)]
    public void APartnerWhoseConnectionTheServerClosedFirstIsToldOnceAtTheNextLogin(bool married, bool partnerRanksFirst,
                                                                                    ServerClose how, Relog relog)
    {
        using var db = new IsolatedDatabase();
        var (npc, def) = ChapelNpc();
        string a = Name("A"), b = Name("B");
        var aRow = NewCharacter(a, def.Map, NearCrystal.X, NearCrystal.Y);
        var bRow = NewCharacter(b, ClosedPartnerMap, 5, 5);
        VowRow(aRow, b, married);
        VowRow(bRow, a, married);
        Seed(db, aRow, bRow);

        Session? aHeld = null, bHeld = null, bBack = null;
        try
        {
            Session aS, bS;
            RecordingOutbound aOut, bOut;
            if (partnerRanksFirst)
            {
                (bS, bOut) = Arrive(db.Store, b);
                bHeld = bS;
                (aS, aOut) = Arrive(db.Store, a);
                aHeld = aS;
            }
            else
            {
                (aS, aOut) = Arrive(db.Store, a);
                aHeld = aS;
                (bS, bOut) = Arrive(db.Store, b);
                bHeld = bS;
            }
            Assert.Equal(partnerRanksFirst, bS.StateRank < aS.StateRank);
            Assert.True(Vowed(aS, b, married, keepsakes: 1) && Vowed(bS, a, married, keepsakes: 1), "the pair did not log in vowed");
            uint aExp = aS.CharExp;
            OnOwnThread(() => OpenVowEntry(aS, aOut, npc, def, married));
            aOut.Clear();
            bOut.Clear();

            // The server closes B's connection; B's read loop has not woken to it yet.
            if (how == ServerClose.Kick) bS.Disconnect("vow fact: a GM kick");
            else CloseConnection.Invoke(bS, new object[] { "writer exit" });
            bool closed = (int)ClosedField.GetValue(bS)! != 0, found = ReferenceEquals(_fx.World.Online.FindPlayer(b), bS);
            bool leaving = (bool)LeavingField.GetValue(bS)!;

            OnOwnThread(() =>
            {
                aS.Receive(MenuFrame(1));
                aS.Receive(NextFrame());
            });
            string bAfterVisit = VowState(bS), bRowAfterVisit = RowVowState(db.Store, b);
            bool bKeptItsHalf = Vowed(bS, a, married, keepsakes: 1) && RowVowed(db.Store, b, a, married);
            int toldAtVisit = SpellCastSupport.MiniTexts(bOut).Count(l => l.Contains(" has ended."));

            Logout(bS);   // B's read loop wakes to the closed socket: the teardown and its save
            string bRowAfterTeardown = RowVowState(db.Store, b);
            if (relog == Relog.Later) Release(b, bS);   // the departed entry pruned, as the sweep does an interval later
            (bBack, var bBackOut) = Arrive(db.Store, b);
            var atLogin = SpellCastSupport.MiniTexts(bBackOut);
            _out.WriteLine($"[{Kind(married)}, B ranks first {partnerRanksFirst}, {how}, relog {relog}] before A's Yes: B closed " +
                           $"{closed}, leaving {leaving}, found by name {found}");
            _out.WriteLine($"   after A's end: B {bAfterVisit}, B's row {bRowAfterVisit}; B's lines then {toldAtVisit}; after B's " +
                           $"teardown B's row {bRowAfterTeardown}; B back {VowState(bBack)}, row {RowVowState(db.Store, b)}; " +
                           $"lines at login [{string.Join(" | ", atLogin)}]; A paid {aExp - aS.CharExp}, A {VowState(aS)}");

            Assert.True(closed && !leaving && found, "B was not closed-but-not-leaving and findable when A answered");
            Assert.Equal(aExp - Price(married), aS.CharExp);
            Assert.Equal(1, Shown(aOut, DonePage(married)));
            Assert.True(VowGone(aS, married), $"A holds a half: {VowState(aS)}");
            Assert.Equal(1, toldAtVisit + atLogin.Count(l => l.Contains(" has ended.")));
            Assert.Equal(1, atLogin.Count(l => l == Line(married, a)));
            Assert.True(bKeptItsHalf, $"A's end ran on B's closed session: B {bAfterVisit}, B's row {bRowAfterVisit}");
            Assert.True(VowGone(bBack, married), $"B logged back in still holding its half: {VowState(bBack)}");
            Assert.True(RowVowGone(db.Store, b, married, keepsakes: 0), $"B's row: {RowVowState(db.Store, b)}");
            Assert.True(RowVowGone(db.Store, a, married, keepsakes: 0), $"A's row: {RowVowState(db.Store, a)}");
        }
        finally
        {
            Release(a, aHeld);
            Release(b, bHeld);
            Release(b, bBack);
        }
    }

    /// <summary>
    /// <b>The Chapel's line to the partner is sent inside the partner's monitor.</b> A, beside Crystal, ends the vow; B,
    /// online on another map and naming A, reads the line. B's outbound records, for every frame, whether the thread
    /// that handed it over held B's monitor at that moment (<c>Session.StateHeld</c>; a send reaches the outbound
    /// synchronously, on the sending thread). B's line, and every other frame the end sends B (for a marriage the
    /// keepsake's <c>0x10</c>), must be sent with B's monitor held: the send numbers the packet with B's own counter,
    /// which that monitor guards. Both <c>StateRank</c> orders.
    /// <para>Green on 453e459. Red, Debug and Release, with the send moved to just after the section that ends B's half
    /// (PR #350 review A, probe P1; the blind check's MOUT): A's thread sends B's line after it has let go of B's
    /// monitor. No other fact in this class sees that move.</para>
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TheChapelSendsThePartnersLineInsideThePartnersMonitor(bool married, bool partnerRanksFirst)
    {
        using var db = new IsolatedDatabase();
        var (npc, def) = ChapelNpc();
        string a = Name("A"), b = Name("B");
        var aRow = NewCharacter(a, def.Map, NearCrystal.X, NearCrystal.Y);
        var bRow = NewCharacter(b, MonitorProbeMap, 5, 5);
        VowRow(aRow, b, married);
        VowRow(bRow, a, married);
        Seed(db, aRow, bRow);

        Session? aHeld = null, bHeld = null;
        try
        {
            Session aS, bS;
            RecordingOutbound aOut;
            HeldOutbound bOut;
            if (partnerRanksFirst)
            {
                (bS, bOut) = ArriveHeld(db.Store, b);
                bHeld = bS;
                (aS, aOut) = Arrive(db.Store, a);
                aHeld = aS;
            }
            else
            {
                (aS, aOut) = Arrive(db.Store, a);
                aHeld = aS;
                (bS, bOut) = ArriveHeld(db.Store, b);
                bHeld = bS;
            }
            Assert.Equal(partnerRanksFirst, bS.StateRank < aS.StateRank);
            bOut.Clear();

            OnOwnThread(() =>
            {
                OpenVowEntry(aS, aOut, npc, def, married);
                aS.Receive(MenuFrame(1));
                aS.Receive(NextFrame());
            });
            var sent = bOut.Sent();
            var lines = sent.Where(s => s.Op == ServerOp.MiniText).Select(s => (Text: MiniTextOf(s.Body), s.Held)).ToList();
            _out.WriteLine($"[{Kind(married)}, B ranks first {partnerRanksFirst}] B's frames: " +
                           $"[{string.Join(" ", sent.Select(s => $"0x{s.Op:x2}:held={s.Held}"))}]; B's lines: " +
                           $"[{string.Join(" | ", lines.Select(l => $"'{l.Text}' held={l.Held}"))}]");

            Assert.True(VowGone(bS, married), $"A's visit did not end B's half: {VowState(bS)}");
            var line = Assert.Single(lines, l => l.Text.Contains(" has ended."));
            Assert.Equal(Line(married, a), line.Text);
            Assert.True(line.Held, "B's line was sent by a thread that did not hold B's monitor");
            Assert.All(sent, s => Assert.True(s.Held, $"B's frame 0x{s.Op:x2} was sent without B's monitor"));
        }
        finally
        {
            Release(a, aHeld);
            Release(b, bHeld);
        }
    }

    /// <summary>
    /// <b>The Chapel's end lets go of the clicker's own monitor while it waits for a partner who ranks before
    /// it.</b> A, beside Crystal, and B, on another map, are engaged (or married) to each other, and B ranks before A.
    /// A's dialog waits at its last question. A third thread takes B's monitor, and A's "Yes", on a thread of its own,
    /// charges A, ends A's own half and then waits to end B's: <c>EndEngagement</c> (or <c>EndMarriage</c>) on B, from
    /// a thread that holds A's monitor, a descending entry. Then the third thread, still inside B, takes A's monitor,
    /// which is what B's own Chapel visit does next, when it ends A's half (an ascending entry, so it keeps B's). It
    /// gets it at once: the end enters B's monitor by <c>StateRank</c>, and <c>EnterState</c>'s descending case drops
    /// A's own while it waits. Once the third thread lets go, B's half ends and B reads the line once; A pays and reads
    /// as before. Deterministic, and bounded: the take is a <c>Monitor.TryEnter</c> with a 5 s timeout, so a monitor
    /// that never comes free fails the fact instead of hanging it. B stands on a map of its own, so nothing of A's map
    /// reaches B.
    /// <para>Red with the partner's half under a plain lock on its monitor object (<c>lock (_state)</c> in place of
    /// <c>EnterState</c> in <c>EndEngagement</c>, or in <c>EndMarriage</c>; PR #350's review, N1), Debug and Release:
    /// A keeps its own monitor while it waits, and the third thread cannot take it in 5 s. In play that is two
    /// partners answering at the Chapel at once and waiting on each other for good, which before this fact only the
    /// race below caught.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheChapelsEndDropsTheClickersMonitorWhileItWaitsForALowerRankedPartner(bool married)
    {
        using var db = new IsolatedDatabase();
        var (npc, def) = ChapelNpc();
        string a = Name("A"), b = Name("B");
        var aRow = NewCharacter(a, def.Map, NearCrystal.X, NearCrystal.Y);
        var bRow = NewCharacter(b, RankedPartnerMap, 5, 5);
        VowRow(aRow, b, married);
        VowRow(bRow, a, married);
        Seed(db, aRow, bRow);

        var (bS, bOut) = Arrive(db.Store, b);
        var (aS, aOut) = Arrive(db.Store, a);
        Assert.True(bS.StateRank < aS.StateRank, "B must rank before A, so that A's end of B's half is the descending entry");
        Assert.True(Vowed(aS, b, married, keepsakes: 1) && Vowed(bS, a, married, keepsakes: 1), "the pair did not log in vowed");
        object aMonitor = StateField.GetValue(aS)!;

        var run = new HeldRun();
        bool aPaidAndEndedFirst = false, bKeptItsHalf = false, tookA = false;
        long waitedMs = -1;
        try
        {
            uint aExp = aS.CharExp;
            OnOwnThread(() => OpenVowEntry(aS, aOut, npc, def, married));
            aOut.Clear();
            bOut.Clear();

            // The check runs inside the third thread's section on B, once A's "Yes" is seen waiting for it.
            WhileAnotherThreadHolds(run, bS, () =>
            {
                aS.Receive(MenuFrame(1));   // "Yes": the re-check, the charge, A's own half, then B's (waits for B)
                aS.Receive(NextFrame());
            }, untouched: () =>
            {
                var aNow = Row(db.Store, a);
                aPaidAndEndedFirst = aNow.Exp == aExp - Price(married) && RowVowGone(db.Store, a, married, keepsakes: 0);
                bKeptItsHalf = Vowed(bS, a, married, keepsakes: 1);
                var clock = Stopwatch.StartNew();
                tookA = Monitor.TryEnter(aMonitor, TimeSpan.FromSeconds(5));
                waitedMs = clock.ElapsedMilliseconds;
                if (tookA) Monitor.Exit(aMonitor);
                return tookA;
            });
            var bLines = SpellCastSupport.MiniTexts(bOut);
            _out.WriteLine($"[{Kind(married)}] A's end waited for B: {!run.FinishedWhileHeld}; then A had paid and ended its " +
                           $"own half {aPaidAndEndedFirst}, B still held its half {bKeptItsHalf}; the third thread, inside B, " +
                           $"took A's monitor: {tookA} after {waitedMs} ms; after: A {VowState(aS)}, B {VowState(bS)}, " +
                           $"B's lines [{string.Join(" | ", bLines)}]");

            AssertWaited(run, "A's end",
                "A kept its own monitor while it waited for B's: B's own Chapel visit, which takes A's next, would deadlock with it");
            Assert.True(aPaidAndEndedFirst && bKeptItsHalf, "the window was not opened as described");
            Assert.Equal(aExp - Price(married), aS.CharExp);
            Assert.Equal(1, Shown(aOut, DonePage(married)));
            Assert.True(VowGone(aS, married), $"A holds a half: {VowState(aS)}");
            Assert.True(VowGone(bS, married), $"A's visit did not end B's half: {VowState(bS)}");
            Assert.True(RowVowGone(db.Store, b, married, keepsakes: 0), $"B's row: {RowVowState(db.Store, b)}");
            Assert.Equal(1, bLines.Count(l => l == Line(married, a)));
            Assert.Equal(1, bLines.Count(l => l.Contains(" has ended.")));
        }
        finally
        {
            if (run.Settled)
            {
                Release(a, aS);
                Release(b, bS);
            }
        }
    }

    // ===== races ===========================================================================================

    /// <summary>
    /// <b>Two partners at the Chapel at once never stall, and each loses one keepsake.</b> A and B stand beside Crystal
    /// engaged (or married) to each other, each with a bought love besides the keepsake. Each round both renew their
    /// half, both open Break Off Engagement (or Divorce) to its last question, and both answer "Yes", each on its own
    /// thread, while the world ticks: at the same moment, or one up to 7 ms after the other (the delay steps by 1 ms
    /// every other round and alternates sides), so the answers both cross and follow one another. Each answer ends its
    /// own half and then the partner's inside the partner's monitor, so one write ascends and the other descends.
    /// Nothing may stall; both end every round with no vow, and (the marriage) with exactly the bought love: one
    /// keepsake taken from each, never two.
    /// <para>Red on 3690542, Debug and Release (the marriage): a second love is taken. When the two answers cross, each
    /// partner's half is run again by the other's answer, whomever it names by then. When one answer finishes first, the
    /// other's own half is run again, whatever it holds. The engagement has no keepsake, so its round ends the same
    /// either way: it pins that nothing stalls.</para>
    /// <para><b>And no answer pays for a vow it did not end</b> (#344's Follow-ups). Each side's answer is either the
    /// one that ended its own half, which pays the price once, reads "It is done." (or "You are now divorced.") and
    /// reads no status line; or it came after the partner's answer had already ended it, which pays nothing, reads
    /// Crystal's "has already ended." and read the partner's status line once. A side whose half was already gone just
    /// before it answered must be the second kind, and every round someone pays. When the answers cross, both pay:
    /// each one ended its own half before the other's reached it.</para>
    /// <para>Red on c7878f2, Debug and Release, both kinds: a side whose half was already gone before it answered is
    /// charged and reads the paid line, and no side reads a status line.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoPartnersAtTheChapelAtOnceNeverStallAndEachLosesOneKeepsake(bool married)
    {
        using var db = new IsolatedDatabase();
        var (npc, def) = ChapelNpc();
        string a = Name("A"), b = Name("B");
        Seed(db, NewCharacter(a, def.Map, NearCrystal.X, NearCrystal.Y, maxHp: 100),
                 NewCharacter(b, def.Map, AlsoNearCrystal.X, AlsoNearCrystal.Y, maxHp: 100));

        Session? aHeld = null, bHeld = null;
        var race = new Race();
        int wrong = 0, staleBeforeTheirYes = 0, free = 0;
        var payers = new int[ChapelRaceRounds];
        string? firstWrong = null;
        uint price = Price(married, maxHp: 100);
        try
        {
            var (aS, aOut) = Arrive(db.Store, a);
            aHeld = aS;
            var (bS, bOut) = Arrive(db.Store, b);
            bHeld = bS;
            var barrier = new Barrier(2);

            void Partnered(Session self, RecordingOutbound selfOut, string other, int side)
            {
                for (int i = 0; i < ChapelRaceRounds; i++)
                {
                    selfOut.Clear();
                    Vow(self, other, married);
                    if (married) TopUpLove(self, 2);
                    barrier.SignalAndWait(race.Cancel.Token);
                    OpenVowEntry(self, selfOut, npc, def, married);
                    barrier.SignalAndWait(race.Cancel.Token);
                    if (i % 2 == side) Spin(Stagger(i, stepMs: 1.0));
                    bool goneBefore = VowGone(self, married);   // a half, once gone, stays gone this round
                    uint before = self.WithState(() => self.CharExp);
                    self.Receive(MenuFrame(1));
                    self.Receive(NextFrame());
                    barrier.SignalAndWait(race.Cancel.Token);
                    string state = VowState(self);
                    uint paid = before - self.WithState(() => self.CharExp);
                    int done = Shown(selfOut, DonePage(married)), already = Shown(selfOut, AlreadyLine(married, other));
                    int lines = SpellCastSupport.MiniTexts(selfOut).Count(l => l.Contains(" has ended."));
                    int toldRight = SpellCastSupport.MiniTexts(selfOut).Count(l => l == Line(married, other));
                    bool ended = VowGone(self, married) && !self.DialogBusy &&
                                 (!married || self.WithState(() => self.CountItem("love")) == 1);
                    bool endedIt = paid == price && done == 1 && already == 0 && lines == 0;
                    bool cameAfter = paid == 0 && done == 0 && already == 1 && lines == 1 && toldRight == 1;
                    bool right = ended && (endedIt || cameAfter) && !(goneBefore && paid != 0);
                    if (goneBefore) Interlocked.Increment(ref staleBeforeTheirYes);
                    if (cameAfter) Interlocked.Increment(ref free);
                    if (paid != 0) Interlocked.Increment(ref payers[i]);
                    if (!right && Interlocked.Increment(ref wrong) == 1)
                        firstWrong = $"round {i}: {self.CharName} {state}, half gone before its Yes: {goneBefore}, paid {paid} " +
                                     $"of {price}, read done {done} / already {already}, status lines {lines}";
                    barrier.SignalAndWait(race.Cancel.Token);
                    race.Progress.Bump();
                }
            }

            RunRace(race, married ? "the two spouses divorcing at the Chapel at once" : "the two partners breaking it off at once",
                    () => Partnered(aS, aOut, b, side: 0), () => Partnered(bS, bOut, a, side: 1));
            int crossed = payers.Count(p => p == 2), unpaid = payers.Count(p => p == 0);
            _out.WriteLine($"[{Kind(married)}] {race.Progress.Rounds} partner-rounds, {wrong} wrong; rounds where both paid " +
                           $"(crossed) {crossed}, one paid {payers.Count(p => p == 1)}, nobody paid {unpaid}; answers free {free}, " +
                           $"half gone before the Yes {staleBeforeTheirYes}; first wrong: {firstWrong ?? "none"}");

            Assert.Null(race.Fault);
            Assert.Equal(2 * ChapelRaceRounds, race.Progress.Rounds);
            Assert.True(wrong == 0, $"{wrong} partner-rounds ended wrong; the first: {firstWrong}");
            Assert.True(unpaid == 0, $"{unpaid} rounds ended the vow with nobody paying");
        }
        finally
        {
            race.Done = true;
            if (race.Settled)
            {
                Release(a, aHeld);
                Release(b, bHeld);
            }
        }
    }

    /// <summary>
    /// <b>An arrival racing the partner's Chapel visit never stalls, and ends clear.</b> Each round A and B are engaged
    /// (or married) to each other, consistent, with B offline; A logs in. Then, at the same moment, B logs in through
    /// the real arrival on one thread while A ends the vow on another (<c>BreakOffEngagement</c> or
    /// <c>FinishDivorce</c> under A's monitor, as the Chapel's dialog runs it). Nothing may stall, and every round ends
    /// with neither holding the vow, in memory or in their rows, and one keepsake taken from each: whichever reaches B
    /// first, A's visit (B already on its map, so found) or B's own check (A already cleared). The rounds alternate
    /// which of the two sessions was built first, so both <c>StateRank</c> orders are run, and A's visit starts after a
    /// stagger of 0 to 2.8 ms that steps every other round, so it lands before, during and after B's arrival.
    /// <para>Red on 3690542, Debug and Release: when A's visit runs before B is on a map, B keeps its half.</para>
    /// <para>And B reads "Your engagement to A has ended." (or marriage) exactly once every round, whichever ended B's
    /// half: A's visit tells an online partner from inside their monitor, B's own check tells B, and the loser of the
    /// two finds nothing left to end and says nothing (#344's Follow-ups). Red on c7878f2, Debug and Release: in a
    /// round A's visit clears, B reads no line.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnArrivalRacingThePartnersChapelVisitNeverStallsAndEndsClear(bool married)
    {
        using var db = new IsolatedDatabase();
        string a = Name("A"), b = Name("B");
        var race = new Race();
        int wrong = 0, cleared = 0, untold = 0;
        string? firstWrong = null;
        Session? aS = null, bS = null;

        try
        {
            var barrier = new Barrier(2);

            void BSide()
            {
                for (int i = 0; i < ArrivalRaceRounds; i++)
                {
                    var aRow = NewCharacter(a, ArrivalRaceMap, 5, 5);
                    var bRow = NewCharacter(b, ArrivalRaceMap, 7, 5);
                    VowRow(aRow, b, married);
                    VowRow(bRow, a, married);
                    Seed(db, aRow, bRow);
                    RecordingOutbound bOut;
                    if (i % 2 == 0) { (bS, bOut) = NewSession(db.Store, b); (aS, _) = Arrive(db.Store, a); }
                    else { (aS, _) = Arrive(db.Store, a); (bS, bOut) = NewSession(db.Store, b); }
                    Assert.True(Vowed(aS, b, married, keepsakes: 1), $"round {i}: A's own login ended a consistent pair");

                    barrier.SignalAndWait(race.Cancel.Token);
                    bS.Receive(ArrivalFrame(b));
                    barrier.SignalAndWait(race.Cancel.Token);

                    var bLines = SpellCastSupport.MiniTexts(bOut);
                    bool told = bLines.Count(l => l == Line(married, a)) == 1 && bLines.Count(l => l.Contains(" has ended.")) == 1;
                    bool right = !bOut.Closed
                                 && VowGone(aS, married) && VowGone(bS, married)
                                 && (!married || (aS.WithState(() => aS.CountItem("love")) == 0
                                                  && bS.WithState(() => bS.CountItem("love")) == 0))
                                 && RowVowGone(db.Store, a, married, keepsakes: 0)
                                 && RowVowGone(db.Store, b, married, keepsakes: 0);
                    if (right) cleared++;
                    if (right && !told) untold++;
                    if ((!right || !told) && Interlocked.Increment(ref wrong) == 1)
                        firstWrong = $"round {i}: A {VowState(aS)}, B {VowState(bS)}, B's row {RowVowState(db.Store, b)}, " +
                                     $"B's lines [{string.Join(" | ", bLines)}]";

                    Logout(bS);
                    Release(b, bS);
                    Logout(aS);
                    Release(a, aS);
                    (aS, bS) = (null, null);
                    race.Progress.Bump();
                }
            }

            void ASide()
            {
                for (int i = 0; i < ArrivalRaceRounds; i++)
                {
                    barrier.SignalAndWait(race.Cancel.Token);
                    var self = aS!;
                    Spin(Stagger(i));
                    self.WithState(() => { if (married) self.FinishDivorce(); else self.BreakOffEngagement(); });
                    barrier.SignalAndWait(race.Cancel.Token);
                }
            }

            RunRace(race, married ? "a spouse logging in while the other divorces" : "a fiance logging in while the other breaks it off",
                    BSide, ASide);
            _out.WriteLine($"[{Kind(married)}] {race.Progress.Rounds} rounds: both cleared {cleared}, of them B not told " +
                           $"exactly once {untold}; {wrong} wrong; first: {firstWrong ?? "none"}");

            Assert.Null(race.Fault);
            Assert.Equal(ArrivalRaceRounds, race.Progress.Rounds);
            Assert.True(wrong == 0, $"{wrong} rounds ended wrong; the first: {firstWrong}");
        }
        finally
        {
            race.Done = true;
            if (race.Settled)
            {
                Release(a, aS);
                Release(b, bS);
            }
        }
    }

    // ===== logging in and out ==============================================================================

    private static string Name(string tag) => $"Vh{tag}{Interlocked.Increment(ref _serial)}";

    private static string Kind(bool married) => married ? "marriage" : "engagement";

    /// <summary>The line an arrival reads when its half ends there.</summary>
    private static string Line(bool married, string partner) =>
        married ? $"Your marriage to {partner} has ended." : $"Your engagement to {partner} has ended.";

    /// <summary>What Crystal says to a confirm whose vow the partner already ended.</summary>
    private static string AlreadyLine(bool married, string partner) =>
        married ? $"Your marriage to {partner} has already ended." : $"Your engagement to {partner} has already ended.";

    /// <summary>The Chapel's last line for a paid break or divorce.</summary>
    private static string DonePage(bool married) => married ? "You are now divorced." : "It is done.";

    /// <summary>The divorce's page for a player with too little experience, before the sacrifice is offered.</summary>
    private const string SacrificeOffer =
        "Hmmm.. you don't have the experience to divorce, but there is something else you can offer.";

    /// <summary>What Crystal says to a player whose vow still holds, for a "No" to Break Off Engagement's question or to
    /// Divorce's first, for Marriage picked by the partner who proposed, and for the ceremony's question answered "Yes"
    /// while the fiancé is offline, or "No". Before PR #350's N3 follow-up a player whose vow the partner had already
    /// ended read them too.</summary>
    private static readonly string[] StillVowedLines =
    {
        "I hope you can salvage your relationship.",
        "Patience and love will save your marriage.\n\nDivorce is not something to take lightly.",
        "The proposee should start the marriage ceremony.",
        "Both parties must be present for the ceremony to commence",
        "Come back when you are firm in your resolve to marry.",
    };

    /// <summary>What the Chapel charges in experience: a divorce <c>MaxHp * 2550</c>, a break <c>MaxMp * 1000</c>
    /// (<see cref="NewCharacter"/> gives 100 Mana).</summary>
    private static uint Price(bool married, uint maxHp = 1_000) => married ? maxHp * 2550 : 100u * 1000;

    /// <summary>Experience and the two base pools: everything the Chapel's price can be paid in.</summary>
    private static (uint Exp, uint MaxHp, uint MaxMp) Pools(Session s) =>
        s.WithState(() => (s.CharExp, s.CharMaxHp, s.CharMaxMp));

    private static (uint Exp, uint MaxHp, uint MaxMp) Pools(Character c) => (c.Exp, c.MaxHp, c.MaxMp);

    /// <summary>A character as creation writes it, standing on <paramref name="map"/>, with experience enough for the
    /// Chapel's prices (a divorce is <c>MaxHp * 2550</c>, a broken engagement <c>MaxMp * 1000</c>).</summary>
    private static Character NewCharacter(string name, ushort map, ushort x, ushort y, uint maxHp = 1_000)
    {
        var c = CharacterFactory.FromCreate(name, new byte[] { 0x29, 0, 1, 0, 0 });
        var info = Content.Maps.GetValueOrDefault(map);
        c.Map = map; c.X = x; c.Y = y;
        c.MapXs = info?.Xs ?? 20; c.MapYs = info?.Ys ?? 20;
        c.Level = 50;
        c.MaxHp = maxHp; c.Hp = maxHp;
        c.MaxMp = 100; c.Mp = 100;
        c.Exp = 1_000_000_000;
        return c;
    }

    private static void Seed(IsolatedDatabase db, params Character[] rows)
    {
        foreach (var c in rows) Assert.True(db.Store.Save(c), $"the setup save of {c.Name} failed");
    }

    /// <summary>Write <paramref name="c"/>'s half of a vow to <paramref name="other"/> into its row: what the proposal
    /// (<c>PromptProposal</c>) or the wedding (<c>RunMarriageCeremony</c>) leaves behind.</summary>
    private static void VowRow(Character c, string other, bool married)
    {
        if (married)
        {
            c.Legends.Add(new Legend(6, 1, $"Married to {other} ({Character.GameDate})", "married"));
            c.Spouse = other;
            GiveRow(c, "love");
        }
        else
        {
            c.Legends.Add(new Legend(6, 1, $"Engaged to {other} ({Character.GameDate})", "engaged"));
            c.Fiance = other;
            c.IsProposee = false;
            c.MarriageTimer = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 259200;
        }
    }

    private static void GiveRow(Character c, string key)
    {
        var def = Content.ItemByKey(key)!;
        byte slot = 0;
        while (c.Inventory.Any(i => i.Slot == slot)) slot++;
        c.Inventory.Add(new InvItem(slot, def.Id, 1));
    }

    /// <summary>The same half, written to an online player under its own monitor, as the proposal and the wedding
    /// write it.</summary>
    private static void Vow(Session self, string other, bool married) =>
        self.WithState(() =>
        {
            if (married)
            {
                self.AddLegend($"Married to {other} ({Character.GameDate})", "married", 6, 1);
                self.SetSpouse(other);
                self.GiveRewardItem("love", 1);
            }
            else
            {
                self.AddLegend($"Engaged to {other} ({Character.GameDate})", "engaged", 6, 1);
                self.SetEngaged(other, isProposee: false, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 259200);
            }
        });

    /// <summary>Loves in the bag up to <paramref name="count"/>: the keepsake, plus any bought from the Chapel's shop
    /// (which sells the same item).</summary>
    private static void TopUpLove(Session s, int count) =>
        s.WithState(() => { while (s.CountItem("love") < count) s.GiveRewardItem("love", 1); });

    private (Session Session, RecordingOutbound Outbound) NewSession(CharacterStore store, string name)
    {
        var outbound = new RecordingOutbound($"recorder:{name}");
        return (new Session(outbound, 2005, store, _fx.World), outbound);
    }

    /// <summary>A session that has logged in through the real <c>0x10</c> arrival.</summary>
    private (Session Session, RecordingOutbound Outbound) Arrive(CharacterStore store, string name)
    {
        var (s, outbound) = NewSession(store, name);
        s.Receive(ArrivalFrame(name));
        Assert.False(outbound.Closed, $"the arrival for '{name}' was refused");
        Assert.Contains(s, _fx.World.Online.All());
        return (s, outbound);
    }

    /// <summary>A session that has logged in through the real <c>0x10</c> arrival, on a <see cref="HeldOutbound"/>.</summary>
    private (Session Session, HeldOutbound Outbound) ArriveHeld(CharacterStore store, string name)
    {
        var outbound = new HeldOutbound($"held:{name}");
        var s = new Session(outbound, 2005, store, _fx.World);
        outbound.Owner = s;
        s.Receive(ArrivalFrame(name));
        Assert.False(outbound.Closed, $"the arrival for '{name}' was refused");
        Assert.Contains(s, _fx.World.Online.All());
        return (s, outbound);
    }

    /// <summary>An outbound that keeps every frame and whether the thread that handed it over held the owner's state
    /// monitor at that moment (<c>Session.StateHeld</c>, <c>Monitor.IsEntered</c>): <c>Session.Send</c> hands each
    /// frame to <see cref="IOutbound.Send"/> synchronously, on the sending thread.</summary>
    private sealed class HeldOutbound : IOutbound
    {
        private readonly List<(byte[] Frame, bool Held)> _frames = new();
        private readonly object _gate = new();
        public Session? Owner;

        public HeldOutbound(string remote) => Remote = remote;
        public string Remote { get; }
        public int Capacity => int.MaxValue;
        public int QueueDepth => 0;
        public bool Closed { get; private set; }

        public bool Send(byte[] frame)
        {
            bool held = Owner?.StateHeld ?? false;
            lock (_gate) _frames.Add((frame, held));
            return true;
        }

        public void Close() => Closed = true;

        public void Clear()
        {
            lock (_gate) _frames.Clear();
        }

        /// <summary>Every frame so far, decrypted as <see cref="RecordingOutbound.BodiesOf"/> does, with its flag.</summary>
        public List<(byte Op, byte[] Body, bool Held)> Sent()
        {
            List<(byte[] Frame, bool Held)> snap;
            lock (_gate) snap = _frames.ToList();
            var sent = new List<(byte Op, byte[] Body, bool Held)>();
            foreach (var (frame, held) in snap)
                if (TkPacket.TryParse(frame, out var pkt, out _))
                    sent.Add((pkt.Opcode, TkCrypt.Crypt(pkt.Body, pkt.Increment, TkCrypt.LoginKey), held));
            return sent;
        }
    }

    /// <summary>A <c>0x0A</c> body's text: the type, the u16BE length, the ASCII (<c>SpellCastSupport.MiniTexts</c>).</summary>
    private static string MiniTextOf(byte[] body) =>
        body.Length < 3 ? "" : Encoding.ASCII.GetString(body, 3, Math.Min((body[1] << 8) | body[2], body.Length - 3));

    /// <summary>The read loop's exit on a socket-free session: the teardown leaves the map, parks the slot and saves.
    /// Its only await is on an already-completed writer, so it runs to its end here.</summary>
    private static void Logout(Session s) =>
        Assert.True(s.EndReadLoopAsync(Task.CompletedTask).IsCompletedSuccessfully, "the logout did not run to its end");

    /// <summary>Off the map, and the account's slot out of the registry, so the next login for this name is not handed
    /// this session to fence (its kick would write this session's character over a row the next round seeded).</summary>
    private void Release(string name, Session? s)
    {
        if (s is null) return;
        string key = CharacterStore.Key(name);
        _fx.World.LeaveMap(s, s.CharMap);
        _fx.World.Online.Register(key, s, out _);
        _fx.World.Online.Unregister(key, s);
    }

    private static byte[] ArrivalFrame(string user)
    {
        var body = new List<byte> { 9 };
        body.AddRange(Encoding.ASCII.GetBytes("NexonInc."));
        body.Add((byte)user.Length);
        body.AddRange(Encoding.ASCII.GetBytes(user));
        body.AddRange(HandoffTokens.Mint(user, System.Net.IPAddress.None.ToString()));
        return TkPacket.Build(Opcode.Arrival, 0, body.ToArray());
    }

    // ===== what a player holds ================================================================================

    /// <summary>The whole half of a vow to <paramref name="partner"/>: the legend, the name and, for a marriage,
    /// <paramref name="keepsakes"/> loves. Read under the player's monitor.</summary>
    private static bool Vowed(Session s, string partner, bool married, int keepsakes) =>
        s.WithState(() => married
            ? s.HasLegend("married") && s.CharSpouseName == partner && s.CountItem("love") == keepsakes
            : s.HasLegend("engaged") && s.CharFiance == partner);

    /// <summary>No half of the vow: no legend and no name, and for an engagement no proposee flag or ceremony timer.
    /// The keepsake count is each fact's own business.</summary>
    private static bool VowGone(Session s, bool married) =>
        s.WithState(() => married
            ? !s.HasLegend("married") && s.CharSpouseName == ""
            : !s.HasLegend("engaged") && s.CharFiance == "" && !s.CharIsProposee && s.CharMarriageTimer == 0);

    private static string VowState(Session s) =>
        s.WithState(() => $"[married={s.HasLegend("married")} engaged={s.HasLegend("engaged")} spouse='{s.CharSpouseName}' " +
                          $"fiance='{s.CharFiance}' love={s.CountItem("love")}]");

    private static Character Row(CharacterStore store, string name)
    {
        var load = store.Load(name);
        Assert.Equal(CharacterLoadStatus.Ok, load.Status);
        return load.Character!;
    }

    private static int Loves(Character c)
    {
        int id = Content.ItemByKey("love")!.Id;
        return c.Inventory.Where(i => i.ItemId == id).Sum(i => i.Amount);
    }

    private static bool RowVowed(CharacterStore store, string name, string partner, bool married)
    {
        var c = Row(store, name);
        return married
            ? c.Legends.Any(l => l.Name == "married") && c.Spouse == partner && Loves(c) == 1
            : c.Legends.Any(l => l.Name == "engaged") && c.Fiance == partner;
    }

    private static bool RowVowGone(CharacterStore store, string name, bool married, int keepsakes)
    {
        var c = Row(store, name);
        return married
            ? c.Legends.All(l => l.Name != "married") && c.Spouse == "" && Loves(c) == keepsakes
            : c.Legends.All(l => l.Name != "engaged") && c.Fiance == "" && !c.IsProposee && c.MarriageTimer == 0;
    }

    private static string RowVowState(CharacterStore store, string name)
    {
        var c = Row(store, name);
        return $"[legends={string.Join("+", c.Legends.Where(l => l.Name != "").Select(l => l.Name))} spouse='{c.Spouse}' " +
               $"fiance='{c.Fiance}' love={Loves(c)}]";
    }

    // ===== the database ======================================================================================

    private static string RowJson(IsolatedDatabase db, string name) =>
        RowJsonOrNull(db, name) ?? throw new Xunit.Sdk.XunitException($"no row for {name}");

    private static string? RowJsonOrNull(IsolatedDatabase db, string name)
    {
        using var cn = db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT json FROM characters WHERE username=$u;";
        cmd.Parameters.AddWithValue("$u", CharacterStore.Key(name));
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>The row gone, as <c>LoginServer/Admin.cs</c>'s delete leaves it.</summary>
    private static void DeleteRow(IsolatedDatabase db, string name)
    {
        Exec(db, "DELETE FROM characters WHERE username=$u;", ("$u", CharacterStore.Key(name)));
        Assert.Equal(CharacterLoadStatus.NotFound, db.Store.Load(name).Status);
    }

    private static void Exec(IsolatedDatabase db, string sql, params (string Name, object Value)[] args)
    {
        using var cn = db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    // ===== the Chapel =========================================================================================

    /// <summary>Crystal and her row. Asserted, not assumed: she must be standing on her map in this world.</summary>
    private (Mob Npc, NpcDef Def) ChapelNpc()
    {
        var def = Content.NpcById(ChapelNpcId);
        Assert.True(def is not null && def.Key == "ChapelNpc", $"NPC {ChapelNpcId} is not a Chapel NPC");
        var npc = _fx.World.NpcsNear(def!.Map, def.X, def.Y, 0).FirstOrDefault(m => m.NpcDefId == ChapelNpcId);
        Assert.True(npc is not null, $"{def.Name} is not placed in the world");
        return (npc!, def);
    }

    private static void Click(Session s, Mob npc)
    {
        uint id = npc.Id;
        s.Receive(SessionFixture.Frame(ClientOp.ClickInfo,
            new byte[] { 0x01, (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id, 0x00 }));
    }

    /// <summary>Whether Crystal's top menu offers <paramref name="s"/> Break Off Engagement (or Divorce). The menu is
    /// then closed with a pick of 0, which ends the conversation (<c>Session.RunNpcAsync</c>).</summary>
    private static bool OfferedVowEntry(Session s, RecordingOutbound o, Mob npc, NpcDef def, bool divorce)
    {
        Click(s, npc);
        var labels = LastMenu(o, $"{def.Name}: How can I help you today?");
        s.Receive(MenuFrame(0));
        Assert.False(s.DialogBusy, "the top menu did not close");
        return labels.Contains(divorce ? "Divorce" : "Break Off Engagement");
    }

    /// <summary>Click Crystal and walk Break Off Engagement (or Divorce) up to its last question, which is left
    /// waiting: "Are you sure you want to end the engagement?" or "It will cost N experience. Are you sure you want
    /// this divorce?". The entry is found by its label on the wire.</summary>
    private static void OpenVowEntry(Session s, RecordingOutbound o, Mob npc, NpcDef def, bool divorce)
    {
        Click(s, npc);
        var labels = LastMenu(o, $"{def.Name}: How can I help you today?");
        int pick = labels.IndexOf(divorce ? "Divorce" : "Break Off Engagement") + 1;
        Assert.True(pick > 0, $"no {(divorce ? "Divorce" : "Break Off Engagement")} in [{string.Join(", ", labels)}]");
        s.Receive(MenuFrame((byte)pick));
        s.Receive(NextFrame());                // "How sad this is necessary..." / "Oh no! ..."
        if (divorce) s.Receive(NextFrame());   // "However, I can help you get that divorce you want."
        Assert.True(s.DialogBusy, "the Chapel's last question is not waiting");
        var question = LastMenu(o, divorce ? "Are you sure you want this divorce?" : "Are you sure you want to end the engagement?");
        Assert.Equal(2, question.Count);
    }

    /// <summary>Answer every prompt the dialog sends from here on as a player who says yes to everything: the first
    /// option of a menu (<c>0x30</c> kind <c>02 02</c>), "next" on a text box; until the dialog closes, at most
    /// <paramref name="steps"/> answers. The prompt already waiting is the caller's to answer.</summary>
    private static void AnswerYesToTheEnd(Session s, RecordingOutbound o, int steps = 8)
    {
        for (int i = 0; i < steps && s.DialogBusy; i++)
        {
            var last = o.BodiesOf(ServerOp.NpcDialog).Last();
            s.Receive(last[0] == 0x02 ? MenuFrame(1) : NextFrame());
        }
        Assert.False(s.DialogBusy, "the dialog was still open after answering yes to everything");
    }

    /// <summary>The labels of the last 0x30 menu whose prompt ends with <paramref name="promptTail"/>: after the
    /// prompt come the item count and each item as u8 length + ASCII (<c>Session.SendNpcMenuP</c>).</summary>
    private static List<string> LastMenu(RecordingOutbound o, string promptTail)
    {
        byte[] tail = Encoding.ASCII.GetBytes(promptTail);
        var body = o.BodiesOf(ServerOp.NpcDialog).LastOrDefault(bd =>
            bd.Length > 2 && bd[0] == 0x02 && bd[1] == 0x02 && bd.AsSpan().IndexOf(tail) >= 0);
        Assert.True(body is not null, $"no menu ending \"{promptTail}\" was sent");
        int i = body!.AsSpan().IndexOf(tail) + tail.Length;
        int count = body[i++];
        var labels = new List<string>();
        for (int k = 0; k < count; k++)
        {
            int len = body[i++];
            labels.Add(Encoding.ASCII.GetString(body, i, len));
            i += len;
        }
        return labels;
    }

    /// <summary>How many NPC text boxes said exactly <paramref name="text"/>: a box ends with its message, after the
    /// message's u16BE length, in ASCII.</summary>
    private static int Shown(RecordingOutbound o, string text)
    {
        byte[] want = Encoding.ASCII.GetBytes(text);
        return o.BodiesOf(ServerOp.NpcDialog).Count(body =>
        {
            int at = body.Length - want.Length;
            return at >= 2 && (body[at - 2] << 8 | body[at - 1]) == want.Length && body.AsSpan(at).SequenceEqual(want);
        });
    }

    /// <summary>The client's "next" on a text box (0x3A kind 1).</summary>
    private static byte[] NextFrame()
    {
        var body = new byte[11];
        body[0] = 0x01;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>A menu pick (0x3A kind 2, the 1-based index at [10]; 0 picks nothing).</summary>
    private static byte[] MenuFrame(byte index)
    {
        var body = new byte[11];
        body[0] = 0x02;
        body[10] = index;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>An input box's answer (0x3A kind 4: the length at [10], the text from [11]).</summary>
    private static byte[] DialogInputFrame(string text)
    {
        byte[] t = Encoding.ASCII.GetBytes(text);
        var body = new byte[11 + t.Length];
        body[0] = 0x04;
        body[8] = 0x02;
        body[10] = (byte)t.Length;
        t.CopyTo(body, 11);
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>Run a dialog on a thread of its own, which has no synchronization context: the dialog's awaits then
    /// resume inline on the thread that delivers each reply, as on the read loop.</summary>
    private static void OnOwnThread(Action body)
    {
        Exception? fault = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { fault = e; }
        }) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(60)), "the dialog thread never finished");
        if (fault is not null) throw new Xunit.Sdk.XunitException($"the dialog thread failed: {fault}");
    }

    // ===== the held-monitor run ===============================================================================

    /// <summary>What <see cref="WhileAnotherThreadHolds"/> saw. <see cref="Settled"/> is false while either thread
    /// may still be parked on a lock: a fact must then leave its maps as they are, since cleaning up would wait on
    /// what the stuck thread holds.</summary>
    private sealed class HeldRun
    {
        public bool Settled = true;
        public bool FinishedWhileHeld;
        public bool UntouchedWhileHeld;
        public Exception? HolderFault, ActorFault;
    }

    /// <summary>A third thread takes <paramref name="target"/>'s monitor; then <paramref name="act"/> runs on a thread
    /// of its own until it finishes or sits blocked for 100 ms; then, still inside the third thread's section,
    /// <paramref name="untouched"/> says whether the state it guards is as it was. Then the third thread lets go and
    /// both are joined.</summary>
    private static void WhileAnotherThreadHolds(HeldRun run, Session target, Action act, Func<bool> untouched)
    {
        var held = new ManualResetEventSlim();
        var letGo = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            try
            {
                target.WithState(() =>
                {
                    held.Set();
                    letGo.Wait();
                    run.UntouchedWhileHeld = untouched();
                });
            }
            catch (Exception e) { run.HolderFault = e; }
        }) { IsBackground = true };
        var actor = new Thread(() =>
        {
            try { act(); }
            catch (Exception e) { run.ActorFault = e; }
        }) { IsBackground = true };

        run.Settled = false;
        try
        {
            holder.Start();
            Assert.True(held.Wait(TimeSpan.FromSeconds(30)), "the holder never took the target's monitor");
            actor.Start();
            WaitUntilDoneOrBlocked(actor);
            run.FinishedWhileHeld = !actor.IsAlive;
        }
        finally
        {
            letGo.Set();
        }
        Assert.True(holder.Join(TimeSpan.FromSeconds(60)), "the holder never let go");
        Assert.True(actor.Join(TimeSpan.FromSeconds(60)), "the action never finished after the monitor was free");
        run.Settled = true;
    }

    private static void AssertWaited(HeldRun run, string what, string touched)
    {
        Assert.Null(run.HolderFault);
        Assert.Null(run.ActorFault);
        Assert.False(run.FinishedWhileHeld, $"{what} finished while another thread held the partner's monitor");
        Assert.True(run.UntouchedWhileHeld, touched);
    }

    /// <summary>Wait until <paramref name="t"/> has finished, or has sat blocked for 100 ms straight (an action
    /// waiting for a monitor), or 10 s have passed.</summary>
    private static void WaitUntilDoneOrBlocked(Thread t)
    {
        var clock = Stopwatch.StartNew();
        long blockedSince = -1;
        while (clock.ElapsedMilliseconds < 10_000 && !t.Join(5))
        {
            if ((t.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0) blockedSince = -1;
            else if (blockedSince < 0) blockedSince = clock.ElapsedMilliseconds;
            else if (clock.ElapsedMilliseconds - blockedSince >= 100) return;
        }
    }

    // ===== the race ===========================================================================================

    /// <summary>Round <paramref name="round"/>'s head start for the other side: 0 to 7 steps of
    /// <paramref name="stepMs"/>, each step held for two rounds so both sides (or both <c>StateRank</c> orders) run
    /// it.</summary>
    private static TimeSpan Stagger(int round, double stepMs = 0.4) => TimeSpan.FromMilliseconds(round / 2 % 8 * stepMs);

    /// <summary>Busy-wait <paramref name="span"/> on the calling thread, holding whatever it holds.</summary>
    private static void Spin(TimeSpan span)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < span) Thread.SpinWait(20);
    }

    /// <summary>One race: the workers' round counter, the first fault, a cancellation that releases a worker parked
    /// at a barrier when its partner has failed, and whether every thread has finished.</summary>
    private sealed class Race
    {
        public readonly StallWatch.RoundCounter Progress = new();
        public readonly StallWatch.RoundCounter Ticks = new();
        public readonly ManualResetEventSlim Start = new();
        public readonly CancellationTokenSource Cancel = new();
        public Exception? Fault;
        public volatile bool Done;
        public bool Settled = true;

        public void Record(Exception e)
        {
            Interlocked.CompareExchange(ref Fault, e, null);
            Cancel.Cancel();
        }
    }

    /// <summary>Start every worker on its own thread and the world's tick on another, release them together, and fail
    /// on a stall (<see cref="StallWatch"/>) rather than a stopwatch. A worker that throws records the fault and
    /// cancels the barrier its partner may be parked at, so a fault is reported as a fault, not as a stall.</summary>
    private void RunRace(Race race, string what, params Action[] workers)
    {
        race.Settled = false;
        var threads = workers.Select(w => new Thread(() =>
        {
            try { race.Start.Wait(); w(); }
            catch (OperationCanceledException) when (race.Fault is not null) { }
            catch (Exception e) { race.Record(e); }
        }) { IsBackground = true }).ToArray();
        var ticker = new Thread(() =>
        {
            try
            {
                race.Start.Wait();
                while (!race.Done) { _fx.World.TickOnceForTest(); race.Ticks.Bump(); }
            }
            catch (Exception e) { race.Record(e); }
        }) { IsBackground = true };

        foreach (var t in threads) t.Start();
        ticker.Start();
        race.Start.Set();
        try
        {
            StallWatch.RunUntilDoneOrStalled(threads, () => race.Progress.Rounds,
                StallWatch.StallQuiet, StallWatch.StallCap, what);
        }
        finally
        {
            race.Done = true;
        }
        StallWatch.RunUntilDoneOrStalled(new[] { ticker }, () => race.Ticks.Rounds,
            StallWatch.StallQuiet, StallWatch.StallCap, "the tick thread");
        race.Settled = true;
    }
}
