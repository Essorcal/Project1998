namespace Server;

public static partial class Content
{

    // ---- Location / warp geometry (Tier-1 extraction; game-data/*.csv) ------------------------------
    // RTK/RE geometry that used to be hard-coded in the game logic, moved to flat files so it hot-reloads via
    // @reload like every other registry. Consumers read these Content.* properties.

    // Which of the two soundtracks a track belongs to. They are separate id SPACES, not one list — mp3 ids
    // 2/3/4 in the 5.x set collide with midi ids 2/3/4 in the old one — so every lookup takes a set.
    //   Old = the 12 stock midis (1.mid..12.mid). In NexusTK.snd on 4.95 and Snd.dat on 5.33, so BOTH
    //         clients can play them, and they stay the default.
    //   New = the 25 mp3s + 52 playlists in the 5.33 client's Mus000.dat. 5.33 ONLY: the 4.95 client has
    //         an mp3 engine (see Session.SendMusic) but ships none of the files, so offering it there
    //         would be silence. See docs/5.x/Wire-Divergences.md §"0x19 music".
    public enum MusicSet { Old, New }

    // The client's background tracks, by id and by NAME (the files are numbered, but the songs have real
    // names — see MusicTracks.csv, which is also what lets "@music mist" work). Type is the 0x19 channel:
    // 2 = midi, 1 = mp3. Playlist is true for the 5.x .LST/.LSR entries, where the id names a list of ten
    // tracks the client cycles by itself rather than one song.
    //
    // Shuffle separates the two kinds of playlist, and map music MUST NOT use a shuffled one. Both cycle
    // fine, but the 5.33 advance (0x4a7b40, on WM_USER+8) picks the next entry as `rand() % count + 1` for
    // an .LSR, and the play function (0x4a5f80 @0x4a6078) early-outs to a NO-OP when the index it is handed
    // equals the one already playing. On that 1-in-10 collision nothing is opened, and because the previous
    // stream has already ended there is no further end-of-stream callback — the music is dead until the
    // server sends another 0x19. An .LST advances `cur + 1` (wrapping 10 -> 1), which can never collide.
    // Measured live 2026-08-22: 2 stalls in 40 shuffled advances, 0 in 24 ordered ones.
    public sealed record MusicTrack(ushort Id, string Name, byte Type, MusicSet Set, bool Playlist,
                                    bool Shuffle = false);
    public static IReadOnlyList<MusicTrack> MusicTracks { get; private set; } = new List<MusicTrack>();

    // Area -> BGM track (BgmFor). A design assignment, not RTK data: RTK's own Maps table has one track
    // (902) on 9799 of 9850 maps, and the 4.95 client files carry no map->track table at all. Zones match by
    // explicit map id/range first, then by map-NAME glob; a map in no zone keeps whatever is already playing
    // (see Session.PlayMapMusic) so walking into a shop or a cave never restarts the song. See MapBgm.csv.
    // Track/Type is the Old (midi) pick, Track5x/Type5x the New (5.x mp3-playlist) one; a zone that names no
    // Track5x falls back to its midi, which on 5.33 still plays.
    public sealed record BgmZone(string Zone, ushort Track, byte Type, ushort Track5x, byte Type5x,
        IReadOnlyList<(ushort Lo, ushort Hi)> Maps, IReadOnlyList<string> Names);
    public static IReadOnlyList<BgmZone> BgmZones { get; private set; } = new List<BgmZone>();

    // Resolved map -> track, built once at load (BuildBgmMap): the zones' own maps at Hops 0, then every
    // other map inherits its NEAREST zone through the warp graph. That spill is what makes a building or a
    // cave play its area's theme without being listed, and — unlike leaving it to "whatever is already
    // playing" — it also works when you LOG IN inside one, where there is no previous song to inherit.
    public sealed record BgmPick(ushort Track, byte Type, ushort Track5x, byte Type5x, string Zone, int Hops);
    private static Dictionary<ushort, BgmPick> _bgmByMap = new();

