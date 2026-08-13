namespace Server;

/// <summary>
/// The peasant tutorial chain (RTK <c>Accepted/NPCs/tutorial/main_tutorial_npc.lua</c>, the shared
/// Jadespear #49 / Ironheart #20 script), driving <c>quest["tutorial_quest"]</c> through 14 stages. Ported
/// faithfully: each stage's dialog, item/creature portraits, gates and rewards match the Lua.
///
/// Reality note — some stages gate on world content this server doesn't have yet (a fishing NPC that grants
/// the <c>learned_to_fish</c> legend, the ice-beast NPC). Those stages show the real dialog but can't be
/// COMPLETED until that content exists — which is exactly "port as far as current content allows."
/// Stage 7's Chu Rua is NOT one of them: TK1111.map through TK1116.map are all present, so the shore, the
/// rabbit, the dolmen and the Tiger Pass all render and the stage is completable end to end (the one map RTK
/// uses that we lack is TK1117, the tiger-free copy — see Session.TryGinseng, which gates on a flag instead).
/// Stage 11's missing brother IS implemented (Haguru on Du Mountain — see
/// <see cref="FindBrother"/>). Most item-turn-in stages (armor, meat, rose+chestnut, ogre cider, antlers,
/// mica) are completable via shops or <c>@item</c>; stage 13's student cap is NOT in any shop or drop table,
/// so <c>@item</c> is currently its only route — the wool → Yon → cloth → Caretaker chain the dialog
/// describes has no scripts behind it yet. The script's separate warrior-armor branch (Chongun's tiger
/// essence, needing the quest items) is still out of scope; its path-choice branch is ported — see
/// <see cref="PathChoice"/>.
///
/// ERA — stages 11 and 13 were added 2001-03-18 and are gated on <see cref="Era.DuMountainQuest"/> /
/// <see cref="Era.StudentCapQuest"/>; before that date the chain simply runs 10 → 12 and ends. The
/// first-steps beats this chain dispatches into (<see cref="NoviceQuest"/>) move out of the tutor and into
/// the newbie area on 2000-10-06. See <see cref="StageInEra"/> and docs/Era-Gating.md.
/// </summary>
public static class TutorialQuest
{
    // sub-flags (RTK player.quest[...] booleans), kept in the int registry alongside the main stage.
    private const string Stage      = "tutorial_quest";
    private const string GaveGold   = "tutorial_quest1_gave_gold";
    private const string GaveMeat   = "tutorial_quest2_gave_meat";
    private const string GaveSword  = "tutorial_quest8_gave_sword";

    public static readonly QuestDef Def = new()
    {
        Key  = Stage,
        Name = "Continue my training",
        Talk = Run,
    };

