using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Protocol.Tk495;
using Shared;

namespace Server;

public sealed partial class Session
{

    // ===== shared-world API =====================================================================
    // Called by the World (mob AI, broadcasts) and by PEER sessions to render entities on THIS client.
    // All wrap the existing private packet builders, so cross-session sends go out through the same
    // locked Send() as our own — no interleaving on the wire.

    /// <summary>This player's runtime entity id / position, read by the world for AI + broadcasts.</summary>
    public uint   PlayerId => _char.Id;
    public ushort PlayerX  => _char.X;
    public ushort PlayerY  => _char.Y;

    /// <summary>Immutable view of our player entity so a peer can draw us without racing our state.</summary>
    public PlayerSnapshot Snapshot() =>
        new(_char.Id, _char.X, _char.Y, _facing, (byte)_char.Sex, FaceLook(), _char.Armor, WeaponLook(), ShieldLook(), _char.Mounted, IsDead, _char.Name,
            ArmorDye(), _morphLook, _morphColor, Stealthed);

    /// <summary>Draw player <paramref name="other"/> on our client. Normally the 0x33 player-look form; while
    /// morphed (see CastMorph/Content.MorphSpells), reroutes to the SAME 0x07 Monster.epf creature-spawn a
    /// real mob uses — the confirmed client wall is 0x33-specific (every renderKind hardcodes the player
    /// archive), so this is the one packet shape that can actually show peers an animal sprite for us. The
    /// target id is still our real player id (never added to World's mob list), so clicking it keeps
    /// resolving through PlayerById, not the mob no-op path. Tradeoff: a 0x07 entity carries no name field.</summary>
    public void ShowPlayer(Session other)
    {
        var s = other.Snapshot();
        // Stealth (RTK PC_INVIS): the caster is visible ONLY to themselves + group members (who see them faded,
        // form 5); everyone else sees nothing at all. `this` is the viewer, `other` the subject — so a non-group
        // viewer gets a despawn instead of a draw (idempotent; also covers a rogue who was visible then vanished).
        if (s.Faded && !ReferenceEquals(other, this) && !SharesGroup(other)) { DespawnEntity(s.Id); return; }
        if (s.MorphLook != 0) { SendCreatureList(new[] { (s.Id, (ushort)(0x8000 | s.MorphLook), s.X, s.Y, s.MorphColor, s.Dir) }); return; }
        var app = new byte[] { s.Sex, (byte)(s.Dead ? 1 : s.Faded ? 5 : s.Mounted ? 3 : 0), s.Face, s.Armor, s.ArmorColor, s.Weapon, s.Shield };   // [1]=form (5=invisible-spell/faded), [4]=war-paint dye
        // The nameplate is drawn straight off this string, so an empty name is the whole "hide nameplates"
        // mechanism — server-side, no client patch (see Content.ShowNameplates).
        string plate = Content.ShowNameplates ? s.Name : "";
        SendLook(s.Id, s.X, s.Y, s.Dir, app, renderKind: 1, plate, $"peer(0x33) id={s.Id} '{s.Name}'");
    }

    /// <summary>Do the viewer (this) and <paramref name="other"/> share a party? (Used to gate who can see a
    /// stealthed player — self + group only.)</summary>
    private bool SharesGroup(Session other) => _party is not null && ReferenceEquals(_party, other._party);

    /// <summary>Draw shared mob <paramref name="m"/> on our client (0x07 Monster.epf spawn).</summary>
    private void ShowMob(Mob m) =>
        SendCreatureList(new[] { (m.Id, (ushort)(0x8000 | m.Sprite), m.X, m.Y, m.Color, m.Dir) });

    // The map rect currently on screen: viewport is 17 wide x 15 tall, self drawn at the camera anchor, so
    // the top-left visible tile is (X - vx, Y - vy). ViewAnchor() is the SAME anchor the 0x04 camera uses
    // (edge-aware follow, or the frozen origin under realm-center), so this matches the client's real view.
    // `pad` widens the rect (spawn early / despawn late).
    private bool InView(int mx, int my, int pad)
    {
        var (vx, vy) = ViewAnchor();
        int ox = _char.X - vx, oy = _char.Y - vy;
        return mx >= ox - pad && mx < ox + 17 + pad
            && my >= oy - pad && my < oy + 15 + pad;
    }

