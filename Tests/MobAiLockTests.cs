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
/// The world lock owns shared-mob AI mutations; code outside <c>World</c> must enter through it.
///
/// <para>The scanned set is the files that hold a <c>Mob</c> they do not own: the session partials, which
/// reach world mobs through the handlers, <c>MobScript.cs</c>, whose <c>MobContext</c> hands a live mob to a
/// Lua hook that runs OUTSIDE <c>World._lock</c> by design, and <c>SuteAi.cs</c>, which is handed one by the
/// tick. <c>MobScript.cs</c> was added after this guard missed <c>MobContext.heal</c> writing
/// <c>_mob.Hp</c> from a hook: the file was not scanned, and the HP pattern matched the identifier
/// <c>mob</c> but not <c>_mob</c>.</para>
///
/// <para><b>Widening that pattern to "any identifier ending in mob" then broke it the other way, and the
/// #100 reviewer caught it.</b> <c>\b[A-Za-z_][A-Za-z0-9_]*[Mm]ob</c> requires at least one character
/// BEFORE the "mob", so it gained <c>_mob.Hp</c> and lost the bare <c>mob.Hp</c> that the original pattern
/// caught — the exemption below stopped being load-bearing, which is what should have given it away. The
/// prefix is optional now and the identifier boundary is a lookbehind rather than <c>\b</c> (which, sitting
/// before a letter, only ever asserted the boundary of the prefix). <c>mob.Hp</c>, <c>_mob.Hp</c>,
/// <c>dummyMob.Hp</c> and <c>h.mob.Hp</c> all match; the buff pattern, which carried the same hole from the
/// start, gets the same treatment.</para>
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
        @"(?<![A-Za-z0-9_])(?:[A-Za-z_][A-Za-z0-9_]*)?[Mm]ob[A-Za-z0-9_]*\.Buffs\.(?:Add|Remove|Clear)\s*\(",
        RegexOptions.Compiled);

    /// <summary>Whatever a creature is carrying is world state too, and it is mutated three ways: the
    /// <c>??=</c> that creates the list, a plain assignment, and the collection calls on it. Keyed on the
    /// field, not on the variable's name, for the reason in the class doc.</summary>
    private static readonly Regex HandedMutation = new(
        @"\.Handed\s*(?:\?\?=|=(?!=))|\.Handed\s*\)?\s*\.(?:Add|Remove|Clear)\s*\(",
        RegexOptions.Compiled);

    /// <summary>The receivers this codebase actually uses for a world <c>Mob</c> a Session does not own:
    /// anything ending in "mob" (<c>mob</c>, <c>_mob</c>, <c>wmob</c>, <c>dummyMob</c>, <c>h.mob</c>), and
    /// <c>node</c>, which is what <c>Session.Harvest</c> calls a harvest node. Listed EXPLICITLY rather than
    /// widened by heuristic: widening this pattern by shape is what dropped the bare <c>mob.Hp</c> in #100,
    /// and a new receiver name is a one-word edit here with a test run to prove it. The pattern is a
    /// tripwire with a documented reach, not a proof — what actually keeps these writes correct is that they
    /// go through World methods, so there is nothing left for it to catch.</summary>
    private static readonly Regex MobHpAssignment = new(
        @"(?<![A-Za-z0-9_])(?:(?:[A-Za-z_][A-Za-z0-9_]*)?[Mm]ob[A-Za-z0-9_]*|node)\.Hp\s*(?:[+\-*/%&|^]?=(?!=)|\+\+|--)",
        RegexOptions.Compiled);

    /// <summary>The files that handle a world mob without owning it. <c>Session*.cs</c> is every handler;
    /// <c>MobScript.cs</c> is the Lua host, the one place a mob is deliberately handed to code running
    /// outside the lock; <c>SuteAi.cs</c> is handed one by the tick. SuteAi's own <c>mob.Hp</c> write is
    /// already under <c>_lock</c> — its only caller is inside the tick's acquisition — but it is scanned so
    /// that stays true by construction rather than by the caller happening not to move.</summary>
    private static IEnumerable<string> ScannedFiles(string serverDir) =>
        Directory.EnumerateFiles(serverDir, "Session*.cs")
                 .Concat(Directory.EnumerateFiles(serverDir, "MobScript.cs"))
                 .Concat(Directory.EnumerateFiles(serverDir, "SuteAi.cs"))
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
                if (IsSuteHealUnderTheTickLock(name, line)) continue;

                if (AiFieldAssignment.IsMatch(line) || ThreatMutation.IsMatch(line) ||
                    MobBuffMutation.IsMatch(line) || MobHpAssignment.IsMatch(line) ||
                    HandedMutation.IsMatch(line))
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

    /// <summary>Sute's wounded self-heal writes a real world mob's HP — and is already inside the lock,
    /// because <c>SuteAi.TryHeal</c> has exactly one caller (<c>World.cs</c>, in the AI sweep) and that
    /// caller sits inside the tick's own <c>lock (_lock)</c>. Verified by reading both, not assumed.
    ///
    /// <para>Exempting one exact line rather than the file is the point: <c>SuteAi.cs</c> is scanned so that
    /// the next write added to it has to justify itself the same way, instead of the file staying invisible
    /// to the guard because one line in it happens to be fine.</para></summary>
    private static bool IsSuteHealUnderTheTickLock(string file, string line)
    {
        return file == "SuteAi.cs" && line.Trim() == "mob.Hp = Math.Min(mob.MaxHp, mob.Hp + HealAmount);";
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
