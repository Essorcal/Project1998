using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// A conversation left waiting is dropped by the next NPC click, NPC speech or floor step
/// (<c>Session.DropWaitingDialog</c>), as RTK frees a waiting script at the same places (<c>sl_async_freeco</c>).
///
/// <para>The 4.95 client sends nothing when an NPC box is closed by its close button or Esc (a static read of the
/// exe, not a live capture). Before this, the prompt behind a closed box stayed pending until a map change, and two
/// things went wrong without a word in the log. The floor triggers that stood down for a pending prompt (the
/// event-cave and Forever Tree entrances, the step encounters) did nothing. And the old flow could still be resumed
/// by a reply that came later, so a charging flow (Baek's join, a shop) could take after the player had gone on to
/// something else. Every fact here but the last two is red on the old code. The last two are controls: an answered
/// conversation and a click with nothing waiting send and take exactly what they did.</para>
///
/// <para>"Left unanswered" is modelled the way the client leaves it: nothing is sent. A reply that arrives after a
/// drop is the real <c>0x3A</c> (or <c>0x39</c>) that a box still on screen would send: the server does not match a
/// reply to its box, so it answers whatever is pending then, or nothing. Each fact runs on a thread of its own, as
/// the read loop does, so a dialog's continuation resumes inline on it.</para>
/// </summary>
[Collection("world")]
public sealed class DialogReleaseTests
{
    private const int BaekNpcId = 119;
    private const int LakeNpcId = 101;      // the Buya Library's librarian, on the event-cave doorway's map (486)
    private const int WalsukNpcId = 52;     // an inn keeper (InnNpc: shop and bank), map 2
    private const int OxNpcId = 57;         // InnNpc2 on the same map: no click entries, so a click opens nothing
    private const int OxKarma = 6;
    private const ushort ForeverTreeMap = 1228;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private static int _serial;

    private readonly SessionFixture _fx;

    public DialogReleaseTests(SessionFixture fx) => _fx = fx;

    // ---- floor triggers ---------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>The event-cave doorway takes a player who left a text page unanswered.</b> The player clicks Lake, gets his
    /// one page, and walks away from it without answering, then steps onto the Buya Library doorway. The waiting
    /// conversation is dropped and the doorway's first page opens; three "next"s later the player is in cave 1.
    /// <para>Red on the old code: the doorway held the player at the from-tile (a 0x04 and nothing else) for as
    /// long as Lake's page was pending, which was until a map change.</para>
    /// </summary>
    [Fact]
    public void TheEventCaveDoorwayOpensAfterAnUnansweredPage()
    {
        var cave = Assert.Single(Content.EventCaves, c => c.Key == "buya_library_caverns");
        var (s, o, c) = Player("CaveWalker", 486, 13, 1, ch => ch.Level = 20);   // cave 1's band, no split menu
        var lake = NpcMob(LakeNpcId);
        try
        {
            using var log = LogLineSink.Acquire();
            OnOwnThread(() =>
            {
                s.Receive(Click(lake));
                Assert.True(s.DialogBusy, "Lake's page is not waiting");
                Assert.StartsWith("Welcome to the great library", Dialogs(o)[^1].Text);

                s.Receive(Walk(0, 13, 1));                           // north onto (13,0), the doorway
                var shown = Dialogs(o);
                Assert.True(shown[^1].Text == cave.Pages[0],
                    $"the doorway did not open; the last box is still \"{shown[^1].Text}\"");

                for (int i = 0; i < cave.Pages.Length; i++) s.Receive(Next());
                Assert.Equal(cave.MapForTier(1), s.CharMap);
            });
            AssertDroppedQuietly(log, c.Name, "event-cave entrance");
        }
        finally { _fx.World.LeaveMap(s, s.CharMap); }
    }

