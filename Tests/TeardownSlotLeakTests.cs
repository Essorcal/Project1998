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
/// The account's online slot on the exception paths of a logout and a login: the two pre-existing defects the
/// PR #298 review listed (<c>reviews/PR298-by-fable.md</c>, "Pre-existing defects seen", items 1 and 2) and the
/// live kick that caused the second.
///
/// <para><b>A teardown step that throws.</b> <c>TearDownWorldState</c> ends the trade and leaves the party before
/// it leaves the map, gives back the slot and saves. Both reach into another session with nothing isolating that
/// session's fault from the leaver, and a throw from either used to leave the teardown there: the session stayed
/// on its map, held the account's slot, and its row was never written, until the next login kicked it as a live
/// duplicate. The review reproduced it (its H5). The facts here make each step throw through the real code — a
/// peer whose send throws, which neither step isolates — and check that the leaver still leaves the map, parks
/// the slot, writes its row, and closes with its CLOSE line.</para>
///
/// <para><b>An arrival that throws after claiming the slot.</b> <c>HandleArrival</c> claims the account's slot
/// (<c>ClaimAccountSlot</c>) before it loads the row, and only sets <c>_enteredWorld</c> once the row is loaded
/// and restored. A throw in between left the session holding the slot, and its teardown gave nothing back
/// because <c>Depart</c> is gated on <c>_enteredWorld</c>; the slot stayed held until the next login kicked the
/// dead session as a live duplicate. The facts drive the real arrival (a 0x10 frame with a minted handoff
/// token) and throw from <c>Session.ArrivalClaimedProbeForTest</c>, which stands for anything in that span.</para>
///
/// <para><b>A live kick whose write throws</b> was the route the review named for that: the old session's capture
/// threw out of <c>ClaimAccountSlot</c>'s live branch and out of the arrival. The kick now finishes around the
/// throw and the arrival carries on with the row as it stands, as the departed branch already did.</para>
///
/// <para><b>A kick whose write fails without throwing</b> (the #303 re-check's pre-existing 1): the database
/// refuses the kick's write, so it takes a sequence number and stamps nothing. It now fences the way a throwing
/// capture does, and the lost write is logged as LOST. The facts refuse the write on a database of their own.</para>
///
/// <para><b>The loading-screen close</b> (the #303 report's follow-up candidate 1). An arrival that throws after
/// claiming the slot and before entering the world now has its connection closed there and then, instead of the
/// handler guard keeping the session while the client sits on the loading screen. The slot is still given back by
/// the teardown that follows. A throw in any other opcode's handler still drops the packet and keeps the
/// session.</para>
///
/// <para>Driven through the read loop's real exit (<c>Session.EndReadLoopAsync</c>) on socket-free sessions.
/// Nothing here waits on a clock.</para>
/// </summary>
[Collection("world")]
public sealed class TeardownSlotLeakTests
{
    /// <summary>Content-free map ids; no other class stands here.</summary>
    private const ushort TradeMap = 61740, PartyMap = 61741, ArrivalMap = 61742, NewerOwnerMap = 61743, KickMap = 61744,
                         SweepDropMap = 61745, DepartedDropMap = 61746, StuckWriteMap = 61747, FailedKickMap = 61748,
                         FailedDepartedKickMap = 61749, CloseMap = 61750, OtherHandlerMap = 61751, LateThrowMap = 61752;

    /// <summary>What the arrival probe throws, so the arrival's log line can be matched to it.</summary>
    private const string ArrivalRefused = "test probe threw after the slot was claimed";

    private const string KickedText = "You have logged in from another location.";

    /// <summary>The arrival's own Error line for a throw between the claim and world entry, after the user name.</summary>
    private const string ArrivalThrewLine =
        "threw after claiming the account's slot, before entering the world — closing the connection";

    /// <summary>The close's line, naming its cause.</summary>
    private const string ArrivalCloseLine = "-> connection teardown (arrival threw before world entry)";

    private const byte TurnIn = 0x11;    // ClientOp.Turn
    private const byte TurnOut = 0x11;   // ServerOp.Turn, the turn handler's own side reply
    private const byte MessageOut = 0x02;   // ServerOp.Message: an arrival's first one is the world trigger

    private const byte ExchangeIn = 0x4a;
    private const byte ExchangeOut = 0x42;
    private const byte MiniTextOut = 0x0A;
    private const byte ExcOpen = 0;

    /// <summary>What a <see cref="FailingOutbound"/> throws, so a log line can be matched to it.</summary>
    private const string SendRefused = "test outbound refused the frame";

