using Microsoft.Data.Sqlite;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// #332: a moderation read the database refuses is logged at Error, with the account (or the address) and the
/// exception, and still gets the answer it always got: no row, an empty list, not banned. Those answers are on
/// purpose (a database hiccup must not lock every player out), but they used to be silent, so a failed ban
/// read let a banned account in and nothing said so.
///
/// <para>Each fault fact makes its read fail on an <see cref="IsolatedDatabase"/> by dropping the table the
/// read needs, and reaches that file through the read's internal connection source, so the process database
/// and every test beside it are untouched. The last fact runs the same three reads on a database that
/// answers, to show the seam changes nothing there and that a read that works logs nothing.</para>
///
/// <para>In <c>"log"</c>, for <see cref="HandoffTokenTests.HandoffTokenWarnTests"/>' reason: each fact takes
/// the log line sink.</para>
/// </summary>
[Collection("log")]
public sealed class ModerationFaultTests
{
    private const string Ip = "203.0.113.7";   // TEST-NET-3; never a real client

    /// <summary>A throwaway account name, unique so a line can only be this fact's.</summary>
    private static string Fresh() => ("zz" + Guid.NewGuid().ToString("N"))[..10];

    private static void Exec(Func<SqliteConnection> open, string sql)
    {
        using var cn = open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every collected line carrying <paramref name="needle"/>.</summary>
    private static LogLineSink.Entry[] Matching(LogLineSink sink, string needle) =>
        sink.Lines.Where(l => l.Line.Contains(needle, StringComparison.Ordinal)).ToArray();

    /// <summary>Exactly one line, at Error, carrying the database's reason and the exception itself.</summary>
    private static void OneErrorWithTheFault(LogLineSink.Entry[] lines, string reason)
    {
        var line = Assert.Single(lines);
        Assert.Equal(LogLevel.Error, line.Level);
        Assert.Contains(reason, line.Line);
        Assert.Contains(typeof(SqliteException).FullName!, line.Line);
    }

    /// <summary>The account read behind the ban check at arrival and at login, the mute load at world entry,
    /// and <c>@unban</c>/<c>@unmute</c>.</summary>
    [Fact]
    public void AFailedAccountReadIsAnErrorAndStillAnswersNoRow()
    {
        using var db = new IsolatedDatabase();
        Exec(db.Open, "DROP TABLE moderation;");
        var user = Fresh();
        ModRecord? answer;
        LogLineSink.Entry[] lines;
        using (var sink = LogLineSink.Acquire())
        {
            answer = Moderation.Get(user, db.Open);
            lines = Matching(sink, $"moderation read for '{user}' failed");
        }
        Assert.Null(answer);
        OneErrorWithTheFault(lines, "no such table: moderation");
    }

    /// <summary>The list behind <c>@bans</c>, which answers "Nobody is banned or muted." for an empty one.</summary>
    [Fact]
    public void AFailedListReadIsAnErrorAndStillAnswersAnEmptyList()
    {
        using var db = new IsolatedDatabase();
        Exec(db.Open, "DROP TABLE moderation;");
        List<ModRecord> answer;
        LogLineSink.Entry[] lines;
        using (var sink = LogLineSink.Acquire())
        {
            answer = Moderation.ActiveList(50, db.Open);
            lines = Matching(sink, "moderation list read failed");
        }
        Assert.Empty(answer);
        OneErrorWithTheFault(lines, "no such table: moderation");
    }

    /// <summary>The address check at login, at the password change and at the game server's re-login.</summary>
    [Fact]
    public void AFailedAddressReadIsAnErrorAndStillAnswersNotBanned()
    {
        using var db = new IsolatedDatabase();
        Exec(db.Open, "DROP TABLE banned_ips;");
        bool banned;
        string reason;
        LogLineSink.Entry[] lines;
        using (var sink = LogLineSink.Acquire())
        {
            banned = Moderation.IsIpBanned(Ip, out reason, db.Open);
            lines = Matching(sink, $"ip ban read for {Ip} failed");
        }
        Assert.False(banned);
        Assert.Equal("", reason);
        OneErrorWithTheFault(lines, "no such table: banned_ips");
    }

    /// <summary>The same three reads on a database that answers: what is there comes back, and nothing is
    /// logged.</summary>
    [Fact]
    public void ReadsThatWorkAnswerWhatIsThereAndLogNothing()
    {
        using var db = new IsolatedDatabase();
        var user = Fresh();
        long now = Moderation.Now;
        Exec(db.Open, "INSERT INTO moderation(username, ban_until, ban_reason, ban_by, ban_at, " +
                      "mute_until, mute_reason, mute_by, mute_at) " +
                      $"VALUES('{Auth.Key(user)}', {Moderation.Forever}, 'botting', 'gm1', {now}, " +
                      $"{now + 3600}, 'spam', 'gm2', {now});");
        Exec(db.Open, $"INSERT INTO banned_ips(ip, until, reason, banned_by, banned_at) " +
                      $"VALUES('{Ip}', {Moderation.Forever}, 'shared box', 'gm1', {now});");
        ModRecord? row, none;
        List<ModRecord> list;
        bool ipBanned, otherBanned;
        string ipReason, otherReason;
        LogLineSink.Entry[] lines;
        using (var sink = LogLineSink.Acquire())
        {
            row = Moderation.Get(user, db.Open);
            none = Moderation.Get(Fresh(), db.Open);
            list = Moderation.ActiveList(50, db.Open);
            ipBanned = Moderation.IsIpBanned(Ip, out ipReason, db.Open);
            otherBanned = Moderation.IsIpBanned("198.51.100.9", out otherReason, db.Open);
            lines = sink.Lines.Where(l => l.Line.Contains("moderation read", StringComparison.Ordinal)
                                       || l.Line.Contains("moderation list read", StringComparison.Ordinal)
                                       || l.Line.Contains("ip ban read", StringComparison.Ordinal)).ToArray();
        }
        Assert.Equal(new ModRecord(Auth.Key(user), Moderation.Forever, "botting", "gm1", now,
                                   now + 3600, "spam", "gm2", now), row);
        Assert.True(row!.IsBanned && row.IsMuted);
        Assert.Null(none);
        Assert.Equal(row, Assert.Single(list));
        Assert.True(ipBanned);
        Assert.Equal("shared box", ipReason);
        Assert.False(otherBanned);
        Assert.Equal("", otherReason);
        Assert.Empty(lines);
    }
}
