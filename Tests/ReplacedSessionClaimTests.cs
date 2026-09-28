using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The parcel and mail claims (<c>CharacterStore.SaveWith</c>: the queue row and the character row in one
/// transaction) under the same write rules as every other single-session write (#298 review, pre-existing 4;
/// the #168 decision sheet's "Not checked" list).
///
/// <para><b>The routes.</b> A claim runs only inside a packet's handler: the messenger's "Receive Parcel" (its
/// menu pick and its "Collect another?" loop are NPC dialog continuations, resumed inline by the <c>0x3A</c>
/// reply) and reading a letter (<c>0x3B</c>). <c>Dispatch</c> refuses every packet once <c>_closed</c> is set,
/// and <c>KickForReplacement</c> sets it (its <c>CloseConnection</c>) under the same monitor the handler runs
/// in, so a reply that arrives after the kick never reaches the claim. The first two facts try both routes on a
/// kicked session and are refused; their live halves are the control that the same frames do reach the
/// claim.</para>
///
/// <para><b>The rules</b> (<c>Session.CaptureAndWriteWith</c>, next to <c>CaptureAndWrite</c>): a replaced
/// session's claim commits nothing; the claim takes its sequence number under the monitor; and the transaction
/// runs under <c>_writeGate</c> and stamps <c>_writtenSeq</c>, so a capture taken before the claim (the autosave
/// sweep's, here) is dropped at the gate instead of landing over the claim. The last fact pins what did not
/// change: the claim and the character row commit together or not at all.</para>
///
/// <para>Each fact runs once through each call site, the parcel and the mail.</para>
/// </summary>
[Collection("world")]
public sealed class ReplacedSessionClaimTests
{
    private static readonly MethodInfo ReadMailMethod =
        typeof(Session).GetMethod("ReadMail", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo WriteGateField =
        typeof(Session).GetField("_writeGate", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly SessionFixture _fx;

    public ReplacedSessionClaimTests(SessionFixture fx) => _fx = fx;

    // ---- the routes ---------------------------------------------------------------------------------------

    /// <summary>The NPC dialog continuation route. A player collecting two parcels claims the first, and the
    /// messenger asks "Collect another?". A newer login for the account kicks the session (the real
    /// <c>ClaimAccountSlot</c>) and loads the row. The player's "Yes" then arrives on the old connection: it is
    /// refused at the door, the dialog never resumes, the second parcel stays queued for the new session, and the
    /// row is the one the new login loaded. Live (no kick), the same "Yes" claims the second parcel.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AParcelReplyAfterTheKickIsRefusedAtTheDoor(bool kicked)
    {
        string name = kicked ? "ClaimRouteParcelKick" : "ClaimRouteParcelLive";
        using var scene = new Scene(_fx, name);
        scene.SendGold(100);
        scene.SendGold(200);

        Task? flow = null;
        scene.Old.WithState(() => flow = scene.Old.ParcelReceiveFlow(scene.Npc));   // the menu pick's handler
        Assert.Equal(100u, scene.LoadOk().Coins);
        scene.Old.Receive(DialogNextFrame());   // past "You receive ...": now "Collect another?" waits
        Assert.Single(Parcel.ListFor(name));

        if (kicked) scene.ReLogin();
        string loaded = scene.RowJson();

        scene.Old.Receive(DialogMenuFrame(1));   // "Yes"

        if (kicked)
        {
            Assert.False(flow!.IsCompleted);
            Assert.Single(Parcel.ListFor(name));
            Assert.Equal(100u, scene.Character.Coins);
            Assert.Equal(loaded, scene.RowJson());
        }
        else
        {
            Assert.Empty(Parcel.ListFor(name));
            Assert.Equal(300u, scene.LoadOk().Coins);
        }
    }

    /// <summary>The packet route: reading a letter that carries an attachment claims it. After the kick the read
    /// is refused at the door; the attachment stays unclaimed and the row stays the one the new login loaded.
    /// Live, the same read claims it into the bag and the row.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMailReadAfterTheKickIsRefusedAtTheDoor(bool kicked)
    {
        string name = kicked ? "ClaimRouteMailKick" : "ClaimRouteMailLive";
        using var scene = new Scene(_fx, name);
        int position = scene.SendFur(3);

        if (kicked) scene.ReLogin();
        string loaded = scene.RowJson();

        scene.Old.Receive(MailReadFrame(position));

        if (kicked)
        {
            Assert.False(Mail.Get(name, position)!.Claimed);
            Assert.Equal(0, Furs(scene.Character));
            Assert.Equal(loaded, scene.RowJson());
        }
        else
        {
            Assert.True(Mail.Get(name, position)!.Claimed);
            Assert.Equal(3, Furs(scene.LoadOk()));
        }
    }

    // ---- the rules ----------------------------------------------------------------------------------------

    /// <summary>A claim that reached a replaced session anyway (driven here past the door, standing for any
    /// route a later change opens) commits nothing: the parcel stays queued or the attachment unclaimed, the bag
    /// is as it was, and the row is the kick's. It runs on a thread of its own, so a claim that spun instead of
    /// refusing fails this fact rather than hanging the run.
    ///
    /// <para>Falsified (see the persist-gate report) by deleting the <c>_replaced</c> check from
    /// <c>CaptureAndWriteWith</c>: red on the row, which took the claim after the new login loaded.</para></summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AReplacedSessionsClaimCommitsNothing(bool mail)
    {
        string name = mail ? "ClaimRefusedMail" : "ClaimRefusedParcel";
        using var scene = new Scene(_fx, name);
        int position = mail ? scene.SendFur(3) : scene.SendGold(400);

        scene.ReLogin();
        string kicked = scene.RowJson();

        Task? flow = null;
        Exception? error = null;
        var claim = new Thread(() => error = Record.Exception(() => scene.Old.WithState(() =>
        {
            if (mail) ReadMailMethod.Invoke(scene.Old, new object[] { position });
            else flow = scene.Old.ParcelReceiveFlow(scene.Npc);
        }))) { IsBackground = true };
        claim.Start();
        Assert.True(claim.Join(TimeSpan.FromSeconds(30)), "the claim never returned");
        Assert.Null(error);
        Assert.False(flow?.IsFaulted ?? false, flow?.Exception?.ToString());

        Assert.Equal(kicked, scene.RowJson());
        if (mail)
        {
            Assert.False(Mail.Get(name, position)!.Claimed);
            Assert.Equal(0, Furs(scene.Character));
        }
        else
        {
            Assert.Single(Parcel.ListFor(name));
            Assert.Equal(0u, scene.Character.Coins);
        }
    }

    /// <summary>The autosave sweep has captured the session (its number is older than the claim's) but its write
    /// has not landed when the claim commits. Before, that older snapshot then landed on top of the claim: the
    /// parcel was gone from the queue and the row no longer had it, until something else wrote the row (a gold
    /// parcel marks nothing dirty, so nothing might before a crash). Now the claim stamps its number as written
    /// and the sweep's write is dropped at the gate. The row keeps the claim, and the change the sweep captured,
    /// which the claim's own snapshot carried.
    ///
    /// <para>Forced, not hoped for: this thread holds the session's write gate, so the sweep parks there after
    /// its capture (the dirty flag going down is the proof it captured). The claim then runs on this same
    /// thread, which is how it gets through a gate it already holds. That takes the gate before the monitor,
    /// which production never does; it cannot deadlock here, because the only other thread, the sweep, holds
    /// neither while it waits.</para>
    ///
    /// <para>Falsified (see the persist-gate report) by calling <c>_store.SaveWith</c> directly again, and by
    /// dropping only the <c>_writtenSeq</c> stamp: red on the row's coins or furs, which the sweep's older
    /// snapshot overwrote.</para></summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASweepCapturedBeforeAClaimCannotLandOverIt(bool mail)
    {
        string name = mail ? "ClaimStaleSweepMail" : "ClaimStaleSweepParcel";
        using var scene = new Scene(_fx, name);
        int position = mail ? scene.SendFur(3) : scene.SendGold(500);

        scene.Old.WithState(() => { scene.Character.Exp = 7; scene.Old.MarkDirty(); });
        object gate = WriteGateField.GetValue(scene.Old)!;

        Thread? sweep = null;
        bool swept = false;
        Monitor.Enter(gate);
        try
        {
            sweep = new Thread(() => swept = World.AutoSaveLoop.FlushIsolated(scene.Old, "autosave")) { IsBackground = true };
            sweep.Start();
            Assert.True(SpinWait.SpinUntil(() => scene.Old.DiagState().Contains("dirty False"), TimeSpan.FromSeconds(30)),
                        "the sweep never captured the session");

            scene.Old.WithState(() =>
            {
                if (mail) ReadMailMethod.Invoke(scene.Old, new object[] { position });
                else _ = scene.Old.ParcelReceiveFlow(scene.Npc);
            });
        }
        finally
        {
            Monitor.Exit(gate);
        }
        Assert.True(sweep!.Join(TimeSpan.FromSeconds(30)), "the sweep never finished");
        Assert.True(swept, "a dropped write is not a failed one");

        var row = scene.LoadOk();
        Assert.Equal(7u, row.Exp);
        if (mail)
        {
            Assert.True(Mail.Get(name, position)!.Claimed);
            Assert.Equal(3, Furs(row));
        }
        else
        {
            Assert.Empty(Parcel.ListFor(name));
            Assert.Equal(500u, row.Coins);
        }
    }

    /// <summary>The transaction is unchanged: a claim whose character row cannot be written (here a value the
    /// serializer rejects, the #282 NaN-karma route) commits neither half. The parcel stays queued or the
    /// attachment unclaimed, the caller puts the bag back, and the row is untouched. A failed save is not a lost
    /// one (Shared/Db.cs): with the row writable again, the same claim commits both halves.
    ///
    /// <para>Falsified (see the persist-gate report) by making <c>CaptureAndWriteWith</c> report success whatever
    /// the store returned: red on the purse or the bag, which kept a give the database never saw.</para></summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AClaimWhoseRowCannotBeWrittenCommitsNeitherHalf(bool mail)
    {
        string name = mail ? "ClaimTxMail" : "ClaimTxParcel";
        using var scene = new Scene(_fx, name);
        int position = mail ? scene.SendFur(3) : scene.SendGold(250);
        string before = scene.RowJson();

        scene.Old.WithState(() => scene.Character.Karma = double.NaN);   // Serialize throws: the store rolls back
        Claim();

        Assert.Equal(before, scene.RowJson());
        if (mail)
        {
            Assert.False(Mail.Get(name, position)!.Claimed);
            Assert.Equal(0, Furs(scene.Character));
        }
        else
        {
            Assert.Single(Parcel.ListFor(name));
            Assert.Equal(0u, scene.Character.Coins);
        }

        scene.Old.WithState(() => scene.Character.Karma = 0);
        Claim();

        if (mail)
        {
            Assert.True(Mail.Get(name, position)!.Claimed);
            Assert.Equal(3, Furs(scene.LoadOk()));
        }
        else
        {
            Assert.Empty(Parcel.ListFor(name));
            Assert.Equal(250u, scene.LoadOk().Coins);
        }

        void Claim() => scene.Old.WithState(() =>
        {
            if (mail) ReadMailMethod.Invoke(scene.Old, new object[] { position });
            else _ = scene.Old.ParcelReceiveFlow(scene.Npc);
        });
    }

    /// <summary>A claim that finds the pack full still drops the goods at the player's feet, after the commit,
    /// as its own ground item. What moved: the drop's id is allocated after the commit instead of inside the
    /// transaction, because the transaction now runs under the write gate (Locking.md row 4) and the allocation
    /// takes <c>World._lock</c> (row 3). This pins that the goods still land, once, with an id of their own. A
    /// map of its own (content-free, in the instance band) so no other fact's floor is involved.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APackFullClaimStillDropsTheGoodsAfterTheCommit(bool mail)
    {
        string name = mail ? "ClaimPackFullMail" : "ClaimPackFullParcel";
        ushort map = mail ? PackFullMailMap : PackFullParcelMap;
        var sabre = Content.ItemByKey("frost_sabre")!;
        using var scene = new Scene(_fx, name, map, c =>
            c.Inventory = Enumerable.Range(0, c.MaxInv).Select(i => new InvItem((byte)i, sabre.Id, 1)).ToList());
        int position = mail ? scene.SendFur(3) : scene.SendFurParcel(3);

        scene.Old.WithState(() =>
        {
            if (mail) ReadMailMethod.Invoke(scene.Old, new object[] { position });
            else _ = scene.Old.ParcelReceiveFlow(scene.Npc);
        });

        if (mail) Assert.True(Mail.Get(name, position)!.Claimed);
        else Assert.Empty(Parcel.ListFor(name));
        Assert.Equal(0, Furs(scene.LoadOk()));
        var dropped = Assert.Single(_fx.World.ItemsOn(map));
        Assert.Equal((Fur.Id, 3, scene.Character.X, scene.Character.Y), (dropped.ItemId, dropped.Amount, dropped.X, dropped.Y));
        Assert.NotEqual(0u, dropped.Id);
    }

    /// <summary>Content-free map ids in the instance band (59000-65000), one per pack-full fact, so the floor
    /// each one reads holds only its own drop.</summary>
    private const ushort PackFullParcelMap = 61760, PackFullMailMap = 61761;

    // ---- setup ---------------------------------------------------------------------------------------------

    /// <summary>One player with a stored row and the account's online slot, a messenger to talk to, and a newer
    /// login standing by. Disposing drops the slot, the map entry and anything left in the two queues.</summary>
    private sealed class Scene : IDisposable
    {
        private readonly SessionFixture _fx;
        private readonly string _key;
        private readonly Session _relogin;

        public Scene(SessionFixture fx, string name, ushort map = SessionFixture.HomeMap, Action<Character>? bag = null)
        {
            _fx = fx;
            Name = name;
            Map = map;
            _key = CharacterStore.Key(name);
            (Old, _, Character) = fx.PlayerWith(name, c => { c.Coins = 0; bag?.Invoke(c); }, map);
            Assert.True(fx.Store.SaveMany(new[] { Character }));
            Npc = new Mob(fx.World.AllocateMobId(), 1, 5, 5, "ClaimMessenger", 1);
            _relogin = new Session(new RecordingOutbound($"recorder:{name}:relogin"), 2005, fx.Store, fx.World);
            fx.World.Online.Register(_key, Old, out _);   // the old session's own arrival claimed the slot
        }

        public string Name { get; }
        public ushort Map { get; }
        public Session Old { get; }
        public Character Character { get; }
        public Mob Npc { get; }

        /// <summary>The same account logs in again: the arrival's real register-and-kick.</summary>
        public void ReLogin()
        {
            _relogin.WithState(() => _relogin.ClaimAccountSlot(Name));
            Assert.True(Old.IsReplaced, "the new login did not kick the old session");
        }

        public int SendGold(int amount) =>
            Parcel.Send(Name, "ClaimSender", -1, amount, 0, "", 1, 1).Position;

        public int SendFur(int amount) =>
            Mail.Send(Name, "ClaimSender", "Fur", "For you", 1, 1, Fur.Id, amount, 0).Position;

        public int SendFurParcel(int amount) =>
            Parcel.Send(Name, "ClaimSender", Fur.Id, amount, 0, "", 1, 1).Position;

        public Character LoadOk()
        {
            var load = _fx.Store.Load(Name);
            Assert.Equal(CharacterLoadStatus.Ok, load.Status);
            return Assert.IsType<Character>(load.Character);
        }

        /// <summary>The stored row as JSON: equal strings mean an equal row.</summary>
        public string RowJson() => CharacterStore.Serialize(LoadOk());

        public void Dispose()
        {
            _fx.World.Online.Unregister(_key, _relogin);
            _fx.World.Online.Unregister(_key, Old);
            _fx.World.LeaveMap(Old, Map);
            using var cn = Db.Open();
            foreach (string table in new[] { "parcels", "mail_posts" })
            {
                using var cmd = cn.CreateCommand();
                cmd.CommandText = $"DELETE FROM {table} WHERE recipient=$r COLLATE NOCASE;";
                cmd.Parameters.AddWithValue("$r", Name);
                cmd.ExecuteNonQuery();
            }
        }
    }

    private static ItemDef Fur => Content.ItemByKey("fox_fur")!;

    private static int Furs(Character c) => c.Inventory.Where(i => i.ItemId == Fur.Id).Sum(i => i.Amount);

    /// <summary>The client's reply to a prompt (<c>0x3A</c>, RTK <c>clif_parsenpcdialog</c>): <c>[0]=kind,
    /// [8]=step, [10]=menu index</c>. Kind 1 advances a text box, kind 2 picks from a menu.</summary>
    private static byte[] DialogNextFrame()
    {
        var body = new byte[11];
        body[0] = 0x01;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    private static byte[] DialogMenuFrame(byte index)
    {
        var body = new byte[11];
        body[0] = 0x02;
        body[10] = index;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>The mailbox read (<c>0x3B</c> sub-3, board 0): <c>03 board(u16BE) post(u16BE)</c>.</summary>
    private static byte[] MailReadFrame(int position) =>
        SessionFixture.Frame(0x3B, new byte[] { 3, 0, 0, (byte)(position >> 8), (byte)position });
}
