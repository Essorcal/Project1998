using Microsoft.Data.Sqlite;
using Shared;

namespace Server;

/// <summary>
/// A single post on a bulletin board — RTK's <c>Boards</c> SQL table (columns confirmed by reading the
/// char-server handler, <c>rtk/src/char/mapif.c</c>'s <c>mapif_parse_showposts</c>: <c>BrdBnmId</c>,
/// <c>BrdPosition</c>, <c>BrdChaName</c>, <c>BrdTopic</c>, <c>BrdMonth</c>, <c>BrdDay</c>, plus a
/// <c>BrdHighlighted</c> color flag we don't use). RTK splits this across a separate char-server process
/// with its own SQL database; here posts live in the shared SQLite DB (<see cref="Shared.Db"/>) — the same
/// store the two processes use for characters — so the board survives a restart and both processes agree.
/// </summary>
public sealed class BoardPost
{
    public int    Id;        // BrdPosition — 1-based, unique within its own board (not globally)
    public int    BoardId;   // BrdBnmId
    public string Author = "";
    public string Topic  = "";
    public string Body   = "";
    public byte   Month;
    public byte   Day;
}

/// <summary>A named board (RTK <c>board_db</c>: id + display name). RTK's own board list
/// (<c>db/board_db.txt</c>) is server-instance configuration the reference tree doesn't ship — there's no
/// real seed data to port, so the roster below is this server's own choice of boards rather than anything
/// recovered from RTK. Order here IS the order the client draws in the <c>b</c> list; the personal
/// <b>Mailbox</b> (board 0) is appended after them by <c>Session.SendBoardList</c> and is not listed here.
/// Ids are the wire <c>BrdBnmId</c> and are what <c>board_posts.board_id</c> and
/// <c>game-data/BoardLocations.csv</c> reference — renumbering an existing board re-homes its posts
/// and any board-sign pointing at it, so add new boards with new ids rather than reshuffling.
/// Every board is open to read + post by any player, matching RTK's own default for a board with no gating
/// <c>check</c> script (RTK's gated boards need GM level / tutor rank / clans, none of which are modelled).</summary>
public sealed record BoardDef(int Id, string Name);

public static class Boards
{
    public static readonly IReadOnlyList<BoardDef> All = new[]
    {
        new BoardDef(1, "Community"),
        new BoardDef(2, "Market (Buy)"),
        new BoardDef(3, "Market (Sell)"),
        new BoardDef(4, "Hunting"),
        new BoardDef(5, "Community Events"),
        new BoardDef(6, "Law"),
        new BoardDef(7, "Guide"),
        new BoardDef(8, "Poetry"),
        new BoardDef(9, "Bugs"),
    };

    public static BoardDef? Find(int boardId) => All.FirstOrDefault(b => b.Id == boardId);

    private static BoardPost Read(SqliteDataReader r) => new()
    {
        Id      = r.GetInt32(0),
        BoardId = r.GetInt32(1),
        Author  = r.IsDBNull(2) ? "" : r.GetString(2),
        Topic   = r.IsDBNull(3) ? "" : r.GetString(3),
        Body    = r.IsDBNull(4) ? "" : r.GetString(4),
        Month   = r.IsDBNull(5) ? (byte)0 : (byte)r.GetInt32(5),
        Day     = r.IsDBNull(6) ? (byte)0 : (byte)r.GetInt32(6),
    };

    private const string Cols = "position, board_id, author, topic, body, month, day";

    /// <summary>All posts on a board, newest first (RTK: <c>ORDER BY BrdPosition DESC</c>).</summary>
    public static List<BoardPost> PostsFor(int boardId)
    {
        var list = new List<BoardPost>();
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = $"SELECT {Cols} FROM board_posts WHERE board_id=$b ORDER BY position DESC;";
            cmd.Parameters.AddWithValue("$b", boardId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(Read(r));
        }
        catch { /* best effort: an empty board is better than a crash */ }
        return list;
    }

    public static BoardPost? Get(int boardId, int postId)
    {
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = $"SELECT {Cols} FROM board_posts WHERE board_id=$b AND position=$p LIMIT 1;";
            cmd.Parameters.AddWithValue("$b", boardId);
            cmd.Parameters.AddWithValue("$p", postId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? Read(r) : null;
        }
        catch { return null; }
    }