    private static async Task Run(NpcContext ctx)
    {
        // RTK main_tutorial_npc.lua:14 — a Peasant who has reached level 5 gets THIS branch instead of the
        // tutorial, on every click, until they pick a path. The Lua `return`s, so it's a hard block, and
        // deliberately so: Session.AwardExp already froze them at level 5, so continuing the chain would hand
        // out exp rewards that evaporate on arrival while hiding the one thing they actually need to do.
        // Sits above the stage-0 intro and the NoviceQuest dispatch because the Lua's check is the first thing
        // in `click` — a player who hits 5 mid-chain is blocked there too, not just between stages.
        if (ctx.Level >= 5 && ctx.ClassId == 0)
        {
            await PathChoice(ctx);
            return;
        }

        int stage = ctx.Stage(Stage);

        // This chain's own greeting still opens the whole conversation — it ends on "Click on me to learn...",
        // which is exactly the invitation to start the first-steps chain. So stage 0 runs before anything else.
        if (stage == 0)
        {
            await Intro(ctx);
            return;
        }

        // The reconstructed first-steps chain then runs to completion, as part of the SAME conversation — the
        // tutor must never offer a choice between the two, so this dispatches rather than adding a menu entry.
        // Era gate: these beats MOVED into the newbie area on 2000-10-06 rather than being retired, so from
        // that date the tutor must not teach them a second time — a player reaching him has already been
        // handed the saber, the garb and Soothe out there. Before it the area doesn't exist and the tutor is
        // the only place they can come from. Exactly one of the pair is ever live (Era.TutorNoviceChain
        // retires the day Era.NewbieArea arrives), and the tutor himself is present in every era either way.
        if (Era.Has(Era.TutorNoviceChain) && ctx.Stage(NoviceQuest.Key) < NoviceQuest.Done)
        {
            await NoviceQuest.Run(ctx);
            return;
        }

        // Step over any stage whose quest doesn't exist yet at the server's era date. Deliberately WITHOUT
        // rewriting the saved stage: the gate is deployment config, and config must never mutate a
        // player's progress. A character stored at 11 with the Du Mountain quest switched off simply
        // dispatches as 12, and moving the era date forward later hands them that same quest back rather
        // than having silently consumed it. Stage 13 skipping to 14 lands on the closing line below, which
        // is exactly where the chain ended before 2001-03-18.
        while (stage <= 13 && !StageInEra(stage)) stage++;

        switch (stage)
        {
            case 0:  await Intro(ctx);        break;
            case 1:  await BuyArmor(ctx);     break;
            case 2:  await SellMeat(ctx);     break;
            case 3:  await FindItems(ctx);    break;
            case 4:  await Fishing(ctx);      break;
            case 5:  await Exploration(ctx);  break;
            case 6:  await OgreCider(ctx);    break;
            case 7:  await ChuRua(ctx);       break;
            case 8:  await GroupHunt(ctx);    break;
            case 9:  await Spelunking(ctx);   break;
            case 10: await HorseRiding(ctx);  break;
            case 11: await FindBrother(ctx);  break;
            case 12: await BetterWeapon(ctx); break;
            case 13: await StudentCap(ctx);   break;
            default: await ctx.Say("I have taught you all that I can, young one. The time has come for you to " +
                                   "venture out into the Kingdoms and create your own legends."); break;
        }
    }

    /// <summary>Does this stage's quest exist at the server's era date? Only the two 2001-03-18 additions
    /// carry a date — the rest of the chain is older than the surviving archive and is always present, so
    /// they're the only two the tutor can ever be missing. Both the mountain and Haguru himself predate
    /// the quest and stay in the world either way; see <see cref="Era.DuMountainQuest"/>.</summary>
    private static bool StageInEra(int stage) => stage switch
    {
        11 => Era.Has(Era.DuMountainQuest),
        13 => Era.Has(Era.StudentCapQuest),
        _  => true,
    };

