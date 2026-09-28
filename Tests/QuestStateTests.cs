using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Server;
using Shared;
using Xunit;

namespace Tests;

/// <summary>
/// <see cref="QuestState"/> is a naming layer over the quest map every character already carries, and the
/// failure it can cause is silent: a slot that resolves to the wrong key reads 0, and the player's progress
/// looks lost with nothing in the log. These pin the three things that keep it honest (#51):
/// <list type="bullet">
/// <item>every quest slot resolves to the key characters were saved under BEFORE the type existed;</item>
/// <item>a real character blob, loaded, read and written back through the type, saves byte-identical;</item>
/// <item>nothing outside <c>Server/QuestState.cs</c> touches <see cref="Character.Quests"/>.</item>
/// </list>
/// </summary>
public class QuestStateTests
{
    // The saved key of every quest slot, copied from the quest classes' own constants at b43a8e5 (the base
    // #51 was cut from, where each quest spelled its keys itself) — NOT from QuestState's table, which is
    // the thing under test. One row per exact alias, plus instances of each variable-part family.
    private static readonly (string Name, string Slot, string Saved)[] LegacyKeys =
    {
        // TutorialQuest.cs:31-37, NpcAbility.cs:645/951, npc_dialog.lua:398
        ("tutorial_quest", "flag.gave_gold",              "tutorial_quest1_gave_gold"),
        ("tutorial_quest", "flag.gave_meat",              "tutorial_quest2_gave_meat"),
        ("tutorial_quest", "flag.gave_sword",             "tutorial_quest8_gave_sword"),
        ("tutorial_quest", "flag.learned_to_fish",        "learned_to_fish"),
        ("tutorial_quest", "flag.talked_to_tutor",        "talked_to_tutor"),
        ("tutorial_quest", "flag.helped_haguru",          "helped_haguru"),
        ("tutorial_quest", "flag.visited_yon_and_weaved", "visited_yon_and_weaved"),
        // TigerMailQuest.cs:119 (MetClawReg), TutorialQuest.cs:37 (NudgedAt)
        ("tiger_armor", "flag.met_claw",                  "tiger_essence_met_claw"),
        ("tiger_armor", "level.nudged",                   "tiger_essence_nudged_level"),
        // NoviceQuest.cs:59-65
        ("novice_quest", "kills.rabbit",                  "novice_quest1_rabbit_snapshot"),
        ("novice_quest", "kills.squirrel",                "novice_quest2_squirrel_snapshot"),
        ("novice_quest", "flag.gave_garb",                "novice_quest2_gave_garb"),
        ("novice_quest", "flag.asked_soothe",             "novice_quest3_asked_soothe"),
        // MageStoneQuest.cs:57-61 (ZapReg + mouse, GhostReg)
        ("mage_stone", "flag.met_ghost",                  "mage_stone_met_ghost"),
        ("mage_stone", "flag.zapped.yin_mouse",           "zapped_yin_mouse"),
        ("mage_stone", "flag.zapped.yang_mouse",          "zapped_yang_mouse"),
        ("mage_stone", "flag.zapped.void_mouse",          "zapped_void_mouse"),
        // MinorQuest.cs:28-31 (KKillPfx + mob, KTier, KTimer, KCompleted)
        ("minor_quest", "tier",                           "minor_quest_tier"),
        ("minor_quest", "timer.cooldown",                 "minor_quest_timer"),
        ("minor_quest", "count.completed",                "minor_quests_completed"),
        ("minor_quest", "kills.squirrel",                 "minor_quest_kill_count_squirrel"),
        // NagnangShieldQuest.cs:59
        ("nagnang_warrior_trial", "kills.forbidden",      "nagnang_trial_kills"),
        // PoetWhipQuest.cs:69-76
        ("nangen_acolyte", "flag.gave_pipe",              "gave_sonhi_pipe"),
        ("nangen_acolyte", "timer.water",                 "sacred_water_timer"),
        ("nangen_acolyte", "flag.destroyed_infected",     "destroyed_infected"),
        ("nangen_acolyte", "kills.magic_rabbit",          "nangen_rabbit_base"),
        // SuteQuest.cs:64-66
        ("sute_quest", "flag.dye",                        "sute_quest_dye"),
        ("sute_quest", "timer.recoat",                    "sute_quest_timer"),
        // TotemWorship.cs:46-50
        ("totem_worship", "timer.daily",                  "totem_worship_daily_timer"),
        ("totem_worship", "count.karma_misses",           "totem_worship_karma_force"),
        ("totem_worship", "count.worships",               "totem_total_worships"),
        // ArmorQuest.cs:70 (TotemStepReg)
        ("sun_armor", "count.totems",                     "sun_armor_totem"),
        // WhiteMoonAxeQuest.cs:61-62
        ("white_moon_axe", "kills.pale_scorpion",         "wma_scorpion_base"),
        ("white_moon_axe", "kills.skeleton_ju",           "wma_ju_base"),
        // ArmorQuestAbility.cs:102/272-295: pfx = $"aq_{chain.Tier}_{stage}_" + "!" / "@" / mob
        ("star_armor", "step.0.opened",                   "aq_star_0_!"),
        ("star_armor", "step.0.total_kills",              "aq_star_0_@"),
        ("star_armor", "step.0.kills.rabbit",             "aq_star_0_rabbit"),
        ("moon_armor", "step.2.opened",                   "aq_moon_2_!"),
        ("moon_armor", "step.2.total_kills",              "aq_moon_2_@"),
        ("moon_armor", "step.2.kills.massive_scorpion",   "aq_moon_2_massive_scorpion"),
        ("sun_armor",  "step.1.kills.sute",               "aq_sun_1_sute"),
    };

