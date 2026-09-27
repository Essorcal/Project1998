using System.Text.RegularExpressions;
using Shared;
using Xunit;

namespace Tests;

/// <summary>
/// Character creation's <c>0x04</c> CreateAppearance carries the appearance only, face first
/// (docs/4.x/Protocol.md §9). The login server used to read its first byte as a name length, so a face of
/// 1 to 4 turned 1 to 4 appearance bytes into the "name", and the name gate refused a name the player
/// never typed after <c>0x02</c> had accepted the real one. Nothing complained: the player just saw a name
/// refusal on the appearance screen.
///
/// <para>The tests cannot reference the login server, so the character a create writes is built by
/// <see cref="CharacterFactory.FromCreate"/> in Shared and tested here, and the last fact reads
/// <c>LoginSession.HandleCreate</c>'s source to check it uses that builder and gates the name it writes.
/// The name gate itself (<c>LoginSession.NameProblem</c>) is the shared shape rule plus the taken-name
/// lookup, so the refusals below are asserted through those two halves.</para>
/// </summary>
[Collection("db")]
public class CreateFaceByteTests : IDisposable
{
    private readonly List<string> _made = new();

    /// <summary>A 4.95 <c>0x04</c> body: face, sex, nation, totem, hair.</summary>
    private static byte[] Body(byte face, byte sex = 0x01, byte nation = 0x02, byte totem = 0x01, byte hair = 0x00) =>
        new[] { face, sex, nation, totem, hair };

    /// <summary>A name the shape rule accepts (letters only, 11 of them) that no other run can have made.</summary>
    private string FreshName()
    {
        var letters = Guid.NewGuid().ToString("N")[..9].Select(h => (char)('a' + Convert.ToInt32(h.ToString(), 16)));
        var n = "Zz" + new string(letters.ToArray());
        _made.Add(n);
        return n;
    }

    public void Dispose()
    {
        try
        {
            using var cn = Db.Open();
            foreach (var n in _made)
            {
                using var cmd = cn.CreateCommand();
                cmd.CommandText = "DELETE FROM characters WHERE username=$u;";
                cmd.Parameters.AddWithValue("$u", CharacterStore.Key(n));
                cmd.ExecuteNonQuery();
            }
        }
        catch { /* best effort cleanup */ }
    }

    /// <summary>THE regression. Faces 1 to 4 used to become a 1-to-4-byte name, and were refused.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void FacesOneToFourCreateUnderTheNameTheNameCheckAccepted(byte face)
    {
        var c = CharacterFactory.FromCreate("Bobby", Body(face));

        Assert.Equal("Bobby", c.Name);
        Assert.Null(NameRules.ShapeProblem(c.Name));   // the gate's shape half passes it
        Assert.Equal(face, c.Face);
        Assert.Equal(1, c.Sex);
        Assert.Equal(2, c.Nation);
        Assert.Equal(1, c.Totem);
        Assert.Equal(0, c.Hair);
        Assert.Equal(Body(face), c.CreationBlob);
        Assert.Equal(Character.CurrentSchemaVersion, c.SchemaVersion);
    }

    /// <summary>Every other face already created under the <c>0x02</c> name, and must still create the same
    /// character. These are the eight live blobs recorded in Protocol.md §9, with the appearance each one was
    /// decoded to there.</summary>
    [Theory]
    [InlineData(new byte[] { 0x55, 0x00, 0x02, 0x02, 0x00 }, 0x55, 0, 2, 2)]   // male
    [InlineData(new byte[] { 0x12, 0x01, 0x02, 0x01, 0x00 }, 0x12, 1, 2, 1)]   // female
    [InlineData(new byte[] { 0x00, 0x00, 0x02, 0x02, 0x00 }, 0x00, 0, 2, 2)]   // faceone: face 0
    [InlineData(new byte[] { 0x23, 0x00, 0x01, 0x02, 0x00 }, 0x23, 0, 1, 2)]   // facetwo
    [InlineData(new byte[] { 0x34, 0x00, 0x01, 0x02, 0x00 }, 0x34, 0, 1, 2)]   // facethree: a high face
    [InlineData(new byte[] { 0x29, 0x00, 0x02, 0x00, 0x00 }, 0x29, 0, 2, 0)]   // newbie
    [InlineData(new byte[] { 0x32, 0x00, 0x02, 0x03, 0x00 }, 0x32, 0, 2, 3)]   // newbiea
    [InlineData(new byte[] { 0x3d, 0x00, 0x02, 0x00, 0x00 }, 0x3d, 0, 2, 0)]   // newbieb
    public void OtherFacesStillCreateAsBefore(byte[] body, int face, int sex, int nation, int totem)
    {
        var c = CharacterFactory.FromCreate("Bobby", body);

        Assert.Equal("Bobby", c.Name);
        Assert.Equal(face, c.Face);
        Assert.Equal(sex, c.Sex);
        Assert.Equal(nation, c.Nation);
        Assert.Equal(totem, c.Totem);
        Assert.Equal(0, c.Hair);
        Assert.Same(body, c.CreationBlob);   // the whole body, as before: the game server re-decodes it
    }

