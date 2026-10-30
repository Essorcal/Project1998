using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Tests;

/// <summary>
/// The first PC-subpath slice (Caleb, 2026-10-26, P1-P5): a teacher on each of the twelve Circles
/// (<c>Server/SubpathTeacher.cs</c>), selling a member only their own subpath's working spells at RTK's script prices
/// (<c>game-data/SpellLearnCosts.csv</c>, the nine rows keyed to paths 13, 16, 18, 19 and 20); a member's Qui hyang
/// offering RTK's "Subpath Circle" (<c>item_verbs.lua</c> <c>quihyang</c>); and the spellbook rebuild keeping the
/// subpath spells the book holds (<see cref="Content.RespecSpellSet"/>).
///
/// <para>Each fails quietly without its piece. A teacher on the wrong map, or not composed, stands there saying
/// "Greetings, traveller."; a missing price row makes a spell vanish from the list, a wrong one sells it for the wrong
/// items; a rebuild without the keep takes a paid spell at the shrine with nothing said; a scroll without the menu
/// sends a member to a tavern as before. Dialogs run on a thread of their own, started inside the player's monitor as
/// the read loop starts one, and each answer is the real <c>0x3A</c>; the scroll goes through the real <c>0x1C</c>.</para>
/// </summary>
[Collection("world")]
public sealed class SubpathTeacherTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private static int _serial;

    private readonly SessionFixture _fx;
    private readonly ITestOutputHelper _out;

    public SubpathTeacherTests(SessionFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    // ---- the twelve Circles and their teachers ------------------------------------------------------------------

    /// <summary>The Circle table names the twelve PC subpaths once each, each on the map Maps.csv calls
    /// "&lt;Subpath&gt; Circle", and every arrival box has walkable tiles, all inside the box, none the teacher's.</summary>
    [Fact]
    public void TheCirclesAreTheTwelveSubpathsOwnMaps()
    {
        Assert.Equal(Enumerable.Range(10, 12), SubpathCircles.All.Select(c => c.Subpath).OrderBy(p => p));
        foreach (var c in SubpathCircles.All)
        {
            Assert.True(Content.IsPcSubpath(c.Subpath), $"path {c.Subpath} is not a PC subpath");
            Assert.Equal($"{Content.PathName(c.Subpath)} Circle", Content.Maps[(ushort)c.Map].Name);

            var tiles = SubpathCircles.ArrivalTiles(c);
            var md = MapData.For((ushort)c.Map)!;
            var teacher = Content.Npcs.Single(n => n.Key == "SubpathTeacherNpc" && n.Map == c.Map);
            Assert.True(tiles.Count > 0, $"{Content.PathName(c.Subpath)} Circle has no walkable arrival tile");
            Assert.All(tiles, t =>
            {
                Assert.InRange((int)t.X, c.X1, c.X2);
                Assert.InRange((int)t.Y, c.Y1, c.Y2);
                Assert.False(md.Solid(t.X, t.Y));
                Assert.NotEqual((teacher.X, teacher.Y), (t.X, t.Y));
            });
        }
    }

    /// <summary>Each Circle has exactly one teacher, "&lt;Subpath&gt; Teacher", placed in the world on a free tile
    /// (open ground, no object, four walkable neighbours), composed of the teacher ability and the universal answers.
    /// The look is the subpath's own where Morphs.csv gives the subpath one (the Chongun uniform, the Marketer guise),
    /// else the look of the base class's own trainers. Red without the NPC rows (no teacher on the map) or without the
    /// composition row or registration (the ability list is the universal answers alone).</summary>
    [Theory]
    [InlineData("Barbarian", 39, 6)]
    [InlineData("Chongun",   37, 0)]
    [InlineData("Do",        39, 6)]
    [InlineData("Merchant",  52, 0)]
    [InlineData("Ranger",    38, 0)]
    [InlineData("Spy",       38, 0)]
    [InlineData("Diviner",   40, 3)]
    [InlineData("Geomancer", 40, 3)]
    [InlineData("Shaman",    40, 3)]
    [InlineData("Druid",     87, 7)]
    [InlineData("Monk",      87, 7)]
    [InlineData("Muse",      87, 7)]
    public void EachCircleHasItsTeacherOnAFreeTile(string subpath, int look, int color)
    {
        int path = Content.PathIdForClass(subpath);
        var circle = SubpathCircles.For(path)!.Value;
        var def = Assert.Single(Content.Npcs, n => n.Map == circle.Map && n.Key == "SubpathTeacherNpc");
        Assert.Equal($"{subpath} Teacher", def.Name);
        Assert.True(def.Enabled);
        Assert.Equal((look, color), ((int)def.Look, (int)def.Color));

        var md = MapData.For((ushort)circle.Map)!;
        Assert.False(md.Solid(def.X, def.Y), "the teacher stands on blocked ground");
        Assert.Equal(0, md.Obj(def.X, def.Y));
        foreach (var (dx, dy) in new[] { (0, -1), (1, 0), (0, 1), (-1, 0) })
            Assert.False(md.Solid(def.X + dx, def.Y + dy), $"({def.X + dx},{def.Y + dy}) next to the teacher is blocked");

        Assert.Equal(new[] { typeof(SubpathTeacherAbility), typeof(InfoAbility) },
                     NpcScripts.For(def).Select(a => a.GetType()));
        var mob = TeacherMob(def);
        Assert.Equal((def.X, def.Y, def.Look), (mob.X, mob.Y, (ushort)mob.Sprite));
    }

    // ---- what each teacher sells --------------------------------------------------------------------------------

    /// <summary>The nine working spells at their RTK scripts' <c>requirements()</c> (the facts report's table 2.3),
    /// and nothing at all for the other seven. Each item named is one Items.csv has. Red without the nine rows: every
    /// list is empty. The Shaman spells slice then put Shaman's solace, Reincarnate and Shaman's torment on sale beside
    /// Phoenix flames, at their scripts' prices; torment's TESTING script has no <c>requirements()</c>, so it sells at its
    /// row's level, 0, for nothing (Caleb's P3). The Do spells slice then put Do's other seven on sale beside Ki strike
    /// and Spirit blade, each at its script's price, the borrowed ones as written (Hear footstep asks Blend's, Crossing
    /// swords Chung ryong rage's).</summary>
    [Theory]
    // The Barbarian spells slice: fifteen of the sixteen at their scripts' prices; Push, whose script has no
    // requirements(), at its row's level, 0, for nothing; Survival challenge, with no script of its own, off sale.
    [InlineData("Barbarian", "barbarian_cry 99 10000 [1 angels_tear, 1 electra, 20 dark_amber]; " +
                             "barbarian_rage 99 20000 [1 dragons_liver, 1 electra, 1 spike]; " +
                             "break_jaw 99 10000 [1 angels_tear, 1 electra, 20 dark_amber]; " +
                             "dash_barbarian 99 25000 [1 blood, 1 splendid_tiger_pelt]; " +
                             "dishonor 99 0 [1 forsaken_staff, 2 forsaken_blade]; " +
                             "envigorate 9 100 [1 aged_wine]; " +
                             "insult 70 1000 [2 amber, 80 acorn]; " +
                             "mount 99 0 []; " +
                             "nomadic_blade 99 60000 [1 ee_san_spike, 4 dragons_liver, 4 electra]; " +
                             "push 0 0 []; " +
                             "rend 99 10000 [1 angels_tear, 1 electra, 20 dark_amber]; " +
                             "sonhi_rage 99 50000 [1 chung_ryong_key, 1 dragons_liver, 1 spike]; " +
                             "tigers_might 99 5000 [10 bears_liver, 10 tiger_meat, 5 antler]; " +
                             "wilderness_guise 99 50000 [1 ju_jak_key, 1 surge]; " +
                             "wilderness_weapon 99 40000 [1 il_san_spike, 2 dragons_liver, 2 electra]")]
    [InlineData("Chongun",   "")]
    [InlineData("Do",        "assess_opponent_do 99 5000 [3 fox_blade, 5 fine_rabbit_meat, 80 red_fox_fur]; " +
                             "balance_weapon 99 60000 [1 ee_san_spike, 4 dragons_liver, 4 electra]; " +
                             "blend 99 0 [1 fox_tail, 1 topaz]; " +
                             "crossing_swords 99 50000 [1 chung_ryong_key, 1 dragons_liver, 1 spike]; " +
                             "dash_do 99 25000 [1 blood, 1 splendid_tiger_pelt]; " +
                             "hear_footstep_do 99 0 [1 fox_tail, 1 topaz]; " +
                             "ki_strike 99 500 [1 amber, 70 acorn]; " +
                             "master_weapon 99 60000 [1 ee_san_spike, 4 dragons_liver, 4 electra]; " +
                             "spirit_blade 99 60000 [1 ee_san_spike, 4 dragons_liver, 4 electra]")]
    [InlineData("Merchant",  "")]
    [InlineData("Ranger",    "")]
    [InlineData("Spy",       "assassinate 99 10000 [1 angels_tear, 1 electra, 20 dark_amber]")]
    [InlineData("Diviner",   "")]
    [InlineData("Geomancer", "hailstorm 64 500 [1 amber, 70 acorn]; ice_bombardment 99 500 [1 amber, 70 acorn]")]
    [InlineData("Shaman",    "phoenix_flames 64 500 [1 amber, 70 acorn]; reincarnate 64 500 [1 amber, 70 acorn]; shamans_solace 99 10000 [1 mountain_ginseng]; shamans_torment 0 0 []")]
    [InlineData("Druid",     "earth_strike 99 0 [1 gold_acorn, 1 topaz]; lightning_storm 99 0 [20 rabbit_meat, 30 rat_meat]; natures_smite 99 0 [1 gold_acorn, 1 topaz]")]
    [InlineData("Monk",      "")]
    [InlineData("Muse",      "")]
    public void EachTeacherSellsTheWorkingSpellsAtRtksScriptPrices(string subpath, string expected)
    {
        int path = Content.PathIdForClass(subpath);
        var sold = Content.SubpathTeacherSpells(path, Content.MarkSpellLevel, Content.MaxMark);
        Assert.All(sold, s => Assert.Equal(path, s.PathId));
        string got = string.Join("; ", sold.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s =>
        {
            var cost = Content.LearnCostFor(s, path)!;
            foreach (var (item, _) in cost.Items) Assert.NotNull(Content.ItemByKey(item));
            Assert.Equal(cost.Level, s.Level);
            string items = string.Join(", ", cost.Items.Select(i => $"{i.Amount} {i.Item}").OrderBy(i => i, StringComparer.Ordinal));
            return $"{s.Key} {cost.Level} {cost.Gold} [{items}]";
        }));
        Assert.Equal(expected, got);
    }

    /// <summary>A price row keyed to a subpath sells to that subpath's teacher alone: no trainer offers the nine to
    /// any path (the rows are keyed to paths 13-20, which no trainer reads), and a subpath's teacher never offers
    /// another subpath's spell.</summary>
    [Fact]
    public void NoTrainerOrOtherTeacherOffersThem()
    {
        foreach (int path in Content.PlayablePaths)
            for (int alignment = 0; alignment <= 3; alignment++)
                Assert.DoesNotContain(Content.SpellsForClass(path, Content.MarkSpellLevel, alignment, Content.MaxMark),
                                      Content.IsPcSubpathSpell);
        foreach (var c in SubpathCircles.All)
            Assert.All(Content.SubpathTeacherSpells(c.Subpath, Content.MarkSpellLevel, Content.MaxMark),
                       s => Assert.Equal(c.Subpath, s.PathId));
        Assert.Empty(Content.SubpathTeacherSpells(4, Content.MarkSpellLevel, Content.MaxMark));   // a base class has no teacher list
    }

    /// <summary>A marked row is offered only from its rank, as at every trainer. Written before any marked row was sold,
    /// this prices Balance weapon (Do, mark 1) through the list's price seam: not at mark 0, offered at mark 1. Red
    /// with the mark gate taken out of <see cref="Content.SubpathTeacherSpells"/>. Its real price row is the Do spells
    /// slice's (<c>DoSpellTests.TheDoTeacherSellsByRank</c>).</summary>
    [Fact]
    public void AMarkedRowNeedsItsRank()
    {
        var balance = Content.SpellByKey("balance_weapon")!;
        Assert.Equal((18, 1), (balance.PathId, balance.Mark));
        Content.LearnCost? Price(SpellDef s) =>
            s.Key == balance.Key ? new Content.LearnCost(99, 60_000, new[] { ("ee_san_spike", 1) }) : Content.LearnCostFor(s, 18);

        Assert.DoesNotContain(Content.SubpathTeacherSpells(18, 99, 0, Price), s => s.Key == balance.Key);
        Assert.Contains(Content.SubpathTeacherSpells(18, 99, 1, Price), s => s.Key == balance.Key);
    }

    /// <summary>The Learn Secret list is the subpath's sold rows at or below the member's level, minus what the book
    /// holds: a level-63 Geomancer is told to grow (the trainer's line), at 64 is offered Hailstorm, at 99 Hailstorm and
    /// Ice bombardment, and holding Hailstorm only Ice bombardment.</summary>
    [Fact]
    public void TheListIsTheRowsAtTheMembersLevelMinusKnown()
    {
        var hail = Content.SpellByKey("hailstorm")!;
        Assert.Equal(new[] { SecretLessons.NothingMoreForNow }, LearnSecretBoxes("Geomancer", 63));
        Assert.Equal(new[] { "Hailstorm" }, LearnSecretOptions("Geomancer", 64));
        Assert.Equal(new[] { "Hailstorm", "Ice bombardment" }, LearnSecretOptions("Geomancer", 99));
        Assert.Equal(new[] { "Ice bombardment" }, LearnSecretOptions("Geomancer", 99, c => c.Spells.Add(hail.Id)));
    }

    // ---- a sale, and the refusals -------------------------------------------------------------------------------

    /// <summary>A Do buys Ki strike: the trainer's lines word for word, the fee priced on the Do's own path, and the
    /// items and coins taken. Red without the price row (the fee reads FREE and nothing is taken, or the spell is not
    /// offered at all). The list is the Do's unmarked seven at level 99, by name (the Do spells slice); Ki strike is the
    /// sixth.</summary>
    [Fact]
    public void ASaleTakesTheItemsAndCoins()
    {
        var (s, o, c) = Member("Do", 99, ch =>
        {
            ch.Coins = 1_000;
            Give(ch, "acorn", 70);
            Give(ch, "amber", 1);
        });
        var def = Teacher("Do");
        Assert.Equal(new[] { "Learn Secret", "Forget Secret" }, Labels(s, def));

        Converse(s, def, "Learn Secret", Pick(6), Pick(1), Pick(1));

        Assert.Equal(new[]
        {
            "Which secret shall I teach you?|Assess opponent|Blend|Crossing swords|Dash|Hear footstep|Ki strike|Spirit blade",
            "You are ready to learn Ki strike. Do you swear you will use this secret only for good causes?|Yes|No",
            "The fee to learn Ki strike is: Acorn (70), Amber (1), 500 gold, All must be in good condition.|Yes|No",
        }, Menus(o));
        Assert.Equal(new[] { "You have learned Ki strike." }, Boxes(o));
        Assert.Contains(Content.SpellByKey("ki_strike")!.Id, c.Spells);
        Assert.Equal((0, 0, 500u), (Count(c, "acorn"), Count(c, "amber"), c.Coins));
    }

    /// <summary>A Druid one Gold acorn short is refused with the trainer's devotion line and keeps the Topaz and the
    /// book as they were.</summary>
    [Fact]
    public void AMissingItemIsRefusedAndNothingIsTaken()
    {
        var (s, o, c) = Member("Druid", 99, ch => Give(ch, "topaz", 1));
        int before = c.Spells.Count;

        Converse(s, Teacher("Druid"), "Learn Secret", Pick(1), Pick(1), Pick(1));

        Assert.Equal("Which secret shall I teach you?|Earth strike|Lightning storm|Nature's smite", Menus(o)[0]);
        Assert.Equal(new[] { "Paying for what you want is a sign of devotion. Return when you have what is required for this." }, Boxes(o));
        Assert.Equal((1, before), (Count(c, "topaz"), c.Spells.Count));
    }

    /// <summary>A full book (52) is refused with the trainer's line and the spell is not learned. The fee is taken
    /// first, as at every trainer here (the shared <c>SecretLessons.Teach</c> checks the items and coins, takes them,
    /// then learns) and as RTK's <c>learnSpecificSpells</c> does; pinned so that a change to that order is seen.</summary>
    [Fact]
    public void AFullBookIsRefused()
    {
        var smite = Content.SpellByKey("natures_smite")!;
        var (s, o, c) = Member("Druid", 99, ch =>
        {
            ch.Spells.AddRange(Content.Spells.Where(sp => sp.Key.Length > 0 && sp.PathId == 4)
                                      .Select(sp => sp.Id).Distinct().Take(Content.SpellBookCap));
            Give(ch, "gold_acorn", 1);
            Give(ch, "topaz", 1);
        });
        Assert.Equal(Content.SpellBookCap, c.Spells.Count);

        Converse(s, Teacher("Druid"), "Learn Secret", Pick(3), Pick(1), Pick(1));

        Assert.StartsWith("The fee to learn Nature's smite is:", Menus(o)[2]);
        Assert.Equal(new[] { "Your mind cannot hold any more secrets right now." }, Boxes(o));
        Assert.DoesNotContain(smite.Id, c.Spells);
        Assert.Equal(Content.SpellBookCap, c.Spells.Count);
        Assert.Equal((0, 0), (Count(c, "gold_acorn"), Count(c, "topaz")));   // taken, as every trainer takes it
    }

    /// <summary>Anyone but a member of the teacher's own subpath gets one entry, which says one line and nothing more:
    /// the Druid teacher's base class (a Poet), another Poet subpath (a Monk) and an unrelated class (a Warrior).</summary>
    [Theory]
    [InlineData("Poet")]
    [InlineData("Monk")]
    [InlineData("Warrior")]
    public void ANonMemberGetsOneLine(string cls)
    {
        var (s, o, _) = Member(cls, 99, _ => { });
        var def = Teacher("Druid");
        Assert.Equal(new[] { "Speak" }, Labels(s, def));

        Converse(s, def, "Speak");

        Assert.Equal(new[] { "You are not a Druid, I can not help you." }, Boxes(o));
        Assert.Empty(Menus(o));
    }

    /// <summary>The teacher's list is its Circle's, not the player's class's, so who is learning is read again: a
    /// Druid who leaves the subpath (<c>@subpath leave</c>) with the click menu up, or after accepting the oath, is
    /// refused with the non-member line, pays nothing and learns nothing. Red without the two re-reads: the second
    /// buys Earth strike as a Poet, after leaving took the subpath's spells away.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMemberWhoLeavesMidLessonBuysNothing(bool afterTheOath)
    {
        var strike = Content.SpellByKey("earth_strike")!;
        var (s, o, c) = Member("Druid", 99, ch =>
        {
            Give(ch, "gold_acorn", 1);
            Give(ch, "topaz", 1);
        });
        var def = Teacher("Druid");
        Assert.Equal(new[] { "Learn Secret", "Forget Secret" }, Labels(s, def));

        if (afterTheOath) Converse(s, def, "Learn Secret", Pick(1), Pick(1), "@subpath leave", Pick(1));
        else Converse(s, def, "Learn Secret", "@subpath leave");   // the leave lands before the pick runs: see Converse

        Assert.Equal("Poet", c.ClassName);
        Assert.Equal(new[] { "You are not a Druid, I can not help you." }, Boxes(o));
        Assert.DoesNotContain(strike.Id, c.Spells);
        Assert.Equal((1, 1), (Count(c, "gold_acorn"), Count(c, "topaz")));
    }

    /// <summary>A teacher with nothing priced yet says so to its member (Merchant, one of those still unpriced; it was
    /// Barbarian's until the Barbarian spells slice priced Barbarian's).</summary>
    [Fact]
    public void ATeacherWithNothingToSellSaysSo()
    {
        var (s, o, _) = Member("Merchant", 99, _ => { });
        Converse(s, Teacher("Merchant"), "Learn Secret");
        Assert.Equal(new[] { SubpathTeacherAbility.NothingYet }, Boxes(o));
    }

    /// <summary>The base trainers still teach with the same lines through the shared code: a level-5 Warrior buys
    /// Gateway from Tabaek for its archive price. A guard on the extraction (green before it too).</summary>
    [Fact]
    public void ATrainerStillTeachesWithTheSameLines()
    {
        var gateway = Content.SpellByKey("gateway")!;
        var (s, o, c) = Member("Warrior", 5, ch =>
        {
            Give(ch, "acorn", 10);
            Give(ch, "rabbit_meat", 10);
        });
        var tabaek = Content.NpcById(36)!;
        var ctx = new NpcContext(s, TeacherMob(tabaek), tabaek);
        var list = ctx.LearnableSpells();
        int pick = list.FindIndex(sp => sp.Id == gateway.Id) + 1;
        Assert.True(pick > 0, "Tabaek does not offer Gateway at level 5");

        Converse(s, tabaek, "Learn Secret", Pick(pick), Pick(1), Pick(1));

        Assert.Equal(new[]
        {
            "Which secret shall I teach you?|" + string.Join("|", list.Select(sp => sp.Name)),
            $"You are ready to learn {gateway.Name}. Do you swear you will use this secret only for good causes?|Yes|No",
            $"The fee to learn {gateway.Name} is: Acorn (10), Rabbit meat (10), All must be in good condition.|Yes|No",
        }, Menus(o));
        Assert.Equal(new[] { $"You have learned {gateway.Name}." }, Boxes(o));
        Assert.Contains(gateway.Id, c.Spells);
        Assert.Equal((0, 0), (Count(c, "acorn"), Count(c, "rabbit_meat")));
    }

    // ---- the way in: a member's Qui hyang -----------------------------------------------------------------------

    /// <summary>A member's Qui hyang asks RTK's question under the scroll's own icon, with RTK's two choices, and spends
    /// nothing yet. "Subpath Circle" then lands on a walkable tile of RTK's box on their Circle, not the teacher's, and
    /// spends one. Red without the verb (no menu: straight to a tavern).</summary>
    [Fact]
    public void AMembersQuiHyangGoesToTheirCircle()
    {
        var circle = SubpathCircles.For(Content.PathIdForClass("Druid"))!.Value;
        var (s, o, c) = Scroller("Druid");
        try
        {
            s.Receive(SessionFixture.Frame(ClientOp.UseItem, new byte[] { 1 }));

            var menu = MenuBodies(o).Single();
            var scroll = Content.ItemByKey("qui_hyang")!;
            Assert.Equal((2, (int)scroll.ClientIcon), (menu[6], (menu[8] << 8) | menu[9]));
            Assert.Equal(new[] { "Where do you wish to go?|Subpath Circle|Main Inn" }, Menus(o));
            Assert.Equal((2, (ushort)SessionFixture.HomeMap), (Count(c, "qui_hyang"), c.Map));

            s.Receive(MenuFrame(1));

            var tiles = SubpathCircles.ArrivalTiles(circle);
            Assert.Equal(circle.Map, c.Map);
            Assert.Contains((c.X, c.Y), tiles);
            Assert.Equal(1, Count(c, "qui_hyang"));
        }
        finally { _fx.World.LeaveMap(s, c.Map); }
    }

    /// <summary>"Main Inn" is the scroll's old destination, a tavern, and spends one; closing the box moves nobody and
    /// spends nothing.</summary>
    [Fact]
    public void AMembersMainInnIsTheTavernAndClosingSpendsNothing()
    {
        var (s, _, c) = Scroller("Spy");
        try
        {
            s.Receive(SessionFixture.Frame(ClientOp.UseItem, new byte[] { 1 }));
            s.Receive(NextFrame());   // the box closed
            Assert.Equal((2, (ushort)SessionFixture.HomeMap), (Count(c, "qui_hyang"), c.Map));

            s.Receive(SessionFixture.Frame(ClientOp.UseItem, new byte[] { 1 }));
            s.Receive(MenuFrame(2));
            Assert.Contains(c.Map, TavernMaps());
            Assert.Equal(1, Count(c, "qui_hyang"));
        }
        finally { _fx.World.LeaveMap(s, c.Map); }
    }

    /// <summary>Everyone else's Qui hyang is as before: no menu, a tavern, one spent. So is a member's Yellow scroll.
    /// A guard: green before this slice too.</summary>
    [Theory]
    [InlineData("Poet", "qui_hyang")]
    [InlineData("Baekho", "qui_hyang")]
    [InlineData("Druid", "yellow_scroll")]
    public void EveryoneElsesScrollIsUnchanged(string cls, string item)
    {
        var (s, o, c) = Scroller(cls, item);
        try
        {
            s.Receive(SessionFixture.Frame(ClientOp.UseItem, new byte[] { 1 }));
            Assert.Empty(o.BodiesOf(ServerOp.NpcDialog));
            Assert.Contains(c.Map, TavernMaps());
            Assert.Equal(1, Count(c, item));
        }
        finally { _fx.World.LeaveMap(s, c.Map); }
    }

    // ---- the rebuild keeps paid subpath spells ------------------------------------------------------------------

    /// <summary>Devoting at a shrine rebuilds the book: a Druid keeps the Earth strike it bought, and gains the new
    /// alignment's list. Forgotten, it stays forgotten through the next rebuild (renouncing, as the summit does). Red
    /// without the keep: the shrine takes it.</summary>
    [Fact]
    public void AShrineRebuildKeepsAPaidSubpathSpell()
    {
        var strike = Content.SpellByKey("earth_strike")!;
        var (s, _, c) = Member("Druid", 99, ch => ch.Spells.Add(strike.Id));
        var shrineDef = Content.NpcById(188)!;   // the Kwi-Sin shrine's shaman
        var ctx = new NpcContext(s, TeacherMob(shrineDef), shrineDef);

        s.WithState(() => ctx.Devote(1));
        Assert.Equal(1, c.Alignment);
        Assert.Contains(strike.Id, c.Spells);
        Assert.Equal(new[] { strike.Id }, c.Spells.Where(id => Content.IsPcSubpathSpell(Content.SpellById(id)!)));
        Assert.True(c.Spells.Count > 1, "the rebuild gave the Druid none of the Poet list");

        s.WithState(() => { ctx.ForgetSpell(strike.Id); ctx.Devote(0); });
        Assert.Equal(0, c.Alignment);
        Assert.DoesNotContain(strike.Id, c.Spells);
    }

    /// <summary>The keep is for the member's own current subpath: a rebuild into the base class (<c>@class Poet</c>)
    /// or into another subpath drops them, and a book entry of another subpath is never kept.</summary>
    [Fact]
    public void ARebuildIntoAnotherPathDropsThem()
    {
        var strike = Content.SpellByKey("earth_strike")!;
        var hail = Content.SpellByKey("hailstorm")!;
        var held = new[] { strike, hail };
        bool Keeps(int path, SpellDef sp) =>
            Content.RespecSpellSet(path, 99, 0, 0, held: held).Any(s => s.Id == sp.Id);

        Assert.True(Keeps(13, strike));    // Druid keeps its own
        Assert.False(Keeps(13, hail));     // ...never a Geomancer's
        Assert.False(Keeps(4, strike));    // @class Poet
        Assert.False(Keeps(17, strike));   // @class Monk
    }

    /// <summary>The most subpath spells a member can hold today is what their teacher sells (three, for a Druid), and
    /// every PC subpath's rebuilt book with all of them, at every alignment and rank and every Sage rung, still fits
    /// the client's 52 slots, keeping each. Red without the keep (they are not in the book). The worst case is written
    /// to the output.</summary>
    [Fact]
    public void TheMostAMemberCanHoldTodayFitsTheBook()
    {
        var problems = new List<string>();
        var sizes = new List<(int Count, int Path, int Alignment, int Mark, int Held)>();
        foreach (var c in SubpathCircles.All)
        {
            var held = Content.SubpathTeacherSpells(c.Subpath, Content.MarkSpellLevel, Content.MaxMark);
            for (int alignment = 0; alignment <= 3; alignment++)
                for (int mark = 0; mark <= Content.MaxMark; mark++)
                {
                    var book = Content.RespecSpellSet(c.Subpath, Content.MarkSpellLevel, alignment, mark,
                                                      dogFlag: Content.CanLearnDogSpells(c.Subpath),
                                                      sageRung: Content.SageLadder.Length, held: held);
                    sizes.Add((book.Count, c.Subpath, alignment, mark, held.Count));
                    foreach (var sp in held.Where(h => book.All(b => b.Id != h.Id)))
                        problems.Add($"{Content.PathName(c.Subpath)} a{alignment} m{mark}: {sp.Key} not kept");
                    if (book.Count > Content.SpellBookCap)
                        problems.Add($"{Content.PathName(c.Subpath)} a{alignment} m{mark}: {book.Count} slots");
                }
        }
        foreach (var s in sizes.Where(s => s.Mark == Content.MaxMark && s.Alignment == 0).OrderByDescending(s => s.Count))
            _out.WriteLine($"{Content.PathName(s.Path)}: {s.Count} of {Content.SpellBookCap} with {s.Held} held");
        _out.WriteLine($"largest: {sizes.Max(s => s.Count)}");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // ===== plumbing ==============================================================================================

    private static NpcDef Teacher(string subpath)
    {
        var circle = SubpathCircles.For(Content.PathIdForClass(subpath))!.Value;
        return Content.Npcs.Single(n => n.Key == "SubpathTeacherNpc" && n.Map == circle.Map);
    }

    private Mob TeacherMob(NpcDef def)
    {
        var mob = _fx.World.NpcsNear(def.Map, def.X, def.Y, 0).FirstOrDefault(m => m.NpcDefId == def.Id);
        Assert.True(mob is not null, $"{def.Name} is not placed in the world");
        return mob!;
    }

    /// <summary>A socket-free session for a new character of <paramref name="cls"/>, built but not placed on a map,
    /// so a conversation moves nothing in the shared world.</summary>
    private (Session Session, RecordingOutbound Out, Character Character) Member(string cls, int level, Action<Character> configure)
    {
        var c = new Character
        {
            SchemaVersion = Character.CurrentSchemaVersion,
            Id = _fx.World.AllocatePlayerId(),
            Name = $"teach{Interlocked.Increment(ref _serial)}",
            Map = SessionFixture.HomeMap, X = 5, Y = 10,
            ClassName = cls, Level = (byte)level,
        };
        c.SettingFlags &= ~Character.SettingBit(4);   // no advice lines (SessionFixture.PlayerWith says why)
        configure(c);
        var o = new RecordingOutbound($"recorder:{c.Name}");
        return (new Session(o, 2005, _fx.Store, _fx.World, c), o, c);
    }

    /// <summary>A placed level-60 player of <paramref name="cls"/>, alive, holding two of <paramref name="item"/> in
    /// bag slot 1.</summary>
    private (Session Session, RecordingOutbound Out, Character Character) Scroller(string cls, string item = "qui_hyang")
    {
        var def = Content.ItemByKey(item)!;
        return _fx.PlayerWith($"scroll{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.ClassName = cls;
            ch.Level = 60;
            ch.MaxHp = 1_000; ch.Hp = 1_000;
            ch.Inventory.Add(new InvItem { Slot = 0, ItemId = def.Id, Amount = 2 });
        });
    }

    private static HashSet<ushort> TavernMaps() =>
        Content.Inns.Values.SelectMany(v => v).Select(i => (ushort)i.Map).ToHashSet();

    private static void Give(Character c, string key, int amount) =>
        c.Inventory.Add(new InvItem { Slot = (byte)c.Inventory.Count, ItemId = Content.ItemByKey(key)!.Id, Amount = amount });

    private static int Count(Character c, string key) =>
        c.Inventory.Where(i => i.ItemId == Content.ItemByKey(key)!.Id).Sum(i => i.Amount);

    private List<string> Labels(Session s, NpcDef def)
    {
        var ctx = new NpcContext(s, TeacherMob(def), def);
        return NpcScripts.For(def).SelectMany(a => a.Entries(ctx)).Select(e => e.label).ToList();
    }

    /// <summary>The options a level-<paramref name="level"/> member is offered by their teacher's Learn Secret.</summary>
    private List<string> LearnSecretOptions(string subpath, int level, Action<Character>? configure = null)
    {
        var (s, o, _) = Member(subpath, level, configure ?? (_ => { }));
        Converse(s, Teacher(subpath), "Learn Secret", NextFrame());   // close the picker
        return Menus(o).Single().Split('|').Skip(1).ToList();
    }

    private List<string> LearnSecretBoxes(string subpath, int level)
    {
        var (s, o, _) = Member(subpath, level, _ => { });
        Converse(s, Teacher(subpath), "Learn Secret");
        Assert.Empty(Menus(o));
        return Boxes(o);
    }

    private static byte[] Pick(int index) => MenuFrame((byte)index);

    /// <summary>Run <paramref name="label"/>'s entry of <paramref name="def"/>'s menu for this player, inside their
    /// monitor on a thread of its own, then take <paramref name="steps"/> in order, then "next" until the conversation
    /// ends. A step is a <c>0x3A</c> answer frame, or a typed '@' command (a string), sent as the real chat packet. The
    /// entry is picked from the menu as it stands before any step; leading commands run after that and before the
    /// entry does, as a command typed with the click menu still up would.</summary>
    private void Converse(Session s, NpcDef def, string label, params object[] steps)
    {
        var ctx = new NpcContext(s, TeacherMob(def), def);
        var entry = NpcScripts.For(def).SelectMany(a => a.Entries(ctx)).Single(e => e.label == label);
        Task? flow = null;
        Exception? fault = null;
        var t = new Thread(() => fault = Record.Exception(() =>
        {
            int k = 0;
            for (; k < steps.Length && steps[k] is string first; k++) s.Receive(ChatFrame(first));
            s.WithState(() => { flow = entry.run(ctx); });
            for (; k < steps.Length && !flow!.IsCompleted; k++)
                s.Receive(steps[k] is string command ? ChatFrame(command) : (byte[])steps[k]);
            for (int i = 0; i < 20 && !flow!.IsCompleted; i++) s.Receive(NextFrame());
        })) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(Bound), "the dialog thread never finished");
        Assert.Null(fault);
        Assert.True(flow!.IsCompletedSuccessfully, "the dialog was still waiting, or failed");
    }

    /// <summary>A typed line (<c>0x0E</c>): <c>00 len text</c>, the layout PcSubpathTests sends its commands in.</summary>
    private static byte[] ChatFrame(string line)
    {
        var text = Encoding.ASCII.GetBytes(line);
        var body = new byte[2 + text.Length];
        body[1] = (byte)text.Length;
        text.CopyTo(body, 2);
        return SessionFixture.Frame(ClientOp.Chat, body);
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

    private static List<byte[]> MenuBodies(RecordingOutbound o) =>
        o.BodiesOf(ServerOp.NpcDialog).Where(b => b.Length >= 23 && b[0] == 2 && b[1] == 2 && b[7] == 1).ToList();

    /// <summary>Every menu (0x30 kind 2) the player was shown, as "prompt|option|option...": the prompt is the u16BE
    /// length at [21] and ASCII from [23], then a count byte and each option as a length byte and ASCII.</summary>
    private static List<string> Menus(RecordingOutbound o)
    {
        var menus = new List<string>();
        foreach (var b in MenuBodies(o))
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

    /// <summary>Every text box (0x30 type 1) the player was shown, its message (u16BE length at [21], ASCII from [23]).</summary>
    private static List<string> Boxes(RecordingOutbound o) =>
        o.BodiesOf(ServerOp.NpcDialog)
         .Where(b => b.Length >= 23 && b[0] == 0 && b[1] == 1 && b[7] == 1)
         .Select(b => Encoding.ASCII.GetString(b, 23, Math.Min((b[21] << 8) | b[22], b.Length - 23)))
         .ToList();
}
