using Shared;

namespace Server;

/// <summary>
/// <b>Bon-Hwa</b>, the immortal at the Forever Tree (map 1229, the chamber past the Tree itself on 1228; the
/// inert <c>BonHwaNpc</c> in NPCs.csv). RTK <c>NPCs/wilderness/bon_hwa.lua</c>. Its two services and its
/// "Il San" branch (the three trials and the promotion to the first mark) are built here; its "Ee San" branch
/// is not — see the notes at the bottom.
///
/// <para><b>Entry gate.</b> Level 99 AND the "Enchanted" rank — 80,000 base Vitality OR 40,000 base Mana
/// (<see cref="NpcContext.MaxHp"/>/<see cref="NpcContext.MaxMp"/> are our baseHealth/baseMagic analog, as the
/// war-paint and Shadow-Stats NPCs already use them). Below either bar it turns you away, word for word.</para>
///
/// <para><b>Bon-Hwa Immortality → My Weapon.</b> Enchant a class weapon (Spike/Blood/Surge/Charm, one ladder
/// per base class) straight to the tier your mark allows — mark 0 → Enchanted, 1 → Il san, 2 → Ee san, 3 →
/// Sam san — for a flat 200,000,000 experience. It consumes whichever tier of your weapon you hand over and
/// gives back the target tier, <b>bonded</b> to you (RTK <c>addItem(..., player.ID)</c>; the tiers are Content
/// bonded ids). You cannot enchant a weapon already at or past your mark's tier. Sa san (mark 4) is out of era
/// and unreachable here (<see cref="Content.MaxMark"/> = 3), so the ladder stops at Sam san.</para>
///
/// <para><b>Shadow Stats.</b> The same trade-banked-exp-for-stat flow the ExpSeller vendors run
/// (<see cref="ShadowStatsAbility"/>), but this is RTK's <c>npcIsBonHwa</c> branch of
/// <c>ExpSeller.showShadowStatsMenu</c>: the Might/Grace/Will cap rises past 130 on a per-(mark, class) table
/// (<see cref="BonHwaCaps"/>), and each point costs a flat <b>100,000,000</b> experience (RTK's
/// <c>statCost * 10</c>) rather than 10M. The entry gate already guarantees the Enchanted-rank stat floor the
/// Lua re-checks, so the table always applies here.</para>
///
/// <para><b>Il San</b> (bon_hwa.lua's "Il San" branch). Offered beside the two services to a mark-0 player with
/// 160,000 base Vitality OR 80,000 base Mana. Three trials, each granting its "Passed First Trial of …" legend:
/// <b>Knowledge</b>, sacrifice 1,200,000,000 experience; <b>Strength</b>, the Spirit Rat killed at any time
/// (the lifetime kill count, RTK <c>killCount</c>, not the kill track); <b>Wealth</b>, 600,000 coins and 5 Well
/// Crafted White Ambers. Nothing in this world sells, drops or crafts that amber yet (RTK makes it by
/// gemcutting, which is not modelled), so in play the Wealth trial cannot be finished until crafting exists —
/// kept as RTK has it, with no stand-in source. Choosing "Il San" again with all three legends promotes: the
/// class animation, the three legends exchanged for "Attained First Mark (date)", mark 1 on the same path, and
/// a system line to everyone online. The promotion changes the mark and nothing else — no stat, no spell; the
/// rank's secrets are then the class trainer's to teach, since its Learn Secret menu reads the mark.</para>
///
/// <para><b>Taking and granting after the last await</b> (#55). A second click on any NPC while one of these
/// prompts waits orphans this conversation (<c>Session.AwaitReply</c> replaces the pending reply), so nothing
/// here may rely on a check made before an await. Every trial reads what it takes — experience, coins, ambers,
/// the kill count — after its last prompt has been answered, and takes and grants with no await in between;
/// the promotion reads the three legends and the mark after the menu pick and finishes before the next prompt.
/// An orphaned conversation never resumes, so it can neither take nor grant.</para>
///
/// <para><b>Not built: Ee San</b> (and the commented-out Sam/Sa/Oh San). Its trials need carnage and minigame
/// win counters and crafting skill levels we do not model. See docs/common/Deferred-Work.md.</para>
/// </summary>
public sealed class BonHwaAbility : INpcAbility
{
    public static readonly BonHwaAbility Instance = new();