    /// <summary>
    /// <b>The Forever Tree crevasse takes a player who left a shop menu unanswered.</b> A smith's "What would you like
    /// to buy?" menu is waiting when the player steps onto Wilderness (19,91): it is dropped, the crevasse's warning
    /// opens, and its "next" carries the player into the Forever Tree. The smith never comes back: no buy grid is sent.
    /// <para>Red on the old code: the crevasse held the player at the from-tile while the menu was pending.</para>
    /// </summary>
    [Fact]
    public void TheForeverTreeCrevasseOpensAfterAnUnansweredShopMenu()
    {
        var smithDef = Content.Npcs.First(n => n.Key == "SmithNpc" && Placed(n));
        var smith = NpcMob(smithDef.Id);
        var (s, o, c) = Player("TreeWalker", 1002, 19, 92);
        try
        {
            using var log = LogLineSink.Acquire();
            OnOwnThread(() =>
            {
                // Opened inside the player's monitor, as the read loop opens one. Where the smith stands does not
                // matter: nothing about a pending reply depends on it.
                s.WithState(() => _ = new NpcContext(s, smith, smithDef).Buy());
                Assert.True(s.DialogBusy, "the smith's menu is not waiting");
                Assert.Equal((Menu, "What would you like to buy?"), (Dialogs(o)[^1].Kind, Dialogs(o)[^1].Text));

                s.Receive(Walk(0, 19, 92));                          // north onto (19,91), the crevasse
                var shown = Dialogs(o);
                Assert.True(shown[^1].Text.StartsWith("You spot a crevasse", StringComparison.Ordinal),
                    $"the crevasse did not open; the last box is still \"{shown[^1].Text}\"");

                s.Receive(Next());
                Assert.Equal(ForeverTreeMap, s.CharMap);
                Assert.Empty(o.BodiesOf(ServerOp.Shop));             // the smith's buy grid never came
            });
            AssertDroppedQuietly(log, c.Name, "Forever Tree entrance");
        }
        finally { _fx.World.LeaveMap(s, s.CharMap); }
    }

    /// <summary>
    /// <b>A step encounter fires over a waiting page.</b> Baek has refused an unblessed player with a page the player
    /// left unanswered; the Watcher's rabbit then finds them on Buya's field (the step's own roll, chosen as 1). The
    /// page is dropped, the blessing is set and the rabbit's box opens, and Baek's flow never resumes: the next "next"
    /// answers the rabbit.
    /// <para>Red on the old code: the encounter stood down while Baek's page was pending, so no blessing and no box,
    /// and the "next" then finished Baek's flow.</para>
    /// </summary>
    [Fact]
    public void AStepEncounterFiresOverAWaitingPage()
    {
        var (s, o, c) = Player("FieldWalker", NpcSubpathQuest.WatcherMap, 12, 8, ch =>
        {
            ch.Level = 99;
            ch.ClassName = "Rogue";
            ch.Karma = OxKarma;                                      // and not blessed: Baek refuses with a page
        });
        try
        {
            using var log = LogLineSink.Acquire();
            Task<bool>? refusal = null;
            OnOwnThread(() =>
            {
                s.WithState(() => refusal = NpcSubpathAbility.Instance.OnSay(BaekContext(s), "baekho"));
                Assert.Equal(NpcSubpathQuest.NotBlessed, Dialogs(o)[^1].Text);
                Assert.True(s.DialogBusy, "Baek's refusal is not waiting");

                s.WithState(() => s.WatcherStep(1));
                Assert.Equal(1, QuestState.Over(c, QuestState.Registry).Get(NpcSubpathQuest.BlessedReg));
                Assert.Equal(NpcSubpathQuest.WatcherSmiles, Dialogs(o)[^1].Text);

                s.Receive(Next());                                   // answers the rabbit, not Baek
                Assert.False(s.DialogBusy);
            });
            Assert.False(refusal!.IsCompleted, "Baek's dropped flow resumed");
            AssertDroppedQuietly(log, c.Name, "Watcher");
        }
        finally { _fx.World.LeaveMap(s, s.CharMap); }
    }

    // The other four encounters. Each leaves a text page waiting (LeavePage), then runs the player's after-step hooks
    // where they stand (StepHere: OnScriptedTileStep, which HandleWalk runs at the end of every accepted step, so the
    // facts need no walkable tile). Each is red with master's guard put back on its encounter
    // (`if (IsDead || DialogBusy) return;`, `return false;` on the altar): the encounter stands down for the page.

