using Shared;

namespace Server;

/// <summary>
/// Server-side terrain for a single map, loaded from a 4.x headerless <c>.map</c> file
/// (4 bytes/cell: <c>[ground u16 LE][object u16 LE]</c>, ground's top 2 bits = passability flag).
///
/// Both clients STREAM their terrain from here: after 0x15 map-info the client sends opcode 0x05 with a
/// view rectangle and the server replies with an opcode-0x06 cell block. (Confirmed by reversing
/// NexusTK.exe handler 0x469060, by the Mithia 7.x reference `clif_sendmapdata`, and — for 4.95 — by a
/// live capture of 2161 such requests.) 4.95 also keeps a local <c>Maps\TK&lt;id&gt;.map</c> copy, but that
/// is a CACHE it verifies with the checksum its requests carry, not its only source; 4.x distributions
/// that ship no <c>Maps</c> directory rely entirely on this. See docs §10.7.
///
/// <para><b>THREE LAYERS.</b> The <c>.map</c> file is never written — not by this class, not by anything
/// in the server. Every change is an overlay, and there are two distinct kinds that must not be
/// conflated:</para>
/// <list type="bullet">
///   <item><b>file</b> — exactly what is on disk. Also exactly what a 4.95 client with a populated
///     <c>Maps</c> directory draws before we stream it anything.</item>
///   <item><b>base</b> — file + AUTHORED overrides (<c>MapCells.csv</c>, <c>DoorObjects.csv</c>'s
///     defaultOpen, <c>Doors.csv</c>'s ForceOpen/DefaultClosed). "The shipped map is wrong here." Content:
///     lives in git, rebuilt from disk on <c>@reload</c>.</item>
///   <item><b>live</b> — base + RUNTIME mutations (a door a player opened, a GM edit, an event script).
///     What every read sees.</item>
/// </list>
/// <para>Two diffs fall out, each with exactly one consumer. <see cref="PatchRuns"/> is live-vs-FILE — the
/// cells where a cached client's own picture disagrees with ours, replayed as 0x06 patches on map entry.
/// <see cref="RuntimeCells"/> is live-vs-BASE — world state a player created, and the only thing worth
/// persisting. Persisting against the base rather than the file is what keeps <c>MapCells.csv</c>
/// authoritative: bake authored corrections into the DB and editing the CSV would silently stop
/// working.</para>
///
/// <para>Both diffs are stored SPARSELY (the value the cell moved off, for the cells that moved). Full
/// parallel arrays would triple the memory of every cached map — and the cache holds every map anyone
/// visits, for the process lifetime — while the number of cells that actually differ is a handful.</para>
/// </summary>
public sealed class MapData
{
    /// <summary>One cell's three components. <c>Pass</c> is the 2-bit ground flag, not a full short.</summary>
    public readonly record struct Cell(ushort Tile, ushort Pass, ushort Obj);

    public ushort Id { get; }
    public ushort Xs { get; }
    public ushort Ys { get; }

    // LIVE terrain — every read (collision, mob AI, the terrain stream) sees these.
    private readonly ushort[] _tile;
    private readonly ushort[] _pass;
    private readonly ushort[] _obj;

    // Sparse diffs, holding the value each cell HELD in that layer. See the class doc.
    private readonly Dictionary<int, Cell> _vsFile = new();
    private readonly Dictionary<int, Cell> _vsBase = new();
    private readonly object _gate = new();
    private bool _baselineSealed;

    private MapData(ushort id, ushort xs, ushort ys, ushort[] tile, ushort[] pass, ushort[] obj)
    {
        Id = id; Xs = xs; Ys = ys; _tile = tile; _pass = pass; _obj = obj;
    }

    public ushort Tile(int x, int y) => _tile[y * Xs + x];
    public ushort Pass(int x, int y) => _pass[y * Xs + x];
    public ushort Obj(int x, int y)  => _obj[y * Xs + x];

