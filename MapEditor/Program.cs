// Map editor for the Project1998 world data: a local web tool that renders game-data/maps/
// with the real 5.33 tile art. Edits are saved as DRAFTS (game-data/maps-edited/) — the
// shipped maps the server serves are only ever changed by a deliberate manual copy.
//
// One deliberate asymmetry, straight from re/render_maps.py: BOTH "modes" draw with the 5.33
// tileset. The 4.x/5.33 mode in the UI selects the FILE FORMAT in and out (.map vs .cmp), not
// the art -- the 5.33 tileset is the correct look for this server's players, and the KRU
// retail tiles are a re-tiled set that silently mis-draws 4.x maps.

using System.Buffers.Binary;
using System.IO.Compression;
using MapEditor;

// This is a WINDOWED app (WinExe — no console): the default run shows the WebView2 shell.
//   --browser     use the system browser instead of the window (heartbeat auto-exit armed)
//   --no-browser  server only, for dev harnesses (no window, no tab, no auto-exit)
//   --heartbeat   arm the no-clients auto-exit explicitly (useful with --no-browser)
// Console output tees into a log file, since a WinExe launched by double-click has nowhere
// else to put it.
bool browserMode = args.Contains("--browser");
bool serverOnly = args.Contains("--no-browser");
bool windowMode = !browserMode && !serverOnly;
bool heartbeatArmed = browserMode || args.Contains("--heartbeat");
try
{
    Console.SetOut(new TeeWriter(Console.Out,
        new StreamWriter(Path.Combine(Path.GetTempPath(), "nexustk-map-editor.log"), false) { AutoFlush = true }));
}
catch { /* logging is best-effort */ }

string? repo = FindRepoRoot();
if (repo is null)
    return Fail("Could not find the game data (a game-data/maps folder).\n" +
        "Put this program inside the Project1998 folder, next to a game-data folder,\n" +
        "or set the P1998_REPO environment variable to the Project1998 folder.");
string? tileDat = FindTileDat();
if (tileDat is null)
    return Fail("Tile.dat (the 5.33 client's tile art) was not found.\n" +
        "Looked next to this program, in %LOCALAPPDATA%/Project1998/game/533,\n" +
        "and at P1998_CLIENT5 if set. Install the 5.33 client or set P1998_CLIENT5.");

Console.WriteLine($"decoding {tileDat} ...");
var sw = System.Diagnostics.Stopwatch.StartNew();
var assets = TileAssets.Load(tileDat, Path.Combine(repo, "game-data", "Tile533Map.csv"));
Console.WriteLine($"  {assets.GroundCount} ground frames, {assets.TilecCount} object frames, " +
                  $"{assets.Objs.Count} SObj, {assets.Sheet2Runs.Count} sheet-2 runs in {sw.ElapsedMilliseconds} ms");

string gameData = Path.Combine(repo, "game-data");
string mapsDir = Path.Combine(gameData, "maps");
var index = LoadIndex(Path.Combine(gameData, "map_index.csv"));

// This is a development tool: Save NEVER touches the shipped maps the server/client read,
// and everything the editor GENERATES lives with the tool under dist/NexusTK-Map-Editor/
// saved/ — never inside game-data. saved/maps holds the draft .map files (loading prefers
// the draft so work continues across sessions), saved/csvs the exported Corrections and
// Spawns rows. Publishing anything into game-data is a deliberate manual copy/append.
string savedRoot = Path.Combine(repo, "dist", "NexusTK-Map-Editor", "saved");
string draftsDir = Path.Combine(savedRoot, "maps");
string csvsDir = Path.Combine(savedRoot, "csvs");
string ShippedPath(int id) => Path.Combine(mapsDir, $"TK{id}.map");
string DraftPath(int id) => Path.Combine(draftsDir, $"TK{id}.map");
string LivePath(int id) => File.Exists(DraftPath(id)) ? DraftPath(id) : ShippedPath(id);
string SavedRel(string path) => Path.GetRelativePath(repo, path).Replace('\\', '/');