    /// <summary>With no <c>0x02</c> on the connection the name is empty, whatever the <c>0x04</c> body holds,
    /// and the gate refuses an empty name with the create screen's own message.</summary>
    [Theory]
    [InlineData(new byte[] { 0x01, 0x01, 0x02, 0x01, 0x00 })]
    [InlineData(new byte[] { 0x04, 0x01, 0x02, 0x01, 0x00 })]
    [InlineData(new byte[] { 0x29, 0x00, 0x02, 0x01, 0x00 })]
    [InlineData(new byte[] { 0x03, (byte)'B', (byte)'o', (byte)'b', 0x29, 0x00, 0x02, 0x01, 0x00 })]   // a name-prefixed body is not a name
    public void ACreateWithNoNameCheckIsRefused(byte[] body)
    {
        var c = CharacterFactory.FromCreate("", body);

        Assert.Equal("", c.Name);
        Assert.Equal("Please enter a name.", NameRules.ShapeProblem(c.Name));
    }

    /// <summary>A taken name reaches the gate's taken-name lookup as itself for faces 1 to 4 too. Before, a
    /// face of 1 to 4 handed the gate appearance bytes instead, and a create for a taken name was refused
    /// for its "shape", not because it was taken.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(0x29)]
    public void ACreateForATakenNameReachesTheTakenCheck(byte face)
    {
        var taken = FreshName();
        Assert.True(new CharacterStore(RepoPaths.CharsDir()).Save(new Character
        {
            SchemaVersion = Character.CurrentSchemaVersion,
            Name = taken,
        }));

        var c = CharacterFactory.FromCreate(taken, Body(face));

        Assert.Equal(taken, c.Name);
        Assert.Null(NameRules.ShapeProblem(c.Name));          // so only the taken check can refuse it
        Assert.True(CharacterStore.CharacterExists(c.Name));   // and it does: NameProblem's first lookup
    }

    /// <summary>The tests cannot reference the login server, so this reads its source. HandleCreate must
    /// build the character from the <c>0x02</c> name, read nothing from the body itself, and refuse through
    /// the name gate, on the name it writes, before it writes anything.</summary>
    [Fact]
    public void TheLoginServerCreatesUnderTheCheckedNameAndGatesIt()
    {
        var src = File.ReadAllText(RepoFile("LoginServer/LoginSession.cs"));
        var fn = Regex.Match(src, @"private void HandleCreate\(byte\[\] dec\).*?\n    \}", RegexOptions.Singleline);
        Assert.True(fn.Success, "could not find LoginSession.HandleCreate to check it");
        var body = fn.Value;

        // The name is the 0x02 one, and nothing in the handler reads the body as a name.
        Assert.Contains("var c = CharacterFactory.FromCreate(_pendingName, dec);", body);
        Assert.DoesNotMatch(@"\bdec\s*\[", body);
        Assert.DoesNotContain("GetString(dec", body);
        Assert.DoesNotMatch(@"\bc\.Name\s*=[^=]", body);
        Assert.DoesNotContain("new Character", body);

        // The gate refuses on the name that is written, and it runs before the character and account rows.
        var gate = Regex.Match(body, @"if \(NameProblem\(c\.Name\) is \{ \} why\)\s*\{(?:\s*Log\.Info\([^\n]*\);)?\s*SendMessage\(why\);\s*return;\s*\}");
        Assert.True(gate.Success, "HandleCreate must refuse through NameProblem(c.Name) and return");
        int save = body.IndexOf("_store.Save(c)", StringComparison.Ordinal);
        int account = body.IndexOf("Accounts.SetPassword(c.Name,", StringComparison.Ordinal);
        Assert.True(save > gate.Index, "the character row must be written after the name gate");
        Assert.True(account > save, "the account row must be written after the character row");

        // No 0x02 means an empty name: the field starts empty and only the name check assigns it.
        Assert.Contains("private string _pendingName = \"\";", src);
        var nameCheck = Regex.Match(src, @"private void NameAvailable\(byte\[\] dec\).*?\n    \}", RegexOptions.Singleline);
        Assert.True(nameCheck.Success, "could not find LoginSession.NameAvailable to check it");
        Assert.Equal(2, Regex.Matches(src, @"\b_pendingName\s*=(?!=)").Count);         // that initializer, and
        Assert.Single(Regex.Matches(nameCheck.Value, @"\b_pendingName\s*=(?!=)"));      // the 0x02 name check's parse

        // And the gate still refuses a taken name, with the message the create screen shows.
        var gateFn = Regex.Match(src, @"private static string\? NameProblem\(string name\).*?\n    \}", RegexOptions.Singleline);
        Assert.True(gateFn.Success, "could not find LoginSession.NameProblem to check it");
        Assert.Contains("if (CharacterStore.CharacterExists(name) || Accounts.Exists(name)) return \"That name is already taken.\";", gateFn.Value);
    }

    /// <summary>Walk up to the repo root so the check above reads the real source however tests are hosted.</summary>
    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Project1998.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}