    // Every stage key the C# quests use: the namespace IS the stage key, so these need no alias.
    private static readonly string[] StageKeys =
    {
        "tutorial_quest", "novice_quest", "leviathan", "dagger_uniform", "forgotten_path", "mage_stone",
        "nagnang_warrior_trial", "nangen_acolyte", "white_moon_axe", "tiger_armor", "star_armor", "moon_armor",
        "sun_armor", "lesser_alliance_rat", "newbie_area_quest",
    };

    // Flat registry keys (Lua, and globals several features share): the slot is the saved key.
    private static readonly string[] RegistryKeys =
    {
        "home", "sage_rung", "sage_timer", "dog_flag", "dog_linguist", "carnage_wins", "mentored",
        "craft_tailoring", "baekhos_cunning", "chu_rua_tiger_gone", "chu_rua_rabbit_greeted",
        "newbie_coords_learned", "newbie_rabbit_snapshot", "dog_task_fissure", "dog_kill_any",
        "dog_linguist_echo_2", "myung_suck_threshold", "paid_gold_for_frost_sabre", "damage_shotgun",
    };

    private static string FixturePath() =>
        Path.Combine(RepoPaths.Root(), "Tests", "Fixtures", "character-quests-v1.json");

    [Fact]
    public void EveryQuestSlotResolvesToTheKeyItWasSavedUnder()
    {
        foreach (var (name, slot, saved) in LegacyKeys)
            Assert.True(QuestState.Resolve(name, slot) == saved,
                        $"{name}.{slot} resolves to '{QuestState.Resolve(name, slot)}', but characters saved it as '{saved}'");
        foreach (var stage in StageKeys)
            Assert.Equal(stage, QuestState.Resolve(stage, QuestState.StageSlot));
        foreach (var key in RegistryKeys)
            Assert.Equal(key, QuestState.Resolve(QuestState.Registry, key));

        // The saved key code outside the quests still spells (Session.PushTigerEssence), and the Sun chains'
        // namespace, which the totem step reads by a constant rather than from its chain.
        Assert.Equal("tiger_essence_met_claw", TigerMailQuest.MetClawReg);
        Assert.All(ArmorQuest.Chains.Values.Where(c => c.Tier == "sun"), c => Assert.Equal(ArmorQuest.SunKey, c.StageKey));
    }

