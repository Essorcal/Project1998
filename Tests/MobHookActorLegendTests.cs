using System.Diagnostics;
using System.Text;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The Leviathan's "Sworn enemy" brand is never skipped because the player's own thread wrote a legend while the
/// hook was reading theirs (#345's pre-existing item 5a).
///
/// <para><b>The defect.</b> <c>game-data/mob_ai.lua</c>'s <c>leviathan.after_death</c> runs on the tick thread
/// (<c>World.FlushTick</c>), inside the Lua gate, outside <c>World._lock</c>, holding no session monitor. It asks
/// <c>actorHasLegend</c> twice and then <c>actorAddLegend</c>, whose first step is the same question. Each walked the
/// killer's legend list (<c>Session.HasLegend</c>) with no monitor, while the killer's own thread edits that list
/// under theirs. A walk that met an edit threw "Collection was modified"; <c>MobScript.Fire</c>'s catch logged it and
/// the rest of the hook was skipped: no brand, no line. Each read now runs inside the killer's monitor
/// (<c>Server/MobScript.cs</c>).</para>
///
/// <para><b>How the edit is forced into the walk.</b> The killer's <c>Character.Legends</c> is a
/// <see cref="PausingLegends"/>: the same <c>List&lt;Legend&gt;</c>, whose interface walk (the one LINQ's
/// <c>Any</c> takes) can be held after its first element, on the walk the case names. While it is held a second
/// thread, standing in for the killer's own read loop, adds a legend the way their handlers do
/// (<c>Session.AddLegend</c>, under their monitor); the walk goes on once that write has finished or the writer has
/// sat blocked for 100 ms. With the monitor held by the walk the writer can only block, and the walk finishes on the
/// list it started. Without it the write lands and the walk's next step is the list's own version check.</para>
///
/// <para>Collection "world", on its shared World: nothing here is process-wide but the content, and the killer is a
/// fresh session on a content-free map no other class uses (61358; 60073-60079 are TickPhaseGuardTests' per-phase
/// maps, computed as a base plus the case, and a killer left standing there breaks its case).</para>
/// </summary>
[Collection("world")]
public sealed class MobHookActorLegendTests
{
    private const ushort BrandMap = 61358;
    private const string Leviathan = "leviathan";
    private const string Enemy = "leviathan_sworn_enemy";
    private const string Other = "brand_race_other";
    private const string Line = "You promised to not attack us! You are now a sworn enemy of the Leviathan.";

    private readonly SessionFixture _fx;
    private static int _serial;

    public MobHookActorLegendTests(SessionFixture fx) => _fx = fx;

    /// <summary>
    /// <b>A legend write by the killer's own thread, landing in the middle of one of the hook's three walks, does not
    /// cost the brand.</b> The killer has the Leviathan quest open (stage 1) and neither legend the hook skips for.
    /// Walk 1 is <c>actorHasLegend("leviathan_sworn_enemy")</c>, walk 2 <c>actorHasLegend("leviathan_freed")</c>,
    /// walk 3 the check inside <c>actorAddLegend</c>. Each case holds one of them while the write is tried. Afterwards
    /// the killer carries the brand and the other legend, was told the line, and the write did not land mid-walk.
    /// <para>Red on 6de1d24 in Debug and Release, all three cases: "Expected (True, True, True) / Actual (False,
    /// True, False)": the write landed mid-walk, the walk threw, and the hook was skipped: no brand and no line. Red
    /// the same way, all three, with a plain <c>lock</c> on the session object in place of the monitor, or no lock;
    /// with only <c>actorAddLegend</c>'s check left bare, case 3 alone; with only <c>actorHasLegend</c> bare, cases 1
    /// and 2.</para>
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ALegendWriteDuringTheLeviathanHookDoesNotCostTheBrand(int walk)
    {
        Assert.True(MobScript.Has(Leviathan, MobScript.AfterDeath), "mob_ai.lua no longer brands on the Leviathan's death");
        var legends = new PausingLegends();
        var (killer, outbound, _) = _fx.PlayerWith($"BrandRaceKiller{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.MapXs = 20; ch.MapYs = 20;
            legends.AddRange(ch.Legends);
            ch.Legends = legends;
        }, BrandMap, 5, 5);
        killer.Quest(QuestState.Registry).Set(Leviathan, 1);   // the quest is open: the brand applies
        outbound.Clear();

        var mob = new Mob(_fx.World.AllocateMobId(), 1, 6, 5, "Leviathan", 100);   // the hook only needs a context
        var ctx = new MobContext(_fx.World, BrandMap, mob, killer);

