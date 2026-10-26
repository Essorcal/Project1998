using System.Net;
using System.Net.Sockets;
using System.Text;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The game port's re-login (a <c>0x03</c> on the game socket, <c>Session.HandleReLogin</c>) keys its address
/// gates (the failed-attempt throttle, the throttle's exempt-address log note, and the IP ban) on the PLAYER's
/// address: the one the login port gates the same connection on, and the one the handoff token is bound to. It
/// used to key them on the socket's own peer, which behind a trusted proxy is the proxy. Behind a remote proxy
/// every player's failures then spent one shared budget, a ban on a player missed them and a ban on the proxy
/// refused everyone; behind a loopback proxy (the default <c>P1998_PROXY_ALLOW</c> topology) the throttle's
/// loopback exemption switched the limiter off for every player. Nothing threw either way.
///
/// <para><b>How a proxied connection is built here.</b> The acceptor (<see cref="TkAcceptor"/>) hands a session
/// the PROXY header's address as <c>realIp</c>, and the production constructor wraps the socket in
/// <c>new TcpOutbound(client, OutboundOptions.Game, realIp)</c>. Each fact builds exactly that transport and gives
/// it to the transport constructor, which is what lets it run the writer and read the replies off the other end.
/// Two proxies. The loopback one is a TCP pair on 127.0.0.1. A remote proxy needs a socket whose peer is not
/// loopback, and the suite cannot open one: that takes a listener on a non-loopback interface, which
/// <see cref="SharedListenerTests"/> refuses to put on whichever box runs the suite, and Windows refuses a
/// connect from a LAN address to 127.0.0.1. So the remote proxy is a Unix-domain stream socket, whose peer
/// <see cref="TcpOutbound"/> reads as <see cref="IPAddress.None"/>: not loopback, and the same for every
/// connection through it, which is all a containerised proxy's gateway address is to these gates.</para>
///
/// <para>In <c>"log"</c>, which runs alone: each fact takes the log line sink, the throttle's table is static,
/// and the ban facts ban a proxy's own address (127.0.0.1 among them) for a moment. Every address a fact charges
/// or bans is cleared again in <see cref="Dispose"/>, and every account it makes is deleted.</para>
/// </summary>
[Collection("log")]
public sealed class ReLoginAddressTests : IDisposable
{
    /// <summary>Which proxy a proxied connection comes through (see the class remarks).</summary>
    public enum Proxy { Loopback, Remote }

    /// <summary>The 4.95 game port; any port that is not a login port takes the same path.</summary>
    private const int GamePort = 2005;

    /// <summary>The failed-attempt budget, read from the configuration the throttle itself reads rather than
    /// hardcoded: <c>P1998_LOGIN_FAILS</c> may resize it where the suite runs.</summary>
    private static int Budget => ServerConfig.Current.LoginFails;

    private static readonly string NoCharacter = LoginAuth.MessageFor(LoginResult.NoCharacter);

    /// <summary>A fresh player address per call, from RFC 5737 TEST-NET-2, so no two facts (and no other class)
    /// share a throttle entry or a ban row.</summary>
    private static int _next = 100;
    private IPAddress Player()
    {
        var ip = IPAddress.Parse($"198.51.100.{Interlocked.Increment(ref _next)}");
        _charged.Add(ip);
        return ip;
    }

    /// <summary>A name with no character, so a re-login fails fast (no BCrypt) and is charged to the address.</summary>
    private static string Nobody() => ("zr" + Guid.NewGuid().ToString("N"))[..10];

    private readonly World _world = new();
    private readonly CharacterStore _store = new(RepoPaths.CharsDir());
    private readonly List<IPAddress> _charged = new() { IPAddress.None, IPAddress.Loopback, IPAddress.IPv6Loopback };
    private readonly List<string> _banned = new();
    private readonly List<string> _accounts = new();

    public void Dispose()
    {
        foreach (var ip in _banned) Moderation.UnbanIp(ip, "relogin-address-test");
        foreach (var ip in _charged) LoginThrottle.RecordSuccess(ip);
        try
        {
            using var cn = Db.Open();
            foreach (var user in _accounts)
                foreach (var table in new[] { "accounts", "characters" })
                {
                    using var cmd = cn.CreateCommand();
                    cmd.CommandText = $"DELETE FROM {table} WHERE username=$u;";
                    cmd.Parameters.AddWithValue("$u", Auth.Key(user));
                    cmd.ExecuteNonQuery();
                }
        }
        catch { /* best-effort cleanup of throwaway rows in the per-run database */ }
    }