    /// <summary>Reconcile the mobs drawn on this client against what's in view: spawn (0x07) any that
    /// entered the camera rect, despawn (0x0E) any that left (with hysteresis so a mob loitering on the
    /// edge doesn't flicker). Called on world entry, after each of our walk steps, and every world tick.</summary>
    public void SyncMobs(IReadOnlyList<Mob> mobs)
    {
        lock (_viewLock)
        {
            foreach (var m in mobs)
            {
                if (!m.Alive) continue;
                bool shown = _shownMobs.Contains(m.Id);
                if (!shown && InView(m.X, m.Y, ShowPad)) { ShowMob(m); _shownMobs.Add(m.Id); }
                else if (shown && !InView(m.X, m.Y, HidePad)) { SendDespawn(m.Id); _shownMobs.Remove(m.Id); }
            }
        }
    }

    /// <summary>Reset the drawn-mob set (before a full 0x15 map rebuild, which drops all foreign entities
    /// client-side). The next SyncMobs then re-streams everything currently in view.</summary>
    private void ForgetShownMobs() { lock (_viewLock) _shownMobs.Clear(); }

    /// <summary>Re-assert every co-located peer + mob on OUR client. Call after re-sending 0x15 mapinfo
    /// in place (the realm-center refresh), which makes the client rebuild the map and drop all FOREIGN
    /// entities — without this the other players/mobs silently vanish until they next move.</summary>
    private void RedrawWorld()
    {
        var (peers, mobs) = _world.View(this, _char.Map);
        foreach (var p in peers) ShowPlayer(p);
        ForgetShownMobs();     // the 0x15 rebuild dropped them client-side — re-stream what's in view
        SyncMobs(mobs);
        foreach (var gi in _world.ItemsOn(_char.Map)) ShowGroundItem(gi);
    }

    // Move a peer entity one step. (x,y) is the SOURCE tile — the client's 0x0C overshoots one tile past it
    // in `dir`, so anchoring on the source lands the peer on the true destination. See HandleWalk / MoveMob.
    public void MoveEntity(uint id, ushort x, ushort y, byte dir) => SendMove(id, x, y, dir);      // 0x0C
    // Move a world MOB one step. (x,y) is the mob's SOURCE tile, not the destination: the 4.95 client's
    // 0x0C walk ends one tile past the packet tile in `dir` (forward-slide overshoot), so anchoring on the
    // source makes it land on the true destination. See World.Tick's move broadcast for the full rationale.
    // Skips clients that don't have the mob in view (the client ignores a 0x0C for an unknown entity anyway,
    // so this just spares the wire on a big map); SyncMobs draws it once it enters view.
    public void MoveMob(uint id, ushort x, ushort y, byte dir)
    {
        lock (_viewLock) { if (!_shownMobs.Contains(id)) return; }
        SendMove(id, x, y, dir);
    }
    // Turn a world MOB in place (0x11 side) — same shown-only guard as MoveMob.
    public void SideMob(uint id, byte side)
    {
        lock (_viewLock) { if (!_shownMobs.Contains(id)) return; }
        SendSide(id, side);
    }
    public void SideEntity(uint id, byte side) => SendSide(id, side);                              // 0x11
    public void SpeakEntity(byte chatType, uint id, byte[] msg) => SendSpeech(chatType, id, msg);  // 0x0D
    public void ActionOver(uint id, byte type, ushort time, byte param) => SendAction(id, type, time, param);  // 0x1A
    public void EffectOver(uint id, int effectId) => SendEffect(id, effectId);                      // 0x29 spell effect
    public void DespawnEntity(uint id) { lock (_viewLock) _shownMobs.Remove(id); SendDespawn(id); }  // 0x0E

    // Non-blocking enqueue. Peer broadcasts and mob AI call this ON the shared World.TickLoop thread, so it
    // must never block: it just hands the frame to the outbound channel (WriterLoop does the socket write).
    // If the queue is full the client can't keep up with the world — drop IT, not the tick thread. The
    // single-reader channel preserves frame order, so bytes never interleave mid-packet (what _sendLock did).
    // (The `_gameInc++` at call sites is a benign nonce and not guarded; a rare duplicate is harmless since
    // each packet carries its own inc in the header.)
    private void Send(byte[] data)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        if (_outbound.Writer.TryWrite(data)) return;
        Log.Info($"!! {_remote} outbound queue full ({OutboundCapacity}) — dropping slow client");
        CloseConnection("slow client (outbound queue full)");
    }
}
