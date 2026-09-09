using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The two owners #37 section 3 pulled out of <c>World</c>: <c>World.Clock</c> (the cached in-game calendar,
/// the <c>@clock</c> pin, the hour-rollover watch) and <c>World.Weather</c> (the zone overrides, the one
/// per-map resolution every reader shares, the period-rollover sweep).
///
/// <para>These are the tests the extraction makes possible rather than the ones it needed. Before it, the
/// only way at the period sweep was the real tick — and the period turns over every ~15 real minutes, so a
/// test could only watch for one and skip when it did not come (<c>MobAiTickTests</c> still does exactly
/// that, deliberately). Now the sweep is a method on an object a test can hold, and the two seams beside it
/// (<c>ForgetPeriodForTest</c>, <c>PinOverrideForTest</c>) let a beat's worth of rollover be staged in
/// microseconds. Nothing here reaches state the production code does not reach the same way.</para>
///
/// <para>Hygiene: the fixture's <c>World</c> is shared by every class in the <c>world</c> collection and has
/// no teardown, so every test here drops the pin or override it set in a <c>finally</c> and re-syncs the
/// per-map weather caches it disturbed — a leaked zone override would change what the next class's map
/// entry sends.</para>
/// </summary>
[Collection("world")]
public class WorldClockWeatherTests
{
    private readonly SessionFixture _fx;

    public WorldClockWeatherTests(SessionFixture fx) => _fx = fx;

    // Content-free maps: no Maps.csv row, so Content.RegionOf is -1 and WeatherModel buckets them all into
    // its shared wilderness zone, and Content.IsIndoor is false. One set per test so nothing is shared.
    private const ushort ZoneMapA = 60060, ZoneMapB = 60061, SweptMap = 60062, EmptyMap = 60063, LockMap = 60064;

    // The indoor case needs a REAL map: Content.IsIndoor is a Maps.csv column, so no synthetic id can be
    // indoor. Ironheart's Home (36, the fixture's start tile) is indoor and sits in region 0; Divine Gateway
    // (45) is outdoor and sits in the same region, which is what makes the pair a zone. Both facts are
    // asserted rather than trusted, so a Maps.csv edit fails here instead of quietly gutting the test.
    private const ushort IndoorMap = SessionFixture.HomeMap;
    private const ushort OutdoorSameZone = 45;

    // =====================================================================================================
    // The clock.
    // =====================================================================================================