    /// <summary>The level-5 Peasant path-choice branch (RTK <c>main_tutorial_npc.lua:14-122</c>): explain the
    /// four paths, or ferry the player to a guild hall. The tutor only WARPS — the Guildmaster inside is what
    /// assigns the class (<see cref="ClassTrainerAbility"/>), exactly as in RTK.
    ///
    /// Divergence: the halls are the player's own kingdom's. The Lua hardcodes Buya's four (341-344) even for a
    /// Kugnae player, but f1npc.lua's level5popupDialog — the same milestone, reached through F1 — picks by
    /// <c>player.country</c>, so the tutor's copy reads as an oversight rather than a rule. Map ids match
    /// Session.ChoosePathMenu so both routes land in the same place.</summary>
    private static async Task PathChoice(NpcContext ctx)
    {
        // RTK order: warriors, rogues, mages, poets (its choice 2..5), each with its own send-off line.
        (string label, ushort kugnae, ushort buya, string sendOff)[] guilds =
        {
            ("Show me the Warriors guild.", 11, 341, "Ah, the heart of a fighter. This is the path for the true fighter. Let me show you to their hall now."),
            ("Show me the Rogues guild.",   15, 343, "A nimble fighter is what you want to be? Let me show you to their hall now."),
            ("Show me the Mage guild.",     13, 342, "A mastery of magic is what you seek? Let me show you to their hall now."),
            ("Show me the Poet guild.",     17, 344, "A caring, nurturing soul you are indeed. Let me show you to their hall now."),
        };

        await ctx.Say("Oh my, you are growing fast! You have already reached the 5th level. But I notice you have not yet picked your path.",
                      "You should really look into picking your path before you continue with these tasks.");

        var opts = new List<string> { "Can you explain the paths?" };
        foreach (var g in guilds) opts.Add(g.label);
        opts.Add("I will pick one later.");

        int choice = await ctx.Menu("Would you like me to send you to your guild to pick your destiny, or would you like to continue?", opts);

        if (choice == 1)
        {
            await ctx.Say(
                "In this land we have 4 main paths. They are the Warriors, the Rogues, the Mages, and the Poets.",
                "Warriors are the fighters, they use their brute force to kill their foes, and can power through large numbers of mobs quickly.",
                "Rogues are also fighters, but use more magic to assist them. A nimble and deadly killer one on one.",
                "Magi are the magic users of the land. Powerful in the art of offensive magic, and relies on range attacks",
                "Finally the Poets. They are the healers of the land. While they kill very little for themselves, they are always a welcomed addition to every group.",
                "You can learn more about each path from the Guild tutors, found in the lower left corner of each guild.");
            return;
        }

        if (choice >= 2 && choice <= 5)
        {
            var g = guilds[choice - 2];
            await ctx.Say(g.sendOff, "Remember to return to me later, so you can continue your tutorial.");
            ctx.Warp(ctx.Nation == 2 ? g.buya : g.kugnae, 8, 7);
            return;
        }

        if (choice == 6)
            await ctx.Say("This is your choice... But remember this, any experience you gain now until you pick your path will go to waste. Pick your path soon...");

        // choice 0 (the player closed the menu) says nothing and changes nothing — they're still blocked.
    }

    // stage 0 -> 1 (only reachable once NoviceQuest is finished — see the dispatch at the top of Run).
    private static async Task Intro(NpcContext ctx)
    {
        ctx.SetStage(Stage, 1);
        await ctx.Say("Greetings and welcome to my home. I see you are eager to get on with your adventure. " +
                      "Before you do however, there is much more you need to learn. Click on me to learn... ");
    }

    // stage 1: buying (bring the sex-appropriate peasant armor from the blacksmith)
    private static async Task BuyArmor(NpcContext ctx)
    {
        // RTK items = {war_platemail (male), spring_mail_dress (female)}, indexed by player.sex (0/1).
        string armor = ctx.Sex == 1 ? "spring_mail_dress" : "war_platemail";
        string armorName = ctx.ItemName(armor);

        if (ctx.HasItem(armor) || ctx.HasEquipped(armor))
        {
            ctx.AwardExp(100);
            ctx.SetReg(GaveGold, 0);
            ctx.SetStage(Stage, 2);
            await ctx.Say("You've done well. Keep the armor. It will serve you well against your first foe... later. Press <u> to wear it.",
                          "If you would like another quest, let me know, I have plenty to teach a young one like yourself.");
            return;
        }

        if (ctx.Reg(GaveGold) == 1)
        {
            await ctx.SayItem(armor, $"I am still waiting for that {armorName}. Please go visit the blacksmith.");
            return;
        }

        await ctx.Say("A hero has to be very wise of the world, and that only comes from experience.",
                      "Experience will teach you that you'd better equip yourself well.",
                      "You need to understand how to buy and sell items. First, you need to buy something...");
        await ctx.Say("You've gained some of this old warrior's trust. Here's some money for some armor.");
        ctx.AwardGold(20);
        ctx.SetReg(GaveGold, 1);
        await ctx.SayItem(armor, $"Go to the blacksmith, get a {armorName}, and bring it back. You'll find it listed under <Peasant's Clothes>");
        await ctx.SayLook(6, 13, "You can find the blacksmith in Buya, at the location 18,103, or in Kugnae, 60,122. If you forget, you can check on the mini map by pressing 'm'.");
        await ctx.SayLook(6, 13, "Just try clicking on the old man, he's got a one track mind. He'll try to sell you something.");
        await ctx.SayItem(armor, $"Get the {armorName} now. Return and tap on me");
    }

