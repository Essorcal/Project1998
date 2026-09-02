// Icon Studio: a local web tool for curating item icons one at a time. It reads the item art of
// every client it can find (the live retail client's 2x icons, the 4.95 and 5.33 sets), proposes
// downscaled candidates for icons a client lacks, lets you pixel-edit the result against the
// frame's real palette, and keeps what you approve as DRAFTS under dist/NexusTK-Icon-Studio/saved/.
// Nothing reaches a client until you press Export, and even then the patched Item.epf/.pal/.tbl
// and repacked archive land in the tool's saved/export folder for a deliberate hand-install.
//
// Same shape as MapEditor (WebView2 shell, --browser / --no-browser / --port, heartbeat exit),
// same rule: game files and client installs are never written by this program.

using System.Text;
using System.Text.Json;
using IconStudio;

bool browserMode = args.Contains("--browser");
bool serverOnly = args.Contains("--no-browser");
bool windowMode = !browserMode && !serverOnly;
bool heartbeatArmed = browserMode || args.Contains("--heartbeat");
try
{
    Console.SetOut(new TeeWriter(Console.Out,
        new StreamWriter(Path.Combine(Path.GetTempPath(), "nexustk-icon-studio.log"), false) { AutoFlush = true }));
}
catch { /* logging is best-effort */ }

string? repo = FindRepoRoot();
if (repo is null)
    return Fail("Could not find the game data (a game-data/Items.csv file).\n" +
        "Put this program inside the Project1998 folder, next to a game-data folder,\n" +
        "or set the P1998_REPO environment variable to the Project1998 folder.");

// The three icon sets. Any subset works; the tool needs at least one.
//   retail  the live KRU client — the 2x source for everything the older clients lack
//   495     the 4.95 client (NexusTK.dat)         533  the 5.33 client (Misc.dat)
var sources = new Dictionary<string, ItemArt>();
void TryLoad(string key, string label, params string?[] candidates)
{
    var path = candidates.FirstOrDefault(p => p is not null && File.Exists(p));
    if (path is null) { Console.WriteLine($"{label}: not found (skipped)"); return; }
    try
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var art = ItemArt.Load(label, path);
        sources[key] = art;
        Console.WriteLine($"{label}: {art.Count} frames, {art.Blocks.Count} palette blocks, tbl {(art.TblText ? "text" : "encoded")} — {path} ({sw.ElapsedMilliseconds} ms)");
    }
    catch (Exception ex) { Console.WriteLine($"{label}: failed to load {path}: {ex.Message}"); }
}
string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
string? Env(string v) => Environment.GetEnvironmentVariable(v);
TryLoad("retail", "retail",
    Env("P1998_CLIENT_LIVE") is { } live ? Path.Combine(live, "Data", "misc.dat") : null,
    Path.Combine(pf86, "KRU", "NexusTK", "Data", "misc.dat"));
TryLoad("495", "4.95",
    Env("P1998_CLIENT") is { } c4 ? Path.Combine(c4, "NexusTK.dat") : null,
    Path.Combine(local, "Project1998", "game", "4x", "NexusTK.dat"),
    Path.Combine(pf86, "Nexon", "NextAeon", "NexusTK.dat"));
TryLoad("533", "5.33",
    Env("P1998_CLIENT5") is { } c5 ? Path.Combine(c5, "Misc.dat") : null,
    Path.Combine(local, "Project1998", "game", "533", "Misc.dat"),
    Path.Combine(pf86, "Nexon", "NextAeon533", "Misc.dat"));
if (sources.Count == 0)
    return Fail("No item art found. Looked for the retail client's Data\\misc.dat (P1998_CLIENT_LIVE),\n" +
        "the 4.95 client's NexusTK.dat (P1998_CLIENT) and the 5.33 client's Misc.dat (P1998_CLIENT5).");
int maxCount = sources.Values.Max(a => a.Count);

// Which items use which icon (Items.csv ItmIcon -> identifiers), for search and labels.
var names = LoadIconNames(Path.Combine(repo, "game-data", "Items.csv"));

