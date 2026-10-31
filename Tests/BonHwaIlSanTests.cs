using System.Reflection;
using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Bon-Hwa's "Il San" branch (<c>Server/BonHwa.cs</c>, RTK <c>NPCs/wilderness/bon_hwa.lua</c>), and <c>@mark</c>
/// giving no stats (the project owner's decision sheet of 2026-10-24, items 1-4 and 21).
///
/// <para><b>The path.</b> Each player stands beside Bon-Hwa on map 1229 and talks to him through the real
/// <c>0x43</c> click and <c>0x3A</c> replies, on a thread of its own with no synchronization context, so every
/// await of the dialog resumes inline on the thread that delivers the reply, as on the read loop. <c>@mark</c> and
/// <c>@lvl</c> go in as framed <c>0x0E</c> chat, through the tier gate and the state monitor.</para>
///
/// <para><b>Written to run against the base too.</b> The legend keys, lines and item key are literals rather than
/// the ability's constants, and the mark setter is reached by reflection, so this file compiles on the commit
/// before the change (473789e) and fails there on its own assertions: the menu has no "Il San", the trials and
/// the promotion are unreachable, the setter is missing, and <c>@mark 1</c> adds a level of growth (Might 102
/// and AC 0 for a Warrior, against 101 and 1 at <c>@lvl 99</c>). The menu theory's two "not offered" rows are
/// the negative controls and pass on both.</para>
/// </summary>
[Collection("world")]
public sealed class BonHwaIlSanTests
{
    private const string Greeting = "Hello! How can I help you today?";
    private const string TrialPrompt = "Please select a trial.";
    private const string Knowledge = "passed_first_trial_of_knowledge";
    private const string Strength = "passed_first_trial_of_strength";
    private const string Wealth = "passed_first_trial_of_wealth";
    private const string FirstMark = "attained_first_mark";
    private const string Amber = "well_crafted_white_amber";

    private readonly SessionFixture _fx;

    public BonHwaIlSanTests(SessionFixture fx)
    {
        _fx = fx;
        EnsureGmRoster();
    }

    // ===== the menu ===========================================================================================

    /// <summary>"Il San" joins the two services for a mark-0 player with 160,000 base Vitality or 80,000 base Mana
    /// (RTK bon_hwa.lua:29-30), and for nobody else: not one point short of both, and not a mark-1 player, who is
    /// offered nothing new either (no "Ee San": that branch is a later slice).</summary>
    [Theory]
    [InlineData(160_000u, 0u, 0, true)]
    [InlineData(80_000u, 80_000u, 0, true)]
    [InlineData(159_999u, 79_999u, 0, false)]
    [InlineData(320_000u, 160_000u, 1, false)]
    public void IlSanIsOfferedOnlyToAQualifyingMarkZeroPlayer(uint vita, uint mana, int mark, bool offered)
    {
        var (s, o, _) = Player($"IlSanMenu{vita}m{mark}", c => { c.MaxHp = vita; c.MaxMp = mana; c.Mark = (byte)mark; });

        List<string> labels = new();
        Drive(() =>
        {
            Click(s);
            labels = LastMenu(o, Greeting);
            s.Receive(MenuFrame(0));
        });

        var expected = offered
            ? new List<string> { "Bon-Hwa Immortality", "Shadow Stats", "Il San" }
            : new List<string> { "Bon-Hwa Immortality", "Shadow Stats" };
        Assert.Equal(expected, labels);
        Assert.False(s.DialogBusy, "the greeting did not close");
    }

    /// <summary>The trial menu lists only the trials whose legend the player does not hold yet (RTK :249-257).</summary>
    [Fact]
    public void TheTrialMenuListsOnlyTheTrialsNotYetPassed()
    {
        var (s, o, _) = Player("IlSanTrialList", c => c.Legends.Add(new Legend(3, 15, "Passed First Trial of Knowledge (x)", Knowledge)));

        List<string> trials = new();
        Drive(() =>
        {
            Click(s);
            PickIlSan(s, o);
            trials = LastMenu(o, TrialPrompt);
            s.Receive(MenuFrame(0));
        });

        Assert.Equal(new List<string> { "First Trial of Strength", "First Trial of Wealth" }, trials);
    }

    // ===== Knowledge =========================================================================================

