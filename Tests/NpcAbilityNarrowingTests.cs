using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The narrowing in game-data/NpcAbilities.csv (<c>name@39</c>, <c>name@map:324</c>) against everything it
/// has to agree with. Every failure here is the silent kind: an id that names nobody, or names an NPC of
/// another identifier, leaves the ability on no one, and a quest simply stops being offered with nothing in
/// the log a player would ever see.
///
/// <para><b>The set-equality facts.</b> Four abilities keep a per-NPC table because the table carries data
/// the ability needs (a guildmaster's path, a shrine's totem, a mythic's alliance row, a shrine map's
/// alignment). The CSV decides which NPCs carry the ability; the table decides what it does there. If the two
/// drift, an NPC either carries an ability that has nothing to say, or has a table row that nothing reaches.
/// Each fact holds the CSV's set equal to the table's keys, and also to the NPCs that actually carry the
/// ability after <see cref="NpcScripts.For"/> applies it.</para>
///
/// <para>Falsified by hand on the branch that added it: dropping 41 from <c>armor_quest@36;41</c> turns
/// <see cref="ArmorQuestNamesExactlyTheGuildMasters"/> red; adding a bogus 9999 to <c>sute@39</c> turns
/// <see cref="EveryNarrowedIdIsAnNpcOfItsOwnIdentifier"/> red.</para>
///
/// <para>Collection <c>"log"</c> (runs alone) because <see cref="TheLoaderLogsABadNarrowingAndFailsClosed"/>
/// holds the process-wide log line sink; see <see cref="TestSeamCollectionTests"/>.</para>
/// </summary>
[Collection("log")]
public class NpcAbilityNarrowingTests
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

    /// <summary>Every entry in the CSV, with the row it is on.</summary>
    private static IEnumerable<(string Key, NpcAbilityRef Ref)> Entries() =>
        Content.NpcCompositions.SelectMany(row => row.Value.Select(r => (row.Key, r)));

    /// <summary>The NpcIds the CSV writes for <paramref name="ability"/>, across every row. Every entry of that
    /// ability must be narrowed by NpcId — a plain one would put it on a whole identifier, which is exactly
    /// what these tables exist to prevent.</summary>
    private static int[] CsvNpcIds(string ability)
    {
        var entries = Entries().Where(e => e.Ref.Name == ability).ToList();
        Assert.NotEmpty(entries);
        foreach (var (key, r) in entries)
            Assert.True(r.NpcIds is not null, $"{key}'s {ability} is not narrowed to NpcIds");
        return entries.SelectMany(e => e.Ref.NpcIds!).Distinct().OrderBy(id => id).ToArray();
    }

    /// <summary>The same for a map narrowing.</summary>
    private static int[] CsvMapIds(string ability)
    {
        var entries = Entries().Where(e => e.Ref.Name == ability).ToList();
        Assert.NotEmpty(entries);
        foreach (var (key, r) in entries)
            Assert.True(r.MapIds is not null, $"{key}'s {ability} is not narrowed to map ids");
        return entries.SelectMany(e => e.Ref.MapIds!).Distinct().OrderBy(id => id).ToArray();
    }

    /// <summary>The NPCs that actually end up carrying <paramref name="ability"/>: every loaded NPC whose row
    /// lists it and whose narrowing lets it through.</summary>
    private static int[] Carriers(string ability) =>
        Content.Npcs
            .Where(n => Content.NpcCompositions.TryGetValue(n.Key, out var refs)
                        && refs.Any(r => r.Name == ability && r.AppliesTo(n)))
            .Select(n => n.Id).OrderBy(id => id).ToArray();

    private static int[] Sorted(IEnumerable<int> ids) => ids.Distinct().OrderBy(id => id).ToArray();

    // ---- item 1: the tables that stay, held equal to the CSV -----------------------------------------

    [Fact]
    public void ArmorQuestNamesExactlyTheGuildMasters()
    {
        EnsureLoaded();
        var table = Sorted(ArmorQuest.GuildMasters.Keys);
        Assert.Equal(table, CsvNpcIds("armor_quest"));
        Assert.Equal(table, Carriers("armor_quest"));
    }

    [Fact]
    public void TotemWorshipNamesExactlyTheShrines()
    {
        EnsureLoaded();
        var table = Sorted(TotemWorship.Shrines.Keys);
        Assert.Equal(table, CsvNpcIds("totem_worship"));
        Assert.Equal(table, Carriers("totem_worship"));
    }

    [Fact]
    public void MythicAllianceNamesExactlyTheAllianceRows()
    {
        EnsureLoaded();
        var table = Sorted(Content.MythicAlliances.Select(a => a.NpcId));
        Assert.NotEmpty(table);
        Assert.Equal(table, CsvNpcIds("mythic_alliance"));
        Assert.Equal(table, Carriers("mythic_alliance"));
    }

    [Fact]
    public void AlignmentNamesExactlyTheShrineMaps()
    {
        EnsureLoaded();
        Assert.Equal(Sorted(AlignmentAbility.Shrines.Keys), CsvMapIds("alignment"));

        // And the shamans that carry it are exactly the AlignmentNpcs standing on those maps.
        var onShrines = Content.Npcs
            .Where(n => n.Key == "AlignmentNpc" && AlignmentAbility.Shrines.ContainsKey(n.Map))
            .Select(n => n.Id);
        Assert.Equal(Sorted(onShrines), Carriers("alignment"));
        Assert.Equal(AlignmentAbility.Shrines.Count, Carriers("alignment").Length);
    }

    /// <summary>dagger_uniform (#302 F3) shipped with no CSV narrowing on the RogueTrainerNpc row at all:
    /// every RogueTrainerNpc — not just Dagger, Maro and Maso — carried the ability and refused the other
    /// eleven itself from inside DaggerUniformAbility. The CSV now says which three. Two of them, Dagger
    /// (138) and Maro (37), are the ids <see cref="DaggerUniformAbility.Entries"/> checks by id
    /// (DaggerUniformQuest.cs:193, :199); the third, Maso (42), is reached with the 'h' gesture and is
    /// checked instead in <c>DaggerUniformAbility.OnHandItem</c> (DaggerUniformQuest.cs:365). All three are
    /// the ability's own per-NPC checks, split across its two dispatch methods, so the narrowing must equal
    /// all three or one of them silently stops being offered.
    ///
    /// <para>This does NOT use <see cref="CsvNpcIds"/>/<see cref="Carriers"/> as the other four facts do:
    /// <c>dagger_uniform</c> also has a second, deliberately un-narrowed row on <c>BlackbirdNpc</c> (the
    /// crow, NPCs.csv 139 — its own identifier has only him, so a plain entry there already narrows to one
    /// NPC without an explicit id list). This fact is scoped to the RogueTrainerNpc row alone.</para></summary>
    [Fact]
    public void DaggerUniformNamesExactlyTheThreeSpeakingRogueMasters()
    {
        EnsureLoaded();
        var table = Sorted(new[]
        {
            DaggerUniformQuest.DaggerNpcId,
            DaggerUniformQuest.MaroNpcId,
            DaggerUniformQuest.MasoNpcId,
        });

        var rogueEntry = Entries().Single(e => e.Key == "RogueTrainerNpc" && e.Ref.Name == "dagger_uniform");
        Assert.True(rogueEntry.Ref.NpcIds is not null, "RogueTrainerNpc's dagger_uniform is not narrowed to NpcIds");
        Assert.Equal(table, Sorted(rogueEntry.Ref.NpcIds!));

        var rogueCarriers = Content.Npcs
            .Where(n => n.Key == "RogueTrainerNpc"
                        && Content.NpcCompositions.TryGetValue(n.Key, out var refs)
                        && refs.Any(r => r.Name == "dagger_uniform" && r.AppliesTo(n)))
            .Select(n => n.Id);
        Assert.Equal(table, Sorted(rogueCarriers));
    }

    // ---- item 3: the smoke test ----------------------------------------------------------------------

    /// <summary>Every NpcId the CSV narrows to is a loaded NPC (NPCs.csv, not dropped at load), and one of the
    /// identifier whose row names it — an id of another identifier can never match, since the row is only
    /// consulted for its own.</summary>
    [Fact]
    public void EveryNarrowedIdIsAnNpcOfItsOwnIdentifier()
    {
        EnsureLoaded();
        var problems = new List<string>();
        foreach (var (key, r) in Entries())
        {
            if (r.NpcIds is null) continue;
            if (r.NpcIds.Count == 0) problems.Add($"{key}: {r.Name} is narrowed to no NPC at all");
            foreach (int id in r.NpcIds)
            {
                var npc = Content.NpcById(id);
                if (npc is null) problems.Add($"{key}: {r.Name}@{id} names no NPC in NPCs.csv");
                else if (!string.Equals(npc.Key, key, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{key}: {r.Name}@{id} is {npc.Name}, a {npc.Key}");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>Every MapId the CSV narrows to is in the maps table, and an NPC of the row's identifier stands
    /// on it.</summary>
    [Fact]
    public void EveryNarrowedMapIsARealMapWithTheIdentifierOnIt()
    {
        EnsureLoaded();
        var problems = new List<string>();
        foreach (var (key, r) in Entries())
        {
            if (r.MapIds is null) continue;
            if (r.MapIds.Count == 0) problems.Add($"{key}: {r.Name} is narrowed to no map at all");
            foreach (int id in r.MapIds)
            {
                if (id > ushort.MaxValue || !Content.TryMap((ushort)id, out _))
                    problems.Add($"{key}: {r.Name}@map:{id} is not in the maps table");
                else if (!Content.Npcs.Any(n => n.Map == id && string.Equals(n.Key, key, StringComparison.OrdinalIgnoreCase)))
                    problems.Add($"{key}: {r.Name}@map:{id} has no {key} standing on it");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>Every ability name resolves to an implementation. <see cref="NpcScripts.For"/> skips an
    /// unknown name without a word, so a typo in the CSV would otherwise just quietly remove a service.</summary>
    [Fact]
    public void EveryAbilityNameIsRegistered()
    {
        EnsureLoaded();
        var unknown = Entries().Where(e => !NpcScripts.IsRegistered(e.Ref.Name))
                               .Select(e => $"{e.Key}: '{e.Ref.Name}'").ToList();
        Assert.True(unknown.Count == 0, "unregistered ability names: " + string.Join(", ", unknown));
    }

    // ---- the loader: what a bad narrowing does, and that it says so -----------------------------------

    /// <summary>A narrowing that names nothing real is logged at load and FAILS CLOSED: it stays a narrowing,
    /// so the ability reaches nobody through the bad id, rather than falling open onto every NPC of the
    /// identifier. A malformed id is dropped, never read as "no narrowing".</summary>
    [Fact]
    public void TheLoaderLogsABadNarrowingAndFailsClosed()
    {
        EnsureLoaded();
        var npcs = Content.Npcs;
        var byId = npcs.ToDictionary(n => n.Id);
        var maps = Content.Maps;
        var eldritch = Content.NpcById(39)!;
        var haedu = Content.NpcById(35)!;

        NpcAbilityRef Parse(string key, string token) => Content.ParseNpcAbility(key, token, npcs, byId, maps);

        using var log = LogLineSink.Acquire();

        var plain = Parse("MageTrainerNpc", "sute");
        Assert.Null(plain.NpcIds);
        Assert.Null(plain.MapIds);
        Assert.True(plain.AppliesTo(haedu));

        var one = Parse("MageTrainerNpc", "sute@39");
        Assert.Equal(new[] { 39 }, one.NpcIds!.OrderBy(i => i));
        Assert.True(one.AppliesTo(eldritch));
        Assert.False(one.AppliesTo(haedu));

        var missing = Parse("MageTrainerNpc", "sute@39;9999");
        Assert.Equal(new[] { 39, 9999 }, missing.NpcIds!.OrderBy(i => i));
        log.LineContaining("NpcKey='MageTrainerNpc', 'sute@39;9999': NPC 9999 is not in NPCs.csv");

        var foreign = Parse("MageTrainerNpc", "sute@41");
        Assert.False(foreign.AppliesTo(eldritch));
        log.LineContaining("'sute@41': NPC 41 (Yabaek) is a WarriorTrainerNpc, not a MageTrainerNpc");

        var malformed = Parse("MageTrainerNpc", "sute@x");
        Assert.NotNull(malformed.NpcIds);
        Assert.Empty(malformed.NpcIds!);
        Assert.False(malformed.AppliesTo(eldritch));
        log.LineContaining("'sute@x': 'x' is not an NPC id");
        log.LineContaining("'sute@x': narrowed to no NPC at all");

        var badMap = Parse("AlignmentNpc", "alignment@map:324;65000");
        Assert.Equal(new[] { 324, 65000 }, badMap.MapIds!.OrderBy(i => i));
        log.LineContaining("'alignment@map:324;65000': map 65000 is not in the maps table");

        var emptyMap = Parse("AlignmentNpc", "alignment@map:12");
        log.LineContaining("'alignment@map:12': no AlignmentNpc stands on map 12");
        Assert.False(emptyMap.AppliesTo(Content.NpcById(188)!));
    }
}
