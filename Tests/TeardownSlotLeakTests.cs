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
/// <para>Driven through the read loop's real exit (<c>Session.EndReadLoopAsync</c>) on socket-free sessions.
/// Nothing here waits on a clock.</para>
/// </summary>
[Collection("world")]
public sealed class TeardownSlotLeakTests
{
    /// <summary>Content-free map ids; no other class stands here.</summary>
    private const ushort TradeMap = 61740, PartyMap = 61741, ArrivalMap = 61742, NewerOwnerMap = 61743;

    /// <summary>What the arrival probe throws, so the handler guard's log line can be matched to it.</summary>
    private const string ArrivalRefused = "test probe threw after the slot was claimed";

    private const string KickedText = "You have logged in from another location.";

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
    /// Item 2. An arrival claims the account's slot and then throws before it enters the world. The handler guard
    /// in <c>Session.Handle</c> logs the throw and keeps the session, which now holds the slot with
    /// <c>_enteredWorld</c> false. Its teardown gives the slot back (dropped, not parked), and a later login for
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
    /// world, so the kick writes nothing (its <c>_enteredWorld</c> gate), latches it, tells it and closes it —
    /// which is all the next login's kick ever did to such a session, on master too. The stuck session's teardown
    /// then runs and must leave the newer login's slot alone.
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

            using (var sink = LogLineSink.Acquire())
            {
                later.Receive(ArrivalFrame(name));
                sink.LineContaining($"ARRIVAL: '{name}' already online — kicking previous session");
            }
            Assert.True(stuck.IsReplaced);
            Assert.True(stuckOut.Closed);
            Assert.Contains(KickedText, MiniTexts(stuckOut));
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

    /// <summary>Deliver a real arrival for <paramref name="name"/> to <paramref name="s"/>, throwing from the probe
    /// right after its slot claim. <c>Session.Handle</c>'s guard catches and logs the throw, as it would any
    /// handler's, and the session carries on without having entered the world.</summary>
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
            var guard = sink.EntryContaining("handler for opcode 0x10 threw");
            Assert.Contains(ArrivalRefused, guard.Line);
        }
        finally { Session.ArrivalClaimedProbeForTest = null; }
    }

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
    /// <c>klen "NexonInc." ulen user token</c>, with a token minted for this user and the socket-free address.</summary>
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