    private const uint EnchantCost = 200_000_000;   // RTK bon_hwa.lua: flat exp to enchant a weapon
    private const uint StatCost    = 100_000_000;   // RTK ExpSeller.lua: statCost (10M) * 10 for Bon-Hwa

    // ---- Il San (RTK bon_hwa.lua's "Il San" branch) ----
    private const uint IlSanVita     = 160_000;          // the menu entry: 160,000 base Vitality ...
    private const uint IlSanMana     = 80_000;           // ... or 80,000 base Mana, at mark 0
    private const uint KnowledgeExp  = 1_200_000_000;    // First Trial of Knowledge
    private const uint WealthCoins   = 600_000;          // First Trial of Wealth: coins ...
    private const string AmberKey    = "well_crafted_white_amber";   // ... and 5 of these (no source yet)
    private const int AmberCount     = 5;
    private const string SpiritRat   = "spirit_rat";     // First Trial of Strength

    private const string KnowledgeLegend = "passed_first_trial_of_knowledge";
    private const string StrengthLegend  = "passed_first_trial_of_strength";
    private const string WealthLegend    = "passed_first_trial_of_wealth";
    private const string FirstMarkLegend = "attained_first_mark";

    // RTK ExpSeller.lua _bonHwaLimits[mark+1][baseClass] — {Might, Grace, Will} caps. Rows are marks 0..3
    // (Enchanted, Il san, Ee san, Sam san); columns are base class 1..4 (Warrior, Rogue, Mage, Poet). The
    // Sa-san row (mark 4) is omitted because MaxMark = 3 makes it unreachable.
    private static readonly int[][][] Limits =
    {
        new[] { new[] {135,130,130}, new[] {130,135,130}, new[] {130,130,135}, new[] {130,130,135} }, // Enchanted
        new[] { new[] {140,135,130}, new[] {135,140,130}, new[] {130,135,140}, new[] {135,130,140} }, // Il san
        new[] { new[] {150,140,130}, new[] {140,150,130}, new[] {130,140,150}, new[] {136,130,150} }, // Ee san
        new[] { new[] {150,145,130}, new[] {145,150,130}, new[] {130,145,150}, new[] {139,130,150} }, // Sam san
    };

    /// <summary>The per-(mark, base class) Might/Grace/Will caps. Falls back to the flat 130 the ExpSeller uses
    /// for any class outside 1..4 (a Peasant can't reach the entry gate, but the ladder must still resolve).</summary>
    private static int[] BonHwaCaps(int mark, int baseClass)
    {
        if (baseClass < 1 || baseClass > 4 || mark < 0 || mark >= Limits.Length)
            return new[] { 130, 130, 130 };
        return Limits[mark][baseClass - 1];
    }

