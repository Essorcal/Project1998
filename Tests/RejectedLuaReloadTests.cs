using System.Reflection;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// #113: a Lua edit that does not take is named by <c>@reload</c> while the previous program keeps running, and
/// a first load, which has no previous program, keeps the behaviour and the message it always had.
///
/// <para><b>The defect.</b> Each host's <c>PrepareReload</c> gives two answers: the candidate (null when the file
/// is missing, does not compile, or defines the wrong globals) and whether the host is live afterwards.
/// <c>Content.Load</c>'s <c>Script</c> helper read the second as if it were the first. On a reload the previous
/// program is always live, so a rejected file never reached <see cref="Content.RejectedScripts"/>, the load
/// report counted it as loaded, and the REJECTED banner <see cref="Content.Reload"/> leads with never fired,
/// while the host's own log line said the candidate was refused: "reload REJECTED" for a compile error or a
/// wrong global, "no file at ... keeping the previously-loaded ..." for a missing file. Only a first load,
/// with nothing live, was named.</para>
///
/// <para><b>A missing file counts as rejected.</b> Whoever deleted or misnamed it is still running the
/// previous program, which is what the banner says, and at startup a missing script was already named.</para>
///
/// <para><b>Isolation.</b> Every fact here replaces process-wide content, so the class shares
/// <c>"tile-translation"</c> with <see cref="ContentReloadTests"/> and <see cref="ContentSmokeTests"/> and holds
/// <see cref="TestProcessState.Gate"/> from the first path override to the restoring load, as they do. A "good"
/// script is always the SHIPPED file plus one probe entry, so a collection running beside this one keeps every
/// real verb, dialog and hook while a probe program is live; a broken candidate is never installed at all. The
/// first-load facts take one host's previous program away by reflection, inside the shared Lua gate. Every
/// reader of those four fields takes the same gate, so no other thread can see the host without its program,
/// and the field is put back before the gate is released.</para>
/// </summary>
[Collection("tile-translation")]
public class RejectedLuaReloadTests
{
    public enum Host { SpellVerbs, ItemVerbs, NpcDialog, MobAi }

    public enum Breakage { CompileError, WrongShape, MissingFile }

    public static TheoryData<Host, Breakage> EveryHostAndBreakage()
    {
        var data = new TheoryData<Host, Breakage>();
        foreach (var host in Enum.GetValues<Host>())
            foreach (var breakage in Enum.GetValues<Breakage>())
                data.Add(host, breakage);
        return data;
    }

    /// <summary>The key every probe entry is filed under. No real verb, NPC or creature has it.</summary>
    private const string ProbeKey = "rejected_reload_probe";

    /// <summary>One Lua host as these facts drive it: its content entry, the global its file must define, the
    /// probe entry a good candidate appends to the shipped file and how to ask whether it is live, the host's
    /// own <c>PrepareReload</c> reduced to its two answers, the field that holds its previous program, and the
    /// pieces of the log lines it writes when a candidate does not take.</summary>
    private sealed record HostCase(
        Content.TableId Table,
        string File,
        string Global,
        string Probe,
        Func<bool> ProbeIsLive,
        Func<string, (bool Live, bool Took)> Prepare,
        Func<(object? Owner, FieldInfo Field)> PreviousProgram,
        string MissingPrefix,
        string KeepsPrevious,
        string NoPrevious,
        string WrongShape,
        string Rejected)
    {
        /// <summary>The file name, so an <c>Assert.All</c> failure names the host instead of printing every member.</summary>
        public override string ToString() => File;
    }

