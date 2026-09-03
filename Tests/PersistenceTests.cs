using Microsoft.Data.Sqlite;
using Protocol.Tk495;
using Server;
using Shared;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tests.Support;
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

    private Character LoadOk(string name)
    {
        var result = _store.Load(name);
        Assert.Equal(CharacterLoadStatus.Ok, result.Status);
        return Assert.IsType<Character>(result.Character);
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

    [Fact]
    public void MissingRow_IsNotReportedAsUnreadable()
    {
        var load = _store.Load(_a);

        Assert.Equal(CharacterLoadStatus.NotFound, load.Status);
        Assert.Null(load.Character);
        Assert.Null(load.Reason);
    }

    [Fact]
    public void ProductionDatabaseIsUnchanged()
    {
        Assert.Equal(TestProcessState.ProductionDatabaseExisted,
                     File.Exists(TestProcessState.ProductionDatabasePath));
        if (TestProcessState.ProductionDatabaseExisted)
            Assert.Equal(TestProcessState.ProductionDatabaseLastWriteTimeUtc,
                         File.GetLastWriteTimeUtc(TestProcessState.ProductionDatabasePath));
    }

    /// <summary>The trade guarantee: both sides land together. This is the happy path — the failure path is
    /// the next test, and it's the one that actually matters.</summary>
    [Fact]
    public void SaveMany_PersistsEveryCharacter()
    {
        var a = Make(_a, 100, (1, 5));
        var b = Make(_b, 200);

        Assert.True(_store.SaveMany(new[] { a, b }));

        Assert.Equal(100u, LoadOk(_a).Coins);
        Assert.Equal(200u, LoadOk(_b).Coins);
        Assert.Equal(5, LoadOk(_a).Inventory.Single().Amount);
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
        var reloadedA = LoadOk(_a);
        var reloadedB = LoadOk(_b);
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
        Assert.Equal(100u, LoadOk(_a).Coins);   // the character save rolled back with it
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
        Assert.Equal(400u, LoadOk(_a).Coins);
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

        Assert.Equal(3, LoadOk(_a).Dir);
    }

    /// <summary>
    /// The checked-in blob is the rename alarm. It contains every current <see cref="Character"/> field and
    /// populated instances of every nested persisted type. Renaming a field without first consuming its old
    /// name in <see cref="CharacterUpgrader"/> therefore leaves an unmapped fixture member and fails here;
    /// do not weaken or delete this test when adding an upgrade step.
    /// </summary>
    [Fact]
    public void CurrentFixture_RoundTripsThroughTheUpgraderWithNoUnmappedMembers()
    {
        string json = File.ReadAllText(FixturePath());

        var character = CharacterStore.Deserialize(json);

        Assert.Equal(Character.CurrentSchemaVersion, character.SchemaVersion);
        Assert.Equal("FixtureHero", character.Name);
        Assert.Equal(123456u, character.Coins);
        Assert.Equal("Keepsake", character.Inventory.Single().CustomName);
        Assert.Equal("fixture-buff", character.Effects.Buffs.Single().Key);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(json),
            JsonNode.Parse(CharacterStore.Serialize(character))));
    }

    [Fact]
    public void SchemaZeroBlob_IsStampedBeforeStrictDeserialization()
    {
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(FixturePath())));
        Assert.True(root.Remove(nameof(Character.SchemaVersion)));

        var character = CharacterStore.Deserialize(root.ToJsonString());

        Assert.Equal(Character.CurrentSchemaVersion, character.SchemaVersion);
        Assert.Equal("FixtureHero", character.Name);
    }

    [Fact]
    public void LegacyJsonImport_UsesTheRawJsonUpgrader()
    {
        string legacyDir = Path.Combine(_fixture.StateDirectory, $"legacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(legacyDir);
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(FixturePath())));
        Assert.True(root.Remove(nameof(Character.SchemaVersion)));
        root[nameof(Character.Name)] = _a;
        File.WriteAllText(Path.Combine(legacyDir, $"{_a}.json"), root.ToJsonString());

        var importingStore = new CharacterStore(legacyDir);
        var load = importingStore.Load(_a);

        Assert.Equal(CharacterLoadStatus.Ok, load.Status);
        Assert.Equal(Character.CurrentSchemaVersion, Assert.IsType<Character>(load.Character).SchemaVersion);
        Assert.Equal(Character.CurrentSchemaVersion,
            Assert.IsType<JsonObject>(JsonNode.Parse(ReadRawCharacter(_a)))[nameof(Character.SchemaVersion)]!.GetValue<int>());
    }

    [Fact]
    public void UnknownPersistedMember_IsRejectedInsteadOfSilentlyDropped()
    {
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(FixturePath())));
        root["FieldRenamedWithoutUpgrade"] = 123;

        Assert.Throws<JsonException>(() => CharacterStore.Deserialize(root.ToJsonString()));
    }

    /// <summary>An unreadable row is evidence to preserve, not an absent character to recreate. The arrival
    /// path gives the player a distinct message and closes before entering the world; every store write path
    /// independently refuses to replace the row, covering a stale duplicate session that flushes first.</summary>
    [Fact]
    public void UnreadableRow_IsRejectedDistinctlyAndNeverOverwritten()
    {
        const string corrupt = "{\"SchemaVersion\":1,\"Name\":\"FixtureHero\",\"RemovedField\":17}";
        InsertRawCharacter(_a, corrupt);

        var load = _store.Load(_a);
        Assert.Equal(CharacterLoadStatus.Unreadable, load.Status);
        Assert.Null(load.Character);
        Assert.False(string.IsNullOrWhiteSpace(load.Reason));

        Assert.False(_store.Save(Make(_a, 999)));
        Assert.Equal(corrupt, ReadRawCharacter(_a));
        Assert.False(_store.SaveMany(new[] { Make(_a, 888) }));
        bool callbackRan = false;
        Assert.False(_store.SaveWith(Make(_a, 777), (_, _) => callbackRan = true));
        Assert.False(callbackRan);
        Assert.Equal(corrupt, ReadRawCharacter(_a));

        var outbound = new RecordingOutbound();
        var session = new Session(outbound, 2005, _store, new World());
        session.Receive(ArrivalFrame(_a));

        Assert.True(outbound.Closed);
        var message = Assert.Single(outbound.BodiesOf(0x02));
        Assert.Equal("Your character record could not be loaded. Please contact an administrator.",
            Encoding.ASCII.GetString(message, 2, message[1]));
        Assert.Equal(corrupt, ReadRawCharacter(_a));
    }

    [Fact]
    public void DatabaseMigrations_AreVersionedAndIdempotent()
    {
        string path = Path.Combine(_fixture.StateDirectory, $"migration-{Guid.NewGuid():N}.db");
        using (var legacy = new SqliteConnection($"Data Source={path}"))
        {
            legacy.Open();
            using var schema = legacy.CreateCommand();
            schema.CommandText = @"
CREATE TABLE handoff_tokens (
  nonce_hash TEXT PRIMARY KEY,
  username TEXT NOT NULL,
  expires_utc INTEGER NOT NULL,
  consumed INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE parcels (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  recipient TEXT NOT NULL COLLATE NOCASE,
  position INTEGER NOT NULL,
  sender TEXT,
  item_id INTEGER NOT NULL DEFAULT -1,
  item_amount INTEGER NOT NULL DEFAULT 0,
  item_dura INTEGER NOT NULL DEFAULT 0,
  engrave TEXT,
  month INTEGER,
  day INTEGER
);";
            schema.ExecuteNonQuery();
        }

        Db.InitializeDatabase(path);
        Db.InitializeDatabase(path);

        using var cn = new SqliteConnection($"Data Source={path}");
        cn.Open();
        using var version = cn.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(Db.CurrentSchemaVersion, Convert.ToInt32(version.ExecuteScalar()));
        Assert.Equal(1, ColumnCount(cn, "handoff_tokens", "ip"));
        Assert.Equal(1, ColumnCount(cn, "parcels", "item_owner"));
    }

    private static string FixturePath() =>
        Path.Combine(RepoPaths.Root(), "Tests", "Fixtures", "character-v1.json");

    private static byte[] ArrivalFrame(string user)
    {
        var body = new List<byte> { 9 };
        body.AddRange(Encoding.ASCII.GetBytes("NexonInc."));
        body.Add((byte)user.Length);
        body.AddRange(Encoding.ASCII.GetBytes(user));
        body.AddRange(HandoffTokens.Mint(user, System.Net.IPAddress.None.ToString()));
        return TkPacket.Build(Opcode.Arrival, 0, body.ToArray());
    }

    private static void InsertRawCharacter(string user, string json)
    {
        using var cn = Db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO characters(username, json, updated_utc) VALUES($u, $j, 1);";
        cmd.Parameters.AddWithValue("$u", CharacterStore.Key(user));
        cmd.Parameters.AddWithValue("$j", json);
        cmd.ExecuteNonQuery();
    }

    private static string ReadRawCharacter(string user)
    {
        using var cn = Db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT json FROM characters WHERE username=$u;";
        cmd.Parameters.AddWithValue("$u", CharacterStore.Key(user));
        return Assert.IsType<string>(cmd.ExecuteScalar());
    }

    private static int ColumnCount(SqliteConnection cn, string table, string column)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$column;";
        cmd.Parameters.AddWithValue("$column", column);
        return Convert.ToInt32(cmd.ExecuteScalar());
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
