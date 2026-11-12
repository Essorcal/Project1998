using System.Diagnostics;
using System.Reflection;
using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The Geomancer spells slice (the project owner, 2026-10-26, P5 "each spell as its RTK script", P1 and P3 "on sale once it
/// works, at its script's price"; 2026-11-12, Exhume's five stand-in creatures, the recognitions' Remove from the target, and
/// Reveal fate's outcomes on the target; the Insult ruling's creature timer for Soften skin and Earths Spite; the smaller
/// calls as RTK under P5). Before, thirteen Geomancer rows fell to the <c>generic</c> verb, 5 mana and "You cast X." and
/// nothing else (<c>Tests/Fixtures/spell-dispatch-routes.txt</c> had them on <c>verb=generic</c>).
///
/// <para>The scripts: <c>RTK-Server/rtklua/Accepted/Spells/Subpaths/Geomancer/</c>, and the NPC spells Good earth's trap
/// casts (<c>Spells/NPCs/sanctuary.lua</c>, <c>harden_armor.lua</c>, <c>valor.lua</c>) and Reveal fate's dispel
/// (<c>Spells/NPCs/dispell.lua</c>). Each spell is bound by its <c>game-data/SpellParams.csv</c> row to the Geomancer region of
/// <c>game-data/spell_verbs.lua</c> (Read karma to Diviner's <c>read_karma</c>); Good earth's trap is <c>World</c>'s (the Good
/// earth block) and <c>Session.SpringGoodEarth</c>.</para>
///
/// <para>Every cast is the real 0x0F frame through <c>Session.Receive</c>, by a fresh level-99 Geomancer facing east, on
/// content-free maps 63700-63731 (no Maps.csv row: no terrain, PvP off) and on Frosted Arena (3049), a PvP map no other
/// class's facts use. A player steps on a trap by a real walk frame; a creature by the mob tick's committed step. A menu, an
/// input box or a box is answered from a thread of its own with no synchronisation context, as a read loop answers it, so the
/// step resumes inline as on the server.</para>
/// </summary>
[Collection("world")]
public sealed class GeomancerSpellTests
{
    private const ushort RevealMap = 63700, RevealRefuseMap = 63701, RevealGoodMap = 63702, RevealBadMap = 63703,
                         RevealTopMap = 63704, RevealFaintMap = 63705, RevealHeldMap = 63706, HonorMap = 63707,
                         HonorRemoveMap = 63708, BalanceMap = 63709, InnerMap = 63710, NeutralMap = 63711, ElementMap = 63712,
                         RecogRefuseMap = 63713, RecogHeldMap = 63714, ExhumeMap = 63715, ExhumeBeatMap = 63716,
                         ExhumeDropMap = 63717, PotionMap = 63718, PotionRefuseMap = 63719, MasteryMap = 63720,
                         EarthMap = 63721, EarthBeatMap = 63722, EarthStepMap = 63723, EarthCreatureMap = 63724,
                         GoneMap = 63725, SoftenMap = 63726, SoftenRefuseMap = 63727, StandInMap = 63728, InnerBoxMap = 63729,
                         FateHeldMap = 63730, Arena = 3049;
    private const ushort CX = 30, CY = 30;
    private const uint Mana = 400_000, Hp = 10_000;

    /// <summary>Slack on a wait, a run or a status read straight after the cast.</summary>
    private const long SlackMs = 5_000;

    private static readonly FieldInfo NextActionTick =
        typeof(Session).GetField("_nextActionTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ActionCount =
        typeof(Session).GetField("_actionCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo StatusFlagsField =
        typeof(Session).GetField("_statusFlags", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo TicksField =
        typeof(Session).GetField("_spellTicks", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo PublishTickDue =
        typeof(Session).GetMethod("PublishSpellTickDue", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo RevealMethod =
        typeof(Session).GetMethod("RevealableTrapsNear", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo MorphLookField =
        typeof(Session).GetField("_morphLook", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo MorphColorField =
        typeof(Session).GetField("_morphColor", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo MorphUntilField =
        typeof(Session).GetField("_morphUntil", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo AetherField =
        typeof(Session).GetField("_aether", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ReplacedField =
        typeof(Session).GetField("_replaced", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo LeavingField =
        typeof(Session).GetField("_leaving", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly SessionFixture _fx;
    private static int _serial;

    public GeomancerSpellTests(SessionFixture fx) => _fx = fx;

    // ===== Reveal fate ==========================================================================================

    /// <summary>The cast asks "Whom would you like to reveal their fate?" in an input box with the Woodland Angel's portrait
    /// (look 49), with no line, nothing spent and no wait yet; there is no "Complete but disabled." box (the project owner's
    /// answer). A name, typed in any case, takes 100 mana and arms the 7,500 s wait, then RTK's menu "Greetings Geomancer. How
    /// is the Chi aligned here?" with no portrait. Inside the wait, "Reveal fate isn't ready yet."; short of 100, "Your will is
    /// too weak.", free and no box. Red on the base (generic: 5 mana, no box).</summary>
    [Fact]
    public void RevealFateAsksANameThenSpendsAndOffersTheChi()
    {
        var (target, _, _) = Player(RevealMap, CX + 9, CY, name: Letters("Fated"));
        var (caster, cOut, cc) = Caster("reveal_fate", RevealMap, CX, CY);
        var (poor, pOut, pc) = Caster("reveal_fate", RevealMap, CX, CY + 4, mana: 99);
        try
        {
            List<Box> asked = new();
            string castLines = "", menu = "";
            uint before = 0;
            List<(int Head, int Gfx)> heads = new();
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                asked = Boxes(cOut);
                castLines = Lines(cOut);
                before = cc.Mp;
                cOut.Clear();
                caster.Receive(InputFrame(target.CharName.ToUpperInvariant()));
                menu = string.Join(" / ", Menus(cOut));
                heads = MenuHeads(cOut);
            });
            long wait = SpellCastSupport.AetherLeft(caster, "reveal_fate");
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            poor.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(asked is [{ Kind: 0x0404, Head: 1, Gfx: 0x8000 | 49, Color: 0 } a] && a.Text == "Whom would you like to reveal their fate?"
                        && castLines == "" && before == Mana && cc.Mp == Mana - 100 && wait > 7_500_000 - SlackMs && wait <= 7_500_000
                        && menu == "Greetings Geomancer. How is the Chi aligned here?|It is disrupted|It flows well"
                        && heads is [(0, 0)] && Lines(cOut) == "Reveal fate isn't ready yet."
                        && Lines(pOut) == "Your will is too weak." && Boxes(pOut).Count == 0 && pc.Mp == 99,
                        $"asked [{string.Join(" / ", asked)}], lines [{castLines}], mana {before} then {cc.Mp}, wait {wait}, menu [{menu}] " +
                        $"heads [{string.Join(",", heads)}]; again [{Lines(cOut)}]; poor [{Lines(pOut)}] boxes {Boxes(pOut).Count} mana {pc.Mp}");
        }
        finally { Leave(RevealMap, target, caster, poor); }
    }

    /// <summary>The name's refusals, each a box with the Angel's portrait and no button that ends it, for free (no mana, no
    /// wait, no menu): anything but letters, spaces and an apostrophe, "You can only enter letters and spaces."; nobody online
    /// by it, "That player is currently not online."; the caster, "You cannot reveal your own fate.". Red on the base.</summary>
    [Fact]
    public void RevealFateNameRefusals()
    {
        var casters = Enumerable.Range(0, 3).Select(i => Caster("reveal_fate", RevealRefuseMap, CX, CY + 4 * i)).ToList();
        try
        {
            string[] typed = { "Bob1", "Nobodyhereatall", casters[2].session.CharName };
            string[] want = { "You can only enter letters and spaces.", "That player is currently not online.", "You cannot reveal your own fate." };
            var problems = new List<string>();
            OnOwnThread(() =>
            {
                for (int i = 0; i < 3; i++)
                {
                    var (s, o, c) = casters[i];
                    s.Receive(SpellCastSupport.CastFrame(0));
                    o.Clear();
                    s.Receive(InputFrame(typed[i]));
                    var boxes = Boxes(o);
                    if (boxes is not [{ Kind: 0x0001, Head: 1, Gfx: 0x8000 | 49, Next: 0 } b] || b.Text != want[i] || c.Mp != Mana
                        || SpellCastSupport.AetherLeft(s, "reveal_fate") != 0 || Menus(o).Count != 0)
                        problems.Add($"{typed[i]}: [{string.Join(" / ", boxes)}] mana {c.Mp} menus {Menus(o).Count}");
                }
            });
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }
        finally { Leave(RevealRefuseMap, casters.Select(c => c.session).ToArray()); }
    }

    /// <summary>Every outcome of a sign lands on the NAMED player, none on the caster (the project owner's answer, "Outcomes
    /// on the target"), each as its line reads, over repeated casts at a married player until each has come up (nine good,
    /// eleven bad): the armour colour 5 or 0; the 8 h "creative" duration; the dispel (a buff they held is gone); 10,000 gold
    /// added or taken, with its line; karma +0.25 or -0.25, with RTK's line; their weaving progress +5 or -5, with its line;
    /// a quarter of their level's band of experience given (AwardExp's line too) or taken, with its line; base vita +25 or
    /// -25; the friendship legends and the marriage legends, the others of each group taken off. After each the caster's
    /// "Performed N Ba Gua experiences" counts on, and the player's "Experienced the Ba Gua N times, assisted by &lt;caster&gt;
    /// (&lt;date&gt;)" counts THEIR tally, not the caster's (which is 50 here and untouched); and the player gets one box with
    /// the outcome's words and the Angel's portrait. The caster's gold, karma, experience and vita never move. Red on the
    /// base (generic), and with the outcomes on the caster (the script as written).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryOutcomeOfASignLandsOnTheNamedPlayer(bool good)
    {
        ushort map = good ? RevealGoodMap : RevealBadMap;
        int path = Content.PathIdForClass("Warrior");
        uint here = Content.ExpToNext(path, 50), prev = Content.ExpToNext(path, 49);
        uint quarter = (uint)Math.Ceiling((here - prev) * 0.25);
        var (target, tOut, tc) = Player(map, CX + 3, CY, name: Letters("Fated"), configure: ch =>
        {
            ch.ClassName = "Warrior";
            ch.Spouse = "Somebody";
            QuestState.Over(ch, QuestState.Registry).Set("experienced_the_ba_gua", 3);
        });
        var (caster, cOut, cc) = Caster("reveal_fate", map, CX, CY, configure: ch =>
        {
            ch.Coins = 7_000; ch.Karma = 2;
            QuestState.Over(ch, QuestState.Registry).Set("experienced_the_ba_gua", 50);
        });
        uint casterExp = cc.Exp, casterVita = cc.MaxHp;
        string[] friends = { "needs_to_work_on_relationships", "proven_to_be_good_friend" };
        string[] marriages = { "married_for_items", "married_for_power", "married_for_money", "married_for_love" };
        var want = good
            ? new[] { "You stand out from those around you.", "You feel creative.", "Your coinpurse feels heavier.", "The gods smile upon you.",
                      "You're better in your specialized skill.", "You gain knowledge towards the next level.", "You are able to endure more.",
                      "You're a good friend, and it shows.", "Married for love" }
            : new[] { "You feel that people do not notice you.", "You lose your train of thought and fail to be creative.",
                      "Your coinpurse feels lighter.", "The gods frown upon you.", "You're worse in your specialized skill.",
                      "You lose knowledge towards the next level.", "You become weak and a bit faint.", "You feel you have let a friend down.",
                      "Married for items", "Married for power", "Married for money" };
        var seen = new HashSet<string>();
        var problems = new List<string>();
        string date = Character.GameDate;
        try
        {
            for (int i = 0; i < 700 && seen.Count < want.Length && problems.Count == 0; i++)
            {
                target.WithState(() =>
                {
                    tc.ArmorColor = 7; tc.Coins = 50_000; tc.Karma = 0; tc.MaxHp = Hp; tc.Hp = Hp; tc.Exp = prev + quarter;
                    QuestState.Over(tc, QuestState.Registry).Set("weaving", 20);
                    ((Dictionary<string, long>)StatusFlagsField.GetValue(target)!).Remove("creative");
                    tc.Legends.RemoveAll(l => friends.Contains(l.Name) || marriages.Contains(l.Name));
                    foreach (var k in friends.Concat(marriages)) tc.Legends.Add(new Legend(1, 1, k, k));
                    target.ReceiveCurse("armor", 1, 600_000, "fate_probe", "Probe", "");
                });
                int tallyBefore = QuestState.Over(tc, QuestState.Registry).Get("experienced_the_ba_gua");
                int performedBefore = QuestState.Over(cc, QuestState.Registry).Get("performed_ba_gua_experiences");
                SpellCastSupport.ClearAether(caster, "reveal_fate");
                FreeSlot(caster);
                tOut.Clear();
                OnOwnThread(() =>
                {
                    caster.Receive(SpellCastSupport.CastFrame(0));
                    caster.Receive(InputFrame(target.CharName));
                    cOut.Clear();
                    caster.Receive(MenuFrame(good ? (byte)2 : (byte)1));
                });
                var boxes = Boxes(tOut);
                string lines = Lines(tOut);
                if (boxes is not [{ Kind: 0x0001, Head: 1, Gfx: 0x8000 | 49, Color: 0, Next: 0 } box])
                { problems.Add($"#{i}: player's boxes [{string.Join(" / ", boxes)}]"); break; }
                string o = box.Text;
                if (!want.Contains(o)) { problems.Add($"#{i}: an outcome of the other sign, \"{o}\""); break; }
                seen.Add(o);
                bool ok = o switch
                {
                    "You stand out from those around you." => tc.ArmorColor == 5,
                    "You feel that people do not notice you." => tc.ArmorColor == 0,
                    "You feel creative." => StatusLeft(target, "creative") is > 28_800_000 - SlackMs and <= 28_800_000,
                    "You lose your train of thought and fail to be creative." => SpellCastSupport.BuffEntry(target, "fate_probe") is null,
                    "Your coinpurse feels heavier." => tc.Coins == 60_000 && lines.Contains("10,000 gold added."),
                    "Your coinpurse feels lighter." => tc.Coins == 40_000 && lines.Contains("10,000 gold removed."),
                    "The gods smile upon you." => tc.Karma == 0.25 && lines.Contains("Your karma rises slightly."),
                    "The gods frown upon you." => tc.Karma == -0.25 && lines.Contains("Your karma decreases."),
                    "You're better in your specialized skill." => Reg(tc, "weaving") == 25 && lines.Contains("You have gained some progress in weaving"),
                    "You're worse in your specialized skill." => Reg(tc, "weaving") == 15 && lines.Contains("You have lost some progress in weaving"),
                    "You gain knowledge towards the next level." => tc.Exp == prev + 2 * quarter && tc.Level == 50
                                                                   && lines.Contains($"{quarter:N0} experience! | You have gained some experience!"),
                    "You lose knowledge towards the next level." => tc.Exp == prev && lines.Contains("You have lost some experience!"),
                    "You are able to endure more." => tc.MaxHp == Hp + 25,
                    "You become weak and a bit faint." => tc.MaxHp == Hp - 25,
                    "You're a good friend, and it shows." => OneOf(tc, friends, "proven_to_be_good_friend", "Proven to be a good friend", 6, 1),
                    "You feel you have let a friend down." => OneOf(tc, friends, "needs_to_work_on_relationships", "Needs to work on relationships", 1, 1),
                    _ => OneOf(tc, marriages, o.ToLowerInvariant().Replace(' ', '_'), o, 6, 1),
                };
                int tally = QuestState.Over(tc, QuestState.Registry).Get("experienced_the_ba_gua");
                int performed = QuestState.Over(cc, QuestState.Registry).Get("performed_ba_gua_experiences");
                var theirs = tc.Legends.Where(l => l.Name == "experienced_the_ba_gua").Select(l => (l.Text, l.Icon, l.Color)).ToList();
                var mine = cc.Legends.Where(l => l.Name == "performed_ba_gua_experiences").Select(l => (l.Text, l.Icon, l.Color)).ToList();
                bool tallies = tally == tallyBefore + 1 && performed == performedBefore + 1
                               && QuestState.Over(cc, QuestState.Registry).Get("experienced_the_ba_gua") == 50
                               && theirs.SequenceEqual(new[] { ($"Experienced the Ba Gua {tally} times, assisted by {caster.CharName} ({date})", (byte)145, (byte)128) })
                               && mine.SequenceEqual(new[] { ($"Performed {performed} Ba Gua experiences", (byte)145, (byte)128) });
                bool casterSame = cc.Coins == 7_000 && cc.Karma == 2 && cc.Exp == casterExp && cc.MaxHp == casterVita && Lines(cOut) == "";
                if (!ok || !tallies || !casterSame)
                    problems.Add($"#{i} \"{o}\": effect {ok} (armour {tc.ArmorColor}, coins {tc.Coins}, karma {tc.Karma}, weaving " +
                                 $"{Reg(tc, "weaving")}, exp {tc.Exp} of {prev}+{quarter}, level {tc.Level}, vita {tc.MaxHp}, lines [{lines}]); " +
                                 $"tallies {tallies} ({tallyBefore}->{tally}, {performedBefore}->{performed}, [{string.Join(" / ", theirs)}], " +
                                 $"[{string.Join(" / ", mine)}]); caster unchanged {casterSame} (coins {cc.Coins}, karma {cc.Karma}, " +
                                 $"exp {cc.Exp}, vita {cc.MaxHp}, lines [{Lines(cOut)}])");
            }
            Assert.True(problems.Count == 0 && seen.Count == want.Length,
                        $"{string.Join("\n", problems)}\nseen {seen.Count} of {want.Length}: missing [{string.Join(" / ", want.Except(seen))}]");
        }
        finally { Leave(map, target, caster); }
    }

    /// <summary>At level 99 the experience outcomes work on what the named player holds, as written: a quarter of it given
    /// ("You gain knowledge..."), half of it taken ("You lose knowledge..."), each rounded up. Red on the base.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AtNinetyNineTheExperienceOutcomesWorkOnWhatTheyHold(bool good)
    {
        int y = good ? CY : CY + 6;
        var (target, tOut, tc) = Player(RevealTopMap, CX + 3, y, name: Letters("Topped"), configure: ch => { ch.ClassName = "Warrior"; ch.Level = 99; });
        var (caster, _, _) = Caster("reveal_fate", RevealTopMap, CX, y);
        string outcome = good ? "You gain knowledge towards the next level." : "You lose knowledge towards the next level.";
        try
        {
            bool found = false;
            uint after = 0;
            for (int i = 0; i < 400 && !found; i++)
            {
                target.WithState(() => tc.Exp = 1_000_001);
                SpellCastSupport.ClearAether(caster, "reveal_fate");
                FreeSlot(caster);
                tOut.Clear();
                OnOwnThread(() =>
                {
                    caster.Receive(SpellCastSupport.CastFrame(0));
                    caster.Receive(InputFrame(target.CharName));
                    caster.Receive(MenuFrame(good ? (byte)2 : (byte)1));
                });
                if (Boxes(tOut) is [{ } b] && b.Text == outcome) { found = true; after = tc.Exp; }
            }
            Assert.True(found && after == (good ? 1_000_001u + 250_001u : 1_000_001u - 500_001u),
                        $"found {found}, exp after {after}");
        }
        finally { Leave(RevealTopMap, target, caster); }
    }

    /// <summary>"You become weak and a bit faint." at a player with less than 25 base vita ends the script there, as
    /// written: their vita stays, no tally moves on either side and no box goes out. Red on the base.</summary>
    [Fact]
    public void AFaintPlayerWithLessThanTwentyFiveBaseVitaEndsTheScript()
    {
        var (target, tOut, tc) = Player(RevealFaintMap, CX + 3, CY, hp: 20, maxHp: 20, name: Letters("Frail"), configure: ch => ch.ClassName = "Warrior");
        var (caster, _, cc) = Caster("reveal_fate", RevealFaintMap, CX, CY);
        try
        {
            bool found = false;
            int tally = -1, performed = -1, tallyAfter = -1, performedAfter = -1;
            for (int i = 0; i < 400 && !found; i++)
            {
                target.WithState(() => { tc.MaxHp = 20; tc.Hp = 20; });
                tally = QuestState.Over(tc, QuestState.Registry).Get("experienced_the_ba_gua");
                performed = QuestState.Over(cc, QuestState.Registry).Get("performed_ba_gua_experiences");
                SpellCastSupport.ClearAether(caster, "reveal_fate");
                FreeSlot(caster);
                tOut.Clear();
                OnOwnThread(() =>
                {
                    caster.Receive(SpellCastSupport.CastFrame(0));
                    caster.Receive(InputFrame(target.CharName));
                    caster.Receive(MenuFrame(1));
                });
                if (Boxes(tOut).Count == 0)
                {
                    found = true;
                    tallyAfter = QuestState.Over(tc, QuestState.Registry).Get("experienced_the_ba_gua");
                    performedAfter = QuestState.Over(cc, QuestState.Registry).Get("performed_ba_gua_experiences");
                }
            }
            Assert.True(found && tc.MaxHp == 20 && tallyAfter == tally && performedAfter == performed,
                        $"found {found}, vita {tc.MaxHp}, tally {tally}->{tallyAfter}, performed {performed}->{performedAfter}");
        }
        finally { Leave(RevealFaintMap, target, caster); }
    }

    /// <summary>Reveal fate's outcome is written inside the named player's own monitor: while another thread holds it the
    /// caster's pick waits and the player's gold, karma, armour colour, legends, vita, experience, weaving progress, creative
    /// duration and tally are untouched (so each of the eight good outcomes would show), in both <c>StateRank</c> orders. Red
    /// with <c>Session.ReceiveFate</c>'s monitor entry taken out (Release).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RevealFateWritesTheNamedPlayerInsideTheirOwnMonitor(bool casterRanksFirst)
    {
        int y = casterRanksFirst ? CY : CY + 6;
        Session caster, target;
        Character tc;
        if (casterRanksFirst)
        {
            (caster, _, _) = Caster("reveal_fate", RevealHeldMap, CX, y);
            (target, _, tc) = Player(RevealHeldMap, CX + 1, y, name: Letters("Held"), configure: Fated);
        }
        else
        {
            (target, _, tc) = Player(RevealHeldMap, CX + 1, y, name: Letters("Held"), configure: Fated);
            (caster, _, _) = Caster("reveal_fate", RevealHeldMap, CX, y);
        }
        Assert.Equal(casterRanksFirst, caster.StateRank < target.StateRank);
        var run = new HeldRun();
        try
        {
            OnOwnThread(() => { caster.Receive(SpellCastSupport.CastFrame(0)); caster.Receive(InputFrame(target.CharName)); });
            (uint coins, double karma, byte armour, int legends, uint vita, uint exp) before = default;
            target.WithState(() => before = (tc.Coins, tc.Karma, tc.ArmorColor, tc.Legends.Count, tc.MaxHp, tc.Exp));
            WhileAnotherThreadHolds(run, target, () => caster.Receive(MenuFrame(2)),
                                    untouched: () => (tc.Coins, tc.Karma, tc.ArmorColor, tc.Legends.Count, tc.MaxHp, tc.Exp) == before
                                                     && Reg(tc, "experienced_the_ba_gua") == 0 && Reg(tc, "weaving") == 20
                                                     && !((Dictionary<string, long>)StatusFlagsField.GetValue(target)!).ContainsKey("creative"));
            Assert.Null(run.HolderFault);
            Assert.Null(run.ActorFault);
            Assert.False(run.FinishedWhileHeld, "the pick finished while another thread held the player's monitor");
            Assert.True(run.UntouchedWhileHeld, "the player's fate landed while another thread held their monitor");
            Assert.True(Reg(tc, "experienced_the_ba_gua") == 1, "the fate never landed");
        }
        finally { if (run.Settled) Leave(RevealHeldMap, caster, target); }
    }

    /// <summary>Each of Reveal fate's outcomes that writes the player's own fields directly (the war paint, the creative
    /// duration, the gold, the karma, the experience, the base vita) does it inside the player's own monitor: with the
    /// player resolved by name, the outcome's primitive, run under the caster's monitor as a cast runs it, waits while
    /// another thread holds the player's, and the player is untouched, in both <c>StateRank</c> orders. (The end-to-end fact
    /// above draws one outcome at random; three outcomes, the dispel, the legends and the crafting key, also enter the
    /// monitor further in, so only these six tell <c>ReceiveFate</c>'s own entry apart.) Red with
    /// <c>Session.ReceiveFate</c>'s monitor entry taken out (Release).</summary>
    [Theory]
    [InlineData("armour", true)] [InlineData("armour", false)]
    [InlineData("creative", true)] [InlineData("creative", false)]
    [InlineData("gold", true)] [InlineData("gold", false)]
    [InlineData("karma", true)] [InlineData("karma", false)]
    [InlineData("exp", true)] [InlineData("exp", false)]
    [InlineData("vita", true)] [InlineData("vita", false)]
    public void EachFateOutcomeWritesInsideTheNamedPlayersMonitor(string outcome, bool casterRanksFirst)
    {
        int y = 4 + 3 * (Interlocked.Increment(ref _serial) % 30);
        Session caster, target;
        Character tc;
        if (casterRanksFirst)
        {
            (caster, _, _) = Caster("reveal_fate", FateHeldMap, CX, y);
            (target, _, tc) = Player(FateHeldMap, CX + 1, y, name: Letters("Held"), configure: Fated);
        }
        else
        {
            (target, _, tc) = Player(FateHeldMap, CX + 1, y, name: Letters("Held"), configure: Fated);
            (caster, _, _) = Caster("reveal_fate", FateHeldMap, CX, y);
        }
        Assert.Equal(casterRanksFirst, caster.StateRank < target.StateRank);
        var fate = Content.SpellByKey("reveal_fate")!;
        caster.WithState(() => Assert.True(caster.LuaResolveNamedPcTarget(fate, target.CharName)));
        (uint coins, double karma, byte armour, uint vita, uint exp) before = default;
        target.WithState(() => before = (tc.Coins, tc.Karma, tc.ArmorColor, tc.MaxHp, tc.Exp));
        bool Untouched() => (tc.Coins, tc.Karma, tc.ArmorColor, tc.MaxHp, tc.Exp) == before
                            && !((Dictionary<string, long>)StatusFlagsField.GetValue(target)!).ContainsKey("creative");
        var run = new HeldRun();
        try
        {
            WhileAnotherThreadHolds(run, target, () => caster.WithState(() =>
            {
                bool landed = outcome switch
                {
                    "armour" => caster.LuaTargetArmorColor(5),
                    "creative" => caster.LuaTargetDuration("creative", 28_800_000),
                    "gold" => caster.LuaTargetGold(10_000, "10,000 gold added."),
                    "karma" => caster.LuaTargetKarma(0.25),
                    "exp" => caster.LuaTargetExp(0.25, 0.25, "You have gained some experience!"),
                    _ => caster.LuaTargetBaseVita(25),
                };
                Assert.True(landed, $"{outcome} did not land");
            }), untouched: Untouched);
            Assert.Null(run.HolderFault);
            Assert.Null(run.ActorFault);
            Assert.False(run.FinishedWhileHeld, $"{outcome} finished while another thread held the player's monitor");
            Assert.True(run.UntouchedWhileHeld, $"{outcome} landed while another thread held the player's monitor");
            bool after = false;
            target.WithState(() => after = !Untouched());
            Assert.True(after, $"{outcome} never landed");
        }
        finally { if (run.Settled) Leave(FateHeldMap, caster, target); }
    }

    /// <summary>A Warrior with a weaving progress of 20, so every good outcome changes something a fact can read.</summary>
    private static void Fated(Character ch)
    {
        ch.ClassName = "Warrior";
        QuestState.Over(ch, QuestState.Registry).Set("weaving", 20);
    }

    // ===== the recognitions =====================================================================================

    /// <summary>Honor of the Three Sects spends 100 and arms its 5 s wait before the question, which comes in an input box
    /// with the Staff of chi's icon (convertGraphic(803, "item")), as do its menus and boxes. Each recognition counts the
    /// NAMED player's saved tally one more and writes their legend "&lt;recognition&gt; N times, recognized by &lt;caster&gt;"
    /// in the script's icon and colour; the Elemental Arts legend counts the player's tally, not the caster's (the project
    /// owner's answer; the caster's is 50 here). The caster reads "&lt;player&gt; has been recognized for
    /// \"&lt;recognition&gt;\"". Red on the base (generic).</summary>
    [Theory]
    [InlineData(1, "practiced_the_elemental_arts", "Practiced the Elemental Arts", 76, 5)]
    [InlineData(2, "balanced_the_flow_of_qi", "Balanced the flow of Qi", 122, 61)]
    [InlineData(3, "studied_with_the_sages", "Studied with the Sages", 145, 61)]
    public void HonorOfTheThreeSectsCountsTheNamedPlayersTally(byte pick, string key, string text, int icon, int color)
    {
        int y = CY + 4 * pick;
        var (target, _, tc) = Player(HonorMap, CX + 5, y, name: Letters("Honored"), configure: ch => QuestState.Over(ch, QuestState.Registry).Set(key, 3));
        var (caster, cOut, cc) = Caster("honor_of_the_three_sects", HonorMap, CX, y, configure: ch => QuestState.Over(ch, QuestState.Registry).Set(key, 50));
        try
        {
            List<Box> asked = new(), mine = new();
            List<(int Head, int Gfx)> heads = new();
            string menu = "";
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                asked = Boxes(cOut);
                cOut.Clear();
                caster.Receive(InputFrame(target.CharName));
                menu = string.Join(" / ", Menus(cOut));
                heads = MenuHeads(cOut);
                cOut.Clear();
                caster.Receive(MenuFrame(pick));
                mine = Boxes(cOut);
            });
            int chi = Content.ItemByKey("staff_of_chi")!.ClientIcon;
            long wait = SpellCastSupport.AetherLeft(caster, "honor_of_the_three_sects");
            var legend = tc.Legends.Where(l => l.Name == key).Select(l => (l.Text, (int)l.Icon, (int)l.Color)).ToList();
            Assert.True(cc.Mp == Mana - 100 && wait > 5_000 - SlackMs && wait <= 5_000
                        && asked is [{ Kind: 0x0404, Head: 2 } a] && a.Gfx == chi && a.Text == "Whom is it that has caught your eye?"
                        && menu == "What do you wish to bestow on them?|Elemental Arts recognition|Flow of Qi recognition|Sage Study recognition|Remove recognition"
                        && heads.Count == 1 && heads[0] == (2, chi)
                        && Reg(tc, key) == 4 && Reg(cc, key) == 50
                        && legend.SequenceEqual(new[] { ($"{text} 4 times, recognized by {caster.CharName}", icon, color) })
                        && mine is [{ Kind: 0x0001, Head: 2, Next: 0 } m] && m.Gfx == chi && m.Text == $"{target.CharName} has been recognized for \"{text}\"",
                        $"spent {Mana - cc.Mp}, wait {wait}, asked [{string.Join(" / ", asked)}], menu [{menu}] heads [{string.Join(",", heads)}] " +
                        $"(chi {chi}); tallies {Reg(tc, key)} / caster {Reg(cc, key)}; legend [{string.Join(" / ", legend)}]; box [{string.Join(" / ", mine)}]");
        }
        finally { Leave(HonorMap, target, caster); }
    }

    /// <summary>"Remove recognition" lists the NAMED player's marks (not the caster's) under "Which recognition would you
    /// like to remove?" and takes the picked one off the named player (the project owner's answer); the caster's own mark of
    /// the same name stays; the tally stays; the caster reads "&lt;player&gt; has had their recognition removed for
    /// &lt;mark&gt;". Red on the base, and with the remove on the caster (the script as written).</summary>
    [Fact]
    public void HonorsRemoveTakesTheMarkOffTheNamedPlayer()
    {
        var (target, _, tc) = Player(HonorRemoveMap, CX + 5, CY, name: Letters("Marked"), configure: ch =>
        {
            ch.Legends.Add(new Legend(122, 61, "Balanced the flow of Qi 2 times, recognized by Someone", "balanced_the_flow_of_qi"));
            ch.Legends.Add(new Legend(145, 61, "Studied with the Sages 1 times, recognized by Someone", "studied_with_the_sages"));
            QuestState.Over(ch, QuestState.Registry).Set("studied_with_the_sages", 1);
        });
        var (caster, cOut, cc) = Caster("honor_of_the_three_sects", HonorRemoveMap, CX, CY, configure: ch =>
            ch.Legends.Add(new Legend(76, 5, "Practiced the Elemental Arts 9 times, recognized by Someone", "practiced_the_elemental_arts")));
        cc.Legends.Add(new Legend(145, 61, "Studied with the Sages 4 times, recognized by Someone", "studied_with_the_sages"));
        try
        {
            string offered = "";
            List<Box> mine = new();
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                caster.Receive(InputFrame(target.CharName));
                cOut.Clear();
                caster.Receive(MenuFrame(4));
                offered = string.Join(" / ", Menus(cOut));
                cOut.Clear();
                caster.Receive(MenuFrame(2));
                mine = Boxes(cOut);
            });
            Assert.True(offered == "Which recognition would you like to remove?|Balanced the flow of Qi|Studied with the Sages"
                        && tc.Legends.All(l => l.Name != "studied_with_the_sages") && tc.Legends.Any(l => l.Name == "balanced_the_flow_of_qi")
                        && cc.Legends.Any(l => l.Name == "studied_with_the_sages") && Reg(tc, "studied_with_the_sages") == 1
                        && mine is [{ Kind: 0x0001 } m] && m.Text == $"{target.CharName} has had their recognition removed for Studied with the Sages",
                        $"offered [{offered}], theirs [{string.Join(", ", tc.Legends.Select(l => l.Name))}], caster's " +
                        $"[{string.Join(", ", cc.Legends.Select(l => l.Name))}], box [{string.Join(" / ", mine)}]");
        }
        finally { Leave(HonorRemoveMap, target, caster); }
    }

    /// <summary>Balance recognition: "Keeper recognition" writes "Keeper of Balance, Marked by &lt;caster&gt;" (icon 11,
    /// colour 1) on the named player; "Disruptor recognition" then replaces it with "Disruptor of Balance, Marked by
    /// &lt;caster&gt;" (11, 4); each with its box. Remove takes the mark off the named player. Red on the base.</summary>
    [Fact]
    public void BalanceMarksKeeperOrDisruptorAndRemovesFromTheNamedPlayer()
    {
        var (target, _, tc) = Player(BalanceMap, CX + 5, CY, name: Letters("Balanced"));
        var (caster, cOut, _) = Caster("balance_recognition", BalanceMap, CX, CY);
        try
        {
            List<Box> keeperBox = new(), disruptorBox = new(), removedBox = new();
            List<(string, int, int)> afterKeeper = new(), afterDisruptor = new();
            string offered = "";
            void Pick(params byte[] picks)
            {
                SpellCastSupport.ClearAether(caster, "balance_recognition");
                FreeSlot(caster);
                caster.Receive(SpellCastSupport.CastFrame(0));
                caster.Receive(InputFrame(target.CharName));
                for (int i = 0; i < picks.Length; i++)
                {
                    if (i == picks.Length - 1) cOut.Clear();
                    else if (i == 1) { }
                    caster.Receive(MenuFrame(picks[i]));
                    if (i == 0 && picks.Length > 1) offered = string.Join(" / ", Menus(cOut));
                }
            }
            List<(string, int, int)> Balance() =>
                tc.Legends.Where(l => l.Name is "keeper_of_balance" or "disruptor_of_balance").Select(l => (l.Text, (int)l.Icon, (int)l.Color)).ToList();
            OnOwnThread(() =>
            {
                Pick(1); keeperBox = Boxes(cOut); afterKeeper = Balance();
                Pick(2); disruptorBox = Boxes(cOut); afterDisruptor = Balance();
                cOut.Clear();
                Pick(3, 1); removedBox = Boxes(cOut);
            });
            string me = caster.CharName, them = target.CharName;
            Assert.True(afterKeeper.SequenceEqual(new[] { ($"Keeper of Balance, Marked by {me}", 11, 1) })
                        && keeperBox is [{ } k] && k.Text == $"{them} has been recognized as a \"Keeper of Balance\""
                        && afterDisruptor.SequenceEqual(new[] { ($"Disruptor of Balance, Marked by {me}", 11, 4) })
                        && disruptorBox is [{ } d] && d.Text == $"{them} has been recognized as a \"Disruptor of Balance\""
                        && Balance().Count == 0 && removedBox is [{ } r] && r.Text == $"{them} has had their recognition removed for Disruptor of Balance",
                        $"keeper [{string.Join(" / ", afterKeeper)}] [{string.Join(" / ", keeperBox)}]; disruptor [{string.Join(" / ", afterDisruptor)}] " +
                        $"[{string.Join(" / ", disruptorBox)}]; offered [{offered}]; after remove [{string.Join(" / ", Balance())}] [{string.Join(" / ", removedBox)}]");
        }
        finally { Leave(BalanceMap, target, caster); }
    }

    /// <summary>Inner sect recognition: a training and a level ("Wu Xing", "Sage") write "Wu Xing Sage, Chosen by
    /// &lt;caster&gt; (&lt;date&gt;)" (icon 76, colour 5) on the named player, the training's other levels gone; the caster's box
    /// with the next arrow, "&lt;player&gt; has been recognized as a Wu Xing Sage", and once they answer it one box with no
    /// button to the player, "You have been recognized as a Wu Xing Sage", with the Staff of chi's icon, which drops the
    /// conversation the player had waiting (as Scouting trip's player box, the binding lesson). Remove lists the named
    /// player's trainings, not the caster's, and takes every level of the picked one off them. Red on the base, and with the
    /// remove reading the caster's marks.</summary>
    [Fact]
    public void InnerSectRecognizesATrainingAndTellsTheNamedPlayer()
    {
        var (target, tOut, tc) = Player(InnerMap, CX + 5, CY, name: Letters("Sect"), configure: ch =>
            ch.Legends.Add(new Legend(76, 5, "Wu Xing Initiate, Chosen by Someone (Yuri 1, Spring)", "wu_xing_initiate")));
        var (caster, cOut, cc) = Caster("inner_sect_recognition", InnerMap, CX, CY, configure: ch =>
            ch.Legends.Add(new Legend(122, 20, "Tiaoli Master, Chosen by Someone (Yuri 1, Spring)", "tiaoli_master")));
        try
        {
            string date = Character.GameDate;
            int chi = Content.ItemByKey("staff_of_chi")!.ClientIcon;
            List<string> menus = new();
            List<Box> mine = new(), before = new(), theirs = new(), removed = new();
            string offered = "";
            bool waiting = false;
            List<(string, int, int)> legend = new();
            OnOwnThread(() =>
            {
                _ = target.DlgInputPush(0, 0, "a conversation the player has waiting");
                caster.Receive(SpellCastSupport.CastFrame(0));
                caster.Receive(InputFrame(target.CharName));
                caster.Receive(MenuFrame(1));
                caster.Receive(MenuFrame(2));                    // Wu Xing
                menus = Menus(cOut);
                cOut.Clear();
                tOut.Clear();
                caster.Receive(MenuFrame(4));                    // Sage
                mine = Boxes(cOut);
                before = Boxes(tOut);
                waiting = target.DialogBusy;
                legend = tc.Legends.Where(l => l.Name.StartsWith("wu_xing_")).Select(l => (l.Text, (int)l.Icon, (int)l.Color)).ToList();
                caster.Receive(NextFrame());
                theirs = Boxes(tOut);
                SpellCastSupport.ClearAether(caster, "inner_sect_recognition");
                FreeSlot(caster);
                caster.Receive(SpellCastSupport.CastFrame(0));
                caster.Receive(InputFrame(target.CharName));
                cOut.Clear();
                caster.Receive(MenuFrame(2));
                offered = string.Join(" / ", Menus(cOut));
                cOut.Clear();
                caster.Receive(MenuFrame(1));
                removed = Boxes(cOut);
            });
            Assert.True(menus.Count >= 3 && menus[^2] == "Which specialized training?|Wu Wei|Wu Xing|Tiaoli"
                        && menus[^1] == "Which level have they attained?|Initiate|Apprentice|Follower|Sage|Master"
                        && legend.SequenceEqual(new[] { ($"Wu Xing Sage, Chosen by {caster.CharName} ({date})", 76, 5) })
                        && mine is [{ Kind: 0x0001, Head: 2, Next: 1 } m] && m.Gfx == chi && m.Text == $"{target.CharName} has been recognized as a Wu Xing Sage"
                        && before.Count == 0 && waiting
                        && theirs is [{ Kind: 0x0001, Head: 2, Next: 0 } t] && t.Gfx == chi && t.Text == "You have been recognized as a Wu Xing Sage"
                        && !target.DialogBusy
                        && offered == "Which recognition would you like to remove?|Wu Xing"
                        && tc.Legends.All(l => !l.Name.StartsWith("wu_xing_")) && cc.Legends.Any(l => l.Name == "tiaoli_master")
                        && removed is [{ } r] && r.Text == $"{target.CharName} has had their recognition removed for Wu Xing",
                        $"menus [{string.Join(" / ", menus)}]; legend [{string.Join(" / ", legend)}]; caster's [{string.Join(" / ", mine)}]; player's before " +
                        $"[{string.Join(" / ", before)}] (waiting {waiting}), after [{string.Join(" / ", theirs)}]; offered [{offered}]; removed " +
                        $"[{string.Join(" / ", removed)}], theirs now [{string.Join(", ", tc.Legends.Select(l => l.Name))}]");
        }
        finally { Leave(InnerMap, target, caster); }
    }

    /// <summary>Neutrality recognition writes "Learned to walk the neutral path, Marked by &lt;caster&gt;" (icon 145, colour 1)
    /// on the named player, and Remove takes it off them. Elementalist's mark takes the caster's own name too (its self check
    /// is commented out), writes "Recognized as Fire Mage, by &lt;caster&gt; (&lt;date&gt;)" (145, 1), the other elements gone,
    /// and Remove takes the picked element off the named player with "&lt;player&gt; has had their Fire mage recognition
    /// removed.". Red on the base.</summary>
    [Fact]
    public void NeutralityAndElementalistsMarkWriteAndRemoveOnTheNamedPlayer()
    {
        var (target, _, tc) = Player(NeutralMap, CX + 5, CY, name: Letters("Neutral"));
        var (neutral, nOut, _) = Caster("neutrality_recognition", NeutralMap, CX, CY);
        var (elem, eOut, ec) = Caster("elementalists_mark", ElementMap, CX, CY, configure: ch =>
            ch.Legends.Add(new Legend(145, 1, "Recognized as Water Mage, by Someone (Yuri 1, Spring)", "recognized_as_water_mage")));
        try
        {
            string date = Character.GameDate;
            List<Box> nBox = new(), nRemoved = new(), eBox = new(), eRemoved = new();
            List<(string, int, int)> nLegend = new(), eLegend = new();
            OnOwnThread(() =>
            {
                neutral.Receive(SpellCastSupport.CastFrame(0));
                neutral.Receive(InputFrame(target.CharName));
                nOut.Clear();
                neutral.Receive(MenuFrame(1));
                nBox = Boxes(nOut);
                nLegend = tc.Legends.Where(l => l.Name == "learned_to_walk_the_neutral_path").Select(l => (l.Text, (int)l.Icon, (int)l.Color)).ToList();
                SpellCastSupport.ClearAether(neutral, "neutrality_recognition");
                FreeSlot(neutral);
                neutral.Receive(SpellCastSupport.CastFrame(0));
                neutral.Receive(InputFrame(target.CharName));
                neutral.Receive(MenuFrame(2));
                nOut.Clear();
                neutral.Receive(MenuFrame(1));
                nRemoved = Boxes(nOut);

                elem.Receive(SpellCastSupport.CastFrame(0));
                elem.Receive(InputFrame(elem.CharName));       // the caster's own name
                elem.Receive(MenuFrame(1));
                eOut.Clear();
                elem.Receive(MenuFrame(2));                    // Fire
                eBox = Boxes(eOut);
                eLegend = ec.Legends.Where(l => l.Name.StartsWith("recognized_as_")).Select(l => (l.Text, (int)l.Icon, (int)l.Color)).ToList();
                SpellCastSupport.ClearAether(elem, "elementalists_mark");
                FreeSlot(elem);
                elem.Receive(SpellCastSupport.CastFrame(0));
                elem.Receive(InputFrame(elem.CharName));
                elem.Receive(MenuFrame(2));
                eOut.Clear();
                elem.Receive(MenuFrame(1));                    // Fire, the only one left
                eRemoved = Boxes(eOut);
            });
            Assert.True(nLegend.SequenceEqual(new[] { ($"Learned to walk the neutral path, Marked by {neutral.CharName}", 145, 1) })
                        && nBox is [{ } nb] && nb.Text == $"{target.CharName} has been recognized for walking the neutral path"
                        && tc.Legends.All(l => l.Name != "learned_to_walk_the_neutral_path")
                        && nRemoved is [{ } nr] && nr.Text == $"{target.CharName} has had their recognition removed for Learned to walk the neutral path"
                        && eLegend.SequenceEqual(new[] { ($"Recognized as Fire Mage, by {elem.CharName} ({date})", 145, 1) })
                        && eBox is [{ } eb] && eb.Text == $"{elem.CharName} has been recognized as a Fire Mage"
                        && ec.Legends.All(l => !l.Name.StartsWith("recognized_as_"))
                        && eRemoved is [{ } er] && er.Text == $"{elem.CharName} has had their Fire mage recognition removed.",
                        $"neutral [{string.Join(" / ", nLegend)}] [{string.Join(" / ", nBox)}] removed [{string.Join(" / ", nRemoved)}]; element " +
                        $"[{string.Join(" / ", eLegend)}] [{string.Join(" / ", eBox)}] removed [{string.Join(" / ", eRemoved)}]");
        }
        finally { Leave(NeutralMap, target, neutral); Leave(ElementMap, elem); }
    }

    /// <summary>The recognitions' refusals: short of 100, "Your will is too weak.", free and no box; inside the 5 s wait, the
    /// wait line. The name, after the 100 and the wait are spent (the scripts spend before they ask), each refusal a box with
    /// the Staff of chi's icon that ends it: anything but letters, "You can only enter letters and spaces."; nobody online by
    /// it, "That player is currently not online."; the caster, "You cannot provide your own recognition.". Red on the
    /// base.</summary>
    [Fact]
    public void RecognitionRefusals()
    {
        var (poor, pOut, pc) = Caster("balance_recognition", RecogRefuseMap, CX, CY, mana: 99);
        var casters = Enumerable.Range(0, 3).Select(i => Caster("balance_recognition", RecogRefuseMap, CX, CY + 4 + 4 * i)).ToList();
        try
        {
            string[] typed = { "Bob1", "Nobodyhereatall", casters[2].session.CharName };
            string[] want = { "You can only enter letters and spaces.", "That player is currently not online.", "You cannot provide your own recognition." };
            int chi = Content.ItemByKey("staff_of_chi")!.ClientIcon;
            var problems = new List<string>();
            OnOwnThread(() =>
            {
                poor.Receive(SpellCastSupport.CastFrame(0));
                for (int i = 0; i < 3; i++)
                {
                    var (s, o, c) = casters[i];
                    s.Receive(SpellCastSupport.CastFrame(0));
                    o.Clear();
                    s.Receive(InputFrame(typed[i]));
                    var boxes = Boxes(o);
                    if (boxes is not [{ Kind: 0x0001, Head: 2, Next: 0 } b] || b.Gfx != chi || b.Text != want[i] || c.Mp != Mana - 100
                        || Menus(o).Count != 0)
                        problems.Add($"{typed[i]}: [{string.Join(" / ", boxes)}] spent {Mana - c.Mp} menus {Menus(o).Count}");
                }
            });
            var (again, aOut, _) = casters[0];
            aOut.Clear();
            FreeSlot(again);
            again.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(problems.Count == 0 && Lines(pOut) == "Your will is too weak." && Boxes(pOut).Count == 0 && pc.Mp == 99
                        && Lines(aOut) == "Balance recognition isn't ready yet.",
                        $"{string.Join("\n", problems)}; poor [{Lines(pOut)}] mana {pc.Mp}; again [{Lines(aOut)}]");
        }
        finally { Leave(RecogRefuseMap, poor); Leave(RecogRefuseMap, casters.Select(c => c.session).ToArray()); }
    }

    /// <summary>A recognition's legend is written inside the named player's own monitor: while another thread holds it the
    /// caster's pick waits and the player has no Keeper mark, in both <c>StateRank</c> orders. Red with
    /// <c>Session.ReceiveLegend</c>'s monitor entry taken out (Release).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARecognitionWritesTheNamedPlayerInsideTheirOwnMonitor(bool casterRanksFirst)
    {
        int y = casterRanksFirst ? CY : CY + 6;
        Session caster, target;
        Character tc;
        if (casterRanksFirst)
        {
            (caster, _, _) = Caster("balance_recognition", RecogHeldMap, CX, y);
            (target, _, tc) = Player(RecogHeldMap, CX + 1, y, name: Letters("Held"));
        }
        else
        {
            (target, _, tc) = Player(RecogHeldMap, CX + 1, y, name: Letters("Held"));
            (caster, _, _) = Caster("balance_recognition", RecogHeldMap, CX, y);
        }
        Assert.Equal(casterRanksFirst, caster.StateRank < target.StateRank);
        var run = new HeldRun();
        try
        {
            OnOwnThread(() => { caster.Receive(SpellCastSupport.CastFrame(0)); caster.Receive(InputFrame(target.CharName)); });
            WhileAnotherThreadHolds(run, target, () => caster.Receive(MenuFrame(1)),
                                    untouched: () => tc.Legends.All(l => l.Name != "keeper_of_balance"));
            Assert.Null(run.HolderFault);
            Assert.Null(run.ActorFault);
            Assert.False(run.FinishedWhileHeld, "the pick finished while another thread held the player's monitor");
            Assert.True(run.UntouchedWhileHeld, "the mark landed while another thread held the player's monitor");
            Assert.True(tc.Legends.Any(l => l.Name == "keeper_of_balance"), "the mark never landed");
        }
        finally { if (run.Settled) Leave(RecogHeldMap, caster, target); }
    }

    // ===== Exhume ===============================================================================================

    /// <summary>The cast takes nothing and says nothing; it arms the 1,250 s wait and a run of 100 s whose beat each second
    /// takes 200 mana. Short of 200 at the cast, "Your will is too weak.", free; short at a beat, "Your will is too weak."
    /// and the run ends. Red on the base (generic: 5 mana and nothing else).</summary>
    [Fact]
    public void ExhumeArmsItsWaitAndARunThatTakesItsManaEachSecond()
    {
        var (caster, cOut, cc) = Caster("exhume", ExhumeBeatMap, CX, CY);
        var (low, lOut, lc) = Caster("exhume", ExhumeBeatMap, CX, CY + 4, mana: 250);
        var (poor, pOut, pc) = Caster("exhume", ExhumeBeatMap, CX, CY + 8, mana: 199);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string castLines = Lines(cOut);
            uint atCast = cc.Mp;
            long wait = SpellCastSupport.AetherLeft(caster, "exhume");
            var runs = Ticks(caster);
            Advance(caster, 1_000); Advance(caster, 1_000); Advance(caster, 1_000);
            uint afterThree = cc.Mp;
            low.Receive(SpellCastSupport.CastFrame(0));
            lOut.Clear();
            Advance(low, 1_000);
            uint lowAfterOne = lc.Mp;
            Advance(low, 1_000);
            string lowLines = Lines(lOut);
            var lowRuns = Ticks(low);
            poor.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(castLines == "" && atCast == Mana && wait > 1_250_000 - SlackMs && wait <= 1_250_000
                        && runs is [{ Verb: "exhume_beat", Self: true, EveryMs: 1_000 } r] && r.LeftMs > 100_001 - SlackMs && r.LeftMs <= 100_001
                        && afterThree == Mana - 600 && lowAfterOne == 50 && lowLines == "Your will is too weak." && lc.Mp == 50
                        && lowRuns.All(t => t.Ended)
                        && Lines(pOut) == "Your will is too weak." && pc.Mp == 199 && SpellCastSupport.AetherLeft(poor, "exhume") == 0,
                        $"cast [{castLines}] mana {atCast}, wait {wait}, runs [{string.Join(" / ", runs)}], after three beats {afterThree}; " +
                        $"low {lowAfterOne} then [{lowLines}] {lc.Mp} runs [{string.Join(" / ", lowRuns)}]; poor [{Lines(pOut)}] {pc.Mp}");
        }
        finally { Leave(ExhumeBeatMap, caster, low, poor); }
    }

    /// <summary>While Exhume's run lasts, dropping one of the script's sixteen items spends one of it, lands nothing on the
    /// floor, plays graphic 14 over the caster and puts that item's creature on the caster's own tile, unowned: the eleven
    /// RTK has and the project owner's five stand-ins (beef, rare pork, horse meat, bone and dragon's tooth). Red on the base
    /// (the item drops).</summary>
    [Theory]
    [InlineData("fox_fur", "dummy_fox")]
    [InlineData("red_fox_fur", "dummy_blood_fox")]
    [InlineData("light_fox_fur", "dummy_sun_fox")]
    [InlineData("bears_liver", "dummy_bear")]
    [InlineData("rat_meat", "dummy_rat")]
    [InlineData("antler", "dummy_deer")]
    [InlineData("beef", "dummy_bull")]
    [InlineData("rare_pork", "dummy_pig")]
    [InlineData("rabbit_meat", "dummy_rabbit")]
    [InlineData("wool", "dummy_sheep")]
    [InlineData("horse_meat", "dummy_horse")]
    [InlineData("bone", "dummy_skeleton")]
    [InlineData("dragons_tooth", "dummy_dragon")]
    [InlineData("tigers_heart", "dummy_tiger")]
    [InlineData("snake_meat", "dummy_snake")]
    [InlineData("acorn", "dummy_squirrel")]
    public void ExhumeTurnsADroppedItemIntoItsCreature(string item, string creature)
    {
        var def = Content.ItemByKey(item)!;
        int y = 4 + 5 * Array.IndexOf(ExhumeItems, item);
        var (caster, cOut, cc) = Caster("exhume", ExhumeDropMap, CX, y, configure: ch => ch.Inventory.Add(new InvItem(0, def.Id, 2)));
        List<Mob> made = new();
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            cOut.Clear();
            caster.Receive(DropFrame(0));
            made = _fx.World.MobsAt(ExhumeDropMap, CX, y).Where(m => !m.IsNpc).ToList();
            int floor = _fx.World.ItemsOn(ExhumeDropMap).Where(g => g.ItemId == def.Id).Sum(g => g.Amount);
            int held = cc.Inventory.Where(i => i.ItemId == def.Id).Sum(i => i.Amount);
            Assert.True(held == 1 && floor == 0 && Effects(cOut, caster.PlayerId).Contains(14)
                        && made is [{ } m] && m.Key == creature && m.OwnerId == 0,
                        $"{item}: held {held}, floor {floor}, graphics [{string.Join(",", Effects(cOut, caster.PlayerId))}], made " +
                        $"[{string.Join(", ", made.Select(x => $"{x.Key} owner {x.OwnerId}"))}]");
        }
        finally { foreach (var m in made) _fx.World.DespawnMob(ExhumeDropMap, m); Leave(ExhumeDropMap, caster); }
    }

    private static readonly string[] ExhumeItems =
    {
        "fox_fur", "red_fox_fur", "light_fox_fur", "bears_liver", "rat_meat", "antler", "beef", "rare_pork", "rabbit_meat", "wool",
        "horse_meat", "bone", "dragons_tooth", "tigers_heart", "snake_meat", "acorn",
    };

    /// <summary>Any other item drops as always while the run lasts; once the run has ended (its 100 s, the wait still
    /// running), one of the sixteen drops as always too. Red with the hook run while the wait lasts rather than while the run
    /// does (the run's loop in <c>Session.RunDropHooks</c> is what holds it to the run).</summary>
    [Fact]
    public void ExhumeLetsOtherItemsDropAndStopsWithItsRun()
    {
        var beef = Content.ItemByKey("beef")!;
        var lime = Content.ItemByKey("limestone")!;
        var (caster, _, _) = Caster("exhume", ExhumeMap, CX, CY, configure: ch =>
        {
            ch.Inventory.Add(new InvItem(0, beef.Id, 1));
            ch.Inventory.Add(new InvItem(1, lime.Id, 1));
        });
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            caster.Receive(DropFrame(1));
            int limeFloor = _fx.World.ItemsOn(ExhumeMap).Where(g => g.ItemId == lime.Id).Sum(g => g.Amount);
            Advance(caster, 100_002);
            bool waitLeft = SpellCastSupport.AetherLeft(caster, "exhume") > 0;
            caster.Receive(DropFrame(0));
            int beefFloor = _fx.World.ItemsOn(ExhumeMap).Where(g => g.ItemId == beef.Id).Sum(g => g.Amount);
            var made = _fx.World.MobsAt(ExhumeMap, CX, CY).Where(m => !m.IsNpc).ToList();
            Assert.True(limeFloor == 1 && waitLeft && beefFloor == 1 && made.Count == 0,
                        $"limestone on the floor {limeFloor}; wait left {waitLeft}; beef on the floor after the run {beefFloor}; made {made.Count}");
        }
        finally
        {
            foreach (var g in _fx.World.ItemsOn(ExhumeMap)) _fx.World.PickUp(ExhumeMap, g.X, g.Y);
            Leave(ExhumeMap, caster);
        }
    }

    /// <summary>The project owner's five stand-ins ("Pick five creatures") are copies of the closest creatures here, as the
    /// eleven dummy_* rows are made: the source's look, colour, vita, level and fighting numbers, no experience, fighting back
    /// only (MobBehavior 0), no drops. Red without the five mobs.csv rows.</summary>
    [Theory]
    [InlineData("dummy_horse", "horse")]
    [InlineData("dummy_bull", "large_bull")]
    [InlineData("dummy_pig", "stubborn_pig")]
    [InlineData("dummy_skeleton", "lesser_skeleton")]
    [InlineData("dummy_dragon", "dragon_hatchling")]
    public void TheFiveStandInsCopyTheirSources(string key, string source)
    {
        var d = Content.MobByKey(key);
        var s = Content.MobByKey(source)!;
        Assert.NotNull(d);
        Assert.True(d!.Look == s.Look && d.Color == s.Color && d.Hp == s.Hp && d.Level == s.Level && d.Will == s.Will
                    && d.Grace == s.Grace && d.MinDam == s.MinDam && d.MaxDam == s.MaxDam && d.MoveTime == s.MoveTime
                    && d.Protection == s.Protection && d.Hit == s.Hit && d.Ac == s.Ac && !d.IsBoss
                    && d.Exp == 0 && !d.Aggressive && !Content.MobDrops.ContainsKey(key),
                    $"{key} from {source}: look {d.Look}/{s.Look}, colour {d.Color}/{s.Color}, vita {d.Hp}/{s.Hp}, exp {d.Exp}, " +
                    $"aggressive {d.Aggressive}, drops {Content.MobDrops.ContainsKey(key)}");
    }

    // ===== GreenPotion ==========================================================================================

    /// <summary>With Limestone and Fine snake meat in the bag, one of each goes and a Lime potion comes, "You create a lime
    /// potion.", the 3,120 s wait, and no mana (the script never checks or takes its 500). Red on the base.</summary>
    [Fact]
    public void GreenPotionMakesALimePotion()
    {
        var lime = Content.ItemByKey("limestone")!;
        var meat = Content.ItemByKey("fine_snake_meat")!;
        var potion = Content.ItemByKey("lime_potion")!;
        var (caster, cOut, cc) = Caster("create_green_potion", PotionMap, CX, CY, configure: ch =>
        {
            ch.Inventory.Add(new InvItem(0, lime.Id, 2));
            ch.Inventory.Add(new InvItem(1, meat.Id, 1));
        });
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            long wait = SpellCastSupport.AetherLeft(caster, "create_green_potion");
            int Held(ItemDef d) => cc.Inventory.Where(i => i.ItemId == d.Id).Sum(i => i.Amount);
            Assert.True(Held(lime) == 1 && Held(meat) == 0 && Held(potion) == 1 && cc.Mp == Mana
                        && Lines(cOut).EndsWith("You create a lime potion.") && !Lines(cOut).Contains("You cast")
                        && wait > 3_120_000 - SlackMs && wait <= 3_120_000,
                        $"limestone {Held(lime)}, meat {Held(meat)}, potion {Held(potion)}, mana {cc.Mp}, lines [{Lines(cOut)}], wait {wait}");
        }
        finally { Leave(PotionMap, caster); }
    }

    /// <summary>No Limestone: the box "Where did you put that limestone?" with Limestone's icon; no Fine snake meat: "Where
    /// did you put that Fine Snake Meat?" with its icon; nothing taken, no wait, no line. Inside the wait, the wait line. Red
    /// on the base.</summary>
    [Fact]
    public void GreenPotionRefusals()
    {
        var lime = Content.ItemByKey("limestone")!;
        var meat = Content.ItemByKey("fine_snake_meat")!;
        var (noLime, nlOut, _) = Caster("create_green_potion", PotionRefuseMap, CX, CY, configure: ch => ch.Inventory.Add(new InvItem(0, meat.Id, 1)));
        var (noMeat, nmOut, nmc) = Caster("create_green_potion", PotionRefuseMap, CX, CY + 4, configure: ch => ch.Inventory.Add(new InvItem(0, lime.Id, 1)));
        try
        {
            noLime.Receive(SpellCastSupport.CastFrame(0));
            noMeat.Receive(SpellCastSupport.CastFrame(0));
            var a = Boxes(nlOut);
            var b = Boxes(nmOut);
            noMeat.WithState(() => ((Dictionary<string, long>)AetherField.GetValue(noMeat)!)["create_green_potion"] = Environment.TickCount64 + 60_000);
            nmOut.Clear();
            FreeSlot(noMeat);
            noMeat.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(a is [{ Kind: 0x0001, Head: 2 } x] && x.Gfx == lime.ClientIcon && x.Text == "Where did you put that limestone?"
                        && b is [{ Kind: 0x0001, Head: 2 } y] && y.Gfx == meat.ClientIcon && y.Text == "Where did you put that Fine Snake Meat?"
                        && nmc.Inventory.Sum(i => i.Amount) == 1 && Lines(nmOut) == "GreenPotion isn't ready yet.",
                        $"no limestone [{string.Join(" / ", a)}], no meat [{string.Join(" / ", b)}], held {nmc.Inventory.Sum(i => i.Amount)}, " +
                        $"inside the wait [{Lines(nmOut)}]");
        }
        finally { Leave(PotionRefuseMap, noLime, noMeat); }
    }

    // ===== Elemental mastery ====================================================================================

    /// <summary>The cast offers "Which element do you choose to manifest?" with its five manifests and no portrait; the pick
    /// wears that manifest's look (Water 325, Fire 519, Earth 186, Metal 248, Wood 192) in the caster's war paint for 120 s.
    /// No mana is taken, no wait is armed, no line is said. In the form already, "Spell already cast."; it ends with nothing
    /// played (no morph exit sound). Red on the base.</summary>
    [Theory]
    [InlineData(1, 325)]
    [InlineData(2, 519)]
    [InlineData(3, 186)]
    [InlineData(4, 248)]
    [InlineData(5, 192)]
    public void ElementalMasteryWearsTheManifestInWarPaint(byte pick, int look)
    {
        int y = CY + 4 * pick;
        var (caster, cOut, cc) = Caster("elemental_mastery", MasteryMap, CX, y, configure: ch => ch.ArmorColor = 12);
        try
        {
            string menu = "", lines = "";
            List<(int Head, int Gfx)> heads = new();
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                menu = string.Join(" / ", Menus(cOut));
                heads = MenuHeads(cOut);
                caster.Receive(MenuFrame(pick));
                lines = Lines(cOut);
            });
            ushort worn = 0; byte paint = 0; long left = 0;
            caster.WithState(() =>
            {
                worn = (ushort)MorphLookField.GetValue(caster)!;
                paint = (byte)MorphColorField.GetValue(caster)!;
                left = (long)MorphUntilField.GetValue(caster)! - Environment.TickCount64;
            });
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            string again = Lines(cOut);
            cOut.Clear();
            caster.RevertMorph();
            Assert.True(menu == "Which element do you choose to manifest?|Water manifest|Fire manifest|Earth manifest|Metal manifest|Wood manifest"
                        && heads is [(0, 0)] && lines == "" && worn == look && paint == 12 && left > 120_000 - SlackMs && left <= 120_000
                        && cc.Mp == Mana && SpellCastSupport.AetherLeft(caster, "elemental_mastery") == 0
                        && again == "Spell already cast." && !Sounds(cOut).Contains(411),
                        $"menu [{menu}] heads [{string.Join(",", heads)}], lines [{lines}], look {worn} paint {paint} left {left}, mana {cc.Mp}, " +
                        $"again [{again}], end sounds [{string.Join(",", Sounds(cOut))}]");
        }
        finally { Leave(MasteryMap, caster); }
    }

    /// <summary>Short of 2,000, "Your will is too weak.", free and no menu. Red on the base.</summary>
    [Fact]
    public void ElementalMasteryShortOfManaIsRefused()
    {
        var (poor, pOut, pc) = Caster("elemental_mastery", MasteryMap, CX + 20, CY, mana: 1_999);
        try
        {
            poor.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(pOut) == "Your will is too weak." && Menus(pOut).Count == 0 && pc.Mp == 1_999, $"[{Lines(pOut)}] {pc.Mp}");
        }
        finally { Leave(MasteryMap, poor); }
    }

    // ===== Good earth ===========================================================================================

    /// <summary>The cast takes nothing and says nothing; it puts an unowned Good earth trap, gone in 20 s, on each tile of the
    /// 3x3 around the caster that has no NPC or trap on it, inside RTK's bounds (not row or column 0, not the map's last), and
    /// arms the 125 s wait and a 125 s run. Short of 100, "Your will is too weak.", free and no trap. Red on the base.</summary>
    [Fact]
    public void GoodEarthSetsItsTrapsAroundTheCaster()
    {
        var (caster, cOut, cc) = Caster("good_earth", EarthMap, CX, CY);
        var (corner, _, _) = Caster("good_earth", EarthMap, 1, 1);
        var (edge, _, _) = Caster("good_earth", EarthMap, 98, CY);
        var (poor, pOut, pc) = Caster("good_earth", EarthMap, CX + 10, CY + 10, mana: 99);
        var npc = new Mob(_fx.World.AllocateMobId(), 1, CX + 1, CY, "Geomancer NPC", 100) { IsNpc = true };
        _fx.World.AddMob(EarthMap, npc);
        try
        {
            _fx.World.PlaceTrap(EarthMap, CX - 1, CY - 1, "dart", 0);
            caster.Receive(SpellCastSupport.CastFrame(0));
            corner.Receive(SpellCastSupport.CastFrame(0));
            edge.Receive(SpellCastSupport.CastFrame(0));
            poor.Receive(SpellCastSupport.CastFrame(0));
            long now = Environment.TickCount64;
            var mine = _fx.World.TrapsNear(EarthMap, CX, CY, 1).Where(t => t.Kind == World.GoodEarthTrap).ToList();
            var tiles = mine.Select(t => (t.X - CX, t.Y - CY)).OrderBy(p => p).ToList();
            int cornerN = _fx.World.TrapsNear(EarthMap, 1, 1, 1).Count(t => t.Kind == World.GoodEarthTrap);
            int edgeN = _fx.World.TrapsNear(EarthMap, 98, CY, 1).Count(t => t.Kind == World.GoodEarthTrap);
            int poorN = _fx.World.TrapsNear(EarthMap, CX + 10, CY + 10, 1).Length;
            var runs = Ticks(caster);
            long wait = SpellCastSupport.AetherLeft(caster, "good_earth");
            var expected = new List<(int, int)> { (-1, 0), (-1, 1), (0, -1), (0, 0), (0, 1), (1, -1), (1, 1) };
            Assert.True(tiles.SequenceEqual(expected) && mine.All(t => t.OwnerId == 0 && t.ExpiresAt - now is > 20_000 - SlackMs and <= 20_000)
                        && cornerN == 4 && edgeN == 6 && Lines(cOut) == "" && cc.Mp == Mana && wait > 125_000 - SlackMs && wait <= 125_000
                        && runs is [{ Verb: "good_earth_beat", Self: true, EveryMs: 1_500 } r] && r.LeftMs > 125_000 - SlackMs
                        && Lines(pOut) == "Your will is too weak." && poorN == 0 && pc.Mp == 99,
                        $"tiles [{string.Join(" ", tiles)}], corner {cornerN}, edge {edgeN}, lines [{Lines(cOut)}], mana {cc.Mp}, wait {wait}, " +
                        $"runs [{string.Join(" / ", runs)}]; poor [{Lines(pOut)}] traps {poorN}");
        }
        finally { _fx.World.DespawnMob(EarthMap, npc); ClearTraps(EarthMap); Leave(EarthMap, caster, corner, edge, poor); }
    }

    /// <summary>The run's beat every 1.5 s takes 100 mana while 90 s or more of its 125 s remain (23 beats, 2,300 mana) and
    /// nothing after; short of 100 at a charging beat, "Your will is too weak." and the run ends. Red on the base.</summary>
    [Fact]
    public void GoodEarthsBeatTakesManaWhileNinetySecondsRemain()
    {
        var (caster, _, cc) = Caster("good_earth", EarthBeatMap, CX, CY);
        var (low, lOut, lc) = Caster("good_earth", EarthBeatMap, CX, CY + 6, mana: 150);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            for (int i = 0; i < 90; i++) Advance(caster, 1_500);
            low.Receive(SpellCastSupport.CastFrame(0));
            lOut.Clear();
            Advance(low, 1_500);
            uint lowAfterOne = lc.Mp;
            Advance(low, 1_500);
            Advance(low, 1_500);
            Assert.True(cc.Mp == Mana - 2_300 && lowAfterOne == 50 && lc.Mp == 50 && Lines(lOut) == "Your will is too weak."
                        && Ticks(low).All(t => t.Ended),
                        $"spent {Mana - cc.Mp}; low {lowAfterOne} then {lc.Mp} [{Lines(lOut)}] runs [{string.Join(" / ", Ticks(low))}]");
        }
        finally { ClearTraps(EarthBeatMap); Leave(EarthBeatMap, caster, low); }
    }

    /// <summary>A player who walks onto a Good earth trap (any map, no PvP needed) gets graphic 18 and "It is peaceful here.",
    /// then Sanctuary ("You have found sanctuary.", half damage for 300 s, graphic 2, "&lt;their name&gt; casts Sanctuary on
    /// you."), Harden armor ("Your armor is strengthened.", sound 5, "You cast Harden Armor.", armour 10 better in the
    /// hardarmors slot for 300 s, graphic 2) and Valor ("Your muscles develop.", Might +3 in the mights slot for 300 s, sound
    /// 12, graphic 11). The trap stays; a second step reads "It is peaceful here." and nothing more (each slot is held). On a
    /// PvP map the two wards last 185 s. A ghost gets Sanctuary and "Spirits can't do that." twice. Red on the base (no
    /// trap).</summary>
    [Fact]
    public void APlayerOnAGoodEarthTrapTakesTheThreeNpcSpells()
    {
        var (walker, wOut, _) = Player(EarthStepMap, CX, CY, name: Letters("Walker"));
        var run = OpenRun(Arena, 3, 0);
        var (fighter, fOut, _) = Player(Arena, run.X, run.Y, name: Letters("Fighter"));
        var (ghost, gOut, _) = Player(EarthStepMap, CX, CY + 6, hp: 0, name: Letters("Ghost"));
        try
        {
            long expires = Environment.TickCount64 + 20_000;
            Assert.True(_fx.World.PlaceTrapWhereNoNpc(EarthStepMap, CX + 1, CY, World.GoodEarthTrap, expires));
            Assert.True(_fx.World.PlaceTrapWhereNoNpc(Arena, (ushort)(run.X + 1), (ushort)run.Y, World.GoodEarthTrap, expires));
            Assert.True(_fx.World.PlaceTrapWhereNoNpc(EarthStepMap, CX + 1, CY + 6, World.GoodEarthTrap, expires));
            Walk(walker, East);
            string first = Lines(wOut);
            var fx = Effects(wOut, walker.PlayerId);
            var snd = Sounds(wOut);
            bool sanctuary = walker.SancDeductActive;
            var harden = SpellCastSupport.BuffEntry(walker, "harden_armor");
            var valor = SpellCastSupport.BuffEntry(walker, "valor");
            int armour = 0, might = 0;
            walker.WithState(() => { armour = walker.ItemArmor; might = walker.LuaMight - walker.CharMight; });
            bool stays = _fx.World.TrapsNear(EarthStepMap, CX + 1, CY, 0).Any(t => t.Kind == World.GoodEarthTrap);
            Walk(walker, 3);
            wOut.Clear();
            Walk(walker, East);
            string second = Lines(wOut);
            Walk(fighter, East);
            var pvpHarden = SpellCastSupport.BuffEntry(fighter, "harden_armor");
            var pvpValor = SpellCastSupport.BuffEntry(fighter, "valor");
            Walk(ghost, East);
            string ghostLines = Lines(gOut);
            string name = walker.CharName;
            Assert.True(first == $"It is peaceful here. | You have found sanctuary. | {name} casts Sanctuary on you. | Your armor is strengthened. " +
                                 "| You cast Harden Armor. | Your muscles develop."
                        && fx.Count(a => a == 18) == 1 && fx.Count(a => a == 2) == 2 && fx.Contains(11) && snd.Contains(5) && snd.Contains(12)
                        && sanctuary && harden is { Category: "hardarmors" } h && h.LeftMs is > 300_000 - SlackMs and <= 300_000
                        && valor is { Category: "mights" } v && v.LeftMs is > 300_000 - SlackMs and <= 300_000
                        && armour == -10 && might == 3 && stays && second == "It is peaceful here."
                        && pvpHarden is { } ph && ph.LeftMs is > 185_000 - SlackMs and <= 185_000
                        && pvpValor is { } pv && pv.LeftMs is > 185_000 - SlackMs and <= 185_000
                        && ghostLines == $"It is peaceful here. | You have found sanctuary. | {ghost.CharName} casts Sanctuary on you. " +
                                        "| Spirits can't do that. | Spirits can't do that."
                        && ghost.SancDeductActive && SpellCastSupport.BuffEntry(ghost, "harden_armor") is null,
                        $"first [{first}] graphics [{string.Join(",", fx)}] sounds [{string.Join(",", snd)}] sanctuary {sanctuary} harden {harden} valor {valor} " +
                        $"armour {armour} might {might} stays {stays}; second [{second}]; pvp {pvpHarden} {pvpValor}; ghost [{ghostLines}]");
        }
        finally { ClearTraps(EarthStepMap); ClearTraps(Arena); Leave(EarthStepMap, walker, ghost); Leave(Arena, fighter); }
    }

    /// <summary>A creature that steps on a Good earth trap takes its graphic 18 and nothing more, and the trap stays (RTK's
    /// click goes on to Sanctuary's body, which creatures here cannot take, and stops at a player-only call). Spot traps shows
    /// no Good earth trap. Red on the base (no trap kind).</summary>
    [Fact]
    public void ACreatureOnAGoodEarthTrapTakesItsGraphicAndTheTrapStays()
    {
        var (viewer, vOut, _) = Caster("good_earth", EarthCreatureMap, CX + 4, CY);
        var mob = AddCreature(EarthCreatureMap, CX, CY);
        try
        {
            Assert.True(_fx.World.PlaceTrapWhereNoNpc(EarthCreatureMap, CX + 1, CY, World.GoodEarthTrap, Environment.TickCount64 + 20_000));
            int hp = mob.Hp;
            var q = StepQueued(EarthCreatureMap, mob, CX + 1, CY, 1);
            vOut.Clear();
            _fx.World.FlushTickForTest(q);
            bool stays = _fx.World.TrapsNear(EarthCreatureMap, CX + 1, CY, 0).Any(t => t.Kind == World.GoodEarthTrap);
            Trap[] revealed = Array.Empty<Trap>();
            viewer.WithState(() => revealed = (Trap[])RevealMethod.Invoke(viewer, null)!);
            Assert.True(Effects(vOut, mob.Id).Contains(18) && stays && mob.Hp == hp && revealed.All(t => t.Kind != World.GoodEarthTrap),
                        $"graphics [{string.Join(",", Effects(vOut, mob.Id))}], stays {stays}, vita {mob.Hp}, revealed {revealed.Length}");
        }
        finally { ClearTraps(EarthCreatureMap); _fx.World.DespawnMob(EarthCreatureMap, mob); Leave(EarthCreatureMap, viewer); }
    }

    /// <summary>The writes this slice adds to the caster's own saved state or the world refuse a caster who is leaving or
    /// whose session a second login has replaced, asked under the caster's monitor (#423's review, F1, for every new writing
    /// primitive): Exhume's creature, Good earth's traps and Reveal fate's caster tally. Red with the <c>CasterGone</c> test
    /// taken out of <c>Session.LuaSpawnHere</c> or <c>Session.LuaSetTrapsAround</c>; the tally's refusal is
    /// <c>Session.ReceiveMarkCount</c>'s own test (Scouting trip's, #423).</summary>
    [Theory]
    [InlineData("leaving")]
    [InlineData("replaced")]
    public void TheGeomancerWritesRefuseAGoneCaster(string why)
    {
        int x = why == "leaving" ? CX : CX + 20;
        var (caster, _, cc) = Caster("exhume", GoneMap, x, CY);
        var sp = Content.SpellByKey("exhume")!;
        var fate = Content.SpellByKey("reveal_fate")!;
        int traps = -1;
        bool spawned = true, marked = true;
        try
        {
            caster.WithState(() =>
            {
                if (why == "leaving") LeavingField.SetValue(caster, true); else ReplacedField.SetValue(caster, 1);
                try
                {
                    spawned = caster.LuaSpawnHere(sp, "dummy_bull");
                    traps = caster.LuaSetTrapsAround(sp, World.GoodEarthTrap, 20_000);
                    marked = caster.LuaMarkSelf(fate, "performed_ba_gua_experiences", "performed_ba_gua_experiences", "Performed %d Ba Gua experiences", 145, 128);
                }
                finally
                {
                    if (why == "leaving") LeavingField.SetValue(caster, false); else ReplacedField.SetValue(caster, 0);
                }
            });
            Assert.True(!spawned && traps == 0 && !marked && _fx.World.MobsAt(GoneMap, x, CY).Count == 0
                        && _fx.World.TrapsNear(GoneMap, x, CY, 1).Length == 0 && Reg(cc, "performed_ba_gua_experiences") == 0,
                        $"{why}: spawned {spawned}, traps {traps}, marked {marked}");
        }
        finally { ClearTraps(GoneMap); Leave(GoneMap, caster); }
    }

    // ===== Soften skin and Earths Spite =========================================================================

    /// <summary>Aimed at a creature (its id, or faced), Soften skin puts armour +35 (Earths Spite +45: the creature takes more
    /// damage) in its curses slot for 637.5 s, the script's 425 s as RTK's creature timer counts it (the project owner's
    /// Insult ruling), with graphic 34 again every 1.5 s while it holds; graphic 1 over it, sound 702, 500 mana and "You cast
    /// Soften skin." ("You cast Earths spite."). No wait. Red on the base (generic: 5 mana and a small blow).</summary>
    [Theory]
    [InlineData("soften_skin", 35, "Soften skin", true)]
    [InlineData("soften_skin", 35, "Soften skin", false)]
    [InlineData("earths_spite", 45, "Earths spite", true)]
    public void SoftenSkinCursesTheAimedCreatureOnItsTimer(string key, int amount, string word, bool byId)
    {
        int y = CY + 4 * (Interlocked.Increment(ref _serial) % 10);
        var (caster, cOut, cc) = Caster(key, SoftenMap, CX, y);
        var mob = AddCreature(SoftenMap, byId ? CX + 6 : CX + 1, y);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0, byId ? mob.Id : null));
            long now = Environment.TickCount64;
            long left = 0; string by = ""; int ac = 0, beat = 0, every = 0;
            _fx.World.UnderWorldLockForTest(() =>
            {
                left = mob.Statuses!["curses"].Until - now; by = mob.Statuses["curses"].Key; ac = mob.Ac;
                beat = mob.FxRepeatAnim; every = mob.FxRepeatEvery;
            });
            Assert.True(left > 637_500 - SlackMs && left <= 637_500 && by == key && ac == amount && beat == 34 && every == 1_500
                        && Effects(cOut, mob.Id).Contains(1) && Sounds(cOut).Contains(702) && cc.Mp == Mana - 500
                        && Lines(cOut) == $"You cast {word}." && SpellCastSupport.AetherLeft(caster, key) == 0,
                        $"left {left}, by {by}, ac {ac}, beat {beat}/{every}, graphics [{string.Join(",", Effects(cOut, mob.Id))}], sounds " +
                        $"[{string.Join(",", Sounds(cOut))}], spent {Mana - cc.Mp}, lines [{Lines(cOut)}]");
        }
        finally { _fx.World.DespawnMob(SoftenMap, mob); Leave(SoftenMap, caster); }
    }

    /// <summary>Aimed at a player on a PvP map, Soften skin puts armour +35 in their curses slot for 425 s, and they read
    /// "&lt;caster&gt; casts Soften skin on you."; graphic 1 over them. Red on the base.</summary>
    [Fact]
    public void SoftenSkinCursesAPlayerOnAPvpMap()
    {
        var run = OpenRun(Arena, 3, 1);
        var (caster, cOut, cc) = Caster("soften_skin", Arena, run.X, run.Y);
        var (target, tOut, _) = Player(Arena, run.X + 1, run.Y, name: Letters("Softened"));
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0, target.PlayerId));
            var curse = SpellCastSupport.BuffEntry(target, "soften_skin");
            int armour = 0;
            target.WithState(() => armour = target.ItemArmor);
            Assert.True(curse is { Category: "curses" } c && c.LeftMs is > 425_000 - SlackMs and <= 425_000 && armour == 35
                        && Lines(tOut) == $"{caster.CharName} casts Soften skin on you." && Effects(cOut, target.PlayerId).Contains(1)
                        && cc.Mp == Mana - 500 && Lines(cOut) == "You cast Soften skin.",
                        $"curse {curse}, armour {armour}, theirs [{Lines(tOut)}], graphics [{string.Join(",", Effects(cOut, target.PlayerId))}], " +
                        $"spent {Mana - cc.Mp}, mine [{Lines(cOut)}]");
        }
        finally { Leave(Arena, caster, target); }
    }

    /// <summary>Soften skin's refusals, each free and in the script's order: short of 500, "Not enough mana."; a ghost, "That
    /// is no longer useful."; a player off a PvP map, "You cannot attack that target."; a creature already cursed, "Another
    /// spell of this type is in effect."; a creature under a protection, "The target is already protected."; nothing aimed
    /// at, nothing at all. Red on the base.</summary>
    [Fact]
    public void SoftenSkinRefusals()
    {
        var (poor, pOut, pc) = Caster("soften_skin", SoftenRefuseMap, CX, CY, mana: 499);
        var (c1, o1, m1) = Caster("soften_skin", SoftenRefuseMap, CX, CY + 4);
        var (c2, o2, m2) = Caster("soften_skin", SoftenRefuseMap, CX, CY + 8);
        var (c3, o3, m3) = Caster("soften_skin", SoftenRefuseMap, CX, CY + 12);
        var (c4, o4, m4) = Caster("soften_skin", SoftenRefuseMap, CX, CY + 16);
        var (c5, o5, m5) = Caster("soften_skin", SoftenRefuseMap, CX, CY + 20);
        var (ghost, _, _) = Player(SoftenRefuseMap, CX + 1, CY + 4, hp: 0, name: Letters("Ghost"));
        var (alive, _, _) = Player(SoftenRefuseMap, CX + 1, CY + 8, name: Letters("Alive"));
        var cursed = AddCreature(SoftenRefuseMap, CX + 1, CY + 12);
        var shielded = AddCreature(SoftenRefuseMap, CX + 1, CY + 16);
        var target = AddCreature(SoftenRefuseMap, CX + 1, CY);
        try
        {
            _fx.World.ApplyMobStatus(cursed, "curses", 60_000, hold: false, blind: false, spellKey: "curse");
            _fx.World.ApplyMobStatus(shielded, "protections", 60_000, hold: false, blind: false, spellKey: "hoche_warrior");
            poor.Receive(SpellCastSupport.CastFrame(0, target.Id));
            c1.Receive(SpellCastSupport.CastFrame(0, ghost.PlayerId));
            c2.Receive(SpellCastSupport.CastFrame(0, alive.PlayerId));
            c3.Receive(SpellCastSupport.CastFrame(0, cursed.Id));
            c4.Receive(SpellCastSupport.CastFrame(0, shielded.Id));
            c5.Receive(SpellCastSupport.CastFrame(0));
            string[] got = { Lines(pOut), Lines(o1), Lines(o2), Lines(o3), Lines(o4), Lines(o5) };
            string[] want = { "Not enough mana.", "That is no longer useful.", "You cannot attack that target.",
                              "Another spell of this type is in effect.", "The target is already protected.", "" };
            Assert.True(got.SequenceEqual(want) && pc.Mp == 499 && new[] { m1, m2, m3, m4, m5 }.All(m => m.Mp == Mana)
                        && SpellCastSupport.BuffEntry(alive, "soften_skin") is null && _fx.World.MobStatusKey(cursed, "curses") == "curse",
                        $"[{string.Join("] [", got)}]; mana {pc.Mp}, {string.Join(",", new[] { m1, m2, m3, m4, m5 }.Select(m => Mana - m.Mp))}");
        }
        finally
        {
            foreach (var m in new[] { cursed, shielded, target }) _fx.World.DespawnMob(SoftenRefuseMap, m);
            Leave(SoftenRefuseMap, poor, c1, c2, c3, c4, c5, ghost, alive);
        }
    }

    /// <summary>Soften skin's curse on a player is written inside their own monitor: while another thread holds it the cast
    /// waits and the player carries no curse, in both <c>StateRank</c> orders. Red with the player branch's monitor entries
    /// taken out (<c>Session.LuaCurseAimed</c>'s and the <c>Session.ReceiveCurse</c> it calls; Release).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SoftenSkinWritesThePlayerInsideTheirOwnMonitor(bool casterRanksFirst)
    {
        var run0 = OpenRun(Arena, 3, casterRanksFirst ? 2 : 3);
        Session caster, target;
        if (casterRanksFirst)
        {
            (caster, _, _) = Caster("soften_skin", Arena, run0.X, run0.Y);
            (target, _, _) = Player(Arena, run0.X + 1, run0.Y, name: Letters("Held"));
        }
        else
        {
            (target, _, _) = Player(Arena, run0.X + 1, run0.Y, name: Letters("Held"));
            (caster, _, _) = Caster("soften_skin", Arena, run0.X, run0.Y);
        }
        Assert.Equal(casterRanksFirst, caster.StateRank < target.StateRank);
        var run = new HeldRun();
        try
        {
            WhileAnotherThreadHolds(run, target, () => caster.Receive(SpellCastSupport.CastFrame(0, target.PlayerId)),
                                    untouched: () => !target.HasStatusFromSpell("soften_skin"));
            Assert.Null(run.HolderFault);
            Assert.Null(run.ActorFault);
            Assert.False(run.FinishedWhileHeld, "the cast finished while another thread held the player's monitor");
            Assert.True(run.UntouchedWhileHeld, "the curse landed while another thread held the player's monitor");
            Assert.True(SpellCastSupport.BuffEntry(target, "soften_skin") is not null, "the curse never landed");
        }
        finally { if (run.Settled) Leave(Arena, caster, target); }
    }

    // ===== Read karma, and the teacher ==========================================================================

    /// <summary>Geomancer's Read karma is Diviner's verb on Geomancer's row: the question, then for a name 250 mana, the 88 s
    /// wait under its own key and the band's box. Red on the base.</summary>
    [Fact]
    public void ReadKarmaIsDivinersVerbOnGeomancersRow()
    {
        var (target, _, _) = Player(StandInMap, CX + 4, CY, name: Letters("Read"));
        var (caster, cOut, cc) = Caster("read_karma_geomancer", StandInMap, CX, CY);
        try
        {
            List<Box> asked = new(), shown = new();
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                asked = Boxes(cOut);
                cOut.Clear();
                caster.Receive(InputFrame(target.CharName));
                shown = Boxes(cOut);
            });
            long wait = SpellCastSupport.AetherLeft(caster, "read_karma_geomancer");
            Assert.True(asked is [{ Kind: 0x0404 } a] && a.Text == "Who's karma shall you read?" && cc.Mp == Mana - 250
                        && wait > 88_000 - SlackMs && wait <= 88_000 && shown is [{ Kind: 0x0001, Next: 1 } s]
                        && s.Text.StartsWith(target.CharName),
                        $"asked [{string.Join(" / ", asked)}], spent {Mana - cc.Mp}, wait {wait}, shown [{string.Join(" / ", shown)}]");
        }
        finally { Leave(StandInMap, target, caster); }
    }

    /// <summary>The Geomancer teacher sells fifteen at their scripts' prices: Read karma from 60, Hailstorm from 64, the rest
    /// from 99, Earths Spite at mark 1 only; Stone Casing, with no script, is not sold. Red without the price rows: two.</summary>
    [Fact]
    public void TheGeomancerTeacherSellsFifteen()
    {
        int path = Content.PathIdForClass("Geomancer");
        Assert.Equal(16, path);
        string Sold(int level, int mark) => string.Join(" ", Content.SubpathTeacherSpells(path, level, mark)
            .OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}:{s.Level}"));
        Assert.Equal("balance_recognition:99 create_green_potion:99 earths_spite:99 elemental_mastery:99 elementalists_mark:99 exhume:99 " +
                     "good_earth:99 hailstorm:64 honor_of_the_three_sects:99 ice_bombardment:99 inner_sect_recognition:99 " +
                     "neutrality_recognition:99 read_karma_geomancer:60 reveal_fate:99 soften_skin:99", Sold(99, Content.MaxMark));
        Assert.DoesNotContain("earths_spite", Sold(99, 0));
        Assert.Equal("read_karma_geomancer:60", Sold(63, Content.MaxMark));
        Assert.Null(Content.LearnCostFor(Content.SpellByKey("stone_casing")!, path));
    }

    // ===== helpers ==============================================================================================

    private const byte East = 1;

    private static string Lines(RecordingOutbound o) => string.Join(" | ", SpellCastSupport.MiniTexts(o));

    private static int Reg(Character c, string key) => QuestState.Over(c, QuestState.Registry).Get(key);

    /// <summary>The group's only legend is <paramref name="name"/> reading <paramref name="text"/> in the given icon and
    /// colour (the others of the group taken off).</summary>
    private static bool OneOf(Character c, string[] group, string name, string text, byte icon, byte color) =>
        c.Legends.Where(l => group.Contains(l.Name)).Select(l => (l.Name, l.Text, l.Icon, l.Color))
         .SequenceEqual(new[] { (name, text, icon, color) });

    /// <summary>A menu pick (0x3A kind 2, the option's 1-based index at [10]).</summary>
    private static byte[] MenuFrame(byte index)
    {
        var body = new byte[11];
        body[0] = 0x02;
        body[10] = index;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>An input box's answer (0x3A kind 4, a submit: step 2 at [8], the length at [10], the text from [11]).</summary>
    private static byte[] InputFrame(string text)
    {
        byte[] t = Encoding.ASCII.GetBytes(text);
        var body = new byte[11 + t.Length];
        body[0] = 0x04;
        body[8] = 0x02;
        body[10] = (byte)t.Length;
        t.CopyTo(body, 11);
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>The client's "next" on a text box (0x3A kind 1, step 2).</summary>
    private static byte[] NextFrame()
    {
        var body = new byte[11];
        body[0] = 0x01;
        body[8] = 0x02;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>The drop frame (0x08): the 1-based bag slot, not all.</summary>
    private static byte[] DropFrame(int slot) => SessionFixture.Frame(ClientOp.DropItem, new byte[] { (byte)(slot + 1), 0, 0 });

    /// <summary>Every menu (0x30 kind 2) the player was shown, as "prompt|option|option...".</summary>
    private static List<string> Menus(RecordingOutbound o)
    {
        var menus = new List<string>();
        foreach (var b in o.BodiesOf(ServerOp.NpcDialog).Where(b => b.Length >= 23 && b[0] == 2 && b[1] == 2))
        {
            int len = (b[21] << 8) | b[22];
            var parts = new List<string> { Encoding.ASCII.GetString(b, 23, len) };
            int at = 23 + len, n = b[at++];
            for (int i = 0; i < n; i++)
            {
                int ol = b[at++];
                parts.Add(Encoding.ASCII.GetString(b, at, ol));
                at += ol;
            }
            menus.Add(string.Join("|", parts));
        }
        return menus;
    }

    /// <summary>Every menu's portrait: its head kind and graphic.</summary>
    private static List<(int Head, int Gfx)> MenuHeads(RecordingOutbound o) =>
        o.BodiesOf(ServerOp.NpcDialog).Where(b => b.Length >= 23 && b[0] == 2 && b[1] == 2).Select(b => ((int)b[6], (b[8] << 8) | b[9])).ToList();

    private sealed record Box(int Kind, uint Id, int Head, int Gfx, int Color, int Prev, int Next, string Text)
    {
        public override string ToString() => $"{Kind:x4} id {Id} head {Head} gfx {Gfx:x4}/{Color} prev {Prev} next {Next} \"{Text}\"";
    }

    /// <summary>Every 0x30 box or input box <paramref name="o"/> was sent, in order (a menu is not one).</summary>
    private static List<Box> Boxes(RecordingOutbound o) =>
        o.BodiesOf(ServerOp.NpcDialog).Where(b => b.Length >= 23 && b[7] == 1 && !(b[0] == 2 && b[1] == 2)).Select(b =>
        {
            int len = (b[21] << 8) | b[22];
            return new Box((b[0] << 8) | b[1], (uint)((b[2] << 24) | (b[3] << 16) | (b[4] << 8) | b[5]), b[6],
                           (b[8] << 8) | b[9], b[10], b[19], b[20], Encoding.ASCII.GetString(b, 23, Math.Min(len, b.Length - 23)));
        }).ToList();

    private static uint U32(byte[] b, int at) => (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]);

    /// <summary>The spell graphics (0x29) sent over <paramref name="id"/>, as effect ids.</summary>
    private static List<int> Effects(RecordingOutbound outbound, uint id) =>
        outbound.BodiesOf(ServerOp.Effect).Where(b => U32(b, 0) == id).Select(b => b[4] - ServerConfig.Current.EfxWireOffset).ToList();

    private static List<int> Sounds(RecordingOutbound outbound) =>
        outbound.BodiesOf(ServerOp.Audio).Where(b => b.Length >= 4).Select(b => (b[2] << 8) | b[3]).ToList();

    private static void FreeSlot(Session s) => s.WithState(() => { NextActionTick.SetValue(s, 0L); ActionCount.SetValue(s, 0); });

    /// <summary>How long a status flag (a setDuration) has left on <paramref name="s"/>, 0 for none.</summary>
    private static long StatusLeft(Session s, string key)
    {
        long left = 0;
        s.WithState(() =>
        {
            if (((Dictionary<string, long>)StatusFlagsField.GetValue(s)!).TryGetValue(key, out long until))
                left = until - Environment.TickCount64;
        });
        return left;
    }

    private sealed record TickView(string Verb, bool Self, int EveryMs, long LeftMs, bool Ended);

    /// <summary>The caster's runs as they stand (Diviner's and Do's facts read them the same way).</summary>
    private static List<TickView> Ticks(Session s)
    {
        var list = new List<TickView>();
        s.WithState(() =>
        {
            long now = Environment.TickCount64;
            foreach (var t in (System.Collections.IList)TicksField.GetValue(s)!)
                list.Add(new TickView((string)F(t, "Verb"), (bool)F(t, "Self"), (int)F(t, "EveryMs"), (long)F(t, "Until") - now,
                                      (bool)F(t, "Ended") || (long)F(t, "Until") <= now));
        });
        return list;
    }

    /// <summary>Move the caster's runs <paramref name="ms"/> on, as <c>DoSpellTests</c> moves them, then run the
    /// heartbeat's call.</summary>
    private static void Advance(Session s, long ms)
    {
        s.WithState(() =>
        {
            foreach (var t in (System.Collections.IList)TicksField.GetValue(s)!)
            {
                t.GetType().GetField("Next")!.SetValue(t, (long)F(t, "Next") - ms);
                t.GetType().GetField("Until")!.SetValue(t, (long)F(t, "Until") - ms);
            }
            PublishTickDue.Invoke(s, null);
        });
        s.TickSpellTicks();
    }

    private static object F(object o, string name) => o.GetType().GetField(name)!.GetValue(o)!;

    /// <summary>The <paramref name="skip"/>'th east-west run of <paramref name="length"/> open tiles on
    /// <paramref name="map"/> (no wall, no warp), the runs at least four rows apart, so each fact walks its own.</summary>
    private static (int X, int Y) OpenRun(ushort map, int length, int skip)
    {
        ushort xs = 100, ys = 100;
        if (Content.Maps.TryGetValue(map, out var info)) { xs = info.Xs; ys = info.Ys; }
        var terrain = MapData.For(map, xs, ys);
        int found = 0, lastY = -100;
        for (int y = 2; y < ys - 2; y++)
        {
            if (y - lastY < 4) continue;
            for (int x = 2; x + length < xs - 2; x++)
            {
                bool open = Enumerable.Range(0, length).All(i =>
                    (terrain is null || (!terrain.Solid(x + i, y) && terrain.Obj(x + i, y) == 0 && terrain.Pass(x + i, y) == 0))
                    && !Content.TryWarp(map, (ushort)(x + i), (ushort)y, out _));
                if (!open) continue;
                if (found++ == skip) return (x, y);
                lastY = y;
                break;
            }
        }
        throw new InvalidOperationException($"map {map} has no open run #{skip}");
    }

    /// <summary>A step on the walk frame (0x06), from where the player stands.</summary>
    private static void Walk(Session s, byte dir)
    {
        ushort x = s.PlayerX, y = s.PlayerY;
        s.Receive(SessionFixture.Frame(ClientOp.Walk, new byte[] { dir, 0, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y }));
    }

    /// <summary>A creature's committed step onto (<paramref name="nx"/>, <paramref name="ny"/>), as the mob tick commits it,
    /// on a beat's queues of its own, which <c>World.FlushTickForTest</c> can then drain.</summary>
    private World.TickQueues StepQueued(ushort map, Mob mob, int nx, int ny, byte dir)
    {
        var q = new World.TickQueues();
        _fx.World.UnderWorldLockForTest(() =>
        {
            var ctx = _fx.World.MobTickContextForTest(map, q);
            Assert.True(World.MobMovement.StepToForTest(ctx, mob, nx, ny, dir));
        });
        return q;
    }

    /// <summary>Every trap left on <paramref name="map"/> lapses and the world tick's sweep takes it.</summary>
    private void ClearTraps(ushort map)
    {
        _fx.World.UnderWorldLockForTest(() =>
        {
            foreach (var t in _fx.World.TrapsNear(map, 0, 0, 10_000)) t.ExpiresAt = 1;
        });
        _fx.World.TickOnceForTest();
    }

    /// <summary>Run <paramref name="body"/> on a thread of its own with no synchronisation context, as a read loop runs a
    /// packet: an await inside it (a menu's, an input box's or a box's) then resumes inline on the thread that answers it.</summary>
    private static void OnOwnThread(Action body)
    {
        Exception? fault = null;
        var t = new Thread(() => fault = Record.Exception(body)) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(60)), "the packet thread never finished");
        Assert.Null(fault);
    }

    /// <summary>A letters-only name, unique in this process: the typed names take letters, spaces and an apostrophe only.</summary>
    private static string Letters(string stem)
    {
        int n = Interlocked.Increment(ref _serial);
        var sb = new StringBuilder(stem);
        do { sb.Append((char)('a' + n % 26)); n /= 26; } while (n > 0);
        return sb.ToString();
    }

    private Mob AddCreature(ushort map, int x, int y, int hp = 100_000)
    {
        var mob = new Mob(_fx.World.AllocateMobId(), 1, (ushort)x, (ushort)y, $"Geomancer target {Interlocked.Increment(ref _serial)}", hp);
        _fx.World.AddMob(map, mob);
        return mob;
    }

    /// <summary>A map's size as Maps.csv has it, or a wide 100 x 100 for a content-free map.</summary>
    private static void Size(Character c, ushort map)
    {
        if (Content.Maps.TryGetValue(map, out var m)) { c.MapXs = m.Xs; c.MapYs = m.Ys; }
        else { c.MapXs = 100; c.MapYs = 100; }
    }

    /// <summary>A level-99 Geomancer at (<paramref name="x"/>, <paramref name="y"/>) facing east, with <paramref name="key"/>
    /// in slot 0 of the book, 10,000 vita, on the map's own size (a wide one for a content-free map).</summary>
    private (Session session, RecordingOutbound outbound, Character c) Caster(string key, ushort map, int x, int y,
                                                                            uint mana = Mana, Action<Character>? configure = null)
    {
        var made = _fx.PlayerWith(Letters("Geomancer"), ch =>
        {
            ch.ClassName = "Geomancer";
            ch.Level = 99;
            ch.Ac = 0;
            ch.MaxHp = Hp; ch.Hp = Hp;
            ch.MaxMp = Mana; ch.Mp = mana;
            ch.Dir = 1;
            Size(ch, map);
            ch.Spells.Add(Content.SpellByKey(key)!.Id);
            configure?.Invoke(ch);
        }, map, (ushort)x, (ushort)y);
        made.session.WithState(() => _ = made.session.ItemArmor);
        return made;
    }

    private (Session session, RecordingOutbound outbound, Character c) Player(ushort map, int x, int y, uint hp = Hp,
                                                                            uint maxHp = Hp, string? name = null,
                                                                            Action<Character>? configure = null) =>
        _fx.PlayerWith(name ?? Letters("Other"), ch =>
        {
            ch.Level = 50;
            ch.Ac = 0;
            ch.MaxHp = maxHp; ch.Hp = hp;
            ch.MaxMp = 1_000; ch.Mp = 0;
            ch.Dir = 1;
            Size(ch, map);
            configure?.Invoke(ch);
        }, map, (ushort)x, (ushort)y);

    private void Leave(ushort map, params Session[] sessions)
    {
        foreach (var s in sessions) _fx.World.LeaveMap(s, map);
    }

    // ---- the held-monitor run (MonkSpellTests' shape) -----------------------------------------------------------------

    private sealed class HeldRun
    {
        public bool Settled = true;
        public bool FinishedWhileHeld;
        public bool UntouchedWhileHeld;
        public Exception? HolderFault, ActorFault;
    }

    /// <summary>A third thread takes <paramref name="target"/>'s monitor; then <paramref name="act"/> runs on a thread of its
    /// own until it finishes or sits blocked for 100 ms; then, still inside the third thread's section,
    /// <paramref name="untouched"/> says whether the target's state is as the third thread left it. Then the third thread
    /// lets go and both are joined.</summary>
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

    /// <summary>Wait until <paramref name="t"/> has finished, or has sat blocked for 100 ms straight, or 10 s have passed.</summary>
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
}
