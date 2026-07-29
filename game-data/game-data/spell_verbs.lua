-- Spell verbs: the LOGIC half of the data-driven spell system (the "verb"; SpellParams.csv is the "row").
-- Each verb is a function(ctx, row):
--   ctx = the SpellContext facade -> engine primitives (ctx:spendMana / ctx:damage / ctx:heal / ...) plus
--         read-only caster stats (ctx.will, ctx.level, ctx.might, ctx.hp, ctx.hasTarget, ...).
--   row = this spell's SpellParams.csv row, numbers already parsed (row.coeff, row.mana, row.amount, ...);
--         empty CSV cells are nil, so `row.base or 0` gives a safe default.
-- Bind a verb to a spell by adding a SpellParams.csv row whose `verb` column names it.
-- Edit THIS file or the CSV and run !reload to see changes live -- no server restart.

verbs = {}

-- =========================================================================================================
-- ARCHETYPE verbs (arch_*): the DEFAULT behaviour for a whole spell archetype, run for every spell of that
-- archetype (see Session.ApplyCast). The engine has already evaluated the spell's real formula and mana cost
-- from spell_effects.csv, so these read ctx.amount / ctx.mana rather than per-spell row params. A missing
-- arch_* verb makes that archetype fall back to its C# handler, so migration is one archetype at a time.
-- =========================================================================================================

-- Damage archetype: the standard direct magic attack. ctx:magicDamage does the whole faithful sequence
-- (mana check -> resolve target -> deflect roll, no mana on a deflect -> spend mana -> apply + XP). Returns
-- false if the cast couldn't happen (no mana / no target) so the cast animation is suppressed, matching C#.
function verbs.arch_damage(ctx, row)
  return ctx:magicDamage(ctx.amount, ctx.mana)
end

-- Heal archetype: restore the caster's own HP by the engine-evaluated amount (spell_effects.csv formula).
-- Mana is checked+spent first (return false on short mana so the cast animation is suppressed, matching C#);
-- ctx:heal caps at max HP and plays the sparkle/message.
function verbs.arch_heal(ctx, row)
  if not ctx:spendMana(ctx.mana) then return false end
  ctx:heal(ctx.amount)
  return true
end

-- =========================================================================================================
-- PER-SPELL verbs: bound to one spell by a SpellParams.csv row whose `verb` column names it (these take
-- precedence over the archetype path). Their numbers come from the row (row.coeff, row.base, ...).
-- =========================================================================================================

-- Direct magic damage to the current target: base + Will*coeff, costing `mana`.
-- (Target resolution, deflect, the HP-bar packet, death + XP are all handled inside ctx:damage.)
function verbs.magic_damage(ctx, row)
  if not ctx:spendMana(row.mana or 0) then return end
  ctx:damage((row.base or 0) + ctx.will * (row.coeff or 1.0))
end

-- =========================================================================================================
-- Baekho's Cunning (RTK Spells/Subpaths/Baekho/baekhos_cunning.lua) — the showcase for COMPOSED verbs: a
-- single spell that is simultaneously a rage MULTIPLIER, a damage-REDUCTION (deduction), and positional
-- STANCE grants, plus a stateful tier machine. It fits NO single archetype; it just composes primitives.
-- Recast to climb Cunning 1->6, each tier stronger and far pricier; each cast supersedes the lesser furies
-- (ctx:rage overwrites the single rage slot). The tier TABLE is an RTK constant so it lives here in the verb.
-- ded = incoming-damage multiplier (RTK target.deduction: tier1 -0.00 .. tier6 -0.40 off 1.0).
local CUNNING = {
  [1] = { mana = 3000,   rage = 6,  ded = 1.00, back = false, flank = false },
  [2] = { mana = 4200,   rage = 7,  ded = 0.92, back = true,  flank = false },
  [3] = { mana = 15634,  rage = 9,  ded = 0.84, back = false, flank = true  },
  [4] = { mana = 46658,  rage = 10, ded = 0.76, back = true,  flank = true  },
  [5] = { mana = 117667, rage = 12, ded = 0.68, back = true,  flank = true  },
  [6] = { mana = 265000, rage = 14, ded = 0.60, back = true,  flank = true  },
}
local CUNNING_DURATION = 938000   -- ~15.6 min active window (RTK setDuration)
local CUNNING_AETHER   = 150000   -- ~2.5 min cooldown between tier-ups (RTK setAether)

function verbs.baekhos_cunning(ctx, row)
  if ctx:onCooldown("baekhos_cunning") then
    ctx:say("Baekho's power is not yet ready to grow.")
    return false
  end
  -- Only continue the ladder if the stance is still active; if it lapsed, hasDuration is false -> start fresh.
  local active = ctx:hasDuration("baekhos_cunning")
  local tier = active and ctx:reg("baekhos_cunning") or 0
  if tier < 0 or tier > 6 then tier = 0 end
  if active and tier >= 6 then
    ctx:say("You have reached your max potential.")
    return false
  end

  local nt = tier + 1
  local c = CUNNING[nt]
  if not ctx:spendMana(c.mana) then return false end        -- huge, tier-scaled cost

  ctx:setReg("baekhos_cunning", nt)
  ctx:setDuration("baekhos_cunning", CUNNING_DURATION)
  ctx:rage(c.rage, CUNNING_DURATION)                         -- whole-swing xN (supersedes lesser furies)
  ctx:deduction(c.ded, CUNNING_DURATION)                     -- take less damage
  ctx:stance("backstab", c.back, CUNNING_DURATION)           -- free positional crits
  ctx:stance("flank",    c.flank, CUNNING_DURATION)
  ctx:setCooldown("baekhos_cunning", CUNNING_AETHER)
  ctx:fx(35, 705)                                            -- RTK sendAnimation(35) / playSound(705)
  ctx:say("[Cunning "..nt.."] Baekho sharpens your instincts (x"..c.rage.." damage).")
  return true
end

-- Restore the caster's own HP: flat `amount` plus optional Will scaling (`willcoeff`), costing `mana`.
function verbs.heal(ctx, row)
  if not ctx:spendMana(row.mana or 0) then return end
  ctx:heal((row.amount or 0) + ctx.will * (row.willcoeff or 0))
end

-- Restore the caster's own mana by a flat `amount` (no mana cost, obviously).
function verbs.restore_mana(ctx, row)
  ctx:restoreMana(row.amount or 0)
end

-- Timed self-buff: spend `mana`, then raise `stat` by `amount` for `duration` ms (default 60s). `stat` is one
-- of the Totals() keys: might/will/grace/hp/mp/armor/hit/dam. Re-casting the same spell refreshes, not stacks.
function verbs.buff(ctx, row)
  if not ctx:spendMana(row.mana or 0) then return end
  ctx:buff(row.stat, row.amount or 0, row.duration or 60000)
end
