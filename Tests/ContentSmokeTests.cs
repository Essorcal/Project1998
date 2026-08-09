using Server;
using Xunit;

namespace Tests;

/// <summary>
/// The CI gate. Not unit tests of game logic — a check that the CONTENT still loads, because that is how this
/// server actually breaks. Almost everything here is driven by <c>data/game-data/*.csv</c> and three Lua files
/// that the compiler never sees: a stray comma in Items.csv, a renamed spell verb, a missing .map file. None
/// of that fails a <c>dotnet build</c>, and all of it reaches players.
///
/// The failure mode this exists to stop is the SILENT one. <see cref="Content.Load"/> does not throw on a bad
/// row — it logs and carries on with a smaller registry — and a spell whose Lua verb went missing does not
/// error either, it just quietly stops doing anything (which is exactly the ~145-spell no-op bug this codebase
/// has already been through once).
/// </summary>
public class ContentSmokeTests
{
    // Content.Load populates static registries, so every test in this class shares one load. Doing it in a
    // fixture rather than per-test also means a Load() that THROWS fails once, loudly, instead of N times.
    private static readonly object _gate = new();
    private static bool _loaded;

    private static void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            Content.Load();     // throws => the whole content pipeline is broken; that IS the test result
            _loaded = true;
        }
    }

    /// <summary>Every registry that the world cannot function without. Empty means the CSV was not found or
    /// failed to parse — the "!!! NO CONTENT LOADED" case Program.cs warns about at startup, caught before
    /// it ships instead of after.</summary>
    [Fact]
    public void CoreRegistriesAreNotEmpty()
    {
        EnsureLoaded();

        Assert.True(Content.Maps.Count   > 0, "map_index.csv loaded no maps");
        Assert.True(Content.Mobs.Count   > 0, "mobs.csv loaded no monsters");
        Assert.True(Content.Items.Count  > 0, "Items.csv loaded no items");
        Assert.True(Content.Warps.Count  > 0, "Warps.csv loaded no warps");
        Assert.True(Content.Spells.Count > 0, "Spells.csv loaded no spells");
        Assert.True(Content.Npcs.Count   > 0, "NPCs.csv loaded no NPCs");
    }

    /// <summary>The three Lua files compile. <see cref="Content.RejectedScripts"/> is how LuaVerbHost reports a
    /// script it refused to swap in — on a live server that is benign (the previous copy keeps running) which
    /// is precisely why it must be caught here: a deploy starts with NO previous copy, so a rejected script
    /// means every spell, item use, or NPC dialog it backs is dead on arrival.</summary>
    [Fact]
    public void LuaScriptsAllCompile()
    {
        EnsureLoaded();

        Assert.True(Content.RejectedScripts.Count == 0,
            "Lua script(s) failed to compile and were rejected: " + string.Join(", ", Content.RejectedScripts));
    }

    /// <summary>Every <c>SpellParams.csv</c> row names a verb that actually exists in <c>spell_verbs.lua</c>.
    /// A row pointing at a missing verb is the silent no-op: <see cref="SpellScript.Run"/> returns null, the
    /// cast falls through to the C# dispatch, and the spell degrades to "spend mana, say nothing".</summary>
    [Fact]
    public void EverySpellParamsRowNamesALoadedVerb()
    {
        EnsureLoaded();

        var missing = new List<string>();
        foreach (var (key, row) in Content.SpellParams)
        {
            var verb = row.GetValueOrDefault("verb", "");
            if (string.IsNullOrWhiteSpace(verb)) { missing.Add($"{key} (no verb column)"); continue; }
            if (!SpellScript.HasVerb(verb)) missing.Add($"{key} -> {verb}");
        }

        Assert.True(missing.Count == 0,
            $"{missing.Count} SpellParams row(s) name a verb that spell_verbs.lua does not define: "
            + string.Join(", ", missing.Take(20)) + (missing.Count > 20 ? ", ..." : ""));
    }

    /// <summary>Every spell with a params row still exists in Spells.csv. Catches the other direction of the
    /// same drift — a spell renamed or deleted in one file and not the other.</summary>
    [Fact]
    public void EverySpellParamsRowMatchesAKnownSpell()
    {
        EnsureLoaded();

        var known = Content.Spells.Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphans = Content.SpellParams.Keys.Where(k => !known.Contains(k)).ToList();

        Assert.True(orphans.Count == 0,
            $"{orphans.Count} SpellParams row(s) have no matching spell in Spells.csv: "
            + string.Join(", ", orphans.Take(20)) + (orphans.Count > 20 ? ", ..." : ""));
    }

    /// <summary>
    /// Terrain. THIS is the one that bites on a first Linux deploy (see deploy/README.md §2): a missing .map
    /// file throws nothing and logs one line — collision and spawn placement just silently degrade and players
    /// walk through walls. On Windows it hides even better, because MapData falls back to the local client
    /// install, so a developer machine looks fine while the host is empty.
    /// </summary>
    [Fact]
    public void EveryMapHasItsTerrainFile()
    {
        EnsureLoaded();

        var (found, total, dirs) = MapData.Availability(Content.Maps.Keys);

        Assert.True(found == total,
            $"{total - found} of {total} map(s) have no .map file (searched: {string.Join(" | ", dirs)}). "
            + "Collision and spawn placement are degraded on those maps.");
    }
}
