using System.Diagnostics;
using System.Reflection;
using System.Text;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The rest of the peer-state class #339 and #341 started on: what one player's thread does to, or reads from,
/// another player's state. The source lists are #341's sweep (reports/server-peer-writes-lock-opus.md, "The
/// sweep") and its tier A review (PR341-by-fable.md, F2 and the pre-existing list). The groups, each added with its
/// fix:
/// <list type="number">
/// <item><b>A dialog prompt opened on another player.</b> A prompt is two things: the 0x30 frame the player sees
/// and the pending reply (<c>_dlgReply</c>) their next 0x3A answers. The proposal, the mentorship offer and the
/// wedding's question open them on the OTHER player from the asker's thread, and the wedding's closing box is opened
/// on the bride from the fiancé's thread (the ceremony resumes there). Both halves were written without that
/// player's monitor, while their own read loop reads and clears <c>_dlgReply</c> under it.</item>
/// <item><b>Sends into another player</b> on those paths. No fact here: a send writes nothing a monitor guards
/// (the report says why), so nothing changed.</item>
/// </list>
///
/// <para>Shapes, as in <see cref="PeerWritesLockTests"/>: the action through its real entry (the 0x0F cast, the
/// typed name and menu picks as 0x3A replies, the 0x43 click, a Chapel NPC's dialog); the same action while a third
/// thread holds the other player's monitor, in both <c>StateRank</c> orders; a <see cref="StallWatch"/> race, both
/// ways at once where the action allows it; and, Debug only, the entry under <c>World._lock</c> asserts. Where the
/// write is a frame, the recipient's outbound records whether the sending thread held the recipient's monitor at the
/// instant the frame was handed over (<see cref="HeldRecorder"/>, the probe <see cref="PeerMiniTextMonitorTests"/>
/// uses). The races call the handler bodies under the actor's monitor, as the packet handler does, below the action
/// budget, and a dialog is driven from a thread of its own so its awaits resume inline on the thread that delivers
/// the reply, as on the read loop. Content-free maps no other class uses; the wedding stands the bride beside Fen at
/// the Gale Chapel (2511), which no other class uses either.</para>
/// </summary>
[Collection("world")]
public sealed class PeerSweepLockTests
{
    // Content-free maps (no Maps.csv row) that no other class uses: 62430-62453, one line per group.
    private const ushort PromptMap = 62430, PromptHeldMap = 62431, PromptRaceMap = 62432, PromptPinMap = 62433,
                         MentorMap = 62434, MentorHeldMap = 62435, WeddingMap = 62436, WeddingRaceMap = 62437,
                         PromptMidMap = 62453;

    /// <summary>Fen, the Gale Chapel's Chapel NPC (game-data/NPCs.csv, NpcId 230, map 2511): the ceremony's real
    /// entry (Server/NpcAbility.cs, ChapelAbility's "Marriage").</summary>
    private const int ChapelNpcId = 230;

    private const int Rounds = 300;
    private const string Ping = "peer sweep race ping";
    private const string Declined = "What but love can last forever?";

    private readonly SessionFixture _fx;
    private static int _serial;