        Exception? writerFault = null;
        var writer = new Thread(() =>
        {
            try
            {
                legends.WalkHeld.Wait();
                legends.WriterGoing.Set();
                killer.AddLegend("A legend of the killer's own", Other, 1, 1);   // their own thread's write, under their monitor
                legends.Written.Set();
            }
            catch (Exception e) { writerFault = e; }
        }) { IsBackground = true, Name = "killer's own thread" };
        writer.Start();
        legends.HoldWalk(walk, writer);

        try
        {
            MobScript.Fire(Leviathan, MobScript.AfterDeath, ctx);   // the tick's flush: outside World._lock, no monitor held
        }
        finally
        {
            legends.WalkHeld.Set();   // a hook that never reached the walk must not leave the writer waiting
        }
        Assert.True(writer.Join(TimeSpan.FromSeconds(30)), "the killer's own write never finished");
        Assert.Null(writerFault);

        Assert.Equal(walk, legends.HeldWalk);
        // The outcome first, so a red run shows what the player lost: (brand, the other legend, the line).
        var (enemy, other) = killer.WithState(() => (killer.HasLegend(Enemy), killer.HasLegend(Other)));
        Assert.Equal((true, true, true), (enemy, other, Told(outbound, Line)));
        Assert.False(legends.WrittenDuringWalk, $"the killer's write landed in the middle of walk {walk}");
    }

    /// <summary>Whether any frame sent to the player carries <paramref name="line"/> (the hook's
    /// <c>actorTell</c> goes out as <c>Session.SendMessage</c>).</summary>
    private static bool Told(RecordingOutbound o, string line)
    {
        byte[] want = Encoding.ASCII.GetBytes(line);
        foreach (var frame in o.Frames)
        {
            if (!TkPacket.TryParse(frame, out var pkt, out _)) continue;
            if (TkCrypt.Crypt(pkt.Body, pkt.Increment, TkCrypt.LoginKey).AsSpan().IndexOf(want) >= 0) return true;
        }
        return false;
    }

    /// <summary>
    /// A character's legend list whose interface walk can be held after its first element, once, on the
    /// <see cref="HoldWalk"/>-th walk after it is armed. The walk is <c>List&lt;Legend&gt;</c>'s own enumerator, so
    /// an edit of the list while it is held makes its next step throw exactly as it would in production. Everything
    /// else (<c>Add</c>, <c>RemoveAll</c>, the indexer, the serializer's reads) is the list unchanged.
    /// </summary>
    private sealed class PausingLegends : List<Legend>, IEnumerable<Legend>
    {
        public readonly ManualResetEventSlim WalkHeld = new(), WriterGoing = new(), Written = new();
        public int HeldWalk;
        public bool WrittenDuringWalk;
        private int _armedAt, _walks;
        private Thread? _writer;

        /// <summary>Hold walk number <paramref name="walk"/> from now on, until <paramref name="writer"/> has written
        /// or sat blocked for 100 ms.</summary>
        public void HoldWalk(int walk, Thread writer)
        {
            _writer = writer;
            Volatile.Write(ref _walks, 0);
            Volatile.Write(ref _armedAt, walk);
        }

        IEnumerator<Legend> IEnumerable<Legend>.GetEnumerator() => Walk();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => Walk();

        private IEnumerator<Legend> Walk()
        {
            bool hold = Volatile.Read(ref _armedAt) > 0 && Interlocked.Increment(ref _walks) == _armedAt;
            if (hold) { HeldWalk = _armedAt; Volatile.Write(ref _armedAt, 0); }
            var inner = GetEnumerator();   // List<Legend>.Enumerator: the one that checks the list's version
            bool first = true;
            while (inner.MoveNext())
            {
                yield return inner.Current;
                if (first && hold) Hold();
                first = false;
            }
        }

        /// <summary>Let the writer go, then wait until it has written, or has sat blocked (a monitor, WaitSleepJoin)
        /// for 100 ms, or 10 s have passed. Runs on the hook's thread, in the middle of the walk.</summary>
        private void Hold()
        {
            WalkHeld.Set();
            WriterGoing.Wait(TimeSpan.FromSeconds(10));
            var clock = Stopwatch.StartNew();
            long blockedSince = -1;
            while (clock.ElapsedMilliseconds < 10_000 && !Written.IsSet)
            {
                if ((_writer!.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0) blockedSince = -1;
                else if (blockedSince < 0) blockedSince = clock.ElapsedMilliseconds;
                else if (clock.ElapsedMilliseconds - blockedSince >= 100) break;
                Thread.Sleep(1);
            }
            WrittenDuringWalk = Written.IsSet;
        }
    }
}