    /// <summary>Knowledge takes 1,200,000,000 experience and grants its legend (icon 3, colour 15), then
    /// congratulates (RTK :261-294).</summary>
    [Fact]
    public void KnowledgeTakesTheExperienceAndGrantsItsLegend()
    {
        var (s, o, c) = Player("IlSanKnowPass", c => c.Exp = 1_300_000_000);

        Drive(() =>
        {
            Click(s);
            PickIlSan(s, o);
            PickTrial(s, o, "First Trial of Knowledge");
            Assert.Equal(new List<string> { "Yes", "No" },
                LastMenu(o, "To complete this trial, you must sacrifice to me 1,200,000,000 (1.2 Billion) experience. Do you wish to do so?"));
            s.Receive(MenuFrame(1));
            Assert.Equal(1, Shown(o, "Congratulations! You have completed the First Trial of Knowledge."));
            s.Receive(NextFrame());
        });

        Assert.Equal(100_000_000u, c.Exp);
        AssertLegend(c, Knowledge, 3, $"Passed First Trial of Knowledge ({Character.GameDate})");
        Assert.False(s.DialogBusy);
    }

    /// <summary>Knowledge refuses a player one point short ("Come back when you have enough experience.") and a
    /// "No" ("Come back when you are serious."), taking nothing and granting nothing either way (RTK :268-298).</summary>
    [Theory]
    [InlineData(1, "Come back when you have enough experience.")]
    [InlineData(2, "Come back when you are serious.")]
    public void KnowledgeRefusesWithoutTakingAnything(byte answer, string line)
    {
        var (s, o, c) = Player($"IlSanKnowRefuse{answer}", c => c.Exp = 1_199_999_999);

        Drive(() =>
        {
            Click(s);
            PickIlSan(s, o);
            PickTrial(s, o, "First Trial of Knowledge");
            s.Receive(MenuFrame(answer));
            Assert.Equal(1, Shown(o, line));
            s.Receive(NextFrame());
        });

        Assert.Equal(1_199_999_999u, c.Exp);
        Assert.DoesNotContain(c.Legends, l => l.Name == Knowledge);
    }

    // ===== Strength ==========================================================================================

    /// <summary>Strength reads the lifetime kill count (RTK <c>killCount("spirit_rat")</c>, :308): a Spirit Rat
    /// killed at any time passes, with the legend (icon 1, colour 15) and the congratulation; none killed gets
    /// "You still need to slay the Spirit Rat." and no legend. Both first read the trial's own page (:300-306).</summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void StrengthPassesOnASpiritRatKilledAtAnyTime(int kills, bool passes)
    {
        var (s, o, c) = Player($"IlSanStrength{kills}", c => { if (kills > 0) c.Kills["spirit_rat"] = kills; });

        Drive(() =>
        {
            Click(s);
            PickIlSan(s, o);
            PickTrial(s, o, "First Trial of Strength");
            Assert.Equal(1, Shown(o, "For this trial, I will need you to kill the pesky Spirit Rat who lurks in the Rat Cave. Return to me when you have done so."));
            s.Receive(NextFrame());
            Assert.Equal(passes ? 1 : 0, Shown(o, "Congratulations! You have completed the First Trial of Strength."));
            Assert.Equal(passes ? 0 : 1, Shown(o, "You still need to slay the Spirit Rat."));
            s.Receive(NextFrame());
        });

        if (passes) AssertLegend(c, Strength, 1, $"Passed First Trial of Strength ({Character.GameDate})");
        else Assert.DoesNotContain(c.Legends, l => l.Name == Strength);
        Assert.False(s.DialogBusy);
    }

    // ===== Wealth ============================================================================================

    /// <summary>Wealth takes 600,000 coins and 5 Well Crafted White Ambers and grants its legend (icon 1, colour
    /// 15), with no line (RTK :337-365 says nothing on success).</summary>
    [Fact]
    public void WealthTakesTheCoinsAndTheAmbersAndGrantsItsLegend()
    {
        var (s, o, c) = Player("IlSanWealthPass", c => c.Coins = 700_000);
        GiveAmbers(s, 6);

        Drive(() =>
        {
            Click(s);
            PickIlSan(s, o);
            PickTrial(s, o, "First Trial of Wealth");
            Assert.Equal(new List<string> { "Yes", "No" },
                LastMenu(o, "To complete this trial, you must sacrifice to me 600,000 coins and 5 Well Crafted White ambers. Do you wish to do so?"));
            s.Receive(MenuFrame(1));
        });

        Assert.Equal(100_000u, c.Coins);
        Assert.Equal(1, s.CountItem(Amber));
        AssertLegend(c, Wealth, 1, $"Passed First Trial of Wealth ({Character.GameDate})");
        Assert.False(s.DialogBusy, "the Wealth trial should end with no line");
    }

