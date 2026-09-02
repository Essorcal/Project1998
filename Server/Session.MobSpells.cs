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
    /// spells), attributed to the mob so the message names it.
    ///
    /// <para><b>Harden Body stops this, as of #28.</b> It did not before — this method predates
    /// <c>Session.DamageImmune</c> and never had the check retrofitted — and RTK settles it: every one of
    /// RTK's creature damage spells (ion, call lightning, thunder touch) lands through
    /// <c>Player.removeHealthExtend</c>, which returns outright while a ward is up. The full reading is on
    /// <see cref="DamageIntake.IgnoresHardenBody"/>.</para></summary>
    internal void ReceiveMobSpell(int rawDmg, Mob caster, string spellName)
    {
        TakeDamage(new DamageIntake(DamageKind.MobSpell, rawDmg)
        {
            IgnoresHardenBody = false,   // RTK Spells/NPCs/{ion,call_lightning,thunder_touch}.lua -> removeHealthExtend
            CritByte = HitCritByte,
            MiniText = $"{caster.Name} attacks you with {spellName} spell.",   // RTK peck.lua's own wording
            LogLine  = dmg => $"   -> mob {caster.Id} '{caster.Name}' cast {spellName} on {_char.Name} for {dmg} -> {_char.Hp}/{_char.MaxHp}",
        });
    }

    /// <summary>Take damage from the ROOM rather than from a creature — Sute's Cave cold tiles
    /// (<see cref="Session.TakeFrigidBlast"/>) are the only source today. Identical to
    /// <see cref="ReceiveMobSpell"/> — same doze-break, same sleep amplifier, same deduction reduction, same
    /// AC-is-for-swings rule — except that there is no caster to attribute it to, so the caller supplies the
    /// whole line instead of it being built from a name.
    ///
    /// <para><b>This is the one intake Harden Body does NOT stop</b>, and it is a sourced position rather
    /// than the gap it used to be: RTK's stepped-on traps take health off with the plain
    /// <c>removeHealth</c>, which has no ward check, while the traps that reach out and pick a target use
    /// the ward-checked <c>removeHealthExtend</c>. A cold tile is stepped on. See
    /// <see cref="DamageIntake.IgnoresHardenBody"/> for the reading and for what is thin about it.</para></summary>
    internal void ReceiveEnvironmentDamage(int rawDmg, string text)
    {
        TakeDamage(new DamageIntake(DamageKind.Environment, rawDmg)
        {
            IgnoresHardenBody = true,   // RTK NPCs/trap/rogue_traps/{dart,death,spear,pit}_trap.lua -> plain removeHealth
            CritByte = HitCritByte,
            MiniText = text,
            LogLine  = dmg => $"   -> environment hit {_char.Name} for {dmg} -> {_char.Hp}/{_char.MaxHp} ({text})",
        });
    }

    /// <summary>Roll this creature's <c>onhit</c> spells because one of its swings just LANDED on us.
    ///
    /// <para>The other trigger — World.Tick's cast timer — cannot express "a one-in-four chance to venom you"
    /// however the numbers are set, because it re-rolls every 333ms tick until it passes: <c>Chance</c> only
    /// moves WHEN the cast lands, and <c>EveryMs</c> is what actually paces it. Hanging the roll on the blow
    /// makes the column mean what it says. It is also the shape the caverns' venom was observed having —
    /// you get poisoned by being hit, not by standing next to something.</para>
    ///
    /// <para>Called from <see cref="ApplyMobHit"/> on the landed blow only (a miss and a killing blow both
    /// skip it), which is already outside the world lock, so this is free to broadcast exactly as the timer
    /// path is. One spell per blow, first match in file order — the same rule the timer roll uses.</para></summary>
    internal void TryMobOnHitSpell(Mob caster)
    {
        if (IsDead) return;
        if (!Content.MobSpells.TryGetValue(caster.Key, out var repertoire)) return;
        foreach (var sp in repertoire)
        {
            if (!sp.OnHit) continue;
            if (Random.Shared.Next(Math.Max(1, sp.Chance)) != 0) continue;
            ApplyMobSpell(caster, sp);   // announce + fx + effect, identical to a timed cast
            return;
        }
    }

    /// <summary>Land one creature spell on this player. Called from World.Tick's post-lock resolve pass, so
    /// it is free to broadcast and to kill.</summary>
    internal void ApplyMobSpell(Mob caster, Content.MobSpellDef spell)
    {
        if (IsDead) return;

        // The creature announces itself first (RTK: mob:talk(0, mob.name .. ": ** summons power **")), so the
        // shout lands before the damage rather than after the corpse hits the floor.
        string say = spell.PickSay();   // one of the row's "|"-separated alternatives
        if (say.Length > 0) _world.BroadcastArea(_char.Map, caster.X, caster.Y, SayHalfW, SayHalfH,
            p => p.SpeakEntity(0, caster.Id, AsciiBytes($"{caster.Name}: {say}")));
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
                // Already venomed: the `venoms` category holds ONE at a time, same rule as the curse branch
                // below, so a second creature's cast must not re-arm it. Without this, a room of casters
                // re-applies on every cooldown — which re-prints the "Poison courses through you." line and,
                // worse, keeps pushing the expiry out so a long venom never actually runs down.
                if (Poisoned) return;
                // `by` is the caster id purely for attribution; a mob-sourced venom ticks exactly like a
                // player's, floor and all (TickPoison never deals the killing blow).
                ReceivePoison(spell.Amount, spell.DurationMs, caster.Id, spell.Anim, $"mob_{spell.Name}", spell.Name,
                              spell.PerTick, spell.TickMinMs, spell.TickMaxMs);
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