    // ---- the facts ----------------------------------------------------------------------------------------

    /// <summary>Facts 1 and 2. Behind a trusted proxy, failed re-logins are charged to the player's address:
    /// player A's re-logins count down A's own budget, A is refused once it is spent, and player B through the
    /// same proxy is not.
    /// <para>Red on master, a different way per proxy. <see cref="Proxy.Remote"/>: every failure was charged to
    /// the proxy, so B's first re-login after A's budget ran out was refused with "Too many failed attempts".
    /// <see cref="Proxy.Loopback"/>: the peer is loopback and exempt, so A's first failure logged
    /// "(throttle-exempt address)" instead of a countdown and A was never refused.</para></summary>
    [Theory]
    [InlineData(Proxy.Remote)]
    [InlineData(Proxy.Loopback)]
    public void Behind_a_proxy_each_player_spends_their_own_budget(Proxy proxy)
    {
        var a = Player();
        var b = Player();
        string userA = Nobody(), userB = Nobody();
        using var sink = LogLineSink.Acquire();

        using (var link = Open(proxy, a))
        {
            for (int k = 1; k <= Budget; k++)
                Assert.Equal(NoCharacter, link.ReLogin(userA, "guess").Text);
            Assert.Equal(LoginThrottle.BlockedMessage, link.ReLogin(userA, "guess").Text);
        }
        AssertCountdown(sink, userA, Budget);
        Assert.True(sink.Has($"RE-LOGIN BLOCKED (failed-attempt budget exhausted) for user='{userA}'"));

        using (var link = Open(proxy, b))
            Assert.Equal(NoCharacter, link.ReLogin(userB, "guess").Text);
        AssertCountdown(sink, userB, 1);
    }

    /// <summary>Fact 3, first half. An IP ban on player A refuses A's re-login through the proxy, with the
    /// ban's reason, and does not refuse player B through the same proxy.
    /// <para>Red on master: the ban was looked up for the proxy's address, so a ban on A never matched and A got
    /// "That character does not exist." instead.</para></summary>
    [Theory]
    [InlineData(Proxy.Remote)]
    [InlineData(Proxy.Loopback)]
    public void An_ip_ban_on_a_player_refuses_that_player_behind_a_proxy(Proxy proxy)
    {
        var a = Player();
        var b = Player();
        Ban(a.ToString(), "relogin probe: player A");

        using (var link = Open(proxy, a))
            Assert.Equal("This address is banned from the server: relogin probe: player A",
                         link.ReLogin(Nobody(), "guess").Text);
        using (var link = Open(proxy, b))
            Assert.Equal(NoCharacter, link.ReLogin(Nobody(), "guess").Text);
    }

    /// <summary>Fact 3, second half. A ban naming the proxy's own address refuses no player behind it.
    /// <para>Red on master: the ban was looked up for the proxy's address, so it refused player B, and with B
    /// every player through that proxy.</para></summary>
    [Theory]
    [InlineData(Proxy.Remote)]
    [InlineData(Proxy.Loopback)]
    public void An_ip_ban_on_the_proxys_address_refuses_no_player_behind_it(Proxy proxy)
    {
        var b = Player();
        using var link = Open(proxy, b);
        Ban(link.ProxyAddress, "relogin probe: the proxy");
        Assert.Equal(NoCharacter, link.ReLogin(Nobody(), "guess").Text);
    }

    /// <summary>Fact 4. With no proxy (<c>P1998_TRUST_PROXY=0</c>, the default, where the acceptor hands the
    /// session no <c>realIp</c>) the gates key on the socket's own peer, exactly as before; and a proxied
    /// connection whose header carried no address (a LOCAL header, for which the acceptor hands over the peer
    /// itself as <c>realIp</c>) is the same. A loopback peer is exempt: more failures than the budget, every one
    /// answered "That character does not exist." and logged "(throttle-exempt address)". A peer that is not
    /// loopback counts down and is refused once the budget is spent. A ban on the peer refuses it.
    /// <para>Not red on master, by design: this is the unchanged path, and it passes on both. It is what the
    /// mutants in the report break.</para></summary>
    [Theory]
    [InlineData(false, false)]   // a direct loopback client
    [InlineData(false, true)]    // a direct client whose peer is not loopback
    [InlineData(true, false)]    // a LOCAL PROXY header through the loopback proxy: realIp is the peer
    public void Without_a_proxy_address_the_gates_key_on_the_socket_peer_as_before(bool localHeader, bool remotePeer)
    {
        using var sink = LogLineSink.Acquire();
        string user = Nobody();
        using (var link = remotePeer ? Link.Unix(null, _world, _store)
                                     : Link.Tcp(localHeader ? IPAddress.Loopback : null, _world, _store))
        {
            IPAddress peer = link.PeerAddress;   // 127.0.0.1, or IPAddress.None for the Unix-domain peer
            Ban(peer.ToString(), "relogin probe: direct");
            Assert.Equal("This address is banned from the server: relogin probe: direct",
                         link.ReLogin(Nobody(), "guess").Text);
            Unban(peer.ToString());

            AssertBudgetFor(peer, link, sink, user);
        }
    }

