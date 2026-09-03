using Shared;

namespace Server;

/// <summary>
/// Gathering — mining, woodcutting, farming. RTK spawns every resource node as a MOB (wheat, ore veins,
/// ginko trees are `mobs.csv` rows with HP and a sprite) and puts the whole gathering loop in that mob's AI
/// script, <c>AI/crafting/{wheat,ore,ginko_tree}.lua</c>. We keep nodes-as-mobs and RTK's numbers, and change
/// the one thing that matters to a player: <b>how you start.</b>
///
/// <para>RTK harvests when you SWING at the node with the tool equipped, which is a 7.x convention. On 4.95
/// you harvest by <b>dropping the tool</b> beside the node — the drop keypress IS the swing (per the user,
/// and corroborated by the archived mines walkthrough: <i>"Keep on dropping your mining pick, 'till you have
/// Mud, and Clay"</i>). The tool never actually leaves the bag, which is what makes "keep on dropping"
/// possible at all — and it is safe to simply not remove it, because the 4.95 client does not clear a bag
/// slot on its own: only a server <c>0x10</c> empties one (see the client bag-array notes). Withholding that
/// packet leaves the client's own view of the pack correct, with no resync.</para>
///
/// <para>Everything else is RTK's: a two-minute exclusive claim on the node, a fixed per-swing damage
/// (65 unskilled, rising with the crafting skill) rather than your weapon's, a 1-in-33 critical that
/// quintuples it, a yield rolled once when the node breaks, and — for trees — a chance the tool snaps.</para>
///
/// <para><b>Not modelled yet:</b> crafting skill LEVELS. RTK scales both the swing damage (65 → 235) and the
/// ore quality table by an eleven-tier skill rank, and grants a skill point per item gathered. We have no
/// skill store, so every character harvests at the unskilled rate with the novice yield table. The two
/// places that would change are marked below; nothing else about this file would move.</para>
/// </summary>
public sealed partial class Session
{
    // RTK's unskilled swing (`local damage = 65` before any skill lookup). Skill tiers raise this to 235.
    private const int HarvestBaseDamage = 65;
    private const int HarvestCritRate   = 33;      // 1-in-33 to quintuple the swing
    private const long HarvestClaimMs   = 120_000; // RTK `os.time() + 120`

    /// <summary>The gathering entry point, called from the drop handler before anything leaves the bag.
    /// True means the drop was consumed as a harvest swing (the item stays put); false means "not a
    /// harvest" and the caller drops the item normally — which covers dropping a pick in an empty field,
    /// dropping bread at a tree, or a node whose skill is switched off in CraftingToggles.</summary>
    private bool TryHarvest(ItemDef tool)
    {
        var (node, def, toolIndex) = FindHarvestNode(tool);
        if (node is null || def is null) return false;

        // Claim: the node belongs to whoever started it, for two minutes. Lapsed claims are settled lazily
        // rather than on a world tick — a node nobody is touching has nothing to observe it, so healing it on
        // the next swing is indistinguishable from RTK's timer and costs no per-tick work.
        //
        // The whole decision is one acquisition of the world lock now (#103): a harvest node is a world mob,
        // and its claim fields and HP were being written from this read loop with no lock at all, while the
        // world read the same mob. Splitting it into a reset call and a claim call would have moved each
        // WRITE under the lock and left the decision spanning both, so two players swinging at one node in
        // the same instant could still both see it free. The semantics are exactly what they were.
        if (!_world.TryClaimHarvestNode(node, _char.Id, HarvestClaimMs))
        { Notify($"Someone else is working this {node.Name.ToLowerInvariant()}."); return true; }

        // The swing. Deliberately NOT your weapon damage: a legendary miner with bare hands out-mines a
        // warrior with a maxcaliber, because the pick is the tool and the skill is the arm.
        int damage = HarvestBaseDamage;   // <- skill tier would multiply in here
        if (Random.Shared.Next(HarvestCritRate) == 0)
        {
            damage *= 5;
            Notify("You wind up and take a large chunk!  *CRACK*");
        }

        // Reuse the drop crouch as the harvest motion — it is already a bend-down-and-work pose, and both
        // the player and everyone watching get a visible swing out of it.
        SendAction(_char.Id, 5, 20, 0);
        _world.BroadcastSameArea(_char.Map, _char.X, _char.Y, p => p.ActionOver(_char.Id, 5, 20, 0), except: this);

        // attackerId 0 on purpose: a node must never aggro (World.TryDamage would set it chasing us).
        if (_world.TryDamage(_char.Map, node, damage, out bool broke))
            ShowDamageResult(node.Id, node, broke);   // over-head bar + hit spark, same as any struck creature

        if (broke) GiveHarvestYield(def);
        else BreakToolMaybe(tool, def, toolIndex, damage);   // an intact node can still cost you the tool
        return true;
    }

