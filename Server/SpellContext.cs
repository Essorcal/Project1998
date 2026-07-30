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
    private readonly SpellFx? _fx;   // the spell's export row (buff stats / duration / chance) for archetype verbs

    internal SpellContext(Session s, SpellDef sp, uint? targetId, string? answer)
    {
        _s = s; _sp = sp; _targetId = targetId; this.answer = answer ?? "";   // set the PROPERTY, not the param (was leaving ctx.answer null)
    }

    /// <summary>Archetype-path ctor: the engine has already resolved this spell's mana cost and evaluated its
    /// per-spell formula (spell_effects.csv <c>amountExpr</c>), so an archetype verb (arch_damage/arch_heal/…) can
    /// stay pure logic — <c>ctx.amount</c> / <c>ctx.mana</c> carry the numbers, no formula duplicated into Lua.
    /// <paramref name="fx"/> is the spell's export row so buff/debuff verbs can read buffStat/buffAmt/duration/chance.</summary>
    internal SpellContext(Session s, SpellDef sp, uint? targetId, string? answer, double amount, double mana, SpellFx? fx = null)
        : this(s, sp, targetId, answer)
    {
        this.amount = amount; this.mana = mana; _fx = fx;
    }

    /// <summary>Engine-evaluated effect amount for the archetype path (the spell's real formula result).</summary>
    public double amount { get; }
    /// <summary>Engine-resolved mana cost for the archetype path.</summary>
    public double mana   { get; }

    // ---- archetype-row fields (Lua: ctx.buffStat, ctx.durationMs, …) for the Buff/TargetBuff/Debuff verbs ----
    /// <summary>The spell's display name (for "&lt;name&gt; finds no target." style notices).</summary>
    public string spellName  => _sp.Name;
    /// <summary>Buff stat key(s), '|'-separated for multi-stat buffs (e.g. "might" or "might|hit"). "" if none.</summary>
    public string buffStat   => _fx?.BuffStat ?? "";
    /// <summary>Buff amount(s), '|'-separated, parallel to <see cref="buffStat"/> (deduction uses a fractional
    /// multiplier here, e.g. "0.5"). Split/parse in Lua so the per-stat loop is scriptable.</summary>
    public string buffAmt    => _fx?.BuffAmt ?? "";
    /// <summary>Buff/debuff duration in ms from the export (0 if unset — the verb supplies its archetype default).</summary>
    public int    durationMs => _fx?.DurationMs ?? 0;
    /// <summary>Debuff take-hold chance as a percent, evaluated against the resolved target's stats (100 if the
    /// export has no chance formula). Read-only; re-resolves the target each access (deterministic within a cast).</summary>
    public double chance     => _s.LuaDebuffChance(_sp, _targetId);

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

    // ---- primitives for the Buff / TargetBuff / Debuff / Cure archetype verbs -------------------------------
    /// <summary>Does the caster have at least <paramref name="amt"/> mana? Sends "You do not have enough mana."
    /// and returns false if not (a check only — <see cref="debitMana"/> does the actual debit later, matching the
    /// C# handlers which check up front but debit only once the cast is committed).</summary>
    public bool enoughMana(double amt)  => _s.LuaEnoughMana((int)amt);
    /// <summary>Debit the caster's mana with no re-check and no message (guarded by an earlier <see cref="enoughMana"/>).</summary>
    public void debitMana(double amt)   => _s.LuaDebitMana((int)amt);
    /// <summary>Remove any active buff from THIS spell before re-applying (refresh, don't stack).</summary>
    public void clearBuff()             => _s.LuaClearBuff(_sp);
    /// <summary>Add one timed stat buff to the caster (no fx/refresh — call <see cref="clearBuff"/> once first,
    /// then <see cref="fxSelf"/> once after the loop). <paramref name="stat"/> is a Totals() key (might/hit/dam/…).</summary>
    public void addBuff(string stat, double amount, double durMs) =>
        _s.LuaAddBuff(stat, (int)System.Math.Round(amount), (int)durMs, _sp);
    /// <summary>Play this spell's cast anim/sound on the caster.</summary>
    public void fxSelf()                => _s.LuaFxSelf(_sp);
    /// <summary>Show this spell's live TARGET-flavor line to the caster (self-cast: flavor before "You cast X").</summary>
    public void flavorSelf()            => _s.LuaFlavorSelf(_sp);

    /// <summary>What the TargetBuff cast is aimed at, resolved from the client target id or the faced tile:
    /// "player" (incl. a self-cast), "mob" (a mob/NPC/pet), or "none". The verb branches on this.</summary>
    public string targetKind            => _s.LuaTargetBuffKind(_targetId);
    /// <summary>Apply a timed stat buff to the resolved TargetBuff target (player or mob), with fx + the target's
    /// flavor line. Call after branching on <see cref="targetKind"/>.</summary>
    public void buffTarget(string stat, double amount, double durMs) =>
        _s.LuaBuffTarget(stat, (int)System.Math.Round(amount), (int)durMs, _sp, _targetId);
    /// <summary>Apply a damage-reduction (deduction) to the resolved TargetBuff target — PLAYER only. <paramref
    /// name="mult"/> is the incoming-damage multiplier (0.5 = take half). Plays fx + the target's flavor line.</summary>
    public void deductionTarget(double mult, double durMs) =>
        _s.LuaDeductionTarget(mult, (int)durMs, _sp, _targetId);

    /// <summary>Roll the RTK magic-deflect check against the resolved mob (only if the spell can fail); true if
    /// the cast was deflected (no mana is spent — the verb should return true without applying).</summary>
    public bool deflected()             => _s.LuaDeflected(_sp, _targetId);
    /// <summary>Uniform percent roll: true with probability <paramref name="pct"/>% (RTK Random.Next(100) &lt; pct).</summary>
    public bool roll(double pct)        => _s.LuaRoll(pct);
    /// <summary>Freeze the resolved mob target for <paramref name="durMs"/> ms (RTK debuff hold) + debuff fx.</summary>
    public void freezeTarget(double durMs) => _s.LuaFreezeTarget((int)durMs, _sp, _targetId);

    // ---- curse / categorized-status primitives (the `curse` verb + arch_cure) ------------------------------
    /// <summary>The category this Cure removes (SpellFx.CureCat: "curses"/"venoms"/"minorcurses"). "" if none.</summary>
    public string cureCat               => _fx?.CureCat ?? "";
    /// <summary>Is the resolved curse target a legal one (a PC only in a PvP map — incl. yourself — or a mob)?
    /// Sends the RTK notice and returns false otherwise ("finds no target." / "You cannot attack that target.").</summary>
    public bool canCurse()              => _s.LuaCanCurseTarget(_sp, _targetId);
    /// <summary>Does the resolved curse target already carry a status of <paramref name="category"/>? (The
    /// checkIfCast guard — a same-category curse is then blocked, which is what makes self-pestilence a defense.)</summary>
    public bool hasStatus(string category) => _s.LuaCurseHasCategory(category, _targetId);
    /// <summary>Apply a categorized status to the resolved curse target: <paramref name="category"/> occupies the
    /// exclusivity slot; <paramref name="stat"/>/<paramref name="amount"/> is the effect (e.g. armor -5 → take more
    /// damage). Plays fx + the target's flavor line.</summary>
    public void applyCurse(string category, string stat, double amount, double durMs) =>
        _s.LuaApplyCurse(category, stat, (int)System.Math.Round(amount), (int)durMs, _sp, _targetId);
    /// <summary>Remove every active status of <paramref name="category"/> from the caster (RTK cure-by-category).</summary>
    public void cureCategory(string category) => _s.LuaCureCategory(category);
}
