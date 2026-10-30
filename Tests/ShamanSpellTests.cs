using System.Collections;
using System.Reflection;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The Shaman spells slice (Caleb, 2026-10-26: P5 "each spell as its RTK script"; Q7 "Port RTK's testing copy"): Shaman's
/// solace, Reincarnate and Shaman's torment do what their RTK scripts do, and the Shaman teacher sells them. Before, all
/// three fell to the <c>generic</c> verb: 5 mana and nothing else (<c>Tests/Fixtures/spell-dispatch-routes.txt</c> had
/// them on <c>verb=generic</c>).
///
/// <para>The scripts: <c>RTK-Server/rtklua/Accepted/Spells/Subpaths/Shaman/shamans_solace.lua</c> and
/// <c>reincarnate.lua</c>, and <c>Subpaths/TESTING/shamans_torment.lua</c>. Each verb is bound by its
/// <c>game-data/SpellParams.csv</c> row. Torment's beat is the periodic spell tick (<c>Session.TickSpellTicks</c>), which
/// the world heartbeat drives; a fact here moves a run's clock on by moving its two deadlines back, as the clock moving
/// on would leave them (the <c>SpellCastSupport.EndBuff</c> seam's shape), and then runs the beat.</para>
///
/// <para>Every cast is the real 0x0F frame through <c>Session.Receive</c>, by a fresh level-99 caster with 20,000 max
/// mana (so the torment's figures are whole: 30 + 1% is 230, 2.5% is 500, 10% is 2,000), at creatures with 0 AC unless a
/// fact says otherwise. The maps are content-free (no Maps.csv row, so PvP is off, as in a town), and no other class
/// uses them.</para>
/// </summary>
[Collection("world")]
public sealed class ShamanSpellTests
{
    private const ushort SolaceMap = 62960, ReviveMap = 62961, TormentMap = 62962, BeatMap = 62963, LeaveMap = 62964,
                         LogoutMap = 62965;
    private const ushort CX = 16, CY = 16;
    private const uint MaxMana = 20_000, Mana = 20_000, Hp = 9_001;
    private const int Creature = 10_000_000;

    /// <summary>Slack on an aether read straight after the cast.</summary>
    private const long SlackMs = 5_000;

