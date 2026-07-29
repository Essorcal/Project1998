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

    /// <summary>The buy catalogue for an NPC identifier. Curated sub-category catalogues
    /// (data/game-data/ShopCatalogues.csv -> <see cref="Content.ShopCatalogues"/>, e.g. SmithNpc's armor
    /// menus) win — hand-authored, ordered, editable + hot-reloadable via !reload; otherwise fall back to the
    /// auto-extracted RTK stock (<see cref="Content.ShopStock"/>, a single flat "Goods" category) so any
    /// shop-flagged NPC has something to sell. Null if neither has it.</summary>
    public static Category[]? For(string npcKey)
    {
        if (Content.ShopCatalogues.TryGetValue(npcKey, out var cats) && cats.Count > 0)
            return cats.Select(c => new Category(c.Name, c.Keys)).ToArray();
        if (Content.ShopStock.TryGetValue(npcKey, out var keys) && keys.Length > 0)
            return new[] { new Category("Goods", keys) };
        return null;
    }
}
