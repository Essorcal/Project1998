using System.Text;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Tests;

/// <summary>
/// <b>The mentorship's lines to the mentor are sent inside the mentor's monitor</b> (PR #369's review, its notes). A
/// mentor's offer opens its accept/decline box on the protégé, and the protégé's answer resumes
/// <c>Session.PromptMentorship</c> on the PROTÉGÉ's thread: their <c>0x3A</c> reply completes the prompt inline, under
/// their own monitor. From there it sends the mentor one of three lines: "X regretably must decline.", "X is already
/// being mentored." or "X accepts your offer of mentorship! ...". Each is a send into another player, and each is
/// handed over inside the mentor's monitor, the shape <see cref="VowHalvesTests"/> pins for the wedding's and the
/// proposal's lines to the other player (#365, #369).
///
/// <para><b>How "inside the monitor" is observed.</b> Every player logs in through the real <c>0x10</c> arrival on a
/// <see cref="HeldOutbound"/>. <c>Session.Send</c> hands each frame to the outbound synchronously, on the sending
/// thread, so the outbound records whether that thread held the owner's monitor at that moment
/// (<c>Session.StateHeld</c>), and passes the frame on to a <see cref="RecordingOutbound"/> that the dialog helpers
/// read. Each fact asserts that every frame each player is sent from the answer on was handed over inside that
/// player's monitor, in both <c>StateRank</c> orders: from the protégé's thread the mentor's monitor is entered
/// ascending in one and descending in the other. Each fact also logs a fingerprint of each player's frames from the
/// answer on, so two builds can be compared frame for frame.</para>
///
/// <para><b>The path.</b> The mentor casts Mentor (the real <c>0x0F</c>), types the protégé's name, reads the four
/// pages and answers "Yes" to "Are you sure"; the protégé answers on a thread of their own, because a dialog resumes
/// inline on the thread that delivers the reply, as on the read loop. Collection "world", on content-free maps 62495
/// and 62496, which no other class uses and <c>game-data/Maps.csv</c> has no row for. Each fact has a database of its
/// own (<see cref="IsolatedDatabase"/>).</para>
/// </summary>
[Collection("world")]
public sealed class MentorshipLinesTests
{
    // This class first stood on the two ids just below these, which PeerSweepLockTests' Mana Steal facts (#372) took
    // meanwhile (PR #371's review, F3).
    private const ushort AnswerMap = 62495, BoundMeanwhileMap = 62496;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly SessionFixture _fx;
    private readonly ITestOutputHelper _out;
    private static int _serial;

