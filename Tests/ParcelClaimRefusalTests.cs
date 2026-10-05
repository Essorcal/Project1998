using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The messenger's "Receive Parcel" loop (<c>Session.ParcelReceiveFlow</c>) when a claim does not commit (wave 2
/// queue row 16, the persist-gate report's follow-up 1; Caleb, 2026-09-29, "4 yes").
///
/// <para><b>Before.</b> The loop listed the queue again whenever a claim came back without a parcel, and that
/// happens in two different ways. The claim step ran and <c>Parcel.ClaimIn</c> found the parcel already gone:
/// another path took it, the next listing does not show it, and listing again is right. Or the claim never found
/// anything out: the store refused or failed before the step, or the step itself threw. Listing again then finds
/// the same parcel and claims it again at once, for as long as the store keeps failing, inside the player's state
/// monitor: at least 149,521 claims in 10 s in every case below and every run on the old loop (see the
/// theory).</para>
///
/// <para><b>Now.</b> Only a claim step that ran and found its parcel gone lists again. Every other claim that does
/// not commit takes the failure branch once: the "try me again" line, the Warn line, the parcel kept, the bag put
/// back.</para>
///
/// <para><b>Counting claims.</b> Each claim takes one write number (<c>CaptureAndWriteWith</c>'s
/// <c>++_saveSeq</c>) before it reaches the store. Nothing else in these scenes takes one (the world is never
/// started, so no autosave sweep runs), so the numbers taken are the claims made.</para>
/// </summary>
[Collection("world")]
public sealed class ParcelClaimRefusalTests
{
    private const string Sender = "RefusalSender";

    /// <summary>The failure branch's line, as the server writes it. The box is ASCII on the wire, so the dash goes
    /// out as '?'; <see cref="Shown"/> encodes this the same way.</summary>
    private const string TryAgain = "I couldn't hand that over just now — try me again in a moment.";

