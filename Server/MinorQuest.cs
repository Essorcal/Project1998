namespace Server;

/// <summary>
/// The repeatable "slay one X" quest, ported from RTK <c>Accepted/Quests/MinorQuest.lua</c>. A class trainer
/// offers it (menu: "Minor Quest" / "Complete Minor Quest"). On request it picks a random target from a tier
/// (Minor, or Major/Epic once the player is high enough) whose level/stat ranges the player falls in, snapshots
/// the player's lifetime kill count for that target's mobs, and marks a legend. On completion it checks the
/// player has killed one of them since (a kill-count delta), rewards experience scaled by tier, and records a
/// "Completed N minor quests" legend. Abandoning locks out new quests for the tier's cooldown.
///
/// State (RTK registry) maps to the character store: the active quest key -> <c>QuestStr("minor_quest")</c>;
/// tier / per-mob kill snapshots / cooldown timer / completed count -> the int quest registry
/// (<c>Reg</c>/<c>SetReg</c>); the two legends by internal name. Faithful to the Lua except the experience
/// curve (RTK's engine <c>getXPforLevel</c> isn't in the scripts, so the reward uses a documented
/// approximation) and karma (not modelled here).
/// </summary>
public sealed class MinorQuestAbility : INpcAbility, INpcSayHandler
{
    public static readonly MinorQuestAbility Instance = new();

    // registry keys (verbatim from the Lua)
    private const string KActive    = "minor_quest";                 // string registry: active quest key
    private const string KInfo      = "minor_quest_info";            // legend name: "On a quest to slay the X"
    private const string KKillPfx   = "minor_quest_kill_count_";     // int registry per mob: kill snapshot
    private const string KTier      = "minor_quest_tier";            // int registry: 1 Minor / 2 Major / 3 Epic
    private const string KTimer     = "minor_quest_timer";           // int registry: unix seconds cooldown ends
    private const string KCompleted = "minor_quests_completed";      // int registry + legend name: lifetime count
    private const string SayComplete = "Say 'complete' to me when you are done.";

