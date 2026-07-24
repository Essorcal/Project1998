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
    private string _user = "?";
    private bool _enteredWorld;   // true once world entry loaded _char; gates the disconnect save

    // --- world-light diagnostic knobs (env-tweakable, no rebuild needed to sweep) ---
    //   NEXUS_LIGHT      integer 0..65535, the map light/darkness value (default 232, proven bright on 4.95)
    //   NEXUS_LIGHT_FMT  how to encode it on the 0x15: "beu16" (default, 4.95), "leu16", or "u8"
    // 5.33 draws terrain black with the 4.95-proven be-u16 232; sweeping these isolates whether the
    // client reads the light field at a different width/endianness (leading 00 -> light 0 -> black).
    private static readonly int LightValue =
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_LIGHT"), out var lv) ? lv : 232;
    private static readonly string LightFmt =
        (Environment.GetEnvironmentVariable("NEXUS_LIGHT_FMT") ?? "beu16").Trim().ToLowerInvariant();

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

    public Session(TcpClient client, int port, CharacterStore store)
    {
        _client = client;
        _stream = client.GetStream();
        _port = port;
        _store = store;
        _remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
    }

    public async Task RunAsync()
    {
        Log.Info($"++ CONNECT from {_remote} on port {_port}");
        try
        {
            if (_port == 2000)   // login channel: send the 0x7E welcome
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
            case 0x06:                    HandleWalk(dec); break;   // client walk+sync (every few steps) -> same
            case 0x0E:                    HandleChat(dec); break;   // client chat -> echo as over-head speech
            case 0x13:                    HandleAttack(dec); break;  // client attack (spacebar) -> echo 0x13 anim
            case 0x2D:                    HandleProfileRequest(dec); break;  // profile key -> self-profile (0x39)
            case 0x43:                    HandleClickInfo(dec); break;       // click entity -> profile/inspect
            case 0x4F:                    HandleChangeProfile(dec); break;   // edit profile -> save pic + blurb
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

        // handoff: send the client to the game server (reversed IP octets + port)
        byte[] ip = { 127, 0, 0, 1 };
        const int gport = 2005;
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
        Log.Info(loaded is null
            ? $"   -> ARRIVAL user='{_user}' — no saved character, using default spawn"
            : $"   -> ARRIVAL user='{_user}' — loaded saved character at map {_char.Map} ({_char.X},{_char.Y})");

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
    }

    private Character _char = new();
    private byte[] _encTable = Array.Empty<byte>();
    private byte _gameInc = 0;   // per-packet increment for game-channel sends

    // Live creatures the player can fight. Server-authoritative HP; the client only draws them.
    // Populated by the mob commands (!mob/!mobrow/!spawn); entries are removed on death (0x0E).
    private readonly List<Mob> _mobs = new();
    private uint _nextMobId = 5000;      // entity-id pool for spawned creatures (well above the self id)
    private byte _facing = 0;            // last direction the player faced (0=N 1=E 2=S 3=W); drives melee

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
        var d = new byte[58];
        d[0] = 0x78;                        // flags: full-stats form
        d[1] = _char.Nation;
        d[2] = _char.Totem;
        d[4] = _char.Level;
        WriteBe32(d, 5, _char.MaxHp);       // maxHP  (offset [5] confirmed via !hp bar-fill test)
        WriteBe32(d, 9, _char.MaxMp);       // maxMP  (offset [9] confirmed)
        d[13] = _char.Might;
        d[14] = _char.Will;
        d[17] = _char.Grace;
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
        var app = new byte[] { (byte)_char.Sex, 0, (byte)_char.Face, (byte)_char.Armor, 0, _char.Weapon, 0 };
        SendLook(_char.Id, _char.X, _char.Y, dir: _facing, app, renderKind: 1, _char.Name, "self(0x33)");
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

    // 0x29 floating number (handler 0x4504b0 -> 0x44e0a0): entityId(u32BE) number(u8) A/B/C(u16BE).
    // The u8 is what gets formatted to text over the entity (0..255 — fine for damage); A*1000 feeds
    // the pop animation offset, B/C style. We send A/B/C = 0 for a plain centered number.
    private void SendNumber(uint id, byte number)
    {
        var d = new List<byte>();
        d.AddRange(Be32(id));
        d.Add(number);
        d.AddRange(Be(0));           // A
        d.AddRange(Be(0));           // B
        d.AddRange(Be(0));           // C
        SendMap(0x29, _gameInc++, d.ToArray(), $"number(0x29) id={id} n={number}");
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

    // Screen tile where the self is drawn (viewport anchor). Camera scroll = (X-_scrX, Y-_scrY),
    // so keeping this constant makes the camera follow the player as it walks. Equals the spawn tile.
    private ushort _scrX = 5, _scrY = 5;

    // 0x04: absolute self (X,Y) + screen anchor. The handler (0x44faf0) sets camera scroll via
    // 0x44c660 AND calls 0x44b140 on the self entity, which advances/commits the self's walk that
    // 0x0C started. Sent at spawn and after every walk step.
    private void SendXy()
    {
        var d = new List<byte>();
        d.AddRange(Be(_char.X));
        d.AddRange(Be(_char.Y));
        d.AddRange(Be(_scrX));
        d.AddRange(Be(_scrY));
        d.Add(0);
        SendMap(0x04, _gameInc++, d.ToArray(),
                $"xy(0x04) pos=({_char.X},{_char.Y}) scroll=({_char.X - _scrX},{_char.Y - _scrY})");
    }

    // ---- live world interaction (client is in-world, sending its own packets) ----

    // Client walk request (0x32). The client predicts one step then blocks until the server
    // confirms the move; we reply 0x0C so it keeps walking. Direction is the first byte
    // (NexusTK: 0=N,1=E,2=S,3=W). We track position server-side and echo the new tile.
    private void HandleWalk(byte[] dec)
    {
        byte dir = dec.Length > 0 ? dec[0] : (byte)0;
        _facing = (byte)(dir & 3);   // remember which way we're facing so melee (0x13) knows the front tile
        int nx = _char.X, ny = _char.Y;
        switch (dir & 3)
        {
            case 0: ny -= 1; break;  // north
            case 1: nx += 1; break;  // east
            case 2: ny += 1; break;  // south
            case 3: nx -= 1; break;  // west
        }
        // clamp inside the map so we never walk off the tile grid
        nx = Math.Clamp(nx, 0, _char.MapXs - 1);
        ny = Math.Clamp(ny, 0, _char.MapYs - 1);
        _char.X = (ushort)nx;
        _char.Y = (ushort)ny;
        SendMove(_char.Id, _char.X, _char.Y, dir);   // 0x0C: start the self walk animation (sets dir + walking flag)
        SendXy();                                    // 0x04: commit the step (0x44b140) + scroll camera to follow
        Log.Info($"   -> walk dir={dir} -> ({_char.X},{_char.Y})");
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
        // ---- REAL monsters via 0x07 (Monster.epf). check !crow before !cre ----
        if (text.StartsWith("!crow", StringComparison.OrdinalIgnoreCase)) { CreatureRow(text); return; }  // sweep monster look ids
        if (text.StartsWith("!cre", StringComparison.OrdinalIgnoreCase)) { CreatureOne(text); return; }    // spawn one real monster
        // ---- mobs / combat (check !mobrow before !mob, !spawn before the catch-all !s) ----
        if (text.StartsWith("!mobrow", StringComparison.OrdinalIgnoreCase)) { MobRow(text); return; }   // sweep graphic ids
        if (text.StartsWith("!mob", StringComparison.OrdinalIgnoreCase)) { MobOne(text); return; }       // spawn one creature
        if (text.StartsWith("!kill", StringComparison.OrdinalIgnoreCase)) { KillMobs(); return; }         // despawn all mobs
        if (text.StartsWith("!weapon", StringComparison.OrdinalIgnoreCase)) { SetWeapon(text); return; }  // equip weapon sprite
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

        SendSpeech(chatType, _char.Id, msg);
        Log.Info($"   -> speech type={chatType}: \"{text}\"");
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

    // "!row i lo hi": sweep appearance byte [i] from lo..hi across a west->east row of dummies, all
    // other bytes 0. One screenshot then maps that byte's entire id space.
    private void LookRow(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int idx = parts.Length > 1 && int.TryParse(parts[1], out var pi) ? Math.Clamp(pi, 0, 6) : 0;
        int lo = parts.Length > 2 && int.TryParse(parts[2], out var pl) ? pl : 0;
        int hi = parts.Length > 3 && int.TryParse(parts[3], out var ph) ? ph : lo + 7;
        ushort y = (ushort)Math.Clamp(_char.Y - 2, 0, _char.MapYs - 1);
        int col = 0;
        for (int v = lo; v <= hi && col < 12; v++, col++)
        {
            // Base = valid body (0)=1, normal form (1)=0, so sweeping [2..6] reads cleanly instead of
            // being blanked by the form/state byte. appearance[1] itself is the form table (0/4 normal,
            // 1 ghost, 3 mounted, 5 invisible-spell, most others = no sprite).
            var app = new byte[] { 1, 0, 0, 0, 0, 0, 0 };
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

    // "!cre <lookId> [hp]": spawn ONE real monster (Monster.epf, via 0x07) on the tile in front of you,
    // so you can see it AND immediately melee it (combat is unchanged — it hits any Mob on the tile).
    private void CreatureOne(string text)
    {
        var a = ParseInts(text);
        int look = a.Length > 0 ? a[0] : 0;
        int hp = a.Length > 1 ? a[1] : 6;
        var (fx, fy) = FrontTile();
        ushort x = (ushort)Math.Clamp(fx, 0, _char.MapXs - 1);
        ushort y = (ushort)Math.Clamp(fy, 0, _char.MapYs - 1);
        SpawnMonster((ushort)look, x, y, $"c{look}", hp, dir: (byte)((_facing + 2) & 3));
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
        if (_mobs.Count == 0) { SendMessage("no mobs to clear"); return; }
        SendDespawn(_mobs.Select(m => m.Id).ToArray());
        int n = _mobs.Count;
        _mobs.Clear();
        SendMessage($"cleared {n} mob(s)");
        Log.Info($"   -> KILL: despawned {n} mobs");
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
        SendAction(_char.Id, type: 1, time: 8, param: 0);   // type 1 = attack swing

        // Melee resolves against whatever creature stands on the tile directly in front of us
        // (facing tracked from the last walk step). Server-authoritative: we own the mob's HP.
        var (fx, fy) = FrontTile();
        var mob = MobAt(fx, fy);
        if (mob is null) return;

        int dmg = Math.Max(1, _char.Might + (_char.Weapon > 0 ? 3 : 0));   // might + a flat weapon bonus
        mob.Hp -= dmg;
        SendNumber(mob.Id, (byte)Math.Min(dmg, 255));                      // floating "-N" over the mob
        Log.Info($"   -> hit mob {mob.Id} '{mob.Name}' for {dmg} -> {mob.Hp}/{mob.MaxHp}");

        if (!mob.Alive)
        {
            _mobs.Remove(mob);
            SendDespawn(mob.Id);                       // 0x0E: remove the corpse from the client
            _char.Exp += (uint)mob.MaxHp;              // reward: exp equal to the mob's max HP
            SendStats();                               // refresh the HUD exp bar
            SendMessage($"You defeated {mob.Name}. (+{mob.MaxHp} exp)");
            Log.Info($"   -> mob {mob.Id} '{mob.Name}' defeated");
        }
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

    // ---- profile window (the "Mind's Eye") ----
    // The client opens the self-profile window when the profile key is pressed by sending 0x2D. Byte 0
    // == 0 is the self-profile request (byte != 0 is group status in 7.x). We reply with 0x39, the
    // self-profile packet (clif_mystaytus): AC/clan/title/class/legend. Without this reply the window
    // never appears — that's the bug the user hit.
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
        // Click -> the public "profile" view (0x34): portrait + writable blurb, NOT the stats/legend
        // window (0x39). Only the self entity exists so far, so always show ours.
        SendClickProfile(id == 0 ? _char.Id : id);
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
    private void SendSelfProfile()
    {
        var d = new List<byte>();
        d.Add((byte)_char.Ac);
        d.Add(_char.Dam);
        d.Add(_char.Hit);
        AddLenStr(d, _char.ClanName);
        AddLenStr(d, _char.ClanTitle);
        AddLenStr(d, _char.Title);
        AddLenStr(d, _char.Spouse);
        d.Add(0);                       // group flag (not grouped)
        d.AddRange(Be32(_char.Tnl));    // experience to next level
        AddLenStr(d, _char.ClassName);

        for (int i = 0; i < 14; i++)    // 14 equipment slots, empty = 10 zero bytes each
            d.AddRange(new byte[10]);

        d.Add(0);                       // exchange flag

        var legs = _char.Legends ?? new List<Legend>();
        d.Add(0);                       // reserved
        d.AddRange(Be((ushort)legs.Count));
        foreach (var lg in legs)
        {
            var t = Encoding.ASCII.GetBytes(lg.Text ?? "");
            d.Add(lg.Icon);
            d.Add(lg.Color);
            d.Add((byte)t.Length);
            d.AddRange(t);
        }

        SendMap(0x39, _gameInc++, d.ToArray(), $"self-profile(0x39) title='{_char.Title}' ac={_char.Ac} legends={legs.Count}");
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
        d.AddRange(new byte[] { (byte)_char.Sex, 0, (byte)_char.Face, _char.Armor, 0, 0, 0 });

        // three portrait/face graphic ids (feed FACE.EPF). 0 = default face for now.
        d.AddRange(Be(0)); d.AddRange(Be(0)); d.AddRange(Be(0));

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

    // Page-1 gear/item list for the click profile: TAB-separated equipped-item names. No inventory
    // system yet, so this is empty (page 1 shows a blank item list, which is correct for a naked char).
    private string GearListText() => "";

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
        b.Add(0);            // realm
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

    private void Send(byte[] data) => _stream.Write(data, 0, data.Length);
}
