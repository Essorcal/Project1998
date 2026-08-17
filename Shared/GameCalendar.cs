namespace Shared;

/// <summary>
/// The world's in-game calendar: a pure function of wall-clock time since a fixed real-world <see
/// cref="Epoch"/>. Nothing here has state — ask it what time it is, whenever you need to know.
///
/// <para><b>Cadence</b> (all of it RTK's own constants, <c>RTK-Server/rtk/src/map/map.c:1661</c>
/// <c>change_time_char</c>): one in-game hour per 7.5 real minutes, 24 hours per day, 91 days per season
/// (RTK's <c>cur_day</c> runs 1..91 and wraps at 92), 4 seasons per year. That is 364 in-game days = 45.5
/// real days per year, matching the community "Time Chart" tutor post exactly — Nexus runs at 8x real time,
/// so a 365-day year takes 365/8 = 45 5/8 real days.</para>
///
/// <para><b>Why derived rather than counted.</b> RTK increments a counter and reloads cur_time/cur_day/
/// cur_season/cur_year from its <c>Time</c> table at boot, so its calendar stops during downtime and slides
/// by however long each restart took. Deriving it from the epoch instead means restarts resume it exactly,
/// there is nothing to persist, and the login server — a separate process with no <c>World</c> — can stamp
/// a new character's "Born in ..." legend with the same date the game server is showing.</para>
/// </summary>
public static class GameCalendar
{
    /// <summary>Midnight local time on 2026-08-12, when the world read Yuri 1, Spring, day 1, hour 0. The
    /// UTC offset is spelled out so the VPS (UTC) and a dev box (Pacific) resolve the same instant.</summary>
    public static readonly DateTimeOffset Epoch = new(2026, 8, 12, 0, 0, 0, TimeSpan.FromHours(-7));

    public const long MsPerHour     = 450_000;   // RTK's timer_insert(450000, ...) — 7.5 real minutes
    public const int  HoursPerDay   = 24;
    public const int  DaysPerSeason = 91;
    public const int  SeasonsPerYear = 4;

    /// <summary>Whole in-game hours elapsed since <see cref="Epoch"/>, floored at 0 so a host clock set
    /// before the epoch parks the world at the beginning rather than running the calendar backwards.</summary>
    public static long HoursSinceEpoch(DateTimeOffset now)
    {
        double ms = (now - Epoch).TotalMilliseconds;
        return ms <= 0 ? 0 : (long)(ms / MsPerHour);
    }

    public static long HoursNow() => HoursSinceEpoch(DateTimeOffset.UtcNow);

    /// <summary>The calendar <paramref name="gameHours"/> in-game hours after <see cref="Epoch"/>. Year is
    /// capped at 255 because the wire field (opcode <c>0x20</c>) is a single byte — some 32 real years out.
    /// Season 1 = Spring (rtklua <c>Developers/sys.lua</c> <c>getCurSeason</c>, the mapping behind RTK's own
    /// <c>curT()</c> = "Yuri N, &lt;season&gt;" timemark; the <c>{Winter, Spring, Summer, Autumn}</c> table in
    /// <c>Accepted/Scripts/scripts.lua</c> is Mithia lore, a different game's ordering — don't use it).</summary>
    public static (int hour, int day, int season, int year) At(long gameHours)
    {
        long days    = gameHours / HoursPerDay;
        long seasons = days / DaysPerSeason;
        return ((int)(gameHours % HoursPerDay),
                (int)(days    % DaysPerSeason)  + 1,
                (int)(seasons % SeasonsPerYear) + 1,
                (int)Math.Min(255, seasons / SeasonsPerYear + 1));
    }

    public static (int hour, int day, int season, int year) Now() => At(HoursNow());

    public static string SeasonName(int season) =>
        season switch { 1 => "Spring", 2 => "Summer", 3 => "Fall", _ => "Winter" };

    /// <summary>"Yuri N, &lt;season&gt;" — the date as RTK's <c>curT()</c> writes it, for legend text and
    /// anywhere else a script asks for "the current date". "Yuri N" is the Nth year of King Yuri's reign,
    /// not a unit of time. (A live 4.95 self-profile capture reads "Born in Hyul 31, Winter" — same king,
    /// the other name; we say Yuri server-wide so every date the server writes agrees.)</summary>
    public static string Stamp
    {
        get { var (_, _, season, year) = Now(); return $"Yuri {year}, {SeasonName(season)}"; }
    }
}