// NEW maps (created in the editor) exist only as drafts plus rows in saved/new-maps.csv —
// which is both this editor's supplemental index and the export artifact: its rows are
// exactly map_index.csv rows. Publishing a new map = append its row to
// game-data/map_index.csv, copy the .map from saved/maps, @reload (the server's map
// registry IS map_index.csv; Maps.csv meta is optional extras).
string newMapsCsv = Path.Combine(savedRoot, "new-maps.csv");
Dictionary<int, (string Name, int Xs, int Ys)> newMaps = File.Exists(newMapsCsv) ? LoadIndex(newMapsCsv) : new();
// Ids reserved by the FULL RTK Maps.csv (9,850 rows up to 65440), not just the served
// subset: a custom map on one of those ids would collide if that content is ever
// imported — and the Maps.csv meta row would silently apply to it after publish.
var reservedIds = Markers.ReservedMapIds(gameData);
int SuggestNewId()
{
    int id = 59000;   // first 500+-id block free in BOTH Maps.csv and map_index.csv
    while (reservedIds.Contains(id) || TryMap(id, out _)) id++;
    return id;
}
bool TryMap(int id, out (string Name, int Xs, int Ys) m) => index.TryGetValue(id, out m) || newMaps.TryGetValue(id, out m);
void SaveNewMaps()
{
    Directory.CreateDirectory(savedRoot);
    var sb = new System.Text.StringBuilder("id,name,xs,ys\n");
    foreach (var kv in newMaps.OrderBy(k => k.Key))
        sb.Append($"{kv.Key},{kv.Value.Name},{kv.Value.Xs},{kv.Value.Ys}\n");
    File.WriteAllText(newMapsCsv, sb.ToString());
}

// One-time migration: an earlier build kept drafts in game-data/maps-edited/.
string oldDrafts = Path.Combine(repo, "game-data", "maps-edited");
if (Directory.Exists(oldDrafts))
{
    var stray = Directory.GetFiles(oldDrafts, "TK*.map");
    if (stray.Length > 0)
    {
        Directory.CreateDirectory(draftsDir);
        foreach (var f in stray)
        {
            var dest = Path.Combine(draftsDir, Path.GetFileName(f));
            if (!File.Exists(dest)) File.Move(f, dest);
        }
        Console.WriteLine($"moved {stray.Length} draft map(s) from game-data/maps-edited to {SavedRel(draftsDir)}");
    }
    if (Directory.GetFileSystemEntries(oldDrafts).Length == 0) Directory.Delete(oldDrafts);
}

// --port <n> picks the preferred port (a second copy — e.g. one per checkout — stays
// addressable instead of silently sliding to the next free port).
int preferred = 5959;
var portArg = Array.IndexOf(args, "--port");
if (portArg >= 0 && portArg + 1 < args.Length && int.TryParse(args[portArg + 1], out var pv)) preferred = pv;
int port = FreePort(preferred);
string url = $"http://127.0.0.1:{port}";
// Content root must be wherever wwwroot actually is: the CWD during `dotnet run` (which
// sets it to the project dir), else the exe's folder or its ancestors (the published exe
// has wwwroot beside it; a dev-built exe finds MapEditor/wwwroot above bin/) — so the
// exe launched from ANY working directory still serves the UI.
string contentRoot = AppContext.BaseDirectory;
if (Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot")))
    contentRoot = Directory.GetCurrentDirectory();
else
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        if (Directory.Exists(Path.Combine(d.FullName, "wwwroot"))) { contentRoot = d.FullName; break; }
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = contentRoot });
builder.WebHost.UseUrls(url);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/meta", () => Results.Json(new
{
    cell = TileAssets.Cell,
    atlasCols = TileAssets.AtlasCols,
    groundCount = assets.GroundCount,
    tilecCount = assets.TilecCount,
    sheet2 = assets.Sheet2Runs.Select(r => new[] { r.Legacy, r.Count, r.S533 }),
    objs = assets.Objs,
    objFlags = assets.ObjFlags,
    maps = index.Select(kv => new
    {
        id = kv.Key,
        name = kv.Value.Name,
        xs = kv.Value.Xs,
        ys = kv.Value.Ys,
        file = File.Exists(ShippedPath(kv.Key)),
        draft = File.Exists(DraftPath(kv.Key)),
        custom = false
    }).Concat(newMaps.Select(kv => new
    {
        id = kv.Key,
        name = kv.Value.Item1,
        xs = kv.Value.Item2,
        ys = kv.Value.Item3,
        file = File.Exists(DraftPath(kv.Key)),
        draft = true,
        custom = true
    })).OrderBy(m => m.id),
    suggestedNewId = SuggestNewId(),
    doorOpen = Markers.DoorDefaultOpen(gameData),
    mobs = Markers.Mobs(gameData),
    npcTemplates = Markers.NpcTemplates(gameData)
}));