    // stage 2: selling (take rabbit meat to the butcher, buy a meat scrap)
    private static async Task SellMeat(NpcContext ctx)
    {
        if (ctx.HasItem("meat_scrap"))
        {
            ctx.TakeItem("meat_scrap", 1);
            ctx.AwardExp(100);
            ctx.SetStage(Stage, 3);
            ctx.SetReg(GaveMeat, 0);
            await ctx.Say("You're a lot better than that last apprentice. He was...well, I'll not go on about that.");
            await ctx.Say("Keep it up and you might find yourself getting referred to as a Hero sometime... or a merchant at any rate.");
            await ctx.Say("If you would like another quest, let me know. I have plenty to teach a young one like yourself.");
            return;
        }

        if (ctx.Reg(GaveMeat) == 1)
        {
            await ctx.SayItem("meat_scrap", "I've already given you the rabbit meat. Buy a meat scrap while you are at the butcher.");
            return;
        }

        await ctx.Say("Money doesn't make a man, but it does mend a sword. You can take some of the animal flesh to the butcher.");
        await ctx.SayLook(11, 3, "She's stingy... but it's a way to get some money. Anyway, if you're going to the butcher, learn to sell.");
        ctx.SetReg(GaveMeat, 1);
        ctx.GiveItem("rabbit_meat", 5);
        await ctx.SayItem("rabbit_meat", "Here's five rabbit corpses. Whew! they do start to stink. Take these to the butcher's shop.");
        await ctx.SayLook(11, 3, "You can find the butcher in Buya, at the location 39,129, or in Kugnae 41,131. If you forget, you can check on the mini map by pressing 'm'.");
        await ctx.Say("You'll see what kind of bargain she'll get from ya. And don't stop nowhere on the way back.");
        await ctx.SayItem("meat_scrap", "Buy a Meat scrap while you are at the butcher's. I'll be expecting Meat scrap.");
    }

    // stage 3: finding items (pick a rose from a bush, gather 5 chestnuts)
    private static async Task FindItems(NpcContext ctx)
    {
        if (ctx.HasItem("chestnut", 5) && ctx.HasItem("rose", 1))
        {
            ctx.TakeItem("chestnut", 5);
            ctx.TakeItem("rose", 1);
            ctx.AwardExp(150);
            ctx.SetStage(Stage, 4);
            await ctx.Say("Perfect! A Rose for my love, and some Chestnuts to eat. Thank you for getting these items for me.",
                          "Remember that there are other ways to get some items to sell to the merchants, or to other citizens.");
            await ctx.Say("If you would like another quest, let me know, I have plenty to teach a young one like yourself.");
            return;
        }

        await ctx.Say("You are quite a merchant now, but you only know how to buy and sell items you already have.",
                      "You need to learn about getting your own items. Yes, some come from the creatures you have slain, but there is more you can find.");
        await ctx.Say("First you should find me a Rose, I hear there is a bush in the city that you can pick them from.");
        await ctx.Say("In Buya the bush is located in the South, near 112,138. In Kugnae, the bush is located near the Lotus Chapel at 152,190.\n\nJust go near the bush, and you will find some.",
                      "I am also hungry for some Chestnuts, you can collect 5 for me in the Northwest of Buya at 27,47. In Kugnae, there is a small farm at 111,156.\n\nThey are small dark nuts, so you will have to look carefully.",
                      "Collect these items, and return to me when you have them.");
    }