    private static HostCase Case(Host host) => host switch
    {
        Host.SpellVerbs => new(Content.TableId.SpellVerbs, "spell_verbs.lua", "verbs",
            $"verbs.{ProbeKey} = function(ctx, row) return true end",
            () => SpellScript.HasVerb(ProbeKey),
            path => { var (live, prepared) = SpellScript.PrepareReload(path); return (live, prepared is not null); },
            () => VerbHostProgram(typeof(SpellScript)),
            "spell_verbs.lua: no verb file at '", "keeping the previously-loaded verbs", "keeping the Lua path disabled",
            "spell_verbs.lua defines no global `verbs` table", "reload REJECTED, keeping the previous verbs"),
        Host.ItemVerbs => new(Content.TableId.ItemVerbs, "item_verbs.lua", "verbs",
            $"verbs.{ProbeKey} = function(ctx, row) return true end",
            () => ItemScript.HasVerb(ProbeKey),
            path => { var (live, prepared) = ItemScript.PrepareReload(path); return (live, prepared is not null); },
            () => VerbHostProgram(typeof(ItemScript)),
            "item_verbs.lua: no verb file at '", "keeping the previously-loaded verbs", "keeping the Lua path disabled",
            "item_verbs.lua defines no global `verbs` table", "reload REJECTED, keeping the previous verbs"),
        Host.NpcDialog => new(Content.TableId.NpcDialog, "npc_dialog.lua", "npcs",
            $"npcs.{ProbeKey} = function(ctx) end",
            () => NpcScript.Has(ProbeKey),
            path => { var (live, prepared) = NpcScript.PrepareReload(path); return (live, prepared is not null); },
            () => StaticProgram(typeof(NpcScript), "_npcs"),
            "npc_dialog.lua: no file at '", "keeping the previously-loaded dialogs", "keeping the Lua NPC path disabled",
            "npc_dialog.lua missing global `npcs` table or `__make_ctx`", "reload REJECTED, keeping the previous dialogs"),
        Host.MobAi => new(Content.TableId.MobAi, "mob_ai.lua", "mobs",
            $"mobs.{ProbeKey} = {{ on_spawn = function(ctx) end }}",
            () => MobScript.Has(ProbeKey, MobScript.OnSpawn),
            path => { var (live, prepared) = MobScript.PrepareReload(path); return (live, prepared is not null); },
            () => StaticProgram(typeof(MobScript), "_mobs"),
            "mob_ai.lua: no file at '", "keeping the previously-loaded hooks", "Lua mob hooks disabled",
            "mob_ai.lua missing global `mobs` table", "reload REJECTED, keeping the previous hooks"),
        _ => throw new ArgumentOutOfRangeException(nameof(host)),
    };

    /// <summary>Acceptance 1 and 2. A good script is loaded first, then a reload brings a candidate that does
    /// not compile, defines the wrong global, or is not there. The previous program, probe and all, keeps
    /// running, the host logs the rejection, and its own answer is the one that misled the reader: live, with
    /// no candidate. The edit did not take, so the file is named in <see cref="Content.RejectedScripts"/>, the
    /// reply leads with the REJECTED banner and the load report says 1/0.</summary>
    [Theory]
    [MemberData(nameof(EveryHostAndBreakage))]
    public void RejectedCandidateIsNamedWhileThePreviousProgramRuns(Host host, Breakage breakage)
    {
        var c = Case(host);
        lock (TestProcessState.Gate)
        {
            string dir = ScratchDir();
            var original = Content.Spec(c.Table);
            try
            {
                Content.ReplaceSpecForTests(c.Table, original with { PathOverride = Good(dir, c) });
                TestProcessState.LoadContent();
                Assert.Empty(Content.RejectedScripts);
                Assert.True(c.ProbeIsLive(), $"{c.File}: the good candidate did not go live");

                string broken = Broken(dir, c, breakage);
                Content.ReplaceSpecForTests(c.Table, original with { PathOverride = broken });
                using var log = LogLineSink.Acquire();

                string reply = Content.Reload();

                Assert.True(c.ProbeIsLive(), $"{c.File}: the previous program stopped running");
                ExpectHostMessage(log, c, breakage, broken, previous: true);
                Assert.Equal((true, false), c.Prepare(broken));
                Assert.Multiple(
                    () => Assert.Equal(new[] { c.File }, Content.RejectedScripts),
                    () => Assert.StartsWith(Banner(c.File), reply),
                    () => AssertReportedRejected(c, breakage, broken));
            }
            finally
            {
                Content.ReplaceSpecForTests(c.Table, original);
                TestProcessState.LoadContent();
                DeleteScratch(dir);
            }
        }
    }