    /// <summary>
    /// <b>The fox finds a player over a waiting page.</b> On a Worn path map, the steps go on until the fox's own
    /// one-in-ten roll comes up (at most <see cref="FoxSteps"/>). The page is dropped and the riddle's input box opens.
    /// A late text-box reply then reaches the riddle as a non-answer: no charm and no throw back to Nagnang. The
    /// page's flow never resumes.
    /// </summary>
    [Fact]
    public void TheFoxFindsAPlayerOverAWaitingPage()
    {
        var (s, o, c) = PlayerMid("FoxWalker", FoxSpirit.Maps[0]);
        try
        {
            using var log = LogLineSink.Acquire();
            Task? page = null;
            OnOwnThread(() =>
            {
                page = LeavePage(s, o);
                for (int i = 0; i < FoxSteps && !Dialogs(o).Any(d => d.Kind == Input); i++) StepHere(s);
                var riddle = Dialogs(o).LastOrDefault(d => d.Kind == Input);
                Assert.True(riddle.Text is not null, $"the fox never came in {FoxSteps} steps while the page waited");
                Assert.Contains(riddle.Text, FoxSpirit.Riddles.Select(r => r.Item1));

                s.Receive(Next());                                   // a text box's reply: not a submit
                Assert.False(s.DialogBusy);
            });
            Assert.Equal(FoxSpirit.Maps[0], s.CharMap);              // a wrong answer would have thrown them out
            Assert.Equal(0, Count(c, FoxSpirit.Charm));
            Assert.False(page!.IsCompleted, "the dropped page's flow resumed");
            AssertDroppedQuietly(log, c.Name, "fox spirit");
        }
        finally { _fx.World.LeaveMap(s, s.CharMap); }
    }

    /// <summary>
    /// <b>The crypt spirit answers Void's task over a waiting page.</b> In Oh-mudum crypt, with Void's mouse zapped and
    /// the spirit not yet met, one step drops the page, sets the "met" flag and opens the spirit's first page; its
    /// pages then run to their end, and the dropped page's flow never resumes.
    /// </summary>
    [Fact]
    public void TheCryptSpiritAppearsOverAWaitingPage()
    {
        var (s, o, c) = PlayerMid("CryptWalker", MageStoneQuest.CryptMap,
            ch => QuestState.Over(ch, MageStoneQuest.StageReg).Set(MageStoneQuest.Zapped("void_mouse"), 1));
        try
        {
            using var log = LogLineSink.Acquire();
            Task? page = null;
            OnOwnThread(() =>
            {
                page = LeavePage(s, o);
                StepHere(s);
                Assert.True(QuestState.Over(c, MageStoneQuest.StageReg).Get(MageStoneQuest.MetGhost) == 1,
                            "the crypt spirit stood down for the waiting page");
                Assert.Equal(MageStoneQuest.SpiritPages[0], Dialogs(o)[^1].Text);

                for (int i = 0; i < MageStoneQuest.SpiritPages.Length; i++) s.Receive(Next());
                Assert.False(s.DialogBusy);
            });
            Assert.False(page!.IsCompleted, "the dropped page's flow resumed");
            AssertDroppedQuietly(log, c.Name, "crypt spirit");
        }
        finally { _fx.World.LeaveMap(s, s.CharMap); }
    }

    /// <summary>
    /// <b>The crow takes Maro's acorn over a waiting page.</b> In Kugnae, sent after the acorn and carrying it, one
    /// step drops the page; the crow takes the acorn, the quest moves on, and the crow's pages open and run to their
    /// end. The dropped page's flow never resumes.
    /// </summary>
    [Fact]
    public void TheCrowStrikesOverAWaitingPage()
    {
        var (s, o, c) = PlayerMid("CrowWalker", DaggerUniformQuest.KugnaeMap, ch =>
        {
            QuestState.Over(ch, DaggerUniformQuest.Key).SetStage(DaggerUniformQuest.Stage.StealAcorn);
            ch.Inventory.Add(new InvItem(0, Item(DaggerUniformQuest.StolenAcorn).Id, 1));
        });
        try
        {
            using var log = LogLineSink.Acquire();
            Task? page = null;
            OnOwnThread(() =>
            {
                page = LeavePage(s, o);
                StepHere(s);
                Assert.True(Count(c, DaggerUniformQuest.StolenAcorn) == 0,
                            "the crow stood down for the waiting page; the acorn is still in the bag");
                Assert.Equal(DaggerUniformQuest.Stage.CrowTookIt, QuestState.Over(c, DaggerUniformQuest.Key).Stage);
                Assert.Equal(DaggerUniformQuest.CrowSnatch[0], Dialogs(o)[^1].Text);

                for (int i = 0; i < DaggerUniformQuest.CrowSnatch.Length; i++) s.Receive(Next());
                Assert.False(s.DialogBusy);
            });
            Assert.False(page!.IsCompleted, "the dropped page's flow resumed");
            AssertDroppedQuietly(log, c.Name, "the crow");
        }
        finally { _fx.World.LeaveMap(s, s.CharMap); }
    }