// Drafts live with the tool, never in game-data or a client folder. One JSON per icon id (the
// frame, its palette, approval), plus a PNG twin for eyes and for round-tripping through an
// external pixel editor (Import PNG reads it back).
string savedRoot = Path.Combine(repo, "dist", "NexusTK-Icon-Studio", "saved");
string draftsDir = Path.Combine(savedRoot, "icons");
string exportDir = Path.Combine(savedRoot, "export");
string SavedRel(string path) => Path.GetRelativePath(repo, path).Replace('\\', '/');
var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false };
var drafts = new SortedDictionary<int, Draft>();
if (Directory.Exists(draftsDir))
    foreach (var f in Directory.GetFiles(draftsDir, "*.json"))
    {
        try
        {
            var d = JsonSerializer.Deserialize<Draft>(File.ReadAllText(f), jsonOpts);
            if (d is not null && d.Valid()) drafts[d.Id] = d;
        }
        catch (Exception ex) { Console.WriteLine($"skipping unreadable draft {f}: {ex.Message}"); }
    }
Console.WriteLine($"{drafts.Count} draft(s) loaded from {SavedRel(draftsDir)} ({drafts.Values.Count(d => d.Approved)} approved)");

int preferred = 5961;
var portArg = Array.IndexOf(args, "--port");
if (portArg >= 0 && portArg + 1 < args.Length && int.TryParse(args[portArg + 1], out var pv)) preferred = pv;
int port = FreePort(preferred);
string url = $"http://127.0.0.1:{port}";
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
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

object IconRow(int id) => new
{
    id,
    n = names.TryGetValue(id, out var nl) ? nl.Take(4).ToArray() : Array.Empty<string>(),
    nc = names.TryGetValue(id, out var nl2) ? nl2.Count : 0,
    r = Has("retail", id), a = Has("495", id), b = Has("533", id),
    d = drafts.TryGetValue(id, out var dr) ? (dr.Approved ? 2 : 1) : 0,
};
bool Has(string src, int id) => sources.TryGetValue(src, out var a) && id < a.Count && a.Frames[id] is not null;

app.MapGet("/api/meta", () => Results.Json(new
{
    slot = Thumbs.Slot,
    atlasCols = Thumbs.Cols,
    sources = sources.ToDictionary(kv => kv.Key, kv => new { count = kv.Value.Count, path = kv.Value.DatPath, blocks = kv.Value.Blocks.Count }),
    count = maxCount,
    drafts = SavedRel(draftsDir),
    export = SavedRel(exportDir),
    icons = Enumerable.Range(0, maxCount).Select(IconRow),
}));

var hb = new HeartbeatState();
app.MapPost("/api/ping", () => { hb.Seen = true; hb.Last = Environment.TickCount64; return Results.Ok(); });

// Thumbnail atlases, baked lazily per source: 48px slots, 64 per row, frame i at (i % 64, i / 64).
var atlases = new Dictionary<string, byte[]>();
app.MapGet("/api/atlas/{src}.png", (string src) =>
{
    if (!sources.TryGetValue(src, out var art)) return Results.NotFound();
    lock (atlases)
    {
        if (!atlases.TryGetValue(src, out var png)) atlases[src] = png = Thumbs.Atlas(art);
        return Results.Bytes(png, "image/png");
    }
});

FrameDto? Dto(ItemArt? art, int id)
{
    if (art is null || id >= art.Count || art.Frames[id] is not { } f) return null;
    return FrameDto.From(f, art.PaletteRgb(id), art.PaletteIndex(id));
}

// Candidate: the retail frame resampled to w*h (default: half) and quantized into its own palette.
FrameDto? Candidate(int id, string method, int? w, int? h)
{
    if (!sources.TryGetValue("retail", out var ret) || id >= ret.Count || ret.Frames[id] is not { } f) return null;
    int w2 = w ?? (f.W + 1) / 2, h2 = h ?? (f.H + 1) / 2;
    if (w2 < 1 || h2 < 1 || w2 > 128 || h2 > 128) return null;
    var pal = ret.PaletteRgb(id);
    var (rgb, alpha) = Codec.Downscale(Codec.ToRgba(f, pal), f.W, f.H, w2, h2, method);
    var (idx, mask) = Codec.Quantize(rgb, alpha, pal);
    return FrameDto.From(Frame.Centered(w2, h2, idx, mask), pal, ret.PaletteIndex(id));
}

app.MapGet("/api/icon/{id:int}", (int id) =>
{
    if (id < 0 || id >= maxCount) return Results.NotFound();
    return Results.Json(new
    {
        id,
        names = names.TryGetValue(id, out var nl) ? nl : [],
        retail = Dto(sources.GetValueOrDefault("retail"), id),
        c495 = Dto(sources.GetValueOrDefault("495"), id),
        c533 = Dto(sources.GetValueOrDefault("533"), id),
        candidates = new
        {
            snap = Candidate(id, "snap", null, null),
            box = Candidate(id, "box", null, null),
            nearest = Candidate(id, "nearest", null, null),
        },
        draft = drafts.GetValueOrDefault(id),
    });
});

