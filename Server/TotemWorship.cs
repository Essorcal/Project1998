using Shared;

namespace Server;

/// <summary>
/// Worshipping a totem animal at its Wilderness shrine — the "Worship &lt;Name&gt;" entry on each of the four
/// shrine NPCs (Baekho 388, Chung ryong 389, Hyun Moo 390, Ju jak 391), ported from RTK
/// <c>NPCs/Common/totem_npc.lua</c>'s <c>_worship</c>. Worshipping sets your totem — which sets your totem
/// time, and with it your experience bonus window (<see cref="Content.IsTotemTime"/>) — and it is what the
/// <b>Poet Sun armor</b> chain's fourth step counts.
///
/// <para><b>The offering.</b> That totem's own key, or five gold acorns; but if you ALREADY worship this
/// totem, reaffirming costs a single gold acorn. All three period sources agree, and the third explains the
/// second: Atlas says <i>"For each you need a totem key… You can use 5 gold acorns instead of keys"</i>,
/// while tswolf's odd-sounding <i>"This can be done by having all 4 Mythic Keys or 3 and a Gold Acorn for
/// your totem"</i> is exactly this rule — three keys for the three totems that are not yours, and the cheap
/// single acorn for the one that is.</para>
///
/// <para><b>Once per 21 real hours</b>, shared across all four shrines (Atlas: "This can only be done once
/// every 21 hours so this will take you several days"; RTK's own dialog calls the same window "every 7 days
/// ((21 hours))", the game-days/real-hours pairing this project's calendar uses everywhere).</para>
///
/// <para><b>Karma</b> comes at RTK's 1-in-5 chance with a pity counter that forces it on the fifth miss —
/// a quarter point for a key or five acorns, an eighth for the single-acorn reaffirmation. Only RTK
/// witnesses the odds; the period pages say only that "frequent worship of your totem animal will doubtless
/// improve your Karma", which the mechanic satisfies either way.</para>
///
/// <para>NOT ported from the same script: the "Totem Animals" lore menu, the totem-helmet forging quest, and
/// the Leviathan "Forgive" branch (see <see cref="LeviathanQuest"/>) — each is its own feature.</para>
/// </summary>
public static class TotemWorship
{
    /// <summary>Shrine NpcId → totem index (0 Ju Jak · 1 Baekho · 2 Hyun Moo · 3 Chung Ryong), RTK's
    /// <c>_totemIndexByName</c>. The four totem PRIESTS (NPCs 94-97, identifier <c>TotemNpc</c>) are a
    /// different set and take no worship — the shrines are the animals themselves.</summary>
    public static readonly IReadOnlyDictionary<int, int> Shrines = new Dictionary<int, int>
    {
        [388] = 1,   // Baekho      (map 1406)
        [389] = 3,   // Chung ryong (map 1401)
        [390] = 2,   // Hyun Moo    (map 1416)
        [391] = 0,   // Ju jak      (map 1411)
    };

    /// <summary>Unix seconds before which no shrine will accept another offering.</summary>
    public const string TimerReg = "totem_worship_daily_timer";
    /// <summary>Consecutive worships that missed the karma roll; the fifth is granted outright.</summary>
    public const string PityReg = "totem_worship_karma_force";
    /// <summary>Lifetime worships, for a future "devout" check. RTK keeps the same tally.</summary>
    public const string CountReg = "totem_total_worships";

    public const long CooldownSeconds = 75600;   // 21 real hours

    /// <summary>Totem index → the key item that totem accepts.</summary>
    public static string KeyFor(int totem) => totem switch
    {
        0 => "ju_jak_key", 1 => "baekho_key", 2 => "hyun_moo_key", _ => "chung_ryong_key",
    };

    /// <summary>The Poet Sun chain's four-totem sequence, in the order the guildmaster names it:
    /// Chung ryong, then Baekho, then Ju Jak, then Hyun moo. Worshipping the NEXT one in this list advances
    /// the counter; worshipping any other leaves it where it is (it does not reset — no source says a
    /// mis-step costs you the run, and at 21 hours a turn that would be a brutal invention).</summary>
    public static readonly int[] PoetSunOrder = { 3, 1, 0, 2 };

    /// <summary>Advance the Poet Sun totem counter if <paramref name="totem"/> is the one due next.
    /// Runs on every worship regardless of path — the counter is meaningless to anyone else, and gating it
    /// on being a poet mid-chain would silently punish a poet who worshipped before starting Sun.</summary>
    internal static void NoteWorship(NpcContext ctx, int totem)
    {
        int done = ctx.Reg(ArmorQuest.TotemStepReg);
        if (done >= 0 && done < PoetSunOrder.Length && PoetSunOrder[done] == totem)
            ctx.SetReg(ArmorQuest.TotemStepReg, done + 1);
    }
}

/// <summary>The "Worship &lt;Name&gt;" menu entry on the four shrine animals. See <see cref="TotemWorship"/>.</summary>
public sealed class TotemWorshipAbility : INpcAbility
{
    public static readonly TotemWorshipAbility Instance = new();

    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        if (!TotemWorship.Shrines.TryGetValue(ctx.Def.Id, out int totem)) yield break;
        yield return ($"Worship {Content.TotemName(totem)}", c => Worship(c, totem));
    }

    private static async Task Worship(NpcContext ctx, int totem)
    {
        string name = Content.TotemName(totem);

        if (ctx.NowUnix < ctx.Reg(TotemWorship.TimerReg))
        { await ctx.Say("You have worshipped a totem animal within the last 7 days ((21 hours))."); return; }

        if (await ctx.Menu($"Do you wish to worship {name}?", new[] { "Yes", "No" }) != 1)
        { await ctx.Say("Then begone with you."); return; }

        // Already this totem's follower -> the cheap reaffirmation, offered without a choice (RTK's own
        // `if player.totem ~= totemIndex` skips the menu entirely for it).
        string item; int count; double karma;
        if (ctx.Totem == totem)
        {
            (item, count, karma) = ("gold_acorn", 1, 0.125);
        }
        else
        {
            int pick = await ctx.Menu($"You must prove your devotion to {name}.  Which do you offer?",
                new[] { $"I offer a {name} key.", "I offer five Gold acorns.", "I have nothing to offer." });
            if (pick == 1)      (item, count, karma) = (TotemWorship.KeyFor(totem), 1, 0.25);
            else if (pick == 2) (item, count, karma) = ("gold_acorn", 5, 0.25);
            else { await ctx.Say("Then begone with you."); return; }
        }

        if (ctx.CountItem(item) < count)
        { await ctx.Say($"Return when you have the {(count > 1 ? $"{count} {ctx.ItemName(item)}s" : ctx.ItemName(item))}."); return; }

        ctx.TakeItem(item, count);
        ctx.SetReg(TotemWorship.TimerReg, (int)(ctx.NowUnix + TotemWorship.CooldownSeconds));

        // RTK's 1-in-5, with the pity counter that guarantees it on the fifth consecutive miss.
        if (ctx.Random(5) == 1 || ctx.Reg(TotemWorship.PityReg) >= 5)
        { ctx.AddKarma(karma); ctx.SetReg(TotemWorship.PityReg, 0); }
        else ctx.SetReg(TotemWorship.PityReg, ctx.Reg(TotemWorship.PityReg) + 1);

        TotemWorship.NoteWorship(ctx, totem);
        ctx.SetTotem(totem);
        ctx.SetReg(TotemWorship.CountReg, ctx.Reg(TotemWorship.CountReg) + 1);
        ctx.Notify($"You worship the mighty {name}.");
        await ctx.Say($"{name} accepts your devotion.");
    }
}