    /// <summary>A player short of the coins, or holding fewer than 5 ambers, is refused with RTK's line (:339-356)
    /// and keeps everything. No amber has a source in this world, so the second case is where every player stops:
    /// nobody can finish Il san in play until crafting exists (the project owner's item 2, "A, as RTK").</summary>
    [Theory]
    [InlineData(599_999u, 5, "Return to me when you have the gold.")]
    [InlineData(600_000u, 4, "Return to me when you have the 5 well crafted white ambers.")]
    [InlineData(600_000u, 0, "Return to me when you have the 5 well crafted white ambers.")]
    public void WealthRefusesAPlayerWithoutTheCoinsOrTheAmbers(uint coins, int ambers, string line)
    {
        var (s, o, c) = Player($"IlSanWealthRefuse{coins}a{ambers}", c => c.Coins = coins);
        if (ambers > 0) GiveAmbers(s, ambers);

        Drive(() =>
        {
            Click(s);
            PickIlSan(s, o);
            PickTrial(s, o, "First Trial of Wealth");
            s.Receive(MenuFrame(1));
            Assert.Equal(1, Shown(o, line));
            s.Receive(NextFrame());
        });

        Assert.Equal(coins, c.Coins);
        Assert.Equal(ambers, s.CountItem(Amber));
        Assert.DoesNotContain(c.Legends, l => l.Name == Wealth);
    }

    // ===== the promotion =====================================================================================

    /// <summary>With all three legends, "Il San" promotes (RTK :216-247): the class animation over the player, the
    /// three legends exchanged for "Attained First Mark (date)" (icon 32, colour 15), mark 1 on the same path, the
    /// status resent, and "[SYSTEM]: name has attained First Mark!" to everyone online, on the system channel
    /// (0x0A type 5, RTK clif_broadcast_sub). Nothing else moves: not the level, not a stat, not the spellbook. The
    /// conversation ends there rather than re-offering the trials it just consumed.</summary>
    [Theory]
    [InlineData("Warrior", 83)]
    [InlineData("Rogue", 82)]
    [InlineData("Mage", 81)]
    [InlineData("Poet", 80)]
    public void ThreeLegendsPromoteToMarkOneAndNothingElse(string cls, int animation)
    {
        string name = $"IlSanPromote{cls}";
        var (s, o, c) = Player(name, c =>
        {
            c.ClassName = cls;
            c.Legends.Add(new Legend(3, 15, "Passed First Trial of Knowledge (x)", Knowledge));
            c.Legends.Add(new Legend(1, 15, "Passed First Trial of Strength (x)", Strength));
            c.Legends.Add(new Legend(1, 15, "Passed First Trial of Wealth (x)", Wealth));
        });
        var (_, observer) = _fx.Player($"IlSanWitness{cls}");
        var before = (c.Level, c.ClassName, c.MaxHp, c.MaxMp, c.Might, c.Grace, c.Will, c.Ac, c.Exp, c.Coins, c.Spells.Count);

        Drive(() =>
        {
            Click(s);
            int ilSan = LastMenu(o, Greeting).IndexOf("Il San") + 1;
            Assert.True(ilSan > 0, "the greeting did not offer Il San");
            o.Clear();                    // from here on, only what the pick sends
            s.Receive(MenuFrame((byte)ilSan));
        });

        Assert.Equal(1, c.Mark);
        Assert.Equal(before, (c.Level, c.ClassName, c.MaxHp, c.MaxMp, c.Might, c.Grace, c.Will, c.Ac, c.Exp, c.Coins, c.Spells.Count));
        Assert.DoesNotContain(c.Legends, l => l.Name is Knowledge or Strength or Wealth);
        AssertLegend(c, FirstMark, 32, $"Attained First Mark ({Character.GameDate})");
        Assert.False(s.DialogBusy, "the promotion should end the conversation");

        Assert.Contains(o.BodiesOf(ServerOp.Effect), b => b.Length >= 5 && ReadU32(b) == c.Id && b[4] == animation);
        Assert.NotEmpty(o.BodiesOf(ServerOp.Stats));
        string line = $"[SYSTEM]: {name} has attained First Mark!";
        Assert.Equal(1, SystemLines(observer).Count(l => l == line));
        Assert.Equal(1, SystemLines(o).Count(l => l == line));
    }