    /// <summary>The raw ground <c>u16</c> for a cell — tile in the low 14 bits, passability flag in the top 2.
    /// This is the wire form both the 0x06 cell-patch and the 4.95 terrain stream carry as a cell's first
    /// word, so a streamed cell is byte-identical to what the client would read from its own copy.</summary>
    public ushort GroundWord(int x, int y) => (ushort)((_tile[y * Xs + x] & 0x3FFF) | (_pass[y * Xs + x] << 14));

    /// <summary>This cell as it stands right now.</summary>
    public Cell At(int x, int y) { int i = y * Xs + x; return new Cell(_tile[i], _pass[i], _obj[i]); }

    /// <summary>Has RUNTIME (not authoring) moved this cell off the authored baseline? Callers that persist
    /// use this to decide between writing a row and deleting one — see <c>MapStore.Persist</c>.</summary>
    public bool IsRuntimeChanged(int x, int y) { lock (_gate) return _vsBase.ContainsKey(y * Xs + x); }

    /// <summary>The one mutation path. <paramref name="authored"/> distinguishes a content correction
    /// (part of the baseline, never persisted) from a runtime change (world state, persisted).
    /// Null components are left alone, so a caller can override passability without touching the graphic.</summary>
    private void Set(int x, int y, ushort? tile, ushort? pass, ushort? obj, bool authored)
    {
        if (x < 0 || y < 0 || x >= Xs || y >= Ys) return;
        int i = y * Xs + x;
        lock (_gate)
        {
            var before = new Cell(_tile[i], _pass[i], _obj[i]);
            // Seed each layer's "what it was" on the FIRST change that crosses it. For _vsFile that is the
            // first change of any kind (live still equals disk). For _vsBase it is the first RUNTIME change
            // (live still equals base, because authoring is finished before runtime starts — SealBaseline).
            if (!_vsFile.ContainsKey(i)) _vsFile[i] = before;
            if (!authored && !_vsBase.ContainsKey(i)) _vsBase[i] = before;

            if (tile is not null) _tile[i] = (ushort)(tile.Value & 0x3FFF);
            if (pass is not null) _pass[i] = (ushort)(pass.Value & 0x3);
            if (obj  is not null) _obj[i]  = (ushort)(obj.Value  & 0x3FFF);

            // Back to where a layer started = no longer a diff against it (a door toggled shut again).
            var after = new Cell(_tile[i], _pass[i], _obj[i]);
            if (_vsFile.TryGetValue(i, out var f) && f == after) _vsFile.Remove(i);
            if (_vsBase.TryGetValue(i, out var b) && b == after) _vsBase.Remove(i);
        }
    }

    /// <summary>Apply an AUTHORED override — part of the baseline, not world state. Only valid during
    /// <see cref="Load"/>, before <see cref="SealBaseline"/>; afterwards it would corrupt the runtime diff
    /// (every later runtime change would measure against the wrong origin), so it degrades to a runtime
    /// change and complains rather than silently poisoning the baseline.</summary>
    public void Author(int x, int y, ushort? tile, ushort? pass, ushort? obj)
    {
        if (_baselineSealed)
        {
            Log.Info($"   !! map {Id}: authored override at ({x},{y}) arrived AFTER the baseline was sealed — treating as runtime");
            Set(x, y, tile, pass, obj, authored: false);
            return;
        }
        Set(x, y, tile, pass, obj, authored: true);
    }

    /// <summary>End the authoring phase. Everything after this counts as runtime world state.</summary>
    public void SealBaseline() { lock (_gate) _baselineSealed = true; }

    /// <summary>Apply a RUNTIME change (door toggle, GM edit, event script). Persisted by the caller.</summary>
    public void SetCell(int x, int y, ushort? tile, ushort? pass, ushort? obj) => Set(x, y, tile, pass, obj, false);

