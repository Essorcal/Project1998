using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The server's own loaders read the synthetic profile (<c>test-content/synthetic/</c>) as its
/// <c>manifest.json</c> says, so a client test that writes its collision table and its walk from the manifest
/// meets the server it is talking to. Each fact points this process at the profile with the manifest's
/// environment, runs <c>Content.Load</c>, checks one promise against the server's registries, terrain, object
/// table, NPC placement or NPC dialog, and puts game-data back.
///
/// <para><b>Collection <c>"tile-translation"</c>, which runs alone</b>
/// (<c>Tests/Support/ExclusiveCollections.cs</c>): the environment, <c>ServerConfig</c>, the content snapshot
/// (with the NPC dialog script <c>Content.Load</c> hands <c>NpcScript</c>), the <c>.map</c> cache and the
/// <c>SObj.tbl</c> cache are one slot each for the whole process, and while a fact here holds the profile every
/// other test would read it. The <c>finally</c> restores the environment, re-resolves the configuration, reloads
/// game-data (its <c>npc_dialog.lua</c> included) and drops both caches, and each fact then checks game-data is
/// back.</para>
///
/// <para>What each promise guards, silently broken otherwise: a <c>.map</c> shorter than its size loads as no
/// terrain at all; <c>SObj.tbl</c> falls back to <c>&lt;root&gt;/RTK-Server/rtk/SObj.tbl</c> when the profile has
/// none, and the install fallback switch does not stop that; the start and the warp landings are trusted by
/// the server with no terrain test; a warp the manifest lists but the server dropped (an unknown destination
/// map) is just a tile; an NPC row out of order shifts every runtime id after it, so a client clicks the wrong
/// NPC; and a dialog script that drifted from the manifest answers a client test with words it does not
/// expect, or a Lua error that ends the conversation with nothing sent.</para>
///
/// <para>The dialog fact also needs the content in the world: the click path reads the NPC's row from
/// <c>Content</c> and its script from <c>NpcScript</c>, which <c>Content.Load</c> fills, and the clerk's runtime id
/// comes from the world's own placement. It builds a <c>World</c> and a socket-free <c>Session</c> of its own,
/// since the shared <c>"world"</c> fixture's world is built on game-data and runs beside other tests.</para>
/// </summary>
[Collection("tile-translation")]
public class SyntheticProfileLoadTests
{
    private static readonly (int Dx, int Dy)[] Steps = { (0, -1), (1, 0), (0, 1), (-1, 0) };   // N E S W, the walk's dir 0-3

    /// <summary>Both maps are in the registry with the manifest's name and size, each <c>.map</c> the server read
    /// is the profile's own and holds exactly width x height x 4 bytes, and both maps are indoor.</summary>
    [Fact]
    public void Maps_load_at_their_manifest_size_from_the_profiles_own_files()
    {
        WithProfileLoaded(manifest =>
        {
            Assert.Equal(manifest.Maps.Select(m => m.Id).Order(), Content.Maps.Keys.Order());
            foreach (var map in manifest.Maps)
            {
                var info = Content.Maps[map.Id];
                Assert.Equal((map.Name, map.Width, map.Height), (info.Name, info.Xs, info.Ys));

                string file = Path.GetFullPath(Path.Combine(manifest.ProfileDir, map.File));
                Assert.Equal((long)map.Width * map.Height * 4, new FileInfo(file).Length);

                MapData? terrain;
                string loaded;
                using (var sink = LogLineSink.Acquire())
                {
                    terrain = MapData.For(map.Id);
                    loaded = sink.LineContaining($"loaded map TK{map.Id}.map");
                }
                Assert.NotNull(terrain);
                Assert.Equal((map.Width, map.Height), (terrain.Xs, terrain.Ys));
                Assert.EndsWith($" from {file}", loaded);

                Assert.Equal(map.Indoor, Content.IsIndoor(map.Id));
            }
        });
    }

