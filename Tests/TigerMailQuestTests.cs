using System.Collections.Generic;
using System.Linq;
using Server;
using Xunit;

namespace Tests;

/// <summary>
/// The Tiger Mail chain (see <see cref="TigerMailQuest"/>) is a ladder of Items.csv keys stitched together by
/// one C# ability and one CSV composition row, and every join in it fails SILENTLY: a renamed item key reads
/// as "the player doesn't have it", a dropped ability row reads as "Claw doesn't answer", and neither breaks a
/// build or logs anything. These pin the joins, plus the three places where this port deliberately departs
/// from RTK (start level, second catalyst, the exp charge actually happening).
/// </summary>
public class TigerMailQuestTests
{
    private static readonly object _gate = new();
    private static bool _loaded;

    private static void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            TestProcessState.LoadContent();
            _loaded = true;
        }
    }

    private static readonly int[] Sexes = { 0, 1 };   // 0 male, 1 female — Session.CharSex / RTK player.sex

    /// <summary>The whole quest is spoken, so the composition row IS its reachability: with it gone Claw still
    /// stands there and "chongun" simply goes unanswered, with nothing anywhere to say why.</summary>
    [Fact]
    public void ClawAnswersToChongun()
    {
        EnsureLoaded();

        var claw = Content.Npcs.FirstOrDefault(n => n.Id == TigerMailQuest.ClawNpcId);
        Assert.NotNull(claw);
        Assert.Equal("ClawNpc", claw!.Key);
        Assert.Equal(TigerMailQuest.ChonsaDenMap, claw.Map);

        var abilities = NpcScripts.For(claw);
        Assert.Contains(abilities, a => a is TigerMailAbility);
        // …and by EAR specifically — the say dispatcher only ever looks at INpcSayHandler.
        Assert.Contains(abilities.OfType<INpcSayHandler>(), h => h is TigerMailAbility);
    }

    /// <summary>Claw's identifier is his alone, which is why the ability needs no in-code NPC gate the way the
    /// Sute quest's does. If a second NPC ever takes the identifier, this fails and the gate becomes required.</summary>
    [Fact]
    public void ClawIdentifierIsNotShared()
    {
        EnsureLoaded();
        Assert.Single(Content.Npcs, n => n.Key == "ClawNpc");
    }

    /// <summary>Chonsa Den has to be enterable and Claw has to be within earshot of where you land, or the
    /// quest is unreachable however well the dialog works. The two mouth tiles are on Buya's east side at
    /// 127-128/120, which is the location the period tutor post gives ("cords 0128/0120").</summary>
    [Fact]
    public void ChonsaDenIsReachableAndClawIsWithinEarshot()
    {
        EnsureLoaded();

        var inbound = Content.Warps.Where(w => w.Value.m == TigerMailQuest.ChonsaDenMap).ToList();
        Assert.NotEmpty(inbound);
        Assert.All(inbound, w => Assert.Equal(330, w.Key.m));                    // from Buya, and only Buya
        Assert.Contains(inbound, w => w.Key.x == 128 && w.Key.y == 120);         // the tutor post's "cords 0128/0120"

        var claw = Content.Npcs.Single(n => n.Id == TigerMailQuest.ClawNpcId);
        foreach (var (_, dest) in inbound)
        {
            int d = System.Math.Max(System.Math.Abs(claw.X - dest.x), System.Math.Abs(claw.Y - dest.y));
            Assert.True(d <= Content.SpeechRange,
                        $"landing on ({dest.x},{dest.y}) puts Claw {d} tiles away — out of speech range ({Content.SpeechRange})");
        }

        // And a way back out, or the den is a trap.
        Assert.Contains(Content.Warps, w => w.Key.m == TigerMailQuest.ChonsaDenMap && w.Value.m == 330);
    }

    /// <summary>Every key the ladder names must resolve. This is the cheap guard that catches a renamed or
    /// deleted Items.csv row, which otherwise surfaces only as a player who can never satisfy a rung.</summary>
    [Fact]
    public void EveryLadderItemExists()
    {
        EnsureLoaded();

        foreach (var rung in TigerMailQuest.Ladder)
        foreach (int sex in Sexes)
        {
            foreach (var key in rung.Sacrifices(sex))
                Assert.True(Content.ItemByKey(key) is not null, $"rung {rung.Level} sacrifice '{key}' is not in Items.csv");
            Assert.True(Content.ItemByKey(rung.Armor(sex)) is not null,
                        $"rung {rung.Level} reward '{rung.Armor(sex)}' is not in Items.csv");
        }
    }

    /// <summary>The ladder must actually be a ladder: each rung hands back the armor the rung before it gave,
    /// and the stage values chain 0 -> 10 -> … -> 60 -> Done with no gap. A break here silently strands a
    /// player at whichever rung stopped pointing at the next one.</summary>
    [Fact]
    public void LadderChainsRungToRung()
    {
        EnsureLoaded();

        Assert.Equal(7, TigerMailQuest.Ladder.Length);
        Assert.Equal(0, TigerMailQuest.Ladder[0].Level);
        Assert.Equal(TigerMailQuest.Done, TigerMailQuest.Ladder[^1].Next);
        Assert.Null(TigerMailQuest.RungAt(TigerMailQuest.Done));   // finished reads as "no rung", not rung 0

        for (int i = 0; i < TigerMailQuest.Ladder.Length; i++)
        {
            var rung = TigerMailQuest.Ladder[i];
            Assert.Same(rung, TigerMailQuest.RungAt(rung.Level));

            if (i == 0)
            {
                foreach (int sex in Sexes) Assert.Equal("", rung.Prev(sex));   // the opener asks for two items
                continue;
            }

            var before = TigerMailQuest.Ladder[i - 1];
            Assert.Equal(rung.Level, before.Next);
            foreach (int sex in Sexes)
                Assert.Equal(before.Armor(sex), rung.Prev(sex));
        }
    }

    /// <summary>Each rung's reward and the war platemail / war dress it is made from must be the same tier —
    /// Items.csv pairs them by wear level and by worn sprite (Autumn war dress 35502 and Royal tigress 42015
    /// are both level 16, look 206). That pairing is what makes the male and female halves of a rung equivalent,
    /// and it is the check that would have caught the "Autumn tigress" naming in the nexusatlas walkthrough.</summary>
    [Fact]
    public void EachRungPairsItsBaseArmorWithItsReward()
    {
        EnsureLoaded();

        foreach (var rung in TigerMailQuest.Ladder)
        foreach (int sex in Sexes)
        {
            var basis  = Content.ItemByKey(rung.Base(sex))!;
            var reward = Content.ItemByKey(rung.Armor(sex))!;

            // Same wear tier and same worn sprite. One documented exception on the tier: the peasant
            // opener's base armors are level 0 ("For peasants") while the Tiger mail / Tigress they become
            // appraise at 1 — nexusatlas' "your tiger mail for peasant level".
            Assert.Equal(basis.Level == 0 ? 1 : basis.Level, (int)reward.Level);
            Assert.Equal(basis.Look, reward.Look);
            Assert.Equal(4, reward.Type);                       // ITM_ARMOR
            Assert.Equal(sex == 1 ? 1 : 0, reward.Sex);         // female rewards are sex-locked; male rows are 0

            // The tiger armor is never WORSE armor — but its AC is in fact identical to the war platemail /
            // dress it is made from at every rung. What the sacrifice actually buys is vitality (and a point
            // or two of hit), so that is what gets pinned; an "AC upgrade" assertion here would be false.
            Assert.True(reward.Armor <= basis.Armor,
                        $"{reward.Key} (AC {reward.Armor}) is worse armor than {basis.Key} (AC {basis.Armor})");
            Assert.True(reward.Vita > basis.Vita,
                        $"{reward.Key} adds no vitality over {basis.Key} — the sacrifice buys nothing");
        }
    }

    /// <summary>Rung levels and reward wear-levels are what the period sources are most specific about, so they
    /// are pinned literally: rungs at 6/10/20/30/40/50/60 (nexusatlas' "Return at level N"), rewards wearable at
    /// 1/6/16/26/36/46/56 (KoyaSoto's "questable at each 10th level … wearable at the 6th levels").</summary>
    [Fact]
    public void RungAndWearLevelsMatchThePeriodSources()
    {
        EnsureLoaded();

        Assert.Equal(new[] { 6, 10, 20, 30, 40, 50, 60 },
                     TigerMailQuest.Ladder.Select(TigerMailQuest.LevelFor).ToArray());
        // The first rung is the one place the stored stage (0) is not the level gate.
        Assert.Equal(TigerMailQuest.MinLevel, TigerMailQuest.LevelFor(TigerMailQuest.Ladder[0]));
        Assert.Equal(6, TigerMailQuest.MinLevel);   // 6, not RTK's 5 — nexusatlas + KoyaSoto against claw.lua

        foreach (int sex in Sexes)
            Assert.Equal(new[] { 1, 6, 16, 26, 36, 46, 56 },
                         TigerMailQuest.Ladder.Select(r => (int)Content.ItemByKey(r.Armor(sex))!.Level).ToArray());
    }

    /// <summary>The experience sacrifice, pinned to Head Tutor KoyaSoto's TNL table (which RTK's seven
    /// constants match exactly). These are the only numbers in the quest no other source can be derived
    /// from, so a silent edit here would be unrecoverable.</summary>
    [Fact]
    public void ExperienceCostsMatchTheTutorTable()
    {
        Assert.Equal(new uint[] { 664, 2556, 11200, 34784, 70344, 178032, 428544 },
                     TigerMailQuest.Ladder.Select(r => r.ExpCost).ToArray());
    }

    /// <summary>The second rung's catalyst is the one place this port picks a side against the nexusatlas
    /// snapshot it was checked against. Pinned in both directions: it is the gold acorn (KoyaSoto + RTK +
    /// every nexusatlas capture from 2007-10 on), and the mountain ginseng the earlier captures name is a
    /// real item too — so the choice stays a choice, not an accident of what existed.</summary>
    [Fact]
    public void SecondRungCatalystIsTheGoldAcorn()
    {
        EnsureLoaded();

        Assert.Equal("gold_acorn", TigerMailQuest.RungAt(10)!.Catalyst);
        Assert.NotNull(Content.ItemByKey("gold_acorn"));
        Assert.NotNull(Content.ItemByKey("mountain_ginseng"));
    }

    /// <summary>Harden Armor lands on the <b>Jade and Blood rungs only</b>. nexusatlas records the cast under
    /// exactly those two steps, RTK casts nothing anywhere, and an earlier pass here wrongly generalised it to
    /// all seven — so the split is pinned in both directions rather than left as a comment.</summary>
    [Fact]
    public void HardenArmorIsCastOnTheJadeAndBloodRungsOnly()
    {
        EnsureLoaded();

        var hardens = TigerMailQuest.Ladder.Where(r => r.Harden).ToList();
        Assert.Equal(2, hardens.Count);
        Assert.Equal(new[] { "jade_tiger_mail", "blood_tiger_mail" }, hardens.Select(r => r.MaleArmor).ToArray());
        Assert.Equal(new[] { 10, 50 }, hardens.Select(r => r.Level).ToArray());
    }

    /// <summary>The female ladder is named for the SEASONS where the male one is named for minerals — Summer /
    /// Autumn / Winter tigress against Jade / Royal / Sky tiger mail — which is how every other female warrior
    /// line in Items.csv already reads (war dress, mail dress). RTK named the female tigress rungs after the
    /// male ones; tswolf's 2001 archive and nexusatlas' own warriorarmor-old.php both say otherwise, so
    /// Items.csv 42014-42016 carry the seasonal names while the KEYS stay RTK's. Pins the display names,
    /// because they are the half a data edit can silently undo.</summary>
    [Fact]
    public void FemaleRungsCarryTheSeasonalNames()
    {
        EnsureLoaded();

        Assert.Equal(new[] { "Tigress", "Summer tigress", "Autumn tigress", "Winter tigress",
                             "Ancient tigress", "Blood tigress", "Earth tigress" },
                     TigerMailQuest.Ladder.Select(r => Content.ItemByKey(r.FemaleArmor)!.Name).ToArray());

        // The male half stays mineral, and each pair's SEASON matches the war dress it is transmuted from —
        // which is the same pairing warriorarmor-old.php spells out (Jade tiger mail <-> Summer tigress, …).
        Assert.Equal(new[] { "Tiger mail", "Jade tiger mail", "Royal tiger mail", "Sky tiger mail",
                             "Ancient tiger mail", "Blood tiger mail", "Earth tiger mail" },
                     TigerMailQuest.Ladder.Select(r => Content.ItemByKey(r.MaleArmor)!.Name).ToArray());

        foreach (var rung in TigerMailQuest.Ladder.Skip(1).Take(3))   // the three seasonal rungs
        {
            string season = Content.ItemByKey(rung.FemaleBase)!.Name.Split(' ')[0];        // "Summer" …
            Assert.StartsWith(season, Content.ItemByKey(rung.FemaleArmor)!.Name);
        }
    }

    /// <summary>Claw's Harden Armor has to resolve to a spell that <see cref="Session.NpcCastWard"/> can
    /// actually apply — a key with no SpellParams row, or one with no duration, makes the cast a silent no-op
    /// and nothing anywhere would say so. Pins the three fields the primitive reads.</summary>
    [Fact]
    public void HardenArmorIsACastableWard()
    {
        EnsureLoaded();

        var sp = Content.SpellByKey(TigerMailQuest.HardenSpell);
        Assert.NotNull(sp);
        Assert.Equal("Harden armor", sp!.Name);

        Assert.True(Content.SpellParams.TryGetValue(TigerMailQuest.HardenSpell, out var row));
        Assert.Equal("hardarmors", row!["category"]);
        Assert.Equal("armor", row["stat"]);
        Assert.True(int.TryParse(row["duration"], out int durMs) && durMs > 0);
        Assert.True(int.TryParse(row["amount"], out int amount) && amount < 0);   // AC delta, lower is better
        Assert.NotNull(Content.FxFor(sp));                                        // the cast is visible
    }

    /// <summary>The tutor briefs at exactly the level Claw will serve, and not a level earlier. These are two
    /// separate gates in two files, and RTK's literal 5 in the tutor branch was correct only because RTK's
    /// quest also started at 5 — moving the quest to 6 (see <see cref="TigerMailQuest.MinLevel"/>) without
    /// moving the briefing would send a level-5 Warrior across Buya to be told "Return when you have reached
    /// level 6." Nothing in the type system ties them, so this does.
    ///
    /// <para>Checked by reflection rather than by calling the branch: it is private, needs a live session, and
    /// what needs pinning is the CONSTANT it reads, not the dialog it prints.</para></summary>
    [Fact]
    public void TutorBriefsExactlyWhenClawWill()
    {
        EnsureLoaded();

        var src = System.IO.File.ReadAllText(RepoFile("Server/TutorialQuest.cs"));
        Assert.Contains("ctx.Level < TigerMailQuest.MinLevel", src);
        // …and specifically NOT a literal, which is what the Lua carries and what would silently drift.
        Assert.DoesNotContain("ctx.Level < 5) return false", src);

        // The push must read the same constant, or the two deliveries disagree about who qualifies.
        var push = System.IO.File.ReadAllText(RepoFile("Server/Session.CharacterApi.cs"));
        Assert.Contains("_char.Level < TigerMailQuest.MinLevel", push);

        // The gate the briefing points AT: the first rung is claimable at exactly MinLevel.
        Assert.Equal(TigerMailQuest.MinLevel, TigerMailQuest.LevelFor(TigerMailQuest.Ladder[0]));
    }

    /// <summary>Walk up from the repo root so the check above reads the real source however tests are hosted.</summary>
    private static string RepoFile(string relative)
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Project1998.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return System.IO.Path.Combine(dir!.FullName, relative);
    }

    /// <summary>The briefing has TWO deliveries — the level-up push and the tutor's click branch — and they
    /// must play the same words. Both read <see cref="TigerMailQuest.Briefing"/>, so this pins the script
    /// itself: seven pages in three blocks, with the middle block (and only the middle block) spoken in
    /// Claw's voice from the cave, and the last page naming the keyword the whole quest hangs on.</summary>
    [Fact]
    public void BriefingIsOneSharedScriptWithTheTigerSpeakingOnce()
    {
        var blocks = TigerMailQuest.Briefing;
        Assert.Equal(3, blocks.Length);
        Assert.Equal(new[] { false, true, false }, blocks.Select(b => b.Tiger).ToArray());
        Assert.Equal(7, blocks.Sum(b => b.Pages.Length));

        Assert.Single(blocks.Where(b => b.Tiger).Single().Pages);
        Assert.EndsWith("say Chongun.", blocks[^1].Pages[^1]);
        Assert.All(blocks.SelectMany(b => b.Pages), page => Assert.False(string.IsNullOrWhiteSpace(page)));
    }

    /// <summary>The tutor blocks until the player has BEEN to Claw — not until they have claimed a rung. The
    /// distinction is the whole reason <see cref="TigerMailQuest.MetClawReg"/> exists: keying the block on
    /// quest progress (which is what RTK does) strands a Warrior who reached Claw but cannot yet afford the
    /// first rung's ingredients, locked out of their own tutor with nothing left to do about it.</summary>
    [Fact]
    public void TutorBlockReleasesOnMeetingClawNotOnProgress()
    {
        var tutor = System.IO.File.ReadAllText(RepoFile("Server/TutorialQuest.cs"));
        Assert.Contains("TigerMailQuest.MetClawReg", tutor);
        // The block must NOT be keyed on the quest stage — that is RTK's condition and the one that strands.
        Assert.DoesNotContain("ctx.Stage(TigerMailQuest.QuestKey) == 0", tutor);

        // Claw stamps it BEFORE his level/ingredient checks, so arriving is enough. Pinned by position:
        // the flag has to be set ahead of the first gate that can return early.
        var claw = System.IO.File.ReadAllText(RepoFile("Server/TigerMailQuest.cs"));
        int stamp = claw.IndexOf("SetReg(TigerMailQuest.MetClawReg, 1)", System.StringComparison.Ordinal);
        int levelGate = claw.IndexOf("Return when you have reached level", System.StringComparison.Ordinal);
        Assert.True(stamp > 0 && levelGate > stamp,
                    "Claw must stamp MetClawReg before the level gate, or a too-young Warrior never clears the tutor block");
    }

    /// <summary>The tutor's briefing is the quest's advertised entry point, and it draws the voice from the
    /// cave with Claw's own creature portrait — so the look/colour it hardcodes has to stay Claw's row.</summary>
    [Fact]
    public void TutorBriefingUsesClawsPortrait()
    {
        EnsureLoaded();

        var claw = Content.Npcs.Single(n => n.Id == TigerMailQuest.ClawNpcId);
        Assert.Equal(29, claw.Look);      // TutorialQuest.TigerEssence -> ctx.SayLook(29, 12, …)
        Assert.Equal(12, claw.Color);
    }

    /// <summary>The quest is Warrior-only (nexusatlas "Prerequisite : Warrior Path"; RTK
    /// <c>player.baseClass ~= 1</c>), and the gate is on the BASE path so the Warrior subpaths keep it.</summary>
    [Fact]
    public void WarriorIsPathOne()
    {
        EnsureLoaded();
        Assert.Equal(TigerMailQuest.WarriorPathId, Content.PathIdForClass("Warrior"));
        Assert.Equal(TigerMailQuest.WarriorPathId, Content.PathBaseOf(TigerMailQuest.WarriorPathId));
    }
}
