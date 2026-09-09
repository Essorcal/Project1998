using System.Diagnostics;

namespace Server;

// The weather half of World (#37, section 3). World.cs keeps the lock, the map table, the per-map last-sent
// cache (MapState.Weather) and the flush that puts 0x1F on the wire; this file keeps what DECIDES the sky:
// the admin overrides, the one resolution every reader shares, and the period-rollover sweep the tick's
// phase (1.7) runs. Nested in World, like SpawnDirector and MobAiTick, so it reaches _lock, _maps, MapState,
// Broadcast and HoldsWorldLock as they are without widening any of them — and so the override table stays
// private to the one type that reads it.
public sealed partial class World
{
    /// <summary>
    /// The world's weather overrides and its period watch. One per <see cref="World"/>, built by the
    /// constructor, and called at the same moments the code was called when it lived in World.cs:
    /// <see cref="For"/> from <see cref="EnterMap"/> and from every other reader here,
    /// <see cref="SweepPeriod"/> from the tick's phase (1.7), and <see cref="Get"/> /
    /// <see cref="Set"/> / <see cref="ClearOverride"/> from the sessions (<c>@weather</c>, map entry).
    ///
    /// <para><b>Lock discipline, unchanged by the move.</b> <see cref="Get"/>, <see cref="Set"/> and
    /// <see cref="ClearOverride"/> take <c>World._lock</c> themselves, exactly as they did on <c>World</c>;
    /// <see cref="BroadcastZone"/> collects under it and sends outside it; <see cref="For"/> and
    /// <see cref="SweepPeriod"/> take none and assert <see cref="HoldsWorldLock"/>, because every caller
    /// already holds it (<see cref="EnterMap"/>, the tick's acquisition, and the three methods above).</para>
    /// </summary>
    internal sealed class WeatherService
    {
        private const string LockNote = "WeatherService.For/SweepPeriod run under World._lock and nowhere else";

        private readonly World world;

        internal WeatherService(World world) => this.world = world;

        // ---- weather (opcode 0x1F / RTK clif_sendweather) ------------------------------------------
        // Weather is now a deterministic function of region-zone + time-period + season (see WeatherModel) rather
        // than the old per-map random roll: it is identical for every player, survives restarts, persists while a
        // player steps indoors, and is driven by the season. This world only (a) broadcasts a change when the
        // weather PERIOD rolls over for an active map and (b) holds optional admin OVERRIDES set via @weather.
        // 0=clear, 1=WRAIN(rain), 2=WSNOW(snow) — the three states the 4.95 client can draw.

        // Admin/debug weather overrides keyed by WeatherModel.ZoneOf(map). When present, a zone shows this state
        // instead of the seasonal model until "@weather auto" clears it (indoors still wins → clear). Guarded by _lock.
        private readonly Dictionary<int, byte> _weatherOverride = new();
        private long _lastWeatherPeriod = -1;          // last WeatherModel period broadcast; -1 forces the first tick to sync

        /// <summary>Current weather for a map (0=clear/1=rain/2=snow), for a player entering/re-entering it.
        /// Deterministic from the season + the map's region-zone + the time period (WeatherModel), unless an
        /// admin override is pinned on the zone; indoors is always clear. Needs no map to be "active".</summary>
        internal byte Get(ushort mapId) { lock (world._lock) return For(mapId); }

        // The weather a map should currently show, computed under _lock: clear indoors, else a zone override if
        // one is pinned, else the seasonal model. This is the single source of truth Get and the tick share.
        internal byte For(ushort mapId)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            if (Content.IsIndoor(mapId)) return WeatherModel.Clear;
            if (_weatherOverride.TryGetValue(WeatherModel.ZoneOf(mapId), out var forced)) return forced;
            return WeatherModel.For(mapId);
        }

        /// <summary>Pin a weather state onto a map's whole region-zone (the "@weather" admin lever) until
        /// <see cref="ClearOverride"/>. Broadcasts to everyone on any active map in that zone right away.</summary>
        internal void Set(ushort mapId, byte weather)
        {
            lock (world._lock) _weatherOverride[WeatherModel.ZoneOf(mapId)] = weather;
            BroadcastZone(mapId);
        }

        /// <summary>Drop a zone's admin override so it returns to the seasonal model, and re-broadcast the now-live
        /// weather to everyone on it.</summary>
        internal void ClearOverride(ushort mapId)
        {
            lock (world._lock) _weatherOverride.Remove(WeatherModel.ZoneOf(mapId));
            BroadcastZone(mapId);
        }

        // Re-broadcast the current weather to every active map sharing this map's zone, updating each map's
        // last-sent cache. Used after an override is set or cleared so the change lands immediately, not at the
        // next period rollover.
        private void BroadcastZone(ushort mapId)
        {
            int zone = WeatherModel.ZoneOf(mapId);
            List<(ushort map, byte w)> hits = new();
            lock (world._lock)
            {
                foreach (var (id, pm) in world._maps)
                {
                    if (pm.Players.Count == 0 || WeatherModel.ZoneOf(id) != zone) continue;
                    byte w = For(id);
                    pm.Weather = w;
                    hits.Add((id, w));
                }
            }
            foreach (var (id, w) in hits) world.Broadcast(id, p => p.SendWeather(w));
        }

        /// <summary>The tick's phase (1.7): when the deterministic weather PERIOD has rolled over, recompute
        /// every active map's weather and queue the ones whose sky actually changed. Caller holds
        /// <c>_lock</c>; the queued changes go on the wire in the flush, outside it.</summary>
        internal void SweepPeriod(TickQueues q)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            long period = WeatherModel.PeriodNow();
            if (period != _lastWeatherPeriod)
            {
                _lastWeatherPeriod = period;
                q.WeatherChanges = new List<(ushort, byte)>();
                foreach (var (mapId, pm) in world._maps)
                {
                    if (pm.Players.Count == 0) continue;
                    byte w = For(mapId);
                    if (w == pm.Weather) continue;
                    pm.Weather = w;
                    q.WeatherChanges.Add((mapId, w));
                }
            }
        }

        // ---- test seams (Tests/WorldClockWeatherTests.cs) --------------------------------------------
        // Kept beside the state they open rather than in the test project, for the same reason as
        // UnderWorldLockForTest: a reader of the override table should be able to see everything that writes it.

        /// <summary>Pin (or drop, with <c>null</c>) a zone override WITHOUT the eager broadcast
        /// <see cref="Set"/> and <see cref="ClearOverride"/> do — so a test can leave a map's last-sent cache
        /// stale and then watch <see cref="SweepPeriod"/> notice. Caller holds <c>_lock</c>.</summary>
        internal void PinOverrideForTest(ushort mapId, byte? weather)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            if (weather is byte w) _weatherOverride[WeatherModel.ZoneOf(mapId)] = w;
            else _weatherOverride.Remove(WeatherModel.ZoneOf(mapId));
        }

        /// <summary>Forget which period was last swept, so the next <see cref="SweepPeriod"/> runs instead of
        /// waiting out the ~15 real minutes to the next rollover. Caller holds <c>_lock</c>.</summary>
        internal void ForgetPeriodForTest()
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            _lastWeatherPeriod = -1;
        }
    }

    /// <summary>The weather — the admin overrides, the one resolution every reader shares, and the
    /// period-rollover sweep phase (1.7) drives. Sessions read it directly (<c>_world.Weather.Get(map)</c>);
    /// <c>World</c> keeps no forwarders, the same rule the spawn and movement extractions followed.</summary>
    internal WeatherService Weather { get; }
}