    /// <summary>Every exact entry in the table is pinned above, so a new alias cannot land unpinned — and a
    /// typo in a saved key has to be made twice, in two files, to go unnoticed.</summary>
    [Fact]
    public void TheAliasTableHasNoEntryTheLegacyCatalogueDoesNotPin()
    {
        var pinned = LegacyKeys.ToDictionary(k => (k.Name, k.Slot), k => k.Saved);
        foreach (var (key, saved) in QuestState.Aliases)
        {
            Assert.True(pinned.TryGetValue(key, out var want), $"alias {key.Name}.{key.Slot} is not pinned in LegacyKeys");
            Assert.Equal(want, saved);
        }
        // No two slots share one saved key: two names for one value would let one quest clobber another.
        var targets = QuestState.Aliases.Values.ToList();
        Assert.Equal(targets.Count, targets.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>A slot the table does not list must fail loudly, not start a fresh counter at 0 under a key no
    /// character has — which is exactly what a typo would otherwise do.</summary>
    [Fact]
    public void ASlotTheTableDoesNotListThrows()
    {
        var c = new Character();
        Assert.Throws<InvalidOperationException>(() => QuestState.Resolve("leviathan", "flag.typo"));
        Assert.Throws<InvalidOperationException>(() => QuestState.Over(c, "tutorial_quest").Get("flag.gave_gld"));
        Assert.Throws<InvalidOperationException>(() => QuestState.Over(c, "tutorial_quest").Set("flag.gave_gld", 1));
        Assert.Empty(QuestState.Saved(c));   // the refused write wrote nothing
    }

    /// <summary>The fixture test: a real character blob (the #32 fixture's shape, carrying every saved key the
    /// quests use, each with a distinct value) is loaded through the store, every slot is read through
    /// QuestState and must see its own value, every slot is written back through QuestState, and the blob
    /// the store would save is byte-for-byte the one that was loaded.</summary>
    [Fact]
    public void SavedQuestKeysRoundTripByteIdenticalThroughQuestState()
    {
        string json = File.ReadAllText(FixturePath()).TrimEnd('\n');
        var character = CharacterStore.Deserialize(json);
        // Baseline: the fixture is exactly what the store writes, so the comparison below is of bytes.
        Assert.Equal(json, CharacterStore.Serialize(character));

        // The saved values, read from the blob itself rather than through any code under test.
        var blob = JsonNode.Parse(json)!["Quests"]!.AsObject()
                           .ToDictionary(p => p.Key, p => p.Value!.GetValue<int>(), StringComparer.Ordinal);

        var addressed = new HashSet<string>(StringComparer.Ordinal);
        void Check(string name, string slot, string saved)
        {
            Assert.True(blob.ContainsKey(saved), $"the fixture has no '{saved}' — add it, or fix the catalogue");
            var q = QuestState.Over(character, name);
            Assert.True(blob[saved] == q.Get(slot), $"{name}.{slot} read {q.Get(slot)}, the blob holds {saved} = {blob[saved]}");
            q.Set(slot, q.Get(slot));
            addressed.Add(saved);
        }
        foreach (var (name, slot, saved) in LegacyKeys) Check(name, slot, saved);
        foreach (var stage in StageKeys) Check(stage, QuestState.StageSlot, stage);
        foreach (var key in RegistryKeys) Check(QuestState.Registry, key, key);

        // Every key in the blob was reached by some name, so the fixture tests nothing it does not cover.
        var unreached = blob.Keys.Where(k => !addressed.Contains(k)).ToList();
        Assert.True(unreached.Count == 0, "fixture keys no quest name reaches: " + string.Join(", ", unreached));
        Assert.Equal(json, CharacterStore.Serialize(character));
    }

    /// <summary>The acceptance line "no raw Quests[...] access outside QuestState", as a fact. Scans every
    /// production project's source (comment lines stripped) for the member name and fails on any use outside
    /// Server/QuestState.cs and the field's own declaration.</summary>
    [Fact]
    public void NothingButQuestStateTouchesTheQuestMap()
    {
        string root = RepoPaths.Root();
        string[] projects = { "Server", "Shared", "Shared.Core", "LoginServer", "LoginProbe", "Protocol.Tk495", "Tools", "MapEditor", "IconStudio" };
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Server/QuestState.cs",
            // The field itself.
            "Shared/Character.cs: public Dictionary<string, int> Quests = new();",
            // SetNation clearing the bound home. It sits outside the region #51 was allowed to edit in
            // Session.CharacterApi.cs; routing it through QuestState is #307. Delete this line when it is.
            "Server/Session.CharacterApi.cs: _char.Quests[HomeReg] = HomeNone;",
        };
        var member = new Regex(@"\bQuests\b", RegexOptions.CultureInvariant);

        var hits = new List<string>();
        var seenAllowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            string dir = Path.Combine(root, project);
            Assert.True(Directory.Exists(dir), $"{project}/ is gone — update the scan so it does not silently shrink");
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Split('/').Any(seg => seg is "bin" or "obj")) continue;
                if (allowed.Contains(rel)) { seenAllowed.Add(rel); continue; }
                foreach (var raw in File.ReadLines(file))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("//", StringComparison.Ordinal)) continue;
                    if (!member.IsMatch(line)) continue;
                    string hit = $"{rel}: {line}";
                    if (allowed.Contains(hit)) { seenAllowed.Add(hit); continue; }
                    hits.Add(hit);
                }
            }
        }

        Assert.True(hits.Count == 0, "Character.Quests touched outside QuestState:\n" + string.Join("\n", hits));
        Assert.Equal(allowed.OrderBy(a => a, StringComparer.Ordinal), seenAllowed.OrderBy(a => a, StringComparer.Ordinal));
    }
}