    /// <summary>The <c>@clock</c> pin beats the derived hour, survives a beat, and is undone by one beat
    /// after it is released — the round trip the command promises ("every session gets a fresh 0x20 within
    /// one tick in both directions").
    ///
    /// <para>Driven through the real <c>Tick</c> rather than through <c>Sync</c> directly, so phase (1.6) is
    /// on the path: if the extraction had wired the tick to something other than <c>Clock.Sync</c>, the pin
    /// would still be readable and this test would still go red on the release half.</para>
    ///
    /// <para>The release half asserts the whole date, not just the hour, because <c>Sync</c> re-derives all
    /// four fields and the pin only ever replaced one of them. Guarded on an in-game hour rollover landing
    /// between the two <c>HoursNow</c> reads — a millisecond-wide window that opens once an in-game hour —
    /// rather than being flaky a few times a day.</para>
    ///
    /// <para>Falsified by deleting <c>if (_hourOverride is int oh) _hour = oh;</c> from
    /// <c>WorldClock.Sync</c>: red on the first post-beat assertion of the pinned hour,
    /// "Assert.Equal() Failure: Values differ / Expected: 9 / Actual: 4" — the two numbers are the pin and
    /// the derived hour, so they move with the wall clock; the failing assertion does not.</para></summary>
    [Fact]
    public void ThePinnedHourBeatsTheDerivedOneAndOneBeatUndoesTheRelease()
    {
        var clock = _fx.World.Clock;
        Assert.Null(clock.HourOverride);   // nothing before us leaked a pin

        try
        {
            int pin = (clock.ClockNow.hour + 5) % 24;   // never the derived hour, whatever the wall clock says

            clock.SetHourOverride(pin);
            Assert.Equal(pin, clock.HourOverride);
            Assert.Equal(pin, clock.ClockNow.hour);
            Assert.Equal((byte)pin, clock.Time.hour);

            // A beat does not wash it out: Sync re-derives the calendar, then puts the pin back over the hour
            // it derived.
            _fx.World.TickOnceForTest();
            Assert.Equal(pin, clock.HourOverride);
            Assert.Equal(pin, clock.ClockNow.hour);

            // Totem time reads the live hour, so it follows the pin — which is the whole reason @clock exists.
            for (int totem = 0; totem < 4; totem++)
                Assert.Equal(Content.IsTotemTime(pin, totem), clock.IsTotemTime(totem));

            // Released, the hour stays where the pin left it until a beat re-derives it (SetHourOverride
            // forces _gameHour = -1 rather than re-deriving on the spot), and ONE beat is enough.
            clock.SetHourOverride(null);
            Assert.Null(clock.HourOverride);
            Assert.Equal(pin, clock.ClockNow.hour);

            long before = GameCalendar.HoursNow();
            _fx.World.TickOnceForTest();
            var (hour, day, season, year) = GameCalendar.At(before);
            if (GameCalendar.HoursNow() == before)   // an in-game hour rolled between the reads: skip, don't flake
            {
                Assert.Equal((hour, day, year), clock.ClockNow);
                Assert.Equal(GameCalendar.SeasonName(season), clock.SeasonName);
            }
        }
        finally
        {
            clock.SetHourOverride(null);
            _fx.World.TickOnceForTest();
        }
    }

    // =====================================================================================================
    // The weather.
    // =====================================================================================================

    /// <summary>Indoors beats an override. The zone override is pinned THROUGH the indoor map — @weather
    /// pins the zone the caller is standing in, and standing indoors is no obstacle to that — so the same
    /// override that leaves map 36 clear is visible on the outdoor map beside it.
    ///
    /// <para>That ordering is the behaviour: <c>WeatherService.For</c> tests <c>IsIndoor</c> BEFORE it looks
    /// the override up. Falsified by swapping those two lines: red with
    /// "Assert.Equal() Failure: Values differ / Expected: 0 / Actual: 2" on the indoor read.</para></summary>
    [Fact]
    public void AnIndoorMapStaysClearUnderAZoneOverride()
    {
        Assert.True(Content.IsIndoor(IndoorMap));
        Assert.False(Content.IsIndoor(OutdoorSameZone));
        Assert.Equal(WeatherModel.ZoneOf(IndoorMap), WeatherModel.ZoneOf(OutdoorSameZone));

        try
        {
            _fx.World.Weather.Set(IndoorMap, WeatherModel.Snow);

            Assert.Equal(WeatherModel.Clear, _fx.World.Weather.Get(IndoorMap));
            Assert.Equal(WeatherModel.Snow, _fx.World.Weather.Get(OutdoorSameZone));
        }
        finally { _fx.World.Weather.ClearOverride(IndoorMap); }
    }

