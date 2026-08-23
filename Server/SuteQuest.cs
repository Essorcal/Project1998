using Shared;

namespace Server;

/// <summary>
/// The Sute quest — Buya's level-28 dungeon chain, and the only way into Sute's Cave (maps 441-447).
/// Ported from RTK <c>NPCs/Common/mage_trainer.lua</c> (its <c>onSayClick</c> "sute" branch) plus the
/// cave-mouth tile trigger in <c>onScriptedTiles/onScriptedTilesQuest.lua</c> (see
/// <c>Session.TrySuteCaveMouth</c> in Session.Navigation.cs).
///
/// <para>The chain, in the order a player walks it:</para>
/// <list type="number">
/// <item>Go to the Buya Mage Guild (Eldritch Sanctum, map 367) and <b>say "sute"</b> near <b>Eldritch</b>.
/// He offers the whole tale or the short version, then the powder.</item>
/// <item>Pay <b>200 gold</b>. He dyes your worn armor (<see cref="DyeColor"/>) — this is the coating that
/// lets you past the seal. It replaces any war paint you were wearing, and can only be applied once an
/// hour (<see cref="RecoatSeconds"/>).</item>
/// <item>Walk to Buya's north edge — North Gate, then east — to the cave mouth at
/// (<see cref="MouthX"/>, <see cref="MouthY"/>). Coated, you walk straight in and the powder is spent;
/// uncoated, the tile shrugs you back south.</item>
/// <item>Fight through the seven rooms to <b>Sute's Nest</b> (map 442) and kill <b>Sute</b>, who always
/// drops <b>Sute's key</b> (MobDrops.csv, 100%).</item>
/// <item>Return to Eldritch and <b>say "sute"</b> again. He takes the key and marks you
/// "<b>Slew the mighty Sute</b>" — and warns you it did not take.</item>
/// </list>
///
/// <para><b>Sources.</b> Primary is tswolf.com/quests/sute (Wayback 2001-08-02), whose page is a strip of
/// per-line dialog screenshots — every line below is transcribed from those GIFs, ORIGINAL TYPOS INTACT
/// ("lauched", "dangeous"), because they are what the 4.95-era client actually printed. RTK silently
/// corrected both and corrupted several other lines with stray letters ("havve", "Sutee", "frozeeen"), so
/// where the two differ the screenshot wins. Only one page of the tale (the silver-mining line, between
/// "…army of bizarre creations" and "Several groups of heroes…") was never archived; it is RTK's text,
/// which matches the screenshots verbatim everywhere it can be checked. Secondary is nexusatlas.com
/// /quests/sute.php and its 5.0 atlas cave pages, used for the level gate, the reward list, and the dye's
/// period name ("the old Super Wasabi dye color").</para>
///
/// <para><b>Two deliberate divergences from RTK:</b></para>
/// <list type="bullet">
/// <item><b>The re-coat cooldown is one hour, not one day.</b> RTK's code waits 86,400s while its own
/// dialog says "more than once per hour" — the dialog is right. Both period sources agree with the
/// dialog: the tswolf screenshot reads "it is dangeous to apply the powder more than once per hour", and
/// its walkthrough warns "be careful not to fall out, or you'll have to wait an hour and pay again for
/// the dye". (Nexus Atlas, years later, says "once a day (a Nexus day is 3 hours in real time)" — a real
/// third value, but it is the later-era site and it contradicts the NPC's own words, so it loses.)</item>
/// <item><b>No experience reward.</b> RTK pays 50,000 exp on turn-in. Neither period source mentions any:
/// tswolf ends "He'll congratulate you and give you a cool legend mark!", and Atlas's structured
/// "Rewards" field lists only "A new legend mark" — that field DOES itemize exp when a quest grants it
/// (the Ice Beast page reads "- 2300 experience points"), so its silence here is evidence, not an
/// omission. The experience in this content is in the mobs; Sute himself is worth 40,000.</item>
/// </list>
///
/// <para><b>Repeatable by design.</b> RTK deliberately comments out its own "you already helped me" early
/// return, and that is kept: once you hold the legend the turn-in branch stops firing, so a second run
/// leaves the key in your pack instead of eating it. That is the right behaviour — Sute's key is a
/// component of the Mage Moon armor quest (mage_trainer.lua asks for it alongside eight elemental keys),
/// and killing Sute is a step of the Poet Sun armor quest. Neither of those chains is ported yet, but this
/// one must not consume their ingredient.</para>
///
/// <para>State is the two RTK registry keys plus the legend, so nothing new is persisted.</para>
/// </summary>
public static class SuteQuest
{
    /// <summary>RTK <c>registry["sute_quest_dye"]</c> — 1 while the powder is on and unspent.</summary>
    public const string DyeReg = "sute_quest_dye";
    /// <summary>RTK <c>registry["sute_quest_timer"]</c> — unix seconds; the earliest next coating.</summary>
    public const string TimerReg = "sute_quest_timer";

    public const string Legend   = "slew_mighty_sute";
    public const string KeyItem  = "sutes_key";

