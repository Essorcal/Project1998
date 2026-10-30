using System.Diagnostics;
using System.Reflection;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The Muse spells slice (the project owner, 2026-10-26 and again 2026-10-30: P5 "each spell as its RTK script", P1 and
/// P3 "on sale once it works, at its script's level and items"; and, for Song of the Goddess, the project owner's answer of
/// 2026-10-30, "Port RTK's testing copy"): Sacred verse, Tranquil symphony, Costume and Song of the Goddess do what their
/// RTK scripts do, and the Muse teacher sells them. Before, all four fell to the <c>generic</c> verb: 5 mana and nothing
/// else (<c>Tests/Fixtures/spell-dispatch-routes.txt</c> had them on <c>verb=generic</c>). WarMusic is not touched.
///
/// <para>The scripts: <c>RTK-Server/rtklua/Accepted/Spells/Subpaths/Muse/sacred_verse.lua</c>,
/// <c>tranquil_symphony.lua</c> and <c>costume.lua</c>, and <c>Subpaths/TESTING/muse_song_of_the_goddess.lua</c>. Each is
/// bound by its <c>game-data/SpellParams.csv</c> row: the two songs to <c>map_sanctuary</c>, over the Sanctuary line's one
/// deduction slot (P11's map-wide sweep, <c>Session.LuaMapSanctuary</c>), Costume to <c>disguise</c> (P3,
/// <c>Session.LuaDisguise</c>), and the Song to <c>heal_run</c>, a run on its target held by the caster on #406's periodic
/// tick (<c>Session.LuaStartHealRun</c>), whose clock a fact moves on by moving the run's deadlines back.</para>
///
/// <para>Every cast is the real 0x0F frame through <c>Session.Receive</c>, by a fresh level-99 Muse facing east. The maps
/// are content-free (no Maps.csv row, so PvP is off, as in a town), 63160-63169, which no other class uses; the PvP facts
/// stand on the Forsaken Arena (2591, a PvP map with spells allowed), as <c>NpcDamageVerbTests</c> does.</para>
/// </summary>
[Collection("world")]
public sealed class MuseSpellTests
{
    private const ushort SongMap = 63160, HalfMap = 63161, AgainMap = 63162, HeldMap = 63163, CostumeMap = 63164,
                         EndMap = 63165, NpcMap = 63166, RefuseMap = 63167, SongRunMap = 63168;
    private const ushort Arena = 2591;
    private const ushort CX = 16, CY = 16;
    private const uint MaxMana = 20_000, Mana = 20_000, Hp = 10_000;

    /// <summary>Slack on a deadline read straight after the cast.</summary>
    private const long SlackMs = 5_000;

    /// <summary>The creature a Costume copies: a graphic and a colour no player has.</summary>
    private const ushort CreatureLook = 77;
    private const byte CreatureColor = 3;

    private static readonly MethodInfo BuffBoxMethod =
        typeof(Session).GetMethod("BuffBoxText", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo DieMethod =
        typeof(Session).GetMethod("Die", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo CaptureMi =
        typeof(Session).GetMethod("CaptureTimedEffects", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo CharField =
        typeof(Session).GetField("_char", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo DisguiseRunField =
        typeof(Session).GetField("_disguiseRun", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo MorphUntilField =
        typeof(Session).GetField("_morphUntil", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo MorphLookField =
        typeof(Session).GetField("_morphLook", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo MorphColorField =
        typeof(Session).GetField("_morphColor", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo NextActionTick =
        typeof(Session).GetField("_nextActionTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ActionCount =
        typeof(Session).GetField("_actionCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo TicksField =
        typeof(Session).GetField("_spellTicks", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo PublishTickDue =
        typeof(Session).GetMethod("PublishSpellTickDue", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo EnterMapMethod =
        typeof(Session).GetMethod("EnterMap", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly SessionFixture _fx;
    private static int _serial;

    public MuseSpellTests(SessionFixture fx) => _fx = fx;

    // ===== Sacred verse and Tranquil symphony ==================================================================

    /// <summary>Every player on the caster's map who is not already in a sanctuary takes half of every blow for the
    /// script's time under the spell's name: the caster, a player beside them, one 40 tiles off and a ghost. Each but the
    /// caster reads "You have found Sanctuary." and "&lt;caster&gt; casts &lt;spell&gt; on you.", each reached has the
    /// script's graphic 11 over them, and its sound 5 plays at the caster; the caster pays 60 and reads "You cast
    /// &lt;spell&gt;.". A player already under a Sanctuary keeps theirs and hears nothing. Red on 912ba3c: generic's 5
    /// mana and nothing else.</summary>
    [Theory]
    [InlineData("sacred_verse", 120_000)]
    [InlineData("tranquil_symphony", 300_000)]
    public void ASongShieldsEveryPlayerOnTheMapNotAlreadyInASanctuary(string key, int ms)
    {
        var sp = Content.SpellByKey(key)!;
        var (near, nOut, _) = Player(SongMap, CX + 1, CY);
        var (far, fOut, _) = Player(SongMap, CX + 40, CY + 40);
        var (ghost, gOut, _) = Player(SongMap, CX - 1, CY, hp: 0);
        var (held, hOut, _) = Player(SongMap, CX, CY + 2);
        held.ApplySanctuaryDeduction(0.5, 30_000, "Sanctuary");
        var (caster, cOut, cc) = Caster(key, SongMap);
        foreach (var o in new[] { nOut, fOut, gOut, hOut }) o.Clear();
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string N(string s) => s.Replace(caster.CharName, "<caster>", StringComparison.Ordinal);
            string Lines(RecordingOutbound o) => string.Join(" | ", SpellCastSupport.MiniTexts(o).Select(N));
            string told = $"You have found Sanctuary. | <caster> casts {sp.Name} on you.";
            var problems = new List<string>();
            if (Lines(cOut) != $"You cast {sp.Name}.") problems.Add($"caster lines [{Lines(cOut)}]");
            if (cc.Mp != Mana - 60) problems.Add($"spent {Mana - cc.Mp} (want 60)");
            if (!Sounds(cOut).Contains(5)) problems.Add($"sounds at the caster [{string.Join(",", Sounds(cOut))}] (want 5)");
            foreach (var (who, o, name, tells) in new[] { (caster, cOut, "caster", false), (near, nOut, "near", true),
                                                         (far, fOut, "far", true), (ghost, gOut, "ghost", true) })
            {
                long left = who.SancDeductUntil - Environment.TickCount64;
                if (!who.SancDeductActive || who.EffDeduction != 0.5 || who.SancDeductName != sp.Name || left <= ms - SlackMs || left > ms)
                    problems.Add($"{name}: active {who.SancDeductActive} x{who.EffDeduction} '{who.SancDeductName}' {left} ms left");
                if (tells && Lines(o) != told) problems.Add($"{name} lines [{Lines(o)}]");
                if (!Effects(o, who.PlayerId).SequenceEqual(new[] { 11 }))
                    problems.Add($"{name}: graphics over them [{string.Join(",", Effects(o, who.PlayerId))}] (want 11)");
            }
            long heldLeft = held.SancDeductUntil - Environment.TickCount64;
            if (held.SancDeductName != "Sanctuary" || heldLeft > 30_000 || Lines(hOut).Length > 0 || Effects(hOut, held.PlayerId).Count > 0)
                problems.Add($"held: '{held.SancDeductName}' {heldLeft} ms, lines [{Lines(hOut)}], graphics {Effects(hOut, held.PlayerId).Count}");
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }
        finally { Leave(SongMap, caster, near, far, ghost, held); }
    }

    /// <summary>A player under the song takes half of a blow: a creature's spell of 1,000 takes 500 (the Sanctuary line's
    /// multiplier, at the damage intake's one floor). Red on 912ba3c: 1,000.</summary>
    [Fact]
    public void UnderTheSongABlowTakesHalf()
    {
        var (near, _, nc) = Player(HalfMap, CX + 1, CY, hp: 50_000);
        var (caster, _, _) = Caster("sacred_verse", HalfMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            near.ReceiveMobSpell(1_000, new Mob { Id = 9_001, Name = "Cave bat", Key = "no_such_mob", Level = 1 }, "Ice ray");
            Assert.True(nc.Hp == 49_500, $"vita {nc.Hp} (want 49500)");
        }
        finally { Leave(HalfMap, caster, near); }
    }

    /// <summary>On a map where the caster may attack the others (a PvP map, this server's canPK), the song is the
    /// script's shorter time, for everyone there, the caster included: 60 s for Sacred verse, 185 s for Tranquil
    /// symphony. Red on 912ba3c: no deduction at all.</summary>
    [Theory]
    [InlineData("sacred_verse", 60_000)]
    [InlineData("tranquil_symphony", 185_000)]
    public void OnAPvpMapTheSongIsTheScriptsShorterTime(string key, int ms)
    {
        Assert.True(Content.IsPvpMap(Arena));
        var (near, _, _) = Player(Arena, 20, 20);
        var (caster, _, _) = Caster(key, Arena, x: 21, y: 20);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            foreach (var who in new[] { caster, near })
            {
                long left = who.SancDeductUntil - Environment.TickCount64;
                Assert.True(who.SancDeductActive && left > ms - SlackMs && left <= ms, $"{who.CharName}: {left} ms left (want {ms})");
            }
        }
        finally { Leave(Arena, caster, near); }
    }

    /// <summary>A second song over the first reaches nobody, the caster included: each is already in a sanctuary, so the
    /// first's time and name stand and nobody hears anything more; the 60 is spent again (the script spends before its
    /// sweep). Tranquil symphony over Sacred verse is the same: one Sanctuary slot here, so a holder of either is in a
    /// sanctuary (RTK's own table leaves Tranquil symphony out; see the PR's departures). Red without the slot check in
    /// <c>Session.ReceiveMapSanctuary</c>: the second song takes the slot over with its own time and lines.</summary>
    [Theory]
    [InlineData("sacred_verse", "sacred_verse")]
    [InlineData("sacred_verse", "tranquil_symphony")]
    [InlineData("tranquil_symphony", "sacred_verse")]
    public void ASongReachesNobodyAlreadyInASanctuary(string first, string second)
    {
        var (near, nOut, _) = Player(AgainMap, CX + 1, CY);
        var (caster, cOut, cc) = Caster(first, AgainMap, extra: second);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            var firstName = Content.SpellByKey(first)!.Name;
            long before = near.SancDeductUntil;
            nOut.Clear(); cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(1));
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(nOut));
            Assert.True(near.SancDeductName == firstName && caster.SancDeductName == firstName && near.SancDeductUntil == before
                        && lines.Length == 0 && Effects(nOut, near.PlayerId).Count == 0 && cc.Mp == Mana - 120,
                        $"near '{near.SancDeductName}' moved {near.SancDeductUntil - before} ms, caster '{caster.SancDeductName}', " +
                        $"near's lines [{lines}], spent {Mana - cc.Mp} (want 120)");
        }
        finally { Leave(AgainMap, caster, near); }
    }

    /// <summary>Short of the 60: "Your will is too weak." and nothing else, for free. Red on 912ba3c: generic's line
    /// and its 5 mana.</summary>
    [Fact]
    public void ASongShortOfManaIsRefusedForFree()
    {
        var (near, _, _) = Player(RefuseMap, CX + 1, CY);
        var (caster, cOut, cc) = Caster("sacred_verse", RefuseMap, mana: 59);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            Assert.True(lines == "Your will is too weak." && cc.Mp == 59 && !near.SancDeductActive && !caster.SancDeductActive
                        && cOut.BodiesOf(ServerOp.Action).Count == 0,
                        $"lines [{lines}], mana {cc.Mp}, near {near.SancDeductActive}, caster {caster.SancDeductActive}");
        }
        finally { Leave(RefuseMap, caster, near); }
    }

    /// <summary>The write to each other player is made inside that player's own monitor: while a third thread holds a
    /// player's monitor the cast waits, and that player's slot is untouched, in both <c>StateRank</c> orders (as
    /// <c>PeerWritesLockTests</c> pins the Sanctuary family). Red with <c>Session.ReceiveMapSanctuary</c> deciding outside
    /// the target's monitor.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EachPlayersSlotIsWrittenInsideTheirOwnMonitor(bool casterRanksFirst)
    {
        Session caster, near;
        RecordingOutbound cOut;
        Character cc;
        if (casterRanksFirst)
        {
            (caster, cOut, cc) = Caster("sacred_verse", HeldMap);
            (near, _, _) = Player(HeldMap, CX + 1, CY);
        }
        else
        {
            (near, _, _) = Player(HeldMap, CX + 1, CY);
            (caster, cOut, cc) = Caster("sacred_verse", HeldMap);
        }
        Assert.Equal(casterRanksFirst, caster.StateRank < near.StateRank);

        var run = new HeldRun();
        try
        {
            WhileAnotherThreadHolds(run, near, () => caster.Receive(SpellCastSupport.CastFrame(0)),
                                    untouched: () => !near.SancDeductActive);
            Assert.Null(run.HolderFault);
            Assert.Null(run.ActorFault);
            Assert.False(run.FinishedWhileHeld, "the song finished while another thread held a player's monitor");
            Assert.True(run.UntouchedWhileHeld, "a player's slot was written while another thread held their monitor");
            Assert.True(near.SancDeductActive && caster.SancDeductActive && cc.Mp == Mana - 60,
                        $"after: near {near.SancDeductActive}, caster {caster.SancDeductActive}, spent {Mana - cc.Mp}");
        }
        finally
        {
            if (run.Settled) Leave(HeldMap, caster, near);
        }
    }

    // ===== Costume ==============================================================================================

    /// <summary>At a creature the caster takes its form and colour (RTK state 4, this server's morph slot): a peer and the
    /// caster themselves are sent the caster's id as that creature (0x07, look 0x8000 | its graphic, its colour); the
    /// caster pays 1,600, reads "You cast Costume.", has graphic 16 over them, waits 225 s, and the buff box lists
    /// "Costume 150s". Red on 912ba3c: generic's 5 mana and nothing else.</summary>
    [Fact]
    public void CostumeAtACreatureTakesItsForm()
    {
        var mob = AddCreature(CostumeMap, CX + 1, CY);
        var (watcher, wOut, _) = Player(CostumeMap, CX - 2, CY);
        var (caster, cOut, cc) = Caster("costume", CostumeMap);
        wOut.Clear();
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            long wait = SpellCastSupport.AetherLeft(caster, "costume");
            string box = BuffBox(caster);
            var seen = LastLookOf(wOut, caster.PlayerId);
            var self = LastLookOf(cOut, caster.PlayerId);
            string want = $"creature {0x8000 | CreatureLook} {CreatureColor}";
            Assert.True(caster.IsMorphed && seen == want && self == want && cc.Mp == Mana - 1_600 && lines == "You cast Costume."
                        && Effects(cOut, caster.PlayerId).SequenceEqual(new[] { 16 }) && wait > 225_000 - SlackMs && wait <= 225_000
                        && box.Contains("Costume 150s"),
                        $"morphed {caster.IsMorphed}, the watcher sees [{seen}], the caster [{self}] (want [{want}]), spent " +
                        $"{Mana - cc.Mp}, lines [{lines}], graphics [{string.Join(",", Effects(cOut, caster.PlayerId))}], wait {wait}, box [{box}]");
        }
        finally { Leave(CostumeMap, caster, watcher); _fx.World.DespawnMob(CostumeMap, mob); }
    }

    /// <summary>At a player the caster takes their look: a peer and the caster are drawn the target's body, head, armour,
    /// dye, weapon and shield under the caster's own name, and the caster is not in a creature's form; the buff box lists
    /// "Costume 150s". Red on 912ba3c: nothing changes.</summary>
    [Fact]
    public void CostumeAtAPlayerTakesTheirLookUnderTheCastersName()
    {
        var sword = Content.ItemByKey("novice_sword")!;
        var (target, _, _) = Player(CostumeMap, CX + 1, CY, configure: ch =>
        {
            ch.Sex = 1; ch.Face = 12; ch.HairColor = 5;
            ch.Equipment.Add(new InvItem { Slot = sword.EquipSlot, ItemId = sword.Id, Dura = sword.Durability });
        });
        var (watcher, wOut, _) = Player(CostumeMap, CX - 2, CY);
        var (caster, cOut, cc) = Caster("costume", CostumeMap, configure: ch => { ch.Sex = 0; ch.Face = 3; });
        wOut.Clear();
        try
        {
            var t = target.Snapshot();
            var mine = caster.Snapshot();
            string want = $"human {t.Sex},0,{t.Face},{t.Armor},{t.ArmorColor},{t.Weapon},{t.Shield}";
            Assert.NotEqual(want, $"human {mine.Sex},0,{mine.Face},{mine.Armor},{mine.ArmorColor},{mine.Weapon},{mine.Shield}");

            caster.Receive(SpellCastSupport.CastFrame(0));
            var seen = LastLookOf(wOut, caster.PlayerId);
            var self = LastLookOf(cOut, caster.PlayerId);
            var name = LastNameOf(wOut, caster.PlayerId);
            string box = BuffBox(caster);
            Assert.True(seen == want && self == want && name == caster.CharName && !caster.IsMorphed && cc.Mp == Mana - 1_600
                        && Effects(cOut, caster.PlayerId).SequenceEqual(new[] { 16 }) && box.Contains("Costume 150s"),
                        $"the watcher sees [{seen}] named '{name}', the caster [{self}] (want [{want}] named '{caster.CharName}'), " +
                        $"morphed {caster.IsMorphed}, spent {Mana - cc.Mp}, graphics [{string.Join(",", Effects(cOut, caster.PlayerId))}], box [{box}]");
        }
        finally { Leave(CostumeMap, caster, target, watcher); }
    }

    /// <summary>At a player who is in a creature's form (RTK state 4) the caster takes that form, with colour 0: the script
    /// reads <c>target.lookColor</c>, which RTK gives an NPC and a creature but not a player. Red on 912ba3c.</summary>
    [Fact]
    public void CostumeAtAPlayerInACreaturesFormTakesThatFormWithColourZero()
    {
        var mob = AddCreature(CostumeMap, CX + 2, CY);
        var (target, _, _) = Player(CostumeMap, CX + 1, CY, configure: ch =>
        {
            ch.Dir = 1;
            ch.MaxMp = MaxMana; ch.Mp = Mana;
            ch.Spells.Add(Content.SpellByKey("costume")!.Id);
        });
        var (caster, _, _) = Caster("costume", CostumeMap);
        try
        {
            target.Receive(SpellCastSupport.CastFrame(0));   // the target costumes as the creature beyond them
            Assert.True(target.IsMorphed, "the target did not take the creature's form");
            caster.Receive(SpellCastSupport.CastFrame(0));
            var (look, color) = Form(caster);
            Assert.True(caster.IsMorphed && look == CreatureLook && color == 0, $"morphed {caster.IsMorphed}, form {look} colour {color}");
        }
        finally { Leave(CostumeMap, caster, target); _fx.World.DespawnMob(CostumeMap, mob); }
    }

    /// <summary>A disguise ends at its time on the world's heartbeat, as the script's uncast ends it: the real look back
    /// for a peer and the caster, the spell's graphic 16 over the caster (and no morph "poof" sound for a creature's form),
    /// and the buff box empty of it. Red with the disguise run left out of the heartbeat's morph phase (the player's look),
    /// or with <c>RevertMorph</c> playing the poof for a disguise (the creature's form).</summary>
    [Theory]
    [InlineData("creature")]
    [InlineData("player")]
    public void ADisguiseEndsAtItsTimeWithTheSpellsGraphic(string what)
    {
        Mob? mob = what == "creature" ? AddCreature(EndMap, CX + 1, CY) : null;
        var (target, _, _) = what == "player"
            ? Player(EndMap, CX + 1, CY, configure: ch => { ch.Sex = 1; ch.Face = 12; })
            : (null!, null!, null!);
        var (watcher, wOut, _) = Player(EndMap, CX - 2, CY);
        var (caster, cOut, _) = Caster("costume", EndMap, configure: ch => { ch.Sex = 0; ch.Face = 3; });
        try
        {
            var mine = caster.Snapshot();
            string real = $"human {mine.Sex},0,{mine.Face},{mine.Armor},{mine.ArmorColor},{mine.Weapon},{mine.Shield}";
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.NotEqual(real, LastLookOf(wOut, caster.PlayerId));
            wOut.Clear(); cOut.Clear();

            Lapse(caster);
            _fx.World.TickOnceForTest();

            var seen = LastLookOf(wOut, caster.PlayerId);
            var self = LastLookOf(cOut, caster.PlayerId);
            string box = BuffBox(caster);
            var drawn = Effects(cOut, caster.PlayerId);
            var sounds = Sounds(cOut);
            Assert.True(seen == real && self == real && !caster.IsMorphed && drawn.SequenceEqual(new[] { 16 }) && !sounds.Contains(411)
                        && !box.Contains("Costume"),
                        $"{what}: the watcher sees [{seen}], the caster [{self}] (want [{real}]), morphed {caster.IsMorphed}, " +
                        $"graphics [{string.Join(",", drawn)}] (want 16), sounds [{string.Join(",", sounds)}], box [{box}]");
        }
        finally
        {
            Leave(EndMap, caster, watcher);
            if (target is not null) Leave(EndMap, target);
            if (mob is not null) _fx.World.DespawnMob(EndMap, mob);
        }
    }

    /// <summary>Death, <c>@dispel</c> and a won Dispell end a disguise, as RTK's pc_diescript and flushDuration run the
    /// costume's uncast (its SpellDispel is 0), each with the spell's graphic 16 over the caster. Red with the end taken
    /// out of <c>ClearAllTimedEffects</c> (death, <c>@dispel</c>: the player's look) or of <c>FlushDurations</c>
    /// (Dispell: both).</summary>
    [Theory]
    [InlineData("death", "player")]
    [InlineData("dispel", "player")]
    [InlineData("flush", "player")]
    [InlineData("flush", "creature")]
    public void WhatEndsTheCastersDurationsEndsTheDisguise(string how, string what)
    {
        Mob? mob = what == "creature" ? AddCreature(EndMap, CX + 1, CY) : null;
        var (target, _, _) = what == "player"
            ? Player(EndMap, CX + 1, CY, configure: ch => { ch.Sex = 1; ch.Face = 12; })
            : (null!, null!, null!);
        var (caster, cOut, _) = Caster("costume", EndMap, configure: ch => { ch.Sex = 0; ch.Face = 3; });
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Disguised(caster), $"{what}: no disguise to end");
            cOut.Clear();
            switch (how)
            {
                case "death":  caster.WithState(() => DieMethod.Invoke(caster, null)); break;
                case "dispel": caster.WithState(caster.DispelSelf); break;
                default:       caster.ReceiveFlush(endHolds: true); break;
            }
            var drawn = Effects(cOut, caster.PlayerId);
            Assert.True(!Disguised(caster) && drawn.Contains(16),
                        $"after {how}: disguised {Disguised(caster)}, graphics [{string.Join(",", drawn)}] (want 16)");
        }
        finally
        {
            Leave(EndMap, caster);
            if (target is not null) Leave(EndMap, target);
            if (mob is not null) _fx.World.DespawnMob(EndMap, mob);
        }
    }

    /// <summary>At an NPC there is no look, but the rest, as the script's two branches both miss one: 1,600 spent, graphic
    /// 16, the 225 s wait, a 150 s run in the buff box that ends with graphic 16. Red on 912ba3c: generic's 5 mana and
    /// nothing else.</summary>
    [Fact]
    public void CostumeAtAnNpcIsARunWithNoLook()
    {
        var npc = AddCreature(NpcMap, CX + 1, CY, npc: true);
        var (caster, cOut, cc) = Caster("costume", NpcMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            long wait = SpellCastSupport.AetherLeft(caster, "costume");
            string box = BuffBox(caster);
            var drawn = Effects(cOut, caster.PlayerId);
            Assert.True(!caster.IsMorphed && LastLookOf(cOut, caster.PlayerId) == "" && cc.Mp == Mana - 1_600
                        && drawn.SequenceEqual(new[] { 16 }) && wait > 225_000 - SlackMs && box.Contains("Costume 150s"),
                        $"morphed {caster.IsMorphed}, redrawn [{LastLookOf(cOut, caster.PlayerId)}], spent {Mana - cc.Mp}, " +
                        $"graphics [{string.Join(",", drawn)}], wait {wait}, box [{box}]");

            cOut.Clear();
            Lapse(caster);
            _fx.World.TickOnceForTest();
            Assert.True(Effects(cOut, caster.PlayerId).SequenceEqual(new[] { 16 }) && !BuffBox(caster).Contains("Costume"),
                        $"at its end: graphics [{string.Join(",", Effects(cOut, caster.PlayerId))}], box [{BuffBox(caster)}]");
        }
        finally { _fx.World.LeaveMap(caster, NpcMap); RemoveNpc(NpcMap, npc); }
    }

    /// <summary>Facing nothing living (an empty tile, or a ghost: RTK's getTargetFacing passes over state 1), the script
    /// takes the 1,600 and then stops with a Lua error on the nil target, so the mana is gone and nothing else happens:
    /// no line, no pose, no graphic, no wait, no disguise. Red on 912ba3c: generic's 5 mana and its line.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CostumeFacingNothingLivingLosesTheManaAndNothingElse(bool ghostInFront)
    {
        var (ghost, _, _) = ghostInFront ? Player(RefuseMap, CX + 1, CY, hp: 0) : (null!, null!, null!);
        var (caster, cOut, cc) = Caster("costume", RefuseMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            Assert.True(cc.Mp == Mana - 1_600 && lines.Length == 0 && cOut.BodiesOf(ServerOp.Action).Count == 0
                        && Effects(cOut, caster.PlayerId).Count == 0 && SpellCastSupport.AetherLeft(caster, "costume") == 0
                        && !Disguised(caster),
                        $"spent {Mana - cc.Mp} (want 1600), lines [{lines}], poses {cOut.BodiesOf(ServerOp.Action).Count}, " +
                        $"graphics {Effects(cOut, caster.PlayerId).Count}, wait {SpellCastSupport.AetherLeft(caster, "costume")}, " +
                        $"disguised {Disguised(caster)}");
        }
        finally
        {
            _fx.World.LeaveMap(caster, RefuseMap);
            if (ghost is not null) _fx.World.LeaveMap(ghost, RefuseMap);
        }
    }

    /// <summary>Short of the 1,600: "Your will is too weak.", for free; inside the 225 s wait: "Costume isn't ready
    /// yet.", for free. Red on 912ba3c.</summary>
    [Fact]
    public void CostumeRefusalsAreFree()
    {
        var mob = AddCreature(RefuseMap, CX + 1, CY);
        var (caster, cOut, cc) = Caster("costume", RefuseMap, mana: 1_599);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string weak = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            Assert.True(weak == "Your will is too weak." && cc.Mp == 1_599 && !caster.IsMorphed, $"short: [{weak}], mana {cc.Mp}");

            caster.WithState(() => cc.Mp = Mana);
            SpellCastSupport.ClearAether(caster, "costume");
            caster.WithState(() => caster.LuaSetCooldown("costume", 100_000));
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            string waiting = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            Assert.True(waiting == "Costume isn't ready yet." && cc.Mp == Mana && !caster.IsMorphed, $"waiting: [{waiting}], mana {cc.Mp}");
        }
        finally { _fx.World.LeaveMap(caster, RefuseMap); _fx.World.DespawnMob(RefuseMap, mob); }
    }

    /// <summary>Nothing new is saved. A disguise as a player lives on the session only (RTK keeps its gfx clone in
    /// memory), so the timed effects a save captures hold no form and no Costume entry, and a relog wears the caster's own
    /// look. A creature's form rides the morph's existing fields (look, colour, end and the spell's key) and its buff-box
    /// entry, as every morph does, so a relog keeps it, as RTK's saved state and disguise keep it.</summary>
    [Theory]
    [InlineData("player")]
    [InlineData("creature")]
    public void OnlyACreaturesFormIsSavedAndThroughTheMorphsFields(string what)
    {
        Mob? mob = what == "creature" ? AddCreature(CostumeMap, CX + 1, CY) : null;
        var (target, _, _) = what == "player"
            ? Player(CostumeMap, CX + 1, CY, configure: ch => { ch.Sex = 1; ch.Face = 12; })
            : (null!, null!, null!);
        var (caster, _, _) = Caster("costume", CostumeMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Disguised(caster));
            TimedEffects? saved = null;
            caster.WithState(() =>
            {
                CaptureMi.Invoke(caster, null);
                saved = ((Character)CharField.GetValue(caster)!).Effects;
            });
            string got = $"form {saved!.MorphLook} {saved.MorphColor} '{saved.MorphKey}', buffs [{string.Join(",", saved.Buffs.Select(b => b.Key))}]";
            if (what == "player")
                Assert.True(saved.MorphLook == 0 && saved.MorphKey == "" && saved.Buffs.All(b => b.Key != "costume"), got);
            else
                Assert.True(saved.MorphLook == CreatureLook && saved.MorphColor == CreatureColor && saved.MorphKey == "costume"
                            && saved.Buffs.Any(b => b.Key == "costume"), got);
        }
        finally
        {
            Leave(CostumeMap, caster);
            if (target is not null) Leave(CostumeMap, target);
            if (mob is not null) _fx.World.DespawnMob(CostumeMap, mob);
        }
    }

    // ===== Song of the Goddess (RTK's TESTING copy) ============================================================

    /// <summary>At the faced player the Song starts a 24 s run held by the caster, beating every second, the last beat as
    /// it ends (RTK's player timer takes 1,000 off a beat): the caster pays 1,000 at rank 0 and reads "You cast Song of the
    /// Goddess."; the player reads "&lt;caster&gt; casts Song of the Goddess on you." and hears sound 4; nothing is healed
    /// yet. Red on 912ba3c: generic's 5 mana and nothing else.</summary>
    [Fact]
    public void TheSongStartsARunOnTheFacedPlayer()
    {
        var (target, tOut, tc) = Player(SongRunMap, CX + 1, CY, hp: 1_000, maxHp: 1_000_000);
        var (caster, cOut, cc) = Caster("song_of_the_goddess", SongRunMap);
        tOut.Clear();
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string N(string s) => s.Replace(caster.CharName, "<caster>", StringComparison.Ordinal);
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            string theirs = string.Join(" | ", SpellCastSupport.MiniTexts(tOut).Select(N));
            var t = Ticks(caster).SingleOrDefault();
            long length = t is null ? 0 : t.UntilIn - t.NextIn + t.Every;
            Assert.True(lines == "You cast Song of the Goddess." && theirs == "<caster> casts Song of the Goddess on you."
                        && cc.Mp == Mana - 1_000 && tc.Hp == 1_000 && Sounds(tOut).Contains(4)
                        && t is { Spell: "song_of_the_goddess", Verb: "heal_run_tick", Every: 1_000, Ended: false }
                        && t.Player == target.PlayerId && t.Mob == 0 && length == 24_001,
                        $"lines [{lines}], theirs [{theirs}], spent {Mana - cc.Mp}, vita {tc.Hp}, sounds [{string.Join(",", Sounds(tOut))}], " +
                        $"run {t}, length {length}");
        }
        finally { Leave(SongRunMap, caster, target); }
    }

    /// <summary>Each beat heals the player by the caster's rank, as the script's two if-chains read it: rank 0 (and any
    /// rank but 1 and 2) pays 1,000 and heals 5,000; rank 1 pays 3,000 and heals 10,000; rank 2 pays 6,000 and heals
    /// 20,000. The beat draws graphic 66 and plays sound 4 over the player. Red on 912ba3c: no run, no heal.</summary>
    [Theory]
    [InlineData(0, 1_000, 5_000)]
    [InlineData(1, 3_000, 10_000)]
    [InlineData(2, 6_000, 20_000)]
    [InlineData(3, 1_000, 5_000)]
    public void EachBeatHealsByTheCastersRank(int mark, int mana, int heal)
    {
        var (target, tOut, tc) = Player(SongRunMap, CX + 1, CY, hp: 1_000, maxHp: 1_000_000);
        var (caster, _, cc) = Caster("song_of_the_goddess", SongRunMap, configure: ch => ch.Mark = (byte)mark);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            tOut.Clear();
            Advance(caster, 1_000);
            var drawn = Effects(tOut, target.PlayerId);
            Assert.True(cc.Mp == Mana - mana && tc.Hp == 1_000 + heal && drawn.SequenceEqual(new[] { 66 }) && Sounds(tOut).Contains(4),
                        $"rank {mark}: spent {Mana - cc.Mp} (want {mana}), vita {tc.Hp} (want {1_000 + heal}), " +
                        $"graphics [{string.Join(",", drawn)}], sounds [{string.Join(",", Sounds(tOut))}]");
        }
        finally { Leave(SongRunMap, caster, target); }
    }

    /// <summary>The run heals 24 times, one a second, the last as it ends, then is gone: RTK's bl_duratimer takes 1,000
    /// off the 24,000 and runs while_cast after each, the one that reaches 0 included. Red with the run's last
    /// millisecond taken off (23 heals).</summary>
    [Fact]
    public void TheSongHealsTwentyFourTimesThenEnds()
    {
        var (target, _, tc) = Player(SongRunMap, CX + 1, CY, hp: 1_000, maxHp: 1_000_000);
        var (caster, _, _) = Caster("song_of_the_goddess", SongRunMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            for (int i = 0; i < 25; i++) Advance(caster, 1_000);
            Assert.True(tc.Hp == 1_000 + 24 * 5_000 && Ticks(caster).Count == 0,
                        $"vita {tc.Hp} (want {1_000 + 24 * 5_000}: {(tc.Hp - 1_000) / 5_000} heals), runs left {Ticks(caster).Count}");
        }
        finally { Leave(SongRunMap, caster, target); }
    }

    /// <summary>At a creature the run beats every 1.5 s, RTK's creature timer (its 50 ms clock runs the duration countdown
    /// once a 1.5 s cycle), still 24 times: 5,000 a beat. Red on 912ba3c: nothing healed.</summary>
    [Fact]
    public void AtACreatureTheSongBeatsEveryOneAndAHalfSeconds()
    {
        var mob = AddCreature(SongRunMap, CX + 1, CY);
        mob.MaxHp = 1_000_000; mob.Hp = 1_000;
        var (caster, cOut, cc) = Caster("song_of_the_goddess", SongRunMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            var t = Ticks(caster).SingleOrDefault();
            long length = t is null ? 0 : t.UntilIn - t.NextIn + t.Every;
            Assert.True(t is { Every: 1_500 } && t.Mob == mob.Id && length == 36_001 && cc.Mp == Mana - 1_000,
                        $"run {t}, length {length}, spent {Mana - cc.Mp}");
            cOut.Clear();
            Advance(caster, 1_500);
            Assert.True(mob.Hp == 6_000 && Effects(cOut, mob.Id).SequenceEqual(new[] { 66 }),
                        $"one beat: vita {mob.Hp} (want 6000), graphics [{string.Join(",", Effects(cOut, mob.Id))}]");
            for (int i = 0; i < 24; i++) Advance(caster, 1_500);
            Assert.True(mob.Hp == 1_000 + 24 * 5_000 && Ticks(caster).Count == 0, $"vita {mob.Hp}, runs {Ticks(caster).Count}");
        }
        finally { _fx.World.LeaveMap(caster, SongRunMap); _fx.World.DespawnMob(SongRunMap, mob); }
    }

    /// <summary>The player's death, their <c>@dispel</c> and a won Dispell on them end the run, as RTK's pc_diescript and
    /// flushDuration end the duration on the player (the song's SplDispel is 0): the next beat heals nothing, even with the
    /// player alive again, and the run is gone. Red with the count taken out of <c>ClearAllTimedEffects</c> (death,
    /// <c>@dispel</c>) or <c>FlushDurations</c> (Dispell).</summary>
    [Theory]
    [InlineData("death")]
    [InlineData("dispel")]
    [InlineData("flush")]
    public void WhatEndsThePlayersDurationsEndsTheRun(string how)
    {
        var (target, _, tc) = Player(SongRunMap, CX + 1, CY, hp: 1_000, maxHp: 1_000_000);
        var (caster, _, _) = Caster("song_of_the_goddess", SongRunMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Ticks(caster).SingleOrDefault() is { Ended: false } run && run.Player == target.PlayerId, "the Song started no run");
            switch (how)
            {
                case "death":  target.WithState(() => { DieMethod.Invoke(target, null); tc.Hp = 1_000; }); break;   // and up again
                case "dispel": target.WithState(target.DispelSelf); break;
                default:       target.ReceiveFlush(endHolds: true); break;
            }
            Advance(caster, 1_000);
            Advance(caster, 1_000);
            Assert.True(tc.Hp == 1_000 && Ticks(caster).Count == 0, $"after {how}: vita {tc.Hp} (want 1000), runs {Ticks(caster).Count}");
        }
        finally { Leave(SongRunMap, caster, target); }
    }

    /// <summary>The run reaches its player wherever they go, as RTK's timer runs on the player: on another map the beat
    /// still heals them. Once they have left the world the next beat ends the run.</summary>
    [Fact]
    public void TheRunFollowsItsPlayerAndEndsWhenTheyLeave()
    {
        var (target, _, tc) = Player(SongRunMap, CX + 1, CY, hp: 1_000, maxHp: 1_000_000);
        var (caster, _, _) = Caster("song_of_the_goddess", SongRunMap);
        var home = Content.Maps[SessionFixture.HomeMap];
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            target.WithState(() => EnterMapMethod.Invoke(target, new object[]
            {
                home.Id, home.Xs, home.Ys, (ushort)5, (ushort)10, home.Name, ArrivalPolicy.Clamp,
            }));
            Advance(caster, 1_000);
            Assert.True(tc.Hp == 6_000 && target.CharMap == SessionFixture.HomeMap, $"on map {target.CharMap}: vita {tc.Hp} (want 6000)");

            _fx.World.LeaveMap(target, SessionFixture.HomeMap);
            Advance(caster, 1_000);
            Assert.True(tc.Hp == 6_000 && Ticks(caster).Count == 0, $"after leaving: vita {tc.Hp}, runs {Ticks(caster).Count}");
        }
        finally { Leave(SongRunMap, caster); _fx.World.LeaveMap(target, SessionFixture.HomeMap); }
    }

    /// <summary>Aimed at nothing, the Song sings to its caster (the rule every "Which target?" heal here has): a run on the
    /// caster, no line but "You cast Song of the Goddess.", and the beat heals them. Red on 912ba3c.</summary>
    [Fact]
    public void ASongAimedAtNothingSingsToItsCaster()
    {
        var (caster, cOut, cc) = Caster("song_of_the_goddess", SongRunMap, configure: ch => ch.Hp = 1_000);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            var t = Ticks(caster).SingleOrDefault();
            Advance(caster, 1_000);
            Assert.True(lines == "You cast Song of the Goddess." && t is not null && t.Player == caster.PlayerId && cc.Hp == 6_000,
                        $"lines [{lines}], run {t}, vita {cc.Hp} (want 6000)");
        }
        finally { _fx.World.LeaveMap(caster, SongRunMap); }
    }

    /// <summary>The refusals, each free and starting nothing: short of the rank's mana (2,999 at rank 1), "Your will is too
    /// weak."; at a ghost, "You need a different type of cure."; at an NPC nothing at all (RTK's engine runs no script for
    /// one). Red on 912ba3c: generic's 5 mana.</summary>
    [Theory]
    [InlineData("mana")]
    [InlineData("ghost")]
    [InlineData("npc")]
    public void SongRefusalsAreFree(string what)
    {
        Mob? npc = what == "npc" ? AddCreature(SongRunMap, CX + 1, CY, npc: true) : null;
        var (ghost, _, _) = what == "ghost" ? Player(SongRunMap, CX + 1, CY, hp: 0) : (null!, null!, null!);
        uint mana = what == "mana" ? 2_999u : Mana;
        var (caster, cOut, cc) = Caster("song_of_the_goddess", SongRunMap, mana: mana, configure: ch => ch.Mark = 1);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            string want = what switch { "mana" => "Your will is too weak.", "ghost" => "You need a different type of cure.", _ => "" };
            Assert.True(lines == want && cc.Mp == mana && Ticks(caster).Count == 0 && cOut.BodiesOf(ServerOp.Action).Count == 0,
                        $"{what}: lines [{lines}] (want [{want}]), mana {cc.Mp}, runs {Ticks(caster).Count}");
        }
        finally
        {
            _fx.World.LeaveMap(caster, SongRunMap);
            if (ghost is not null) _fx.World.LeaveMap(ghost, SongRunMap);
            if (npc is not null) RemoveNpc(SongRunMap, npc);
        }
    }

    // ===== the rows and the teacher ===========================================================================

    /// <summary>The four are Muse's path's rows, with Sacred verse's mark 1 and Tranquil symphony's mark 2 (guards: the
    /// slice changes no Spells.csv row).</summary>
    [Theory]
    [InlineData("sacred_verse", 1)]
    [InlineData("tranquil_symphony", 2)]
    [InlineData("costume", 0)]
    [InlineData("song_of_the_goddess", 0)]
    public void TheRowsAreMusesWithTheirMarks(string key, int mark)
    {
        var sp = Content.SpellByKey(key)!;
        Assert.Equal((21, mark), (sp.PathId, sp.Mark));
    }

    /// <summary>The Muse teacher's list by rank: Costume and Song of the Goddess from mark 0 at their rows' level, 0 (their
    /// scripts have no <c>requirements()</c>); Sacred verse from mark 1 and Tranquil symphony from mark 2, each at its
    /// script's level, 30. Red without the price rows: nothing.</summary>
    [Theory]
    [InlineData(0, "costume 0; song_of_the_goddess 0")]
    [InlineData(1, "costume 0; sacred_verse 30; song_of_the_goddess 0")]
    [InlineData(2, "costume 0; sacred_verse 30; song_of_the_goddess 0; tranquil_symphony 30")]
    public void TheMuseTeacherSellsByRank(int mark, string expected)
    {
        var sold = Content.SubpathTeacherSpells(21, 99, mark);
        Assert.Equal(expected, string.Join("; ", sold.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key} {s.Level}")));
    }

    // ===== helpers ============================================================================================

    private sealed record TickView(string Spell, string Verb, uint Mob, uint Player, int Every, long NextIn, long UntilIn, bool Ended);

    /// <summary>The caster's running ticks, read under their monitor; times are milliseconds from now.</summary>
    private static List<TickView> Ticks(Session s)
    {
        var list = new List<TickView>();
        s.WithState(() =>
        {
            long now = Environment.TickCount64;
            foreach (var t in (System.Collections.IList)TicksField.GetValue(s)!)
                list.Add(new TickView((string)F(t, "SpellKey"), (string)F(t, "Verb"), (uint)F(t, "MobId"), (uint)F(t, "PlayerId"),
                                      (int)F(t, "EveryMs"), (long)F(t, "Next") - now, (long)F(t, "Until") - now, (bool)F(t, "Ended")));
        });
        return list;
    }

    /// <summary>Move the caster's runs <paramref name="ms"/> on, as <c>ShamanSpellTests</c> moves them (each beat time and
    /// end moved back by that much, the hint republished), then run the heartbeat's call.</summary>
    private static void Advance(Session s, long ms)
    {
        s.WithState(() =>
        {
            foreach (var t in (System.Collections.IList)TicksField.GetValue(s)!)
            {
                t.GetType().GetField("Next")!.SetValue(t, (long)F(t, "Next") - ms);
                t.GetType().GetField("Until")!.SetValue(t, (long)F(t, "Until") - ms);
            }
            PublishTickDue.Invoke(s, null);
        });
        s.TickSpellTicks();
    }

    private static object F(object o, string name) => o.GetType().GetField(name)!.GetValue(o)!;

    /// <summary>Is the caster wearing a disguise of either kind: a creature's form, or a player-look or NPC run?</summary>
    private static bool Disguised(Session s)
    {
        bool run = false;
        s.WithState(() => run = DisguiseRunField.GetValue(s) is not null);
        return run || s.IsMorphed;
    }

    /// <summary>The form the caster wears: its graphic and colour.</summary>
    private static (ushort Look, byte Color) Form(Session s)
    {
        (ushort, byte) form = default;
        s.WithState(() => form = ((ushort)MorphLookField.GetValue(s)!, (byte)MorphColorField.GetValue(s)!));
        return form;
    }

    /// <summary>Move a running disguise's end one millisecond into the past, where the clock would put it, for the
    /// heartbeat to find: the morph slot's deadline, and the run's (an immutable record, changed in place by this seam
    /// alone).</summary>
    private static void Lapse(Session s) => s.WithState(() =>
    {
        long past = Environment.TickCount64 - 1;
        if ((ushort)MorphLookField.GetValue(s)! != 0) MorphUntilField.SetValue(s, past);
        if (DisguiseRunField.GetValue(s) is { } run) run.GetType().GetProperty("Until")!.SetValue(run, past);
    });

    /// <summary>The last look <paramref name="outbound"/> was sent for entity <paramref name="id"/>, in order across the
    /// three packets that carry one: "creature &lt;look&gt; &lt;colour&gt;" (0x07), or "human &lt;the 4.95 seven bytes,
    /// the form byte as 0&gt;" (0x33, 0x1d). "" when none was sent.</summary>
    private static string LastLookOf(RecordingOutbound outbound, uint id)
    {
        string last = "";
        foreach (var (op, b) in Bodies(outbound))
        {
            if (op == ServerOp.CreatureList)
            {
                int n = (b[0] << 8) | b[1];
                for (int i = 0, at = 2; i < n; i++, at += 12)
                    if (U32(b, at + 4) == id) last = $"creature {(b[at + 8] << 8) | b[at + 9]} {b[at + 10]}";
            }
            else if (op == ServerOp.PlayerLook && b.Length >= 17 && U32(b, 5) == id)
                last = $"human {b[10]},0,{b[12]},{b[13]},{b[14]},{b[15]},{b[16]}";
            else if (op == ServerOp.LookUpdate && b.Length >= 12 && U32(b, 0) == id)
                last = $"human {b[5]},0,{b[7]},{b[8]},{b[9]},{b[10]},{b[11]}";
        }
        return last;
    }

    /// <summary>The name the last 0x33 for entity <paramref name="id"/> carried.</summary>
    private static string LastNameOf(RecordingOutbound outbound, uint id)
    {
        string name = "";
        foreach (var (op, b) in Bodies(outbound))
            if (op == ServerOp.PlayerLook && b.Length >= 19 && U32(b, 5) == id)
                name = System.Text.Encoding.ASCII.GetString(b, 19, b[18]);
        return name;
    }

    /// <summary>Every recorded frame's opcode and decrypted body, in the order sent.</summary>
    private static IEnumerable<(byte Op, byte[] Body)> Bodies(RecordingOutbound outbound)
    {
        foreach (var frame in outbound.Frames)
        {
            if (!TkPacket.TryParse(frame, out var pkt, out _)) continue;
            yield return (pkt.Opcode, TkCrypt.Crypt(pkt.Body, pkt.Increment, TkCrypt.LoginKey));
        }
    }

    private static uint U32(byte[] b, int at) => (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]);

    /// <summary>The spell graphics (0x29) <paramref name="outbound"/> was sent over <paramref name="id"/>, as effect ids.</summary>
    private static List<int> Effects(RecordingOutbound outbound, uint id) =>
        outbound.BodiesOf(ServerOp.Effect).Where(b => U32(b, 0) == id).Select(b => b[4] - ServerConfig.Current.EfxWireOffset).ToList();

    /// <summary>The sounds (0x19: type, 3, then the sfx id as a u16 big-endian, <c>Session.SendSound</c>)
    /// <paramref name="outbound"/> was sent.</summary>
    private static List<int> Sounds(RecordingOutbound outbound) =>
        outbound.BodiesOf(ServerOp.Audio).Where(b => b.Length >= 4).Select(b => (b[2] << 8) | b[3]).ToList();

    private static string BuffBox(Session s)
    {
        string box = "";
        s.WithState(() => box = (string)BuffBoxMethod.Invoke(s, null)!);
        return box;
    }

    /// <summary>Let the next cast run now: a fresh action budget and a free cast slot.</summary>
    private static void FreeSlot(Session s) => s.WithState(() => { NextActionTick.SetValue(s, 0L); ActionCount.SetValue(s, 0); });

    /// <summary>Take a test NPC off its map: <c>World.DespawnMob</c> refuses an NPC, so it is made a plain creature first
    /// (a test seam; a placed NPC never leaves).</summary>
    private void RemoveNpc(ushort map, Mob npc)
    {
        npc.IsNpc = false;
        _fx.World.DespawnMob(map, npc);
    }

    private Mob AddCreature(ushort map, int x, int y, bool npc = false)
    {
        var mob = new Mob(_fx.World.AllocateMobId(), CreatureLook, (ushort)x, (ushort)y, $"Muse target {Interlocked.Increment(ref _serial)}", 50_000)
        {
            Color = CreatureColor,
            IsNpc = npc,
        };
        _fx.World.AddMob(map, mob);
        return mob;
    }

    /// <summary>A level-99 Muse at (<paramref name="x"/>, <paramref name="y"/>) facing east, with <paramref name="key"/> in
    /// slot 0 (and <paramref name="extra"/> in slot 1).</summary>
    private (Session session, RecordingOutbound outbound, Character c) Caster(string key, ushort map, int x = CX, int y = CY,
                                                                            uint mana = Mana, string? extra = null,
                                                                            Action<Character>? configure = null) =>
        _fx.PlayerWith($"Muse{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.ClassName = "Muse";
            ch.Level = 99;
            ch.MaxHp = Hp; ch.Hp = Hp;
            ch.MaxMp = MaxMana; ch.Mp = mana;
            ch.Dir = 1;
            Wide(ch);
            ch.Spells.Add(Content.SpellByKey(key)!.Id);
            if (extra is not null) ch.Spells.Add(Content.SpellByKey(extra)!.Id);
            configure?.Invoke(ch);
        }, map, (ushort)x, (ushort)y);

    private (Session session, RecordingOutbound outbound, Character c) Player(ushort map, int x, int y, uint hp = Hp,
                                                                            Action<Character>? configure = null, uint maxHp = 0) =>
        _fx.PlayerWith($"MuseOther{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 50;
            ch.MaxHp = Math.Max(Math.Max(hp, Hp), maxHp); ch.Hp = hp;
            ch.MaxMp = 1_000; ch.Mp = 1_000;
            Wide(ch);
            configure?.Invoke(ch);
        }, map, (ushort)x, (ushort)y);

    /// <summary>Content-free maps have no size, and the cast handler needs one.</summary>
    private static void Wide(Character c) { c.MapXs = 100; c.MapYs = 100; }

    private void Leave(ushort map, params Session[] sessions)
    {
        foreach (var s in sessions) _fx.World.LeaveMap(s, map);
    }

    // ---- the held-monitor run (PeerWritesLockTests' shape) ------------------------------------------------------------

    private sealed class HeldRun
    {
        public bool Settled = true;
        public bool FinishedWhileHeld;
        public bool UntouchedWhileHeld;
        public Exception? HolderFault, ActorFault;
    }

    /// <summary>A third thread takes <paramref name="target"/>'s monitor; then <paramref name="act"/> runs on a thread of
    /// its own until it finishes or sits blocked for 100 ms; then, still inside the third thread's section,
    /// <paramref name="untouched"/> says whether the target's state is as it was. Then the third thread lets go and both
    /// are joined.</summary>
    private static void WhileAnotherThreadHolds(HeldRun run, Session target, Action act, Func<bool> untouched)
    {
        var held = new ManualResetEventSlim();
        var letGo = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            try
            {
                target.WithState(() =>
                {
                    held.Set();
                    letGo.Wait();
                    run.UntouchedWhileHeld = untouched();
                });
            }
            catch (Exception e) { run.HolderFault = e; }
        }) { IsBackground = true };
        var actor = new Thread(() =>
        {
            try { act(); }
            catch (Exception e) { run.ActorFault = e; }
        }) { IsBackground = true };

        run.Settled = false;
        try
        {
            holder.Start();
            Assert.True(held.Wait(TimeSpan.FromSeconds(30)), "the holder never took the target's monitor");
            actor.Start();
            WaitUntilDoneOrBlocked(actor);
            run.FinishedWhileHeld = !actor.IsAlive;
        }
        finally
        {
            letGo.Set();
        }
        Assert.True(holder.Join(TimeSpan.FromSeconds(60)), "the holder never let go");
        Assert.True(actor.Join(TimeSpan.FromSeconds(60)), "the action never finished after the monitor was free");
        run.Settled = true;
    }

    /// <summary>Wait until <paramref name="t"/> has finished, or has sat blocked for 100 ms straight, or 10 s have passed.</summary>
    private static void WaitUntilDoneOrBlocked(Thread t)
    {
        var clock = Stopwatch.StartNew();
        long blockedSince = -1;
        while (clock.ElapsedMilliseconds < 10_000 && !t.Join(5))
        {
            if ((t.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0) blockedSince = -1;
            else if (blockedSince < 0) blockedSince = clock.ElapsedMilliseconds;
            else if (clock.ElapsedMilliseconds - blockedSince >= 100) return;
        }
    }
}
