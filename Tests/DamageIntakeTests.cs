using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The five ways a player takes damage, pinned one by one — the before/after harness for #28.
///
/// <para>The five intakes (<c>ApplyMobHit</c>, <c>ReceiveSpellDamage</c>, <c>ReceiveMeleeDamage</c>,
/// <c>ReceiveMobSpell</c>, <c>ReceiveEnvironmentDamage</c>) run the same sequence — immunity, wake,
/// amplifier, armor, deduction, HP, durability, stats, the over-head bar, the chat line, death — with five
/// different subsets and, in one place, a different ORDER. Nothing tested any of it, so "the refactor
/// changed nothing" was unfalsifiable. Every number here was read off the pre-refactor code and confirmed
/// against it before the pipeline existed.</para>
///
/// <para><b>The numbers are chosen to discriminate, not to be round.</b> AC -50 halves damage and the sleep
/// amplifier multiplies by 1.5; with a raw 101, rounding after each step gave 75 one way round and 76 the
/// other. Since #78 every term is one product floored once (75.75, so 75 both ways), and the damage-math facts
/// use figures where that single floor and the old step rounding part, so a return to either is caught.</para>
///
/// <para>Determinism: a naked character rolls no durability (the loop has no slots to roll), and
/// <c>Combat.RollCritChance</c> only ever reaches the wire-visual crit byte — RTK never multiplies a mob's
/// swing by it — so every damage figure here is exact, not distributional. The one test that does want
/// durability drives it with 60 hits, where "no slot ever lost a point" is a 1-in-2^60 event.</para>
/// </summary>
[Collection("world")]
public class DamageIntakeTests
{
    private readonly SessionFixture _fx;

    public DamageIntakeTests(SessionFixture fx) => _fx = fx;

    /// <summary>Odd on purpose: 101/2 = 50.5 keeps a half for the single floor (#78) to act on.</summary>
    private const int Raw = 101;

    private const uint FullHp = 100_000;

    /// <summary>A live, socket-free player with room to be hit and an AC worth netting. Facing north (dir 0)
    /// so <see cref="Attacker"/> can put a mob behind them on demand.</summary>
    private (Session session, RecordingOutbound outbound, Character character) Victim(
        string name, sbyte ac = -50, uint hp = FullHp) =>
        _fx.PlayerWith(name, c => { c.MaxHp = FullHp; c.Hp = hp; c.Ac = ac; c.Dir = 0; c.Level = 1; });

    /// <summary>A creature standing on the victim's own tile. Facing south by default, which is NOT the
    /// victim's facing, so <c>Combat.IsBehindTarget</c> is false and the rear x2 stays out of the way.</summary>
    private static Mob Attacker(byte dir = 2, ushort x = 5, ushort y = 10) =>
        new() { Id = 9001, Name = "Cave bat", Key = "no_such_mob", Level = 1, Hit = 0, X = x, Y = y, Dir = dir };

    /// <summary>Every 0x0A mini-text line the session sent, decoded (body = type | u16 BE length | ASCII).</summary>
    private static List<string> MiniTexts(RecordingOutbound outbound)
    {
        var lines = new List<string>();
        foreach (var body in outbound.BodiesOf(0x0A))
        {
            int len = (body[1] << 8) | body[2];
            lines.Add(System.Text.Encoding.ASCII.GetString(body, 3, len));
        }
        return lines;
    }

    // ---- the damage math, one intake at a time ------------------------------------------------------------
    //
    // #78 (Caleb, 2026-10-21: "multiply everything, round down once, minimum 1"). Every term multiplies the
    // unrounded figure and the product is floored once and raised to 1 (Combat.FloorOnce); a hit from behind is
    // then doubled ("Round down once (minimum 1), then double for a hit from behind"). Each step used to
    // round on its own: the armor floor, then Math.Round (half to even) after the amplifier and again after the
    // deduction, and the two armored kinds ran armor and the amplifier in opposite orders. The facts below use
    // figures where the two rules part, and each says what 6de1d24 (step rounding) gave.