    /// <summary>The re-login's success path, behind a proxy. The reply is the same handoff redirect it always
    /// was, back to this game port, with a token bound to the player's address (not the proxy's), which the
    /// arrival then consumes. The success clears the PLAYER's failure count, so their next failure starts a
    /// fresh budget.
    /// <para>Red on master only in the counting: the loopback peer was exempt, so the failures logged
    /// "(throttle-exempt address)" and the success cleared the proxy's (empty) count. The redirect and the
    /// token's binding were already the player's and pass on master too.</para></summary>
    [Fact]
    public void A_successful_relogin_behind_a_proxy_redirects_with_the_players_token_and_clears_their_count()
    {
        var a = Player();
        var (user, pass) = Account();
        using var sink = LogLineSink.Acquire();
        using var link = Open(Proxy.Loopback, a);

        string failing = Nobody();
        Assert.Equal(NoCharacter, link.ReLogin(failing, "guess").Text);
        Assert.Equal(NoCharacter, link.ReLogin(failing, "guess").Text);
        AssertCountdown(sink, failing, 2);

        var ok = link.ReLogin(user, pass);
        Assert.Equal(Opcode.Login, ok.Opcode);
        byte[] key = ok.Frame[13..22];
        byte[] token = ok.Frame[^LoginRedirect.TailBytes..];
        Assert.Equal(LoginRedirect.Build(HostAddress.Parse(ServerConfig.Current.GameHost), GamePort, user, key, token),
                     ok.Frame);
        Assert.False(HandoffTokens.Consume(token, key, user, link.ProxyAddress, GamePort), "the token is bound to the proxy");
        Assert.True(HandoffTokens.Consume(token, key, user, a.ToString(), GamePort), "the token is not bound to the player");

        string after = Nobody();
        Assert.Equal(NoCharacter, link.ReLogin(after, "guess").Text);
        AssertCountdown(sink, after, 1);   // budget-1 left: the success cleared the two before it
    }

    /// <summary>The loopback exemption still covers a player whose own address is loopback: a client on the
    /// proxy's box, coming through the loopback proxy. More failures than the budget, none refused, each logged
    /// "(throttle-exempt address)". Passes on master too (there the loopback peer was exempt as well).</summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void A_loopback_player_behind_the_loopback_proxy_stays_exempt(string player)
    {
        var ip = IPAddress.Parse(player);
        string user = Nobody();
        using var sink = LogLineSink.Acquire();
        using var link = Open(Proxy.Loopback, ip);
        AssertBudgetFor(ip, link, sink, user);
    }