    private sealed record Tier(string Label, double ExpFactor, int KarmaChance, int AbandonHours);
    // index 0 unused; tiers are 1-based to match the Lua's _questTiers.
    private static readonly Tier[] Tiers =
    {
        new("", 0, 0, 0),
        new("Minor", 0.6, 1, 2),
        new("Major", 0.8, 2, 10),
        new("Epic",  1.0, 3, 21),
    };

    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        yield return ("Minor Quest",          Quest);
        yield return ("Complete Minor Quest", Complete);
    }

    // Spoken shortcuts, verbatim from RTK's four class-trainer scripts (warrior/rogue/mage/poet_trainer.lua):
    // "quest"/"minor"/"minor quest" requests one; "complete"/"complete quest" turns it in.
    public async Task<bool> OnSay(NpcContext ctx, string speech)
    {
        switch (speech)
        {
            case "quest" or "minor" or "minor quest":     await Quest(ctx);    return true;
            case "complete" or "complete quest":          await Complete(ctx); return true;
            default:                                      return false;
        }
    }

    private static bool Between(long v, long lo, long hi) => v >= lo && v <= hi;
    private static MinorQuestDef? Find(string tierLabel, string key) =>
        Content.MinorQuests.FirstOrDefault(q => q.Tier == tierLabel && q.Key == key);

    // ---- request a quest ------------------------------------------------------------------------
    private async Task Quest(NpcContext ctx)
    {
        long timer = ctx.Reg(KTimer);
        if (ctx.NowUnix < timer)
        {
            int hours = (int)Math.Ceiling((timer - ctx.NowUnix) / 3600.0);
            await ctx.Say($"You must wait {hours} hour{(hours > 1 ? "s" : "")} before beginning another quest.");
            return;
        }

        if (ctx.QuestStr(KActive) != "")
        {
            await AbandonMenu(ctx);
            return;
        }

        int tier = 1;   // Minor by default; higher tiers unlock with level (matches the Lua gating)
        if (ctx.Level >= 10)
        {
            var labels = new List<string> { Tiers[1].Label, Tiers[2].Label };
            if (ctx.Level >= 15) labels.Add(Tiers[3].Label);
            int pick = await ctx.Menu("Which type of quest do you seek?", labels);
            if (pick < 1 || pick > labels.Count) return;   // cancelled
            tier = pick;
        }

        string label = Tiers[tier].Label;
        long stat = ctx.Stat;
        int mark = ctx.Mark, level = ctx.Level;
        var qualifying = Content.MinorQuests.Where(q =>
            q.Tier == label &&
            Between(level, q.MinLevel, q.MaxLevel) &&
            Between(stat,  q.MinStat,  q.MaxStat)  &&
            Between(mark,  q.MinMark,  q.MaxMark)).ToList();

        if (qualifying.Count == 0)
        {
            await ctx.Say("I have no such quest for you right now. Please choose another.");
            return;
        }

        var quest = qualifying[ctx.Random(qualifying.Count) - 1];

        foreach (var mob in quest.Mobs) ctx.SetReg(KKillPfx + mob, ctx.KillCount(mob));   // snapshot
        ctx.SetQuestStr(KActive, quest.Key);
        ctx.SetReg(KTier, tier);
        ctx.AddLegend($"On a quest to slay the {quest.DisplayName}", KInfo, 5, 128);

        await ctx.Say("Alas, it has come to my attention that a curse has been laid upon one of your fellow citizens.");
        await ctx.Say($"You must slay one {quest.DisplayName} to release the curse. I will reward you if you can accomplish this task.");
        await ctx.Say(SayComplete);
    }

    // ---- complete the active quest --------------------------------------------------------------
    private async Task Complete(NpcContext ctx)
    {
        string key = ctx.QuestStr(KActive);
        if (key == "")
        {
            await ctx.Say("You must begin a quest before you can complete it.");
            return;
        }

        int tier = ctx.Reg(KTier);
        var quest = Find(Tiers[Math.Clamp(tier, 1, 3)].Label, key);
        if (quest is null) { ClearQuest(ctx, tier, abandoned: false); return; }   // stale key — reset cleanly

        bool met = quest.Mobs.Any(mob => ctx.KillCount(mob) > ctx.Reg(KKillPfx + mob));
        if (!met)
        {
            await ctx.Say($"Please return when you have slain one {quest.DisplayName}.");
            return;
        }

        ClearQuest(ctx, tier, abandoned: false);
        await AwardBonuses(ctx, tier);
    }

    private async Task AbandonMenu(NpcContext ctx)
    {
        int tier = ctx.Reg(KTier);
        var quest = Find(Tiers[Math.Clamp(tier, 1, 3)].Label, ctx.QuestStr(KActive));
        string name = quest?.DisplayName ?? "creature";
        int hours = Tiers[Math.Clamp(tier, 1, 3)].AbandonHours;

        int choice = await ctx.Menu(
            $"You are already on a quest to slay one {name}. Abandoning your quest will prevent you from beginning a new quest for {hours} hours.",
            new[] { "Continue", "Abandon" });

        if (choice == 1) { await ctx.Say(SayComplete); return; }
        if (choice == 2)
        {
            ClearQuest(ctx, tier, abandoned: true);
            await ctx.Say("So be it.");
        }
    }

    // Reset the active quest's state. On abandon, start the tier cooldown; on completion, bump the count legend.
    private static void ClearQuest(NpcContext ctx, int tier, bool abandoned)
    {
        var quest = Find(Tiers[Math.Clamp(tier, 1, 3)].Label, ctx.QuestStr(KActive));
        if (quest is not null) foreach (var mob in quest.Mobs) ctx.SetReg(KKillPfx + mob, 0);

        ctx.SetQuestStr(KActive, "");
        ctx.SetReg(KTier, 0);
        ctx.RemoveLegend(KInfo);

        if (abandoned)
        {
            ctx.SetReg(KTimer, (int)(ctx.NowUnix + Tiers[Math.Clamp(tier, 1, 3)].AbandonHours * 3600));
            return;
        }

        int completed = ctx.Reg(KCompleted) + 1;
        ctx.SetReg(KCompleted, completed);
        ctx.AddLegend($"Completed {completed} minor quests", KCompleted, 5, 128);
    }

    private static async Task AwardBonuses(NpcContext ctx, int tier)
    {
        double expFactor = Tiers[Math.Clamp(tier, 1, 3)].ExpFactor;
        // RTK: expBonus = max(ceil(tnl * 0.20 * expFactor), 300), where tnl is the exp between this level and
        // the next from the engine's getXPforLevel table. That table isn't in the Lua scripts, so approximate
        // tnl with a smooth, monotonic curve (floored at 300 exactly as RTK does for low levels).
        long approxTnl = 100L * ctx.Level * (ctx.Level + 1);
        uint expBonus = (uint)Math.Max(300, (long)Math.Ceiling(approxTnl * 0.20 * expFactor));
        ctx.AwardExp(expBonus);

        await ctx.Say("Thank you for your efforts! You have served your path and kingdom well.");
    }
}