// Heartbeat: the page POSTs here every few seconds. When armed (--browser/--heartbeat),
// the server exits itself ~15s after the last client goes away — closing the tab is
// enough, no console to hunt down. Never armed in window mode (the window closing exits)
// or plain --no-browser (dev harnesses poke the API with no page open).
var hb = new HeartbeatState();
app.MapPost("/api/ping", () => { hb.Seen = true; hb.Last = Environment.TickCount64; return Results.Ok(); });

app.MapGet("/api/tiles/ground.png", () => Results.Bytes(assets.GroundPng, "image/png"));
app.MapGet("/api/tiles/tilec.png", () => Results.Bytes(assets.TilecPng, "image/png"));

// Load prefers the draft so a mapping session survives a restart; X-Draft says which
// file the bytes came from.
app.MapGet("/api/map/{id:int}", (int id, HttpResponse resp) =>
{
    string path = LivePath(id);
    if (!File.Exists(path)) return Results.NotFound();
    resp.Headers["X-Draft"] = path == DraftPath(id) ? "1" : "0";
    return Results.Bytes(File.ReadAllBytes(path), "application/octet-stream");
});

// New map: writes a blank (all-void) draft and a saved/new-maps.csv row. Nothing in
// game-data changes until the row and file are hand-published.
app.MapPost("/api/maps", (NewMapReq req) =>
{
    if (req.Id < 1 || req.Id > 65535) return Results.BadRequest("map id must be 1..65535");
    if (TryMap(req.Id, out _)) return Results.BadRequest($"map id {req.Id} is already taken");
    if (reservedIds.Contains(req.Id))
        return Results.BadRequest($"map id {req.Id} is reserved by the full RTK Maps.csv (not served here, " +
            $"but its meta row would apply to your map and later content imports would collide) — try {SuggestNewId()}");
    if (req.Xs < 5 || req.Xs > 255 || req.Ys < 5 || req.Ys > 255)
        return Results.BadRequest("dimensions must be 5..255 per axis (the largest shipped map is 250x220)");
    var name = (req.Name ?? "").Trim();
    if (name.Length == 0) return Results.BadRequest("a name is required");
    Directory.CreateDirectory(draftsDir);
    File.WriteAllBytes(DraftPath(req.Id), new byte[req.Xs * req.Ys * 4]);
    newMaps[req.Id] = (name, req.Xs, req.Ys);
    SaveNewMaps();
    return Results.Ok(new { id = req.Id, name, xs = req.Xs, ys = req.Ys, row = SavedRel(newMapsCsv) });
});

// Save writes the DRAFT only — game-data/maps/ stays exactly as shipped.
app.MapPut("/api/map/{id:int}", async (int id, HttpRequest req) =>
{
    if (!TryMap(id, out var dims)) return Results.BadRequest("unknown map id");
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var data = ms.ToArray();
    int expect = dims.Xs * dims.Ys * 4;
    if (data.Length != expect)
        return Results.BadRequest($"size {data.Length} != {dims.Xs}x{dims.Ys}x4 = {expect}");
    Directory.CreateDirectory(draftsDir);
    File.WriteAllBytes(DraftPath(id), data);
    return Results.Ok(new { saved = data.Length, draft = SavedRel(DraftPath(id)) });
});

