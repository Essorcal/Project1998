using System;
using System.Collections.Generic;
using System.Linq;
using Server;
using Xunit;

namespace Tests;

/// <summary>
/// The twelve Star/Moon/Sun chains are declared as data (<see cref="ArmorQuest.Chains"/>) that names item
/// keys, mob keys, NPC ids and a CSV of gates — every one of them a join nothing else validates. A mistyped
/// mob key does not fail a build; it produces a step that can never be completed, and looks to a player
/// exactly like a quest that is merely hard. These pin the joins, and pin the handful of source decisions
/// that were close calls, so a later edit that quietly flips one shows up as a red test rather than as a
/// silently different quest.
/// </summary>
public class ArmorQuestTests
{
    private static readonly object _gate = new();
    private static bool _loaded;

    private static void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            Content.Load();
            _loaded = true;
        }
    }

    // ---- the joins ------------------------------------------------------------------------------

    /// <summary>Every item a chain asks for, hands back, or awards must exist in the registry. This is the
    /// test that would have caught <c>star_burst</c> — the fourth "Star" item on both period pages, which
    /// has no 4.95 row (it is carpenter-made) and is therefore deliberately absent from the required pool.</summary>
    [Fact]
    public void EveryItemKeyResolves()
    {
        EnsureLoaded();

        var missing = new List<string>();
        foreach (var chain in ArmorQuest.Chains.Values)
        {
            Check(chain.MaleArmor); Check(chain.FemaleArmor);
            foreach (var step in chain.Steps)
            {
                foreach (var (key, _) in step.Items) Check(key);
                foreach (var (key, _) in step.KeepBack) Check(key);
                if (step.Rebond is not null) Check(step.Rebond);
            }
        }
        Assert.True(missing.Count == 0, "unknown item keys: " + string.Join(", ", missing.Distinct()));

        void Check(string key) { if (Content.ItemByKey(key) is null) missing.Add(key); }
    }

    /// <summary>Every mob a chain counts (or forbids) must exist. A stale key here is invisible: its kill
    /// count simply never rises.</summary>
    [Fact]
    public void EveryMobKeyResolves()
    {
        EnsureLoaded();

        var known = Content.Mobs.Select(m => m.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = ArmorQuest.Chains.Values
            .SelectMany(c => c.Steps)
            .SelectMany(s => s.Kills.SelectMany(g => g.Select(r => r.Mob)).Concat(s.Forbid))
            .Where(m => !known.Contains(m))
            .Distinct().ToList();

        Assert.True(missing.Count == 0, "unknown mob keys: " + string.Join(", ", missing));
    }

    /// <summary>The eight guildmasters must exist, be the identifier the ability is composed onto, sit in
    /// Kugnae or Buya, and carry the ability. The chains are SPOKEN, so nothing visible breaks if the
    /// composition row is dropped — "star" just goes unanswered.</summary>
    [Fact]
    public void GuildMastersAnswerAndMatchTheirPath()
    {
        EnsureLoaded();

        var expectedKey = new Dictionary<int, string>
        {
            [1] = "WarriorTrainerNpc", [2] = "RogueTrainerNpc",
            [3] = "MageTrainerNpc",    [4] = "PoetTrainerNpc",
        };

        foreach (var (npcId, path) in ArmorQuest.GuildMasters)
        {
            var npc = Content.Npcs.FirstOrDefault(n => n.Id == npcId);
            Assert.True(npc is not null, $"NPC {npcId} is missing");
            Assert.Equal(expectedKey[path], npc!.Key);

            // Kugnae guild rooms are maps 12-18; Buya's sanctums are 366-369.
            Assert.True((npc.Map >= 12 && npc.Map <= 18) || (npc.Map >= 366 && npc.Map <= 369),
                        $"{npc.Name} ({npcId}) is on map {npc.Map}, which is neither capital's guild");

            Assert.Contains(NpcScripts.For(npc), a => a is ArmorQuestAbility);
        }
    }

    /// <summary>The narrowing is the point: the ability rides on the four trainer IDENTIFIERS, which
    /// eighteen NPCs share, and only the eight capital guildmasters are supposed to run the chain. If the
    /// map ever grows another trainer, this catches it being silently enrolled.</summary>
    [Fact]
    public void OnlyEightNpcsRunTheChains()
    {
        EnsureLoaded();
        Assert.Equal(8, ArmorQuest.GuildMasters.Count);
        Assert.Equal(4, ArmorQuest.GuildMasters.Values.Distinct().Count());
        foreach (var path in new[] { 1, 2, 3, 4 })
            Assert.Equal(2, ArmorQuest.GuildMasters.Count(kv => kv.Value == path));
    }

    /// <summary>Twelve chains, each with exactly one closing step, and every closing step must know which
    /// garment the previous tier left behind.</summary>
    [Fact]
    public void EveryChainIsWellFormed()
    {
        EnsureLoaded();

        foreach (var path in new[] { 1, 2, 3, 4 })
            foreach (var tier in new[] { "star", "moon", "sun" })
            {
                Assert.True(ArmorQuest.Chains.ContainsKey((path, tier)), $"missing chain {path}/{tier}");
                var chain = ArmorQuest.Chains[(path, tier)];

                Assert.Equal(1, chain.Steps.Count(s => s.IsFinal));
                Assert.True(chain.Steps[^1].IsFinal, $"{path}/{tier}: the closing step must be last");
                Assert.All(chain.Steps, s => Assert.False(string.IsNullOrWhiteSpace(s.Unmet)));

                // Star has nothing to consume; Moon eats Star and Sun eats Moon, per sex.
                foreach (int sex in new[] { 0, 1 })
                {
                    string? prev = chain.PreviousArmorFor(sex);
                    if (tier == "star") Assert.Null(prev);
                    else Assert.Equal(ArmorQuest.Chains[(path, tier == "moon" ? "star" : "moon")].ArmorFor(sex), prev);
                }
            }
    }

    /// <summary>Both garments of every tier must be flagged bonded, or the quest's whole reward — a piece
    /// only you can wear — silently becomes ordinary tradeable armor. (star_blouse was missing from that
    /// list until this chain was built.)</summary>
    [Fact]
    public void EveryAwardedGarmentIsBonded()
    {
        EnsureLoaded();

        foreach (var chain in ArmorQuest.Chains.Values)
            foreach (var key in new[] { chain.MaleArmor, chain.FemaleArmor })
            {
                var def = Content.ItemByKey(key);
                Assert.True(def is not null, $"{key} is missing from the item registry");
                Assert.True(def!.Bonded, $"{key} is awarded by an armor quest but is not flagged bonded");
            }
    }

    /// <summary>Rogue Moon rebonds the White Moon Axe rather than eating it, which only works if the axe is
    /// a bonded row — <see cref="Session.GivePlaced"/> stamps the owner off <see cref="ItemDef.Bonded"/>.</summary>
    [Fact]
    public void TheWhiteMoonAxeIsBondable()
    {
        EnsureLoaded();

        var step = ArmorQuest.Chains[(2, "moon")].Steps.Single(s => s.Rebond is not null);
        Assert.Equal("white_moon_axe", step.Rebond);
        Assert.True(Content.ItemByKey("white_moon_axe")!.Bonded);
    }

    // ---- the gates ------------------------------------------------------------------------------

    /// <summary>The CSV must cover all twelve, name karma tiers the ladder actually knows, and agree with
    /// the compiled fallback — a typo'd tier name is silently false in <see cref="Karma.Meets"/>, which
    /// would wall the chain off from everybody.</summary>
    [Fact]
    public void GateRowsCoverEveryChainAndNameRealTiers()
    {
        EnsureLoaded();

        foreach (var path in new[] { 1, 2, 3, 4 })
            foreach (var tier in new[] { "star", "moon", "sun" })
            {
                Assert.True(Content.ArmorQuestGates.ContainsKey((path, tier)), $"ArmorQuests.csv has no {path}/{tier} row");
                var (level, karma) = ArmorQuest.GateFor(path, tier);

                Assert.Equal(tier switch { "star" => 66, "moon" => 76, _ => 86 }, level);
                Assert.Contains(karma, Karma.TierNames);
                // A tier name the ladder knows but that nobody could ever satisfy would be just as bad.
                Assert.True(Karma.Meets(50, karma), $"{path}/{tier}: karma tier '{karma}' is unreachable");
            }
    }

    /// <summary>The karma split as shipped, pinned because it is the one place the period sources fight.
    /// Star is unanimous. Moon is Dog for the two paths Atlas says Dog for and Ox for the other two — the
    /// split that Atlas and RTK reached independently, against a tswolf line that is copy-pasted across all
    /// four of its pages. Sun is Tiger for Warrior and Mage, Bear for Rogue and Poet; Rogue's is the
    /// genuinely unresolved one (Atlas Bear, tutor board Tiger) and Bear ships because it is the
    /// recoverable direction. Flip the CSV row, and flip this, together.</summary>
    [Fact]
    public void TheShippedKarmaSplitIsTheDocumentedOne()
    {
        EnsureLoaded();

        Assert.All(new[] { 1, 2, 3, 4 }, p => Assert.Equal("Rabbit", ArmorQuest.GateFor(p, "star").Karma));

        Assert.Equal("Dog", ArmorQuest.GateFor(1, "moon").Karma);   // Warrior
        Assert.Equal("Dog", ArmorQuest.GateFor(2, "moon").Karma);   // Rogue
        Assert.Equal("Ox",  ArmorQuest.GateFor(3, "moon").Karma);   // Mage
        Assert.Equal("Ox",  ArmorQuest.GateFor(4, "moon").Karma);   // Poet

        Assert.Equal("Tiger", ArmorQuest.GateFor(1, "sun").Karma);  // Warrior
        Assert.Equal("Bear",  ArmorQuest.GateFor(2, "sun").Karma);  // Rogue   <- the unresolved one
        Assert.Equal("Tiger", ArmorQuest.GateFor(3, "sun").Karma);  // Mage
        Assert.Equal("Bear",  ArmorQuest.GateFor(4, "sun").Karma);  // Poet
    }

    // ---- the step shapes that were source decisions ----------------------------------------------

    /// <summary>The four numbers where tswolf disagrees with Atlas + the tutor boards, pinned to the value
    /// that ships. See docs/common/Armor-Quests.md.</summary>
    [Fact]
    public void ContestedCountsShipTheCorroboratedValue()
    {
        EnsureLoaded();

        var poetStar = ArmorQuest.Chains[(4, "star")];
        Assert.Equal(9, poetStar.Steps[0].Kills.Single().Single(r => r.Mob == "nine_tailed_fox").Count);

        var poetMoon = ArmorQuest.Chains[(4, "moon")];
        Assert.Contains(poetMoon.Steps, s => s.Items.Any(i => i.Key == "rose" && i.Count == 50));
    }

    /// <summary>Poet Moon's asks, in order: marriage first, then the roses. Atlas prints roses first but
    /// then cites them as "[Step 2]" in its own sacrifice list; tswolf and the tutor guide both have
    /// marriage first, so two internally-consistent accounts beat one that contradicts itself.</summary>
    [Fact]
    public void PoetMoonAsksForCommitmentBeforeRoses()
    {
        EnsureLoaded();

        var steps = ArmorQuest.Chains[(4, "moon")].Steps;
        Assert.NotNull(steps[0].Extra);                                       // married / blood-bonded
        Assert.Contains(steps[1].Items, i => i.Key == "rose");
        Assert.NotNull(steps[2].Extra);                                       // mentored 3
        Assert.True(steps[3].IsFinal);
    }

    /// <summary>The "and NOTHING else" steps use the strict whole-world check; the "nothing else IN THAT
    /// CAVE" steps use the forbidden-list check. The distinction is the sources' own wording and it is the
    /// difference between a fair step and an impossible one.</summary>
    [Fact]
    public void StrictKillStepsUseTheRuleTheirSourceStates()
    {
        EnsureLoaded();

        // Warrior Sun and Mage Sun: "kill 200 Rabbits and nothing else."
        foreach (var (path, tier) in new[] { (1, "sun"), (3, "sun") })
        {
            var rabbits = ArmorQuest.Chains[(path, tier)].Steps
                .Single(s => s.Kills.Any(g => g.Any(r => r.Mob == "rabbit")));
            Assert.True(rabbits.Pure, $"{path}/{tier}: the 200-rabbit step must be exclusive");
            Assert.Empty(rabbits.Forbid);
        }

        // Rogue Sun: "WITHOUT killing any other creatures in that cave" — scoped, so a forbid list.
        var rogueSun = ArmorQuest.Chains[(2, "sun")].Steps;
        var bossSteps = rogueSun.Where(s => s.Forbid.Length > 0).ToList();
        Assert.Equal(2, bossSteps.Count);                                     // the rats, then the rabbits
        Assert.All(bossSteps, s => Assert.False(s.Pure));
        Assert.Contains(bossSteps, s => s.Forbid.Contains("hop"));            // the Thump lookalike
    }

    /// <summary>A forbidden mob must never also be a required one, or the step forbids what it demands.</summary>
    [Fact]
    public void NoStepForbidsWhatItRequires()
    {
        EnsureLoaded();

        foreach (var chain in ArmorQuest.Chains.Values)
            foreach (var step in chain.Steps)
            {
                var required = step.Kills.SelectMany(g => g.Select(r => r.Mob)).ToHashSet();
                var clash = step.Forbid.Where(required.Contains).ToList();
                Assert.True(clash.Count == 0,
                            $"{chain.Path}/{chain.Tier}: step both requires and forbids {string.Join(", ", clash)}");
            }
    }

    /// <summary>Anything held back must actually be asked for, and never more than was asked for — a
    /// KeepBack larger than its Items count would let the guildmaster take goods the player never owed.</summary>
    [Fact]
    public void KeepBackNeverExceedsWhatWasAsked()
    {
        EnsureLoaded();

        foreach (var chain in ArmorQuest.Chains.Values)
            foreach (var step in chain.Steps)
                foreach (var (key, max) in step.KeepBack)
                {
                    var asked = step.Items.FirstOrDefault(i => i.Key == key);
                    Assert.True(asked.Key is not null, $"{chain.Path}/{chain.Tier}: keeps back {key}, never asks for it");
                    Assert.InRange(max, 1, asked.Count);
                }
    }

    /// <summary>Mage Sun's "three items with Star in the name": the pool is the four the pages name, and
    /// exactly three of them exist here, so the step is satisfiable but has no slack. If a Star burst row is
    /// ever added the step becomes a genuine choice — this test is what will notice.</summary>
    [Fact]
    public void ThreeStarItemsIsSatisfiable()
    {
        EnsureLoaded();

        var step = ArmorQuest.Chains[(3, "sun")].Steps.Single(s => s.AnyItems is not null);
        var (pool, count) = step.AnyItems!.Value;

        Assert.Equal(3, count);
        Assert.Contains("star_burst", pool);                                  // named on both pages…
        Assert.Null(Content.ItemByKey("star_burst"));                         // …but carpenter-made, no 4.95 row
        Assert.Equal(3, pool.Count(k => Content.ItemByKey(k) is not null));
    }

    // ---- the pieces the chains lean on -----------------------------------------------------------

    /// <summary>Poet Sun's totem step depends on the four shrines carrying the worship ability, and on its
    /// order being the one the guildmaster names: Chung ryong, Baekho, Ju Jak, Hyun moo.</summary>
    [Fact]
    public void TotemShrinesTakeWorshipInTheOrderPoetSunWants()
    {
        EnsureLoaded();

        Assert.Equal(new[] { 3, 1, 0, 2 }, TotemWorship.PoetSunOrder);
        Assert.Equal(new[] { "Chung Ryong", "Baekho", "Ju Jak", "Hyun Moo" },
                     TotemWorship.PoetSunOrder.Select(Content.TotemName).ToArray());

        foreach (var (npcId, totem) in TotemWorship.Shrines)
        {
            var npc = Content.Npcs.FirstOrDefault(n => n.Id == npcId);
            Assert.True(npc is not null, $"shrine NPC {npcId} is missing");
            Assert.Contains(NpcScripts.For(npc!), a => a is TotemWorshipAbility);
            Assert.True(Content.ItemByKey(TotemWorship.KeyFor(totem)) is not null,
                        $"{Content.TotemName(totem)} has no key item");
            Assert.Contains(Content.TotemName(totem).Split(' ')[0], npc!.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Blessed by the Stars is the gate on the whole line. Its altar must be inside the Nexus, its
    /// offering must exist, and the rite must not accept a tile outside the middle circle.</summary>
    [Fact]
    public void TheStarAltarIsWhereItShouldBe()
    {
        EnsureLoaded();

        Assert.True(Content.TryMap(BlessedByTheStars.NexusMap, out var nexus));
        Assert.Equal("Mythic Nexus", nexus!.Name);
        Assert.True(Content.ItemByKey(BlessedByTheStars.Offering) is not null);

        Assert.True(BlessedByTheStars.AtAltar(41, 30, 32));                   // dead centre of the 60x60 map
        Assert.False(BlessedByTheStars.AtAltar(41, 27, 32));                  // one west of the circle
        Assert.False(BlessedByTheStars.AtAltar(41, 30, 29));                  // one north of it
        Assert.False(BlessedByTheStars.AtAltar(36, 30, 32));                  // right tile, wrong map
    }

    /// <summary>Poet Moon counts what the Mentor spell records, and Warrior Sun counts what @carnage does.
    /// Both are cross-file string keys, so a rename on one side is silent.</summary>
    [Fact]
    public void CrossSystemCountersAgreeOnTheirKeys()
    {
        EnsureLoaded();

        Assert.Equal(ArmorQuest.MentoredReg, Mentorship.MentoredReg);
        Assert.True(Content.SpellByKey("mentor") is not null, "the Mentor ability is missing from Spells.csv");

        // The three manufacturing gates Poet Sun accepts are the three the pages name, at RTK's Adept rank.
        Assert.Equal(new[] { "Tailoring", "Smithing", "Carpentry" },
                     ArmorQuest.ManufactureSkills.Select(s => s.Skill).ToArray());
        Assert.All(ArmorQuest.ManufactureSkills, s => Assert.True(s.Adept > 0));
    }
}
