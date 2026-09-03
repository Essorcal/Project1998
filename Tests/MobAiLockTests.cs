using System.Text.RegularExpressions;
using Xunit;

namespace Tests;

/// <summary>
/// The world lock owns shared-mob AI mutations; Session code must enter through World.
///
/// <para>Two more field families joined the list for #103, and they are worth naming because of HOW they got
/// past it. <c>Mob.Handed</c> — the items a creature is carrying — was mutated by
/// <c>Session.Social.GiveItemToMob</c> through <c>(mob.Handed ??= new()).Add(...)</c>, which no pattern here
/// described: the buff pattern knows about <c>.Buffs.Add</c> and nothing knew about <c>.Handed</c> at all.
/// And <c>Session.Harvest</c> wrote a world mob's claim fields under the name <c>node</c>, so the
/// identifier-shaped HP pattern could never have seen it — which is the argument for
/// <see cref="AiFieldAssignment"/> being keyed on the FIELD rather than on what the variable is called.
/// <c>HarvestClaimBy</c> and <c>HarvestClaimUntil</c> are unique to harvest nodes, so keying on them costs no
/// false positives and catches the site whatever the local is named.</para>
/// </summary>
public class MobAiLockTests
{
    private static readonly Regex AiFieldAssignment = new(
        @"\.(?:OwnerId|PetExpiresAt|TargetId|Summoned|AmnesiaBy|AmnesiaUntil|DamageAmp|DamageAmpUntil|FrozenUntil" +
        @"|HarvestClaimBy|HarvestClaimUntil)\s*(?:[+\-*/%&|^]?=(?!=)|\+\+|--)",
        RegexOptions.Compiled);

    private static readonly Regex ThreatMutation = new(
        @"\.(?:ClearThreat|AddThreat)\s*\(", RegexOptions.Compiled);

    private static readonly Regex MobBuffMutation = new(
        @"\b[A-Za-z_][A-Za-z0-9_]*[Mm]ob[A-Za-z0-9_]*\.Buffs\.(?:Add|Remove|Clear)\s*\(",
        RegexOptions.Compiled);

    /// <summary>Whatever a creature is carrying is world state too, and it is mutated three ways: the
    /// <c>??=</c> that creates the list, a plain assignment, and the collection calls on it. Keyed on the
    /// field, not on the variable's name, for the reason in the class doc.</summary>
    private static readonly Regex HandedMutation = new(
        @"\.Handed\s*(?:\?\?=|=(?!=))|\.Handed\s*\)?\s*\.(?:Add|Remove|Clear)\s*\(",
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
                    MobBuffMutation.IsMatch(line) || MobHpAssignment.IsMatch(line) ||
                    HandedMutation.IsMatch(line))
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
