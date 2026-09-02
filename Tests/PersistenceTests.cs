using Microsoft.Data.Sqlite;
using Shared;
using Xunit;

namespace Tests;

/// <summary>
/// Tests for the persistence guarantees the trade/parcel paths now depend on. These run against a real
/// SQLite database in a throwaway state directory, because the thing under test is SQLite transaction
/// behaviour — a mock would only prove the mock is transactional.
/// </summary>
[Collection("db")]
public class PersistenceTests : IDisposable
{
    // Distinct per test class run, and prefixed so a leaked row is obviously test debris.
    private readonly string _a = $"zz_test_a_{Guid.NewGuid():N}"[..24];
    private readonly string _b = $"zz_test_b_{Guid.NewGuid():N}"[..24];
    private readonly CharacterStore _store = new(RepoPaths.CharsDir());
    private readonly DbFixture _fixture;

    public PersistenceTests(DbFixture fixture) => _fixture = fixture;

    private static Character Make(string name, uint coins, params (int id, int amount)[] items)
    {
        var c = new Character { Name = name, Coins = coins };
        byte slot = 0;
        foreach (var (id, amount) in items) c.Inventory.Add(new InvItem(slot++, id, amount));
        return c;
    }

    public void Dispose()
    {
        // Leave no debris in the live database.
        try
        {
            using var cn = Db.Open();
            foreach (var n in new[] { _a, _b })
            {
                using var cmd = cn.CreateCommand();
                cmd.CommandText = "DELETE FROM characters WHERE username=$u;";
                cmd.Parameters.AddWithValue("$u", CharacterStore.Key(n));
                cmd.ExecuteNonQuery();
            }
        }
        catch { /* best effort cleanup */ }
    }

    [Fact]
    public void DatabaseUsesTheThrowawayStateDirectory()
    {
        Assert.Equal(Path.Combine(_fixture.StateDirectory, "project1998.db"), Db.Path);
        Assert.True(File.Exists(Db.Path));
    }

    /// <summary>The trade guarantee: both sides land together. This is the happy path — the failure path is
    /// the next test, and it's the one that actually matters.</summary>
    [Fact]
    public void SaveMany_PersistsEveryCharacter()
    {
        var a = Make(_a, 100, (1, 5));
        var b = Make(_b, 200);

        Assert.True(_store.SaveMany(new[] { a, b }));

        Assert.Equal(100u, _store.Load(_a)!.Coins);
        Assert.Equal(200u, _store.Load(_b)!.Coins);
        Assert.Equal(5, _store.Load(_a)!.Inventory.Single().Amount);
    }

    /// <summary>
    /// THE test. A trade moves goods from one character to the other; if the write fails partway, NEITHER
    /// side may be left changed — a half-applied exchange is a dupe or a vanish depending on which half won.
    ///
    /// <para>The failure is induced the way it would really happen: another connection holds the write lock,
    /// so SaveMany's transaction cannot commit. That exercises the actual contention path (SQLITE_BUSY after
    /// the busy_timeout) rather than a synthetic serializer error, which is why this test takes ~5s.</para>
    /// </summary>
    [Fact]
    public void SaveMany_LeavesNothingWritten_WhenTheWriteFails()
    {
        // Seed both at a known state: `a` holds 10 of item 1 and 100 coin, `b` holds nothing.
        var a = Make(_a, 100, (1, 10));
        var b = Make(_b, 100);
        Assert.True(_store.SaveMany(new[] { a, b }));

        // Stage the trade in memory: 5 of item 1 and 50 coin move from a to b.
        a.Inventory[0].Amount = 5;
        a.Coins = 50;
        b.Inventory.Add(new InvItem(0, 1, 5));
        b.Coins = 150;

        // Block all writes for the duration of the attempt.
        using (var blocker = Db.Open())
        {
            using var begin = blocker.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE;";   // takes the write lock and holds it
            begin.ExecuteNonQuery();

            Assert.False(_store.SaveMany(new[] { a, b }));

            using var rollback = blocker.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }

        // Neither side moved. `a` in particular must NOT have been debited — that is the half which, had it
        // committed alone, would have destroyed the goods outright.
        var reloadedA = _store.Load(_a)!;
        var reloadedB = _store.Load(_b)!;
        Assert.Equal(100u, reloadedA.Coins);
        Assert.Equal(10, reloadedA.Inventory.Single().Amount);
        Assert.Equal(100u, reloadedB.Coins);
        Assert.Empty(reloadedB.Inventory);
    }

