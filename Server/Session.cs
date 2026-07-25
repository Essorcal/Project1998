using System.Net.Sockets;
using System.Text;
using Protocol.Tk495;
using Shared;

namespace Server;

/// <summary>
/// One client connection. Frames incoming packets, decrypts, dispatches, and replies.
/// This is the disposable 4.95 adapter behavior; the reusable world logic will live elsewhere.
/// </summary>
public sealed class Session
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly int _port;
    private readonly string _remote;
    private readonly CharacterStore _store;
    private readonly World _world;   // the shared world (players + mobs); every broadcast goes through it
    private string _user = "?";
    private bool _enteredWorld;   // true once world entry loaded _char; gates the disconnect save

    // --- client version, tagged by the port the connection arrived on (unified dual-client server) ---
    // 4.95 speaks the original protocol (local map files; incoming 0x06 = walk-sync). 5.33 streams
    // terrain from the server (incoming 0x05/0x06 = map-data requests -> reply 0x06). Rather than sniff
    // the wire we give 5.33 its own listener ports and stamp the version here; the proven 4.95 path is
    // never entered by a 5.33 session. Login 2000 / game 2005 = V495; login 2001 / game 2006 = V533.
    public enum ClientVersion { V495, V533 }
    private readonly ClientVersion _ver;
    private bool IsLoginPort => _port == 2000 || _port == 2001;

    // --- world-light diagnostic knobs (env-tweakable, no rebuild needed to sweep) ---
    //   NEXUS_LIGHT      integer 0..65535, the map light/darkness value (default 232, proven bright on 4.95)
    //   NEXUS_LIGHT_FMT  how to encode it on the 0x15: "beu16" (default, 4.95), "leu16", or "u8"
    // 5.33 draws terrain black with the 4.95-proven be-u16 232; sweeping these isolates whether the
    // client reads the light field at a different width/endianness (leading 00 -> light 0 -> black).
    private static readonly int LightValue =
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_LIGHT"), out var lv) ? lv : 232;
    private static readonly string LightFmt =
        (Environment.GetEnvironmentVariable("NEXUS_LIGHT_FMT") ?? "beu16").Trim().ToLowerInvariant();

    // 5.33 terrain-stream tile offset. The 4.x .map stores raw frame indices (the 4.95 client draws
    // them as-is); some client eras expect index+1 (0 = "no tile", client subtracts 1). Mithia 7.x
    // streams raw, so default 0 — but if 5.33 terrain comes out shifted by one frame, set NEXUS_TILE_OFF=1
    // (or -1) to correct it without a rebuild. Applied to the ground + object shorts, not passability.
    private static readonly int CellOff =
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_TILE_OFF"), out var co) ? co : 0;

    // 5.33 terrain-render diagnostic for the 0x06 stream (set via a .bat so PowerShell's `set` quirk
    // can't bite). "" = real map tiles. "sweep" = ramp the ground index across the whole visible rect
    // over the FULL 16-bit range (0..28550) so we can read off which indices actually draw a tile.
    // "solid:N" = fill with ground index N. Sweep/solid send the tile UNMASKED (real tiles are still
    // masked to 14 bits since 4.x ground packs passability in the top 2 bits).
    private static readonly string MapDiag =
        (Environment.GetEnvironmentVariable("NEXUS_MAP_DIAG") ?? "").Trim().ToLowerInvariant();

    // Server-side passability (collision). NEXUS_PASS=0 disables it (walk through anything) if the 4.x
    // top-2-bits polarity turns out wrong for a given map. Default on. Mithia 7.x: read_pass!=0 => blocked.
    private static readonly bool PassEnforce =
        (Environment.GetEnvironmentVariable("NEXUS_PASS") ?? "1").Trim() != "0";
    // Parsed block value for the passtest:N diagnostic (default 1); shared by the map stream + collision.
    private static readonly int PassTestN =
        MapDiag.StartsWith("passtest:") && int.TryParse(MapDiag.AsSpan(9), out var ptn) ? ptn : 1;

    // 4.95 self-walk: 0x0C starts a LOCAL-PREDICTION animation, entirely self-timed client-side (confirmed
    // live via Frida: the walk-frame counter at entity+0x18e advances on its own, ~90ms/tick, with ZERO
    // packets from us in between). But the client caps local prediction at exactly 2 ticks (~180ms) and
    // then FREEZES — it will not advance further until our 0x04 arrives, at which point it snaps straight
    // to completion. So 0x04 must land just AFTER that natural ~180ms window: too early truncates the
    // prediction (looks like an instant snap); too late just prolongs the freeze before the snap.
    // NEXUS_V495_WALK_MS tunes it (0 = old same-frame slide, sent before ANY tick can play).
    private static readonly int V495WalkMs =
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_V495_WALK_MS"), out var wm) ? wm : 200;

    // Self-walk drive mode for 4.95, chosen by static RE of the client's animation path.
    //   start-walk @0x462320  sets walk-active [+0x18c]=1 and registers the anim timer (0x41b5d0)
    //                         UNCONDITIONALLY, then repositions logical=dest ONLY if current!=dest.
    //   walk-render @0x44b140 draws screen = logical + forward_step*(frameCtr/4)  [dir->delta @0x44aad0].
    //   0x04 handler @0x44faf0 -> @0x44c660 sets decaying scroll offsets [+0xb8]=-12/[+0xbc]=-10 = a
    //                         SMOOTH camera scroll, independent of 0x0C.
    // So the OVERSHOOT (sprite lands on dest then slides past) comes only from the reposition, which is
    // gated on current!=dest. The LEG cycle comes from walk-active + anim timer, set before that gate.
    // Modes (NEXUS_V495_SELF_MOVE):
    //   0 = 0x04 only            -> smooth camera scroll, but NO legs (walk-active never set)
    //   1 = 0x0C(dest)+delay+0x04-> legacy; legs but forward overshoot + snap on the final step
    //   2 = 0x0C(SOURCE)+0x04    -> legs on (walk-active set) with reposition SKIPPED (dest==current);
    //                              still cancelled by 0x04's move-commit, so no visible legs.
    //   3 = send nothing         -> pure local walk; 1 PERFECT animated step, then the client blocks
    //                              awaiting a server ack (proven live: local controller does move+legs+camera).
    //   5 = delay + 0x04         -> DEFAULT: send nothing immediately so the LOCAL controller animates the
    //                              full step (legs + slide + camera), then after the anim completes send 0x04
    //                              to unblock the next step. By then move-commit's unregister is a no-op, so
    //                              the legs are NOT cancelled. NEXUS_V495_ACK_MS tunes the delay.
    //   7 = nothing on a good walk (RTK-faithful) -> DEFAULT: client moves/animates/scrolls locally; 0x04
    //       is sent ONLY as a correction (desync/block). Stops our per-step 0x04 from re-scrolling the
    //       camera the client already moved (the residual "wonkiness" + fighting realm-center).
    private static readonly int V495SelfMove =
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_V495_SELF_MOVE"), out var sm) ? sm : 7;

    // Delay (ms) before the mode-5 unblock 0x04. Must be >= the client's local walk animation (~4 frames,
    // ~360ms) so the 0x04 lands AFTER the legs finish and doesn't cancel them. Too short => truncated legs;
    // too long => sluggish walk cadence (the client gates the next step on this ack).
    private static readonly int V495AckMs =
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_V495_ACK_MS"), out var am) ? am : 360;

    // FAST-MOVE OFF (server-authoritative) response strategy for 4.95. When fast-move is off the client
    // makes NO local prediction — it sends the walk request and waits for the server to assign the step
    // before moving/animating. The old behavior (0x04 only) slides with no legs because 0x04's handler
    // (0x44faf0 -> move-commit) teleports the sprite and never runs the leg cycle. But 0x0C runs the SAME
    // start-walk (0x462320) a PEER uses: it sets walk-active(+0x18c)=1, frameCtr(+0x18e)=0, REGISTERS the
    // anim list (0x41b5d0), moves logical->dest, and sets up the source->dest screen interpolation
    // (0x44b090) — a full animated step with legs. And 0x0C does NOT branch self-vs-peer, so the self
    // animates exactly like the peers we already drive smoothly. Strategies:
    // The render (0x44b140) draws  screen = screen(entity.logical) + FORWARD_unit*(frameCtr/4)  where
    // FORWARD_unit is the dir delta (0x44aad0) and start-walk (0x462320) sets logical=DEST at frame 0 —
    // BUT ONLY IF the 0x0C tile != current logical (the reposition guard). So the tile we put in the 0x0C
    // decides the anchor:  0x0C(DEST) -> logical jumps to dest, sprite starts ON dest and drifts to +2
    // (OVERSHOOT); 0x0C(SOURCE) -> guard trips, logical stays at source, sprite animates source->dest
    // correctly, and the delayed 0x04(dest) commits the landing. Strategies:
    //   0 = 0x04 only            -> legacy: assigns tile + camera, but SLIDES (no legs).
    //   1 = 0x0C(dest) only      -> legs but OVERSHOOTS to +2 then needs a commit to snap back.
    //   2 = 0x0C(SOURCE)+delayed 0x04(dest) -> DEFAULT: source-anchored legs (no overshoot); the delayed
    //                               0x04 commits logical=dest AFTER the legs finish so it can't cancel them.
    //   3 = 0x0C(dest)+delayed 0x04 -> the overshoot variant (kept to compare against 2).
    //   4 = 0x0C(SOURCE) only    -> source-anchored legs but no commit -> may snap back to source.
    //   5 = 0x26 self-walk (DEFAULT) -> the real smooth primitive: routes to handlerB (0x4903d0) which
    //       move-commits the step + starts the next locally ([+0x65f3]=1, no wait, no 0x04, no forced
    //       scroll). Same packet 5.33 uses; respects realm-center. See HandleWalk for the full RE trail.
    private static readonly int V495SlowMove =
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_V495_SLOW_MOVE"), out var slm) ? slm : 5;

    // "Realm center" (F4 in RTK) — a CLIENT camera mode signalled by a flag byte in the 0x15 mapinfo
    // packet (RTK clif_sendmapinfo byte +12; our SendMapInfo body[7]). When ON the client locks the camera
    // dead-center on the character (the world scrolls under a fixed sprite); when OFF the camera uses the
    // edge-aware/offset behavior that can lag during a walk. The 4.95 client honors it: its 0x15 handler
    // (@0x44f8b0) reads this byte, computes (realm==0), and feeds it to the view/camera rebuild (@0x44c570).
    // NEXUS_V495_REALM=1 enables it to test whether a locked camera fixes the walk "wonkiness".
    private static readonly byte RealmCenter =
        Environment.GetEnvironmentVariable("NEXUS_V495_REALM") == "1" ? (byte)1 : (byte)0;

    // AA 00 13 7E 1B "CONNECTED SERVER\n"  (plaintext welcome, as the 6.x reference sends)
    private static readonly byte[] Welcome =
        BuildWelcome();

    private static byte[] BuildWelcome()
    {
        var head = new byte[] { 0xAA, 0x00, 0x13, 0x7E, 0x1B };
        var text = "CONNECTED SERVER\n"u8.ToArray();
        var all = new byte[head.Length + text.Length];
        head.CopyTo(all, 0);
        text.CopyTo(all, head.Length);
        return all;
    }

    public Session(TcpClient client, int port, CharacterStore store, World world)
    {
        _client = client;
        _stream = client.GetStream();
        _port = port;
        _store = store;
        _world = world;
        _ver = (port == 2001 || port == 2006) ? ClientVersion.V533 : ClientVersion.V495;
        _remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
    }

    public async Task RunAsync()
    {
        Log.Info($"++ CONNECT from {_remote} on port {_port} [{_ver}]");
        try
        {
            if (IsLoginPort)   // login channel: send the 0x7E welcome
            {
                await _stream.WriteAsync(Welcome);
                Log.Info($"   -> sent welcome ({Welcome.Length}B)");
            }
            else                 // game channel: the client speaks first (sends 0x10). Send NOTHING now.
            {
                // Reversing NexusTK.exe shows the game socket's receive path is identical to login's
                // (mode 0x3aa4c=5 -> recv loop 0x477fc0 -> decrypt 0x478680 -> WndProc queue). There is
                // no server greeting on the game port and no seed/ack handshake. Sending unsolicited
                // packets before the client's 0x10 only risks desyncing its frame assembler, so we wait.
                Log.Info("   == game connect: waiting for client 0x10 arrival (no pre-arrival sends) ==");
            }

            var buf = new List<byte>();
            var tmp = new byte[4096];
            while (true)
            {
                int n = await _stream.ReadAsync(tmp);
                if (n == 0) break;
                Log.Info($"   <~ RAW {n}B on :{_port}: {Log.Hex(tmp[..n])}");
                for (int i = 0; i < n; i++) buf.Add(tmp[i]);

                var arr = buf.ToArray();
                int off = 0;
                while (arr.Length - off >= 5 && arr[off] == 0xAA)
                {
                    if (!TkPacket.TryParse(arr.AsSpan(off), out var pkt, out int consumed)) break;
                    off += consumed;
                    Handle(pkt);
                }
                if (off > 0) buf.RemoveRange(0, off);
                if (buf.Count > 0)
                    Log.Info($"   (… {buf.Count}B buffered/unframed: {Log.Hex(buf.ToArray())})");
            }
        }
        catch (Exception e) { Log.Info($"!! {_remote} error: {e.Message}"); }
        finally
        {
            // Leave the shared world: despawn us for the other players on our map. World mobs persist
            // (they belong to the map, not this session), so they keep wandering for whoever remains.
            if (_enteredWorld) _world.LeaveMap(this, _char.Map);
            // Persist the last state (position/stats) only for a session that actually entered the
            // world. The login-channel session never populates _char, so saving it would clobber the
            // real record with defaults.
            if (_enteredWorld)
            {
                _store.Save(_char);
                Log.Info($"   -> persisted '{_char.Name}' at map {_char.Map} ({_char.X},{_char.Y})");
            }
            _client.Close();
            Log.Info($"-- CLOSE {_remote}");
        }
    }

    private void Handle(TkPacket pkt)
    {
        var dec = TkCrypt.Crypt(pkt.Body, pkt.Increment, TkCrypt.LoginKey);
        Log.Info($"   <- pkt op=0x{pkt.Opcode:x2} inc=0x{pkt.Increment:x2} len={pkt.Body.Length + 2} body={pkt.Body.Length}B");
        Log.Info($"        dec : {Log.Hex(dec)}");

        switch (pkt.Opcode)
        {
            case Opcode.Arrival:          HandleArrival(pkt); break;
            case Opcode.NameCheck:        NameAvailable(dec); break;
            case Opcode.CreateAppearance: HandleCreate(dec); break;
            case Opcode.Login:            HandleLogin(dec); break;
            case 0x32:                    HandleWalk(dec); break;   // client walk step -> confirm move
            // 0x11 = "side" (turn to face a direction, NO movement) for BOTH clients. The 4.95 client's
            // 0x11 recv handler (@0x450350) reads id(u32)@+1, side(u8)@+5, looks the entity up and calls
            // its turn method (@0x462410) -- exactly SendSide's layout. Previously dropped for 4.95, which
            // left facing unconfirmed until the next walk ("press a new direction, first step goes the OLD
            // way"). In NexusTK the first press in a new direction turns in place; only the second walks.
            case 0x11:
                HandleTurn(dec);
                break;
            // 0x1b = client setting toggle. body[0] = which setting (RTK settings-parse cases):
            //   0x07 = Realm center (F4)   0x09 = Fast move   ... others not yet handled.
            case 0x1b:
                HandleSetting(dec);
                break;
            // 5.33 map/walk split (confirmed via Mithia 7.x clif dispatch + live capture):
            //   0x05 = map-data request (view rect) -> stream terrain back as 0x06 (HandleMapRequest).
            //   0x06 = walk (dir @ body[0], + reported pos/viewport) -> confirm move, same as 4.95.
            // 4.95 differs: 0x05 unused, 0x06 = walk+sync. So 0x06 -> HandleWalk for BOTH versions.
            case 0x05:
                if (_ver == ClientVersion.V533) HandleMapRequest(dec);
                else Log.Info("   ?? 0x05 with no V495 handler");
                break;
            case 0x06:
                HandleWalk(dec);
                break;
            // 0x38 = hard refresh (Ctrl+R): the client grays the screen and asks the server to re-assert
            // authoritative state. RTK's clif_refresh replies with sendmapinfo + sendxy + re-drawn entities
            // (0x04 is the re-anchor primitive here — authoritative position + recentered camera). See §refresh.
            case 0x38:
                HandleRefresh(dec);
                break;
            case 0x0E:                    HandleChat(dec); break;   // client chat -> echo as over-head speech
            // 0x1d = emotion request (the ':' emote wheel). body[0] = emote index; the client plays action
            // type = index + 11 (RTK clif_parseemotion: sendaction(index+11)). Broadcast as a 0x1A action.
            case 0x1D:                    HandleEmotion(dec); break;
            case 0x13:                    HandleAttack(dec); break;  // client attack (spacebar) -> echo 0x13 anim
            case 0x2D:                    HandleProfileRequest(dec); break;  // profile key -> self-profile (0x39)
            case 0x43:                    HandleClickInfo(dec); break;       // click entity -> profile / NPC dialog
            // 0x3A = NPC dialog response (RTK clif_parsenpcdialog): the client sends this after the player
            // acts on a dialog we opened via 0x30. body[0] = kind (01 text next/close, 02 menu pick, 04 input
            // text). See HandleNpcDialog — a logging stub until the 0x30 send format is confirmed live.
            case 0x3A:                    HandleNpcDialog(dec); break;
            case 0x4F:                    HandleChangeProfile(dec); break;   // edit profile -> save pic + blurb
            // ---- items (opcode numbers from RTK 7.x recv dispatch; confirmed to align with 4.95 by the
            // walk/turn/chat/attack/setting opcodes already matching). See §11c. ----
            case 0x07:                    HandlePickup(dec); break;    // pick up the floor item under me
            case 0x08:                    HandleDropItem(dec); break;  // drop a bag slot to the floor
            case 0x17:                    HandleThrow(dec); break;     // throw a bag slot (flies ahead)
            case 0x1A:                    HandleUseItem(dec, eat: true); break;   // eat/consume a slot
            // 0x12 = the WIELD hotkey (press 'w', then the item's letter). Body = [slot(1-based), 00] — the
            // same shape as 0x1C, confirmed by live capture (wield sent `12 01 00`). Double-click already used
            // 0x1C; the hotkey just uses a different opcode, so route it to the same use/equip path.
            case 0x12:                    HandleUseItem(dec, eat: false); break;  // wield hotkey -> equip a slot
            case 0x1C:                    HandleUseItem(dec, eat: false); break;  // use/equip a slot
            case 0x1F:                    HandleUnequip(dec); break;   // remove a worn item back to the bag
            case 0x24:                    HandleDropGold(dec); break;  // drop a gold amount
            // 0x20 = the 'o' / Open key (RTK clif_parse case 0x20 "Clicked 'O'" -> clif_cancelafk + clif_open_sub
            // -> onOpen script). A deliberate action (RTK's handler clears AFK, so NOT a heartbeat): in NexusTK it
            // toggles the faced door object's open/closed graphic in place. See HandleOpen (swaps the object tile
            // via the 0x06 cell-patch and broadcasts it to the map).
            case 0x20:                    HandleOpen(dec); break;
            // 0x0F = cast a learned spell (RTK clif_parsemagic): body[0]=book slot+1, then per spell type
            // 1 -> typed answer string, type 2 -> target entity id (u32BE), type 5 -> nothing. See HandleCast.
            case 0x0F:                    HandleCast(dec); break;
            // 0x66 = right-click "examine item" request. The client sends it (and RETRIES ~6× because we don't
            // answer), expecting a 0x66 reply its handler 0x4511b0 renders as the item-detail popup. We can't
            // build that reply until its wire format is known — for now decode+log the request so labelled
            // right-clicks map body[1] -> which item. See HandleItemInfoRequest / issue #3.
            case 0x66:                    HandleItemInfoRequest(dec); break;
            default:                      Log.Info($"   ?? no handler for opcode 0x{pkt.Opcode:x2}"); break;
        }
    }

    // ---- login flow ----
    private string _pendingName = "";   // name from the availability check, fallback for creation

    // Create step 1 (0x02): the client asks whether a name is free. Body is the length-prefixed name.
    // We stash it so creation (0x04) can key the record even if that packet omits the name.
    private void NameAvailable(byte[] dec)
    {
        try
        {
            int nlen = dec.Length > 0 ? dec[0] : 0;
            if (nlen > 0 && 1 + nlen <= dec.Length)
                _pendingName = Encoding.ASCII.GetString(dec, 1, nlen);
        }
        catch { /* leave _pendingName as-is */ }

        Send(new byte[] { 0xAA, 0x00, 0x06, 0x02, 0x01, 0x4F, 0x64, 0x79, 0x6E });
        Log.Info($"   -> name available (pending='{_pendingName}')");
    }

    // Create step 2 (0x04): the client sends the chosen name + appearance (gender, etc.) after the
    // availability check. Persist it so world entry uses the player's real choices instead of the
    // hardcoded spawn. The exact byte layout is confirmed from the annotated dump below on first use;
    // we reliably read the length-prefixed name and store the raw appearance bytes alongside it.
    private void HandleCreate(byte[] dec)
    {
        Log.Info($"   -> CREATE raw({dec.Length}B): {Log.Hex(dec)}");

        string name = _pendingName;
        try
        {
            int nlen = dec.Length > 0 ? dec[0] : 0;
            if (nlen > 0 && 1 + nlen <= dec.Length)
                name = Encoding.ASCII.GetString(dec, 1, nlen);
        }
        catch { /* fall back to _pendingName */ }
        if (string.IsNullOrEmpty(name)) name = _user;

        var c = _store.Load(name) ?? new Character();
        c.Name = name;
        c.CreationBlob = dec;      // keep the raw body for future re-decoding if the mapping changes
        ApplyAppearance(c);        // decode gender/hair/face so world entry renders the real choices
        _store.Save(c);
        Log.Info($"   -> CREATE persisted '{name}' (sex={c.Sex} hair={c.Hair} face={c.Face}) -> {_store.Directory}");
        SendMessage("Account created.");
    }

    // Map the raw 0x04 creation body onto the renderable 0x33 appearance bytes.
    // Creation body layout (live captures): [0]=hairStyle [1]=hairColor [2]=face [3]=gender [4]=skin.
    //
    // IMPORTANT (learned the hard way): the 0x33 render appearance bytes are a DIFFERENT id space
    // than the creation bytes. Copying creation hair/face into 0x33 appearance[1]/[2] blanks the
    // composed sprite (character invisible though the entity still exists). The only known-good 0x33
    // appearance is [0]=<bodyForm>, [1..6]=0. So we translate ONLY gender -> bodyForm here, via a
    // whitelist of body values known to render, and leave hair/face at 0 until the render id space
    // for those layers is decoded. Unknown gender codes fall back to the safe default.
    private static void ApplyAppearance(Character c)
    {
        // Creation blob (login-channel 0x04), decoded from CONTROLLED captures — a char named "male"
        // gave 55 00 02 02 00 and one named "female" gave 12 01 02 01 00; byte[1] is the ONLY byte that
        // tracks gender across every sample (male=00, every female=01):
        //   [0]=hair(style|color nibbles)  [1]=GENDER(0=male,1=female)  [2]=face  [3]/[4]=nation/totem
        // Gender maps straight onto render body/sex (appearance[0] is also 0=male/1=female). Face maps
        // onto render face (appearance[2]). Hair has no slot in the 4.95 type-0 render form. nation/totem
        // are STATS (profile/HUD), not appearance.
        var b = c.CreationBlob;
        if (b is null || b.Length < 2) return;
        c.Sex  = b[1];   // gender: 0=male, 1=female (proven by the "male"/"female" creations)
        // Face: creation byte[0] is what varies across distinct face picks (faceone=00, facetwo=23,
        // facethree=34); byte[2] only had 2 values for 3 faces, so it was the wrong byte.
        c.Face = b[0];   // -> render appearance[2]
        c.Hair = 0;      // not renderable in this form
    }

    private void HandleLogin(byte[] dec)
    {
        int ulen = dec[0];
        _user = Encoding.ASCII.GetString(dec, 1, ulen);
        Log.Info($"   -> LOGIN accepted for user='{_user}'");

        // handoff: send the client to the game server (reversed IP octets + port). Keep the version's
        // channel together: a V533 login (port 2001) hands off to the V533 game port 2006, so the game
        // session is tagged V533 too; V495 stays on 2005.
        byte[] ip = { 127, 0, 0, 1 };
        int gport = _ver == ClientVersion.V533 ? 2006 : 2005;
        var p = new List<byte>
        {
            0xAA, 0, 0, Opcode.Login,
            ip[3], ip[2], ip[1], ip[0],
            (byte)(gport >> 8), (byte)(gport & 0xFF),
            23, 0, 9
        };
        p.AddRange(TkCrypt.LoginKey);
        var uname = Encoding.ASCII.GetBytes(_user);
        p.Add((byte)uname.Length);
        p.AddRange(uname);
        p.AddRange(new byte[] { 0, 1, 18, 17, 0 });   // handoff token echoed back by 0x10
        p[2] = (byte)(p.Count - 3);
        Send(p.ToArray());
        Log.Info($"   -> game handoff -> 127.0.0.1:{gport}");
    }

    // ---- game server ----
    private void HandleArrival(TkPacket pkt)
    {
        // plaintext body: <klen> "NexonInc." <ulen> "<user>" <token>
        var body = pkt.Body;
        try
        {
            int klen = body[0];
            int ulen = body[1 + klen];
            _user = Encoding.ASCII.GetString(body, 2 + klen, ulen);
        }
        catch { /* keep default */ }

        // Load the persisted character (created on the login channel, or saved at last logout).
        // Fall back to a fresh default spawn for an account we've never seen.
        var loaded = _store.Load(_user);
        _char = loaded ?? new Character();
        _char.Name = _user;
        ApplyAppearance(_char);   // re-derive appearance for records saved before the mapping existed
        _enteredWorld = true;
        // Assign a UNIQUE world entity id (the old default was 1 for everyone, which made every player
        // collide on the shared-world broadcast key). This id binds the client's camera (0x05/SendId) and
        // is how peers address this player's move/speech/despawn packets. It is a runtime handle, not a
        // persistent key, so we overwrite whatever was loaded and never save it back meaningfully.
        _char.Id = _world.AllocatePlayerId();
        Log.Info(loaded is null
            ? $"   -> ARRIVAL user='{_user}' — no saved character, using default spawn (entity id {_char.Id})"
            : $"   -> ARRIVAL user='{_user}' — loaded saved character at map {_char.Map} ({_char.X},{_char.Y}) (entity id {_char.Id})");

        // *** THE MISSING TRIGGER (found by reversing NexusTK.exe) ***
        // After 0x10 the client is on the loading screen; its game-WORLD object doesn't exist yet,
        // so the world dispatcher (handles opcodes 0x03-0x68) never runs and every world packet is
        // dropped. Handler 0x444de0 shows the client only builds that world object when it receives
        // opcode 0x02 whose first payload byte is 0x00. The 6.x/7.x reference servers never send this,
        // which is why every prior attempt sat silent. Send it FIRST.
        SendMap(0x02, _gameInc++, new byte[] { 0x00 }, "ENTER-WORLD (0x02.00)");

        // Now the world object exists. Replicate the PROVEN 6.x entry order (Replay6x): the map
        // alone loads (confirmed by Frida: CreateFileW("Maps\TK32.map") ok) but the client stays
        // black and won't move because it was never told its OWN entity id. 0x05 supplies that.
        //   0x1E ack, 0x20 time  -> handshake acks (harmless, part of the working sequence)
        //   0x05 = YOUR entity id (binds camera/input to the self player)  <-- the missing piece
        //   0x15 = enter-map (loads Maps\TK<mapId>.map), 0x04 = coords, 0x33 = our appearance
        SendMap(0x1E, _gameInc++, new byte[] { 0x06, 0x00, 0x00 }, "ack(0x1E)");
        SendMap(0x20, _gameInc++, new byte[] { 0x10, 0x32 }, "time(0x20)");
        SendId();
        SendMapInfo(_char.Map, _char.MapXs, _char.MapYs, "Nexus", 232, _gameInc++);
        Log.Info("   -> mapinfo(0x15)");
        SendXy();
        SendSelfLook();
        SendStats();

        Log.Info("   == entry sent: 0x02 trigger + 0x1E/0x20 acks + 0x05 id + 0x15 map + 0x04 xy + 0x33 self + 0x08 stats ==");

        // Join the shared world: register on this map, draw everyone/everything already here for us, and
        // let EnterMap broadcast US to them. From now on peers see our moves/speech and we see theirs.
        var (peers, mobs) = _world.EnterMap(this, _char.Map);
        foreach (var p in peers) ShowPlayer(p);   // existing players -> draw on our client (0x33)
        SyncMobs(mobs);                            // shared mobs in view -> draw on our client (0x07, streamed)
        foreach (var gi in _world.ItemsOn(_char.Map)) ShowGroundItem(gi);  // floor items (0x16)
        RefreshInventory();                       // fill the bag + equipment windows (0x0F / 0x37)
        RefreshSpells();                          // fill the spell/skill book (0x17) with learned spells
        Log.Info($"   == world join: map {_char.Map} has {peers.Length} other player(s), {mobs.Length} mob(s) ==");
    }

    // ---- 5.33 terrain streaming (opcode 0x06) ----
    // The 5.33 client draws NO terrain from local files; after 0x15 map-info it asks the server for the
    // tiles in its viewport by sending a view-rect request (opcode 0x05 = initial full pull, 0x06 =
    // incremental refresh). We reply with an opcode-0x06 cell block. Layout confirmed from BOTH the 5.33
    // binary (handler sub_469060) and the Mithia 7.x reference (clif_parsemap / clif_sendmapdata):
    //   request  body : x0(BE u16) y0(BE u16) w(u8) h(u8) checksum(BE u16)
    //   response body : 00 | x0(BE u16) | y0(BE u16) | w(u8) | h(u8) | { tile(BE) pass(BE) obj(BE) } * w*h
    // cell shorts are BIG-ENDIAN; the client draws each cell at (x0+ix, y0+iy). Without this the map is
    // an empty black void — which is exactly what we saw before implementing it.
    private void HandleMapRequest(byte[] dec)
    {
        if (dec.Length < 6) { Log.Info($"   ?? map-req too short ({dec.Length}B)"); return; }
        int x0 = (dec[0] << 8) | dec[1];
        int y0 = (dec[2] << 8) | dec[3];
        int reqW = dec[4];
        int reqH = dec[5];

        var map = MapData.For(_char.Map, _char.MapXs, _char.MapYs);
        if (map is null) { Log.Info($"   ?? map-req for map {_char.Map}: no server-side tile data"); return; }

        // Clamp the rect to the map so the header w/h EXACTLY match the emitted cell count — the client
        // reads w*h cells sequentially, so a mismatch desyncs its stream.
        if (x0 < 0) x0 = 0;
        if (y0 < 0) y0 = 0;
        int w = Math.Clamp(reqW, 0, Math.Max(0, map.Xs - x0));
        int h = Math.Clamp(reqH, 0, Math.Max(0, map.Ys - y0));

        // NO leading flag byte: the 5.33 client (handler sub_469060) reads x0 immediately after op+inc
        // — calibrated via the 0x15 handler and confirmed by the client-side Frida probe (a spurious
        // leading 0x00 shifted every field by one byte, making w read as 0 -> zero cells -> black void).
        // (Mithia 7.x's clif_sendmapdata DOES emit a leading 0 here; 5.33 differs.)
        var b = new List<byte>();
        b.AddRange(Be((ushort)x0));
        b.AddRange(Be((ushort)y0));
        b.Add((byte)w);
        b.Add((byte)h);

        int total = w * h;
        bool solid = MapDiag.StartsWith("solid:");
        int solidN = solid && int.TryParse(MapDiag.AsSpan(6), out var sn) ? sn : 0;
        // passtest:N -> floor 651 everywhere (all visible), but stamp pass=N onto a vertical wall-line
        // every 5 tiles (mx % 5 == 2). Same tile graphic on wall/non-wall cells, so any movement block is
        // purely collision from the `pass` short and any visual change is purely `pass` affecting render.
        // The exact same pattern is enforced by PassAt() in HandleWalk, so seen wall == blocked wall.
        bool passtest = MapDiag.StartsWith("passtest:");
        int ci = 0;
        for (int iy = 0; iy < h; iy++)
        for (int ix = 0; ix < w; ix++, ci++)
        {
            int mx = x0 + ix, my = y0 + iy;
            ushort tile, pass, obj;
            if (MapDiag == "sweep")
            {
                tile = (ushort)((long)ci * 28550 / Math.Max(1, total));   // 0..28550 across the rect, unmasked
                pass = 0; obj = 0;
            }
            else if (solid)
            {
                tile = (ushort)solidN; pass = 0; obj = 0;
            }
            else if (passtest)
            {
                // Floor 651 everywhere; put a VISIBLE object marker on the wall columns so the tester can
                // see the walls they're blocked by. Objects render but don't collide (only `pass` does),
                // so the marker is purely visual and the block is purely `pass` — the two stay separable.
                bool wall = (mx % 5 == 2);
                tile = 651;
                obj  = (ushort)(wall ? 1542 : 0);
                pass = PassAt(map, mx, my);   // pass=N on the same wall columns (collision)
            }
            else
            {
                tile = (ushort)((map.Tile(mx, my) + CellOff) & 0x3FFF);
                pass = PassAt(map, mx, my);
                obj  = (ushort)((map.Obj(mx, my) + CellOff) & 0x3FFF);
            }
            b.AddRange(Be(tile));
            b.AddRange(Be(pass));
            b.AddRange(Be(obj));
        }

        string mode = MapDiag.Length == 0 ? $"real tileOff={CellOff}" : $"DIAG={MapDiag}";
        Log.Info($"   -> map-data(0x06) rect ({x0},{y0}) req {reqW}x{reqH} -> {w}x{h} cells={total} [{mode}]");
        Send(MapBuild(0x06, _gameInc++, b.ToArray()));
    }

    private Character _char = new();
    private byte[] _encTable = Array.Empty<byte>();
    private byte _gameInc = 0;   // per-packet increment for game-channel sends


    // Live creatures the player can fight. Server-authoritative HP; the client only draws them.
    // Populated by the mob commands (!mob/!mobrow/!spawn); entries are removed on death (0x0E).
    private readonly List<Mob> _mobs = new();
    private uint _nextMobId = 5000;      // entity-id pool for spawned creatures (well above the self id)
    private byte _facing = 0;            // last direction the player faced (0=N 1=E 2=S 3=W); drives melee
    private byte _realm = RealmCenter;   // realm-center camera lock; toggled live by F4 (0x1b sub-cmd 0x07)
    private int _lockOx, _lockOy;        // camera origin frozen when realm-center turned ON (map top-left tile)
    // Fast-move = the client's movement model (RTK clif_parsewalk gates on FLAG_FASTMOVE):
    //   ON  = client-authoritative: the client moves/animates freely and is only corrected on desync.
    //         The server must send the walker NOTHING on a good step (0x26 self-walk is skipped).
    //   OFF = server-authoritative: the client will NOT move until the server assigns the tile, so every
    //         step must be answered with a position/move packet.
    // The client toggles it locally and notifies us via 0x1b sub-cmd 0x09 (it does NOT report its state on
    // connect). The client PERSISTS fast-move across launches, and the working/smooth setup is fast-move
    // ON (client-authoritative), so we default ON to match a client that already has it enabled. Each
    // 0x1b/09 notification flips it to stay in sync. (If a fresh client actually boots OFF, one toggle
    // re-syncs; NEXUS_V495_FASTMOVE_DEFAULT can override the assumed startup state.)
    private bool _fastMove = FastMoveDefault;

    private static readonly bool FastMoveDefault =
        Environment.GetEnvironmentVariable("NEXUS_V495_FASTMOVE_DEFAULT") == "0" ? false : true;

    // Serializes socket writes so the World's mob-AI thread and peer broadcasts can't interleave bytes
    // with this session's own read-loop on the shared stream.
    private readonly object _sendLock = new();

    // Viewport-streamed world mobs: the set of shared-mob ids currently drawn on THIS client. The client's
    // 0x07 spawn silently drops entities outside the camera rect, so a 400-mob map can't be blanket-sent —
    // instead SyncMobs spawns mobs as they enter view and despawns them as they leave, keeping the client to
    // a screenful. Guarded by _viewLock (touched by both this read-loop and the World tick thread). Lock
    // order is always _viewLock -> _sendLock (never the reverse) so the two can't deadlock.
    private readonly HashSet<uint> _shownMobs = new();
    private readonly object _viewLock = new();
    // The pads MUST hug the real 17x15 viewport. The client's 0x07 spawn is viewport-gated: a spawn sent
    // for an OFF-screen tile is silently dropped, so spawning ahead of the edge (ShowPad>0) would mark a
    // mob "shown" that the client never created — it then never appears. Likewise the client culls entities
    // that move off-screen, so keeping a mob "shown" past the edge (HidePad>0) leaves a dead zone where we
    // think it's drawn but it's gone, and we never re-send it. So: show/despawn EXACTLY at the screen edge.
    private const int ShowPad = 0;
    private const int HidePad = 0;

    /// <summary>
    /// Best-effort world-entry burst, extrapolated from the RTK 6.x/7.x reference sequence
    /// (intif.c char-load callback). ROUGH: formats/increments/flags are first-pass guesses —
    /// each packet is logged so we can see exactly where the client stops or reacts.
    /// </summary>
    private void SendWorldEntry()
    {
        Log.Info($"   == WORLD ENTRY burst for '{_char.Name}' (map={_char.Map} @ {_char.X},{_char.Y}) ==");

        SendMap(0x1E, 0, new byte[] { 0x06, 0x00 }, "ack(0x1E)");
        SendMap(0x20, 3, new byte[] { 0x00, 0x00 }, "time(0x20)");
        SendId();
        SendMapInfo(_char.Map, _char.MapXs, _char.MapYs, "Nexus", 232);
        Log.Info("   -> mapinfo(0x15)");
        SendStats();
        SendSelfLook();
        SendXy();
        SendMap(0x22, 3, Array.Empty<byte>(), "map-done(0x22)");
        PlayMapMusic(_char.Map);   // 0x19: start this map's background track

        Log.Info("   == burst sent; watching for client packets (walk/request = progress, disconnect = a packet was rejected) ==");
    }

    // 0x05 = the client's OWN entity id, decoded from the working 6.x capture:
    //   05 | entityId(u32BE) | 00 00 00 02 | 00 00 00 00
    private void SendId()
    {
        var d = new List<byte>();
        d.AddRange(Be32(_char.Id));   // your entity id
        d.AddRange(Be32(2));          // field2 = 2 (per 6.x)
        d.AddRange(Be32(0));          // field3 = 0
        SendMap(0x05, _gameInc++, d.ToArray(), "id(0x05) — YOUR entity id");
    }

    // 0x08 self-stats -> the always-on HUD. Opcode + full byte layout decoded empirically (2026-07-24):
    // a real 6.x server capture (jeedee/TkServer) proved stats = 0x08; a self-describing gradient packet
    // (!stg, body[i]=i) then pinned every 4.95 field offset by reading the value off the HUD. flags=0x78
    // selects the full-stats form. Multi-byte stat fields are big-endian u32 (verified: HP=0x18191A1B at
    // offset 24, Exp=0x20212223 at 32, etc.). maxHP[5]/maxMP[9] CONFIRMED via !hp (sending 100/1000
    // drops the bar to ~10%). Nation id table (CONFIRMED via !nat, see Character.NationName).
    //   [0]=flags(0x78) [1]=nation [2]=totem [4]=level [5..8]=maxHP u32BE [9..12]=maxMP u32BE
    //   [13]=might [14]=will [17]=grace [24..27]=HP u32BE [28..31]=MP u32BE [32..35]=exp u32BE
    //   [36..39]=coins u32BE
    private void SendStats()
    {
        // Effective = base (_char.*) + worn-gear bonuses + active timed buffs. Clamp current HP/MP to the
        // effective caps so an unequip that lowers max Vita/Mana can't leave the bar reading over 100%.
        var eq = Totals();
        uint maxHp = (uint)Math.Max(1, (int)_char.MaxHp + eq.hp);
        uint maxMp = (uint)Math.Max(0, (int)_char.MaxMp + eq.mp);
        if (_char.Hp > maxHp) _char.Hp = maxHp;
        if (_char.Mp > maxMp) _char.Mp = maxMp;

        var d = new byte[58];
        d[0] = 0x78;                        // flags: full-stats form
        d[1] = _char.Nation;
        d[2] = _char.Totem;
        d[4] = _char.Level;
        WriteBe32(d, 5, maxHp);             // maxHP  (offset [5] confirmed via !hp bar-fill test) — base + gear
        WriteBe32(d, 9, maxMp);             // maxMP  (offset [9] confirmed) — base + gear
        d[13] = (byte)Math.Clamp(_char.Might + eq.might, 0, 255);
        d[14] = (byte)Math.Clamp(_char.Will  + eq.will,  0, 255);
        d[17] = (byte)Math.Clamp(_char.Grace + eq.grace, 0, 255);
        WriteBe32(d, 24, _char.Hp);         // current HP (confirmed)
        WriteBe32(d, 28, _char.Mp);         // current MP (confirmed)
        WriteBe32(d, 32, _char.Exp);        // experience (confirmed)
        WriteBe32(d, 36, _char.Coins);      // coins      (confirmed)
        SendMap(0x08, _gameInc++, d, "stats(0x08)");
    }

    private static void WriteBe32(byte[] d, int off, uint v)
    {
        d[off]     = (byte)(v >> 24);
        d[off + 1] = (byte)(v >> 16);
        d[off + 2] = (byte)(v >> 8);
        d[off + 3] = (byte)v;
    }

    // ---- natural HP/MP regeneration (RTK Player.regen migration) -------------------------------
    // RTK heals a resting player every 25s: Accepted/player.lua `Player.regen` fires on timerTick%50
    // (the timer runs at 0.5s/tick), restoring ceil(maxHP * 0.02 * (1 + healing/100)) vita and
    // ceil(maxMP * 0.02) mana, then pushing a status update. We don't carry RTK's derived `healing`
    // stat, so HP regen scales with Grace and MP regen with Will (so vitals come back "based on your
    // stats", as expected), keeping RTK's 2% base and 25s cadence. The world heartbeat (World.Tick)
    // calls this once per 600ms tick for every player; we accumulate real elapsed ms so the 25s
    // cadence is independent of the tick period.
    //
    // Threading: runs on the world-tick thread and writes _char.Hp/Mp, which the session's own
    // read-loop also writes (damage/heal). Both are plain field writes with no lock — consistent with
    // the codebase's lock-free _char posture (see PlayerSnapshot) — so a regen tick landing in the
    // same instant as a hit could at worst drop one small increment. The 25s cadence makes that
    // vanishingly rare and self-correcting on the next tick.
    private long _regenAccum;
    private const int RegenIntervalMs = 25_000;   // RTK regen period (timerTick%50 @ 0.5s/tick)

    public void RegenTick(int ms)
    {
        if (_char.Hp == 0) return;   // dead: no natural regen (RTK bails on health==0 / state==1)

        var eq = Totals();
        uint maxHp = (uint)Math.Max(1, (int)_char.MaxHp + eq.hp);
        uint maxMp = (uint)Math.Max(0, (int)_char.MaxMp + eq.mp);
        if (_char.Hp >= maxHp && _char.Mp >= maxMp) { _regenAccum = 0; return; }   // already topped off

        _regenAccum += ms;
        if (_regenAccum < RegenIntervalMs) return;
        _regenAccum -= RegenIntervalMs;

        // 2% of max per tick, scaled by the governing attribute (Grace->vita, Will->mana). Ceil keeps a
        // low-level character (small max) ticking up by at least 1 instead of rounding to nothing.
        int hpGain = (int)Math.Ceiling(maxHp * 0.02 * (1 + (_char.Grace + eq.grace) / 100.0));
        int mpGain = (int)Math.Ceiling(maxMp * 0.02 * (1 + (_char.Will  + eq.will)  / 100.0));

        uint newHp = Math.Min(maxHp, _char.Hp + (uint)hpGain);
        uint newMp = Math.Min(maxMp, _char.Mp + (uint)mpGain);
        if (newHp == _char.Hp && newMp == _char.Mp) return;   // no change -> skip the HUD packet

        _char.Hp = newHp;
        _char.Mp = newMp;
        SendStats();   // push the refreshed HP/MP to the always-on HUD
    }

    // "!nat <n>" — send stats with nation byte = n so we can read which kingdom name/crest the HUD shows.
    // Nation names live in a client data file (no strings in the exe; NATION_E.EPF is a graphic set), so
    // the id -> nation mapping can only be built empirically. Sweep 0,1,2,... and record each.
    private void StatNation(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        byte n = 0;
        if (parts.Length > 1) byte.TryParse(parts[1], out n);
        byte save = _char.Nation;
        _char.Nation = n;
        SendStats();
        _char.Nation = save;
        Log.Info($"   -> NATION probe: sent nation={n}; read the HUD nation name/crest");
    }

    // "!hp <cur> <max>" — send stats with HP=cur, maxHP=max (and the same for MP) to PIN the maxHP/maxMP
    // offsets: if [5]/[9] are really maxHP/maxMP, the HP/MP bar fill becomes cur/max (e.g. 100/1000 = 10%
    // full) and any "cur/max" text shows those numbers. If the bar stays full, the offset is wrong.
    private void StatHpTest(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        uint cur = 100, max = 1000;
        if (parts.Length > 1) uint.TryParse(parts[1], out cur);
        if (parts.Length > 2) uint.TryParse(parts[2], out max);
        var (sh, sm, smh, smm) = (_char.Hp, _char.Mp, _char.MaxHp, _char.MaxMp);
        _char.Hp = cur; _char.MaxHp = max; _char.Mp = cur; _char.MaxMp = max;
        SendStats();
        (_char.Hp, _char.Mp, _char.MaxHp, _char.MaxMp) = (sh, sm, smh, smm);
        Log.Info($"   -> HP/MAX probe: sent HP={cur}/max={max}; expect bar fill = {cur}/{max} and text '{cur}/{max}' if offsets [5]/[9] are correct");
    }

    /// <summary>
    /// 0x33 self/character appearance. Format decoded from handler 0x44fef0:
    ///   X(u16BE) Y(u16BE) dir(u8) entityId(u32BE) type(u8=0)
    ///   [7 appearance bytes] flag(u8) nameLen(u8) name[]
    /// type=0 selects the 7-byte appearance form (reader 0x436120). type must be 0 or 1 or the
    /// client bails. The 7 bytes are body-form/hair/face/colors/gear; exact semantics TBD, using
    /// plausible values so the sprite is visible.
    /// </summary>
    private void SendSelfLook()
    {
        // 4.95 type-0 appearance layout (decoded via the look-lab sweeps):
        //   [0]=body/sex(0=M,1=F)  [1]=form/state(0=normal; 1 ghost, 3 mount, 5 invisible-spell)
        //   [2]=face  [3]=armor/coat  [4]=? (no visible change 0..8)  [5]=weapon  [6]=shield
        // NOTE the old code put "Hair" in [1] = the FORM byte — that's what blanked the character.
        //   ... [5]=weapon (Honor sword/Flame blade/…), [6]=shield
        var app = new byte[] { (byte)_char.Sex, MountForm(), (byte)_char.Face, (byte)_char.Armor, 0, WeaponLook(), ShieldLook() };
        SendLook(_char.Id, _char.X, _char.Y, dir: _facing, app, renderKind: 1, _char.Name, "self(0x33)");
    }

    // Weapon/shield look bytes for the 0x33 appearance. CRITICAL: look 0 is a REAL weapon/shield sprite,
    // so an EMPTY slot must send 0xFF ("-1", proven live and matching RTK clif.c, which sends 0xFFFF for
    // weapon/shield when !pc_isequip). The slot is "occupied" iff a matching item is actually worn — a worn
    // weapon whose Look happens to be 0 (e.g. Novice sword) still shows sprite 0, only a bare slot is 0xFF.
    // Form/state byte for appearance[1]: 3 = mounted (horse+rider composite), 0 = normal human. Other
    // documented values (1 ghost, 5 invisible-spell) aren't driven from here.
    private byte MountForm() => _char.Mounted ? (byte)3 : (byte)0;

    private byte WeaponLook() => EquippedLook(3, _char.Weapon != 0 ? _char.Weapon : (byte)0xFF);  // Type 3 = weapon; !weapon GM override
    private byte ShieldLook() => EquippedLook(5, 0xFF);                                            // Type 5 = shield
    private byte EquippedLook(int itmType, byte none)
    {
        var e = _char.Equipment.FirstOrDefault(w => Content.ItemById(w.ItemId)?.Type == itmType);
        return e is null ? none : (byte)(Content.ItemById(e.ItemId)?.Look ?? 0);
    }

    // The swing sfx of the weapon currently in hand (RTK ItmSound). 0 if unarmed or the weapon has no sound.
    private int EquippedWeaponSound()
    {
        var e = _char.Equipment.FirstOrDefault(w => Content.ItemById(w.ItemId)?.Type == 3);
        return e is null ? 0 : (Content.ItemById(e.ItemId)?.Sound ?? 0);
    }

    // General 0x33 "create/look" for ANY entity (self or a test dummy). Type=0 = the 7-byte player
    // appearance form (parser 0x436120). renderKind (byte after the 7 appearance bytes) MUST be 1/2/3
    // or handler 0x44fef0 bails before allocating the sprite (1 = player sprite). appearance[0] is the
    // body form; [1]/[2] are the sprite layers whose valid id space we're mapping with the look-lab.
    private void SendLook(uint id, ushort x, ushort y, byte dir, byte[] app, byte renderKind,
                          string name, string label)
    {
        var nm = Encoding.ASCII.GetBytes(name);
        var d = new List<byte>();
        d.AddRange(Be(x));
        d.AddRange(Be(y));
        d.Add(dir);
        d.AddRange(Be32(id));
        d.Add(0);                                   // type = 0 (7-byte appearance form)
        for (int i = 0; i < 7; i++) d.Add(i < app.Length ? app[i] : (byte)0);
        d.Add(renderKind);
        d.Add((byte)nm.Length);
        d.AddRange(nm);
        SendMap(0x33, _gameInc++, d.ToArray(), label);
    }

    // 0x16 CREATURE spawn (handler 0x450a00 -> builder 0x44dbc0 -> ctor 0x463020 -> base 0x462ec0).
    // Unlike 0x33 (which ALWAYS draws from the player sprite archive 0x4f2a84, so it can only render
    // players/NPCs that look human), 0x16 has its OWN graphic — this is the real monster/creature path.
    // Decoded field layout (offsets from the opcode byte; multi-byte = big-endian):
    //   +1  u32 owner/parent id (unused by the ctor; send 0)
    //   +5  u16 GRAPHIC id  -> stored at sprite+0x130 by 0x462ec0  (THE creature sprite)
    //   +7  u32 entity id   -> find/despawn key (must match what we track + pass to 0x0E)
    //   +0xb u16 X   +0xd u16 Y            (resting tile -> stored at entity+0x10c/+0x110)
    //   +0xf u16 X'  +0x11 u16 Y'          (the "walked-from" tile)
    //   +0x13 u32 flags (color/hp-bar?; send 0)   +0x17 u8 dir
    // There is NO name field and NO viewport gate (0x44dbc0 skips the 0x424310 check), so a creature
    // can be placed anywhere. The graphic id-space is unknown — swept live via !mobrow.
    //
    // *** CRITICAL: (X',Y') MUST differ from (X,Y). *** The ctor 0x463020 computes the walk distance
    // `[obj+0x148] = |X-X'| + |Y-Y'|`, and the per-frame screen-position code (0x463160) does
    // `idiv [obj+0x148]`. A stationary spawn with (X',Y')==(X,Y) → distance 0 → **integer
    // divide-by-zero → client crash** (found live via the Frida crash-catcher). So a creature always
    // "walks in" one tile: we send the from-tile one step north (or south at the top edge), distance 1.
    private void SendCreature(uint id, ushort sprite, ushort x, ushort y, byte dir, string label)
    {
        ushort fromX = x;
        ushort fromY = (ushort)(y > 0 ? y - 1 : y + 1);   // 1 tile away so the walk distance != 0
        var d = new List<byte>();
        d.AddRange(Be32(0));         // +1  owner/parent id
        d.AddRange(Be(sprite));      // +5  graphic id
        d.AddRange(Be32(id));        // +7  entity id
        d.AddRange(Be(x));           // +0xb resting X
        d.AddRange(Be(y));           // +0xd resting Y
        d.AddRange(Be(fromX));       // +0xf walked-from X
        d.AddRange(Be(fromY));       // +0x11 walked-from Y
        d.AddRange(Be32(0));         // +0x13 flags
        d.Add(dir);                  // +0x17 dir
        SendMap(0x16, _gameInc++, d.ToArray(), label);
    }

    // *** 0x07 = the REAL creature/monster spawn (the "area characters" list). *** Handler 0x44fdb0
    // loops a u16 count of entities, each built by the SAME entity factory 0x44d7d0 that 0x33 uses —
    // BUT here the look descriptor's `look` field selects a DIRECT sprite instead of the 7-byte player
    // appearance. RE'd path: 0x44fdb0 -> 0x44d7d0 -> (look in 0x8000..0xbfff => descriptor type 1) ->
    // 0x44d8c8 -> ctor 0x461a50 (entity vtable 0x4cd098). That entity's draw 0x461c70 branches on
    //   [ent+0x178] (=type): type!=0 -> 0x461d37 -> monster resolver 0x434020/0x4342e0 which pushes
    //   "MONSTER.EPF" (0x4f1d18) and resolves the sprite from Monster.epf via 0x433d00 (Monster.tbl).
    // So look = 0x8000 + monsterLookId draws a real monster. (look < 0x8000 or > 0xbfff => descriptor
    // type 2 -> 0x462ec0, vtable 0x4cd118 = the item/object base, i.e. the invisible 0x16 path.)
    //
    // Per-entry layout (12 bytes; body[0..1] = count, entries follow; multi-byte = big-endian):
    //   +0 X(u16)  +2 Y(u16)  +4 id(u32)  +8 look(u16=0x8000|monsterLookId)  +10 color(u8)  +11 dir(u8)
    // color -> palette (ent+0x18e via resolver), dir/state -> ent+0x18d. Unlike 0x16 there IS a viewport
    // gate (0x424310): entries outside the camera rect are silently skipped, so spawn inside view.
    private void SendCreatureList(IReadOnlyList<(uint id, ushort look, ushort x, ushort y, byte color, byte dir)> es)
    {
        if (es.Count == 0) return;
        var d = new List<byte>();
        d.AddRange(Be((ushort)es.Count));           // body[0..1] = entity count
        foreach (var e in es)
        {
            d.AddRange(Be(e.x));                    // +0  X
            d.AddRange(Be(e.y));                    // +2  Y
            d.AddRange(Be32(e.id));                 // +4  entity id
            d.AddRange(Be(e.look));                 // +8  look (0x8000|monsterId => Monster.epf)
            d.Add(e.color);                         // +10 palette/color
            d.Add(e.dir);                           // +11 dir/state
        }
        SendMap(0x07, _gameInc++, d.ToArray(), $"creature-list(0x07) x{es.Count}");
    }

    // Register a monster server-side AND draw it via the real Monster.epf path (0x07). lookId is the
    // Monster.tbl look index (0..~326); we OR in 0x8000 to hit the direct-monster-sprite branch.
    private Mob SpawnMonster(ushort lookId, ushort x, ushort y, string name, int hp, byte dir = 2, byte color = 0)
    {
        var mob = new Mob(_nextMobId++, lookId, x, y, name, hp) { Dir = dir };
        _mobs.Add(mob);
        SendCreatureList(new[] { (mob.Id, (ushort)(0x8000 | lookId), x, y, color, dir) });
        Log.Info($"   -> spawn MONSTER {mob.Id} '{name}' look={lookId} @({x},{y}) hp={hp}");
        return mob;
    }

    // 0x0E despawn (server->client; handler 0x450440): count(u8) then that many entity ids (u32BE).
    // The client destroys each by id (0x44d9f0) and stops early on a 0 id, so never pass id 0.
    private void SendDespawn(params uint[] ids)
    {
        if (ids.Length == 0) return;
        var d = new List<byte> { (byte)Math.Min(ids.Length, 255) };
        foreach (var id in ids) d.AddRange(Be32(id));
        SendMap(0x0E, _gameInc++, d.ToArray(), $"despawn(0x0E) x{ids.Length}");
    }

    // NexusTK has NO floating combat numbers — combat feedback is the over-head HP bar (0x13, below) plus the
    // effect/spell animations (0x29, SendEffect). The old melee path used to abuse 0x29 with a raw damage value
    // (which played effect #(dmg-1), an unintended graphic); that is gone — hits now send the 0x13 damage packet.
    //
    // 0x13 (server->client) = the combat DAMAGE packet: over-head HP bar + a hit-reaction animation +
    // an optional hit sound, all in ONE packet. Decoded from the 4.95 client handler 0x4508f0 (disx):
    //   body = id(u32BE) | critical(u8) | percent(u8) | hitSound(u8)
    //   * critical -> plays overlay animation (0x8f - critical, SIGNED) over the entity (a hit spark; the
    //     hit-effect is queued for 0x78 ticks via 0x4622d0->0x41b5d0). RTK uses 33 (normal) / 255 (crit).
    //   * percent  -> the over-head HP bar fill, 0..100 (the client skips the bar if percent > 100; 0 = empty).
    //   * hitSound -> played through the sound manager if nonzero (RTK's u32 damage tail lands here as its high
    //     byte = 0, so no hit sound normally; the 4.95 client ignores everything past body[7]).
    // This is what draws the "remaining HP bar above a monster's head" on every hit. RTK's clif_send_mob_health
    // builds the same shape (plus the ignored u32 damage). critical is calibratable live via NEXUS_HIT_CRIT.
    private static readonly byte HitCritByte =
        byte.TryParse(Environment.GetEnvironmentVariable("NEXUS_HIT_CRIT"), out var c) ? c : (byte)0x21; // 33 = RTK normal hit
    private void SendDamage(uint id, byte percent, byte critical, byte hitSound = 0)
    {
        if (percent > 100) percent = 100;                 // >100 would make the client skip the bar entirely
        var d = new List<byte>();
        d.AddRange(Be32(id));        // body[1..4] entity id (u32BE)
        d.Add(critical);            // body[5] hit type -> overlay anim 0x8f-critical
        d.Add(percent);             // body[6] HP bar fill 0..100
        d.Add(hitSound);            // body[7] optional hit sfx (0 = none)
        SendMap(0x13, _gameInc++, d.ToArray(), $"damage(0x13) id={id} pct={percent} crit={critical}");
    }
    public void DamageOver(uint id, byte percent, byte critical, byte hitSound = 0) => SendDamage(id, percent, critical, hitSound);  // peer-facing

    // Remaining-HP percent for the over-head bar (1..100 for a live mob so its bar never reads empty; caller
    // passes 0 explicitly on death). Guards against MaxHp<=0 and negative Hp (a killing blow overshoots).
    private static byte HpPercent(Mob m)
    {
        int max = Math.Max(1, m.MaxHp);
        int cur = Math.Clamp(m.Hp, 0, max);
        if (cur <= 0) return 0;
        int pct = (int)((long)cur * 100 / max);
        return (byte)Math.Clamp(pct, 1, 100);
    }

    // Death beat: on a killing blow, empty the target's HP bar (0x13 percent=0, which also plays the final hit
    // overlay), then remove the corpse (0x0E) after a short delay so it doesn't just pop out of existence. 4.95
    // monsters have no death frame-set (monsfrm.tbl defines only walk/attack states), so the "death animation" is
    // this beat: last hit spark + empty bar, held briefly, then despawn. Delay is calibratable via NEXUS_DEATH_DELAY_MS.
    private static readonly int DeathDespawnMs =
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_DEATH_DELAY_MS"), out var v) ? Math.Clamp(v, 0, 5000) : 600;

    // Show the result of a hit that already resolved in the world: draw the over-head HP bar for everyone on the
    // map, and on death run the death beat (empty bar + delayed despawn). `mob` is read for its remaining HP%.
    private void ShowDamageResult(uint mobId, Mob mob, bool died)
    {
        byte pct = died ? (byte)0 : HpPercent(mob);
        _world.Broadcast(_char.Map, p => p.DamageOver(mobId, pct, HitCritByte));
        if (died) ScheduleDespawn(_char.Map, mobId, DeathDespawnMs);
    }

    // Broadcast a corpse despawn (0x0E) to the map after `ms`, so the death beat is visible first. The mob is
    // already gone from the world's mob list (World.TryDamage removed it), so nothing ticks it in the meantime,
    // and mob ids are monotonic so the id can't be reused before this fires. 0 ms = despawn immediately.
    private void ScheduleDespawn(ushort map, uint id, int ms)
    {
        if (ms <= 0) { _world.Broadcast(map, p => p.DespawnEntity(id)); return; }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(ms); _world.Broadcast(map, p => p.DespawnEntity(id)); }
            catch (Exception ex) { Log.Info($"   -x delayed despawn {id} failed: {ex.Message}"); }
        });
    }

    // Play effect `effectId` over an entity via 0x29 — the real 4.95 spell-animation path. The wire byte maps
    // DIRECTLY to RTK's sendAnimation id (EfxWireOffset = 0), proven live: casting Ion (pcalign 0 → anim 4 =
    // unaligned zap) with a +1 offset showed the anim-5 graphic (unaligned heal) — i.e. wire N draws the anim-N
    // effect, so no adjustment. (The handler's internal index-1 is cancelled by the effect table being loaded
    // 1-based.) Overridable via NEXUS_EFX_WIRE_OFFSET. A/B/C = 0 → centered, default style. effectId < 0 = no-op.
    private static readonly int EfxWireOffset =
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_EFX_WIRE_OFFSET"), out var w) ? w : 0;
    private void SendEffect(uint id, int effectId)
    {
        if (effectId < 0) return;
        int b = effectId + EfxWireOffset;
        if (b < 1 || b > 255) return;   // outside the u8 / effect-table range — skip rather than send garbage
        var d = new List<byte>();
        d.AddRange(Be32(id));
        d.Add((byte)b);
        d.AddRange(Be(0)); d.AddRange(Be(0)); d.AddRange(Be(0));   // A/B/C = centered, default style
        SendMap(0x29, _gameInc++, d.ToArray(), $"effect(0x29) id={id} efx={effectId}");
    }

    // One-shot sound effect via 0x19. RTK's clif_playsound (type 3, a positional descriptor) is a LATER-client
    // format that the 4.95 client's TLV parser mis-walks into garbage (verified live: it built a sound object with
    // mode 4 / random soundId and stayed silent). This is the reverse-engineered 4.95 layout instead.
    //
    // The 0x19 handler (0x450ad0): body[1]=type. type 0 = sfx -> reads soundId(u16BE)@body[3..4], volume(u8)@body[5],
    // then runs a TLV tail (0x450c48) starting at body[2 (=P0)]+3. The tail's "C" field becomes the sound object's
    // MODE (ctor 0x463950 -> [obj+0x148]); the play wrapper (0x463ab0) plays only for mode 1 (-> 0x463ae8 ->
    // play fn 0x4798c0(soundId, type, gain, 0)). soundId/type/gain come from body[3..5] via the type-group. So we
    // put a minimal TLV *after* the 5-byte header (P0=3) that yields tagA=3, B0=0, C=1 with all skip bytes 0:
    //
    //   19 | 00(type=sfx) | 03(P0) | soundId u16BE | 64(vol=100 -> 0 dB) | 03(tagA) 00(B0) 01(C=mode1) 00 00 00 …
    //
    // volume 100 -> gain 0 (full): the client does log10(vol*0.01)*2000 dB (const @0x4cc408 = 0.0, so >0 is audible).
    // Sound id space is NexusTK.snd (zap 56, heal 4, fire 88, …). entityId is unused (this plays as a global sfx,
    // which is what spell sounds want — full volume, not distance-attenuated). Does NOT touch _bgm.
    private void SendSound(int soundId, uint entityId, byte volume = 100)
    {
        // soundId rides body[3..4] as a u16; the play wrapper reads [obj+0x134] as a WORD, so keep it in range.
        if (soundId <= 0 || soundId > 0xffff) { Log.Info($"   -x sound skipped sfx={soundId} (out of 1..65535)"); return; }
        var d = new List<byte> { 0x00, 0x03 };   // type 0 (sfx); P0=3 -> TLV tail begins after the 5-byte header
        d.AddRange(Be((ushort)soundId));          // body[3..4] soundId (u16BE)
        d.Add(volume);                            // body[5] volume (0..100 -> dB gain; 100 = 0 dB)
        d.Add(0x03); d.Add(0x00); d.Add(0x01);    // body[6..8] tagA=3, B0=0, C=1 (C -> object mode 1 = "play")
        d.Add(0x00); d.Add(0x00); d.Add(0x00);    // body[9..11] B1=0, F=0, B2=0 (all skips 0 -> clean parse exit)
        d.Add(0x00);                              // trailing pad
        SendMap(0x19, _gameInc++, d.ToArray(), $"sound(0x19) sfx={soundId}");
    }
    public void SoundAt(int soundId, uint entityId) => SendSound(soundId, entityId);   // peer-facing (broadcast)

    // Broadcast a cast's effect graphic (0x29) + its sound (0x19) over `overId` to everyone on the map, caster
    // included, so visuals + audio match RTK. Effect id / sound id come from the pcalign ladder
    // (Content.EffectAnim / EffectSound). anim/sound < 0 are skipped.
    //
    // Sound uses RTK's clif_playsound layout (0x19 type 3 = a positional sound bound to the source entity). Static
    // RE of the 4.95 client confirms this path IS wired to the audio player: 0x19 handler (0x450ad0) routes type>=2
    // through the TLV tail 0x450c48 -> spatial builder 0x44e6c0 -> 0x463ab0 -> play fn 0x4798c0. (The earlier
    // action-4th-byte route was a dead end: the client picks an action's sound from a fixed type->sound table, so
    // magic/type 6 -> soundId 0 -> silent regardless of the byte we send.)
    private void BroadcastFx(uint overId, int anim, int sound)
    {
        if (anim >= 0)  _world.Broadcast(_char.Map, p => p.EffectOver(overId, anim));
        if (sound > 0)  _world.Broadcast(_char.Map, p => p.SoundAt(sound, overId));
    }

    // Register a creature server-side AND draw it on the client (via 0x16). Used by the mob commands.
    private Mob SpawnMob(ushort sprite, ushort x, ushort y, string name, int hp, byte dir = 2)
    {
        var mob = new Mob(_nextMobId++, sprite, x, y, name, hp) { Dir = dir };
        _mobs.Add(mob);
        SendCreature(mob.Id, sprite, x, y, dir, $"mob '{name}' gfx={sprite}");
        Log.Info($"   -> spawn mob {mob.Id} '{name}' gfx={sprite} @({x},{y}) hp={hp}");
        return mob;
    }

    // Screen tile where the self is drawn (viewport anchor): the client's own tile->screen conversion
    // centers the player around here, NOT at a fixed spot -- it's edge-aware (clamped near map borders),
    // per Mithia 7.x's clif_sendxy. We previously hardcoded a flat (5,5), which only roughly matched near
    // spawn; anywhere else it diverged from where the CLIENT itself draws the character on screen, and
    // the mismatch is what showed up as "teleport -> animate -> pulled back": our camera scroll (driven by
    // this wrong anchor) fought the client's own correctly-centered render position every step.
    // Default mid-anchor (8,7) matches a 17-wide x 15-tall viewport, same as 5.33's SendSelfWalk.
    private (ushort vx, ushort vy) ViewAnchor()
    {
        // Realm-center (F4) FREEZES the camera: the client stops scrolling and the character walks across a
        // static view. The 0x04 handler writes the map origin as (X - vx, Y - vy) [0x44c660], so to keep the
        // origin pinned at the value captured when F4 was pressed (_lockOx,_lockOy) we draw the self at screen
        // (X - Ox, Y - Oy). That makes (X-vx, Y-vy) == (Ox,Oy) every step -> no scroll. If a step carries the
        // anchor outside the viewport, the client's scroll-gate (0x44c8f0 bounds check) rejects it, which ALSO
        // leaves the origin frozen — so the camera never moves regardless. Without realm-center, use the normal
        // edge-aware anchor (self centered, clamped near map borders).
        if (_realm != 0)
            return ((ushort)(_char.X - _lockOx), (ushort)(_char.Y - _lockOy));
        return EdgeAwareAnchor(_char.X, _char.Y);
    }

    // The normal follow-camera anchor: the screen tile the self is drawn at (mid-view, clamped near borders).
    private (ushort vx, ushort vy) EdgeAwareAnchor(int cx, int cy)
    {
        int xs = _char.MapXs, ys = _char.MapYs;
        int vx = cx < 8 ? cx : (cx >= xs - 8 ? cx - xs + 17 : 8);
        int vy = cy < 7 ? cy : (cy >= ys - 7 ? cy - ys + 15 : 7);
        return ((ushort)Math.Clamp(vx, 0, 16), (ushort)Math.Clamp(vy, 0, 14));
    }

    // 0x04: absolute self (X,Y) + screen anchor. The handler (0x44faf0) sets camera scroll via
    // 0x44c660 AND calls 0x44b140 on the self entity, which advances/commits the self's walk that
    // 0x0C started. Sent at spawn and after every walk step.
    private void SendXy() => SendXyAt(_char.X, _char.Y);

    private void SendXyAt(ushort x, ushort y)
    {
        var (vx, vy) = ViewAnchor();
        var d = new List<byte>();
        d.AddRange(Be(x));
        d.AddRange(Be(y));
        d.AddRange(Be(vx));
        d.AddRange(Be(vy));
        d.Add(0);
        SendMap(0x04, _gameInc++, d.ToArray(),
                $"xy(0x04) pos=({x},{y}) scroll=({x - vx},{y - vy})");
    }

    // Complete/unblock a server-authoritative walk WITHOUT scrolling the camera. The 0x04 handler
    // (0x44faf0) always calls the camera fn (0x44c660) THEN the walk-completion (0x44b140). 0x44c660
    // skips its ENTIRE scroll block — settle-offset, origin write, viewport rebuild — when the bounds-gate
    // 0x44c8f0 fails, and that gate rejects any view anchor outside the viewport. So sending vx/vy way
    // out of range makes the camera code a no-op (zero jerk) while 0x44b140 still runs: it advances the
    // self's logical to the walk destination and clears the walk-active gate so the next step is allowed.
    // This is how realm-center (frozen camera) coexists with fast-move OFF (which REQUIRES the 0x04 to
    // unblock — 0x0C commits the tile but never clears the gate, proven live: the client freezes at
    // frameCtr 2 until a 0x04 arrives).
    private void SendXyCommitNoScroll()
    {
        var d = new List<byte>();
        d.AddRange(Be(_char.X));
        d.AddRange(Be(_char.Y));
        d.AddRange(Be(0xFFFF));   // vx: out of viewport -> scroll-gate (0x44c8f0) fails -> camera untouched
        d.AddRange(Be(0xFFFF));   // vy: out of viewport
        d.Add(0);
        SendMap(0x04, _gameInc++, d.ToArray(), $"xy-commit(0x04 no-scroll) pos=({_char.X},{_char.Y})");
    }

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
    private void HandleWalk(byte[] dec)
    {
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
        bool blocked = offMap || mobHere || (PassEnforce && map != null && Blocked(map, nx, ny));

        // Doors/portals take precedence over collision: if the tile we're stepping toward is a warp
        // source, take it — even if that tile is otherwise "solid" (many doorways sit on object tiles).
        if (!offMap && Content.TryWarp(_char.Map, (ushort)nx, (ushort)ny, out var dest)
            && Content.TryMap(dest.m, out var dm))
        {
            Log.Info($"   -> WARP ({nx},{ny}) on map {_char.Map} -> map {dest.m} '{dm.Name}' ({dest.x},{dest.y})");
            EnterMap(dm.Id, dm.Xs, dm.Ys, dest.x, dest.y, dm.Name);
            return;
        }

        // Mythic Nexus (map 41) zodiac cave entrances are RTK Lua tile-scripts (onScriptedTilesMythic ->
        // mythic_cave_selector), NOT SQL warps — so they need their own handler. Stepping on a zodiac tile
        // warps to the deepest cave tier the player's level/vitals unlock (or refuses, under-levelled).
        if (!offMap && _char.Map == 41 && TryMythicCaveEntrance((ushort)nx, (ushort)ny)) return;

        if (blocked)
        {
            _char.X = (ushort)fromX; _char.Y = (ushort)fromY;   // hold at the from-tile
            SendXy();                                           // 0x04 snap-back cancels the prediction
            Log.Info($"   -> walk dir={dir} BLOCKED at ({nx},{ny}) obj={obj}{(offMap ? " off-map" : "")}{(mobHere ? " mob" : "")} — held at ({_char.X},{_char.Y})");
            return;
        }

        _char.X = (ushort)nx;
        _char.Y = (ushort)ny;

        // Shared world: everyone ELSE on this map watches us step (0x0C animates our entity to the new
        // tile on their clients). Our OWN client is handled by the self-walk modes below (mode 7 stays
        // silent and lets the local controller animate) — so exclude ourselves from the broadcast.
        // We broadcast the SOURCE tile (fromX,fromY), not the destination: the 4.95 client's 0x0C walk ends
        // one tile PAST the packet tile in `dir` (forward-slide overshoot), so anchoring on the source makes
        // a peer land on our true destination instead of one tile ahead. Same fix as the mob moves.
        _world.Broadcast(_char.Map, p => p.MoveEntity(_char.Id, (ushort)fromX, (ushort)fromY, dir), except: this);

        // Our viewport just shifted a tile: stream in mobs that entered view, drop ones that left.
        SyncMobs(_world.View(this, _char.Map).mobs);

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
                // ---- legacy fallbacks (NEXUS_V495_SLOW_MOVE != 5), kept for comparison ----
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
            // NEXUS_V495_SELF_MOVE=0: NO 0x0C. The self sprite stays centered; 0x04's camera scroll
            // (@0x44c660, decaying -10/-12 offsets) is the motion. Smooth, but no leg cycle.
            SendXy();
        }
        Log.Info($"   -> walk dir={dir} ({fromX},{fromY})->({_char.X},{_char.Y}) obj={obj}");

        // The step is complete and the player stands on the new tile — run the after-step scripted tiles
        // (foraging, mythic fall-rooms). A fall-room warps, so this must come last (nothing follows it).
        OnScriptedTileStep();
    }

    // 5.33 turn (0x11 = "side"): the client reports a new facing but does NOT move. We update facing and
    // echo the side packet so the character turns in place. (Treating this as a walk — which we briefly
    // did — forces a step the client never intended, desyncing position and defeating collision.)
    private void HandleTurn(byte[] dec)
    {
        byte side = dec.Length > 0 ? dec[0] : (byte)0;
        _facing = (byte)(side & 3);
        SendSide(_char.Id, _facing);
        _world.Broadcast(_char.Map, p => p.SideEntity(_char.Id, _facing), except: this);   // peers see us turn
        Log.Info($"   -> turn side={_facing} @ ({_char.X},{_char.Y})");
    }

    // 0x20 = the 'o' / Open key. In NexusTK this TOGGLES the door object I'm facing between its closed and open
    // graphic in place (RTK open.lua `openDoors`: setObject(m,x,y, closed<->open) — e.g. Buya door 342<->364;
    // some doors are 3 tiles wide). It is purely COSMETIC (passability is untouched — collision is the ground
    // pass flag only) and shared world state: everyone on the map is told to redraw. Entering a building is
    // done by WALKING onto its warp tile (HandleWalk warp-precedence) — NOT by 'o', so 'o' never warps.
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

        ushort obj = md.Obj(fx, fy);
        var door = DoorToggle(obj);
        Log.Info($"   -> OPEN('o') facing={_facing} front=({fx},{fy}) obj={obj} door={(door is null ? "no" : "yes")}");
        if (door is null) return;

        var (startDx, objs) = door.Value;
        int sx = fx + startDx;
        if (sx < 0 || sx + objs.Length > _char.MapXs) return;   // door run would fall off the map edge

        // Mutate the shared map (so a later 'o' can toggle it back), then tell every client on the map to redraw.
        for (int i = 0; i < objs.Length; i++) md.SetObj(sx + i, fy, objs[i]);
        _world.Broadcast(_char.Map, p => p.PatchObjRow((ushort)sx, (ushort)fy, objs));
    }

    // The door-object toggle table, transcribed from RTK open.lua `openDoors`. Given the object the player is
    // facing, returns (startDx, newObjectIds left->right) — startDx is where the affected run begins relative to
    // the faced tile (a 3-wide door reports which corner you're on), and the ids are the swapped graphics for
    // that run. Mappings are symmetric (closed<->open) so pressing 'o' twice returns the door to its first state.
    // Returns null if the faced object is not a door. (RTK's 17408+ range doors are for maps whose object ids
    // exceed the 4.x 14-bit field and aren't served here, so they're omitted.)
    private static (int startDx, ushort[] objs)? DoorToggle(ushort obj)
    {
        switch (obj)
        {
            // single-tile swinging doors
            case 51:  return (0, new ushort[] { 114 });
            case 114: return (0, new ushort[] { 51 });
            case 57:  return (0, new ushort[] { 115 });
            case 115: return (0, new ushort[] { 57 });
            case 76:  return (0, new ushort[] { 116 });
            case 116: return (0, new ushort[] { 76 });
            case 82:  return (0, new ushort[] { 117 });
            case 117: return (0, new ushort[] { 82 });
            // 3-tile-wide doors — the faced piece tells us which of the three tiles we're standing at
            case 53:  return (0,  new ushort[] { 102, 103, 104 });
            case 54:  return (-1, new ushort[] { 102, 103, 104 });
            case 55:  return (-2, new ushort[] { 102, 103, 104 });
            case 102: return (0,  new ushort[] { 53, 54, 55 });
            case 103: return (-1, new ushort[] { 53, 54, 55 });
            case 104: return (-2, new ushort[] { 53, 54, 55 });
            case 78:  return (0,  new ushort[] { 105, 106, 107 });
            case 79:  return (-1, new ushort[] { 105, 106, 107 });
            case 80:  return (0,  new ushort[] { 105, 106, 107 });
            case 105: return (0,  new ushort[] { 78, 79, 80 });
            case 106: return (-1, new ushort[] { 78, 79, 80 });
            case 107: return (-2, new ushort[] { 78, 79, 80 });
            case 97:  return (-1, new ushort[] { 108, 109, 110 });
            case 109: return (-1, new ushort[] { 96, 97, 98 });
            case 100: return (-1, new ushort[] { 111, 112, 113 });
            case 112: return (-1, new ushort[] { 99, 100, 101 });
        }
        // range-based single-tile toggles: open<->closed differ by a fixed delta
        int o = obj;
        int? nn = obj switch
        {
            >= 340 and <= 341 => o + 20,
            >= 342 and <= 343 => o + 22,   // Buya door 342 -> 364
            >= 344 and <= 345 => o + 18,
            >= 346 and <= 347 => o + 20,
            >= 348 and <= 349 => o + 22,
            >= 350 and <= 353 => o + 24,
            >= 354 and <= 355 => o + 14,
            >= 360 and <= 361 => o - 20,
            >= 362 and <= 363 => o - 18,
            >= 364 and <= 365 => o - 22,   // 364 -> 342 (close it again)
            >= 366 and <= 367 => o - 20,
            >= 368 and <= 369 => o - 14,
            >= 370 and <= 371 => o - 22,
            >= 374 and <= 377 => o - 24,
            >= 378 and <= 379 => o + 16,
            >= 380 and <= 381 => o + 107,
            >= 394 and <= 395 => o - 16,
            >= 487 and <= 488 => o - 107,
            _ => (int?)null,
        };
        return nn is null ? null : (0, new ushort[] { (ushort)nn.Value });
    }

    // Server->client 0x06 CELL PATCH: redraw a horizontal run of cells starting at (startX, y), setting each
    // cell's object to objs[i] while keeping its ground word (tile + passability) unchanged. This is how doors
    // open/close on the client (see HandleOpen). Wire: startX(u16BE) y(u16BE) width(u8) height=1(u8) then per
    // cell ground(u16BE) object(u16BE). The ground word is read live from the map, so it reflects real terrain.
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
            d.AddRange(Be(md.GroundWord(startX + i, y)));   // ground (tile+pass) unchanged
            d.AddRange(Be(objs[i]));                        // new object graphic
        }
        SendMap(0x06, _gameInc++, d.ToArray(), $"cellpatch(0x06) ({startX},{y}) w{objs.Length} objs=[{string.Join(",", objs)}]");
    }
    public void PatchObjRow(ushort startX, ushort y, ushort[] objs) => SendObjRow(startX, y, objs);   // peer-facing (0x06)

    // 0x1b = client setting toggle (F-keys). body[0] = setting id (matches RTK's settings-parse switch):
    //   0x07 = Realm center (F4, camera lock)   0x09 = Fast move   (others logged, not yet acted on).
    // For realm-center we flip the flag and re-apply it via an in-place refresh (0x15 mapinfo carries the
    // realm byte to the client's camera rebuild @0x44c570), mirroring RTK's case 0x07 (sendmapinfo/setpos).
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
            Log.Info($"   -> setting 0x07 Realm-center = {(_realm != 0 ? "ON" : "OFF")} (refreshed in place)");
        }
        else if (setting == 0x09)
        {
            _fastMove = !_fastMove;   // client toggled fast-move; keep our model in sync
            Log.Info($"   -> setting 0x09 Fast-move = {(_fastMove ? "ON (client-authoritative)" : "OFF (server-authoritative)")}");
        }
        else
        {
            Log.Info($"   -> setting 0x{setting:X2} (not handled)");
        }
    }

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

    // Client chat (0x0E): chatType(u8) msgLen(u8) msg[]. Echo it back as over-head speech (0x0D)
    // attributed to the sender's entity so the bubble appears above the character.
    private void HandleChat(byte[] dec)
    {
        if (dec.Length < 2) return;
        byte chatType = dec[0];
        int msgLen = dec[1];
        if (msgLen < 0 || 2 + msgLen > dec.Length) return;
        var msg = dec[2..(2 + msgLen)];
        var text = Encoding.ASCII.GetString(msg);

        // Appearance look-lab: drive 0x33 appearance bytes live so we can read the sprite id-space
        // off the screen instead of guessing.  "!look b0 b1 b2 b3 b4 b5 b6" spawns one test dummy with
        // those 7 bytes; "!row i lo hi" spawns a labeled row sweeping appearance[i] from lo..hi.
        if (text.StartsWith("!look", StringComparison.OrdinalIgnoreCase)) { LookOne(text); return; }
        if (text.StartsWith("!row", StringComparison.OrdinalIgnoreCase)) { LookRow(text); return; }
        // ---- REAL monsters via 0x07 (Monster.epf). check !crecol/!crow before the generic !cre ----
        if (text.StartsWith("!crecol", StringComparison.OrdinalIgnoreCase)) { CreatureColorRow(text); return; } // sweep the 0x07 color byte for one look id
        if (text.StartsWith("!crow", StringComparison.OrdinalIgnoreCase)) { CreatureRow(text); return; }  // sweep monster look ids
        if (text.StartsWith("!cre", StringComparison.OrdinalIgnoreCase)) { CreatureOne(text); return; }    // spawn one real monster [look] [hp] [color]
        // ---- navigation + data-driven content (registries loaded at startup from external data) ----
        if (text.StartsWith("!music", StringComparison.OrdinalIgnoreCase)) { PlayMusicCmd(text); return; } // play a specific track (0x19)
        if (text.StartsWith("!warp", StringComparison.OrdinalIgnoreCase)) { Warp(text); return; }        // warp to a map by name/id [x y]
        if (text.StartsWith("!maps", StringComparison.OrdinalIgnoreCase)) { ListMaps(text); return; }    // list/fuzzy-search maps
        if (text.StartsWith("!mobs", StringComparison.OrdinalIgnoreCase)) { ListMobs(text); return; }    // list/fuzzy-search mobs (BEFORE !mob*)
        if (text.StartsWith("!summon", StringComparison.OrdinalIgnoreCase)) { Summon(text); return; }    // spawn a named mob from the registry
        // ---- items (check !items before !item) ----
        if (text.StartsWith("!icons", StringComparison.OrdinalIgnoreCase)) { IconSweep(text); return; }  // fill bag with client Item.epf frames N..N+26 (icon RE)
        if (text.StartsWith("!items", StringComparison.OrdinalIgnoreCase)) { ListItems(text); return; }  // list/fuzzy-search the item registry
        if (text.StartsWith("!item", StringComparison.OrdinalIgnoreCase)) { GiveItemCmd(text); return; } // summon a named item into the bag
        if (text.StartsWith("!clearinv", StringComparison.OrdinalIgnoreCase)) { ClearInventory(); return; } // empty the bag + gear
        // ---- mobs / combat (check !mobrow before !mob, !spawn before the catch-all !s) ----
        if (text.StartsWith("!rabbit", StringComparison.OrdinalIgnoreCase)) { SpawnRabbit(); return; }  // MVP: one wandering, killable rabbit
        if (text.StartsWith("!mobrow", StringComparison.OrdinalIgnoreCase)) { MobRow(text); return; }   // sweep graphic ids
        if (text.StartsWith("!mob", StringComparison.OrdinalIgnoreCase)) { MobOne(text); return; }       // spawn one creature
        if (text.StartsWith("!kill", StringComparison.OrdinalIgnoreCase)) { KillMobs(); return; }         // despawn all mobs
        if (text.StartsWith("!weapon", StringComparison.OrdinalIgnoreCase)) { SetWeapon(text); return; }  // equip weapon sprite
        if (text.StartsWith("!ride", StringComparison.OrdinalIgnoreCase) || text.StartsWith("!mount", StringComparison.OrdinalIgnoreCase)) { ToggleMount(text); return; } // get on/off the horse (form byte 3)
        if (text.StartsWith("!lvl", StringComparison.OrdinalIgnoreCase)) { SetBaseStat("level", text); return; }   // set base level (test wear reqs)
        if (text.StartsWith("!might", StringComparison.OrdinalIgnoreCase)) { SetBaseStat("might", text); return; } // set base might (test wear reqs)
        if (text.StartsWith("!class", StringComparison.OrdinalIgnoreCase)) { SetClass(text); return; }  // set the profile class/path line
        if (text.StartsWith("!spells", StringComparison.OrdinalIgnoreCase)) { TeachClassSpells(); return; }      // learn ALL my class's spells up to my level
        if (text.StartsWith("!learnspell", StringComparison.OrdinalIgnoreCase)) { LearnSpellCmd(text); return; } // learn one spell by name/id
        if (text.StartsWith("!forgetspells", StringComparison.OrdinalIgnoreCase)) { ForgetSpells(); return; }    // clear the spellbook
        if (text.StartsWith("!align", StringComparison.OrdinalIgnoreCase)) { SetAlignment(text); return; }        // set sub-alignment (Kwisin/Mingken/Ohaeng) for !spells
        if (text.StartsWith("!swingsnd", StringComparison.OrdinalIgnoreCase)) { SetSwingSound(text); return; }  // set + audition the melee swing sfx id
        if (text.StartsWith("!snd", StringComparison.OrdinalIgnoreCase)) { SoundProbe(text); return; }   // play raw client sound ids (calibrate the NexusTK.snd id space)
        if (text.StartsWith("!efx", StringComparison.OrdinalIgnoreCase)) { EffectProbe(text); return; }  // play raw Effect.tbl animation ids over self (calibrate the effect id space)
        if (text.StartsWith("!hit", StringComparison.OrdinalIgnoreCase)) { HitProbe(text); return; }      // 0x13 over-head HP bar + hit anim on the faced mob (calibrate NEXUS_HIT_CRIT)
        if (text.StartsWith("!spawn", StringComparison.OrdinalIgnoreCase)) { SpawnCritters(text); return; } // squirrel/rabbit pack
        if (text.StartsWith("!sweep", StringComparison.OrdinalIgnoreCase)) { StatSweep(text); return; }
        if (text.StartsWith("!batch", StringComparison.OrdinalIgnoreCase)) { StatBatch(text); return; }
        if (text.StartsWith("!r6", StringComparison.OrdinalIgnoreCase)) { StatReplay6x(text); return; }
        if (text.StartsWith("!stg", StringComparison.OrdinalIgnoreCase)) { StatGradient(text); return; }
        if (text.StartsWith("!leg", StringComparison.OrdinalIgnoreCase)) { SendProfileReplay6x(); return; }   // exact 6.x 0x39 replay
        if (text.StartsWith("!self", StringComparison.OrdinalIgnoreCase)) { SendSelfProfile(); return; }        // native 0x39 builder
        if (text.StartsWith("!ckm", StringComparison.OrdinalIgnoreCase)) { SendClickMarker(); return; }             // 0x34 with marker strings
        if (text.StartsWith("!click", StringComparison.OrdinalIgnoreCase)) { SendClickProfile(_char.Id); return; }  // native 0x34 click-profile
        if (text.StartsWith("!nat", StringComparison.OrdinalIgnoreCase)) { StatNation(text); return; }              // sweep nation id -> HUD name
        if (text.StartsWith("!hp", StringComparison.OrdinalIgnoreCase)) { StatHpTest(text); return; }               // verify maxHP/maxMP offsets
        if (text.StartsWith("!s", StringComparison.OrdinalIgnoreCase)) { StatProbe(text); return; }

        // Real chat (not a ! command): everyone on the map hears it. Broadcast the over-head bubble (0x0D)
        // to all co-located players INCLUDING us, so we see our own bubble too.
        _world.Broadcast(_char.Map, p => p.SpeakEntity(chatType, _char.Id, msg));
        Log.Info($"   -> speech type={chatType}: \"{text}\" -> map {_char.Map}");
    }

    private uint _probeId = 1000;

    // Parse up to 7 whitespace-separated byte values after the command word.
    private static byte[] ParseBytes(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var app = new byte[7];
        for (int i = 1; i < parts.Length && i - 1 < 7; i++) byte.TryParse(parts[i], out app[i - 1]);
        return app;
    }

    // Spawn one dummy just north of the player with the given 7 appearance bytes; its name is the
    // bytes so the screen is self-labeling. New id each call so repeats don't collide.
    private void LookOne(string text)
    {
        var app = ParseBytes(text);
        uint id = ++_probeId;
        ushort x = (ushort)Math.Clamp(_char.X, 0, _char.MapXs - 1);
        ushort y = (ushort)Math.Clamp(_char.Y - 2, 0, _char.MapYs - 1);
        SendLook(id, x, y, dir: 2, app, renderKind: 1, $"{app[0]}-{app[1]}-{app[2]}", $"look-lab {id}");
        Log.Info($"   -> LOOK dummy id={id} @({x},{y}) app=[{string.Join(" ", app)}]");
    }

    // "!row i lo hi [body]": sweep appearance byte [i] from lo..hi across a west->east row of dummies, all
    // other bytes 0. One screenshot then maps that byte's entire id space. Optional 4th arg sets appearance
    // byte [0] (the BODY/sex) for the whole row — default 1 (female, the historically-swept base); pass 0 to
    // sweep the MALE body (its weapon/shield defaults differ from female — male frame 0 was never mapped).
    private void LookRow(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int idx = parts.Length > 1 && int.TryParse(parts[1], out var pi) ? Math.Clamp(pi, 0, 6) : 0;
        int lo = parts.Length > 2 && int.TryParse(parts[2], out var pl) ? pl : 0;
        int hi = parts.Length > 3 && int.TryParse(parts[3], out var ph) ? ph : lo + 7;
        int body = parts.Length > 4 && int.TryParse(parts[4], out var pb) ? pb : 1;
        ushort y = (ushort)Math.Clamp(_char.Y - 2, 0, _char.MapYs - 1);
        int col = 0;
        for (int v = lo; v <= hi && col < 12; v++, col++)
        {
            // Base = valid body (0)=body, normal form (1)=0, so sweeping [2..6] reads cleanly instead of
            // being blanked by the form/state byte. appearance[1] itself is the form table (0/4 normal,
            // 1 ghost, 3 mounted, 5 invisible-spell, most others = no sprite).
            var app = new byte[] { (byte)body, 0, 0, 0, 0, 0, 0 };
            app[idx] = (byte)v;
            uint id = ++_probeId;
            ushort x = (ushort)Math.Clamp(_char.X - 4 + col, 0, _char.MapXs - 1);
            SendLook(id, x, y, dir: 2, app, renderKind: 1, $"{idx}={v}", $"row byte[{idx}]={v}");
        }
        Log.Info($"   -> LOOK row: appearance[{idx}] sweep {lo}..{hi}");
    }

    // ---- mob / combat lab ----
    // The 4.95 creature GRAPHIC id-space is unknown, so we discover it live (look-lab style) via 0x16.
    //   "!mob <hi> <lo> [hp]"   spawn ONE creature on the tile in front of you (gfx = hi*256+lo) so you
    //                           can see it and immediately whack it.
    //   "!mobrow <lo> <hi> [step]"  spawn a W->E row sweeping graphic id lo..hi (step defaults to 1).
    //                           The gfx id is a FRAME index into the monster archive (client adds
    //                           0x4000, category "I"), and Monster.tbl's "Starting" column lists each
    //                           monster's idle frame — the first ~19 monsters start at 0,20,40,...,360.
    //                           So "!mobrow 0 360 20" shows one idle sprite per monster 0..18.
    //   "!spawn [hi] [lo]"      drop a little pack of critters around you at one graphic id.
    //   "!kill"                 despawn every mob.

    private static int[] ParseInts(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var vals = new List<int>();
        for (int i = 1; i < parts.Length; i++) if (int.TryParse(parts[i], out var v)) vals.Add(v);
        return vals.ToArray();
    }

    // "!cre <lookId> [hp] [color]": spawn ONE real monster (Monster.epf, via 0x07) on the tile in front
    // of you, so you can see it AND immediately melee it (combat is unchanged — it hits any Mob on the
    // tile). [color] is the 0x07 color byte we're trying to identify as a recolor/palette selector.
    private void CreatureOne(string text)
    {
        var a = ParseInts(text);
        int look = a.Length > 0 ? a[0] : 0;
        int hp = a.Length > 1 ? a[1] : 6;
        int color = a.Length > 2 ? a[2] : 0;
        var (fx, fy) = FrontTile();
        ushort x = (ushort)Math.Clamp(fx, 0, _char.MapXs - 1);
        ushort y = (ushort)Math.Clamp(fy, 0, _char.MapYs - 1);
        SpawnMonster((ushort)look, x, y, $"c{look}", hp, dir: (byte)((_facing + 2) & 3), color: (byte)color);
    }

    // ===== MVP: spawn a rabbit, watch it wander, kill it =====================================
    // The whole lifecycle end-to-end, kept deliberately hardcoded (one rabbit, look 21, 6 HP, random
    // wander near its spawn) before generalizing into a real mob/AI/spawn system. It mirrors how the RTK
    // map-server drives a mob: the server owns the entity + HP, ticks its AI on a timer, streams walk
    // steps (0x0C) to the client, and despawns it (0x0E) on death. Combat is the EXISTING melee path —
    // face the rabbit and press space; HandleAttack finds it on the front tile and deals damage.
    private const ushort RabbitLook = 21;   // Monster.tbl look id — validated shape-match: rabbit = 21

    // "!rabbit": drop a single wandering rabbit into the SHARED world on the tile in front of you.
    // Everyone on the map sees it, everyone fights the SAME one, and World.Tick drives its wander — no
    // per-session task anymore (that only moved the rabbit on the spawner's screen).
    private void SpawnRabbit()
    {
        var (fx, fy) = FrontTile();
        ushort x = (ushort)Math.Clamp(fx, 0, _char.MapXs - 1);
        ushort y = (ushort)Math.Clamp(fy, 0, _char.MapYs - 1);
        byte dir = (byte)((_facing + 2) & 3);   // face the player on arrival
        SummonWorldMob(RabbitLook, x, y, "Rabbit", hp: 6, dir: dir, color: 0, exp: 5, moveTime: 3000);
        SendLog("A rabbit appears. Face it and press space to attack.");
    }

    // Register a mob in the SHARED world (drawn via 0x07 = Monster.epf) and broadcast the spawn to every
    // player on the map. World.Tick then wanders it (leashed to its spawn tile); combat resolves against
    // the world's authoritative HP in HandleAttack. This is the gameplay-mob path (!rabbit / !summon);
    // the debug lab (!cre/!mob/!crow/look-lab) still uses the session-local SpawnMonster/SpawnMob.
    private Mob SummonWorldMob(ushort look, ushort x, ushort y, string name, int hp, byte dir, byte color,
                               int exp = 0, int moveTime = 2500, string key = "")
    {
        var mob = new Mob(_world.AllocateMobId(), look, x, y, name, hp)
        {
            Key = key,   // MobDef identifier (for quest kill-matching); empty for keyless debug summons
            Dir = dir, Color = color, Exp = exp, HomeX = x, HomeY = y, Wander = true,
            MoveTime = moveTime, MoveTimer = Random.Shared.Next(moveTime),
        };
        _world.AddMob(_char.Map, mob);   // broadcasts the 0x07 spawn to every player on the map (incl. us)
        Log.Info($"   -> world spawn mob {mob.Id} '{name}' look={look} c{color} @({x},{y}) hp={hp} on map {_char.Map}");
        return mob;
    }

    // ===== navigation: warp + map/mob listing + data-driven summon ==========================

    // ---- Mythic Nexus zodiac cave entrances (map 41) ----
    // RTK gates each of the 12 zodiac caves behind a level/vitals check (Scripts/mythicCaveReqCheck.lua) and
    // an easy/dangerous/deadly tier picker (NPCs/mythic/mythic_cave_selector.lua). With the picker menu off
    // (the default — it's GM/Config-only), RTK auto-warps to the DEEPEST tier the player qualifies for, so we
    // reproduce that: tier 1 -> base map, tier 2 -> base+3000, tier 3 -> base+4000. The two-tile entrance
    // footprints and destinations are copied verbatim from onScriptedTilesMythic.lua + mythic_cave_selector.lua.
    private readonly record struct CaveDest(ushort Map, ushort X, ushort Y);
    private readonly record struct CaveReq(byte Level, uint Health, uint Magic);

    private static readonly Dictionary<(ushort x, ushort y), string> MythicTiles = new()
    {
        [(49, 12)] = "Rabbit",  [(50, 12)] = "Rabbit",
        [(43, 48)] = "Monkey",  [(44, 48)] = "Monkey",
        [(18, 25)] = "Dog",     [(19, 25)] = "Dog",
        [(48, 30)] = "Rooster", [(49, 30)] = "Rooster",
        [(9, 12)]  = "Rat",     [(10, 12)] = "Rat",
        [(15, 48)] = "Horse",   [(16, 48)] = "Horse",
        [(29, 45)] = "Ox",      [(30, 45)] = "Ox",
        [(17, 39)] = "Pig",     [(18, 39)] = "Pig",
        [(40, 25)] = "Snake",   [(41, 25)] = "Snake",
        [(41, 39)] = "Sheep",   [(42, 39)] = "Sheep",
        [(10, 30)] = "Tiger",   [(11, 30)] = "Tiger",
        [(29, 19)] = "Dragon",  [(30, 19)] = "Dragon",
    };

    // animal -> cave-1 base map + the arrival tile inside it (same coords for every tier). +3000 = cave 2, +4000 = cave 3.
    private static readonly Dictionary<string, CaveDest> MythicDest = new()
    {
        ["Rabbit"] = new(201, 13, 19), ["Monkey"] = new(160, 1, 1),  ["Dog"]    = new(191, 11, 27),
        ["Rooster"] = new(214, 9, 58), ["Rat"]    = new(151, 12, 18), ["Horse"]  = new(246, 7, 22),
        ["Ox"]     = new(170, 2, 27),  ["Pig"]    = new(181, 26, 22), ["Snake"]  = new(231, 17, 1),
        ["Sheep"]  = new(470, 14, 12), ["Tiger"]  = new(100, 30, 4),  ["Dragon"] = new(257, 17, 10),
    };

    // Per-animal tier requirements [tier1, tier2, tier3]. A tier is met when level >= Level AND
    // (baseMaxHP >= Health OR baseMaxMP >= Magic). Tier-1 has no HP/MP floor, so level alone unlocks it.
    private static readonly Dictionary<string, CaveReq[]> MythicReqs = new()
    {
        ["Rabbit"]  = new[] { new CaveReq(25, 0, 0),     new CaveReq(70, 0, 0),           new CaveReq(99, 20000, 10000) },
        ["Monkey"]  = new[] { new CaveReq(32, 0, 0),     new CaveReq(77, 0, 0),           new CaveReq(99, 40000, 20000) },
        ["Dog"]     = new[] { new CaveReq(39, 0, 0),     new CaveReq(84, 0, 0),           new CaveReq(99, 60000, 30000) },
        ["Rooster"] = new[] { new CaveReq(46, 0, 0),     new CaveReq(91, 0, 0),           new CaveReq(99, 100000, 50000) },
        ["Rat"]     = new[] { new CaveReq(53, 0, 0),     new CaveReq(98, 0, 0),           new CaveReq(99, 140000, 70000) },
        ["Horse"]   = new[] { new CaveReq(60, 0, 0),     new CaveReq(99, 30000, 15000),   new CaveReq(99, 180000, 90000) },
        ["Ox"]      = new[] { new CaveReq(67, 0, 0),     new CaveReq(99, 50000, 25000),   new CaveReq(99, 220000, 110000) },
        ["Pig"]     = new[] { new CaveReq(74, 0, 0),     new CaveReq(99, 80000, 40000),   new CaveReq(99, 260000, 130000) },
        ["Snake"]   = new[] { new CaveReq(81, 0, 0),     new CaveReq(99, 110000, 55000),  new CaveReq(99, 300000, 150000) },
        ["Sheep"]   = new[] { new CaveReq(88, 0, 0),     new CaveReq(99, 140000, 70000),  new CaveReq(99, 340000, 170000) },
        ["Tiger"]   = new[] { new CaveReq(95, 0, 0),     new CaveReq(99, 170000, 85000),  new CaveReq(99, 380000, 190000) },
        ["Dragon"]  = new[] { new CaveReq(99, 0, 0),     new CaveReq(99, 200000, 100000), new CaveReq(99, 420000, 210000) },
    };

    // Deepest tier (1..3) the player unlocks for `animal`, or a negative "how close" code when locked out:
    // 0 = within 3 levels, -1 = within 4-7, -2 = 8+ levels short. Mirrors mythicCaveReqCheck.lua exactly.
    private int MythicCaveTier(string animal)
    {
        var reqs = MythicReqs[animal];
        for (int i = 2; i >= 0; i--)   // check tier 3 -> 1, return the first satisfied
        {
            var r = reqs[i];
            if (_char.Level >= r.Level && (_char.MaxHp >= r.Health || _char.MaxMp >= r.Magic))
                return i + 1;
        }
        int levelsUntil = reqs[0].Level - _char.Level;
        if (levelsUntil >= 8) return -2;
        if (levelsUntil >= 4) return -1;
        return 0;
    }

    // Handle a step onto a zodiac entrance tile on map 41: warp into the deepest unlocked cave tier, or
    // refuse (snap back + flavour line) when under-levelled. Returns false if (x,y) isn't an entrance tile.
    private bool TryMythicCaveEntrance(ushort x, ushort y)
    {
        if (!MythicTiles.TryGetValue((x, y), out var animal)) return false;
        int tier = MythicCaveTier(animal);
        if (tier < 1)
        {
            SendMessage(tier switch
            {
                -2 => "Nightmarish visions of your own death repel you.",
                0  => "You almost understand the secrets of this entrance.",
                _  => "You are not yet ready to enter here.",
            });
            SendXy();   // cancel the client's step prediction — the entrance holds them out
            Log.Info($"   -> MYTHIC {animal} entrance REFUSED (tier {tier}, level {_char.Level})");
            return true;
        }

        var d = MythicDest[animal];
        ushort destMap = (ushort)(d.Map + (tier == 3 ? 4000 : tier == 2 ? 3000 : 0));
        if (!Content.TryMap(destMap, out var dm)) { destMap = d.Map; Content.TryMap(destMap, out dm); }
        if (dm is null) { SendXy(); return true; }   // map data missing — don't strand the player
        Log.Info($"   -> MYTHIC {animal} cave {tier} -> map {destMap} '{dm.Name}' ({d.X},{d.Y}) [level {_char.Level}]");
        EnterMap(dm.Id, dm.Xs, dm.Ys, d.X, d.Y, dm.Name);
        return true;
    }

    // ---- After-step scripted tiles (fire once the step has completed, i.e. standing on the new tile) ----
    // RTK runs these from onScriptedTile on every walk. We only port the two that are self-contained AND live
    // entirely on maps the 4.95 client can render: mythic-cave fall-rooms and bush/tree foraging.
    private void OnScriptedTileStep()
    {
        TryForage();                         // adjacent apple tree / rose bush -> small chance of an item
        if (TryMythicFallRoom()) return;     // mythic cave trap floor -> drop to a lower sub-room (warps)
    }

    // Mythic cave "fall rooms": inside a zodiac cave, every step has a 1/500 chance to drop through the floor
    // to a fixed landing tile in a lower sub-room (onScriptedTilesMythicFallRooms.lua). The three depth tiers
    // mirror each other (+3000 = cave 2, +4000 = cave 3), so the tier-1 groups below are expanded to all three.
    private const int FallRate = 500;
    private static readonly (ushort dest, ushort dx, ushort dy, ushort[] src)[] FallGroups =
    {
        (169, 23, 3,  new ushort[] { 167, 168 }),        // Monkey
        (217, 10, 17, new ushort[] { 212, 216, 218 }),   // Rooster
        (208, 15, 18, new ushort[] { 203, 205, 208 }),   // Rabbit
        (479, 23, 3,  new ushort[] { 482, 484 }),        // Sheep
        (180, 22, 7,  new ushort[] { 177, 178 }),        // Ox
        (183, 2, 9,   new ushort[] { 186, 187, 190 }),   // Pig
        (244, 15, 25, new ushort[] { 243, 245, 247 }),   // Horse
        (196, 11, 38, new ushort[] { 192, 194, 199 }),   // Dog
        (255, 12, 34, new ushort[] { 253, 254, 258 }),   // Dragon
        (235, 1, 4,   new ushort[] { 233, 236, 237 }),   // Snake
    };
    // map -> landing (destMap, x, y). Built once from FallGroups (all three tiers) + the tier-less Iron lab.
    private static readonly Dictionary<ushort, (ushort map, ushort x, ushort y)> FallRooms = BuildFallRooms();
    private static Dictionary<ushort, (ushort, ushort, ushort)> BuildFallRooms()
    {
        var m = new Dictionary<ushort, (ushort, ushort, ushort)>();
        foreach (var g in FallGroups)
            for (ushort off = 0; off <= 4000; off += 3000)   // 0 = cave 1, +3000 = cave 2, +4000 = cave 3
                foreach (var s in g.src)
                    m[(ushort)(s + off)] = ((ushort)(g.dest + off), g.dx, g.dy);
        foreach (var s in new ushort[] { 1302, 1303, 1304, 1305, 1306 })   // Iron lab -> Treasure Room (no tiers)
            m[s] = (1307, 4, 5);
        return m;
    }

    private bool TryMythicFallRoom()
    {
        if (!FallRooms.TryGetValue(_char.Map, out var f)) return false;
        if (Random.Shared.Next(FallRate) != 0) return false;
        if (!Content.TryMap(f.map, out var dm)) return false;   // dest not renderable -> no fall (don't strand)
        Log.Info($"   -> FALL through map {_char.Map} -> {f.map} '{dm.Name}' ({f.x},{f.y})");
        EnterMap(dm.Id, dm.Xs, dm.Ys, f.x, f.y, dm.Name);
        return true;
    }

    // Bush/tree foraging (onScriptedTilesBushTree.lua): standing next to an apple tree (object ids 860-864)
    // or a rose bush (876-889), each step has a 1/50 chance to pick an apple / rose. Objects are read from the
    // map's OWN object layer (same ids RTK's checkProximityObjects uses), scanned in the 3x3 around the player.
    private const int ForageRate = 50;
    private void TryForage()
    {
        var map = MapData.For(_char.Map, _char.MapXs, _char.MapYs);
        if (map is null) return;

        string? item = null;
        for (int dy = -1; dy <= 1 && item is null; dy++)
        for (int dx = -1; dx <= 1 && item is null; dx++)
        {
            int tx = _char.X + dx, ty = _char.Y + dy;
            if (tx < 0 || ty < 0 || tx >= _char.MapXs || ty >= _char.MapYs) continue;
            ushort o = map.Obj(tx, ty);
            if (o >= 860 && o <= 864) item = "apple";
            else if (o >= 876 && o <= 889) item = "rose";
        }
        if (item is null) return;
        if (Random.Shared.Next(ForageRate) != 0) return;

        var def = Content.FindItem(item);
        if (def is null || !GiveItem(def)) return;
        SendMessage(item == "apple" ? "You found an apple." : "You find a beautiful rose!");
        Log.Info($"   -> FORAGE {item} on map {_char.Map} @({_char.X},{_char.Y})");
    }

    // Move the player to another map (or a far tile) and redraw. On 4.95 the client loads its OWN local
    // Maps\TK<id>.map from the 0x15 mapId, so a warp is just: update tracked position, then re-send the
    // entry trio — 0x15 (map) + 0x04 (coords + camera) + 0x33 (our sprite). The world object (0x02) and
    // our entity id (0x05) are already established this session, so those are NOT resent.
    private void EnterMap(ushort mapId, ushort xs, ushort ys, ushort x, ushort y, string mapName)
    {
        // Leave the OLD map in the shared world (despawn us for the players we're leaving behind), and
        // clear our session-local debug dummies (the client drops all foreign entities on a map change).
        _world.LeaveMap(this, _char.Map);
        _mobs.Clear();
        _dlgReply = null;    // orphan any NPC prompt awaiting a reply — its NPC is on the old map
        ForgetShownMobs();   // new map -> the client wiped every foreign entity; re-stream from scratch

        _char.Map = mapId;
        _char.MapXs = xs;
        _char.MapYs = ys;
        _char.X = (ushort)Math.Clamp((int)x, 0, xs - 1);
        _char.Y = (ushort)Math.Clamp((int)y, 0, ys - 1);

        SendMapInfo(mapId, xs, ys, mapName, 232, _gameInc++);   // 0x15 (light arg ignored; uses LightValue)
        SendXy();                                                // 0x04 coords + camera anchor
        SendSelfLook();                                          // 0x33 draw self on the new map
        PlayMapMusic(mapId);                                     // 0x19 swap to the new map's track (if different)

        // Join the NEW map: draw the players + mobs already there for us, and broadcast us to them.
        var (peers, mobs) = _world.EnterMap(this, mapId);
        foreach (var p in peers) ShowPlayer(p);
        SyncMobs(mobs);   // stream the in-view mobs of the new map
        foreach (var gi in _world.ItemsOn(mapId)) ShowGroundItem(gi);   // floor items on the new map (0x16)
        Log.Info($"   -> ENTER map {mapId} '{mapName}' {xs}x{ys} @({_char.X},{_char.Y}) — {peers.Length} player(s), {mobs.Length} mob(s) here");
    }

    // "!warp <map name or id> [x y]": jump to another map by fuzzy name or numeric id, optional coords.
    // Trailing "x y" integers are the destination tile; the rest is the map query. Defaults to map centre.
    private void Warp(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) { SendLog("usage: !warp <map name or id> [x y]"); return; }

        int? cx = null, cy = null, end = parts.Length;
        if (parts.Length >= 4 && int.TryParse(parts[^1], out var py) && int.TryParse(parts[^2], out var px))
        { cx = px; cy = py; end = parts.Length - 2; }

        string query = string.Join(' ', parts[1..end.Value]);
        var map = Content.FindMap(query);
        if (map is null) { SendLog($"no map matches \"{query}\" — try  !maps {query}"); return; }

        ushort x = (ushort)(cx ?? map.Xs / 2);
        ushort y = (ushort)(cy ?? map.Ys / 2);
        EnterMap(map.Id, map.Xs, map.Ys, x, y, map.Name);
        SendLog($"Warped to {map.Name} (map {map.Id}, {map.Xs}x{map.Ys}) at ({_char.X},{_char.Y}).");
    }

    // "!maps [filter]": list maps, fuzzy-ranked by name (blank = alphabetical). Capped so we don't flood.
    private void ListMaps(string text)
    {
        string q = text.Length > "!maps".Length ? text["!maps".Length..].Trim() : "";
        var found = Content.SearchMaps(q, 15);
        if (found.Count == 0) { SendLog(q.Length == 0 ? "no maps loaded (run re/build_map_index.py)" : $"no maps match \"{q}\""); return; }
        SendLog($"maps{(q.Length > 0 ? $" ~ \"{q}\"" : "")} ({found.Count} of {Content.Maps.Count}):");
        foreach (var m in found) SendLog($"  {m.Id}: {m.Name} ({m.Xs}x{m.Ys})");
    }

    // "!mobs [filter]": list summonable mobs, fuzzy-ranked by name.
    private void ListMobs(string text)
    {
        string q = text.Length > "!mobs".Length ? text["!mobs".Length..].Trim() : "";
        var found = Content.SearchMobs(q, 15);
        if (found.Count == 0) { SendLog(q.Length == 0 ? "no mobs loaded (check re/monster-matcher/rtk_mobs.csv)" : $"no mobs match \"{q}\""); return; }
        SendLog($"mobs{(q.Length > 0 ? $" ~ \"{q}\"" : "")} ({found.Count} of {Content.Mobs.Count}):");
        foreach (var m in found) SendLog($"  {m.Name} — look {m.Look} c{m.Color}, {m.Hp}hp, {m.Exp}xp   (!summon {m.Name})");
    }

    // "!summon <mob name or id>": spawn a real, named creature from the registry on the tile in front of
    // you — correct look + palette colour + HP + exp, all data-driven. Same 0x07 spawn + melee-kill loop
    // as !rabbit, but any of the 700+ mobs by name. (No wander AI yet — that generalizes next.)
    private void Summon(string text)
    {
        string q = text.Length > "!summon".Length ? text["!summon".Length..].Trim() : "";
        if (q.Length == 0) { SendLog("usage: !summon <mob name or id>   (browse with  !mobs <name>)"); return; }
        var mob = Content.FindMob(q);
        if (mob is null) { SendLog($"no mob matches \"{q}\" — try  !mobs {q}"); return; }

        var (fx, fy) = FrontTile();
        ushort x = (ushort)Math.Clamp(fx, 0, _char.MapXs - 1);
        ushort y = (ushort)Math.Clamp(fy, 0, _char.MapYs - 1);
        SummonWorldMob(mob.Look, x, y, mob.Name, mob.Hp, dir: (byte)((_facing + 2) & 3), color: mob.Color, exp: mob.Exp, moveTime: mob.MoveTime, key: mob.Key);
        SendLog($"Summoned {mob.Name} into the world (look {mob.Look} c{mob.Color}, {mob.Hp}hp).");
    }

    // ===== items ================================================================================
    // Wire layouts translated from RTK 7.x clif.c (clif_sendadditem/senddelitem/equipit/unequipit and
    // the parse* handlers). Multi-byte ints are big-endian, same as every other packet here. The
    // send-side opcodes (0x0F/0x10/0x37/0x38) are the historically-stable TK inventory opcodes; the
    // recv-side (0x07/0x08/0x17/0x1A/0x1C/0x1F/0x24) are confirmed to line up with 4.95 because the
    // walk/turn/chat/attack/setting opcodes already do. See docs §11c.

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s ?? "");

    private InvItem? InvAt(int slot) => _char.Inventory.FirstOrDefault(i => i.Slot == slot);

    private int FreeSlot()
    {
        for (int i = 0; i < _char.MaxInv; i++)
            if (_char.Inventory.All(it => it.Slot != i)) return i;
        return -1;
    }

    /// <summary>Put <paramref name="amount"/> of <paramref name="def"/> into the bag (stacking if the item
    /// stacks and a stack already exists), draw the slot (0x0F), and return false if the pack is full.</summary>
    private bool GiveItem(ItemDef def, int amount = 1, ushort dura = 0, string customName = "")
    {
        if (dura == 0 && def.IsEquip) dura = def.Durability;
        if (def.Stackable)
        {
            var stack = _char.Inventory.FirstOrDefault(i => i.ItemId == def.Id && i.CustomName == customName);
            if (stack is not null) { stack.Amount += amount; SendAddItem(stack); return true; }
        }
        int slot = FreeSlot();
        if (slot < 0) { SendLog("Your pack is full."); return false; }
        var it = new InvItem((byte)slot, def.Id, amount, dura) { CustomName = customName };
        _char.Inventory.Add(it);
        SendAddItem(it);
        return true;
    }

    // Redraw the whole bag + worn gear (on world entry / warp): one 0x0F per bag slot, one 0x37 per gear slot.
    private void RefreshInventory()
    {
        foreach (var it in _char.Inventory.OrderBy(i => i.Slot)) SendAddItem(it);
        foreach (var e in _char.Equipment) SendEquip(e);
    }

    /// <summary>Persist the character if it has actually entered the world (mirrors the inline guard used at the
    /// other save sites). Cheap best-effort; the store swallows IO errors so a save never crashes a session.</summary>
    private void SaveChar() { if (_enteredWorld) _store.Save(_char); }

    // ===== quests (see Server/Quests.cs, NpcContext quest helpers) ================================
    // Quest state lives in _char.Quests (a flat key->int map, persisted): a quest's stage under its key, its
    // progress tallies under composite counter keys. These internal helpers are the whole surface the quest
    // scripts (via NpcContext) and the kill hook touch, so quest logic never reaches into session internals.
    internal int  QuestStage(string questKey) => _char.Quests.GetValueOrDefault(questKey);
    internal void SetQuestStage(string questKey, int stage) { _char.Quests[questKey] = stage; SaveChar(); }
    internal int  QuestCounter(string counterKey) => _char.Quests.GetValueOrDefault(counterKey);

    /// <summary>Award experience (refresh the HUD exp bar + persist). Quest EXP rewards funnel through here.</summary>
    internal void AwardExp(uint amount)  { if (amount == 0) return; _char.Exp   += amount; SendStats(); SaveChar(); }
    /// <summary>Award coin (refresh the HUD + persist).</summary>
    internal void AwardGold(uint amount) { if (amount == 0) return; _char.Coins += amount; SendStats(); SaveChar(); }

    /// <summary>How many of an item (by content key) the player is carrying, summed across stacks.</summary>
    internal int CountItem(string itemKey)
    {
        var def = Content.ItemByKey(itemKey);
        return def is null ? 0 : _char.Inventory.Where(i => i.ItemId == def.Id).Sum(i => i.Amount);
    }

    /// <summary>Consume <paramref name="amount"/> of an item by key (across stacks, low slots first), redrawing
    /// each touched slot. Returns false and takes nothing if the player doesn't have that many.</summary>
    internal bool TakeItem(string itemKey, int amount)
    {
        var def = Content.ItemByKey(itemKey);
        if (def is null || amount <= 0 || CountItem(itemKey) < amount) return false;
        int remaining = amount;
        foreach (var it in _char.Inventory.Where(i => i.ItemId == def.Id).OrderBy(i => i.Slot).ToList())
        {
            if (remaining <= 0) break;
            int take = Math.Min(remaining, it.Amount);
            it.Amount -= take; remaining -= take;
            if (it.Amount <= 0) { _char.Inventory.Remove(it); SendDelItem((byte)it.Slot, 1); }   // reason 1 = removed
            else SendAddItem(it);
        }
        SaveChar();
        return true;
    }

    /// <summary>Give a reward item by key (stacking; one call per unit for non-stackables). False if the item is
    /// unknown or the pack filled mid-give (GiveItem already told the player).</summary>
    internal bool GiveRewardItem(string itemKey, int amount)
    {
        var def = Content.ItemByKey(itemKey);
        if (def is null || amount <= 0) return false;
        if (def.Stackable) { if (!GiveItem(def, amount)) return false; }
        else for (int i = 0; i < amount; i++) if (!GiveItem(def)) return false;
        SaveChar();
        return true;
    }

    /// <summary>Called on every world-mob kill: bump the lifetime kill tally for that mob key (RTK's
    /// per-mob kill count). Quests read a delta of this — kills since they were accepted — so nothing else is
    /// needed here. Keyless kills (debug summons) are ignored.</summary>
    private void TallyKill(Mob m)
    {
        if (string.IsNullOrEmpty(m.Key)) return;
        _char.Kills[m.Key] = _char.Kills.GetValueOrDefault(m.Key) + 1;
        SaveChar();
    }

    /// <summary>Lifetime kills recorded for a mob key (RTK's <c>player:killCount</c>).</summary>
    internal int KillCount(string mobKey) => _char.Kills.GetValueOrDefault(mobKey);

    // ---- string quest registry (RTK registryString): the active minor-quest key, etc. -----------
    internal string QuestStr(string key) => _char.QuestStrings.GetValueOrDefault(key, "");
    internal void   SetQuestStr(string key, string value) { _char.QuestStrings[key] = value; SaveChar(); }

    // ---- legends by internal name (add/replace/remove/query) -------------------------------------
    // A quest owns a legend by its Name key, so it can update or clear its own line without matching text.
    internal bool HasLegend(string name) => _char.Legends.Any(l => l.Name == name);
    internal void RemoveLegend(string name) { if (_char.Legends.RemoveAll(l => l.Name == name) > 0) SaveChar(); }
    internal void AddLegend(string text, string name, byte icon, byte color)
    {
        if (!string.IsNullOrEmpty(name)) _char.Legends.RemoveAll(l => l.Name == name);   // replace-by-name
        _char.Legends.Add(new Legend(icon, color, text, name));
        SaveChar();
    }

    // ---- player facts quests read (level / a stat total / random / wall-clock) -------------------
    internal int  CharLevel => _char.Level;
    /// <summary>A single "power" number quests gate on (RTK's baseMagic*2 + baseHealth analog).</summary>
    internal int  CharStat  => (int)(_char.MaxMp * 2 + _char.MaxHp);
    /// <summary>Subpath mark count (0 — subpath marks aren't modelled yet; keeps min/maxMark gates working).</summary>
    internal int  CharMark  => 0;
    internal int  QuestRandom(int maxInclusive) => Random.Shared.Next(1, Math.Max(1, maxInclusive) + 1);
    internal long NowUnix   => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ===== spells / skills ======================================================================
    // Spellbook wire = RTK 7.x clif_sendmagic, opcode 0x17: slot(u8=idx+1) type(u8) [name u8len+txt]
    // [question u8len+txt]. This is the same "no-op in the main world dispatcher (remap 0x2a), handled by
    // the client's SECONDARY dispatcher" pattern already proven for the item opcodes (0x0F/0x10/0x37/0x38):
    // 0x17 add-spell resolves to 0x2a in remap[0x17-3], exactly like 0x0F/0x10 which work live. The client
    // sorts type 1/2 into the Spell book and type 5 into the Skill book (one 0x17 packet, keyed on type).
    // Casting comes back client->server as 0x0F (clif_parsemagic) -> HandleCast. The 906 spell definitions
    // (name/class/level/type/prompt) come from the RTK Spells table (Content.Spells) — real NexusTK data.

    // The client's spellbook array size is unconfirmed for 4.95; RTK 7.x uses 52 (MAX_SPELLS). Cap
    // conservatively so an over-long teach can't overrun the client array; raise via NEXUS_SPELLBOOK_CAP
    // once a live test confirms the real limit.
    private static readonly int SpellBookCap =
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_SPELLBOOK_CAP"), out var c) && c > 0 ? c : 52;

    // Re-send every learned spell/skill on world entry (the client's book starts empty each login). Slot =
    // list index, matching the 0x0F cast "pos" the client sends back.
    private void RefreshSpells()
    {
        for (int i = 0; i < _char.Spells.Count; i++)
        {
            var sp = Content.SpellById(_char.Spells[i]);
            if (sp is not null) SendAddSpell(i, sp);
        }
    }

    // 0x17 add-spell-to-book: slot(u8=idx+1) type(u8) [name u8len+txt] [question u8len+txt].
    private void SendAddSpell(int slot, SpellDef sp)
    {
        var d = new List<byte> { (byte)(slot + 1), sp.Type };
        var nm = Ascii(sp.Name);     d.Add((byte)nm.Length); d.AddRange(nm);
        var q  = Ascii(sp.Question); d.Add((byte)q.Length);  d.AddRange(q);
        SendMap(0x17, _gameInc++, d.ToArray(), $"addspell(0x17) slot={slot} '{sp.Name}' t{sp.Type}");
    }

    // "!spells" — learn EVERY spell/skill of your class up to your level and populate the book. Class comes
    // from the profile class line (set with !class); level from !lvl. Skips ones already known.
    private void TeachClassSpells()
    {
        int path = Content.PathIdForClass(_char.ClassName);
        if (path < 0)
        {
            SendLog($"'{_char.ClassName}' isn't a known class — set one first, e.g.  !class Mage  (Warrior / Rogue / Mage / Poet / Peasant).");
            return;
        }
        var all = Content.SpellsForClass(path, _char.Level, _char.Alignment);
        if (all.Count == 0) { SendLog($"No spells found for {Content.PathName(path)} ({Character.AlignmentName(_char.Alignment)}) at level {_char.Level}."); return; }

        int learned = 0; bool capped = false;
        foreach (var sp in all)
        {
            if (_char.Spells.Contains(sp.Id)) continue;
            if (_char.Spells.Count >= SpellBookCap) { capped = true; break; }
            _char.Spells.Add(sp.Id);
            SendAddSpell(_char.Spells.Count - 1, sp);
            learned++;
        }
        if (_enteredWorld) _store.Save(_char);
        int spells = all.Count(s => s.IsSpell), skills = all.Count(s => s.IsSkill);
        SendLog($"Learned {learned} new {Content.PathName(path)} ({Character.AlignmentName(_char.Alignment)}) ability(ies) — " +
                $"book now {_char.Spells.Count} (class has {all.Count} ≤ lvl {_char.Level}: {spells} spell / {skills} skill)." +
                (capped ? $"  Hit the {SpellBookCap}-slot cap (raise NEXUS_SPELLBOOK_CAP)." : ""));
        Log.Info($"   -> !spells: {Content.PathName(path)}({path}) align {_char.Alignment} lvl {_char.Level} -> +{learned}, total {_char.Spells.Count}{(capped ? " (CAPPED)" : "")}");
    }

    // "!align <Unaligned|Kwisin|Mingken|Ohaeng | 0-3>" — set the sub-alignment !spells teaches. A character
    // learns only universal spells + this alignment's set, never the other sub-alignments' parallel spells
    // (which share display names and showed up as duplicates). Non-destructive: run !forgetspells + !spells
    // to relearn a clean single-alignment book after changing it.
    private void SetAlignment(string text)
    {
        string a = text.Length > "!align".Length ? text["!align".Length..].Trim() : "";
        if (a.Length == 0)
        {
            SendLog($"alignment is {Character.AlignmentName(_char.Alignment)} ({_char.Alignment}). usage: !align <Unaligned|Kwisin|Mingken|Ohaeng | 0-3>");
            return;
        }
        int val = int.TryParse(a, out var n) && n >= 0 && n < Character.Alignments.Length
            ? n
            : Array.FindIndex(Character.Alignments, s => string.Equals(s, a, StringComparison.OrdinalIgnoreCase));
        if (val < 0) { SendLog($"unknown alignment \"{a}\" — use Unaligned / Kwisin / Mingken / Ohaeng (or 0-3)."); return; }
        _char.Alignment = (byte)val;
        if (_enteredWorld) _store.Save(_char);
        SendLog($"Alignment set to {Character.AlignmentName(_char.Alignment)}. Run  !forgetspells  then  !spells  to relearn a clean {Character.AlignmentName(_char.Alignment)} set.");
        Log.Info($"   -> ALIGN set to {_char.Alignment} ({Character.AlignmentName(_char.Alignment)})");
    }

    // "!learnspell <name|id>" — learn a single spell/skill by fuzzy name or id (any class; handy for testing).
    private void LearnSpellCmd(string text)
    {
        string q = text.Length > "!learnspell".Length ? text["!learnspell".Length..].Trim() : "";
        if (q.Length == 0) { SendLog("usage: !learnspell <name or id>   (or  !spells  to learn all for your class)"); return; }
        var sp = Content.FindSpell(q);
        if (sp is null) { SendLog($"no spell matches \"{q}\"."); return; }
        if (_char.Spells.Contains(sp.Id)) { SendLog($"You already know {sp.Name}."); return; }
        if (_char.Spells.Count >= SpellBookCap) { SendLog($"Spellbook full ({SpellBookCap})."); return; }
        _char.Spells.Add(sp.Id);
        SendAddSpell(_char.Spells.Count - 1, sp);
        if (_enteredWorld) _store.Save(_char);
        SendLog($"Learned {sp.Name} ({(sp.IsSkill ? "skill" : "spell")}, {Content.PathName(sp.PathId)}).");
    }

    // "!forgetspells" — clear the whole book (0x18 remove per slot, then empty the list).
    private void ForgetSpells()
    {
        int n = _char.Spells.Count;
        for (int slot = n - 1; slot >= 0; slot--)
            SendMap(0x18, _gameInc++, new byte[] { (byte)(slot + 1) }, $"removespell(0x18) slot={slot}");
        _char.Spells.Clear();
        if (_enteredWorld) _store.Save(_char);
        SendLog($"Forgot all {n} spell(s).");
    }

    // 0x0F cast (RTK clif_parsemagic): body[0]=book slot+1; then per the learned spell's type: type 1 -> a
    // typed answer string, type 2 -> target entity id (u32BE), type 5 -> nothing. We play the cast animation
    // (0x1A type 6 = magic) for us + peers, spend a little mana, and apply a GENERIC effect: targeted (type 2)
    // spells damage the target world mob with a magic-power hit (reusing the world damage/exp path, so a spell
    // kill rewards exp like a melee kill); self/prompt spells just animate. Per-spell bespoke effects (heals,
    // buffs, teleports, summons) are a follow-up — RTK implements those as ~900 Lua scripts.
    private void HandleCast(byte[] dec)
    {
        if (dec.Length < 1) return;
        int slot = dec[0] - 1;
        if (slot < 0 || slot >= _char.Spells.Count)
        { Log.Info($"   ?? cast slot {slot} out of range ({_char.Spells.Count} known)"); return; }
        var sp = Content.SpellById(_char.Spells[slot]);
        if (sp is null) return;

        // Per spell type, the body carries different args after the slot byte. Type 1 ("Which …?") = a typed
        // answer string — e.g. Gateway's N/E/S/W. RTK's clif_parsemagic (clif.c:8857) strcpy's it straight from
        // the byte after the slot, so it is a NUL-terminated ASCII string (NOT length-prefixed). Type 2 ("Which
        // target?") = a u32BE entity id; if absent the client cast with just the slot (log: `0f 14 00`), so we
        // fall back to the faced tile.
        string? answer = null;
        if (sp.Type == 1 && dec.Length >= 2)
        {
            int end = 1;
            while (end < dec.Length && dec[end] != 0) end++;
            answer = Encoding.ASCII.GetString(dec, 1, end - 1);
        }
        uint? targetId = sp.Type != 1 && dec.Length >= 5
            ? (uint)((dec[1] << 24) | (dec[2] << 16) | (dec[3] << 8) | dec[4]) : null;
        Log.Info($"   -> CAST '{sp.Name}' slot {slot} type {sp.Type} base '{Content.BaseKey(sp)}'" +
                 $"{(answer is null ? "" : $" answer '{answer}'")} by {_char.Name}");

        if (!ApplyCast(sp, targetId, answer)) return;   // couldn't cast (no mana / too weak) — a message was already sent

        // The cast's magic animation (0x1A type 6). Sound is NOT carried here — the client picks an action's sound
        // from a fixed type->sound table (magic/type 6 has none), so the 4th byte is ignored. The spell's sound is
        // sent separately over 0x19 by BroadcastFx (RTK clif_playsound), which the static RE shows IS wired to the
        // audio player. param stays 0.
        SendAction(_char.Id, type: 6, time: 8, param: 0);                                  // cast anim (magic)
        _world.Broadcast(_char.Map, p => p.ActionOver(_char.Id, 6, 8, 0), except: this);   // peers see us cast
        SendStats();                                                                        // push HP/MP to the HUD
    }

    // Apply a spell's effect. Returns false if the cast couldn't happen (a message was already sent). The engine
    // is DATA-DRIVEN: Content.FxFor(sp) supplies the archetype + real RTK formulas (extracted from the Lua by
    // re/extract_spell_formulas.py). We dispatch on the archetype — Damage/Heal evaluate the actual per-spell
    // formula, Buff applies a timed stat mod, Debuff freezes a mob, ManaBattery trades HP↔MP, Cure clears our
    // debuffs; Utility/Summon/Teleport/Dialog degrade to "spend mana + acknowledge". A spell with no export row
    // falls back to the keyword classifier (ApplyCastGeneric).
    private bool ApplyCast(SpellDef sp, uint? targetId, string? answer = null)
    {
        // Gateway (common/gateway.lua): a teleport with its own bespoke logic — warp to a gate of the caster's
        // kingdom picked by the N/E/S/W answer. Intercept before the fx dispatch (a teleport has no damage/heal
        // archetype and would otherwise degrade to CastMisc's "spend mana + acknowledge" no-op).
        if (Content.BaseKey(sp) == "gateway") return CastGateway(sp, answer);

        var fx = Content.FxFor(sp);
        string arch = fx?.Archetype ?? "";

        // Mana-battery family (Invoke / Spirit's Power / …) always runs the verbatim RTK formula, whether the
        // export tagged it ManaBattery or we recognise its base identifier (belt-and-suspenders for export gaps).
        if (arch == "ManaBattery" || Content.BaseKey(sp) is "invoke" or "spirits_power" or "life_force" or "gather_magic")
            return CastManaBattery(sp);

        if (fx is null) return ApplyCastGeneric(sp, targetId);   // no export row — keyword classifier fallback

        // Cooldown (RTK "aether"), if this spell has one and it's still ticking.
        if (fx.Aether > 0 && OnCooldown(sp.Key, out int wait))
        { SendMessage($"{sp.Name} isn't ready yet ({wait}s)."); return false; }

        int mana = fx.Mana > 0 ? fx.Mana : 5;   // real per-spell cost; 5 only if the export had none
        bool ok = arch switch
        {
            "Damage" => CastDamage(sp, fx, targetId, mana),
            "Heal"   => CastHeal(sp, fx, mana),
            "Buff"   => CastBuff(sp, fx, mana),
            "Debuff" => CastDebuff(sp, fx, targetId, mana),
            "Cure"   => CastCure(sp, fx, mana),
            _        => CastMisc(sp, mana),   // Utility / Summon / Teleport / Dialog — graceful
        };
        if (ok && fx.Aether > 0) SetCooldown(sp.Key, fx.Aether);
        return ok;
    }

    // The stat variables an RTK spell formula reads (player.level, player.will, target.baseHealth, …). Effective
    // (base + gear + buff) values, so a buffed caster hits harder. enchant/rage/fury default to 1 (we don't model
    // those multipliers yet). Passed to Formula.Eval.
    private Dictionary<string, double> SpellVars(Mob? target)
    {
        var t = Totals();
        return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["player.level"]      = _char.Level,
            ["player.will"]       = _char.Will + t.will,
            ["player.grace"]      = _char.Grace + t.grace,
            ["player.might"]      = EffMight,
            ["player.magic"]      = _char.Mp,
            ["player.maxMagic"]   = EffMaxMp,
            ["player.health"]     = _char.Hp,
            ["player.maxHealth"]  = EffMaxHp,
            ["player.enchant"]    = 1, ["player.rage"] = 1, ["player.fury"] = 1, ["player.invis"] = 1,
            ["target.health"]     = target?.Hp ?? 0,
            ["target.baseHealth"] = target?.MaxHp ?? 0,
        };
    }

    // Damage: evaluate the spell's real RTK damage formula, spend mana, apply to the faced/targeted mob.
    private bool CastDamage(SpellDef sp, SpellFx fx, uint? targetId, int mana)
    {
        if (_char.Mp < (uint)mana) { SendMessage("You do not have enough mana."); return false; }
        var mob = ResolveCastTarget(targetId);
        if (mob is null) { SendMessage($"{sp.Name} finds no target."); return false; }

        int power = Math.Max(1, (int)Math.Round(Formula.Eval(fx.AmountExpr, SpellVars(mob))));
        _char.Mp -= (uint)mana;
        if (_world.TryDamage(_char.Map, mob, power, out bool died))
        {
            BroadcastFx(mob.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));   // graphic + sound
            ShowDamageResult(mob.Id, mob, died);   // 0x13: over-head HP bar (empty bar + delayed despawn on death)
            if (died)
            {
                uint reward = (uint)(mob.Exp > 0 ? mob.Exp : mob.MaxHp);
                _char.Exp += reward;
                SendMessage($"Your {sp.Name} destroys {mob.Name}! (+{reward} exp)");
            }
            else SendMessage($"Your {sp.Name} hits {mob.Name} for {power}.");
            Log.Info($"      {sp.Name} -> mob {mob.Id} '{mob.Name}' for {power} (died={died})");
        }
        return true;
    }

    // Heal: evaluate the spell's real heal amount and restore the caster's HP (RTK heals a target; solo, that's
    // us). A pure heal at full HP still "works" (mana spent) — matches casting a heal you don't strictly need.
    private bool CastHeal(SpellDef sp, SpellFx fx, int mana)
    {
        if (_char.Mp < (uint)mana) { SendMessage("You do not have enough mana."); return false; }
        int amount = Math.Max(0, (int)Math.Round(Formula.Eval(fx.AmountExpr, SpellVars(null))));
        _char.Mp -= (uint)mana;
        uint before = _char.Hp;
        _char.Hp = Math.Min(EffMaxHp, _char.Hp + (uint)amount);
        uint gain = _char.Hp - before;
        BroadcastFx(_char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));   // heal sparkle + sound
        SendMessage(gain > 0 ? $"{sp.Name} restores {gain} HP." : $"You cast {sp.Name} (already at full HP).");
        return true;
    }

    // Buff: apply the spell's timed stat modifier(s) (might/hit/dam/…) for its RTK duration, folded live into the
    // HUD/melee via Totals(). Re-casting refreshes rather than stacks (matches RTK removeDuras-then-setDuration).
    // Buffs whose stat delta the export couldn't pin down still "cast" (mana + duration marker) so they're not
    // silently dead.
    private bool CastBuff(SpellDef sp, SpellFx fx, int mana)
    {
        if (_char.Mp < (uint)mana) { SendMessage("You do not have enough mana."); return false; }
        _char.Mp -= (uint)mana;

        int durMs = fx.DurationMs > 0 ? fx.DurationMs : 60000;
        long expires = Environment.TickCount64 + durMs;
        _buffs.RemoveAll(b => b.Key == sp.Key);   // refresh, don't stack

        var stats = fx.BuffStat.Split('|', StringSplitOptions.RemoveEmptyEntries);
        var amts  = fx.BuffAmt.Split('|', StringSplitOptions.RemoveEmptyEntries);
        int applied = 0;
        for (int i = 0; i < stats.Length && i < amts.Length; i++)
            if (int.TryParse(amts[i].Split('.')[0], out var amt) && amt != 0)
            { _buffs.Add(new ActiveBuff { Stat = stats[i], Amount = amt, Expires = expires, Key = sp.Key, Name = sp.Name }); applied++; }

        SendStats();   // reflect the boosted caps/attributes on the HUD immediately
        BroadcastFx(_char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));   // buff aura + sound
        SendMessage(applied > 0
            ? $"You cast {sp.Name} — you feel its power ({durMs / 1000}s)."
            : $"You cast {sp.Name}.");
        return true;
    }

    // Debuff: paralyze/sleep the targeted mob — freeze its wandering for the RTK duration, subject to the spell's
    // hit chance. PC targets are immune here (no PvP). Damage-less crowd control.
    private bool CastDebuff(SpellDef sp, SpellFx fx, uint? targetId, int mana)
    {
        if (_char.Mp < (uint)mana) { SendMessage("You do not have enough mana."); return false; }
        var mob = ResolveCastTarget(targetId);
        if (mob is null) { SendMessage($"{sp.Name} finds no target."); return false; }

        double chance = string.IsNullOrEmpty(fx.Chance) ? 100 : Formula.Eval(fx.Chance, SpellVars(mob));
        _char.Mp -= (uint)mana;
        if (chance < 100 && Random.Shared.Next(100) >= chance) { SendMessage($"{sp.Name} fails to take hold."); return true; }

        int durMs = fx.DurationMs > 0 ? fx.DurationMs : 20000;
        mob.FrozenUntil = Environment.TickCount64 + durMs;
        BroadcastFx(mob.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));   // debuff graphic + sound
        SendMessage($"Your {sp.Name} holds {mob.Name} for {durMs / 1000}s.");
        Log.Info($"      {sp.Name} -> mob {mob.Id} '{mob.Name}' frozen {durMs}ms ({fx.Debuff})");
        return true;
    }

    // Cure: RTK removes a category of durations from the target. We clear the caster's own active debuffs/buffs
    // (we don't yet carry negative status), so functionally it's a "dispel my timers" + mana spend.
    private bool CastCure(SpellDef sp, SpellFx fx, int mana)
    {
        if (_char.Mp < (uint)mana) { SendMessage("You do not have enough mana."); return false; }
        _char.Mp -= (uint)mana;
        SendMessage($"You cast {sp.Name}.");
        return true;
    }

    // Utility / Summon / Teleport / Dialog: no faithful model yet — spend the real mana and acknowledge so the
    // cast isn't a silent no-op. These get bespoke cases later as they're wanted.
    private bool CastMisc(SpellDef sp, int mana)
    {
        if (_char.Mp < (uint)mana) { SendMessage($"Not enough mana to cast {sp.Name}."); return false; }
        _char.Mp -= (uint)mana;
        SendMessage($"You cast {sp.Name}.");
        return true;
    }

    // Gateway destination table (ported verbatim from Accepted/Spells/common/gateway.lua): region -> the
    // kingdom's city map + the four gate spawn boxes. Casting Gateway warps you to a RANDOM tile inside the
    // box for the gate you answered (N/E/S/W), on the region's city map — regardless of which sub-map of the
    // kingdom you cast from. Coords are 1:1 with RTK; only the four playable kingdoms (regions 0-3) have gates.
    private static readonly Dictionary<int, (ushort map, string city,
        Dictionary<char, (int xlo, int xhi, int ylo, int yhi)> gates)> GatewayRegions = new()
    {
        [0] = (0,    "Kugnae",  new() { ['n'] = (104, 116, 13, 17),  ['e'] = (201, 208, 105, 111), ['w'] = (14, 19, 104, 111),  ['s'] = (104, 115, 207, 211) }),
        [1] = (330,  "Buya",    new() { ['n'] = (71, 75, 22, 27),    ['e'] = (132, 136, 86, 90),   ['w'] = (8, 12, 88, 92),     ['s'] = (74, 78, 140, 145) }),
        [2] = (41,   "Mythic",  new() { ['n'] = (27, 36, 10, 15),    ['e'] = (54, 57, 28, 33),     ['w'] = (3, 5, 28, 33),      ['s'] = (25, 33, 48, 53) }),
        [3] = (2500, "Nagnang", new() { ['n'] = (37, 39, 23, 25),    ['e'] = (138, 140, 86, 88),   ['w'] = (4, 6, 92, 94),      ['s'] = (75, 77, 151, 153) }),
    };

    // Gateway: teleport to a gate of the caster's kingdom. The N/E/S/W answer to the spell's question picks the
    // gate; the region (Content.RegionOf) picks the city. Faithful to gateway.lua incl. its guards (dead can't
    // cast, warp-locked maps say "It doesn't work here", non-kingdom maps "Cannot find any gates!") and the
    // per-gate random landing spread. No mana cost — RTK's gateway only calls canCast (a state check), not a
    // mana debit. On success we re-run the full map-entry sequence via EnterMap so the client redraws.
    private bool CastGateway(SpellDef sp, string? answer)
    {
        if (_char.Hp == 0) { SendMessage("Spirits cannot use Gateway."); return false; }
        if (!Content.WarpOut(_char.Map)) { SendMessage("It doesn't work here."); return false; }

        int region = Content.RegionOf(_char.Map);
        if (!GatewayRegions.TryGetValue(region, out var r) || !Content.Maps.TryGetValue(r.map, out var map))
        { SendMessage("Cannot find any gates!"); return false; }

        // RTK keys on the answer's first letter (string.sub(q,1,1)). Take the first ASCII letter so a stray
        // framing byte or leading space can't swallow the direction.
        char dir = (answer ?? "").ToLowerInvariant().FirstOrDefault(char.IsLetter);
        if (!r.gates.TryGetValue(dir, out var box))
        { SendMessage("Which gate? Answer North, East, South or West."); return false; }

        ushort x = (ushort)Random.Shared.Next(box.xlo, box.xhi + 1);
        ushort y = (ushort)Random.Shared.Next(box.ylo, box.yhi + 1);
        string gate = dir switch { 'n' => "North", 'e' => "East", 'w' => "West", 's' => "South", _ => "" };

        EnterMap(map.Id, map.Xs, map.Ys, x, y, map.Name);
        SendMessage($"You have arrived at {gate} Gate of {r.city}.");
        Log.Info($"      Gateway -> region {region} {r.city} {gate} gate: map {map.Id} ({x},{y})");
        return true;
    }

    // Fallback for spells with NO export row: the old keyword classifier over the name (Damage/Heal/Buff/Utility)
    // with the shared magic-damage base formula. Kept so an unmatched identifier still does something sensible.
    private bool ApplyCastGeneric(SpellDef sp, uint? targetId)
    {
        const uint cost = 5;
        if (_char.Mp < cost) { SendMessage($"Not enough mana to cast {sp.Name}."); return false; }
        var eq = Totals();
        int power = Math.Max(1, 1 + (_char.Will + eq.will) * 4 + (_char.Grace + eq.grace) * 3);
        switch (Content.EffectOf(sp))
        {
            case Content.SpellEffect.Heal:
            {
                _char.Mp -= cost;
                uint before = _char.Hp;
                _char.Hp = Math.Min(EffMaxHp, _char.Hp + (uint)power);
                uint gain = _char.Hp - before;
                BroadcastFx(_char.Id, 5, 4);   // generic unaligned heal graphic + sound
                SendMessage(gain > 0 ? $"{sp.Name} restores {gain} HP." : $"You cast {sp.Name} (already at full HP).");
                return true;
            }
            case Content.SpellEffect.Damage:
            {
                var mob = ResolveCastTarget(targetId);
                if (mob is null) { SendMessage($"{sp.Name} finds no target."); return false; }
                _char.Mp -= cost;
                if (_world.TryDamage(_char.Map, mob, power, out bool died))
                {
                    BroadcastFx(mob.Id, 4, 56);   // generic unaligned zap graphic + sound
                    ShowDamageResult(mob.Id, mob, died);   // 0x13: over-head HP bar (empty bar + delayed despawn on death)
                    if (died)
                    {
                        uint reward = (uint)(mob.Exp > 0 ? mob.Exp : mob.MaxHp);
                        _char.Exp += reward;
                        SendMessage($"Your {sp.Name} destroys {mob.Name}! (+{reward} exp)");
                    }
                    else SendMessage($"Your {sp.Name} hits {mob.Name} for {power}.");
                }
                return true;
            }
            case Content.SpellEffect.Buff:
                _char.Mp -= cost;
                SendMessage($"You invoke {sp.Name} — you feel its power.");
                return true;
            default:
                _char.Mp -= cost;
                SendMessage($"You cast {sp.Name}.");
                return true;
        }
    }

    // Per-spell cooldowns ("aether"), keyed by spell identifier -> earliest next-cast tick (ms). Mirrors RTK's
    // player:setAether. Lightweight and session-local (resets on relog, like most timers here).
    private readonly Dictionary<string, long> _aether = new();
    private bool OnCooldown(string key, out int secondsLeft)
    {
        secondsLeft = 0;
        if (_aether.TryGetValue(key, out var until) && Environment.TickCount64 < until)
        { secondsLeft = (int)Math.Ceiling((until - Environment.TickCount64) / 1000.0); return true; }
        return false;
    }
    private void SetCooldown(string key, int ms) => _aether[key] = Environment.TickCount64 + ms;

    // Mana-battery family (Invoke / Spirit's Power / Life Force / Gather Magic). Ported verbatim from RTK
    // rtklua/Accepted/Spells/{mage,poet}/invoke.lua: needs ≥30 current mana; HP cost = 40% of MAX mana, with
    // HP floored at 100 (never below); then refill mana to FULL. 22s aether (cooldown).
    private bool CastManaBattery(SpellDef sp)
    {
        const uint MinMana = 30;
        if (OnCooldown(sp.Key, out int wait)) { SendMessage($"{sp.Name} isn't ready yet ({wait}s)."); return false; }
        if (_char.Mp < MinMana) { SendMessage("Not enough mana."); return false; }

        uint healthCost = (uint)(EffMaxMp * 0.4);
        uint before = _char.Hp;
        _char.Hp = (long)_char.Hp - healthCost < 100 ? 100u : _char.Hp - healthCost;   // RTK: floor at 100
        _char.Mp = EffMaxMp;                                                            // refill to full
        SetCooldown(sp.Key, 22000);

        uint lost = before > _char.Hp ? before - _char.Hp : 0;
        if (Content.FxFor(sp) is { } fx)
            BroadcastFx(_char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));   // Invoke graphic + sound
        SendMessage($"You cast {sp.Name}.");
        Log.Info($"      {sp.Name}: -{lost} HP (cost {healthCost}, floor 100), MP -> full {_char.Mp}/{EffMaxMp}");
        return true;
    }

    // The world mob a cast should affect: an explicit target id if the client sent one, else the mob on the
    // tile the caster faces (like melee). World mobs only — session-local debug dummies aren't spell targets.
    private Mob? ResolveCastTarget(uint? targetId)
    {
        if (targetId is uint id && id != 0)
        {
            var byId = _world.MobById(_char.Map, id);
            if (byId is not null) return byId;
        }
        var (fx, fy) = FrontTile();
        return _world.MobAt(_char.Map, fx, fy);
    }

    // 0x0F add-item-to-slot: slot(u8=idx+1) icon(u16) iconColor(u8) [dispName u8len+txt] [baseName u8len+txt]
    //   amount(u32) [block: stack/0(u8) dura(u32) protected(u8)] [owner u8len+txt] 00 00 00.
    private void SendAddItem(InvItem it)
    {
        var def = Content.ItemById(it.ItemId);
        if (def is null) return;
        string name = string.IsNullOrEmpty(it.CustomName) ? def.Name : it.CustomName;
        string disp = it.Amount > 1 ? $"{name} ({it.Amount})" : name;

        var d = new List<byte> { (byte)(it.Slot + 1) };
        d.AddRange(Be(IconWire(def.Icon)));   // RTK ItmIcon == client Item.epf frame; encode for the +0x4000 resolver
        // 5.x (V533) carries an icon-color byte here; 4.95 (V495) does NOT — it reads the name length
        // right after the icon. Proven live: on 4.95 an extra byte here made the client read the name
        // one byte early (Apple iconColor=0 → empty name "You ate ."; Poison apple iconColor=12 → 12-char
        // garbled "⊥Poison appl"). See docs §11c.
        if (_ver == ClientVersion.V533) d.Add(def.IconColor);
        var dn = Ascii(disp); d.Add((byte)dn.Length); d.AddRange(dn);
        var bn = Ascii(def.Name); d.Add((byte)bn.Length); d.AddRange(bn);
        d.AddRange(Be32((uint)it.Amount));
        if (def.IsEquip) { d.Add(0); d.AddRange(Be32(it.Dura)); d.Add(0); }
        else { d.Add((byte)(def.Stackable ? 1 : 0)); d.AddRange(Be32(0)); d.Add(0); }
        d.Add(0);                 // owner name length (0 = unowned)
        d.AddRange(Be(0));        // trailing u16
        d.Add(0);                 // trailing u8
        SendMap(0x0F, _gameInc++, d.ToArray(), $"additem(0x0F) slot={it.Slot} '{name}' x{it.Amount}");
    }

    // 0x10 remove-from-slot: slot(u8=idx+1) reason(u8) 00 00. reason: 0=Remove 1=Drop 2=Eat 4=Throw 6=Used …
    private void SendDelItem(byte slot, byte reason) =>
        SendMap(0x10, _gameInc++, new byte[] { (byte)(slot + 1), reason, 0, 0 }, $"delitem(0x10) slot={slot} r={reason}");

    // 0x37 equip-window: equipType(u8) icon(u16) iconColor(u8) [name u8len+txt] [baseName u8len+txt] dura(u32) 00 00.
    private void SendEquip(InvItem worn)
    {
        var def = Content.ItemById(worn.ItemId);
        if (def is null) return;
        string name = string.IsNullOrEmpty(worn.CustomName) ? def.Name : worn.CustomName;
        var d = new List<byte> { worn.Slot };     // worn.Slot holds the wire equip-slot byte
        d.AddRange(Be(IconWire(def.Icon)));        // +0x4000 resolver encoding (see SendAddItem / IconWire)
        if (_ver == ClientVersion.V533) d.Add(def.IconColor);   // 4.95 omits the icon-color byte (see SendAddItem)
        var nn = Ascii(name); d.Add((byte)nn.Length); d.AddRange(nn);
        var bn = Ascii(def.Name); d.Add((byte)bn.Length); d.AddRange(bn);
        d.AddRange(Be32(worn.Dura));
        d.AddRange(Be(0));
        SendMap(0x37, _gameInc++, d.ToArray(), $"equip(0x37) slot={worn.Slot} '{name}'");
    }

    // The profile-screen equipment ICON cells (helm + two rings). 4.95 has no character-sprite layer for these
    // slots, so both profile views (0x39 self, 0x34 other) show them as ground-icon boxes fed by three u16
    // fields. Encoded with IconWire, exactly like the 0x37 equip window (the old bug proved these boxes render
    // an IconWire value — it wrongly showed the weapon there). Client wire slots (from 0x1F captures): helm=4,
    // left ring=7, right ring=8. Returns 0 (empty box) when nothing is worn in that slot.
    private ushort ProfileCellIcon(byte wireSlot)
    {
        var worn = _char.Equipment.FirstOrDefault(e => e.Slot == wireSlot);
        var def = worn is null ? null : Content.ItemById(worn.ItemId);
        return def is null ? (ushort)0 : IconWire(def.Icon);
    }

    // 0x38 unequip-window: spot(u8) 00.
    private void SendUnequip(byte wireSlot) =>
        SendMap(0x38, _gameInc++, new byte[] { wireSlot, 0 }, $"unequip(0x38) slot={wireSlot}");

    /// <summary>Draw a floor item AT REST via the 0x07 static-object path (NOT 0x16). Full RE (2026-07-24):
    /// 0x16 builds a WALK projectile (vtable 0x4cd18c, tick 0x463270) that interpolates in then drops off the
    /// moving-list / self-destructs on arrival -> invisible at rest (that was the bug). The 0x07 handler
    /// (0x44fdb0 @ 0x44fe7f) routes any look OUTSIDE 0x8000..0xbfff to descriptor type 2 = the BASE object
    /// (vtable 0x4cd118, tick 0x4601a0 = `xor al,al;ret` no-op) built by 0x462ec0 alone: it never moves, never
    /// self-destructs, and is drawn by the shared render loop exactly like a monster but stationary. IconWire
    /// frames (0..1310) map to 0xc000..0xc51e, all > 0xbfff, so they hit type 2 and resolve (look+0x4000)&0xffff
    /// against Item.epf -- the SAME resolver the bag/0x0F path uses. Caveat: 0x07 has a viewport gate (0x424310),
    /// so the tile must be on-screen when spawned (true for drop/throw at the player's feet).</summary>
    public void ShowGroundItem(GroundItem gi) =>
        SendCreatureList(new[] { (gi.Id, IconWire(gi.Graphic), gi.X, gi.Y, (byte)0, (byte)0) });

    // The 4.95 type-0 form has three gear-driven look bytes: weapon [5], armor [3] and shield [6]. Weapon/
    // shield are derived live from Equipment by WeaponLook()/ShieldLook() (0xFF = bare), so equipping any of
    // the three must re-draw self + peers; only armor still needs its cached _char.Armor byte written here.
    private void ApplyAppearance(ItemDef def, bool equip)
    {
        if (def.Type == 4) _char.Armor = equip ? (byte)def.Look : (byte)0;        // ITM_ARMOR (cached in [3])
        else if (def.Type == 3) _char.Weapon = equip ? (byte)def.Look : (byte)0;  // ITM_WEAP (kept for combat/GM)
        else if (def.Type != 5) return;                                           // not weapon/armor/shield -> no look change
        SendSelfLook();
        _world.Broadcast(_char.Map, p => p.ShowPlayer(this), except: this);
    }

    // ---- recv handlers (client -> server) ----

    // 0x07 pick up: grab whatever floor item sits on my tile; coins (sentinel ItemId<0) go to the purse.
    // The client sends pickuptype at body[0] (RTK clif_parsegetitem: RFIFOB(fd,5)): ',' = 0 (grab the top
    // item), '<'/Shift+, = 1 (grab EVERYTHING stacked on the tile). Either way, play the bend-down action
    // first — type 4, time 40; the crouch sprite carries the pickup sound — on self AND peers, even when the
    // tile is empty (matches RTK, which sends the action before it looks at the floor).
    private void HandlePickup(byte[] dec)
    {
        bool pickAll = dec.Length > 0 && dec[0] != 0;
        SendAction(_char.Id, 4, 40, 0);                                                     // our crouch + sound
        _world.Broadcast(_char.Map, p => p.ActionOver(_char.Id, 4, 40, 0), except: this);   // peers see it too

        do
        {
            var gi = _world.PickUp(_char.Map, _char.X, _char.Y);
            if (gi is null) return;                       // tile empty (or now cleared)
            if (gi.ItemId < 0) { _char.Coins += (uint)gi.Amount; SendStats(); continue; }   // coins -> purse
            var def = Content.ItemById(gi.ItemId);
            if (def is null) continue;
            if (!GiveItem(def, gi.Amount, gi.Dura, gi.CustomName))
            {
                // pack full — put it straight back on the floor so it isn't lost, and stop grabbing.
                _world.DropItem(_char.Map, new GroundItem { Id = _world.AllocateItemId(), ItemId = gi.ItemId,
                    X = _char.X, Y = _char.Y, Amount = gi.Amount, Dura = gi.Dura, Graphic = gi.Graphic, CustomName = gi.CustomName });
                return;
            }
        } while (pickAll);                                // ',' runs once; '<' loops until the tile is empty
    }

    // 0x08 drop: dec[0]=slot(1-based). Drop the whole stack onto my tile.
    private void HandleDropItem(byte[] dec)
    {
        if (dec.Length < 1) return;
        int slot = dec[0] - 1;
        // dec[1] = the "all" flag: 'd' (drop one) sends 0, 'D'/Shift+d (drop whole stack) sends 1.
        // Confirmed live: client emits `08 <slot+1> 00 00` for d and `08 <slot+1> 01 00` for D.
        bool dropAll = dec.Length > 1 && dec[1] != 0;
        var it = InvAt(slot); if (it is null) return;
        var def = Content.ItemById(it.ItemId); if (def is null) return;
        if (def.NoDrop) { SendLog($"You can't drop {def.Name}."); return; }

        // Bend-down drop animation + sound (RTK clif_parsedropitem: type 5, time 20 — a distinct pose from
        // pickup's type 4). Fired only once the drop is allowed, on self AND peers, before the item leaves the bag.
        SendAction(_char.Id, 5, 20, 0);                                                     // our drop crouch + sound
        _world.Broadcast(_char.Map, p => p.ActionOver(_char.Id, 5, 20, 0), except: this);   // peers see it too

        int count = dropAll ? it.Amount : 1;
        int remaining = it.Amount - count;
        if (remaining <= 0) { _char.Inventory.Remove(it); SendDelItem((byte)slot, 1); }  // reason 1 = Drop
        else { it.Amount = remaining; SendAddItem(it); }   // stack shrinks: redraw the slot with the new count
        _world.DropItem(_char.Map, new GroundItem { Id = _world.AllocateItemId(), ItemId = def.Id,
            X = _char.X, Y = _char.Y, Amount = count, Dura = it.Dura, Graphic = def.Icon, CustomName = it.CustomName });
    }

    // 0x17 throw: dec[0]=confirm, dec[1]=slot(1-based). Throw one, land it a few tiles ahead.
    private void HandleThrow(byte[] dec)
    {
        if (dec.Length < 2) return;
        int slot = dec[1] - 1;
        var it = InvAt(slot); if (it is null) return;
        var def = Content.ItemById(it.ItemId); if (def is null) return;
        SendAction(_char.Id, 2, 20, 0);                                                    // throw animation (self)
        _world.Broadcast(_char.Map, p => p.ActionOver(_char.Id, 2, 20, 0), except: this);   // peers see the throw too
        it.Amount -= 1;
        if (it.Amount <= 0) { _char.Inventory.Remove(it); SendDelItem((byte)slot, 4); }  // reason 4 = Throw
        else SendAddItem(it);
        // Fly up to 3 tiles in the facing direction, but STOP at the last passable tile — a thrown item
        // must not land past a wall/off the map into an unreachable spot. Step tile-by-tile and halt before
        // the first blocked/off-map cell (same collision the player walk uses). If the tile directly ahead is
        // solid, the item just lands on the thrower's own tile.
        int tx = _char.X, ty = _char.Y, dx = 0, dy = 0;
        switch (_facing & 3) { case 0: dy = -1; break; case 1: dx = 1; break; case 2: dy = 1; break; case 3: dx = -1; break; }
        var tmap = MapData.For(_char.Map, _char.MapXs, _char.MapYs);
        for (int step = 0; step < 3; step++)
        {
            int cx = tx + dx, cy = ty + dy;
            if (cx < 0 || cy < 0 || cx >= _char.MapXs || cy >= _char.MapYs) break;   // off the tile grid
            if (PassEnforce && tmap != null && Blocked(tmap, cx, cy)) break;         // wall/object -> stop short
            tx = cx; ty = cy;
        }
        _world.DropItem(_char.Map, new GroundItem { Id = _world.AllocateItemId(), ItemId = def.Id,
            X = (ushort)tx, Y = (ushort)ty, Amount = 1, Dura = it.Dura, Graphic = def.Icon, CustomName = it.CustomName });
    }

    // 0x1C use / 0x1A eat: dec[0]=slot(1-based). Equipment -> wear it; consumable -> consume (+ any heal).
    private void HandleUseItem(byte[] dec, bool eat)
    {
        if (dec.Length < 1) return;
        int slot = dec[0] - 1;
        var it = InvAt(slot); if (it is null) return;
        var def = Content.ItemById(it.ItemId); if (def is null) return;
        if (def.IsEquip) { if (eat) { SendLog($"You can't eat {def.Name}."); return; } EquipFromSlot(slot); return; }
        if (eat && def.Type != 0) { SendLog("That is not edible."); return; }   // ITM_EAT only
        // Play the eat animation on self + peers (0x1A action type 8 = eat on 4.95; the client shows its
        // own "You ate <name>" status line from the slot name, so DON'T also speak it as 0x0D chat).
        SendAction(_char.Id, 8, 40, 0);
        _world.Broadcast(_char.Map, p => p.ActionOver(_char.Id, 8, 40, 0), except: this);
        bool healed = false;
        if (def.Vita > 0) { _char.Hp = Math.Min(EffMaxHp, _char.Hp + (uint)def.Vita); healed = true; }
        if (def.Mana > 0) { _char.Mp = Math.Min(EffMaxMp, _char.Mp + (uint)def.Mana); healed = true; }
        it.Amount -= 1;
        if (it.Amount <= 0) { _char.Inventory.Remove(it); SendDelItem((byte)slot, (byte)(eat ? 2 : 6)); }
        else SendAddItem(it);
        if (healed) SendStats();
    }

    // Sum of every stat line across all worn gear. Equipment NEVER writes back into the character's base
    // stats (those stay in _char.*); the effective values the client sees are base + these, recomputed on
    // every SendStats / profile / attack. That keeps a relog — which reloads Equipment and redraws it via
    // RefreshInventory — from drifting or double-counting, since nothing was ever baked into the base.
    private (int hp, int mp, int might, int will, int grace, int armor, int hit, int dam) EquipTotals()
    {
        int hp = 0, mp = 0, mt = 0, wl = 0, gr = 0, ar = 0, ht = 0, dm = 0;
        foreach (var e in _char.Equipment)
        {
            var def = Content.ItemById(e.ItemId); if (def is null) continue;
            hp += def.Vita; mp += def.Mana; mt += def.Might; wl += def.Will; gr += def.Grace;
            ar += def.Armor; ht += def.Hit; dm += def.Dam;
        }
        return (hp, mp, mt, wl, gr, ar, ht, dm);
    }

    // Active timed stat buffs (from casting Buff spells). Session-local, like cooldowns — they clear on relog.
    // Each carries the stat it boosts, the amount, and the tick it expires at. Expired ones are pruned on read.
    private sealed class ActiveBuff { public string Stat = ""; public int Amount; public long Expires; public string Key = ""; public string Name = ""; }
    private readonly List<ActiveBuff> _buffs = new();

    private (int hp, int mp, int might, int will, int grace, int armor, int hit, int dam) BuffTotals()
    {
        long now = Environment.TickCount64;
        _buffs.RemoveAll(b => b.Expires <= now);
        int hp = 0, mp = 0, mt = 0, wl = 0, gr = 0, ar = 0, ht = 0, dm = 0;
        foreach (var b in _buffs) switch (b.Stat)
        {
            case "hp": case "maxhp": hp += b.Amount; break;
            case "mp": case "maxmp": mp += b.Amount; break;
            case "might": mt += b.Amount; break;
            case "will":  wl += b.Amount; break;
            case "grace": gr += b.Amount; break;
            case "armor": ar += b.Amount; break;
            case "hit":   ht += b.Amount; break;
            case "dam":   dm += b.Amount; break;
        }
        return (hp, mp, mt, wl, gr, ar, ht, dm);
    }

    // Gear + active timed buffs: the full bonus layered on the character's base stats. Everything the client
    // sees (HUD, profile) and every derived calc (heals, melee) reads through this so buffs are reflected live.
    private (int hp, int mp, int might, int will, int grace, int armor, int hit, int dam) Totals()
    {
        var e = EquipTotals(); var b = BuffTotals();
        return (e.hp + b.hp, e.mp + b.mp, e.might + b.might, e.will + b.will,
                e.grace + b.grace, e.armor + b.armor, e.hit + b.hit, e.dam + b.dam);
    }

    // Effective (base + gear + buffs) caps/attributes used by the HUD, heals and melee. AC is signed and LOWER
    // is better in TK, so armor SUBTRACTS from it.
    private uint EffMaxHp => (uint)Math.Max(1, (int)_char.MaxHp + Totals().hp);
    private uint EffMaxMp => (uint)Math.Max(0, (int)_char.MaxMp + Totals().mp);
    private int  EffMight => Math.Clamp(_char.Might + Totals().might, 0, 255);

    // Move a bag item onto the body: bumps any item already in that gear slot back to the bag first.
    private void EquipFromSlot(int slot)
    {
        var it = InvAt(slot); if (it is null) return;
        var def = Content.ItemById(it.ItemId); if (def is null || !def.IsEquip) return;
        // Wear requirements (RTK item_data): sex-locked gear, a minimum level, and a minimum MIGHT (checked
        // against effective might so already-worn +might gear counts). Path/class restriction (ItmPthId) is
        // parsed by the client too, but this bring-up character has no path id yet, so it isn't enforced.
        // ItmSex: 0 = male-only, 1 = female-only, 2 = UNISEX (the common case — 1944/2545 items, incl. most
        // weapons). Character.Sex uses the same 0=M/1=F encoding, so a sex-locked item (0 or 1) must match;
        // anything >= 2 is unrestricted. (The old `!= 0` test wrongly blocked every unisex item.)
        if (def.Sex < 2 && def.Sex != _char.Sex) { SendLog($"You can't wear {def.Name}."); return; }
        if (def.Level > _char.Level) { SendLog($"You must be level {def.Level} to wear {def.Name}."); return; }
        if (def.MightReq > EffMight) { SendLog($"You need {def.MightReq} might to wear {def.Name}."); return; }
        byte wire = def.EquipSlot;
        // Rings/gauntlets are all Type 7 (wire slot 7 = left ring) but share TWO interchangeable slots — 7 and
        // 8 (right ring). Wear the second one in the free right slot instead of replacing the left. Only when
        // BOTH are taken does a new ring replace the left. (Slot 8 carries no items in the data, so it's only
        // ever filled by this path.)
        if (wire == 7 && _char.Equipment.Any(e => e.Slot == 7) && _char.Equipment.All(e => e.Slot != 8))
            wire = 8;

        _char.Inventory.Remove(it);
        SendDelItem((byte)slot, 0);                   // reason 0 = Remove (moved to gear)

        var prev = _char.Equipment.FirstOrDefault(e => e.Slot == wire);
        if (prev is not null)
        {
            _char.Equipment.Remove(prev);
            SendUnequip(wire);
            var pdef = Content.ItemById(prev.ItemId);
            if (pdef is not null) { ApplyAppearance(pdef, equip: false); GiveItem(pdef, 1, prev.Dura, prev.CustomName); }
        }

        var worn = new InvItem(wire, def.Id, 1, it.Dura == 0 ? def.Durability : it.Dura) { CustomName = it.CustomName };
        _char.Equipment.Add(worn);
        SendEquip(worn);
        ApplyAppearance(def, equip: true);
        SendStats();                                  // push the new gear bonuses to the HUD
        // (No "Equipped X" over-head bubble — the paperdoll + gear stats are feedback enough; SendLog here
        // spoke it as 0x0D chat over the character, which the player didn't want.)
    }

    // 0x1F unequip: dec[0]=wire equip-slot byte. Take the worn item off and return it to the bag.
    private void HandleUnequip(byte[] dec)
    {
        if (dec.Length < 1) return;
        byte wire = dec[0];
        var worn = _char.Equipment.FirstOrDefault(e => e.Slot == wire);
        if (worn is null) return;
        _char.Equipment.Remove(worn);
        SendUnequip(wire);
        var def = Content.ItemById(worn.ItemId);
        if (def is not null) { ApplyAppearance(def, equip: false); GiveItem(def, 1, worn.Dura, worn.CustomName); }
        SendStats();                                  // drop the gear bonuses from the HUD
    }

    // 0x24 drop gold: dec[0..3]=amount(u32BE). Spill coins onto my tile as a pickup-able gold pile.
    private void HandleDropGold(byte[] dec)
    {
        if (dec.Length < 4) return;
        uint amt = (uint)((dec[0] << 24) | (dec[1] << 16) | (dec[2] << 8) | dec[3]);
        if (amt > _char.Coins) amt = _char.Coins;
        if (amt == 0) { SendLog("You have no coins to drop."); return; }
        _char.Coins -= amt;
        SendStats();
        ushort gfx = amt < 2 ? (ushort)22 : amt < 100 ? (ushort)73 : (ushort)72;   // coins_1 / _2_99 / _100_999 icons
        _world.DropItem(_char.Map, new GroundItem { Id = _world.AllocateItemId(), ItemId = -1,
            X = _char.X, Y = _char.Y, Amount = (int)amt, Graphic = gfx });
    }

    // ---- item GM commands ----

    // "!items [filter]": browse the item registry, fuzzy-ranked by name.
    private void ListItems(string text)
    {
        string q = text.Length > "!items".Length ? text["!items".Length..].Trim() : "";
        var found = Content.SearchItems(q, 15);
        if (found.Count == 0) { SendLog(q.Length == 0 ? "no items loaded (check re/rtk-data/Items.csv)" : $"no items match \"{q}\""); return; }
        SendLog($"items{(q.Length > 0 ? $" ~ \"{q}\"" : "")} ({found.Count} of {Content.Items.Count}):");
        foreach (var i in found)
            SendLog($"  #{i.Id} {i.Name} — {(i.IsEquip ? $"equip(dam {i.Dam}/ac {i.Armor})" : i.IsConsumable ? "use" : "etc")}   (!item {i.Name})");
    }

    // "!item <name or id> [amount]": summon an item into the bag (equip items keep a single copy per slot).
    private void GiveItemCmd(string text)
    {
        string q = text.Length > "!item".Length ? text["!item".Length..].Trim() : "";
        if (q.Length == 0) { SendLog("usage: !item <name or id> [amount]   (browse with  !items <name>)"); return; }
        int amount = 1;
        var parts = q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 1 && int.TryParse(parts[^1], out var n) && n > 0) { amount = n; q = string.Join(' ', parts[..^1]); }
        var def = Content.FindItem(q);
        if (def is null) { SendLog($"no item matches \"{q}\" — try  !items {q}"); return; }
        if (def.Stackable) GiveItem(def, amount);
        else for (int i = 0; i < amount; i++) if (!GiveItem(def)) break;
        SendLog($"Gave {def.Name}{(amount > 1 ? $" x{amount}" : "")} (#{def.Id}, {(def.IsEquip ? $"equip slot {def.EquipSlot}" : def.IsConsumable ? "use" : "etc")}).");
    }

    // "!clearinv": empty the bag + gear (test reset).
    private void ClearInventory()
    {
        foreach (var it in _char.Inventory.ToList()) SendDelItem(it.Slot, 0);
        _char.Inventory.Clear();
        foreach (var e in _char.Equipment.ToList()) SendUnequip(e.Slot);
        _char.Equipment.Clear();
        if (_char.Weapon != 0 || _char.Armor != 0)
        {
            _char.Weapon = 0; _char.Armor = 0;
            SendSelfLook();
            _world.Broadcast(_char.Map, p => p.ShowPlayer(this), except: this);
        }
        SendLog("Cleared your pack and gear.");
    }

    // "!icons [start]": ICON-ID RE. Fill every bag slot with a raw 0x0F whose icon id = start+slot, named
    // "f<icon>", so a screenshot shows which client Item.epf frames render (frame index == client item id;
    // this is a DIFFERENT space from the RTK ItmIcon). Sweep with !icons 0, !icons 27, 54, 81, … and match
    // the rendered icons to re/render_items.py's contact sheet to build the RTK-item -> client-frame map.
    private void IconSweep(string text)
    {
        var a = ParseInts(text);
        int start = a.Length > 0 ? a[0] : 0;
        _char.Inventory.Clear();
        for (int i = 0; i < _char.MaxInv; i++)
            SendRawIcon((byte)i, (ushort)(start + i), $"f{start + i}");
        SendLog($"icons {start}..{start + _char.MaxInv - 1} in bag (match vs render_items.py sheet)");
    }

    // The client's item-sprite resolver (0x435ab0) does `spriteId = iconField + 0x4000`, then the frame
    // indexer (0x431450) bounds-checks the LOW 16 BITS against the Item.epf frame count (1310) — so to
    // render Item.epf frame N (== client item id), the packet icon field must be (N - 0x4000) & 0xFFFF,
    // which wraps back to N after the client's +0x4000. Sending N raw overflows (N+0x4000 >= 1310 → blank).
    private static ushort IconWire(int clientFrame) => (ushort)((clientFrame - 0x4000) & 0xFFFF);

    // Build a 0x0F for a raw client-frame + label with no registry item behind it — for the !icons sweep.
    private void SendRawIcon(byte slot, ushort frame, string label)
    {
        var d = new List<byte> { (byte)(slot + 1) };
        d.AddRange(Be(IconWire(frame)));
        if (_ver == ClientVersion.V533) d.Add(0);          // 5.x icon-color byte (4.95 omits, see SendAddItem)
        var nn = Ascii(label);
        d.Add((byte)nn.Length); d.AddRange(nn);            // display name
        d.Add((byte)nn.Length); d.AddRange(nn);            // base name
        d.AddRange(Be32(1));                               // amount
        d.Add(0); d.AddRange(Be32(0)); d.Add(0);           // stack/dura/protected block
        d.Add(0); d.AddRange(Be(0)); d.Add(0);             // owner len 0 + trailing u16 + u8
        SendMap(0x0F, _gameInc++, d.ToArray(), $"rawicon(0x0F) slot={slot} frame={frame} wire=0x{IconWire(frame):x4}");
    }

    // "!crecol <lookId> [loColor] [hiColor] [step]": spawn the SAME look id across a GRID (12 cols/row,
    // wraps to more rows north) at increasing 0x07 color-byte values (default 0..23 — the client's color
    // byte visibly wraps mod 24, see docs) so every candidate recolor is visible in one screenshot without
    // silently truncating past 12 entries like the old single-row version did.
    private void CreatureColorRow(string text)
    {
        var a = ParseInts(text);
        int look = a.Length > 0 ? a[0] : 0;
        int lo = a.Length > 1 ? a[1] : 0;
        int hi = a.Length > 2 ? a[2] : 23;
        int step = a.Length > 3 ? Math.Max(1, a[3]) : 1;
        const int cols = 12;
        var es = new List<(uint, ushort, ushort, ushort, byte, byte)>();
        int n = 0;
        for (int c = lo; c <= hi; c += step, n++)
        {
            int col = n % cols, row = n / cols;
            ushort x = (ushort)Math.Clamp(_char.X - 4 + col, 0, _char.MapXs - 1);
            ushort y = (ushort)Math.Clamp(_char.Y - 2 - row * 2, 0, _char.MapYs - 1);
            var mob = new Mob(_nextMobId++, (ushort)look, x, y, $"col{c}", 6) { Dir = 2 };
            _mobs.Add(mob);
            es.Add((mob.Id, (ushort)(0x8000 | look), x, y, (byte)c, (byte)2));
        }
        SendCreatureList(es);
        Log.Info($"   -> CREATURE color row: look {look}, color {lo}..{hi} step {step} ({es.Count} sent, {cols}/row)");
    }

    // "!crow <lo> <hi> [step]": sweep monster look ids lo..hi across a W->E row (one 0x07 packet with
    // up to 12 entries) so one screenshot maps the Monster.epf look-id space. Find squirrel/rabbit here.
    private void CreatureRow(string text)
    {
        var a = ParseInts(text);
        int lo = a.Length > 0 ? a[0] : 0;
        int hi = a.Length > 1 ? a[1] : lo + 11;
        int step = a.Length > 2 ? Math.Max(1, a[2]) : 1;
        ushort y = (ushort)Math.Clamp(_char.Y - 2, 0, _char.MapYs - 1);
        var es = new List<(uint, ushort, ushort, ushort, byte, byte)>();
        int col = 0;
        for (int v = lo; v <= hi && col < 12; v += step, col++)
        {
            ushort x = (ushort)Math.Clamp(_char.X - 4 + col, 0, _char.MapXs - 1);
            var mob = new Mob(_nextMobId++, (ushort)v, x, y, $"c{v}", 6) { Dir = 2 };
            _mobs.Add(mob);
            es.Add((mob.Id, (ushort)(0x8000 | v), x, y, (byte)0, (byte)2));
        }
        SendCreatureList(es);
        Log.Info($"   -> CREATURE row: monster look sweep {lo}..{hi} step {step} ({es.Count} sent)");
    }

    private void MobOne(string text)
    {
        var a = ParseInts(text);
        int hi = a.Length > 0 ? a[0] : 0;
        int lo = a.Length > 1 ? a[1] : 1;
        int hp = a.Length > 2 ? a[2] : 6;
        ushort sprite = (ushort)((hi << 8) | (lo & 0xFF));
        var (fx, fy) = FrontTile();
        ushort x = (ushort)Math.Clamp(fx, 0, _char.MapXs - 1);
        ushort y = (ushort)Math.Clamp(fy, 0, _char.MapYs - 1);
        SpawnMob(sprite, x, y, $"m{sprite}", hp);
    }

    private void MobRow(string text)
    {
        var a = ParseInts(text);
        int lo = a.Length > 0 ? a[0] : 1;
        int hi = a.Length > 1 ? a[1] : lo + 11;
        int step = a.Length > 2 ? Math.Max(1, a[2]) : 1;
        ushort y = (ushort)Math.Clamp(_char.Y - 2, 0, _char.MapYs - 1);
        int col = 0;
        for (int v = lo; v <= hi && col < 12; v += step, col++)
        {
            ushort x = (ushort)Math.Clamp(_char.X - 4 + col, 0, _char.MapXs - 1);
            SpawnMob((ushort)v, x, y, $"g{v}", 6);
        }
        Log.Info($"   -> MOB row: graphic id sweep {lo}..{hi} step {step}");
    }

    private void KillMobs()
    {
        int world = _world.ClearMap(_char.Map);   // shared mobs -> despawned for EVERYONE on this map
        int local = _mobs.Count;                  // session-local debug dummies -> just us
        if (local > 0) { SendDespawn(_mobs.Select(m => m.Id).ToArray()); _mobs.Clear(); }
        if (world + local == 0) { SendMessage("no mobs to clear"); return; }
        SendMessage($"cleared {world} world mob(s) + {local} local dummy(s)");
        Log.Info($"   -> KILL: despawned {world} world + {local} local mobs on map {_char.Map}");
    }

    // A small pack of REAL, killable monsters around the player (via 0x07 = Monster.epf). "!spawn
    // [lookId] [hp]" — lookId is the Monster.tbl monster index (0..326); defaults to 0.
    private void SpawnCritters(string text)
    {
        var a = ParseInts(text);
        int look = a.Length > 0 ? a[0] : 0;
        int hp = a.Length > 1 ? a[1] : 6;
        (int dx, int dy)[] spots = { (0, -2), (2, 0), (-2, 0), (0, 2) };
        foreach (var (dx, dy) in spots)
        {
            ushort x = (ushort)Math.Clamp(_char.X + dx, 0, _char.MapXs - 1);
            ushort y = (ushort)Math.Clamp(_char.Y + dy, 0, _char.MapYs - 1);
            SpawnMonster((ushort)look, x, y, $"monster{look}", hp, dir: 2);
        }
        Log.Info($"   -> SPAWN monster pack look={look}");
    }

    private void SetWeapon(string text)
    {
        var a = ParseInts(text);
        _char.Weapon = (byte)(a.Length > 0 ? a[0] : 0);
        if (_enteredWorld) _store.Save(_char);
        SendSelfLook();   // re-send the self appearance so the weapon shows (may need a relog to redraw)
        SendMessage($"weapon set to {_char.Weapon}");
        Log.Info($"   -> WEAPON set to {_char.Weapon}");
    }

    // "!ride" / "!mount [0|1]" — toggle (or set) the mounted-on-horse state. Flips appearance[1] to the
    // form byte 3, which makes the client draw the horse+rider composite (SPR 344/345) instead of the human
    // sprite. Re-draws self and every co-located peer in place (same path ApplyAppearance uses for gear).
    private void ToggleMount(string text)
    {
        var a = ParseInts(text);
        _char.Mounted = a.Length > 0 ? a[0] != 0 : !_char.Mounted;
        SendSelfLook();                                                    // redraw self on the horse
        _world.Broadcast(_char.Map, p => p.ShowPlayer(this), except: this); // and for everyone watching
        SendMessage(_char.Mounted ? "You climb onto the horse." : "You dismount.");
        Log.Info($"   -> MOUNT {( _char.Mounted ? "on" : "off")}");
    }

    // "!lvl N" / "!might N" — set a BASE character stat so wear-requirements can be exercised on the
    // fabricated bring-up character (default is level 1 / might 3, which gates out most real gear).
    private void SetBaseStat(string which, string text)
    {
        var a = ParseInts(text);
        int v = a.Length > 0 ? a[0] : 0;
        if (which == "level") _char.Level = (byte)Math.Clamp(v, 1, 99);
        else                  _char.Might = (byte)Math.Clamp(v, 0, 255);
        if (_enteredWorld) _store.Save(_char);
        SendStats();
        SendMessage($"{which} set to {(which == "level" ? _char.Level : _char.Might)}");
        Log.Info($"   -> {which.ToUpper()} set to {(which == "level" ? _char.Level : _char.Might)}");
    }

    // "!class <name>" — set the class/path line shown in the self-profile ("Mind's Eye", 0x39). This is a
    // display string only; path/class WEAR restrictions (ItmPthId) aren't enforced (no path-id concept yet).
    // Re-pushes the profile so an open window updates immediately.
    private void SetClass(string text)
    {
        string name = text.Length > "!class".Length ? text["!class".Length..].Trim() : "";
        if (name.Length == 0) { SendLog($"class is '{_char.ClassName}' (usage: !class <name>)"); return; }
        _char.ClassName = name;
        if (_enteredWorld) _store.Save(_char);
        SendSelfProfile();
        SendMessage($"class set to {name}");
        Log.Info($"   -> CLASS set to '{name}'");
    }

    // ---- stats/HUD probe lab ----
    // The 4.95 self-stats opcode is unknown (0x08 is a no-op here, unlike 7.x). Static RE narrowed
    // the candidates but can't confirm which opcode drives the persistent HUD. So we probe live:
    // "!s <hexop> [hexflags]" fires a 7.x-shaped status packet full of unmistakable SENTINEL values
    // on the given opcode; whichever opcode makes the HUD numbers change is the stats opcode. Once
    // found, we decode the exact field layout by varying one sentinel at a time (look-lab style).
    // Sentinels chosen to be visually unmistakable and distinct from each other:
    //   level=99  might=11 will=22 grace=33  maxHP=1000 maxMP=500  hp=987 mp=456  exp=54321 coins=777
    private void StatProbe(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        byte op = 0x08, flags = 0xFF;
        if (parts.Length > 1) byte.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out op);
        if (parts.Length > 2) byte.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out flags);
        SendStatProbe(op, flags, level: 99);
        Log.Info($"   -> STAT PROBE op=0x{op:x2} flags=0x{flags:x2}");
    }

    // "!batch" — fire the sentinel-laden status probe at a CURATED SAFE set of opcodes (no resource
    // loaders like 0x2e, no risky memcpy/spawn), ~700ms apart with a bubble label. Paired with the
    // probe's whole-memory sentinel scan, one run reveals which opcode (if any) STORES the stats — no
    // matter where the client keeps them. Watch the HUD too and note any opcode that changes a number.
    private void StatBatch(string text)
    {
        byte[] safe = { 0x11, 0x12, 0x1d, 0x1f, 0x1b, 0x21, 0x29, 0x2f, 0x30, 0x31,
                        0x35, 0x36, 0x42, 0x46, 0x59, 0x34, 0x39 };
        Log.Info($"   -> STAT BATCH over {safe.Length} opcodes");
        foreach (var op in safe)
        {
            SendSpeech(0, _char.Id, Encoding.ASCII.GetBytes($"op 0x{op:x2}"));
            SendStatProbe(op, 0xFF, level: 99);
            System.Threading.Thread.Sleep(700);
        }
        SendSpeech(0, _char.Id, "batch done"u8.ToArray());
        Log.Info("   -> STAT BATCH done");
    }

    // "!r6 [hexop]" — replay the EXACT stats packet captured from a real 6.x server (jeedee/TkServer
    // game_server.rb), decrypted with the shared NexonInc cipher. 6.x uses opcode 0x08 for stats; this
    // is a valid low-level character: level=1, maxHP=51, maxMP=33, might/will/grace=3. If the 4.95 HUD
    // populates, 0x08 is (still) the stats opcode here; if not, its opcode shifted and we match this
    // KNOWN-GOOD layout against 4.95 handlers. Default op 0x08; pass another hex op to try the layout
    // on a different opcode.
    private static readonly byte[] Stats6xFull =
    {
        0x78,                               // flags (full)
        0x00, 0x00, 0x00, 0x00,             // unk, nation, totem, unk
        0x01,                               // level = 1
        0x00, 0x00, 0x00, 0x33,             // maxHP u32BE = 51
        0x00, 0x00, 0x00, 0x21,             // maxMP u32BE = 33
        0x03, 0x03, 0x03, 0x03, 0x03,       // might, will, ?, ?, grace
        0x00, 0x00,
        0x63, 0xdf, 0x9c, 0x5f,             // (captured; ac/exp-ish region)
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x33, 0x00, 0x00, 0x00, 0x21,       // repeat 51/33 -> current HP/MP block
        0x00, 0x00, 0x00, 0x01,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x00, 0xb3, 0x3d, 0x00, // trailing settings/flags
    };

    // "!stg" — self-describing GRADIENT stats packet on 0x08 (the confirmed 4.95 stats opcode). Body
    // byte[i] = i, so every HUD number reveals its own field offset: a byte field shows its offset; a
    // u32 field shows 0xNN.. from which the offset AND endianness fall out. Flags kept at 0x78 (the
    // captured 6.x "full" value that lit every field). One read maps the entire 4.95 layout.
    private void StatGradient(string text)
    {
        var d = new byte[60];
        d[0] = 0x78;                                   // flags (full-stats)
        for (int i = 1; i < d.Length; i++) d[i] = (byte)i;
        SendMap(0x08, _gameInc++, d, "stat-gradient(0x08)");
        Log.Info("   -> STAT GRADIENT on 0x08 (body[i]=i); read each HUD number = that field's offset");
    }

    private void StatReplay6x(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        byte op = 0x08;
        if (parts.Length > 1) byte.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out op);
        SendMap(op, _gameInc++, Stats6xFull, $"replay6x-stats(0x{op:x2})");
        Log.Info($"   -> REPLAY 6.x stats on op=0x{op:x2} (expect HUD: level 1, HP 51, MP 33, might/will/grace 3)");
    }

    // "!sweep" is DISABLED. Blind-sweeping unknown opcodes crashes the client: several handlers do real
    // resource loads from the packet body (e.g. 0x2e = the skills/spells list loads an .EPF sprite archive
    // per entry — garbage bytes -> bogus filename -> "File not found .EPF" -> crash). Find the stats opcode
    // deterministically instead (self player object is [world+0x40c]); only fire "!s <op>" once a specific
    // opcode is confirmed safe by reading its handler.
    private void StatSweep(string text)
    {
        SendMessage("!sweep is disabled (crashes the client on resource-loading opcodes). Use !s <hexop>.");
        Log.Info("   -> !sweep refused (unsafe blind probe)");
    }

    // Build a 7.x-style status packet (flags byte then FULLSTATS/HPMP/XPMONEY/ALWAYS blocks) with the
    // given opcode, flags, and level sentinel. Layout mirrors Mithia clif_sendstatus so that if the
    // 4.95 handler is structurally similar, recognizable numbers land on the HUD.
    private void SendStatProbe(byte op, byte flags, byte level)
    {
        var d = new List<byte> { flags };
        // FULLSTATS block
        d.Add(0);                    // unknown
        d.Add(_char.Nation);         // nation
        d.Add(_char.Totem);          // totem
        d.Add(0);                    // unknown
        d.Add(level);                // level (sentinel)
        d.AddRange(Be32(1000));      // maxHP
        d.AddRange(Be32(500));       // maxMP
        d.Add(11);                   // might
        d.Add(22);                   // will
        d.Add(3); d.Add(3);          // (7.x constants)
        d.Add(33);                   // grace
        d.Add(0); d.Add(0);
        d.Add(0);                    // AC
        d.Add(0); d.Add(0); d.Add(0); d.Add(0); d.Add(0); d.Add(0); d.Add(0);
        d.Add(_char.MaxInv);         // maxinv
        // HPMP block
        d.AddRange(Be32(987));       // hp
        d.AddRange(Be32(456));       // mp
        // XPMONEY block — zero-free distinctive sentinels so a memory scan finds the STORED copy cleanly
        d.AddRange(Be32(0x11223344));  // exp   -> wire 11 22 33 44 ; stored LE 44 33 22 11
        d.AddRange(Be32(0x55667788));  // coins -> wire 55 66 77 88 ; stored LE 88 77 66 55
        d.Add(50);                   // exp %
        // ALWAYS block
        d.Add(0); d.Add(0); d.Add(0); d.Add(0); d.Add(0); d.Add(0); d.Add(0);
        d.AddRange(Be32(0));         // settingFlags
        SendMap(op, _gameInc++, d.ToArray(), $"statprobe(0x{op:x2})");
    }

    // 0x0D speech: chatType(u8) entityId(u32BE) msgLen(u8) msg[]. Handler 0x450170 shows msg
    // over the entity's head.
    private void SendSpeech(byte chatType, uint id, byte[] msg)
    {
        var d = new List<byte> { chatType };
        d.AddRange(Be32(id));
        d.Add((byte)msg.Length);
        d.AddRange(msg);
        SendMap(0x0D, _gameInc++, d.ToArray(), "speech(0x0D)");
    }

    // Client attack (0x13, spacebar) = just a trigger ("13 00"). Reply with an ACTION packet 0x1A
    // so the entity plays the swing. (0x13 was WRONG — its handler 0x4508f0 computes anim = 0x8f-a,
    // and a=0 -> anim 0x8f = the DEATH animation, which is why the character flashed "dead".)
    // 0x1A = entityId(u32BE) type(u8) time(u16BE) param(u8); handler 0x4503a0 plays the action
    // (client scales time x10). type: 0=stand,1=attack,2=throw,3=shot,4=sit,6=magic,8=eat.
    private void HandleAttack(byte[] dec)
    {
        SendAction(_char.Id, type: 1, time: 8, param: 0);                                 // our own swing anim
        _world.Broadcast(_char.Map, p => p.ActionOver(_char.Id, 1, 8, 0), except: this);  // peers see us swing

        // Weapon swing sfx: the client plays no sound for the swing action itself, so send one over 0x19 when
        // a weapon is in hand (bare-handed swings stay quiet). The sound is the weapon's own ItmSound (RTK's
        // per-weapon mapping — most swords 331, Sword of power 337, …); !swingsnd overrides it for calibration.
        // Everyone on the map hears it, bound to us.
        int swing = _swingSfx > 0 ? _swingSfx : EquippedWeaponSound();
        if (swing > 0) _world.Broadcast(_char.Map, p => p.SoundAt(swing, _char.Id));

        // Melee resolves against whatever creature stands on the tile directly in front of us (facing
        // tracked from the last walk step). Check the SHARED world FIRST — HP there is world-authoritative
        // so two players can't double-kill and both claim the reward — then fall back to session-local
        // debug dummies (look-lab / !cre / !mob sweeps, visible only to us).
        var (fx, fy) = FrontTile();
        int dmg = Math.Max(1, EffMight + (WeaponLook() != 0xFF ? 3 : 0) + Totals().dam);   // effective might + weapon + gear/buff Dam

        var wmob = _world.MobAt(_char.Map, fx, fy);
        if (wmob is not null)
        {
            if (_world.TryDamage(_char.Map, wmob, dmg, out bool died))
            {
                ShowDamageResult(wmob.Id, wmob, died);   // 0x13: over-head HP bar + hit anim (empty bar + delayed despawn on death)
                Log.Info($"   -> hit world mob {wmob.Id} '{wmob.Name}' for {dmg} -> {wmob.Hp}/{wmob.MaxHp}");
                if (died)
                {
                    uint reward = (uint)(wmob.Exp > 0 ? wmob.Exp : wmob.MaxHp);   // real mob Exp; fallback to HP
                    _char.Exp += reward;                                          // reward to the killer only
                    SendStats();
                    SendMessage($"You defeated {wmob.Name}. (+{reward} exp)");
                    Log.Info($"   -> world mob {wmob.Id} '{wmob.Name}' defeated (+{reward} exp)");
                    TallyKill(wmob);   // bump the lifetime kill count for quests (see TallyKill / KillCount)
                }
            }
            return;
        }

        var mob = MobAt(fx, fy);
        if (mob is null) return;

        mob.Hp -= dmg;
        bool dummyDied = !mob.Alive;
        SendDamage(mob.Id, dummyDied ? (byte)0 : HpPercent(mob), HitCritByte);   // 0x13: over-head HP bar + hit anim (dummy is session-local)
        Log.Info($"   -> hit dummy {mob.Id} '{mob.Name}' for {dmg} -> {mob.Hp}/{mob.MaxHp}");
        if (dummyDied)
        {
            _mobs.Remove(mob);
            uint deadId = mob.Id;
            if (DeathDespawnMs <= 0) SendDespawn(deadId);   // 0x0E: remove the corpse from our client
            else _ = Task.Run(async () => { try { await Task.Delay(DeathDespawnMs); SendDespawn(deadId); } catch { } });   // after the death beat
            _char.Exp += (uint)mob.MaxHp;              // reward: exp equal to the mob's max HP
            SendStats();                               // refresh the HUD exp bar
            SendMessage($"You defeated {mob.Name}. (+{mob.MaxHp} exp)");
            Log.Info($"   -> dummy {mob.Id} '{mob.Name}' defeated");
        }
    }

    // 0x1d = the emote wheel (press ':'), body[0] = emote index. The client plays action
    // (index + 11) — see RTK clif_parseemotion: sendaction(&bl, RFIFOB(5)+11, 0x4E, 0). The +11 maps
    // index 0 -> action 11 (Laughter) ... index 11 -> action 22 (Dance) ... index 13 -> 24 (Kiss).
    // Broadcast it as a 0x1A action so we AND every peer on the map see the animation (the client's own
    // action sprite carries any looped sound). time 0x4E matches RTK's emote length; param 0 = no extra sound.
    private void HandleEmotion(byte[] dec)
    {
        if (dec.Length < 1) return;
        byte action = (byte)(dec[0] + 11);
        const ushort time = 0x4E;
        SendAction(_char.Id, action, time, 0);                                       // play it on our own client
        _world.Broadcast(_char.Map, p => p.ActionOver(_char.Id, action, time, 0), except: this);  // and for peers
        Log.Info($"   -> EMOTE idx={dec[0]} -> action {action} (0x1A)");
    }

    // The map tile one step ahead of the player, in the direction we're currently facing.
    private (int x, int y) FrontTile()
    {
        int x = _char.X, y = _char.Y;
        switch (_facing & 3) { case 0: y--; break; case 1: x++; break; case 2: y++; break; case 3: x--; break; }
        return (x, y);
    }

    // First living mob standing on (x,y), or null.
    private Mob? MobAt(int x, int y) =>
        _mobs.FirstOrDefault(m => m.Alive && m.X == x && m.Y == y);

    private void SendAction(uint id, byte type, ushort time, byte param)
    {
        var d = new List<byte>();
        d.AddRange(Be32(id));
        d.Add(type);
        d.AddRange(Be(time));
        d.Add(param);
        SendMap(0x1A, _gameInc++, d.ToArray(), $"action(0x1A) type={type} time={time}");
    }

    // 0x19 = background music. Handler 0x450ad0 reads: type(u8 @+1) pad(u8 @+2) bgm(u16BE @+3)
    // volume(u8 @+5, 0..100, client log-scales it). type 2 = MIDI (the stock 1.mid..12.mid in
    // NexusTK.snd); type 1 = mp3/lsr (the stock client has none). bgm 0 stops the music.
    private ushort _bgm = 0xFFFF;   // last track sent, so we don't restart the same song on a refresh

    // Melee swing sfx (NexusTK.snd id). The client's action->sound table gives the swing action (0x1A type 1)
    // NO sound (like magic/type 6 -> 0), so a weapon swing is silent unless we play one explicitly over 0x19.
    // Calibrate the id live with "!swingsnd <id>" (auditions it), then it rides every armed swing; 0 = silent.
    private int _swingSfx = 0;
    private void SendMusic(ushort bgm, byte type = 2, byte volume = 100)
    {
        var d = new List<byte>();
        d.Add(type);            // +1 type/channel (2 = midi)
        d.Add(0);               // +2 reserved
        d.AddRange(Be(bgm));    // +3 track id (u16 BE)
        d.Add(volume);          // +5 volume 0..100
        SendMap(0x19, _gameInc++, d.ToArray(), $"music(0x19) bgm={bgm} type={type} vol={volume}");
        _bgm = bgm;
    }

    // Play the track assigned to a map, but only if it differs from what's already playing — re-sending
    // the same id would restart the song (jarring on a Ctrl+R refresh or a same-map re-entry).
    private void PlayMapMusic(ushort mapId)
    {
        var (bgm, type) = Content.BgmFor(mapId);
        if (bgm == _bgm) return;
        SendMusic(bgm, type);
        Log.Info($"   -> music map {mapId} -> {bgm}.mid (0x19)");
    }

    // "!music <id> [vol]" — play a specific track. id is 1..12 (the stock client ships 12 midis, type 2).
    // vol is the raw volume byte the client log-scales: 100 is nominal "full", but the midi path compresses
    // it, so values ABOVE 100 (up to 255) push it louder. "!music 0" / "!music stop" stops the music.
    private void PlayMusicCmd(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            SendLog("usage: !music <1-12> [vol 0-255, default 100]   (!music 0 or !music stop = stop)");
            return;
        }
        if (parts[1].Equals("stop", StringComparison.OrdinalIgnoreCase)) { SendMusic(0); SendLog("music stopped"); return; }
        if (!ushort.TryParse(parts[1], out var bgm)) { SendLog($"'{parts[1]}' is not a track number"); return; }
        byte vol = parts.Length > 2 && byte.TryParse(parts[2], out var v) ? v : (byte)100;
        SendMusic(bgm, type: 2, volume: vol);
        SendLog(bgm == 0 ? "music stopped" : $"playing track {bgm} (vol {vol})");
        Log.Info($"   -> !music bgm={bgm} vol={vol}");
    }

    // Play raw client sound ids (0x19 sfx) to calibrate the 4.95 NexusTK.snd id space. RTK's per-spell sound
    // ids may not line up with the client's 001.wav..197.wav numbering, and the user hears "shifted" variants.
    // `!snd 4` plays one; `!snd 4 5 6` plays several; `!snd 1 197 -` (a trailing '-') is rejected — keep it to a
    // few at a time so they don't overlap into noise. Identify each by ear to map RTK sound -> client sound.
    private void SoundProbe(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) { SendLog("usage: !snd <id> [id2 …]   (plays client sound ids; NexusTK.snd has 001..197.wav)"); return; }
        int played = 0;
        for (int i = 1; i < parts.Length && played < 8; i++)
        {
            if (!int.TryParse(parts[i], out var id) || id <= 0) continue;
            SendSound(id, _char.Id);
            SendLog($"playing sound {id}");
            Log.Info($"   -> !snd {id}");
            played++;
        }
        if (played == 0) SendLog("no valid sound ids (want positive integers)");
    }

    // "!swingsnd <id>" — set the melee swing sfx (and play it once so you can audition it in place). Use with
    // "!snd" to hunt the right woosh id in NexusTK.snd, then "!swingsnd <that id>" bakes it onto every armed
    // swing. "!swingsnd 0" mutes it again. Session-local (resets on relog) until we bake the final id as default.
    private void SetSwingSound(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var id) || id < 0)
        { SendLog($"usage: !swingsnd <id>   (current: {_swingSfx}; 0 = silent)"); return; }
        _swingSfx = id;
        if (id > 0) SendSound(id, _char.Id);
        SendLog($"swing sfx = {id}{(id == 0 ? " (muted)" : "")}");
        Log.Info($"   -> !swingsnd {id}");
    }

    // Play raw Effect.tbl animation ids (0x29) over the caster, to calibrate the 4.95 effect id space vs RTK's
    // sendAnimation ids. Low ids (unaligned heal 5, spark 28) are confirmed identity, but RTK's 6.x/7.x client may
    // have inserted effects that shift mid/high ids — e.g. the aligned heals (Ohaeng 63 / Ming-Ken 64 / Kwi-Sin 65)
    // may not line up. `!efx 5 63 64 65` plays the four heal variants so we can see which id is really which.
    private void EffectProbe(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) { SendLog("usage: !efx <id> [id2 …]   (play Effect.tbl anim ids 0..127 over you)"); return; }
        int played = 0;
        for (int i = 1; i < parts.Length && played < 8; i++)
        {
            if (!int.TryParse(parts[i], out var id) || id < 0 || id > 127) continue;
            SendEffect(_char.Id, id);
            SendLog($"effect {id}");
            Log.Info($"   -> !efx {id}");
            played++;
        }
        if (played == 0) SendLog("no valid effect ids (0..127)");
    }

    // "!hit <pct> [crit]" — audition the 0x13 combat packet over the mob you're facing (or yourself if none):
    // draws the over-head HP bar at <pct>% and plays the hit overlay animation 0x8f-<crit>. Use it to calibrate
    // NEXUS_HIT_CRIT (which hit spark looks right) and to confirm the HP bar renders. Default crit = the baked-in
    // HitCritByte. e.g. "!hit 50" (half bar) then "!hit 50 0" / "!hit 50 40" to compare hit animations.
    private void HitProbe(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var pct))
        { SendLog("usage: !hit <pct 0..100> [crit 0..255]   (over-head HP bar + hit anim on the faced mob)"); return; }
        byte crit = parts.Length > 2 && byte.TryParse(parts[2], out var c) ? c : HitCritByte;
        pct = Math.Clamp(pct, 0, 100);

        var (fx, fy) = FrontTile();
        var wmob = _world.MobAt(_char.Map, fx, fy);
        uint target = wmob?.Id ?? MobAt(fx, fy)?.Id ?? _char.Id;
        if (wmob is not null) _world.Broadcast(_char.Map, p => p.DamageOver(target, (byte)pct, crit));
        else                  SendDamage(target, (byte)pct, crit);
        SendLog($"hit id={target} pct={pct} crit={crit} (anim {0x8f - (sbyte)crit})");
        Log.Info($"   -> !hit id={target} pct={pct} crit={crit}");
    }

    // ---- profile window (the "Mind's Eye") ----
    // The client opens the self-profile window when the profile key is pressed by sending 0x2D. Byte 0
    // == 0 is the self-profile request (byte != 0 is group status in 7.x). We reply with 0x39, the
    // self-profile packet (clif_mystaytus): AC/clan/title/class/legend. Without this reply the window
    // never appears — that's the bug the user hit.
    // 0x66 right-click "examine item". Body (live capture): [0]=00 [1]=itemRef(varies per item) [2]=00
    // [3..6]=01 01 01 01 [7..9]=00 00 00. body[1] is the item selector — decode it against the bag/gear by
    // right-clicking KNOWN items and reading this log (does body[1] equal the slot? the icon? the item id?).
    // Once body[1] is understood AND the 0x66 REPLY format (client handler 0x4511b0) is reversed, this can
    // answer with the item-detail popup. Until then it's a decode probe (no reply -> the client stops retrying
    // only after its own timeout, which is harmless).
    private void HandleItemInfoRequest(byte[] dec)
    {
        int sel = dec.Length > 1 ? dec[1] : -1;
        var it = _char.Inventory.FirstOrDefault(i => i.Slot == sel)
              ?? _char.Equipment.FirstOrDefault(e => e.Slot == sel);
        var def = it is null ? null : Content.ItemById(it.ItemId);
        Log.Info($"   -> ITEM-INFO (0x66) sel={sel} (0x{(sel < 0 ? 0 : sel):x2}) body={Log.Hex(dec)}" +
                 (def is null ? "  [no bag/gear item at that slot]" : $"  -> '{def.Name}' #{def.Id}"));
    }

    private void HandleProfileRequest(byte[] dec)
    {
        byte sub = dec.Length > 0 ? dec[0] : (byte)0;
        Log.Info($"   -> PROFILE request (0x2D) sub={sub}");
        if (sub == 0) SendSelfProfile();
    }

    // The client clicks an entity to inspect it: 0x43 = 01 entityId(u32BE) 00. For our own id we show
    // the self-profile; other ids would use the other-player profile (0x34) once other entities exist.
    private void HandleClickInfo(byte[] dec)
    {
        uint id = 0;
        if (dec.Length >= 5) id = (uint)((dec[1] << 24) | (dec[2] << 16) | (dec[3] << 8) | dec[4]);
        Log.Info($"   -> CLICK-INFO (0x43) id={id}");

        // An NPC click opens its dialog instead of a profile. NPCs live in the shared mob list (as
        // non-fighting mobs), so MobById finds them; the IsNpc flag distinguishes them from a real creature.
        if (id != 0 && _world.MobById(_char.Map, id) is { IsNpc: true } npc) { OpenNpcDialog(npc); return; }

        // Otherwise -> the public "profile" view (0x34): portrait + writable blurb, NOT the stats/legend
        // window (0x39). id 0 (or a plain creature/self) falls back to our own profile.
        SendClickProfile(id == 0 ? _char.Id : id);
    }

    // ===== NPC dialog =============================================================================
    // Clicking an NPC (0x43) runs its behaviour here. An NPC is a COMPOSITION of reusable abilities
    // (Shop, Bank, Transport, …) declared in NpcScripts; its own definition holds only what's unique to it.
    // The flow is async: a behaviour awaits each prompt and the client's 0x3A reply (HandleNpcDialog)
    // completes that await, so behaviours read as linear code (menu -> branch -> loop) rather than a
    // callback tree — mirroring RTK's coroutine scripts. Everything runs on the read thread (the reply
    // completes the TaskCompletionSource inline), so it never races the session's other state.
    private readonly record struct DialogReply(byte Kind, int Step, int MenuIndex, string Input);
    private TaskCompletionSource<DialogReply>? _dlgReply;   // the prompt currently awaiting a 0x3A reply
    private const uint BankMax = 100_000_000;              // RTK per-account coin cap

    private void OpenNpcDialog(Mob npc)
    {
        var def = Content.NpcById(npc.NpcDefId);
        Log.Info($"   -> NPC dialog: id={npc.Id} '{npc.Name}' def={npc.NpcDefId}");
        if (def is null) { SendScriptMessage(npc.Id, $"{npc.Name}\n\nGreetings, traveller.", NpcPortrait(npc), npc.Color); return; }
        _ = RunNpcAsync(npc, def);   // fire-and-forget: suspends on the first prompt, resumes on the reply
    }

    // Assemble the NPC's top menu from its abilities' entries and dispatch the pick. Identical for every
    // NPC — the abilities carry all the behaviour, so nothing NPC-specific lives here.
    private async Task RunNpcAsync(Mob npc, NpcDef def)
    {
        try
        {
            var ctx = new NpcContext(this, npc, def);
            var entries = NpcScripts.For(def).SelectMany(a => a.Entries(ctx)).ToList();
            if (entries.Count == 0) { await ctx.Say("Greetings, traveller."); return; }

            int choice = await ctx.Menu($"{def.Name}: How can I help you today?", entries.Select(e => e.label).ToList());
            if (choice >= 1 && choice <= entries.Count) await entries[choice - 1].run(ctx);
        }
        catch (Exception e) { Log.Info($"!! NPC dialog error ('{npc.Name}'): {e.Message}"); }
    }

    // ---- async dialog primitives (used by NpcContext, which abilities call) ---------------------
    // Each sends a 0x30 and awaits the client's 0x3A. A menu returns the 1-based pick (0 = cancelled).
    internal async Task<int> DlgMenu(Mob npc, string prompt, IReadOnlyList<string> options)
    {
        SendNpcMenu(npc, prompt, options);
        var r = await AwaitReply();
        return r.Kind == 0x02 ? r.MenuIndex : 0;
    }

    internal async Task DlgSay(Mob npc, string text)
    {
        SendScriptMessage(npc.Id, text, NpcPortrait(npc), npc.Color);
        await AwaitReply();   // hold the script until the player closes the box
    }

    // Free-text input box. Returns the typed string, or null if the player cancelled. The client confirms a
    // real submit with kind 4 + step 2 (RTK clif_parsenpcdialog requires RFIFOB(fd,13)==2); anything else is
    // a cancel/close.
    internal async Task<string?> DlgInput(Mob npc, string prompt)
    {
        SendInputBox(npc, prompt);
        var r = await AwaitReply();
        return r.Kind == 0x04 && r.Step == 0x02 ? r.Input : null;
    }

    private Task<DialogReply> AwaitReply()
    {
        var tcs = new TaskCompletionSource<DialogReply>();
        _dlgReply = tcs;      // a new click orphans any previous pending prompt (it's GC'd, never resumes)
        return tcs.Task;
    }

    // ---- shop ability implementation (Buy / Sell) ----------------------------------------------
    // Looped so the window stays open: pick -> confirm -> back to the list; cancel (0) to leave. Reads as a
    // shop should — the async layer is what makes this straight-line instead of a web of callbacks.
    internal async Task DlgBuy(Mob npc, Shops.Category[]? catalogue)
    {
        var cats = catalogue?.Where(c => c.Keys.Any(k => Content.ItemByKey(k) is not null)).ToList() ?? new();
        if (cats.Count == 0) { await DlgSay(npc, "I've nothing to sell right now."); return; }

        Shops.Category cat;
        if (cats.Count == 1) cat = cats[0];   // flat shop (inn) — no category step
        else
        {
            int ci = await DlgMenu(npc, "What would you like to buy?", cats.Select(c => c.Name).ToList());
            if (ci < 1 || ci > cats.Count) return;
            cat = cats[ci - 1];
        }

        var items = cat.Keys.Select(Content.ItemByKey).OfType<ItemDef>().ToList();
        while (true)
        {
            int ii = await DlgMenu(npc, "What would you like?", items.Select(it => $"{it.Name} - {it.BuyPrice}g").ToList());
            if (ii < 1 || ii > items.Count) return;   // cancelled -> done shopping
            var it = items[ii - 1];
            if (_char.Coins < (uint)it.BuyPrice) { await DlgSay(npc, $"You can't afford {it.Name} ({it.BuyPrice} gold)."); continue; }
            if (!GiveItem(it)) return;                 // pack full — GiveItem already told the player
            _char.Coins -= (uint)it.BuyPrice;
            SendStats();
            Log.Info($"   -> BUY '{it.Name}' -{it.BuyPrice}g (coins now {_char.Coins})");
            await DlgSay(npc, $"You bought {it.Name} for {it.BuyPrice} gold.");
        }
    }

    internal async Task DlgSell(Mob npc)
    {
        while (true)
        {
            var sellable = _char.Inventory.OrderBy(i => i.Slot)
                .Select(inv => (inv, def: Content.ItemById(inv.ItemId)))
                .Where(t => t.def is { NoDrop: false } && t.def.SellPrice > 0)
                .ToList();
            if (sellable.Count == 0) { await DlgSay(npc, "You have nothing I'd buy."); return; }

            int i = await DlgMenu(npc, "What would you like to sell?",
                                  sellable.Select(t => $"{t.def!.Name} - {t.def.SellPrice}g").ToList());
            if (i < 1 || i > sellable.Count) return;
            var (inv, def) = sellable[i - 1];
            _char.Coins += (uint)def!.SellPrice;
            if (--inv.Amount <= 0) { _char.Inventory.Remove(inv); SendDelItem((byte)inv.Slot, 1); }   // reason 1 = removed
            else SendAddItem(inv);
            SendStats();
            await DlgSay(npc, $"You sold {def.Name} for {def.SellPrice} gold.");
        }
    }

    // ---- bank ability implementation (vault: coin + item storage) ------------------------------
    // Looped like the shop: each action returns to the vault menu until the player cancels. Storage lives on
    // the Character (BankMoney / BankItems) and persists via the JSON store. Joint/shared accounts (RTK's
    // multi-owner vaults) are intentionally out of scope for a single-owner vault.
    internal async Task DlgBank(Mob npc)
    {
        while (true)
        {
            var opts = new List<string> { "Deposit Item", "Withdraw Item" };
            if (_char.Coins > 0)     opts.Add("Deposit Money");
            if (_char.BankMoney > 0) opts.Add("Withdraw Money");

            int c = await DlgMenu(npc, $"Your vault holds {_char.BankMoney} coins. What would you like to do?", opts);
            if (c < 1 || c > opts.Count) return;
            switch (opts[c - 1])
            {
                case "Deposit Item":   await BankDepositItem(npc);   break;
                case "Withdraw Item":  await BankWithdrawItem(npc);  break;
                case "Deposit Money":  await BankDepositMoney(npc);  break;
                case "Withdraw Money": await BankWithdrawMoney(npc); break;
            }
        }
    }

    private async Task BankDepositMoney(Mob npc)
    {
        var s = await DlgInput(npc, $"You carry {_char.Coins} coins. How much will you deposit?");
        if (s is null) return;   // cancelled
        long amt = Math.Min(Math.Min(ParseAmount(s), _char.Coins), BankMax - _char.BankMoney);
        if (amt <= 0) { await DlgSay(npc, "You deposit nothing."); return; }
        _char.Coins -= (uint)amt;
        _char.BankMoney += (uint)amt;
        SendStats();
        await DlgSay(npc, $"You deposit {amt} coins. Your vault now holds {_char.BankMoney}.");
    }

    private async Task BankWithdrawMoney(Mob npc)
    {
        var s = await DlgInput(npc, $"Your vault holds {_char.BankMoney} coins. How much will you withdraw?");
        if (s is null) return;
        long amt = Math.Min(ParseAmount(s), _char.BankMoney);
        if (amt <= 0) { await DlgSay(npc, "You withdraw nothing."); return; }
        _char.BankMoney -= (uint)amt;
        _char.Coins += (uint)amt;
        SendStats();
        await DlgSay(npc, $"Here are your {amt} coins. Your vault now holds {_char.BankMoney}.");
    }

    private async Task BankDepositItem(Mob npc)
    {
        var items = _char.Inventory.OrderBy(i => i.Slot)
            .Select(inv => (inv, def: Content.ItemById(inv.ItemId)))
            .Where(t => t.def is not null)
            .ToList();
        if (items.Count == 0) { await DlgSay(npc, "You have nothing to store."); return; }

        int i = await DlgMenu(npc, "Which item will you store?",
                              items.Select(t => t.inv.Amount > 1 ? $"{t.def!.Name} ({t.inv.Amount})" : t.def!.Name).ToList());
        if (i < 1 || i > items.Count) return;
        var (inv, def) = items[i - 1];
        _char.Inventory.Remove(inv);
        SendDelItem((byte)inv.Slot, 1);         // reason 1 = removed from the bag
        _char.BankItems.Add(inv);               // whole stack goes to the vault
        await DlgSay(npc, $"You store {def!.Name} in your vault.");
    }

    private async Task BankWithdrawItem(Mob npc)
    {
        var stored = _char.BankItems
            .Select(bi => (bi, def: Content.ItemById(bi.ItemId)))
            .Where(t => t.def is not null)
            .ToList();
        if (stored.Count == 0) { await DlgSay(npc, "Your vault is empty."); return; }

        int i = await DlgMenu(npc, "Which item will you withdraw?",
                              stored.Select(t => t.bi.Amount > 1 ? $"{t.def!.Name} ({t.bi.Amount})" : t.def!.Name).ToList());
        if (i < 1 || i > stored.Count) return;
        var (bi, def) = stored[i - 1];
        int slot = FreeSlot();
        if (slot < 0) { await DlgSay(npc, "Your pack is full."); return; }
        _char.BankItems.Remove(bi);
        bi.Slot = (byte)slot;                   // assign a fresh bag slot (vault slots are meaningless)
        _char.Inventory.Add(bi);
        SendAddItem(bi);
        await DlgSay(npc, $"You withdraw {def!.Name} from your vault.");
    }

    // Digits-only amount parse (mirrors RTK inputNumberCheck), capped so it can't overflow the coin math.
    private static long ParseAmount(string? s)
    {
        long v = 0;
        if (s is not null)
            foreach (char ch in s)
                if (char.IsDigit(ch)) { v = v * 10 + (ch - '0'); if (v > BankMax) return BankMax; }
        return v;
    }

    // Portrait = the NPC's creature sprite drawn from Monster.epf — the SAME 0x8000|look encoding the on-map
    // spawn uses (RTK clif.c:3190 sends the NPC graphic as look+32768). The dialog's kind-1 "npc gfx" range
    // is exactly [32768, 49151], so an encoded creature look lands there; a look of 0 -> no portrait.
    private static ushort NpcPortrait(Mob npc) => npc.Sprite == 0 ? (ushort)0 : (ushort)(0x8000 | npc.Sprite);

    // 0x30 clif_scriptmenuseq (type-0, graphic head): a text prompt + picker buttons. Same frame mapping as
    // SendScriptMessage (RTK WFIFO(fd,N) -> body[N-5]); the menu differs only in the kind bytes
    // (body[0..1] = 02 02, RTK WFIFOB(5)=WFIFOB(6)=2) and the item list appended after the prompt:
    //   body[23+L] = item count (u8), then each item = len(u8) + ASCII text, contiguous.
    private void SendNpcMenu(Mob npc, string prompt, IReadOnlyList<string> options)
    {
        ushort gfx = NpcPortrait(npc);
        byte head = gfx == 0 ? (byte)0 : gfx >= 49152 ? (byte)2 : (byte)1;
        byte color = npc.Color;
        var pr = Encoding.ASCII.GetBytes(prompt);

        var d = new List<byte>();
        d.Add(0x02); d.Add(0x02);          // [0..1] kind = menu (RTK WFIFOB(5)=2, WFIFOB(6)=2)
        d.AddRange(Be32(npc.Id));          // [2..5] npc entity id
        d.Add(head);                       // [6]   head kind
        d.Add(1);                          // [7]
        d.AddRange(Be(gfx));               // [8..9] portrait graphic
        d.Add(color);                      // [10]  portrait palette
        d.Add(1);                          // [11]
        d.AddRange(Be(gfx));               // [12..13]
        d.Add(color);                      // [14]
        d.AddRange(Be32(1));               // [15..18]
        d.Add(0);                          // [19] prev button
        d.Add(0);                          // [20] next button
        d.AddRange(Be((ushort)pr.Length)); // [21..22] prompt length
        d.AddRange(pr);                    // [23..] prompt text
        d.Add((byte)options.Count);        // [23+L] menu item count
        foreach (var label in options)
        {
            var ob = Encoding.ASCII.GetBytes(label);
            d.Add((byte)ob.Length);
            d.AddRange(ob);
        }
        SendMap(0x30, _gameInc++, d.ToArray(), $"npc-menu(0x30) id={npc.Id} x{options.Count}");
    }

    // 0x30 clif_inputseq (type-0, graphic head): a free-text entry box. Same head as the menu; kind bytes
    // are 04 04 (RTK WFIFOB(5)=WFIFOB(6)=4). After the prompt come RTK's secondary lines we don't use:
    //   [+1] dialog2 len(=0)   [+1] '*' separator(42)   [+1] dialog3 len(=0)   [+2] trailing (0,0).
    // The client returns the text via 0x3A kind 4 (HandleNpcDialog -> DlgInput).
    private void SendInputBox(Mob npc, string prompt)
    {
        ushort gfx = NpcPortrait(npc);
        byte head = gfx == 0 ? (byte)0 : gfx >= 49152 ? (byte)2 : (byte)1;
        byte color = npc.Color;
        var pr = Encoding.ASCII.GetBytes(prompt);

        var d = new List<byte>();
        d.Add(0x04); d.Add(0x04);          // [0..1] kind = input (RTK WFIFOB(5)=WFIFOB(6)=4)
        d.AddRange(Be32(npc.Id));          // [2..5] npc entity id
        d.Add(head);                       // [6]   head kind
        d.Add(1);                          // [7]
        d.AddRange(Be(gfx));               // [8..9] portrait graphic
        d.Add(color);                      // [10]  portrait palette
        d.Add(1);                          // [11]
        d.AddRange(Be(gfx));               // [12..13]
        d.Add(color);                      // [14]
        d.AddRange(Be32(1));               // [15..18]
        d.Add(0);                          // [19] prev button
        d.Add(0);                          // [20] next button
        d.AddRange(Be((ushort)pr.Length)); // [21..22] prompt length
        d.AddRange(pr);                    // [23..] prompt text
        d.Add(0);                          // dialog2 length (unused)
        d.Add(42);                         // '*' separator
        d.Add(0);                          // dialog3 length (unused)
        d.Add(0); d.Add(0);                // trailing pad (RTK advances len by +3 past dialog3)
        SendMap(0x30, _gameInc++, d.ToArray(), $"npc-input(0x30) id={npc.Id}");
    }

    // 0x30 clif_scriptmes (type-0, graphic head): a plain NPC text box. Ported from RTK clif.c; the RTK
    // WFIFO(fd,N) offsets map to this server's body[N-5] (frame = AA len len opcode inc, body at wire+5).
    //   body[0..1] u16=1   [2..5] npc id(u32BE)   [6] head kind(0 none/1 npc gfx/2 item gfx)   [7]=1
    //   [8..9] gfx(u16BE)   [10] color   [11]=1   [12..13] gfx   [14] color   [15..18] u32=1
    //   [19] prev-button    [20] next-button   [21..22] msg len(u16BE)   [23..] msg (ASCII)
    // prev/next 0 => a single OK/close box; the client answers a close with 0x3A kind 1 (HandleNpcDialog).
    private void SendScriptMessage(uint npcId, string msg, ushort gfx, byte color,
                                   bool prev = false, bool next = false)
    {
        // Head kind, classified from the graphic id exactly as RTK does (clif_scriptmes): 0 -> none,
        // >=49152 -> item gfx (kind 2), else -> npc/creature gfx (kind 1).
        byte head = gfx == 0 ? (byte)0 : gfx >= 49152 ? (byte)2 : (byte)1;
        var m = Encoding.ASCII.GetBytes(msg);
        var d = new List<byte>();
        d.AddRange(Be(1));                 // [0..1] type/count = 1
        d.AddRange(Be32(npcId));           // [2..5] npc entity id
        d.Add(head);                       // [6]   head kind
        d.Add(1);                          // [7]
        d.AddRange(Be(gfx));               // [8..9] portrait graphic
        d.Add(color);                      // [10]  portrait palette
        d.Add(1);                          // [11]
        d.AddRange(Be(gfx));               // [12..13]
        d.Add(color);                      // [14]
        d.AddRange(Be32(1));               // [15..18]
        d.Add((byte)(prev ? 1 : 0));       // [19] prev button
        d.Add((byte)(next ? 1 : 0));       // [20] next button
        d.AddRange(Be((ushort)m.Length));  // [21..22] message length
        d.AddRange(m);                     // [23..] message text
        SendMap(0x30, _gameInc++, d.ToArray(), $"npc-dialog(0x30) id={npcId} {m.Length}B");
    }

    // 0x3A = the client's reply to a 0x30 we sent (RTK clif_parsenpcdialog). body[0]=kind (01 text/close,
    // 02 menu pick, 04 input), [8]=step, [10]=menu index (1-based) or input length, [11..]=input text. We
    // just complete the prompt that's awaiting a reply; the suspended behaviour resumes and drives what's
    // next (nested menu, purchase, loop back). No routing table here — the await IS the continuation.
    private void HandleNpcDialog(byte[] dec)
    {
        byte kind = dec.Length > 0 ? dec[0] : (byte)0;
        int step = dec.Length > 8 ? dec[8] : 0;
        int menuOrLen = dec.Length > 10 ? dec[10] : 0;
        string input = "";
        if (kind == 0x04 && dec.Length > 11)   // input box returned text
        {
            int n = Math.Min(menuOrLen, dec.Length - 11);
            if (n > 0) input = Encoding.ASCII.GetString(dec, 11, n);
        }
        Log.Info($"   -> NPC-DIALOG (0x3A) kind={kind} step={step} menu/len={menuOrLen}" +
                 (input.Length > 0 ? $" input='{input}'" : ""));

        var tcs = _dlgReply;
        _dlgReply = null;
        tcs?.TrySetResult(new DialogReply(kind, step, menuOrLen, input));
    }

    // The client sends 0x4F when the player saves their profile from the edit box. Body (matches the
    // client's own change-profile parse): [picSize u16BE][picSize bytes][blurbLen u8][blurb bytes][00].
    // We persist both so a later click (0x34) shows the player's own words + drawing.
    private void HandleChangeProfile(byte[] dec)
    {
        if (dec.Length < 3) return;
        int picLen = (dec[0] << 8) | dec[1];
        int off = 2;
        if (picLen > 0 && off + picLen <= dec.Length)
        {
            _char.ProfilePic = dec[off..(off + picLen)];
            off += picLen;
        }
        else
        {
            _char.ProfilePic = null;
        }

        if (off < dec.Length)
        {
            int tlen = dec[off++];
            if (tlen >= 0 && off + tlen <= dec.Length)
                _char.ProfileText = Encoding.ASCII.GetString(dec, off, tlen);
        }

        if (_enteredWorld) _store.Save(_char);
        Log.Info($"   -> CHANGE-PROFILE (0x4F) saved: pic={_char.ProfilePic?.Length ?? 0}B text=\"{_char.ProfileText}\"");
        SendMessage("Your profile has been saved.");
    }

    // 0x39 self-profile ("Mind's Eye"). Layout decoded from the 7.x clif_mystaytus builder and confirmed
    // against a real 6.x capture (jeedee/TkServer) that decrypts to this exact shape (AC=99, class
    // "Peasant", legend "Born in Hyul 31, Winter"). Body:
    //   [AC u8][dam u8][hit u8]
    //   [clan  : len u8 + bytes]        (len 0 = clanless)
    //   [clanTitle : len u8 + bytes]
    //   [title : len u8 + bytes]
    //   [spouse : len u8 + bytes]
    //   [group u8]  [TNL u32BE]
    //   [className : len u8 + bytes]
    //   14 × equip slot (each 10 bytes, all zero = empty)
    //   [exchange u8]
    //   [0 u8] [legendCount u16BE]
    //   legendCount × { icon u8, color u8, textLen u8, text bytes }
    //
    // WIRE FORMAT (reverse-engineered from the client parser at 0x4732a0 — the mode-0 widget picked by the
    // shared profile dispatcher 0x424820; the mode-1/other-view widget 0x48b6a0 is a DIFFERENT, larger layout):
    //   [AC u8][dam u8][hit u8]
    //   [clan str][clanTitle str][title str][spouse str]       (each: u8 len + bytes)
    //   [group u8][TNL u32BE][className str]
    //   [g0 u16BE][g1 u16BE][g2 u16BE]                         (three portrait/graphic ids — see below)
    //   [box str]                                              (multi-line box; client maps TAB->CR)
    //   [flag u8]
    //   [legendCount u8]  then legendCount × { icon u8, color u8, len u8, text }
    // CRITICAL: 4.95 has NO packed equipment-icon array and the legend count is a single u8. The old code
    // sent a 6.x/RTK-shaped 14-cell/113-byte equip region (that fork has more item slots — hence the bigger
    // block); on 4.95 it pushed the legend count into the padding (count read as 0 -> no legends) and spilled
    // icons into the wrong fields (gear rendered in the wrong paperdoll slots). Proven by decoding a real 6.x
    // capture with this exact grammar: it aligns perfectly up to the legend count, then the 6.x equip block
    // remains unconsumed. The self paperdoll BODY is drawn from the live on-map character sprite, not this
    // packet, so g0/g1/g2 = 0 (default) exactly matches the known-good capture.
    private void SendSelfProfile()
    {
        var eq = Totals();                    // fold worn-gear bonuses + active buffs into the displayed AC/dam/hit
        var d = new List<byte>();
        d.Add((byte)(sbyte)Math.Clamp(_char.Ac - eq.armor, -128, 127));   // AC: lower is better, armor subtracts
        d.Add((byte)Math.Clamp(_char.Dam + eq.dam, 0, 255));
        d.Add((byte)Math.Clamp(_char.Hit + eq.hit, 0, 255));
        AddLenStr(d, _char.ClanName);
        AddLenStr(d, _char.ClanTitle);
        AddLenStr(d, _char.Title);
        AddLenStr(d, _char.Spouse);
        d.Add(0);                       // group flag (not grouped)
        d.AddRange(Be32(_char.Tnl));    // experience to next level
        AddLenStr(d, _char.ClassName);

        // The three equipment ICON cells beside the doll: helm, left ring, right ring. These slots have no
        // character-sprite layer in 4.95, so the profile shows them as ground-icon boxes fed by these u16s.
        d.AddRange(Be(ProfileCellIcon(4)));   // helm  (wire slot 4)
        d.AddRange(Be(ProfileCellIcon(7)));   // left ring  (wire slot 7)
        d.AddRange(Be(ProfileCellIcon(8)));   // right ring (wire slot 8)

        // The multi-line text BOX under the character. The client converts TAB(0x09)->CR(0x0d), so tab-separated
        // entries become separate lines. This is the self-view's buff/effect box (issue #6): active buff/debuff
        // names + remaining seconds. Empty when nothing is active. (The other-view 0x34 puts the GEAR list here
        // instead; self-view = buffs, other-view = gear, exactly as requested.)
        AddLenStr(d, BuffBoxText());
        d.Add(0);                       // trailing flag before the legend list (client field +0x935; 0 in captures)

        var legs = _char.Legends ?? new List<Legend>();
        d.Add((byte)Math.Min(legs.Count, 255));   // legend count is a single u8 in 4.95 (NOT u16)
        foreach (var lg in legs)
        {
            var t = Encoding.ASCII.GetBytes(lg.Text ?? "");
            if (t.Length > 255) t = t[..255];
            d.Add(lg.Icon);
            d.Add(lg.Color);
            d.Add((byte)t.Length);
            d.AddRange(t);
        }

        SendMap(0x39, _gameInc++, d.ToArray(),
            $"self-profile(0x39) ac={_char.Ac} class='{_char.ClassName}' buffs={_buffs.Count} legends={legs.Count}");
    }

    // The self-view buff/effect box (issue #6): one tab-separated line per active buff/debuff with the remaining
    // time in seconds. Grouped by spell so a multi-stat buff shows once. The client turns the tabs into line
    // breaks (see SendSelfProfile). Reopening the profile re-reads the current durations.
    private string BuffBoxText()
    {
        long now = Environment.TickCount64;
        _buffs.RemoveAll(b => b.Expires <= now);
        var lines = _buffs
            .GroupBy(b => b.Key)
            .Select(g =>
            {
                int secs = (int)Math.Max(0, (g.Max(x => x.Expires) - now + 999) / 1000);
                var name = string.IsNullOrEmpty(g.First().Name) ? g.Key : g.First().Name;
                return $"{name} ({secs}s)";
            });
        return string.Join('\t', lines);
    }

    // length-prefixed ASCII string: [len u8][bytes]. Empty string -> a single 0 byte.
    private static void AddLenStr(List<byte> d, string? s)
    {
        var b = Encoding.ASCII.GetBytes(s ?? "");
        d.Add((byte)b.Length);
        d.AddRange(b);
    }

    // "!leg" — replay the EXACT 0x39 self-profile captured from a real 6.x server (jeedee/TkServer),
    // decrypted with the shared NexonInc cipher. Known-good content: AC 99, class "Peasant", legend
    // "Born in Hyul 31, Winter". If the 4.95 profile window opens and shows these, the format is shared
    // and our native SendSelfProfile is correct; if it garbles, we diff against this capture.
    private static readonly byte[] Profile6x =
    {
        0x63, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x2b, 0x07,
        0x50, 0x65, 0x61, 0x73, 0x61, 0x6e, 0x74,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x80, 0x17,
        0x42, 0x6f, 0x72, 0x6e, 0x20, 0x69, 0x6e, 0x20, 0x48, 0x79, 0x75, 0x6c, 0x20, 0x33, 0x31, 0x2c,
        0x20, 0x57, 0x69, 0x6e, 0x74, 0x65, 0x72,
    };

    private void SendProfileReplay6x()
    {
        SendMap(0x39, _gameInc++, Profile6x, "replay6x-profile(0x39)");
        Log.Info("   -> REPLAY 6.x self-profile on 0x39 (expect: AC 99, class Peasant, legend 'Born in Hyul 31, Winter')");
    }

    // 0x34 = the "click" profile: the public view shown when you click a character. Distinct from the
    // profile-key window (0x39, stats/legend), it carries the character PORTRAIT, the writable profile
    // TEXT + PICTURE, nation, and legend. Layout REVERSED from the 4.95 client's own parser (0x48b6a0,
    // profile-page vtable+0x5c) — NOT the 7.x clif_clickonplayer, which is a different, much larger shape.
    // All multi-byte ints are BIG-ENDIAN. Body (after opcode/increment):
    //   5 header strings (u8 len + bytes): title, clan, clanTitle, class, name  (order confirmed live)
    //   appearance: tag u8 (=0) + 7 look bytes (same 7-byte form as 0x33 type-0)
    //   3 × portrait graphic id (u16BE) -> FACE.EPF
    //   profile TEXT blurb (u8 len + bytes)
    //   numeric attr (u32BE)   look-selector A (u8)   look-selector B (u8)   NATION (u8)
    //   profile PICTURE (u16BE len + bytes)
    //   legend count (u8) + legends { icon u8, color u8, textLen u8, text }
    // NOTE: 4.95's click popup has NO totem slot (TOTEM.EPF is unreferenced in the client).
    private void SendClickProfile(uint targetId)
    {
        var d = new List<byte>();

        // header strings — order pinned by the marker test (each renders in its labeled slot)
        AddLenStr(d, _char.Title);
        AddLenStr(d, _char.ClanName);
        AddLenStr(d, _char.ClanTitle);
        AddLenStr(d, _char.ClassName);
        AddLenStr(d, _char.Name);

        // appearance descriptor — tag 0 selects the 7-byte player look (identical to 0x33 self-look,
        // which already renders this character correctly): [sex, form, face, armor, 0, 0, 0]
        d.Add(0);
        d.AddRange(new byte[] { (byte)_char.Sex, 0, (byte)_char.Face, _char.Armor, 0, WeaponLook(), ShieldLook() });

        // three equipment ICON cells beside the doll: helm, left ring, right ring (no sprite layer for these
        // in 4.95, so they render as ground-icon boxes). Same IconWire encoding as the 0x37 equip window.
        d.AddRange(Be(ProfileCellIcon(4)));   // helm  (wire slot 4)
        d.AddRange(Be(ProfileCellIcon(7)));   // left ring  (wire slot 7)
        d.AddRange(Be(ProfileCellIcon(8)));   // right ring (wire slot 8)

        // FIELD #10 — PAGE-1 gear/item list (u8 len + text). Item names are TAB-separated (client
        // converts 0x09 -> CR for multiline). Empty until inventory/equipment exists.
        AddLenStr(d, GearListText());

        d.AddRange(Be32(0));      // numeric scalar — unknown, 0 for now
        d.Add(0xFF);              // look-selector A (0xff = none)
        d.Add(0xFF);              // look-selector B (0xff = none)
        d.Add(_char.Nation);      // nation index -> NATION_E.EPF

        // FIELD #15 — profile PICTURE bitmap: u16BE size + bytes (empty = 00 00)
        var pic = _char.ProfilePic ?? Array.Empty<byte>();
        d.AddRange(Be((ushort)pic.Length));
        d.AddRange(pic);

        // FIELD #16 — PAGE-2 writable profile BLURB (u8 len + text). This is the free-text box, a
        // SEPARATE field from the page-1 gear list. Omitting it desyncs the legend count.
        var blurb = Encoding.ASCII.GetBytes(_char.ProfileText ?? "");
        if (blurb.Length > 255) blurb = blurb[..255];
        d.Add((byte)blurb.Length);
        d.AddRange(blurb);

        // FIELD #17/#18 — legends: count u8, then each { icon u8, color u8, textLen u8, text }
        var legs = _char.Legends ?? new List<Legend>();
        d.Add((byte)Math.Min(legs.Count, 255));
        foreach (var lg in legs)
        {
            var t = Encoding.ASCII.GetBytes(lg.Text ?? "");
            if (t.Length > 255) t = t[..255];
            d.Add(lg.Icon);
            d.Add(lg.Color);
            d.Add((byte)t.Length);
            d.AddRange(t);
        }

        SendMap(0x34, _gameInc++, d.ToArray(), $"click-profile(0x34) id={targetId} nation={_char.Nation} blurb={blurb.Length}B legends={legs.Count}");
    }

    // Page-1 gear/item list for the click profile (the "inspect another player" view): the names of every
    // worn item, TAB-separated (the client turns 0x09 -> CR, one per line). This is the equipment list that
    // shows below the portrait when you click a character. Ordered by the canonical equip-slot byte so the
    // list reads weapon → armour → shield → helm → … regardless of the order items were put on.
    private string GearListText()
    {
        var names = _char.Equipment
            .Select(e => (worn: e, def: Content.ItemById(e.ItemId)))
            .Where(x => x.def is not null)
            .OrderBy(x => x.def!.EquipSlot)
            .Select(x => string.IsNullOrEmpty(x.worn.CustomName) ? x.def!.Name : x.worn.CustomName);
        return string.Join('\t', names);
    }

    // "!ckm" — send a 0x34 click-profile with DISTINCT MARKER strings in every text field, so we can
    // read off which window slot each field lands in and pin the true 4.95 layout (the 7.x port
    // misaligns). Numeric appearance (nation/totem/sprite) is handled by the parser RE separately.
    private void SendClickMarker()
    {
        var save = (_char.Title, _char.ClanName, _char.ClanTitle, _char.ClassName, _char.Name, _char.ProfileText, _char.Legends);
        _char.Title     = "TTL";
        _char.ClanName  = "CLAN";
        _char.ClanTitle = "CRANK";
        _char.ClassName = "CLASS";
        _char.Name      = "NAME";
        _char.ProfileText = "BLURBTEXT";
        _char.Legends   = new List<Legend> { new Legend(0, 0, "LEGEND") };
        SendClickProfile(_char.Id);
        (_char.Title, _char.ClanName, _char.ClanTitle, _char.ClassName, _char.Name, _char.ProfileText, _char.Legends) = save;
        Log.Info("   -> MARKER click-profile sent (TTL/CLAN/CRANK/CLASS/NAME/BLURBTEXT/LEGEND)");
    }

    /// <summary>Build an encrypted game packet, send it, and log it.</summary>
    private void SendMap(byte opcode, byte inc, byte[] data, string label)
    {
        var pkt = MapBuild(opcode, inc, data);
        Send(pkt);
        Log.Info($"   -> {label}: {pkt.Length}B  {Log.Hex(pkt)}");
    }

    private static byte[] Be32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private void SendMapInfo(ushort mapId, ushort xs, ushort ys, string title, ushort light, byte inc = 0)
    {
        var t = Encoding.ASCII.GetBytes(title);
        var b = new List<byte>();
        b.AddRange(Be(mapId));
        b.AddRange(Be(xs));
        b.AddRange(Be(ys));
        b.Add(5);            // flag
        b.Add(_realm);       // realm-center camera lock (0=off edge-aware, 1=on centered); toggled by F4
        b.Add((byte)t.Length);
        b.AddRange(t);
        // light field — encoding chosen by NEXUS_LIGHT_FMT so 5.33's parse can be probed live.
        var lv = LightValue;
        switch (LightFmt)
        {
            case "u8":    b.Add((byte)(lv & 0xFF)); break;                 // single byte (5.x may have narrowed it)
            case "leu16": b.Add((byte)(lv & 0xFF)); b.Add((byte)(lv >> 8)); break;  // little-endian u16
            default:      b.AddRange(Be((ushort)lv)); break;              // big-endian u16 (4.95-proven)
        }
        Log.Info($"   -> mapinfo(0x15) light={lv} fmt={LightFmt}");
        Send(MapBuild(Opcode.MapInfo, inc, b.ToArray()));
    }

    // In-world command feedback that lands in the CHAT LOG. The client's chat pane + over-head bubbles
    // are both driven by 0x0D speech (RE: handler 0x450170 → 0x44dc90 registers a 3s text object into the
    // world message-manager at world+0x418). The 0x02 SendMessage path is a login-style message BOX that
    // doesn't stack for multi-line output (why !maps/!mobs showed nothing). So command results speak as
    // the player's own entity → one chat-log line each. ASCII, clamped to the 0x0D u8 length field.
    private void SendLog(string text)
    {
        if (text.Length > 250) text = text[..250];
        SendSpeech(0, _char.Id, Encoding.ASCII.GetBytes(text));
    }

    // ---- helpers ----
    private void SendMessage(string text)
    {
        var t = Encoding.ASCII.GetBytes(text);
        var body = new List<byte> { 0x0F, (byte)t.Length };
        body.AddRange(t);
        body.Add(0);
        var enc = TkCrypt.Crypt(body.ToArray(), 0x02, TkCrypt.LoginKey);
        Send(TkPacket.Build(0x02, 0x02, enc));
        Log.Info($"   -> message: {text}");
    }

    /// <summary>
    /// Game packet: AA | len(u16 BE) | op | inc | body. The body is encrypted with the SAME
    /// simple NexonInc cipher as the login channel — confirmed by reversing NexusTK.exe: 4.95
    /// has ONE cipher (decrypt 0x478680 / key buffer 0x50211c built only from "NexonInc.",
    /// keylen 9, identity table 0x4f3358). No name-derived/table cipher, no 3 trailer bytes —
    /// those are 7.x-only and were the bug in the previous version of this method.
    /// </summary>
    private byte[] MapBuild(byte opcode, byte inc, byte[] data)
    {
        var enc = TkCrypt.Crypt(data, inc, TkCrypt.LoginKey);
        return TkPacket.Build(opcode, inc, enc);
    }

    private static byte[] Be(ushort v) => new[] { (byte)(v >> 8), (byte)(v & 0xFF) };

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
        new(_char.Id, _char.X, _char.Y, _facing, (byte)_char.Sex, (byte)_char.Face, _char.Armor, WeaponLook(), ShieldLook(), _char.Mounted, _char.Name);

    /// <summary>Draw player <paramref name="other"/> on our client (0x33 player look form).</summary>
    public void ShowPlayer(Session other)
    {
        var s = other.Snapshot();
        var app = new byte[] { s.Sex, (byte)(s.Mounted ? 3 : 0), s.Face, s.Armor, 0, s.Weapon, s.Shield };   // same layout as SendSelfLook ([1]=form: 3=mounted)
        SendLook(s.Id, s.X, s.Y, s.Dir, app, renderKind: 1, s.Name, $"peer(0x33) id={s.Id} '{s.Name}'");
    }

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

    // Locked so the World's mob-AI thread and peer broadcasts can't interleave bytes mid-packet with this
    // session's own read-loop on the shared stream. (The `_gameInc++` at call sites is a benign nonce and
    // not guarded; a rare duplicate is harmless since each packet carries its own inc in the header.)
    private void Send(byte[] data) { lock (_sendLock) _stream.Write(data, 0, data.Length); }
}