    /// <summary>The object table is the profile's <c>SObj.tbl</c>, never the RTK-Server fallback; it holds the
    /// manifest's count; every object's server flag (the file's flag after <c>ObjectFlagOverrides.csv</c>) is the
    /// manifest's <c>serverFlag</c>; and every object sits on the cells the manifest gives it.</summary>
    [Fact]
    public void Object_flags_come_from_the_profiles_own_table_and_equal_the_manifest()
    {
        WithProfileLoaded(manifest =>
        {
            int count;
            string loaded;
            ObjectFlags.Invalidate();
            using (var sink = LogLineSink.Acquire())
            {
                count = ObjectFlags.Count;
                loaded = sink.LineContaining("SObj.tbl");
            }
            var line = Regex.Match(loaded,
                @"-> loaded SObj\.tbl \((\d+) objects\) from (.+?)(?: — (\d+) sprite flag\(s\) overridden by ObjectFlagOverrides\.csv)?$");
            Assert.True(line.Success, $"SObj.tbl did not load: {loaded}");
            string path = Path.GetFullPath(line.Groups[2].Value);
            Assert.Equal(Path.Combine(manifest.ProfileDir, manifest.ObjectTableFile), path);
            Assert.StartsWith(manifest.ProfileDir + Path.DirectorySeparatorChar, path);

            Assert.Equal(manifest.ObjectCount, count);
            Assert.Equal(manifest.ObjectCount.ToString(), line.Groups[1].Value);

            // Object by object first, so a wrong flag or a lost override row fails naming the object.
            Assert.Equal(Enumerable.Range(1, manifest.ObjectCount), manifest.Objects.Select(o => o.Id).Order());
            foreach (var o in manifest.Objects)
            {
                Assert.True(o.ServerFlag == ObjectFlags.Flag(o.Id),
                            $"object {o.Id} ({o.Role}): the server reads flag 0x{ObjectFlags.Flag(o.Id):X2}, " +
                            $"the manifest says 0x{o.ServerFlag:X2} (0x{o.FileFlag:X2} in {manifest.ObjectTableFile}" +
                            (o.FileFlag != o.ServerFlag ? $", 0x{o.ServerFlag:X2} after ObjectFlagOverrides.csv)" : ")"));
                foreach (var cell in o.Cells)
                    Assert.True(MapData.For(cell.Map)!.Obj(cell.X, cell.Y) == o.Id,
                                $"object {o.Id} is not on {cell}: the map holds {MapData.For(cell.Map)!.Obj(cell.X, cell.Y)}");
            }

            // Then the override count the load line reports: an override row that changes nothing, or one for an
            // object the manifest does not list as overridden, shows here.
            var overridden = manifest.Objects.Where(o => o.FileFlag != o.ServerFlag).Select(o => o.Id).ToList();
            string reported = line.Groups[3].Value.Length == 0 ? "0" : line.Groups[3].Value;
            Assert.True(reported == overridden.Count.ToString(),
                        $"ObjectFlagOverrides.csv changed {reported} flag(s) in {manifest.ObjectTableFile}; the manifest " +
                        $"overrides {overridden.Count} (object(s) {string.Join(", ", overridden)}): {loaded}");
        });
    }

