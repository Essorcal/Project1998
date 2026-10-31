using System.Net;
using System.Text;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The redirect key at the game door: <c>Session.HandleArrival</c> reading the key field of a real <c>0x10</c>
/// arrival and the game server's two other <c>0x03</c> redirects. <see cref="HandoffTokenTests"/> pins the mint and
/// consume themselves; these pin what the arrival does with them: the key it reads, the refusal and its line, the
/// <c>P1998_ENFORCE_HANDOFF=0</c> downgrade, the 5.33 door, and the bounce that must keep <c>"NexonInc."</c>. Two
/// facts pin that neither the refusal's line nor an unparsable arrival's writes a key or nonce byte to the log.
///
/// <para>Each arrival is the one the stock 4.95 client sent in the live check of 2026-10-26: <c>09</c>, the
/// redirect's nine key bytes, then its 13-byte field (length byte, name, what fits of the token, a forced NUL),
/// 23 bytes in all (<see cref="StockArrival"/>). Sessions are socket-free, so the address they arrive from is
/// <see cref="IPAddress.None"/>, which is what the hand-offs are minted for; characters live on an
/// <see cref="IsolatedDatabase"/>, tokens in the process database the door reads.</para>
///
/// <para>In <c>"log"</c>, which runs alone: the facts read the arrival's lines from the log sink, and the
/// downgrade fact re-reads the configuration with <c>P1998_ENFORCE_HANDOFF=0</c> set for its own span.</para>
/// </summary>
[Collection("log")]
public sealed class HandoffKeyArrivalTests : IDisposable
{
    private static readonly string Addr = IPAddress.None.ToString();

    private readonly IsolatedDatabase _db = new();
    private readonly World _world;
    private readonly List<string> _names = new();

    public HandoffKeyArrivalTests()
    {
        TestProcessState.LoadContent();
        _world = new World();
    }

    public void Dispose()
    {
        try
        {
            using var cn = Db.Open();
            foreach (var n in _names)
            {
                using var cmd = cn.CreateCommand();
                cmd.CommandText = "DELETE FROM handoff_tokens WHERE username=$u;";
                cmd.Parameters.AddWithValue("$u", Auth.Key(n));
                cmd.ExecuteNonQuery();
            }
        }
        catch { /* best-effort cleanup of throwaway rows in the per-run database */ }
        _db.Dispose();
    }

    /// <summary>An account of exactly <paramref name="length"/> letters with a character on Kugnae's Home map.</summary>
    private string Account(int length)
    {
        string name = ("k" + Guid.NewGuid().ToString("N"))[..length];
        _names.Add(name);
        var c = CharacterFactory.FromCreate(name, new byte[] { 0x29, 0, 1, 0, 0 });
        var info = Content.Maps.GetValueOrDefault(SessionFixture.HomeMap);
        c.Map = SessionFixture.HomeMap; c.X = 5; c.Y = 10;
        c.MapXs = info?.Xs ?? 20; c.MapYs = info?.Ys ?? 20;
        Assert.True(_db.Store.Save(c), $"the setup save of {name} failed");
        return name;
    }

    /// <summary>The stock client's arrival for a redirect that carried <paramref name="key"/>: <c>klen</c>, the key,
    /// then its 13-byte field: the length byte, the name and the token, cut to 12 bytes and NUL-terminated.</summary>
    private static byte[] StockArrival(string user, byte[] key, byte[] token)
    {
        var field = new List<byte> { (byte)user.Length };
        field.AddRange(Encoding.ASCII.GetBytes(user));
        field.AddRange(token);
        while (field.Count < 13) field.Add(0);
        field = field.GetRange(0, 13);
        field[12] = 0;
        var body = new List<byte> { (byte)key.Length };
        body.AddRange(key);
        body.AddRange(field);
        return body.ToArray();
    }

    private static byte[] StockArrival(string user, HandoffTokens.Grant grant) =>
        StockArrival(user, grant.Key ?? TkCrypt.LoginKey, grant.Token);

    /// <summary>One arrival on a fresh session at <paramref name="port"/>: whether the door let it in.</summary>
    private (Session Session, RecordingOutbound Outbound) Arrive(byte[] body, int port = Handoff.Game495)
    {
        var outbound = new RecordingOutbound("recorder:arrival");
        var session = new Session(outbound, port, _db.Store, _world);
        session.Receive(Handoff.Frame(body));
        return (session, outbound);
    }

    private void AssertEntered((Session Session, RecordingOutbound Outbound) arrival, string what)
    {
        Assert.False(arrival.Outbound.Closed, $"{what}: the arrival was refused");
        Assert.Contains(arrival.Session, _world.Online.All());
    }

    private static void AssertRefusedAsABadToken((Session Session, RecordingOutbound Outbound) arrival, LogLineSink sink,
                                                 string user, string what)
    {
        Assert.True(arrival.Outbound.Closed, $"{what}: the arrival was let in");
        Assert.True(sink.Has($"ARRIVAL REJECTED: invalid/expired handoff token for user='{user}' from {Addr} (token "),
                    $"{what}: no bad-token refusal line");
    }

