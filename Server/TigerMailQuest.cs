using Shared;

namespace Server;

/// <summary>
/// The Tiger Mail chain — the Warrior path's seven-rung armor ladder, run entirely by <b>Claw</b>, the
/// immortal tiger in <b>Chonsa Den</b> (NPCs.csv 148, map 3041, reached from Buya at 127-128/120). Ported
/// from RTK <c>NPCs/buya/claw.lua</c>, with the ladder itself cross-checked against two period sources
/// (see <b>Sources</b> below).
///
/// <para>The chain, in the order a player walks it:</para>
/// <list type="number">
/// <item>The tutor (Ironheart/Jadespear) tells a Warrior who has reached <see cref="MinLevel"/> about
/// <b>Tiger Essence</b> and names the
/// old teacher: "Go to the heart of the Tiger's cave and say Chongun." Optional — the quest can be started
/// without ever hearing it (Atlas: "You can do the quest without speaking to the Tutor").
/// See <see cref="TutorialQuest"/>'s tiger-essence branch.</item>
/// <item>Walk into Chonsa Den and <b>say "chongun"</b> near Claw, carrying that rung's three (or two, on the
/// first rung) sacrifices. He takes them, takes a slice of banked experience, and hands over the armor.</item>
/// <item>Come back every ten levels for the next rung, up to <b>Earth tiger mail / Earth tigress</b> at 60,
/// where Claw's knowledge runs out.</item>
/// </list>
///
/// <para>Say <b>"i lost my tiger mail"</b> to reset progress to the first rung — RTK's own recovery hatch for
/// a player who destroyed or gave away an armor and can no longer supply it as the next rung's ingredient.
/// It refunds nothing and takes nothing away, so a player who still holds their old armors ends up with
/// duplicates. That is RTK's behaviour and the dialog's stated purpose, and it is kept.</para>
///
/// <para><b>Sources, and where they disagree.</b> Three describe this quest and all three agree on the
/// ladder's shape (seven rungs at levels 6/10/20/30/40/50/60, catalyst + matching war platemail/dress +
/// the previous tiger armor, plus an experience sacrifice):</para>
/// <list type="bullet">
/// <item><b>Head Tutor KoyaSoto, "Info - Tiger Mail Quest"</b> (boards.nexustk.com Warriors board, credited
/// to Seoulfire and Braincrese) — a live-server tutor's own table. It is the only source for the exact
/// experience figures, which it lists as a "TNL penalty" per armor: 664 / 2,556 / 11,200 / 34,784 / 70,344 /
/// 178,032 / 428,544. Those are exactly RTK's seven constants, so the flat costs are corroborated twice.</item>
/// <item><b>nexusatlas.com/quests/tigermail.php</b> — the level gate ("Level Required: 6"), the Warrior-path
/// prerequisite, the Chonsa Den location, and the Harden Armor cast.</item>
/// <item><b>tswolf.com / NexNet</b> (Wayback 2001-01-28 and 2001-06-26) — the closest source to this
/// server's 4.95 target date. <c>quests/armor/warrior.shtml</c> is the Warrior armour-quest index and
/// <c>armor/warrior.shtml</c> the item archive; between them they fix the era-correct armour NAMES and settle
/// what Claw does and does not lead to (see the two notes below).</item>
/// <item><b>RTK <c>claw.lua</c></b> — the only source of the actual DIALOG, so every line below is its text.</item>
/// </list>
///
/// <para>Three deliberate calls where the sources conflict:</para>
/// <list type="bullet">
/// <item><b>The starting level is 6, not RTK's 5.</b> RTK gates the first rung on <c>player.level &lt; 5</c>;
/// Atlas's structured field reads "Level Required : 6" and KoyaSoto writes "the quest cannot be started till
/// level 6". Two independent period sources beat the fan server, and 6 is the self-consistent number: a
/// Peasant is walled at 5 (<see cref="Session.AwardExp"/>) and the quest demands the Warrior path, so the
/// first level at which a Warrior can actually have earned anything is 6.</item>
/// <item><b>The second rung's catalyst is a gold acorn, not a mountain ginseng.</b> The Atlas snapshot this
/// was checked against (2006-09-16) says "1 mountain ginseng", and so do its 2006-11, 2007-03 and 2007-08
/// captures — but its 2007-10 capture and every one after it says "1 gold acorn", which is also what
/// KoyaSoto's table and RTK say. Two-against-one, and the one that changed is the one that changed TOWARD
/// the other two, which reads as Atlas correcting itself rather than the game changing. (Both items exist in
/// Items.csv — mountain_ginseng 10045, gold_acorn 10039 — so this is a live choice, not a forced one.)</item>
/// <item><b>The female rungs are the Summer / Autumn / Winter tigress, not Jade / Royal / Sky.</b> RTK's
/// Items.csv names the female ladder after the male one, and that is a slip: EVERY other female warrior line
/// is seasonal where the male is mineral (war dress spring/summer/autumn/winter, mail dress the same), and
/// both period sources give the tigress the seasonal set — tswolf's 2001 archive lists "Summer Tigress 6 /
/// Autumn Tigress 16 / Winter Tigress 26", and nexusatlas' own <c>warriorarmor-old.php</c> pairs "Jade tiger
/// mail / Summer tigress", "Royal tiger mail / Autumn tigress", "Sky tiger mail / Winter tigress". So
/// nexusatlas' quest page saying "Autumn tigress" at rung 3 was RIGHT and is not the slip it looks like.
/// Items.csv rows 42014-42016 are renamed to the seasonal names; the KEYS stay <c>jade_/royal_/sky_tigress</c>
/// so they still line up with the RTK reference tree a future porter will be reading beside this.</item>
/// </list>
///
/// <para><b>Not ported:</b> <c>claw.lua</c>'s other branch — the level-99 "dragon" / "earth dragon" / "shard"
/// conversation that seeds the Dragon Shard quest. It is a different quest, needs Baegi's ring shop and the
/// Sonhi Desert to mean anything, and both are unported. See docs/common/Deferred-Work.md.</para>
///
/// <para>State is RTK's single registry key (<see cref="QuestKey"/>) holding the level of the NEXT rung —
/// 0 · 10 · 20 · 30 · 40 · 50 · 60 · <see cref="Done"/> — so nothing new is persisted.</para>
/// </summary>
public static class TigerMailQuest
{
    /// <summary>RTK <c>player.quest["tiger_armor"]</c>. Holds the level of the rung the player is ON, which is
    /// also the level they must reach to claim it: 0 (nothing yet) · 10 · 20 · 30 · 40 · 50 · 60 · 70 = done.</summary>
    public const string QuestKey = "tiger_armor";

