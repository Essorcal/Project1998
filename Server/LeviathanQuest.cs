using Shared;

namespace Server;

/// <summary>
/// The Leviathan quest — the Nagnang chain that ends at the Hermit's weapon shop. Ported from RTK
/// <c>NPCs/nagnang/ancient_leviathan.lua</c> + <c>NPCs/nagnang/hermit.lua</c>, with the two tile triggers
/// from <c>onScriptedTiles/onScriptedTilesQuest.lua</c> (see <c>Session.TryLeviathanRelease</c> and
/// <c>Session.TryLeviathanHermitDoor</c> in Session.Navigation.cs).
///
/// <para>The chain, in the order a player walks it:</para>
/// <list type="number">
/// <item><b>Dae-Whan</b>, the Ancient Leviathan (Leviathan Fields, map 2538), asks for help: a man keeps
/// taking his young away to train them for war. Agree and he hands over a <c>leviathan_talisman</c>; refuse
/// and you are marked his sworn enemy, which costs a million coins to undo.</item>
/// <item>The <b>Border patrol</b> (Nagnang, map 2500) is the ONLY door into the training camp: he smells the
/// leviathans on you and looks the other way for a green squirrel pelt, then walks you through to Worn path
/// (map 2542). There is no warp row from 2500 into that pocket — hand him the pelt or the rest of the chain
/// is unreachable.</item>
/// <item>In <b>Blight pen</b> (map 2544) four captured leviathans stand caged at y=2 behind shut doors. Walk
/// up to a cage and DROP the talisman — the spell breaks through the bars, the captive thanks you and is
/// gone. See <see cref="PenMap"/> for why the drop is the mechanic and not a step.</item>
/// <item>Report back to Dae-Whan for the <b>Freed Leviathan</b> legend, and he points you at "one of your
/// kind in a small hut northeast of here… just tell him Dae-Whan has sent you".</item>
/// <item>That legend opens the hut door on <b>Leviathan Hermit</b> (map 2539); without it the door shoves
/// you back with a "Go AWAY!".</item>
/// <item>The <b>Hermit</b> (Hermit Home, map 2540) throws out anyone who hasn't said the password. Say
/// <b>"dae-whan"</b> and he tells you what he is — he cursed weapons with dark arts and his own kind cast
/// him out — and opens his shop for good.</item>
/// </list>
///
/// <para><b>The shop</b> is his tainted blade/staff/ring (RTK's list) plus the sixteen class fans, per the
/// user (2026-08-12): the archive knows him as "the Fan Hermit" (nexusatlas news, 2002-10-24), and the fans
/// are era-safe here — they arrived in October 2000 ("a ton of new items, especially the cool new fans out",
/// 2000-10-14), well before the 4.95 target date. Stock lives in ShopCatalogues.csv, not in code.</para>
///
/// <para>Quest state is the RTK registry key <c>leviathan</c> (<see cref="Key"/>) plus the two legends, so
/// it maps onto the ordinary quest-stage store with no new persistence. Note RTK gates the door and the
/// pen on the <b>legend</b>, not the stage — the stage alone would let someone who freed a captive but never
/// went back to Dae-Whan walk straight in, and would let them free a second one.</para>
/// </summary>
public static class LeviathanQuest
{
    /// <summary>RTK <c>player.quest["leviathan"]</c>.</summary>
    public const string Key = "leviathan";

    public const int StageAsked  = 1;   // talisman in hand, captive not yet freed
    public const int StageFreed   = 2;  // captive released; Dae-Whan not yet thanked you
    public const int StageTrusted = 3;  // said "dae-whan" to the Hermit — his shop is open for good

    public const string LegendFreed = "leviathan_freed";
    public const string LegendEnemy = "leviathan_sworn_enemy";
    public const string Talisman    = "leviathan_talisman";
    public const string Pelt        = "green_squirrel_pelt";   // the Border patrol's bribe

    public const int  MinLevel        = 12;          // RTK: "Come back when you've gained more insight."
    public const uint ForgivenessGold = 1_000_000;

    /// <summary>Where the Border patrol drops you: Worn path, one tile east of the way back (Warps.csv 1638,
    /// 2542 (0,16) -> Nagnang (142,87)). Worn path -> Worn trail -> Blight pen are ordinary warps from here.</summary>
    public const ushort WornPathMap = 2542;
    public const int WornPathX = 1, WornPathY = 16;