    public static BoardPost Post(int boardId, string author, string topic, string body, byte month, byte day)
    {
        // Assign the next per-board position and insert in one transaction so two simultaneous posts can't
        // collide on the same BrdPosition.
        using var cn = Db.Open();
        using var tx = cn.BeginTransaction();

        int nextId;
        using (var q = cn.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT COALESCE(MAX(position),0)+1 FROM board_posts WHERE board_id=$b;";
            q.Parameters.AddWithValue("$b", boardId);
            nextId = Convert.ToInt32(q.ExecuteScalar());
        }
        using (var ins = cn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = @"INSERT INTO board_posts(board_id, position, author, topic, body, month, day)
                                VALUES($b, $p, $author, $topic, $body, $month, $day);";
            ins.Parameters.AddWithValue("$b", boardId);
            ins.Parameters.AddWithValue("$p", nextId);
            ins.Parameters.AddWithValue("$author", author);
            ins.Parameters.AddWithValue("$topic", topic);
            ins.Parameters.AddWithValue("$body", body);
            ins.Parameters.AddWithValue("$month", month);
            ins.Parameters.AddWithValue("$day", day);
            ins.ExecuteNonQuery();
        }
        tx.Commit();

        return new BoardPost { Id = nextId, BoardId = boardId, Author = author, Topic = topic, Body = body, Month = month, Day = day };
    }

    /// <summary>RTK only lets a post's own author delete it (no per-board CAN_DEL grant modelled — that's
    /// a tutor/GM concept this server doesn't have). False if the post doesn't exist or isn't theirs.</summary>
    public static bool Delete(int boardId, int postId, string requester)
    {
        try
        {
            using var cn = Db.Open();
            using var cmd = cn.CreateCommand();
            // Delete only when the requester is the author (case-insensitive), in a single atomic statement.
            cmd.CommandText = @"DELETE FROM board_posts
                                WHERE board_id=$b AND position=$p AND author=$a COLLATE NOCASE;";
            cmd.Parameters.AddWithValue("$b", boardId);
            cmd.Parameters.AddWithValue("$p", postId);
            cmd.Parameters.AddWithValue("$a", requester);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch { return false; }
    }

    // One-time import of a legacy state/boards.json (if present) into the DB. Idempotent: skips when the
    // board_posts table already has rows. Called once at game-server startup.
    public static void MigrateFromJsonIfNeeded()
    {
        try
        {
            using var cn = Db.Open();
            using (var count = cn.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM board_posts;";
                if (Convert.ToInt32(count.ExecuteScalar()) > 0) return;   // already have posts -> done
            }

            var path = RepoPaths.State("boards.json");
            if (!File.Exists(path)) return;

            List<BoardPost>? posts;
            try
            {
                posts = System.Text.Json.JsonSerializer.Deserialize<List<BoardPost>>(
                    File.ReadAllText(path),
                    new System.Text.Json.JsonSerializerOptions { IncludeFields = true });
            }
            catch { return; }
            if (posts is null || posts.Count == 0) return;

            using var tx = cn.BeginTransaction();
            foreach (var p in posts)
            {
                using var ins = cn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = @"INSERT INTO board_posts(board_id, position, author, topic, body, month, day)
                                    VALUES($b, $p, $author, $topic, $body, $month, $day);";
                ins.Parameters.AddWithValue("$b", p.BoardId);
                ins.Parameters.AddWithValue("$p", p.Id);
                ins.Parameters.AddWithValue("$author", p.Author ?? "");
                ins.Parameters.AddWithValue("$topic", p.Topic ?? "");
                ins.Parameters.AddWithValue("$body", p.Body ?? "");
                ins.Parameters.AddWithValue("$month", p.Month);
                ins.Parameters.AddWithValue("$day", p.Day);
                ins.ExecuteNonQuery();
            }
            tx.Commit();
            Log.Info($"[db] migrated {posts.Count} board post(s) from {path} into {Db.Path} (JSON kept as backup)");
        }
        catch { /* best effort */ }
    }
}
