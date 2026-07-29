-- Item use-effect verbs: the LOGIC half of the data-driven consumable system (the "verb"; ItemParams.csv is
-- the "row"). Each verb is a function(ctx, row):
--   ctx = the ItemContext facade -> engine primitives (ctx:heal / ctx:restoreMana / ctx:setStatus / ...) plus
--         read-only eater stats (ctx.maxHp, ctx.armor, ...).
--   row = this item's ItemParams.csv row, numbers already parsed (row.amount, row.duration, ...); empty CSV
--         cells are nil, so `row.amount or 0` gives a safe default.
-- Bind a verb to an item by adding an ItemParams.csv row whose `verb` column names it.
-- A verb may `return false` to REFUSE the use (a gate, e.g. a ward already active): the item is NOT consumed.
-- Returning nothing (or true) consumes the item as normal.
-- Edit THIS file or the CSV and run !reload to see changes live -- no server restart.

verbs = {}

-- Eat food or drink a heal potion: restore HP by a flat `amount`, or to full when `full` is set. Plays the
-- shared eat animation first (RTK's own scripts sendAction before applying the heal).
function verbs.heal(ctx, row)
  ctx:animate()
  if row.full then ctx:heal(ctx.maxHp) else ctx:heal(row.amount or 0) end
end

-- poison_apple: RTK removeHealthExtend(999999999) -- an always-lethal joke item.
function verbs.fatal(ctx, row)
  ctx:animate()
  ctx:kill()
end

-- Drinks + smoke: restore mana by `amount` at a small HP cost (`hpcost`) -- RTK's mana-for-HP trade.
function verbs.drink(ctx, row)
  ctx:animate()
  ctx:restoreMana(row.amount or 0)
  if row.hpcost then ctx:loseHp(row.hpcost) end
end

-- Timed ward potion/scroll (sanctuary / harden_armor / curse_protection / ...): set a status flag for a
-- duration. If the ward is already up AND the item has a guard message (`activemsg`), refuse without
-- consuming (RTK's checkIfCast). A row with no activemsg (e.g. black_potion) has no guard and always re-applies.
function verbs.ward(ctx, row)
  if row.activemsg and ctx:hasStatus(row.statuskey) then
    ctx:say(row.activemsg)
    return false
  end
  ctx:setStatus(row.statuskey, row.duration or 0)
  return true
end

-- scroll_of_immortality: an armor-scaled success roll before granting the harden-body ward (RTK's own
-- ceil((120 + clampedArmor) / 2)% chance). Refuses (no consume) while already active or on a failed roll.
function verbs.hardenbody(ctx, row)
  if ctx:hasStatus(row.statuskey) then ctx:say(row.activemsg); return false end
  local rate = math.ceil((120 + ctx.armor) / 2)
  if not ctx:chance(rate) then ctx:say("Something went wrong."); return false end
  ctx:setStatus(row.statuskey, row.duration or 0)
  ctx:castPose()
  ctx:say("You cast Harden Body.")
  return true
end

-- indigo_potion / clear_water_song: no player poison/curse model exists yet to clear -- a faithful no-op consume.
function verbs.cure(ctx, row)
  return true
end

-- yellow_scroll / qui_hyang: warp to a random tavern in your nation (RTK returnFunc -> returnToInn).
function verbs.warphome(ctx, row)
  ctx:warpHome()
  return true
end