    // ---- tile geometry (onScriptedTilesQuest.lua) ------------------------------------------------
    /// <summary>Blight pen, and the three rows that matter — a cage is read bottom-up as
    /// <see cref="PenStandY"/> (open floor, where you stand) / <see cref="PenDoorY"/> (the SHUT cage door) /
    /// <see cref="PenCaptiveY"/> (the captive, sealed inside).
    ///
    /// <para><b>The doors stay shut, and that is the whole mechanic.</b> The 4.95 map puts object 600 on the
    /// door row — the game's closed cell door, SObj flag 0x01, which refuses entry heading north — and the
    /// only approach is northward from below. So the door row is unreachable and you can never get next to a
    /// captive. That is exactly what the quest instructions describe: "Walk up to one of the cages and drop
    /// your talisman on the ground. The leviathan <i>inside</i> will vanish, along with the talisman." You
    /// stand outside a shut cage and the spell breaks through the bars.</para>
    ///
    /// <para>RTK does it differently — its <c>onScriptedTilesQuest.lua</c> fires on STEPPING onto the door
    /// row, and RTK's own copy of this map has object 601 there (600's walkable twin) to make that possible.
    /// That is RTK editing the terrain to suit its script, not evidence about the live game: the client's map
    /// is the shipped original. We follow the client map and the player-facing instructions, so the drop is
    /// the ONLY trigger and there is deliberately no step trigger to go with it.</para></summary>
    public const ushort PenMap = 2544;
    public static readonly int[] PenX = { 4, 9, 14, 19 };
    public const int PenCaptiveY = 2;
    public const int PenDoorY    = 3;
    public const int PenStandY   = 4;
    public const string CaptiveMob = "captured_leviathan";

    /// <summary>How near a captive you must be for dropping the talisman to free it (Chebyshev tiles). Two,
    /// because the shut door sits between you and it: from <see cref="PenStandY"/> to
    /// <see cref="PenCaptiveY"/> is exactly two. Nothing tighter can work, and nothing looser can misfire —
    /// a captive only ever exists in this pen. See <c>Session.TryLeviathanTalismanDrop</c>.</summary>
    public const int DropRange = PenStandY - PenCaptiveY;

    /// <summary>The Hermit's hut door, on the map outside it.</summary>
    public const ushort DoorMap = 2539;
    public static readonly int[] DoorX = { 22, 23 };
    public const int DoorY       = 8;
    public const int DoorPushToY = DoorY + 4;   // RTK: warp(player.m, player.x, player.y + 4)

    /// <summary>Inside the hut (where the door lands you, and where the Hermit throws you back out from).</summary>
    public const ushort HutMap = 2540;
    public const int HutX = 6, HutY = 10;
    public const int EjectX = 22, EjectY = 11;
}