    /// <summary>
    /// <b>The Gauntlet statue pays over a waiting page.</b> On the ring around the statue in the first tier's
    /// Objective room, with nothing forbidden killed, one step drops the page; the shield and the legend are given and
    /// the statue's pages open and run to their end. The dropped page's flow never resumes.
    /// </summary>
    [Fact]
    public void TheGauntletStatueAnswersOverAWaitingPage()
    {
        const ushort objective = 2549;
        Assert.True(NagnangShieldQuest.IsObjective(objective) && NagnangShieldQuest.AtAltar(8, 3));
        var (s, o, c) = Player("StatueWalker", objective, 8, 3);
        try
        {
            using var log = LogLineSink.Acquire();
            Task? page = null;
            OnOwnThread(() =>
            {
                page = LeavePage(s, o);
                StepHere(s);
                Assert.True(c.Legends.Any(l => l.Name == NagnangShieldQuest.Legend),
                            "the statue stood down for the waiting page; no legend");
                Assert.Equal(1, Count(c, NagnangShieldQuest.Shield));
                Assert.Equal(NagnangShieldQuest.StatueReward[0], Dialogs(o)[^1].Text);

                for (int i = 0; i < NagnangShieldQuest.StatueReward.Length; i++) s.Receive(Next());
                Assert.False(s.DialogBusy);
            });
            Assert.False(page!.IsCompleted, "the dropped page's flow resumed");
            AssertDroppedQuietly(log, c.Name, "Gauntlet altar");
        }
        finally { _fx.World.LeaveMap(s, s.CharMap); }
    }

    // ---- clicks and speech ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Clicking another NPC drops Baek's offer, and he takes nothing.</b> Baek's five-page offer is waiting on its
    /// first page (his takes follow its last page with no prompt in between). The player clicks Ox, an NPC whose click
    /// opens nothing, and then the old page's "next" arrives six times. With the offer dropped no reply resumes it:
    /// no box is sent and the favor, the pelts, the experience and the path all stay. Then the player clicks Walsuk:
    /// his menu opens exactly as it does with nothing waiting, and a further late "next" answers that menu, because a
    /// reply is matched to whatever is pending, not to its box (not changed here).
    /// <para>Red on the old code: the click left the offer pending, and the "next"s ran it to the end, charges
    /// included.</para>
    /// </summary>
    [Fact]
    public void ClickingAnotherNpcDropsBaeksOfferAndHeTakesNothing()
    {
        var (s, o, c) = Player("Clicker", 2, 18, 6, Joiner);
        var ox = NpcMob(OxNpcId);
        var walsuk = NpcMob(WalsukNpcId);
        try
        {
            using var log = LogLineSink.Acquire();
            var before = Holdings(c);
            Task<bool>? offer = null;
            OnOwnThread(() =>
            {
                s.WithState(() => offer = NpcSubpathAbility.Instance.OnSay(BaekContext(s), "baekho"));
                Assert.Equal(NpcSubpathQuest.Offer[0], Dialogs(o)[^1].Text);
                Assert.True(s.DialogBusy, "the offer is not waiting");

                s.Receive(Click(ox));
                int sent = o.BodiesOf(ServerOp.NpcDialog).Count;
                for (int i = 0; i < 6; i++) s.Receive(Next());
                Assert.True(before == Holdings(c) && c.ClassName == "Rogue",
                            $"Baek's offer resumed after the click and took: {before} -> {Holdings(c)}, path {c.ClassName}");
                Assert.Equal(sent, o.BodiesOf(ServerOp.NpcDialog).Count);   // no page of the old flow, ever
                Assert.False(s.DialogBusy);

                s.Receive(Click(walsuk));
                var opened = o.BodiesOf(ServerOp.NpcDialog).Skip(sent).ToList();
                Assert.Single(opened);
                Assert.Equal((Menu, "Walsuk: How can I help you today?"), (Dialogs(o)[^1].Kind, Dialogs(o)[^1].Text));
                Assert.True(s.DialogBusy);

                s.Receive(Next());                                   // late: answers Walsuk's menu (not a pick)
                Assert.False(s.DialogBusy);
                Assert.Equal(sent + 1, o.BodiesOf(ServerOp.NpcDialog).Count);
            });
            Assert.False(offer!.IsCompleted, "Baek's dropped offer resumed");
            Assert.Equal(before, Holdings(c));
            Assert.Equal("Rogue", c.ClassName);
            AssertDroppedQuietly(log, c.Name, "click on 'Ox'");
            Assert.False(log.Has($"waiting on {c.Name} (click on 'Walsuk')"), "a click with nothing waiting logged a drop");
        }
        finally { _fx.World.LeaveMap(s, s.CharMap); }
    }