    /// <summary>Claw, the immortal tiger of Chonsa Den (NPCs.csv 148, map 3041 at 5/4). The identifier
    /// <c>ClawNpc</c> is his alone, so the ability's composition row is the whole gate — unlike the Sute
    /// quest's, which has to narrow a shared identifier (see <see cref="SuteQuest.GuildMasterNpcId"/>).</summary>
    public const int ClawNpcId = 148;
    public const ushort ChonsaDenMap = 3041;

    /// <summary>Warrior. RTK gates on <c>player.baseClass ~= 1</c>; Atlas states the prerequisite outright
    /// ("Prerequisite : Warrior Path"). Base path, so the Chongun/Barbarian subpaths qualify too.</summary>
    public const int WarriorPathId = 1;

    /// <summary>The first rung's level gate — 6, not RTK's 5. See the class doc for why. Also the gate on the
    /// tutor's briefing (<c>TutorialQuest.TigerEssence</c>): the tutor must not send a player to Claw a level
    /// before Claw will serve them, so the two read the same constant.</summary>
    public const int MinLevel = 6;

    /// <summary>The registry value once the last rung is claimed. Earth is genuinely the end of this ladder:
    /// nexusatlas closes the walkthrough with "You cannot get another level of Tiger mail any longer."
    ///
    /// <para><b>Do not extend this to Star / Moon / Sun tiger mail.</b> Those items are real (tswolf's 2001
    /// archive lists Star/Moon/Sun Tiger Mail and Tigress with sell values, and nexusatlas later files Moon
    /// and Sun as <i>extinct</i>) but nothing grants them: tswolf's source column for all six reads
    /// <b>"Unknown"</b>, where the same table says "Quest" for Star/Moon/Sun Scale Mail and "Tailor+Smith" for
    /// Star/Moon/Sun War Platemail. RTK's closing line — "perhaps a celestial being elsewhere would know
    /// more" — leads to a tiger-mail continuation that is RTK's own invention. The real Warrior Star / Moon /
    /// Sun / Wind chain is <b>Scale Mail / Mail Dress</b>, run by the <b>Kugnae Guildmaster</b> at levels
    /// 66 / 76 / 86 / 96, and has nothing to do with Claw. See docs/common/Deferred-Work.md.</para></summary>
    public const int Done = 70;

