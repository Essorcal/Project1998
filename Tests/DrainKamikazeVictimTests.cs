using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Drain and Kamikaze read their victim and strike the same victim, or refuse. #343's review, F2: each verb read its
/// victim through one resolver and struck through another, and the two disagree on an id.
/// <list type="bullet">
/// <item>Drain read its creature's HP through <c>ResolveCastTarget</c>, which falls back to the faced creature for an
/// id that names no creature, then struck through <c>ResolveDamageTarget</c>, which resolves an id directly. Aimed
/// at an id nobody has with a weak creature faced: "You cast Drain.", 60 mana, the caster healed by the creature's
/// life, and the creature untouched, repeatably. Aimed at a player on the caster's map, the blow went to that player
/// (on a PvP map) or answered "You can't attack that target.", and the caster was healed off the creature
/// anyway.</item>
/// <item>Kamikaze asked <c>ctx.hasTarget</c> (the same cast-target resolver) and blasted through
/// <c>ctx:damage</c>. At an id nobody has: 120 mana, the caster left on 10 HP, and nothing hit.</item>
/// </list>
///
/// <para>Each verb now resolves its victim once, through the damage resolver, the one its blow always used, and
/// the HP read, the animation and the blow all read that one answer (<c>SpellContext</c>'s victim). Where the blow
/// had no creature to strike, the verb refuses, free: Drain with "It doesn't work.", its line for no weak creature;
/// Kamikaze silently, its answer to no target.</para>
///
/// <para>Unchanged: the slot alone (the stock 4.95 client's frame, which takes the faced creature), a creature's own
/// id, and the id of a player on another map, which #343 sends to the faced tile (Caleb: "keeping the worker's
/// fallback for off-map ids"). The stock client sends no id; a Kamikaze weapon proc passes the faced creature's
/// id, which both resolvers read alike.</para>
///
/// <para>Every cast is the real 0x0F frame through <c>Session.Receive</c>, by a fresh caster facing a fresh creature.
/// The arena is a real PvP map with no spawns and no NPCs; the other two maps are content-free, so PvP is off on
/// them. No other class uses any of the three.</para>
/// </summary>
[Collection("world")]
public sealed class DrainKamikazeVictimTests
{
    /// <summary>Savage Arena (Maps.csv MapPvP 1, MapSpells 1).</summary>
    private const ushort Arena = 3018;
    /// <summary>Content-free: no Maps.csv row, so <c>Content.IsPvpMap</c> is false, as it is in every town.</summary>
    private const ushort Town = 62481, Elsewhere = 62482;

    private const ushort CX = 10, CY = 10;      // where every caster stands, facing east at (CX + 1, CY)
    private const ushort NearX = 14;             // a player on the caster's map, not faced
    private const uint NobodyId = 0x7FFF_FFE0;   // an id nobody online has
    private const uint Hp = 5_000, MaxHp = 10_000, Mana = 50_000;
    private const int CreatureHp = 500;          // under Drain's 1000, and under Kamikaze's blast

    /// <summary>What the cast is aimed at: the slot alone or the id of something.</summary>
    public enum Aim { Slot, CreatureId, OffMapPlayerId, NobodyId, OnMapPlayerId, OwnId }

    private readonly SessionFixture _fx;
    private static int _serial;

    public DrainKamikazeVictimTests(SessionFixture fx) => _fx = fx;

    public static TheoryData<string, ushort, Aim> RefusedCases
    {
        get
        {
            var data = new TheoryData<string, ushort, Aim>();
            foreach (var key in new[] { "drain_rogue", "kamikaze" })
                foreach (var map in new[] { Arena, Town })
                    foreach (var aim in new[] { Aim.NobodyId, Aim.OnMapPlayerId, Aim.OwnId })
                        data.Add(key, map, aim);
            return data;
        }
    }