    private static readonly FieldInfo DlgReplyField =
        typeof(Session).GetField("_dlgReply", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public PeerSweepLockTests(SessionFixture fx) => _fx = fx;

    // ===== 1. a dialog prompt opened on another player =================================================

    /// <summary>A proposal through its real entry: the 0x0F cast of Propose, then the beloved's name typed into the
    /// prompt. The beloved's accept/decline menu is opened inside the beloved's monitor, and accepting it engages
    /// both, as before: the legends, the fiancé fields, the ring taken, a line to each.
    /// <para>Red on 3690542 in both builds: the menu went to the beloved from the proposer's thread, which held the
    /// proposer's monitor and not the beloved's.</para></summary>
    [Fact]
    public void AProposalOpensTheBelovedsPromptInsideTheirMonitor()
    {
        var propose = Content.SpellByKey("propose")!;
        var (b, bOut, _) = Player("Beloved", PromptMap, 22, 20);
        var (p, pOut, _) = Player("Proposer", PromptMap, 20, 20, c => c.Spells.Add(propose.Id));
        try
        {
            GiveRing(b);
            OnOwnThread(() =>
            {
                p.Receive(SpellCastSupport.CastFrame(0));
                Assert.True(p.DialogBusy, "the name prompt is not waiting");
                p.Receive(InputFrame(b.CharName));
            });

            var ask = LastMenu(bOut, $"{p.CharName} proposes marriage. Do you accept?");
            Assert.True(ask.Held, "the proposal was opened on the beloved outside their monitor");
            Assert.Equal(new[] { "Yes! I am madly in love.", "I must decline." }, ask.Labels);
            Assert.True(b.DialogBusy);

            OnOwnThread(() => b.Receive(MenuFrame(1)));
            Assert.False(b.DialogBusy);
            b.WithState(() => Assert.True(b.HasLegend("engaged") && b.CharFiance == p.CharName && b.CharIsProposee
                                          && b.CountItem("engagement_ring") == 0, "the beloved is not engaged"));
            p.WithState(() => Assert.True(p.HasLegend("engaged") && p.CharFiance == b.CharName && !p.CharIsProposee,
                                          "the proposer is not engaged"));
            Assert.Contains($"You are now engaged to {p.CharName}!", bOut.MiniTexts());
            Assert.Contains($"{b.CharName} accepts!", pOut.MiniTexts());
        }
        finally
        {
            Leave(PromptMap, p, b);
        }
    }

    /// <summary>The reproduction that waits: the proposal's last step, as the proposer's read loop runs it (inside the
    /// proposer's monitor), opens nothing on the beloved while a third thread holds the beloved's monitor, in both
    /// <c>StateRank</c> orders; it opens once that thread lets go, and a decline still says both lines.
    /// <para>Red on 3690542 in both builds: the menu was sent and the pending reply replaced while the third thread
    /// held the beloved's monitor.</para></summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AProposalWaitsForTheBelovedsMonitor(bool proposerRanksFirst)
    {
        Session p, b;
        HeldRecorder pOut, bOut;
        if (proposerRanksFirst)
        {
            (p, pOut, _) = Player("HeldProposer", PromptHeldMap, 20, 20);
            (b, bOut, _) = Player("HeldBeloved", PromptHeldMap, 22, 20);
        }
        else
        {
            (b, bOut, _) = Player("HeldBeloved", PromptHeldMap, 22, 20);
            (p, pOut, _) = Player("HeldProposer", PromptHeldMap, 20, 20);
        }
        Assert.Equal(proposerRanksFirst, p.StateRank < b.StateRank);
        string prompt = $"{p.CharName} proposes marriage. Do you accept?";

        var run = new HeldRun();
        try
        {
            WhileAnotherThreadHolds(run, b, () => p.WithState(() => _ = b.PromptProposal(p)),
                                    untouched: () => !b.DialogBusy && !bOut.HasMenu(prompt));
            AssertWaited(run, "the proposal", "the beloved's prompt was opened while another thread held their monitor");

            Assert.True(b.DialogBusy);
            Assert.True(LastMenu(bOut, prompt).Held);
            OnOwnThread(() => b.Receive(MenuFrame(2)));
            Assert.Contains($"{b.CharName} regretably must decline.", pOut.MiniTexts());
            Assert.Contains(Declined, bOut.MiniTexts());
        }
        finally
        {
            if (run.Settled) Leave(PromptHeldMap, p, b);
        }
    }

    /// <summary>What the swap could do to a player. The beloved is in a conversation of their own and has just picked
    /// "Yes" in it; their read loop is handling that pick (a third thread holds their monitor, as <c>Handle</c> does)
    /// when a proposal arrives. The pick answers the beloved's own menu, and the proposal opens after it and waits for
    /// an answer of its own, in both orders.
    /// <para>Red on 3690542 in both builds: the proposal replaced the pending reply in the middle of the handler, the
    /// pick resolved the PROPOSAL, and the beloved was engaged by a click they made on another box, which itself never
    /// answered.</para></summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AProposalArrivingWhileTheBelovedAnswersAnotherBoxDoesNotTakeTheirAnswer(bool proposerRanksFirst)
    {
        var (npc, _) = ChapelNpc();   // any NPC can frame the beloved's own menu
        Session p, b;
        HeldRecorder bOut;
        if (proposerRanksFirst)
        {
            (p, _, _) = Player("MidProposer", PromptMidMap, 20, 20);
            (b, bOut, _) = Player("MidBeloved", PromptMidMap, 22, 20);
        }
        else
        {
            (b, bOut, _) = Player("MidBeloved", PromptMidMap, 22, 20);
            (p, _, _) = Player("MidProposer", PromptMidMap, 20, 20);
        }
        Assert.Equal(proposerRanksFirst, p.StateRank < b.StateRank);
        string prompt = $"{p.CharName} proposes marriage. Do you accept?";

        Task<int>? own = null;
        OnOwnThread(() => b.WithState(() => { own = b.DlgMenu(npc, "Will you buy a lotus?", new[] { "Yes", "No" }); }));
        var run = new HeldRun();
        bool ownAnswered = false, engaged = false;
        try
        {
            WhileAnotherThreadHolds(run, b, () => p.WithState(() => _ = b.PromptProposal(p)),
                untouched: () =>
                {
                    b.Receive(MenuFrame(1));   // the pick being handled: "Yes" on the beloved's own menu
                    ownAnswered = own!.IsCompleted && own.Result == 1;
                    engaged = b.HasLegend("engaged");
                    return ownAnswered && !engaged;
                });
            Assert.Null(run.HolderFault);
            Assert.Null(run.ActorFault);
            Assert.True(ownAnswered && !engaged, $"the beloved's pick was taken by the proposal: their own menu answered "
                                                + $"{ownAnswered}, engaged {engaged}");
            Assert.False(run.FinishedWhileHeld, "the proposal finished while another thread held the beloved's monitor");

            b.WithState(() => Assert.False(b.HasLegend("engaged") || b.CharFiance.Length > 0, "the beloved was engaged"));
            Assert.True(b.DialogBusy, "the proposal is not waiting for its own answer");
            Assert.True(LastMenu(bOut, prompt).Held);
            OnOwnThread(() => b.Receive(MenuFrame(2)));
            Assert.Contains(Declined, bOut.MiniTexts());
        }
        finally
        {
            if (run.Settled) Leave(PromptMidMap, p, b);
        }
    }

    /// <summary>A mentorship offer through its real entry: the 0x0F cast of Mentor, the protégé's name, the four pages
    /// and the "Are you sure" menu. The protégé's accept/decline menu is opened inside the protégé's monitor, and
    /// accepting binds them as before: the mentor slot, the "being mentored" legend, a line to each.
    /// <para>Red on 3690542 in both builds: the menu went to the protégé from the mentor's thread.</para></summary>
    [Fact]
    public void AMentorshipOfferOpensTheProtegesPromptInsideTheirMonitor()
    {
        var mentor = Content.SpellByKey("mentor")!;
        var (t, tOut, _) = Player("Protege", MentorMap, 22, 20, c => c.Level = 5);
        var (m, mOut, _) = Player("Mentor", MentorMap, 20, 20, c => c.Spells.Add(mentor.Id));
        try
        {
            OnOwnThread(() =>
            {
                m.Receive(SpellCastSupport.CastFrame(0));
                m.Receive(InputFrame(t.CharName));
                for (int i = 0; i < 4; i++) m.Receive(NextFrame());   // the four pages about mentoring
                Assert.True(m.DialogBusy, "the mentor's last question is not waiting");
                m.Receive(MenuFrame(1));                               // "Are you sure ...?" Yes
            });

            var offer = LastMenu(tOut, $"{m.CharName} would like to offer you mentorship. Do you accept?");
            Assert.True(offer.Held, "the offer was opened on the protégé outside their monitor");
            Assert.True(t.DialogBusy);

            OnOwnThread(() => t.Receive(MenuFrame(1)));
            t.WithState(() =>
            {
                Assert.Equal(m.CharName, t.Quest(Mentorship.Key).GetText(Mentorship.MentorSlot));
                Assert.True(t.HasLegend(Mentorship.BeingMentoredLegend));
            });
            Assert.Contains($"{m.CharName} is now your mentor.", tOut.MiniTexts());
            Assert.Contains(mOut.MiniTexts(), l => l.StartsWith($"{t.CharName} accepts your offer of mentorship!"));
        }
        finally
        {
            Leave(MentorMap, m, t);
        }
    }

    /// <summary>The offer's last step, as the mentor's read loop runs it, waits for the protégé's monitor in both
    /// orders, and a decline still tells the mentor. Red on 3690542 in both builds: opened while it was held.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AMentorshipOfferWaitsForTheProtegesMonitor(bool mentorRanksFirst)
    {
        Session m, t;
        HeldRecorder mOut, tOut;
        if (mentorRanksFirst)
        {
            (m, mOut, _) = Player("HeldMentor", MentorHeldMap, 20, 20);
            (t, tOut, _) = Player("HeldProtege", MentorHeldMap, 22, 20, c => c.Level = 5);
        }
        else
        {
            (t, tOut, _) = Player("HeldProtege", MentorHeldMap, 22, 20, c => c.Level = 5);
            (m, mOut, _) = Player("HeldMentor", MentorHeldMap, 20, 20);
        }
        Assert.Equal(mentorRanksFirst, m.StateRank < t.StateRank);
        string prompt = $"{m.CharName} would like to offer you mentorship. Do you accept?";

        var run = new HeldRun();
        try
        {
            WhileAnotherThreadHolds(run, t, () => m.WithState(() => _ = t.PromptMentorship(m)),
                                    untouched: () => !t.DialogBusy && !tOut.HasMenu(prompt));
            AssertWaited(run, "the offer", "the protégé's prompt was opened while another thread held their monitor");

            Assert.True(t.DialogBusy);
            OnOwnThread(() => t.Receive(MenuFrame(2)));
            Assert.Contains($"{t.CharName} regretably must decline.", mOut.MiniTexts());
        }
        finally
        {
            if (run.Settled) Leave(MentorHeldMap, m, t);
        }
    }

    /// <summary>The wedding through Fen's real dialog: the bride (the proposee) clicks Fen, picks "Marriage" and
    /// confirms. The ceremony asks the fiancé "Do you, X take Y as your partner?" inside the fiancé's monitor, and a
    /// decline still tells both. Red on 3690542 in both builds: the question went to the fiancé from the bride's
    /// thread, which held the bride's monitor and not the fiancé's.</summary>
    [Fact]
    public void TheWeddingsQuestionOpensInsideTheFiancesMonitor()
    {
        var (npc, def) = ChapelNpc();
        var (fi, fiOut, _) = Player("Fiance", WeddingMap, 20, 20);
        var (br, brOut, _) = Player("Bride", def.Map, (ushort)def.X, (ushort)(def.Y + 2), c => Wide(c, def.Map));
        try
        {
            Engage(br, fi, proposee: true);
            Engage(fi, br, proposee: false);
            OnOwnThread(() => OpenCeremony(br, brOut, npc, def));

            var question = LastMenu(fiOut, $"Do you, {fi.CharName} take {br.CharName} as your partner?");
            Assert.True(question.Held, "the wedding's question was opened on the fiancé outside their monitor");
            Assert.True(fi.DialogBusy);

            OnOwnThread(() => fi.Receive(MenuFrame(2)));   // "I don't."
            Assert.Contains($"{fi.CharName} regretably must decline.", brOut.MiniTexts());
            Assert.Contains(Declined, fiOut.MiniTexts());
            br.WithState(() => Assert.False(br.HasLegend("married")));
        }
        finally
        {
            Leave(def.Map, br);
            Leave(WeddingMap, fi);
        }
    }

    /// <summary>The fiancé's "I do" resumes the ceremony on the FIANCÉ's thread, and the Chapel then shows the bride its
    /// closing box from there. That box is opened inside the bride's monitor; the bride's "next" closes it, and both are
    /// married as before: the legend, the spouse, a love keepsake each, the engagement gone, the line to each.
    /// <para>Red on 3690542 in both builds: the box went to the bride from the fiancé's thread without her
    /// monitor.</para></summary>
    [Fact]
    public void TheWeddingsClosingBoxOpensInsideTheBridesMonitor()
    {
        var (npc, def) = ChapelNpc();
        var (fi, fiOut, _) = Player("Groom", WeddingMap, 24, 20);
        var (br, brOut, _) = Player("Bride", def.Map, (ushort)(def.X + 1), (ushort)(def.Y + 2), c => Wide(c, def.Map));
        try
        {
            Engage(br, fi, proposee: true);
            Engage(fi, br, proposee: false);
            OnOwnThread(() => OpenCeremony(br, brOut, npc, def));
            OnOwnThread(() => fi.Receive(MenuFrame(1)));   // "I do."

            var box = LastBox(brOut, "Congratulations! You are both now married.");
            Assert.True(box.Held, "the Chapel's closing box was opened on the bride outside her monitor");
            Assert.True(br.DialogBusy);
            OnOwnThread(() => br.Receive(NextFrame()));
            Assert.False(br.DialogBusy);

            br.WithState(() => Assert.True(Married(br, fi.CharName), "the bride is not married"));
            fi.WithState(() => Assert.True(Married(fi, br.CharName), "the fiancé is not married"));
            Assert.Contains("I now pronounce you (married)", brOut.MiniTexts());
            Assert.Contains("I now pronounce you (married)", fiOut.MiniTexts());
        }
        finally
        {
            Leave(def.Map, br);
            Leave(WeddingMap, fi);
        }
    }

    /// <summary>Two players open prompts on each other, again and again: each proposes to the other and offers the
    /// other a mentorship, as the asker's read loop does it (inside the asker's monitor), and answers whatever is
    /// waiting on itself with a decline through the real 0x3A; between them each holds its own monitor a fifth of a
    /// millisecond and checks its pending reply did not change, then steps at the other's tile while the world
    /// ticks. Each prompt enters the other player's monitor from inside the asker's, so one direction ascends and the
    /// other descends. Nothing may stall, and no prompt may land inside a player's own section.
    /// <para>Red on 3690542 in both builds: prompts land inside the other's section. It stalls with the prompt
    /// opened under <c>World._lock</c> (one player holds the lock and waits for the other's monitor while the other,
    /// mid-step, holds its own and waits for the lock), and with a plain <c>lock</c> instead of <c>EnterState</c>
    /// (each holds its own and waits for the other's).</para></summary>
    [Fact]
    public void TwoPlayersOpeningPromptsOnEachOtherNeverStall()
    {
        var (a, aOut, _) = Player("PromptRaceA", PromptRaceMap, 10, 10, c => c.Level = 5);
        var (b, bOut, _) = Player("PromptRaceB", PromptRaceMap, 11, 10, c => c.Level = 5);
        var race = new Race();
        int torn = 0;

        void Asker(Session self, Session other)
        {
            for (int i = 0; i < Rounds; i++)
            {
                self.WithState(() =>
                {
                    object? before = DlgReplyField.GetValue(self);
                    Dwell();
                    if (!ReferenceEquals(DlgReplyField.GetValue(self), before)) Interlocked.Increment(ref torn);
                });
                self.WithState(() => _ = other.PromptProposal(self));
                self.Receive(MenuFrame(2));
                self.WithState(() => _ = other.PromptMentorship(self));
                self.Receive(MenuFrame(2));
                Step(self, other, PromptRaceMap);
                race.Progress.Bump();
            }
        }

        try
        {
            RunRace(race, "the two players opening prompts on each other", tick: true,
                    () => Asker(a, b), () => Asker(b, a));

            Assert.Null(race.Fault);
            Assert.Equal(0, torn);
            Assert.True(aOut.Count(Declined) > 0 && bOut.Count(Declined) > 0, "no proposal was declined both ways");
            Assert.Equal(((ushort)10, (ushort)11), (a.PlayerX, b.PlayerX));
        }
        finally
        {
            race.Done = true;
            if (race.Settled) Leave(PromptRaceMap, a, b);
        }
    }

    /// <summary>A wedding cannot run both ways (only the proposee starts one), so its race pairs the bride's question
    /// with the fiancé's party line back into her. Round after round the bride asks, as the Chapel's continuation does
    /// it inside her monitor; the fiancé holds his monitor a fifth of a millisecond and checks his pending reply did
    /// not change, answers "I don't" through the real 0x3A (the ceremony resumes on his thread and tells them both),
    /// and sends the bride a party line. The fiancé is created first, so the bride's question descends. Nothing may
    /// stall; nothing lands inside his section; every decline tells both.
    /// <para>It stalls with the question under <c>World._lock</c> and with a plain <c>lock</c> on the fiancé's monitor
    /// (the bride holds hers and waits for his, while he holds his and waits for hers in the party line).</para></summary>
    [Fact]
    public void ABrideAskingAFianceWhoWritesBackNeverStalls()
    {
        var (fi, fiOut, _) = Player("WedRaceFiance", WeddingRaceMap, 10, 10);
        var (br, brOut, _) = Player("WedRaceBride", WeddingRaceMap, 11, 10);
        Assert.True(fi.StateRank < br.StateRank);
        var race = new Race();
        int torn = 0;
        try
        {
            Engage(br, fi, proposee: true);
            Engage(fi, br, proposee: false);

            RunRace(race, "the bride asking while the fiancé writes back", tick: true,
                () =>
                {
                    for (int i = 0; i < Rounds; i++)
                    {
                        br.WithState(() => _ = br.RunMarriageCeremony());
                        Step(br, fi, WeddingRaceMap);
                        race.Progress.Bump();
                    }
                },
                () =>
                {
                    for (int i = 0; i < Rounds; i++)
                    {
                        fi.WithState(() =>
                        {
                            object? before = DlgReplyField.GetValue(fi);
                            Dwell();
                            if (!ReferenceEquals(DlgReplyField.GetValue(fi), before)) Interlocked.Increment(ref torn);
                        });
                        fi.Receive(MenuFrame(2));
                        fi.WithState(() => br.NotifyGroup(Ping));
                        Step(fi, br, WeddingRaceMap);
                        race.Progress.Bump();
                    }
                });

            Assert.Null(race.Fault);
            Assert.Equal(0, torn);
            int declined = fiOut.Count(Declined);
            Assert.True(declined > 0, "the fiancé never declined");
            Assert.Equal(declined, brOut.Count($"{fi.CharName} regretably must decline."));
            Assert.Equal(Rounds, brOut.Count(Ping));
        }
        finally
        {
            race.Done = true;
            if (race.Settled) Leave(WeddingRaceMap, fi, br);
        }
    }

#if DEBUG
    /// <summary>A prompt on another player is opened the ordered way, asserted: with <c>World._lock</c> held the
    /// lock-order assert fires before anything is sent or set waiting; from a lower- and a higher-ranked player's
    /// monitor it is silent and the prompt waits. Debug-only by construction. The assert is raised inside the async
    /// prompt, so it is the prompt's task that carries it.
    /// <para>Red without the fix: with no monitor nothing fires and the prompt opens under the world lock.</para></summary>
    [Fact]
    public void OpeningAPromptOnAnotherPlayerUnderTheWorldLockAsserts()
    {
        var (npc, _) = ChapelNpc();
        var (low, _, _) = Player("PromptPinLow", PromptPinMap, 60, 60);
        var (v, vOut, _) = Player("PromptPinV", PromptPinMap, 62, 60);
        var (high, _, _) = Player("PromptPinHigh", PromptPinMap, 64, 60);
        var labels = new[] { "Yes", "No" };
        try
        {
            Assert.True(low.StateRank < v.StateRank && v.StateRank < high.StateRank);

            Task<int>? wrongWay = null;
            _fx.World.UnderWorldLockForTest(() => { wrongWay = v.DlgMenu(npc, "Pinned under the world lock?", labels); });
            Assert.True(wrongWay!.IsFaulted, "the prompt opened under the world lock without asserting");
            Assert.Contains("lock order violated: World._lock is held while entering a session monitor",
                            wrongWay.Exception!.InnerException!.Message);
            Assert.False(v.DialogBusy);
            Assert.False(vOut.HasMenu("Pinned under the world lock?"));

            Task<int>? fromLow = null, fromHigh = null;
            Assert.Null(Record.Exception(() => low.WithState(() => { fromLow = v.DlgMenu(npc, "Pinned from below?", labels); })));
            Assert.False(fromLow!.IsFaulted);
            Assert.True(v.DialogBusy);
            Assert.Null(Record.Exception(() => high.WithState(() => { fromHigh = v.DlgMenu(npc, "Pinned from above?", labels); })));
            Assert.False(fromHigh!.IsFaulted);
            Assert.True(LastMenu(vOut, "Pinned from above?").Held);
        }
        finally
        {
            Leave(PromptPinMap, low, v, high);
        }
    }
#endif

    // ===== the held-monitor run =======================================================================

    /// <summary>What <see cref="WhileAnotherThreadHolds"/> saw. <see cref="Settled"/> is false while either thread may
    /// still be parked on a lock: a fact must then leave its maps as they are.</summary>
    private sealed class HeldRun
    {
        public bool Settled = true;
        public bool FinishedWhileHeld;
        public bool UntouchedWhileHeld;
        public Exception? HolderFault, ActorFault;
    }

    /// <summary>A third thread takes <paramref name="target"/>'s monitor; then <paramref name="act"/> runs on a thread
    /// of its own until it finishes or sits blocked for 100 ms; then, still inside the third thread's section,
    /// <paramref name="untouched"/> says whether the state is as it was. Then the third thread lets go and both are
    /// joined.</summary>
    private static void WhileAnotherThreadHolds(HeldRun run, Session target, Action act, Func<bool> untouched)
    {
        var held = new ManualResetEventSlim();
        var letGo = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            try
            {
                target.WithState(() =>
                {
                    held.Set();
                    letGo.Wait();
                    run.UntouchedWhileHeld = untouched();
                });
            }
            catch (Exception e) { run.HolderFault = e; }
        }) { IsBackground = true };
        var actor = new Thread(() =>
        {
            try { act(); }
            catch (Exception e) { run.ActorFault = e; }
        }) { IsBackground = true };

