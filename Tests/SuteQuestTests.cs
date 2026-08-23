using System.Linq;
using Server;
using Xunit;

namespace Tests;

/// <summary>
/// The Sute quest is a chain of CSV rows joined by one C# ability and one tile trigger (see
/// <see cref="SuteQuest"/>), and every join is the silent kind: none of it fails a build, and a break in the
/// middle looks exactly like a player who "just can't get in". These pin the joins.
///
/// <para>The specific regressions this guards, in walk order: Eldritch losing the <c>sute</c> ability (his
/// menu still works, "sute" just goes unanswered); the cave mouth's ground or object layer changing so the
/// trigger tile can't be stepped on at all; the exit warps moving onto the mouth row (which would bounce a
/// player leaving the cave straight back into the trigger); Sute vanishing from his nest's spawn table; and
/// the key drop or the key item disappearing, which strands the turn-in.</para>
/// </summary>
public class SuteQuestTests
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

    /// <summary>The quest is spoken, so the ONLY thing that makes it reachable is Eldritch carrying the
    /// ability — there is no menu entry that would visibly disappear if this row were dropped.</summary>
    [Fact]
    public void EldritchAnswersToSute()
    {
        EnsureLoaded();

        var eldritch = Content.Npcs.FirstOrDefault(n => n.Id == 39);
        Assert.NotNull(eldritch);
        Assert.Equal("MageTrainerNpc", eldritch!.Key);
        Assert.Equal(367, eldritch.Map);                     // Eldritch Sanctum, the Buya Mage Guild

        var abilities = NpcScripts.For(eldritch);
        Assert.Contains(abilities, a => a is SuteQuestAbility);
        // and it must be reachable by EAR specifically — the say dispatcher only considers INpcSayHandler.
        Assert.Contains(abilities.OfType<INpcSayHandler>(), h => h is SuteQuestAbility);
    }

    /// <summary>NINE NPCs share the <c>MageTrainerNpc</c> identifier — Eldritch, Kugnae's Haedu, Wand, and
    /// six subpath masters — so composing the ability onto that identifier hands it to all nine. Only
    /// Eldritch may actually answer: the dialog is his in the first person ("Eldritch's face looks grim",
    /// "I sealed Sute…"), and both period sources send the player specifically to the Buya Mage Guild
    /// master. This pins the in-ability gate that RTK does not have.</summary>
    [Fact]
    public void OnlyTheBuyaGuildMasterAnswers()
    {
        EnsureLoaded();

        var trainers = Content.Npcs.Where(n => n.Key == "MageTrainerNpc").ToList();
        Assert.True(trainers.Count > 1, "expected the identifier to be shared — the gate below is why");
        Assert.Contains(trainers, n => n.Id == SuteQuest.GuildMasterNpcId);

        // Every one of them CARRIES the ability (there is no per-NPC composition key) …
        foreach (var t in trainers)
            Assert.Contains(NpcScripts.For(t), a => a is SuteQuestAbility);

        // … and every one of them EXCEPT Eldritch declines the word, so it falls through to normal chat.
        foreach (var t in trainers.Where(n => n.Id != SuteQuest.GuildMasterNpcId))
            Assert.False(SuteQuestAbility.AnswersFor(t), $"{t.Name} (npc {t.Id}) answers to \"sute\" but should not");

        Assert.True(SuteQuestAbility.AnswersFor(trainers.Single(n => n.Id == SuteQuest.GuildMasterNpcId)));
    }

    /// <summary>The cave mouth has to be STEPPABLE, or the trigger never runs and the quest dead-ends with
    /// no message at all. Two things can take it away: the ground pass flag, and an object-layer id that
    /// SObj.tbl calls a wall (the tiles either side of the mouth are exactly that, 0x0F all-directions).</summary>
    [Fact]
    public void CaveMouthIsWalkableAndFlankedByWalls()
    {
        EnsureLoaded();

        var buya = MapData.For(SuteQuest.BuyaMap);
        Assert.NotNull(buya);

        foreach (int x in SuteQuest.MouthX)
        {
            Assert.False(buya!.Solid(x, SuteQuest.MouthY), $"cave mouth ({x},{SuteQuest.MouthY}) is solid ground");
            Assert.False(ObjectFlags.Blocks(buya.Obj(x, SuteQuest.MouthY), 0), $"cave mouth ({x},{SuteQuest.MouthY}) is object-walled from the south");
            // and the refusal path must have somewhere to put them
            Assert.False(buya.Solid(x, SuteQuest.MouthPushToY), $"push-back tile ({x},{SuteQuest.MouthPushToY}) is solid");
        }

        // The mouth is a two-tile gap in a wall; if the flanks stop blocking, the whole gate is bypassable.
        foreach (int x in new[] { SuteQuest.MouthX[0] - 1, SuteQuest.MouthX[1] + 1 })
            Assert.True(buya!.Solid(x, SuteQuest.MouthY) || ObjectFlags.Blocks(buya.Obj(x, SuteQuest.MouthY), 0),
                        $"the wall beside the cave mouth at ({x},{SuteQuest.MouthY}) no longer blocks");
    }

    /// <summary>Where the powder puts you, and where walking out puts you back. The exit warps must land on
    /// the push-back row, NOT the mouth row — landing on the mouth would re-fire the trigger the instant a
    /// player left the cave, and (uncoated by then) shove them south in an endless bounce.</summary>
    [Fact]
    public void LandingTilesAreWalkableAndTheExitDoesNotReenterTheMouth()
    {
        EnsureLoaded();

        var welcome = MapData.For(SuteQuest.WelcomeMap);
        Assert.NotNull(welcome);
        foreach (int x in new[] { SuteQuest.LandX0, SuteQuest.LandX1 })
            Assert.False(welcome!.Solid(x, SuteQuest.LandY), $"landing tile ({x},{SuteQuest.LandY}) is solid");

        var exits = Content.Warps
            .Where(w => w.Key.m == SuteQuest.WelcomeMap && w.Value.m == SuteQuest.BuyaMap)
            .ToList();
        Assert.Equal(2, exits.Count);
        foreach (var e in exits)
        {
            Assert.NotEqual(SuteQuest.MouthY, e.Value.y);
            Assert.Equal(SuteQuest.MouthPushToY, e.Value.y);
            Assert.Contains(e.Value.x, SuteQuest.MouthX.Select(v => (ushort)v));
        }
    }

    /// <summary>Sute himself, his nest, and the seven rooms — all level 28, matching the level the ability
    /// gates on. A room quietly dropping to level 0 would let an under-level player who begged a coating walk
    /// the whole dungeon.</summary>
    [Fact]
    public void TheSevenRoomsExistAndAreLevelTwentyEight()
    {
        EnsureLoaded();

        for (ushort id = 441; id <= 447; id++)
            Assert.True(Content.Maps.ContainsKey(id), $"Sute's Cave room {id} is missing");

        Assert.Equal("Sute's Nest", Content.Maps[442].Name);
        Assert.Equal("Sute's Welcome", Content.Maps[SuteQuest.WelcomeMap].Name);

        var sute = Content.MobByKey("sute");
        Assert.NotNull(sute);
        Assert.Equal(SuteQuest.MinLevel, sute!.Level);

        // exactly one Sute, and only in his nest
        var spawns = Content.AreaSpawns.Where(s => s.MobId == sute.Id).ToList();
        Assert.Single(spawns);
        Assert.Equal(442, spawns[0].Map);
        Assert.Equal(1, spawns[0].Count);
    }

    /// <summary>The turn-in token. The drop is 100% because the quest has no other completion signal — a
    /// rate below 100 turns "kill Sute" into "kill Sute repeatedly", and a missing item key means the drop
    /// silently rolls nothing at all.</summary>
    [Fact]
    public void SuteAlwaysDropsHisKey()
    {
        EnsureLoaded();

        Assert.True(Content.MobDrops.TryGetValue("sute", out var drops), "sute has no drop table");
        var key = drops!.Loot.FirstOrDefault(l => l.ItemKey == SuteQuest.KeyItem);
        Assert.NotNull(key);
        Assert.Equal(100, key!.RatePercent);

        Assert.NotNull(Content.ItemByKey(SuteQuest.KeyItem));
    }
}
