using Shared;

namespace Server;

/// <summary>
/// Which NPCs are switched ON vs OFF, without deleting their rows from NPCs0.csv ("they can stay in the
/// data"). Same flat-file + <c>!reload</c> pattern as every other registry in <see cref="Content"/>
/// (maps/mobs/items/etc.) and as <see cref="CraftingToggles"/> — NOT a live SQLite-backed toggle. Two
/// layers decide it:
///   1. <see cref="DefaultDisabled"/> — a code-level default-OFF set (the tavern "small guy" hands, Ox/Taur:
///      the InnNpc2 rows whose only service was the ferry/transport we've retired). These are off out of the
///      box on any fresh checkout.
///   2. <see cref="Content.NpcToggleOverrides"/> — rows from <c>data/game-data/NpcToggles.csv</c>
///      (<c>NpcId,Enabled</c> columns, env override <c>NEXUS_NPC_TOGGLES</c>). Only ids actually listed
///      there override the code default; anything absent falls through to (1). Edit the file and run
///      <c>!reload</c> to change it — <see cref="World.ReconcileNpcToggles"/> spawns/despawns live NPCs to
///      match on every reload, no restart required.
///
/// This is distinct from <see cref="Content"/>'s <c>DroppedNpcIds</c>: that's a permanent, non-toggleable
/// drop for NPCs whose sprite can't render at all (look &gt; the client's Monster.tbl ceiling). Toggles are
/// for NPCs that work fine but we choose to hide.
/// </summary>
public static class NpcToggles
{
    // The tavern-hand "small guy" NPCs (InnNpc2 = Ox / Taur), whose only ability was Transport + Date/Time.
    // Off by default; put an override row in NpcToggles.csv + !reload to bring any of them back.
    public static readonly IReadOnlySet<int> DefaultDisabled = new HashSet<int> { 54, 56, 57, 59, 61, 63 };

    /// <summary>Whether an NPC should be placed in the world. File override wins; else on unless
    /// default-off.</summary>
    public static bool IsEnabled(int npcId) =>
        Content.NpcToggleOverrides.TryGetValue(npcId, out var forced) ? forced : !DefaultDisabled.Contains(npcId);
}
