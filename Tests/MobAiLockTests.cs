using System.Text.RegularExpressions;
using Xunit;

namespace Tests;

/// <summary>
/// The world lock owns shared-mob AI mutations; code outside <c>World</c> must enter through it.
///
/// <para>The scanned set is the files that hold a <c>Mob</c> they do not own: the session partials, which
/// reach world mobs through the handlers, and <c>MobScript.cs</c>, whose <c>MobContext</c> hands a live mob
/// to a Lua hook that runs OUTSIDE <c>World._lock</c> by design. <c>MobScript.cs</c> was added to the set
/// after this guard missed <c>MobContext.heal</c> writing <c>_mob.Hp</c> from a hook: the file was not
/// scanned, and the HP pattern only matched the identifier <c>mob</c>, not <c>_mob</c>. Both holes are
/// closed here, and the HP pattern now matches any identifier ending in "mob" — which is what the buff
/// pattern below has always done.</para>
/// </summary>
public class MobAiLockTests
{
    private static readonly Regex AiFieldAssignment = new(
        @"\.(?:OwnerId|PetExpiresAt|TargetId|Summoned|AmnesiaBy|AmnesiaUntil|DamageAmp|DamageAmpUntil|FrozenUntil)\s*(?:[+\-*/%&|^]?=(?!=)|\+\+|--)",
        RegexOptions.Compiled);

    private static readonly Regex ThreatMutation = new(
        @"\.(?:ClearThreat|AddThreat)\s*\(", RegexOptions.Compiled);

    private static readonly Regex MobBuffMutation = new(
        @"\b[A-Za-z_][A-Za-z0-9_]*[Mm]ob[A-Za-z0-9_]*\.Buffs\.(?:Add|Remove|Clear)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex MobHpAssignment = new(
        @"\b[A-Za-z_][A-Za-z0-9_]*[Mm]ob[A-Za-z0-9_]*\.Hp\s*(?:[+\-*/%&|^]?=(?!=)|\+\+|--)",
        RegexOptions.Compiled);

    /// <summary>The files that handle a world mob without owning it. <c>Session*.cs</c> is every handler;
    /// <c>MobScript.cs</c> is the Lua host, the one place a mob is deliberately handed to code running
    /// outside the lock.</summary>
    private static IEnumerable<string> ScannedFiles(string serverDir) =>
        Directory.EnumerateFiles(serverDir, "Session*.cs")
                 .Concat(Directory.EnumerateFiles(serverDir, "MobScript.cs"))
                 .Order();

    [Fact]
    public void SessionAndScriptFilesDoNotMutateWorldMobAiState()
    {
        DirectoryInfo root = RepoRoot();
        string serverDir = Path.Combine(root.FullName, "Server");
        var violations = new List<string>();

        foreach (string file in ScannedFiles(serverDir))
        {
            string name = Path.GetFileName(file);
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (IsSessionLocalDebugDummyDamage(name, line)) continue;

                if (AiFieldAssignment.IsMatch(line) || ThreatMutation.IsMatch(line) ||
                    MobBuffMutation.IsMatch(line) || MobHpAssignment.IsMatch(line))
                    violations.Add($"{name}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(violations.Count == 0,
            "Code outside World mutates Mob state owned by World._lock; add a lock-owning World method and " +
            "call that instead — World.HealMobFromScript is what MobContext.heal does:\n" +
            string.Join('\n', violations));
    }

    private static bool IsSessionLocalDebugDummyDamage(string file, string line)
    {
        // This `mob` came from Session._mobs: it is a session-local debug dummy, never a world mob.
        return file == "Session.Combat.cs" && line.Trim() == "mob.Hp -= dummyDmg;";
    }

    private static DirectoryInfo RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Project1998.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!;
    }
}