    /// <summary>
    /// <b>Speech that reaches an NPC drops Baek's offer, and he takes nothing.</b> The player says "baekho" beside Baek
    /// (the real chat packet), leaves the offer's first page, and asks "what is your name?". Baek answers over his
    /// head as before, and the offer is gone: six late "next"s send nothing and take nothing.
    /// <para>Red on the old code: the question did not prompt, so the offer stayed pending and the "next"s ran it to
    /// the end.</para>
    /// </summary>
    [Fact]
    public void SpeechToAnNpcDropsBaeksOfferAndHeTakesNothing()
    {
        var (s, o, c) = Player("Speaker", 30, 3, 5, Joiner);
        try
        {
            using var log = LogLineSink.Acquire();
            var before = Holdings(c);
            OnOwnThread(() =>
            {
                s.Receive(Say("baekho"));
                Assert.Equal(NpcSubpathQuest.Offer[0], Dialogs(o)[^1].Text);
                Assert.True(s.DialogBusy, "the offer is not waiting");

                s.Receive(Say("what is your name?"));
                Assert.Contains(o.BodiesOf(ServerOp.Speech), b => Carries(b, "Hello, my name is Baek."));

                int sent = o.BodiesOf(ServerOp.NpcDialog).Count;
                for (int i = 0; i < 6; i++) s.Receive(Next());
                Assert.True(before == Holdings(c) && c.ClassName == "Rogue",
                            $"Baek's offer resumed after the speech and took: {before} -> {Holdings(c)}, path {c.ClassName}");
                Assert.Equal(sent, o.BodiesOf(ServerOp.NpcDialog).Count);
                Assert.False(s.DialogBusy);
            });
            Assert.Equal(before, Holdings(c));
            Assert.Equal("Rogue", c.ClassName);
            AssertDroppedQuietly(log, c.Name, "speech to an NPC");
        }
        finally { _fx.World.LeaveMap(s, s.CharMap); }
    }

    /// <summary>
    /// <b>A merchant window's flow is dropped too.</b> The player opens Walsuk's buy grid (his menu, then Buy), leaves
    /// it, and clicks Ox. A pick from the old grid that arrives after the click (the real <c>0x39</c>) buys nothing:
    /// the coins stay, no apple, and no grid or box follows.
    /// <para>Red on the old code: the click left the grid's flow waiting, and the late pick bought the apple.</para>
    /// </summary>
    [Fact]
    public void ALatePickInAShopWindowLeftBehindBuysNothing()
    {
        var (s, o, c) = Player("Shopper", 2, 18, 6, ch => ch.Coins = 1000);
        var ox = NpcMob(OxNpcId);
        var walsuk = NpcMob(WalsukNpcId);
        var apple = Content.ItemByKey("apple")!;
        try
        {
            using var log = LogLineSink.Acquire();
            OnOwnThread(() =>
            {
                s.Receive(Click(walsuk));
                s.Receive(MenuPick(1));                              // Buy
                Assert.Single(o.BodiesOf(ServerOp.Shop));            // the inn's one-category grid is up
                Assert.False(s.DialogBusy);                          // a grid waits on 0x39, not on a 0x3A

                s.Receive(Click(ox));
                int dialogs = o.BodiesOf(ServerOp.NpcDialog).Count;
                s.Receive(BuyPick(apple.Name));
                Assert.True(c.Coins == 1000 && c.Inventory.All(i => i.ItemId != apple.Id),
                            $"the old grid's flow resumed and bought: coins {c.Coins}");
                Assert.Single(o.BodiesOf(ServerOp.Shop));
                Assert.Equal(dialogs, o.BodiesOf(ServerOp.NpcDialog).Count);
            });
            AssertDroppedQuietly(log, c.Name, "click on 'Ox'");
        }
        finally { _fx.World.LeaveMap(s, s.CharMap); }
    }