    /// <summary>The game-to-game re-login mints like the login server: on the 4.95 port its redirect carries a
    /// one-time key in the key field, nine non-zero bytes that are not <c>"NexonInc."</c>, and the token it
    /// carries enters only with that key, once. On the 5.33 port it carries <c>"NexonInc."</c> and no key, and its
    /// token enters there as before.
    /// <para>Red on master: the re-login's key field was always <c>"NexonInc."</c>. Falsified by minting the
    /// re-login for any port but its own (<c>HandoffTokens.Mint(user, _remoteIp, 2006)</c> in
    /// <c>Session.HandleReLogin</c>): the redirect's grant check throws and no redirect is sent.</para></summary>
    [Fact]
    public void A_relogin_carries_a_one_time_key_on_the_495_port_and_NexonInc_on_the_533_port()
    {
        var a = Player();
        var (user, pass) = Account();
        using (var link = Open(Proxy.Loopback, a))
        {
            var ok = link.ReLogin(user, pass);
            Assert.Equal(Opcode.Login, ok.Opcode);
            Assert.Equal(new byte[] { 0x07, 0xD5 }, ok.Frame[8..10]);   // back to 2005
            Assert.Equal(9, ok.Frame[12]);
            byte[] key = ok.Frame[13..22];
            byte[] token = ok.Frame[^LoginRedirect.TailBytes..];
            Assert.NotEqual(TkCrypt.LoginKey, key);
            Assert.DoesNotContain((byte)0, key);
            Assert.False(HandoffTokens.Consume(token, TkCrypt.LoginKey, user, a.ToString(), GamePort),
                         "the re-login's token entered with \"NexonInc.\" in place of its key");
            Assert.True(HandoffTokens.Consume(token, key, user, a.ToString(), GamePort), "the re-login's own key was refused");
            Assert.False(HandoffTokens.Consume(token, key, user, a.ToString(), GamePort), "the re-login's key was replayed");
        }

        using (var link = Link.Tcp(a, _world, _store, Handoff.Game533))
        {
            var ok = link.ReLogin(user, pass);
            Assert.Equal(Opcode.Login, ok.Opcode);
            Assert.Equal(new byte[] { 0x07, 0xD6 }, ok.Frame[8..10]);   // back to 2006
            Assert.Equal(TkCrypt.LoginKey, ok.Frame[13..22]);
            byte[] token = ok.Frame[^LoginRedirect.TailBytes..];
            Assert.True(HandoffTokens.Consume(token, ok.Frame[13..22], user, a.ToString(), Handoff.Game533),
                        "the 5.33 re-login's token was refused on its own port");
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private Link Open(Proxy proxy, IPAddress player) =>
        proxy == Proxy.Remote ? Link.Unix(player, _world, _store) : Link.Tcp(player, _world, _store);

    private void Ban(string ip, string reason)
    {
        Assert.True(Moderation.BanIp(ip, Moderation.Forever, reason, "relogin-address-test"));
        _banned.Add(ip);
    }

    private static void Unban(string ip) => Assert.True(Moderation.UnbanIp(ip, "relogin-address-test"));

    /// <summary>An account that exists, with a password: the character row <c>LoginAuth.Authenticate</c> looks
    /// for and the hash it verifies. Six letters, so every byte of the handoff nonce survives the client's
    /// truncation (see <see cref="HandoffTokens.SurvivingBytes"/>).</summary>
    private (string User, string Pass) Account()
    {
        string user = ("r" + Guid.NewGuid().ToString("N"))[..6];
        const string pass = "pw1234";
        using (var cn = Db.Open())
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO characters(username,json,updated_utc) VALUES($u,'{}',1)";
            cmd.Parameters.AddWithValue("$u", Auth.Key(user));
            cmd.ExecuteNonQuery();
        }
        Accounts.SetPassword(user, Auth.Hash(pass));
        _accounts.Add(user);
        return (user, pass);
    }

    /// <summary>The "RE-LOGIN REJECTED" lines for <paramref name="user"/>, in the order they were written.</summary>
    private static string[] Rejections(LogLineSink sink, string user) =>
        sink.Lines.Select(l => l.Line)
            .Where(l => l.Contains("RE-LOGIN REJECTED", StringComparison.Ordinal)
                        && l.Contains($"user='{user}'", StringComparison.Ordinal))
            .ToArray();

    /// <summary>Drive <paramref name="link"/>'s re-logins past the budget and assert what the throttle does
    /// with an address in <paramref name="ip"/>'s standing. Exempt (loopback, unless
    /// <c>P1998_LOGIN_EXEMPT_LOOPBACK=0</c> where the suite runs): more failures than the budget, none refused,
    /// each logged "(throttle-exempt address)". Counted: a countdown, then the refusal.</summary>
    private static void AssertBudgetFor(IPAddress ip, Link link, LogLineSink sink, string user)
    {
        if (LoginThrottle.IsExempt(ip))
        {
            for (int k = 1; k <= Budget + 2; k++)
                Assert.Equal(NoCharacter, link.ReLogin(user, "guess").Text);
            var lines = Rejections(sink, user);
            Assert.Equal(Budget + 2, lines.Length);
            Assert.All(lines, l => Assert.EndsWith("(throttle-exempt address)", l));
        }
        else
        {
            for (int k = 1; k <= Budget; k++)
                Assert.Equal(NoCharacter, link.ReLogin(user, "guess").Text);
            Assert.Equal(LoginThrottle.BlockedMessage, link.ReLogin(user, "guess").Text);
            AssertCountdown(sink, user, Budget);
        }
    }

    /// <summary><paramref name="count"/> rejections for <paramref name="user"/> from a fresh address, each
    /// logging the budget left after it: budget-1, budget-2, ... — a counted address, not an exempt one.</summary>
    private static void AssertCountdown(LogLineSink sink, string user, int count)
    {
        var lines = Rejections(sink, user);
        Assert.Equal(count, lines.Length);
        for (int k = 1; k <= count; k++)
            Assert.EndsWith($"({Budget - k} attempt(s) left)", lines[k - 1]);
    }

    /// <summary>One reply frame: its opcode, the text of a message box (<c>0x02</c>), and the raw frame.</summary>
    private readonly record struct Reply(byte Opcode, string? Text, byte[] Frame);

    /// <summary>One game connection: the server half wrapped the way the production constructor wraps it, a
    /// session on that transport, the writer running, and the client half to read the replies from.</summary>
    private sealed class Link : IDisposable
    {
        private readonly Socket _client;
        private readonly NetworkStream _replies;
        private readonly TcpOutbound _out;
        private readonly Task _writer;
        private readonly string[] _files;
        private readonly Session _session;

        private Link(Socket client, TcpClient server, IPAddress? realIp, World world, CharacterStore store, int port,
                     params string[] files)
        {
            _client = client;
            _files = files;
            _client.ReceiveTimeout = 5_000;
            _replies = new NetworkStream(client, ownsSocket: false);
            _out = new TcpOutbound(server, OutboundOptions.Game, realIp);   // = Session(TcpClient, ..., realIp)
            _session = new Session(_out, port, store, world);
            _writer = _out.RunWriterAsync(_ => { });
        }

        /// <summary>The socket's own peer, as the transport reads it: the proxy's address on a proxied link.</summary>
        public IPAddress PeerAddress => _out.PeerAddress;

        public string ProxyAddress => _out.PeerAddress.ToString();

        /// <summary>A TCP pair on 127.0.0.1: a direct loopback client, or the loopback proxy.</summary>
        public static Link Tcp(IPAddress? realIp, World world, CharacterStore store, int port = GamePort)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            return new Link(client, listener.AcceptTcpClient(), realIp, world, store, port);
        }

        /// <summary>A Unix-domain stream pair: a peer the transport reads as <see cref="IPAddress.None"/>. Both
        /// ends are bound to a path, so neither side's address is the unnamed one.</summary>
        public static Link Unix(IPAddress? realIp, World world, CharacterStore store)
        {
            string tag = Guid.NewGuid().ToString("N")[..12];
            string serverPath = Path.Combine(Path.GetTempPath(), $"p1998-{tag}.s");
            string clientPath = Path.Combine(Path.GetTempPath(), $"p1998-{tag}.c");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(serverPath));
            listener.Listen(1);
            var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Bind(new UnixDomainSocketEndPoint(clientPath));
            client.Connect(new UnixDomainSocketEndPoint(serverPath));
            var server = new TcpClient();
            server.Client.Dispose();          // the unconnected TCP socket TcpClient made for itself
            server.Client = listener.Accept();
            return new Link(client, server, realIp, world, store, GamePort, serverPath, clientPath);
        }

