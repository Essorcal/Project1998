using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Protocol.Tk495;
using Shared;

namespace Server;

public sealed partial class Session
{

    // ---- live world interaction (client is in-world, sending its own packets) ----

    // Client walk request. 4.95: 0x32/0x06. 5.33: 0x06 (turn is a SEPARATE opcode, 0x11 -> HandleTurn).
    // Direction is body[0] (NexusTK: 0=N,1=E,2=S,3=W). The client predicts one step then blocks until
    // the server confirms; a successful step is confirmed with 0x26 (self-walk animation) on 5.33, or
    // 0x0C+0x04 on 4.95. Passability is enforced HERE, server-side, like Mithia 7.x's clif_parsewalk:
    // if the destination tile is blocked (PassAt != 0) we do NOT move and re-send 0x04 to snap the
    // client back, cancelling its optimistic prediction.
    //
    // 5.33 walk packets also carry the client's believed current position at body[2..5] (BE u16 x,y).
    // We step from THAT tile (when in-bounds), not just our tracked one, so the collision check runs on
    // the cell the client is actually standing on and the server re-syncs to the client each step. This
    // is what stops rapid direction-changes from desyncing us and walking through walls.
    // Warp-destination gate (RTK clif.c:5187-5203, checked only when a player steps onto a warp tile —
    // scripted/quest/GM warps call EnterMap directly via Warp() above and are NOT gated, matching RTK where
    // the check lives in the walk handler, not in pc_warp itself). Denial text mirrors RTK's cascade exactly,
    // including its dead branches: since almost every gated map sets a nonzero ReqLvl, the level-difference
    // messages (cases 1-3) already cover every diff value, so the mark/path-specific text below them is only
    // reachable when ReqLvl equals the player's level exactly and a mark/path check also fails — an RTK
    // inconsistency preserved here rather than "fixed".
    // Quest-locked warp (game-data/WarpQuestLocks.csv): is this warp switched off until a quest advances?
    //
    // Nothing about MOVEMENT changes when this returns true — see the call site. It is not a barrier and
    // not a collision rule; the tile stays walkable and the player stands on it. Contrast TryWarpGate
    // below, which is RTK's map-requirement cascade and genuinely does push the player back off the tile.
    //
    // Keyed on (from, to), so the lock is one-way: walking BACK the way you came is never affected.
    private bool WarpLockedByQuest(ushort destMap, out string lockMsg)
    {
        lockMsg = "";
        if (!Content.WarpQuestLocks.TryGetValue((_char.Map, destMap), out var wl)) return false;
        if (QuestStage(wl.QuestKey) >= wl.MinStage) return false;
        lockMsg = wl.Message;
        return true;
    }

    private bool TryWarpGate(ushort destMap, out string denyMsg)
    {
        denyMsg = "";
        if (!Content.MapMeta.TryGetValue(destMap, out var meta)) return true;   // no Maps.csv row -> unrestricted

        bool lvlFail  = _char.Level < meta.ReqLvl;
        bool statFail = (long)_char.MaxHp < meta.ReqVita && (long)_char.MaxMp < meta.ReqMana;
        bool markFail = CharMark < meta.ReqMark;
        bool pathFail = meta.ReqPath > 0 && CharClassId != meta.ReqPath;
        if (lvlFail || statFail || markFail || pathFail)
        {
            if (!string.IsNullOrEmpty(meta.RejectMsg)) { denyMsg = meta.RejectMsg; return false; }
            int diff = Math.Abs(meta.ReqLvl - _char.Level);
            denyMsg = diff >= 10 ? "Nightmarish visions of your own death repel you."
                    : diff >= 5  ? "You're not quite ready to enter yet."
                    : diff < 5   ? "You almost understand the secrets to this entrance."
                    : markFail   ? "You do not understand the secrets to enter."
                    : pathFail   ? "Your path forbids it."
                    : "A powerful force repels you.";
            return false;
        }

        if (meta.LvlMax > 0 && (_char.Level > meta.LvlMax || ((long)_char.MaxHp > meta.VitaMax && (long)_char.MaxMp > meta.ManaMax)))
        {
            denyMsg = "A magical barrier prevents you from entering.";
            return false;
        }
        return true;
    }