    /// <summary>The live check's 11-letter case at the door: the stock client's 23-byte arrival carries no nonce
    /// byte, and enters with the key its redirect carried. With one key byte changed, or with <c>"NexonInc."</c>,
    /// the same arrival is refused exactly as a bad token is (the same line, the connection closed), and those
    /// refusals spend nothing.
    /// <para>Red on master: the door skipped the key field, so both wrong keys entered.</para></summary>
    [Fact]
    public void An_eleven_letter_arrival_enters_with_its_key_and_is_refused_with_a_wrong_one()
    {
        string name = Account(11);
        var grant = HandoffTokens.Mint(name, Addr, Handoff.Game495);
        byte[] right = StockArrival(name, grant);
        Assert.Equal(23, right.Length);
        byte[] wrong = (byte[])right.Clone();
        wrong[5] = (byte)(wrong[5] == 0xFF ? 0x01 : wrong[5] + 1);

        using var sink = LogLineSink.Acquire();
        AssertRefusedAsABadToken(Arrive(wrong), sink, name, "a key wrong in one byte");
        AssertRefusedAsABadToken(Arrive(StockArrival(name, TkCrypt.LoginKey, grant.Token)), sink, name, "\"NexonInc.\"");
        AssertEntered(Arrive(right), "the right key");
    }

    /// <summary>An arrival whose key field is missing (<c>klen</c> 0) or another width (<c>klen</c> 8) is refused
    /// like a bad token; the right arrival still enters afterwards.</summary>
    [Fact]
    public void A_missing_key_or_another_klen_is_refused_like_a_bad_token()
    {
        string name = Account(7);
        var grant = HandoffTokens.Mint(name, Addr, Handoff.Game495);

        using var sink = LogLineSink.Acquire();
        AssertRefusedAsABadToken(Arrive(StockArrival(name, Array.Empty<byte>(), grant.Token)), sink, name, "klen 0");
        AssertRefusedAsABadToken(Arrive(StockArrival(name, grant.Key![..8], grant.Token)), sink, name, "klen 8");
        AssertEntered(Arrive(StockArrival(name, grant)), "the right key");
    }

    /// <summary>Every way a secret's bytes could be written as text in a log line: <see cref="Log.Hex"/>'s spaced
    /// lower-case pairs, and <see cref="Convert.ToHexString(byte[])"/> in either case.</summary>
    private static string[] Renderings(byte[] secret) =>
        [string.Join(" ", secret.Select(b => b.ToString("x2"))),
         Convert.ToHexString(secret).ToLowerInvariant(),
         Convert.ToHexString(secret)];

    private static void AssertNotLogged(LogLineSink sink, byte[] secret, string what)
    {
        foreach (var form in Renderings(secret))
            Assert.False(sink.Has(form), $"{what} reached the log as \"{form}\"");
    }

    /// <summary>A refused arrival's line says what was refused, for whom, from where and how many token bytes came,
    /// but not the bytes. The refusal below is a wrong key, which leaves the row unspent: the nonce in that arrival
    /// is still live, and the right arrival enters on it afterwards.
    /// <para>Red on master: the line carried the token as <c>Log.Hex</c>, so the live nonce was in the log.</para></summary>
    [Fact]
    public void A_refused_arrival_logs_its_refusal_without_the_token_bytes()
    {
        string name = Account(7);
        var grant = HandoffTokens.Mint(name, Addr, Handoff.Game495);
        byte[] nonce = grant.Token[..HandoffTokens.SurvivingBytes(name)];
        byte[] right = StockArrival(name, grant);
        byte[] wrong = (byte[])right.Clone();
        wrong[1] = (byte)(wrong[1] == 0xFF ? 0x01 : wrong[1] + 1);

        using var sink = LogLineSink.Acquire();
        AssertRefusedAsABadToken(Arrive(wrong), sink, name, "a key wrong in one byte");
        AssertNotLogged(sink, nonce, "the refused arrival's live nonce");
        Assert.True(sink.Has($"(token 5 byte(s), {nonce.Length} expected; bytes not logged) — closing connection"),
                    "the refusal line no longer says how many token bytes came and how many were expected");
        AssertEntered(Arrive(right), "the right key, on the nonce the refusal left unspent");
    }

    /// <summary>An arrival whose name length runs past its body cannot be parsed. Its line still names the
    /// connection, the body's size and the exception, but none of its bytes, though those bytes here are a live key
    /// and nonce: the same hand-off enters afterwards.
    /// <para>Red on master: the line carried the whole body as hex, key and nonce included.</para></summary>
    [Fact]
    public void An_unparsable_arrival_logs_its_size_without_its_key_or_nonce()
    {
        string name = Account(7);
        var grant = HandoffTokens.Mint(name, Addr, Handoff.Game495);
        byte[] nonce = grant.Token[..HandoffTokens.SurvivingBytes(name)];
        byte[] right = StockArrival(name, grant);
        byte[] broken = (byte[])right.Clone();
        broken[1 + HandoffTokens.KeyBytes] = 40;   // the name's length byte now claims more than the body holds

        using var sink = LogLineSink.Acquire();
        // The name was never read, so the refusal names the session's placeholder, "?".
        AssertRefusedAsABadToken(Arrive(broken), sink, "?", "a name length past the body");
        AssertNotLogged(sink, grant.Key!, "the unparsable arrival's live key");
        AssertNotLogged(sink, nonce, "the unparsable arrival's live nonce");
        Assert.True(sink.Has($"arrival body could not be parsed ({broken.Length}B; key and token bytes not logged)"),
                    "no parse-failure line, or it no longer gives the body's size");
        AssertEntered(Arrive(right), "the same hand-off, whose key and nonce the refusal left unspent");
    }