    /// <summary>Aimed at an id that names no creature the blow can reach (nobody, a player on the caster's own map,
    /// the caster), with a weak creature faced. The verb refuses and charges nothing: Drain says "It doesn't work."
    /// and nothing else, Kamikaze says nothing. No pose, no shout, no mana, the caster's HP as it was, the creature
    /// untouched, and the player on the caster's map neither hit nor told anything.
    /// <para>Red on 6be6be3 for all twelve. Drain: "You cast Drain.", 60 mana, the caster +500 off the creature, the
    /// creature alive; at a player, the blow goes to that player (hit in the arena, "You can't attack that target."
    /// in the town). Kamikaze: 120 mana, "Kamikaze~!", the caster on 10 HP, the creature untouched; at a player, the
    /// blast goes to that player in the arena.</para></summary>
    [Theory]
    [MemberData(nameof(RefusedCases))]
    public void AVictimTheBlowCannotStrikeIsNeitherReadNorPaidFor(string key, ushort map, Aim aim)
    {
        var sp = Content.SpellByKey(key)!;
        Assert.Equal(map == Arena, Content.IsPvpMap(map));
        var r = Cast(sp, map, aim);
        string expectLines = key == "drain_rogue" ? "It doesn't work." : "";
        Assert.True(r.Lines == expectLines && r.Spent == 0 && r.HpDelta == 0 && r.Pose == 0 && r.Speech == 0,
                    $"{sp.Name} at {aim} on map {map}: {r}");
        Assert.True(r.CreatureHp == CreatureHp, $"{sp.Name} at {aim} on map {map}: the creature went to {r.CreatureHp}");
        Assert.True(r.NearHp == Hp && r.NearLines == "", $"{sp.Name} at {aim} on map {map}: the near player: {r}");
    }

    public static TheoryData<string, ushort, Aim> LandingCases
    {
        get
        {
            var data = new TheoryData<string, ushort, Aim>();
            foreach (var key in new[] { "drain_rogue", "kamikaze" })
                foreach (var map in new[] { Arena, Town })
                    foreach (var aim in new[] { Aim.Slot, Aim.CreatureId, Aim.OffMapPlayerId })
                        data.Add(key, map, aim);
            return data;
        }
    }

    /// <summary>The control: the slot alone, the creature's own id, and the id of a player on another map (#343's
    /// fallback to the faced tile) still take the faced creature, as they did. Drain kills it, heals the caster by
    /// its 500 and costs 60; Kamikaze shouts, kills it, costs 120 and leaves the caster on 10. "You cast X." and the
    /// pose follow, the player on the caster's map and the player on the other map are untouched.
    /// <para>Green on 6be6be3 as well: the two resolvers agree on these three.</para></summary>
    [Theory]
    [MemberData(nameof(LandingCases))]
    public void TheFacedOrNamedCreatureIsStillTheVictim(string key, ushort map, Aim aim)
    {
        var sp = Content.SpellByKey(key)!;
        var r = Cast(sp, map, aim);
        bool drain = key == "drain_rogue";
        Assert.True(r.CreatureHp <= 0, $"{sp.Name} at {aim} on map {map}: the creature lived ({r})");
        Assert.True(r.Lines.Contains($"You cast {sp.Name}.", StringComparison.Ordinal) && r.Pose == 1
                    && !r.Lines.Contains("You can't attack that target.", StringComparison.Ordinal),
                    $"{sp.Name} at {aim} on map {map}: {r}");
        if (drain) Assert.True(r.Spent == 60 && r.HpDelta == CreatureHp, $"{sp.Name} at {aim} on map {map}: {r}");
        else Assert.True(r.Spent == 120 && r.HpAfter == 10 && r.Speech >= 1, $"{sp.Name} at {aim} on map {map}: {r}");
        Assert.True(r.NearHp == Hp && r.NearLines == "" && r.FarHp == Hp && r.FarLines == "",
                    $"{sp.Name} at {aim} on map {map}: a player was reached ({r})");
    }

    // ===== the cast ======================================================================================