    private static readonly FieldInfo TicksField =
        typeof(Session).GetField("_spellTicks", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo PublishTickDue =
        typeof(Session).GetMethod("PublishSpellTickDue", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo EnterMapMethod =
        typeof(Session).GetMethod("EnterMap", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo ActionCountField =
        typeof(Session).GetField("_actionCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo TearDownMethod =
        typeof(Session).GetMethod("TearDownWorldState", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo StateField =
        typeof(Session).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly SessionFixture _fx;
    private static int _serial;

    public ShamanSpellTests(SessionFixture fx) => _fx = fx;

    // ===== Shaman's solace ======================================================================================

    /// <summary>The faced player takes 2,200 vita for 600 mana; the caster reads "You cast Shaman's solace." and the
    /// target the engine's line; the script's graphic 5 plays over the target. Red on be75e7e: 5 mana and nothing
    /// healed.</summary>
    [Fact]
    public void SolaceHealsTheFacedPlayerForItsFigure()
    {
        var (target, tOut, tc) = Player(SolaceMap, CX + 1, CY, hp: 1_000);
        var (caster, cOut, cc) = Caster("shamans_solace", SolaceMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string N(string s) => s.Replace(caster.CharName, "<caster>", StringComparison.Ordinal);
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            string theirs = string.Join(" | ", SpellCastSupport.MiniTexts(tOut).Select(N));
            var drawn = Effects(cOut, target.PlayerId);
            Assert.True(lines == "You cast Shaman's solace." && theirs == "<caster> casts Shaman's solace on you."
                        && tc.Hp == 3_200 && cc.Mp == Mana - 600 && drawn.SequenceEqual(new[] { 5 }),
                        $"lines [{lines}], target's [{theirs}], target vita {tc.Hp} (want 3200), spent {Mana - cc.Mp} " +
                        $"(want 600), graphics over the target [{string.Join(",", drawn)}] (want 5)");
        }
        finally { Leave(SolaceMap, caster, target); }
    }

    /// <summary>Aimed at a player by id, solace reaches 10 tiles on the larger axis and no further: at 11 it does
    /// nothing, silently and for free, as the script's <c>distanceSquare(player, target, 10)</c> return does. Red on
    /// be75e7e: 5 mana at either distance, nothing healed.</summary>
    [Theory]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public void SolaceReachesTenTiles(int tiles, bool heals)
    {
        var (target, _, tc) = Player(SolaceMap, CX + tiles, CY - 3, hp: 1_000);
        var (caster, cOut, cc) = Caster("shamans_solace", SolaceMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0, target.PlayerId));
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            int pose = cOut.BodiesOf(ServerOp.Action).Count;
            string what = $"{tiles} tiles: lines [{lines}], pose {pose}, target vita {tc.Hp}, spent {Mana - cc.Mp}";
            if (heals) Assert.True(tc.Hp == 3_200 && cc.Mp == Mana - 600 && pose > 0, what);
            else Assert.True(tc.Hp == 1_000 && cc.Mp == Mana && lines.Length == 0 && pose == 0, what);
        }
        finally { Leave(SolaceMap, caster, target); }
    }

    /// <summary>A creature takes the heal too, as RTK's <c>Mob.addHealthExtend</c> gives it. Red on be75e7e.</summary>
    [Fact]
    public void SolaceHealsACreature()
    {
        var mob = AddCreature(SolaceMap, CX + 1, CY, hp: 50_000, maxHp: 100_000);
        var (caster, _, cc) = Caster("shamans_solace", SolaceMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(mob.Hp == 52_200 && cc.Mp == Mana - 600, $"creature {mob.Hp} (want 52200), spent {Mana - cc.Mp}");
        }
        finally { _fx.World.LeaveMap(caster, SolaceMap); _fx.World.DespawnMob(SolaceMap, mob); }
    }

    /// <summary>A ghost is answered "You need a different type of cure." and stays a ghost; nothing is spent and no pose
    /// is shown. Red on be75e7e: 5 mana and "You cast Shaman's solace.".</summary>
    [Fact]
    public void AGhostNeedsADifferentCure()
    {
        var (target, _, tc) = Player(SolaceMap, CX + 1, CY, hp: 0);
        var (caster, cOut, cc) = Caster("shamans_solace", SolaceMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            Assert.True(lines == "You need a different type of cure." && tc.Hp == 0 && cc.Mp == Mana
                        && cOut.BodiesOf(ServerOp.Action).Count == 0,
                        $"lines [{lines}], ghost vita {tc.Hp}, spent {Mana - cc.Mp}");
        }
        finally { Leave(SolaceMap, caster, target); }
    }

    /// <summary>With 599 mana: "Your will is too weak.", for free. Red on be75e7e: the 5 mana spent.</summary>
    [Fact]
    public void SolaceWithTooLittleManaIsRefused()
    {
        var (target, _, tc) = Player(SolaceMap, CX + 1, CY, hp: 1_000);
        var (caster, cOut, cc) = Caster("shamans_solace", SolaceMap, mana: 599);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            Assert.True(lines == "Your will is too weak." && tc.Hp == 1_000 && cc.Mp == 599, $"lines [{lines}], mana {cc.Mp}");
        }
        finally { Leave(SolaceMap, caster, target); }
    }

    // ===== Reincarnate ==========================================================================================

    /// <summary>The dead on the caster's four sides come back where they lie with a tenth of their max vita, floored
    /// (9,999 gives 999), and the mana they had, each told "&lt;caster&gt; casts Reincarnate on you." with the script's
    /// graphic 96 over them; a ghost two tiles off and a living player beside the caster are left as they were. The
    /// caster pays 10,000 mana and waits 7,500 s. Red on be75e7e: 5 mana, nobody revived, no wait.</summary>
    [Fact]
    public void ReincarnateRevivesTheDeadBesideTheCaster()
    {
        var (east, eOut, ec) = Player(ReviveMap, CX + 1, CY, hp: 0, maxHp: 9_999, mana: 1_234);
        var (north, _, nc) = Player(ReviveMap, CX, CY - 1, hp: 0, maxHp: 9_999, mana: 77);
        var (far, _, fc) = Player(ReviveMap, CX + 2, CY, hp: 0, maxHp: 9_999);
        var (alive, _, ac) = Player(ReviveMap, CX - 1, CY, hp: 500, maxHp: 9_999);
        var (caster, cOut, cc) = Caster("reincarnate", ReviveMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            long wait = SpellCastSupport.AetherLeft(caster, "reincarnate");
            string N(string s) => s.Replace(caster.CharName, "<caster>", StringComparison.Ordinal);
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            string theirs = string.Join(" | ", SpellCastSupport.MiniTexts(eOut).Select(N));
            var drawn = Effects(cOut, east.PlayerId).Concat(Effects(cOut, north.PlayerId)).ToList();
            var none = Effects(cOut, far.PlayerId).Concat(Effects(cOut, alive.PlayerId)).ToList();
            string what = $"lines [{lines}], east [{theirs}] {ec.Hp}/{ec.Mp}, north {nc.Hp}/{nc.Mp}, two off {fc.Hp}, " +
                          $"living {ac.Hp}, spent {Mana - cc.Mp}, wait {wait} ms, graphics over the two " +
                          $"[{string.Join(",", drawn)}] (want 96,96), over the others [{string.Join(",", none)}]";
            Assert.True(lines == "You cast Reincarnate." && theirs == "<caster> casts Reincarnate on you."
                        && drawn.SequenceEqual(new[] { 96, 96 }) && none.Count == 0
                        && ec.Hp == 999 && ec.Mp == 1_234 && !east.IsDead && nc.Hp == 999 && nc.Mp == 77 && !north.IsDead
                        && fc.Hp == 0 && far.IsDead && ac.Hp == 500 && cc.Mp == Mana - 10_000
                        && wait > 7_500_000 - SlackMs && wait <= 7_500_000, what);
        }
        finally { Leave(ReviveMap, caster, east, north, far, alive); }
    }

    /// <summary>With nobody dead beside them the caster still pays 10,000 and waits, as the script spends before it
    /// looks; a second cast inside the wait is refused for free with this server's wait line. Red on be75e7e: 5 mana, no
    /// wait, and the second cast spent 5 more.</summary>
    [Fact]
    public void ReincarnateWithNobodyDeadStillCostsAndWaits()
    {
        var (caster, cOut, cc) = Caster("reincarnate", ReviveMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            long wait = SpellCastSupport.AetherLeft(caster, "reincarnate");
            Assert.True(cc.Mp == Mana - 10_000 && wait > 7_500_000 - SlackMs,
                        $"spent {Mana - cc.Mp} (want 10000), wait {wait} ms");

            cOut.Clear();
            caster.Receive(SpellCastSupport.CastFrame(0));
            string again = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            Assert.True(again == "Reincarnate isn't ready yet." && cc.Mp == Mana - 10_000, $"again: [{again}], mana {cc.Mp}");
        }
        finally { _fx.World.LeaveMap(caster, ReviveMap); }
    }

    /// <summary>With 9,999 mana: "Your will is too weak.", nothing spent, nobody revived. Red on be75e7e.</summary>
    [Fact]
    public void ReincarnateWithTooLittleManaIsRefused()
    {
        var (east, _, ec) = Player(ReviveMap, CX + 1, CY, hp: 0, maxHp: 9_999);
        var (caster, cOut, cc) = Caster("reincarnate", ReviveMap, mana: 9_999);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            Assert.True(lines == "Your will is too weak." && cc.Mp == 9_999 && ec.Hp == 0, $"lines [{lines}], mana {cc.Mp}");
        }
        finally { Leave(ReviveMap, caster, east); }
    }

    // ===== Shaman's torment: the cast ===========================================================================

    /// <summary>At a player: "This target's soul is too pure for this magic." and nothing else, for 30 + 1% of max mana
    /// (230), with the pose, no "You cast" line, no "total siphon" line (the script's debug line, dropped on the project
    /// owner's decision), and no run started. Red on be75e7e: 5 mana and "You cast Shaman's torment.".</summary>
    [Fact]
    public void TormentAtAPlayerIsTooPure()
    {
        var (target, _, tc) = Player(TormentMap, CX + 1, CY, hp: 1_000);
        var (caster, cOut, cc) = Caster("shamans_torment", TormentMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            var lines = SpellCastSupport.MiniTexts(cOut);
            Assert.True(lines.SequenceEqual(new[] { "This target's soul is too pure for this magic." })
                        && cc.Mp == Mana - 230 && cOut.BodiesOf(ServerOp.Action).Count > 0 && Ticks(caster).Count == 0
                        && tc.Hp == 1_000,
                        $"lines [{string.Join(" | ", lines)}], spent {Mana - cc.Mp} (want 230), ticks {Ticks(caster).Count}");
        }
        finally { Leave(TormentMap, caster, target); }
    }

    /// <summary>The script's mana check is 2.5% of max mana: 499 of 20,000 is refused, "You do not have enough mana.",
    /// for free; 500 casts, silently, and leaves 270. Red on be75e7e: 499 cast for 5.</summary>
    [Theory]
    [InlineData(499u, false)]
    [InlineData(500u, true)]
    public void TormentNeedsTwoAndAHalfPercentOfMaxMana(uint mana, bool casts)
    {
        var mob = AddCreature(TormentMap, CX + 1, CY);
        var (caster, cOut, cc) = Caster("shamans_torment", TormentMap, mana: mana);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            if (casts) Assert.True(lines.Length == 0 && cc.Mp == 270, $"lines [{lines}], mana {cc.Mp} (want 270)");
            else Assert.True(lines == "You do not have enough mana." && cc.Mp == mana && Ticks(caster).Count == 0,
                             $"lines [{lines}], mana {cc.Mp}");
        }
        finally { _fx.World.LeaveMap(caster, TormentMap); _fx.World.DespawnMob(TormentMap, mob); }
    }

