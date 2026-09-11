using Shared;

namespace Server;

/// <summary>
/// The Geomancers' <b>Forgotten Past</b> — the level-50 chain that ends in one of the five elemental orbs
/// (Items.csv 26034-26038), chosen by the player and never chosen again. Ported from RTK
/// <c>NPCs/wilderness/rotah.lua</c> (its <c>forgottenPast</c> branch and the <c>forgotten_path</c> half of its
/// <c>onSayClick</c>), <c>NPCs/Common/shaman.lua</c> (the "Storm Shaman" block) and
/// <c>NPCs/Common/smith.lua</c> (its "Gruff Smith" and "Thane's Cave" blocks).
///
/// <para>The chain, in the order a player walks it:</para>
/// <list type="number">
/// <item>Click <b>Rotah</b> in the Wilderness village (NPCs.csv 195, map 1002 at 208/138) and pick
/// "<b>Forgotten past</b>". He talks about the breeze and says nothing useful.</item>
/// <item>Say <b>"sweet summer blossoms"</b>, then ask about <b>"wilderness life"</b>. He is thinking of a
/// woman who left for Buya.</item>
/// <item>Find the <b>Storm Shaman</b> (NPCs.csv 64, map 339, reached from Buya at 125/57 — just north of the
/// Arena, as the Atlas says). Say <b>"wilderness life"</b>, <b>"geomancer"</b>, <b>"elemental orb"</b>. She
/// sends you to a smith.</item>
/// <item>Say <b>"forge metal"</b> to <b>Gruff</b>, the Sanhae smith (NPCs.csv 142, map 1125 off Sanhae
/// Valley). He sends you to Thane.</item>
/// <item>Say <b>"special metal"</b> then <b>"metal orb"</b> to <b>Thane</b> (NPCs.csv 196, map 1144) — he
/// wants <b>5 poor, 5 med and 5 high ore</b>. Bring them and say "metal orb" again for the strange metal,
/// which is a STAGE and not an item ("The special metal is in invisible mark on your character" — Atlas).</item>
/// <item>Back to Rotah: <b>"strange metal"</b>, then <b>"elemental orb"</b>. He wants a <b>Shu jing</b>
/// (Items.csv 12263, bought from the roaming Earths Dragon in the House of Chi) in hand, and asks the five
/// questions out of it.</item>
/// <item>Answer all five, name your element, and pay that orb's materials. You get the orb and the legend —
/// <b>once, ever</b>.</item>
/// </list>
///
/// <para><b>Sources.</b> The best one is a PERIOD TUTOR POST — Head Tutor Nussan, "Quests: Elemental orb
/// quest", Mages board (scraped archive,
/// <c>artifacts/game_data/boards_tutors/by_category/quests.md</c>) — which walks all nineteen steps, gives
/// the five answers in order, and itemizes every orb's cost. nexusatlas.com/quests/geoorbs.php agrees with
/// it line for line and adds the gates ("Level Required: 50", "Prerequisite: None", "Karma Needed: Not
/// applicable") and screenshots of every dialog page, which is where the wording here comes from. RTK agrees
/// with both on structure, stage numbering and costs, and is the only witness for the STAGE MACHINE's shape,
/// so it is what <see cref="Chain"/> is modelled on.</para>
///
/// <para><b>Where the screenshots beat RTK.</b> Three of RTK's lines are mistranscribed and the Atlas
/// captures show what the client actually printed: "I can <i>smell</i> the summer blossoms" (RTK: "spell"),
/// "Ahh <i>yes</i>, the wilderness life" (RTK: "yess"), and "Umm.. <i>whats</i> this you have here?" (RTK
/// repairs the missing apostrophe). The client's own typos are kept — "thats why I came back here", "Oahh".
/// The one page RTK is missing entirely, "I shall ask you a few questions from the Shu jing", is restored
/// from the questionnaire capture.</para>
///
/// <para><b>No karma gate and no path gate</b>, because the Atlas's structured fields say so outright and
/// RTK's own <c>forgottenPast</c> never calls <c>Tools.checkKarma</c> — unusually for that file, whose
/// neighbouring branches do. The <b>level 50</b> gate is the Atlas's; RTK has none.</para>
///
/// <para><b>What this quest cannot buy for you.</b> Four of the materials it asks for have no source in this
/// server yet: <c>ore_high</c> (RTK's mining table only rolls it at apprentice skill and up — we ship its
/// NOVICE row, see game-data/HarvestNodes.csv), <c>metal</c> and <c>hot_coal</c> (smelting), and
/// <c>ice_shard</c>. That is the crafting system's gap, not this quest's: the chain, the costs and the grant
/// are all built and correct, and every one of them starts working the day its material does. Only the
/// <b>Earth</b> orb is payable today, and the ore toll at Thane blocks even that. Logged in
/// docs/common/Deferred-Work.md.</para>
/// </summary>
public static class ForgottenPastQuest
{
    /// <summary>RTK <c>player.quest["forgotten_path"]</c>. Reset to 0 on completion, exactly as RTK does —
    /// <see cref="Legend"/> is what marks it done, and what makes the choice permanent.</summary>
    public const string StageReg = "forgotten_path";

