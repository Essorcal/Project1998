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
        // THE DRAWN SET, made true to what this client is about to hold. ShowPlayer is the one peer draw that
        // does NOT come through DecidePeerUnderViewLock: RefreshAppearance (Session.Entity.cs), CastMorph and
        // RevertMorph (Session.Spells.cs) each broadcast DespawnEntity and then ShowPlayer to every session on
        // the map, and the despawn takes the id OUT of the recipient's _drawnPeers while the draw put it back
        // on the recipient's screen. That left a window of up to one beat — until the tick sweep's first-show
        // branch re-added it — in which the client held the entity and the store said it did not. Harmless
        // while MoveEntity sent unconditionally; with the gate below it would drop every 0x0C for that peer in
        // the window, so the peer would freeze and then snap. (reviews/PR263-by-opus.md, F1.)
        //
        // DrawnBand, not DrawnInside, and the difference is a frame: "drawn, and re-assert on the next sweep"
        // is what the store has to say here, because that re-assert is the 0x33 the next sweep sends TODAY —
        // today's store has forgotten the id, so the sweep takes its first-show branch and draws it. Writing
        // DrawnInside would make that sweep decide nothing and the frame would vanish, which is not this
        // change's one permitted class of dropped frame. With DrawnBand the sweep's re-assert branch fires
        // instead, and the wire is identical.
        //
        // TryAdd, never an overwrite, and that is load-bearing twice over. The sweep reaches this method with
        // the id ALREADY written as DrawnInside by its own decide pass, and re-stating it as DrawnBand there
        // would make the following beat re-assert a peer that has not moved — a 0x33 per in-view peer per
        // beat, which is the opposite of what this PR is for. An add-only write also cannot take an entry
        // away, so it can only ever let a frame through, never stop one.
        //
        // Only inside the STRICT rect, because that is the only place the client accepts a 0x33/0x07 (ShowPad,
        // the same test the sweep's show branch makes), and only for a peer: ShowPlayer(this) draws OURSELVES,
        // whom no broadcast ever addresses and no sweep ever reconciles. Taking our own _viewLock from under
        // the SUBJECT's state monitor is the order DespawnEntity's broadcast already uses from exactly these
        // three call sites, and it is after Snapshot() returns, never across it (#29).
        //
        // And it is exactly the per-viewer pending state the tick's sweep skip cannot see: a band entry
        // means "re-assert this on the next sweep", and the morph pair that put it there changed nothing
        // about the MAP, so the map generation would let this viewer skip the very sweep the entry exists
        // for. MarkSweepPending is what makes that sweep run. See MarkSweepPending.
        if (!ReferenceEquals(other, this))
        {
            bool banded;
            using (EnterView())
                banded = CurrentView().Contains(s.X, s.Y, ShowPad) && _drawnPeers.TryAdd(s.Id, DrawnBand);
            // Only when the entry was actually CREATED, which is only ever the redraw pair's path: a sweep
            // reaches this method with the id already written DrawnInside by its own decide pass, so its
            // TryAdd fails and the sweep does not flag itself into a second beat it does not need.
            //
            // REDUNDANT TODAY, AND KEPT DELIBERATELY. All three redraw sites broadcast DespawnEntity one line
            // before this one, and DespawnEntity flags the recipient too, so deleting this line breaks no
            // fact — checked, not assumed (the falsification table in briefs/reports/tick-sweep-skip-opus.md,
            // case 3). It stays because the band entry created HERE is what the next sweep is expected to
            // resolve: a later site that draws a peer without the despawn would otherwise leave that entry
            // waiting on the map to change. The flag belongs with the write that creates the obligation.
            if (banded) MarkSweepPending();
        }
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
    /// edge doesn't flicker). Called on world entry, after each of our walk steps, and every world tick.
    ///
    /// <para>Decide for every mob under ONE acquisition, then send after releasing it and only if the decision
    /// is still current — the deferred-send half of the shape <see cref="SyncPeers"/> has had since PR #245.
    /// What it buys here is not the acquisition count (this method already took one for the whole loop): it is
    /// that <see cref="ShowMob"/> and <see cref="SendDespawn"/> no longer run under <c>_viewLock</c>. Every
    /// packet build the sweep produces — the <c>List&lt;byte&gt;</c>, the palette remap, the cipher, the
    /// channel write — used to happen with the viewer's viewport lock held, so on the beats that DO send (map
    /// entry, a step that crosses many mobs, a spawn wave) the hold span was the whole sweep. It also makes
    /// true of this method the rule <c>SyncPeers</c> and <c>ReconcilePeer</c> already state — a send never
    /// happens under <c>_viewLock</c> (#29).</para>
    ///
    /// <para><b>Why the walk stays under the acquisition, and why the parameter is a concrete array.</b> The
    /// peer sweep once copied each peer's id and tile OUTSIDE the lock, because it had to: its decide pass
    /// would otherwise read another session under the viewer's view lock. Nothing here does — a <c>Mob</c> is
    /// a plain object with plain fields — so the walk stays under the acquisition, exactly where the base had
    /// it. Moving it out was tried and measured on 400 viewers and 305 mobs that produce no frames: +38% on
    /// the sweep, with no acquisition saving to pay for it (briefs/reports/sweep-deferred-sends-opus.md).</para>
    ///
    /// <para>What that argument never covered is the walk ITSELF. While the parameter was an
    /// <c>IReadOnlyList&lt;Mob&gt;</c>, <c>foreach</c> called <c>GetEnumerator</c>, <c>MoveNext</c> and
    /// <c>Current</c> through an interface under <c>_viewLock</c> — arbitrary code by type, kept harmless
    /// only by the convention that every caller passed an array. A <c>Mob[]</c> parameter makes it a
    /// guarantee instead of a convention, the same move <c>SyncPeers</c> made in PR #256, and the index loop
    /// below runs no code that is not this session's own. Every caller already passed an array
    /// (<c>World.EnterMap</c>, <c>World.View</c>, <c>ReconcileViews</c>' snapshot, <c>World.AddMob</c>'s
    /// one-mob array), so no caller changed and no overload is left.</para>
    ///
    /// <para>The saving is small and it is the enumerator alone — there was no copy pass here to delete, so
    /// this is not the 7 µs PR #256 took off the peer sweep. An in-process A/B of the two loop shapes on 400
    /// viewers x 305 mobs, interleaved and repeated three times in each of four runs, measured <b>0.50-0.86 µs
    /// per viewer per beat in Debug and 0.65-0.72 µs in Release</b>. The real method before and after, in the
    /// same machine window, went 11,412 -&gt; 9,246 ns Debug and 2,987 -&gt; 2,296 ns Release — but the
    /// untouched <c>SyncPeers</c> control moved -10.3% Debug and +2.0% Release between those two runs, so the
    /// figures this change can claim are about 1.0 µs Debug and 0.75 µs Release, which is roughly 0.4 ms and
    /// 0.3 ms off a beat at 400 players. The cleaner half of the answer is allocation: the interface
    /// enumerator over an array is a 32 B heap object per sweep, and this sweep and the item sweep now
    /// allocate nothing, which is 12.8 KB a beat at 400 players.
    /// See briefs/reports/mob-sweep-array-opus.md.</para>
    ///
    /// <para>The mob decision also stays written out here rather than going through the peer half's helper —
    /// that helper is an eight-argument call neither build inlines, once per mob.</para>
    ///
    /// <para><paramref name="count"/> is how many of <paramref name="mobs"/> to sweep, and defaults to all of
    /// it. A caller passes one only when the array is LONGER than the fill, which is what
    /// <see cref="System.Buffers.ArrayPool{T}"/> hands back: the tail past the fill belongs to whoever rented
    /// the buffer last and is not ours to read. The sweep neither stores <paramref name="mobs"/> nor keeps a
    /// reference into it — a decision copies the mob reference into this session's own pending-send scratch,
    /// which is wiped in the <c>finally</c> below — so a pooled buffer may go back to the pool the moment
    /// this returns. See <see cref="World.ViewPooled"/>.</para></summary>
    public void SyncMobs(Mob[] mobs, int count = -1)
    {
        if (count < 0) count = mobs.Length;
        var pend = Scratch<PendingSend<Mob>>.Rent(PendingSeed);
        int p = 0;
        try
        {
            using (EnterView())
            {
                // Inside the lock, not before it: the whole loop decides under this acquisition, so a sweep
                // that queued behind a walk reconcile anchors on the tile it finds when it gets in, not on the
                // one the viewer stood on when the tick reached this line (PR #240's F1).
                var view = CurrentView();                    // once for the sweep, not once per mob per pad
                for (int i = 0; i < count; i++)              // under the lock, as the base had it — see above
                {
                    var m = mobs[i];                         // a reference copy: Mob is a class
                    if (!m.Alive) continue;                  // a dead mob's despawn is the world's broadcast
                    Reanchor(ref view);                      // the viewer walks on its own thread; it takes
                                                             // World._lock and its own monitor, not this one
                    // m.Id/m.X/m.Y read ONCE, where the base read the tile twice (once per pad) while the
                    // mob's only writer holds World._lock rather than this one — so the two rect tests could
                    // see different tiles. Now they cannot.
                    uint id = m.Id;
                    ushort mx = m.X, my = m.Y;
                    // ONE lookup of the drawn-state store per mob, where the base probed the shown set and
                    // then the band set (see DrawnInside): `state` carries both answers, and a state that
                    // has not changed is not written back. It answers FIRST, and then each branch runs the
                    // one rect test it reads: the drawn 19x17 was already tested lazily here, and `core` is
                    // now computed on the two branches that read it rather than on every mob. The tile was
                    // read once, above, so this cannot reintroduce the two-tile hazard that comment names.
                    if (!_drawnMobs.TryGetValue(id, out byte state))
                    {
                        if (!view.Contains(mx, my, ShowPad)) continue;   // strict 17x15 — where a 0x07 is accepted
                        _drawnMobs[id] = DrawnInside;            // was _shownMobs.Add(id)
                        if (p == pend.Length) pend = Scratch<PendingSend<Mob>>.Grow(pend);
                        pend[p++] = new PendingSend<Mob>(m, id, StampUnderViewLock(id), mx, my, true, false, true);
                    }
                    else if (!view.Contains(mx, my, HidePad))     // left the DRAWN 19x17 rect — now really gone
                    {
                        _drawnMobs.Remove(id);                   // was _shownMobs.Remove; _edgeMobs.Remove
                        if (p == pend.Length) pend = Scratch<PendingSend<Mob>>.Grow(pend);
                        pend[p++] = new PendingSend<Mob>(m, id, StampUnderViewLock(id), mx, my, false, true, false);
                    }
                    else if (view.Contains(mx, my, ShowPad))      // inside the drawn rect: now ask the strict one
                    {
                        // Back inside the strict rect after loitering in the overdraw band. We don't know
                        // whether the client culled it out there, so re-send the spawn: 0x07 on a live id is
                        // an in-place update, and this is strictly cheaper than the despawn+respawn pair the
                        // old HidePad=0 sent on every boundary crossing. The state test is the base's
                        // `!_edgeMobs.Remove(id)`: not banded means nothing to re-assert and nothing to write.
                        if (state != DrawnBand) continue;
                        _drawnMobs[id] = DrawnInside;
                        if (p == pend.Length) pend = Scratch<PendingSend<Mob>>.Grow(pend);
                        pend[p++] = new PendingSend<Mob>(m, id, StampUnderViewLock(id), mx, my, true, true, true);
                    }
                    else if (state != DrawnBand) _drawnMobs[id] = DrawnBand;  // in the band: drawn, suspect
                }
            }

            // THE SENDS, outside the lock, in sweep order, each revalidated immediately before it goes out.
            // THE INVARIANT: a deferred despawn goes out only if the drawn state still says what it said when the
            // decision was taken; a deferred SHOW goes out only if that holds AND the mob is still inside the
            // strict rect, because that is the only place the client accepts the 0x07 — otherwise the show is
            // dropped and the mob is marked undrawn again (PR #246's F1). Nothing on this path blocks on
            // another session the way ShowPlayer -> Snapshot does, so the window is a scheduling one rather
            // than a monitor one — but the viewer's OWN read loop reconciles its walk steps on another thread
            // and can decide the opposite about this mob inside it, which is PR #245's F1 with a different
            // way in.
            //
            // The frame carries a FRESH read of the mob (x, y, sprite, colour, dir), not the tile the decision
            // tested. That is the base's behaviour — ShowMob always re-read the mob when it built the packet —
            // and it is the better of the two here, because a mob moves on the world thread every beat and the
            // client anchors the following 0x0C moves on whatever tile this 0x07 put it on. The rect test
            // above it uses the DECISION's tile, which is the tile the decision to draw was justified by.
            if (p > 0)
            {
                var sendView = CurrentView();                // one anchor for the pass, re-anchored per frame
                for (int i = 0; i < p; i++)
                {
                    bool current;
                    using (EnterView())
                        current = SendStillCurrentUnderViewLock(new DrawnRef(_drawnMobs), pend[i].Id, pend[i].Show,
                                                                pend[i].DrawnBefore, pend[i].ShownAfter, pend[i].Stamp,
                                                                pend[i].X, pend[i].Y, ref sendView);
                    if (!current) continue;                  // a newer reconcile decided otherwise while we waited
                    if (pend[i].Show) ShowMob(pend[i].Subject);
                    else SendDespawn(pend[i].Id);
                }
            }
        }
        finally
        {
            Array.Clear(pend, 0, p);                         // do not let scratch pin a despawned Mob
            Scratch<PendingSend<Mob>>.Return(pend);
        }
    }

    /// <summary>Reconcile the FLOOR ITEMS drawn on this client against what's in view — the ground-item twin
    /// of <see cref="SyncMobs"/>, and needed for exactly the same reason: <see cref="ShowGroundItem"/> draws
    /// through the viewport-gated 0x07 static-object path, so a 0x07 for an off-screen tile is discarded by
    /// the client. Items never move, so a discarded draw is permanent — which is why forage drops
    /// (chestnuts, scattered across a box far bigger than a screen) and loot dropped across the map read as
    /// "not spawning". Called on world entry, after each of our walk steps, and every world tick.
    ///
    /// <para>No overdraw-band state: a stationary item can only leave or enter the band by US moving,
    /// and re-showing it is a single idempotent 0x07, so the plain show/hide pair is enough.</para>
    ///
    /// <para>The parameter is a concrete <c>GroundItem[]</c> for the reason <c>SyncMobs</c> and
    /// <c>SyncPeers</c> have one: an interface parameter makes the walk arbitrary code by type. The capture
    /// pass stays, though, and this is the one sweep of the three that still has one — not because of the
    /// parameter, but because this session's own trap and warp markers are appended to the same array INSIDE
    /// the acquisition, so the world items have to be in it before the lock is taken. The walk over
    /// <paramref name="items"/> is an index loop and it still runs outside the lock.</para>
    ///
    /// <para>Same shape as the other two sweeps since this PR, and here the acquisition count IS the cut: this
    /// method took <b>one <c>EnterView</c> per item</b> (plus a second on every despawn, plus one for the
    /// markers) to read one set and, on most beats, find nothing to do — the per-entity shape
    /// <c>ReconcilePeer</c> had before PR #245. It costs nothing on the 400-player load map, which has no
    /// floor items at all, and 109ns per item per viewer per beat in Debug on a map that has some (measured,
    /// 50 items: 5,470ns of sweep for 50 decisions). It is now one acquisition for the whole sweep, and the
    /// sends — which were already outside the lock — are revalidated, which they were not.</para></summary>
    public void SyncGroundItems(GroundItem[] items)
    {
        // CAPTURE, OUTSIDE THE LOCK — not because the walk needs to be out here (the parameter is a concrete
        // array), but because the markers below are appended to this same array under the lock. Items never
        // move, so the captured tile is the item's tile for good.
        var subs = Scratch<ViewSubject<GroundItem>>.Rent(items.Length + 8);
        var pend = Scratch<PendingSend<GroundItem>>.Rent(PendingSeed);
        int n = 0, p = 0;
        try
        {
            for (int i = 0; i < items.Length; i++)
            {
                var gi = items[i];                           // a reference copy: GroundItem is a class
                if (n == subs.Length) subs = Scratch<ViewSubject<GroundItem>>.Grow(subs);
                subs[n++] = new ViewSubject<GroundItem>(gi, gi.Id, gi.X, gi.Y);
            }

            using (EnterView())
            {
                // Our own spot-traps markers ride along: they are drawn through the identical viewport-gated
                // 0x07 path, and the reveal radius (15) is nearly twice the view rect, so they need the same
                // walk-into-view draw the world's floor items get. They live only on this session — no other
                // client ever sees them. The @showwarps overlay markers ride along for the same reason: they
                // span the whole map. They are appended HERE, inside the sweep's own acquisition, rather than
                // copied to an array before it: they are this session's own state guarded by this very lock,
                // so no arbitrary code runs, the sweep keeps to ONE acquisition, and the GroundItem[] the
                // base allocated on every sweep that had a marker is gone. World items first, then markers —
                // the order the base's Concat produced.
                foreach (var marker in _trapMarkers.Values)
                {
                    if (n == subs.Length) subs = Scratch<ViewSubject<GroundItem>>.Grow(subs);
                    subs[n++] = new ViewSubject<GroundItem>(marker, marker.Id, marker.X, marker.Y);
                }
                foreach (var marker in _warpMarkers)
                {
                    if (n == subs.Length) subs = Scratch<ViewSubject<GroundItem>>.Grow(subs);
                    subs[n++] = new ViewSubject<GroundItem>(marker, marker.Id, marker.X, marker.Y);
                }

                var view = CurrentView();                    // once for the sweep, not once per item per pad
                for (int i = 0; i < n; i++)
                {
                    // The rect is re-anchored in the same acquisition that reads the tracking set, so this
                    // item's decision and the state it is made against are both as of one moment (F1's shape,
                    // on items).
                    Reanchor(ref view);
                    var draw = DecideItemUnderViewLock(subs[i].Id, subs[i].X, subs[i].Y,
                                                       in view, out bool drawnBefore, out bool shownAfter, out uint stamp);
                    if (draw == EntityDraw.Nothing) continue;
                    if (p == pend.Length) pend = Scratch<PendingSend<GroundItem>>.Grow(pend);
                    pend[p++] = new PendingSend<GroundItem>(subs[i].Subject, subs[i].Id, stamp, subs[i].X, subs[i].Y,
                                                            draw == EntityDraw.Show, drawnBefore, shownAfter);
                }
            }

            // THE SENDS, outside the lock, in sweep order, each revalidated. The sends were already outside
            // the lock here; what is new is that they are checked. Items do not move, so the only thing that
            // can invalidate a deferred item frame is the viewer's own walk reconcile completing in between —
            // and that reconcile calls this same method, so before this PR it could remove an id from
            // _shownItems that a parked pass then despawned again, or draw one a parked pass then re-drew.
            //
            // A parked item SHOW takes the same rect test as the other two sweeps, and for items it is belt
            // and braces rather than a fix: ShowGroundItem re-tests the viewport itself and only records the
            // item as drawn if the draw was accepted, so a late item show could never leave the mismatch
            // PR #246's F1 found on mobs. What the test buys here is the packet build and the call it saves,
            // and one shape for all three sweeps. There is no band set to roll back — items have no overdraw
            // band — so `null` is passed for it.
            if (p > 0)
            {
                var sendView = CurrentView();                // one anchor for the pass, re-anchored per frame
                for (int i = 0; i < p; i++)
                {
                    bool current;
                    using (EnterView())
                        current = SendStillCurrentUnderViewLock(new DrawnRef(_shownItems), pend[i].Id, pend[i].Show,
                                                                pend[i].DrawnBefore, pend[i].ShownAfter, pend[i].Stamp,
                                                                pend[i].X, pend[i].Y, ref sendView);
                    if (!current) continue;
                    if (pend[i].Show) ShowGroundItem(pend[i].Subject);
                    else SendDespawn(pend[i].Id);
                }
            }
        }
        finally
        {
            Array.Clear(subs, 0, n);                         // do not let scratch pin a picked-up GroundItem
            Scratch<ViewSubject<GroundItem>>.Return(subs);
            Array.Clear(pend, 0, p);
            Scratch<PendingSend<GroundItem>>.Return(pend);
        }
    }

    /// <summary>The peer sweep's interleaving point, for tests only: null in production, and invoked exactly
    /// once per <see cref="SyncPeers"/> call, <b>after the sweep has taken its rect and before anything is
    /// reconciled</b> — the moment a walk that lands there can make the sweep's rect stale.
    ///
    /// <para>Why the production path carries it, which is the same argument <see cref="World.PhaseProbeForTest"/>
    /// makes. The two staleness facts in <c>Tests/ViewportRectStalenessTests.cs</c> are PR #240's reviewer's
    /// probes for finding F1 and F2, and they got that moment for free: <c>SyncPeers</c> took an
    /// <c>IReadOnlyList</c>, so a test could hand it a list whose <c>GetEnumerator</c> ran a real world-lock
    /// position write. That is exactly the "arbitrary code from an interface" this sweep no longer admits —
    /// the parameter is a concrete <c>PeerTile[]</c> — so the interleaving point has to be a named seam
    /// instead of a side effect of the enumeration. It is the regression guard for a HIGH; it does not get
    /// dropped because the shape that hosted it went away.</para>
    ///
    /// <para>Its cost on the healthy path is one static null check per sweep — not per peer — next to a loop
    /// over every player on the map.</para></summary>
    internal static Action? PeerSweepProbeForTest;

    /// <summary>Reconcile the PEER players drawn on this client against what's in view — the player twin of
    /// <see cref="SyncMobs"/>, and needed for the same reason: <see cref="ShowPlayer"/> draws through the
    /// viewport-gated 0x33 look path, so a draw for an off-screen peer is dropped by the client. Peers only
    /// move (or we do), so without this a peer we entered the map too far from, or who walks toward us from
    /// off-screen, is invisible forever until a room change or Ctrl+R re-draws them in view — the reported
    /// "can't see users I walk up to". Called on world entry, after each of our walk steps, and every world
    /// tick — the same three sites as SyncMobs. Self is skipped.
    ///
    /// <para><paramref name="count"/> is how many of <paramref name="peers"/> to sweep, and defaults to all of
    /// it, for the reason <see cref="SyncMobs"/> takes one: a pooled buffer is at least as long as the fill
    /// and its tail is the previous tenant's. Nothing here outlives the call either — a decision copies
    /// <c>peer.Session</c> and the tile into this session's own pending-send scratch, which the
    /// <c>finally</c> wipes, and the loop below dereferences nothing in the array. See
    /// <see cref="World.ViewPooled"/>.</para></summary>
    public void SyncPeers(PeerTile[] peers, int count = -1)
    {
        if (count < 0) count = peers.Length;
        var view = CurrentView();                            // once for the sweep, not once per peer per pad
        PeerSweepProbeForTest?.Invoke();                     // null except under test — see the field

        // ONE ACQUISITION FOR THE SWEEP, not one per peer. ReconcilePeer took EnterView() per peer, which at
        // 400 players on one map is 400 viewers x 400 peers = 160,000 acquire/release pairs a beat for, in the
        // steady state, zero frames. EnterView is not a bare Monitor.Enter: it also writes the [ThreadStatic]
        // _viewDepth counter that makes the "session monitors are outside _viewLock" assert possible, and a
        // thread-static access is a helper call the Debug build does not inline. The Step 1 profile measured
        // the per-peer acquisition at ~38us of a ~51us modelled peer sweep in Debug and ~3.3us of ~7.5us in
        // Release, per viewer per beat (briefs/reports/viewport-sweep-opus.md).
        //
        // THE INVARIANT, and it is what makes this legal: nothing under the lock calls into another session,
        // or into anything else at all. `peers` is the snapshot array World._lock filled, and a PeerTile
        // carries the peer's id and tile as well as its reference, so every line under the acquisition is an
        // array index and a struct field read. The peer's Session reference is copied into the pending frame
        // and never followed there — ShowPlayer follows it after the release. The decide pass therefore
        // touches only this session's own sets, its own rect and its own _viewGen, and acquires nothing. The
        // lock order (session monitor OUTSIDE _viewLock, Session.State.cs) is unchanged, and so is the #29
        // rule that a send never happens under _viewLock — the sends are still after the release, exactly
        // as ReconcilePeer did them.
        //
        // WHY THE PARAMETER IS A CONCRETE ARRAY, AND WHY THERE IS NO COPY PASS ANY MORE. This sweep used to
        // take an IReadOnlyList, so enumerating it was arbitrary code: the interleaving harness in
        // Tests/ViewportRectStalenessTests.cs ran a real world-lock position write from inside GetEnumerator.
        // Arbitrary code must not run under a view lock (Session.EnterState asserts !HoldsAnyViewLock and
        // World._lock would be taken second), so the enumeration had to stay outside the acquisition and copy
        // each peer into a rented ViewSubject array for the decide pass to read. Every production caller
        // already passed a PeerTile[] — World.EnterMap and World.View return arrays, ReconcileViews passes
        // the snapshot's — so the copy pass existed only to defend against a caller that did not. The type
        // is the guarantee now, there is no IReadOnlyList overload left for a later caller to reintroduce one
        // through, and the decide loop reads the snapshot array directly. Measured on the viewport profile's
        // 400-viewer / 305-mob fixture: this method went 21.4us -> 14.4us per viewer per beat in Debug and
        // 7.2us -> 4.3us in Release, which at 400 players is 2.8ms and 1.2ms off a beat, and an in-process
        // A/B of the two shapes agrees (about 7us Debug, 2.9us Release, over three runs). The enumerator the
        // interface form allocated goes too: the sweep is 0 B per viewer per beat now, and ReconcileViews
        // 55.3 B instead of 87.3 B. The interleaving the harness needed is PeerSweepProbeForTest, above.
        //
        // The decisions themselves are not made any staler by this: Reanchor still runs per peer, inside the
        // acquisition, against the same _viewGen handshake, so no decision uses a rect older than the viewer's
        // tile at the moment it is made (F1/F2, PR #240's review).
        var pend = Scratch<PendingSend<Session>>.Rent(PendingSeed);
        int p = 0;
        try
        {
            // NOTHING IS DEREFERENCED IN HERE. `peer` is a read-only reference into the snapshot array, and
            // its id, like its tile, came out of the world lock (see World.PeerTile) — so the whole loop is
            // an array index, four struct field reads and this session's own decision. peer.Session is copied
            // into the pending frame as a reference and never followed: ShowPlayer follows it after the
            // release. The self test stays a reference compare rather than an id compare for the same reason
            // — the reference is loaded either way, so comparing it touches nothing that load did not
            // already bring in. Array order is the snapshot's order, which the interface enumeration walked
            // too, so the peers are decided in exactly the order they were before.
            using (EnterView())
                for (int i = 0; i < count; i++)
                {
                    ref readonly var peer = ref peers[i];
                    if (ReferenceEquals(peer.Session, this)) continue;
                    Reanchor(ref view);                      // no decision on a rect older than the step
                    var draw = DecidePeerUnderViewLock(_drawnPeers, peer.Id, peer.X, peer.Y,
                                                          in view, out bool drawnBefore, out bool shownAfter, out uint stamp);
                    if (draw == EntityDraw.Nothing) continue;
                    if (p == pend.Length) pend = Scratch<PendingSend<Session>>.Grow(pend);
                    pend[p++] = new PendingSend<Session>(peer.Session, peer.Id, stamp, peer.X, peer.Y,
                                                        draw == EntityDraw.Show, drawnBefore, shownAfter);
                }

            // THE SENDS, outside the lock, in sweep order — each one revalidated before it goes out.
            //
            // THE INVARIANT: a deferred despawn goes out only if the drawn state still says what it said when the
            // decision was taken; a deferred SHOW goes out only if that holds AND the peer is still inside the
            // strict rect, because that is the only place the client accepts the 0x33 — otherwise the show is
            // dropped and the peer is marked undrawn again. The rect half is PR #246's F1, found on the mob
            // sweep and fixed here too: the hole is #245's, because a completed walk that moves a peer into
            // the overdraw band takes the band-state branch, which produces no frame and so takes no
            // stamp, and a parked show behind it passed the membership test and drew nothing on a client that
            // the drawn store said was drawn.
            //
            // Deciding for every peer under one acquisition put an interval on the board that the per-peer
            // shape did not have: peer B's decision is made, and then this pass can BLOCK on peer A's
            // ShowPlayer -> Snapshot (the subject's state monitor, held by any thread doing its own work)
            // while B's decision waits its turn. A walk reconcile on the viewer's own thread can complete in
            // that interval and decide the opposite about B — and on the first cut of this change the parked
            // pass then sent its older frame anyway, leaving a peer despawned on the client while _drawnPeers
            // still said drawn, which no later sweep repairs (PR #245's review, finding F1, and its mirror:
            // a stale show after a completed despawn is a ghost the server does not know it drew). So every
            // frame is revalidated under _viewLock immediately before it goes out, and dropped if a newer
            // decision has taken its place. Snapshot() is still called AFTER the release, never under the
            // lock (#29).
            //
            // Cost: an entry with nothing to send takes no acquisition at all, which in the steady state is
            // every entry — 399 peers a beat that produce no frames pay nothing for this. An entry WITH a
            // send pays one acquisition around one drawn-state read, a re-anchor, one rect test and, on a current
            // despawn or a dropped show, one dictionary removal — next to a packet build and a socket write.
            if (p > 0)
            {
                var sendView = CurrentView();                // one anchor for the pass, re-anchored per frame
                for (int i = 0; i < p; i++)
                {
                    bool current;
                    using (EnterView())
                        current = SendStillCurrentUnderViewLock(new DrawnRef(_drawnPeers), pend[i].Id, pend[i].Show,
                                                                pend[i].DrawnBefore, pend[i].ShownAfter, pend[i].Stamp,
                                                                pend[i].X, pend[i].Y, ref sendView);
                    if (!current) continue;                  // a newer reconcile decided otherwise while we waited
                    if (pend[i].Show) ShowPlayer(pend[i].Subject);
                    else SendDespawn(pend[i].Id);
                }
            }
        }
        finally
        {
            Array.Clear(pend, 0, p);                         // do not let scratch pin a disconnected Session
            Scratch<PendingSend<Session>>.Return(pend);
        }
    }

    /// <summary>One entity's identity and tile, captured outside <c>_viewLock</c> so the decide pass under it
    /// touches nothing but this session's own state. A struct in a reused array: the sweep must not allocate
    /// per beat. <typeparamref name="T"/> is the <see cref="GroundItem"/> the frame would be built from, and
    /// that is now the only closed type: <see cref="SyncPeers"/> decides straight off its <c>PeerTile[]</c>
    /// since PR #256 and <see cref="SyncMobs"/> never captured at all, so the item sweep is the one caller
    /// left — it still captures, because its own trap and warp markers are appended to the same array under
    /// the lock.</summary>
    private struct ViewSubject<T> where T : class
    {
        internal T Subject;
        internal uint Id;
        internal ushort X, Y;

        internal ViewSubject(T subject, uint id, ushort x, ushort y) { Subject = subject; Id = id; X = x; Y = y; }
    }

    /// <summary>A frame a decision parked for the send pass, appended <b>only when a decision produces one</b>
    /// — which in the steady state is never, so a sweep over hundreds of entities that have not moved does not
    /// write to this buffer at all.
    ///
    /// <para>That is the difference between this and recording a decision per entity, and it is not a style
    /// choice: the first cut of this change carried a full decision struct for every entity and the measured
    /// mob sweep went 8.7us -&gt; 15.1us per viewer per beat in Debug and 2.9us -&gt; 5.5us in Release, on
    /// 305 mobs that produce no frames. The shape here costs the capture and nothing else
    /// (briefs/reports/sweep-deferred-sends-opus.md).</para></summary>
    private struct PendingSend<T> where T : class
    {
        internal T Subject;
        internal uint Id;
        /// <summary>The serial the decide pass stamped this decision with, compared against the session's
        /// <c>_sendStamp</c> before the frame goes out.</summary>
        internal uint Stamp;
        /// <summary>The tile this decision was taken on. A SHOW is re-tested against the viewer's CURRENT rect
        /// on this tile immediately before it goes out — see <see cref="SendStillCurrentUnderViewLock"/>.
        /// Items never move, so for them it is simply the item's tile.</summary>
        internal ushort X, Y;
        /// <summary>A show when true, a despawn when false.</summary>
        internal bool Show;
        /// <summary>What the drawn set said about <see cref="Id"/> BEFORE this decision wrote to it — the one
        /// bit that tells a dropped show whether it was a first show (the client has nothing, so remove) or a
        /// re-assert (the client really did draw it, so leave it drawn and put it back in the band).
        /// PR #246's F2.</summary>
        internal bool DrawnBefore;
        /// <summary>What the drawn set said about <see cref="Id"/> immediately AFTER this decision was taken.
        /// The send pass re-reads the set and drops the frame if it no longer says this — half of the
        /// still-current test; see <see cref="SendStillCurrentUnderViewLock"/>.</summary>
        internal bool ShownAfter;

        internal PendingSend(T subject, uint id, uint stamp, ushort x, ushort y,
                             bool show, bool drawnBefore, bool shownAfter)
        {
            Subject = subject; Id = id; Stamp = stamp; X = x; Y = y;
            Show = show; DrawnBefore = drawnBefore; ShownAfter = shownAfter;
        }
    }

    /// <summary>How big a pending-send buffer starts out. A sweep that sends at all usually sends a handful of
    /// frames — a walk step crosses the rect edge for a few entities — and the buffer doubles from here if a
    /// map entry or a spawn wave needs more.</summary>
    private const int PendingSeed = 16;

    /// <summary>A sweep's reusable scratch array. <b>Thread-static, not per-session</b>, and that is the whole
    /// safety argument: two threads sweep the SAME viewer concurrently all the time — the world tick's
    /// reconcile and the viewer's own read loop reconciling a walk step — so a buffer hanging off the session
    /// would be two sweeps writing one array. It is a property of the sweep in flight, which is a property of
    /// the thread. (A static field of a generic type gets its own storage per closed type, so the four buffers
    /// in play here — a pending array for each of peers, mobs and items, plus the item sweep's subject array,
    /// which is the only capture left — are four separate arrays.)
    ///
    /// <para>Rented by nulling the slot, so a sweep that somehow re-entered on this thread would get a fresh
    /// array instead of the one being iterated. Nothing on the send path reaches a sweep today
    /// (<c>ShowPlayer</c>, <c>ShowMob</c>, <c>ShowGroundItem</c> and <c>SendDespawn</c> all end at
    /// <c>Send</c>), so this is belt and braces, not a known case.</para></summary>
    private static class Scratch<TElem>
    {
        [ThreadStatic] private static TElem[]? _buf;

        internal static TElem[] Rent(int want)
        {
            var buf = _buf;
            _buf = null;                                     // rented: a re-entrant sweep gets its own
            if (buf is null || buf.Length < want) buf = new TElem[Math.Max(want, 64)];
            return buf;
        }

        internal static TElem[] Grow(TElem[] buf)
        {
            var bigger = new TElem[buf.Length * 2];
            Array.Copy(buf, bigger, buf.Length);
            return bigger;
        }

        internal static void Return(TElem[] buf)
        {
            if (_buf is null || _buf.Length < buf.Length) _buf = buf;
        }
    }

    /// <summary>Re-evaluate from scratch which peers WE can see — needed when OUR OWN state flips a per-viewer
    /// visibility rule. Dying in a PvP area lets us see the other ghosts; reviving takes that sight away again.
    /// Clears the tracking sets and re-runs SyncPeers over every peer on our map: ShowPlayer redraws the ones
    /// now visible and despawns the ones now hidden (it decides per viewer), so this both reveals and hides.
    /// Map changes get this for free via EnterMap; this covers an in-place death/revive that stays on the map.
    ///
    /// <para><b>It DOWNGRADES the drawn store, it does not clear it</b>, and the difference is a frame for
    /// every peer the client holds in the overdraw band. This method sends the client nothing to forget with
    /// — no 0x15, no 0x0E — so after it the client still holds every peer it held a moment earlier. Clearing
    /// the store said otherwise, and the sweep below only draws what is inside the STRICT rect
    /// (<see cref="DecidePeerUnderViewLock"/>: an absent id outside it decides Nothing), so a peer loitering
    /// in the one-tile band came back held-by-the-client and absent-from-the-store. With the move gate on
    /// (<c>P1998_GATE_PEER_MOVES</c>) that peer's 0x0C and 0x11 were then dropped until it re-entered the
    /// strict rect, where the first-show branch SNAPPED it to its new tile — a lost step for an entity the
    /// client holds, which is the one thing the gate must never do. Found by PR #264's review as F1 (HIGH),
    /// reviews/PR264-by-fable.md; the probe is Tests/PeerMoveGateTests.cs fact (j).</para>
    ///
    /// <para>Downgrading to <see cref="DrawnBand"/> says exactly what is true: the client has these peers,
    /// and we are no longer sure it should. Membership keeps the gate open, so no move is lost. The state
    /// sends the sweep down its RE-ASSERT branch instead of its first-show branch, and the two build the
    /// identical 0x33 through the identical <see cref="ShowPlayer"/>, so every peer inside the strict rect
    /// gets the same frame the clearing version sent. And a peer now outside the drawn rect reaches the
    /// despawn branch and gets the 0x0E that the clearing version leaked on BOTH arms — a frame this repairs
    /// rather than removes.</para>
    ///
    /// <para><see cref="ForgetShownMobs"/> is the other case and needs none of this: its two callers
    /// (<c>Session.Navigation.cs</c>'s map change, and <see cref="RedrawWorld"/> after a 0x15 mapinfo
    /// re-send) are exactly the ones where the CLIENT has just dropped every foreign entity, so clearing the
    /// store keeps it true. Client and store are cleared together there; here only the store was.</para></summary>
    public void ResyncPeers()
    {
        using (EnterView())
        {
            // Copy the keys out rather than write through the dictionary while enumerating it. Overwriting an
            // existing key does not invalidate a Dictionary enumerator on .NET Core 3.0+, but this runs once
            // per death and once per revive, next to a full-map World.View snapshot, so the obviously correct
            // form costs nothing worth having.
            uint[] ids = new uint[_drawnPeers.Count];
            _drawnPeers.Keys.CopyTo(ids, 0);
            foreach (uint id in ids) _drawnPeers[id] = DrawnBand;
            _sendStamp.Clear();     // no send is in flight across this, so no decision here has a stamp to keep
        }
        // Every entry is now "drawn, re-assert on the next sweep", which is per-viewer pending state by
        // definition: the sweep below resolves what is in view now, and the flag makes the tick's next sweep
        // run too, so a peer this one could not settle is not left to wait on the map changing.
        MarkSweepPending();
        SyncPeers(_world.View(this, _char.Map).peers);
    }

    /// <summary>Reconcile a SINGLE peer into our view (view-gated + tracked). Used when the world tells one
    /// client about one newcomer (World.EnterMap) so the newcomer is drawn only if in view AND recorded in
    /// _drawnPeers — so a later step out of view despawns cleanly, like every other tracked entity.</summary>
    public void SyncPeer(PeerTile other)
    {
        var view = CurrentView();
        ReconcilePeer(other, ref view);
    }

    /// <summary>What reconciling one entity decided to do about it, once the bookkeeping is settled.</summary>
    private enum EntityDraw { Nothing, Show, Despawn }

    // DECIDE UNDER _viewLock, SEND OUTSIDE IT — the shape SyncGroundItems above already uses, and since #29 a
    // requirement rather than a style: ShowPlayer reads the SUBJECT through other.Snapshot(), which takes that
    // session's state monitor. Doing that with our own _viewLock held is one half of a genuine cycle, because
    // the other half exists too — a morph revert, a stealth revert or a death broadcasts DespawnEntity to every
    // peer from under the caster's OWN monitor, and DespawnEntity takes the recipient's _viewLock. Viewer B's
    // _viewLock waiting on subject A's monitor, against A's monitor waiting on B's _viewLock, is two threads
    // that never come back. Both are constant traffic: the tick reconciles viewports every beat.
    //
    // Mirrors SyncMobs' per-entity logic exactly (show inside the strict rect, despawn past the drawn rect with
    // the band state's hysteresis, re-assert on re-entry from the overdraw band). ShowPlayer itself decides
    // draw-vs-despawn for stealth/morph; we only gate on geometry here. The set updates happen at the same
    // points they always did — _drawnPeers gains the id on a show, which ShowPlayer cannot fail — so the only
    // thing that moved is WHERE the packet is built.
    //
    // ONE PEER ONLY. The sweep no longer comes through here: SyncPeers decides for every peer under a single
    // acquisition and sends afterwards, because 400 viewers x 400 peers was 160,000 acquire/release pairs a
    // beat. This is World.EnterMap's newcomer path, one peer at a time, and it keeps the acquire-decide-release
    // -send shape because for one peer there is nothing to amortise. Both call DecidePeerUnderViewLock, so the
    // decision itself exists once.
    private void ReconcilePeer(PeerTile peer, ref ViewRect view)
    {
        var other = peer.Session;
        if (ReferenceEquals(other, this)) return;
        uint id = peer.Id;                                         // out of the snapshot, like the tile
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
        EntityDraw draw;
        uint stamp;
        bool shownAfter, drawnBefore;
        using (EnterView())
        {
            Reanchor(ref view);                                    // no decision on a rect older than the step
            draw = DecidePeerUnderViewLock(_drawnPeers, id, peer.X, peer.Y, in view, out drawnBefore, out shownAfter, out stamp);
        }

        if (draw == EntityDraw.Nothing) return;
        // The same revalidation the sweep does, for the same reason: this send is outside the lock too, so a
        // reconcile on another thread can decide the opposite about this peer between the release and the
        // frame. One acquisition, and only on the path that actually sends.
        bool current;
        using (EnterView())
            current = SendStillCurrentUnderViewLock(new DrawnRef(_drawnPeers), id, draw == EntityDraw.Show,
                                                    drawnBefore, shownAfter, stamp, peer.X, peer.Y, ref view);
        if (!current) return;

        if (draw == EntityDraw.Show) ShowPlayer(other);
        else SendDespawn(id);
    }

    /// <summary>The reconcile's decision for a TRACKED-WITH-HYSTERESIS entity — peers and mobs, which have the
    /// identical rule (show inside the strict rect, despawn past the drawn rect, re-assert on re-entry from
    /// the overdraw band) and differ only in which drawn-state store they keep it in. It is the ONLY place that
    /// rule is written: the peer sweep, the single-peer path <see cref="ReconcilePeer"/> and the mob sweep all
    /// call it, so none of the three can drift from the others.
    ///
    /// <para><b>The caller holds this session's <c>_viewLock</c> and has just re-anchored
    /// <paramref name="view"/>.</b> Nothing in here acquires anything or touches another session: the id and
    /// the tile were captured by the caller, and the store is ours. That is what lets a sweep hold the lock
    /// across every entity without any risk of the #29 cycle (ShowPlayer -> the subject's monitor) — the send
    /// is the caller's job, after the release.</para>
    ///
    /// <para><b>ONE LOOKUP PER ENTITY.</b> <paramref name="drawn"/> is the drawn-state store
    /// (<see cref="DrawnInside"/>), which answers "is it drawn" and "is it in the band" in a single
    /// <c>TryGetValue</c> where the two sets it replaced took one probe each; and a state that has not
    /// changed is not written back, so the steady state — a drawn entity that is still inside the strict
    /// rect — costs exactly one hash lookup and no write.</para>
    ///
    /// <para><paramref name="shownAfter"/> is what the store says about the id when this returns, which is
    /// what the send pass revalidates against. <paramref name="drawnBefore"/> is what it said before — the
    /// one bit that tells a dropped show which of the two show shapes it was, and therefore what to put
    /// back; see <see cref="SendStillCurrentUnderViewLock"/>.</para></summary>
    private EntityDraw DecidePeerUnderViewLock(Dictionary<uint, byte> drawn,
                                                  uint id, ushort px, ushort py, in ViewRect view,
                                                  out bool drawnBefore, out bool shownAfter, out uint stamp)
    {
        stamp = 0;                                        // nothing to send, nothing to revalidate
        drawnBefore = drawn.TryGetValue(id, out byte state);   // what the store said BEFORE this decision

        // EACH RECT TEST RUNS ONLY WHERE ITS ANSWER IS READ. The store answers first, and then the branch
        // it selects asks the one question it needs: an entity that is not drawn is decided by the strict
        // rect alone, and a drawn entity is decided by the drawn rect first and only reaches the strict one
        // if it is still inside. Every branch takes exactly the path the eager pair took for the same
        // (state, tile) — the two tests are pure functions of `view`, `px` and `py`, all three of which the
        // caller captured before the call and none of which anything in here writes.
        if (!drawnBefore)
        {
            shownAfter = false;
            // strict 17x15 — where a 0x33 / 0x07 is accepted. The drawn rect is not consulted: an entity
            // nothing has drawn yet has nothing to keep drawn.
            if (!view.Contains(px, py, ShowPad)) return EntityDraw.Nothing;
            drawn[id] = DrawnInside;                      // was shown.Add(id): a first show is never banded
            shownAfter = true;
            stamp = StampUnderViewLock(id);
            return EntityDraw.Show;
        }
        if (!view.Contains(px, py, HidePad))               // left the wider 19x17 the client renders
        {
            drawn.Remove(id);                             // was shown.Remove(id); edge.Remove(id)
            shownAfter = false;
            stamp = StampUnderViewLock(id);
            return EntityDraw.Despawn;
        }
        shownAfter = true;
        if (view.Contains(px, py, ShowPad))               // inside the drawn rect: now ask the strict one
        {
            // Back inside the strict rect after loitering in the overdraw band. We don't know whether the
            // client culled it out there, so re-send the spawn: a 0x07/0x33 on a live id is an in-place
            // update, and this is strictly cheaper than the despawn+respawn pair HidePad=0 sent on every
            // boundary crossing. `state != DrawnBand` is the base's `!edge.Remove(id)`: it was not in the
            // band, so there is nothing to re-assert and nothing to write.
            if (state != DrawnBand) return EntityDraw.Nothing;
            drawn[id] = DrawnInside;
            stamp = StampUnderViewLock(id);
            return EntityDraw.Show;
        }
        if (state != DrawnBand) drawn[id] = DrawnBand;    // in the band: keep drawn, flag suspect
        return EntityDraw.Nothing;
    }

    /// <summary>The same decision for a GROUND ITEM, which has no overdraw band — a stationary item can only
    /// leave or enter the band by US moving, and re-showing it is a single idempotent 0x07, so the plain
    /// show/hide pair is enough (see <see cref="SyncGroundItems"/>).
    ///
    /// <para>The show branch deliberately does NOT add the id to <c>_shownItems</c>, because
    /// <see cref="ShowGroundItem"/> does — it re-tests the viewport itself (it is also called straight from
    /// <c>World.Broadcast</c>, which has no rect) and adds only if the 0x07 was actually accepted. Keeping
    /// that ownership is what makes this sweep's frames identical to the per-item shape's: a draw the
    /// viewport gate rejects leaves the item untracked and the next sweep tries again. So a show's
    /// <paramref name="shownAfter"/> is <c>false</c>, and the send pass's test reads as "nobody else has drawn
    /// it while we waited".</para></summary>
    private EntityDraw DecideItemUnderViewLock(uint id, ushort px, ushort py, in ViewRect view,
                                               out bool drawnBefore, out bool shownAfter, out uint stamp)
    {
        stamp = 0;
        drawnBefore = _shownItems.Contains(id);           // an item show never writes to the set, so a dropped
        shownAfter = drawnBefore;                         // one has nothing to put back — see the rollback
        if (!shownAfter)
        {
            if (!view.Contains(px, py, ShowPad)) return EntityDraw.Nothing;
            stamp = StampUnderViewLock(id);
            return EntityDraw.Show;
        }
        if (view.Contains(px, py, HidePad)) return EntityDraw.Nothing;
        _shownItems.Remove(id);
        shownAfter = false;
        stamp = StampUnderViewLock(id);
        return EntityDraw.Despawn;
    }

    /// <summary>Record that THIS is now the current decision about <paramref name="id"/> and return its
    /// serial. Called under <c>_viewLock</c>, and only on the branches that produce a frame — a decision with
    /// nothing to send has nothing to be overtaken by.</summary>
    private uint StampUnderViewLock(uint id)
    {
        // 0 is reserved for "nothing to send", so the counter never hands it out (it wraps past it).
        if (++_sendSeq == 0) _sendSeq = 1;
        _sendStamp[id] = _sendSeq;
        return _sendSeq;
    }

    /// <summary>Whichever drawn state the send pass's entity kind is tracked in, so the revalidation below
    /// can be written ONCE for all three sweeps: the two-state store for peers and mobs
    /// (<see cref="DrawnInside"/>), the plain <c>HashSet</c> for ground items, which have no overdraw band
    /// and therefore no second state to be in.
    ///
    /// <para>It is a discriminated pair rather than an interface because it is read on the SEND path only —
    /// an entry that produces no frame never constructs one — so the branch it costs is paid once per
    /// deferred frame, next to a packet build and a socket write, and the decide loop that runs 705 times a
    /// beat goes straight at the dictionary.</para></summary>
    private readonly struct DrawnRef
    {
        private readonly Dictionary<uint, byte>? _drawn;
        private readonly HashSet<uint>? _set;

        internal DrawnRef(Dictionary<uint, byte> drawn) { _drawn = drawn; _set = null; }
        internal DrawnRef(HashSet<uint> set) { _drawn = null; _set = set; }

        /// <summary>Was <c>shown.Contains(id)</c>.</summary>
        internal bool IsDrawn(uint id) => _drawn is not null ? _drawn.ContainsKey(id) : _set!.Contains(id);

        /// <summary>Was <c>edge?.Add(id)</c>: put a DRAWN entity back into the overdraw band. Only ever
        /// reached for an id the caller's membership test has just found drawn, so it cannot invent an entry;
        /// for items it is the no-op the null band set made it.</summary>
        internal void MarkBand(uint id) { if (_drawn is not null) _drawn[id] = DrawnBand; }

        /// <summary>Was <c>shown.Remove(id); edge?.Remove(id)</c>: nothing was ever drawn.</summary>
        internal void Forget(uint id) { if (_drawn is not null) _drawn.Remove(id); else _set!.Remove(id); }
    }

    /// <summary>May the frame a send pass is holding still go out? Called under <c>_viewLock</c>, immediately
    /// before the frame goes out, for every deferred send of every kind — and, for a show that may no longer
    /// go out, this is also where the bookkeeping that show was going to justify is rolled back.
    ///
    /// <para><b>The invariant, in one sentence: a deferred DESPAWN goes out only if the drawn state still says
    /// what it said when the decision was taken; a deferred SHOW goes out only if it still says that AND the
    /// entity is still inside the strict rect at the moment of sending — otherwise it is not sent and the
    /// state is restored to what it said BEFORE that decision.</b> Restored to what it said before, not
    /// to "undrawn": see the rollback below and PR #246's F2.</para>
    ///
    /// <para><b>Why the rect half exists</b> (PR #246's review, finding F1, HIGH). The set half alone is
    /// necessary and not sufficient for a show, because a reconcile can change what the viewer can see without
    /// changing the drawn state in any way the stamp records: a completed walk that moves an entity from the
    /// strict rect into the overdraw band takes the <c>edge.Add(id)</c> branch, which produces no frame and so
    /// takes no stamp. A show parked behind it then passed both halves of the old test and put a 0x07/0x33 on
    /// the wire for a tile outside the strict rect, which is exactly the tile the client's viewport gate
    /// discards (<c>0x424310</c>) — so the client drew nothing while <c>_drawnMobs</c> said drawn, and no
    /// later sweep repaired it until the entity re-entered the strict rect or left the drawn rect. The frame
    /// was the symptom; the drawn-set mismatch was the defect.</para>
    ///
    /// <para><b>Why stamping the band transition is not the fix, and is not done.</b> It makes the frame go
    /// away and leaves the mismatch exactly as it was: the store still says drawn and the client still has
    /// nothing. The rollback is what closes it, and once a show is revalidated against the rect the band
    /// transition needs no stamp — the rect test catches that case and every other way the viewer's rect can
    /// have moved, at one <see cref="Reanchor"/> and one <c>Contains</c> per deferred frame, inside an
    /// acquisition the send pass already takes, and nothing at all in the steady state.</para>
    ///
    /// <para>The two membership halves, unchanged: the stamp separates "nobody touched this entity" from "somebody
    /// despawned it and drew it again while we were parked"; the membership test catches the reconciles that
    /// changed the set without stamping — a wholesale clear, a <see cref="DespawnEntity"/> broadcast. It is
    /// stated as "the set still says what it said when this decision was taken" rather than as a per-kind
    /// rule, which is what lets items (whose show does not add, because <see cref="ShowGroundItem"/> does)
    /// use the same test as peers and mobs (whose show does).</para>
    ///
    /// <para>Whenever this leaves the id undrawn the stamp entry goes with it: an entity nobody has drawn and
    /// nobody has a send in flight for needs no entry. An entry therefore lives exactly as long as the id is
    /// drawn or has a send in flight.</para>
    ///
    /// <para><paramref name="tracked"/> is whichever drawn state this frame's entity kind is tracked in: the
    /// two-state store for peers and mobs (<see cref="DrawnInside"/>), the plain set for ground items, which
    /// have no overdraw band. <paramref name="view"/> is the send pass's rect, re-anchored here under the
    /// same acquisition, so a viewer that has not moved pays one integer compare for the whole pass.</para></summary>
    private bool SendStillCurrentUnderViewLock(in DrawnRef tracked, uint id,
                                               bool show, bool drawnBefore, bool shownAfter, uint stamp,
                                               ushort px, ushort py, ref ViewRect view)
    {
        if (!_sendStamp.TryGetValue(id, out uint latest) || latest != stamp) return false;
        if (tracked.IsDrawn(id) != shownAfter) return false;
        if (!show)                                        // a despawn: nothing to re-gate, the client always takes it
        {
            _sendStamp.Remove(id);
            return true;
        }
        Reanchor(ref view);                               // no send gated on a rect older than the viewer's tile
        if (view.Contains(px, py, ShowPad)) return true;  // still where the client will accept the draw

        // THE SHOW CAN NO LONGER GO OUT, so put the state back the way this decision found it. Which is not
        // the same as "undrawn", and that distinction is PR #246's F2 (MEDIUM). A show has two shapes:
        //
        //   a FIRST show   — the entity was not drawn, the decision recorded it as drawn, and the client
        //                    never received anything. Removing the entry is exactly right.
        //   a RE-ASSERT    — the entity WAS drawn, by a 0x07/0x33 that really went out, and loitered into the
        //                    overdraw band; the decision's only write was to move it out of the band state.
        //                    Rolling THAT back to undrawn discards the server's record of a draw the client
        //                    did receive, and with it the 0x0E that would later take it off the screen: an
        //                    untracked entity outside the strict rect produces no decision at all, so nothing
        //                    repairs it. That is the mirror of F1 — a ghost the server does not know it drew.
        //
        // `drawnBefore` is the one bit that separates them, and it is read off the store by the decide pass
        // before it writes. A dropped re-assert therefore puts the id back in the band, leaving it drawn,
        // restoring exactly the state the walk reconcile itself had set (drawn, suspect), so a later exit
        // from the drawn rect still despawns and a later re-entry still re-asserts.
        if (drawnBefore) tracked.MarkBand(id);            // re-assert: drawn and suspect, as the band left it
        else tracked.Forget(id);                          // first show: nothing was ever drawn
        _sendStamp.Remove(id);
        return false;
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
    // MarkSweepPending: everything this client held is gone from both the client and the store, and the
    // sweeps its callers run immediately afterwards re-stream what is in view. The flag covers the beat AFTER
    // that, and with it the one input to a viewer's rect that is not its tile — realm-center (F4) and the
    // Ctrl+R refresh both re-anchor ViewAnchor() with no position write at all, and both come through
    // RedrawWorld, which comes through here.
    private void ForgetShownMobs() { using (EnterView()) { _drawnMobs.Clear(); _shownItems.Clear(); _drawnPeers.Clear(); _sendStamp.Clear(); _trapMarkers.Clear(); _warpMarkers.Clear(); } MarkSweepPending(); }

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
        // MarkSweepPending, and here it is not belt and braces: this method deliberately does NOT draw —
        // "the DRAW is left to SyncGroundItems" — and a trap revealed under a standing player changes
        // nothing about the map, so without the flag the marker would wait for the next thing that moved.
        bool added;
        using (EnterView()) added = _trapMarkers.TryAdd(trapId, marker);
        if (added) MarkSweepPending();
        return added;
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

    // ---- the tick's sweep skip ------------------------------------------------------------------
    //
    // WHAT THIS IS. World.ReconcileViews runs SyncPeers, SyncMobs and SyncGroundItems for every player of
    // every populated map, every beat. At 400 players and 305 mobs on one map that is 400 viewers deciding
    // against 704 entities each — and in the steady state all 281,600 decisions are "send nothing", because
    // nothing on the map moved. The three sweeps have no way to know that: they are given the roster and
    // they walk it. The two fields below are the fact they were missing.
    //
    // WHY THE SKIP IS SOUND, in one paragraph, because it is the whole argument. A sweep is IDEMPOTENT: for
    // a fixed (entity tile, viewer rect, drawn state) it acts at most once — a first show writes DrawnInside
    // and sends, and the next sweep over the same inputs takes the `state != DrawnBand` early-out and sends
    // nothing; a despawn removes the entry and the next sweep finds nothing drawn and out of view; a band
    // write is skipped when the state is already the band. So running the sweep N times over an unchanged
    // world produces exactly what running it ONCE produces. It follows that a viewer may skip every beat on
    // which none of the sweep's inputs changed, and the inputs are exactly two things: what the MAP holds,
    // which World.MapState.ViewGen counts, and what THIS session's own view state holds, which
    // MarkSweepPending flags. Nothing else reaches a sweep.
    //
    // THE RECORD IS THE TICK THREAD'S ALONE. Begin/EndTickSweep are called only from ReconcileViews, on the
    // tick thread, so _sweptMap and _sweptGen need no synchronisation of their own. The walk step's and map
    // entry's sweeps do NOT touch them: an extra tick sweep is free by the idempotence above, so leaving the
    // record behind those is the safe direction. _sweepPending is the one field other threads write, and it
    // is an int through Interlocked for that reason.
    private ushort _sweptMap;
    private long _sweptGen = -1;             // -1: this viewer has never swept _sweptMap under the tick
    private int _sweepPending;               // 0/1 — set by any change to THIS viewer's own view state

    /// <summary>"Something about THIS viewer's own view state changed, so its next tick sweep must run
    /// whatever the map generation says." The per-viewer half of the skip, and the only half a map counter
    /// cannot express.
    ///
    /// <para>Set wherever a session's drawn-state store or marker set is changed by something that is NOT a
    /// sweep's own decision: <see cref="DespawnEntity"/> (the morph, stealth, death and pickup broadcasts,
    /// and <see cref="ShowPlayer"/>'s own hidden-peer path), <see cref="ShowPlayer"/>'s band re-add,
    /// <see cref="AddTrapMarker"/> and the <c>@showwarps</c> stamp (both of which register a marker and leave
    /// the DRAW to <see cref="SyncGroundItems"/>), <see cref="ResyncPeers"/> and
    /// <see cref="ForgetShownMobs"/>. Each of those is a state a sweep is expected to resolve on the beat
    /// after it appears, and none of them changes anything about the MAP.</para>
    ///
    /// <para>It is set generously on purpose. A false positive costs one viewer one sweep it did not need;
    /// a false negative is a frame the client never receives, which is the stale view this slice is not
    /// allowed to create. Where the two were in tension the extra sweep won.</para></summary>
    private void MarkSweepPending() => Interlocked.Exchange(ref _sweepPending, 1);

    // The kill switch, read ONCE for the process the way GatePeerMoves below is, and not `readonly` for the
    // same test reason: Tests/TickSweepSkipTests.cs runs both shapes in one process, because the strongest
    // form of "the frames are identical" is to RUN the ungated shape rather than keep a hand copy of it
    // (the PR #264 precedent). Production never writes it.
    private static bool TickSweepSkip = ServerConfig.Current.TickSweepSkip;

    /// <summary>The skip's kill switch, live, for the facts that must run both shapes in one process.</summary>
    internal static bool TickSweepSkipForTest
    {
        get => TickSweepSkip;
        set => TickSweepSkip = value;
    }

    /// <summary>May the tick skip this viewer's three sweeps this beat? Called by
    /// <see cref="World.ReconcileViews"/> once per viewer per beat, on the tick thread, with the map id and
    /// the generation IT CAPTURED INSIDE <c>World._lock</c> with the roster — never a live read.
    ///
    /// <para>Returns true to sweep. It returns true when the switch is off, when this viewer is on a map it
    /// has not swept under the tick before (which is also how a map CHANGE is caught — the record carries the
    /// map id, so a generation can never be read across maps), when the map has changed since, or when this
    /// viewer has per-viewer state pending. The pending flag is consumed here, BEFORE the sweeps run, so a
    /// change that lands while they are running sets it again and is swept on the next beat rather than
    /// being cleared by the sweep that never saw it.</para></summary>
    internal bool BeginTickSweep(ushort mapId, long mapGen)
    {
        if (!TickSweepSkip) return true;
        bool pending = Interlocked.Exchange(ref _sweepPending, 0) != 0;
        return pending || _sweptMap != mapId || _sweptGen != mapGen;
    }

    /// <summary>Record that this viewer has now swept <paramref name="mapId"/> at
    /// <paramref name="mapGen"/> — called after the three sweeps return, with the SAME captured generation
    /// <see cref="BeginTickSweep"/> was given. Recording the captured value rather than a fresh read is what
    /// makes a change that lands DURING the sweeps show up as a difference on the next beat.</summary>
    internal void EndTickSweep(ushort mapId, long mapGen)
    {
        _tickSweeps++;
        if (!TickSweepSkip) return;
        _sweptMap = mapId;
        _sweptGen = mapGen;
    }

    /// <summary>How many tick sweeps of this viewer have actually RUN TO COMPLETION. Every fact about the
    /// skip reads it: that the skip fires, that each change source un-skips, that a map change sweeps, and
    /// that the switch off restores the per-beat sweep.
    ///
    /// <para>Counted HERE and not in <see cref="BeginTickSweep"/>, and the difference is what the counter is
    /// worth. Begin's answer is a DECISION, and a falsification that keeps the decision and calls the three
    /// sweeps anyway — <c>_ = t.Session.BeginTickSweep(...)</c> in place of the early return in
    /// <c>ReconcileViews</c> — would leave a decision-counter reading zero while every sweep ran. Counting
    /// at the far end means the number is what happened. (The obvious alternative, the static
    /// <see cref="PeerSweepProbeForTest"/>, counts every SESSION's sweep in the process, so in a full suite
    /// run it picks up other fixtures' maps; this is per viewer.)</para>
    ///
    /// <para>It exists for the reason <see cref="PositionWritesUnderWorldLock"/> exists — a claim about how
    /// often something runs that nothing can falsify is not worth making — and it costs one non-atomic
    /// increment per SWEPT viewer per beat, at the end of a walk over every peer, mob and item on the map.
    /// The write is the tick thread's alone; the read is volatile because the reader is a test
    /// thread.</para></summary>
    internal long TickSweepsForTest => Volatile.Read(ref _tickSweeps);
    private long _tickSweeps;

    // The kill switch for the two peer gates below, read ONCE for the process the same way PassEnforce and
    // CastQueueEnabled are (Session.cs). 0 restores the ungated broadcast this change replaced.
    //
    // It is not `readonly` for one reason, and it is a test reason: the fact that proves the switch actually
    // switches (Tests/PeerMoveGateTests.cs) has to run both shapes in one process, and it also RECORDS the
    // base's frame sequence by running the ungated shape rather than keeping a hand copy of it. Production
    // never writes this field — GatePeerMovesForTest is internal and has no production caller.
    private static bool GatePeerMoves = ServerConfig.Current.GatePeerMoves;

    /// <summary>The kill switch's live value, for the facts that must run both shapes in one process. Writing
    /// it is a test-only act; nothing in the server does.</summary>
    internal static bool GatePeerMovesForTest
    {
        get => GatePeerMoves;
        set => GatePeerMoves = value;
    }

    // Move a peer entity one step. (x,y) is the SOURCE tile — the client's 0x0C overshoots one tile past it
    // in `dir`, so anchoring on the source lands the peer on the true destination. See HandleWalk / MoveMob.
    // Skips clients that don't have the peer drawn, exactly as MoveMob does for mobs (the client ignores a
    // 0x0C for an unknown entity anyway, so this just spares the wire on a big map); SyncPeers draws it once
    // it enters view. _drawnPeers holds both drawn states — DrawnInside and DrawnBand are both "the client
    // has this peer" — so the test is membership, not a state test.
    public void MoveEntity(uint id, ushort x, ushort y, byte dir)                                  // 0x0C
    {
        if (GatePeerMoves) { using (EnterView()) { if (!_drawnPeers.ContainsKey(id)) return; } }
        SendMove(id, x, y, dir);
    }
    // Move a world MOB one step. (x,y) is the mob's SOURCE tile, not the destination: the 4.95 client's
    // 0x0C walk ends one tile past the packet tile in `dir` (forward-slide overshoot), so anchoring on the
    // source makes it land on the true destination. See World.Tick's move broadcast for the full rationale.
    // Skips clients that don't have the mob in view (the client ignores a 0x0C for an unknown entity anyway,
    // so this just spares the wire on a big map); SyncMobs draws it once it enters view.
    public void MoveMob(uint id, ushort x, ushort y, byte dir)
    {
        using (EnterView()) { if (!_drawnMobs.ContainsKey(id)) return; }
        SendMove(id, x, y, dir);
    }
    // Turn a world MOB in place (0x11 side) — same shown-only guard as MoveMob.
    public void SideMob(uint id, byte side)
    {
        using (EnterView()) { if (!_drawnMobs.ContainsKey(id)) return; }
        SendSide(id, side);
    }
    // Turn a peer in place (0x11 side) — the same drawn-only guard as MoveEntity, for the same reason.
    public void SideEntity(uint id, byte side)                                                     // 0x11
    {
        if (GatePeerMoves) { using (EnterView()) { if (!_drawnPeers.ContainsKey(id)) return; } }
        SendSide(id, side);
    }
    public void SpeakEntity(byte chatType, uint id, byte[] msg) => SendSpeech(chatType, id, msg);  // 0x0D
    /// <summary>Play a <c>0x1A</c> action over an entity on this client. The byte overload is the boundary
    /// the dynamic senders cross — the <c>@mobact</c> calibration probe and the mob swing type it sets —
    /// so it stays a raw byte and casts unchanged; peers rendering a KNOWN pose take the named overload.</summary>
    public void ActionOver(uint id, byte type, ushort time, byte param) => SendAction(id, (ActionType)type, time, param);  // 0x1A
    public void ActionOver(uint id, ActionType type, ushort time, byte param) => SendAction(id, type, time, param);        // 0x1A
    public void EffectOver(uint id, int effectId) => SendEffect(id, effectId);                      // 0x29 spell effect
    // MarkSweepPending: this takes an id OUT of our drawn store without the map changing — the morph,
    // stealth, death and pickup broadcasts, and ShowPlayer's own hidden-peer path, all reach here — and the
    // sweep after it is what draws the entity again if it is still in view. Without the flag a stealthed or
    // just-morphed peer standing still would be drawn once and never re-asserted. See MarkSweepPending.
    public void DespawnEntity(uint id) { using (EnterView()) { _drawnMobs.Remove(id); _shownItems.Remove(id); _drawnPeers.Remove(id); _sendStamp.Remove(id); } MarkSweepPending(); SendDespawn(id); }  // 0x0E

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
