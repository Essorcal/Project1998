namespace Server;

/// <summary>
/// Per-tile door configuration: lock state (+ which item, if any, opens it) and whether a tile should simply
/// never block movement regardless of its graphic. RTK's own door script (<c>open.lua</c> <c>openDoors</c>)
/// only defines a handful of closed↔open OBJECT-ID pairs — most doors just swap graphics via
/// <see cref="Session"/>'s <c>DoorToggle</c> table, which is a straight port of that script. Some doors RTK
/// ships (e.g. Buya Salon's, object ids 356/357) have NO open-graphic pair defined anywhere in that script at
/// all — there's no "open" sprite to swap to — so they're configured <c>ForceOpen</c> here instead of faking
/// a toggle: the tile simply never blocks, whatever its graphic looks like.
///
/// Locking is a separate axis from the RTK open/close graphic swap (mirrors RTK's own two lock flavours —
/// <c>open.lua</c>'s <c>doorLockedCheck</c> per-map "always locked" rooms, and the Iron Lab treasure chest's
/// <c>iron_key</c> consume-to-open). A door can be configured Locked with an optional required item key; once
/// unlocked (or with no key configured at all, just a flat "no entry") it either falls through to the normal
/// open/close toggle or, combined with ForceOpen, is simply walkable from then on.
///
/// Runtime unlock state is process-wide, same lifetime as a toggled door's object-id mutation in
/// <see cref="MapData"/> (resets on <c>@reload</c> / restart) — keyed by the tile the player interacts with
/// (matches <c>Session.HandleOpen</c>'s faced tile fx,fy).
/// </summary>
public static class Doors
{
    /// <summary>Per-tile door configuration. <c>ClosedObjs</c>/<c>OpenObjs</c> give a door an EXPLICIT
    /// open/closed object run, independent of RTK's id-swap table — needed for event doors (carnage,
    /// bloodlust, foxhunt, elixir), whose ids aren't in <c>open.lua</c> and which have to be openable
    /// programmatically as well as with a key. The run starts at this tile and runs right; both arrays must
    /// be the same length. <c>DefaultClosed</c> stamps <c>ClosedObjs</c> on at load so the door starts shut
    /// however the shipped map draws it — pair it with <c>Locked</c> for "shut and locked until the event
    /// opens it".</summary>
    public sealed record DoorConfig(
        bool Locked = false,
        string? Key = null,
        bool ConsumeKey = true,
        bool ForceOpen = false,
        ushort[]? ClosedObjs = null,
        ushort[]? OpenObjs = null,
        bool DefaultClosed = false);

    // Per-tile door config, loaded from game-data/Doors.csv by Content.Load (via SetConfig) and swapped on
    // @reload. Starts empty; a missing file just means no configured doors (plain RTK open/close toggle, no lock).
    // e.g. the Buya Salon entrance (map 330, tiles 118/119,133 -> "Buya Salon"): object ids 356/357 aren't in
    // RTK's open.lua table yet SObj.tbl flags them solid on every side, so 'o' silently no-opped — "locked open"
    // (ForceOpen) per user direction makes the warp tile always walkable.
    private static Dictionary<(ushort map, ushort x, ushort y), DoorConfig> Config = new();

    /// <summary>Replace the door config table (Content.Load / @reload). Reference assignment is atomic, so a
    /// concurrent reader always sees a whole old-or-new table.</summary>
    internal static void SetConfig(Dictionary<(ushort map, ushort x, ushort y), DoorConfig> config) => Config = config;

    private static readonly HashSet<(ushort map, ushort x, ushort y)> Unlocked = new();
    private static readonly object Lock = new();

    /// <summary>This tile's door config, or null if it isn't configured (falls back to the plain RTK
    /// open/close toggle with no lock).</summary>
    public static DoorConfig? For(ushort map, ushort x, ushort y) => Config.TryGetValue((map, x, y), out var c) ? c : null;

    /// <summary>Does this tile force-bypass ALL movement collision (ground pass + object wall alike),
    /// regardless of its current graphic? See <see cref="MapData.BlockedMove"/>.</summary>
    public static bool IsForceOpen(ushort map, ushort x, ushort y) => For(map, x, y)?.ForceOpen == true;

    /// <summary>Every ForceOpen tile on a map — the 4.95 client never streams map/object data from us except
    /// via 0x06 cell-patches (it loads its own local .map file for everything else), so a server-side-only
    /// bypass is invisible to the client until we explicitly patch these tiles on every entry. See
    /// <c>Session.EnterMap</c> / <c>Session.SyncForceOpenDoors</c>.</summary>
    public static IEnumerable<(ushort x, ushort y)> ForceOpenTiles(ushort map) =>
        Config.Where(kv => kv.Key.map == map && kv.Value.ForceOpen).Select(kv => (kv.Key.x, kv.Key.y));

    /// <summary>Every door on a map configured to START closed, as (startX, y, closedObjs) runs — applied as
    /// an AUTHORED override in <see cref="MapData.Load"/>. Rows without a ClosedObj run are skipped: there is
    /// nothing to stamp on, and guessing a closed graphic from the shipped one is how you get an unopenable
    /// wall.</summary>
    public static IEnumerable<(ushort x, ushort y, ushort[] objs)> DefaultClosedRuns(ushort map) =>
        Config.Where(kv => kv.Key.map == map && kv.Value.DefaultClosed && kv.Value.ClosedObjs is { Length: > 0 })
              .Select(kv => (kv.Key.x, kv.Key.y, kv.Value.ClosedObjs!));

    /// <summary>Has this locked tile been unlocked? Survives restarts (<c>map_unlocks</c>) — a door whose key
    /// was consumed must not relock, or the key is gone and the door is shut again.</summary>
    public static bool IsUnlocked(ushort map, ushort x, ushort y) { lock (Lock) return Unlocked.Contains((map, x, y)); }

    /// <summary>Mark a locked tile unlocked, write-through to the database.</summary>
    public static void Unlock(ushort map, ushort x, ushort y)
    {
        lock (Lock) { if (!Unlocked.Add((map, x, y))) return; }
        MapStore.SetUnlocked(map, x, y, true);
    }

    /// <summary>Re-lock a tile (event reset), write-through to the database.</summary>
    public static void Relock(ushort map, ushort x, ushort y)
    {
        lock (Lock) { if (!Unlocked.Remove((map, x, y))) return; }
        MapStore.SetUnlocked(map, x, y, false);
    }

    /// <summary>Load the persisted unlock set. Called at startup and on <c>@reload</c>; replaces whatever is
    /// in memory, so a row deleted straight out of the DB takes effect on reload.</summary>
    public static void LoadUnlocks()
    {
        var rows = MapStore.Unlocks();
        lock (Lock)
        {
            Unlocked.Clear();
            foreach (var (m, x, y) in rows) Unlocked.Add((m, x, y));
        }
        if (rows.Count > 0) Log.Info($"   -> restored {rows.Count} unlocked door tile(s)");
    }
}
