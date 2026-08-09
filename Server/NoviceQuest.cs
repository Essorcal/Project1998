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
/// The DIALOG below is transcribed from the surviving screenshots on tswolf.com/newb/quest.shtml and
/// quest2.shtml (archived 2001-06-27 / 2001-07-23; saved under scraped_nexus_data artifacts/tswolf/
/// newb_questpics/). It is the real in-game text, reworked only where the area's staging doesn't survive
/// consolidation: the area split these across a weapon smith, his brother the armorer, a third brother, and
/// the brother's sister Mignok who actually taught the spell. One tutor now speaks all of it, and "continue
/// down this path" / "this part of the woods" / "in the hut beside me is my sister" become real-world
/// directions the way BuyArmor and SellMeat already do it. Quest 1's OFFER screenshots are missing from the
/// archive (only its completion survives), so that one prompt is written to match the surrounding voice.
/// The area's coordinate lesson (quest 3) is dropped — it taught navigation of the zone itself.
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

    private const int RabbitsWanted   = 10;
    private const int SquirrelsWanted = 10;
    private const uint StageExp       = 50;

    public static readonly QuestDef Def = new()
    {
        Key  = Key,
        Name = "Take my first steps",
        Talk = Run,
    };

    private static async Task Run(NpcContext ctx)
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

    // stage 0 -> 1: hand over the wooden saber (a gift, not a task) and send them at the rabbits.
    // Quest 1's offer screenshots didn't survive; this prompt is written to match the voice of the rest.
    private static async Task Intro(NpcContext ctx)
    {
        ctx.GiveItem("wooden_saber", 1);
        ctx.SetReg(RabbitSnap, ctx.KillCount("rabbit"));
        ctx.SetStage(Key, 1);

        await ctx.Say("Welcome, child. You have come into this world with nothing but your name, and a name " +
                      "will not keep the beasts off you.");
        await ctx.SayItem("wooden_saber", "Take this saber. It is old and it is plain, but it holds an edge, " +
                                          "and every legend in these kingdoms began with worse.");
        await ctx.Say("Press <i> to see it in your inventory, then type <u> and the letter beside it to wield it.");
        await ctx.SayLook(125, 11, "There are rabbits in the fields beyond the gates. Go now, and slay ten of " +
                                   "them, then return to me. Your first hunt is the smallest step, but it is the first.");
    }

    // stage 1 -> 2: slay 10 rabbits. Completion text is verbatim from quest1congrats.gif.
    private static async Task SlayRabbits(NpcContext ctx)
    {
        int slain = ctx.KillCount("rabbit") - ctx.Reg(RabbitSnap);
        if (slain >= RabbitsWanted)
        {
            ctx.AwardExp(StageExp);
            ctx.SetReg(SquirrelSnap, ctx.KillCount("squirrel"));
            ctx.SetReg(GaveGarb, 0);
            ctx.SetStage(Key, 2);
            await ctx.Say("Congratulations! Your first hunt is a success! When you get to the town make sure to " +
                          "seek out a blacksmith to keep your weapons in top condition, as you use them they can " +
                          "grow dull and may break.");
            await ctx.Say("Now come back to me, and I will tell you a little of another trade - Armor!");
            return;
        }

        await ctx.SayLook(125, 11, $"You have slain {slain} of the ten rabbits. Back to the fields with you.");
    }

    // stage 2 -> 3: hand over the spring garb, THEN send them at the squirrels (as the area did — the armor
    // was given before the hunt it protects you through). Dialog verbatim from quest201-207 / quest2congrats*.
    private static async Task SlaySquirrels(NpcContext ctx)
    {
        int slain = ctx.KillCount("squirrel") - ctx.Reg(SquirrelSnap);
        if (ctx.Reg(GaveGarb) == 1 && slain >= SquirrelsWanted)
        {
            ctx.AwardExp(StageExp);
            ctx.SetStage(Key, 3);
            await ctx.Say("Well, that was fast! You are well on your way to being a truly mighty fighter. " +
                          "Remember to keep your Armor well maintained like your weapons.");
            await ctx.Say("As you grow stronger, and gain more insight, you will be able to use better armor and " +
                          "weapons. These can be gained from creatures you kill or bought from players and shops.");
            await ctx.Say("You seem to be learning much, I can sense your mind expanding in leaps and bounds. " +
                          "But are you ready for the greatest test of mental power?");
            return;
        }

        if (ctx.Reg(GaveGarb) == 1)
        {
            await ctx.SayLook(25, 9, $"You have slain {slain} of the ten Squirrels. Keep at it — and mind the acorns.");
            return;
        }

        // RTK's own armor stage picks the sex-appropriate item; the garb does the same ("tailored to fit your gender").
        string garb = ctx.Sex == 1 ? "spring_dress" : "spring_garb";
        ctx.GiveItem(garb, 1);
        ctx.SetReg(GaveGarb, 1);

        await ctx.Say("Whoa there mighty fighter. Where are you off to in such a hurry? I see you have already " +
                      "armed yourself well for a hunt, but your still in your rags.");
        await ctx.SayItem(garb, "Take this, like all armor in this kingdom it is tailored to fit your gender. " +
                                "It is Spring in quality, and represents the spring of your adventure.");
        await ctx.Say("As with weapons, you can press <i> to see it in your inventory. Then type <u> and the " +
                      "letter next to the armor to wear it.");
        await ctx.Say("Armor helps protect you from attacks, and reduces the damage you take. If you look to the " +
                      "bottom right of your screen you will see a red bar, that is your vitality, your health.");
        await ctx.Say("Watch it carefully as you hunt, for should it fall to low your body will become but a " +
                      "spirit, and some items can drop to the floor or even break from the extra damage of death!");
        await ctx.SayLook(25, 9, "The woods are filled with Squirrels. Unlike the rabbits from before these have " +
                                 "far sharper teeth. Your new armor will help protect you from their bite.");
        await ctx.SayLook(25, 9, "Go now, kill ten Squirrels. As you kill them you will see that they drop " +
                                 "Acorns. Stand above the acorns and press <,> to pick them up.");
    }

    // stage 3 -> Done: 5 acorns + 5 rabbit meat, learn Soothe. Dialog verbatim from quest402/405/407 and
    // quest4congrats*, with Mignok's lines spoken by the tutor since there is no hut to send them to.
    private static async Task LearnSoothe(NpcContext ctx)
    {
        if (ctx.HasItem("acorn", 5) && ctx.HasItem("rabbit_meat", 5))
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
            await ctx.Say("Ah you look eager to use your new spell. To see the list of spells you have press the " +
                          "'+' key on the keypad on the right of your keyboard.");
            await ctx.Say("Try your spells now, and when you're ready you should continue with your travels.");
            await ctx.Say("Ask me of your training when you are ready, and we shall begin in earnest.");
            return;
        }

        await ctx.Say("Magic, and its mastery, is the greatest challenge of the mind. Are you ready to face the " +
                      "challenge? Depending on which path you follow later in your life ((level 5)) you will " +
                      "learn different secrets.");
        await ctx.Say("All classes learn various spells. Some are common to all, while others are unique to the " +
                      "path they choose.");
        await ctx.Say("When you click on your Path's tutor, they will have a button that says 'Learn Secret' and " +
                      "it is from there that you will be able to see what spells can be learned.");
        await ctx.SayItem("acorn", "Every spell will usually have a price associated with it. For instance, I " +
                                   "will be happy to teach you the spell \"Soothe\" - a healing spell - but you " +
                                   "will have to bring me 5 acorns and 5 rabbit meats.");
    }
}
