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

        Log.Info("   == entry sent: 0x02 trigger + 0x1E/0x20 acks + 0x05 id + 0x15 map + 0x04 xy + 0x33 self ==");
    }

    private Character _char = new();
    private byte[] _encTable = Array.Empty<byte>();
    private byte _gameInc = 0;   // per-packet increment for game-channel sends

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
        SendStatus();
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

    private void SendStatus()
    {
        var d = new List<byte>
        {
            0x1F,                       // flags (fullstats|hpmp|alwayson) — ROUGH guess
            0, _char.Nation, _char.Totem, 0, _char.Level
        };
        d.AddRange(Be32(_char.MaxHp));
        d.AddRange(Be32(_char.MaxMp));
        d.Add(_char.Might); d.Add(_char.Will); d.Add(3); d.Add(3); d.Add(_char.Grace);
        d.AddRange(new byte[] { 0, 0, _char.Armor, 0, 0, 0, 0, 0, 0, 0 });
        d.Add(_char.MaxInv);
        d.AddRange(Be32(_char.Hp));
        d.AddRange(Be32(_char.Mp));
        SendMap(0x08, 0, d.ToArray(), "status(0x08)");
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
        var app = new byte[] { (byte)_char.Sex, 0, (byte)_char.Face, (byte)_char.Armor, 0, 0, 0 };
        SendLook(_char.Id, _char.X, _char.Y, dir: 0, app, renderKind: 1, _char.Name, "self(0x33)");
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
        if (text.StartsWith("!sweep", StringComparison.OrdinalIgnoreCase)) { StatSweep(text); return; }
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

    // "!sweep" — walk the ranked candidate opcodes, one every ~600ms, each announced by an over-head
    // bubble and carrying level==opcode as its sentinel, so whatever value lands on the HUD names the
    // winning opcode. Watch the HUD while it runs; report which opcode (the level number) took effect.
    private void StatSweep(string text)
    {
        // Non-window, non-entity, substantial handlers first (most likely the always-on HUD), then the
        // window-openers that could be the self-profile panel.
        byte[] tier = { 0x2e, 0x4a, 0x19, 0x16, 0x49, 0x68, 0x3b, 0x66, 0x67, 0x34, 0x21, 0x42, 0x31, 0x2f, 0x30 };
        Log.Info($"   -> STAT SWEEP over {tier.Length} opcodes");
        foreach (var op in tier)
        {
            var label = Encoding.ASCII.GetBytes($"op 0x{op:x2}");
            SendSpeech(0, _char.Id, label);
            SendStatProbe(op, 0xFF, level: op);   // level sentinel == opcode
            System.Threading.Thread.Sleep(700);
        }
        SendSpeech(0, _char.Id, "sweep done"u8.ToArray());
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
        // XPMONEY block
        d.AddRange(Be32(54321));     // exp
        d.AddRange(Be32(777));       // money/coins
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
    }

    private void SendAction(uint id, byte type, ushort time, byte param)
    {
        var d = new List<byte>();
        d.AddRange(Be32(id));
        d.Add(type);
        d.AddRange(Be(time));
        d.Add(param);
        SendMap(0x1A, _gameInc++, d.ToArray(), $"action(0x1A) type={type} time={time}");
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
        b.AddRange(Be(light));
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
