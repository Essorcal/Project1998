using System.Diagnostics;
using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// #57's acceptance line: <b>a trade cannot finalize after either party warps or dies.</b>
///
/// <para>The gates that decide a trade may start at all — same map, both alive — used to be asked exactly
/// once, in <c>Session.TryStartTrade</c>, and never again. <c>_trade</c> was cleared only by the cancel
/// packet, by finalize and by disconnect, so a party who warped away or was killed mid-negotiation kept a
/// live <c>Trade</c> and a live window, and <c>ConfirmExchange</c> would happily finalize it. That is a
/// player-reachable duplication/theft surface, not a cosmetic one: the two cases below are the exploit, and
/// each of them moves real coin on the pre-fix code.</para>
///
/// <para>Both cases drive the REAL path rather than writing a field. The warp is a walk step onto a genuine
/// Warps.csv doorway (the same door <see cref="WarpUnderLockTests"/> uses), which is what reaches
/// <c>Session.EnterMap</c> — the funnel every non-walk position change goes through. The death is lethal
/// melee damage through <c>Session.ReceiveMeleeDamage</c> -&gt; <c>TakeDamage</c> -&gt; <c>Die()</c>, the
/// same sequence a mob or another player produces.</para>
///
/// <para><see cref="ATradeBetweenTwoLivingPartiesOnOneMapStillFinalizes"/> is the negative control: neither
/// teardown fires for a trade whose parties stay put and stay alive, and the coin still moves. Without it
/// "nothing finalizes, ever" would pass the two cases above.</para>
/// </summary>
[Collection("world")]
public class TradeTeardownTests
{
    private const byte ExchangeIn = 0x4a;    // client -> server (RTK clif_parse_exchange)
    private const byte ExchangeOut = 0x42;   // server -> client (the window's own packet family)
    private const byte WalkIn = 0x06;        // client -> server walk step
    private const byte MiniTextOut = 0x0A;   // server -> client status line / message text

    private const byte ExcOpen = 0, ExcGold = 3, ExcConfirm = 5;

    /// <summary>Verbatim from <c>Session.TradeDoneText</c> — pinned here rather than shared, for the reason
    /// <c>ExchangeAcceptHandlerTests</c> gives: the client renders it, so it is part of the wire contract.</summary>
    private const string DoneText = "You exchanged, and gave away ownership of the items.";

    /// <summary>The teardown line. Deliberately the same string the disconnect teardown has always used
    /// (<c>Session.TearDownWorldState</c>) — walking away and dropping the link are the same event as far as
    /// the surviving window is concerned, and RTK has no separate walk-away line to port.</summary>
    private const string CancelText = "Exchange cancelled.";

    /// <summary>Country Farm's door into Mignok's Home — a real Warps.csv doorway, read back rather than
    /// hardcoded.</summary>
    private const ushort DoorMap = 4715, DoorX = 17, DoorY = 5;

    /// <summary>Content-free map ids, one per test: no Maps.csv row means nothing is terrain-blocked and no
    /// other test on the shared World is standing there. 59000-65000 is the instance band, which
    /// <c>ApplyDeathPenalties</c> charges exp only for — so a death here spills no coin onto the floor to
    /// confuse the balances these tests assert on.</summary>
    private const ushort DeathMap = 60020, ControlMap = 60021, RaceMap = 60022;

    /// <summary>The tail of the "you are dead" line <c>Die()</c> sends after the penalties and before the
    /// save — the last thing the death sequence puts on the wire, and therefore the cheapest proof from
    /// another thread that the sequence had already run to its end.</summary>
    private const string DefeatedTextPrefix = "You have been defeated";

    /// <summary>What <c>DeathExpLoss</c> announces. Pinned so the ORDER of the two can be asserted.</summary>
    private const string ExpLostTextPrefix = "You've lost";

    private const string RevivedText = "A test revived you mid-death.";

    private readonly SessionFixture _fx;

    public TradeTeardownTests(SessionFixture fx) => _fx = fx;

