using Shared;

namespace Server;

/// <summary>Which of the five ways a player can take damage this blow is. The enum exists so the shape of an
/// intake is a property of its SOURCE rather than a bag of booleans re-chosen at each call site, and so a
/// test can enumerate the sources and assert something of every one of them (#28).</summary>
public enum DamageKind
{
    /// <summary>A creature's landed swing — <c>Session.ApplyMobHit</c>, driven by World.Tick.</summary>
    MobMelee,
    /// <summary>Another player's spell, or a self-cast — <c>Session.ReceiveSpellDamage</c>.</summary>
    PlayerSpell,
    /// <summary>Another player's weapon swing (PvP) — <c>Session.ReceiveMeleeDamage</c>.</summary>
    PlayerMelee,
    /// <summary>A creature's spell — <c>Session.ReceiveMobSpell</c>.</summary>
    MobSpell,
    /// <summary>The room itself: Sute's cold tiles — <c>Session.ReceiveEnvironmentDamage</c>.</summary>
    Environment,
}

/// <summary>
/// One blow arriving at one player, described rather than executed. <see cref="Session.TakeDamage"/> runs it.
///
/// <para>The per-blow facts live here (how much, from whom, what to say, what to log); the per-KIND facts —
/// which terms of the sequence run and in what order — live in <see cref="Terms"/>, keyed off
/// <see cref="Kind"/>, because they are properties of the damage source and not of the individual hit.</para>
/// </summary>
public sealed class DamageIntake
{
    public DamageIntake(DamageKind kind, int raw)
    {
        Kind = kind;
        Raw  = raw;
    }

    public DamageKind Kind { get; }

    /// <summary>Damage before any intake-side term. What "before" means differs by kind — a PvP melee figure
    /// has already had the victim's armor and rear-x2 applied attacker-side, a mob swing has not.</summary>
    public int Raw { get; }

    /// <summary>Whether this source walks past the Harden Body ward. Required — never defaulted — because
    /// two of the five sources answer differently from the other three and the difference must be stated,
    /// not inherited. See the provenance note at the top of <see cref="Session.TakeDamage"/>.</summary>
    public required bool IgnoresHardenBody { get; init; }

    /// <summary>The creature to record as <c>owner.attacker</c> (RTK's "last thing that actually landed a
    /// blow on you", which a Call of the Wild pet reads). Null for every non-creature source; stamped only
    /// once the blow is past the gates, exactly as before.</summary>
    public Mob? StampMobAttacker { get; init; }

    /// <summary>The positional "attacked from behind while both face the same way" x2 (RTK
    /// swingDamage.lua's <c>side == target.side</c> rule). Decided by the caller, which owns the geometry.</summary>
    public bool DoubleForRear { get; init; }

    /// <summary>The over-head bar's hit-animation byte (RTK: 33 normal, 255 critical). Ignored when
    /// <see cref="RollCritByte"/> is set.</summary>
    public byte CritByte { get; init; }

    /// <summary>A DEFERRED crit byte, for the one source that has to roll for it. Deferred because
    /// <c>Combat.RollCritChance</c> draws from <c>Random.Shared</c>, and RTK returns on an immune target
    /// before the damage calc runs at all — computing it at the call site would advance the shared stream on
    /// blows that never land. The pipeline invokes it at the original position: after the gates, before the
    /// damage math (and therefore before the durability rolls, which draw from the same stream).</summary>
    public Func<byte>? RollCritByte { get; init; }

    /// <summary>Sound id layered on the victim's own tile once the blow lands (RTK binds the landed-hit
    /// sound to the VICTIM). 0 = silent, which is every source but the creature swing.</summary>
    public int HitSound { get; init; }

    /// <summary>The 0x0A line the victim reads, or null for the sources that show only the HP bar. Built by
    /// the caller because only it knows whether there is anyone to name.</summary>
    public string? MiniText { get; init; }

    /// <summary>The other player in a PvP exchange — BOTH sides get marked, which is what an arena pet reads
    /// to pick someone to go for. Null for a self-cast and for everything a player did not do.</summary>
    public Session? PvpFoe { get; init; }

    /// <summary>The log line, given the damage that actually landed. A delegate rather than a string because
    /// every one of these reads the victim's HP AFTER the deduction.</summary>
    public Func<int, string>? LogLine { get; init; }

    /// <summary>Run last, and only when the blow landed AND the victim survived it — the creature on-hit
    /// spell proc, which must trail neither a blocked blow nor a corpse.</summary>
    public Action? AfterSurvivedHit { get; init; }

