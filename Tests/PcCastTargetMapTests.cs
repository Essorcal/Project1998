using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// A player-target cast aimed by id reaches only a player on the caster's own map. Caleb, 2026-10-11 ("Fix as a
/// slice"), on PR #340's review (F2): <c>Session.ResolvePcCastTarget</c> took an explicit target id to the player
/// with that id on ANY map, so a frame carrying the id of a player on another map Dispelled them.
///
/// <para>The resolver serves every verb that calls <c>ctx:pcTarget()</c> in game-data/spell_verbs.lua: the
/// Dispell family (<c>cleanse</c>), Mana Steal (<c>mana_steal</c>), Mana Gift (<c>mana_gift</c>), the Resurrect
/// family (<c>revive</c>) and Divination (<c>divine</c>), 24 spells. A player on another map is now answered as an
/// id that names nobody: the faced tile, and with nobody there, nothing. Each verb's own answer to nothing is
/// unchanged: it returns false before it spends or says anything, so the cast is silent and free, with no "You cast
/// X." and no pose (<c>Session.LogNoTarget</c>). The refusals each verb makes before it resolves the target (too
/// little mana, Revive's cooldown) come first, as before.</para>
///
/// <para>Only a client that sends a target id reaches this. The stock 4.95 client casts with the slot alone
/// (docs/4.x/Protocol.md, "Live-confirmed"), and the slot path is the faced tile, which this does not
/// touch.</para>
///
/// <para>Every cast is the real 0x0F frame through <c>Session.Receive</c>. A caster casts at most three frames, so
/// the packet handler's action budget never decides a fact. A Dispell is won where a fact needs one: Will 255
/// against the default AC (99, clamped to 70) and Will 3 rolls against 108. The players stand on content-free maps
/// 62710-62713, which no other class uses and <c>game-data/Maps.csv</c> has no row for.</para>
/// </summary>
[Collection("world")]
public sealed class PcCastTargetMapTests
{
    private const ushort HereMap = 62710, ThereMap = 62711, FacedHereMap = 62712, FacedThereMap = 62713;

    private const uint Mana = 10_000;
    private const string BuffKey = "pc_target_map_might";

    private readonly SessionFixture _fx;
    private static int _serial;

    public PcCastTargetMapTests(SessionFixture fx) => _fx = fx;

