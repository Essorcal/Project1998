using System.Text.RegularExpressions;
using Shared;
using Xunit;

namespace Tests;

/// <summary>
/// The shape a new character's name must have (#299). A 12-letter name used to be created and then locked
/// out at world entry, because the client's handoff field carries back at most 11 letters. The messages are
/// written out in full: they are what the create screen shows, and the one change #299 allows is that a
/// 12-letter name is now refused with the 11-letter wording. Every other refusal must read as it did.
/// </summary>
public class NameRulesTests
{
    [Fact]
    public void TwelveLetterNameIsRefusedWithTheElevenLetterMessage() =>
        Assert.Equal("Names must be 3 to 11 letters.", NameRules.ShapeProblem("Abcdefghijkl"));

    [Theory]
    [InlineData("Bob")]
    [InlineData("abcdefghijk")]
    [InlineData("AbCdEfGhIjK")]
    public void NamesOfThreeToElevenLettersPass(string name) =>
        Assert.Null(NameRules.ShapeProblem(name));

    /// <summary>The rest of the shape gate, unchanged apart from the ceiling the length message names.</summary>
    [Theory]
    [InlineData(null, "Please enter a name.")]
    [InlineData("", "Please enter a name.")]
    [InlineData("   ", "Please enter a name.")]
    [InlineData("Bo", "Names must be 3 to 11 letters.")]
    [InlineData("Abcdefghijklm", "Names must be 3 to 11 letters.")]
    [InlineData("Bo b", "Names may only use letters.")]
    [InlineData("Bob1", "Names may only use letters.")]
    [InlineData("Bob_", "Names may only use letters.")]
    public void OtherNamesKeepTheirRefusals(string? name, string expected) =>
        Assert.Equal(expected, NameRules.ShapeProblem(name));

    /// <summary>The tests cannot reference the login server, so this reads its source: the create screen's
    /// name gate must run the shared rule and keep no length rule of its own. Otherwise the login server
    /// could go back to its own 12 with every test above still green.</summary>
    [Fact]
    public void TheLoginServerNameGateRunsTheSharedRule()
    {
        var src = File.ReadAllText(RepoFile("LoginServer/LoginSession.cs"));
        var fn = Regex.Match(src, @"private static string\? NameProblem\(string name\).*?\n    \}", RegexOptions.Singleline);
        Assert.True(fn.Success, "could not find LoginSession.NameProblem to check its rule");
        Assert.Contains("NameRules.ShapeProblem(name)", fn.Value);
        Assert.DoesNotContain(".Length", fn.Value);
        Assert.DoesNotMatch(@"const\s+int\s+\w*NameLength", src);
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