    /// <summary>Change the object tile at (x,y) — the door toggle ('o'/0x20) uses this. The cache is
    /// process-wide, so a toggled door is shared world state: everyone already on the map is told to redraw via
    /// the 0x06 patch, and reading it back lets the next 'o' toggle the door closed again. No-op if out of range.
    /// Not purely cosmetic: the object layer carries the client's <c>SObj.tbl</c> directional walls, so swapping
    /// a gate's solid leaves (obj 5-8) for its open ones (15-18) is what actually opens the way through.</summary>
    public void SetObj(int x, int y, ushort obj) => Set(x, y, null, null, obj, false);

    /// <summary>Every cell whose LIVE state differs from the on-disk <c>.map</c>, grouped into horizontal runs
    /// (<c>startX, y, objs</c>) — one 0x06 cell-patch each. This is what a client holding its own copy of the
    /// file needs replayed on map entry: authored corrections, doors opened at load, doors another player has
    /// toggled, and <see cref="Doors.IsForceOpen"/> tiles. A cell whose only change is ground/passability is
    /// included too — the patch sender reads the ground word live, so the run carries objects alone.
    /// (A client we are streaming to gets all of this in the stream itself; the replay is belt-and-braces
    /// for one that still has a populated <c>Maps</c> directory.)</summary>
    public IEnumerable<(ushort X, ushort Y, ushort[] Objs)> PatchRuns()
    {
        List<int> cells;
        lock (_gate) cells = _vsFile.Keys.OrderBy(i => i).ToList();   // row-major, so a row's cells are adjacent
        for (int i = 0; i < cells.Count;)
        {
            int start = cells[i], y = start / Xs, run = 1;
            while (i + run < cells.Count && cells[i + run] == start + run && (start + run) / Xs == y) run++;
            var objs = new ushort[run];
            for (int k = 0; k < run; k++) objs[k] = _obj[start + k];
            yield return ((ushort)(start % Xs), (ushort)y, objs);
            i += run;
        }
    }

    /// <summary>Every cell RUNTIME has moved off the authored baseline — the persistable world state, and
    /// nothing else. Deliberately excludes authored corrections so the CSV stays the source of truth for
    /// those; see the class doc.</summary>
    public IReadOnlyList<(ushort X, ushort Y, Cell Cell)> RuntimeCells()
    {
        lock (_gate)
            return _vsBase.Keys.OrderBy(i => i)
                .Select(i => ((ushort)(i % Xs), (ushort)(i / Xs), new Cell(_tile[i], _pass[i], _obj[i])))
                .ToList();
    }

    /// <summary>Is (x,y) impassable? Only the ground-passability flag blocks (water/cliff/out-of-bounds and
    /// the ground baked under walls) — the object layer is VISUAL, not collision (matches the RTK reference's
    /// map_canmove, which collides on pass only and leaves its object check commented out). This is the same
    /// test the player's walk uses (see Session.Blocked), so mob AI and player collision agree. Out-of-range
    /// coords count as solid.</summary>
    public bool Solid(int x, int y) =>
        x < 0 || y < 0 || x >= Xs || y >= Ys || _pass[y * Xs + x] != 0;

    /// <summary>Is a move INTO (x,y) while heading <paramref name="dir"/> (0=N 1=E 2=S 3=W) blocked? Combines
    /// the ground pass flag (<see cref="Solid"/>) with the client's <c>SObj.tbl</c> directional object-wall
    /// flags (<see cref="ObjectFlags"/>). Object walls sit on walkable ground (the pass flag is 0 under many
    /// building walls — only the door graphic itself gets pass=3), so the object layer is what stops you from
    /// walking through a hut's side; the 4.x client enforces this locally, and this makes server-side movement
    /// (mob AI + the player walk) agree. Out-of-range short-circuits to solid before the object read.
    /// A tile configured <see cref="Doors.IsForceOpen"/> bypasses both checks — that config ALSO authors the
    /// cell walkable at load, so the client agrees; this stays as the server-side guarantee.</summary>
    public bool BlockedMove(int x, int y, int dir) =>
        !Doors.IsForceOpen(Id, (ushort)x, (ushort)y) &&
        (Solid(x, y) || ObjectFlags.Blocks(_obj[y * Xs + x], dir));

