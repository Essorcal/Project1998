using Microsoft.Data.Sqlite;
using Shared;

namespace Server;

/// <summary>
/// A single piece of mail (RTK nmail — clif.c's board sub-opcodes 6/9 route through the SAME
/// boards_showposts/boards_readpost machinery as a normal bulletin board, just addressed at board id 0,
/// which the char-server treats as "your own mailbox" rather than a shared board — see HandleBoard's case 9
/// doc). The real nmail_write/boards_post/boards_readpost implementations aren't present anywhere in this
/// reference tree (checked rtk/src/map/board_db.c and clif.c both — only the dispatcher survives), so unlike
/// Boards.cs there's no wire evidence at all for how mail gets COMPOSED (no recipient field visible in the
/// dispatcher, no separate compose packet). Compose is therefore our own design, driven from the NATIVE
/// board window (HandleBoardWrite): the "@mail" chat command that used to front it is gone. Reading a mailbox
/// reuses the SAME wire builders as a normal board (SendBoardPosts/SendBoardReadPost) since that reply shape
/// is at least cross-referenced against the char-server hop, just sourced from this table instead of
/// board_posts when boardId==0.
///
/// item_id &lt; 0 = a plain text letter, no parcel attached (RTK's own reward mail always carries one via
/// sendRewardParcel — the "parcel" half of "mail + parcel system").
/// </summary>
public sealed class MailPost
{
    public int    Id;          // internal rowid (not wire-visible)
    public int    Position;    // 1-based within the RECIPIENT's own mailbox — this IS the wire postId
    public string Recipient = "";
    public string Sender    = "";
    public string Topic     = "";
    public string Body      = "";
    public byte   Month;
    public byte   Day;
    public int    ItemId    = -1;
    public int    ItemAmount;
    public int    ItemDura;
    public bool   Claimed;
    public bool   IsRead;
}

public static class Mail
{
    private const string Cols =
        "id, recipient, position, sender, topic, body, month, day, item_id, item_amount, item_dura, claimed, is_read";

    private static MailPost Read(SqliteDataReader r) => new()
    {
        Id         = r.GetInt32(0),
        Recipient  = r.IsDBNull(1) ? "" : r.GetString(1),
        Position   = r.GetInt32(2),
        Sender     = r.IsDBNull(3) ? "" : r.GetString(3),
        Topic      = r.IsDBNull(4) ? "" : r.GetString(4),
        Body       = r.IsDBNull(5) ? "" : r.GetString(5),
        Month      = r.IsDBNull(6) ? (byte)0 : (byte)r.GetInt32(6),
        Day        = r.IsDBNull(7) ? (byte)0 : (byte)r.GetInt32(7),
        ItemId     = r.GetInt32(8),
        ItemAmount = r.GetInt32(9),
        ItemDura   = r.GetInt32(10),
        Claimed    = r.GetInt32(11) != 0,
        IsRead     = r.GetInt32(12) != 0,
    };