    /// <summary>Two legends of three do not promote: "Il San" offers the missing trial and the mark stays 0.</summary>
    [Fact]
    public void TwoLegendsOfThreeDoNotPromote()
    {
        var (s, o, c) = Player("IlSanTwoOfThree", c =>
        {
            c.Legends.Add(new Legend(3, 15, "Passed First Trial of Knowledge (x)", Knowledge));
            c.Legends.Add(new Legend(1, 15, "Passed First Trial of Strength (x)", Strength));
        });

        List<string> trials = new();
        Drive(() =>
        {
            Click(s);
            PickIlSan(s, o);
            trials = LastMenu(o, TrialPrompt);
            s.Receive(MenuFrame(0));
        });

        Assert.Equal(new List<string> { "First Trial of Wealth" }, trials);
        Assert.Equal(0, c.Mark);
        Assert.Contains(c.Legends, l => l.Name == Knowledge);
        Assert.DoesNotContain(c.Legends, l => l.Name == FirstMark);
    }

    /// <summary>The pick is read again after the greeting's await (#55): a player whose mark changed while the
    /// greeting was open (here to 2, as a GM's <c>@mark</c> would) and who then picks "Il San" from it is not
    /// promoted, so the mark is not lowered to 1 and the legends stay.</summary>
    [Fact]
    public void AnIlSanPickFromAStaleGreetingPromotesNobody()
    {
        var (s, o, c) = Player("IlSanStalePick", c =>
        {
            c.Legends.Add(new Legend(3, 15, "Passed First Trial of Knowledge (x)", Knowledge));
            c.Legends.Add(new Legend(1, 15, "Passed First Trial of Strength (x)", Strength));
            c.Legends.Add(new Legend(1, 15, "Passed First Trial of Wealth (x)", Wealth));
        });

        Drive(() =>
        {
            Click(s);
            int ilSan = LastMenu(o, Greeting).IndexOf("Il San") + 1;
            Assert.True(ilSan > 0, "the greeting did not offer Il San");
            s.WithState(() => c.Mark = 2);
            s.Receive(MenuFrame((byte)ilSan));
        });

        Assert.Equal(2, c.Mark);
        Assert.Equal(3, c.Legends.Count(l => l.Name is Knowledge or Strength or Wealth));
        Assert.DoesNotContain(c.Legends, l => l.Name == FirstMark);
        Assert.False(s.DialogBusy);
    }

    /// <summary>A second click while a trial's question waits orphans that conversation (<c>Session.AwaitReply</c>
    /// replaces the pending reply), and the orphan never resumes, so it takes nothing: the answer goes to the new
    /// greeting instead. This is the existing guard #55 names; the trials need no busy gate on top of it because
    /// each reads what it takes after its last await.</summary>
    [Fact]
    public void ASecondClickOrphansAWaitingTrialAndNothingIsTaken()
    {
        var (s, o, c) = Player("IlSanOrphan", c => c.Exp = 1_300_000_000);

        Drive(() =>
        {
            Click(s);
            PickIlSan(s, o);
            PickTrial(s, o, "First Trial of Knowledge");
            o.Clear();
            Click(s);                     // a new greeting replaces the waiting Yes/No
            LastMenu(o, Greeting);
            s.Receive(MenuFrame(0));      // answered: the greeting closes; the orphaned question never resumes
        });

        Assert.Equal(1_300_000_000u, c.Exp);
        Assert.DoesNotContain(c.Legends, l => l.Name == Knowledge);
        Assert.False(s.DialogBusy);
    }

    // ===== the mark setter ===================================================================================