    /// <summary>The check (2.5% of max) is below the cost (30 + 1%) when max mana is under 2,000. With 1,000 max and 30
    /// held the check passes and the cost is 40: the cast takes what there is and leaves 0. RTK's own assignment would go
    /// below 0 (an unsigned underflow in its C); here the debit stops at 0. Red on be75e7e: 25 left (5 spent).</summary>
    [Fact]
    public void TormentTakesWhatItCanDownToNothing()
    {
        var mob = AddCreature(TormentMap, CX + 1, CY);
        var (caster, _, cc) = Caster("shamans_torment", TormentMap, mana: 30, maxMana: 1_000);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(cc.Mp == 0 && Ticks(caster).Count == 1, $"mana {cc.Mp} (want 0), ticks {Ticks(caster).Count}");
        }
        finally { _fx.World.LeaveMap(caster, TormentMap); _fx.World.DespawnMob(TormentMap, mob); }
    }

    /// <summary>At a creature: no line at all (no "You cast", as in the script, and no "total siphon" line, the script's
    /// debug line, dropped on the project owner's decision), 230 mana, the script's graphic 84 drawn over the creature
    /// for the caster alone (a player watching sees none), and a run on the creature whose beat comes every 1.5 s for the
    /// script's roll of 5 to 20 whole seconds, each 1.5 s long as RTK's creature timer counts it: 7.5 to 30 s in steps of
    /// 1.5 s. The siphon count is still kept: it reads 1. Red on be75e7e: 5 mana, "You cast Shaman's torment.", no
    /// graphic and no run; red with the debug line put back. The length's check alone passes on 912ba3c's clock-time run
    /// when the roll is 9, 12, 15 or 18 (lengths both counts share), so
    /// <see cref="TheShortestAndLongestRunsLastAsRtksTimerCountsThem"/> pins the two ends with the roll fixed.</summary>
    [Fact]
    public void TormentAtACreatureStartsARun()
    {
        var mob = AddCreature(TormentMap, CX + 1, CY);
        var (watcher, wOut, _) = Player(TormentMap, CX - 1, CY, hp: 1_000);
        var (caster, cOut, cc) = Caster("shamans_torment", TormentMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            var lines = SpellCastSupport.MiniTexts(cOut);
            var ticks = Ticks(caster);
            int held = 0;
            caster.WithState(() => held = caster.LuaTempReg("totalsiphon"));
            var mine = cOut.BodiesOf(ServerOp.Effect).Where(b => Id(b) == mob.Id).ToList();
            var theirs = wOut.BodiesOf(ServerOp.Effect).Where(b => Id(b) == mob.Id).ToList();
            var t = ticks.SingleOrDefault();
            long length = t is null ? 0 : t.UntilIn - t.NextIn + t.Every;   // both were set from one clock reading
            string what = $"lines [{string.Join(" | ", lines)}], siphon {held}, spent {Mana - cc.Mp}, ticks {ticks.Count}, " +
                          $"every {t?.Every}, length {length} ms, caster effects {mine.Count} " +
                          $"({string.Join(",", mine.Select(b => b[4]))}), watcher's {theirs.Count}";
            Assert.True(lines.Count == 0 && held == 1 && cc.Mp == Mana - 230
                        && t is { Spell: "shamans_torment", Verb: "torment_tick", Every: 1_500, Ended: false } && t.Mob == mob.Id
                        && length >= 7_500 && length <= 30_000 && length % 1_500 == 0
                        && mine.Count == 1 && mine[0][4] == 84 + ServerConfig.Current.EfxWireOffset && theirs.Count == 0, what);
        }
        finally { Leave(TormentMap, caster, watcher); _fx.World.DespawnMob(TormentMap, mob); }
    }

    /// <summary>A creature counts once while its run lasts: a second cast at it a second later leaves the siphon count at
    /// 1 and sets the run's length again from now (at least 5 s left) while keeping its beat (half a second off, not a
    /// fresh 1.5 s), as RTK's <c>setDuration(..., 1)</c> sets the time and nothing else; a cast at a second creature makes
    /// it 2. No cast says anything (the script's "total siphon" debug line is dropped on the project owner's decision).
    /// The count is held for the login only: the saved registry has no "totalsiphon". Red on be75e7e: "You cast" lines
    /// and no count.</summary>
    [Fact]
    public void ACreatureCountsOnceWhileItsRunLasts()
    {
        var first = AddCreature(TormentMap, CX + 1, CY);
        var second = AddCreature(TormentMap, CX, CY + 1);
        var (caster, cOut, cc) = Caster("shamans_torment", TormentMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            Advance(caster, 1_000, beat: false);   // a second on, its first beat half a second off
            var before = Ticks(caster).Single();
            cOut.Clear();
            caster.Receive(SpellCastSupport.CastFrame(0));
            string again = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            var after = Ticks(caster).Single();
            int count = 0;
            caster.WithState(() => count = caster.LuaTempReg("totalsiphon"));
            Assert.True(again.Length == 0 && count == 1 && after.NextIn <= before.NextIn && after.NextIn < 1_000
                        && after.UntilIn > 5_000 - SlackMs / 5,
                        $"again: [{again}], siphon {count}, next {before.NextIn} then {after.NextIn} ms, end {before.UntilIn} then {after.UntilIn} ms");

            cOut.Clear();
            caster.Receive(SpellCastSupport.CastFrame(0, second.Id));
            string other = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            int saved = 0, held = 0;
            caster.WithState(() => { saved = caster.Quest(QuestState.Registry).Get("totalsiphon"); held = caster.LuaTempReg("totalsiphon"); });
            Assert.True(other.Length == 0 && Ticks(caster).Count == 2 && saved == 0 && held == 2,
                        $"second creature: [{other}], ticks {Ticks(caster).Count}, saved {saved}, held {held}");
        }
        finally { _fx.World.LeaveMap(caster, TormentMap); _fx.World.DespawnMob(TormentMap, first); _fx.World.DespawnMob(TormentMap, second); }
    }

    // ===== Shaman's torment: the beat ===========================================================================

    /// <summary>Each beat strikes each tormented creature for max mana x 0.1 x 2 / (1 + 2.71828^(-0.95 (n - 1))) / n,
    /// rounded up, plus 1,000, where n is how many creatures on the map the caster torments, kept at 20 at most: 3,000 for
    /// one, 2,443 for two, 2,160 for three, and 1,200 each for twenty-one, the n = 20 figure (1,191 without the cap)
    /// (worked out by hand from 2,000). The siphon count becomes how many there are, 21 included: the script caps only the
    /// figure it divides by. Red on be75e7e: no run; the 21 case red with the cap taken out (#406 review, F2).</summary>
    [Theory]
    [InlineData(1, 3_000)]
    [InlineData(2, 2_443)]
    [InlineData(3, 2_160)]
    [InlineData(21, 1_200)]
    public void EachBeatStrikesForTheScriptsFigure(int creatures, int each)
    {
        var mobs = Enumerable.Range(0, creatures).Select(i => AddCreature(BeatMap, CX + 1 + i, CY - 5)).ToList();
        var (caster, _, _) = Caster("shamans_torment", BeatMap);
        try
        {
            foreach (var m in mobs)
            {
                // The action budget (RTK's three actions a second) would hold back a fourth cast inside the second.
                caster.WithState(() => ActionCountField.SetValue(caster, 0));
                caster.Receive(SpellCastSupport.CastFrame(0, m.Id));
            }
            Assert.Equal(creatures, Ticks(caster).Count);
            Advance(caster, 1_500);
            int held = 0;
            caster.WithState(() => held = caster.LuaTempReg("totalsiphon"));
            Assert.True(mobs.All(m => Lost(m) == each) && held == creatures,
                        $"lost {string.Join("/", mobs.Select(Lost))} (want {each} each), siphon {held} (want {creatures})");
        }
        finally { _fx.World.LeaveMap(caster, BeatMap); foreach (var m in mobs) _fx.World.DespawnMob(BeatMap, m); }
    }

    /// <summary>The blow is netted against the creature's armour like every spell blow here, and never lands under 1,000:
    /// at AC -95 the 3,000 nets to 150 and lands 1,000; at AC 50 it lands 4,500. Red with the floor taken out of the
    /// strike: 150.</summary>
    [Theory]
    [InlineData(-95, 1_000)]
    [InlineData(50, 4_500)]
    public void TheBlowIsNettedAndNeverUnderAThousand(int ac, int lands)
    {
        var mob = AddCreature(BeatMap, CX + 1, CY, ac: ac);
        var (caster, _, _) = Caster("shamans_torment", BeatMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            Advance(caster, 1_500);
            Assert.True(Lost(mob) == lands, $"AC {ac}: lost {Lost(mob)} (want {lands})");
        }
        finally { _fx.World.LeaveMap(caster, BeatMap); _fx.World.DespawnMob(BeatMap, mob); }
    }

    /// <summary>A run strikes on every beat of its rolled length and then ends: a beat 1.5 s apart while the beat
    /// falls before the end, so a 7.5 s run strikes four times and a 30 s run nineteen. Red on be75e7e: no run.</summary>
    [Fact]
    public void ARunStrikesEveryBeatOfItsLengthThenEnds()
    {
        var mob = AddCreature(BeatMap, CX + 1, CY);
        var (caster, _, _) = Caster("shamans_torment", BeatMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            var t = Ticks(caster).Single();
            long length = t.UntilIn - t.NextIn + t.Every;
            int beats = (int)((length - 1) / 1_500);   // beats at 1.5 s, 3 s, ... strictly before the end
            for (int i = 0; i < 20; i++) Advance(caster, 1_500);
            Assert.True(Lost(mob) == beats * 3_000 && Ticks(caster).Count == 0,
                        $"{length} ms run: lost {Lost(mob)} (want {beats} beats of 3000), ticks left {Ticks(caster).Count}");
        }
        finally { _fx.World.LeaveMap(caster, BeatMap); _fx.World.DespawnMob(BeatMap, mob); }
    }

    /// <summary>The shortest and the longest runs, the script's rolls of 5 and 20 seconds, last as RTK's creature timer
    /// counts them (the project owner's choice): <c>rtk/src/map/mob.c</c>'s <c>mob_timer_spawns</c> steps every 50 ms
    /// through a 30-step cycle and its <c>mob_duratimer</c> takes 1,000 off a creature's duration once a cycle, so a
    /// second lasts 1.5 s and the runs 7.5 s and 30 s. Their beats come 1.5 s apart, strictly before the end: 4 and 19.
    /// The verb runs as a cast runs it (<c>ApplyCast</c>'s <c>SpellScript.Run</c>, under the caster's monitor), with the
    /// spell's real row and its roll pinned to the row's own bound. Red on 912ba3c (clock time): 5,000 ms and 3 beats,
    /// 20,000 ms and 13.</summary>
    [Theory]
    [InlineData("duration", 7_500, 4)]
    [InlineData("durationMax", 30_000, 19)]
    public void TheShortestAndLongestRunsLastAsRtksTimerCountsThem(string bound, int lasts, int beats)
    {
        var mob = AddCreature(BeatMap, CX + 1, CY);
        var (caster, _, _) = Caster("shamans_torment", BeatMap);
        try
        {
            var sp = Content.SpellByKey("shamans_torment")!;
            var real = Content.SpellParams[sp.Key];
            var row = new Dictionary<string, string>(real) { ["duration"] = real[bound], ["durationMax"] = real[bound] };
            bool? cast = caster.WithState(() => SpellScript.Run(real["verb"], new SpellContext(caster, sp, mob.Id, null), row));
            var t = Ticks(caster).SingleOrDefault();
            long length = t is null ? 0 : t.UntilIn - t.NextIn + t.Every;   // both were set from one clock reading
            for (int i = 0; i < 20; i++) Advance(caster, 1_500);              // 30 s, the longest run's end
            Assert.True(cast == true && length == lasts && Lost(mob) == beats * 3_000 && Ticks(caster).Count == 0,
                        $"roll {int.Parse(real[bound]) / 1_000} s: cast {cast}, length {length} ms (want {lasts}), " +
                        $"lost {Lost(mob)} (want {beats} beats of 3000), ticks left {Ticks(caster).Count}");
        }
        finally { _fx.World.LeaveMap(caster, BeatMap); _fx.World.DespawnMob(BeatMap, mob); }
    }

    /// <summary>When the caster has left the creature's map, the next beat ends the run and still lands, as the script
    /// orders it (its <c>setDuration(..., 0)</c> does not return); no beat follows. The graphic is not drawn for a
    /// caster who can no longer see the creature. Red with the map check taken out of the beat: a second beat lands.</summary>
    [Fact]
    public void LeavingTheMapEndsTheRunAfterOneLastBeat()
    {
        var mob = AddCreature(LeaveMap, CX + 1, CY);
        var (caster, cOut, _) = Caster("shamans_torment", LeaveMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            var home = Content.Maps[SessionFixture.HomeMap];
            caster.WithState(() => EnterMapMethod.Invoke(caster, new object[]
            {
                home.Id, home.Xs, home.Ys, (ushort)5, (ushort)10, home.Name, ArrivalPolicy.Clamp,
            }));
            cOut.Clear();
            Advance(caster, 1_500);
            int first = Lost(mob);
            int drawn = cOut.BodiesOf(ServerOp.Effect).Count(b => Id(b) == mob.Id);
            Advance(caster, 1_500);
            Assert.True(first == 3_000 && Lost(mob) == 3_000 && Ticks(caster).Count == 0 && drawn == 0,
                        $"after leaving: lost {first} then {Lost(mob)} (want 3000 both), ticks {Ticks(caster).Count}, drawn {drawn}");
        }
        finally { _fx.World.LeaveMap(caster, SessionFixture.HomeMap); _fx.World.DespawnMob(LeaveMap, mob); }
    }

    /// <summary>The world heartbeat runs the beat: a run whose beat is due strikes on the next world tick, with nothing
    /// else calling it. Red without the call in World.FlushTick's per-player loop.</summary>
    [Fact]
    public void TheWorldHeartbeatRunsTheBeat()
    {
        var mob = AddCreature(BeatMap, CX + 1, CY);
        var (caster, _, _) = Caster("shamans_torment", BeatMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            Advance(caster, 1_500, beat: false);
            _fx.World.TickOnceForTest();
            Assert.True(Lost(mob) == 3_000, $"lost {Lost(mob)} on the world's beat (want 3000)");
        }
        finally { _fx.World.LeaveMap(caster, BeatMap); _fx.World.DespawnMob(BeatMap, mob); }
    }

    /// <summary>A beat that kills its creature pays the caster its experience and ends the run; a dead creature is no
    /// creature, so no beat follows. Red on be75e7e: no run.</summary>
    [Fact]
    public void ABeatThatKillsPaysAndEndsTheRun()
    {
        var mob = AddCreature(BeatMap, CX + 1, CY, hp: 2_500, maxHp: 2_500);
        mob.Exp = 777;
        var (caster, _, cc) = Caster("shamans_torment", BeatMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            uint exp = cc.Exp;
            Advance(caster, 1_500);
            Advance(caster, 1_500);
            Assert.True(!mob.Alive && cc.Exp > exp && Ticks(caster).Count == 0,
                        $"alive {mob.Alive}, exp {exp} -> {cc.Exp}, ticks {Ticks(caster).Count}");
        }
        finally { _fx.World.LeaveMap(caster, BeatMap); if (mob.Alive) _fx.World.DespawnMob(BeatMap, mob); }
    }

    /// <summary>A logout ends the runs: no beat lands for a caster who has gone. The heartbeat takes its roster before it
    /// reaches each player, so a logout can run to its end in between: here a beat is made due, the roster taken, the real
    /// teardown run under the caster's monitor, and the heartbeat's call made from that roster. No blow, no graphic, and
    /// the runs are gone. Red without the logout check at the top of the beat: the beat runs (#406 review, F1).</summary>
    [Fact]
    public void NoBeatRunsAfterTheCastersLogout()
    {
        var mob = AddCreature(LogoutMap, CX + 1, CY);
        var (caster, cOut, _) = Caster("shamans_torment", LogoutMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            Advance(caster, 1_500, beat: false);                           // a beat is due
            var roster = _fx.World.Online.All();                           // the heartbeat's snapshot
            Assert.Contains(caster, roster);
            caster.WithState(() => TearDownMethod.Invoke(caster, null));   // the logout, run to its end
            cOut.Clear();
            foreach (var s in roster) if (ReferenceEquals(s, caster)) s.TickSpellTicks();
            int drawn = cOut.BodiesOf(ServerOp.Effect).Count(b => Id(b) == mob.Id);
            Assert.True(Lost(mob) == 0 && drawn == 0 && Ticks(caster).Count == 0,
                        $"after the logout: lost {Lost(mob)}, graphics {drawn}, runs left {Ticks(caster).Count}");
        }
        finally { if (mob.Alive) _fx.World.DespawnMob(LogoutMap, mob); }
    }

    /// <summary>A logout that gets in while a beat waits for the Lua gate lands no blow either. The beat runs its verb in
    /// the caster's monitor and enters the gate from there; on the gate's slow path the monitor is dropped while it waits,
    /// and a logout can run to its end in that gap. Here another thread holds the gate, the beat starts, takes the
    /// monitor, and waits at the gate; the teardown then runs on this thread (it needs the monitor the beat dropped), and
    /// only then is the gate let go. The fact checks the order it meant: the beat is still waiting after the teardown,
    /// which it would not be had the teardown come first (it would have returned at once, finding the caster gone). No
    /// blow lands and the runs are gone. Red without the logout check in the strike: the blow lands (#406 review, F1).
    /// <para>Deterministic but for one assumption: that the first wait the beat thread is seen in comes after it took the
    /// caster's monitor. Nothing before that monitor waits (a volatile read, then <c>EnterState</c> on a monitor nobody
    /// holds), and the after-teardown check would fail the fact, not pass it, if that ever did not hold.</para></summary>
    [Fact]
    public void ALogoutWhileTheBeatWaitsForTheLuaGateLandsNoBlow()
    {
        var mob = AddCreature(LogoutMap, CX + 1, CY);
        var (caster, _, _) = Caster("shamans_torment", LogoutMap);
        var gateHeld = new ManualResetEventSlim();
        var releaseGate = new ManualResetEventSlim();
        Thread? holder = null, beat = null;
        Exception? beatFault = null;
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            Advance(caster, 1_500, beat: false);                           // a beat is due

            holder = new Thread(() => { using (Session.EnterScriptGate()) { gateHeld.Set(); releaseGate.Wait(Bound); } })
                     { IsBackground = true, Name = "gate holder" };
            holder.Start();
            Assert.True(gateHeld.Wait(Bound), "the gate holder never took the gate");

            beat = new Thread(() => { try { caster.TickSpellTicks(); } catch (Exception e) { beatFault = e; } })
                   { IsBackground = true, Name = "heartbeat" };
            beat.Start();
            Assert.True(SpinWait.SpinUntil(() => (beat.ThreadState & ThreadState.WaitSleepJoin) != 0, Bound),
                        "the beat never waited");

            caster.WithState(() => TearDownMethod.Invoke(caster, null));   // the logout, in the gap the gate leaves
            Assert.False(SpinWait.SpinUntil(() => !beat.IsAlive, 250),
                         "the beat finished before the gate was let go: the teardown came first, not in the gate's gap");
            Assert.False(Monitor.IsEntered(StateField.GetValue(caster)!));

            releaseGate.Set();
            Assert.True(beat.Join(Bound), "the beat never finished once the gate was let go");
            Assert.Null(beatFault);
            Assert.True(Lost(mob) == 0 && Ticks(caster).Count == 0,
                        $"after the logout in the gate's gap: lost {Lost(mob)}, runs left {Ticks(caster).Count}");
        }
        finally
        {
            releaseGate.Set();
            holder?.Join(Bound);
            beat?.Join(Bound);
            if (mob.Alive) _fx.World.DespawnMob(LogoutMap, mob);
        }
    }

    // ===== the rows ============================================================================================

    /// <summary>All three are Shaman's path's rows at the level their scripts ask (solace 99, Reincarnate 64; torment's
    /// TESTING script asks nothing, so its row's 0), solace keeping its mark 1. Torment's row was on path 5 (Archon,
    /// which nobody plays), where the Shaman teacher could not sell it. Red on be75e7e for Reincarnate (level 0) and
    /// torment (path 5); solace's case is a guard, since its mark already loads it at 99
    /// (<c>Content.MarkSpellLevel</c>).</summary>
    [Theory]
    [InlineData("shamans_solace", 99, 1)]
    [InlineData("reincarnate", 64, 0)]
    [InlineData("shamans_torment", 0, 0)]
    public void TheRowsAreShamansAtTheirScriptsLevels(string key, int level, int mark)
    {
        var sp = Content.SpellByKey(key)!;
        Assert.Equal((20, level, mark), (sp.PathId, sp.Level, sp.Mark));
    }

    /// <summary>The six blank rows in Shaman's block stay as they are (no RTK script): the same Spells.csv lines, on
    /// path 5 with no identifier, which load as no spell at all. A guard, green on be75e7e too.</summary>
    [Fact]
    public void TheSixBlankRowsStayAsTheyAre()
    {
        var lines = File.ReadAllLines(Path.Combine(RepoPaths.Root(), "game-data", "Spells.csv"));
        for (int id = 31204; id <= 31209; id++)
        {
            Assert.Contains($"{id},,,5,5,-1,0,0,,0,0,0,1,1,1", lines);
            Assert.Null(Content.SpellById(id));
        }
    }

    // ===== helpers ============================================================================================

    private sealed record TickView(string Spell, string Verb, uint Mob, int Every, long NextIn, long UntilIn, bool Ended);

    /// <summary>The caster's running ticks, read under their monitor; times are milliseconds from now.</summary>
    private static List<TickView> Ticks(Session s)
    {
        var list = new List<TickView>();
        s.WithState(() =>
        {
            long now = Environment.TickCount64;
            foreach (var t in (IList)TicksField.GetValue(s)!)
                list.Add(new TickView((string)F(t, "SpellKey"), (string)F(t, "Verb"), (uint)F(t, "MobId"), (int)F(t, "EveryMs"),
                                      (long)F(t, "Next") - now, (long)F(t, "Until") - now, (bool)F(t, "Ended")));
        });
        return list;
    }

    /// <summary>Move the caster's runs <paramref name="ms"/> on: each beat time and end moved back by that much, which is
    /// where the clock moving on would leave them, the hint republished as the list's writers keep it; then, unless
    /// <paramref name="beat"/> is false, the heartbeat's call.</summary>
    private static void Advance(Session s, long ms, bool beat = true)
    {
        s.WithState(() =>
        {
            foreach (var t in (IList)TicksField.GetValue(s)!)
            {
                t.GetType().GetField("Next")!.SetValue(t, (long)F(t, "Next") - ms);
                t.GetType().GetField("Until")!.SetValue(t, (long)F(t, "Until") - ms);
            }
            PublishTickDue.Invoke(s, null);
        });
        if (beat) s.TickSpellTicks();
    }

    private static object F(object o, string name) => o.GetType().GetField(name)!.GetValue(o)!;

    private static uint Id(byte[] effectBody) =>
        (uint)((effectBody[0] << 24) | (effectBody[1] << 16) | (effectBody[2] << 8) | effectBody[3]);

    /// <summary>The spell graphics (0x29) <paramref name="outbound"/> was sent over <paramref name="id"/>, as effect ids.</summary>
    private static List<int> Effects(RecordingOutbound outbound, uint id) =>
        outbound.BodiesOf(ServerOp.Effect).Where(b => Id(b) == id).Select(b => b[4] - ServerConfig.Current.EfxWireOffset).ToList();

    private Mob AddCreature(ushort map, int x, int y, int hp = Creature, int maxHp = Creature, int ac = 0)
    {
        var mob = new Mob(_fx.World.AllocateMobId(), 1, (ushort)x, (ushort)y, $"Shaman target {Interlocked.Increment(ref _serial)}", maxHp);
        mob.Hp = hp;
        mob.Ac = ac;
        _fx.World.AddMob(map, mob);
        return mob;
    }

    private static int Lost(Mob mob) => mob.MaxHp - mob.Hp;

    /// <summary>A level-99 Shaman at (CX, CY) facing east, with <paramref name="key"/> in slot 0.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Caster(string key, ushort map, uint mana = Mana,
                                                                            uint maxMana = MaxMana) =>
        _fx.PlayerWith($"Shaman{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.ClassName = "Shaman";
            ch.Level = 99;
            ch.MaxHp = Hp; ch.Hp = Hp;
            ch.MaxMp = maxMana; ch.Mp = mana;
            ch.Dir = 1;
            Wide(ch);
            ch.Spells.Add(Content.SpellByKey(key)!.Id);
        }, map, CX, CY);

    private (Session session, RecordingOutbound outbound, Character c) Player(ushort map, int x, int y, uint hp,
                                                                            uint maxHp = 1_000_000, uint mana = 1_000) =>
        _fx.PlayerWith($"ShamanTarget{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 50;
            ch.MaxHp = maxHp; ch.Hp = hp;
            ch.MaxMp = mana; ch.Mp = mana;
            Wide(ch);
        }, map, (ushort)x, (ushort)y);

    /// <summary>Content-free maps have no size, and the cast handler needs one.</summary>
    private static void Wide(Character c) { c.MapXs = 100; c.MapYs = 100; }

    private void Leave(ushort map, params Session[] sessions)
    {
        foreach (var s in sessions) _fx.World.LeaveMap(s, map);
    }
}
