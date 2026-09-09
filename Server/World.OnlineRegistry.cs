using System.Diagnostics;

namespace Server;

// The "who is logged in" half of World (#37, section 4). World.cs keeps the lock, the maps and the per-map
// Players lists; this file keeps the account-keyed online slot table behind the duplicate-login guard and the
// three lookups that walk those Players lists on behalf of everyone outside World (by name, by entity id, all
// of them, how many). Nested in World, like SpawnDirector and WorldClock, so it reaches _lock, _maps and
// HoldsWorldLock as they are without widening any of them — and so the slot table stays private to the one
// type that reads it.
public sealed partial class World
{
    /// <summary>
    /// The server-wide roster: the online-account slots the duplicate-login guard turns on, and the lookups
    /// that answer "which session is that?" for code outside <see cref="World"/>. One per <see cref="World"/>,
    /// built by the constructor, called at exactly the moments the code was called when it lived in World.cs.
    ///
    /// <para><b>Lock discipline, unchanged by the move.</b> <see cref="Register"/>, <see cref="Unregister"/>,
    /// <see cref="FindPlayer"/>, <see cref="ById"/>, <see cref="All"/> and <see cref="Count"/> take
    /// <c>World._lock</c> themselves, exactly as they did on <c>World</c>. <see cref="ByIdLocked"/> takes none
    /// and asserts <see cref="HoldsWorldLock"/>, which is the same <c>Monitor.IsEntered(_lock)</c> its old
    /// assert made — it is for callers already inside the lock. <see cref="All"/> is a SNAPSHOT taken under
    /// the lock and iterated outside it; the autosave sweep and the tick both depend on that, because a sweep
    /// that held <c>_lock</c> while flushing to SQLite would freeze the world.</para>
    /// </summary>
    internal sealed class OnlineRegistry
    {
        private const string LockNote = "OnlineRegistry.ByIdLocked runs under World._lock and nowhere else";

        private readonly World world;

        internal OnlineRegistry(World world) => this.world = world;

        // Server-wide online-account registry (independent of the per-map Players lists in World.cs, which a
        // session only joins AFTER its own arrival/load logic runs). Keyed by CharacterStore.Key(username).
        // Exists solely for the duplicate-login guard: Register lets HandleArrival atomically detect + evict a
        // stale session for the same account BEFORE loading, so a slow-to-unwind old session can never clobber
        // the new one's fresher save (SQLite's persistence is blind last-write-wins). Guarded by the same
        // _lock as everything else here — registration/eviction is rare (once per login), so sharing the lock
        // costs nothing measurable against the map operations.
        private readonly Dictionary<string, Session> _online = new();

        /// <summary>The connected player with this character name (case-insensitive, any map), or null if
        /// they're offline. Used by whisper/tell (RTK clif_parsewisp's target lookup).</summary>
        internal Session? FindPlayer(string name)
        {
            // CharName, not Snapshot().Name: this runs under _lock, and Snapshot takes the session's state
            // monitor, which is the wrong way round (#29 — session state THEN _lock). Building a whole
            // PlayerSnapshot — face, armour, weapon, shield, dye, all off the equipment list — per player per
            // lookup, to read one string, was never the intent either.
            lock (world._lock)
                return world._maps.Values.SelectMany(m => m.Players)
                                  .FirstOrDefault(p => string.Equals(p.CharName, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The connected player with this entity id (any map), or null. Used by click-profile's "view
        /// another player" path (RTK <c>clif_clickonplayer</c>, §9.5/§11l) and the exchange-initiate opcode
        /// <c>0x4A</c> (RTK <c>clif_parse_exchange</c> type 0), both of which address a player by id — the
        /// client already knows it from the entity it rendered — rather than by name.</summary>
        internal Session? ById(uint id)
        {
            lock (world._lock)
                return ByIdLocked(id);
        }

        /// <summary>Same lookup for callers that already hold <c>_lock</c>. The monitor is re-entrant so taking it
        /// twice would work, but saying which methods expect it is how this file stays readable.</summary>
        internal Session? ByIdLocked(uint id)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            return world._maps.Values.SelectMany(m => m.Players).FirstOrDefault(p => p.PlayerId == id);
        }

        /// <summary>Every connected player, across every map — a server-wide (not map-scoped) roster snapshot.
        /// Used by channels that reach beyond one map, like subpath chat (RTK clif_sendsubpathmessage loops
        /// every session, not just one map's block list).</summary>
        internal List<Session> All()
        {
            lock (world._lock)
                return world._maps.Values.SelectMany(m => m.Players).ToList();
        }

        /// <summary>How many players are in the world right now. Separate from <see cref="All"/> because
        /// the status publisher wants only the number, and materialising every session into a list on a timer to
        /// read <c>.Count</c> off it is pure garbage.</summary>
        internal int Count
        {
            get
            {
                lock (world._lock)
                {
                    var n = 0;
                    foreach (var m in world._maps.Values) n += m.Players.Count;
                    return n;
                }
            }
        }

        /// <summary>Duplicate-login guard: atomically register <paramref name="s"/> as the online session for
        /// <paramref name="key"/> (CharacterStore.Key(username)), returning whatever session previously held
        /// that slot via <paramref name="old"/> (null if this is a fresh login). Called from HandleArrival
        /// BEFORE the character is loaded from disk, so a second concurrent arrival for the same account can
        /// never both pass unnoticed — the dictionary write is atomic under _lock. The caller (HandleArrival)
        /// is responsible for kicking <paramref name="old"/> (Session.KickForReplacement) so its state is
        /// flushed before the new session's own Load runs.</summary>
        internal void Register(string key, Session s, out Session? old)
        {
            lock (world._lock)
            {
                _online.TryGetValue(key, out old);
                _online[key] = s;
            }
        }

        /// <summary>Remove <paramref name="s"/> from the online registry, but ONLY if it still owns that slot —
        /// a compare-and-remove so a session that was already kicked/replaced (Register overwrote its
        /// slot with the newer session) can't accidentally evict the session that replaced it when its own
        /// (now-stale) teardown finally runs.</summary>
        internal void Unregister(string key, Session s)
        {
            lock (world._lock)
            {
                if (_online.TryGetValue(key, out var cur) && ReferenceEquals(cur, s))
                    _online.Remove(key);
            }
        }

        /// <summary>Whether <paramref name="key"/>'s slot is held by <paramref name="s"/> right now — the
        /// compare half of <see cref="Unregister"/>'s compare-and-remove, exposed so a test can watch the slot
        /// change hands instead of inferring it from who gets kicked. Reads the same table under the same
        /// lock; nothing in production calls it.</summary>
        internal bool HoldsSlotForTest(string key, Session s)
        {
            lock (world._lock)
                return _online.TryGetValue(key, out var cur) && ReferenceEquals(cur, s);
        }
    }

    /// <summary>The online roster — the duplicate-login slot table and the lookups by name, by entity id and
    /// across the whole world. Sessions reach it directly (<c>_world.Online.FindPlayer(name)</c>);
    /// <c>World</c> keeps no forwarders, the same rule the spawn, movement and clock extractions followed.</summary>
    internal OnlineRegistry Online { get; }
}