    /// <summary>Set the first time Claw actually engages a Warrior on "chongun" — BEFORE any level or
    /// ingredient check, so it means "you have met him", not "you have made progress".
    ///
    /// <para>It exists to release the tutor's block (<c>TutorialQuest.TigerEssence</c>), which repeats the
    /// briefing on every click until the player has been to Chonsa Den. Keying that on
    /// <see cref="QuestKey"/> instead — which is what RTK effectively does — would strand a Warrior who found
    /// Claw but cannot yet afford an antler and a war platemail: they would have done everything the briefing
    /// asked and still be blocked out of the tutor forever. Meeting him is the thing the briefing is FOR.</para></summary>
    public const string MetClawReg = "tiger_essence_met_claw";

    /// <summary>Claw's creature portrait (NPCs.csv 148 look/colour), for the pages where the voice from the
    /// cave answers instead of the tutor. <c>TigerMailQuestTests.TutorBriefingUsesClawsPortrait</c> pins it
    /// against his row.</summary>
    public const int ClawLook = 29, ClawColor = 12;

    /// <summary>The Tiger Essence briefing, RTK <c>main_tutorial_npc.lua:130-156</c> verbatim, as an
    /// alternating script: each block is a run of pages plus who is speaking. It lives here rather than in
    /// <see cref="TutorialQuest"/> because it has TWO deliveries that must stay identical — the level-up push
    /// (<c>Session.PushTigerEssence</c>) and the tutor's own click branch — and a copy in each is a copy that
    /// drifts.</summary>
    public static readonly (bool Tiger, string[] Pages)[] Briefing =
    {
        (false, new[] { "You wish to learn the essence of the tiger?",
                        "Listen carefully." }),
        (true,  new[] { "There is an old, old soul that dwells within a cave. You must hurry to him." }),
        (false, new[] { "When you can, go within and speak to him. He will imbue your armor with the essence of the tiger.",
                        "He knows me and knows his old title, Chongun. Go to him immediately. Say that to him, and give him what he demands.",
                        "But beware, it is the essence within, your own experience that is made into your armor.",
                        "Go to the heart of the Tiger's cave and say Chongun." }),
    };

    /// <summary>The ward Claw lays on the new armor, on the <b>Jade and Blood rungs only</b> (see
    /// <see cref="Rung.Harden"/>). nexusatlas records it at exactly those two — "The tiger will cast Harden
    /// armor on you", under steps 2 and 6 and under no other step — and the user confirms the split is real
    /// rather than a gap in the page's coverage. RTK casts nothing anywhere, so there is no second opinion.
    ///
    /// <para>It is <c>harden_armor_mage</c> rather than the pathless <c>harden_armor</c> (Spells.csv 50028)
    /// because only the mage row carries data — SpellParams gives it hardarmors/armor -10/300s and
    /// spell_effects its animation and sound; 50028 has neither, and would be an invisible no-op.</para></summary>
    public const string HardenSpell = "harden_armor_mage";

