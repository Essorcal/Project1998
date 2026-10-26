using Microsoft.Data.Sqlite;
using Protocol.Tk495;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The login→game handoff token, mint through consume. These run against a real SQLite database in a
/// throwaway state directory, because the guarantees under test are single-use and expiry — both enforced
/// by the SQL, so a mock would only prove the mock works.
///
/// The regression that motivated this file: a freshly created 8-character account could log in but was then
/// bounced at world entry with "invalid/expired handoff token". The client shares ONE fixed 13-byte field
/// between the username and the nonce, so a name longer than 7 characters truncates the nonce — and the
/// game server was comparing a fixed 4 bytes it would never receive.
///
/// <para>Since 2026-10-26 a hand-off to the 4.95 game port also carries a one-time key in the redirect's 9-byte
/// key field, which the client returns whole at every name length. The facts mint and consume on that port unless
/// they name the 5.33 one, so they run the keyed path the 4.95 client takes.</para>
/// </summary>
[Collection("db")]
public class HandoffTokenTests : IDisposable
{
    private const string Ip = "203.0.113.7";           // TEST-NET-3; never a real client
    private const int Game495 = Handoff.Game495;       // keyed
    private const int Game533 = Handoff.Game533;       // no key, "NexonInc." on the wire
    private readonly List<string> _minted = new();

    /// <summary>A throwaway username of EXACTLY <paramref name="length"/> characters — the length is the
    /// whole point of these tests — and unique, so two names in one test are genuinely different accounts.</summary>
    private string Name(int length)
    {
        var n = ("zz" + Guid.NewGuid().ToString("N"))[..length];
        _minted.Add(n);
        return n;
    }

    public void Dispose()
    {
        try
        {
            using var cn = Db.Open();
            foreach (var n in _minted)
            {
                using var cmd = cn.CreateCommand();
                cmd.CommandText = "DELETE FROM handoff_tokens WHERE username=$u;";
                cmd.Parameters.AddWithValue("$u", Auth.Key(n));
                cmd.ExecuteNonQuery();
            }
        }
        catch { /* best effort cleanup */ }
    }

    /// <summary>The client's handoff field as these tests model it: 13 bytes, the last forced to NUL. Written
    /// out here rather than read from <see cref="HandoffTokens"/>, so the server's constants have an
    /// independent number to disagree with.</summary>
    private const int ClientField = 13;

    /// <summary>Simulate the client's copy of our 0x03 reply tail: it strncpy's
    /// &lt;ulen&gt;&lt;username&gt;&lt;nonce&gt; into a 13-byte field (12 bytes + a forced NUL) and echoes
    /// the field back in 0x10. This is the exact transform that broke long names.</summary>
    private static byte[] ClientEcho(string user, byte[] nonce)
    {
        var blob = new List<byte> { (byte)user.Length };
        blob.AddRange(System.Text.Encoding.ASCII.GetBytes(user));
        blob.AddRange(nonce);
        while (blob.Count < ClientField) blob.Add(0);   // strncpy pads the remainder with NULs
        blob = blob.GetRange(0, ClientField);
        blob[ClientField - 1] = 0;                      // ...and the field is always NUL-terminated
        return blob.GetRange(1 + user.Length, ClientField - 1 - user.Length).ToArray();   // the token slot
    }

    /// <summary>What the client hands back from one hand-off: the token slot of its 13-byte field, and the key
    /// field whole, which sits outside that field. That is the key the redirect carried, or <c>"NexonInc."</c>
    /// when it carried none.</summary>
    private readonly record struct Echo(byte[] Token, byte[] Key);

    private static Echo EchoOf(string user, HandoffTokens.Grant grant) =>
        new(ClientEcho(user, grant.Token), grant.Key ?? TkCrypt.LoginKey);

    /// <summary>A mint on the process database, as the client echoes it back.</summary>
    private static Echo MintEcho(string user, int port = Game495, string ip = Ip) =>
        EchoOf(user, HandoffTokens.Mint(user, ip, port));

    /// <summary>The game server's consume on the process database.</summary>
    private static bool Enters(Echo echo, string user, int port = Game495, string ip = Ip) =>
        HandoffTokens.Consume(echo.Token, echo.Key, user, ip, port);