    // stage 4: fishing (needs a minnow AND the learned_to_fish flag — both granted by FishNpc/Bate, see
    // FishAbility; the catch is the normal 25% roll here as everywhere else, so this takes a few casts).
    private static async Task Fishing(NpcContext ctx)
    {
        if (ctx.HasItem("minnow", 1) && ctx.Reg("learned_to_fish") == 1)
        {
            ctx.TakeItem("minnow", 1);
            ctx.AwardExp(50);
            ctx.AwardGold(5);
            ctx.SetStage(Stage, 5);
            await ctx.Say("Thanks for the fish! That wasn't so hard was it?",
                          "I've heard stores about people finding pretty strange things while fishing.",
                          "If you would like another quest, let me know, I have plenty to teach a young one like yourself.");
            return;
        }

        await ctx.Say("So, you are interested in more things to do? Fishing can weaken your fighting skills, but is a nice diversion from time to time.",
                      "Find Bate on the west side of Kugnae at 28,170 or Wim in the southeast of Buya at 109,88. Say to him out loud 'I'd like to fish'.",
                      "If you bring me back a Minnow, I'll give you a little gold.");
    }

    // stage 5: exploration (needs the talked_to_tutor legend from the kingdom greeter/librarian — not in yet).
    private static async Task Exploration(NpcContext ctx)
    {
        if (ctx.Reg("talked_to_tutor") == 1)
        {
            ctx.AwardExp(150);
            ctx.SetStage(Stage, 6);
            ctx.SetReg("talked_to_tutor", 0);
            await ctx.Say("I am glad to see you have discovered our kingdoms heart, and I hope you enjoyed looking around inside the safety of the kingdoms walls.",
                          "If you would like another quest, let me know, I have plenty to teach a young one like yourself.");
            return;
        }

        await ctx.Say("Well, we are finding our way around now, arn't we? But do you understand the Kingdom around you?",
                      "It's time you got some culture in you.\n\nGo to the palace and look around. You will find the main palace with the kingdom greeter in Buya at 73,56, or in Kugnae at 110,123.",
                      "To the south east of Buya is the Kingdom of Koguryo, whose capitol city is Kugnae. Their King is called Mhul.",
                      "You will also find the theater where the Muses occasionally hold poetry competitions, and the library where our kingdoms knowledge is stored.",
                      "When you get to the library mention my name to the librarian, I am sure he will have some words to say to you. Remember to say my name out loud.");
    }

    // stage 6: ogre cider (fetch — completable via @item; RTK checks hasItemDura, we treat as hasItem)
    private static async Task OgreCider(NpcContext ctx)
    {
        if (ctx.HasItem("ogre_cider", 1))
        {
            ctx.AwardExp(150);
            ctx.SetStage(Stage, 7);
            ctx.TakeItem("ogre_cider", 1);
            await ctx.Say("Terrific, you have some Ogre cider! Nothing like some cider to wash down a meal.",
                          "If you want to be successful, you'll have to explore many places outside of the cozy towns.",
                          "If you would like another quest, let me know, I have plenty to teach a young one like yourself.");
            return;
        }

        await ctx.Say("There's much more to the world than the city. Other lands hold different terrain, challenges, and goods.");
        await ctx.SayItem("ogre_cider", "I have a taste for ogre cider, but it's tough stuff to find around here. Most ogres will rough you up pretty good, too.");
        await ctx.SayLook(185, 14, "But I hear that in Hamgyong Nam-Do to the south east lives a relatively pleasant ogre who sometimes trades with humans.");
        await ctx.Say("To journey to other lands, first go out the north gate of the city. You'll find yourself in a gathering place. You will be able to get on a map there.",
                      "Visit Hamgyong Nam-Do and pick up some cider for me.");
        await ctx.SayItem("ogre_cider", "I'll be waiting here for the cider. Don't get lost now!");
    }

