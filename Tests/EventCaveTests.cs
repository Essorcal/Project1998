using System.Linq;
using Server;
using Xunit;

namespace Tests;

/// <summary>
/// The Buya Library Caverns doorway and the tier ladder behind it (game-data/EventCaves.csv +
/// game-data/EventCaveTiers.csv).
///
/// <para>Worth its own file because the failure mode is invisible from either side. The ladder is a list of
/// bands matched in FILE ORDER, so a row moved, a bound off by one, or a band that overlaps its neighbour all
/// parse cleanly and simply route people to the wrong depth — and the entrance is a scripted tile whose
/// destination maps live in a different table again, so a tier can point at a map id that has no terrain and
/// nothing says so until a player walks into it.</para>
/// </summary>
public class EventCaveTests
{
    private static readonly object _gate = new();
    private static bool _loaded;

    private static void EnsureLoaded()
    {
        lock (_gate) { if (_loaded) return; TestProcessState.LoadContent(); _loaded = true; }
    }

    private const string BuyaLibraryCaverns = "buya_library_caverns";

    private static Content.EventCaveDef Caverns() =>
        Assert.Single(Content.EventCaves, c => c.Key == BuyaLibraryCaverns);

    /// <summary>Both tables loaded at all. An empty ladder does not throw — it makes every doorway refuse
    /// everyone, which reads in-game as "the caverns are closed" rather than as a broken file.</summary>
    [Fact]
    public void LadderAndEntrancesLoad()
    {
        EnsureLoaded();

        Assert.True(Content.EventCaveBands.Count > 0, "EventCaveTiers.csv loaded no bands");
        Assert.True(Content.EventCaves.Count > 0, "EventCaves.csv loaded no entrances");
        Assert.True(Content.EventCaveTiles.Count > 0, "no event-cave entrance tiles registered");
    }

    /// <summary>The bands must not overlap. They are matched first-hit in file order, so an overlap is not a
    /// load error — it silently makes the later row unreachable for the characters both rows claim.</summary>
    [Fact]
    public void BandsAreDisjoint()
    {
        EnsureLoaded();

        var bands = Content.EventCaveBands;
        for (int i = 0; i < bands.Count; i++)
            for (int j = i + 1; j < bands.Count; j++)
            {
                var a = bands[i];
                var b = bands[j];
                bool levelsOverlap = a.MinLevel <= b.MaxLevel && b.MinLevel <= a.MaxLevel;
                bool marksOverlap  = a.MinMark  <= b.MaxMark  && b.MinMark  <= a.MaxMark;
                Assert.False(levelsOverlap && marksOverlap,
                    $"EventCaveTiers.csv bands '{a.Label}' and '{b.Label}' overlap — '{b.Label}' is unreachable");
            }
    }

    /// <summary>Every level from the ladder's floor to the cap resolves to some band, and every rank at 99
    /// does too. A hole here is a level at which the doorway refuses for no stated reason.</summary>
    [Fact]
    public void LadderCoversEveryLevelFromItsFloorUp()
    {
        EnsureLoaded();

        int floor = Content.EventCaveBands.Min(b => b.MinLevel);
        for (int lvl = floor; lvl <= 99; lvl++)
            Assert.True(Content.EventCaveBandFor(lvl, 0) is not null, $"level {lvl} falls through the ladder");
        for (int mark = 0; mark <= Content.MaxMark; mark++)
            Assert.True(Content.EventCaveBandFor(99, mark) is not null, $"level 99 mark {mark} falls through the ladder");
    }

