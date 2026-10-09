using System.Text;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Tests;

/// <summary>
/// What a character picked at creation and later changed in play survives a logout and a login: the nation, the
/// face, the sex and the totem (the PR #325 re-check 3, finding F7; Caleb, 2026-10-08, and the same day's "fold into
/// this PR" for face, gender and totem).
///
/// <para><b>Before.</b> Every arrival ran <c>CharacterFactory.ApplyAppearance</c> on the loaded character, and that
/// wrote the creation blob (face, sex, nation, totem, hair) over the saved values. Nothing that changes one of them
/// writes the blob, so every change was undone at the next login: the town criers' kingdom join (and its 20 gold
/// acorns), Rotah's "Become Neutral", <c>Session.SetNation</c> and <c>@nation</c> (whose help line says
/// "(persists)"); the rogue-guild shaman's paid Change Face and Change Gender; worship at a totem shrine, and
/// <c>@totem</c>.</para>
///
/// <para><b>Now.</b> The blob is applied once: by <c>CharacterFactory.FromCreate</c>, and by the legacy import of a
/// per-file record from before creation decoded it. A login keeps the saved values, except a face, a totem or a
/// nation that no client can use, which falls back to the creation pick as the old login made it
/// (<c>CharacterFactory.RestoreUnusableFromCreation</c>). <c>@nation</c> no longer stores a nation past the crest
/// table (the PR #337 review, F1).</para>
///
/// <para><b>The path.</b> Each change fact seeds the row creation writes (<c>FromCreate</c>), sends the real
/// <c>0x10</c> arrival through <c>Session.Receive</c>, changes the value through the path it names, logs out through
/// the read loop's real exit (<c>Session.EndReadLoopAsync</c>, whose teardown saves) and arrives again. The second
/// arrival comes within one autosave interval, so it also fences the departed session (#168), as a quick relog does
/// live. The NPC paths are the real click (<c>0x43</c>) and the real replies (<c>0x3A</c>).</para>
///
/// <para><b>Collection "world", on a World of its own.</b> The GM-command facts need the staff roster
/// (<c>StaffAccounts.Load</c>), which "world" owns (<see cref="TestSeamCollectionTests"/>). The
/// <see cref="SessionFixture"/> is a class fixture, so this class runs on a World no other class stands on. The NPC
/// facts stand on the real maps their NPC stands on: Buya (330, Honi, who picks the kingdom from the map), the
/// Wilderness (1002, Rotah), Onyx's room (343) and Baekho's shrine (1406). Every other fact uses a content-free map
/// in 61901-61913, which no other class uses. Each fact has a database of its own (<see cref="IsolatedDatabase"/>),
/// so "cmdgm", the GM name every GM-command class shares, has a row only that fact writes.</para>
/// </summary>
[Collection("world")]
public sealed class ChangedPicksSurviveLoginTests : IClassFixture<SessionFixture>
{
    // Content-free maps (no Maps.csv row), claimed by no other class.
    private const ushort SetJoinMap = 61901, SetNeutralMap = 61902, GmJoinMap = 61903, GmNeutralMap = 61904;
    private const ushort NewCharacterMap = 61905, TotemMap = 61906, LegacyMap = 61907;
    private const ushort AtTotemMapA = 61908, AtTotemMapB = 61909, FaceMap = 61910;
    private const ushort AtNationClampMapA = 61911, AtNationClampMapB = 61912, NationMap = 61913;

    // The NPCs, by their NPCs.csv rows: Honi, Buya's town crier (map 330); Rotah (the Wilderness, 1002); Onyx, a
    // rogue-guild shaman (343); Baekho's shrine (1406).
    private const int HoniId = 215, RotahId = 195, OnyxId = 81, BaekhoId = 388;

    private const string GmName = "cmdgm";

    // move_to_country's gates (game-data/npc_dialog.lua): level 20, and 20 gold acorns to join a kingdom.
    private const byte MoveLevel = 20;
    private const int Tribute = 20;