// Discarding the draft of a NEW map deletes the map itself — it has no shipped file to
// fall back to, so its index row goes too.
app.MapDelete("/api/map/{id:int}/draft", (int id) =>
{
    if (!File.Exists(DraftPath(id))) return Results.NotFound();
    File.Delete(DraftPath(id));
    bool removedMap = newMaps.Remove(id);
    if (removedMap) SaveNewMaps();
    return Results.Ok(new { removedMap });
});

// Read-only overlay data: warps / world-map cells / spawns / NPCs pointing at this map,
// so the mapper sees which cells server content depends on before painting over them.
app.MapGet("/api/map/{id:int}/markers", (int id) =>
    Results.Json(Markers.For(id, gameData,
        m => TryMap(m, out var mi) ? mi.Name : "")));

// Placed-spawn export: JSON [{x,y,mob}] → Spawns.csv rows, SpnId numbered after the
// file's current max. Like Corrections, the rows DOWNLOAD for a deliberate hand-append —
// the editor never writes the tracked CSVs (Content.LoadSpawns only reads
// SpnMobId/SpnMapId/SpnX/SpnY; the RTK bookkeeping columns get zeros).
app.MapPost("/api/map/{id:int}/spawns.csv", (int id, List<PlacedSpawn> placed, HttpResponse resp) =>
{
    if (!TryMap(id, out var dims)) return Results.BadRequest("unknown map id");
    if (placed is null || placed.Count == 0) return Results.BadRequest("no spawn points in body");
    foreach (var p in placed)
        if (p.X < 0 || p.Y < 0 || p.X >= dims.Xs || p.Y >= dims.Ys)
            return Results.BadRequest($"({p.X},{p.Y}) is outside {dims.Xs}x{dims.Ys}");
    int next = Markers.MaxSpawnId(gameData) + 1;
    var sb = new System.Text.StringBuilder(
        "SpnId,SpnMobId,SpnMapId,SpnX,SpnY,SpnLastDeath,SpnStartTime,SpnEndTime,SpnMobIdReplace\n");
    foreach (var p in placed)
        sb.Append($"{next++},{p.Mob},{id},{p.X},{p.Y},0,0,0,0\n");
    resp.Headers["X-Row-Count"] = placed.Count.ToString();
    Directory.CreateDirectory(csvsDir);
    var outFile = Path.Combine(csvsDir, $"spawns-TK{id}.csv");
    File.WriteAllText(outFile, sb.ToString());
    resp.Headers["X-Saved"] = SavedRel(outFile);
    return Results.Text(sb.ToString(), "text/csv");
});