    // stage 7: By the Sea — Chu Rua (needs the aided_chu_rua legend, granted by the turtle on Guol Shore once
    // you bring him the young ginseng; see npc_dialog.lua npcs.ChuRuaNpc). The whole chain renders and is
    // completable. Note this warp is a ONE-WAY lift: Chu Rua does not send you back, so the player walks home
    // and returns here on foot — which is what the era walkthroughs describe.
    private static async Task ChuRua(NpcContext ctx)
    {
        if (ctx.HasLegend("aided_chu_rua"))
        {
            ctx.SetStage(Stage, 8);
            await ctx.Say("The Dragon King shall fare better because of you.");
            return;
        }

        await ctx.Say("The Dragon King lives beneath the waves, but he has fallen very ill. I know because a turtle told me.");
        await ctx.SayLook(174, 0, "The turtle, named Chu Rua, swam to shore to ask young men of land to help him. He is in distress and needs help.");
        int choice = await ctx.Menu("Will you go to him now? Be careful, it is dangerous and you may get lost.",
                                    new[] { "I am willing to risk it", "O, in that case, never mind" });
        if (choice == 1)
        {
            await ctx.Say("I know of a secret way to the shore. From there, you must find Chu Rua and use all your wits and cunning to succeed.");
            ctx.Warp(1111, 4, 18);   // Chu Rua's shore — not renderable in 4.95 yet; Warp declines without stranding
        }
        else if (choice == 2)
        {
            await ctx.Say("So be it.");
        }
    }

    // stage 8: group hunting (gather 3 antlers from deer — completable via kills/@item)
    private static async Task GroupHunt(NpcContext ctx)
    {
        if (ctx.HasItem("antler", 3))
        {
            ctx.TakeItem("antler", 3);
            ctx.AwardExp(200);
            ctx.SetStage(Stage, 9);
            // RTK also casts sanctuary (a protective blessing) here; not modelled.
            await ctx.Say("You are a great fighter, that has learnt well. I hope you fought well and defended your other members well.",
                          "If you would like another quest, let me know, I have plenty to teach a young one like yourself.");
            return;
        }

        // Deer are the first stage that genuinely needs a weapon (level 5, 300 vita, vs the rabbit/squirrel the
        // wooden saber was for), so the novice sword goes out HERE, with the task — same shape as BuyArmor's
        // gold and SellMeat's rabbit meat. Archive has it as "Tutor - Free" (tswolf swords table, Jan 2001),
        // later re-documented as the tutorial area's Angel Quiz. Once only; the Smith stocks no replacement.
        //
        // Era gate: the sword is the ONE first-steps reward that doesn't sit in NoviceQuest (it hangs off this
        // stage, which is not part of the moved chain), so the dispatch at the top of Run doesn't cover it —
        // without this check the tutor hands out a second sword to a player who already earned one from the
        // Woodland Guard's law quiz (npc_dialog.lua WoodlandGuardNpc). Keyed on the AREA existing rather than
        // on Era.TutorNoviceChain because the area is where the replacement comes from: with gating switched
        // off entirely every feature is on, new characters still spawn into 4711 (CharacterFactory.StartFor)
        // and still pass the guard — the gate out of 4717 is HIS act, so the quiz can't be skipped — which
        // makes "the area exists" the accurate reading of "they already have one" in that case too.
        if (!Era.Has(Era.NewbieArea) && ctx.Reg(GaveSword) == 0)
        {
            ctx.GiveItem("novice_sword", 1);
            ctx.SetReg(GaveSword, 1);
            await ctx.SayItem("novice_sword", "Before you go — that saber has served you, but it was cut for rabbits. Take this novice sword.");
        }

        await ctx.Say("You're coming well by yourself, but solitary legends die fast. Form a covenant with others for great adventure.",
                      "Close this scroll. Press <f> or click 'GROUP' tab. Then return and read on.",
                      "It is your group status, which might be blank right now.",
                      "You will gain more Experience when you are in a group. You and the members have more total Experience gained.",
                      "You want to find someone about your level, so that you both get enough Experience.",
                      "Adventure with someone you like and trust. You'll learn which Paths combine well. Poets always help groups.",
                      "Press <shift><g> to make yourself sociable so that you can join a group.");
        await ctx.SayLook(89, 5, "Go now, group with a number of persons to hunt about 12 deer (they have antlers). Hunt more if your group is larger.",
                                 "It is best to divide the antlers fairly. They, by the way, are a great boon to a warrior, for they hold the power of the deer.",
                                 "When ground and eaten <u> the vitality flows into you. Be very careful. If you're as fresh as you look you won't survive against a deer!");
    }