    /// <summary>How long a claim gets to come back. Once the loop takes the failure branch it comes back in
    /// milliseconds: one claim, then the line's await. The longest one claim can take is a busy database's wait for
    /// the write lock (<c>Db.BusyTimeoutMs</c>, 5 s), so this is twice that. A loop that spins never comes
    /// back.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static readonly FieldInfo SaveSeqField =
        typeof(Session).GetField("_saveSeq", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo WriteGateField =
        typeof(Session).GetField("_writeGate", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly SessionFixture _fx;

    public ParcelClaimRefusalTests(SessionFixture fx) => _fx = fx;

    /// <summary>Ways a claim fails without its claim step finding the parcel gone.</summary>
    public enum Refusal
    {
        /// <summary>The store refuses before the claim step: the player's row is marked unreadable, so
        /// <c>CharacterStore.SaveWith</c>'s overwrite check rolls back and returns false.</summary>
        UnreadableRow,

        /// <summary>The store fails before the claim step: its database has no <c>characters</c> table, so the
        /// overwrite check throws and <c>SaveWith</c> returns false from its catch. A busy database reaches the
        /// same catch when its write lock times out.</summary>
        StoreFailsFirst,

        /// <summary>The claim step runs and throws: its database has no <c>parcels</c> table, so
        /// <c>Parcel.ClaimIn</c> never says whether the parcel is gone.</summary>
        ClaimStepFails,
    }

    /// <summary>A claim that fails without its claim step finding the parcel gone is made once. The player is told
    /// to try again, the Warn line is logged once they click past that box and the flow ends, the parcel stays
    /// queued and the purse is as it was. The claim runs on a thread of its own with <see cref="Bound"/> to come
    /// back, so a loop that spins fails this fact rather than hanging the run.
    ///
    /// <para>Falsified (see the w2-parcel-refusal report). On master's loop: red in all three cases, "the claim
    /// never came back", with 149,521 to 446,438 claims in 10 s over three runs each in Debug and Release (the
    /// count follows the machine's load). With the flag set on entering the claim step instead of on its null
    /// answer: red in the ClaimStepFails case only.</para></summary>
    [Theory]
    [InlineData(Refusal.UnreadableRow)]
    [InlineData(Refusal.StoreFailsFirst)]
    [InlineData(Refusal.ClaimStepFails)]
    public void ARefusedOrFailedClaimTakesTheFailureBranchOnce(Refusal refusal)
    {
        string name = $"Refusal{refusal}";
        using var scene = new Scene(_fx, name, ownDatabase: refusal != Refusal.UnreadableRow);
        int position = scene.SendGold(300);
        scene.Refuse(refusal);

        using (var sink = LogLineSink.Acquire())
        {
            var run = Claim(scene);
            Assert.True(run.CameBack,
                $"the claim never came back: {run.Claims:N0} claims in {run.Elapsed.TotalSeconds:0.0} s");
            Assert.Equal(1L, run.Claims);
            Assert.Equal(1, Shown(scene.Outbound, TryAgain));

            // The branch writes its Warn line after the player clicks past the box (the line's await), then ends.
            scene.Session.Receive(DialogNextFrame());
            var flow = run.Flow;
            Assert.NotNull(flow);
            Assert.True(SpinWait.SpinUntil(() => flow.IsCompleted, TimeSpan.FromSeconds(30)), "the flow never ended");
            Assert.True(flow.IsCompletedSuccessfully, flow.Exception?.ToString());
            var failed = Assert.Single(sink.Lines,
                l => l.Line.Contains($"parcel claim FAILED for '{name}' pos={position}", StringComparison.Ordinal));
            Assert.Equal(LogLevel.Warn, failed.Level);
        }

        Assert.Equal(0, Shown(scene.Outbound, $"You receive 300 gold from {Sender}."));
        var kept = Assert.Single(Parcel.ListFor(name));
        Assert.Equal((position, -1, 300), (kept.Position, kept.ItemId, kept.Amount));
        Assert.Equal(0u, scene.Character.Coins);
    }

    /// <summary>The path that still lists again: the claim step runs and finds its parcel already gone, because
    /// another path took it between the listing and the claim. The loop lists again and hands over the next
    /// parcel, and the player is never told to try again.
    ///
    /// <para>Forced, not hoped for: this thread holds the session's write gate, so the claim parks there after it
    /// has listed both parcels and taken its number. The first parcel then leaves the queue, and the gate is
    /// released. The claim waits holding the session's monitor, and this thread takes only the gate and the
    /// database meanwhile, so the two cannot deadlock.</para>
    ///
    /// <para>Green on master's loop too: the change leaves this path as it was. Falsified (see the
    /// w2-parcel-refusal report) by deleting the re-list: red, one claim where two were expected.</para></summary>
    [Fact]
    public void AClaimThatFindsItsParcelGoneHandsOverTheNextOne()
    {
        const string name = "RefusalGoneParcel";
        using var scene = new Scene(_fx, name);
        int first = scene.SendGold(100);
        scene.SendGold(200);

        object gate = WriteGateField.GetValue(scene.Session)!;
        long before = SaveSeq(scene.Session);
        ClaimThread claim;
        Monitor.Enter(gate);
        try
        {
            claim = new ClaimThread(scene);
            Assert.True(SpinWait.SpinUntil(() => SaveSeq(scene.Session) > before, TimeSpan.FromSeconds(30)),
                        "the claim never reached the write gate");
            scene.Take(first);   // another path claims it while this claim waits at the gate
        }
        finally
        {
            Monitor.Exit(gate);
        }
        Assert.True(claim.Join(TimeSpan.FromSeconds(30)), "the claim never came back");
        claim.AssertClean();

        Assert.Equal(2L, SaveSeq(scene.Session) - before);   // the claim that found its parcel gone, then the next
        Assert.Empty(Parcel.ListFor(name));
        Assert.Equal(200u, scene.Character.Coins);
        Assert.Equal(200u, scene.LoadOk().Coins);
        Assert.Equal(1, Shown(scene.Outbound, $"You receive 200 gold from {Sender}."));
        Assert.Equal(0, Shown(scene.Outbound, TryAgain));
    }

    /// <summary>An item parcel still lands in the bag and in the row, with the line that says so. The gold and the
    /// pack-full deliveries are pinned in <see cref="ReplacedSessionClaimTests"/>. Falsified (see the
    /// w2-parcel-refusal report) by skipping the give, so the item takes the pack-full drop: red, 0 in the bag
    /// where 3 were expected.</summary>
    [Fact]
    public void AnItemParcelStillLandsInTheBag()
    {
        const string name = "RefusalItemParcel";
        using var scene = new Scene(_fx, name);
        var fur = Content.ItemByKey("fox_fur")!;
        Parcel.Send(name, Sender, fur.Id, 3, 0, "", 1, 1);

        scene.Session.WithState(() => { _ = scene.Session.ParcelReceiveFlow(scene.Npc); });   // the menu pick's handler

        Assert.Empty(Parcel.ListFor(name));
        Assert.Equal(3, Count(scene.Character, fur.Id));
        Assert.Equal(3, Count(scene.LoadOk(), fur.Id));
        Assert.Equal(1, Shown(scene.Outbound, $"You receive a parcel from {Sender}: {fur.Name} x3."));
    }

    // ---- running a claim -----------------------------------------------------------------------------------

    private readonly record struct ClaimRun(bool CameBack, long Claims, TimeSpan Elapsed, Task? Flow);

    /// <summary>One claim, given <see cref="Bound"/>: whether it came back, and how many claims it made in that
    /// time. A loop that does not come back is spinning. The fact then empties the queue, the one thing that stops
    /// the old loop, so a red run still ends.</summary>
    private static ClaimRun Claim(Scene scene)
    {
        long before = SaveSeq(scene.Session);
        var clock = Stopwatch.StartNew();
        var claim = new ClaimThread(scene);
        bool cameBack = claim.Join(Bound);
        long claims = SaveSeq(scene.Session) - before;
        var elapsed = clock.Elapsed;
        if (!cameBack)
        {
            scene.EmptyTheQueue();
            Assert.True(claim.Join(TimeSpan.FromSeconds(30)), "the claim kept spinning after its parcel left the queue");
        }
        claim.AssertClean();
        return new ClaimRun(cameBack, claims, elapsed, claim.Flow);
    }

    /// <summary>The messenger's "Receive Parcel" pick, run the way its handler runs it (inside the session's
    /// monitor), on a thread of its own so a claim that spins cannot hang the run.</summary>
    private sealed class ClaimThread
    {
        private readonly Thread _thread;
        private Task? _flow;
        private Exception? _error;

        public ClaimThread(Scene scene)
        {
            _thread = new Thread(() => _error = Record.Exception(() =>
                scene.Session.WithState(() => { _flow = scene.Session.ParcelReceiveFlow(scene.Npc); })))
            { IsBackground = true };
            _thread.Start();
        }

        public bool Join(TimeSpan bound) => _thread.Join(bound);

        /// <summary>The flow, waiting at its first await once the thread is back.</summary>
        public Task? Flow => _flow;

        /// <summary>Nothing threw: not the handler, and not the flow up to the await it stopped at.</summary>
        public void AssertClean()
        {
            Assert.Null(_error);
            Assert.False(_flow?.IsFaulted ?? false, _flow?.Exception?.ToString());
        }
    }

    private static long SaveSeq(Session session) => (long)SaveSeqField.GetValue(session)!;

    /// <summary>How many NPC text boxes the player was shown that say exactly <paramref name="text"/>. A box
    /// (0x30, <c>SendScriptMessageP</c>) ends with its message, after the message's u16BE length, in ASCII.</summary>
    private static int Shown(RecordingOutbound outbound, string text)
    {
        byte[] want = Encoding.ASCII.GetBytes(text);
        return outbound.BodiesOf(ServerOp.NpcDialog).Count(body =>
        {
            int at = body.Length - want.Length;
            return at >= 2 && (body[at - 2] << 8 | body[at - 1]) == want.Length && body.AsSpan(at).SequenceEqual(want);
        });
    }

    private static int Count(Character c, int itemId) => c.Inventory.Where(i => i.ItemId == itemId).Sum(i => i.Amount);

    /// <summary>The client's "next" on a text box (<c>0x3A</c> kind 1), which resumes the box's await; the same frame
    /// as <see cref="ReplacedSessionClaimTests"/>' own.</summary>
    private static byte[] DialogNextFrame()
    {
        var body = new byte[11];
        body[0] = 0x01;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    // ---- setup ---------------------------------------------------------------------------------------------

    /// <summary>One player with a stored row and a messenger to talk to. With <c>ownDatabase</c> the session's
    /// store writes to a database file of its own (<see cref="IsolatedDatabase"/>), which a fact can break without
    /// touching anyone else's; the queue the loop lists is still the process database's. Disposing leaves the map
    /// and removes this player's parcels and row.</summary>
    private sealed class Scene : IDisposable
    {
        private readonly SessionFixture _fx;
        private readonly IsolatedDatabase? _own;

        public Scene(SessionFixture fx, string name, bool ownDatabase = false)
        {
            _fx = fx;
            Name = name;
            _own = ownDatabase ? new IsolatedDatabase() : null;
            Store = _own?.Store ?? fx.Store;
            Character = new Character
            {
                SchemaVersion = Character.CurrentSchemaVersion,
                Id = fx.World.AllocatePlayerId(),
                Name = name,
                Map = SessionFixture.HomeMap,
                X = 5,
                Y = 10,
                Coins = 0,
            };
            Assert.True(Store.SaveMany(new[] { Character }));
            Outbound = new RecordingOutbound($"recorder:{name}");
            Session = new Session(Outbound, 2005, Store, fx.World, Character);
            fx.World.EnterMap(Session, SessionFixture.HomeMap);
            Outbound.Clear();
            Npc = new Mob(fx.World.AllocateMobId(), 1, 5, 5, "RefusalMessenger", 1);
        }

        public string Name { get; }
        public CharacterStore Store { get; }
        public Character Character { get; }
        public RecordingOutbound Outbound { get; }
        public Session Session { get; }
        public Mob Npc { get; }

        public int SendGold(int amount) => Parcel.Send(Name, Sender, -1, amount, 0, "", 1, 1).Position;

        /// <summary>Make the store fail this player's claims in the way <paramref name="refusal"/> names.</summary>
        public void Refuse(Refusal refusal)
        {
            if (refusal == Refusal.UnreadableRow)
            {
                using var cn = Db.Open();
                Assert.Equal(1, Exec(cn, "UPDATE characters SET unreadable_since=1 WHERE username=$u;"));
                return;
            }
            using var own = _own!.Open();
            Exec(own, refusal == Refusal.StoreFailsFirst ? "DROP TABLE characters;" : "DROP TABLE parcels;");
        }

        /// <summary>Another path claims the parcel at <paramref name="position"/>.</summary>
        public void Take(int position)
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "DELETE FROM parcels WHERE recipient=$r COLLATE NOCASE AND position=$p;";
            cmd.Parameters.AddWithValue("$r", Name);
            cmd.Parameters.AddWithValue("$p", position);
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }

        /// <summary>Take every parcel this player has out of the queue. A loop that spins claims until the queue
        /// is empty, so this is also what ends a red run. Retried while a spinning claim holds the write
        /// lock.</summary>
        public void EmptyTheQueue()
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    using var cn = Db.Open();
                    using var cmd = cn.CreateCommand();
                    cmd.CommandText = "DELETE FROM parcels WHERE recipient=$r COLLATE NOCASE;";
                    cmd.Parameters.AddWithValue("$r", Name);
                    cmd.ExecuteNonQuery();
                    return;
                }
                catch (SqliteException) when (clock.Elapsed < TimeSpan.FromSeconds(60)) { }
            }
        }

        public Character LoadOk()
        {
            var load = Store.Load(Name);
            Assert.Equal(CharacterLoadStatus.Ok, load.Status);
            return Assert.IsType<Character>(load.Character);
        }

        public void Dispose()
        {
            _fx.World.LeaveMap(Session, SessionFixture.HomeMap);
            EmptyTheQueue();
            using (var cn = Db.Open()) Exec(cn, "DELETE FROM characters WHERE username=$u;");
            _own?.Dispose();
        }

        private int Exec(SqliteConnection cn, string sql)
        {
            using var cmd = cn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$u", CharacterStore.Key(Name));
            return cmd.ExecuteNonQuery();
        }
    }
}