    /// <summary>Everything in a player's mailbox, newest first — same ordering as Boards.PostsFor.</summary>
    public static List<MailPost> InboxFor(string recipient)
    {
        var list = new List<MailPost>();
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = $"SELECT {Cols} FROM mail_posts WHERE recipient=$r COLLATE NOCASE ORDER BY position DESC;";
            cmd.Parameters.AddWithValue("$r", recipient);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(Read(r));
        }
        catch (Exception e) { Log.Error($"mail_posts read failed for '{recipient}' — showing the inbox empty", e); }
        return list;
    }

    public static int UnreadCount(string recipient) => InboxFor(recipient).Count(m => !m.IsRead);

    public static MailPost? Get(string recipient, int position)
    {
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = $"SELECT {Cols} FROM mail_posts WHERE recipient=$r COLLATE NOCASE AND position=$p LIMIT 1;";
            cmd.Parameters.AddWithValue("$r", recipient);
            cmd.Parameters.AddWithValue("$p", position);
            using var r = cmd.ExecuteReader();
            return r.Read() ? Read(r) : null;
        }
        catch (Exception e) { Log.Error($"mail_posts read failed for '{recipient}' position {position}", e); return null; }
    }

    /// <summary>Send a letter (optionally with one attached item stack — the "parcel" half). Assigns the
    /// next position within the RECIPIENT's own mailbox, same collision-safe pattern as Boards.Post.</summary>
    public static MailPost Send(string recipient, string sender, string topic, string body, byte month, byte day,
        int itemId = -1, int itemAmount = 0, int itemDura = 0)
    {
        using var cn = Db.Open();
        using var tx = cn.BeginTransaction();

        int nextPos;
        using (var q = cn.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT COALESCE(MAX(position),0)+1 FROM mail_posts WHERE recipient=$r COLLATE NOCASE;";
            q.Parameters.AddWithValue("$r", recipient);
            nextPos = Convert.ToInt32(q.ExecuteScalar());
        }
        using (var ins = cn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = @"INSERT INTO mail_posts(recipient, position, sender, topic, body, month, day, item_id, item_amount, item_dura)
                                VALUES($rec, $pos, $sender, $topic, $body, $month, $day, $iid, $iamt, $idura);";
            ins.Parameters.AddWithValue("$rec", recipient);
            ins.Parameters.AddWithValue("$pos", nextPos);
            ins.Parameters.AddWithValue("$sender", sender);
            ins.Parameters.AddWithValue("$topic", topic);
            ins.Parameters.AddWithValue("$body", body);
            ins.Parameters.AddWithValue("$month", month);
            ins.Parameters.AddWithValue("$day", day);
            ins.Parameters.AddWithValue("$iid", itemId);
            ins.Parameters.AddWithValue("$iamt", itemAmount);
            ins.Parameters.AddWithValue("$idura", itemDura);
            ins.ExecuteNonQuery();
        }
        tx.Commit();

        return new MailPost { Position = nextPos, Recipient = recipient, Sender = sender, Topic = topic, Body = body,
            Month = month, Day = day, ItemId = itemId, ItemAmount = itemAmount, ItemDura = itemDura };
    }

    public static void MarkRead(string recipient, int position)
    {
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "UPDATE mail_posts SET is_read=1 WHERE recipient=$r COLLATE NOCASE AND position=$p;";
            cmd.Parameters.AddWithValue("$r", recipient);
            cmd.Parameters.AddWithValue("$p", position);
            cmd.ExecuteNonQuery();
        }
        catch (Exception e) { Log.Error($"mail_posts mark-read failed for '{recipient}' position {position}", e); }
    }

    /// <summary>One-shot claim of an attached parcel: returns the item to give and flips claimed so a
    /// second read can't duplicate it. Null if this letter has no attachment or it's already been claimed.</summary>
    /// <summary>Runs inside the caller's transaction, alongside the character save that receives the item —
    /// see <see cref="Parcel.ClaimIn"/> for why the two must commit together. The <c>claimed=0</c> predicate
    /// is still what makes a second read a no-op rather than a duplication.</summary>
    public static (int itemId, int amount, int dura)? ClaimItemIn(
        SqliteConnection cn, SqliteTransaction tx, string recipient, int position)
    {
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"UPDATE mail_posts SET claimed=1
                            WHERE recipient=$r COLLATE NOCASE AND position=$p AND item_id>=0 AND claimed=0
                            RETURNING item_id, item_amount, item_dura;";
        cmd.Parameters.AddWithValue("$r", recipient);
        cmd.Parameters.AddWithValue("$p", position);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    /// <summary>Delete a letter from the RECIPIENT's own mailbox — mail has no "author delete" concept like
    /// a board post (Boards.Delete); only the owner of the mailbox it's sitting in can clear it.</summary>
    public static bool Delete(string recipient, int position)
    {
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "DELETE FROM mail_posts WHERE recipient=$r COLLATE NOCASE AND position=$p;";
            cmd.Parameters.AddWithValue("$r", recipient);
            cmd.Parameters.AddWithValue("$p", position);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (Exception e) { Log.Error($"mail_posts delete failed for '{recipient}' position {position}", e); return false; }
    }
}