    /// <summary>RTK's legend name, and the one-time gate. Legends persist with the character, so this holds
    /// across relog in a way a quest stage reset to 0 deliberately does not.</summary>
    public const string Legend = "forged_orb";

    /// <summary>RTK's icon/colour. No period screenshot of this mark survives; cosmetic either way (the same
    /// call <see cref="MageStoneQuest"/> and the armor chains made).</summary>
    public const byte LegendIcon = 6, LegendColor = 128;

    /// <summary>nexusatlas.com/quests/geoorbs.php, "Level Required: 50". RTK gates on nothing at all.</summary>
    public const int MinLevel = 50;

    // ---- the four NPCs -----------------------------------------------------------------------------
    // Each identifier below is SHARED (nineteen smiths, ten shamans), so every branch is narrowed to one id
    // here — the same shape SuteQuestAbility and MageStoneAbility use, and the same narrowing RTK gets from
    // its `npc.mapTitle == "..."` tests, since our map names ARE its map titles.

    /// <summary>Rotah, the Wilderness village elder (map 1002 at 208/138). Atlas says 207/139 — same NPC,
    /// one-based.</summary>
    public const int RotahNpcId = 195;
    /// <summary>The Storm Shaman, map 339 ("Storm Shaman"), reached from Buya at 125/57.</summary>
    public const int StormShamanNpcId = 64;
    /// <summary>Gruff, map 1125 ("Gruff Smith") off Sanhae Valley — the Atlas's and the tutor's "Sanhae
    /// smith", and RTK's "Gruff Smith". One smith, three names.</summary>
    public const int SanhaeSmithNpcId = 142;
    /// <summary>Thane, map 1144 ("Thane's Cave"), the mining shop in the Wilderness.</summary>
    public const int ThaneNpcId = 196;

    /// <summary>Every NPC that answers for this quest — what the composition rows in NpcAbilities.csv are
    /// narrowed down to.</summary>
    public static readonly int[] Everyone = { RotahNpcId, StormShamanNpcId, SanhaeSmithNpcId, ThaneNpcId };

    public static bool AnswersFor(NpcDef def) => Array.IndexOf(Everyone, def.Id) >= 0;

    // ---- stages ------------------------------------------------------------------------------------
    // RTK's own numbering, kept so an imported character lands on the step it was already on.

    /// <summary>Not started, or finished (the legend is the real record).</summary>
    public const int NotStarted = 0;
    /// <summary>Clicked "Forgotten past".</summary>
    public const int Asked = 1;
    /// <summary>Told Rotah "sweet summer blossoms".</summary>
    public const int Blossoms = 2;
    /// <summary>Heard Rotah on the wilderness life — she went to Buya.</summary>
    public const int SentToBuya = 3;
    /// <summary>The Storm Shaman has admitted she left the wilderness.</summary>
    public const int ShamanTalking = 4;
    /// <summary>…that she studied under a Geomancer.</summary>
    public const int ShamanNamedRotah = 5;
    /// <summary>…and that a smith is who you want. </summary>
    public const int SentToSmith = 6;
    /// <summary>Gruff has sent you on to Thane.</summary>
    public const int SentToThane = 7;
    /// <summary>Thane has admitted he has the strange metal.</summary>
    public const int ThaneHasMetal = 8;
    /// <summary>Thane wants the fifteen ore.</summary>
    public const int OreOwed = 9;
    /// <summary>Ore paid; you carry the strange metal, which is this stage and nothing else.</summary>
    public const int HasStrangeMetal = 10;
    /// <summary>Rotah has seen the strange metal. "elemental orb" now starts the questions.</summary>
    public const int RotahImpressed = 11;
    /// <summary>All five answered. "elemental orb" now forges.</summary>
    public const int Examined = 12;