    /// <summary>The archive chart's own numbers (Warrior Tutor AzNCloudBoi, "Info - Event Cave Levels"),
    /// band by band, plus the two top bands that are ours rather than the chart's. Pinned as VALUES because
    /// that is the whole point of the table — if someone re-tunes a bound, this is where they say so.</summary>
    [Theory]
    // level, mark, expected tier, expected alt (0 = no choice)
    [InlineData(15, 0, 1, 0)]
    [InlineData(38, 0, 1, 0)]
    [InlineData(39, 0, 1, 2)]   // Cave 1/2 split
    [InlineData(40, 0, 1, 2)]
    [InlineData(41, 0, 2, 0)]
    [InlineData(66, 0, 2, 0)]
    [InlineData(67, 0, 2, 3)]   // Cave 2/3 split
    [InlineData(68, 0, 2, 3)]
    [InlineData(69, 0, 3, 0)]
    [InlineData(95, 0, 3, 0)]
    [InlineData(96, 0, 3, 4)]   // Cave 3/4 split
    [InlineData(98, 0, 3, 4)]
    [InlineData(99, 0, 4, 0)]
    [InlineData(99, 1, 4, 5)]   // Il san  — Cave 4/5 split (ours)
    [InlineData(99, 2, 4, 5)]   // Ee san
    [InlineData(99, 3, 5, 0)]   // Sam san — the top cave (ours)
    public void BandBoundsMatchTheChart(int level, int mark, int tier, int alt)
    {
        EnsureLoaded();

        var band = Content.EventCaveBandFor(level, mark);
        Assert.NotNull(band);
        Assert.Equal(tier, band!.Value.Tier);
        Assert.Equal(alt, band.Value.Alt);
    }

    /// <summary>Below the chart's floor there is no band at all, which is the refusal case. Asserted as a
    /// real outcome rather than left to fall out of the data: it is what the doorway's DenyMsg exists for.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    public void UnderTheFloorMatchesNoBand(int level)
    {
        EnsureLoaded();
        Assert.Null(Content.EventCaveBandFor(level, 0));
    }

    /// <summary>Every tier any band can select points at a map with real terrain, on every entrance. This is
    /// the check that would have caught the caverns shipping with only tier 1 wired.</summary>
    [Fact]
    public void EveryReachableTierHasARenderableMap()
    {
        EnsureLoaded();

        foreach (var cave in Content.EventCaves)
            foreach (var tier in Content.EventCaveBands.SelectMany(b => new[] { b.Tier, b.Alt }).Where(t => t > 0).Distinct())
            {
                ushort map = cave.MapForTier(tier);
                Assert.True(Content.TryMap(map, out _), $"event cave '{cave.Key}' tier {tier} -> map {map} has no map data");
            }
    }

    /// <summary>The caverns' own geometry: five tiers, RTK's landing tile, and both halves of the doorway
    /// registered as entrance tiles.</summary>
    [Fact]
    public void BuyaLibraryDoorwayIsWired()
    {
        EnsureLoaded();

        var cave = Caverns();
        Assert.Equal(new ushort[] { 6503, 6523, 6543, 6563, 6583 }, cave.TierMaps);
        Assert.Equal(486, cave.EntranceMap);
        Assert.Equal(9, cave.DestX);
        Assert.Equal(1, cave.DestY);

        foreach (ushort x in new ushort[] { 13, 14 })
            Assert.True(Content.EventCaveTiles.ContainsKey(((ushort)486, x, (ushort)0)),
                $"Buya Library doorway tile ({x},0) is not an event-cave entrance");
    }

    /// <summary>A tier deeper than the cave's map list clamps to the deepest map it HAS. Adding a band to the
    /// ladder must never be able to warp someone to map 0.</summary>
    [Fact]
    public void TierBeyondTheMapListClampsToTheDeepest()
    {
        EnsureLoaded();

        var cave = Caverns();
        Assert.Equal(cave.TierMaps[^1], cave.MapForTier(cave.TierMaps.Length + 3));
        Assert.Equal(cave.TierMaps[0], cave.MapForTier(0));
    }

    /// <summary>The doorway must NOT also be a plain warp. It was two Warps.csv rows straight into tier 1
    /// before this; the warp branch in HandleWalk runs FIRST, so leaving either row in place would take the
    /// step before the scripted tile ever saw it and quietly restore the un-tiered behaviour.</summary>
    [Fact]
    public void DoorwayIsNotAlsoAStaticWarp()
    {
        EnsureLoaded();

        foreach (ushort x in new ushort[] { 13, 14 })
            Assert.False(Content.TryWarp(486, x, 0, out var dest),
                $"Buya Library doorway tile ({x},0) is still a Warps.csv row -> {dest.m}; it would pre-empt the tier prompt");
    }