    /// <summary>One rung of the ladder. Everything is per-sex except the catalyst and the price: the base
    /// armor to be transmuted, the tiger armor it becomes, and the previous rung's tiger armor handed back in.
    ///
    /// <para><paramref name="MalePrev"/>/<paramref name="FemalePrev"/> are empty on the first rung only — RTK
    /// asks for two items there and three on every rung after.</para></summary>
    /// <param name="Level">The level this rung demands, and the value stored in the registry while the player
    /// is on it. RTK's stage numbers ARE these levels, so one field serves as both.</param>
    /// <param name="Next">The registry value once this rung is claimed (the next rung's level, or <see cref="Done"/>).</param>
    /// <param name="Harden">Whether Claw casts Harden Armor when he hands this rung's armor over. True on the
    /// Jade and Blood rungs only — see <see cref="HardenSpell"/>.</param>
    public sealed record Rung(
        int Level, int Next,
        string Catalyst,
        string MaleBase, string FemaleBase,
        string MalePrev, string FemalePrev,
        string MaleArmor, string FemaleArmor,
        uint ExpCost, bool Harden = false)
    {
        public string Base(int sex)  => sex == 1 ? FemaleBase  : MaleBase;
        public string Prev(int sex)  => sex == 1 ? FemalePrev  : MalePrev;
        public string Armor(int sex) => sex == 1 ? FemaleArmor : MaleArmor;

        /// <summary>Everything this rung consumes, in the order the dialog names it: catalyst, base armor,
        /// and (past the first rung) the tiger armor being replaced.</summary>
        public IReadOnlyList<string> Sacrifices(int sex) =>
            Prev(sex).Length == 0 ? new[] { Catalyst, Base(sex) }
                                  : new[] { Catalyst, Base(sex), Prev(sex) };
    }

    /// <summary>The ladder. Level gates and catalysts are the sources' consensus; each male/female pair is
    /// confirmed by Items.csv carrying the same wear level and body sprite on both halves (Autumn war dress
    /// 35502 and the Autumn tigress 42015 are both level 16, look 206). Experience costs are KoyaSoto's TNL
    /// table, which RTK matches exactly. Harden Armor lands on the Jade and Blood rungs only.
    ///
    /// <para>The three female keys read <c>jade_/royal_/sky_tigress</c> but the items are NAMED Summer /
    /// Autumn / Winter tigress — the key is RTK's, the name is the era's. See the class doc.</para></summary>
    public static readonly Rung[] Ladder =
    {
        //     lvl next  catalyst           male base                female base           male prev             female prev         male armor              female armor         exp
        new Rung( 0, 10, "antler",     "war_platemail",       "spring_war_dress",   "",                   "",                 "tiger_mail",           "tigress",              664),
        new Rung(10, 20, "gold_acorn", "jade_war_platemail",  "summer_war_dress",   "tiger_mail",         "tigress",          "jade_tiger_mail",      "jade_tigress",        2556, Harden: true),
        new Rung(20, 30, "fox_blade",  "royal_war_platemail", "autumn_war_dress",   "jade_tiger_mail",    "jade_tigress",     "royal_tiger_mail",     "royal_tigress",      11200),
        new Rung(30, 40, "amber",      "sky_war_platemail",   "winter_war_dress",   "royal_tiger_mail",   "royal_tigress",    "sky_tiger_mail",       "sky_tigress",        34784),
        new Rung(40, 50, "moonblade",  "ancient_war_platemail", "ancient_war_dress", "sky_tiger_mail",    "sky_tigress",      "ancient_tiger_mail",   "ancient_tigress",    70344),
        new Rung(50, 60, "maxcaliber", "blood_war_platemail", "blood_war_dress",    "ancient_tiger_mail", "ancient_tigress",  "blood_tiger_mail",     "blood_tigress",     178032, Harden: true),
        new Rung(60, Done, "electra",  "earth_war_platemail", "earth_war_dress",    "blood_tiger_mail",   "blood_tigress",    "earth_tiger_mail",     "earth_tigress",     428544),
    };

