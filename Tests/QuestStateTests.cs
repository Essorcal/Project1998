using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Server;
using Shared;
using Xunit;
using Xunit.Abstractions;

namespace Tests;

/// <summary>
/// <see cref="QuestState"/> is a naming layer over the two quest maps every character already carries, the int map
/// <see cref="Character.Quests"/> and the string registry <see cref="Character.QuestStrings"/>, and the failure it
/// can cause is silent: a slot that resolves to the wrong key reads 0 (or ""), and the player's progress looks lost
/// with nothing in the log. These pin the things that keep it honest (#51, #308, #309, the #310 review's F1):
/// <list type="bullet">
/// <item>every quest slot, int or text, resolves to the key characters were saved under BEFORE it was a slot;</item>
/// <item>every slot the game DATA can produce (each armor chain's steps and mobs, each MinorQuests.csv mob)
/// resolves through the variable-part families to the key the pre-#51 code built;</item>
/// <item>a real character blob, loaded, read and written back through the type, saves byte-identical, and so does
/// a text value of any shape;</item>
/// <item>nothing outside <c>Server/QuestState.cs</c> touches either map.</item>
/// </list>
/// </summary>
public class QuestStateTests
{
    private readonly ITestOutputHelper _out;

    public QuestStateTests(ITestOutputHelper output) => _out = output;

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
        // TigerMailQuest.cs:119 (MetClawReg, gone since #308), TutorialQuest.cs:37 (NudgedAt)
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

