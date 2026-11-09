using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shared;

namespace Tools;

/// <summary>
/// The synthetic content profile: a small content root under <c>test-content/synthetic/</c>, every byte of it
/// invented, that a server can be pointed at instead of <c>game-data/</c> (<c>P1998_GAME_DATA</c>,
/// <c>P1998_MAPS</c>, <c>P1998_SOBJ</c>). It exists so a client test can rely on known terrain, walls, warps and
/// an entity without carrying any retail content: two maps from an id band no game-data file and no server
/// literal uses, a band of blocked ground, an object for every direction-flag value, a flag-0 decor object, an
/// object the table marks solid but the server lets through, a warp each way and two NPCs that never move: one
/// with no script, whose click opens nothing, and one whose scripted dialog opens a menu, an input box and text
/// boxes, so a client test can send every kind of dialog reply.
///
/// <para><b>Nothing reads it by default.</b> The server opens only <c>P1998_GAME_DATA</c> (default
/// <c>&lt;root&gt;/game-data</c>), the release bundle ships <c>game-data/</c> only, and this class is reached
/// only through the <c>synthetic-profile</c> subcommand and the tests.</para>
///
/// <para><b>Deterministic.</b> No clock, no random, no culture: text is UTF-8 without a BOM with LF line ends,
/// numbers are written invariant, binaries are little-endian. <c>Tests/SyntheticProfileTests.cs</c> regenerates
/// into a temp folder and compares every byte with the committed copy, so a change here that is not
/// regenerated fails the suite.</para>
///
/// <para><b>Formats</b> are the ones the server reads, and the layout is checked against them before a byte is
/// written (<see cref="Validate"/>): a <c>.map</c> is headerless, row-major, four bytes a cell (ground u16,
/// object u16), its size taken from <c>map_index.csv</c> (<c>Server/MapData.cs</c> <c>Load</c>); a ground word
/// below 0x4000 is walkable and one at or above it is blocked (<c>MapData.Solid</c>); an object's flag bits
/// UP=1 DOWN=2 RIGHT=4 LEFT=8 refuse the step INTO its cell heading north, south, east or west
/// (<c>Server/ObjectFlags.cs</c> <c>Blocks</c>).</para>
/// </summary>
public static class SyntheticProfile
{
    /// <summary>The one file in the profile folder this generator does not write.</summary>
    public const string HandWrittenFile = "README.md";

    /// <summary>How to regenerate; named in every generated text file.</summary>
    public const string Command = "dotnet run --project Tools -- synthetic-profile";

    /// <summary>The committed profile, relative to the repository root.</summary>
    public const string RelativeDirectory = "test-content/synthetic";

    /// <summary>The committed profile's folder: <c>&lt;root&gt;/test-content/synthetic</c>.</summary>
    public static string DefaultDirectory() => Path.Combine(RepoPaths.Root(), "test-content", "synthetic");

    // ---- the layout ------------------------------------------------------------------------------------------

    private readonly record struct Tile(int X, int Y);

    private sealed record MapSpec(ushort Id, string Name, int Width, int Height, ushort Floor, Tile? Start);

    /// <summary>An object id, its flag in <c>SObj.tbl</c> (<paramref name="FileFlag"/>), the flag the server
    /// applies after <c>ObjectFlagOverrides.csv</c> (<paramref name="ServerFlag"/>), and the one cell it sits
    /// on.</summary>
    private sealed record ObjectSpec(ushort Id, byte FileFlag, byte ServerFlag, string Role, ushort Map, Tile Cell);

    private sealed record WarpSpec(ushort FromMap, Tile From, ushort ToMap, Tile To);

    /// <summary>One row of blocked ground from <paramref name="FromX"/> to <paramref name="ToX"/>, except one
    /// walkable gap cell that holds <paramref name="GapObject"/>.</summary>
    private sealed record WallBandSpec(ushort Map, int Y, int FromX, int ToX, ushort Ground, int GapX, ushort GapObject);

    /// <summary>One <c>NPCs.csv</c> row. An NPC with a <paramref name="Dialog"/> gets a script in
    /// <c>npc_dialog.lua</c> keyed by its <paramref name="Identifier"/> and a <c>dialog</c> block in the manifest.
    /// One without has no click menu entries either, so a click on it opens nothing (Server/Session.Dialog.cs
    /// <c>RunNpcAsync</c>: the greeting there is only for an NPC that answers no spoken question, and every NPC
    /// answers some).</summary>
    private sealed record NpcSpec(int NpcId, string Identifier, string Description, ushort Map, Tile At, ushort Look,
                                  DialogSpec? Dialog = null);

    /// <summary>Where a menu entry leads: the input box, the notice's text box, or the end of the conversation.</summary>
    private enum Leads { Input, Notice, End }

    private sealed record MenuEntrySpec(string Text, Leads To);