    /// <summary>An override is the ZONE's, not the map's: set through one map it is visible through every
    /// other map of the same zone, and cleared through any of them it is gone for all of them. The two maps
    /// here share the wilderness zone, which is asserted rather than assumed.
    ///
    /// <para>The pinned state is chosen to differ from what the model would say, so "the override is
    /// visible" cannot pass because the two happened to agree. The post-clear comparison is against a fresh
    /// <c>WeatherModel.For</c> and is skipped if the weather period rolled between the two reads (~15 real
    /// minutes apart, so a microsecond-wide window).</para>
    ///
    /// <para>Falsified by keying the override table on <c>mapId</c> instead of
    /// <c>WeatherModel.ZoneOf(mapId)</c> in <c>Set</c>: red on the FIRST read — the map it was set through
    /// no longer finds it either — "Assert.Equal() Failure: Values differ / Expected: 1 / Actual: 0" (the 1
    /// is the pinned state, which is rain whenever the model is not).</para></summary>
    [Fact]
    public void AnOverrideCoversTheWholeZoneAndClearsBackToTheModel()
    {
        Assert.Equal(WeatherModel.ZoneOf(ZoneMapA), WeatherModel.ZoneOf(ZoneMapB));
        Assert.False(Content.IsIndoor(ZoneMapA));
        Assert.False(Content.IsIndoor(ZoneMapB));

        byte model = _fx.World.Weather.Get(ZoneMapA);
        byte pin = model == WeatherModel.Rain ? WeatherModel.Snow : WeatherModel.Rain;
        try
        {
            _fx.World.Weather.Set(ZoneMapA, pin);
            Assert.Equal(pin, _fx.World.Weather.Get(ZoneMapA));
            Assert.Equal(pin, _fx.World.Weather.Get(ZoneMapB));

            long period = WeatherModel.PeriodNow();
            _fx.World.Weather.ClearOverride(ZoneMapB);   // cleared through the OTHER map of the zone
            byte back = _fx.World.Weather.Get(ZoneMapA);
            if (WeatherModel.PeriodNow() == period)      // a rollover would move the model under us
                Assert.Equal(WeatherModel.For(ZoneMapA), back);
        }
        finally { _fx.World.Weather.ClearOverride(ZoneMapA); }
    }

