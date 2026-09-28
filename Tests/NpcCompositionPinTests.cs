using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// What the narrowed NPC abilities look like to a player, pinned for #50: the change that moved "which of the
/// NPCs sharing an identifier does this ability answer for" out of the abilities' own id/map tables and into
/// <c>game-data/NpcAbilities.csv</c>.
///
/// <para>Written and green on upstream/master e3054cb BEFORE anything moved. The pins are of three kinds, and
/// they are deliberately not equally stable:</para>
/// <list type="bullet">
/// <item><b>Menus, answers and the tutorial givers</b> (<see cref="MenuIsPinned"/>,
/// <see cref="AnswererIsPinned"/>, <see cref="TutorialGiverOffersTheTutorialConversationFirst"/>) are what a
/// player sees. Moving the narrowing must not change one expected value here; a change to any of them is a
/// player-visible change, and needs a decision, not a test edit.</item>
/// <item><b>The ability lists</b> (<see cref="ForIsPinned"/>) are the mechanism. Where an NPC is outside an
/// ability's set, the ability used to be composed onto it and refuse it from the inside; the CSV now leaves it
/// off. So those rows lose exactly the narrowed ability and nothing else, and the history of this file is the
/// record of which.</item>
/// </list>
///
/// <para>The harness mirrors the two dispatchers without going through them: the click menu is every
/// ability's <see cref="INpcAbility.Entries"/> in <see cref="NpcScripts.For"/> order
/// (<c>Session.OpenNpcDialog</c>), and a spoken word goes to each <see cref="INpcSayHandler"/> in the same
/// order until one takes it (<c>Session.RunNpcSayAsync</c>). A handler "takes" a word when its task is still
/// running (a dialog is open, waiting on the player) or has finished with <c>true</c>.</para>
/// </summary>
[Collection("world")]
public class NpcCompositionPinTests
{
    private readonly SessionFixture _fx;
    private static int _serial;

    public NpcCompositionPinTests(SessionFixture fx) => _fx = fx;

    // ---- the harness -------------------------------------------------------------------------------

    /// <summary>A player shape, by name, so the pins can be InlineData. The level-99 path profiles clear
    /// every level gate on the narrowed quests; fpN sits on the Forgotten Past chain at stage N.</summary>
    private static Action<Character> Profile(string name) => name switch
    {
        "peasant" => c => { c.Level = 1; c.ClassName = "Peasant"; },
        "warrior" => c => { c.Level = 99; c.ClassName = "Warrior"; },
        "rogue"   => c => { c.Level = 99; c.ClassName = "Rogue"; },
        "mage"    => c => { c.Level = 99; c.ClassName = "Mage"; },
        "poet"    => c => { c.Level = 99; c.ClassName = "Poet"; },
        _ when name.StartsWith("fp") => c =>
        {
            c.Level = 99; c.ClassName = "Mage";
            c.Quests[ForgottenPastQuest.StageReg] = int.Parse(name[2..]);
        },
        _ => throw new ArgumentException($"no profile '{name}'"),
    };

    /// <summary>A fresh socket-free player of <paramref name="profile"/>, talking to NPC <paramref name="npcId"/>.
    /// Never entered into a map: nothing here needs the player placed, only a session to record what the NPC
    /// sends.</summary>
    private (NpcDef Def, NpcContext Ctx, RecordingOutbound Out) Talk(int npcId, string profile)
    {
        var def = Content.NpcById(npcId);
        Assert.True(def is not null, $"NPC {npcId} is not loaded");
        var mob = _fx.World.NpcsNear(def!.Map, def.X, def.Y, 0).FirstOrDefault(m => m.NpcDefId == npcId);
        Assert.True(mob is not null, $"NPC {npcId} ({def.Name}) is not placed in the world");

        int n = Interlocked.Increment(ref _serial);
        var character = new Character
        {
            SchemaVersion = Character.CurrentSchemaVersion,
            Id = _fx.World.AllocatePlayerId(),
            Name = $"npcpin{n}",
            Map = SessionFixture.HomeMap, X = 5, Y = 10,
        };
        Profile(profile)(character);
        var outbound = new RecordingOutbound($"recorder:npcpin{n}");
        var session = new Session(outbound, 2005, _fx.Store, _fx.World, character);
        return (def, new NpcContext(session, mob!, def), outbound);
    }

    /// <summary>A readable name for an ability: its type, except the four class trainers, which are four
    /// instances of one type.</summary>
    private static string NameOf(INpcAbility a) =>
        ReferenceEquals(a, ClassTrainerAbility.Warrior) ? "warrior_trainer"
      : ReferenceEquals(a, ClassTrainerAbility.Rogue)   ? "rogue_trainer"
      : ReferenceEquals(a, ClassTrainerAbility.Mage)    ? "mage_trainer"
      : ReferenceEquals(a, ClassTrainerAbility.Poet)    ? "poet_trainer"
      : a.GetType().Name;