    /// <summary>An NPC's click dialog, every word of it held here once and written into both
    /// <c>npc_dialog.lua</c> and <c>manifest.json</c>, so the script and its description cannot drift apart. A menu
    /// of <paramref name="Entries"/> (picked 1-based) under <paramref name="MenuPrompt"/>. The entry that leads to
    /// the input box opens one under <paramref name="InputPrompt"/>; a submit is answered with
    /// <paramref name="TypedBefore"/>, the submitted text's length, <paramref name="TypedBetween"/> and the text, and
    /// any other reply with <paramref name="NotSubmitted"/>. The entry that leads to the notice answers with
    /// <paramref name="Notice"/>. Every answer is a text box with a next arrow, whose next ends the conversation;
    /// an entry that leads to the end, and any other reply to the menu, ends it at once.</summary>
    private sealed record DialogSpec(string MenuPrompt, MenuEntrySpec[] Entries, string InputPrompt, string TypedBefore,
                                     string TypedBetween, string NotSubmitted, string Notice)
    {
        /// <summary>The submit's answer as the manifest states it, with the two parts the server fills in written
        /// <c>{N}</c> and <c>{TEXT}</c>: braces, so that substituting them never depends on the order, whatever the
        /// submitted text holds.</summary>
        public string SubmitPattern => TypedBefore + "{N}" + TypedBetween + "{TEXT}";

        /// <summary>The 1-based picks of the entries that lead to <paramref name="to"/>.</summary>
        public IEnumerable<int> Picks(Leads to) =>
            Entries.Select((e, i) => (e.To, Pick: i + 1)).Where(e => e.To == to).Select(e => e.Pick);

        /// <summary>Every string the dialog shows or writes into a reply.</summary>
        public IEnumerable<string> Texts =>
            new[] { MenuPrompt, InputPrompt, TypedBefore, TypedBetween, NotSubmitted, Notice }
                .Concat(Entries.Select(e => e.Text));
    }

    // Map ids from 7201-7643, a run that no game-data map list and no server literal uses, away from the ids the
    // code treats specially (the 4711-4718 tutorial, 410-419 warp exclusion, 59000-65000 instances).
    // Tests/SyntheticProfileTests.cs keeps them out of game-data's map lists.
    private const ushort StartMapId = 7400;
    private const ushort AnnexMapId = 7401;

    // Ground words. The top two bits are the pass bits, so below 0x4000 is walkable; 0 draws nothing on either
    // client, so the floors start at 1 and differ per map (a cell left over from the other map shows). The wall
    // is a sheet-B word (>= 0xC000), the form every blocked cell in the shipped maps has.
    private const ushort WalkableBelow = 0x4000;
    private const ushort SheetB = 0xC000;
    private const ushort StartFloor = 0x0001;
    private const ushort AnnexFloor = 0x0002;
    private const ushort WallGround = 0xC001;

    private const byte Up = 1, Down = 2, Right = 4, Left = 8;
    private const ushort DecorObject = 16;
    private const ushort GateObject = 17;

    // A fresh server numbers NPCs from 300000 in NPCs.csv order (Server/World.cs, _nextNpcId).
    private const int FirstNpcRuntimeId = 300_000;

    // The 4.95 view is 17 x 15 tiles around the player (Server/Session.Entity.cs), so an NPC within 8 columns
    // and 7 rows of the start tile is in view on arrival, and a start tile 8 or more tiles from every edge keeps
    // the camera centred.
    private const int ViewHalfWidth = 8, ViewHalfHeight = 7, EdgeMargin = 8;

    private static readonly Tile StartTile = new(32, 32);

    private static readonly MapSpec[] Maps =
    {
        new(StartMapId, "Synthetic Start", 64, 64, StartFloor, StartTile),
        new(AnnexMapId, "Synthetic Annex", 40, 32, AnnexFloor, null),
    };

    // Six rows north of the start, in view: blocked ground with one gap, and the gap holds the gate object.
    private static readonly WallBandSpec[] WallBands =
    {
        new(StartMapId, Y: 26, FromX: 16, ToX: 47, Ground: WallGround, GapX: 32, GapObject: GateObject),
    };

    private static readonly ObjectSpec[] Objects = BuildObjects();

    private static ObjectSpec[] BuildObjects()
    {
        var objects = new List<ObjectSpec>();
        // Flags 1-15 as object ids 1-15, in a 5 x 3 grid south-east of the start with one free tile between
        // neighbours, so every object can be entered from all four sides. The grid stays clear of column 32,
        // the straight walk from the start to the outbound warp.
        for (int flag = 1; flag <= 15; flag++)
        {
            int i = flag - 1;
            objects.Add(new((ushort)flag, (byte)flag, (byte)flag, "direction", StartMapId,
                            new Tile(34 + 2 * (i % 5), 36 + 2 * (i / 5))));
        }
        objects.Add(new(DecorObject, 0x00, 0x00, "decor", StartMapId, new Tile(28, 32)));
        // Solid from every side in SObj.tbl, walkable on the server through ObjectFlagOverrides.csv: an entrance
        // that looks blocked to a client predicting from the table's flags.
        objects.Add(new(GateObject, Up | Down | Right | Left, 0x00, "pass-through", StartMapId, new Tile(32, 26)));
        return objects.ToArray();
    }

    // Stepping INTO a source tile warps (before any collision verdict); arriving on a landing tile does not, so
    // each landing sits beside the return source: south from the start map, north to come back.
    private static readonly WarpSpec[] Warps =
    {
        new(StartMapId, new Tile(32, 56), AnnexMapId, new Tile(20, 13)),
        new(AnnexMapId, new Tile(20, 12), StartMapId, new Tile(32, 55)),
    };

