using Server;
using Xunit;

namespace Tests;

/// <summary>
/// The CI gate. Not unit tests of game logic — a check that the CONTENT still loads, because that is how this
/// server actually breaks. Almost everything here is driven by <c>game-data/*.csv</c> and three Lua files
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

    /// <summary>A brand-new character's spawn tile can actually reach the rest of the world.
    ///
    /// <para>This is not a hypothetical. Welcome (4711) shipped with its only exits at <c>(9,16)</c> and
    /// <c>(10,16)</c> on a 16x16 map — rows 0-15 — so both source tiles were out of bounds and the room was
    /// a sealed box. Nothing detects that at load time: the warp rows parse fine, the map loads fine, and the
    /// first symptom is a new player who cannot move on. Assert the doorway instead of trusting it.</para></summary>
    [Fact]
    public void NewbieAreaFirstDoorwayIsReachable()
    {
        EnsureLoaded();

        var start = Shared.CharacterFactory.StartFor(1);
        if (start.map != 4711) return;    // pre-area era configured; the tutor-home spawn has no doorway to check

        Assert.True(start.x < start.xs && start.y < start.ys,
            $"spawn ({start.x},{start.y}) is outside Welcome's {start.xs}x{start.ys} bounds");

        for (ushort wx = 8; wx <= 11; wx++)
            Assert.True(Content.TryWarp(4711, wx, 15, out var d) && d.m == 4712,
                $"Welcome (4711) tile ({wx},15) must warp into Open Field (4712) — without it a new character is sealed in");
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

    /// <summary>
    /// The armor-dye ramp remap (ArmorDyeRamps.csv). Silent-failure shaped in the usual way: if the file goes
    /// missing or its header stops matching, <see cref="Content.DyeRampFor"/> quietly becomes the identity and
    /// dyes go back to rendering the wrong colour on any body whose Body.tbl palette is not the seasonal one —
    /// nothing throws, nothing logs, the war paint just lies. Wind armor (bodies 36..43, Palette 1) is the only
    /// family affected today: ramp 10 is black on Palette 0 but BROWN there, so a Hyun moo dye came out brown.
    /// </summary>
    [Fact]
    public void ArmorDyeRampsRemapWindArmor()
    {
        EnsureLoaded();

        Assert.NotEmpty(Content.ArmorDyeRamps);

        // Wind bodies must move OFF the canonical value for the colours Palette 1 disagrees on...
        Assert.Equal(28, Content.DyeRampFor(38, 10));    // Hyun moo  black -> Palette 1's grayscale row
        Assert.Equal(29, Content.DyeRampFor(38, 11));    // Baekho    white
        Assert.Equal(3,  Content.DyeRampFor(38, 17));    // River     kept distinct from Chung ryong's blue
        Assert.Equal(16, Content.DyeRampFor(38, 20));    // Fire      orange (Palette 1's ramp 20 is khaki)
        Assert.Equal(24, Content.DyeRampFor(38, 24));    // Chung ryong blue — a SHARED row, so identity
        Assert.Equal(31, Content.DyeRampFor(38, 31));    // Ju jak    vermilion — shared row, identity

        // ...and every other body must pass through untouched, or we would break the armors that work.
        foreach (byte dye in WarPaintAbility.TeamColors)
        {
            Assert.Equal(dye, Content.DyeRampFor(4, dye));    // Palette 0, the seasonal armors
            Assert.Equal(dye, Content.DyeRampFor(48, dye));   // Palette 2 (ice) — identical ramps to Palette 0
        }
    }

    /// <summary>Every Arena Master team colour must have a ramp defined for the wind bodies, or that team
    /// silently renders as some unrelated hue there (the original bug). Catches the easy mistake of retuning
    /// <see cref="WarPaintAbility"/> without adding the matching ArmorDyeRamps.csv row.</summary>
    [Fact]
    public void EveryTeamDyeHasAWindArmorRamp()
    {
        EnsureLoaded();

        var missing = WarPaintAbility.TeamColors
            .Where(c => !Content.ArmorDyeRamps.ContainsKey(((ushort)38, c)))
            .ToList();

        Assert.True(missing.Count == 0,
            "Arena Master team colour(s) with no wind-armor (Palette 1) ramp in ArmorDyeRamps.csv: "
            + string.Join(", ", missing) + ". They will render the wrong colour on wind armor.");
    }

    /// <summary>Every return-destination group <see cref="Session"/> can route a player to exists in
    /// Inns.csv, and its maps are real.
    ///
    /// <para><c>Session.HomeGroup</c> names these groups as string literals, and a name that doesn't match a
    /// row does not fail — <c>Inns.GetValueOrDefault</c> returns null and Return quietly falls through to the
    /// home-city safety net. So a renamed or misspelled group would send a neutral to Kugnae's palace
    /// forever with nothing in the log to say why.</para></summary>
    [Fact]
    public void EveryReturnDestinationGroupResolves()
    {
        EnsureLoaded();

        foreach (var group in new[] { "Kugnae", "Buya", "Nagnang", "Wilderness", "Sanhae", "Hausson" })
        {
            var inns = Content.Inns.GetValueOrDefault(group);
            Assert.True(inns is { Count: > 0 }, $"Inns.csv has no '{group}' group — Session.HomeGroup names it");
            foreach (var inn in inns!)
            {
                Assert.True(Content.TryMap(inn.Map, out _), $"Inns.csv '{group}' points at unknown map {inn.Map}");
                Assert.True(inn.X2 >= inn.X && inn.Y2 >= inn.Y,
                    $"Inns.csv '{group}' map {inn.Map} has an inverted arrival box");
            }
        }

        // The wilderness clearing is the one group that is a BOX rather than a bed — neutrals have no tavern
        // to wake up in, so RTK scatters them across a few tiles. A collapsed box here means the X2/Y2
        // columns stopped parsing and every neutral now lands on the same tile.
        var wild = Content.Inns["Wilderness"][0];
        Assert.True(wild.X2 > wild.X && wild.Y2 > wild.Y, "the Wilderness return box collapsed to one tile");
    }
}