    private static readonly FieldInfo TradeField =
        typeof(Session).GetField("_trade", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo PartyField =
        typeof(Session).GetField("_party", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo WriteGateField =
        typeof(Session).GetField("_writeGate", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo ClaimedKeyField =
        typeof(Session).GetField("_claimedKey", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly SessionFixture _fx;
    private readonly ITestOutputHelper _out;

    public TeardownSlotLeakTests(SessionFixture fx, ITestOutputHelper output) { _fx = fx; _out = output; }

    /// <summary>
    /// Item 1, the trade. The leaver has an exchange open with a partner (the real 0x4a open frame), and the
    /// partner's connection throws on the cancel box the teardown sends it. <c>EndTrade</c> throws out of the
    /// teardown.
    ///
    /// <para>After the read loop's exit the leaver is off its map, its slot is parked for the next login's fence
    /// as on any logout, its row is written, its connection is closed and the CLOSE line is logged; the fault is
    /// logged with its stack, and nothing leaves <c>EndReadLoopAsync</c>. On master the throw left the teardown
    /// at the trade: still on the map, still holding the slot, no row.</para>
    /// </summary>
    [Fact]
    public async Task ATeardownWhoseTradeCancelThrowsStillLeavesSavesAndGivesBackTheSlot()
    {
        const string name = "SlotLeakTrader";
        string key = CharacterStore.Key(name);
        var online = _fx.World.Online;
        var (leaver, leaverOut, _) = _fx.PlayerWith(name, c => c.Coins = 321, TradeMap, 5, 5);
        var (partner, partnerOut) = FailingPlayer("SlotLeakTradePartner", _ => { }, TradeMap, 6, 5);

        try
        {
            online.Register(key, leaver, out _);
            leaver.Receive(OpenRequest(partner.PlayerId));
            Assert.NotNull(TradeField.GetValue(leaver));
            Assert.Same(TradeField.GetValue(leaver), TradeField.GetValue(partner));
            Assert.Equal(CharacterLoadStatus.NotFound, _fx.Store.Load(name).Status);   // nothing written yet

            partnerOut.FailOn = ExchangeOut;   // the cancel box to the partner throws, out of EndTrade
            Exception? error;
            using (var sink = LogLineSink.Acquire())
            {
                error = await Record.ExceptionAsync(() => leaver.EndReadLoopAsync(Task.CompletedTask));
                AssertLeftSavedAndClosed(leaver, leaverOut, name, key, coins: 321, sink);
                AssertFaultLogged(sink, $"teardown of '{name}': ending the trade threw");
            }
            Assert.Null(error);
        }
        finally
        {
            partnerOut.FailOn = -1;
            online.Unregister(key, leaver);
            _fx.World.LeaveMap(leaver, TradeMap);
            _fx.World.LeaveMap(partner, TradeMap);
        }
    }

    /// <summary>
    /// Item 1, the party. The leaver is in a party with one member (the real 0x2E invite frame), and the
    /// member's connection throws on the "is leaving the group" line the teardown broadcasts. <c>RemoveFromParty</c>
    /// throws out of the teardown after the leaver has left the roster.
    ///
    /// <para>The same end state as the trade: off the map, the slot parked, the row written, closed, CLOSE logged,
    /// the fault logged with its stack, nothing leaving <c>EndReadLoopAsync</c>. On master the throw left the
    /// teardown at the party.</para>
    /// </summary>
    [Fact]
    public async Task ATeardownWhosePartyLeaveThrowsStillLeavesSavesAndGivesBackTheSlot()
    {
        const string name = "SlotLeakGrouper";
        string key = CharacterStore.Key(name);
        var online = _fx.World.Online;
        var (leaver, leaverOut, _) = _fx.PlayerWith(name, c => c.Coins = 654, PartyMap, 5, 5);
        var (member, memberOut) = FailingPlayer("SlotLeakGroupMember", c => c.Grouped = true, PartyMap, 6, 5);

        try
        {
            online.Register(key, leaver, out _);
            SessionFixture.FormParty(leaver, member);
            Assert.NotNull(PartyField.GetValue(leaver));
            Assert.Same(PartyField.GetValue(leaver), PartyField.GetValue(member));
            Assert.Equal(CharacterLoadStatus.NotFound, _fx.Store.Load(name).Status);   // nothing written yet

            memberOut.FailOn = MiniTextOut;   // the member's "is leaving the group" line throws, out of RemoveFromParty
            Exception? error;
            using (var sink = LogLineSink.Acquire())
            {
                error = await Record.ExceptionAsync(() => leaver.EndReadLoopAsync(Task.CompletedTask));
                AssertLeftSavedAndClosed(leaver, leaverOut, name, key, coins: 654, sink);
                AssertFaultLogged(sink, $"teardown of '{name}': leaving the party threw");
            }
            Assert.Null(error);
            Assert.Null(PartyField.GetValue(leaver));   // the removal itself ran before the broadcast threw
        }
        finally
        {
            memberOut.FailOn = -1;
            online.Unregister(key, leaver);
            _fx.World.LeaveMap(leaver, PartyMap);
            _fx.World.LeaveMap(member, PartyMap);
        }
    }

    /// <summary>
    /// Item 2. An arrival claims the account's slot and then throws before it enters the world. The arrival logs
    /// the throw and closes the connection (the loading-screen close), and the session holds the slot with
    /// <c>_enteredWorld</c> false until its teardown. That gives the slot back (dropped, not parked), and a later login for
    /// the account is handed nobody: it neither kicks nor fences the dead session, and enters with the row as it
    /// stands. The session that never entered the world wrote nothing.
    ///
    /// <para>On master the teardown gave nothing back and the later login was handed the dead session as a live
    /// duplicate.</para>
    /// </summary>
    [Fact]
    public async Task AnArrivalThatThrowsAfterClaimingTheSlotGivesItBackOnTeardown()
    {
        const string name = "SlkArrive";
        string key = CharacterStore.Key(name);
        var online = _fx.World.Online;
        SeedRow(name, ArrivalMap, coins: 100);
        var (stuck, stuckOut) = Arriving(name, "stuck");
        var (later, _) = Arriving(name, "later");

        try
        {
            ArriveAndThrowAfterTheClaim(stuck, name);
            Assert.True(online.HoldsSlotForTest(key, stuck), "the arrival never claimed the slot");
            Assert.DoesNotContain(stuck, _fx.World.Online.All());   // and never entered the world

            Exception? error;
            using (var sink = LogLineSink.Acquire())
            {
                error = await Record.ExceptionAsync(() => stuck.EndReadLoopAsync(Task.CompletedTask));
                _out.WriteLine($"[state] {name}: after the stuck session's exit, holds slot={online.HoldsSlotForTest(key, stuck)}, " +
                               $"parked={online.HoldsDepartedForTest(key, stuck)}, closed={stuckOut.Closed}");
                Assert.False(online.HoldsSlotForTest(key, stuck),
                             "the teardown left the slot held by a session that never entered the world");
                Assert.False(online.HoldsDepartedForTest(key, stuck), "the teardown parked a session that never entered the world");
                Assert.True(stuckOut.Closed);
                sink.LineContaining($"-- CLOSE recorder:{name}:stuck");
            }
            Assert.Null(error);

            using (var sink = LogLineSink.Acquire())
            {
                later.Receive(ArrivalFrame(name));
                Assert.False(sink.Has($"ARRIVAL: '{name}' already online"), "the later login was handed the dead session as a live duplicate");
                Assert.False(sink.Has($"ARRIVAL: '{name}' left moments ago"), "the later login was handed the dead session to fence");
                sink.LineContaining($"ARRIVAL user='{name}' — loaded character");
            }
            Assert.False(stuck.IsReplaced, "the later login kicked the dead session");
            Assert.True(online.HoldsSlotForTest(key, later));
            Assert.Contains(later, _fx.World.Online.All());
            Assert.Equal(100u, later.CharCoins);                // the row as it stands
            Assert.Equal(0L, stuck.LastSaveAtMsForTest);        // the session that never entered wrote nothing
        }
        finally
        {
            Session.ArrivalClaimedProbeForTest = null;
            online.Unregister(key, stuck);
            online.Unregister(key, later);
            _fx.World.LeaveMap(later, ArrivalMap);
        }
    }

    /// <summary>
    /// The newer owner. The same stuck arrival, but a later login arrives BEFORE the stuck session's teardown:
    /// it takes the slot and kicks the stuck session as a live duplicate. The stuck session never entered the
    /// world, so the kick writes nothing (its <c>_enteredWorld</c> gate) and latches it; its notice and its close
    /// find the connection already closed by the arrival's own throw (the loading-screen close). Before that close
    /// the kick also told and closed it, which is all the next login's kick ever did to such a session. The stuck
    /// session's teardown then runs and must leave the newer login's slot alone.
    ///
    /// <para>This is the case <c>Unregister</c>'s compare-and-remove exists for: a teardown that gave the slot
    /// back without asking who owns it would take it from the login that is now in the world.</para>
    /// </summary>
    [Fact]
    public async Task AStuckArrivalsTeardownLeavesANewerLoginsSlotAlone()
    {
        const string name = "SlkNewer";
        string key = CharacterStore.Key(name);
        var online = _fx.World.Online;
        SeedRow(name, NewerOwnerMap, coins: 200);
        var (stuck, stuckOut) = Arriving(name, "stuck");
        var (later, _) = Arriving(name, "later");

        try
        {
            ArriveAndThrowAfterTheClaim(stuck, name);
            Assert.True(online.HoldsSlotForTest(key, stuck));
            Assert.True(stuckOut.Closed);                       // by its own arrival's throw, before any teardown

            using (var sink = LogLineSink.Acquire())
            {
                later.Receive(ArrivalFrame(name));
                sink.LineContaining($"ARRIVAL: '{name}' already online — kicking previous session");
            }
            Assert.True(stuck.IsReplaced);
            Assert.Equal(0L, stuck.LastSaveAtMsForTest);        // the kick wrote nothing for a session that never entered
            Assert.True(online.HoldsSlotForTest(key, later));
            Assert.Equal(200u, later.CharCoins);

            var error = await Record.ExceptionAsync(() => stuck.EndReadLoopAsync(Task.CompletedTask));
            Assert.Null(error);
            Assert.True(online.HoldsSlotForTest(key, later), "the stuck session's teardown took the newer login's slot");
            Assert.False(online.HoldsDepartedForTest(key, stuck));
            Assert.Equal(0L, stuck.LastSaveAtMsForTest);
        }
        finally
        {
            Session.ArrivalClaimedProbeForTest = null;
            online.Unregister(key, stuck);
            online.Unregister(key, later);
            _fx.World.LeaveMap(later, NewerOwnerMap);
        }
    }

    /// <summary>
    /// Item 3. A second login for an account whose session is live, and whose state cannot be captured (a value
    /// the serializer rejects, the PR #282 route: NaN karma). The kick's write throws. The kick still finishes:
    /// the old session is latched, told "You have logged in from another location." and closed. The lost save is
    /// logged as LOST, as the departed branch logs its own, and the new login loads the row as it stands — the
    /// old session's last good save — and enters the world.
    ///
    /// <para>On master the throw left <c>HandleArrival</c> through the handler guard: the old session was
    /// latched but neither told nor closed, and the new login never entered, holding the slot with
    /// <c>_enteredWorld</c> false (the review's pre-existing 2).</para>
    /// </summary>
    [Fact]
    public void ALiveKickWhoseWriteThrowsStillKicksAndTheNewLoginEnters()
    {
        const string name = "SlkKicked";
        string key = CharacterStore.Key(name);
        var online = _fx.World.Online;
        var (old, oldOut, oldChar) = _fx.PlayerWith(name, c => c.Coins = 300, KickMap, 5, 5);
        var (fresh, _) = Arriving(name, "new");

        try
        {
            online.Register(key, old, out _);
            old.WithState(old.MarkDirty);
            Assert.True(World.AutoSaveLoop.FlushIsolated(old, "autosave"));   // the last good save: 300 coins
            Assert.Equal(300u, LoadOk(name).Coins);
            old.WithState(() =>
            {
                oldChar.Coins = 999;              // progress since that save ...
                oldChar.Karma = double.NaN;       // ... in a state that cannot be serialized: the capture throws
                old.MarkDirty();
            });

            using (var sink = LogLineSink.Acquire())
            {
                fresh.Receive(ArrivalFrame(name));
                _out.WriteLine($"[state] {name}: old replaced={old.IsReplaced}, told={MiniTexts(oldOut).Contains(KickedText)}, " +
                               $"closed={oldOut.Closed}; new holds slot={online.HoldsSlotForTest(key, fresh)}, " +
                               $"on map={_fx.World.Online.All().Contains(fresh)}");

                Assert.True(old.IsReplaced);
                Assert.Contains(KickedText, MiniTexts(oldOut));
                Assert.True(oldOut.Closed, "the old connection was left open");
                Assert.True(online.HoldsSlotForTest(key, fresh));
                Assert.Contains(fresh, _fx.World.Online.All());
                Assert.Equal(300u, fresh.CharCoins);             // the row as it stands: the last good save

                sink.LineContaining($"ARRIVAL: '{name}' already online — kicking previous session");
                var lost = sink.EntryContaining($"the previous session's final write for '{name}' threw — its save is LOST");
                Assert.Equal(LogLevel.Error, lost.Level);
                Assert.False(ArrivalThrew(sink, $"recorder:{name}:new"), "the kick's throw escaped the arrival");
            }
            Assert.Equal(300u, LoadOk(name).Coins);                // and nothing wrote over it
        }
        finally
        {
            oldChar.Karma = 0;
            online.Unregister(key, fresh);
            online.Unregister(key, old);
            _fx.World.LeaveMap(old, KickMap);
            _fx.World.LeaveMap(fresh, KickMap);
        }
    }

    /// <summary>
    /// Fix round 1, the #303 review's F1, live branch. An autosave sweep has captured the old session (350 coins
    /// over a landed 300) and its write is on the way to the write gate when the account logs in again, and the
    /// old session's state can no longer be captured (NaN karma). The kick's capture throws before it takes a
    /// sequence number. The kick must still fence: the sweep's older capture is dropped at the gate, so the row
    /// the new login loaded (300) is the row that stays, and the new session's next write erases nothing.
    ///
    /// <para>The order capture, kick, gate check is forced without a clock. The test thread holds the old
    /// session's write gate, so the sweep's write waits there, and runs the arrival itself. The gate is
    /// re-entrant, so the kick's fence enters it at once. That holds row 4 across row 2 entries on the test
    /// thread, which production never does; the sweep thread holds no monitor while it waits at the gate, so no
    /// cycle can form. A write already past the gate is <see cref="AThrowingKickWaitsOutAWriteAlreadyAtTheDatabase"/>.</para>
    ///
    /// <para>From the review's probe Hunt4, which read "row at load=300; row after the in-flight write
    /// landed=350; row after the new session's next write=300" without the fence.</para>
    /// </summary>
    [Fact]
    public void ALiveKickWhoseCaptureThrowsDropsASweepWriteCapturedBeforeIt()
    {
        const string name = "SlkSweep";
        string key = CharacterStore.Key(name);
        var online = _fx.World.Online;
        var (old, oldOut, oldChar) = _fx.PlayerWith(name, c => c.Coins = 300, SweepDropMap, 5, 5);
        var (fresh, _) = Arriving(name, "new");
        object gate = WriteGateOf(old);
        Thread? sweep = null;

        try
        {
            online.Register(key, old, out _);
            old.WithState(old.MarkDirty);
            Assert.True(World.AutoSaveLoop.FlushIsolated(old, "autosave"));   // the last landed row: 300
            old.WithState(() => { oldChar.Coins = 350; old.MarkDirty(); });

            uint loaded, rowAtLoad;
            lock (gate)
            {
                sweep = new Thread(() => World.AutoSaveLoop.FlushIsolated(old, "autosave"))
                    { IsBackground = true, Name = "sweep-write" };
                sweep.Start();
                WaitBlocked(sweep, "the sweep's write, on the old session's write gate");
                Assert.Contains("dirty False", old.DiagState());               // it has captured 350
                old.WithState(() => { oldChar.Coins = 999; oldChar.Karma = double.NaN; old.MarkDirty(); });

                using (var sink = LogLineSink.Acquire())
                {
                    fresh.Receive(ArrivalFrame(name));
                    sink.EntryContaining($"the previous session's final write for '{name}' threw — its save is LOST");
                }
                Assert.True(old.IsReplaced);
                Assert.True(oldOut.Closed);
                Assert.Contains(fresh, _fx.World.Online.All());
                loaded = fresh.CharCoins;
                rowAtLoad = LoadOk(name).Coins;
            }

            Assert.True(sweep.Join(Bound), "the sweep's write never finished");
            uint rowAfterSweep = LoadOk(name).Coins;
            fresh.WithState(fresh.MarkDirty);
            Assert.True(World.AutoSaveLoop.FlushIsolated(fresh, "autosave"));   // the new session's next write
            uint rowAfterNext = LoadOk(name).Coins;
            _out.WriteLine($"[state] {name}: loaded={loaded}, row at load={rowAtLoad}, row after the sweep's write=" +
                           $"{rowAfterSweep}, row after the new session's next write={rowAfterNext}");

            Assert.Equal(300u, loaded);
            Assert.Equal(300u, rowAtLoad);
            Assert.Equal(loaded, rowAfterSweep);            // the older capture was dropped, not landed after the load
            Assert.Equal(loaded, rowAfterNext);
        }
        finally
        {
            oldChar.Karma = 0;
            sweep?.Join(Bound);
            online.Unregister(key, fresh);
            online.Unregister(key, old);
            _fx.World.LeaveMap(old, SweepDropMap);
            _fx.World.LeaveMap(fresh, SweepDropMap);
        }
    }

    /// <summary>
    /// Fix round 1, the departed branch (the #303 review's pre-existing 1). A sweep captured the session (350
    /// over a landed 100) and its write is on the way to the gate; the session's state then stops serializing
    /// and it logs out, so its teardown save throws ("save LOST") and it is parked for the next login's fence. A
    /// fast re-login fences it, and that kick's capture throws too. The fence must still drop the sweep's older
    /// capture: the row the new login loaded (100) stays. Forced the same way as the live fact: the test thread
    /// holds the departed session's write gate across the logout and the arrival.
    /// </summary>
    [Fact]
    public void ADepartedKickWhoseCaptureThrowsDropsASweepWriteCapturedBeforeIt()
    {
        const string name = "SlkParked";
        string key = CharacterStore.Key(name);
        var online = _fx.World.Online;
        var (departed, _, ch) = _fx.PlayerWith(name, c => c.Coins = 100, DepartedDropMap, 5, 5);
        var (fresh, _) = Arriving(name, "new");
        object gate = WriteGateOf(departed);
        Thread? sweep = null;

        try
        {
            online.Register(key, departed, out _);
            departed.WithState(departed.MarkDirty);
            Assert.True(World.AutoSaveLoop.FlushIsolated(departed, "autosave"));   // the last landed row: 100
            departed.WithState(() => { ch.Coins = 350; departed.MarkDirty(); });

            uint loaded, rowAtLoad;
            lock (gate)
            {
                sweep = new Thread(() => World.AutoSaveLoop.FlushIsolated(departed, "autosave"))
                    { IsBackground = true, Name = "sweep-write" };
                sweep.Start();
                WaitBlocked(sweep, "the sweep's write, on the session's write gate");
                Assert.Contains("dirty False", departed.DiagState());          // it has captured 350
                departed.WithState(() => { ch.Coins = 999; ch.Karma = double.NaN; departed.MarkDirty(); });

                using (var sink = LogLineSink.Acquire())
                {
                    // The logout: the teardown's save throws and the slot is parked (#168). EndReadLoopAsync runs
                    // synchronously to the end here, still on this thread and inside the gate: its only await is on
                    // an already-completed writer.
                    Assert.True(departed.EndReadLoopAsync(Task.CompletedTask).IsCompletedSuccessfully,
                                "the logout did not run to its end on this thread");
                    sink.LineContaining($"disconnect save of '{name}' threw — save LOST");
                    Assert.True(online.HoldsDepartedForTest(key, departed));

                    fresh.Receive(ArrivalFrame(name));
                    sink.LineContaining($"ARRIVAL: '{name}' left moments ago — fencing");
                    sink.EntryContaining($"the departed session's final write for '{name}' threw — its save is LOST");
                }
                Assert.True(departed.IsReplaced);
                Assert.Contains(fresh, _fx.World.Online.All());
                loaded = fresh.CharCoins;
                rowAtLoad = LoadOk(name).Coins;
            }

            Assert.True(sweep.Join(Bound), "the sweep's write never finished");
            uint rowAfterSweep = LoadOk(name).Coins;
            _out.WriteLine($"[state] {name}: loaded={loaded}, row at load={rowAtLoad}, row after the sweep's write={rowAfterSweep}");

            Assert.Equal(100u, loaded);
            Assert.Equal(100u, rowAtLoad);
            Assert.Equal(loaded, rowAfterSweep);            // the older capture was dropped, not landed after the load
        }
        finally
        {
            ch.Karma = 0;
            sweep?.Join(Bound);
            online.Unregister(key, fresh);
            online.Unregister(key, departed);
            _fx.World.LeaveMap(departed, DepartedDropMap);
            _fx.World.LeaveMap(fresh, DepartedDropMap);
        }
    }

    /// <summary>
    /// Fix round 1, the other half of the fence: a write already PAST the gate. The sweep's write of 350 has
    /// passed its sequence check and is stuck in SQLite (this fact's own database file, its write lock held by
    /// hand), holding the old session's write gate. A re-login's kick throws on the old session's NaN karma. The
    /// fence takes the gate, so the arrival cannot load until that write has landed; it then loads 350, and the
    /// row stays 350. With a stamp that skipped the gate, the arrival would load 300 underneath the stuck write,
    /// and the write would land after it.
    ///
    /// <para>The database lock is let go once the arrival is seen blocked, milliseconds later, far inside the
    /// 5 s busy timeout the stuck write waits under.</para>
    /// </summary>
    [Fact]
    public void AThrowingKickWaitsOutAWriteAlreadyAtTheDatabase()
    {
        const string name = "SlkStuck";
        string key = CharacterStore.Key(name);
        var online = _fx.World.Online;
        using var db = new IsolatedDatabase();
        var oldChar = new Character
        {
            SchemaVersion = Character.CurrentSchemaVersion, Id = _fx.World.AllocatePlayerId(), Name = name,
            Map = StuckWriteMap, X = 5, Y = 5, Coins = 300,
        };
        var old = new Session(new RecordingOutbound($"recorder:{name}"), 2005, db.Store, _fx.World, oldChar);
        var fresh = new Session(new RecordingOutbound($"recorder:{name}:new"), 2005, db.Store, _fx.World);
        _fx.World.EnterMap(old, StuckWriteMap);
        object gate = WriteGateOf(old);

        using var locked = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var fenceReached = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            using var blocker = db.Open();
            using var begin = blocker.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();
            locked.Set();
            release.Wait(Bound);
            using var rollback = blocker.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }) { IsBackground = true, Name = "db-write-lock" };
        Thread? sweep = null, arrival = null;

        try
        {
            online.Register(key, old, out _);
            old.WithState(old.MarkDirty);
            Assert.True(World.AutoSaveLoop.FlushIsolated(old, "autosave"));   // the last landed row: 300
            Assert.Equal(300u, LoadOk(db.Store, name).Coins);
            old.WithState(() => { oldChar.Coins = 350; old.MarkDirty(); });

            holder.Start();
            Assert.True(locked.Wait(Bound), "the blocking thread never took the database's write lock");
            sweep = new Thread(() => World.AutoSaveLoop.FlushIsolated(old, "autosave")) { IsBackground = true, Name = "sweep-write" };
            sweep.Start();
            // Captured (dirty down) and inside the gate (a try-enter from here fails): the write is in SQLite.
            Assert.True(SpinWait.SpinUntil(() => old.DiagState().Contains("dirty False") && GateHeldElsewhere(gate), Bound),
                        "the sweep's write never reached the database");
            old.WithState(() => { oldChar.Coins = 999; oldChar.Karma = double.NaN; old.MarkDirty(); });

            Session.ArrivalFenceProbeForTest = s => { if (ReferenceEquals(s, old)) fenceReached.Set(); };
            arrival = new Thread(() => fresh.Receive(ArrivalFrame(name))) { IsBackground = true, Name = "arrival" };
            arrival.Start();
            Assert.True(fenceReached.Wait(Bound), "the arrival never reached the kick");
            Assert.True(SpinWait.SpinUntil(() => !arrival.IsAlive || Blocked(arrival), Bound));
            bool pendingWhileStuck = arrival.IsAlive;
            uint loadedEarly = pendingWhileStuck ? 0 : fresh.CharCoins;

            release.Set();
            Assert.True(arrival.Join(Bound), "the arrival never finished");
            Assert.True(sweep.Join(Bound), "the sweep's write never finished");
            uint rowAfter = LoadOk(db.Store, name).Coins;
            _out.WriteLine($"[state] {name}: arrival pending while the write was stuck={pendingWhileStuck}" +
                           (pendingWhileStuck ? "" : $" (loaded {loadedEarly} before it landed)") +
                           $", loaded={fresh.CharCoins}, row after={rowAfter}");

            Assert.True(pendingWhileStuck, "the arrival loaded while a write already past the gate was still pending");
            Assert.Contains(fresh, _fx.World.Online.All());
            Assert.Equal(350u, rowAfter);                    // the stuck write landed ...
            Assert.Equal(rowAfter, fresh.CharCoins);         // ... before the load
        }
        finally
        {
            Session.ArrivalFenceProbeForTest = null;
            release.Set();
            oldChar.Karma = 0;
            holder.Join(Bound);
            sweep?.Join(Bound);
            arrival?.Join(Bound);
            online.Unregister(key, fresh);
            online.Unregister(key, old);
            _fx.World.LeaveMap(old, StuckWriteMap);
            _fx.World.LeaveMap(fresh, StuckWriteMap);
        }
    }

    /// <summary>
    /// The #303 re-check's pre-existing 1, live branch: a kick whose write FAILS without throwing. An autosave
    /// sweep has captured the old session (350 coins over a landed 300) and its write is on the way to the write
    /// gate when the account logs in again. The kick captures (999, a state that serializes fine) and takes a
    /// newer sequence number, but the database refuses the write, so <c>SaveJson</c> returns false and nothing
    /// is stamped. The kick must still fence: the sweep's older capture is dropped at the gate, so the row the
    /// new login loaded (300) is the row that stays, and the new session's next write erases nothing. The lost
    /// write is logged as LOST, as a throwing kick's is.
    ///
    /// <para>The refusal is this fact's own database (<see cref="RefuseWrites"/>): <c>SaveJson</c> catches the
    /// abort the way it catches a busy database past its timeout, and returns false. The order capture, kick,
    /// gate check is forced the way <see cref="ALiveKickWhoseCaptureThrowsDropsASweepWriteCapturedBeforeIt"/>
    /// forces it: the test thread holds the old session's write gate and runs the arrival itself.</para>
    /// </summary>
    [Fact]
    public void ALiveKickWhoseWriteFailsDropsASweepWriteCapturedBeforeIt()
    {
        const string name = "SlkFail";
        string key = CharacterStore.Key(name);
        var online = _fx.World.Online;
        using var db = new IsolatedDatabase();
        InstallWriteRefusal(db);
        var oldChar = new Character
        {
            SchemaVersion = Character.CurrentSchemaVersion, Id = _fx.World.AllocatePlayerId(), Name = name,
            Map = FailedKickMap, X = 5, Y = 5, Coins = 300,
        };
        var oldOut = new RecordingOutbound($"recorder:{name}");
        var old = new Session(oldOut, 2005, db.Store, _fx.World, oldChar);
        var fresh = new Session(new RecordingOutbound($"recorder:{name}:new"), 2005, db.Store, _fx.World);
        _fx.World.EnterMap(old, FailedKickMap);
        object gate = WriteGateOf(old);
        Thread? sweep = null;

        try
        {
            online.Register(key, old, out _);
            old.WithState(old.MarkDirty);
            Assert.True(World.AutoSaveLoop.FlushIsolated(old, "autosave"));   // the last landed row: 300
            Assert.Equal(300u, LoadOk(db.Store, name).Coins);
            old.WithState(() => { oldChar.Coins = 350; old.MarkDirty(); });

            uint loaded, rowAtLoad;
            bool lostLogged, lostAtError, guardLogged;
            lock (gate)
            {
                sweep = new Thread(() => World.AutoSaveLoop.FlushIsolated(old, "autosave"))
                    { IsBackground = true, Name = "sweep-write" };
                sweep.Start();
                WaitBlocked(sweep, "the sweep's write, on the old session's write gate");
                Assert.Contains("dirty False", old.DiagState());               // it has captured 350
                old.WithState(() => { oldChar.Coins = 999; old.MarkDirty(); });   // serializable: the kick's capture succeeds

                RefuseWrites(db, true);
                using (var sink = LogLineSink.Acquire())
                {
                    fresh.Receive(ArrivalFrame(name));
                    sink.LineContaining($"ARRIVAL: '{name}' already online — kicking previous session");
                    lostLogged = sink.Has($"the previous session's final write for '{name}' failed — its save is LOST");
                    lostAtError = lostLogged &&
                        sink.EntryContaining($"the previous session's final write for '{name}' failed").Level == LogLevel.Error;
                    guardLogged = ArrivalThrew(sink, $"recorder:{name}:new");
                }
                RefuseWrites(db, false);
                Assert.True(old.IsReplaced);
                Assert.True(oldOut.Closed);
                Assert.Contains(fresh, _fx.World.Online.All());
                loaded = fresh.CharCoins;
                rowAtLoad = LoadOk(db.Store, name).Coins;
            }

            Assert.True(sweep.Join(Bound), "the sweep's write never finished");
            uint rowAfterSweep = LoadOk(db.Store, name).Coins;
            fresh.WithState(fresh.MarkDirty);
            Assert.True(World.AutoSaveLoop.FlushIsolated(fresh, "autosave"));   // the new session's next write
            uint rowAfterNext = LoadOk(db.Store, name).Coins;
            _out.WriteLine($"[state] {name}: loaded={loaded}, row at load={rowAtLoad}, row after the sweep's write=" +
                           $"{rowAfterSweep}, row after the new session's next write={rowAfterNext}, LOST logged={lostLogged}");

            Assert.Equal(300u, loaded);
            Assert.Equal(300u, rowAtLoad);
            Assert.Equal(loaded, rowAfterSweep);            // the older capture was dropped, not landed after the load
            Assert.Equal(loaded, rowAfterNext);
            Assert.True(lostLogged, "the kick's failed write was not logged as LOST");
            Assert.True(lostAtError, "the LOST line was not at Error");
            Assert.False(guardLogged, "the arrival threw");
        }
        finally
        {
            RefuseWrites(db, false);
            sweep?.Join(Bound);
            online.Unregister(key, fresh);
            online.Unregister(key, old);
            _fx.World.LeaveMap(old, FailedKickMap);
            _fx.World.LeaveMap(fresh, FailedKickMap);
        }
    }

    /// <summary>
    /// The departed branch of the same. A sweep captured the session (350 over a landed 100) and its write is on
    /// the way to the gate; the database then refuses writes and the session logs out, so its teardown save fails
    /// and it is parked for the next login's fence. A fast re-login fences it, and that kick's write fails too.
    /// The fence must still drop the sweep's older capture: the row the new login loaded (100) stays. Forced the
    /// same way: the test thread holds the departed session's write gate across the logout and the arrival.
    /// </summary>
    [Fact]
    public void ADepartedKickWhoseWriteFailsDropsASweepWriteCapturedBeforeIt()
    {
        const string name = "SlkFGone";
        string key = CharacterStore.Key(name);
        var online = _fx.World.Online;
        using var db = new IsolatedDatabase();
        InstallWriteRefusal(db);
        var ch = new Character
        {
            SchemaVersion = Character.CurrentSchemaVersion, Id = _fx.World.AllocatePlayerId(), Name = name,
            Map = FailedDepartedKickMap, X = 5, Y = 5, Coins = 100,
        };
        var departed = new Session(new RecordingOutbound($"recorder:{name}"), 2005, db.Store, _fx.World, ch);
        var fresh = new Session(new RecordingOutbound($"recorder:{name}:new"), 2005, db.Store, _fx.World);
        _fx.World.EnterMap(departed, FailedDepartedKickMap);
        object gate = WriteGateOf(departed);
        Thread? sweep = null;

        try
        {
            online.Register(key, departed, out _);
            departed.WithState(departed.MarkDirty);
            Assert.True(World.AutoSaveLoop.FlushIsolated(departed, "autosave"));   // the last landed row: 100
            Assert.Equal(100u, LoadOk(db.Store, name).Coins);
            departed.WithState(() => { ch.Coins = 350; departed.MarkDirty(); });

            uint loaded, rowAtLoad;
            bool lostLogged;
            lock (gate)
            {
                sweep = new Thread(() => World.AutoSaveLoop.FlushIsolated(departed, "autosave"))
                    { IsBackground = true, Name = "sweep-write" };
                sweep.Start();
                WaitBlocked(sweep, "the sweep's write, on the session's write gate");
                Assert.Contains("dirty False", departed.DiagState());          // it has captured 350
                departed.WithState(() => { ch.Coins = 999; departed.MarkDirty(); });

                RefuseWrites(db, true);
                using (var sink = LogLineSink.Acquire())
                {
                    // The logout: the teardown's save fails (returns false) and the slot is parked (#168).
                    // EndReadLoopAsync runs synchronously to the end here, still on this thread and inside the
                    // gate: its only await is on an already-completed writer.
                    Assert.True(departed.EndReadLoopAsync(Task.CompletedTask).IsCompletedSuccessfully,
                                "the logout did not run to its end on this thread");
                    Assert.True(online.HoldsDepartedForTest(key, departed));

                    fresh.Receive(ArrivalFrame(name));
                    sink.LineContaining($"ARRIVAL: '{name}' left moments ago — fencing");
                    lostLogged = sink.Has($"the departed session's final write for '{name}' failed — its save is LOST");
                }
                RefuseWrites(db, false);
                Assert.True(departed.IsReplaced);
                Assert.Contains(fresh, _fx.World.Online.All());
                loaded = fresh.CharCoins;
                rowAtLoad = LoadOk(db.Store, name).Coins;
            }

            Assert.True(sweep.Join(Bound), "the sweep's write never finished");
            uint rowAfterSweep = LoadOk(db.Store, name).Coins;
            _out.WriteLine($"[state] {name}: loaded={loaded}, row at load={rowAtLoad}, row after the sweep's write={rowAfterSweep}, " +
                           $"LOST logged={lostLogged}");

            Assert.Equal(100u, loaded);
            Assert.Equal(100u, rowAtLoad);
            Assert.Equal(loaded, rowAfterSweep);            // the older capture was dropped, not landed after the load
            Assert.True(lostLogged, "the departed kick's failed write was not logged as LOST");
        }
        finally
        {
            RefuseWrites(db, false);
            sweep?.Join(Bound);
            online.Unregister(key, fresh);
            online.Unregister(key, departed);
            _fx.World.LeaveMap(departed, FailedDepartedKickMap);
            _fx.World.LeaveMap(fresh, FailedDepartedKickMap);
        }
    }

    /// <summary>
    /// The loading-screen close (the #303 report's follow-up candidate 1). An arrival claims the account's slot
    /// and then throws before it enters the world, so the client is left on the loading screen with nothing more
    /// coming. The connection is closed there and then, with the cause in the log: the arrival's own Error line,
    /// carrying the exception, and the close's "connection teardown" line naming it. The close does not give the
    /// slot back. The teardown it brings about (the read loop's exit) does, through the claimed key as #303 made
    /// it, and logs the CLOSE line.
    ///
    /// <para>Before, the handler guard caught the throw, logged "the packet is dropped, the session continues",
    /// and the client sat on the loading screen until it disconnected.</para>
    /// </summary>
    [Fact]
    public async Task AnArrivalThatThrowsAfterClaimingTheSlotClosesTheConnection()
    {
        const string name = "SlkClose";
        string key = CharacterStore.Key(name);
        var online = _fx.World.Online;
        SeedRow(name, CloseMap, coins: 100);
        var (stuck, stuckOut) = Arriving(name, "stuck");

        try
        {
            Session.ArrivalClaimedProbeForTest = arriving =>
            {
                if (ReferenceEquals(arriving, stuck)) throw new InvalidOperationException(ArrivalRefused);
            };
            bool holdsSlot;
            using (var sink = LogLineSink.Acquire())
            {
                stuck.Receive(ArrivalFrame(name));
                bool closed = stuckOut.Closed;
                holdsSlot = online.HoldsSlotForTest(key, stuck);
                bool guardLogged = sink.Has($"recorder:{name}:stuck handler for opcode 0x10 threw");
                _out.WriteLine($"[state] {name}: after the arrival threw, closed={closed}, holds slot={holdsSlot}, " +
                               $"on map={online.All().Contains(stuck)}, cause logged={sink.Has(ArrivalCloseLine)}, " +
                               $"guard line (session kept)={guardLogged}");

                Assert.True(closed, "the connection was left open on the loading screen");
                sink.LineContaining(ArrivalCloseLine);
                var thrown = sink.EntryContaining($"recorder:{name}:stuck arrival for '{name}' {ArrivalThrewLine}");
                Assert.Equal(LogLevel.Error, thrown.Level);
                Assert.Contains(ArrivalRefused, thrown.Line);                  // the exception, with its stack
                Assert.False(guardLogged, "the handler guard logged the throw as one it keeps the session for");
            }
            Session.ArrivalClaimedProbeForTest = null;
            Assert.True(holdsSlot, "the close gave the slot back itself; the teardown is what does that");
            Assert.DoesNotContain(stuck, online.All());

            Exception? error;
            using (var sink = LogLineSink.Acquire())
            {
                error = await Record.ExceptionAsync(() => stuck.EndReadLoopAsync(Task.CompletedTask));
                sink.LineContaining($"-- CLOSE recorder:{name}:stuck");
            }
            Assert.Null(error);
            Assert.False(online.HoldsSlotForTest(key, stuck), "the teardown left the slot held");
            Assert.False(online.HoldsDepartedForTest(key, stuck));
            Assert.Equal(0L, stuck.LastSaveAtMsForTest);                        // it never entered, so it never wrote
        }
        finally
        {
            Session.ArrivalClaimedProbeForTest = null;
            online.Unregister(key, stuck);
        }
    }

    /// <summary>
    /// The other side of the loading-screen close: it is the arrival's alone. A session that has entered the
    /// world through a real arrival, and so has claimed its slot, gets a packet whose handler throws: the turn
    /// handler, whose own 0x11 reply throws on a failing connection. The handler guard in <c>Session.Handle</c>
    /// logs it and drops the packet, and the session carries on: the connection stays open, the slot stays held,
    /// and the next turn is answered, exactly as before the close was added.
    /// </summary>
    [Fact]
    public void AThrowInAnyOtherHandlerStillDropsThePacketAndKeepsTheSession()
    {
        const string name = "SlkOther";
        string key = CharacterStore.Key(name);
        var online = _fx.World.Online;
        SeedRow(name, OtherHandlerMap, coins: 100);
        var outbound = new FailingOutbound($"recorder:{name}");
        var s = new Session(outbound, 2005, _fx.Store, _fx.World);

        try
        {
            s.Receive(ArrivalFrame(name));
            Assert.Contains(s, online.All());                                   // in the world, through a real claim
            Assert.True(online.HoldsSlotForTest(key, s));

            outbound.FailOn = TurnOut;                                          // the turn handler's own reply throws
            using (var sink = LogLineSink.Acquire())
            {
                s.Receive(SessionFixture.Frame(TurnIn, new byte[] { 2 }));
                _out.WriteLine($"[state] {name}: after the turn handler threw, closed={outbound.Recorded.Closed}, " +
                               $"holds slot={online.HoldsSlotForTest(key, s)}, on map={online.All().Contains(s)}");
                var guard = sink.EntryContaining(
                    $"recorder:{name} handler for opcode 0x{TurnIn:x2} threw — the packet is dropped, the session continues");
                Assert.Equal(LogLevel.Error, guard.Level);
                Assert.Contains(SendRefused, guard.Line);
            }
            Assert.False(outbound.Recorded.Closed, "a throw in the turn handler closed the connection");
            Assert.True(online.HoldsSlotForTest(key, s));
            Assert.Contains(s, online.All());

            outbound.FailOn = -1;
            int answered = outbound.Recorded.BodiesOf(TurnOut).Count;
            s.Receive(SessionFixture.Frame(TurnIn, new byte[] { 1 }));
            Assert.Equal(answered + 1, outbound.Recorded.BodiesOf(TurnOut).Count);   // the next packet is handled
        }
        finally
        {
            outbound.FailOn = -1;
            online.Unregister(key, s);
            _fx.World.LeaveMap(s, OtherHandlerMap);
        }
    }

    /// <summary>
    /// The loading-screen close's other boundary: world entry. An arrival that throws after <c>_enteredWorld</c>
    /// is set (here its first 0x02, the world trigger, on a failing connection) is not in the close's span. Such a
    /// session gives its slot back the way an entered one does, parked and saved by its teardown, not through the
    /// claimed key. Its throw goes on to the handler guard as before, which logs it and keeps the session. This
    /// pins the close's scope, the span #303's give-back covers; it is not a judgement on this path.
    /// </summary>
    [Fact]
    public void AnArrivalThatThrowsAfterWorldEntryStillGoesToTheHandlerGuard()
    {
        const string name = "SlkLate";
        string key = CharacterStore.Key(name);
        var online = _fx.World.Online;
        SeedRow(name, LateThrowMap, coins: 100);
        var outbound = new FailingOutbound($"recorder:{name}");
        var s = new Session(outbound, 2005, _fx.Store, _fx.World);

        try
        {
            outbound.FailOn = MessageOut;
            using (var sink = LogLineSink.Acquire())
            {
                s.Receive(ArrivalFrame(name));
                _out.WriteLine($"[state] {name}: after the arrival threw past world entry, closed={outbound.Recorded.Closed}, " +
                               $"holds slot={online.HoldsSlotForTest(key, s)}, arrival line={sink.Has($"recorder:{name} arrival for '")}");
                sink.LineContaining($"ARRIVAL user='{name}' — loaded character");   // past the load, so past _enteredWorld
                var guard = sink.EntryContaining(
                    $"recorder:{name} handler for opcode 0x10 threw — the packet is dropped, the session continues");
                Assert.Contains(SendRefused, guard.Line);
                Assert.False(sink.Has($"recorder:{name} arrival for '"), "the close took an arrival that had entered the world");
            }
            Assert.False(outbound.Recorded.Closed, "an arrival that threw after world entry had its connection closed");
            Assert.True(online.HoldsSlotForTest(key, s));
        }
        finally
        {
            outbound.FailOn = -1;
            online.Unregister(key, s);
            _fx.World.LeaveMap(s, LateThrowMap);
        }
    }

    /// <summary>
    /// The arrival's three refusals after the slot claim: no character record, an unreadable record, a storage
    /// error. Each gives the slot back itself and clears the claimed key, so the teardown after it has nothing
    /// to give back. Read right after the refusal, before any teardown: the connection is closed with the
    /// refusal's own reason, the slot is neither held nor parked, and the key is null. The teardown then runs,
    /// and the slot stays free. This is the behaviour the refusals' shared give-back helper (the #303 report's
    /// follow-up candidate 3) must keep unchanged.
    ///
    /// <para>Each case has a database of its own: no row, a row that does not parse, and a missing table,
    /// which makes the read itself fail.</para>
    /// </summary>
    [Theory]
    [InlineData(CharacterLoadStatus.NotFound, "arrival rejected (no character record)")]
    [InlineData(CharacterLoadStatus.Unreadable, "arrival rejected (unreadable character record)")]
    [InlineData(CharacterLoadStatus.StorageError, "arrival rejected (character storage unavailable)")]
    public async Task EachArrivalRefusalGivesTheSlotBackItselfAndClearsTheKey(CharacterLoadStatus status, string reason)
    {
        string name = $"SlkRef{(int)status}";
        string key = CharacterStore.Key(name);
        var online = _fx.World.Online;
        using var db = new IsolatedDatabase();
        if (status == CharacterLoadStatus.Unreadable)
        {
            using var cn = db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "INSERT INTO characters(username, json, updated_utc) VALUES($u, '{broken', 1);";
            cmd.Parameters.AddWithValue("$u", key);
            cmd.ExecuteNonQuery();
        }
        else if (status == CharacterLoadStatus.StorageError)
        {
            using var cn = db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "ALTER TABLE characters RENAME TO characters_gone;";
            cmd.ExecuteNonQuery();
        }
        var outbound = new RecordingOutbound($"recorder:{name}");
        var s = new Session(outbound, 2005, db.Store, _fx.World);

        try
        {
            using (var sink = LogLineSink.Acquire())
            {
                s.Receive(ArrivalFrame(name));
                _out.WriteLine($"[state] {name} ({status}): closed={outbound.Closed}, holds slot={online.HoldsSlotForTest(key, s)}, " +
                               $"parked={online.HoldsDepartedForTest(key, s)}, claimed key={ClaimedKeyField.GetValue(s) ?? "null"}");
                sink.LineContaining($"-> connection teardown ({reason})");
            }
            Assert.True(outbound.Closed);
            Assert.False(online.HoldsSlotForTest(key, s), "the refusal left the slot held");
            Assert.False(online.HoldsDepartedForTest(key, s), "the refusal parked the slot");
            Assert.Null(ClaimedKeyField.GetValue(s));           // given back already: the teardown has nothing to return

            var error = await Record.ExceptionAsync(() => s.EndReadLoopAsync(Task.CompletedTask));
            Assert.Null(error);
            Assert.False(online.HoldsSlotForTest(key, s));
            Assert.False(online.HoldsDepartedForTest(key, s));
        }
        finally
        {
            online.Unregister(key, s);
        }
    }

    /// <summary>Give <paramref name="db"/> a switch that makes it refuse every character write: a BEFORE INSERT
    /// and a BEFORE UPDATE trigger that abort the statement while <c>refuse_writes</c> has a row. Reads are
    /// untouched. <c>CharacterStore.SaveJson</c> catches the abort the way it catches any database fault (a busy
    /// database past its timeout, a full disk): it logs "Save(...) failed" and returns false.</summary>
    private static void InstallWriteRefusal(IsolatedDatabase db)
    {
        using var cn = db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE refuse_writes(flag INTEGER NOT NULL);" +
            "CREATE TRIGGER refuse_insert BEFORE INSERT ON characters WHEN EXISTS (SELECT 1 FROM refuse_writes) " +
            "BEGIN SELECT RAISE(ABORT, 'test database refuses the write'); END;" +
            "CREATE TRIGGER refuse_update BEFORE UPDATE ON characters WHEN EXISTS (SELECT 1 FROM refuse_writes) " +
            "BEGIN SELECT RAISE(ABORT, 'test database refuses the write'); END;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Turn <see cref="InstallWriteRefusal"/>'s switch on or off.</summary>
    private static void RefuseWrites(IsolatedDatabase db, bool refuse)
    {
        using var cn = db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = refuse ? "INSERT INTO refuse_writes(flag) VALUES (1);" : "DELETE FROM refuse_writes;";
        cmd.ExecuteNonQuery();
    }

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static object WriteGateOf(Session s) => WriteGateField.GetValue(s)!;

    private static bool Blocked(Thread t) => (t.ThreadState & ThreadState.WaitSleepJoin) != 0;

    /// <summary>Wait until <paramref name="t"/> is parked on a lock, failing if it finishes instead.</summary>
    private static void WaitBlocked(Thread t, string what)
    {
        Assert.True(SpinWait.SpinUntil(() => !t.IsAlive || Blocked(t), Bound), $"{what}: the thread never blocked");
        Assert.True(t.IsAlive, $"{what}: the thread finished instead of blocking");
    }

    /// <summary>Whether another thread holds <paramref name="gate"/> right now: a try-enter from here fails.</summary>
    private static bool GateHeldElsewhere(object gate)
    {
        if (!Monitor.TryEnter(gate)) return true;
        Monitor.Exit(gate);
        return false;
    }

    private static Character LoadOk(CharacterStore store, string name)
    {
        var load = store.Load(name);
        Assert.Equal(CharacterLoadStatus.Ok, load.Status);
        return Assert.IsType<Character>(load.Character);
    }

    private Character LoadOk(string name)
    {
        var load = _fx.Store.Load(name);
        Assert.Equal(CharacterLoadStatus.Ok, load.Status);
        return Assert.IsType<Character>(load.Character);
    }

    /// <summary>Deliver a real arrival for <paramref name="name"/> to <paramref name="s"/>, throwing from the probe
    /// right after its slot claim. The arrival logs the throw and closes the connection (the loading-screen close);
    /// the session holds the slot, without having entered the world, until its teardown gives it back.</summary>
    private static void ArriveAndThrowAfterTheClaim(Session s, string name)
    {
        Session.ArrivalClaimedProbeForTest = arriving =>
        {
            if (ReferenceEquals(arriving, s)) throw new InvalidOperationException(ArrivalRefused);
        };
        try
        {
            using var sink = LogLineSink.Acquire();
            s.Receive(ArrivalFrame(name));
            var thrown = sink.EntryContaining($"arrival for '{name}' {ArrivalThrewLine}");
            Assert.Contains(ArrivalRefused, thrown.Line);
        }
        finally { Session.ArrivalClaimedProbeForTest = null; }
    }

    /// <summary>Whether the arrival on <paramref name="remote"/> threw: the handler guard's line (a throw before the
    /// claim or after world entry) or the arrival's own (a throw between the two, which closes the connection).</summary>
    private static bool ArrivalThrew(LogLineSink sink, string remote) =>
        sink.Has($"{remote} handler for opcode 0x10 threw") || sink.Has($"{remote} arrival for '");

    /// <summary>A stored row for <paramref name="name"/>, as the login server's character creation leaves one.</summary>
    private void SeedRow(string name, ushort map, uint coins)
    {
        var character = new Character
        {
            SchemaVersion = Character.CurrentSchemaVersion, Name = name, Map = map, X = 5, Y = 5, Coins = coins,
        };
        Assert.True(_fx.Store.Save(character), "the seed row did not land");
    }

    /// <summary>A fresh game-port session with no character yet, as a new connection is before its arrival.</summary>
    private (Session session, RecordingOutbound outbound) Arriving(string name, string tag)
    {
        var outbound = new RecordingOutbound($"recorder:{name}:{tag}");
        return (new Session(outbound, 2005, _fx.Store, _fx.World), outbound);
    }

    /// <summary>The 0x10 arrival as the client sends it after the login server's handoff: the plaintext
    /// <c>klen "NexonInc." ulen user token</c>, with a token minted for this user and the socket-free address.
    ///
    /// <para>The account names in this class are 9 letters or fewer, so each token keeps at least 2 nonce bytes. The
    /// token table's primary key is the hash of the surviving bytes, and a name of 11 letters or more hashes an
    /// empty prefix: its mint then fails on any other such name's row still in the table, and that arrival is
    /// refused (a 13-letter name here broke <c>PersistenceTests</c>' 24-letter arrival that way).</para></summary>
    private static byte[] ArrivalFrame(string user)
    {
        var body = new List<byte> { 9 };
        body.AddRange(Encoding.ASCII.GetBytes("NexonInc."));
        body.Add((byte)user.Length);
        body.AddRange(Encoding.ASCII.GetBytes(user));
        body.AddRange(HandoffTokens.Mint(user, System.Net.IPAddress.None.ToString()));
        return TkPacket.Build(Opcode.Arrival, 0, body.ToArray());
    }

    private static List<string> MiniTexts(RecordingOutbound o) =>
        o.BodiesOf(MiniTextOut).Select(b => Encoding.ASCII.GetString(b, 3, (b[1] << 8) | b[2])).ToList();

    /// <summary>What an ordinary logout leaves, checked after a teardown one of whose steps threw: off every
    /// map, the slot given back (parked for the next login's fence, which is what <c>Depart</c> does for a
    /// session that entered the world), the row written with the character's state, the connection closed and
    /// the CLOSE line logged.</summary>
    private void AssertLeftSavedAndClosed(Session s, RecordingOutbound outbound, string name, string key, uint coins,
                                          LogLineSink sink)
    {
        _out.WriteLine($"[state] {name}: on map={_fx.World.Online.All().Contains(s)}, " +
                       $"holds slot={_fx.World.Online.HoldsSlotForTest(key, s)}, " +
                       $"parked={_fx.World.Online.HoldsDepartedForTest(key, s)}, row={_fx.Store.Load(name).Status}, " +
                       $"closed={outbound.Closed}");
        Assert.DoesNotContain(s, _fx.World.Online.All());
        Assert.False(_fx.World.Online.HoldsSlotForTest(key, s), "the leaver still holds the account's slot");
        Assert.True(_fx.World.Online.HoldsDepartedForTest(key, s), "the leaver's slot was not parked for the next login");
        var row = _fx.Store.Load(name);
        Assert.Equal(CharacterLoadStatus.Ok, row.Status);
        Assert.Equal(coins, Assert.IsType<Character>(row.Character).Coins);
        sink.LineContaining($"persisted '{name}'");
        Assert.True(outbound.Closed, "the connection was left open after the teardown");
        sink.LineContaining($"-- CLOSE recorder:{name}");
    }

    /// <summary>The step's fault is an Error line carrying the exception and its stack: the frame that threw is
    /// the failing test outbound's <c>Send</c>.</summary>
    private static void AssertFaultLogged(LogLineSink sink, string needle)
    {
        var fault = sink.EntryContaining(needle);
        Assert.Equal(LogLevel.Error, fault.Level);
        Assert.Contains(SendRefused, fault.Line);
        Assert.Contains($"{nameof(FailingOutbound)}.{nameof(FailingOutbound.Send)}", fault.Line);
    }

    private static byte[] Be32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    /// <summary>The profile window's "Exchange" button: 0x4a sub-type 0 aimed at an entity id.</summary>
    private static byte[] OpenRequest(uint targetId) =>
        SessionFixture.Frame(ExchangeIn, new byte[] { ExcOpen }.Concat(Be32(targetId)).Concat(new byte[] { 0 }).ToArray());

    /// <summary>A session standing on <paramref name="map"/> whose connection can be told to throw, built the way
    /// <see cref="SessionFixture.PlayerWith"/> builds one.</summary>
    private (Session session, FailingOutbound outbound) FailingPlayer(string name, Action<Character> configure,
                                                                      ushort map, ushort x, ushort y)
    {
        var character = new Character
        {
            SchemaVersion = Character.CurrentSchemaVersion, Id = _fx.World.AllocatePlayerId(), Name = name,
            Map = map, X = x, Y = y,
        };
        configure(character);
        var outbound = new FailingOutbound($"recorder:{name}");
        var session = new Session(outbound, 2005, _fx.Store, _fx.World, character);
        _fx.World.EnterMap(session, map);
        return (session, outbound);
    }

    /// <summary>A recorder that throws from <see cref="Send"/> for frames of one opcode once told to — standing
    /// in for any fault on a peer's side of a step, since a real transport's <c>Send</c> is a non-blocking
    /// <c>TryWrite</c>. <see cref="FailOn"/> is -1 (off) until the test has finished setting up.</summary>
    private sealed class FailingOutbound : IOutbound
    {
        private readonly RecordingOutbound _inner;
        private int _failOn = -1;

        public FailingOutbound(string remote) => _inner = new RecordingOutbound(remote);

        /// <summary>The opcode whose frames throw, or -1.</summary>
        public int FailOn
        {
            get => Volatile.Read(ref _failOn);
            set => Volatile.Write(ref _failOn, value);
        }

        /// <summary>What went out, for a test that reads it back.</summary>
        public RecordingOutbound Recorded => _inner;

        public string Remote => _inner.Remote;
        public int Capacity => _inner.Capacity;
        public int QueueDepth => _inner.QueueDepth;

        public bool Send(byte[] frame)
        {
            // aa | len_hi | len_lo | op: the same byte Session.Send reads as the last outbound opcode.
            if (frame.Length > 3 && frame[3] == FailOn) throw new InvalidOperationException(SendRefused);
            return _inner.Send(frame);
        }

        public void Close() => _inner.Close();
    }
}