    /// <summary>The NPC mark setter refuses a mark above <see cref="Content.MaxMark"/> or below 0, changing nothing,
    /// and sets one within it, keeping the path.</summary>
    [Fact]
    public void TheNpcMarkSetterRefusesAnythingPastMaxMark()
    {
        var (s, _, c) = Player("IlSanSetter", _ => { });
        var (npc, def) = BonHwa();
        var ctx = new NpcContext(s, npc, def);

        bool above = true, below = true, within = false;
        s.WithState(() =>
        {
            above = SetMark(ctx, Content.MaxMark + 1);
            below = SetMark(ctx, -1);
        });
        Assert.False(above);
        Assert.False(below);
        Assert.Equal(0, c.Mark);

        s.WithState(() => within = SetMark(ctx, Content.MaxMark));
        Assert.True(within);
        Assert.Equal(Content.MaxMark, c.Mark);
        Assert.Equal("Warrior", c.ClassName);
    }

    // ===== @mark =============================================================================================

    /// <summary><c>@mark n</c> gives exactly what <c>@lvl 99</c> gives in every deterministic stat (Might, Grace,
    /// Will and AC), whatever n is: a rank adds no growth (the project owner's item 3, "A, no stats, as RTK"). It still sets the
    /// mark and keeps level 99. Vita and mana are re-rolled by every rebuild, so they get only a sanity bound: the
    /// range 98 level-up rolls of the class curve (<c>PathGrowth.csv</c>) can give. That bound cannot tell 98 rolls
    /// from 99 (with the old extra LevelUp per rank every value still fell inside it); the deterministic
    /// Might/Grace/Will/AC check is the guard.</summary>
    [Theory]
    [InlineData("Warrior")]
    [InlineData("Mage")]
    public void MarkAddsNoStatsOverLevelNinetyNine(string cls)
    {
        var (gm, _, c) = _fx.PlayerWith(GmName, ch => { ch.ClassName = cls; ch.Level = 99; ch.Mark = 0; });

        Run(gm, "@lvl 99");
        var level99 = (c.Might, c.Grace, c.Will, c.Ac);
        Assert.Equal((sbyte)1, c.Ac);

        foreach (int mark in new[] { 1, 2, 3 })
        {
            Run(gm, $"@mark {mark}");
            Assert.Equal(mark, c.Mark);
            Assert.Equal(99, c.Level);
            Assert.Equal(level99, (c.Might, c.Grace, c.Will, c.Ac));
            var g = Content.PathGrowthFor(Content.PathBaseOf(Content.PathIdForClass(cls)));
            Assert.InRange(c.MaxHp, 45u + 98u * (uint)g.HpMin, 55u + 98u * (uint)(g.HpMax - 1));
            Assert.InRange(c.MaxMp, 32u + 98u * (uint)g.MpMin, 36u + 98u * (uint)(g.MpMax - 1));
        }
    }

    // ===== helpers ===========================================================================================

    private const string GmName = "cmdgm";   // the roster name every GM-command class writes (see GmExpCommandTests)
    private static bool _rosterWritten;

    private static void EnsureGmRoster()
    {
        lock (TestProcessState.Gate)
        {
            if (_rosterWritten) return;
            Directory.CreateDirectory(TestProcessState.StateDirectory);
            File.WriteAllText(Path.Combine(TestProcessState.StateDirectory, "gm_accounts.txt"), GmName + "\n");
            StaffAccounts.Load();
            _rosterWritten = true;
        }
    }

    /// <summary>A level-99 Warrior with 160,000 base Vitality standing beside Bon-Hwa, shaped further by
    /// <paramref name="configure"/>.</summary>
    private (Session Session, RecordingOutbound Outbound, Character Character) Player(string name, Action<Character> configure)
    {
        var (_, def) = BonHwa();
        var (s, o, c) = _fx.PlayerWith(name, ch =>
        {
            ch.ClassName = "Warrior";
            ch.Level = 99;
            ch.Mark = 0;
            ch.MaxHp = 160_000;
            ch.MaxMp = 1_000;
            configure(ch);
        }, map: def.Map, x: def.X, y: (ushort)(def.Y + 1));
        o.Clear();
        return (s, o, c);
    }

    private (Mob Npc, NpcDef Def) BonHwa()
    {
        var def = Content.Npcs.FirstOrDefault(n => n.Key == "BonHwaNpc");
        Assert.True(def is not null, "no BonHwaNpc row");
        var npc = _fx.World.NpcsNear(def!.Map, def.X, def.Y, 0).FirstOrDefault(m => m.NpcDefId == def.Id);
        Assert.True(npc is not null, "Bon-Hwa is not placed in the world");
        return (npc!, def);
    }

