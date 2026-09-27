using System.Reflection;
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
/// <para>Driven through the read loop's real exit (<c>Session.EndReadLoopAsync</c>) on socket-free sessions.
/// Nothing here waits on a clock.</para>
/// </summary>
[Collection("world")]
public sealed class TeardownSlotLeakTests
{
    /// <summary>Content-free map ids; no other class stands here.</summary>
    private const ushort TradeMap = 61740, PartyMap = 61741;

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