    // The saved key of every TEXT slot: the string registry's two keys, as the quests spelled them at 9c00b98,
    // the base #309 was cut from (MinorQuest.cs:26 KActive, Mentorship.cs:40 MentorStr).
    private static readonly (string Name, string Slot, string Saved)[] LegacyTextKeys =
    {
        ("minor_quest", "text.target",                    "minor_quest"),
        ("mentorship",  "text.mentor",                    "mentor"),
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

    // Flat registry TEXT keys: the slot is the saved key, as @quest writes any non-numeric value under the name a
    // GM types. No quest reads these two. The fixture carries them for the two value shapes its quest text keys do
    // not hold there: the empty string (what the minor quest and the mentorship write when they end; the key
    // stays) and non-ASCII text, which the store saves escaped.
    private static readonly string[] RegistryTextKeys = { "fixture_empty", "fixture_non_ascii" };

    private static string FixturePath() =>
        Path.Combine(RepoPaths.Root(), "Tests", "Fixtures", "character-quests-v1.json");

    [Fact]
    public void EveryQuestSlotResolvesToTheKeyItWasSavedUnder()
    {
        foreach (var (name, slot, saved) in LegacyKeys)
            Assert.True(QuestState.Resolve(name, slot) == saved,
                        $"{name}.{slot} resolves to '{QuestState.Resolve(name, slot)}', but characters saved it as '{saved}'");
        foreach (var (name, slot, saved) in LegacyTextKeys)
            Assert.True(QuestState.ResolveText(name, slot) == saved,
                        $"{name}.{slot} resolves to '{QuestState.ResolveText(name, slot)}', but characters saved it as " +
                        $"'{saved}' in the string registry");
        foreach (var stage in StageKeys)
            Assert.Equal(stage, QuestState.Resolve(stage, QuestState.StageSlot));
        foreach (var key in RegistryKeys)
            Assert.Equal(key, QuestState.Resolve(QuestState.Registry, key));
        // The flat registry is verbatim in the string registry too: @quest reads and writes raw text keys there.
        foreach (var (_, _, saved) in LegacyTextKeys)
            Assert.Equal(saved, QuestState.ResolveText(QuestState.Registry, saved));

        // The Sun chains' namespace, which the totem step reads by a constant rather than from its chain.
        Assert.All(ArmorQuest.Chains.Values.Where(c => c.Tier == "sun"), c => Assert.Equal(ArmorQuest.SunKey, c.StageKey));
    }

    /// <summary>Every exact entry in the table is pinned above, so a new alias cannot land unpinned — and a
    /// typo in a saved key has to be made twice, in two files, to go unnoticed.</summary>
    [Fact]
    public void TheAliasTableHasNoEntryTheLegacyCatalogueDoesNotPin()
    {
        var pinned = LegacyKeys.Concat(LegacyTextKeys).ToDictionary(k => (k.Name, k.Slot), k => k.Saved);
        foreach (var (key, saved) in QuestState.Aliases)
        {
            Assert.True(pinned.TryGetValue(key, out var want),
                        $"alias {key.Name}.{key.Slot} is not pinned in LegacyKeys or LegacyTextKeys");
            Assert.Equal(want, saved);
        }
        // No two slots share one saved key in the same map: two names for one value would let one quest clobber
        // another. A text key and an int key may share a name, because they are in different maps.
        foreach (bool text in new[] { false, true })
        {
            var targets = QuestState.Aliases
                .Where(a => a.Key.Slot.StartsWith(QuestState.TextPrefix, StringComparison.Ordinal) == text)
                .Select(a => a.Value).ToList();
            Assert.NotEmpty(targets);
            Assert.Equal(targets.Count, targets.Distinct(StringComparer.Ordinal).Count());
        }
    }

    /// <summary>The quests name their slots by constants (public or private) rather than by the literals
    /// pinned above, so a typo in one of THOSE would only surface as a throw the first time a player reached
    /// that line. Every slot-shaped constant in the server has to be a slot the table knows.</summary>
    [Fact]
    public void EverySlotConstantInTheServerIsInTheTable()
    {
        var shaped = new Regex(@"^(flag|kills|timer|count|level|step|text)\.[^.]+(\.[^.]+)*$|^tier$", RegexOptions.CultureInvariant);
        var known = new HashSet<string>(QuestState.Aliases.Keys.Select(k => k.Slot), StringComparer.Ordinal);
        var found = new List<string>();
        foreach (var type in typeof(QuestState).Assembly.GetTypes())
            foreach (var f in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
                                             BindingFlags.DeclaredOnly))
            {
                if (!f.IsLiteral || f.FieldType != typeof(string)) continue;
                if (f.GetRawConstantValue() is not string value || !shaped.IsMatch(value)) continue;
                found.Add($"{type.Name}.{f.Name}");
                Assert.True(known.Contains(value), $"{type.Name}.{f.Name} = \"{value}\" is not a slot in QuestState's alias table");
            }
        // The sweep has to be looking at the quests at all: these are a few of the constants it must see.
        Assert.Contains("TutorialQuest.GaveGold", found);
        Assert.Contains("TotemWorship.Pity", found);
        Assert.Contains("MinorQuestAbility.KTimer", found);
        Assert.Contains("MinorQuestAbility.KTarget", found);
        Assert.Contains("Mentorship.MentorSlot", found);
    }

    /// <summary>A slot the table does not list must fail loudly, not start a fresh counter at 0 under a key no
    /// character has — which is exactly what a typo would otherwise do. Text slots the same.</summary>
    [Fact]
    public void ASlotTheTableDoesNotListThrows()
    {
        var c = new Character();
        Assert.Throws<InvalidOperationException>(() => QuestState.Resolve("leviathan", "flag.typo"));
        Assert.Throws<InvalidOperationException>(() => QuestState.Over(c, "tutorial_quest").Get("flag.gave_gld"));
        Assert.Throws<InvalidOperationException>(() => QuestState.Over(c, "tutorial_quest").Set("flag.gave_gld", 1));
        Assert.Throws<InvalidOperationException>(() => QuestState.ResolveText("minor_quest", "text.targt"));
        Assert.Throws<InvalidOperationException>(() => QuestState.Over(c, "minor_quest").GetText("text.targt"));
        Assert.Throws<InvalidOperationException>(() => QuestState.Over(c, "minor_quest").SetText("text.targt", "rabbit"));
        Assert.Empty(QuestState.Saved(c));       // the refused writes wrote nothing, in either map
        Assert.Empty(QuestState.SavedText(c));
    }

