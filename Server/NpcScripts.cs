namespace Server;

/// <summary>
/// How each NPC is COMPOSED from reusable <see cref="INpcAbility"/> features. An NPC entry lists only what
/// that NPC <i>is</i> (a smith is a shop + repair; an inn keeper is a shop + bank + transport + clock) —
/// the abilities themselves hold how each feature works, shared across every NPC that has it.
///
/// NPCs whose menu is fully implied by their data flags need no entry here at all: <see cref="For"/> falls
/// back to deriving abilities from the shop/repair/bank flags, so a plain stocked shopkeeper is zero-config.
/// Register an NPC only when its composition differs from that default (extra features, a unique order, or
/// bespoke <see cref="InlineAbility"/> options).
/// </summary>
public static class NpcScripts
{
    private static readonly Dictionary<string, INpcAbility[]> ByKey = new(StringComparer.OrdinalIgnoreCase)
    {
        // smith.lua: Buy / Sell / Fix Item (+ crafting & quests, ported later).
        ["SmithNpc"] = new INpcAbility[] { ShopAbility.Instance, RepairAbility.Instance },

        // messenger.lua: Buy / Sell / Send Parcel / Receive Parcel. The 5 MessengerNpc's (Redcap, Paryu,
        // Sying, Tinbae, + a test) are already placed from NPCs.csv; this composition gives them the parcel
        // post (Parcel.cs) plus their shop stock. (RTK's Checks/Waypoint branches aren't modelled here.)
        ["MessengerNpc"] = new INpcAbility[] { ShopAbility.Instance, MessengerAbility.Instance },

        // inn_npc.lua: Buy / Sell / Banking / Date & Time. Note the inn does banking without a bank
        // data-flag — composition captures that cleanly where flag-derivation couldn't. (Transport was
        // dropped from the tavern banker per design — the ferry/transport service isn't offered here.)
        ["InnNpc"]  = new INpcAbility[] { ShopAbility.Instance, BankAbility.Instance, TimeAbility.Instance },

        // inn_npc2.lua: the non-shop tavern hand — Transport / Date & Time only.
        ["InnNpc2"] = new INpcAbility[] { TransportAbility.Instance, TimeAbility.Instance },

        // fishnpc.lua: Bate / Wim teach beginner fishing (tutorial stage 4 minnow). Triggered by clicking OR
        // by saying "I'd like to fish" (INpcSayHandler).
        ["FishNpc"] = new INpcAbility[] { FishAbility.Instance },

        // librarian.lua: say the tutor's name ("ironheart"/"jadespear") on tutorial stage 5 -> talked_to_tutor.
        // Also a "Talk to Librarian" click option so it works without voice.
        ["LibrarianNpc"] = new INpcAbility[] { LibrarianAbility.Instance },

        // chu_rua.lua (ChuRuaNpc) is a Lua dialog script now (npc_dialog.lua -> npcs.ChuRuaNpc), so it's not
        // registered here — the Lua path owns it (Session.RunNpcAsync checks NpcScript.Has first).

        // The Guol "magic animals" that hint at / gate the ginseng (all speech-triggered, INpcSayHandler):
        // the rabbit (hints), the Ancient dolmen (the tiger hint), the tiger (say "rabbit" -> Forest -> 1117).
        ["ChuRuaRabbitNpc"] = new INpcAbility[] { ChuRuaRabbitAbility.Instance },
        ["ChuRuaRockNpc"]   = new INpcAbility[] { ChuRuaRockAbility.Instance },
        ["ChuRuaTigerNpc"]  = new INpcAbility[] { ChuRuaTigerAbility.Instance },

        // Class trainers (warrior_trainer.lua &c.): Become a <Class> at lvl 5 (starter kit + path change),
        // Learn/Divine/Forget Secret (spell teaching), Become Noble (lvl-75 title) — see ClassTrainerAbility —
        // plus the repeatable Minor Quest. The lvl-66+ star/moon/sun armor chains + nagnang trials aren't ported.
        ["WarriorTrainerNpc"] = new INpcAbility[] { ClassTrainerAbility.Warrior, MinorQuestAbility.Instance },
        ["RogueTrainerNpc"]   = new INpcAbility[] { ClassTrainerAbility.Rogue,   MinorQuestAbility.Instance },
        ["MageTrainerNpc"]    = new INpcAbility[] { ClassTrainerAbility.Mage,    MinorQuestAbility.Instance },
        ["PoetTrainerNpc"]    = new INpcAbility[] { ClassTrainerAbility.Poet,    MinorQuestAbility.Instance },

        // rogue_guild_shaman.lua: Face / Gender change (real, visible on this client — see AppearanceAbility).
        // "Eyes" and the level-50+ Rogue "white_moon_axe" speech quest aren't ported. This is the ONLY place
        // Face/Gender is offered — the SalonNpc rows (Seme/Serge/Sarge) that duplicated it are dropped
        // entirely (Enabled=0 in NPCs.csv), per user direction to keep it Rogue-hall-only.
        ["RogueGuildShamanNpc"] = new INpcAbility[] { AppearanceAbility.Instance },

        // arena_master.lua: "Mountain"/"Tower" — the Arena Masters. Their whole service is one option, "War
        // paint" (the team/special armor dye — see WarPaintAbility). No shop/bank/repair, exactly as RTK.
        ["ArenaMasterNpc"] = new INpcAbility[] { WarPaintAbility.Instance },

        // ExpSeller.lua: "Shady"/"Sunset"/"Midnight" — the shadow-stat vendors (see ShadowStatsAbility).
        // Bon-Hwa (its own NpcIdentifier, higher rebirth-rank-gated caps + the Kawlana item quest) isn't ported.
        ["ExpSeller"] = new INpcAbility[] { ShadowStatsAbility.Instance },

        // chapel_npc.lua: "Lotus"/"Peach"/"Fen" in Kugnae/Buya/Nagnang — Buy/Sell (its own ShopStock.csv
        // catalogue: love/cooked_fish/rose_petals) plus the full marriage feature set (see ChapelAbility).
        // Registered explicitly (not flag-derived) so ChapelAbility's entries sit alongside Buy/Sell.
        ["ChapelNpc"] = new INpcAbility[] { ShopAbility.Instance, ChapelAbility.Instance },
    };

    /// <summary>The abilities that make up an NPC: its explicit composition if registered, else derived
    /// from its data flags (so simple shops/banks work with no entry here).</summary>
    public static INpcAbility[] For(NpcDef def)
    {
        var list = new List<INpcAbility>();
        // Any NPC that gives quests gets the quest menu first — including data-driven NPCs (like the two
        // MainTutorialNpc givers, which share an identifier but differ by id) that have no ByKey entry.
        if (Quests.ForNpc(def.Id).Count > 0) list.Add(QuestAbility.Instance);

        if (ByKey.TryGetValue(def.Key, out var abilities)) { list.AddRange(abilities); return list.ToArray(); }

        if (def.Shop)   list.Add(ShopAbility.Instance);   // contributes nothing if we haven't stocked it
        if (def.Repair) list.Add(RepairAbility.Instance);
        if (def.Bank)   list.Add(BankAbility.Instance);
        return list.ToArray();
    }
}
