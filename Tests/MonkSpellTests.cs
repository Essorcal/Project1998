using System.Diagnostics;
using System.Reflection;
using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The Monk spells slice (the project owner, 2026-10-26, P5 "each spell as its RTK script", P1 and P3 "on sale once it
/// works, at its script's price"; the smaller calls as RTK under P5). Before, all nine Monk rows fell to the
/// <c>generic</c> verb, 5 mana and "You cast X." and nothing else (<c>Tests/Fixtures/spell-dispatch-routes.txt</c> had them
/// on <c>verb=generic</c>); Bless drink, which has no script of its own in RTK (its file is Enhanced drink's under the
/// table name <c>enhanced_drink</c>), still does.
///
/// <para>The scripts: <c>RTK-Server/rtklua/Accepted/Spells/Subpaths/Monk/</c>, one file each, every spell bound by its
/// <c>game-data/SpellParams.csv</c> row to a verb in the Monk region of <c>game-data/spell_verbs.lua</c> (Read karma to
/// Diviner's <c>read_karma</c>).</para>
///
/// <para>Every cast is the real 0x0F frame through <c>Session.Receive</c>, by a fresh level-99 Monk facing east, on
/// content-free maps 63400-63420 (no Maps.csv row: no terrain, PvP off) and on Chestnut Arena (3034), a PvP map no other
/// class's facts use. A menu or a box is answered from a thread of its own with no synchronisation context, as a read loop
/// answers it, so the step resumes inline as on the server.</para>
/// </summary>
[Collection("world")]
public sealed class MonkSpellTests
{
    private const ushort KarmaMap = 63400, KarmaFarMap = 63401, ForgiveMap = 63402, ForgiveRefuseMap = 63403,
                         ForgiveHeldMap = 63404, ForgiveGoneMap = 63405, LifeMap = 63406, LifeCostMap = 63407,
                         LifeRefuseMap = 63408, LifeHeldMap = 63409, FoodMap = 63410, AweMap = 63411, AweRefuseMap = 63412,
                         SonKalMap = 63413, FaithMap = 63414, MassMap = 63415, MassRefuseMap = 63416, MassHeldMap = 63417,
                         Arena = 3034;
    // Fix round 1 (#421's review, F2 and F3): content-free too, claimed by no other class.
    private const ushort AimMap = 63418, AimElsewhereMap = 63419, LeavingMap = 63420;
    private const ushort CX = 30, CY = 30;
    private const uint MaxMana = 200_000, Mana = 200_000, Hp = 10_000;
    private const byte Will = 41;

    /// <summary>Slack on a wait or a hold read straight after the cast.</summary>
    private const long SlackMs = 5_000;

    private static readonly FieldInfo NextActionTick =
        typeof(Session).GetField("_nextActionTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ActionCount =
        typeof(Session).GetField("_actionCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ReplacedField =
        typeof(Session).GetField("_replaced", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo LeavingField =
        typeof(Session).GetField("_leaving", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly SessionFixture _fx;
    private static int _serial;

    public MonkSpellTests(SessionFixture fx) => _fx = fx;

    // ===== Read karma ===========================================================================================

    /// <summary>Monk's Read karma is Diviner's on Monk's row: the cast asks a name ("Who's karma shall you read?"); the
    /// name finds the player on another map, the caster pays 250, the 88 s wait is armed under Monk's own key, and the
    /// caster reads the band's words in a box with the next arrow; once they answer it, the player read gets their own box.
    /// Red on 59ae55b: generic's 5 mana and "You cast Read karma.", and no box.</summary>
    [Fact]
    public void ReadKarmaIsDivinersOnMonksRow()
    {
        var (target, tOut, _) = Player(KarmaFarMap, CX, CY, configure: ch => ch.Karma = 3.5, name: Letters("Faithful"));
        var (caster, cOut, cc) = Caster("read_karma_monk", KarmaMap, CX, CY);
        try
        {
            List<Box> asked = new(), mine = new(), theirs = new();
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                asked = Boxes(cOut);
                cOut.Clear();
                caster.Receive(InputFrame(target.CharName));
                mine = Boxes(cOut);
                tOut.Clear();
                caster.Receive(NextFrame());
                theirs = Boxes(tOut);
            });
            long wait = SpellCastSupport.AetherLeft(caster, "read_karma_monk");
            Assert.True(asked is [{ Kind: 0x0404, Text: "Who's karma shall you read?" }]
                        && mine is [{ Kind: 0x0001 } m] && m.Text == $"{target.CharName} is faithful to the teachings of the Gods."
                        && theirs is [{ Kind: 0x0001, Text: "You are faithful to the teachings of the Gods." }]
                        && cc.Mp == Mana - 250 && wait > 88_000 - SlackMs && wait <= 88_000
                        && SpellCastSupport.AetherLeft(caster, "read_karma_diviner") == 0,
                        $"asked [{string.Join(" / ", asked)}], caster's [{string.Join(" / ", mine)}], read's " +
                        $"[{string.Join(" / ", theirs)}], spent {Mana - cc.Mp}, wait {wait} ms");
        }
        finally { Leave(KarmaFarMap, target); Leave(KarmaMap, caster); }
    }

    // ===== Forgive ==============================================================================================

    /// <summary>A Dragon (karma 15) faces a Snake (karma -5): RTK's menu, its question naming the caster as the script
    /// writes it; "Yes, I want to restore them." takes 1 to 2 karma from the caster ("Your karma has decreased." and the
    /// karma graphic 49), and the player takes graphic 49, karma 0, "Your karma has been restored.", been_forgiven 1 in
    /// their saved registry and the legend "Has been forgiven" (icon 12, colour 128); the caster reads "&lt;player&gt;'s
    /// karma has been restored to Cat." in a box. A second Forgive counts 2 and the legend reads "Has been forgiven 2
    /// times", the one legend by that name. No mana, no wait, no "You cast" line. Red on 59ae55b (generic); red with the
    /// target's name not carried to the pick's step; red with the count's legend never rewritten.</summary>
    [Fact]
    public void ForgiveRestoresASnakeToTheCatForOneToTwoOfTheCastersKarma()
    {
        var (target, tOut, tc) = Player(ForgiveMap, CX + 1, CY, configure: ch => ch.Karma = -5, name: Letters("Snake"));
        var (caster, cOut, cc) = Caster("forgive", ForgiveMap, CX, CY, configure: ch => ch.Karma = 15);
        try
        {
            List<string> menus = new(), castLines = new();
            List<Box> boxes = new();
            string casterLines = "", targetLines = "";
            List<int> casterFx = new(), targetFx = new();
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                menus = Menus(cOut);
                castLines = SpellCastSupport.MiniTexts(cOut);
                cOut.Clear();
                tOut.Clear();
                caster.Receive(MenuFrame(1));
                boxes = Boxes(cOut);
                casterLines = Lines(cOut);
                targetLines = Lines(tOut);
                casterFx = Effects(cOut, caster.PlayerId);
                targetFx = Effects(tOut, target.PlayerId);
            });
            double first = cc.Karma;
            int count1 = QuestState.Over(tc, QuestState.Registry).Get("been_forgiven");
            var legend1 = tc.Legends.Where(l => l.Name == "been_forgiven").Select(l => (l.Text, l.Icon, l.Color)).ToList();
            string question = $"Are you sure you want to restore {caster.CharName}'s karma? It will cost between 1 and 2 points " +
                              "of karma to do so.|Yes, I want to restore them.|No, nevermind.";
            Assert.True(menus.SequenceEqual(new[] { question }) && castLines.Count == 0
                        && first >= 13 && first < 14 && casterLines == "Your karma has decreased." && casterFx.Contains(49)
                        && tc.Karma == 0 && targetLines == "Your karma has been restored." && targetFx.Contains(49)
                        && count1 == 1 && legend1.SequenceEqual(new[] { ("Has been forgiven", (byte)12, (byte)128) })
                        && boxes is [{ Kind: 0x0001 } b] && b.Text == $"{target.CharName}'s karma has been restored to Cat."
                        && cc.Mp == Mana && SpellCastSupport.AetherLeft(caster, "forgive") == 0,
                        $"menus [{string.Join(" / ", menus)}], cast lines [{string.Join(" | ", castLines)}]; caster karma " +
                        $"{first}, [{casterLines}] fx [{string.Join(",", casterFx)}]; player karma {tc.Karma}, [{targetLines}] fx " +
                        $"[{string.Join(",", targetFx)}], count {count1}, legend [{string.Join(" / ", legend1)}]; boxes " +
                        $"[{string.Join(" / ", boxes)}]; spent {Mana - cc.Mp}");

            tc.Karma = -5;
            cc.Karma = 15;
            FreeSlot(caster);
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                caster.Receive(MenuFrame(1));
            });
            int count2 = QuestState.Over(tc, QuestState.Registry).Get("been_forgiven");
            var legend2 = tc.Legends.Where(l => l.Name == "been_forgiven").Select(l => l.Text).ToList();
            Assert.True(count2 == 2 && legend2.SequenceEqual(new[] { "Has been forgiven 2 times" }) && tc.Karma == 0,
                        $"second: count {count2}, legend [{string.Join(" / ", legend2)}], karma {tc.Karma}");
        }
        finally { Leave(ForgiveMap, caster, target); }
    }

    /// <summary>Forgive's refusals, each with no karma, count, legend or mana changed: below Dragon karma (13.9), the box
    /// "Your soul is too impure restore karma. Improve your karma first."; at a player above the snake (-2.9), the box
    /// "&lt;player&gt;'s karma is not Snake level."; "No, nevermind.", the box "Very well."; at a creature with Dragon karma,
    /// nothing at all (RTK's script stops on the creature's missing karma); at a creature below it, the impure box; facing
    /// nothing, nothing. Red on 59ae55b (generic's line and 5 mana each time).</summary>
    [Theory]
    [InlineData("impure")]
    [InlineData("not snake")]
    [InlineData("nevermind")]
    [InlineData("creature dragon")]
    [InlineData("creature impure")]
    [InlineData("nothing")]
    public void ForgiveRefusals(string refusal)
    {
        int row = Array.IndexOf(new[] { "impure", "not snake", "nevermind", "creature dragon", "creature impure", "nothing" }, refusal);
        int y = CY + 4 * row;
        double casterKarma = refusal is "impure" or "creature impure" ? 13.9 : 15;
        double targetKarma = refusal == "not snake" ? -2.9 : -5;
        bool player = refusal is "impure" or "not snake" or "nevermind";
        bool creature = refusal.StartsWith("creature", StringComparison.Ordinal);
        Session? target = null;
        Character? tc = null;
        if (player) (target, _, tc) = Player(ForgiveRefuseMap, CX + 1, y, configure: ch => ch.Karma = targetKarma, name: Letters("Refused"));
        var mob = creature ? AddCreature(ForgiveRefuseMap, CX + 1, y) : null;
        var (caster, cOut, cc) = Caster("forgive", ForgiveRefuseMap, CX, y, configure: ch => ch.Karma = casterKarma);
        try
        {
            List<Box> boxes = new();
            List<string> menus = new();
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                menus = Menus(cOut);
                if (refusal == "nevermind") caster.Receive(MenuFrame(2));
                boxes = Boxes(cOut);
            });
            string want = refusal switch
            {
                "impure" or "creature impure" => "Your soul is too impure restore karma. Improve your karma first.",
                "not snake" => $"{target!.CharName}'s karma is not Snake level.",
                "nevermind" => "Very well.",
                _ => "",
            };
            bool shown = want.Length == 0 ? boxes.Count == 0 : boxes is [{ Kind: 0x0001 } b] && b.Text == want;
            bool unchanged = cc.Karma == casterKarma && cc.Mp == Mana && Lines(cOut) == ""
                             && (tc is null || (tc.Karma == targetKarma && tc.Legends.All(l => l.Name != "been_forgiven")
                                                && QuestState.Over(tc, QuestState.Registry).Get("been_forgiven") == 0));
            Assert.True(shown && unchanged && menus.Count == (refusal == "nevermind" ? 1 : 0),
                        $"{refusal}: boxes [{string.Join(" / ", boxes)}] (want '{want}'), menus {menus.Count}, lines " +
                        $"[{Lines(cOut)}], caster karma {cc.Karma}, spent {Mana - cc.Mp}, player karma {tc?.Karma}");
        }
        finally
        {
            Leave(ForgiveRefuseMap, caster);
            if (target is not null) Leave(ForgiveRefuseMap, target);
            if (mob is not null) _fx.World.DespawnMob(ForgiveRefuseMap, mob);
        }
    }

    /// <summary>A player who has gone by the pick (here, a session a second login replaced, which the name lookup passes
    /// over) is restored nothing, and the caster pays nothing and reads nothing: the pick finds its player again by name.
    /// Red with the pick's lookup taken out (the caster paid 1 to 2 karma for nothing).</summary>
    [Fact]
    public void ForgiveRestoresNothingToAPlayerGoneBeforeThePick()
    {
        var (target, _, tc) = Player(ForgiveGoneMap, CX + 1, CY, configure: ch => ch.Karma = -5, name: Letters("Gone"));
        var (caster, cOut, cc) = Caster("forgive", ForgiveGoneMap, CX, CY, configure: ch => ch.Karma = 15);
        try
        {
            List<Box> boxes = new();
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                ReplacedField.SetValue(target, 1);
                cOut.Clear();
                caster.Receive(MenuFrame(1));
                boxes = Boxes(cOut);
            });
            Assert.True(cc.Karma == 15 && tc.Karma == -5 && Lines(cOut) == "" && boxes.Count == 0
                        && tc.Legends.All(l => l.Name != "been_forgiven"),
                        $"caster karma {cc.Karma}, player karma {tc.Karma}, lines [{Lines(cOut)}], boxes [{string.Join(" / ", boxes)}]");
        }
        finally
        {
            ReplacedField.SetValue(target, 0);
            Leave(ForgiveGoneMap, caster, target);
        }
    }

    /// <summary>The player's karma, count and legend are written inside their own monitor: while a third thread holds it,
    /// the pick's step waits and the player is untouched, in both <c>StateRank</c> orders. Red with
    /// <c>Session.ReceiveKarmaRestore</c> outside the player's monitor (a Release build).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ForgiveWritesThePlayerInsideTheirOwnMonitor(bool casterRanksFirst)
    {
        Session caster, target;
        Character tc;
        if (casterRanksFirst)
        {
            (caster, _, _) = Caster("forgive", ForgiveHeldMap, CX, CY, configure: ch => ch.Karma = 15);
            (target, _, tc) = Player(ForgiveHeldMap, CX + 1, CY, configure: ch => ch.Karma = -5, name: Letters("Held"));
        }
        else
        {
            (target, _, tc) = Player(ForgiveHeldMap, CX + 1, CY + 6, configure: ch => ch.Karma = -5, name: Letters("Held"));
            (caster, _, _) = Caster("forgive", ForgiveHeldMap, CX, CY + 6, configure: ch => ch.Karma = 15);
        }
        Assert.Equal(casterRanksFirst, caster.StateRank < target.StateRank);
        var run = new HeldRun();
        try
        {
            OnOwnThread(() => caster.Receive(SpellCastSupport.CastFrame(0)));
            WhileAnotherThreadHolds(run, target, () => caster.Receive(MenuFrame(1)),
                                    untouched: () => tc.Karma == -5 && tc.Legends.All(l => l.Name != "been_forgiven"));
            Assert.Null(run.HolderFault);
            Assert.Null(run.ActorFault);
            Assert.False(run.FinishedWhileHeld, "the pick's step finished while another thread held the player's monitor");
            Assert.True(run.UntouchedWhileHeld, "the player was written while another thread held their monitor");
            Assert.True(tc.Karma == 0 && tc.Legends.Any(l => l.Name == "been_forgiven"), $"after: karma {tc.Karma}");
        }
        finally { if (run.Settled) Leave(ForgiveHeldMap, caster, target); }
    }

    // ===== Second life ==========================================================================================

    /// <summary>At a dead player, faced or aimed at by id: 6,000 mana, the 1,250 s wait and "You cast Second life."; the
    /// player comes back where they lie with 600 vita (or their max, when it is less) and the mana they had, with graphic
    /// 18 and "&lt;caster&gt; cast Second life on you.". Red on 59ae55b: generic's 5 mana, nobody revived.</summary>
    [Theory]
    [InlineData(false, 10_000u, 600u)]
    [InlineData(true, 10_000u, 600u)]
    [InlineData(false, 400u, 400u)]
    public void SecondLifeRaisesTheDeadPlayerItIsAimedAt(bool aimed, uint maxHp, uint want)
    {
        int y = CY + 4 * Interlocked.Increment(ref _serial) % 60;
        var (target, tOut, tc) = Player(LifeMap, aimed ? CX + 6 : CX + 1, y, hp: 0, maxHp: maxHp, mana: 77);
        var (caster, cOut, cc) = Caster("second_life", LifeMap, CX, y);
        try
        {
            Assert.True(target.IsDead);
            caster.Receive(SpellCastSupport.CastFrame(0, aimed ? target.PlayerId : null));
            long wait = SpellCastSupport.AetherLeft(caster, "second_life");
            string theirs = Lines(tOut).Replace(caster.CharName, "<caster>", StringComparison.Ordinal);
            Assert.True(Lines(cOut) == "You cast Second life." && tc.Hp == want && tc.Mp == 77 && !target.IsDead
                        && theirs.Contains("<caster> cast Second life on you.") && Effects(tOut, target.PlayerId).Contains(18)
                        && cc.Mp == Mana - 6_000 && wait > 1_250_000 - SlackMs && wait <= 1_250_000,
                        $"lines [{Lines(cOut)}], player {tc.Hp}/{tc.Mp} dead {target.IsDead}, theirs [{theirs}], fx " +
                        $"[{string.Join(",", Effects(tOut, target.PlayerId))}], spent {Mana - cc.Mp}, wait {wait} ms");
        }
        finally { Leave(LifeMap, caster, target); }
    }

    /// <summary>The mana and the wait go whatever it was aimed at, as the script spends before it looks: at a living
    /// player, "You cast Second life." and nothing more; at a creature, or at a dead player on a PvP map (one the caster
    /// may attack), "You can't do that." and nobody comes back. Red on 59ae55b.</summary>
    [Theory]
    [InlineData("living")]
    [InlineData("creature")]
    [InlineData("pvp")]
    public void SecondLifeCostsWhateverItIsAimedAt(string at)
    {
        ushort map = at == "pvp" ? Arena : LifeCostMap;
        int y = at switch { "living" => CY, "creature" => CY + 4, _ => 14 };
        Session? other = null;
        Character? oc = null;
        if (at != "creature") (other, _, oc) = Player(map, CX + 1, y, hp: at == "living" ? 5_000u : 0u);
        var mob = at == "creature" ? AddCreature(map, CX + 1, y) : null;
        var (caster, cOut, cc) = Caster("second_life", map, CX, y);
        try
        {
            if (at == "pvp") Assert.True(Content.IsPvpMap(Arena));
            caster.Receive(SpellCastSupport.CastFrame(0));
            long wait = SpellCastSupport.AetherLeft(caster, "second_life");
            string want = at == "living" ? "You cast Second life." : "You can't do that.";
            uint hp = at == "living" ? 5_000u : 0u;
            Assert.True(Lines(cOut) == want && cc.Mp == Mana - 6_000 && wait > 1_250_000 - SlackMs
                        && (oc is null || oc.Hp == hp),
                        $"{at}: lines [{Lines(cOut)}] (want '{want}'), spent {Mana - cc.Mp}, wait {wait} ms, other {oc?.Hp}");
        }
        finally
        {
            Leave(map, caster);
            if (other is not null) Leave(map, other);
            if (mob is not null) _fx.World.DespawnMob(map, mob);
        }
    }

    /// <summary>Short of 6,000, "Your will is too weak.", free, no wait; facing nothing, nothing at all, free; inside the
    /// wait, "Second life isn't ready yet.", free. Red on 59ae55b (generic's line and 5 mana).</summary>
    [Fact]
    public void SecondLifeRefusals()
    {
        var (dead, _, dc) = Player(LifeRefuseMap, CX + 1, CY, hp: 0);
        var (poor, pOut, pc) = Caster("second_life", LifeRefuseMap, CX, CY, mana: 5_999);
        var (alone, aOut, ac) = Caster("second_life", LifeRefuseMap, CX, CY + 6);
        try
        {
            poor.Receive(SpellCastSupport.CastFrame(0));
            alone.Receive(SpellCastSupport.CastFrame(0));
            string short_ = Lines(pOut), nothing = Lines(aOut);
            SpellCastSupport.ClearAether(alone, "second_life");
            alone.WithState(() => alone.LuaSetCooldown("second_life", 60_000));
            FreeSlot(alone);
            alone.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(short_ == "Your will is too weak." && pc.Mp == 5_999 && SpellCastSupport.AetherLeft(poor, "second_life") == 0
                        && dc.Hp == 0 && nothing == "" && Lines(aOut) == "Second life isn't ready yet." && ac.Mp == Mana,
                        $"short [{short_}] mana {pc.Mp}; nothing [{nothing}]; waiting [{Lines(aOut)}] spent {Mana - ac.Mp}");
        }
        finally { Leave(LifeRefuseMap, dead, poor, alone); }
    }

    /// <summary>The revive is decided and written inside the player's own monitor: while a third thread holds it, the cast
    /// waits and the player stays dead, in both <c>StateRank</c> orders. Red with <c>Session.ReceiveRaise</c> outside the
    /// player's monitor (a Release build).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SecondLifeRaisesInsideThePlayersOwnMonitor(bool casterRanksFirst)
    {
        int y = casterRanksFirst ? CY : CY + 6;
        Session caster, target;
        Character tc;
        if (casterRanksFirst)
        {
            (caster, _, _) = Caster("second_life", LifeHeldMap, CX, y);
            (target, _, tc) = Player(LifeHeldMap, CX + 1, y, hp: 0);
        }
        else
        {
            (target, _, tc) = Player(LifeHeldMap, CX + 1, y, hp: 0);
            (caster, _, _) = Caster("second_life", LifeHeldMap, CX, y);
        }
        Assert.Equal(casterRanksFirst, caster.StateRank < target.StateRank);
        var run = new HeldRun();
        try
        {
            WhileAnotherThreadHolds(run, target, () => caster.Receive(SpellCastSupport.CastFrame(0)), untouched: () => tc.Hp == 0);
            Assert.Null(run.HolderFault);
            Assert.Null(run.ActorFault);
            Assert.False(run.FinishedWhileHeld, "the cast finished while another thread held the player's monitor");
            Assert.True(run.UntouchedWhileHeld, "the player was revived while another thread held their monitor");
            Assert.True(tc.Hp == 600 && !target.IsDead, $"after: vita {tc.Hp}");
        }
        finally { if (run.Settled) Leave(LifeHeldMap, caster, target); }
    }

    // ===== Bless food ===========================================================================================

    /// <summary>The typed answer, lowered: "mountain ginseng" with a Mountain ginseng in the bag, "Red fog spreads around
    /// Mountain ginseng."; without one, "Oh! if only you had that."; anything else, "You can't imbibe that.". Every answer
    /// arms the 150 s wait, and no mana is asked or spent. Red on 59ae55b: generic's 5 mana and "You cast Bless food.";
    /// red with the answer not lowered.</summary>
    [Theory]
    [InlineData("mountain ginseng", true, "Red fog spreads around Mountain ginseng.")]
    [InlineData("Mountain Ginseng", true, "Red fog spreads around Mountain ginseng.")]
    [InlineData("mountain ginseng", false, "Oh! if only you had that.")]
    [InlineData("water jug", true, "You can't imbibe that.")]
    public void BlessFoodSaysItsLineAndArmsItsWait(string answer, bool ginseng, string expected)
    {
        var item = Content.ItemByKey("mountain_ginseng")!;
        var (caster, cOut, cc) = Caster("bless_food", FoodMap, CX + 4 * Interlocked.Increment(ref _serial) % 60, CY, configure: ch =>
        {
            if (ginseng) ch.Inventory.Add(new InvItem { Slot = 0, ItemId = item.Id, Amount = 1 });
        });
        try
        {
            caster.Receive(AnswerFrame(0, answer));
            long wait = SpellCastSupport.AetherLeft(caster, "bless_food");
            Assert.True(Lines(cOut) == expected && cc.Mp == Mana && wait > 150_000 - SlackMs && wait <= 150_000,
                        $"[{Lines(cOut)}], spent {Mana - cc.Mp}, wait {wait} ms");
        }
        finally { Leave(FoodMap, caster); }
    }

    /// <summary>Inside its wait, "Bless food isn't ready yet.", and nothing else. Red on 59ae55b.</summary>
    [Fact]
    public void BlessFoodWaitsItsTime()
    {
        var (caster, cOut, _) = Caster("bless_food", FoodMap, CX, CY + 10);
        try
        {
            caster.Receive(AnswerFrame(0, "rice"));
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(AnswerFrame(0, "mountain ginseng"));
            Assert.Equal("Bless food isn't ready yet.", Lines(cOut));
        }
        finally { Leave(FoodMap, caster); }
    }

    // ===== Awe ==================================================================================================

    /// <summary>For 2,000 mana, the 125 s wait and "You cast Awe.", with sound 70 at the caster: every creature within 10
    /// tiles on both axes (the corners at 10 and 10 included, a boss too) is held in the paralysis slot for 15 s (the
    /// script's 10,000 as RTK's creature timer counts it), with graphic 11 over it; one 11 tiles off, an NPC, and a
    /// creature already paralyzed by another spell are left as they were. Red on 59ae55b (generic: 5 mana, nothing held);
    /// red with the range test taken out (the 11-tile creature held).</summary>
    [Fact]
    public void AweHoldsEveryCreatureWithinTenTiles()
    {
        var near = AddCreature(AweMap, CX + 3, CY);
        var corner = AddCreature(AweMap, CX + 10, CY + 10);
        var other = AddCreature(AweMap, CX - 10, CY - 10);
        var boss = AddCreature(AweMap, CX, CY + 5);
        boss.IsBoss = true;
        var beyond = AddCreature(AweMap, CX + 11, CY);
        var npc = AddCreature(AweMap, CX + 1, CY, npc: true);
        var held = AddCreature(AweMap, CX + 2, CY);
        Assert.True(_fx.World.ApplyMobStatus(held, "paras", 60_000, hold: true, blind: false, spellKey: "paralyze_mage"));
        long heldUntil = held.FrozenUntil;
        var (caster, cOut, cc) = Caster("awe", AweMap, CX, CY);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            long now = Environment.TickCount64;
            long wait = SpellCastSupport.AetherLeft(caster, "awe");
            bool Held(Mob m) => _fx.World.MobStatusKey(m, "paras") == "awe" && m.FrozenUntil - now > 15_000 - SlackMs
                                && m.FrozenUntil - now <= 15_000 && Effects(cOut, m.Id).Contains(11);
            bool Free(Mob m) => _fx.World.MobStatusKey(m, "paras") == "" && m.FrozenUntil <= now && !Effects(cOut, m.Id).Contains(11);
            Assert.True(Held(near) && Held(corner) && Held(other) && Held(boss) && Free(beyond) && Free(npc)
                        && _fx.World.MobStatusKey(held, "paras") == "paralyze_mage" && held.FrozenUntil == heldUntil
                        && !Effects(cOut, held.Id).Contains(11)
                        && Lines(cOut) == "You cast Awe." && Sounds(cOut).Contains(70)
                        && cc.Mp == Mana - 2_000 && wait > 125_000 - SlackMs && wait <= 125_000,
                        $"near {Show(near, now)}, corner {Show(corner, now)}, other {Show(other, now)}, boss {Show(boss, now)}, " +
                        $"beyond {Show(beyond, now)}, npc {Show(npc, now)}, held {Show(held, now)}; lines [{Lines(cOut)}], sounds " +
                        $"[{string.Join(",", Sounds(cOut))}], spent {Mana - cc.Mp}, wait {wait} ms");
        }
        finally
        {
            Leave(AweMap, caster);
            foreach (var m in new[] { near, corner, other, boss, beyond, npc, held }) _fx.World.DespawnMob(AweMap, m);
        }
    }

    /// <summary>With nobody near it still costs 2,000 and arms the wait; inside it, "Awe isn't ready yet."; short of 2,000,
    /// "Your will is too weak.", free. Red on 59ae55b (generic's 5 mana).</summary>
    [Fact]
    public void AweCostsWithNobodyNearAndRefusesShortOrWaiting()
    {
        var (caster, cOut, cc) = Caster("awe", AweRefuseMap, CX, CY);
        var (poor, pOut, pc) = Caster("awe", AweRefuseMap, CX, CY + 30, mana: 1_999);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string first = Lines(cOut);
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            poor.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(first == "You cast Awe." && cc.Mp == Mana - 2_000 && Lines(cOut) == "Awe isn't ready yet."
                        && Lines(pOut) == "Your will is too weak." && pc.Mp == 1_999 && SpellCastSupport.AetherLeft(poor, "awe") == 0,
                        $"first [{first}] spent {Mana - cc.Mp}; again [{Lines(cOut)}]; short [{Lines(pOut)}] mana {pc.Mp}");
        }
        finally { Leave(AweRefuseMap, caster, poor); }
    }

    // ===== Son-Kal ==============================================================================================

    /// <summary>The caster shouts "Son-Kal~!", keeps 90% of their vita (9,000 of 10,000), pays 1,000 and a 10 s wait, and
    /// the faced creature takes 1,250 + 10% of the caster's vita before the cost, 2,250 (its AC 0), with graphic 7 and sound
    /// 14; a player standing there off a PvP map is passed over. Lines "You cast Son-Kal." and "You can not touch the spirit
    /// world.". With nothing faced the cost is paid all the same; short of 1,000, "Your will is too weak.", free. Red on
    /// 59ae55b (generic: 5 mana and a Will blow).</summary>
    [Fact]
    public void SonKalStrikesTheFacedCreatureForATenthOfTheCastersVita()
    {
        var mob = AddCreature(SonKalMap, CX + 1, CY);
        var (bystander, _, bc) = Player(SonKalMap, CX + 1, CY);
        var (caster, cOut, cc) = Caster("son_kal", SonKalMap, CX, CY);
        var (empty, eOut, ec) = Caster("son_kal", SonKalMap, CX, CY + 6);
        var (poor, pOut, pc) = Caster("son_kal", SonKalMap, CX, CY + 12, mana: 999);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            empty.Receive(SpellCastSupport.CastFrame(0));
            poor.Receive(SpellCastSupport.CastFrame(0));
            long wait = SpellCastSupport.AetherLeft(caster, "son_kal");
            const string two = "You cast Son-Kal. | You can not touch the spirit world.";
            Assert.True(mob.Hp == 100_000 - 2_250 && cc.Hp == 9_000 && cc.Mp == Mana - 1_000 && wait > 10_000 - SlackMs
                        && wait <= 10_000 && Lines(cOut) == two && Speech(cOut).Any(s => s.Contains("Son-Kal~!"))
                        && Effects(cOut, mob.Id).Contains(7) && Sounds(cOut).Contains(14) && bc.Hp == Hp
                        && ec.Hp == 9_000 && ec.Mp == Mana - 1_000 && Lines(eOut) == two
                        && Lines(pOut) == "Your will is too weak." && pc.Hp == Hp && pc.Mp == 999,
                        $"creature {mob.Hp}, vita {cc.Hp}, spent {Mana - cc.Mp}, wait {wait}, lines [{Lines(cOut)}], bystander " +
                        $"{bc.Hp}; empty vita {ec.Hp} [{Lines(eOut)}]; short [{Lines(pOut)}] vita {pc.Hp} mana {pc.Mp}");
        }
        finally { Leave(SonKalMap, caster, bystander, empty, poor); _fx.World.DespawnMob(SonKalMap, mob); }
    }

    /// <summary>On a PvP map the faced player takes the blow too: 2,250 of their 10,000 (AC 0). Red on 59ae55b: nothing
    /// struck.</summary>
    [Fact]
    public void SonKalStrikesTheFacedPlayerOnAPvpMap()
    {
        Assert.True(Content.IsPvpMap(Arena));
        var (other, _, oc) = Player(Arena, 11, 10);
        var (caster, _, cc) = Caster("son_kal", Arena, 10, 10);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(oc.Hp == Hp - 2_250 && cc.Hp == 9_000, $"other {oc.Hp}, caster {cc.Hp}");
        }
        finally { Leave(Arena, caster, other); }
    }

    // ===== Faiths call ==========================================================================================

    /// <summary>A creature 10 tiles off takes 700 + floor(99 / 2) + floor((41 + 3) / 4) = 760 (its AC 0), with graphic 4
    /// (its sound 56 plays there, beyond the caster's hearing) and "You cast Faiths call."; the 150 mana is checked and
    /// never taken, and there is no wait. At 11 tiles, nothing at all, free; at an NPC, nothing; short of 150, "Your will
    /// is too weak."; at a player off a PvP map, "You can't attack that target."; each free. Red on 59ae55b (generic: 5
    /// mana and a Will blow at any reach).</summary>
    [Fact]
    public void FaithsCallStrikesWithinTenTilesAndTakesNoMana()
    {
        var near = AddCreature(FaithMap, CX + 10, CY + 3);
        var beyond = AddCreature(FaithMap, CX - 11, CY);
        var npc = AddCreature(FaithMap, CX, CY - 5, npc: true);
        var (other, _, oc) = Player(FaithMap, CX + 1, CY + 12);
        var (caster, cOut, cc) = Caster("faiths_call", FaithMap, CX, CY);
        var (poor, pOut, pc) = Caster("faiths_call", FaithMap, CX, CY + 6, mana: 149);
        var (town, tOut, tc) = Caster("faiths_call", FaithMap, CX, CY + 12);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0, beyond.Id));
            string far = Lines(cOut);
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0, npc.Id));
            string atNpc = Lines(cOut);
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0, near.Id));
            poor.Receive(SpellCastSupport.CastFrame(0, near.Id));
            town.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(far == "" && atNpc == "" && beyond.Hp == 100_000 && npc.Hp == 100_000
                        && near.Hp == 100_000 - 760 && Lines(cOut) == "You cast Faiths call."
                        && Effects(cOut, near.Id).Contains(4) && cc.Mp == Mana
                        && SpellCastSupport.AetherLeft(caster, "faiths_call") == 0
                        && Lines(pOut) == "Your will is too weak." && pc.Mp == 149
                        && Lines(tOut) == "You can't attack that target." && oc.Hp == Hp && tc.Mp == Mana,
                        $"far [{far}] {beyond.Hp}; npc [{atNpc}] {npc.Hp}; near {near.Hp} (want {100_000 - 760}), lines " +
                        $"[{Lines(cOut)}], spent {Mana - cc.Mp}; short [{Lines(pOut)}]; town [{Lines(tOut)}] other {oc.Hp}");
        }
        finally
        {
            Leave(FaithMap, caster, poor, town, other);
            foreach (var m in new[] { near, beyond, npc }) _fx.World.DespawnMob(FaithMap, m);
        }
    }

    /// <summary>On a PvP map the faced player takes it: 760 of their 10,000 (AC 0), with graphic 4 and sound 56 over them.
    /// Red on 59ae55b (generic's Will blow).</summary>
    [Fact]
    public void FaithsCallStrikesAPlayerOnAPvpMap()
    {
        Assert.True(Content.IsPvpMap(Arena));
        var (other, oOut, oc) = Player(Arena, 11, 18);
        var (caster, _, cc) = Caster("faiths_call", Arena, 10, 18);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(oc.Hp == Hp - 760 && cc.Mp == Mana && Effects(oOut, other.PlayerId).Contains(4) && Sounds(oOut).Contains(56),
                        $"other {oc.Hp}, spent {Mana - cc.Mp}, fx [{string.Join(",", Effects(oOut, other.PlayerId))}], sounds " +
                        $"[{string.Join(",", Sounds(oOut))}]");
        }
        finally { Leave(Arena, caster, other); }
    }

    // ===== Mass resurrect =======================================================================================

    /// <summary>For 15,000 mana, the 900 s wait and "You cast Mass resurrect.", every dead player on the caster's map, near
    /// or 60 tiles off, comes back where they lie with full vita and full mana, graphic 11 and sound 112 over them and
    /// "&lt;caster&gt; cast Mass resurrect on you."; a living player and a dead player on another map are left as they were.
    /// Red on 59ae55b (generic: 5 mana, nobody revived).</summary>
    [Fact]
    public void MassResurrectRaisesEveryDeadPlayerOnTheMapToFull()
    {
        var (near, nOut, nc) = Player(MassMap, CX + 2, CY, hp: 0, maxHp: 9_000, mana: 10, maxMana: 7_000);
        var (far, fOut, fc) = Player(MassMap, CX + 60, CY + 40, hp: 0, maxHp: 8_000, mana: 0, maxMana: 6_000);
        var (alive, _, ac) = Player(MassMap, CX - 2, CY, hp: 500, maxHp: 9_000, mana: 20, maxMana: 7_000);
        var (elsewhere, _, xc) = Player(MassRefuseMap, CX, CY + 20, hp: 0, maxHp: 9_000);
        var (caster, cOut, cc) = Caster("mass_resurrect", MassMap, CX, CY);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            long wait = SpellCastSupport.AetherLeft(caster, "mass_resurrect");
            string N(RecordingOutbound o) => Lines(o).Replace(caster.CharName, "<caster>", StringComparison.Ordinal);
            bool Back(Session s, RecordingOutbound o, Character c, uint hp, uint mp) =>
                !s.IsDead && c.Hp == hp && c.Mp == mp && N(o).Contains("<caster> cast Mass resurrect on you.")
                && Effects(o, s.PlayerId).Contains(11) && Sounds(o).Contains(112);
            Assert.True(Back(near, nOut, nc, 9_000, 7_000) && Back(far, fOut, fc, 8_000, 6_000)
                        && ac.Hp == 500 && ac.Mp == 20 && xc.Hp == 0 && elsewhere.IsDead
                        && Lines(cOut) == "You cast Mass resurrect." && cc.Mp == Mana - 15_000
                        && wait > 900_000 - SlackMs && wait <= 900_000,
                        $"near {nc.Hp}/{nc.Mp} [{N(nOut)}], far {fc.Hp}/{fc.Mp} [{N(fOut)}], living {ac.Hp}/{ac.Mp}, elsewhere " +
                        $"{xc.Hp}; lines [{Lines(cOut)}], spent {Mana - cc.Mp}, wait {wait} ms");
        }
        finally { Leave(MassMap, near, far, alive, caster); Leave(MassRefuseMap, elsewhere); }
    }

    /// <summary>On a PvP map the caster may attack everyone, so nobody comes back, and the cast still costs 15,000 and the
    /// wait; inside it, "Mass resurrect isn't ready yet."; short of 15,000, "Your will is too weak.", free. Red on 59ae55b
    /// (generic's 5 mana).</summary>
    [Fact]
    public void MassResurrectOnAPvpMapRaisesNobodyAndStillCosts()
    {
        Assert.True(Content.IsPvpMap(Arena));
        var (dead, _, dc) = Player(Arena, 12, 26, hp: 0);
        var (caster, cOut, cc) = Caster("mass_resurrect", Arena, 10, 26);
        var (poor, pOut, pc) = Caster("mass_resurrect", MassRefuseMap, CX, CY, mana: 14_999);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string first = Lines(cOut);
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            poor.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(first == "You cast Mass resurrect." && dc.Hp == 0 && dead.IsDead && cc.Mp == Mana - 15_000
                        && Lines(cOut) == "Mass resurrect isn't ready yet."
                        && Lines(pOut) == "Your will is too weak." && pc.Mp == 14_999,
                        $"first [{first}], dead {dc.Hp}, spent {Mana - cc.Mp}; again [{Lines(cOut)}]; short [{Lines(pOut)}]");
        }
        finally { Leave(Arena, dead, caster); Leave(MassRefuseMap, poor); }
    }

    /// <summary>Each revive is decided and written inside that player's own monitor: while a third thread holds it, the
    /// cast waits and the player stays dead, in both <c>StateRank</c> orders. Red with <c>Session.ReceiveRaise</c> outside
    /// the player's monitor (a Release build).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MassResurrectRaisesEachPlayerInsideTheirOwnMonitor(bool casterRanksFirst)
    {
        Session caster, target;
        Character tc;
        if (casterRanksFirst)
        {
            (caster, _, _) = Caster("mass_resurrect", MassHeldMap, CX, CY);
            (target, _, tc) = Player(MassHeldMap, CX + 5, CY, hp: 0);
        }
        else
        {
            (target, _, tc) = Player(MassHeldMap, CX + 5, CY, hp: 0);
            (caster, _, _) = Caster("mass_resurrect", MassHeldMap, CX, CY);
        }
        Assert.Equal(casterRanksFirst, caster.StateRank < target.StateRank);
        var run = new HeldRun();
        try
        {
            WhileAnotherThreadHolds(run, target, () => caster.Receive(SpellCastSupport.CastFrame(0)), untouched: () => tc.Hp == 0);
            Assert.Null(run.HolderFault);
            Assert.Null(run.ActorFault);
            Assert.False(run.FinishedWhileHeld, "the cast finished while another thread held the player's monitor");
            Assert.True(run.UntouchedWhileHeld, "the player was revived while another thread held their monitor");
            Assert.True(tc.Hp == tc.MaxHp && !target.IsDead, $"after: vita {tc.Hp}");
        }
        finally { if (run.Settled) Leave(MassHeldMap, caster, target); }
    }

    // ===== the aimed target (fix round 1: #421's review, F2) ===================================================

    /// <summary>Second life takes an aimed id as RTK's engine takes it (<c>clif_parsemagic</c>: the block the id names, on
    /// the caster's map within 21 tiles; a creature while alive; an NPC or anything else, no script), never falling back
    /// to the faced tile, where a ghost lies in every case: a creature's id, "You can't do that." for the 6,000 and the
    /// wait; an NPC's id, a player's on another map, or a ghost's 22 tiles off, nothing at all, free; a ghost's 21 tiles
    /// off, that ghost back with 600. The faced ghost stays a ghost each time. Red on 6dfaad3, whose player resolver fell
    /// back to the faced tile and revived the faced ghost (the review's probes A, C and E). Fix round 2 (the re-check's
    /// R2) adds four more RTK answers: a dead creature's id, an id that names nothing, and a creature's id on another map,
    /// nothing, free; the caster's own id, "You cast Second life." for the 6,000 and the wait, and nothing more (the caster
    /// is alive).</summary>
    [Theory]
    [InlineData("creature")]
    [InlineData("npc")]
    [InlineData("off map")]
    [InlineData("22 tiles")]
    [InlineData("21 tiles")]
    [InlineData("dead creature")]
    [InlineData("missing")]
    [InlineData("own")]
    [InlineData("creature off map")]
    public void SecondLifeTakesAnAimedIdAsRtkDoes(string aim)
    {
        int y = 10 + 6 * Array.IndexOf(new[] { "creature", "npc", "off map", "22 tiles", "21 tiles" }, aim);
        if (y < 10) y = 82 + 4 * Array.IndexOf(new[] { "dead creature", "missing", "own", "creature off map" }, aim);
        var (faced, _, fc) = Player(AimMap, CX + 1, y, hp: 0);
        Session? far = null;
        Character? farc = null;
        Mob? mob = null;
        ushort farMap = AimMap;
        switch (aim)
        {
            case "creature": mob = AddCreature(AimMap, CX + 3, y); break;
            case "npc": mob = AddCreature(AimMap, CX + 3, y, npc: true); break;
            case "off map": farMap = AimElsewhereMap; (far, _, farc) = Player(AimElsewhereMap, CX + 3, y, hp: 0); break;
            case "22 tiles": (far, _, farc) = Player(AimMap, CX + 22, y, hp: 0); break;
            case "21 tiles": (far, _, farc) = Player(AimMap, CX + 21, y, hp: 0); break;
            case "dead creature": mob = AddCreature(AimMap, CX + 3, y, hp: 0); break;
            case "creature off map": mob = AddCreature(AimElsewhereMap, CX + 3, y); break;
        }
        var (caster, cOut, cc) = Caster("second_life", AimMap, CX, y);
        try
        {
            uint id = aim switch
            {
                "missing" => 0x7FFF_FFF0u,
                "own" => caster.PlayerId,
                _ => mob?.Id ?? far!.PlayerId,
            };
            caster.Receive(SpellCastSupport.CastFrame(0, id));
            long wait = SpellCastSupport.AetherLeft(caster, "second_life");
            bool ok = aim switch
            {
                "creature" => Lines(cOut) == "You can't do that." && cc.Mp == Mana - 6_000 && wait > 1_250_000 - SlackMs,
                "21 tiles" => Lines(cOut) == "You cast Second life." && cc.Mp == Mana - 6_000 && farc!.Hp == 600,
                "own" => Lines(cOut) == "You cast Second life." && cc.Mp == Mana - 6_000 && wait > 1_250_000 - SlackMs
                         && cc.Hp == Hp,
                _ => Lines(cOut) == "" && cc.Mp == Mana && wait == 0 && (farc is null || farc.Hp == 0),
            };
            Assert.True(ok && fc.Hp == 0 && faced.IsDead,
                        $"{aim}: lines [{Lines(cOut)}], spent {Mana - cc.Mp}, wait {wait} ms, aimed player {farc?.Hp}, faced ghost {fc.Hp}");
        }
        finally
        {
            Leave(AimMap, caster, faced);
            if (far is not null) Leave(farMap, far);
            if (mob is not null) _fx.World.DespawnMob(aim == "creature off map" ? AimElsewhereMap : AimMap, mob);
        }
    }

    /// <summary>Forgive takes an aimed id as RTK's engine does, never falling back to the Snake standing in front: a
    /// creature's id (a Dragon caster), nothing at all, as RTK's script stops on the creature's missing karma; an NPC's id
    /// (a caster below Dragon), nothing, not even the impure box, since RTK runs no script; a Snake's id 22 tiles off,
    /// nothing; a Snake's id 21 tiles off, RTK's menu, and on "Yes" that Snake is restored. The faced Snake keeps -5 each
    /// time. Red on 6dfaad3: the menu came up for the faced Snake and "Yes" restored them (the review's probe B), and the
    /// NPC's id showed the impure box.</summary>
    [Theory]
    [InlineData("creature")]
    [InlineData("npc")]
    [InlineData("22 tiles")]
    [InlineData("21 tiles")]
    public void ForgiveTakesAnAimedIdAsRtkDoes(string aim)
    {
        int y = 50 + 6 * Array.IndexOf(new[] { "creature", "npc", "22 tiles", "21 tiles" }, aim);
        var (faced, _, fc) = Player(AimMap, CX + 1, y, configure: ch => ch.Karma = -5, name: Letters("Faced"));
        Session? far = null;
        Character? farc = null;
        Mob? mob = null;
        switch (aim)
        {
            case "creature": mob = AddCreature(AimMap, CX + 3, y); break;
            case "npc": mob = AddCreature(AimMap, CX + 3, y, npc: true); break;
            default:
                (far, _, farc) = Player(AimMap, CX + (aim == "22 tiles" ? 22 : 21), y, configure: ch => ch.Karma = -5, name: Letters("Far"));
                break;
        }
        double karma = aim == "npc" ? 13.9 : 15;
        var (caster, cOut, cc) = Caster("forgive", AimMap, CX, y, configure: ch => ch.Karma = karma);
        try
        {
            List<string> menus = new();
            List<Box> boxes = new();
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0, mob?.Id ?? far!.PlayerId));
                menus = Menus(cOut);
                if (menus.Count > 0) caster.Receive(MenuFrame(1));
                boxes = Boxes(cOut);
            });
            bool ok = aim == "21 tiles"
                ? menus.Count == 1 && farc!.Karma == 0 && cc.Karma < 14
                  && boxes is [{ Kind: 0x0001 } b] && b.Text == $"{far!.CharName}'s karma has been restored to Cat."
                : menus.Count == 0 && boxes.Count == 0 && Lines(cOut) == "" && cc.Karma == karma && (farc is null || farc.Karma == -5);
            Assert.True(ok && fc.Karma == -5 && fc.Legends.All(l => l.Name != "been_forgiven"),
                        $"{aim}: menus {menus.Count}, boxes [{string.Join(" / ", boxes)}], lines [{Lines(cOut)}], caster karma " +
                        $"{cc.Karma}, aimed player {farc?.Karma}, faced Snake {fc.Karma}");
        }
        finally
        {
            Leave(AimMap, caster, faced);
            if (far is not null) Leave(AimMap, far);
            if (mob is not null) _fx.World.DespawnMob(AimMap, mob);
        }
    }

    /// <summary>With no id (the 4.95 client's slot-only cast), a ghost and a creature on the faced tile: the ghost is the
    /// target, decided once (a player before a creature), and comes back. Red with the faced tile read creature
    /// first.</summary>
    [Fact]
    public void UnaimedSecondLifeTakesTheFacedPlayerBeforeACreature()
    {
        int y = 80;
        var (ghost, _, gc) = Player(AimMap, CX + 1, y, hp: 0);
        var mob = AddCreature(AimMap, CX + 1, y);
        var (caster, cOut, cc) = Caster("second_life", AimMap, CX, y);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(cOut) == "You cast Second life." && gc.Hp == 600 && cc.Mp == Mana - 6_000,
                        $"lines [{Lines(cOut)}], ghost {gc.Hp}, spent {Mana - cc.Mp}");
        }
        finally { Leave(AimMap, caster, ghost); _fx.World.DespawnMob(AimMap, mob); }
    }

    /// <summary>With no id, a living player who came to the faced tile before a ghost lying there: Second life takes the
    /// ghost, which comes back with 600, and the living player is left as they are; it revives, so a ghost on the tile
    /// comes first (fix round 2, the re-check's R1; RTK has no faced-tile cast). Forgive keeps the first player there.
    /// Red with the faced tile read in the map's order alone (the living player took the cast).</summary>
    [Fact]
    public void UnaimedSecondLifeTakesAGhostBeforeALivingPlayerOnTheFacedTile()
    {
        int y = 98;
        var (living, _, lc) = Player(AimMap, CX + 1, y, hp: 5_000);
        var (ghost, _, gc) = Player(AimMap, CX + 1, y, hp: 0);
        var (caster, cOut, cc) = Caster("second_life", AimMap, CX, y);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(cOut) == "You cast Second life." && gc.Hp == 600 && !ghost.IsDead && lc.Hp == 5_000
                        && cc.Mp == Mana - 6_000,
                        $"lines [{Lines(cOut)}], ghost {gc.Hp}, living {lc.Hp}, spent {Mana - cc.Mp}");
        }
        finally { Leave(AimMap, caster, living, ghost); }
    }

    // ===== Forgive's player logging out at the pick (fix round 1: #421's review, F3) ===============================

    /// <summary>A player found by name at the pick but already logging out (their teardown has begun) is restored
    /// nothing, and the caster pays nothing and reads nothing: the price is taken once the restore has landed. Red with
    /// the price taken first, as RTK's script orders it (the caster lost 1 to 2 karma for nothing).</summary>
    [Fact]
    public void ForgiveCostsNothingWhenThePlayerIsLeavingAtThePick()
    {
        var (target, _, tc) = Player(LeavingMap, CX + 1, CY, configure: ch => ch.Karma = -5, name: Letters("Leaving"));
        var (caster, cOut, cc) = Caster("forgive", LeavingMap, CX, CY, configure: ch => ch.Karma = 15);
        try
        {
            List<Box> boxes = new();
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                target.WithState(() => LeavingField.SetValue(target, true));
                cOut.Clear();
                caster.Receive(MenuFrame(1));
                boxes = Boxes(cOut);
            });
            Assert.True(cc.Karma == 15 && tc.Karma == -5 && Lines(cOut) == "" && boxes.Count == 0
                        && tc.Legends.All(l => l.Name != "been_forgiven"),
                        $"caster karma {cc.Karma}, player karma {tc.Karma}, lines [{Lines(cOut)}], boxes [{string.Join(" / ", boxes)}]");
        }
        finally
        {
            target.WithState(() => LeavingField.SetValue(target, false));
            Leave(LeavingMap, caster, target);
        }
    }

    // ===== the teacher ==========================================================================================

    /// <summary>The Monk teacher sells eight, each at its script's level: Read karma and Forgive from 60, Awe from 62, Second
    /// life (no requirements()) from its row's 0, the other four from 99. Bless drink, with no script of its own, has no
    /// price and is not on sale. Red without the price rows: an empty list.</summary>
    [Fact]
    public void TheMonkTeacherSellsEight()
    {
        int path = Content.PathIdForClass("Monk");
        var sold = Content.SubpathTeacherSpells(path, 99, Content.MaxMark);
        Assert.Equal("awe:62 bless_food:99 faiths_call:99 forgive:60 mass_resurrect:99 read_karma_monk:60 second_life:0 son_kal:99",
                     string.Join(" ", sold.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}:{s.Level}")));
        Assert.All(sold, s => Assert.Equal(path, s.PathId));
        Assert.Null(Content.LearnCostFor(Content.SpellByKey("bless_drink")!, path));
        Assert.Equal("second_life", string.Join(" ", Content.SubpathTeacherSpells(path, 59, Content.MaxMark).Select(s => s.Key)));
    }

    // ===== helpers ==============================================================================================

    /// <summary>One 0x30 dialog box (<c>Session.SendScriptMessageP</c>, <c>SendInputBox</c>): its kind (0x0001 a text box,
    /// 0x0404 an input box) and text, as <c>DivinerSpellTests</c> reads them.</summary>
    private sealed record Box(int Kind, string Text)
    {
        public override string ToString() => $"{Kind:x4} \"{Text}\"";
    }

    private static List<Box> Boxes(RecordingOutbound o) =>
        o.BodiesOf(ServerOp.NpcDialog).Where(b => b.Length >= 23 && b[7] == 1 && !(b[0] == 2 && b[1] == 2)).Select(b =>
        {
            int len = (b[21] << 8) | b[22];
            return new Box((b[0] << 8) | b[1], Encoding.ASCII.GetString(b, 23, Math.Min(len, b.Length - 23)));
        }).ToList();

    /// <summary>An input box's answer (0x3A kind 4, a submit).</summary>
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

    /// <summary>A menu pick (0x3A kind 2, the option's 1-based index at [10]).</summary>
    private static byte[] MenuFrame(byte index)
    {
        var body = new byte[11];
        body[0] = 0x02;
        body[10] = index;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

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

    /// <summary>The 0x0F cast frame for a question spell: the slot byte, then the typed answer, NUL-terminated.</summary>
    private static byte[] AnswerFrame(int slot, string answer)
    {
        var body = new List<byte> { (byte)(slot + 1) };
        body.AddRange(Encoding.ASCII.GetBytes(answer));
        body.Add(0);
        return SessionFixture.Frame(ClientOp.Cast, body.ToArray());
    }

    private static string Lines(RecordingOutbound o) => string.Join(" | ", SpellCastSupport.MiniTexts(o));

    private static List<string> Speech(RecordingOutbound o) =>
        o.BodiesOf(ServerOp.Speech).Select(b => Encoding.ASCII.GetString(b)).ToList();

    private static uint U32(byte[] b, int at) => (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]);

    /// <summary>The spell graphics (0x29) sent over <paramref name="id"/>, as effect ids.</summary>
    private static List<int> Effects(RecordingOutbound outbound, uint id) =>
        outbound.BodiesOf(ServerOp.Effect).Where(b => U32(b, 0) == id).Select(b => b[4] - ServerConfig.Current.EfxWireOffset).ToList();

    /// <summary>The sounds (0x19) sent.</summary>
    private static List<int> Sounds(RecordingOutbound outbound) =>
        outbound.BodiesOf(ServerOp.Audio).Where(b => b.Length >= 4).Select(b => (b[2] << 8) | b[3]).ToList();

    private string Show(Mob m, long now) =>
        $"[{_fx.World.MobStatusKey(m, "paras")} {Math.Max(0, m.FrozenUntil - now)} ms]";

    private static void FreeSlot(Session s) => s.WithState(() => { NextActionTick.SetValue(s, 0L); ActionCount.SetValue(s, 0); });

    /// <summary>A map's size as Maps.csv has it, or a wide 100 x 100 for a content-free map.</summary>
    private static void Size(Character c, ushort map)
    {
        if (Content.Maps.TryGetValue(map, out var m)) { c.MapXs = m.Xs; c.MapYs = m.Ys; }
        else { c.MapXs = 100; c.MapYs = 100; }
    }

    /// <summary>Run <paramref name="body"/> on a thread of its own with no synchronisation context, as a read loop runs a
    /// packet: an await inside it (a menu's or a box's) then resumes inline on the thread that answers it.</summary>
    private static void OnOwnThread(Action body)
    {
        Exception? fault = null;
        var t = new Thread(() => fault = Record.Exception(body)) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(30)), "the packet thread never finished");
        Assert.Null(fault);
    }

    /// <summary>A letters-only name, unique in this process: Read karma's and Forgive's lookups take a name, and the
    /// fixture's other players carry digits.</summary>
    private static string Letters(string stem)
    {
        int n = Interlocked.Increment(ref _serial);
        var sb = new StringBuilder(stem);
        do { sb.Append((char)('a' + n % 26)); n /= 26; } while (n > 0);
        return sb.ToString();
    }

    private Mob AddCreature(ushort map, int x, int y, int hp = 100_000, int maxHp = 100_000, bool npc = false)
    {
        var mob = new Mob(_fx.World.AllocateMobId(), 1, (ushort)x, (ushort)y, $"Monk target {Interlocked.Increment(ref _serial)}", maxHp)
        {
            IsNpc = npc,
        };
        mob.Hp = hp;
        _fx.World.AddMob(map, mob);
        return mob;
    }

    /// <summary>A level-99 Monk at (<paramref name="x"/>, <paramref name="y"/>) facing east, with <paramref name="key"/> in
    /// slot 0 of the book, Will 41, on the map's own size (a wide one for a content-free map).</summary>
    private (Session session, RecordingOutbound outbound, Character c) Caster(string key, ushort map, int x, int y,
                                                                            uint mana = Mana, uint maxMana = MaxMana,
                                                                            Action<Character>? configure = null, string? name = null) =>
        _fx.PlayerWith(name ?? Letters("Monk"), ch =>
        {
            ch.ClassName = "Monk";
            ch.Level = 99;
            ch.Ac = 0;
            ch.Will = Will;
            ch.MaxHp = Hp; ch.Hp = Hp;
            ch.MaxMp = maxMana; ch.Mp = mana;
            ch.Dir = 1;
            Size(ch, map);
            ch.Spells.Add(Content.SpellByKey(key)!.Id);
            configure?.Invoke(ch);
        }, map, (ushort)x, (ushort)y);

    private (Session session, RecordingOutbound outbound, Character c) Player(ushort map, int x, int y, uint hp = Hp,
                                                                            uint maxHp = Hp, uint mana = 0, uint maxMana = 1_000,
                                                                            Action<Character>? configure = null, string? name = null) =>
        _fx.PlayerWith(name ?? Letters("Other"), ch =>
        {
            ch.Level = 50;
            ch.Ac = 0;
            ch.MaxHp = maxHp; ch.Hp = hp;
            ch.MaxMp = maxMana; ch.Mp = mana;
            Size(ch, map);
            configure?.Invoke(ch);
        }, map, (ushort)x, (ushort)y);

    private void Leave(ushort map, params Session[] sessions)
    {
        foreach (var s in sessions) _fx.World.LeaveMap(s, map);
    }

    // ---- the held-monitor run (ChongunSpellTests' shape) -------------------------------------------------------------

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