    /// <summary>The two maps are keyed independently: the minor quest's text target is saved as <c>minor_quest</c> in
    /// the string registry, and its int stage would be <c>minor_quest</c> in the int map. So a slot asked of the
    /// OTHER map's pair must fail loudly too: an int read of <c>text.target</c> that resolved through the table would
    /// read the int map's <c>minor_quest</c>, an unrelated number, with nothing in the log.</summary>
    [Fact]
    public void ASlotAskedOfTheOtherMapThrows()
    {
        var c = new Character();
        var minor = QuestState.Over(c, "minor_quest");
        minor.SetStage(7);                       // an int value under the same saved name, for a wrong read to find
        minor.SetText("text.target", "squirrel");

        Assert.Throws<InvalidOperationException>(() => QuestState.Resolve("minor_quest", "text.target"));
        Assert.Throws<InvalidOperationException>(() => minor.Get("text.target"));
        Assert.Throws<InvalidOperationException>(() => minor.Set("text.target", 1));
        Assert.Throws<InvalidOperationException>(() => QuestState.ResolveText("minor_quest", "tier"));
        Assert.Throws<InvalidOperationException>(() => QuestState.ResolveText("minor_quest", QuestState.StageSlot));
        Assert.Throws<InvalidOperationException>(() => minor.GetText("tier"));
        Assert.Throws<InvalidOperationException>(() => minor.SetText("tier", "x"));

        // Nothing moved: each map still holds exactly what it was given.
        Assert.Equal(7, minor.Stage);
        Assert.Equal("squirrel", minor.GetText("text.target"));
        Assert.Equal(new[] { "minor_quest" }, QuestState.Saved(c).Keys);
        Assert.Equal(new[] { "minor_quest" }, QuestState.SavedText(c).Keys);
    }

    /// <summary>The fixture test: a real character blob (the #32 fixture's shape, carrying every saved key the
    /// quests use, int and text, each with a distinct value, plus two flat registry text keys holding the empty
    /// string and non-ASCII text) is loaded through the store, every slot is read through QuestState and must see
    /// its own value, every slot is written back through QuestState, and the blob the store would save is
    /// byte-for-byte the one that was loaded.</summary>
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
        var texts = JsonNode.Parse(json)!["QuestStrings"]!.AsObject()
                            .ToDictionary(p => p.Key, p => p.Value!.GetValue<string>(), StringComparer.Ordinal);

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

        var textsAddressed = new HashSet<string>(StringComparer.Ordinal);
        void CheckText(string name, string slot, string saved)
        {
            Assert.True(texts.ContainsKey(saved), $"the fixture has no text key '{saved}' — add it, or fix the catalogue");
            var q = QuestState.Over(character, name);
            Assert.True(string.Equals(texts[saved], q.GetText(slot), StringComparison.Ordinal),
                        $"{name}.{slot} read \"{q.GetText(slot)}\", the blob holds {saved} = \"{texts[saved]}\"");
            q.SetText(slot, q.GetText(slot));
            textsAddressed.Add(saved);
        }
        foreach (var (name, slot, saved) in LegacyTextKeys) CheckText(name, slot, saved);
        foreach (var key in RegistryTextKeys) CheckText(QuestState.Registry, key, key);
        // The two value shapes are really in the blob, so the round trip below covers them as stored.
        Assert.Equal("", texts["fixture_empty"]);
        Assert.Contains(texts["fixture_non_ascii"], ch => ch > '\u007F');