    /// <summary>A creature's swing: armor and the sleep amplifier multiply and are floored once.
    /// 103 x 0.5 x 1.5 = 77.25, so 77. Step rounding netted armor first, floor(51.5) = 51, then rounded
    /// 51 x 1.5 = 76.5 to the even 76.</summary>
    [Fact]
    public void MobMeleeMultipliesArmorAndTheSleepAmplifierThenFloorsOnce()
    {
        var (session, _, character) = Victim("DmgMobMelee");
        session.ArmDamageAmp(1.5, 60_000);

        session.ApplyMobHit(Attacker(), 103);

        Assert.Equal(FullHp - 77, character.Hp);
    }

    /// <summary>A player's spell: the same product, 101 x 1.5 x 0.5 = 75.75, so 75. Step rounding amplified
    /// first and rounded, 151.5 to the even 152, then netted armor, floor(76) = 76.</summary>
    [Fact]
    public void PlayerSpellMultipliesTheAmplifierAndArmorThenFloorsOnce()
    {
        var (victim, _, character) = Victim("DmgSpellVictim");
        var (caster, _, _) = Victim("DmgSpellCaster");
        victim.ArmDamageAmp(1.5, 60_000);

        int applied = victim.ReceiveSpellDamage(Raw, caster, "Spark");

        Assert.Equal(75, applied);
        Assert.Equal(FullHp - 75, character.Hp);
    }

    /// <summary>The order no longer matters. The two armored kinds used to run armor and the amplifier in
    /// opposite orders, which is why a raw 101 at AC -50 under a 1.5x amplifier landed 75 from a creature and
    /// 76 from a player. One product, one floor: both land 75.</summary>
    [Fact]
    public void TheTwoArmoredKindsLandTheSameFigureWhicheverWayTheyWereOrdered()
    {
        var (mobVictim, _, mobChar) = Victim("DmgOrderMob");
        mobVictim.ArmDamageAmp(1.5, 60_000);
        mobVictim.ApplyMobHit(Attacker(), Raw);

        var (spellVictim, _, spellChar) = Victim("DmgOrderSpell");
        spellVictim.ArmDamageAmp(1.5, 60_000);
        spellVictim.ReceiveSpellDamage(Raw, Victim("DmgOrderCaster").session, "Spark");

        Assert.Equal(new[] { 75u, 75u }, new[] { FullHp - mobChar.Hp, FullHp - spellChar.Hp });
    }

    /// <summary>PvP melee arrives POST-armor (the attacker side already netted it via SwingTarget.Of), so the
    /// intake must not net it a second time: 101 x 1.5 = 151.5, floored to 151 and nothing else. Step rounding
    /// took it to the even 152.</summary>
    [Fact]
    public void PlayerMeleeAppliesTheAmplifierAndNoArmor()
    {
        var (victim, _, character) = Victim("DmgPvpMeleeVictim");
        var (attacker, _, _) = Victim("DmgPvpMeleeAttacker");
        victim.ArmDamageAmp(1.5, 60_000);

        victim.ReceiveMeleeDamage(Raw, attacker, crit: false);

        Assert.Equal(FullHp - 151, character.Hp);
    }

    /// <summary>A creature's spell skips physical AC exactly as a player's melee intake does — 101 lands as
    /// 101 against AC -50 — and names the caster in the RTK peck.lua wording.</summary>
    [Fact]
    public void MobSpellSkipsArmorAndNamesTheCaster()
    {
        var (victim, outbound, character) = Victim("DmgMobSpell");

        victim.ReceiveMobSpell(Raw, Attacker(), "Ice ray");

        Assert.Equal(FullHp - Raw, character.Hp);
        Assert.Contains("Cave bat attacks you with Ice ray spell.", MiniTexts(outbound));
    }

    /// <summary>Room damage is the mob-spell twin with the line supplied whole by the caller. Sute's cold
    /// tile is the only source today, and its 257 lands unnetted against AC -50.</summary>
    [Fact]
    public void EnvironmentDamageSkipsArmorAndUsesTheCallerLine()
    {
        var (victim, outbound, character) = Victim("DmgFrigid");

        victim.TakeFrigidBlast();

        Assert.Equal(FullHp - SuteAi.FrigidDamage, character.Hp);
        Assert.Contains(SuteAi.FrigidText, MiniTexts(outbound));
    }