    private void HandleWalk(byte[] dec)
    {
        // SLEEP GATE (the Doze family — see Session.ReceiveSleep). A held player does not move, and this is
        // the same mechanism RTK uses: clif_parsewalk refuses on `sd->paralyzed || sd->sleep != 1.0f ||
        // sd->snare` with `clif_blockmovement(0); clif_sendxy(sd); clif_blockmovement(1)`. The `0x51`
        // block/unblock wrapper has no 4.95 handler (it no-ops in the world dispatcher, and the player-state
        // dispatcher's range check `cmp esi,0x4a` on `opcode-4` rejects it outright), but it isn't the part
        // that does the work: `clif_sendxy` is, and that is our `SendXy` — the exact 0x04 snap-back the
        // `blocked` branch below already uses to stop players walking through walls. Client-side prediction
        // is not the same as client authority; the client guesses, and 0x04 overrules it.
        //
        // Snapping to the SERVER's (X,Y) rather than the client-reported tile at body[2..5] is deliberate: a
        // normal step trusts the client's claim (see below), but while you're held that claim is exactly what
        // must not be honoured, or a client that keeps reporting a tile further along creeps a step per packet.
        //
        // Sending SOMETHING is mandatory, not politeness: on 4.95 the client has already predicted the step
        // and blocks awaiting the ack. Returning silently freezes it for good, not for the doze's duration.
        if (Asleep)
        {
            SendXy();
            Log.Info($"   -> walk refused: asleep — held at ({_char.X},{_char.Y})");
            return;
        }

        byte dir = dec.Length > 0 ? dec[0] : (byte)0;
        _facing = (byte)(dir & 3);   // remember which way we're facing so melee (0x13) knows the front tile

        // Fast-move (client-authoritative) is flagged PER WALK: the client sets the high bit of the step
        // counter (dec[1]) on every predicted step. This is authoritative per-packet, so we read it here
        // instead of tracking the 0x1b/09 toggle (which desyncs if we guess the client's startup state).
        //   high bit SET   -> client already moved/animated -> we send NOTHING (only correct on block).
        //   high bit CLEAR -> server-authoritative -> the client waits; we assign the tile with 0x04.
        bool clientFast = dec.Length > 1 && (dec[1] & 0x80) != 0;
        _fastMove = clientFast;   // keep the tracked flag in sync for logging/other uses

        // Both 4.95 and 5.33 report the client's believed current tile at body[2..5] (BE u16 x,y). We step
        // from THAT tile (client-authoritative resync), so collision runs on the cell the client is really
        // on and we never drift out of sync — this is what a normal walk needs (RTK only "corrects" the
        // client via 0x04 on an actual mismatch/block, never on a good step).
        int fromX = _char.X, fromY = _char.Y;
        if (dec.Length >= 6)
        {
            int rx = (dec[2] << 8) | dec[3];
            int ry = (dec[4] << 8) | dec[5];
            if (rx >= 0 && ry >= 0 && rx < _char.MapXs && ry < _char.MapYs) { fromX = rx; fromY = ry; }
        }

        int nx = fromX, ny = fromY;
        switch (dir & 3)
        {
            case 0: ny -= 1; break;  // north
            case 1: nx += 1; break;  // east
            case 2: ny += 1; break;  // south
            case 3: nx -= 1; break;  // west
        }
        // Off the tile grid is always blocked. Otherwise consult passability for the destination tile.
        bool offMap = nx < 0 || ny < 0 || nx >= _char.MapXs || ny >= _char.MapYs;
        var map = MapData.For(_char.Map, _char.MapXs, _char.MapYs);
        ushort obj = (!offMap && map != null) ? map.Obj(nx, ny) : (ushort)0;
        // A living mob occupies its tile too (the client also self-blocks on creatures — enforce it
        // server-side so a desync can't let a player stand on one). Warp tiles still win (checked below).
        bool mobHere = !offMap && _world.MobAt(_char.Map, nx, ny) is not null;
        // Collision = ground pass flag (Blocked, honors the passtest diag) OR the client's SObj.tbl directional
        // object-wall for this heading (ObjectFlags) — the layer that stops you walking through a hut's thin
        // side wall (pass=0 under it). Warp tiles still win: the warp check below returns before `blocked` is
        // consulted, so doorways sitting on object tiles keep working.
        bool blocked = offMap || mobHere
            || (PassEnforce && map != null && (Blocked(map, nx, ny) || ObjectFlags.Blocks(map.Obj(nx, ny), dir & 3)));

        // Doors/portals take precedence over collision: if the tile we're stepping toward is a warp
        // source, take it — even if that tile is otherwise "solid" (many doorways sit on object tiles).
        //
        // A quest lock switches the WARP off and nothing else. The player is not stopped, pushed back or
        // blocked in any way: the tile simply stops being a portal for them, so they walk onto it like any
        // other patch of ground and get told why they weren't carried anywhere. That is the whole mechanic
        // — no barrier, no wall.
        bool warpLocked = false;
        if (!offMap && Content.TryWarp(_char.Map, (ushort)nx, (ushort)ny, out var lockedDest)
            && WarpLockedByQuest(lockedDest.m, out var lockMsg))
        {
            warpLocked = true;
            SendMiniText(lockMsg);
            Log.Info($"   -> WARP ({nx},{ny}) map {_char.Map} -> {lockedDest.m} quest-locked: {lockMsg} — step allowed, no warp");
        }

        if (!warpLocked && !offMap && Content.TryWarp(_char.Map, (ushort)nx, (ushort)ny, out var dest)
            && Content.TryMap(dest.m, out var dm))
        {
            if (!TryWarpGate(dest.m, out var denyMsg))
            {
                // Rejected. In 4.95 self-walk is client-local: the client already stepped onto the warp
                // tile AND is now blocked awaiting a 0x04 ack to release its next step. If we just return,
                // that gate never clears — the player freezes and "can't move/turn." RTK handles this by
                // calling clif_pushback(sd) (a re-warp back off the tile) BEFORE the reject text (clif.c:5190).
                // Our 4.95-correct equivalent is the same snap-back the `blocked` branch uses: hold at the
                // from-tile and re-assert with 0x04. The denial goes to the STATUS box (RTK clif_sendminitext),
                // not the chat bubble.
                _char.X = (ushort)fromX; _char.Y = (ushort)fromY;
                SendXy();
                SendMiniText(denyMsg);
                Log.Info($"   -> WARP ({nx},{ny}) map {_char.Map} -> {dest.m} DENIED: {denyMsg} — held at ({fromX},{fromY})");
                return;
            }
            Log.Info($"   -> WARP ({nx},{ny}) on map {_char.Map} -> map {dest.m} '{dm.Name}' ({dest.x},{dest.y})");
            // Quest beats that trigger on stepping THROUGH a warp tile, not on standing anywhere — the
            // after-step hooks (OnScriptedTileStep) never run for a warp, since we leave the map here.
            TryNewbieCoordinateLesson(_char.Map, (ushort)nx, (ushort)ny);
            EnterMap(dm.Id, dm.Xs, dm.Ys, dest.x, dest.y, dm.Name);
            return;
        }

        // Zodiac cave entrances (Mythic Nexus) are RTK Lua tile-scripts (onScriptedTilesMythic ->
        // mythic_cave_selector), NOT SQL warps — so they need their own handler. Stepping on a configured
        // entrance tile (game-data/MythicCaves.csv, keyed by map+x+y) warps to the deepest cave tier the
        // player's level/vitals unlock (or refuses, under-levelled). Non-entrance tiles return immediately.
        if (!offMap && TryMythicCaveEntrance((ushort)nx, (ushort)ny)) return;

        // Class path-hall interior doorways (onScriptedTilesPathHalls.lua) are scripted tiles, not SQL warps —
        // only the "outside" warp is in Warps.csv, which is why the leader/arena doors felt dead.
        if (!offMap && TryPathHallWarp((ushort)nx, (ushort)ny)) return;

        // Tower Arena's five side doors (onScriptedTilesArena.lua) are scripted tiles too, level-banded per
        // arena — the SQL table only holds each arena's way BACK, so the whole hub was one-way until now.
        if (!offMap && TryArenaDoor((ushort)nx, (ushort)ny)) return;

        if (blocked)
        {
            _char.X = (ushort)fromX; _char.Y = (ushort)fromY;   // hold at the from-tile
            SendXy();                                           // 0x04 snap-back cancels the prediction
            TryOpenBoardSign();   // bumping north into a board sprite opens it (RTK onSign), same as a turn
            Log.Info($"   -> walk dir={dir} BLOCKED at ({nx},{ny}) obj={obj}{(offMap ? " off-map" : "")}{(mobHere ? " mob" : "")} — held at ({_char.X},{_char.Y})");
            return;
        }

        _char.X = (ushort)nx;
        _char.Y = (ushort)ny;
        MarkDirty();   // position, unlike most mutations, is only ever picked up by the autosave/disconnect flush

        // Bladestorm is the one trap kind a PLAYER can trigger (see Content.IsBladestormTrap) — the hazard
        // family (dart/snare/…) stays mob-only, checked separately in World.Tick.
        _world.CheckPlayerTrapTrigger(this, _char.Map, (ushort)nx, (ushort)ny, (byte)(dir & 3));

        // Shared world: everyone ELSE on this map watches us step (0x0C animates our entity to the new
        // tile on their clients). Our OWN client is handled by the self-walk modes below (mode 7 stays
        // silent and lets the local controller animate) — so exclude ourselves from the broadcast.
        // We broadcast the SOURCE tile (fromX,fromY), not the destination: the 4.95 client's 0x0C walk ends
        // one tile PAST the packet tile in `dir` (forward-slide overshoot), so anchoring on the source makes
        // a peer land on our true destination instead of one tile ahead. Same fix as the mob moves.
        _world.Broadcast(_char.Map, p => p.MoveEntity(_char.Id, (ushort)fromX, (ushort)fromY, dir), except: this);

        // Our viewport just shifted a tile: stream in mobs that entered view, drop ones that left.
        SyncMobs(_world.View(this, _char.Map).mobs);
        // ...and the FLOOR ITEMS, which need it even more than mobs do: they never move, so an item whose
        // 0x07 was dropped by the client's viewport gate (a forage chestnut across the farm, loot from a
        // kill on the far side of the map) is invisible forever unless walking up to it re-draws it.
        SyncGroundItems(_world.ItemsOn(_char.Map));

        // ...and the TERRAIN under that viewport. The client only requests map data (0x05) on map ENTRY, so
        // without this it runs off the edge of what was streamed and hits a black wall. Pushes just the newly
        // exposed strip. See StreamViewport.
        StreamViewport();

        if (_ver == ClientVersion.V533)
        {
            SendSelfWalk(dir, (ushort)fromX, (ushort)fromY);   // 0x26: animate one step from the old tile
        }
        else if (V495SelfMove == 1)
        {
            SendMove(_char.Id, _char.X, _char.Y, dir);         // 4.95 (legacy): 0x0C at DEST starts the timed walk animation
            // Let the client animate the step, THEN send 0x04 to complete it. The client blocks on the
            // commit anyway (it won't send the next walk until it arrives), so sleeping this session's
            // thread here just paces the step to walk speed — nothing else is waiting on it.
            if (V495WalkMs > 0) Thread.Sleep(V495WalkMs);
            SendXy();                                          // 0x04 completes the step + camera
        }
        else if (V495SelfMove == 2)
        {
            // 4.95 default: legs WITHOUT overshoot. Send 0x0C with the SOURCE tile as the destination.
            // The client is still on (fromX,fromY), so start-walk's reposition guard (current==dest)
            // SKIPS the logical=dest move -- no overshoot -- but still sets walk-active=1 and starts the
            // anim timer (both are before the guard), so the legs cycle. Then 0x04 does the smooth
            // camera scroll that actually carries the character to the new tile.
            SendMove(_char.Id, (ushort)fromX, (ushort)fromY, dir);
            SendXy();
        }
        else if (V495SelfMove == 3)
        {
            // Diagnostic: send NOTHING for self-walk. The 4.95 client has an authoritative LOCAL
            // self-walk controller (proven live: selfWalkAnim @0x48f2c0 fires on keypress by itself).
            // Result: exactly one PERFECT animated step, then the client blocks awaiting a server ack.
        }
        else if (V495SelfMove == 5)
        {
            // 4.95 default: let the LOCAL controller animate the whole step (legs+slide+camera), then
            // unblock. We send nothing immediately, sleep past the local animation, and only then send
            // 0x04 to release the next-step gate. Because the animation has already finished (and
            // unregistered itself), 0x04's move-commit unregister is a no-op -> legs are NOT cancelled.
            // The client won't send the next walk until this ack lands, so sleeping this thread just
            // paces the walk cadence; nothing else waits on it.
            if (V495AckMs > 0) Thread.Sleep(V495AckMs);
            SendXy();
        }
        else if (V495SelfMove == 6)
        {
            // Like mode 5, but the unblock 0x04 reports the SOURCE tile, not the destination. The client
            // tracks a server-anchor that lags its local (animated) position by one tile; sending dest
            // makes 0x04's camera scroll (dest - anchor) fire a full tile AFTER the animation stopped =
            // the "+1 cell teleport at the end". Sending source matches the anchor, so the scroll delta
            // is ~0: it unblocks the next step without re-scrolling a camera the local walk already moved.
            // (_char stays at dest for server-side collision; only the 0x04 payload lags by one.)
            if (V495AckMs > 0) Thread.Sleep(V495AckMs);
            SendXyAt((ushort)fromX, (ushort)fromY);
        }
        else if (V495SelfMove == 7)
        {
            // 4.95 DEFAULT (RTK-faithful, fast-move aware — see RTK clif_parsewalk's FLAG_FASTMOVE gate).
            // clientFast comes straight from this walk's step-counter high bit (no toggle tracking needed).
            if (clientFast)
            {
                // Client-authoritative: the client already moved/animated/scrolled itself. Send NOTHING on
                // a good step (RTK skips the self-walk packet here). 0x04 stays reserved for corrections
                // (desync/block, handled above). This is the smooth, self-paced walk.
            }
            else if (V495SlowMove == 5)
            {
                // 4.95 DEFAULT (fast-move OFF): the 0x26 self-walk packet — the SAME primitive 5.33 uses.
                // Proven by RE that 0x26 is NOT dead on 4.95 (the no-op 0x44fb80 in the main opcode table
                // fooled us, exactly like the 0x08 stats case): the client pre-dispatches 0x26 through the
                // self-entity vtable (+0x38 @0x4cf038 -> 0x48eb40) to handlerB @0x4903d0, which
                //   (a) move-commits (0x48f160) the pending step to (destX,destY) — completing it WITHOUT
                //       the camera-scroll a 0x04 forces (move-commit stores the passed view anchor and
                //       respects realm-center, identical to the fast-move-ON local path), and
                //   (b) starts the next step's leg anim (selfWalkAnim 0x48f2c0) with [+0x65f3]=1 = the
                //       "complete locally, don't wait" flag, so it animates 0->3->0 and finishes on its own
                //       (no frameCtr-2 freeze, no 0x04 needed to unblock).
                // So one 0x26 per step = smooth legs + continuous movement + correct camera (incl. realm).
                // Send the SOURCE tile (the step being confirmed as complete) exactly like 5.33.
                SendSelfWalk(dir, (ushort)fromX, (ushort)fromY);
            }
            else
            {
                // ---- legacy fallbacks (P1998_V495_SLOW_MOVE != 5), kept for comparison ----
                // Realm-center ON: camera FROZEN. These paths REQUIRE a 0x04 to unblock the next step
                // (0x0C commits the tile but never clears the walk gate — the client freezes at frameCtr 2
                // awaiting a 0x04). Animate legs source-anchored (0x0C from-tile, no scroll) then complete
                // with a NO-SCROLL 0x04 (out-of-bounds anchor => camera fn no-ops, completion still runs).
                if (_realm != 0)
                {
                    SendMove(_char.Id, (ushort)fromX, (ushort)fromY, dir);
                    if (V495AckMs > 0) Thread.Sleep(V495AckMs);
                    SendXyCommitNoScroll();
                }
                else
                // Server-authoritative (fast-move OFF): the client makes NO local prediction — it waits for
                // us to assign the step. Drive it the same way peers are driven: 0x0C runs start-walk
                // (0x462320) => legs + logical->dest + source->dest interpolation (NOT a slide). See
                // V495SlowMove. (0x26 self-walk — RTK's choice — is a genuine no-op on 4.95: 0x44fb80 is
                // `mov al,1; ret`, so it cannot drive the step; 0x0C is what the 4.95 client honors.)
                switch (V495SlowMove)
                {
                    case 1:  // 0x0C(DEST): legs but overshoots to +2 (logical jumps to dest at frame 0)
                        SendMove(_char.Id, _char.X, _char.Y, dir);
                        break;
                    case 2:  // DEFAULT: 0x0C(SOURCE) anchors the legs at the from-tile (no overshoot)...
                        SendMove(_char.Id, (ushort)fromX, (ushort)fromY, dir);
                        if (V495AckMs > 0) Thread.Sleep(V495AckMs);  // ...let the legs animate source->dest...
                        SendXy();                                    // ...then 0x04 commits logical=dest (lands the step)
                        break;
                    case 3:  // overshoot variant: 0x0C(DEST) + delayed commit (compare against 2)
                        SendMove(_char.Id, _char.X, _char.Y, dir);
                        if (V495AckMs > 0) Thread.Sleep(V495AckMs);
                        SendXy();
                        break;
                    case 4:  // 0x0C(SOURCE) only: source-anchored legs, no commit (may snap back)
                        SendMove(_char.Id, (ushort)fromX, (ushort)fromY, dir);
                        break;
                    default: // 0 = legacy 0x04-only slide (no legs)
                        SendXy();
                        break;
                }
            }
        }
        else
        {
            // P1998_V495_SELF_MOVE=0: NO 0x0C. The self sprite stays centered; 0x04's camera scroll
            // (@0x44c660, decaying -10/-12 offsets) is the motion. Smooth, but no leg cycle.
            SendXy();
        }
        Log.Info($"   -> walk dir={dir} ({fromX},{fromY})->({_char.X},{_char.Y}) obj={obj}");

        // Walking straight up TO a board opens it. On 4.95 self-walk is client-local: pressing INTO the solid
        // board tile is blocked by the client and no packet reaches us (so the blocked-branch hook above can't
        // fire on a bump). Instead we catch the approaching step — the moment you land on the tile directly
        // south of a board while facing north, TryOpenBoardSign matches (X, Y-1). This is the "walk up to the
        // board and it opens" case; the turn-in-place case is handled in HandleTurn. Runs before the scripted
        // tiles below so a fall-room warp can't fire on the same step and strand an open board on the old map.
        TryOpenBoardSign();

        // The step is complete and the player stands on the new tile — run the after-step scripted tiles
        // (foraging, mythic fall-rooms). A fall-room warps, so this must come last (nothing follows it).
        OnScriptedTileStep();
    }