    // stage 9: spelunking (bring 1 mica from white rats — completable; rewards a blue potion)
    private static async Task Spelunking(NpcContext ctx)
    {
        if (ctx.HasItem("mica", 1))
        {
            ctx.TakeItem("mica", 1);
            ctx.SetStage(Stage, 10);
            ctx.GiveItem("blue_potion", 1);
            ctx.AwardExp(500);
            await ctx.SayItem("mica", "A Mica! Just what I needed.");
            await ctx.SayItem("blue_potion", "Take this, it is one of the potions I made. It will heal some of your wounds...");
            await ctx.Say("and remember this, you will find many greater secrets in other caves.",
                          "If you would like another quest, let me know, I have plenty to teach a young one like yourself.");
            return;
        }

        await ctx.Say("You are ready for something a bit more adventurous now!\nThus far, your travels have been limited to lands above ground, where the Sun keeps much evil away.",
                      "But many greater rewards - and challenges - await you beneath the ground.");
        int choice = await ctx.Menu("I am trying to brew some healing potions. Will you help me to gather the needed components?",
                                    new[] { "Yes. I am ready for greater things.", "No, not right now." });
        if (choice == 1)
        {
            await ctx.Say("Excellent. I have everything I need except for some mica.");
            await ctx.SayLook(90, 11, "There is a cave, not too far from here, where some white rats can occasionally be found.");
            await ctx.Say("You can find their home near a well, beneath a tall golden tree near the Dusk Shaman.");
            await ctx.SayLook(90, 11, "The rats have been corrupted by evil chi. They live not off food like you and I, but from eating the very rock that makes their home.");
            await ctx.SayItem("mica", "Sometimes, you will find that they possess mica, a mineral that can be found in the rock in this area.");
            await ctx.Say("Be careful! Many of the creatures that live below ground are much more dangerous than those you have met thus far.",
                          "Bring me one piece of mica so that I may make my potions.");
        }
        else if (choice == 2)
        {
            await ctx.Say("Perhaps another time, then.");
        }
    }

    // stage 10: horse riding. RTK main_tutorial_npc.lua:777 gates on `player.state == 3 and player.disguise
    // == 26` — mounted AND wearing the horse graphic. Here those are one flag (Character.Mounted drives the
    // 0x33 form byte), so ctx.Mounted is the whole check. Ride a real wild horse in with the 'r' key
    // (Session.TryRideHorse) and this clears.
    private static async Task HorseRiding(NpcContext ctx)
    {
        if (ctx.Mounted)
        {
            ctx.AwardExp(150);
            ctx.SetStage(Stage, 11);
            await ctx.SayLook(17, 3, "What a great steed you have there. Very impressive indeed, I love to watch horses.",
                                     "If you would like another quest, let me know, I have plenty to teach a young one like yourself.");
            return;
        }

        await ctx.Say("Nothing is better than a swift steed carrying you off to where you want to go. You should learn to ride now, as it will aid you greatly on your way to destiny.");
        await ctx.SayLook(17, 3, "You can't do much while mounted on a horse, but it is much faster than walking. Go find a horse and ride it back to me.");
        await ctx.Say("Talk to me again when you are mounted on a horse. Near the top left of the city is a good place to look for horses, there is usually a few there. Once you find one walk up to it and ride it by pressing the [r] key.");
    }

