namespace Server;

/// <summary>
/// The first-steps chain that runs BEFORE <see cref="TutorialQuest"/> on the same tutors (Jadespear #49 /
/// Ironheart #20), driving <c>quest["novice_quest"]</c> through stages 0..3.
///
/// Reconstruction note — this chain is NOT in the RTK Lua. Originally players went straight into the tutor
/// line and picked up their first weapon, garb and spell along the way; when Nexon added the separate
/// tutorial AREA (~Oct 2000) those beats moved into that zone, and the NPC-delivered versions were never
/// recorded. The one contemporary report of Jadespear's line says the content was the same ("he will give
/// you the quests, one by one. All the quests are the same" — TSWolf, 2001-03-19), so this is the area
/// chain folded back onto a single tutor.
///
/// ERA — because those beats MOVED rather than being retired, this chain and the area are mutually
/// exclusive: <see cref="Era.TutorNoviceChain"/> retires on 2000-10-06, the day
/// <see cref="Era.NewbieArea"/> arrives, and <see cref="TutorialQuest"/> only dispatches here while it is
/// live. The area's own versions of these four beats — with the four separate speakers whose "continue
/// down this path" framing this file had to rewrite away — are transcribed from the surviving screenshots
/// in <c>game-data/npc_dialog.lua</c> (WoodlandSmithNpc, WoodlandArmorerNpc, TutorialNpc1/2,
/// MignokNpc). Note the area asks for TEN rabbits and TEN squirrels where this asks for five: the tswolf
/// guide states ten, and the five here is this file's own exp-budget tuning, described below.
/// See docs/common/Era-Gating.md.
///
/// The MECHANICAL beats and the item/spell prices come from the surviving screenshots on
/// tswolf.com/newb/quest.shtml and quest2.shtml (archived 2001-06-27 / 2001-07-23; saved under
/// scraped_nexus_data artifacts/tswolf/newb_questpics/): wield with u, inventory with i, the vitality bar
/// and death penalty, acorns picked up with ',', the spell list on '+', and Soothe's price of 5 acorns and
/// 5 rabbit meats.
///
/// The WORDING is rewritten rather than transcribed. The screenshots' dialog was staged across four NPCs in
/// a zone the player walked through — a weapon smith, his brother the armorer, a third brother, and the
/// sister who taught the spell — so it is full of framing that is false here: "continue down this path",
/// "this part of the woods", "in the hut beside me is my sister", "when you get to the town" (we are in the
/// town), and a hand-off to "another trade - Armor!" that only parsed because it was a different brother.
/// One tutor, standing in one home, says all of it instead: the creatures are just outside his door, and
/// each stage closes with the same "if you would like another quest" line the RTK chain already uses.
///
/// Item sources are archive-attested (tswolf weapons/armor tables, Jan 2001): the wooden saber and novice
/// sword were both free, the spring garb the cheapest peasant armor. The saber is given outright in
/// <see cref="Intro"/> and the garb on the squirrel offer — matching the area, where you were handed the
/// armor BEFORE the hunt it was meant to protect you through, and matching how SellMeat hands you the meat
/// with the task. The novice sword is NOT here; it goes out at <see cref="TutorialQuest"/> stage 8, the deer
/// hunt, which is the first stage that actually needs a weapon.
///
/// Exp is deliberately junior to every TutorialQuest stage (smallest of those is 50, most are 100-150): 50
/// per stage here, plus the 150 the kills themselves pay (10 rabbits at 5, 10 squirrels at 10), lands a
/// fresh character at exactly level 2 (300) as they learn their first spell. Levels 1-5 cost the same on
/// every path, so this needs no per-path tuning.
/// </summary>
public static class NoviceQuest
{
    /// <summary>Quest key, also the progress registry key.</summary>
    public const string Key = "novice_quest";
    /// <summary>Stage at which the chain is finished and <see cref="TutorialQuest"/> may begin.</summary>
    public const int Done = 4;

