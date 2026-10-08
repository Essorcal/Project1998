using System.Text;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Tests;

/// <summary>
/// A character's saved nation survives a logout and a login (the PR #325 re-check 3, finding F7; Caleb, 2026-10-08).
///
/// <para><b>Before.</b> Every arrival ran <c>CharacterFactory.ApplyAppearance</c> on the loaded character, and that
/// wrote the creation blob's nation byte (<c>CreationBlob[2]</c>) over the saved <c>Nation</c> whenever the byte was
/// a nation. Nothing that changes a nation writes the blob, so every change was undone at the next login: the town
/// criers' kingdom join (and its 20 gold acorns), Rotah's "Become Neutral", <c>Session.SetNation</c>, and
/// <c>@nation</c>, whose help line says "(persists)".</para>
///
/// <para><b>Now.</b> The creation byte decides the nation once: when the character is created
/// (<c>CharacterFactory.FromCreate</c>), and when the legacy import brings a per-file record from before creation
/// decoded the nation into the database. A login keeps the saved nation.</para>
///
/// <para><b>The path.</b> Each change fact seeds the row creation writes (<c>FromCreate</c>), sends the real
/// <c>0x10</c> arrival through <c>Session.Receive</c>, changes the nation through the path it names, logs out
/// through the read loop's real exit (<c>Session.EndReadLoopAsync</c>, whose teardown saves) and arrives again. The
/// second arrival comes within one autosave interval, so it also fences the departed session (#168), as a quick
/// relog does live.</para>
///
/// <para><b>Collection "world", on a World of its own.</b> The <c>@nation</c> facts need the staff roster
/// (<c>StaffAccounts.Load</c>), which "world" owns (<see cref="TestSeamCollectionTests"/>). The
/// <see cref="SessionFixture"/> is a class fixture, so this class runs on a World no other class stands on. The two
/// NPC facts stand on the real maps their NPC stands on, Buya (330) and the Wilderness (1002), because the crier
/// picks the kingdom from the map; every other fact uses a content-free map in 61901-61907, which no other class
/// uses. Each fact has a database of its own (<see cref="IsolatedDatabase"/>), so "cmdgm", the GM name every
/// GM-command class shares, has a row only that fact writes.</para>
/// </summary>
[Collection("world")]
public sealed class NationSurvivesLoginTests : IClassFixture<SessionFixture>
{
    // Content-free maps (no Maps.csv row), claimed by no other class.
    private const ushort SetJoinMap = 61901, SetNeutralMap = 61902, GmJoinMap = 61903, GmNeutralMap = 61904;
    private const ushort NewCharacterMap = 61905, TotemMap = 61906, LegacyMap = 61907;

    // The two NPCs, by their NPCs.csv rows: Honi, Buya's town crier (map 330), and Rotah (the Wilderness, 1002).
    private const int HoniId = 215, RotahId = 195;

    private const string GmName = "cmdgm";

    // move_to_country's gates (game-data/npc_dialog.lua): level 20, and 20 gold acorns to join a kingdom.
    private const byte MoveLevel = 20;
    private const int Tribute = 20;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly SessionFixture _fx;
    private readonly ITestOutputHelper _out;

    public NationSurvivesLoginTests(SessionFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
        lock (TestProcessState.Gate)
        {
            Directory.CreateDirectory(TestProcessState.StateDirectory);
            File.WriteAllText(Path.Combine(TestProcessState.StateDirectory, "gm_accounts.txt"), GmName + "\n");
            StaffAccounts.Load();
        }
    }

    // ---- the four paths that change a nation ----------------------------------------------------------