        /// <summary>Send one re-login, as the client frames it on the game socket, and read the one frame the
        /// session answers it with.</summary>
        public Reply ReLogin(string user, string pass)
        {
            var body = new List<byte> { (byte)user.Length };
            body.AddRange(Encoding.ASCII.GetBytes(user));
            body.Add((byte)pass.Length);
            body.AddRange(Encoding.ASCII.GetBytes(pass));
            body.Add(0);
            const byte inc = 0x06;
            _session.Receive(TkPacket.Build(Opcode.Login, inc, TkCrypt.Crypt(body.ToArray(), inc, TkCrypt.LoginKey)));

            var head = new byte[3];
            _replies.ReadExactly(head);
            Assert.Equal(0xAA, head[0]);
            var frame = new byte[3 + ((head[1] << 8) | head[2])];
            head.CopyTo(frame, 0);
            _replies.ReadExactly(frame, 3, frame.Length - 3);
            Assert.True(TkPacket.TryParse(frame, out var pkt, out int consumed));
            Assert.Equal(frame.Length, consumed);
            if (pkt.Opcode != 0x02) return new Reply(pkt.Opcode, null, frame);
            var dec = TkCrypt.Crypt(pkt.Body, pkt.Increment, TkCrypt.LoginKey);
            return new Reply(pkt.Opcode, Encoding.ASCII.GetString(dec, 2, dec[1]), frame);
        }

        public void Dispose()
        {
            _out.Close();
            try { _writer.Wait(TimeSpan.FromSeconds(5)); } catch { /* the writer's own teardown already logged */ }
            _replies.Dispose();
            _client.Dispose();
            foreach (var f in _files)
                try { File.Delete(f); } catch { /* a leftover socket file in the temp directory is harmless */ }
        }
    }
}