    private static byte[] Be32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    /// <summary>An inbound <c>0x4a</c>: sub-type, the partner's entity id, then whatever that sub-type
    /// carries (RTK's trailing zero when it carries nothing).</summary>
    private static byte[] ExchangeRequest(byte sub, uint targetId, byte[]? tail = null) =>
        SessionFixture.Frame(ExchangeIn, new byte[] { sub }.Concat(Be32(targetId))
                                                           .Concat(tail ?? new byte[] { 0 }).ToArray());

    /// <summary>Sub-type 4 as it goes out: <c>04 | 00 | len | text</c> — the box that closes a window that
    /// did not finish.</summary>
    private static byte[] Message(string text) =>
        new byte[] { 4, 0, (byte)text.Length }.Concat(Encoding.ASCII.GetBytes(text)).ToArray();

    /// <summary>Sub-type 5 as it goes out: <c>05 | extra | len | text</c>.</summary>
    private static byte[] Finish(byte extra, string text) =>
        new byte[] { 5, extra, (byte)text.Length }.Concat(Encoding.ASCII.GetBytes(text)).ToArray();

    /// <summary>The text of every <c>0x0A</c> recorded so far, in order: <c>type | len(u16 BE) | text</c>.</summary>
    private static List<string> MiniTexts(RecordingOutbound o) =>
        o.BodiesOf(MiniTextOut).Select(b => Encoding.ASCII.GetString(b, 3, (b[1] << 8) | b[2])).ToList();

    private static byte[] WalkPacket(byte dir, int fromX, int fromY) =>
        SessionFixture.Frame(WalkIn, new byte[]
        {
            dir, 0,
            (byte)(fromX >> 8), (byte)fromX,
            (byte)(fromY >> 8), (byte)fromY,
        });

    /// <summary>
    /// <b>One party walks through a door.</b> The trade ends on both sides as the map change is processed,
    /// and the two confirms that follow — which on the pre-fix code were a complete, successful exchange —
    /// move nothing and produce no packet at all, because neither session has a trade any more.
    ///
    /// <para>Red on <c>57c6931</c> at the first balance assertion: the 500 coin the walker had staked
    /// finalized into the stayer's purse from another map.</para>
    /// </summary>
    [Fact]
    public void AWarpEndsBothWindowsAndALaterConfirmDoesNotFinalize()
    {
        Assert.True(Content.TryMap(DoorMap, out var src));
        Assert.True(Content.TryWarp(DoorMap, DoorX, DoorY, out _), "Country Farm's door into Mignok's Home");

        var (walker, walkerOut, walkerChar) = _fx.PlayerWith(
            "TradeWarpWalker", c => { c.MapXs = src.Xs; c.MapYs = src.Ys; c.Coins = 500; },
            DoorMap, DoorX, (ushort)(DoorY + 1));
        var (stayer, stayerOut, stayerChar) = _fx.PlayerWith(
            "TradeWarpStayer", c => { c.MapXs = src.Xs; c.MapYs = src.Ys; },
            DoorMap, 10, 10);

        walkerOut.Clear();
        stayerOut.Clear();
        walker.Receive(ExchangeRequest(ExcOpen, stayer.PlayerId));
        Assert.Single(walkerOut.BodiesOf(ExchangeOut));    // the window really is open, on both sides
        Assert.Single(stayerOut.BodiesOf(ExchangeOut));
        walker.Receive(ExchangeRequest(ExcGold, stayer.PlayerId, Be32(500)));   // coin on the table

        // The walk-away: one step north, onto a doorway that warps to another map entirely.
        walkerOut.Clear();
        stayerOut.Clear();
        walker.Receive(WalkPacket(dir: 0, fromX: DoorX, fromY: DoorY + 1));
        Assert.NotEqual(DoorMap, walker.CharMap);          // the warp happened; the rest is about the trade

        var walkerClose = walkerOut.BodiesOf(ExchangeOut);
        var stayerClose = stayerOut.BodiesOf(ExchangeOut);
        walkerOut.Clear();
        stayerOut.Clear();

        // Both windows confirm from across the map boundary — the exploit, exactly as a client can send it.
        walker.Receive(ExchangeRequest(ExcConfirm, stayer.PlayerId));
        stayer.Receive(ExchangeRequest(ExcConfirm, walker.PlayerId));

        Assert.Equal(500u, walkerChar.Coins);              // nothing moved
        Assert.Equal(0u, stayerChar.Coins);
        Assert.Empty(walkerOut.BodiesOf(ExchangeOut));     // and nothing was even answered
        Assert.Empty(stayerOut.BodiesOf(ExchangeOut));
        // ...because the map change closed both windows the moment it happened.
        Assert.Equal(Message(CancelText), Assert.Single(walkerClose));
        Assert.Equal(Message(CancelText), Assert.Single(stayerClose));
    }

