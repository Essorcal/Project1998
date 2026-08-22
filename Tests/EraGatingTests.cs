using Server;
using Shared;
using Xunit;

namespace Tests;

/// <summary>
/// The era calendar decides what a brand-new player sees, and it is the one part of the content pipeline
/// whose failure is INVISIBLE: a wrong date doesn't throw, doesn't log, and doesn't fail to load — it just
/// quietly serves the wrong decade. These tests pin the three tutorial eras against the REAL
/// <c>EraFeatures.csv</c> (only the target date is overridden), so editing a date in that file without
/// meaning to breaks the build rather than the world.
///
/// Assembly-wide parallelism is off (see AssemblyInfo.cs), so driving the static calendar here is safe —
/// but every test still restores it in a finally, because <see cref="ContentSmokeTests"/> calls
/// <c>Content.Load</c>, which re-reads the calendar from the same environment variable.
/// </summary>
public class EraGatingTests
{
    // Point the calendar at a throwaway ServerTuning.csv holding just the date. EraFeatures.csv is left
    // alone on purpose: the shipped dates are the thing under test.
    private static void WithEraDate(int yyyymmdd, Action body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-era-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var tuning = Path.Combine(dir, "ServerTuning.csv");
        File.WriteAllText(tuning, "key,value\nEraDate," + yyyymmdd + "\n");

        var prev = Environment.GetEnvironmentVariable("P1998_SERVER_TUNING");
        try
        {
            Environment.SetEnvironmentVariable("P1998_SERVER_TUNING", tuning);
            EraCalendar.Reload();
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("P1998_SERVER_TUNING", prev);
            EraCalendar.Reload();           // put the real calendar back for whatever runs next
            try { Directory.Delete(dir, true); } catch { /* temp dir; not worth failing a test over */ }
        }
    }

    /// <summary>2001-07-09, the shipped default (the day client 4.95 released): the newbie area exists, the
    /// tutor has stopped teaching the beats that moved into it, and both 2001-03-18 quests are live.</summary>
    [Fact]
    public void DefaultDateIsTheFullTutorial()
    {
        WithEraDate(EraCalendar.DefaultDate, () =>
        {
            Assert.True(Era.Has(Era.NewbieArea),        "newbie area should exist at 2001-07-09");
            Assert.False(Era.Has(Era.TutorNoviceChain), "tutor should NOT re-teach beats that moved into the area");
            Assert.True(Era.Has(Era.DuMountainQuest),   "Du Mountain quest shipped 2001-03-18");
            Assert.True(Era.Has(Era.StudentCapQuest),   "student cap quest shipped 2001-03-18");
        });
    }

    /// <summary>The day before 2001-03-18: the area is open but neither of that day's quests exists yet.
    /// This is the middle of the three eras.</summary>
    [Fact]
    public void DayBeforeTheTwoThousandOneQuestsExcludesBoth()
    {
        WithEraDate(20010317, () =>
        {
            Assert.True(Era.Has(Era.NewbieArea));
            Assert.False(Era.Has(Era.DuMountainQuest), "quest is dated 2001-03-18; 03-17 must exclude it");
            Assert.False(Era.Has(Era.StudentCapQuest), "quest is dated 2001-03-18; 03-17 must exclude it");
        });

        WithEraDate(20010318, () =>
        {
            Assert.True(Era.Has(Era.DuMountainQuest), "the introduction date itself is INCLUSIVE");
            Assert.True(Era.Has(Era.StudentCapQuest), "the introduction date itself is INCLUSIVE");
        });
    }

    /// <summary>The area and the tutor-delivered chain are EXCLUSIVE — the beats moved, they were never
    /// duplicated. Retirement being exclusive is what makes the handover land on a single day, so check the
    /// hinge from both sides.</summary>
    [Fact]
    public void AreaAndTutorChainAreNeverBothLive()
    {
        WithEraDate(20001005, () =>
        {
            Assert.False(Era.Has(Era.NewbieArea),      "area opens 2000-10-06; the day before must exclude it");
            Assert.True(Era.Has(Era.TutorNoviceChain), "before the area, the tutor is the only source");
        });

        WithEraDate(20001006, () =>
        {
            Assert.True(Era.Has(Era.NewbieArea),        "introduction is inclusive");
            Assert.False(Era.Has(Era.TutorNoviceChain), "retirement is EXCLUSIVE — gone ON the handover day");
        });

        // The invariant itself, swept across the whole handover window.
        foreach (var d in new[] { 20000101, 20001005, 20001006, 20010317, 20010318, 20010709, 20050101 })
            WithEraDate(d, () =>
                Assert.True(Era.Has(Era.NewbieArea) != Era.Has(Era.TutorNoviceChain),
                    $"at {d} exactly one of the area / tutor-taught chain must be live"));
    }

    /// <summary>The Druid bouquet quest (2005-05-31) is the first gate that lands far OUTSIDE our window
    /// rather than near it, and the first whose subject is an NPC's existence: Yarlof runs that quest and
    /// nothing else, so at 2001-07-09 he must not be in the world at all. Checked either side of the day, and
    /// against the shipped default, so moving the date in EraFeatures.csv breaks the build.</summary>
    [Fact]
    public void DruidBouquetQuestIsFourYearsAfterOurEra()
    {
        WithEraDate(EraCalendar.DefaultDate, () =>
            Assert.False(Era.Has(Era.DruidBouquetQuest), "bouquet quest is 2005; the 4.95 default must exclude it"));

        WithEraDate(20050530, () =>
            Assert.False(Era.Has(Era.DruidBouquetQuest), "quest is dated 2005-05-31; the day before must exclude it"));

        WithEraDate(20050531, () =>
            Assert.True(Era.Has(Era.DruidBouquetQuest), "the introduction date itself is INCLUSIVE"));
    }