    // 5.33 turn (0x11 = "side"): the client reports a new facing but does NOT move. We update facing and
    // echo the side packet so the character turns in place. (Treating this as a walk — which we briefly
    // did — forces a step the client never intended, desyncing position and defeating collision.)
    private void HandleTurn(byte[] dec)
    {
        // Held players don't pivot either — the same rule the mob side follows (a frozen mob holds its
        // facing). Unlike the walk gate this needs no snap-back: a turn is fire-and-forget on 4.95, with no
        // ack the client is waiting on, so simply not broadcasting it is enough. The turner's own screen has
        // already pivoted locally; that is cosmetic and rights itself on the next real move.
        if (Asleep) return;

        byte side = dec.Length > 0 ? dec[0] : (byte)0;
        _facing = (byte)(side & 3);
        MarkDirty();   // facing persists (Character.Dir); like position it only rides the autosave/disconnect flush
        SendSide(_char.Id, _facing);
        _world.Broadcast(_char.Map, p => p.SideEntity(_char.Id, _facing), except: this);   // peers see us turn
        Log.Info($"   -> turn side={_facing} @ ({_char.X},{_char.Y})");
        TryOpenBoardSign();   // RTK onSign: turning to face a board (looking north) opens it
    }

    // RTK board-sign (on_event.lua onSign -> selectBulletinBoard -> showBoard): while looking NORTH (back to
    // screen), if the tile directly in front of us (one up) is a registered board sprite, open THAT board
    // straight to its posts — bypassing the `b` board-list menu. This is the "walk up to the Buya arena
    // schedule board and it opens" behaviour. Fired both on a turn-in-place and on a blocked north step (bumping the
    // board), so either approach triggers it. Content.TryBoardAt applies RTK's ±1 X tolerance for wide boards.
    // Returns true when a board was opened (the north-facing action was consumed by the sign).
    private bool TryOpenBoardSign()
    {
        if ((_facing & 3) != 0) return false;                                          // only when looking north (RTK side==0)
        if (!Content.TryBoardAt(_char.Map, _char.X, _char.Y - 1, out var boardId)) return false;
        SendBoardPosts(boardId, popup: true);                                           // RTK showBoard: pop the window open (unsolicited)
        Log.Info($"   -> BOARD-SIGN open board {boardId} facing ({_char.X},{_char.Y - 1}) on map {_char.Map}");
        return true;
    }