    /// <summary>Every NPC row loads as the manifest describes it, in the manifest's order, stationary; and a fresh
    /// world places each on its tile under the manifest's runtime id (the sentinel 300000, the clerk 300001), through
    /// the server's own placement. Nothing else in the world holds an NPC id, so no placement took one first.</summary>
    [Fact]
    public void Each_NPC_stands_on_its_manifest_tile_under_its_manifest_runtime_id()
    {
        WithProfileLoaded(manifest =>
        {
            Assert.True(manifest.Npcs.Count >= 2, $"the manifest lists {manifest.Npcs.Count} NPC(s)");
            // The same rows, in any order: the order is what the placement below has to show.
            Assert.Equal(manifest.Npcs.Select(n => n.NpcId).Order(), Content.Npcs.Select(n => n.Id).Order());
            var world = new World();
            foreach (var want in manifest.Npcs)
            {
                var row = Content.NpcById(want.NpcId)!;
                Assert.Equal((want.NpcId, want.Identifier, want.Name, want.At.Map, want.At.X, want.At.Y, want.Look),
                             (row.Id, row.Key, row.Name, row.Map, row.X, row.Y, row.Look));
                Assert.True(row.Enabled);
                Assert.True(want.Stationary);
                Assert.False(row.MoveTime > 0 && row.ReturnDistance > 0, $"NPC {want.NpcId} would pace");
                Assert.False(MapData.For(want.At.Map)!.Solid(want.At.X, want.At.Y), $"NPC {want.NpcId}'s tile {want.At} is solid");

                var placed = world.MobById(want.At.Map, want.RuntimeId);
                Assert.True(placed is { IsNpc: true } && placed.NpcDefId == want.NpcId,
                            $"the manifest gives NPC {want.NpcId} ({want.Identifier}) runtime id {want.RuntimeId}; the " +
                            $"server placed {(placed is null ? "nothing" : $"NPC {placed.NpcDefId} ('{placed.Name}')")} there");
                Assert.Equal((want.At.X, want.At.Y), (placed!.X, placed.Y));
                Assert.Same(placed, world.MobAt(want.At.Map, want.At.X, want.At.Y));
            }

            var everyNpc = manifest.Maps.SelectMany(m => world.NpcsNear(m.Id, 0, 0, int.MaxValue))
                                        .Select(m => (m.Id, m.NpcDefId)).Order().ToList();
            Assert.Equal(manifest.Npcs.Select(n => (n.RuntimeId, n.NpcId)).Order(), everyNpc);
        });
    }

    /// <summary>
    /// <b>The clerk's conversation runs as the manifest's <c>dialog</c> block says, through the server's own
    /// code.</b> <c>Content.Load</c> hands <c>NpcScript</c> the profile's <c>npc_dialog.lua</c>; a fresh world places
    /// the clerk; a player on the start tile clicks it with the real <c>0x43</c> and answers every box with the real
    /// <c>0x3A</c> bytes, through <c>Session.Receive</c> and <c>HandleNpcDialog</c>; and every <c>0x30</c> the session
    /// sends is read back off the recorder. Each round starts with a click and checks the menu (kind <c>02 02</c>,
    /// the clerk's runtime id, the prompt and the entries):
    /// <list type="number">
    /// <item>the input pick, the input box (<c>04 04</c>, its prompt), a submit of <see cref="Typed"/>, and the text
    /// box (<c>00 01</c>) with the manifest's <c>submitReply</c> for it;</item>
    /// <item>the same with an empty submit;</item>
    /// <item>the same with the input box's Previous (kind 4, step 0), answered with <c>otherReply</c>;</item>
    /// <item>the notice pick and the notice;</item>
    /// <item>each ending pick, a pick past the last entry, and a reply to the menu that is not a pick, each followed
    /// by no <c>0x30</c> at all.</item>
    /// </list>
    /// Every text box carries the next arrow, and its next ends the conversation: no <c>0x30</c> follows and nothing
    /// is left waiting. The words come from the manifest, never from the script, so a script edited on its own
    /// fails here. Last, a click on each NPC the manifest gives no dialog (the sentinel) opens nothing: it has no
    /// script and no menu entries, and the server's greeting is only for an NPC that answers no spoken question.
    /// </summary>
    [Fact]
    public void The_clerks_conversation_answers_every_reply_as_the_manifest_says()
    {
        WithProfileLoaded(manifest =>
        {
            var clerk = Assert.Single(manifest.Npcs, n => n.Dialog is not null);
            var dialog = clerk.Dialog!;
            Assert.Equal(Enumerable.Range(1, dialog.Entries.Count), dialog.Entries.Select(e => e.Pick));
            Assert.NotEmpty(dialog.EndingPicks);

            var world = new World();
            var talk = new Talk(world, manifest, clerk.RuntimeId);
            using var log = LogLineSink.Acquire();
            OnOwnThread(() =>
            {
                byte[] typed = Encoding.ASCII.GetBytes(Typed);
                foreach (var (submit, answer) in new (byte[] Frame, string Answer)[]
                         {
                             (Submit(clerk.RuntimeId, typed), dialog.Submitted(typed)),
                             (Submit(clerk.RuntimeId, Array.Empty<byte>()), dialog.Submitted(Array.Empty<byte>())),
                             (InputPrevious(clerk.RuntimeId), dialog.OtherReply),
                         })
                {
                    talk.OpenMenu(dialog);
                    var input = talk.One(MenuPick(clerk.RuntimeId, dialog.Leading("input").Pick));
                    Assert.Equal((InputBox, InputBox, clerk.RuntimeId, dialog.InputPrompt),
                                 (input.Kind, input.Form, input.Entity, input.Text));
                    talk.AssertAnswered(submit, answer);
                }

                talk.OpenMenu(dialog);
                talk.AssertAnswered(MenuPick(clerk.RuntimeId, dialog.Leading("notice").Pick), dialog.Notice);

                foreach (int pick in dialog.EndingPicks.Append(dialog.Entries.Count + 1))
                {
                    talk.OpenMenu(dialog);
                    talk.AssertEnds(MenuPick(clerk.RuntimeId, pick), $"pick {pick}");
                }
                talk.OpenMenu(dialog);
                talk.AssertEnds(Next(clerk.RuntimeId), "a text box's next sent to the menu");

                foreach (var other in manifest.Npcs.Where(n => n.Dialog is null))
                    talk.AssertEnds(Click(other.RuntimeId), $"a click on {other.Identifier}, which has no dialog,");
            });

            var loud = log.Lines.Where(l => l.Level != LogLevel.Info && l.Line.Contains(talk.Player, StringComparison.Ordinal)).ToList();
            Assert.True(loud.Count == 0, "logged above Info:\n" + string.Join("\n", loud.Select(l => l.Line)));
        });
    }