    /// <summary>The positional rear x2 belongs to the mob swing alone, and doubles the figure AFTER the one floor,
    /// as RTK's swingDamage.lua does (:147-150). Caleb, 2026-10-21: "Round down once (minimum 1), then double for a
    /// hit from behind. Ordinary hits from behind stay exactly as today." Same facing, attacker to the south of a
    /// north-facing victim: floor(101 x 0.5) = 50, doubled to 100, as on 6de1d24. Under a 1.5x amplifier,
    /// floor(101 x 0.5 x 1.5) = 75, doubled to 150 (6de1d24 also 150, by its own rounding).
    /// <para>Red on d5edcf8, which put the double inside the floor: 101 and 151.</para></summary>
    [Fact]
    public void MobMeleeDoublesFromBehindAfterTheOneFloor()
    {
        var (plain, _, plainChar) = Victim("DmgRear");
        plain.ApplyMobHit(Attacker(dir: 0, x: 5, y: 11), Raw);
        Assert.Equal(FullHp - 100, plainChar.Hp);

        var (slept, _, sleptChar) = Victim("DmgRearSlept");
        slept.ArmDamageAmp(1.5, 60_000);
        slept.ApplyMobHit(Attacker(dir: 0, x: 5, y: 11), Raw);
        Assert.Equal(FullHp - 150, sleptChar.Hp);
    }

    /// <summary>The sanctuary/Cunning deduction multiplies into the same product on every intake. The decision
    /// sheet's example: a creature swing of 300 on a slept (1.5x) player at AC -22 under Sanctuary,
    /// 300 x 0.78 x 1.5 x 0.5 = 175.5, so 175; step rounding gave floor(234) x 1.5 = 351, then 175.5 rounded to
    /// the even 176. On a creature's spell there is no armor: 103 x 0.5 = 51.5, so 51, where step rounding
    /// took it to the even 52.</summary>
    [Fact]
    public void TheDeductionMultipliesIntoTheOneFloorOnBothKinds()
    {
        var (melee, _, meleeChar) = Victim("DmgDeductMelee", ac: -22);
        melee.ApplySanctuaryDeduction(0.5, 60_000, "Sanctuary");
        melee.ArmDamageAmp(1.5, 60_000);
        melee.ApplyMobHit(Attacker(), 300);
        Assert.Equal(FullHp - 175, meleeChar.Hp);

        var (spell, _, spellChar) = Victim("DmgDeductSpell");
        spell.ApplySanctuaryDeduction(0.5, 60_000, "Sanctuary");
        spell.ReceiveMobSpell(103, Attacker(), "Ice ray");
        Assert.Equal(FullHp - 51, spellChar.Hp);
    }

    /// <summary>A hit that lands takes at least 1, on every intake, however armored and protected the player.
    /// The decision sheet's edge: a creature's raw 9 on a player at AC -80 under Sanctuary is 9 x 0.2 x 0.5 =
    /// 0.9, raised to 1. Step rounding netted armor to floor(1.8) = 1 and then rounded 1 x 0.5 to 0, so the
    /// hit landed (the bar, the sound, the durability rolls) and took nothing. The same on a creature's spell
    /// under high Cunning (0.15): 1 x 0.15 rounded to 0, and is 1 now.</summary>
    [Fact]
    public void AWeakHitOnAnArmouredSanctuariedPlayerTakesOneNotZero()
    {
        var (melee, _, meleeChar) = Victim("DmgWeakMelee", ac: -80);
        melee.ApplySanctuaryDeduction(0.5, 60_000, "Sanctuary");
        melee.ApplyMobHit(Attacker(), 9);
        Assert.Equal(FullHp - 1, meleeChar.Hp);

        var (spell, _, spellChar) = Victim("DmgWeakSpell");
        spell.ApplySanctuaryDeduction(0.15, 60_000, "Baekho's cunning");
        spell.ReceiveMobSpell(1, Attacker(), "Ice ray");
        Assert.Equal(FullHp - 1, spellChar.Hp);
    }

    // ---- Harden Body: one gate, five answers, all of them sourced -------------------------------------------

