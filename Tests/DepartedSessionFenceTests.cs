using System.Diagnostics;
using System.Reflection;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// #168 item 1c: the next login for an account fences the session that tore down moments before it.
///
/// <para><b>The problem.</b> A write decided before a session's teardown can land after it: a group share
/// whose killer was already waiting on the leaver's monitor (the PR #166 review's H5), or a mob swing the tick
/// queued before the teardown. The teardown used to drop the account's online slot, so a login arriving in
/// that gap found nobody to kick and loaded the row underneath the late write; its own next write then erased
/// the share (or rolled its first moments back).</para>
///
/// <para><b>The fence.</b> The teardown parks the account's slot in a departed table (<c>OnlineRegistry.Depart</c>)
/// instead of dropping it. The next login's <c>Register</c> hands the departed session back, and the arrival
/// (<c>Session.ClaimAccountSlot</c>) runs the ordinary <c>KickForReplacement</c> on it: that enters the departed
/// session's monitor — waiting out whatever still holds it — writes the row, and latches <c>_replaced</c>. The
/// load happens after. The autosave sweep prunes the table after one interval.</para>
///
/// <para>Driven with the departed session's monitor held on a helper thread, standing in for a late payout, and
/// the arrival on a thread of its own. <c>Session.ArrivalFenceProbeForTest</c> says when the arrival has reached
/// the fence; nothing here waits on a clock.</para>
/// </summary>
[Collection("world")]
public class DepartedSessionFenceTests
{
    /// <summary>Content-free map ids; no other class stands here.</summary>
    private const ushort FenceMap = 61710, PruneMap = 61711, ReplacedMap = 61712;