    private static string Menu(NpcDef def, NpcContext ctx) =>
        string.Join(" / ", NpcScripts.For(def).SelectMany(a => a.Entries(ctx)).Select(e => e.label));

    /// <summary>The first say-handler, in dispatch order, that takes <paramref name="word"/>; "none" if every
    /// one lets it fall through to ordinary chat.</summary>
    private static async Task<string> Answerer(NpcDef def, NpcContext ctx, string word)
    {
        foreach (var h in NpcScripts.For(def).OfType<INpcSayHandler>())
        {
            var t = h.OnSay(ctx, word);
            if (!t.IsCompleted || await t) return NameOf((INpcAbility)h);
        }
        return "none";
    }

    // ---- the ability lists (the mechanism) ---------------------------------------------------------

    [Theory]
    // The tutorial givers: the quest entry first.
    [InlineData(20,  "QuestAbility|StarHintAbility|InfoAbility")]
    [InlineData(49,  "QuestAbility|StarHintAbility|InfoAbility")]
    // MageTrainerNpc: Sute (Eldritch), Mage Stone (Wand), the armor chains (Haedu, Eldritch).
    [InlineData(39,  "mage_trainer|MinorQuestAbility|SuteQuestAbility|ArmorQuestAbility|InfoAbility")]
    [InlineData(35,  "mage_trainer|MinorQuestAbility|ArmorQuestAbility|InfoAbility")]
    [InlineData(133, "mage_trainer|MinorQuestAbility|MageStoneAbility|InfoAbility")]
    [InlineData(165, "mage_trainer|MinorQuestAbility|InfoAbility")]
    [InlineData(168, "mage_trainer|MinorQuestAbility|InfoAbility")]
    // WarriorTrainerNpc: the armor chains (Tabaek, Yabaek), Nagnang shield (Sword).
    [InlineData(36,  "warrior_trainer|MinorQuestAbility|ArmorQuestAbility|InfoAbility")]
    [InlineData(41,  "warrior_trainer|MinorQuestAbility|ArmorQuestAbility|InfoAbility")]
    [InlineData(91,  "warrior_trainer|MinorQuestAbility|NagnangShieldAbility|InfoAbility")]
    [InlineData(153, "warrior_trainer|MinorQuestAbility|InfoAbility")]
    // RogueTrainerNpc: White Moon Axe (the four Masos), the armor chains (Maro, Maso). Dagger uniform (#302
    // F3) is now narrowed in the CSV to Maro, Maso and Dagger himself (37, 42, 138); the other rogue trainers
    // below lose it from this list with nothing else changing, since their own id gate inside
    // DaggerUniformAbility already refused it (see NpcAbilityNarrowingTests.DaggerUniformNamesExactlyThe...).
    [InlineData(37,  "rogue_trainer|MinorQuestAbility|ArmorQuestAbility|DaggerUniformAbility|InfoAbility")]
    [InlineData(42,  "rogue_trainer|MinorQuestAbility|WhiteMoonAxeAbility|ArmorQuestAbility|DaggerUniformAbility|InfoAbility")]
    [InlineData(138, "rogue_trainer|MinorQuestAbility|DaggerUniformAbility|InfoAbility")]
    [InlineData(159, "rogue_trainer|MinorQuestAbility|InfoAbility")]
    [InlineData(162, "rogue_trainer|MinorQuestAbility|WhiteMoonAxeAbility|InfoAbility")]
    [InlineData(163, "rogue_trainer|MinorQuestAbility|WhiteMoonAxeAbility|InfoAbility")]
    [InlineData(164, "rogue_trainer|MinorQuestAbility|WhiteMoonAxeAbility|InfoAbility")]
    // PoetTrainerNpc: the armor chains (Jinsun, Song), Poet's whip (the four Staffs).
    [InlineData(38,  "poet_trainer|MinorQuestAbility|ArmorQuestAbility|InfoAbility")]
    [InlineData(40,  "poet_trainer|MinorQuestAbility|ArmorQuestAbility|InfoAbility")]
    [InlineData(137, "poet_trainer|MinorQuestAbility|PoetWhipQuestAbility|InfoAbility")]
    [InlineData(408, "poet_trainer|MinorQuestAbility|PoetWhipQuestAbility|InfoAbility")]
    [InlineData(409, "poet_trainer|MinorQuestAbility|PoetWhipQuestAbility|InfoAbility")]
    [InlineData(410, "poet_trainer|MinorQuestAbility|PoetWhipQuestAbility|InfoAbility")]
    [InlineData(171, "poet_trainer|MinorQuestAbility|InfoAbility")]
    // SmithNpc: the tall shield (Chul), Forgotten Past (Gruff, Thane).
    [InlineData(108, "ShopAbility|RepairAbility|NagnangTallShieldAbility|InfoAbility")]
    [InlineData(142, "ShopAbility|RepairAbility|ForgottenPastAbility|InfoAbility")]
    [InlineData(196, "ShopAbility|RepairAbility|ForgottenPastAbility|InfoAbility")]
    [InlineData(21,  "ShopAbility|RepairAbility|InfoAbility")]
    // ShamanNpc: Forgotten Past (Storm). RotahNpc is Rotah alone.
    [InlineData(64,  "ReviveAbility|ForgottenPastAbility|InfoAbility")]
    [InlineData(65,  "ReviveAbility|InfoAbility")]
    [InlineData(195, "ShopAbility|RepairAbility|BankAbility|ForgottenPastAbility|InfoAbility")]
    // The four shrine animals, each its own identifier; a totem priest is not a shrine.
    [InlineData(388, "ReviveAbility|TotemWorshipAbility|InfoAbility")]
    [InlineData(389, "ReviveAbility|TotemWorshipAbility|InfoAbility")]
    [InlineData(390, "ReviveAbility|TotemWorshipAbility|InfoAbility")]
    [InlineData(391, "ReviveAbility|TotemWorshipAbility|InfoAbility")]
    [InlineData(94,  "InfoAbility")]
    // Mythic animals (all twelve share the identifier and all twelve have a row), the shrine shamans, and
    // the summit, which is neither.
    [InlineData(120, "MythicAllianceAbility|InfoAbility")]
    [InlineData(125, "MythicAllianceAbility|InfoAbility")]
    [InlineData(131, "MythicAllianceAbility|InfoAbility")]
    [InlineData(188, "AlignmentAbility|InfoAbility")]
    [InlineData(189, "AlignmentAbility|InfoAbility")]
    [InlineData(190, "AlignmentAbility|InfoAbility")]
    [InlineData(187, "SummitAbility|InfoAbility")]
    public void ForIsPinned(int npcId, string expected)
    {
        var def = Content.NpcById(npcId);
        Assert.True(def is not null, $"NPC {npcId} is not loaded");
        Assert.Equal(expected, string.Join("|", NpcScripts.For(def!).Select(NameOf)));
    }

