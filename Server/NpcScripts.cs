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

        // inn_npc.lua: Buy / Sell / Banking / Transport / Date & Time. Note the inn does banking without a
        // bank data-flag — composition captures that cleanly where flag-derivation couldn't.
        ["InnNpc"]  = new INpcAbility[] { ShopAbility.Instance, BankAbility.Instance, TransportAbility.Instance, TimeAbility.Instance },

        // inn_npc2.lua: the non-shop tavern hand — Transport / Date & Time only.
        ["InnNpc2"] = new INpcAbility[] { TransportAbility.Instance, TimeAbility.Instance },

        // Class trainers (warrior_trainer.lua &c.) offer the repeatable minor quest. Their path-advancement
        // menus (Become a Warrior, star/moon/sun armor, …) aren't ported yet — just the minor-quest ability.
        ["WarriorTrainerNpc"] = new INpcAbility[] { MinorQuestAbility.Instance },
        ["RogueTrainerNpc"]   = new INpcAbility[] { MinorQuestAbility.Instance },
        ["MageTrainerNpc"]    = new INpcAbility[] { MinorQuestAbility.Instance },
        ["PoetTrainerNpc"]    = new INpcAbility[] { MinorQuestAbility.Instance },
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