    // stage 11: find the missing brother. The brother IS Haguru (npc_dialog.lua HaguruNpc, ported from RTK
    // NPCs/arctic/haguru.lua) — he stands on Du Mountain, off the Northern Pass, and sets helped_haguru once
    // you have killed 3 mountain wolves for him. Note the tutor's directions below send you at Sanhae and the
    // Arctic; that is RTK's own wording and it is a red herring — Du Mountain is the FIRST turning off the
    // Northern Pass, before the Arctic gate, and Haguru is the only NPC on it.
    private static async Task FindBrother(NpcContext ctx)
    {
        if (ctx.Reg("helped_haguru") == 1)
        {
            ctx.AwardExp(500);
            ctx.SetReg("helped_haguru", 0);
            ctx.SetStage(Stage, 12);
            await ctx.Say("Oh thank you so much for finding my brother! It is such a burden off my mind. He is such a noble one trying to help that town like that.",
                          "If you would like another quest, let me know, I have plenty to teach a young one like yourself.");
            return;
        }

        await ctx.Say("Oh my... I have just received some bad news. My youngest brother has just gone missing from his home. Are you willing to do me a favor?");
        int choice = await ctx.Menu("Will you go look for him, and find out what has happened?",
                                    new[] { "No, I will not help.", "Yes, I will go look." });
        if (choice == 1)
        {
            await ctx.Say("Oh, what a shame. Sorry, but I can not continue your training until I know what has happened to him");
        }
        else if (choice == 2)
        {
            await ctx.Say("All I can tell you is that he lives in Sanhae, a town far to the north.",
                          "You can get there by going back to the stables, and traveling to the Arctic Land.",
                          "From there go south east, and follow the valley around. Talk to the Mayor there, he may be able to tell you what happened.");
        }
    }

    // stage 12: a better weapon. The trickster is Blood, in Blood's Home off KaMing's Encampment (npcs_say
    // .BloodNpc, ported from RTK NPCs/kaming/blood.lua): say "ice beast", pay his 100 gold, kill the Ice Beast
    // in Northeast Koguryo for its ice heart, and he forges the Frost sabre and grants the legend.
    private static async Task BetterWeapon(NpcContext ctx)
    {
        if (ctx.HasLegend("defeated_ice_beast"))
        {
            ctx.SetStage(Stage, 13);
            await ctx.Say("Well done, I see you have upgraded yourself to a better weapon. I hope it works out for you.",
                          "If you would like another quest, let me know, I have plenty to teach a young one like yourself.");
            return;
        }

        await ctx.Say("So you are still carrying that little stick around to beat on things with? I think you need to upgrade to something better!",
                      "Let's see... Ah yes! A good weapon, with great magical properties, and perhaps even a challenge too great for even you.",
                      "I have heard tales of a warrior who is a bit of a trickster in the KaMing's encampment. You can get there from the stables you used before.",
                      "Go there and talk to him about a new weapon. I am sure he will be willing to \"help\" you, ask about the \"Ice beast\"");
    }

    // stage 13: end of tutorial (make a student cap from cloth; completable via @item / the museum caretaker)
    private static async Task StudentCap(NpcContext ctx)
    {
        if (ctx.HasItem("student_cap", 1) || ctx.HasEquipped("student_cap"))
        {
            ctx.AwardExp(2000);
            ctx.SetStage(Stage, 14);
            ctx.SetReg("visited_yon_and_weaved", 0);
            await ctx.Say("I have taught you all that I can, young one. The time has now come for you to venture out into the Kingdoms and create your own legends.");
            return;
        }

        if (ctx.Reg("visited_yon_and_weaved") == 1 && ctx.HasItem("cloth", 1))
        {
            await ctx.Say("Ah, I see you have visited Yon.. how was she?",
                          "Now that you have the cloth, you must visit the museum Caretaker, whom resides in the museum north of Dae Shore. He is the only one who can make your Student Cap.");
            return;
        }

        await ctx.Say("Well, your time with me is nearly at an end, but before we part I want to give you a small gift to show your time here.");
        await ctx.SayItem("student_cap", "I will show you the way to prove your worth, by making your very own Student's cap!");
        await ctx.Say("This offers good protection during battle even though it is made of cloth.",
                      "The first step to making your cap is to get some cloth.\nGoto the center of the wilderness, you will find some sheep there.",
                      "Collect some of the wool they drop, and take it to the weavers hut. There are many skills you can learn later on, weaving is just one of them.",
                      "There is armor making, weapon smith in both wood and metal, gem crafting, and even cooking!",
                      "You will find the weavers near 45,30 in the Wilderness, that's out of the North gate of Kugnae. Ask about \"weaving\" when you get there.",
                      "It's quite a walk, so you might want to use a horse. Go now, and return when you have made some cloth.");
    }
}
