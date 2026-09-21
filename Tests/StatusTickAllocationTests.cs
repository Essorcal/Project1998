using System.Buffers.Binary;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The no-op path of <see cref="Session.TickSleep"/> and <see cref="Session.TickPoison"/> allocates nothing,
/// and the paths that DO broadcast still send the same frames with the same arguments.
///
/// <para><b>Why this needs tests by the "would this fail loudly?" rule.</b> Both failure modes are silent.
/// Move either captured local back up to method scope and the compiler builds its display class at method
/// entry again — 32 B on every one of the 400 calls a beat, 25,600 B a beat at 400 players — and nothing
/// throws, nothing logs, and every behavioural test stays green; the only symptom is a GC that runs more
/// often under load. And a rewrite of a broadcast that changed one argument (the wrong id, the wrong
/// percent, the crit byte dropped) would draw a wrong bar over a poisoned player's head, which is precisely
/// the "plausible-but-wrong" class this repository's tests exist for.</para>
///
/// <para>Measured in <c>briefs/reports/status-tick-scope-opus.md</c>; evidence in
/// <c>reviews/status-tick-scope-evidence/</c>.</para>
/// </summary>
[Collection("world")]
public class StatusTickAllocationTests
{
    /// <summary>The roster the production tick sweeps. The claim is per CALL, so the count only sets the
    /// resolution: at the old 64 B per player per beat a broken head reads 25,600 B a beat here, not a
    /// number a tolerance could hide.</summary>
    private const int Players = 400;

    /// <summary>Beats swept per pass. 400 x 20 = 8,000 calls of each method.</summary>
    private const int Beats = 20;

    /// <summary>A map of this roster's own, NOT <see cref="SessionFixture.HomeMap"/>. 400 sessions entering
    /// and leaving the map every other fact in the "world" collection stands on is not a neutral act: each
    /// entry and each exit broadcasts to everyone already there. The roster's own allocation volume can then
    /// force a Gen2, on which <c>ArrayPool&lt;Session&gt;.Shared</c> — process-wide — trims its per-core
    /// stacks, and the next rent ANYWHERE in the process allocates a fresh buffer: that is how this roster
    /// perturbs a NEIGHBOURING allocation fact
    /// (<c>BroadcastIsolationTests.BroadcastAllocatesNothingPerPeer</c>, which broadcasts to a crowd on
    /// HomeMap) without anything here being wrong. That route is a plausible mechanism, not a measurement;
    /// the rule is what is certain, and the rule is that a roster this size stands on a map of its own.
    /// <c>EnterMap</c> and <c>LeaveMap</c> do not themselves rent from that pool — they use plain LINQ
    /// arrays (PR #260's review F2). The world builds a
    /// <c>MapState</c> for whatever id it is asked for, so an id no content file uses costs nothing and
    /// touches nobody: the same trick the profile harness uses.</summary>
    private const ushort RosterMap = 60998;

    private readonly SessionFixture _fx;

    public StatusTickAllocationTests(SessionFixture fx) => _fx = fx;

    /// <summary>(a) 400 sessions with neither a hold nor a venom: <c>TickSleep</c> + <c>TickPoison</c> over
    /// 20 beats allocate <b>0 B</b> on the calling thread.
    ///
    /// <para>Two passes over the same loop, the pattern
    /// <c>TickStepIsolationTests.TheIsolationHelperAllocatesNothingWithAStaticLambda</c> established: the
    /// first pass pays each call site's one cached delegate and whatever the JIT wants, and the second is
    /// the claim. Tiering is not a factor for the ALLOCATION number — a tier-0 and a tier-1 body allocate
    /// the same objects — so no tiering switch is needed here, unlike the timed profile.</para>
    ///
    /// <para><b>Falsification.</b> Restore one method-scoped capture: put
    /// <c>int a = _sleepFxAnim; _world.BroadcastWideArea(…, p =&gt; p.EffectOver(_char.Id, a));</c> back
    /// inline at the foot of <c>TickSleep</c> in place of the <c>BroadcastStatusFx</c> call. Run, confirm
    /// red, restore by hand. Recorded in the report: red at <b>256,000 B over 8,000 calls — 32 B a
    /// call</b>.</para></summary>
    [Fact]
    public void TheNoOpStatusSweepAllocatesNothing()
    {
        var (sessions, _) = Roster(Players, "AllocIdle");
        try
        {
            foreach (var s in sessions) { Assert.False(s.Asleep); Assert.False(s.Poisoned); }

            long first = 0, second = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int beat = 0; beat < Beats; beat++)
                    foreach (var s in sessions) { s.TickSleep(); s.TickPoison(); }
                long taken = GC.GetAllocatedBytesForCurrentThread() - before;
                if (pass == 0) first = taken; else second = taken;
            }

