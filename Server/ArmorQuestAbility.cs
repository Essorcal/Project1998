using Shared;

namespace Server;

/// <summary>
/// The guildmaster's half of the Star/Moon/Sun chains: hears "star"/"moon"/"sun" and runs whichever step of
/// that chain the speaker is on. Speech-only — every period source describes the whole interface as saying
/// the word again after each completed step, so this contributes no click-menu entry and clicking a
/// guildmaster still shows the ordinary trainer menu.
///
/// <para>Composed onto all four <c>*TrainerNpc</c> identifiers (game-data/NpcAbilities.csv), then narrowed
/// here to the eight Kugnae/Buya guildmasters and to the speaker's OWN path — see
/// <see cref="ArmorQuest.GuildMasters"/>. A word that isn't for this NPC returns false and falls through to
/// ordinary chat, exactly as an unrecognised word does.</para>
///
/// <para><b>Progress state</b> is one stage int per chain (<see cref="ArmorChain.StageKey"/> — RTK's own
/// <c>star_armor</c>/<c>moon_armor</c>/<c>sun_armor</c> names, so imported characters keep their place) plus
/// per-step kill snapshots. A step's kill requirement is always a DELTA measured from the moment the
/// guildmaster asked, which is what makes "make SURE these are the LAST creatures you kill" enforceable:
/// kills banked before the ask do not pay for it.</para>
/// </summary>
public sealed class ArmorQuestAbility : INpcAbility, INpcSayHandler
{
    public static readonly ArmorQuestAbility Instance = new();

