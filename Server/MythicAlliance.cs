using Shared;

namespace Server;

/// <summary>
/// The twelve <b>lesser alliances</b> with the mythic animals — the zodiac's private war, fought by proxy.
/// Each of the twelve caves is sworn against exactly one other (Rat↔Horse, Dragon↔Dog, Rabbit↔Rooster,
/// Snake↔Pig, Sheep↔Ox, Tiger↔Monkey), and each keeps a chamber past its guard room where the animal itself
/// waits. Say the name of its <i>enemy</i> to it and it offers you a favour: kill three of each of that
/// enemy's two leaders, bring back a tribute stolen from their cave, and you are its ally for good.
///
/// <para><b>The whole quest runs on one word.</b> There is no menu entry — Atlas is explicit that you
/// "approach the black gate and say the name of the enemy of the cave", and you say it again to hand in and
/// again ever after to be healed. So this is an <see cref="INpcSayHandler"/> only; clicking a mythic still
/// does nothing, exactly as clicking any other creature does (see the click-mob no-op rule).</para>
///
/// <para><b>What makes it hard is not the bosses.</b> It is the KILL TRACK — see
/// <see cref="Session.TrackedKills"/> and <see cref="Character.KillTrack"/>. The real Nexus remembered only
/// the last eight KINDS of creature you had killed, so "you must avoid killing anything else" was never a
/// rule the NPC enforced; it was arithmetic. Two of your eight slots hold the bosses, which leaves six for
/// everything else you meet on the way back, and a ninth kind pushes your oldest boss (and its count) off
/// the end. Accepting an alliance wipes the track, which is why the tutors all say to start every alliance
/// you intend to run BEFORE killing anything for any of them, and why four at a time is the ceiling. All of
/// that behaviour falls out of the eight-slot list; none of it is special-cased here.</para>
///
/// <para><b>Sources.</b> Nexus Atlas's twelve alliance pages give the shape and the five rewards, its
/// Alliance Tips page (Moraghul) gives the kill track, and Head Tutor Nussan's walkthrough is the eyewitness
/// that itemises the reward — "1 Mythic favour, 10 million exp, 3 Karma points, Legend mark" — and supplies
/// the punishment ("it will zap you and send you to tavern"). RTK's <c>mythic_alliance_npc.lua</c> is used
/// for <b>prose only</b>, on the standing rule that the archive outranks it: the mythics' lines survive
/// nowhere else. Where the two disagree on numbers, see <c>docs/common/Mythic-Alliances.md</c> — there are
/// exactly two such places, and both went to the archive.</para>
///
/// <para>Greater alliances are NOT implemented. They need Ee San, six lesser alliances, and a
/// thirty-boss sweep across three caves; see docs/common/Deferred-Work.md.</para>
/// </summary>
public sealed record MythicAllianceDef(
    string Animal, int NpcId, string Enemy,
    string[] KeyBosses, string[] ItemBosses,
    string KeyDrop, int KeyTribute, string ItemDrop, int ItemTribute,
    string Favor, uint Exp, double Karma, string Sources)
{
    /// <summary>Lower-case animal name: the word a visitor says, and the suffix of every key below.</summary>
    public string Key => Animal.ToLowerInvariant();

    /// <summary>Quest-registry stage for THIS animal's alliance: 0 not started, 1 accepted. RTK's own
    /// <c>player.quest["lesser_alliance_dog"]</c> name, so an imported character keeps its place.</summary>
    public string QuestKey => "lesser_alliance_" + Key;

    /// <summary>Legend mark granted on completion. Same name as the quest key — RTK uses one string for
    /// both, and the pair is never live at once (the legend replaces the stage).</summary>
    public string LegendKey => "lesser_alliance_" + Key;

    /// <summary>The legend a GREATER alliance with this animal would grant. Nothing awards it yet, but the
    /// enemy-ally check reads it: a champion of the Dog must be turned away by the Dragon whether their mark
    /// says lesser or greater, and having the name here means adding greater alliances later cannot
    /// accidentally leave that hole open.</summary>
    public string GreaterLegendKey => "greater_alliance_" + Key;

    /// <summary>The three (key boss, item boss) pairs, cave 1 → 3. An ally must take three of BOTH halves of
    /// ONE pair: the pairs are the cave tiers, and the tiers are level-banded, so in practice the pair you
    /// can reach is the pair you fight.</summary>
    public IEnumerable<(string KeyBoss, string ItemBoss)> BossPairs()
    {
        for (int t = 0; t < KeyBosses.Length && t < ItemBosses.Length; t++)
            yield return (KeyBosses[t], ItemBosses[t]);
    }
}