    // ---- the linear speech steps -------------------------------------------------------------------

    /// <summary>One rung of the chain: an NPC, a word, the stage it needs, the stage it leaves, and what is
    /// said. Every step that is nothing but "say this here and hear that" lives in this table — which is all
    /// of them but Thane's ore turn-in and Rotah's forge, and those two are the only ones with a branch.
    ///
    /// <para>Speech arrives already trimmed and lower-cased (<c>Session.DispatchSpeech</c>), so
    /// <see cref="Speech"/> is written the way it is compared.</para></summary>
    public sealed record Step(int NpcId, string Speech, int From, int To, string[] Pages);

    /// <summary>The chain in order. Nothing here loops or branches: a step fires once, from exactly one
    /// stage, and a word said out of turn falls through to ordinary speech (which is what RTK does too, and
    /// what the player sees as the NPC ignoring them).</summary>
    public static readonly Step[] Chain =
    {
        new(RotahNpcId, "sweet summer blossoms", Asked, Blossoms, new[]
        {
            "What did you say?\nHmm.. yes sometimes I can smell the summer blossoms on the breeze..",
            "Ohh--that smell reminds me of her. She always smelled of sweet flowers, but sadly she doesn't come around here anymore.",
            "The wilderness life was never one she could stand.",
        }),
        new(RotahNpcId, "wilderness life", Blossoms, SentToBuya, new[]
        {
            "Ahh yes, the wilderness life...\nShe never had many people she could help out here.",
            "She moved to Buya, she kept telling me she only wanted to help others with her healing magic.",
        }),
        new(StormShamanNpcId, "wilderness life", SentToBuya, ShamanTalking, new[]
        {
            "Bah, I hate the wilderness.\n\nI would rather stay here and help others.",
            "There was never anyone I could help out there.",
            "The only reason I was living in the wilderness is because I was studying under a Geomancer.",
        }),
        new(StormShamanNpcId, "geomancer", ShamanTalking, ShamanNamedRotah, new[]
        {
            "Yes, I studied under a Geomancer... my mind fails me, I think his name might have been Rotah.",
            "What is the reason you ask me about my past life though?",
        }),
        new(StormShamanNpcId, "elemental orb", ShamanNamedRotah, SentToSmith, new[]
        {
            "What did you say!?",
            "Were you able to forge the element of metal into an orb?",
            "If you could find a way to forge the element of metal into an orb, I bet that old man Rotah would tell you the way to forge the others.",
            "He would never tell me, thats why I came back here!",
            "Oahh, and you might want to ask a smith for help.",
        }),
        new(SanhaeSmithNpcId, "forge metal", SentToSmith, SentToThane, new[]
        {
            "Yes, I can forge metal.\n\nFor what do you need forged metal?",
            "An orb you say, what kind of orb?",
            "You need some special metal for a magic orb, go talk to my friend Thane in the wilderness, maybe he can help you.",
        }),
        new(ThaneNpcId, "special metal", SentToThane, ThaneHasMetal, new[]
        {
            "You need special metal?",
            "Well... I have this strange metal that I sometimes come across deep in the ground.",
            "It glows this odd blue color.\n\nWhat do you need it for?",
        }),
        new(ThaneNpcId, "metal orb", ThaneHasMetal, OreOwed, new[]
        {
            "Well, I will make you a deal.",
            "If you can gather me 5 poor, medium, and high ore, I'll give you this here strange metal.",
            "Sound like a deal?\n\nLet me know when you have all the ore.",
        }),
        new(RotahNpcId, "strange metal", HasStrangeMetal, RotahImpressed, new[]
        {
            "Umm.. whats this you have here?",
            "Wow.. this is some strange metal you have.\n\nYou say it will make a Metal orb?",
            "Wow, really? I must tell my brothers and sisters!",
        }),
    };

    /// <summary>The rung this NPC, word and stage sit on, or null if none — the whole linear half of the
    /// stage machine, and the reason it is testable without a session.</summary>
    public static Step? Advance(int npcId, string speech, int stage) =>
        Chain.FirstOrDefault(s => s.NpcId == npcId && s.Speech == speech && s.From == stage);

