namespace Server;

/// <summary>
/// The Rogue's <b>White Moon Axe</b> — "Destruction of the Rogue Oppression". The only source of the axe
/// (Items.csv 47002), which Rogue Moon armor then asks to see and bonds (<see cref="ArmorQuest"/>).
///
/// <para><b>Sources.</b> The structure is tswolf.com/quests/wma/index.shtml (Wayback 2001-01-29), the only
/// period page: Rogue path, level 70, "Say 'Moon' to … the Guildmaster of the BUYA Rogue Guild".</para>
/// <list type="number">
/// <item>Say Moon: he tells of the White Moon, then asks to see a <b>Whisper bracelet</b>. It is only looked
/// at, never taken; worn is fine, but it must be at 100% (<see cref="NpcContext.CountReady"/>).</item>
/// <item>Say Moon: slay <b>5 Pale Scorpions</b> (Kugnae Spider Cave).</item>
/// <item>Say Moon: slay <b>Skeleton Ju</b>, the one red skeleton in the Kugnae or Buya Haunted House
/// basement.</item>
/// <item>Say Moon: pay <b>20,000 coins</b> for the axe.</item>
/// </list>
/// <para>tswolf says of both kill steps "be sure these are the last creatures you kill before returning". The
/// armor chains read the same warning as a kill delta counted from the ask, not "nothing else", so this does
/// too.</para>
///
/// <para><b>The axe is NOT bonded.</b> tswolf's item box: "Bonded when used in Rogue Moon Quest / Non Bonded
/// when Aquired in WMA Buya Quest". So the Items.csv row is no longer in <c>BondedItemIds</c>, and Rogue Moon
/// stamps the bond itself. Nothing says the quest is once only, and a loose axe is one you can sell, so the
/// quest can be repeated, the way RTK resets it to 0.</para>
///
/// <para><b>Prose</b> is RTK's (<c>NPCs/Common/rogue_guild_shaman.lua</c>), since no capture survives. One
/// substitution: RTK asks for a Lucky coin where every period source has the Whisper bracelet. The later
/// sources move the quest to Shaman Onyx of the Rogue Guild (nexusatlas, RTK) and bond the axe, and the
/// Rogue tutor's guide names "Elder Maso". tswolf is the oldest and is what this follows.</para>
///
/// <para><b>"Moon" at Maso.</b> Maso is also a Buya guildmaster for the armor chains, and both quests listen
/// for "moon". A rogue of level 70 or more gets this quest. That covers every rogue who could do Moon armor
/// (level 76), and Moon armor is still said to Maro in Kugnae, where tswolf puts every armor quest. The
/// ability sits before <c>armor_quest</c> in NpcAbilities.csv so it hears the word first. Below 70 it stays
/// silent and the armor chain answers as it always did.</para>
/// </summary>
public sealed class WhiteMoonAxeAbility : INpcAbility, INpcSayHandler
{
    public static readonly WhiteMoonAxeAbility Instance = new();

    /// <summary>RTK <c>player.quest["white_moon_axe"]</c>, so imported characters keep their place.</summary>
    public const string Key = "white_moon_axe";
    public const int StageBracelet = 1, StageScorpions = 2, StageJu = 3, StagePay = 4;

    /// <summary>Buya's rogue guildmaster Maso (42) and the three alignment copies that
    /// <c>PathHalls.csv</c> routes an aligned rogue to instead (Kwi-Sin 162, Ming-Ken 163, Ohaeng 164).
    /// Gating on 42 alone would hide the quest from every aligned rogue.</summary>
    public static readonly int[] Masos = { 42, 162, 163, 164 };

    public const int RoguePath = 2;
    public const int MinLevel = 70;       // tswolf, Atlas, the Rogue tutor; RTK asks 50
    public const uint Price = 20_000;
    public const int Scorpions = 5;

    public const string Bracelet = "whisper_bracelet";
    public const string Axe = "white_moon_axe";
    public const string ScorpionMob = "pale_scorpion";
    public const string JuMob = "skeleton_ju";