    // The clerk's dialog: a menu whose first entry opens an input box and answers what was submitted, whose second
    // answers with a text box, and whose third ends the conversation. All of it invented.
    private static readonly DialogSpec ClerkDialog = new(
        MenuPrompt: "Synthetic Clerk: pick a test.",
        Entries: new MenuEntrySpec[]
        {
            new("Type a line", Leads.Input),
            new("Read the notice", Leads.Notice),
            new("Goodbye", Leads.End),
        },
        InputPrompt: "Type a line for the clerk.",
        TypedBefore: "You typed ",
        TypedBetween: " characters: ",
        NotSubmitted: "Nothing was submitted.",
        Notice: "The notice is blank. It is a synthetic notice.");

    // NpcMoveTime 0: an NPC never paces (Server/World.cs PlaceNpc), and EraFeature is blank, so it always stands. A
    // fresh server numbers them in this order (FirstNpcRuntimeId). The clerk mirrors the sentinel across column 32,
    // which stays clear for the walk from the start tile to the outbound warp.
    private static readonly NpcSpec[] Npcs =
    {
        new(1000, "SyntheticSentinel", "Synthetic Sentinel", StartMapId, new Tile(30, 29), Look: 1),
        new(1001, "SyntheticClerk", "Synthetic Clerk", StartMapId, new Tile(34, 29), Look: 1, ClerkDialog),
    };

    // ---- output ----------------------------------------------------------------------------------------------

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Every generated file as (path relative to the profile folder with '/' separators, bytes), in
    /// ordinal path order. Throws <see cref="InvalidOperationException"/> when the layout breaks a rule the
    /// profile promises (<see cref="Validate"/>).</summary>
    public static IReadOnlyList<(string Path, byte[] Bytes)> Files()
    {
        Validate();
        var files = new List<(string Path, byte[] Bytes)>
        {
            ("map_index.csv", Text(MapIndexCsv())),
            ("Maps.csv", Text(MapsCsv())),
            ("Warps.csv", Text(WarpsCsv())),
            ("NPCs.csv", Text(NpcsCsv())),
            ("ObjectFlagOverrides.csv", Text(OverridesCsv())),
            ("SObj.tbl", ObjectTable()),
            ("spell_verbs.lua", Text(VerbStub("spell_verbs.lua", "spell verbs", "Server/LuaVerbHost.cs"))),
            ("item_verbs.lua", Text(VerbStub("item_verbs.lua", "item verbs", "Server/LuaVerbHost.cs"))),
            ("npc_dialog.lua", Text(NpcDialogScript())),
            ("mob_ai.lua", Text(MobAiStub())),
            ("manifest.json", Manifest()),
        };
        foreach (var map in Maps)
            files.Add(($"maps/TK{Num(map.Id)}.map", MapFile(map)));
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return files;
    }

    /// <summary>Write every generated file under <paramref name="directory"/>, creating it if needed, and return
    /// the relative paths written. Never deletes anything. Refuses (throws) a directory that already holds files
    /// but no synthetic <c>manifest.json</c>, so a mistyped path cannot overwrite <c>game-data/</c>.</summary>
    public static IReadOnlyList<string> Write(string directory)
    {
        var files = Files();
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()
            && !HoldsSyntheticManifest(directory))
            throw new InvalidOperationException(
                $"{directory} is not empty and holds no synthetic manifest.json; refusing to write the profile over it");