    /// <summary>Acceptance 3. At startup there is no previous program, and a broken script there still does
    /// what it always did: <c>Content.Load</c> names it in <see cref="Content.RejectedScripts"/> (which is how
    /// <c>ContentSmokeTests.LuaScriptsAllCompile</c> catches a shipped script that would be dead on arrival), the
    /// load report says 1/0, nothing is installed so the Lua path stays off, the host answers "not live, no
    /// candidate", and a missing file is logged with the first-load wording ("disabled"), not the reload
    /// wording ("keeping the previously-loaded ...").</summary>
    [Theory]
    [MemberData(nameof(EveryHostAndBreakage))]
    public void FirstLoadWithNoPreviousProgramKeepsItsBehaviourAndMessage(Host host, Breakage breakage)
    {
        var c = Case(host);
        lock (TestProcessState.Gate)
        {
            string dir = ScratchDir();
            var original = Content.Spec(c.Table);
            try
            {
                string broken = Broken(dir, c, breakage);
                Content.ReplaceSpecForTests(c.Table, original with { PathOverride = broken });
                var (owner, field) = c.PreviousProgram();
                using var log = LogLineSink.Acquire();
                using (Session.EnterScriptGate())
                {
                    object? previous = field.GetValue(owner);
                    field.SetValue(owner, null);
                    try
                    {
                        TestProcessState.LoadContent();   // Content.Load, as Program.cs runs it at startup

                        Assert.Equal(new[] { c.File }, Content.RejectedScripts);
                        AssertReportedRejected(c, breakage, broken);
                        Assert.Null(field.GetValue(owner));
                        Assert.Equal((false, false), c.Prepare(broken));
                        ExpectHostMessage(log, c, breakage, broken, previous: false);
                    }
                    finally { field.SetValue(owner, previous); }
                }
            }
            finally
            {
                Content.ReplaceSpecForTests(c.Table, original);
                TestProcessState.LoadContent();
                DeleteScratch(dir);
            }
        }
    }

    /// <summary>Acceptance 4. A reload whose four candidates all take names nothing and has no banner, and each
    /// candidate is installed at the publish boundary: none of the probes is live when the loader reaches
    /// <c>BeforePublish</c>, and all four are once <see cref="Content.Reload"/> returns.</summary>
    [Fact]
    public void GoodReloadNamesNothingAndCommitsAtThePublishBoundary()
    {
        var cases = Enum.GetValues<Host>().Select(Case).ToArray();
        lock (TestProcessState.Gate)
        {
            string dir = ScratchDir();
            var originals = cases.Select(c => Content.Spec(c.Table)).ToArray();
            bool[]? atPublish = null;
            try
            {
                TestProcessState.LoadContent();
                Assert.All(cases, c => Assert.False(c.ProbeIsLive(), $"{c.File}: a probe is live before the reload"));
                for (int i = 0; i < cases.Length; i++)
                    Content.ReplaceSpecForTests(cases[i].Table, originals[i] with { PathOverride = Good(dir, cases[i]) });
                Content.LoadStepForTests = step =>
                {
                    if (step == "BeforePublish") atPublish = cases.Select(c => c.ProbeIsLive()).ToArray();
                };

                string reply = Content.Reload();

                Assert.Empty(Content.RejectedScripts);
                Assert.DoesNotContain("REJECTED", reply);
                Assert.Equal(new[] { false, false, false, false }, atPublish);
                Assert.All(cases, c => Assert.True(c.ProbeIsLive(), $"{c.File}: the accepted candidate was not installed"));
                Assert.All(cases, c => Assert.Equal(1, Assert.IsType<TableLoad>(Content.LoadReport[c.File]).Kept));
            }
            finally
            {
                Content.LoadStepForTests = null;
                for (int i = 0; i < cases.Length; i++) Content.ReplaceSpecForTests(cases[i].Table, originals[i]);
                TestProcessState.LoadContent();
                DeleteScratch(dir);
            }
        }
    }

