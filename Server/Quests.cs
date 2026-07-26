namespace Server;

/// <summary>
/// A single-giver quest: its identity plus the NPC conversation that offers / nudges / turns it in. The whole
/// quest reads as linear script through <see cref="NpcContext"/> (menu/say + the quest helpers on it),
/// branching on the player's stage — a plain int the quest owns the meaning of (the tutorial chain runs 0..14).
/// Stages persist in <see cref="Shared.Character.Quests"/>. Bind a quest to its giver by NPC id in
/// <see cref="Quests.ByNpc"/> and
/// the NPC gains a <see cref="QuestAbility"/> menu automatically (see <see cref="NpcScripts.For"/>).
///
/// This models a per-NPC stage-machine quest (like the RTK tutorial chain). Self-contained multi-entry quests
/// that don't fit one <c>Talk</c> — e.g. the repeatable minor-quest (request/complete) — are their own ability
/// instead; see <see cref="MinorQuestAbility"/>.
/// </summary>
public sealed class QuestDef
{
    /// <summary>Unique quest id + the base key for its progress state (never shown to the player).</summary>
    public required string Key { get; init; }
    /// <summary>Display name for the NPC menu entry.</summary>
    public required string Name { get; init; }
    /// <summary>The whole conversation: greet / offer / in-progress nudge / turn-in / closing, branching on the
    /// player's current stage. Runs when the player picks this quest at its giver NPC.</summary>
    public required Func<NpcContext, Task> Talk { get; init; }
}

/// <summary>
/// The catalogue of single-giver quests and which NPC gives each. <see cref="ForNpc"/> drives the per-NPC quest
/// menu. Populated by the tutorial-chain port; empty for now.
/// </summary>
public static class Quests
{
    // giver NpcId -> the quests it offers. Jadespear #49 and Ironheart #20 share the tutorial chain (both are
    // MainTutorialNpc in the RTK data), so the same QuestDef is listed under both.
    private static readonly Dictionary<int, QuestDef[]> ByNpc = new()
    {
        [20] = new[] { TutorialQuest.Def },   // Ironheart
        [49] = new[] { TutorialQuest.Def },   // Jadespear
    };

    /// <summary>The quests a given NPC offers (empty if none).</summary>
    public static IReadOnlyList<QuestDef> ForNpc(int npcId) =>
        ByNpc.TryGetValue(npcId, out var q) ? q : System.Array.Empty<QuestDef>();
}