app.MapPost("/api/icon/{id:int}/candidate", (int id, CandidateReq req) =>
{
    var c = Candidate(id, req.Method is "box" or "nearest" ? req.Method : "snap", req.W, req.H);
    return c is null ? Results.BadRequest("no retail frame for this id, or size out of range (1..128)") : Results.Json(c);
});

// Save = write the draft JSON + PNG twin. The frame is validated against what the export can
// actually encode (size, buffer lengths, a 256-colour palette) so a bad save fails here, not later.
app.MapPut("/api/icon/{id:int}/draft", (int id, Draft d) =>
{
    if (id < 0 || id > 65534) return Results.BadRequest("icon id out of range");
    d = d with { Id = id, Updated = DateTime.UtcNow.ToString("o") };
    if (!d.Valid()) return Results.BadRequest("draft rejected: size must be 1..128 per axis, idx/alpha w*h bytes, palette 768 bytes");
    Directory.CreateDirectory(draftsDir);
    File.WriteAllText(Path.Combine(draftsDir, $"{id}.json"), JsonSerializer.Serialize(d, jsonOpts));
    File.WriteAllBytes(Path.Combine(draftsDir, $"{id}.png"), d.Png());
    drafts[id] = d;
    return Results.Ok(new { saved = SavedRel(Path.Combine(draftsDir, $"{id}.json")), png = SavedRel(Path.Combine(draftsDir, $"{id}.png")), row = IconRow(id) });
});

app.MapPost("/api/icon/{id:int}/approve", (int id, ApproveReq req) =>
{
    if (!drafts.TryGetValue(id, out var d)) return Results.NotFound();
    d = d with { Approved = req.Approved, Updated = DateTime.UtcNow.ToString("o") };
    File.WriteAllText(Path.Combine(draftsDir, $"{id}.json"), JsonSerializer.Serialize(d, jsonOpts));
    drafts[id] = d;
    return Results.Ok(new { row = IconRow(id) });
});

app.MapDelete("/api/icon/{id:int}/draft", (int id) =>
{
    if (!drafts.Remove(id)) return Results.NotFound();
    foreach (var ext in new[] { ".json", ".png" })
    {
        var p = Path.Combine(draftsDir, id + ext);
        if (File.Exists(p)) File.Delete(p);
    }
    return Results.Ok(new { row = IconRow(id) });
});