    // ---- what a player sees: the click menu ---------------------------------------------------------

    [Theory]
    [InlineData(133, "mage",    "Learn Secret / Divine Secret / Forget Secret / Become Noble / Mage Stone")]
    [InlineData(35,  "mage",    "Learn Secret / Divine Secret / Forget Secret / Become Noble")]
    [InlineData(165, "mage",    "Learn Secret / Divine Secret / Forget Secret / Become Noble")]
    [InlineData(91,  "warrior", "Learn Secret / Divine Secret / Forget Secret / Become Noble / Strangers / Shield")]
    [InlineData(36,  "warrior", "Learn Secret / Divine Secret / Forget Secret / Become Noble")]
    [InlineData(137, "poet",    "Learn Secret / Divine Secret / Forget Secret / Become Noble / Welcome Stranger")]
    [InlineData(408, "poet",    "Learn Secret / Divine Secret / Forget Secret / Become Noble / Welcome Stranger")]
    [InlineData(40,  "poet",    "Learn Secret / Divine Secret / Forget Secret / Become Noble")]
    [InlineData(171, "poet",    "Learn Secret / Divine Secret / Forget Secret / Become Noble")]
    [InlineData(388, "peasant", "Worship Baekho")]
    [InlineData(389, "peasant", "Worship Chung Ryong")]
    [InlineData(390, "peasant", "Worship Hyun Moo")]
    [InlineData(391, "peasant", "Worship Ju Jak")]
    [InlineData(94,  "peasant", "")]
    [InlineData(188, "peasant", "Speak")]
    [InlineData(189, "peasant", "Speak")]
    [InlineData(190, "peasant", "Speak")]
    [InlineData(187, "peasant", "")]
    [InlineData(195, "peasant", "Fix Item / Deposit Money / Deposit Item / Withdraw Item / Forgotten past")]
    [InlineData(20,  "peasant", "Continue my training")]
    [InlineData(49,  "peasant", "Continue my training")]
    public void MenuIsPinned(int npcId, string profile, string expected)
    {
        var (def, ctx, _) = Talk(npcId, profile);
        Assert.Equal(expected, Menu(def, ctx));
    }

    // ---- what a player sees: who answers a spoken word ----------------------------------------------

