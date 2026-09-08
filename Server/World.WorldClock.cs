using System.Diagnostics;
using Shared;

namespace Server;

// The calendar half of World (#37, section 3). World.cs keeps the lock, the heartbeat and the 0x20 broadcast
// itself; this file keeps the cached in-game date, the hour-rollover watch that decides WHEN that broadcast
// goes out, and the @clock pin. Nested in World, like SpawnDirector and MobAiTick, so it reaches _lock and
// HoldsWorldLock as they are without widening either — and so the four calendar fields stay private to the
// one type that reads them.
public sealed partial class World
{
    /// <summary>
    /// The world's cached in-game date and its hour-rollover watch. One per <see cref="World"/>, built by the
    /// constructor before its first <see cref="Sync"/>, and called at the same moments the code was called
    /// when it lived in World.cs: <see cref="Sync"/> from the constructor and from the tick's phase (1.6),
    /// <see cref="SetHourOverride"/> from <c>@clock</c>, and the readouts (<see cref="Time"/>,
    /// <see cref="ClockNow"/>, <see cref="SeasonName"/>, <see cref="HourOverride"/>,
    /// <see cref="IsTotemTime"/>) from the sessions.
    ///
    /// <para><b>Lock discipline, unchanged by the move.</b> <see cref="SetHourOverride"/> takes
    /// <c>World._lock</c> itself, exactly as it did on <c>World</c>. <see cref="Sync"/> takes none and
    /// asserts <see cref="HoldsWorldLock"/>: the tick calls it inside its own acquisition, and the
    /// constructor now calls it inside a <c>lock</c> of its own, the way the two <c>Populate</c> calls above
    /// it already did. The readouts are plain field reads and are unsynchronised, exactly as they were — the
    /// flush reads <see cref="Time"/> outside the lock on every beat that rolled the hour.</para>
    /// </summary>
    internal sealed class WorldClock
    {
        private const string LockNote = "WorldClock.Sync runs under World._lock and nowhere else";

        private readonly World world;

        internal WorldClock(World world) => this.world = world;

        // ---- world calendar (opcode 0x20) ---------------------------------------------------------
        // The calendar itself lives in Shared.GameCalendar — a pure function of wall-clock time since a fixed
        // epoch, with RTK's own cadence constants and the reasoning for deriving rather than counting. It is in
        // Shared because the LOGIN server, a separate process with no World, stamps a new character's "Born in
        // ..." legend with the same date this server is showing.
        //
        // What World adds is the broadcast: RTK's change_time_char (map.c:1661) pushes clif_sendtime to every
        // connected session on each in-game hour, so we cache the calendar and watch for the hour to roll over.
        // Only hour+year go on the wire (see Session.SendTime); day/season are tracked because the year cadence
        // is defined in terms of them. Nothing reports the season to a player any more (@time is gone) — it
        // reaches them only through legend text (GameCalendar.Stamp) and whatever scripts read it.
        private int _hour, _day = 1, _season = 1, _year = 1;
        private long _gameHour = -1;          // whole in-game hours since the epoch; -1 = not yet synced
        private int? _hourOverride;           // @clock pin: when set, this hour REPLACES the derived one
        internal (byte hour, byte year) Time => ((byte)_hour, (byte)_year);
        internal string SeasonName => GameCalendar.SeasonName(_season);
        internal (int hour, int day, int year) ClockNow => (_hour, _day, _year);
        internal int? HourOverride => _hourOverride;

        /// <summary>Pin the shared in-game hour (@clock), or release it (null). The day/season/year keep
        /// deriving from the real epoch — only the HOUR is pinned, because the hour is what gates behavior
        /// (totem-time windows). Forcing <c>_gameHour = -1</c> makes the next tick's <see cref="Sync"/>
        /// report a change, so every session gets a fresh 0x20 within one tick in both directions.</summary>
        internal void SetHourOverride(int? hour)
        {
            lock (world._lock)
            {
                _hourOverride = hour;
                _gameHour = -1;
                if (hour is int h) _hour = h;   // immediate, so a readout or IsTotemTime right after is correct
            }
        }

        /// <summary>Re-read the calendar; true when the in-game hour changed, i.e. it is time to broadcast
        /// <c>0x20</c>. Caller holds <c>_lock</c>.</summary>
        internal bool Sync()
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            long gameHour = GameCalendar.HoursNow();
            if (gameHour == _gameHour) return false;
            _gameHour = gameHour;
            (_hour, _day, _season, _year) = GameCalendar.At(gameHour);
            if (_hourOverride is int oh) _hour = oh;   // @clock pin wins over the derived hour
            return true;
        }

        /// <summary>Whether the shared world clock is currently in <paramref name="totem"/>'s totem time
        /// (RTK isTotemTime) — the +5% kill-exp window. Reads the live hour; see <see cref="Content.IsTotemTime"/>.</summary>
        internal bool IsTotemTime(int totem) => Content.IsTotemTime(_hour, totem);
    }

    /// <summary>The world calendar — the cached in-game date, the hour-rollover watch the tick's phase (1.6)
    /// drives, and the <c>@clock</c> pin. Sessions read it directly (<c>_world.Clock.Time</c>); <c>World</c>
    /// keeps no forwarders, the same rule the spawn and movement extractions followed.</summary>
    internal WorldClock Clock { get; }
}