    // ---- Thane's toll ------------------------------------------------------------------------------

    /// <summary>"5 ore [poor], 5 ore [med], and 5 ore [high]" (tutor step 13; RTK removes exactly these).
    /// Spent, not shown.</summary>
    public static readonly (string Key, int Count)[] OreToll =
        { ("ore_poor", 5), ("ore_med", 5), ("ore_high", 5) };

    // ---- Rotah's examination -----------------------------------------------------------------------

    public const string ShuJing = "shu_jing";   // Items.csv 12263 — shown, never taken

    /// <summary>The five questions from the Shu jing and their answers, in order, from the Atlas's
    /// questionnaire capture. RTK asks the same five in the same order; the capitalisation of "Kan trigram"
    /// is the screenshot's.</summary>
    public static readonly (string Question, string Answer)[] Questions =
    {
        ("Which element is the beginning of new life?", "wood"),
        ("Which element represents the Kun trigram?", "earth"),
        ("Which element contains the most Yang?", "fire"),
        ("Which element represents the Kan trigram?", "water"),
        ("Which element is the most commonly used remedy for the negative Earth energies?", "metal"),
    };

    // ---- the five orbs -----------------------------------------------------------------------------

    /// <summary>One orb: the element the player types, the item they get, the offer they are read, and what
    /// it costs.</summary>
    public sealed record OrbRecipe(string Element, string Title, string Item, string Offer,
                                   (string Key, int Count)[] Cost);

    /// <summary>The five recipes. Costs are the tutor post's, which the Atlas's per-orb captures match
    /// exactly and RTK's <c>mats</c>/<c>amts</c> arrays match too — three independent sources agreeing, which
    /// is rare enough in this project to be worth saying.
    ///
    /// <para><b>"25 wood" is Ginko wood here, not RTK's <c>wood_scraps</c>.</b> Both sources say plain
    /// "wood" and no item in either item table is called that, so it is a judgement call either way. RTK's
    /// pick is visibly contaminated by its own fork: <c>wood_scraps</c> is what RTK's 7.x woodworking system
    /// hands back on a FAILED craft (Crafting/woodworking.lua) and has no other source in the game. Ginko
    /// wood is what a 4.95 player gets by cutting a tree (game-data/HarvestNodes.csv), and is already what
    /// the Nagnang tall shield asks for by the same loose name.</para></summary>
    public static readonly OrbRecipe[] Orbs =
    {
        new("wood", "Wood", "wood_orb",
            "To make a Wood orb I will need 25 wood, a star drop, and 2 yellow ambers.\n\nAre you ready to trade?",
            new[] { ("ginko_wood", 25), ("stardrop", 1), ("yellow_amber", 2) }),
        new("earth", "Earth", "earth_orb",
            "To make an Earth orb I will need 25 poor ore, a star drop, and 2 yellow ambers.\n\nAre you ready to trade?",
            new[] { ("ore_poor", 25), ("stardrop", 1), ("yellow_amber", 2) }),
        new("fire", "Fire", "fire_orb",
            "To make a Fire orb I will need 10 hot coal, a star drop, and 2 yellow ambers.\n\nAre you ready to trade?",
            new[] { ("hot_coal", 10), ("stardrop", 1), ("yellow_amber", 2) }),
        new("water", "Water", "water_orb",
            "To make a Water orb I will need 2 Ice shards, 5 star drops, and 2 yellow ambers.\n\nAre you ready to trade?",
            new[] { ("ice_shard", 2), ("stardrop", 5), ("yellow_amber", 2) }),
        new("metal", "Metal", "metal_orb",
            "To make a Metal orb I will need 10 Metal, a star drop, and 2 yellow ambers.\n\nAre you ready to trade?",
            new[] { ("metal", 10), ("stardrop", 1), ("yellow_amber", 2) }),
    };