    /// <summary>A brand-new character starts in Welcome (4711) once the area exists, and at their nation's
    /// tutor before that. This is the behaviour the LOGIN server depends on, which is why the calendar
    /// lives in Shared at all.</summary>
    [Fact]
    public void NewCharacterSpawnFollowsTheEra()
    {
        WithEraDate(EraCalendar.DefaultDate, () =>
        {
            foreach (byte nation in new byte[] { 1, 2 })
            {
                var s = CharacterFactory.StartFor(nation);
                Assert.Equal(4711, s.map);                    // Welcome
                Assert.Equal((ushort)3, s.x);
                Assert.Equal((ushort)5, s.y);
                Assert.Equal((ushort)16, s.xs);               // 16x16 — dims must match the map, not the 12x12 homes
                Assert.Equal((ushort)16, s.ys);
            }
        });

        WithEraDate(20001005, () =>
        {
            Assert.Equal(36,  CharacterFactory.StartFor(1).map);   // Ironheart's Home
            Assert.Equal(351, CharacterFactory.StartFor(2).map);   // Jadespear's Home (Buya)
            Assert.Equal((ushort)12, CharacterFactory.StartFor(1).xs);
        });
    }

    /// <summary>Revive must NOT follow the era — a defeated veteran does not wake up in the newbie area.
    /// <c>HomeCityFor</c> is shared with the Silver Thread revive point, so it stays nation-only.</summary>
    [Fact]
    public void ReviveIgnoresTheEra()
    {
        WithEraDate(EraCalendar.DefaultDate, () =>
        {
            Assert.Equal(36,  CharacterFactory.HomeCityFor(1).map);
            Assert.Equal(351, CharacterFactory.HomeCityFor(2).map);
        });
    }

    /// <summary>The two overkill-spending mechanics on the vita strikes, both of which postdate our window by
    /// years: warrior <b>overflow</b> (2007-04-10) and the rogue <b>overkill</b> refund (2008-09-18). At the
    /// shipped default neither exists, which is what makes Berserk/Whirlwind a plain one-tile hit.
    ///
    /// <para>They are two keys rather than one because KRU shipped them seventeen months apart, and the gap
    /// is the point: 2007-04-10 → 2008-09-18 is a real, playable era in which warriors had overflow and
    /// rogues had no answer to it. Both edges are checked so neither date can drift unnoticed.</para></summary>
    [Fact]
    public void OverflowAndOverkillAreYearsAfterOurEra()
    {
        WithEraDate(EraCalendar.DefaultDate, () =>
        {
            Assert.False(Era.Has(Era.WarriorOverflow), "overflow is 2007; the 4.95 default must exclude it");
            Assert.False(Era.Has(Era.RogueOverkill),   "rogue overkill is 2008; the 4.95 default must exclude it");
        });

        WithEraDate(20070409, () =>
            Assert.False(Era.Has(Era.WarriorOverflow), "overflow is dated 2007-04-10; the day before excludes it"));

        WithEraDate(20070410, () =>
        {
            Assert.True(Era.Has(Era.WarriorOverflow), "the introduction date itself is INCLUSIVE");
            Assert.False(Era.Has(Era.RogueOverkill),  "rogues waited another 17 months for their counterweight");
        });

        WithEraDate(20080917, () =>
            Assert.False(Era.Has(Era.RogueOverkill), "overkill is dated 2008-09-18; the day before excludes it"));

        WithEraDate(20080918, () =>
        {
            Assert.True(Era.Has(Era.RogueOverkill),   "the introduction date itself is INCLUSIVE");
            Assert.True(Era.Has(Era.WarriorOverflow), "and overflow is still live by then");
        });
    }

    /// <summary>Every key our code gates on must actually HAVE a row in <c>EraFeatures.csv</c>. This is the
    /// one failure the fail-open design cannot report: a key with no row reads as PRESENT, so a misspelling,
    /// a dropped row, or a const added without its data silently leaves the feature switched on forever —
    /// and looks identical to a deliberate "we haven't dated this yet".
    ///
    /// <para>The guard lives here rather than in <c>Has</c> because fail-open is correct at RUNTIME (absence
    /// of evidence must never delete content) and wrong at BUILD time (a key we wrote code against is
    /// evidence we meant to date it). <c>KnownFeatures</c> is exactly the set where that distinction
    /// applies — a researched row nothing reads yet is fine and deliberately not checked.</para></summary>
    [Fact]
    public void EveryGatedFeatureHasADatedRow()
    {
        WithEraDate(EraCalendar.DefaultDate, () =>
        {
            foreach (var f in Era.KnownFeatures)
                Assert.True(Era.Window(f) is not null,
                    $"Era.KnownFeatures names '{f}' but EraFeatures.csv has no row for it — the gate is " +
                    "silently always-on. Add the row (with a Source), or drop the const.");
        });
    }

    /// <summary>EraDate=0 switches gating off entirely: every dated feature reads as present, including
    /// pairs that are mutually exclusive under a real date. That's the documented escape hatch.</summary>
    [Fact]
    public void ZeroDateDisablesGating()
    {
        WithEraDate(0, () =>
        {
            Assert.Null(Era.Today);
            foreach (var f in Era.KnownFeatures)
                Assert.True(Era.Has(f), $"{f} should be present when gating is off");
        });
    }
}
