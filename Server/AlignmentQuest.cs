namespace Server;

/// <summary>
/// The three sub-alignment shrines (RTK <c>NPCs/Common/alignment.lua</c>, the <c>AlignmentNpc</c> that stands
/// in each of the Kwi-sin / Ming-ken / Ohaeng Shrine maps). Clicking the shaman runs a two-question catechism;
/// pass it and confirm "for life" and you devote to that nature — which rebuilds your spellbook into that
/// alignment's parallel spell set and stamps a "&lt;Align&gt; &lt;Class&gt; since" legend
/// (<see cref="Session.SwapAlignment"/>).
///
/// <para>Which shrine this is comes from the map the NPC stands on — RTK branches on <c>npc.mapTitle</c>; the
/// three shrine maps are fixed (<see cref="Shrines"/>), so we key on <see cref="NpcContext.Def"/>.Map, which
/// is the same answer without a name lookup. The level-50 gate, the wrong-shrine / already-devoted branches
/// and every line of dialog are RTK's verbatim.</para>
///
/// <para>The 32-tile route into each shrine already exists in the world (Arctic Village → 324, Islets 1008 →
/// 325, Wilderness 1002 → 326); this only wires up the shaman once you are standing in front of it.</para>
/// </summary>
public sealed class AlignmentAbility : INpcAbility
{
    public static readonly AlignmentAbility Instance = new();

    // Shrine map id -> (alignment id 1-3, the shaman's own name for the nature). RTK's shrine strings:
    // "Kwi-Sin" / "Ming-ken" / "Ohaeng" (note the casing — kept verbatim because the dialog interpolates it).
    private static readonly IReadOnlyDictionary<int, (int Align, string Shrine)> Shrines =
        new Dictionary<int, (int, string)>
        {
            [324] = (1, "Kwi-Sin"),
            [325] = (2, "Ming-ken"),
            [326] = (3, "Ohaeng"),
        };

    // One entry, so a click dives straight into the catechism (the picker only appears for real choices) —
    // matching RTK, where alignment.lua's whole flow is the `click` handler.
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        if (Shrines.ContainsKey(ctx.Def.Map)) yield return ("Speak", Speak);
    }

    private static async Task Speak(NpcContext ctx)
    {
        if (!Shrines.TryGetValue(ctx.Def.Map, out var s)) return;
        int alignCheck = s.Align;
        string shrine = s.Shrine;

        if (ctx.Level < 50)
        {
            await ctx.Say("You are not ready to make this choice. Return later.");
            return;
        }

        if (ctx.Alignment != 0)
        {
            await ctx.Say(ctx.Alignment != alignCheck
                ? $"You are not {shrine}, I can not help you."
                : $"Go now young one, and tell your guild master of your choice to be {shrine}");
            return;
        }

        await ctx.Say(
            "Greetings, magic user.",
            "You seek to find your true nature?",
            "First I must know that you understand the nature of your soul.");

        // Q1: "how many natures?" — the correct answer is always the third (three), but each shrine phrases
        // the third option in terms of the OTHER two natures (RTK builds the list per shrine).
        string third = shrine switch
        {
            "Kwi-Sin"  => "Three, you, Ming-Ken, and Ohaeng.",
            "Ming-ken" => "Three, you, Ohaeng, and Kwi-Sin.",
            _          => "Three, you, Ming-Ken, and Kwi-Sin.",   // Ohaeng
        };
        int q1 = await ctx.Menu("How many natures are there to choose from?",
            new[] { "Only one, and that is you.", "Two, you and Ming-Ken", third });
        if (q1 != 3)   // 1, 2 or cancel
        {
            if (q1 == 1 || q1 == 2)
                await ctx.Say("You are incorrect, there are three. Learn about yourself, and of the natures " +
                              "before you go too far. You may only choose once, ever!");
            return;
        }

        await ctx.Say("Yes, yes. Wise one, you are learning about the natures. But there is more to know " +
                      "before you dedicate to one for life.");

        // Q2: which aspect is this nature? Same three options every shrine; the right one differs
        // (Kwi-Sin = afterlife, Ming-ken = living, Ohaeng = balance).
        int correct = shrine switch { "Kwi-Sin" => 3, "Ming-ken" => 1, _ => 2 };
        int q2 = await ctx.Menu($"Each nature represents a different aspect of power, which does {shrine} represent?",
            new[]
            {
                $"{shrine} is the nature of living.",
                $"{shrine} is the balance of all.",
                $"{shrine} is the nature of the afterlife.",
            });
        if (q2 != correct)
        {
            await ctx.Say(
                "You are incorrect, you need to learn of the natures.",
                "Ming-Ken is the nature of the living.",
                "Ohaeng is the balance of all.",
                "Kwi-Sin is the nature of the afterlife.",
                "Go and study more before you take this step.");
            return;
        }

        await ctx.Say($"Yes, you do understand. You are on your way to becoming {shrine}");

        int confirm = await ctx.Menu(
            "This is your last chance, from here there is no turning back, you may only choose once in your life.",
            new[]
            {
                $"I do not wish to be {shrine}",
                "I need to think first.",
                "I want another nature.",
                "I will dedicate for life.",
            });
        if (confirm != 4)   // 1-3 back out, cancel too
        {
            if (confirm >= 1 && confirm <= 3)
                await ctx.Say("It is wise that you do not leap into something that will affect you for the rest of your life.");
            return;
        }

        ctx.Devote(alignCheck);
        await ctx.Say($"You are now devoted to the nature of {shrine}, tell your Guildmaster of your decision.");
    }
}

