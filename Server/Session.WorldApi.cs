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

    /// <summary>Immutable view of our player entity so a peer can draw us without racing our state — and,
    /// since #29, actually taken without racing it: <c>WeaponLook</c>/<c>ShieldLook</c>/<c>ArmorWireLook</c>
    /// all walk <c>_char.Equipment</c>, and the world tick builds this for every peer of every player on
    /// every beat while its owner may be equipping. The allocation-free guard is deliberate — this is the
    /// hottest cross-session call there is.</summary>
    public PlayerSnapshot Snapshot()
    {
        using var _ = EnterState();
        return new(_char.Id, _char.X, _char.Y, _facing, (byte)_char.Sex, FaceLook(), ArmorWireLook(_char.Armor), WeaponLook(), ShieldLook(), _char.Mounted, IsDead, _char.Name,
                   ArmorDye(), _morphLook, _morphColor, Stealthed, _char.HairColor);
    }

    /// <summary>Draw player <paramref name="other"/> on our client. Normally the 0x33 player-look form; while
    /// morphed (see CastMorph/Content.MorphSpells), reroutes to the SAME 0x07 Monster.epf creature-spawn a
    /// real mob uses — the confirmed client wall is 0x33-specific (every renderKind hardcodes the player
    /// archive), so this is the one packet shape that can actually show peers an animal sprite for us. The
    /// target id is still our real player id (never added to World's mob list), so clicking it keeps
    /// resolving through Online.ById, not the mob no-op path. Tradeoff: a 0x07 entity carries no name field.</summary>
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
    //
    // THE ORIGIN IS THE VIEWER'S, NOT THE ENTITY'S, so it is the same for every entity a single sweep tests
    // — and the sweeps are what this tick costs. At 400 players and 305 mobs on one map the reconcile runs
    // 400 viewers x (400 peers + 305 mobs) entity tests per beat, and the old shape recomputed the anchor
    // inside every one of them, twice per entity (once per pad): ~560,000 EdgeAwareAnchor calls a beat, for
    // 800 distinct answers. `(3) viewports` led 65 of the 66 slow beats in the 400-player hold at p50 119ms
    // of a p50 129ms beat (briefs/reports/load-run-2.md), which is what this is cutting. The rect is taken
    // once per sweep in <see cref="CurrentView"/> and the per-entity test is then four integer compares.
    //
    // ONE RECT PER SWEEP IS NOT ONE RECT PER DECISION. A sweep that takes the rect and then waits on
    // _viewLock decides on a tile the viewer may already have left, and that is not a cosmetic difference:
    // PR #240's reviewer showed a delayed mob sweep despawning a mob a completed walk reconcile had just
    // drawn, then redrawing it on the next beat (F1, HIGH). The per-entity shape this replaces never had
    // that problem, because it re-read the viewer's tile at every test. So the rect carries the value of a
    // per-session counter bumped by every write of this player's tile (Session.SetPositionUnderWorldLock,
    // the one seam all three world writers go through), and every decision re-compares it under _viewLock:
    // equal means the rect is still the viewer's current tile and the sweep keeps it; different means the
    // viewer moved and the rect is rebuilt. That is one anchor computation per sweep in the common case, one
    // int compare per entity, and one recomputation per step the viewer actually took.
    private readonly struct ViewRect
    {
        private readonly int _ox, _oy;

        /// <summary>The value <see cref="_viewGen"/> had when this rect was built. Compared, never
        /// interpreted — see <see cref="Reanchor"/>.</summary>
        internal readonly int Gen;

        internal ViewRect(int ox, int oy, int gen) { _ox = ox; _oy = oy; Gen = gen; }

        /// <summary>The identical test the per-entity <see cref="InView"/> ran, against an origin already
        /// computed. `pad` widens the rect (spawn early / despawn late).</summary>
        internal bool Contains(int mx, int my, int pad) =>
            mx >= _ox - pad && mx < _ox + ViewW + pad
         && my >= _oy - pad && my < _oy + ViewH + pad;
    }

    /// <summary>How many times this player's tile has been written. Incremented by
    /// <see cref="SetPositionUnderWorldLock"/> AFTER the two stores, which is the half of the handshake that
    /// matters: a reader that samples the counter BEFORE reading the tile and finds the same value later has
    /// seen no write complete in between. (A counter bumped before the stores would let exactly the stale
    /// read this exists to catch through.)</summary>
    private int _viewGen;

    /// <summary>This viewer's on-screen rect right now — one <see cref="ViewAnchor"/> read, for a whole
    /// sweep.</summary>
    private ViewRect CurrentView()
    {
        int gen = Volatile.Read(ref _viewGen);   // sampled BEFORE the tile read, so a write that lands after
        var (vx, vy) = ViewAnchor();             // this point is guaranteed to change the counter
        return new ViewRect(_char.X - vx, _char.Y - vy, gen);
    }

    /// <summary>Leave <paramref name="view"/> alone if the viewer has not moved since it was taken, rebuild it
    /// if it has. Called at every decision, under the <c>_viewLock</c> that decision is made under, so no
    /// decision can use a rect older than the viewer's tile at the moment it is made.
    ///
    /// <para>By reference, and that is a measurement rather than a style: this runs 705 times per viewer per
    /// beat at 400 players, and a form that RETURNED the rect copied twelve bytes at every one of them, which
    /// the whole-sweep scratch bench could see (the numbers are in briefs/reports/load-run-2-fix-1.md). In the
    /// common case — a viewer that did not move — this is one field read, one compare and a branch that is
    /// not taken.</para></summary>
    private void Reanchor(ref ViewRect view)
    {
        if (Volatile.Read(ref _viewGen) != view.Gen) view = CurrentView();
    }

    /// <summary>The single-entity form, for the callers that test one tile and are not in a sweep.</summary>
    private bool InView(int mx, int my, int pad) => CurrentView().Contains(mx, my, pad);

    /// <summary>Reconcile the mobs drawn on this client against what's in view: spawn (0x07) any that
    /// entered the camera rect, despawn (0x0E) any that left (with hysteresis so a mob loitering on the
    /// edge doesn't flicker). Called on world entry, after each of our walk steps, and every world tick.</summary>
    public void SyncMobs(IReadOnlyList<Mob> mobs)
    {
        using (EnterView())
        {
            // Inside the lock, not before it: the whole loop decides under this acquisition, so a sweep that
            // queued behind a walk reconcile anchors on the tile it finds when it gets in, not on the one the
            // viewer stood on when the tick reached this line (F1).
            var view = CurrentView();                        // once for the sweep, not once per mob per pad
            foreach (var m in mobs)
            {
                if (!m.Alive) continue;
                Reanchor(ref view);                          // the viewer walks on its own thread; it takes
                                                             // World._lock and its own monitor, not this one
                bool core = view.Contains(m.X, m.Y, ShowPad); // strict 17x15 — where a 0x07 is accepted
                if (!_shownMobs.Contains(m.Id))
                {
                    if (core) { ShowMob(m); _shownMobs.Add(m.Id); }
                }
                else if (!view.Contains(m.X, m.Y, HidePad))   // left the DRAWN 19x17 rect — now really gone
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
        // The @showwarps overlay markers ride along for the same reason: they span the whole map.
        GroundItem[]? markers = null;
        using (EnterView())
            if (_trapMarkers.Count > 0 || _warpMarkers.Count > 0)
                markers = _trapMarkers.Values.Concat(_warpMarkers).ToArray();
        var view = CurrentView();                            // once for the sweep, not once per item per pad
        foreach (var gi in markers is null ? items : items.Concat(markers))
        {
            bool shown;
            // The rect is re-anchored in the same acquisition that reads the tracking set, so this item's
            // decision and the state it is made against are both as of one moment (F1's shape, on items).
            using (EnterView()) { Reanchor(ref view); shown = _shownItems.Contains(gi.Id); }
            if (!shown)
            {
                if (view.Contains(gi.X, gi.Y, ShowPad)) ShowGroundItem(gi);
            }
            else if (!view.Contains(gi.X, gi.Y, HidePad))
            {
                using (EnterView()) _shownItems.Remove(gi.Id);
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
    public void SyncPeers(IReadOnlyList<PeerTile> peers)
    {
        var view = CurrentView();                            // once for the sweep, not once per peer per pad
        // By reference so a re-anchor one peer paid for is kept by the rest of the sweep — otherwise every
        // remaining peer would find the counter changed and rebuild the same rect again.
        foreach (var other in peers) ReconcilePeer(other, ref view);
    }

    /// <summary>Re-evaluate from scratch which peers WE can see — needed when OUR OWN state flips a per-viewer
    /// visibility rule. Dying in a PvP area lets us see the other ghosts; reviving takes that sight away again.
    /// Clears the tracking sets and re-runs SyncPeers over every peer on our map: ShowPlayer redraws the ones
    /// now visible and despawns the ones now hidden (it decides per viewer), so this both reveals and hides.
    /// Map changes get this for free via EnterMap; this covers an in-place death/revive that stays on the map.</summary>
    public void ResyncPeers()
    {
        using (EnterView()) { _shownPeers.Clear(); _edgePeers.Clear(); }
        SyncPeers(_world.View(this, _char.Map).peers);
    }

    /// <summary>Reconcile a SINGLE peer into our view (view-gated + tracked). Used when the world tells one
    /// client about one newcomer (World.EnterMap) so the newcomer is drawn only if in view AND recorded in
    /// _shownPeers — so a later step out of view despawns cleanly, like every other tracked entity.</summary>
    public void SyncPeer(PeerTile other)
    {
        var view = CurrentView();
        ReconcilePeer(other, ref view);
    }

    /// <summary>What reconciling one peer decided to do about them, once the bookkeeping is settled.</summary>
    private enum PeerDraw { Nothing, Show, Despawn }

    // DECIDE UNDER _viewLock, SEND OUTSIDE IT — the shape SyncGroundItems above already uses, and since #29 a
    // requirement rather than a style: ShowPlayer reads the SUBJECT through other.Snapshot(), which takes that
    // session's state monitor. Doing that with our own _viewLock held is one half of a genuine cycle, because
    // the other half exists too — a morph revert, a stealth revert or a death broadcasts DespawnEntity to every
    // peer from under the caster's OWN monitor, and DespawnEntity takes the recipient's _viewLock. Viewer B's
    // _viewLock waiting on subject A's monitor, against A's monitor waiting on B's _viewLock, is two threads
    // that never come back. Both are constant traffic: the tick reconciles viewports every beat.
    //
    // Mirrors SyncMobs' per-entity logic exactly (show inside the strict rect, despawn past the drawn rect with
    // _edgePeers hysteresis, re-assert on re-entry from the overdraw band). ShowPlayer itself decides
    // draw-vs-despawn for stealth/morph; we only gate on geometry here. The set updates happen at the same
    // points they always did — _shownPeers gains the id on a show, which ShowPlayer cannot fail — so the only
    // thing that moved is WHERE the packet is built.
    private void ReconcilePeer(PeerTile peer, ref ViewRect view)
    {
        var other = peer.Session;
        if (ReferenceEquals(other, this)) return;
        uint id = other.PlayerId;
        // The tile comes from the caller's snapshot, taken under World._lock with the peer list itself
        // (see World.PeerTile). Reading other.PlayerX/PlayerY here instead — which is what this did — is two
        // unsynchronised ushort reads of a character every writer of which holds that lock, so the pair could
        // be torn, and the two InView calls below could each catch a DIFFERENT pair. The drawn position is
        // unaffected either way: ShowPlayer takes it from other.Snapshot(), under the peer's own monitor.
        // `view` is OUR rect, taken once by the caller for the whole sweep (SyncPeers) rather than rebuilt per
        // pad inside each test. Same arithmetic, same answer for a viewer standing still — and for a viewer
        // walking on another thread both tests are taken from ONE re-anchored rect inside the acquisition
        // that decides, so they agree with each other AND with where the viewer is standing when they are
        // taken. There is no longer a point between the two tests where a step can land unseen.
        PeerDraw draw;
        using (EnterView())
        {
            Reanchor(ref view);                                    // no decision on a rect older than the step
            bool core = view.Contains(peer.X, peer.Y, ShowPad);    // strict 17x15 — where a 0x33 is accepted
            bool drawn = view.Contains(peer.X, peer.Y, HidePad);   // the wider 19x17 the client renders

            if (!_shownPeers.Contains(id))
            {
                if (!core) return;
                _shownPeers.Add(id);
                draw = PeerDraw.Show;
            }
            else if (!drawn)                                          // left the drawn rect — really gone
            {
                _shownPeers.Remove(id); _edgePeers.Remove(id);
                draw = PeerDraw.Despawn;
            }
            else if (core)
            {
                draw = _edgePeers.Remove(id) ? PeerDraw.Show          // back inside after loitering — re-assert
                                             : PeerDraw.Nothing;
            }
            else { _edgePeers.Add(id); return; }                      // in the band: keep drawn, flag suspect
        }

        if (draw == PeerDraw.Show) ShowPlayer(other);
        else if (draw == PeerDraw.Despawn) SendDespawn(id);
    }

    /// <summary>Reset the drawn-mob set (before a full 0x15 map rebuild, which drops all foreign entities
    /// client-side). The next SyncMobs/SyncPeers then re-streams everything currently in view.
    ///
    /// <para><c>_trapMarkers</c> goes with them: a revealed trap is a marker on THIS map, and the client just
    /// dropped every foreign entity. RTK's own markers are per-map floor items and die with the room the same
    /// way — which is the "stays until you leave the map" lifetime seeSpotTraps describes.</para></summary>
    // _warpMarkers goes with them too — but unlike trap markers, the @showwarps overlay SURVIVES as a toggle:
    // EnterMap and RedrawWorld re-stamp it for whatever map the client rebuilds, so only the stale marker set
    // dies here, not the feature.
    private void ForgetShownMobs() { using (EnterView()) { _shownMobs.Clear(); _edgeMobs.Clear(); _shownItems.Clear(); _shownPeers.Clear(); _edgePeers.Clear(); _trapMarkers.Clear(); _warpMarkers.Clear(); } }

    /// <summary>Rub out the spot-traps marker for one trap, if this client ever revealed it — RTK
    /// <c>removeTrapItem(npc)</c>, which every trap NPC calls right before deleting itself. Broadcast to the
    /// whole map by World when a trap goes off, so it is a no-op for everyone who never spotted that one.</summary>
    public void ClearTrapMarker(uint trapId)
    {
        GroundItem? marker;
        using (EnterView())
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
        using (EnterView()) return _trapMarkers.TryAdd(trapId, marker);
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
        if (_gm.ShowWarps) StampWarpMarkers();   // the rebuild dropped the @showwarps overlay — put it back
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
        using (EnterView()) { if (!_shownMobs.Contains(id)) return; }
        SendMove(id, x, y, dir);
    }
    // Turn a world MOB in place (0x11 side) — same shown-only guard as MoveMob.
    public void SideMob(uint id, byte side)
    {
        using (EnterView()) { if (!_shownMobs.Contains(id)) return; }
        SendSide(id, side);
    }
    public void SideEntity(uint id, byte side) => SendSide(id, side);                              // 0x11
    public void SpeakEntity(byte chatType, uint id, byte[] msg) => SendSpeech(chatType, id, msg);  // 0x0D
    /// <summary>Play a <c>0x1A</c> action over an entity on this client. The byte overload is the boundary
    /// the dynamic senders cross — the <c>@mobact</c> calibration probe and the mob swing type it sets —
    /// so it stays a raw byte and casts unchanged; peers rendering a KNOWN pose take the named overload.</summary>
    public void ActionOver(uint id, byte type, ushort time, byte param) => SendAction(id, (ActionType)type, time, param);  // 0x1A
    public void ActionOver(uint id, ActionType type, ushort time, byte param) => SendAction(id, type, time, param);        // 0x1A
    public void EffectOver(uint id, int effectId) => SendEffect(id, effectId);                      // 0x29 spell effect
    public void DespawnEntity(uint id) { using (EnterView()) { _shownMobs.Remove(id); _edgeMobs.Remove(id); _shownItems.Remove(id); _shownPeers.Remove(id); _edgePeers.Remove(id); } SendDespawn(id); }  // 0x0E

    // The one funnel every outbound packet in the server goes through, and the top half of the test seam:
    // it hands the frame to _out and does nothing transport-specific itself. TcpOutbound is a non-blocking
    // enqueue onto the bounded outbound channel — peer broadcasts and mob AI call this ON the shared
    // World.TickLoop thread, so it must never block, and the socket write happens on that transport's own
    // writer task. If the queue is full the client can't keep up with the world — drop IT, not the tick
    // thread. The single-reader channel preserves frame order, so bytes never interleave mid-packet (what
    // _sendLock did).
    // (The `_gameInc++` at call sites is a benign nonce and not guarded; a rare duplicate is harmless since
    // each packet carries its own inc in the header.)
    private void Send(byte[] data)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        Volatile.Write(ref _lastOutboundMs, Environment.TickCount64);   // silence watchdog
        if (data.Length > 3) LastOutboundOp = data[3];                  // aa | len_hi | len_lo | op
        if (_out.Send(data)) return;
        Log.Warn($"{_remote} outbound queue full ({_out.Capacity}) — dropping slow client");
        CloseConnection("slow client (outbound queue full)");
    }
}