    private static readonly Dictionary<ushort, MapData?> Cache = new();

    /// <summary>Load (and cache) map <paramref name="id"/> at the given dims, or null if the file is missing/short.</summary>
    public static MapData? For(ushort id, ushort xs, ushort ys)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(id, out var cached)) return cached;
            var md = Load(id, xs, ys);
            Cache[id] = md;
            return md;
        }
    }

    /// <summary>Load (and cache) map <paramref name="id"/>, taking its dims from the map registry. For callers
    /// that have a map id but no character to read dims off (events, GM tools, the door API).</summary>
    public static MapData? For(ushort id) =>
        Content.Maps.TryGetValue(id, out var mi) ? For(id, mi.Xs, mi.Ys) : null;

    /// <summary>Drop every cached map so the next <see cref="For"/> re-reads the <c>.map</c> file from disk.
    /// Called by the hot-reload path (<c>@reload</c>): a changed <c>TK&lt;id&gt;.map</c> (terrain / object edits)
    /// or a changed <c>MapCells.csv</c> then takes effect for anyone who re-enters or re-requests the map, no
    /// server restart needed. Runtime state (open doors) SURVIVES this — it is reloaded from the database on
    /// the next load, so a reload no longer slams every door shut the way it used to.</summary>
    public static void Invalidate()
    {
        lock (Cache) Cache.Clear();
    }

    private static MapData? Load(ushort id, ushort xs, ushort ys)
    {
        var path = Locate(id);
        if (path is null) { Log.Info($"   !! map TK{id}.map not found (searched NEXUS_MAPS, repo data/maps, client installs)"); return null; }

        var d = File.ReadAllBytes(path);
        int cells = xs * ys;
        if (d.Length < cells * 4)
        {
            Log.Info($"   !! map {path} is {d.Length}B, expected >= {cells * 4} for {xs}x{ys}");
            return null;
        }

        var tile = new ushort[cells];
        var pass = new ushort[cells];
        var obj  = new ushort[cells];
        for (int i = 0; i < cells; i++)
        {
            ushort g = (ushort)(d[i * 4] | (d[i * 4 + 1] << 8));
            ushort o = (ushort)(d[i * 4 + 2] | (d[i * 4 + 3] << 8));
            tile[i] = (ushort)(g & 0x3FFF);           // ground frame index (low 14 bits)
            pass[i] = (ushort)((g >> 14) & 0x3);      // passability flag (top 2 bits)
            obj[i]  = (ushort)(o & 0x3FFF);           // object frame index
        }
        var md = new MapData(id, xs, ys, tile, pass, obj);

        // ---- AUTHORED layer: "the shipped map is wrong here" ------------------------------------------
        // Ordered least-specific to most, so a hand-written MapCells.csv row always wins.

        // Doors that should START open (Content.DoorDefaultOpen, from DoorObjects.csv defaultOpen=1). The city
        // gates ship CLOSED in a few maps' object layers — Kugnae's south gate is the obvious one — and closed
        // gate leaves are solid in SObj.tbl, so they read as a wall you can't 'o' your way through unless the
        // table knows them. Rewriting here rather than editing the .map keeps the toggle symmetric ('o' closes
        // it again) and keeps the file pristine.
        int opened = 0;
        for (int i = 0; i < cells; i++)
            if (Content.DoorDefaultOpen.TryGetValue(obj[i], out var openId)) { md.Author(i % xs, i / xs, null, null, openId); opened++; }

        // Event doors configured to start shut + locked (Doors.csv DefaultClosed + ClosedObj).
        int shut = 0;
        foreach (var (x, y, objs) in Doors.DefaultClosedRuns(id))
        {
            for (int k = 0; k < objs.Length; k++) md.Author(x + k, y, null, null, objs[k]);
            shut++;
        }

        // ForceOpen tiles (Doors.csv): doors RTK ships with no open-graphic pair anywhere, so there is nothing
        // to toggle to. Author the cell walkable AND clear the blocking sprite, which is what the client needs
        // to agree with the server-side BlockedMove bypass. This used to be applied per-session on map entry,
        // which mutated shared map state from a session path; it belongs here, once, at load.
        int forced = 0;
        foreach (var (x, y) in Doors.ForceOpenTiles(id)) { md.Author(x, y, null, 0, 0); forced++; }

        // Hand-authored cell corrections (data/game-data/MapCells.csv). Last, so they beat everything above.
        int patched = 0;
        foreach (var c in Content.MapCellsFor(id)) { md.Author(c.X, c.Y, c.Tile, c.Pass, c.Obj); patched++; }

        md.SealBaseline();

        // ---- RUNTIME layer: world state players created, restored from SQLite -------------------------
        int restored = 0;
        foreach (var c in MapStore.Cells(id)) { md.SetCell(c.X, c.Y, c.Tile, c.Pass, c.Obj); restored++; }

        var notes = new List<string>();
        if (opened  > 0) notes.Add($"{opened} door cell(s) opened by default");
        if (shut    > 0) notes.Add($"{shut} door(s) closed by config");
        if (forced  > 0) notes.Add($"{forced} force-open tile(s)");
        if (patched > 0) notes.Add($"{patched} authored cell override(s)");
        if (restored> 0) notes.Add($"{restored} persisted runtime cell(s)");
        Log.Info($"   -> loaded map TK{id}.map ({xs}x{ys}, {cells} cells) from {path}" +
                 (notes.Count > 0 ? " — " + string.Join(", ", notes) : ""));
        return md;
    }

    private static string? Locate(ushort id)
    {
        string file = $"TK{id}.map";
        foreach (var dir in SearchDirs())
        {
            var p = Path.Combine(dir, file);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Startup sanity check: how many of the registry's maps actually have a <c>.map</c> file we can
    /// find, and where we looked. Server-side terrain drives COLLISION, mob/NPC spawn-tile picking, AND the
    /// terrain stream both clients now pull from, so a deployment missing the map set doesn't fail loudly on
    /// its own — players walk through walls and, with a client that has no local <c>Maps</c> directory, see
    /// nothing at all. On Windows the last-resort search dirs are the client installs, which is why this never
    /// bit locally and would bite immediately on a Linux host: copy the client's <c>Maps</c> directory to
    /// <c>data/maps</c> or point <c>NEXUS_MAPS</c> at it.</summary>
    public static (int found, int total, string[] dirs) Availability(IEnumerable<ushort> mapIds)
    {
        int found = 0, total = 0;
        foreach (var id in mapIds)
        {
            total++;
            if (Locate(id) is not null) found++;
        }
        return (found, total, SearchDirs().ToArray());
    }

    private static IEnumerable<string> SearchDirs()
    {
        var env = Environment.GetEnvironmentVariable("NEXUS_MAPS");
        if (!string.IsNullOrWhiteSpace(env)) yield return env;

        // repo data/maps (self-contained, committed)
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            bool isRoot = dir.GetFiles("*.sln").Length > 0
                       || (Directory.Exists(Path.Combine(dir.FullName, "Server"))
                           && Directory.Exists(Path.Combine(dir.FullName, "Shared")));
            if (isRoot) { yield return Path.Combine(dir.FullName, "data", "maps"); break; }
            dir = dir.Parent;
        }

        // fall back to the client installs, which ship the full 4.x map set
        yield return @"C:\Program Files (x86)\Nexon\NextAeon\Maps";
        yield return @"C:\Program Files (x86)\Nexon\NextAeon5\Maps";
    }
}