    public const int  MinLevel      = 28;      // Atlas: "Level Required: 28"; the cave rooms are ReqLvl 28 too
    public const uint PowderCost    = 200;     // "It will cost 200 gold for the powder."
    public const long RecoatSeconds = 3600;    // "more than once per hour" — see the divergence note above

    /// <summary>The powder's colour, on the shared armor-dye palette (RTK <c>player.armorColor = 26</c>).
    /// Atlas names it in the dungeon's launch notice: the cave "makes the old 'Super Wasabi' dye color
    /// available to many". It goes through <see cref="Content.DyeRampFor"/> like any war paint, so the byte
    /// on the wire depends on which body sprite the armor is (ArmorDyeRamps.csv).</summary>
    public const byte DyeColor = 26;

    // The legend mark itself: "Slew the mighty Sute (Yuri 33, Summer)" in the archived screenshot, which is
    // exactly Character.GameDate's format. Icon/colour are RTK's — the screenshot shows a small brown
    // hatchet glyph but nothing in either source names the index.
    public const byte LegendIcon = 5, LegendColor = 16;

    /// <summary>Eldritch of the Eldritch Sanctum (NPCs.csv 39, map 367) — the Buya Mage Guild master, and
    /// the only NPC who runs this quest.
    ///
    /// <para>This gate is a DELIBERATE narrowing of RTK. Its quest lives in <c>mage_trainer.lua</c>, which is
    /// the script for the <c>MageTrainerNpc</c> identifier, and nine NPCs share that identifier: Eldritch,
    /// <b>Haedu</b> (the Kugnae guild master), <b>Wand</b>, and six subpath masters. RTK therefore lets you
    /// say "sute" to any of them and hear Eldritch's own words in the first person — "Eldritch's face looks
    /// grim", "I sealed Sute and his creations in the cave" — out of a stranger's mouth in the wrong kingdom.
    /// Both period sources name one place and one man: tswolf's instruction is "Go to the Buya Mage Guild …
    /// say 'Sute' near the merchant", and Atlas's is "visit Buya Mage Guild and say the name 'Sute' to the
    /// Guild Master". So the ability is still composed onto the shared identifier (there is no per-NPC
    /// composition key), and the narrowing happens here.</para></summary>
    public const int GuildMasterNpcId = 39;

    // ---- cave-mouth geometry (onScriptedTilesQuest.lua) ------------------------------------------
    /// <summary>Buya. The mouth is the pair of tiles on the north edge — tswolf: "Go to North Gate, then
    /// East, until you see a blue Cave."</summary>
    public const ushort BuyaMap = 330;
    public static readonly int[] MouthX = { 103, 104 };
    public const int MouthY = 22;
    /// <summary>Where the tile puts you back when you are not coated (RTK <c>warp(m, x, y + 1)</c>) — the
    /// row the cave's own exit warps (Warps.csv 1425/1426) drop you on, so a refusal leaves you exactly
    /// where walking out would have.</summary>
    public const int MouthPushToY = MouthY + 1;

    /// <summary>Sute's Welcome, the first room. You land on a random one of two tiles just inside.</summary>
    public const ushort WelcomeMap = 446;
    public const int LandX0 = 10, LandX1 = 11, LandY = 22;
}

/// <summary>
/// Eldritch's half of the quest: the tale, the powder, and the turn-in. Speech-only — RTK hangs the whole
/// thing off <c>onSayClick</c>, and tswolf's instruction is "Go inside and say 'Sute'", so it adds no menu
/// entry and clicking him shows the ordinary mage-trainer menu.
///
/// <para>Composed onto <c>MageTrainerNpc</c> (NpcAbilities.csv) because that is the only handle the
/// composition table offers, but it answers for exactly one NPC — see
/// <see cref="SuteQuest.GuildMasterNpcId"/> for why the other eight mage trainers must not.</para>
/// </summary>
public sealed class SuteQuestAbility : INpcAbility, INpcSayHandler
{
    public static readonly SuteQuestAbility Instance = new();

