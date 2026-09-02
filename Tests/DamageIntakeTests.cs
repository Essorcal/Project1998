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
/// amplifier multiplies by 1.5; with a raw 101 those two terms give 75 one way round and 76 the other, so
/// the tests below actually catch a re-ordering of armor and amplifier instead of quietly agreeing with it.
/// (100 would not: floor(100/2)x1.5 and floor(100x1.5/2) are both 75.)</para>
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

    /// <summary>Odd on purpose: floor(101/2) = 50 keeps the half that makes the ordering visible.</summary>
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

    /// <summary>A creature's swing nets ARMOR FIRST and only then applies the sleep amplifier — RTK
    /// swingDamage.lua's own order. floor(101 x 0.5) = 50, then x1.5 = 75.</summary>
    [Fact]
    public void MobMeleeNetsArmorBeforeTheSleepAmplifier()
    {
        var (session, _, character) = Victim("DmgMobMelee");
        session.ArmDamageAmp(1.5, 60_000);

        session.ApplyMobHit(Attacker(), Raw);

        Assert.Equal(FullHp - 75, character.Hp);
    }

    /// <summary>A player's spell amplifies FIRST and nets armor after — the opposite order, and the reason
    /// the pipeline cannot simply pick one. 101 x 1.5 = 152 (152 is even, so the .5 rounds to it), then
    /// floor(152 x 0.5) = 76.</summary>
    [Fact]
    public void PlayerSpellAmplifiesBeforeNettingArmor()
    {
        var (victim, _, character) = Victim("DmgSpellVictim");
        var (caster, _, _) = Victim("DmgSpellCaster");
        victim.ArmDamageAmp(1.5, 60_000);

        int applied = victim.ReceiveSpellDamage(Raw, caster, "Spark");

        Assert.Equal(76, applied);
        Assert.Equal(FullHp - 76, character.Hp);
    }

    /// <summary>PvP melee arrives POST-armor (the attacker side already netted it via SwingTarget.Of), so the
    /// intake must not net it a second time: 101 x 1.5 = 152 and nothing else.</summary>
    [Fact]
    public void PlayerMeleeAppliesTheAmplifierAndNoArmor()
    {
        var (victim, _, character) = Victim("DmgPvpMeleeVictim");
        var (attacker, _, _) = Victim("DmgPvpMeleeAttacker");
        victim.ArmDamageAmp(1.5, 60_000);

        victim.ReceiveMeleeDamage(Raw, attacker, crit: false);

        Assert.Equal(FullHp - 152, character.Hp);
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

    /// <summary>The positional rear x2 lands AFTER armor and belongs to the mob swing alone. Same facing,
    /// attacker to the south of a north-facing victim: floor(100 x 0.5) = 50, doubled to 100.</summary>
    [Fact]
    public void MobMeleeDoublesFromBehindAfterArmor()
    {
        var (victim, _, character) = Victim("DmgRear");

        victim.ApplyMobHit(Attacker(dir: 0, x: 5, y: 11), 100);

        Assert.Equal(FullHp - 100, character.Hp);
    }

    /// <summary>The sanctuary/Cunning deduction is the LAST term on every intake that has one. On the mob
    /// swing it lands on the already-armored figure (50 -> 25); on a mob spell there is no armor to precede
    /// it, so it halves the raw (101 -> 50, banker's rounding taking 50.5 down to the even 50).</summary>
    [Fact]
    public void DeductionAppliesLastOnBothOrderings()
    {
        var (melee, _, meleeChar) = Victim("DmgDeductMelee");
        melee.ApplySanctuaryDeduction(0.5, 60_000, "Sanctuary");
        melee.ApplyMobHit(Attacker(), Raw);
        Assert.Equal(FullHp - 25, meleeChar.Hp);

        var (spell, _, spellChar) = Victim("DmgDeductSpell");
        spell.ApplySanctuaryDeduction(0.5, 60_000, "Sanctuary");
        spell.ReceiveMobSpell(Raw, Attacker(), "Ice ray");
        Assert.Equal(FullHp - 50, spellChar.Hp);
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