    /// <summary>
    /// <b>One party is killed.</b> Same shape, driven by lethal melee damage: <c>Die()</c> tears the trade
    /// down, and the confirms that follow move nothing. The coin is staked by the SURVIVOR here, so the
    /// balance assertions cannot be confused with anything the death penalty does to the victim.
    ///
    /// <para>Red on <c>57c6931</c> at the first balance assertion: a ghost finalized the exchange and took
    /// the 500 coin.</para>
    /// </summary>
    [Fact]
    public void DeathEndsBothWindowsAndALaterConfirmDoesNotFinalize()
    {
        var (victim, victimOut, victimChar) = _fx.PlayerWith("TradeDeathVictim", _ => { }, DeathMap, 5, 10);
        var (partner, partnerOut, partnerChar) = _fx.PlayerWith("TradeDeathPartner", c => c.Coins = 500,
                                                                DeathMap, 6, 10);

        victimOut.Clear();
        partnerOut.Clear();
        victim.Receive(ExchangeRequest(ExcOpen, partner.PlayerId));
        Assert.Single(victimOut.BodiesOf(ExchangeOut));
        Assert.Single(partnerOut.BodiesOf(ExchangeOut));
        partner.Receive(ExchangeRequest(ExcGold, victim.PlayerId, Be32(500)));

        victimOut.Clear();
        partnerOut.Clear();
        victim.ReceiveMeleeDamage(9999, partner, crit: false);
        Assert.True(victim.IsDead, "the melee blow was meant to be lethal");

        var victimClose = victimOut.BodiesOf(ExchangeOut);
        var partnerClose = partnerOut.BodiesOf(ExchangeOut);
        victimOut.Clear();
        partnerOut.Clear();

        victim.Receive(ExchangeRequest(ExcConfirm, partner.PlayerId));
        partner.Receive(ExchangeRequest(ExcConfirm, victim.PlayerId));

        Assert.Equal(500u, partnerChar.Coins);             // nothing moved
        Assert.Equal(0u, victimChar.Coins);
        Assert.Empty(victimOut.BodiesOf(ExchangeOut));
        Assert.Empty(partnerOut.BodiesOf(ExchangeOut));
        Assert.Equal(Message(CancelText), Assert.Single(victimClose));
        Assert.Equal(Message(CancelText), Assert.Single(partnerClose));
    }

    /// <summary>
    /// <b>The negative control.</b> Two living parties who stay on one map still exchange: the confirm latch
    /// goes out as 1 then 0, the window closes with the DONE text rather than the cancel box, and the coin
    /// actually changes hands. This is what stops the two teardowns above from being satisfied by a trade
    /// system that has simply stopped working.
    /// </summary>
    [Fact]
    public void ATradeBetweenTwoLivingPartiesOnOneMapStillFinalizes()
    {
        var (giver, giverOut, giverChar) = _fx.PlayerWith("TradeControlGiver", c => c.Coins = 500,
                                                          ControlMap, 5, 10);
        var (taker, takerOut, takerChar) = _fx.PlayerWith("TradeControlTaker", _ => { }, ControlMap, 6, 10);

        giverOut.Clear();
        takerOut.Clear();
        giver.Receive(ExchangeRequest(ExcOpen, taker.PlayerId));
        giver.Receive(ExchangeRequest(ExcGold, taker.PlayerId, Be32(500)));

        giverOut.Clear();
        takerOut.Clear();
        giver.Receive(ExchangeRequest(ExcConfirm, taker.PlayerId));
        Assert.Equal(Finish(1, DoneText), Assert.Single(giverOut.BodiesOf(ExchangeOut)));
        Assert.Equal(Finish(1, DoneText), Assert.Single(takerOut.BodiesOf(ExchangeOut)));

        giverOut.Clear();
        takerOut.Clear();
        taker.Receive(ExchangeRequest(ExcConfirm, giver.PlayerId));

        Assert.Equal(Finish(0, DoneText), Assert.Single(giverOut.BodiesOf(ExchangeOut)));
        Assert.Equal(Finish(0, DoneText), Assert.Single(takerOut.BodiesOf(ExchangeOut)));
        Assert.Equal(0u, giverChar.Coins);
        Assert.Equal(500u, takerChar.Coins);
    }