    /// <summary>Every <see cref="DamageKind"/>, driven through its real production entry point, against a
    /// live Harden Body ward. Four are blocked; room damage is the one exception and carries a citation
    /// (RTK's stepped-on traps take health off with the plain <c>removeHealth</c>, which has no ward check —
    /// see <c>DamageIntake.IgnoresHardenBody</c>).
    ///
    /// <para>The theory data is <c>Enum.GetValues</c> and the switch has no default case that passes, so a
    /// sixth damage source cannot be added without landing here and stating its answer. That is the whole
    /// point: the two sites that skipped the check skipped it by saying nothing.</para></summary>
    [Theory]
    [MemberData(nameof(EveryDamageKind))]
    public void EveryDamageKindHonoursHardenBodyExceptTheSourcedRoomCase(DamageKind kind)
    {
        var (victim, _, character) = Victim($"DmgWard{kind}");
        victim.ItemSetStatus("harden_body", 60_000);

        switch (kind)
        {
            case DamageKind.MobMelee:
                victim.ApplyMobHit(Attacker(), Raw);
                break;
            case DamageKind.PlayerSpell:
                // The 0 return is part of the contract: a blocked hit yields no overkill to splash.
                Assert.Equal(0, victim.ReceiveSpellDamage(Raw, Victim($"DmgWard{kind}Peer").session, "Spark"));
                break;
            case DamageKind.PlayerMelee:
                victim.ReceiveMeleeDamage(Raw, Victim($"DmgWard{kind}Peer").session, crit: false);
                break;
            case DamageKind.MobSpell:
                victim.ReceiveMobSpell(Raw, Attacker(), "Ice ray");
                break;
            case DamageKind.Environment:
                victim.TakeFrigidBlast();
                break;
            default:
                throw new NotSupportedException(
                    $"DamageKind.{kind} is new and has no Harden Body case here. Say what the ward does to " +
                    "it and cite RTK for the answer — see DamageIntake.IgnoresHardenBody.");
        }

        if (kind == DamageKind.Environment)
            Assert.True(character.Hp < FullHp,
                        "Room damage is the one sourced exception. If it is now blocked, the flag moved " +
                        "without the citation on DamageIntake.IgnoresHardenBody moving with it.");
        else
            Assert.Equal(FullHp, character.Hp);
    }

    public static TheoryData<DamageKind> EveryDamageKind()
    {
        var kinds = new TheoryData<DamageKind>();
        foreach (var kind in Enum.GetValues<DamageKind>()) kinds.Add(kind);
        return kinds;
    }

    /// <summary>An intake on a player who is already down does nothing at all, on every path — that gate is
    /// what keeps Die() from firing twice while the Silver Thread revive is still pending.</summary>
    [Fact]
    public void NothingLandsOnAPlayerWhoIsAlreadyDead()
    {
        var (victim, _, character) = Victim("DmgAlreadyDead", hp: 0);
        var (peer, _, _) = Victim("DmgAlreadyDeadPeer");

        victim.ApplyMobHit(Attacker(), Raw);
        Assert.Equal(0, victim.ReceiveSpellDamage(Raw, peer, "Spark"));
        victim.ReceiveMeleeDamage(Raw, peer, crit: false);
        victim.ReceiveMobSpell(Raw, Attacker(), "Ice ray");
        victim.TakeFrigidBlast();

        Assert.Equal(0u, character.Hp);
    }

    // ---- durability, death, and the spell intake's return value -------------------------------------------

    /// <summary>Per-hit durability decay belongs to the two MELEE intakes (RTK clif_deductarmor rolls every
    /// worn slot on a blow) and to none of the three magic/room ones. 60 swings against a 49%-per-slot roll
    /// make "lost nothing" impossible in practice; the negative half is exact.</summary>
    [Fact]
    public void OnlyTheMeleeIntakesRollDurability()
    {
        var def = Content.Items.First(i => i.Durability >= 200 && !i.Indestructible);

        var (swung, _, swungChar) = _fx.PlayerWith("DmgDuraMelee", c =>
        {
            c.MaxHp = FullHp; c.Hp = FullHp; c.Ac = 0; c.Level = 1;
            c.Equipment.Add(new InvItem { Slot = 0, ItemId = def.Id, Dura = def.Durability });
        });
        for (int i = 0; i < 60; i++) swung.ApplyMobHit(Attacker(), 1);
        Assert.True(swungChar.Equipment[0].Dura < def.Durability,
                    "60 mob swings rolled no durability loss at all — DeductDura is not being reached");

        var (zapped, _, zappedChar) = _fx.PlayerWith("DmgDuraSpell", c =>
        {
            c.MaxHp = FullHp; c.Hp = FullHp; c.Ac = 0; c.Level = 1;
            c.Equipment.Add(new InvItem { Slot = 0, ItemId = def.Id, Dura = def.Durability });
        });
        for (int i = 0; i < 60; i++) zapped.ReceiveMobSpell(1, Attacker(), "Ice ray");
        Assert.Equal(def.Durability, zappedChar.Equipment[0].Dura);
    }