// Sparse-patch export: POST the editor's live cell buffer, get back MapCells.csv rows
// (Server/Content.cs LoadMapCells: blank column = inherit from the .map) for exactly the
// cells that differ from the SHIPPED map — saves are drafts that never touch it, so
// game-data/maps IS the baseline. Small fixes become reviewable CSV rows in git instead
// of a rewritten binary map.
app.MapPost("/api/map/{id:int}/mapcells.csv", async (int id, HttpRequest req, HttpResponse resp) =>
{
    if (!index.TryGetValue(id, out var dims))
        return newMaps.ContainsKey(id)
            ? Results.BadRequest("a new map has no shipped baseline to diff against — publish the whole .map from saved/maps instead")
            : Results.BadRequest("unknown map id");
    string baseline = ShippedPath(id);
    if (!File.Exists(baseline)) return Results.BadRequest("no shipped map on disk");
    var old = File.ReadAllBytes(baseline);
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var cur = ms.ToArray();
    int cells = dims.Xs * dims.Ys;
    if (cur.Length != cells * 4) return Results.BadRequest($"buffer {cur.Length} != {dims.Xs}x{dims.Ys}x4");
    if (old.Length != cells * 4) return Results.BadRequest($"baseline {old.Length} != {dims.Xs}x{dims.Ys}x4");
    var sb = new System.Text.StringBuilder("Map,X,Y,Tile,Pass,Obj,Sources\n");
    int n = 0;
    for (int i = 0; i < cells; i++)
    {
        ushort og = BinaryPrimitives.ReadUInt16LittleEndian(old.AsSpan(i * 4));
        ushort oo = BinaryPrimitives.ReadUInt16LittleEndian(old.AsSpan(i * 4 + 2));
        ushort cg = BinaryPrimitives.ReadUInt16LittleEndian(cur.AsSpan(i * 4));
        ushort co = BinaryPrimitives.ReadUInt16LittleEndian(cur.AsSpan(i * 4 + 2));
        if (og == cg && oo == co) continue;
        string tile = (og & 0x3FFF) != (cg & 0x3FFF) ? (cg & 0x3FFF).ToString() : "";
        string pass = (og >> 14) != (cg >> 14) ? (cg >> 14).ToString() : "";
        string obj = (oo & 0x3FFF) != (co & 0x3FFF) ? (co & 0x3FFF).ToString() : "";
        sb.Append($"{id},{i % dims.Xs},{i / dims.Xs},{tile},{pass},{obj},map-editor\n");
        n++;
    }
    resp.Headers["X-Cell-Count"] = n.ToString();
    if (n > 0)
    {
        Directory.CreateDirectory(csvsDir);
        var outPath = Path.Combine(csvsDir, $"mapcells-TK{id}.csv");
        File.WriteAllText(outPath, sb.ToString());
        resp.Headers["X-Saved"] = SavedRel(outPath);
    }
    return Results.Text(sb.ToString(), "text/csv");
});

// Exports read what the editor shows: the draft when one exists, else the shipped map.
// Placed-warp export: JSON [{sm,sx,sy,dm,dx,dy}] → Warps.csv rows, WarpId numbered after
// the file's current max, written to saved/csvs for a deliberate hand-append. Pairs span
// maps, so this is one global file rather than per-map.
app.MapPost("/api/warps.csv", (List<PlacedWarp> warps, HttpResponse resp) =>
{
    if (warps is null || warps.Count == 0) return Results.BadRequest("no warp pairs in body");
    foreach (var w in warps)
    {
        if (!TryMap(w.Sm, out var sd) || !TryMap(w.Dm, out var dd))
            return Results.BadRequest($"unknown map in pair TK{w.Sm}->TK{w.Dm}");
        if (w.Sx < 0 || w.Sy < 0 || w.Sx >= sd.Xs || w.Sy >= sd.Ys)
            return Results.BadRequest($"source ({w.Sx},{w.Sy}) outside TK{w.Sm} {sd.Xs}x{sd.Ys}");
        if (w.Dx < 0 || w.Dy < 0 || w.Dx >= dd.Xs || w.Dy >= dd.Ys)
            return Results.BadRequest($"destination ({w.Dx},{w.Dy}) outside TK{w.Dm} {dd.Xs}x{dd.Ys}");
    }
    int next = Markers.MaxWarpId(gameData) + 1;
    var sb = new System.Text.StringBuilder("WarpId,SourceMapId,SourceX,SourceY,DestinationMapId,DestinationX,DestinationY\n");
    foreach (var w in warps)
        sb.Append($"{next++},{w.Sm},{w.Sx},{w.Sy},{w.Dm},{w.Dx},{w.Dy}\n");
    resp.Headers["X-Row-Count"] = warps.Count.ToString();
    Directory.CreateDirectory(csvsDir);
    var outFile = Path.Combine(csvsDir, "warps-pending.csv");
    File.WriteAllText(outFile, sb.ToString());
    resp.Headers["X-Saved"] = SavedRel(outFile);
    return Results.Text(sb.ToString(), "text/csv");
});

