using Shared;

namespace Server;

/// <summary>
/// The world's weather: a pure, deterministic function of <em>where</em> (region zone), <em>when</em>
/// (a coarse time period), and the <em>season</em>. Like <see cref="GameCalendar"/> it holds no state —
/// ask it what the weather is, whenever you need to know — which is what makes weather robust:
///
/// <list type="bullet">
/// <item><b>Consistent across players and restarts.</b> Every session (and the server after a reboot)
/// computes the identical value for a map, so everyone standing on it sees the same sky (requirement 3)
/// and a restart resumes the same weather rather than re-clearing the world.</item>
/// <item><b>Persists while you step inside.</b> Weather is a function of time, not of who is watching, so
/// ducking into a shop and back out inside the same <see cref="PeriodHours"/> window returns the exact
/// same weather — if it was raining when you went in, it is still raining when you come out
/// (requirement 2). Stay inside past a period boundary and it may have moved on, which is realistic.</item>
/// <item><b>No weather indoors.</b> Indoor maps (RTK <c>MapIndoor</c> — every town interior, cave and
/// dungeon) are always clear (requirement 1). See <see cref="Content.IsIndoor"/>.</item>
/// <item><b>Season-driven.</b> Snow only in Winter; rain in Spring and Summer (more often in Spring);
/// Fall is dry (requirement 4). See <see cref="SeasonRule"/>.</item>
/// </list>
///
/// <para>The 4.95 client renders only three states (see <c>Session.SendWeather</c> / <c>WeatherWire</c>):
/// 0 clear, 1 rain, 2 snow. "More rain in Spring than Summer" is therefore a matter of how <em>often</em>
/// it rains, not how hard — there is a single rain visual — so it is expressed as a higher per-period
/// chance, not a heavier effect.</para>
/// </summary>
internal static class WeatherModel
{
    public const byte Clear = 0, Rain = 1, Snow = 2;

    /// <summary>Weather holds steady for this many in-game hours, then re-rolls. 2 in-game hours is 15 real
    /// minutes at <see cref="GameCalendar.MsPerHour"/> — the cadence the old per-map random roll used, slow
    /// enough that a normal indoor visit never crosses a boundary (see the class doc's persistence point).</summary>
    public const int PeriodHours = 2;

    /// <summary>Bucket for maps with no RTK region row (<see cref="Content.RegionOf"/> &lt; 0): they all share
    /// one weather so a hunting field and its neighbour agree rather than disagreeing screen-to-screen. Offset
    /// well past the real kingdom ids (0..3) so it can never collide with one.</summary>
    private const int WildernessZone = 1000;

    /// <summary>Per-season precipitation as (chance out of 100, type). Snow only in Winter, rain in Spring
    /// (frequent) and Summer (occasional, less than Spring), Fall and everything else dry. Season ids are
    /// <see cref="GameCalendar.SeasonName"/>'s: 1 Spring · 2 Summer · 3 Fall · 4 Winter. Tunable.</summary>
    private static (int chance, byte precip) SeasonRule(int season) => season switch
    {
        1 => (55, Rain),   // Spring — frequent rain
        2 => (30, Rain),   // Summer — occasional rain (less than Spring)
        4 => (45, Snow),   // Winter — snow
        _ => (0,  Clear),  // Fall (3) / unknown — dry
    };

    /// <summary>The weather zone a map belongs to — its RTK kingdom region, or the shared wilderness bucket
    /// for region-less maps. Indoor-ness is handled separately by <see cref="For"/>, not here.</summary>
    public static int ZoneOf(ushort mapId)
    {
        int r = Content.RegionOf(mapId);
        return r >= 0 ? r : WildernessZone;
    }

    /// <summary>The current period index (whole <see cref="PeriodHours"/> windows since the calendar epoch).
    /// Changes exactly when weather is allowed to change; the world tick watches it for rollovers.</summary>
    public static long PeriodNow() => GameCalendar.HoursNow() / PeriodHours;

    /// <summary>Deterministic weather for an OUTDOOR zone in a given period and season. Indoor gating is the
    /// caller's job (see <see cref="For"/>); this is the pure roll everyone agrees on.</summary>
    public static byte Roll(int zone, long period, int season)
    {
        var (chance, precip) = SeasonRule(season);
        if (chance <= 0) return Clear;
        return Hash(zone, period) % 100 < (uint)chance ? precip : Clear;
    }

    /// <summary>The weather a player should see on <paramref name="mapId"/> right now: clear indoors, else the
    /// deterministic roll for the map's zone, period and the live season.</summary>
    public static byte For(ushort mapId)
    {
        if (Content.IsIndoor(mapId)) return Clear;
        var (_, _, season, _) = GameCalendar.Now();
        return Roll(ZoneOf(mapId), PeriodNow(), season);
    }

    // A stable scramble of (zone, period) into a well-distributed uint — SplitMix64 finalizer. NOT Random:
    // it must yield the identical value on every process and every restart so all players (and the server
    // after a reboot) agree on the sky.
    private static uint Hash(int zone, long period)
    {
        ulong x = (ulong)period * 0x9E3779B97F4A7C15UL + unchecked((ulong)(uint)zone) * 0xD1B54A32D192ED03UL;
        x ^= x >> 30; x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27; x *= 0x94D049BB133111EBUL;
        x ^= x >> 31;
        return (uint)x;
    }
}