    /// <summary>Which terms of the damage sequence this kind runs, and in which order.</summary>
    public readonly record struct Sequence(
        bool FloorRawToOne, bool AppliesArmor, bool ArmorBeforeAmp, bool DeductsDurability);

    /// <summary>
    /// The whole divergence matrix, in one readable table — the thing five copy-pasted intakes never let
    /// anyone see at once.
    ///
    /// <para><b>FloorRawToOne</b> — four sources raise a sub-1 figure to 1 so a heavily-reduced hit still
    /// stings; the creature swing does not, because its armor term (<c>Combat.ApplyArmor</c>) already floors
    /// its own result at 1.</para>
    /// <para><b>AppliesArmor</b> — physical AC is netted at intake for a creature swing (the attacker side
    /// has no view of our gear) and for a player's spell (centralised there so no PvP spell path can forget
    /// it, and no path can apply it twice). A PvP melee figure arrives already netted, via
    /// <c>SwingTarget.Of(this)</c> on the attacker side. Creature spells and room damage skip AC entirely —
    /// "the AC pass belongs to swings, not spells".</para>
    /// <para><b>ArmorBeforeAmp</b> — the creature swing nets armor and THEN applies the sleep-family
    /// amplifier, which is RTK swingDamage.lua's own order; a player's spell amplifies first and nets after.
    /// It is not a rounding curiosity: at AC -50 with a 1.5x amplifier, a raw 101 lands as 75 one way and 76
    /// the other. Preserved per kind rather than unified, because unifying it would be a behaviour change and
    /// #28 is explicitly not that.</para>
    /// <para><b>DeductsDurability</b> — RTK clif_deductarmor rolls every worn slot on a HIT; magic and the
    /// room do not touch gear.</para>
    /// </summary>
    public Sequence Terms => Kind switch
    {
        //                                 floor<1  armor  armor-1st  dura
        DamageKind.MobMelee    => new Sequence(false, true,  true,     true),
        DamageKind.PlayerSpell => new Sequence(true,  true,  false,    false),
        DamageKind.PlayerMelee => new Sequence(true,  false, false,    true),
        DamageKind.MobSpell    => new Sequence(true,  false, false,    false),
        DamageKind.Environment => new Sequence(true,  false, false,    false),
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "unhandled damage kind"),
    };
}