        run.Settled = false;
        try
        {
            holder.Start();
            Assert.True(held.Wait(TimeSpan.FromSeconds(30)), "the holder never took the target's monitor");
            actor.Start();
            WaitUntilDoneOrBlocked(actor);
            run.FinishedWhileHeld = !actor.IsAlive;
        }
        finally
        {
            letGo.Set();
        }
        Assert.True(holder.Join(TimeSpan.FromSeconds(60)), "the holder never let go");
        Assert.True(actor.Join(TimeSpan.FromSeconds(60)), "the action never finished after the monitor was free");
        run.Settled = true;
    }

    private static void AssertWaited(HeldRun run, string what, string touched)
    {
        Assert.Null(run.HolderFault);
        Assert.Null(run.ActorFault);
        Assert.False(run.FinishedWhileHeld, $"{what} finished while another thread held the target's monitor");
        Assert.True(run.UntouchedWhileHeld, touched);
    }

    /// <summary>Wait until <paramref name="t"/> has finished, or has sat blocked for 100 ms straight, or 10 s have
    /// passed.</summary>
    private static void WaitUntilDoneOrBlocked(Thread t)
    {
        var clock = Stopwatch.StartNew();
        long blockedSince = -1;
        while (clock.ElapsedMilliseconds < 10_000 && !t.Join(5))
        {
            if ((t.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0) blockedSince = -1;
            else if (blockedSince < 0) blockedSince = clock.ElapsedMilliseconds;
            else if (clock.ElapsedMilliseconds - blockedSince >= 100) return;
        }
    }

    // ===== the race ===================================================================================

    private sealed class Race
    {
        public readonly StallWatch.RoundCounter Progress = new();
        public readonly StallWatch.RoundCounter Ticks = new();
        public readonly ManualResetEventSlim Start = new();
        public Exception? Fault;
        public volatile bool Done;
        public bool Settled = true;

        public void Record(Exception e) => Interlocked.CompareExchange(ref Fault, e, null);
    }

    /// <summary>Start every worker on its own thread, and the world's tick on another when <paramref name="tick"/>;
    /// release them together; fail on a stall (<see cref="StallWatch"/>) rather than a stopwatch.</summary>
    private void RunRace(Race race, string what, bool tick, params Action[] workers)
    {
        race.Settled = false;
        var threads = workers.Select(w => new Thread(() =>
        {
            try { race.Start.Wait(); w(); }
            catch (Exception e) { race.Record(e); }
        }) { IsBackground = true }).ToArray();
        var ticker = new Thread(() =>
        {
            try
            {
                race.Start.Wait();
                while (!race.Done) { _fx.World.TickOnceForTest(); race.Ticks.Bump(); }
            }
            catch (Exception e) { race.Record(e); }
        }) { IsBackground = true };

        foreach (var t in threads) t.Start();
        if (tick) ticker.Start();
        race.Start.Set();
        try
        {
            StallWatch.RunUntilDoneOrStalled(threads, () => race.Progress.Rounds,
                StallWatch.StallQuiet, StallWatch.StallCap, what);
        }
        finally
        {
            race.Done = true;
        }
        if (tick)
            StallWatch.RunUntilDoneOrStalled(new[] { ticker }, () => race.Ticks.Rounds,
                StallWatch.StallQuiet, StallWatch.StallCap, "the tick thread");
        race.Settled = true;
    }

    /// <summary>A step at <paramref name="other"/>'s tile, refused because it is taken: the stepper's monitor and then
    /// <c>World._lock</c>, the order every walk takes.</summary>
    private void Step(Session self, Session other, ushort map) =>
        self.WithState(() => _fx.World.TryMovePlayer(self, map, other.PlayerX, other.PlayerY, ghostMover: false,
                                                     enforceOccupancy: true, otherwiseBlocked: false, out _));

    /// <summary>A fifth of a millisecond of work on the calling thread, without giving up whatever it holds.</summary>
    private static void Dwell()
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedTicks < Stopwatch.Frequency / 5_000) Thread.SpinWait(20);
    }

    // ===== vows =======================================================================================

    /// <summary>An engagement ring in <paramref name="s"/>'s bag, given under its own monitor.</summary>
    private static void GiveRing(Session s) =>
        s.WithState(() => Assert.True(s.GiveRewardItem("engagement_ring", 1), "no room for the ring"));

    /// <summary><paramref name="self"/>'s half of an engagement to <paramref name="other"/>, as an accepted proposal
    /// leaves it, with the three-day wait already over.</summary>
    private static void Engage(Session self, Session other, bool proposee) =>
        self.WithState(() =>
        {
            self.AddLegend($"Engaged to {other.CharName} ({Character.GameDate})", "engaged", 6, 1);
            self.SetEngaged(other.CharName, proposee, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1);
        });

    /// <summary>Whether <paramref name="s"/> is married to <paramref name="partner"/> exactly as the ceremony leaves
    /// it. Called under its monitor.</summary>
    private static bool Married(Session s, string partner) =>
        s.HasLegend("married") && !s.HasLegend("engaged") && s.CharSpouseName == partner && s.CharFiance == ""
        && s.CountItem("love") == 1;

    /// <summary>Fen and her row. Asserted, not assumed: she must be standing on her map in this world.</summary>
    private (Mob Npc, NpcDef Def) ChapelNpc()
    {
        var def = Content.NpcById(ChapelNpcId);
        Assert.True(def is not null && def.Key == "ChapelNpc", $"NPC {ChapelNpcId} is not a Chapel NPC");
        var npc = _fx.World.NpcsNear(def!.Map, def.X, def.Y, 0).FirstOrDefault(m => m.NpcDefId == ChapelNpcId);
        Assert.True(npc is not null, $"{def.Name} is not placed in the world");
        return (npc!, def);
    }

    /// <summary>Click <paramref name="npc"/> (the real 0x43), pick "Marriage" by its label on the wire, and confirm:
    /// the ceremony then asks the fiancé.</summary>
    private static void OpenCeremony(Session s, HeldRecorder o, Mob npc, NpcDef def)
    {
        s.Receive(ClickFrame(npc.Id));
        var labels = LastMenu(o, $"{def.Name}: How can I help you today?").Labels;
        int pick = labels.IndexOf("Marriage") + 1;
        Assert.True(pick > 0, $"no Marriage in [{string.Join(", ", labels)}]");
        s.Receive(MenuFrame((byte)pick));
        LastMenu(o, "Are you certain you wish to devote yourself to this man or woman for life?");
        s.Receive(MenuFrame(1));
    }

    // ===== frames =====================================================================================

    /// <summary>The 0x43 click on an entity: 01 id(u32BE) 00.</summary>
    private static byte[] ClickFrame(uint id) =>
        SessionFixture.Frame(ClientOp.ClickInfo,
            new byte[] { 0x01, (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id, 0x00 });

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

    /// <summary>A submitted input box (0x3A kind 4, step 2 at [8], the length at [10], the text from [11]).</summary>
    private static byte[] InputFrame(string text)
    {
        var t = Encoding.ASCII.GetBytes(text);
        var body = new byte[11 + t.Length];
        body[0] = 0x04;
        body[8] = 0x02;
        body[10] = (byte)t.Length;
        t.CopyTo(body, 11);
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>The last 0x30 menu whose prompt contains <paramref name="prompt"/>: its labels, and whether the
    /// sending thread held the recipient's monitor. After the prompt come the item count and each item as u8 length +
    /// ASCII (<c>Session.SendNpcMenuP</c>).</summary>
    private static (List<string> Labels, bool Held) LastMenu(HeldRecorder o, string prompt)
    {
        byte[] want = Encoding.ASCII.GetBytes(prompt);
        var hit = o.Bodies(ServerOp.NpcDialog).LastOrDefault(f =>
            f.Body.Length > 2 && f.Body[0] == 0x02 && f.Body[1] == 0x02 && f.Body.AsSpan().IndexOf(want) >= 0);
        Assert.True(hit.Body is not null, $"no menu with \"{prompt}\" was sent");
        var body = hit.Body!;
        int promptLen = body[21] << 8 | body[22];
        int i = 23 + promptLen;
        int count = body[i++];
        var labels = new List<string>();
        for (int k = 0; k < count; k++)
        {
            int len = body[i++];
            labels.Add(Encoding.ASCII.GetString(body, i, len));
            i += len;
        }
        return (labels, hit.Held);
    }

    /// <summary>The last 0x30 text box that says exactly <paramref name="text"/>, and whether it was sent inside the
    /// recipient's monitor.</summary>
    private static (byte[] Body, bool Held) LastBox(HeldRecorder o, string text)
    {
        var hit = o.Bodies(ServerOp.NpcDialog).LastOrDefault(f => HeldRecorder.IsBox(f.Body, text));
        Assert.True(hit.Body is not null, $"no box saying \"{text}\" was sent");
        return hit;
    }

    // ===== players ====================================================================================

    /// <summary>A level-50 player on a 100x100 walkable map, whose outbound records whether each frame was handed over
    /// inside their own monitor. <paramref name="shape"/> runs last.</summary>
    private (Session session, HeldRecorder outbound, Character c) Player(string name, ushort map, ushort x, ushort y,
                                                                       Action<Character>? shape = null)
    {
        var c = new Character
        {
            SchemaVersion = Character.CurrentSchemaVersion,
            Id = _fx.World.AllocatePlayerId(),
            Name = $"{name}{Interlocked.Increment(ref _serial)}",
            Map = map,
            X = x,
            Y = y,
            Level = 50,
            MaxHp = 1_000,
            Hp = 1_000,
        };
        Wide(c, map);
        shape?.Invoke(c);
        var o = new HeldRecorder($"held:{c.Name}");
        var s = new Session(o, 2005, _fx.Store, _fx.World, c);
        o.Owner = s;
        _fx.World.EnterMap(s, map);
        o.Clear();
        return (s, o, c);
    }

    private void Leave(ushort map, params Session[] sessions)
    {
        foreach (var s in sessions) _fx.World.LeaveMap(s, map);
    }

    /// <summary>Content-free maps have no size of their own; the cast and walk handlers need one.</summary>
    private static void Wide(Character c, ushort map)
    {
        if (Content.Maps.TryGetValue(map, out var m)) { c.MapXs = m.Xs; c.MapYs = m.Ys; }
        else { c.MapXs = 100; c.MapYs = 100; }
    }

    /// <summary>Run <paramref name="body"/> on a thread of its own, with no synchronization context, and wait for it:
    /// a dialog's awaits then resume inline on the thread that delivers each reply, as on the read loop.</summary>
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

    /// <summary>
    /// An outbound that keeps every frame, as <see cref="RecordingOutbound"/> does, and also whether the thread that
    /// handed it over held the recipient's monitor at that instant (<see cref="Session.StateHeld"/>):
    /// <c>Session.Send</c> hands the finished frame over synchronously on the sending thread. Thread-safe.
    /// </summary>
    private sealed class HeldRecorder : IOutbound
    {
        private readonly List<(byte[] Frame, bool Held)> _frames = new();
        private readonly object _gate = new();

        /// <summary>The session this recorder belongs to: set right after the session is built, before anything sends.</summary>
        internal Session? Owner;

        public HeldRecorder(string remote) => Remote = remote;

        public string Remote { get; }
        public int Capacity => int.MaxValue;
        public int QueueDepth => 0;
        public bool Closed { get; private set; }

        public bool Send(byte[] frame)
        {
            bool held = Owner is not null && Owner.StateHeld;
            lock (_gate) _frames.Add((frame, held));
            return true;
        }

        public void Close() => Closed = true;

        public void Clear()
        {
            lock (_gate) _frames.Clear();
        }

        /// <summary>The decrypted bodies of every frame carrying <paramref name="opcode"/>, in order, each with whether
        /// it was handed over inside the recipient's monitor.</summary>
        public List<(byte[] Body, bool Held)> Bodies(byte opcode)
        {
            (byte[] Frame, bool Held)[] frames;
            lock (_gate) frames = _frames.ToArray();
            var bodies = new List<(byte[], bool)>();
            foreach (var (frame, held) in frames)
            {
                if (!TkPacket.TryParse(frame, out var pkt, out _) || pkt.Opcode != opcode) continue;
                bodies.Add((TkCrypt.Crypt(pkt.Body, pkt.Increment, TkCrypt.LoginKey), held));
            }
            return bodies;
        }

        /// <summary>Every 0x0A mini-text line, as ASCII.</summary>
        public List<string> MiniTexts() =>
            Bodies(ServerOp.MiniText).Where(b => b.Body.Length >= 3)
                .Select(b => Encoding.ASCII.GetString(b.Body, 3, Math.Min((b.Body[1] << 8) | b.Body[2], b.Body.Length - 3)))
                .ToList();

        /// <summary>How many mini-text lines (any channel) say exactly <paramref name="line"/>.</summary>
        public int Count(string line) => MiniTexts().Count(l => l == line);

        /// <summary>Whether any 0x30 menu carried <paramref name="prompt"/>.</summary>
        public bool HasMenu(string prompt) => MenusWith(prompt) > 0;

        /// <summary>How many 0x30 menus carried <paramref name="prompt"/>.</summary>
        public int MenusWith(string prompt)
        {
            byte[] want = Encoding.ASCII.GetBytes(prompt);
            return Bodies(ServerOp.NpcDialog).Count(f =>
                f.Body.Length > 2 && f.Body[0] == 0x02 && f.Body[1] == 0x02 && f.Body.AsSpan().IndexOf(want) >= 0);
        }

        /// <summary>How many 0x30 text boxes start with <paramref name="start"/>.</summary>
        public int BoxesStarting(string start)
        {
            byte[] want = Encoding.ASCII.GetBytes(start);
            return Bodies(ServerOp.NpcDialog).Count(f =>
                f.Body.Length > 23 && f.Body[0] == 0x00 && f.Body[1] == 0x01 && f.Body.AsSpan(23).StartsWith(want));
        }

        /// <summary>Whether a 0x30 body is a text box saying exactly <paramref name="text"/>: the message ends the body,
        /// after its u16BE length.</summary>
        public static bool IsBox(byte[] body, string text)
        {
            byte[] want = Encoding.ASCII.GetBytes(text);
            int at = body.Length - want.Length;
            return at >= 2 && body[0] == 0x00 && body[1] == 0x01 && (body[at - 2] << 8 | body[at - 1]) == want.Length
                   && body.AsSpan(at).SequenceEqual(want);
        }
    }
}