    /// <summary>No click entry — the chain is heard, not read off a menu.</summary>
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx) => NoClickMenu.None;

    public async Task<bool> OnSay(NpcContext ctx, string speech)
    {
        string word = (speech ?? "").Trim().ToLowerInvariant();
        if (word is not ("star" or "moon" or "sun")) return false;
        if (!ArmorQuest.GuildMasters.TryGetValue(ctx.Def.Id, out int path)) return false;
        if (ctx.BasePathId != path) return false;      // another path's master has nothing to say about it
        if (!ArmorQuest.Chains.TryGetValue((path, word), out var chain)) return false;
        if (ctx.KarmaTooLow()) return true;

        // ---- the three gates that come before any step: legends, level, karma --------------------
        if (!await Eligible(ctx, chain)) return true;

        await Run(ctx, chain);
        return true;
    }

    /// <summary>Prerequisite legends, then level, then karma tier. Each refusal speaks; none of them
    /// advances anything.</summary>
    private static async Task<bool> Eligible(NpcContext ctx, ArmorChain chain)
    {
        if (ctx.HasLegend(chain.Legend))
        { await ctx.Say(AlreadyDone(chain.Tier)); return false; }

        switch (chain.Tier)
        {
            case "star":
                if (!ctx.HasLegend(ArmorQuest.BlessedLegend))
                { await ctx.Say("The stars have not yet blessed you. Return when they have."); return false; }
                break;
            case "moon":
                if (!ctx.HasLegend(ArmorQuest.StarLegend))
                { await ctx.Say("You have not yet mastered the stars. The moon is not for you."); return false; }
                // Sun is finished, so Moon is behind you even without its own mark (RTK gates the same way).
                if (ctx.HasLegend(ArmorQuest.SunLegend))
                { await ctx.Say(AlreadyDone("moon")); return false; }
                break;
            default:
                if (!ctx.HasLegend(ArmorQuest.StarLegend) || !ctx.HasLegend(ArmorQuest.MoonLegend))
                { await ctx.Say("The stars and the moon come first. Return when you understand them both."); return false; }
                break;
        }

        var (level, karma) = ArmorQuest.GateFor(chain.Path, chain.Tier);
        if (ctx.Level < level)
        { await ctx.Say($"You are not yet ready for this. Return when you have seen {level} seasons of battle."); return false; }
        if (!ctx.KarmaCheck(karma))
        { await ctx.Say(Impure(chain.Tier)); return false; }

        return true;
    }

    private static string AlreadyDone(string tier) => tier switch
    {
        "star" => "You have already mastered the stars.",
        "moon" => "You have already understood the moon.",
        _      => "You have already survived the sun. There is nothing more I can teach you.",
    };

    // RTK's three refusal lines, one per tier — the only surviving wording for them.
    private static string Impure(string tier) => tier switch
    {
        "star" => "Your soul is too impure to master the stars. Improve your karma and return.",
        "moon" => "Your soul is too impure to understand the moon. Improve your karma and return.",
        _      => "Your soul is too impure to survive the sun. Improve your karma and return.",
    };

    // =============================================================================================
    // The step runner
    // =============================================================================================
    private static async Task Run(NpcContext ctx, ArmorChain chain)
    {
        int stage = Math.Clamp(ctx.Stage(chain.StageKey), 0, chain.Steps.Length - 1);
        var step = chain.Steps[stage];
        string pfx = $"aq_{chain.Tier}_{stage}_";

        // The chain's opening lines play whenever you are still on its first step — RTK speaks them on
        // every visit to step 1 too, and they read as the guildmaster restating why you are here.
        if (stage == 0 && chain.Intro.Length > 0) await ctx.Say(chain.Intro);

        OpenStep(ctx, step, pfx);
        await SpeakAsk(ctx, chain, step);

        // ---- kills you should NOT have made ------------------------------------------------------
        // Both of these restart the step's count from now rather than failing it forever. That is the
        // gentler of the two readings — Atlas's is "you will have to start from step 1" — and it is the
        // only one that leaves a poisoned counter recoverable.
        if (step.Forbid.Any(m => ctx.KillCount(m) > ctx.Reg(pfx + m)))
        {
            Resnapshot(ctx, step, pfx);
            await ctx.Say(step.Spoiled);
            return;
        }

        // "and NOTHING else". Checked BEFORE the count and independently of it: a stray squirrel taken on
        // the way to 200 rabbits has to be caught while the tally is still short, or the total delta stays
        // permanently one ahead of the rabbit delta and the step can never be passed.
        if (step.Pure)
        {
            int allowed = Watched(step).Sum(m => ctx.KillCount(m) - ctx.Reg(pfx + m));
            if (ctx.TotalKills - ctx.Reg(pfx + "@") > allowed)
            {
                Resnapshot(ctx, step, pfx);
                await ctx.Say(step.Spoiled);
                return;
            }
        }

        // ---- kills -----------------------------------------------------------------------------
        (string Mob, int Count)[]? met = null;
        foreach (var group in step.Kills)
            if (group.All(r => ctx.KillCount(r.Mob) - ctx.Reg(pfx + r.Mob) >= r.Count)) { met = group; break; }

        if (step.Kills.Length > 0 && met is null) { await ctx.Say(step.Unmet); return; }

        // ---- everything else the step wants ----------------------------------------------------
        if (step.Items.Any(i => ctx.CountReady(i.Key) < i.Count)) { await ctx.Say(step.Unmet); return; }

        string[] offered = Array.Empty<string>();
        if (step.AnyItems is { } any)
        {
            offered = any.Pool.Where(k => ctx.CountReady(k) >= 1).Take(any.Count).ToArray();
            if (offered.Length < any.Count) { await ctx.Say(step.Unmet); return; }
        }

        if (step.Gold > 0 && ctx.Coins < step.Gold) { await ctx.Say(step.Unmet); return; }
        if (step.Rebond is not null && ctx.CountReady(step.Rebond) < 1) { await ctx.Say(step.Unmet); return; }
        if (step.Extra is not null && !step.Extra(ctx)) { await ctx.Say(step.Unmet); return; }

        string? prevArmor = step.IsFinal ? chain.PreviousArmorFor(ctx.Sex) : null;
        if (prevArmor is not null && ctx.CountReady(prevArmor) < 1)
        { await ctx.Say($"Please return when you have your {ctx.ItemName(prevArmor)}."); return; }

        // A give that can't fit must not follow a take, or the tribute is gone and the reward never lands.
        // The old garment (or the axe) frees its own slot only if it was in the bag rather than worn.
        if (step.IsFinal || step.Rebond is not null)
        {
            string incoming = step.Rebond ?? chain.ArmorFor(ctx.Sex);
            int freed = step.Rebond is not null ? ctx.CountItem(step.Rebond) > 0 ? 1 : 0
                      : prevArmor is not null && ctx.CountItem(prevArmor) > 0 ? 1 : 0;
            if (ctx.FreeSlotCount + freed < 1)
            { await ctx.Say($"Make room in your pack first. I cannot hand you {ctx.ItemName(incoming)} with your hands so full."); return; }
        }

        // ---- last chance to back out, then the price is taken ----------------------------------
        if (step.TakesSomething && !await Confirm(ctx, chain, step)) return;

        // The confirm AWAITED, and the player was free to act while it was open — they could have dropped,
        // traded or banked the tribute between the check above and this line. Re-verify, and take nothing if
        // anything moved: the alternative is a guildmaster who hands out bonded armor for goods that are no
        // longer there. Cheap, and the only window in the whole flow where the two can disagree.
        if (!StillHolds(ctx, step, offered, prevArmor)) { await ctx.Say(step.Unmet); return; }

        foreach (var (key, count) in step.Items)
        {
            var back = step.KeepBack.FirstOrDefault(k => k.Key == key);
            ctx.TakeReady(key, back.Key is null ? count : Math.Min(count, ctx.Random(back.Max)));
        }
        foreach (var key in offered) ctx.TakeReady(key, 1);
        if (step.Gold > 0) ctx.SpendGold(step.Gold);

        // Rebonding: take the axe (whosever it was) and hand back a fresh one, which GivePlaced stamps with
        // this character's name because white_moon_axe is a bonded row. "It will simply be rebonded to you."
        if (step.Rebond is not null) { ctx.TakeReady(step.Rebond, 1); ctx.GiveItem(step.Rebond, 1); }
        if (prevArmor is not null) ctx.TakeReady(prevArmor, 1);

        if (step.Might > 0) ctx.RaiseMight(-step.Might);
        if (step.Grace > 0) ctx.RaiseGrace(-step.Grace);
        if (step.Will  > 0) ctx.RaiseWill(-step.Will);
        if (step.Karma > 0) ctx.RemoveKarma(step.Karma);
        step.OnPay?.Invoke(ctx);

        if (!step.IsFinal)
        {
            ctx.SetStage(chain.StageKey, stage + 1);
            await ctx.Say(step.Done);
            return;
        }

        // ---- the chain ends -------------------------------------------------------------------
        string armor = chain.ArmorFor(ctx.Sex);
        ctx.GiveItem(armor, 1);                                     // bonded on the way in (ItemDef.Bonded)
        ctx.AddLegend($"{chain.LegendText} ({Character.GameDate})", chain.Legend,
                      ArmorQuest.LegendIcon, ArmorQuest.LegendColor);
        ctx.SetStage(chain.StageKey, 0);
        ClearMarkers(ctx, chain);
        await ctx.Say("It is yours.");
    }

    /// <summary>Is every material thing the step charges for still in hand? Re-checked after the confirm
    /// dialog returns — see the call site. Kills and <see cref="ArmorStep.Extra"/> gates are not re-tested:
    /// neither can be undone by anything the player does while a dialog is open.</summary>
    private static bool StillHolds(NpcContext ctx, ArmorStep step, string[] offered, string? prevArmor)
    {
        if (step.Items.Any(i => ctx.CountReady(i.Key) < i.Count)) return false;
        if (offered.Any(k => ctx.CountReady(k) < 1)) return false;
        if (step.Gold > 0 && ctx.Coins < step.Gold) return false;
        if (step.Rebond is not null && ctx.CountReady(step.Rebond) < 1) return false;
        if (prevArmor is not null && ctx.CountReady(prevArmor) < 1) return false;
        return true;
    }

    /// <summary>Speak the step's ask. A closing step generates most of its own: the previous tier's garment
    /// (whose name depends on the speaker's sex, so it cannot be a literal) and an itemised price, because
    /// "some of your abilities and some karma" is not something a player can plan around and every period
    /// page prints the exact numbers.</summary>
    private static async Task SpeakAsk(NpcContext ctx, ArmorChain chain, ArmorStep step)
    {
        var lines = new List<string>(step.Ask);
        if (step.IsFinal)
        {
            string? prev = chain.PreviousArmorFor(ctx.Sex);
            if (prev is not null)
                lines.Add($"Now bring me your unequipped {ctx.ItemName(prev)}, from which to make the new. " +
                          "Any will serve — bonded to another, bonded to you, or bonded to nobody.");
            string price = Price(step);
            if (price.Length > 0) lines.Add($"It will cost you {price}.");
        }
        if (lines.Count > 0) await ctx.Say(lines.ToArray());
    }

    /// <summary>"3 points of might, 2 of grace, 2 of will and 3 karma" — the closing step's bill, spelled
    /// out. Gold is included when the step takes any.</summary>
    private static string Price(ArmorStep step)
    {
        var parts = new List<string>();
        if (step.Gold  > 0) parts.Add($"{step.Gold:N0} coins");
        if (step.Might > 0) parts.Add($"{step.Might} point{(step.Might == 1 ? "" : "s")} of might");
        if (step.Grace > 0) parts.Add($"{step.Grace} point{(step.Grace == 1 ? "" : "s")} of grace");
        if (step.Will  > 0) parts.Add($"{step.Will} point{(step.Will == 1 ? "" : "s")} of will");
        if (step.Karma > 0) parts.Add($"{step.Karma} karma");
        return parts.Count switch
        {
            0 => "",
            1 => parts[0],
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
        };
    }

    /// <summary>Snapshot this step's kill counts the first time the player reaches it, so its requirement
    /// counts only what happens after the guildmaster asks.</summary>
    private static void OpenStep(NpcContext ctx, ArmorStep step, string pfx)
    {
        if (ctx.Reg(pfx + "!") != 0) return;
        ctx.SetReg(pfx + "!", 1);
        Resnapshot(ctx, step, pfx);
    }

    private static void Resnapshot(NpcContext ctx, ArmorStep step, string pfx)
    {
        ctx.SetReg(pfx + "@", ctx.TotalKills);
        foreach (var mob in Watched(step)) ctx.SetReg(pfx + mob, ctx.KillCount(mob));
    }

    private static IEnumerable<string> Watched(ArmorStep step) =>
        step.Kills.SelectMany(g => g.Select(r => r.Mob)).Concat(step.Forbid).Distinct();

    /// <summary>Wipe every per-step marker for a finished chain, so a character who somehow runs it again
    /// (a GM stripping the legend, say) starts from clean snapshots rather than stale ones.</summary>
    private static void ClearMarkers(NpcContext ctx, ArmorChain chain)
    {
        for (int i = 0; i < chain.Steps.Length; i++)
        {
            string pfx = $"aq_{chain.Tier}_{i}_";
            ctx.SetReg(pfx + "!", 0);
            ctx.SetReg(pfx + "@", 0);
            foreach (var mob in Watched(chain.Steps[i])) ctx.SetReg(pfx + mob, 0);
        }
    }

    /// <summary>The "you are about to lose these" prompt. RTK's own is a Next/Quit page reading
    /// <i>"((Press \"Next\" ONLY if you are ready to have your items taken.))"</i>; a Yes/No is the same
    /// gesture in a form the 4.95 client renders unambiguously. Asked only once everything needed is
    /// confirmed present, so declining costs nothing and accepting always completes.</summary>
    private static async Task<bool> Confirm(NpcContext ctx, ArmorChain chain, ArmorStep step)
    {
        if (step.IsFinal)
        {
            string armor = chain.ArmorFor(ctx.Sex);
            await ctx.SayItem(armor, "You have persevered through many trials. A mighty reward is almost yours!");
            int yes = await ctx.Menu("You want to wear this armor? It shall cost you some of your abilities and some karma.",
                                     new[] { "Yes, I am ready.", "Not yet." });
            return yes == 1;
        }

        int pick = await ctx.Menu("I will take these from you now — worn or carried. Are you ready?",
                                  new[] { "Yes, take them.", "Not yet." });
        return pick == 1;
    }
}
