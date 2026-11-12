using System.Diagnostics;
using System.Reflection;
using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The Merchant spells slice (the project owner, 2026-10-26, P5 "each spell as its RTK script", P1 and P3 "on sale once it
/// works, at its script's price"; 2026-11-12, Engrave and Gambling: "Offers as written", "As the script means", "Find
/// again by name"; the smaller calls as RTK under P5). Before, all seven Merchant rows fell to the <c>generic</c> verb, 5
/// mana and "You cast X." and nothing else (<c>Tests/Fixtures/spell-dispatch-routes.txt</c> had them on
/// <c>verb=generic</c>).
///
/// <para>The scripts: <c>RTK-Server/rtklua/Accepted/Spells/Subpaths/Merchant/</c>. Each spell is bound by its
/// <c>game-data/SpellParams.csv</c> row: Skill up to the climbing fury on Spy's ladder, Golden fist to Chongun's
/// <c>lesser_fury</c>, the others to the Merchant region of <c>game-data/spell_verbs.lua</c>, whose engine half is
/// Session.Spells.cs's "the Merchant spells".</para>
///
/// <para>Every cast is the real 0x0F frame through <c>Session.Receive</c>, by a fresh level-99 Merchant facing east, on
/// content-free maps 63700-63728 (no Maps.csv row: no terrain, PvP off). A menu, an input box, a box or the item-list
/// window is answered from a thread of its own with no synchronisation context, as a read loop answers it, so the next
/// step resumes inline as on the server, on the session of the player who answered.</para>
/// </summary>
[Collection("world")]
public sealed class MerchantSpellTests
{
    private const ushort RageMap = 63700, FistMap = 63701, GuiseMap = 63702, PilferMap = 63703, PilferRefuseMap = 63704,
                         PilferFullMap = 63705, BekyunMap = 63706, BekyunRefuseMap = 63707, EngraveMap = 63708,
                         EngraveRefuseMap = 63709, EngraveFeeMap = 63710, EngraveOfferMap = 63711, EngraveNameMap = 63712,
                         EngraveConfirmMap = 63713, EngraveRecheckMap = 63714, EngraveSelfMap = 63715,
                         EngraveClosedMap = 63716, EngraveHeldMap = 63717, EngraveBoxHeldMap = 63718, GambleMap = 63719,
                         GambleRefuseMap = 63720, GambleAnswerMap = 63721, GambleRecheckMap = 63722, GambleSelfMap = 63723,
                         GambleClosedMap = 63724, GambleHeldMap = 63725, GambleSettleMap = 63726, PilferStackMap = 63727,
                         EngraveDropMap = 63728;
    private const ushort CX = 30, CY = 30;
    private const uint MaxMana = 400_000, Mana = 400_000, Hp = 10_000;

    /// <summary>Slack on a wait, a run or a status read straight after the cast.</summary>
    private const long SlackMs = 5_000;

    private static readonly FieldInfo NextActionTick =
        typeof(Session).GetField("_nextActionTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ActionCount =
        typeof(Session).GetField("_actionCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo RageUntilField =
        typeof(Session).GetField("_rageUntil", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ShopReplyField =
        typeof(Session).GetField("_shopReply", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo LeavingField =
        typeof(Session).GetField("_leaving", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo MorphLookField =
        typeof(Session).GetField("_morphLook", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo MorphColorField =
        typeof(Session).GetField("_morphColor", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly SessionFixture _fx;
    private static int _serial;

    public MerchantSpellTests(SessionFixture fx) => _fx = fx;

    // ===== Skill up =============================================================================================

    /// <summary>Merchant's Skill up is Spy's script under its own key: six casts climb rage 6, 7, 9, 10, 12, 14 for 3,000,
    /// 4,711, 17,960, 53,646, 135,327 and 269,420 mana, each the script's line after "[Skill up N] ", the 150 s wait under
    /// Merchant's own key each time; a seventh says "You have reached your max potential." and spends nothing; the run
    /// lapsing at tier 6 keeps all its vita (the uncast only sets rage 1). Red without the row: generic's 5 mana and "You
    /// cast Skill up.".</summary>
    [Fact]
    public void SkillUpClimbsItsSixTiersAndHoldsAtTheTop()
    {
        var (caster, cOut, cc) = Merchant("skill_up_merchant", RageMap, CX, CY, mana: 600_000, maxMana: 600_000);
        int[] mults = { 6, 7, 9, 10, 12, 14 }, mana = { 3_000, 4_711, 17_960, 53_646, 135_327, 269_420 };
        string[] said =
        {
            "[Skill up 1] Your senses sharpen.", "[Skill up 2] You hone your skills.",
            "[Skill up 3] Agility becomes your strongest suit..", "[Skill up 4] Acquiring cat-like reflex.",
            "[Skill up 5] Incredible skill!", "[Skill up 6] Sagat would be jealous.",
        };
        try
        {
            for (int tier = 1; tier <= 6; tier++)
            {
                uint before = cc.Mp;
                cOut.Clear();
                FreeSlot(caster);
                caster.Receive(SpellCastSupport.CastFrame(0));
                var lines = SpellCastSupport.MiniTexts(cOut);
                var (amount, left) = SpellCastSupport.Rage(caster);
                long wait = SpellCastSupport.AetherLeft(caster, "skill_up_merchant");
                Assert.True(lines.SequenceEqual(new[] { said[tier - 1] }) && amount == mults[tier - 1]
                            && before - cc.Mp == mana[tier - 1] && wait > 150_000 - SlackMs && wait <= 150_000
                            && left > 938_000 - SlackMs * 2,
                            $"tier {tier}: [{string.Join(" | ", lines)}], rage {amount}, spent {before - cc.Mp}, wait {wait} ms, run {left} ms");
                SpellCastSupport.ClearAether(caster, "skill_up_merchant");
            }
            uint top = cc.Mp;
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(cOut) == "You have reached your max potential." && cc.Mp == top && SpellCastSupport.Rage(caster).Amount == 14,
                        $"at the top: [{Lines(cOut)}], spent {top - cc.Mp}");
            cOut.Clear();
            caster.WithState(() => RageUntilField.SetValue(caster, Environment.TickCount64 - 1));
            caster.RegenTick(0, regenDue: false);
            Assert.True(cc.Hp == Hp && caster.LuaCrRageTier == 0, $"lapsed at tier 6: vita {cc.Hp}, tier left {caster.LuaCrRageTier}");
        }
        finally { Leave(RageMap, caster); }
    }

    // ===== Golden fist ==========================================================================================

    /// <summary>Golden fist is Protectors fury's script with Merchant's numbers: for 90 mana, rage x4 for 625 s, "You cast
    /// Golden fist.", graphic 11 and sound 4. Again while it runs, "This spell is already active.", free; short of 90, "You
    /// do not have enough mana.", free. Red without the row (generic).</summary>
    [Fact]
    public void GoldenFistIsAFuryOfFourFor625Seconds()
    {
        var (caster, cOut, cc) = Merchant("golden_fist", FistMap, CX, CY);
        var (poor, pOut, pc) = Merchant("golden_fist", FistMap, CX, CY + 4, mana: 89);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            var (amount, left) = SpellCastSupport.Rage(caster);
            string first = Lines(cOut);
            var fx = Effects(cOut, caster.PlayerId);
            var snd = Sounds(cOut);
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            poor.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(first == "You cast Golden fist." && amount == 4 && left > 625_000 - SlackMs && left <= 625_000
                        && cc.Mp == Mana - 90 && fx.Contains(11) && snd.Contains(4)
                        && Lines(cOut) == "This spell is already active." && cc.Mp == Mana - 90
                        && Lines(pOut) == "You do not have enough mana." && pc.Mp == 89 && SpellCastSupport.Rage(poor).Amount == 1,
                        $"first [{first}] rage {amount} for {left} ms, spent {Mana - cc.Mp}, graphics [{string.Join(",", fx)}], " +
                        $"sounds [{string.Join(",", snd)}]; again [{Lines(cOut)}]; poor [{Lines(pOut)}] mana {pc.Mp}");
        }
        finally { Leave(FistMap, caster, poor); }
    }

    // ===== Marketer guise =======================================================================================

    /// <summary>Marketer guise takes look 52 for 60 s in the caster's war-paint colour, with graphic 3 and sound 708 and no
    /// line, and spends nothing (the script checks its 125 and never takes it). Cast again in the form, the form ends, free
    /// and silent, with the script's uncast sound 410. Short of 125, "Your will is too weak.", no form. Red without the row
    /// (generic); red without the guise's end sound in <c>Session.RevertMorph</c> (411 plays instead).</summary>
    [Fact]
    public void MarketerGuiseWearsLook52InWarPaintAndSpendsNothing()
    {
        var (caster, cOut, cc) = Merchant("marketer_guise", GuiseMap, CX, CY, configure: ch => ch.ArmorColor = 7);
        var (poor, pOut, pc) = Merchant("marketer_guise", GuiseMap, CX, CY + 4, mana: 124);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            var (look, color) = Morph(caster);
            string lines = Lines(cOut);
            var fx = Effects(cOut, caster.PlayerId);
            var snd = Sounds(cOut);
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            bool endedMorph = !caster.IsMorphed;
            var endSnd = Sounds(cOut);
            poor.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(look == 52 && color == 7 && cc.Mp == Mana && lines == "" && fx.Contains(3) && snd.Contains(708)
                        && endedMorph && endSnd.SequenceEqual(new[] { 410 }) && Lines(cOut) == "" && cc.Mp == Mana
                        && Lines(pOut) == "Your will is too weak." && !poor.IsMorphed && pc.Mp == 124,
                        $"look {look}/{color}, spent {Mana - cc.Mp}, lines [{lines}], graphics [{string.Join(",", fx)}], sounds " +
                        $"[{string.Join(",", snd)}]; again: ended {endedMorph}, sounds [{string.Join(",", endSnd)}], lines [{Lines(cOut)}]; " +
                        $"poor [{Lines(pOut)}]");
        }
        finally { Leave(GuiseMap, caster, poor); }
    }

    // ===== Pilfer ===============================================================================================

    /// <summary>For 60 mana, sound 4 and the shout "I'll take that", every item lying on the two tiles ahead comes to the
    /// caster: an item into the bag with its durability, engraved name and owner, coins into the purse; no line. Red without
    /// the row (generic).</summary>
    [Fact]
    public void PilferTakesWhatLiesTwoTilesAhead()
    {
        var (caster, cOut, cc) = Merchant("pilfer", PilferMap, CX, CY);
        var sword = Content.ItemByKey("steel_sword")!;
        var acorn = Content.ItemByKey("acorn")!;
        try
        {
            Lay(PilferMap, CX + 1, CY, sword.Id, 1, dura: 77, name: "** Mine **", owner: "Someone");
            Lay(PilferMap, CX + 2, CY, -1, 345);                        // coins
            Lay(PilferMap, CX + 2, CY, acorn.Id, 12);
            Lay(PilferMap, CX + 3, CY, acorn.Id, 5);                    // three tiles: out of reach
            uint coins = cc.Coins;
            caster.Receive(SpellCastSupport.CastFrame(0));
            var got = cc.Inventory.Select(i => (Content.ItemById(i.ItemId)!.Key, i.Amount, i.Dura, i.CustomName, i.Owner)).ToList();
            var left = _fx.World.ItemsOn(PilferMap).Select(g => (g.X, g.ItemId, g.Amount)).ToList();
            Assert.True(cc.Mp == Mana - 60 && Lines(cOut) == "" && Sounds(cOut).Contains(4)
                        && Speech(cOut).Any(s => s.Contains("I'll take that")) && cc.Coins == coins + 345
                        && got.Contains(("steel_sword", 1, (ushort)77, "** Mine **", "Someone")) && got.Any(g => g.Key == "acorn" && g.Amount == 12)
                        && left.SequenceEqual(new[] { ((ushort)(CX + 3), acorn.Id, 5) }),
                        $"spent {Mana - cc.Mp}, lines [{Lines(cOut)}], coins +{cc.Coins - coins}, bag [{string.Join(" / ", got)}], " +
                        $"floor [{string.Join(" / ", left)}]");
        }
        finally { Leave(PilferMap, caster); ClearFloor(PilferMap); }
    }

    /// <summary>A tile a player stands on is passed over, silently, its item left; a death pile still locked to someone
    /// else is refused, "That item does not belong to you." then "But that would be stealing.", and left; short of 60,
    /// "Your will is too weak.", free. Red without the player test or the lock test in <c>Session.LuaPilfer</c>.</summary>
    [Fact]
    public void PilferPassesOverAPlayersTileAndRefusesALockedPile()
    {
        var (caster, cOut, cc) = Merchant("pilfer", PilferRefuseMap, CX, CY);
        var (stand, _, _) = Player(PilferRefuseMap, CX + 1, CY);
        var (poor, pOut, pc) = Merchant("pilfer", PilferRefuseMap, CX, CY + 6, mana: 59);
        var acorn = Content.ItemByKey("acorn")!;
        try
        {
            Lay(PilferRefuseMap, CX + 1, CY, acorn.Id, 3);
            Lay(PilferRefuseMap, CX + 2, CY, acorn.Id, 4, looter: 999_999);
            caster.Receive(SpellCastSupport.CastFrame(0));
            poor.Receive(SpellCastSupport.CastFrame(0));
            int floor = _fx.World.ItemsOn(PilferRefuseMap).Count(g => g.Y == CY);
            Assert.True(cc.Inventory.Count == 0 && floor == 2 && cc.Mp == Mana - 60
                        && Lines(cOut) == "That item does not belong to you. | But that would be stealing."
                        && Lines(pOut) == "Your will is too weak." && pc.Mp == 59,
                        $"bag {cc.Inventory.Count}, floor {floor}, lines [{Lines(cOut)}]; poor [{Lines(pOut)}]");
        }
        finally { Leave(PilferRefuseMap, caster, stand, poor); ClearFloor(PilferRefuseMap); }
    }

    /// <summary>Another player stacked on the caster's own tile: each item ahead is refused with "You cannot pick up items
    /// while stacked." then "But that would be stealing.", and left. Red without the stacked test.</summary>
    [Fact]
    public void PilferWhileStackedTakesNothing()
    {
        var (caster, cOut, cc) = Merchant("pilfer", PilferStackMap, CX, CY);
        var (stacked, _, _) = Player(PilferStackMap, CX, CY);
        var acorn = Content.ItemByKey("acorn")!;
        try
        {
            Lay(PilferStackMap, CX + 1, CY, acorn.Id, 3);
            Lay(PilferStackMap, CX + 2, CY, acorn.Id, 4);
            caster.Receive(SpellCastSupport.CastFrame(0));
            const string line = "You cannot pick up items while stacked. | But that would be stealing.";
            Assert.True(cc.Inventory.Count == 0 && _fx.World.ItemsOn(PilferStackMap).Length == 2 && Lines(cOut) == line + " | " + line,
                        $"bag {cc.Inventory.Count}, lines [{Lines(cOut)}]");
        }
        finally { Leave(PilferStackMap, caster, stacked); ClearFloor(PilferStackMap); }
    }

    /// <summary>With a full bag a pilfered item lies at the caster's feet with its durability and name, and the bag's line
    /// "You can't have more." (RTK addItem's pc_dropitemfull); the tile ahead is empty. Red without the floor drop.</summary>
    [Fact]
    public void APilferedItemThatDoesNotFitLiesAtTheCastersFeet()
    {
        var sword = Content.ItemByKey("steel_sword")!;
        var (caster, cOut, cc) = Merchant("pilfer", PilferFullMap, CX, CY, configure: ch =>
        {
            for (byte s = 0; s < ch.MaxInv; s++) ch.Inventory.Add(new InvItem(s, sword.Id, 1, sword.Durability));
        });
        try
        {
            Lay(PilferFullMap, CX + 1, CY, sword.Id, 1, dura: 55, name: "** Old **");
            caster.Receive(SpellCastSupport.CastFrame(0));
            var floor = _fx.World.ItemsOn(PilferFullMap).Select(g => (g.X, g.Y, g.ItemId, g.Dura, g.CustomName)).ToList();
            Assert.True(floor.SequenceEqual(new[] { (CX, CY, sword.Id, (ushort)55, "** Old **") }) && Lines(cOut) == "You can't have more.",
                        $"floor [{string.Join(" / ", floor)}], lines [{Lines(cOut)}]");
        }
        finally { Leave(PilferFullMap, caster); ClearFloor(PilferFullMap); }
    }

    // ===== BekyunArrow ==========================================================================================

    /// <summary>With Bekyun's spear: "You cast BekyunArrow.", the 10,000 s wait, 2.5% of the vita and mana back (floored),
    /// the spear gone with reason 9 ("You gave Bekyun's spear.") and five Bekyun's set given. No mana. Red without the row
    /// (generic); red with <c>Session.LuaTakeItem</c>'s reason dropped (0 goes out).</summary>
    [Fact]
    public void BekyunArrowTradesTheSpearForFiveSets()
    {
        var spear = Content.ItemByKey("bekyuns_spear")!;
        var (caster, cOut, cc) = Merchant("bekyunarrow", BekyunMap, CX, CY, mana: 100_000, configure: ch =>
        {
            ch.Hp = 8_000;
            ch.Inventory.Add(new InvItem(3, spear.Id, 1, spear.Durability));
        });
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            long wait = SpellCastSupport.AetherLeft(caster, "bekyunarrow");
            var del = cOut.BodiesOf(ServerOp.DeleteItem).Select(b => (b[0], b[1])).ToList();
            var bag = cc.Inventory.Select(i => (Content.ItemById(i.ItemId)!.Key, i.Amount)).ToList();
            Assert.True(Lines(cOut) == "You cast BekyunArrow." && wait > 10_000_000 - SlackMs && wait <= 10_000_000
                        && cc.Hp == 8_200 && cc.Mp == 102_500 && del.SequenceEqual(new[] { ((byte)4, (byte)9) })
                        && bag.SequenceEqual(new[] { ("bekyuns_set", 5) }),
                        $"lines [{Lines(cOut)}], wait {wait}, vita {cc.Hp}, mana {cc.Mp}, deletes [{string.Join(",", del)}], bag [{string.Join(",", bag)}]");
        }
        finally { Leave(BekyunMap, caster); }
    }

    /// <summary>Without the spear: the line, the wait and the heal (vita at its max stays there), then a box with the
    /// spear's icon, "You need his spear to craft arrows from."; with no room for five sets, "Your inventory is full." and
    /// the spear kept; inside the wait, "BekyunArrow isn't ready yet.". Red without the room check.</summary>
    [Fact]
    public void BekyunArrowRefusals()
    {
        var spear = Content.ItemByKey("bekyuns_spear")!;
        var sword = Content.ItemByKey("steel_sword")!;
        var (bare, bOut, bc) = Merchant("bekyunarrow", BekyunRefuseMap, CX, CY, mana: 100_000);
        var (full, fOut, fc) = Merchant("bekyunarrow", BekyunRefuseMap, CX, CY + 4, configure: ch =>
        {
            ch.Inventory.Add(new InvItem(0, spear.Id, 1, spear.Durability));
            for (byte s = 1; s < ch.MaxInv; s++) ch.Inventory.Add(new InvItem(s, sword.Id, 1, sword.Durability));
        });
        try
        {
            bare.Receive(SpellCastSupport.CastFrame(0));
            var box = Boxes(bOut);
            long wait = SpellCastSupport.AetherLeft(bare, "bekyunarrow");
            full.Receive(SpellCastSupport.CastFrame(0));
            bOut.Clear();
            FreeSlot(bare);
            bare.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(box is [{ Kind: 0x0001, Head: 2, Next: 0 } b] && b.Text == "You need his spear to craft arrows from."
                        && b.Gfx == spear.ClientIcon && wait > 10_000_000 - SlackMs && bc.Hp == Hp && bc.Mp == 102_500
                        && Lines(fOut) == "You cast BekyunArrow. | Your inventory is full." && fc.Inventory.Any(i => i.ItemId == spear.Id)
                        && Lines(bOut) == "BekyunArrow isn't ready yet.",
                        $"bare box [{string.Join(" / ", box)}], wait {wait}, vita {bc.Hp}, mana {bc.Mp}; full [{Lines(fOut)}]; again [{Lines(bOut)}]");
        }
        finally { Leave(BekyunRefuseMap, bare, full); }
    }

    // ===== Engrave ==============================================================================================

    /// <summary>The whole flow. The engraver aims at the customer: the fee box (icon 71), "12345"; the customer's waiting
    /// conversation is dropped and they read "&lt;engraver&gt; is offering you their engraving services for 12,345 gold. Are
    /// you willing to pay this amount?"; "Yes" opens their item list of types 3-17 (the two swords, the second told apart
    /// as "Steel sword (2)", not the acorns); the second picked asks the name over the sword's icon; "Bob's blade" asks the engraver the naming policy, then "Are you sure you want
    /// to engrave the item?"; "Yes" moves 12,345 gold ("12345 gold removed." and "12345 gold added."), names that sword
    /// "** Bob's blade **" (the other untouched), sets the engraver's engrave_timer 24 h on, and both read "The item has been engraved.". No mana.
    /// Red without the row (generic).</summary>
    [Fact]
    public void EngraveRenamesThePickedItemAndMovesTheFee()
    {
        var sword = Content.ItemByKey("steel_sword")!;
        var acorn = Content.ItemByKey("acorn")!;
        var (customer, uOut, uc) = Player(EngraveMap, CX + 3, CY, coins: 20_000, configure: ch =>
        {
            ch.Inventory.Add(new InvItem(0, acorn.Id, 5, 0));
            ch.Inventory.Add(new InvItem(2, sword.Id, 1, sword.Durability));
            ch.Inventory.Add(new InvItem(4, sword.Id, 1, sword.Durability));
        });
        var (engraver, eOut, ec) = Merchant("engrave", EngraveMap, CX, CY);
        try
        {
            uint ecoins = ec.Coins;
            List<Box> fee = new(), named = new();
            string offer = "", policy = "", sure = "";
            Grid? list = null;
            OnOwnThread(() =>
            {
                _ = customer.DlgInputPush(0, 0, "a conversation the customer has waiting");
                engraver.Receive(SpellCastSupport.CastFrame(0, customer.PlayerId));
                fee = Boxes(eOut);
                uOut.Clear();
                engraver.Receive(InputFrame("12345"));
            });
            offer = string.Join(" / ", Menus(uOut));
            OnOwnThread(() =>
            {
                uOut.Clear();
                customer.Receive(MenuFrame(1));
                list = Grids(uOut).SingleOrDefault();
                uOut.Clear();
                customer.Receive(BuyPick("Steel sword (2)"));
                named = Boxes(uOut);
                eOut.Clear();
                customer.Receive(InputFrame("Bob's blade"));
            });
            policy = string.Join(" / ", Menus(eOut));
            List<Box> eDone = new(), uDone = new();
            OnOwnThread(() =>
            {
                eOut.Clear();
                engraver.Receive(MenuFrame(1));
                sure = string.Join(" / ", Menus(eOut));
                eOut.Clear();
                uOut.Clear();
                engraver.Receive(MenuFrame(1));
                eDone = Boxes(eOut);
                uDone = Boxes(uOut);
            });
            int timer = QuestState.Over(ec, QuestState.Registry).Get("engrave_timer");
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var swordNow = uc.Inventory.Single(i => i.Slot == 4);
            var otherSword = uc.Inventory.Single(i => i.Slot == 2);
            Assert.True(fee is [{ Kind: 0x0404, Head: 2, Gfx: 71 } f] && f.Text == "How much gold will you be asking for offering your services?"
                        && offer == $"{engraver.CharName} is offering you their engraving services for 12,345 gold. Are you willing to pay this amount?|Yes|No"
                        && list is { Head: 2, Gfx: 71 } g && g.Prompt == "What item would you like to engrave?"
                        && g.Rows.SequenceEqual(new[] { ("Steel sword", 1), ("Steel sword (2)", 1) })
                        && named is [{ Kind: 0x0404, Head: 2 } n] && n.Gfx == sword.Icon
                        && n.Text == "Please type in some text that you would like the item to be known as.\n\n(You do not need to enter the asterisks (*))"
                        && policy == $"{customer.CharName} has accepted your offer.\n\nProposed name: ** Bob's blade **\n\nDoes this name meet the Acceptable naming policy set forth in the Terms of Service?|Yes|No"
                        && sure == "Are you sure you want to engrave the item?|Yes|No"
                        && swordNow.CustomName == "** Bob's blade **" && otherSword.CustomName == "" && uc.Coins == 20_000 - 12_345 && ec.Coins == ecoins + 12_345
                        && Lines(uOut).Contains("12345 gold removed.") && Lines(eOut).Contains("12345 gold added.")
                        && timer > now + 86_400 - 60 && timer <= now + 86_400
                        && eDone is [{ Kind: 0x0001, Head: 0, Next: 0 } e] && e.Text == "The item has been engraved."
                        && uDone is [{ Kind: 0x0001, Head: 0, Next: 0 } u] && u.Text == "The item has been engraved."
                        && ec.Mp == Mana,
                        $"fee [{string.Join(" / ", fee)}]; offer [{offer}]; list {list}; named [{string.Join(" / ", named)}]; " +
                        $"policy [{policy}]; sure [{sure}]; sword '{swordNow.CustomName}', customer {uc.Coins}, engraver +{ec.Coins - ecoins}, " +
                        $"lines c [{Lines(uOut)}] e [{Lines(eOut)}]; timer {timer - now}; done e [{string.Join(" / ", eDone)}] c [{string.Join(" / ", uDone)}]");
        }
        finally { Leave(EngraveMap, customer, engraver); }
    }

    /// <summary>A new offer drops the conversation the customer had waiting, the item list of an earlier offer included
    /// (RTK's target:freeAsync()): a pick from that old list afterwards names nothing. Red without
    /// <c>Session.DropWaitingDialog</c> in <c>Session.LuaPromptTarget</c> (the old list's pick asks the name).</summary>
    [Fact]
    public void ANewOfferDropsTheListTheCustomerHadOpen()
    {
        var sword = Content.ItemByKey("steel_sword")!;
        var (customer, uOut, _) = Player(EngraveDropMap, CX + 1, CY, coins: 50_000, configure: ch =>
            ch.Inventory.Add(new InvItem(0, sword.Id, 1, sword.Durability)));
        var (first, _, _) = Merchant("engrave", EngraveDropMap, CX, CY);
        var (second, _, _) = Merchant("engrave", EngraveDropMap, CX + 2, CY);
        try
        {
            OnOwnThread(() =>
            {
                first.Receive(SpellCastSupport.CastFrame(0, customer.PlayerId));
                first.Receive(InputFrame("100"));
            });
            OnOwnThread(() => customer.Receive(MenuFrame(1)));          // the first offer's list is open
            int lists = Grids(uOut).Count;
            OnOwnThread(() =>
            {
                second.Receive(SpellCastSupport.CastFrame(0, customer.PlayerId));
                second.Receive(InputFrame("200"));
            });
            uOut.Clear();
            OnOwnThread(() => customer.Receive(BuyPick("Steel sword")));
            Assert.True(lists == 1 && Boxes(uOut).Count == 0, $"lists {lists}, after the stale pick [{string.Join(" / ", Boxes(uOut))}]");
        }
        finally { Leave(EngraveDropMap, customer, first, second); }
    }

    /// <summary>Inside the engraver's saved engrave_timer, the box "You have engraved too recently." (icon 71) and nothing
    /// more; with nothing faced and no id, nothing at all. Red without the timer test.</summary>
    [Fact]
    public void EngraveRefusesInsideItsTimerAndAtNothing()
    {
        var (customer, uOut, _) = Player(EngraveRefuseMap, CX + 1, CY);
        var (timed, tOut, _) = Merchant("engrave", EngraveRefuseMap, CX, CY, configure: ch =>
            QuestState.Over(ch, QuestState.Registry).Set("engrave_timer", (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3_600)));
        var (alone, aOut, _) = Merchant("engrave", EngraveRefuseMap, CX, CY + 6);
        try
        {
            timed.Receive(SpellCastSupport.CastFrame(0));
            alone.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Boxes(tOut) is [{ Kind: 0x0001, Head: 2, Gfx: 71, Next: 0 } b] && b.Text == "You have engraved too recently."
                        && Boxes(uOut).Count == 0 && Menus(uOut).Count == 0
                        && aOut.BodiesOf(ServerOp.NpcDialog).Count == 0 && Lines(aOut) == "",
                        $"timed [{string.Join(" / ", Boxes(tOut))}]; alone dialogs {aOut.BodiesOf(ServerOp.NpcDialog).Count}, lines [{Lines(aOut)}]");
        }
        finally { Leave(EngraveRefuseMap, customer, timed, alone); }
    }

    /// <summary>RTK's inputNumberCheck on the fee, each ending it: "12a" the box "You can only enter numbers, no letters.";
    /// "0" the box "You must enter a positive number."; "" nothing. Aimed at a creature, the fee is asked and then nothing
    /// (RTK's target:freeAsync() raises on one). Red without the digit check.</summary>
    [Fact]
    public void EngraveFeeChecks()
    {
        var (customer, uOut, _) = Player(EngraveFeeMap, CX + 1, CY);
        var mob = AddCreature(EngraveFeeMap, CX + 1, CY + 8);
        var casters = Enumerable.Range(0, 4).Select(i => Merchant("engrave", EngraveFeeMap, CX, CY + 2 * i + (i == 3 ? 6 : 0))).ToList();
        try
        {
            string[] typed = { "12a", "0", "", "500" };
            var boxes = new List<List<Box>>();
            List<Box> creatureAsked = new();
            OnOwnThread(() =>
            {
                for (int i = 0; i < 4; i++)
                {
                    var (s, o, _) = casters[i];
                    s.Receive(SpellCastSupport.CastFrame(0, i == 3 ? mob.Id : customer.PlayerId));
                    if (i == 3) creatureAsked = Boxes(o);
                    o.Clear();
                    s.Receive(InputFrame(typed[i]));
                    boxes.Add(Boxes(o));
                }
            });
            Assert.True(boxes[0] is [{ Head: 2, Gfx: 71, Next: 0 } a] && a.Text == "You can only enter numbers, no letters."
                        && boxes[1] is [{ Head: 2, Gfx: 71, Next: 0 } z] && z.Text == "You must enter a positive number."
                        && boxes[2].Count == 0 && boxes[3].Count == 0 && Menus(uOut).Count == 0
                        && creatureAsked is [{ Kind: 0x0404 } c] && c.Text == "How much gold will you be asking for offering your services?",
                        $"boxes [{string.Join(" || ", boxes.Select(b => string.Join(" / ", b)))}], customer menus {Menus(uOut).Count}");
        }
        finally { Leave(EngraveFeeMap, casters.Select(c => c.session).Append(customer).ToArray()); _fx.World.DespawnMob(EngraveFeeMap, mob); }
    }

    /// <summary>The customer's answer: "No", the engraver reads "&lt;customer&gt; has not accepted your offer."; "Yes" short
    /// of the fee, "&lt;customer&gt; is unable to afford your services." (each a box with no button, RTK's popUp); no list
    /// either way and nothing moves. Red without the gold check.</summary>
    [Fact]
    public void EngraveOfferDeclinedOrUnaffordable()
    {
        var (no, nOut, nc) = Player(EngraveOfferMap, CX + 1, CY, coins: 50_000);
        var (poor, pOut, pc) = Player(EngraveOfferMap, CX + 1, CY + 4, coins: 999);
        var (e1, e1Out, _) = Merchant("engrave", EngraveOfferMap, CX, CY);
        var (e2, e2Out, _) = Merchant("engrave", EngraveOfferMap, CX, CY + 4);
        try
        {
            OnOwnThread(() =>
            {
                e1.Receive(SpellCastSupport.CastFrame(0, no.PlayerId));
                e1.Receive(InputFrame("1000"));
                e2.Receive(SpellCastSupport.CastFrame(0, poor.PlayerId));
                e2.Receive(InputFrame("1000"));
                e1Out.Clear();
                e2Out.Clear();
            });
            OnOwnThread(() => no.Receive(MenuFrame(2)));
            OnOwnThread(() => poor.Receive(MenuFrame(1)));
            Assert.True(Boxes(e1Out) is [{ Kind: 0x0001, Head: 0, Next: 0 } a] && a.Text == $"{no.CharName} has not accepted your offer."
                        && Boxes(e2Out) is [{ Kind: 0x0001, Head: 0, Next: 0 } b] && b.Text == $"{poor.CharName} is unable to afford your services."
                        && Grids(nOut).Count == 0 && Grids(pOut).Count == 0 && nc.Coins == 50_000 && pc.Coins == 999,
                        $"e1 [{string.Join(" / ", Boxes(e1Out))}], e2 [{string.Join(" / ", Boxes(e2Out))}]");
        }
        finally { Leave(EngraveOfferMap, no, poor, e1, e2); }
    }

    /// <summary>The proposed name, on the customer's side, over the picked item's icon: "Bob2" the box "You can only enter
    /// letters and spaces."; nineteen letters the box "The proposed name is too long. Maximum of 18 characters." with the
    /// next arrow; the engraver is asked nothing; eighteen pass. A name already engraved is asked as "Current Engrave:
    /// &lt;name&gt;". Red without the length check.</summary>
    [Fact]
    public void EngraveNameChecks()
    {
        var sword = Content.ItemByKey("steel_sword")!;
        var (customer, uOut, _) = Player(EngraveNameMap, CX + 1, CY, coins: 50_000, configure: ch =>
            ch.Inventory.Add(new InvItem(0, sword.Id, 1, sword.Durability) { CustomName = "** Old **" }));
        var (engraver, eOut, _) = Merchant("engrave", EngraveNameMap, CX, CY);
        try
        {
            string[] typed = { "Bob2", new string('a', 19), new string('b', 18) };
            var boxes = new List<List<Box>>();
            var menus = new List<int>();
            List<Box> asked = new();
            for (int i = 0; i < typed.Length; i++)
            {
                FreeSlot(engraver);
                OnOwnThread(() =>
                {
                    engraver.Receive(SpellCastSupport.CastFrame(0, customer.PlayerId));
                    engraver.Receive(InputFrame("100"));
                });
                OnOwnThread(() =>
                {
                    customer.Receive(MenuFrame(1));
                    uOut.Clear();
                    customer.Receive(BuyPick("** Old **"));
                    if (i == 0) asked = Boxes(uOut);
                    uOut.Clear();
                    eOut.Clear();
                    customer.Receive(InputFrame(typed[i]));
                    boxes.Add(Boxes(uOut));
                    menus.Add(Menus(eOut).Count);
                });
            }
            Assert.True(asked is [{ Kind: 0x0404 } q] && q.Text == "Current Engrave: ** Old **\n\n(You do not need to enter the asterisks (*))"
                        && boxes[0] is [{ Head: 2, Next: 0 } a] && a.Gfx == sword.Icon && a.Text == "You can only enter letters and spaces."
                        && boxes[1] is [{ Head: 2, Next: 1 } t] && t.Text == "The proposed name is too long. Maximum of 18 characters."
                        && boxes[2].Count == 0 && menus.SequenceEqual(new[] { 0, 0, 1 }),
                        $"asked [{string.Join(" / ", asked)}]; boxes [{string.Join(" || ", boxes.Select(b => string.Join(" / ", b)))}]; engraver menus [{string.Join(",", menus)}]");
        }
        finally { Leave(EngraveNameMap, customer, engraver); }
    }

    /// <summary>The engraver's answers: "No" on the naming policy, the customer reads "Your proposed engrave name does not
    /// meet the standards set forth in the Terms of Service."; "No" on "Are you sure", nothing. Neither moves gold or names
    /// anything. Red without the policy's "No" line.</summary>
    [Fact]
    public void EngravePolicyNoAndConfirmNo()
    {
        var sword = Content.ItemByKey("steel_sword")!;
        var (customer, uOut, uc) = Player(EngraveConfirmMap, CX + 1, CY, coins: 50_000, configure: ch =>
            ch.Inventory.Add(new InvItem(0, sword.Id, 1, sword.Durability)));
        var (engraver, eOut, ec) = Merchant("engrave", EngraveConfirmMap, CX, CY);
        try
        {
            uint before = ec.Coins;
            RunEngraveToPolicy(engraver, customer, "100", "Steel sword", "Bob");
            uOut.Clear();
            OnOwnThread(() => engraver.Receive(MenuFrame(2)));
            var told = Boxes(uOut);
            FreeSlot(engraver);
            RunEngraveToPolicy(engraver, customer, "100", "Steel sword", "Bob");
            OnOwnThread(() => { engraver.Receive(MenuFrame(1)); uOut.Clear(); eOut.Clear(); engraver.Receive(MenuFrame(2)); });
            Assert.True(told is [{ Kind: 0x0001, Head: 0, Next: 0 } t]
                        && t.Text == "Your proposed engrave name does not meet the standards set forth in the Terms of Service."
                        && Boxes(uOut).Count == 0 && Boxes(eOut).Count == 0 && uc.Coins == 50_000 && ec.Coins == before
                        && uc.Inventory.Single().CustomName == ""
                        && QuestState.Over(ec, QuestState.Registry).Get("engrave_timer") == 0,
                        $"told [{string.Join(" / ", told)}]; after no: c [{string.Join(" / ", Boxes(uOut))}] e [{string.Join(" / ", Boxes(eOut))}], " +
                        $"coins {uc.Coins}/{ec.Coins - before}, name '{uc.Inventory.Single().CustomName}'");
        }
        finally { Leave(EngraveConfirmMap, customer, engraver); }
    }

    /// <summary>The confirm's re-checks, inside both monitors: the customer going away since, the box "That character is no
    /// longer online."; the customer short of the fee since, "That character no longer has enough gold to cover your
    /// cost."; each over the item's icon, and nothing moves, no name, no timer. Red without either re-check in
    /// <c>Session.LuaEngraveItem</c>.</summary>
    [Fact]
    public void EngraveConfirmRechecksPresenceAndGold()
    {
        var sword = Content.ItemByKey("steel_sword")!;
        var (gone, _, gc) = Player(EngraveRecheckMap, CX + 1, CY, coins: 50_000, configure: ch =>
            ch.Inventory.Add(new InvItem(0, sword.Id, 1, sword.Durability)));
        var (spent, _, sc) = Player(EngraveRecheckMap, CX + 1, CY + 4, coins: 50_000, configure: ch =>
            ch.Inventory.Add(new InvItem(0, sword.Id, 1, sword.Durability)));
        var (e1, e1Out, e1c) = Merchant("engrave", EngraveRecheckMap, CX, CY);
        var (e2, e2Out, e2c) = Merchant("engrave", EngraveRecheckMap, CX, CY + 4);
        try
        {
            RunEngraveToPolicy(e1, gone, "1000", "Steel sword", "Bob");
            RunEngraveToPolicy(e2, spent, "1000", "Steel sword", "Bob");
            OnOwnThread(() => { e1.Receive(MenuFrame(1)); e2.Receive(MenuFrame(1)); });
            gone.WithState(() => LeavingField.SetValue(gone, true));
            spent.WithState(() => sc.Coins = 999);
            e1Out.Clear();
            e2Out.Clear();
            OnOwnThread(() => { e1.Receive(MenuFrame(1)); e2.Receive(MenuFrame(1)); });
            gone.WithState(() => LeavingField.SetValue(gone, false));
            Assert.True(Boxes(e1Out) is [{ Head: 2, Next: 0 } a] && a.Gfx == sword.Icon && a.Text == "That character is no longer online."
                        && Boxes(e2Out) is [{ Head: 2, Next: 0 } b] && b.Text == "That character no longer has enough gold to cover your cost."
                        && gc.Coins == 50_000 && sc.Coins == 999 && e1c.Coins == 0 && e2c.Coins == 0
                        && gc.Inventory.Single().CustomName == "" && sc.Inventory.Single().CustomName == ""
                        && QuestState.Over(e1c, QuestState.Registry).Get("engrave_timer") == 0
                        && QuestState.Over(e2c, QuestState.Registry).Get("engrave_timer") == 0,
                        $"e1 [{string.Join(" / ", Boxes(e1Out))}], e2 [{string.Join(" / ", Boxes(e2Out))}]; coins {gc.Coins}/{sc.Coins}/{e1c.Coins}/{e2c.Coins}");
        }
        finally { Leave(EngraveRecheckMap, gone, spent, e1, e2); }
    }

    /// <summary>Aimed at themselves: the fee, then the offer in the engraver's own box, and its "Yes" does nothing (RTK's
    /// script drops it: its freeAsync on its own caster, then its end). Red with the step named for one's own offer.</summary>
    [Fact]
    public void EngraveAtYourselfShowsTheOfferAndNothingFollows()
    {
        var sword = Content.ItemByKey("steel_sword")!;
        var (engraver, eOut, ec) = Merchant("engrave", EngraveSelfMap, CX, CY, configure: ch =>
        {
            ch.Coins = 50_000;
            ch.Inventory.Add(new InvItem(1, sword.Id, 1, sword.Durability));
        });
        try
        {
            string offer = "";
            OnOwnThread(() =>
            {
                engraver.Receive(SpellCastSupport.CastFrame(0, engraver.PlayerId));
                eOut.Clear();
                engraver.Receive(InputFrame("100"));
                offer = string.Join(" / ", Menus(eOut));
                eOut.Clear();
                engraver.Receive(MenuFrame(1));
            });
            Assert.True(offer == $"{engraver.CharName} is offering you their engraving services for 100 gold. Are you willing to pay this amount?|Yes|No"
                        && Grids(eOut).Count == 0 && eOut.BodiesOf(ServerOp.NpcDialog).Count == 0 && ec.Coins == 50_000,
                        $"offer [{offer}], grids {Grids(eOut).Count}, dialogs after {eOut.BodiesOf(ServerOp.NpcDialog).Count}");
        }
        finally { Leave(EngraveSelfMap, engraver); }
    }

    /// <summary>A closed box at each step answers nothing (the 4.95 client sends nothing on a close): the flow stopped after
    /// the fee box, the offer, the item list, the name box, the policy or the confirm (each the one prompt its step sent)
    /// leaves both players' gold, the item's name and the engraver's timer as they were, and no next prompt goes out. Red
    /// without the slice (generic sends no prompt).</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void EngraveStoppedAtAClosedBoxMovesNothing(int stage)
    {
        var sword = Content.ItemByKey("steel_sword")!;
        var (customer, uOut, uc) = Player(EngraveClosedMap, CX + 1, CY + 2 * stage, coins: 50_000, configure: ch =>
            ch.Inventory.Add(new InvItem(0, sword.Id, 1, sword.Durability)));
        var (engraver, eOut, ec) = Merchant("engrave", EngraveClosedMap, CX, CY + 2 * stage);
        try
        {
            OnOwnThread(() =>
            {
                engraver.Receive(SpellCastSupport.CastFrame(0, customer.PlayerId));
                if (stage > 1) engraver.Receive(InputFrame("100"));
            });
            OnOwnThread(() =>
            {
                if (stage > 2) customer.Receive(MenuFrame(1));
                if (stage > 3) customer.Receive(BuyPick("Steel sword"));
                if (stage > 4) customer.Receive(InputFrame("Bob"));
            });
            OnOwnThread(() => { if (stage > 5) engraver.Receive(MenuFrame(1)); });
            int uDialogs = uOut.BodiesOf(ServerOp.NpcDialog).Count + Grids(uOut).Count, eDialogs = eOut.BodiesOf(ServerOp.NpcDialog).Count;
            Thread.Sleep(50);
            Assert.True(uDialogs + eDialogs == stage                   // one prompt per step reached: the stage's is up
                        && uc.Coins == 50_000 && ec.Coins == 0 && uc.Inventory.Single().CustomName == ""
                        && QuestState.Over(ec, QuestState.Registry).Get("engrave_timer") == 0
                        && uOut.BodiesOf(ServerOp.NpcDialog).Count + Grids(uOut).Count == uDialogs
                        && eOut.BodiesOf(ServerOp.NpcDialog).Count == eDialogs,
                        $"stage {stage}: prompts {uDialogs}+{eDialogs}, coins {uc.Coins}/{ec.Coins}, name '{uc.Inventory.Single().CustomName}'");
        }
        finally { Leave(EngraveClosedMap, customer, engraver); }
    }

    /// <summary>Engrave's settle is written inside the customer's own monitor: while another thread holds it the engraver's
    /// "Yes" waits and the customer's gold and item are untouched, in both <c>StateRank</c> orders. Red with the settle's
    /// pair section taken out (Release).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EngraveSettlesInsideTheCustomersMonitor(bool engraverRanksFirst)
    {
        var sword = Content.ItemByKey("steel_sword")!;
        int y = engraverRanksFirst ? CY : CY + 6;
        Session engraver, customer;
        Character uc;
        Action<Character> bag = ch => ch.Inventory.Add(new InvItem(0, sword.Id, 1, sword.Durability));
        if (engraverRanksFirst)
        {
            (engraver, _, _) = Merchant("engrave", EngraveHeldMap, CX, y);
            (customer, _, uc) = Player(EngraveHeldMap, CX + 1, y, coins: 50_000, configure: bag);
        }
        else
        {
            (customer, _, uc) = Player(EngraveHeldMap, CX + 1, y, coins: 50_000, configure: bag);
            (engraver, _, _) = Merchant("engrave", EngraveHeldMap, CX, y);
        }
        Assert.Equal(engraverRanksFirst, engraver.StateRank < customer.StateRank);
        var run = new HeldRun();
        try
        {
            RunEngraveToPolicy(engraver, customer, "1000", "Steel sword", "Bob");
            OnOwnThread(() => engraver.Receive(MenuFrame(1)));
            WhileAnotherThreadHolds(run, customer, () => engraver.Receive(MenuFrame(1)),
                                    untouched: () => uc.Coins == 50_000 && uc.Inventory.Single().CustomName == "");
            Assert.Null(run.HolderFault);
            Assert.Null(run.ActorFault);
            Assert.False(run.FinishedWhileHeld, "the confirm finished while another thread held the customer's monitor");
            Assert.True(run.UntouchedWhileHeld, "the customer paid or was engraved while another thread held their monitor");
            Assert.True(uc.Coins == 49_000 && uc.Inventory.Single().CustomName == "** Bob **", "the settle never landed");
        }
        finally { if (run.Settled) Leave(EngraveHeldMap, engraver, customer); }
    }

    /// <summary>The offer reaches the customer inside their own monitor, and so does the drop of what they had waiting:
    /// with the item list of an earlier offer open, while another thread holds the customer's monitor the second engraver's
    /// fee step waits, and the customer has no new offer and still has that list, in both <c>StateRank</c> orders. Red with
    /// <c>Session.LuaPromptTarget</c>'s monitor entry taken out (Release).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EngravesOfferReachesTheCustomerInsideTheirOwnMonitor(bool engraverRanksFirst)
    {
        var sword = Content.ItemByKey("steel_sword")!;
        int y = engraverRanksFirst ? CY : CY + 6;
        Session engraver, customer, earlier;
        RecordingOutbound uOut;
        Action<Character> bag = ch => ch.Inventory.Add(new InvItem(0, sword.Id, 1, sword.Durability));
        (earlier, _, _) = Merchant("engrave", EngraveBoxHeldMap, CX + 2, y);
        if (engraverRanksFirst)
        {
            (engraver, _, _) = Merchant("engrave", EngraveBoxHeldMap, CX, y);
            (customer, uOut, _) = Player(EngraveBoxHeldMap, CX + 1, y, coins: 50_000, configure: bag);
        }
        else
        {
            (customer, uOut, _) = Player(EngraveBoxHeldMap, CX + 1, y, coins: 50_000, configure: bag);
            (engraver, _, _) = Merchant("engrave", EngraveBoxHeldMap, CX, y);
        }
        Assert.Equal(engraverRanksFirst, engraver.StateRank < customer.StateRank);
        var run = new HeldRun();
        try
        {
            OnOwnThread(() =>
            {
                earlier.Receive(SpellCastSupport.CastFrame(0, customer.PlayerId));
                earlier.Receive(InputFrame("100"));
            });
            OnOwnThread(() => customer.Receive(MenuFrame(1)));       // the earlier offer's list is open
            OnOwnThread(() => engraver.Receive(SpellCastSupport.CastFrame(0, customer.PlayerId)));
            uOut.Clear();
            WhileAnotherThreadHolds(run, customer, () => engraver.Receive(InputFrame("200")),
                                    untouched: () => Menus(uOut).Count == 0 && ShopReplyField.GetValue(customer) is not null);
            Assert.Null(run.HolderFault);
            Assert.Null(run.ActorFault);
            Assert.False(run.FinishedWhileHeld, "the fee step finished while another thread held the customer's monitor");
            Assert.True(run.UntouchedWhileHeld, "the offer or the drop reached the customer while another thread held their monitor");
            Assert.True(Menus(uOut).Count == 1 && customer.WithState(() => ShopReplyField.GetValue(customer)) is null,
                        "the offer never went out, or the list was never dropped");
        }
        finally { if (run.Settled) Leave(EngraveBoxHeldMap, engraver, customer, earlier); }
    }

    // ===== Gambling =============================================================================================

    /// <summary>The whole flow. 120 mana checked and never spent, the 30 s wait, "Who shall be gambling?" (the coins' icon);
    /// the first gambler's name, the wager menu "They will wager the amount of"; "10,000 Gold" drops the first gambler's
    /// waiting conversation and offers them "... your gamble of 10,000 Gold. ..."; "Yes" asks the merchant "Who will be
    /// trying their luck against this person?"; the second gambler's name offers them the same; "Yes" gives the merchant
    /// "Both parties agreed to this gamble. Proceed to finish." with the next arrow; on it one gambler wins 10,000 from the
    /// other ("You win." and "10000 gold added.", "You lose." and "10000 gold removed."), or on equal rolls neither moves
    /// and the merchant reads "There was a tie. No gold removed from either party.". The merchant gains nothing. Red without
    /// the row (generic).</summary>
    [Fact]
    public void GamblingRunsAWagerBetweenTwoPlayers()
    {
        var (a, aOut, ac) = Player(GambleMap, CX + 6, CY - 6, coins: 30_000);
        var (b, bOut, bc) = Player(GambleMap, CX - 6, CY + 6, coins: 30_000);
        var (merchant, mOut, mc) = Merchant("gambling", GambleMap, CX, CY);
        try
        {
            uint mcoins = mc.Coins;
            List<Box> asked = new(), second = new(), agreed = new();
            string wager = "", aOffer = "", bOffer = "";
            OnOwnThread(() =>
            {
                _ = a.DlgInputPush(0, 0, "a conversation the gambler has waiting");
                merchant.Receive(SpellCastSupport.CastFrame(0));
                asked = Boxes(mOut);
                mOut.Clear();
                merchant.Receive(InputFrame(a.CharName));
                wager = string.Join(" / ", Menus(mOut));
                aOut.Clear();
                merchant.Receive(MenuFrame(2));
                aOffer = string.Join(" / ", Menus(aOut));
            });
            long wait = SpellCastSupport.AetherLeft(merchant, "gambling");
            OnOwnThread(() => { mOut.Clear(); a.Receive(MenuFrame(1)); second = Boxes(mOut); });
            OnOwnThread(() => { bOut.Clear(); merchant.Receive(InputFrame(b.CharName)); bOffer = string.Join(" / ", Menus(bOut)); });
            OnOwnThread(() => { mOut.Clear(); b.Receive(MenuFrame(1)); agreed = Boxes(mOut); });
            aOut.Clear(); bOut.Clear(); mOut.Clear();
            OnOwnThread(() => merchant.Receive(NextFrame()));
            string offer = "A merchant is willing to assist you in your gamble of 10,000 Gold. You and your opponent will each " +
                           "put in that amount and the winner will receive double back. Do you accept?|Yes|No";
            var coins = (ac.Coins, bc.Coins);
            bool aWon = coins == (40_000u, 20_000u) && Lines(aOut) == "You win. | 10000 gold added." && Lines(bOut) == "You lose. | 10000 gold removed.";
            bool bWon = coins == (20_000u, 40_000u) && Lines(bOut) == "You win. | 10000 gold added." && Lines(aOut) == "You lose. | 10000 gold removed.";
            bool tie = coins == (30_000u, 30_000u) && Boxes(mOut) is [{ } t] && t.Text == "There was a tie. No gold removed from either party.";
            Assert.True(asked is [{ Kind: 0x0404, Head: 2 } q] && q.Text == "Who shall be gambling?" && q.Gfx == Content.ItemByKey("coins_1")!.ClientIcon
                        && mc.Mp == Mana && wait > 30_000 - SlackMs && wait <= 30_000
                        && wager == "They will wager the amount of|1,000 Gold|10,000 Gold|100,000 Gold"
                        && aOffer == offer && bOffer == offer
                        && second is [{ Kind: 0x0404 } s] && s.Text == "Who will be trying their luck against this person?"
                        && agreed is [{ Kind: 0x0001, Next: 1 } g] && g.Text == "Both parties agreed to this gamble. Proceed to finish."
                        && (aWon || bWon || tie) && mc.Coins == mcoins,
                        $"asked [{string.Join(" / ", asked)}], spent {Mana - mc.Mp}, wait {wait}; wager [{wager}]; a [{aOffer}]; " +
                        $"second [{string.Join(" / ", second)}]; b [{bOffer}]; agreed [{string.Join(" / ", agreed)}]; coins {coins}; " +
                        $"a [{Lines(aOut)}] b [{Lines(bOut)}] m [{string.Join(" / ", Boxes(mOut))}]");
        }
        finally { Leave(GambleMap, a, b, merchant); }
    }

    /// <summary>The settle itself, at chosen rolls: the higher roll's gambler wins the wager from the other, with graphic 2
    /// over the winner and the lines in the script's order; equal rolls move nothing and both read "There was a tie, so no
    /// gold removed."; a gambler short of the wager, the first named checked first, "short1" or "short2", nothing moving.
    /// Red with the comparison turned round.</summary>
    [Fact]
    public void GamblingSettlesByTheHigherRoll()
    {
        var (p1, o1, c1) = Player(GambleSettleMap, CX, CY, coins: 5_000);
        var (p2, o2, c2) = Player(GambleSettleMap, CX + 1, CY, coins: 5_000);
        var (merchant, _, _) = Merchant("gambling", GambleSettleMap, CX + 2, CY);
        var sp = Content.SpellByKey("gambling")!;
        try
        {
            string Settle(int r1, int r2) => merchant.WithState(() =>
                merchant.LuaSettleWager(sp, p1.CharName, p2.CharName, 1_000, r1, r2, "You win.", "You lose.", "There was a tie, so no gold removed."));
            string win1 = Settle(90, 10);
            var after1 = (c1.Coins, c2.Coins, Lines(o1), Lines(o2), Effects(o1, p1.PlayerId).Contains(2));
            o1.Clear(); o2.Clear();
            string win2 = Settle(10, 90);
            var after2 = (c1.Coins, c2.Coins, Lines(o1), Lines(o2));
            o1.Clear(); o2.Clear();
            string tie = Settle(50, 50);
            var after3 = (c1.Coins, c2.Coins, Lines(o1), Lines(o2));
            p2.WithState(() => c2.Coins = 999);
            string short2 = Settle(90, 10);
            p1.WithState(() => c1.Coins = 999);
            string short1 = Settle(90, 10);
            Assert.True(win1 == "win1" && after1 == (6_000u, 4_000u, "You win. | 1000 gold added.", "You lose. | 1000 gold removed.", true)
                        && win2 == "win2" && after2 == (5_000u, 5_000u, "You lose. | 1000 gold removed.", "You win. | 1000 gold added.")
                        && tie == "tie" && after3 == (5_000u, 5_000u, "There was a tie, so no gold removed.", "There was a tie, so no gold removed.")
                        && short2 == "short2" && short1 == "short1" && c1.Coins == 999 && c2.Coins == 999,
                        $"win1 {win1} {after1}; win2 {win2} {after2}; tie {tie} {after3}; short2 {short2}; short1 {short1}");
        }
        finally { Leave(GambleSettleMap, p1, p2, merchant); }
    }

    /// <summary>The typed gamblers' refusals, each a box over the coins' icon that ends it: "Bob2", "You can only enter
    /// letters and spaces."; nobody by the name, "That player is not online."; seven tiles off, "That player is not close
    /// enough to you." (six is near); the first gambler named again, "The person cannot gamble against themselves.". Short
    /// of 120, "Your will is too weak.", no wait; inside the wait, "Gambling isn't ready yet.". Red without the range test.</summary>
    [Fact]
    public void GamblingRefusals()
    {
        var (near, nOut, _) = Player(GambleRefuseMap, CX + 6, CY + 6, coins: 5_000);
        var (far, _, _) = Player(GambleRefuseMap, CX + 7, CY, coins: 5_000);
        var (poor, pOut, pc) = Merchant("gambling", GambleRefuseMap, CX - 10, CY, mana: 119);
        var merchants = Enumerable.Range(0, 3).Select(i => Merchant("gambling", GambleRefuseMap, CX, CY)).ToList();
        var (m4, m4Out, _) = Merchant("gambling", GambleRefuseMap, CX, CY);
        try
        {
            string[] typed = { "Bob2", "Nobodyhereatall", far.CharName };
            var boxes = new List<List<Box>>();
            OnOwnThread(() =>
            {
                poor.Receive(SpellCastSupport.CastFrame(0));
                for (int i = 0; i < typed.Length; i++)
                {
                    var (s, o, _) = merchants[i];
                    s.Receive(SpellCastSupport.CastFrame(0));
                    o.Clear();
                    s.Receive(InputFrame(typed[i]));
                    boxes.Add(Boxes(o));
                }
                m4.Receive(SpellCastSupport.CastFrame(0));
                m4.Receive(InputFrame(near.CharName));
                m4.Receive(MenuFrame(1));
            });
            OnOwnThread(() => near.Receive(MenuFrame(1)));
            OnOwnThread(() => { m4Out.Clear(); m4.Receive(InputFrame(near.CharName)); });
            var again = merchants[0];
            again.outbound.Clear();
            FreeSlot(again.session);
            again.session.Receive(SpellCastSupport.CastFrame(0));
            string[] want = { "You can only enter letters and spaces.", "That player is not online.", "That player is not close enough to you." };
            var problems = new List<string>();
            for (int i = 0; i < want.Length; i++)
                if (boxes[i] is not [{ Head: 2, Next: 0 } b] || b.Text != want[i]) problems.Add($"{typed[i]}: [{string.Join(" / ", boxes[i])}]");
            Assert.True(problems.Count == 0 && Boxes(m4Out) is [{ Head: 2 } same] && same.Text == "The person cannot gamble against themselves."
                        && Lines(pOut) == "Your will is too weak." && pc.Mp == 119 && SpellCastSupport.AetherLeft(poor, "gambling") == 0
                        && Lines(again.outbound) == "Gambling isn't ready yet.",
                        $"{string.Join("; ", problems)}; same [{string.Join(" / ", Boxes(m4Out))}]; poor [{Lines(pOut)}]; again [{Lines(again.outbound)}]");
        }
        finally
        {
            Leave(GambleRefuseMap, near, far, poor, m4);
            Leave(GambleRefuseMap, merchants.Select(m => m.session).ToArray());
        }
    }

    /// <summary>A gambler's "No" ends it, nothing said; "Yes" short of the wager, their box "You do not have enough gold."
    /// and nothing more, for the first and the second gambler alike. Red without the gold check.</summary>
    [Fact]
    public void GamblingDeclinedOrShortOfGold()
    {
        var (no, _, _) = Player(GambleAnswerMap, CX + 1, CY, coins: 50_000);
        var (poor1, p1Out, _) = Player(GambleAnswerMap, CX + 1, CY + 4, coins: 999);
        var (rich, _, _) = Player(GambleAnswerMap, CX + 2, CY + 8, coins: 50_000);
        var (poor2, p2Out, _) = Player(GambleAnswerMap, CX + 3, CY + 8, coins: 999);
        var (m1, m1Out, _) = Merchant("gambling", GambleAnswerMap, CX, CY);
        var (m2, m2Out, _) = Merchant("gambling", GambleAnswerMap, CX, CY + 4);
        var (m3, m3Out, _) = Merchant("gambling", GambleAnswerMap, CX, CY + 8);
        try
        {
            OnOwnThread(() =>
            {
                foreach (var (m, g) in new[] { (m1, no), (m2, poor1), (m3, rich) })
                {
                    m.Receive(SpellCastSupport.CastFrame(0));
                    m.Receive(InputFrame(g.CharName));
                    m.Receive(MenuFrame(1));
                }
            });
            m1Out.Clear(); m2Out.Clear();
            OnOwnThread(() => no.Receive(MenuFrame(2)));
            OnOwnThread(() => poor1.Receive(MenuFrame(1)));
            OnOwnThread(() => rich.Receive(MenuFrame(1)));
            OnOwnThread(() => m3.Receive(InputFrame(poor2.CharName)));
            m3Out.Clear();
            OnOwnThread(() => poor2.Receive(MenuFrame(1)));
            Assert.True(m1Out.BodiesOf(ServerOp.NpcDialog).Count == 0 && m2Out.BodiesOf(ServerOp.NpcDialog).Count == 0
                        && Boxes(p1Out).Last() is { Head: 2, Next: 0 } a && a.Text == "You do not have enough gold."
                        && Boxes(p2Out).Last() is { Head: 2, Next: 0 } b && b.Text == "You do not have enough gold."
                        && m3Out.BodiesOf(ServerOp.NpcDialog).Count == 0,
                        $"m1 {m1Out.BodiesOf(ServerOp.NpcDialog).Count}, m2 {m2Out.BodiesOf(ServerOp.NpcDialog).Count}, p1 [{string.Join(" / ", Boxes(p1Out))}], " +
                        $"p2 [{string.Join(" / ", Boxes(p2Out))}], m3 {m3Out.BodiesOf(ServerOp.NpcDialog).Count}");
        }
        finally { Leave(GambleAnswerMap, no, poor1, rich, poor2, m1, m2, m3); }
    }

    /// <summary>The re-checks at the merchant's next arrow, inside both gamblers' monitors: a gambler going away since,
    /// "One of the parties logged."; the second gambler short since, "&lt;second&gt; tried to cheat, they no longer have the
    /// money they were offering to gamble."; nothing moves. Red without the presence re-check in
    /// <c>Session.LuaSettleWager</c>.</summary>
    [Fact]
    public void GamblingRechecksAtTheRoll()
    {
        var (a1, _, a1c) = Player(GambleRecheckMap, CX + 1, CY, coins: 5_000);
        var (b1, _, b1c) = Player(GambleRecheckMap, CX + 2, CY, coins: 5_000);
        var (a2, _, a2c) = Player(GambleRecheckMap, CX + 1, CY + 4, coins: 5_000);
        var (b2, _, b2c) = Player(GambleRecheckMap, CX + 2, CY + 4, coins: 5_000);
        var (m1, m1Out, _) = Merchant("gambling", GambleRecheckMap, CX, CY);
        var (m2, m2Out, _) = Merchant("gambling", GambleRecheckMap, CX, CY + 4);
        try
        {
            RunGambleToAgreement(m1, a1, b1);
            RunGambleToAgreement(m2, a2, b2);
            a1.WithState(() => LeavingField.SetValue(a1, true));
            b2.WithState(() => b2c.Coins = 999);
            m1Out.Clear(); m2Out.Clear();
            OnOwnThread(() => { m1.Receive(NextFrame()); m2.Receive(NextFrame()); });
            a1.WithState(() => LeavingField.SetValue(a1, false));
            Assert.True(Boxes(m1Out) is [{ Head: 2 } g] && g.Text == "One of the parties logged."
                        && Boxes(m2Out) is [{ Head: 2 } c] && c.Text == $"{b2.CharName} tried to cheat, they no longer have the money they were offering to gamble."
                        && a1c.Coins == 5_000 && b1c.Coins == 5_000 && a2c.Coins == 5_000 && b2c.Coins == 999,
                        $"m1 [{string.Join(" / ", Boxes(m1Out))}], m2 [{string.Join(" / ", Boxes(m2Out))}]");
        }
        finally { Leave(GambleRecheckMap, a1, b1, a2, b2, m1, m2); }
    }

    /// <summary>Naming the merchant themselves as a gambler: the offer in the merchant's own box, and its "Yes" does nothing
    /// (RTK's script drops it). Red with the step named for one's own offer.</summary>
    [Fact]
    public void GamblingNamingTheMerchantShowsTheOfferAndNothingFollows()
    {
        var (merchant, mOut, mc) = Merchant("gambling", GambleSelfMap, CX, CY, configure: ch => ch.Coins = 50_000);
        try
        {
            string offer = "";
            OnOwnThread(() =>
            {
                merchant.Receive(SpellCastSupport.CastFrame(0));
                merchant.Receive(InputFrame(merchant.CharName));
                mOut.Clear();
                merchant.Receive(MenuFrame(1));
                offer = string.Join(" / ", Menus(mOut));
                mOut.Clear();
                merchant.Receive(MenuFrame(1));
            });
            Assert.True(offer.StartsWith("A merchant is willing to assist you in your gamble of 1,000 Gold.", StringComparison.Ordinal)
                        && mOut.BodiesOf(ServerOp.NpcDialog).Count == 0 && mc.Coins == 50_000,
                        $"offer [{offer}], dialogs after {mOut.BodiesOf(ServerOp.NpcDialog).Count}");
        }
        finally { Leave(GambleSelfMap, merchant); }
    }

    /// <summary>A closed box at each step answers nothing: stopped after the name box, the wager, either gambler's offer,
    /// the second name box or the agreement box (each the one prompt its step sent), nobody's gold moves and no next prompt
    /// goes out. Red without the slice (generic sends no prompt).</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void GamblingStoppedAtAClosedBoxMovesNothing(int stage)
    {
        int y = CY + 3 * stage;
        var (a, aOut, ac) = Player(GambleClosedMap, CX + 1, y, coins: 5_000);
        var (b, bOut, bc) = Player(GambleClosedMap, CX + 2, y, coins: 5_000);
        var (merchant, mOut, _) = Merchant("gambling", GambleClosedMap, CX, y);
        try
        {
            OnOwnThread(() =>
            {
                merchant.Receive(SpellCastSupport.CastFrame(0));
                if (stage > 1) merchant.Receive(InputFrame(a.CharName));
                if (stage > 2) merchant.Receive(MenuFrame(1));
            });
            OnOwnThread(() => { if (stage > 3) a.Receive(MenuFrame(1)); });
            OnOwnThread(() => { if (stage > 4) merchant.Receive(InputFrame(b.CharName)); });
            OnOwnThread(() => { if (stage > 5) b.Receive(MenuFrame(1)); });
            int dialogs = aOut.BodiesOf(ServerOp.NpcDialog).Count + bOut.BodiesOf(ServerOp.NpcDialog).Count + mOut.BodiesOf(ServerOp.NpcDialog).Count;
            Thread.Sleep(50);
            Assert.True(dialogs == stage                                 // one prompt per step reached: the stage's is up
                        && ac.Coins == 5_000 && bc.Coins == 5_000
                        && aOut.BodiesOf(ServerOp.NpcDialog).Count + bOut.BodiesOf(ServerOp.NpcDialog).Count + mOut.BodiesOf(ServerOp.NpcDialog).Count == dialogs,
                        $"stage {stage}: prompts {dialogs}, coins {ac.Coins}/{bc.Coins}");
        }
        finally { Leave(GambleClosedMap, a, b, merchant); }
    }

    /// <summary>Gambling's settle is written inside the gamblers' own monitors: while another thread holds the first
    /// gambler's, the merchant's next arrow waits and neither gambler's gold moves, in both <c>StateRank</c> orders (the
    /// merchant before the two gamblers and the first before the second, then the other way round). Red with the settle's pair section taken out (Release).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GamblingSettlesInsideTheGamblersMonitors(bool merchantRanksFirst)
    {
        int y = merchantRanksFirst ? CY : CY + 6;
        Session merchant, a, b;
        Character ac, bc;
        if (merchantRanksFirst)
        {
            (merchant, _, _) = Merchant("gambling", GambleHeldMap, CX, y);
            (a, _, ac) = Player(GambleHeldMap, CX + 1, y, coins: 5_000);
            (b, _, bc) = Player(GambleHeldMap, CX + 2, y, coins: 5_000);
        }
        else
        {
            (b, _, bc) = Player(GambleHeldMap, CX + 2, y, coins: 5_000);
            (a, _, ac) = Player(GambleHeldMap, CX + 1, y, coins: 5_000);
            (merchant, _, _) = Merchant("gambling", GambleHeldMap, CX, y);
        }
        Assert.Equal(merchantRanksFirst, merchant.StateRank < a.StateRank);
        Assert.Equal(merchantRanksFirst, a.StateRank < b.StateRank);
        var run = new HeldRun();
        try
        {
            RunGambleToAgreement(merchant, a, b);
            WhileAnotherThreadHolds(run, a, () => merchant.Receive(NextFrame()),
                                    untouched: () => ac.Coins == 5_000 && bc.Coins == 5_000);
            Assert.Null(run.HolderFault);
            Assert.Null(run.ActorFault);
            Assert.False(run.FinishedWhileHeld, "the roll finished while another thread held a gambler's monitor");
            Assert.True(run.UntouchedWhileHeld, "gold moved while another thread held a gambler's monitor");
            Assert.True(ac.Coins + bc.Coins == 10_000, "the wager was not conserved");
        }
        finally { if (run.Settled) Leave(GambleHeldMap, merchant, a, b); }
    }

    // ===== the teacher ==========================================================================================

    /// <summary>The Merchant teacher sells six, each at its script's level: Skill up and Engrave (no requirements()) from
    /// their rows' 0, Marketer guise from 33, Pilfer, Gambling and Golden fist from 99. BekyunArrow, whose price names
    /// merchant_token (in no item table), has no price. Red without the price rows: nothing.</summary>
    [Fact]
    public void TheMerchantTeacherSellsSix()
    {
        int path = Content.PathIdForClass("Merchant");
        Assert.Equal(11, path);
        var sold = Content.SubpathTeacherSpells(path, 99, Content.MaxMark);
        Assert.Equal("engrave:0 gambling:99 golden_fist:99 marketer_guise:33 pilfer:99 skill_up_merchant:0",
                     string.Join(" ", sold.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}:{s.Level}")));
        Assert.Null(Content.LearnCostFor(Content.SpellByKey("bekyunarrow")!, path));
        Assert.Equal("engrave marketer_guise skill_up_merchant",
                     string.Join(" ", Content.SubpathTeacherSpells(path, 98, Content.MaxMark).Select(s => s.Key).OrderBy(k => k, StringComparer.Ordinal)));
    }

    // ===== helpers ==============================================================================================

    /// <summary>Engrave from the cast to the engraver's naming-policy menu: the fee, the customer's "Yes", the pick of
    /// <paramref name="row"/>, the name <paramref name="text"/>.</summary>
    private static void RunEngraveToPolicy(Session engraver, Session customer, string fee, string row, string text)
    {
        OnOwnThread(() =>
        {
            engraver.Receive(SpellCastSupport.CastFrame(0, customer.PlayerId));
            engraver.Receive(InputFrame(fee));
        });
        OnOwnThread(() =>
        {
            customer.Receive(MenuFrame(1));
            customer.Receive(BuyPick(row));
            customer.Receive(InputFrame(text));
        });
    }

    /// <summary>Gambling from the cast to the merchant's agreement box: 1,000 Gold, both gamblers' "Yes".</summary>
    private static void RunGambleToAgreement(Session merchant, Session a, Session b)
    {
        OnOwnThread(() =>
        {
            merchant.Receive(SpellCastSupport.CastFrame(0));
            merchant.Receive(InputFrame(a.CharName));
            merchant.Receive(MenuFrame(1));
        });
        OnOwnThread(() => a.Receive(MenuFrame(1)));
        OnOwnThread(() => merchant.Receive(InputFrame(b.CharName)));
        OnOwnThread(() => b.Receive(MenuFrame(1)));
    }

    private static string Lines(RecordingOutbound o) => string.Join(" | ", SpellCastSupport.MiniTexts(o));

    private static List<string> Speech(RecordingOutbound o) =>
        o.BodiesOf(ServerOp.Speech).Select(b => Encoding.ASCII.GetString(b)).ToList();

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

    /// <summary>A pick in the item-list window (<c>0x39</c>): the buy tag at [0], the row's name length-prefixed at [7].</summary>
    private static byte[] BuyPick(string name)
    {
        var n = Encoding.ASCII.GetBytes(name);
        var body = new byte[8 + n.Length];
        body[0] = 2;
        body[7] = (byte)n.Length;
        n.CopyTo(body, 8);
        return SessionFixture.Frame(ClientOp.ShopReply, body);
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

    private sealed record Box(int Kind, uint Id, int Head, int Gfx, int Color, int Prev, int Next, string Text)
    {
        public override string ToString() => $"{Kind:x4} id {Id} head {Head} gfx {Gfx}/{Color} prev {Prev} next {Next} \"{Text}\"";
    }

    /// <summary>Every 0x30 box or input box <paramref name="o"/> was sent, in order (a menu is not one).</summary>
    private static List<Box> Boxes(RecordingOutbound o) =>
        o.BodiesOf(ServerOp.NpcDialog).Where(b => b.Length >= 23 && b[7] == 1 && !(b[0] == 2 && b[1] == 2)).Select(b =>
        {
            int len = (b[21] << 8) | b[22];
            return new Box((b[0] << 8) | b[1], (uint)((b[2] << 24) | (b[3] << 16) | (b[4] << 8) | b[5]), b[6],
                           (b[8] << 8) | b[9], b[10], b[19], b[20], Encoding.ASCII.GetString(b, 23, Math.Min(len, b.Length - 23)));
        }).ToList();

    private sealed record Grid(uint Id, int Head, int Gfx, string Prompt, List<(string Name, int Number)> Rows)
    {
        public override string ToString() => $"id {Id} head {Head} gfx {Gfx} \"{Prompt}\" [{string.Join(", ", Rows)}]";
    }

    /// <summary>Every item-list window (0x2f sub-kind 4) the player was shown: the head, the prompt and each row's name and
    /// number (4.95 rows: icon u16, number u32, name, blurb).</summary>
    private static List<Grid> Grids(RecordingOutbound o) =>
        o.BodiesOf(ServerOp.Shop).Where(b => b.Length > 17 && b[0] == 4).Select(b =>
        {
            uint id = (uint)((b[2] << 24) | (b[3] << 16) | (b[4] << 8) | b[5]);
            int at = 15, plen = (b[at] << 8) | b[at + 1];
            string prompt = Encoding.ASCII.GetString(b, at + 2, plen);
            at += 2 + plen + 2;
            int count = (b[at] << 8) | b[at + 1];
            at += 2;
            var rows = new List<(string, int)>();
            for (int i = 0; i < count; i++)
            {
                at += 2;
                int number = (int)U32(b, at);
                at += 4;
                int nl = b[at++];
                string name = Encoding.ASCII.GetString(b, at, nl);
                at += nl;
                at += 1 + b[at];
                rows.Add((name, number));
            }
            return new Grid(id, b[6], (b[8] << 8) | b[9], prompt, rows);
        }).ToList();

    private static uint U32(byte[] b, int at) => (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]);

    /// <summary>The spell graphics (0x29) sent over <paramref name="id"/>, as effect ids.</summary>
    private static List<int> Effects(RecordingOutbound outbound, uint id) =>
        outbound.BodiesOf(ServerOp.Effect).Where(b => U32(b, 0) == id).Select(b => b[4] - ServerConfig.Current.EfxWireOffset).ToList();

    private static List<int> Sounds(RecordingOutbound outbound) =>
        outbound.BodiesOf(ServerOp.Audio).Where(b => b.Length >= 4).Select(b => (b[2] << 8) | b[3]).ToList();

    private static (ushort Look, byte Color) Morph(Session s)
    {
        (ushort, byte) got = default;
        s.WithState(() => got = ((ushort)MorphLookField.GetValue(s)!, (byte)MorphColorField.GetValue(s)!));
        return got;
    }

    private static void FreeSlot(Session s) => s.WithState(() => { NextActionTick.SetValue(s, 0L); ActionCount.SetValue(s, 0); });

    /// <summary>A floor item at (<paramref name="x"/>, <paramref name="y"/>): <paramref name="itemId"/> -1 is coins.</summary>
    private void Lay(ushort map, int x, int y, int itemId, int amount, ushort dura = 0, string name = "", string owner = "",
                     uint looter = 0)
    {
        ushort graphic = itemId < 0 ? (ushort)0 : Content.ItemById(itemId)!.Icon;
        _fx.World.DropItem(map, new GroundItem
        {
            Id = _fx.World.AllocateItemId(), ItemId = itemId, X = (ushort)x, Y = (ushort)y, Amount = amount, Dura = dura,
            Graphic = graphic, CustomName = name, Owner = owner, LooterId = looter,
            LockedUntil = looter == 0 ? 0 : Environment.TickCount64 + 300_000,
        });
    }

    private void ClearFloor(ushort map)
    {
        foreach (var g in _fx.World.ItemsOn(map))
            while (_fx.World.PickUp(map, g.X, g.Y) is not null) { }
    }

    /// <summary>Run <paramref name="body"/> on a thread of its own with no synchronisation context, as a read loop runs a
    /// packet: an await inside it then resumes inline on the thread that answers it.</summary>
    private static void OnOwnThread(Action body)
    {
        Exception? fault = null;
        var t = new Thread(() => fault = Record.Exception(body)) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(30)), "the packet thread never finished");
        Assert.Null(fault);
    }

    /// <summary>A letters-only name, unique in this process: the typed names take letters, spaces and an apostrophe.</summary>
    private static string Letters(string stem)
    {
        int n = Interlocked.Increment(ref _serial);
        var sb = new StringBuilder(stem);
        do { sb.Append((char)('a' + n % 26)); n /= 26; } while (n > 0);
        return sb.ToString();
    }

    private Mob AddCreature(ushort map, int x, int y)
    {
        var mob = new Mob(_fx.World.AllocateMobId(), 1, (ushort)x, (ushort)y, $"Merchant target {Interlocked.Increment(ref _serial)}", 100_000);
        mob.Hp = 100_000;
        _fx.World.AddMob(map, mob);
        return mob;
    }

    /// <summary>A level-99 Merchant at (<paramref name="x"/>, <paramref name="y"/>) facing east, with <paramref name="key"/> in
    /// slot 0 of the book, 10,000 vita, on a wide content-free map, no gold.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Merchant(string key, ushort map, int x, int y,
                                                                              uint mana = Mana, uint maxMana = MaxMana,
                                                                              Action<Character>? configure = null)
    {
        var made = _fx.PlayerWith(Letters("Merchant"), ch =>
        {
            ch.ClassName = "Merchant";
            ch.Level = 99;
            ch.Ac = 0;
            ch.MaxHp = Hp; ch.Hp = Hp;
            ch.MaxMp = maxMana; ch.Mp = mana;
            ch.Coins = 0;
            ch.Dir = 1;
            ch.MapXs = 100; ch.MapYs = 100;
            ch.Spells.Add(Content.SpellByKey(key)!.Id);
            configure?.Invoke(ch);
        }, map, (ushort)x, (ushort)y);
        return made;
    }

    private (Session session, RecordingOutbound outbound, Character c) Player(ushort map, int x, int y, uint coins = 0,
                                                                            Action<Character>? configure = null) =>
        _fx.PlayerWith(Letters("Other"), ch =>
        {
            ch.Level = 50;
            ch.Ac = 0;
            ch.MaxHp = Hp; ch.Hp = Hp;
            ch.MaxMp = 1_000; ch.Mp = 0;
            ch.Coins = coins;
            ch.Dir = 1;
            ch.MapXs = 100; ch.MapYs = 100;
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
