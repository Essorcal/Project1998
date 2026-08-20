using Shared;

namespace Server;

/// <summary>
/// Creatures casting spells at players — the <c>CastFromMob</c> path.
///
/// <para>RTK's spell scripts are caster-agnostic: <c>peck.cast(block, target)</c> takes a "block" that is a
/// mob as readily as a player, which is how a raven blinds you with the same script a rogue would use. Ours
/// aren't — <see cref="SpellContext"/> is bound to a <see cref="Session"/>, and generalising it would mean
/// reopening the player cast path (mana, aethers, the action budget, the spellbook, deflect rolls) for a
/// caster that needs none of it. So this is a deliberately narrow second door: a creature's repertoire is
/// DATA (game-data/MobSpells.csv), and each effect lands through the same primitive a player spell would
/// use — <see cref="ReceiveMobSpell"/> for damage, <see cref="ReceivePoison"/> for venom,
/// <see cref="ReceiveCurse"/> for the categorised statuses. Nothing here duplicates an effect.</para>
///
/// <para><b>Blind is the one honest gap.</b> RTK's peck does <c>target.blind = true</c> on a PC, and this
/// server has no player-blind state at all (our <c>BlindUntil</c> is mob-only, and no client-side mechanism
/// for darkening a player's view has been identified). A blind-effect row therefore occupies the
/// <c>blinds</c> exclusivity slot and prints its line — so cures work on it and the profile shows it — but
/// does not actually impair the player. Marked here rather than silently approximated.</para>
/// </summary>
public sealed partial class Session
{
    /// <summary>Take spell damage from a CREATURE. The mob twin of <see cref="ReceiveSpellDamage"/>: same
    /// deduction reduction and the same "magic ignores physical AC" rule (the AC pass belongs to swings, not
    /// spells), attributed to the mob so the message names it.</summary>
    internal void ReceiveMobSpell(int rawDmg, Mob caster, string spellName)
    {
        if (IsDead) return;
        WakeUp(byDamage: true);            // a spell to the face ends a doze, exactly like a swing
        if (rawDmg < 1) rawDmg = 1;
        double amp = TakeDamageAmp();      // sleep-family amplifier, same as every other damage source
        if (amp > 1.0) rawDmg = (int)Math.Round(rawDmg * amp);
        int dmg = EffDeduction < 1.0 ? (int)Math.Round(rawDmg * EffDeduction) : rawDmg;

        _char.Hp = (uint)Math.Max(0, (int)_char.Hp - dmg);
        SendStats();
        byte hpPct = PlayerHpPercent();
        _world.BroadcastWideArea(_char.Map, _char.X, _char.Y, p => p.DamageOver(_char.Id, hpPct, HitCritByte));
        SendMiniText($"{caster.Name} attacks you with {spellName} spell.");   // RTK peck.lua's own wording
        Log.Info($"   -> mob {caster.Id} '{caster.Name}' cast {spellName} on {_char.Name} for {dmg} -> {_char.Hp}/{_char.MaxHp}");
        if (IsDead) Die();
    }

    /// <summary>Land one creature spell on this player. Called from World.Tick's post-lock resolve pass, so
    /// it is free to broadcast and to kill.</summary>
    internal void ApplyMobSpell(Mob caster, Content.MobSpellDef spell)
    {
        if (IsDead) return;

        // The creature announces itself first (RTK: mob:talk(0, mob.name .. ": ** summons power **")), so the
        // shout lands before the damage rather than after the corpse hits the floor.
        if (spell.Say.Length > 0) _world.BroadcastArea(_char.Map, caster.X, caster.Y, SayHalfW, SayHalfH,
            p => p.SpeakEntity(0, caster.Id, AsciiBytes($"{caster.Name}: {spell.Say}")));
        if (spell.Anim > 0 || spell.Sound > 0)
            // Both halves ride OUR tile (the spell lands on us) and are range-gated the way RTK gates them:
            // the graphic over the AREA box, the sound over the tighter SAMEAREA one. See World.SoundHalfW.
            _world.BroadcastWideArea(_char.Map, _char.X, _char.Y, p => { if (spell.Anim > 0) p.EffectOver(_char.Id, spell.Anim); });
            if (spell.Sound > 0) _world.BroadcastSameArea(_char.Map, _char.X, _char.Y, p => p.SoundAt(spell.Sound, _char.Id));

        switch (spell.Effect)
        {
            case "damage":
                ReceiveMobSpell(spell.Amount, caster, spell.Name);
                break;

            case "poison":
                // `by` is the caster id purely for attribution; a mob-sourced venom ticks exactly like a
                // player's, floor and all (TickPoison never deals the killing blow).
                ReceivePoison(spell.Amount, spell.DurationMs, caster.Id, spell.Anim, $"mob_{spell.Name}", spell.Name);
                break;

            case "curse":
            case "blind":   // see the class doc: slot + message only, no player-blind state exists
                if (HasStatusCategory(spell.Category)) return;   // one per family, same rule as a player curse
                ReceiveCurse(spell.Stat, -Math.Abs(spell.Amount), spell.DurationMs,
                             $"mob_{spell.Name}", spell.Name, spell.Category);
                SendMiniText($"{caster.Name} attacks you with {spell.Name} spell.");
                break;

            default:
                Log.Info($"   ?? MobSpells.csv: '{caster.Key}' has unknown effect '{spell.Effect}'");
                break;
        }
    }
}
