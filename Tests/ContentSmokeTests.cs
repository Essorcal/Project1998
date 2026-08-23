using System.Linq;
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

    /// <summary>Every consumable's <c>ItemParams.csv</c> row names a verb <c>item_verbs.lua</c> actually
    /// defines — the item-side twin of <see cref="EverySpellParamsRowNamesALoadedVerb"/>. A row pointing at a
    /// missing verb falls through to the item DB's Vita/Mana columns, which almost no item carries, so the
    /// consumable is eaten for nothing.</summary>
    [Fact]
    public void EveryItemParamsRowNamesALoadedVerb()
    {
        EnsureLoaded();

        var missing = new List<string>();
        foreach (var (key, row) in Content.ItemParams)
        {
            var verb = row.GetValueOrDefault("verb", "");
            if (verb.Length == 0) { missing.Add($"{key} -> (blank)"); continue; }
            if (!ItemScript.HasVerb(verb)) missing.Add($"{key} -> {verb}");
        }
        Assert.True(missing.Count == 0,
            $"{missing.Count} ItemParams row(s) name a verb that item_verbs.lua does not define: "
            + string.Join(", ", missing));
    }

    /// <summary>Every ward a potion or scroll grants is one something actually READS.
    ///
    /// <para>This is the test for the bug that motivated it. A ward is a name and an expiry in the
    /// setDuration/hasDuration map; setting one is not an effect. Ten potions and scrolls — the whole Sanctuary
    /// line, Harden Armor, the Scroll of Immortality, the Black Potion — set wards that no code anywhere looked
    /// up, so they consumed the item, played the animation, and did nothing measurable. Nothing failed; the
    /// items just quietly weren't worth drinking.</para>
    ///
    /// <para>A ward is legitimate two ways: it names a <c>category</c>, which routes it into the slot its SPELL
    /// version uses (so the spell's own effect applies), or its status key appears in
    /// <see cref="Content.ReadStatusWards"/> next to the code that reads it. Anything else is a no-op.</para></summary>
    [Fact]
    public void EveryItemWardIsActuallyRead()
    {
        EnsureLoaded();

        var inert = new List<string>();
        var badCat = new List<string>();
        foreach (var (key, row) in Content.ItemParams)
        {
            var verb = row.GetValueOrDefault("verb", "");
            if (verb is not ("ward" or "hardenbody")) continue;

            var category = row.GetValueOrDefault("category", "");
            if (category.Length > 0)
            {
                if (!Content.ItemWardCategories.Contains(category)) badCat.Add($"{key} -> '{category}'");
                continue;   // routed into the spell system's slot; the spell's effect is what applies
            }
            var statusKey = row.GetValueOrDefault("statuskey", "");
            if (statusKey.Length == 0 || !Content.ReadStatusWards.Contains(statusKey))
                inert.Add($"{key} -> '{statusKey}'");
        }

        Assert.True(badCat.Count == 0,
            $"{badCat.Count} ward row(s) name a category Session.ItemApplyWard cannot route, so they apply "
            + "nothing: " + string.Join(", ", badCat));
        Assert.True(inert.Count == 0,
            $"{inert.Count} ward row(s) set a status nothing reads — the item is consumed and has no effect. "
            + "Give it a `category` (to reuse its spell version's slot) or a reader + a Content.ReadStatusWards "
            + "entry: " + string.Join(", ", inert));
    }

    /// <summary>Every hand-keyed post-cast pool cost still names a real spell, and every spell that carries one
    /// still has the <c>spell_effects.csv</c> row those costs are layered on top of.
    ///
    /// <para>This exists because the failure is invisible from the game. A pool cost (Hellfire's 70% of mana,
    /// Slash's 10% of vita, Restore's third) lives in a <see cref="Content"/> dictionary keyed by spell key,
    /// NOT in the CSV — the RTK scripts charge it in their own body, where the formula extractor could never
    /// see it, which is how Hellfire came to cost a flat 1000 out of a five-figure pool. Rename a key on
    /// either side and the spell does not break: it goes back to being nearly free, and nothing says so.</para></summary>
    [Fact]
    public void EveryPostCastPoolCostNamesAKnownSpell()
    {
        EnsureLoaded();

        // Spells.csv legitimately carries the same key twice (apply_stealth), so resolve through the registry
        // rather than building a dictionary off the raw list.
        var orphans = Content.PostCastCostKeys.Where(k => Content.SpellByKey(k) is null).ToList();
        Assert.True(orphans.Count == 0,
            $"{orphans.Count} post-cast pool cost(s) name no spell in Spells.csv (that cost is now silently "
            + "not charged): " + string.Join(", ", orphans));

        var rowless = Content.PostCastCostKeys
                             .Where(k => Content.SpellByKey(k) is SpellDef sp && Content.FxFor(sp) is null)
                             .ToList();
        Assert.True(rowless.Count == 0,
            $"{rowless.Count} post-cast pool cost(s) have no spell_effects.csv row, so the cast never reaches "
            + "the archetype dispatch that applies them: " + string.Join(", ", rowless));
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

    /// <summary>NPCs.csv's <c>EraFeature</c> column reaches <c>Enabled</c>, i.e. the loader actually asks the
    /// calendar. Both halves matter and both fail silently: a dropped column leaves a 2005 NPC standing in
    /// 2001, and a gate that reads the calendar too early (before <c>EraCalendar.Reload</c>) places him by the
    /// PREVIOUS date across an <c>@reload</c>.
    ///
    /// <para>Asserted as an equality against <c>Era.Has</c> rather than a flat "Yarlof is off", so the test
    /// states the invariant and still passes at <c>EraDate=0</c> or on a deployment aimed at 2005.</para></summary>
    [Fact]
    public void EraGatedNpcsFollowTheCalendar()
    {
        EnsureLoaded();

        var gated = Content.Npcs.Where(n => n.EraFeature.Length > 0).ToList();
        Assert.NotEmpty(gated);   // the column parses at all — a header typo would empty this silently

        foreach (var n in gated)
            Assert.True(n.Enabled == Era.Has(n.EraFeature),
                $"#{n.Id} {n.Name} is Enabled={n.Enabled} but era '{n.EraFeature}' is " +
                (Era.Has(n.EraFeature) ? "present" : "absent"));

        // Yarlof by name: he is the reason the column exists, and a row that lost its key would still pass the
        // loop above (no key, no gate). Haguru stands on the same map and is 4.0-era — he must NOT be gated.
        var yarlof = Content.NpcById(34);
        Assert.NotNull(yarlof);
        Assert.Equal(Era.DruidBouquetQuest, yarlof!.EraFeature);
        Assert.Equal("", Content.NpcById(33)?.EraFeature);
    }

    /// <summary>Propose is the chapel's, and only the chapel's — you get it with the engagement ring you buy
    /// there. It is also the one spell whose data says otherwise: SpellLearnCosts.csv carries archive-sourced
    /// Mage and Poet rows for it, and a learn-cost row is precisely what puts a spell in a path leader's
    /// "Learn Secret" menu, so for a while every mage and poet could be taught it for 1000 gold and skip the
    /// ring entirely. <see cref="Content.IsNpcGrantedOnly"/> is the gate; this asserts the outcome for every
    /// class at the level cap, which is where the CSV rows would show up if the gate were dropped.</summary>
    [Fact]
    public void ProposeIsNotTaughtByPathTrainers()
    {
        EnsureLoaded();

        var propose = Content.SpellByKey("propose");
        Assert.NotNull(propose);   // still in Spells.csv at all — a renamed key would make the gate a no-op

        for (int path = 0; path <= 4; path++)
            for (int align = 0; align <= 3; align++)
                Assert.DoesNotContain(Content.SpellsForClass(path, 99, align, mark: 3),
                                      s => s.Key.Equals("propose", System.StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A rebuilt character (@lvl / @class / @mark / @align) always keeps Soothe ALONGSIDE its best
    /// self-heal — never instead of it. Soothe is the bottom rung of the Warrior/Rogue/Mage heal_self ladders,
    /// so <see cref="Content.RespecSpellSet"/>'s top-rung collapse would drop it the moment a character
    /// out-levels it; it is exempted so the first-steps heal stays in the book.</summary>
    [Fact]
    public void RespecAlwaysKeepsSootheAlongsideTheBestSelfHeal()
    {
        EnsureLoaded();

        const int warrior = 1, rogue = 2, mage = 3;

        foreach (int path in new[] { warrior, rogue, mage })
        {
            var book = Content.RespecSpellSet(path, 99, alignment: 0, mark: 0)
                              .Select(s => s.Key).ToHashSet(System.StringComparer.OrdinalIgnoreCase);
            Assert.Contains("soothe", book);   // survives the collapse for every class whose ladder holds it
        }

        // The collapse still happens: a level-99 mage keeps Soothe AND its top self-heal (Relief), but NOT
        // the middle rung (Lay Hands) it out-ranks — proving Soothe is exempted, not that the ladder stopped
        // collapsing onto one rung.
        var mageBook = Content.RespecSpellSet(mage, 99, alignment: 0, mark: 0)
                              .Select(s => s.Key).ToHashSet(System.StringComparer.OrdinalIgnoreCase);
        Assert.Contains("soothe", mageBook);
        Assert.Contains("relief_mage", mageBook);
        Assert.DoesNotContain("lay_hands_mage", mageBook);
    }

    /// <summary>A PC subpath is never eligible for Dog spells, and no class ever sees one at a tutor.
    /// <para>Two rules, both previously broken. Eligibility was tested as <c>pathId != basePath</c>, exactly
    /// inverted: it let all twelve PC subpaths (Barbarian, Monk, Druid, Muse, …) learn Dog spells while
    /// excluding the four base classes that should have them. And the spells were offered by the guild tutor,
    /// which the archived listing rules out in as many words — "The guildmaster is not involved in these
    /// spells". The class's Dog teaches them (npc_dialog.lua <c>npcs_say.DogLinguistNpc</c>), so the tutor
    /// list must stay clean no matter the level, mark or alignment.</para></summary>
    [Fact]
    public void DogSpellsAreNeverTaughtByATutorAndPcSubpathsCantHoldThem()
    {
        EnsureLoaded();

        const int mage = 3, jujak = 8, monk = 17, barbarian = 10;

        // Eligibility is base classes + NPC subpaths only.
        Assert.True(Content.CanLearnDogSpells(mage));
        Assert.True(Content.CanLearnDogSpells(jujak));        // NPC subpath (PthIcon 4)
        Assert.False(Content.CanLearnDogSpells(monk));        // PC subpath — must never qualify
        Assert.False(Content.CanLearnDogSpells(barbarian));

        // No Dog spell reaches any tutor menu, for any class, at the level/mark/alignment cap.
        var dogSpells = new[] { "greater_blessing", "spirit_fury", "spot_traps", "serpents_fury",
                                "fissure", "lava_surge", "survive", "fascinate" };
        foreach (int path in new[] { mage, jujak, monk, barbarian, 1, 2, 4 })
        foreach (int align in new[] { 0, 1, 2, 3 })
        {
            var offered = Content.SpellsForClass(path, 99, align, mark: 3).Select(s => s.Key).ToHashSet(
                System.StringComparer.OrdinalIgnoreCase);
            foreach (var key in dogSpells)
                Assert.False(offered.Contains(key), $"tutor offered Dog spell '{key}' to path {path} (alignment {align})");
        }

        // Volcanic Blast is NOT a Dog spell — it is an ordinary Il san mage secret with a SpellLearnCosts
        // row, so it stays buyable at the guild and keeps its own mark-1 gate. It was wrongly bundled with
        // the Dog set; removing it must not make it unreachable.
        static bool HasVolcanic(int mark) =>
            Content.SpellsForClass(mage, 99, 0, mark: mark)
                   .Any(s => s.Key.Equals("volcanic_blast_mage", System.StringComparison.OrdinalIgnoreCase));
        Assert.True(HasVolcanic(mark: 1));
        Assert.False(HasVolcanic(mark: 0));
    }

    /// <summary>The character rebuild (@lvl / @class / @mark / @align) hands a finished Dog Linguist the Dog
    /// spells its class and level entitle it to — the flag alone is not enough, and neither is the level.
    /// <para>This is what "@dog 1" then "@lvl 99" is supposed to produce. It used not to: Dog spells carry
    /// <c>SplPthId</c> 99 so that no tutor can reach them, which also meant <see cref="Content.SpellsForClass"/>
    /// never returned one, so <see cref="Content.RespecSpellSet"/> never granted one — and because a rebuild
    /// REPLACES the book rather than topping it up, every rebuild silently forgot Dog spells that had been
    /// earned honestly at the Dog as well.</para></summary>
    [Fact]
    public void RebuildGrantsDogSpellsOnlyToAFlaggedLinguistAtTheRightLevel()
    {
        EnsureLoaded();

        const int warrior = 1, rogue = 2, mage = 3, poet = 4, jujak = 8, monk = 17;

        static System.Collections.Generic.HashSet<string> Book(int path, int level, bool dog) =>
            Content.RespecSpellSet(path, level, alignment: 0, mark: 0, dogFlag: dog)
                   .Select(s => s.Key).ToHashSet(System.StringComparer.OrdinalIgnoreCase);

        // The four base classes, each with its own pair — and nobody else's.
        var pairs = new[] { (warrior, "greater_blessing", "spirit_fury"),
                            (rogue,   "spot_traps",       "serpents_fury"),
                            (mage,    "fissure",          "lava_surge"),
                            (poet,    "survive",          "fascinate") };
        foreach (var (path, low, high) in pairs)
        {
            Assert.Contains(low,  Book(path, 99, dog: true));
            Assert.Contains(high, Book(path, 99, dog: true));

            // No flag, no Dog spells — the linguist chain is the gate, at every level.
            Assert.DoesNotContain(low,  Book(path, 99, dog: false));
            Assert.DoesNotContain(high, Book(path, 99, dog: false));

            // Level still gates each tier independently: 70 gets the first, not the second.
            Assert.Contains(low,          Book(path, 70, dog: true));
            Assert.DoesNotContain(high,   Book(path, 70, dog: true));
            Assert.DoesNotContain(low,    Book(path, 69, dog: true));

            // A class only ever gets its OWN pair, never another class's.
            foreach (var (other, otherLow, otherHigh) in pairs)
            {
                if (other == path) continue;
                Assert.DoesNotContain(otherLow,  Book(path, 99, dog: true));
                Assert.DoesNotContain(otherHigh, Book(path, 99, dog: true));
            }
        }

        // An NPC subpath reads its BASE class's pair (Ju jak is a Mage); a PC subpath is refused outright,
        // flag or no flag — Content.CanLearnDogSpells, the same rule the Dog applies in person.
        Assert.Contains("fissure",       Book(jujak, 99, dog: true));
        Assert.Contains("lava_surge",    Book(jujak, 99, dog: true));
        Assert.DoesNotContain("fissure", Book(monk, 99, dog: true));

        // Peasant has no Dog, and no pair to inherit.
        Assert.Empty(Content.DogSpellsFor(0, 99));
    }

    /// <summary>Every key in the Dog-spell tier table names a real spell, and the table agrees with
    /// npc_dialog.lua's <c>DOG_SPELLS</c> — the two halves of one dataset (C# owns the pairing and the levels
    /// the rebuild grants at, the Lua owns the kills and goods the Dog charges). A typo on either side is
    /// silent: the rebuild would just skip the spell.</summary>
    [Fact]
    public void DogSpellTiersAreReal()
    {
        EnsureLoaded();

        var expected = new[] { "greater_blessing", "spirit_fury", "spot_traps", "serpents_fury",
                               "fissure", "lava_surge", "survive", "fascinate" };
        var granted = new[] { 1, 2, 3, 4 }.SelectMany(p => Content.DogSpellsFor(p, 99))
                                          .Select(s => s.Key)
                                          .ToHashSet(System.StringComparer.OrdinalIgnoreCase);

        Assert.Equal(expected.Length, granted.Count);
        foreach (var key in expected)
        {
            Assert.NotNull(Content.SpellByKey(key));   // still in Spells.csv under the ===DOG SPELLS=== divider
            Assert.Contains(key, granted);
        }

        // Two per class, at 70 and 99 — the atlas levels, and what the level gate above depends on.
        foreach (int path in new[] { 1, 2, 3, 4 })
        {
            var levels = Content.DogSpellsFor(path, 99).Select(s => s.Level).OrderBy(l => l).ToArray();
            Assert.Equal(new[] { 70, 99 }, levels);
        }
    }

    /// <summary>Every key in a shop's buys-from list is a real item. A typo'd or 7.x-only key silently
    /// narrows the accept list instead of failing, and the symptom — "the smith won't buy my ore" — looks like
    /// a shop bug rather than a data one. Also pins the butcher, whose over-broad buying is why these lists
    /// exist: she takes meat, not platemail.</summary>
    [Fact]
    public void ShopBuysFromListsNameRealItems()
    {
        EnsureLoaded();

        Assert.NotEmpty(Content.ShopBuysFrom);
        foreach (var (npc, keys) in Content.ShopBuysFrom)
            foreach (var key in keys)
                Assert.True(Content.ItemByKey(key) is not null, $"{npc} buys unknown item '{key}'");

        var butcher = Shops.BuysFrom("ButcherNpc");
        Assert.NotNull(butcher);
        Assert.Contains("rabbit_meat", butcher!);
        Assert.DoesNotContain("war_platemail", butcher);
        Assert.DoesNotContain("steel_sword", butcher);

        // "Buys nothing" and "no list, buys anything" are opposite answers that both look like an empty
        // list — the chapel's "-" row must survive the loader as an EMPTY set, not as a missing key.
        var chapel = Shops.BuysFrom("ChapelNpc");
        Assert.NotNull(chapel);
        Assert.Empty(chapel!);
        Assert.Null(Shops.BuysFrom("NoSuchNpc"));

        // The Arctic butcher is ours, not RTK's, so her list is an alias of ButcherNpc's in the extractor —
        // a mapping nothing else would notice going stale (a missing row reads as "buys anything").
        Assert.Equal(butcher, Shops.BuysFrom("TaimyrNpc"));
    }

    /// <summary>The Dog Linguist chain is wired end to end (npc_dialog.lua <c>npcs_say.DogLinguistNpc</c>).
    ///
    /// <para>Every link fails SILENTLY. The four dogs share ONE identifier and the handler tells them apart by
    /// display NAME, so renaming one in NPCs.csv makes that dog deaf with nothing logged. A requirement naming
    /// an item or monster key that doesn't exist gives you a dog asking for something that cannot be obtained
    /// — <c>hasItem</c> and <c>killCount</c> both just answer "no" forever. And the eight spells the dogs
    /// teach have to resolve, or <c>learnSpell</c> returns false after the components are already gone.</para>
    ///
    /// <para>The requirement keys are duplicated from the Lua here on purpose: this test is the thing that
    /// notices when one side is edited and the other isn't.</para></summary>
    [Fact]
    public void DogLinguistChainIsWiredEndToEnd()
    {
        EnsureLoaded();

        // The four dogs, by the exact NpcDescription the Lua DOGS table keys on.
        var dogs = Content.Npcs.Where(n => n.Key == "DogLinguistNpc").Select(n => n.Name).ToHashSet();
        foreach (var name in new[] { "Mutt", "Jindo dog", "Hunting dog", "Spotted dog" })
            Assert.True(dogs.Contains(name), $"no DogLinguistNpc named '{name}' in NPCs.csv — that dog goes deaf");

        // …and the speech handler they all route through is actually registered in the loaded Lua.
        Assert.True(NpcScript.HasSay("DogLinguistNpc"), "npc_dialog.lua registered no npcs_say.DogLinguistNpc");

        // The eight taught spells resolve by key.
        foreach (var key in new[] { "greater_blessing", "spirit_fury", "spot_traps", "serpents_fury",
                                    "fissure", "lava_surge", "survive", "fascinate" })
            Assert.True(Content.SpellByKey(key) is not null, $"Dog spell '{key}' is not in Spells.csv");

        // Everything the dogs ask you to bring…
        foreach (var key in new[] { "ambrosia", "whisper_bracelet", "amber", "amethyst", "quartz", "topaz",
                                    "star_staff", "scribes_pen", "mountain_ginseng", "pearl_charm",
                                    "titanium_lance", "purified_water" })
            Assert.True(Content.ItemByKey(key) is not null, $"Dog quest asks for unknown item '{key}'");

        // …and everything they ask you to kill.
        foreach (var key in new[] { "trapdoor_spider", "spirit_rabbit", "zinte_ogre", "zangze_ogre", "ice_panther" })
            Assert.True(Content.MobByKey(key) is not null, $"Dog quest asks you to slay unknown mob '{key}'");
    }

    /// <summary>The Old dog's Poet's Restore quest, which the Dog Linguist legend gates but which is NOT a
    /// Dog spell — any poet-derived path may complete it (Poet, Hyun moo, Druid, Monk, Muse), so it must NOT
    /// be gated on <see cref="Content.CanLearnDogSpells"/>, which exists to keep PC subpaths out of the eight
    /// Dog spells and would wrongly refuse a Druid or a Muse here.
    ///
    /// <para>Restore's heal also has to be a real number. Its <c>amountExpr</c> was empty, which the Heal
    /// archetype evaluates to zero — a fifty-million-experience quest whose reward healed nobody.</para></summary>
    [Fact]
    public void PoetsRestoreQuestIsWiredForEveryPoetPath()
    {
        EnsureLoaded();

        var oldDog = Content.Npcs.FirstOrDefault(n => n.Key == "OldDogNpc");
        Assert.NotNull(oldDog);
        Assert.True(NpcScript.Has("OldDogNpc"), "npc_dialog.lua registered no npcs.OldDogNpc click handler");
        Assert.True(Content.MobByKey("tiger_storm") is not null, "Storm is missing from mobs.csv");

        // Every poet-derived path shares base path 4 — the gate the handler actually uses. Three of these are
        // PC subpaths that CanLearnDogSpells deliberately refuses, which is exactly why it is the wrong test.
        foreach (int path in new[] { 4, 9, 13, 17, 21 })   // Poet · Hyun moo · Druid · Monk · Muse
            Assert.Equal(4, Content.PathBaseOf(path));
        Assert.False(Content.CanLearnDogSpells(13), "Druid is a PC subpath — it may not learn Dog spells…");
        Assert.False(Content.CanLearnDogSpells(21), "…nor may Muse; but both may still earn Restore");

        // Restore heals 150% of the caster's current mana (atlas poet listing) — a real formula, not blank.
        var fx = Content.FxFor(Content.SpellByKey("restore_poet")!);
        Assert.NotNull(fx);
        Assert.False(string.IsNullOrWhiteSpace(fx!.AmountExpr), "restore_poet has no heal formula — it heals 0");
        Assert.Equal(1500, Formula.Eval(fx.AmountExpr, new Dictionary<string, double> { ["player.magic"] = 1000 }),
                     precision: 3);
    }

    /// <summary>The Sage and his wisdom ladder — five ways this one goes silently wrong.
    ///
    /// <para><b>He must stand somewhere reachable.</b> He shipped at (4,6) on map 1230, which is
    /// <c>pass=3</c> — solid rock, walled off from the room's only doorway. An NPC in a wall draws fine,
    /// answers nothing, and nothing at load time objects.</para>
    ///
    /// <para><b>He must own his own dialog.</b> Without a Lua click handler he falls through to the C#
    /// abilities, and with no abilities and no quests that is the bare "Greetings, traveller." — which is
    /// exactly what he did before the ladder was written.</para>
    ///
    /// <para><b>Nobody else may sell rung 1.</b> The archive is explicit that the Sage is the only teacher
    /// ("The Share wisdom spells can be learned from 'The Sage'"), but SpellLearnCosts.csv carries
    /// share_wisdom rows for Warrior/Mage/Poet at level 90 — and a SpellCosts row is all it takes for
    /// SpellsForClass to put a spell in a path trainer's menu. <see cref="Content.IsNpcGrantedOnly"/> is the
    /// gate; that CSV is generated, so the rows can come back and this must hold when they do.</para>
    ///
    /// <para><b>The rungs must be real, and must cast.</b> Each of the five needs a Spells.csv row AND a
    /// SpellParams row on the <c>sage_shout</c> verb — without the row the cast falls through to the
    /// Utility catch-all, which takes the mana and does nothing.</para>
    ///
    /// <para><b>The map must never lock out someone he would teach.</b> Entry to map 1230 is deliberately
    /// LOWER than the ladder's own gate — the Atlas says "Only Level 90+ may enter this location", but we
    /// let anyone in from level 50 (RTK's own figure) so a player can find the Sage, hear the requirement
    /// and come back for it, rather than meeting a wall with no explanation. That gap is a choice; the
    /// direction is not. If <c>MapReqLvl</c> ever rose ABOVE <c>SAGE_LEVEL</c>, qualifying players could not
    /// reach the only NPC who sells the spell, and nothing else would notice.</para></summary>
    [Fact]
    public void TheSageIsReachableAndOwnsTheWholeWisdomLadder()
    {
        EnsureLoaded();

        var sage = Content.Npcs.FirstOrDefault(n => n.Key == "SageNpc");
        Assert.NotNull(sage);
        Assert.True(NpcScript.Has("SageNpc"), "npc_dialog.lua registered no npcs.SageNpc click handler");

        // Standing on ground a player can walk to — not inside the room's wall.
        var room = MapData.For(sage!.Map);
        Assert.NotNull(room);
        Assert.False(room!.Solid(sage.X, sage.Y),
            $"the Sage stands on solid ground at ({sage.X},{sage.Y}) on map {sage.Map} — unreachable");

        // The ladder, in order, with the archive's aether for each rung — 15/10/10/5/5 minutes, agreed by
        // the dated Atlas sage page, the Atlas 6.5 map page, the tutor board and tswolf. RTK's sage.lua ran
        // 15/10/5/2.5/1, which made the top rung five times faster than retail; these are the numbers that
        // a re-merge from RTK would quietly undo, so they are asserted rather than trusted.
        var ladder = new[]
        {
            (key: "share_wisdom",       aetherMs: 900_000),
            (key: "mentors_wisdom",     aetherMs: 600_000),
            (key: "apprentices_wisdom", aetherMs: 600_000),
            (key: "adepts_wisdom",      aetherMs: 300_000),
            (key: "sages_wisdom",       aetherMs: 300_000),
        };

        // Content.SageLadder is the ONE definition — the Lua copies, the trainer gate, the rebuild's re-grant
        // and @sage all read it. If this array and that one drift, everything below is testing a fiction.
        Assert.Equal(Content.SageLadder, ladder.Select(l => l.key).ToArray());

        foreach (var (key, aetherMs) in ladder)
        {
            Assert.NotNull(Content.SpellByKey(key));                    // still in Spells.csv
            Assert.True(Content.SpellParams.TryGetValue(key, out var row),
                $"'{key}' has no SpellParams row — the cast would fall through to the Utility catch-all");
            Assert.Equal("sage_shout", row!.GetValueOrDefault("verb", ""));
            Assert.Equal(aetherMs.ToString(), row.GetValueOrDefault("duration", ""));
            // tswolf prices Share Wisdom at 600 mana and no source varies it per rung — the upgrade buys
            // aether and nothing else. RTK's ascending 10/50/100/250/300 made rung 1 sixty times too cheap.
            Assert.Equal("600", row.GetValueOrDefault("mana", ""));
            Assert.True(Content.IsNpcGrantedOnly(Content.SpellByKey(key)!),
                $"'{key}' is not gated to the Sage — a path trainer can teach it");
        }

        // The aether must SHRINK as you climb (never grow) — that ordering is the whole product the Sage
        // sells, and it is the shape any future retune has to keep.
        for (int i = 1; i < ladder.Length; i++)
            Assert.True(ladder[i].aetherMs <= ladder[i - 1].aetherMs, "the ladder must never lengthen an aether");

        // …and no trainer menu shows one, at any path, alignment or rank.
        foreach (int path in new[] { 1, 2, 3, 4, 8, 13 })
        foreach (int align in new[] { 0, 1, 2, 3 })
        {
            var offered = Content.SpellsForClass(path, 99, align, mark: 3).Select(s => s.Key)
                                 .ToHashSet(System.StringComparer.OrdinalIgnoreCase);
            foreach (var (key, _) in ladder)
                Assert.False(offered.Contains(key), $"a path-{path} trainer offered the Sage spell '{key}'");
        }

        // The Lua owns the ladder the NPC actually walks; a rename on one side and not the other is silent.
        var lua = System.IO.File.ReadAllText(
            System.IO.Path.Combine(Shared.RepoPaths.GameDataDir(), "npc_dialog.lua"));
        var table = System.Text.RegularExpressions.Regex.Match(lua, @"SAGE_LADDER\s*=\s*\{(.*?)\}",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(table.Success, "npc_dialog.lua no longer defines SAGE_LADDER");
        foreach (var (key, _) in ladder)
            Assert.Contains(key, table.Groups[1].Value);

        // A CHARACTER REBUILD MUST HAND THE RUNG BACK. @lvl/@class/@mark/@align wipe the book and refill it
        // from RespecSpellSet, and the five rungs are deliberately invisible to SpellsForClass — so without
        // the registry re-grant, one @lvl confiscated a 500,000-gold ladder that no trainer can resell. That
        // is not hypothetical: it is what happened the first time anyone ran @lvl 99 after the Sage shipped.
        foreach (int path in new[] { 1, 2, 3, 4 })
        {
            var rebuilt = Content.RespecSpellSet(path, 99, alignment: 0, mark: 0, dogFlag: false, sageRung: 3)
                                 .Select(s => s.Key).ToHashSet(System.StringComparer.OrdinalIgnoreCase);
            Assert.Contains("apprentices_wisdom", rebuilt);             // the rung on record, and only it
            Assert.DoesNotContain("share_wisdom", rebuilt);
            Assert.DoesNotContain("sages_wisdom", rebuilt);
        }

        // …but only what was paid for, and only once the level qualifies — the same shape as the Dog tiers.
        Assert.DoesNotContain(Content.RespecSpellSet(1, 99, 0, 0, false, sageRung: 0).Select(s => s.Key),
                              k => Content.SageRungOf(k) > 0);
        Assert.DoesNotContain(Content.RespecSpellSet(1, Content.SageLevel - 1, 0, 0, false, sageRung: 5).Select(s => s.Key),
                              k => Content.SageRungOf(k) > 0);
        Assert.Null(Content.SageSpellFor(3, Content.SageLevel - 1));    // too low: nothing to grant
        Assert.NotNull(Content.SageSpellFor(3, Content.SageLevel));
        Assert.Null(Content.SageSpellFor(0, 99));                       // holds none
        Assert.Null(Content.SageSpellFor(99, 99));                      // out of range

        // The upgrade wait: 2 yuris. The tutor post's "(Updated)" revision says 45 days and the dated Atlas
        // page says 90; the dated one won, and a silent flip back would only show up 45 days into a save.
        var wait = System.Text.RegularExpressions.Regex.Match(lua, @"SAGE_WAIT\s*=\s*(\d+)\s*\*\s*86400");
        Assert.True(wait.Success, "npc_dialog.lua no longer defines SAGE_WAIT in days");
        Assert.Equal(90, int.Parse(wait.Groups[1].Value));

        // Both halves of the level gate. They are allowed to differ — see the note above — but only one way
        // round: the door may be more generous than the Sage, never stricter.
        var level = System.Text.RegularExpressions.Regex.Match(lua, @"SAGE_LEVEL\s*=\s*(\d+)");
        Assert.True(level.Success, "npc_dialog.lua no longer defines SAGE_LEVEL");
        Assert.Equal(90, int.Parse(level.Groups[1].Value));
        Assert.True(Content.MapMeta.TryGetValue(sage.Map, out var meta), $"map {sage.Map} has no Maps.csv row");
        Assert.True(meta!.ReqLvl <= int.Parse(level.Groups[1].Value),
            $"map {sage.Map} admits at {meta.ReqLvl} but the Sage teaches at {level.Groups[1].Value} — " +
            "a qualifying player cannot reach the only NPC who sells the spell");
    }

    /// <summary>Where each sage rung reaches — the half of the ladder that is not the NPC.
    ///
    /// <para>Only the top rung sages everywhere. Below it the spell works in the "4.0 designated areas"
    /// (<see cref="Content.IsSageArea"/>) and, from rung 3, in the caster's own kingdom
    /// (<see cref="Content.IsOwnKingdom"/>); anywhere else it becomes the Mentor spell and still burns the
    /// aether. Region 2 carries the whole designated set — tswolf's "Mythic, Wilderness, and Kamings
    /// Encampment" — so this pins the four maps that prove it, and the three capitals that must NOT qualify.
    /// If a region were ever renumbered, every rung below 5 would silently start sagaing from the capitals
    /// (or stop sagaing at all), and nothing else in the server would notice.</para>
    ///
    /// <para>The nation-to-region hop is the subtle one: nation ids (Neutral 0 · Koguryo 1 · Buya 2 ·
    /// Nagnang 3) are NOT map-region ids (Kugnae 0 · Buya 1 · Mythic 2 · Nagnang 3), so an off-by-one here
    /// hands a Koguryan sage rights over Buya. A neutral caster must never get a home kingdom — that is what
    /// makes their rungs 3-4 behave as rung 2, per the tutor board.</para></summary>
    [Fact]
    public void SageRungsReachTheMythicAndOnlyTheirOwnKingdom()
    {
        EnsureLoaded();

        // The designated areas: the Mythic region, and the carnage/event maps named alongside it.
        Assert.True(Content.IsSageArea(41),   "Mythic Nexus must be a sage area");
        Assert.True(Content.IsSageArea(1002), "the Wilderness must be a sage area");
        Assert.True(Content.IsSageArea(3800), "KaMing's Encampment must be a sage area");
        Assert.True(Content.IsSageArea(3010), "Carnage Hall must be a sage area");

        // The three capitals are NOT. This is the whole point of the ladder: from town, rungs 1-2 are Mentor.
        Assert.False(Content.IsSageArea(0),    "Kugnae must not be a sage area");
        Assert.False(Content.IsSageArea(330),  "Buya must not be a sage area");
        Assert.False(Content.IsSageArea(2500), "Nagnang must not be a sage area");

        // Own kingdom: nation id -> map region, and each capital answers to exactly one nation.
        Assert.True(Content.IsOwnKingdom(0, 1),    "a Koguryan is at home in Kugnae");
        Assert.True(Content.IsOwnKingdom(330, 2),  "a Buyan is at home in Buya");
        Assert.True(Content.IsOwnKingdom(2500, 3), "a Nagnanger is at home in Nagnang");
        Assert.False(Content.IsOwnKingdom(330, 1), "a Koguryan is NOT at home in Buya");
        Assert.False(Content.IsOwnKingdom(0, 2),   "a Buyan is NOT at home in Kugnae");
        foreach (var map in new ushort[] { 0, 330, 2500 })
            Assert.False(Content.IsOwnKingdom(map, 0), "a neutral villager has no home kingdom to sage from");

        // The verb still exists and still owns the per-rung policy, with all five rungs in its table.
        Assert.True(SpellScript.HasVerb("sage_shout"), "spell_verbs.lua lost sage_shout");
        var lua = System.IO.File.ReadAllText(
            System.IO.Path.Combine(Shared.RepoPaths.GameDataDir(), "spell_verbs.lua"));
        var rungs = System.Text.RegularExpressions.Regex.Match(lua, @"SAGE_RUNGS\s*=\s*\{(.*?)\}",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(rungs.Success, "spell_verbs.lua no longer defines SAGE_RUNGS");
        for (int i = 0; i < Content.SageLadder.Length; i++)
            Assert.Matches($@"{Content.SageLadder[i]}\s*=\s*{i + 1}\b", rungs.Groups[1].Value);

        // The fallback must be Mentor, and the charge must sit outside the branch — a refactor that moved
        // debitMana/setCooldown into the reaching branch would make casting from town free, which the Atlas
        // explicitly refused to do ("Won't be changed. Was written to be like that").
        var verb = System.Text.RegularExpressions.Regex.Match(lua,
            @"function verbs\.sage_shout.*?\nend", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(verb.Success, "could not find verbs.sage_shout to check its charge path");
        Assert.Contains("ctx:mentor()", verb.Value);
    }

    /// <summary>The world shout must not go out on the login box.
    ///
    /// <para><c>SendMessage</c> is RTK's <c>0x02</c> pre-world login-box packet; in-world text is <c>0x0A</c>
    /// (<c>clif_sendmsg</c>, dispatched by type). A 2026-07-26 audit swept that mislabeling out of the whole
    /// server, but the Sage's world channel was written afterwards and went out on <c>0x02</c> — so on 5.33
    /// the shout sent, logged, and drew NOTHING, because no in-world widget listens there.</para>
    ///
    /// <para>This is asserted against the SOURCE because it cannot be caught any other way: both calls
    /// compile, both log, and the difference only exists on a real client. That is precisely the failure
    /// mode that let it ship.</para></summary>
    [Fact]
    public void TheWorldShoutRidesTheInWorldTextChannel()
    {
        var src = System.IO.File.ReadAllText(RepoFile("Server/Session.Spells.cs"));
        var fn = System.Text.RegularExpressions.Regex.Match(src,
            @"internal bool LuaWorldShout.*?
    \}", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(fn.Success, "could not find LuaWorldShout to check its channel");
        Assert.Contains("SendMiniText", fn.Value);
        Assert.DoesNotContain("SendMessage", fn.Value);
    }

    /// <summary>Walk up from the repo root so the check above reads the real source however tests are hosted.</summary>
    private static string RepoFile(string relative)
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Project1998.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return System.IO.Path.Combine(dir!.FullName, relative);
    }

    /// <summary>Blood's two halves, both of which fail silently when wrong.
    ///
    /// <para>His CLICK must be a menu. Without a Lua click handler he falls through to the C# abilities, and
    /// a lone entry there dives straight into its service (Session.RunNpcAsync) — which for Blood meant a
    /// click opened the "which secret do you wish to forget?" picker cold.</para>
    ///
    /// <para>And the Frost sabre he forges must be <see cref="ItemDef.Bonded"/> ("only YOU will be able to
    /// wield your Frost sabre") but NOT <see cref="ItemDef.Unrepairable"/> ("it can be repaired with ease") —
    /// the two used to be one flag, so adding the sabre to the bonded set would otherwise have quietly made
    /// the repair spell refuse it.</para></summary>
    [Fact]
    public void BloodOpensAMenuAndForgesABondedFrostSabre()
    {
        EnsureLoaded();

        Assert.NotNull(Content.Npcs.FirstOrDefault(n => n.Key == "BloodNpc"));
        Assert.True(NpcScript.Has("BloodNpc"), "npc_dialog.lua registered no npcs.BloodNpc click handler");
        Assert.True(NpcScript.HasSay("BloodNpc"), "npc_dialog.lua registered no npcs_say.BloodNpc (the 'ice beast' branch)");

        var sabre = Content.ItemByKey("frost_sabre");
        Assert.NotNull(sabre);
        Assert.True(sabre!.Bonded, "the Frost sabre is forged for one person — it must bind to its owner");
        Assert.False(sabre.Unrepairable, "Blood says the Frost sabre repairs with ease — it must stay repairable");
    }

    /// <summary>Bonded · break-on-death · unrepairable are THREE independent properties, and every time one of
    /// them has been derived from another the overlap has come out wrong. They are sourced differently on
    /// purpose: ItmBoD and ItmRepairable are columns on the item row, while bonding is a property of the GRANT
    /// and comes from the Nexus Atlas's per-item "Special Info" field. A rule that swept a whole id RANGE into
    /// the bonded set is what last made every break-on-death subpath weapon look bound to its holder.
    ///
    /// <para>The Charm family alone carries all three cases, which is why it is the fixture here: the base
    /// tier is a boss drop that breaks on death, the san tiers are NPC-forged and do both, and Enchanted charm
    /// is the one the Atlas marks Non-Bonded.</para></summary>
    [Fact]
    public void BondedBreakOnDeathAndUnrepairableAreIndependent()
    {
        EnsureLoaded();

        // Break-on-death but NOT bonded: Spike drops off mobs (MobDrops.lua) and Gan sells it over a counter
        // (kaming/gan.lua) — nothing you can buy in a shop arrives bound to you. Same for the other three
        // base-tier subpath weapons, all of them "Break on Death" and nothing else on the Atlas.
        foreach (var key in new[] { "spike", "blood", "surge", "charm" })
        {
            var w = Content.ItemByKey(key);
            Assert.NotNull(w);
            Assert.True(w!.BreakOnDeath, $"{key} is ItmBoD");
            Assert.False(w.Bonded, $"{key} is a boss drop / shop stock — it binds to nobody");
        }

        // Bonded but NOT break-on-death: the smith forges the Nimble blade for one person (smith.lua class 7).
        var nimble = Content.ItemByKey("nimble_blade");
        Assert.NotNull(nimble);
        Assert.True(nimble!.Bonded, "the smith forges the Nimble blade FOR you — it must bind");
        Assert.False(nimble.BreakOnDeath, "the Nimble blade is not ItmBoD");

        // Both: the san tiers an NPC upgrades for you — the Atlas reads "Bonded / Break on Death".
        var ilCharm = Content.ItemByKey("il_san_charm");
        Assert.NotNull(ilCharm);
        Assert.True(ilCharm!.Bonded && ilCharm.BreakOnDeath, "Il san charm is NPC-forged AND ItmBoD");

        // The per-INSTANCE case must NOT be a def-level bond: the Frozen spear and Giasomo stick rows are
        // plain break-on-death drops, and only the copies Laptev sells arrive owned (user, 2026-08-22).
        foreach (var key in new[] { "frozen_spear", "giasomo_stick" })
        {
            var w = Content.ItemByKey(key);
            Assert.NotNull(w);
            Assert.True(w!.BreakOnDeath, $"{key} is ItmBoD");
            Assert.False(w.Bonded, $"{key} binds per-instance at Laptev's counter, not by item id");
        }

        // And neither flag may imply the other anywhere in the registry — the failure mode this guards is one
        // set becoming a subset of the other, which reads as "correct" on any single item you happen to test.
        var bonded = Content.Items.Where(i => i.Bonded).ToList();
        var bod    = Content.Items.Where(i => i.BreakOnDeath).ToList();
        Assert.NotEmpty(bonded);
        Assert.NotEmpty(bod);
        Assert.Contains(bonded, i => !i.BreakOnDeath);
        Assert.Contains(bod,    i => !i.Bonded);
        Assert.Contains(bonded, i => i.BreakOnDeath);          // the overlap is real, just not total

        // Unrepairable rides on ItmRepairable alone, so it cuts across both: bound gear that still repairs
        // (the Frost sabre, the whole armory shield ladder) and unrepairable gear nobody owns (the headbands).
        Assert.Contains(bonded, i => !i.Unrepairable);
        Assert.Contains(bonded, i => i.Unrepairable);           // the totem helms: "Bonded / Unrepairable"
        Assert.Contains(Content.Items, i => i.Unrepairable && !i.Bonded && !i.BreakOnDeath);
    }

    /// <summary>The Leviathan chain is wired end to end (see Server/LeviathanQuest.cs). Every link here is
    /// data that fails SILENTLY: a composition row naming an ability nothing registers is skipped with a log
    /// line, a stock key that doesn't resolve is dropped from the grid, a captive that wanders off its spawn
    /// tile leaves the release tile with nothing to find, and none of it throws.</summary>
    [Fact]
    public void LeviathanQuestIsWiredEndToEnd()
    {
        EnsureLoaded();

        // The two NPCs resolve to their abilities THROUGH NpcAbilities.csv, so this covers the CSV row, the
        // name registration in NpcScripts, and the ability itself.
        var hermit = Content.Npcs.FirstOrDefault(n => n.Key == "HermitNpc");
        var daeWhan = Content.Npcs.FirstOrDefault(n => n.Key == "AncientLeviathanNpc");
        Assert.NotNull(hermit);
        Assert.NotNull(daeWhan);
        Assert.Contains(NpcScripts.For(hermit!), a => a is HermitAbility);
        Assert.Contains(NpcScripts.For(daeWhan!), a => a is AncientLeviathanAbility);

        // His stock: RTK's three cursed weapons plus the sixteen class fans, one per class per tier.
        var keys = (Shops.For("HermitNpc") ?? System.Array.Empty<Shops.Category>()).SelectMany(c => c.Keys).ToList();
        foreach (var key in new[] { "tainted_blade", "tainted_staff", "tainted_ring" })
            Assert.Contains(key, keys);
        var fans = keys.Select(Content.ItemByKey).OfType<ItemDef>()
                       .Where(d => d.Name.EndsWith("fan", System.StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(16, fans.Count);
        foreach (var path in new[] { 1, 2, 3, 4 })
            Assert.Equal(new[] { 30, 50, 75, 99 }, fans.Where(f => f.PathId == path).Select(f => (int)f.Level).OrderBy(l => l));

        // The talisman, and the four captives penned where the release tile looks for them.
        Assert.NotNull(Content.ItemByKey(LeviathanQuest.Talisman));
        var captive = Content.MobByKey(LeviathanQuest.CaptiveMob);
        Assert.NotNull(captive);
        Assert.True(captive!.Stationary, "a captive that wanders leaves the release tile with nothing to free");

        var pens = Content.Spawns.Where(s => s.Map == LeviathanQuest.PenMap && s.MobId == captive.Id).ToList();
        Assert.Equal(LeviathanQuest.PenX.Length, pens.Count);
        foreach (var pen in pens)
        {
            Assert.Contains(pen.X, LeviathanQuest.PenX);
            Assert.Equal(LeviathanQuest.PenCaptiveY, pen.Y);
        }

        // Both ends of the hut door have to be renderable or the door strands the player mid-quest.
        Assert.True(Content.TryMap(LeviathanQuest.DoorMap, out _));
        Assert.True(Content.TryMap(LeviathanQuest.HutMap, out _));
    }

    /// <summary>The mob-AI data layer resolves. Every one of these fails silently in production: a spell row
    /// naming a mob that doesn't exist never fires, an unknown effect string logs once and does nothing, a
    /// boss room on an unrenderable map leaves the boss where it started, and a chatter row for a missing
    /// creature is simply never spoken. None of it throws, so nothing else would ever tell us.</summary>
    [Fact]
    public void MobAiDataResolves()
    {
        EnsureLoaded();

        // "melee" is a bonus swing with the creature's own weapon rather than a spell (Gim Yi's ambush).
        var effects = new[] { "damage", "poison", "curse", "blind", "melee" };
        Assert.NotEmpty(Content.MobSpells);
        foreach (var (key, spells) in Content.MobSpells)
        {
            Assert.True(Content.MobByKey(key) is not null, $"MobSpells.csv names unknown mob '{key}'");
            foreach (var sp in spells)
            {
                Assert.Contains(sp.Effect, effects);
                Assert.True(sp.Chance >= 1, $"{key}/{sp.Name} would cast on every single tick");
                Assert.True(sp.Range >= 1, $"{key}/{sp.Name} has no reach");
                // A status with no duration is a no-op that still costs the mob its cast slot.
                if (sp.Effect is "curse" or "blind" or "poison") Assert.True(sp.DurationMs > 0, $"{key}/{sp.Name} has no duration");
                // A melee row is a bonus swing with the creature's own weapon, so a fixed Amount on it would
                // be silently ignored — that would read as a damage value that does nothing.
                if (sp.Effect == "melee") Assert.Equal(0, sp.Amount);
            }
        }

        foreach (var (key, chat) in Content.MobChatter)
        {
            Assert.True(Content.MobByKey(key) is not null, $"MobChatter.csv names unknown mob '{key}'");
            Assert.NotEmpty(chat.Lines);
        }

        foreach (var (key, boss) in Content.MobBosses)
        {
            Assert.True(Content.MobByKey(key) is not null, $"MobBosses.csv names unknown mob '{key}'");
            Assert.True(boss.HealChance >= 2, $"{key} would never take a killing blow at all");
        }

        foreach (var (key, rule) in Content.MobSpawnRules)
        {
            Assert.True(Content.MobByKey(key) is not null, $"MobSpawnRules.csv names unknown mob '{key}'");
            foreach (var room in rule.Rooms)
                Assert.True(Content.TryMap(room.Map, out _), $"{key} has a room on unrenderable map {room.Map}");
            // A rout threshold at or above 100% would have the creature bolt the moment it spawns, and one at
            // 0 is the "no rout" default — anything in between is a real fraction of its health.
            Assert.InRange(rule.FleeBelowPct, 0, 99);
        }

        // RTK's three routing bosses (nine_tailed_fox.lua, ogre_maletic.lua's two tables). These break off the
        // fight for good below 15% and cannot be finished off toe to toe — losing the rows turns three chase
        // fights back into ordinary punch-ups without anything failing.
        foreach (var key in new[] { "nine_tailed_fox", "ogre_maletic", "ogre_citelam" })
            Assert.Equal(15, Content.MobSpawnRules[key].FleeBelowPct);

        // Citelam and Maletic are gated finds, not fixtures (RTK NPCs/trap/mob_spawn.lua): one at a time, a
        // 1-in-10 roll, and never inside half an hour of the last one dying. All three gates AND a spawn point
        // to hang them on — without the trap row the rules describe a creature that never appears, which is
        // exactly the state this pair was in.
        foreach (var (key, map, id) in new[] { ("ogre_citelam", 136, 153), ("ogre_maletic", 140, 154) })
        {
            var rule = Content.MobSpawnRules[key];
            Assert.Equal(1, rule.MaxAlive);
            Assert.Equal(10, rule.SpawnChance);
            Assert.Equal(1800, rule.DeathCooldownSec);
            Assert.True(Content.AreaSpawns.Any(a => a.Map == map && a.MobId == id && a.RespawnSec > 0),
                        $"{key} has spawn rules but no spawn point on map {map}");
        }

        // The global spawn-HP jitter is the "*" row; losing it silently makes every spawn identical again.
        Assert.True(Content.MobHpJitter);

        // ---- the two spawn systems stay separable -------------------------------------------------
        // Every hunting-map row carries a batch timer, and every timer is a real RTK `handleSpawn` value.
        // A row that loses its Timer silently falls back to the per-point model, which is the treadmill the
        // batch model exists to prevent — so this is the assertion that keeps room-clearing meaningful.
        var batch = Content.AreaSpawns.Where(a => a.Timer > 0).ToList();
        var points = Content.AreaSpawns.Where(a => a.Timer <= 0).ToList();
        Assert.True(batch.Count > 2000, $"only {batch.Count} batched area-spawn rows — did Timer stop loading?");
        Assert.All(batch, a => Assert.InRange(a.Timer, 2, 21600));
        // 300s is RTK's overwhelming default (445 of 844 calls); if the modal timer moves, the extractor
        // resolved the rate tables against something other than _defaultRates.
        Assert.Equal(300, batch.GroupBy(a => a.Timer).OrderByDescending(g => g.Count()).First().Key);
        // The only rows left on the point model are the trap-ambush supplement, which is a different RTK
        // system (trap/mob_spawn.lua) and is meant to stay per-point.
        Assert.All(points, a => Assert.True(a.RespawnSec >= 0));
        Assert.True(points.Count < 200, $"{points.Count} area rows fell out of the batch model");

        // Rows of one handleSpawn call share a clock. If grouping collapsed, every row would be its own
        // group and a twelve-mob cave would refill in twelve independent dribbles instead of one batch.
        var groups = batch.Select(a => (a.Map, a.Group)).Distinct().Count();
        Assert.True(groups < batch.Count, "every batch row is its own group — call grouping was lost");
        Assert.True(groups > 700, $"only {groups} spawn groups — grouping over-merged");

        // No group asks for more of a creature than RTK does. RTK counts map-wide before topping up, so two
        // calls naming one mob cap at the MAX rather than the sum; summing them (the old extractor) put 20
        // mob-13s on map 167 where RTK has 10, and 40 on map 399 where RTK has 20.
        Assert.Equal(10, batch.Where(a => a.Map == 167 && a.MobId == 13).Max(a => a.Count));
        Assert.Equal(20, batch.Where(a => a.Map == 399 && a.MobId == 76).Max(a => a.Count));

        // Per-creature static respawn (RTK Mobs.MobSpawnTime). One shared cadence for everything is what let
        // a Mythic elite come back as fast as a town rat.
        var rat = Content.Mobs.First(m => m.Key == "rabbit");
        var elite = Content.Mobs.First(m => m.Key == "mythic_monkey");
        Assert.Equal(12, rat.SpawnTime);
        Assert.Equal(360, elite.SpawnTime);
        Assert.True(Content.Mobs.Select(m => m.SpawnTime).Distinct().Count() > 5,
                    "every mob has the same SpawnTime — the merge didn't land");

        // The Lua escape hatch is wired: mob_ai.lua compiled (RejectedScripts covers that) AND its hooks
        // registered under keys the world will actually look up. A typo'd creature name there is invisible.
        Assert.True(MobScript.Has("yin_mouse", MobScript.OnAttacked));
        // The sworn-enemy brand hangs off the KILL, not the hit — the in-game instructions say "do not kill
        // any Leviathans", and hooking on_attacked would brand a player for one stray swing.
        Assert.True(MobScript.Has("leviathan", MobScript.AfterDeath));
        Assert.False(MobScript.Has("leviathan", MobScript.OnAttacked));
        Assert.False(MobScript.Has("white_rabbit", MobScript.OnAttacked));   // ordinary mobs stay pure C#
    }

    /// <summary>Gathering nodes resolve end to end: every node names a real mob, every tool and every item it
    /// can yield is a real item, and none of them wander. All four fail silently — a typo'd tool key makes a
    /// node simply unharvestable (the drop just drops), and a node left out of MobStationary.csv walks off
    /// its own spawn tile, which is how the Kugnae wheat field ended up strolling around.</summary>
    [Fact]
    public void HarvestNodesResolve()
    {
        EnsureLoaded();

        Assert.NotEmpty(Content.HarvestNodes);
        foreach (var (key, node) in Content.HarvestNodes)
        {
            var mob = Content.MobByKey(key);
            Assert.True(mob is not null, $"HarvestNodes.csv names unknown mob '{key}'");
            Assert.True(mob!.Stationary, $"harvest node '{key}' is not in MobStationary.csv — it will wander");
            Assert.NotEmpty(node.Tools);
            foreach (var tool in node.Tools)
                Assert.True(Content.ItemByKey(tool) is not null, $"{key} names unknown tool '{tool}'");
            foreach (var (item, _) in node.Yield.Concat(node.Bonus))
                Assert.True(Content.ItemByKey(item) is not null, $"{key} yields unknown item '{item}'");
            Assert.NotEmpty(node.Yield);
        }

        // The wheat field is the one node type actually placed in our world; if these rows ever vanish from
        // Spawns.csv, farming silently has no source at all.
        var wheat = Content.MobByKey("tall_wheat");
        Assert.NotNull(wheat);
        Assert.Contains(Content.Spawns, s => s.MobId == wheat!.Id);
    }

    /// <summary>The no-casting maps resolve, on both sides of the rule.
    ///
    /// <para>Maps.csv's MapSpells column was parsed by nothing at all, so magic worked everywhere — inside
    /// taverns, the three Gathering halls, the class trainers' buildings. This asserts the column is read, and
    /// that the 40 Kugnae/Buya trainer-building rows corrected in place (RTK's dump blocks Nagnang's identical
    /// rooms but leaves these open) stayed corrected.</para>
    ///
    /// <para>The "still allowed" half matters just as much: MapIndoor is set on every cave and dungeon in the
    /// game, so a rule keyed off "indoors" instead of this column would have silently made hunting
    /// impossible.</para></summary>
    [Fact]
    public void NoCastingMapsResolve()
    {
        EnsureLoaded();

        // Straight from RTK's dump — the three Gathering halls, a tavern, Nagnang's rogue trainer.
        foreach (var (map, name) in new (ushort, string)[]
                 { (1011, "Kugnae Gathering"), (1012, "Buya Gathering"), (2520, "Nagnang Gathering"),
                   (2, "Walsuk Tavern"), (2514, "Rogue Dagger — Nagnang rogue trainer") })
            Assert.False(Content.SpellsAllowed(map), $"map {map} ({name}) must refuse casting");

        // …and the rows we corrected: the Kugnae/Buya trainer halls, sanctums and alignment rooms.
        foreach (var (map, name) in new (ushort, string)[]
                 { (15, "Rogue Maro hall"), (16, "Maro Sanctum"), (343, "Rogue Maso hall"),
                   (368, "Maso Sanctum"), (11, "Warrior Tebaek"), (312, "Kwi-Sin Maro") })
            Assert.False(Content.SpellsAllowed(map), $"map {map} ({name}) must refuse casting");

        // …and casting still works outdoors AND in the dungeons, which are MapIndoor=1 too. 201/100 are the
        // Rabbit and Tiger Mythic caves (MythicCaves.csv DestMap) — hunting maps, where a rule keyed off
        // "indoors" would have silently disarmed every mage and poet.
        foreach (var (map, name) in new (ushort, string)[]
                 { (0, "Kugnae"), (330, "Buya"), (52, "Bat Cave"), (53, "Rat Maze"),
                   (201, "Mythic Waters 1 — the Rabbit cave"), (100, "Restless Cage 1 — the Tiger cave") })
            Assert.True(Content.SpellsAllowed(map), $"map {map} ({name}) must still allow casting");
    }

    /// <summary>The city-locked rogue Remedies resolve to their one kingdom, every alignment reskin included.
    ///
    /// <para>nexusatlas 2003-07-01: Maro's Remedy is Kugnae-only, Maso's Buya-only, Dagger's Nagnang-only.
    /// The lock is keyed on <see cref="Content.BaseKey"/> so the Kwi-Sin/Ming-Ken/Ohaeng reskins are covered
    /// by one row each — which is exactly the thing that breaks silently if the key naming ever shifts, since
    /// an unmatched key just means "any trainer teaches it" and nobody notices.</para></summary>
    [Fact]
    public void RogueRemediesAreLockedToOneCity()
    {
        EnsureLoaded();

        var expected = new (string Key, int Region)[]
        {
            ("maros_remedy_rogue", 0), ("kwisin_maros_remedy_rogue", 0),
            ("mingken_maros_remedy_rogue", 0), ("ohaeng_maros_remedy_rogue", 0),
            ("masos_remedy_rogue", 1), ("kwisin_masos_remedy_rogue", 1),
            ("mingken_masos_remedy_rogue", 1), ("ohaeng_masos_remedy_rogue", 1),
            ("daggers_remedy_rogue", 3), ("kwisin_daggers_remedy_rogue", 3),
            ("mingken_daggers_remedy_rogue", 3), ("ohaeng_daggers_remedy_rogue", 3),
        };
        foreach (var (key, region) in expected)
        {
            var sp = Content.SpellByKey(key);
            Assert.True(sp is not null, $"Spells.csv no longer has '{key}'");
            Assert.Equal(region, Content.CityLockOf(sp!));
            Assert.True(Content.TeachableInRegion(sp!, region));
            Assert.False(Content.TeachableInRegion(sp!, region == 0 ? 1 : 0));
        }

        // The trainers really are in the regions the lock names (Content.RegionOf drives the gate).
        Assert.Equal(0, Content.RegionOf(16));    // Maro Sanctum, Kugnae
        Assert.Equal(1, Content.RegionOf(368));   // Maso Sanctum, Buya
        Assert.Equal(3, Content.RegionOf(2514));  // Rogue Dagger, Nagnang

        // Everything else stays teachable anywhere — a lock that leaked would silently strand a whole ladder.
        var ordinary = Content.SpellByKey("might_rogue");
        Assert.NotNull(ordinary);
        Assert.Equal(-1, Content.CityLockOf(ordinary!));
        Assert.True(Content.TeachableInRegion(ordinary!, 1));
    }

    /// <summary>Every buff spell whose export row names an exclusivity slot still reaches the verb that reads
    /// it. <c>arch_buff</c> refuses a re-cast while the slot is occupied (RTK checkIfCast), and it takes the
    /// slot from spell_effects.csv's <c>cureCat</c> when spell_verbs.lua's own table doesn't name the spell —
    /// so a missing verb here means Might goes back to being spammable with no error anywhere.</summary>
    [Fact]
    public void BuffArchetypeVerbsAreLoaded()
    {
        EnsureLoaded();

        Assert.True(SpellScript.HasVerb("arch_buff"), "spell_verbs.lua lost arch_buff");
        Assert.True(SpellScript.HasVerb("arch_targetbuff"), "spell_verbs.lua lost arch_targetbuff");

        // The Might family is the reported bug: rogue + mage self-cast, mage/poet cast-on-target. All four
        // reskins of each must still be Buff/TargetBuff rows carrying a might bonus, or the slot has nothing
        // to attach to.
        foreach (var key in new[]
                 { "might_rogue", "spirit_strength_rogue", "inner_blessing_rogue", "temper_rogue",
                   "might_mage", "spirit_strength_mage", "inner_blessing_mage", "temper_mage",
                   "valor_poet", "valor_mage" })
        {
            var sp = Content.SpellByKey(key);
            Assert.True(sp is not null, $"Spells.csv no longer has '{key}'");
            var fx = Content.FxFor(sp!);
            Assert.True(fx is not null, $"spell_effects.csv has no row for '{key}'");
            Assert.True(fx!.Archetype is "Buff" or "TargetBuff",
                $"'{key}' is archetype '{fx.Archetype}' — it no longer reaches the verb that owns the slot guard");
            Assert.Equal("might", fx.BuffStat);
        }
    }

    /// <summary>The two soundtracks are separate id SPACES, not one list. mp3 2 ("buyeo", 5.x) and midi 2
    /// ("dragon", stock) are different songs on different 0x19 channels, so a set-blind lookup would hand a
    /// 5.x player the midi. A name unique to one set must still resolve from the other, so a player can name
    /// anything he can hear without switching first.</summary>
    [Fact]
    public void TheTwoSoundtracksAreSeparateIdSpaces()
    {
        EnsureLoaded();

        Assert.Equal("dragon", Content.FindTrack("2", Content.MusicSet.Old)!.Name);
        Assert.Equal("buyeo",  Content.FindTrack("2", Content.MusicSet.New)!.Name);
        Assert.Equal(121,      Content.FindTrack("underwater", Content.MusicSet.Old)!.Id);
    }

    /// <summary>Every zone's 5.x pick must be an ORDERED playlist (a .LST id) — not a single mp3, and not a
    /// SHUFFLED (.LSR) one. A single track never advances off itself, and the client's shuffled advance picks
    /// rand()%10+1 and no-ops whenever that lands on the entry already playing, which kills the music for
    /// good (see Content.MusicTrack). Both failures are silent — the area just goes quiet — so they are only
    /// ever caught here.</summary>
    [Fact]
    public void EveryFiveXMapPickIsAnOrderedPlaylist()
    {
        EnsureLoaded();

        Assert.NotEmpty(Content.BgmZones);
        foreach (var z in Content.BgmZones)
        {
            Assert.True(z.Type5x == 1, $"zone '{z.Zone}' resolved its Track5x to a midi ({z.Track5x})");
            var track = Content.MusicTracks.FirstOrDefault(
                t => t.Set == Content.MusicSet.New && t.Id == z.Track5x);
            Assert.True(track is { Playlist: true },
                $"zone '{z.Zone}' 5.x track {z.Track5x} is not a playlist — it will never leave its one song");
            Assert.False(track!.Shuffle,
                $"zone '{z.Zone}' 5.x track {z.Track5x} is a shuffled list — it will stall dead on a repeat pick");
        }
    }

    /// <summary>The two playlist families are the SAME ten songs; only the entry point and the advance differ.
    /// Every ordered list must therefore have a "-rand" twin and vice versa, so a stalling assignment can
    /// always be swapped for a safe one by name.</summary>
    [Fact]
    public void EveryShuffledPlaylistHasAnOrderedTwin()
    {
        EnsureLoaded();

        var ordered = Content.MusicTracks.Where(t => t.Playlist && !t.Shuffle).ToList();
        var shuffled = Content.MusicTracks.Where(t => t.Shuffle).ToList();
        Assert.NotEmpty(ordered);
        Assert.Equal(ordered.Count, shuffled.Count);
        foreach (var s in shuffled)
        {
            Assert.EndsWith("-rand", s.Name);
            Assert.True(ordered.Any(o => o.Name == s.Name[..^"-rand".Length]),
                $"shuffled list '{s.Name}' ({s.Id}) has no ordered twin to fall back on");
        }
    }

    /// <summary>Chung Ryong's Rage must be castable to the TOP of its climb inside one run. It is the only
    /// spell whose window and whose recast gate are separate numbers that have to agree: the run is armed once
    /// (RTK sets its duration in the first-cast branch only, and never re-arms on a tier-up), while climbing
    /// 1 to 6 costs five more casts at one aether apiece. If the window is shorter than five aethers, the top
    /// tiers are simply unreachable — which is exactly what happened when the RTK export dropped the spell's
    /// in-script <c>local duration = 938000</c> and the verb fell back to a guessed 135s against a 120s gate,
    /// leaving a 15-second sliver to catch and no way past Rage 2. Read from the two files that actually feed a
    /// cast, so a well-meant edit to either one can't quietly re-break the climb.</summary>
    [Fact]
    public void ChungRyongRageWindowFitsTheWholeClimb()
    {
        EnsureLoaded();

        var sp = Content.SpellByKey("chung_ryongs_rage");
        Assert.NotNull(sp);
        var aether = Content.FxFor(sp!)?.Aether ?? 0;
        Assert.True(aether > 0, "chung_ryongs_rage lost its spell_effects aether — the recast gate is the climb's clock");

        var lua = System.IO.File.ReadAllText(
            System.IO.Path.Combine(Shared.RepoPaths.GameDataDir(), "spell_verbs.lua"));
        var m = System.Text.RegularExpressions.Regex.Match(lua, @"CR_RAGE_DURATION_MS\s*=\s*(\d+)");
        Assert.True(m.Success, "spell_verbs.lua no longer defines CR_RAGE_DURATION_MS");
        int window = int.Parse(m.Groups[1].Value);

        // Six tiers = five climbing casts after the first, each gated by one aether.
        Assert.True(window >= 5 * aether,
            $"Chung Ryong's rage window {window}ms cannot reach Rage 6: the climb needs 5 x {aether}ms of "
            + $"recasts ({5 * aether}ms) before the fury wears out");
    }

    /// <summary>Baekho's Cunning is the other tiered fury and has the identical shape: one run armed by the
    /// first cast (RTK sets its duration in the first-cast branch only), climbed by recasting on the aether.
    /// Its constants all live in the verb rather than a CSV, so this reads them straight out of the Lua — the
    /// point is that the three numbers have to AGREE, and nothing else checks that they do.</summary>
    [Fact]
    public void BaekhosCunningWindowFitsTheWholeClimb()
    {
        EnsureLoaded();

        var lua = System.IO.File.ReadAllText(
            System.IO.Path.Combine(Shared.RepoPaths.GameDataDir(), "spell_verbs.lua"));
        static int LuaConst(string src, string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(src, name + @"\s*=\s*(\d+)");
            Assert.True(m.Success, $"spell_verbs.lua no longer defines {name}");
            return int.Parse(m.Groups[1].Value);
        }

        int window = LuaConst(lua, "CUNNING_DURATION");
        int aether = LuaConst(lua, "CUNNING_AETHER");
        int max    = LuaConst(lua, "CUNNING_MAX");

        Assert.True(window >= (max - 1) * aether,
            $"Baekho's Cunning window {window}ms cannot reach Cunning {max}: the climb needs {max - 1} x "
            + $"{aether}ms of recasts ({(max - 1) * aether}ms) before the stance lapses");
    }

    /// <summary>The Buya town rat nest (AmbushConfig.csv map 330 + AmbushBursts.csv <c>rat_nest</c>). Unlike
    /// every other ambush row this one has NO RTK source — it is reconstructed from a live 7.x sighting — so
    /// there is no Lua to diff it against and this is the only thing keeping the hand-authored row honest.
    /// Both of its silent failure modes are the reason it exists: a burst-table typo loads as an EMPTY burst
    /// (the tile fires, spawns nothing, and reads exactly like "there is no ambush in Buya"), and a MobCap at
    /// or under the town's resident population means RefillAmbush never places a tile in the first place.</summary>
    [Fact]
    public void BuyaRatNestAmbushIsWired()
    {
        EnsureLoaded();

        Assert.True(Content.Ambushes.TryGetValue(330, out var cfg), "no ambush config for Buya (map 330)");
        Assert.Equal("burst", cfg!.PrimaryKind);
        Assert.Equal("You have disturbed a nest of rats.", cfg.Message);
        Assert.Equal(5, cfg.Count);

        // RefillAmbush stops placing while live mobs >= MobCap, and Buya already carries its own residents.
        int residents = Content.AreaSpawns.Where(a => a.Map == 330).Sum(a => a.Count);
        Assert.True(cfg.MobCap > residents,
            $"MobCap {cfg.MobCap} <= the {residents} mobs Buya already spawns — no rat tile would ever be placed");

        // The burst itself: four rats, which World.AmbushBurstTile lands east/west/north/south — the
        // "surrounded" of the sighting. A fifth would spawn ON the stepper, which is the sentry-pack shape.
        Assert.True(Content.AmbushBursts.TryGetValue(cfg.PrimaryTable, out var variants),
            $"AmbushConfig points map 330 at burst table '{cfg.PrimaryTable}', which AmbushBursts.csv does not define");
        Assert.NotEmpty(variants!);
        Assert.All(variants!, v =>
        {
            Assert.Equal(4, v.Length);
            Assert.All(v, id => Assert.Equal("rat", Content.MobById(id)?.Key));
        });
    }
}