    /// <summary>The rung a stored registry value puts the player on, or null once the ladder is finished (or
    /// if the value was never a rung — an unknown value reads as "done" rather than restarting them).</summary>
    public static Rung? RungAt(int stage) => Ladder.FirstOrDefault(r => r.Level == stage);

    /// <summary>The level gate for the rung the player is on. The first rung is the one exception to
    /// "stage value == required level": stage 0 is claimable at <see cref="MinLevel"/>.</summary>
    public static int LevelFor(Rung rung) => rung.Level == 0 ? MinLevel : rung.Level;
}

/// <summary>
/// Claw's half of the quest. Speech-only, exactly as RTK has it (<c>onSayClick</c>) and as both period sources
/// describe it — "Just say 'Chongun' to the tiger in Chonsa Den" — so it adds no click entry and clicking him
/// shows the default greeting.
///
/// <para>Composed onto <c>ClawNpc</c> in NpcAbilities.csv.</para>
/// </summary>
public sealed class TigerMailAbility : INpcAbility, INpcSayHandler
{
    public static readonly TigerMailAbility Instance = new();

    /// <summary>No click entry — the quest is heard, not read off a menu.</summary>
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx) => NoClickMenu.None;

    public async Task<bool> OnSay(NpcContext ctx, string speech)
    {
        switch (speech)
        {
            case "i lost my tiger mail": await Relearn(ctx); return true;
            case "chongun":              await Chongun(ctx); return true;
            default:                     return false;
        }
    }

    // ---- "i lost my tiger mail" — RTK's recovery hatch --------------------------------------------
    // Ungated, as in RTK: it refunds nothing and confiscates nothing, so the worst a curious player can do is
    // re-buy a rung they already own. Its reason for existing is the player whose armor is gone (destroyed,
    // traded away) and who therefore can never supply the next rung's third ingredient.
    private static async Task Relearn(NpcContext ctx)
    {
        await ctx.Say("Oh? So you've lost your tiger mail, huh? I will have to reteach you the ways of the tiger.");

        int choice = await ctx.Menu("Are you sure you wish to relearn the ways of the tiger? (this will reset your quest progress)",
                                    new[] { "Yes", "No" });
        if (choice != 1) return;

        ctx.SetStage(TigerMailQuest.QuestKey, 0);
        await ctx.Say("Ah, so we are going to begin again. Please say \"Chongun\" when you are ready to start again.");
    }

    // ---- "chongun" — the ladder itself -----------------------------------------------------------
    private static async Task Chongun(NpcContext ctx)
    {
        if (ctx.KarmaTooLow()) return;                       // RTK Tools.checkKarma

        if (ctx.BasePathId != TigerMailQuest.WarriorPathId)
        {
            await ctx.Say("Sorry I cannot help your kind.");
            return;
        }

        // Reaching Claw is what the tutor's briefing asks for, so it is stamped here — before the level and
        // ingredient checks, because "come and see me" is satisfied by arriving, not by qualifying. It is what
        // stops the tutor repeating the briefing at this player forever (see TigerMailQuest.MetClawReg).
        ctx.SetReg(TigerMailQuest.MetClawReg, 1);

        // RTK opens with this every time, before any level or progress check — so a level-4 warrior and a
        // finished level-60 one both hear it. Kept: it is the only place the rules are stated ("Do NOT hand
        // me anything"), and Claw is not an NPC you talk to by accident.
        await ctx.Say("In former lives, I was a mighty Chongun, that would put your contemporaries to shame.",
                      "You seek my essence? You wish to become as powerful and invincible as the tiger?",
                      "Then you must use that essence within yourself. Do NOT hand me anything. Just have it on you.");

        // Walk the ladder as far as the player can go in one visit. RTK gets this by falling through seven
        // consecutive `if quest == N` blocks rather than elseif-ing them, so a player who arrives with every
        // ingredient for several rungs climbs them all in one conversation; that is preserved. What is NOT
        // preserved is the duplicate line the fall-through produces — RTK's grant ends on "Return when you
        // have reached level N" and then the next block immediately says it again.
        int stage = ctx.Stage(TigerMailQuest.QuestKey);
        while (true)
        {
            var rung = TigerMailQuest.RungAt(stage);
            if (rung is null)
            {
                // RTK's line, kept as flavour — but the "celestial being elsewhere" it gestures at leads
                // nowhere in this era, and must not be built as a tiger-mail continuation (see Done).
                await ctx.Say("You have seen what I know for I have only lived on earth. Perhaps a celestial being elsewhere would know more.");
                return;
            }

            int need = TigerMailQuest.LevelFor(rung);
            if (ctx.Level < need)
            {
                await ctx.Say($"Return when you have reached level {need}.");
                return;
            }

            var sacrifices = rung.Sacrifices(ctx.Sex);
            if (sacrifices.Any(k => !ctx.HasItem(k)))
            {
                await ctx.Say(AskFor(ctx, rung, sacrifices));
                // A player wearing the armor Claw wants reads "bring your Jade tiger mail" while it is on
                // their back, because the pack and the worn slots are separate stores (CountItem is pack-only,
                // as is RTK's player:hasItem). Say so rather than leaving them to work it out.
                foreach (var key in sacrifices)
                    if (ctx.HasEquipped(key))
                        ctx.Notify($"You must remove your {ctx.ItemName(key)} before I can take it.");
                return;
            }

            foreach (var key in sacrifices) ctx.TakeItem(key, 1);
            // The pack cannot fill here: every rung consumes at least one non-stackable armor, whose slot
            // frees before the reward is handed over (the catalysts stack, so they may not free one).
            ctx.GiveItem(rung.Armor(ctx.Sex), 1);

            // "it is the essence within, your own experience that is made into your armor" (the tutor's
            // warning). RTK clamps at zero rather than refusing — a player who cannot pay in full pays what
            // they have and still gets the armor — and only ever WRITES BACK the clamped case, so its
            // subtraction is silently dropped for everyone who can afford it. That is a plain bug; the
            // sacrifice is the whole point of the quest, so it is charged here.
            uint paid = Math.Min(ctx.Exp, rung.ExpCost);
            ctx.SpendExp(paid);
            if (rung.Harden) ctx.CastWard(TigerMailQuest.HardenSpell);

            // Server status, not dialog — the sacrifice is this quest's headline mechanic and is otherwise
            // completely invisible (the player just sees their experience-to-next-level jump). It also gives a
            // per-rung marker when a late starter climbs several rungs in one conversation, which RTK's
            // fall-through gets from its repeated "Return when you have reached level N" and this loop drops.
            ctx.Notify($"{ctx.ItemName(rung.Armor(ctx.Sex))} — {paid:N0} experience sacrificed.");

            stage = rung.Next;
            ctx.SetStage(TigerMailQuest.QuestKey, stage);
        }
    }

    /// <summary>The "you are missing something" line. RTK words the first rung differently from the rest —
    /// "Fetch an X and Y" for the two-item opener, "For the next armor, bring a X, Y, and your Z" once the
    /// previous tiger armor is part of the price.</summary>
    private static string AskFor(NpcContext ctx, TigerMailQuest.Rung rung, IReadOnlyList<string> sacrifices)
    {
        var names = sacrifices.Select(ctx.ItemName).ToList();
        return rung.Prev(ctx.Sex).Length == 0
            ? $"Fetch an {names[0]} and {names[1]}"
            : $"For the next armor, bring a {names[0]}, {names[1]}, and your {names[2]}";
    }
}