    [Theory]
    // "sute" is Eldritch's alone.
    [InlineData(39,  "mage",    "sute", "SuteQuestAbility")]
    [InlineData(35,  "mage",    "sute", "none")]
    [InlineData(133, "mage",    "sute", "none")]
    [InlineData(165, "mage",    "sute", "none")]
    // The armor chains: the eight capital guildmasters, each to their own path.
    [InlineData(39,  "mage",    "star", "ArmorQuestAbility")]
    [InlineData(35,  "mage",    "moon", "ArmorQuestAbility")]
    [InlineData(133, "mage",    "star", "none")]
    [InlineData(168, "mage",    "sun",  "none")]
    [InlineData(36,  "warrior", "star", "ArmorQuestAbility")]
    [InlineData(41,  "warrior", "sun",  "ArmorQuestAbility")]
    [InlineData(91,  "warrior", "star", "none")]
    [InlineData(153, "warrior", "moon", "none")]
    [InlineData(38,  "poet",    "star", "ArmorQuestAbility")]
    [InlineData(40,  "poet",    "sun",  "ArmorQuestAbility")]
    [InlineData(137, "poet",    "star", "none")]
    [InlineData(37,  "rogue",   "moon", "ArmorQuestAbility")]
    [InlineData(42,  "rogue",   "star", "ArmorQuestAbility")]
    // "moon" at a Maso is the White Moon Axe first, and at every Maso, aligned or not.
    [InlineData(42,  "rogue",   "moon", "WhiteMoonAxeAbility")]
    [InlineData(162, "rogue",   "moon", "WhiteMoonAxeAbility")]
    [InlineData(163, "rogue",   "moon", "WhiteMoonAxeAbility")]
    [InlineData(164, "rogue",   "moon", "WhiteMoonAxeAbility")]
    [InlineData(159, "rogue",   "moon", "none")]
    [InlineData(138, "rogue",   "moon", "none")]
    // Chul's tall shield.
    [InlineData(108, "warrior", "shield", "NagnangTallShieldAbility")]
    [InlineData(21,  "warrior", "shield", "none")]
    // A mythic answers its enemy's name, and only that.
    [InlineData(125, "peasant", "horse", "MythicAllianceAbility")]
    [InlineData(124, "peasant", "rat",   "MythicAllianceAbility")]
    [InlineData(125, "peasant", "rat",   "none")]
    // The Forgotten Past chain, one step at each of its four NPCs, and the same word at a stranger.
    [InlineData(195, "fp1", "sweet summer blossoms", "ForgottenPastAbility")]
    [InlineData(64,  "fp3", "wilderness life",       "ForgottenPastAbility")]
    [InlineData(65,  "fp3", "wilderness life",       "none")]
    [InlineData(142, "fp6", "forge metal",           "ForgottenPastAbility")]
    [InlineData(21,  "fp6", "forge metal",           "none")]
    [InlineData(196, "fp7", "special metal",         "ForgottenPastAbility")]
    [InlineData(108, "fp7", "special metal",         "none")]
    public async Task AnswererIsPinned(int npcId, string profile, string word, string expected)
    {
        var (def, ctx, _) = Talk(npcId, profile);
        Assert.Equal(expected, await Answerer(def, ctx, word));
    }

    // ---- which NPCs offer which quest: the tutorial givers ----------------------------------------------

    /// <summary>Ironheart and Jadespear give the tutorial chain, and nobody else does. For both, the quest's
    /// entry is the FIRST ability and the only menu entry — which is what makes a click dive straight into the
    /// conversation instead of showing a one-item picker — and picking it plays exactly what
    /// <see cref="TutorialQuest"/>'s own conversation plays for the same player.</summary>
    [Theory]
    [InlineData(20)]
    [InlineData(49)]
    public void TutorialGiverOffersTheTutorialConversationFirst(int npcId)
    {
        var (def, ctx, viaMenu) = Talk(npcId, "peasant");
        var abilities = NpcScripts.For(def);
        Assert.IsType<QuestAbility>(abilities[0]);

        var entries = abilities.SelectMany(a => a.Entries(ctx)).ToList();
        Assert.Equal(new[] { "Continue my training" }, entries.Select(e => e.label));

        // Both conversations stop on their first page, waiting for a click that never comes; what they sent
        // up to that point is the comparison.
        _ = entries[0].run(ctx);
        var (_, directCtx, direct) = Talk(npcId, "peasant");
        _ = TutorialQuest.Def.Talk(directCtx);

        Assert.NotEmpty(direct.Frames);
        Assert.Equal(direct.Frames, viaMenu.Frames);
    }

    [Fact]
    public void OnlyTheTwoTutorsGiveTheTutorial()
    {
        var givers = Content.Npcs.Where(n => NpcScripts.For(n).Any(a => a is QuestAbility))
                                 .Select(n => n.Id).OrderBy(id => id).ToArray();
        Assert.Equal(new[] { 20, 49 }, givers);
    }
}