    /// <summary>
    /// <b>A kingdom joined at the town crier is still the kingdom after a relog.</b> A Neutral (creation byte 0)
    /// clicks Honi in Buya, says yes and pays the 20 gold acorns: <c>move_to_country</c> calls
    /// <c>ctx:setNation(2)</c>. The acorns are gone, and the nation is 2 in the row, after the logout and after the
    /// next login.
    /// <para>Red on 45dafed, Debug and Release: 0 after the relog, and the relog's own logout saved the 0.</para>
    /// </summary>
    [Fact]
    public void AKingdomJoinedAtTheTownCrierSurvivesARelog()
    {
        var (honi, honiMob) = Npc(HoniId, "TownCrierNpc", "Honi");
        var acorn = Content.ItemByKey("gold_acorn")!;
        var report = ChangeSurvivesARelog("NsvCrier", created: 0, changed: 2, honi.Map, honi.X, (ushort)(honi.Y + 1),
            c => { c.Level = MoveLevel; c.Inventory.Add(new InvItem(0, acorn.Id, Tribute)); },
            (s, o) =>
            {
                Converse(s, o, honiMob, MenuFrame(2), NextFrame());   // "Yes, very much.", then "Welcome to Buya."
                Assert.Equal(1, Shown(o, "Welcome to Buya."));
                Assert.Equal(0, s.WithState(() => s.CountItem(acorn.Key)));   // the tribute was taken
            });
        AssertSurvived(report);
    }

    /// <summary>
    /// <b>Becoming Neutral at Rotah is still Neutral after a relog.</b> A Koguryo citizen (creation byte 1) clicks
    /// Rotah, reads the three warning pages and says "Yes, please.": <c>ctx:setNation(0)</c>. The nation is 0 in
    /// the row, after the logout and after the next login.
    /// <para>Red on 45dafed, Debug and Release: 1 after the relog, saved back as 1.</para>
    /// </summary>
    [Fact]
    public void BecomingNeutralAtRotahSurvivesARelog()
    {
        var (rotah, rotahMob) = Npc(RotahId, "RotahNpc", "Rotah");
        var report = ChangeSurvivesARelog("NsvRotah", created: 1, changed: 0, rotah.Map, rotah.X, (ushort)(rotah.Y + 1),
            c => c.Level = MoveLevel,
            (s, o) =>
            {
                Converse(s, o, rotahMob, NextFrame(), NextFrame(), NextFrame(), MenuFrame(2), NextFrame());
                Assert.Equal(1, Shown(o, "Welcome to the wilderness."));
            });
        AssertSurvived(report);
    }

    /// <summary>
    /// <b><c>Session.SetNation</c>, called directly, survives a relog</b>, a join (0 to 3) and a move to Neutral
    /// (2 to 0). Its side effect is unchanged: the bound home (the mayors' "Live in" room, registry "home") is
    /// cleared, and is still clear after the relog.
    /// <para>Red on 45dafed, Debug and Release: 0 after the relog for the join and 2 for the move, each saved back.
    /// The home check is red when <c>SetNation</c> stops clearing it: 10, the room, after the relog.</para>
    /// </summary>
    [Theory]
    [InlineData("NsvSetJn", 0, 3, SetJoinMap)]
    [InlineData("NsvSetNt", 2, 0, SetNeutralMap)]
    public void SetNationSurvivesARelog(string name, byte created, byte changed, ushort map)
    {
        var report = ChangeSurvivesARelog(name, created, changed, map, 5, 5,
            c => QuestState.Over(c, QuestState.Registry).Set(Session.HomeReg, 10),   // Sanhae's room
            (s, _) =>
            {
                Assert.Equal(10, s.WithState(() => s.Quest(QuestState.Registry).Get(Session.HomeReg)));
                s.WithState(() => s.SetNation(changed));
            });
        AssertSurvived(report);
        Assert.Equal(Session.HomeNone, QuestState.Over(report.Row, QuestState.Registry).Get(Session.HomeReg));
    }