    /// <summary>The recipe a typed element names, or null. Case and surrounding space are the player's
    /// problem, not theirs.</summary>
    public static OrbRecipe? OrbFor(string? answer) =>
        answer is null ? null
        : Orbs.FirstOrDefault(o => o.Element.Equals(answer.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Is a typed answer the one the Shu jing gives? Same leniency as <see cref="OrbFor"/>.</summary>
    public static bool AnswerIs(string? typed, string expected) =>
        typed is not null && typed.Trim().Equals(expected, StringComparison.OrdinalIgnoreCase);

    // ---- what Rotah says on his own ----------------------------------------------------------------

    /// <summary>"Forgotten past" — three pages, split as the Atlas capture shows them.</summary>
    public static readonly string[] Opening =
    {
        "You wish to know about my past?",
        "Why would you ask such a silly question?\n\nI am just an old man, nothing has ever really happened to me.",
        "I just sit here, listening to the whispers in the breeze...",
    };

    /// <summary>What he says to "elemental orb" once he has seen the strange metal — whether or not the Shu
    /// jing is in the pack. RTK reads it out in both cases and only then checks, which is how the player is
    /// told what to go and buy.</summary>
    public static readonly string[] BringTheShuJing =
    {
        "Hmm.. well seeing as you helped myself and the Geomancers to make the final orb, perhaps I can help you craft one.",
        "First you must show me how much you know of my teachings...\n\nOtherwise how else could I trust you?",
        "First bring me a Shu jing, and when you do I will ask you five questions, one for every element there is.",
    };
}

/// <summary>
/// The whole chain, in one ability composed onto all four of its NPCs (game-data/NpcAbilities.csv:
/// <c>RotahNpc</c>, <c>ShamanNpc</c>, <c>SmithNpc</c>) and narrowed by NPC id inside — see
/// <see cref="ForgottenPastQuest.Everyone"/>. One class because it is one conversation: the stage the shaman
/// leaves is the stage the smith needs, and splitting it across four files would only hide that.
///
/// <para>Rotah gets the one click entry; everything else is spoken, which is what the whole quest is made of.
/// <b>Rotah had no composition row at all before this</b>, which meant clicking him opened nothing — the
/// silent-NPC failure this codebase keeps re-learning, and the reason <c>ForgottenPastQuestTests</c>
/// asserts the composition rather than trusting it.</para>
/// </summary>
public sealed class ForgottenPastAbility : INpcAbility, INpcSayHandler
{
    public static readonly ForgottenPastAbility Instance = new();

    /// <summary>Rotah's "Forgotten past" — RTK's own label, capitalised as the Atlas capture shows it.
    /// Shown to everyone, always: the level gate and the already-forged refusal are both SPOKEN, because a
    /// hidden entry is indistinguishable from a quest that does not exist.</summary>
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        if (ctx.Def.Id != ForgottenPastQuest.RotahNpcId) yield break;
        yield return ("Forgotten past", Open);
    }

    private static async Task Open(NpcContext ctx)
    {
        if (ctx.HasLegend(ForgottenPastQuest.Legend))
        {
            await ctx.Say("You have already forged an orb.");
            return;
        }
        if (ctx.Level < ForgottenPastQuest.MinLevel)
        {
            // Authored: no source records what he tells someone too young, because the Atlas's walkthrough
            // starts from a character who already qualifies. Level 50 is the Atlas's gate all the same.
            await ctx.Say("The whispers in the breeze are not for the young. Live a while longer, and come back to me.");
            return;
        }

        await ctx.Say(ForgottenPastQuest.Opening);
        if (ctx.Stage(ForgottenPastQuest.StageReg) == ForgottenPastQuest.NotStarted)
            ctx.SetStage(ForgottenPastQuest.StageReg, ForgottenPastQuest.Asked);
    }

    public async Task<bool> OnSay(NpcContext ctx, string speech)
    {
        if (!ForgottenPastQuest.AnswersFor(ctx.Def)) return false;
        if (ctx.HasLegend(ForgottenPastQuest.Legend)) return false;   // done forever — nothing here listens

        int stage = ctx.Stage(ForgottenPastQuest.StageReg);

        // The linear half: one table lookup, then say it and move on.
        if (ForgottenPastQuest.Advance(ctx.Def.Id, speech, stage) is { } step)
        {
            ctx.SetStage(ForgottenPastQuest.StageReg, step.To);
            await ctx.Say(step.Pages);
            return true;
        }

        // Thane, with the ore in hand.
        if (ctx.Def.Id == ForgottenPastQuest.ThaneNpcId && speech == "metal orb"
            && stage == ForgottenPastQuest.OreOwed)
        {
            await OreTurnIn(ctx);
            return true;
        }

        // Rotah, once he has seen the metal: the examination, then the forge.
        if (ctx.Def.Id == ForgottenPastQuest.RotahNpcId && speech == "elemental orb")
        {
            if (stage == ForgottenPastQuest.RotahImpressed) { await Examine(ctx); return true; }
            if (stage == ForgottenPastQuest.Examined)       { await Forge(ctx);   return true; }
        }

        return false;
    }

    /// <summary>"Return to Thane, and say 'Metal orb', Choose Yes if you're ready to trade" — the tutor's
    /// step 14, which is where the confirm comes from; RTK takes the ore with no question asked.</summary>
    private static async Task OreTurnIn(NpcContext ctx)
    {
        foreach (var (key, count) in ForgottenPastQuest.OreToll)
            if (ctx.CountItem(key) < count)
            {
                await ctx.Say("Well... where is it?");
                return;
            }

        int choice = await ctx.Menu("Are you ready to trade?", new[] { "Yes", "No" });
        if (choice != 1) return;

        // Re-checked after the prompt: the menu is a round trip, and a trade window can empty a pack inside it.
        foreach (var (key, count) in ForgottenPastQuest.OreToll)
            if (ctx.CountItem(key) < count)
            {
                await ctx.Say("Well... where is it?");
                return;
            }

        foreach (var (key, count) in ForgottenPastQuest.OreToll) ctx.TakeItem(key, count);
        ctx.SetStage(ForgottenPastQuest.StageReg, ForgottenPastQuest.HasStrangeMetal);
        // Nothing is handed over: "The special metal is in invisible mark on your character... Its not an
        // actual item" (Atlas). The stage IS the metal.
        await ctx.Say("Thanks, here you can have this strange metal. Good luck!");
    }

    /// <summary>The five questions out of the Shu jing. The book has to be in the pack and is never taken —
    /// "It will not be taken from you but you will be asked some questions from inside it" (Atlas).</summary>
    private static async Task Examine(NpcContext ctx)
    {
        await ctx.Say(ForgottenPastQuest.BringTheShuJing);
        if (!ctx.HasItem(ForgottenPastQuest.ShuJing)) return;

        await ctx.Say("I shall ask you a few questions from the Shu jing");

        foreach (var (question, answer) in ForgottenPastQuest.Questions)
        {
            string? typed = await ctx.Input(question);
            if (!ForgottenPastQuest.AnswerIs(typed, answer))
            {
                // RTK ends the conversation on a wrong answer without a word. A closed box and no reply reads
                // as a bug rather than a failure, so one authored status line says what happened; the question
                // set is unchanged and the player can simply ask again.
                ctx.Notify("Rotah shakes his head, and says no more.");
                return;
            }
        }

        ctx.SetStage(ForgottenPastQuest.StageReg, ForgottenPastQuest.Examined);
        await ctx.Say("Correct!");
    }

    /// <summary>Name an element, pay for it, keep it forever.</summary>
    private static async Task Forge(NpcContext ctx)
    {
        string? typed = await ctx.Input("Which type of orb would you like to make?");
        var orb = ForgottenPastQuest.OrbFor(typed);
        if (orb is null)
        {
            if (typed is not null) ctx.Notify("Rotah shakes his head, and says no more.");
            return;
        }

        int choice = await ctx.Menu(orb.Offer, new[] { "Yes", "No" });
        if (choice != 1) return;

        foreach (var (key, count) in orb.Cost)
            if (ctx.CountItem(key) < count)
            {
                await ctx.Say("You do not have all the required items.");
                return;
            }

        // Room checked BEFORE anything is spent — the materials for an orb are a long walk, and a full pack
        // must not eat them (same order NagnangTallShieldAbility uses).
        if (ctx.FreeSlotCount < 1)
        {
            await ctx.Say("You have no room to carry it. Come back with an emptier pack.");
            return;
        }

        foreach (var (key, count) in orb.Cost) ctx.TakeItem(key, count);
        ctx.GiveItem(orb.Item, 1);

        // The legend, and with it the gate: the stage goes back to 0 (RTK does the same), so from here the
        // ONLY record that this happened is a mark that persists with the character.
        ctx.AddLegend($"Forged an orb of {orb.Title} ({Character.GameDate})", ForgottenPastQuest.Legend,
                      ForgottenPastQuest.LegendIcon, ForgottenPastQuest.LegendColor);
        ctx.SetStage(ForgottenPastQuest.StageReg, ForgottenPastQuest.NotStarted);
    }
}