    // ---- caller-level: a named NPC's spell (#79) ----------------------------------------------------------

    /// <summary>A mythic's Stormstrike, driven through its real entry point, against an UNWARDED player.
    /// This pins the damage figure so the #79 fix — which moves the intake this spell lands on — can be shown
    /// not to have moved the number.
    ///
    /// <para>Level 50, Will 20: <c>125 + 50x4 + ceil((21/2) x 3.5)</c> = 125 + 200 + 37 = 362. AC -50 is set
    /// deliberately and must NOT halve it: neither the room intake this used to take nor the creature-spell
    /// intake it takes now applies physical AC, so a change in that figure means the terms moved, not just
    /// the ward.</para></summary>
    [Fact]
    public void AMythicsStormstrikeLandsItsFullFigureOnAnUnwardedPlayer()
    {
        var (victim, outbound, character) = _fx.PlayerWith("DmgStormUnwarded", c =>
        {
            c.MaxHp = FullHp; c.Hp = FullHp; c.Ac = -50; c.Level = 50; c.Will = 20;
        });

        victim.NpcCastStormstrike("Mythic Tiger");

        Assert.Equal(FullHp - 362, character.Hp);
        Assert.Contains("Mythic Tiger attacks you with Stormstrike spell.", MiniTexts(outbound));
    }

    /// <summary>The same spell against a WARDED player: nothing lands (#79). RTK's own
    /// Spells/NPCs/stormstrike.lua:9 goes through <c>removeHealthExtend</c>, which returns before the damage
    /// calc while a Harden Body ward is up, so this is the sourced behaviour and not a nicety.
    ///
    /// <para><b>This is the assertion the enum Theory above cannot make.</b> That Theory proves each
    /// <see cref="DamageKind"/> honours the ward as its row says; it is silent on whether a CALLER reached
    /// for the right kind. Stormstrike routed itself to <see cref="DamageKind.Environment"/> — which is
    /// correctly, deliberately exempt — and the Theory was perfectly happy the whole time. Only a test that
    /// drives the caller can see it, which is why this one exists.</para>
    ///
    /// <para>The banish is deliberately not asserted: <c>ReturnToInn</c> picks a random tile in a random inn
    /// of the player's home group, which is not a stable thing to pin. It runs either way by design — RTK
    /// treats the zap and the walk home as two beats, and warding the first does not cancel the second.</para></summary>
    [Fact]
    public void AWardedPlayerTakesNothingFromAMythicsStormstrike()
    {
        var (victim, outbound, character) = _fx.PlayerWith("DmgStormWarded", c =>
        {
            c.MaxHp = FullHp; c.Hp = FullHp; c.Ac = -50; c.Level = 50; c.Will = 20;
        });
        victim.ItemSetStatus("harden_body", 60_000);

        victim.NpcCastStormstrike("Mythic Tiger");

        Assert.Equal(FullHp, character.Hp);
        Assert.DoesNotContain("Mythic Tiger attacks you with Stormstrike spell.", MiniTexts(outbound));
    }

    /// <summary>A lethal spell hit reports the FULL figure, uncapped by the HP that was left (the vita
    /// strikes' overflow is computed from the excess), drops the player to 0, and runs the death beat.</summary>
    [Fact]
    public void ALethalSpellHitReportsOverkillAndKills()
    {
        var (victim, outbound, character) = Victim("DmgLethal", ac: 0, hp: 10);
        var (caster, _, _) = Victim("DmgLethalCaster");

        int applied = victim.ReceiveSpellDamage(Raw, caster, "Spark");

        Assert.Equal(Raw, applied);        // 101 applied against 10 HP — overkill is `applied - hpBefore`
        Assert.Equal(0u, character.Hp);
        Assert.Contains(MiniTexts(outbound), line => line.StartsWith("You have been defeated!"));
    }
}