    public MentorshipLinesTests(SessionFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    /// <summary>The protégé's two answers to the offer.</summary>
    public enum Answer
    {
        /// <summary>"No, I must decline."</summary>
        Decline,
        /// <summary>"Yes! I need guidance."</summary>
        Accept,
    }

    /// <summary>
    /// <b>The protégé's answer tells the mentor inside the mentor's monitor.</b> M offers T, who is at the 5th insight
    /// and free, a mentorship, and T answers "No, I must decline." or "Yes! I need guidance.". The decline: M is sent
    /// "T regretably must decline." once, T is sent no line, and nobody is bound. The accept: M is sent "T accepts your
    /// offer of mentorship! ..." once, T is sent "M is now your mentor." once, and T holds M's name in the mentor slot
    /// and the "being mentored" legend. Either way T's box is closed, and every frame each of the two is sent from the
    /// answer on was handed over by a thread that held that player's monitor. Both <c>StateRank</c> orders.
    /// <para>Red on 2874006, Debug and Release, all four cases: M's line is handed over by T's thread, which holds T's
    /// monitor and not M's. T's own line goes out under T's monitor either way.</para>
    /// </summary>
    [Theory]
    [InlineData(Answer.Decline, false)]
    [InlineData(Answer.Decline, true)]
    [InlineData(Answer.Accept, false)]
    [InlineData(Answer.Accept, true)]
    public void TheProtegesAnswerTellsTheMentorInsideTheirMonitor(Answer answer, bool mentorRanksFirst)
    {
        using var db = new IsolatedDatabase();
        string m = Name("M"), t = Name("T");
        Seed(db, MentorRow(m, AnswerMap, 9, 5), ProtegeRow(t, AnswerMap, 7, 5));

        Session? mHeld = null, tHeld = null;
        try
        {
            Session mS, tS;
            HeldOutbound mWire, tWire;
            if (mentorRanksFirst)
            {
                (mS, mWire) = ArriveHeld(db.Store, m);
                mHeld = mS;
                (tS, tWire) = ArriveHeld(db.Store, t);
                tHeld = tS;
            }
            else
            {
                (tS, tWire) = ArriveHeld(db.Store, t);
                tHeld = tS;
                (mS, mWire) = ArriveHeld(db.Store, m);
                mHeld = mS;
            }
            Assert.Equal(mentorRanksFirst, mS.StateRank < tS.StateRank);

            BeginOffer(mS, t);
            FinishOffer(mS, mWire.Rec, tS, tWire.Rec, m, t);
            mWire.Clear();
            tWire.Clear();

            OnOwnThread(() => tS.Receive(MenuFrame(answer == Answer.Accept ? (byte)1 : (byte)2)));
            var mLines = mWire.Lines();
            var tLines = tWire.Lines();
            Report($"{answer}, M ranks first {mentorRanksFirst}", mWire, tWire, m, t);

            if (answer == Answer.Accept)
            {
                Assert.Equal(new[] { AcceptLine(t) }, mLines.Select(l => l.Text));
                Assert.Equal(new[] { $"{m} is now your mentor." }, tLines.Select(l => l.Text));
                Assert.Equal((m, true), BondOf(tS));
            }
            else
            {
                Assert.Equal(new[] { $"{t} regretably must decline." }, mLines.Select(l => l.Text));
                Assert.Empty(tLines);
                Assert.Equal(("", false), BondOf(tS));
            }
            Assert.All(mWire.Sent(), s => Assert.True(s.Held, $"M's frame 0x{s.Op:x2} was sent by a thread that did not hold M's monitor"));
            Assert.All(tWire.Sent(), s => Assert.True(s.Held, $"T's frame 0x{s.Op:x2} was sent by a thread that did not hold T's monitor"));
            Assert.False(tS.DialogBusy, "T's box was still open");
        }
        finally
        {
            Release(m, mHeld);
            Release(t, tHeld);
        }
    }

    /// <summary>
    /// <b>An offer whose protégé was bound meanwhile tells the mentor inside the mentor's monitor.</b> M casts Mentor at
    /// T, and the cast's checks find T free. While M reads the four pages, O offers T a mentorship and T accepts O.
    /// Then M answers "Yes" to "Are you sure", T's box asks again, and T answers "Yes! I need guidance.": the re-check on
    /// landing finds T bound, so M is sent "T is already being mentored." once, T is sent no line, and T stays O's
    /// protégé. T's box is closed, and every frame M and T are sent from that answer on was handed over by a thread
    /// that held that player's monitor. Both <c>StateRank</c> orders of M and T.
    /// <para>Red on 2874006, Debug and Release, both cases: M's line is handed over by T's thread, which holds T's
    /// monitor and not M's.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnOfferWhoseProtegeWasBoundMeanwhileTellsTheMentorInsideTheirMonitor(bool mentorRanksFirst)
    {
        using var db = new IsolatedDatabase();
        string m = Name("M"), t = Name("T"), o = Name("O");
        Seed(db, MentorRow(m, BoundMeanwhileMap, 9, 5), ProtegeRow(t, BoundMeanwhileMap, 7, 5),
             MentorRow(o, BoundMeanwhileMap, 11, 5));

        Session? mHeld = null, tHeld = null, oHeld = null;
        try
        {
            Session mS, tS;
            HeldOutbound mWire, tWire;
            if (mentorRanksFirst)
            {
                (mS, mWire) = ArriveHeld(db.Store, m);
                mHeld = mS;
                (tS, tWire) = ArriveHeld(db.Store, t);
                tHeld = tS;
            }
            else
            {
                (tS, tWire) = ArriveHeld(db.Store, t);
                tHeld = tS;
                (mS, mWire) = ArriveHeld(db.Store, m);
                mHeld = mS;
            }
            var (oS, oWire) = ArriveHeld(db.Store, o);
            oHeld = oS;
            Assert.Equal(mentorRanksFirst, mS.StateRank < tS.StateRank);

            BeginOffer(mS, t);                                   // M's checks find T free; M is on the first page
            BeginOffer(oS, t);
            FinishOffer(oS, oWire.Rec, tS, tWire.Rec, o, t);
            OnOwnThread(() => tS.Receive(MenuFrame(1)));         // T accepts O
            Assert.Equal((o, true), BondOf(tS));
            Assert.True(mS.DialogBusy, "M's offer did not wait on its pages");

            FinishOffer(mS, mWire.Rec, tS, tWire.Rec, m, t);
            mWire.Clear();
            tWire.Clear();

            OnOwnThread(() => tS.Receive(MenuFrame(1)));         // "Yes! I need guidance."
            var mLines = mWire.Lines();
            var tLines = tWire.Lines();
            Report($"bound meanwhile, M ranks first {mentorRanksFirst}", mWire, tWire, m, t, o);

            Assert.Equal(new[] { $"{t} is already being mentored." }, mLines.Select(l => l.Text));
            Assert.Empty(tLines);
            Assert.Equal((o, true), BondOf(tS));
            Assert.All(mWire.Sent(), s => Assert.True(s.Held, $"M's frame 0x{s.Op:x2} was sent by a thread that did not hold M's monitor"));
            Assert.All(tWire.Sent(), s => Assert.True(s.Held, $"T's frame 0x{s.Op:x2} was sent by a thread that did not hold T's monitor"));
            Assert.False(tS.DialogBusy, "T's box was still open");
        }
        finally
        {
            Release(m, mHeld);
            Release(t, tHeld);
            Release(o, oHeld);
        }
    }

    // ===== the offer ==========================================================================================

    /// <summary>The mentor's line for an accepted offer (<c>Session.PromptMentorship</c>).</summary>
    private static string AcceptLine(string protege) =>
        $"{protege} accepts your offer of mentorship! Please guide them until level {Mentorship.CulminateLevel}, " +
        "where you will need to cast this again to end the mentorship.";

    /// <summary><paramref name="mentor"/> casts Mentor (the real <c>0x0F</c>, the spell in slot 0) and types
    /// <paramref name="protege"/>'s name: the cast's checks of the protégé run, and the mentor's dialog waits on the
    /// first of its four pages.</summary>
    private static void BeginOffer(Session mentor, string protege) => OnOwnThread(() =>
    {
        mentor.Receive(SpellCastSupport.CastFrame(0));
        Assert.True(SpinWait.SpinUntil(() => mentor.DialogBusy, Bound), "the cast never asked for a name");
        mentor.Receive(DialogInputFrame(protege));
        Assert.True(mentor.DialogBusy, "the offer stopped before its pages");
    });

    /// <summary>The mentor reads the four pages and answers "Yes" to "Are you sure": the protégé's box then asks
    /// whether they accept, with its two answers.</summary>
    private static void FinishOffer(Session mentor, RecordingOutbound mentorOut, Session protege,
                                    RecordingOutbound protegeOut, string m, string t)
    {
        OnOwnThread(() =>
        {
            for (int i = 0; i < 4; i++) mentor.Receive(NextFrame());
            Assert.Equal(new[] { "Yes", "No" }, LastMenu(mentorOut, $"Are you sure you would like to offer mentorship to {t}?"));
            mentor.Receive(MenuFrame(1));
        });
        Assert.True(protege.DialogBusy, "the offer did not ask the protégé");
        Assert.Equal(new[] { "Yes! I need guidance.", "No, I must decline." },
                     LastMenu(protegeOut, $"{m} would like to offer you mentorship. Do you accept?"));
    }

    /// <summary>The protégé's side of a mentorship, read under their monitor: the name in the mentor slot, and whether
    /// they hold the "being mentored" legend.</summary>
    private static (string Mentor, bool Legend) BondOf(Session s) =>
        s.WithState(() => (s.Quest(Mentorship.Key).GetText(Mentorship.MentorSlot), s.HasLegend(Mentorship.BeingMentoredLegend)));

    /// <summary>Log each of the two players' lines and frames from the answer on, with the held flag of each, and a
    /// fingerprint of each one's frames.</summary>
    private void Report(string label, HeldOutbound mWire, HeldOutbound tWire, params string[] names)
    {
        static string Lines(HeldOutbound w) => string.Join(" | ", w.Lines().Select(l => $"'{l.Text}' held={l.Held}"));
        static string Frames(HeldOutbound w) => string.Join(" ", w.Sent().Select(s => $"0x{s.Op:x2}:held={s.Held}"));
        _out.WriteLine($"[{label}] M's lines [{Lines(mWire)}], frames [{Frames(mWire)}]; T's lines [{Lines(tWire)}], " +
                       $"frames [{Frames(tWire)}]");
        _out.WriteLine($"[{label}] M's frames {Fingerprint(mWire.Rec, names)}; T's frames {Fingerprint(tWire.Rec, names)}");
    }

    // ===== the players ========================================================================================

    /// <summary>A name of fixed length (the serial has three digits), so a line's length, which its <c>0x0A</c> body
    /// carries, and with it each fingerprint, is the same from run to run.</summary>
    private static string Name(string tag) => $"Ml{tag}{Interlocked.Increment(ref _serial) % 1000:D3}";

    /// <summary>A character as creation writes it, at the 50th insight, standing on <paramref name="map"/>.</summary>
    private static Character NewCharacter(string name, ushort map, ushort x, ushort y)
    {
        var c = CharacterFactory.FromCreate(name, new byte[] { 0x29, 0, 1, 0, 0 });
        var info = Content.Maps.GetValueOrDefault(map);
        c.Map = map; c.X = x; c.Y = y;
        c.MapXs = info?.Xs ?? 20; c.MapYs = info?.Ys ?? 20;
        c.Level = 50;
        c.MaxHp = 1_000; c.Hp = 1_000;
        c.MaxMp = 100; c.Mp = 100;
        return c;
    }

    /// <summary>A mentor: Mentor (Spells.csv 106) in slot 0.</summary>
    private static Character MentorRow(string name, ushort map, ushort x, ushort y)
    {
        var c = NewCharacter(name, map, x, y);
        c.Spells.Clear();
        c.Spells.Add(Content.SpellByKey("mentor")!.Id);
        return c;
    }

    /// <summary>A protégé the offer accepts: at the 5th insight, never mentored and with no mentor.</summary>
    private static Character ProtegeRow(string name, ushort map, ushort x, ushort y)
    {
        var c = NewCharacter(name, map, x, y);
        c.Level = 5;
        return c;
    }

    private static void Seed(IsolatedDatabase db, params Character[] rows)
    {
        foreach (var c in rows) Assert.True(db.Store.Save(c), $"the setup save of {c.Name} failed");
    }

    /// <summary>A session that has logged in through the real <c>0x10</c> arrival, on a <see cref="HeldOutbound"/>.</summary>
    private (Session Session, HeldOutbound Outbound) ArriveHeld(CharacterStore store, string name)
    {
        var outbound = new HeldOutbound($"held:{name}");
        var s = new Session(outbound, 2005, store, _fx.World);
        outbound.Owner = s;
        s.Receive(ArrivalFrame(name));
        Assert.False(outbound.Closed, $"the arrival for '{name}' was refused");
        Assert.Contains(s, _fx.World.Online.All());
        return (s, outbound);
    }

    /// <summary>Off the map, and the account's slot out of the registry, so a later login for this name is not handed
    /// this session to fence.</summary>
    private void Release(string name, Session? s)
    {
        if (s is null) return;
        string key = CharacterStore.Key(name);
        _fx.World.LeaveMap(s, s.CharMap);
        _fx.World.Online.Register(key, s, out _);
        _fx.World.Online.Unregister(key, s);
    }

    private static byte[] ArrivalFrame(string user) =>
        Handoff.ArrivalFrame(user, System.Net.IPAddress.None.ToString());

    /// <summary>An outbound that keeps every frame and whether the thread that handed it over held the owner's state
    /// monitor at that moment (<c>Session.StateHeld</c>, <c>Monitor.IsEntered</c>): <c>Session.Send</c> hands each
    /// frame to <see cref="IOutbound.Send"/> synchronously, on the sending thread. Every frame also goes on to
    /// <see cref="Rec"/>, so the dialog helpers can drive and read a player who logged in on this.</summary>
    private sealed class HeldOutbound : IOutbound
    {
        private readonly List<(byte[] Frame, bool Held)> _frames = new();
        private readonly object _gate = new();
        public Session? Owner;
        public readonly RecordingOutbound Rec;

        public HeldOutbound(string remote)
        {
            Remote = remote;
            Rec = new RecordingOutbound(remote);
        }

        public string Remote { get; }
        public int Capacity => int.MaxValue;
        public int QueueDepth => 0;
        public bool Closed { get; private set; }

        public bool Send(byte[] frame)
        {
            bool held = Owner?.StateHeld ?? false;
            lock (_gate) _frames.Add((frame, held));
            return Rec.Send(frame);
        }

        public void Close()
        {
            Closed = true;
            Rec.Close();
        }

        public void Clear()
        {
            lock (_gate) _frames.Clear();
            Rec.Clear();
        }

        /// <summary>Every status line (<c>0x0A</c>) so far, with its flag.</summary>
        public List<(string Text, bool Held)> Lines() =>
            Sent().Where(s => s.Op == ServerOp.MiniText).Select(s => (MiniTextOf(s.Body), s.Held)).ToList();

        /// <summary>Every frame so far, decrypted as <see cref="RecordingOutbound.BodiesOf"/> does, with its flag.</summary>
        public List<(byte Op, byte[] Body, bool Held)> Sent()
        {
            List<(byte[] Frame, bool Held)> snap;
            lock (_gate) snap = _frames.ToList();
            var sent = new List<(byte Op, byte[] Body, bool Held)>();
            foreach (var (frame, held) in snap)
                if (TkPacket.TryParse(frame, out var pkt, out _))
                    sent.Add((pkt.Opcode, TkCrypt.Crypt(pkt.Body, pkt.Increment, TkCrypt.LoginKey), held));
            return sent;
        }
    }

    /// <summary>A <c>0x0A</c> body's text: the type, the u16BE length, the ASCII (<c>SpellCastSupport.MiniTexts</c>).</summary>
    private static string MiniTextOf(byte[] body) =>
        body.Length < 3 ? "" : Encoding.ASCII.GetString(body, 3, Math.Min((body[1] << 8) | body[2], body.Length - 3));

    /// <summary>Every frame <paramref name="o"/> holds, as its opcodes in order and the first 16 hex digits of a SHA-256
    /// over each opcode and decrypted body, with each of <paramref name="names"/> put back to &lt;0&gt;, &lt;1&gt;, ...:
    /// two builds that send the same frames print the same line.</summary>
    private static string Fingerprint(RecordingOutbound o, params string[] names)
    {
        var ops = new List<string>();
        var all = new StringBuilder();
        foreach (var frame in o.Frames)
        {
            if (!TkPacket.TryParse(frame, out var pkt, out _)) { ops.Add("?"); continue; }
            ops.Add($"{pkt.Opcode:x2}");
            string body = Encoding.Latin1.GetString(TkCrypt.Crypt(pkt.Body, pkt.Increment, TkCrypt.LoginKey));
            for (int i = 0; i < names.Length; i++) body = body.Replace(names[i], $"<{i}>");
            all.Append($"{pkt.Opcode:x2}:{body}\n");
        }
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.Latin1.GetBytes(all.ToString()));
        return $"[{string.Join(" ", ops)}] sha256 {Convert.ToHexString(hash)[..16]}";
    }