/// <summary>Registry + rules for <see cref="MythicAllianceDef"/>. The rows live in
/// game-data/MythicAlliances.csv (hot-reloads with <c>@reload</c>).</summary>
public static class MythicAlliance
{
    /// <summary>Three of each of the enemy's two leaders. Atlas: "Slay 3 &lt;enemy&gt; key bosses and 3
    /// &lt;enemy&gt; item bosses"; Nussan: "killing 3 of each boss of his mythic enemy".</summary>
    public const int Required = 3;

    /// <summary>Legend glyph + colour. RTK's values (icon 5, colour 128) and the only witness for them —
    /// no period page records the glyph. Cosmetic; matches the other quest marks on this server.</summary>
    public const byte LegendIcon = 5;
    public const byte LegendColor = 128;

    /// <summary>The alliance whose chamber this NPC stands in, or null for any other NPC.</summary>
    public static MythicAllianceDef? ByNpc(int npcId)
    {
        foreach (var a in Content.MythicAlliances) if (a.NpcId == npcId) return a;
        return null;
    }

    /// <summary>Look one up by animal name, case-insensitively (how a row names its enemy, and how a player
    /// says it out loud).</summary>
    public static MythicAllianceDef? ByName(string? animal)
    {
        if (string.IsNullOrWhiteSpace(animal)) return null;
        var want = animal.Trim();
        foreach (var a in Content.MythicAlliances)
            if (string.Equals(a.Animal, want, StringComparison.OrdinalIgnoreCase)) return a;
        return null;
    }
}

/// <summary>
/// The mythic animal itself, answering to the name of its enemy. See <see cref="MythicAllianceDef"/> for what
/// the quest is and where it comes from; this is the conversation.
/// </summary>
public sealed class MythicAllianceAbility : INpcAbility, INpcSayHandler
{
    public static readonly MythicAllianceAbility Instance = new();