/// <summary>
/// Dae-Whan, the Ancient Leviathan (RTK <c>ancient_leviathan.lua</c>). One entry, so a click dives straight
/// into the conversation the way the Lua does — the branch he takes is decided entirely by your quest state.
/// </summary>
public sealed class AncientLeviathanAbility : INpcAbility
{
    public static readonly AncientLeviathanAbility Instance = new();

    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        yield return ("Speak", Talk);
    }

    private static async Task Talk(NpcContext ctx)
    {
        if (ctx.Level < LeviathanQuest.MinLevel)
        { await ctx.Say("Come back when you've gained more insight."); return; }

        if (ctx.HasLegend(LeviathanQuest.LegendEnemy) && !await Forgive(ctx)) return;

        if (ctx.HasLegend(LeviathanQuest.LegendFreed)) { await ThankedAlready(ctx); return; }

        switch (ctx.Stage(LeviathanQuest.Key))
        {
            case LeviathanQuest.StageAsked:
                await ctx.Say("Please go save my kindred with the talisman I gave you.");
                return;

            case LeviathanQuest.StageFreed:
                ctx.AddLegend($"Freed Leviathan ({Character.GameDate})", LeviathanQuest.LegendFreed, 7, 128);
                await ctx.Say(
                    "I thank you with all my heart for saving even one of our young kind. You will always be a friend to the free Leviathans.",
                    "In fact, there is one of your kind in a small hut northeast of here. He may be able to help you. He mistrusts strangers, but just tell him Dae-Whan has sent you.");
                return;

            default:
                await Ask(ctx);
                return;
        }
    }

    /// <summary>The million-coin apology. True if the conversation may continue — either they were forgiven,
    /// or they refused and there is nothing more to say (false), which is RTK's behaviour by omission: every
    /// later branch of its click handler is guarded on NOT holding this legend.
    ///
    /// <para>TODO — this is only HALF the forgiveness system. The in-game instructions describe a second,
    /// separate gate: if you do not hold the legend but he still says "You still have not been forgiven by
    /// all the totem Animals!", you must visit each Totem Shrine in the Wilderness and say "Forgive" to each
    /// NPC. Nothing here checks that, so that line can never appear. The pieces exist and are unwired — a
    /// targeted <c>forgive</c> spell (Spells.csv 31502) with no SpellParams row or verb, and the four totem
    /// priests (NPCs 94-97) all sharing the TotemNpc script. Open question before building it: is totem
    /// forgiveness required INSTEAD of the coins or AFTER them?</para></summary>
    private static async Task<bool> Forgive(NpcContext ctx)
    {
        int choice = await ctx.Menu(
            "You have been rude to me and my kind. For that you will need to pay me 1 million coins for me to continue talking with you. Do you wish to do so?",
            new[] { "Yes. I am sorry for my remark and will pay you.", "No. You are not worth the money." });

        if (choice != 1)
        {
            if (choice == 2) await ctx.Say("Then GO! And never return as we do not want your help!");
            return false;
        }
        if (ctx.Coins < LeviathanQuest.ForgivenessGold)
        { await ctx.Say("Return to me when you have the gold."); return false; }

        ctx.SpendGold(LeviathanQuest.ForgivenessGold);
        ctx.RemoveLegend(LeviathanQuest.LegendEnemy);
        await ctx.Say("I forgive you.");
        return true;
    }

    // The opening plea, then the choice that starts the quest or makes an enemy of him.
    private static async Task Ask(NpcContext ctx)
    {
        await ctx.Say(
            "No! Please! Take no more of our kind!!!",
            "Oh, you are not Him. I am sorry for my greeting of you. But we have been losing our kind for months now to a man whom we are unable to destroy.",
            "He continues to come here from time to time to take our youngsters away and trains them to do his bidding at war.",
            "He was just here a few days ago, and has taken a new group of them to his training grounds. Where he forces them to work and slave until they are mindless monsters for him.",
            "I have spent many days making just one of these fragile talismans. My kind is bound by a spell in the cage. Only this talisman can free them.");

        int choice = await ctx.Menu("Would you be willing to help an old leviathan save his kindred?",
            new[] { "Yes, I am honored.", "No. Your kind deserve their fate." });

        if (choice == 2)
        {
            ctx.AddLegend($"Sworn enemy of the Leviathans ({Character.GameDate})", LeviathanQuest.LegendEnemy, 7, 4);
            await ctx.Say("Then GO! And never return as we do not want your help!");
            return;
        }
        if (choice != 1) return;   // closed the box — no talisman, no legend, ask again later

        ctx.SetStage(LeviathanQuest.Key, LeviathanQuest.StageAsked);
        ctx.GiveItem(LeviathanQuest.Talisman);
        await ctx.Say(
            "Thank you! Here is a talisman. It will only work once. Since they are so fragile and take so long to make, I will give you only one.",
            "You must step next to my captured kind. The talisman will then break the spell and fall to dust. And my kind will be transported back here.",
            "He moves his camp around from time to time but we believe it to be East of his homeland. If you go there and free even one of my kind, I will be grateful.");
    }

    /// <summary>What he says once you already hold the Freed Leviathan legend. RTK says NOTHING here — its
    /// last branch is guarded on not holding the legend, so clicking him after the quest opens no dialog at
    /// all. Repeating the hint costs nothing and is the only in-game record of the password, which a player
    /// who read it once and logged off has no other way to recover.</summary>
    private static async Task ThankedAlready(NpcContext ctx) =>
        await ctx.Say(
            "You will always be a friend to the free Leviathans.",
            "The old man in the hut northeast of here still mistrusts strangers. Tell him Dae-Whan has sent you.");
}

/// <summary>
/// The Border patrol (RTK <c>border_patrol.lua</c>) — the bribable guard on Nagnang's southern edge, and the
/// only way into the camp where the young leviathans are penned. Maps 2542/2543/2544 have no inbound warp
/// row at all: 1638/1639 run Worn path -> Nagnang (he stands beside where they land) and everything else in
/// that pocket is internal. His warp IS the entrance, so until he takes the pelt the quest dead-ends with the
/// talisman in your bag.
///
/// <para>He opens up on the quest STAGE, not a legend — the scent is on you from the moment Dae-Whan hands
/// over the talisman, and RTK never closes the door again (stage 2 and 3 still pass), so a finished player
/// can still cross for a pelt. That matters: Worn path and Worn trail are green squirrel ground, and locking
/// the gate behind the quest would strand anyone who walked back out to hunt.</para>
/// </summary>
public sealed class BorderPatrolAbility : INpcAbility, INpcHandItemHandler
{
    public static readonly BorderPatrolAbility Instance = new();