    // "@boardobj" — board-sign calibration probe. Reports the tile you're FACING, the object sprite id sitting
    // there (RTK's board sprites are 1619/1620; the 4.95 id is TBD), and whether a BoardLocations row already
    // matches. Stand below a board looking north and run this to capture the (map,x,y) for BoardLocations.csv.
    private void BoardObjProbe()
    {
        int dx = 0, dy = 0;
        switch (_facing & 3) { case 0: dy = -1; break; case 1: dx = 1; break; case 2: dy = 1; break; case 3: dx = -1; break; }
        int fx = _char.X + dx, fy = _char.Y + dy;
        string dir = (_facing & 3) switch { 0 => "N", 1 => "E", 2 => "S", _ => "W" };
        var md = MapData.For(_char.Map, _char.MapXs, _char.MapYs);
        int obj = (md != null && fx >= 0 && fy >= 0 && fx < _char.MapXs && fy < _char.MapYs) ? md.Obj(fx, fy) : -1;
        bool match = Content.TryBoardAt(_char.Map, fx, fy, out var bid);
        string boardName = match ? (Boards.Find(bid)?.Name ?? "?") : "-";
        SendLog($"boardobj: map {_char.Map} you@({_char.X},{_char.Y}) facing {dir} -> tile ({fx},{fy}) obj={obj} | board={(match ? $"{bid} \"{boardName}\"" : "none")}");
        SendLog($"  to register: add  {_char.Map},{fx},{fy},<BoardId>  to BoardLocations.csv then @reload");
        // Board ids come straight from Boards.All so this hint can't go stale when the roster changes; a few
        // per line because one chat line can't hold the whole roster.
        foreach (var chunk in Boards.All.Select(b => $"{b.Id}={b.Name}").Chunk(4))
            SendLog("  boards: " + string.Join("  ", chunk));
        Log.Info($"   -> @boardobj map {_char.Map} facing {dir} tile ({fx},{fy}) obj={obj} match={match} board={bid}");
    }

    // 0x20 = the 'o' / Open key. In NexusTK this TOGGLES the door object I'm facing between its closed and open
    // graphic in place (RTK open.lua `openDoors`: setObject(m,x,y, closed<->open) — e.g. Buya door 342<->364;
    // some doors are 3 tiles wide). The graphic swap itself is shared world state (everyone on the map is told
    // to redraw); whether it actually changes PASSABILITY is downstream, via ObjectFlags reading the door's
    // (now-mutated) object id off the shared map — CORRECTED 2026-07-26: an older version of this comment said
    // "collision is the ground pass flag only," which predates the SObj object-wall layer (see
    // MapData.BlockedMove) that later made object graphics collision-relevant too. Some RTK doors have no
    // open-graphic pair defined anywhere in openDoors at all (nothing to toggle to), so those are configured
    // Doors.ForceOpen instead of faking a swap — see Server/Doors.cs. Locking is a separate, optional gate in
    // front of the toggle (Doors config: Locked + an optional required item Key, RTK's own iron_key precedent)
    // — checked here before the swap runs. Entering a building is done by WALKING onto its warp tile
    // (HandleWalk warp-precedence) — NOT by 'o', so 'o' never warps.
    //
    // Rendering the swap uses the server->client 0x06 CELL-PATCH packet (client handler 0x44fb90, RE'd via disx):
    //   body = startX(u16BE) startY(u16BE) width(u8) height(u8) then width*height cells, each = ground(u16BE)
    //   object(u16BE). The client writes each cell into its live map array and redraws the object layer over the
    //   patched rectangle (tail 0x44df30 re-renders objects regardless of whether the ground word changed). We
    //   keep the ground word unchanged (tile+pass) and set only the object word to the toggled door id.
    // The next 'o' reads the mutated object back (via MapData.SetObj) and toggles the door closed again.
    private void HandleOpen(byte[] dec)
    {
        int dx = 0, dy = 0;
        switch (_facing & 3) { case 0: dy = -1; break; case 1: dx = 1; break; case 2: dy = 1; break; case 3: dx = -1; break; }
        int fx = _char.X + dx, fy = _char.Y + dy;
        var md = MapData.For(_char.Map, _char.MapXs, _char.MapYs);
        if (fx < 0 || fy < 0 || fx >= _char.MapXs || fy >= _char.MapYs || md is null) return;

        // Locked doors (Doors config): gate the toggle on a key before falling through to the normal RTK
        // open/close swap below. A configured-but-unlocked or not-configured-at-all tile just toggles as usual.
        var cfg = Doors.For(_char.Map, (ushort)fx, (ushort)fy);
        if (cfg is { Locked: true } && !Doors.IsUnlocked(_char.Map, (ushort)fx, (ushort)fy))
        {
            if (cfg.Key is not null && CountItem(cfg.Key) > 0)
            {
                if (cfg.ConsumeKey) TakeItem(cfg.Key, 1);
                Doors.Unlock(_char.Map, (ushort)fx, (ushort)fy);
                SendMiniText("You unlock the door.");
            }
            else
            {
                SendMiniText("The door is locked.");
                return;
            }
        }

        ushort obj = md.Obj(fx, fy);

        // A PER-TILE run (Doors.csv ClosedObj/OpenObj) wins over RTK's shared id-swap table. It's the only way
        // to describe a door whose open and closed graphics aren't a function of the object id alone — see
        // Doors.Toggle. Falls through to the shared table when this tile has no run configured, which is
        // every door in the game bar the handful listed in Doors.csv.
        // 0xFFFF is unreachable for a 14-bit object id, so an off-map cell simply fails the "is it open?"
        // comparison rather than indexing past the row; the run-bounds check below then rejects it.
        var perTile = Doors.Toggle(_char.Map, (ushort)fx, (ushort)fy,
                                   cx => cx >= 0 && cx < _char.MapXs ? md.Obj(cx, fy) : (ushort)0xFFFF);
        var door = perTile is null ? Content.DoorToggleFor(obj) : null;
        Log.Info($"   -> OPEN('o') facing={_facing} front=({fx},{fy}) obj={obj} " +
                 $"door={(perTile is not null ? "per-tile" : door is null ? "no" : "yes")}");
        if (perTile is null && door is null) return;

        var (sx, objs) = perTile ?? (fx + door!.Value.StartDx, door.Value.Objs);
        if (sx < 0 || sx + objs.Length > _char.MapXs) return;   // door run would fall off the map edge

        // Mutate the shared map (so a later 'o' can toggle it back), then tell every client on the map to redraw.
        for (int i = 0; i < objs.Length; i++) md.SetObj(sx + i, fy, objs[i]);
        _world.Broadcast(_char.Map, p => p.PatchObjRow((ushort)sx, (ushort)fy, objs));
    }

    // The door-object toggle table now lives in game-data/DoorObjects.csv (RTK open.lua `openDoors`), loaded
    // into Content.DoorSwaps / Content.DoorDeltas and queried via Content.DoorToggleFor.
    //
    // RTK's 17408+ ids are a LATER client's object space and can't be used verbatim (4.x objects are a 14-bit
    // field, max 16383) — but that does NOT mean those doors are absent here, only renumbered. The city gates
    // are the case that mattered: RTK's 4-tile-wide run 17670-17673 <-> 17680-17683 is, in the 4.95 object
    // table, 5-8 <-> 15-18 (same 4-wide shape, same +10 pairing, and the map data agrees — every gate in the
    // game has wall pieces 1-4 to its left and 9-12 to its right). Read the STATE off SObj.tbl, not the id
    // order: 5-8 are flagged 0x0f (solid on all four sides) = CLOSED, 15-18 are 0x00 = OPEN. So Kugnae's north
    // gate ships open and its south gate ships shut, which is why 'o' appeared broken in opposite ways at the
    // two ends of the same city. The closed ids carry defaultOpen=1 so gates start open (Content.DoorDefaultOpen).
    // Not ported: RTK's other 17408+ entries (17423/17425/17428/17430 pairs, the 3-wide 17408/17417), whose
    // 4.95 counterparts nobody has identified — find them the same way, by matching run width against SObj flags.

    // Server->client 0x06 CELL PATCH: redraw a horizontal run of cells starting at (startX, y), setting each
    // cell's object to objs[i] while keeping its ground (tile + passability) unchanged. This is how doors
    // open/close on the client (see HandleOpen). Header: startX(u16BE) y(u16BE) width(u8) height=1(u8).
    //
    // Wire: startX(u16BE) y(u16BE) width(u8) height=1(u8) then, PER CLIENT VERSION:
    //   4.95  ground(u16BE) object(u16BE)                 -- 2 shorts, ground word carries passability
    //   5.33  tile(u16BE) pass(u16BE) object(u16BE)       -- 3 shorts, same shape as the terrain stream
    //
    // The cell shape is NOT optional and NOT a guess. 5.33's handler (sub_469060) reads three BE u16 per
    // cell unconditionally — three calls to the stream reader storing to [esi], [esi+2], [esi+4], with a
    // six-byte stride (`lea ecx,[eax+eax*2]` then `[edx+ecx*2]`). There is no length check and no
    // two-short path. Feeding it a 2-short run made it consume the NEXT cell's bytes as its own and read
    // past the end of the body, so a door toggle repainted the strip with garbage that only corrected
    // itself on the next full refresh. That was the reported 'o' bug.
    //
    // Note on the middle short: 5.33 merges it as `new = old ^ ((old ^ read) & 1)` — it takes ONLY bit 0
    // and preserves the rest of whatever was already in the cell. So passability is a single bit there,
    // and sending 3 (our 4.x-derived value) is equivalent to sending 1.
    //
    // Ground DOES go through TileTranslation: identity for sheet 1, table lookup for sheet 2. This strip
    // has to move with the terrain around it rather than stay a tile off.
    private void SendObjRow(ushort startX, ushort y, ushort[] objs)
    {
        var md = MapData.For(_char.Map, _char.MapXs, _char.MapYs);
        if (md is null || objs.Length == 0) return;
        var d = new List<byte>();
        d.AddRange(Be(startX));
        d.AddRange(Be(y));
        d.Add((byte)objs.Length);   // width
        d.Add(1);                   // height (single row)
        for (int i = 0; i < objs.Length; i++)
        {
            int mx = startX + i;
            // Source the WHOLE ground word: its top two bits are the legacy sheet selector, and
            // TileTranslation needs them.
            ushort word = md.GroundWord(mx, y);
            MapCell.Write(d, TileTranslation.Ground(word, _ver), md.Pass(mx, y),
                          TileTranslation.Object(objs[i], _ver), _ver);
        }
        SendMap(0x06, _gameInc++, d.ToArray(),
                $"cellpatch(0x06) ({startX},{y}) w{objs.Length} " +
                $"{(_ver == ClientVersion.V533 ? "3-short" : "2-short")} cells objs=[{string.Join(",", objs)}]");
    }
    public void PatchObjRow(ushort startX, ushort y, ushort[] objs) => SendObjRow(startX, y, objs);   // peer-facing (0x06)

    // 0x1b = client setting toggle (F-keys). body[0] = setting id (matches RTK's settings-parse switch):
    //   0x07 = Realm center (F4, camera lock)   0x09 = Fast move   (others logged, not yet acted on).
    // For realm-center we flip the flag and re-apply it via an in-place refresh (0x15 mapinfo carries the
    // realm byte to the client's camera rebuild @0x44c570), mirroring RTK's case 0x07 (sendmapinfo/setpos).
    // Re-send the map-entry trio (0x15 mapinfo + position + self-look) in place, then re-assert the peers and
    // mobs the 0x15 rebuild drops. Used whenever a byte the client reads ONLY at map-entry has to change live:
    // both the F4 realm-center camera lock and the weather arm/disarm render byte ride the 0x15 mapinfo cell,
    // and re-sending it is the only way to move them without an actual map change.
    internal void RefreshMapInPlace()
    {
        SendMapInfo(_char.Map, _char.MapXs, _char.MapYs, "Nexus", 232, _gameInc++);
        SendXy();
        SendSelfLook();
        RedrawWorld();
    }

    private void HandleSetting(byte[] dec)
    {
        byte setting = dec.Length > 0 ? dec[0] : (byte)0;
        if (setting == 0x07)
        {
            _realm ^= 1;
            if (_realm != 0)
            {
                // Freeze the camera at the origin it's showing RIGHT NOW. The follow-camera keeps the map
                // top-left at (X - vx, Y - vy) with the edge-aware anchor, so that's the current origin —
                // capture it, and ViewAnchor() will hold the origin there while realm-center stays on.
                var (cvx, cvy) = EdgeAwareAnchor(_char.X, _char.Y);
                _lockOx = _char.X - cvx;
                _lockOy = _char.Y - cvy;
            }
            // Re-send the entry trio in place so the 0x15 realm byte reconfigures the camera at the
            // current position (no map change; same map/coords).
            SendMapInfo(_char.Map, _char.MapXs, _char.MapYs, "Nexus", 232, _gameInc++);
            SendXy();
            SendSelfLook();
            RedrawWorld();   // 0x15 rebuild drops FOREIGN entities — re-assert peers + mobs so they don't vanish
            SendMessage(_realm != 0 ? "Realm-centered   :ON" : "Realm-centered   :OFF");   // RTK clif_changestatus case 0x07 (verbatim text)
            Log.Info($"   -> setting 0x07 Realm-center = {(_realm != 0 ? "ON" : "OFF")} (refreshed in place)");
        }
        else if (setting == 0x09)
        {
            _fastMove = !_fastMove;   // client toggled fast-move; keep our model in sync
            SendMessage(_fastMove ? "Fast Move        :ON" : "Fast Move        :OFF");   // RTK clif_changestatus case 0x09 (verbatim text)
            Log.Info($"   -> setting 0x09 Fast-move = {(_fastMove ? "ON (client-authoritative)" : "OFF (server-authoritative)")}");
        }
        else if (setting == 0x00)
        {
            // 0x1b sub-0 is sent by TWO different gestures, told apart only by dec[1] (live-captured
            // 2026-08-16): the 'r' Ride key sends `00 01 00` (dec[1]=0x01); opening the Options menu (F10)
            // sends a bare `00 00` (dec[1]=0x00). Only the ride shape may drive TryRideHorse — routing the
            // F10 packet there was dismounting anyone who opened their options while on a horse.
            if (dec.Length >= 2 && dec[1] == 0x01)
            {
                // 'r' Ride (RTK clif_changestatus case 0x00 -> clif_findmount). Unlike @ride/@mount (a plain
                // GM toggle), this is tied to a real world "horse" mob (MobDef key "horse", e.g. the wild
                // horses roaming Buya/Horse Valley): mounting rides one away (despawns it) and dismounting
                // sets it back down in front of you.
                if (_char.Hp == 0) SendMiniText("Spirits can't do that.");
                else TryRideHorse();
            }
            else
            {
                // F10 / Options-menu open — the client just announcing the menu is up. No state change.
                Log.Info($"   -> setting 0x00 options-open (F10), no-op [{Convert.ToHexString(dec)}]");
            }
        }
        else if (setting == 0x02)
        {
            // Shift+G — toggle "sociable/group" (whether others may group with you). Persisted; the profile
            // window (0x39 group byte / 0x34 status cell) reads it, so reopening the profile shows the change.
            //
            // Turning it OFF while you are in a group LEAVES that group — this toggle is the native "leave"
            // gesture, and the only one. It reads naturally ("I'm no longer grouping") and it's the sole
            // group control the client offers a player who isn't looking at somebody else's profile: the
            // profile Group button (0x2E) can only ADD, and its kick branch is the leader's alone. Without
            // this a non-leader could only get out by logging off.
            _char.Grouped = !_char.Grouped;
            SaveChar();
            SendMessage(SettingLine("Join a group", _char.Grouped));
            Log.Info($"   -> setting 0x02 Group/sociable = {(_char.Grouped ? "ON" : "OFF")}");
            if (!_char.Grouped && _party is not null) RemoveFromParty(this);
        }
        else if (setting == 0x08)
        {
            // Toggle "exchange/trade" (whether others may exchange with you). Same profile cells; persisted.
            _char.Exchange = !_char.Exchange;
            SaveChar();
            SendMessage(SettingLine("Exchange", _char.Exchange));
            Log.Info($"   -> setting 0x08 Exchange = {(_char.Exchange ? "ON" : "OFF")}");
        }
        else if (setting == 0x0A)
        {
            // Clan whisper. RTK keeps this one OUT of the settingFlags word (status.clan_chat), so we do too.
            _char.ClanChat = !_char.ClanChat;
            SaveChar();
            SendMessage(SettingLine("Clan whisper", _char.ClanChat));
            Log.Info($"   -> setting 0x0A Clan whisper = {(_char.ClanChat ? "ON" : "OFF")}");
        }
        else if (SettingLabels.TryGetValue(setting, out var label))
        {
            // The remaining Options-menu toggles. Each is one bit of Character.SettingFlags (RTK
            // clif_changestatus cases 1/3/4/5/6/13/14/15) and the client only reports the FLIP, never the
            // state — so our stored bit and the client's checkbox stay in sync purely by both starting from
            // the same defaults. Same contract as fast-move.
            bool on = _char.ToggleSetting(setting);
            SaveChar();
            SendMessage(SettingLine(label, on));
            Log.Info($"   -> setting 0x{setting:X2} {label} = {(on ? "ON" : "OFF")}");
            // Weather is the only one of these with a packet behind it, and it needs BOTH halves to take effect
            // live: the 0x1F state (intensity) AND the 0x15 mapinfo render byte (4=armed / 5=disarmed), which is
            // the master "draw weather on this map" switch — the 0x1F state alone isn't enough (see SendMapInfo).
            // That byte is normally only sent at map-entry, so without re-sending mapinfo here, toggling weather
            // off left the map ARMED and the effect kept drawing until the next map change. RefreshMapInPlace
            // re-sends it now (mirrors the F4 realm-center refresh, which moves the same cell the same way).
            if (setting == 0x06) { SendWeather(); RefreshMapInPlace(); }
        }
        else
        {
            Log.Info($"   -> setting 0x{setting:X2} (not handled)");
        }
    }

    // RTK's clif_changestatus writes each toggle as "<label><pad>:ON|OFF" left-padded to a fixed column, and
    // the live 4.95 capture of the fast-move toggle ("Fast Move        :OFF") shows the client renders the
    // padding verbatim — it is part of the string, not the widget. Column 17 reproduces every RTK line.
    private static string SettingLine(string label, bool on) => $"{label,-17}:{(on ? "ON" : "OFF")}";

    // 0x1b sub-command -> Options-menu label, for the toggles that are nothing but a settingFlags bit.
    // Ride (0), group (2), realm (7), exchange (8), fast-move (9) and clan whisper (10) all do extra work
    // and are handled above; these just flip and announce.
    private static readonly Dictionary<byte, string> SettingLabels = new()
    {
        [0x01] = "Listen to whisper",
        [0x03] = "Listen to shout",
        [0x04] = "Listen to advice",
        [0x05] = "Believe in magic",
        [0x06] = "Weather change",
        [0x0D] = "Hear sounds",
        [0x0E] = "Show Helmet",
        [0x0F] = "Show Necklace",
    };

    // 0x38 = HARD REFRESH (Ctrl+R). The client dims the screen (gray mask) and asks the server to re-assert
    // authoritative state. We mirror RTK's clif_refresh: re-send mapinfo (0x15 — on 4.95 this triggers a
    // client map RELOAD, which is what clears the gray mask) + xy (0x04 — the re-anchor primitive: authoritative
    // position + recentered camera) + self look (0x33) + re-draw nearby entities. This is the SAME in-place
    // refresh the F4 realm-center toggle performs. (RTK also emits a trailing 0x22 03 terminator, but 0x22 is
    // the default no-op on 4.95 — remap slot 0x2a — so it is not needed to end the refresh here.)
    private void HandleRefresh(byte[] dec)
    {
        // RTK's clif_refresh always RECENTERS (clif_sendxy uses the edge-aware centered anchor, with no
        // realm-center handling) — a hard refresh is a "reset to the authoritative centered view". So if
        // realm-center is on and we're parked off-center, re-lock the freeze at the NEW centered origin so
        // the recenter takes effect AND realm-center keeps working afterward (re-anchored at center).
        if (_realm != 0)
        {
            var (cvx, cvy) = EdgeAwareAnchor(_char.X, _char.Y);
            _lockOx = _char.X - cvx;
            _lockOy = _char.Y - cvy;
        }
        SendMapInfo(_char.Map, _char.MapXs, _char.MapYs, "Nexus", 232, _gameInc++);
        SendXy();          // 0x04: authoritative (X,Y) + recentered camera (now centered even under realm)
        SendSelfLook();    // 0x33: redraw self on the reloaded map
        PrimeViewport("refresh");   // 0x06: re-fill the window — Ctrl+R is the player's "fix my screen" key
        RedrawWorld();     // re-assert peers + mobs + ground items the 0x15 reload dropped
        Log.Info($"   -> refresh (0x38, Ctrl+R) — recentered at ({_char.X},{_char.Y}){(_realm != 0 ? " (realm re-locked at center)" : "")}");
    }

    // 0x26 self-walk (moving player's OWN client): dir(u8) oldX(u16BE) oldY(u16BE) viewX(u16BE)
    // viewY(u16BE) 0. The client animates the self character stepping one tile in `dir` from
    // (oldX,oldY) and re-anchors the camera to (viewX,viewY). A plain 0x0C would teleport (slide).
    // viewX/viewY use Mithia's edge-aware anchor so the camera stops at map edges.
    private void SendSelfWalk(byte dir, ushort oldX, ushort oldY)
    {
        var (vx, vy) = ViewAnchor();
        var d = new List<byte>();
        d.Add(dir);
        d.AddRange(Be(oldX));
        d.AddRange(Be(oldY));
        d.AddRange(Be(vx));
        d.AddRange(Be(vy));
        d.Add(0);
        SendMap(0x26, _gameInc++, d.ToArray(), $"self-walk(0x26) ({oldX},{oldY}) dir={dir} view=({vx},{vy})");
    }

    // 0x11 side: entityId(u32BE) side(u8) 0. Turns the entity in place (no movement).
    private void SendSide(uint id, byte side)
    {
        var d = new List<byte>();
        d.AddRange(Be32(id));
        d.Add(side);
        d.Add(0);
        SendMap(0x11, _gameInc++, d.ToArray(), $"side(0x11) id={id} side={side}");
    }

    // The per-cell `pass` short we STREAM to the 5.x client (honors the passtest:N diagnostic). This is
    // the 4.x ground top-2-bits (value 3 = blocked on real maps like TK0 — water/cliffs — 0 = walkable);
    // it feeds both the wire format AND server collision (see Blocked, which now honors it).
    private static ushort PassAt(MapData map, int mx, int my)
    {
        if (MapDiag.StartsWith("passtest:"))
            return (ushort)((mx % 5 == 2) ? PassTestN : 0);   // wall-lines every 5 tiles
        return map.Pass(mx, my);
    }

    // Server-side collision: is this destination cell solid? The GROUND passability flag alone decides —
    // the ground word's top-2-bits (value 3 = blocked, 0 = walkable; 1/2 never occur). On real world maps
    // this is NOT 0: e.g. Kugnae TK0 has 14841 flagged cells — water, cliffs, out-of-bounds, AND the ground
    // under every wall (map authors bake wall footprints into this layer). Confirmed against the heaviest
    // wall objects on the real maps: obj 1519-1522 (~2000 placements each) sit on pass=3 ground 100% of the
    // time. So walls are already caught by pass; the object layer is purely VISUAL.
    //
    // The object layer is therefore NOT a collision source. It used to be (obj != 0 blocked), which made the
    // player "stuck on shadows" — shadows, flat rugs, ground-decor are objects on walkable ground and were
    // wrongly blocked. The authoritative RTK reference server agrees exactly: map_canmove() has `if(obj)
    // return 1;` COMMENTED OUT and collides on the pass layer only; object_flag_init() even parses SObj.tbl
    // but leaves `objectFlags[z]=flag;` commented out — the object table's flags are height/draw-order, not
    // passability (they don't predict blocking: wall objects 1519-1522 have flag high-byte 0x00, while many
    // 0x0f-flagged objects sit on fully-walkable ground). Doors that ARE closed have pass=3 and still block
    // (open them via 'o'/0x20); door tiles that are warps get warp-precedence in HandleWalk before this check.
    private static bool Blocked(MapData map, int mx, int my)
    {
        if (MapDiag.StartsWith("passtest:"))
            return (mx % 5 == 2) && PassTestN != 0;   // synthetic wall-lines
        return map.Pass(mx, my) != 0;
    }

    // 0x0C move: entityId(u32BE) X(u16BE) Y(u16BE) dir(u8). Handler 0x4502c0 finds the entity
    // by id (0x45cb80) and animates it to (X,Y) facing dir.
    private void SendMove(uint id, ushort x, ushort y, byte dir)
    {
        var d = new List<byte>();
        d.AddRange(Be32(id));
        d.AddRange(Be(x));
        d.AddRange(Be(y));
        d.Add(dir);
        SendMap(0x0C, _gameInc++, d.ToArray(), "move(0x0C)");
    }

}
