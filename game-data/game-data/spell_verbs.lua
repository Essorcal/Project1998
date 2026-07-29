-- Spell verbs: the LOGIC half of the data-driven spell system (the "verb"; SpellParams.csv is the "row").
-- Each verb is a function(ctx, row):
--   ctx = the SpellContext facade -> engine primitives (ctx:spendMana / ctx:damage / ctx:heal / ...) plus
--         read-only caster stats (ctx.will, ctx.level, ctx.might, ctx.hp, ctx.hasTarget, ...).
--   row = this spell's SpellParams.csv row, numbers already parsed (row.coeff, row.mana, row.amount, ...);
--         empty CSV cells are nil, so `row.base or 0` gives a safe default.
-- Bind a verb to a spell by adding a SpellParams.csv row whose `verb` column names it.
-- Edit THIS file or the CSV and run !reload to see changes live -- no server restart.

verbs = {}

-- Direct magic damage to the current target: base + Will*coeff, costing `mana`.
-- (Target resolution, deflect, the HP-bar packet, death + XP are all handled inside ctx:damage.)
function verbs.magic_damage(ctx, row)
  if not ctx:spendMana(row.mana or 0) then return end
  ctx:damage((row.base or 0) + ctx.will * (row.coeff or 1.0))
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
