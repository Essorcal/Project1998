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

    /// <summary>
    /// Whether this source walks past the Harden Body ward. Required — never defaulted — because the five
    /// sources do NOT all answer the same way, and the difference has to be a stated position rather than an
    /// omission. It was an omission: <c>ReceiveMobSpell</c> was written three days before
    /// <c>Session.DamageImmune</c> existed and never had the check retrofitted, and
    /// <c>ReceiveEnvironmentDamage</c> was copied from it a week later (#28).
    ///
    /// <para><b>RTK, read out of the reference server (rtklua/Accepted).</b> A player's HP comes off through
    /// one of three functions and only one of them carries the ward check:
    /// <c>Player.removeHealthExtend</c> (player.lua:164) opens by RETURNING OUTRIGHT on any of
    /// harden_body_poet / deaths_guard_poet / lifes_protection_poet / body_of_alignment_poet — no
    /// net-damage calc, no HP change, the blow does not land. <c>Player.removeHealthWithoutDamageNumbers</c>
    /// (player.lua:111) and the engine's plain <c>removeHealth</c> both subtract unconditionally. So "does
    /// the ward stop this?" is answered, per source, by which of the three that source calls.</para>
    ///
    /// <para><b>Creature spells: BLOCKED, unanimously.</b> Every creature spell in RTK that takes HP off a
    /// player ends in <c>target:removeHealthExtend(...)</c>, so the ward stops all of them. The instantaneous
    /// ones pass every term on (<c>amount, 1, 1, 1, 1, 0</c>): Spells/NPCs/ion.lua:9, call_lightning.lua:11,
    /// thunder_touch.lua:9 — the lightning line mob_ai_mythic.lua fires — plus freeze.lua:29 and
    /// stormstrike.lua:9. The over-time ones pass every term OFF (<c>damage, 0, 0, 0, 0, 0</c>) and floor the
    /// victim at 1 HP instead of killing, because they run per tick out of <c>while_cast</c>: burn.lua:51 and
    /// :59, venom.lua:61 and :74. The mythic boss's own abilities are the same call again —
    /// Instances/instance_boss.lua:288 (Rockslide) and :574 (the Baekdu Guardian's gust). A ward stops a
    /// boss's Thunder touch in RTK, so <see cref="DamageKind.MobSpell"/> honours it here. That is the one
    /// behaviour #28 changed, and it is the finding rather than a side effect of it.</para>
    ///
    /// <para><b>Room hazards: NOT blocked.</b> RTK's trap scripts split, and they split on exactly the axis
    /// that matters. The five you STEP ON — NPCs/trap/rogue_traps/{dart,death,spear,repeating_dart}_trap.lua
    /// and Ranger/pit_trap.lua — all call the plain <c>block:removeHealth(damage)</c>. The two that reach out
    /// and pick a target (bladestorm_trap.lua, Spy/toxic_spray.lua) use <c>removeHealthExtend</c> and are
    /// blocked. Sute's cold tiles are a stepped-on hazard, so <see cref="DamageKind.Environment"/> keeps this
    /// flag set.</para>
    ///
    /// <para>That plain <c>removeHealth</c> really is unguarded, in the ENGINE and not merely in the Lua
    /// that calls it: it binds to <c>pcl_removehealth</c> (rtk/src/map/sl.c:7848-7885), which sets the
    /// damage/crit fields and hands off to <c>clif_send_pc_healthscript</c> (clif.c:1229) — and neither
    /// function contains a ward test of any kind. So the stepped-on traps bypass Harden Body by direct
    /// evidence, not by inference from which Lua helper they happened to pick.</para>
    ///
    /// <para>The caveat that does remain is a different one, and it stands: Sute's cave is later content with
    /// no RTK script of its own (see docs/common/Deferred-Work.md, "Sute's combat kit is built from ONE
    /// eyewitness report"), so the stepped-on trap family is the nearest ANALOGUE RTK offers for a cold tile,
    /// not a reading of the cold tile itself. The mechanism is now sourced to the engine; the decision that a
    /// cold tile belongs in that family is still ours.</para>
    ///
    /// <para><i>Checked and rejected as a counter-example:</i> Instances/instance_boss.lua's rockdamage
    /// (:288) and gustdamage (:574) do go through <c>removeHealthExtend</c>, but they are not room damage —
    /// <c>instance_boss</c> is a mob AI table, the blows are attributed to <c>mob.ID</c> and announced as
    /// "&lt;mob&gt; casts Rockslide on you." and "[Baekdu Guardian]: Haha, pitiful fool!". They are creature
    /// abilities, and they belong to the MobSpell row above, which they support. Nothing in RTK damages a
    /// player from the ROOM except the traps.</para>
    ///
    /// <para><b>What the ward suppresses here that RTK still prints.</b> ion.lua sends its
    /// "&lt;mob&gt; attacks you with &lt;spell&gt; spell." line BEFORE removeHealthExtend, so an RTK player
    /// under a ward sees the line and takes nothing. Ours carries the line on the intake, so a blocked mob
    /// spell prints nothing (the caster's shout, animation and sound still go out from ApplyMobSpell, which
    /// runs first). Left as-is deliberately: our player-spell intake already swallows its own "X hits you
    /// with Y." the same way, and matching RTK on the mob side alone would make the two disagree inside our
    /// own codebase. Recorded rather than silently accepted.</para>
    ///
    /// <para><b>Two divergences this reading turned up and did NOT fix</b>, per the #21 ground rule that a
    /// divergence found while refactoring gets its own issue instead of a silent edit — they are
    /// <b>#77</b> and <b>#78</b>. (#77) RTK nets the player's armor on every damage path a player meets in
    /// one piece: the instantaneous creature spells and every PvP spell pass <c>ac = 1</c> to
    /// <c>Player.calculateNetDamage</c> (player.lua:228-244), and even the stepped-on traps run
    /// <c>calculateDamage</c> (scripts.lua:1183-1204) first. Ours skips AC on
    /// <see cref="DamageKind.MobSpell"/> and <see cref="DamageKind.Environment"/>, on the "magic ignores
    /// physical AC" rule — and <c>SuteAi.IceRayObservedDamage</c>'s 405-at-AC--22 reading was taken as the
    /// RAW figure precisely because of that assumption, so turning AC on means re-deriving Sute's whole kit.
    /// (#78) RTK's three damage helpers all run amplifier, deduction, THEN armor; every intake here applies
    /// armor before the deduction, and the mob swing also amplifies on the wrong side of it.</para>
    ///
    /// <para><b>Two damage paths this pipeline does not reach</b>, both in files owned by #26 while it is in
    /// flight, so they are named here rather than converted. (<b>#79</b>) <c>Session.NpcCastStormstrike</c>
    /// (Session.Spells.cs:2049) borrows <see cref="DamageKind.Environment"/> for a NAMED caster's spell,
    /// purely because that intake is the one that lets the caller supply the whole chat line — so a mythic's
    /// Stormstrike still walks through a ward, where RTK's own stormstrike.lua:9 is
    /// <c>removeHealthExtend</c> and is stopped by one. It is the last creature spell that ignores Harden
    /// Body, and the fix is small: the two kinds' term rows below are identical, so it wants
    /// <see cref="DamageKind.MobSpell"/> with a caller-supplied line — one sibling method in
    /// Session.MobSpells.cs, one line at a call site this branch must not touch. Note that the enum theory in
    /// Tests/DamageIntakeTests.cs cannot catch this class of bug: it proves each KIND behaves, not that each
    /// caller picked the right one. (<b>#82</b>) <c>Session.TickPoison</c> (Session.Spells.cs:1825) is a
    /// sixth damage path that never went through any of the five and so was outside #28's scope — it
    /// hand-rolls this very epilogue (HP, WakeUp, SendStats, mini-text, the over-head bar, the foe mark),
    /// which makes #28's "five copies" really six. RTK's equivalent ticks (burn/venom <c>while_cast</c>) are
    /// ward-gated like everything else.</para>
    /// </summary>
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
    /// amplifier; a player's spell amplifies first and nets after. It is not a rounding curiosity: at AC -50
    /// with a 1.5x amplifier, a raw 101 lands as 75 one way and 76 the other. Preserved per kind rather than
    /// unified: unifying it would be an UNSOURCED behaviour change, and the only behaviour #28 changes is the
    /// sourced one (see <see cref="IgnoresHardenBody"/>). <b>Neither order is RTK's</b> — swingDamage.lua
    /// amplifies at :92 and nets armor at :105-108, i.e. amplifier first on the swing too, and all three of
    /// RTK's damage helpers put the deduction BEFORE armor where every kind here puts it after. Filed as
    /// #78 rather than fixed here, per the #21 ground rule.</para>
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
    /// answer to <see cref="DamageIntake.IgnoresHardenBody"/> out loud — with a citation, since that
    /// property's own note is where the RTK reading lives.</para>
    /// </summary>
    /// <returns>The damage actually applied, AFTER every term but NOT capped by the victim's remaining HP —
    /// so a killing blow reports how far past zero it went (what the vita strikes' overflow is computed
    /// from). 0 when nothing landed: already dead, or immune.</returns>
    internal int TakeDamage(DamageIntake intake)
    {
        if (IsDead) return 0;   // already down — don't re-trigger Die() while the revive delay is pending
        // ---- Harden Body: total damage immunity -----------------------------------------------------------
        // RTK Player.removeHealthExtend (player.lua:164) opens by RETURNING OUTRIGHT if any of four wards is
        // up: harden_body_poet / deaths_guard_poet / lifes_protection_poet / body_of_alignment_poet — the
        // poet spell and its three alignment reskins. No net-damage calc, no HP change: the blow simply does
        // not land. The Scroll of Immortality grants the same ward (item_verbs.lua `hardenbody`, 16s, behind
        // RTK's armor-scaled success roll), which is what makes the scroll worth its name — and is our one
        // deliberate widening, since RTK's own scroll sets `harden_body` while removeHealthExtend looks only
        // at the four `*_poet` keys, so the RTK scroll grants a ward that stops nothing.
        //
        // Four of the five sources reach this gate; the fifth (room damage) walks past it, sourced. Which
        // ones and why is the note on DamageIntake.IgnoresHardenBody.
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