    // One entry, so a click dives straight into his line the way the Lua's click handler does.
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        yield return ("Speak", Talk);
    }

    private static async Task Talk(NpcContext ctx)
    {
        if (ctx.Stage(LeviathanQuest.Key) == 0)
        {
            await ctx.Say("Just doin' my job here. Keep yer nose clean and I won't have to do my job on you.");
            return;
        }

        await ctx.Say(
            "Eh? What's that? Sorry Stranger, we don't let anyone past our borders here.",
            "Hmmm, you have the scent of the Leviathans on you. Perhaps I could look the other way if you were to hand me one of those lovely pelts the green squirrels drop.");
    }

    /// <summary>The bribe. Exactly one pelt per crossing (RTK <c>removeItem(..., 1, ...)</c>), so handing the
    /// whole stack with 'H' costs one and leaves the rest in the bag. Returning false on anything else — the
    /// wrong item, or a pelt from someone who never spoke to Dae-Whan — lets the generic refusal fire, which
    /// puts the item back on the ground at their feet rather than eating it.</summary>
    public async Task<bool> OnHandItem(NpcContext ctx, ItemDef item, int amount)
    {
        if (item.Key != LeviathanQuest.Pelt) return false;
        if (ctx.Stage(LeviathanQuest.Key) == 0) return false;
        if (!ctx.TakeItem(LeviathanQuest.Pelt, 1)) return false;

        await ctx.Say("Well thank you kindly! Now be on your way and I don't know you. Oh, and look out for those tricky Fox spirits. They enjoy their little games.");
        ctx.Warp(LeviathanQuest.WornPathMap, LeviathanQuest.WornPathX, LeviathanQuest.WornPathY);
        return true;
    }
}

/// <summary>
/// The Hermit (RTK <c>hermit.lua</c>) — the cursed-weapon smith living with the leviathans, and the shop the
/// whole chain leads to. Clicking him before you have said the password gets you thrown out of his hut;
/// saying <b>"dae-whan"</b> unlocks him permanently (<see cref="LeviathanQuest.StageTrusted"/>).
/// </summary>
public sealed class HermitAbility : INpcAbility, INpcSayHandler
{
    public static readonly HermitAbility Instance = new();

    // One entry, so a click runs this directly — the gate is INSIDE it, matching the Lua, where the eject
    // check is the first thing the click handler does and the shop is what's left if you survive it.
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        yield return ("Buy", Shop);
    }

    private static async Task Shop(NpcContext ctx)
    {
        if (ctx.Stage(LeviathanQuest.Key) < LeviathanQuest.StageTrusted)
        {
            await ctx.Say("Who let you in here? Go away! I don't like strangers.");
            ctx.Warp(LeviathanQuest.DoorMap, LeviathanQuest.EjectX, LeviathanQuest.EjectY);
            return;
        }
        await ctx.Buy();
    }

    public async Task<bool> OnSay(NpcContext ctx, string speech)
    {
        // RTK only listens for the password while the quest is unfinished; afterwards the word is just chat.
        if (speech != "dae-whan" || ctx.Stage(LeviathanQuest.Key) >= LeviathanQuest.StageTrusted) return false;

        await ctx.Say(
            "Eh? So you are a friend of those big green guys to the south? Nice, peaceful folk they are. Leave me alone, and I leave them alone as well.",
            "My own kind has forsaken me. I devised a way to curse weapons with dark arts, corrupting them in exchange for formidable power. My companions feared my work and cast me out.",
            "They scattered my creations among monsters as deadly as the dark relics they now protect. I do not have the strength to reclaim them. I salvaged only the weakest of my designs, tainted and inferior.",
            "Still, even those may prove useful to you. The Leviathans took me in when no one else would. You helped them, so I will help you in return. Be wary. Wielding these items comes at a cost.");

        ctx.SetStage(LeviathanQuest.Key, LeviathanQuest.StageTrusted);
        await ctx.Buy();
        return true;
    }
}