    /// <summary>Acceptance 4, per file: one rejected candidate does not hold back the other three. mob_ai.lua
    /// does not compile; the other three candidates still take and are installed, and only mob_ai.lua is
    /// named.</summary>
    [Fact]
    public void RejectedScriptDoesNotHoldBackTheOtherCommits()
    {
        var rejected = Case(Host.MobAi);
        var accepted = new[] { Host.SpellVerbs, Host.ItemVerbs, Host.NpcDialog }.Select(Case).ToArray();
        lock (TestProcessState.Gate)
        {
            string dir = ScratchDir();
            var rejectedOriginal = Content.Spec(rejected.Table);
            var acceptedOriginals = accepted.Select(c => Content.Spec(c.Table)).ToArray();
            try
            {
                TestProcessState.LoadContent();
                Content.ReplaceSpecForTests(rejected.Table,
                    rejectedOriginal with { PathOverride = Broken(dir, rejected, Breakage.CompileError) });
                for (int i = 0; i < accepted.Length; i++)
                    Content.ReplaceSpecForTests(accepted[i].Table,
                        acceptedOriginals[i] with { PathOverride = Good(dir, accepted[i]) });

                string reply = Content.Reload();

                Assert.Equal(new[] { rejected.File }, Content.RejectedScripts);
                Assert.StartsWith(Banner(rejected.File), reply);
                Assert.All(accepted, c => Assert.True(c.ProbeIsLive(), $"{c.File}: the accepted candidate was not installed"));
            }
            finally
            {
                Content.ReplaceSpecForTests(rejected.Table, rejectedOriginal);
                for (int i = 0; i < accepted.Length; i++)
                    Content.ReplaceSpecForTests(accepted[i].Table, acceptedOriginals[i]);
                TestProcessState.LoadContent();
                DeleteScratch(dir);
            }
        }
    }

    private static string Banner(string file) => $"*** REJECTED (still running the previous version, see log): {file} *** — ";

    /// <summary>The shipped file plus the probe entry: a script that takes, and whose program can be told apart
    /// from the shipped one.</summary>
    private static string Good(string dir, HostCase c)
    {
        string path = Path.Combine(dir, $"good-{c.File}");
        File.WriteAllText(path, File.ReadAllText(RepoPaths.GameData(c.File)) + "\n" + c.Probe + "\n");
        return path;
    }

    /// <summary>A candidate that does not take: it does not compile, it compiles but defines the wrong global,
    /// or nothing is there at all.</summary>
    private static string Broken(string dir, HostCase c, Breakage breakage)
    {
        string path = Path.Combine(dir, $"{breakage}-{c.File}");
        if (breakage == Breakage.CompileError) File.WriteAllText(path, $"{c.Global} = {{ this is not lua ===\n");
        else if (breakage == Breakage.WrongShape) File.WriteAllText(path, "not_the_expected_global = {}\n");
        return path;
    }

    /// <summary>The load report's entry for a candidate that did not take: 1 read, 0 kept, and the problem line
    /// <c>TableLoad</c> writes for a script.</summary>
    private static void AssertReportedRejected(HostCase c, Breakage breakage, string path)
    {
        var entry = Assert.IsType<TableLoad>(Content.LoadReport[c.File]);
        Assert.Equal(0, entry.Kept);
        Assert.Equal(breakage == Breakage.MissingFile
                ? $"{c.File}: FILE NOT FOUND ({path}) — nothing loaded"
                : $"{c.File}: REJECTED — the previously loaded version is still running (the compile error is above)",
            entry.Problem);
    }

    /// <summary>The host's own line for the rejection. Only a missing file is worded differently with and
    /// without a previous program; the compile and shape lines read the same either way.</summary>
    private static void ExpectHostMessage(LogLineSink log, HostCase c, Breakage breakage, string path, bool previous)
    {
        if (breakage == Breakage.MissingFile)
            log.LineContaining($"{c.MissingPrefix}{path}' — {(previous ? c.KeepsPrevious : c.NoPrevious)}");
        else if (breakage == Breakage.WrongShape)
            log.LineContaining($"{c.WrongShape} — {c.Rejected}");
        else
            Assert.Contains($"— {c.Rejected}", log.LineContaining($"{c.File} load failed: "));
    }

    /// <summary>The verb host behind <paramref name="wrapper"/> and the field it keeps its program's verb table
    /// in — what <c>PrepareReload</c> reads to tell a first load from a reload.</summary>
    private static (object? Owner, FieldInfo Field) VerbHostProgram(Type wrapper)
    {
        object? host = wrapper.GetField("_host", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        FieldInfo? verbs = typeof(LuaVerbHost).GetField("_verbs", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(host);
        Assert.NotNull(verbs);
        return (host, verbs);
    }

    /// <summary>A static host's program field, which its <c>PrepareReload</c> reads the same way.</summary>
    private static (object? Owner, FieldInfo Field) StaticProgram(Type host, string name)
    {
        FieldInfo? field = host.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return (null, field);
    }

    private static string ScratchDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "project1998-rejected-lua-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteScratch(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup of a test fixture */ }
    }
}