    // Lifetime kill counts are compared against a snapshot taken when the stage is offered, so kills made
    // before accepting don't count (same pattern as MinorQuest's KKillPfx).
    private const string RabbitSnap   = "novice_quest1_rabbit_snapshot";
    private const string SquirrelSnap = "novice_quest2_squirrel_snapshot";
    private const string GaveGarb     = "novice_quest2_gave_garb";
    // Stages 1-2 send the player at the very creatures that drop acorns and rabbit meat, so by the time they
    // reach stage 3 they usually ALREADY hold the price. Without this flag the turn-in fires on the first
    // click and the offer is never seen. Same guard the RTK stages use with GaveGold / GaveMeat.
    private const string AskedSoothe  = "novice_quest3_asked_soothe";

    private const int RabbitsWanted   = 5;
    private const int SquirrelsWanted = 5;
    private const uint StageExp       = 50;

    // The RTK chain's own sign-off, reused so the two chains read as one conversation.
    private const string AnotherQuest =
        "If you would like another quest, let me know, I have plenty to teach a young one like yourself.";

    // No QuestDef of its own: QuestAbility renders one menu entry per registered quest, and these two chains
    // are sequential rather than alternatives. TutorialQuest owns the single entry and delegates here until
    // the chain reaches <see cref="Done"/>.
    internal static async Task Run(NpcContext ctx)
    {
        switch (ctx.Stage(Key))
        {
            case 0:  await Intro(ctx);          break;
            case 1:  await SlayRabbits(ctx);    break;
            case 2:  await SlaySquirrels(ctx);  break;
            case 3:  await LearnSoothe(ctx);    break;
            default: await ctx.Say("Try your spells now, and when you're ready you should continue with your travels."); break;
        }
    }

    // stage 0 -> 1: hand over the wooden saber (a gift, not a task) and send them at the rabbits outside.
    private static async Task Intro(NpcContext ctx)
    {
        ctx.GiveItem("wooden_saber", 1);
        ctx.SetReg(RabbitSnap, ctx.KillCount("rabbit"));
        ctx.SetStage(Key, 1);

        await ctx.Say("You have come into this world with nothing but your name, and a name will not " +
                      "keep the beasts off you.");
        await ctx.SayItem("wooden_saber", "Take this saber. It is old and it is plain, but it holds an edge.");
        await ctx.Say("Press <i> to see it in your inventory, then type <u> and the letter beside it to wield it.");
        await ctx.SayLook(125, 11, "There are rabbits just outside my door. Slay five of them, and return to me " +
                                   "when it is done.");
    }

    // stage 1 -> 2: slay 10 rabbits.
    private static async Task SlayRabbits(NpcContext ctx)
    {
        int slain = ctx.KillCount("rabbit") - ctx.Reg(RabbitSnap);
        if (slain >= RabbitsWanted)
        {
            ctx.AwardExp(StageExp);
            ctx.SetReg(SquirrelSnap, ctx.KillCount("squirrel"));
            ctx.SetReg(GaveGarb, 0);
            ctx.SetStage(Key, 2);
            await ctx.Say("Your first hunt is a success! Mind that a blade grows dull with use, and may break — " +
                          "the blacksmith here in the city will keep it in good condition for you.");
            await ctx.Say(AnotherQuest);
            return;
        }

        await ctx.SayLook(125, 11, "You have not yet slain the five rabbits. Return to me when it is done.");
    }

