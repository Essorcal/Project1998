using System.Diagnostics;
using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// #174: <b>a PvP kill's HP write and its death sequence are one critical section.</b>
///
/// <para><c>TakeDamage</c> (<c>Server/DamageIntake.cs</c>) marks both halves of a PvP exchange so an arena
/// pet knows who to go for. All six shipped PvP intake sites run on the attacker's own handler thread, whose
/// <c>Dispatch</c> is wrapped in <c>WithState</c>, so that thread already holds the attacker's monitor.
/// <c>foe.MarkPvpFoe(_char.Id)</c> is therefore re-entrant under #29 rule 3
/// (<c>Session.State.cs</c>) and never drops the victim's monitor on a shipped path.</para>
///
/// <para>The descent, and the gap #29 rule 2 would open, is reachable only from a caller that does not already
/// hold the attacker's monitor; none is shipped today, though a queued or timer-driven blow or #29's
/// channel-drained read loop could introduce one. These facts use a bare-thread caller, so the race is
/// observable here. It is the same shape #172 found in <c>Die()</c>'s trade teardown and #177 found in its
/// viewport reconcile (both in <see cref="TradeTeardownTests"/>), one step earlier in the damage path.</para>
///
/// <para>The last-statement ordering is kept as a hardening: <c>TakeDamage</c> through <c>Die()</c> stays one
/// critical section for any caller, with the mark after the death sequence or survived-hit epilogue. The two
/// facts pin that invariant with the bare-thread race and its control: a nonlethal hit still marks both sides.</para>
/// </summary>
[Collection("world")]
public class PvpDeathOrderingTests
{
    private const byte MiniTextOut = 0x0A;   // server -> client status line / message text

    /// <summary>Yusa Arena — a real <c>MapPvP=1</c> row in <c>game-data/Maps.csv</c> (id 32, 33x33 in
    /// <c>map_index.csv</c>), with no spawn row in <c>Spawns.csv</c> or <c>AreaSpawns.csv</c>, so entering it
    /// drags no mobs into the race. A PvP map is not decoration here: <c>Session.Combat</c>'s melee target
    /// gate (<c>:250</c>) only lets a player swing at another player where <c>Content.IsPvpMap</c> is true, so
    /// an arena is the only place a PvP kill happens at all. The second fact uses Stone Valley Arena (33), the
    /// same kind of row, because the fixture's World is shared and its sessions never leave the map they were
    /// created on — two facts on one arena would put each other's players in the race's viewport.</summary>
    private const ushort RaceArena = 32, ControlArena = 33;

    /// <summary>The tail of the "you are dead" line <c>Die()</c> sends after the penalties and before the
    /// save — the last thing the death sequence puts on the wire, and therefore the cheapest proof from
    /// another thread that the sequence had already run to its end. Same constant, same reason, as
    /// <see cref="TradeTeardownTests"/>.</summary>
    private const string DefeatedTextPrefix = "You have been defeated";

    private const string RevivedText = "A test revived you mid-kill.";

    /// <summary>What <see cref="ADeathFromAPvpBlowFinishesBeforeTheFoeMarkDropsTheMonitor"/> records when the
    /// character store has no row for the victim at all — i.e. <c>SaveChar</c> has not run yet. A real HP is
    /// never this, so the assertion cannot be satisfied by an accident of loading.</summary>
    private const uint NeverSaved = uint.MaxValue;

    private readonly SessionFixture _fx;

    public PvpDeathOrderingTests(SessionFixture fx) => _fx = fx;

    /// <summary>The text of every <c>0x0A</c> recorded so far, in order: <c>type | len(u16 BE) | text</c>.</summary>
    private static List<string> MiniTexts(RecordingOutbound o) =>
        o.BodiesOf(MiniTextOut).Select(b => Encoding.ASCII.GetString(b, 3, (b[1] << 8) | b[2])).ToList();