        // Every key in the blob was reached by some name, so the fixture tests nothing it does not cover.
        var unreached = blob.Keys.Where(k => !addressed.Contains(k))
                                 .Concat(texts.Keys.Where(k => !textsAddressed.Contains(k))).ToList();
        Assert.True(unreached.Count == 0, "fixture keys no quest name reaches: " + string.Join(", ", unreached));
        Assert.Equal(json, CharacterStore.Serialize(character));
    }

    /// <summary>
    /// A text value is free text: a MinorQuests.csv key, a character's name. So every text key must round-trip
    /// byte-identical whatever it holds. Each value goes into each text key of the fixture blob in turn; the blob is
    /// the store's own output for it (its encoder escapes non-ASCII, the quote and the backslash), and that blob,
    /// loaded, read through the text slot and written back through it, must save to the same bytes. "" is a value
    /// like any other: the minor quest writes it when a quest ends, and the key stays.
    ///
    /// <para>The second half: a text key the blob does not carry reads "" and the read adds nothing.</para>
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("Ünsal Ølberg")]                     // Latin-1
    [InlineData("무사 武士")]                         // Hangul and Han, outside Latin-1
    [InlineData("say \"hi\" \\ back")]               // a quote and a backslash
    public void EveryTextKeyRoundTripsByteIdenticalWhateverItHolds(string value)
    {
        string fixture = File.ReadAllText(FixturePath()).TrimEnd('\n');
        foreach (var (name, slot, saved) in LegacyTextKeys)
        {
            var seeded = CharacterStore.Deserialize(fixture);
            Assert.True(QuestState.SavedText(seeded).ContainsKey(saved), $"the fixture has no text key '{saved}'");
            QuestState.WriteText(seeded, saved, value);
            string json = CharacterStore.Serialize(seeded);
            var character = CharacterStore.Deserialize(json);
            Assert.Equal(json, CharacterStore.Serialize(character));   // baseline: the store itself round-trips it

            var q = QuestState.Over(character, name);
            string read = q.GetText(slot);
            Assert.True(string.Equals(value, read, StringComparison.Ordinal), $"{name}.{slot} read \"{read}\", saved \"{value}\"");
            q.SetText(slot, read);
            Assert.Equal(json, CharacterStore.Serialize(character));
            Assert.True(QuestState.SavedText(character).ContainsKey(saved), $"writing \"{value}\" dropped '{saved}'");

            // Absent: the key removed from the blob reads "" and stays absent.
            Assert.True(QuestState.RemoveText(character, saved));
            string without = CharacterStore.Serialize(character);
            Assert.Equal("", q.GetText(slot));
            Assert.Equal(without, CharacterStore.Serialize(character));
        }
    }

    /// <summary>
    /// The families resolve keys whose variable part comes from game DATA: each armor chain's tier, steps and
    /// watched mobs, and every mob a MinorQuests.csv row names (every tier, offered or not). The pins above check a
    /// handful of instances; this walks all of them through <see cref="QuestState.Resolve"/>, so a data change the
    /// family patterns reject fails here rather than as a throw in a guildmaster's or trainer's dialog (#310 review
    /// F1: a family's hole matches no dot, so a mob key with a dot in it would throw where the pre-#51 code read 0).
    ///
    /// <para>Each resolved key must also be the key the pre-#51 code built for that slot, at b43a8e5:
    /// <c>$"aq_{chain.Tier}_{stage}_"</c> + "!", "@" or the mob (ArmorQuestAbility.cs:102 and :292), and
    /// <c>"minor_quest_kill_count_"</c> + the mob (MinorQuest.cs:28). The slot names come from the quests' own
    /// private builders and constants, read by reflection, so the walk names exactly what the runners name.</para>
    /// </summary>
    [Fact]
    public void EveryArmorStepAndMinorQuestMobResolvesToItsLegacyKey()
    {
        EnsureContentLoaded();

        var armor = typeof(ArmorQuestAbility);
        var opened     = StaticFunc<int, string>(armor, "Opened");
        var totalKills = StaticFunc<int, string>(armor, "TotalKills");
        var kills      = StaticFunc<int, string, string>(armor, "Kills");
        var watched    = StaticFunc<ArmorStep, IEnumerable<string>>(armor, "Watched");
        string minorName  = PrivateConst(typeof(MinorQuestAbility), "KQuest");
        string minorKills = PrivateConst(typeof(MinorQuestAbility), "KKills");

        var wrong = new List<string>();
        void Expect(string name, string slot, string legacy)
        {
            string got;
            try { got = QuestState.Resolve(name, slot); }
            catch (InvalidOperationException e) { wrong.Add($"{name}.{slot}: THROWS {e.Message}"); return; }
            if (got != legacy) wrong.Add($"{name}.{slot} resolves to '{got}', but the pre-#51 code saved it as '{legacy}'");
        }

        int steps = 0, armorSlots = 0;
        foreach (var chain in ArmorQuest.Chains.Values)
            for (int n = 0; n < chain.Steps.Length; n++, steps++)
            {
                string pfx = $"aq_{chain.Tier}_{n}_";
                Expect(chain.StageKey, opened(n), pfx + "!");
                Expect(chain.StageKey, totalKills(n), pfx + "@");
                armorSlots += 2;
                foreach (var mob in watched(chain.Steps[n]))
                {
                    Expect(chain.StageKey, kills(n, mob), pfx + mob);
                    armorSlots++;
                }
            }

        int minorSlots = 0;
        foreach (var quest in Content.MinorQuests)
            foreach (var mob in quest.Mobs)
            {
                Expect(minorName, minorKills + mob, "minor_quest_kill_count_" + mob);
                minorSlots++;
            }

        _out.WriteLine($"walked {ArmorQuest.Chains.Count} armor chains ({steps} steps, {armorSlots} slots) and " +
                       $"{Content.MinorQuests.Count} MinorQuests.csv rows ({minorSlots} mob slots); {wrong.Count} wrong");
        Assert.True(wrong.Count == 0, $"{wrong.Count} data-driven slot(s) do not resolve to their saved key:\n" +
                                      string.Join("\n", wrong));

        // The walk has to be walking something: all twelve chains (four paths, three tiers), and a loaded
        // MinorQuests.csv in which every row names at least one mob.
        Assert.Equal(12, ArmorQuest.Chains.Count);
        Assert.NotEmpty(Content.MinorQuests);
        Assert.True(minorSlots >= Content.MinorQuests.Count, "a MinorQuests.csv row names no mob");
    }

    /// <summary>The acceptance lines "no raw Quests[...] access outside QuestState" (#51) and the same for the string
    /// registry (#309), as one fact. Scans every production project's source (comment lines stripped) for either
    /// member name and fails on any use outside Server/QuestState.cs and the two fields' own declarations. Every
    /// allowlisted entry must still be found, so the list cannot go stale.</summary>
    [Fact]
    public void NothingButQuestStateTouchesTheQuestMap()
    {
        string root = RepoPaths.Root();
        string[] projects = { "Server", "Shared", "Shared.Core", "LoginServer", "LoginProbe", "Protocol.Tk495", "Tools", "MapEditor", "IconStudio" };
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Server/QuestState.cs",
            // The fields themselves.
            "Shared/Character.cs: public Dictionary<string, int> Quests = new();",
            "Shared/Character.cs: public Dictionary<string, string> QuestStrings = new();",
        };
        var member = new Regex(@"\bQuests\b|\bQuestStrings\b", RegexOptions.CultureInvariant);

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

        Assert.True(hits.Count == 0, "Character.Quests or Character.QuestStrings touched outside QuestState:\n" + string.Join("\n", hits));
        Assert.Equal(allowed.OrderBy(a => a, StringComparer.Ordinal), seenAllowed.OrderBy(a => a, StringComparer.Ordinal));
    }

    // ---- plumbing --------------------------------------------------------------------------------------

    private static readonly object ContentGate = new();
    private static bool _contentLoaded;

    /// <summary>MinorQuests.csv, through the real loader (ArmorQuestTests' pattern).</summary>
    private static void EnsureContentLoaded()
    {
        lock (ContentGate)
        {
            if (_contentLoaded) return;
            TestProcessState.LoadContent();
            _contentLoaded = true;
        }
    }

    private static MethodInfo PrivateStatic(Type type, string name) =>
        type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new Xunit.Sdk.XunitException($"{type.Name}.{name} is gone: the walk names its slots through it");

    private static Func<T, TResult> StaticFunc<T, TResult>(Type type, string name) =>
        PrivateStatic(type, name).CreateDelegate<Func<T, TResult>>();

    private static Func<T1, T2, TResult> StaticFunc<T1, T2, TResult>(Type type, string name) =>
        PrivateStatic(type, name).CreateDelegate<Func<T1, T2, TResult>>();

    private static string PrivateConst(Type type, string name) =>
        type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.GetRawConstantValue() as string
        ?? throw new Xunit.Sdk.XunitException($"{type.Name}.{name} is gone: the walk names its slots through it");
}