        foreach (var (path, bytes) in files)
        {
            string full = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }
        return files.Select(f => f.Path).ToArray();
    }

    /// <summary>Files under <paramref name="directory"/> that are neither generated nor the hand-written
    /// <see cref="HandWrittenFile"/>, as relative '/' paths in ordinal order: a file a layout change left behind.</summary>
    public static IReadOnlyList<string> Unexpected(string directory)
    {
        var known = new HashSet<string>(Files().Select(f => f.Path), StringComparer.Ordinal) { HandWrittenFile };
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(directory, f).Replace('\\', '/'))
            .Where(rel => !known.Contains(rel))
            .OrderBy(rel => rel, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool HoldsSyntheticManifest(string directory)
    {
        string manifest = Path.Combine(directory, "manifest.json");
        return File.Exists(manifest)
            && File.ReadAllText(manifest).Contains("\"profile\": \"synthetic\"", StringComparison.Ordinal);
    }

    // ---- the CSVs and the Lua stubs --------------------------------------------------------------------------

    private static string Banner =>
        $"Synthetic test profile: invented content written by `{Command}`. Edit the generator, not this file.";

    private static string[] MapIndexCsv() =>
        Lines($"# {Banner}",
              "id,name,xs,ys",
              Maps.Select(m => Row(m.Id, m.Name, m.Width, m.Height)));

    // Only the columns the server reads (Server/Content.Csv.cs LoadMapMeta). MapIndoor=1 holds the weather clear
    // (Server/Weather.cs For); the requirement and cap columns are 0, which is no gate for a walk-in warp.
    private static string[] MapsCsv() =>
        Lines($"# {Banner}",
              "# MapIndoor=1 holds the weather clear. Requirement and cap columns 0: no gate on a warp in.",
              "MapId,MapPvP,MapSpells,MapChat,MapRegion,MapIndoor,MapWarpout,MapReqLvl,MapReqPath,MapReqMark," +
              "MapReqVita,MapReqMana,MapLvlMax,MapVitaMax,MapManaMax,MapRejectMsg,MapCanSummon",
              Maps.Select(m => Row(m.Id, 0, 1, 0, -1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, "", 1)));

    private static string[] WarpsCsv() =>
        Lines($"# {Banner}",
              "SourceMapId,SourceX,SourceY,DestinationMapId,DestinationX,DestinationY",
              Warps.Select(w => Row(w.FromMap, w.From.X, w.From.Y, w.ToMap, w.To.X, w.To.Y)));

    // The sixteen columns the server reads (Server/Content.Csv.cs LoadNpcs).
    private static string[] NpcsCsv() =>
        Lines($"# {Banner}",
              "NpcId,NpcIdentifier,NpcDescription,NpcMapId,NpcX,NpcY,NpcLook,NpcLookColor,NpcIsChar," +
              "NpcIsRepairNpc,NpcIsShopNpc,NpcIsBankNpc,NpcReturnDistance,NpcMoveTime,Enabled,EraFeature",
              Npcs.Select(n => Row(n.NpcId, n.Identifier, n.Description, n.Map, n.At.X, n.At.Y, n.Look,
                                   0, 0, 0, 0, 0, 0, 0, 1, "")));

    private static string[] OverridesCsv() =>
        Lines($"# {Banner}",
              "# No header line: the server supplies Obj,Flag,Note.",
              Objects.Where(o => o.ServerFlag != o.FileFlag)
                     .Select(o => Row(o.Id, $"0x{o.ServerFlag.ToString("X2", CultureInfo.InvariantCulture)}",
                                      $"Synthetic gate: 0x{o.FileFlag.ToString("X2", CultureInfo.InvariantCulture)} " +
                                      "in SObj.tbl and walkable on the server")));

    private static string[] VerbStub(string file, string what, string loader) =>
        Lines($"-- {file}: {Banner}",
              $"-- No {what}. The loader ({loader}) accepts the file once it defines a global `verbs` table.",
              new[] { "verbs = {}" });

    /// <summary><c>npc_dialog.lua</c>: a script for each NPC that has a <see cref="DialogSpec"/>, keyed by its
    /// identifier. <c>__make_ctx</c> defines only the three requests these scripts make, in the form
    /// <c>game-data/npc_dialog.lua</c> gives them; the loader needs it and the global <c>npcs</c> table.</summary>
    private static string[] NpcDialogScript()
    {
        var lines = new List<string>(Lines(
            $"-- npc_dialog.lua: {Banner}",
            "-- The loader (Server/NpcScript.cs) runs npcs.<NpcIdentifier>(ctx) as a coroutine when that NPC is clicked.",
            "-- Each ctx method yields one request, and the server resumes the coroutine with the player's 0x3A reply:",
            "--   ctx:say(text)               a text box with a next arrow; returns after the next",
            "--   ctx:menu(prompt, entries)   a menu; returns the 1-based pick, or 0 for a reply that is not a pick",
            "--   ctx:input(prompt)           an input box; returns the submitted text, or nil for any other reply",
            "-- manifest.json describes each dialog under its NPC's entity; both are written from the same strings.",
            "",
            "function __make_ctx()",
            "  local ctx = {}",
            "  function ctx:say(...)                 return coroutine.yield({op=\"say\", pages={...}}) end",
            "  function ctx:menu(prompt, options)    return coroutine.yield({op=\"menu\", prompt=prompt, options=options}) end",
            "  function ctx:input(prompt)            return coroutine.yield({op=\"input\", prompt=prompt}) end",
            "  return ctx",
            "end",
            "",
            "npcs = {}"));

        foreach (var n in Npcs.Where(n => n.Dialog is not null))
        {
            var d = n.Dialog!;
            lines.Add("");
            lines.Add(Invariant($"-- {n.Description} (NPC {n.NpcId}). Any reply to the menu that leads nowhere ends the conversation."));
            lines.Add($"function npcs.{n.Identifier}(ctx)");
            lines.Add($"  local pick = ctx:menu({Lua(d.MenuPrompt)}, {{{string.Join(", ", d.Entries.Select(e => Lua(e.Text)))}}})");
            string branch = "if";
            for (int i = 0; i < d.Entries.Length; i++)
            {
                if (d.Entries[i].To == Leads.End) continue;
                lines.Add(Invariant($"  {branch} pick == {i + 1} then"));
                branch = "elseif";
                if (d.Entries[i].To == Leads.Input)
                {
                    lines.Add($"    local typed = ctx:input({Lua(d.InputPrompt)})");
                    lines.Add("    if typed then");
                    lines.Add($"      ctx:say({Lua(d.TypedBefore)} .. #typed .. {Lua(d.TypedBetween)} .. typed)");
                    lines.Add("    else");
                    lines.Add($"      ctx:say({Lua(d.NotSubmitted)})");
                    lines.Add("    end");
                }
                else lines.Add($"    ctx:say({Lua(d.Notice)})");
            }
            if (branch == "elseif") lines.Add("  end");
            lines.Add("end");
        }
        return lines.ToArray();
    }

    /// <summary>A Lua string literal. <see cref="Validate"/> holds every dialog text to printable ASCII, so only
    /// the backslash and the quote need escaping.</summary>
    private static string Lua(string text) =>
        "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string[] MobAiStub() =>
        Lines($"-- mob_ai.lua: {Banner}",
              "-- No creature hooks. The loader (Server/MobScript.cs) accepts the file once it defines a global `mobs` table.",
              new[] { "mobs = {}" });

    // ---- the binaries ----------------------------------------------------------------------------------------

    private static (ushort[] Ground, ushort[] Objects) Cells(MapSpec map)
    {
        var ground = new ushort[map.Width * map.Height];
        var objects = new ushort[ground.Length];
        Array.Fill(ground, map.Floor);
        foreach (var band in WallBands.Where(b => b.Map == map.Id))
            for (int x = band.FromX; x <= band.ToX; x++)
                if (x != band.GapX) ground[band.Y * map.Width + x] = band.Ground;
        foreach (var o in Objects.Where(o => o.Map == map.Id))
            objects[o.Cell.Y * map.Width + o.Cell.X] = o.Id;
        return (ground, objects);
    }

    /// <summary>Row-major, cell index <c>y * width + x</c>, each cell ground u16 then object u16, little-endian,
    /// no header: exactly <c>width * height * 4</c> bytes.</summary>
    private static byte[] MapFile(MapSpec map)
    {
        var (ground, objects) = Cells(map);
        var bytes = new byte[ground.Length * 4];
        for (int i = 0; i < ground.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 4), ground[i]);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 4 + 2), objects[i]);
        }
        return bytes;
    }

    /// <summary><c>SObj.tbl</c> in the layout <c>Server/ObjectFlags.cs</c> documents. The bytes: u32 count (17),
    /// then u8 0 (object 0's flag, never read), then 17 records, each a u8 frame count (1), one u16 frame id, the
    /// separator FF FF FF FF 00 and a u8 flag.
    /// <para>A record is NOT one object's frames followed by that object's flag (the misreading
    /// <c>ObjectFlags.cs</c> calls "the trap"). A record's frames belong to one object and its trailing flag to
    /// the NEXT: record r (1-based) carries object r-1's frames and object r's flag. So the flags of objects 1..17
    /// land where the server reads them, and the frame ids, which are placeholders (record r writes frame id r),
    /// give object k frame k+1 and leave object 17 with no frame record.</para>
    /// <para>The server reads only the count and the flags, never these frames. The manifest, not this table,
    /// says what each object is.</para></summary>
    private static byte[] ObjectTable()
    {
        int count = Objects.Max(o => o.Id);
        var bytes = new List<byte>();
        var u32 = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)count);
        bytes.AddRange(u32);
        bytes.Add(0x00);
        for (int id = 1; id <= count; id++)
        {
            var o = Objects.Single(o => o.Id == id);
            bytes.Add(1);                                                // object id-1's frame count
            bytes.Add((byte)(id & 0xFF));                                // ...and its one frame: placeholder id
            bytes.Add((byte)(id >> 8));
            bytes.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x00 });
            bytes.Add(o.FileFlag);                                       // object id's flag
        }
        return bytes.ToArray();
    }

    // ---- the manifest ----------------------------------------------------------------------------------------

    private static byte[] Manifest()
    {
        var buffer = new MemoryStream();
        var options = new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using (var w = new Utf8JsonWriter(buffer, options))
        {
            w.WriteStartObject();
            w.WriteString("profile", "synthetic");
            w.WriteNumber("manifestVersion", 1);
            w.WriteString("generatedBy", Command);
            w.WriteString("about", "Invented test content. No map cell, object id or flag, name or tile here comes " +
                                   "from game-data or a client install.");

            var startMap = Maps.Single(m => m.Start is not null);
            var startTile = startMap.Start!.Value;
            w.WriteStartObject("start");
            WriteTile(w, startMap.Id, startTile);
            w.WriteEndObject();

            w.WriteStartArray("maps");
            foreach (var m in Maps)
            {
                w.WriteStartObject();
                w.WriteNumber("id", m.Id);
                w.WriteString("name", m.Name);
                w.WriteNumber("width", m.Width);
                w.WriteNumber("height", m.Height);
                w.WriteString("file", $"maps/TK{Num(m.Id)}.map");
                w.WriteNumber("floorGround", m.Floor);
                w.WriteBoolean("indoor", true);
                if (m.Start is { } s)
                {
                    w.WriteStartObject("start");
                    w.WriteNumber("x", s.X);
                    w.WriteNumber("y", s.Y);
                    w.WriteEndObject();
                }
                else w.WriteNull("start");
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartObject("ground");
            w.WriteNumber("walkableBelow", WalkableBelow);
            w.WriteString("rule", "A ground word below 0x4000 (16384) is walkable; at or above it the cell is blocked. " +
                                  "0 draws nothing.");
            w.WriteEndObject();

            w.WriteStartArray("wallBands");
            foreach (var b in WallBands)
            {
                w.WriteStartObject();
                w.WriteNumber("map", b.Map);
                w.WriteNumber("ground", b.Ground);
                w.WriteNumber("y", b.Y);
                w.WriteNumber("fromX", b.FromX);
                w.WriteNumber("toX", b.ToX);
                w.WriteStartArray("gaps");
                w.WriteStartObject();
                w.WriteNumber("x", b.GapX);
                w.WriteNumber("object", b.GapObject);
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartObject("objectTable");
            w.WriteString("file", "SObj.tbl");
            w.WriteNumber("count", Objects.Max(o => o.Id));
            w.WriteString("overrides", "ObjectFlagOverrides.csv");
            w.WriteNumber("up", Up);
            w.WriteNumber("down", Down);
            w.WriteNumber("right", Right);
            w.WriteNumber("left", Left);
            w.WriteString("rule", "A set bit refuses a step into the object's cell heading that way: north by up, " +
                                  "south by down, east by right, west by left. Only the destination cell's object " +
                                  "is tested. serverFlag is fileFlag after ObjectFlagOverrides.csv.");
            w.WriteEndObject();

            w.WriteStartArray("objects");
            foreach (var o in Objects.OrderBy(o => o.Id))
            {
                w.WriteStartObject();
                w.WriteNumber("id", o.Id);
                w.WriteNumber("fileFlag", o.FileFlag);
                w.WriteNumber("serverFlag", o.ServerFlag);
                w.WriteString("role", o.Role);
                w.WriteStartArray("cells");
                w.WriteStartObject();
                WriteTile(w, o.Map, o.Cell);
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("warps");
            foreach (var warp in Warps)
            {
                w.WriteStartObject();
                w.WriteStartObject("from");
                WriteTile(w, warp.FromMap, warp.From);
                w.WriteEndObject();
                w.WriteStartObject("to");
                WriteTile(w, warp.ToMap, warp.To);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteString("warpRule", "Stepping into a from tile moves the player to its to tile, before any " +
                                      "collision test. Arriving on a tile never fires a warp.");

            w.WriteStartArray("entities");
            for (int i = 0; i < Npcs.Length; i++)
            {
                var n = Npcs[i];
                w.WriteStartObject();
                w.WriteString("kind", "npc");
                w.WriteNumber("npcId", n.NpcId);
                w.WriteNumber("runtimeId", FirstNpcRuntimeId + i);
                w.WriteString("identifier", n.Identifier);
                w.WriteString("name", n.Description);
                WriteTile(w, n.Map, n.At);
                w.WriteNumber("look", n.Look);
                w.WriteBoolean("stationary", true);
                if (n.Dialog is { } dialog) WriteDialog(w, dialog);
                else w.WriteNull("dialog");
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteString("runtimeIdRule", "A fresh server numbers NPCs from 300000 in NPCs.csv order.");

            w.WriteStartObject("environment");
            w.WriteString("pathsRelativeTo", "the repository root. The server uses a path as given, so start it " +
                                             "from the root or make the paths absolute.");
            w.WriteStartArray("variables");
            WriteVariable(w, "P1998_GAME_DATA", RelativeDirectory, "The content root: every CSV and Lua file.");
            WriteVariable(w, "P1998_MAPS", RelativeDirectory + "/maps", "The .map files.");
            WriteVariable(w, "P1998_SOBJ", RelativeDirectory + "/SObj.tbl", "The object flag table.");
            WriteVariable(w, "P1998_MAPS_INSTALL_FALLBACK", "0",
                          "A missing map is reported missing, never read from a client install.");
            WriteVariable(w, "P1998_STATE", null, "A new, empty directory for every run.");
            WriteVariable(w, "P1998_START_POINT",
                          Invariant($"{startMap.Id},{startTile.X},{startTile.Y}"),
                          "New characters start on the start tile. Needs the start-point setting; a server " +
                          "without it ignores the variable and starts new characters on map 4711, which this " +
                          "profile does not have.");
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteEndObject();
        }
        // Utf8JsonWriter ends indented lines with Environment.NewLine on .NET 8; the committed file is LF everywhere.
        string json = Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal);
        return Utf8NoBom.GetBytes(json + "\n");
    }

    private static void WriteTile(Utf8JsonWriter w, ushort map, Tile tile)
    {
        w.WriteNumber("map", map);
        w.WriteNumber("x", tile.X);
        w.WriteNumber("y", tile.Y);
    }

    /// <summary>An NPC's <c>dialog</c> block: what each box says, where each pick leads, the rule for the submit's
    /// answer, and which replies end the conversation.</summary>
    private static void WriteDialog(Utf8JsonWriter w, DialogSpec d)
    {
        int last = d.Entries.Length;
        w.WriteStartObject("dialog");
        w.WriteString("script", "npc_dialog.lua");

        w.WriteStartObject("menu");
        w.WriteString("prompt", d.MenuPrompt);
        w.WriteStartArray("entries");
        for (int i = 0; i < d.Entries.Length; i++)
        {
            w.WriteStartObject();
            w.WriteNumber("pick", i + 1);
            w.WriteString("text", d.Entries[i].Text);
            w.WriteString("leadsTo", d.Entries[i].To.ToString().ToLowerInvariant());
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();

        w.WriteStartObject("input");
        w.WriteString("prompt", d.InputPrompt);
        w.WriteString("submitReply", d.SubmitPattern);
        w.WriteString("submitRule", "A submit (0x3A kind 4, step 2) is answered with a text box reading submitReply, " +
                                    "where {TEXT} is the text the server read and {N} is its length in bytes, in " +
                                    $"decimal. An empty submit gives \"{d.TypedBefore}0{d.TypedBetween}\".");
        w.WriteString("otherReply", d.NotSubmitted);
        w.WriteString("otherRule", "Any other reply to the input box, such as its Previous (0x3A kind 4, step 0), is " +
                                   "answered with a text box reading otherReply.");
        w.WriteEndObject();

        w.WriteString("notice", d.Notice);

        w.WriteStartArray("endingPicks");
        foreach (int pick in d.Picks(Leads.End)) w.WriteNumberValue(pick);
        w.WriteEndArray();
        w.WriteString("endRule", Invariant(
            $"A pick in endingPicks, a pick outside 1-{last}, or a reply to the menu that is not a pick ends the ") +
            "conversation at once: the server sends nothing more. Every other pick ends it after the text box it " +
            "leads to, whose next arrow ends the conversation.");
        w.WriteEndObject();
    }

    private static void WriteVariable(Utf8JsonWriter w, string name, string? value, string note)
    {
        w.WriteStartObject();
        w.WriteString("name", name);
        if (value is null) w.WriteNull("value");
        else w.WriteString("value", value);
        w.WriteString("note", note);
        w.WriteEndObject();
    }

    // ---- the rules the layout promises -----------------------------------------------------------------------

    /// <summary>Throw unless the layout keeps every promise the README and the manifest make. Runs before any
    /// file is produced, so a layout edit that breaks one fails the generator and the regenerate test.</summary>
    private static void Validate()
    {
        static void Require(bool ok, string rule)
        {
            if (!ok) throw new InvalidOperationException($"synthetic profile layout: {rule}");
        }

        Require(Maps.Select(m => m.Id).Distinct().Count() == Maps.Length, "map ids are unique");
        Require(Maps.Count(m => m.Start is not null) == 1, "exactly one map has the start tile");
        Require(Maps.Select(m => (m.Width, m.Height)).Distinct().Count() == Maps.Length, "the maps differ in size");
        foreach (var m in Maps)
            Require(m.Floor is > 0 and < WalkableBelow, $"map {m.Id}'s floor is a drawn, walkable ground word");
        Require(Maps.Select(m => m.Floor).Distinct().Count() == Maps.Length, "each map has its own floor word");
        foreach (var b in WallBands)
            Require(b.Ground >= SheetB, "the wall band is a blocked sheet-B ground word");

        var maps = Maps.ToDictionary(m => m.Id);
        var cells = Maps.ToDictionary(m => m.Id, Cells);
        var warpSources = Warps.Select(w => (w.FromMap, w.From)).ToHashSet();
        var npcTiles = Npcs.Select(n => (n.Map, n.At)).ToHashSet();

        bool InBounds(ushort map, Tile t) => t.X >= 0 && t.Y >= 0 && t.X < maps[map].Width && t.Y < maps[map].Height;
        bool Walkable(ushort map, Tile t) => InBounds(map, t) && cells[map].Ground[t.Y * maps[map].Width + t.X] < WalkableBelow;
        ushort ObjectAt(ushort map, Tile t) => cells[map].Objects[t.Y * maps[map].Width + t.X];
        // Walkable floor with no object, no warp and no NPC on it.
        bool Free(ushort map, Tile t) =>
            Walkable(map, t) && ObjectAt(map, t) == 0 && !warpSources.Contains((map, t)) && !npcTiles.Contains((map, t));
        IEnumerable<Tile> Sides(Tile t) =>
            new[] { t with { Y = t.Y - 1 }, t with { Y = t.Y + 1 }, t with { X = t.X - 1 }, t with { X = t.X + 1 } };

        var start = Maps.Single(m => m.Start is not null);
        var s = start.Start!.Value;
        Require(s.X >= EdgeMargin && s.Y >= EdgeMargin
                && start.Width - 1 - s.X >= EdgeMargin && start.Height - 1 - s.Y >= EdgeMargin,
                "the start tile is at least 8 tiles from every edge");
        Require(Free(start.Id, s), "the start tile is walkable and empty");

        Require(Npcs.Select(n => n.NpcId).Distinct().Count() == Npcs.Length, "NPC ids are unique");
        Require(Npcs.Select(n => n.Identifier).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Npcs.Length,
                "NPC identifiers are unique");
        // The server would not refuse two NPCs on one tile: it moves the later one off it (World.PlaceNpc,
        // FreeSpawnTile), so the manifest's tile and the server's would disagree.
        Require(Npcs.Select(n => (n.Map, n.At)).Distinct().Count() == Npcs.Length, "no two NPCs share a tile");
        foreach (var n in Npcs)
        {
            Require(maps.ContainsKey(n.Map) && Walkable(n.Map, n.At) && ObjectAt(n.Map, n.At) == 0
                    && !warpSources.Contains((n.Map, n.At)), $"NPC {n.NpcId} stands on walkable, empty floor");
            Require(n.Map == start.Id && Math.Abs(n.At.X - s.X) <= ViewHalfWidth && Math.Abs(n.At.Y - s.Y) <= ViewHalfHeight,
                    $"NPC {n.NpcId} is in view of the start tile");
        }

        foreach (var n in Npcs.Where(n => n.Dialog is not null))
        {
            var d = n.Dialog!;
            Require(LuaName.IsMatch(n.Identifier), $"NPC {n.NpcId}'s identifier is a Lua name, so npcs.{n.Identifier} keys its script");
            // The server writes dialog text as ASCII (Server/Session.Dialog.cs), so anything else would arrive as '?'.
            Require(d.Texts.All(t => t.Length > 0 && t.All(c => c is >= ' ' and <= '~')),
                    $"NPC {n.NpcId}'s dialog text is printable ASCII");
            // A menu frame carries the entry count and each entry's length in one byte (SendNpcMenuP).
            Require(d.Entries.Length is > 0 and <= byte.MaxValue && d.Entries.All(e => e.Text.Length <= byte.MaxValue),
                    $"NPC {n.NpcId}'s menu fits the menu frame's one-byte count and lengths");
            Require(d.Picks(Leads.Input).Count() == 1 && d.Picks(Leads.Notice).Count() == 1 && d.Picks(Leads.End).Any(),
                    $"NPC {n.NpcId}'s menu has one entry to the input box, one to the notice and at least one that ends");
        }
        // Read back from the two generated files, so a writer that drops or renames one side fails here.
        var described = ManifestDialogKeys(Manifest());
        var scripted = LuaScriptKeys(NpcDialogScript());
        Require(described.SetEquals(scripted),
                "every NPC the manifest gives a dialog has a script in npc_dialog.lua, and every script is such an " +
                $"NPC's (manifest: {string.Join(", ", described.Order())}; npc_dialog.lua: {string.Join(", ", scripted.Order())})");

        Require(Objects.Select(o => o.Id).Distinct().Count() == Objects.Length, "object ids are unique");
        Require(Objects.Select(o => (int)o.Id).Order().SequenceEqual(Enumerable.Range(1, Objects.Length)),
                "object ids run 1..count with no hole, so SObj.tbl has a record for each");
        Require(Objects.Where(o => o.Role == "direction").Select(o => (int)o.FileFlag).Order()
                       .SequenceEqual(Enumerable.Range(1, 15)), "one direction object for each flag value 1-15");
        Require(Objects.Count(o => o.Role == "decor" && o.FileFlag == 0 && o.ServerFlag == 0) == 1, "one flag-0 decor object");
        Require(Objects.Count(o => o.Role == "pass-through" && o.FileFlag == 0x0F && o.ServerFlag == 0) == 1,
                "one object solid in SObj.tbl that the server lets through");
        foreach (var o in Objects)
        {
            Require(maps.ContainsKey(o.Map) && Walkable(o.Map, o.Cell) && !warpSources.Contains((o.Map, o.Cell))
                    && !npcTiles.Contains((o.Map, o.Cell)), $"object {o.Id} sits on walkable ground, off warps and NPCs");
            Require(o.Role == "pass-through" || o.FileFlag == o.ServerFlag, $"only the pass-through object is overridden");
            if (o.Role == "pass-through")
                Require(WallBands.Any(b => b.Map == o.Map && b.Y == o.Cell.Y && b.GapX == o.Cell.X && b.GapObject == o.Id)
                        && Free(o.Map, o.Cell with { Y = o.Cell.Y - 1 }) && Free(o.Map, o.Cell with { Y = o.Cell.Y + 1 }),
                        $"object {o.Id} is the wall band's gap, open to the north and south");
            else
                Require(Sides(o.Cell).All(t => Free(o.Map, t)), $"object {o.Id} is open on all four sides");
        }

        Require(Warps.Any(w => w.FromMap == StartMapId && w.ToMap == AnnexMapId)
                && Warps.Any(w => w.FromMap == AnnexMapId && w.ToMap == StartMapId), "a warp each way");
        Require(warpSources.Count == Warps.Length, "no two warps share a source tile");
        foreach (var w in Warps)
        {
            Require(maps.ContainsKey(w.FromMap) && maps.ContainsKey(w.ToMap), "warps join known maps");
            Require(Walkable(w.FromMap, w.From) && ObjectAt(w.FromMap, w.From) == 0, "a warp source is open floor");
            Require(Free(w.ToMap, w.To), "a landing tile is walkable, empty and not a warp");
        }
    }

    private static readonly Regex LuaName = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    private static readonly Regex LuaScriptLine =
        new(@"^function npcs\.([A-Za-z_][A-Za-z0-9_]*)\(ctx\)$", RegexOptions.CultureInvariant);

    /// <summary>The identifiers of the entities the manifest gives a <c>dialog</c> object.</summary>
    private static HashSet<string> ManifestDialogKeys(byte[] manifest)
    {
        using var doc = JsonDocument.Parse(manifest);
        return doc.RootElement.GetProperty("entities").EnumerateArray()
            .Where(e => e.GetProperty("dialog").ValueKind == JsonValueKind.Object)
            .Select(e => e.GetProperty("identifier").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The keys of the click scripts <c>npc_dialog.lua</c> defines, one <c>function npcs.Key(ctx)</c> line
    /// each.</summary>
    private static HashSet<string> LuaScriptKeys(string[] script) =>
        script.Select(line => LuaScriptLine.Match(line))
              .Where(m => m.Success)
              .Select(m => m.Groups[1].Value)
              .ToHashSet(StringComparer.Ordinal);

    // ---- text helpers ----------------------------------------------------------------------------------------

    private static string[] Lines(params object[] parts)
    {
        var lines = new List<string>();
        foreach (var part in parts)
        {
            if (part is string line) lines.Add(line);
            else lines.AddRange((IEnumerable<string>)part);
        }
        return lines.ToArray();
    }

    private static byte[] Text(string[] lines) => Utf8NoBom.GetBytes(string.Join("\n", lines) + "\n");

    private static string Row(params object[] fields) =>
        string.Join(",", fields.Select(f => f switch
        {
            string text => text,
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => throw new ArgumentException($"unexpected field type {f.GetType()}"),
        }));

    private static string Num(ushort value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
