using Protocol.Tk495;
using Server;
using Shared;
using Xunit;

namespace Tests.Support;

/// <summary>
/// One <see cref="World"/> and as many socket-free <see cref="Session"/>s as a test wants — the setup half of
/// the seam added for #27.
///
/// <para>The World is CONSTRUCTED and never started: no tick thread, no autosave sweep, no watchdog, no
/// restart ladder, no status writer (see <c>World.Start</c>). Nothing in this process moves unless a test
/// moves it, so an assertion cannot race the world. It is shared across the collection because building one
/// walks the whole spawn roster, and it is a class fixture rather than a static so xUnit — not a hand-rolled
/// double-checked lock — owns when it is built.</para>
///
/// <para>The character store is the redirected test one (#23: <c>TestProcessState</c> points
/// <c>P1998_STATE</c> at a temp directory before anything loads), so a session that autosaves cannot reach
/// the live store.</para>
/// </summary>
public sealed class SessionFixture
{
    /// <summary>Kugnae, inside Ironheart's Home — the tile a brand new character starts on. Chosen for the
    /// obvious reason: it is a small indoor map with nothing spawning on it, so entering it does not drag a
    /// hunting map's worth of mobs into a trade test.</summary>
    public const ushort HomeMap = 36;

    public World World { get; }
    public CharacterStore Store { get; }

    public SessionFixture()
    {
        TestProcessState.LoadContent();   // World's constructor builds its spawn roster from Content
        World = new World();
        Store = new CharacterStore(Path.Combine(TestProcessState.StateDirectory, "chars"));
    }

    /// <summary>A session with no socket, standing on <paramref name="map"/> and registered with the world so
    /// the id lookups handlers do (<c>World.PlayerById</c>) can find it. Returns its recorder with the
    /// world-entry chatter already cleared, so a test sees only what its own packet caused.</summary>
    public (Session session, RecordingOutbound outbound) Player(
        string name, ushort map = HomeMap, ushort x = 5, ushort y = 10)
    {
        var (session, outbound, _) = PlayerWith(name, _ => { }, map, x, y);
        return (session, outbound);
    }

    /// <summary>The same session, plus the <see cref="Character"/> behind it and a hook to shape that
    /// character BEFORE the session is built — stats, gear, position. A test that asserts on damage needs
    /// both halves: the hook to set up an HP pool and an AC worth netting, and the character itself to read
    /// the HP back (the session deliberately exposes no setter for it).</summary>
    public (Session session, RecordingOutbound outbound, Character character) PlayerWith(
        string name, Action<Character> configure, ushort map = HomeMap, ushort x = 5, ushort y = 10)
    {
        var character = new Character
        {
            Id = World.AllocatePlayerId(),
            Name = name,
            Map = map,
            X = x,
            Y = y,
        };
        configure(character);

        var outbound = new RecordingOutbound($"recorder:{name}");
        // Port 2005 is the 4.95 game port, which is what tags the session ClientVersion.V495 — the version
        // whose packet layouts the existing wire tests pin.
        var session = new Session(outbound, port: 2005, Store, World, character);
        World.EnterMap(session, map);
        outbound.Clear();
        return (session, outbound, character);
    }

    /// <summary>Frame a client-&gt;server packet the way the client does: encrypt the body with the login-key
    /// cipher, then wrap it in <c>AA | len | op | inc</c>. Handed to <c>Session.Receive</c>, this is the same
    /// byte sequence the read loop would have pulled off a socket.</summary>
    public static byte[] Frame(byte opcode, byte[] body, byte inc = 0) =>
        TkPacket.Build(opcode, inc, TkCrypt.Crypt(body, inc, TkCrypt.LoginKey));
}

/// <summary>Everything sharing the one unstarted World runs in this collection, so tests never mutate the
/// same map from two threads.</summary>
[CollectionDefinition("world")]
public sealed class WorldCollection : ICollectionFixture<SessionFixture> { }
