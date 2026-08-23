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
        new(_char.Id, _char.X, _char.Y, _facing, (byte)_char.Sex, FaceLook(), ArmorWireLook(_char.Armor), WeaponLook(), ShieldLook(), _char.Mounted, IsDead, _char.Name,
            ArmorDye(), _morphLook, _morphColor, Stealthed, _char.HairColor);

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
        // PvP ghost (Vale, Sire Pit, ...): a player killed in a PvP area is invisible to the LIVING, so an enemy
        // can't see or camp the corpse — but other GHOSTS still see them (and self sees self). So hide only when
        // the VIEWER (`this`) is alive. Same despawn-not-draw shape as stealth. See PvpGhostHidden. A ghost still
        // sees the living too (a living subject isn't PvpGhostHidden, so this gate never fires for them).
        if (other.PvpGhostHidden && !ReferenceEquals(other, this) && !IsDead) { DespawnEntity(s.Id); return; }
        if (s.MorphLook != 0) { SendCreatureList(new[] { (s.Id, (ushort)(0x8000 | s.MorphLook), s.X, s.Y, s.MorphColor, s.Dir) }); return; }
        var app = new byte[] { s.Sex, (byte)(s.Dead ? 1 : s.Faded ? 5 : s.Mounted ? 3 : 0), s.Face, s.Armor, s.ArmorColor, s.Weapon, s.Shield };   // [1]=form (5=invisible-spell/faded), [4]=war-paint dye
        // The nameplate is drawn straight off this string, so an empty name is the whole "hide nameplates"
        // mechanism — server-side, no client patch (see Content.ShowNameplates).
        string plate = Content.ShowNameplates ? s.Name : "";
        // hairColor from the SUBJECT's snapshot — `this` is the viewer, so AppearanceFor must NOT read our own.
        SendLook(s.Id, s.X, s.Y, s.Dir, app, renderKind: 1, plate, $"peer(0x33) id={s.Id} '{s.Name}'", hairColor: s.HairColor);
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
                bool core = InView(m.X, m.Y, ShowPad);        // strict 17x15 — where a 0x07 is accepted
                if (!_shownMobs.Contains(m.Id))
                {
                    if (core) { ShowMob(m); _shownMobs.Add(m.Id); }
                }
                else if (!InView(m.X, m.Y, HidePad))          // left the DRAWN 19x17 rect — now really gone
                {
                    SendDespawn(m.Id); _shownMobs.Remove(m.Id); _edgeMobs.Remove(m.Id);
                }
                else if (core)
                {
                    // Back inside the strict rect after loitering in the overdraw band. We don't know whether
                    // the client culled it out there, so re-send the spawn: 0x07 on a live id is an in-place
                    // update, and this is strictly cheaper than the despawn+respawn pair the old HidePad=0
                    // sent on every boundary crossing.
                    if (_edgeMobs.Remove(m.Id)) ShowMob(m);
                }
                else _edgeMobs.Add(m.Id);                     // in the band: keep it drawn, flag it as suspect
            }
        }
    }

    /// <summary>Reconcile the FLOOR ITEMS drawn on this client against what's in view — the ground-item twin
    /// of <see cref="SyncMobs"/>, and needed for exactly the same reason: <see cref="ShowGroundItem"/> draws
    /// through the viewport-gated 0x07 static-object path, so a 0x07 for an off-screen tile is discarded by
    /// the client. Items never move, so a discarded draw is permanent — which is why forage drops
    /// (chestnuts, scattered across a box far bigger than a screen) and loot dropped across the map read as
    /// "not spawning". Called on world entry, after each of our walk steps, and every world tick.
    ///
    /// <para>No <c>_edgeMobs</c> equivalent: a stationary item can only leave or enter the band by US moving,
    /// and re-showing it is a single idempotent 0x07, so the plain show/hide pair is enough.</para></summary>
    public void SyncGroundItems(IReadOnlyList<GroundItem> items)
    {
        // Our own spot-traps markers ride along: they are drawn through the identical viewport-gated 0x07
        // path, and the reveal radius (15) is nearly twice the view rect, so they need the same walk-into-view
        // draw the world's floor items get. They live only on this session — no other client ever sees them.
        GroundItem[]? markers = null;
        lock (_viewLock) if (_trapMarkers.Count > 0) markers = _trapMarkers.Values.ToArray();
        foreach (var gi in markers is null ? items : items.Concat(markers))
        {
            bool shown;
            lock (_viewLock) shown = _shownItems.Contains(gi.Id);
            if (!shown)
            {
                if (InView(gi.X, gi.Y, ShowPad)) ShowGroundItem(gi);
            }
            else if (!InView(gi.X, gi.Y, HidePad))
            {
                lock (_viewLock) _shownItems.Remove(gi.Id);
                SendDespawn(gi.Id);
            }
        }
    }

    /// <summary>Reconcile the PEER players drawn on this client against what's in view — the player twin of
    /// <see cref="SyncMobs"/>, and needed for the same reason: <see cref="ShowPlayer"/> draws through the
    /// viewport-gated 0x33 look path, so a draw for an off-screen peer is dropped by the client. Peers only
    /// move (or we do), so without this a peer we entered the map too far from, or who walks toward us from
    /// off-screen, is invisible forever until a room change or Ctrl+R re-draws them in view — the reported
    /// "can't see users I walk up to". Called on world entry, after each of our walk steps, and every world
    /// tick — the same three sites as SyncMobs. Self is skipped.</summary>
    public void SyncPeers(IReadOnlyList<Session> peers)
    {
        lock (_viewLock)
            foreach (var other in peers)
                ReconcilePeer(other);
    }

    /// <summary>Re-evaluate from scratch which peers WE can see — needed when OUR OWN state flips a per-viewer
    /// visibility rule. Dying in a PvP area lets us see the other ghosts; reviving takes that sight away again.
    /// Clears the tracking sets and re-runs SyncPeers over every peer on our map: ShowPlayer redraws the ones
    /// now visible and despawns the ones now hidden (it decides per viewer), so this both reveals and hides.
    /// Map changes get this for free via EnterMap; this covers an in-place death/revive that stays on the map.</summary>
    public void ResyncPeers()
    {
        lock (_viewLock) { _shownPeers.Clear(); _edgePeers.Clear(); }
        SyncPeers(_world.View(this, _char.Map).peers);
    }

    /// <summary>Reconcile a SINGLE peer into our view (view-gated + tracked). Used when the world tells one
    /// client about one newcomer (World.EnterMap) so the newcomer is drawn only if in view AND recorded in
    /// _shownPeers — so a later step out of view despawns cleanly, like every other tracked entity.</summary>
    public void SyncPeer(Session other) { lock (_viewLock) ReconcilePeer(other); }

    // Caller holds _viewLock. Mirrors SyncMobs' per-entity logic exactly (show inside the strict rect, despawn
    // past the drawn rect with _edgePeers hysteresis, re-assert on re-entry from the overdraw band). ShowPlayer
    // itself decides draw-vs-despawn for stealth/morph; we only gate on geometry here.
    private void ReconcilePeer(Session other)
    {
        if (ReferenceEquals(other, this)) return;
        uint id = other.PlayerId;
        bool core = InView(other.PlayerX, other.PlayerY, ShowPad);   // strict 17x15 — where a 0x33 is accepted
        if (!_shownPeers.Contains(id))
        {
            if (core) { ShowPlayer(other); _shownPeers.Add(id); }
        }
        else if (!InView(other.PlayerX, other.PlayerY, HidePad))      // left the drawn 19x17 rect — really gone
        {
            SendDespawn(id); _shownPeers.Remove(id); _edgePeers.Remove(id);
        }
        else if (core)
        {
            if (_edgePeers.Remove(id)) ShowPlayer(other);            // back inside after loitering — re-assert
        }
        else _edgePeers.Add(id);                                      // in the band: keep drawn, flag suspect
    }

    /// <summary>Reset the drawn-mob set (before a full 0x15 map rebuild, which drops all foreign entities
    /// client-side). The next SyncMobs/SyncPeers then re-streams everything currently in view.
    ///
    /// <para><c>_trapMarkers</c> goes with them: a revealed trap is a marker on THIS map, and the client just
    /// dropped every foreign entity. RTK's own markers are per-map floor items and die with the room the same
    /// way — which is the "stays until you leave the map" lifetime seeSpotTraps describes.</para></summary>
    private void ForgetShownMobs() { lock (_viewLock) { _shownMobs.Clear(); _edgeMobs.Clear(); _shownItems.Clear(); _shownPeers.Clear(); _edgePeers.Clear(); _trapMarkers.Clear(); } }

    /// <summary>Rub out the spot-traps marker for one trap, if this client ever revealed it — RTK
    /// <c>removeTrapItem(npc)</c>, which every trap NPC calls right before deleting itself. Broadcast to the
    /// whole map by World when a trap goes off, so it is a no-op for everyone who never spotted that one.</summary>
    public void ClearTrapMarker(uint trapId)
    {
        GroundItem? marker;
        lock (_viewLock)
        {
            if (!_trapMarkers.Remove(trapId, out marker)) return;
            if (!_shownItems.Remove(marker.Id)) return;   // never made it past the viewport gate — nothing drawn to erase
        }
        SendDespawn(marker.Id);
    }

    /// <summary>Register a spot-traps marker on a revealed trap's tile — one per TRAP, so re-casting over the
    /// same ground re-marks it instead of piling a second sword on the tile. Returns false if that trap was
    /// already marked. The DRAW is left to <see cref="SyncGroundItems"/> so a trap revealed beyond the view
    /// rect is drawn when we walk to it rather than thrown away by the 0x07 gate.</summary>
    public bool AddTrapMarker(uint trapId, GroundItem marker)
    {
        lock (_viewLock) return _trapMarkers.TryAdd(trapId, marker);
    }

    /// <summary>Re-assert every co-located peer + mob on OUR client. Call after re-sending 0x15 mapinfo
    /// in place (the realm-center refresh), which makes the client rebuild the map and drop all FOREIGN
    /// entities — without this the other players/mobs silently vanish until they next move.</summary>
    private void RedrawWorld()
    {
        var (peers, mobs) = _world.View(this, _char.Map);
        ForgetShownMobs();     // the 0x15 rebuild dropped ALL foreign entities client-side — re-stream in view
        SyncPeers(peers);
        SyncMobs(mobs);
        SyncGroundItems(_world.ItemsOn(_char.Map));
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
    public void DespawnEntity(uint id) { lock (_viewLock) { _shownMobs.Remove(id); _edgeMobs.Remove(id); _shownItems.Remove(id); _shownPeers.Remove(id); _edgePeers.Remove(id); } SendDespawn(id); }  // 0x0E

    // Non-blocking enqueue. Peer broadcasts and mob AI call this ON the shared World.TickLoop thread, so it
    // must never block: it just hands the frame to the outbound channel (WriterLoop does the socket write).
    // If the queue is full the client can't keep up with the world — drop IT, not the tick thread. The
    // single-reader channel preserves frame order, so bytes never interleave mid-packet (what _sendLock did).
    // (The `_gameInc++` at call sites is a benign nonce and not guarded; a rare duplicate is harmless since
    // each packet carries its own inc in the header.)
    private void Send(byte[] data)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        long nowMs = Environment.TickCount64;
        Volatile.Write(ref _lastOutboundMs, nowMs);            // silence watchdog
        if (data.Length > 3) LastOutboundOp = data[3];         // aa | len_hi | len_lo | op
        // Stamped on enqueue so WriterLoop can report how long the frame sat here before it hit the socket.
        if (_outbound.Writer.TryWrite(new Outbound(data, nowMs))) return;
        Log.Info($"!! {_remote} outbound queue full ({OutboundCapacity}) — dropping slow client");
        CloseConnection("slow client (outbound queue full)");
    }
}