    /// <summary>The track to start on a zone-less map when nothing is playing yet (a fresh session): the
    /// "Default" row of MapBgm.csv. Null leaves such a session silent until it reaches a zoned map.</summary>
    public static (ushort bgm, byte type)? DefaultBgm { get; private set; }

    /// <summary>The <see cref="MusicSet.New"/> half of the "Default" row (its <c>Track5x</c>).</summary>
    public static (ushort bgm, byte type)? DefaultBgmNew { get; private set; }

    /// <summary>The fresh-session fallback for one soundtrack, falling back to the midi when the Default row
    /// names no 5.x track.</summary>
    public static (ushort bgm, byte type)? DefaultBgmFor(MusicSet set) =>
        set == MusicSet.New ? DefaultBgmNew ?? DefaultBgm : DefaultBgm;

    // ---- background music (0x19) --------------------------------------------------------------
    // The stock 4.95 client keeps its audio in NexusTK.snd, which ships exactly 12 background tracks
    // (1.mid .. 12.mid); the 0x19 music packet plays one by id with type 2 = MIDI. There is no original
    // map->track table in the client files, so we assign them ourselves — by AREA, not by map (MapBgm.csv).
    //
    // The 5.33 client keeps those same 12 midis (in Snd.dat) AND a second, larger soundtrack in Mus000.dat:
    // 25 mp3s plus 52 playlists, played over 0x19 type 1. That is the MusicSet.New half of every table here,
    // and it is 5.33-only because 4.95 ships none of those files. Players opt in per character with
    // "@music new" (Session.PlayMusicCmd); the midis stay the default for everyone.

    /// <summary>The background track for a map in one soundtrack: (bgm id, 0x19 type), or null only for a map
    /// that no zone claims AND that has no warp path to one — in which case the caller keeps whatever is
    /// already playing (see Session.PlayMapMusic).</summary>
    public static (ushort bgm, byte type)? BgmFor(ushort mapId, MusicSet set = MusicSet.Old) =>
        _bgmByMap.TryGetValue(mapId, out var p)
            ? (set == MusicSet.New ? (p.Track5x, p.Type5x) : (p.Track, p.Type))
            : null;

    /// <summary>The zone a map's music comes from, for "@music" feedback ("" if none). Maps that inherited
    /// it through the warp graph rather than being listed are shown with their hop distance.</summary>
    public static string BgmZoneOf(ushort mapId) =>
        _bgmByMap.TryGetValue(mapId, out var p) ? (p.Hops == 0 ? p.Zone : $"{p.Zone} +{p.Hops}") : "";