// Placed-NPC export: each pending placement is a COPY of an existing NPCs.csv row (the
// template) at a new map/cell, with the identifier/description optionally overridden —
// look, type, and behavior flags come from the template verbatim, since those are what
// the editor cannot author. Rows are emitted in the file's own column order, NpcId
// numbered past the current max, Enabled forced to 1, and written to saved/csvs for a
// deliberate hand-append.
app.MapPost("/api/npcs.csv", (List<PlacedNpc> npcs, HttpResponse resp) =>
{
    if (npcs is null || npcs.Count == 0) return Results.BadRequest("no NPC placements in body");
    foreach (var p in npcs)
    {
        if (!TryMap(p.Map, out var dd)) return Results.BadRequest($"unknown map TK{p.Map}");
        if (p.X < 0 || p.Y < 0 || p.X >= dd.Xs || p.Y >= dd.Ys)
            return Results.BadRequest($"({p.X},{p.Y}) outside TK{p.Map} {dd.Xs}x{dd.Ys}");
    }
    var header = Markers.CsvHeader(Path.Combine(gameData, "NPCs.csv"));
    if (header.Length == 0) return Results.BadRequest("NPCs.csv has no header");
    var (byId, maxId) = Markers.NpcRows(gameData);
    int next = maxId + 1;
    var sb = new System.Text.StringBuilder(string.Join(',', header)).Append('\n');
    foreach (var p in npcs)
    {
        if (!byId.TryGetValue(p.Template, out var t))
            return Results.BadRequest($"unknown template NpcId {p.Template}");
        var row = new Dictionary<string, string>(t, StringComparer.OrdinalIgnoreCase)
        {
            ["NpcId"] = (next++).ToString(),
            ["NpcMapId"] = p.Map.ToString(),
            ["NpcX"] = p.X.ToString(),
            ["NpcY"] = p.Y.ToString(),
            ["Enabled"] = "1",
        };
        if (!string.IsNullOrWhiteSpace(p.Identifier)) row["NpcIdentifier"] = p.Identifier.Trim();
        if (!string.IsNullOrWhiteSpace(p.Description)) row["NpcDescription"] = p.Description.Trim();
        sb.Append(string.Join(',', header.Select(h => CsvEsc(row.GetValueOrDefault(h, ""))))).Append('\n');
    }
    resp.Headers["X-Row-Count"] = npcs.Count.ToString();
    Directory.CreateDirectory(csvsDir);
    var outFile = Path.Combine(csvsDir, "npcs-pending.csv");
    File.WriteAllText(outFile, sb.ToString());
    resp.Headers["X-Saved"] = SavedRel(outFile);
    return Results.Text(sb.ToString(), "text/csv");
});

app.MapGet("/api/map/{id:int}/export.cmp", (int id) =>
{
    if (!TryMap(id, out var dims)) return Results.NotFound();
    string path = LivePath(id);
    if (!File.Exists(path)) return Results.NotFound();
    var cmp = MapToCmp(File.ReadAllBytes(path), dims.Xs, dims.Ys);
    return Results.File(cmp, "application/octet-stream", $"TK{id:D6}.cmp");
});

app.MapGet("/api/map/{id:int}/export.map", (int id) =>
{
    string path = LivePath(id);
    return File.Exists(path)
        ? Results.File(File.ReadAllBytes(path), "application/octet-stream", $"TK{id}.map")
        : Results.NotFound();
});