// Export: the target client's shipped item art plus ONLY the approved drafts — a draft on an
// existing id replaces that frame, one past the end appends (ids in between get 1x1 blank
// frames so the id space stays aligned; they draw nothing). Palette blocks are reused when
// an identical one exists, else appended. Output goes to saved/export/<client>/ with a
// manifest; installing it over a client's copy is a deliberate manual step.
app.MapPost("/api/export", (ExportReq req) =>
{
    if (!sources.TryGetValue(req.Client, out var art)) return Results.BadRequest($"client '{req.Client}' is not loaded");
    var approved = drafts.Values.Where(d => d.Approved).OrderBy(d => d.Id).ToList();
    if (approved.Count == 0) return Results.BadRequest("nothing approved yet — approve at least one draft");

    var frames = art.Frames.Select(f => f ?? Frame.Blank()).ToList();
    var recs = art.Recs.ToList();
    var blocks = art.Blocks.ToList();
    var byRgb = new Dictionary<string, int>();
    for (int i = 0; i < blocks.Count; i++) byRgb.TryAdd(Convert.ToBase64String(Codec.PalRgb(blocks[i])), i);
    var template = blocks.FirstOrDefault(b => b.Length == 1056)
        ?? sources.Values.SelectMany(a => a.Blocks).FirstOrDefault(b => b.Length == 1056);
    if (template is null) return Results.BadRequest("no plain palette block to use as a template");
    var retail = sources.GetValueOrDefault("retail");

    var replaced = new List<int>(); var appended = new List<int>(); int gaps = 0;
    foreach (var d in approved)
    {
        if (!byRgb.TryGetValue(d.Pal, out int palIdx))
        {
            palIdx = blocks.Count;
            blocks.Add(Codec.MakeBlock(template, Convert.FromBase64String(d.Pal)));
            byRgb[d.Pal] = palIdx;
        }
        var frame = d.ToFrame();
        if (d.Id < frames.Count)
        {
            frames[d.Id] = frame;
            recs[d.Id] = recs[d.Id] with { Palette = (uint)palIdx };
            replaced.Add(d.Id);
            continue;
        }
        while (frames.Count < d.Id)
        {
            frames.Add(Frame.Blank());
            recs.Add(new TblRec((uint)recs.Count, 0, 0f, -1, 0));
            gaps++;
        }
        var src = retail is not null && d.Id < retail.Count ? retail.Recs[d.Id] : new TblRec((uint)d.Id, 0, 0f, -1, 0);
        frames.Add(frame);
        recs.Add(new TblRec((uint)d.Id, (uint)palIdx, src.Alpha, src.Light, src.Flag));
        appended.Add(d.Id);
    }

    var epf = Codec.EpfBuild(art.Epf, frames);
    var pal = Codec.PalBuild(blocks);
    var tbl = Codec.TblBuild(art.TblText, recs);
    var outDir = Path.Combine(exportDir, req.Client);
    Directory.CreateDirectory(outDir);
    File.WriteAllBytes(Path.Combine(outDir, "Item.epf"), epf);
    File.WriteAllBytes(Path.Combine(outDir, "Item.pal"), pal);
    File.WriteAllBytes(Path.Combine(outDir, "Item.tbl"), tbl);
    var ents = Codec.DatSet(Codec.DatSet(Codec.DatSet(art.Entries, "ITEM.EPF", epf), "ITEM.PAL", pal), "ITEM.TBL", tbl);
    var datName = Path.GetFileName(art.DatPath);
    File.WriteAllBytes(Path.Combine(outDir, datName), Codec.WriteDat(ents));
    var manifest = new
    {
        client = art.Name, source = art.DatPath, exported = DateTime.UtcNow.ToString("o"),
        framesBefore = art.Count, framesAfter = frames.Count, replaced, appended, gaps,
        paletteBlocks = new { before = art.Blocks.Count, after = blocks.Count },
        icons = approved.Select(d => new { d.Id, d.W, d.H, names = names.GetValueOrDefault(d.Id, []), d.Source, d.Note }),
        install = $"back up the client's {datName}, copy this one over it; the server's icon bound must allow ids up to {frames.Count - 1}",
    };
    File.WriteAllText(Path.Combine(outDir, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    return Results.Ok(new
    {
        dir = SavedRel(outDir), dat = datName, framesBefore = art.Count, framesAfter = frames.Count,
        replaced = replaced.Count, appended = appended.Count, gaps, paletteBlocks = blocks.Count, ids = approved.Select(d => d.Id),
    });
});

app.MapPost("/api/debug/shot", async (HttpRequest req) =>
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var s = Encoding.ASCII.GetString(ms.ToArray());
    int comma = s.IndexOf(',');
    var path = Path.Combine(Path.GetTempPath(), "iconstudio-shot.png");
    File.WriteAllBytes(path, Convert.FromBase64String(comma >= 0 ? s[(comma + 1)..] : s));
    return Results.Text(path);
});

Console.WriteLine($"icon studio at {url}");

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
    if (AppWindow.Run(url))
    {
        await app.StopAsync();
        return 0;
    }
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
    catch { }
}

static int Fail(string msg)
{
    Console.WriteLine(msg);
    try { System.Windows.Forms.MessageBox.Show(msg, "NexusTK Icon Studio", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error); }
    catch { }
    return 1;
}

static string? FindRepoRoot()
{
    var env = Environment.GetEnvironmentVariable("P1998_REPO");
    if (env is not null && File.Exists(Path.Combine(env, "game-data", "Items.csv"))) return env;
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "game-data", "Items.csv")))
                return d.FullName;
    return null;
}

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
    return 0;
}

// Items.csv: ItmIcon -> the identifiers of every item drawn with that frame. Quoted fields are
// honoured (descriptions carry commas and escaped quotes); only the two columns matter here.
static Dictionary<int, List<string>> LoadIconNames(string path)
{
    var outp = new Dictionary<int, List<string>>();
    if (!File.Exists(path)) return outp;
    using var reader = new StreamReader(path);
    var header = SplitCsv(reader.ReadLine() ?? "");
    int iIcon = header.IndexOf("ItmIcon"), iIdent = header.IndexOf("ItmIdentifier");
    if (iIcon < 0 || iIdent < 0) return outp;
    while (reader.ReadLine() is { } line)
    {
        if (line.Length == 0 || line.StartsWith('#')) continue;
        var cols = SplitCsv(line);
        if (cols.Count <= Math.Max(iIcon, iIdent) || !int.TryParse(cols[iIcon], out int icon)) continue;
        if (!outp.TryGetValue(icon, out var list)) outp[icon] = list = [];
        list.Add(cols[iIdent]);
    }
    return outp;
}