    /// <summary>Every spell whose verb resolves its player target through <c>ctx:pcTarget()</c>, by verb.</summary>
    public static TheoryData<string, string> ResolverSpells
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var key in new[] { "dispell_poet", "remove_magic_poet", "return_natural_poet", "restore_balance_poet" })
                data.Add("cleanse", key);
            foreach (var key in new[] { "inspiration_poet", "draw_energy_poet", "harness_power_poet", "combine_focus_poet" })
                data.Add("mana_steal", key);
            foreach (var key in new[] { "inspire_poet", "share_energy_poet", "bestow_power_poet", "release_focus_poet" })
                data.Add("mana_gift", key);
            foreach (var key in new[] { "resurrect_poet", "return_spirit_poet", "mingken_blessing_poet", "death_undone_poet" })
                data.Add("revive", key);
            foreach (var key in new[] { "judge_rogue", "spiritual_advisor_rogue", "natural_talent_rogue", "appraise_rogue",
                                        "spy_rogue", "spiritual_guide_rogue", "natures_handiwork_rogue", "judgement_day_rogue" })
                data.Add("divine", key);
            return data;
        }
    }

    /// <summary>Each of the 24, cast through the real frame at the id of a player on another map, with nobody on the
    /// caster's faced tile: the caster reads nothing, shows no cast pose, gets no popup and keeps their mana and
    /// cooldown; the player on the other map is told nothing and keeps everything the verb would have changed (a
    /// Dispell: the buff; Mana Steal: their mana; Mana Gift: their empty pool; Revive: their ghost; Divination: their
    /// stats unread). Then the control: the same caster, the same cast, at the id of the same kind of player standing
    /// on the caster's own map, lands as it always did.
    /// <para>Red on db2a48d for all 24: the cast lands on the other map. A Dispell strips the buff for 200, Mana
    /// Steal drains the far player, Mana Gift fills them, Revive raises them for 3000, and Divination shows the
    /// caster their stats for 30.</para></summary>
    [Theory]
    [MemberData(nameof(ResolverSpells))]
    public void ACastAimedByIdAtAPlayerOnAnotherMapIsRefusedAsAMissingTarget(string verb, string key)
    {
        var sp = Content.SpellByKey(key)!;
        var (far, farOut, farc) = Target(verb, ThereMap, 20, 20);
        var (near, nearOut, nearc) = Target(verb, HereMap, 30, 20);
        // Facing north (Dir 0) from (20, 20): the faced tile (20, 19) is empty, on both maps.
        var (caster, cOut, cc) = Caster(verb, HereMap, 20, 20, sp);
        try
        {
            Assert.NotEqual(caster.CharMap, far.CharMap);
            if (verb == "mana_steal")
            {
                SessionFixture.FormParty(caster, far);
                SessionFixture.FormParty(caster, near);
                Assert.True(caster.WithState(() => caster.LuaResolvePcTarget(sp, near.PlayerId) && caster.LuaTargetInGroup),
                            "the caster and the near player are not grouped");
            }
            uint casterMp = cc.Mp, farMp = farc.Mp;
            cOut.Clear();
            farOut.Clear();

            caster.Receive(SpellCastSupport.CastFrame(0, far.PlayerId));

            Assert.Empty(SpellCastSupport.MiniTexts(cOut));
            Assert.Empty(cOut.BodiesOf(ServerOp.NpcDialog));
            Assert.Empty(cOut.BodiesOf(ServerOp.Action));
            Assert.Equal(casterMp, cc.Mp);
            Assert.Equal(0L, SpellCastSupport.AetherLeft(caster, sp.Key));
            Assert.Empty(SpellCastSupport.MiniTexts(farOut));
            Assert.Equal(farMp, farc.Mp);
            switch (verb)
            {
                case "cleanse": Assert.NotNull(SpellCastSupport.BuffEntry(far, BuffKey)); break;
                case "revive": Assert.True(far.IsDead, "the player on the other map was revived"); break;
            }

            // The control: the same cast at a player on the caster's own map, by id.
            uint nearMp = nearc.Mp;
            cOut.Clear();
            nearOut.Clear();

            caster.Receive(SpellCastSupport.CastFrame(0, near.PlayerId));

            var cLines = SpellCastSupport.MiniTexts(cOut);
            var nLines = SpellCastSupport.MiniTexts(nearOut);
            switch (verb)
            {
                case "cleanse":
                    Assert.Null(SpellCastSupport.BuffEntry(near, BuffKey));
                    Assert.Equal(casterMp - 200, cc.Mp);
                    Assert.Equal(new[] { $"You cast {sp.Name}." }, cLines);
                    Assert.Contains($"{caster.CharName} casts {sp.Name} on you.", nLines);
                    break;
                case "mana_steal":
                    Assert.Equal(0u, nearc.Mp);
                    Assert.Equal(casterMp + nearMp, cc.Mp);
                    Assert.Equal(new[] { $"You cast {sp.Name}." }, cLines);
                    break;
                case "mana_gift":
                    Assert.Equal(nearc.MaxMp, nearc.Mp);
                    Assert.Equal(casterMp - nearc.MaxMp, cc.Mp);
                    Assert.Equal(new[] { $"You cast {sp.Name}." }, cLines);
                    break;
                case "revive":
                    Assert.False(near.IsDead, "the player on the caster's own map was not revived");
                    Assert.Equal(casterMp - 3000, cc.Mp);
                    Assert.Equal(new[] { $"You cast {sp.Name}." }, cLines);
                    Assert.Contains($"{caster.CharName} cast {sp.Name} on you.", nLines);
                    break;
                case "divine":
                    Assert.Equal(casterMp - 30, cc.Mp);
                    Assert.Empty(cLines);
                    Assert.Contains(cOut.BodiesOf(ServerOp.NpcDialog),
                                    b => Encoding.ASCII.GetString(b).Contains(near.CharName, StringComparison.Ordinal));
                    break;
                default:
                    Assert.Fail($"no control for verb '{verb}'");
                    break;
            }
        }
        finally
        {
            _fx.World.LeaveMap(caster, HereMap);
            _fx.World.LeaveMap(near, HereMap);
            _fx.World.LeaveMap(far, ThereMap);
        }
    }

    /// <summary>A player on another map is answered exactly as an id that names nobody: by the faced tile. A
    /// Dispeller faces a buffed player on their own map, and casts three times: with the slot alone (the stock 4.95
    /// client's frame), with an id nobody online has, and with the id of a buffed player on another map. Each cast
    /// lands on the faced player, for 200, and the player on the other map keeps their buff.
    /// <para>The slot-alone and unknown-id casts are green before and after: the slot path is unchanged. The last
    /// is red on db2a48d: the player on the other map is Dispelled and the faced one keeps the buff.</para></summary>
    [Fact]
    public void AnIdOnAnotherMapIsAnsweredLikeAnIdNobodyHasByTheFacedTile()
    {
        var sp = Content.SpellByKey("dispell_poet")!;
        var (faced, facedOut, _) = Target("cleanse", FacedHereMap, 41, 20);
        var (far, farOut, _) = Target("cleanse", FacedThereMap, 41, 20);
        var (caster, cOut, cc) = Caster("cleanse", FacedHereMap, 40, 20, sp, dir: 1);   // facing east, at (41, 20)
        try
        {
            var frames = new[]
            {
                ("the slot alone", SpellCastSupport.CastFrame(0)),
                ("an id nobody has", SpellCastSupport.CastFrame(0, 0x7FFF_FFF0)),
                ("the id of a player on another map", SpellCastSupport.CastFrame(0, far.PlayerId)),
            };
            foreach (var (what, frame) in frames)
            {
                if (SpellCastSupport.BuffEntry(faced, BuffKey) is null) GiveBuff(faced);
                uint before = cc.Mp;
                cOut.Clear();
                facedOut.Clear();
                farOut.Clear();

                caster.Receive(frame);

                Assert.True(SpellCastSupport.BuffEntry(far, BuffKey) is not null,
                            $"cast with {what}: the player on the other map lost their buff");
                Assert.Empty(SpellCastSupport.MiniTexts(farOut));
                Assert.True(SpellCastSupport.BuffEntry(faced, BuffKey) is null,
                            $"cast with {what}: the faced player kept their buff");
                Assert.Equal(before - 200, cc.Mp);
                Assert.Equal(new[] { $"You cast {sp.Name}." }, SpellCastSupport.MiniTexts(cOut));
                Assert.Contains($"{caster.CharName} casts {sp.Name} on you.", SpellCastSupport.MiniTexts(facedOut));
            }
        }
        finally
        {
            _fx.World.LeaveMap(caster, FacedHereMap);
            _fx.World.LeaveMap(faced, FacedHereMap);
            _fx.World.LeaveMap(far, FacedThereMap);
        }
    }

    // ===== helpers ====================================================================================

    /// <summary>A level-99 caster with <paramref name="sp"/> in slot 0, Will 255, on a 100x100 walkable map, facing
    /// <paramref name="dir"/> (0 north, 1 east). Mana Steal's caster starts low, so what it takes shows.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Caster(string verb, ushort map, ushort x, ushort y,
                                                                            SpellDef sp, byte dir = 0) =>
        _fx.PlayerWith($"PcMapCaster{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 99;
            ch.MaxHp = 1_000; ch.Hp = 1_000;
            ch.MaxMp = Mana; ch.Mp = verb == "mana_steal" ? 100u : Mana;
            ch.Will = 255;
            ch.Dir = dir;
            Wide(ch, map);
            ch.Spells.Add(sp.Id);
        }, map, x, y);

    /// <summary>A level-50 player shaped for <paramref name="verb"/>: buffed for a Dispell, holding 500 mana and
    /// willing to group for Mana Steal, with an empty pool of 1,000 for Mana Gift, dead for Revive, and as is for
    /// Divination.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Target(string verb, ushort map, ushort x, ushort y)
    {
        var made = _fx.PlayerWith($"PcMapTarget{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 50;
            ch.MaxHp = 1_000; ch.Hp = verb == "revive" ? 0u : 1_000u;
            ch.MaxMp = 1_000; ch.Mp = verb switch { "mana_steal" => 500u, "mana_gift" => 0u, _ => 1_000u };
            ch.Grouped = verb == "mana_steal";
            Wide(ch, map);
        }, map, x, y);
        if (verb == "cleanse") GiveBuff(made.session);
        return made;
    }

    private static void GiveBuff(Session s) => s.ReceiveCurse("might", 5, 600_000, BuffKey, "Probe Might", "");

    /// <summary>Content-free maps have no size of their own; the cast handler needs one.</summary>
    private static void Wide(Character c, ushort map)
    {
        if (Content.Maps.TryGetValue(map, out var m)) { c.MapXs = m.Xs; c.MapYs = m.Ys; }
        else { c.MapXs = 100; c.MapYs = 100; }
    }
}