    private sealed record Outcome(string Lines, uint Spent, int HpDelta, uint HpAfter, int Pose, int Speech,
                                  int CreatureHp, uint NearHp, string NearLines, uint FarHp, string FarLines);

    /// <summary>A fresh caster on <paramref name="map"/> faces a fresh 500-HP creature, with a fresh player on the same
    /// map out of reach of the faced tile and another on a content-free map, and casts <paramref name="sp"/> from
    /// slot 0 at <paramref name="aim"/>. Returns what the caster saw, spent and kept, and what the three others
    /// kept.</summary>
    private Outcome Cast(SpellDef sp, ushort map, Aim aim)
    {
        var mob = new Mob(_fx.World.AllocateMobId(), 1, CX + 1, CY, $"Dkv creature {Interlocked.Increment(ref _serial)}", CreatureHp);
        _fx.World.AddMob(map, mob);
        var (near, nearOut, nearc) = Player("DkvNear", map, NearX, CY);
        var (far, farOut, farc) = Player("DkvFar", Elsewhere, CX, CY);
        var (caster, cOut, cc) = Caster(sp, map);
        try
        {
            uint? target = aim switch
            {
                Aim.Slot => null,
                Aim.CreatureId => mob.Id,
                Aim.OffMapPlayerId => far.PlayerId,
                Aim.NobodyId => NobodyId,
                Aim.OnMapPlayerId => near.PlayerId,
                Aim.OwnId => caster.PlayerId,
                _ => throw new ArgumentOutOfRangeException(nameof(aim)),
            };
            uint mp0 = cc.Mp, hp0 = cc.Hp;
            cOut.Clear(); nearOut.Clear(); farOut.Clear();

            caster.Receive(SpellCastSupport.CastFrame(0, target));

            return new Outcome(string.Join(" | ", SpellCastSupport.MiniTexts(cOut)), mp0 - cc.Mp, (int)cc.Hp - (int)hp0,
                               cc.Hp, cOut.BodiesOf(ServerOp.Action).Count, cOut.BodiesOf(ServerOp.Speech).Count,
                               mob.Hp, nearc.Hp, string.Join(" | ", SpellCastSupport.MiniTexts(nearOut)),
                               farc.Hp, string.Join(" | ", SpellCastSupport.MiniTexts(farOut)));
        }
        finally
        {
            _fx.World.LeaveMap(caster, map);
            _fx.World.LeaveMap(near, map);
            _fx.World.LeaveMap(far, Elsewhere);
            _fx.World.DespawnMob(map, mob);
        }
    }

    // ===== helpers =======================================================================================

    /// <summary>A level-99 caster with <paramref name="sp"/> in slot 0, Will 255, half its HP (so Drain's heal shows)
    /// and plenty of mana, facing east.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Caster(SpellDef sp, ushort map) =>
        _fx.PlayerWith($"DkvCaster{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 99;
            ch.MaxHp = MaxHp; ch.Hp = Hp;
            ch.MaxMp = Mana; ch.Mp = Mana;
            ch.Will = 255;
            ch.Dir = 1;
            Wide(ch, map);
            ch.Spells.Add(sp.Id);
        }, map, CX, CY);

    /// <summary>A level-50 player at half HP with nothing running.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Player(string name, ushort map, ushort x, ushort y) =>
        _fx.PlayerWith($"{name}{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 50;
            ch.MaxHp = MaxHp; ch.Hp = Hp;
            ch.MaxMp = 1_000; ch.Mp = 1_000;
            Wide(ch, map);
        }, map, x, y);

    /// <summary>The arena carries its own size; content-free maps have none, and the cast handler needs one.</summary>
    private static void Wide(Character c, ushort map)
    {
        if (Content.Maps.TryGetValue(map, out var m)) { c.MapXs = m.Xs; c.MapYs = m.Ys; }
        else { c.MapXs = 100; c.MapYs = 100; }
    }
}