    // ===== the dialog =========================================================================================

    /// <summary>The labels of the last 0x30 menu whose prompt ends with <paramref name="promptTail"/>: after the
    /// prompt come the item count and each item as u8 length + ASCII (<c>Session.SendNpcMenuP</c>).</summary>
    private static List<string> LastMenu(RecordingOutbound o, string promptTail)
    {
        byte[] tail = Encoding.ASCII.GetBytes(promptTail);
        var body = o.BodiesOf(ServerOp.NpcDialog).LastOrDefault(bd =>
            bd.Length > 2 && bd[0] == 0x02 && bd[1] == 0x02 && bd.AsSpan().IndexOf(tail) >= 0);
        Assert.True(body is not null, $"no menu ending \"{promptTail}\" was sent");
        int i = body!.AsSpan().IndexOf(tail) + tail.Length;
        int count = body[i++];
        var labels = new List<string>();
        for (int k = 0; k < count; k++)
        {
            int len = body[i++];
            labels.Add(Encoding.ASCII.GetString(body, i, len));
            i += len;
        }
        return labels;
    }

    /// <summary>The client's "next" on a text box (0x3A kind 1).</summary>
    private static byte[] NextFrame()
    {
        var body = new byte[11];
        body[0] = 0x01;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>A menu pick (0x3A kind 2, the 1-based index at [10]).</summary>
    private static byte[] MenuFrame(byte index)
    {
        var body = new byte[11];
        body[0] = 0x02;
        body[10] = index;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>An input box's answer (0x3A kind 4: the length at [10], the text from [11]).</summary>
    private static byte[] DialogInputFrame(string text)
    {
        byte[] t = Encoding.ASCII.GetBytes(text);
        var body = new byte[11 + t.Length];
        body[0] = 0x04;
        body[8] = 0x02;
        body[10] = (byte)t.Length;
        t.CopyTo(body, 11);
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>Run a dialog on a thread of its own, which has no synchronization context: the dialog's awaits then
    /// resume inline on the thread that delivers each reply, as on the read loop.</summary>
    private static void OnOwnThread(Action body)
    {
        Exception? fault = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { fault = e; }
        }) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(60)), "the dialog thread never finished");
        if (fault is not null) throw new Xunit.Sdk.XunitException($"the dialog thread failed: {fault}");
    }
}