    private static readonly MethodInfo TearDown =
        typeof(Session).GetMethod("TearDownWorldState", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly SessionFixture _fx;

    public DepartedSessionFenceTests(SessionFixture fx) => _fx = fx;

    /// <summary>The fence itself, and what it leaves behind.
    /// <list type="number">
    /// <item>M logs out: registered, torn down through the real <c>TearDownWorldState</c>, which writes its row
    /// and parks it in the departed table.</item>
    /// <item>A helper thread takes M's monitor and changes M's coins (dirty, not written), standing in for a late
    /// payout still running under M's monitor.</item>
    /// <item>N arrives on its own thread: <c>ClaimAccountSlot</c>, then the load. It reaches the fence and cannot
    /// finish while the helper holds M.</item>
    /// <item>The helper lets go. N's kick writes M's row with the change, and N's load carries it. The wait from
    /// the release to N's load is well inside one busy timeout (<c>Db.BusyTimeoutMs</c>).</item>
    /// <item>After the fence, M is replaced: a further change on M, flushed the way the sweep does, does not
    /// reach the row.</item>
    /// </list>
    ///
    /// <para>Falsified (see the #168 report): <c>Depart</c> back to a plain remove (no departed table) goes red
    /// at step 3 — the arrival finished while the helper still held M, and the probe never fired — and, with
    /// that assertion deleted, on the load, which carries the teardown's coins. The kick's latch deleted goes
    /// red at step 5, the row taking the later change.</para></summary>
    [Fact]
    public void AReLoginWaitsForTheDepartedSessionAndLoadsItsLastWrite()
    {
        const string name = "FenceDeparted";
        string key = CharacterStore.Key(name);
        var (departed, _, character) = _fx.PlayerWith(name, c => c.Coins = 100, FenceMap, 5, 5);
        var fresh = new Session(new RecordingOutbound($"recorder:{name}:new"), 2005, _fx.Store, _fx.World);
        var online = _fx.World.Online;

        using var held = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var fenceReached = new ManualResetEventSlim(false);
        Thread? helper = null, arrival = null;

        try
        {
            online.Register(key, departed, out var before);
            Assert.Null(before);
            departed.WithState(() => TearDown.Invoke(departed, null));
            Assert.True(online.HoldsDepartedForTest(key, departed), "the teardown did not park the slot");
            Assert.False(online.HoldsSlotForTest(key, departed));
            Assert.Equal(100u, LoadOk(name).Coins);                    // the teardown's own write

            helper = new Thread(() => departed.WithState(() =>
            {
                character.Coins = 999;                                   // the late write, still in memory
                departed.MarkDirty();
                held.Set();
                release.Wait(TimeSpan.FromSeconds(60));
            })) { IsBackground = true };
            helper.Start();
            Assert.True(held.Wait(TimeSpan.FromSeconds(30)), "the helper never took the departed session's monitor");

            Session.ArrivalFenceProbeForTest = s => { if (ReferenceEquals(s, departed)) fenceReached.Set(); };
            CharacterLoadResult? loaded = null;
            long loadedAt = 0;
            arrival = new Thread(() =>
            {
                fresh.WithState(() => fresh.ClaimAccountSlot(name));    // HandleArrival runs under its own monitor
                loaded = _fx.Store.Load(name);
                loadedAt = Stopwatch.GetTimestamp();
            }) { IsBackground = true };
            arrival.Start();

            Assert.True(SpinWait.SpinUntil(() => fenceReached.IsSet || !arrival.IsAlive, TimeSpan.FromSeconds(30)),
                        "the arrival neither reached the fence nor finished");
            Assert.True(arrival.IsAlive, "the arrival loaded while the departed session's last write was still pending");
            Assert.Null(loaded);

            long releasedAt = Stopwatch.GetTimestamp();
            release.Set();
            Assert.True(arrival.Join(TimeSpan.FromSeconds(30)), "the arrival never finished");
            var waited = Stopwatch.GetElapsedTime(releasedAt, loadedAt);
            Assert.True(waited < TimeSpan.FromMilliseconds(Db.BusyTimeoutMs),
                        $"the arrival waited {waited.TotalMilliseconds:F0} ms past the release, over one busy timeout");

            Assert.NotNull(loaded);
            Assert.Equal(CharacterLoadStatus.Ok, loaded!.Status);
            Assert.Equal(999u, Assert.IsType<Character>(loaded.Character).Coins);
            Assert.True(online.HoldsSlotForTest(key, fresh));
            Assert.False(online.HoldsDepartedForTest(key, departed));

            // After the fence: the departed session is replaced and writes nothing more.
            Assert.True(departed.IsReplaced);
            departed.WithState(() => { character.Coins = 5; departed.MarkDirty(); });
            Assert.True(World.AutoSaveLoop.FlushIsolated(departed, "autosave"));
            Assert.Equal(999u, LoadOk(name).Coins);
        }
        finally
        {
            Session.ArrivalFenceProbeForTest = null;
            release.Set();
            helper?.Join(TimeSpan.FromSeconds(30));
            arrival?.Join(TimeSpan.FromSeconds(30));
            online.Unregister(key, fresh);
            online.Unregister(key, departed);
            _fx.World.LeaveMap(departed, FenceMap);
        }
    }

    /// <summary>The prune: a departed entry survives a sweep inside its interval and is gone after the first
    /// sweep past it, after which the next login finds nobody to fence. <c>Tick(long)</c> ages the table
    /// without waiting.
    ///
    /// <para>Falsified (see the #168 report) by deleting the prune from <c>AllForSweep</c>: red on the second
    /// <c>HoldsDepartedForTest</c>.</para></summary>
    [Fact]
    public void TheSweepPrunesADepartedSessionAfterOneInterval()
    {
        const string name = "FencePrune";
        string key = CharacterStore.Key(name);
        var (departed, _) = _fx.Player(name, PruneMap, 5, 5);
        var fresh = new Session(new RecordingOutbound($"recorder:{name}:new"), 2005, _fx.Store, _fx.World);
        var online = _fx.World.Online;

        try
        {
            online.Register(key, departed, out _);
            departed.WithState(() => TearDown.Invoke(departed, null));
            Assert.True(online.HoldsDepartedForTest(key, departed));

            _fx.World.AutoSave.Tick(Environment.TickCount64);                     // inside the interval
            Assert.True(online.HoldsDepartedForTest(key, departed));

            _fx.World.AutoSave.Tick(Environment.TickCount64 + Session.AutoSaveMs);   // one interval on
            Assert.False(online.HoldsDepartedForTest(key, departed));

            online.RegisterArrival(key, fresh, out var old, out bool wasDeparted);
            Assert.Null(old);
            Assert.False(wasDeparted);
        }
        finally
        {
            online.Unregister(key, fresh);
            online.Unregister(key, departed);
            _fx.World.LeaveMap(departed, PruneMap);
        }
    }

    /// <summary>Only a session that still owns the slot parks. A session a newer login already replaced has
    /// been fenced by that login's kick; its own (later) teardown must neither park it nor take the slot from
    /// its replacement.
    ///
    /// <para>Falsified (see the #168 report) by dropping the ownership test from <c>Depart</c> (park and remove
    /// unconditionally): red on the first assertion after the teardown.</para></summary>
    [Fact]
    public void AReplacedSessionsTeardownDoesNotParkIt()
    {
        const string name = "FenceReplaced";
        string key = CharacterStore.Key(name);
        var (old, _) = _fx.Player(name, ReplacedMap, 5, 5);
        var (fresh, _) = _fx.Player(name, ReplacedMap, 6, 5);
        var online = _fx.World.Online;

        try
        {
            online.Register(key, old, out _);
            fresh.WithState(() => fresh.ClaimAccountSlot(name));   // live duplicate: kicks old
            Assert.True(old.IsReplaced);

            old.WithState(() => TearDown.Invoke(old, null));

            Assert.False(online.HoldsDepartedForTest(key, old), "a replaced session's teardown parked it");
            Assert.True(online.HoldsSlotForTest(key, fresh), "a replaced session's teardown took its replacement's slot");
        }
        finally
        {
            online.Unregister(key, fresh);
            online.Unregister(key, old);
            _fx.World.LeaveMap(old, ReplacedMap);
            _fx.World.LeaveMap(fresh, ReplacedMap);
        }
    }

    private Character LoadOk(string name)
    {
        var load = _fx.Store.Load(name);
        Assert.Equal(CharacterLoadStatus.Ok, load.Status);
        return Assert.IsType<Character>(load.Character);
    }
}