    /// <summary>No click entry. The gate is opened by speaking, and a mythic that offered a menu would be
    /// the only creature on the server that did.</summary>
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx) => NoClickMenu.None;

    public async Task<bool> OnSay(NpcContext ctx, string speech)
    {
        var mine = MythicAlliance.ByNpc(ctx.Def.Id);
        if (mine is null) return false;
        var foe = MythicAlliance.ByName(mine.Enemy);
        if (foe is null) return false;

        // Only its enemy's NAME opens this. Anything else the player says near a mythic falls through to
        // ordinary chat — including "greater", which is the greater alliance's word and has no handler yet.
        if (!string.Equals((speech ?? "").Trim(), foe.Animal, StringComparison.OrdinalIgnoreCase)) return false;

        if (ctx.KarmaTooLow()) return true;

        // ---- sworn to the other side ------------------------------------------------------------
        // "If you try to do an alliance with the enemy of an animal you already alli'ed with, it will zap
        // you and send you to tavern" (Nussan). The dialog plays first so the line is read before the walk
        // home; RTK banishes first and speaks after, which on this server would talk to an empty chamber.
        if (ctx.HasLegend(foe.LegendKey) || ctx.HasLegend(foe.GreaterLegendKey))
        {
            await ctx.Say("You are allied with our enemies. Die scum!");
            ctx.CastStormstrikeAndBanish();
            return true;
        }

        // ---- already ours: the standing reward ---------------------------------------------------
        if (ctx.HasLegend(mine.LegendKey) || ctx.HasLegend(mine.GreaterLegendKey))
        {
            ctx.CastRebirth();
            return true;
        }

        if (ctx.Stage(mine.QuestKey) == 1) await TurnIn(ctx, mine, foe);
        else                               await Offer(ctx, mine, foe);
        return true;
    }

    // =============================================================================================
    // Offering it
    // =============================================================================================
    private static async Task Offer(NpcContext ctx, MythicAllianceDef mine, MythicAllianceDef foe)
    {
        int choice = await ctx.Menu(
            $"Greetings, mortal. Auspicious timing. Will you ally yourself with the mighty {mine.Animal}?",
            new[] { "With honor.", "I withhold my allegiance." });

        if (choice != 1)
        {
            // Refusing to its face is answered the same way being an enemy is. Only RTK's script witnesses
            // this, but it is the same beat Nussan describes for the enemy case, so it stands.
            //
            // ONLY on the explicit refusal, though. RTK branches on `choice == 2`, and closing the window
            // (choice 0) reaches neither arm — which is the right call regardless of provenance: dismissing
            // a dialog is not an answer, and a player who did it by accident should not be struck down for it.
            if (choice == 2)
            {
                await ctx.Say("Then die.");
                ctx.CastStormstrikeAndBanish();
            }
            return;
        }

        // The confirmation exists because accepting COSTS something: the kill track is wiped, so bosses
        // already banked toward ANOTHER alliance are gone with it (that alliance stays accepted, and its
        // tribute stays in the bag — only the kills go).
        //
        // RTK's line, verbatim, and it UNDERSTATES what happens: it says "these mobs", but the whole track
        // goes ("it resets your Kill Track to zero" — Alliance Tips). Left as it is anyway, because this is
        // ported dialogue and the mythics' lines survive nowhere else. Do not "correct" it.
        if (await ctx.Menu("Starting this quest will reset your kills of these mobs that you may have had prior. Continue?",
                new[] { "Yes, reset the kills.", "No, nevermind." }) != 1)
            return;

        ctx.ClearKillTrack();
        ctx.SetStage(mine.QuestKey, 1);

        await ctx.Say(
            $"A wise choice. We do well in our eternal struggle against the vile {foe.Animal}. " +
            "I charge you with helping us finish them!");
        await ctx.Say(
            $"Slay three of each of their leaders and bring to me {Tribute(ctx, foe)}. " +
            $"Try not to become too distracted, we want to win! I want the blood of the {foe.Animal}s fresh on your blade!");
    }

    // =============================================================================================
    // Handing it in
    // =============================================================================================
    private static async Task TurnIn(NpcContext ctx, MythicAllianceDef mine, MythicAllianceDef foe)
    {
        // Three of each half of ONE tier pair, counted off the kill track rather than off lifetime kills —
        // a boss killed before the alliance was accepted, or long enough ago to have been pushed off the
        // end, does not pay for it.
        bool enough = foe.BossPairs().Any(p =>
            ctx.TrackedKills(p.KeyBoss) >= MythicAlliance.Required &&
            ctx.TrackedKills(p.ItemBoss) >= MythicAlliance.Required);

        if (!enough)
        { await ctx.Say("You did not heed my words and kill enough to fill my vengeance!"); return; }

        if (ctx.CountItem(foe.ItemDrop) < foe.ItemTribute || ctx.CountItem(foe.KeyDrop) < foe.KeyTribute)
        { await ctx.Say("You are missing the needed items!"); return; }

        // The favour is a real item and the pack can be full. Check BEFORE the tribute is taken — taking it
        // frees at least one slot, so this is the only moment the reward can be lost.
        if (ctx.FreeSlotCount == 0 && ctx.CountItem(mine.Favor) == 0)
        { await ctx.Say("You carry too much to accept what I would give you. Return lighter."); return; }

        ctx.TakeItem(foe.ItemDrop, foe.ItemTribute);
        ctx.TakeItem(foe.KeyDrop, foe.KeyTribute);

        ctx.AddKarma(mine.Karma);
        ctx.GiveItem(mine.Favor);
        ctx.AwardExp(mine.Exp);
        ctx.AddLegend($"Lesser alliance with the {mine.Animal} ({Character.GameDate})",
            mine.LegendKey, MythicAlliance.LegendIcon, MythicAlliance.LegendColor);
        ctx.SetStage(mine.QuestKey, 0);

        await ctx.Say($"You have proven yourself worthy! Consider yourself an ally of the {mine.Animal}!");
    }

    /// <summary>"(4) Fragile rose and (8) Key to wind" — RTK's own phrasing, item before key.</summary>
    private static string Tribute(NpcContext ctx, MythicAllianceDef foe) =>
        $"({foe.ItemTribute}) {ctx.ItemName(foe.ItemDrop)} and ({foe.KeyTribute}) {ctx.ItemName(foe.KeyDrop)}";
}