    /// <summary>
    /// <b>F1 drops a shop window left open, as any NPC click does.</b> The player opens Walsuk's buy grid, leaves it,
    /// and presses F1 (the real <c>0x43</c> with the sentinel id <c>FFFFFFFF</c>). The F1 menu opens as it always
    /// does, and a pick from the old grid that arrives after it buys nothing.
    /// <para>Red with F1's drop removed: F1's menu replaces only a pending <c>0x3A</c> prompt, so the grid's flow
    /// stayed waiting and the late pick bought the apple.</para>
    /// </summary>
    [Fact]
    public void F1DropsAShopWindowLeftOpen()
    {
        var (s, o, c) = Player("F1Presser", 2, 18, 6, ch => ch.Coins = 1000);
        var walsuk = NpcMob(WalsukNpcId);
        var apple = Content.ItemByKey("apple")!;
        try
        {
            using var log = LogLineSink.Acquire();
            OnOwnThread(() =>
            {
                s.Receive(Click(walsuk));
                s.Receive(MenuPick(1));                              // Buy
                Assert.Single(o.BodiesOf(ServerOp.Shop));

                s.Receive(F1());
                Assert.Equal((Menu, $"Hello {c.Name}! How can I help you today?"), (Dialogs(o)[^1].Kind, Dialogs(o)[^1].Text));

                s.Receive(BuyPick(apple.Name));
                Assert.True(c.Coins == 1000 && c.Inventory.All(i => i.ItemId != apple.Id),
                            $"the old grid's flow resumed after F1 and bought: coins {c.Coins}");
                Assert.Single(o.BodiesOf(ServerOp.Shop));
                Assert.True(s.DialogBusy, "F1's menu is no longer waiting");
            });
            AssertDroppedQuietly(log, c.Name, "F1");
        }
        finally { _fx.World.LeaveMap(s, s.CharMap); }
    }

    // ---- controls: green before and after ---------------------------------------------------------------------------

    /// <summary>
    /// <b>An answered conversation is unchanged.</b> The player says "baekho" beside Baek and answers every page with
    /// "next": the join runs as it always has (the favor, the pelts and 1,000,000,000 experience taken, the subpath
    /// set, the offer, the study page and the last line shown), and nothing is dropped on the way.
    /// </summary>
    [Fact]
    public void AnAnsweredConversationRunsAsBefore()
    {
        var (s, o, c) = Player("Answerer", 30, 3, 5, Joiner);
        try
        {
            using var log = LogLineSink.Acquire();
            uint exp = c.Exp;
            OnOwnThread(() =>
            {
                s.Receive(Say("baekho"));
                for (int i = 0; i < 20 && s.DialogBusy; i++) s.Receive(Next());
                Assert.False(s.DialogBusy, "Baek was still waiting after twenty answers");
            });
            Assert.Equal(Content.PathName(7), c.ClassName);          // Baekho
            Assert.Equal(exp - NpcSubpathQuest.JoinExp, c.Exp);
            Assert.Equal(0, Count(c, "tigers_favor"));
            Assert.Equal(0, Count(c, NpcSubpathQuest.Pelt));
            Assert.Equal(NpcSubpathQuest.Offer.Append(NpcSubpathQuest.Studied).Append(NpcSubpathQuest.Ready),
                         Dialogs(o).Where(d => d.Kind == Text).Select(d => d.Text));
            Assert.False(log.Has($"waiting on {c.Name} ("), "an answered conversation logged a drop");
        }
        finally { _fx.World.LeaveMap(s, s.CharMap); }
    }

    /// <summary>
    /// <b>A click with nothing waiting sends what it always did:</b> one <c>0x30</c>, the NPC's menu with its five
    /// entries, and no drop.
    /// </summary>
    [Fact]
    public void AClickWithNothingWaitingIsUnchanged()
    {
        var (s, o, c) = Player("FreshClicker", 2, 18, 6);
        var walsuk = NpcMob(WalsukNpcId);
        try
        {
            using var log = LogLineSink.Acquire();
            OnOwnThread(() => s.Receive(Click(walsuk)));
            var body = Assert.Single(o.BodiesOf(ServerOp.NpcDialog));
            Assert.Equal((Menu, "Walsuk: How can I help you today?"), (Dialogs(o)[0].Kind, Dialogs(o)[0].Text));
            Assert.Equal(5, body[23 + ((body[21] << 8) | body[22])]);   // the item count after the prompt
            Assert.True(s.DialogBusy);
            Assert.False(log.Has($"waiting on {c.Name} ("), "a click with nothing waiting logged a drop");
        }
        finally { _fx.World.LeaveMap(s, s.CharMap); }
    }

    // ---- harness ----------------------------------------------------------------------------------------------------

    private const byte Text = 0x00, Menu = 0x02, Input = 0x04;

