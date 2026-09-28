using System.Text.RegularExpressions;
using Shared;

namespace Server;

/// <summary>
/// One quest's view of <see cref="Character.Quests"/>, the flat string-to-int map every quest has always
/// saved into. A <c>QuestState</c> owns a NAMESPACE (the quest's name, <c>leviathan</c>) and names each
/// value in it by a SLOT: <c>stage</c>, <c>flag.gave_gold</c>, <c>kills.rabbit</c>, <c>timer.recoat</c>,
/// <c>count.worships</c>. Quest code reads and writes through it — <c>ctx.Quest(Key).Stage</c>,
/// <c>ctx.Quest(Key).Set("flag.gave_gold", 1)</c> — and never spells a saved key itself.
///
/// <para><b>No storage migration.</b> Every name resolves to the key characters were ALREADY saved under, so
/// a character loaded, played and saved through this type carries byte-identical quest keys and values
/// (Tests/QuestStateTests.cs proves it against a real character blob). Resolution, in order:</para>
/// <list type="number">
/// <item><see cref="Registry"/>, the empty namespace, is RTK's flat <c>player.registry</c>: the slot IS the
/// saved key. The Lua verbs (<c>stage</c>/<c>reg</c> in npc_dialog.lua, <c>reg</c> in the spell verbs,
/// <c>actorQuest</c> in mob_ai.lua) and the registry globals several features share (<c>home</c>,
/// <c>sage_rung</c>, <c>dog_flag</c>, <c>carnage_wins</c>, <c>mentored</c>, <c>damage_shotgun</c>…) live
/// here, by the names RTK gave them.</item>
/// <item><c>stage</c> is saved under the bare namespace. That is the scheme every quest already used, so a
/// quest's namespace IS its stage key and a stage needs no alias.</item>
/// <item>Every other slot is looked up in the alias table below: <see cref="Aliases"/> for fixed keys,
/// <see cref="Families"/> for keys with a variable part (a mob, a tier, a step).</item>
/// <item>A slot in neither THROWS. A mistyped slot must not quietly start a fresh counter at 0 and lose a
/// player's progress (AGENTS.md rule 3). A new slot is added to the table, mapped to its own dotted name
/// (<c>("leviathan", "flag.x") = "leviathan.flag.x"</c>); no saved key has a dot in it, so a dotted key can
/// never collide with an old one.</item>
/// </list>
///
/// <para><b>The only code that touches <see cref="Character.Quests"/></b> is the store section at the bottom
/// of this file, bar one write <c>Session.SetNation</c> still makes (#307).
/// <c>QuestStateTests.NothingButQuestStateTouchesTheQuestMap</c> fails the test run when anything else does.
/// <see cref="Character.QuestStrings"/> (the string registry) is a separate store and is not covered here.</para>
///
/// <para>A value type: two references and a name, made per call (<see cref="NpcContext.Quest"/>,
/// <see cref="Session.Quest"/>) and held only in locals, never in a field. Bound to a session, a read sees its live
/// character and a write takes the session monitor and saves (<see cref="Session.SetQuestStage"/>); over a
/// bare character (<see cref="Over"/>) it does neither, which is what tests and offline tools want.</para>
/// </summary>
public readonly struct QuestState
{
    /// <summary>The flat namespace: the slot is the saved key, verbatim. See the type doc.</summary>
    public const string Registry = "";

    /// <summary>The slot every quest's stage machine lives in, saved under the bare namespace.</summary>
    public const string StageSlot = "stage";

    private readonly Session? _session;
    private readonly Character? _char;

    /// <summary>The namespace — the quest's name and its stage key.</summary>
    public string Name { get; }

    internal QuestState(Session session, string name) { _session = session; _char = null; Name = name; }
    private QuestState(Character character, string name) { _session = null; _char = character; Name = name; }

    /// <summary>A view over a bare character, with no session behind it: no monitor, no save.</summary>
    public static QuestState Over(Character character, string name) => new(character, name);

    /// <summary>This quest's stage (0 = not started; the quest owns the rest of the meaning).</summary>
    public int Stage => Get(StageSlot);

    /// <summary>Set this quest's stage (persists).</summary>
    public void SetStage(int stage) => Set(StageSlot, stage);

    /// <summary>A slot's value; 0 if it was never set.</summary>
    public int Get(string slot)
    {
        string key = Resolve(Name, slot);
        return _session is not null ? _session.QuestStage(key) : Read(_char!, key);
    }

    /// <summary>Set a slot (persists, through the session when there is one).</summary>
    public void Set(string slot, int value)
    {
        string key = Resolve(Name, slot);
        if (_session is not null) _session.SetQuestStage(key, value);
        else Write(_char!, key, value);
    }

    /// <summary>The key <paramref name="slot"/> of <paramref name="name"/> is saved under. Throws for a
    /// slot the alias table does not list — see the type doc.</summary>
    public static string Resolve(string name, string slot)
    {
        if (name.Length == 0) return slot;
        if (slot == StageSlot) return name;
        if (Aliases.TryGetValue((name, slot), out var saved)) return saved;
        foreach (var family in CompiledFamilies)
            if (family.TryResolve(name, slot, out saved)) return saved;
        throw new InvalidOperationException(
            $"Quest slot '{name}.{slot}' has no saved key. Add it to the alias table in Server/QuestState.cs " +
            $"(a new slot maps to its own dotted name, \"{name}.{slot}\").");
    }

    // =============================================================================================
    // The alias table: every quest slot, and the key characters were saved with before this type existed.
    // Keep docs/common/Quest-Registry.md in step with it. Stages are not listed (rule 2 above), nor is the
    // flat registry (rule 1).
    // =============================================================================================

    /// <summary>Fixed keys: (namespace, slot) to saved key.</summary>
    internal static readonly IReadOnlyDictionary<(string Name, string Slot), string> Aliases =
        new Dictionary<(string Name, string Slot), string>
        {
            // tutorial_quest: TutorialQuest.cs. learned_to_fish and talked_to_tutor are also written by the
            // fishing and tutor-talk abilities in NpcAbility.cs, helped_haguru by npc_dialog.lua (flat).
            [("tutorial_quest", "flag.gave_gold")]             = "tutorial_quest1_gave_gold",
            [("tutorial_quest", "flag.gave_meat")]             = "tutorial_quest2_gave_meat",
            [("tutorial_quest", "flag.gave_sword")]            = "tutorial_quest8_gave_sword",
            [("tutorial_quest", "flag.learned_to_fish")]       = "learned_to_fish",
            [("tutorial_quest", "flag.talked_to_tutor")]       = "talked_to_tutor",
            [("tutorial_quest", "flag.helped_haguru")]         = "helped_haguru",
            [("tutorial_quest", "flag.visited_yon_and_weaved")] = "visited_yon_and_weaved",

            // tiger_armor: TigerMailQuest.cs, and the tutor's Tiger Essence branch in TutorialQuest.cs.
            [("tiger_armor", "flag.met_claw")]                 = "tiger_essence_met_claw",
            [("tiger_armor", "level.nudged")]                  = "tiger_essence_nudged_level",

            // novice_quest: NoviceQuest.cs.
            [("novice_quest", "kills.rabbit")]                 = "novice_quest1_rabbit_snapshot",
            [("novice_quest", "kills.squirrel")]               = "novice_quest2_squirrel_snapshot",
            [("novice_quest", "flag.gave_garb")]               = "novice_quest2_gave_garb",
            [("novice_quest", "flag.asked_soothe")]            = "novice_quest3_asked_soothe",

            // mage_stone: MageStoneQuest.cs and the crypt spirit (Session.Navigation.cs).
            [("mage_stone", "flag.met_ghost")]                 = "mage_stone_met_ghost",

            // minor_quest: MinorQuest.cs. The active target is in the STRING registry under "minor_quest".
            [("minor_quest", "tier")]                          = "minor_quest_tier",
            [("minor_quest", "timer.cooldown")]                = "minor_quest_timer",
            [("minor_quest", "count.completed")]               = "minor_quests_completed",

            // nagnang_warrior_trial: NagnangShieldQuest.cs and the Gauntlet tiles (Session.Navigation.cs).
            [("nagnang_warrior_trial", "kills.forbidden")]     = "nagnang_trial_kills",

            // nangen_acolyte: PoetWhipQuest.cs and the drop rite (Session.Navigation.cs).
            [("nangen_acolyte", "flag.gave_pipe")]             = "gave_sonhi_pipe",
            [("nangen_acolyte", "timer.water")]                = "sacred_water_timer",
            [("nangen_acolyte", "flag.destroyed_infected")]    = "destroyed_infected",
            [("nangen_acolyte", "kills.magic_rabbit")]         = "nangen_rabbit_base",

            // sute_quest: SuteQuest.cs and the cave mouth (Session.Navigation.cs).
            [("sute_quest", "flag.dye")]                       = "sute_quest_dye",
            [("sute_quest", "timer.recoat")]                   = "sute_quest_timer",

            // totem_worship: TotemWorship.cs.
            [("totem_worship", "timer.daily")]                 = "totem_worship_daily_timer",
            [("totem_worship", "count.karma_misses")]          = "totem_worship_karma_force",
            [("totem_worship", "count.worships")]              = "totem_total_worships",

            // sun_armor: the Poet Sun totem step (ArmorQuest.cs), advanced by TotemWorship.cs.
            [("sun_armor", "count.totems")]                    = "sun_armor_totem",

            // white_moon_axe: WhiteMoonAxeQuest.cs.
            [("white_moon_axe", "kills.pale_scorpion")]        = "wma_scorpion_base",
            [("white_moon_axe", "kills.skeleton_ju")]          = "wma_ju_base",
        };

    /// <summary>Keys with a variable part. <c>{x}</c> matches a run with no dot in it and is copied into the
    /// saved key. Checked in order, after <see cref="Aliases"/>.</summary>
    internal static readonly IReadOnlyList<(string Name, string Slot, string Saved)> Families = new[]
    {
        // The Star/Moon/Sun chains' per-step markers (ArmorQuestAbility.cs). The namespace is the chain's
        // stage key ("star_armor"); the markers are per TIER, not per path, as they always were.
        ("{tier}_armor", "step.{n}.opened",      "aq_{tier}_{n}_!"),
        ("{tier}_armor", "step.{n}.total_kills", "aq_{tier}_{n}_@"),
        ("{tier}_armor", "step.{n}.kills.{mob}", "aq_{tier}_{n}_{mob}"),

        // MinorQuest.cs: per-target kill snapshot at accept.
        ("minor_quest", "kills.{mob}", "minor_quest_kill_count_{mob}"),

        // MageStoneQuest.cs: set by game-data/mob_ai.lua (flat) when a cursed mouse is struck.
        ("mage_stone", "flag.zapped.{mouse}", "zapped_{mouse}"),
    };

    private static readonly Family[] CompiledFamilies = Families.Select(f => new Family(f.Name, f.Slot, f.Saved)).ToArray();

    private sealed class Family
    {
        private static readonly Regex Hole = new(@"\{(\w+)\}", RegexOptions.CultureInvariant);
        private readonly Regex _match;
        private readonly string _saved;

        public Family(string name, string slot, string saved)
        {
            _match = new Regex("^" + Pattern(name) + "\n" + Pattern(slot) + "$",
                               RegexOptions.CultureInvariant);
            _saved = saved;
        }

        // Literal text is escaped; each {x} becomes a named group over a dot-free run.
        private static string Pattern(string template)
        {
            var sb = new System.Text.StringBuilder();
            int at = 0;
            foreach (Match m in Hole.Matches(template))
            {
                sb.Append(Regex.Escape(template[at..m.Index]));
                sb.Append("(?<").Append(m.Groups[1].Value).Append(">[^.\\n]+)");
                at = m.Index + m.Length;
            }
            return sb.Append(Regex.Escape(template[at..])).ToString();
        }

        public bool TryResolve(string name, string slot, out string saved)
        {
            var m = _match.Match(name + "\n" + slot);
            if (!m.Success) { saved = ""; return false; }
            saved = Hole.Replace(_saved, h => m.Groups[h.Groups[1].Value].Value);
            return true;
        }
    }

    // =============================================================================================
    // The store. The only code in the server that reads or writes Character.Quests (bar SetNation, #307). Callers pass a
    // SAVED key (already resolved); the session primitives (Session.QuestStage / SetQuestStage) own the
    // monitor and the save around them, and @quest / @questreset read and clear the raw map through them.
    // =============================================================================================

    internal static int Read(Character c, string key) => c.Quests.GetValueOrDefault(key);

    internal static void Write(Character c, string key, int value) => c.Quests[key] = value;

    /// <summary>The saved map, read-only, by saved key: the GM dump and single-key read (<c>@quest</c>).</summary>
    internal static IReadOnlyDictionary<string, int> Saved(Character c) => c.Quests;

    /// <summary>Drop a saved key outright (<c>@quest &lt;key&gt; 0</c>). True if it was there.</summary>
    internal static bool Remove(Character c, string key) => c.Quests.Remove(key);

    /// <summary>Drop every saved key (<c>@questreset</c>).</summary>
    internal static void Clear(Character c) => c.Quests.Clear();
}
