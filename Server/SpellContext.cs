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
    /// <summary>The spell's stable key (for keying its own cooldown/duration, e.g. a ward's aether).</summary>
    public string spellKey   => _sp.Key;
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
    /// <summary>Heal whoever this cast is AIMED at — another player, a mob (your pet), or yourself — with the
    /// spell's fx drawn over them. Type-5 self-skills always heal the caster; only Type-2 "Which target? &gt;"
    /// spells redirect. Falls back to the caster when nothing is targeted.</summary>
    public void healTarget(double amt)  => _s.LuaHealTarget((int)System.Math.Round(amt), _sp, _targetId);
    /// <summary>Current HP of the targeted mob (0 if it isn't a living mob) — Drain's "is it weak enough" test
    /// and the amount of life it yields.</summary>
    public double targetHp              => _s.LuaTargetMobHp(_targetId);
    /// <summary>Play this spell's own anim/sound over the TARGET instead of the caster.</summary>
    public void fxTarget()              => _s.LuaFxTarget(_sp, _targetId);
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
    public void rage(double amount, double durMs)     => _s.LuaSetRage((int)amount, (int)durMs, _sp.Name);
    /// <summary>Arm Baekho's Cunning damage-reduction (its own slot, independent of the Sanctuary line — Sanctuary
    /// overrides it while up, then it re-asserts). <paramref name="mult"/> is the incoming-damage multiplier.</summary>
    public void deduction(double mult, double durMs)  => _s.ApplyCunningDeduction(mult, (int)durMs);
    /// <summary>Arm (on=true) or clear a positional stance ("backstab"/"flank") for <paramref name="durMs"/> ms.</summary>
    public void stance(string name, bool on, double durMs) => _s.LuaStance(name, on, (int)durMs);
    /// <summary>Play a cast animation + sound on the caster (Effect.tbl anim id, sound id).</summary>
    public void fx(double anim, double sound)         => _s.LuaFx((int)anim, (int)sound);

    // ---- Tier-2 combat-stance primitives (rage / enchant / stealth verbs) ----
    /// <summary>Is a fury/rage tier already active? (RTK blocks casting another while one runs.)</summary>
    public bool rageActive                            => _s.LuaRageActive;
    /// <summary>Is an enchant tier already active? (RTK blocks re-casting while one runs.)</summary>
    public bool enchantActive                         => _s.LuaEnchantActive;
    /// <summary>Arm the Rogue stealth burst (next swing ×9, then strips) for <paramref name="durMs"/> ms.</summary>
    public void armStealth(double durMs)              => _s.LuaSetStealth((int)durMs);
    /// <summary>Arm an enchant tier (multiplies only the raw weapon-swing term) for <paramref name="durMs"/> ms.</summary>
    public void armEnchant(double amount, double durMs) => _s.LuaSetEnchant(amount, (int)durMs);

    /// <summary>Apply a mob-only venom DoT (MaxHp×1% per 1.5s, per-tick clamped to <paramref name="tickCap"/>,
    /// for 1 + random(<paramref name="lowMs"/>, <paramref name="highMs"/>) ms). False if no mob target or it's
    /// already venomed (a notice was sent) — the verb then spends no mana. <paramref name="flatTick"/> &gt; 0
    /// substitutes a FIXED per-tick amount for the percentage (Burn's hardcoded 1000).</summary>
    public bool applyVenom(double tickCap, double lowMs, double highMs, double flatTick = 0) =>
        _s.LuaApplyVenom((int)tickCap, (int)lowMs, (int)highMs, _sp, _targetId, (int)flatTick);

    /// <summary>Blind the faced/targeted creature for <paramref name="durMs"/> ms: a mob drops its target and
    /// stops acquiring new ones (it just wanders); a PC only takes the 'blinds' exclusivity slot — 4.95 has no
    /// client-side blind effect to drive. False (with the RTK notice) if there's no target or one is already blind.</summary>
    public bool blindTarget(double durMs) => _s.LuaBlindTarget((int)durMs, _sp, _targetId);

    /// <summary>Mind-control the faced/targeted mob for <paramref name="durMs"/> ms (RTK endear): it becomes
    /// yours via the same ownership the pet system uses, then reverts to a normal world mob when the timer
    /// lapses. False (with the RTK notice) for a boss, an already-owned mob, or no target.</summary>
    public bool charmTarget(double durMs) => _s.LuaCharmTarget((int)durMs, _sp, _targetId);

    /// <summary>Shout as the caster — an over-head chat bubble everyone on the map sees (RTK player:talk).</summary>
    public void talk(string msg) => _s.LuaTalk(msg);

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
    /// <summary>Uniform integer in [<paramref name="lo"/>, <paramref name="hi"/>] inclusive, off the same
    /// stream as <see cref="roll"/>. For weighted picks (e.g. Endear's variable control duration).</summary>
    public double rollRange(double lo, double hi) => _s.LuaRollRange((int)lo, (int)hi);
    /// <summary>Freeze the resolved mob target for <paramref name="durMs"/> ms (RTK debuff hold) + debuff fx.</summary>
    public void freezeTarget(double durMs) => _s.LuaFreezeTarget((int)durMs, _sp, _targetId);

    // ---- curse / categorized-status primitives (the `curse` verb + arch_cure) ------------------------------
    /// <summary>The category this Cure removes (SpellFx.CureCat: "curses"/"venoms"/"minorcurses"). "" if none.</summary>
    public string cureCat               => _fx?.CureCat ?? "";
    /// <summary>Is the resolved curse target a legal one (a PC only in a PvP map — incl. yourself — or a mob)?
    /// Sends the RTK notice and returns false otherwise ("finds no target." / "You can't attack that target.").</summary>
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

    // ---- ward primitives (the `ward` verb: bolster / harden / protections — the beneficial categorized status) ----
    /// <summary>Resolve + validate the ward target: "self" (protections) or "ally" (self/ally PC, or a mob for
    /// harden-on-a-pet). No PvP gate. False (with "It doesn't work.") if nothing is found.</summary>
    public bool wardTarget(string mode)      => _s.LuaWardTarget(mode, _targetId);
    /// <summary>Does the resolved ward target already carry a status of <paramref name="category"/>? (PC only.)</summary>
    public bool wardHasStatus(string category) => _s.LuaWardHasStatus(category);
    /// <summary>Apply the ward to the resolved target: a PC gets the categorized status (shares curse storage,
    /// folds into AC); a mob gets just the stat buff. <paramref name="amount"/> may be 0 (a protection slot).</summary>
    public void applyWard(string category, string stat, double amount, double durMs) =>
        _s.LuaApplyWard(category, stat, (int)System.Math.Round(amount), (int)durMs, _sp);

    // ---- Tier-3 utility/target primitives (mana_steal/mana_gift/cleanse/revive/leap/mana_battery) ----
    /// <summary>The caster's effective max mana.</summary>
    public double maxMp           => _s.LuaMaxMp;
    /// <summary>Set the caster's HP (clamped to [0, maxHp]). For a raw set (e.g. mana_battery's HP cost).</summary>
    public void setHp(double n)   => _s.LuaSetHp((int)System.Math.Round(n));
    /// <summary>Set the caster's mana (clamped to [0, maxMp]).</summary>
    public void setMana(double n) => _s.LuaSetMana((int)System.Math.Round(n));

    /// <summary>Revive the CASTER where they stand; true if they were actually dead. Use this and not
    /// <see cref="setHp"/> to bring someone back — ghost form is a redraw, not just a number.</summary>
    public bool reviveSelf()      => _s.LuaReviveSelf();

    /// <summary>Keyword-classifier verdict for a spell with no spell_effects row: "heal"/"damage"/"buff"/"other".</summary>
    public string effectKind      => _s.LuaEffectKind(_sp);
    /// <summary>Play a raw anim/sound over the resolved target mob (the generic fallback's zap).</summary>
    public void fxRawTarget(double anim, double sound) => _s.LuaFxRawTarget((int)anim, (int)sound, _targetId);

    /// <summary>The recorded Chung Ryong rage tier (0 = none). Check <see cref="rageActive"/> too: the tier
    /// deliberately outlives the fury so the wear-out drain knows what to charge.</summary>
    public double crRageTier      => _s.LuaCrRageTier;
    /// <summary>Record a Chung Ryong rage tier and arm its multiplier, duration and (keyed) AC buff.</summary>
    public void setCrRage(double tier, double mult, double ac, double durMs)
        => _s.LuaSetCrRage((int)tier, (int)mult, (int)ac, (int)durMs, spellName);

    /// <summary>Classify a 0-based pack slot for a repair spell: "empty" · "notgear" · "perfect" · "ok".</summary>
    public string packSlotState(double slot) => _s.LuaPackSlotState((int)slot);
    /// <summary>Display name of whatever is in a 0-based pack slot ("" if empty).</summary>
    public string packSlotName(double slot)  => _s.LuaPackSlotName((int)slot);
    /// <summary>Restore a 0-based pack slot to full durability (no-op unless packSlotState is "ok").</summary>
    public void repairPackSlot(double slot)  => _s.LuaRepairPackSlot((int)slot);

    /// <summary>Resolve the targeted PLAYER (explicit id incl. self, else the faced peer) for this cast; all the
    /// target.* members below then act on it. False (with "&lt;name&gt; finds no target.") if none.</summary>
    public bool   pcTarget()      => _s.LuaResolvePcTarget(_sp, _targetId);
    /// <summary>Resolved target's current mana.</summary>
    public double targetMana      => _s.LuaTargetMana;
    /// <summary>Resolved target's effective max mana.</summary>
    public double targetMaxMana   => _s.LuaTargetMaxMana;
    /// <summary>Is the resolved target a ghost/dead?</summary>
    public bool   targetIsDead    => _s.LuaTargetIsDead;
    /// <summary>Is the resolved target the caster themselves?</summary>
    public bool   targetIsSelf    => _s.LuaTargetIsSelf;
    /// <summary>Is the resolved target in the caster's own party?</summary>
    public bool   targetInGroup   => _s.LuaTargetInGroup;
    /// <summary>Resolved target's effective AC (lower = better; for the cleanse success formula).</summary>
    public double targetArmor     => _s.LuaTargetArmor;
    /// <summary>Resolved target's effective Will.</summary>
    public double targetWill      => _s.LuaTargetWill;
    /// <summary>Set the resolved target's mana (clamped to their [0, maxMp]).</summary>
    public void   setTargetMana(double n) => _s.LuaSetTargetMana((int)System.Math.Round(n));
    /// <summary>Send the resolved target this spell's "&lt;caster&gt; casts &lt;name&gt; on you." (or flavor) line.</summary>
    public void   tellTarget()    => _s.LuaTellTarget(_sp);
    /// <summary>Strip every timed effect (buffs + debuffs) from the resolved target (RTK flushDuration).</summary>
    public void   flushTarget()   => _s.LuaFlushTarget();
    /// <summary>Revive the resolved (dead) target in place at full health.</summary>
    public void   reviveTarget()  => _s.LuaReviveTarget(_sp);
    /// <summary>Leap up to <paramref name="maxDist"/> tiles in the faced direction (collision-stopped); returns
    /// the number of tiles actually moved (0 = blocked, nothing happened).</summary>
    public double leap(double maxDist) => _s.LuaLeap((int)maxDist);

    // ---- Tier-4 world-effecting primitives (gateway/return/divine/spot_traps/filch/trap/bladestorm/pet/morph/propose) ----
    /// <summary>Is the caster a ghost/dead? (Gateway/Return can't be cast while dead.)</summary>
    public bool   isDead        => _s.IsDead;
    /// <summary>Does the caster's current map allow warping out? (RTK warpOut flag — false on arenas/instances.)</summary>
    public bool   canWarpOut    => _s.LuaWarpOut;
    /// <summary>The spell's real per-spell mana cost from its export row (or 5 if the export had none).</summary>
    public double spellMana     => _s.LuaSpellMana(_sp);
    /// <summary>The spell's cooldown (RTK aether) in ms from its export row (0 if none).</summary>
    public double spellAether    => _s.LuaSpellAether(_sp);
    /// <summary>The resolved target player's level (after <see cref="pcTarget"/>). For Divination's level gate.</summary>
    public double targetLevel   => _s.LuaTargetLevel;
    /// <summary>Is this the spy (inventory-listing, equal-level-allowed) Divination variant, not judge?</summary>
    public bool   spyMode       => _s.LuaIsSpy(_sp);
    /// <summary>Live pet count this caster owns on the current map, and the level-scaled cap.</summary>
    public double petCount      => _s.LuaPetCount;
    public double petCap        => _s.LuaPetCap;
    /// <summary>This pet spell's mana cost / cooldown ms (0 if none) — data-bound per pet spell.</summary>
    public double petMana       => _s.LuaPetMana(_sp);
    public double petCooldown   => _s.LuaPetCooldownMs(_sp);
    /// <summary>Is a morph already active at the look this cast would apply? (Re-cast of the same form is a no-op.)</summary>
    public bool   morphActive() => _s.LuaMorphActive();
    /// <summary>The staged morph's resolved mana cost.</summary>
    public double morphMana     => _s.LuaMorphMana;

    /// <summary>Gateway: warp to the answered gate of the caster's kingdom. Self-narrates arrival; false (with a
    /// notice) if the region has no gates or the answer isn't a direction.</summary>
    public bool gateway()       => _s.LuaGateway(answer);
    /// <summary>Return: warp home to a random tavern in the caster's nation.</summary>
    public void returnHome()    => _s.LuaReturnHome();
    /// <summary>Divination: build + send the inspect popup for the resolved target to the caster (spy variant
    /// appends inventory). Self-narrates.</summary>
    public void divine(bool showInventory) => _s.LuaDivine(_sp, showInventory);
    /// <summary>Reveal every hidden trap within 15 tiles as a caster-only marker; returns how many were found.</summary>
    public double revealTraps() => _s.LuaRevealTraps();
    /// <summary>Grab the item on the faced tile (coins -> purse, else -> pack; put back if the pack is full).</summary>
    public void filch()         => _s.LuaFilch();
    /// <summary>Place a hidden trap on the caster's own tile — resolves kind/level/mana from the spell (or the
    /// typed answer for the set_trap dispatcher) and owns its own mana debit. False (with a notice) on a bad
    /// answer or too-low level / not enough mana.</summary>
    public bool placeTrap()     => _s.LuaPlaceTrap(_sp, answer);
    /// <summary>Place a bladestorm decoy on the caster's tile, auto-expiring in <paramref name="lifetimeMs"/> ms.</summary>
    public void placeBladestorm(double lifetimeMs) => _s.LuaPlaceBladestorm((int)lifetimeMs);
    /// <summary>Summon this spell's pet as a real owned world mob (one tile ahead, else on the caster's tile).
    /// False if the pet/mob couldn't be resolved.</summary>
    public bool summonPet()     => _s.LuaSummonPet(_sp);
    /// <summary>Apply the staged morph plan (look/duration) to the caster + rebroadcast to self and peers.</summary>
    public void applyMorph()    => _s.LuaApplyMorph(_sp);
    /// <summary>Kick off the async marriage-proposal dialog (RTK RunProposeAsync). Returns immediately.</summary>
    public void propose()       => _s.LuaPropose(_sp);
    /// <summary>Does the caster carry a legend mark (e.g. "engaged"/"married")?</summary>
    public bool hasLegend(string mark) => _s.LuaHasLegend(mark);
    /// <summary>Forget this spell from the caster's spellbook (RTK cleanup of a spell you shouldn't still have).</summary>
    public void forgetSpell()   => _s.LuaForgetSpell(_sp);
    /// <summary>Mark this cast as self-narrated so the central "You cast X." line is suppressed.</summary>
    public void narrated()      => _s.LuaMarkNarrated();

    // ---- combat-stray primitives (sacrifice strikes + ambush) ----------------------------------------------
    /// <summary>Which self-sacrifice strike family this spell is ("LethalStrike"/"DesperateAttack"/"Berserk"/
    /// "Whirlwind"), driving its per-family damage/mana/cooldown/HP-cost formulas.</summary>
    public string sacrificeFamily => _s.LuaSacrificeFamily(_sp);
    /// <summary>The caster's alignment stat (Whirlwind's damage factor + HP cost + cooldown branch on it).</summary>
    public double alignment       => _s.LuaAlignment;
    /// <summary>Is Baekho's Rage specifically active (rage tier 5, not a lesser Fury)? Adds x1.5 to Berserk/Whirlwind.</summary>
    public bool   baekhoRage      => _s.LuaBaekhoRage;
    /// <summary>Resolve + stash the mob on the faced tile for a sacrifice strike (alive). False if none — the
    /// strike lands nothing (no HP cost), though the mana/cooldown were already spent.</summary>
    public bool   sacFrontMob()   => _s.LuaSacFrontMob();
    /// <summary>Apply <paramref name="damage"/> (armor-netted) to the stashed sacrifice target; plays the family
    /// fx, awards kill XP. Returns the overkill (net damage minus the mob's pre-hit HP; may be negative).</summary>
    public double sacApply(double damage) => _s.LuaSacApply(_sp, (int)System.Math.Round(damage));
    /// <summary>Rogue overkill refund: up to half the overkill returns as HP and MP, each capped at half the
    /// caster's pre-cast HP/MP.</summary>
    public void   backflow(double overkill, double preHp, double preMp) => _s.LuaBackflow((int)overkill, (int)preHp, (int)preMp);
    /// <summary>Warrior overkill splash: the overkill cleaves onto adjacent-tile mobs, recursively re-splashing.</summary>
    public void   overflow(double overkill) => _s.LuaOverflow(_sp, (int)overkill);
    /// <summary>Resolve + stash the mob on the faced tile for an ambush. False if none ("finds no target").</summary>
    public bool   ambushMob()     => _s.LuaAmbushMob();
    /// <summary>Teleport to the far side of the stashed mob (its back, else a flank), facing it. False if the
    /// back and both flanks are all occupied ("finds no opening").</summary>
    public bool   ambushLeap()    => _s.LuaAmbushLeap();
    /// <summary>Swing on the stashed ambush target (gets the free positional backstab if landed on its blind side).</summary>
    public void   ambushStrike()  => _s.LuaAmbushStrike(_sp);
}