    // AppearanceAbility's prices (Server/NpcAbility.cs).
    private const uint FaceCost = 3000, GenderCost = 12000;

    // The face every creation below picks: 0x29, the "newbie" sample (Protocol.md section 9).
    private const byte CreatedFace = 0x29;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly SessionFixture _fx;
    private readonly ITestOutputHelper _out;

    public ChangedPicksSurviveLoginTests(SessionFixture fx, ITestOutputHelper output)
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

    private enum Pick { Nation, Face, Sex, Totem }

    // ---- the nation -------------------------------------------------------------------------------------

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
        var report = ChangeSurvivesARelog("NsvCrier", Blob(nation: 0), Pick.Nation, 2, honi.Map, honi.X,
            (ushort)(honi.Y + 1),
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
        var report = ChangeSurvivesARelog("NsvRotah", Blob(nation: 1), Pick.Nation, 0, rotah.Map, rotah.X,
            (ushort)(rotah.Y + 1),
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
        var report = ChangeSurvivesARelog(name, Blob(nation: created), Pick.Nation, changed, map, 5, 5,
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
        var report = ChangeSurvivesARelog(GmName, Blob(nation: created), Pick.Nation, changed, map, 5, 5, _ => { },
            (s, o) =>
            {
                Run(s, $"@nation {changed}");
                Assert.Contains($"nation set to {changed} ({Character.NationName(changed)}).", MiniTexts(o));
            });
        AssertSurvived(report);
    }

    /// <summary>
    /// <b><c>@nation</c> past the crest table sets the last crest, as <c>@totem</c> does past its own</b> (the PR #337
    /// review, F1). The table is <c>Character.Nations</c>, 0 to 7; 8 and 200 both set 7 (Kaya), with the command's own
    /// reply, and 7 survives the relog.
    /// <para>Red without the clamp (0 to 255, as on 45dafed): the nation is 200 or 8 after the command, and the reply
    /// names "nation#200" or "nation#8".</para>
    /// </summary>
    [Theory]
    [InlineData(2, 200, AtNationClampMapA)]
    [InlineData(0, 8, AtNationClampMapB)]
    public void AtNationPastTheCrestTableSetsTheLastCrest(byte created, int typed, ushort map)
    {
        byte last = (byte)(Character.Nations.Length - 1);
        var report = ChangeSurvivesARelog(GmName, Blob(nation: created), Pick.Nation, last, map, 5, 5, _ => { },
            (s, o) =>
            {
                Run(s, $"@nation {typed}");
                var said = MiniTexts(o);
                _out.WriteLine($"[nation] @nation {typed} said: {string.Join(" | ", said)}");
                Assert.Contains($"nation set to {last} ({Character.NationName(last)}).", said);
            });
        AssertSurvived(report);
    }

    // ---- the face, the sex and the totem ------------------------------------------------------------------

    /// <summary>
    /// <b>A face bought from the shaman is still the face after a relog.</b> A character created with face 41 clicks
    /// Onyx, picks Change Face, says it is not wanted for a crime, pays, steps to the next face and takes it: 42. The
    /// 3,000 coins are charged once, and both the face and the purse are in the row after the relog's logout.
    /// <para>Red on 45dafed, Debug and Release: 41 after the relog, saved back as 41.</para>
    /// </summary>
    [Fact]
    public void AFaceBoughtFromTheShamanSurvivesARelog()
    {
        var (onyx, onyxMob) = Npc(OnyxId, "RogueGuildShamanNpc", "Onyx");
        var report = ChangeSurvivesARelog("NsvFace", Blob(), Pick.Face, CreatedFace + 1, onyx.Map, onyx.X,
            (ushort)(onyx.Y + 1),
            c => c.Coins = FaceCost + 2000,
            (s, o) =>
            {
                // "Change Face"; not wanted for a crime; pay; the instructions; "Next face"; "I want this one"; the
                // closing line.
                Converse(s, o, onyxMob, MenuFrame(1), MenuFrame(2), MenuFrame(1), NextFrame(), MenuFrame(2),
                         MenuFrame(1), NextFrame());
                Assert.Equal(1, Shown(o, "It's tricky to mold this flesh. Let's see how it looks."));
                Assert.Equal(2000u, s.CharCoins);
            });
        AssertSurvived(report);
        Assert.Equal(2000u, report.Row.Coins);
    }

    /// <summary>
    /// <b>A gender bought from the shaman is still the gender after a relog.</b> A man (creation byte 0) with nothing
    /// worn clicks Onyx, picks Change Gender and says yes twice: a woman, for 12,000 coins, charged once.
    /// <para>Red on 45dafed, Debug and Release: 0 after the relog, saved back as 0.</para>
    /// </summary>
    [Fact]
    public void AGenderBoughtFromTheShamanSurvivesARelog()
    {
        var (onyx, onyxMob) = Npc(OnyxId, "RogueGuildShamanNpc", "Onyx");
        var report = ChangeSurvivesARelog("NsvGender", Blob(sex: 0), Pick.Sex, 1, onyx.Map, (ushort)(onyx.X + 1),
            (ushort)(onyx.Y + 1),
            c => c.Coins = GenderCost + 3000,
            (s, o) =>
            {
                // "Change Gender"; "Yes" to the clothes warning; "Yes" to becoming a woman; the closing line.
                Converse(s, o, onyxMob, MenuFrame(2), MenuFrame(1), MenuFrame(1), NextFrame());
                Assert.Equal(1, Shown(o, "There, wow that was hard work."));
                Assert.Equal(3000u, s.CharCoins);
            });
        AssertSurvived(report);
        Assert.Equal(3000u, report.Row.Coins);
    }

    /// <summary>
    /// <b>A totem worshipped at a shrine is still the totem after a relog.</b> A Ju Jak follower (creation byte 0)
    /// clicks Baekho's shrine, says yes and offers five gold acorns: <c>SetTotem(1)</c>. The acorns are gone, and the
    /// totem is 1 in the row, after the logout and after the next login.
    /// <para>Red on 45dafed, Debug and Release: 0 after the relog, saved back as 0.</para>
    /// </summary>
    [Fact]
    public void ATotemWorshippedAtAShrineSurvivesARelog()
    {
        var (shrine, shrineMob) = Npc(BaekhoId, "BaekhoNpc", "Baekho");
        var acorn = Content.ItemByKey("gold_acorn")!;
        var report = ChangeSurvivesARelog("NsvShrine", Blob(totem: 0), Pick.Totem, 1, shrine.Map, shrine.X,
            (ushort)(shrine.Y + 1),
            c => c.Inventory.Add(new InvItem(0, acorn.Id, 5)),
            (s, o) =>
            {
                // A living player has only the worship entry, so the shrine dives in: "Yes", "five Gold acorns",
                // then the closing line.
                Converse(s, o, shrineMob, MenuFrame(1), MenuFrame(2), NextFrame());
                Assert.Equal(1, Shown(o, $"{Content.TotemName(1)} accepts your devotion."));
                Assert.Equal(0, s.WithState(() => s.CountItem(acorn.Key)));
            });
        AssertSurvived(report);
    }

    /// <summary>
    /// <b><c>@totem</c> survives a relog, as its help line says</b>: 0 to 2, and 3 to 0. The only GM command that
    /// persists a face, a sex or a totem (<c>@totemsweep</c> puts the totem back after its one packet).
    /// <para>Red on 45dafed, Debug and Release: 0 and 3 after the relog, each saved back.</para>
    /// </summary>
    [Theory]
    [InlineData(0, 2, AtTotemMapA)]
    [InlineData(3, 0, AtTotemMapB)]
    public void AtTotemSurvivesARelog(byte created, byte changed, ushort map)
    {
        var report = ChangeSurvivesARelog(GmName, Blob(totem: created), Pick.Totem, changed, map, 5, 5, _ => { },
            (s, o) =>
            {
                Run(s, $"@totem {changed}");
                Assert.Contains($"totem set to {changed}.", MiniTexts(o));
            });
        AssertSurvived(report);
    }

    // ---- values no client can use ---------------------------------------------------------------------------

    /// <summary>
    /// <b>A saved totem wins, unless no client can use it.</b> Four cases:
    /// <list type="bullet">
    /// <item>A: creation 2, saved 1 (a shrine or <c>@totem</c> since): 1, the saved one.</item>
    /// <item>B: creation 4 ("none"), saved 1: 1.</item>
    /// <item>C: creation 4, saved 4: the arrival's clamp, unchanged, makes it 3.</item>
    /// <item>D: creation 2, saved 4 (a record from before 85d423b, or <c>@totem 4</c> before 744dfd1): 2, the creation
    /// pick, as the old login gave it.</item>
    /// </list>
    /// <para>A is red on 45dafed (2, the creation byte). D is red when the login has no fallback (3, the clamp).</para>
    /// </summary>
    [Theory]
    [InlineData("NsvTotA", 2, 1, 1)]
    [InlineData("NsvTotB", 4, 1, 1)]
    [InlineData("NsvTotC", 4, 4, 3)]
    [InlineData("NsvTotD", 2, 4, 2)]
    public void TheSavedTotemWinsUnlessNoClientCanUseIt(string name, byte creationTotem, byte savedTotem, byte expected)
    {
        int atLogin = LoginOnce(name, Blob(totem: creationTotem), TotemMap, c => c.Totem = savedTotem,
                                s => s.CharTotem, out var row);
        _out.WriteLine($"[totem] {name}: creation byte {creationTotem}, saved {savedTotem}; at login {atLogin}, " +
                       $"row after logout {row.Totem}");
        Assert.Equal(expected, atLogin);
        Assert.Equal(expected, row.Totem);
    }

    /// <summary>
    /// <b>A saved face wins, unless the 4.95 client has no head for it.</b> The client has heads 0 to 89. The
    /// shaman's first version sold RTK's 200 to 216 and wrote each browsed face into the character, so a row can hold
    /// one. A face of 90 or more falls back to the creation face, as the old login made it; 89 is a head, and stays.
    /// <para>The 89 case is red on 45dafed (41, the creation byte). The 205 and 90 cases are red when the login has no
    /// fallback (the saved value, which <c>FaceLook</c> would draw as head 89).</para>
    /// </summary>
    [Theory]
    [InlineData("NsvFc205", 205, (int)CreatedFace)]
    [InlineData("NsvFc90", 90, (int)CreatedFace)]
    [InlineData("NsvFc89", 89, 89)]
    public void ASavedFaceNoClientCanDrawFallsBackToTheCreationFace(string name, int savedFace, int expected)
    {
        int atLogin = LoginOnce(name, Blob(), FaceMap, c => c.Face = (ushort)savedFace, s => s.CharFace, out var row);
        _out.WriteLine($"[face] {name}: creation byte {CreatedFace}, saved {savedFace}; at login {atLogin}, " +
                       $"row after logout {row.Face}");
        Assert.Equal(expected, atLogin);
        Assert.Equal(expected, (int)row.Face);
    }

    /// <summary>
    /// <b>A saved nation wins, unless it is past the crest table</b> (<c>Character.Nations</c>, 0 to 7; the PR #337
    /// review, F1). A nation of 8 or more falls back to the creation byte when that byte is a nation, as the old login
    /// made it; 7 is a nation, and stays. With no usable creation byte (9) the saved value stays, as the old login left
    /// it. The first case is the reviewer's <c>RvNat200</c>.
    /// <para>The 7 case is red on 45dafed (2, the creation byte). The 200 and 8 cases are red when the login has no
    /// nation fallback (the saved value).</para>
    /// </summary>
    [Theory]
    [InlineData("NsvNat200", 2, 200, 2)]
    [InlineData("NsvNat8", 2, 8, 2)]
    [InlineData("NsvNat7", 2, 7, 7)]
    [InlineData("NsvNatNo", 9, 200, 200)]
    public void ASavedNationPastTheCrestTableFallsBackToTheCreationNation(string name, byte creationNation,
                                                                         byte savedNation, int expected)
    {
        int atLogin = LoginOnce(name, Blob(nation: creationNation), NationMap, c => c.Nation = savedNation,
                                s => s.CharNation, out var row);
        _out.WriteLine($"[nation] {name}: creation byte {creationNation}, saved {savedNation}; at login {atLogin}, " +
                       $"row after logout {row.Nation}");
        Assert.Equal(expected, atLogin);
        Assert.Equal(expected, (int)row.Nation);
    }

    // ---- what creation and the legacy import still decode -----------------------------------------------------

    /// <summary>
    /// <b>A new character still takes its nation from the creation byte.</b> The row creation writes
    /// (<c>FromCreate</c>, then <c>PlaceNewCharacter</c>, as the login server does) holds the byte when it is a
    /// nation, 0 to 7, and the compiled-in Koguryo (1) when it is not; the first login keeps it.
    /// <para>Green on 45dafed. Red, at the creation check, when <c>FromCreate</c> does not decode the blob: every
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
        var c = CharacterFactory.FromCreate(name, Blob(nation: (byte)creationByte));
        CharacterFactory.PlaceNewCharacter(c);
        Assert.Equal(expected, c.Nation);

        int atLogin = LoginOnce(c, NewCharacterMap, s => s.CharNation, out var row);
        _out.WriteLine($"[nation] {name}: creation byte {creationByte}; at login {atLogin}, row after logout {row.Nation}");
        Assert.Equal(expected, atLogin);
        Assert.Equal(expected, row.Nation);
    }

    /// <summary>
    /// <b>A new character still takes its face, sex, totem and hair from the creation bytes</b>, at creation, at the
    /// first login and in the row after it. A totem byte of 4 ("none") keeps the compiled-in 4 at creation, which the
    /// arrival clamps to 3, as before. A face byte the client has no head for (200) is kept: it has nothing better to
    /// fall back to, and <c>FaceLook</c> draws it as head 89, as before.
    /// <para>Green on 45dafed. Red, at the creation check, when <c>FromCreate</c> does not decode the blob.</para>
    /// </summary>
    [Theory]
    [InlineData("NsvNewA", 0x23, 1, 2, 7, 2)]
    [InlineData("NsvNewB", 0x55, 0, 4, 0, 3)]
    [InlineData("NsvNewC", 200, 0, 0, 0, 0)]
    public void ANewCharacterTakesEveryOtherPickFromItsCreationBytes(string name, byte face, byte sex, byte totem,
                                                                    byte hair, byte totemAtLogin)
    {
        var c = CharacterFactory.FromCreate(name, Blob(face, sex, 1, totem, hair));
        CharacterFactory.PlaceNewCharacter(c);
        Assert.Equal((face, sex, totem <= 3 ? totem : (byte)4, hair), ((byte)c.Face, (byte)c.Sex, c.Totem, (byte)c.Hair));

        var seen = (Face: 0, Sex: 0, Totem: 0);
        LoginOnce(c, NewCharacterMap, s => { seen = (s.CharFace, s.CharSex, s.CharTotem); return 0; }, out var row);
        _out.WriteLine($"[look] {name}: bytes face {face} sex {sex} totem {totem} hair {hair}; at login face " +
                       $"{seen.Face} sex {seen.Sex} totem {seen.Totem}; row face {row.Face} sex {row.Sex} totem " +
                       $"{row.Totem} hair {row.Hair}");
        Assert.Equal((face, sex, totemAtLogin), ((byte)seen.Face, (byte)seen.Sex, (byte)seen.Totem));
        Assert.Equal((face, sex, totemAtLogin, hair), ((byte)row.Face, (byte)row.Sex, row.Totem, (byte)row.Hair));
    }

    /// <summary>
    /// <b>A record from before creation decoded its picks still gets them.</b> Creation decoded face and sex from
    /// 521e49b and nation, totem and hair from 85d423b (2026-07-25); older records hold other values, and only the
    /// login re-derivation gave them their picks. They lived in the per-file JSON store that 07878cf replaced with
    /// SQLite, and its legacy import runs at every start and still brings such a file in. This one is the initial
    /// commit's <c>Character</c>, member for member: Sex 1, Face 2 (the byte read before 521e49b), Hair 0, Nation 1,
    /// Totem 4, with creation bytes <c>29 00 02 01 05</c>. After the import and a login it is a man with face 41, a
    /// Buyan, a Baekho follower, with hair 5, as the login's re-derivation used to make it.
    /// <para>Green on 45dafed, where the login re-derived them. Red when the import does not apply them and the login
    /// no longer does: face 2, sex 1, nation 1, hair 0. The totem still comes out 1, through the login's fallback for a
    /// saved 4.</para>
    /// </summary>
    [Fact]
    public void ARecordFromBeforeCreationDecodedItsPicksGetsThemAtImport()
    {
        const string name = "NsvLegacy";
        string dir = Path.Combine(TestProcessState.StateDirectory, $"picks-legacy-{Guid.NewGuid():N}");
        string chars = Path.Combine(dir, "chars");
        Directory.CreateDirectory(chars);
        File.WriteAllText(Path.Combine(chars, "nsvlegacy.json"),
            $$"""{"Id":1,"Name":"{{name}}","Map":{{LegacyMap}},"X":5,"Y":5,"MapXs":20,"MapYs":20,"Sex":1,"Face":2,""" +
            """ "Hair":0,"Nation":1,"Totem":4,"Level":1,"MaxHp":100,"MaxMp":50,"Hp":100,"Mp":50,"Might":3,""" +
            """ "Will":3,"Grace":3,"Armor":0,"MaxInv":27,"CreationBlob":"KQACAQU="}""");

        var store = CharacterStore.ForDatabase(chars, Path.Combine(dir, "project1998.db"));   // imports the file
        Session? s = null;
        try
        {
            (s, _) = Arrive(store, name);
            var atLogin = (Face: s.CharFace, Sex: s.CharSex, Nation: s.CharNation, Totem: s.CharTotem);
            Logout(s);
            var row = Stored(store, name);
            _out.WriteLine($"[legacy] {name}: file face 2 sex 1 nation 1 totem 4 hair 0, bytes 29 00 02 01 05; at login " +
                           $"face {atLogin.Face} sex {atLogin.Sex} nation {atLogin.Nation} totem {atLogin.Totem}; row " +
                           $"face {row.Face} sex {row.Sex} nation {row.Nation} totem {row.Totem} hair {row.Hair}");
            Assert.Equal((CreatedFace, 0, 2, 1), ((byte)atLogin.Face, atLogin.Sex, atLogin.Nation, atLogin.Totem));
            Assert.Equal((CreatedFace, 0, 2, 1, 5), ((byte)row.Face, (int)row.Sex, (int)row.Nation, (int)row.Totem, (int)row.Hair));
        }
        finally
        {
            Release(name, s);
            try { Directory.Delete(dir, recursive: true); } catch { /* the state directory's own cleanup takes it */ }
        }
    }

    // ---- the relog --------------------------------------------------------------------------------------

    private readonly record struct Relog(string Name, string Path, Pick Pick, int Created, int Changed, int AfterChange,
                                         int RowAfterChange, int RowAfterLogout, int AfterRelog, Character Row);

    /// <summary>Seed the row creation writes from <paramref name="blob"/>, log in, run <paramref name="change"/>, log
    /// out, log in again, log out again. Returns what each step read of <paramref name="pick"/>, for
    /// <see cref="AssertSurvived"/>.</summary>
    private Relog ChangeSurvivesARelog(string name, byte[] blob, Pick pick, int changed, ushort map, ushort x, ushort y,
                                       Action<Character> shape, Action<Session, RecordingOutbound> change,
                                       [System.Runtime.CompilerServices.CallerMemberName] string path = "")
    {
        using var db = new IsolatedDatabase();
        var seed = CharacterFactory.FromCreate(name, blob);
        int created = Read(seed, pick);
        Assert.NotEqual(changed, created);
        PutOn(seed, map, x, y);
        shape(seed);
        Assert.True(db.Store.Save(seed));

        Session? first = null, second = null;
        try
        {
            (first, var firstOut) = Arrive(db.Store, name);
            Assert.Equal(created, Read(first, pick));   // an unchanged character logs in with its creation pick
            firstOut.Clear();

            change(first, firstOut);
            int afterChange = Read(first, pick);
            int rowAfterChange = Read(Stored(db.Store, name), pick);   // the path's own save
            Logout(first);
            int rowAfterLogout = Read(Stored(db.Store, name), pick);

            (second, _) = Arrive(db.Store, name);
            int afterRelog = Read(second, pick);
            Logout(second);
            var row = Stored(db.Store, name);

            var r = new Relog(name, path, pick, created, changed, afterChange, rowAfterChange, rowAfterLogout, afterRelog,
                              row);
            _out.WriteLine($"[{pick.ToString().ToLowerInvariant()}] {name} ({path}): created {created}, changed to " +
                           $"{changed}; after the change {afterChange}, row {rowAfterChange}; row after logout " +
                           $"{rowAfterLogout}; after the relog {afterRelog}, row after its logout {Read(row, pick)}");
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
        Assert.Equal(r.Changed, r.AfterRelog);       // red before the fix: the creation pick again
        Assert.Equal(r.Changed, Read(r.Row, r.Pick)); // and the relog's own logout saved it
    }

    private static int Read(Session s, Pick p) => p switch
    {
        Pick.Nation => s.CharNation, Pick.Face => s.CharFace, Pick.Sex => s.CharSex, _ => s.CharTotem,
    };

    private static int Read(Character c, Pick p) => p switch
    {
        Pick.Nation => c.Nation, Pick.Face => c.Face, Pick.Sex => c.Sex, _ => c.Totem,
    };

    /// <summary>Seed the row creation writes from <paramref name="blob"/>, shaped by <paramref name="shape"/>, log in
    /// once and out again. Returns what <paramref name="read"/> saw at login, and the row after the logout.</summary>
    private int LoginOnce(string name, byte[] blob, ushort map, Action<Character> shape, Func<Session, int> read,
                          out Character row)
    {
        var c = CharacterFactory.FromCreate(name, blob);
        shape(c);
        return LoginOnce(c, map, read, out row);
    }

    private int LoginOnce(Character c, ushort map, Func<Session, int> read, out Character row)
    {
        using var db = new IsolatedDatabase();
        PutOn(c, map, 5, 5);   // off any start map: its own scripts are not what this pins
        Assert.True(db.Store.Save(c));
        Session? s = null;
        try
        {
            (s, _) = Arrive(db.Store, c.Name);
            int atLogin = read(s);
            Logout(s);
            row = Stored(db.Store, c.Name);
            return atLogin;
        }
        finally { Release(c.Name, s); }
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
    /// in as the same account (the GM-command facts are all "cmdgm") would be handed this fact's session, and its
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

    /// <summary>A 4.95 creation body (Protocol.md section 9): face, sex, nation, totem, hair. The defaults are a male
    /// Koguryo Ju Jak follower with face 0x29, close to the "newbie" sample.</summary>
    private static byte[] Blob(byte face = CreatedFace, byte sex = 0, byte nation = 1, byte totem = 0, byte hair = 0) =>
        new[] { face, sex, nation, totem, hair };

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