    // stage 2 -> 3: hand over the spring garb, THEN send them at the squirrels (as the area did — the armor
    // was given before the hunt it protects you through).
    private static async Task SlaySquirrels(NpcContext ctx)
    {
        int slain = ctx.KillCount("squirrel") - ctx.Reg(SquirrelSnap);
        if (ctx.Reg(GaveGarb) == 1 && slain >= SquirrelsWanted)
        {
            ctx.AwardExp(StageExp);
            ctx.SetReg(AskedSoothe, 0);
            ctx.SetStage(Key, 3);
            await ctx.Say("Well, that was fast! You are well on your way to being a truly mighty fighter. " +
                          "Remember to keep your armor well maintained, as you do your weapon.");
            await ctx.Say("As you grow stronger, and gain more insight, you will be able to use better armor and " +
                          "weapons. These can be gained from creatures you kill or bought from players and shops.");
            await ctx.Say(AnotherQuest);
            return;
        }

        if (ctx.Reg(GaveGarb) == 1)
        {
            await ctx.SayLook(25, 9, "You have not yet slain the five squirrels. Return to me when it is done.");
            return;
        }

        // RTK's own armor stage picks the sex-appropriate item; the garb does the same ("tailored to fit your gender").
        string garb = ctx.Sex == 1 ? "spring_dress" : "spring_garb";
        ctx.GiveItem(garb, 1);
        ctx.SetReg(GaveGarb, 1);

        await ctx.Say("You have armed yourself well, but you are still in your rags.");
        await ctx.SayItem(garb, "Take this, like all armor in this kingdom it is tailored to fit your gender. " +
                                "It is Spring in quality, and represents the spring of your adventure.");
        await ctx.Say("As with your weapon, press <i> to see it in your inventory, then type <u> and the letter " +
                      "beside it to wear it.");
        await ctx.Say("Armor helps protect you from attacks, and reduces the damage you take. If you look to the " +
                      "bottom right of your screen you will see a red bar — that is your vitality, your health.");
        await ctx.Say("Watch it carefully as you hunt, for should it fall too low your body will become but a " +
                      "spirit, and some items can drop to the floor or even break from the extra damage of death!");
        await ctx.SayLook(25, 9, "There are squirrels about my home as well. Unlike the rabbits they have far " +
                                 "sharper teeth, but your new armor will turn their bite.");
        await ctx.SayLook(25, 9, "Slay five of them, and return to me. As you kill them you will see that they " +
                                 "drop acorns — stand above one and press <,> to pick it up. Keep them.");
    }

    // stage 3 -> Done: 5 acorns + 5 rabbit meat, learn Soothe.
    private static async Task LearnSoothe(NpcContext ctx)
    {
        if (ctx.Reg(AskedSoothe) == 1 && ctx.HasItem("acorn", 5) && ctx.HasItem("rabbit_meat", 5))
        {
            var soothe = Content.SpellByKey("soothe");
            if (soothe is null || !ctx.LearnSpell(soothe))
            {
                await ctx.Say("Your mind cannot hold any more secrets right now.");
                return;
            }

            ctx.TakeItem("acorn", 5);
            ctx.TakeItem("rabbit_meat", 5);
            ctx.AwardExp(StageExp);
            ctx.SetStage(Key, Done);

            await ctx.Say("Thank you for the items! Now here is your spell, Soothe.");
            await ctx.Say("To see the list of spells you have, press the '+' key on the keypad on the right of " +
                          "your keyboard. Try it now, and when you are ready we shall begin your training in earnest.");
            await ctx.Say(AnotherQuest);
            return;
        }

        if (ctx.Reg(AskedSoothe) == 1)
        {
            await ctx.SayItem("acorn", "I am still waiting on 5 acorns and 5 rabbit meats. Bring them to me and " +
                                       "the secret is yours.");
            return;
        }

        ctx.SetReg(AskedSoothe, 1);

        await ctx.Say("You have a blade and you have armor, but steel is the crudest answer to the world. " +
                      "Magic, and its mastery, is the greatest challenge of the mind.");
        await ctx.Say("Some secrets are common to all, while others are unique to the path you choose later in " +
                      "your life ((level 5)). When you click on your Path's tutor, they will have a button that " +
                      "says 'Learn Secret', and it is from there that you will see what spells can be learned.");
        await ctx.SayItem("acorn", "Every spell will usually have a price. I will be happy to teach you the " +
                                   "spell \"Soothe\" - a healing spell - but you will have to bring me 5 acorns " +
                                   "and 5 rabbit meats.");
    }
}