    /// <summary><c>P1998_ENFORCE_HANDOFF=0</c> still downgrades the whole check, the key included: a wrong key
    /// enters with the warning line.
    /// <para>Red on master: the key was never checked, so the arrival entered without the warning.</para></summary>
    [Fact]
    public void Enforce_handoff_0_lets_a_wrong_key_in_with_its_warning()
    {
        string name = Account(11);
        var grant = HandoffTokens.Mint(name, Addr, Handoff.Game495);
        byte[] wrong = StockArrival(name, TkCrypt.LoginKey, grant.Token);

        string? previous = Environment.GetEnvironmentVariable("P1998_ENFORCE_HANDOFF");
        bool enforcedBefore = ServerConfig.Current.EnforceHandoff;
        Environment.SetEnvironmentVariable("P1998_ENFORCE_HANDOFF", "0");
        ServerConfig.ReloadForTests();
        try
        {
            Assert.False(ServerConfig.Current.EnforceHandoff);
            using var sink = LogLineSink.Acquire();
            AssertEntered(Arrive(wrong), "a wrong key with the check downgraded");
            Assert.True(sink.Has($"ARRIVAL WARN: invalid handoff token for user='{name}' — allowed (P1998_ENFORCE_HANDOFF=0)"),
                        "the downgraded arrival entered without its warning");
        }
        finally
        {
            Environment.SetEnvironmentVariable("P1998_ENFORCE_HANDOFF", previous);
            ServerConfig.ReloadForTests();
        }
        Assert.Equal(enforcedBefore, ServerConfig.Current.EnforceHandoff);
    }

    /// <summary>The 5.33 door is today's for a 5.33 hand-off: <c>"NexonInc."</c> in the key field, entering on the
    /// nonce and the address. A token minted for the 4.95 port, presented there with its own key, is refused like a
    /// bad token, and still enters at 4.95 afterwards.
    /// <para>Red on master for the refusal: any port consumed any token.</para></summary>
    [Fact]
    public void The_533_door_admits_its_own_hand_off_and_refuses_a_495_token()
    {
        string own = Account(9);
        AssertEntered(Arrive(StockArrival(own, HandoffTokens.Mint(own, Addr, Handoff.Game533)), Handoff.Game533),
                      "a 5.33 hand-off at the 5.33 port");

        string other = Account(9);
        var grant = HandoffTokens.Mint(other, Addr, Handoff.Game495);
        using (var sink = LogLineSink.Acquire())
            AssertRefusedAsABadToken(Arrive(StockArrival(other, grant), Handoff.Game533), sink, other,
                                     "a 4.95 token at the 5.33 port");
        AssertEntered(Arrive(StockArrival(other, grant)), "the same token at the 4.95 port");
    }

    /// <summary>The bounce back to login on Alt+X (<c>0x0B</c>) keeps <c>"NexonInc."</c> in the key field and a
    /// zero tail, on both channels: the login server checks the password itself, so this redirect carries no
    /// secret. Falsified by sending a minted key there (<c>HandoffTokens.Mint(...).Key</c> in place of
    /// <c>TkCrypt.LoginKey</c> in <c>Session.HandleExitToSelect</c>): the key assert fails.</summary>
    [Theory]
    [InlineData(Handoff.Game495, 2000)]
    [InlineData(Handoff.Game533, 2001)]
    public void The_bounce_to_login_still_sends_NexonInc(int gamePort, int loginPort)
    {
        var c = CharacterFactory.FromCreate("bouncer", new byte[] { 0x29, 0, 1, 0, 0 });
        var outbound = new RecordingOutbound("recorder:bounce");
        var session = new Session(outbound, gamePort, _db.Store, _world, c);
        session.Receive(SessionFixture.Frame(ClientOp.ExitToSelect, new byte[] { 0 }));

        byte[] p = Assert.Single(outbound.Frames, f => f.Length > 3 && f[3] == Opcode.Login);
        Assert.Equal(new byte[] { (byte)(loginPort >> 8), (byte)(loginPort & 0xFF) }, p[8..10]);
        Assert.Equal(new byte[] { 23, 0, 9 }, p[10..13]);
        Assert.Equal("NexonInc."u8.ToArray(), p[13..22]);
        Assert.Equal(new byte[LoginRedirect.TailBytes], p[^LoginRedirect.TailBytes..]);
    }
}
