using MoonSharp.Interpreter;

namespace Server;

/// <summary>
/// The Lua-facing facade a spell verb (<c>data/game-data/spell_verbs.lua</c>) uses to act on the game — the
/// spell-cast analogue of <see cref="NpcContext"/>. Created per cast, bound to one caster + target. It exposes
/// a small, safe set of engine primitives (spend mana, damage the target, heal, speak) plus read-only caster
/// stats, so a verb reads as linear script:
/// <code>if ctx:spendMana(row.mana) then ctx:damage(row.base + ctx.will * row.coeff) end</code>
/// Member names are deliberately lower-cased to read naturally from Lua (<c>ctx.will</c>, <c>ctx:damage(n)</c>).
/// Every primitive delegates to an existing <see cref="Session"/> path (e.g. <c>_world.TryDamage</c>), so the
/// Lua route shares the exact same combat/heal plumbing as the C# <c>CastX</c> methods — no divergent logic.
/// </summary>
[MoonSharpUserData]
public sealed class SpellContext
{
    private readonly Session _s;
    private readonly SpellDef _sp;
    private readonly uint? _targetId;

    internal SpellContext(Session s, SpellDef sp, uint? targetId, string? answer)
    {
        _s = s; _sp = sp; _targetId = targetId; this.answer = answer ?? "";   // set the PROPERTY, not the param (was leaving ctx.answer null)
    }

    /// <summary>Archetype-path ctor: the engine has already resolved this spell's mana cost and evaluated its
    /// per-spell formula (spell_effects.csv <c>amountExpr</c>), so an archetype verb (arch_damage/arch_heal/…) can
    /// stay pure logic — <c>ctx.amount</c> / <c>ctx.mana</c> carry the numbers, no formula duplicated into Lua.</summary>
    internal SpellContext(Session s, SpellDef sp, uint? targetId, string? answer, double amount, double mana)
        : this(s, sp, targetId, answer)
    {
        this.amount = amount; this.mana = mana;
    }

    /// <summary>Engine-evaluated effect amount for the archetype path (the spell's real formula result).</summary>
    public double amount { get; }
    /// <summary>Engine-resolved mana cost for the archetype path.</summary>
    public double mana   { get; }

    // ---- read-only caster stats (Lua: ctx.will, ctx.level, ...) ----
    public double level     => _s.LuaLevel;
    public double will      => _s.LuaWill;
    public double grace     => _s.LuaGrace;
    public double might     => _s.LuaMight;
    public double hp        => _s.LuaHp;
    public double maxHp     => _s.LuaMaxHp;
    public double mp        => _s.LuaMp;
    public bool   hasTarget => _s.LuaHasTarget(_targetId);
    /// <summary>The player's typed answer to the spell's question prompt (empty if none).</summary>
    public string answer    { get; }

    // ---- primitives (Lua: ctx:spendMana(n), ctx:damage(n), ...) ----
    /// <summary>Check + debit the caster's mana; false (with a "not enough mana" notice) if short.</summary>
    public bool spendMana(double amt)   => _s.LuaSpendMana((int)amt, _sp);
    /// <summary>Damage the resolved target mob (handles deflect/HP-bar/death/XP). False if no target.</summary>
    public bool damage(double amt)      => _s.LuaDamageTarget((int)System.Math.Round(amt), _sp, _targetId);
    /// <summary>Full magic-attack sequence matching the C# Damage archetype exactly: mana check → resolve target →
    /// deflect roll (no mana spent on a deflect) → spend mana → apply. Returns false if the cast couldn't happen
    /// (no mana / no target — a notice was sent), true otherwise (including a deflect). Used by arch_damage.</summary>
    public bool magicDamage(double amt, double manaCost) =>
        _s.LuaMagicDamage((int)System.Math.Round(amt), (int)manaCost, _sp, _targetId);
    /// <summary>Heal the caster's own HP (capped at max), with the spell's sparkle fx.</summary>
    public void heal(double amt)        => _s.LuaHeal((int)System.Math.Round(amt), _sp);
    /// <summary>Restore the caster's own mana (capped at max).</summary>
    public void restoreMana(double amt) => _s.LuaRestoreMana((int)System.Math.Round(amt));
    /// <summary>Apply a timed stat buff (e.g. "might"/"dam"/"hit") of <paramref name="amount"/> for
    /// <paramref name="durationMs"/> ms; re-casting the same spell refreshes it.</summary>
    public void buff(string stat, double amount, double durationMs) =>
        _s.LuaBuff(stat, (int)System.Math.Round(amount), (int)System.Math.Round(durationMs), _sp);
    /// <summary>Status-box text (RTK sendminitext).</summary>
    public void say(string msg)         => _s.LuaSay(msg);
    /// <summary>Chat-log message.</summary>
    public void message(string msg)     => _s.LuaMessage(msg);

    // ---- primitives for COMPOSED / stateful verbs (Baekho's Cunning) ----
    /// <summary>Read a transient per-caster integer registry value (0 if unset). Resets on relog.</summary>
    public int  reg(string key)                       => _s.LuaReg(key);
    /// <summary>Set a transient per-caster integer registry value.</summary>
    public void setReg(string key, double v)          => _s.LuaSetReg(key, (int)v);
    /// <summary>Is a named duration (RTK setDuration) still running?</summary>
    public bool hasDuration(string key)               => _s.LuaHasDuration(key);
    /// <summary>Start/refresh a named duration for <paramref name="ms"/> milliseconds.</summary>
    public void setDuration(string key, double ms)    => _s.LuaSetDuration(key, (int)ms);
    /// <summary>Is a named cooldown (RTK aether) still ticking?</summary>
    public bool onCooldown(string key)                => _s.LuaOnCooldown(key);
    /// <summary>Start a named cooldown for <paramref name="ms"/> milliseconds.</summary>
    public void setCooldown(string key, double ms)    => _s.LuaSetCooldown(key, (int)ms);
    /// <summary>Arm the melee rage multiplier (whole-swing ×amount) for <paramref name="durMs"/> ms.</summary>
    public void rage(double amount, double durMs)     => _s.LuaSetRage((int)amount, (int)durMs);
    /// <summary>Arm a damage-reduction: <paramref name="mult"/> is the incoming-damage multiplier (0.5 = take half).</summary>
    public void deduction(double mult, double durMs)  => _s.ApplyDeduction(mult, (int)durMs, _sp.Name);
    /// <summary>Arm (on=true) or clear a positional stance ("backstab"/"flank") for <paramref name="durMs"/> ms.</summary>
    public void stance(string name, bool on, double durMs) => _s.LuaStance(name, on, (int)durMs);
    /// <summary>Play a cast animation + sound on the caster (Effect.tbl anim id, sound id).</summary>
    public void fx(double anim, double sound)         => _s.LuaFx((int)anim, (int)sound);
}