    /// <summary>The node this tool would work on: the tile you FACE first (same reach as a melee swing, so
    /// it's aimable), then any of the four around you. Returns nulls when there's nothing to harvest.</summary>
    private (Mob? node, Content.HarvestNodeDef? def, int toolIndex) FindHarvestNode(ItemDef tool)
    {
        if (Content.HarvestNodes.Count == 0) return (null, null, -1);

        var (fx, fy) = FrontTile();
        var tiles = new[] { (fx, fy), (_char.X, _char.Y - 1), (_char.X + 1, _char.Y),
                            (_char.X, _char.Y + 1), (_char.X - 1, _char.Y) };
        foreach (var (x, y) in tiles)
        {
            var mob = _world.MobAt(_char.Map, x, y);
            if (mob is null || !Content.HarvestNodes.TryGetValue(mob.Key, out var def)) continue;
            int idx = def.ToolIndex(tool.Key);
            // Right node, wrong tool -> not a harvest at all, so the item drops as usual. RTK scolds you
            // ("You cannot mine with an empty hand") because there it's a swing you already committed to;
            // here the drop still has a perfectly good meaning, so let it happen.
            if (idx < 0) continue;
            if (def.Skill.Length > 0 && !CraftingToggles.IsEnabled(def.Skill)) continue;   // era-gated off
            return (mob, def, idx);
        }
        return (null, null, -1);
    }

    /// <summary>Pay out a broken node: one guaranteed unit plus <c>Rolls</c> coin-flips of the yield table,
    /// then one roll on the bonus table. A full pack stops the payout where it is and says so — the rest is
    /// forfeit, which is RTK's behaviour too (its addItem simply fails).</summary>
    private void GiveHarvestYield(Content.HarvestNodeDef def)
    {
        int given = 0;
        bool room = true;

        for (int i = 0; room && i <= def.Rolls; i++)
        {
            if (i > 0 && Random.Shared.Next(2) != 0) continue;     // RTK: `local rand = math.random(1, 2)`
            var item = PickWeighted(def.Yield);
            if (item is null) break;
            if (GiveRewardItem(item, 1)) given++; else room = false;
            // <- a skill point per item gathered would go here (RTK crafting.skillChanceIncrease)
        }

        if (room && def.Bonus.Length > 0)
        {
            double roll = Random.Shared.NextDouble() * 100.0, acc = 0;
            foreach (var (item, pct) in def.Bonus)
                if (roll < (acc += pct)) { GiveRewardItem(item, 1); break; }   // remainder = nothing
        }

        if (!room) Notify("Your bags are full.");
        else if (given > 0 && def.Message.Length > 0) Notify(def.Message);
    }

    /// <summary>RTK's tree-felling tool breakage: 1-in-(base + this swing's damage), so a harder swing is a
    /// riskier one. The tool is destroyed outright — it is in the pack, not worn, so there's no equipment
    /// slot to strip the way RTK does.</summary>
    private void BreakToolMaybe(ItemDef tool, Content.HarvestNodeDef def, int toolIndex, int damage)
    {
        int chance = def.BreakChanceFor(toolIndex);
        if (chance <= 0 || Random.Shared.Next(chance + damage) != 0) return;
        if (!TakeItem(tool.Key, 1)) return;
        Notify($"Your {tool.Name} has broke!");   // verbatim RTK wording, grammar and all
    }

    /// <summary>Pick one entry from a weighted table (weights are relative, so something always comes out).
    /// Null only if the table is empty.</summary>
    private static string? PickWeighted((string Item, double Weight)[] table)
    {
        double total = 0;
        foreach (var (_, w) in table) total += w;
        if (total <= 0) return table.Length > 0 ? table[0].Item : null;

        double roll = Random.Shared.NextDouble() * total, acc = 0;
        foreach (var (item, w) in table)
            if (roll < (acc += w)) return item;
        return table[^1].Item;
    }
}