    /// <summary>The start tile is on a known map, its ground is walkable, it can be entered heading any way, and
    /// it is no warp and no NPC's tile in a fresh world. The game server never checks a new character's tile, so
    /// this is the only check it gets.</summary>
    [Fact]
    public void The_start_tile_is_walkable()
    {
        WithProfileLoaded(manifest =>
        {
            var start = manifest.Start;
            Assert.Contains(manifest.Maps, m => m.Id == start.Map && m.Width > start.X && m.Height > start.Y);
            AssertWalkable(start, "the start");
            Assert.False(Content.TryWarp(start.Map, start.X, start.Y, out _), $"the start {start} is a warp");
            Assert.Null(new World().MobAt(start.Map, start.X, start.Y));
        });
    }

    /// <summary>Every warp the manifest lists is one the server kept, and each round-trips: step into its source,
    /// land; step from that landing into the return source beside it, land beside the first source; step back
    /// into the first source and land on the first landing again. Each landing is walkable and not itself a
    /// warp, since arriving never fires one and the server checks no terrain where it puts the player.</summary>
    [Fact]
    public void Every_warp_round_trips()
    {
        WithProfileLoaded(manifest =>
        {
            Assert.Equal(manifest.Warps.Count, Content.Warps.Count);
            foreach (var w in manifest.Warps)
            {
                var first = Take(w.From);
                Assert.Equal(w.To, first);
                AssertLanding(first);

                var back = AdjacentSource(first);
                var second = Take(back);
                AssertLanding(second);

                Assert.True(IsOneStep(second, w.From),
                            $"from {second}, where the return {back} lands, the source {w.From} is not one step away");
                Assert.Equal(first, Take(w.From));
            }
        });
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    /// <summary>The fixed text the dialog fact submits: mixed case, a digit, punctuation and spaces.</summary>
    private const string Typed = "Line 7: Hello, clerk!";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>The kind bytes of the three boxes (<c>0x30</c> body[0..1]): a text box <c>00 01</c>, a menu
    /// <c>02 02</c>, an input box <c>04 04</c>.</summary>
    private const byte TextBox = 0x00, TextBoxForm = 0x01, MenuBox = 0x02, InputBox = 0x04;

    /// <summary>One <c>0x30</c> as the client parses it, for the sprite head every box here carries
    /// (<c>Session.WriteHead</c>): its kind bytes, the entity id, the prev and next arrows, its message or prompt,
    /// and a menu's entries.</summary>
    private readonly record struct Box(byte Kind, byte Form, uint Entity, bool Prev, bool Next, string Text,
                                       IReadOnlyList<string> Entries);

    private static Box Read(byte[] b)
    {
        Assert.True(b.Length >= 23 && b[7] == 1, $"not a sprite-head box: {Convert.ToHexString(b)}");
        int length = (b[21] << 8) | b[22];
        Assert.True(b.Length >= 23 + length, $"the box's text runs past its end: {Convert.ToHexString(b)}");
        var entries = new List<string>();
        if (b[0] == MenuBox)
        {
            int at = 23 + length;
            int count = b[at++];
            for (int i = 0; i < count; i++)
            {
                int n = b[at++];
                entries.Add(Encoding.ASCII.GetString(b, at, n));
                at += n;
            }
            Assert.Equal(b.Length, at);
        }
        return new Box(b[0], b[1], (uint)((b[2] << 24) | (b[3] << 16) | (b[4] << 8) | b[5]), b[19] != 0, b[20] != 0,
                       Encoding.ASCII.GetString(b, 23, length), entries);
    }

    /// <summary>A player on the profile's start tile in a world of its own, talking to the clerk: each call sends
    /// one client frame through <c>Session.Receive</c> and checks the <c>0x30</c>s that came back.</summary>
    private sealed class Talk
    {
        private static int _serial;
        private readonly Session _s;
        private readonly RecordingOutbound _o;
        private readonly uint _clerk;
        private int _seen;

        public string Player { get; }

        public Talk(World world, SyntheticManifest manifest, uint clerk)
        {
            Player = $"ClerkVisitor{Interlocked.Increment(ref _serial)}";
            var start = manifest.Start;
            var info = Content.Maps[start.Map];
            var character = new Character
            {
                SchemaVersion = Character.CurrentSchemaVersion,
                Id = world.AllocatePlayerId(),
                Name = Player,
                Map = start.Map,
                X = start.X,
                Y = start.Y,
                MapXs = info.Xs,
                MapYs = info.Ys,
            };
            _o = new RecordingOutbound($"recorder:{Player}");
            _s = new Session(_o, 2005, new CharacterStore(RepoPaths.CharsDir()), world, character);
            world.EnterMap(_s, start.Map);
            _o.Clear();
            _clerk = clerk;
        }

        /// <summary>Send <paramref name="frame"/>; exactly one box comes back, and the conversation waits on it.</summary>
        public Box One(byte[] frame)
        {
            _s.Receive(frame);
            var fresh = Fresh();
            Assert.True(fresh.Count == 1, $"expected one box, got {fresh.Count}: " +
                                          string.Join(" | ", fresh.Select(f => $"{f.Kind:X2} {f.Form:X2} \"{f.Text}\"")));
            Assert.True(_s.DialogBusy, $"the box \"{fresh[0].Text}\" is not waiting on a reply");
            return fresh[0];
        }

        /// <summary>A click on the clerk opens its menu: kind <c>02 02</c>, the clerk's runtime id, the manifest's
        /// prompt and its entries in pick order.</summary>
        public void OpenMenu(SyntheticManifest.DialogEntry dialog)
        {
            var menu = One(Click(_clerk));
            Assert.Equal((MenuBox, MenuBox, _clerk, dialog.MenuPrompt), (menu.Kind, menu.Form, menu.Entity, menu.Text));
            Assert.Equal(dialog.Entries.OrderBy(e => e.Pick).Select(e => e.Text), menu.Entries);
        }

        /// <summary>Send <paramref name="reply"/>; a text box comes back (kind <c>00 01</c>, the clerk's id, a next
        /// arrow and no prev) reading <paramref name="text"/>, and its next ends the conversation.</summary>
        public void AssertAnswered(byte[] reply, string text)
        {
            var box = One(reply);
            Assert.Equal((TextBox, TextBoxForm, _clerk, text), (box.Kind, box.Form, box.Entity, box.Text));
            Assert.True(box.Next && !box.Prev, $"the box \"{text}\" has next={box.Next} prev={box.Prev}");
            AssertEnds(Next(_clerk), $"the next on \"{text}\"");
        }

        /// <summary>Send <paramref name="reply"/>; no box comes back and nothing is left waiting.</summary>
        public void AssertEnds(byte[] reply, string what)
        {
            _s.Receive(reply);
            var fresh = Fresh();
            Assert.True(fresh.Count == 0, $"{what} was answered with {fresh.Count} box(es): " +
                                          string.Join(" | ", fresh.Select(f => $"\"{f.Text}\"")));
            Assert.False(_s.DialogBusy, $"after {what} the conversation is still waiting");
        }

        private List<Box> Fresh()
        {
            var all = _o.BodiesOf(ServerOp.NpcDialog);
            var fresh = all.Skip(_seen).Select(Read).ToList();
            _seen = all.Count;
            return fresh;
        }
    }

    /// <summary>Run <paramref name="body"/> on a thread of its own, as the read loop runs a packet, so an awaited
    /// dialog resumes inline on it rather than on the test runner's context.</summary>
    private static void OnOwnThread(Action body)
    {
        Exception? fault = null;
        var t = new Thread(() => fault = Record.Exception(body)) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(Bound), "the fact's thread never finished");
        if (fault is not null) ExceptionDispatchInfo.Capture(fault).Throw();
    }