    // Resolve every map to a track, once per Load(). Three passes, each only filling maps still unclaimed:
    //   1. explicit ids/ranges  -> so a single map can be carved out of an area another zone claims by name
    //   2. map-name globs       -> "Buya *" and friends
    //   3. warp-graph spill     -> multi-source BFS from everything claimed above, so each remaining map
    //                             takes its NEAREST claimed map's track (Buya's shops/caves become Tiger
    //                             without being listed; a login inside one starts on the right song)
    private static Dictionary<ushort, BgmPick> BuildBgmMap()
    {
        var byMap = new Dictionary<ushort, BgmPick>();

        foreach (var z in BgmZones)
            foreach (var (lo, hi) in z.Maps)
                for (int id = lo; id <= hi; id++)
                    if ((Maps.ContainsKey((ushort)id) || lo == hi) && !byMap.ContainsKey((ushort)id))
                        byMap[(ushort)id] = new BgmPick(z.Track, z.Type, z.Track5x, z.Type5x, z.Zone, 0);

        foreach (var z in BgmZones)
            foreach (var pat in z.Names)
                foreach (var m in Maps.Values)
                    if (!byMap.ContainsKey(m.Id) && GlobMatch(m.Name, pat))
                        byMap[m.Id] = new BgmPick(z.Track, z.Type, z.Track5x, z.Type5x, z.Zone, 0);

        // Map-level adjacency from the tile warp table, treated as undirected: a one-way drop still tells us
        // the two maps are the same neighbourhood, and most warps are paired anyway.
        var adj = new Dictionary<ushort, List<ushort>>();
        void Link(ushort a, ushort b)
        {
            if (a == b) return;
            if (!adj.TryGetValue(a, out var l)) adj[a] = l = new List<ushort>();
            if (!l.Contains(b)) l.Add(b);
        }
        foreach (var (from, to) in Warps)
        {
            if (!Maps.ContainsKey(from.m) || !Maps.ContainsKey(to.m)) continue;
            Link(from.m, to.m);
            Link(to.m, from.m);
        }

        var queue = new Queue<ushort>(byMap.Keys.Where(Maps.ContainsKey).OrderBy(id => id));
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!adj.TryGetValue(cur, out var neighbours)) continue;
            var here = byMap[cur];   // NB: not `from` — that's a LINQ query keyword and breaks `with`
            foreach (var n in neighbours)
            {
                if (byMap.ContainsKey(n)) continue;
                byMap[n] = here with { Hops = here.Hops + 1 };
                queue.Enqueue(n);
            }
        }
        return byMap;
    }

    /// <summary>A track by name ("mist") or by number ("6"); prefix match as a fallback so "mon" finds
    /// "monkey". Null when nothing matches.
    ///
    /// <para><paramref name="set"/> is searched FIRST and the other set second, so the id spaces can overlap
    /// (midi 2 = "dragon", mp3 2 = "buyeo") while a player in either mode can still name any track he can
    /// hear. An id with no row resolves to an unnamed track in <paramref name="set"/> rather than to null —
    /// the client will happily play a number we have never given a name.</para></summary>
    public static MusicTrack? FindTrack(string query, MusicSet set = MusicSet.Old)
    {
        query = query.Trim();
        if (query.Length == 0) return null;
        var (mine, theirs) = (MusicTracks.Where(t => t.Set == set), MusicTracks.Where(t => t.Set != set));
        if (ushort.TryParse(query, out var id))
            return mine.FirstOrDefault(t => t.Id == id)
                ?? theirs.FirstOrDefault(t => t.Id == id)
                ?? new MusicTrack(id, "", set == MusicSet.New ? (byte)1 : (byte)2, set, false);
        return mine.FirstOrDefault(t => t.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? theirs.FirstOrDefault(t => t.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? mine.FirstOrDefault(t => t.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            ?? theirs.FirstOrDefault(t => t.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The name of a track id within one soundtrack, or "" if it has none (only some of the 12 stock
    /// midis are named).</summary>
    public static string TrackName(ushort id, MusicSet set = MusicSet.Old) =>
        MusicTracks.FirstOrDefault(t => t.Id == id && t.Set == set)?.Name ?? "";

    // Case-insensitive '*' glob (no '?', no escaping — map names have neither). Used for the MapBgm.csv
    // name patterns, e.g. "Buya *" matching "Buya Kan Shop" but not "Buyan Stables".
    private static bool GlobMatch(string text, string pattern)
    {
        if (pattern.Length == 0) return false;
        var parts = pattern.Split('*');
        if (parts.Length == 1) return text.Equals(pattern, StringComparison.OrdinalIgnoreCase);

        int pos = 0;
        if (!text.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase)) return false;
        pos = parts[0].Length;
        for (int i = 1; i < parts.Length - 1; i++)
        {
            if (parts[i].Length == 0) continue;
            int at = text.IndexOf(parts[i], pos, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            pos = at + parts[i].Length;
        }
        var tail = parts[^1];
        return tail.Length == 0
            ? true
            : text.Length - pos >= tail.Length && text.EndsWith(tail, StringComparison.OrdinalIgnoreCase);
    }
}