    /// <summary>No click entry — the quest is heard, not read off a menu.</summary>
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx) => NoClickMenu.None;

    /// <summary>Whether this NPC is the one who runs the quest. Every mage trainer carries the ability
    /// (the composition table is keyed by identifier, and nine NPCs share this one), so this is what keeps
    /// Eldritch's first-person story in Eldritch's mouth — see <see cref="SuteQuest.GuildMasterNpcId"/>.</summary>
    public static bool AnswersFor(NpcDef def) => def.Id == SuteQuest.GuildMasterNpcId;

    // Eldritch's tale, transcribed from the tswolf screenshots (sute2..sute15). The typos are the client's.
    private static readonly string[] Tale =
    {
        "\"Ah, Sute...\" the mage sighs sadly. \"Sute was once a great pupil of mine. He had an incredible talent for magic. But he also had too much confidence, too much pride.\"",
        "\"About two decades ago, the Northern Ogres lauched a massive surprise assault against Buya. They used elaborate tactics and were supported by strange magics.\"",
        "Eldritch gazes upward, recalling the old memories. \"The very gates of Buya fell and we were forced to withdraw into the palace. After the initial attack, we were able to survive, but could not overcome the Ogres.\"",
        "\"We know the Ogre must have been united by some more cunning power to attack us so effectively. We suspected it was a corrupt mage of some sort, but never learned the truth.\"",
        "\"As we were developing plans to overcome this threat, the impatient Sute, who had just earned his Ancient clothes, headed alone to the Arctic Land. Before we realized he had left, the Ogres mysteriously retreated.\"",
        "\"We assumed Sute dead and were amazed that he somehow was successful. Two years after, what was once Sute returned. His body was frozen and he babbled incoherently.\"",
        "\"A poet, Lintong, tried to heal him, but Sute smote him with a powerful ice spell.\"",
        "\"As we tried to subdue him, Sute flew into an insane rage and fled to that cave on the north side of Buya, though we did not know where he had gone to at the time. He formed a virtual army of bizarre creations.\"",
        // The one page tswolf never captured (sute10) — RTK's text, requoted to match the pages around it.
        "\"Strangely, he did not use them to attack, but to mine the cave for silver, which he reportedly hoarded. But we were worried about Sute's future plans.\"",
        "\"Several groups of heroes were sent into the cave to put Sute out of his misery, but all failed.\"",
        "\"Many died. Some were even able to defeat Sute, but his body would later rise again. There was no other choice,\" Eldritch says with regret. \"I sealed Sute and his creations in the cave.\"",
    };

    // The last three pages — the offer itself. Both branches of the opening menu end here, so "just tell me
    // what must be done" is the tale minus its history.
    private static readonly string[] Offer =
    {
        "\"Some incredibly evil force has polluted Sute's soul. I doubt that you will be able to finally put him to rest, but, if you are brave enough, I will help you try.\"",
        "\"I can coat you with a special powder that will allow you to enter Sute's cave. I have one batch of powder available, but it will only let you into the cave once.\"",
        "\"If you leave the cave, you will have to be recoated and it is dangeous to apply the powder more than once per hour. It will cost 200 gold for the powder.\"",
    };

    public async Task<bool> OnSay(NpcContext ctx, string speech)
    {
        if (speech != "sute") return false;
        // Not Eldritch -> not his story to tell; the word falls through to ordinary chat.
        if (!AnswersFor(ctx.Def)) return false;
        if (ctx.KarmaTooLow()) return true;              // RTK Tools.checkKarma

        // Turn-in first, exactly as the Lua orders it: the key in hand and no mark yet ends the quest, and
        // clears the cooldown so "Sute will soon be reborn" is an invitation rather than a tease.
        if (ctx.CountItem(SuteQuest.KeyItem) > 0 && !ctx.HasLegend(SuteQuest.Legend))
        {
            ctx.TakeItem(SuteQuest.KeyItem, 1);
            ctx.AddLegend($"Slew the mighty Sute ({Character.GameDate})", SuteQuest.Legend,
                          SuteQuest.LegendIcon, SuteQuest.LegendColor);
            ctx.SetReg(SuteQuest.DyeReg, 0);
            ctx.SetReg(SuteQuest.TimerReg, 0);
            await ctx.Say("You have done well and all will know of your efforts. Unfortunately, I have learned that his spirit is not yet at rest. Sute will soon be reborn.");
            return true;
        }

        if (ctx.NowUnix < ctx.Reg(SuteQuest.TimerReg))
        {
            await ctx.Say("Not enough time has passed. If I apply more powder now, it will kill you. Return later.");
            return true;
        }

        if (ctx.Level < SuteQuest.MinLevel)
        {
            await ctx.Say("Eldritch's face looks grim. 'You are still too young to learn of that.'");
            return true;
        }

        int choice = await ctx.Menu("Yes, I can tell you about Sute.  Do you wish to hear the whole story?",
                                    new[] { "Please enlighten me.", "No, just tell me what must be done." });
        if (choice == 1) await ctx.Say(Tale.Concat(Offer).ToArray());
        else if (choice == 2) await ctx.Say(Offer);
        else return true;                                 // closed the box — no sale, no cooldown

        int pay = await ctx.Menu("Do you want me to apply it to you?",
                                 new[] { "Yes, I am willing to pay.", "No thank you." });
        if (pay != 1) return true;

        if (ctx.Coins < SuteQuest.PowderCost)
        {
            await ctx.Say("Sorry but you do not have enough gold.");
            return true;
        }
        // The powder IS the armor dye, so there has to be something to dye (RTK getEquippedItem(EQ_ARMOR)).
        if (!ctx.HasVisibleArmor)
        {
            await ctx.Say("You must be wearing an armor for me to dye you.");
            return true;
        }

        ctx.SpendGold(SuteQuest.PowderCost);
        ctx.SetReg(SuteQuest.DyeReg, 1);
        ctx.SetReg(SuteQuest.TimerReg, (int)(ctx.NowUnix + SuteQuest.RecoatSeconds));
        ctx.SetArmorColor(SuteQuest.DyeColor);            // persists + redraws self and peers
        await ctx.Say("The powder turns your clothing a strange color.",
                      "If you manage to kill Sute, return to me and I will see that your efforts are acknowledged.");
        return true;
    }
}