    /// <summary>
    /// <b><c>@nation</c> survives a relog, as its help line says</b>, a join (0 to 1) and a move to Neutral (3 to
    /// 0). Entered as the framed <c>0x0E</c> chat packet, so the tier gate is in the path.
    /// <para>Red on 45dafed, Debug and Release: 0 and 3 after the relog, each saved back.</para>
    /// </summary>
    [Theory]
    [InlineData(0, 1, GmJoinMap)]
    [InlineData(3, 0, GmNeutralMap)]
    public void AtNationSurvivesARelog(byte created, byte changed, ushort map)
    {
        var report = ChangeSurvivesARelog(GmName, created, changed, map, 5, 5, _ => { },
            (s, o) =>
            {
                Run(s, $"@nation {changed}");
                Assert.Contains($"nation set to {changed} ({Character.NationName(changed)}).", MiniTexts(o));
            });
        AssertSurvived(report);
    }

    // ---- what did not change ------------------------------------------------------------------------------

    /// <summary>
    /// <b>A new character still takes its nation from the creation byte.</b> The row creation writes
    /// (<c>FromCreate</c>, then <c>PlaceNewCharacter</c>, as the login server does) holds the byte when it is a
    /// nation, 0 to 7, and the compiled-in Koguryo (1) when it is not; the first login keeps it.
    /// <para>Green on 45dafed. Red, at the creation check, when <c>FromCreate</c> does not apply the byte: every
    /// nation but 1 comes out 1.</para>
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(5, 5)]
    [InlineData(6, 6)]
    [InlineData(7, 7)]
    [InlineData(8, 1)]
    [InlineData(255, 1)]
    public void ANewCharacterTakesItsNationFromTheCreationByte(int creationByte, int expected)
    {
        string name = $"NsvNew{creationByte}";
        using var db = new IsolatedDatabase();
        var c = CharacterFactory.FromCreate(name, Blob((byte)creationByte));
        CharacterFactory.PlaceNewCharacter(c);
        Assert.Equal(expected, c.Nation);
        PutOn(c, NewCharacterMap, 5, 5);   // off the start map: its own scripts are not what this pins
        Assert.True(db.Store.Save(c));

        Session? s = null;
        try
        {
            (s, _) = Arrive(db.Store, name);
            int atLogin = s.CharNation;
            Logout(s);
            int row = Stored(db.Store, name).Nation;
            _out.WriteLine($"[nation] {name}: creation byte {creationByte}; at login {atLogin}, row after logout {row}");
            Assert.Equal(expected, atLogin);
            Assert.Equal(expected, row);
        }
        finally { Release(name, s); }
    }

    /// <summary>
    /// <b>The totem at login is what it was.</b> The arrival still applies a creation totem byte of 0 to 3 over the
    /// saved totem (the first case, which is the open follow-up in the report, pinned so this change cannot move
    /// it), still leaves the saved totem alone when the byte is 4 ("none"), and still clamps a saved 4 to 3.
    /// <para>Green on 45dafed. Red when the arrival applies a byte of 4 (NsvTotB reads 3), and when it leaves the
    /// totem alone (NsvTotA reads 1, NsvTotC 4).</para>
    /// </summary>
    [Theory]
    [InlineData("NsvTotA", 2, 1, 2)]
    [InlineData("NsvTotB", 4, 1, 1)]
    [InlineData("NsvTotC", 4, 4, 3)]
    public void TheTotemAtLoginIsUnchanged(string name, byte creationTotem, byte savedTotem, byte expected)
    {
        using var db = new IsolatedDatabase();
        var c = CharacterFactory.FromCreate(name, Blob(1, creationTotem));
        PutOn(c, TotemMap, 5, 5);
        c.Totem = savedTotem;   // as a shrine or @totem leaves it
        Assert.True(db.Store.Save(c));

        Session? s = null;
        try
        {
            (s, _) = Arrive(db.Store, name);
            _out.WriteLine($"[totem] {name}: creation byte {creationTotem}, saved {savedTotem}; at login {s.CharTotem}");
            Assert.Equal(expected, s.CharTotem);
            Assert.Equal(1, s.CharNation);
            Logout(s);
        }
        finally { Release(name, s); }
    }

    /// <summary>
    /// <b>A record from before creation decoded the nation still gets its creation nation.</b> Until 85d423b
    /// (2026-07-25) creation saved the compiled-in Koguryo (1) whatever the player picked, and those records
    /// lived in the per-file JSON store that 07878cf replaced with SQLite. Its legacy import runs at every start
    /// and still brings such a file in. This one is the initial commit's <c>Character</c>, member for member,
    /// saved with Nation 1 and a creation byte of 2 (Buya): after the import and a login, the character is Buyan,
    /// as the login's re-derivation used to make it.
    /// <para>Green on 45dafed, where the login re-derived it. Red when the import does not apply the creation nation
    /// and the login no longer does: 1 at login and in the row.</para>
    /// </summary>
    [Fact]
    public void ARecordFromBeforeCreationDecodedTheNationGetsItsCreationNation()
    {
        const string name = "NsvLegacy";
        string dir = Path.Combine(TestProcessState.StateDirectory, $"nation-legacy-{Guid.NewGuid():N}");
        string chars = Path.Combine(dir, "chars");
        Directory.CreateDirectory(chars);
        // 0x29 0x00 0x02 0x00 0x00: face 41, male, Buya, JuJak, hair 0 (the "newbie" sample, Protocol.md section 9).
        File.WriteAllText(Path.Combine(chars, "nsvlegacy.json"),
            $$"""{"Id":1,"Name":"{{name}}","Map":{{LegacyMap}},"X":5,"Y":5,"MapXs":20,"MapYs":20,"Sex":0,"Face":41,""" +
            """ "Hair":0,"Nation":1,"Totem":4,"Level":1,"MaxHp":100,"MaxMp":50,"Hp":100,"Mp":50,"Might":3,""" +
            """ "Will":3,"Grace":3,"Armor":0,"MaxInv":27,"CreationBlob":"KQACAAA="}""");

        var store = CharacterStore.ForDatabase(chars, Path.Combine(dir, "project1998.db"));   // imports the file
        Session? s = null;
        try
        {
            (s, _) = Arrive(store, name);
            int atLogin = s.CharNation;
            Logout(s);
            int row = Stored(store, name).Nation;
            _out.WriteLine($"[nation] {name}: legacy row Nation 1, creation byte 2; at login {atLogin}, row after logout {row}");
            Assert.Equal(2, atLogin);
            Assert.Equal(2, row);
        }
        finally
        {
            Release(name, s);
            try { Directory.Delete(dir, recursive: true); } catch { /* the state directory's own cleanup takes it */ }
        }
    }

    // ---- the relog --------------------------------------------------------------------------------------

    private readonly record struct Relog(string Name, string Path, byte Created, byte Changed, int AfterChange,
                                         int RowAfterChange, int RowAfterLogout, int AfterRelog, Character Row);

    /// <summary>Seed the row creation writes for <paramref name="created"/>, log in, run <paramref name="change"/>,
    /// log out, log in again, log out again. Returns what each step read, for <see cref="AssertSurvived"/>.</summary>
    private Relog ChangeSurvivesARelog(string name, byte created, byte changed, ushort map, ushort x, ushort y,
                                       Action<Character> shape, Action<Session, RecordingOutbound> change,
                                       [System.Runtime.CompilerServices.CallerMemberName] string path = "")
    {
        using var db = new IsolatedDatabase();
        var seed = CharacterFactory.FromCreate(name, Blob(created));
        Assert.Equal(created, seed.Nation);
        PutOn(seed, map, x, y);
        shape(seed);
        Assert.True(db.Store.Save(seed));

        Session? first = null, second = null;
        try
        {
            (first, var firstOut) = Arrive(db.Store, name);
            Assert.Equal(created, first.CharNation);   // an unchanged character logs in with its creation nation
            firstOut.Clear();

            change(first, firstOut);
            int afterChange = first.CharNation;
            int rowAfterChange = Stored(db.Store, name).Nation;   // the path's own save
            Logout(first);
            int rowAfterLogout = Stored(db.Store, name).Nation;

            (second, _) = Arrive(db.Store, name);
            int afterRelog = second.CharNation;
            Logout(second);
            var row = Stored(db.Store, name);

            var r = new Relog(name, path, created, changed, afterChange, rowAfterChange, rowAfterLogout, afterRelog, row);
            _out.WriteLine($"[nation] {name} ({path}): created {created}, changed to {changed}; after the change " +
                           $"{afterChange}, row {rowAfterChange}; row after logout {rowAfterLogout}; after the relog " +
                           $"{afterRelog}, row after its logout {row.Nation}");
            return r;
        }
        finally
        {
            Release(name, second ?? first);
            if (first is not null && second is not null) _fx.World.LeaveMap(first, first.CharMap);
        }
    }

    private static void AssertSurvived(Relog r)
    {
        Assert.Equal(r.Changed, r.AfterChange);
        Assert.Equal(r.Changed, r.RowAfterChange);
        Assert.Equal(r.Changed, r.RowAfterLogout);
        Assert.Equal(r.Changed, r.AfterRelog);       // red before the fix: the creation byte again
        Assert.Equal(r.Changed, r.Row.Nation);       // and the relog's own logout saved it
    }

    private (Session Session, RecordingOutbound Outbound) Arrive(CharacterStore store, string name)
    {
        var outbound = new RecordingOutbound($"recorder:{name}");
        var s = new Session(outbound, 2005, store, _fx.World);
        s.Receive(ArrivalFrame(name));
        Assert.False(outbound.Closed, $"the arrival for '{name}' was refused");
        Assert.Contains(s, _fx.World.Online.All());
        return (s, outbound);
    }

    /// <summary>The read loop's exit on a socket-free session: the teardown leaves the map, parks the slot and
    /// saves. Its only await is on an already-completed writer, so it runs to its end here.</summary>
    private static void Logout(Session s) =>
        Assert.True(s.EndReadLoopAsync(Task.CompletedTask).IsCompletedSuccessfully, "the logout did not run to its end");

    /// <summary>Off the map, and the account's slot out of the registry. The last logout parks the slot for the next
    /// login's fence (#168), and on a World whose autosave sweep never runs nothing prunes it: the next fact that logs
    /// in as the same account (both <c>@nation</c> facts are "cmdgm") would be handed this fact's session, and its
    /// kick would write to this fact's database. Taking the slot and giving it straight back drops the parked entry
    /// without a kick.</summary>
    private void Release(string name, Session? s)
    {
        if (s is null) return;
        string key = CharacterStore.Key(name);
        _fx.World.LeaveMap(s, s.CharMap);
        _fx.World.Online.Register(key, s, out _);
        _fx.World.Online.Unregister(key, s);
    }

    private static Character Stored(CharacterStore store, string name)
    {
        var load = store.Load(name);
        Assert.Equal(CharacterLoadStatus.Ok, load.Status);
        return Assert.IsType<Character>(load.Character);
    }

    /// <summary>A 4.95 creation body (Protocol.md section 9): face, sex, nation, totem, hair. Face 0x29 and male,
    /// as the "newbie" sample.</summary>
    private static byte[] Blob(byte nation, byte totem = 0) => new byte[] { 0x29, 0x00, nation, totem, 0x00 };

    private static void PutOn(Character c, ushort map, ushort x, ushort y)
    {
        var info = Content.Maps.GetValueOrDefault(map);
        c.Map = map; c.X = x; c.Y = y;
        c.MapXs = info?.Xs ?? 20; c.MapYs = info?.Ys ?? 20;
    }

    private static byte[] ArrivalFrame(string user)
    {
        var body = new List<byte> { 9 };
        body.AddRange(Encoding.ASCII.GetBytes("NexonInc."));
        body.Add((byte)user.Length);
        body.AddRange(Encoding.ASCII.GetBytes(user));
        body.AddRange(HandoffTokens.Mint(user, System.Net.IPAddress.None.ToString()));
        return TkPacket.Build(Opcode.Arrival, 0, body.ToArray());
    }

    // ---- talking to an NPC ----------------------------------------------------------------------------

    /// <summary>The NPC's row and the creature standing on its tile in this class's World.</summary>
    private (NpcDef Def, Mob Mob) Npc(int npcId, string key, string name)
    {
        var def = Content.NpcById(npcId);
        Assert.True(def is not null && def.Key == key && def.Name == name, $"NPC {npcId} is not {name} ({key})");
        var mob = _fx.World.NpcsNear(def!.Map, def.X, def.Y, 0).FirstOrDefault(m => m.NpcDefId == npcId);
        Assert.True(mob is not null, $"{name} is not placed in the world");
        return (def, mob!);
    }

    /// <summary>Click <paramref name="npc"/> (the real <c>0x43</c>, <c>01 id(u32) 00</c>) and answer its prompts in
    /// order, each once it is up: the n-th <c>0x30</c> sent and a prompt waiting on its <c>0x3A</c>. Ends when the
    /// conversation does.</summary>
    private static void Converse(Session s, RecordingOutbound o, Mob npc, params byte[][] answers)
    {
        int before = o.BodiesOf(ServerOp.NpcDialog).Count;
        uint id = npc.Id;
        s.Receive(SessionFixture.Frame(ClientOp.ClickInfo,
            new byte[] { 0x01, (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id, 0x00 }));
        for (int i = 0; i < answers.Length; i++)
        {
            int prompt = before + i + 1;
            Assert.True(SpinWait.SpinUntil(() => s.DialogBusy && o.BodiesOf(ServerOp.NpcDialog).Count >= prompt, Bound),
                        $"prompt {i + 1} of {npc.Name}'s conversation never came");
            s.Receive(answers[i]);
        }
        Assert.True(SpinWait.SpinUntil(() => !s.DialogBusy, Bound), $"{npc.Name}'s conversation did not end");
    }

    /// <summary>The client's "next" on a text box (<c>0x3A</c> kind 1).</summary>
    private static byte[] NextFrame()
    {
        var body = new byte[11];
        body[0] = 0x01;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>A menu pick (<c>0x3A</c> kind 2, the 1-based index at [10]).</summary>
    private static byte[] MenuFrame(byte index)
    {
        var body = new byte[11];
        body[0] = 0x02;
        body[10] = index;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>How many NPC text boxes said exactly <paramref name="text"/>: a box ends with its message, after the
    /// message's u16BE length, in ASCII.</summary>
    private static int Shown(RecordingOutbound o, string text)
    {
        byte[] want = Encoding.ASCII.GetBytes(text);
        return o.BodiesOf(ServerOp.NpcDialog).Count(body =>
        {
            int at = body.Length - want.Length;
            return at >= 2 && (body[at - 2] << 8 | body[at - 1]) == want.Length && body.AsSpan(at).SequenceEqual(want);
        });
    }

    // ---- a GM command -----------------------------------------------------------------------------------

    private static void Run(Session session, string command)
    {
        var text = Encoding.ASCII.GetBytes(command);
        var body = new byte[2 + text.Length];
        body[0] = 0;
        body[1] = (byte)text.Length;
        text.CopyTo(body, 2);
        session.Receive(SessionFixture.Frame(0x0E, body));
    }

    private static List<string> MiniTexts(RecordingOutbound o) =>
        o.BodiesOf(0x0A).Select(b => Encoding.ASCII.GetString(b, 3, (b[1] << 8) | b[2])).ToList();
}