    private static void GiveAmbers(Session s, int count)
    {
        bool given = false;
        s.WithState(() => given = s.GiveRewardItem(Amber, count));
        Assert.True(given, "the ambers were not given");
    }

    private void Click(Session s)
    {
        uint id = BonHwa().Npc.Id;
        s.Receive(SessionFixture.Frame(ClientOp.ClickInfo,
            new byte[] { 0x01, (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id, 0x00 }));
    }

    private static void PickIlSan(Session s, RecordingOutbound o)
    {
        int i = LastMenu(o, Greeting).IndexOf("Il San") + 1;
        Assert.True(i > 0, "the greeting did not offer Il San");
        s.Receive(MenuFrame((byte)i));
    }

    private static void PickTrial(Session s, RecordingOutbound o, string trial)
    {
        int i = LastMenu(o, TrialPrompt).IndexOf(trial) + 1;
        Assert.True(i > 0, $"the trial menu did not offer {trial}");
        s.Receive(MenuFrame((byte)i));
    }

    private static bool SetMark(NpcContext ctx, int mark)
    {
        var setter = typeof(NpcContext).GetMethod("SetMark", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(setter is not null, "NpcContext has no mark setter");
        return (bool)setter!.Invoke(ctx, new object[] { mark })!;
    }

    private static void AssertLegend(Character c, string name, byte icon, string text)
    {
        var legend = Assert.Single(c.Legends, l => l.Name == name);
        Assert.Equal((icon, (byte)15, text), (legend.Icon, legend.Color, legend.Text));
    }

    /// <summary>The 0x0A lines of type 5, the system channel (<c>Session.SystemAnnounce</c>).</summary>
    private static List<string> SystemLines(RecordingOutbound o) =>
        o.BodiesOf(ServerOp.MiniText)
         .Where(b => b.Length >= 3 && b[0] == 5)
         .Select(b => Encoding.ASCII.GetString(b, 3, Math.Min((b[1] << 8) | b[2], b.Length - 3)))
         .ToList();

    private static uint ReadU32(byte[] b) => (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);

    /// <summary>The newest menu whose prompt contains <paramref name="prompt"/>, as its labels (the 0x30 menu body:
    /// kind 2, sub 2, …, the prompt, a count, then each label as u8 length + ASCII).</summary>
    private static List<string> LastMenu(RecordingOutbound o, string prompt)
    {
        byte[] tail = Encoding.ASCII.GetBytes(prompt);
        var body = o.BodiesOf(ServerOp.NpcDialog).LastOrDefault(bd =>
            bd.Length > 2 && bd[0] == 0x02 && bd[1] == 0x02 && bd.AsSpan().IndexOf(tail) >= 0);
        Assert.True(body is not null, $"no menu \"{prompt}\" was sent");
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

    /// <summary>How many NPC text boxes said exactly <paramref name="text"/> (the message ends the body, after its
    /// u16BE length).</summary>
    private static int Shown(RecordingOutbound o, string text)
    {
        byte[] want = Encoding.ASCII.GetBytes(text);
        return o.BodiesOf(ServerOp.NpcDialog).Count(body =>
        {
            int at = body.Length - want.Length;
            return at >= 2 && (body[at - 2] << 8 | body[at - 1]) == want.Length && body.AsSpan(at).SequenceEqual(want);
        });
    }

    private static byte[] NextFrame()
    {
        var body = new byte[11];
        body[0] = 0x01;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    private static byte[] MenuFrame(byte index)
    {
        var body = new byte[11];
        body[0] = 0x02;
        body[10] = index;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>A command the way the read loop delivers one: a framed 0x0E chat packet.</summary>
    private static void Run(Session session, string command)
    {
        var text = Encoding.ASCII.GetBytes(command);
        var body = new byte[2 + text.Length];
        body[1] = (byte)text.Length;
        text.CopyTo(body, 2);
        session.Receive(SessionFixture.Frame(0x0E, body));
    }

    /// <summary>Run a dialog on a thread of its own, which has no synchronization context: its awaits then resume
    /// inline on the thread that delivers each reply, as on the read loop.</summary>
    private static void Drive(Action body)
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