    /// <summary>The way OUT of each tier is still an ordinary warp — only the entrance became conditional.
    /// Losing these would seal five maps.</summary>
    [Fact]
    public void EveryTierStillWarpsBackToTheLibrary()
    {
        EnsureLoaded();

        foreach (ushort tierMap in Caverns().TierMaps)
        {
            Assert.True(Content.TryWarp(tierMap, 9, 0, out var dest), $"cavern tier {tierMap} has no exit at (9,0)");
            Assert.Equal(486, dest.m);
        }
    }

    /// <summary>Entry dialog and the split menu are all present. A blank page list or a blank option would
    /// send the client an empty box, which it renders as an unanswerable dialog.</summary>
    [Fact]
    public void EntryTextIsPopulated()
    {
        EnsureLoaded();

        var cave = Caverns();
        Assert.Equal(3, cave.Pages.Length);
        Assert.All(cave.Pages, p => Assert.False(string.IsNullOrWhiteSpace(p)));
        Assert.False(string.IsNullOrWhiteSpace(cave.Prompt));
        Assert.False(string.IsNullOrWhiteSpace(cave.OptionNear));
        Assert.False(string.IsNullOrWhiteSpace(cave.OptionFar));
        Assert.False(string.IsNullOrWhiteSpace(cave.DenyMsg));
    }

    /// <summary>The caverns' venom, as observed in the client: exactly 100 a tick, 220 seconds, and a gap
    /// that is a whole number of seconds between 1 and 4. PerTick has to be set rather than Amount — Amount
    /// is read as a RATE and converted against the tick length, and there is no single tick length here to
    /// convert against.</summary>
    [Fact]
    public void CavernVenomIsPerTickAndRagged()
    {
        EnsureLoaded();

        var venom = Assert.Single(Content.MobSpells["buya_library_mob"], s => s.Effect == "poison");
        Assert.Equal(100, venom.PerTick);
        Assert.Equal(220_000, venom.DurationMs);
        Assert.Equal(1_000, venom.TickMinMs);
        Assert.Equal(4_000, venom.TickMaxMs);
        Assert.Equal(0, (venom.TickMaxMs - venom.TickMinMs) % 1000);   // the gap is drawn in whole seconds
    }

    /// <summary>The venom procs on a LANDED SWING, one chance in four. It has to be an `onhit` row for that
    /// number to be true: on the timer trigger the roll repeats every 333ms tick until it passes, so a
    /// Chance of 4 there would not be a one-in-four proc, it would be a near-certain cast about a second
    /// after the cooldown lapsed.</summary>
    [Fact]
    public void CavernVenomProcsOnTheSwing()
    {
        EnsureLoaded();

        var venom = Assert.Single(Content.MobSpells["buya_library_mob"], s => s.Effect == "poison");
        Assert.True(venom.OnHit, "the caverns venom must be Trigger=onhit for Chance to mean one swing in N");
        Assert.Equal(4, venom.Chance);
        Assert.Equal(0, venom.EveryMs);   // ignored on an onhit row; 0 says so rather than implying a cooldown
    }

    /// <summary>Nothing in the table sets Trigger to something the loader does not understand. A typo there
    /// fails OPEN — the row silently reverts to the cast timer, where its Chance means something entirely
    /// different — so it is worth an assertion rather than a code read.</summary>
    [Fact]
    public void EveryTriggerValueIsRecognised()
    {
        EnsureLoaded();

        foreach (var (key, rows) in Content.MobSpells)
            foreach (var row in rows)
                Assert.True(row.Trigger is "" or "timer" or "onhit",
                    $"MobSpells.csv '{key}' / '{row.Name}' has unknown Trigger '{row.Trigger}' — it will fall back to the cast timer");
    }

    /// <summary>Cave 1's two starter creatures pay what they were observed paying. They shipped at 0 exp
    /// from the RTK import, which is worth pinning precisely because 0 is a value that never looks wrong.</summary>
    [Theory]
    [InlineData(198, "Scroll mouse", 500)]
    [InlineData(199, "Scroll rat", 750)]
    public void CaveOneStartersPayTheirExp(int mobId, string name, int exp)
    {
        EnsureLoaded();

        var mob = Content.MobById(mobId);
        Assert.NotNull(mob);
        Assert.Equal(name, mob!.Name);
        Assert.Equal(exp, mob.Exp);
    }
}
