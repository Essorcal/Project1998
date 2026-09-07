using System;
using System.Linq;
using Server;
using Xunit;

namespace Tests;

/// <summary>
/// The Geomancers' Forgotten Past (see <see cref="ForgottenPastQuest"/>) is a twelve-stage conversation held
/// across four NPCs in four different corners of the world, and every joint in it fails the quiet way. A
/// missing composition row leaves Rotah standing there with nothing to click — which is exactly the state he
/// was in before this quest existed. A renamed item key reads as "you do not have all the required items". A
/// step whose <c>From</c> stage no earlier step writes leaves the chain welded shut in the middle, with the
/// NPC simply not answering. None of that breaks a build or logs a word.
///
/// <para>So these pin three things: that the four NPCs are placed and wired, that the stage ladder is
/// unbroken and single-valued, and that the one-time gate is a LEGEND rather than a stage — because the stage
/// is reset to 0 on completion and a gate built on it would hand out a second orb.</para>
/// </summary>
public class ForgottenPastQuestTests
{
    private static readonly object _gate = new();
    private static bool _loaded;

    private static void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            TestProcessState.LoadContent();
            _loaded = true;
        }
    }

    // ---- the four NPCs -----------------------------------------------------------------------------

    /// <summary>Rotah in particular: he had NO row in NpcAbilities.csv, which is this codebase's signature
    /// silent failure — the NPC loads, renders, and answers a click with nothing at all. Without the row the
    /// whole chain is unreachable and nothing anywhere says so.</summary>
    [Fact]
    public void EveryNpcInTheChainIsPlacedAndCarriesTheAbility()
    {
        EnsureLoaded();

        var expected = new (int Id, string Key, string Name, ushort Map)[]
        {
            (ForgottenPastQuest.RotahNpcId,       "RotahNpc",  "Rotah", 1002),
            (ForgottenPastQuest.StormShamanNpcId, "ShamanNpc", "Storm",  339),
            (ForgottenPastQuest.SanhaeSmithNpcId, "SmithNpc",  "Gruff", 1125),
            (ForgottenPastQuest.ThaneNpcId,       "SmithNpc",  "Thane", 1144),
        };

        foreach (var (id, key, name, map) in expected)
        {
            var npc = Content.Npcs.FirstOrDefault(n => n.Id == id);
            Assert.True(npc is not null, $"NPC {id} ({name}) is not in the world — that step of the chain is dead");
            Assert.Equal(key, npc!.Key);
            Assert.Equal(name, npc.Name);
            Assert.Equal(map, npc.Map);
            Assert.Contains(NpcScripts.For(npc), a => a is ForgottenPastAbility);
            Assert.True(ForgottenPastQuest.AnswersFor(npc), $"{name} carries the ability but is gated out of it");
        }
    }

    /// <summary>Both shared identifiers carry the row, so nineteen smiths and ten shamans get the ability and
    /// only four NPCs may act on it. Without the id gate a player could forge metal at any smith in the
    /// world, in the wrong kingdom, and never walk to Sanhae at all.</summary>
    [Fact]
    public void TheOtherSmithsAndShamansAreGatedOut()
    {
        EnsureLoaded();

        var sharers = Content.Npcs.Where(n => n.Key is "SmithNpc" or "ShamanNpc").ToList();
        Assert.True(sharers.Count > ForgottenPastQuest.Everyone.Length,
                    "the identifiers are no longer shared — the in-code id gate may now be dead weight");

        foreach (var npc in sharers.Where(n => !ForgottenPastQuest.Everyone.Contains(n.Id)))
            Assert.False(ForgottenPastQuest.AnswersFor(npc),
                         $"{npc.Name} (npc {npc.Id}) answers for the orb chain but should not");
    }

    // ---- the stage ladder --------------------------------------------------------------------------

    /// <summary>Every rung advances by exactly one stage, and no two rungs leave from the same place for the
    /// same NPC and word. A duplicate would make <see cref="ForgottenPastQuest.Advance"/> pick whichever came
    /// first in the table and silently strand the other.</summary>
    [Fact]
    public void EveryStepAdvancesExactlyOneStageAndIsUnique()
    {
        foreach (var step in ForgottenPastQuest.Chain)
        {
            Assert.Equal(step.From + 1, step.To);
            Assert.NotEmpty(step.Pages);
            Assert.Equal(step.Speech, step.Speech.Trim().ToLowerInvariant());   // speech arrives lower-cased
        }

        var keys = ForgottenPastQuest.Chain.Select(s => (s.NpcId, s.Speech, s.From)).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    /// <summary>The ladder with no missing rungs. Stages 1-8 and 10 are the spoken steps; 9 is Thane's ore
    /// turn-in and 11/12 are Rotah's examination and forge, all three of which branch and so live in code
    /// rather than the table. A gap anywhere here is a conversation that stops answering mid-quest.</summary>
    [Fact]
    public void TheLadderHasNoMissingRungs()
    {
        for (int stage = ForgottenPastQuest.Asked; stage <= ForgottenPastQuest.OreOwed; stage++)
        {
            int rungs = ForgottenPastQuest.Chain.Count(s => s.From == stage);
            int want = stage == ForgottenPastQuest.OreOwed ? 0 : 1;   // 9 is the ore turn-in, handled in code
            Assert.True(rungs == want, $"stage {stage} has {rungs} spoken steps out of it, expected {want}");
        }

        Assert.Single(ForgottenPastQuest.Chain, s => s.From == ForgottenPastQuest.HasStrangeMetal);
        Assert.DoesNotContain(ForgottenPastQuest.Chain, s => s.From >= ForgottenPastQuest.RotahImpressed);
    }

    /// <summary>A word said out of turn does nothing — the lookup is by stage, so the same sentence advances
    /// the quest at one point in the chain and falls through to ordinary speech everywhere else. "wilderness
    /// life" is the sharp case: it is said to two different NPCs, one stage apart.</summary>
    [Fact]
    public void AWordSaidOutOfTurnDoesNothing()
    {
        // The word is right, the stage is not.
        Assert.Null(ForgottenPastQuest.Advance(ForgottenPastQuest.RotahNpcId, "sweet summer blossoms",
                                               ForgottenPastQuest.NotStarted));
        Assert.Null(ForgottenPastQuest.Advance(ForgottenPastQuest.RotahNpcId, "strange metal",
                                               ForgottenPastQuest.Asked));

        // The stage is right, the LISTENER is not: Rotah answers "wilderness life" at stage 2, the Storm
        // Shaman at stage 3, and neither takes the other's turn.
        Assert.NotNull(ForgottenPastQuest.Advance(ForgottenPastQuest.RotahNpcId, "wilderness life",
                                                  ForgottenPastQuest.Blossoms));
        Assert.Null(ForgottenPastQuest.Advance(ForgottenPastQuest.StormShamanNpcId, "wilderness life",
                                               ForgottenPastQuest.Blossoms));
        Assert.NotNull(ForgottenPastQuest.Advance(ForgottenPastQuest.StormShamanNpcId, "wilderness life",
                                                  ForgottenPastQuest.SentToBuya));
        Assert.Null(ForgottenPastQuest.Advance(ForgottenPastQuest.RotahNpcId, "wilderness life",
                                               ForgottenPastQuest.SentToBuya));
    }

    /// <summary>The one-time gate. Completion resets the stage to 0 (RTK does the same), so if any spoken
    /// step could fire from stage 0 a finished character would simply walk the chain again — the legend is
    /// the only thing standing between them and a second orb, and it is the only part of this quest's state
    /// that survives a relog by design.</summary>
    [Fact]
    public void NothingReEntersTheChainBySpeech()
    {
        Assert.DoesNotContain(ForgottenPastQuest.Chain, s => s.From == ForgottenPastQuest.NotStarted);
        Assert.Equal("forged_orb", ForgottenPastQuest.Legend);
    }

    // ---- what the quest moves between hands --------------------------------------------------------

    /// <summary>The five orbs, by id. This quest must GRANT the orbs that already ship, not mint new ones —
    /// they carry stats and a level requirement and a second copy would diverge from them silently.</summary>
    [Fact]
    public void TheFiveOrbsAreTheOnesThatAlreadyShip()
    {
        EnsureLoaded();

        var byId = ForgottenPastQuest.Orbs.ToDictionary(o => o.Element, o => Content.ItemByKey(o.Item)?.Id);
        Assert.Equal(new[] { "wood", "earth", "fire", "water", "metal" },
                     ForgottenPastQuest.Orbs.Select(o => o.Element).ToArray());
        Assert.Equal(26034, byId["wood"]);
        Assert.Equal(26035, byId["fire"]);
        Assert.Equal(26036, byId["metal"]);
        Assert.Equal(26037, byId["earth"]);
        Assert.Equal(26038, byId["water"]);
    }

    /// <summary>Every material named in a recipe, in the ore toll, and the Shu jing itself. A key that no
    /// longer resolves reads to the player as "you do not have all the required items" while they are holding
    /// exactly what was asked for.
    ///
    /// <para>This asserts the keys EXIST, not that they are obtainable — four of them (ore_high, metal,
    /// hot_coal, ice_shard) have no source in the world yet and that is the crafting system's gap, logged in
    /// docs/common/Deferred-Work.md.</para></summary>
    [Fact]
    public void EveryMaterialKeyResolves()
    {
        EnsureLoaded();

        Assert.NotNull(Content.ItemByKey(ForgottenPastQuest.ShuJing));
        Assert.Equal(12263, Content.ItemByKey(ForgottenPastQuest.ShuJing)!.Id);

        foreach (var (key, count) in ForgottenPastQuest.OreToll)
        {
            Assert.True(Content.ItemByKey(key) is not null, $"Items.csv has no '{key}' — Thane's toll is unpayable");
            Assert.Equal(5, count);
        }

        foreach (var orb in ForgottenPastQuest.Orbs)
        {
            Assert.NotEmpty(orb.Cost);
            foreach (var (key, count) in orb.Cost)
            {
                Assert.True(Content.ItemByKey(key) is not null,
                            $"Items.csv has no '{key}' — the {orb.Title} orb can never be paid for");
                Assert.True(count > 0);
            }
        }
    }

    /// <summary>The Shu jing has to be BUYABLE or Rotah's examination is a wall. The Earths Dragon is the
    /// only NPC who stocks it, and he had no composition row either — a stocked shopkeeper with no abilities
    /// is a mute one, and the item behind him is unobtainable with nothing to say why.</summary>
    [Fact]
    public void TheShuJingIsBuyableFromTheEarthsDragon()
    {
        EnsureLoaded();

        var dragon = Content.Npcs.FirstOrDefault(n => n.Key == "NpcSubpathGeomancerEarthsDragonNpc");
        Assert.True(dragon is not null, "the Earths Dragon is not in the world — the Shu jing is unobtainable");
        Assert.Contains(NpcScripts.For(dragon!), a => a is ShopAbility);

        var stock = Shops.For(dragon!.Key);
        Assert.True(stock is not null, "the Earths Dragon stocks nothing");
        Assert.Contains(stock!, c => c.Keys.Contains(ForgottenPastQuest.ShuJing));
    }

    // ---- the examination ---------------------------------------------------------------------------

    /// <summary>The five answers, in order, as the Atlas capture and the tutor post both give them. They are
    /// typed free-hand by the player, so a reordering here would fail a correct answer.</summary>
    [Fact]
    public void TheQuestionsAreTheShuJingsFiveInOrder()
    {
        Assert.Equal(new[] { "wood", "earth", "fire", "water", "metal" },
                     ForgottenPastQuest.Questions.Select(q => q.Answer).ToArray());
        Assert.All(ForgottenPastQuest.Questions, q => Assert.EndsWith("?", q.Question, StringComparison.Ordinal));
    }

    /// <summary>Both free-text reads are forgiving about case and stray spaces — the player is typing into a
    /// box — and forgiving about nothing else.</summary>
    [Fact]
    public void TypedAnswersAreCaseAndSpaceInsensitive()
    {
        Assert.True(ForgottenPastQuest.AnswerIs("  Wood ", "wood"));
        Assert.False(ForgottenPastQuest.AnswerIs("woodd", "wood"));
        Assert.False(ForgottenPastQuest.AnswerIs(null, "wood"));

        Assert.Equal("water_orb", ForgottenPastQuest.OrbFor(" WATER ")?.Item);
        Assert.Null(ForgottenPastQuest.OrbFor("wind"));
        Assert.Null(ForgottenPastQuest.OrbFor(null));
    }

    /// <summary>Atlas's level gate, pinned so a "port it faithfully to RTK" pass has to argue with a test —
    /// RTK gates this chain on nothing at all.</summary>
    [Fact]
    public void TheLevelGateIsAtlasNotRtk()
    {
        Assert.Equal(50, ForgottenPastQuest.MinLevel);
    }
}