public sealed partial class Session
{
    /// <summary>
    /// The one player-damage pipeline (#28). Every blow a player takes runs this sequence: immunity gate,
    /// wake, attacker stamp, amplifier, armor, rear x2, deduction, HP, durability, stats, over-head bar,
    /// sound, chat line, PvP foe mark, log, death — with the per-kind subset and ordering coming from
    /// <see cref="DamageIntake.Terms"/>.
    ///
    /// <para>It replaces five hand-copied versions of that sequence. Three checked
    /// <see cref="DamageImmune"/> and two did not, which is the finding this pipeline exists to make
    /// impossible to repeat: a new intake term is now one edit, and a new intake SOURCE has to state its
    /// answer to <see cref="DamageIntake.IgnoresHardenBody"/> out loud.</para>
    /// </summary>
    /// <returns>The damage actually applied, AFTER every term but NOT capped by the victim's remaining HP —
    /// so a killing blow reports how far past zero it went (what the vita strikes' overflow is computed
    /// from). 0 when nothing landed: already dead, or immune.</returns>
    internal int TakeDamage(DamageIntake intake)
    {
        if (IsDead) return 0;   // already down — don't re-trigger Die() while the revive delay is pending
        // ---- Harden Body: total damage immunity -----------------------------------------------------------
        // RTK Player.removeHealthExtend (player.lua:163) opens by RETURNING OUTRIGHT if any of four wards is
        // up: harden_body_poet / deaths_guard_poet / lifes_protection_poet / body_of_alignment_poet — the
        // poet spell and its three alignment reskins. No net-damage calc, no HP change: the blow simply does
        // not land. The Scroll of Immortality grants the same ward (item_verbs.lua `hardenbody`, 16s, behind
        // RTK's armor-scaled success roll), which is what makes the scroll worth its name.
        //
        // Two of the five sources set IgnoresHardenBody and walk past this. That is preserved behaviour, not
        // an endorsement — see the note on DamageKind.MobSpell / DamageKind.Environment at their call sites.
        if (!intake.IgnoresHardenBody && DamageImmune) return 0;

        WakeUp(byDamage: true);   // being hit ends a Doze (RTK on_takedamage_while_cast) — see ReceiveSleep

        // RTK's `owner.attacker`: the last creature to actually land a blow on you. A Call of the Wild pet
        // reads this to decide what to defend you from — see World.Tick's pet block. Set on the LANDED hit,
        // not on aggro, which is the whole difference between a pet that holds a corner and one that charges
        // the moment anything looks at you.
        if (intake.StampMobAttacker is { } attackerMob)
        {
            LastMobAttackerId = attackerMob.Id;
            LastMobAttackerAt = Environment.TickCount64;
        }

        // Resolved here, at the position the mob swing's roll used to sit: past the gates (so a blocked blow
        // consumes no randomness) and ahead of the durability rolls (so it keeps its place in the stream).
        byte critByte = intake.RollCritByte is null ? intake.CritByte : intake.RollCritByte();

        var terms = intake.Terms;
        int dmg = intake.Raw;
        if (terms.FloorRawToOne && dmg < 1) dmg = 1;

        // RTK swingDamage.lua: finalDamage = floor(finalDamage * (1 + max(armor,-80)/100)). AC is signed and
        // LOWER is better, and gear/buff armor is an AC delta in the same units, so it simply ADDS (a -4 garb
        // takes 4 off your AC; see Session.Items.EquipTotals). A well-armored (very negative effective AC)
        // player takes as little as 20% of the raw swing, while a naked/positive-AC player takes MORE than
        // raw — armor can't fully negate a hit (-80 floor = min 20%).
        //
        // Sleep-family amplifier: being dozed/slept makes the NEXT hit on you land harder (Doze 1.3x,
        // Sleep 1.5x). Consumed here, so it applies to one hit only — WakeUp above already broke the hold,
        // and together that is the whole point of the spell: set up one amplified opener.
        //
        // The two run in opposite orders on the two kinds that have both. See DamageIntake.Terms.
        if (terms.ArmorBeforeAmp)
        {
            if (terms.AppliesArmor) dmg = Combat.ApplyArmor(dmg, _char.Ac + Totals().armor, floor: -80);
            double armorFirstAmp = TakeDamageAmp();
            if (armorFirstAmp > 1.0) dmg = (int)Math.Round(dmg * armorFirstAmp);
        }
        else
        {
            double amp = TakeDamageAmp();
            if (amp > 1.0) dmg = (int)Math.Round(dmg * amp);
            if (terms.AppliesArmor) dmg = Combat.ApplyArmor(dmg, _char.Ac + Totals().armor, floor: -80);
        }

        if (intake.DoubleForRear) dmg *= 2;

        // RTK player.deduction: a flat damage-reduction multiplier from the sanctuary line / Baekho's Cunning
        // (1.0 normally, down to 0.5/0.6 while active). Applied last, after armor + position.
        if (EffDeduction < 1.0) dmg = (int)Math.Round(dmg * EffDeduction);

        _char.Hp = (uint)Math.Max(0, (int)_char.Hp - dmg);
        // RTK clif_deductarmor: taking a hit rolls durability loss on every worn slot (not just armor —
        // the reference implementation checks the weapon slot here too).
        if (terms.DeductsDurability) foreach (var worn in _char.Equipment.ToArray()) DeductDura(worn);
        SendStats();

        byte hpPct = PlayerHpPercent();   // same for every peer — compute once, not inside the per-peer lambda
        _world.BroadcastWideArea(_char.Map, _char.X, _char.Y, p => p.DamageOver(_char.Id, hpPct, critByte));
        if (intake.HitSound != 0)
        {
            int sound = intake.HitSound;
            // 001.wav: layered on the 009 swing sfx World.Tick already played (RTK binds a landed hit to the
            // VICTIM, so it rings from OUR tile).
            _world.BroadcastSameArea(_char.Map, _char.X, _char.Y, p => p.SoundAt(sound, _char.Id));
        }
        if (intake.MiniText is { } line) SendMiniText(line);
        if (intake.PvpFoe is { } foe)
        {
            // Both sides remember the exchange — that's what a PvP-map pet reads to pick a person to go for.
            MarkPvpFoe(foe._char.Id);
            foe.MarkPvpFoe(_char.Id);
        }
        if (intake.LogLine is { } log) Log.Info(log(dmg));

        if (IsDead) { Die(); return dmg; }
        intake.AfterSurvivedHit?.Invoke();
        return dmg;
    }
}