// Import: body is a .cmp (detected by the CMAP magic; dims come from its header) or a headerless
// .map (dims must come from ?xs=&ys=). Returns the cells as .map words + the dims in headers —
// nothing touches disk until the user saves.
app.MapPost("/api/import", async (HttpRequest req, HttpResponse resp, int? xs, int? ys) =>
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var data = ms.ToArray();
    byte[] words;
    int w, h;
    if (data.Length > 8 && data[0] == 'C' && data[1] == 'M' && data[2] == 'A' && data[3] == 'P')
    {
        uint packed = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        w = (int)(packed & 0xFFFF);
        h = (int)(packed >> 16);
        using var z = new ZLibStream(new MemoryStream(data, 8, data.Length - 8), CompressionMode.Decompress);
        using var payload = new MemoryStream();
        z.CopyTo(payload);
        var cells = payload.ToArray();
        if (cells.Length != w * h * 6) return Results.BadRequest($"cmp payload {cells.Length} != {w}x{h}x6");
        words = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            ushort ground = BinaryPrimitives.ReadUInt16LittleEndian(cells.AsSpan(i * 6));
            ushort pass = BinaryPrimitives.ReadUInt16LittleEndian(cells.AsSpan(i * 6 + 2));
            ushort obj = BinaryPrimitives.ReadUInt16LittleEndian(cells.AsSpan(i * 6 + 4));
            ushort g16 = (ushort)((ground & 0x3FFF) | ((pass & 3) << 14));
            BinaryPrimitives.WriteUInt16LittleEndian(words.AsSpan(i * 4), g16);
            BinaryPrimitives.WriteUInt16LittleEndian(words.AsSpan(i * 4 + 2), (ushort)(obj & 0x3FFF));
        }
    }
    else
    {
        if (xs is null || ys is null || xs * ys * 4 != data.Length)
            return Results.BadRequest($"headerless .map needs ?xs=&ys= with xs*ys*4 == {data.Length}");
        w = xs.Value; h = ys.Value;
        words = data;
    }
    resp.Headers["X-Xs"] = w.ToString();
    resp.Headers["X-Ys"] = h.ToString();
    return Results.Bytes(words, "application/octet-stream");
});

// Dev aid: the frontend can post its canvas as a data URL and get a PNG on disk (the browser
// sandbox blocks page-initiated downloads in some hosts; this is the reliable path out).
app.MapPost("/api/debug/shot", async (HttpRequest req) =>
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var s = System.Text.Encoding.ASCII.GetString(ms.ToArray());
    int comma = s.IndexOf(',');
    var path = Path.Combine(Path.GetTempPath(), "mapeditor-shot.png");
    File.WriteAllBytes(path, Convert.FromBase64String(comma >= 0 ? s[(comma + 1)..] : s));
    return Results.Text(path);
});

Console.WriteLine($"map editor at {url}");

// Heartbeat watchdog: armed modes exit ~15s after the last ping once a client was seen.
using var hbTimer = new System.Threading.Timer(_ =>
{
    if (!heartbeatArmed || !hb.Seen) return;
    if (Environment.TickCount64 - hb.Last > 15_000)
    {
        Console.WriteLine("no client for 15s — shutting down");
        app.Lifetime.StopApplication();
    }
}, null, 5_000, 5_000);

if (windowMode)
{
    await app.StartAsync();
    if (AppWindow.Run(url))          // blocks until the window closes
    {
        await app.StopAsync();
        return 0;
    }
    // WebView2 runtime missing: fall back to the system browser, heartbeat governs exit.
    Console.WriteLine("WebView2 runtime not found — falling back to the system browser");
    heartbeatArmed = true;
    OpenBrowser(url);
    await app.WaitForShutdownAsync();
    return 0;
}
if (browserMode) app.Lifetime.ApplicationStarted.Register(() => OpenBrowser(url));
app.Run();
return 0;

static void OpenBrowser(string url)
{
    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
    catch { /* no default browser handler — the log shows the address */ }
}

static int Fail(string msg)
{
    Console.WriteLine(msg);
    try { System.Windows.Forms.MessageBox.Show(msg, "NexusTK Map Editor", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error); }
    catch { /* headless session — the log has it */ }
    return 1;
}

// ---------------------------------------------------------------------------- helpers

static string? FindRepoRoot()
{
    var env = Environment.GetEnvironmentVariable("P1998_REPO");
    if (env is not null && Directory.Exists(Path.Combine(env, "game-data", "maps"))) return env;
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "game-data", "maps")))
                return d.FullName;
    return null;
}

// Tile.dat search order: P1998_CLIENT5, next to this program, the standard client install.
static string? FindTileDat()
{
    var candidates = new List<string>();
    var env = Environment.GetEnvironmentVariable("P1998_CLIENT5");
    if (env is not null) candidates.Add(Path.Combine(env, "Tile.dat"));
    candidates.Add(Path.Combine(AppContext.BaseDirectory, "Tile.dat"));
    candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Project1998", "game", "533", "Tile.dat"));
    candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "Nexon", "NextAeon533", "Tile.dat"));
    return candidates.FirstOrDefault(File.Exists);
}

