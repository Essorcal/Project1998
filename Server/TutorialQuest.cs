namespace Server;

/// <summary>
/// The peasant tutorial chain (RTK <c>Accepted/NPCs/tutorial/main_tutorial_npc.lua</c>, the shared
/// Jadespear #49 / Ironheart #20 script), driving <c>quest["tutorial_quest"]</c> through 14 stages. Ported
/// faithfully: each stage's dialog, item/creature portraits, gates and rewards match the Lua.
///
/// Reality note — stages gate on world content this server doesn't have yet (a fishing NPC that grants the
/// <c>learned_to_fish</c> legend, Chu Rua's shore on map 1111, the mount system, the missing-brother and
/// ice-beast NPCs). Those stages show the real dialog but can't be COMPLETED until that content exists — which
/// is exactly "port as far as current content allows." The item-turn-in stages (armor, meat, rose+chestnut,
/// ogre cider, antlers, mica, student cap) are completable now via shops or <c>@item</c>. The separate
/// path-choice and warrior-armor branches of the RTK script (which need the class/guild systems) are out of scope.
/// </summary>
public static class TutorialQuest
{
    // sub-flags (RTK player.quest[...] booleans), kept in the int registry alongside the main stage.
    private const string Stage      = "tutorial_quest";
    private const string GaveGold   = "tutorial_quest1_gave_gold";
    private const string GaveMeat   = "tutorial_quest2_gave_meat";

    public static readonly QuestDef Def = new()
    {
        Key  = Stage,
        Name = "Continue my training",
        Talk = Run,
    };

    private static async Task Run(NpcContext ctx)
    {
        int stage = ctx.Stage(Stage);
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

    // stage 0 -> 1
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
    // FishAbility; while on this stage the catch is guaranteed so the tutorial doesn't grind the 10% roll).
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

    // stage 7: By the Sea — Chu Rua (needs the aided_chu_rua legend from map 1111, which isn't renderable here,
    // so the warp is declined gracefully and the gate can't clear yet).
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

    // stage 10: horse riding (needs the mount system — state 3 + horse disguise — which isn't in yet).
    private static async Task HorseRiding(NpcContext ctx)
    {
        // We don't model mounts, so the mounted check can't pass yet; the offer dialog is faithful.
        await ctx.Say("Nothing is better than a swift steed carrying you off to where you want to go. You should learn to ride now, as it will aid you greatly on your way to destiny.");
        await ctx.SayLook(17, 3, "You can't do much while mounted on a horse, but it is much faster than walking. Go find a horse and ride it back to me.");
        await ctx.Say("Talk to me again when you are mounted on a horse. Near the top left of the city is a good place to look for horses, there is usually a few there. Once you find one walk up to it and ride it by pressing the [r] key.");
    }

    // stage 11: find the missing brother (needs the helped_haguru legend from Sanhae — not in yet).
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

    // stage 12: a better weapon (needs the defeated_ice_beast legend from KaMing's trickster — not in yet).
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