    /// <summary>The stored form of a key: the hex SHA-256, as <c>key_hash</c> holds it.</summary>
    private static string KeyHash(byte[] key) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(key));

    // 7 chars was the only length ever probed, which is how the fixed-4-bytes assumption survived. The 8-
    // to 10-char cases are the bug: each one used to be rejected at world entry.
    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void TruncatedTokenStillConsumes(int nameLength)
    {
        var user = Name(nameLength);
        var grant = HandoffTokens.Mint(user, Ip, Game495);
        Assert.Equal(5, grant.Token.Length);   // the wire slot is always 5 bytes, however much survives

        Assert.True(Enters(EchoOf(user, grant), user),
            $"a {nameLength}-character name must survive the client's truncation");
    }

    [Fact]
    public void TokenIsSingleUse()
    {
        var user = Name(8);
        var echo = MintEcho(user);

        Assert.True(Enters(echo, user));
        Assert.False(Enters(echo, user));   // replayed 0x10
    }

    [Fact]
    public void TokenIsBoundToTheLoginAddress()
    {
        var user = Name(8);
        var echo = MintEcho(user);
        Assert.False(Enters(echo, user, ip: "198.51.100.9"));
    }

    [Fact]
    public void TokenIsBoundToTheUsername()
    {
        var user = Name(8);
        var other = Name(8);
        var echo = MintEcho(user);
        Assert.False(Enters(echo, other));
    }

    /// <summary>A wrong nonce must still fail — the truncation fix must not have degraded into accepting
    /// anything of the right length, and the key must not have replaced the nonce check.</summary>
    [Fact]
    public void WrongNonceIsRejected()
    {
        var user = Name(8);
        var echo = MintEcho(user);
        Assert.False(Enters(echo with { Token = new byte[] { 1, 2, 3, 0 } }, user));
    }

    /// <summary>The truncation budget the two sides derive independently. If this table ever changes, the
    /// login and game servers must change together or every login breaks.</summary>
    [Theory]
    [InlineData(3, 4)]
    [InlineData(7, 4)]
    [InlineData(8, 3)]
    [InlineData(9, 2)]
    [InlineData(10, 1)]
    [InlineData(11, 0)]
    [InlineData(12, 0)]
    public void SurvivingBytesMatchesTheClientField(int nameLength, int expected) =>
        Assert.Equal(expected, HandoffTokens.SurvivingBytes(new string('x', nameLength)));

    /// <summary>#299: the name cap is the client's field, not a number of its own. The field's usable bytes
    /// carry the length byte and then the name, so the longest name that arrives whole is one less than
    /// the field, and at that length nothing of the nonce is left. At 12 letters the name itself was cut,
    /// and the account was created but could never enter the world.</summary>
    [Fact]
    public void MaxNameLengthIsTheClientFieldLessItsLengthByte()
    {
        Assert.Equal(ClientField - 1, HandoffTokens.HandoffFieldBytes);
        Assert.Equal(ClientField - 1, 1 + HandoffTokens.MaxNameLength);
        Assert.Equal(0, HandoffTokens.SurvivingBytes(new string('x', HandoffTokens.MaxNameLength)));
    }

    /// <summary>The longest name creation allows still enters the world: no nonce byte survives, so its token
    /// consumes on the key, the username and the address.</summary>
    [Fact]
    public void LongestAllowedNameStillConsumes()
    {
        var user = Name(HandoffTokens.MaxNameLength);
        Assert.True(Enters(MintEcho(user), user),
            $"a {HandoffTokens.MaxNameLength}-character name must survive the client's truncation");
    }


    // ---- the table's key -------------------------------------------------------------------------------
    // Every fact below runs on a database file of its own (IsolatedDatabase, or a file it builds by hand),
    // with an explicit clock, so what it counts and what it collides with is only what it wrote.

    /// <summary>A fixed clock for the isolated facts, in unix seconds.</summary>
    private const long T0 = 1_800_000_000;

    private const string OtherIp = "198.51.100.9";   // TEST-NET-2

    /// <summary>A throwaway username of exactly <paramref name="length"/> characters for a fact on its own
    /// database file: nothing to clean up in the process database, so it is not recorded for Dispose.</summary>
    private static string Fresh(int length) => ("zz" + Guid.NewGuid().ToString("N"))[..length];

    private static Echo MintAt(Func<SqliteConnection> open, string user, long now, string ip = Ip, int port = Game495) =>
        EchoOf(user, HandoffTokens.Mint(user, ip, port, open, now));

    private static bool ConsumeAt(Func<SqliteConnection> open, Echo echo, string user, long now, string ip = Ip,
                                  int port = Game495) =>
        HandoffTokens.Consume(echo.Token, echo.Key, user, ip, port, open, now);

    /// <summary>One login as the two servers perform it: the mint the login server makes, the client's
    /// truncating echo, and the consume the game server makes on arrival.</summary>
    private static bool Login(Func<SqliteConnection> open, string user, long now, string ip = Ip) =>
        ConsumeAt(open, MintAt(open, user, now, ip), user, now, ip);

    private static long Scalar(Func<SqliteConnection> open, string sql)
    {
        using var cn = open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static long Rows(Func<SqliteConnection> open) => Scalar(open, "SELECT COUNT(*) FROM handoff_tokens;");

    private static void Exec(Func<SqliteConnection> open, string sql)
    {
        using var cn = open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // -- the reproduction: red before this change, one fact per case the defect names --

    [Fact]
    public void ElevenLetterAccountEntersEveryTime()
    {
        using var db = new IsolatedDatabase();
        var user = Fresh(11);
        for (int i = 1; i <= 2; i++)
            Assert.True(Login(db.Open, user, T0 + i), $"login {i} of one 11-letter account was refused");
    }

    [Fact]
    public void TwoElevenLetterAccountsBothEnter()
    {
        using var db = new IsolatedDatabase();
        Assert.True(Login(db.Open, Fresh(11), T0), "the first 11-letter account was refused");
        Assert.True(Login(db.Open, Fresh(11), T0 + 1), "the second 11-letter account was refused");
    }

    [Fact]
    public void TenLetterAccountEntersThreeHundredTimes()
    {
        using var db = new IsolatedDatabase();
        var user = Fresh(10);
        for (int i = 1; i <= 300; i++)
            Assert.True(Login(db.Open, user, T0 + i), $"login {i} of one 10-letter account was refused");
    }

    // -- one guard each --

    /// <summary>The key. Two 11-letter logins in flight at once — both minted before either arrives — hash
    /// to the same value (no nonce byte survives), so only a key that includes the account keeps them
    /// apart.</summary>
    [Fact]
    public void TwoElevenLetterAccountsInFlightTogetherBothEnter()
    {
        using var db = new IsolatedDatabase();
        var first = Fresh(11);
        var second = Fresh(11);
        var firstEcho = MintAt(db.Open, first, T0);
        var secondEcho = MintAt(db.Open, second, T0);
        Assert.True(ConsumeAt(db.Open, firstEcho, first, T0), "the first account's token was lost to the second's");
        Assert.True(ConsumeAt(db.Open, secondEcho, second, T0), "the second account's token was never recorded");
    }

    /// <summary>A user's own live row must not block a new mint: the first login's client never arrived, and
    /// the player logs in again from another address inside the TTL. For an 11-letter name the two tokens
    /// share one key, so the second has to replace the first.</summary>
    [Fact]
    public void RetryFromAnotherAddressBeforeTheFirstLoginArrivesEnters()
    {
        using var db = new IsolatedDatabase();
        var user = Fresh(11);
        MintAt(db.Open, user, T0);
        var retry = MintAt(db.Open, user, T0 + 1, OtherIp);
        Assert.True(ConsumeAt(db.Open, retry, user, T0 + 1, OtherIp),
            "the retry's token was blocked by the first login's unconsumed row");
    }

    /// <summary>The purge, for spent tokens: a thousand logins by a thousand accounts of every allowed length,
    /// and the table never holds more than the one row the latest login left behind.</summary>
    [Fact]
    public void SpentTokensDoNotAccumulateAcrossAThousandLogins()
    {
        using var db = new IsolatedDatabase();
        long most = 0;
        for (int i = 0; i < 1000; i++)
        {
            int length = 3 + i % (HandoffTokens.MaxNameLength - 2);   // 3..11
            Assert.True(Login(db.Open, Fresh(length), T0 + i), $"login {i + 1} ({length} letters) was refused");
            most = Math.Max(most, Rows(db.Open));
        }
        Assert.Equal(1, most);
    }

    /// <summary>The purge, for abandoned tokens: a thousand logins whose clients never arrive, one a second.
    /// A row lives out its 60-second TTL and is gone at the next mint after it, so the table holds at most the
    /// logins of the last minute.</summary>
    [Fact]
    public void ExpiredTokensDoNotAccumulateAcrossAThousandLogins()
    {
        using var db = new IsolatedDatabase();
        long most = 0;
        for (int i = 0; i < 1000; i++)
        {
            MintAt(db.Open, Fresh(7), T0 + i);
            most = Math.Max(most, Rows(db.Open));
        }
        Assert.Equal(60, most);
    }

    /// <summary>
    /// The facts that read their Warn or Error line from the log, run in <c>"log"</c> rather than this class's
    /// <c>"db"</c>. Each takes the log line sink, which is one process-wide slot, and a capture collects every
    /// running test's lines. <c>"db"</c> runs beside the rest of the parallel phase; <c>"log"</c> runs alone,
    /// after it (<c>Tests/Support/ExclusiveCollections.cs</c>). <see cref="TestSeamCollectionTests"/> fails a
    /// capture taken anywhere but <c>"world"</c> or a collection that runs alone.
    ///
    /// <para>The class is nested only so the facts keep this class's private helpers. xunit takes a test
    /// class's collection off the class itself, never off the type around it, so these run in <c>"log"</c>
    /// and the rest of this class stays in <c>"db"</c>. Each fact builds its own
    /// <see cref="IsolatedDatabase"/>, so neither needs the process database.</para>
    /// </summary>
    [Collection("log")]
    public sealed class HandoffTokenWarnTests
    {
        /// <summary>A mint that cannot record its token says so, at Warn, with the database's reason, and the
        /// login it belongs to is refused at the door rather than let in unchecked.</summary>
        [Fact]
        public void AMintThatCannotRecordItsTokenWarnsWithTheReason()
        {
            using var db = new IsolatedDatabase();
            Exec(db.Open, "CREATE TRIGGER refuse_mint BEFORE INSERT ON handoff_tokens " +
                          "BEGIN SELECT RAISE(ABORT, 'test trigger refuses this insert'); END;");
            var user = Fresh(9);
            Echo echo;
            LogLineSink.Entry warning;
            using (var sink = LogLineSink.Acquire())
            {
                echo = MintAt(db.Open, user, T0);
                warning = sink.EntryContaining($"handoff token for '{user}' could not be recorded");
            }
            Assert.Equal(LogLevel.Warn, warning.Level);
            Assert.Contains("test trigger refuses this insert", warning.Line);
            Assert.False(ConsumeAt(db.Open, echo, user, T0));
        }

        /// <summary>The purge and the insert are independent: a purge the database refuses is logged at Warn and
        /// the login still enters.</summary>
        [Fact]
        public void APurgeThatFailsWarnsAndTheLoginStillEnters()
        {
            using var db = new IsolatedDatabase();
            Assert.True(Login(db.Open, Fresh(8), T0));   // leaves a consumed row for the next purge to meet
            Exec(db.Open, "CREATE TRIGGER refuse_purge BEFORE DELETE ON handoff_tokens " +
                          "BEGIN SELECT RAISE(ABORT, 'test trigger refuses this delete'); END;");
            var user = Fresh(8);
            bool entered;
            LogLineSink.Entry warning;
            using (var sink = LogLineSink.Acquire())
            {
                entered = Login(db.Open, user, T0 + 1);
                warning = sink.EntryContaining("handoff token purge failed");
            }
            Assert.True(entered, "a failed purge stopped the mint");
            Assert.Equal(LogLevel.Warn, warning.Level);
            Assert.Contains("test trigger refuses this delete", warning.Line);
        }

        /// <summary>#332: a consume the database refuses is logged once, at Error, with the account and the
        /// exception, and still answers false, as an invalid token; what the arrival does with that answer is
        /// the arrival's, and unchanged. The line carries neither the token's bytes nor the key's, nor either
        /// stored hash. Before this the fault was swallowed and the arrival's "invalid/expired handoff token"
        /// line was all an operator saw.</summary>
        [Fact]
        public void AConsumeTheDatabaseRefusesIsAnErrorAndStillRefuses()
        {
            using var db = new IsolatedDatabase();
            var user = Fresh(7);   // four nonce bytes survive: the most a line could leak
            var echo = MintAt(db.Open, user, T0);
            Exec(db.Open, "CREATE TRIGGER refuse_consume BEFORE UPDATE ON handoff_tokens " +
                          "BEGIN SELECT RAISE(ABORT, 'test trigger refuses this update'); END;");
            bool entered;
            LogLineSink.Entry[] errors;
            using (var sink = LogLineSink.Acquire())
            {
                entered = ConsumeAt(db.Open, echo, user, T0);
                errors = sink.Lines.Where(l => l.Line.Contains($"handoff token for '{user}' could not be checked",
                                                               StringComparison.Ordinal)).ToArray();
            }
            Assert.False(entered, "a consume the database refused answered true");
            var error = Assert.Single(errors);
            Assert.Equal(LogLevel.Error, error.Level);
            Assert.Contains("answered as an invalid token", error.Line);
            Assert.Contains("test trigger refuses this update", error.Line);
            Assert.Contains(typeof(SqliteException).FullName!, error.Line);
            var surviving = echo.Token[..HandoffTokens.SurvivingBytes(user)];
            Assert.DoesNotContain(Convert.ToHexString(surviving), error.Line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(PreviousHash(user, echo.Token), error.Line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Convert.ToHexString(echo.Key), error.Line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(KeyHash(echo.Key), error.Line, StringComparison.OrdinalIgnoreCase);
        }
    }

    // -- the security properties, where the key change could have touched them --

    /// <summary>Two 11-letter names carry no nonce byte at all, so only the key, the username and the address
    /// bind a token. A's token presented as B is refused both when B has no token and when B's token is bound to
    /// another address, and consuming A's leaves B's alone.</summary>
    [Fact]
    public void ElevenLetterTokenIsBoundToItsAccountAndAddress()
    {
        using var db = new IsolatedDatabase();
        var a = Fresh(11);
        var b = Fresh(11);
        var echoA = MintAt(db.Open, a, T0);
        Assert.False(ConsumeAt(db.Open, echoA, b, T0), "A's token entered as B, who has no token");
        var echoB = MintAt(db.Open, b, T0, OtherIp);
        Assert.False(ConsumeAt(db.Open, echoA, b, T0), "A's token entered as B, whose token is bound to another address");
        Assert.True(ConsumeAt(db.Open, echoA, a, T0), "A's own token was refused after B minted");
        Assert.True(ConsumeAt(db.Open, echoB, b, T0, OtherIp), "consuming A's token consumed B's");
        Assert.False(ConsumeAt(db.Open, echoA, a, T0), "A's arrival replayed");
    }

    /// <summary>The TTL is 60 seconds from the mint: stored as such, accepted at 59, refused at 60.</summary>
    [Fact]
    public void TokenExpiresSixtySecondsAfterItsMint()
    {
        using var db = new IsolatedDatabase();
        var user = Fresh(7);
        var late = MintAt(db.Open, user, T0);
        Assert.Equal(T0 + 60, Scalar(db.Open, "SELECT expires_utc FROM handoff_tokens;"));
        Assert.False(ConsumeAt(db.Open, late, user, T0 + 60), "a token was accepted at its expiry");

        var other = Fresh(7);
        var inTime = MintAt(db.Open, other, T0);
        Assert.True(ConsumeAt(db.Open, inTime, other, T0 + 59), "a token was refused a second before its expiry");
    }

    // -- the redirect key (2026-10-26): one guard per fact the change names --

    /// <summary>An 11-letter name carries no nonce byte, so before the key its token entered on the address alone.
    /// It now enters with the key its redirect carried and is refused with any other: each of the nine bytes
    /// changed in turn, and <c>"NexonInc."</c>, what the field held before. The refusals spend nothing, so the
    /// right key still enters after them.
    /// <para>Red on master, where the consume never read a key. Falsified by dropping the key predicate,
    /// <c>AND (key_hash=$k OR key_hash IS NULL)</c>, from the keyed consume: every wrong key enters.</para></summary>
    [Fact]
    public void ElevenLetterNameEntersWithItsKeyAndIsRefusedWithAWrongOne()
    {
        using var db = new IsolatedDatabase();
        var user = Fresh(11);
        var echo = MintAt(db.Open, user, T0);
        Assert.Equal(HandoffTokens.KeyBytes, echo.Key.Length);
        for (int i = 0; i < HandoffTokens.KeyBytes; i++)
        {
            var wrong = (byte[])echo.Key.Clone();
            wrong[i] = (byte)(wrong[i] == 0xFF ? 0x01 : wrong[i] + 1);
            Assert.False(ConsumeAt(db.Open, echo with { Key = wrong }, user, T0), $"a key wrong in byte {i} entered");
        }
        Assert.False(ConsumeAt(db.Open, echo with { Key = TkCrypt.LoginKey }, user, T0), "\"NexonInc.\" entered for a keyed token");
        Assert.True(ConsumeAt(db.Open, echo, user, T0), "the right key was refused, or a wrong one spent the token");
    }

    /// <summary>A key field of any other width is refused like a wrong key: none at all (<c>klen</c> 0), one byte
    /// short or long, and a null. The client always sends <c>klen</c> 9, so another width is not a client this
    /// server has seen.</summary>
    [Fact]
    public void AMissingKeyOrAnotherKeyLengthIsRefused()
    {
        using var db = new IsolatedDatabase();
        var user = Fresh(11);
        var echo = MintAt(db.Open, user, T0);
        Assert.False(ConsumeAt(db.Open, echo with { Key = Array.Empty<byte>() }, user, T0), "an arrival with no key entered");
        Assert.False(ConsumeAt(db.Open, echo with { Key = echo.Key[..8] }, user, T0), "an 8-byte key entered");
        Assert.False(ConsumeAt(db.Open, echo with { Key = [.. echo.Key, 0x01] }, user, T0), "a 10-byte key entered");
        Assert.False(ConsumeAt(db.Open, echo with { Key = null! }, user, T0), "a null key entered");
        Assert.True(ConsumeAt(db.Open, echo, user, T0), "the right key was refused after the malformed ones");
    }

    /// <summary>A key is single-use with its token: the arrival that spends it spends the row, and the same nine
    /// bytes presented again are refused. Falsified by consuming without marking the row
    /// (<c>SET consumed=consumed</c>): the replay enters.</summary>
    [Fact]
    public void AReplayedKeyIsRefused()
    {
        using var db = new IsolatedDatabase();
        var user = Fresh(11);
        var echo = MintAt(db.Open, user, T0);
        Assert.True(ConsumeAt(db.Open, echo, user, T0));
        Assert.False(ConsumeAt(db.Open, echo, user, T0), "a key was accepted twice");
    }

    /// <summary>A key is bound to the account and the address it was minted for, like the token it rides with:
    /// A's key is refused as B (who has no token, then one of their own), from another address, and B's key does
    /// not open A's row. Each of the two still enters with its own.</summary>
    [Fact]
    public void AKeyMintedForAnotherNameOrAddressIsRefused()
    {
        using var db = new IsolatedDatabase();
        var a = Fresh(11);
        var b = Fresh(11);
        var echoA = MintAt(db.Open, a, T0);
        Assert.False(ConsumeAt(db.Open, echoA, b, T0), "A's key entered as B, who has no token");
        Assert.False(ConsumeAt(db.Open, echoA, a, T0, OtherIp), "A's key entered from another address");
        var echoB = MintAt(db.Open, b, T0);
        Assert.False(ConsumeAt(db.Open, echoA, b, T0), "A's key entered as B");
        Assert.False(ConsumeAt(db.Open, echoA with { Key = echoB.Key }, a, T0), "B's key opened A's token");
        Assert.True(ConsumeAt(db.Open, echoA, a, T0), "A's own key was refused");
        Assert.True(ConsumeAt(db.Open, echoB, b, T0), "B's own key was refused");
    }

    /// <summary>Every key byte is 1-255 over a thousand mints, every key is nine bytes, none is
    /// <c>"NexonInc."</c>, and no two are alike. Falsified by drawing the bytes from 0-255
    /// (<c>GetInt32(0, 256)</c>): over 9,000 bytes a zero is all but certain ((255/256)^9000 is about 5e-16).</summary>
    [Fact]
    public void KeyBytesAreNonZeroOverManyMints()
    {
        using var db = new IsolatedDatabase();
        var seen = new HashSet<string>();
        for (int i = 0; i < 1000; i++)
        {
            var grant = HandoffTokens.Mint(Fresh(11), Ip, Game495, db.Open, T0);
            Assert.NotNull(grant.Key);
            Assert.Equal(HandoffTokens.KeyBytes, grant.Key!.Length);
            Assert.DoesNotContain((byte)0, grant.Key);
            Assert.NotEqual(TkCrypt.LoginKey, grant.Key);
            Assert.True(seen.Add(Convert.ToHexString(grant.Key)), $"mint {i + 1} repeated an earlier key");
        }
    }

    /// <summary>The key is stored only as its SHA-256, beside the nonce hash on the same row, which keeps its
    /// meaning: the SHA-256 of the surviving nonce bytes and nothing else, so a server from before the key column
    /// still finds the row (the deploy window, below).</summary>
    [Fact]
    public void TheKeyIsStoredOnlyAsItsHashBesideAnUnchangedNonceHash()
    {
        using var db = new IsolatedDatabase();
        var user = Fresh(7);
        var echo = MintAt(db.Open, user, T0);
        using var cn = db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT nonce_hash, key_hash FROM handoff_tokens;";
        using var row = cmd.ExecuteReader();
        Assert.True(row.Read());
        Assert.Equal(PreviousHash(user, echo.Token), row.GetString(0));
        Assert.Equal(KeyHash(echo.Key), row.GetString(1));
        Assert.False(row.Read());
    }

    /// <summary>A 5.33 hand-off is today's: no key minted (the redirect carries <c>"NexonInc."</c>), the row
    /// exactly what the code before the key wrote (the same nonce hash, <c>key_hash</c> NULL), and its port reads
    /// no key, so it enters whatever the key field holds. Falsified by keying every port
    /// (<c>CarriesKey</c> returning true): the grant has a key and the stored row a hash.</summary>
    [Fact]
    public void AFiveThreeThreeHandOffIsUnchanged()
    {
        using var db = new IsolatedDatabase();
        Assert.False(HandoffTokens.CarriesKey(Game533));
        foreach (int length in new[] { 7, 11 })
        {
            var user = Fresh(length);
            var grant = HandoffTokens.Mint(user, Ip, Game533, db.Open, T0);
            Assert.Null(grant.Key);
            var echo = EchoOf(user, grant);
            Assert.Equal(TkCrypt.LoginKey, echo.Key);
            Assert.Equal(1, Scalar(db.Open, "SELECT COUNT(*) FROM handoff_tokens WHERE key_hash IS NULL " +
                                            $"AND nonce_hash='{PreviousHash(user, echo.Token)}' AND username='{Auth.Key(user)}';"));
            Assert.True(ConsumeAt(db.Open, echo, user, T0, port: Game533), $"a {length}-letter 5.33 hand-off was refused");

            var other = Fresh(length);
            var otherEcho = EchoOf(other, HandoffTokens.Mint(other, Ip, Game533, db.Open, T0));
            Assert.True(ConsumeAt(db.Open, otherEcho with { Key = Array.Empty<byte>() }, other, T0, port: Game533),
                        "the 5.33 port read the key field");
        }
    }

    /// <summary>The 5.33 port refuses a token minted for a 4.95 login, even with its key: spending it there would
    /// skip the key check its own port makes. The refusal spends nothing, and the token still enters at 4.95.
    /// <para>Red on master, where any port consumed any token. Falsified by dropping <c>AND key_hash IS NULL</c>
    /// from the 5.33 consume.</para></summary>
    [Fact]
    public void TheFiveThreeThreePortRefusesATokenMintedForFourNineFive()
    {
        using var db = new IsolatedDatabase();
        foreach (int length in new[] { 7, 11 })
        {
            var user = Fresh(length);
            var echo = MintAt(db.Open, user, T0);
            Assert.False(ConsumeAt(db.Open, echo, user, T0, port: Game533), $"a {length}-letter 4.95 token entered at 5.33");
            Assert.False(ConsumeAt(db.Open, echo with { Key = TkCrypt.LoginKey }, user, T0, port: Game533),
                         $"a {length}-letter 4.95 token entered at 5.33 with \"NexonInc.\"");
            Assert.True(ConsumeAt(db.Open, echo, user, T0), $"the {length}-letter token no longer entered at 4.95");
        }
    }

    /// <summary>The channel decides, and only the 5.33 port is exempt: the 4.95 port and a port the pairing does
    /// not know (a custom layout such as 7005) both mint a key and refuse an arrival without it. Falsified by
    /// keying only the 4.95 port (<c>gamePort == 2005</c>): the unknown port mints no key.</summary>
    [Fact]
    public void AnUnknownPortRequiresTheKey()
    {
        using var db = new IsolatedDatabase();
        Assert.True(HandoffTokens.CarriesKey(Game495));
        Assert.True(HandoffTokens.CarriesKey(7005));
        var user = Fresh(11);
        var echo = MintAt(db.Open, user, T0, port: 7005);
        Assert.NotEqual(TkCrypt.LoginKey, echo.Key);
        Assert.False(ConsumeAt(db.Open, echo with { Key = TkCrypt.LoginKey }, user, T0, port: 7005),
                     "an unknown port let a token in without its key");
        Assert.True(ConsumeAt(db.Open, echo, user, T0, port: 7005), "an unknown port refused the right key");
    }

    // -- the deploy window: a login server and a game server on either side of a change --

    // The two statements of Shared/HandoffTokens.cs as they were before the rekey (upstream/master
    // 9c00b98583495bf53684397f48beda773fdbd3ff, Mint and Consume), and the hash they used, computed here
    // rather than borrowed, so a change to the stored hash has something independent to disagree with.
    private const string PreviousMintSql = @"INSERT INTO handoff_tokens(nonce_hash, username, expires_utc, consumed, ip)
                                VALUES($h, $u, $e, 0, $ip);";
    private const string PreviousConsumeSql = @"UPDATE handoff_tokens SET consumed=1
                                WHERE nonce_hash=$h AND username=$u AND ip=$ip
                                  AND consumed=0 AND expires_utc>$now;";

    // Mint's statement as it was before the key column (upstream/master e67cd9df81e5784656a5f7f02971bd1dc073d9aa),
    // the last code that names no key_hash. Its consume is PreviousConsumeSql unchanged.
    private const string KeylessMintSql = @"INSERT INTO handoff_tokens(nonce_hash, username, expires_utc, consumed, ip)
                                VALUES($h, $u, $e, 0, $ip)
                                ON CONFLICT(username, nonce_hash) DO UPDATE
                                  SET expires_utc=excluded.expires_utc, consumed=0, ip=excluded.ip;";

    private static string PreviousHash(string user, byte[] echo) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(echo[..HandoffTokens.SurvivingBytes(user)]));

    /// <summary>A nonce as the previous login server minted one: four non-zero bytes and the terminator.</summary>
    private static readonly byte[] PreviousNonce = { 0x5A, 0x6B, 0x7C, 0x8D, 0 };

    private static int PreviousMint(Func<SqliteConnection> open, string user, byte[] echo, long expires)
    {
        using var cn = open();
        return PreviousMint(cn, user, echo, expires);
    }

    private static int PreviousMint(SqliteConnection cn, string user, byte[] echo, long expires) =>
        RunMint(cn, PreviousMintSql, user, echo, expires);

    private static int KeylessMint(Func<SqliteConnection> open, string user, byte[] echo, long expires)
    {
        using var cn = open();
        return KeylessMint(cn, user, echo, expires);
    }

    private static int KeylessMint(SqliteConnection cn, string user, byte[] echo, long expires) =>
        RunMint(cn, KeylessMintSql, user, echo, expires);

    private static int RunMint(SqliteConnection cn, string sql, string user, byte[] echo, long expires)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$h", PreviousHash(user, echo));
        cmd.Parameters.AddWithValue("$u", Auth.Key(user));
        cmd.Parameters.AddWithValue("$e", expires);
        cmd.Parameters.AddWithValue("$ip", Ip);
        return cmd.ExecuteNonQuery();
    }

    private static int PreviousConsume(Func<SqliteConnection> open, string user, byte[] echo, long now)
    {
        using var cn = open();
        return PreviousConsume(cn, user, echo, now);
    }

    private static int PreviousConsume(SqliteConnection cn, string user, byte[] echo, long now)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = PreviousConsumeSql;
        cmd.Parameters.AddWithValue("$h", PreviousHash(user, echo));
        cmd.Parameters.AddWithValue("$u", Auth.Key(user));
        cmd.Parameters.AddWithValue("$ip", Ip);
        cmd.Parameters.AddWithValue("$now", now);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>The window across the key column, both ways, on both game ports. This login server mints (a key
    /// on 4.95, none on 5.33) and the game server before it consumes, reading no key and no column. The login
    /// server before it mints (no <c>key_hash</c>, <c>"NexonInc."</c> on the wire) and this game server consumes:
    /// on 4.95 through the keyless path, on 5.33 as before. Then an 11-letter account's token from each side in
    /// flight at once.
    /// <para>Falsified one way by hashing the key into <c>nonce_hash</c> (the previous game server then refuses
    /// every keyed token), and the other by dropping <c>OR key_hash IS NULL</c> from the keyed consume (this game
    /// server then refuses the previous login server's 4.95 tokens).</para></summary>
    [Fact]
    public void TheServerBeforeThisChangeAndThisOneConsumeEachOthersTokens()
    {
        using var db = new IsolatedDatabase();
        foreach (int port in new[] { Game495, Game533 })
            foreach (int length in new[] { 7, 10, 11 })
            {
                var user = Fresh(length);   // this login server mints, the previous game server consumes
                Assert.True(PreviousConsume(db.Open, user, MintAt(db.Open, user, T0, port: port).Token, T0) == 1,
                    $"the previous game server refused a {length}-letter token this login server minted for {port}");

                var other = Fresh(length);  // the previous login server mints, this game server consumes
                var echo = ClientEcho(other, PreviousNonce);
                Assert.Equal(1, KeylessMint(db.Open, other, echo, expires: T0 + 60));
                Assert.True(ConsumeAt(db.Open, new Echo(echo, TkCrypt.LoginKey), other, T0, port: port),
                    $"this game server refused at {port} a {length}-letter token the previous login server minted");
            }

        // The previous login server mints for an 11-letter account while this code's token for another
        // 11-letter account is in flight.
        var ours = Fresh(11);
        var ourEcho = MintAt(db.Open, ours, T0 + 1);
        var theirs = Fresh(11);
        var theirEcho = ClientEcho(theirs, PreviousNonce);
        Assert.Equal(1, KeylessMint(db.Open, theirs, theirEcho, expires: T0 + 61));
        Assert.True(ConsumeAt(db.Open, new Echo(theirEcho, TkCrypt.LoginKey), theirs, T0 + 1),
            "the previous login server's in-flight token was refused");
        Assert.True(ConsumeAt(db.Open, ourEcho, ours, T0 + 1), "this login server's in-flight token was refused");
    }

    /// <summary>Migration 4 on the database a deployment had before it: the old key, with a live token, a
    /// consumed one and an expired one in it. The live one survives and still enters, the spent ones are
    /// gone, the key is the new one, and a second run changes nothing. A connection opened before the
    /// migration — the server that has not restarted yet — runs the previous code's statements on the new
    /// table.</summary>
    [Fact]
    public void RekeyMigrationKeepsLiveTokensAndDropsSpentOnes()
    {
        string dir = System.IO.Path.Combine(TestProcessState.StateDirectory, $"rekey-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, "project1998.db");
        Func<SqliteConnection> open = () => Db.Open(path);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();   // the migration reads SQLite's own clock

        var live = Fresh(11);
        var spent = Fresh(11);
        var expired = Fresh(10);
        var liveEcho = ClientEcho(live, PreviousNonce);
        using (var cn = new SqliteConnection($"Data Source={path}"))
        {
            cn.Open();
            using var cmd = cn.CreateCommand();
            // handoff_tokens exactly as upstream/master 9c00b98 declares it, at that schema's version (3).
            cmd.CommandText = @"
CREATE TABLE handoff_tokens (
  nonce_hash  TEXT PRIMARY KEY,
  username    TEXT NOT NULL,
  expires_utc INTEGER NOT NULL,
  consumed    INTEGER NOT NULL DEFAULT 0,
  ip          TEXT NOT NULL DEFAULT ''
);
PRAGMA journal_mode = WAL;
PRAGMA user_version = 3;";
            cmd.ExecuteNonQuery();
        }
        Assert.Equal(1, PreviousMint(open, live, liveEcho, expires: now + 60));
        Exec(open, $"INSERT INTO handoff_tokens VALUES('{new string('A', 64)}', '{spent}', {now + 60}, 1, '{Ip}');");
        Exec(open, $"INSERT INTO handoff_tokens VALUES('{new string('B', 64)}', '{expired}', {now - 1}, 0, '{Ip}');");
        Assert.False(PrimaryKeyIsUserThenHash(open), "the legacy table already had the new key");
        using var running = open();   // the process that has not restarted: its connection predates the rebuild

        Db.InitializeDatabase(path);

        Assert.True(PrimaryKeyIsUserThenHash(open), "migration 4 did not rekey the table");
        Assert.Equal(Db.CurrentSchemaVersion, Scalar(open, "PRAGMA user_version;"));
        Assert.Equal(1, Rows(open));
        var another = Fresh(11);   // the same hash as the live row, another account: collided before
        var anotherEcho = MintAt(open, another, now);
        var third = Fresh(11);     // the previous code's insert, on the running connection, for the same hash
        var thirdEcho = ClientEcho(third, PreviousNonce);
        Assert.Equal(1, PreviousMint(running, third, thirdEcho, expires: now + 60));
        Assert.True(ConsumeAt(open, new Echo(liveEcho, TkCrypt.LoginKey), live, now), "a token minted before the migration was lost");
        Assert.True(ConsumeAt(open, anotherEcho, another, now), "a mint after the migration was refused");
        Assert.Equal(1, PreviousConsume(running, third, thirdEcho, now));

        Db.InitializeDatabase(path);
        Assert.True(PrimaryKeyIsUserThenHash(open), "a second run changed the key");
        Assert.Equal(Db.CurrentSchemaVersion, Scalar(open, "PRAGMA user_version;"));
    }

    /// <summary>Migration 5 on the database a deployment has today: <c>handoff_tokens</c> exactly as
    /// upstream/master e67cd9d declares it, at schema version 4, with a live token for each game port minted by
    /// the code before the key. The migration adds <c>key_hash</c> as NULL on those rows, and both still enter at
    /// their ports. A connection opened before it, the server not yet restarted, runs that code's statements on
    /// the new table: its mint enters through this code's consume, and this code's keyed mint through its
    /// consume. A second run changes nothing.
    /// <para>Falsified by deleting the migration's entry from <c>Db.Migrations</c>: the column is never added
    /// (and this code's mint and consume, which name it, fail).</para></summary>
    [Fact]
    public void KeyColumnMigrationKeepsLiveTokensAndTheOldStatementsWork()
    {
        string dir = System.IO.Path.Combine(TestProcessState.StateDirectory, $"keycol-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, "project1998.db");
        Func<SqliteConnection> open = () => Db.Open(path);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using (var cn = new SqliteConnection($"Data Source={path}"))
        {
            cn.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE handoff_tokens (
  nonce_hash  TEXT NOT NULL,
  username    TEXT NOT NULL,
  expires_utc INTEGER NOT NULL,
  consumed    INTEGER NOT NULL DEFAULT 0,
  ip          TEXT NOT NULL DEFAULT '',
  PRIMARY KEY (username, nonce_hash)
);
PRAGMA journal_mode = WAL;
PRAGMA user_version = 4;";
            cmd.ExecuteNonQuery();
        }
        var live495 = Fresh(11);
        var live533 = Fresh(11);
        var echo495 = ClientEcho(live495, PreviousNonce);
        var echo533 = ClientEcho(live533, PreviousNonce);
        Assert.Equal(1, KeylessMint(open, live495, echo495, expires: now + 60));
        Assert.Equal(1, KeylessMint(open, live533, echo533, expires: now + 60));
        Assert.Equal(0, KeyColumns(open));
        using var running = open();   // the process that has not restarted: its connection predates the column

        Db.InitializeDatabase(path);

        Assert.Equal(1, KeyColumns(open));
        Assert.Equal(Db.CurrentSchemaVersion, Scalar(open, "PRAGMA user_version;"));
        Assert.Equal(2, Scalar(open, "SELECT COUNT(*) FROM handoff_tokens WHERE key_hash IS NULL;"));
        Assert.True(ConsumeAt(open, new Echo(echo495, TkCrypt.LoginKey), live495, now, port: Game495),
                    "a 4.95 token minted before the migration was lost");
        Assert.True(ConsumeAt(open, new Echo(echo533, TkCrypt.LoginKey), live533, now, port: Game533),
                    "a 5.33 token minted before the migration was lost");

        var theirs = Fresh(11);   // the previous code's mint on the running connection; this code consumes
        var theirEcho = ClientEcho(theirs, PreviousNonce);
        Assert.Equal(1, KeylessMint(running, theirs, theirEcho, expires: now + 60));
        Assert.True(ConsumeAt(open, new Echo(theirEcho, TkCrypt.LoginKey), theirs, now),
                    "this game server refused a token the not-yet-restarted login server minted");
        var ours = Fresh(11);     // this code's keyed mint; the previous code consumes on the running connection
        var ourEcho = MintAt(open, ours, now);
        Assert.Equal(1, PreviousConsume(running, ours, ourEcho.Token, now));

        Db.InitializeDatabase(path);
        Assert.Equal(1, KeyColumns(open));
        Assert.Equal(Db.CurrentSchemaVersion, Scalar(open, "PRAGMA user_version;"));
    }

    private static long KeyColumns(Func<SqliteConnection> open) =>
        Scalar(open, "SELECT COUNT(*) FROM pragma_table_info('handoff_tokens') WHERE name='key_hash';");

    private static bool PrimaryKeyIsUserThenHash(Func<SqliteConnection> open)
    {
        using var cn = open();
        return Db.PrimaryKeyIs(cn, null, "handoff_tokens", "username", "nonce_hash");
    }
}
