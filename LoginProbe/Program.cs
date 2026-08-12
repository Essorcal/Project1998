using System.Net.Sockets;
using System.Text;
using Protocol.Tk495;

// Headless NexusTK 4.95 login probe. Runs the whole client-side login flow end to end:
//   login channel (0x02 name-check, 0x04 create-if-needed, 0x03 login) -> parse the handoff reply
//   -> game channel (0x10 arrival with the handoff token) -> confirm world entry.
// Exit 0 = logged into the world; non-zero = failed, with the reason printed.
//
// Usage: LoginProbe [--user test] [--pass test1] [--host 127.0.0.1] [--port 2000]

string user = "test", pass = "test1", loginHost = "127.0.0.1";
int loginPort = 2000;
for (int i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--user": user = args[i + 1]; break;
        case "--pass": pass = args[i + 1]; break;
        case "--host": loginHost = args[i + 1]; break;
        case "--port": loginPort = int.Parse(args[i + 1]); break;
    }
}

var key = TkCrypt.LoginKey;
Console.WriteLine($"== LoginProbe: user='{user}' pass='{pass}' login {loginHost}:{loginPort} ==");

try
{
    // ---------------- login channel ----------------
    using var login = new TcpClient();
    login.Connect(loginHost, loginPort);
    var ls = login.GetStream();
    ls.ReadTimeout = 5000;

    var welcome = ReadPacket(ls);
    Console.WriteLine($"[login] welcome op=0x{welcome.Op:x2} ({welcome.Body.Length}B body)");

    // 0x02 name-check: body = <ulen>user<plen>pass 00 00 00
    var nc = new List<byte> { (byte)user.Length };
    nc.AddRange(Encoding.ASCII.GetBytes(user));
    nc.Add((byte)pass.Length);
    nc.AddRange(Encoding.ASCII.GetBytes(pass));
    nc.AddRange(new byte[] { 0, 0, 0 });
    SendLogin(ls, 0x02, 0x02, nc.ToArray(), key);

    var reply = ReadPacket(ls);
    bool available = reply.Op == 0x02 && reply.Inc == 0x01;   // "Odyn" OK reply
    string msg = reply.Inc == 0x02 ? DecodeMessage(reply.Body, key) : "";
    Console.WriteLine($"[login] name-check -> {(available ? "AVAILABLE" : $"message: \"{msg}\"")}");

    if (available)
    {
        // 0x04 create: appearance blob (sex/face/nation/totem), same bytes a real client sends.
        SendLogin(ls, 0x04, 0x04, new byte[] { 0x29, 0x00, 0x02, 0x01, 0x00 }, key);
        var cr = ReadPacket(ls);
        Console.WriteLine($"[login] create -> \"{DecodeMessage(cr.Body, key)}\"");
    }
    else if (!msg.Contains("already taken", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"FAIL: name-check refused for a reason other than 'already exists': {msg}");
        return 2;
    }

    // 0x03 login: body = <ulen>user<plen>pass 00
    var lg = new List<byte> { (byte)user.Length };
    lg.AddRange(Encoding.ASCII.GetBytes(user));
    lg.Add((byte)pass.Length);
    lg.AddRange(Encoding.ASCII.GetBytes(pass));
    lg.Add(0);
    SendLogin(ls, 0x03, 0x06, lg.ToArray(), key);

    var handoff = ReadPacket(ls);
    if (handoff.Op != 0x03)
    {
        // A message here means the login itself was refused (bad password, throttle, etc.).
        Console.WriteLine($"FAIL: login refused: \"{DecodeMessage(handoff.Body, key)}\"");
        return 3;
    }

    // Handoff layout (raw, past AA/len): op=03 | revIP[4] | gport[2] | 23 00 | 09 "NexonInc." <ulen>user<nonce>
    // raw indices: [4..8)=reversed octets, [8..10)=port, [12..]=the exact 0x10 arrival body.
    var raw = handoff.Raw;
    string gameHost = $"{raw[7]}.{raw[6]}.{raw[5]}.{raw[4]}";
    int gport = (raw[8] << 8) | raw[9];
    byte[] arrivalBody = raw[12..];    // <klen>"NexonInc."<ulen>user<nonce> — echoed verbatim
    Console.WriteLine($"[login] handoff -> game {gameHost}:{gport}  arrival={Hex(arrivalBody)}");

    // ---------------- game channel ----------------
    using var game = new TcpClient();
    game.Connect(gameHost, gport);
    var gs = game.GetStream();
    gs.ReadTimeout = 5000;

    // 0x10 arrival is PLAINTEXT (server reads pkt.Body raw). No encryption.
    gs.Write(TkPacket.Build(0x10, 0x00, arrivalBody));
    Console.WriteLine($"[game]  sent 0x10 arrival to {gameHost}:{gport}");

    // Accepted => world-entry burst arrives. Rejected => the server closes the socket immediately.
    try
    {
        var first = ReadPacket(gs);
        Console.WriteLine($"[game]  world-entry packet op=0x{first.Op:x2} ({first.Body.Length}B) — ACCEPTED");
        Console.WriteLine("");
        Console.WriteLine($"SUCCESS: '{user}' logged into the world (login -> handoff -> game arrival).");
        return 0;
    }
    catch (EndOfStreamException)
    {
        Console.WriteLine("FAIL: game server closed the connection after 0x10 — handoff token rejected or no character.");
        return 4;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
    return 5;
}

// ---- helpers ----

static void SendLogin(NetworkStream s, byte op, byte inc, byte[] plaintextBody, byte[] key)
{
    var enc = TkCrypt.Crypt(plaintextBody, inc, key);
    s.Write(TkPacket.Build(op, inc, enc));
}

static string DecodeMessage(byte[] encBody, byte[] key)
{
    // message body decrypts to: 0x0F <len> <ascii...> 00
    var dec = TkCrypt.Crypt(encBody, 0x02, key);
    if (dec.Length >= 2 && dec[0] == 0x0F)
    {
        int len = dec[1];
        if (2 + len <= dec.Length) return Encoding.ASCII.GetString(dec, 2, len);
    }
    return Hex(dec);
}

static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

// Read exactly one AA-framed packet, blocking until it's complete.
static Pkt ReadPacket(NetworkStream s)
{
    var buf = new List<byte>();
    var tmp = new byte[1024];
    while (true)
    {
        // Ensure we have the 3-byte header first, then the full length.
        while (buf.Count < 3) FillOne(s, buf, tmp);
        int len = (buf[1] << 8) | buf[2];
        int total = 3 + len;
        while (buf.Count < total) FillOne(s, buf, tmp);
        var arr = buf.ToArray();
        if (arr[0] != 0xAA) throw new InvalidDataException($"bad frame start 0x{arr[0]:x2}");
        return new Pkt
        {
            Op = arr[3],
            Inc = arr[4],
            Body = arr[5..total],
            Raw = arr[0..total],
        };
    }
}

static void FillOne(NetworkStream s, List<byte> buf, byte[] tmp)
{
    int n = s.Read(tmp, 0, tmp.Length);
    if (n == 0) throw new EndOfStreamException();
    for (int i = 0; i < n; i++) buf.Add(tmp[i]);
}

struct Pkt
{
    public byte Op;
    public byte Inc;
    public byte[] Body;
    public byte[] Raw;
}
