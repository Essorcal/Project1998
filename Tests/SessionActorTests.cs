using System.Text.RegularExpressions;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The session actor (#29): one state monitor per player, taken at every entry into that player's state.
///
/// <para>What used to be true is worth restating, because it is what these tests are checking has stopped
/// being true. <c>_buffs</c> was a plain <c>List&lt;ActiveBuff&gt;</c> with three concurrent writers — the
/// world tick removing expired entries through <c>RegenTick</c>, the read loop adding through the spell
/// verbs, and a peer's read loop adding through the PvP paths — plus the autosave thread enumerating it
/// mid-serialize. <c>_statusFlags</c>, <c>_char.Equipment</c> and <c>_char</c> had the same shape. The
/// failures were a buff that vanished, a save that was skipped, and nothing in the log either way.</para>
/// </summary>
[Collection("world")]
public class SessionActorTests
{
    private readonly SessionFixture _fx;

    public SessionActorTests(SessionFixture fx) => _fx = fx;

    /// <summary>Long enough that nothing under test expires on its own — every disappearance is then a bug,
    /// not a timer.</summary>
    private const int Forever = 10 * 60 * 1000;

    // =====================================================================================================
    // The acceptance test the ticket asks for by name.
    // =====================================================================================================

    /// <summary>
    /// <b>"Concurrent RegenTick + buff apply cannot lose an entry."</b>
    ///
    /// <para>One thread is the world tick (<c>RegenTick</c>, whose <c>ExpireBuffs</c> pass is the tick-side
    /// WRITER of <c>_buffs</c> and whose <c>Totals()</c> is a reader of it); the other is a caster landing
    /// curses on this player. The applier alternates keepers with already-lapsed decoys, so the tick thread
    /// is genuinely REMOVING while the other adds — two writers on one plain <c>List</c>. Every keeper the
    /// applier got through must still be there at the end.</para>
    ///
    /// <para><b>Shaped so it actually fails without the monitor.</b> The first version of this test counted
    /// iterations, finished in microseconds and never overlapped: it passed against unguarded code, and what
    /// I had actually falsified was the Debug assert firing, not the race (thanks to the reviewer for
    /// catching that). This one primes the list wide so every tick walks 400 entries, and runs both threads
    /// to a WALL CLOCK so neither can finish while the other warms up. Verified by removing
    /// <c>EnterState</c> from <c>RegenTick</c>/<c>ReceiveCurse</c> AND the <c>AssertStateHeld</c> guards from
    /// the Buff helpers — i.e. genuinely pre-#29 — where it fails every run.</para>
    ///
    /// <para>The count is read back through the save path rather than a test-only accessor, so this pins the
    /// other half of the ticket too: what the autosave serializes is the list the game has.</para>
    /// </summary>
    [Fact]
    public void ConcurrentRegenTickAndBuffApplyLosesNoEntry()
    {
        var (session, _, character) = _fx.PlayerWith("ActorBuffRace", c => { c.MaxHp = 5_000; c.Hp = 100; });
        // A wide standing list: every RegenTick now walks 400 entries in ExpireBuffs and 400 again in
        // Totals(), which is what turns a theoretical window into one the scheduler actually lands in.
        for (int i = 0; i < 400; i++) session.ReceiveCurse("might", 1, Forever, $"actor_prime_{i}", "prime", "");

        long until = Environment.TickCount64 + 1_500;
        var start = new ManualResetEventSlim();
        Exception? applierFault = null, tickFault = null;
        int applied = 0, ticks = 0;

        var applier = new Thread(() =>
        {
            start.Wait();
            try
            {
                for (int i = 0; Environment.TickCount64 < until; i++)
                {
                    // A decoy that lapses immediately, so the tick thread has real removals to do rather
                    // than just reads.
                    session.ReceiveCurse("", 0, 1, $"actor_drop_{i}", "decoy", "");
                    session.ReceiveCurse("might", 1, Forever, $"actor_keep_{i}", "keeper", "");
                    applied = i + 1;
                }
            }
            catch (Exception e) { applierFault = e; }
        });

        var tick = new Thread(() =>
        {
            start.Wait();
            try
            {
                // ms: 0 keeps the 25s regen accumulator from ever firing, so this is purely the buff-expiry
                // pass and the effective-stat walk — the parts of RegenTick that touch the list.
                while (Environment.TickCount64 < until) { session.RegenTick(0); ticks++; }
            }
            catch (Exception e) { tickFault = e; }
        });

        applier.Start();
        tick.Start();
        start.Set();
        Assert.True(applier.Join(TimeSpan.FromSeconds(60)), "the buff applier never finished");
        Assert.True(tick.Join(TimeSpan.FromSeconds(60)), "the regen ticker never finished");

        Assert.Null(applierFault);
        Assert.Null(tickFault);
        Assert.True(applied > 100 && ticks > 100, $"the race never got going: {applied} apply round(s), {ticks} tick(s)");

        // Read the list back the way persistence does.
        session.WithState(session.MarkDirty);
        session.FlushNow();

        var saved = character.Effects.Buffs.Select(b => b.Key).ToHashSet();
        var missing = Enumerable.Range(0, applied).Select(i => $"actor_keep_{i}").Where(k => !saved.Contains(k)).ToList();
        Assert.True(missing.Count == 0,
            $"{missing.Count} of {applied} buff(s) lost to the race, first few: {string.Join(", ", missing.Take(5))}");
        for (int i = 0; i < 400; i++) Assert.Contains($"actor_prime_{i}", saved);
    }

    // =====================================================================================================
    // The rest of the acceptance list.
    // =====================================================================================================

    /// <summary>
    /// The autosave sweep against live mutation. <c>CaptureTimedEffects</c> ENUMERATES <c>_buffs</c> and
    /// <c>_statusFlags</c> to build the row, which is the third of the ticket's three concurrent readers,
    /// and it used to do that on the autosave thread while the read loop added to both. Now the whole
    /// capture — timers plus JSON — happens under the monitor and only the SQLite write is outside it, so
    /// the sweep cannot throw on a collection mutated mid-serialize and cannot store a half-applied
    /// character.
    ///
    /// <para>Primed with a large effect list on purpose: the failure is a window, and a 400-entry
    /// enumeration is a wide one. Both threads run to a wall clock rather than a count, so neither can
    /// finish while the other is still warming up.</para>
    /// </summary>
    [Fact]
    public void ConcurrentAutosaveAndMutationSerializesAConsistentSnapshot()
    {
        var (session, _, character) = _fx.PlayerWith("ActorSaveRace", c =>
        {
            c.MaxHp = 5_000;
            c.Hp = 5_000;
            c.Equipment.Add(new InvItem(1, 1, 1, 100));
        });
        for (int i = 0; i < 400; i++) session.ReceiveCurse("armor", 1, Forever, $"actor_prime_{i}", "prime", "");

        long until = Environment.TickCount64 + 1_500;
        var start = new ManualResetEventSlim();
        Exception? saverFault = null, mutatorFault = null;
        int saves = 0, mutations = 0;

        var saver = new Thread(() =>
        {
            start.Wait();
            try
            {
                while (Environment.TickCount64 < until)
                {
                    session.WithState(session.MarkDirty);
                    session.FlushNow();
                    saves++;
                }
            }
            catch (Exception e) { saverFault = e; }
        });

        var mutator = new Thread(() =>
        {
            start.Wait();
            try
            {
                for (int i = 0; Environment.TickCount64 < until; i++)
                {
                    session.ReceiveCurse("armor", i % 7, Forever, $"actor_churn_{i % 64}", "churn", "");
                    session.ItemSetStatus($"actor_ward_{i % 32}", Forever);
                    mutations++;
                }
            }
            catch (Exception e) { mutatorFault = e; }
        });

        saver.Start();
        mutator.Start();
        start.Set();
        Assert.True(saver.Join(TimeSpan.FromSeconds(60)), "the autosaver never finished");
        Assert.True(mutator.Join(TimeSpan.FromSeconds(60)), "the mutator never finished");

        Assert.Null(saverFault);
        Assert.Null(mutatorFault);
        Assert.True(saves > 10 && mutations > 100, $"the race never got going: {saves} save(s), {mutations} mutation(s)");

        // What was written last has to be a whole character, primed effects and all.
        var load = _fx.Store.Load("ActorSaveRace");
        Assert.Equal(CharacterLoadStatus.Ok, load.Status);
        var reloaded = Assert.IsType<Character>(load.Character);
        Assert.Single(reloaded.Equipment);
        Assert.Equal(character.Effects.Buffs.Count, reloaded.Effects.Buffs.Count);
        Assert.True(reloaded.Effects.Buffs.Count >= 400);
    }

#if DEBUG
    /// <summary>
    /// Acceptance box 2: "a Debug assert fires if <c>World._lock</c> is entered before the session monitor."
    ///
    /// <para>Both directions, because only the pair says anything. Session-then-world is the legal order and
    /// must stay silent; world-then-session is the inversion the whole ranking rests on not happening, and
    /// has to be loud. Debug-only by construction — the assert is <c>[Conditional("DEBUG")]</c>, which is
    /// what makes it free in a release server — so the test is too.</para>
    /// </summary>
    [Fact]
    public void EnteringTheSessionMonitorUnderTheWorldLockAsserts()
    {
        var (session, _) = _fx.Player("ActorLockOrder");

        var wrongWay = Record.Exception(() => _fx.World.UnderWorldLockForTest(() => session.WithState(() => { })));
        Assert.NotNull(wrongWay);
        Assert.Contains("lock order violated", wrongWay!.Message);

        var rightWay = Record.Exception(() => session.WithState(() => _fx.World.UnderWorldLockForTest(() => { })));
        Assert.Null(rightWay);
    }
#endif

    /// <summary>
    /// Two players reaching into each other at once — the ABBA shape that a naive per-session lock deadlocks
    /// on, and the reason session monitors are only ever taken in ascending <c>StateRank</c>. On a PvP map
    /// this is two people casting at each other in the same instant, so it is not a theoretical arrangement.
    ///
    /// <para>Also checks what the reordering owes its caller: when the inner acquisition has to drop and
    /// retake the outer one, the caller is holding it again by the time the inner body returns AND after it
    /// unwinds.</para>
    /// </summary>
    [Fact]
    public void PeerEntryPointsFromBothSidesAtOnceCannotDeadlock()
    {
        const int Rounds = 20_000;
        var (a, _) = _fx.Player("ActorPeerA");
        var (b, _) = _fx.Player("ActorPeerB");

        var start = new ManualResetEventSlim();
        Exception? faultAb = null, faultBa = null;

        Thread Nest(Session outer, Session inner, Action<Exception> onFault) => new(() =>
        {
            start.Wait();
            try
            {
                for (int i = 0; i < Rounds; i++)
                    outer.WithState(() =>
                    {
                        inner.WithState(() =>
                        {
                            Assert.True(outer.StateHeld, "the caller's monitor was not held for the nested body");
                            Assert.True(inner.StateHeld);
                        });
                        Assert.True(outer.StateHeld, "the caller's monitor was not restored after the nested body");
                    });
            }
            catch (Exception e) { onFault(e); }
        });

        var ab = Nest(a, b, e => faultAb = e);
        var ba = Nest(b, a, e => faultBa = e);
        ab.Start();
        ba.Start();
        start.Set();

        Assert.True(ab.Join(TimeSpan.FromSeconds(30)), "A->B deadlocked");
        Assert.True(ba.Join(TimeSpan.FromSeconds(30)), "B->A deadlocked");
        Assert.Null(faultAb);
        Assert.Null(faultBa);
    }

    /// <summary>
    /// The viewport lock against the session monitor — the first of the two cycles the reviewer of this
    /// change reproduced, and one this change itself created by guarding <c>Snapshot()</c>.
    ///
    /// <para>Edge one: the tick's viewport reconcile held the VIEWER's <c>_viewLock</c> across
    /// <c>ShowPlayer</c> → the SUBJECT's <c>Snapshot()</c> → the subject's monitor. Edge two, which was
    /// always there: a morph revert, a stealth revert or a death runs under the caster's own monitor and
    /// broadcasts <c>DespawnEntity</c>, which takes every recipient's <c>_viewLock</c>. Viewer B's viewport
    /// lock waiting on subject A's monitor, against A's monitor waiting on B's viewport lock. Both sides are
    /// constant traffic on a populated map.</para>
    ///
    /// <para>These are the production calls, not stand-ins. Before the fix both threads hang.</para>
    /// </summary>
    [Fact]
    public void ViewportReconcileAgainstADespawnBroadcastCannotDeadlock()
    {
        const int Rounds = 20_000;
        var (a, _) = _fx.Player("ActorViewA", x: 5, y: 10);
        var (b, _) = _fx.Player("ActorViewB", x: 6, y: 10);

        var start = new ManualResetEventSlim();
        Exception? despawnerFault = null, reconcilerFault = null;

        // A's read loop reverting a morph: under A's monitor, broadcast a despawn into B's viewport sets.
        var despawner = new Thread(() =>
        {
            start.Wait();
            try { for (int i = 0; i < Rounds; i++) a.WithState(() => b.DespawnEntity(a.PlayerId)); }
            catch (Exception e) { despawnerFault = e; }
        });

        // The tick reconciling B's viewport, which draws A and so snapshots A.
        var reconciler = new Thread(() =>
        {
            start.Wait();
            try { for (int i = 0; i < Rounds; i++) b.SyncPeer(a); }
            catch (Exception e) { reconcilerFault = e; }
        });

        despawner.Start();
        reconciler.Start();
        start.Set();

        Assert.True(despawner.Join(TimeSpan.FromSeconds(30)), "the despawn broadcast deadlocked against the viewport reconcile");
        Assert.True(reconciler.Join(TimeSpan.FromSeconds(30)), "the viewport reconcile deadlocked against the despawn broadcast");
        Assert.Null(despawnerFault);
        Assert.Null(reconcilerFault);
    }

#if DEBUG
    /// <summary>The rule behind the test above, asserted rather than merely arranged: nothing holding a
    /// viewport lock may enter a session monitor. <c>Monitor.IsEntered</c> cannot answer this — the offending
    /// lock belongs to a DIFFERENT session — so it is a counted depth, and this is what proves the counter is
    /// wired to the assert.</summary>
    [Fact]
    public void EnteringTheSessionMonitorUnderAViewportLockAsserts()
    {
        var (a, _) = _fx.Player("ActorViewOrderA");
        var (b, _) = _fx.Player("ActorViewOrderB");

        var wrongWay = Record.Exception(() => b.UnderViewLockForTest(() => a.WithState(() => { })));
        Assert.NotNull(wrongWay);
        Assert.Contains("viewport lock is held while entering a session monitor", wrongWay!.Message);

        var rightWay = Record.Exception(() => a.WithState(() => b.UnderViewLockForTest(() => { })));
        Assert.Null(rightWay);
    }
#endif

    /// <summary>
    /// The Lua gate against the session monitor — the second reproduced cycle, and the one
    /// <c>StateRank</c> cannot break, because a script host lock sits outside the session ordering entirely.
    ///
    /// <para>B's read loop holds B's monitor (Handle), enters Lua, and a verb reaches a peer
    /// (<c>healTarget</c> → <c>ReceiveHeal</c>, <c>applyCurse</c> → <c>ReceiveCurse</c>, <c>amplify</c> →
    /// <c>ArmDamageAmp</c>) so it wants A's monitor. A's read loop holds A's monitor and wants the gate for
    /// any Lua-backed cast of its own. A poet healing a party member while the other player casts anything.
    /// </para>
    ///
    /// <para>The gate's fast path (<c>TryEnter</c>, no wait) is what keeps an ordinary cast atomic; the slow
    /// path drops this thread's session monitors before waiting, which is the invariant the whole thing
    /// rests on — a thread waiting for the gate holds no monitor, so the gate holder can always finish.</para>
    /// </summary>
    [Fact]
    public void LuaGateAgainstAPeerMonitorCannotDeadlock()
    {
        const int Rounds = 20_000;
        var (a, _) = _fx.Player("ActorLuaA");
        var (b, _) = _fx.Player("ActorLuaB");

        var start = new ManualResetEventSlim();
        Exception? verbFault = null, castFault = null;

        // B: monitor, then the gate, then a peer's monitor from inside the verb.
        var verb = new Thread(() =>
        {
            start.Wait();
            try
            {
                for (int i = 0; i < Rounds; i++)
                    b.WithState(() =>
                    {
                        using (Session.EnterScriptGate()) a.ReceiveHeal(1);
                    });
            }
            catch (Exception e) { verbFault = e; }
        });

        // A: monitor, then the gate. No peer — this is the plain "cast anything Lua-backed" side.
        var cast = new Thread(() =>
        {
            start.Wait();
            try
            {
                for (int i = 0; i < Rounds; i++)
                    a.WithState(() => { using (Session.EnterScriptGate()) { } });
            }
            catch (Exception e) { castFault = e; }
        });

        verb.Start();
        cast.Start();
        start.Set();

        Assert.True(verb.Join(TimeSpan.FromSeconds(30)), "the peer-reaching Lua verb deadlocked");
        Assert.True(cast.Join(TimeSpan.FromSeconds(30)), "the plain Lua cast deadlocked");
        Assert.Null(verbFault);
        Assert.Null(castFault);
    }

    // =====================================================================================================
    // Acceptance box 1, structurally: nothing writes the four families without the guard.
    // =====================================================================================================

    /// <summary>
    /// "Every mutation of <c>_buffs</c>, <c>_statusFlags</c>, <c>Equipment</c>, <c>_char</c> happens under
    /// the session monitor" is a claim about roughly thirty scattered sites. Wrapping the entry points is
    /// what makes it true; this is what keeps it true — a raw write added later fails the build instead of
    /// quietly reopening the hole, the same trick <c>MobAiLockTests</c> plays for the world lock.
    ///
    /// <para>The rule: a raw write to one of these collections is legal only directly beneath an
    /// <c>AssertStateHeld</c>, which is the shape of the guarded helpers in Session.Items.cs. Everything
    /// else has to go through those helpers. <c>_char</c>'s scalars are not (and cannot usefully be) covered
    /// by a regex — its chokepoint is the runtime assert in <c>SaveChar</c>/<c>MarkDirty</c>.</para>
    /// </summary>
    [Fact]
    public void RawWritesToGuardedSessionStateGoThroughTheGuardedHelpers()
    {
        var raw = new Regex(
            @"(_buffs\.(Add|Remove|RemoveAll|RemoveAt|RemoveRange|Clear|Insert|Sort)\s*\()" +
            @"|(_statusFlags\s*\[[^\]]*\]\s*=[^=])" +
            @"|(_statusFlags\.(Clear|Remove|Add)\s*\()" +
            @"|(\.Equipment\.(Add|Remove|RemoveAll|Clear|Insert)\s*\()" +
            @"|(\.Equipment\s*=)",
            RegexOptions.Compiled);

        var violations = new List<string>();
        string serverDir = Path.Combine(RepoRoot().FullName, "Server");

        // Recursive: a Session partial or a helper moved into a subdirectory must not fall out of the scan
        // just by moving.
        foreach (string file in Directory.EnumerateFiles(serverDir, "*.cs", SearchOption.AllDirectories).Order())
        {
            bool isHelperHome = Path.GetFileName(file) == HelperHome;
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.TrimStart().StartsWith("//")) continue;
                if (!raw.IsMatch(line)) continue;
                // The exemption is deliberately narrow: the guarded helpers all live in ONE file, and the
                // guard has to be the line IMMEDIATELY above. Otherwise "type AssertStateHeld somewhere near
                // it" would be enough to wave a raw write through, which is the opposite of the point.
                if (isHelperHome && i > 0 && lines[i - 1].Trim().StartsWith("AssertStateHeld(")) continue;
                violations.Add($"{Path.GetFileName(file)}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(violations.Count == 0,
            "session state guarded by the state monitor (#29) is written raw; route it through the " +
            "BuffAdd/BuffRemoveAll/BuffRemoveAt/BuffClear, SetStatusFlag/SetStatusFlagUntil/ClearStatusFlags " +
            "or EquipAdd/EquipRemove/EquipClear helpers in Session.Items.cs:\n" + string.Join('\n', violations));
    }

    /// <summary>The one file the guarded helpers live in. Keeping the exemption file-scoped is what stops a
    /// raw write anywhere else claiming it by typing the guard's name above itself.</summary>
    private const string HelperHome = "Session.Items.cs";

    private static DirectoryInfo RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Project1998.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!;
    }
}