/// <summary>
/// The Tiger Palace summit shaman (RTK <c>NPCs/arctic/tiger_palace/summit.lua</c>, <c>SummitNpc</c> in the Sun
/// Heart room) — the ONLY way to renounce a sub-alignment. Speech-triggered, not a menu: stand in front of it
/// and say "cleanse curse" (or "cleanse"). It exacts a permanent price — RTK docks 10,000 base vita and 5,000
/// base mana — then returns you to Unaligned, which rebuilds your spellbook out of the aligned set and clears
/// the alignment legend (<see cref="Session.SwapAlignment"/>).
///
/// <para>Gated exactly as RTK: level 99, at least 20,000 base vita and 10,000 base mana, and currently aligned
/// (there is nothing to cleanse otherwise). Reaching the Sun Heart room is its own Tiger Palace trigram-key
/// puzzle in the world; this only wires up the shaman waiting at the end of it.</para>
/// </summary>
public sealed class SummitAbility : INpcAbility, INpcSayHandler
{
    public static readonly SummitAbility Instance = new();

    // Speech-only, like RTK (summit.lua has no `click`): clicking does nothing, you speak to it.
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx) => NoClickMenu.None;

    public async Task<bool> OnSay(NpcContext ctx, string speech)
    {
        if (speech != "cleanse curse" && speech != "cleanse") return false;

        // Below minimum stats, or nothing to cleanse (already unaligned). RTK checks baseHealth/baseMagic,
        // which are our base MaxHp/MaxMp pools (gear/buffs sit on top and don't count here).
        if (ctx.Level < 99 || ctx.MaxHp < 20000 || ctx.MaxMp < 10000 || ctx.Alignment == 0)
        {
            await ctx.Say("I am unable to help you.");
            return true;
        }

        await ctx.Say(
            "Hahaha.. so the great one returns to me, does he master?",
            "Got yourself into a spot of trouble, so you come running back to me?",
            "Yes, from this level of \"life\" I can see into your soul, I can see your soul is not pure.",
            "I take it you want me to help you again, after what you did to me?");

        int choice = await ctx.Menu(
            "Well, do you really want me to help you? You know it will cost you greatly.",
            new[] { "Yes, I need help.", "No, I need no help." });

        if (choice == 1)
        {
            ctx.LowerMaxHp(10000);   // permanent — RTK player.baseHealth -= 10000
            ctx.LowerMaxMp(5000);    // permanent — RTK player.baseMagic  -=  5000
            ctx.Devote(0);           // back to Unaligned: rebuilds the book, clears the alignment legend
            await ctx.Say("You are now unaligned.");
        }
        else if (choice == 2)
        {
            await ctx.Say("Come back to me if you change your mind.");
        }
        return true;
    }
}