static List<string> SplitCsv(string line)
{
    var cols = new List<string>();
    var sb = new StringBuilder();
    bool q = false;
    for (int i = 0; i < line.Length; i++)
    {
        char c = line[i];
        if (q)
        {
            if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
            else if (c == '"') q = false;
            else sb.Append(c);
        }
        else if (c == '"') q = true;
        else if (c == ',') { cols.Add(sb.ToString()); sb.Clear(); }
        else sb.Append(c);
    }
    cols.Add(sb.ToString());
    return cols;
}

/// <summary>Thumbnail atlas: every frame of a source centred in a fixed slot (larger frames are
/// subsampled by an integer factor), so the list can show 5,879 icons from one image.</summary>
static class Thumbs
{
    public const int Slot = 48, Cols = 64;

    public static byte[] Atlas(ItemArt art)
    {
        int rows = (art.Count + Cols - 1) / Cols;
        int aw = Cols * Slot, ah = rows * Slot;
        var rgba = new byte[aw * ah * 4];
        for (int id = 0; id < art.Count; id++)
        {
            if (art.Frames[id] is not { } f) continue;
            var pal = art.PaletteRgb(id);
            int factor = Math.Max(1, (Math.Max(f.W, f.H) + Slot - 1) / Slot);
            int tw = f.W / factor, th = f.H / factor;
            int ox = id % Cols * Slot + (Slot - tw) / 2, oy = id / Cols * Slot + (Slot - th) / 2;
            for (int y = 0; y < th; y++)
                for (int x = 0; x < tw; x++)
                {
                    int si = y * factor * f.W + x * factor;
                    if (!f.Alpha[si]) continue;
                    int di = ((oy + y) * aw + ox + x) * 4;
                    int c = f.Idx[si] * 3;
                    rgba[di] = pal[c]; rgba[di + 1] = pal[c + 1]; rgba[di + 2] = pal[c + 2]; rgba[di + 3] = 255;
                }
        }
        return Codec.EncodePng(aw, ah, rgba);
    }
}

/// <summary>A frame on the wire: palette (768 B), indices and alpha (w*h bytes each), all base64.</summary>
record FrameDto(int W, int H, int Top, int Left, string Pal, string Idx, string Alpha, int PalIndex)
{
    public static FrameDto From(Frame f, byte[] rgb, int palIndex) =>
        new(f.W, f.H, f.Top, f.Left, Convert.ToBase64String(rgb), Convert.ToBase64String(f.Idx),
            Convert.ToBase64String(f.Alpha.Select(a => a ? (byte)1 : (byte)0).ToArray()), palIndex);
}

/// <summary>A saved draft: the same payload as a FrameDto plus curation state. Stored as JSON.</summary>
record Draft(int Id, int W, int H, int Top, int Left, string Pal, string Idx, string Alpha,
             bool Approved, string? Source, string? Note, string? Updated)
{
    public bool Valid()
    {
        if (W < 1 || H < 1 || W > 128 || H > 128) return false;
        try
        {
            return Convert.FromBase64String(Pal).Length == 768
                && Convert.FromBase64String(Idx).Length == W * H
                && Convert.FromBase64String(Alpha).Length == W * H;
        }
        catch { return false; }
    }

    public Frame ToFrame()
    {
        var idx = Convert.FromBase64String(Idx);
        var alpha = Convert.FromBase64String(Alpha).Select(b => b != 0).ToArray();
        for (int i = 0; i < idx.Length; i++) if (!alpha[i]) idx[i] = 0;
        return new Frame((short)Top, (short)Left, (short)(Top + H), (short)(Left + W), idx, alpha, null);
    }

    public byte[] Png()
    {
        var f = ToFrame();
        return Codec.EncodePng(W, H, Codec.ToRgba(f, Convert.FromBase64String(Pal)));
    }
}

record CandidateReq(string Method, int? W, int? H);
record ApproveReq(bool Approved);
record ExportReq(string Client);

sealed class HeartbeatState { public volatile bool Seen; public long Last; }

sealed class TeeWriter(TextWriter a, TextWriter b) : TextWriter
{
    public override Encoding Encoding => a.Encoding;
    public override void Write(char value) { a.Write(value); b.Write(value); }
    public override void Write(string? value) { a.Write(value); b.Write(value); }
    public override void WriteLine(string? value) { a.WriteLine(value); b.WriteLine(value); }
}