            // The first pass may pay each site's cached delegate once for the life of the process; it is
            // still a handful of objects across 8,000 calls, not a per-call cost.
            Assert.True(first < 4096,
                $"first pass: {first} B over {Players * Beats} calls of each method");
            Assert.Equal(0, second);
        }
        finally
        {
            foreach (var s in sessions) _fx.World.LeaveMap(s, RosterMap);
        }
    }

    /// <summary>(b) A sleeping player's redrawing beat broadcasts <c>EffectOver(id, anim)</c> to a PEER in
    /// the wide area — the frame the sleeper's own recorder cannot prove, because the point of the drowse is
    /// that everyone watching sees it.
    ///
    /// <para><b>Falsification.</b> Change <c>BroadcastStatusFx</c>'s argument — pass <c>anim + 1</c>, or
    /// <c>p.EffectOver(p.PlayerId, a)</c> instead of <c>_char.Id</c>. Run, confirm red, restore by hand.
    /// Recorded in the report: red on the anim assertion, and red on the id assertion (zero 0x29 frames over
    /// the sleeper).</para></summary>
    [Fact]
    public void ASleepingPlayersBeatDrawsTheDrowseForAPeer()
    {
        const int Anim = 2;
        var (sleeper, _) = _fx.Player("TickScopeSleeper", SessionFixture.HomeMap, x: 5, y: 10);
        var (peer, peerOut) = _fx.Player("TickScopeSleepWatcher", SessionFixture.HomeMap, x: 6, y: 10);
        try
        {
            sleeper.ReceiveSleep("sleeps", durMs: 60_000, key: "scope_doze", name: "Doze", anim: Anim, repeatFxMs: 1);
            Assert.True(sleeper.Asleep);
            peerOut.Clear();

            Assert.True(
                SpinWait.SpinUntil(() => { sleeper.TickSleep(); return EffectAnimsOver(peerOut, sleeper.PlayerId).Count > 0; }, 2000),
                "the peer was never told to draw the drowse over the sleeping player");
            Assert.Contains(Anim, EffectAnimsOver(peerOut, sleeper.PlayerId));
        }
        finally
        {
            using (var _ = sleeper.EnterState()) sleeper.WakeUp(byDamage: false);   // WakeUp writes _buffs
            _fx.World.LeaveMap(sleeper, SessionFixture.HomeMap);
            _fx.World.LeaveMap(peer, SessionFixture.HomeMap);
        }
    }

    /// <summary>(c) A venomed player's DAMAGING beat broadcasts, to a peer and in this order,
    /// <c>EffectOver(id, anim)</c> then <c>DamageOver(id, pct, HitCritByte)</c> — the two frames and the
    /// order <c>TickPoison</c> has always sent them in, with the percent read after the health came off.
    ///
    /// <para>500 max HP and a 100-point tick, so the first damaging beat leaves 400 and the bar reads
    /// <b>80</b>. That number is the arithmetic being pinned: a percent computed BEFORE the subtraction
    /// would read 100 and nothing else in the suite would notice.</para>
    ///
    /// <para><b>Falsification.</b> In <c>BroadcastPoisonBar</c>, send <c>0</c> or <c>HealBarCritByte</c> in
    /// place of <c>HitCritByte</c>, or move the <c>PlayerHpPercent()</c> read above the <c>_char.Hp -=</c>
    /// line. Run, confirm red, restore by hand. Recorded in the report: red on the crit byte, and red on
    /// percent 100 against the expected 80.</para></summary>
    [Fact]
    public void AVenomedPlayersDamagingBeatDrawsTheFlashThenTheBarForAPeer()
    {
        const int Anim = 3;
        var (victim, _, hurt) = _fx.PlayerWith(
            "TickScopeVictim", c => { c.Hp = 500; c.MaxHp = 500; }, SessionFixture.HomeMap, x: 7, y: 10);
        var (peer, peerOut) = _fx.Player("TickScopeVenomWatcher", SessionFixture.HomeMap, x: 8, y: 10);
        try
        {
            victim.ReceivePoison(
                dps: 1, durMs: 60_000, by: 0, anim: Anim, key: "scope_venom", name: "venom",
                perTick: 100, tickMinMs: 1, tickMaxMs: 1);
            Assert.True(victim.Poisoned);
            peerOut.Clear();

            Assert.True(
                SpinWait.SpinUntil(() => { victim.TickPoison(); return hurt.Hp < 500; }, 2000),
                "the first venom tick never landed");
            Assert.Equal(400u, hurt.Hp);

            // The flash first, the bar second — the order TickPoison sends them in.
            var ops = OpsOf(peerOut, ServerOp.Effect, ServerOp.Damage);
            Assert.Equal(ServerOp.Effect, ops[0]);
            Assert.Equal(ServerOp.Damage, ops[1]);

            Assert.Contains(Anim, EffectAnimsOver(peerOut, victim.PlayerId));

            var bars = DamageBarsOver(peerOut, victim.PlayerId);
            Assert.NotEmpty(bars);
            var (percent, critical) = bars[0];
            Assert.Equal(80, percent);                      // 400 of 500, read AFTER the health came off
            Assert.Equal(Content.HitCritByte, critical);
        }
        finally
        {
            using (var _ = victim.EnterState()) victim.CurePoison();   // CurePoison writes _buffs
            _fx.World.LeaveMap(victim, SessionFixture.HomeMap);
            _fx.World.LeaveMap(peer, SessionFixture.HomeMap);
        }
    }

    // ---- arrangement and readers ---------------------------------------------------------------------

    /// <summary><paramref name="count"/> plain sessions on the fixture's world, with neither status.</summary>
    private (Session[] sessions, RecordingOutbound[] outbounds) Roster(int count, string prefix)
    {
        var sessions = new Session[count];
        var outbounds = new RecordingOutbound[count];
        for (int i = 0; i < count; i++)
        {
            var (s, o) = _fx.Player($"{prefix}{i:0000}", RosterMap, x: 5, y: 10);
            sessions[i] = s;
            outbounds[i] = o;
        }
        return (sessions, outbounds);
    }

    /// <summary>The effect ids this recorder was told to draw over <paramref name="id"/>. The 0x29 body is
    /// <c>id(u32 BE) | efx(u8) | A(u16) | B(u16) | C(u16)</c> — see <c>Session.SendEffect</c> — and the wire
    /// byte carries <c>Session.EfxWireOffset</c>, which is subtracted back off here.</summary>
    private static List<int> EffectAnimsOver(RecordingOutbound outbound, uint id) =>
        outbound.BodiesOf(ServerOp.Effect)
                .Where(b => b.Length >= 5 && BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(0)) == id)
                .Select(b => b[4] - ServerConfig.Current.EfxWireOffset)
                .ToList();

    /// <summary>The over-head HP bars this recorder was told to draw over <paramref name="id"/>. The 0x13
    /// body is <c>id(u32 BE) | critical(u8) | percent(u8) | hitSound(u8)</c> — see
    /// <c>Session.SendDamage</c>.</summary>
    private static List<(int percent, byte critical)> DamageBarsOver(RecordingOutbound outbound, uint id) =>
        outbound.BodiesOf(ServerOp.Damage)
                .Where(b => b.Length >= 7 && BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(0)) == id)
                .Select(b => ((int)b[5], b[4]))
                .ToList();

    /// <summary>The opcodes this recorder saw, in order, keeping only <paramref name="wanted"/>.</summary>
    private static List<byte> OpsOf(RecordingOutbound outbound, params byte[] wanted)
    {
        var ops = new List<byte>();
        foreach (var frame in outbound.Frames)
        {
            if (!Protocol.Tk495.TkPacket.TryParse(frame, out var pkt, out _)) continue;
            if (Array.IndexOf(wanted, pkt.Opcode) >= 0) ops.Add(pkt.Opcode);
        }
        return ops;
    }
}