    /// <summary>The parcel guarantee: the queue row and the character save commit together. Here the
    /// callback reports failure, standing in for "the parcel was already claimed by another path".</summary>
    [Fact]
    public void SaveWith_RollsBackTheRowAndTheCharacter_WhenTheWorkFails()
    {
        var a = Make(_a, 100);
        Assert.True(_store.SaveMany(new[] { a }));

        // Put a parcel-shaped row in the queue.
        using (var cn = Db.Open())
        {
            using var cmd = cn.CreateCommand();
            cmd.CommandText = @"INSERT INTO parcels(recipient, position, sender, item_id, item_amount, item_dura, engrave, month, day)
                                VALUES($r, 1, 'tester', 5, 3, 0, '', 1, 1);";
            cmd.Parameters.AddWithValue("$r", _a);
            cmd.ExecuteNonQuery();
        }

        // A claim that deletes the row, credits the character, then reports failure. Both halves must revert.
        a.Coins = 999;
        bool committed = _store.SaveWith(a, (cn, tx) =>
        {
            using var del = cn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM parcels WHERE recipient=$r COLLATE NOCASE AND position=1;";
            del.Parameters.AddWithValue("$r", _a);
            Assert.Equal(1, del.ExecuteNonQuery());
            return false;   // "already claimed" / the work decided not to proceed
        });

        Assert.False(committed);
        Assert.Equal(100u, _store.Load(_a)!.Coins);   // the character save rolled back with it
        Assert.Equal(1, ParcelCount(_a));             // and the parcel is still in the queue

        // Cleanup.
        using (var cn = Db.Open())
        {
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "DELETE FROM parcels WHERE recipient=$r COLLATE NOCASE;";
            cmd.Parameters.AddWithValue("$r", _a);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>And the committing path: the row goes and the character change stays.</summary>
    [Fact]
    public void SaveWith_CommitsBothHalvesTogether()
    {
        var a = Make(_a, 100);
        Assert.True(_store.SaveMany(new[] { a }));

        using (var cn = Db.Open())
        {
            using var cmd = cn.CreateCommand();
            cmd.CommandText = @"INSERT INTO parcels(recipient, position, sender, item_id, item_amount, item_dura, engrave, month, day)
                                VALUES($r, 1, 'tester', 5, 3, 0, '', 1, 1);";
            cmd.Parameters.AddWithValue("$r", _a);
            cmd.ExecuteNonQuery();
        }

        a.Coins = 400;
        bool committed = _store.SaveWith(a, (cn, tx) =>
        {
            using var del = cn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM parcels WHERE recipient=$r COLLATE NOCASE AND position=1;";
            del.Parameters.AddWithValue("$r", _a);
            return del.ExecuteNonQuery() == 1;
        });

        Assert.True(committed);
        Assert.Equal(400u, _store.Load(_a)!.Coins);
        Assert.Equal(0, ParcelCount(_a));
    }

    /// <summary>Facing survives a save/load round trip.
    ///
    /// <para>It didn't used to exist on the character at all — <c>Session._facing</c> was session-local, so
    /// every login snapped the player back to north no matter which way they walked out. The character blob is
    /// System.Text.Json over public FIELDS, and a field that isn't on <see cref="Character"/> serialises to
    /// nothing at all silently, which is exactly how that stayed invisible.</para></summary>
    [Fact]
    public void Dir_SurvivesASaveAndLoad()
    {
        var c = Make(_a, 0);
        c.Dir = 3;                                   // 0=N 1=E 2=S 3=W — anything but the default
        Assert.True(_store.Save(c));

        Assert.Equal(3, _store.Load(_a)!.Dir);
    }

    private static int ParcelCount(string recipient)
    {
        using var cn = Db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM parcels WHERE recipient=$r COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$r", recipient);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
