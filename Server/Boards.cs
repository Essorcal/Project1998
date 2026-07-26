using System.Text.Json;

namespace Server;

/// <summary>
/// A single post on a bulletin board — RTK's <c>Boards</c> SQL table (columns confirmed by reading the
/// char-server handler, <c>rtk/src/char/mapif.c</c>'s <c>mapif_parse_showposts</c>: <c>BrdBnmId</c>,
/// <c>BrdPosition</c>, <c>BrdChaName</c>, <c>BrdTopic</c>, <c>BrdMonth</c>, <c>BrdDay</c>, plus a
/// <c>BrdHighlighted</c> color flag we don't use). RTK splits this across a separate char-server process
/// with its own SQL database; this server is single-process, so posts collapse into one server-wide JSON
/// file instead — the shape is RTK's, the storage mechanism is ours (same choice already made for
/// characters, see <see cref="Shared.CharacterStore"/>).
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
/// real seed data to port. This list instead reuses REAL RTK board identifiers straight from RTK's own
/// board scripts (<c>rtklua/Developers/Boards/*.lua</c>: lore/map/poetry/minigamescarnages), not invented
/// names, picking the ones that don't depend on concepts this server has no model for yet (GM level, tutor
/// rank, clans — <c>pathBoards.lua</c>'s per-class boards gate posting on "tutor" status, subpath boards
/// need a clan/subpath system). Every board here is open to read + post by any player, matching RTK's own
/// default for a board with no gating <c>check</c> script.</summary>
public sealed record BoardDef(int Id, string Name);

public static class Boards
{
    public static readonly IReadOnlyList<BoardDef> All = new[]
    {
        new BoardDef(1, "Lore"),
        new BoardDef(2, "Map"),
        new BoardDef(3, "Poetry"),
        new BoardDef(4, "Minigames & Carnages"),
    };

    public static BoardDef? Find(int boardId) => All.FirstOrDefault(b => b.Id == boardId);

    private static readonly object Lock = new();
    private static List<BoardPost> _posts = new();
    private static string _path = "";
    private static bool _loaded;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Lock)
        {
            if (_loaded) return;
            _path = ResolvePath();
            try
            {
                if (File.Exists(_path))
                    _posts = JsonSerializer.Deserialize<List<BoardPost>>(File.ReadAllText(_path), Json) ?? new();
            }
            catch { _posts = new(); }   // corrupt file -> start clean rather than crash the server
            _loaded = true;
        }
    }

    // Mirrors CharacterStore's repo-root anchoring (Net.cs's RepoDataDir) so this lands in the same data/
    // folder regardless of the process's working directory.
    private static string ResolvePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            bool isRoot = dir.GetFiles("*.sln").Length > 0
                       || (Directory.Exists(Path.Combine(dir.FullName, "Server")) && Directory.Exists(Path.Combine(dir.FullName, "Shared")));
            if (isRoot)
            {
                var d = Path.Combine(dir.FullName, "data");
                Directory.CreateDirectory(d);
                return Path.Combine(d, "boards.json");
            }
            dir = dir.Parent;
        }
        var fallback = Path.Combine(Directory.GetCurrentDirectory(), "data");
        Directory.CreateDirectory(fallback);
        return Path.Combine(fallback, "boards.json");
    }

    private static void SaveLocked()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_posts, Json)); }
        catch { /* best effort; persistence must never crash a session */ }
    }

    /// <summary>All posts on a board, newest first (RTK: <c>ORDER BY BrdPosition DESC</c>).</summary>
    public static List<BoardPost> PostsFor(int boardId)
    {
        EnsureLoaded();
        lock (Lock) return _posts.Where(p => p.BoardId == boardId).OrderByDescending(p => p.Id).ToList();
    }

    public static BoardPost? Get(int boardId, int postId)
    {
        EnsureLoaded();
        lock (Lock) return _posts.FirstOrDefault(p => p.BoardId == boardId && p.Id == postId);
    }

    public static BoardPost Post(int boardId, string author, string topic, string body, byte month, byte day)
    {
        EnsureLoaded();
        lock (Lock)
        {
            int nextId = _posts.Where(p => p.BoardId == boardId).Select(p => p.Id).DefaultIfEmpty(0).Max() + 1;
            var post = new BoardPost { Id = nextId, BoardId = boardId, Author = author, Topic = topic, Body = body, Month = month, Day = day };
            _posts.Add(post);
            SaveLocked();
            return post;
        }
    }

    /// <summary>RTK only lets a post's own author delete it (no per-board CAN_DEL grant modelled — that's
    /// a tutor/GM concept this server doesn't have). False if the post doesn't exist or isn't theirs.</summary>
    public static bool Delete(int boardId, int postId, string requester)
    {
        EnsureLoaded();
        lock (Lock)
        {
            var post = _posts.FirstOrDefault(p => p.BoardId == boardId && p.Id == postId);
            if (post is null || !string.Equals(post.Author, requester, StringComparison.OrdinalIgnoreCase)) return false;
            _posts.Remove(post);
            SaveLocked();
            return true;
        }
    }
}
