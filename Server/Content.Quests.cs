namespace Server;

/// <summary>One "slay-one-of" quest target from RTK MinorQuest.lua (extracted to MinorQuests.csv). A quest is
/// picked at random among those whose Level/Stat/Mark ranges the player falls in; the objective is met by
/// killing any one of <see cref="Mobs"/>. <see cref="Tier"/> is "Minor"/"Major"/"Epic".</summary>
public sealed record MinorQuestDef(
    string Tier, string Key, string DisplayName, IReadOnlyList<string> Mobs,
    int MinLevel, int MaxLevel, long MinStat, long MaxStat, int MinMark, int MaxMark);

public static partial class Content
{

    // "Slay one X" quest targets (RTK MinorQuest.lua -> MinorQuests.csv), grouped by tier for the trainer
    // minor-quest ability. See Server/MinorQuest.cs.
    public static IReadOnlyList<MinorQuestDef> MinorQuests
    {
        get => _snapshotBuilder?.MinorQuests ?? Snapshot.MinorQuests;
        private set => Builder.MinorQuests = value;
    }

    // ---- Mythic alliances (game-data/MythicAlliances.csv) -------------------------------------------
    // One row per zodiac animal, describing its OWN cave: its enemy, its two sets of bosses, and the
    // tribute an ally of its enemy must steal from it. Consumed by Server/MythicAlliance.cs, which reads a
    // quest off the ENEMY's row. An empty file simply means no mythic answers to anything, the same
    // fail-soft posture as every other table here.
    public static IReadOnlyList<MythicAllianceDef> MythicAlliances
    {
        get => _snapshotBuilder?.MythicAlliances ?? Snapshot.MythicAlliances;
        private set => Builder.MythicAlliances = value;
    }

    // Quest-locked warps (game-data/WarpQuestLocks.csv): a warp switched OFF until a quest reaches a
    // stage. Only the warp is affected — the tile stays walkable and the player is never blocked or pushed
    // back; see Session.WarpLockedByQuest. Keyed on the map PAIR so the lock is one-way: walking back the
    // way you came is never affected.
    public sealed record WarpQuestLock(ushort FromMap, ushort ToMap, string QuestKey, int MinStage, string Message);

    public static IReadOnlyDictionary<(ushort From, ushort To), WarpQuestLock> WarpQuestLocks
    {
        get => _snapshotBuilder?.WarpQuestLocks ?? Snapshot.WarpQuestLocks;
        private set => Builder.WarpQuestLocks = value;
    }

    // ---- Star/Moon/Sun armor quest gates (game-data/ArmorQuests.csv) ------------------------------
    /// <summary>Level + karma tier each armor chain demands, keyed by (base path id, tier name). The tiers
    /// live in a file because that is the one field the period sources genuinely fight over — see the
    /// header comment in ArmorQuests.csv. A missing row falls back to <see cref="ArmorQuest"/>'s own
    /// defaults, so a deleted file degrades to the shipped values rather than an open gate.</summary>
    public static IReadOnlyDictionary<(int Path, string Tier), (int Level, string Karma)> ArmorQuestGates
    {
        get => _snapshotBuilder?.ArmorQuestGates ?? Snapshot.ArmorQuestGates;
        private set => Builder.ArmorQuestGates = value;
    }
}