    /// <summary>After-step runs the fox fact allows: the chance that the fox's one-in-ten roll misses every one of
    /// them is (9/10)^400, about 5e-19.</summary>
    private const int FoxSteps = 400;

    private const string WaitingPage = "A page the player leaves unanswered.";

    private static readonly MethodInfo AfterStep =
        typeof(Session).GetMethod("OnScriptedTileStep", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>The after-step hooks for the tile the player stands on, inside their monitor: what
    /// <c>HandleWalk</c> runs at the end of every accepted step.</summary>
    private static void StepHere(Session s) => s.WithState(() => AfterStep.Invoke(s, null));

    /// <summary>Open one text page on the player, from Lake, inside their monitor as a click would, and leave it
    /// unanswered. Returns its flow, which must never complete once dropped.</summary>
    private Task LeavePage(Session s, RecordingOutbound o)
    {
        var lake = NpcMob(LakeNpcId);
        Task? page = null;
        s.WithState(() => page = new NpcContext(s, lake, Content.NpcById(LakeNpcId)!).Say(WaitingPage));
        Assert.True(s.DialogBusy, "the page is not waiting");
        Assert.Equal(WaitingPage, Dialogs(o)[^1].Text);
        return page!;
    }

    private static string Name(string stem) => $"{stem}{Interlocked.Increment(ref _serial)}";

    /// <summary>A player standing in the middle of <paramref name="map"/>.</summary>
    private (Session S, RecordingOutbound O, Character C) PlayerMid(string stem, ushort map,
                                                                   Action<Character>? configure = null)
    {
        var info = Content.Maps[map];
        return Player(stem, map, (ushort)(info.Xs / 2), (ushort)(info.Ys / 2), configure);
    }

    /// <summary>A socket-free player on <paramref name="map"/>, with the map's size on the character (the walk
    /// handler bounds a step by it) and no other setup unless <paramref name="configure"/> adds some.</summary>
    private (Session S, RecordingOutbound O, Character C) Player(string stem, ushort map, ushort x, ushort y,
                                                                Action<Character>? configure = null)
    {
        var info = Content.Maps[map];
        return _fx.PlayerWith(Name(stem), ch =>
        {
            ch.MapXs = info.Xs;
            ch.MapYs = info.Ys;
            configure?.Invoke(ch);
        }, map, x, y);
    }

    /// <summary>A level-99 Rogue with everything Baek asks for: Ox karma, the blessing, the Dog-linguist legend, a
    /// Tiger's favor, ten Splendid tiger pelts and two billion experience.</summary>
    private static void Joiner(Character c)
    {
        c.Level = 99;
        c.ClassName = "Rogue";
        c.Karma = OxKarma;
        c.Exp = 2_000_000_000;
        QuestState.Over(c, QuestState.Registry).Set(NpcSubpathQuest.BlessedReg, 1);
        c.Legends.Add(new Legend(3, 128, "Dog linguist", NpcSubpathQuest.DogLegend));
        c.Inventory.Add(new InvItem(0, Item("tigers_favor").Id, 1));
        c.Inventory.Add(new InvItem(1, Item(NpcSubpathQuest.Pelt).Id, NpcSubpathQuest.PeltCount));
    }

    private static (int Favor, int Pelts, uint Exp, int Legends) Holdings(Character c) =>
        (Count(c, "tigers_favor"), Count(c, NpcSubpathQuest.Pelt), c.Exp, c.Legends.Count);

    private static ItemDef Item(string key) => Content.ItemByKey(key) ?? throw new InvalidOperationException($"no item '{key}'");
    private static int Count(Character c, string key) => c.Inventory.Where(i => i.ItemId == Item(key).Id).Sum(i => i.Amount);

    private bool Placed(NpcDef def) => _fx.World.NpcsNear(def.Map, def.X, def.Y, 0).Any(m => m.NpcDefId == def.Id);

    private Mob NpcMob(int npcId)
    {
        var def = Content.NpcById(npcId) ?? throw new InvalidOperationException($"no NPC {npcId}");
        var mob = _fx.World.NpcsNear(def.Map, def.X, def.Y, 0).FirstOrDefault(m => m.NpcDefId == npcId);
        Assert.True(mob is not null, $"NPC {npcId} ({def.Name}) is not placed in the world");
        return mob!;
    }

    private NpcContext BaekContext(Session s) => new(s, NpcMob(BaekNpcId), Content.NpcById(BaekNpcId)!);

    /// <summary>Run <paramref name="body"/> on a thread of its own, as the read loop runs a packet, so an awaited
    /// dialog resumes inline on it rather than on the test runner's context.</summary>
    private static void OnOwnThread(Action body)
    {
        Exception? fault = null;
        var t = new Thread(() => fault = Record.Exception(body)) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(Bound), "the fact's thread never finished");
        if (fault is not null) ExceptionDispatchInfo.Capture(fault).Throw();
    }

