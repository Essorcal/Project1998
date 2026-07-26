namespace Server;

/// <summary>
/// NPC shop catalogues — what each shop NPC sells, grouped into the sub-menus RTK uses. Keyed by the NPC's
/// identifier (<see cref="NpcDef.Key"/>), so every smith shares one catalogue. Item <b>keys</b> are resolved
/// against <see cref="Content.ItemByKey"/> at menu-build time (unknown keys are skipped), and prices come
/// from the item db (<see cref="ItemDef.BuyPrice"/>/<see cref="ItemDef.SellPrice"/>), so there are no prices
/// to maintain here — only stock lists. Ported verbatim from the RTK Lua NPC scripts (rtklua/Accepted/NPCs).
/// Additional shop NPCs (butcher, herbalist, …) get their own entries as they're ported.
/// </summary>
public static class Shops
{
    public sealed record Category(string Name, string[] Keys);

    // rtklua/Accepted/NPCs/Common/smith.lua -> SmithNpc.buyItems (category order preserved).
    private static readonly Category[] Smith =
    {
        new("Projectiles",          new[] { "spring_bow" }),
        new("Other items",          new[] { "wooden_saber", "wooden_sword", "viperhead_woodsaber",
                                             "viperhead_woodsword", "steel_dagger", "steel_saber",
                                             "steel_sword", "steel_blade" }),
        new("Peasant clothes",      new[] { "war_platemail", "spring_mail_dress", "spring_war_dress",
                                             "scale_mail", "merchant_armor", "spring_armor_dress" }),
        new("Male helms",           new[] { "merchant_helm", "farmer_helm", "royal_helm", "sky_helm",
                                             "ancient_helm", "blood_helm", "earth_helm" }),
        new("Female helmets",       new[] { "spring_helmet", "summer_helmet", "autumn_helmet", "winter_helmet",
                                             "ancient_helmet", "blood_helmet", "earth_helmet" }),
        new("Warrior's platemail",  new[] { "jade_war_platemail", "royal_war_platemail", "sky_war_platemail",
                                             "ancient_war_platemail", "blood_war_platemail", "earth_war_platemail",
                                             "summer_war_dress", "autumn_war_dress", "winter_war_dress",
                                             "ancient_war_dress", "blood_war_dress", "earth_war_dress" }),
        new("Rogue's armor",        new[] { "farmer_armor", "royal_armor", "sky_armor", "ancient_armor",
                                             "blood_armor", "earth_armor", "summer_armor_dress",
                                             "autumn_armor_dress", "winter_armor_dress", "ancient_armor_dress",
                                             "blood_armor_dress", "earth_armor_dress" }),
        new("Warrior's scalemail",  new[] { "jade_scale_mail", "royal_scale_mail", "sky_scale_mail",
                                             "ancient_scale_mail", "blood_scale_mail", "earth_scale_mail",
                                             "summer_mail_dress", "autumn_mail_dress", "winter_mail_dress",
                                             "ancient_mail_dress", "blood_mail_dress", "earth_mail_dress" }),
    };

    // rtklua/Accepted/NPCs/Common/inn_npc.lua -> InnNpc.buyItems (a flat list — no sub-categories, so a
    // single category which the Buy menu shows directly).
    private static readonly Category[] Inn =
    {
        new("Goods", new[] { "apple", "wine", "thick_wine", "yellow_scroll",
                             "soup_bowl", "comb", "rice_wine", "root_liquor" }),
    };

    // Curated overrides only. Every other shop NPC (butcher, Nogh, potion shop, tailor, …) is served
    // automatically from the auto-extracted RTK stock (Content.ShopStock) via For(), so it needs no entry here.
    private static readonly Dictionary<string, Category[]> Catalogues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SmithNpc"] = Smith,   // kept for the hand-authored sub-category menu
        ["InnNpc"]   = Inn,
    };

    /// <summary>The buy catalogue for an NPC identifier. Curated catalogues above win (nice sub-categories,
    /// hand-tuned stock); otherwise fall back to the auto-extracted RTK stock (<see cref="Content.ShopStock"/>,
    /// a single flat "Goods" category) so any shop-flagged NPC has something to sell. Null if neither has it.</summary>
    public static Category[]? For(string npcKey)
    {
        if (Catalogues.TryGetValue(npcKey, out var c)) return c;
        if (Content.ShopStock.TryGetValue(npcKey, out var keys) && keys.Length > 0)
            return new[] { new Category("Goods", keys) };
        return null;
    }
}
