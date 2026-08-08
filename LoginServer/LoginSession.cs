using System.Net;
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
    private readonly IPAddress _ip;   // source address, for the per-IP failed-login throttle
    private readonly CharacterStore _store;
    private readonly object _sendLock = new();
    private string _user = "?";
    private string _pendingName = "";   // name from the availability check, fallback for creation
    private string _pendingPass = "";   // password from the availability check (0x02), used at creation (0x04)

    // Slow-loris defense (the login port is the internet-facing front door): a connection must send its first
    // valid framed packet within this budget or it is dropped. Only the first packet is gated. Env-tunable,
    // shared with the game server's NEXUS_HANDSHAKE_MS; 15s is far more than a real client needs.
    private static readonly int HandshakeMs =
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_HANDSHAKE_MS"), out var hs) && hs > 0 ? hs : 15_000;
    private int _established;   // 0 until the first valid packet is parsed; gates the handshake timeout

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
        _ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
    }

    public async Task RunAsync()
    {
        Log.Info($"++ CONNECT from {_remote} on login port {_port}");
        try
        {
            await _stream.WriteAsync(Welcome);
            Log.Info($"   -> sent welcome ({Welcome.Length}B)");

            // Handshake watchdog: drop a connection that sends no valid packet within HandshakeMs. Gated on
            // _established so it can only ever close a still-silent connection; closing makes ReadAsync throw.
            using var handshake = new CancellationTokenSource(HandshakeMs);
            handshake.Token.Register(() =>
            {
                if (Volatile.Read(ref _established) != 0) return;
                Log.Info($"!! {_remote} handshake timeout ({HandshakeMs}ms) — no valid packet, dropping");
                try { _client.Close(); } catch { /* already gone */ }
            });

            var buf = new List<byte>();
            var tmp = new byte[4096];
            while (true)
            {
                int n = await _stream.ReadAsync(tmp);
                if (n == 0) break;
                // Wire dumps are OFF by default on this channel — these bytes contain the player's
                // password, and 4.95's cipher is a fixed published XOR, so "encrypted" is not a defense.
                // See Log.WireEnabled.
                if (Log.WireEnabled) Log.Info($"   <~ RAW {n}B on :{_port}: {Log.Hex(tmp[..n])}");
                for (int i = 0; i < n; i++) buf.Add(tmp[i]);

                var arr = buf.ToArray();
                int off = 0;
                while (arr.Length - off >= 5 && arr[off] == 0xAA)
                {
                    if (!TkPacket.TryParse(arr.AsSpan(off), out var pkt, out int consumed)) break;
                    off += consumed;
                    Handle(pkt);
                }
                if (off > 0)
                {
                    buf.RemoveRange(0, off);
                    Volatile.Write(ref _established, 1);   // first valid frame parsed -> handshake satisfied
                }
                if (buf.Count > 0 && Log.WireEnabled)
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
        if (Log.WireEnabled) Log.Info($"        dec : {Log.Hex(dec)}");   // contains the password — see Log.WireEnabled

        switch (pkt.Opcode)
        {
            case Opcode.NameCheck:        NameAvailable(dec); break;
            case Opcode.CreateAppearance: HandleCreate(dec); break;
            case Opcode.Login:            HandleLogin(dec); break;
            // 0x62 signature / 0x00 version are sent by the client on connect; we don't need them.
            default:                      Log.Info($"   ?? no login handler for opcode 0x{pkt.Opcode:x2}"); break;
        }
    }

    // Create step 1 (0x02): the client asks whether a name is free. Body is the length-prefixed name
    // (plus the chosen password — see the protocol doc §9). We stash both so creation (0x04) can key the
    // record even if that packet omits the name.
    //
    // This check is now REAL. It used to answer "available" unconditionally, which meant re-creating an
    // existing name walked straight into HandleCreate's load-then-overwrite path and RESET that character's
    // password — i.e. anyone could take over any account by "creating" it again. HandleCreate now refuses a
    // taken name outright (that refusal is the actual security boundary); this reply is the friendly half,
    // so the player is told at the name field instead of after picking a face.
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

        if (NameProblem(_pendingName) is { } why)
        {
            // Refuse by sending ONLY the message box (the same 0x02/0x0F path "Incorrect password." uses on
            // this screen, which the client is known to render without a paired protocol reply) and NOT the
            // availability OK, so the client can't advance to appearance selection.
            SendMessage(why);
            Log.Info($"   -> name REJECTED ('{_pendingName}'): {why}");
            return;
        }

        Send(new byte[] { 0xAA, 0x00, 0x06, 0x02, 0x01, 0x4F, 0x64, 0x79, 0x6E });
        Log.Info($"   -> name available (pending='{_pendingName}')");
    }

    // Shared name gate for the availability check and creation. Returns null if the name is usable, else the
    // player-facing reason. Both the `characters` and `accounts` tables key on CharacterStore.Key (lowercased,
    // non-alphanumerics stripped), so the character set has to be restricted to what survives that
    // normalization — otherwise "Bo b" and "Bob" would be the same account under two different display names.
    private static string? NameProblem(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Please enter a name.";
        if (name.Length < 3 || name.Length > 12) return "Names must be 3 to 12 characters.";
        foreach (var ch in name)
            if (!char.IsLetterOrDigit(ch) && ch != '_') return "Names may only use letters, numbers and _.";
        if (CharacterStore.CharacterExists(name) || Accounts.Exists(name)) return "That name is already taken.";
        return null;
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
        if (string.IsNullOrEmpty(name)) name = _pendingName;

        // Re-run the name gate here, not just at the availability check: 0x04 is a separate packet and a
        // hand-rolled client can send it without ever asking 0x02. Refusing a TAKEN name is what stops the
        // old load-then-overwrite path from resetting an existing character's password (account takeover).
        if (NameProblem(name) is { } why)
        {
            Log.Info($"   -> CREATE REJECTED ('{name}'): {why}");
            SendMessage(why);
            return;
        }

        // A character with no password can't be logged into at all now that TOFU is gone (LoginAuth returns
        // NoPassword), so refuse to create one rather than persist an unreachable record.
        if (string.IsNullOrEmpty(_pendingPass))
        {
            Log.Info($"   -> CREATE REJECTED ('{name}'): no password captured from the 0x02 name-check");
            SendMessage("Please enter a password.");
            return;
        }

        var c = new Character();
        c.Name = name;             // stored with the player's chosen CASING; logins match case-insensitively
        c.CreationBlob = dec;      // keep the raw body for future re-decoding if the mapping changes
        CharacterFactory.ApplyAppearance(c);        // decode gender/face/nation/totem/hair
        CharacterFactory.PlaceNewCharacter(c);      // home city for the picked nation
        if (!_store.Save(c))
        {
            Log.Info($"   -> CREATE FAILED to persist '{name}' — not registering the account");
            SendMessage("Could not create the character. Try again.");
            return;
        }
        Accounts.SetPassword(name, Auth.Hash(_pendingPass));
        Log.Info($"   -> CREATE persisted '{name}' (sex={c.Sex} face={c.Face} nation={Character.NationName(c.Nation)} totem={c.Totem}) -> {_store.Directory}");
        SendMessage("Account created.");
    }

    private void HandleLogin(byte[] dec)
    {
        int ulen = dec[0];
        _user = Encoding.ASCII.GetString(dec, 1, ulen);
        string pass = LoginAuth.ReadPassword(dec, 1 + ulen);   // 0x03 body: nameLen name pwLen pw 00

        // Online-guessing defense: refuse BEFORE verifying, so a burned-out address can't keep us doing
        // BCrypt work either. ConnGuard only limits how often an address may CONNECT — one connection can
        // send unlimited 0x03s, and a 3-8 char password doesn't survive that.
        if (LoginThrottle.IsBlocked(_ip))
        {
            Log.Info($"   -> LOGIN BLOCKED (failed-attempt budget exhausted) from {_remote} for user='{_user}'");
            SendMessage(LoginThrottle.BlockedMessage);
            return;
        }

        // Authenticate. STRICT: an unknown name is refused, never created — the only path to a character is
        // the creation flow above. Shared rule so login + the game channel's re-login can't drift.
        var auth = LoginAuth.Authenticate(_user, pass);
        if (auth != LoginResult.Ok)
        {
            int left = LoginThrottle.RecordFailure(_ip);
            Log.Info($"   -> LOGIN REJECTED ({auth}) for user='{_user}' from {_remote} ({left} attempt(s) left)");
            SendMessage(LoginAuth.MessageFor(auth));
            return;   // no handoff — the client stays on the login screen showing the message
        }
        LoginThrottle.RecordSuccess(_ip);
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