// First free TCP port at or after the preferred one, so a second copy (or another app on
// 5959) doesn't kill the launch — the browser gets whichever port we actually bound.
static int FreePort(int preferred)
{
    for (int p = preferred; p < preferred + 20; p++)
    {
        try
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, p);
            l.Start(); l.Stop();
            return p;
        }
        catch { }
    }
    return 0;   // let the OS pick
}

// Minimal CSV escaping for emitted rows (a template value could carry a comma or quote).
static string CsvEsc(string v) =>
    v.Contains(',') || v.Contains('"') ? '"' + v.Replace("\"", "\"\"") + '"' : v;


static Dictionary<int, (string Name, int Xs, int Ys)> LoadIndex(string path)
{
    var outp = new Dictionary<int, (string, int, int)>();
    foreach (var line in File.ReadLines(path).Skip(1))
    {
        // id,name,xs,ys — name may contain commas only if quoted; the file today has none, but split defensively.
        var p = line.Split(',');
        if (p.Length < 4) continue;
        if (!int.TryParse(p[0], out int id)) continue;
        outp[id] = (string.Join(',', p[1..^2]), int.Parse(p[^2]), int.Parse(p[^1]));
    }
    return outp;
}

// Exactly re/map2cmp.py: per cell [ground&0x3FFF][passBits][obj&0x3FFF] as u16LE triples,
// zlib-compressed behind "CMAP" + u32(H<<16|W).
static byte[] MapToCmp(byte[] mapBytes, int w, int h)
{
    var payload = new byte[w * h * 6];
    for (int i = 0; i < w * h; i++)
    {
        ushort g16 = BinaryPrimitives.ReadUInt16LittleEndian(mapBytes.AsSpan(i * 4));
        ushort o16 = BinaryPrimitives.ReadUInt16LittleEndian(mapBytes.AsSpan(i * 4 + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(i * 6), (ushort)(g16 & 0x3FFF));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(i * 6 + 2), (ushort)((g16 >> 14) & 3));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(i * 6 + 4), (ushort)(o16 & 0x3FFF));
    }
    using var zms = new MemoryStream();
    using (var z = new ZLibStream(zms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(payload);
    var comp = zms.ToArray();
    var outp = new byte[8 + comp.Length];
    outp[0] = (byte)'C'; outp[1] = (byte)'M'; outp[2] = (byte)'A'; outp[3] = (byte)'P';
    BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan(4), (uint)((h << 16) | w));
    comp.CopyTo(outp, 8);
    return outp;
}

// One pending spawn point from the placement tool (JSON body of /api/map/{id}/spawns.csv).
record PlacedSpawn(int X, int Y, int Mob);

// One pending warp leg (JSON body of /api/warps.csv): source map/cell → destination map/cell.
record PlacedWarp(int Sm, int Sx, int Sy, int Dm, int Dx, int Dy);

// One pending NPC placement (JSON body of /api/npcs.csv): a template NpcId copied to a new
// map/cell, identifier/description optionally overridden.
record PlacedNpc(int Map, int X, int Y, int Template, string? Identifier, string? Description);

// A new map to create (JSON body of /api/maps).
record NewMapReq(int Id, string Name, int Xs, int Ys);

// Last time any page pinged /api/ping (the frontend does, every ~3s while open).
sealed class HeartbeatState { public volatile bool Seen; public long Last; }

// Console output duplicated into the log file — a WinExe has no console to read.
sealed class TeeWriter(TextWriter a, TextWriter b) : TextWriter
{
    public override System.Text.Encoding Encoding => a.Encoding;
    public override void Write(char value) { a.Write(value); b.Write(value); }
    public override void Write(string? value) { a.Write(value); b.Write(value); }
    public override void WriteLine(string? value) { a.WriteLine(value); b.WriteLine(value); }
}
