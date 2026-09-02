using System;
using System.Collections.Generic;
using System.Linq;
using Server;
using Shared;
using Xunit;

namespace Tests;

/// <summary>
/// The twelve lesser alliances are declared as data (game-data/MythicAlliances.csv) that names NPC ids, mob
/// keys and item keys — joins nothing else validates. A mistyped boss key does not fail a build; it produces
/// a quest whose kill requirement can never be met, and looks to a player exactly like one they are simply
/// bad at. These pin the joins.
///
/// <para>Two of them go further than "the key exists". <see cref="TributeIsFarmableInTheEnemysCave"/> is the
/// reason the mythic drop tables were corrected: the whole quest is "bring a tribute of items and keys
/// stolen from the enemies cave", so if the enemy's bosses do not actually drop what the mythic asks for,
/// the quest is unfinishable no matter how correct every individual key is. And
/// <see cref="KillTrackTests"/> pins the eight-slot list the alliance counts on, which is where every
/// documented quirk of the quest comes from.</para>
/// </summary>
public class MythicAllianceTests
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

    // ---- the roster -----------------------------------------------------------------------------

    /// <summary>Twelve animals, one per zodiac cave. A row silently dropped by the loader (a missing enemy,
    /// an empty boss list) would take a whole alliance offline with no other symptom.</summary>
    [Fact]
    public void TwelveAlliancesLoad()
    {
        EnsureLoaded();
        Assert.Equal(12, Content.MythicAlliances.Count);
        Assert.Equal(12, Content.MythicAlliances.Select(a => a.Key).Distinct().Count());
    }

    /// <summary>The war is symmetrical: every animal's enemy names it back, nobody is their own enemy, and
    /// the twelve therefore fall into six disjoint pairs. An asymmetric row would let one side offer a quest
    /// while the other refused to recognise the ally it created.</summary>
    [Fact]
    public void EnemiesAreMutualAndPairUpCleanly()
    {
        EnsureLoaded();

        foreach (var a in Content.MythicAlliances)
        {
            var foe = MythicAlliance.ByName(a.Enemy);
            Assert.True(foe is not null, $"{a.Animal}'s enemy '{a.Enemy}' is not an alliance");
            Assert.NotEqual(a.Key, foe!.Key);
            Assert.Equal(a.Key, MythicAlliance.ByName(foe.Enemy)!.Key);
        }

        var pairs = Content.MythicAlliances
            .Select(a => string.CompareOrdinal(a.Key, MythicAlliance.ByName(a.Enemy)!.Key) < 0
                ? (a.Key, MythicAlliance.ByName(a.Enemy)!.Key)
                : (MythicAlliance.ByName(a.Enemy)!.Key, a.Key))
            .Distinct().ToList();
        Assert.Equal(6, pairs.Count);
    }

    /// <summary>Every row points at a real NPC, that NPC is one of the twelve mythics, and it is the RIGHT
    /// one — the NPC's own description names the animal. An id off by one would put the Dog's quest in the
    /// Rooster's chamber, which no other check would notice.</summary>
    [Fact]
    public void EveryNpcIdResolvesToItsOwnMythic()
    {
        EnsureLoaded();

        foreach (var a in Content.MythicAlliances)
        {
            var npc = Content.NpcById(a.NpcId);
            Assert.True(npc is not null, $"{a.Animal}: no NPC {a.NpcId}");
            Assert.Equal("MythicAllianceNpc", npc!.Key);
            Assert.Equal($"Mythic {a.Animal}", npc.Name);
            Assert.Same(a, MythicAlliance.ByNpc(a.NpcId));
        }
    }

    /// <summary>Every mythic NPC in the world has a row. The reverse of the test above: an NPC without one
    /// stands in its chamber and answers to nothing.</summary>
    [Fact]
    public void EveryMythicNpcHasARow()
    {
        EnsureLoaded();

        var orphans = Content.Npcs
            .Where(n => n.Key == "MythicAllianceNpc" && MythicAlliance.ByNpc(n.Id) is null)
            .Select(n => $"{n.Id} {n.Name}").ToList();
        Assert.True(orphans.Count == 0, "mythic NPCs with no alliance row: " + string.Join(", ", orphans));
    }

    /// <summary>The ability is actually attached — the CSV composition and the C# registration have to
    /// agree, and neither half fails loudly on its own.</summary>
    [Fact]
    public void TheAbilityIsComposedOntoTheNpc()
    {
        EnsureLoaded();

        var npc = Content.NpcById(Content.MythicAlliances[0].NpcId)!;
        Assert.Contains(NpcScripts.For(npc), ab => ab is MythicAllianceAbility);
    }

    // ---- the joins ------------------------------------------------------------------------------

    /// <summary>Three key bosses and three item bosses per cave, one pair per cave tier, and every one of
    /// them a real creature.</summary>
    [Fact]
    public void EveryBossKeyResolves()
    {
        EnsureLoaded();

        var known = Content.Mobs.Select(m => m.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        foreach (var a in Content.MythicAlliances)
        {
            Assert.Equal(3, a.KeyBosses.Length);
            Assert.Equal(3, a.ItemBosses.Length);
            Assert.Equal(3, a.BossPairs().Count());
            foreach (var m in a.KeyBosses.Concat(a.ItemBosses))
                if (!known.Contains(m)) missing.Add($"{a.Animal}:{m}");
        }
        Assert.True(missing.Count == 0, "unknown mob keys: " + string.Join(", ", missing));
    }

    /// <summary>Bosses belong to exactly one cave. A key shared between two rows would make one alliance
    /// payable by hunting a different animal entirely.</summary>
    [Fact]
    public void NoBossServesTwoCaves()
    {
        EnsureLoaded();

        var dupes = Content.MythicAlliances
            .SelectMany(a => a.KeyBosses.Concat(a.ItemBosses))
            .GroupBy(m => m, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dupes.Count == 0, "bosses listed by more than one cave: " + string.Join(", ", dupes));
    }

    /// <summary>Tribute items, keys and the favour all exist, and the tribute amounts are positive — a zero
    /// would make the quest hand itself in.</summary>
    [Fact]
    public void EveryItemKeyResolves()
    {
        EnsureLoaded();

        var missing = new List<string>();
        foreach (var a in Content.MythicAlliances)
        {
            foreach (var key in new[] { a.KeyDrop, a.ItemDrop, a.Favor })
                if (Content.ItemByKey(key) is null) missing.Add($"{a.Animal}:{key}");

            Assert.True(a.KeyTribute > 0, $"{a.Animal}: KeyTribute must be positive");
            Assert.True(a.ItemTribute > 0, $"{a.Animal}: ItemTribute must be positive");
            Assert.True(a.Exp > 0, $"{a.Animal}: Exp must be positive");
            Assert.True(a.Karma > 0, $"{a.Animal}: Karma must be positive");
        }
        Assert.True(missing.Count == 0, "unknown item keys: " + string.Join(", ", missing));
    }

    /// <summary>Each animal's favour is its own. Twelve rows sharing one favour item would be invisible
    /// until a player noticed the Dog handing out the Dragon's.</summary>
    [Fact]
    public void EveryFavourIsDistinct()
    {
        EnsureLoaded();
        Assert.Equal(12, Content.MythicAlliances.Select(a => a.Favor).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// THE quest-defining join: what a mythic demands must be lootable from the cave it sends you to. Both
    /// halves of the tribute — the item and the key — come off the enemy's own bosses, and both are checked
    /// against the drop tables rather than merely against the item registry.
    ///
    /// <para>This is the test that caught the four-cave scramble in MobDrops.csv: Ambrosia, Battle helm, Tao
    /// stone and Scribe's pen were assigned to the wrong caves (Monkey/Rat and Rooster/Ox swapped), which
    /// made four of the twelve alliances ask for tribute that could not be found where the quest pointed.
    /// The period drop tables (Nexus Atlas's 5.0 cave pages, corroborated by the tutor-board drop chart) are
    /// what the data was corrected to.</para></summary>
    [Fact]
    public void TributeIsFarmableInTheEnemysCave()
    {
        EnsureLoaded();

        var wrong = new List<string>();
        foreach (var a in Content.MythicAlliances)
        {
            // The KEY comes off the key bosses, the ITEM off the item bosses. Check the halves separately —
            // a key that only dropped from item bosses would still be "in the cave" and still be wrong.
            Check(a.KeyBosses, a.KeyDrop, "key");
            Check(a.ItemBosses, a.ItemDrop, "item");

            void Check(string[] bosses, string want, string what)
            {
                var dropped = bosses.Any(b => DropsFrom(b).Contains(want, StringComparer.OrdinalIgnoreCase));
                if (!dropped) wrong.Add($"{a.Animal} {what} '{want}' drops from none of {string.Join("/", bosses)}");
            }
        }
        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }

    /// <summary>And the other direction: nothing a mythic asks for is farmable in its OWN cave. The tribute
    /// is loot stolen from the enemy — an overlap would let an ally pay the Dragon with dragon drops.</summary>
    [Fact]
    public void TributeCannotBeFarmedAtHome()
    {
        EnsureLoaded();

        var wrong = new List<string>();
        foreach (var mine in Content.MythicAlliances)
        {
            var foe = MythicAlliance.ByName(mine.Enemy)!;
            var home = mine.KeyBosses.Concat(mine.ItemBosses).SelectMany(DropsFrom).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (home.Contains(foe.KeyDrop))  wrong.Add($"{mine.Animal} asks for {foe.KeyDrop}, which its own bosses drop");
            if (home.Contains(foe.ItemDrop)) wrong.Add($"{mine.Animal} asks for {foe.ItemDrop}, which its own bosses drop");
        }
        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }

    // Every item key a mob can drop, both tables (common loot and rare loot).
    private static IEnumerable<string> DropsFrom(string mobKey)
    {
        if (!Content.MobDrops.TryGetValue(mobKey, out var table)) return Array.Empty<string>();
        return table.Loot.Select(d => d.ItemKey)
            .Concat(table.Rare.Select(d => d.ItemKey))
            .Where(k => !string.IsNullOrEmpty(k))!;
    }
}

/// <summary>
/// The kill track on its own (<see cref="KillTrack"/>). Every documented behaviour of the lesser alliances
/// is a consequence of these two rules, so they are worth pinning apart from the quest that reads them:
/// eight slots, most-recent-first, counts lost on eviction.
/// </summary>
public class KillTrackTests
{
    private static List<KillTrackEntry> Track(params string[] kills)
    {
        var t = new List<KillTrackEntry>();
        foreach (var k in kills) KillTrack.Push(t, k);
        return t;
    }

    [Fact]
    public void CountsRepeatKillsOfOneKind()
    {
        var t = Track("rabbit", "rabbit", "rabbit");
        Assert.Single(t);
        Assert.Equal(3, KillTrack.Count(t, "rabbit"));
    }

    [Fact]
    public void MostRecentKindComesFirst()
    {
        var t = Track("rabbit", "squirrel");
        Assert.Equal("squirrel", t[0].Mob);
        KillTrack.Push(t, "rabbit");
        Assert.Equal("rabbit", t[0].Mob);
        Assert.Equal(2, KillTrack.Count(t, "rabbit"));   // moving to the front does not reset the count
    }

    /// <summary>"You need reserve 2 slots on your Kill Track record for the 2 bosses the Mythic NPC asks you
    /// to kill. This means you can kill 6 other TYPES of creatures." Six is fine; the seventh costs you a
    /// boss.</summary>
    [Fact]
    public void SixOtherKindsAreFreeAndTheSeventhIsNot()
    {
        var t = Track("spirit_pig", "spirit_pig", "spirit_pig", "pig_avenger", "pig_avenger", "pig_avenger");

        foreach (var junk in new[] { "a", "b", "c", "d", "e", "f" }) KillTrack.Push(t, junk);
        Assert.Equal(3, KillTrack.Count(t, "spirit_pig"));    // both still on the track, at the very back
        Assert.Equal(3, KillTrack.Count(t, "pig_avenger"));

        KillTrack.Push(t, "g");                               // the seventh mistake
        Assert.Equal(0, KillTrack.Count(t, "spirit_pig"));    // the OLDEST kind goes, and its count with it
        Assert.Equal(3, KillTrack.Count(t, "pig_avenger"));
    }

    /// <summary>The trick the tutors teach: after a run of mistakes, kill one more of each boss BEFORE the
    /// next one. That moves both bosses back to the front, so the mistake that falls off next is a mistake
    /// rather than a boss — and the counts survive the move.</summary>
    [Fact]
    public void KillingOneMoreBossPushesTheMistakeOffInstead()
    {
        var t = Track("spirit_pig", "spirit_pig", "spirit_pig", "pig_avenger", "pig_avenger", "pig_avenger");
        foreach (var junk in new[] { "a", "b", "c", "d", "e", "f" }) KillTrack.Push(t, junk);

        KillTrack.Push(t, "spirit_pig");                      // back to the front, count intact
        KillTrack.Push(t, "pig_avenger");
        Assert.Equal("pig_avenger", t[0].Mob);
        Assert.Equal("spirit_pig", t[1].Mob);

        KillTrack.Push(t, "g");                               // the same seventh mistake as above...
        Assert.Equal(4, KillTrack.Count(t, "spirit_pig"));    // ...and this time it costs a mistake, not a boss
        Assert.Equal(4, KillTrack.Count(t, "pig_avenger"));
        Assert.Equal(0, KillTrack.Count(t, "a"));
    }

    [Fact]
    public void NeverHoldsMoreThanEightKinds()
    {
        var t = Track(Enumerable.Range(0, 40).Select(i => $"mob{i}").ToArray());
        Assert.Equal(KillTrack.Slots, t.Count);
        Assert.Equal("mob39", t[0].Mob);
        Assert.Equal("mob32", t[^1].Mob);
    }

    /// <summary>"The database will record up to 255 kills of a single creature."</summary>
    [Fact]
    public void CountSaturatesAt255()
    {
        var t = new List<KillTrackEntry>();
        for (int i = 0; i < 300; i++) KillTrack.Push(t, "rabbit");
        Assert.Equal(KillTrack.Cap, KillTrack.Count(t, "rabbit"));
    }

    /// <summary>A kind that falls off and comes back starts over. This is why starting an alliance and THEN
    /// hunting is the only order that works — nothing banked before is recoverable.</summary>
    [Fact]
    public void ReturningAfterEvictionStartsFromZero()
    {
        var t = Track("boss", "boss", "boss");
        foreach (var junk in new[] { "a", "b", "c", "d", "e", "f", "g", "h" }) KillTrack.Push(t, junk);
        Assert.Equal(0, KillTrack.Count(t, "boss"));

        KillTrack.Push(t, "boss");
        Assert.Equal(1, KillTrack.Count(t, "boss"));
    }
}