    // The four class weapon ladders, base -> Sam san (index 0..4). Bon-Hwa gives ladder[mark+1] and consumes any
    // lower tier you hold. Poet's Enchanted charm (index 1) is the one tier Atlas marks Non-Bonded; see Content.
    private static readonly Dictionary<int, string[]> Ladders = new()
    {
        [1] = new[] { "spike", "enchanted_spike", "il_san_spike", "ee_san_spike", "sam_san_spike" },
        [2] = new[] { "blood", "enchanted_blood", "il_san_blood", "ee_san_blood", "sam_san_blood" },
        [3] = new[] { "surge", "enchanted_surge", "il_san_surge", "ee_san_surge", "sam_san_surge" },
        [4] = new[] { "charm", "enchanted_charm", "il_san_charm", "ee_san_charm", "sam_san_charm" },
    };

    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        yield return ($"Talk to {ctx.Def.Name}", Talk);
    }

    private static bool MeetsEnchantedRank(NpcContext ctx) => ctx.MaxHp >= 80_000 || ctx.MaxMp >= 40_000;

    private static async Task Talk(NpcContext ctx)
    {
        if (ctx.Level < 99 || !MeetsEnchantedRank(ctx))
        { await ctx.Say("It was foolhardy of you to venture here, one so weak as yourself. There is nothing I can do for you."); return; }

        var options = new List<string> { "Bon-Hwa Immortality", "Shadow Stats" };
        if (QualifiesForIlSan(ctx)) options.Add("Il San");

        int choice = await ctx.Menu("Hello! How can I help you today?", options);
        if (choice < 1 || choice > options.Count) return;

        switch (options[choice - 1])
        {
            case "Bon-Hwa Immortality": await EnchantWeapon(ctx); break;
            case "Shadow Stats":        await ShadowStats(ctx); break;
            case "Il San":              await IlSan(ctx); break;
        }
    }

    // RTK: (baseHealth >= 160000 or baseMagic >= 80000) and mark == 0.
    private static bool QualifiesForIlSan(NpcContext ctx) =>
        (ctx.MaxHp >= IlSanVita || ctx.MaxMp >= IlSanMana) && ctx.Mark == 0;

    // ---- Il San: the three trials and the promotion (RTK bon_hwa.lua "Il San") ------------------------
    private static async Task IlSan(NpcContext ctx)
    {
        // Read again after the menu's await: the pick may answer a menu shown before something else changed
        // the mark (a GM's @mark), and a promotion must never be granted on a stale read (#55).
        if (!QualifiesForIlSan(ctx)) return;

        if (ctx.HasLegend(KnowledgeLegend) && ctx.HasLegend(StrengthLegend) && ctx.HasLegend(WealthLegend))
        {
            Promote(ctx);
            // RTK falls through here and offers the trial menu again, listing the three trials whose legends
            // it has just consumed; the promotion is where its own block ends (bon_hwa.lua:216-247), so ours
            // ends the conversation there.
            return;
        }

        var trials = new List<string>();
        if (!ctx.HasLegend(KnowledgeLegend)) trials.Add("First Trial of Knowledge");
        if (!ctx.HasLegend(StrengthLegend))  trials.Add("First Trial of Strength");
        if (!ctx.HasLegend(WealthLegend))    trials.Add("First Trial of Wealth");

        int pick = await ctx.Menu("Please select a trial.", trials);
        if (pick < 1 || pick > trials.Count) return;

        switch (trials[pick - 1])
        {
            case "First Trial of Knowledge": await TrialOfKnowledge(ctx); break;
            case "First Trial of Strength":  await TrialOfStrength(ctx); break;
            case "First Trial of Wealth":    await TrialOfWealth(ctx); break;
        }
    }

    private static async Task TrialOfKnowledge(NpcContext ctx)
    {
        int yes = await ctx.Menu(
            "To complete this trial, you must sacrifice to me 1,200,000,000 (1.2 Billion) experience. Do you wish to do so?",
            new[] { "Yes", "No" });
        if (yes == 2) { await ctx.Say("Come back when you are serious."); return; }
        if (yes != 1) return;

        // SpendExp checks and spends in one step, after the last await.
        if (!ctx.SpendExp(KnowledgeExp)) { await ctx.Say("Come back when you have enough experience."); return; }
        ctx.AddLegend($"Passed First Trial of Knowledge ({Character.GameDate})", KnowledgeLegend, 3, 15);
        await ctx.Say("Congratulations! You have completed the First Trial of Knowledge.");
    }

    private static async Task TrialOfStrength(NpcContext ctx)
    {
        await ctx.Say("For this trial, I will need you to kill the pesky Spirit Rat who lurks in the Rat Cave. Return to me when you have done so.");

        // RTK player:killCount — the lifetime tally, so a Spirit Rat killed at any time counts.
        if (ctx.KillCount(SpiritRat) < 1) { await ctx.Say("You still need to slay the Spirit Rat."); return; }
        ctx.AddLegend($"Passed First Trial of Strength ({Character.GameDate})", StrengthLegend, 1, 15);
        await ctx.Say("Congratulations! You have completed the First Trial of Strength.");
    }

    private static async Task TrialOfWealth(NpcContext ctx)
    {
        int yes = await ctx.Menu(
            "To complete this trial, you must sacrifice to me 600,000 coins and 5 Well Crafted White ambers. Do you wish to do so?",
            new[] { "Yes", "No" });
        if (yes == 2) { await ctx.Say("Come back when you are serious."); return; }
        if (yes != 1) return;

        // Both checks, then both takes, with no await between them: nothing can spend the coins or move the
        // ambers in between, so neither take can come up short.
        if (ctx.Coins < WealthCoins) { await ctx.Say("Return to me when you have the gold."); return; }
        if (!ctx.HasItem(AmberKey, AmberCount)) { await ctx.Say("Return to me when you have the 5 well crafted white ambers."); return; }
        if (!ctx.SpendGold(WealthCoins)) return;
        if (!ctx.TakeItem(AmberKey, AmberCount)) { ctx.AwardGold(WealthCoins); return; }
        // RTK says nothing on this trial's success: the legend is the only sign.
        ctx.AddLegend($"Passed First Trial of Wealth ({Character.GameDate})", WealthLegend, 1, 15);
    }

    /// <summary>RTK's promotion block (bon_hwa.lua:216-247): the class animation, the three trial legends
    /// exchanged for the First Mark legend, <c>updatePath(player.class, 1)</c> (mark 1, the path kept, the status
    /// resent) and the world broadcast. No await anywhere in it, so it cannot be interleaved.</summary>
    private static void Promote(NpcContext ctx)
    {
        ctx.PlayEffect(PromotionAnimation(ctx.BaseClass));
        if (!ctx.SetMark(1)) return;   // refused only past Content.MaxMark, which is 3: nothing is taken then
        ctx.RemoveLegend(KnowledgeLegend);
        ctx.RemoveLegend(StrengthLegend);
        ctx.RemoveLegend(WealthLegend);
        ctx.AddLegend($"Attained First Mark ({Character.GameDate})", FirstMarkLegend, 32, 15);
        ctx.AnnounceToWorld($"[SYSTEM]: {ctx.PlayerName} has attained First Mark!");
    }

    // RTK: baseClass 1 Warrior 83 · 2 Rogue 82 · 3 Mage 81 · 4 Poet 80. A subpath reads its base class.
    private static int PromotionAnimation(int baseClass) => baseClass switch
    {
        1 => 83,
        2 => 82,
        3 => 81,
        4 => 80,
        _ => -1,   // no class to animate (a Peasant cannot reach Bon-Hwa); a negative effect is not sent
    };

    // ---- Bon-Hwa Immortality -> My Weapon --------------------------------------------------------------
    private static async Task EnchantWeapon(NpcContext ctx)
    {
        int what = await ctx.Menu("What would you like to enchant?", new[] { "My Weapon" });
        if (what != 1) return;

        if (!Ladders.TryGetValue(ctx.BaseClass, out var ladder))
        { await ctx.Say("There is nothing I can do for you."); return; }

        // mark 0 -> Enchanted (ladder[1]); mark 1 -> Il san (ladder[2]); ... The gate already forced the
        // Enchanted-rank stats mark 0 needs, so mark maps straight to the target index.
        int targetIndex = Math.Min(ctx.Mark + 1, ladder.Length - 1);
        string targetKey = ladder[targetIndex];

        // What you can hand in: any tier BELOW the target that you actually carry.
        var candidates = new List<string>();
        for (int i = 0; i < targetIndex; i++)
            if (ctx.CountItem(ladder[i]) > 0) candidates.Add(ladder[i]);

        if (candidates.Count == 0)
        {
            bool holdsAtOrAboveTarget = false;
            for (int i = targetIndex; i < ladder.Length; i++)
                if (ctx.CountItem(ladder[i]) > 0) { holdsAtOrAboveTarget = true; break; }

            if (holdsAtOrAboveTarget)
                await ctx.Say("Your weapon is already upgraded to its max level and cannot be upgraded further.");
            else
                await ctx.Say($"You carry no {ctx.ItemName(ladder[0])} for me to enchant.");
            return;
        }

        int pick = candidates.Count == 1 ? 1
            : await ctx.Menu("Please select the weapon you would like to enchant.",
                             candidates.Select(ctx.ItemName).ToList());
        if (pick < 1 || pick > candidates.Count) return;
        string sacrificeKey = candidates[pick - 1];

        int confirm = await ctx.Menu(
            $"It will cost {EnchantCost:N0} experience to enchant the {ctx.ItemName(sacrificeKey)} to your current mark.\n\nWould you like to upgrade this item?",
            new[] { "Okay", "No" });
        if (confirm != 1) { await ctx.Say("Please return to me if you change your mind."); return; }

        if (ctx.Exp < EnchantCost) { await ctx.Say("You do not have enough experience."); return; }

        // Take the sacrifice first, then pay, then grant — never charge for a weapon we couldn't consume.
        if (!ctx.TakeItem(sacrificeKey, 1)) { await ctx.Say("You do not have that weapon."); return; }
        ctx.SpendExp(EnchantCost);
        ctx.GiveItem(targetKey, 1);   // bonds on grant: every enchant-output tier is a Content bonded id
        await ctx.Say("Use this weapon well and wisely.");
    }

    // ---- Shadow Stats (RTK's npcIsBonHwa branch: per-(mark,class) caps, 100M/point) ---------------------
    private static async Task ShadowStats(NpcContext ctx)
    {
        int[] caps = BonHwaCaps(ctx.Mark, ctx.BaseClass);

        if (ctx.Exp < StatCost)
        { await ctx.Say($"You do not understand enough of your true nature to unleash your potential any further. Please return when you possess at least {StatCost:N0} experience."); return; }

        var opts = new List<(string Label, int Base, int Cap, Action<int> Raise)>();
        if (ctx.Might < caps[0]) opts.Add(("Might", ctx.Might, caps[0], ctx.RaiseMight));
        if (ctx.Grace < caps[1]) opts.Add(("Grace", ctx.Grace, caps[1], ctx.RaiseGrace));
        if (ctx.Will  < caps[2]) opts.Add(("Will",  ctx.Will,  caps[2], ctx.RaiseWill));

        if (opts.Count == 0) { await ctx.Say("You have already realized your full potential."); return; }

        int pick = await ctx.Menu("Which aspect of your potential do you seek to unleash?",
                                  opts.Select(o => o.Label).ToList());
        if (pick < 1 || pick > opts.Count) return;
        var (label, baseVal, cap, raise) = opts[pick - 1];

        int maxShadows = Math.Min((int)(ctx.Exp / StatCost), cap - baseVal);
        if (maxShadows <= 0) { await ctx.Say("It is impossible to exceed one's own potential."); return; }

        string? input = await ctx.Input(
            $"Your natural {label} is {baseVal}.\n\nYou can unleash your shadow potential up to {maxShadows} times.\n\nHow many times do you choose?");
        if (!int.TryParse(input, out int count) || count <= 0) return;
        if (count > maxShadows) { await ctx.Say("It is impossible to exceed one's own potential."); return; }

        uint cost = (uint)count * StatCost;
        int newVal = baseVal + count;
        int confirm = await ctx.Menu(
            $"Your {label} will permanently increase to {newVal}.\n\n{cost:N0} experience will be irrevocably sacrificed.\n\nAre you sure?",
            new[] { "Yes", "No" });
        if (confirm != 1) return;

        if (!ctx.SpendExp(cost)) { await ctx.Say("It is impossible to exceed one's own potential."); return; }
        raise(count);
        await ctx.Say("It is done.");
    }
}