    /// <summary>
    /// <b>The death finishes before the PvP foe mark lets anyone in.</b> The mark of the OTHER side is the
    /// last statement of <c>TakeDamage</c> rather than a statement between the HP write and <c>Die()</c>, and
    /// this is the fact that says why.
    ///
    /// <para>The attacker is created FIRST, so it ranks BELOW the victim and <c>foe.MarkPvpFoe</c> has to
    /// descend into it; a third thread parks on the attacker's monitor so that descent really blocks. It also
    /// stands at (30,30), far outside the victim's drawn band at (5,10), for the reason
    /// <see cref="TradeTeardownTests.ADeathFinishesBeforeItsOwnTradeTeardownDropsTheMonitor"/> keeps its own
    /// partner out of view: with the attacker in the viewport, <c>Die()</c> -&gt; <c>ResyncPeers</c> -&gt;
    /// <c>ShowPlayer(peer)</c> -&gt; <c>peer.Snapshot()</c> would descend into the same monitor as well, and
    /// the drop the reviver walks through could be either one. Out of view, the foe mark is the ONLY
    /// cross-session acquisition anywhere in the sequence, so the gap can be nothing else. (Nothing gates the
    /// blow on range: <c>ReceiveMeleeDamage</c> is called directly, exactly as the two facts in
    /// <see cref="TradeTeardownTests"/> call <c>ReceiveEnvironmentDamage</c> directly.)</para>
    ///
    /// <para>The assertion is about ORDER, and it is taken from inside the gap: the reviving thread records
    /// what it can see at the instant it gets the victim's monitor. Red on <c>e2938a8</c> — HP 0 and
    /// <c>IsDead</c> true, but no "You have been defeated!" on the wire and no row in the store at all,
    /// because <c>Die()</c> had not been reached. Green here, where the mark comes after it.</para>
    ///
    /// <para>An arena charges no death penalty (<c>ApplyDeathPenalties</c> returns on
    /// <c>Content.IsPvpMap</c>), which is exactly why this defect has been harmless so far and why the proof
    /// is the defeated line and the save rather than the exp loss the other two facts read. It is also why
    /// the fix matters anyway: the harmlessness is a property of today's penalty table, not of the code.</para>
    ///
    /// <para><c>reviverEntered.Wait</c> is kept deliberately: it passes on the base AND on the fixed tree,
    /// which is the point — moving the mark does not remove the drop, it puts it where nothing is left to
    /// invalidate.</para>
    /// </summary>
    [Fact]
    public void ADeathFromAPvpBlowFinishesBeforeTheFoeMarkDropsTheMonitor()
    {
        Assert.True(Content.IsPvpMap(RaceArena), "the melee PvP gate only opens where IsPvpMap is true");
        Assert.True(Content.TryMap(RaceArena, out var arena));

        // The attacker is created FIRST, so it ranks BELOW the victim and the foe mark has to descend.
        var (attacker, _, _) = _fx.PlayerWith(
            "PvpFoeAttacker", c => { c.MapXs = arena.Xs; c.MapYs = arena.Ys; }, RaceArena, 30, 30);
        var (victim, victimOut, victimChar) = _fx.PlayerWith(
            "PvpFoeVictim", c => { c.MapXs = arena.Xs; c.MapYs = arena.Ys; }, RaceArena, 5, 10);
        Assert.True(victim.StateRank > attacker.StateRank,
                    "the victim has to outrank the attacker or the foe mark never descends and never drops");
        victimOut.Clear();

        // A third thread parks on the attacker's monitor so the descending acquisition really does block.
        var attackerHeld = new ManualResetEventSlim();
        var releaseAttacker = new ManualResetEventSlim();
        var holder = new Thread(() => attacker.WithState(() => { attackerHeld.Set(); releaseAttacker.Wait(); }))
        { IsBackground = true, Name = "attacker-holder" };
        holder.Start();
        Assert.True(attackerHeld.Wait(5000), "the holder never took the attacker's monitor");

        // The kill, on its own thread: a PvP melee blow, which is the intake that carries a foe.
        var killer = new Thread(() => victim.ReceiveMeleeDamage(9999, attacker, crit: false))
        { IsBackground = true, Name = "killer" };
        killer.Start();
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref victimChar.Hp) != 0 && sw.ElapsedMilliseconds < 5000) Thread.Sleep(5);
        Assert.Equal(0u, victimChar.Hp);
        Thread.Sleep(300);
        Assert.True(killer.IsAlive, "TakeDamage should still be parked on the attacker's monitor");

        // The revive. It can only get in through the gap the foe mark opens; what it finds when it does is
        // the whole of this fact.
        bool sawDead = false;
        uint hpAtEntry = uint.MaxValue, savedHpAtEntry = NeverSaved;
        var textsAtEntry = new List<string>();
        var reviverEntered = new ManualResetEventSlim();
        var reviver = new Thread(() => victim.WithState(() =>
        {
            sawDead = victim.IsDead;
            hpAtEntry = victimChar.Hp;
            textsAtEntry = MiniTexts(victimOut);
            var saved = _fx.Store.Load("PvpFoeVictim");
            if (saved.Status == CharacterLoadStatus.Ok) savedHpAtEntry = saved.Character!.Hp;
            reviverEntered.Set();
            victim.ReviveInPlace(RevivedText);
        })) { IsBackground = true, Name = "reviver" };
        reviver.Start();

        Assert.True(reviverEntered.Wait(5000),
                    "the reviver never got the victim's monitor: the foe mark did not drop it at all");
        Assert.True(sawDead, "the reviver landed on a living player — it never got inside the kill");
        Assert.True(reviver.Join(10000), "the revive never finished");
        // Still parked: the reviver ran entirely inside the gap, so nothing below is racing the killer thread.
        Assert.True(killer.IsAlive, "TakeDamage got past its foe mark before the attacker's monitor was released");

        releaseAttacker.Set();
        Assert.True(killer.Join(10000), "TakeDamage never finished");
        Assert.True(holder.Join(5000));

        // RED at e2938a8, where the foe mark sat between the HP write and Die(): at this point the kill had
        // written HP 0 and nothing else — no ghost, no defeated line, no save. (And with the HP restored under
        // it, the `if (IsDead)` below the mark then read FALSE and Die() never ran at all.)
        Assert.Equal(0u, hpAtEntry);
        Assert.Contains(textsAtEntry, t => t.StartsWith(DefeatedTextPrefix));
        Assert.Equal(0u, savedHpAtEntry);

        // ...and what followed is an ordinary revive of a completed corpse: alive, defeated exactly once, and
        // the revive line after the death's own.
        Assert.False(victim.IsDead);
        var texts = MiniTexts(victimOut);
        Assert.Equal(1, texts.Count(t => t.StartsWith(DefeatedTextPrefix)));
        Assert.True(texts.IndexOf(RevivedText) > texts.FindIndex(t => t.StartsWith(DefeatedTextPrefix)),
                    $"the revive line came before the death finished; texts: {string.Join(" | ", texts)}");

        // Both halves of the exchange are still remembered — the mark moved, it did not go away. This is what
        // World.MobAiTick reads to point an arena pet at somebody.
        Assert.Equal(attacker.PlayerId, victim.PvpFoeId);
        Assert.Equal(victim.PlayerId, attacker.PvpFoeId);
    }

    /// <summary>
    /// <b>A hit nobody dies from still marks both sides.</b> The deferred mark runs on the survived-hit path
    /// too, after <c>AfterSurvivedHit</c> rather than before it, and this is the control that says the move
    /// did not quietly drop it on the path that has no <c>Die()</c> in it.
    ///
    /// <para>Single-threaded on purpose: there is no race to show here, only the outcome. The blow is 5 raw
    /// against the 50 HP a fresh character has, so the victim is still standing when <c>TakeDamage</c>
    /// returns.</para>
    /// </summary>
    [Fact]
    public void ANonLethalPvpBlowStillMarksBothSides()
    {
        Assert.True(Content.IsPvpMap(ControlArena));
        Assert.True(Content.TryMap(ControlArena, out var arena));

        var (attacker, _, _) = _fx.PlayerWith(
            "PvpMarkAttacker", c => { c.MapXs = arena.Xs; c.MapYs = arena.Ys; }, ControlArena, 6, 10);
        var (victim, _, victimChar) = _fx.PlayerWith(
            "PvpMarkVictim", c => { c.MapXs = arena.Xs; c.MapYs = arena.Ys; }, ControlArena, 5, 10);

        victim.ReceiveMeleeDamage(5, attacker, crit: false);

        Assert.False(victim.IsDead, "the blow was meant to be survivable");
        Assert.Equal(45u, victimChar.Hp);
        Assert.Equal(attacker.PlayerId, victim.PvpFoeId);
        Assert.Equal(victim.PlayerId, attacker.PvpFoeId);
    }
}
