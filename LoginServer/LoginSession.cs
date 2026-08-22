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
    // shared with the game server's P1998_HANDSHAKE_MS; 15s is far more than a real client needs.
    private static readonly int HandshakeMs =
        int.TryParse(Environment.GetEnvironmentVariable("P1998_HANDSHAKE_MS"), out var hs) && hs > 0 ? hs : 15_000;
    private int _established;   // 0 until the first valid packet is parsed; gates the handshake timeout

    // AA 00 13 7E 1B "CONNECTED SERVER\n"  (plaintext welcome, as the 6.x reference sends on connect)
    private static readonly byte[] Welcome = BuildWelcome();

    // The game server the client is redirected to after a successful login. Defaults to loopback (login
    // and game on the same box); set P1998_GAME_HOST to the game server's public IP for a split
    // deployment. The client stores the host/port from our 0x03 reply, opens a FRESH connection to it,
    // and announces itself there with 0x10.
    private static readonly byte[] GameHost = ParseHost(Environment.GetEnvironmentVariable("P1998_GAME_HOST"));

    /// <param name="realIp">The client's true address when a trusted proxy sits in front and the listener
    /// has already consumed its PROXY header. Null on a direct connection. The per-IP failed-login throttle
    /// and the handoff token are both keyed on this, and keyed on the proxy they protect nothing.</param>
    public LoginSession(TcpClient client, int port, CharacterStore store, IPAddress? realIp = null)
    {
        _client = client;
        _stream = client.GetStream();
        _port = port;
        _store = store;
        var peer = client.Client.RemoteEndPoint as IPEndPoint;
        _remote = realIp is not null ? $"{realIp} (via {peer?.Address})" : peer?.ToString() ?? "?";
        _ip = realIp ?? peer?.Address ?? IPAddress.None;
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

        // Report a weak password HERE too, not just at creation: 0x02 carries the password alongside the
        // name, so the player can be told before they go pick a face. Only when one was actually supplied —
        // an empty box at this stage is normal, and creation is the gate that insists on one.
        if (!string.IsNullOrEmpty(_pendingPass) && PasswordProblem(_pendingPass) is { } pwWhy)
        {
            SendMessage(pwWhy);
            Log.Info($"   -> password REJECTED at name-check ('{_pendingName}'): {pwWhy}");
            return;
        }

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
        // The password LENGTH only — never the password. This is the measurement that decides whether the
        // client's entry box truncates what it transmits (and so whether stronger passwords need a client
        // patch at all). A length is safe to keep in a log; the password is not — see Log.WireEnabled.
        Log.Info($"   -> name available (pending='{_pendingName}', password {_pendingPass.Length} chars)");
    }

    // Shared name gate for the availability check and creation. Returns null if the name is usable, else the
    // player-facing reason. Both the `characters` and `accounts` tables key on CharacterStore.Key (lowercased,
    // non-alphanumerics stripped), so the character set has to be restricted to what survives that
    // normalization — otherwise "Bo b" and "Bob" would be the same account under two different display names.
    //
    // The rule matches STANDARD NexusTK: up to 12 characters, LETTERS ONLY — no spaces, no digits, no
    // punctuation. (It used to allow digits and _, which normalization would have folded together anyway.)
    private const int MaxNameLength = 12;

    private static string? NameProblem(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Please enter a name.";
        if (name.Length < 3 || name.Length > MaxNameLength) return $"Names must be 3 to {MaxNameLength} letters.";
        foreach (var ch in name)
            if (!char.IsAsciiLetter(ch)) return "Names may only use letters.";
        if (CharacterStore.CharacterExists(name) || Accounts.Exists(name)) return "That name is already taken.";
        return null;
    }

    // Password gate, applied at CREATION only — never at login, so tightening it can't lock out an existing
    // account. Standard NexusTK is 4-8 characters with at least one number; the floor and the digit rule are
    // kept (nothing that works on a standard server is refused here) but the 8-character ceiling is NOT a
    // protocol limit — the wire carries a u8-length password, so 255 fit, and BCrypt doesn't care how long
    // the input is. MaxPasswordLength is therefore ours to choose; it is capped here only by what the 4.95
    // client's entry box will actually transmit, which is measured, not assumed. Raise it once that number
    // is known — this constant is the only thing to change.
    private const int MinPasswordLength = 4;
    private const int MaxPasswordLength = 16;

    private static string? PasswordProblem(string pass)
    {
        if (string.IsNullOrEmpty(pass)) return "Please enter a password.";
        if (pass.Length < MinPasswordLength) return $"Passwords must be at least {MinPasswordLength} characters.";
        if (pass.Length > MaxPasswordLength) return $"Passwords must be at most {MaxPasswordLength} characters.";
        if (!pass.Any(char.IsAsciiDigit)) return "Passwords must contain at least one number.";
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
        // NoPassword), so refuse to create one rather than persist an unreachable record. The strength rules
        // ride along here: this is the one place a password is chosen.
        if (PasswordProblem(_pendingPass) is { } pwWhy)
        {
            Log.Info($"   -> CREATE REJECTED ('{name}'): password ({_pendingPass.Length} chars) — {pwWhy}");
            SendMessage(pwWhy);
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
        // Defensive parse. The client's name/password boxes are effectively unbounded, so the length byte
        // and the body can disagree (a >255-char entry wraps the u8 length) — and every other handler on
        // this channel already guards. Unguarded, a malformed 0x03 threw out of the read loop and the
        // player just saw a silent disconnect with no message, which is the hardest failure to diagnose.
        int ulen = dec.Length > 0 ? dec[0] : 0;
        if (ulen <= 0 || 1 + ulen > dec.Length)
        {
            Log.Info($"   -> LOGIN REJECTED (malformed 0x03: {dec.Length}B body, nameLen={ulen}) from {_remote}");
            SendMessage("Please enter a name and password.");
            return;
        }
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
        // Source-address ban (Moderation.BanIp). Checked before the password so a banned address can't spend
        // our BCrypt budget either — unlike an account ban, this one reveals nothing, since the address
        // already knows it is the one being refused.
        if (Moderation.IsIpBanned(_ip.ToString(), out var ipReason))
        {
            Log.Info($"   -> LOGIN REJECTED (ip banned) for user='{_user}' from {_remote}");
            SendMessage(string.IsNullOrWhiteSpace(ipReason)
                ? "This address is banned from the server."
                : $"This address is banned from the server: {ipReason}");
            return;
        }

        var auth = LoginAuth.Authenticate(_user, pass);
        if (auth != LoginResult.Ok)
        {
            // A ban is NOT a failed credential — the password was right. Charging it to the per-IP
            // failed-attempt budget would eventually IP-block a banned player's whole household as a side
            // effect of them retrying, which is a different (and unintended) punishment.
            if (auth == LoginResult.Banned)
            {
                Log.Info($"   -> LOGIN REJECTED (banned) for user='{_user}' from {_remote}");
                SendMessage(LoginAuth.BanMessageFor(_user));
                return;
            }
            int left = LoginThrottle.RecordFailure(_ip);
            Log.Info($"   -> LOGIN REJECTED ({auth}) for user='{_user}' from {_remote} ({left} attempt(s) left)");
            SendMessage(LoginAuth.MessageFor(auth));
            return;   // no handoff — the client stays on the login screen showing the message
        }
        LoginThrottle.RecordSuccess(_ip);
        Log.Info($"   -> LOGIN accepted for user='{_user}' (name {_user.Length} chars, password {pass.Length} chars)");
        Accounts.TouchLogin(_user);

        // Mint a single-use handoff nonce (5 bytes on the wire; how many the client can carry back depends
        // on the username length — see Shared/HandoffTokens) bound to this connection's source address, and
        // record it in the shared DB; the game server validates+consumes it on 0x10 arrival.
        var nonce = HandoffTokens.Mint(_user, _ip.ToString());

        // handoff: send the client to the GAME server (reversed IP octets + port). Keep the version's
        // channel together: a V533 login (port 2001) hands off to the V533 game port 2006; V495 -> 2005.
        int gport = ChannelPorts.GameFor(_port);
        // Shared builder (Protocol.Tk495/LoginRedirect): the game server sends this same struct back the other
        // way when the client exits to the select screen (0x0B), and the client parses it as a fixed-size
        // record — so a byte of drift between the two processes hangs a screen instead of throwing.
        // `nonce` = the 5-byte single-use handoff token (was the static {0,1,18,17,0}); echoed back in 0x10.
        Send(LoginRedirect.Build(GameHost, gport, _user, nonce));
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
