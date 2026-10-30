using System.Reflection;
using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The Barbarian spells slice (the project owner, 2026-10-26, P5 "each spell as its RTK script", P1 and P3 "on sale once
/// it works, at its script's price"; 2026-10-30, Q4 "Yes, as RTK": Push and Break jaw on any player on any map with no PK
/// check, Sonhi rage on RTK's tiers and armour sign). Before, all fifteen fell to the <c>generic</c> verb, 5 mana and
/// "You cast X." and nothing else (<c>Tests/Fixtures/spell-dispatch-routes.txt</c> had them on <c>verb=generic</c>);
/// Survival challenge, which has no script of its own, still does.
///
/// <para>The scripts: <c>RTK-Server/rtklua/Accepted/Spells/Subpaths/Barbarian/</c>, one file each. Sonhi rage, Dash,
/// Barbarian cry, Mount, Wilderness guise, Push, Insult, Rend, Break jaw and Dishonor are bound by their
/// <c>game-data/SpellParams.csv</c> rows; Envigorate (Heal), Tiger's might (Buff) and Barbarian rage (Buff, with its
/// <c>game-data/SpellMods.csv</c> rage row) by their <c>game-data/spell_effects.csv</c> rows; Wilderness weapon and Nomadic
/// blade are enchants (SpellMods.csv).</para>
///
/// <para>Every cast is the real 0x0F frame through <c>Session.Receive</c>, by a fresh level-99 Barbarian facing east. Dash
/// and the map-object fact need terrain, so they run on the Barbarian Guide map (3511, no NPC stands on it and no other
/// class uses it); the facts that strike or curse a player run on the Savage Arena (3018, a PvP map with no spawns and no
/// NPCs); the rest on content-free maps (no Maps.csv row, so PvP is off, as in a town).</para>
/// </summary>
[Collection("world")]
public sealed class BarbarianSpellTests
{
    private const ushort GuideMap = 3511, Arena = 3018;
    private const ushort RageMap = 63240, CryMap = 63241, RendMap = 63242, MountMap = 63243, GuiseMap = 63244,
                         PushMap = 63245, JawMap = 63246, CurseMap = 63247, SelfMap = 63248;
    private const ushort CX = 16, CY = 16;
    private const uint MaxMana = 200_000, Mana = 200_000, Hp = 10_000;

    /// <summary>Slack on a wait or a run read straight after the cast.</summary>
    private const long SlackMs = 5_000;

    private static readonly FieldInfo RageUntilField =
        typeof(Session).GetField("_rageUntil", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo MorphLookField =
        typeof(Session).GetField("_morphLook", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo MorphUntilField =
        typeof(Session).GetField("_morphUntil", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo StatusFlagsField =
        typeof(Session).GetField("_statusFlags", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo NextActionTick =
        typeof(Session).GetField("_nextActionTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ActionCount =
        typeof(Session).GetField("_actionCount", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly SessionFixture _fx;
    private static int _serial;

    public BarbarianSpellTests(SessionFixture fx) => _fx = fx;

    // ===== Sonhi rage ===========================================================================================

    /// <summary>Six casts climb RTK's copy of Chung ryong rage: rage 6/9/12/18/27/50 for 3,000/10,000/22,000/38,000/
    /// 78,000/150,000 mana, AC 0 then 5 worse a tier (the script's recast adds armor), each tier's own line (the first names
    /// Chung Ryong's rage, as the script does), one 938 s run armed by the first cast, a 115 s wait; a seventh cast at the
    /// top does nothing, silently and for free. The wait is cleared between casts. Red on 912ba3c: generic's 5 mana a cast
    /// and no rage.</summary>
    [Fact]
    public void SonhiRageClimbsSixTiers()
    {
        var (caster, cOut, cc) = Caster("sonhi_rage", RageMap, CX, CY, mana: 400_000, maxMana: 400_000);
        int[] mults = { 6, 9, 12, 18, 27, 50 }, mana = { 3_000, 10_000, 22_000, 38_000, 78_000, 150_000 };
        string[] said =
        {
            "[Rage 1] You cast Chung Ryong's rage.", "[Rage 2] Sonhi's rage grows within you.",
            "[Rage 3] Sonhi's rage inspires you.", "[Rage 4] Your body trembles with incredible strength.",
            "[Rage 5] You enter a mindless frenzy.", "[Rage 6] Your body is torn apart with Sonhi's rage.",
        };
        try
        {
            int baseArmor = caster.ItemArmor;
            long firstEnd = 0;
            for (int tier = 1; tier <= 6; tier++)
            {
                uint before = cc.Mp;
                cOut.Clear();
                FreeSlot(caster);
                caster.Receive(SpellCastSupport.CastFrame(0));
                var lines = SpellCastSupport.MiniTexts(cOut);
                var (amount, left) = SpellCastSupport.Rage(caster);
                long wait = SpellCastSupport.AetherLeft(caster, "sonhi_rage");
                int armor = caster.ItemArmor - baseArmor;
                if (tier == 1) firstEnd = Environment.TickCount64 + left;
                long end = Environment.TickCount64 + left;
                Assert.True(lines.SequenceEqual(new[] { said[tier - 1] }) && amount == mults[tier - 1] && before - cc.Mp == mana[tier - 1]
                            && armor == 5 * (tier - 1) && wait > 115_000 - SlackMs && wait <= 115_000
                            && left > 938_000 - SlackMs * 2 && Math.Abs(end - firstEnd) < SlackMs,
                            $"tier {tier}: [{string.Join(" | ", lines)}], rage {amount}, spent {before - cc.Mp}, armor {armor}, " +
                            $"wait {wait} ms, run left {left} ms");
                SpellCastSupport.ClearAether(caster, "sonhi_rage");
            }
            uint top = cc.Mp;
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(SpellCastSupport.MiniTexts(cOut).Count == 0 && cc.Mp == top && SpellCastSupport.Rage(caster).Amount == 50,
                        $"at the top: [{string.Join(" | ", SpellCastSupport.MiniTexts(cOut))}], spent {top - cc.Mp}");
        }
        finally { Leave(RageMap, caster); }
    }

    /// <summary>Inside its 115 s wait, "Sonhi rage isn't ready yet."; short of a tier's mana, "Your will is too weak.". Both
    /// free. Red on 912ba3c: generic's 5 mana and "You cast Sonhi rage.".</summary>
    [Fact]
    public void SonhiRageRefusesInsideItsWaitAndShortOfMana()
    {
        var (caster, cOut, cc) = Caster("sonhi_rage", RageMap, CX, CY + 2);
        var (poor, pOut, pc) = Caster("sonhi_rage", RageMap, CX, CY + 4, mana: 2_999);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            uint after = cc.Mp;
            cOut.Clear();
            caster.Receive(SpellCastSupport.CastFrame(0));
            string again = Lines(cOut);
            poor.Receive(SpellCastSupport.CastFrame(0));
            string short_ = Lines(pOut);
            Assert.True(again == "Sonhi rage isn't ready yet." && cc.Mp == after && SpellCastSupport.Rage(caster).Amount == 6
                        && short_ == "Your will is too weak." && pc.Mp == 2_999 && SpellCastSupport.Rage(poor).Amount == 1,
                        $"again [{again}] spent {after - cc.Mp}; short [{short_}] mana {pc.Mp}");
        }
        finally { Leave(RageMap, caster, poor); }
    }

    /// <summary>A Sonhi rage climb that lapses at tier 6 keeps a tenth of its vita (RTK's uncast, <c>health * .1</c>), takes
    /// no mana and says nothing: the rtk_rage ladder's price, found by the fury's name. Red without the SpellParams.csv row
    /// (no climb at all) or with the row's ladder taken off (Chung Ryong's price: 1 vita and 1 mana, and its line).</summary>
    [Fact]
    public void ALapsedSonhiRageKeepsATenthOfItsVitaAtTierSix()
    {
        var (caster, cOut, cc) = Caster("sonhi_rage", RageMap, CX + 6, CY, mana: 400_000, maxMana: 400_000);
        try
        {
            for (int i = 0; i < 6; i++)
            {
                FreeSlot(caster);
                caster.Receive(SpellCastSupport.CastFrame(0));
                SpellCastSupport.ClearAether(caster, "sonhi_rage");
            }
            Assert.Equal(6, caster.LuaCrRageTier);
            uint mana = cc.Mp;
            cOut.Clear();
            caster.WithState(() => RageUntilField.SetValue(caster, Environment.TickCount64 - 1));
            caster.RegenTick(0, regenDue: false);
            Assert.True(cc.Hp == 1_000 && cc.Mp == mana && SpellCastSupport.MiniTexts(cOut).Count == 0 && caster.LuaCrRageTier == 0,
                        $"vita {cc.Hp} (want 1000), mana {mana} -> {cc.Mp}, lines [{Lines(cOut)}]");
        }
        finally { Leave(RageMap, caster); }
    }

    // ===== Dash =================================================================================================

    /// <summary>Barbarian's Dash is Do's: three tiles in the faced direction over open ground, a 150 s wait, "You cast
    /// Dash." and no mana spent (the script checks its 250 and never takes it). Red on 912ba3c: 5 mana and no move.</summary>
    [Fact]
    public void DashJumpsThreeTilesAndArmsItsWait()
    {
        var (x, y) = OpenRun(GuideMap, 4);
        var (caster, cOut, cc) = Caster("dash_barbarian", GuideMap, x, y);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            long wait = SpellCastSupport.AetherLeft(caster, "dash_barbarian");
            Assert.True((cc.X, cc.Y) == (x + 3, y) && cc.Mp == Mana && Lines(cOut) == "You cast Dash."
                        && wait > 150_000 - SlackMs && wait <= 150_000,
                        $"at ({cc.X},{cc.Y}) (want ({x + 3},{y})), spent {Mana - cc.Mp}, lines [{Lines(cOut)}], wait {wait} ms");
        }
        finally { Leave(GuideMap, caster); }
    }

    // ===== Barbarian cry ========================================================================================

    /// <summary>At a creature: it takes three times the caster's vita (30,000 of a 10,000-vita caster, its AC 0), the
    /// caster keeps a third (math.ceil(10,000 x 0.33) = 3,300) and pays 3,100 mana, a 1,200 s wait, the shout over their
    /// head, and the script's three lines in its order, its copy slip included. Red on 912ba3c: 5 mana, "You cast
    /// Barbarian cry." and nothing struck.</summary>
    [Fact]
    public void BarbarianCryStrikesTheFacedCreatureAndLeavesAThird()
    {
        var (caster, cOut, cc) = Caster("barbarian_cry", CryMap, CX, CY);
        var mob = AddCreature(CryMap, CX + 1, CY, 1_000_000);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            long wait = SpellCastSupport.AetherLeft(caster, "barbarian_cry");
            Assert.True(mob.Hp == 1_000_000 - 30_000 && cc.Hp == 3_300 && cc.Mp == Mana - 3_100
                        && wait > 1_200_000 - SlackMs && wait <= 1_200_000
                        && Lines(cOut) == "You cast Townie. | You can not touch the spirit world. | You cast Barbarian cry."
                        && Speech(cOut).Any(s => s.Contains("TOWNIE~~!")),
                        $"creature {mob.Hp}, vita {cc.Hp}, spent {Mana - cc.Mp}, wait {wait}, lines [{Lines(cOut)}], " +
                        $"speech [{string.Join(" | ", Speech(cOut))}]");
        }
        finally { Leave(CryMap, caster); _fx.World.DespawnMob(CryMap, mob); }
    }

    /// <summary>The cost is the script's whatever stands there: with nothing faced the vita, mana and wait are paid and the
    /// three lines said. A player faced off a PvP map is passed over, as RTK's <c>canPK</c> gate passes them, and the
    /// caster pays as at nothing. Short of 3,100, "You do not have enough mana." and nothing else. Red on 912ba3c: 5 mana and
    /// "You cast Barbarian cry." each time.</summary>
    [Fact]
    public void BarbarianCryPaysWhateverStandsThereAndPassesOverAPlayerOffAPvpMap()
    {
        var (empty, eOut, ec) = Caster("barbarian_cry", CryMap, CX, CY + 4);
        var (town, tOut, tc) = Caster("barbarian_cry", CryMap, CX, CY + 8);
        var (other, oOut, oc) = Player(CryMap, CX + 1, CY + 8);
        var (poor, pOut, pc) = Caster("barbarian_cry", CryMap, CX, CY + 12, mana: 3_099);
        try
        {
            empty.Receive(SpellCastSupport.CastFrame(0));
            town.Receive(SpellCastSupport.CastFrame(0));
            poor.Receive(SpellCastSupport.CastFrame(0));
            const string three = "You cast Townie. | You can not touch the spirit world. | You cast Barbarian cry.";
            Assert.True(ec.Hp == 3_300 && ec.Mp == Mana - 3_100 && Lines(eOut) == three
                        && tc.Hp == 3_300 && tc.Mp == Mana - 3_100 && Lines(tOut) == three && oc.Hp == 100_000
                        && Lines(pOut) == "You do not have enough mana." && pc.Hp == Hp && pc.Mp == 3_099
                        && SpellCastSupport.AetherLeft(poor, "barbarian_cry") == 0,
                        $"empty: vita {ec.Hp}, spent {Mana - ec.Mp}, [{Lines(eOut)}]; town: vita {tc.Hp}, other {oc.Hp}, " +
                        $"[{Lines(tOut)}]; poor: [{Lines(pOut)}] vita {pc.Hp} mana {pc.Mp}");
        }
        finally { Leave(CryMap, empty, town, other, poor); }
    }

    /// <summary>On a PvP map the faced player takes the blow: 30,000 of their 100,000 (AC 0), with the engine's line for a
    /// player struck. Red on 912ba3c: nothing struck.</summary>
    [Fact]
    public void BarbarianCryStrikesTheFacedPlayerOnAPvpMap()
    {
        Assert.True(Content.IsPvpMap(Arena));
        var (x, y) = ArenaSpot(0);
        var (caster, _, cc) = Caster("barbarian_cry", Arena, x, y);
        var (other, oOut, oc) = Player(Arena, x + 1, y);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(oc.Hp == 100_000 - 30_000 && cc.Hp == 3_300 && Lines(oOut).Contains($"{cc.Name} hits you with Barbarian cry."),
                        $"other {oc.Hp}, caster {cc.Hp}, other's lines [{Lines(oOut)}]");
        }
        finally { Leave(Arena, caster, other); }
    }

    // ===== Rend =================================================================================================

    /// <summary>At a creature: twice the caster's vita plus twice their mana (2 x 10,000 + 2 x 20,000 = 60,000, its AC 0);
    /// the caster is left with 5% of their max vita (500) and no mana, an 87 s wait, the shout and the script's two lines.
    /// No mana is asked: a caster with none still rends, for twice their vita. Red on 912ba3c: 5 mana and nothing struck
    /// (and "Not enough mana" at 0).</summary>
    [Theory]
    [InlineData(20_000u, 60_000)]
    [InlineData(0u, 20_000)]
    public void RendStrikesTheFacedCreatureAndLeavesFivePercent(uint mana, int dealt)
    {
        var (caster, cOut, cc) = Caster("rend", RendMap, CX, CY + (int)(mana / 10_000), mana: mana);
        var mob = AddCreature(RendMap, CX + 1, CY + (int)(mana / 10_000), 1_000_000);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            long wait = SpellCastSupport.AetherLeft(caster, "rend");
            Assert.True(mob.Hp == 1_000_000 - dealt && cc.Hp == 500 && cc.Mp == 0 && wait > 87_000 - SlackMs && wait <= 87_000
                        && Lines(cOut) == "You cast Rend. | You can not touch the spirit world."
                        && Speech(cOut).Any(s => s.Contains("Rraah~!")),
                        $"creature {mob.Hp} (want {1_000_000 - dealt}), vita {cc.Hp}, mana {cc.Mp}, wait {wait}, lines [{Lines(cOut)}]");
        }
        finally { Leave(RendMap, caster); _fx.World.DespawnMob(RendMap, mob); }
    }

    /// <summary>On a PvP map Rend takes the faced player, the first thing RTK's <c>getTargetFacing(BL_ALL)</c> lists. Red on
    /// 912ba3c: nothing struck.</summary>
    [Fact]
    public void RendStrikesTheFacedPlayerOnAPvpMap()
    {
        var (x, y) = ArenaSpot(1);
        var (caster, _, cc) = Caster("rend", Arena, x, y, mana: 20_000);
        var (other, oOut, oc) = Player(Arena, x + 1, y);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(oc.Hp == 100_000 - 60_000 && cc.Hp == 500 && Lines(oOut).Contains($"{cc.Name} hits you with Rend."),
                        $"other {oc.Hp}, caster {cc.Hp}, other's lines [{Lines(oOut)}]");
        }
        finally { Leave(Arena, caster, other); }
    }

    // ===== Mount ================================================================================================

    /// <summary>A horse (mobs.csv <c>horse</c>) appears on the faced tile, a 187 s wait, "You cast Mount.", no mana spent
    /// (the script checks its 600 and never takes it), and the ride key rides it away. Red on 912ba3c: 5 mana and no
    /// horse ("Good try, but there is nothing here that you can ride.").</summary>
    [Fact]
    public void MountPutsAHorseOnTheFacedTileThatTheRideKeyRides()
    {
        var (caster, cOut, cc) = Caster("mount", MountMap, CX, CY);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            var horse = _fx.World.MobAt(MountMap, CX + 1, CY);
            long wait = SpellCastSupport.AetherLeft(caster, "mount");
            Assert.True(horse is { Key: "horse" } && cc.Mp == Mana && Lines(cOut) == "You cast Mount."
                        && wait > 187_000 - SlackMs && wait <= 187_000,
                        $"faced: {horse?.Key ?? "nothing"}, spent {Mana - cc.Mp}, lines [{Lines(cOut)}], wait {wait}");
            cOut.Clear();
            caster.Receive(SessionFixture.Frame(ClientOp.Setting, new byte[] { 0x00, 0x01, 0x00 }));
            Assert.True(cc.Mounted && Lines(cOut) == "The powerful steed takes you where you want to go."
                        && _fx.World.MobAt(MountMap, CX + 1, CY) is null,
                        $"mounted {cc.Mounted}, lines [{Lines(cOut)}]");
        }
        finally { Leave(MountMap, caster); }
    }

    /// <summary>With the faced tile taken, the horse comes on the caster's own tile (the script's else branch); short of 600,
    /// "You do not have enough mana." and no horse. Red on 912ba3c: no horse either way.</summary>
    [Fact]
    public void MountFallsBackToTheCastersTileAndRefusesShortOfMana()
    {
        var (caster, _, _) = Caster("mount", MountMap, CX, CY + 4);
        var block = AddCreature(MountMap, CX + 1, CY + 4, 1_000);
        var (poor, pOut, pc) = Caster("mount", MountMap, CX, CY + 8, mana: 599);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            var own = _fx.World.MobAt(MountMap, CX, CY + 4);
            poor.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(own is { Key: "horse" } && Lines(pOut) == "You do not have enough mana." && pc.Mp == 599
                        && _fx.World.MobAt(MountMap, CX + 1, CY + 8) is null && _fx.World.MobAt(MountMap, CX, CY + 8) is null,
                        $"own tile: {own?.Key ?? "nothing"}; poor [{Lines(pOut)}]");
            _fx.World.DespawnMob(MountMap, own!);
        }
        finally { Leave(MountMap, caster, poor); _fx.World.DespawnMob(MountMap, block); }
    }

    /// <summary>RTK's spawn test refuses a tile that carries any map object (<c>getObject ~= 0</c>), even one a player can
    /// walk over: on the Barbarian Guide map, facing such a tile, the horse comes on the caster's own tile. Red with the
    /// object read taken out of <c>Session.RtkTileFree</c>.</summary>
    [Fact]
    public void MountRefusesAFacedTileThatCarriesAMapObject()
    {
        var (x, y) = FacingAnObject(GuideMap);
        var (caster, _, _) = Caster("mount", GuideMap, x, y);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            var own = _fx.World.MobAt(GuideMap, x, y);
            var front = _fx.World.MobAt(GuideMap, x + 1, y);
            Assert.True(own is { Key: "horse" } && front is null, $"own {own?.Key ?? "nothing"}, faced {front?.Key ?? "nothing"}");
            _fx.World.DespawnMob(GuideMap, own!);
        }
        finally { Leave(GuideMap, caster); }
    }

    // ===== Wilderness guise =====================================================================================

    /// <summary>The cast puts RTK's menu up, "Which guise would you like to use?" over its five guises, with no portrait,
    /// spends nothing (the script checks its 30 and never takes it) and says "You cast Wilderness guise."; picking a guise
    /// takes on its look for 300 s: Wolf 23, Thirsty ogre 185. Red on 912ba3c: generic's 5 mana and no menu.</summary>
    [Theory]
    [InlineData(1, 23)]
    [InlineData(5, 185)]
    public void WildernessGuiseOffersRtksMenuAndTakesThePickedLook(int pick, int look)
    {
        var (caster, cOut, cc) = Caster("wilderness_guise", GuiseMap, CX, CY + pick);
        try
        {
            Bare(() => caster.Receive(SpellCastSupport.CastFrame(0)));
            var menus = Menus(cOut);
            Assert.True(menus.SequenceEqual(new[] { "Which guise would you like to use?|Wolf|Rabbit|Deer|Sheep|Thirsty ogre" })
                        && Lines(cOut) == "You cast Wilderness guise." && cc.Mp == Mana && !caster.IsMorphed,
                        $"menus [{string.Join(" / ", menus)}], lines [{Lines(cOut)}], spent {Mana - cc.Mp}");
            Bare(() => caster.Receive(MenuFrame((byte)pick)));
            Assert.True(SpinWait.SpinUntil(() => caster.IsMorphed, TimeSpan.FromSeconds(10)), "the pick never morphed the caster");
            var (got, left) = Morph(caster);
            Assert.True(got == look && left > 300_000 - SlackMs && left <= 300_000 && cc.Mp == Mana,
                        $"look {got} (want {look}), {left} ms left, spent {Mana - cc.Mp}");
        }
        finally { Leave(GuiseMap, caster); }
    }

    /// <summary>A second cast inside the script's 1 s lock says "Spell already cast."; short of 30 mana, "Your will is too
    /// weak."; a box closed without a pick changes nothing. Each refusal puts no menu up. Red on 912ba3c: generic's "You
    /// cast" and 5 mana each time.</summary>
    [Fact]
    public void WildernessGuiseRefusesInsideItsLockAndShortOfMana()
    {
        var (caster, cOut, _) = Caster("wilderness_guise", GuiseMap, CX + 6, CY);
        var (poor, pOut, pc) = Caster("wilderness_guise", GuiseMap, CX + 6, CY + 4, mana: 29);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            poor.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(cOut) == "Spell already cast." && Menus(cOut).Count == 0 && !caster.IsMorphed
                        && Lines(pOut) == "Your will is too weak." && Menus(pOut).Count == 0 && pc.Mp == 29,
                        $"again [{Lines(cOut)}] menus {Menus(cOut).Count}; poor [{Lines(pOut)}] menus {Menus(pOut).Count}");
        }
        finally { Leave(GuiseMap, caster, poor); }
    }

    /// <summary>A pick ends any form the caster was in, as the script's <c>removeDuras(morphs)</c>: Wolf, then (the lock
    /// over) Rabbit, and the look is Rabbit's 21. Red without <c>morphTo</c>'s apply.</summary>
    [Fact]
    public void ALaterPickReplacesTheEarlierGuise()
    {
        var (caster, _, _) = Caster("wilderness_guise", GuiseMap, CX + 6, CY + 8);
        try
        {
            Bare(() => caster.Receive(SpellCastSupport.CastFrame(0)));
            Bare(() => caster.Receive(MenuFrame(1)));
            Assert.True(SpinWait.SpinUntil(() => Morph(caster).Look == 23, TimeSpan.FromSeconds(10)), "no wolf");
            caster.WithState(() => ((Dictionary<string, long>)StatusFlagsField.GetValue(caster)!).Remove("wilderness_guise"));
            FreeSlot(caster);
            Bare(() => caster.Receive(SpellCastSupport.CastFrame(0)));
            Bare(() => caster.Receive(MenuFrame(2)));
            Assert.True(SpinWait.SpinUntil(() => Morph(caster).Look == 21, TimeSpan.FromSeconds(10)),
                        $"still look {Morph(caster).Look}");
        }
        finally { Leave(GuiseMap, caster); }
    }

    // ===== Push =================================================================================================

    /// <summary>The faced player goes one tile on in the caster's facing, off a PvP map (Q4: any player, any map), for 600
    /// mana; the caster reads "You cast Push.", the pushed player "&lt;caster&gt; casts Push spell on you.". With the first
    /// tile taken they go two on; with both taken, "You can't push it." and the mana is spent. Red on 912ba3c: generic's 5
    /// mana and nobody moves.</summary>
    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 3)]
    [InlineData(2, 1)]
    public void PushMovesTheFacedPlayerOnOrSaysItCannot(int taken, int landsAt)
    {
        int y = CY + taken * 3;
        var (caster, cOut, cc) = Caster("push", PushMap, CX, y);
        var (other, oOut, oc) = Player(PushMap, CX + 1, y);
        var blocks = Enumerable.Range(2, taken).Select(dx => AddCreature(PushMap, CX + dx, y, 1_000)).ToList();
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string mine = taken == 2 ? "You can't push it." : "You cast Push.";
            string theirs = taken == 2 ? "" : $"{cc.Name} casts Push spell on you.";
            Assert.True(other.PlayerX == CX + landsAt && other.PlayerY == y && cc.Mp == Mana - 600
                        && Lines(cOut) == mine && Lines(oOut) == theirs,
                        $"{taken} taken: other at ({other.PlayerX},{other.PlayerY}) (want x {CX + landsAt}), spent {Mana - cc.Mp}, " +
                        $"caster [{Lines(cOut)}], other [{Lines(oOut)}]");
        }
        finally { Leave(PushMap, caster, other); foreach (var b in blocks) _fx.World.DespawnMob(PushMap, b); }
    }

    /// <summary>Nobody faced, or only a ghost: nothing happens and nothing is spent, silently. Short of 600: "Your will is
    /// too weak." and nobody moves. Red on 912ba3c: generic's 5 mana and "You cast Push." each time.</summary>
    [Fact]
    public void PushRefusesForFree()
    {
        var (lone, lOut, lc) = Caster("push", PushMap, CX + 10, CY);
        var (atGhost, gOut, gc) = Caster("push", PushMap, CX + 10, CY + 3);
        var (ghost, _, _) = Player(PushMap, CX + 11, CY + 3, ch => ch.Hp = 0);
        var (poor, pOut, pc) = Caster("push", PushMap, CX + 10, CY + 6, mana: 599);
        var (other, _, _) = Player(PushMap, CX + 11, CY + 6);
        try
        {
            lone.Receive(SpellCastSupport.CastFrame(0));
            atGhost.Receive(SpellCastSupport.CastFrame(0));
            poor.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(lOut) == "" && lc.Mp == Mana && Lines(gOut) == "" && gc.Mp == Mana && ghost.PlayerX == CX + 11
                        && Lines(pOut) == "Your will is too weak." && pc.Mp == 599 && other.PlayerX == CX + 11,
                        $"lone [{Lines(lOut)}] {Mana - lc.Mp}; ghost [{Lines(gOut)}] {Mana - gc.Mp} at {ghost.PlayerX}; " +
                        $"poor [{Lines(pOut)}] other at {other.PlayerX}");
        }
        finally { Leave(PushMap, lone, atGhost, ghost, poor, other); }
    }

    /// <summary>RTK's bounds are <c>x &lt; getMapXMax</c>, the width less one: a player one tile from the map's last
    /// column cannot be pushed into it, nor past it. Red with the bound at the width (the push lands on the last column).</summary>
    [Fact]
    public void PushNeverLandsOnRtksLastColumn()
    {
        var (caster, cOut, _) = Caster("push", PushMap, 96, CY + 12);
        var (other, _, _) = Player(PushMap, 97, CY + 12);
        var block = AddCreature(PushMap, 98, CY + 12, 1_000);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(other.PlayerX == 97 && Lines(cOut) == "You can't push it.",
                        $"other at {other.PlayerX}, caster [{Lines(cOut)}]");
        }
        finally { Leave(PushMap, caster, other); _fx.World.DespawnMob(PushMap, block); }
    }

    // ===== Break jaw ============================================================================================

    /// <summary>The faced player, off a PvP map (Q4), speaks garbled for 60 s: a "Break jaw" entry in their buff list, the
    /// line "&lt;caster&gt; lands a swift blow to your jaw.", and every letter of what they say kept or turned to '*', some
    /// of them turned. The caster pays nothing (the script names 600 and never asks it), arms a 150 s wait and reads "You
    /// cast Break jaw.". Red on 912ba3c: generic's "Not enough mana" at 0 mana, and plain speech.</summary>
    [Fact]
    public void BreakJawGarblesTheFacedPlayersSpeech()
    {
        var (caster, cOut, cc) = Caster("break_jaw", JawMap, CX, CY, mana: 0);
        var (other, oOut, oc) = Player(JawMap, CX + 1, CY);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            long wait = SpellCastSupport.AetherLeft(caster, "break_jaw");
            var jaw = SpellCastSupport.BuffEntry(other, "break_jaw");
            Assert.True(Lines(cOut) == "You cast Break jaw." && wait > 150_000 - SlackMs && wait <= 150_000
                        && Lines(oOut) == $"{cc.Name} lands a swift blow to your jaw."
                        && jaw is { Category: Session.GarbledSpeech, Name: "Break jaw" } && jaw.Value.LeftMs > 60_000 - SlackMs,
                        $"caster [{Lines(cOut)}] wait {wait}; other [{Lines(oOut)}] entry {jaw}");

            string said = Said(other, oOut, oc.Name, Phrase);
            Assert.True(said.Length == Phrase.Length && said.Contains('*')
                        && said.Select((ch, i) => ch == '*' || ch == Phrase[i]).All(ok => ok),
                        $"heard [{said}]");
        }
        finally { Leave(JawMap, caster, other); }
    }

    /// <summary>A jaw already broken stays as it is: a second Break jaw (its wait cleared) says nothing to the player and
    /// leaves the first one's time; with nobody faced the cast still lands, its wait armed. Red with the hasDuration check
    /// taken out of <c>LuaSpeechStatus</c> (a second line).</summary>
    [Fact]
    public void ASecondBreakJawLeavesTheFirstAndNobodyFacedStillCasts()
    {
        var (caster, cOut, _) = Caster("break_jaw", JawMap, CX, CY + 4);
        var (other, oOut, _) = Player(JawMap, CX + 1, CY + 4);
        var (lone, lOut, _) = Caster("break_jaw", JawMap, CX, CY + 8);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            long first = SpellCastSupport.BuffEntry(other, "break_jaw")!.Value.LeftMs;
            oOut.Clear();
            SpellCastSupport.ClearAether(caster, "break_jaw");
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            long second = SpellCastSupport.BuffEntry(other, "break_jaw")!.Value.LeftMs;
            lone.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(oOut) == "" && second <= first && Lines(lOut) == "You cast Break jaw."
                        && SpellCastSupport.AetherLeft(lone, "break_jaw") > 0,
                        $"second time [{Lines(oOut)}] left {first} -> {second}; lone [{Lines(lOut)}]");
        }
        finally { Leave(JawMap, caster, other, lone); }
    }

    /// <summary>At its time the jaw ends with "Your jaw feels better." (its SpellText.csv fade line, the script's uncast) and
    /// the player speaks plainly again. Red without the fade row (no line).</summary>
    [Fact]
    public void ABrokenJawEndsWithItsLineAndPlainSpeech()
    {
        var (caster, _, _) = Caster("break_jaw", JawMap, CX + 10, CY);
        var (other, oOut, oc) = Player(JawMap, CX + 11, CY);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            SpellCastSupport.EndBuff(other, "break_jaw");
            oOut.Clear();
            other.RegenTick(0, regenDue: true);
            Assert.Equal("Your jaw feels better.", Lines(oOut));
            Assert.Equal(Phrase, Said(other, oOut, oc.Name, Phrase));
        }
        finally { Leave(JawMap, caster, other); }
    }

    /// <summary>The other speech status, built here for Spy's Dagger to throat (its script's
    /// <c>setDuration("dagger_to_throat")</c>, read by speech.lua:14-17): a muted player who says anything reads "Your
    /// anxiety renders you speechless." and nobody hears a word. Red without the mute check in <c>HandleChat</c>.</summary>
    [Fact]
    public void AMutedVoiceSaysNothing()
    {
        var (other, oOut, oc) = Player(JawMap, CX + 10, CY + 4);
        try
        {
            other.WithState(() => other.ReceiveCurse("", 0, 20_000, "dagger_to_throat", "Dagger to throat", Session.MutedSpeech));
            oOut.Clear();
            other.Receive(ChatFrame(Phrase));
            Assert.True(Lines(oOut) == Session.SpeechlessLine && !Speech(oOut).Any(s => s.Contains(Phrase[..10])),
                        $"lines [{Lines(oOut)}], speech [{string.Join(" | ", Speech(oOut))}]");
        }
        finally { Leave(JawMap, other); }
    }

    // ===== Insult and Dishonor ==================================================================================

    /// <summary>Each creature on the caster's four sides takes the curse: its curses slot under the spell's key, AC +30 (it
    /// takes more damage, RTK's sign), the scripts' while_cast graphic 34 every second; 200 mana, "You cast Insult.", and no
    /// wait (the script never arms its 150 s). Dishonor is the same with +50. Red on 912ba3c: generic's 5 mana and nothing
    /// cursed.</summary>
    [Theory]
    [InlineData("insult", 30)]
    [InlineData("dishonor", 50)]
    public void TheCurseLandsOnEachCreatureAround(string key, int armor)
    {
        int y = key == "insult" ? CY : CY + 10;
        var (caster, cOut, cc) = Caster(key, CurseMap, CX, y);
        var mobs = new[] { (0, -1), (1, 0), (0, 1), (-1, 0) }.Select(d => AddCreature(CurseMap, CX + d.Item1, y + d.Item2, 1_000)).ToList();
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string name = Content.SpellByKey(key)!.Name;
            Assert.True(cc.Mp == Mana - 200 && Lines(cOut) == $"You cast {name}." && SpellCastSupport.AetherLeft(caster, key) == 0,
                        $"spent {Mana - cc.Mp}, lines [{Lines(cOut)}], wait {SpellCastSupport.AetherLeft(caster, key)}");
            foreach (var m in mobs)
                Assert.True(_fx.World.MobStatusKey(m, "curses") == key && m.Ac == armor && m.FxRepeatAnim == 34 && m.FxRepeatEvery == 1_000,
                            $"creature at ({m.X},{m.Y}): slot {_fx.World.MobStatusKey(m, "curses")}, AC {m.Ac}, beat {m.FxRepeatAnim}/{m.FxRepeatEvery}");
        }
        finally { Leave(CurseMap, caster); foreach (var m in mobs) _fx.World.DespawnMob(CurseMap, m); }
    }

    /// <summary>A creature that carries a curse already is passed over (the scripts' <c>checkIfCast(curses)</c>); a dead
    /// player met on the walk ends it, as the scripts' <c>return</c> does: the ghost stands north, the walk's first side, so
    /// the creatures east, south and west are never cursed. Short of 200, "Your will is too weak.". Red with the dead
    /// player's stop taken out (the three creatures cursed).</summary>
    [Fact]
    public void TheCursePassesOverTheCursedAndStopsAtADeadPlayer()
    {
        var (caster, _, cc) = Caster("insult", CurseMap, CX + 10, CY);
        var cursed = AddCreature(CurseMap, CX + 11, CY, 1_000);
        _fx.World.ApplyMobStatus(cursed, "curses", 60_000, hold: false, blind: false, spellKey: "vex_mage");
        var (walker, _, wc) = Caster("insult", CurseMap, CX + 10, CY + 4);
        var (ghost, _, _) = Player(CurseMap, CX + 10, CY + 3, ch => ch.Hp = 0);
        var after = new[] { (1, 0), (0, 1), (-1, 0) }.Select(d => AddCreature(CurseMap, CX + 10 + d.Item1, CY + 4 + d.Item2, 1_000)).ToList();
        var (poor, pOut, pc) = Caster("insult", CurseMap, CX + 10, CY + 8, mana: 199);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            walker.Receive(SpellCastSupport.CastFrame(0));
            poor.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(cursed.Ac == 0 && _fx.World.MobStatusKey(cursed, "curses") == "vex_mage" && cc.Mp == Mana - 200,
                        $"cursed creature AC {cursed.Ac}, slot {_fx.World.MobStatusKey(cursed, "curses")}");
            Assert.True(after.All(m => m.Ac == 0 && _fx.World.MobStatusKey(m, "curses") == "") && wc.Mp == Mana - 200,
                        "past the ghost: " + string.Join(", ", after.Select(m => $"({m.X},{m.Y}) AC {m.Ac}")));
            Assert.True(Lines(pOut) == "Your will is too weak." && pc.Mp == 199, $"poor [{Lines(pOut)}]");
        }
        finally
        {
            Leave(CurseMap, caster, walker, ghost, poor);
            _fx.World.DespawnMob(CurseMap, cursed);
            foreach (var m in after) _fx.World.DespawnMob(CurseMap, m);
        }
    }

    /// <summary>On a PvP map a player beside the caster is cursed too: AC +30, the curses slot, "&lt;caster&gt; attacks you
    /// with Insult spell.", and "Insult end." when it runs out. A second Insult passes over the player it already cursed
    /// (the scripts' <c>checkIfCast(curses)</c>), saying nothing to them. Off a PvP map a player beside the caster is
    /// passed over (the scripts' <c>canPK</c>). Red on 912ba3c: nobody cursed.</summary>
    [Fact]
    public void TheCurseLandsOnAnAttackablePlayerAroundAndEndsWithItsLine()
    {
        var (x, y) = ArenaSpot(2);
        var (caster, _, cc) = Caster("insult", Arena, x, y);
        var (other, oOut, _) = Player(Arena, x + 1, y);
        var (townCaster, _, _) = Caster("insult", CurseMap, CX + 20, CY);
        var (townOther, tOut, _) = Player(CurseMap, CX + 21, CY);
        try
        {
            int before = other.ItemArmor;
            caster.Receive(SpellCastSupport.CastFrame(0));
            townCaster.Receive(SpellCastSupport.CastFrame(0));
            var entry = SpellCastSupport.BuffEntry(other, "insult");
            Assert.True(entry is { Category: "curses", Name: "Insult" } && other.ItemArmor - before == 30
                        && Lines(oOut) == $"{cc.Name} attacks you with Insult spell."
                        && SpellCastSupport.BuffEntry(townOther, "insult") is null && Lines(tOut) == "",
                        $"arena: entry {entry}, armor +{other.ItemArmor - before}, lines [{Lines(oOut)}]; town: [{Lines(tOut)}]");

            oOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(oOut) == "" && other.ItemArmor - before == 30,
                        $"a second Insult on the cursed player: [{Lines(oOut)}], armor +{other.ItemArmor - before}");

            SpellCastSupport.EndBuff(other, "insult");
            oOut.Clear();
            other.RegenTick(0, regenDue: true);
            Assert.Equal("Insult end.", Lines(oOut));
        }
        finally { Leave(Arena, caster, other); Leave(CurseMap, townCaster, townOther); }
    }

    // ===== Envigorate, Tiger's might, Barbarian rage, the two weapons ===========================================

    /// <summary>Envigorate heals its caster 5,000 for 1,000 mana and arms a 500 s wait, inside which it is refused for free.
    /// Red on 912ba3c: generic's 5 mana and no heal.</summary>
    [Fact]
    public void EnvigorateHealsItsCasterAndWaits()
    {
        var (caster, cOut, cc) = Caster("envigorate", SelfMap, CX, CY, configure: ch => ch.Hp = 1_000);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            long wait = SpellCastSupport.AetherLeft(caster, "envigorate");
            Assert.True(cc.Hp == 6_000 && cc.Mp == Mana - 1_000 && wait > 500_000 - SlackMs && wait <= 500_000,
                        $"vita {cc.Hp}, spent {Mana - cc.Mp}, wait {wait}, lines [{Lines(cOut)}]");
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(cOut).StartsWith("Envigorate isn't ready yet") && cc.Hp == 6_000 && cc.Mp == Mana - 1_000,
                        $"again [{Lines(cOut)}]");
        }
        finally { Leave(SelfMap, caster); }
    }

    /// <summary>Tiger's might: +3 Might for 625 s in the mights slot for 30 mana; a second cast is refused while it runs.
    /// Red on 912ba3c: generic's 5 mana and no Might.</summary>
    [Fact]
    public void TigersMightRaisesMightForItsTime()
    {
        var (caster, cOut, cc) = Caster("tigers_might", SelfMap, CX, CY + 4);
        try
        {
            int before = caster.LuaMight;
            caster.Receive(SpellCastSupport.CastFrame(0));
            var entry = SpellCastSupport.BuffEntry(caster, "tigers_might");
            Assert.True(caster.LuaMight - before == 3 && cc.Mp == Mana - 30 && entry is { Category: "mights" }
                        && entry.Value.LeftMs > 625_000 - SlackMs && Lines(cOut) == "You cast Tiger's might.",
                        $"might +{caster.LuaMight - before}, spent {Mana - cc.Mp}, entry {entry}, lines [{Lines(cOut)}]");
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(cOut) == "You already cast that spell." && cc.Mp == Mana - 30, $"again [{Lines(cOut)}]");
        }
        finally { Leave(SelfMap, caster); }
    }

    /// <summary>Barbarian rage: the whole swing x3 for 625 s, 90 mana; refused while a fury runs. Red on 912ba3c: generic's 5
    /// mana and no rage.</summary>
    [Fact]
    public void BarbarianRageTriplesTheSwingForItsTime()
    {
        var (caster, cOut, cc) = Caster("barbarian_rage", SelfMap, CX, CY + 8);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            var (amount, left) = SpellCastSupport.Rage(caster);
            Assert.True(amount == 3 && left > 625_000 - SlackMs && left <= 625_000 && cc.Mp == Mana - 90,
                        $"rage {amount} for {left} ms, spent {Mana - cc.Mp}, lines [{Lines(cOut)}]");
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(cOut) == "You are already benefiting from a fury." && cc.Mp == Mana - 90, $"again [{Lines(cOut)}]");
        }
        finally { Leave(SelfMap, caster); }
    }

    /// <summary>Each weapon spell arms its enchant at its script's multiplier for its 10,000 mana: Wilderness weapon x4,
    /// Nomadic blade x8. Red on 912ba3c: the enchant stays at 1 and generic's 5 is spent.</summary>
    [Theory]
    [InlineData("wilderness_weapon", 4.0)]
    [InlineData("nomadic_blade", 8.0)]
    public void EachWeaponArmsItsEnchant(string key, double times)
    {
        Assert.Equal((times, 10_000), Content.EnchantFor(Content.SpellByKey(key)!));
        var (caster, cOut, cc) = Caster(key, SelfMap, CX, key == "nomadic_blade" ? CY + 14 : CY + 12);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            double armed = SpellCastSupport.Enchant(caster);
            Assert.True(armed == times && cc.Mp == Mana - 10_000 && Lines(cOut) == $"You cast {Content.SpellByKey(key)!.Name}.",
                        $"{key}: enchant {armed} (want {times}), spent {Mana - cc.Mp}, lines [{Lines(cOut)}]");
        }
        finally { Leave(SelfMap, caster); }
    }

    // ===== the teacher ==========================================================================================

    /// <summary>The Barbarian teacher's list by rank, each at its script's level: thirteen from mark 0 (Push at its row's 0,
    /// its script having no list; Envigorate at 9; Insult at 70; the rest at 99), Wilderness weapon from mark 1 and Nomadic
    /// blade from mark 2. Survival challenge, still generic, is not on sale. Red without the price rows: nothing.</summary>
    [Theory]
    [InlineData(0, "barbarian_cry barbarian_rage break_jaw dash_barbarian dishonor envigorate insult mount push rend sonhi_rage tigers_might wilderness_guise")]
    [InlineData(1, "barbarian_cry barbarian_rage break_jaw dash_barbarian dishonor envigorate insult mount push rend sonhi_rage tigers_might wilderness_guise wilderness_weapon")]
    [InlineData(2, "barbarian_cry barbarian_rage break_jaw dash_barbarian dishonor envigorate insult mount nomadic_blade push rend sonhi_rage tigers_might wilderness_guise wilderness_weapon")]
    public void TheBarbarianTeacherSellsByRank(int mark, string expected)
    {
        var sold = Content.SubpathTeacherSpells(10, 99, mark);
        var levels = sold.ToDictionary(s => s.Key, s => s.Level);
        Assert.Equal(expected, string.Join(" ", sold.Select(s => s.Key).OrderBy(k => k, StringComparer.Ordinal)));
        Assert.All(levels, kv => Assert.Equal(kv.Key switch { "push" => 0, "envigorate" => 9, "insult" => 70, _ => 99 }, kv.Value));
    }

    // ===== helpers ==============================================================================================

    /// <summary>A long line every letter of which is distinct from '*', so a garbled copy can be read letter by letter.</summary>
    private const string Phrase = "the quick brown fox jumps over the lazy dog and keeps running past the old mill " +
                                  "toward the river where the boats wait for the morning tide to carry them home again";

    /// <summary>Have <paramref name="s"/> say <paramref name="text"/> (the real 0x0E say frame) and return what their own
    /// bubble carried after "&lt;name&gt;: ", as long as the text.</summary>
    private static string Said(Session s, RecordingOutbound o, string name, string text)
    {
        o.Clear();
        s.Receive(ChatFrame(text));
        string prefix = $"{name}: ";
        foreach (var line in Speech(o))
        {
            int at = line.IndexOf(prefix, StringComparison.Ordinal);
            if (at >= 0 && line.Length >= at + prefix.Length + text.Length) return line.Substring(at + prefix.Length, text.Length);
        }
        return "";
    }

    private static byte[] ChatFrame(string text)
    {
        byte[] m = Encoding.ASCII.GetBytes(text);
        var body = new List<byte> { 0, (byte)m.Length };
        body.AddRange(m);
        return SessionFixture.Frame(ClientOp.Chat, body.ToArray());
    }

    private static List<string> Speech(RecordingOutbound o) =>
        o.BodiesOf(ServerOp.Speech).Select(b => Encoding.ASCII.GetString(b)).ToList();

    private static string Lines(RecordingOutbound o) => string.Join(" | ", SpellCastSupport.MiniTexts(o));

    private static byte[] MenuFrame(byte index)
    {
        var body = new byte[11];
        body[0] = 0x02;
        body[10] = index;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>Every menu (0x30 kind 2) the player was shown, as "prompt|option|option...".</summary>
    private static List<string> Menus(RecordingOutbound o)
    {
        var menus = new List<string>();
        foreach (var b in o.BodiesOf(ServerOp.NpcDialog).Where(b => b.Length >= 23 && b[0] == 2 && b[1] == 2))
        {
            int len = (b[21] << 8) | b[22];
            var parts = new List<string> { Encoding.ASCII.GetString(b, 23, len) };
            int at = 23 + len, n = b[at++];
            for (int i = 0; i < n; i++)
            {
                int ol = b[at++];
                parts.Add(Encoding.ASCII.GetString(b, at, ol));
                at += ol;
            }
            menus.Add(string.Join("|", parts));
        }
        return menus;
    }

    private static (int Look, long LeftMs) Morph(Session s)
    {
        (int, long) got = default;
        s.WithState(() => got = ((ushort)MorphLookField.GetValue(s)!, (long)MorphUntilField.GetValue(s)! - Environment.TickCount64));
        return got;
    }

    private static void FreeSlot(Session s) => s.WithState(() => { NextActionTick.SetValue(s, 0L); ActionCount.SetValue(s, 0); });

    /// <summary>Run <paramref name="body"/> on a thread of its own, with no synchronisation context, as a read loop runs a
    /// packet: an await inside it (the menu's) then resumes inline on the thread that answers it, as on the server, rather
    /// than being posted to the test runner's context.</summary>
    private static void Bare(Action body)
    {
        Exception? fault = null;
        var t = new Thread(() => fault = Record.Exception(body)) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(30)), "the packet thread never finished");
        Assert.Null(fault);
    }

    /// <summary>A tile on <paramref name="map"/> facing east with <paramref name="open"/> tiles after it that RTK's spawn
    /// test passes (no object, passable) and walkable, the tile itself walkable; the <paramref name="skip"/>-th such tile,
    /// so two facts on one map stand apart.</summary>
    private static (int X, int Y) OpenRun(ushort map, int open, int skip = 0)
    {
        var md = MapData.For(map)!;
        for (int y = 2; y < md.Ys - 2; y += 3)
            for (int x = 2; x + open + 2 < md.Xs; x++)
            {
                bool fits = md.Obj(x, y) == 0 && md.Pass(x, y) == 0;
                for (int i = 1; i <= open && fits; i++)
                    fits = md.Obj(x + i, y) == 0 && md.Pass(x + i, y) == 0 && !md.BlockedMove(x + i, y, 1);
                if (!fits) continue;
                if (skip-- == 0) return (x, y);
                x += open + 2;
            }
        throw new InvalidOperationException($"no run of {open} open tiles on map {map}");
    }

    /// <summary>Where the <paramref name="n"/>-th arena fact's caster stands, its target east of it: rows four apart, so no
    /// two facts' casters are beside each other. The arena's terrain file is not in the repository (MapData has none for
    /// it), and nothing here reads its terrain: a blow and a curse look only at who stands where.</summary>
    private static (int X, int Y) ArenaSpot(int n) => (10, 10 + 4 * n);

    /// <summary>A walkable tile on <paramref name="map"/> whose east neighbour carries a map object on a passable tile.</summary>
    private static (int X, int Y) FacingAnObject(ushort map)
    {
        var md = MapData.For(map)!;
        for (int y = 1; y < md.Ys - 2; y++)
            for (int x = 1; x < md.Xs - 3; x++)
                if (md.Obj(x, y) == 0 && md.Pass(x, y) == 0 && md.Obj(x + 1, y) != 0 && md.Pass(x + 1, y) == 0)
                    return (x, y);
        throw new InvalidOperationException($"no tile on map {map} faces an object on passable ground");
    }

    private Mob AddCreature(ushort map, int x, int y, int hp)
    {
        var mob = new Mob(_fx.World.AllocateMobId(), 1, (ushort)x, (ushort)y, $"Barbarian target {Interlocked.Increment(ref _serial)}", hp);
        _fx.World.AddMob(map, mob);
        return mob;
    }

    /// <summary>A level-99 Barbarian at (<paramref name="x"/>, <paramref name="y"/>) facing east, with <paramref name="key"/>
    /// in slot 0. On a map with terrain, the map's own size; elsewhere a wide one.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Caster(string key, ushort map, int x, int y,
                                                                            uint mana = Mana, uint maxMana = MaxMana,
                                                                            Action<Character>? configure = null) =>
        _fx.PlayerWith($"Barb{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.ClassName = "Barbarian";
            ch.Level = 99;
            ch.Ac = 0;   // the base AC sits inside ItemArmor's clamp, so a tier's AC shows
            ch.MaxHp = Hp; ch.Hp = Hp;
            ch.MaxMp = maxMana; ch.Mp = mana;
            ch.Dir = 1;
            Size(ch, map);
            ch.Spells.Add(Content.SpellByKey(key)!.Id);
            configure?.Invoke(ch);
        }, map, (ushort)x, (ushort)y);

    private (Session session, RecordingOutbound outbound, Character c) Player(ushort map, int x, int y,
                                                                            Action<Character>? configure = null) =>
        _fx.PlayerWith($"BarbOther{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 50;
            ch.Ac = 0;
            ch.MaxHp = 100_000; ch.Hp = 100_000;
            Size(ch, map);
            configure?.Invoke(ch);
        }, map, (ushort)x, (ushort)y);

    private static void Size(Character c, ushort map)
    {
        if (Content.Maps.TryGetValue(map, out var m)) { c.MapXs = m.Xs; c.MapYs = m.Ys; }
        else { c.MapXs = 100; c.MapYs = 100; }
    }

    private void Leave(ushort map, params Session[] sessions)
    {
        foreach (var s in sessions) _fx.World.LeaveMap(s, map);
    }
}