    /// <summary>The drop happened, and quietly: its one Info line, naming the cause, and no warning or error that
    /// names the player (a flow that threw, or a handler that did).</summary>
    private static void AssertDroppedQuietly(LogLineSink log, string player, string cause)
    {
        Assert.True(log.Has($"dropped the conversation waiting on {player} ({cause})"),
                    $"no drop was logged for {player} ({cause})");
        var loud = log.Lines.Where(l => l.Level != LogLevel.Info && l.Line.Contains(player, StringComparison.Ordinal)).ToList();
        Assert.True(loud.Count == 0, "logged above Info:\n" + string.Join("\n", loud.Select(l => l.Line)));
    }

    // ---- frames: the client's own bytes -------------------------------------------------------------------------------

    /// <summary>A click on an entity (<c>0x43</c>): <c>01 id(u32) 00</c>.</summary>
    private static byte[] Click(Mob npc)
    {
        uint id = npc.Id;
        return SessionFixture.Frame(ClientOp.ClickInfo,
            new byte[] { 0x01, (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id, 0x00 });
    }

    /// <summary>The F1 key: the same <c>0x43</c> with the sentinel id <c>FFFFFFFF</c>.</summary>
    private static byte[] F1() =>
        SessionFixture.Frame(ClientOp.ClickInfo, new byte[] { 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 });

    /// <summary>The text box's next arrow (<c>0x3A</c>): kind 1, step 2 at [8]. The echoed entity id at [1..4] is
    /// left 0; the server does not read it.</summary>
    private static byte[] Next()
    {
        var body = new byte[10];
        body[0] = 0x01;
        body[8] = 0x02;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>A menu pick (<c>0x3A</c>): kind 2, step 2, then 01 and the 1-based row at [10].</summary>
    private static byte[] MenuPick(byte row)
    {
        var body = new byte[12];
        body[0] = 0x02;
        body[8] = 0x02;
        body[9] = 0x01;
        body[10] = row;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>A pick in the buy grid (<c>0x39</c>): the buy tag at [0], the item's name length-prefixed at [7].</summary>
    private static byte[] BuyPick(string name)
    {
        var n = Encoding.ASCII.GetBytes(name);
        var body = new byte[8 + n.Length];
        body[0] = 2;
        body[7] = (byte)n.Length;
        n.CopyTo(body, 8);
        return SessionFixture.Frame(ClientOp.ShopReply, body);
    }

    /// <summary>Plain speech (<c>0x0E</c>): type 0, the length, the text.</summary>
    private static byte[] Say(string text)
    {
        var t = Encoding.ASCII.GetBytes(text);
        var body = new byte[2 + t.Length];
        body[1] = (byte)t.Length;
        t.CopyTo(body, 2);
        return SessionFixture.Frame(ClientOp.Chat, body);
    }

    /// <summary>A walk step (<c>0x06</c>): the direction, the step counter, and the tile the client says it is on.</summary>
    private static byte[] Walk(byte dir, ushort x, ushort y) =>
        SessionFixture.Frame(ClientOp.Walk, new byte[] { dir, 0, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y });

    private readonly record struct Shown(byte Kind, string Text);

    /// <summary>Every NPC box the player was sent (<c>0x30</c>), in order: its kind byte ([0]: 00 text, 02 menu, 04
    /// input) and its message or prompt (u16BE length at [21], ASCII from [23], for the sprite head every box here
    /// carries; Session.WriteHead).</summary>
    private static List<Shown> Dialogs(RecordingOutbound o) =>
        o.BodiesOf(ServerOp.NpcDialog)
         .Where(b => b.Length >= 23 && b[7] == 1)
         .Select(b => new Shown(b[0], Encoding.ASCII.GetString(b, 23, Math.Min((b[21] << 8) | b[22], b.Length - 23))))
         .ToList();

    private static bool Carries(byte[] body, string text)
    {
        var want = Encoding.ASCII.GetBytes(text);
        return body.AsSpan().IndexOf(want) >= 0;
    }
}