    /// <summary>
    /// <b>The death finishes before its own teardown lets anyone in.</b> The teardown is the LAST statement of
    /// <c>Die()</c> rather than the first, and this is the fact that says why.
    ///
    /// <para><c>EndTrade</c> takes both sessions' monitors through <c>WithStatePair</c>. When the partner ranks
    /// below the dying player, that acquisition is DESCENDING, so #29 rule 2 exits the victim's own monitor
    /// while it blocks on the partner's — a real gap in the middle of <c>Die()</c>, not a theoretical one, and
    /// three shipped paths walk straight into it from another thread: a GM <c>@revive &lt;other&gt;</c>, an NPC
    /// Rebirth, and a poet's Resurrect. With the teardown FIRST (as it was at <c>2c1452a</c>) a revive landing
    /// in that gap left <c>ClearAllTimedEffects</c>, <c>ApplyDeathPenalties</c>, "You have been defeated!" and
    /// <c>SaveChar</c> to run on a player who was alive again: charged the death penalty, told they were
    /// defeated, and persisted that way. With it LAST, the gap opens only after every one of those has already
    /// happened, and the revive that follows is an ordinary revive of a completed corpse.</para>
    ///
    /// <para>So the assertion is about ORDER, and it is taken from inside the gap: the reviving thread records
    /// what it can see at the instant it gets the victim's monitor. Red at <c>2c1452a</c> — the exp is still
    /// untouched, the "defeated" line has not been sent and nothing has been saved, because the death has only
    /// run as far as <c>_char.Mounted = false</c>.</para>
    ///
    /// <para>The partner stands out of view rather than beside the victim, for a reason worth stating: with a
    /// peer in view, <c>Die()</c> -&gt; <c>ResyncPeers</c> -&gt; <c>ShowPlayer(peer)</c> reads that peer through
    /// <c>Snapshot()</c>, which takes the PEER's monitor and so descends — and drops — one step earlier still.
    /// That is master's behaviour too (<c>ResyncPeers</c> is untouched here), so it is not this change's to fix
    /// or to pin; keeping the partner outside the viewport rect leaves the teardown as the only cross-session
    /// acquisition in the sequence, which is what this fact is about.</para>
    /// </summary>
    [Fact]
    public void ADeathFinishesBeforeItsOwnTradeTeardownDropsTheMonitor()
    {
        const uint StartExp = 1_000_000;

        // The partner is created FIRST, so it ranks BELOW the victim and the pair acquisition has to descend.
        // It is placed far off the victim's viewport rect: see the note above about ResyncPeers.
        var (partner, partnerOut, _) = _fx.PlayerWith("TradeRacePartner", _ => { }, RaceMap, 60, 60);
        var (victim, victimOut, victimChar) = _fx.PlayerWith("TradeRaceVictim", c => c.Exp = StartExp,
                                                             RaceMap, 5, 10);
        Assert.True(victim.StateRank > partner.StateRank,
                    "the victim has to outrank the partner or the teardown never descends and never drops");

        victimOut.Clear();
        partnerOut.Clear();
        victim.Receive(ExchangeRequest(ExcOpen, partner.PlayerId));   // same map is the only gate TryStartTrade has
        Assert.Single(victimOut.BodiesOf(ExchangeOut));
        Assert.Single(partnerOut.BodiesOf(ExchangeOut));
        victimOut.Clear();
        partnerOut.Clear();

        // A third thread parks on the partner's monitor so the descending acquisition really does block.
        var partnerHeld = new ManualResetEventSlim();
        var releasePartner = new ManualResetEventSlim();
        var holder = new Thread(() => partner.WithState(() => { partnerHeld.Set(); releasePartner.Wait(); }))
        { IsBackground = true, Name = "partner-holder" };
        holder.Start();
        Assert.True(partnerHeld.Wait(5000), "the holder never took the partner's monitor");

        // The kill, on its own thread. Environment damage has no attacker, so nothing in TakeDamage reaches
        // the partner before Die() does — the teardown is the only thing in the sequence that can block.
        var killer = new Thread(() => victim.ReceiveEnvironmentDamage(9999, "a cold tile, in a test"))
        { IsBackground = true, Name = "killer" };
        killer.Start();
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref victimChar.Hp) != 0 && sw.ElapsedMilliseconds < 5000) Thread.Sleep(5);
        Assert.Equal(0u, victimChar.Hp);
        Thread.Sleep(300);
        Assert.True(killer.IsAlive, "Die() should still be parked on the partner's monitor");

        // The revive. It can only get in through the gap the teardown opens; what it finds when it does is
        // the whole of this fact.
        bool sawDead = false;
        uint expAtEntry = StartExp, savedExpAtEntry = StartExp;
        var textsAtEntry = new List<string>();
        var reviverEntered = new ManualResetEventSlim();
        var reviver = new Thread(() => victim.WithState(() =>
        {
            sawDead = victim.IsDead;
            expAtEntry = victimChar.Exp;
            textsAtEntry = MiniTexts(victimOut);
            var saved = _fx.Store.Load("TradeRaceVictim");
            if (saved.Status == CharacterLoadStatus.Ok) savedExpAtEntry = saved.Character!.Exp;
            reviverEntered.Set();
            victim.ReviveInPlace(RevivedText);
        })) { IsBackground = true, Name = "reviver" };
        reviver.Start();

        Assert.True(reviverEntered.Wait(5000),
                    "the reviver never got the victim's monitor: the teardown did not drop it at all");
        Assert.True(sawDead, "the reviver landed on a living player — it never got inside the death sequence");
        Assert.True(reviver.Join(10000), "the revive never finished");
        // Still parked: the reviver ran entirely inside the gap, so nothing below is racing the killer thread.
        Assert.True(killer.IsAlive, "Die() got past its teardown before the partner's monitor was released");

        releasePartner.Set();
        Assert.True(killer.Join(10000), "Die() never finished");
        Assert.True(holder.Join(5000));

        // RED at 2c1452a, where the teardown was Die()'s first statement: at this point the death had done
        // nothing but unmount the horse.
        Assert.Contains(textsAtEntry, t => t.StartsWith(DefeatedTextPrefix));
        Assert.Contains(textsAtEntry, t => t.StartsWith(ExpLostTextPrefix));
        Assert.True(expAtEntry < StartExp,
                    $"the death penalty had not been applied when the revive got in (exp {expAtEntry})");
        Assert.True(savedExpAtEntry < StartExp,
                    $"SaveChar had not persisted the death when the revive got in (saved exp {savedExpAtEntry})");

        // ...and what followed is an ordinary revive of a completed corpse: alive, and charged exactly once.
        Assert.False(victim.IsDead);
        Assert.Equal(expAtEntry, victimChar.Exp);
        var texts = MiniTexts(victimOut);
        Assert.Equal(1, texts.Count(t => t.StartsWith(ExpLostTextPrefix)));
        Assert.True(texts.IndexOf(RevivedText) > texts.FindIndex(t => t.StartsWith(DefeatedTextPrefix)),
                    $"the revive line came before the death finished; texts: {string.Join(" | ", texts)}");

        // The teardown itself still did its job, on both windows, exactly once.
        Assert.Equal(Message(CancelText), Assert.Single(victimOut.BodiesOf(ExchangeOut)));
        Assert.Equal(Message(CancelText), Assert.Single(partnerOut.BodiesOf(ExchangeOut)));
    }
}