    /// <summary>The tick's phase (1.7) in isolation: on a period rollover the sweep queues a 0x1F for a map
    /// only when it has players AND its last-sent weather actually changed, and on a beat with no rollover it
    /// queues nothing at all (<c>TickQueues.WeatherChanges</c> stays null, which is what the flush tests).
    ///
    /// <para>Three maps' worth of case in one beat. <c>SweptMap</c> has a player and a stale cache — it is
    /// the only one that should appear. <c>EmptyMap</c> is in the same zone with the same stale cache and no
    /// player: a map nobody is standing on is not worth a packet, and its cache is deliberately left stale
    /// rather than being quietly refreshed. Then the same sweep is run twice more: once with the period
    /// unchanged (no rollover, nothing queued, the field still null) and once with the period forgotten again
    /// but the cache now current (a rollover with nothing to say).</para>
    ///
    /// <para>The override is pinned through <c>PinOverrideForTest</c>, which does NOT broadcast, precisely so
    /// the caches stay stale — the production <c>Set</c> pushes the change to every populated map in the zone
    /// on the spot, which would leave the sweep nothing to find.</para>
    ///
    /// <para>Falsified two ways. Dropping <c>if (pm.Players.Count == 0) continue;</c> from
    /// <c>SweepPeriod</c>: red on the empty map, "Assert.DoesNotContain() Failure: Filter matched in
    /// collection / (pos 1) / Collection: [Tuple (60062, 1), Tuple (60063, 1)]" — the second tuple is the
    /// map nobody is standing on. Dropping <c>if (w == pm.Weather) continue;</c>: red on the third sweep,
    /// "... (pos 0) / Collection: [Tuple (60062, 1)]" — a rollover with nothing to say said it
    /// anyway.</para></summary>
    [Fact]
    public void ThePeriodSweepQueuesOnlyPopulatedMapsWhoseSkyChanged()
    {
        Assert.Equal(WeatherModel.ZoneOf(SweptMap), WeatherModel.ZoneOf(EmptyMap));

        var (watcher, _) = _fx.Player("SweepWatcher", SweptMap);
        var (leaver, _) = _fx.Player("SweepLeaver", EmptyMap);
        _fx.World.LeaveMap(leaver, EmptyMap);   // the MapState stays, with an empty player list

        // EnterMap seeded both caches with WeatherService.For, so both are on the model right now.
        byte model = _fx.World.Weather.Get(SweptMap);
        byte pin = model == WeatherModel.Rain ? WeatherModel.Snow : WeatherModel.Rain;

        var rollover = new World.TickQueues();
        var noRollover = new World.TickQueues();
        var nothingToSay = new World.TickQueues();
        try
        {
            _fx.World.UnderWorldLockForTest(() =>
            {
                _fx.World.Weather.PinOverrideForTest(SweptMap, pin);
                _fx.World.Weather.ForgetPeriodForTest();
                _fx.World.Weather.SweepPeriod(rollover);
                _fx.World.Weather.SweepPeriod(noRollover);   // same period: the sweep must not run again
                _fx.World.Weather.ForgetPeriodForTest();
                _fx.World.Weather.SweepPeriod(nothingToSay); // rolled again, but every cache is current now
            });

            Assert.NotNull(rollover.WeatherChanges);
            Assert.Contains((SweptMap, pin), rollover.WeatherChanges!);
            Assert.DoesNotContain(rollover.WeatherChanges!, c => c.map == EmptyMap);

            Assert.Null(noRollover.WeatherChanges);

            Assert.NotNull(nothingToSay.WeatherChanges);
            Assert.DoesNotContain(nothingToSay.WeatherChanges!, c => c.map == SweptMap);
        }
        finally
        {
            _fx.World.UnderWorldLockForTest(() =>
            {
                _fx.World.Weather.PinOverrideForTest(SweptMap, null);
                _fx.World.Weather.ForgetPeriodForTest();
                _fx.World.Weather.SweepPeriod(new World.TickQueues());   // every cache back onto the model
            });
            _fx.World.LeaveMap(watcher, SweptMap);
        }
    }

#if DEBUG
    /// <summary>The contracts at the top of the three lock-requiring methods, both directions, in the
    /// <c>MobAiTickTests.StepOutsideTheWorldLockAsserts</c> shape: off the lock they are loud, under it they
    /// are silent. This is the guard that replaces the old "it is private to World, and everything in World
    /// already holds the lock" argument, which stopped applying the moment the state moved onto objects a
    /// session can reach.
    ///
    /// <para><c>Get</c> is the negative control: it takes <c>_lock</c> itself and so is legal from exactly
    /// the place the other three are not. Without it a stray <c>lock</c> around everything would pass the
    /// three assertions above and pass this test for the wrong reason.</para>
    ///
    /// <para>Debug-only by construction, like every lock assert in <c>docs/common/Locking.md</c>: the assert
    /// is compiled out of Release, so the test is too. Falsified by deleting each of the three assert lines
    /// in turn: red on that method's <c>Assert.NotNull</c>.</para></summary>
    [Fact]
    public void TheLockRequiringHalvesRefuseToRunOutsideTheWorldLock()
    {
        Assert.False(_fx.World.HoldsWorldLock);

        var forOff = Record.Exception(() => _fx.World.Weather.For(LockMap));
        Assert.NotNull(forOff);
        Assert.Contains("nowhere else", forOff!.Message);

        var sweepOff = Record.Exception(() => _fx.World.Weather.SweepPeriod(new World.TickQueues()));
        Assert.NotNull(sweepOff);
        Assert.Contains("nowhere else", sweepOff!.Message);

        var syncOff = Record.Exception(() => _fx.World.Clock.Sync());
        Assert.NotNull(syncOff);
        Assert.Contains("nowhere else", syncOff!.Message);

        // The same three under the lock, plus the one that takes the lock for itself.
        var under = Record.Exception(() => _fx.World.UnderWorldLockForTest(() =>
        {
            _fx.World.Weather.For(LockMap);
            _fx.World.Weather.SweepPeriod(new World.TickQueues());
            _fx.World.Clock.Sync();
        }));
        Assert.Null(under);
        Assert.InRange(_fx.World.Weather.Get(LockMap), WeatherModel.Clear, WeatherModel.Snow);
    }
#endif
}