    // Kill-count snapshots taken at each ask; a step counts only kills made after it.
    public const string ScorpionBaseReg = "wma_scorpion_base";
    public const string JuBaseReg = "wma_ju_base";

    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx) => NoClickMenu.None;

    public async Task<bool> OnSay(NpcContext ctx, string speech)
    {
        if ((speech ?? "").Trim().ToLowerInvariant() != "moon") return false;
        if (Array.IndexOf(Masos, ctx.Def.Id) < 0) return false;
        if (ctx.BasePathId != RoguePath || ctx.Level < MinLevel) return false;   // armor_quest answers instead
        if (ctx.KarmaTooLow()) return true;

        await ctx.Say(
            "Under a white moon I slew a powerful man, of the family Ju, that owed me much money",
            "Yet, still I am not satisfied.");

        int stage = ctx.Stage(Key);
        if (stage == 0)
        {
            int pick = await ctx.Menu("Are you willing to make such a commitment?",
                                      new[] { "Yes, I am ready.", "I am busy. Later, perhaps." });
            if (pick != 1)
            {
                if (pick == 2) await ctx.Say("Then you were not the right person for this task.");
                return true;
            }
            ctx.SetStage(Key, stage = StageBracelet);
        }

        switch (stage)
        {
            case StageBracelet:
                if (ctx.CountReady(Bracelet) < 1)
                { await ctx.Say("Your hands are empty! Had you a Whisper bracelet, perhaps I could trust you would survive."); break; }
                ctx.SetReg(ScorpionBaseReg, ctx.KillCount(ScorpionMob));
                ctx.SetStage(Key, StageScorpions);
                await ctx.Say("If you knew what venom and speed combined were, perhaps I would consider you.");
                break;

            case StageScorpions:
                if (ctx.KillCount(ScorpionMob) - ctx.Reg(ScorpionBaseReg) < Scorpions)
                { await ctx.Say("Had you slain at least five pale scorpions, you would know."); break; }
                ctx.SetReg(JuBaseReg, ctx.KillCount(JuMob));
                ctx.SetStage(Key, StageJu);
                await AskForJu(ctx);
                break;

            case StageJu:
                if (ctx.KillCount(JuMob) - ctx.Reg(JuBaseReg) < 1) { await AskForJu(ctx); break; }
                ctx.SetStage(Key, StagePay);
                await Pay(ctx);
                break;

            case StagePay:
                await Pay(ctx);
                break;
        }
        return true;
    }

    private static Task AskForJu(NpcContext ctx) => ctx.Say(
        "Someone owes me money. Lots of it.",
        "He's dead now, but that doesn't matter. This is about honor.",
        "That night of slaying the Ju family caused the earth to shake. My weapons were filled with the power of the white moon.",
        "But I never found the money he owed me.",
        "I'm charging you to torment the family's skeletal remains. Their name in life was Ju.",
        "Destroy the skeleton of Ju, and we'll talk about you getting my money...",
        "...and the axe with which I slew the powerful man under a white moon.");

    private static async Task Pay(NpcContext ctx)
    {
        await ctx.Say(
            "Ah, his torment is my music.",
            "But I still must have the money he owed me, and all I have is this White moon axe.",
            "It was more than what you have. 20,000 coins in all. Should you repay me the money I am owed, I would give you this axe.");

        int pick = await ctx.Menu("Are you willing to give me 20,000 gold to replace what I am owed?", new[] { "Yes", "No" });
        if (pick != 1)
        {
            if (pick == 2) await ctx.Say("Then you were not the right person for this task.");
            return;
        }

        // Re-checked after the menu: the player could have spent or dropped coins while it was open.
        if (ctx.Coins < Price) { await ctx.Say("You do not have enough gold to repay me."); return; }
        if (ctx.FreeSlotCount < 1)
        { await ctx.Say("Make room in your pack first. I cannot hand you the axe with your hands so full."); return; }

        ctx.SpendGold(Price);
        ctx.GiveItem(Axe);
        ctx.SetStage(Key, 0);   // repeatable, as in RTK
        ctx.SetReg(ScorpionBaseReg, 0);
        ctx.SetReg(JuBaseReg, 0);

        await ctx.Say("There you are rogue. May it inspire you as it has me.",
                      "The moon is white, He's been desecrated. I have a moment of peace.");
    }
}
