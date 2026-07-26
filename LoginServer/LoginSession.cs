using System.Net.Sockets;
using System.Text;
using Protocol.Tk495;
using Shared;

namespace LoginServer;

/// <summary>
/// One login-channel connection: name-availability (0x02), account creation (0x04), and login (0x03)
/// which ends by handing the client off to the GAME server (a separate process). This is the whole
/// login protocol for 4.95 — there is no character-list step (the account name IS the character).
///
/// State round-trips to the game process through the shared character store on disk (soon SQLite): the
/// record written here at creation is re-read by the game server at world entry (0x10). Nothing else
/// transfers on the wire except the username + a handoff token.
/// </summary>
public sealed class LoginSession
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly int _port;
    private readonly string _remote;
    private readonly CharacterStore _store;
    private readonly object _sendLock = new();
    private string _user = "?";
    private string _pendingName = "";   // name from the availability check, fallback for creation
    private string _pendingPass = "";   // password from the availability check (0x02), used at creation (0x04)

    // AA 00 13 7E 1B "CONNECTED SERVER\n"  (plaintext welcome, as the 6.x reference sends on connect)
    private static readonly byte[] Welcome = BuildWelcome();

    // The game server the client is redirected to after a successful login. Defaults to loopback (login
    // and game on the same box); set NEXUS_GAME_HOST to the game server's public IP for a split
    // deployment. The client stores the host/port from our 0x03 reply, opens a FRESH connection to it,
    // and announces itself there with 0x10.
    private static readonly byte[] GameHost = ParseHost(Environment.GetEnvironmentVariable("NEXUS_GAME_HOST"));

    public LoginSession(TcpClient client, int port, CharacterStore store)
    {
        _client = client;
        _stream = client.GetStream();
        _port = port;
        _store = store;
        _remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
    }

    public async Task RunAsync()
    {
        Log.Info($"++ CONNECT from {_remote} on login port {_port}");
        try
        {
            await _stream.WriteAsync(Welcome);
            Log.Info($"   -> sent welcome ({Welcome.Length}B)");

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
            case Opcode.NameCheck:        NameAvailable(dec); break;
            case Opcode.CreateAppearance: HandleCreate(dec); break;
            case Opcode.Login:            HandleLogin(dec); break;
            // 0x62 signature / 0x00 version are sent by the client on connect; we don't need them.
            default:                      Log.Info($"   ?? no login handler for opcode 0x{pkt.Opcode:x2}"); break;
        }
    }

    // Create step 1 (0x02): the client asks whether a name is free. Body is the length-prefixed name.
    // We stash it so creation (0x04) can key the record even if that packet omits the name.
    private void NameAvailable(byte[] dec)
    {
        try
        {
            int nlen = dec.Length > 0 ? dec[0] : 0;
            if (nlen > 0 && 1 + nlen <= dec.Length)
            {
                _pendingName = Encoding.ASCII.GetString(dec, 1, nlen);
                _pendingPass = LoginAuth.ReadPassword(dec, 1 + nlen);   // 0x02 body: nameLen name pwLen pw 00 00 00
            }
        }
        catch { /* leave pending values as-is */ }

        Send(new byte[] { 0xAA, 0x00, 0x06, 0x02, 0x01, 0x4F, 0x64, 0x79, 0x6E });
        Log.Info($"   -> name available (pending='{_pendingName}')");
    }

    // Create step 2 (0x04): the client sends the chosen name + appearance (gender, etc.) after the
    // availability check. Persist it so world entry uses the player's real choices instead of the
    // hardcoded spawn. The appearance decode + home-city placement live in Shared/CharacterFactory so the
    // game server (which re-derives them at world entry) stays in lock-step with what we write here.
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

        var existing = _store.Load(name);
        var c = existing ?? new Character();
        c.Name = name;
        c.CreationBlob = dec;      // keep the raw body for future re-decoding if the mapping changes
        CharacterFactory.ApplyAppearance(c);        // decode gender/face/nation/totem/hair
        if (existing is null) CharacterFactory.PlaceNewCharacter(c);   // brand new -> home city for the picked nation
        _store.Save(c);
        // Register the account with the password captured at the availability check (0x02). If it was
        // missing for some reason, TOFU at first login (HandleLogin) still sets it.
        if (!string.IsNullOrEmpty(_pendingPass))
        {
            Accounts.SetPassword(name, Auth.Hash(_pendingPass));
            Log.Info($"   -> account '{name}' registered with a password hash");
        }
        Log.Info($"   -> CREATE persisted '{name}' (sex={c.Sex} face={c.Face} nation={Character.NationName(c.Nation)} totem={c.Totem}) -> {_store.Directory}");
        SendMessage("Account created.");
    }

    private void HandleLogin(byte[] dec)
    {
        int ulen = dec[0];
        _user = Encoding.ASCII.GetString(dec, 1, ulen);
        string pass = LoginAuth.ReadPassword(dec, 1 + ulen);   // 0x03 body: nameLen name pwLen pw 00

        // Authenticate (verify existing hash, or trust-on-first-use for a never-registered / legacy account
        // so existing characters aren't locked out). Shared rule so login + game re-login can't drift.
        if (!LoginAuth.Authenticate(_user, pass))
        {
            Log.Info($"   -> LOGIN REJECTED (incorrect password) for user='{_user}'");
            SendMessage("Incorrect password.");
            return;   // no handoff — the client stays on the login screen showing the message
        }
        Log.Info($"   -> LOGIN accepted for user='{_user}'");
        Accounts.TouchLogin(_user);

        // Mint a single-use handoff nonce (exactly 5 bytes = the client's echoed token slot) and record it
        // in the shared DB; the game server validates+consumes it on 0x10 arrival (Shared/HandoffTokens).
        var nonce = HandoffTokens.Mint(_user);

        // handoff: send the client to the GAME server (reversed IP octets + port). Keep the version's
        // channel together: a V533 login (port 2001) hands off to the V533 game port 2006; V495 -> 2005.
        int gport = _port == 2001 ? 2006 : 2005;
        var p = new List<byte>
        {
            0xAA, 0, 0, Opcode.Login,
            GameHost[3], GameHost[2], GameHost[1], GameHost[0],   // reversed octets, as the client expects
            (byte)(gport >> 8), (byte)(gport & 0xFF),
            23, 0, 9
        };
        p.AddRange(TkCrypt.LoginKey);
        var uname = Encoding.ASCII.GetBytes(_user);
        p.Add((byte)uname.Length);
        p.AddRange(uname);
        p.AddRange(nonce);   // 5-byte single-use handoff token (was the static {0,1,18,17,0}); echoed in 0x10
        p[2] = (byte)(p.Count - 3);
        Send(p.ToArray());
        Log.Info($"   -> game handoff -> {GameHost[0]}.{GameHost[1]}.{GameHost[2]}.{GameHost[3]}:{gport} (token minted {Log.Hex(nonce)})");
    }

    // Login-style single-line message box (server -> client 0x02 wrapping a 0x0F). Used for "Account
    // created." — the only status text the login channel emits.
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

    private void Send(byte[] data) { lock (_sendLock) _stream.Write(data, 0, data.Length); }

    private static byte[] BuildWelcome()
    {
        var head = new byte[] { 0xAA, 0x00, 0x13, 0x7E, 0x1B };
        var text = "CONNECTED SERVER\n"u8.ToArray();
        var all = new byte[head.Length + text.Length];
        head.CopyTo(all, 0);
        text.CopyTo(all, head.Length);
        return all;
    }

    // "a.b.c.d" -> 4 octets in normal order (the handoff packet reverses them). Falls back to loopback.
    private static byte[] ParseHost(string? host)
    {
        var def = new byte[] { 127, 0, 0, 1 };
        if (string.IsNullOrWhiteSpace(host)) return def;
        var parts = host.Split('.');
        if (parts.Length != 4) return def;
        var o = new byte[4];
        for (int i = 0; i < 4; i++)
            if (!byte.TryParse(parts[i], out o[i])) return def;
        return o;
    }
}
