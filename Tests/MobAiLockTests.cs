using System.Text.RegularExpressions;
using Xunit;

namespace Tests;

/// <summary>The world lock owns shared-mob AI mutations; Session code must enter through World.</summary>
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
        @"\bmob\.Hp\s*(?:[+\-*/%&|^]?=(?!=)|\+\+|--)", RegexOptions.Compiled);

    [Fact]
    public void SessionFilesDoNotMutateWorldMobAiState()
    {
        DirectoryInfo root = RepoRoot();
        string serverDir = Path.Combine(root.FullName, "Server");
        var violations = new List<string>();

        foreach (string file in Directory.EnumerateFiles(serverDir, "Session*.cs").Order())
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
            "Session code mutates Mob state owned by World._lock; add a lock-owning World method instead:\n" +
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
