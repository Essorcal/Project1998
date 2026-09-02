using Microsoft.Data.Sqlite;
using Shared;

namespace Server;

/// <summary>
/// A parcel waiting for a player at the messenger (RTK Parcels row). A parcel is an item stack or a lump of
/// gold that one player sent to another via a <c>MessengerNpc</c> (RTK messenger.lua "Send Parcel" →
/// Parcel.lua <c>sendParcelTo</c>), collected later via "Receive Parcel" (<c>receiveParcelFrom</c>). This is
/// deliberately SEPARATE from <see cref="Mail"/>: RTK stores parcels in their own table, gold parcels have no
/// letter to attach to, and the bottom-left HUD bag icon (0x08 body[45] bit 0x01) means "a parcel waits at
/// the messenger", distinct from the n-mail arrow.
///
/// <para><see cref="ItemId"/> &lt; 0 marks a GOLD parcel — <see cref="Amount"/> is then the coin amount.
/// Otherwise it's an item content id and <see cref="Amount"/> is the stack count.</para>
/// </summary>
public sealed class ParcelItem
{
    public int    Id;          // internal rowid
    public int    Position;    // 1-based within the RECIPIENT's own queue (FIFO claim order)
    public string Recipient = "";
    public string Sender    = "";
    public int    ItemId    = -1;   // <0 = gold parcel
    public int    Amount;           // item count, or gold amount when ItemId<0
    public int    Dura;
    public string Engrave   = "";   // RTK ParEngrave — the item's custom/real name, preserved across the send
    public string Owner     = "";   // bound owner (InvItem.Owner) carried across the send so a bonded item stays bound
    public byte   Month;
    public byte   Day;

    public bool IsGold => ItemId < 0;
}

/// <summary>The parcels store — same "RTK's shape, our SQLite storage, name-addressed" pattern as
/// <see cref="Mail"/> and <see cref="Boards"/>.</summary>
public static class Parcel
{
    private const string Cols = "id, position, recipient, sender, item_id, item_amount, item_dura, engrave, month, day, item_owner";

    private static ParcelItem Read(SqliteDataReader r) => new()
    {
        Id        = r.GetInt32(0),
        Position  = r.GetInt32(1),
        Recipient = r.IsDBNull(2) ? "" : r.GetString(2),
        Sender    = r.IsDBNull(3) ? "" : r.GetString(3),
        ItemId    = r.GetInt32(4),
        Amount    = r.GetInt32(5),
        Dura      = r.GetInt32(6),
        Engrave   = r.IsDBNull(7) ? "" : r.GetString(7),
        Month     = r.IsDBNull(8) ? (byte)0 : (byte)r.GetInt32(8),
        Day       = r.IsDBNull(9) ? (byte)0 : (byte)r.GetInt32(9),
        Owner     = r.IsDBNull(10) ? "" : r.GetString(10),
    };

    /// <summary>Every parcel waiting for a player, oldest first (FIFO — RTK claims the lowest position).</summary>
    public static List<ParcelItem> ListFor(string recipient)
    {
        var list = new List<ParcelItem>();
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = $"SELECT {Cols} FROM parcels WHERE recipient=$r COLLATE NOCASE ORDER BY position ASC;";
            cmd.Parameters.AddWithValue("$r", recipient);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(Read(r));
        }
        catch (Exception e) { Log.Error($"parcels read failed for '{recipient}' — showing the queue empty", e); }
        return list;
    }

    public static int CountFor(string recipient)
    {
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM parcels WHERE recipient=$r COLLATE NOCASE;";
            cmd.Parameters.AddWithValue("$r", recipient);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch (Exception e) { Log.Error($"parcels count failed for '{recipient}' — reporting 0", e); return 0; }
    }

    public static bool HasAny(string recipient) => CountFor(recipient) > 0;

    /// <summary>Queue a parcel for a recipient, assigning the next position in their own queue (same
    /// collision-safe MAX(position)+1 in a transaction as Mail.Send/Boards.Post).</summary>
    public static ParcelItem Send(string recipient, string sender, int itemId, int amount, int dura,
        string engrave, byte month, byte day, string owner = "")
    {
        using var cn = Db.Open();
        using var tx = cn.BeginTransaction();

        int nextPos;
        using (var q = cn.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT COALESCE(MAX(position),0)+1 FROM parcels WHERE recipient=$r COLLATE NOCASE;";
            q.Parameters.AddWithValue("$r", recipient);
            nextPos = Convert.ToInt32(q.ExecuteScalar());
        }
        using (var ins = cn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = @"INSERT INTO parcels(recipient, position, sender, item_id, item_amount, item_dura, engrave, month, day, item_owner)
                                VALUES($rec, $pos, $sender, $iid, $amt, $dura, $eng, $month, $day, $owner);";
            ins.Parameters.AddWithValue("$rec", recipient);
            ins.Parameters.AddWithValue("$pos", nextPos);
            ins.Parameters.AddWithValue("$sender", sender);
            ins.Parameters.AddWithValue("$iid", itemId);
            ins.Parameters.AddWithValue("$amt", amount);
            ins.Parameters.AddWithValue("$dura", dura);
            ins.Parameters.AddWithValue("$eng", engrave ?? "");
            ins.Parameters.AddWithValue("$month", month);
            ins.Parameters.AddWithValue("$day", day);
            ins.Parameters.AddWithValue("$owner", owner ?? "");
            ins.ExecuteNonQuery();
        }
        tx.Commit();

        return new ParcelItem { Position = nextPos, Recipient = recipient, Sender = sender, ItemId = itemId,
            Amount = amount, Dura = dura, Engrave = engrave ?? "", Month = month, Day = day, Owner = owner ?? "" };
    }

    /// <summary>
    /// Remove one parcel by position and return it — the claim step. Null if it was already gone, which is
    /// what guards the double-claim race (the conditional DELETE either matches a row or it doesn't).
    ///
    /// <para><b>Takes the caller's transaction on purpose.</b> The parcel leaving the queue and the item
    /// arriving in the player's bag have to commit together: claiming on its own connection meant a crash
    /// between the delete and the character's next autosave destroyed the parcel outright — gone from the
    /// queue, never delivered. The caller runs this inside <see cref="CharacterStore.SaveWith"/> so both
    /// halves land or neither does.</para>
    /// </summary>
    public static ParcelItem? ClaimIn(SqliteConnection cn, SqliteTransaction tx, string recipient, int position)
    {
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM parcels WHERE recipient=$r COLLATE NOCASE AND position=$p RETURNING {Cols};";
        cmd.Parameters.AddWithValue("$r", recipient);
        cmd.Parameters.AddWithValue("$p", position);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }
}