    // The client's frames, as Tests/DialogReleaseTests.cs builds them, with the box's entity id echoed at [1..4].

    /// <summary>A click on an entity (<c>0x43</c>): <c>01 id(u32) 00</c>.</summary>
    private static byte[] Click(uint id) =>
        SessionFixture.Frame(ClientOp.ClickInfo, new byte[] { 0x01, (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id, 0x00 });

    /// <summary>A <c>0x3A</c> reply: the kind at [0], the entity id at [1..4], the step at [8], then
    /// <paramref name="tail"/> from [9].</summary>
    private static byte[] Reply(byte kind, uint id, byte step, params byte[] tail)
    {
        var body = new byte[9 + tail.Length];
        body[0] = kind;
        body[1] = (byte)(id >> 24); body[2] = (byte)(id >> 16); body[3] = (byte)(id >> 8); body[4] = (byte)id;
        body[8] = step;
        tail.CopyTo(body, 9);
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>A text box's next arrow: kind 1, step 2.</summary>
    private static byte[] Next(uint id) => Reply(0x01, id, 0x02, 0x00);

    /// <summary>A menu pick: kind 2, step 2, then 01 and the 1-based row at [10].</summary>
    private static byte[] MenuPick(uint id, int pick) => Reply(MenuBox, id, 0x02, 0x01, (byte)pick, 0x00);

    /// <summary>An input box's submit: kind 4, step 2, the text's length at [10] and the text from [11].</summary>
    private static byte[] Submit(uint id, byte[] text) =>
        Reply(InputBox, id, 0x02, new byte[] { 0x00, (byte)text.Length }.Concat(text).ToArray());

    /// <summary>An input box's Previous: kind 4, step 0, no text.</summary>
    private static byte[] InputPrevious(uint id) => Reply(InputBox, id, 0x00, 0x00, 0x00);

    private static SyntheticManifest.Tile Take(SyntheticManifest.Tile source)
    {
        Assert.True(Content.TryWarp(source.Map, source.X, source.Y, out var dest), $"{source} is not a warp on the server");
        return new SyntheticManifest.Tile(dest.m, dest.x, dest.y);
    }

    private static void AssertLanding(SyntheticManifest.Tile landing)
    {
        Assert.True(Content.Maps.ContainsKey(landing.Map), $"the landing {landing} is on an unknown map");
        AssertWalkable(landing, "the landing");
        Assert.False(Content.TryWarp(landing.Map, landing.X, landing.Y, out _), $"the landing {landing} is itself a warp");
    }

    /// <summary>The one warp source a step from <paramref name="tile"/>, on its map.</summary>
    private static SyntheticManifest.Tile AdjacentSource(SyntheticManifest.Tile tile)
    {
        var sources = Steps
            .Select(s => (X: tile.X + s.Dx, Y: tile.Y + s.Dy))
            .Where(p => p.X >= 0 && p.Y >= 0 && Content.TryWarp(tile.Map, (ushort)p.X, (ushort)p.Y, out _))
            .Select(p => new SyntheticManifest.Tile(tile.Map, (ushort)p.X, (ushort)p.Y))
            .ToList();
        Assert.True(sources.Count == 1, $"expected one warp source a step from {tile}, found {sources.Count}");
        return sources[0];
    }

    private static bool IsOneStep(SyntheticManifest.Tile a, SyntheticManifest.Tile b) =>
        a.Map == b.Map && Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) == 1;

    /// <summary>In bounds, walkable ground, and enterable heading every way (no object wall bit set).</summary>
    private static void AssertWalkable(SyntheticManifest.Tile tile, string what)
    {
        var terrain = MapData.For(tile.Map);
        Assert.True(terrain is not null, $"{what} {tile}: the map has no terrain");
        Assert.False(terrain.Solid(tile.X, tile.Y), $"{what} {tile}: the ground is solid");
        for (int dir = 0; dir < 4; dir++)
            Assert.False(terrain.BlockedMove(tile.X, tile.Y, dir),
                         $"{what} {tile}: a step heading {"NESW"[dir]} into it is blocked (object {terrain.Obj(tile.X, tile.Y)})");
    }

    /// <summary>Point this process at the profile with the manifest's environment, load it, run
    /// <paramref name="check"/>, and put game-data back whatever happens. The state directory stays this run's
    /// own temp one: the profile's maps have no persisted cells in it.</summary>
    private static void WithProfileLoaded(Action<SyntheticManifest> check)
    {
        var manifest = SyntheticManifest.Read();
        var environment = manifest.ResolvedEnvironment();
        var previous = environment.Select(v => (v.Name, Value: Environment.GetEnvironmentVariable(v.Name))).ToList();
        string gameData = RepoPaths.GameDataDir();

        lock (TestProcessState.Gate)
        {
            try
            {
                foreach (var (name, value) in environment) Environment.SetEnvironmentVariable(name, value);
                ServerConfig.ReloadForTests();
                MapData.Invalidate();
                TestProcessState.LoadContent();
                Assert.Equal(manifest.ProfileDir, RepoPaths.GameDataDir());

                check(manifest);
            }
            finally
            {
                foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
                ServerConfig.ReloadForTests();
                TestProcessState.LoadContent();
                MapData.Invalidate();
                ObjectFlags.Invalidate();
            }

            Assert.Equal(gameData, RepoPaths.GameDataDir());
            Assert.DoesNotContain(manifest.Start.Map, Content.Maps.Keys);
            Assert.True(Content.Maps.Count > 1000, $"game-data did not come back: {Content.Maps.Count} map(s) loaded");
            // The dialog script is the NPC host's one slot: the profile's must be gone and game-data's back.
            foreach (var npc in manifest.Npcs.Where(n => n.Dialog is not null))
                Assert.False(NpcScript.Has(npc.Identifier), $"the profile's script for {npc.Identifier} is still loaded");
            Assert.True(Content.Npcs.Any(n => NpcScript.Has(n.Key)), "game-data's npc_dialog.lua did not come back");
        }
    }
}
